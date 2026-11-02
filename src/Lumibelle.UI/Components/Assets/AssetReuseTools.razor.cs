using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace lumibelle.Components.Assets;

public partial class AssetReuseTools
{
    [Inject] public IAssetStore Store { get; set; } = null!;
    [Inject] public IProjectStore Projects { get; set; } = null!;
    [Inject] public IJSRuntime JS { get; set; } = null!;
    [Parameter, EditorRequired] public AssetLibrary Library { get; set; } = null!;
    [Parameter] public Guid? AssetId { get; set; }
    [Parameter] public Guid? MediaId { get; set; }
    [Parameter] public string? MediaKind { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool ClipboardDirty { get; set; }
    [Parameter, EditorRequired] public Func<Task<AssetLibrary?>> Prepare { get; set; } = null!;
    [Parameter] public EventCallback<AssetReuseResult> Changed { get; set; }
    private IAssetReuseStore? Reuse => Store as IAssetReuseStore;
    private bool _busy, _disposed, _transferOpen, _publishOpen, _sharedOpen, _targetLoading;
    private bool Blocked => Disabled || _busy || _pending is not null || _pendingPublish is not null;
    private string? _error, _notice;
    private string _name = "", _mode = "copy", _targetAsset = "", _sharedSearch = "", _capturedFingerprint = "";
    private Guid _targetProject;
    private IReadOnlyList<ProjectInfo> _projects = [];
    private IReadOnlyList<SharedAssetEntry> _shared = [];
    private AssetLibrary? _targetLibrary;
    private AssetReuseSelection? _capturedSource;
    private AssetCategory _capturedCategory;
    private AssetReuseCommand? _pending;
    private (Guid Id, AssetReuseSelection Source, string Fingerprint, string Name)? _pendingPublish;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private CancellationToken Token => _operationCancellation?.Token ?? _lifetime.Token;
    private void CancelTransfer() => _operationCancellation?.Cancel();
    private ElementReference _element;
    private IJSObjectReference? _module;
    private DotNetObjectReference<AssetReuseTools>? _reference;
    private bool _attached;
    private AssetReuseSelection? Source => AssetId is not { } asset || Library.Assets.All(a => a.Id != asset) ? null :
        MediaId is { } media && Enum.TryParse<AssetReuseKind>(MediaKind, out var kind) && kind != AssetReuseKind.Asset
            ? new(Library.ProjectId, asset, kind, media) : new(Library.ProjectId, asset);
    private string DestinationKey => Library.ProjectId.ToString("D") + "/" + (AssetId?.ToString("D") ?? "new");
    private IEnumerable<ReferenceAsset> CompatibleTargets => (_targetLibrary?.Assets ?? []).Where(a =>
        _capturedSource?.Kind == AssetReuseKind.Reel ? a.Category == _capturedCategory :
        _capturedSource?.Kind != AssetReuseKind.Voice || a.Category == AssetCategory.Character);
    private Task<bool> CanCloseAsync() => Task.FromResult(!_busy && !_targetLoading);
    private async Task<AssetLibrary> ReadyAsync(Guid project)
    {
        var saved = await Prepare();
        if (saved is null || saved.ProjectId != project || _disposed) throw new WorkspaceStoreException("Save the current asset edits before reusing media.");
        return saved;
    }
    private static string SourceName(AssetReuseContent content)
    {
        var name = content.Source.Kind switch {
            AssetReuseKind.Image => content.Asset.Images.Single().Name,
            AssetReuseKind.Reel => content.Reels.Single().Name,
            AssetReuseKind.Voice => content.Voices.Single().Name,
            _ => content.Asset.Name
        };
        if (string.IsNullOrWhiteSpace(name)) name = content.Asset.Name + " " + content.Source.Kind.ToString().ToLowerInvariant();
        return name[..Math.Min(name.Length, 240)];
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true; _error = null; _notice = null;
        await InvokeAsync(StateHasChanged);
        try { await action(); }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        { _notice = "Transfer cancelled. Any already-saved destination copy is retained; retry uses the same copy identity."; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException or UnauthorizedAccessException or JSException or InvalidOperationException)
        { _error = e.Message; }
        finally { _operationCancellation?.Dispose(); _operationCancellation = null; _busy = false; if (!_disposed) await InvokeAsync(StateHasChanged); }
    }
    private async Task ExecutePendingAsync()
    {
        if (_pending is not { } command || Reuse is null) return;
        var result = await Reuse.ReuseAsync(command, Token);
        // Keep the same command for a partial move. A retry must never create a second destination copy.
        _transferOpen = _sharedOpen = false;
        _notice = result.Notice ?? (command.Move ? "Moved to the destination project." : "Independent copy saved.");
        await Changed.InvokeAsync(result);
        if (!command.Move || result.SourceRemoved || !result.Available) _pending = null;
    }
    private Task RetryAsync() => RunAsync(ExecutePendingAsync);
    private void DismissPending() { _pending = null; _error = null; }
    private Task DuplicateAsync(bool whole) => RunAsync(async () => {
        var source = whole && AssetId is { } asset ? new AssetReuseSelection(Library.ProjectId, asset) : Source;
        if (source is null) return;
        var saved = await ReadyAsync(source.ProjectId); var content = AssetReusePolicy.Capture(saved, source);
        var name = SourceName(content); name = name[..Math.Min(name.Length, 235)] + " copy";
        _pending = new(Guid.NewGuid(), source, null, AssetReusePolicy.Hash(content),
            new(saved.ProjectId, name, whole ? null : source.AssetId));
        await ExecutePendingAsync();
    });
    private async Task CaptureSourceAsync()
    {
        var source = Source ?? throw new WorkspaceStoreException("Select an asset or media item first.");
        var saved = await ReadyAsync(source.ProjectId); var content = AssetReusePolicy.Capture(saved, source);
        _capturedSource = source; _capturedFingerprint = AssetReusePolicy.Hash(content);
        _capturedCategory = content.Asset.Category; _name = SourceName(content);
    }
    private Task OpenTransferAsync() => RunAsync(async () => {
        await CaptureSourceAsync();
        var projects = await Projects.ListAsync(Token); _projects = projects.Projects;
        _targetProject = _projects.FirstOrDefault(p => p.Id != Library.ProjectId)?.Id ?? Library.ProjectId;
        _mode = "copy"; _targetAsset = "";
        _targetLibrary = await Store.LoadAsync(_targetProject, Token); _transferOpen = true;
    });
    private async Task TargetChangedAsync(ChangeEventArgs args)
    {
        if (!Guid.TryParse(args.Value?.ToString(), out var id)) return;
        _targetProject = id; _targetAsset = ""; _targetLibrary = null; _targetLoading = true;
        try { var library = await Store.LoadAsync(id, Token); if (_targetProject == id) _targetLibrary = library; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { if (_targetProject == id) _error = e.Message; }
        finally { if (_targetProject == id) _targetLoading = false; }
    }
    private Task ConfirmTransferAsync() => RunAsync(async () => {
        if (_capturedSource is null || _targetLoading || _pending is not null) return;
        var target = Guid.TryParse(_targetAsset, out var id) ? id : (Guid?)null;
        _pending = new(Guid.NewGuid(), _capturedSource, null, _capturedFingerprint, new(_targetProject, _name, target), _mode == "move");
        await ExecutePendingAsync();
    });
    private Task OpenPublishAsync() => RunAsync(async () => { await CaptureSourceAsync(); _publishOpen = true; });
    private Task ConfirmPublishAsync() => RunAsync(async () => {
        if (_capturedSource is null || _pendingPublish is not null) return;
        _pendingPublish = (Guid.NewGuid(), _capturedSource, _capturedFingerprint, _name.Trim());
        await PublishPendingAsync();
    });
    private Task RetryPublishAsync() => RunAsync(PublishPendingAsync);
    private async Task PublishPendingAsync()
    {
        if (_pendingPublish is not { } pending || Reuse is null) return;
        await Reuse.PublishSharedAsync(pending.Id, pending.Source, pending.Fingerprint, pending.Name, Token);
        _pendingPublish = null; _publishOpen = false; _notice = "Shared snapshot published. It is independent of the source project.";
    }
    private Task OpenSharedAsync() => RunAsync(async () => { _shared = await Reuse!.ListSharedAsync(Token); _sharedOpen = true; });
    private Task UseSharedAsync(SharedAssetEntry entry) => RunAsync(async () => {
        var project = Library.ProjectId; var assetId = AssetId;
        var saved = await ReadyAsync(project);
        var target = saved.Assets.FirstOrDefault(a => a.Id == assetId);
        var compatible = target is not null && (entry.Source.Kind == AssetReuseKind.Image ||
            entry.Source.Kind == AssetReuseKind.Reel && target.Category == entry.Category || entry.Source.Kind == AssetReuseKind.Voice && target.Category == AssetCategory.Character);
        _pending = new(Guid.NewGuid(), null, entry.Id, entry.SourceFingerprint, new(project, entry.Name, compatible ? assetId : null));
        await ExecutePendingAsync();
    });
    [JSInvokable]
    public Task PasteAssetReferenceAsync(string text, string destinationKey) => RunAsync(async () => {
        if (Disabled || _pending is not null || _pendingPublish is not null || destinationKey != DestinationKey)
            throw new WorkspaceStoreException("The paste destination changed or is busy. Paste again into the intended asset.");
        var item = AssetReusePolicy.ParseClipboard(text); var project = Library.ProjectId; var asset = AssetId;
        await ReadyAsync(project);
        // Read names only from this workspace's validated source, never from arbitrary clipboard paths/URLs.
        var library = await Store.LoadAsync(item.Source.ProjectId, Token);
        var content = AssetReusePolicy.Capture(library, item.Source);
        if (AssetReusePolicy.Hash(content) != item.Fingerprint) { _pending = null; throw new WorkspaceStoreException("The copied item changed. Copy it again."); }
        var name = SourceName(content); name = name[..Math.Min(name.Length, 240)];
        _pending = new(Guid.NewGuid(), item.Source, null, item.Fingerprint,
            new(project, name, item.Source.Kind == AssetReuseKind.Asset ? null : asset));
        await ExecutePendingAsync();
    });
    [JSInvokable]
    public async Task PasteImageAsync(IJSStreamReference image, string destinationKey)
    {
        await using (image)
        {
            await RunAsync(async () => {
                if (Disabled || _pending is not null || _pendingPublish is not null || destinationKey != DestinationKey)
                    throw new WorkspaceStoreException("The paste destination changed or is busy. Paste again into the intended asset.");
                if (image.Length <= 0 || image.Length > FileAssetStore.MaximumImageBytes)
                    throw new WorkspaceStoreException("Paste a PNG, JPEG or WebP image no larger than 25 MB.");
                var project = Library.ProjectId; var assetId = AssetId;
                var saved = await ReadyAsync(project);
                if (assetId is null) throw new WorkspaceStoreException("Create or select an asset before pasting an external image.");
                await using var stream = await image.OpenReadStreamAsync(FileAssetStore.MaximumImageBytes, Token);
                var updated = await Store.AddImageAsync(project, assetId.Value, stream, new("clipboard.png", [], AssetImageOrigin.Imported), saved.Revision, Token);
                var added = updated.Assets.Single(a => a.Id == assetId).Images.Last();
                _notice = "Clipboard image imported. The original pixels are retained; use Crop image for a separate cropped copy.";
                await Changed.InvokeAsync(new(updated, assetId.Value, added.Id, true));
            });
        }
    }
    [JSInvokable]
    public Task ClipboardStatusAsync(string message)
    { _notice = message.Length > 1000 ? message[..1000] : message; return InvokeAsync(StateHasChanged); }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || Reuse is null) return;
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", lumibelle.UiAssets.Module("asset-clipboard.js"));
            _reference ??= DotNetObjectReference.Create(this);
            if (!_attached) { await _module.InvokeVoidAsync("attach", _element, _reference); _attached = true; }
            string? token = null;
            if (!ClipboardDirty && Source is { } source)
                try { token = AssetReusePolicy.Clipboard(Library, source); } catch (WorkspaceStoreException) { }
            var url = Source is { Kind: AssetReuseKind.Image } selection
                ? $"/media/projects/{selection.ProjectId}/assets/{selection.AssetId}/images/{selection.MediaId}" : null;
            await _module.InvokeVoidAsync("update", _element, new { token, imageUrl = url, destinationKey = DestinationKey,
                disabled = Blocked || _transferOpen || _publishOpen || _sharedOpen, canPasteImage = AssetId is not null });
        }
        catch (JSDisconnectedException) { }
        catch (JSException e) { if (_error is null) { _error = "Clipboard integration could not start: " + e.Message; await InvokeAsync(StateHasChanged); } }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _lifetime.Cancel();
        if (_module is not null) try { await _module.InvokeVoidAsync("detach", _element); await _module.DisposeAsync(); } catch (JSDisconnectedException) { }
        _reference?.Dispose(); _lifetime.Dispose();
    }
}
