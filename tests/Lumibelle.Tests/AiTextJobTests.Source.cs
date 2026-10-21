using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private FileSourceStore SourceStore {
        get {
            Directory.CreateDirectory(Path.Combine(_root, "App_Data", "Projects", _project.ToString()));
            return new(new ProjectFiles(Options.Create(new ProjectStorageOptions()), new StorageTestEnvironment(_root), _projects));
        }
    }
    private AiJobCoordinator SourceQueue() => new(Store, _settings, [Handler()], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
    [Fact]
    public async Task SourceReviewCanCorrectCitationsWithoutChangingTheGeneratedProposal()
    {
        var store = SourceStore;
        const string text = "Chapter 1\nThe key is found.\n\nChapter 2\nThe gate opens.";
        await store.SaveAsync(_project, 0, "", text, "", _ct);
        var run = SourceAnalysisTests.Run(text, SourceAnalysis.Split(text, 1000)) with {
            State = SourceRunState.Review, Proposal = SourceAnalysisTests.Outline()
        };
        await store.UpdateAsync(_project, d => Task.FromResult(d with { Runs = [run] }), _ct);
        var review = SourceAnalysisTests.Outline();
        review.Acts[0].Scenes[0].Sources = SourceAnalysis.ParseSourceSections("2", 2);
        review.ReviewedOmissions = [1];
        var saved = await store.ReviewAsync(_project, 0, run.Id, review, false, _ct);
        Assert.Equal([1], saved.Runs[0].Proposal!.Acts[0].Scenes[0].Sources);
        Assert.Equal([2], saved.Runs[0].Reviewed!.Acts[0].Scenes[0].Sources);
        Assert.Equal(1, saved.Runs[0].ReviewRevision);
        Assert.Equal(text, saved.Text);
        var restored = (await store.LoadAsync(_project, _ct)).Runs[0];
        Assert.Equal([1], restored.Reviewed!.ReviewedOmissions);
        Assert.Empty(restored.Proposal!.ReviewedOmissions);
        Assert.Empty(SourceAnalysis.Warnings(restored, restored.Reviewed));
        restored.Reviewed.ReviewedOmissions = [];
        await store.ReviewAsync(_project, restored.ReviewRevision, run.Id, restored.Reviewed, false, _ct);
        restored = (await store.LoadAsync(_project, _ct)).Runs[0];
        Assert.Equal([1], SourceAnalysis.UnlinkedSources(restored, restored.Reviewed!));
    }
    [Theory]
    [InlineData(null, 4096, 3072)]
    [InlineData(8192L, 4096, 3072)]
    [InlineData(32768L, 4096, 27648)]
    [InlineData(262144L, 4096, SourceAnalysis.MaximumPromptBudget)]
    public async Task SourceBudgetUsesAdvertisedRoomWithAReservedReply(long? context, int reply, int expected)
    {
        _providers.ContextLength = context;
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        Assert.Equal(expected, await capture.SourceBudgetAsync(_model with { MaxOutputTokens = reply }, _ct));
    }
    [Fact]
    public async Task SourceResumeCanUseFullNotesWhenTheSameModelAdvertisesMoreRoom()
    {
        _providers.ContextLength = 16384;
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        var store = SourceStore;
        var text = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Chapter {i}\nThe story continues."));
        var doc = await store.SaveAsync(_project, 0, "The key", text, "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        var checkpoint = doc.Runs[0] with { State = SourceRunState.Paused,
            Notes = doc.Runs[0].Chunks.Select(c => new SourceNote(new string('a', 1700), [c.Number])).ToList() };
        doc = await store.UpdateAsync(_project, d => Task.FromResult(FileSourceStore.Replace(d, checkpoint)), _ct);
        Assert.Equal(SourceAnalysisStage.Combine, SourceWorkflow.Next(_project, checkpoint).Request.Stage);
        _providers.ContextLength = 262144;
        doc = await workflow.ResumeAsync(_project, checkpoint.Id, _ct);
        var resumed = doc.Runs[0];
        Assert.Equal(SourceAnalysis.MaximumPromptBudget, resumed.PromptBudget);
        Assert.Equal(checkpoint.Model, resumed.Model);
        Assert.Equal(JsonSerializer.Serialize(checkpoint.Notes), JsonSerializer.Serialize(resumed.Notes));
        Assert.Equal(checkpoint.Chunks, resumed.Chunks);
        var next = SourceWorkflow.Next(_project, resumed);
        Assert.Equal(SourceAnalysisStage.Outline, next.Request.Stage);
        Assert.Equal(checkpoint.Chunks.Select(c => c.Number), next.Request.Inputs.SelectMany(n => n.Sources));
        Assert.True(SourceAnalysis.RequestTokens(next.Request) <= resumed.PromptBudget);
    }
    [Fact]
    public async Task SourceResumeRejectsReducedCapacityForTheSameModelWithoutLosingItsCheckpoint()
    {
        _providers.ContextLength = 32768;
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        var store = SourceStore;
        var text = "Chapter 1\n" + string.Concat(Enumerable.Repeat("A key is found. ", 1000));
        var doc = await store.SaveAsync(_project, 0, "The key", text, "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        doc = await workflow.PauseAsync(_project, doc.Runs[0].Id, _ct);
        _providers.ContextLength = 8192;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ResumeAsync(_project, doc.Runs[0].Id, _ct));
        var saved = await store.LoadAsync(_project, _ct);
        Assert.Equal(doc.Revision, saved.Revision);
        Assert.Equal(doc.Runs[0].PromptBudget, saved.Runs[0].PromptBudget);
        Assert.Equal(JsonSerializer.Serialize(doc.Runs[0]), JsonSerializer.Serialize(saved.Runs[0]));
    }
    [Theory]
    [InlineData(AiBackend.Codex)]
    [InlineData(AiBackend.ClaudeCode)]
    public async Task SourceCliModelsKeepSupportedControls(AiBackend backend)
    {
        var model = await Capture.SourceModelAsync(new(backend, "test-model", "CLI model"), _ct);
        Assert.Null(model.MaxOutputTokens);
        TextModelPolicy.Validate(model);
    }
    [Fact]
    public async Task SourceSequenceSurvivesRecreationAndPauseWithoutRepeatingCompletedSections()
    {
        var source = SourceStore;
        var doc = await source.SaveAsync(_project, 0, "The key", "Chapter 1\nA key is found.\n\nChapter 2\nThe gate is opened.", "Keep the reveal.", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(source, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var first = doc.Runs[0].Pending!;
        Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
        await workflow.AdvanceAsync(_project, _ct);
        await workflow.AdvanceAsync(_project, _ct);
        Assert.Single((await Store.ReadAsync(_ct)).Jobs);
        var longerSummary = "Section 1: A key is found. " + new string('a', 2330);
        await CompleteSource(first, new(longerSummary));
        doc = await new SourceWorkflow(SourceStore, Capture, Store, queue).AdvanceAsync(_project, _ct);
        Assert.Single(doc.Runs[0].Notes);
        Assert.Equal(longerSummary, doc.Runs[0].Notes[0].Text);
        var second = doc.Runs[0].Pending!;
        Assert.NotEqual(first.Id, second.Id);
        await workflow.AdvanceAsync(_project, _ct);
        await workflow.PauseAsync(_project, doc.Runs[0].Id, _ct);
        await CompleteSource(second, new("Section 2: The gate is opened."));
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(SourceRunState.Paused, doc.Runs[0].State);
        Assert.Equal(2, doc.Runs[0].Notes.Count);
        Assert.Null(doc.Runs[0].Pending);
        Assert.Equal(doc.Revision, (await workflow.AdvanceAsync(_project, _ct)).Revision);
        await workflow.ResumeAsync(_project, doc.Runs[0].Id, _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var outline = doc.Runs[0].Pending!;
        Assert.Equal(SourceAnalysisStage.Outline, outline.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<SourceAnalysisRequest>().Stage);
        await workflow.AdvanceAsync(_project, _ct);
        await CompleteSource(outline, new("", SourceAnalysisTests.Outline()));
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(SourceRunState.Review, doc.Runs[0].State);
        Assert.Equal(3, (await Store.ReadAsync(_ct)).Jobs.Count);
        doc = await source.ReviewAsync(_project, 0, doc.Runs[0].Id, doc.Runs[0].Proposal!, true, _ct);
        Assert.Equal(SourceRunState.Accepted, doc.Runs[0].State);
        Assert.False(File.Exists(Path.Combine(_root, "App_Data", "Projects", _project.ToString(), "script.json")));
    }
    private async Task CompleteSource(AiJobSubmission submission, SourceAnalysisResult result)
    {
        var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var parsed = AiTextResults.Parse(request, JsonSerializer.Serialize(result, AtomicJsonFile.Options), "stop");
        Assert.Null(parsed.Error);
        await Store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, parsed, _ct);
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed }, _ct);
    }
    [Fact]
    public async Task SourceChangesPauseCapturedAnalysisAndPreventStaleAcceptance()
    {
        var store = SourceStore;
        var doc = await store.SaveAsync(_project, 0, "Old title", "Chapter 1\nOriginal text.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        await store.SaveAsync(_project, doc.SourceRevision, "Changed title", "Changed text.", "", _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(SourceRunState.Paused, doc.Runs[0].State);
        Assert.Equal("Chapter 1\nOriginal text.", doc.Runs[0].Text);
        Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ResumeAsync(_project, doc.Runs[0].Id, _ct));
        doc = await store.UpdateAsync(_project, d => Task.FromResult(FileSourceStore.Replace(d, d.Runs[0] with { State = SourceRunState.Review, Proposal = SourceAnalysisTests.Outline() })), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ReviewAsync(_project, 0, doc.Runs[0].Id, SourceAnalysisTests.Outline(), true, _ct));
        await store.ReviewAsync(_project, 0, doc.Runs[0].Id, SourceAnalysisTests.Outline(), false, _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.ReviewAsync(_project, 0, doc.Runs[0].Id, SourceAnalysisTests.Outline(), false, _ct));
    }
    [Theory]
    [InlineData("source-analysis-v1")]
    [InlineData("source-analysis-v2")]
    [InlineData(SourceAnalysis.Profile)]
    public async Task SourceModelResponseIsDurableAndRecoverableWithoutSecondInference(string profile)
    {
        var task = new SourceAnalysisRequest(_project, Guid.NewGuid(), SourceAnalysisStage.Read, "Source", "", [new("The courier finds a key.", [1])]);
        var submission = await Capture.SourceAsync(Guid.NewGuid(), Guid.NewGuid(), task, _model with { MaxOutputTokens = 2048 }, 4000, _ct);
        // Older captured requests remain recoverable after the prompt profile changes.
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(captured with { Profile = profile }, AtomicJsonFile.Options) };
        var context = await Claim(submission);
        _providers.Chat.Output = "{\"summary\":\"Section 1: The courier finds a key.\"}";
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        Assert.Equal("Section 1: The courier finds a key.", result!.Read<SourceAnalysisResult>()!.Summary);
        _providers.OnCheck = () => throw new InvalidOperationException("Recovery must use the saved response.");
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
    }
    [Fact]
    public async Task SourceReplanningUsesSavedEvidenceWithNewDirectionAndKeepsOriginalReview()
    {
        var store = SourceStore;
        var doc = await store.SaveAsync(_project, 0, "The key", "Chapter 1\nThe key.\n\nChapter 2\nThe gate opens.", "A mystery.", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        var original = doc.Runs[0] with {
            State = SourceRunState.Accepted, Proposal = SourceAnalysisTests.Outline(), Reviewed = SourceAnalysisTests.Outline(), ReviewRevision = 2,
            Notes = [new("The key is found [1].", [1]), new("It opens the gate [2].", [2])],
            Level = [new("Found key [1] opens the gate [2].", [1, 2])]
        };
        doc = await store.UpdateAsync(_project, d => Task.FromResult(FileSourceStore.Replace(d, original)), _ct);
        doc = await store.SaveAsync(_project, doc.SourceRevision, doc.Title, doc.Text, "A hopeful adventure.", _ct);
        var model = _model with { Model = "test/alternate", MaxOutputTokens = 2048 };
        doc = await workflow.ReplanAsync(_project, doc.SourceRevision, original.Id, model, Guid.NewGuid(), _ct);
        var replanned = doc.Runs[1];
        Assert.Equal(original.Id, replanned.AnalysisFromRunId);
        Assert.Equal("A hopeful adventure.", replanned.Direction);
        Assert.Equal(model, replanned.Model);
        Assert.False(SourceAnalysis.Stale(doc, replanned));
        Assert.Empty(replanned.Jobs);
        Assert.Null(replanned.Proposal);
        Assert.Null(replanned.Reviewed);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(doc.Runs[0]));
        var next = SourceWorkflow.Next(_project, replanned);
        Assert.Equal(SourceAnalysisStage.Outline, next.Request.Stage);
        Assert.Equal(JsonSerializer.Serialize(original.Notes), JsonSerializer.Serialize(next.Request.Inputs));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ReplanAsync(_project, doc.SourceRevision, original.Id, model, Guid.NewGuid(), _ct));
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(SourceAnalysisStage.Outline, doc.Runs[1].Pending!.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<SourceAnalysisRequest>().Stage);
        Assert.Single(doc.Runs[1].Jobs);
    }
    [Fact]
    public async Task SourceReplanningKeepsTheSavedOverviewWhenFullNotesStillCannotFit()
    {
        var store = SourceStore;
        var text = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"Chapter {i}\nThe story continues."));
        var doc = await store.SaveAsync(_project, 0, "The key", text, "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        var original = doc.Runs[0] with {
            State = SourceRunState.Review, Proposal = SourceAnalysisTests.Outline(),
            Notes = doc.Runs[0].Chunks.Select(c => new SourceNote(new string('a', 1700), [c.Number])).ToList(),
            Level = [new("The key is found [1] and eventually opens the gate [20].", Enumerable.Range(1, 20).ToArray())]
        };
        doc = await store.UpdateAsync(_project, d => Task.FromResult(FileSourceStore.Replace(d, original)), _ct);
        doc = await workflow.ReplanAsync(_project, doc.SourceRevision, original.Id, _model, Guid.NewGuid(), _ct);
        var next = SourceWorkflow.Next(_project, doc.Runs[1]);
        Assert.Equal(SourceAnalysisStage.Outline, next.Request.Stage);
        Assert.Equal(JsonSerializer.Serialize(original.Level), JsonSerializer.Serialize(next.Request.Inputs));
        Assert.Equal(20, doc.Runs[1].Notes.Count);
        Assert.Empty(doc.Runs[1].Jobs);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceReplanningRejectsChangedTextOrIncompleteEvidenceWithoutCreatingRun(bool changeText)
    {
        var store = SourceStore;
        var doc = await store.SaveAsync(_project, 0, "The key", "Chapter 1\nThe key.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        var original = doc.Runs[0] with {
            State = SourceRunState.Review, Proposal = SourceAnalysisTests.Outline(),
            Notes = changeText ? [new("The key is found [1].", [1])] : []
        };
        doc = await store.UpdateAsync(_project, d => Task.FromResult(FileSourceStore.Replace(d, original)), _ct);
        if (changeText) doc = await store.SaveAsync(_project, doc.SourceRevision, doc.Title, "Chapter 1\nA different story.", "", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ReplanAsync(_project, doc.SourceRevision, original.Id, _model, Guid.NewGuid(), _ct));
        Assert.Equal(doc.Revision, (await store.LoadAsync(_project, _ct)).Revision);
        Assert.Single((await store.LoadAsync(_project, _ct)).Runs);
    }
    [Fact]
    public async Task FailedSourceStepRequiresExplicitResumeAndKeepsTheCapturedRequest()
    {
        var store = SourceStore;
        var doc = await store.SaveAsync(_project, 0, "", "Chapter 1\nThe key.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var failed = doc.Runs[0].Pending!;
        await workflow.AdvanceAsync(_project, _ct);
        await Store.UpdateAsync(failed.Id, j => j with { State = AiJobState.NeedsAttention, Error = "Incomplete output.", Recovery = AiJobRecovery.GenerateAgain }, _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(SourceRunState.Paused, doc.Runs[0].State);
        await workflow.AdvanceAsync(_project, _ct);
        Assert.Single((await Store.ReadAsync(_ct)).Jobs);
        Assert.Equal(failed.Id, doc.Runs[0].Pending!.Id);
        await workflow.ResumeAsync(_project, doc.Runs[0].Id, _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.NotEqual(failed.Id, doc.Runs[0].Pending!.Id);
        Assert.Empty(doc.Runs[0].Notes);
        Assert.True(JsonElement.DeepEquals(failed.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Task,
            doc.Runs[0].Pending!.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Task));
    }
    [Fact]
    public async Task SourceResumeWithAnotherModelKeepsNotesAndReplacesOnlyTheFailedStep()
    {
        var source = SourceStore;
        var doc = await source.SaveAsync(_project, 0, "The key", "Chapter 1\nA key is found.\n\nChapter 2\nThe gate is opened.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(source, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var first = doc.Runs[0].Pending!;
        await workflow.AdvanceAsync(_project, _ct);
        await CompleteSource(first, new("Section 1: The key is found."));
        doc = await workflow.AdvanceAsync(_project, _ct);
        var failed = doc.Runs[0].Pending!;
        await workflow.AdvanceAsync(_project, _ct);
        await Store.UpdateAsync(failed.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.RetryOutput, Error = "Unavailable." }, _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var captured = doc.Runs[0];
        var replacement = _model with { Model = "test/alternate", Name = "Alternate model", MaxOutputTokens = 2048 };
        doc = await workflow.ResumeAsync(_project, captured.Id, replacement, _ct);
        Assert.Equal(replacement, doc.Runs[0].Model);
        Assert.Equal(5120, doc.Runs[0].PromptBudget);
        Assert.Null(doc.Runs[0].Pending);
        Assert.Equal(captured.Notes.Select(n => (n.Text, string.Join(",", n.Sources))),
            doc.Runs[0].Notes.Select(n => (n.Text, string.Join(",", n.Sources))));
        Assert.Equal(captured.Chunks, doc.Runs[0].Chunks);
        Assert.Equal(captured.Jobs, doc.Runs[0].Jobs);
        Assert.Equal(AiJobState.NeedsAttention, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == failed.Id).State);

        doc = await new SourceWorkflow(SourceStore, Capture, Store, queue).AdvanceAsync(_project, _ct);
        var next = doc.Runs[0].Pending!;
        Assert.NotEqual(failed.Id, next.Id);
        var request = next.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(replacement, request.Model);
        Assert.True(JsonElement.DeepEquals(failed.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Task, request.Task));
        Assert.Equal("test/model", first.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Model.Model);
        await workflow.AdvanceAsync(_project, _ct);
        await CompleteSource(next, new("Section 2: The gate opens."));
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(2, doc.Runs[0].Notes.Count);
        Assert.Equal("test/alternate", doc.Runs[0].Pending!.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Model.Model);
    }
    [Theory]
    [InlineData(AiJobState.Waiting, false)]
    [InlineData(AiJobState.Running, false)]
    [InlineData(AiJobState.NeedsAttention, true)]
    public async Task SourceModelSwitchCannotReplaceAnUnfinishedProviderRequest(AiJobState state, bool remote)
    {
        var source = SourceStore;
        var doc = await source.SaveAsync(_project, 0, "", "Chapter 1\nA key is found.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(source, Capture, Store, queue);
        var original = remote ? _model with { Backend = AiBackend.ComfyUI, ComfyUrl = "http://127.0.0.1:8188" } : _model;
        doc = await workflow.StartAsync(_project, doc.SourceRevision, original, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var pending = doc.Runs[0].Pending!;
        await workflow.AdvanceAsync(_project, _ct);
        await Store.UpdateAsync(pending.Id, j => j with { State = state, RemoteUnconfirmed = remote, LeaseId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow }, _ct);
        doc = await workflow.PauseAsync(_project, doc.Runs[0].Id, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ResumeAsync(_project, doc.Runs[0].Id, _model with { Model = "test/alternate" }, _ct));
        var saved = await source.LoadAsync(_project, _ct);
        Assert.Equal(doc.Revision, saved.Revision);
        Assert.Equal(pending.Id, saved.Runs[0].Pending!.Id);
        Assert.Equal("test/model", saved.Runs[0].Model.Model);
    }
    [Fact]
    public async Task SourceModelSwitchSavesACompletedResponseBeforeContinuing()
    {
        var source = SourceStore;
        var doc = await source.SaveAsync(_project, 0, "", "Chapter 1\nA key is found.\n\nChapter 2\nThe gate opens.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(source, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var pending = doc.Runs[0].Pending!;
        await workflow.AdvanceAsync(_project, _ct);
        await workflow.PauseAsync(_project, doc.Runs[0].Id, _ct);
        await CompleteSource(pending, new("Section 1: The key is found."));
        doc = await workflow.ResumeAsync(_project, doc.Runs[0].Id, _model with { Model = "test/alternate" }, _ct);
        Assert.Single(doc.Runs[0].Notes);
        Assert.Null(doc.Runs[0].Pending);
        doc = await workflow.AdvanceAsync(_project, _ct);
        var request = doc.Runs[0].Pending!.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal([2], request.Payload<SourceAnalysisRequest>().Inputs[0].Sources);
        Assert.Equal("test/alternate", request.Model.Model);
    }
    [Fact]
    public async Task SourceModelSwitchRejectsSmallerContextWithoutChangingTheCheckpoint()
    {
        var source = SourceStore;
        var doc = await source.SaveAsync(_project, 0, "", "Chapter 1\n" + string.Concat(Enumerable.Repeat("A key is found. ", 1000)), "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(source, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model with { MaxOutputTokens = 2048 }, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        doc = await workflow.PauseAsync(_project, doc.Runs[0].Id, _ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ResumeAsync(_project, doc.Runs[0].Id, _model with { Model = "test/smaller" }, _ct));
        Assert.Contains("remaining source sections", error.Message);
        var saved = await source.LoadAsync(_project, _ct);
        Assert.Equal(doc.Revision, saved.Revision);
        Assert.Equal(doc.Runs[0].Pending!.Id, saved.Runs[0].Pending!.Id);
        Assert.Equal(doc.Runs[0].Model, saved.Runs[0].Model);
        Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
    }
    [Fact]
    public async Task SourceChangeBetweenCaptureAndEnqueuePausesWithoutSubmittingOldText()
    {
        var store = SourceStore;
        var doc = await store.SaveAsync(_project, 0, "", "Chapter 1\nThe key.", "", _ct);
        using var queue = SourceQueue();
        var workflow = new SourceWorkflow(store, Capture, Store, queue);
        doc = await workflow.StartAsync(_project, doc.SourceRevision, _model, Guid.NewGuid(), _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.NotNull(doc.Runs[0].Pending);
        await store.SaveAsync(_project, doc.SourceRevision, "", "Chapter 1\nThe new key.", "", _ct);
        doc = await workflow.AdvanceAsync(_project, _ct);
        Assert.Equal(SourceRunState.Paused, doc.Runs[0].State);
        Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
    }
}
