using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed partial class ComfyMiniMaxH3Images(IHttpClientFactory clients, TimeProvider clock, IComfyExecutionMonitor monitor)
{
    public const string OutputNode = "9";
    public static readonly ComfyExecutionOptions ExecutionOptions = ComfyFlux3Images.ExecutionOptions with
    {
        Nodes = new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening MiniMax H3 Image…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the H3 text encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the image VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the prompt and references…"),
            ["7"] = new(GenerationPhase.Preparing, "Preparing MiniMax H3 Image…", "Generating image", "steps", "sampling"),
            ["8"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        RejectedMessage = "ComfyUI rejected MiniMax H3 Image. Refresh image models and check the required nodes.",
        ExecutionErrorMessage = "MiniMax H3 Image could not run. Check its diffusion model, H3 encoder, image VAE and GPU memory.",
        UnreadableMessage = "ComfyUI returned an unreadable MiniMax H3 Image output."
    };

    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId = null, IReadOnlyList<AppliedLora>? loras = null)
    {
        var files = MiniMaxH3ImagePolicy.Settings(settings);
        MiniMaxH3ImagePolicy.ValidateSettings(files);
        if (string.IsNullOrWhiteSpace(prompt) || seed < 0 || references.Count > MiniMaxH3ImagePolicy.MaximumReferences ||
            references.Any(string.IsNullOrWhiteSpace) || width is < 32 or > 16384 || height is < 32 or > 16384 ||
            width % 32 != 0 || height % 32 != 0 || (long)width * height > MiniMaxH3ImagePolicy.MaximumPixels ||
            LoraPolicy.InvalidApplied(loras ?? [], ImageWorkflow.MiniMaxH3Image))
            throw new AiGenerationException("Check the MiniMax H3 Image prompt, canvas, seed and up to sixteen ordered pictures.");
        static object[] Link(string id, int port = 0) => [id, port];
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var conditioning = new Dictionary<string, object>
        {
            ["clip"] = Link("2"), ["vae"] = Link("3"), ["prompt"] = H3ImagePrompts.Compile(prompt, references.Count),
            ["width"] = width, ["height"] = height
        };
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = files.Model, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = files.TextEncoder, type = "minimax", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = files.Vae }),
            ["4"] = Node("MiniMaxH3ImageConditioning", conditioning),
            ["5"] = Node("ConditioningZeroOut", new { conditioning = Link("4") }),
            ["8"] = Node("VAEDecode", new { samples = Link("7"), vae = Link("3") }),
            [OutputNode] = Node("PreviewImage", new { images = Link("8") })
        };
        for (var i = 0; i < references.Count; i++)
        {
            var id = $"reference_{i + 1}";
            nodes[id] = Node("ETN_LoadImageBase64", new { image = references[i] });
            conditioning[$"images.image_{i + 1}"] = Link(id);
        }
        var model = LoraPolicy.AddNodes(nodes, "1", loras ?? []);
        nodes["7"] = Node("KSampler", new
        {
            model = Link(model), seed, steps = 6, cfg = 1.0, sampler_name = "euler", scheduler = "simple", denoise = 1.0,
            positive = Link("4"), negative = Link("5"), latent_image = Link("4", 1)
        });
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, bool editing, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return CheckCatalog(json.RootElement, settings, editing);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "The MiniMax H3 Image connection check timed out.", [], [], []); }
        catch (HttpRequestException) { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(false, "ComfyUI returned an unexpected MiniMax H3 Image catalog.", [], [], []); }
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement root, AiSettings settings, bool editing)
    {
        var files = MiniMaxH3ImagePolicy.Settings(settings);
        var models = ComfyNodeCatalog.Choices(root, "UNETLoader", "unet_name").Select(n => new AiModel(n, n)).ToArray();
        var encoders = ComfyNodeCatalog.Choices(root, "CLIPLoader", "clip_name").Select(n => new AiModel(n, n)).ToArray();
        var vaes = ComfyNodeCatalog.Choices(root, "VAELoader", "vae_name").Select(n => new AiModel(n, n)).ToArray();
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type", "device"],
            ["VAELoader"] = ["vae_name"], ["MiniMaxH3ImageConditioning"] = ["clip", "vae", "prompt", "width", "height"],
            ["ConditioningZeroOut"] = ["conditioning"], ["VAEDecode"] = ["samples", "vae"], ["PreviewImage"] = ["images"],
            ["KSampler"] = ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"]
        };
        if (editing) required["ETN_LoadImageBase64"] = ["image"];
        var missing = required.SelectMany(n => n.Value.Where(i => !ComfyNodeCatalog.Input(root, n.Key, i, out _)).Select(i => n.Key + "." + i)).ToList();
        if (!ComfyNodeCatalog.Choice(root, "CLIPLoader", "type", "minimax")) missing.Add("CLIPLoader minimax type");
        if (!ComfyNodeCatalog.Choice(root, "KSampler", "sampler_name", "euler")) missing.Add("KSampler euler");
        if (!ComfyNodeCatalog.Choice(root, "KSampler", "scheduler", "simple")) missing.Add("KSampler simple");
        if (!ComfyNodeCatalog.OutputAt(root, "MiniMaxH3ImageConditioning", 0, "CONDITIONING") ||
            !ComfyNodeCatalog.OutputAt(root, "MiniMaxH3ImageConditioning", 1, "LATENT")) missing.Add("image conditioning outputs");
        if (editing && !ComfyNodeCatalog.References(root, "MiniMaxH3ImageConditioning", 16, "image_", 1)) missing.Add("sixteen ordered image inputs");
        if (missing.Count > 0) return new(false, "MiniMax H3 Image needs " + string.Join(", ", missing) + ". Update ComfyUI. Reference loading uses comfyui-tooling-nodes.", models, encoders, vaes);
        var absent = new List<string>();
        if (!models.Any(m => m.Id == files.Model)) absent.Add("diffusion model " + files.Model);
        if (!encoders.Any(m => m.Id == files.TextEncoder)) absent.Add("text encoder " + files.TextEncoder);
        if (!vaes.Any(m => m.Id == files.Vae)) absent.Add("image VAE " + files.Vae);
        return new(absent.Count == 0, absent.Count > 0 ? "Configured MiniMax H3 Image files are missing. " + string.Join(", ", absent)
            : "MiniMax H3 Image is available · six steps, up to sixteen ordered pictures. Model execution has not been tested by this check.", models, encoders, vaes);
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }
}
