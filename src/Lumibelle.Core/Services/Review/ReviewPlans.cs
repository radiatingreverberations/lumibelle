using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>What a proposer asks for. Validated against the project and turned into a step; nothing is changed.</summary>
public sealed record ReviewStepProposal(ReviewStepKind Kind, Guid? ShotId, string Reason)
{
    public IReadOnlyList<Guid> FindingIds { get; init; } = [];
    /// <summary>SetPrompt: the whole new prompt, or leave null and give <see cref="Edits"/>.</summary>
    public string? Prompt { get; init; }
    /// <summary>SetPrompt: exact text to replace. Each old text must occur exactly once in the prompt as it stands.</summary>
    public IReadOnlyList<(string Old, string New)> Edits { get; init; } = [];
    public Guid? CharacterAssetId { get; init; }
    public Guid? VoiceId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? ImageId { get; init; }
    public Guid? NewAssetId { get; init; }
    public Guid? NewImageId { get; init; }
    public ReviewFrameSource? Frame { get; init; }
    public Guid? TakeId { get; init; }
    public int Takes { get; init; } = 1;
    public VideoResolution? Resolution { get; init; }
}

/// <summary>
/// Proposing and deciding review plans. A proposer, whether an agent over MCP or a review inside the app, can only add findings and
/// steps here. Nothing in this class changes shots, prompts, the cut or the queue; <see cref="ReviewPlanRunner"/> does, for approved plans.
/// </summary>
public sealed class ReviewPlans(ReviewPlanStore store, ProjectReview review, TimeProvider clock)
{
    public const string Director = "Lumibelle";
    public ReviewPlanStore Store => store;

    public Task<ReviewPlan> CreateAsync(Guid projectId, string title, string who, CancellationToken ct = default) => store.CreateAsync(projectId, title, who, ct);

    public async Task<ReviewPlanFinding> AddFindingAsync(Guid projectId, Guid planId, Guid? shotId, string message, string evidence, string who, CancellationToken ct = default)
    {
        message = (message ?? "").Trim(); evidence = (evidence ?? "").Trim();
        if (message.Length is 0 or > 2000 || evidence.Length > 4000) throw new WorkspaceStoreException("Describe the finding in up to 2000 characters, with evidence of up to 4000.");
        if (shotId is { } shot && (await review.LoadAsync(projectId, ct)).Row(shot) is null) throw new WorkspaceStoreException("That shot is not in this project.");
        var finding = new ReviewPlanFinding { ShotId = shotId, Message = message, Evidence = evidence, ReportedBy = who };
        await store.UpdateAsync(projectId, planId, plan => Editable(plan).Findings.Count >= 500 ? throw new WorkspaceStoreException("A plan holds up to 500 findings.")
            : plan with { Findings = [.. plan.Findings, finding] }, proposal: true, ct);
        return finding;
    }

    /// <summary>Adds a step, or with <paramref name="replaces"/> rewrites one that has not run.</summary>
    public async Task<ReviewPlanStep> ProposeAsync(Guid projectId, Guid planId, ReviewStepProposal proposal, string who, Guid? replaces = null, CancellationToken ct = default)
    {
        var snapshot = await review.LoadAsync(projectId, ct);
        var current = await store.GetAsync(projectId, planId, ct);
        var step = Build(snapshot, current, proposal, who) with { Id = replaces ?? Guid.NewGuid() };
        await store.UpdateAsync(projectId, planId, plan =>
        {
            Editable(plan);
            if (step.FindingIds.Any(f => plan.Findings.All(x => x.Id != f))) throw new WorkspaceStoreException("A step can only name findings of its own plan.");
            if (replaces is { } id)
            {
                var old = plan.Step(id) ?? throw new WorkspaceStoreException("That step is not in this plan.");
                if (old.Kind != step.Kind || old.ShotId != step.ShotId) throw new WorkspaceStoreException("A step can be rewritten, not turned into another kind or moved to another shot. Propose a new step instead.");
                return plan with { Steps = plan.Steps.Select(s => s.Id == id ? step : s).ToArray(), Log = [.. plan.Log, new(clock.GetUtcNow(), who, $"Rewrote the step “{Describe(snapshot, step)}”.")] };
            }
            if (plan.Steps.Count >= 500) throw new WorkspaceStoreException("A plan holds up to 500 steps.");
            return plan with { Steps = [.. plan.Steps, step] };
        }, proposal: true, ct);
        return step;
    }

    public Task<ReviewPlan> SubmitAsync(Guid projectId, Guid planId, string who, CancellationToken ct = default) => store.UpdateAsync(projectId, planId, plan =>
        plan.State != ReviewPlanState.Draft ? plan : plan.Steps.Count == 0 && plan.Findings.Count == 0 ? throw new WorkspaceStoreException("Add findings or steps before submitting the plan.")
        : plan with { State = ReviewPlanState.Submitted, Log = [.. plan.Log, new(clock.GetUtcNow(), who, "Submitted the plan for approval.")] }, proposal: false, ct);

    /// <summary>The director keeps or rejects one proposed step before approving the plan.</summary>
    public Task<ReviewPlan> DecideAsync(Guid projectId, Guid planId, Guid stepId, bool keep, CancellationToken ct = default) => store.UpdateAsync(projectId, planId, plan =>
    {
        Editable(plan);
        var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
        var state = keep ? ReviewStepState.Proposed : ReviewStepState.Rejected;
        return step.State == state ? plan : plan with { Steps = plan.Steps.Select(s => s.Id == stepId ? s with { State = state } : s).ToArray() };
    }, proposal: true, ct);

    /// <summary>What approving the plan as it stands agrees to.</summary>
    public static ReviewPlanTotals Totals(ReviewPlan plan, ProjectReviewSnapshot snapshot)
    {
        var kept = plan.Steps.Where(s => s.State != ReviewStepState.Rejected && s.State != ReviewStepState.Skipped).ToArray();
        var renders = kept.Where(s => s.SpendsGpu).ToArray();
        return new(renders.Sum(s => s.Takes), Math.Round(renders.Sum(s => s.Takes * Seconds(snapshot, s.ShotId)), 3), kept.Count(s => !s.SpendsGpu));
    }
    private static double Seconds(ProjectReviewSnapshot snapshot, Guid? shotId) =>
        snapshot.Row(shotId ?? Guid.Empty)?.Coverage.Duration is { } seconds and >= 1 and <= 15 ? H3Policy.Seconds(seconds) : 0;

    /// <summary>
    /// Approves the plan. From the app the director has the plan in front of them. From a client, the call must name the revision
    /// and the totals it read, so an approval can never cover a plan other than the one that was shown.
    /// </summary>
    public async Task<ReviewPlan> ApproveAsync(Guid projectId, Guid planId, string from, long? revision = null, ReviewPlanTotals? totals = null, CancellationToken ct = default)
    {
        var snapshot = await review.LoadAsync(projectId, ct);
        return await store.UpdateAsync(projectId, planId, plan =>
        {
            if (plan.State is not (ReviewPlanState.Draft or ReviewPlanState.Submitted)) throw new WorkspaceStoreException(plan.State == ReviewPlanState.Approved ? "This plan is already approved." : "This plan is closed. Start a new plan.");
            if (from != Director)
            {
                if (plan.State != ReviewPlanState.Submitted) throw new WorkspaceStoreException("Submit the plan before it is approved.");
                var actual = Totals(plan, snapshot);
                if (revision != plan.Revision || totals is null || totals.Takes != actual.Takes || totals.Changes != actual.Changes || Math.Abs(totals.FootageSeconds - actual.FootageSeconds) > .01)
                    throw new WorkspaceStoreException($"The approval does not match the plan as it stands (revision {plan.Revision}: {actual.Takes} takes, {actual.FootageSeconds} seconds of footage, {actual.Changes} changes). Read the plan again and show the director these totals.");
            }
            if (!plan.Steps.Any(s => s.State == ReviewStepState.Proposed)) throw new WorkspaceStoreException("No step of this plan is left to approve.");
            var now = clock.GetUtcNow(); var approved = Totals(plan, snapshot);
            return plan with
            {
                State = ReviewPlanState.Approved, ApprovedUtc = now, ApprovedFrom = from,
                Steps = plan.Steps.Select(s => s.State == ReviewStepState.Proposed ? s with { State = ReviewStepState.Waiting } : s).ToArray(),
                Log = [.. plan.Log, new(now, from, $"Approved revision {plan.Revision}: {approved.Takes} takes, {approved.FootageSeconds} seconds of footage, {approved.Changes} changes.")]
            };
        }, proposal: false, ct);
    }

    /// <summary>Stops an approved plan. Steps that have not run are skipped; a render already in the queue is left to finish or be cancelled in AI activity.</summary>
    public Task<ReviewPlan> StopAsync(Guid projectId, Guid planId, string who, CancellationToken ct = default) => store.UpdateAsync(projectId, planId, plan =>
        plan.State is ReviewPlanState.Finished or ReviewPlanState.Stopped ? plan : plan with
        {
            State = ReviewPlanState.Stopped,
            Steps = plan.Steps.Select(s => s.State is ReviewStepState.Waiting or ReviewStepState.Held or ReviewStepState.Proposed ? s with { State = ReviewStepState.Skipped, Message = "The plan was stopped." } : s).ToArray(),
            Log = [.. plan.Log, new(clock.GetUtcNow(), who, "Stopped the plan.")]
        }, proposal: false, ct);

    /// <summary>A proposer's advice on which of a step's new takes to use. The director still picks.</summary>
    public async Task<ReviewPlan> RecommendAsync(Guid projectId, Guid planId, Guid stepId, Guid takeId, string reason, string who, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length is 0 or > 2000) throw new WorkspaceStoreException("Say why in up to 2000 characters.");
        return await store.UpdateAsync(projectId, planId, plan =>
        {
            var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
            if (step.State != ReviewStepState.Pick) throw new WorkspaceStoreException("This step has no takes waiting for a pick.");
            if (!step.ProducedTakeIds.Contains(takeId)) throw new WorkspaceStoreException("Recommend one of the takes this step produced.");
            return plan with { Steps = plan.Steps.Select(s => s.Id == stepId ? s with { RecommendedTakeId = takeId, Recommendation = reason } : s).ToArray(),
                Log = [.. plan.Log, new(clock.GetUtcNow(), who, $"Recommended a take for “{step.Reason}”.")] };
        }, proposal: false, ct);
    }

    /// <summary>Lets a held step run against the shot as it is now.</summary>
    public Task<ReviewPlan> ReleaseAsync(Guid projectId, Guid planId, Guid stepId, string? currentPrompt, CancellationToken ct = default) => store.UpdateAsync(projectId, planId, plan =>
    {
        var step = plan.Step(stepId) ?? throw new WorkspaceStoreException("That step is not in this plan.");
        if (step.State != ReviewStepState.Held) return plan;
        return plan with { Steps = plan.Steps.Select(s => s.Id == stepId ? s with { State = ReviewStepState.Waiting, Message = null, Before = s.Kind == ReviewStepKind.SetPrompt ? currentPrompt : s.Before } : s).ToArray(),
            Log = [.. plan.Log, new(clock.GetUtcNow(), Director, "Let a held step run against the shot as it is now.")] };
    }, proposal: false, ct);

    private static ReviewPlan Editable(ReviewPlan plan) => plan.State is ReviewPlanState.Draft or ReviewPlanState.Submitted ? plan
        : throw new WorkspaceStoreException(plan.State == ReviewPlanState.Approved ? "This plan is approved and running. Start a new plan for further changes." : "This plan is closed. Start a new plan.");

    internal static string Text(string? value) => (value ?? "").Replace("\r\n", "\n").Trim();

    /// <summary>Applies exact replacements. Each old text must occur exactly once, so a fix can never land in the wrong place.</summary>
    public static string ApplyEdits(string prompt, IReadOnlyList<(string Old, string New)> edits)
    {
        foreach (var (old, replacement) in edits)
        {
            if (string.IsNullOrEmpty(old)) throw new WorkspaceStoreException("Give the exact text to replace.");
            var first = prompt.IndexOf(old, StringComparison.Ordinal);
            if (first < 0) throw new WorkspaceStoreException($"The prompt does not contain “{Short(old)}”. Quote the text exactly as get_shot returns it.");
            if (prompt.IndexOf(old, first + 1, StringComparison.Ordinal) >= 0) throw new WorkspaceStoreException($"“{Short(old)}” occurs more than once in the prompt. Quote more of the surrounding text so it is unique.");
            prompt = prompt[..first] + replacement + prompt[(first + old.Length)..];
        }
        return prompt;
    }
    private static string Short(string text) => (text = text.ReplaceLineEndings(" ")).Length <= 60 ? text : text[..57] + "…";

    // Checks the proposal against the project as it is now and captures what the step was written against.
    private static ReviewPlanStep Build(ProjectReviewSnapshot snapshot, ReviewPlan plan, ReviewStepProposal p, string who)
    {
        var reason = (p.Reason ?? "").Trim();
        if (reason.Length is 0 or > 2000) throw new WorkspaceStoreException("Say why the step is needed in up to 2000 characters.");
        var step = new ReviewPlanStep { Kind = p.Kind, ShotId = p.ShotId, Reason = reason, FindingIds = p.FindingIds.Distinct().ToArray(), ProposedBy = who };
        if (p.Kind == ReviewStepKind.UpdateCut) return step with { ShotId = null };
        var row = snapshot.Row(p.ShotId ?? Guid.Empty) ?? throw new WorkspaceStoreException("Name the shot this step changes.");
        var setup = row.Setup ?? throw new WorkspaceStoreException($"Shot #{row.Number} has no setup yet. Open it in Shots once before proposing changes to it.");
        var shot = setup.Shot;
        switch (p.Kind)
        {
            case ReviewStepKind.SetPrompt:
                if (p.Prompt is null == (p.Edits.Count == 0)) throw new WorkspaceStoreException("Give either the whole new prompt or the exact texts to replace.");
                var after = p.Prompt ?? ApplyEdits(setup.Prompt, p.Edits);
                if (string.IsNullOrWhiteSpace(after) || after.Length > 100000) throw new WorkspaceStoreException("Write a prompt under 100000 characters.");
                if (Text(after) == Text(setup.Prompt)) throw new WorkspaceStoreException("That is the prompt the shot already has.");
                return step with { Before = setup.Prompt, After = after };
            case ReviewStepKind.MarkPromptReviewed:
                if (string.IsNullOrWhiteSpace(setup.Prompt)) throw new WorkspaceStoreException($"Shot #{row.Number} has no prompt to review.");
                return step with { Before = setup.Prompt };
            case ReviewStepKind.SetVoice:
                var character = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == p.CharacterAssetId && a.Category == AssetCategory.Character) ?? throw new WorkspaceStoreException("Name a character from list_assets.");
                if (p.VoiceId is { } voice && !snapshot.Assets.Voices.Any(v => v.Id == voice && v.AssetId == character.Id)) throw new WorkspaceStoreException($"That is not one of {character.Name}'s voice recordings.");
                return step with { CharacterAssetId = character.Id, VoiceId = p.VoiceId };
            case ReviewStepKind.AddShotFrame:
                var source = p.Frame ?? throw new WorkspaceStoreException("Say which take the frame comes from, or which shot's pick.");
                if (source.TakeId is null == source.FromShotId is null) throw new WorkspaceStoreException("Give either a take, or the shot whose picked take will supply the frame.");
                if (source.TakeId is { } takeId)
                {
                    var take = snapshot.Shots.Takes.FirstOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("That take is not in this project.");
                    if (source.Position == ReviewFramePosition.Index && (source.Index < 0 || source.Index >= take.FrameCount)) throw new WorkspaceStoreException($"That take has frames 1 to {take.FrameCount}.");
                }
                else
                {
                    var from = snapshot.Row(source.FromShotId!.Value) ?? throw new WorkspaceStoreException("The shot supplying the frame is not in this project.");
                    if (from.Id == row.Id) throw new WorkspaceStoreException("A shot cannot take a frame from its own next pick.");
                    if (source.Position == ReviewFramePosition.Index) throw new WorkspaceStoreException("A frame from a take that does not exist yet is named by position: the first or the last frame.");
                    if (!plan.Steps.Any(s => s.Kind == ReviewStepKind.GenerateTakes && s.ShotId == from.Id && s.State != ReviewStepState.Rejected) && from.SelectedTake is null)
                        throw new WorkspaceStoreException($"Shot #{from.Number} has no selected take and this plan does not render it. Propose its render first.");
                }
                var name = string.IsNullOrWhiteSpace(source.Name) ? "Shot frame" : source.Name.Trim();
                if (name.Length > 200) throw new WorkspaceStoreException("Keep the frame's name under 200 characters.");
                return step with { Frame = source with { Name = name } };
            case ReviewStepKind.RemoveShotFrame:
                var remove = p.Frame is { TakeId: not null } f ? f : throw new WorkspaceStoreException("Name the take and frame of the shot frame to remove, as get_shot lists them.");
                if (!shot.ShotFrames.Any(x => x.TakeId == remove.TakeId && x.Frame == remove.Index)) throw new WorkspaceStoreException($"Shot #{row.Number} has no shot frame from that take and frame.");
                return step with { Frame = remove with { Position = ReviewFramePosition.Index } };
            case ReviewStepKind.ReplacePicture or ReviewStepKind.RemovePicture:
                if (!shot.Images.Any(i => i.Kind == ShotImageKind.AssetImage && i.AssetId == p.AssetId && i.MediaId == p.ImageId)) throw new WorkspaceStoreException($"Shot #{row.Number} does not select that picture.");
                if (p.Kind == ReviewStepKind.RemovePicture) return step with { AssetId = p.AssetId, ImageId = p.ImageId };
                var replacement = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == p.NewAssetId)?.Images.FirstOrDefault(i => i.Id == p.NewImageId) ?? throw new WorkspaceStoreException("The replacement picture is not in Assets.");
                if (replacement.PendingReview == true) throw new WorkspaceStoreException("The replacement picture is still waiting for review in Assets.");
                if (shot.Images.Any(i => i.AssetId == p.NewAssetId && i.MediaId == p.NewImageId)) throw new WorkspaceStoreException($"Shot #{row.Number} already selects the replacement picture.");
                return step with { AssetId = p.AssetId, ImageId = p.ImageId, NewAssetId = p.NewAssetId, NewImageId = p.NewImageId };
            case ReviewStepKind.UseTake:
                if (!row.Takes.Any(t => t.Id == p.TakeId)) throw new WorkspaceStoreException($"That is not a take of shot #{row.Number}.");
                if (row.Coverage.SelectedTakeId == p.TakeId) throw new WorkspaceStoreException("That take is already the selected one.");
                return step with { TakeId = p.TakeId };
            case ReviewStepKind.GenerateTakes:
                if (p.Takes is < 1 or > 4) throw new WorkspaceStoreException("Ask for one to four takes.");
                if (p.Resolution is { } resolution && !Enum.IsDefined(resolution)) throw new WorkspaceStoreException("Choose one of the listed sizes.");
                if (plan.Steps.Any(s => s.Kind == ReviewStepKind.GenerateTakes && s.ShotId == row.Id && s.State != ReviewStepState.Rejected))
                    throw new WorkspaceStoreException($"This plan already renders shot #{row.Number}. Rewrite that step to change the number of takes.");
                if (row.Coverage.Duration is null) throw new WorkspaceStoreException($"Shot #{row.Number} has no duration. Set one in Shots first.");
                return step with { Takes = p.Takes, Resolution = p.Resolution };
            default: throw new WorkspaceStoreException("That kind of step is not supported.");
        }
    }

    /// <summary>One line saying what a step does, for lists and the log.</summary>
    public static string Describe(ProjectReviewSnapshot snapshot, ReviewPlanStep step)
    {
        var shot = step.ShotId is { } id && snapshot.Row(id) is { } row ? $"#{row.Number}" : "a shot that was removed";
        string Picture(Guid? asset, Guid? image) => snapshot.Assets.Assets.FirstOrDefault(a => a.Id == asset) is { } owner
            ? owner.Images.FirstOrDefault(i => i.Id == image)?.Name is { Length: > 0 } name ? $"“{name}” of {owner.Name}" : $"a picture of {owner.Name}" : "a picture no longer in Assets";
        string Character() => snapshot.Assets.Assets.FirstOrDefault(a => a.Id == step.CharacterAssetId)?.Name ?? "a character";
        return step.Kind switch
        {
            ReviewStepKind.SetPrompt => $"Change the prompt of {shot} and mark it reviewed",
            ReviewStepKind.MarkPromptReviewed => $"Mark the prompt of {shot} reviewed",
            ReviewStepKind.SetVoice => step.VoiceId is { } voice ? $"Set {Character()}'s voice in {shot} to “{snapshot.Assets.Voices.FirstOrDefault(v => v.Id == voice)?.Name}”" : $"Set {Character()}'s voice in {shot} to None",
            ReviewStepKind.AddShotFrame => step.Frame is { FromShotId: { } from } frame
                ? $"Add the {(frame.Position == ReviewFramePosition.First ? "first" : "last")} frame of the take picked for #{snapshot.Row(from)?.Number} to {shot} as “{frame.Name}”"
                : $"Add a frame of a take of #{snapshot.OwnerOf(step.Frame?.TakeId ?? Guid.Empty)?.Number} to {shot} as “{step.Frame?.Name}”",
            ReviewStepKind.RemoveShotFrame => $"Remove a shot frame from {shot}",
            ReviewStepKind.ReplacePicture => $"In {shot}, replace {Picture(step.AssetId, step.ImageId)} with {Picture(step.NewAssetId, step.NewImageId)}",
            ReviewStepKind.RemovePicture => $"Remove {Picture(step.AssetId, step.ImageId)} from {shot}",
            ReviewStepKind.UseTake => $"Select another take for {shot}",
            ReviewStepKind.UpdateCut => "Point the cut at the selected takes",
            ReviewStepKind.GenerateTakes => $"Render {step.Takes} {(step.Takes == 1 ? "take" : "takes")} of {shot}" + (step.Resolution is { } size ? $" at {VideoResolutions.Label(size)}" : ""),
            _ => step.Kind.ToString()
        };
    }
}
