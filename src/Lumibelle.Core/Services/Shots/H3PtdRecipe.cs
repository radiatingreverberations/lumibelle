using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3PtdRecipe
{
    public const string Key = "ptd-refasym6";
    public const string AdapterFile = "ptd_h3_ref2va_refasymopd_6step_int8.safetensors";
    public const string AdapterHash = "FC0809CDF5171FA904D3016B066E14956D0248ED592DAF4455ECE6752AD4BF5B";
    public const string PackageRevision = "f9eb8914a9ee39d69ac097979d49e86bb423380d";
    public const string Loader = "PTDH3CompactLoader", NativeLoader = Loader, Sampler = "PTDH3CompactSampler", Sigmas = "PTDH3CompactSigmas";
    public static string Model(H3Settings s) => s.PtdBaseModel ?? H3SmdRecipe.BaseFile;
    public static string Adapter(H3Settings s) => s.PtdAdapter ?? AdapterFile;
    public static bool IsAdapter(string file) => Path.GetFileName(file.Replace('\\', '/')).Equals(AdapterFile, StringComparison.OrdinalIgnoreCase);
    public static VideoSnapshot Effective(VideoSnapshot s) => H3Presets.Key(s.Shot) == Key ? s with { Settings = s.Settings with { Model = Model(s.Settings) } } : s;
    public static Dictionary<string, object> Inputs(H3Settings s) => new() {
        ["kind"] = "per-hop-adapter", ["model"] = Model(s), ["base_sha256"] = H3SmdRecipe.BaseHash,
        ["checkpoint"] = Adapter(s), ["adapter_sha256"] = AdapterHash, ["loader"] = Loader,
        ["sampler"] = Sampler, ["scheduler"] = Sigmas, ["steps"] = 6, ["guider"] = "BasicGuider",
        ["shift_video"] = 12.0, ["shift_audio"] = 3.0, ["pdd_num_steps"] = 6, ["pdd_block_size"] = 1,
        ["kernels"] = "exact (PTD reference)", ["allocation_compiler"] = "upstream default", ["compiler_fallback"] = "upstream sampler policy",
        ["node_package_revision"] = PackageRevision
    };
    public static string Apply(string model, H3Settings s, Action<string, string, object> node)
    {
        node("ptd-base-shift", "MiniMaxH3SigmaShift", new { model = new object[] { model, 0 }, shift_video = 12.0, shift_audio = 3.0 });
        node("ptd-adapter", Loader, new { model = new object[] { "ptd-base-shift", 0 }, checkpoint = Adapter(s), kernels = "exact (PTD reference)" });
        return "ptd-adapter";
    }
    public static H3PresetSetup Inspect(JsonElement root, H3Settings s, string? baseIssue)
    {
        var files = ComfyH3Video.Options(root, NativeLoader, "checkpoint").Where(IsAdapter).ToArray();
        var issue = baseIssue ?? (!H3SmdRecipe.IsBase(Model(s)) || !ComfyH3Video.Options(root, "UNETLoader", "unet_name").Contains(Model(s))
            ? "Select the official pruned INT8 Ref2VA base for PTD." : null);
        issue ??= !IsAdapter(Adapter(s)) || !files.Contains(Adapter(s)) ? "Select the listed six-hop RefAsymOPD adapter." : null;
        issue ??= H3Presets.ContractIssue(root, NativeLoader, new() { ["model"] = new object[] { "1", 0 }, ["checkpoint"] = Adapter(s), ["kernels"] = "exact (PTD reference)" }, ["MODEL"]);
        issue ??= H3Presets.ContractIssue(root, "MiniMaxH3SigmaShift", new() { ["model"] = new object[] { "1", 0 }, ["shift_video"] = 12.0, ["shift_audio"] = 3.0 }, ["MODEL"]);
        issue ??= H3Presets.ContractIssue(root, Sampler, new() { ["model"] = new object[] { "ptd-adapter", 0 } }, ["SAMPLER"]);
        issue ??= H3Presets.ContractIssue(root, Sigmas, new() { ["model"] = new object[] { "ptd-adapter", 0 } }, ["SIGMAS"]);
        return new(Key, issue, files);
    }
    public static void ValidateIdentity(VideoSnapshot s, H3GenerationRecipe recipe, bool adapterActive = true)
    {
        if (H3Presets.Key(s.Shot) != Key) {
            if (recipe.ProbeAvailable && recipe.Models.Any(m => m.Sha256 == AdapterHash))
                throw new WorkspaceStoreException("Per-hop PTD weights require their dedicated loader, sampler and checkpoint schedule.");
            return;
        }
        if (s.Settings.Model != Model(s.Settings) || !H3SmdRecipe.IsBase(s.Settings.Model) || !IsAdapter(Adapter(s.Settings)))
            throw new WorkspaceStoreException("PTD requires its selected pruned INT8 Ref2VA base and six-hop RefAsymOPD adapter.");
        if (recipe.ProbeAvailable && (recipe.Models.FirstOrDefault(m => m.Category == "diffusion_models")?.Sha256 != H3SmdRecipe.BaseHash ||
            adapterActive && recipe.Models.FirstOrDefault(m => m.Category == "ptd" && m.FileName == Adapter(s.Settings))?.Sha256 != AdapterHash))
            throw new WorkspaceStoreException("The selected PTD files do not match the captured base/adapter recipe. Select the listed files and capture a new request.");
    }
}
