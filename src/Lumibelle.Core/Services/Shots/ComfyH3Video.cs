using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using lumibelle.Services.Production;

namespace lumibelle.Services.Shots;

public interface IVideoGenerator
{
    Task<H3Configuration> CheckAsync(AiSettings settings, CancellationToken ct = default);
    Task PrepareAsync(VideoRun run, string directory, CancellationToken ct);
    Task ValidateInputsAsync(VideoSnapshot snapshot, CancellationToken ct);
    Task<string> SubmitAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct);
    IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct);
    Task<ShotTake> DownloadAsync(VideoRun run, VideoCandidate candidate, string directory, Func<string, Task> progress, CancellationToken ct);
    Task<bool> CancelAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct);
    Task<bool> ExistsAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct);
}
public sealed partial class ComfyH3Video(IHttpClientFactory clients, IComfyExecutionMonitor monitor, IAssetStore assets, IVoiceStore voices,
    IShotStore shots, IProductionMediaTools mediaTools, IReferenceVideoStore? referenceVideos = null) : IVideoGenerator
{
    private HttpClient Client(string url) { var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new Uri(AiProviderRegistry.NormalizeComfyUrl(url) + "/"); return http; }
    internal static readonly ComfyExecutionOptions MonitorOptions = new(new Dictionary<string, ComfyNodeStage>
    {
        ["1"] = new(GenerationPhase.Preparing, "Loading H3…"), ["2"] = new(GenerationPhase.Preparing, "Loading H3 encoder…"),
        ["5"] = new(GenerationPhase.Preparing, "Conditioning references…"), ["61"] = new(GenerationPhase.Preparing, "Anchoring the starting frame…"), ["10"] = new(GenerationPhase.Generating, "Generating video and audio…", "Sampling H3", "steps", "sampling"),
        ["50"] = new(GenerationPhase.Preparing, "Preparing PDD sampling…"), ["51"] = new(GenerationPhase.Preparing, "Preparing generation preset…"),
        ["11"] = new(GenerationPhase.Finalizing, "Decoding video…"), ["12"] = new(GenerationPhase.Finalizing, "Decoding audio…"),
        ["14"] = new(GenerationPhase.Finalizing, "Encoding MP4…"), ["15"] = new(GenerationPhase.Finalizing, "Saving lossless frames…"),
        ["21"] = new(GenerationPhase.Finalizing, "Saving refinement data…"), ["30"] = new(GenerationPhase.Preparing, "Loading the take…"),
        ["32"] = new(GenerationPhase.Preparing, "Upscaling the take…"),
        ["40"] = new(GenerationPhase.Finalizing, "Separating video and audio…"), ["41"] = new(GenerationPhase.Finalizing, "Upscaling video…"),
        ["42"] = new(GenerationPhase.Finalizing, "Combining video and audio…")
    }, "H3 workflow rejected. Refresh Video models.", "H3 execution failed. Check the ComfyUI log for details.", "Video submission timed out.", "Video generation timed out.", "Unreadable video response.", "ComfyUI connection failed.")
    {
        TimingNodes = new Dictionary<string, string>
        {
            ["1"] = "Preparation", ["2"] = "Preparation", ["3"] = "Preparation", ["4"] = "Preparation", ["5"] = "Preparation",
            ["6"] = "Preparation", ["60"] = "Preparation", ["61"] = "Preparation", ["7"] = "Preparation", ["8"] = "Preparation", ["9"] = "Preparation", ["16"] = "Preparation",
            ["50"] = "Preparation", ["51"] = "Preparation", ["18"] = "Preparation", ["30"] = "Preparation", ["31"] = "Preparation", ["10"] = "Sampling",
            ["11"] = "Decoding", ["12"] = "Decoding", ["13"] = "Decoding", ["14"] = "Decoding", ["17"] = "Archive", ["15"] = "Archive",
            ["40"] = "Upscaling", ["41"] = "Upscaling", ["42"] = "Upscaling"
        }
    };
    public async Task<H3Configuration> CheckAsync(AiSettings settings, CancellationToken ct = default)
    {
        H3Policy.ValidateSettings(settings.H3);
        H3Configuration Failure(string message) =>
            new(false, false, message, [], [], [], []) { RefinementIssue = message, PreviewUpscaling = new(null, message) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings.ComfyUrl); using var response = await http.GetAsync("object_info", timeout.Token); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return Inspect(json.RootElement, settings.H3);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure("Could not check H3: ComfyUI did not respond within 15 seconds. Make sure it is running, then refresh Video models.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            var access = AccessFailure(e);
            if (access is null)
            {
                var reason = e.Message.Trim();
                if (reason.Length == 0) reason = "the request failed";
                if (!reason.EndsWith('.')) reason += ".";
                return Failure("Could not check H3 against " + Client(settings.ComfyUrl).BaseAddress + ": " + reason +
                    " Check this ComfyUI connection, then retry.");
            }
            if (access.HttpRejected)
                return Failure("ComfyUI denied the H3 model check. Restore access and refresh Video models.");
            if (access.RequestWasNotSent)
                return Failure("ComfyUI model discovery could not start: " + access.Message + " Fix this in Connections, then refresh Video models.");
            return Failure(access.Message);
        }
    }

    private static ComfyAccessException? AccessFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ComfyAccessException access) return access;
        return null;
    }
    internal static string[] Options(JsonElement root, string node, string field)
    {
        var input = Input(root, node, field);
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0) return [];
        var values = input[0].ValueKind == JsonValueKind.Array ? input[0] : input.GetArrayLength() > 1 && input[1].ValueKind == JsonValueKind.Object && input[1].TryGetProperty("options", out var opts) ? opts : default;
        return values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
    }
    internal static JsonElement Input(JsonElement root, string node, string field)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(node, out var n) && n.ValueKind == JsonValueKind.Object && n.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object)
            foreach (var group in new[] { "required", "optional" }) if (i.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty(field, out var f)) return f;
        return default;
    }
    internal static JsonElement DynamicOption(JsonElement input, string key)
    {
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() < 2 || input[1].ValueKind != JsonValueKind.Object ||
            !input[1].TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return default;
        return options.EnumerateArray().FirstOrDefault(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key);
    }
    public static H3Configuration Inspect(JsonElement root, H3Settings s)
    {
        var installedModels = Options(root, "UNETLoader", "unet_name");
        var installedEncoders = Options(root, "CLIPLoader", "clip_name");
        var installedVaes = Options(root, "VAELoader", "vae_name");
        var installedLoras = Options(root, "LoraLoaderModelOnly", "lora_name");
        var models = installedModels.Where(IsRef2VaModel).ToArray();
        var encoders = installedEncoders.Where(x => !x.Contains("generation_tail", StringComparison.OrdinalIgnoreCase) &&
            new[] { "qwen3vl_32b_minimax_h3_", "qwen3vl_32b_minimax_h3-", "qwen3vl_32b_h3_", "qwen3vl_32b_h3-" }
                .Any(prefix => Path.GetFileName(x).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray();
        var vaes = installedVaes.Where(x => x.Contains("minimax_h3_", StringComparison.OrdinalIgnoreCase)).ToArray();
        var loras = installedLoras.Where(x => new[] { "minimax_h3_ref2v_turbo_4step", "minimax_h3_ref2v_turbo_8step" }
            .Any(prefix => Path.GetFileName(x.Replace('\\', '/')).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray();
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type"], ["VAELoader"] = ["vae_name"],
            ["MiniMaxH3ReferenceToVideo"] = ["clip", "vae", "audio_vae", "prompt", "width", "height", "length", "ref_image_size", "ref_images", "ref_audios"],
            ["BasicGuider"] = ["model", "conditioning"], ["RandomNoise"] = ["noise_seed"], ["KSamplerSelect"] = ["sampler_name"],
            ["BasicScheduler"] = ["model", "scheduler", "steps", "denoise"], ["SamplerCustomAdvanced"] = ["noise", "guider", "sampler", "sigmas", "latent_image"],
            ["VAEDecode"] = ["samples", "vae"], ["VAEDecodeAudio"] = ["samples", "vae"], ["CreateVideo"] = ["images", "fps", "audio"],
            ["SaveVideo"] = ["video", "filename_prefix", "format"], ["SaveAnimatedWEBP"] = ["images", "filename_prefix", "fps", "lossless", "quality", "method"],
            ["RebatchImages"] = ["images", "batch_size"], ["LoadImage"] = ["image"], ["LoadAudio"] = ["audio"]
        };
        var archiveMissing = new List<string>();
        foreach (var node in new[] { "SaveAnimatedWEBP", "RebatchImages" })
        {
            archiveMissing.AddRange(required[node].Where(f => Input(root, node, f).ValueKind != JsonValueKind.Array).Select(f => node + "." + f));
            required.Remove(node);
        }
        var missing = required.SelectMany(pair => pair.Value.Where(f => Input(root, pair.Key, f).ValueKind != JsonValueKind.Array).Select(f => pair.Key + "." + f)).ToList();
        if (!Options(root, "SaveAnimatedWEBP", "method").Contains("default")) archiveMissing.Add("lossless WebP compression method");
        if (!root.TryGetProperty("RebatchImages", out var rebatch) || !rebatch.TryGetProperty("output_is_list", out var outputList) || outputList.ValueKind != JsonValueKind.Array || outputList.GetArrayLength() != 1 || outputList[0].ValueKind != JsonValueKind.True)
            archiveMissing.Add("RebatchImages list output");
        if (!Options(root, "CLIPLoader", "type").Contains("minimax")) missing.Add("CLIPLoader minimax type");
        if (!Options(root, "BasicScheduler", "scheduler").Contains("simple")) missing.Add("simple scheduler");
        if (!Options(root, "MiniMaxH3ReferenceToVideo", "ref_image_size").Contains("match")) missing.Add("reference image sizing");
        var format = Input(root, "SaveVideo", "format");
        var mp4 = DynamicOption(format, "mp4");
        var codec = mp4.ValueKind == JsonValueKind.Object && mp4.TryGetProperty("inputs", out var nested) && nested.TryGetProperty("required", out var requiredCodec) && requiredCodec.TryGetProperty("codec", out var codecInput) ? codecInput : default;
        if (DynamicOption(codec, "h264").ValueKind != JsonValueKind.Object) missing.Add("MP4/H.264 output contract");
        foreach (var field in new[] { "ref_images", "ref_audios" })
        {
            var input = Input(root, "MiniMaxH3ReferenceToVideo", field);
            if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() < 2 || !input[1].TryGetProperty("template", out var template) || !template.TryGetProperty("max", out var max) || max.GetInt32() < (field == "ref_images" ? 9 : 3)) missing.Add(field + " capacity");
        }
        // Filenames suggest choices, but community exports may use different names. Submission
        // checks exact catalog identities and node contracts without claiming to inspect weights.
        var incompatible = IsFl2VaOnly(s.Model) ? "Choose Ref2VA diffusion weights; FL2VA weights use a different workflow."
            : Path.GetFileName(s.Encoder).Contains("generation_tail", StringComparison.OrdinalIgnoreCase) ? "Choose an H3 conditioning encoder. A generation tail is not a standalone encoder." : null;
        var baseIssue = missing.Count > 0 ? "Update ComfyUI: missing " + string.Join(", ", missing) : incompatible ??
            (!installedModels.Contains(s.Model) || !installedEncoders.Contains(s.Encoder) || !installedVaes.Contains(s.VideoVae) || !installedVaes.Contains(s.AudioVae)
                ? "Select installed H3 Ref2VA model, encoder, video VAE, and audio VAE files." : null);
        var standardIssue = baseIssue ?? (!Options(root, "KSamplerSelect", "sampler_name").Contains("res_multistep") ? "Update ComfyUI: missing res_multistep sampler." : null);
        string? LoraIssue(string file, int steps) => H3Policy.TurboFileIssue(file, steps) ??
            (!installedLoras.Contains(file) ? $"Select an installed Ref2V {steps}-step Turbo LoRA in Video models." :
                new[] { "model", "lora_name", "strength_model" }.Any(f => Input(root, "LoraLoaderModelOnly", f).ValueKind != JsonValueKind.Array) ? "Update ComfyUI: missing model-only LoRA loader inputs." : null);
        var turboIssue = standardIssue ?? LoraIssue(s.TurboLora, 4);
        var turbo8Issue = baseIssue ?? LoraIssue(s.Turbo8StepLora, 8) ??
            (!Options(root, "KSamplerSelect", "sampler_name").Contains("euler") ? "Update ComfyUI: missing Euler sampler for 8-step Turbo." : null);
        if (turbo8Issue is null && (Input(root, "MiniMaxH3SigmaShift", "model").ValueKind != JsonValueKind.Array ||
            !SupportsShift(root, "shift_video", 12) || !SupportsShift(root, "shift_audio", 3)))
            turbo8Issue = "Update ComfyUI: 8-step Turbo requires MiniMaxH3SigmaShift with video shift 12 and audio shift 3.";
        var message = baseIssue ?? string.Join(" ", new[] {
            standardIssue is null ? "Standard ready." : standardIssue,
            turboIssue is null ? "4-step Turbo ready." : "4-step Turbo: " + turboIssue,
            turbo8Issue is null ? "8-step Turbo ready." : "8-step Turbo: " + turbo8Issue,
            "Model compatibility has not been tested." });
        var archiveIssue = archiveMissing.Count > 0 ? "Update ComfyUI: missing " + string.Join(", ", archiveMissing) : null;
        var previewUpscaling = H3PreviewUpscaling.Inspect(root, s);
        var refinement = InspectRefinement(root, standardIssue ?? archiveIssue, previewUpscaling);
        var performance = H3Performance.Inspect(root);
        return new(standardIssue is null, turboIssue is null, message, models, encoders, vaes, loras)
        { DiscoverySucceeded = true, Presets = H3Presets.Inspect(root, s, baseIssue, standardIssue, turboIssue, turbo8Issue),
            VideoReferenceIssue = SupportsVideoReferences(root)
                ? null : "Update ComfyUI to enable LoadVideo, GetVideoComponents, and H3 reference video/audio inputs, then refresh Video models.",
            RefModIssue = ComfyRefModClient.Inspect(root),
            StartFrameIssue = SupportsStartFrame(root) ? null : "Update ComfyUI to start from a frame: it needs the MiniMaxH3AddGuide node. Then refresh Video models.",
            ArchiveIssue = archiveIssue,
            AttentionIssue = H3Performance.Issue(performance, H3Performance.Capture(s.Performance with { SolAttention = false, ArchiveCompression = H3ArchiveCompression.Compact })),
            OptionalLoras = ComfyLoraCatalog.Parse(root), Performance = performance, SelectedPerformanceIssue = H3Performance.Issue(performance, H3Performance.Capture(s.Performance)),
            Turbo8StepReady = turbo8Issue is null, TurboIssue = turboIssue, Turbo8StepIssue = turbo8Issue,
            PackageCaptureReady = refinement.Capture, RefinementIssue = refinement.Issue, LatentUpscalers = refinement.Models,
            PreviewUpscaling = previewUpscaling,
            InstalledModels = installedModels, InstalledEncoders = installedEncoders, InstalledVaes = installedVaes, InstalledLoras = installedLoras,
            Nodes = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal) : [] };
    }
    // Official Ref2VA files, finetunes such as Singularity and FL2VA/Ref2VA merges all name ref2va after a minimax_h3 prefix.
    internal static bool IsRef2VaModel(string file) => ModelStem(file) is var stem && stem.StartsWith("minimax_h3_", StringComparison.Ordinal) && stem.Contains("ref2va", StringComparison.Ordinal);
    private static bool IsFl2VaOnly(string file) => ModelStem(file) is var stem && stem.Contains("fl2va", StringComparison.Ordinal) && !stem.Contains("ref2va", StringComparison.Ordinal);
    private static string ModelStem(string file) => Path.GetFileName(file.Replace('\\', '/')).ToLowerInvariant().Replace('-', '_');
    private static bool SupportsStartFrame(JsonElement root) =>
        new[] { ("positive", "CONDITIONING"), ("latent", "LATENT"), ("frame_idx", "INT"), ("vae", "VAE"), ("image", "IMAGE") }.All(p =>
            Input(root, StartFrameNode, p.Item1) is { ValueKind: JsonValueKind.Array } input && input.GetArrayLength() > 0 &&
            input[0].ValueKind == JsonValueKind.String && input[0].GetString() == p.Item2);
    internal const string StartFrameNode = "MiniMaxH3AddGuide";
    private static bool SupportsVideoReferences(JsonElement root)
    {
        bool Output(string node, string type) => root.TryGetProperty(node, out var n) && n.TryGetProperty("output", out var o) &&
            o.ValueKind == JsonValueKind.Array && o.GetArrayLength() > 0 && o[0].ValueKind == JsonValueKind.String && o[0].GetString() == type;
        var video = Input(root, "GetVideoComponents", "video");
        if (Input(root, "LoadVideo", "file").ValueKind != JsonValueKind.Array || !Output("LoadVideo", "VIDEO") || !Output("GetVideoComponents", "IMAGE") ||
            video.ValueKind != JsonValueKind.Array || video.GetArrayLength() == 0 || video[0].ValueKind != JsonValueKind.String || video[0].GetString() != "VIDEO") return false;
        foreach (var (field, name, type) in new[] { ("ref_videos", "ref_video", "IMAGE"), ("ref_video_audios", "ref_video_audio", "AUDIO") })
        {
            var input = Input(root, "MiniMaxH3ReferenceToVideo", field);
            if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() < 2 || input[1].ValueKind != JsonValueKind.Object ||
                !input[1].TryGetProperty("template", out var template) || !template.TryGetProperty("max", out var max) || !max.TryGetInt32(out var capacity) || capacity < 3 ||
                !template.TryGetProperty("prefix", out var prefix) || prefix.GetString() != name + "_" ||
                !template.TryGetProperty("input", out var nested) || !nested.TryGetProperty("required", out var required) || !required.TryGetProperty(name, out var port) ||
                port.ValueKind != JsonValueKind.Array || port.GetArrayLength() == 0 || port[0].ValueKind != JsonValueKind.String || port[0].GetString() != type) return false;
        }
        return true;
    }
    private static bool SupportsShift(JsonElement root, string field, double value)
    {
        var input = Input(root, "MiniMaxH3SigmaShift", field);
        return input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 1 && input[0].ValueKind == JsonValueKind.String && input[0].GetString() == "FLOAT" && input[1].ValueKind == JsonValueKind.Object &&
            input[1].TryGetProperty("min", out var min) && min.TryGetDouble(out var low) && low <= value &&
            input[1].TryGetProperty("max", out var max) && max.TryGetDouble(out var high) && high >= value;
    }
    public async Task ValidateInputsAsync(VideoSnapshot s, CancellationToken ct)
    {
        H3Policy.Validate(s.Shot, true, requireScene: s.Reel is null);
        if (s.Shot.Videos.Count > 0) await (referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.")).ValidateAsync(s.ProjectId, s.Shot.Videos, ct);
        foreach (var b in s.Shot.Images)
        {
            await using var m = b.Kind == ShotImageKind.AssetImage ? await assets.OpenImageAsync(s.ProjectId, b.AssetId, b.MediaId, ct) : throw new WorkspaceStoreException("The standalone frame was removed. Replace it with an asset image.");
            if (m is null) throw new WorkspaceStoreException($"{b.Name} is missing or in Trash. Restore or replace it before generating.");
        }
        var a = await assets.LoadAsync(s.ProjectId, ct);
        if (s.Production is null && s.Reel is null) ShotLooks.Validate(s.Shot, a);
        foreach (var v in s.Shot.Voices)
        {
            var voice = a.Voices.FirstOrDefault(x => x.Matches(v));
            await using var content = await voices.OpenVoiceAsync(s.ProjectId, v.VoiceId, ct: ct);
            if (voice is null || content is null || v.Start + v.Duration > voice.Duration + .01) throw new WorkspaceStoreException("A voice reference is missing, in Trash, or has an invalid excerpt.");
        }
        if (s.Shot.StartFrame is not null) await StartTakeAsync(s, ct);
        if (s.Shot.ContinuityFrame is { } continuity)
        {
            var source = (await shots.LoadAsync(s.ProjectId, ct)).Takes.FirstOrDefault(t => t.Id == continuity.TakeId);
            if (source is null || continuity.Frame >= source.FrameCount)
                throw new WorkspaceStoreException($"The take of the continuity picture “{continuity.Name}” is in Trash or was deleted. Restore it, or remove the picture.");
        }
    }
    /// <summary>The take a shot starts from, after checking it is still in the project and matches the shot's frame shape.</summary>
    private async Task<ShotTake> StartTakeAsync(VideoSnapshot s, CancellationToken ct)
    {
        var start = s.Shot.StartFrame!;
        var take = (await shots.LoadAsync(s.ProjectId, ct)).Takes.FirstOrDefault(t => t.Id == start.TakeId)
            ?? throw new WorkspaceStoreException("The take this shot starts from is in Trash or was deleted. Restore it, or remove the starting frame.");
        if (start.Frame >= take.FrameCount) throw new WorkspaceStoreException("The starting frame is past the end of its take. Choose the frame again.");
        if (Math.Abs((double)take.Width / take.Height - (double)s.Width / s.Height) > .02)
            throw new WorkspaceStoreException($"The starting frame is {take.Width} × {take.Height}, a different shape from this shot's {s.Width} × {s.Height}. Use the same aspect ratio as the take, or remove the starting frame.");
        return take;
    }
    public async Task PrepareAsync(VideoRun run, string directory, CancellationToken ct)
    {
        await ValidateInputsAsync(run.Snapshot, ct);
        var folder = Path.Combine(directory, "inputs"); Directory.CreateDirectory(folder); run.Inputs.Clear();
        foreach (var b in run.Snapshot.Shot.Images)
        {
            await using var source = b.Kind == ShotImageKind.AssetImage ? await assets.OpenImageAsync(run.Snapshot.ProjectId, b.AssetId, b.MediaId, ct) : throw new WorkspaceStoreException("The standalone frame was removed. Replace it with an asset image.");
            if (source is null) throw new WorkspaceStoreException("An image disappeared before it could be captured.");
            var file = $"image-{run.Inputs.Count:D2}.png";
            await File.WriteAllBytesAsync(Path.Combine(folder, file), await ComfyReferenceImageEditor.PrepareSourcePngAsync(source.Content, b.Crop, ct), ct);
            run.Inputs.Add(new(file, false));
        }
        if (run.Snapshot.Shot.ContinuityFrame is { } continuity)
        {
            var file = $"image-{run.Inputs.Count:D2}.png";
            await File.WriteAllBytesAsync(Path.Combine(folder, file), await ProductionInputs.ContinuityPngAsync(run.Snapshot.ProjectId, continuity, shots, ct), ct);
            run.Inputs.Add(new(file, false));
        }
        if (run.Snapshot.Shot.Videos.Count > 0)
            await referenceVideos!.PrepareAsync(run.Snapshot.ProjectId, run.Snapshot.Shot, folder, run.Inputs, run.Snapshot.Settings, ct);
        var library = await assets.LoadAsync(run.Snapshot.ProjectId, ct);
        foreach (var v in run.Snapshot.Shot.Voices)
        {
            var info = library.Voices.Single(x => x.Matches(v));
            await using var content = await voices.OpenVoiceAsync(run.Snapshot.ProjectId, v.VoiceId, ct: ct) ?? throw new WorkspaceStoreException("A voice disappeared before capture.");
            var original = Path.Combine(folder, "original-" + v.VoiceId.ToString("N") + Path.GetExtension(info.FileName));
            await using (var target = File.Create(original)) await content.Content.CopyToAsync(target, ct);
            var file = $"voice-{run.Inputs.Count:D2}.wav";
            await mediaTools.PrepareVoiceAsync(original, Path.Combine(folder, file), v.Start, v.Duration, run.Snapshot.Settings, ct);
            File.Delete(original); run.Inputs.Add(new(file, true));
        }
        if (run.Snapshot.Shot.StartFrame is { } start)
        {
            await StartTakeAsync(run.Snapshot, ct);
            await using var frame = await shots.OpenAsync(run.Snapshot.ProjectId, start.TakeId, ShotTrashKind.Take, start.Frame, ct: ct)
                ?? throw new WorkspaceStoreException("The starting frame could not be read from its take. Check that the take's video is intact.");
            const string file = "start-frame.png";
            await using (var target = File.Create(Path.Combine(folder, file))) await frame.Content.CopyToAsync(target, ct);
            run.Inputs.Add(new(file, false) { Kind = VideoInputKind.StartFrame });
        }
        run.InputsPrepared = true;
    }
    public async Task<string> SubmitAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct)
    {
        var directory = await shots.RunDirectoryAsync(run.Snapshot.ProjectId, run.Id, ct);
        var check = await CheckAsync(new() { ComfyUrl = run.Snapshot.ExecutionComfyUrl,
            H3 = run.Snapshot.Settings with { LatentUpscaler = run.Refinement?.Upscaler ?? run.Snapshot.Settings.LatentUpscaler,
                Performance = H3Performance.Preferences(run.Snapshot.Performance) } }, ct);
        object workflow;
        H3Loras.CheckSubmission(run.Snapshot, check.OptionalLoras);
        if (run.Refinement is { } refinement)
        {
            if (check.RefinementIssue is { } issue) throw new WorkspaceStoreException(issue);
            var (video, audio) = await UploadRefinementAsync(run.Snapshot.ExecutionComfyUrl, Path.Combine(directory, "inputs", H3RefinementPackage.FileName), Path.Combine(directory, "refinement-upload"), ct);
            var uploaded = await UploadAsync(run, directory, ct);
            workflow = BuildWorkflow(run.Snapshot, candidate.Seed, candidate.ClientId, uploaded, refine: new(refinement, video, audio));
        }
        else
        {
            if (run.Snapshot.Shot.Videos.Count == 0) await ValidateInputsAsync(run.Snapshot, ct);
            if (run.Snapshot.Shot.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.FullReel) && check.VideoReferenceIssue is { } videoIssue) throw new WorkspaceStoreException(videoIssue);
            H3Presets.CheckSubmission(run.Snapshot, check);
            H3PreviewUpscaling.CheckSubmission(run.Snapshot, check);
            if (run.Snapshot.CaptureRefinementData && !check.PackageCaptureReady) throw new WorkspaceStoreException("Update ComfyUI to keep refinement data for new takes.");
            var uploaded = await UploadAsync(run, directory, ct);
            workflow = BuildWorkflow(run.Snapshot, candidate.Seed, candidate.ClientId, uploaded);
        }
        using var http = Client(run.Snapshot.ExecutionComfyUrl);
        using var submitted = await http.PostAsJsonAsync("prompt", workflow, ct);
        if (!submitted.IsSuccessStatusCode) throw new AiGenerationException("ComfyUI rejected the H3 workflow. Refresh Video models and check the server.");
        using var result = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct));
        if (!Guid.TryParse(result.RootElement.GetProperty("prompt_id").GetString(), out var id)) throw new AiGenerationException("ComfyUI returned no valid job ID. Check its queue before retrying.");
        return id.ToString("D");
    }
    // Named by content, so identical references keep one name across batches and ComfyUI can reuse their cached encoding.
    internal static async Task<string> UploadNameAsync(Stream content, string fileName, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(content, ct));
        content.Position = 0;
        return "h3-" + hash[..32] + Path.GetExtension(fileName).ToLowerInvariant();
    }
    internal async Task<IReadOnlyList<PreparedVideoInput>> UploadAsync(VideoRun run, string directory, CancellationToken ct,
        IReadOnlyDictionary<Guid, ReelRefModReference>? preparedRefMods = null)
    {
        using var http = Client(run.Snapshot.ExecutionComfyUrl);
        await ValidateRefModsAsync(run.Snapshot, ct, preparedRefMods);
        List<PreparedVideoInput> uploaded = [];
        foreach (var input in run.Inputs)
        {
            await using var stream = File.OpenRead(CapturedInputStore.Resolve(directory, input.FileName, input.Sha256));
            using var body = new MultipartFormDataContent(); body.Add(new StreamContent(stream), "image", await UploadNameAsync(stream, input.FileName, ct));
            body.Add(new StringContent("input"), "type"); body.Add(new StringContent("lumibelle"), "subfolder"); body.Add(new StringContent("true"), "overwrite");
            using var response = await http.PostAsync("upload/image", body, ct); response.EnsureSuccessStatusCode();
            using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var file = receipt.RootElement.GetProperty("name").GetString()!;
            var sub = receipt.RootElement.TryGetProperty("subfolder", out var f) ? f.GetString() : "";
            uploaded.Add(input with { FileName = string.IsNullOrEmpty(sub) ? file : sub + "/" + file });
        }
        return uploaded;
    }
    // A take refinement starts from the take's saved latents instead of empty noise: the video latent is
    // enlarged, joined with the saved audio latent, and partially re-noised by a Standard pass at the
    // new size. The prompt and references are encoded again at that size.
    public sealed record RefineSource(TakeRefinement Refinement, string VideoLatent, string AudioLatent);
    public static object BuildWorkflow(VideoSnapshot s, long seed, string clientId, IReadOnlyList<PreparedVideoInput> inputs,
        IReadOnlyDictionary<Guid, ReelRefModReference>? preparedRefMods = null, RefineSource? refine = null)
    {
        H3Policy.Validate(s.Shot, true, requireScene: s.Reel is null);
        H3Policy.ValidateSettings(s.Settings);
        H3Performance.Validate(s.Performance);
        H3Presets.Validate(s);
        H3Loras.ValidateSnapshot(s);
        var performance = s.Performance ?? H3Performance.Legacy;
        var sampling = H3Policy.Sampling(s.Shot, s.Settings);
        if (s.Sampling is not null && s.Sampling != sampling) throw new WorkspaceStoreException("The captured sampling profile does not match this shot. Start a new batch.");
        if (H3Presets.UsesTurboLora(H3Presets.Key(s.Shot)) && sampling.Lora is { } lora && H3Policy.TurboFileIssue(lora, sampling.Steps) is { } issue) throw new WorkspaceStoreException(issue);
        if (!inputs.Select(i => (i.EffectiveKind, i.VideoIndex)).SequenceEqual(ReferenceVideos.InputOrder(s.Shot))) throw new WorkspaceStoreException("Prepared inputs do not match the shot.");
        object Link(string id, int port = 0) => new object[] { id, port };
        var nodes = new Dictionary<string, object>();
        void Node(string id, string type, object values) => nodes.Add(id, new { class_type = type, inputs = values });
        Node("1", "UNETLoader", new { unet_name = s.Settings.Model, weight_dtype = "default" });
        Node("2", "CLIPLoader", new { clip_name = s.Settings.Encoder, type = "minimax", device = "default" });
        Node("3", "VAELoader", new { vae_name = s.Settings.VideoVae }); Node("4", "VAELoader", new { vae_name = s.Settings.AudioVae });
        var (width, height) = refine is null ? (s.Width, s.Height) : (refine.Refinement.Width, refine.Refinement.Height);
        // The starting frame is not a reference: it is anchored as frame 0 after the references are encoded.
        // A refinement already starts from the take, so its first frame needs no anchor.
        var start = inputs.SingleOrDefault(i => i.EffectiveKind == VideoInputKind.StartFrame);
        if (start is not null) inputs = [.. inputs.Where(i => i != start)];
        if (refine is not null) start = null;
        if (ReelRefMods.Uses(s.Shot)) RefModConditioning(s with { Width = width, Height = height }, inputs, Node, preparedRefMods);
        else
        {
            var conditioning = new Dictionary<string, object> { ["clip"] = Link("2"), ["vae"] = Link("3"), ["audio_vae"] = Link("4"), ["prompt"] = s.Prompt,
                ["width"] = width, ["height"] = height, ["length"] = s.FrameCount, ["ref_image_size"] = "match" };
            var picture = 0; var audio = 0;
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i]; var id = (100 + i).ToString();
                if (input.EffectiveKind == VideoInputKind.Video)
                {
                    Node(id, "LoadVideo", new { file = input.FileName });
                    Node(id + "-frames", "GetVideoComponents", new { video = Link(id) });
                    conditioning[$"ref_videos.ref_video_{ResolvedReferences.For(s.Shot).Videos.Single(v => v.BindingIndex == input.VideoIndex).Number - 1}"] = Link(id + "-frames");
                }
                else
                {
                    Node(id, input.Audio ? "LoadAudio" : "LoadImage", input.Audio ? new Dictionary<string, object> { ["audio"] = input.FileName } : new Dictionary<string, object> { ["image"] = input.FileName });
                    conditioning[input.EffectiveKind == VideoInputKind.VideoSoundtrack ? $"ref_video_audios.ref_video_audio_{ResolvedReferences.For(s.Shot).Videos.Single(v => v.BindingIndex == input.VideoIndex).Number - 1}" :
                        input.Audio ? $"ref_audios.ref_audio_{audio++}" : $"ref_images.ref_image_{picture++}"] = Link(id);
                }
            }
            Node("5", "MiniMaxH3ReferenceToVideo", conditioning);
        }
        var latent = ReelRefMods.Uses(s.Shot) ? 2 : 1;
        var guided = "5";
        if (start is not null)
        {
            Node("60", "LoadImage", new { image = start.FileName });
            Node("61", StartFrameNode, new { positive = Link("5"), latent = Link("5", latent), frame_idx = 0, vae = Link("3"), image = Link("60") });
            guided = "61";
        }
        // A refinement pass always uses Standard sampling, whatever preset made the take.
        var key = refine is null ? H3Presets.Key(s.Shot) : "standard";
        var model = "1";
        if (H3Presets.UsesTurboLora(key) || key == H3HyperFlow.Key) { Node("16", "LoraLoaderModelOnly", new { model = Link("1"), lora_name = sampling.Lora, strength_model = sampling.LoraStrength }); model = "16"; }
        if (key is "larry" or "pdd")
        {
            if (key == "pdd") { Node("50", "MiniMaxH3SigmaShift", new { model = Link(model), shift_video = 12.0, shift_audio = 3.0 }); model = "50"; }
            var recipe = H3Presets.Inputs(key, s.Settings); recipe["model"] = Link(model);
            Node("51", H3Presets.Node(key), recipe); model = "51";
        }
        model = LoraPolicy.AddNodes(nodes, model, H3Loras.Applied(s));
        if (key is "turbo8" or H3HyperFlow.Key)
        {
            Node("18", "MiniMaxH3SigmaShift", new { model = Link(model), shift_video = sampling.VideoShift, shift_audio = sampling.AudioShift }); model = "18";
        }
        var scheduleModel = key == "turbo8" ? model : "1";
        model = H3Performance.Apply(performance, model, Node);
        if (key == "spectrum")
        {
            var recipe = H3Presets.Inputs(key, s.Settings); recipe["model"] = Link(model);
            Node("51", H3Presets.Node(key), recipe); model = "51";
        }
        if (key is "larry" or "spectrum") scheduleModel = model;
        Node("6", "BasicGuider", new { model = Link(model), conditioning = Link(guided) });
        Node("7", "RandomNoise", new { noise_seed = seed });
        if (key == "larry") Node("8", "MiniMaxH3TurboSampler", new { });
        else Node("8", "KSamplerSelect", new { sampler_name = refine is null ? sampling.Sampler : "res_multistep" });
        // H3Presets.Validate above verifies the saved v1 grid. ManualSigmas takes
        // video-shifted sigmas directly, so do not pass these through BasicScheduler.
        if (key == H3HyperFlow.Key) Node("9", "ManualSigmas", new { sigmas = s.Preset!.Inputs.GetProperty("sigmas").GetString()! });
        else if (refine is not null) Node("9", "BasicScheduler", new { model = Link(scheduleModel), scheduler = "simple", steps = refine.Refinement.Steps, denoise = refine.Refinement.Denoise });
        else if (key != "pdd") Node("9", "BasicScheduler", new { model = Link(scheduleModel), scheduler = sampling.Scheduler, steps = sampling.Steps, denoise = 1.0 });
        var source = Link("5", latent);
        if (refine is { Refinement: var r })
        {
            Node("30", "LoadLatent", new { latent = refine.VideoLatent }); Node("31", "LoadLatent", new { latent = refine.AudioLatent });
            Node("32", H3PreviewUpscaling.Node, H3PreviewUpscaling.Inputs(H3PreviewUpscaling.Capture(r.Implementation, r.Upscaler, s.Shot.Aspect) with { Width = r.Width, Height = r.Height }, Link("30")));
            Node("33", "LTXVConcatAVLatent", new { video_latent = Link("32"), audio_latent = Link("31") });
            source = Link("33");
        }
        Node("10", "SamplerCustomAdvanced", new { noise = Link("7"), guider = Link("6"), sampler = Link("8"), sigmas = key == "pdd" ? Link("51", 1) : Link("9"), latent_image = source });
        var output = H3PreviewUpscaling.Apply(s, Node);
        // Refine keeps the take's audio exactly: decode the saved audio latent beside the new video.
        var keepAudio = refine?.Refinement.Mode == TakeRefinementMode.Refine;
        if (keepAudio)
        {
            Node("34", "LTXVSeparateAVLatent", new { av_latent = Link("10") });
            Node("35", "LTXVConcatAVLatent", new { video_latent = Link("34"), audio_latent = Link("31") });
            output = "35";
        }
        // Keep the latents with stock nodes so the take can be refined later. They come from the same
        // sampler output the take decodes, so a refinement that keeps the audio reproduces it exactly.
        if (s.CaptureRefinementData || refine is not null)
        {
            Node("20", "LTXVSeparateAVLatent", new { av_latent = Link("10") });
            Node("21", "SaveLatent", new { samples = Link("20"), filename_prefix = "lumibelle/" + clientId + "/latent-video" });
            Node("22", "SaveLatent", new { samples = keepAudio ? Link("31") : Link("20", 1), filename_prefix = "lumibelle/" + clientId + "/latent-audio" });
        }
        Node("11", "VAEDecode", new { samples = Link(output), vae = Link("3") }); Node("12", "VAEDecodeAudio", new { samples = Link(output), vae = Link("4") });
        Node("13", "CreateVideo", new { images = Link("11"), audio = Link("12"), fps = 24, bit_depth = 8 });
        Node("14", "SaveVideo", new Dictionary<string, object> { ["video"] = Link("13"), ["filename_prefix"] = "lumibelle/" + clientId + "/video", ["format"] = "mp4", ["format.codec"] = "h264" });
        if (refine is not null || s.OutputPolicy?.SaveLosslessFrames != false)
        {
            Node("17", "RebatchImages", new { images = Link("11"), batch_size = LosslessFrameArchive.SegmentFrames });
            Node("15", "SaveAnimatedWEBP", new { images = Link("17"), filename_prefix = "lumibelle/" + clientId + "/frames", fps = 24, lossless = true, quality = performance.ArchiveQuality, method = performance.ArchiveMethod });
        }
        return new { client_id = clientId, prompt = nodes };
    }
    public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(VideoRun run, VideoCandidate candidate, [EnumeratorCancellation] CancellationToken ct)
    {
        using var http = Client(run.Snapshot.ExecutionComfyUrl);
        await foreach (var u in monitor.ObserveAsync(http, candidate.PromptId!, candidate.ClientId, MonitorOptions, ct)) yield return u;
    }
    public async Task<bool> ExistsAsync(VideoRun run, VideoCandidate c, CancellationToken ct)
    {
        using var http = Client(run.Snapshot.ExecutionComfyUrl);
        using var history = JsonDocument.Parse(await http.GetStringAsync("history/" + c.PromptId, ct));
        if (history.RootElement.TryGetProperty(c.PromptId!, out _)) return true;
        using var queue = JsonDocument.Parse(await http.GetStringAsync("queue", ct));
        return new[] { "queue_running", "queue_pending" }.Any(k => queue.RootElement.TryGetProperty(k, out var list) && list.EnumerateArray().Any(row => row.GetArrayLength() > 1 && row[1].GetString() == c.PromptId));
    }
    public async Task<bool> CancelAsync(VideoRun run, VideoCandidate c, CancellationToken ct)
    {
        if (c.PromptId is null) return true;
        using var http = Client(run.Snapshot.ExecutionComfyUrl);
        using var response = await http.PostAsJsonAsync("api/jobs/" + c.PromptId + "/cancel", new { }, ct);
        if (response.IsSuccessStatusCode) return true;
        using var queued = await http.PostAsJsonAsync("queue", new { delete = new[] { c.PromptId } }, ct);
        return false; // Never issue the global /interrupt fallback.
    }
    private static string ViewUrl(JsonElement output) => "view?filename=" + Uri.EscapeDataString(output.GetProperty("filename").GetString()!) +
        "&subfolder=" + Uri.EscapeDataString(output.TryGetProperty("subfolder", out var sub) ? sub.GetString() ?? "" : "") + "&type=" + Uri.EscapeDataString(output.TryGetProperty("type", out var type) ? type.GetString() ?? "output" : "output");
    public async Task<ShotTake> DownloadAsync(VideoRun run, VideoCandidate c, string directory, Func<string, Task> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var outputs = c.Output!.Value.GetProperty("outputs");
        var videoOutput = outputs.GetProperty("14");
        var videos = videoOutput.TryGetProperty("images", out var im) ? im : videoOutput.TryGetProperty("videos", out var vi) ? vi : throw new WorkspaceStoreException("No video output was returned.");
        var saveFrames = run.Refinement is not null || run.Snapshot.OutputPolicy?.SaveLosslessFrames != false;
        var frames = saveFrames ? outputs.GetProperty("15").GetProperty("images").EnumerateArray().ToArray() : [];
        var expectedFiles = saveFrames ? (run.Snapshot.FrameCount + LosslessFrameArchive.SegmentFrames - 1) / LosslessFrameArchive.SegmentFrames : 0;
        if (videos.GetArrayLength() != 1 || frames.Length != expectedFiles || frames.Select(ViewUrl).Distinct().Count() != frames.Length) throw new WorkspaceStoreException("The returned video/frame sequence is incomplete or duplicated.");
        using var http = Client(run.Snapshot.ExecutionComfyUrl);
        var videoPath = Path.Combine(directory, "video.mp4");
        if (!File.Exists(videoPath))
        {
            await progress("Downloading MP4…"); using var response = await http.GetAsync(ViewUrl(videos[0]), HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using (var output = File.Create(videoPath + ".tmp")) await response.Content.CopyToAsync(output, ct);
            DurableFile.Flush(videoPath + ".tmp");
            File.Move(videoPath + ".tmp", videoPath, true);
        }
        VideoFileInfo info;
        var size = RefinementPolicy.OutputSize(run);
        try
        {
            info = await mediaTools.VideoInfoAsync(videoPath, run.Snapshot.Settings, ct);
            if (info.Width != size.Width || info.Height != size.Height || info.Frames != run.Snapshot.FrameCount || info.Fps != 24 || !info.HasAudio) throw new WorkspaceStoreException("The MP4 does not match its captured frame count, size, or audio output.");
        }
        catch (Exception e) when (e is WorkspaceStoreException or JsonException or FormatException or KeyNotFoundException)
        { File.Delete(videoPath); throw new WorkspaceStoreException("The downloaded MP4 could not be validated. Retry its transfer without regenerating.", e); }
        var archive = await DownloadWebpFramesAsync(http, frames, directory, info, progress, ct);
        H3RefinementPackage? package = null;
        if (run.Snapshot.CaptureRefinementData || run.Refinement is not null)
            package = await DownloadPackageAsync(http, outputs, run, c.TakeId, directory, progress, ct);
        return new() { Id = c.TakeId, ShotId = run.Snapshot.Shot.Id, RunId = run.Id, Candidate = c.Number, Seed = c.Seed,
            RefinementPackage = package, Refinement = ShotCopy.Of(run.Refinement),
            CreatedUtc = DateTimeOffset.UtcNow, Width = info.Width, Height = info.Height, Fps = 24, Directory = c.TakeId.ToString("D"), Frames = archive,
            Bytes = new FileInfo(videoPath).Length + archive.DistinctBy(f => f.FileName).Sum(f => f.Bytes) + (package?.Bytes ?? 0), Snapshot = ShotCopy.Of(run.Snapshot) };
    }

    private static async Task<List<ShotFrame>> DownloadWebpFramesAsync(HttpClient http, JsonElement[] outputs, string directory, VideoFileInfo info, Func<string, Task> progress, CancellationToken ct)
    {
        List<ShotFrame> frames = [];
        for (var segment = 0; segment < outputs.Length; segment++)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(LosslessFrameArchive.SegmentFrames, info.Frames - frames.Count);
            var file = LosslessFrameArchive.FileName(segment); var path = Path.Combine(directory, file);
            await progress($"Archiving lossless WebP · {frames.Count}/{info.Frames} frames · file {segment + 1}/{outputs.Length}");
            IReadOnlyList<int>? indices = null;
            if (File.Exists(path))
            {
                try { indices = await LosslessFrameArchive.ValidateAsync(path, info.Width, info.Height, count, ct); }
                catch (WorkspaceStoreException) { File.Delete(path); }
            }
            if (indices is null)
            {
                var transfer = path + ".download";
                if (!File.Exists(transfer))
                {
                    using var response = await http.GetAsync(ViewUrl(outputs[segment]), HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
                    await using (var output = File.Create(transfer + ".tmp")) await response.Content.CopyToAsync(output, ct);
                    DurableFile.Flush(transfer + ".tmp");
                    File.Move(transfer + ".tmp", transfer, true);
                }
                try { indices = await LosslessFrameArchive.CleanAsync(transfer, path, info.Width, info.Height, count, ct); }
                catch (WorkspaceStoreException) { File.Delete(transfer); throw; }
                File.Delete(transfer);
            }
            foreach (var suffix in new[] { ".download", ".download.tmp", ".tmp" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            var bytes = new FileInfo(path).Length;
            foreach (var index in indices) frames.Add(new(frames.Count, file, bytes) { ArchiveFrameIndex = index });
        }
        return frames;
    }
}
