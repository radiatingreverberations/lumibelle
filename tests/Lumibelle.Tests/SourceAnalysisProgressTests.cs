using Bunit;
using lumibelle.Components;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class SourceAnalysisProgressTests : BunitContext
{
    [Fact]
    public void CompletedReadingShowsCombiningProgressInsteadOfAFullReadingBar()
    {
        var run = Example();
        run = Pending(run with {
            Level = run.Notes, CombineIndex = 32,
            Combined = Enumerable.Range(0, 8).Select(i => new SourceNote("Saved summary", Enumerable.Range(i * 4 + 1, 4).ToArray())).ToList()
        }, SourceAnalysisStage.Combine, run.Notes.Skip(32).Take(4).ToList());
        var ui = Render<SourceAnalysisProgressView>(p => p.Add(c => c.Run, run));

        Assert.Equal("Combining story notes", ui.Find("strong").TextContent);
        Assert.Contains("Step 2 of 3", ui.Markup);
        Assert.Contains("32 / 38 notes combined in this pass", ui.Markup);
        Assert.Contains("notes 33–36 of 38", ui.Find("[role=status]").TextContent);
        Assert.Contains("8 summaries saved", ui.Markup);
        Assert.Contains("38 / 38 source sections", ui.Markup);
        var progress = ui.Find("progress");
        Assert.Equal("Story overview progress", progress.GetAttribute("aria-label"));
        Assert.Equal("32", progress.GetAttribute("value"));
        Assert.Equal("38", progress.GetAttribute("max"));
    }

    [Fact]
    public void AnotherCombiningPassUsesItsOwnTotalAndKeepsReadingComplete()
    {
        var run = Example();
        run = run with {
            Level = run.Notes, CombineIndex = 38,
            Combined = Enumerable.Range(0, 10).Select(i => new SourceNote(new string('a', 1700), Enumerable.Range(i * 4 + 1, Math.Min(4, 38 - i * 4)).ToArray())).ToList()
        };
        var next = SourceWorkflow.Next(Guid.NewGuid(), run);
        Assert.Equal(SourceAnalysisStage.Combine, next.Request.Stage);
        var ui = Render<SourceAnalysisProgressView>(p => p.Add(c => c.Run, Pending(next.Run, next.Request.Stage, next.Request.Inputs)));

        Assert.Contains("0 / 10 notes combined in this pass", ui.Markup);
        Assert.Contains("38 / 38 source sections", ui.Markup);
        Assert.Contains("several passes", ui.Markup);
        Assert.Equal("10", ui.Find("progress").GetAttribute("max"));
    }

    [Theory]
    [InlineData(SourceRunState.Running)]
    [InlineData(SourceRunState.Paused)]
    public void OutlineProgressDoesNotPresentReadingCompletionAsOverallCompletion(SourceRunState state)
    {
        var run = Pending(Example() with { State = state }, SourceAnalysisStage.Outline, [new("Story overview", Enumerable.Range(1, 38).ToArray())]);
        var ui = Render<SourceAnalysisProgressView>(p => p.Add(c => c.Run, run));
        Assert.Contains("Step 3 of 3", ui.Markup);
        Assert.Contains("Choosing acts and scenes", ui.Markup);
        if (state == SourceRunState.Running) {
            Assert.Equal("Proposing adaptation outline", ui.Find("strong").TextContent);
            Assert.Null(ui.Find("progress").GetAttribute("value"));
        } else {
            Assert.Equal("Paused", ui.Find("strong").TextContent);
            Assert.Empty(ui.FindAll("progress"));
        }
    }

    [Fact]
    public void ReadingShowsItIsOnlyTheFirstStepAndReviewHasNoRunningProgress()
    {
        var run = Example();
        var ui = Render<SourceAnalysisProgressView>(p => p.Add(c => c.Run, run with { Notes = run.Notes.Take(5).ToList() }));
        Assert.Contains("Step 1 of 3", ui.Markup);
        Assert.Equal("5", ui.Find("progress").GetAttribute("value"));
        Assert.Equal("Source reading progress", ui.Find("progress").GetAttribute("aria-label"));

        var ready = Render<SourceAnalysisProgressView>(p => p.Add(c => c.Run, run with { State = SourceRunState.Review }));
        Assert.Equal("Ready for review", ready.Find("strong").TextContent);
        Assert.Empty(ready.FindAll("progress"));
    }

    private static SourceAnalysisRun Example()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 38).Select(i => $"Chapter {i}\nThe story continues."));
        return new() {
            Title = "The river key", Text = text, Direction = "", Fingerprint = SourceAnalysis.Fingerprint("The river key", text, ""),
            Model = new(AiBackend.OpenRouter, "mock/script", "Mock writer"), TabId = Guid.NewGuid(), PromptBudget = 3000,
            Chunks = SourceAnalysis.Split(text, 1000),
            Notes = Enumerable.Range(1, 38).Select(i => new SourceNote("Saved section note", [i])).ToList()
        };
    }
    private static SourceAnalysisRun Pending(SourceAnalysisRun run, SourceAnalysisStage stage, List<SourceNote> inputs) =>
        run with { Pending = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.SourceAnalysis, run.Model.Backend, new(Guid.NewGuid()),
            "Preview", "Source request", run.TabId, new { task = new SourceAnalysisRequest(Guid.NewGuid(), run.Id, stage, run.Title, run.Direction, inputs) }) };
}
