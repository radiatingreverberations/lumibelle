using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

// A shot can open exactly on a frame of an earlier take, so it continues that take without a visible cut.
public partial class ProductionStudio
{
    // The paused frame being continued, and the existing shot to start from it; null adds a new shot after the take's shot.
    private (Guid TakeId, int Index)? _continueFrame;
    private TakeExtensionDirection _extensionDirection;
    private Guid? _continueTarget;
    private void OpenContinue(int index)
    {
        if (ReviewTake is not { } take) return;
        _frameDestination = null; _reviewError = null;
        _continueFrame = (take.Id, index); _continueTarget = null;
        _extensionDirection = TakeExtensionDirection.After;
    }
    private void OpenLeadInto(int index) { OpenContinue(index); _extensionDirection = TakeExtensionDirection.Before; }
    private void CloseContinue() { if (_extensionPreparation is not null) { _extensionPreparation.Cancel(); return; } _continueFrame = null; _restoreFrameFocus = true; }
    /// <summary>The shots a frame can be continued in: any shot but the one the take belongs to.</summary>
    private IEnumerable<Shot> ContinueTargets(ShotTake take) => _doc.Shots.Where(s => s.Id != take.ShotId);

    private async Task ConfirmContinue()
    {
        if (_continueFrame is not { } frame || ReviewTake is not { } take || take.Id != frame.TakeId) return;
        if (_continueTarget is { } target) await StartShotFrom(target, take, frame.Index);
        else await ContinueFromFrame(take, frame.Index);
    }

    /// <summary>Adds a shot after the reviewed take's shot that starts from the paused frame, and opens it.</summary>
    private async Task ContinueFromFrame(ShotTake take, int index)
    {
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        if (!await Save()) return;
        if (_doc.Shots.FirstOrDefault(s => s.Id == take.ShotId) is not { } source) { _reviewError = "Restore this take's shot before continuing from it."; return; }
        Shot shot;
        try { shot = ProductionPolicy.Continuation(source, take, index); }
        catch (WorkspaceStoreException e) { _reviewError = e.Message; return; }
        _continueFrame = null;
        await ReviewVisibility(false);
        _openShotView = true; Remember(); _coverageDirty = true;
        _doc.Shots.Insert(_doc.Shots.IndexOf(source) + 1, shot);
        _selected = shot.Id; _compositionId = null; _savedComposition = null; CoverageChanged();
        Notify($"Added “{shot.Title}”. It starts from frame {index + 1} of {TakeName(take)}; Draft shot writes what happens next.");
    }

    /// <summary>Makes an existing shot start from the paused frame, for example when a cutaway sits between the two.</summary>
    private async Task StartShotFrom(Guid target, ShotTake take, int index)
    {
        if (_doc.Shots.FirstOrDefault(s => s.Id == target) is not { } shot) { _reviewError = "Choose an existing shot."; return; }
        var start = new ShotStartFrame(take.Id, index);
        try { H3Policy.Validate(ShotCopy.Of(shot) with { StartFrame = start }); }
        catch (WorkspaceStoreException e) { _reviewError = e.Message; return; }
        _continueFrame = null;
        await ReviewVisibility(false);
        await Select(target);
        if (_selected != target) return;
        _openShotView = true;
        EditCoverage(s => s.StartFrame = start);
        Notify($"“{shot.Title}” now starts from frame {index + 1} of {TakeName(take)}. Check its prompt before generating.");
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
    private void RemoveStartFrame() => EditCoverage(s => s.StartFrame = null);

    /// <summary>The last frame of a shot's production take, as a continuity picture for the shot that copies its references.</summary>
    private ShotContinuityFrame? LastFrameOf(Shot source) =>
        _doc.Shots.FirstOrDefault(s => s.Id == source.Id)?.SelectedTakeId is { } id && _doc.Takes.FirstOrDefault(t => t.Id == id) is { } take
            ? new(Guid.NewGuid(), take.Id, take.FrameCount - 1, $"{source.Title} · last frame") : null;
}
