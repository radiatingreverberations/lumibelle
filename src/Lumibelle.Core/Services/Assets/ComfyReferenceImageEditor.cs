using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Assets;

public sealed class ComfyReferenceImageEditor(
    IHttpClientFactory clients,
    IAiSettingsStore settingsStore,
    TimeProvider clock,
    IComfyExecutionMonitor monitor) : IReferenceImageEditor
{
    public const int MaximumReferences = 2;
    internal const int MaximumSourcePixels = 2_000_000;
    internal const int MaximumEncodedSourceBytes = 16 * 1024 * 1024;

    internal static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening the Krea 2 model…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the image text encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the image VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Applying the identity edit model…"),
            ["5"] = new(GenerationPhase.Preparing, "Reading the source image…"),
            ["6"] = new(GenerationPhase.Preparing, "Encoding the source image…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing the image canvas…"),
            ["8"] = new(GenerationPhase.Preparing, "Preparing the reference…"),
            ["9"] = new(GenerationPhase.Preparing, "Grounding the edit instruction…"),
            ["10"] = new(GenerationPhase.Preparing, "Preparing image conditioning…"),
            ["11"] = new(GenerationPhase.Preparing, "Preparing Krea 2 Edit…", "Editing image", "steps", "sampling"),
            ["12"] = new(GenerationPhase.Finalizing, "Decoding the edited image…"),
            ["13"] = new(GenerationPhase.Finalizing, "Preparing the edited output…"),
            ["14"] = new(GenerationPhase.Preparing, "Reading the second reference…"),
            ["15"] = new(GenerationPhase.Preparing, "Encoding the second reference…")
        },
        "ComfyUI rejected the Krea 2 Edit workflow. Refresh image models and editing dependencies in AI settings.",
        "ComfyUI could not execute the Krea 2 Edit workflow. Check model compatibility and GPU memory.",
        "Image editing timed out before ComfyUI accepted the job.",
        "Image editing timed out.",
        "ComfyUI returned an unreadable edited image response.",
        "The connection to ComfyUI failed during image editing.");

    public async Task<ComfyImageEditConfiguration> CheckAsync(AiSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= await settingsStore.LoadAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token),
                cancellationToken: timeout.Token);
            var root = json.RootElement;
            var required = new[]
            {
                "UNETLoader", "CLIPLoader", "VAELoader", "LoraLoaderModelOnly", "ETN_LoadImageBase64",
                "VAEEncode", "EmptySD3LatentImage", "Krea2EditModelPatch", "Krea2EditGroundedEncode",
                "KSampler", "VAEDecode", "PreviewImage"
            };
            if (required.Any(name => !root.TryGetProperty(name, out _)))
                return new(false,
                    "Install comfyui-tooling-nodes and the current comfyui-krea2edit node pack, then restart ComfyUI.", []);
            if (!HasCurrentEditInputs(root))
                return new(false,
                    "Update comfyui-krea2edit: Lumibelle requires the current target_latent, pixel, fidelity, fit, and grounding inputs.", []);

            var models = Options(root, "UNETLoader", "unet_name").Where(IsKreaTurbo).ToHashSet(StringComparer.Ordinal);
            var encoders = Options(root, "CLIPLoader", "clip_name").Where(IsKreaEncoder).ToHashSet(StringComparer.Ordinal);
            var vaes = Options(root, "VAELoader", "vae_name").Where(IsKreaVae).ToHashSet(StringComparer.Ordinal);
            var loras = Options(root, "LoraLoaderModelOnly", "lora_name")
                .Where(IsIdentityEditLora).Select(name => new AiModel(name, name)).ToArray();
            var missing = new List<string>();
            if (!models.Contains(settings.ComfyImageModel)) missing.Add($"model '{settings.ComfyImageModel}'");
            if (!encoders.Contains(settings.ComfyImageTextEncoder)) missing.Add($"text encoder '{settings.ComfyImageTextEncoder}'");
            if (!vaes.Contains(settings.ComfyImageVae)) missing.Add($"VAE '{settings.ComfyImageVae}'");
            if (loras.All(model => model.Id != settings.ComfyImageEditLora)) missing.Add($"identity-edit LoRA '{settings.ComfyImageEditLora}'");
            var configured = missing.Count == 0;
            var twoImages = new[] { "source_latent_b", "source_image_b", "ref_boost_a" }
                .All(input => HasInput(root, "Krea2EditModelPatch", input)) && HasInput(root, "Krea2EditGroundedEncode", "image_b");
            return new(configured,
                configured ? "Connected. Krea 2 image editing is ready." :
                    $"Connected, but these Krea 2 editing files are unavailable: {string.Join("; ", missing)}. Install them in ComfyUI, then refresh image models.", loras,
                twoImages ? MaximumReferences : 1, twoImages ? "Krea 2 supports a base image and one additional reference." :
                "Update comfyui-krea2edit and restart ComfyUI to enable two-image edits. Single-image editing is available.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "The ComfyUI image-editing check timed out.", []); }
        catch (HttpRequestException)
        { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", []); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(false, "ComfyUI returned an unexpected node catalog.", []); }
    }

    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, Stream source,
        CancellationToken cancellationToken = default) => EditAsync(request,
            [new(request.SourceAssetId, request.SourceImageId, source)], cancellationToken);

    public async IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request,
        IReadOnlyList<ReferenceImageSource> sources, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var captured = sources.ToArray();
        request = ReferenceEditInputs.Capture(request, captured, MaximumReferences);
        var settings = request.SettingsSnapshot ?? await settingsStore.LoadAsync(cancellationToken);
        await foreach (var update in EditAsync(request, captured, settings, cancellationToken)) yield return update;
    }

    internal async IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request,
        IReadOnlyList<ReferenceImageSource> sources, AiSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request.Regions?.Count > 0) throw new AiGenerationException("Regional edits require the queued review workflow.");
        sources = sources.ToArray();
        request = ReferenceEditInputs.Capture(request, sources, MaximumReferences);
        settings = settings with { LoraLibrary = settings.LoraLibrary.ToArray() };
        ValidateRequest(request);
        ValidateConfiguration(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ImageTimeoutSeconds));
        var encoded = new List<string>();
        foreach (var source in sources)
        {
            var label = $"Preparing image {encoded.Count + 1} of {sources.Count}…";
            yield return new(label, 1, request.Count, Progress: new(GenerationPhase.Preparing, label));
            encoded.Add(await EncodeSourceAsync(source.Content, ReferenceEditInputs.CropFor(request, source), timeout.Token));
        }
        var check = await CheckAsync(settings, timeout.Token);
        if (!check.Success) throw new AiGenerationException(check.Message);
        if (sources.Count > check.MaximumReferences)
            throw new AiGenerationException(check.ReferenceCapabilityMessage ?? "Two-image editing is unavailable.");
        var references = Array.AsReadOnly(sources.Select(s => new AssetImageReference(s.AssetId, s.ImageId)).ToArray());
        using var http = Client(settings);

        var loras = await LoraPolicy.PrepareAsync(request.Loras, settings, ImageWorkflow.Krea2, clients, timeout.Token);
        for (var candidate = 1; candidate <= request.Count; candidate++)
        {
            if (timeout.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new AiCancellationException("Image editing cancelled.", cancellationToken);
                throw new AiGenerationException("Image editing timed out before the next candidate started.");
            }

            var seed = request.Seed is null ? Random.Shared.NextInt64(1, long.MaxValue) :
                checked(request.Seed.Value + candidate - 1);
            var size = ImageAspectPolicy.Size(request.AspectRatio, Convert.FromBase64String(encoded[0]), request.Resolution);
            var started = clock.GetUtcNow();
            ComfyOutput? output = null;
            GenerationProgress? progress = null;
            await foreach (var update in monitor.ExecuteAsync(http,
                clientId => BuildWorkflow(settings, request, encoded, seed, size.Width, size.Height, clientId),
                ExecutionOptions, timeout.Token, cancellationToken))
            {
                progress = update.Progress;
                yield return new(progress.Label, candidate, request.Count, Progress: progress);
                if (update.Complete && update.Job is { } job) output = ReadOutput(job);
            }

            if (output is null) throw new AiGenerationException("ComfyUI finished without an edited image output.");
            progress = new(GenerationPhase.Downloading, "Downloading edited candidate…",
                Elapsed: clock.GetUtcNow() - started, LiveUpdatesAvailable: progress?.LiveUpdatesAvailable ?? false);
            yield return new(progress.Label, candidate, request.Count, Progress: progress);
            var image = await DownloadHandledAsync(http, output, timeout, cancellationToken);
            var metadata = new AssetGenerationMetadata
            {
                Prompt = request.Prompt.Trim(), Seed = seed, AspectRatio = request.AspectRatio, Resolution = request.Resolution,
                DiffusionModel = settings.ComfyImageModel, TextEncoder = settings.ComfyImageTextEncoder,
                Vae = settings.ComfyImageVae, Steps = 10, Loras = loras,
                Edit = new()
                {
                    SourceAssetId = request.SourceAssetId, SourceImageId = request.SourceImageId,
                    Lora = settings.ComfyImageEditLora, LoraStrength = 1f,
                    ReferenceBoost = request.ReferenceBoost, GroundingPixels = request.GroundingPixels,
                    FitMode = "fit", SourceCrop = request.SourceCrop, References = references,
                    ReferenceCrops = request.ReferenceCrops, BaseReferenceBoost = sources.Count == 2 ? request.BaseReferenceBoost : null
                }
            };
            progress = new(GenerationPhase.Saving, "Saving edited candidate…", Elapsed: clock.GetUtcNow() - started,
                LiveUpdatesAvailable: progress.LiveUpdatesAvailable);
            yield return new(progress.Label, candidate, request.Count, image, output.FileName, metadata, progress);
        }
    }

    public static object BuildWorkflow(AiSettings settings, ReferenceEditRequest request, string sourceBase64,
        long seed, int width, int height, string? clientId = null) =>
        BuildWorkflow(settings, request, [sourceBase64], seed, width, height, clientId);

    // Krea2Edit's two-ref workflow uses the base first and subject/donor second in both conditioning paths.
    public static object BuildWorkflow(AiSettings settings, ReferenceEditRequest request, IReadOnlyList<string> images,
        long seed, int width, int height, string? clientId = null)
    {
        if (images.Count is < 1 or > MaximumReferences) throw new AiGenerationException("Krea 2 edits use one or two images.");
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var patch = new Dictionary<string, object>
        {
            ["model"] = Link("4"), ["source_latent"] = Link("6"), ["ref_boost"] = request.ReferenceBoost,
            ["fit_mode"] = "fit", ["vae"] = Link("3"), ["source_image"] = Link("5"), ["target_latent"] = Link("7")
        };
        Dictionary<string, object> Grounded(string prompt) => new()
        {
            ["clip"] = Link("2"), ["prompt"] = prompt, ["image"] = Link("5"), ["grounding_px"] = request.GroundingPixels
        };
        var positive = Grounded(request.Prompt.Trim()); var negative = Grounded("");
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = settings.ComfyImageModel, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = settings.ComfyImageTextEncoder, type = "krea2", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = settings.ComfyImageVae }),
            ["4"] = Node("LoraLoaderModelOnly", new { model = Link("1"), lora_name = settings.ComfyImageEditLora, strength_model = 1f }),
            ["5"] = Node("ETN_LoadImageBase64", new { image = images[0] }),
            ["6"] = Node("VAEEncode", new { pixels = Link("5"), vae = Link("3") }),
            ["7"] = Node("EmptySD3LatentImage", new { width, height, batch_size = 1 }),
            ["8"] = Node("Krea2EditModelPatch", patch),
            ["9"] = Node("Krea2EditGroundedEncode", positive),
            ["10"] = Node("Krea2EditGroundedEncode", negative),
            ["11"] = Node("KSampler", new Dictionary<string, object>
            {
                ["model"] = Link("8"), ["seed"] = seed, ["steps"] = 10, ["cfg"] = 1f,
                ["sampler_name"] = "euler", ["scheduler"] = "simple", ["positive"] = Link("9"),
                ["negative"] = Link("10"), ["latent_image"] = Link("7"), ["denoise"] = 1f
            }),
            ["12"] = Node("VAEDecode", new { samples = Link("11"), vae = Link("3") }),
            ["13"] = Node("PreviewImage", new { images = Link("12") })
        };
        patch["model"] = Link(LoraPolicy.AddNodes(nodes, "4", request.Loras.Where(s => s.Enabled && s.Strength != 0).Select(s => new AppliedLora(s.Reference, s.Strength))));
        if (images.Count == 2)
        {
            nodes["14"] = Node("ETN_LoadImageBase64", new { image = images[1] });
            nodes["15"] = Node("VAEEncode", new { pixels = Link("14"), vae = Link("3") });
            patch["source_latent_b"] = Link("15"); patch["source_image_b"] = Link("14");
            patch["ref_boost_a"] = request.BaseReferenceBoost;
            positive["image_b"] = Link("14"); negative["image_b"] = Link("14");
        }
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    internal static Task<byte[]> PrepareSourcePngAsync(Stream source, CancellationToken cancellationToken = default) =>
        PrepareSourcePngAsync(source, null, cancellationToken);

    internal static async Task<byte[]> PrepareSourcePngAsync(Stream source, ImageCropRegion? crop,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var image = await Image.LoadAsync(source, cancellationToken);
            image.Mutate(context => context.AutoOrient());
            if (crop is not null)
            {
                image.Mutate(context => context.Crop(ImageGeometry.CropPixels(image.Width, image.Height, crop)));
            }
            var pixels = (long)image.Width * image.Height;
            if (pixels > MaximumSourcePixels)
            {
                var scale = Math.Sqrt(MaximumSourcePixels / (double)pixels);
                var width = Math.Max(1, (int)Math.Floor(image.Width * scale));
                var height = Math.Max(1, (int)Math.Floor(image.Height * scale));
                image.Mutate(context => context.Resize(width, height));
            }
            await using var encoded = new MemoryStream();
            // ComfyUI images can contain large compressed workflow/prompt text chunks. Re-encoding
            // pixels without metadata keeps the in-memory Base64 payload small and avoids Pillow's
            // MAX_TEXT_CHUNK guard in ETN_LoadImageBase64.
            await image.SaveAsPngAsync(encoded, new PngEncoder { SkipMetadata = true }, cancellationToken);
            if (encoded.Length is 0 or > MaximumEncodedSourceBytes)
                throw new AiGenerationException("The prepared source image is too large to send to ComfyUI.");
            return encoded.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (AiGenerationException) { throw; }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        { throw new AiGenerationException("The source image could not be decoded as PNG, JPEG, or WebP."); }
    }

    private static async Task<string> EncodeSourceAsync(Stream source, ImageCropRegion? crop,
        CancellationToken cancellationToken) =>
        Convert.ToBase64String(await PrepareSourcePngAsync(source, crop, cancellationToken));

    private static object[] Link(string node) => [node, 0];

    private static void ValidateRequest(ReferenceEditRequest request)
    {
        if (request.SourceAssetId == Guid.Empty || request.SourceImageId == Guid.Empty)
            throw new AiGenerationException("Choose a source image to edit.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new AiGenerationException("Describe the edit first.");
        if (request.Count is < 1 or > 4) throw new AiGenerationException("Generate between one and four candidates.");
        if (request.Seed is < 0 || request.Seed > long.MaxValue - request.Count + 1)
            throw new AiGenerationException("Seed must be nonnegative with room for each candidate.");
        if (!float.IsFinite(request.BaseReferenceBoost) || request.BaseReferenceBoost is < 0 or > 10)
            throw new AiGenerationException("Base fidelity must be between 0 and 10.");
        if (!ImageAspectPolicy.IsSupported(request.AspectRatio, true)) throw new AiGenerationException("Choose a supported aspect ratio.");
        if (!float.IsFinite(request.ReferenceBoost) || request.ReferenceBoost is < 0 or > 10)
            throw new AiGenerationException("Reference fidelity must be between 0 and 10.");
        if (request.GroundingPixels is < 384 or > 1024 || request.GroundingPixels % 64 != 0)
            throw new AiGenerationException("Grounding resolution must be 384–1,024 pixels in steps of 64.");
        if (request.SourceCrop is not null) ValidateCrop(request.SourceCrop);
    }

    internal static void ValidateCrop(ImageCropRegion crop)
    {
        if (!double.IsFinite(crop.X) || !double.IsFinite(crop.Y) || !double.IsFinite(crop.Width) ||
            !double.IsFinite(crop.Height) || crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            crop.X + crop.Width > 1.0000001 || crop.Y + crop.Height > 1.0000001)
            throw new AiGenerationException("The source crop is outside the image bounds.");
    }

    private static void ValidateConfiguration(AiSettings settings)
    {
        FileAiSettingsStore.Validate(settings);
        if (!IsKreaTurbo(settings.ComfyImageModel) || !IsKreaEncoder(settings.ComfyImageTextEncoder) ||
            !IsKreaVae(settings.ComfyImageVae) || !IsIdentityEditLora(settings.ComfyImageEditLora))
            throw new AiGenerationException("Choose compatible Krea 2 image models and an identity-edit LoRA in AI settings first.");
    }

    private static bool HasCurrentEditInputs(JsonElement root) =>
        new[] { "model", "source_latent", "ref_boost", "fit_mode", "vae", "source_image", "target_latent" }
            .All(input => HasInput(root, "Krea2EditModelPatch", input)) &&
        new[] { "clip", "prompt", "image", "grounding_px" }
            .All(input => HasInput(root, "Krea2EditGroundedEncode", input)) &&
        HasInput(root, "ETN_LoadImageBase64", "image");

    private static bool HasInput(JsonElement root, string node, string input)
    {
        var inputs = root.GetProperty(node).GetProperty("input");
        return inputs.TryGetProperty("required", out var required) && required.TryGetProperty(input, out _) ||
            inputs.TryGetProperty("optional", out var optional) && optional.TryGetProperty(input, out _);
    }

    private static IReadOnlyList<string> Options(JsonElement root, string node, string input) => root.GetProperty(node)
        .GetProperty("input").GetProperty("required").GetProperty(input)[0].EnumerateArray()
        .Select(item => item.GetString()!).ToArray();

    private static bool IsIdentityEditLora(string name) => Path.GetFileName(name)
        .StartsWith("krea2_identity_edit", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
    private static bool IsKreaTurbo(string name) => Path.GetFileName(name)
        .Contains("krea2_turbo", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
    private static bool IsKreaEncoder(string name) => Path.GetFileName(name)
        .StartsWith("qwen3vl_4b", StringComparison.OrdinalIgnoreCase);
    private static bool IsKreaVae(string name) => Path.GetFileName(name)
        .StartsWith("qwen_image_vae", StringComparison.OrdinalIgnoreCase);

    private static ComfyOutput ReadOutput(JsonElement job)
    {
        if (job.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty("13", out var preview) &&
            preview.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            var image = images[0];
            return new(image.GetProperty("filename").GetString()!,
                image.TryGetProperty("subfolder", out var folder) ? folder.GetString() ?? "" : "",
                image.TryGetProperty("type", out var type) ? type.GetString() ?? "temp" : "temp");
        }
        throw new AiGenerationException("ComfyUI finished without an edited image output.");
    }

    private static async Task<byte[]> DownloadHandledAsync(HttpClient http, ComfyOutput output,
        CancellationTokenSource timeout, CancellationToken caller)
    {
        try { return await DownloadAsync(http, output, timeout.Token); }
        catch (OperationCanceledException)
        {
            if (caller.IsCancellationRequested) throw new AiCancellationException("Image editing cancelled.", caller);
            throw new AiGenerationException("Image editing timed out while downloading the completed candidate.");
        }
        catch (HttpRequestException)
        { throw new AiGenerationException("The edited image could not be downloaded from ComfyUI."); }
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, ComfyOutput output, CancellationToken cancellationToken)
    {
        var url = $"view?filename={Uri.EscapeDataString(output.FileName)}&subfolder={Uri.EscapeDataString(output.Subfolder)}&type={Uri.EscapeDataString(output.Type)}";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > FileAssetStore.MaximumImageBytes)
            throw new AiGenerationException("ComfyUI returned an image larger than 25 MB.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var result = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (result.Length + read > FileAssetStore.MaximumImageBytes)
                throw new AiGenerationException("ComfyUI returned an image larger than 25 MB.");
            await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return result.ToArray();
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new Uri(settings.ComfyUrl.TrimEnd('/') + "/");
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }

    private sealed record ComfyOutput(string FileName, string Subfolder, string Type);
}
