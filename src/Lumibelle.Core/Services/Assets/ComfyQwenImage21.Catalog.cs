using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed partial class ComfyQwenImage21
{
    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, bool editing, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token); response.EnsureSuccessStatusCode();
            using var catalog = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return CheckCatalog(catalog.RootElement, settings, editing);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "The Qwen Image 2.1 connection check timed out.", [], [], []); }
        catch (HttpRequestException) { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(false, "ComfyUI returned an unexpected Qwen node catalog.", [], [], []); }
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement catalog, AiSettings settings, bool editing)
    {
        var qwen = QwenImage21Policy.Settings(settings);
        QwenImage21Policy.ValidateSettings(qwen);
        var models = Choices(catalog, "UNETLoader", "unet_name").Select(n => new AiModel(n, n)).ToArray();
        var encoders = Choices(catalog, "CLIPLoader", "clip_name").Select(n => new AiModel(n, n)).ToArray();
        var vaes = Choices(catalog, "VAELoader", "vae_name").Select(n => new AiModel(n, n)).ToArray();
        var required = new Dictionary<string, string[]>
        {
            ["UNETLoader"] = ["unet_name", "weight_dtype"], ["CLIPLoader"] = ["clip_name", "type", "device"],
            ["VAELoader"] = ["vae_name"], ["TextEncodeQwenImage21"] = ["clip", "prompt", "negative_prompt", "resolution"],
            ["KSampler"] = ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"],
            ["VAEDecode"] = ["samples", "vae"], ["PreviewImage"] = ["images"]
        };
        if (editing)
        {
            required["TextEncodeQwenImage21"] = ["clip", "prompt", "negative_prompt", "resolution", "vae"];
            required["QwenImage21Cache"] = ["model", "device", "dtype"];
            required["ETN_LoadImageBase64"] = ["image"];
            required["ImageToMask"] = ["image", "channel"];
            required["JoinImageWithAlpha"] = ["image", "alpha"];
        }
        if (qwen.Variant == QwenImage21Variant.Turbo)
        {
            required.Remove("KSampler");
            required["ManualSigmas"] = ["sigmas"];
            required["KSamplerSelect"] = ["sampler_name"];
            required["RandomNoise"] = ["noise_seed"];
            required["CFGGuider"] = ["model", "positive", "negative", "cfg"];
            required["SamplerCustomAdvanced"] = ["noise", "guider", "sampler", "sigmas", "latent_image"];
        }
        required["EmptyLatentImage"] = ["width", "height", "batch_size"];
        var missing = required.SelectMany(pair => pair.Value.Where(input => !Input(catalog, pair.Key, input, out _))
            .Select(input => pair.Key + "." + input)).ToList();
        if (editing)
            for (var i = 1; i <= QwenImage21Policy.MaximumReferences; i++)
                if (!SupportsReference(catalog, i)) missing.Add($"TextEncodeQwenImage21.images.image_{i}");
        if (!Output(catalog, "TextEncodeQwenImage21", 0, "CONDITIONING") ||
            !Output(catalog, "TextEncodeQwenImage21", 1, "CONDITIONING") || !Output(catalog, "TextEncodeQwenImage21", 2, "LATENT"))
            missing.Add("TextEncodeQwenImage21 positive/negative/latent outputs");
        if (!Choices(catalog, "CLIPLoader", "type").Contains("qwen_image")) missing.Add("CLIPLoader qwen_image type");
        if (!Choices(catalog, qwen.Variant == QwenImage21Variant.Turbo ? "KSamplerSelect" : "KSampler", "sampler_name").Contains("euler")) missing.Add("Euler sampler");
        if (qwen.Variant == QwenImage21Variant.Base && !Choices(catalog, "KSampler", "scheduler").Contains("simple")) missing.Add("KSampler simple scheduler");
        if (editing && !Choices(catalog, "QwenImage21Cache", "device").Contains(qwen.CacheDevice)) missing.Add("QwenImage21Cache device " + qwen.CacheDevice);
        if (editing && !Choices(catalog, "QwenImage21Cache", "dtype").Contains(qwen.CacheDtype)) missing.Add("QwenImage21Cache dtype " + qwen.CacheDtype);
        if (missing.Count > 0) return new(false,
            "Qwen Image 2.1 needs these nodes or inputs: " + string.Join(", ", missing.Distinct()) +
            ". Update ComfyUI; editing also uses the existing comfyui-tooling-nodes image loader.", models, encoders, vaes);
        var absent = new List<string>();
        if (!models.Any(m => m.Id == qwen.Model)) absent.Add(qwen.Model);
        if (!encoders.Any(m => m.Id == qwen.TextEncoder)) absent.Add(qwen.TextEncoder);
        if (!vaes.Any(m => m.Id == qwen.Vae)) absent.Add(qwen.Vae);
        return new(absent.Count == 0, absent.Count > 0
            ? "Configured Qwen Image 2.1 files are missing: " + string.Join(", ", absent) + ". Select the installed 2.1 model, Qwen3-VL 8B encoder and 2.1 VAE."
            : "Qwen Image 2.1 " + (editing ? "editing · up to ten ordered references" : "image creation") + " is available. Model execution has not been tested by this check.",
            models, encoders, vaes);
    }

    private static bool Input(JsonElement catalog, string node, string name, out JsonElement spec)
    {
        spec = default;
        if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty(node, out var info) ||
            info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("input", out var inputs) || inputs.ValueKind != JsonValueKind.Object) return false;
        foreach (var group in new[] { "required", "optional" })
            if (inputs.TryGetProperty(group, out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out spec)) return true;
        return false;
    }
    private static IReadOnlyList<string> Choices(JsonElement catalog, string node, string name)
    {
        if (!Input(catalog, node, name, out var spec) || spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0) return [];
        var choices = spec[0];
        if (choices.ValueKind != JsonValueKind.Array && spec.GetArrayLength() > 1 && spec[1].ValueKind == JsonValueKind.Object && spec[1].TryGetProperty("options", out var named)) choices = named;
        return choices.ValueKind == JsonValueKind.Array ? choices.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
    }
    private static bool Output(JsonElement catalog, string node, int index, string type) => catalog.ValueKind == JsonValueKind.Object &&
        catalog.TryGetProperty(node, out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("output", out var outputs) &&
        outputs.ValueKind == JsonValueKind.Array && outputs.GetArrayLength() > index && outputs[index].ValueKind == JsonValueKind.String && outputs[index].GetString() == type;

    internal static bool SupportsReference(JsonElement catalog, int number)
    {
        if (number is < 1 or > QwenImage21Policy.MaximumReferences) return false;
        var name = "image_" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Input(catalog, "TextEncodeQwenImage21", "images." + name, out var flat))
            return flat.ValueKind == JsonValueKind.Array && flat.GetArrayLength() > 0 && flat[0].ValueKind == JsonValueKind.String && flat[0].GetString() == "IMAGE";
        // V3 exposes the autogrow template as one input; API prompts use its flattened names.
        return Input(catalog, "TextEncodeQwenImage21", "images", out var grow) && HasNamedReference(grow, name, 0);
    }
    private static bool HasNamedReference(JsonElement value, string name, int depth)
    {
        if (depth > 12) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array &&
                names.EnumerateArray().Any(n => n.ValueKind == JsonValueKind.String && n.GetString() == name)) return true;
            return value.EnumerateObject().Any(p => HasNamedReference(p.Value, name, depth + 1));
        }
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => HasNamedReference(v, name, depth + 1));
    }
}
