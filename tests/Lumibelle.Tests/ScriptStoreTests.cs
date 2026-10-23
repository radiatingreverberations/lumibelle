using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Story;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed class ScriptStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.Tests", Guid.NewGuid().ToString("D"));
    private readonly ApplicationSession _session = new();
    private FileProjectStore Projects => new(Options.Create(new ProjectStorageOptions()), Environment, TimeProvider.System, NullLogger<FileProjectStore>.Instance);
    private IHostEnvironment Environment => new StorageTestEnvironment(_root);
    private ProjectFiles Files => new(Options.Create(new ProjectStorageOptions()), Environment, Projects);
    private FileScriptStore Stories => new(Files, TimeProvider.System);
    private string ProjectPath(Guid id) => Path.Combine(_root, "App_Data", "Projects", id.ToString("D"));

    [Fact]
    public async Task NewProjectOpensEmptyAndUnicodeAndBlockOrderSurviveReopening()
    {
        var project = await Projects.CreateAsync(new("Film"), TestContext.Current.CancellationToken);
        var blank = await Stories.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(0, blank.Revision);
        Assert.False(File.Exists(Path.Combine(ProjectPath(project.Id), "script.json")));
        var draft = blank with { Brief = new() { Idea = "  森の映画 🌲\n\n  " }, Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "Två"), ScriptBlock.Create(ScriptBlockKind.Dialogue, "  dialogue\n"), ScriptBlock.Create(ScriptBlockKind.Scene, "One")] };
        var saved = await Stories.SaveAsync(draft, 0, cancellationToken: TestContext.Current.CancellationToken);
        var reopened = await Stories.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Empty(reopened.Brief.Idea);
        Assert.DoesNotContain("brief", await File.ReadAllTextAsync(Path.Combine(ProjectPath(project.Id), "script.json"), TestContext.Current.CancellationToken));
        Assert.Equal(ScriptStructure.Fingerprint(draft.Blocks), ScriptStructure.Fingerprint(reopened.Blocks));
        Assert.Equal(1, saved.Revision);
        Assert.Empty(Directory.GetFiles(ProjectPath(project.Id), "*.tmp"));
    }

    [Fact]
    public async Task RecoveryPrecedesAiChangeAndRestoreKeepsCurrentVersion()
    {
        var project = await Projects.CreateAsync(new("Film"), TestContext.Current.CancellationToken);
        var initial = await Stories.SaveAsync(new() { ProjectId = project.Id, Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "original")] }, 0, cancellationToken: TestContext.Current.CancellationToken);
        await Stories.SaveAsync(initial with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "expanded")] }, 1, "Before Expand", TestContext.Current.CancellationToken);
        var version = Assert.Single(await Stories.ListRecoveryAsync(project.Id, TestContext.Current.CancellationToken));
        Assert.Equal("original", version.Document.Blocks[0].Text);
        var restored = await Stories.RestoreAsync(project.Id, version.Id, 2, TestContext.Current.CancellationToken);
        Assert.Equal("original", restored.Blocks[0].Text);
        Assert.Equal(3, restored.Revision);
        Assert.Contains(await Stories.ListRecoveryAsync(project.Id, TestContext.Current.CancellationToken), item => item.Document.Blocks[0].Text == "expanded");
    }

    [Fact]
    public async Task RedraftRecoveryRestoresThePriorSequenceAndItsSourceLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("A river journey"), ct);
        var scene = new ScriptSourceScene(Guid.NewGuid(), 1, 0, 0);
        var blocks = new List<ScriptBlock> { ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "She waits."),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. GARDEN - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "The gate opens.") };
        var initial = await Stories.SaveAsync(new() { ProjectId = project.Id, Blocks = blocks, SourceSceneLinks = [new(scene, blocks.Take(2).Select(b => b.Id).ToArray())] }, 0, cancellationToken: ct);
        var run = new AssistantRun { Operation = WritingOperation.Rewrite, Status = AssistantRunStatus.Completed, SourceScene = scene,
            Target = ScriptAdaptation.SequenceTarget(initial, scene), Proposal = [ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. BOAT - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "She rows.")] };
        var next = ScriptProposals.Apply(initial, run);
        var saved = await Stories.SaveAsync(next, initial.Revision, "Before replacing sequence 1.1", ct);
        var recovery = Assert.Single(await Stories.ListRecoveryAsync(project.Id, ct));
        var restored = await Stories.RestoreAsync(project.Id, recovery.Id, saved.Revision, ct);
        Assert.Equal(ScriptStructure.Fingerprint(initial.Blocks), ScriptStructure.Fingerprint(restored.Blocks));
        Assert.Equal(initial.SourceSceneLinks[0].BlockIds, restored.SourceSceneLinks[0].BlockIds);
        Assert.Contains(run.Id, restored.AppliedProposalIds); // Restoring must not make an old proposal applicable twice.
    }

    [Fact]
    public async Task ConflictingAndCancelledWritesLeaveSavedStoryIntact()
    {
        var project = await Projects.CreateAsync(new("Film"), TestContext.Current.CancellationToken);
        var saved = await Stories.SaveAsync(new() { ProjectId = project.Id, Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "keep")] }, 0, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Stories.SaveAsync(saved with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "stale")] }, 0, cancellationToken: TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stories.SaveAsync(saved with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "cancelled")] }, 1, cancellationToken: cancelled.Token));
        Assert.Equal("keep", (await Stories.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Blocks[0].Text);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task BadStoryIsNeverOverwritten(string content)
    {
        var project = await Projects.CreateAsync(new("Film"), TestContext.Current.CancellationToken);
        var path = Path.Combine(ProjectPath(project.Id), "script.json");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.LoadAsync(project.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.SaveAsync(new() { ProjectId = project.Id }, 0, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnpublishedFilesAreIgnoredAndWriteFailurePreservesPreviousFile()
    {
        var project = await Projects.CreateAsync(new("Film"), TestContext.Current.CancellationToken);
        var saved = await Stories.SaveAsync(new() { ProjectId = project.Id, Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "keep")] }, 0, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ProjectPath(project.Id), "script.json.abandoned.tmp"), "{", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ProjectPath(project.Id), "script-history"), "blocks recovery", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.SaveAsync(saved with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "lost")] }, 1, "AI edit", TestContext.Current.CancellationToken));
        Assert.Equal("keep", (await Stories.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Blocks[0].Text);
    }

    [Fact]
    public async Task HistoryMergesIndependentRunsAndInterruptsOnlyAfterRestart()
    {
        var project = await Projects.CreateAsync(new("Film"), TestContext.Current.CancellationToken);
        var store = new FileAssistantHistoryStore(Files, _session);
        var run = await store.SaveRunAsync(project.Id, new() { SessionId = _session.Id }, TestContext.Current.CancellationToken);
        await store.SaveRunAsync(project.Id, new() { SessionId = _session.Id, Status = AssistantRunStatus.Completed, Output = "Idea" }, TestContext.Current.CancellationToken);
        Assert.Equal(AssistantRunStatus.Running, (await new FileAssistantHistoryStore(Files, _session).LoadAsync(project.Id, TestContext.Current.CancellationToken)).Runs[0].Status);
        var restarted = await new FileAssistantHistoryStore(Files, new()).LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AssistantRunStatus.Interrupted, restarted.Runs[0].Status);
        Assert.Equal("Idea", restarted.Runs[1].Output);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveRunAsync(project.Id, run with { Output = "late" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingProjectIsNotCreatedByStorySave()
    {
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.SaveAsync(new() { ProjectId = Guid.NewGuid() }, 0, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ApprovalIsImmutableAndRestoringDraftDoesNotChangeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("Film"), ct);
        var draft = await Stories.SaveAsync(ScriptFixtures.Document(project.Id), 0, cancellationToken: ct);
        var approvedDoc = await Stories.ApproveAsync(project.Id, draft.Revision, ct);
        var approved = (await Stories.LoadApprovedAsync(project.Id, cancellationToken: ct))!;
        var edited = await Stories.SaveAsync(approvedDoc with { Blocks = [.. approvedDoc.Blocks, ScriptBlock.Create(ScriptBlockKind.Action, "A new ending.")] }, approvedDoc.Revision, "Before edit", ct);
        Assert.Equal(approved.Id, edited.ApprovedSnapshotId);
        Assert.Equal(4, (await Stories.LoadApprovedAsync(project.Id, cancellationToken: ct))!.Blocks.Count);
        var secondApproval = await Stories.ApproveAsync(project.Id, edited.Revision, ct);
        Assert.NotEqual(approved.Id, secondApproval.ApprovedSnapshotId);
        var recovery = (await Stories.ListRecoveryAsync(project.Id, ct))[0];
        var restored = await Stories.RestoreAsync(project.Id, recovery.Id, secondApproval.Revision, ct);
        Assert.Equal(secondApproval.ApprovedSnapshotId, restored.ApprovedSnapshotId);
        Assert.Equal(4, (await Stories.LoadApprovedAsync(project.Id, approved.Id, ct))!.Blocks.Count);
        Assert.Equal(5, (await Stories.LoadApprovedAsync(project.Id, cancellationToken: ct))!.Blocks.Count);
    }
    [Fact]
    public async Task FailedApprovalAndStaleRevisionLeaveProductionSourceIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("Film"), ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.ApproveAsync(project.Id, 0, ct));
        var draft = await Stories.SaveAsync(ScriptFixtures.Document(project.Id), 0, cancellationToken: ct);
        await File.WriteAllTextAsync(Path.Combine(ProjectPath(project.Id), "script-approved"), "blocks publication", ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.ApproveAsync(project.Id, draft.Revision, ct));
        Assert.Null((await Stories.LoadAsync(project.Id, ct)).ApprovedSnapshotId);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Stories.ApproveAsync(project.Id, 0, ct));
        Assert.Equal(draft.Revision, (await Stories.LoadAsync(project.Id, ct)).Revision);
    }

    [Fact]
    public async Task SavedSourcesAreStableImmutableAndReadLegacySnapshotsWithoutApproval()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("Source capture"), ct);
        Assert.Null(await Stories.CaptureSourceAsync(project.Id, cancellationToken: ct));
        var draft = await Stories.SaveAsync(ScriptFixtures.Document(project.Id), 0, cancellationToken: ct);
        var first = (await Stories.CaptureSourceAsync(project.Id, draft.Revision, ct))!;
        Assert.Equal(first.Id, (await Stories.CaptureSourceAsync(project.Id, draft.Revision, ct))!.Id);
        Assert.Equal(draft.Revision, (await Stories.LoadAsync(project.Id, ct)).Revision);
        Assert.Null((await Stories.LoadAsync(project.Id, ct)).ApprovedSnapshotId);
        var changed = await Stories.SaveAsync(draft with { Blocks = [.. draft.Blocks, ScriptBlock.Create(ScriptBlockKind.Action, "Another beat.")] }, draft.Revision, cancellationToken: ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Stories.CaptureSourceAsync(project.Id, draft.Revision, ct));
        var second = (await Stories.CaptureSourceAsync(project.Id, changed.Revision, ct))!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ScriptStructure.Fingerprint(draft.Blocks), ScriptStructure.Fingerprint((await Stories.LoadSourceAsync(project.Id, first.Id, ct))!.Blocks));
        var legacy = await Stories.ApproveAsync(project.Id, changed.Revision, ct);
        Assert.Equal(changed.Blocks.Count, (await Stories.LoadSourceAsync(project.Id, legacy.ApprovedSnapshotId!.Value, ct))!.Blocks.Count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

internal sealed class StorageTestEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Lumibelle.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
