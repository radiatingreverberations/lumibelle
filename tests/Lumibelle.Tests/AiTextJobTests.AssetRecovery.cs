using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetResponseCorrectionPreservesOriginalAndReviewIdsWithoutInference(bool edited)
    {
        var submission = await Request(AiJobKind.AssetExtraction);
        var context = await Claim(submission);
        var request = AiTextJobHandler.Read(context.Job, submission.Snapshot);
        var payload = request.Payload<AssetExtractionRequest>();
        var valid = JsonSerializer.Serialize(new[] { new { category = "prop", name = "Key", description = "A brass key.",
            evidence = new[] { new { label = "Room", sceneId = payload.SceneIds[0], excerpt = "A brass key" } } } });
        var raw = edited ? valid.Replace(payload.SceneIds[0].ToString(), Guid.NewGuid().ToString()) : valid;
        var original = new AiTextJobResult(raw, true, "stop", Error: "Old asset validation failure");
        await Store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, original, _ct);
        await Store.UpdateAsync(submission.Id, j => j with { State = AiJobState.NeedsAttention, LeaseId = null,
            Recovery = AiJobRecovery.GenerateAgain, Error = original.Error, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        var calls = _providers.Created;
        await AiTextResponseCorrections.SaveAsync(Store, submission.Id, valid, edited, _ct);
        var header = (await Store.ReadAsync(_ct)).Jobs.Single();
        Assert.Equal(AiJobState.Completed, header.State); Assert.Null(header.Error);
        var correction = (await AiTextResponseCorrections.ReadAsync(Store, header, _ct))!;
        Assert.True(correction.LocallyCorrected); Assert.Null(correction.Error);
        var proposal = Assert.Single(correction.Read<AssetExtractionResult>()!.Proposals);
        await AiTextResponseCorrections.SaveAsync(Store, submission.Id, valid, edited, _ct);
        var again = (await AiTextResponseCorrections.ReadAsync(Store, header, _ct))!;
        Assert.Equal(proposal.Id, Assert.Single(again.Read<AssetExtractionResult>()!.Proposals).Id);
        Assert.Equal(original, await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct));
        Assert.True(JsonElement.DeepEquals(submission.Snapshot, await Store.ReadSnapshotAsync(submission.Id, _ct)));
        Assert.Equal(calls, _providers.Created); Assert.Single(_assets.Library.Assets);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => AiTextResponseCorrections.SaveAsync(Store, submission.Id, "[]", true, _ct));
    }

    [Theory]
    [InlineData("length", false)]
    [InlineData("stop", true)]
    public async Task AssetRecoveryCannotPromoteTruncatedOrCancelledOutput(string finish, bool cancelled)
    {
        var submission = await Request(AiJobKind.AssetExtraction); await Claim(submission);
        await Store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, new AiTextJobResult("[]", true, finish, Error: "Stopped"), _ct);
        await Store.UpdateAsync(submission.Id, j => j with { State = cancelled ? AiJobState.Cancelled : AiJobState.NeedsAttention,
            CancelRequested = cancelled, LeaseId = null, FinishedUtc = DateTimeOffset.UtcNow, Recovery = AiJobRecovery.GenerateAgain }, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => AiTextResponseCorrections.SaveAsync(Store, submission.Id, "[]", false, _ct));
        Assert.Null(await Store.ReadArtifactAsync<AiTextResponseCorrection[]>(submission.Id, AiJobArtifact.TextResponseCorrections, _ct));
    }
}
