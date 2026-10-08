using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3PreviewUpscaling
{
    public const string Version = "h3-preview-upscale-v1";
    public const string Node = "MinimaxH3LatentUpscaler3D";
    public static H3PreviewUpscaleProfile Capture(H3UpscalerImplementation implementation, string checkpoint, string aspect)
    {
        var size = H3Policy.Size(aspect, true);
        return new(Version, implementation, checkpoint, size.Width, size.Height, "cuda", "fp16", 32, true,
            implementation == H3UpscalerImplementation.Lbh, false);
    }
    public static (int Width, int Height) OutputSize(VideoSnapshot s, TakeRefinement? refinement = null) =>
        refinement is { } r ? (r.Width, r.Height) : s.PreviewUpscale is { } p ? (p.Width, p.Height) : (s.Width, s.Height);

    public static void Validate(VideoSnapshot s)
    {
        if (!s.Shot.UpscalePreview)
        {
            if (s.PreviewUpscale is not null) throw new WorkspaceStoreException("Unexpected preview upscaling profile.");
            return;
        }
        var p = s.PreviewUpscale;
        if (p is null || !Enum.IsDefined(p.Implementation) || s.Shot.NativeResolution || s.CaptureRefinementData ||
            string.IsNullOrWhiteSpace(p.Checkpoint) || !p.Checkpoint.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            p.Checkpoint != s.Settings.LatentUpscaler || p != Capture(p.Implementation, p.Checkpoint, s.Shot.Aspect) ||
            (s.Width, s.Height) != H3Policy.Size(s.Shot.Aspect, false))
            throw new WorkspaceStoreException("The captured preview upscaling profile is invalid. Start a new batch.");
    }
    public static void CheckSubmission(VideoSnapshot s, H3Configuration configuration)
    {
        Validate(s);
        if (s.PreviewUpscale is not { } p) return;
        if (configuration.PreviewUpscaling.Issue is { } issue) throw new WorkspaceStoreException(issue);
        if (configuration.PreviewUpscaling.Implementation != p.Implementation)
            throw new WorkspaceStoreException("The installed preview upscaler changed after this batch was queued. Restore its captured implementation or start a new batch.");
    }

    public static H3PreviewUpscaleCapability Inspect(JsonElement root, H3Settings settings)
    {
        JsonElement Input(string node, string field) => ComfyH3Video.Input(root, node, field);
        bool Type(JsonElement input, params string[] types) => input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0 &&
            input[0].ValueKind == JsonValueKind.String && types.Contains(input[0].GetString());
        bool Outputs(string node, params string[] outputs) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(node, out var n) &&
            n.ValueKind == JsonValueKind.Object && n.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == outputs.Length &&
            o.EnumerateArray().Select((v, i) => v.ValueKind == JsonValueKind.String && (v.GetString() == outputs[i] || node == Node && v.GetString() == "*")).All(x => x) &&
            (!n.TryGetProperty("output_is_list", out var lists) || lists.ValueKind == JsonValueKind.Array && lists.GetArrayLength() == outputs.Length && lists.EnumerateArray().All(v => v.ValueKind == JsonValueKind.False));
        bool Range(JsonElement input, int value) => Type(input, "INT") && input.GetArrayLength() > 1 && input[1].ValueKind == JsonValueKind.Object &&
            input[1].TryGetProperty("min", out var lo) && lo.ValueKind == JsonValueKind.Number && lo.TryGetInt32(out var min) && min <= value &&
            input[1].TryGetProperty("max", out var hi) && hi.ValueKind == JsonValueKind.Number && hi.TryGetInt32(out var max) && max >= value &&
            (!input[1].TryGetProperty("step", out var step) || step.ValueKind == JsonValueKind.Number && step.TryGetInt32(out var stride) && stride > 0 && value % stride == 0);
        bool KnownInputs(string node, params string[] known) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(node, out var n) && n.ValueKind == JsonValueKind.Object &&
            n.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("required", out var fields) &&
            fields.ValueKind == JsonValueKind.Object && fields.EnumerateObject().All(p => known.Contains(p.Name));
        bool Flag(string name) => Type(Input(Node, name), "BOOLEAN");
        const string setup = "Install one supported learned 3D latent upscaler pack (LBH or Plus), restart ComfyUI, then check again in Video models.";
        if (!Type(Input("LTXVSeparateAVLatent", "av_latent"), "LATENT") || !Outputs("LTXVSeparateAVLatent", "LATENT", "LATENT") ||
            !KnownInputs("LTXVSeparateAVLatent", "av_latent") ||
            !Type(Input("LTXVConcatAVLatent", "video_latent"), "LATENT") || !Type(Input("LTXVConcatAVLatent", "audio_latent"), "LATENT") ||
            !Outputs("LTXVConcatAVLatent", "LATENT") || !KnownInputs("LTXVConcatAVLatent", "video_latent", "audio_latent"))
            return new(null, "Update ComfyUI for the native audio/video latent separation and combination nodes, then refresh Video models.");
        var lbh = Flag("enable_temporal_chunking") && Flag("force_unload") && Input(Node, "keep_proportion").ValueKind == JsonValueKind.Undefined && Input(Node, "offload_after_upscale").ValueKind == JsonValueKind.Undefined;
        var plus = Flag("keep_proportion") && Flag("offload_after_upscale") && Input(Node, "enable_temporal_chunking").ValueKind == JsonValueKind.Undefined && Input(Node, "force_unload").ValueKind == JsonValueKind.Undefined;
        if (lbh == plus || !Type(Input(Node, "latent"), "LATENT", "*") || !Outputs(Node, "LATENT") || !Range(Input(Node, "align"), 32) ||
            !KnownInputs(Node, "latent", "model_name", "mode", "align", "device", "precision", lbh ? "enable_temporal_chunking" : "keep_proportion", lbh ? "force_unload" : "offload_after_upscale") ||
            !ComfyH3Video.Options(root, Node, "device").Contains("cuda") || !ComfyH3Video.Options(root, Node, "precision").Contains("fp16")) return new(null, setup);
        if (!Type(Input(Node, "mode"), "COMFY_DYNAMICCOMBO_V3")) return new(null, setup);
        var mode = ComfyH3Video.DynamicOption(Input(Node, "mode"), "target dimensions");
        if (mode.ValueKind != JsonValueKind.Object || !mode.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Object || required.EnumerateObject().Any(p => p.Name is not ("width" or "height")) ||
            !required.TryGetProperty("width", out var width) || !required.TryGetProperty("height", out var height) ||
            new[] { 768, 992, 1344 }.Any(value => !Range(width, value) || !Range(height, value))) return new(null, setup);
        var implementation = lbh ? H3UpscalerImplementation.Lbh : H3UpscalerImplementation.Plus;
        if (string.IsNullOrWhiteSpace(settings.LatentUpscaler) || !settings.LatentUpscaler.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            !ComfyH3Video.Options(root, Node, "model_name").Contains(settings.LatentUpscaler))
            return new(implementation, "Choose an installed learned 3D upscaler checkpoint under Upscaled preview in Video models, then save.");
        return new(implementation, null);
    }

    // The upscaler node's inputs for the installed implementation; take refinement uses them too.
    internal static Dictionary<string, object> Inputs(H3PreviewUpscaleProfile p, object latent)
    {
        var values = new Dictionary<string, object> { ["latent"] = latent, ["model_name"] = p.Checkpoint,
            ["mode"] = "target dimensions", ["mode.width"] = p.Width, ["mode.height"] = p.Height,
            ["align"] = p.Align, ["device"] = p.Device, ["precision"] = p.Precision };
        if (p.Implementation == H3UpscalerImplementation.Lbh) { values["enable_temporal_chunking"] = p.TemporalChunking; values["force_unload"] = p.Offload; }
        else { values["keep_proportion"] = p.KeepProportion; values["offload_after_upscale"] = p.Offload; }
        return values;
    }
    internal static string Apply(VideoSnapshot s, Action<string, string, object> node)
    {
        Validate(s);
        if (s.PreviewUpscale is not { } p) return "10";
        object Link(string id, int port = 0) => new object[] { id, port };
        node("40", "LTXVSeparateAVLatent", new { av_latent = Link("10", 1) });
        node("41", Node, Inputs(p, Link("40")));
        node("42", "LTXVConcatAVLatent", new { video_latent = Link("41"), audio_latent = Link("40", 1) });
        return "42";
    }
}
