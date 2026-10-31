using Microsoft.AspNetCore.Components;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Shots;
using MudBlazor;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    [Inject] public ISnackbar Snackbar { get; set; } = null!;
    private IReadOnlyList<RefineSource> _refineSources = [];
    public void RefineFromVideo(AssetReferenceReel reel)
    {
        if (!_busy && MotionReferencePolicy.Issue(reel) is null) RefineReels([reel]);
    }
    // Ineligible reels stay in the list, flagged, so a mixed selection can still be queued.
    public void RefineReels(IEnumerable<AssetReferenceReel> reels)
    {
        var sources = reels.Select(r => RefineSource.From(ProjectId, r)).ToArray();
        if (_busy || sources.Length == 0) return;
        _refiningReel = null; _regenerateReel = null; _refineSources = sources;
        StateHasChanged();
    }
    private void RefinementQueued(RefinementBatch batch)
    {
        _refineSources = [];
        var url = ProjectLink(ProjectId, $"/refinements?batchId={batch.Id}");
        Snackbar.Add($"{batch.Name} queued{(batch.State == RefinementBatchState.Held ? " and held" : "")}. Media is prepared in the background; source reels are unchanged.",
            Severity.Success, config => { config.Action = "View batch"; config.OnClick = _ => { Navigation.NavigateTo(url); return Task.CompletedTask; }; });
    }
}
