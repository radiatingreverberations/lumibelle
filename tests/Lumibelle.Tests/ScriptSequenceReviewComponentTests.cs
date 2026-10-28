using Bunit;
using lumibelle.Components.Script;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed class ScriptSequenceReviewComponentTests : BunitContext
{
    public ScriptSequenceReviewComponentTests() => Services.AddSingleton<MarkdownRenderer>();
    [Theory]
    [InlineData(ScriptSequenceReviewState.Checking, "Checking draft")]
    [InlineData(ScriptSequenceReviewState.Repairing, "Repairing findings")]
    [InlineData(ScriptSequenceReviewState.Passed, "Passed")]
    [InlineData(ScriptSequenceReviewState.NeedsAttention, "Needs attention")]
    public void ThirdStageMakesAutomaticCheckingAndRepairVisible(ScriptSequenceReviewState state, string label)
    {
        var document = ScriptFixtures.Document();
        var run = new AssistantRun { Target = ScriptStructure.Capture(document, ScriptScope.Document, null), Model = "Sample model",
            Status = state is ScriptSequenceReviewState.Checking or ScriptSequenceReviewState.Repairing ? AssistantRunStatus.Running : AssistantRunStatus.Completed };
        var report = new ScriptSequenceReview(state, state == ScriptSequenceReviewState.Checking ? 0 : 1, [],
            [new("screenplay", "note", "A short optional rhythm suggestion.", [], [])]);
        var frame = Render<SequenceReviewFrame>(p => p.Add(c => c.Run, run).Add(c => c.ReviewedSequence, true).Add(c => c.Review, report));
        Assert.Equal(3, frame.FindAll(".planning-stages > li").Count);
        Assert.Contains("Review draft", frame.Find("[aria-current=step]").TextContent);
        Assert.Contains(label, frame.Find("[aria-current=step]").TextContent);
        Assert.Equal(state == ScriptSequenceReviewState.NeedsAttention, frame.Find(".sequence-quality-review").HasAttribute("open"));
        Assert.Contains("optional rhythm", frame.Find(".review-note").TextContent);
    }

    [Fact]
    public void FailedReviewEvidenceShowsTheCauseAndReadOnlyRecoveryBeforeTheDraft()
    {
        var run = new AssistantRun { Status = AssistantRunStatus.Failed, Error = "Beat check 1 must use ID B1, a reason, and exact quotations.", Target = ScriptStructure.Capture(ScriptFixtures.Document(), ScriptScope.Document, null) };
        var report = new ScriptSequenceReview(ScriptSequenceReviewState.NeedsAttention, 0, [], [], ScriptSequenceDraftingTests.RedundantReview());
        var frame = Render<SequenceReviewFrame>(p => p.Add(c => c.Run, run).Add(c => c.ReviewedSequence, true).Add(c => c.Review, report));
        var alert = frame.Find("[role=alert]").TextContent;
        Assert.Contains("Automatic review could not be verified", alert);
        Assert.Contains("19 citations", alert); Assert.Contains("does not confirm a screenplay error", alert);
        Assert.Contains("without another AI call", alert);
        Assert.False(frame.Find(".sequence-quality-review details").HasAttribute("open"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ProviderFailuresAreVisibleInTheMainBannerAndHistoricalLabelsAreCorrected(bool planning)
    {
        const string original = "OpenRouter reports insufficient credits for this request.";
        var run = new AssistantRun { Status = AssistantRunStatus.Failed, Error = original,
            Target = ScriptStructure.Capture(ScriptFixtures.Document(), ScriptScope.Document, null), Output = "" };
        var frame = Render<SequenceReviewFrame>(p => p.Add(c => c.Run, run).Add(c => c.Planning, planning).Add(c => c.ProviderName, "Studio AI"));
        var banner = frame.Find(".planning-stopped[role=alert]");
        Assert.Contains("Studio AI", banner.TextContent);
        Assert.Contains("HTTP 402", banner.TextContent);
        Assert.DoesNotContain("OpenRouter", banner.TextContent);
        Assert.Contains("No response text was received.", banner.TextContent);
        var detail = banner.QuerySelector(".planning-error-detail")!;
        Assert.Null(detail.Closest("details"));
        Assert.Equal(original, run.Error);
        Assert.DoesNotContain("OpenRouter", frame.Find(".planning-response-details").TextContent);
    }
}
