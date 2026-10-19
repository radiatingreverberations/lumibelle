using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CapturedEnvironmentHashCitationCanReopenWithoutAnotherProviderCall(bool resolvable)
    {
        var submission = await Request(AiJobKind.ShotPlanning);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        byte[] picture = [1, 2, 3];
        var view = new EnvironmentViewCandidate($"reel/{Guid.NewGuid()}/{Guid.NewGuid()}", "Podium side", false, "", null, null, null,
            lumibelle.Services.Production.ReelViewSuggestions.Hash(picture));
        var planning = captured.Payload<ShotPlanningRequest>() with { Environments = [new(Guid.NewGuid(), _script.Blocks[0].Id, Guid.NewGuid(), Guid.NewGuid(), "Plaza", "", "", [view], "captured")], InspectionImageHashes = [view.Sha256] };
        captured = captured with { Task = JsonSerializer.SerializeToElement(planning, AtomicJsonFile.Options),
            Messages = ShotPlanner.BuildMessages(planning, new Dictionary<string, byte[]> { [view.Sha256] = picture }).Select(AiTextMessage.Capture).ToArray() };
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(captured, AtomicJsonFile.Options) };
        var shot = new Shot { Title = "Pause", SceneId = _script.Blocks[0].Id, SourceBlockIds = _script.Blocks.Select(b => b.Id).ToList(), Duration = 5, Description = "A mouse waits." };
        var alias = view.Id[..(view.Id.LastIndexOf('/') + 1)] + (resolvable ? view.Sha256 : new string('f', 64));
        var raw = JsonSerializer.Serialize(new { sceneStrategies = new[] { new { sceneId = shot.SceneId, environmentAssessment = new { claims = new[] { new EnvironmentSupportClaim("Standing side", ReferenceSupportStatus.Supported, [alias]) } } } }, shots = new[] { shot } }, AtomicJsonFile.Options);
        const string error = "The proposal could not be applied: Environment evidence cites a picture not attached to this drafting request. Nothing was added; inspect it and retry.";
        await Store.EnqueueAsync(submission, _ct);
        await Store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, new AiTextJobResult(raw, true, "stop", Error: error), _ct);
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.NeedsAttention, Error = error }, _ct);
        _providers.OnCheck = () => throw new InvalidOperationException("Recovery needs no provider");
        var snapshot = (await Store.ReadSnapshotAsync(submission.Id, _ct)).GetRawText();
        var reopened = await Store.ReviewShotPlanningAsync(_project, submission.Id, _ct);
        if (!resolvable) {
            Assert.Equal(AiJobState.NeedsAttention, reopened.State);
            var unchanged = (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
            Assert.Equal(error, unchanged.Error); Assert.Equal(raw, unchanged.Raw); Assert.Null(unchanged.Value);
            Assert.Equal(0, _providers.Created); return;
        }
        Assert.Equal(AiJobState.Completed, reopened.State);
        var saved = (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
        Assert.Null(saved.Error); Assert.Equal(raw, saved.Raw);
        Assert.Equal(view.Id, saved.Read<ShotPlanningResult>()!.Shots[0].DraftingEnvironment!.Assessment.Claims[0].CandidateIds[0]);
        Assert.Equal(snapshot, (await Store.ReadSnapshotAsync(submission.Id, _ct)).GetRawText());
        Assert.Equal(0, _providers.Created);
    }
    [Fact]
    public async Task CameraWarningRecoveryKeepsExactTextAndStableShotIdsWithoutAProviderCall()
    {
        const string description = "End holding on her face, leaving the screenplay’s cut to black for the edit after this take.";
        const string oldError = "The model returned an invalid or incomplete shot list. Nothing was added; inspect it and retry.";
        var submission = await Request(AiJobKind.ShotPlanning);
        var shot = new Shot { Title = "Final line", SceneId = _script.Blocks[0].Id, SourceBlockIds = _script.Blocks.Select(b => b.Id).ToList(),
            Duration = 5, Description = description };
        var raw = JsonSerializer.Serialize(new[] { shot }, AtomicJsonFile.Options);
        await Store.EnqueueAsync(submission, _ct);
        await Store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, new AiTextJobResult(raw, true, "stop",
            JsonSerializer.SerializeToElement(new ShotPlanningResult([], raw, [], oldError), AtomicJsonFile.Options), oldError), _ct);
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.NeedsAttention, Error = oldError, Recovery = AiJobRecovery.GenerateAgain }, _ct);
        var originalSnapshot = (await Store.ReadSnapshotAsync(submission.Id, _ct)).GetRawText();
        _providers.OnCheck = () => throw new InvalidOperationException("Recovery must not call a provider");
        var reopened = await Task.WhenAll(Store.ReviewShotPlanningAsync(_project, submission.Id, _ct), Store.ReviewShotPlanningAsync(_project, submission.Id, _ct));
        Assert.All(reopened, j => { Assert.Equal(AiJobState.Completed, j.State); Assert.Null(j.Error); Assert.Equal(AiJobRecovery.None, j.Recovery); });
        Assert.Equal(reopened[0].Version, reopened[1].Version);
        var saved = (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
        var proposal = saved.Read<ShotPlanningResult>()!;
        Assert.Null(saved.Error); Assert.Equal(raw, saved.Raw); Assert.Equal(raw, proposal.Raw);
        Assert.Single(proposal.CoverageNotes); Assert.Equal(description, Assert.Single(proposal.Shots).Description);
        Assert.Equal(originalSnapshot, (await Store.ReadSnapshotAsync(submission.Id, _ct)).GetRawText());
        // Recovering after an interrupted queue-index write must reuse the parsed IDs.
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.NeedsAttention, Error = oldError }, _ct);
        await Store.ReviewShotPlanningAsync(_project, submission.Id, _ct);
        Assert.Equal(saved.Value!.Value.GetRawText(), (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!.Value!.Value.GetRawText());
        Assert.Equal(0, _providers.Created);
    }

    [Theory]
    [InlineData("[{", true, "stop")]
    [InlineData("[]", true, "stop")]
    [InlineData("[]", false, "stop")]
    [InlineData("[]", true, "length")]
    public async Task UnusableShotResponsesRemainUntouchedOnRepeatedReview(string raw, bool complete, string finishReason)
    {
        var request = await Request(AiJobKind.ShotPlanning);
        await Store.EnqueueAsync(request, _ct);
        const string error = "The model returned an invalid or incomplete shot list. Nothing was added; inspect it and retry.";
        var job = await Store.UpdateAsync(request.Id, j => j with { State = AiJobState.NeedsAttention, Error = error }, _ct);
        var artifact = new AiTextJobResult(raw, complete, finishReason, Error: error);
        await Store.WriteArtifactAsync(request.Id, AiJobArtifact.Result, artifact, _ct);
        var revision = (await Store.ReadAsync(_ct)).Revision;
        for (var i = 0; i < 2; i++)
            Assert.Equal(job, await Store.ReviewShotPlanningAsync(_project, request.Id, _ct));
        Assert.Equal(artifact, await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct));
        Assert.Equal(revision, (await Store.ReadAsync(_ct)).Revision);
    }

    [Theory]
    [InlineData("It’s raining.")]
    [InlineData("I prefer sunshine.")]
    public async Task DialogueDifferencesRemainReviewableWithoutChangingProposedText(string wording)
    {
        _script.Blocks.Add(ScriptBlock.Create(ScriptBlockKind.Character, "MOUSE"));
        _script.Blocks.Add(ScriptBlock.Create(ScriptBlockKind.Dialogue, "It's raining."));
        var submission = await Request(AiJobKind.ShotPlanning);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var shot = new Shot { Title = "Rain", SceneId = _script.Blocks[0].Id, SourceBlockIds = _script.Blocks.Select(b => b.Id).ToList(),
            Duration = 5, Description = "The camera holds on a mouse by the window.", Dialogue = [new() { Speaker = "MOUSE", Text = wording }] };
        var raw = JsonSerializer.Serialize(new[] { shot }, AtomicJsonFile.Options);
        var parsed = AiTextResults.Parse(captured, raw, "stop");
        Assert.Null(parsed.Error);
        var proposal = parsed.Read<ShotPlanningResult>()!;
        Assert.Equal(wording, Assert.Single(proposal.Shots).Dialogue[0].Text);
        Assert.Single(proposal.DialogueNotes); Assert.Single(proposal.UncoveredDialogue);
        Assert.Equal(raw, proposal.Raw);

        const string oldError = "The proposal changed or invented dialogue. Inspect the response and retry.";
        await Store.EnqueueAsync(submission, _ct);
        await Store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result,
            parsed with { Error = oldError, Value = JsonSerializer.SerializeToElement(new ShotPlanningResult([], raw, [], oldError), AtomicJsonFile.Options) }, _ct);
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.NeedsAttention, Error = oldError, Recovery = AiJobRecovery.GenerateAgain }, _ct);
        var originalSnapshot = (await Store.ReadSnapshotAsync(submission.Id, _ct)).GetRawText();
        _providers.OnCheck = () => throw new InvalidOperationException("Review needs no provider");
        var reviews = await Task.WhenAll(Store.ReviewShotPlanningAsync(_project, submission.Id, _ct), Store.ReviewShotPlanningAsync(_project, submission.Id, _ct));
        Assert.All(reviews, j => Assert.Equal(AiJobState.Completed, j.State));
        Assert.Equal(reviews[0].Version, reviews[1].Version);
        var saved = (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
        Assert.Null(saved.Error); Assert.Equal(raw, saved.Raw);
        Assert.Equal(wording, saved.Read<ShotPlanningResult>()!.Shots[0].Dialogue[0].Text);
        Assert.Equal(originalSnapshot, (await Store.ReadSnapshotAsync(submission.Id, _ct)).GetRawText());
        // A crash after publishing the parsed result must not create fresh shot IDs on reopening.
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.NeedsAttention, Error = oldError }, _ct);
        await Store.ReviewShotPlanningAsync(_project, submission.Id, _ct);
        Assert.Equal(saved.Value!.Value.GetRawText(), (await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct))!.Value!.Value.GetRawText());
        Assert.Equal(0, _providers.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavedResponseReviewDoesNotBypassCancellationOrIncompleteOutput(bool cancelled)
    {
        var request = await Request(AiJobKind.ShotPlanning);
        await Store.EnqueueAsync(request, _ct);
        const string error = "The proposal changed or invented dialogue. Inspect the response and retry.";
        await Store.UpdateAsync(request.Id, j => j with { State = AiJobState.NeedsAttention, Error = error, CancelRequested = cancelled }, _ct);
        await Store.WriteArtifactAsync(request.Id, AiJobArtifact.Result, new AiTextJobResult(Response(AiJobKind.ShotPlanning), true, "length", Error: error), _ct);
        var result = await Store.ReviewShotPlanningAsync(_project, request.Id, _ct);
        Assert.Equal(AiJobState.NeedsAttention, result.State);
        Assert.NotNull(result.Error);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.ReviewShotPlanningAsync(Guid.NewGuid(), request.Id, _ct));
    }
}
