using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AiTextRepairServiceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public readonly List<AiJobHeader> Headers = [];
        public readonly Dictionary<Guid, AiTextJobRequest> Requests = [];
        public readonly Dictionary<Guid, AiTextJobResult> Results = [];
        public readonly Guid Project = Guid.NewGuid();
        public AiTextRepairService Service { get; }
        public AssetLibrary Library;
        public int Saves;
        public bool FailSave;
        public Fixture()
        {
            Library = new() { ProjectId = Project };
            var store = AiTextRepairTests.Proxy<IAiJobStore>((method, args) => method.Name switch {
                nameof(IAiJobStore.ReadAsync) => Task.FromResult(new AiQueueDocument { Jobs = Headers.ToArray() }),
                nameof(IAiJobStore.ReadSnapshotAsync) => Task.FromResult(JsonSerializer.SerializeToElement(Requests[(Guid)args![0]!], AtomicJsonFile.Options)),
                nameof(IAiJobStore.ReadArtifactAsync) => Task.FromResult(Results.GetValueOrDefault((Guid)args![0]!)),
                _ => throw new InvalidOperationException("Unexpected queue write: " + method.Name)
            });
            var projects = AiTextRepairTests.Proxy<IProjectStore>((method, _) => method.Name == nameof(IProjectStore.GetAsync)
                ? Task.FromResult<ProjectInfo?>(new() { SchemaVersion = 1, Id = Project, Name = "QA", CreatedUtc = DateTimeOffset.UtcNow })
                : throw new InvalidOperationException(method.Name));
            var assets = AiTextRepairTests.Proxy<IAssetStore>((method, _) => method.Name == nameof(IAssetStore.LoadAsync)
                ? Task.FromResult(Library.Copy()) : throw new InvalidOperationException("No media reads or writes: " + method.Name));
            var reels = AiTextRepairTests.Proxy<IAssetReelStore>((method, args) => {
                if (method.Name != nameof(IAssetReelStore.SaveDraftAsync)) throw new InvalidOperationException(method.Name);
                Saves++;
                if (FailSave) { FailSave = false; throw new WorkspaceStoreException("Simulated save failure"); }
                var draft = ((ReferenceReelDraft)args![1]!).Copy(); draft.Revision++;
                Library = Library with { ReelDrafts = [draft] };
                return Task.FromResult(draft.Copy());
            });
            var capture = new AiTextJobCapture(AiTextRepairTests.NoCalls<IAiSettingsStore>(), projects, assets,
                AiTextRepairTests.NoCalls<IPromptEnhancer>(), AiTextRepairTests.NoCalls<IGuidanceAssistant>());
            Service = new(store, capture, AiTextRepairTests.NoCalls<IProductionStore>(),
                AiTextRepairTests.NoCalls<IShotStore>(), assets, reels, projects);
        }
        public AiJobHeader Add(AiTextJobRequest request, string raw, AiJobHeader? header = null)
        {
            header ??= AiTextRepairTests.Header(request); Headers.Add(header); Requests[header.Id] = request;
            Results[header.Id] = AiTextResults.Parse(request, raw, "stop"); return header;
        }
        public (AiJobHeader Root, AiJobHeader Child) Reel()
        {
            var asset = Guid.NewGuid();
            var draft = new ReferenceReelDraft { AssetId = asset, Speaker = "Clara", Line = "Hello.", Revision = 3,
                Images = [new() { AssetId = asset, MediaId = Guid.NewGuid(), Name = "Face" }] };
            var original = AiTextRepairTests.ShotRequest(Project) with { Kind = AiJobKind.ReelComposition, Profile = ReferenceReels.Profile,
                Task = JsonSerializer.SerializeToElement(new ReelCompositionRequest(Project, draft, ReferenceReels.Fingerprint(draft),
                    new(asset, "Clara", "", "", null, "", "", ""), [new(draft.Images[0].Id, AiTextRepairTests.Hash)]), AtomicJsonFile.Options) };
            const string raw = "{\"prompt\":\"Incomplete\",\"useGuidance\":\"\"}";
            var root = Add(original, raw);
            var repaired = AiTextRepairTests.Repaired(original, root, raw);
            var child = Add(repaired, raw, AiTextRepairTests.Header(repaired) with { State = AiJobState.Waiting });
            draft.PendingJobId = root.Id;
            Library = new() { ProjectId = Project, Assets = [new() { Id = asset, Name = "Clara", Category = AssetCategory.Character }], ReelDrafts = [draft] };
            return (root, child);
        }
    }
    [Fact]
    public async Task RepairCaptureReadsSavedSourcesAndNeedsNoCurrentImagesOrSettings()
    {
        var f = new Fixture(); var request = AiTextRepairTests.ShotRequest(f.Project); var source = f.Add(request, AiTextRepairTests.Invalid);
        var offer = await f.Service.InspectAsync(source.Id, _ct); Assert.NotNull(offer); Assert.Null(offer.Issue);
        var captured = await f.Service.CaptureAsync(source.Id, Guid.NewGuid(), Guid.NewGuid(), ct: _ct);
        var child = captured.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(source.Id, child.Repair!.SourceJobId); Assert.False(child.InspectsImages);
        Assert.Equal(request.Model, child.Model); Assert.Equal(AiTextRepairTests.Invalid, child.Repair.FailedResponse);
        Assert.Equal(0, f.Saves); Assert.Single(f.Headers);
    }
    [Fact]
    public async Task AStaleOfferCannotRepairACancelledOrNowActiveRequest()
    {
        var f = new Fixture(); var request = AiTextRepairTests.ShotRequest(f.Project); var source = f.Add(request, AiTextRepairTests.Invalid);
        Assert.Null((await f.Service.InspectAsync(source.Id, _ct))!.Issue);
        f.Headers[0] = source with { CancelRequested = true };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.CaptureAsync(source.Id, Guid.NewGuid(), Guid.NewGuid(), ct: _ct));
    }
    [Fact]
    public async Task ExistingRepairIsRediscoveredAndNotAutomaticallyRepeated()
    {
        var f = new Fixture(); var original = AiTextRepairTests.ShotRequest(f.Project); var root = f.Add(original, AiTextRepairTests.Invalid);
        var childRequest = AiTextRepairTests.Repaired(original, root);
        var child = f.Add(childRequest, AiTextRepairTests.Invalid, AiTextRepairTests.Header(childRequest) with { State = AiJobState.Waiting });
        Assert.Equal(child.Id, (await f.Service.InspectAsync(root.Id, _ct))!.ExistingRepair!.Id);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.CaptureAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), ct: _ct));
    }
    [Fact]
    public async Task AnotherRepairRequiresExplicitAcknowledgementOfAFailedChild()
    {
        var f = new Fixture(); var original = AiTextRepairTests.ShotRequest(f.Project); var root = f.Add(original, AiTextRepairTests.Invalid);
        var childRequest = AiTextRepairTests.Repaired(original, root); var child = f.Add(childRequest, AiTextRepairTests.Invalid);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.CaptureAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), ct: _ct));
        var next = await f.Service.CaptureAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), child.Id, _ct);
        Assert.Equal(child.Id, next.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Repair!.ReviewPredecessorJobId);
        f.Headers[1] = child with { State = AiJobState.Completed, Error = null };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.CaptureAsync(root.Id, Guid.NewGuid(), Guid.NewGuid(), child.Id, _ct));
    }
    [Fact]
    public async Task ConnectingReelReviewChangesOnlyThePointerAndIsIdempotent()
    {
        var f = new Fixture(); var (_, child) = f.Reel(); var before = f.Library.ReelDrafts[0].Copy();
        await f.Service.ConnectReviewAsync(child.Id, _ct); await f.Service.ConnectReviewAsync(child.Id, _ct);
        var after = f.Library.ReelDrafts[0];
        Assert.Equal(child.Id, after.PendingJobId); Assert.Equal(before.Prompt, after.Prompt); Assert.Equal(before.UseGuidance, after.UseGuidance);
        Assert.Equal(ReferenceReels.Fingerprint(before), ReferenceReels.Fingerprint(after)); Assert.Equal(1, f.Saves);
    }
    [Theory]
    [InlineData("prompt")][InlineData("dialogue")][InlineData("dismissed")][InlineData("new-review")]
    public async Task ConnectingRepairCannotOverwriteEditedOrDismissedReel(string change)
    {
        var f = new Fixture(); var (_, child) = f.Reel(); var draft = f.Library.ReelDrafts[0];
        switch (change) {
            case "prompt": draft.Prompt = "New authored prompt"; break;
            case "dialogue": draft.Line = "New line"; break;
            case "dismissed": draft.PendingJobId = null; break;
            default: draft.PendingJobId = Guid.NewGuid(); break;
        }
        var before = JsonSerializer.Serialize(draft);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.ConnectReviewAsync(child.Id, _ct));
        Assert.Equal(before, JsonSerializer.Serialize(f.Library.ReelDrafts[0])); Assert.Equal(0, f.Saves);
    }
    [Fact]
    public async Task FailedReviewSaveCanRetryWithoutAnySubmissionOrInference()
    {
        var f = new Fixture(); var (root, child) = f.Reel(); f.FailSave = true;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.ConnectReviewAsync(child.Id, _ct));
        Assert.Equal(root.Id, f.Library.ReelDrafts[0].PendingJobId);
        await f.Service.ConnectReviewAsync(child.Id, _ct);
        Assert.Equal(child.Id, f.Library.ReelDrafts[0].PendingJobId); Assert.Equal(2, f.Headers.Count); Assert.Equal(2, f.Saves);
    }
}
