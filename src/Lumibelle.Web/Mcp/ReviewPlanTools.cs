using System.ComponentModel;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Review;
using lumibelle.Services.Story;
using ModelContextProtocol.Server;
using static lumibelle.Mcp.McpHosting;

namespace lumibelle.Mcp;

/// <summary>An exact replacement in a prompt.</summary>
public sealed record PromptEdit([property: Description("The exact text in the prompt now. It must occur exactly once.")] string Old, [property: Description("The text to put in its place.")] string New);
/// <summary>An asset image, by the ids list_assets gives.</summary>
public sealed record ImageReference([property: Description("The asset's id.")] string Asset, [property: Description("The image's id.")] string Image);
/// <summary>The take chosen for one shot.</summary>
public sealed record TakePick([property: Description("The id of the take or image chosen, one of those the step produced.")] string Take,
    [property: Description("The shot of a render step, by number such as #31 or by id.")] string? Shot = null, [property: Description("The step's id. Needed for an image edit, which has no shot.")] string? Step = null);

/// <summary>
/// Tools that write to a plan and nothing else. A finding or a step added here changes no shot, prompt, cut or queue;
/// the plan runs only after the director approves it.
/// </summary>
[McpServerToolType]
public sealed class ReviewPlanTools(ReviewQueries queries, ReviewPlans plans, IHttpContextAccessor http)
{
    private const string Project = "The project's id, or its exact name.";
    private const string PlanId = "The plan's id, from create_plan or list_plans.";
    private const string Kinds = "SetPrompt, MarkPromptReviewed, SetVoice, AddShotFrame, RemoveShotFrame, ReplacePicture, RemovePicture, UseTake, UpdateCut, GenerateTakes or EditImage.";

    [McpServerTool(Name = "list_plans", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists the project's review plans with their state.")]
    public Task<string> ListPlans([Description(Project)] string project, CancellationToken ct) => Guard(async () =>
        Text(ReviewQueries.Plans(await plans.Store.ListAsync((await queries.ProjectAsync(project, ct)).Id, ct))));

    [McpServerTool(Name = "get_plan", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Reads a plan: its state, revision, the totals an approval agrees to, findings, steps with their states, and the page in Lumibelle where the director reviews it. Name a step to read its full prompt texts.")]
    public Task<string> GetPlan([Description(Project)] string project, [Description(PlanId)] string plan, [Description("A step's id, to read that step in full.")] string? step = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var found = await plans.Store.GetAsync(snapshot.ProjectId, Id(plan), ct);
        return Text(ReviewQueries.Plan(snapshot, found, step is null ? null : Id(step), Page(http.HttpContext, snapshot.ProjectId, found.Id)));
    });

    [McpServerTool(Name = "wait_for_plan", ReadOnly = true, OpenWorld = false), Description("Waits until the plan changes, for example when the director approves it, a render finishes or a pick is made, then returns it as get_plan does. Returns after the timeout if nothing changed; call it again to keep waiting.")]
    public Task<string> WaitForPlan([Description(Project)] string project, [Description(PlanId)] string plan,
        [Description("How long to wait, 1 to 50 seconds.")] int seconds = 25, CancellationToken ct = default) => Guard(async () =>
    {
        var found = await queries.ProjectAsync(project, ct); var id = Id(plan);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Signal(Guid projectId, Guid planId) { if (projectId == found.Id && planId == id) changed.TrySetResult(); }
        plans.Store.Changed += Signal;
        try
        {
            await plans.Store.GetAsync(found.Id, id, ct);
            await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 50)), ct));
            ct.ThrowIfCancellationRequested();
        }
        finally { plans.Store.Changed -= Signal; }
        var snapshot = await queries.SnapshotAsync(project, ct);
        return Text(new { changed = changed.Task.IsCompleted, plan = ReviewQueries.Plan(snapshot, await plans.Store.GetAsync(found.Id, id, ct), page: Page(http.HttpContext, found.Id, id)) });
    });

    [McpServerTool(Name = "create_plan", OpenWorld = false), Description("Starts a plan: a list of findings and proposed steps for the director to review. Creating a plan changes nothing in the project.")]
    public Task<string> CreatePlan([Description(Project)] string project, [Description("A short title saying what the plan is for, such as “Notes on rough cut 3”.")] string title, CancellationToken ct) => Guard(async () =>
    {
        var found = await queries.ProjectAsync(project, ct); var plan = await plans.CreateAsync(found.Id, title, Client(http.HttpContext), ct);
        return Text(new { id = plan.Id, title = plan.Title, state = plan.State.ToString(), page = Page(http.HttpContext, found.Id, plan.Id) });
    });

    [McpServerTool(Name = "add_finding", OpenWorld = false), Description("Records something that is wrong, with what shows it. Findings explain steps to the director; a finding by itself changes nothing.")]
    public Task<string> AddFinding([Description(Project)] string project, [Description(PlanId)] string plan,
        [Description("What is wrong, in one or two plain sentences.")] string message,
        [Description("What shows it: the exact prompt text, the frame and take, the check result, or the director's note and its time in the cut.")] string evidence,
        [Description("The shot it is about, by number or id. Leave out for the whole film.")] string? shot = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        var finding = await plans.AddFindingAsync(snapshot.ProjectId, Id(plan), shot is null ? null : ReviewQueries.Shot(snapshot, shot).Id, message, evidence, Client(http.HttpContext), ct);
        return Text(new { id = finding.Id });
    });

    [McpServerTool(Name = "propose_step", OpenWorld = false), Description("""
        Proposes one change or one render as a step of a plan. It is checked against the project now and recorded; it runs only after the director approves the plan.
        Kinds and what each needs:
        - SetPrompt: shot, and either edits (exact text to replace, each occurring once) or prompt (the whole new prompt). The prompt is marked reviewed when the step runs.
        - MarkPromptReviewed: shot. For a prompt that still fits after its references changed.
        - SetVoice: shot, character (asset id), and voice (a recording's id from list_assets); leave voice out for None.
        - AddShotFrame: shot, frame_name, and either take with frame (counted from 1) or frame_position, or from_shot with frame_position (first or last) to use the take the director will pick for that shot in this plan, or the take a UseTake step of this plan selects for it.
        - RemoveShotFrame: shot, take and frame (counted from 1) as get_shot lists them.
        - ReplacePicture: shot, asset and image selected now, and either new_asset and new_image to select instead, or from_step with the id of an EditImage step in this plan to use the image the director picks from it. The picture keeps its number in the prompt.
        - RemovePicture: shot, asset and image. Later pictures are renumbered, so propose the prompt change too.
        - UseTake: shot and take, to select an existing take.
        - GenerateTakes: shot, takes (1 to 4). Optional: setup, a global setup's name or id, instead of the one selected in Shots at approval; preset, an H3 preset key for this render only; resolution. list_video_options has the choices. A shot's other steps run before its render.
        - EditImage: asset and image to edit, prompt with the edit instruction, takes (1 to 4 candidates). Optional: model, instead of the project's default, from list_image_models; preset, for MiniMax H3, which otherwise runs Standard; references, further images after the source, which the instruction can call image 2 and so on. Keeps the source's proportions and look. The candidates wait for the director to pick one; those not picked go to Trash.
        - UpdateCut: nothing more. Points the cut at the selected takes, after every pick is made.
        """)]
    public Task<string> ProposeStep([Description(Project)] string project, [Description(PlanId)] string plan, [Description(Kinds)] string kind,
        [Description("Why the step is needed, for the director to read.")] string reason,
        [Description("The shot the step changes, by number or id. Not for UpdateCut.")] string? shot = null,
        [Description("Ids of this plan's findings the step answers.")] string[]? findings = null,
        [Description("SetPrompt: exact replacements.")] PromptEdit[]? edits = null, [Description("SetPrompt: the whole new prompt, instead of edits.")] string? prompt = null,
        [Description("SetVoice: the character's asset id.")] string? character = null, [Description("SetVoice: the voice recording's id.")] string? voice = null,
        [Description("The asset id of the picture selected now.")] string? asset = null, [Description("The image id of the picture selected now.")] string? image = null,
        [Description("ReplacePicture: the asset id of the picture to select instead.")] string? new_asset = null, [Description("ReplacePicture: the image id of the picture to select instead.")] string? new_image = null,
        [Description("ReplacePicture: the id of the EditImage step in this plan whose picked image to use.")] string? from_step = null,
        [Description("A take's id: the frame's source, the take to select, or the shot frame to remove.")] string? take = null,
        [Description("AddShotFrame: the shot whose picked take supplies the frame, when that take is rendered by this plan.")] string? from_shot = null,
        [Description("AddShotFrame: first or last.")] string? frame_position = null, [Description("A frame number, counted from 1.")] int? frame = null,
        [Description("AddShotFrame: what the frame shows, such as “End of the arrival”.")] string? frame_name = null,
        [Description("GenerateTakes: how many takes, 1 to 4. EditImage: how many candidates.")] int takes = 1, [Description("GenerateTakes: Quick, Preview, Detail, Native, FullHd or Qhd. Leave out to use the setup's own setting.")] string? resolution = null,
        [Description("GenerateTakes: a global generation setup, by name or id.")] string? setup = null, [Description("An H3 preset key, from list_video_options.")] string? preset = null,
        [Description("EditImage: an image model key, from list_image_models.")] string? model = null, [Description("EditImage: further reference images after the source.")] ImageReference[]? references = null,
        CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        var proposal = Proposal(snapshot, kind, reason, shot, findings, edits, prompt, character, voice, asset, image, new_asset, new_image, take, from_shot, frame_position, frame, frame_name, takes, resolution, from_step, setup, preset, model, references);
        var step = await plans.ProposeAsync(snapshot.ProjectId, Id(plan), proposal, Client(http.HttpContext), ct: ct);
        return Text(new { id = step.Id, does = ReviewPlans.Describe(snapshot, step), state = step.State.ToString() });
    });

    [McpServerTool(Name = "revise_step", OpenWorld = false), Description("Rewrites a step that has not been approved yet, for example after the prompt changed or the director asked for something else. Give the step's full content again, as for propose_step; the kind and the shot stay the same.")]
    public Task<string> ReviseStep([Description(Project)] string project, [Description(PlanId)] string plan, [Description("The id of the step to rewrite.")] string step, [Description("Why the step is needed, for the director to read.")] string reason,
        string[]? findings = null, PromptEdit[]? edits = null, string? prompt = null, string? character = null, string? voice = null, string? asset = null, string? image = null,
        string? new_asset = null, string? new_image = null, string? from_step = null, string? take = null, string? from_shot = null, string? frame_position = null, int? frame = null, string? frame_name = null,
        int takes = 1, string? resolution = null, string? setup = null, string? preset = null, string? model = null, ImageReference[]? references = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var planId = Id(plan);
        var old = (await plans.Store.GetAsync(snapshot.ProjectId, planId, ct)).Step(Id(step)) ?? throw new WorkspaceStoreException("That step is not in this plan.");
        var proposal = Proposal(snapshot, old.Kind.ToString(), reason, old.ShotId?.ToString(), findings, edits, prompt, character, voice, asset, image, new_asset, new_image, take, from_shot, frame_position, frame, frame_name, takes, resolution, from_step, setup, preset, model, references);
        var revised = await plans.ProposeAsync(snapshot.ProjectId, planId, proposal, Client(http.HttpContext), replaces: old.Id, ct);
        return Text(new { id = revised.Id, does = ReviewPlans.Describe(snapshot, revised), state = revised.State.ToString() });
    });

    [McpServerTool(Name = "submit_plan", Idempotent = true, OpenWorld = false), Description("Hands the plan to the director for approval. Tell the director what the plan contains and where to review it; then use wait_for_plan. Once approved, Lumibelle runs the plan by itself; you do not need to stay connected. Use get_queue to see how far a render has come.")]
    public Task<string> SubmitPlan([Description(Project)] string project, [Description(PlanId)] string plan, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        return Text(ReviewQueries.Plan(snapshot, await plans.SubmitAsync(snapshot.ProjectId, Id(plan), Client(http.HttpContext), ct), page: Page(http.HttpContext, snapshot.ProjectId, Id(plan))));
    });

    [McpServerTool(Name = "recommend_take", Idempotent = true, OpenWorld = false), Description("Advises the director which of a step's new takes or images to use, with the reason. Look at them first: get_frames and get_neighbour_sheet for takes, get_asset_image for images. The director still makes the pick.")]
    public Task<string> RecommendTake([Description(Project)] string project, [Description(PlanId)] string plan, [Description("The step's id.")] string step,
        [Description("The id of the take or image you recommend, from the step's takesToPickFrom or imagesToPickFrom.")] string take, [Description("Why this take, from what its frames show.")] string reason, CancellationToken ct) => Guard(async () =>
    {
        var found = await queries.ProjectAsync(project, ct);
        await plans.RecommendAsync(found.Id, Id(plan), Id(step), Id(take), reason, Client(http.HttpContext), ct);
        return Text(new { recommended = take });
    });

    internal static Guid Id(string value) => Guid.TryParse(value, out var id) ? id : throw new WorkspaceStoreException($"“{value}” is not an id. Use the ids the other tools return.");
    internal static string Client(HttpContext? http)
    {
        // Each request stands alone, so the client is known by how it names itself in the request.
        var agent = http?.Request.Headers.UserAgent.ToString() ?? "";
        var name = agent.Split(' ', '/')[0].Trim();
        // Never the app's own name, so a client's work is never shown as the director's.
        return name.Length is > 0 and <= 60 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') && !name.Equals(ReviewPlans.Director, StringComparison.OrdinalIgnoreCase) ? name : "MCP client";
    }
    internal static string? Page(HttpContext? http, Guid project, Guid plan) => http is null ? null : $"{http.Request.Scheme}://{http.Request.Host}/projects/{project:D}/review?planId={plan:D}";

    private static ReviewStepProposal Proposal(ProjectReviewSnapshot snapshot, string kind, string reason, string? shot, string[]? findings, PromptEdit[]? edits, string? prompt,
        string? character, string? voice, string? asset, string? image, string? newAsset, string? newImage, string? take, string? fromShot, string? framePosition, int? frame, string? frameName, int takes, string? resolution, string? fromStep, string? setup, string? preset, string? model, ImageReference[]? references)
    {
        if (!Enum.TryParse<ReviewStepKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) throw new WorkspaceStoreException("Choose one of these kinds: " + Kinds);
        Guid? Optional(string? value) => value is null ? null : Id(value);
        var position = framePosition?.Trim().ToLowerInvariant() switch
        {
            "first" => ReviewFramePosition.First, "last" => ReviewFramePosition.Last, null or "" => frame is null ? ReviewFramePosition.Last : ReviewFramePosition.Index,
            _ => throw new WorkspaceStoreException("frame_position is first or last.")
        };
        if (frame is < 1) throw new WorkspaceStoreException("Frames are counted from 1.");
        // A removal names its frame; the defaults that help an addition must never pick a frame to remove.
        if (parsed == ReviewStepKind.RemoveShotFrame && (frame is null || framePosition is { Length: > 0 }))
            throw new WorkspaceStoreException("Name the shot frame to remove by its take and frame number, counted from 1, as get_shot lists them.");
        VideoResolution? size = null;
        if (resolution is { Length: > 0 }) size = Enum.TryParse<VideoResolution>(resolution, ignoreCase: true, out var chosen) && Enum.IsDefined(chosen) ? chosen : throw new WorkspaceStoreException("resolution is Quick, Preview, Detail, Native, FullHd or Qhd.");
        ImageWorkflow? workflow = null;
        if (model is { Length: > 0 }) workflow = Enum.GetValues<ImageWorkflow>().Where(w => w.ToString().Equals(model.Trim(), StringComparison.OrdinalIgnoreCase) || w.Label().Equals(model.Trim(), StringComparison.OrdinalIgnoreCase)).Cast<ImageWorkflow?>().FirstOrDefault()
            ?? throw new WorkspaceStoreException("Choose an image model from list_image_models.");
        var needsFrame = parsed is ReviewStepKind.AddShotFrame or ReviewStepKind.RemoveShotFrame;
        return new(parsed, shot is null ? null : Guid.TryParse(shot, out var shotId) ? shotId : ReviewQueries.Shot(snapshot, shot).Id, reason)
        {
            FindingIds = (findings ?? []).Select(Id).ToArray(), Prompt = prompt, Edits = (edits ?? []).Select(e => (e.Old, e.New)).ToArray(),
            CharacterAssetId = Optional(character), VoiceId = Optional(voice), AssetId = Optional(asset), ImageId = Optional(image), NewAssetId = Optional(newAsset), NewImageId = Optional(newImage),
            TakeId = needsFrame ? null : Optional(take), FromStepId = Optional(fromStep),
            Frame = !needsFrame ? null : new() { TakeId = Optional(take), FromShotId = fromShot is null ? null : ReviewQueries.Shot(snapshot, fromShot).Id, Position = position, Index = (frame ?? 1) - 1, Name = frameName ?? "" },
            Takes = takes, Resolution = size, Setup = setup, Preset = preset, Workflow = workflow,
            References = (references ?? []).Select(r => new AssetImageReference(Id(r.Asset), Id(r.Image))).ToArray()
        };
    }
}

/// <summary>
/// Tools that only reduce work: they stop, cancel or pause, and none starts anything or changes a shot. They need no approval
/// from the director, so an agent can stop at once when asked or when it sees that a plan is wrong.
/// </summary>
[McpServerToolType]
public sealed class ReviewControlTools(ReviewQueries queries, ReviewPlanRunner runner, AiJobCoordinator jobs, IHttpContextAccessor http)
{
    [McpServerTool(Name = "stop_plan", Destructive = true, Idempotent = true, OpenWorld = false), Description("Stops a plan: no more of its steps run, and its waiting and running renders are cancelled. Changes it already made to prompts, references and selections stay, and takes it already finished stay in Shots. A stopped plan cannot be restarted; propose what is still needed in a new plan.")]
    public Task<string> StopPlan([Description("The project's id, or its exact name.")] string project, [Description("The plan's id.")] string plan, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var id = ReviewPlanTools.Id(plan);
        var stopped = await runner.StopAsync(snapshot.ProjectId, id, ReviewPlanTools.Client(http.HttpContext), ct);
        return Text(ReviewQueries.Plan(await queries.SnapshotAsync(project, ct), stopped, page: ReviewPlanTools.Page(http.HttpContext, snapshot.ProjectId, id)));
    });

    [McpServerTool(Name = "cancel_request", Destructive = true, Idempotent = true, OpenWorld = false), Description("Cancels one request in the AI queue, as Cancel request does in AI activity. A running render stops mid-way; AI activity can recover any takes it had finished. The queue is shared with the director's own work, so cancel a request you did not start only when asked. Use get_queue for the ids.")]
    public Task<string> CancelRequest([Description("The request's id, from get_queue.")] string request, CancellationToken ct) => Guard(async () =>
    {
        var id = ReviewPlanTools.Id(request);
        var job = jobs.View.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("That request is not in the queue. Use get_queue for the ids.");
        await jobs.CancelAsync(job.Id, ct);
        return Text(queries.Queue(job.Target.ProjectId));
    });

    [McpServerTool(Name = "pause_queue", Idempotent = true, OpenWorld = false), Description("Pauses one provider's queue: waiting requests stay queued and do not start. It is shared with the director's own work. On ComfyUI, a render already running continues unless you cancel it. Only the director can resume, unless they allow approval from the client.")]
    public Task<string> PauseQueue([Description(ReviewApprovalTools.Providers)] string provider, CancellationToken ct) => Guard(async () =>
    {
        await jobs.SetPausedAsync(ReviewApprovalTools.Provider(provider), true, ct);
        return Text(queries.Queue());
    });
}

/// <summary>
/// The tools that act or start work, listed only when the director turned on approval from the client. All carry the mark that makes
/// Claude Code ask the director on every call, in every permission mode. Lumibelle cannot see that prompt, so the server also
/// refuses an approval that does not name the plan exactly as it stands.
/// </summary>
[McpServerToolType]
public sealed class ReviewApprovalTools(ReviewQueries queries, ReviewPlans plans, ReviewPlanRunner runner, AiJobCoordinator jobs, IHttpContextAccessor http)
{
    public static readonly string[] Names = ["approve_plan", "accept_picks", "resume_queue", "resume_request"];
    public const string Off = "Approval from the client is turned off. The director does this in Lumibelle: plans and picks on the plan's page from get_plan, queues and requests in AI activity.";
    public const string Providers = "The provider: ComfyUI, Vllm, OpenRouter or Codex.";
    internal static AiBackend Provider(string name) => Enum.TryParse<AiBackend>(name, ignoreCase: true, out var backend) && Enum.IsDefined(backend) && !name.All(char.IsDigit) && !TextModelPolicy.Retired(backend)
        ? backend : throw new WorkspaceStoreException(Providers);

    [McpServerTool(Name = "resume_queue", OpenWorld = false), McpMeta("anthropic/requiresUserInteraction", true)]
    [Description("Resumes a paused provider queue on the director's word, so its waiting requests can start and spend GPU time or credits.")]
    public Task<string> ResumeQueue([Description(Providers)] string provider, CancellationToken ct) => Guard(async () =>
    {
        await jobs.SetPausedAsync(Provider(provider), false, ct);
        return Text(queries.Queue());
    });

    [McpServerTool(Name = "resume_request", OpenWorld = false), McpMeta("anthropic/requiresUserInteraction", true)]
    [Description("On the director's word, picks a request back up as AI activity does: checks a lost render's status, retries its output, continues it, or recovers takes a cancelled render had finished. Continuing can spend GPU time. It does nothing for a request that has nothing to recover.")]
    public Task<string> ResumeRequest([Description("The request's id, from get_queue.")] string request, CancellationToken ct) => Guard(async () =>
    {
        var id = ReviewPlanTools.Id(request);
        var job = jobs.View.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("That request is not in the queue. Use get_queue for the ids.");
        await jobs.ResumeAsync(job.Id, ct);
        return Text(queries.Queue(job.Target.ProjectId));
    });

    [McpServerTool(Name = "approve_plan", Destructive = true, OpenWorld = false), McpMeta("anthropic/requiresUserInteraction", true)]
    [Description("Approves a submitted plan on the director's word, so its changes and renders start. Call it only after the director has seen the plan and agreed in this conversation. Repeat the plan's revision and totals exactly as get_plan returned them; the call is refused if the plan changed since.")]
    public Task<string> ApprovePlan([Description("The project's id, or its exact name.")] string project, [Description("The plan's id.")] string plan,
        [Description("The plan's revision, from get_plan.")] long revision, [Description("totals.takes from get_plan: takes that will be rendered.")] int takes,
        [Description("totals.footageSeconds from get_plan: seconds of footage that will be rendered.")] double footage_seconds,
        [Description("totals.changes from get_plan: changes to prompts, references, selections and the cut.")] int changes,
        [Description("totals.images from get_plan: image candidates that will be generated.")] int images = 0,
        [Description("totals.renderSetup from get_plan, when it has one: the setup for renders that name none.")] string? render_setup = null,
        [Description("totals.imageModel from get_plan, when it has one: the image model for edits that name none.")] string? image_model = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var id = ReviewPlanTools.Id(plan);
        var setup = render_setup is { Length: > 0 } named ? Guid.TryParse(named.Trim(), out var parsed) ? parsed : throw new WorkspaceStoreException("render_setup is the id get_plan lists as totals.renderSetup.") : (Guid?)null;
        var model = image_model is { Length: > 0 } key ? Enum.TryParse<ImageWorkflow>(key.Trim(), ignoreCase: true, out var workflow) && Enum.IsDefined(workflow) ? workflow
            : throw new WorkspaceStoreException("image_model is the key get_plan lists as totals.imageModel.") : (ImageWorkflow?)null;
        await plans.ApproveFromClientAsync(snapshot.ProjectId, id, ReviewPlanTools.Client(http.HttpContext), revision, new(takes, footage_seconds, changes, images) { RenderSetupId = setup, ImageModel = model }, ct);
        var running = await runner.AdvanceAsync(snapshot.ProjectId, id, ct);
        return Text(ReviewQueries.Plan(await queries.SnapshotAsync(project, ct), running, page: ReviewPlanTools.Page(http.HttpContext, snapshot.ProjectId, id)));
    });

    [McpServerTool(Name = "accept_picks", Destructive = true, OpenWorld = false), McpMeta("anthropic/requiresUserInteraction", true)]
    [Description("Selects, on the director's word, what to use from each step waiting for a pick: the take for a render, named by shot, or the image for an edit, named by step. Name each one. Later steps that waited for these picks then run.")]
    public Task<string> AcceptPicks([Description("The project's id, or its exact name.")] string project, [Description("The plan's id.")] string plan,
        [Description("The take or image chosen for each step.")] TakePick[] picks, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var id = ReviewPlanTools.Id(plan); var current = await plans.Store.GetAsync(snapshot.ProjectId, id, ct);
        if (picks is not { Length: > 0 }) throw new WorkspaceStoreException("Name at least one step or shot, and what was chosen.");
        // All of them are checked before any is applied, so a wrong id leaves every pick to the director.
        var chosen = picks.Select(pick =>
        {
            var take = ReviewPlanTools.Id(pick.Take);
            ReviewPlanStep? step;
            if (pick.Step is { } named) step = current.Step(ReviewPlanTools.Id(named));
            else
            {
                if (pick.Shot is null) throw new WorkspaceStoreException("Name the shot of a render, or the step of an image edit.");
                var shot = ReviewQueries.Shot(snapshot, pick.Shot);
                step = current.Steps.FirstOrDefault(s => s.ShotId == shot.Id && s.State == ReviewStepState.Pick);
            }
            if (step is not { State: ReviewStepState.Pick }) throw new WorkspaceStoreException("That step or shot has nothing waiting for a pick in this plan.");
            return step.Produced.Contains(take) ? (Step: step.Id, Take: take) : throw new WorkspaceStoreException("That is not one of the takes or images this step produced.");
        }).ToArray();
        // All of them are checked again, together, before any is made: one step named twice, or a take moved to Trash meanwhile,
        // leaves every selection as it was.
        current = await runner.PickAllAsync(snapshot.ProjectId, id, chosen, ReviewPlanTools.Client(http.HttpContext), ct);
        return Text(ReviewQueries.Plan(await queries.SnapshotAsync(project, ct), current, page: ReviewPlanTools.Page(http.HttpContext, snapshot.ProjectId, id)));
    });
}
