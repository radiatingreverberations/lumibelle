using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3Performance
{
    public const string Version = "h3-performance-v1";
    // KJNodes' public node ID deliberately retains its upstream spelling.
    public const string SageNode = "PathchSageAttentionKJ";
    public static H3PerformanceProfile Legacy { get; } = Capture(new() { ArchiveCompression = H3ArchiveCompression.Compact });
    public static string Label(H3AttentionBackend backend) => backend switch
    {
        H3AttentionBackend.ServerDefault => "Server default", H3AttentionBackend.PyTorch => "PyTorch",
        H3AttentionBackend.Kitchen => "Comfy Kitchen", H3AttentionBackend.Sage => "SageAttention", _ => "Unknown"
    };
    public static H3PerformanceProfile Capture(H3PerformancePreferences p)
    {
        Validate(p);
        return new(Version, p.Attention, p.Attention switch
        {
            H3AttentionBackend.ServerDefault => "server-default", H3AttentionBackend.Sage => SageNode, _ => "ModelAttentionBackend"
        }, p.SolAttention, 1.3, .2, 1, 12288, 256, "", "exact_kv_and_rows",
            p.ArchiveCompression == H3ArchiveCompression.Fast ? "fastest" : "default", 80);
    }
    public static H3PerformancePreferences Preferences(H3PerformanceProfile? profile)
    {
        var p = profile ?? Legacy;
        return new() { Attention = p.Attention, SolAttention = p.SolAttention,
            ArchiveCompression = p.ArchiveMethod == "fastest" ? H3ArchiveCompression.Fast : H3ArchiveCompression.Compact };
    }
    public static void Validate(H3PerformancePreferences? p)
    {
        if (p is null || !Enum.IsDefined(p.Attention) || !Enum.IsDefined(p.ArchiveCompression))
            throw new WorkspaceStoreException("Choose supported video performance options.");
    }
    public static void Validate(H3PerformanceProfile? p)
    {
        if (p is not null && p != Capture(Preferences(p)))
            throw new WorkspaceStoreException("The captured H3 performance profile is invalid or unsupported. Start a new batch.");
    }
    public static string? Issue(H3PerformanceCapabilities c, H3PerformanceProfile p) =>
        (p.Attention switch { H3AttentionBackend.PyTorch => c.PyTorchIssue, H3AttentionBackend.Kitchen => c.KitchenIssue,
            H3AttentionBackend.Sage => c.SageIssue, _ => null }) ??
        (p.SolAttention ? c.SolIssue : null) ?? (p.ArchiveMethod == "fastest" ? c.FastArchiveIssue : null);

    public static H3PerformanceCapabilities Inspect(JsonElement root)
    {
        JsonElement Input(string node, string field) => ComfyH3Video.Input(root, node, field);
        bool Type(JsonElement input, string type) => input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0 && input[0].ValueKind == JsonValueKind.String && input[0].GetString() == type;
        bool Output(string node) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(node, out var n) && n.ValueKind == JsonValueKind.Object && n.TryGetProperty("output", out var o) &&
            o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == 1 && o[0].ValueKind == JsonValueKind.String && o[0].GetString() == "MODEL";
        bool Range(JsonElement input, string type, double value) => Type(input, type) && input.GetArrayLength() > 1 &&
            input[1].ValueKind == JsonValueKind.Object && input[1].TryGetProperty("min", out var lo) && lo.ValueKind == JsonValueKind.Number && lo.TryGetDouble(out var min) && min <= value &&
            input[1].TryGetProperty("max", out var hi) && hi.ValueKind == JsonValueKind.Number && hi.TryGetDouble(out var max) && max >= value;
        bool Backend(string option) => Type(Input("ModelAttentionBackend", "model"), "MODEL") && Output("ModelAttentionBackend") &&
            ComfyH3Video.Options(root, "ModelAttentionBackend", "attention").Contains(option);
        var sol = "BlockSparseAttention";
        var selection = Input(sol, "selection"); var tau = default(JsonElement);
        if (selection.ValueKind == JsonValueKind.Array && selection.GetArrayLength() > 1 && selection[1].ValueKind == JsonValueKind.Object && selection[1].TryGetProperty("options", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
                if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() == "Sol-Attn (adaptive tau)" && choice.TryGetProperty("inputs", out var inputs) &&
                    inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Object && required.TryGetProperty("tau", out var field)) tau = field;
        }
        var solReady = Output(sol) && Type(Input(sol, "model"), "MODEL") && Range(tau, "FLOAT", 1.3) &&
            Range(Input(sol, "start_percent"), "FLOAT", .2) && Range(Input(sol, "end_percent"), "FLOAT", 1) &&
            Range(Input(sol, "min_tokens"), "INT", 12288) && Range(Input(sol, "extra_tokens"), "INT", 256) &&
            Type(Input(sol, "dense_blocks"), "STRING") && Type(Input(sol, "verbose"), "BOOLEAN") &&
            ComfyH3Video.Options(root, sol, "sink_conditioning").Contains("exact_kv_and_rows");
        return new()
        {
            PyTorchIssue = Backend("pytorch attention") ? null : "Update ComfyUI to use the native PyTorch attention selector.",
            KitchenIssue = Backend("comfy kitchen attention") ? null : "Comfy Kitchen attention is unavailable. Update ComfyUI and its matching comfy-kitchen dependency.",
            SageIssue = Type(Input(SageNode, "model"), "MODEL") && Output(SageNode) && Type(Input(SageNode, "allow_compile"), "BOOLEAN") &&
                ComfyH3Video.Options(root, SageNode, "sage_attention").Contains("auto") ? null :
                "Explicit Sage requires KJNodes' Patch Sage Attention and the sageattention package. Install them in ComfyUI, restart, and refresh. A server launched with --use-sage-attention can keep Server default.",
            SolIssue = solReady ? null : "Update ComfyUI and comfy-kitchen for native Block Sparse Attention with the supported Sol-Attn inputs.",
            FastArchiveIssue = ComfyH3Video.Options(root, "SaveAnimatedWEBP", "method").Contains("fastest") ? null : "Update ComfyUI for fast lossless WebP compression, or choose Compact."
        };
    }

    internal static string Apply(H3PerformanceProfile p, string model, Action<string, string, object> node, string prefix = "")
    {
        object Link(string id) => new object[] { id, 0 };
        if (p.Attention != H3AttentionBackend.ServerDefault)
        {
            if (p.Attention == H3AttentionBackend.Sage)
                node(prefix + "30", SageNode, new { model = Link(model), sage_attention = "auto", allow_compile = false });
            else node(prefix + "30", "ModelAttentionBackend", new { model = Link(model), attention = p.Attention == H3AttentionBackend.Kitchen ? "comfy kitchen attention" : "pytorch attention" });
            model = prefix + "30";
        }
        if (p.SolAttention)
        {
            node(prefix + "31", "BlockSparseAttention", new Dictionary<string, object>
            {
                ["model"] = Link(model), ["selection"] = "Sol-Attn (adaptive tau)", ["selection.tau"] = p.Tau,
                ["start_percent"] = p.StartPercent, ["end_percent"] = p.EndPercent, ["min_tokens"] = p.MinimumTokens,
                ["extra_tokens"] = p.ExtraTokens, ["dense_blocks"] = p.DenseBlocks, ["sink_conditioning"] = p.SinkConditioning,
                // Diagnostics distinguish sparse execution from the node's intentional dense paths.
                ["verbose"] = true
            });
            model = prefix + "31";
        }
        return model;
    }
}
