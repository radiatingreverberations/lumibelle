using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
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

        await f.Approve(plan);
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
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveFromClientAsync(f.Project, plan.Id, "Claude Code", draft.Revision, totals, _ct));
        await f.Plans.SubmitAsync(f.Project, plan.Id, "agent", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveFromClientAsync(f.Project, plan.Id, "Claude Code", draft.Revision, totals with { Takes = 1 }, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveFromClientAsync(f.Project, plan.Id, "Claude Code", null, null, _ct));

        // The plan changes after the agent read it: the old revision no longer approves anything.
        await f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Show the pick."));
        var e = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveFromClientAsync(f.Project, plan.Id, "Claude Code", draft.Revision, totals, _ct));
        Assert.Contains("2 takes", e.Message);
        Assert.Equal(ReviewPlanState.Submitted, (await f.Plan(plan)).State);

        var current = await f.Plan(plan);
        var approved = await f.Plans.ApproveFromClientAsync(f.Project, plan.Id, "Claude Code", current.Revision, ReviewPlans.Totals(current, await f.Review.LoadAsync(f.Project, _ct)), _ct);
        Assert.Equal((ReviewPlanState.Approved, "Claude Code"), (approved.State, approved.ApprovedFrom));
        // Approved plans take no more steps and no second approval.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Again.")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Approve(plan));
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
        await f.Approve(plan);

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
        await f.Approve(plan);

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
        await f.Approve(plan);
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
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Approve(second));
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
        await f.Approve(plan);

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

        await f.Approve(plan);
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
        await f.Approve(plan);

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
    public async Task RendersUseTheGenerationSetupSelectedWhenThePlanWasApproved()
    {
        var f = await PlanFixture.Create(this, shots: 2); var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var original = (await f.Setup(first)).GenerationSetupId;
        var library = await f.Setups.SaveAsync(new() { Name = "Drafting", Settings = new() { GenerationPreset = "10eros-compact" } }, 0, _ct);
        var drafting = library.Setups.Single(s => s.Name == "Drafting");
        await f.Setups.SelectAsync(drafting.Id, _ct);

        var plan = await f.Plans.CreateAsync(f.Project, "Retakes", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.SetPrompt, first, "Fix.") { Prompt = "The mouse reads a letter." });
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Render.") { Takes = 2 });
        var other = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, second, "Render.") { Takes = 1, Resolution = VideoResolution.Detail });
        // The plan says which setup it will use before it is approved.
        var snapshot = await f.Review.LoadAsync(f.Project, _ct);
        Assert.EndsWith("with “Drafting”", ReviewPlans.Describe(snapshot, render));
        // A render already queued without a recorded setup is not credited with the one selected now.
        Assert.DoesNotContain("Drafting", ReviewPlans.Describe(snapshot, render with { JobId = Guid.NewGuid() }));

        await f.Approve(plan);
        // Selecting something else afterwards does not change what was approved.
        await f.Setups.SelectAsync(original!.Value, _ct);
        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.All(new[] { render, other }, s => Assert.Equal(ReviewStepState.Running, running.Step(s.Id)!.State));
        Assert.Equal((drafting.Id, "Drafting"), (running.Step(render.Id)!.GenerationSetupId, running.Step(render.Id)!.GenerationSetupName));
        Assert.Contains(running.Log, l => l.What.Contains("Renders use “Drafting”"));
        // Each render used the shot's record for that setup, created as Shots does, with the shot's shared prompt and the setup's settings.
        var used = f.Renders.Setups;
        Assert.Equal([drafting.Id, drafting.Id], used.Select(c => c.GenerationSetupId));
        Assert.Equal("The mouse reads a letter.", used[0].Prompt);
        Assert.Equal("10eros-compact", used[0].Shot.GenerationPreset);
        Assert.Equal([VideoResolution.Detail], f.Renders.Resolutions.OfType<VideoResolution>());
        // The shot's other setup is still there, with the same prompt.
        var records = (await f.Production.LoadAsync(f.Project, _ct)).Compositions.Where(c => c.ShotId == first).ToArray();
        Assert.Equal(2, records.Length);
        Assert.All(records, c => Assert.Equal("The mouse reads a letter.", c.Prompt));
    }

    [Fact]
    public async Task ARenderFailsWhenItsApprovedSetupWasArchivedInsteadOfUsingAnother()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var library = await f.Setups.SaveAsync(new() { Name = "Drafting", Settings = new() { GenerationPreset = "10eros-compact" } }, 0, _ct);
        var drafting = library.Setups.Single(s => s.Name == "Drafting");
        await f.Setups.SelectAsync(drafting.Id, _ct);
        var plan = await f.Plans.CreateAsync(f.Project, "Retake", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Render."));
        await f.Approve(plan);
        await f.Setups.SaveAsync(drafting with { Archived = true }, drafting.Version, _ct);

        var done = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal(ReviewStepState.Failed, done.Step(render.Id)!.State);
        Assert.Contains("“Drafting” was archived", done.Step(render.Id)!.Message);
        Assert.Empty(f.Renders.Captured);
    }

    [Fact]
    public async Task StoppingAPlanCancelsItsRendersLeavesNoStepOpenAndUndoesNothing()
    {
        var f = await PlanFixture.Create(this, shots: 3);
        var plan = await f.Plans.CreateAsync(f.Project, "Stopped mid-way", "agent", _ct);
        var prompt = await f.Propose(plan, new(ReviewStepKind.SetPrompt, f.ShotIds[0], "Fix.") { Prompt = "The mouse reads a letter." });
        var picking = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Render."));
        var running = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[1], "Render."));
        var waiting = await f.Propose(plan, new(ReviewStepKind.AddShotFrame, f.ShotIds[2], "Frame.") { Frame = new() { FromShotId = f.ShotIds[0], Name = "End" } });
        var cut = await f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Cut."));
        await f.Approve(plan);
        var started = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        var job = started.Step(running.Id)!.JobId!.Value;
        var takes = await f.Finish(started.Step(picking.Id)!, 1);
        Assert.Equal(ReviewStepState.Pick, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(picking.Id)!.State);

        var stopped = await f.Runner.StopAsync(f.Project, plan.Id, "agent", _ct);

        Assert.Equal(ReviewPlanState.Stopped, stopped.State);
        Assert.DoesNotContain(stopped.Steps, s => s.Open);
        Assert.Equal(ReviewStepState.Done, stopped.Step(prompt.Id)!.State);
        Assert.All(new[] { picking, running, waiting, cut }, s => Assert.Equal(ReviewStepState.Skipped, stopped.Step(s.Id)!.State));
        Assert.Equal(AiJobState.Cancelled, f.Renders.States[job]);
        Assert.Contains("takes stay in Shots", stopped.Step(picking.Id)!.Message);
        Assert.Contains(stopped.Log, l => l.Who == "agent" && l.What == "Stopped the plan.");
        // What already happened stays, and the finished take is there but not selected.
        Assert.Equal("The mouse reads a letter.", (await f.Setup(f.ShotIds[0])).Prompt);
        var document = await f.Shots.LoadAsync(f.Project, _ct);
        Assert.Contains(document.Takes, t => t.Id == takes[0]);
        Assert.Null(document.Shots[0].SelectedTakeId);
        // A stopped plan never moves again, and stopping twice is harmless.
        Assert.Equal(stopped.Log.Count, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Log.Count);
        Assert.Equal(stopped.Log.Count, (await f.Runner.StopAsync(f.Project, plan.Id, "agent", _ct)).Log.Count);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Runner.PickAsync(f.Project, plan.Id, picking.Id, takes[0], ReviewPlans.Director, _ct));
    }

    [Fact]
    public async Task AnEditedImageWaitsForAPickThenReplacesThePictureBeforeTheRender()
    {
        var f = await PlanFixture.Create(this, shots: 1, character: true); var shot = f.ShotIds[0];
        var source = f.Vera.Images[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Night plate", "agent", _ct);
        var edit = await f.Propose(plan, new(ReviewStepKind.EditImage, null, "The scene is at night.") { AssetId = f.Vera.Id, ImageId = source.Id, Prompt = "Make it night, moonlight through the window.", Takes = 2 });
        var replace = await f.Propose(plan, new(ReviewStepKind.ReplacePicture, shot, "Use the night plate.") { AssetId = f.Vera.Id, ImageId = source.Id, FromStepId = edit.Id });
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Render at night."));
        // Proposals are checked when they are written.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.EditImage, null, "Unknown.") { AssetId = f.Vera.Id, ImageId = Guid.NewGuid(), Prompt = "x" }));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.EditImage, null, "No instruction.") { AssetId = f.Vera.Id, ImageId = source.Id, Prompt = " " }));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.ReplacePicture, shot, "Not an edit.") { AssetId = f.Vera.Id, ImageId = source.Id, FromStepId = render.Id }));

        var snapshot = await f.Review.LoadAsync(f.Project, _ct);
        Assert.Equal(new ReviewPlanTotals(1, H3Policy.Seconds(1), 1, 2), ReviewPlans.Totals(await f.Plan(plan), snapshot));
        Assert.Contains("into 2 candidates", ReviewPlans.Describe(snapshot, edit));
        await f.Approve(plan);

        // The edit is queued with the source and waits for review. What needs its pick waits too.
        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        var captured = Assert.Single(f.Renders.Edits);
        Assert.Equal((source.Id, 2, true, ImageAspectPolicy.FromImage1), (captured.SourceImageId, captured.Count, captured.PendingReview, captured.AspectRatio));
        Assert.Equal(ReviewStepState.Running, running.Step(edit.Id)!.State);
        Assert.All(new[] { replace, render }, s => Assert.Equal(ReviewStepState.Waiting, running.Step(s.Id)!.State));
        Assert.Empty(f.Renders.Captured);

        var candidates = f.FinishEdit(running.Step(edit.Id)!, 2);
        var picking = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal(ReviewStepState.Pick, picking.Step(edit.Id)!.State);
        Assert.Equal(candidates, picking.Step(edit.Id)!.ProducedImageIds);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Runner.PickAsync(f.Project, plan.Id, edit.Id, Guid.NewGuid(), ReviewPlans.Director, _ct));
        await f.Plans.RecommendAsync(f.Project, plan.Id, edit.Id, candidates[1], "The window stays readable.", "agent", _ct);

        var after = await f.Runner.AcceptRecommendationsAsync(f.Project, plan.Id, ReviewPlans.Director, ct: _ct);

        // The pick joins the asset, the shot's picture becomes it at the same place, and the render goes.
        var images = f.Assets.Library.Assets[0].Images;
        Assert.Equal(false, images.Single(i => i.Id == candidates[1]).PendingReview);
        Assert.DoesNotContain(images, i => i.Id == candidates[0]);
        Assert.Equal(candidates[1], after.Step(edit.Id)!.NewImageId);
        Assert.Equal(ReviewStepState.Done, after.Step(replace.Id)!.State);
        Assert.Equal(candidates[1], Assert.Single((await f.Setup(shot)).Shot.Images).MediaId);
        Assert.Equal(ReviewStepState.Running, after.Step(render.Id)!.State);
        Assert.Equal(candidates[1], Assert.Single(f.Renders.Setups).Shot.Images.Single().MediaId);
    }

    [Fact]
    public async Task TurningDownAPickSkipsWhatNeededItAndTheRestGoesOn()
    {
        var f = await PlanFixture.Create(this, shots: 2, character: true); var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var source = f.Vera.Images[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Turned down", "agent", _ct);
        var edit = await f.Propose(plan, new(ReviewStepKind.EditImage, null, "Night.") { AssetId = f.Vera.Id, ImageId = source.Id, Prompt = "Make it night." });
        var replace = await f.Propose(plan, new(ReviewStepKind.ReplacePicture, first, "Use it.") { AssetId = f.Vera.Id, ImageId = source.Id, FromStepId = edit.Id });
        var other = await f.Propose(plan, new(ReviewStepKind.MarkPromptReviewed, second, "Unrelated."));
        await f.Approve(plan);
        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        Assert.Equal(ReviewStepState.Done, running.Step(other.Id)!.State);
        f.FinishEdit(running.Step(edit.Id)!, 1);
        await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        var done = await f.Runner.RejectPickAsync(f.Project, plan.Id, edit.Id, ReviewPlans.Director, _ct);

        Assert.Equal(ReviewStepState.Skipped, done.Step(edit.Id)!.State);
        Assert.Equal(ReviewStepState.Skipped, done.Step(replace.Id)!.State);
        Assert.Contains("did not run", done.Step(replace.Id)!.Message);
        Assert.Equal(ReviewPlanState.Finished, done.State);
        Assert.Equal(source.Id, Assert.Single((await f.Setup(first)).Shot.Images).MediaId);
    }

    [Fact]
    public async Task ARenderCanNameItsSetupAndPresetAndTheApprovalKeepsThem()
    {
        var f = await PlanFixture.Create(this, shots: 2); var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var library = await f.Setups.SaveAsync(new() { Name = "Drafting", Settings = new() { GenerationPreset = "10eros-compact" } }, 0, _ct);
        library = await f.Setups.SaveAsync(new() { Name = "Production", Settings = new() { GenerationPreset = "beta" } }, 0, _ct);
        await f.Setups.SelectAsync(library.Setups.Single(s => s.Name == "Production").Id, _ct);
        var plan = await f.Plans.CreateAsync(f.Project, "Drafts", "agent", _ct);

        var draft = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Draft it.") { Setup = "drafting" });
        var quick = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, second, "Try a preset.") { Preset = "turbo4" });
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Unknown.") { Setup = "No such setup" }));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ProposeAsync(f.Project, plan.Id, new(ReviewStepKind.GenerateTakes, first, "Retired.") { Preset = "turbo8" }, "agent", replaces: draft.Id, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ProposeAsync(f.Project, plan.Id, new(ReviewStepKind.GenerateTakes, first, "Unknown preset.") { Preset = "nope" }, "agent", replaces: draft.Id, _ct));

        var snapshot = await f.Review.LoadAsync(f.Project, _ct);
        Assert.EndsWith("with “Drafting”", ReviewPlans.Describe(snapshot, draft));
        Assert.EndsWith("with “Production” using Turbo · 4 steps", ReviewPlans.Describe(snapshot, quick));
        await f.Approve(plan);
        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal("Drafting", running.Step(draft.Id)!.GenerationSetupName);
        Assert.Equal("Production", running.Step(quick.Id)!.GenerationSetupName);
        Assert.Equal(["Drafting", "Production"], f.Renders.Setups.Select(c => c.Name));
        Assert.Equal([null, "turbo4"], f.Renders.Presets);
    }

    [Fact]
    public async Task AnEditCanNameItsModelPresetAndReferences()
    {
        var f = await PlanFixture.Create(this, shots: 1, character: true); var (source, other) = (f.Vera.Images[0], f.Vera.Images[1]);
        var plan = await f.Plans.CreateAsync(f.Project, "Models", "agent", _ct);
        var edit = await f.Propose(plan, new(ReviewStepKind.EditImage, null, "Night.") { AssetId = f.Vera.Id, ImageId = source.Id, Prompt = "Make it night, as in image 2.", Workflow = ImageWorkflow.MiniMaxH3, Preset = "10eros-compact", References = [new(f.Vera.Id, other.Id)] });

        ReviewStepProposal Bad(ImageWorkflow? workflow = null, string? preset = null, IReadOnlyList<AssetImageReference>? references = null) => new(ReviewStepKind.EditImage, null, "Wrong.") { AssetId = f.Vera.Id, ImageId = source.Id, Prompt = "x", Workflow = workflow, Preset = preset, References = references ?? [] };
        // Z-Image creates and cannot edit. A preset is only for H3. The source is not its own reference. An unknown image is refused.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, Bad(ImageWorkflow.ZImage)));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, Bad(ImageWorkflow.QwenImage21, "turbo4")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, Bad(ImageWorkflow.MiniMaxH3, "nope")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, Bad(references: [new(f.Vera.Id, source.Id)])));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, Bad(references: [new(f.Vera.Id, Guid.NewGuid())])));

        var snapshot = await f.Review.LoadAsync(f.Project, _ct);
        Assert.EndsWith("with 1 more reference on MiniMax H3 (10Eros Compact · 8 steps)", ReviewPlans.Describe(snapshot, edit));
        await f.Approve(plan);
        await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        var captured = Assert.Single(f.Renders.Edits);
        Assert.Equal((ImageWorkflow.MiniMaxH3, "10eros-compact"), (captured.Workflow, captured.H3Preset));
        Assert.Equal([new AssetImageReference(f.Vera.Id, source.Id), new AssetImageReference(f.Vera.Id, other.Id)], f.Renders.EditReferences.Single());

        // With no model named, the step shows the project's default and approval records it.
        Assert.EndsWith("on Krea 2", ReviewPlans.Describe(snapshot with { ImageDefault = ImageWorkflow.Krea2 }, edit with { Workflow = null, ReferenceImages = [], Preset = null }));
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

    [Fact]
    public async Task AnApprovalInTheAppCoversOnlyThePlanThePageShowed()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Another take.") { Takes = 2 });
        var shown = await f.Plan(plan); var totals = ReviewPlans.Totals(shown, await f.Review.LoadAsync(f.Project, _ct));

        // The agent adds a step while the director reads the page.
        await f.Propose(plan, new(ReviewStepKind.UpdateCut, null, "Show the pick."));
        var e = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, shown.Revision, totals, _ct));
        Assert.Contains("changed while you were reading", e.Message);

        // The shot is made longer: the same revision now costs more footage.
        shown = await f.Plan(plan); totals = ReviewPlans.Totals(shown, await f.Review.LoadAsync(f.Project, _ct));
        await f.SetDuration(shot, 5);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveAsync(f.Project, plan.Id, shown.Revision, totals, _ct));
        Assert.Equal(ReviewPlanState.Draft, (await f.Plan(plan)).State);

        var approved = await f.Approve(plan);
        Assert.Equal((ReviewPlanState.Approved, ReviewPlans.Director), (approved.State, approved.ApprovedFrom));
    }

    [Fact]
    public async Task AClientThatCallsItselfLumibelleIsStillHeldToTheClientRules()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Another take.") { Takes = 2 });
        var draft = await f.Plan(plan); var totals = ReviewPlans.Totals(draft, await f.Review.LoadAsync(f.Project, _ct));

        // Not submitted, and then totals that leave the takes out: both refused whatever the client is called.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveFromClientAsync(f.Project, plan.Id, ReviewPlans.Director, draft.Revision, totals, _ct));
        await f.Plans.SubmitAsync(f.Project, plan.Id, "agent", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.ApproveFromClientAsync(f.Project, plan.Id, ReviewPlans.Director, draft.Revision, totals with { Takes = 0, FootageSeconds = 0 }, _ct));
        Assert.Empty(f.Renders.Captured);

        var approved = await f.Plans.ApproveFromClientAsync(f.Project, plan.Id, "lumibelle", draft.Revision, totals, _ct);
        Assert.Equal("MCP client", approved.ApprovedFrom);
    }

    [Fact]
    public async Task ReleasingAHeldReviewMarkAcceptsThePromptAsItIsNow()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Review", "agent", _ct);
        var step = await f.Propose(plan, new(ReviewStepKind.MarkPromptReviewed, shot, "The prompt reads well."));
        var setup = await f.Setup(shot); setup.Prompt = "The mouse sleeps."; await f.Production.SaveAsync(f.Project, setup, setup.Version, _ct);
        await f.Approve(plan);

        Assert.Equal(ReviewStepState.Held, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(step.Id)!.State);
        await f.Plans.ReleaseAsync(f.Project, plan.Id, step.Id, "The mouse sleeps.", _ct);
        var released = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal(ReviewStepState.Done, released.Step(step.Id)!.State);
        Assert.Equal("The mouse sleeps.", (await f.Setup(shot)).Accepted?.Prompt);
    }

    [Fact]
    public async Task ARenderWaitsForChangesToItsShotProposedAfterIt()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Fix then render", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Render the fix."));
        var prompt = await f.Propose(plan, new(ReviewStepKind.SetPrompt, shot, "Fix.") { Prompt = "The mouse reads a letter." });
        await f.Approve(plan);

        var running = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal((ReviewStepState.Done, ReviewStepState.Running), (running.Step(prompt.Id)!.State, running.Step(render.Id)!.State));
        Assert.Equal("The mouse reads a letter.", Assert.Single(f.Renders.Setups).Prompt);
    }

    [Fact]
    public async Task ShotsThatWouldWaitForEachOthersPicksAreRefused()
    {
        var f = await PlanFixture.Create(this, shots: 2);
        var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var plan = await f.Plans.CreateAsync(f.Project, "Ring", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Render."));
        await f.Propose(plan, new(ReviewStepKind.GenerateTakes, second, "Render."));
        await f.Propose(plan, new(ReviewStepKind.AddShotFrame, second, "Carry the end over.") { Frame = new() { FromShotId = first, Name = "End" } });

        var e = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Propose(plan, new(ReviewStepKind.AddShotFrame, first, "And back.") { Frame = new() { FromShotId = second, Position = ReviewFramePosition.First, Name = "Start" } }));
        Assert.Matches("#(1 → #2 → #1|2 → #1 → #2) would each wait", e.Message);
        Assert.Equal(3, (await f.Plan(plan)).Steps.Count);
    }

    [Fact]
    public async Task ARenderWhoseShotGrewAfterApprovalIsHeldUntilReleased()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Another take.") { Takes = 2 });
        await f.Approve(plan);
        await f.SetDuration(shot, 5);

        var held = (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!;
        Assert.Equal(ReviewStepState.Held, held.State);
        Assert.Contains("approved for", held.Message);
        Assert.Empty(f.Renders.Captured);

        await f.Plans.ReleaseAsync(f.Project, plan.Id, render.Id, null, _ct);
        Assert.Equal(ReviewStepState.Running, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!.State);
        Assert.Equal((shot, 2), Assert.Single(f.Renders.Captured));
    }

    [Fact]
    public async Task APlanApprovedWithoutBeingAdvancedIsStartedByTheNextWatchedPass()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        await f.Runner.AdvanceAllAsync(_ct);
        var plan = await f.Plans.CreateAsync(f.Project, "Fix", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.SetPrompt, shot, "Fix.") { Prompt = "The mouse reads a letter." });
        // The approving request ends before it advances the plan.
        await f.Approve(plan);

        await f.Runner.AdvanceWatchedAsync(ct: _ct);

        Assert.Equal(ReviewPlanState.Finished, (await f.Plan(plan)).State);
        Assert.Equal("The mouse reads a letter.", (await f.Setup(shot)).Prompt);
    }

    [Fact]
    public async Task StoppingAgainCancelsARenderAnEarlierStopCouldNot()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Another take."));
        await f.Approve(plan);
        var job = (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!.JobId!.Value;
        f.Renders.FailCancels = 1;

        var e = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Runner.StopAsync(f.Project, plan.Id, ReviewPlans.Director, _ct));
        Assert.Contains("could not be cancelled", e.Message);
        Assert.Equal(ReviewPlanState.Stopped, (await f.Plan(plan)).State);
        Assert.Equal(AiJobState.Waiting, f.Renders.States[job]);
        // The page offers Stop plan again for as long as the render is queued.
        Assert.True(f.Runner.Uncancelled(await f.Plan(plan)));

        await f.Runner.StopAsync(f.Project, plan.Id, ReviewPlans.Director, _ct);
        Assert.Equal(AiJobState.Cancelled, f.Renders.States[job]);
        Assert.False(f.Runner.Uncancelled(await f.Plan(plan)));
    }

    [Fact]
    public async Task APickRacingAStopEitherLandsBeforeItOrSelectsNothing()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, shot, "Another take."));
        await f.Approve(plan);
        var takes = await f.Finish((await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!, 1);
        Assert.Equal(ReviewStepState.Pick, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!.State);

        var pick = Task.Run(() => f.Runner.PickAsync(f.Project, plan.Id, render.Id, takes[0], ReviewPlans.Director, _ct), _ct);
        var stop = Task.Run(() => f.Runner.StopAsync(f.Project, plan.Id, ReviewPlans.Director, _ct), _ct);
        await stop;
        try { await pick; } catch (WorkspaceStoreException) { }

        var step = (await f.Plan(plan)).Step(render.Id)!;
        var selected = (await f.Shots.LoadAsync(f.Project, _ct)).Shots.Single().SelectedTakeId;
        // Picked first, then stopped; or stopped first, and the pick changed nothing.
        if (step.State == ReviewStepState.Done) Assert.Equal(takes[0], selected);
        else Assert.Equal((ReviewStepState.Skipped, (Guid?)null), (step.State, selected));
    }

    [Fact]
    public async Task ARenderThatFinishesAfterThePassReadTheProjectStillOffersItsTakes()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Another take."));
        await f.Approve(plan);
        var running = (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!;
        f.Renders.States[running.JobId!.Value] = AiJobState.Running;
        // The pass has read the project when the queue is asked about the render, which publishes its take and completes just then.
        f.Renders.OnState = job => { if (job == running.JobId) { f.Renders.OnState = null; f.Finish(running, 1).GetAwaiter().GetResult(); } };

        var settled = (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!;

        Assert.Equal(ReviewStepState.Pick, settled.State);
        Assert.Single(settled.ProducedTakeIds);
    }

    [Fact]
    public async Task ARunningPlanCannotBeDeletedUntilItIsStopped()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Another take."));
        await f.Approve(plan);
        await f.Finish((await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!, 1);
        Assert.Equal(ReviewStepState.Pick, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!.State);

        // Waiting for a pick, which another tab may be making: refused.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.Store.RemoveAsync(f.Project, plan.Id, _ct));
        await f.Runner.StopAsync(f.Project, plan.Id, ReviewPlans.Director, _ct);
        await f.Plans.Store.RemoveAsync(f.Project, plan.Id, _ct);
        Assert.Empty(await f.Plans.Store.ListAsync(f.Project, _ct));
    }

    [Fact]
    public async Task APlanApprovedWhileAPassForgetsItsProjectIsStillStarted()
    {
        var f = await PlanFixture.Create(this, shots: 1); var shot = f.ShotIds[0];
        var plan = await f.Plans.CreateAsync(f.Project, "Fix", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.SetPrompt, shot, "Fix.") { Prompt = "The mouse reads a letter." });
        // The startup pass reads the plans before the approval and would forget the project after it.
        f.Runner.Listed = _ => { f.Runner.Listed = null; f.Approve(plan).GetAwaiter().GetResult(); };
        await f.Runner.AdvanceAllAsync(_ct);
        Assert.Equal(ReviewPlanState.Approved, (await f.Plan(plan)).State);

        await f.Runner.AdvanceWatchedAsync(ct: _ct);

        Assert.Equal(ReviewPlanState.Finished, (await f.Plan(plan)).State);
        Assert.Equal("The mouse reads a letter.", (await f.Setup(shot)).Prompt);
    }

    [Fact]
    public async Task AFrameFromAShotWaitsForTheRenderThatReplacedARejectedOne()
    {
        var f = await PlanFixture.Create(this, shots: 2);
        var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var plan = await f.Plans.CreateAsync(f.Project, "Carry over", "agent", _ct);
        var rejected = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "One take."));
        await f.Plans.DecideAsync(f.Project, plan.Id, rejected.Id, keep: false, _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Two takes instead.") { Takes = 2 });
        var frame = await f.Propose(plan, new(ReviewStepKind.AddShotFrame, second, "Carry the end over.") { Frame = new() { FromShotId = first, Name = "End" } });
        // The rejected render cannot be kept again beside its replacement.
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Plans.DecideAsync(f.Project, plan.Id, rejected.Id, keep: true, _ct));
        await f.Approve(plan);

        var takes = await f.Finish((await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(render.Id)!, 2);
        Assert.Equal(ReviewStepState.Waiting, (await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct)).Step(frame.Id)!.State);
        var done = await f.Runner.PickAsync(f.Project, plan.Id, render.Id, takes[1], ReviewPlans.Director, _ct);

        Assert.Equal(ReviewStepState.Done, done.Step(frame.Id)!.State);
        Assert.Contains((await f.Setup(second)).Shot.ShotFrames, x => x.TakeId == takes[1]);
    }

    [Fact]
    public async Task ARenderIsHeldWhenItsShotGrowsAfterThePassReadTheProject()
    {
        var f = await PlanFixture.Create(this, shots: 2);
        var (first, second) = (f.ShotIds[0], f.ShotIds[1]);
        var plan = await f.Plans.CreateAsync(f.Project, "Renders", "agent", _ct);
        await f.Propose(plan, new(ReviewStepKind.GenerateTakes, first, "Render."));
        var later = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, second, "Render."));
        await f.Approve(plan);
        // The second render is queued in a pass whose snapshot was read before the second shot was made longer.
        f.Renders.OnState = _ => { f.Renders.OnState = null; f.SetDuration(second, 5).GetAwaiter().GetResult(); };

        var advanced = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);

        Assert.Equal(ReviewStepState.Held, advanced.Step(later.Id)!.State);
        Assert.Equal(first, Assert.Single(f.Renders.Captured).Shot);
    }

    [Fact]
    public async Task AFrameUsesTheTakePickedInThePlanEvenIfTheShotSelectsAnotherSince()
    {
        var f = await PlanFixture.Create(this, shots: 3);
        var (source, target, other) = (f.ShotIds[0], f.ShotIds[1], f.ShotIds[2]);
        var plan = await f.Plans.CreateAsync(f.Project, "Carry over", "agent", _ct);
        var render = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, source, "Two takes.") { Takes = 2 });
        var unrelated = await f.Propose(plan, new(ReviewStepKind.GenerateTakes, other, "Elsewhere."));
        var frame = await f.Propose(plan, new(ReviewStepKind.AddShotFrame, target, "Carry the end over.") { Frame = new() { FromShotId = source, Name = "End" } });
        await f.Approve(plan);
        var queued = await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        var takes = await f.Finish(queued.Step(render.Id)!, 2);
        await f.Runner.AdvanceAsync(f.Project, plan.Id, _ct);
        // While the pick is advanced, another tab selects the other take of the source shot.
        var otherJob = queued.Step(unrelated.Id)!.JobId!.Value;
        f.Renders.OnState = job =>
        {
            if (job != otherJob) return;
            f.Renders.OnState = null; f.Renders.States[otherJob] = AiJobState.Cancelled;
            var document = f.Shots.LoadAsync(f.Project, _ct).GetAwaiter().GetResult();
            document.Shots.Single(s => s.Id == source).SelectedTakeId = takes[1];
            f.Shots.SaveAsync(f.Project, document.Shots, document.Revision, ct: _ct).GetAwaiter().GetResult();
        };

        var done = await f.Runner.PickAsync(f.Project, plan.Id, render.Id, takes[0], ReviewPlans.Director, _ct);

        Assert.Equal(ReviewStepState.Done, done.Step(frame.Id)!.State);
        Assert.Equal(takes[0], Assert.Single((await f.Setup(target)).Shot.ShotFrames).TakeId);
    }

    [Fact]
    public async Task OnlyOneOfTwoRendersOfAShotProposedAtOnceIsKept()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        var plan = await f.Plans.CreateAsync(f.Project, "Render", "agent", _ct);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { await f.Propose(plan, new(ReviewStepKind.GenerateTakes, f.ShotIds[0], "Another take.")); return true; }
            catch (WorkspaceStoreException) { return false; }
        }, _ct)));

        Assert.Single(attempts, ok => ok);
        Assert.Single((await f.Plan(plan)).Steps);
    }

    [Fact]
    public async Task APlanWhoseLastStepRanButWasNotMarkedFinishedIsFinishedByTheNextPass()
    {
        var f = await PlanFixture.Create(this, shots: 1);
        await f.Runner.AdvanceAllAsync(_ct);
        var plan = await f.Plans.CreateAsync(f.Project, "Fix", "agent", _ct);
        var step = await f.Propose(plan, new(ReviewStepKind.SetPrompt, f.ShotIds[0], "Fix.") { Prompt = "The mouse reads a letter." });
        await f.Approve(plan);
        // The pass saved the last step and was cancelled before it marked the plan finished.
        await f.Plans.Store.UpdateAsync(f.Project, plan.Id, p => p with { Steps = p.Steps.Select(s => s.Id == step.Id ? s with { State = ReviewStepState.Done } : s).ToArray() }, ct: _ct);

        await f.Runner.AdvanceWatchedAsync(ct: _ct);

        Assert.Equal(ReviewPlanState.Finished, (await f.Plan(plan)).State);
    }

    private sealed class PlanFixture
    {
        public const string Prompt = "The camera holds as the mouse looks up.";
        public Guid Project; public ProjectFiles Files = null!; public FileShotStore Shots = null!; public FakeAssetStore Assets = null!;
        public FileProductionStore Production = null!; public FileCutStore Cuts = null!; public ProjectReview Review = null!;
        public ReviewPlans Plans = null!; public ReviewPlanRunner Runner = null!; public FakeRenders Renders = null!; public FileGenerationSetupStore Setups = null!;
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
            var data = Path.Combine(owner._root, "plan-data"); Directory.CreateDirectory(data);
            result.Setups = new(new lumibelle.Services.ApplicationPaths(data));
            result.Production = new(f.Files, f.Shots, result.Assets, projects, owner._clock, new FileAiJobStore(Path.Combine(owner._root, "plan-queue"), owner._clock), generationSetups: result.Setups);
            foreach (var setup in (await result.Production.InitializeAsync(f.Project.Id, ct)).Compositions)
            {
                setup.Prompt = Prompt;
                if (character) setup.Shot.Images = [new() { AssetId = result.Vera.Id, MediaId = result.Vera.Images[0].Id, Name = "Portrait" }];
                await result.Production.SaveAsync(f.Project.Id, setup, setup.Version, ct);
            }
            result.Cuts = new(f.Files, f.Shots, owner._clock);
            result.Review = new(f.Shots, result.Production, result.Cuts, result.Assets, new FakeScriptStore(), result.Setups);
            var store = new ReviewPlanStore(f.Files, owner._clock);
            result.Plans = new(store, result.Review, owner._clock); result.Renders = new();
            result.Runner = new(store, result.Review, result.Production, f.Shots, result.Cuts, result.Assets, result.Renders, projects, owner._clock, NullLogger<ReviewPlanRunner>.Instance, result.Setups);
            return result;
        }
        public Task<ReviewPlanStep> Propose(ReviewPlan plan, ReviewStepProposal proposal) => Plans.ProposeAsync(Project, plan.Id, proposal, "agent", ct: _owner._ct);
        public Task<ReviewPlan> Plan(ReviewPlan plan) => Plans.Store.GetAsync(Project, plan.Id, _owner._ct);
        // The director approves the plan as the Review page shows it.
        public async Task<ReviewPlan> Approve(ReviewPlan plan)
        {
            var shown = await Plan(plan);
            return await Plans.ApproveAsync(Project, plan.Id, shown.Revision, ReviewPlans.Totals(shown, await Review.LoadAsync(Project, _owner._ct)), _owner._ct);
        }
        public async Task SetDuration(Guid shot, double seconds)
        {
            var document = await Shots.LoadAsync(Project, _owner._ct);
            document.Shots.Single(s => s.Id == shot).Duration = seconds;
            await Shots.SaveAsync(Project, document.Shots, document.Revision, ct: _owner._ct);
        }
        public async Task<ProductionComposition> Setup(Guid shot) => (await Production.LoadAsync(Project, _owner._ct)).Compositions.First(c => c.ShotId == shot && !c.Archived);
        /// <summary>The queued edit completes and publishes pending candidates, as the image handler would.</summary>
        public Guid[] FinishEdit(ReviewPlanStep step, int count)
        {
            var job = step.JobId!.Value;
            var made = Enumerable.Range(1, count).Select(n => new AssetImage { Id = Guid.NewGuid(), FileName = $"edit-{n}.png", ContentType = "image/png", Width = 64, Height = 64, PendingReview = true, Generation = new() { AiJobId = job } }).ToArray();
            var library = Assets.Library;
            Assets.Library = library with { Assets = library.Assets.Select(a => a.Id == step.AssetId ? a with { Images = [.. a.Images, .. made] } : a).ToList() };
            Renders.States[job] = AiJobState.Completed;
            return made.Select(i => i.Id).ToArray();
        }
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
        public List<ProductionComposition> Setups { get; } = [];
        public List<VideoResolution?> Resolutions { get; } = [];
        public List<ReferenceEditRequest> Edits { get; } = [];
        public Task<AiJobSubmission> CaptureEditAsync(Guid id, Guid assetId, ReferenceEditRequest request, IReadOnlyList<AssetImageReference> references, CancellationToken ct)
        {
            Edits.Add(request); EditReferences.Add(references);
            return Task.FromResult(AiJobSubmission.Create(id, AiJobKind.ImageEdit, AiBackend.ComfyUI, new(request.ProjectId, AssetId: assetId), "Project", "Image", Guid.Empty, new { request.Count }));
        }
        public Func<Guid, string?> Refuse { get; set; } = _ => null;
        public List<string?> Presets { get; } = [];
        public List<IReadOnlyList<AssetImageReference>> EditReferences { get; } = [];
        public Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, string? preset, CancellationToken ct)
        {
            Presets.Add(preset);
            if (Refuse(setup.ShotId) is { } refusal) throw new WorkspaceStoreException(refusal);
            Captured.Add((setup.ShotId, takes)); Setups.Add(setup); Resolutions.Add(resolution);
            return Task.FromResult(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, new(projectId, ShotId: setup.ShotId, CompositionId: setup.Id), "Project", "Shot", Guid.Empty, new { takes }));
        }
        public Task EnqueueAsync(AiJobSubmission request, CancellationToken ct) { States[request.Id] = AiJobState.Waiting; return Task.CompletedTask; }
        public int FailCancels { get; set; }
        public Task CancelAsync(Guid jobId, CancellationToken ct)
        {
            if (FailCancels > 0) { FailCancels--; throw new IOException("The queue could not be reached."); }
            States[jobId] = AiJobState.Cancelled; return Task.CompletedTask;
        }
        /// <summary>Runs when the queue is asked about a request, before it answers.</summary>
        public Action<Guid>? OnState { get; set; }
        public AiJobState? State(Guid jobId) { OnState?.Invoke(jobId); return States.TryGetValue(jobId, out var state) ? state : null; }
    }
}
