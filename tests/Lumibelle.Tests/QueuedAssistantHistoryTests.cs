using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed class QueuedAssistantHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.QueuedHistory", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private StorageTestEnvironment Environment => new(_root);
    private FileProjectStore Projects => new(Options.Create(new ProjectStorageOptions()), Environment, TimeProvider.System, NullLogger<FileProjectStore>.Instance);
    private ProjectFiles Files => new(Options.Create(new ProjectStorageOptions()), Environment, Projects);
    private FileAiJobStore Jobs => new(Environment, TimeProvider.System);
    private QueuedAssistantHistoryStore History => new(new FileAssistantHistoryStore(Files, new()), Jobs);
    private string HistoryPath(Guid project) => Path.Combine(_root, "App_Data", "Projects", project.ToString("D"), "script-assistant.json");
    private async Task<AiJobSubmission> Submit()
    {
        var project = await Projects.CreateAsync(new("QA film"), _ct);
        var document = new ScriptDocument { ProjectId = project.Id, Revision = 3, Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "A mouse watches rain.")] };
        var id = Guid.NewGuid(); var model = new TextModelReference(AiBackend.OpenRouter, "test/model", "QA text");
        var run = new AssistantRun { Id = id, JobId = id, SessionId = Guid.NewGuid(), Operation = WritingOperation.Revise, Model = model.Model, Backend = model.Backend,
            SourceRevision = document.Revision, SourceFingerprint = ScriptStructure.ContextFingerprint(document), Instructions = "Add a small smile.", Target = ScriptStructure.Capture(document, ScriptScope.Document, null) };
        var request = new AiTextJobRequest(1, AiJobKind.ScriptAssistant, model, true, new(), "script-v1", .7f, 23,
            JsonSerializer.SerializeToElement(new ScriptAssistantRequest(run, document, [], model), AtomicJsonFile.Options), [new("user", [new(Text: "Captured instructions")])]);
        var submission = AiJobSubmission.Create(id, AiJobKind.ScriptAssistant, model.Backend, new(project.Id), project.Name, "Revise whole script", Guid.NewGuid(), request);
        await Jobs.EnqueueAsync(submission, _ct); return submission;
    }
    private async Task Finish(AiJobSubmission submission, string? error = null)
    {
        var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var result = AiTextResults.Parse(request, "[{\"kind\":\"Scene\",\"spans\":[{\"text\":\"INT. ROOM - DAY\"}]},{\"kind\":\"Action\",\"spans\":[{\"text\":\"The mouse smiles at the rain.\"}]}]", "stop");
        await Jobs.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, error is null ? result : result with { Error = error, Value = null }, _ct);
        await Jobs.UpdateAsync(submission.Id, j => j with { State = error is null ? AiJobState.Completed : AiJobState.NeedsAttention, Error = error }, _ct);
    }
    private async Task<AssistantRun> Read(AiJobSubmission submission) => Assert.Single((await History.LoadAsync(submission.Target.ProjectId!.Value, _ct)).Runs);

    [Fact]
    public async Task QueuedAndRunningRequestsSurviveSessionChangesWithoutCreatingProjectHistory()
    {
        var submission = await Submit(); var waiting = await Read(submission);
        Assert.Equal(AssistantRunStatus.Running, waiting.Status); Assert.Equal(submission.Id, waiting.JobId);
        await Jobs.ClaimNextAsync(AiBackend.OpenRouter, 1, _ct);
        var reopened = await Read(submission); Assert.Equal(AssistantRunStatus.Running, reopened.Status); Assert.Null(reopened.Error);
        Assert.False(File.Exists(HistoryPath(submission.Target.ProjectId!.Value)));
    }
    [Fact]
    public async Task CompletedResultsReopenWithExactProposalIdsAndCapturedTargetWithoutPagePublication()
    {
        var submission = await Submit(); await Finish(submission);
        var result = await Read(submission); var reopened = await Read(submission);
        Assert.Equal(AssistantRunStatus.Completed, result.Status); Assert.Null(result.Error);
        Assert.Equal(result.Proposal!.Select(b => b.Id), reopened.Proposal!.Select(b => b.Id));
        Assert.Equal(submission.Id, result.Id); Assert.Equal(3, result.SourceRevision);
        Assert.Equal("A mouse watches rain.", result.Target.OriginalBlocks.Last().Text);
        Assert.False(File.Exists(HistoryPath(submission.Target.ProjectId!.Value)));
    }
    [Fact]
    public async Task ReviewDecisionsPreserveCapturedContentAndConflictAcrossTabs()
    {
        var submission = await Submit(); await Finish(submission); var original = await Read(submission);
        var reviewedTarget = original.Target with { Name = "Reviewed target" };
        var saved = await History.SaveRunAsync(submission.Target.ProjectId!.Value, original with { Applied = true, AppliedTarget = reviewedTarget }, _ct);
        Assert.Equal(1, saved.Revision); var reopened = await Read(submission);
        Assert.True(reopened.Applied); Assert.Equal(reviewedTarget.Name, reopened.AppliedTarget!.Name); Assert.Equal(original.Output, reopened.Output);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => History.SaveRunAsync(submission.Target.ProjectId.Value, original with { Rejected = true }, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(submission.Target.ProjectId.Value, reopened with { Applied = false }, _ct));
        Assert.True((await Read(submission)).Applied);
    }
    [Fact]
    public async Task SavedResponseCannotLoseItsJobIdentityOrReplaceTheCapturedTargetOrOutput()
    {
        var submission = await Submit(); await Finish(submission); var run = await Read(submission); var project = submission.Target.ProjectId!.Value;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(project, run with { JobId = null }, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(project, run with { Output = "A different response" }, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(project, run with { Target = run.Target with { Name = "Moved silently" } }, _ct));
        var other = await Projects.CreateAsync(new("Other film"), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(other.Id, run, _ct));
        Assert.False(File.Exists(HistoryPath(project)));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CancellationAndInvalidOutputRemainInspectableButCannotBeApplied(bool cancel)
    {
        var submission = await Submit(); await Finish(submission, cancel ? null : "The response is incomplete.");
        if (cancel) await Jobs.UpdateAsync(submission.Id, j => j with { CancelRequested = true, State = AiJobState.Cancelled }, _ct);
        var run = await Read(submission); Assert.NotEmpty(run.Output); Assert.NotNull(run.Error);
        Assert.Equal(cancel ? AssistantRunStatus.Cancelled : AssistantRunStatus.Completed, run.Status);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(submission.Target.ProjectId!.Value, run with { Applied = true }, _ct));
    }
    [Fact]
    public async Task FailedReviewPublicationRetainsTheDurableProposalForRetry()
    {
        var submission = await Submit(); await Finish(submission); var run = await Read(submission); var project = submission.Target.ProjectId!.Value;
        var path = HistoryPath(project);
        using (TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(path)!))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => History.SaveRunAsync(project, run with { Applied = true }, _ct));
        var retry = await Read(submission); Assert.False(retry.Applied); Assert.Equal(run.Proposal!.Select(b => b.Id), retry.Proposal!.Select(b => b.Id));
        Assert.True((await History.SaveRunAsync(project, retry with { Applied = true }, _ct)).Applied);
    }
    [Fact]
    public async Task LegacyHistoryStillLoadsAndQueuedRowsAreNotMarkedInterruptedByTheLegacyStore()
    {
        var submission = await Submit(); var project = submission.Target.ProjectId!.Value;
        var legacy = new FileAssistantHistoryStore(Files, new());
        var old = await legacy.SaveRunAsync(project, new() { SessionId = Guid.NewGuid() }, _ct);
        var queued = (await History.LoadAsync(project, _ct)).Runs.Single(r => r.Id == submission.Id);
        await legacy.SaveRunAsync(project, queued, _ct);
        var restarted = await History.LoadAsync(project, _ct);
        Assert.Equal(AssistantRunStatus.Interrupted, restarted.Runs.Single(r => r.Id == old.Id).Status);
        Assert.Equal(AssistantRunStatus.Running, restarted.Runs.Single(r => r.Id == submission.Id).Status);
    }
    [Fact]
    public async Task CorrectedJsonSurvivesReloadWithoutChangingTheOriginalJobAndRetainsSaveConflicts()
    {
        var submission = await Submit(); await Finish(submission, "Invalid original response");
        var original = await Read(submission); var project = submission.Target.ProjectId!.Value;
        var artifact = await Jobs.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        const string json = """[{"kind":"Action","spans":[{"text":"A kettle whistles."}]}]""";
        var correction = ScriptProposals.CorrectJson(original, json, Guid.NewGuid());
        var saved = await History.SaveRunAsync(project, correction, _ct);
        var reopened = (await History.LoadAsync(project, _ct)).Runs;
        Assert.Equal(2, reopened.Count); Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(reopened.Single(r => r.Id == original.Id)));
        var manual = reopened.Single(r => r.Id == saved.Id);
        Assert.Equal(original.Id, manual.CorrectedFromRunId); Assert.Equal(json, manual.Output);
        Assert.Equal(saved.Proposal!.Select(b => b.Id), manual.Proposal!.Select(b => b.Id));
        var applied = await History.SaveRunAsync(project, manual with { Applied = true }, _ct);
        Assert.True(applied.Applied);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => History.SaveRunAsync(project, manual with { Rejected = true }, _ct));
        var unchanged = await Jobs.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        Assert.Equal(JsonSerializer.Serialize(artifact), JsonSerializer.Serialize(unchanged));
        Assert.Single((await Jobs.ReadAsync(_ct)).Jobs);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
