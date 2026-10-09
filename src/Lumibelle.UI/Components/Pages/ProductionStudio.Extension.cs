using lumibelle.Models;
using lumibelle.Services.Shots;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private lumibelle.Components.Shots.TakePlayer? _joinPlayer;
    private CancellationTokenSource? _extensionPreparation;
    private Guid? _joinPreviewTake;
    private async Task OpenExtension(ShotTake take) {
        if (!_reviewOpen || ReviewTake?.Id != take.Id) await OpenTake(take);
        _trimmingTake = _refiningTake = null;
        OpenContinue(take.FrameCount - 1);
    }
    private async Task OpenLeadIn(ShotTake take) {
        if (!_reviewOpen || ReviewTake?.Id != take.Id) await OpenTake(take);
        _trimmingTake = _refiningTake = null;
        OpenLeadInto(0);
    }
    private async Task QueueExtension(TakeExtensionOptions options) {
        if (_refinementBusy || _refinementEnqueue is not null) return;
        _refinementBusy = true; _reviewError = null;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _extensionPreparation = preparation;
        try {
            if (!await Save()) return;
            _refinementEnqueue = await VideoRequests.CaptureExtensionAsync(Guid.NewGuid(), await AiReviews.TabIdAsync(), Id, options, preparation.Token);
            await EnqueueRefinement(); _continueFrame = null;
            Notify((options.Direction == TakeExtensionDirection.Before ? "Lead-in" : "Extension") + " queued. The result opens for review; production selection stays explicit.");
        } catch (OperationCanceledException) { _reviewError = "Extension preparation cancelled."; }
        catch (Exception e) { _reviewError = e.Message; }
        finally { _extensionPreparation = null; _refinementBusy = false; }
    }
    private async Task PreviewJoin() {
        if (_joinPlayer is null || ReviewTake?.Composition is not { } composition) return;
        if (composition.HasJoinPreview) { await _joinPlayer.PauseAsync(); _joinPreviewTake = ReviewTake.Id; }
        else await _joinPlayer.PreviewJoinAsync(composition.JoinFrame);
    }
}
