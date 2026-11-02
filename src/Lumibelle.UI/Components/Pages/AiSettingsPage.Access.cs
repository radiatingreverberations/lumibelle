using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    private bool _accessDialogOpen;
    private string _accessDialogServer = "";
    private string? _accessReason, _comfyCheckedUrl;
    private ComfyAccessCheck? _comfyTransportCheck;
    private lumibelle.Components.AI.ComfyAccessEditor? _accessEditor;
    // The close button and Escape follow the editor's Cancel, which waits for a running save.
    private Task CancelAccessDialogAsync() => _accessEditor is { } editor ? editor.CancelAsync() : Task.CompletedTask;
    private Task AccessDialogVisibilityAsync(bool visible) => visible ? Task.CompletedTask : CancelAccessDialogAsync();

    private Task CheckComfyWithAccessAsync() => CheckComfyWithAccessAsync(allowCredentialPrompt: true);

    private async Task CheckComfyWithAccessAsync(bool allowCredentialPrompt)
    {
        if (Busy || _settings is null || _token.IsCancellationRequested) return;
        var server = _comfyUrl.Trim();
        _checking = true; _error = null; _comfyCheck = null; _comfyTransportCheck = null;
        _comfyCheckedUrl = server;
        string? requestCredentials = null;
        try
        {
            // Keep model discovery in the normal check; /system_stats alone does
            // not exercise the /object_info route that the studios need.
            var catalog = await Providers.CheckAsync(AiBackend.ComfyUI,
                _settings with { ComfyUrl = server }, cancellationToken: _token);
            if (_token.IsCancellationRequested) return;
            _comfyCheck = catalog.Message;
            // Legacy/test hosts can omit the optional diagnostic service. Both
            // production hosts register it through AddComfyAccess.
            if (Services.GetService<IComfyAccessProbe>() is { } probe)
            {
                var transports = await probe.CheckAsync(server, _token);
                if (_token.IsCancellationRequested) return;
                _comfyTransportCheck = transports;
                if (transports.NeedsCredentials)
                    requestCredentials = transports.HttpSucceeded ? transports.WebSocketMessage : transports.HttpMessage;
            }
        }
        catch (ComfyAccessException e)
        {
            _comfyCheck = e.Message;
            if (e.NeedsCredentials) requestCredentials = e.Message;
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException)
        { _comfyCheck = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        { _comfyCheck = "The connection check could not finish. Check the network and retry."; }
        finally { _checking = false; }

        // At most one prompt per user check. The explicit save-and-retry below
        // never recursively reopens a dialog after a bad token or policy failure.
        if (!_token.IsCancellationRequested && allowCredentialPrompt && requestCredentials is not null && _comfyUrl.Trim() == server)
            OpenAccessDialog(server, requestCredentials);
    }

    private void OpenComfyAuthentication()
    {
        if (Busy) return;
        OpenAccessDialog(_comfyUrl.Trim(), null);
    }

    private void OpenAccessDialog(string server, string? reason)
    {
        if (Busy || _token.IsCancellationRequested) return;
        try
        {
            _ = ComfyAccessOrigin.FromServerUrl(server);
            if (Services.GetService<IComfyAccessCredentialStore>() is null)
                throw new ComfyAccessException("Access credential storage is unavailable in this host.");
            _accessDialogServer = server; _accessReason = reason;
            _dialogOpen = true; _accessDialogOpen = true;
        }
        catch (ComfyAccessException e) { _comfyCheck = e.Message; _comfyCheckedUrl = _comfyUrl.Trim(); }
    }

    private void CloseAccessDialog()
    {
        _accessDialogOpen = false; _dialogOpen = false; _accessReason = null;
    }

    private void AccessCredentialsChanged()
    {
        _comfyCheck = null; _comfyTransportCheck = null;
        InvalidateTextCatalog(AiBackend.ComfyUI);
    }

    private async Task<string?> SaveAccessConnectionAsync(string server)
    {
        if (_token.IsCancellationRequested) return "The settings page was closed.";
        if (!_accessDialogOpen || server != _accessDialogServer || _comfyUrl.Trim() != server)
            return "The selected server changed. Close this dialog and review the connection URL.";
        try
        {
            // PersistAsync is the page's existing serialized write path. It builds
            // from saved settings, not unrelated image/text form drafts. Do not
            // use SaveChangeAsync: Busy intentionally includes this open dialog.
            await PersistAsync(s => s with { ComfyUrl = server }, "ComfyUI connection and Access credentials saved.");
            CancelComfy();
            InvalidateTextCatalog(AiBackend.ComfyUI);
            _imageModels = []; _imageEncoders = []; _imageVaes = []; _imageEditLoras = []; _imageEditCheck = null;
            return null;
        }
        catch (WorkspaceConflictException) { return "Another window changed settings. Close this dialog, reload settings and retry; the token remains saved."; }
        catch (WorkspaceStoreException)
        {
            _error = _conflict
                ? "Another window changed AI settings. Close this dialog, reload saved settings and retry; the token remains saved."
                : "Connection settings could not be saved. Check settings storage and retry; the token remains saved.";
            return _error;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return "The settings page was closed."; }
        catch { return "Connection settings could not be saved. Check storage and retry; the token remains saved."; }
    }

    private async Task AccessConnectionSavedAsync()
    {
        CloseAccessDialog();
        if (!_token.IsCancellationRequested) await CheckComfyWithAccessAsync(allowCredentialPrompt: false);
    }
}
