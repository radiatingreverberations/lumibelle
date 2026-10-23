using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ScriptScenePlanningTests
{
    private static ScriptAdaptationContext Context()
    {
        var run = Guid.NewGuid();
        return new(Guid.NewGuid(), run, 0, "A journey", "", new(), new(run, 0), [],
            [new("Eira finds a brass key.\r\n\"Wait here,\" says Rowan.", [1]), new("She follows the river.", [2])], []);
    }
    private static ScriptScenePlan Brief(params ScriptSceneEvidence[] evidence) =>
        new(["Eira finds the key."], [], ["The garden's identity."], "She takes the key.", "", Evidence: evidence);

    [Fact]
    public void GroundingKeepsOnlyVerbatimExcerptsInTheirCitedSelectedSections()
    {
        var valid = new ScriptSceneEvidence(1, "Eira finds a brass key. \"Wait here,\" says Rowan.");
        var plan = Brief(valid, valid, new(2, "Eira finds a brass key."), new(1, "The key becomes a dragon."));
        var grounded = ScriptScenePlanning.Ground(plan, Context());
        Assert.Equal([valid], grounded.Evidence!);
        Assert.Equal(2, grounded.UnverifiedEvidenceCount);
        Assert.Equal(4, plan.Evidence!.Length);
        Assert.Contains("2 proposed source excerpt", ScriptScenePlanning.Markdown(grounded));
    }
    [Fact]
    public void DraftReceivesVerifiedExcerptsWithoutUnverifiedTextOrWithheldAnswers()
    {
        var plan = ScriptScenePlanning.Ground(Brief(new(1, "Eira finds a brass key."), new(9, "Secret invented answer.")), Context());
        var messages = ScriptScenePlanning.WithPlan([AiTextMessage.Capture(new(ChatRole.User, "Draft this sequence."))], plan);
        var input = string.Join("\n", messages.SelectMany(m => m.Parts).Select(p => p.Text));
        Assert.Contains("Eira finds a brass key.", input);
        Assert.DoesNotContain("Secret invented answer.", input);
        Assert.DoesNotContain("The garden's identity.", input);
        Assert.Equal("Draft this sequence.", messages[^1].Parts[0].Text);
    }
    [Theory]
    [InlineData(0, "An excerpt.")]
    [InlineData(1, " ")]
    public void InvalidExcerptShapesAreRejected(int section, string quote)
        => Assert.False(ScriptScenePlanning.Valid(Brief(new ScriptSceneEvidence(section, quote))));

    [Fact]
    public void HistoricalBriefsWithoutExcerptsRemainUsable()
    {
        const string raw = "{\"beats\":[\"She finds the key.\"],\"continuity\":[],\"withhold\":[],\"ending\":\"She leaves.\",\"timeCue\":\"\"}";
        var plan = ScriptScenePlanning.Parse(raw);
        Assert.NotNull(plan);
        var grounded = ScriptScenePlanning.Ground(plan, Context());
        Assert.Empty(grounded.Evidence!);
        Assert.Equal(0, grounded.UnverifiedEvidenceCount);
        Assert.Equal(plan.Beats, grounded.Beats);
        Assert.NotNull(ScriptScenePlanning.Parse(JsonSerializer.Serialize(grounded)));
    }

    [Theory]
    [InlineData(801)]
    [InlineData(15000)]
    public void OversizedOptionalQuoteDoesNotDiscardAUsableBrief(int length)
    {
        var valid = new ScriptSceneEvidence(1, "Eira finds a brass key.");
        var original = Brief(new(1, new string('x', length)), valid);
        var raw = JsonSerializer.Serialize(original, AtomicJsonFile.Options);
        var parsed = ScriptScenePlanning.Parse(raw);
        Assert.NotNull(parsed);
        Assert.Equal(original.Beats, parsed.Beats);
        Assert.Equal(original.Ending, parsed.Ending);
        Assert.Equal([valid], parsed.Evidence!);
        Assert.Equal(1, parsed.OversizedEvidenceCount);
        var grounded = ScriptScenePlanning.Ground(parsed, Context());
        Assert.Equal(1, grounded.OversizedEvidenceCount);
        Assert.Equal(0, grounded.UnverifiedEvidenceCount);
        Assert.Contains("exceeded 800 characters", ScriptScenePlanning.Markdown(grounded));
        Assert.Contains(new string('x', length), raw);
    }

    [Fact]
    public void DroppingOptionalEvidenceDoesNotRescueMissingStoryBeats()
    {
        var invalid = Brief(new ScriptSceneEvidence(1, new string('x', 801))) with { Beats = [] };
        Assert.Null(ScriptScenePlanning.Parse(JsonSerializer.Serialize(invalid, AtomicJsonFile.Options)));
    }

    [Theory]
    [InlineData(", ")]
    [InlineData("...")]
    [InlineData("[]")]
    public void PunctuationOnlyEntriesAreNotStoryPlanning(string filler)
    {
        var plan = Brief();
        Assert.False(ScriptScenePlanning.Valid(plan with { Beats = [filler] }));
        Assert.False(ScriptScenePlanning.Valid(plan with { Continuity = [filler] }));
        Assert.False(ScriptScenePlanning.Valid(plan with { Withhold = [filler] }));
        Assert.False(ScriptScenePlanning.Valid(plan with { Details = [filler] }));
        Assert.False(ScriptScenePlanning.Valid(plan with { Ending = filler }));
    }

    [Fact]
    public void ShortNonLatinStoryNotesRemainUsable()
        => Assert.True(ScriptScenePlanning.Valid(new(["鍵を拾う"], [], [], "出発する", "")));

    [Fact]
    public void SequenceContextListsUseTheTextBudgetWithoutFixedEntryCounts()
    {
        var plan = Brief() with {
            Continuity = Enumerable.Range(1, 25).Select(i => $"Object {i} stays in the garden.").ToArray(),
            Withhold = Enumerable.Range(1, 18).Select(i => $"The purpose of object {i}.").ToArray(),
            Details = Enumerable.Range(1, 65).Select(i => $"Marker {i} is brass.").ToArray()
        };
        var raw = JsonSerializer.Serialize(plan, AtomicJsonFile.Options);
        var inspection = ScriptScenePlanning.Inspect(raw);
        Assert.True(inspection.CanUse); Assert.Empty(inspection.Warnings);
        var parsed = ScriptScenePlanning.Parse(raw)!;
        Assert.Equal(plan.Continuity, parsed.Continuity);
        Assert.Equal(plan.Withhold, parsed.Withhold);
        Assert.Equal(plan.Details, parsed.Details);
        foreach (var name in new[] { "continuity", "withhold", "details" })
            Assert.False(ScriptScenePlanning.PlanningSchema.GetProperty("properties").GetProperty(name).TryGetProperty("maxItems", out _));
    }

    [Fact]
    public void LongCompleteBriefsHaveApprovableWarningsWithoutLosingTheirText()
    {
        var longBeat = string.Join(" ", Enumerable.Repeat("The traveller follows the river and notices a leaf emblem.", 210));
        var raw = JsonSerializer.Serialize(Brief() with { Beats = [longBeat] }, AtomicJsonFile.Options);
        var inspected = ScriptScenePlanning.Inspect(raw);
        Assert.True(inspected.CanUse); Assert.Empty(inspected.Errors);
        Assert.Contains(inspected.Warnings, w => w.Contains("entry 1") && w.Contains(ScriptScenePlanning.MaximumItemCharacters.ToString("N0")));
        Assert.Contains(inspected.Warnings, w => w.Contains(ScriptScenePlanning.MaximumCharacters.ToString("N0")));
        Assert.Equal(longBeat, inspected.Plan!.Beats[0]);
        Assert.Null(ScriptScenePlanning.Parse(raw)); // Automatic use still follows the normal budget.
    }

    [Theory]
    [InlineData("{\"beats\":[\"The traveller finds a key.\"]")]
    [InlineData("{\"beats\":[\"The traveller finds a key.\"]}")]
    public void BrokenOrMissingFieldsNeedCorrectionInsteadOfApproval(string raw)
    {
        var inspected = ScriptScenePlanning.Inspect(raw);
        Assert.False(inspected.CanUse); Assert.NotEmpty(inspected.Errors);
        if (!raw.EndsWith('}')) Assert.Contains("line 1", inspected.Errors[0]);
    }

    [Fact]
    public void AValidJsonPrefixFromAnUnfinishedResponseCannotBeApprovedAutomatically()
    {
        var inspected = ScriptScenePlanning.Inspect(JsonSerializer.Serialize(Brief()), "length");
        Assert.False(inspected.CanUse); Assert.Contains(inspected.Errors, e => e.Contains("reply limit"));
    }

    [Theory]
    [InlineData(1000, 8192)]
    [InlineData(16000, 12288)]
    [InlineData(24000, 16384)]
    public void DefaultReplyBudgetGrowsWithSourceVolumeWithinItsBounds(int characters, int budget)
        => Assert.Equal(budget, ScriptScenePlanning.DraftReplyBudget(Context() with { Passages = [new(new string('a', characters), [1])] }));

    [Fact]
    public void DefaultReplyBudgetAccountsForNonAsciiSourceText()
        => Assert.Equal(16384, ScriptScenePlanning.DraftReplyBudget(Context() with { Passages = [new(new string('鍵', 8000), [1])] }));
}
