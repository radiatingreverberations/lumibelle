using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private IReadOnlyList<RefineSource> _refineSources = [];
    // Mounted on first opening, then kept: removing an open dialog would leave it on screen.
    private bool _refinementBatchesOpen, _refinementBatchesMounted;
    private Guid? _requestedRefinementBatch;
    [Inject] public RefinementBatchWorkflow BatchWorkflow { get; set; } = null!;
    private RefinementBatches? _refinementRuns;
    private int _refinementLoads;
    // A finished batch is new until its completed clips are seen, in the batches or as new takes.
    // The header is narrow, so its badge shows only the count; the label says "new".
    private int NewRefinementBatches => _refinementRuns?.Runs.Count(r => r.State == RefinementBatchState.Finished &&
        r.Items.Any(i => UnreadJobs.Contains(i.Id) && AiJobs.View.Jobs.Any(j => j.Id == i.Id && j.State == AiJobState.Completed))) ?? 0;
    private string? NewRefinementLabel => NewRefinementBatches is > 0 and var count ? $"{count} new" : null;
    private async Task LoadRefinementRuns()
    {
        var project = Id; var load = ++_refinementLoads;
        try
        {
            var runs = await BatchWorkflow.LoadAsync(project, _lifetime.Token);
            if (!_disposed && Id == project && load == _refinementLoads) _refinementRuns = runs;
        }
        catch (Exception e) when (e is lumibelle.Services.Story.WorkspaceStoreException or OperationCanceledException) { }
    }
    private void RefinementRunsChanged(Guid project)
    {
        if (_disposed || project != Id) return;
        _ = InvokeAsync(async () => { await LoadRefinementRuns(); if (!_disposed) StateHasChanged(); });
    }
    private bool CanRefineFromVideo(ShotTake take) => !_starting && !_mediaBusy && MotionReferencePolicy.Issue(take) is null;
    private void RefineFromVideo(ShotTake take) { if (CanRefineFromVideo(take)) _refineSources = [RefineSource.From(Id, take, TakeName(take))]; }
    // Ineligible takes stay in the list, flagged, so a mixed selection can still be queued.
    private void RefineTakes(IEnumerable<ShotTake> takes)
    {
        var sources = takes.Select(t => RefineSource.From(Id, t, TakeName(t))).ToArray();
        if (sources.Length > 0 && !_starting && !_mediaBusy) _refineSources = sources;
    }
    private void RefineSelectedTakes() => RefineTakes(ShotTakes.Where(t => _moveTakeIds.Contains(t.Id)));
    // Each shot's selected take, in shot order; the dialog lets clips be left out.
    private IReadOnlyList<ShotTake> SelectedTakes =>
        _doc.Shots.Where(s => s.SelectedTakeId is not null).Select(s => _doc.Takes.FirstOrDefault(t => t.Id == s.SelectedTakeId)).OfType<ShotTake>().ToArray();
    private void RefineSelectedTakesOfShots() => RefineTakes(SelectedTakes);
    private async Task RefinementQueued(RefinementBatch batch)
    {
        _refineSources = [];
        if (SelectingTakes) { _selectingTakes = false; _moveTakeIds.Clear(); }
        Snackbar.Add($"{batch.Name} queued{(batch.State == RefinementBatchState.Held ? " and held" : "")}. Media is prepared in the background; originals stay selected.",
            Severity.Success, config => { config.Action = "View batch"; config.OnClick = _ => InvokeAsync(() => OpenRefinementBatches(batch.Id)); });
        await RefreshMedia();
    }
    private void OpenRefinementBatches(Guid? batch = null) { _requestedRefinementBatch = batch; _refinementBatchesMounted = _refinementBatchesOpen = true; StateHasChanged(); }
}
