using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

// Uses the existing real FileAiJobStore, capture, handler, result parser and
// provider fixture. No real provider, ComfyUI instance or paid generation.
public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(AiJobKind.PromptEnhancement)]
    [InlineData(AiJobKind.Guidance)]
    [InlineData(AiJobKind.ScriptAssistant)]
    public async Task TextRepairMakesANewTextOnlyRequestAndPreservesTheFailedArtifact(AiJobKind kind)
    {
        var original = kind == AiJobKind.PromptEnhancement
            ? await Capture.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), Enhancement(true), _ct)
            : await Request(kind);
        _providers.Chat.Output = "{\"broken\":";
        var originalContext = await Claim(original);
        Assert.Equal(AiJobState.NeedsAttention, (await Handler().ExecuteAsync(originalContext, original.Snapshot, _ct)).State);
        var failed = (await Store.ReadArtifactAsync<AiTextJobResult>(original.Id, AiJobArtifact.Result, _ct))!;
        var source = await Store.UpdateAsync(original.Id, j => j with { State = AiJobState.NeedsAttention,
            Recovery = AiJobRecovery.GenerateAgain, LeaseId = null, Error = failed.Error, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        var originalRequest = AiTextJobHandler.Read(source, original.Snapshot);
        var repair = AiTextRepairs.Capture(source, originalRequest, failed);
        var child = await Capture.RepairAsync(Guid.NewGuid(), Guid.NewGuid(), source, originalRequest, repair, _ct);
        var childRequest = child.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.NotEqual(original.Id, child.Id); Assert.Equal(original.Target, child.Target);
        Assert.Equal(originalRequest.Model, childRequest.Model); Assert.False(childRequest.InspectsImages);
        Assert.All(childRequest.Messages.SelectMany(m => m.Parts), p => Assert.Null(p.Image));
        // Even missing source images must not force a second image inspection.
        // Applying still uses the existing studio's current-target/media guards.
        _assets.Library = new() { ProjectId = _project };
        _providers.Chat.Output = Response(kind);
        var context = await Claim(child);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, child.Snapshot, _ct)).State);
        var corrected = (await Store.ReadArtifactAsync<AiTextJobResult>(child.Id, AiJobArtifact.Result, _ct))!;
        Assert.Null(corrected.Error); Assert.NotNull(corrected.Value);
        Assert.Equal(failed.Raw, (await Store.ReadArtifactAsync<AiTextJobResult>(original.Id, AiJobArtifact.Result, _ct))!.Raw);
        Assert.True(JsonElement.DeepEquals(original.Snapshot, await Store.ReadSnapshotAsync(original.Id, _ct)));
        _providers.Created = 0; _providers.OnCheck = () => throw new InvalidOperationException("Repair recovery must not contact the provider");
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), child.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        Assert.Equal(corrected.Value!.Value.GetRawText(), (await Store.ReadArtifactAsync<AiTextJobResult>(child.Id, AiJobArtifact.Result, _ct))!.Value!.Value.GetRawText());
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task TextRepairOfShotOrReelNeverAutoAppliesOnFinishOrRecovery(bool reel, bool recoverFromRaw)
    {
        var sourceRequest = AiTextRepairTests.ShotRequest(_project);
        if (reel)
        {
            var draft = new ReferenceReelDraft { AssetId = _asset.Id, Speaker = "Clara", Line = "Hello.", VoiceMode = ReelVoiceMode.NewVoice,
                Images = [new() { AssetId = _asset.Id, MediaId = _asset.Images[0].Id, Name = "Face" }] };
            var payload = new ReelCompositionRequest(_project, draft, ReferenceReels.Fingerprint(draft),
                new(_asset.Id, "Clara", "", "", null, "", "", ""), [new(draft.Images[0].Id, AiTextRepairTests.Hash)]);
            sourceRequest = sourceRequest with { Kind = AiJobKind.ReelComposition, Task = JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options) };
        }
        var source = AiTextRepairTests.Header(sourceRequest);
        // Weak reel prose is now a review note, so only an unreadable response needs a repair.
        var invalid = reel ? "{\"prompt\":\"Incomplete\",\"useGuidance\":\"Keep the face\"" : AiTextRepairTests.Invalid;
        var failed = AiTextResults.Parse(sourceRequest, invalid, "stop");
        var repair = AiTextRepairs.Capture(source, sourceRequest, failed);
        var child = await Capture.RepairAsync(Guid.NewGuid(), Guid.NewGuid(), source, sourceRequest, repair, _ct);
        var shot = reel ? ReferenceReels.Inputs(sourceRequest.Payload<ReelCompositionRequest>().Draft) : sourceRequest.Payload<PromptCompositionRequest>().Shot;
        _providers.Chat.Output = reel
            ? JsonSerializer.Serialize(new { prompt = AiTextRepairTests.ValidPrompt(shot, true), useGuidance = "Use for appearance; not verified output." })
            : JsonSerializer.Serialize(new { prompt = AiTextRepairTests.ValidPrompt(shot), referenceUsage = "Keep the face." });
        var handler = new AiTextJobHandler(_providers,
            new TestHttpFactory(new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("No ComfyUI request"))),
            new ComfyJobExecution(TestComfy.Monitor()), _projects, Enhancer, Guidance, TimeProvider.System,
            production: AiTextRepairTests.NoCalls<IProductionStore>(), reels: AiTextRepairTests.NoCalls<IAssetReelStore>());
        var context = await Claim(child);
        Assert.Equal(AiJobState.Completed, (await handler.ExecuteAsync(context, child.Snapshot, _ct)).State);
        if (recoverFromRaw) await Store.WriteArtifactAsync(child.Id, AiJobArtifact.Result, new AiTextJobResult(""), _ct);
        _providers.Created = 0; _providers.OnCheck = () => throw new InvalidOperationException("No provider on recovery");
        Assert.Equal(AiJobState.Completed, (await handler.RecoverAsync(await Recover(context), child.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
    }
}
