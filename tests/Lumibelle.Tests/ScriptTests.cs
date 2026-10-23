using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScriptTests
{
    [Theory]
    [InlineData(ScriptScope.Document)]
    [InlineData(ScriptScope.Scene)]
    [InlineData(ScriptScope.Passage)]
    public void TargetComparisonAcceptsSpanMergingButKeepsTextStyleAndStructureChangesVisible(ScriptScope scope)
    {
        var doc = ScriptFixtures.Document();
        doc.Blocks[1] = doc.Blocks[1] with { Spans = [new("Eira "), new("waits."), new("", true), new("Quietly.", true), new("", false, true)] };
        var target = ScriptStructure.Capture(doc, scope, new(doc.Blocks[1].Id, 0, doc.Blocks[1].Id, 5));
        var normalized = doc.Copy();
        normalized.Blocks[1] = normalized.Blocks[1] with { Spans = [new("Eira waits."), new("Quietly.", true)] };
        Assert.NotEqual(ScriptStructure.Fingerprint(doc.Blocks), ScriptStructure.Fingerprint(normalized.Blocks));
        Assert.True(ScriptStructure.Matches(normalized, target));
        foreach (var change in new[] {
            normalized.Blocks[1] with { Spans = [new("Eira moves."), new("Quietly.", true)] },
            normalized.Blocks[1] with { Spans = [new("Eira waits.", true), new("Quietly.", true)] },
            normalized.Blocks[1] with { Spans = [new("Eira waits."), new("Quietly.", true, true)] },
            normalized.Blocks[1] with { Kind = ScriptBlockKind.Dialogue },
            normalized.Blocks[1] with { Id = Guid.NewGuid() }
        }) {
            var changed = normalized.Copy(); changed.Blocks[1] = change;
            Assert.False(ScriptStructure.Matches(changed, target));
        }
    }

    [Fact]
    public void OutlineIsDerivedFromOrderedBlocksAndOptionalActs()
    {
        var doc = ScriptFixtures.Document();
        var act = ScriptBlock.Create(ScriptBlockKind.Act, "A small mistake");
        var second = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. GARDEN — DAWN");
        doc = doc with { Blocks = [act, .. doc.Blocks, second, ScriptBlock.Create(ScriptBlockKind.Action, "A quiet breakfast.")] };
        var sections = ScriptStructure.Sections(doc.Blocks);
        Assert.Equal(3, sections.Count); Assert.Equal(doc.Blocks.Count, sections[0].Count);
        Assert.Equal(new[] { 1, 2 }, sections.Where(s => s.Kind == ScriptBlockKind.Scene).Select(s => s.Number));
        Assert.Equal(act.Id, sections[2].ActId); Assert.Equal(second.Id, sections[2].Id);
        Assert.Equal(4, sections[1].Count);
    }
    [Fact]
    public void PassageReplacementKeepsSurroundingTextMarksAndOtherScenes()
    {
        var doc = ScriptFixtures.Document();
        doc.Blocks[1] = doc.Blocks[1] with { Spans = [new("A ", true), new("mouse"), new(" appears.", false, true)] };
        var target = ScriptStructure.Capture(doc, ScriptScope.Passage, new(doc.Blocks[1].Id, 2, doc.Blocks[1].Id, 7));
        var run = Proposal(doc, target, [ScriptBlock.Create(ScriptBlockKind.Action, "tiny mouse")]);
        var result = ScriptProposals.Apply(doc, run);
        Assert.Equal("A tiny mouse appears.", result.Blocks[1].Text);
        Assert.True(result.Blocks[1].Spans[0].Bold); Assert.True(result.Blocks[1].Spans[^1].Italic);
        Assert.Equal(doc.Blocks[1].Id, result.Blocks[1].Id);
        Assert.Equal(ScriptStructure.Fingerprint([doc.Blocks[2]]), ScriptStructure.Fingerprint([result.Blocks[2]])); Assert.Equal(doc.Brief, result.Brief);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(result, run));
    }
    [Fact]
    public void ReverseSelectionAcrossBlocksPreservesPrefixAndSuffix()
    {
        var doc = ScriptFixtures.Document(); var a = doc.Blocks[1]; var b = doc.Blocks[3];
        var target = ScriptStructure.Capture(doc, ScriptScope.Passage, new(b.Id, 3, a.Id, 2));
        Assert.Equal("mouse appears in a saucepan.", ScriptStructure.TargetBlocks(target)[0].Text);
        var result = ScriptProposals.Apply(doc, Proposal(doc, target, [ScriptBlock.Create(ScriptBlockKind.Action, "magic") ]));
        Assert.Equal("A magic called?", result.Blocks[1].Text); Assert.Equal(2, result.Blocks.Count);
    }
    [Theory]
    [InlineData(ScriptScope.Document)]
    [InlineData(ScriptScope.Scene)]
    [InlineData(ScriptScope.Act)]
    public void ScopedReplacementRetainsHeadingIdentity(ScriptScope scope)
    {
        var doc = ScriptFixtures.Document();
        if (scope == ScriptScope.Act) doc.Blocks.Insert(0, ScriptBlock.Create(ScriptBlockKind.Act, "ACT ONE"));
        var target = ScriptStructure.Capture(doc, scope, new(doc.Blocks[1].Id, 0, doc.Blocks[1].Id, 0));
        var proposal = doc.Blocks.Select(b => b.Copy() with { Id = Guid.NewGuid() }).ToList();
        proposal[^1] = proposal[^1] with { Spans = [new("Soup again?")] };
        var result = ScriptProposals.Apply(doc, Proposal(doc, target, proposal));
        Assert.Equal(doc.Blocks[0].Id, result.Blocks[0].Id); Assert.Equal("Soup again?", result.Blocks[^1].Text);
    }
    [Fact]
    public void ChangedDeletedOrExpandedTargetCannotBeAppliedButOtherEditsCan()
    {
        var doc = ScriptFixtures.Document();
        doc.Blocks.AddRange([ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. GARDEN — DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "Breakfast.")]);
        var target = ScriptStructure.Capture(doc, ScriptScope.Scene, new(doc.Blocks[1].Id, 0, doc.Blocks[1].Id, 0));
        var run = Proposal(doc, target, target.OriginalBlocks);
        var otherEdit = doc.Copy(); otherEdit.Blocks[^1] = otherEdit.Blocks[^1] with { Spans = [new("Lunch.")] };
        Assert.True(ScriptStructure.Matches(otherEdit, target)); Assert.Equal("Lunch.", ScriptProposals.Apply(otherEdit, run).Blocks[^1].Text);
        var changed = doc.Copy(); changed.Blocks[1] = changed.Blocks[1] with { Spans = [new("Changed")] };
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(changed, run));
        changed = doc.Copy(); changed.Blocks.RemoveAt(0); Assert.False(ScriptStructure.Matches(changed, target));
        changed = doc.Copy(); changed.Blocks.Insert(2, ScriptBlock.Create(ScriptBlockKind.Action, "New beat")); Assert.False(ScriptStructure.Matches(changed, target));
    }
    [Fact]
    public void ContinueInsertsAfterTargetWithoutReplacingIt()
    {
        var doc = ScriptFixtures.Document(); var target = ScriptStructure.Capture(doc, ScriptScope.Document, null);
        var run = Proposal(doc, target, [ScriptBlock.Create(ScriptBlockKind.Action, "The kettle screams.")]) with { Operation = WritingOperation.Continue };
        var result = ScriptProposals.Apply(doc, run);
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks), ScriptStructure.Fingerprint(result.Blocks.Take(doc.Blocks.Count)));
        Assert.Equal("The kettle screams.", result.Blocks[^1].Text);
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[{\"kind\":\"Camera\",\"spans\":[{\"text\":\"a\"}]}]")]
    [InlineData("[{\"spans\":[{\"text\":\"a\"}]}]")]
    [InlineData("[{\"kind\":\"Action\",\"spans\":[{\"text\":null}]}]")]
    [InlineData("[{\"kind\":\"Action\",\"spans\":[{\"text\":\"unfinished\"}]")]
    public void InvalidAndPartialScreenplaysAreNotImportable(string text) => Assert.Null(ScriptAssistant.ParseBlocks(text));
    [Fact]
    public void ProposalParserPreservesUnicodeAndCreatesFreshIds()
    {
        var id = Guid.NewGuid();
        var json = $$"""[{"id":"{{id}}","kind":"Scene","spans":[{"text":"  森 🌲","bold":true}]}]""";
        var block = Assert.Single(ScriptAssistant.ParseBlocks("```json\n" + json + "\n```")!);
        Assert.NotEqual(id, block.Id); Assert.Equal("  森 🌲", block.Text); Assert.True(block.Spans[0].Bold);
    }
    [Theory]
    [InlineData("**Here is the screenplay:**\n\n```json\n", "\n```")]
    [InlineData("Here is the draft.\r\n```JSON\r\n", "\r\n```\r\nLet me know if you want changes.")]
    [InlineData("A draft:\n```\n", "\n```\n")]
    public void ProposalParserAcceptsOneCompleteFencedPayloadWithCommentary(string before, string after)
    {
        const string json = """[{"kind":"Scene","spans":[{"text":"INT. KITCHEN — DAY","bold":true}]},{"kind":"Action","spans":[{"text":"A kettle whistles."}]}]""";
        var blocks = ScriptAssistant.ParseBlocks(before + json + after);
        Assert.NotNull(blocks); Assert.Equal(2, blocks.Count);
        Assert.Equal("INT. KITCHEN — DAY", blocks[0].Text); Assert.True(blocks[0].Spans[0].Bold);
        Assert.Equal("A kettle whistles.", blocks[1].Text);
    }
    [Theory]
    [InlineData("Here is the draft:\n```json\n", "")]
    [InlineData("```python\n", "\n```")]
    [InlineData("```json\n", "\n```\nOr this:\n```json\n[]\n```")]
    [InlineData("```json\n", "\n```\n```json\nunfinished")]
    [InlineData("```json\n", ",\n```")]
    public void ProposalParserRejectsIncompleteOrAmbiguousWrappers(string before, string after)
    {
        const string json = """[{"kind":"Action","spans":[{"text":"A kettle whistles."}]}]""";
        Assert.Null(ScriptAssistant.ParseBlocks(before + json + after));
    }
    [Fact]
    public void FullScriptBriefAndRecentDiscussionAreContextButNotAcceptedProposals()
    {
        var doc = ScriptFixtures.Document();
        var target = ScriptStructure.Capture(doc, ScriptScope.Document, null);
        var run = Proposal(doc, target, []);
        var request = new ScriptAssistantRequest(run, doc, Enumerable.Range(0, 30).Select(i => new ConversationMessage("user", $"message-{i:00}")).ToList());
        var prompt = string.Join("\n", ScriptAssistant.BuildMessages(request).Select(m => m.Text));
        Assert.DoesNotContain(doc.Brief.Idea, prompt); Assert.Contains("You called?", prompt); Assert.DoesNotContain("h3-practical-v1", prompt);
        Assert.DoesNotContain("message-00", prompt); Assert.Contains("message-29", prompt);
        Assert.True(ScriptAssistant.EstimateInputTokens(request with { Run = run with { Instructions = new string('x', 4000) } }) > ScriptAssistant.EstimateInputTokens(request) + 900);
        Assert.Empty(ScriptAssistant.Conversation([run with { Output = "Unaccepted draft" }]));
        Assert.Contains("## INT. KITCHEN", ScriptStructure.Markdown(doc.Blocks));
    }
    [Fact]
    public async Task TypingDuringSaveAndFailuresKeepLatestDraft()
    {
        var store = new FakeScriptStore(); using var session = new ScriptEditingSession(store, TimeProvider.System);
        await session.LoadAsync(store.Document.ProjectId);
        var started = new TaskCompletionSource(); var release = new TaskCompletionSource();
        store.BeforeSave = async () => { started.TrySetResult(); await release.Task; };
        session.Edit(session.Document with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "first")] });
        var saving = session.SaveAsync(); await started.Task;
        session.Edit(session.Document with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "newer")] }); release.SetResult();
        Assert.True(await saving); Assert.Equal("newer", store.Document.Blocks[0].Text); Assert.False(session.Dirty);
        store.SaveError = new WorkspaceStoreException("Disk full"); session.Edit(session.Document with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "keep me")] });
        Assert.False(await session.SaveAsync()); Assert.Equal("keep me", session.Document.Blocks[0].Text); Assert.True(session.Dirty);
        store.SaveError = null; Assert.True(await session.SaveAsync()); Assert.Equal("keep me", store.Document.Blocks[0].Text);
    }
    [Fact]
    public void InvalidIdentityAndOversizedBlocksAreRejected()
    {
        var b = ScriptBlock.Create(ScriptBlockKind.Action, "Text");
        Assert.Throws<WorkspaceStoreException>(() => ScriptStructure.ValidateBlocks([b, b]));
        Assert.Throws<WorkspaceStoreException>(() => ScriptStructure.ValidateBlocks([b with { Id = Guid.Empty }]));
        Assert.Throws<WorkspaceStoreException>(() => ScriptStructure.ValidateBlocks([ScriptBlock.Create(ScriptBlockKind.Action, new string('x', 500001))]));
    }
    internal static AssistantRun Proposal(ScriptDocument doc, ScriptTarget target, List<ScriptBlock> blocks) => new() { Operation = WritingOperation.Revise,
        Status = AssistantRunStatus.Completed, Target = target, SourceFingerprint = ScriptStructure.ContextFingerprint(doc), Proposal = blocks };
}
