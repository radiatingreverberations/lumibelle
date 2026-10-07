using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3Presets
{
    public const string Beta = "beta", EulerBeta = "euler-beta", TurboLight = "turbo4-075";
    public const double TurboLightStrength = 0.75;
    public static readonly string[] Keys = ["standard", Beta, EulerBeta, "larry", "pdd", "spectrum", "turbo4", TurboLight, "turbo8", H3HyperFlow.Key];
    // Retired presets are no longer offered for new choices, but stay valid so shots, named setups
    // and queued requests that captured them keep generating exactly as before.
    public static bool Retired(string key) => key is "spectrum" or "turbo8" or TurboLight;
    public static IEnumerable<string> Offered => Keys.Where(k => !Retired(k));
    // The offered presets, plus the current one when it has been retired so pickers still show it.
    public static IEnumerable<string> Choices(string current) => Keys.Where(k => !Retired(k) || k == current);
    public static string ChoiceLabel(string key) => Label(key) + (Retired(key) ? " · Retired" : Experimental(key) ? " · Experimental" : "");
    public const string LarryCheckpoint = "minimax_h3_turbo_v4_step600_ema.safetensors";
    public const string PddCheckpoint = "MiniMax-H3-Ref2VA-Acc-8Step.safetensors";
    public static string Key(Shot shot) => shot.GenerationPreset ?? (shot.Turbo ? shot.TurboSteps == 8 ? "turbo8" : "turbo4" : "standard");
    public static bool Experimental(string key) => key is "larry" or "pdd" or "spectrum" or H3HyperFlow.Key or Beta or EulerBeta or TurboLight;
    // Presets in the 11 September comparison, which ran on the base Ref2VA model.
    public static bool Compared(string key) => key is "larry" or "pdd" or "spectrum" or "turbo4" or "turbo8";
    public static bool UsesTurboLora(string key) => key is "turbo4" or "turbo8" or TurboLight;
    public static string Label(string key) => key switch
    {
        "standard" => "Standard · 20 steps", Beta => "Beta · 20 steps", EulerBeta => "Euler beta · 20 steps",
        "larry" => "Larry · 6 steps", "pdd" => "PDD · 8 steps", "spectrum" => "Spectrum · Standard 20 steps",
        "turbo4" => "Turbo · 4 steps", TurboLight => "Turbo · 4 steps · 0.75", "turbo8" => "Turbo · 8 steps",
        H3HyperFlow.Key => "HyperFlow · 8 steps", _ => "Unknown preset"
    };
    public static string Help(string key) => key switch
    {
        H3HyperFlow.Key => "Experimental ComfyUI conversion, not the upstream two-time loader. Eight Euler evaluations on a fixed manual sigma grid; shifts 12/3, strength 1, no CFG pass. Not yet benchmarked in Lumibelle.",
        Beta => "Standard with the beta scheduler (res_multistep · beta). Suggested for reference-heavy prompts. Not yet benchmarked in Lumibelle.",
        EulerBeta => "Euler with the beta scheduler. Community testing preferred it to Standard for reference adherence, with speedups enabled. Not yet benchmarked in Lumibelle.",
        TurboLight => "Retired. The 4-step Turbo LoRA at strength 0.75. Never benchmarked in Lumibelle, and the Singularity model card gives no strength for it. Use Turbo · 4 steps instead.",
        "larry" => "Experimental. Both clips looked acceptable in our small single-seed comparison on the base model; quality and speed depend on your setup.",
        "pdd" => "Experimental. Good motion in our base-model comparison, but dialogue was strongly distorted.",
        "spectrum" => "Retired. Dialogue was acceptable on the base model, but the motion scene had distorted interiors. Replay protects audio and uses system RAM.",
        "turbo8" => "Retired. Dialogue was acceptable in our base-model comparison; the motion scene had distorted interiors and it was slower than Turbo · 4 steps.",
        "turbo4" => "The fastest preset. Dialogue was acceptable in our base-model comparison; the motion scene had distorted interiors.",
        _ => "The original 20-step recipe. A useful control when comparing accelerated presets."
    };
    public static string? Checkpoint(string key, H3Settings s) => key switch
    { "larry" => s.LarryLora ?? LarryCheckpoint, "pdd" => s.PddCheckpoint ?? PddCheckpoint, "turbo4" or TurboLight => s.TurboLora, "turbo8" => s.Turbo8StepLora, H3HyperFlow.Key => H3HyperFlow.Checkpoint(s), _ => null };
    public static string Node(string key) => key switch
    { "larry" => "MiniMaxH3TurboLoRA", "pdd" => "MiniMaxH3PDDAccApply", "spectrum" => "SpectrumApplyMiniMaxH3", _ => "" };
    public static Dictionary<string, object> Inputs(string key, H3Settings settings) => key switch
    {
        H3HyperFlow.Key => H3HyperFlow.Inputs(settings),
        // Native nodes only; captured so a queued request cannot pick up a changed recipe.
        Beta => new() { ["sampler"] = "res_multistep", ["scheduler"] = "beta", ["steps"] = 20 },
        EulerBeta => new() { ["sampler"] = "euler", ["scheduler"] = "beta", ["steps"] = 20 },
        TurboLight => new() { ["lora_name"] = settings.TurboLora, ["strength_model"] = TurboLightStrength, ["sampler"] = "res_multistep", ["scheduler"] = "simple", ["steps"] = 4 },
        "larry" => new() { ["lora_name"] = Checkpoint(key, settings)!, ["strength"] = 1.0, ["low_vram"] = false },
        "pdd" => new() { ["pdd_file"] = Checkpoint(key, settings)!, ["nfe"] = "8", ["lora_strength"] = 1.0, ["head_strength"] = 1.0,
            ["on_off_grid"] = "error", ["partition"] = "", ["enabled"] = true, ["partition_check"] = "error" },
        // Frozen from the successful 455bd35 benchmark graph. Never inherit live defaults.
        "spectrum" => new() {
            ["enabled"] = true, ["blend_weight"] = .5, ["degree"] = 1, ["ridge_lambda"] = .1, ["window_size"] = 2.0,
            ["flex_window"] = .75, ["warmup_steps"] = 1, ["tail_actual_steps"] = 1, ["max_history"] = 8, ["debug"] = true,
            ["history_storage"] = "system_ram", ["bootstrap_first_forecast"] = true, ["anchor_residual_feedback"] = false,
            ["selective_rollback_correction"] = false, ["offline_smoothing_replay"] = true, ["audio_blend_weight"] = 0.0,
            ["offline_archive_storage"] = "system_ram", ["model_aware_mode"] = "off", ["model_aware_risk_threshold"] = .65,
            ["model_aware_trust_shrinkage"] = false, ["model_aware_replay_generic_correction"] = false,
            ["generic_correction_mode"] = "coordinate_rls", ["generic_correction_limiter"] = "hard_clip",
            ["generic_correction_limit"] = .4, ["generic_correction_attenuation"] = "no_attenuation", ["sa_pece_forecast_policy"] = "balanced" },
        _ => new()
    };
    public static H3PresetProfile Capture(Shot shot, H3Settings settings)
    {
        var key = Key(shot);
        if (!Keys.Contains(key)) throw new WorkspaceStoreException("Choose a supported generation preset.");
        if (key == H3HyperFlow.Key && H3HyperFlow.FileIssue(H3HyperFlow.Checkpoint(settings)) is { } issue)
            throw new WorkspaceStoreException(issue);
        return new("h3-presets-v1", key, Checkpoint(key, settings), JsonSerializer.SerializeToElement(Inputs(key, settings)));
    }
    public static H3PerformancePreferences NewPerformance(H3Settings settings) => new()
    { Attention = settings.Performance.Attention, SolAttention = false, ArchiveCompression = H3ArchiveCompression.Fast };
    public static void Validate(VideoSnapshot snapshot)
    {
        if (snapshot.Preset is null)
        {
            if (Experimental(Key(snapshot.Shot)) || snapshot.OutputPolicy is not null)
                throw new WorkspaceStoreException("This video request is missing its captured preset.");
            return;
        }
        var expected = Capture(snapshot.Shot, snapshot.Settings);
        var actual = snapshot.Preset;
        if (actual.Version != expected.Version || actual.Key != expected.Key || actual.Checkpoint != expected.Checkpoint ||
            !JsonElement.DeepEquals(actual.Inputs, expected.Inputs) || snapshot.OutputPolicy is null ||
            snapshot.OutputPolicy.SaveLosslessFrames != snapshot.Shot.SaveLosslessFrames ||
            snapshot.Performance != H3Performance.Capture(NewPerformance(snapshot.Settings)))
            throw new WorkspaceStoreException("The captured video preset or output policy is invalid. Start a new batch.");
    }
    public static string? Issue(H3Configuration c, Shot shot)
    {
        var key = Key(shot);
        var setup = c.Presets.FirstOrDefault(p => p.Key == key);
        var issue = setup?.Issue ?? (setup is not null ? null : key switch {
            "standard" => c.StandardReady ? null : c.Message,
            "turbo4" => c.TurboReady ? null : c.TurboIssue ?? c.Message,
            "turbo8" => c.Turbo8StepReady ? null : c.Turbo8StepIssue ?? c.Message,
            _ => "Refresh Video models to check this preset." });
        return c.AttentionIssue ?? issue ?? (shot.SaveLosslessFrames ? c.ArchiveIssue ?? c.Performance.FastArchiveIssue : null)
            ?? (shot.UpscalePreview ? c.PreviewUpscaling.Issue ?? (c.PreviewUpscaling.Implementation is null ? "Refresh preview upscaling setup." : null) : null);
    }
    public static void CheckSubmission(VideoSnapshot s, H3Configuration c)
    {
        Validate(s);
        string? issue;
        if (s.Preset is not null) issue = Issue(c, s.Shot);
        else issue = c.SelectedPerformanceIssue ?? c.ArchiveIssue ?? (s.Shot.Turbo
            ? s.Shot.TurboSteps == 8 ? c.Turbo8StepReady ? null : c.Turbo8StepIssue ?? c.Message : c.TurboReady ? null : c.TurboIssue ?? c.Message
            : c.StandardReady ? null : c.Message);
        if (issue is not null) throw new WorkspaceStoreException(issue);
    }
    public static IReadOnlyList<H3PresetSetup> Inspect(JsonElement root, H3Settings settings, string? baseIssue, string? standard, string? turbo4, string? turbo8)
    {
        return Keys.Select(key => {
            if (key == H3HyperFlow.Key) return H3HyperFlow.Inspect(root, settings, baseIssue);
            if (key is Beta or EulerBeta)
            {
                var sampler = key == Beta ? "res_multistep" : "euler";
                return new H3PresetSetup(key, baseIssue ??
                    (!ComfyH3Video.Options(root, "KSamplerSelect", "sampler_name").Contains(sampler) ? $"Update ComfyUI: missing {sampler} sampler." : null) ??
                    (!ComfyH3Video.Options(root, "BasicScheduler", "scheduler").Contains("beta") ? "Update ComfyUI: missing beta scheduler." : null), []);
            }
            if (key == TurboLight) return new H3PresetSetup(key, turbo4, ComfyH3Video.Options(root, "LoraLoaderModelOnly", "lora_name"));
            var node = Node(key);
            var files = key switch {
                "larry" => ComfyH3Video.Options(root, node, "lora_name"), "pdd" => ComfyH3Video.Options(root, node, "pdd_file"),
                "turbo4" or "turbo8" => ComfyH3Video.Options(root, "LoraLoaderModelOnly", "lora_name"), _ => [] };
            var issue = key switch { "standard" => standard, "turbo4" => turbo4, "turbo8" => turbo8, _ => baseIssue };
            if (Experimental(key))
            {
                var values = Inputs(key, settings); values["model"] = new object[] { "1", 0 };
                issue ??= ContractIssue(root, node, values, key == "pdd" ? ["MODEL", "SIGMAS", "STRING"] : ["MODEL"]);
                if (key == "larry") issue ??= ContractIssue(root, "MiniMaxH3TurboSampler", new(), ["SAMPLER"]);
                if (key == "pdd")
                {
                    issue ??= ContractIssue(root, "MiniMaxH3SigmaShift", new() { ["model"] = new object[] { "1", 0 }, ["shift_video"] = 12.0, ["shift_audio"] = 3.0 }, ["MODEL"]);
                    if (!ComfyH3Video.Options(root, "KSamplerSelect", "sampler_name").Contains("euler")) issue ??= "PDD requires the Euler sampler.";
                    if (Checkpoint(key, settings)!.Contains("fl2va", StringComparison.OrdinalIgnoreCase)) issue ??= "Choose the Ref2VA PDD checkpoint, not FL2VA.";
                }
                if (key == "spectrum") issue ??= standard;
            }
            return new H3PresetSetup(key, issue, files);
        }).ToArray();
    }
    internal static string? ContractIssue(JsonElement root, string node, Dictionary<string, object> values, string[] outputs)
    {
        string Error(string field) => $"{node}: missing or unsupported {field}. Install the supported package, restart ComfyUI, and refresh.";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(node, out var schema) || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("output", out var ports) || ports.ValueKind != JsonValueKind.Array ||
            !ports.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).SequenceEqual(outputs)) return Error("outputs");
        if (schema.TryGetProperty("output_is_list", out var lists) && (lists.ValueKind != JsonValueKind.Array || lists.GetArrayLength() != outputs.Length ||
            lists.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.False))) return Error("list outputs");
        if (!schema.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return Error("inputs");
        if (input.TryGetProperty("required", out var required) && (required.ValueKind != JsonValueKind.Object ||
            required.EnumerateObject().Any(p => !values.ContainsKey(p.Name)))) return Error("required inputs");
        foreach (var (field, value) in values)
        {
            var contract = ComfyH3Video.Input(root, node, field);
            if (contract.ValueKind != JsonValueKind.Array || contract.GetArrayLength() == 0) return Error(field);
            var options = ComfyH3Video.Options(root, node, field);
            if (options.Length > 0) { if (value is not string text || !options.Contains(text)) return Error(field + " selection"); continue; }
            var type = contract[0].ValueKind == JsonValueKind.String ? contract[0].GetString() : null;
            if (value is object[]) { if (type != "MODEL") return Error(field); }
            else if (value is bool) { if (type != "BOOLEAN") return Error(field); }
            else if (value is string) { if (type != "STRING") return Error(field + " selection"); }
            else
            {
                if (type != (value is int ? "INT" : "FLOAT") || contract.GetArrayLength() < 2) return Error(field);
                var bounds = contract[1]; var number = Convert.ToDouble(value);
                if (bounds.ValueKind != JsonValueKind.Object || !bounds.TryGetProperty("min", out var min) || !bounds.TryGetProperty("max", out var max) ||
                    min.ValueKind != JsonValueKind.Number || max.ValueKind != JsonValueKind.Number ||
                    !min.TryGetDouble(out var lo) || !max.TryGetDouble(out var hi) || number < lo || number > hi) return Error(field + " range");
            }
        }
        return null;
    }
}
