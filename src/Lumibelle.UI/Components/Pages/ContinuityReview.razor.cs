using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Review;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace lumibelle.Components.Pages;

/// <summary>The continuity checks' report and the plans proposed for this project. Approval happens here.</summary>
public partial class ContinuityReview
{
    [Parameter] public Guid Id { get; set; }
    /// <summary>The plan to open, from a link an agent gave the director.</summary>
    [Parameter] public Guid? RequestedPlanId { get; set; }
    private ProjectInfo? _project;
    private ProjectReviewSnapshot? _snapshot;
    private IReadOnlyList<ReviewFinding> _findings = [];
    private IReadOnlyList<ReviewPlan> _plans = [];
    private Guid? _planId, _handledPlan;
    private string? _error;
    private bool _busy, _measuring, _soundMeasured, _confirmRemove, _disposed, _viewChosen;
    private lumibelle.Components.Layout.StudioWorkspace? _workspace;
    private int _levels;
    private readonly CancellationTokenSource _lifetime = new();
    private ReviewPlan? Selected => _plans.FirstOrDefault(p => p.Id == _planId);

    protected override void OnInitialized() { Plans.Store.Changed += PlanChanged; Jobs.Changed += QueueChanged; }

    // Render progress and queue positions: only a plan that is running shows them.
    private void QueueChanged()
    {
        if (!_disposed && Selected?.Active == true) _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Where a running plan is: how far along, what is happening now, and what it is waiting for.</summary>
    private string? PlanProgress(ReviewPlan plan)
    {
        if (!plan.Active || _snapshot is not { } snapshot) return null;
        var counted = plan.Steps.Where(s => s.State != ReviewStepState.Rejected).ToArray();
        var parts = new List<string> { $"{counted.Count(s => s.State == ReviewStepState.Done)} of {counted.Length} steps done" };
        var renders = counted.Where(s => s.State == ReviewStepState.Running).Select(s => (Step: s, State: Jobs.View.Jobs.FirstOrDefault(j => j.Id == s.JobId)?.State)).ToArray();
        if (renders.FirstOrDefault(r => r.State == AiJobState.Running).Step is { } now) parts.Add("Now: " + ReviewPlans.Describe(snapshot, now));
        if (renders.Count(r => r.State == AiJobState.Waiting) is > 0 and var waiting) parts.Add($"{Count(waiting, "render")} waiting in the queue");
        if (renders.Count(r => r.State == AiJobState.NeedsAttention) is > 0 and var attention) parts.Add($"{Count(attention, "render")} {(attention == 1 ? "needs" : "need")} attention in AI activity");
        if (counted.Count(s => s.State == ReviewStepState.Pick) is > 0 and var picks) parts.Add($"{Count(picks, "choice")} waiting for you");
        if (counted.Count(s => s.State == ReviewStepState.Held) is > 0 and var held) parts.Add($"{held} held");
        return string.Join(" · ", parts);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_project?.Id != Id)
        {
            _project = null; _snapshot = null; _findings = []; _plans = []; _planId = null; _soundMeasured = false; _error = null; _viewChosen = false;
            try
            {
                _project = await Projects.GetAsync(Id, _lifetime.Token) ?? throw new WorkspaceStoreException("This project is no longer in your library.");
                await Reload();
            }
            catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _error = e.Message; }
        }
        if (RequestedPlanId is { } requested && _handledPlan != requested && _plans.Any(p => p.Id == requested)) { _handledPlan = requested; _planId = requested; _viewChosen = true; }
    }

    private async Task Reload()
    {
        _snapshot = await Review.LoadAsync(Id, _lifetime.Token);
        // Sound findings are kept until the checks are run again; measuring is the slow part.
        _findings = [.. ReviewChecks.Run(_snapshot), .. _findings.Where(f => f.Check == ReviewMedia.Levels)];
        _plans = await Plans.Store.ListAsync(Id, _lifetime.Token);
        if (!_viewChosen)
        {
            // The last view in this browser, or else a plan that needs the director. A plan arriving later never takes the page away.
            _viewChosen = true;
            var last = Place<string?>(Id, "review", "view", null);
            _planId = last == "checks" ? null : Guid.TryParse(last, out var remembered) && _plans.Any(p => p.Id == remembered) ? remembered
                : _plans.Where(p => p.Active || p.State == ReviewPlanState.Submitted).OrderByDescending(p => p.CreatedUtc).FirstOrDefault()?.Id;
        }
        if (_planId is not null && Selected is null) _planId = null;
    }

    // Raised from the store on whichever thread saved the plan: a proposer over MCP, the runner, or this page.
    private void PlanChanged(Guid project, Guid plan)
    {
        if (project != Id || _disposed) return;
        _ = InvokeAsync(async () =>
        {
            try { await Reload(); StateHasChanged(); }
            catch (Exception e) when (e is WorkspaceStoreException or OperationCanceledException or ObjectDisposedException) { }
        });
    }

    private string Summary
    {
        get
        {
            var problems = _findings.Count(f => f.Severity == ReviewSeverity.Problem); var looks = _findings.Count - problems;
            return _findings.Count == 0 ? $"No findings in {Count(_snapshot!.Rows.Count, "shot")}."
                : $"{Count(problems, "problem")} and {looks} worth a look, in {Count(_findings.Where(f => f.ShotId is not null).Select(f => f.ShotId).Distinct().Count(), "shot")} of {_snapshot!.Rows.Count}.";
        }
    }

    // Only shots that feed or follow another need an order.
    private IReadOnlyList<string[]> RenderWaves
    {
        get
        {
            if (_snapshot is null) return [];
            var order = ShotFrameOrder.Build(_snapshot);
            return order.Waves.Select(w => w.Where(s => order.Sources.ContainsKey(s) || order.Dependants(s).Count > 0).Select(s => "#" + _snapshot.Row(s)?.Number).ToArray()).Where(w => w.Length > 0).ToArray();
        }
    }

    private string ChecksSummary => _snapshot is null ? "Running…" : _snapshot.Rows.Count == 0 ? "No shots yet" : _findings.Count == 0 ? "No findings"
        : $"{Count(_findings.Count(f => f.Severity == ReviewSeverity.Problem), "problem")} · {_findings.Count(f => f.Severity == ReviewSeverity.Look)} worth a look";
    private static bool Open(ReviewPlan plan) => plan.State is ReviewPlanState.Draft or ReviewPlanState.Submitted;
    private string Title(Guid shot) => _snapshot?.Row(shot)?.Coverage.Title ?? "";
    private static string Count(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";

    private static string StateLabel(ReviewPlan plan) => plan.State switch
    {
        ReviewPlanState.Draft => "Being written", ReviewPlanState.Submitted => "Waiting for your approval",
        ReviewPlanState.Approved => plan.Steps.Any(s => s.State == ReviewStepState.Pick) ? "Waiting for your pick" : plan.Steps.Any(s => s.State == ReviewStepState.Held) ? "A step is held" : "Running",
        ReviewPlanState.Finished => "Finished", _ => "Stopped"
    };

    // A plan stopped by an earlier build can hold a step still marked as rendering.
    private static string StepLabel(ReviewPlan plan, ReviewPlanStep step) => step.Open && !plan.Active && plan.State is ReviewPlanState.Finished or ReviewPlanState.Stopped ? "Stopped" : step.State switch
    {
        ReviewStepState.Proposed => "Proposed", ReviewStepState.Rejected => "Rejected", ReviewStepState.Waiting => "Approved, waiting", ReviewStepState.Running => "Rendering",
        ReviewStepState.Pick => step.Kind == ReviewStepKind.EditImage ? "Pick an image" : "Pick a take", ReviewStepState.Done => "Done", ReviewStepState.Failed => "Failed", ReviewStepState.Held => "Held", _ => "Skipped"
    };

    private Task RunChecks() => Do(async () => { _findings = []; _soundMeasured = false; await Reload(); });

    private async Task MeasureSound()
    {
        if (_snapshot is null || _measuring) return;
        _measuring = true; _error = null;
        try
        {
            var levels = await Media.AudioLevelsAsync(_snapshot, ct: _lifetime.Token);
            _findings = [.. _findings.Where(f => f.Check != ReviewMedia.Levels), .. ReviewMedia.LevelFindings(_snapshot, levels)];
            _levels = levels.Count; _soundMeasured = true;
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _measuring = false; }
    }

    private async Task Select(Guid? plan)
    {
        _planId = plan; _confirmRemove = false;
        await Remember(Id, "review", "view", plan?.ToString() ?? "checks");
        if (_workspace is not null) await _workspace.RevealCenterAsync();
    }

    private Task Decide(ReviewPlan plan, ReviewPlanStep step, bool keep) => Do(() => Plans.DecideAsync(Id, plan.Id, step.Id, keep, _lifetime.Token));

    private Task Approve(ReviewPlan plan) => Do(async () =>
    {
        // Bound to what the page shows: a plan or shot that changed since must be looked at again.
        if (_snapshot is not { } shown) return;
        await Plans.ApproveAsync(Id, plan.Id, plan.Revision, ReviewPlans.Totals(plan, shown), _lifetime.Token);
        await Runner.AdvanceAsync(Id, plan.Id, _lifetime.Token);
        Snackbar.Add("Plan approved.", Severity.Success);
    });

    private Task Pick(ReviewPlan plan, ReviewPlanStep step, Guid take) => Do(() => Runner.PickAsync(Id, plan.Id, step.Id, take, ReviewPlans.Director, _lifetime.Token));
    private Task RejectPick(ReviewPlan plan, ReviewPlanStep step) => Do(() => Runner.RejectPickAsync(Id, plan.Id, step.Id, ReviewPlans.Director, _lifetime.Token));
    private Task AcceptRecommendations(ReviewPlan plan) => Do(() => Runner.AcceptRecommendationsAsync(Id, plan.Id, ReviewPlans.Director, ct: _lifetime.Token));
    private Task Stop(ReviewPlan plan) => Do(() => Runner.StopAsync(Id, plan.Id, ReviewPlans.Director, _lifetime.Token));

    private Task Release(ReviewPlan plan, ReviewPlanStep step) => Do(async () =>
    {
        // What the page shows is what is agreed to: the prompt for a prompt step, the length the hold names for a render.
        if (step.Kind == ReviewStepKind.GenerateTakes) await Plans.ReleaseRenderAsync(Id, plan.Id, step.Id, step.HeldSeconds ?? 0, _lifetime.Token);
        else await Plans.ReleaseAsync(Id, plan.Id, step.Id, _snapshot?.Row(step.ShotId ?? Guid.Empty)?.Prompt, _lifetime.Token);
        await Runner.AdvanceAsync(Id, plan.Id, _lifetime.Token);
    });

    private Task Remove() => Do(async () =>
    {
        if (_planId is { } plan) await Plans.Store.RemoveAsync(Id, plan, _lifetime.Token);
        _confirmRemove = false; _planId = null;
    });

    private async Task Do(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _error = null;
        try { await action(); await Reload(); }
        catch (WorkspaceConflictException) { _error = "The project changed while this was saved. Try again."; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        _disposed = true; Plans.Store.Changed -= PlanChanged; Jobs.Changed -= QueueChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
