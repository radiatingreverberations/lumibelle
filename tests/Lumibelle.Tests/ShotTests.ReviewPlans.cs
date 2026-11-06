using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Review;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task ProposingAPlanChangesNothingAndAnApprovedPlanRunsInDependencyOrder()
    {
        var f = await PlanFixture.Create(this, shots: 2);
        var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var plan = await f.Plans.CreateAsync(f.Project, "Fix the letter", "agent", _ct);
        var finding = await f.Plans.AddFindingAsync(f.Project, plan.Id, first, "The mouse holds nothing.", "Prompt: “looks up”", "agent", _ct);
        var prompt = await f.Propose(plan, new(ReviewStepKind.SetPrompt, first, "Name the letter.") { Edits = [("looks up", "looks up, holding a letter")], FindingIds = [finding.Id] });
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Render the fixed shot.") { Takes = 2 });
        var frame = await f.Propose(plan, new(ReviewStepKind.AddShotFrame, second, "Carry the letter into the next shot.") { Frame = new() { FromShotId = first, Position = ReviewFramePosition.Last, Name = "End of the arrival" } });
        var next = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, second, "Render with the frame."));
        var cut = await f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Show the picks."));
        await f.Plans.SubmitAsync(f.Project, plan.Id, "agent", _ct);

        // Proposing wrote to the plan only.
        Assert.Equal(PlanFixture.Prompt, (await f.Setup(first)).Prompt);
        Assert.Empty(f.Renders.Captured);
        Assert.Equal(new ReviewPlanTotals(3, 3 * H3Policy.Seconds(1), 3), ReviewPlans.Totals(await f.Plan(plan), await f.Review.LoadAsync(f.Project, _ct)));
        Assert.Equal(ReviewPlanState.Submitted, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).State);
        Assert.Equal(PlanFixture.Prompt, (await f.Setup(first)).Prompt);

        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);
        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        // The first shot's prompt is changed and reviewed, then its render is queued. Everything that needs its pick waits.
        var setup = await f.Setup(first);
        Assert.Contains("holding a letter", setup.Prompt);
        Assert.Equal(setup.Prompt, setup.Accepted?.Prompt);
        Assert.Equal(ReviewStepState.Done, running.Step(prompt.Id)!.State);
        Assert.Equal(ReviewStepState.Running, running.Step(render.Id)!.State);
        Assert.All(new[] { frame, next, cut }, s => Assert.Equal(ReviewStepState.Waiting, running.Step(s.Id)!.State));
        Assert.Equal((first, 2), Assert.Single(f.Renders.Captured));

        // The render finishes: the step waits for the director's pick, and still nothing downstream runs.
        var takes = await f.Finish(running.Step(render.Id)!, 2);
        var picking = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal(ReviewStepState.Pick, picking.Step(render.Id)!.State);
        Assert.Equal(takes, picking.Step(render.Id)!.ProducedTakeIds);
        Assert.Null((await f.Shots.LoadAsync(f.Project, _ct)).Shots[0].SelectedTakeId);
        Assert.Single(f.Renders.Captured);

        await f.Plans.RecommendAsync(f.Project, plan.Id, render.Id, takes[1], "The letter is readable.", "agent", _ct);
        var picked = await f.Runner.AcceptRecommendationsAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);

        // The pick selects the take, resolves the frame against it, and lets the second render go.
        var document = await f.Shots.LoadAsync(f.Project, _ct);
        Assert.Equal(takes[1], document.Shots[0].SelectedTakeId);
        var added = Assert.Single((await f.Setup(second)).Shot.ShotFrames);
        Assert.Equal((takes[1], document.Takes.Single(t => t.Id == takes[1]).FrameCount - 1, "End of the arrival"), (added.TakeId, added.Frame, added.Name));
        Assert.Equal(ReviewStepState.Running, picked.Step(next.Id)!.State);
        Assert.Equal(ReviewStepState.Waiting, picked.Step(cut.Id)!.State);

        var second2 = await f.Finish(picked.Step(next.Id)!, 1);
        await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        var finished = await f.Runner.PickAsync(f.Project, plan.Id, next.Id, second2[0], ReviewPlans.Director, _ct);

        Assert.Equal(ReviewPlanState.Finished, finished.State);
        Assert.All(finished.Steps, s => Assert.Equal(ReviewStepState.Done, s.State));
        Assert.Equal([takes[1], second2[0]], (await f.Cuts.LoadAsync(f.Project, _ct)).Clips.Select(c => c.TakeId));
        Assert.Contains(finished.Log, l => l.What.StartsWith("Approved revision"));
        // A plan read back from the project database is the same plan.
        Assert.Equal(finished.Steps.Select(s => (s.Id, s.State)), (await new ReviewPlanStore(f.Files, _clock).GetAsync(f.Project, plan.Id, _ct)).Steps.Select(s => (s.Id, s.State)));
    }

    [Fact]
    public async Task AnApprovalFromAClientMustNameTheRevisionAndTotalsOfThePlanAsItStands()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Another take.") { Takes = 2 });
        var draft = await f.Plan(plan); var totals = ReviewPlans.Totals(draft, await f.Review.LoadAsync(f.Project, _ct));
        // Not submitted yet.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, "Claude Code", draft.Revision, totals, _ct));
        await f.Plans.SubmitAsync(f.Project, plan.Id, "agent", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, "Claude Code", draft.Revision, totals with { Takes = 1 }, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, "Claude Code", null, null, _ct));

        // The plan changes after the agent read it: the old revision no longer approves anything.
        await f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Show the pick."));
        var e = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, "Claude Code", draft.Revision, totals, _ct));
        Assert.Contains("2 takes", e.Message);
        Assert.Equal(ReviewPlanState.Submitted, (await f.Plan(plan)).State);

        var current = await f.Plan(plan);
        var approved = await f.Plans.ApproveAsync(f.Project, plan.Id, "Claude Code", current.Revision, ReviewPlans.Totals(current, await f.Review.LoadAsync(f.Project, _ct)), _ct);
        Assert.Equal((ReviewPlanState.Approved, "Claude Code"), (approved.State, approved.ApprovedFrom));
        // Approved plans take no more steps and no second approval.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Again.")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct));
    }

    [Fact]
    public async Task ARejectedStepSkipsTheStepsThatDependOnIt()
    {
        var f = await PlanFixture.Create(this, shots: 2);
        var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var plan = await f.Plans.CreateAsync(f.Project, "Two shots", "agent", _ct);
        var prompt = await f.Propose(plan, new(ReviewStepKind.SetPrompt, first, "Fix.") { Prompt = "The mouse reads a letter." });
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Render the fix."));
        var frame = await f.Propose(plan, new(ReviewStepKind.AddShotFrame, second, "Carry it over.") { Frame = new() { FromShotId = first, Name = "End" } });
        var other = await f.Propose(plan, new(ReviewStepKind.SetPrompt, second, "Independent.") { Prompt = "The mouse leaves." });
        await f.Plans.DecideAsync(f.Project, plan.Id, prompt.Id, keep: false, _ct);
        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);

        var done = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal(ReviewStepState.Rejected, done.Step(prompt.Id)!.State);
        Assert.Equal(ReviewStepState.Skipped, done.Step(render.Id)!.State);
        Assert.Contains("did not run", done.Step(render.Id)!.Message);
        Assert.Equal(ReviewStepState.Skipped, done.Step(frame.Id)!.State);
        Assert.Equal(ReviewStepState.Done, done.Step(other.Id)!.State);
        Assert.Equal(ReviewPlanState.Finished, done.State);
        Assert.Empty(f.Renders.Captured);
        Assert.Equal(PlanFixture.Prompt, (await f.Setup(first)).Prompt);
        Assert.Equal("The mouse leaves.", (await f.Setup(second)).Prompt);
    }

    [Fact]
    public async Task AStepWrittenAgainstAnOlderPromptIsHeldUntilTheDirectorLetsItRun()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Stale", "agent", _ct);
        var step = await f.Propose(plan, new(ReviewStepKind.SetPrompt, shot, "Fix.") { Prompt = "The mouse reads a letter." });
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Render."));
        // The director edits the prompt in Shots after the agent read it.
        var setup = await f.Setup(shot); setup.Prompt = "The mouse sleeps."; await f.Production.SaveAsync(f.Project, setup, setup.Version, _ct);
        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);

        var held = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal(ReviewStepState.Held, held.Step(step.Id)!.State);
        Assert.Contains("edited after this step was written", held.Step(step.Id)!.Message);
        Assert.Equal(ReviewStepState.Waiting, held.Step(render.Id)!.State);
        Assert.Equal("The mouse sleeps.", (await f.Setup(shot)).Prompt);
        Assert.Empty(f.Renders.Captured);

        await f.Plans.ReleaseAsync(f.Project, plan.Id, step.Id, "The mouse sleeps.", _ct);
        var released = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal(ReviewStepState.Done, released.Step(step.Id)!.State);
        Assert.Equal("The mouse reads a letter.", (await f.Setup(shot)).Prompt);
        Assert.Equal(ReviewStepState.Running, released.Step(render.Id)!.State);
    }

    [Fact]
    public async Task ARefusedOrCancelledRenderFailsItsStepAndStoppingSkipsTheRest()
    {
        var f = await PlanFixture.Create(this, shots: 2);
        var plan = await f.Plans.CreateAsync(f.Project, "Renders", "agent", _ct);
        var refused = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Render."));
        var cancelled = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[1], "Render."));
        var cut = await f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Cut."));
        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);
        f.Renders.Refuse = shot => shot == f.ShotIds[0] ? "Refresh Video models to check H3." : null;

        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal((ReviewStepState.Failed, "Refresh Video models to check H3."), (running.Step(refused.Id)!.State, running.Step(refused.Id)!.Message));
        Assert.Equal(ReviewStepState.Running, running.Step(cancelled.Id)!.State);

        f.Renders.States[running.Step(cancelled.Id)!.JobId!.Value] = AiJobState.Cancelled;
        var after = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal((ReviewStepState.Failed, "The render was cancelled."), (after.Step(cancelled.Id)!.State, after.Step(cancelled.Id)!.Message));
        // The cut step had nothing left to wait for, so it ran and the plan finished.
        Assert.Equal(ReviewStepState.Done, after.Step(cut.Id)!.State);
        Assert.Equal(ReviewPlanState.Finished, after.State);

        var second = await f.Plans.CreateAsync(f.Project, "Stopped", "agent", _ct);
        var waiting = await f.Propose(second, new(ReviewStepKind.MarkPromptReviewed, f.ShotIds[0], "It still fits."));
        var stopped = await f.Plans.StopAsync(f.Project, second.Id, ReviewPlans.Director, _ct);
        Assert.Equal((ReviewPlanState.Stopped, ReviewStepState.Skipped), (stopped.State, stopped.Step(waiting.Id)!.State));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, second.Id, ReviewPlans.Director, ct: _ct));
    }

    [Fact]
    public async Task VoicePictureAndTakeStepsApplyThroughTheStores()
    {
        var f = await PlanFixture.Create(this, shots: 1, character: true); var shot = f.ShotIds[0];
        var old = await AddTake(f.Project, f.Shots, (await f.Shots.LoadAsync(f.Project, _ct)).Shots[0]); var take = old.Takes.Single();
        var plan = await f.Plans.CreateAsync(f.Project, "References", "agent", _ct);
        var voice = await f.Propose(plan, new(ReviewStepKind.SetVoice, shot, "Vera has no voice.") { CharacterAssetId = f.Vera.Id, VoiceId = f.Voice.Id });
        var picture = await f.Propose(plan, new(ReviewStepKind.ReplacePicture, shot, "The corrected portrait.") { AssetId = f.Vera.Id, ImageId = f.Vera.Images[0].Id, NewAssetId = f.Vera.Id, NewImageId = f.Vera.Images[1].Id });
        var frame = await f.Propose(plan, new(ReviewStepKind.AddShotFrame, shot, "Open on the earlier take.") { Frame = new() { TakeId = take.Id, Position = ReviewFramePosition.First, Name = "Earlier" } });
        var use = await f.Propose(plan, new(ReviewStepKind.UseTake, shot, "The only take.") { TakeId = take.Id });
        // Proposals are checked against the project when they are written.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.SetVoice, shot, "Wrong.") { CharacterAssetId = f.Vera.Id, VoiceId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.RemovePicture, shot, "Wrong.") { AssetId = f.Vera.Id, ImageId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.UseTake, shot, "Wrong.") { TakeId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Too many.") { Takes = 5 }));
        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);

        var done = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.All(done.Steps, s => Assert.True(s.State == ReviewStepState.Done, $"{s.Kind}: {s.Message}"));
        var setup = await f.Setup(shot);
        var choice = Assert.Single(setup.Shot.CharacterVoices!);
        Assert.Equal((CharacterVoiceSource.Recording, "VERA", f.Voice.Id), (choice.Source, choice.Speaker, choice.Recording!.VoiceId));
        Assert.Equal(f.Voice.Id, Assert.Single(setup.Shot.Voices).VoiceId);
        Assert.Equal(f.Vera.Images[1].Id, Assert.Single(setup.Shot.Images).MediaId);
        Assert.Equal((take.Id, 0), (setup.Shot.ShotFrames.Single().TakeId, setup.Shot.ShotFrames.Single().Frame));
        Assert.Equal(take.Id, (await f.Shots.LoadAsync(f.Project, _ct)).Shots[0].SelectedTakeId);
        Assert.Empty(CharacterVoices.Unvoiced(setup.Shot, f.Assets.Library));
    }

    [Fact]
    public async Task AStepCanBeRewrittenUntilThePlanIsApproved()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Rewrite", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "One take."));
        var before = (await f.Plan(plan)).Revision;

        // The step keeps its id and place; a second render of the same shot is still refused.
        var rewritten = await f.Plans.ProposeAsync(f.Project, plan.Id, new(ReviewStepKind.GenerateTakes, shot, "Three, to choose from.") { Takes = 3 }, "agent", replaces: render.Id, _ct);
        Assert.Equal(render.Id, rewritten.Id);
        var current = await f.Plan(plan);
        Assert.Equal((3, "Three, to choose from."), (Assert.Single(current.Steps).Takes, current.Steps[0].Reason));
        Assert.True(current.Revision > before);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Again.")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ProposeAsync(f.Project, plan.Id, new(ReviewStepKind.UpdateCut, null, "Another kind."), "agent", replaces: render.Id, _ct));

        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ProposeAsync(f.Project, plan.Id, new(ReviewStepKind.GenerateTakes, shot, "Too late.") { Takes = 1 }, "agent", replaces: render.Id, _ct));
        await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal((shot, 3), Assert.Single(f.Renders.Captured));
    }

    [Fact]
    public async Task TheBackgroundPassOnlyReadsAPlanInFullOnceARenderHasLeftTheQueue()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Background", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "One take."));
        await f.Plans.ApproveAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);

        // Startup picks up an approved plan nobody advanced, as after a restart.
        await f.Runner.AdvanceAllAsync(_ct);
        var running = await f.Plan(plan);
        Assert.Equal(ReviewStepState.Running, running.Step(render.Id)!.State);

        // Queue changes while the render runs leave the plan alone.
        var revision = (await f.Plans.Store.ListAsync(f.Project, _ct)).Single().Log.Count;
        await f.Runner.AdvanceWatchedAsync(ct: _ct);
        Assert.Equal(revision, (await f.Plan(plan)).Log.Count);

        var takes = await f.Finish(running.Step(render.Id)!, 1);
        await f.Runner.AdvanceWatchedAsync(ct: _ct);
        Assert.Equal((ReviewStepState.Pick, takes[0]), ((await f.Plan(plan)).Step(render.Id)!.State, (await f.Plan(plan)).Step(render.Id)!.ProducedTakeIds.Single()));
    }

    [Fact]
    public void PromptEditsReplaceTextThatOccursExactlyOnce()
    {
        const string prompt = "Vera stands by the window. The window is open.";
        Assert.Equal("Vera sits by the window. The window is shut.", ReviewPlans.ApplyEdits(prompt, [("stands", "sits"), ("is open", "is shut")]));
        Assert.Contains("more than once", Assert.Throws<WorkspaceStoreException>(() => ReviewPlans.ApplyEdits(prompt, [("window", "door")])).Message);
        Assert.Contains("does not contain", Assert.Throws<WorkspaceStoreException>(() => ReviewPlans.ApplyEdits(prompt, [("table", "chair")])).Message);
        Assert.Throws<WorkspaceStoreException>(() => ReviewPlans.ApplyEdits(prompt, [("", "x")]));
    }

    private sealed class PlanFixture
    {
        public const string Prompt = "The camera holds as the mouse looks up.";
        public Guid Project; public ProjectFiles Files = null!; public FileShotStore Shots = null!; public FakeAssetStore Assets = null!;
        public FileProductionStore Production = null!; public FileCutStore Cuts = null!; public ProjectReview Review = null!;
        public ReviewPlans Plans = null!; public ReviewPlanRunner Runner = null!; public FakeRenders Renders = null!;
        public List<Guid> ShotIds = []; public ReferenceAsset Vera = null!; public VoiceReference Voice = null!;
        private ShotTests _owner = null!;

        public static async Task<PlanFixture> Create(ShotTests owner, int shots, bool character = false)
        {
            var f = owner.Fixture(); var ct = owner._ct;
            var result = new PlanFixture { _owner = owner, Project = f.Project.Id, Files = f.Files, Shots = f.Shots, Assets = new(f.Project.Id) };
            if (character)
            {
                AssetImage Image(string name) => new() { Id = Guid.NewGuid(), Name = name, FileName = "image.png", ContentType = "image/png", Width = 64, Height = 64 };
                result.Vera = new() { Id = Guid.NewGuid(), Name = "Vera Sappleton", Category = AssetCategory.Character, Images = [Image("Portrait"), Image("Corrected portrait")] };
                result.Voice = new() { AssetId = result.Vera.Id, Name = "Vera", Duration = 6, ExcerptDuration = 5 };
                result.Assets.Library = new() { ProjectId = f.Project.Id, Assets = [result.Vera], Voices = [result.Voice] };
            }
            var coverage = Enumerable.Range(0, shots).Select(i => { var shot = Ready(); shot.Title = "Shot " + (i + 1); if (character) shot.Dialogue = [new() { Speaker = "VERA", Text = "Do you see them?" }]; return shot; }).ToList();
            await f.Shots.SaveAsync(f.Project.Id, coverage, 0, ct: ct);
            result.ShotIds = coverage.Select(s => s.Id).ToList();
            var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project), List = () => Task.FromResult(new ProjectLibrary([f.Project], [])) };
            result.Production = new(f.Files, f.Shots, result.Assets, projects, owner._clock, new FileAiJobStore(Path.Combine(owner._root, "plan-queue"), owner._clock));
            foreach (var setup in (await result.Production.InitializeAsync(f.Project.Id, ct)).Compositions)
            {
                setup.Prompt = Prompt;
                if (character) setup.Shot.Images = [new() { AssetId = result.Vera.Id, MediaId = result.Vera.Images[0].Id, Name = "Portrait" }];
                await result.Production.SaveAsync(f.Project.Id, setup, setup.Version, ct);
            }
            result.Cuts = new(f.Files, f.Shots, owner._clock);
            result.Review = new(f.Shots, result.Production, result.Cuts, result.Assets, new FakeScriptStore());
            var store = new ReviewPlanStore(f.Files, owner._clock);
            result.Plans = new(store, result.Review, owner._clock); result.Renders = new();
            result.Runner = new(store, result.Review, result.Production, f.Shots, result.Cuts, result.Renders, projects, owner._clock, NullLogger<ReviewPlanRunner>.Instance);
            return result;
        }
        public Task<ReviewPlanStep> Propose(ReviewPlan plan, ReviewStepProposal proposal) => Plans.ProposeAsync(Project, plan.Id, proposal, "agent", ct: _owner._ct);
        public Task<ReviewPlan> Plan(ReviewPlan plan) => Plans.Store.GetAsync(Project, plan.Id, _owner._ct);
        public async Task<ProductionComposition> Setup(Guid shot) => (await Production.LoadAsync(Project, _owner._ct)).Compositions.Single(c => c.ShotId == shot);
        /// <summary>The queued render completes and publishes its takes, as the video handler would.</summary>
        public async Task<Guid[]> Finish(ReviewPlanStep step, int takes)
        {
            var job = step.JobId!.Value; var ct = _owner._ct; var ids = new List<Guid>();
            for (var n = 1; n <= takes; n++)
            {
                var shot = (await Shots.LoadAsync(Project, ct)).Shots.Single(s => s.Id == step.ShotId);
                var snapshot = Snapshot(Project, shot); var run = Guid.NewGuid();
                var directory = Path.Combine(await Shots.RunDirectoryAsync(Project, run, ct), "candidate-" + n); Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), [1, 2, 3], ct);
                var frames = await Lumibelle.Testing.MockFrameArchive.WriteAsync(directory, snapshot.FrameCount, 32, 32, ct);
                var document = await Shots.PublishTakeAsync(Project, new() { ShotId = shot.Id, RunId = run, Candidate = n, AiJobId = job, Snapshot = snapshot, Width = 32, Height = 32, Frames = frames, Bytes = 3 + frames.DistinctBy(x => x.FileName).Sum(x => x.Bytes) }, directory, ct);
                ids.Add(document.Takes.Single(t => t.RunId == run).Id);
            }
            Renders.States[job] = AiJobState.Completed;
            return [.. ids];
        }
    }

    private sealed class FakeRenders : IReviewRenderQueue
    {
        public Dictionary<Guid, AiJobState> States { get; } = [];
        public List<(Guid Shot, int Takes)> Captured { get; } = [];
        public Func<Guid, string?> Refuse { get; set; } = _ => null;
        public Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, CancellationToken ct)
        {
            if (Refuse(setup.ShotId) is { } refusal) throw new WorkspaceStoreException(refusal);
            Captured.Add((setup.ShotId, takes));
            return Task.FromResult(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, new(projectId, ShotId: setup.ShotId, CompositionId: setup.Id), "Project", "Shot", Guid.Empty, new { takes }));
        }
        public Task EnqueueAsync(AiJobSubmission request, CancellationToken ct) { States[request.Id] = AiJobState.Waiting; return Task.CompletedTask; }
        public AiJobState? State(Guid jobId) => States.TryGetValue(jobId, out var state) ? state : null;
    }
}
