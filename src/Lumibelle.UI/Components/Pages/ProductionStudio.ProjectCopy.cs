namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private bool _copyShotsOpen;
    private bool _copyShotsBusy;
    private Guid? _copyInitialShot;

    private async Task OpenShotProjectCopy(Guid? single = null)
    {
        if (ShotListLocked || _copyShotsOpen) return;
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        if (!await Save()) return;
        _copyInitialShot = single; _copyShotsOpen = true;
    }
}
