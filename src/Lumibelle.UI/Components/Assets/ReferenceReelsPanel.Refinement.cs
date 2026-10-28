using System.Text.Json;
using Microsoft.AspNetCore.Components;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    private AssetReferenceReel? _refiningReel;
    private bool _prepareRefinement;
    public void Refine(AssetReferenceReel reel)
    {
        if (_busy || reel.Generation is null) return;
        _refiningReel = reel; StateHasChanged();
    }
    private void PrepareRefinement(AssetReferenceReel reel)
    {
        if (_busy || !CanRegenerate(reel)) return;
        _refiningReel = null; _prepareRefinement = true; _regenerateReel = reel;
    }
    private void CloseRegeneration() { _regenerateReel = null; _prepareRefinement = false; }
    private Task RefinementSubmitted(AiJobSubmission submission)
    {
        _videoJob = Jobs.View.Jobs.SingleOrDefault(j => j.Id == submission.Id);
        _videoRequest = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options);
        return Task.CompletedTask;
    }
    private async Task ChangeLatentRetention(ChangeEventArgs change)
    {
        if (_draft is null) return;
        _draft.SaveLatents = change.Value is true;
        await Changed();
    }
}
