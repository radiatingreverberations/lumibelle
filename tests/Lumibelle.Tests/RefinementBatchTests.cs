using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Services.Assets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using SixLabors.ImageSharp;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private sealed class IdleVideoHandler : IAiJobHandler
    {
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.Video, AiJobKind.ReelVideo];
        public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => throw new NotSupportedException();
        public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed record RefinementFixture(Guid Project, FileShotStore Shots, FileAssetStore Assets, FileAiJobStore Jobs, AiJobCoordinator Queue,
        MockVideoGenerator Generator, AiVideoJobCapture Capture, AiReelCapture ReelCapture, FakeAiSettingsStore Settings, MotionMediaFake Media,
        Func<RefinementBatchWorkflow> Restart);

    private RefinementFixture Refinement()
    {
        var f = Fixture(); var settings = new FakeAiSettingsStore(); var media = new MotionMediaFake();
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "refinement-jobs"), TimeProvider.System);
        var queue = new AiJobCoordinator(jobs, settings, [new IdleVideoHandler()], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var generator = new MockVideoGenerator(f.Assets, f.Shots, media);
        var capture = new AiVideoJobCapture(f.Shots, ApprovedShot(f.Project.Id).Scripts, f.Assets, settings, generator, projects,
            new FakeProjectAiPreferencesStore(), referenceVideos: media, jobs: jobs);
        var reels = new AiReelCapture(f.Assets, f.Assets, settings, generator, projects, new FakeProjectAiPreferencesStore(), jobs, media);
        // Each call models a fresh process: new workflow and store instances over the same files.
        return new(f.Project.Id, f.Shots, f.Assets, jobs, queue, generator, capture, reels, settings, media, () => new RefinementBatchWorkflow(new FileRefinementBatchStore(f.Files),
            capture, reels, jobs, queue, f.Shots, f.Assets, f.Assets, settings, generator, media, TimeProvider.System));
    }

    private async Task<ShotTake> RefinableTake(RefinementFixture r, string title, double seconds = 5, ShotTake? frameFrom = null)
    {
        var shot = Ready() with { Title = title, Duration = seconds };
        if (frameFrom is not null) shot.ShotFrames = [new(Guid.NewGuid(), frameFrom.Id, frameFrom.FrameCount - 1, "Earlier · last frame")];
        var doc = await r.Shots.LoadAsync(r.Project, _ct);
        await r.Shots.SaveAsync(r.Project, [.. doc.Shots, shot], doc.Revision, ct: _ct);
        return (await AddTake(r.Project, r.Shots, shot)).Takes.Single(t => t.ShotId == shot.Id);
    }

    private async Task<RefinementBatchEntry> TakeEntry(RefinementFixture r, string title)
    {
        var take = await RefinableTake(r, title);
        var draft = await r.Capture.PrepareMotionReferenceAsync(r.Project, take.Id, _ct);
        return new(RefinementSourceKind.Take, take.Id, title, new(draft, draft.Source.BasePrompt, draft.Source.Instructions));
    }

    private static RefinementBatch Run(RefinementBatches doc, Guid id) => doc.Runs.Single(r => r.Id == id);

    [Fact]
    public async Task HeldBatchPreparesInTheBackgroundThenSubmitsOneRenderAtATime()
    {
        var r = Refinement(); var workflow = r.Restart();
        var entries = new[] { await TakeEntry(r, "One"), await TakeEntry(r, "Two"), await TakeEntry(r, "Three") };
        var request = new RefinementBatchRequest(Guid.NewGuid(), "", StartNow: false, entries);
        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), request, _ct);
        // A repeated Queue click returns the same batch instead of duplicating clips.
        var repeated = await workflow.CreateAsync(r.Project, Guid.NewGuid(), request, _ct);
        Assert.Equal(batch.Items.Select(i => i.Id), repeated.Items.Select(i => i.Id));
        Assert.Single((await workflow.LoadAsync(r.Project, _ct)).Runs);
        var revision = (await workflow.LoadAsync(r.Project, _ct)).Revision;
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal(revision, (await workflow.LoadAsync(r.Project, _ct)).Revision);
        Assert.Equal(RefinementBatchState.Held, batch.State);
        Assert.All(batch.Items, i => { Assert.Equal(RefinementItemState.Preparing, i.State); Assert.NotNull(i.Options.Seed); });
        Assert.Equal("Refine 3 clips", batch.Name);

        // An app restart during preparation left partial inputs; a new process rebuilds them.
        var interrupted = Path.Combine(await r.Shots.RunDirectoryAsync(r.Project, batch.Items[0].Id, _ct), "inputs");
        Directory.CreateDirectory(interrupted); await File.WriteAllBytesAsync(Path.Combine(interrupted, "partial.png"), [9], _ct);
        workflow = r.Restart();
        var checks = r.Generator.CheckCalls;
        foreach (var item in batch.Items) await workflow.PrepareAsync(r.Project, batch.Id, item.Id, _ct);
        Assert.False(File.Exists(Path.Combine(interrupted, "partial.png")));
        Assert.Equal(1, r.Generator.CheckCalls - checks);
        batch = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        Assert.All(batch.Items, i => {
            Assert.Equal(RefinementItemState.Ready, i.State);
            Assert.Equal(i.Options.Seed, Assert.Single(i.Submission!.Batch!.Candidates).Seed);
            var captured = i.Submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
            Assert.Equal("standard", captured.Snapshot.Preset!.Key); Assert.Null(captured.Snapshot.Shot.SelectedTakeId);
        });

        // Held: prepared but nothing reaches the GPU queue until started.
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Empty((await r.Jobs.ReadAsync(_ct)).Jobs);
        await workflow.StartAsync(r.Project, batch.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct); await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal(batch.Items[0].Id, Assert.Single((await r.Jobs.ReadAsync(_ct)).Jobs).Id);
        var views = RefinementBatchWorkflow.View(Run(await workflow.LoadAsync(r.Project, _ct), batch.Id), (await r.Jobs.ReadAsync(_ct)).Jobs);
        Assert.Equal([RefinementItemStage.Waiting, RefinementItemStage.Waiting, RefinementItemStage.Waiting], views.Select(v => v.Stage));

        // The first render publishes a take; the batch links it and submits the next clip.
        var first = batch.Items[0];
        var take = await PublishRefinedTake(r, first);
        await r.Jobs.UpdateAsync(first.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        batch = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        Assert.Equal(take, batch.Items[0].ResultId);
        Assert.Equal(RefinementItemState.Submitted, batch.Items[1].State);
        Assert.Equal(2, (await r.Jobs.ReadAsync(_ct)).Jobs.Count);
        Assert.Null((await r.Shots.LoadAsync(r.Project, _ct)).Shots.Single(s => s.Id == first.OwnerId).SelectedTakeId);

        // Pause after the current clip: the render finishes, the third clip waits.
        await workflow.PauseAsync(r.Project, batch.Id, _ct);
        Assert.True(Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).PauseRequested);
        await PublishRefinedTake(r, batch.Items[1]);
        await r.Jobs.UpdateAsync(batch.Items[1].Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        batch = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        Assert.Equal(RefinementBatchState.Paused, batch.State); Assert.Equal(RefinementItemState.Ready, batch.Items[2].State);
        Assert.Equal(2, (await r.Jobs.ReadAsync(_ct)).Jobs.Count);

        await workflow.CancelWaitingAsync(r.Project, batch.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        batch = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        Assert.Equal(RefinementBatchState.Finished, batch.State); Assert.Equal(RefinementItemState.Cancelled, batch.Items[2].State);
        var counts = RefinementBatchWorkflow.Count(RefinementBatchWorkflow.View(batch, (await r.Jobs.ReadAsync(_ct)).Jobs));
        Assert.Equal(new RefinementBatchCounts(2, 0, 0, 0, 3), counts);

        // Accepting selects the refined take; the original stays until then.
        await workflow.AcceptAsync(r.Project, batch.Id, first.Id, _ct);
        Assert.Equal(take, (await r.Shots.LoadAsync(r.Project, _ct)).Shots.Single(s => s.Id == first.OwnerId).SelectedTakeId);
        Assert.NotNull(Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0].ReviewedUtc);
    }

    [Fact]
    public async Task PreparationFailureLeavesOtherClipsUsableAndRetryKeepsTheFrozenSeedWithoutTheSource()
    {
        var r = Refinement(); var workflow = r.Restart();
        var entries = new[] { await TakeEntry(r, "PREPARATION_FAILURE"), await TakeEntry(r, "Usable") };
        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "Night scene", true, entries), _ct);
        foreach (var item in batch.Items) await workflow.PrepareAsync(r.Project, batch.Id, item.Id, _ct);
        batch = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        Assert.Equal(RefinementItemState.Failed, batch.Items[0].State); Assert.Equal("Mock media preparation failed.", batch.Items[0].Error);
        Assert.Equal(RefinementItemState.Ready, batch.Items[1].State);
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal(batch.Items[1].Id, Assert.Single((await r.Jobs.ReadAsync(_ct)).Jobs).Id);

        // Source takes may be deleted: queued items own their guide copies and frozen settings.
        var doc = await r.Shots.LoadAsync(r.Project, _ct);
        foreach (var take in doc.Takes.ToArray()) doc = await r.Shots.DiscardAsync(r.Project, take.Id, ShotTrashKind.Take, doc.Revision, _ct);
        var cancelled = batch.Items[1];
        await r.Queue.CancelAsync(cancelled.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        await workflow.RetryAsync(r.Project, batch.Id, cancelled.Id, _ct);
        batch = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        var retried = batch.Items[1];
        Assert.NotEqual(cancelled.Id, retried.Id); Assert.Equal(RefinementItemState.Preparing, retried.State);
        Assert.Equal(cancelled.Options.Seed, retried.Options.Seed); Assert.Equal(cancelled.Options.BasePrompt, retried.Options.BasePrompt);
        await workflow.PrepareAsync(r.Project, batch.Id, retried.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        var resubmitted = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[1];
        Assert.Equal(RefinementItemState.Submitted, resubmitted.State);
        Assert.Equal(cancelled.Options.Seed, resubmitted.Submission!.Batch!.Candidates.Single().Seed);
        Assert.Equal(cancelled.Submission!.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Prompt,
            resubmitted.Submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Prompt);
    }

    [Fact]
    public async Task ClipsRenderInBatchOrderAndFailedClipsAreRetriedTogetherInPlace()
    {
        var r = Refinement(); var workflow = r.Restart();
        var entries = new[] { await TakeEntry(r, "PREPARATION_FAILURE"), await TakeEntry(r, "Second"), await TakeEntry(r, "Third") };
        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "", true, entries), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.RetryFailedAsync(r.Project, batch.Id, _ct));
        // The third clip is prepared first, but earlier clips still being prepared hold it back.
        await workflow.PrepareAsync(r.Project, batch.Id, batch.Items[2].Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Empty((await r.Jobs.ReadAsync(_ct)).Jobs);
        // A failed clip does not hold back the rest.
        await workflow.PrepareAsync(r.Project, batch.Id, batch.Items[0].Id, _ct);
        await workflow.PrepareAsync(r.Project, batch.Id, batch.Items[1].Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal(batch.Items[1].Id, Assert.Single((await r.Jobs.ReadAsync(_ct)).Jobs).Id);

        await workflow.RetryFailedAsync(r.Project, batch.Id, _ct);
        var retried = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id);
        Assert.Equal(batch.Items.Select(i => i.Id), retried.Items.Select(i => i.Id));
        Assert.Equal([RefinementItemState.Preparing, RefinementItemState.Submitted, RefinementItemState.Ready], retried.Items.Select(i => i.State));
        Assert.Null(retried.Items[0].Error); Assert.Equal(batch.Items[0].Options.Seed, retried.Items[0].Options.Seed);
    }

    [Fact]
    public async Task ARefinementKeepsItsTakeReferencesAfterTheirImageIsAssignedALook()
    {
        var f = Fixture(); var look = new CharacterLook { Name = "Nightshirt" };
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        library = await f.Assets.SaveAsync(library with { Assets = [new ReferenceAsset { Id = Guid.NewGuid(), Name = "Jan", Category = AssetCategory.Character, Looks = [look] }] }, library.Revision, _ct);
        using (var bytes = new MemoryStream(AssetStoreTests.Png(32, 32)))
            library = await f.Assets.AddImageAsync(f.Project.Id, library.Assets[0].Id, bytes, new("jan.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var image = library.Assets[0].Images[0];
        var shot = Ready(); shot.Images = [new() { AssetId = library.Assets[0].Id, MediaId = image.Id, Name = "Jan", InferUsage = true, Role = "Let AI decide" }];
        await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { Images = [image with { LookId = look.Id }] }] }, library.Revision, _ct);
        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools());
        // A new request composes its prompt from the current looks, so the changed assignment is reviewed first.
        Assert.Contains("assignment changed", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.ValidateInputsAsync(Snapshot(f.Project.Id, shot), _ct))).Message);
        // A refinement reuses the same images and the take's prompt.
        var media = new ReferenceVideoMedia(Guid.NewGuid(), "", 1, 32, 32, 24, 24, 1, false);
        await generator.ValidateInputsAsync(Snapshot(f.Project.Id, shot) with { MotionReference = new(Guid.NewGuid(), Guid.NewGuid(), media, 24, 24, "", "") }, _ct);
    }

    [Fact]
    public async Task OversizedSharedResolutionIsRejectedPerClipAndAnotherShotRequestMakesTheBatchWait()
    {
        var r = Refinement(); var workflow = r.Restart();
        var entry = await TakeEntry(r, "Busy shot");
        // The mock clip is 32 × 32, so every preset fits; a source wider than the preset does not.
        var oversized = entry.Options with { Draft = entry.Options.Draft with { Source = entry.Options.Draft.Source with { Media = entry.Options.Draft.Source.Media with { Width = 4096, Height = 2304 } } } };
        Assert.Contains("larger than the available output presets", MotionReferencePolicy.Issue(oversized));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "", true, [entry with { Options = oversized }]), _ct));

        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "", true, [entry]), _ct);
        await workflow.PrepareAsync(r.Project, batch.Id, batch.Items[0].Id, _ct);
        var blocker = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.Video, AiBackend.ComfyUI, new(r.Project, ShotId: batch.Items[0].OwnerId), "Shots", "Other", Guid.NewGuid(), new { });
        await r.Jobs.EnqueueAsync(blocker, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        var waiting = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0];
        Assert.Equal(RefinementItemState.Ready, waiting.State); Assert.Contains("another request on this shot", waiting.Notice);
        await r.Queue.CancelAsync(blocker.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal(RefinementItemState.Submitted, Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0].State);
    }

    [Fact]
    public async Task RefineDialogFlagsRowsSharesOneModelCheckAndQueuesAHeldBatch()
    {
        var r = Refinement(); r.Media.Width = 1120; r.Media.Height = 640;
        var one = await RefinableTake(r, "One"); var two = await RefinableTake(r, "Two", frameFrom: one); var brief = await RefinableTake(r, "Brief", 1);
        var workflow = r.Restart(); var checks = r.Generator.CheckCalls;
        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddMudServices(); ui.Services.AddSingleton(r.Capture); ui.Services.AddSingleton(r.ReelCapture);
        ui.Services.AddSingleton<IAssetStore>(r.Assets); ui.Services.AddSingleton<IAiSettingsStore>(r.Settings);
        ui.Services.AddSingleton(workflow); ui.Services.AddSingleton<IAiReviewGate>(new ScriptReviewGate());
        var dialogs = ui.Render<MudDialogProvider>();
        RefinementBatch? queued = null;
        IReadOnlyList<RefineSource> sources = [RefineSource.From(r.Project, one, "One"), RefineSource.From(r.Project, two, "Two"), RefineSource.From(r.Project, brief, "Brief")];
        ui.Render<RefineFromVideoDialog>(p => p.Add(d => d.ProjectId, r.Project).Add(d => d.Sources, sources).Add(d => d.Queued, b => queued = b));
        dialogs.WaitForAssertion(() => Assert.Contains("Standard H3 model ready.", dialogs.Markup), TimeSpan.FromSeconds(10));
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Preparing sources", dialogs.Markup), TimeSpan.FromSeconds(10));

        // Sources below 2 seconds are flagged and left out; the others show source and effective output sizes.
        var briefRow = dialogs.FindAll(".refine-row").Single(row => row.TextContent.Contains("Brief"));
        Assert.Contains("Choose a visible clip lasting 2–15 seconds", briefRow.TextContent);
        Assert.False(briefRow.QuerySelector("input[aria-label='Include Brief']")!.HasAttribute("checked"));
        Assert.Contains("1120 × 640 → Native · 1 MP · 1344 × 768", dialogs.FindAll(".refine-row")[0].TextContent);
        Assert.Contains("Queue 2 refinements", dialogs.Markup);
        // Two's shot frame comes from One's take, so it can wait for One's refined final.
        Assert.True(dialogs.Find("input[aria-label='Use refined frames from earlier clips']").HasAttribute("checked"));
        Assert.Contains("Uses the refined One once rendered", dialogs.FindAll(".refine-row").Single(row => row.TextContent.Contains("Two")).TextContent);

        // A shared size below the source flags each clip it cannot apply to.
        // The dialog content renders inside the provider; refresh its snapshot before each interaction.
        void Act(Action action) { dialogs.Render(); action(); }
        Act(() => dialogs.Find("select[aria-label='Shared output resolution']").Change("quick"));
        Assert.Equal(2, dialogs.FindAll(".refine-row.needs-adjustment").Count);
        Assert.True(dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Queue 2 refinements").HasAttribute("disabled"));
        Act(() => dialogs.Find("button[aria-label='Edit One']").Click());
        Act(() => dialogs.Find("select[aria-label='Output resolution for One']").Change("detail"));
        Assert.Contains("1120 × 640 → Detail · 0.7 MP · 1120 × 640", dialogs.FindAll(".refine-row")[0].TextContent);
        Assert.Contains("Custom settings", dialogs.Markup);
        Act(() => dialogs.Find("input[aria-label='Include Two']").Change(false));
        Assert.Empty(dialogs.FindAll(".refine-row.needs-adjustment"));
        Assert.Empty(dialogs.FindAll("input[aria-label='Use refined frames from earlier clips']"));
        Act(() => dialogs.FindAll("input[type=radio]")[1].Change(true));
        await dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Queue refinement").ClickAsync(new());
        dialogs.WaitForAssertion(() => Assert.NotNull(queued));

        Assert.Equal(RefinementBatchState.Held, queued!.State); Assert.False(queued.UseRefinedFrames);
        var item = Assert.Single(queued.Items);
        Assert.Equal(one.Id, item.SourceId); Assert.Equal(VideoResolution.Detail, item.Options.Resolution); Assert.NotNull(item.Options.Seed);
        await workflow.PrepareAsync(r.Project, queued.Id, item.Id, _ct);
        Assert.Equal(1, r.Generator.CheckCalls - checks);
        var prepared = Run(await workflow.LoadAsync(r.Project, _ct), queued.Id).Items[0];
        Assert.Equal((1120, 640), (prepared.Submission!.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Width,
            prepared.Submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Height));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ALaterClipWaitsForAndUsesTheRefinedFrameOfAnEarlierClip(bool earlierRenders)
    {
        var r = Refinement(); var workflow = r.Restart();
        var opening = await RefinableTake(r, "Black, then upright");
        // The later shot's draft referenced the earlier draft's last frame.
        var later = Ready() with { Title = "Weight on the bed", Duration = 5 };
        later.ShotFrames = [new(Guid.NewGuid(), opening.Id, opening.FrameCount - 1, "Black, then upright · last frame")];
        var doc = await r.Shots.LoadAsync(r.Project, _ct);
        await r.Shots.SaveAsync(r.Project, [.. doc.Shots, later], doc.Revision, ct: _ct);
        var laterTake = (await AddTake(r.Project, r.Shots, later)).Takes.Single(t => t.ShotId == later.Id);
        async Task<RefinementBatchEntry> Entry(ShotTake take, string name)
        {
            var draft = await r.Capture.PrepareMotionReferenceAsync(r.Project, take.Id, _ct);
            return new(RefinementSourceKind.Take, take.Id, name, new(draft, draft.Source.BasePrompt, draft.Source.Instructions));
        }
        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "", true,
            [await Entry(laterTake, "Weight on the bed · Take 1"), await Entry(opening, "Black, then upright · Take 1")], UseRefinedFrames: true), _ct);
        Assert.True(batch.UseRefinedFrames);
        var (dependent, first) = (batch.Items[0], batch.Items[1]);

        // The later clip waits and then gives up its preparation slot to unrelated clips.
        await workflow.PrepareAsync(r.Project, batch.Id, dependent.Id, _ct);
        var waiting = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0];
        Assert.Equal(RefinementItemState.Preparing, waiting.State); Assert.Equal(first.Id, waiting.WaitingForItemId);
        Assert.Contains("Waiting for the refined take of Black, then upright · Take 1.", waiting.Notice);
        Assert.Equal([first.Id], (await workflow.PreparingAsync(r.Project, _ct)).Select(p => p.Item));

        await workflow.PrepareAsync(r.Project, batch.Id, first.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        first = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[1];
        Assert.Equal(RefinementItemState.Submitted, first.State);
        // The earlier render keeps the frame the later clip uses losslessly, without a full frame archive.
        var firstRequest = first.Submission!.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.False(firstRequest.Snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.Equal([firstRequest.Snapshot.FrameCount - 1], firstRequest.Snapshot.OutputPolicy.KeptFrames);
        Guid? refined = null;
        if (earlierRenders)
        {
            refined = await PublishRefinedTake(r, first, keptFrames: true);
            await using (var keptFrame = await r.Shots.OpenAsync(r.Project, refined.Value, ShotTrashKind.Take, firstRequest.Snapshot.FrameCount - 1, ct: _ct))
                Assert.Equal(200, SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(keptFrame!.Content)[0, 0].R);
            await r.Jobs.UpdateAsync(first.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        }
        else await r.Queue.CancelAsync(first.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal([dependent.Id], (await workflow.PreparingAsync(r.Project, _ct)).Select(p => p.Item));
        await workflow.PrepareAsync(r.Project, batch.Id, dependent.Id, _ct);
        var prepared = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0];
        Assert.Equal(RefinementItemState.Ready, prepared.State); Assert.Null(prepared.WaitingForItemId); Assert.Null(prepared.Notice);
        var frame = Assert.Single(prepared.Submission!.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Shot.ShotFrames);
        if (earlierRenders)
        {
            var take = (await r.Shots.LoadAsync(r.Project, _ct)).Takes.Single(t => t.Id == refined);
            Assert.Equal((refined!.Value, take.FrameCount - 1), (frame.TakeId, frame.Frame));
            Assert.Equal(["A shot frame uses the refined take of Black, then upright · Take 1."], prepared.Adjustments);
        }
        else
        {
            // An unavailable final keeps the draft frame, so an overnight batch continues.
            Assert.Equal((opening.Id, opening.FrameCount - 1), (frame.TakeId, frame.Frame));
            Assert.Contains("keeps the draft of Black, then upright · Take 1", Assert.Single(prepared.Adjustments));
        }
        Assert.Equal(dependent.Options.Draft.Shot.ShotFrames[0].Id, frame.Id);
    }

    [Fact]
    public void KeptFramesAreSavedWithStockNodesOnlyWhenTheFullArchiveIsOff()
    {
        var shot = Ready() with { Duration = 5, SaveLosslessFrames = false };
        var snapshot = ReferenceSnapshot(shot) with { OutputPolicy = new(false) { KeptFrames = [0, 123] }, Preset = H3Presets.Capture(shot, new()), Performance = H3Performance.Capture(new()) };
        H3Presets.Validate(snapshot);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "attempt", [])).GetProperty("prompt");
        Assert.False(graph.TryGetProperty("15", out _));
        foreach (var (frame, index) in new[] { (0, 0), (123, 1) })
        {
            var pick = graph.GetProperty(ComfyH3Video.KeptPickNode(index));
            Assert.Equal("ImageFromBatch", pick.GetProperty("class_type").GetString());
            Assert.Equal(frame, pick.GetProperty("inputs").GetProperty("batch_index").GetInt32());
            Assert.Equal("11", pick.GetProperty("inputs").GetProperty("image")[0].GetString());
            var save = graph.GetProperty(ComfyH3Video.KeptSaveNode(index));
            Assert.Equal("SaveImage", save.GetProperty("class_type").GetString());
            Assert.EndsWith($"kept-frame-{frame:D4}", save.GetProperty("inputs").GetProperty("filename_prefix").GetString());
        }
        // Kept frames never accompany the full archive, never fall outside the clip and stay ordered.
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Validate(snapshot with { OutputPolicy = new(false) { KeptFrames = [124] } }));
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Validate(snapshot with { OutputPolicy = new(false) { KeptFrames = [5, 2] } }));
        var archived = ReferenceSnapshot(shot with { SaveLosslessFrames = true }) with { Preset = H3Presets.Capture(shot, new()), Performance = H3Performance.Capture(new()) };
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Validate(archived with { OutputPolicy = new(true) { KeptFrames = [0] } }));
        // Requests without kept frames keep their earlier JSON and graph.
        Assert.DoesNotContain("keptFrames", JsonSerializer.Serialize(new H3OutputPolicy(false), AtomicJsonFile.Options));
    }

    [Fact]
    public async Task KeptFramesDownloadResumeAndMustMatchTheTakeSize()
    {
        var f = Fixture(); var shot = Ready() with { SaveLosslessFrames = false };
        var snap = Snapshot(f.Project.Id, shot); var last = snap.FrameCount - 1;
        snap = snap with { OutputPolicy = new(false) { KeptFrames = [0, last] } };
        var run = new VideoRun { Snapshot = snap }; var c = new VideoCandidate { Number = 1 };
        c.Output = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> {
            ["14"] = new { images = new[] { new { filename = "video.mp4" } } },
            [ComfyH3Video.KeptSaveNode(0)] = new { images = new[] { new { filename = "kept-first.png" } } },
            [ComfyH3Video.KeptSaveNode(1)] = new { images = new[] { new { filename = "kept-last.png" } } } } });
        byte[] Png(int size) { using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(size, size); using var stream = new MemoryStream(); image.SaveAsPng(stream); return stream.ToArray(); }
        var fail = true; var size = 32; Dictionary<string, int> requests = [];
        using var handler = new ScriptedHttpHandler((r, ct) => {
            var file = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["filename"]!; requests[file] = requests.GetValueOrDefault(file) + 1;
            if (file == "kept-last.png" && fail) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(file.EndsWith("mp4") ? [1, 2, 3] : Png(size)) });
        });
        var generator = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(snap.FrameCount));
        var dir = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, run.Id, _ct), "candidate-1");
        await Assert.ThrowsAsync<HttpRequestException>(() => generator.DownloadAsync(run, c, dir, _ => Task.CompletedTask, _ct));
        Assert.True(File.Exists(Path.Combine(dir, TakeKeptFrame.File(0)))); Assert.False(File.Exists(Path.Combine(dir, TakeKeptFrame.File(last))));
        fail = false;
        var take = await generator.DownloadAsync(run, c, dir, _ => Task.CompletedTask, _ct);
        Assert.Equal((1, 1, 2), (requests["video.mp4"], requests["kept-first.png"], requests["kept-last.png"]));
        Assert.Empty(take.Frames);
        Assert.Equal([0, last], take.KeptFrames!.Select(k => k.Frame));
        Assert.Equal(3 + take.KeptFrames!.Sum(k => k.Bytes), take.Bytes);
        Assert.Equal(TakeBundles.Files(take).Where(file => file.StartsWith("kept-frame-")), take.KeptFrames!.Select(k => k.FileName));

        // A frame of another size is rejected and removed for a retry.
        foreach (var file in take.KeptFrames!) File.Delete(Path.Combine(dir, file.FileName));
        size = 16;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.DownloadAsync(run, c, dir, _ => Task.CompletedTask, _ct));
        Assert.False(File.Exists(Path.Combine(dir, TakeKeptFrame.File(0))));
    }

    [Fact]
    public async Task AThreeClipCycleKeepsItsDraftFramesInsteadOfWaitingForever()
    {
        var r = Refinement(); var workflow = r.Restart();
        var takes = new[] { await RefinableTake(r, "One"), await RefinableTake(r, "Two"), await RefinableTake(r, "Three") };
        // One uses a frame of Two, Two of Three, and Three of One.
        var entries = new List<RefinementBatchEntry>();
        for (var i = 0; i < takes.Length; i++)
        {
            var draft = await r.Capture.PrepareMotionReferenceAsync(r.Project, takes[i].Id, _ct);
            var next = takes[(i + 1) % takes.Length];
            draft.Shot.ShotFrames = [new(Guid.NewGuid(), next.Id, next.FrameCount - 1, $"Take {i + 2} · last frame")];
            entries.Add(new(RefinementSourceKind.Take, takes[i].Id, $"Take {i + 1}", new(draft, draft.Source.BasePrompt, draft.Source.Instructions)));
        }
        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "", false, entries, UseRefinedFrames: true), _ct);
        foreach (var item in batch.Items) await workflow.PrepareAsync(r.Project, batch.Id, item.Id, _ct);
        var prepared = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items;
        Assert.All(prepared, item => {
            Assert.Equal(RefinementItemState.Ready, item.State); Assert.Null(item.WaitingForItemId);
            Assert.Contains("keeps the draft", Assert.Single(item.Adjustments));
        });
    }

    [Fact]
    public async Task UnreachableComfyKeepsClipsPreparingAndAMissingResultFailsWithANewRequestOnRetry()
    {
        var r = Refinement(); var workflow = r.Restart();
        var ready = await r.Generator.CheckAsync(await r.Settings.LoadAsync(_ct), _ct);
        r.Generator.Catalog = ready with { DiscoverySucceeded = false };
        var batch = await workflow.CreateAsync(r.Project, Guid.NewGuid(), new(Guid.NewGuid(), "", true, [await TakeEntry(r, "Hall")]), _ct);
        var item = batch.Items[0];
        await workflow.PrepareAsync(r.Project, batch.Id, item.Id, _ct);
        var waiting = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0];
        // An overnight batch survives ComfyUI starting or restarting: the clip is tried again, not failed.
        Assert.Equal(RefinementItemState.Preparing, waiting.State); Assert.StartsWith("Waiting for ComfyUI: ", waiting.Notice);
        Assert.Equal([item.Id], (await workflow.PreparingAsync(r.Project, _ct)).Select(p => p.Item));

        r.Generator.Catalog = ready;
        workflow = r.Restart();
        await workflow.PrepareAsync(r.Project, batch.Id, item.Id, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        Assert.Equal(RefinementItemState.Submitted, Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0].State);
        // The job completed, but its take was never published to the shot (for example, it went to Trash).
        await r.Jobs.UpdateAsync(item.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        await workflow.AdvanceAsync(r.Project, _ct);
        var failed = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0];
        Assert.Equal(RefinementItemState.Failed, failed.State); Assert.Contains("Look in Trash", failed.Error);
        await workflow.RetryAsync(r.Project, batch.Id, item.Id, _ct);
        var retried = Run(await workflow.LoadAsync(r.Project, _ct), batch.Id).Items[0];
        Assert.NotEqual(item.Id, retried.Id); Assert.Equal(RefinementItemState.Preparing, retried.State); Assert.Null(retried.Submission);
    }

    private async Task<Guid> PublishRefinedTake(RefinementFixture r, RefinementBatchItem item, bool readableFrames = false, bool keptFrames = false)
    {
        var request = item.Submission!.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var dir = Path.Combine(await r.Shots.RunDirectoryAsync(r.Project, item.Id, _ct), "candidate-1"); Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "video.mp4"), [1, 2, 3], _ct);
        // Motion-reference finals default to MP4 without lossless frames.
        Assert.False(request.Snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.False(request.Snapshot.CaptureRefinementData); // Latents are off by default.
        // The fake MP4 cannot be decoded, so a take whose frames are read later keeps a lossless archive instead.
        var frames = readableFrames ? await MockFrameArchive.WriteAsync(dir, request.Snapshot.FrameCount, 32, 32, _ct) : [];
        // The frames the render was asked to keep, as the stock SaveImage nodes return them.
        List<TakeKeptFrame>? kept = null;
        if (keptFrames)
        {
            kept = [];
            foreach (var frame in request.Snapshot.OutputPolicy!.KeptFrames!)
            {
                using var png = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(32, 32, new SixLabors.ImageSharp.PixelFormats.Rgb24(200, 40, (byte)frame));
                var file = TakeKeptFrame.File(frame); await png.SaveAsPngAsync(Path.Combine(dir, file), _ct);
                kept.Add(new(frame, file, new FileInfo(Path.Combine(dir, file)).Length));
            }
        }
        // The latent package is covered by the refinement tests; this fake take has none.
        var take = new ShotTake { ShotId = item.OwnerId, RunId = item.Id, Candidate = 1, AiJobId = item.Id,
            Snapshot = request.Snapshot with { CaptureRefinementData = false, Shot = request.Snapshot.Shot with { SaveLosslessFrames = readableFrames },
                OutputPolicy = new(readableFrames) { KeptFrames = keptFrames ? request.Snapshot.OutputPolicy!.KeptFrames : null } },
            Width = 32, Height = 32, Frames = frames, KeptFrames = kept, Bytes = 3 + frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) + (kept?.Sum(f => f.Bytes) ?? 0) };
        await r.Shots.PublishTakeAsync(r.Project, take, dir, _ct);
        return take.Id;
    }
}
