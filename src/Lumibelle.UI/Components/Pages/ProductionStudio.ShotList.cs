using lumibelle.Components.Shots;
using lumibelle.Models;

namespace lumibelle.Components.Pages;

// The shot list: reordering within a scene by drag, row menu or Move to…, and collapsible scenes.
public partial class ProductionStudio
{
    private HashSet<Guid> _collapsedScenes = [];
    private Guid? _moveShotId;
    private Guid _moveShotTarget;
    private bool _moveShotAfter;
    private bool ShotListLocked => _starting || _setupBusy || _selectingShot;

    // Shots without a script scene share one group.
    private static Guid SceneKey(Shot shot) => shot.SceneId ?? Guid.Empty;
    private IEnumerable<Shot> SceneShots(Shot shot) => _doc.Shots.Where(s => SceneKey(s) == SceneKey(shot));
    private string SceneLabel(Shot shot) => !string.IsNullOrWhiteSpace(shot.SceneTitle) ? shot.SceneTitle
        : shot.SceneId is { } scene ? Scenes.FirstOrDefault(s => s.Id == scene)?.Title ?? "Untitled scene" : "No scene";
    private bool SceneCollapsed(Shot shot) => _collapsedScenes.Contains(SceneKey(shot));
    private async Task ToggleScene(Shot shot)
    {
        if (!_collapsedScenes.Remove(SceneKey(shot))) _collapsedScenes.Add(SceneKey(shot));
        await Remember(Id, "shots", "collapsedScenes", _collapsedScenes.ToList());
    }
    private void LoadCollapsedScenes() => _collapsedScenes = [.. Place<List<Guid>>(Id, "shots", "collapsedScenes", [])];

    private void MoveShotTo(ShotListOrder.ShotMove move)
    {
        if (ShotListLocked || move.Id == move.Target || _doc.Shots.FirstOrDefault(s => s.Id == move.Id) is not { } shot ||
            _doc.Shots.FirstOrDefault(s => s.Id == move.Target) is not { } target || SceneKey(shot) != SceneKey(target)) return;
        Remember(); _coverageDirty = true;
        _doc.Shots.Remove(shot);
        _doc.Shots.Insert(_doc.Shots.IndexOf(target) + (move.After ? 1 : 0), shot);
        _moveShotId = null;
        CoverageChanged();
    }
    // Up and down step past the neighbouring shot in the same scene, so numbering never jumps between scenes.
    private Shot? SceneNeighbour(Shot shot, int by)
    {
        var scene = SceneShots(shot).ToList(); var index = scene.IndexOf(shot) + by;
        return index >= 0 && index < scene.Count ? scene[index] : null;
    }
    private void MoveShotInScene(Guid id, int by)
    {
        if (_doc.Shots.FirstOrDefault(s => s.Id == id) is { } shot && SceneNeighbour(shot, by) is { } neighbour)
            MoveShotTo(new(id, neighbour.Id, by > 0));
    }
    private void MoveShot(int by) { if (SourceShot is { } shot) MoveShotInScene(shot.Id, by); }

    private void OpenShotMove(Guid id)
    {
        if (ShotListLocked || _doc.Shots.FirstOrDefault(s => s.Id == id) is not { } shot) return;
        var others = SceneShots(shot).Where(s => s.Id != id).ToList();
        if (others.Count == 0) return;
        // Default to the current position: after the previous shot, or before the next one.
        var previous = SceneNeighbour(shot, -1);
        _moveShotTarget = (previous ?? others[0]).Id; _moveShotAfter = previous is not null;
        _moveShotId = id;
    }
    private void ConfirmShotMove() { if (_moveShotId is { } id) MoveShotTo(new(id, _moveShotTarget, _moveShotAfter)); }

    private async Task DuplicateShotRow(Guid id)
    {
        if (_selected != id) await Select(id);
        if (_selected == id) await DuplicateShot();
    }
}
