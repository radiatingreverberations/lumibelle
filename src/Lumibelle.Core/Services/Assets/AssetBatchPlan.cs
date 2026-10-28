using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public static partial class AssetBatchPlan
{
    public const int MaximumAdditionalInstructions = 6000;
    private static string Presentation(AssetCategory category)
    {
        var direction = category switch
        {
            AssetCategory.Character => "Present one character against a plain neutral studio-style background with soft, even lighting. " +
                "Keep the face, distinctive features and clothing clearly visible and unobstructed, with a natural neutral pose. " +
                "Show the requested appearance in one image, rather than multiple looks, panels or a contact sheet.",
            AssetCategory.Prop => "Present the described prop against a plain, uncluttered neutral background with soft, even lighting. " +
                "Keep the complete object inside the frame with space around it, a clear silhouette and readable materials. " +
                "Use a simple supporting surface when needed, without unrelated objects or added lettering.",
            AssetCategory.Environment => "Use a clear establishing view of the described environment, with a readable spatial layout, depth and recognizable fixed features. " +
                "Keep the setting and its appropriate lighting; leave important areas unobstructed by unrequested foreground subjects.",
            _ => ""
        };
        return direction.Length == 0 ? "" : "Presentation defaults: " + direction + " " +
            "Keep the requested rendering medium and captured visual style. These are starting defaults only: explicit asset/look direction, " +
            "requested framing, pose, background or lighting, and additional batch instructions take priority.\n\n";
    }
    public static string ImagePrompt(AssetBatchItem item)
    {
        // Enhanced text already incorporates the captured batch instructions.
        if (item.EnhancedPrompt is { } enhanced) return enhanced;
        var prompt = item.Prompt ?? "";
        if (string.IsNullOrWhiteSpace(item.AdditionalInstructions)) return prompt;
        var direction = "Additional instructions for this batch:\n" + item.AdditionalInstructions.Trim();
        // Preserve edited Fibo VGL captions instead of appending text outside their JSON object.
        if (prompt.TrimStart().StartsWith('{')) {
            try {
                if (System.Text.Json.Nodes.JsonNode.Parse(prompt) is System.Text.Json.Nodes.JsonObject caption &&
                    caption["short_description"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var description)) {
                    caption["short_description"] = description.TrimEnd() + "\n\n" + direction;
                    return caption.ToJsonString();
                }
            } catch (System.Text.Json.JsonException) { /* The image workflow reports malformed captions. */ }
        }
        return prompt.TrimEnd() + "\n\n" + direction;
    }

    public static IReadOnlyList<AssetBatchPlanRow> Build(AssetLibrary library, ImageWorkflow workflow, VisualStyle? projectStyle = null, VisualStyle? runStyle = null)
    {
        var rows = new List<AssetBatchPlanRow>();
        foreach (var asset in library.Assets)
        {
            foreach (var look in new Guid?[] { null }.Concat(asset.Looks.Where(l => !l.Archived).Select(l => (Guid?)l.Id)))
            {
                var context = LookPolicy.Capture(asset, look);
                var approved = asset.Images.Where(i => i.IsReference && LookPolicy.Find(asset, i.LookId)?.Archived != true).ToArray();
                var ordered = approved.Where(i => look is not null && i.LookId == look)
                    .Concat(asset.PreferredIdentityReferences.Select(p => approved.FirstOrDefault(i => i.Id == p.ImageId)).OfType<AssetImage>())
                    .Concat(approved.OrderByDescending(i => i.IsCover)).DistinctBy(i => i.Id).Take(Math.Min(2, ImageWorkflowLimits.MaximumReferences(workflow))).ToArray();
                var references = ordered.Select(i => new AssetReferenceLook(new(asset.Id, i.Id), LookPolicy.Capture(asset, i.LookId))).ToArray();
                var issue = look is not null && asset.Category == AssetCategory.Character && references.Length == 0
                    ? "Approve an identity reference before generating this character look." : string.IsNullOrWhiteSpace(context.IdentityNotes) && string.IsNullOrWhiteSpace(context.Description)
                    ? "Add visual direction in the prompt before starting." : null;
                var direction = string.Join("\n\n", new[] { context.IdentityNotes, context.Description, context.IdentityGuidance, context.PreservationGuidance }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var prompt = (references.Length > 0 ? "Use the supplied references to preserve the described visual features. Apply the requested look; source clothing is not a requirement.\n\n" : "") +
                    $"Create a clear reference image of {context.AssetName}" + (look is null ? "." : $" for the {context.LookName} look.") + "\n\n" + Presentation(asset.Category) + direction;
                var item = new AssetBatchItem { Context = context, Category = asset.Category, Prompt = prompt.Trim(), References = references, VisualStyle = VisualStyles.Resolve(projectStyle, asset.VisualStyle, runStyle),
                    Loras = (asset.Loras.GetValueOrDefault(workflow) ?? []).ToArray() };
                rows.Add(new(item, !approved.Any(i => i.LookId == look), issue));
            }
        }
        return rows;
    }
}
