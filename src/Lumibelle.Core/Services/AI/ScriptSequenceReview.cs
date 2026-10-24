using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public enum ScriptSequenceReviewState { Checking, Repairing, Passed, NeedsAttention }
public sealed record ScriptDraftReference(int Scene, int Block, string Quote);
public sealed record ScriptBeatReview(string Id, bool Covered, string Reason, ScriptDraftReference[] Evidence);
public sealed record ScriptSequenceFinding(string Category, string Severity, string Detail, ScriptDraftReference[] Evidence, ScriptSceneEvidence[] SourceEvidence);
public sealed record ScriptSequenceReview(ScriptSequenceReviewState State, int Repairs, ScriptBeatReview[] Beats, ScriptSequenceFinding[] Findings, string? RawResponse = null)
{
    public string? ValidationError { get; init; }
    public string[] Warnings { get; init; } = [];
}
public sealed record ScriptDraftScene(string Heading, string[] BeatIds, List<ScriptBlock> Blocks);
public sealed record ScriptSequenceCandidate(ScriptDraftScene[] Scenes, List<ScriptBlock> Blocks);
public sealed record ScriptRequiredBeat(string Id, string Text);

public static partial class ScriptSequenceDrafting
{
    public static ScriptRequiredBeat[] RequiredBeats(ScriptScenePlan plan) =>
        [.. plan.Beats.Select((text, index) => new ScriptRequiredBeat($"B{index + 1}", text)), new("END", plan.Ending)];
    public static ScriptSequenceCandidate Parse(string raw)
    {
        try {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array || scenes.GetArrayLength() is < 1 or > 64)
                throw new WorkspaceStoreException("Return a scenes object with one or more screenplay scenes.");
            List<ScriptDraftScene> parsed = []; List<ScriptBlock> blocks = [];
            foreach (var scene in scenes.EnumerateArray()) {
                if (scene.ValueKind != JsonValueKind.Object || !scene.TryGetProperty("heading", out var heading) || heading.ValueKind != JsonValueKind.String ||
                    heading.GetString() is not { Length: > 0 and <= 180 } title || string.IsNullOrWhiteSpace(title) || title.Contains('\n') || title.Contains('\r') ||
                    !scene.TryGetProperty("beatIds", out var beatIds) || beatIds.ValueKind != JsonValueKind.Array || beatIds.GetArrayLength() > ScriptScenePlanning.MaximumBeats + 1 ||
                    beatIds.EnumerateArray().Any(b => b.ValueKind != JsonValueKind.String || b.GetString() is not { Length: > 0 and <= 12 }) ||
                    !scene.TryGetProperty("blocks", out var body))
                    throw new WorkspaceStoreException("Every scene needs a short single-line heading, beatIds and screenplay blocks.");
                var screenplay = ScreenplayJson.Parse(body.GetRawText());
                if (screenplay.Blocks is not { Count: > 0 } content) throw new WorkspaceStoreException(screenplay.Error ?? "The scene has no screenplay blocks.");
                if (content.Any(b => b.Kind is ScriptBlockKind.Scene or ScriptBlockKind.Act))
                    throw new WorkspaceStoreException("Each scene heading belongs in heading; its body contains action, speakers, dialogue and transitions.");
                parsed.Add(new(title, beatIds.EnumerateArray().Select(b => b.GetString()!).ToArray(), content));
                blocks.Add(ScriptBlock.Create(ScriptBlockKind.Scene, title)); blocks.AddRange(content);
            }
            ScriptStructure.ValidateBlocks(blocks);
            if (ScriptAdaptation.DraftError(blocks) is { } error) throw new WorkspaceStoreException(error);
            return new(parsed.ToArray(), blocks);
        }
        catch (JsonException) { throw new WorkspaceStoreException("The response is not a complete screenplay scenes object."); }
    }

    public static ScriptSequenceFinding[] CheckStructure(ScriptSequenceCandidate candidate, ScriptScenePlan plan)
    {
        List<ScriptSequenceFinding> findings = [];
        var required = RequiredBeats(plan).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var claims = candidate.Scenes.SelectMany(s => s.BeatIds).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in required.Except(claims)) findings.Add(Problem("coverage", $"Assign and dramatize required beat {missing}."));
        foreach (var unknown in claims.Except(required)) findings.Add(Problem("coverage", $"Replace unknown beat ID {unknown} with the applicable required beat IDs."));
        for (var si = 0; si < candidate.Scenes.Length; si++) {
            var scene = candidate.Scenes[si];
            var hasSpeaker = false;
            for (var bi = 0; bi < scene.Blocks.Count; bi++) {
                var block = scene.Blocks[bi];
                if (block.Kind == ScriptBlockKind.Character) hasSpeaker = true;
                else if (block.Kind is ScriptBlockKind.Action or ScriptBlockKind.Transition) hasSpeaker = false;
                // A clear standalone conventional slugline is a structural issue. A
                // new scene object resolves it without changing the author's prose.
                if (block.Kind == ScriptBlockKind.Action && Regex.IsMatch(block.Text, @"^(?:INT\.|EXT\.|INT/EXT\.)\s+[^\r\n]{1,160}$", RegexOptions.CultureInvariant) &&
                    !block.Text.Any(char.IsLower))
                    findings.Add(Problem("screenplay", "Move this location/time heading into a new scene object.", new(si + 1, bi + 1, block.Text[..Math.Min(block.Text.Length, 400)])));
                if (block.Kind == ScriptBlockKind.Dialogue && !hasSpeaker)
                    findings.Add(Problem("screenplay", "Give this spoken exchange a Character block identifying its speaker.", new(si + 1, bi + 1, block.Text[..Math.Min(block.Text.Length, 400)])));
            }
        }
        return findings.ToArray();
    }

    internal static ScriptSequenceFinding Problem(string category, string detail, ScriptDraftReference? reference = null) =>
        new(category, "repair", detail, reference is null ? [] : [reference], []);

    public static ScriptSequenceReview ParseReview(string raw, ScriptSequenceCandidate candidate, ScriptScenePlan plan, ScriptAdaptationContext context, int repairs)
    {
        try {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var required = RequiredBeats(plan);
            if (!root.TryGetProperty("beats", out var beats) || beats.ValueKind != JsonValueKind.Array || beats.GetArrayLength() != required.Length ||
                !root.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array || findings.GetArrayLength() > 32)
                throw new WorkspaceStoreException($"Return exactly {required.Length} beat checks in this order: {string.Join(", ", required.Select(b => b.Id))}, followed by findings.");
            var checks = beats.Deserialize<ScriptBeatReview[]>(AtomicJsonFile.Options)!;
            var issues = findings.Deserialize<ScriptSequenceFinding[]>(AtomicJsonFile.Options)!;
            List<string> warnings = [];
            for (var i = 0; i < checks.Length; i++) {
                var beat = checks[i];
                if (beat is null || beat.Id != required[i].Id || !MeaningfulReviewText(beat.Reason) || beat.Evidence is null || beat.Evidence.Length > 128)
                    throw new WorkspaceStoreException($"The reviewer returned an invalid check for {required[i].Id}. It needs that ID, a reason, and a list of draft citations.");
                var verified = beat.Evidence.Where(e => ReferenceMatches(e, candidate)).ToArray();
                if (beat.Covered && verified.Length == 0)
                    throw new WorkspaceStoreException($"The reviewer marked {beat.Id} covered, but none of its {beat.Evidence.Length} citation(s) match the numbered draft. This is a review-evidence failure, not a confirmed screenplay error.");
                if (verified.Length < beat.Evidence.Length) warnings.Add($"{beat.Id}: {beat.Evidence.Length - verified.Length} citation(s) could not be verified and were omitted; {verified.Length} verified citation(s) remain. The original review response is preserved.");
                if (beat.Evidence.Length > 6) warnings.Add($"{beat.Id}: the reviewer supplied {beat.Evidence.Length} citations instead of the recommended maximum of 6. Extra verified citations were retained.");
                checks[i] = beat with { Evidence = verified };
            }
            for (var i = 0; i < issues.Length; i++) {
                var issue = issues[i];
                if (issue is null || issue.Category is not ("coverage" or "ending" or "source_facts" or "knowledge" or "continuity" or "duplication" or "screenplay" or "scope") ||
                    issue.Severity is not ("repair" or "note") || !MeaningfulReviewText(issue.Detail) || issue.Evidence is not { Length: > 0 and <= 6 } ||
                    issue.SourceEvidence is not { Length: <= 6 } ||
                    issue.SourceEvidence.Any(e => e is null || e.Quote is not { Length: > 0 and <= 800 } || string.IsNullOrWhiteSpace(e.Quote) || !context.Passages.Any(p => p.Sources.Contains(e.Section) && Normalize(p.Text).Contains(Normalize(e.Quote), StringComparison.Ordinal))))
                    throw new WorkspaceStoreException("Review findings need valid categories and verifiable draft/source evidence.");
                // Findings use the same citation rule as beat checks: an invalid
                // extra does not erase verified evidence. Never invent a quote,
                // relocate a citation, discard the finding or change its severity.
                var verified = issue.Evidence.Where(e => ReferenceMatches(e, candidate)).ToArray();
                if (verified.Length == 0)
                    throw new WorkspaceStoreException($"Finding {i + 1} ({issue.Category}): none of its {issue.Evidence.Length} draft citation(s) match the numbered draft. This is a review-evidence failure, not a confirmed screenplay error.");
                if (verified.Length < issue.Evidence.Length)
                    warnings.Add($"Finding {i + 1} ({issue.Category}): {issue.Evidence.Length - verified.Length} draft citation(s) could not be verified and were omitted; {verified.Length} verified citation(s) remain. The finding and original review response are preserved.");
                issues[i] = issue with { Evidence = verified };
            }
            // A beat needs verified evidence in a scene that claims it. Additional
            // verified setup/aftermath citations may legitimately span other scenes.
            var misplacedClaims = checks.Where(b => b.Covered && !b.Evidence.Any(e => candidate.Scenes[e.Scene - 1].BeatIds.Contains(b.Id, StringComparer.Ordinal)))
                .Select(b => Problem("coverage", $"Include {b.Id} in the beatIds of the scene that dramatizes it.", b.Evidence[0]));
            var allIssues = CheckStructure(candidate, plan).Concat(issues).Concat(misplacedClaims)
                .Concat(checks.Where(b => !b.Covered).Select(b => Problem("coverage", $"{b.Id}: {b.Reason}"))).ToArray();
            return new(allIssues.Any(f => f.Severity == "repair") ? ScriptSequenceReviewState.NeedsAttention : ScriptSequenceReviewState.Passed, repairs, checks, allIssues, raw) { Warnings = warnings.ToArray() };
        }
        catch (JsonException) { throw new WorkspaceStoreException("The review response is not a complete evidence-based review object."); }
        catch (InvalidOperationException) { throw new WorkspaceStoreException("The review response has invalid field types."); }
    }

    private static bool ReferenceMatches(ScriptDraftReference? evidence, ScriptSequenceCandidate candidate)
    {
        if (evidence is null || evidence.Scene < 1 || evidence.Scene > candidate.Scenes.Length || evidence.Quote is not { Length: > 0 and <= 400 } || string.IsNullOrWhiteSpace(evidence.Quote)) return false;
        var scene = candidate.Scenes[evidence.Scene - 1];
        return evidence.Block >= 0 && evidence.Block <= scene.Blocks.Count && Normalize(evidence.Block == 0 ? scene.Heading : scene.Blocks[evidence.Block - 1].Text).Contains(Normalize(evidence.Quote), StringComparison.Ordinal);
    }
    private static bool MeaningfulReviewText(string? text) => text is { Length: > 0 and <= 1600 } && text.Any(char.IsLetterOrDigit);
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
