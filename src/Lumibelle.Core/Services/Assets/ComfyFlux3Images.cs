using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed partial class ComfyFlux3Images(IHttpClientFactory clients, TimeProvider clock, IComfyExecutionMonitor monitor)
{
    public const string OutputNode = "9";
    public static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening FLUX 3 Image Klein…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the Qwen3-VL 4B encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the FLUX.2 VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the scene caption…"),
            ["references"] = new(GenerationPhase.Preparing, "Encoding the ordered reference images…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing FLUX 3…", "Generating image", "steps", "sampling"),
            ["8"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the FLUX 3 workflow. Refresh image models and check the installed nodes.",
        "FLUX 3 could not run. Check its model variant, Qwen3-VL encoder, FLUX.2 VAE and GPU memory in ComfyUI.",
        "Image generation timed out before ComfyUI accepted the job.", "Image generation timed out.",
        "ComfyUI returned an unreadable FLUX 3 image output.", "The connection to ComfyUI failed during image generation.");

    // Native FLUX 3 conditioning. References are ordered independently of the output canvas.
    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId = null, IReadOnlyList<AppliedLora>? loras = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        var flux = Flux3ImagePolicy.Settings(settings); Flux3ImagePolicy.ValidateSettings(flux);
        if (string.IsNullOrWhiteSpace(prompt) || seed < 0 || references.Count > Flux3ImagePolicy.MaximumReferences ||
            references.Any(string.IsNullOrWhiteSpace) || width is < 32 or > 16384 || height is < 32 or > 16384 || width % 32 != 0 || height % 32 != 0 ||
            (long)width * height > Flux3ImagePolicy.MaximumPixels || LoraPolicy.InvalidApplied(loras ?? [], ImageWorkflow.Flux3ImageKlein))
            throw new AiGenerationException("Check the FLUX 3 caption, canvas, seed and up to ten ordered references. Use only FLUX 3-assigned LoRAs.");
        static object[] Link(string id, int output = 0) => [id, output];
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = flux.Model, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = flux.TextEncoder, type = "flux3", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = flux.Vae }),
            ["4"] = Node("CLIPTextEncode", new { clip = Link("2"), text = prompt }),
            ["5"] = flux.Variant == Flux3ImageVariant.Distilled
                ? Node("ConditioningZeroOut", new { conditioning = Link("4") })
                : Node("CLIPTextEncode", new { clip = Link("2"), text = "" }),
            ["6"] = Node("EmptyFlux2LatentImage", new { width, height, batch_size = 1 }),
            ["8"] = Node("VAEDecode", new { samples = Link("7"), vae = Link("3") }),
            [OutputNode] = Node("PreviewImage", new { images = Link("8") })
        };
        var model = LoraPolicy.AddNodes(nodes, "1", loras ?? []);
        var positive = Link("4"); var negative = Link("5");
        if (references.Count > 0)
        {
            var conditioning = new Dictionary<string, object>
            {
                ["positive"] = positive, ["negative"] = negative, ["vae"] = Link("3"),
                ["resolution"] = "resize", ["resolution.megapixels"] = flux.ReferenceMegapixels
            };
            for (var i = 0; i < references.Count; i++)
            {
                var id = $"reference_{i}";
                nodes[id] = Node("ETN_LoadImageBase64", new { image = references[i] });
                conditioning[$"images.image{i}"] = Link(id);
            }
            nodes["references"] = Node("ReferenceMultiLatent", conditioning);
            positive = Link("references"); negative = Link("references", 1);
        }
        nodes["7"] = Node("KSampler", new
        {
            model = Link(model), seed, steps = Flux3ImagePolicy.Steps(flux), cfg = Flux3ImagePolicy.Guidance(flux),
            sampler_name = "euler", scheduler = "simple", denoise = 1.0,
            positive, negative, latent_image = Link("6")
        });
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, bool editing, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return CheckCatalog(json.RootElement, settings, editing);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "The FLUX 3 connection check timed out.", [], [], []); }
        catch (HttpRequestException) { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(false, "ComfyUI returned an unexpected FLUX 3 node catalog.", [], [], []); }
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement root, AiSettings settings, bool editing)
    {
        var flux = Flux3ImagePolicy.Settings(settings); Flux3ImagePolicy.ValidateSettings(flux);
        var models = ComfyNodeCatalog.Choices(root, "UNETLoader", "unet_name").Select(n => new AiModel(n, n)).ToArray();
        var encoders = ComfyNodeCatalog.Choices(root, "CLIPLoader", "clip_name").Select(n => new AiModel(n, n)).ToArray();
        var vaes = ComfyNodeCatalog.Choices(root, "VAELoader", "vae_name").Select(n => new AiModel(n, n)).ToArray();
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type", "device"],
            ["VAELoader"] = ["vae_name"], ["CLIPTextEncode"] = ["clip", "text"],
            ["EmptyFlux2LatentImage"] = ["width", "height", "batch_size"], ["VAEDecode"] = ["samples", "vae"], ["PreviewImage"] = ["images"],
            ["KSampler"] = ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"]
        };
        if (flux.Variant == Flux3ImageVariant.Distilled) required["ConditioningZeroOut"] = ["conditioning"];
        if (editing)
        {
            required["ReferenceMultiLatent"] = ["positive", "negative", "vae", "resolution"];
            required["ETN_LoadImageBase64"] = ["image"];
        }
        var missing = required.SelectMany(n => n.Value.Where(i => !ComfyNodeCatalog.Input(root, n.Key, i, out _)).Select(i => n.Key + "." + i)).ToList();
        if (!ComfyNodeCatalog.Choices(root, "CLIPLoader", "type").Contains("flux3")) missing.Add("CLIPLoader flux3 type");
        foreach (var choice in new[] { ("sampler_name", "euler"), ("scheduler", "simple") })
            if (!ComfyNodeCatalog.Choices(root, "KSampler", choice.Item1).Contains(choice.Item2)) missing.Add("KSampler " + choice.Item2);
        if (editing)
        {
            if (!ComfyNodeCatalog.References(root, "ReferenceMultiLatent", Flux3ImagePolicy.MaximumReferences, "image")) missing.Add("ReferenceMultiLatent ten ordered image inputs");
            if (!ComfyNodeCatalog.Choice(root, "ReferenceMultiLatent", "resolution", "resize")) missing.Add("ReferenceMultiLatent resize mode");
            if (!ComfyNodeCatalog.OutputAt(root, "ReferenceMultiLatent", 0, "CONDITIONING") ||
                !ComfyNodeCatalog.OutputAt(root, "ReferenceMultiLatent", 1, "CONDITIONING")) missing.Add("ReferenceMultiLatent positive/negative outputs");
        }
        if (missing.Count > 0) return new(false, "FLUX 3 needs these nodes or inputs: " + string.Join(", ", missing) +
            ". Update ComfyUI; reference loading also uses comfyui-tooling-nodes.", models, encoders, vaes);
        var absent = new List<string>();
        if (!models.Any(m => m.Id == flux.Model)) absent.Add("diffusion model " + flux.Model);
        if (!encoders.Any(m => m.Id == flux.TextEncoder)) absent.Add("text encoder " + flux.TextEncoder);
        if (!vaes.Any(m => m.Id == flux.Vae)) absent.Add("VAE " + flux.Vae);
        return new(absent.Count == 0, absent.Count > 0 ? "Configured FLUX 3 files are missing: " + string.Join(", ", absent) + "."
            : "FLUX 3 Image Klein " + (editing ? "editing · up to ten ordered references" : "image creation") + " is available. Model execution has not been tested by this check.", models, encoders, vaes);
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
