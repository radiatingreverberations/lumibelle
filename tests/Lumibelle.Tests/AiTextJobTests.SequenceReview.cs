using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Fact]
    public async Task WriterFormatExampleShowsOneBeatAcrossScenesAndClaimsTheEnding()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var instruction = captured.Messages[0].Parts[0].Text!;
        var exampleStart = instruction.IndexOf("{\"scenes\":", StringComparison.Ordinal);
        var exampleEnd = instruction.IndexOf(". Each location heading", exampleStart, StringComparison.Ordinal);
        var example = ScriptSequenceDrafting.Parse(instruction[exampleStart..exampleEnd]);
        Assert.Equal(2, example.Scenes.Length);
        Assert.All(example.Scenes, scene => Assert.Contains("B1", scene.BeatIds));
        Assert.Contains("END", example.Scenes[^1].BeatIds);
        Assert.Empty(ScriptSequenceDrafting.CheckStructure(example, ScriptSequenceDraftingTests.Plan));
    }

    [Fact]
    public async Task NewLocalSequenceCapturesTheGroupedContractAndRequiresAnEvidenceBasedReview()
    {
        var submission = await LocalSceneRequest(instructions: "Use a silent visual style.", reviewed: true);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(ScriptSequenceDrafting.Profile, captured.Profile);
        Assert.Equal("object", captured.ResponseSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(ScriptSequenceDrafting.DraftGrammar, captured.ResponseGrammar);
        _providers.Chat.Outputs.Enqueue(PlannedScene);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Candidate);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Review());
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Equal(ScriptSequenceReviewState.Passed, result.SequenceReview!.State);
        Assert.Equal(0, result.SequenceReview.Repairs);
        Assert.All(result.SequenceReview.Findings, f => Assert.Equal("note", f.Severity));
        Assert.Equal(2, result.Read<AssistantUpdate>()!.Blocks!.Count);
        Assert.Equal(3, _providers.Created);
        foreach (var input in _providers.Chat.Requests) Assert.Contains(input, m => m.Text?.Contains("Use a silent visual style.") == true);
        var draft = await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct);
        Assert.Contains(draft!.Messages, m => m.Parts.Any(p => p.Text?.Contains("PREPARED SEQUENCE CONTRACT") == true));
        Assert.Contains(draft.Messages, m => m.Parts.Any(p => p.Text?.Contains("The garden remains undiscovered.") == true));
        var review = await context.ReadOperationAsync<AiTextJobRequest>("sequence-review-0-0", AiOperationArtifact.Request, _ct);
        Assert.Equal(ScriptSequenceDrafting.ReviewGrammarFor(result.ScenePlan!), review!.ResponseGrammar);
        Assert.Equal(ScriptSequenceDrafting.ReviewReplyTokens, review.Model.MaxOutputTokens);
        using var packet = JsonDocument.Parse(review.Messages[^1].Parts[0].Text!.Split('\n', 2)[1]);
        Assert.Equal(new[] { "B1", "END" }, packet.RootElement.GetProperty("requiredChecks").EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        var scene = packet.RootElement.GetProperty("scenes")[0];
        Assert.Equal(1, scene.GetProperty("scene").GetInt32());
        Assert.Equal(0, scene.GetProperty("heading").GetProperty("block").GetInt32());
        Assert.Equal(1, scene.GetProperty("blocks")[0].GetProperty("block").GetInt32());
        Assert.Equal(ScriptSequenceDrafting.Parse(ScriptSequenceDraftingTests.Candidate).Scenes[0].Blocks[0].Text,
            scene.GetProperty("blocks")[0].GetProperty("text").GetString());
        _providers.Created = 0;
        var reopened = await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct);
        Assert.Equal(AiJobState.Completed, reopened.State); Assert.Equal(0, _providers.Created);
        Assert.Equal(result.Value!.Value.GetRawText(), (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!.Value!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterialReviewFindingsGetOneRepairAndAnotherReviewBeforeApproval(bool unresolved)
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var repaired = ScriptSequenceDraftingTests.Candidate.Replace("follows the river", "walks toward the bridge");
        foreach (var output in new[] { PlannedScene, ScriptSequenceDraftingTests.Candidate, ScriptSequenceDraftingTests.Review(severity: "repair"),
            repaired, ScriptSequenceDraftingTests.Review(severity: unresolved ? "repair" : "none") }) _providers.Chat.Outputs.Enqueue(output);
        var context = await Claim(submission);
        var outcome = await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Equal(unresolved ? AiJobState.NeedsAttention : AiJobState.Completed, outcome.State);
        Assert.Equal(unresolved ? ScriptSequenceReviewState.NeedsAttention : ScriptSequenceReviewState.Passed, result.SequenceReview!.State);
        Assert.Equal(1, result.SequenceReview.Repairs);
        Assert.Equal(5, _providers.Created);
        Assert.Equal(repaired, result.Raw);
        Assert.Equal(ScriptSequenceDraftingTests.Candidate, (await context.ReadOperationAsync<AiTextJobResult>("text", AiOperationArtifact.Output, _ct))!.Raw);
        Assert.Equal(repaired, (await context.ReadOperationAsync<AiTextJobResult>("sequence-repair-1", AiOperationArtifact.Output, _ct))!.Raw);
        if (unresolved) { Assert.NotNull(result.Error); Assert.Null(result.Value); }
        else { Assert.Null(result.Error); Assert.NotNull(result.Read<AssistantUpdate>()!.Blocks); }
        Assert.Empty(_script.SourceSceneLinks);
    }

    [Fact]
    public async Task MalformedReviewEvidenceIsRecheckedWithoutRewritingTheDraft()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        foreach (var output in new[] { PlannedScene, ScriptSequenceDraftingTests.Candidate,
            ScriptSequenceDraftingTests.Review(quote: "An invented quote."), ScriptSequenceDraftingTests.Review(severity: "none") }) _providers.Chat.Outputs.Enqueue(output);
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Equal(4, _providers.Created);
        Assert.Equal(0, (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!.SequenceReview!.Repairs);
        Assert.Null(await context.ReadOperationAsync<AiTextJobResult>("sequence-repair-1", AiOperationArtifact.Output, _ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoppedReviewCannotPublishOrRegenerateDuringRecoveryAndExplicitRetryReusesTheDraft(bool ioFailure)
    {
        var submission = await LocalSceneRequest(reviewed: true);
        foreach (var output in new[] { PlannedScene, ScriptSequenceDraftingTests.Candidate, ScriptSequenceDraftingTests.Review(severity: "none") }) _providers.Chat.Outputs.Enqueue(output);
        _providers.Chat.FailWithIoException = ioFailure;
        _providers.Chat.OnRequest = messages => _providers.Chat.FailAfterText = messages[0].Text!.StartsWith("Review a screenplay adaptation");
        var context = await Claim(submission);
        var interrupted = await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        Assert.Equal(AiJobState.NeedsAttention, interrupted.State);
        Assert.Equal(AiJobRecovery.ContinueRequest, interrupted.Recovery);
        _providers.Created = 0;
        Assert.Equal(AiJobState.NeedsAttention, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        Assert.Null((await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!.Value);
        _providers.Chat.OnRequest = null; _providers.Chat.FailAfterText = false;
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Review(severity: "none"));
        await Store.UpdateAsync(context.Job.Id, j => j with { State = interrupted.State, Recovery = interrupted.Recovery }, _ct);
        await Store.RequeueAsync(context.Job.Id, _ct);
        var retry = Context((await Store.ClaimNextAsync(AiBackend.Vllm, 1, _ct))!, false);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(retry, submission.Snapshot, _ct)).State);
        Assert.Equal(1, _providers.Created);
    }

    [Fact]
    public async Task CompletedChecksCanBePublishedAfterRestartWithoutAnotherModelCall()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        foreach (var output in new[] { PlannedScene, ScriptSequenceDraftingTests.Candidate, ScriptSequenceDraftingTests.Review(severity: "none") }) _providers.Chat.Outputs.Enqueue(output);
        var context = await Claim(submission);
        await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        // Reproduce a restart after immutable operation outputs, before final result publication.
        await context.SaveResultAsync(new AiTextJobResult(ScriptSequenceDraftingTests.Candidate) { ScenePlan = ScriptScenePlanning.Parse(PlannedScene) });
        _providers.Created = 0;
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        Assert.Equal(ScriptSequenceReviewState.Passed, (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!.SequenceReview!.State);
    }

    [Fact]
    public async Task ReviewContinuationUsesItsSavedPromptEvenWhenTheCurrentBuilderDiffers()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        foreach (var output in new[] { PlannedScene, ScriptSequenceDraftingTests.Candidate, ScriptSequenceDraftingTests.Review(severity: "none") }) _providers.Chat.Outputs.Enqueue(output);
        var context = await Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var plan = ScriptScenePlanning.Parse(PlannedScene)!;
        var saved = captured with {
            Messages = [.. ScriptSequenceDrafting.ReviewMessages(captured, ScriptSequenceDrafting.Parse(ScriptSequenceDraftingTests.Candidate), plan),
                AiTextMessage.Capture(new(Microsoft.Extensions.AI.ChatRole.User, "A previously captured review instruction."))],
            ResponseSchema = ScriptSequenceDrafting.ReviewSchemaFor(plan), ResponseGrammar = ScriptSequenceDrafting.ReviewGrammarFor(plan)
        };
        await context.SaveOperationAsync("sequence-review-0-0", AiOperationArtifact.Request, saved, _ct);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Contains(_providers.Chat.Requests[^1], m => m.Text == "A previously captured review instruction.");
        Assert.Equal(3, _providers.Created);
        Assert.Equal(JsonSerializer.Serialize(saved, AtomicJsonFile.Options), JsonSerializer.Serialize(
            await context.ReadOperationAsync<AiTextJobRequest>("sequence-review-0-0", AiOperationArtifact.Request, _ct), AtomicJsonFile.Options));
    }

    [Fact]
    public async Task RecheckingAHistoricalReviewReusesEverySavedOutputAndStartsNoInference()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var context = await Claim(submission);
        var plan = ScriptScenePlanning.Parse(PlannedScene)!;
        var original = new AiTextJobResult(ScriptSequenceDraftingTests.Candidate, true, "stop") { ScenePlan = plan };
        var raw = ScriptSequenceDraftingTests.RedundantReview();
        await context.SaveOperationAsync("text", AiOperationArtifact.Output, original, _ct);
        await context.SaveOperationAsync("sequence-review-0-0", AiOperationArtifact.Output, new AiTextJobResult(raw, true, "stop"), _ct);
        await context.SaveResultAsync(original with { Error = "The old review evidence exceeded its limit.", SequenceReview = new(ScriptSequenceReviewState.NeedsAttention, 0, [], [], raw) });
        await Store.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain }, _ct);
        await ScriptSequenceReviewRecovery.PrepareRecheckAsync(Store, context.Job.Id, _ct);
        Assert.Equal(AiJobRecovery.CheckStatus, (await Store.ReadAsync(_ct)).Jobs.Single().Recovery);
        await Store.RequeueAsync(context.Job.Id, _ct);
        var recovered = Context((await Store.ClaimNextAsync(AiBackend.Vllm, 1, _ct))!, true);
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(recovered, submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Equal(ScriptSequenceReviewState.Passed, result.SequenceReview!.State);
        Assert.Equal(2, result.SequenceReview.Warnings.Length);
        Assert.Equal(original.Raw, result.Raw);
        Assert.Equal(raw, (await context.ReadOperationAsync<AiTextJobResult>("sequence-review-0-0", AiOperationArtifact.Output, _ct))!.Raw);
    }

    [Fact]
    public async Task SavedFindingWithOneValidCitationAndOneBadExtraRecoversWithoutInference()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var context = await Claim(submission);
        var original = new AiTextJobResult(ScriptSequenceDraftingTests.Candidate, true, "stop") { ScenePlan = ScriptScenePlanning.Parse(PlannedScene)! };
        var raw = ScriptSequenceDraftingTests.FindingWithExtraCitation();
        await context.SaveOperationAsync("text", AiOperationArtifact.Output, original, _ct);
        await context.SaveOperationAsync("sequence-review-0-0", AiOperationArtifact.Output, new AiTextJobResult(raw, true, "stop"), _ct);
        await context.SaveResultAsync(original with { Error = "Review findings need valid categories and verifiable draft/source evidence.", SequenceReview = new(ScriptSequenceReviewState.NeedsAttention, 0, [], [], raw) });
        await Store.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain }, _ct);
        await ScriptSequenceReviewRecovery.PrepareRecheckAsync(Store, context.Job.Id, _ct);
        await Store.RequeueAsync(context.Job.Id, _ct);
        var recovered = Context((await Store.ClaimNextAsync(AiBackend.Vllm, 1, _ct))!, true);
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(recovered, submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Equal(ScriptSequenceReviewState.Passed, result.SequenceReview!.State);
        Assert.Single(result.SequenceReview.Warnings);
        Assert.Equal(original.Raw, result.Raw);
        Assert.Equal(raw, (await context.ReadOperationAsync<AiTextJobResult>("sequence-review-0-0", AiOperationArtifact.Output, _ct))!.Raw);
    }

    [Fact]
    public async Task RecheckWithoutACompletedReviewStaysStoppedWithoutStartingInference()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var context = await Claim(submission);
        var original = new AiTextJobResult(ScriptSequenceDraftingTests.Candidate, true, "stop") { ScenePlan = ScriptScenePlanning.Parse(PlannedScene)! };
        await context.SaveOperationAsync("text", AiOperationArtifact.Output, original, _ct);
        await context.SaveResultAsync(original with { Error = "The review was interrupted.", SequenceReview = new(ScriptSequenceReviewState.NeedsAttention, 0, [], []) });
        await Store.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.ContinueRequest }, _ct);
        await ScriptSequenceReviewRecovery.PrepareRecheckAsync(Store, context.Job.Id, _ct);
        await Store.RequeueAsync(context.Job.Id, _ct);
        var recovered = Context((await Store.ClaimNextAsync(AiBackend.Vllm, 1, _ct))!, true);
        var outcome = await Handler().RecoverAsync(recovered, submission.Snapshot, _ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State);
        Assert.Equal(AiJobRecovery.ContinueRequest, outcome.Recovery);
        Assert.Equal(0, _providers.Created);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Null(result.Value); Assert.Equal(original.Raw, result.Raw);
    }
}
