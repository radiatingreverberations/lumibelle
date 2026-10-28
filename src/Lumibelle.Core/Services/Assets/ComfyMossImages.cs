using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed partial class ComfyMossImages(IHttpClientFactory clients, TimeProvider clock, IComfyExecutionMonitor monitor)
{
    public const string OutputNode = "9";
    public static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening MOSS-Image…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the MOSS text encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the FLUX.2 VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the image prompt and references…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing MOSS-Image…", "Generating image", "steps", "sampling"),
            ["8"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the MOSS-Image workflow. Refresh image models and check the installed nodes.",
        "MOSS-Image could not run. Check the model, encoder, VAE, edit LoRA and GPU memory in ComfyUI.",
        "Image generation timed out before ComfyUI accepted the job.", "Image generation timed out.",
        "ComfyUI returned an unreadable MOSS-Image output.", "The connection to ComfyUI failed during image generation.");

    // Native ComfyUI graph, including its edit encoder's conditioning on BOTH CFG branches.
    // No Diffusers plugin, Python execution or model download is required by Lumibelle.
    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId = null, IReadOnlyList<AppliedLora>? loras = null, bool legacyReferenceSizing = false)
    {
        var moss = MossImagePolicy.Settings(settings); MossImagePolicy.ValidateSettings(moss);
        var editing = references.Count > 0;
        if (string.IsNullOrWhiteSpace(prompt) || seed < 0 || references.Count > MossImagePolicy.MaximumReferences ||
            references.Any(string.IsNullOrWhiteSpace) || width < 16 || height < 16 || width % 16 != 0 || height % 16 != 0 ||
            (long)width * height > 4_500_000 || editing && string.IsNullOrWhiteSpace(moss.EditLora) ||
            LoraPolicy.InvalidApplied(loras ?? [], ImageWorkflow.MossImage) ||
            (loras ?? []).Any(l => l.Reference.FileName == moss.EditLora))
            throw new AiGenerationException("Check the MOSS prompt, canvas, seed, optional LoRAs and configured edit LoRA (up to four references).");
        static object[] Link(string id) => [id, 0];
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = moss.Model, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = moss.TextEncoder, type = "moss_image", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = moss.Vae }),
            ["6"] = Node("EmptyFlux2LatentImage", new { width, height, batch_size = 1 }),
            ["8"] = Node("VAEDecode", new { samples = Link("7"), vae = Link("3") }),
            [OutputNode] = Node("PreviewImage", new { images = Link("8") })
        };
        var model = LoraPolicy.AddNodes(nodes, "1", loras ?? []);
        if (editing)
        {
            nodes["edit_lora"] = Node("LoraLoaderModelOnly", new { model = Link(model), lora_name = moss.EditLora, strength_model = 1.0 });
            model = "edit_lora";
            var positive = new Dictionary<string, object> { ["clip"] = Link("2"), ["prompt"] = prompt, ["vae"] = Link("3"), ["edit_format"] = moss.EditFormat, ["image_strength"] = 1.0 };
            var negative = new Dictionary<string, object>(positive) { ["prompt"] = moss.NegativePrompt };
            for (var i = 0; i < references.Count; i++)
            {
                var id = $"reference_{i + 1}";
                nodes[id] = Node("ETN_LoadImageBase64", new { image = references[i] });
                (int Width, int Height) source;
                try { source = QwenImage21Policy.PngSize(Convert.FromBase64String(references[i])); }
                catch (FormatException) { throw new AiGenerationException("A captured MOSS reference is not valid Base64."); }
                // The edit adapter conditions on reference latents at their actual scale.
                // Match the output pixel budget while retaining each reference's aspect;
                // a fixed 512px cap weakens edits on larger canvases. Old queued profiles
                // retain their captured execution policy.
                var scale = legacyReferenceSizing ? Math.Min(1, 512.0 / Math.Max(source.Width, source.Height))
                    : Math.Sqrt((double)width * height / ((double)source.Width * source.Height));
                // Round upward to the model grid so canvas alignment doesn't needlessly
                // shrink an already matching reference (e.g. 1024 square for a 1 MP edit).
                var refWidth = Math.Max(16, legacyReferenceSizing ? (int)(source.Width * scale) / 16 * 16 : (int)Math.Ceiling(source.Width * scale / 16) * 16);
                var refHeight = Math.Max(16, legacyReferenceSizing ? (int)(source.Height * scale) / 16 * 16 : (int)Math.Ceiling(source.Height * scale / 16) * 16);
                nodes[id + "_scale"] = Node("ImageScale", new { image = Link(id), width = refWidth, height = refHeight, upscale_method = "area", crop = "disabled" });
                // Native TemplatePrefix sockets start at zero; author-facing image labels start at one.
                positive[$"images.image_{i}"] = negative[$"images.image_{i}"] = Link(id + "_scale");
            }
            nodes["4"] = Node("TextEncodeMOSSImageEdit", positive);
            nodes["5"] = Node("TextEncodeMOSSImageEdit", negative);
        }
        else
        {
            nodes["4"] = Node("CLIPTextEncode", new { clip = Link("2"), text = prompt });
            nodes["5"] = Node("CLIPTextEncode", new { clip = Link("2"), text = moss.NegativePrompt });
            if (moss.SkipLayerGuidance)
            {
                nodes["slg"] = Node("SkipLayerGuidanceDiTSimple", new { model = Link(model), double_layers = "", single_layers = "22", start_percent = 0.0, end_percent = 0.5 });
                model = "slg";
            }
        }
        // MOSS uses multiplier 1. ModelSamplingSD3 from older plugin graphs is incompatible.
        nodes["shift"] = Node("ModelSamplingAuraFlow", new { model = Link(model), shift = moss.Shift });
        nodes["7"] = Node("KSampler", new
        {
            model = Link("shift"), seed, steps = editing ? moss.EditSteps : moss.Steps, cfg = editing ? moss.EditGuidance : moss.Guidance,
            sampler_name = "euler", scheduler = "simple", denoise = 1.0, positive = Link("4"), negative = Link("5"), latent_image = Link("6")
        });
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, bool editing, CancellationToken ct = default) =>
        (await ProbeAsync(settings, editing, ct)).Check;

    private async Task<(ComfyImageConfiguration Check, IReadOnlyList<AiModel> Loras)> ProbeAsync(AiSettings settings, bool editing, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return (CheckCatalog(json.RootElement, settings, editing),
                ComfyNodeCatalog.Choices(json.RootElement, "LoraLoaderModelOnly", "lora_name").Select(n => new AiModel(n, n)).ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (new(false, "The MOSS-Image connection check timed out.", [], [], []), []); }
        catch (HttpRequestException) { return (new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []), []); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return (new(false, "ComfyUI returned an unexpected MOSS node catalog.", [], [], []), []); }
    }

    public async Task<ComfyImageEditConfiguration> CheckEditAsync(AiSettings settings, CancellationToken ct = default)
    {
        var (check, loras) = await ProbeAsync(settings, true, ct);
        // Discovery remains available when the selected edit file is missing.
        return new(check.Success, check.Message, loras, MossImagePolicy.MaximumReferences);
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement root, AiSettings settings, bool editing)
    {
        var moss = MossImagePolicy.Settings(settings); MossImagePolicy.ValidateSettings(moss);
        var models = ComfyNodeCatalog.Choices(root, "UNETLoader", "unet_name").Select(n => new AiModel(n, n)).ToArray();
        var encoders = ComfyNodeCatalog.Choices(root, "CLIPLoader", "clip_name").Select(n => new AiModel(n, n)).ToArray();
        var vaes = ComfyNodeCatalog.Choices(root, "VAELoader", "vae_name").Select(n => new AiModel(n, n)).ToArray();
        var loras = ComfyNodeCatalog.Choices(root, "LoraLoaderModelOnly", "lora_name");
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type", "device"],
            ["VAELoader"] = ["vae_name"], ["EmptyFlux2LatentImage"] = ["width", "height", "batch_size"],
            ["ModelSamplingAuraFlow"] = ["model", "shift"], ["VAEDecode"] = ["samples", "vae"], ["PreviewImage"] = ["images"],
            ["KSampler"] = ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"]
        };
        if (editing)
        {
            required["TextEncodeMOSSImageEdit"] = ["clip", "prompt", "vae", "edit_format", "image_strength"];
            required["LoraLoaderModelOnly"] = ["model", "lora_name", "strength_model"];
            required["ETN_LoadImageBase64"] = ["image"];
            required["ImageScale"] = ["image", "width", "height", "upscale_method", "crop"];
        }
        else
        {
            required["CLIPTextEncode"] = ["clip", "text"];
            if (moss.SkipLayerGuidance) required["SkipLayerGuidanceDiTSimple"] = ["model", "double_layers", "single_layers", "start_percent", "end_percent"];
        }
        var missing = required.SelectMany(n => n.Value.Where(i => !ComfyNodeCatalog.Input(root, n.Key, i, out _)).Select(i => n.Key + "." + i)).ToList();
        if (!ComfyNodeCatalog.Choices(root, "CLIPLoader", "type").Contains("moss_image")) missing.Add("CLIPLoader moss_image type");
        foreach (var choice in new[] { ("sampler_name", "euler"), ("scheduler", "simple") })
            if (!ComfyNodeCatalog.Choices(root, "KSampler", choice.Item1).Contains(choice.Item2)) missing.Add("KSampler " + choice.Item2);
        if (editing && (!ComfyNodeCatalog.References(root, "TextEncodeMOSSImageEdit", MossImagePolicy.MaximumReferences) ||
            !ComfyNodeCatalog.Output(root, "TextEncodeMOSSImageEdit", "CONDITIONING"))) missing.Add("TextEncodeMOSSImageEdit ordered image inputs/conditioning output");
        if (editing && !ComfyNodeCatalog.Choices(root, "TextEncodeMOSSImageEdit", "edit_format").Contains(moss.EditFormat)) missing.Add("TextEncodeMOSSImageEdit format " + moss.EditFormat);
        if (missing.Count > 0) return new(false, "MOSS-Image needs these nodes or inputs: " + string.Join(", ", missing) + ". Update ComfyUI; reference loading also uses comfyui-tooling-nodes.", models, encoders, vaes);
        var absent = new List<string>();
        if (!models.Any(m => m.Id == moss.Model)) absent.Add("diffusion model " + moss.Model);
        if (!encoders.Any(m => m.Id == moss.TextEncoder)) absent.Add("text encoder " + moss.TextEncoder);
        if (!vaes.Any(m => m.Id == moss.Vae)) absent.Add("VAE " + moss.Vae);
        if (editing && (string.IsNullOrWhiteSpace(moss.EditLora) || !loras.Contains(moss.EditLora))) absent.Add("edit LoRA (choose its exact installed file)");
        return new(absent.Count == 0, absent.Count > 0 ? "Configured MOSS-Image files are missing: " + string.Join(", ", absent) + "."
            : "MOSS-Image " + (editing ? "editing · up to four ordered references" : "image creation") + " is available. Model execution has not been tested by this check.", models, encoders, vaes);
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
