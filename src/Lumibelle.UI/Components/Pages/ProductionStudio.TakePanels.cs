using lumibelle.Components.Shots;
using lumibelle.Models;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private bool _frameSaveBusy;
    private int _trimStart;
    private int? _trimEnd;
    private bool ReviewPanelBusy => _frameSaveBusy || _refinementBusy || _trimCancellation is not null;
    private TakeToolPanel? ActiveReviewPanel => _trimmingTake is not null ? TakeToolPanel.Trim
        : _continueFrame is not null ? (_extensionDirection == TakeExtensionDirection.Before ? TakeToolPanel.LeadInto : TakeToolPanel.Continue)
        : _frameDestination is not null ? TakeToolPanel.SaveFrame : null;

    private void CloseReviewPanel()
    {
        if (ReviewPanelBusy) return;
        if (_trimmingTake is not null) CloseTrim();
        else if (_continueFrame is not null) CloseContinue();
        else CloseFrameDestination();
    }

    private void TrimRangeChanged((int Start, int End) range) { _trimStart = range.Start; _trimEnd = range.End; }

    private void ReviewFrameChanged(int index)
    {
        if (ReviewPanelBusy || ReviewTake is not { } take) return;
        if (_continueFrame?.TakeId == take.Id) _continueFrame = (take.Id, index);
        if (_frameDestination?.TakeId == take.Id) _frameDestination = (take.Id, index);
    }
}
