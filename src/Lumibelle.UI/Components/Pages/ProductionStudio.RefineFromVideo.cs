using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Shots;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private IReadOnlyList<RefineSource> _refineSources = [];
    private bool _refinementBatchesOpen;
    private Guid? _requestedRefinementBatch;
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
    private void OpenRefinementBatches(Guid? batch = null) { _requestedRefinementBatch = batch; _refinementBatchesOpen = true; StateHasChanged(); }
}
