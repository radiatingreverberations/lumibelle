using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class SourceAnalysisTests
{
    [Fact]
    public void SourceSectionEditsKeepValidDistinctReferences()
        => Assert.Equal([6, 7, 10], SourceAnalysis.ParseSourceSections("10, 6; 7 6", 35));
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("36")]
    [InlineData("-1")]
    [InlineData("1, 99")]
    [InlineData("1-3")]
    [InlineData("one")]
    public void InvalidSourceSectionEditsCannotCreateBrokenLinks(string text)
        => Assert.Throws<WorkspaceStoreException>(() => SourceAnalysis.ParseSourceSections(text, 35));
    [Fact]
    public void InterleavedSourceReferencesWarnAboutPossiblePrematureReveals()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 5).Select(i => $"Chapter {i}\nThe story continues."));
        var run = Run(text, SourceAnalysis.Split(text, 1000));
        var outline = Outline();
        outline.Acts[0].Scenes = [
            new() { Title = "Early merge", Summary = "A setup and a late discovery are combined.", Sources = [1, 5] },
            new() { Title = "The escape", Summary = "The protagonist gets free.", Sources = [3] }
        ];
        SourceAnalysis.ValidateOutline(outline, run.Chunks.Select(c => c.Number).ToHashSet());
        Assert.Contains(SourceAnalysis.Warnings(run, outline), w => w.Contains("Early merge") && w.Contains("sections 3") && w.Contains("reveal order"));
    }
    [Fact]
    public void FlashbacksAndContinuousMontagesDoNotTriggerAnInterleavingWarning()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 5).Select(i => $"Chapter {i}\nThe story continues."));
        var run = Run(text, SourceAnalysis.Split(text, 1000));
        var outline = Outline();
        outline.Acts[0].Scenes = [
            new() { Title = "Frame", Summary = "A future frame.", Sources = [5] },
            new() { Title = "Flashback", Summary = "The earlier beginning.", Sources = [1] },
            new() { Title = "Montage", Summary = "A continuous period.", Sources = [2, 3, 4] },
            new() { Title = "Revisit", Summary = "A new angle on a remembered event.", Sources = [3] }
        ];
        Assert.Empty(SourceAnalysis.Warnings(run, outline));
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SplittingPreservesEveryCharacterAndRespectsHeadingsAndBudget(string newline)
    {
        var text = newline + "Prologue" + newline + "The key." + newline + newline + "Chapter 1" + newline +
            string.Join(newline + newline, Enumerable.Repeat("Eira follows the river. 森の映画 🌲", 300)) +
            newline + "Chapter 2" + newline + "The key opens the gate.";
        var chunks = SourceAnalysis.Split(text, 300);
        Assert.True(chunks.Count > 3);
        Assert.Equal(text, string.Concat(chunks.Select(c => text.Substring(c.Start, c.Length))));
        Assert.All(chunks, c => { Assert.True(SourceAnalysis.Tokens(text.Substring(c.Start, c.Length)) <= 300); Assert.False(char.IsLowSurrogate(text[c.Start])); });
        Assert.Contains(chunks, c => c.Heading == "Chapter 2" && text.Substring(c.Start, c.Length).Contains("opens the gate"));
        Assert.Equal("Prologue", chunks[0].Heading);
    }
    [Fact]
    public void ProseBeginningWithPartBookOrActDoesNotCreateFalseHeadings()
    {
        const string text = "Title\n\nPrologue\nAn opening.\n\nChapter One\nA memory was\npart of the plan.\nbook tickets tomorrow.\nact naturally.\n\nPart II: The return\nShe returns.\n\nChapter Twenty-One\nThe ending.\n\nEpilogue — Home\nThe gate closes.";
        var chunks = SourceAnalysis.Split(text, 1000);
        Assert.Equal(["Opening", "Prologue", "Chapter One", "Part II: The return", "Chapter Twenty-One", "Epilogue — Home"], chunks.Select(c => c.Heading));
        Assert.Equal(text, string.Concat(chunks.Select(c => text.Substring(c.Start, c.Length))));
        Assert.Contains("part of the plan.", text.Substring(chunks[2].Start, chunks[2].Length));
    }
    [Fact]
    public void OversizedUnbrokenParagraphIsSplitWithoutLosingText()
    {
        var text = string.Concat(Enumerable.Repeat("🌲", 800)) + new string('x', 9000);
        var chunks = SourceAnalysis.Split(text, 300);
        Assert.Equal(text, string.Concat(chunks.Select(c => text.Substring(c.Start, c.Length))));
        Assert.All(chunks, c => Assert.InRange(SourceAnalysis.Tokens(text.Substring(c.Start, c.Length)), 1, 300));
    }
    [Fact]
    public void HierarchyCombinesBoundedNotesBeforePlanningAndRetainsEverySourceId()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 40).Select(n => $"Chapter {n}\n" + new string('x', 100)));
        var chunks = SourceAnalysis.Split(text, 1000);
        var run = Run(text, chunks) with { Notes = chunks.Select(c => new SourceNote(new string('a', 1700), [c.Number])).ToList() };
        var steps = 0;
        while (true) {
            var next = SourceWorkflow.Next(Guid.NewGuid(), run); run = next.Run;
            Assert.True(SourceAnalysis.RequestTokens(next.Request) <= run.PromptBudget);
            if (next.Request.Stage == SourceAnalysisStage.Outline) {
                Assert.Equal(chunks.Select(c => c.Number), next.Request.Inputs.SelectMany(n => n.Sources));
                break;
            }
            Assert.Equal(SourceAnalysisStage.Combine, next.Request.Stage);
            run = SourceWorkflow.Consume(run, next.Request, new(new string('b', 1700)));
            Assert.True(++steps < 100);
        }
        Assert.True(steps > 1);
        Assert.Equal(chunks.Count, run.Notes.Count);
    }
    [Theory]
    [InlineData(SourceAnalysisStage.Read)]
    [InlineData(SourceAnalysisStage.Combine)]
    public void SummaryLengthTargetDoesNotRejectCompleteUsefulNotes(SourceAnalysisStage stage)
    {
        // Reproduces the first live run: valid JSON with a 2,356-character summary.
        var summary = new string('a', 2356);
        var request = new SourceAnalysisRequest(Guid.NewGuid(), Guid.NewGuid(), stage, "Story", "", [new("Source", [1])]);
        var raw = JsonSerializer.Serialize(new SourceAnalysisResult(summary), AtomicJsonFile.Options);
        Assert.Equal(summary, SourceAnalysis.Parse(raw, request).Summary);
        Assert.Throws<WorkspaceStoreException>(() => SourceAnalysis.Parse(
            JsonSerializer.Serialize(new SourceAnalysisResult(new string('a', SourceAnalysis.MaximumNote + 1)), AtomicJsonFile.Options), request));
    }
    [Fact]
    public void LongOrientationIsExcerptedWithoutTruncatingTheSourceOrSavedNotes()
    {
        var text = "Chapter 1\nThe key.\n\nChapter 2\n" + new string('x', 6000);
        var chunks = SourceAnalysis.Split(text, 2500);
        var note = string.Concat(Enumerable.Repeat("森の映画 🌲 ", 800)) + "The gate is still locked.";
        var run = Run(text, chunks) with { Notes = [new(note, [1])] };
        var next = SourceWorkflow.Next(Guid.NewGuid(), run);
        Assert.Equal(SourceAnalysisStage.Read, next.Request.Stage);
        Assert.True(SourceAnalysis.RequestTokens(next.Request) <= run.PromptBudget);
        Assert.Equal(text.Substring(chunks[1].Start, chunks[1].Length), next.Request.Inputs[0].Text);
        Assert.StartsWith("Excerpt from previous section notes", next.Request.PreviousContext);
        Assert.EndsWith("The gate is still locked.", next.Request.PreviousContext);
        Assert.Equal(note, Assert.Single(next.Run.Notes).Text);
    }
    [Fact]
    public void LongerNotesStillReduceIntoAWholeStoryOutlineWithinBudget()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 38).Select(n => $"Chapter {n}\nEvents."));
        var chunks = SourceAnalysis.Split(text, 1000);
        var summary = new string('a', 2356);
        var run = Run(text, chunks) with { Notes = chunks.Select(c => new SourceNote(summary, [c.Number])).ToList() };
        var steps = 0;
        while (true) {
            var next = SourceWorkflow.Next(Guid.NewGuid(), run); run = next.Run;
            Assert.True(SourceAnalysis.RequestTokens(next.Request) <= run.PromptBudget);
            if (next.Request.Stage == SourceAnalysisStage.Outline) {
                Assert.Equal(chunks.Select(c => c.Number), next.Request.Inputs.SelectMany(n => n.Sources));
                break;
            }
            run = SourceWorkflow.Consume(run, next.Request, new(summary));
            Assert.True(++steps < 100);
        }
        Assert.All(run.Notes, note => Assert.Equal(summary, note.Text));
    }
    [Fact]
    public void InvalidCitationsAreFatalButUncoveredSectionsRemainReviewWarnings()
    {
        var request = new SourceAnalysisRequest(Guid.NewGuid(), Guid.NewGuid(), SourceAnalysisStage.Outline, "Story", "", [new("Story notes", [1, 2])]);
        var outline = Outline();
        var valid = JsonSerializer.Serialize(new SourceAnalysisResult("", outline), AtomicJsonFile.Options);
        Assert.NotNull(SourceAnalysis.Parse(valid, request).Outline);
        var run = Run("Chapter 1\nOne\nChapter 2\nTwo", SourceAnalysis.Split("Chapter 1\nOne\nChapter 2\nTwo", 1000));
        Assert.Single(SourceAnalysis.Warnings(run, outline));
        outline.ReviewedOmissions = [2];
        Assert.Empty(SourceAnalysis.Warnings(run, outline));
        Assert.Empty(SourceAnalysis.UnlinkedSources(run, outline));
        // An AI response cannot acknowledge missing coverage on the reviewer's behalf.
        var generated = SourceAnalysis.Parse(JsonSerializer.Serialize(new SourceAnalysisResult("", outline), AtomicJsonFile.Options), request).Outline!;
        Assert.Empty(generated.ReviewedOmissions);
        Assert.Single(SourceAnalysis.Warnings(run, generated));
        outline.ReviewedOmissions = [];
        Assert.Equal([2], SourceAnalysis.UnlinkedSources(run, outline));
        outline.ReviewedOmissions = [99];
        Assert.Throws<WorkspaceStoreException>(() => SourceAnalysis.ValidateOutline(outline, [1, 2]));
        outline.ReviewedOmissions = [];
        outline.Acts[0].Scenes[0].Sources = [99];
        Assert.Throws<WorkspaceStoreException>(() => SourceAnalysis.Parse(JsonSerializer.Serialize(new SourceAnalysisResult("", outline), AtomicJsonFile.Options), request));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"summary\":null}")]
    [InlineData("{\"summary\":\"")]
    public void IncompleteAnalysisCannotAdvance(string raw) =>
        Assert.Throws<WorkspaceStoreException>(() => SourceAnalysis.Parse(raw, new(Guid.NewGuid(), Guid.NewGuid(), SourceAnalysisStage.Read, "", "", [new("Source", [1])])));

    internal static SourceOutline Outline() => new() { Title = "The key", Premise = "A courier finds the missing gate.", Approach = "Follow the courier.",
        Acts = [new() { Title = "Discovery", Purpose = "Find the route.", Scenes = [new() { Title = "The river", Summary = "The courier follows the river to the gate.", Sources = [1] }] }] };
    internal static SourceAnalysisRun Run(string text, List<SourceChunk> chunks) => new() {
        Title = "", Direction = "", Text = text, Fingerprint = SourceAnalysis.Fingerprint("", text, ""),
        Model = new(AiBackend.OpenRouter, "test/model", "Test"), TabId = Guid.NewGuid(), PromptBudget = 3000, Chunks = chunks
    };
}
