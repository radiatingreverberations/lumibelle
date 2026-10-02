using lumibelle.Components.Shots;
using lumibelle.Models;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private IReadOnlyList<ShotPickerItem> BulkPickerItems => _doc.Shots.Select((shot, index) =>
        PickerItem(_doc, shot, index, _bulkGenerateTargets.FirstOrDefault(t => t.ShotId == shot.Id)?.Issue)).ToArray();

    private IReadOnlyList<ShotPickerItem> DeletePickerItems => _deleteSnapshot is { } snapshot
        ? snapshot.Shots.Select((shot, index) => PickerItem(snapshot, shot, index)).ToArray() : [];

    private ShotPickerItem PickerItem(ShotDocument document, Shot shot, int index, string? issue = null)
    {
        var takes = document.Takes.Where(t => t.ShotId == shot.Id).ToArray();
        var preview = takes.FirstOrDefault(t => t.Id == shot.SelectedTakeId)
            ?? takes.Where(t => t.Snapshot.Dub is null).OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).FirstOrDefault()
            ?? takes.OrderByDescending(t => t.CreatedUtc).ThenByDescending(t => t.Id).FirstOrDefault();
        return new(shot.Id, index + 1, string.IsNullOrWhiteSpace(shot.Title) ? "Untitled shot" : shot.Title,
            shot.SceneId, SceneTitle(shot) ?? "Unassigned scene",
            DurationLabel(shot), takes.Length, preview is null ? null : TakeUrl(preview.Id, 0), shot.SelectedTakeId is not null, issue);
    }

    private void SetBulkSelection(HashSet<Guid> selected)
    {
        if (BulkGenerateLocked) return;
        _bulkGenerateSelected = selected.Intersect(_bulkGenerateTargets.Where(t => t.Eligible).Select(t => t.ShotId)).ToHashSet();
    }

    private void SetDeleteSelection(HashSet<Guid> selected)
    {
        if (_deletingShots || _deleteSnapshot is null) return;
        _deleteShotIds = selected.Intersect(_deleteSnapshot.Shots.Select(s => s.Id)).ToHashSet();
        _clearProductionSelections = false;
    }
}
