using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

// Common execution and catalog handling; each family retains its native conditioning graph.
public sealed partial class ComfyCaptionImages(IHttpClientFactory clients, TimeProvider clock, IComfyExecutionMonitor monitor)
{
    public const string OutputNode = "9";
    public static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening the image model…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the text encoder…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the caption and references…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing image generation…", "Generating image", "steps", "sampling"),
            ["8"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the image workflow. Refresh image models and check the installed nodes.",
        "The image model could not run. Check its checkpoint variant, encoder, VAE and GPU memory in ComfyUI.",
        "Image generation timed out before ComfyUI accepted the job.", "Image generation timed out.",
        "ComfyUI returned an unreadable image output.", "The connection to ComfyUI failed during image generation.");

    public static object BuildWorkflow(AiSettings settings, ImageWorkflow workflow, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId = null, IReadOnlyList<AppliedLora>? loras = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        var value = CaptionImagePolicy.Settings(settings, workflow); CaptionImagePolicy.ValidateSettings(value, workflow);
        var fibo = workflow == ImageWorkflow.Fibo2;
        if (references.Count > (fibo ? CaptionImagePolicy.MaximumFiboReferences : 0) ||
            fibo && value.Variant == CaptionImageVariant.Turbo && references.Count == 0 ||
            references.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(prompt) || seed < 0 ||
            width is < 32 or > 16384 || height is < 32 or > 16384 || width % 32 != 0 || height % 32 != 0 ||
            (long)width * height > QwenImage21Policy.MaximumNativePixels || LoraPolicy.InvalidApplied(loras ?? [], workflow))
            throw new AiGenerationException("Check the caption, canvas, seed and workflow-assigned LoRAs. Fibo Edit Turbo needs references; Z-Image supports creation only.");
        static object[] Link(string id, int output = 0) => [id, output];
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = value.Model, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = value.TextEncoder, type = fibo ? "fibo2" : "lumina2", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = value.Vae }),
            ["8"] = Node("VAEDecode", new { samples = Link("7"), vae = Link("3") }),
            [OutputNode] = Node("PreviewImage", new { images = Link("8") })
        };
        var model = LoraPolicy.AddNodes(nodes, "1", loras ?? []);
        object[] positive = Link("4"), negative, latent;
        if (fibo)
        {
            var encoding = new Dictionary<string, object> { ["clip"] = Link("2"), ["prompt"] = prompt,
                ["negative_prompt"] = references.Count > 0 ? "" : value.NegativePrompt, ["vae"] = Link("3"), ["width"] = width, ["height"] = height, ["batch_size"] = 1 };
            for (var i = 0; i < references.Count; i++)
            {
                var id = $"reference_{i}"; nodes[id] = Node("ETN_LoadImageBase64", new { image = references[i] });
                encoding[$"images.image_{i + 1}"] = Link(id);
            }
            nodes["4"] = Node("TextEncodeFibo2", encoding);
            negative = Link("4", 1); latent = Link("4", 2);
        }
        else
        {
            nodes["4"] = Node("CLIPTextEncode", new { clip = Link("2"), text = prompt });
            nodes["5"] = value.Variant == CaptionImageVariant.Turbo ? Node("ConditioningZeroOut", new { conditioning = Link("4") })
                : Node("CLIPTextEncode", new { clip = Link("2"), text = value.NegativePrompt });
            nodes["6"] = Node("EmptySD3LatentImage", new { width, height, batch_size = 1 });
            nodes["sampling"] = Node("ModelSamplingAuraFlow", new { model = Link(model), shift = CaptionImagePolicy.Shift(value, workflow) });
            model = "sampling"; negative = Link("5"); latent = Link("6");
        }
        nodes["7"] = Node("KSampler", new { model = Link(model), seed, steps = CaptionImagePolicy.Steps(value, workflow),
            cfg = CaptionImagePolicy.Guidance(value, workflow), sampler_name = CaptionImagePolicy.Sampler(workflow), scheduler = "simple", denoise = 1.0,
            positive, negative, latent_image = latent });
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, ImageWorkflow workflow, bool editing, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings); using var response = await http.GetAsync("object_info", timeout.Token); response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return CheckCatalog(json.RootElement, settings, workflow, editing);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "The image connection check timed out.", [], [], []); }
        catch (HttpRequestException) { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(false, "ComfyUI returned an unexpected image node catalog.", [], [], []); }
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement root, AiSettings settings, ImageWorkflow workflow, bool editing)
    {
        var value = CaptionImagePolicy.Settings(settings, workflow); CaptionImagePolicy.ValidateSettings(value, workflow);
        var fibo = workflow == ImageWorkflow.Fibo2;
        var models = ComfyNodeCatalog.Choices(root, "UNETLoader", "unet_name").Select(n => new AiModel(n, n)).ToArray();
        var encoders = ComfyNodeCatalog.Choices(root, "CLIPLoader", "clip_name").Select(n => new AiModel(n, n)).ToArray();
        var vaes = ComfyNodeCatalog.Choices(root, "VAELoader", "vae_name").Select(n => new AiModel(n, n)).ToArray();
        if (!fibo && editing) return new(false, "Z-Image Base and Turbo support creation here. Choose an editing workflow to use references.", models, encoders, vaes);
        if (fibo && !editing && value.Variant == CaptionImageVariant.Turbo) return new(false, "Fibo Edit Turbo needs at least one reference image. Select Dev for creation.", models, encoders, vaes);
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type", "device"], ["VAELoader"] = ["vae_name"],
            ["KSampler"] = ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"],
            ["VAEDecode"] = ["samples", "vae"], ["PreviewImage"] = ["images"]
        };
        if (fibo) required["TextEncodeFibo2"] = ["clip", "prompt", "negative_prompt", "vae", "width", "height", "batch_size"];
        else
        {
            required["CLIPTextEncode"] = ["clip", "text"]; required["EmptySD3LatentImage"] = ["width", "height", "batch_size"];
            required["ModelSamplingAuraFlow"] = ["model", "shift"];
            if (value.Variant == CaptionImageVariant.Turbo) required["ConditioningZeroOut"] = ["conditioning"];
        }
        if (editing) required["ETN_LoadImageBase64"] = ["image"];
        var missing = required.SelectMany(n => n.Value.Where(i => !ComfyNodeCatalog.Input(root, n.Key, i, out _)).Select(i => n.Key + "." + i)).ToList();
        if (!ComfyNodeCatalog.Choices(root, "CLIPLoader", "type").Contains(fibo ? "fibo2" : "lumina2")) missing.Add("CLIPLoader " + (fibo ? "fibo2" : "lumina2") + " type");
        if (!ComfyNodeCatalog.Choices(root, "KSampler", "sampler_name").Contains(CaptionImagePolicy.Sampler(workflow))) missing.Add("KSampler " + CaptionImagePolicy.Sampler(workflow));
        if (!ComfyNodeCatalog.Choices(root, "KSampler", "scheduler").Contains("simple")) missing.Add("KSampler simple scheduler");
        if (fibo && (!ComfyNodeCatalog.OutputAt(root, "TextEncodeFibo2", 0, "CONDITIONING") || !ComfyNodeCatalog.OutputAt(root, "TextEncodeFibo2", 1, "CONDITIONING") ||
            !ComfyNodeCatalog.OutputAt(root, "TextEncodeFibo2", 2, "LATENT"))) missing.Add("TextEncodeFibo2 positive/negative/latent outputs");
        if (editing && !ComfyNodeCatalog.References(root, "TextEncodeFibo2", CaptionImagePolicy.MaximumFiboReferences, first: 1)) missing.Add("TextEncodeFibo2 four ordered reference inputs");
        if (missing.Count > 0) return new(false, "Update ComfyUI for " + workflow.Label() + ": " + string.Join(", ", missing) + ". Reference loading uses comfyui-tooling-nodes.", models, encoders, vaes);
        var absent = new[] { (value.Model, models), (value.TextEncoder, encoders), (value.Vae, vaes) }.Where(x => !x.Item2.Any(m => m.Id == x.Item1)).Select(x => x.Item1).ToArray();
        return new(absent.Length == 0, absent.Length > 0 ? "Configured files are missing: " + string.Join(", ", absent) + "."
            : workflow.Label() + " is available. This check does not execute the model.", models, encoders, vaes);
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
}
