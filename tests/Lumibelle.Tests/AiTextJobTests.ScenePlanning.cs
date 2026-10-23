using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private const string PlannedScene = "{\"beats\":[\"Eira finds the key.\"],\"continuity\":[\"The gate has not opened yet.\"],\"withhold\":[\"The garden remains undiscovered.\"],\"ending\":\"She takes the key.\",\"timeCue\":\"\"}";
    private async Task<AiJobSubmission> LocalSceneRequest(int? replyLimit = null, int? profileLimit = null, string instructions = "", bool reviewed = false)
    {
        _model = new(AiBackend.Vllm, "test/model", "Local") { VllmUrl = "http://llm-server:8000/v1", MaxOutputTokens = replyLimit };
        _settings.Value = _settings.Value with { VllmUrl = _model.VllmUrl! };
        if (profileLimit is not null)
            _settings.Value = _settings.Value with { VllmTextModels = new() { [TextModelPolicy.Key(_model)] = new(MaxOutputTokens: profileLimit) } };
        var source = await AcceptedScriptSource();
        var request = ScriptFromOutline(source);
        request = request with { Run = request.Run with { Instructions = instructions } };
        var submission = await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct);
        return reviewed ? submission : LegacySequence(submission);
    }
    // Exercise durable historical captures independently of the new reviewed profile.
    private static AiJobSubmission LegacySequence(AiJobSubmission submission)
    {
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var payload = captured.Payload<ScriptAssistantRequest>();
        return submission with { Snapshot = JsonSerializer.SerializeToElement(captured with {
            Profile = ScriptScenePlanning.Profile,
            Messages = ScriptAssistant.BuildMessages(payload, includeSourceEvidence: false).Select(AiTextMessage.Capture).ToArray(),
            ScenePlanMessages = ScriptScenePlanning.Messages(payload).Select(AiTextMessage.Capture).ToArray(),
            ResponseSchema = ScriptScenePlanning.ScreenplaySchema, ResponseGrammar = ScriptScenePlanning.ScreenplayGrammar
        }, AtomicJsonFile.Options) };
    }
    [Fact]
    public async Task LocalRedraftKeepsTheTwoStepWorkflowAndCapturedReplacementTarget()
    {
        _model = new(AiBackend.Vllm, "test/model", "Local") { VllmUrl = "http://llm-server:8000/v1" };
        _settings.Value = _settings.Value with { VllmUrl = _model.VllmUrl! };
        var source = await AcceptedScriptSource();
        var scene = new ScriptSourceScene(source.Id, source.ReviewRevision, 0, 0);
        var script = _script with { SourceSceneLinks = [new(scene, _script.Blocks.Select(b => b.Id).ToArray())] };
        var request = ScriptFromOutline(source) with { Script = script };
        request = request with { Run = request.Run with { Operation = WritingOperation.Rewrite, Instructions = "Use a quieter pace." },
            OutlineSelection = request.OutlineSelection! with { RedraftScene = true } };
        var submission = LegacySequence(await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct));
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(ScriptScenePlanning.Profile, snapshot.Profile);
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var draft = (await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct))!;
        Assert.Equal(WritingOperation.Rewrite, draft.Payload<ScriptAssistantRequest>().Run.Operation);
        Assert.Equal(ScriptStructure.Fingerprint(script.Blocks), ScriptStructure.Fingerprint(draft.Payload<ScriptAssistantRequest>().Run.Target.OriginalBlocks));
        var text = string.Join("\n", draft.Messages.SelectMany(m => m.Parts).Select(p => p.Text));
        Assert.Contains("Redraft ONLY", text);
        Assert.Contains("Use a quieter pace.", text);
        Assert.DoesNotContain("A mouse listens to rain.", text);
    }
    [Theory]
    [InlineData(null, null, 8192, 4096)]
    [InlineData(2048, null, 2048, 2048)]
    [InlineData(null, 3072, 3072, 3072)]
    [InlineData(6144, 2048, 6144, 4096)]
    public async Task SequenceReplyBudgetsAreCapturedAndRespectExplicitModelAndProfileLimits(int? selected, int? profile, int draftLimit, int briefLimit)
    {
        var submission = await LocalSceneRequest(selected, profile);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(draftLimit, snapshot.Model.MaxOutputTokens);
        Assert.Equal("array", snapshot.ResponseSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(ScriptScenePlanning.ScreenplayGrammar, snapshot.ResponseGrammar);
        var attributed = TextModelSession.Attribute(submission, new(_model, true, Source: TextModelSelectionSource.ProjectDefault));
        Assert.Equal(TextModelSelectionSource.ProjectDefault, attributed.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.SelectionSource);
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var planning = await context.ReadOperationAsync<AiTextJobRequest>("scene-plan", AiOperationArtifact.Request, _ct);
        Assert.Equal(briefLimit, planning!.Model.MaxOutputTokens);
        Assert.Equal("object", planning.ResponseSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(ScriptScenePlanning.PlanningGrammar, planning.ResponseGrammar);
        var draft = await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct);
        Assert.Equal(draftLimit, draft!.Model.MaxOutputTokens);
        Assert.Equal(draftLimit, _providers.Chat.Options!.MaxOutputTokens);
        Assert.Null(_providers.Chat.Options.ResponseFormat);
        Assert.NotNull(_providers.Chat.Options.RawRepresentationFactory);
    }

    [Theory]
    [InlineData(null, null, 16384)]
    [InlineData(3072, null, 3072)]
    [InlineData(null, 4096, 4096)]
    public async Task LongSequenceCapturesALargerDefaultButKeepsExplicitReplyLimits(int? selected, int? profile, int expected)
    {
        _model = new(AiBackend.Vllm, "test/model", "Local") { VllmUrl = "http://llm-server:8000/v1", MaxOutputTokens = selected };
        _settings.Value = _settings.Value with { VllmUrl = _model.VllmUrl! };
        if (profile is not null)
            _settings.Value = _settings.Value with { VllmTextModels = new() { [TextModelPolicy.Key(_model)] = new(MaxOutputTokens: profile) } };
        var text = "Chapter 1\n" + new string('a', 24000);
        await SourceStore.SaveAsync(_project, 0, "", text, "", _ct);
        var chunks = SourceAnalysis.Split(text, 10000);
        var outline = SourceAnalysisTests.Outline();
        outline.Acts[0].Scenes[0].Sources = [.. chunks.Select(c => c.Number)];
        var source = SourceAnalysisTests.Run(text, chunks) with { State = SourceRunState.Accepted, Proposal = outline };
        await SourceStore.UpdateAsync(_project, d => Task.FromResult(d with { Runs = [source] }), _ct);
        var request = ScriptFromOutline(source);
        var submission = await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct);
        submission = TextModelSession.Attribute(submission, new(_model, true));
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(expected, captured.Model.MaxOutputTokens);
        Assert.Equal(selected, captured.SelectedModel!.MaxOutputTokens);
        Assert.Equal(text.Length, captured.Payload<ScriptAssistantRequest>().Adaptation!.Passages.Sum(p => p.Text.Length));
    }
    [Fact]
    public async Task HistoricalSequenceRequestsKeepTheirCapturedUnconstrainedFormat()
    {
        var submission = await LocalSceneRequest();
        var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)! with { ResponseSchema = null, ResponseGrammar = null };
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options) };
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Null((await context.ReadOperationAsync<AiTextJobRequest>("scene-plan", AiOperationArtifact.Request, _ct))!.ResponseSchema);
        Assert.Null(_providers.Chat.Options!.ResponseFormat);
        Assert.Null(_providers.Chat.Options.RawRepresentationFactory);
    }

    [Fact]
    public async Task HistoricalSchemaRequestsKeepSchemaDecodingWithoutAcquiringANewGrammar()
    {
        var submission = await LocalSceneRequest();
        var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)! with { ResponseGrammar = null };
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options) };
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Null((await context.ReadOperationAsync<AiTextJobRequest>("scene-plan", AiOperationArtifact.Request, _ct))!.ResponseGrammar);
        Assert.NotNull(_providers.Chat.Options!.ResponseFormat);
        Assert.Null(_providers.Chat.Options.RawRepresentationFactory);
    }

    [Fact]
    public async Task WhitespaceLoopStopsWithThePartialDraftSavedAndNeverRetriesInference()
    {
        var submission = await LocalSceneRequest();
        var looping = "[{\"kind\":\"Scene\",\"spans\":[{\"text\":\"EXT. ROAD - NIGHT\"" + new string(' ', JsonOutputProgress.MaximumSeparatorWhitespace);
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(looping);
        var context = await Claim(submission);
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, submission.Snapshot, _ct));
        Assert.Contains("repeated JSON whitespace", error.Message);
        var saved = await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct);
        Assert.Equal(looping, saved!.Raw); Assert.False(saved.Complete);
        Assert.Equal(2, _providers.Created);
        Assert.Equal(AiJobState.NeedsAttention, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
        Assert.Equal(2, _providers.Created);
    }
    [Fact]
    public async Task InvalidCapturedSchemaIsRejectedBeforeInference()
    {
        var submission = await LocalSceneRequest();
        var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var context = await Claim(submission);
        var malformed = JsonSerializer.SerializeToElement(request with { ResponseSchema = JsonSerializer.SerializeToElement(true) }, AtomicJsonFile.Options);
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(context.Job, malformed));
        foreach (var invalid in new[] { request with { ResponseGrammar = " " }, request with { ResponseSchema = null }, request with { ResponseGrammar = new string('x', 16385) } })
            Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(context.Job, JsonSerializer.SerializeToElement(invalid, AtomicJsonFile.Options)));
        Assert.Equal(0, _providers.Created);
    }
    [Fact]
    public async Task ResolvedSequenceDefaultsDoNotAllowChangingAnExplicitChoiceOrProviderIdentity()
    {
        var submission = await LocalSceneRequest(replyLimit: 2048);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        foreach (var changed in new[] { captured.Model with { Model = "other/model" }, captured.Model with { VllmUrl = "http://other-server:8000/v1" },
            captured.Model with { MaxOutputTokens = 8192 }, captured.Model with { ProfileId = Guid.NewGuid() } })
        {
            var invalid = submission with { Snapshot = JsonSerializer.SerializeToElement(captured with { Model = changed }, AtomicJsonFile.Options) };
            Assert.Throws<WorkspaceStoreException>(() => TextModelSession.Attribute(invalid, new(_model, true)));
        }
    }
    [Fact]
    public async Task SequencePlanningUsesOriginalPassagesInsteadOfRedundantPotentiallyMistakenNotes()
    {
        var submission = await LocalSceneRequest();
        var payload = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<ScriptAssistantRequest>();
        payload = payload with { Adaptation = payload.Adaptation! with { Notes = [new("A mistaken note says the key became a dragon.", [1])] } };
        var input = string.Join("\n", ScriptScenePlanning.Messages(payload).Select(m => m.Text));
        Assert.Contains("brass key", input);
        Assert.DoesNotContain("became a dragon", input);
        Assert.Contains("Reviewed adaptation", input);
    }
    [Theory]
    [InlineData("Keep the source's direct adult tone, with an offscreen narrator.")]
    [InlineData("Use a restrained tone and imply the confrontation through its aftermath.")]
    public async Task AuthorToneInstructionsReachBothSequencePlanningAndDrafting(string instructions)
    {
        var submission = await LocalSceneRequest(instructions: instructions);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        foreach (var operation in new[] { "scene-plan", "scene-draft" })
        {
            var captured = await context.ReadOperationAsync<AiTextJobRequest>(operation, AiOperationArtifact.Request, _ct);
            Assert.Equal(instructions, captured!.Payload<ScriptAssistantRequest>().Run.Instructions);
            Assert.Contains(captured.Messages.SelectMany(m => m.Parts), p => p.Text?.Contains(instructions) == true);
        }
    }
    [Fact]
    public async Task SequenceDraftReceivesOnlySourceVerifiedExcerptsAndRetainsTheRawPlan()
    {
        var submission = await LocalSceneRequest();
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var plan = JsonSerializer.Serialize(ScriptScenePlanning.Parse(PlannedScene)! with {
            Evidence = [new(1, "Eira finds a brass key."), new(1, "The key becomes a dragon.")]
        }, AtomicJsonFile.Options);
        _providers.Chat.Outputs.Enqueue(plan); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var input = string.Join("\n", _providers.Chat.Messages.Select(m => m.Text));
        Assert.Contains("Eira finds a brass key.", input);
        Assert.DoesNotContain("The key becomes a dragon.", input);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        Assert.Equal(1, result!.ScenePlan!.UnverifiedEvidenceCount);
        Assert.Single(result.ScenePlan.Evidence!);
        var original = await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct);
        Assert.Equal(plan, original!.Raw);
    }
    [Fact]
    public async Task LocalSceneDraftKeepsReviewedSummaryAndOriginalPassagesWhenTheBriefEndsTooEarly()
    {
        var submission = await LocalSceneRequest();
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Contains(snapshot.ScenePlanMessages!, m => m.Parts.Any(p => p.Text?.Contains("brass key") == true));
        Assert.Contains(snapshot.Messages, m => m.Parts.Any(p => p.Text?.Contains("brass key") == true));
        Assert.DoesNotContain(snapshot.Messages, m => m.Parts.Any(p => p.Text?.Contains("She opens the gate.") == true));
        Assert.Contains(snapshot.Messages, m => m.Parts.Any(p => p.Text?.Contains("Sequence summary: Eira discovers the key.") == true));
        var shortened = ScriptScenePlanning.Parse(PlannedScene)! with { Ending = "She stops before collecting the key." };
        _providers.Chat.Outputs.Enqueue(JsonSerializer.Serialize(shortened, AtomicJsonFile.Options)); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Equal(2, _providers.Created);
        Assert.Contains(_providers.Chat.Messages, m => m.Text?.Contains("The gate has not opened yet.") == true);
        Assert.DoesNotContain(_providers.Chat.Messages, m => m.Text?.Contains("The garden remains undiscovered.") == true);
        Assert.Contains(_providers.Chat.Messages, m => m.Text?.Contains("brass key") == true);
        var result = (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
        Assert.NotNull(result.ScenePlan); Assert.Equal(Response(AiJobKind.ScriptAssistant), result.Raw);
        Assert.NotNull(await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct));
        var actual = await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct);
        Assert.Contains(actual!.Messages, m => m.Parts.Any(p => p.Text?.Contains("PREPARED SEQUENCE BRIEF") == true));
        var draftInput = string.Join("\n", actual.Messages.SelectMany(m => m.Parts).Select(p => p.Text));
        Assert.Contains("Sequence summary: Eira discovers the key.", draftInput);
        Assert.Contains("reviewed summary controls scope and ending", draftInput);
        Assert.DoesNotContain("stop at this brief's Ending", draftInput);
        _providers.Created = 0;
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        Assert.NotNull((await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!.ScenePlan);
    }
    [Theory]
    [InlineData("stop", true)]
    [InlineData("length", false)]
    [InlineData(null, false)]
    public async Task UnusableScenePlanNeverStartsDrafting(string? finish, bool malformed)
    {
        var submission = await LocalSceneRequest();
        _providers.Chat.Output = malformed ? "not a scene brief" : PlannedScene;
        _providers.Chat.Finish = finish is null ? null : new(finish);
        var context = await Claim(submission);
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, submission.Snapshot, _ct));
        Assert.Equal(1, _providers.Created);
        Assert.Empty(_script.SourceSceneLinks);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        Assert.Contains("Planning needs correction", result!.Error);
        Assert.Contains("Retry planning", result.Error);
    }

    [Fact]
    public async Task LongOptionalEvidenceContinuesToScreenplayWithTheRawPlanningReplyPreserved()
    {
        var submission = await LocalSceneRequest();
        var plan = ScriptScenePlanning.Parse(PlannedScene)! with { Evidence = [new(1, new string('x', 1210))] };
        var raw = JsonSerializer.Serialize(plan, AtomicJsonFile.Options);
        _providers.Chat.Outputs.Enqueue(raw);
        _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var saved = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.False(saved.ScenePlanning);
        Assert.Equal(1, saved.ScenePlan!.OversizedEvidenceCount);
        Assert.Empty(saved.ScenePlan.Evidence!);
        Assert.Equal(raw, (await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct))!.Raw);
        var draft = (await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct))!;
        var input = string.Join("\n", draft.Messages.SelectMany(m => m.Parts).Select(p => p.Text));
        Assert.Contains("brass key", input); // Original passages are still available.
        Assert.DoesNotContain(new string('x', 1210), input);
        Assert.Equal(2, _providers.Created);
    }

    [Fact]
    public async Task InterruptedDraftKeepsItsCompletedPlanAndRecoveryNeverMakesAnotherRequest()
    {
        var submission = await LocalSceneRequest();
        _providers.Chat.Outputs.Enqueue(PlannedScene); _providers.Chat.Outputs.Enqueue(Response(AiJobKind.ScriptAssistant));
        _providers.Chat.OnRequest = messages => _providers.Chat.FailAfterText = messages.Any(m => m.Text?.Contains("PREPARED SEQUENCE BRIEF") == true);
        var context = await Claim(submission);
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, submission.Snapshot, _ct));
        Assert.Equal(2, _providers.Created);
        Assert.True((await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct))!.Complete);
        Assert.NotNull((await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!.ScenePlan);
        _providers.Created = 0;
        Assert.Equal(AiJobState.NeedsAttention, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
    }

    [Fact]
    public async Task SpeakerFormatNormalizationKeepsTheProviderResponseAndAVisibleNotice()
    {
        var submission = await Request(AiJobKind.ScriptAssistant);
        const string raw = "[{\"kind\":\"Scene\",\"spans\":[{\"text\":\"INT. ROOM - DAY\"}]},{\"kind\":\"Speaker A (V.O.)\",\"spans\":[{\"text\":\"I remember.\"}]}]";
        _providers.Chat.Output = raw;
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(await Claim(submission), submission.Snapshot, _ct)).State);
        var result = (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
        Assert.Equal(raw, result.Raw); Assert.Null(result.Error);
        var update = result.Read<AssistantUpdate>()!;
        Assert.NotNull(update.NormalizationNotes); Assert.Equal(3, update.Blocks!.Count);
    }
}
