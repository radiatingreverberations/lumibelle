using lumibelle.Models;
using lumibelle.Services.Shots;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private Guid? _trimmingTake;
    private CancellationTokenSource? _trimCancellation;
    private string? _trimProgress, _trimError;
    private async Task OpenTrim(ShotTake take) {
        if (_trimCancellation is not null) return;
        if (!_reviewOpen || ReviewTake?.Id != take.Id) await OpenTake(take);
        _trimmingTake = take.Id; _trimError = _trimProgress = null;
        _frameDestination = null; _continueFrame = null; _refiningTake = null;
    }
    private void CloseTrim() {
        if (_trimCancellation is not null) { _trimCancellation.Cancel(); return; }
        _trimmingTake = null; _restoreFrameFocus = true;
    }
    private async Task SaveTrim(TakeTrimRequest request) {
        if (_trimCancellation is not null) return;
        _trimCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _trimError = null;
        try {
            if (!await Save()) return;
            var latest = await Store.LoadAsync(Id, _trimCancellation.Token);
            _doc = await Store.TrimTakeAsync(Id, request, latest.Revision,
                new Progress<string>(message => { _trimProgress = message; _ = InvokeAsync(StateHasChanged); }), _trimCancellation.Token);
            _trimmingTake = null; _trimProgress = null;
            _reviewTakeId = request.ResultId;
            Notify("Trimmed version saved. Review it, then choose Use this take when ready.");
        }
        catch (OperationCanceledException) { _trimProgress = null; _trimError = "Saving cancelled. Your selected range is retained."; }
        catch (Exception e) { _trimError = e.Message; _trimProgress = null; }
        finally { _trimCancellation.Dispose(); _trimCancellation = null; }
    }
}
