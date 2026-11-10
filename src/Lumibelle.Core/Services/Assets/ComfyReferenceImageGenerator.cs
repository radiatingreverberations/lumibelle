using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed class ComfyReferenceImageGenerator(
    IHttpClientFactory clients,
    IAiSettingsStore settingsStore,
    TimeProvider clock,
    IComfyExecutionMonitor monitor) : IReferenceImageGenerator
{
    internal static readonly IReadOnlyDictionary<string, (int Width, int Height)> Sizes =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["1:1"] = (1024, 1024), ["4:3"] = (1152, 864), ["3:2"] = (1216, 832),
            ["16:9"] = (1344, 768), ["2:3"] = (832, 1216), ["3:4"] = (864, 1152), ["9:16"] = (768, 1344)
        };

    internal static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening the Krea 2 model…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the image text encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the image VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the image prompt…"),
            ["5"] = new(GenerationPhase.Preparing, "Preparing image conditioning…"),
            ["6"] = new(GenerationPhase.Preparing, "Preparing the image canvas…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing Krea 2…", "Generating image", "steps", "sampling"),
            ["8"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            ["9"] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the Krea 2 workflow. Refresh image models in AI settings.",
        "ComfyUI could not execute the Krea 2 workflow. Check model compatibility and GPU memory.",
        "Image generation timed out before ComfyUI accepted the job.",
        "Image generation timed out.",
        "ComfyUI returned an unreadable image response.",
        "The connection to ComfyUI failed during image generation.");

    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= await settingsStore.LoadAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = json.RootElement;
            var required = new[] { "UNETLoader", "CLIPLoader", "VAELoader", "CLIPTextEncode", "ConditioningZeroOut", "EmptyLatentImage", "KSampler", "VAEDecode", "PreviewImage" };
            if (required.Any(name => !root.TryGetProperty(name, out _)))
                return new(false, "Update ComfyUI: one or more nodes required by the Krea 2 workflow are unavailable.", [], [], []);
            var models = Options(root, "UNETLoader", "unet_name").Where(IsKreaTurbo).Select(name => new AiModel(name, name)).ToArray();
            var encoders = Options(root, "CLIPLoader", "clip_name").Where(name => Path.GetFileName(name).StartsWith("qwen3vl_4b", StringComparison.OrdinalIgnoreCase)).Select(name => new AiModel(name, name)).ToArray();
            var vaes = Options(root, "VAELoader", "vae_name").Where(name => Path.GetFileName(name).StartsWith("qwen_image_vae", StringComparison.OrdinalIgnoreCase)).Select(name => new AiModel(name, name)).ToArray();
            var success = models.Any(model => model.Id == settings.ComfyImageModel) &&
                encoders.Any(model => model.Id == settings.ComfyImageTextEncoder) && vaes.Any(model => model.Id == settings.ComfyImageVae);
            return new(success, success ? "Connected. The local Krea 2 image workflow is ready." :
                "Connected, but compatible Krea 2 Turbo, Qwen3-VL 4B, or Qwen Image VAE files are missing.", models, encoders, vaes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "The ComfyUI image check timed out.", [], [], []); }
        catch (HttpRequestException)
        { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(false, "ComfyUI returned an unexpected node catalog.", [], [], []); }
    }

    public async IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request = request with { Loras = LoraPolicy.Capture(request.Loras), Tags = request.Tags.ToArray() };
        var settings = await settingsStore.LoadAsync(cancellationToken);
        await foreach (var update in GenerateAsync(request, settings, cancellationToken)) yield return update;
    }

    internal async IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request, AiSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request = request with { Loras = LoraPolicy.Capture(request.Loras) };
        settings = settings with { LoraLibrary = settings.LoraLibrary.ToArray() };
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new AiGenerationException("Describe the reference image first.");
        if (request.Count is < 1 or > 4) throw new AiGenerationException("Generate between one and four candidates.");
        var size = ImageAspectPolicy.Size(request.AspectRatio, resolution: request.Resolution);
        ValidateConfiguration(settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ImageTimeoutSeconds));
        var loras = await LoraPolicy.PrepareAsync(request.Loras, settings, ImageWorkflow.Krea2, clients, timeout.Token);
        using var http = Client(settings);
        for (var candidate = 1; candidate <= request.Count; candidate++)
        {
            if (timeout.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested) throw new AiCancellationException("Generation cancelled.", cancellationToken);
                throw new AiGenerationException("Image generation timed out before the next candidate started.");
            }
            var seed = request.Seed is null ? Random.Shared.NextInt64(1, long.MaxValue) : checked(request.Seed.Value + candidate - 1);
            var started = clock.GetUtcNow();
            ComfyOutput? output = null;
            GenerationProgress? progress = null;
            await foreach (var update in monitor.ExecuteAsync(http,
                clientId => BuildWorkflow(settings, request.Prompt.Trim(), seed, size.Width, size.Height, clientId, loras),
                ExecutionOptions, timeout.Token, cancellationToken))
            {
                progress = update.Progress;
                yield return new(progress.Label, candidate, request.Count, Progress: progress);
                if (update.Complete && update.Job is { } job) output = ReadOutput(job);
            }
            if (output is null) throw new AiGenerationException("ComfyUI finished without an image output.");
            progress = new(GenerationPhase.Downloading, "Downloading candidate…", Elapsed: clock.GetUtcNow() - started,
                LiveUpdatesAvailable: progress?.LiveUpdatesAvailable ?? false);
            yield return new(progress.Label, candidate, request.Count, Progress: progress);
            var image = await DownloadHandledAsync(http, output, timeout, cancellationToken);
            var metadata = new AssetGenerationMetadata
            {
                Prompt = request.Prompt.Trim(), Seed = seed, AspectRatio = request.AspectRatio, Resolution = request.Resolution,
                DiffusionModel = settings.ComfyImageModel, TextEncoder = settings.ComfyImageTextEncoder,
                Vae = settings.ComfyImageVae, Steps = 8, Loras = loras
            };
            progress = new(GenerationPhase.Saving, "Saving candidate…", Elapsed: clock.GetUtcNow() - started,
                LiveUpdatesAvailable: progress.LiveUpdatesAvailable);
            yield return new(progress.Label, candidate, request.Count, image, output.FileName, metadata, progress);
        }
    }

    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height, string? clientId = null,
        IReadOnlyList<AppliedLora>? loras = null)
    {
        var nodes = new Dictionary<string, object>();
        var model = LoraPolicy.AddNodes(nodes, "1", loras ?? []);
        foreach (var node in new Dictionary<string, object>
        {
            ["1"] = new { class_type = "UNETLoader", inputs = new { unet_name = settings.ComfyImageModel, weight_dtype = "default" } },
            ["2"] = new { class_type = "CLIPLoader", inputs = new { clip_name = settings.ComfyImageTextEncoder, type = "krea2", device = "default" } },
            ["3"] = new { class_type = "VAELoader", inputs = new { vae_name = settings.ComfyImageVae } },
            ["4"] = new { class_type = "CLIPTextEncode", inputs = new { text = prompt, clip = new object[] { "2", 0 } } },
            ["5"] = new { class_type = "ConditioningZeroOut", inputs = new { conditioning = new object[] { "4", 0 } } },
            ["6"] = new { class_type = "EmptyLatentImage", inputs = new { width, height, batch_size = 1 } },
            ["7"] = new { class_type = "KSampler", inputs = new
                {
                    model = new object[] { model, 0 }, seed, steps = 8, cfg = 1.0, sampler_name = "euler", scheduler = "simple",
                    positive = new object[] { "4", 0 }, negative = new object[] { "5", 0 }, latent_image = new object[] { "6", 0 }, denoise = 1.0
                } },
            ["8"] = new { class_type = "VAEDecode", inputs = new { samples = new object[] { "7", 0 }, vae = new object[] { "3", 0 } } },
            ["9"] = new { class_type = "PreviewImage", inputs = new { images = new object[] { "8", 0 } } }
        }) nodes.Add(node.Key, node.Value);
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    internal static ComfyOutput ReadOutput(JsonElement job, string outputNode = "9")
    {
        if (job.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty(outputNode, out var preview) &&
            preview.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            var image = images[0];
            return new(image.GetProperty("filename").GetString()!,
                image.TryGetProperty("subfolder", out var folder) ? folder.GetString() ?? "" : "",
                image.TryGetProperty("type", out var type) ? type.GetString() ?? "temp" : "temp");
        }
        throw new AiGenerationException("ComfyUI finished without an image output.");
    }

    internal static async Task<byte[]> DownloadHandledAsync(HttpClient http, ComfyOutput output, CancellationTokenSource timeout, CancellationToken caller, long maximumBytes = FileAssetStore.MaximumImageBytes)
    {
        try { return await DownloadAsync(http, output, timeout.Token, maximumBytes); }
        catch (OperationCanceledException)
        {
            if (caller.IsCancellationRequested) throw new AiCancellationException("Generation cancelled.", caller);
            throw new AiGenerationException("Image generation timed out while downloading the completed candidate.");
        }
        catch (HttpRequestException) { throw new AiGenerationException("The completed image could not be downloaded from ComfyUI."); }
    }

    internal static async Task<byte[]> DownloadAsync(HttpClient http, ComfyOutput output, CancellationToken ct, long maximumBytes = FileAssetStore.MaximumImageBytes)
    {
        var url = $"view?filename={Uri.EscapeDataString(output.FileName)}&subfolder={Uri.EscapeDataString(output.Subfolder)}&type={Uri.EscapeDataString(output.Type)}";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new AiGenerationException($"ComfyUI returned an image larger than {maximumBytes / 1024 / 1024} MB.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var result = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (result.Length + read > maximumBytes) throw new AiGenerationException($"ComfyUI returned an image larger than {maximumBytes / 1024 / 1024} MB.");
            await result.WriteAsync(buffer.AsMemory(0, read), ct);
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

    private static IReadOnlyList<string> Options(JsonElement root, string node, string input) => root.GetProperty(node)
        .GetProperty("input").GetProperty("required").GetProperty(input)[0].EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static bool IsKreaTurbo(string name) => Path.GetFileName(name).Contains("krea2_turbo", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);

    private static void ValidateConfiguration(AiSettings settings)
    {
        FileAiSettingsStore.Validate(settings);
        if (!IsKreaTurbo(settings.ComfyImageModel) || !Path.GetFileName(settings.ComfyImageTextEncoder).StartsWith("qwen3vl_4b", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(settings.ComfyImageVae).StartsWith("qwen_image_vae", StringComparison.OrdinalIgnoreCase))
            throw new AiGenerationException("Choose compatible local Krea 2 image models in AI settings first.");
    }

    internal sealed record ComfyOutput(string FileName, string Subfolder, string Type);
}
