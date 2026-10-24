using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScriptSequenceDraftingTests
{
    internal static ScriptScenePlan Plan => new(["Eira finds the key."], ["The gate is closed."], ["The visitor remains unidentified."], "Eira takes the key.", "");
    internal const string Candidate = """
        {"scenes":[{"heading":"EXT. RIVER — DAY","beatIds":["B1","END"],"blocks":[{"kind":"Action","spans":[{"text":"Eira finds a brass key in the grass. She takes the key and follows the river."}]}]}]}
        """;
    internal const string CandidateQuote = "Eira finds a brass key in the grass. She takes the key";
    internal static string Review(bool covered = true, string quote = CandidateQuote, string severity = "note") => JsonSerializer.Serialize(new {
        beats = new[] { "B1", "END" }.Select(id => new { id, covered, reason = covered ? "The action dramatizes the required event." : "The required event is missing.",
            evidence = covered ? new[] { new { scene = 1, block = 1, quote } } : [] }),
        findings = severity == "none" ? [] : new[] { new { category = "screenplay", severity, detail = severity == "note" ? "A shorter sentence could improve the rhythm." : "The required action needs correction.",
            evidence = new[] { new { scene = 1, block = 1, quote } }, sourceEvidence = Array.Empty<ScriptSceneEvidence>() } }
    }, AtomicJsonFile.Options);
    internal static ScriptAdaptationContext Context => new(Guid.NewGuid(), Guid.NewGuid(), 0, "A journey", "", new(), new(Guid.NewGuid(), 0), [], [new("Eira finds a brass key.", [1])], []);

    [Fact]
    public void SceneObjectsBecomeRealSceneBlocksAndPreserveTextAndEmphasis()
    {
        var raw = """
            {"scenes":[{"heading":"INT. HALL — NIGHT","beatIds":["B1"],"blocks":[{"kind":"Character","spans":[{"text":"EIRA"}]},{"kind":"Dialogue","spans":[{"text":"It opens.","italic":true}]}]},{"heading":"EXT. RIVER — DAY","beatIds":["END"],"blocks":[{"kind":"Action","spans":[{"text":"She takes the key."}]}]}]}
            """;
        var parsed = ScriptSequenceDrafting.Parse(raw);
        Assert.Equal(2, parsed.Blocks.Count(b => b.Kind == ScriptBlockKind.Scene));
        Assert.True(parsed.Blocks[2].Spans[0].Italic);
        Assert.Equal("She takes the key.", parsed.Blocks[^1].Text);
        Assert.Empty(ScriptSequenceDrafting.CheckStructure(parsed, Plan));
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("{\"scenes\":[]}")]
    [InlineData("{\"scenes\":[{\"heading\":\"x\",\"beatIds\":[],\"blocks\":[]}]}")]
    public void IncompleteOrWrongShapesCannotBecomeScreenplay(string raw) => Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.Parse(raw));

    [Fact]
    public void StructuralChecksFindMissingClaimsHiddenHeadingsAndOrphanDialogue()
    {
        var parsed = ScriptSequenceDrafting.Parse("""
            {"scenes":[{"heading":"INT. HALL — NIGHT","beatIds":["B99"],"blocks":[{"kind":"Dialogue","spans":[{"text":"It opens."}]},{"kind":"Action","spans":[{"text":"EXT. RIVER — DAY"}]}]}]}
            """);
        var findings = ScriptSequenceDrafting.CheckStructure(parsed, Plan);
        Assert.Contains(findings, f => f.Detail.Contains("B1")); Assert.Contains(findings, f => f.Detail.Contains("END"));
        Assert.Contains(findings, f => f.Detail.Contains("B99"));
        Assert.Equal(2, findings.Count(f => f.Category == "screenplay"));
    }

    [Fact]
    public void ConsecutiveDialogueParagraphsAndParentheticalsKeepTheirEstablishedSpeaker()
    {
        var candidate = ScriptSequenceDrafting.Parse("""
            {"scenes":[{"heading":"INT. HALL — NIGHT","beatIds":["B1","END"],"blocks":[{"kind":"Character","spans":[{"text":"EIRA"}]},{"kind":"Dialogue","spans":[{"text":"I found a key."}]},{"kind":"Dialogue","spans":[{"text":"It was beside the river."}]},{"kind":"Parenthetical","spans":[{"text":"(quietly)"}]},{"kind":"Dialogue","spans":[{"text":"I will keep it."}]}]}]}
            """);
        Assert.Empty(ScriptSequenceDrafting.CheckStructure(candidate, Plan));
        var scene = candidate.Scenes[0];
        var withoutSpeaker = candidate with { Scenes = [scene with { Blocks = scene.Blocks.Skip(1).ToList() }] };
        Assert.Equal(3, ScriptSequenceDrafting.CheckStructure(withoutSpeaker, Plan).Length);
        var afterAction = candidate with { Scenes = [scene with { Blocks = [scene.Blocks[0], ScriptBlock.Create(ScriptBlockKind.Action, "She turns away."), scene.Blocks[1]] }] };
        Assert.Single(ScriptSequenceDrafting.CheckStructure(afterAction, Plan));
    }

    [Theory]
    [InlineData(true, ScriptSequenceReviewState.Passed)]
    [InlineData(false, ScriptSequenceReviewState.NeedsAttention)]
    public void BeatCoverageNeedsAPrimaryCitationButAllowsSupportingEvidenceInOtherScenes(bool primary, ScriptSequenceReviewState expected)
    {
        var candidate = ScriptSequenceDrafting.Parse("""
            {"scenes":[{"heading":"EXT. RIVER — DAY","beatIds":["B1"],"blocks":[{"kind":"Action","spans":[{"text":"Eira finds a brass key."}]}]},{"heading":"INT. HALL — NIGHT","beatIds":["END"],"blocks":[{"kind":"Action","spans":[{"text":"Eira takes the key."}]}]}]}
            """);
        var primaryQuote = new ScriptDraftReference(1, 1, "Eira finds a brass key.");
        var supportingQuote = new ScriptDraftReference(2, 1, "Eira takes the key.");
        var raw = JsonSerializer.Serialize(new { beats = new[] {
            new ScriptBeatReview("B1", true, "The key is found, then taken.", primary ? [primaryQuote, supportingQuote] : [supportingQuote]),
            new ScriptBeatReview("END", true, "She takes the key.", [supportingQuote])
        }, findings = Array.Empty<ScriptSequenceFinding>() }, AtomicJsonFile.Options);
        var report = ScriptSequenceDrafting.ParseReview(raw, candidate, Plan, Context, 0);
        Assert.Equal(expected, report.State);
        if (primary) { Assert.Empty(report.Findings); Assert.Equal(2, report.Beats[0].Evidence.Length); }
        else Assert.Contains(report.Findings, f => f.Severity == "repair" && f.Detail.Contains("Include B1"));
    }

    [Fact]
    public void EveryRequiredBeatAndTheEndingNeedVerifiedDraftEvidence()
    {
        var candidate = ScriptSequenceDrafting.Parse(Candidate);
        var reviewed = ScriptSequenceDrafting.ParseReview(Review(), candidate, Plan, Context, 0);
        Assert.Equal(ScriptSequenceReviewState.Passed, reviewed.State);
        Assert.Equal(2, reviewed.Beats.Length);
        Assert.All(reviewed.Findings, f => Assert.Equal("note", f.Severity));
        var missing = ScriptSequenceDrafting.ParseReview(Review(false, severity: "none"), candidate, Plan, Context, 0);
        Assert.Equal(ScriptSequenceReviewState.NeedsAttention, missing.State);
        Assert.Contains(missing.Findings, f => f.Detail.StartsWith("END:"));
        Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(Review(quote: "An invented quotation."), candidate, Plan, Context, 0));
        Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(Review().Replace("\"END\"", "\"B1\""), candidate, Plan, Context, 0));
    }

    [Fact]
    public void UnsupportedReviewSourceQuotesCannotTriggerCreativeRepairs()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Review(severity: "repair"))!;
        json["findings"]![0]!["sourceEvidence"] = JsonSerializer.SerializeToNode(new[] { new ScriptSceneEvidence(1, "The key was silver.") }, AtomicJsonFile.Options);
        var raw = json.ToJsonString();
        Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(raw, ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0));
        raw = raw.Replace("The key was silver.", "Eira finds a brass key.");
        Assert.Equal(ScriptSequenceReviewState.NeedsAttention, ScriptSequenceDrafting.ParseReview(raw, ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0).State);
    }

    internal static string RedundantReview()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Review(severity: "none"))!;
        var references = new System.Text.Json.Nodes.JsonArray();
        for (var i = 0; i < 18; i++) references.Add(JsonSerializer.SerializeToNode(new ScriptDraftReference(1, 1, CandidateQuote), AtomicJsonFile.Options));
        references.Add(JsonSerializer.SerializeToNode(new ScriptDraftReference(1, 99, "A misplaced quotation."), AtomicJsonFile.Options));
        json["beats"]![0]!["evidence"] = references;
        return json.ToJsonString();
    }

    internal static string FindingWithExtraCitation(string severity = "note")
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Review(severity: severity))!;
        json["findings"]![0]!["evidence"]!.AsArray().Add(JsonSerializer.SerializeToNode(new ScriptDraftReference(1, 1, "An unverifiable extra quotation."), AtomicJsonFile.Options));
        return json.ToJsonString();
    }

    [Theory]
    [InlineData("note", ScriptSequenceReviewState.Passed)]
    [InlineData("repair", ScriptSequenceReviewState.NeedsAttention)]
    public void FindingExtraCitationIsOmittedButItsSeverityAndVerifiedEvidenceRemain(string severity, ScriptSequenceReviewState expected)
    {
        var raw = FindingWithExtraCitation(severity);
        var report = ScriptSequenceDrafting.ParseReview(raw, ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0);
        Assert.Equal(expected, report.State); Assert.Equal(raw, report.RawResponse);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(severity, finding.Severity); Assert.Equal("screenplay", finding.Category);
        Assert.Equal(CandidateQuote, Assert.Single(finding.Evidence).Quote);
        Assert.Contains("Finding 1 (screenplay): 1 draft citation(s)", Assert.Single(report.Warnings));
    }

    [Fact]
    public void FindingWithoutVerifiedEvidenceOrWithInventedSourceEvidenceStillBlocksApproval()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(FindingWithExtraCitation())!;
        json["findings"]![0]!["evidence"]!.AsArray().RemoveAt(0);
        var error = Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(json.ToJsonString(), ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0));
        Assert.Contains("Finding 1", error.Message); Assert.Contains("none of its 1", error.Message);
        json = System.Text.Json.Nodes.JsonNode.Parse(FindingWithExtraCitation())!;
        json["findings"]![0]!["sourceEvidence"] = JsonSerializer.SerializeToNode(new[] { new ScriptSceneEvidence(1, "The key was silver.") }, AtomicJsonFile.Options);
        Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(json.ToJsonString(), ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0));
    }

    [Fact]
    public void RedundantReviewCitationsAndAnUnverifiableExtraAreWarningsWhenVerifiedEvidenceRemains()
    {
        var raw = RedundantReview();
        var report = ScriptSequenceDrafting.ParseReview(raw, ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0);
        Assert.Equal(ScriptSequenceReviewState.Passed, report.State);
        Assert.Equal(18, report.Beats[0].Evidence.Length);
        Assert.Contains(report.Warnings, w => w.Contains("1 citation(s) could not be verified"));
        Assert.Contains(report.Warnings, w => w.Contains("19 citations"));
        Assert.Equal(raw, report.RawResponse); Assert.Empty(report.Findings);
        var error = Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(Review(quote: "An invented quote."), ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0));
        Assert.Contains("none of its 1 citation(s)", error.Message);
    }

    [Theory]
    [InlineData("B2")]
    [InlineData("END")]
    public void ExtraOrDuplicatedReviewChecksAreRejectedWithTheExpectedChecklist(string extraId)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Review(severity: "none"))!;
        var extra = json["beats"]![1]!.DeepClone();
        extra["id"] = extraId;
        json["beats"]!.AsArray().Add(extra);
        var error = Assert.Throws<WorkspaceStoreException>(() => ScriptSequenceDrafting.ParseReview(json.ToJsonString(), ScriptSequenceDrafting.Parse(Candidate), Plan, Context, 0));
        Assert.Contains("exactly 2", error.Message); Assert.Contains("B1, END", error.Message);
    }

    [Fact]
    public void ReviewFormatUsesTheExactBriefChecklistIncludingOneFinalEnding()
    {
        var plan = Plan with { Beats = ["First event.", "Second event."] };
        var schema = ScriptSequenceDrafting.ReviewSchemaFor(plan).GetProperty("properties").GetProperty("beats");
        Assert.Equal(3, schema.GetProperty("minItems").GetInt32());
        Assert.Equal(3, schema.GetProperty("maxItems").GetInt32());
        Assert.Equal(new[] { "B1", "B2", "END" }, schema.GetProperty("items").GetProperty("properties").GetProperty("id").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        var grammar = ScriptSequenceDrafting.ReviewGrammarFor(plan);
        Assert.Contains("beat-1 comma beat-2 comma beat-3", grammar);
        Assert.DoesNotContain("(comma beat)*", grammar);
        // A format generated for another job must not mutate the earlier checklist.
        Assert.Equal(2, ScriptSequenceDrafting.ReviewSchemaFor(Plan).GetProperty("properties").GetProperty("beats").GetProperty("maxItems").GetInt32());
        Assert.Equal(3, schema.GetProperty("maxItems").GetInt32());
    }

    [Theory]
    [InlineData("Mystery; the visitor remains unidentified.", "The visitor delivers a sealed letter. The recipient opens it.")]
    [InlineData("A silent visual comedy.", "A hat blows into a fountain. The owner retrieves it with an umbrella.")]
    [InlineData("First-person narration with voiceover.", "A traveller remembers leaving home, then boards a train.")]
    [InlineData("A present-tense drama without voiceover.", "Two friends disagree at dinner and reach a compromise.")]
    [InlineData("日本語の無声映画", "旅人が鍵を拾い、門を開ける。")]
    public void ContractAndReviewCarryAuthorChoicesWithoutGenreSpecificRules(string direction, string beat)
    {
        var plan = Plan with { Beats = [beat], Ending = beat };
        var outline = new SourceOutline { Acts = [new() { Scenes = [new() { Summary = beat, Sources = [1] }] }] };
        var initial = Context;
        var source = initial with { Direction = direction, Outline = outline, Selection = new(initial.RunId, 0, 0, 0, true) };
        var script = new ScriptDocument { ProjectId = source.ProjectId };
        var request = new ScriptAssistantRequest(new() { SourceScene = new(source.RunId, 0, 0, 0), Operation = WritingOperation.Continue,
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null) }, script, []) { Adaptation = source };
        var messages = ScriptSequenceDrafting.WithPlan(ScriptSequenceDrafting.Messages(request, []).Select(AiTextMessage.Capture).ToArray(), plan);
        var text = string.Join("\n", messages.SelectMany(m => m.Parts).Select(p => p.Text));
        using var task = JsonDocument.Parse(messages[^1].Parts[0].Text!);
        Assert.Equal(direction, task.RootElement.GetProperty("direction").GetString());
        using var contract = JsonDocument.Parse(messages[^2].Parts[0].Text!.Split('\n', 2)[1]);
        Assert.Equal(beat, contract.RootElement.GetProperty("requiredBeats")[0].GetProperty("text").GetString());
        Assert.Contains("B1", text); Assert.Contains("END", text); Assert.Contains("The visitor remains unidentified.", text);
        Assert.DoesNotContain("sexual", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("American", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanningSeparatesItsFormatExampleFromTheRealSourceAndFinalTask()
    {
        var script = new ScriptDocument { ProjectId = Context.ProjectId };
        var request = new ScriptAssistantRequest(new() {
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null),
            Instructions = "Present the story as a silent visual comedy."
        }, script, []) { Adaptation = Context };
        var messages = ScriptSequenceDrafting.PlanningMessages(request);
        Assert.Equal("system", messages[0].Role.Value);
        Assert.Contains("separate screenplay writer", messages[0].Text);
        Assert.Contains("third-person factual sentences", messages[0].Text);
        Assert.Contains("FORMAT EXAMPLE ONLY", messages[1].Text);
        Assert.Equal("assistant", messages[2].Role.Value);
        var example = ScriptScenePlanning.Inspect(messages[2].Text!);
        Assert.True(example.CanUse); Assert.Empty(example.Warnings);
        Assert.Equal(4, example.Plan!.Beats.Length);
        Assert.Empty(example.Plan.Withhold);
        Assert.Empty(example.Plan.Evidence!); Assert.Empty(example.Plan.TimeCue);
        Assert.All(example.Plan.Beats, beat => Assert.InRange(beat.Length, 1, 200));
        Assert.Contains("ORIGINAL SOURCE EVIDENCE", messages[3].Text);
        Assert.DoesNotContain("RECENT SCREENPLAY WRITING", messages[3].Text);
        using var task = JsonDocument.Parse(messages[^1].Text!);
        Assert.Contains("each distinct consequential change", task.RootElement.GetProperty("task").GetString());
        Assert.Equal(request.Run.Instructions, task.RootElement.GetProperty("authorInstructions").GetString());
        Assert.Equal(Context.Focus, task.RootElement.GetProperty("focus").GetString());
        Assert.Equal(Context.Passages.SelectMany(p => p.Sources).Distinct().Order(),
            task.RootElement.GetProperty("evidenceSectionIds").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public void PlannerReceivesSourceAndProgressWhileWriterStillReceivesActualScreenplayContinuity()
    {
        var source = Context with { Outline = new() { Acts = [new() { Scenes = [new() { Title = "The key", Summary = "Eira finds the key.", Sources = [1] }] }] } };
        source = source with { Selection = new(source.RunId, 0, 0, 0, true) };
        var script = new ScriptDocument { ProjectId = source.ProjectId,
            Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. KITCHEN - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "The kettle has gone cold.")] };
        var request = new ScriptAssistantRequest(new() {
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null),
            SourceScene = new(source.RunId, 0, 0, 0)
        }, script, []) { Adaptation = source };
        var planning = string.Join("\n", ScriptSequenceDrafting.PlanningMessages(request).Select(m => m.Text));
        var writing = string.Join("\n", ScriptSequenceDrafting.Messages(request, []).Select(m => m.Text));
        Assert.DoesNotContain("The kettle has gone cold.", planning);
        Assert.Contains("The kettle has gone cold.", writing);
        Assert.Contains("ORIGINAL SOURCE EVIDENCE", planning);
        Assert.Contains("scenesInScript", planning);
    }
}
