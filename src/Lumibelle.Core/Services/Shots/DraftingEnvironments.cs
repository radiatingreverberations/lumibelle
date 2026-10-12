using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Shots;

public static class DraftingEnvironments
{
    public const string SelectionProfile = "scene-environment-views-v1";
    public static DraftingEnvironmentAssessment? ReadAssessment(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is not System.Text.Json.Nodes.JsonObject) throw new JsonException("Environment assessment must be an object.");
        var normalized = node.DeepClone().AsObject();
        foreach (var field in new[] { "summary", "assumptions", "concerns", "missingCoverage" }) {
            if (normalized[field] is System.Text.Json.Nodes.JsonArray lines) {
                if (lines.Any(line => line is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out _))) throw new JsonException("Assessment notes must be text.");
                normalized[field] = string.Join("\n", lines.Select(line => line!.GetValue<string>()));
            }
        }
        return normalized.Deserialize<DraftingEnvironmentAssessment>(AtomicJsonFile.Options);
    }
    public static DraftingEnvironmentSetup? Effective(Shot? shot, SceneReferenceSetup? scene)
    {
        if (shot?.EnvironmentInspectionDisabled == true) return null;
        if (shot?.EnvironmentInspection is { } own && (shot.EnvironmentAssetId is null || own.AssetId == shot.EnvironmentAssetId)) return own;
        if (shot?.EnvironmentAssetId is { } id && scene?.Environment?.AssetId != id)
            return new() { AssetId = id, Version = Guid.Empty }; // No accidental fallback to another set.
        return scene?.Environment;
    }
    public static string Semantic(Guid assetId, string guidance, string constraints, IEnumerable<EnvironmentViewCandidate> views) =>
        ShotDirections.Hash(new { assetId, guidance, constraints, views = views.Select(v => new { v.Name, v.Notes, v.Approved, v.Sha256, Crop = v.Keyframe?.Crop ?? v.Image?.Crop }) });
    public static DraftingEnvironmentSnapshot Snapshot(Guid sceneId, DraftingEnvironmentSetup setup, EnvironmentSourcePool pool)
    {
        if (setup.AssetId != pool.AssetId || setup.Views.Select(v => v.Id).Distinct().Count() != setup.Views.Count)
            throw new WorkspaceStoreException("Choose distinct views from this environment.");
        List<EnvironmentViewCandidate> selected = [];
        foreach (var saved in setup.Views) {
            var current = pool.Candidates.SingleOrDefault(v => v.Id == saved.Id);
            if (current is null || current.Sha256 != saved.Sha256 || current.Name != saved.Name || current.Notes != saved.Notes || current.Approved != saved.Approved)
                throw new WorkspaceStoreException("A selected environment view changed or is unavailable. Review the scene's environment views again.");
            selected.Add(current);
        }
        return new(Guid.NewGuid(), sceneId, setup.Version, setup.AssetId, pool.AssetName, pool.Guidance, setup.Constraints,
            selected, Semantic(setup.AssetId, pool.Guidance, setup.Constraints, selected));
    }
    public static void ValidateAssessment(DraftingEnvironmentAssessment assessment, DraftingEnvironmentSnapshot snapshot)
    {
        if (assessment.Claims is null) throw new WorkspaceStoreException("Environment evidence is incomplete. Inspect the response before applying.");
        foreach (var claim in assessment.Claims) {
            if (claim is null || claim.CandidateIds is null || !Enum.IsDefined(claim.Status) || claim.CandidateIds.Distinct().Count() != claim.CandidateIds.Count ||
                claim.CandidateIds.Any(id => !snapshot.Views.Any(v => v.Id == id)) ||
                claim.Status == ReferenceSupportStatus.Supported && claim.CandidateIds.Count == 0)
                throw new WorkspaceStoreException("Environment evidence cites a picture not attached to this drafting request.");
        }
    }
    public static void ValidateCapacity(AiTextJobRequest request, long? contextLength)
    {
        var text = string.Join("\n", request.Messages.SelectMany(m => m.Parts).Select(p => p.Text ?? ""));
        var pictures = request.Messages.Sum(m => m.Parts.Count(p => p.Image is not null));
        var reply = request.Model.MaxOutputTokens ?? request.Settings.MaxOutputTokens;
        if (pictures > ComfyTextVision.MaximumImages || contextLength is { } limit && ComfyTextCapacity.TextTokens(text) + (long)pictures * 1536 + reply > limit)
            throw new WorkspaceStoreException("The complete drafting request exceeds this model's capacity. Reduce scenes or inspection views, or choose another model.");
    }
    public static void ValidateSelection(SceneEnvironmentSelectionRequest r)
    {
        if (r.ProjectId == Guid.Empty || r.SceneId == Guid.Empty || r.AssetId == Guid.Empty || r.Candidates.Count is < 1 or > 24 ||
            r.Candidates.Select(v => v.Id).Distinct().Count() != r.Candidates.Count ||
            r.SourceIds.Count > 0 && (r.Candidates.Any(c => !r.SourceIds.Contains(c.Id)) || r.PreviouslyInspected.Any(id => !r.SourceIds.Contains(id))))
            throw new WorkspaceStoreException("Choose distinct environment candidates for this inspection batch.");
    }
    public static List<ChatMessage> SelectionMessages(SceneEnvironmentSelectionRequest r, IReadOnlyList<byte[]> pictures)
    {
        ValidateSelection(r);
        if (!pictures.Select(lumibelle.Services.Production.ReelViewSuggestions.Hash).SequenceEqual(r.Candidates.Select(c => c.Sha256)))
            throw new WorkspaceStoreException("Environment inspection images do not match the captured candidates.");
        const string instruction = "Choose a compact, complementary set of pictures to help direct the WHOLE scene. " +
            "Inspect the attached candidates only. Preserve coverage of acting areas, landmarks, obstacles, sightlines and routes, " +
            "and retain evidence needed to explain conflicting set versions. Prefer approved sources when useful; avoid redundant pretty views. " +
            "Authored constraints are author-specified, not discoveries from images. Recipes describe intended camera coverage, not proven geometry. " +
            "These are drafting inspection pictures, not generation bindings or a storyboard. Do not copy reel camera motion. " +
            "There is no three-view target: choose the smallest useful selection. In intermediate batches retain complementary and conflicting evidence for later comparison. " +
            "Do not cite unseen candidates or invent missing views. Return JSON {summary,views:[{candidateId,reason}],warnings:[short concerns]}.";
        var metadata = new { r.SceneText, r.Instructions, r.Constraints, r.AssetName, r.Guidance, r.Final,
            candidates = r.Candidates.Select(c => new { candidateId = c.Id, c.Name, c.Approved, c.Notes, reel = c.Reel?.Name, seconds = c.Keyframe?.Frame.Seconds }) };
        return [new(ChatRole.System, instruction), new(ChatRole.User, new List<AIContent> { new TextContent(JsonSerializer.Serialize(metadata, AtomicJsonFile.Options)) }
            .Concat(pictures.Select(p => new DataContent(p, "image/png"))).ToList())];
    }
    public static SceneEnvironmentSelectionResult ParseSelection(string raw, SceneEnvironmentSelectionRequest r)
    {
        ValidateSelection(r);
        var text = raw.Trim();
        if (text.StartsWith("```json") && text.EndsWith("```")) text = text[7..^3].Trim();
        try {
            var result = JsonSerializer.Deserialize<SceneEnvironmentSelectionResult>(text, AtomicJsonFile.Options)!;
            if (result is null || result.Views is null || result.Warnings is null || result.Views.Any(v => v is null) || result.Views.Select(v => v.CandidateId).Distinct().Count() != result.Views.Count ||
                result.Views.Any(v => !r.Candidates.Any(c => c.Id == v.CandidateId)))
                throw new WorkspaceStoreException("Selection cites a picture not inspected in this batch.");
            return result;
        } catch (JsonException) { throw new WorkspaceStoreException("The environment selection response is incomplete. Inspect it or choose views manually."); }
    }
}
