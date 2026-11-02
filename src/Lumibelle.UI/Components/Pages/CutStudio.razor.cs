using System.Globalization;
using System.Text.Json;
using lumibelle.Components.Layout;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Story;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class CutStudio
{
    [Parameter] public Guid Id { get; set; }
    private ProjectInfo? _project;
    private CutDocument _doc = new();
    private ShotDocument _shots = new();
    private Guid? _selected;
    private string _saveStatus = SaveStatusText.Saved;
    private string? _error, _trimError, _chooserError;
    private bool _dirty, _saving, _exporting, _disposed, _interactive, _chooserOpen, _reloadOpen, _adding, _refreshing, _swapping;
    // Retry, download and reload only help after a failed save that retained the draft.
    private bool _saveFailed;
    // The cut or its takes could not be opened; the studio is replaced by LoadFailure.
    private Exception? _loadFailure;
    private int _version, _previewSelection;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _saveDelay;
    private readonly Stack<List<CutClip>> _undo = [], _redo = [];
    private readonly Dictionary<Guid, Guid> _choices = [];
    private readonly Dictionary<Guid, Guid> _choiceTargets = [];
    private string _latestResolution = "";
    private string _latestLanguage = "";
    private int ChoiceChanges => _choices.Count(c => IsChoiceChange(c.Key, c.Value));
    private bool IsChoiceChange(Guid shot, Guid take) => !_doc.Clips.Any(c => c.Id == _choiceTargets.GetValueOrDefault(shot) && c.TakeId == take);
    private ShotTake[] TakeChoices(CutClip clip) => _shots.Takes.Where(t => t.ShotId == (Take(clip)?.ShotId ?? clip.ShotId)).OrderBy(t => t.CreatedUtc).ToArray();
    private ElementReference _addButton;
    private bool _restoreFocus;
    private CutPlayer? _sequence;
    private CutClip? Selected => _doc.Clips.FirstOrDefault(c => c.Id == _selected);
    private Guid[] UnavailableTakes => _doc.Clips.Where(c => Issue(c) is not null).Select(c => c.TakeId).Distinct().ToArray();
    private static readonly RenderFragment ChooserSummary = builder => builder.AddContent(0, "Swap existing clips in place, or add missing shots in shot order. Swapping resets the clip's trim to the full take.");
    private static readonly RenderFragment ExportIntro = builder => builder.AddContent(0, "Export the whole cut, or part of it, such as everything from the first clip of a new act.");
    private static string Time(double seconds) => $"{seconds:0.###} s";
    private static string Label(ShotTake take) => TakeDisplay.Label(take);
    private ShotTake? Take(CutClip c) => _shots.Takes.FirstOrDefault(t => t.Id == c.TakeId);
    private string? Issue(CutClip c) => Take(c) is null ? _shots.Trash.Any(t => t.Take?.Id == c.TakeId) ? "Take is in Trash." : "Take is unavailable." : null;

    protected override async Task OnParametersSetAsync()
    {
        if (_doc.ProjectId == Id) return;
        CancelExport(); _completedExport = null; _exportError = _exportStatus = null;
        await StopSequence();
        _project = null;
        var projectId = Id;
        try { var project = await Projects.GetAsync(projectId, _lifetime.Token); if (_disposed || Id != projectId) return; _project = project; await Reload(); }
        catch (Exception e) { _error = e.Message; }
    }
    private Task RetryLoadAsync() => Reload();
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_doc.ProjectId == Id) await Remember(Id, "cut", "clip", _selected);
        if (firstRender) { _interactive = true; StateHasChanged(); }
        if (_restoreFocus) { _restoreFocus = false; try { await _addButton.FocusAsync(); } catch (JSDisconnectedException) { } }
    }
    private async Task Reload()
    {
        var projectId = Id;
        _saveDelay?.Cancel();
        await StopSequence();
        await _saveGate.WaitAsync();
        try
        {
            _loadFailure = null;
            var document = await Store.LoadAsync(projectId, _lifetime.Token);
            var shots = await Shots.LoadAsync(projectId, _lifetime.Token);
            if (_disposed || Id != projectId) return;
            _doc = document; _shots = shots; _selected = _doc.Clips.FirstOrDefault(c => c.Id == Place<Guid?>(Id, "cut", "clip"))?.Id ?? _doc.Clips.FirstOrDefault()?.Id;
            _undo.Clear(); _redo.Clear(); _choices.Clear(); _version++;
            _dirty = false; _saveStatus = SaveStatusText.Saved; _error = _trimError = null; _saveFailed = false; _reloadOpen = _chooserOpen = false;
        }
        catch (Exception e) when (e is WorkspaceStoreException or lumibelle.Services.ProjectStoreException) { _loadFailure = e; }
        catch (Exception e) { _error = e.Message; }
        finally { _saveGate.Release(); }
    }
    private async Task RefreshTakes()
    {
        if (_refreshing) return;
        _refreshing = true;
        var projectId = Id;
        try { await StopSequence(); var shots = await Shots.LoadAsync(projectId, _lifetime.Token); if (_disposed || Id != projectId) return; _shots = shots; }
        catch (Exception e) { _error = e.Message; }
        finally { _refreshing = false; }
    }
    private async Task<bool> Save()
    {
        _saveDelay?.Cancel();
        await _saveGate.WaitAsync();
        try
        {
            if (!_dirty || _disposed) return !_dirty;
            _saving = true;
            while (_dirty && !_disposed)
            {
                var projectId = _doc.ProjectId; var version = _version; var clips = ShotCopy.Of(_doc.Clips);
                _saveStatus = SaveStatusText.Saving; StateHasChanged();
                var saved = await Store.SaveAsync(projectId, clips, _doc.Revision, _lifetime.Token);
                if (_disposed || _doc.ProjectId != projectId) return false;
                // Acknowledgements only advance metadata; they must never replace newer edits.
                _doc.Revision = saved.Revision; _doc.UpdatedUtc = saved.UpdatedUtc;
                if (version == _version) _dirty = false;
            }
            _saveStatus = SaveStatusText.Saved; _error = null; _saveFailed = false; return true;
        }
        catch (Exception e) when (!_disposed)
        { _error = e.Message; _saveFailed = true; _saveStatus = e is WorkspaceConflictException ? SaveStatusText.Conflict : SaveStatusText.Failed; return false; }
        catch (OperationCanceledException) { return false; }
        finally { _saving = false; _saveGate.Release(); }
    }
    private void Changed()
    {
        _dirty = true; _version++; _saveStatus = SaveStatusText.Unsaved;
        _saveDelay?.Cancel(); _saveDelay?.Dispose(); _saveDelay = new();
        _ = DelayedSave(_saveDelay.Token);
    }
    private async Task DelayedSave(CancellationToken ct)
    {
        try { await Task.Delay(800, ct); await InvokeAsync(async () => { if (!_disposed) { await Save(); StateHasChanged(); } }); }
        catch (OperationCanceledException) { }
    }
    private async Task Mutate(Action action)
    {
        await StopSequence();
        Remember(); _redo.Clear();
        action(); _trimError = null; Changed();
    }
    private void Remember()
    {
        if (_undo.Count >= 40) { var keep = _undo.Take(39).Reverse().ToArray(); _undo.Clear(); foreach (var item in keep) _undo.Push(item); }
        _undo.Push(ShotCopy.Of(_doc.Clips));
    }
    private async Task Undo()
    {
        if (_undo.Count == 0) return;
        await StopSequence();
        _redo.Push(ShotCopy.Of(_doc.Clips)); _doc.Clips = _undo.Pop(); EnsureSelection(); Changed();
    }
    private async Task Redo()
    {
        if (_redo.Count == 0) return;
        await StopSequence();
        Remember(); _doc.Clips = _redo.Pop(); EnsureSelection(); Changed();
    }
    private void EnsureSelection() { if (Selected is null) _selected = _doc.Clips.FirstOrDefault()?.Id; _trimError = null; }
    private void Select(Guid id) { if (_doc.Clips.Any(c => c.Id == id)) { _selected = id; _trimError = null; } }
    private async Task Move(Guid id, int delta)
    {
        var index = _doc.Clips.FindIndex(c => c.Id == id); var target = index + delta;
        if (index < 0 || target < 0 || target >= _doc.Clips.Count) return;
        await Mutate(() => { var clip = _doc.Clips[index]; _doc.Clips.RemoveAt(index); _doc.Clips.Insert(target, clip); });
    }
    private async Task Remove(Guid id)
    {
        var index = _doc.Clips.FindIndex(c => c.Id == id); if (index < 0) return;
        await Mutate(() => { _doc.Clips.RemoveAt(index); if (_selected == id) _selected = _doc.Clips.ElementAtOrDefault(Math.Min(index, _doc.Clips.Count - 1))?.Id; });
        _restoreFocus = true;
    }
    private async Task OpenChooser()
    {
        await RefreshTakes(); _choices.Clear(); _choiceTargets.Clear(); _chooserError = null;
        _latestResolution = ""; _latestLanguage = "";
        foreach (var shot in _shots.Shots)
        {
            var existing = _doc.Clips.FirstOrDefault(c => c.ShotId == shot.Id && c.Id == _selected) ?? _doc.Clips.FirstOrDefault(c => c.ShotId == shot.Id);
            if (existing is not null) { _choiceTargets[shot.Id] = existing.Id; _choices[shot.Id] = existing.TakeId; }
            else if (shot.SelectedTakeId is { } id && _shots.Takes.Any(t => t.Id == id && t.ShotId == shot.Id)) _choices[shot.Id] = id;
        }
        _chooserOpen = true;
    }
    private void ChooseLatest()
    {
        if (_adding || !_chooserOpen) return;
        var latest = CutTakeSelection.LatestByShot(_shots, _latestResolution, _latestLanguage);
        // Replace the proposal, not the cut. In particular, do not leave seeded
        // production selections or earlier bulk choices on shots without a match.
        _choices.Clear();
        foreach (var (shot, take) in latest) _choices[shot] = take;
        _chooserError = null;
    }
    private string ChoiceValue(Guid shot) => _choices.TryGetValue(shot, out var take) ? take.ToString() : "";
    private void Choose(Guid shot, ChangeEventArgs e) { if (Guid.TryParse(e.Value?.ToString(), out var id)) _choices[shot] = id; else _choices.Remove(shot); }
    private void ChooseTarget(Guid shot, ChangeEventArgs e)
    {
        if (!Guid.TryParse(e.Value?.ToString(), out var id)) return;
        _choiceTargets[shot] = id;
        if (_doc.Clips.FirstOrDefault(c => c.Id == id && c.ShotId == shot) is { } clip) _choices[shot] = clip.TakeId;
    }
    private void ChooserVisibility(bool value) { _chooserOpen = value; if (!value) _restoreFocus = true; }
    private async Task AddChosen()
    {
        if (_adding || ChoiceChanges == 0) return;
        _adding = true;
        var projectId = Id; var version = _version;
        var choices = _choices.Where(c => IsChoiceChange(c.Key, c.Value)).ToDictionary();
        var targets = new Dictionary<Guid, Guid>(_choiceTargets);
        try
        {
            var latest = await Shots.LoadAsync(projectId, _lifetime.Token);
            if (_disposed || Id != projectId || !_chooserOpen) return;
            if (version != _version) throw new WorkspaceStoreException("The cut changed. Close and reopen the chooser to review it.");
            var entries = new List<CutClip>();
            foreach (var shot in _shots.Shots.Where(s => choices.ContainsKey(s.Id)))
            {
                var current = latest.Shots.SingleOrDefault(s => s.Id == shot.Id);
                var take = latest.Takes.SingleOrDefault(t => t.Id == choices[shot.Id] && t.ShotId == shot.Id);
                if (current is null || take is null) throw new WorkspaceStoreException("A selected take is no longer available. Close and reopen the chooser to refresh it.");
                var entry = CutClip.From(current, take);
                if (targets.GetValueOrDefault(shot.Id) is var target && target != Guid.Empty)
                {
                    if (!_doc.Clips.Any(c => c.Id == target && c.ShotId == shot.Id)) throw new WorkspaceStoreException("The clip to replace is no longer in the cut.");
                    entry.Id = target;
                }
                entries.Add(entry);
            }
            _shots = latest;
            await Mutate(() =>
            {
                foreach (var entry in entries)
                {
                    var replace = _doc.Clips.FindIndex(c => c.Id == entry.Id);
                    if (replace >= 0) _doc.Clips[replace] = entry;
                    else InsertInShotOrder(entry);
                }
                _selected = entries.FirstOrDefault()?.Id ?? _selected;
            });
            ChooserVisibility(false);
            _previewSelection++;
        }
        catch (Exception e) { _chooserError = e.Message; }
        finally { _adding = false; }
    }
    private void InsertInShotOrder(CutClip entry)
    {
        var order = _shots.Shots.FindIndex(s => s.Id == entry.ShotId);
        var next = _doc.Clips.FindIndex(c => _shots.Shots.FindIndex(s => s.Id == c.ShotId) > order);
        _doc.Clips.Insert(next < 0 ? _doc.Clips.Count : next, entry);
    }
    private Task ReplaceTake(ChangeEventArgs e) => Guid.TryParse(e.Value?.ToString(), out var id) ? ReplaceTake(id) : Task.CompletedTask;
    private Task CycleTake(int delta)
    {
        if (Selected is not { } clip) return Task.CompletedTask;
        var choices = TakeChoices(clip); var index = Array.FindIndex(choices, t => t.Id == clip.TakeId) + delta;
        return index >= 0 && index < choices.Length ? ReplaceTake(choices[index].Id) : Task.CompletedTask;
    }
    private async Task ReplaceTake(Guid id)
    {
        if (_swapping || Selected is not { } clip || id == clip.TakeId) return;
        var take = TakeChoices(clip).FirstOrDefault(t => t.Id == id);
        var shot = _shots.Shots.FirstOrDefault(s => s.Id == take?.ShotId);
        if (take is null || shot is null) return;
        _swapping = true;
        try
        {
            await Mutate(() => { var replacement = CutClip.From(shot, take); replacement.Id = clip.Id; _doc.Clips[_doc.Clips.IndexOf(clip)] = replacement; });
            _previewSelection++;
        }
        finally { _swapping = false; }
    }
    private async Task SetFrame(ChangeEventArgs e, bool start)
    {
        if (Selected is not { } clip) return;
        if (!int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)) { _trimError = "Enter a whole frame number."; return; }
        await Trim(clip.Id, start ? frame - 1 : clip.StartFrame, start ? clip.EndFrameExclusive : frame);
    }
    private async Task Trim(Guid id, int start, int end)
    {
        var clip = _doc.Clips.FirstOrDefault(c => c.Id == id); if (clip is null) return;
        if (start < 0 || end > clip.FrameCount || start >= end) { _trimError = $"Choose at least one frame, from 1 to {clip.FrameCount}. Start must not be after end."; return; }
        if (clip.StartFrame == start && clip.EndFrameExclusive == end) { _trimError = null; return; }
        await Mutate(() => { clip.StartFrame = start; clip.EndFrameExclusive = end; });
    }
    private Task ResetTrim() => Selected is { } clip ? Trim(clip.Id, 0, clip.FrameCount) : Task.CompletedTask;
    private async Task GestureTrim(CutTrimEdit edit)
    {
        if (edit.Version != _version) { _trimError = "The cut changed during this drag. Try again."; return; }
        await Trim(edit.ClipId, edit.Start, edit.End);
    }
    private async Task GestureOrder(CutOrderEdit edit)
    {
        if (edit.Version != _version) { _trimError = "The cut changed during this drag. Try again."; return; }
        var clip = _doc.Clips.FirstOrDefault(c => c.Id == edit.ClipId);
        if (clip is null || edit.BeforeId == clip.Id || (edit.BeforeId is not null && !_doc.Clips.Any(c => c.Id == edit.BeforeId))) return;
        var next = _doc.Clips.Where(c => c.Id != clip.Id).ToList();
        var index = edit.BeforeId is { } before ? next.FindIndex(c => c.Id == before) : next.Count;
        next.Insert(index, clip);
        if (next.Select(c => c.Id).SequenceEqual(_doc.Clips.Select(c => c.Id))) return;
        await Mutate(() => _doc.Clips = next);
    }
    private async Task StopSequence()
    {
        if (_sequence is not null) { try { await _sequence.PauseAsync(); } catch (JSDisconnectedException) { } }
    }
    private async Task BeforeNavigation(LocationChangingContext e)
    {
        if (_dirty && !await Save()) { e.PreventNavigation(); return; }
        CancelExport();
        await StopSequence();
    }
    private async Task DownloadDraft() => await JS.InvokeVoidAsync("lumibelleShots.download", "unsaved-cut.json", JsonSerializer.Serialize(_doc, AtomicJsonFile.Options));
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _saveDelay?.Cancel(); _saveDelay?.Dispose(); _lifetime.Cancel();
        await StopSequence(); _lifetime.Dispose();
    }
}
