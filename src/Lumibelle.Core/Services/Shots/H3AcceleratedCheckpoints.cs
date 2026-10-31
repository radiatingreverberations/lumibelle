using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3AcceleratedCheckpoints
{
    public const string Compact = "10eros-compact";
    public const string CompactFile = "10Eros_Max_h3_8stepTURBO_beta7_activation_learned_compact_w4w6int8_14.65gb.safetensors";
    public const string CompactHash = "64BF10E2EF650C2484C60A0EB3BC495BBA707AADC50281361AD0DD8BBA7C7B1E";
    public const string Fast = "fasth3-omni";
    public const string FastFile = "fasth3_omniref_8step_pruned_int8_convrot.safetensors";
    public const string FastHash = "851B2DC9670ED864B7F1E951AC9DA047D83B1C04DED9BFCC1A9E5BBA0EE33F80";
    public static bool Full(string key) => key is Compact or Fast;
    public static string Model(H3Settings settings, string key = Compact) => key == Fast ? settings.FastH3Model ?? FastFile : settings.TenErosCompactModel ?? CompactFile;
    public static bool IsCompactFile(string file) => Path.GetFileName(file.Replace('\\', '/')).Equals(CompactFile, StringComparison.OrdinalIgnoreCase);
    public static bool IsFastFile(string file) => Path.GetFileName(file.Replace('\\', '/')).Equals(FastFile, StringComparison.OrdinalIgnoreCase);
    private static bool Matches(string file, string key) => key == Fast ? IsFastFile(file) : IsCompactFile(file);
    public static bool IsBakedFile(string file)
    {
        var name = Path.GetFileName(file.Replace('\\', '/'));
        return IsCompactFile(file) || name.Contains("10eros", StringComparison.OrdinalIgnoreCase) && name.Contains("8stepTURBO", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("fasth3", StringComparison.OrdinalIgnoreCase) && name.Contains("omniref", StringComparison.OrdinalIgnoreCase);
    }
    public static bool Baked(VideoSnapshot snapshot) => Full(H3Presets.Key(snapshot.Shot)) || IsBakedFile(snapshot.Settings.Model);
    public const string RefinementIssue = "Use as motion reference to generate a fresh Standard final. Standard partial-denoise refinement is unavailable for this accelerated checkpoint.";
    public const string ExtensionIssue = "Extending continues a Standard generation. Use as motion reference to make a Standard final of this take first, then extend that.";
    public static VideoSnapshot Effective(VideoSnapshot snapshot)
        => Full(H3Presets.Key(snapshot.Shot)) ? snapshot with { Settings = snapshot.Settings with { Model = Model(snapshot.Settings, H3Presets.Key(snapshot.Shot)) } } : snapshot;
    public static Dictionary<string, object> FastInputs(H3Settings settings)
    {
        var inputs = Inputs(settings);
        inputs["model"] = Model(settings, Fast); inputs["checkpoint_sha256"] = FastHash; inputs["dense_blocks"] = "";
        inputs.Remove("embedded_turbo"); inputs["distillation"] = "FastH3 OmniRef";
        inputs["compatibility"] = "Native ComfyUI OmniRef INT8 ConvRot checkpoint, VSA keep 10%, shifts 12/3. T2V/FL2VA FastH3 checkpoints are incompatible.";
        return inputs;
    }
    public static Dictionary<string, object> Inputs(H3Settings settings) => new() {
        ["kind"] = "complete-checkpoint", ["model"] = Model(settings), ["checkpoint_sha256"] = CompactHash,
        ["embedded_turbo"] = true, ["additional_acceleration_adapter"] = false,
        ["steps"] = 8, ["sampler"] = "euler", ["scheduler"] = "simple", ["shift_video"] = 12.0, ["shift_audio"] = 3.0,
        ["attention"] = "comfy kitchen attention", ["selection"] = "vsa", ["selection.keep_percent"] = 10.0,
        ["start_percent"] = 0.0, ["end_percent"] = 1.0, ["dense_blocks"] = "0,1", ["min_tokens"] = 12288,
        ["extra_tokens"] = 256, ["sink_conditioning"] = "exact_kv_and_rows", ["verbose"] = false,
        ["compatibility"] = "Native W4/W6/INT8 support; Beta7 Compact only. Coarse VSA gates are absent; actual kernel execution is not inferred."
    };
    public static string Apply(string model, Action<string, string, object> node, string key = Compact)
    {
        var prefix = key == Fast ? "fasth3" : "compact";
        node("18", "MiniMaxH3SigmaShift", new { model = new object[] { model, 0 }, shift_video = 12.0, shift_audio = 3.0 });
        node(prefix + "-vsa", "BlockSparseAttention", new Dictionary<string, object> {
            ["model"] = new object[] { "18", 0 }, ["selection"] = "vsa", ["selection.keep_percent"] = 10.0,
            ["start_percent"] = 0.0, ["end_percent"] = 1.0, ["dense_blocks"] = key == Fast ? "" : "0,1", ["min_tokens"] = 12288,
            ["extra_tokens"] = 256, ["sink_conditioning"] = "exact_kv_and_rows", ["verbose"] = false
        });
        node(prefix + "-attention", "ModelAttentionBackend", new { model = new object[] { prefix + "-vsa", 0 }, attention = "comfy kitchen attention" });
        return prefix + "-attention";
    }
    public static H3PresetSetup Inspect(JsonElement root, H3Settings settings, string? baseIssue, string key = Compact)
    {
        var files = ComfyH3Video.Options(root, "UNETLoader", "unet_name").Where(f => Matches(f, key)).ToArray();
        var model = Model(settings, key);
        var issue = baseIssue ?? (!Matches(model, key) || !files.Contains(model) ? "Select the installed checkpoint listed for this preset." : null);
        issue ??= H3Presets.ContractIssue(root, "MiniMaxH3SigmaShift", new() { ["model"] = new object[] { "1", 0 }, ["shift_video"] = 12.0, ["shift_audio"] = 3.0 }, ["MODEL"]);
        issue ??= H3Presets.ContractIssue(root, "ModelAttentionBackend", new() { ["model"] = new object[] { "1", 0 }, ["attention"] = "comfy kitchen attention" }, ["MODEL"]);
        issue ??= SparseContractIssue(root, key);
        if (issue is null && !ComfyH3Video.Options(root, "KSamplerSelect", "sampler_name").Contains("euler")) issue = "Update ComfyUI for Euler sampling.";
        return new(key, issue, files);
    }
    private static string? SparseContractIssue(JsonElement root, string key)
    {
        var option = ComfyH3Video.DynamicOption(ComfyH3Video.Input(root, "BlockSparseAttention", "selection"), "vsa");
        if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("inputs", out var nested) || !nested.TryGetProperty("required", out var fields))
            return "Update ComfyUI for native VSA attention, then refresh Video models.";
        // Flatten the selected dynamic branch exactly as the submitted API graph does.
        var normalized = JsonNode.Parse(root.GetRawText())!;
        var required = normalized["BlockSparseAttention"]!["input"]!["required"]!.AsObject();
        required["selection"] = new JsonArray(new JsonArray("vsa"));
        foreach (var field in fields.EnumerateObject()) required["selection." + field.Name] = JsonNode.Parse(field.Value.GetRawText());
        Dictionary<string, object>? values = null;
        Apply("1", (id, _, inputs) => { if (id.EndsWith("-vsa", StringComparison.Ordinal)) values = (Dictionary<string, object>)inputs; }, key);
        return H3Presets.ContractIssue(JsonSerializer.SerializeToElement(normalized), "BlockSparseAttention", values!, ["MODEL"]);
    }
    public static void ValidateIdentity(VideoSnapshot snapshot, H3GenerationRecipe recipe)
    {
        var key = H3Presets.Key(snapshot.Shot);
        if (!Full(key)) {
            if (recipe.ProbeAvailable && recipe.Models.FirstOrDefault(m => m.Category == "diffusion_models")?.Sha256 is CompactHash or FastHash)
                throw new WorkspaceStoreException("This complete checkpoint has embedded acceleration. Choose its dedicated preset and capture its supported recipe.");
            return;
        }
        if (snapshot.Settings.Model != Model(snapshot.Settings, key) || !Matches(snapshot.Settings.Model, key))
            throw new WorkspaceStoreException("This preset must use its separately selected complete checkpoint.");
        if (recipe.ProbeAvailable && recipe.Models.FirstOrDefault(m => m.Category == "diffusion_models")?.Sha256 != (key == Fast ? FastHash : CompactHash))
            throw new WorkspaceStoreException("This file does not match the preset's published checkpoint. Select the listed weights and capture a new request.");
    }
}
