using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

// Pika H3 Flex as in ComfyUI's video_pika_h3_flex_ref2va template: the Ref2VA LoRA at strength 1 on the
// official pruned INT8 base, lcm with the template's fixed sigmas, BasicGuider, and no shift node.
public static class H3PikaFlexRecipe
{
    public const string Key = "pika-flex6";
    public const string AdapterFile = "pika_h3_flex_ref2va_v1_bf16.safetensors";
    public const string AdapterHash = "DE3745CEB7D8D5C20898DD8FA3A05F1C0B00089CCD55297B2A8BAB95C766B0E0";
    // The published list; the template notes that a shorter one breaks the schedule.
    public const string Sigmas = "1.0, 0.972973, 0.923077, 0.8, 0.571429, 0.333333, 0.0";
    public static string Model(H3Settings s) => s.PikaBaseModel ?? H3SmdRecipe.BaseFile;
    public static string Adapter(H3Settings s) => s.PikaLora ?? AdapterFile;
    public static bool IsAdapter(string file) => Path.GetFileName(file.Replace('\\', '/')).Equals(AdapterFile, StringComparison.OrdinalIgnoreCase);
    public static VideoSnapshot Effective(VideoSnapshot s) => H3Presets.Key(s.Shot) == Key ? s with { Settings = s.Settings with { Model = Model(s.Settings) } } : s;
    public static Dictionary<string, object> Inputs(H3Settings s) => new() {
        ["kind"] = "adapter", ["model"] = Model(s), ["base_sha256"] = H3SmdRecipe.BaseHash,
        ["lora_name"] = Adapter(s), ["adapter_sha256"] = AdapterHash, ["strength_model"] = 1.0,
        ["sampler"] = "lcm", ["scheduler"] = "manual", ["sigmas"] = Sigmas, ["steps"] = 6,
        ["guider"] = "BasicGuider", ["additional_acceleration_adapter"] = false,
        ["compatibility"] = "Official pruned INT8 Ref2VA base and Pika H3 Flex Ref2VA LoRA, from ComfyUI's Pika H3 Flex template. Uses the configured shared encoder and VAEs; the template uses the NVFP4-AWQ encoder."
    };
    public static string Apply(string model, H3Settings settings, Action<string, string, object> node)
    {
        node("pika-adapter", "LoraLoaderModelOnly", new { model = new object[] { model, 0 }, lora_name = Adapter(settings), strength_model = 1.0 });
        return "pika-adapter";
    }
    public static H3PresetSetup Inspect(JsonElement root, H3Settings settings, string? baseIssue)
    {
        var files = ComfyH3Video.Options(root, "LoraLoaderModelOnly", "lora_name").Where(IsAdapter).ToArray();
        var issue = baseIssue ?? (!H3SmdRecipe.IsBase(Model(settings)) || !ComfyH3Video.Options(root, "UNETLoader", "unet_name").Contains(Model(settings))
            ? "Select the official pruned INT8 Ref2VA base for Pika H3 Flex." : null);
        issue ??= !IsAdapter(Adapter(settings)) || !files.Contains(Adapter(settings)) ? "Select the Pika H3 Flex Ref2VA LoRA." : null;
        issue ??= H3Presets.ContractIssue(root, "LoraLoaderModelOnly", new() { ["model"] = new object[] { "1", 0 }, ["lora_name"] = Adapter(settings), ["strength_model"] = 1.0 }, ["MODEL"]);
        issue ??= H3Presets.ContractIssue(root, "ManualSigmas", new() { ["sigmas"] = Sigmas }, ["SIGMAS"]);
        if (issue is null && !ComfyH3Video.Options(root, "KSamplerSelect", "sampler_name").Contains("lcm")) issue = "Update ComfyUI for lcm sampling.";
        return new(Key, issue, files);
    }
    public static void ValidateIdentity(VideoSnapshot s, H3GenerationRecipe recipe, bool adapterActive = true)
    {
        if (recipe.ProbeAvailable && recipe.Models.Any(m => m.Category == "loras" && m.Sha256 == AdapterHash &&
            (H3Presets.Key(s.Shot) != Key || m.FileName != Adapter(s.Settings))))
            throw new WorkspaceStoreException("Pika H3 Flex weights must use their dedicated preset, not the optional character/style LoRA list.");
        if (H3Presets.Key(s.Shot) != Key) return;
        if (s.Settings.Model != Model(s.Settings) || !H3SmdRecipe.IsBase(s.Settings.Model) || !IsAdapter(Adapter(s.Settings)))
            throw new WorkspaceStoreException("Pika H3 Flex requires its separately selected official pruned INT8 base and Ref2VA LoRA.");
        if (recipe.ProbeAvailable && (recipe.Models.FirstOrDefault(m => m.Category == "diffusion_models")?.Sha256 != H3SmdRecipe.BaseHash ||
            adapterActive && recipe.Models.FirstOrDefault(m => m.Category == "loras" && m.FileName == Adapter(s.Settings))?.Sha256 != AdapterHash))
            throw new WorkspaceStoreException("Pika H3 Flex weights do not match the published pruned INT8 base and Ref2VA LoRA. Select the published files and capture a new request.");
    }
}
