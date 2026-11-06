using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

// Runs the H3 video model on a single latent frame and decodes it as an edited image. The cut uses stock
// nodes, the rest is wired like the video graph, and the files come from AiSettings.H3 and the chosen preset.
public sealed class ComfyH3Images(IHttpClientFactory clients)
{
    public const string OutputNode = "13";
    public static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening MiniMax H3…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the H3 text encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the H3 video VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Opening the H3 audio VAE…"),
            ["5"] = new(GenerationPhase.Preparing, "Encoding the instruction and references…"),
            ["10"] = new(GenerationPhase.Preparing, "Preparing MiniMax H3…", "Generating image", "steps", "sampling"),
            ["11"] = new(GenerationPhase.Finalizing, "Decoding the frame…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the MiniMax H3 image workflow. Refresh image models and check the installed nodes.",
        "MiniMax H3 could not run. Check the model, encoder, VAEs and GPU memory in ComfyUI.",
        "Image generation timed out before ComfyUI accepted the job.", "Image generation timed out.",
        "ComfyUI returned an unreadable MiniMax H3 output.", "The connection to ComfyUI failed during image generation.");

    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId = null, IReadOnlyList<AppliedLora>? loras = null, string preset = H3ImagePolicy.StandardPreset)
    {
        try { return Build(settings, prompt, seed, width, height, references, clientId, loras ?? [], preset); }
        catch (WorkspaceStoreException e) { throw new AiGenerationException(e.Message); }
    }

    private static object Build(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId, IReadOnlyList<AppliedLora> applied, string preset)
    {
        var h3 = settings.H3; H3Policy.ValidateSettings(h3);
        if (H3ImagePolicy.PresetIssue(preset) is { } presetIssue) throw new AiGenerationException(presetIssue);
        if (string.IsNullOrWhiteSpace(prompt) || seed < 0 || references.Count is < 1 or > H3ImagePolicy.MaximumReferences || references.Any(string.IsNullOrWhiteSpace) ||
            width < 32 || height < 32 || width % 32 != 0 || height % 32 != 0 || (long)width * height > H3ImagePolicy.MaximumPixels ||
            LoraPolicy.InvalidApplied(applied, ImageWorkflow.MiniMaxH3) || applied.Any(l => LoraPolicy.ReservedH3(l.Reference.FileName, h3)))
            throw new AiGenerationException("Check the MiniMax H3 instruction, canvas, seed, optional LoRAs and references (one to nine).");
        if (!H3Presets.OwnModel(preset) && ComfyH3Video.OrdinaryModelFileIssue(h3.Model) is { } issue) throw new AiGenerationException(issue);
        var effective = h3 with { Model = H3Presets.Model(preset, h3) };
        var sampling = H3ImagePolicy.Sampling(h3, preset);
        static object[] Link(string id, int port = 0) => [id, port];
        var nodes = new Dictionary<string, object>();
        void Node(string id, string type, object inputs) => nodes.Add(id, new { class_type = type, inputs });
        Node("1", "UNETLoader", new { unet_name = effective.Model, weight_dtype = "default" });
        Node("2", "CLIPLoader", new { clip_name = h3.Encoder, type = "minimax", device = "default" });
        Node("3", "VAELoader", new { vae_name = h3.VideoVae });
        Node("4", "VAELoader", new { vae_name = h3.AudioVae });
        var conditioning = new Dictionary<string, object>
        {
            ["clip"] = Link("2"), ["vae"] = Link("3"), ["audio_vae"] = Link("4"), ["prompt"] = H3ImagePrompts.Compile(prompt, references.Count),
            ["width"] = width, ["height"] = height, ["length"] = H3ImagePolicy.Length, ["ref_image_size"] = H3ImagePolicy.RefImageSize
        };
        for (var i = 0; i < references.Count; i++)
        {
            var id = $"reference_{i + 1}";
            Node(id, "ETN_LoadImageBase64", new { image = references[i] });
            conditioning[$"ref_images.ref_image_{i}"] = Link(id);
        }
        Node("5", "MiniMaxH3ReferenceToVideo", conditioning);
        Node("separate", "LTXVSeparateAVLatent", new { av_latent = Link("5", 1) });
        Node("cut", "LatentCut", new { samples = Link("separate"), dim = "t", index = 0, amount = H3ImagePolicy.LatentFrames });
        Node("join", "LTXVConcatAVLatent", new { video_latent = Link("cut"), audio_latent = Link("separate", 1) });
        // As in the video graph: the schedule reads the base model unless a preset says otherwise, and the guider the patched one.
        var model = LoraPolicy.AddNodes(nodes, H3PresetGraph.Adapters(preset, effective, sampling, "1", Node), applied);
        var scheduleModel = "1";
        if (H3AcceleratedCheckpoints.Full(preset)) { model = H3AcceleratedCheckpoints.Apply(model, Node, preset); scheduleModel = "18"; }
        if (preset == "larry") scheduleModel = model;
        Node("6", "BasicGuider", new { model = Link(model), conditioning = Link("5") });
        Node("7", "RandomNoise", new { noise_seed = seed });
        H3PresetGraph.Sampler(preset, sampling.Sampler, model, Node);
        H3PresetGraph.Schedule(preset, sampling, model, scheduleModel, Node);
        Node("10", "SamplerCustomAdvanced", new { noise = Link("7"), guider = Link("6"), sampler = Link("8"), sigmas = H3PresetGraph.Sigmas(preset), latent_image = Link("join") });
        Node("11", "VAEDecode", new { samples = Link("10"), vae = Link("3") });
        Node(OutputNode, "PreviewImage", new { images = Link("11") });
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    public Task<ComfyImageEditConfiguration> CheckEditAsync(AiSettings settings, CancellationToken ct = default) =>
        ReadCatalogAsync(settings, root => CheckEditCatalog(root, settings), message => new ComfyImageEditConfiguration(false, message, [], H3ImagePolicy.MaximumReferences), ct);

    public Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, bool editing, CancellationToken ct = default, string preset = H3ImagePolicy.StandardPreset) =>
        ReadCatalogAsync(settings, root => CheckCatalog(root, settings, editing, preset), message => new ComfyImageConfiguration(false, message, [], [], []), ct);

    private async Task<T> ReadCatalogAsync<T>(AiSettings settings, Func<JsonElement, T> read, Func<string, T> failure, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl) + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await http.GetAsync("object_info", timeout.Token); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return read(json.RootElement);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return failure("The MiniMax H3 connection check timed out."); }
        catch (HttpRequestException) { return failure("Couldn’t reach ComfyUI. Check its address and connection."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return failure("ComfyUI returned an unexpected MiniMax H3 node catalog."); }
    }

    private const string ReadyMessage = "MiniMax H3 editing · up to nine ordered references. Model execution has not been tested by this check.";

    internal static ComfyImageEditConfiguration CheckEditCatalog(JsonElement root, AiSettings settings)
    {
        var issue = CommonIssue(root, settings);
        return new(issue is null, issue ?? ReadyMessage, [], H3ImagePolicy.MaximumReferences) { H3Presets = issue is null ? PresetSetups(root, settings) : [] };
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement root, AiSettings settings, bool editing, string preset = H3ImagePolicy.StandardPreset)
    {
        var models = ComfyNodeCatalog.Choices(root, "UNETLoader", "unet_name").Select(n => new AiModel(n, n)).ToArray();
        var encoders = ComfyNodeCatalog.Choices(root, "CLIPLoader", "clip_name").Select(n => new AiModel(n, n)).ToArray();
        var vaes = ComfyNodeCatalog.Choices(root, "VAELoader", "vae_name").Select(n => new AiModel(n, n)).ToArray();
        if (!editing) return new(false, H3ImagePolicy.EditOnlyMessage, models, encoders, vaes);
        var issue = CommonIssue(root, settings) ?? H3ImagePolicy.PresetIssue(preset) ?? PresetSetups(root, settings).First(p => p.Key == preset).Issue;
        return new(issue is null, issue ?? ReadyMessage, models, encoders, vaes);
    }

    // What every preset needs: the single-frame graph's nodes, and the shared encoder and VAE files.
    private static string? CommonIssue(JsonElement root, AiSettings settings)
    {
        var h3 = settings.H3;
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type", "device"], ["VAELoader"] = ["vae_name"],
            ["MiniMaxH3ReferenceToVideo"] = ["clip", "vae", "audio_vae", "prompt", "width", "height", "length", "ref_image_size", "ref_images"],
            ["LTXVSeparateAVLatent"] = ["av_latent"], ["LatentCut"] = ["samples", "dim", "index", "amount"], ["LTXVConcatAVLatent"] = ["video_latent", "audio_latent"],
            ["BasicGuider"] = ["model", "conditioning"], ["RandomNoise"] = ["noise_seed"], ["SamplerCustomAdvanced"] = ["noise", "guider", "sampler", "sigmas", "latent_image"],
            ["VAEDecode"] = ["samples", "vae"], ["PreviewImage"] = ["images"], ["ETN_LoadImageBase64"] = ["image"]
        };
        var missing = required.SelectMany(n => n.Value.Where(i => !ComfyNodeCatalog.Input(root, n.Key, i, out _)).Select(i => n.Key + "." + i)).ToList();
        if (!ComfyNodeCatalog.Choice(root, "CLIPLoader", "type", "minimax")) missing.Add("CLIPLoader minimax type");
        if (!ComfyNodeCatalog.Choice(root, "LatentCut", "dim", "t")) missing.Add("LatentCut t dimension");
        if (!ComfyNodeCatalog.Choice(root, "MiniMaxH3ReferenceToVideo", "ref_image_size", H3ImagePolicy.RefImageSize)) missing.Add("MiniMaxH3ReferenceToVideo ref_image_size " + H3ImagePolicy.RefImageSize);
        if (!ReferenceCapacity(root)) missing.Add("MiniMaxH3ReferenceToVideo capacity for nine pictures");
        if (!AcceptsLength(root)) missing.Add("MiniMaxH3ReferenceToVideo length " + H3ImagePolicy.Length);
        if (missing.Count > 0) return "MiniMax H3 images need these nodes or inputs: " + string.Join(", ", missing) + ". Update ComfyUI; reference loading also uses comfyui-tooling-nodes.";
        var encoders = ComfyNodeCatalog.Choices(root, "CLIPLoader", "clip_name"); var vaes = ComfyNodeCatalog.Choices(root, "VAELoader", "vae_name");
        var absent = new List<string>();
        if (!encoders.Contains(h3.Encoder)) absent.Add("text encoder " + h3.Encoder);
        if (!vaes.Contains(h3.VideoVae)) absent.Add("video VAE " + h3.VideoVae);
        if (!vaes.Contains(h3.AudioVae)) absent.Add("audio VAE " + h3.AudioVae);
        return absent.Count > 0 ? "The files selected in Video models are missing: " + string.Join(", ", absent) + "." : null;
    }

    // The same preset check as Video models, so a preset reads as ready in both places or in neither.
    private static IReadOnlyList<H3PresetSetup> PresetSetups(JsonElement root, AiSettings settings)
    {
        var h3 = settings.H3;
        var modelIssue = ComfyH3Video.OrdinaryModelFileIssue(h3.Model) is not null || !ComfyNodeCatalog.Choices(root, "UNETLoader", "unet_name").Contains(h3.Model);
        return ComfyH3Video.Inspect(root, h3).Presets.Select(p => p.Issue is null || !modelIssue || H3Presets.OwnModel(p.Key) ? p
            : p with { Issue = p.Issue + " MiniMax H3 images use the model selected in Video models." }).ToArray();
    }

    private static bool ReferenceCapacity(JsonElement root) =>
        ComfyNodeCatalog.Input(root, "MiniMaxH3ReferenceToVideo", "ref_images", out var spec) && spec.ValueKind == JsonValueKind.Array && spec.GetArrayLength() > 1 &&
        spec[1].ValueKind == JsonValueKind.Object && spec[1].TryGetProperty("template", out var template) &&
        template.TryGetProperty("max", out var max) && max.TryGetInt32(out var maximum) && maximum >= H3ImagePolicy.MaximumReferences;

    private static bool AcceptsLength(JsonElement root) =>
        ComfyNodeCatalog.Input(root, "MiniMaxH3ReferenceToVideo", "length", out var spec) && spec.ValueKind == JsonValueKind.Array && spec.GetArrayLength() > 1 &&
        spec[1].ValueKind == JsonValueKind.Object && spec[1].TryGetProperty("min", out var min) && min.TryGetInt32(out var minimum) && minimum <= H3ImagePolicy.Length;
}
