using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3SmdRecipe
{
    public const string Key = "smd4";
    public const string BaseFile = "minimax_h3_ref2va_pruned_int8_convrot.safetensors";
    public const string BaseHash = "9255F52B6677845AD238F20DFAAFA94727053694127AB7F255C048F0F9365779";
    public const string AdapterFile = "SMD-MiniMax-H3-Ref2VA-4step.safetensors";
    public const string AdapterHash = "9C6F61221A1043EE709AF0D32AE083C17EFEB36950B4B08A945296B5B9C39353";
    public const string Sigmas = "1.0000000000,0.9677419355,0.9000000000,0.6666666667,0";
    public static string Model(H3Settings s) => s.SmdBaseModel ?? BaseFile;
    public static string Adapter(H3Settings s) => s.SmdLora ?? AdapterFile;
    private static bool Named(string file, string expected) => Path.GetFileName(file.Replace('\\', '/')).Equals(expected, StringComparison.OrdinalIgnoreCase);
    public static bool IsBase(string file) => Named(file, BaseFile);
    public static bool IsAdapter(string file) => Named(file, AdapterFile);
    public static VideoSnapshot Effective(VideoSnapshot s) => H3Presets.Key(s.Shot) == Key ? s with { Settings = s.Settings with { Model = Model(s.Settings) } } : s;
    public static Dictionary<string, object> Inputs(H3Settings s) => new() {
        ["kind"] = "adapter", ["model"] = Model(s), ["base_sha256"] = BaseHash,
        ["lora_name"] = Adapter(s), ["adapter_sha256"] = AdapterHash, ["strength_model"] = 1.0,
        ["sampler"] = "euler", ["scheduler"] = "manual", ["sigmas"] = Sigmas, ["steps"] = 4,
        ["shift_video"] = 12.0, ["shift_audio"] = 3.0, ["guider"] = "BasicGuider", ["additional_acceleration_adapter"] = false,
        ["compatibility"] = "Official pruned INT8 Ref2VA base and published SMD Ref2VA 4-step adapter. Uses the configured shared encoder and VAEs; the bounded trial retained W4A8 encoding rather than the companion NVFP4-AWQ encoder."
    };
    public static string Apply(string model, H3Settings settings, Action<string, string, object> node)
    {
        node("smd-adapter", "LoraLoaderModelOnly", new { model = new object[] { model, 0 }, lora_name = Adapter(settings), strength_model = 1.0 });
        node("smd-shift", "MiniMaxH3SigmaShift", new { model = new object[] { "smd-adapter", 0 }, shift_video = 12.0, shift_audio = 3.0 });
        return "smd-shift";
    }
    public static H3PresetSetup Inspect(JsonElement root, H3Settings settings, string? baseIssue)
    {
        var files = ComfyH3Video.Options(root, "LoraLoaderModelOnly", "lora_name").Where(IsAdapter).ToArray();
        var issue = baseIssue ?? (!IsBase(Model(settings)) || !ComfyH3Video.Options(root, "UNETLoader", "unet_name").Contains(Model(settings))
            ? "Select the official pruned INT8 Ref2VA base for SMD." : null);
        issue ??= !IsAdapter(Adapter(settings)) || !files.Contains(Adapter(settings)) ? "Select the published SMD Ref2VA 4-step adapter." : null;
        issue ??= H3Presets.ContractIssue(root, "LoraLoaderModelOnly", new() { ["model"] = new object[] { "1", 0 }, ["lora_name"] = Adapter(settings), ["strength_model"] = 1.0 }, ["MODEL"]);
        issue ??= H3Presets.ContractIssue(root, "MiniMaxH3SigmaShift", new() { ["model"] = new object[] { "1", 0 }, ["shift_video"] = 12.0, ["shift_audio"] = 3.0 }, ["MODEL"]);
        issue ??= H3Presets.ContractIssue(root, "ManualSigmas", new() { ["sigmas"] = Sigmas }, ["SIGMAS"]);
        if (issue is null && !ComfyH3Video.Options(root, "KSamplerSelect", "sampler_name").Contains("euler")) issue = "Update ComfyUI for Euler sampling.";
        return new(Key, issue, files);
    }
    public static void ValidateIdentity(VideoSnapshot s, H3GenerationRecipe recipe, bool adapterActive = true)
    {
        if (recipe.ProbeAvailable && recipe.Models.Any(m => m.Category == "loras" && m.Sha256 == AdapterHash &&
            (H3Presets.Key(s.Shot) != Key || m.FileName != Adapter(s.Settings))))
            throw new WorkspaceStoreException("SMD acceleration weights must use their dedicated preset, not the optional character/style LoRA list.");
        if (H3Presets.Key(s.Shot) != Key) return;
        if (s.Settings.Model != Model(s.Settings) || !IsBase(s.Settings.Model) || !IsAdapter(Adapter(s.Settings)))
            throw new WorkspaceStoreException("SMD requires its separately selected official pruned INT8 base and Ref2VA 4-step adapter.");
        if (recipe.ProbeAvailable && (recipe.Models.FirstOrDefault(m => m.Category == "diffusion_models")?.Sha256 != BaseHash ||
            adapterActive && recipe.Models.FirstOrDefault(m => m.Category == "loras" && m.FileName == Adapter(s.Settings))?.Sha256 != AdapterHash))
            throw new WorkspaceStoreException("SMD checkpoint contents do not match the validated pruned INT8 base and 4-step adapter. Select the published files and capture a new request.");
    }
}
