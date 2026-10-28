using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class VisualStyles
{
    public static string Label(VisualStylePreset preset) => preset switch {
        VisualStylePreset.Photographic => "Photographic", VisualStylePreset.Anime => "Anime",
        VisualStylePreset.Illustration => "Illustration", VisualStylePreset.ThreeDimensional => "3D",
        VisualStylePreset.Custom => "Custom", _ => "Not specified"
    };
    public static void Validate(VisualStyle? style)
    {
        if (style is not null && (!Enum.IsDefined(style.Preset) || style.Direction is null || style.Direction.Length > 4000))
            throw new WorkspaceStoreException("Choose a supported visual style and use up to 4,000 characters of visual direction.");
    }
    public static VisualStyle Normalize(VisualStyle style) { Validate(style); return style with { Direction = style.Preset == VisualStylePreset.Unspecified ? "" : style.Direction.Trim() }; }
    public static VisualStyle? Context(VisualStyle? style) => Brief(style).Length == 0 ? null : style;
    public static bool Valid(VisualStyle? style) => style is null || Enum.IsDefined(style.Preset) && style.Direction is not null && style.Direction.Length <= 4000;
    public static VisualStyle Resolve(VisualStyle? project, VisualStyle? asset = null, VisualStyle? request = null) => Normalize(request ?? asset ?? project ?? new());
    public static async Task<VisualStyle> CaptureAsync(Guid project, ReferenceAsset asset, VisualStyle? request,
        IProjectAiPreferencesStore? preferences, CancellationToken ct = default) => request is not null ? Normalize(request)
        : Resolve(preferences is null ? null : (await preferences.LoadAsync(project, ct)).VisualStyle, asset.VisualStyle);
    public static string Brief(VisualStyle? style)
    {
        if (style is null) return "";
        style = Normalize(style);
        var medium = style.Preset switch {
            VisualStylePreset.Photographic => "Photographic imagery with natural photographic detail and coherent lighting.",
            VisualStylePreset.Anime => "Anime rendering with deliberate linework, coherent character design and cel shading.",
            VisualStylePreset.Illustration => "Drawn or painted illustration with a consistent illustrative finish.",
            VisualStylePreset.ThreeDimensional => "3D rendered imagery with consistent materials, forms and lighting.",
            _ => ""
        };
        return string.Join("\n", new[] { medium, style.Direction }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
    public static string Apply(string prompt, VisualStyle? style)
    {
        var brief = Brief(style);
        if (brief.Length == 0) return prompt;
        var direction = "Visual style direction: " + brief + "\nKeep this visual treatment consistent while preserving the requested identity, content and framing.";
        var summary = prompt.IndexOf("summary:", StringComparison.OrdinalIgnoreCase);
        if (summary >= 0 && prompt.Contains("subject_definitions:", StringComparison.OrdinalIgnoreCase)) return prompt.Insert(summary + "summary:".Length, "\n" + direction + "\n");
        var description = prompt.IndexOf("[Description]", StringComparison.OrdinalIgnoreCase);
        return description >= 0 ? prompt.Insert(description + "[Description]".Length, "\n" + direction + "\n") : prompt.TrimEnd() + "\n\n" + direction;
    }
    public static string ApplyImage(string prompt, VisualStyle? style, ImageWorkflow workflow)
    {
        if (Brief(style).Length == 0) return prompt;
        if (workflow == ImageWorkflow.Fibo2 && prompt.TrimStart().StartsWith('{')) {
            try {
                if (System.Text.Json.Nodes.JsonNode.Parse(prompt) is System.Text.Json.Nodes.JsonObject caption) {
                    caption["artistic_style"] = string.Join("\n", new[] { caption["artistic_style"]?.ToString(), Brief(style) }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    return caption.ToJsonString();
                }
            } catch (System.Text.Json.JsonException) { /* The workflow's normal caption validation reports malformed JSON. */ }
        }
        return Apply(prompt, style);
    }
}
