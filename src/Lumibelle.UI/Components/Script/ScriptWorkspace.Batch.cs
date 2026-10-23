using lumibelle.Services;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace lumibelle.Components.Script;

public partial class ScriptWorkspace
{
    [Inject] public ScriptDraftWorkflow DraftWorkflow { get; set; } = null!;
    private string? _batchSyncNotice;
    private void BatchScriptChanged(Guid project)
    {
        if (!_disposed && project == Project.Id) _ = InvokeAsync(RefreshBatchScriptAsync);
    }
    private async Task RefreshBatchScriptAsync()
    {
        if (_session is null || _disposed) return;
        try {
            if ((await Scripts.LoadAsync(Project.Id)).Revision <= Document.Revision) return;
            await ExternalAsync(async () => {
                if (Unsaved || _session.Conflict || _session.Error is not null) {
                    _batchSyncNotice = "The batch added writing to the saved script. Your edits are still here; resolve the save conflict or reload the saved script to see the additions.";
                    return false;
                }
                await _session.LoadAsync(Project.Id);
                _batchSyncNotice = null;
                return true;
            });
        }
        catch (JSDisconnectedException) { /* Reopening the page reads the checkpoint. */ }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _batchSyncNotice = e.Message; StateHasChanged(); }
    }
}
