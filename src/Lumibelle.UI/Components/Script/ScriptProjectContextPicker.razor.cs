using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace lumibelle.Components.Script;

public sealed record ScriptReferenceSelection(string? Details, IReadOnlyList<AssetImageReference> Images);

public partial class ScriptProjectContextPicker
{
    [Inject] public IShotStore Shots { get; set; } = null!;
    [Inject] public IAssetStore AssetStore { get; set; } = null!;
    [Parameter] public Guid ProjectId { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool SupportsImages { get; set; }
    [Parameter] public EventCallback<ScriptReferenceSelection> Changed { get; set; }
    private sealed record Entry(Guid Id, string Kind, string Label, string Text, string? Thumbnail, string Category = "", AssetImageReference? Image = null)
    {
        public string Key => $"{Kind}/{Id}";
        public string Status => Image is not null ? "Image + saved description" : Kind == "Shots" ? "Shot · Saved text" : "Asset · Saved text";
    }
    private List<Entry> _entries = [], _draft = [], _applied = [];
    private AssetLibrary _library = new() { ProjectId = Guid.Empty };
    private IReadOnlyList<ShotPickerItem> _shotItems = [];
    private string _search = "", _source = "Images";
    private string? _error;
    private bool _loading, _open, _selectedTab;
    private Guid _project;
    private bool Busy => Disabled || _loading;
    private bool ImageIssue => !SupportsImages && _applied.Any(e => e.Image is not null);
    private IEnumerable<Entry> MatchingAssets => _entries.Where(e => e.Kind == "Assets" && (e.Label + " " + e.Category).Contains(_search, StringComparison.OrdinalIgnoreCase));
    private static readonly DialogOptions PickerOptions = new() { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, BackdropClick = false };

    protected override async Task OnParametersSetAsync()
    {
        if (_project == ProjectId) return;
        _project = ProjectId; _entries = []; _draft = []; _applied = []; _open = false; _error = null;
        await Changed.InvokeAsync(new(null, []));
    }
    private async Task Open()
    {
        if (Busy) return;
        _draft = [.. _applied]; _selectedTab = false;
        if (await Load()) _open = true;
    }
    private Task Close(bool visible)
    {
        if (!Busy) _open = visible;
        return Task.CompletedTask;
    }
    private async Task Refresh() => await Load();
    private async Task<bool> Load()
    {
        if (Busy) return false;
        var project = ProjectId;
        _loading = true; _error = null;
        try
        {
            var shots = await Shots.LoadAsync(project);
            var library = await AssetStore.LoadAsync(project);
            if (project != ProjectId) return false;
            _library = library;
            _shotItems = shots.Shots.Select((s, i) => {
                var takes = shots.Takes.Where(t => t.ShotId == s.Id).ToArray();
                var preview = takes.FirstOrDefault(t => t.Id == s.SelectedTakeId) ?? takes.OrderByDescending(t => t.CreatedUtc).FirstOrDefault();
                return new ShotPickerItem(s.Id, i + 1, s.Title, s.SceneId, string.IsNullOrWhiteSpace(s.SceneTitle) ? "Unassigned scene" : s.SceneTitle,
                    $"{s.Duration:0.#} s", takes.Length, preview is null ? null : $"/media/projects/{project}/takes/{preview.Id}/frames/0", s.SelectedTakeId is not null);
            }).ToArray();
            _entries = shots.Shots.Select((s, i) => new Entry(s.Id, "Shots", $"{i + 1}. {s.Title}",
                Details(("Scene", s.SceneTitle), ("Action and camera", s.Description), ("Dialogue", string.Join("\n", s.Dialogue.Select(d => $"{d.Speaker}: {d.Text}" + (string.IsNullOrWhiteSpace(d.Language) ? "" : $" ({d.Language})")))), ("Atmosphere", s.Atmosphere), ("Music", s.Music)), _shotItems[i].ThumbnailUrl)).ToList();
            _entries.AddRange(library.Assets.Select(a => new Entry(a.Id, "Assets", a.Name, AssetDetails(a),
                a.Images.FirstOrDefault(i => i.LookId is null || a.Looks.Any(l => l.Id == i.LookId && !l.Archived)) is { } image ? Url(new(a.Id, image.Id)) : null, a.Category.ToString())));
            foreach (var asset in library.Assets)
                foreach (var image in asset.Images.Where(i => i.LookId is null || asset.Looks.Any(l => l.Id == i.LookId && !l.Archived)))
                    _entries.Add(new(image.Id, "Images", ScriptReferences.Label(asset, image), ScriptReferences.Description(asset, image), Url(new(asset.Id, image.Id)), asset.Category.ToString(), new(asset.Id, image.Id)));
            // Retain unavailable choices so a refresh never silently changes what will be sent.
            var missing = _draft.Any(e => !_entries.Any(current => current.Key == e.Key));
            _draft = _draft.Select(e => _entries.FirstOrDefault(current => current.Key == e.Key) ?? e).ToList();
            if (missing) _error = "A selected reference is no longer available. Remove it or choose another before applying.";
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException)
        { _error = "Could not load project references. " + e.Message; return false; }
        finally { _loading = false; }
    }
    private void Add(Entry entry) { if (!Busy && !_draft.Any(e => e.Key == entry.Key)) _draft.Add(entry); }
    private void Remove(Entry entry) { if (!Busy) _draft.Remove(entry); }
    private void Clear() { if (!Busy) _draft.Clear(); }
    private void AddImage(AssetImageReference image)
    {
        if (_draft.Count(e => e.Image is not null) >= ScriptReferences.MaximumImages) return;
        if (_entries.FirstOrDefault(e => e.Image == image) is { } entry) Add(entry);
    }
    private void SelectShots(HashSet<Guid> ids)
    {
        if (Busy) return;
        _draft.RemoveAll(e => e.Kind == "Shots" && !ids.Contains(e.Id));
        foreach (var entry in _entries.Where(e => e.Kind == "Shots" && ids.Contains(e.Id))) Add(entry);
    }
    private void Move(int index, int offset)
    {
        if (Busy || index + offset < 0 || index + offset >= _draft.Count) return;
        (_draft[index], _draft[index + offset]) = (_draft[index + offset], _draft[index]);
    }
    private async Task Apply()
    {
        if (Busy) return;
        if (_draft.Any(e => !_entries.Any(current => current.Key == e.Key)))
        { _error = "Remove unavailable references before applying."; return; }
        _applied = [.. _draft]; _open = false; _error = null;
        var text = _applied.Where(e => e.Image is null).Select(e => e.Kind + " · " + e.Label + "\n" + e.Text).ToArray();
        await Changed.InvokeAsync(new(text.Length == 0 ? null : string.Join("\n\n", text), _applied.Where(e => e.Image is not null).Select(e => e.Image!).ToArray()));
    }
    private string Url(AssetImageReference image) => $"/media/projects/{ProjectId}/assets/{image.AssetId}/images/{image.ImageId}";
    private static string Title(Entry entry, List<Entry> entries) => entry.Image is null ? entry.Label : $"Image {entries.Where(e => e.Image is not null).ToList().IndexOf(entry) + 1} · {entry.Label}";
    private static string AssetDetails(ReferenceAsset asset) => Details(("Category", asset.Category.ToString()), ("Description", asset.Description), ("Keep consistent", asset.PreservationGuidance),
        ("Looks", string.Join("\n\n", asset.Looks.Where(l => !l.Archived).Select(l => l.Name + "\n" + Details(("Description", l.Description), ("Keep consistent", l.PreservationGuidance))))));
    private static string Details(params (string Label, string? Value)[] fields) =>
        string.Join("\n\n", fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => f.Label + ":\n" + f.Value));
}
