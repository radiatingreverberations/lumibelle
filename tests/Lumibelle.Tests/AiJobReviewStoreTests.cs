using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AiJobReviewStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.ReviewDraftTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private FileAiJobStore Jobs => new(_root, TimeProvider.System);
    private AiJobReviewStore Drafts => new(Jobs);
    private async Task<Guid> Job(AiJobState state = AiJobState.Completed)
    {
        var id = Guid.NewGuid();
        await Jobs.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.AssetExtraction, AiBackend.OpenRouter, new(Guid.NewGuid()), "Project", "Extraction", Guid.NewGuid(), new { prompt = "Captured original" }), _ct);
        if (state == AiJobState.Running) await Jobs.ClaimNextAsync(AiBackend.OpenRouter, 1, _ct);
        else await Jobs.UpdateAsync(id, j => j with { State = state }, _ct);
        return id;
    }
    [Fact]
    public async Task ReviewDraftSurvivesReopeningWithoutChangingTheModelResponse()
    {
        var id = await Job(); var original = new AiTextJobResult("raw", true, "stop");
        await Jobs.WriteArtifactAsync(id, AiJobArtifact.Result, original, _ct);
        Assert.Equal(0, (await Drafts.LoadAsync(id, _ct)).Revision);
        var saved = await Drafts.SaveAsync(id, new { title = "Reviewed name", decisions = new[] { "Skip" } }, 0, _ct);
        var reopened = await new AiJobReviewStore(Jobs).LoadAsync(id, _ct);
        Assert.Equal(1, reopened.Revision); Assert.True(JsonElement.DeepEquals(saved.Value!.Value, reopened.Value!.Value));
        Assert.Equal(original, await Jobs.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, _ct));
        Assert.Equal("Captured original", (await Jobs.ReadSnapshotAsync(id, _ct)).GetProperty("prompt").GetString());
    }
    [Fact]
    public async Task ConflictingTabsCannotOverwriteEachOtherButAnAcknowledgementCanBeRetried()
    {
        var id = await Job(); var first = await Drafts.SaveAsync(id, new { choice = "First" }, 0, _ct);
        var retry = await Drafts.SaveAsync(id, new { choice = "First" }, 0, _ct); Assert.Equal(first.Revision, retry.Revision);
        Assert.Equal(first.Revision, (await Drafts.SaveAsync(id, new { choice = "First" }, first.Revision, _ct)).Revision);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Drafts.SaveAsync(id, new { choice = "Other tab" }, 0, _ct));
        Assert.Equal("First", (await Drafts.LoadAsync(id, _ct)).Value!.Value.GetProperty("choice").GetString());
        Assert.Equal(2, (await Drafts.SaveAsync(id, new { choice = "Reviewed again" }, 1, _ct)).Revision);
    }
    [Fact]
    public async Task CapturePrecedesLockWaitAndFailedPublicationPreservesTheSavedDraft()
    {
        var id = await Job(); var path = Path.Combine(Jobs.DirectoryFor(id), "review-draft.json");
        var proposals = new List<string> { "Original" }; Task<AiJobReviewDraft> save;
        using (await ProjectFiles.LockAsync(path, _ct))
        { save = Drafts.SaveAsync(id, new { proposals }, 0, _ct); proposals[0] = "Changed later"; Assert.False(save.IsCompleted); }
        await save;
        Assert.Equal("Original", (await Drafts.LoadAsync(id, _ct)).Value!.Value.GetProperty("proposals")[0].GetString());
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => Drafts.SaveAsync(id, new { proposals = new[] { "Replacement" } }, 1, _ct));
        }
        Assert.Equal(1, (await Drafts.LoadAsync(id, _ct)).Revision);
    }
    [Fact]
    public async Task IncompleteOrUnknownJobsCannotHaveAnApplicableReviewDraft()
    {
        var id = await Job(AiJobState.Running);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Drafts.SaveAsync(id, new { decision = "Create" }, 0, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Drafts.SaveAsync(Guid.NewGuid(), new { decision = "Create" }, 0, _ct));
        Assert.Null((await Drafts.LoadAsync(id, _ct)).Value);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

internal sealed class TestReviewDraftStore(IAiJobReviewStore inner) : IAiJobReviewStore
{
    public Exception? SaveError { get; set; }
    public Task? LoadGate { get; set; }
    private readonly TaskCompletionSource _loadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Await this rather than a WaitFor: a page waiting on the load does not render, and bUnit checks only after renders.
    public Task LoadStarted => _loadStarted.Task;
    public async Task<AiJobReviewDraft> LoadAsync(Guid id, CancellationToken ct = default)
    {
        _loadStarted.TrySetResult();
        if (LoadGate is { } gate) await gate.WaitAsync(ct);
        return await inner.LoadAsync(id, ct);
    }
    public Task<AiJobReviewDraft> SaveAsync<T>(Guid id, T value, long revision, CancellationToken ct = default) => SaveError is null
        ? inner.SaveAsync(id, value, revision, ct) : Task.FromException<AiJobReviewDraft>(SaveError);
}
