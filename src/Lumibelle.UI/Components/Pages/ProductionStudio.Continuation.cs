using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

// A shot can open exactly on a frame of an earlier take, so it continues that take without a visible cut.
public partial class ProductionStudio
{
    /// <summary>Adds a shot after the reviewed take's shot that starts from the paused frame, and opens it.</summary>
    private async Task ContinueFromFrame(int index)
    {
        if (ReviewTake is not { } take) return;
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        if (!await Save()) return;
        if (_doc.Shots.FirstOrDefault(s => s.Id == take.ShotId) is not { } source) { _reviewError = "Restore this take's shot before continuing from it."; return; }
        Shot shot;
        try { shot = ProductionPolicy.Continuation(source, take, index); }
        catch (WorkspaceStoreException e) { _reviewError = e.Message; return; }
        await ReviewVisibility(false);
        _openShotView = true; Remember(); _coverageDirty = true;
        _doc.Shots.Insert(_doc.Shots.IndexOf(source) + 1, shot);
        _selected = shot.Id; _compositionId = null; _savedComposition = null; CoverageChanged();
        Notify($"Added “{shot.Title}”. It starts from frame {index + 1} of {TakeName(take)}; Draft shot writes what happens next.");
    }

    private ShotTake? StartTake(Shot shot) => shot.StartFrame is { } start ? _doc.Takes.FirstOrDefault(t => t.Id == start.TakeId) : null;
    private string TakeName(ShotTake take)
    {
        var owner = _doc.Shots.FirstOrDefault(s => s.Id == take.ShotId);
        var number = _doc.Takes.Where(t => t.ShotId == take.ShotId).OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id).ToList().IndexOf(take) + 1;
        return $"{owner?.Title ?? "another shot"} · Take {number}";
    }
    private string StartFrameLabel(ShotStartFrame start, ShotTake? take) =>
        take is null ? "The take is in Trash or was deleted. Restore it, or remove the starting frame." : $"{TakeName(take)} · frame {start.Frame + 1} of {take.FrameCount}";

    /// <summary>The previous shot in the scene and its production take, when this shot could start where that take ends.</summary>
    private (Shot Shot, ShotTake Take)? PreviousProductionTake
    {
        get
        {
            if (SourceShot is not { StartFrame: null } shot) return null;
            var previous = _doc.Shots.Take(_doc.Shots.IndexOf(shot)).LastOrDefault(s => s.SceneId == shot.SceneId);
            return previous?.SelectedTakeId is { } id && _doc.Takes.FirstOrDefault(t => t.Id == id) is { } take ? (previous, take) : null;
        }
    }
    private void StartFromPreviousShot()
    {
        if (PreviousProductionTake is { } previous) EditCoverage(s => s.StartFrame = new(previous.Take.Id, previous.Take.FrameCount - 1));
    }
    private void RemoveStartFrame() => EditCoverage(s => s.StartFrame = null);
}
