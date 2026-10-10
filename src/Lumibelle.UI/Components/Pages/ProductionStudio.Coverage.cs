using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
namespace lumibelle.Components.Pages;
public partial class ProductionStudio
{
    [Inject] private ILogger<ProductionStudio> Logger { get; set; } = null!;
    private ScriptSourceSnapshot? _approved;
    private List<Shot> _baseline = []; private List<SceneReferenceSetup> _setupBaseline = [];
    private bool _coverageDirty, _planningOpen, _planning, _recoveryOpen;
    private double _maximum = 15; private string _instructions = ""; private HashSet<Guid> _sceneSelection = [];
    private TextModelSelectionState? _model; private ShotPlanningResult? _proposal; private string? _planningError;
    private ShotEditState EditState => ShotCopy.Of(new ShotEditState(_doc.Shots, _doc.SceneSetups));
    private bool SameBaseline(ShotDocument d) => Json(d.Shots) == Json(_baseline) && Json(d.SceneSetups) == Json(_setupBaseline);
    private void Baseline(ShotDocument d) { _baseline = ShotCopy.Of(d.Shots); _setupBaseline = ShotCopy.Of(d.SceneSetups); }
    private IEnumerable<ScriptSection> Scenes => _approved is null ? [] : ScriptStructure.Sections(_approved.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene);
    private string MaximumLabel => _maximum is >= 1 and <= 15 ? $"Maximum generated duration: {H3Policy.Seconds(_maximum):0.###} seconds" : "Choose a maximum from 1 to 15 seconds";
    private void MergeMedia(ShotDocument latest) { _doc.Revision = latest.Revision; _doc.Takes = latest.Takes; _doc.Trash = latest.Trash; _doc.Recovery = latest.Recovery; _doc.PlanningReviews = latest.PlanningReviews; _doc.TakePublications = latest.TakePublications; _doc.TrimPublications = latest.TrimPublications; }
    private void EditCoverage(Action<Shot> edit) { if (SourceShot is not { } shot) return; Remember(); edit(shot); CoverageChanged(); }
    private void CoverageChanged() { _coverageDirty = true; SyncCoverage(); Changed(); }
    private void SyncCoverage() { foreach (var c in _production.Compositions) if (_doc.Shots.FirstOrDefault(s => s.Id == c.ShotId) is { } s) lumibelle.Services.Production.ProductionPolicy.CopyCoverage(s, c.Shot); }
    private async Task SaveCoverageLocked()
    {
        while (_coverageDirty)
        {
            var latest = await Store.LoadAsync(Id, _lifetime.Token);
            if (!SameBaseline(latest)) throw new WorkspaceConflictException();
            var version = _version; var state = EditState;
            var saved = await Store.SaveWorkspaceAsync(Id, state.Shots, state.SceneSetups, latest.Revision, ct: _lifetime.Token);
            Baseline(saved); if (version == _version) { _doc = saved; _coverageDirty = false; } else MergeMedia(saved);
        }
        var initialized = await Production.InitializeAsync(Id, _lifetime.Token);
        foreach (var c in initialized.Compositions.Where(c => _production.Compositions.All(x => x.Id != c.Id))) _production.Compositions.Add(c);
        if (Current?.ShotId != _selected) { _compositionId = _production.Compositions.FirstOrDefault(c => c.ShotId == _selected && !c.Archived)?.Id; await ApplySelectedGlobalSetup(); _savedComposition = Current?.Copy(); }
        SyncCoverage();
    }
    private void ChangeSpeaker(Guid id, ChangeEventArgs e) => EditCoverage(s => ShotReferences.ChangeSpeaker(s, id, Text(e)));
    private void EditCast(List<ShotCharacter> characters) => EditCoverage(s => {
        var old = ShotReferences.Characters(s);
        foreach (var c in characters) if (old.FirstOrDefault(x => x.Id == c.Id) is { } before && before.Name != c.Name)
            foreach (var line in s.Dialogue.Where(d => string.Equals(d.Speaker, before.Name, StringComparison.OrdinalIgnoreCase))) line.Speaker = c.Name;
        s.Characters = characters;
        foreach (var b in s.Images.Where(b => b.RepresentsId is { } id && characters.All(c => c.Id != id))) { b.RepresentsId = null; b.Purpose = null; }
    });
    private void EditLine(Guid id, Action<ShotDialogue> edit) => EditCoverage(s => edit(s.Dialogue.Single(d => d.Id == id)));
    private static void Move<T>(List<T> list, T item, int by) { var i = list.IndexOf(item); var to = i + by; if (to < 0 || to >= list.Count) return; list.RemoveAt(i); list.Insert(to, item); }
    private void MoveLine(Guid id, int by) => EditCoverage(s => Move(s.Dialogue, s.Dialogue.Single(d => d.Id == id), by));
    private async Task AddShot()
    {
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        if (!await Save() || !await RefreshSavedSource()) return;
        var previous = SourceShot;
        _openShotView = true;
        Remember();
        var shot = new Shot();
        if (previous is null)
        {
            if (Scenes.FirstOrDefault() is { } first) SetScene(shot, first);
            _doc.Shots.Add(shot);
        }
        else
        {
            if (Scenes.FirstOrDefault(s => s.Id == previous.SceneId) is { } scene) SetScene(shot, scene);
            _doc.Shots.Insert(_doc.Shots.IndexOf(previous) + 1, shot);
        }
        _filter = "";
        _promptStatusFilter = "all";
        _collapsedScenes.Remove(SceneKey(shot));
        _selected = shot.Id;
        _compositionId = null;
        _savedComposition = null;
        CoverageChanged();
    }
    private async Task DuplicateShot() { if (_promptEditor is not null) await _promptEditor.FlushAsync(); if (SourceShot is null || !await Save()) return; _openShotView = true; Remember(); _coverageDirty = true; var s = lumibelle.Services.Production.ProductionPolicy.CoverageCopy(SourceShot); s.Id = Guid.NewGuid(); s.Title += " (copy)"; s.SelectedTakeId = null; _doc.Shots.Insert(_doc.Shots.IndexOf(SourceShot) + 1, s); _selected = s.Id; _compositionId = null; _savedComposition = null; CoverageChanged(); }
    private void SetScene(Shot shot, ScriptSection scene) { shot.SceneId = scene.Id; shot.SceneTitle = scene.Title; shot.ApprovedScriptId = _approved!.Id; shot.SourceBlockIds = _approved.Blocks.Skip(scene.Start).Take(scene.Count).Select(b => b.Id).ToList(); shot.SourceExcerpt = ScriptStructure.Markdown(_approved.Blocks.Skip(scene.Start).Take(scene.Count)); }
    // A shot keeps the script lines it was made from. Saving the script elsewhere does not concern it;
    // only a change to its own lines does, and then the person checks the shot and marks it checked.
    private string? CurrentSourceExcerpt(Shot shot) => _approved is null || shot.SourceBlockIds.Count == 0 || !_approved.Blocks.Any(b => shot.SourceBlockIds.Contains(b.Id))
        ? null : ScriptStructure.Markdown(_approved.Blocks.Where(b => shot.SourceBlockIds.Contains(b.Id)));
    private bool SourceLinesRemoved(Shot shot) => _approved is not null && shot.ApprovedScriptId != _approved.Id && shot.SourceBlockIds.Count > 0 && CurrentSourceExcerpt(shot) is null;
    private bool SourceLinesChanged(Shot shot) => _approved is not null && shot.ApprovedScriptId != _approved.Id && CurrentSourceExcerpt(shot) is { } now && now != shot.SourceExcerpt;
    private async Task MarkSourceChecked()
    {
        if (!await RefreshSavedSource()) return;
        EditCoverage(s =>
        {
            if (CurrentSourceExcerpt(s) is not { } now) return;
            s.SourceBlockIds = s.SourceBlockIds.Where(id => _approved!.Blocks.Any(b => b.Id == id)).ToList();
            s.SourceExcerpt = now; s.ApprovedScriptId = _approved!.Id;
        });
    }
    private async Task<bool> RefreshSavedSource()
    {
        try { _approved = await Scripts.CaptureSourceAsync(Id, cancellationToken: _lifetime.Token); return true; }
        catch (WorkspaceStoreException e) { _error = e.Message; return false; }
    }
    private async Task AssignScene(ChangeEventArgs e)
    {
        if (string.IsNullOrEmpty(Text(e)))
        {
            EditCoverage(s => { s.SceneId = null; s.SceneTitle = ""; s.ApprovedScriptId = null; s.SourceBlockIds = []; s.SourceExcerpt = ""; });
            return;
        }
        if (!Guid.TryParse(Text(e), out var id) || !await RefreshSavedSource()) return;
        if (Scenes.FirstOrDefault(s => s.Id == id) is { } scene) EditCoverage(s => SetScene(s, scene));
        else _error = "That scene has been removed from the saved script. Choose another scene.";
    }
    private void DurationChanged(ChangeEventArgs e) { if (double.TryParse(Text(e), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 15) EditCoverage(s => s.Duration = n); else _error = "Choose a duration from 1 to 15 seconds."; }
    private async Task Recover(Guid id)
    {
        if (!await Save()) return;
        try { _doc = await Store.RecoverAsync(Id, id, _doc.Revision, _lifetime.Token); Baseline(_doc); _recoveryOpen = false; Notify("Shot coverage restored. Setups are available with their restored shots."); }
        catch (Exception e) { _error = e.Message; }
    }
}
