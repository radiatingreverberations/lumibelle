using System.Runtime.CompilerServices;
using lumibelle.Models;
using lumibelle.Services.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace lumibelle.Services.Assets;

public sealed partial class ComfyQwenImage21(IHttpClientFactory clients, TimeProvider clock, IComfyExecutionMonitor monitor)
{
    // From the official Qwen Image 2.1 Turbo templates; changing steps alone is not equivalent.
    public const string TurboSigmas = "1.0, 0.978453, 0.95418, 0.926626, 0.89508, 0.845148, 0.704534, 0.414568, 0";
    public const string OutputNode = "9";
    public static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening Qwen Image 2.1…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening Qwen3-VL 8B…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the Qwen Image 2.1 VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the prompt and ordered references…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing Qwen Image 2.1…", "Generating image", "steps", "sampling"),
            ["8"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the Qwen Image 2.1 workflow. Refresh image models and update ComfyUI.",
        "Qwen Image 2.1 could not run. Check its diffusion model, Qwen3-VL 8B encoder, 2.1 VAE, GPU memory and console.",
        "Image generation timed out before ComfyUI accepted the job.", "Image generation timed out.",
        "ComfyUI returned an unreadable Qwen Image 2.1 output.", "The connection to ComfyUI failed during image generation.");

    // Flattened from the supplied official API workflows. Lumibelle already uses the
    // tooling Base64 loader for captured references and PreviewImage for output retrieval.
    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, QwenImage21Options options, string? clientId = null,
        IReadOnlyList<AppliedLora>? loras = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        QwenImage21Policy.ValidateOptions(options, references.Count > 0);
        var qwen = QwenImage21Policy.Settings(settings); QwenImage21Policy.ValidateSettings(qwen);
        if (qwen.Variant == QwenImage21Variant.Turbo && options.Steps != 8)
            throw new AiGenerationException("Qwen Image 2.1 Turbo requires its fixed eight-step schedule.");
        if (references.Count > QwenImage21Policy.MaximumReferences || references.Any(string.IsNullOrWhiteSpace) ||
            seed < 0 || string.IsNullOrWhiteSpace(prompt) || width < 32 || height < 32 || width > 16384 || height > 16384 || width % 32 != 0 || height % 32 != 0 ||
            (long)width * height > QwenImage21Policy.MaximumNativePixels)
            throw new AiGenerationException("Check the Qwen prompt, output size, seed and one-to-ten edit references.");
        var useSourceLatent = false;
        if (references.Count > 0)
        {
            try
            {
                var first = QwenImage21Policy.PngSize(Convert.FromBase64String(references[0]));
                useSourceLatent = QwenImage21Policy.Size(first.Width, first.Height, options.Resolution) == (width, height);
            }
            catch (FormatException) { throw new AiGenerationException("A captured Qwen reference is not valid Base64."); }
        }
        if (LoraPolicy.InvalidApplied(loras ?? [], ImageWorkflow.QwenImage21))
            throw new AiGenerationException("Use only LoRAs assigned to Qwen Image 2.1.");
        static object[] Link(string id, int output = 0) => [id, output];
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var encoding = new Dictionary<string, object>
        { ["clip"] = Link("2"), ["prompt"] = prompt, ["negative_prompt"] = "", ["resolution"] = options.Resolution };
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = qwen.Model, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = qwen.TextEncoder, type = "qwen_image", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = qwen.Vae }),
            ["4"] = Node("TextEncodeQwenImage21", encoding),
            ["8"] = Node("VAEDecode", new { samples = Link("7"), vae = Link("3") }),
            [OutputNode] = Node("PreviewImage", new { images = Link("8") })
        };
        var model = LoraPolicy.AddNodes(nodes, "1", loras ?? []);
        object[] latent;
        if (references.Count == 0)
        {
            nodes["5"] = Node("EmptyLatentImage", new { width, height, batch_size = 1 });
            latent = Link("5");
        }
        else
        {
            encoding["vae"] = Link("3");
            for (var i = 0; i < references.Count; i++)
            {
                var id = (100 + i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                nodes[id] = Node("ETN_LoadImageBase64", new { image = references[i] });
                var image = id;
                // ETN's auxiliary mask is not ComfyUI's inverted, batched alpha mask.
                // Explicit pixel transport avoids depending on those differing conventions.
                if (AlphaMask(references[i]) is { } alpha)
                {
                    var load = (101 + i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var mask = (102 + i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    image = (103 + i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    nodes[load] = Node("ETN_LoadImageBase64", new { image = alpha });
                    nodes[mask] = Node("ImageToMask", new { image = Link(load), channel = "red" });
                    nodes[image] = Node("JoinImageWithAlpha", new { image = Link(id), alpha = Link(mask) });
                }
                encoding[$"images.image_{i + 1}"] = Link(image);
            }
            nodes["6"] = Node("QwenImage21Cache", new { model = Link(model), device = qwen.CacheDevice, dtype = qwen.CacheDtype });
            model = "6";
            // Output geometry is independent of the ordered reference conditioning.
            if (!useSourceLatent)
            {
                nodes["5"] = Node("EmptyLatentImage", new { width, height, batch_size = 1 });
                latent = Link("5");
            }
            else latent = Link("4", 2);
        }
        nodes["7"] = Node("KSampler", new
        {
            model = Link(model), seed, steps = options.Steps, cfg = 1.0,
            sampler_name = "euler", scheduler = "simple", denoise = 1.0,
            positive = Link("4"), negative = Link("4", 1), latent_image = latent
        });
        if (qwen.Variant == QwenImage21Variant.Turbo)
        {
            nodes["turbo_sigmas"] = Node("ManualSigmas", new { sigmas = TurboSigmas });
            nodes["turbo_sampler"] = Node("KSamplerSelect", new { sampler_name = "euler" });
            nodes["turbo_noise"] = Node("RandomNoise", new { noise_seed = seed });
            nodes["turbo_guider"] = Node("CFGGuider", new { model = Link(model), positive = Link("4"), negative = Link("4", 1), cfg = 1.0 });
            nodes["7"] = Node("SamplerCustomAdvanced", new { noise = Link("turbo_noise"), guider = Link("turbo_guider"),
                sampler = Link("turbo_sampler"), sigmas = Link("turbo_sigmas"), latent_image = latent });
        }
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    internal static string? AlphaMask(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var size = QwenImage21Policy.PngSize(bytes);
            if ((long)size.Width * size.Height > QwenImage21Inputs.MaximumDecodedPixels)
                throw new AiGenerationException("A prepared Qwen reference exceeds the decoded-image safety limit.");
            using var image = Image.Load<Rgba32>(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 }, bytes);
            using var mask = new Image<L8>(image.Width, image.Height);
            var transparent = false;
            image.ProcessPixelRows(mask, (colors, alphas) =>
            {
                for (var y = 0; y < colors.Height; y++)
                {
                    var source = colors.GetRowSpan(y); var target = alphas.GetRowSpan(y);
                    for (var x = 0; x < source.Length; x++)
                    {
                        var alpha = source[x].A; transparent |= alpha != 255;
                        target[x] = new L8((byte)(255 - alpha));
                    }
                }
            });
            if (!transparent) return null;
            using var output = new MemoryStream(); mask.SaveAsPng(output, new PngEncoder { SkipMetadata = true, BitDepth = PngBitDepth.Bit8, ColorType = PngColorType.Grayscale });
            return Convert.ToBase64String(output.ToArray());
        }
        catch (Exception e) when (e is FormatException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        { throw new AiGenerationException("A captured Qwen reference could not be decoded."); }
    }

    public IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request, AiSettings settings,
        CancellationToken ct = default) => RunAsync(request.Prompt, request.AspectRatio, request.Count, request.Seed,
            request.QwenImage21 ?? new(), null, [], request.Loras, settings, ct);

    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources,
        AiSettings settings, CancellationToken ct = default) => RunAsync(request.Prompt, request.AspectRatio, request.Count, request.Seed,
            request.QwenImage21 ?? new(), request, sources, request.Loras, settings, ct);

    private async IAsyncEnumerable<ReferenceGenerationUpdate> RunAsync(string prompt, string aspect, int count, long? fixedSeed,
        QwenImage21Options options, ReferenceEditRequest? edit, IReadOnlyList<ReferenceImageSource> sources,
        IReadOnlyList<LoraSelection> selections, AiSettings settings, [EnumeratorCancellation] CancellationToken ct)
    {
        sources = sources.ToArray(); selections = LoraPolicy.Capture(selections);
        settings = settings with { QwenImage21 = QwenImage21Policy.Settings(settings) };
        options = QwenImage21Policy.CaptureOptions(settings, options);
        FileAiSettingsStore.Validate(settings); QwenImage21Policy.ValidateOptions(options, edit is not null);
        if (string.IsNullOrWhiteSpace(prompt) || count is < 1 or > 4 || fixedSeed is < 0 || fixedSeed > long.MaxValue - count + 1)
            throw new AiGenerationException("Enter a prompt and choose one to four candidates with a valid seed.");
        if (edit?.Regions is { Count: > 0 }) throw new AiGenerationException("Regional edits require the queued review workflow.");
        if (edit is not null) edit = ReferenceEditInputs.Capture(edit, sources, QwenImage21Policy.MaximumReferences);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ImageTimeoutSeconds));
        var check = await CheckAsync(settings, edit is not null, timeout.Token);
        if (!check.Success) throw new AiGenerationException(check.Message);
        var loras = await LoraPolicy.PrepareAsync(selections, settings, ImageWorkflow.QwenImage21, clients, timeout.Token);
        var encoded = new List<string>(); long totalBytes = 0;
        (int Width, int Height) size = default;
        foreach (var source in sources)
        {
            var png = await QwenImage21Inputs.PrepareAsync(source.Content,
                edit is null ? null : ReferenceEditInputs.CropFor(edit, source), timeout.Token);
            totalBytes += png.Length;
            if (totalBytes > QwenImage21Policy.MaximumPreparedBytes) throw new AiGenerationException("Prepared references exceed 250 MB.");
            var dimensions = QwenImage21Policy.PngSize(png);
            var resized = QwenImage21Policy.Size(dimensions.Width, dimensions.Height, options.Resolution);
            if (encoded.Count == 0) size = edit is null ? resized : QwenImage21Policy.EditSize(aspect, dimensions.Width, dimensions.Height, options);
            encoded.Add(Convert.ToBase64String(png));
        }
        if (edit is null) size = QwenImage21Policy.CreateSize(aspect, options.Resolution);
        using var http = Client(settings);
        for (var candidate = 1; candidate <= count; candidate++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var seed = fixedSeed is null ? Random.Shared.NextInt64(1, long.MaxValue) : fixedSeed.Value + candidate - 1;
            var started = clock.GetUtcNow();
            ComfyReferenceImageGenerator.ComfyOutput? output = null;
            await foreach (var update in monitor.ExecuteAsync(http,
                id => BuildWorkflow(settings, prompt.Trim(), seed, size.Width, size.Height, encoded, options, id, loras),
                ExecutionOptions, timeout.Token, ct))
            {
                yield return new(update.Progress.Label, candidate, count, Progress: update.Progress);
                if (update.Complete && update.Job is { } job) output = ComfyReferenceImageGenerator.ReadOutput(job, OutputNode);
            }
            if (output is null) throw new AiGenerationException("ComfyUI completed without a Qwen Image 2.1 image output.");
            var png = await ComfyReferenceImageGenerator.DownloadHandledAsync(http, output, timeout, ct);
            var actual = ImageInspector.Inspect(png);
            if ((actual.Width, actual.Height) != size) throw new AiGenerationException("The Qwen output dimensions differ from the captured canvas.");
            var metadata = QwenImage21Policy.Metadata(settings, options, prompt, aspect, size, edit,
                sources.Select(s => new AssetImageReference(s.AssetId, s.ImageId)).ToArray(), loras, seed);
            yield return new("Saving candidate…", candidate, count, png, output.FileName, metadata,
                new(GenerationPhase.Saving, "Saving candidate…", Elapsed: clock.GetUtcNow() - started));
        }
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
