using lumibelle.Models;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class CutStudio
{
    [Inject] public ICutExporter CutExporter { get; set; } = null!;
    private CancellationTokenSource? _exportCancellation;
    private CutExportResult? _completedExport;
    private string? _exportError, _exportStatus;
    private bool _downloadingExport;

    private void CancelExport() => _exportCancellation?.Cancel();

    // Export MP4 asks which clips: the whole cut, or a part such as everything from a chosen clip.
    // The choice is kept between exports; one that ran to the end still does when clips are added.
    private bool _exportOpen, _exportToEnd = true;
    private int _exportFrom = 1, _exportTo = 1;
    private void OpenExport()
    {
        if (_exporting || _downloadingExport || _doc.Clips.Count == 0) return;
        if (_exportToEnd || _exportTo > _doc.Clips.Count) _exportTo = _doc.Clips.Count;
        if (_exportFrom > _exportTo) _exportFrom = 1;
        _exportOpen = true;
    }
    private void ExportWhole() { _exportFrom = 1; _exportTo = _doc.Clips.Count; _exportToEnd = true; }
    private void ExportFromSelected() { if (Selected is { } clip) { _exportFrom = _doc.Clips.IndexOf(clip) + 1; _exportTo = _doc.Clips.Count; _exportToEnd = true; } }
    private void ExportFromChanged(ChangeEventArgs e) { if (int.TryParse(e.Value?.ToString(), out var from)) { _exportFrom = from; if (_exportTo < from) _exportTo = from; } }
    private void ExportToChanged(ChangeEventArgs e) { if (int.TryParse(e.Value?.ToString(), out var to)) { _exportTo = to; _exportToEnd = to == _doc.Clips.Count; if (_exportFrom > to) _exportFrom = to; } }
    private IEnumerable<CutClip> ExportClips => _doc.Clips.Skip(_exportFrom - 1).Take(Math.Max(0, _exportTo - _exportFrom + 1));
    private static string FormatDuration(double seconds) => seconds < 60
        ? seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
        : TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    private string ExportSummary
    {
        get
        {
            var clips = ExportClips.ToList();
            var what = ExportsWholeCut ? "The whole cut" : "Clips " + new CutExportRange(_exportFrom - 1, _exportTo - 1).Label["clips ".Length..];
            if (!ExportsWholeCut && _exportFrom == _exportTo) what = $"Clip {_exportFrom}";
            return $"{what} · {clips.Count} clip{(clips.Count == 1 ? "" : "s")} · {FormatDuration(clips.Sum(c => c.Duration))}";
        }
    }
    private bool ExportsWholeCut => _exportFrom == 1 && _exportTo == _doc.Clips.Count;
    private async Task ConfirmExport()
    {
        _exportOpen = false;
        await Export(ExportsWholeCut ? null : new(_exportFrom - 1, _exportTo - 1));
    }

    private async Task Export(CutExportRange? range = null)
    {
        if (_disposed || _exporting || _downloadingExport || _doc.Clips.Count == 0 || UnavailableTakes.Length > 0) return;
        var projectId = Id;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportCancellation = cancellation;
        _exporting = true;
        _exportError = null;
        _exportStatus = "Saving the cut before exporting…";
        try
        {
            if (!await Save() || _disposed || Id != projectId) return;
            cancellation.Token.ThrowIfCancellationRequested();
            var revision = _doc.Revision;
            _exportStatus = $"Exporting saved revision {revision}{(range is null ? "" : ", " + range.Label)}. Later edits will not change this export.";
            StateHasChanged();
            var result = await CutExporter.ExportAsync(projectId, revision, range, cancellation.Token);
            if (_disposed || Id != projectId) return;
            cancellation.Token.ThrowIfCancellationRequested();
            _completedExport = result;
            _exportStatus = $"Revision {revision}{(result.Range is { } part ? ", " + part.Label + "," : "")} is ready. Downloads remain available for up to one hour, while this app is running.";
            // Rendering has finished; a native save dialog is not cancellable via
            // the render token. Keep its busy state separate.
            _exporting = false;
            _exportCancellation = null;
            await DownloadExport();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed && Id == projectId) _exportStatus = "Export cancelled. Your cut is unchanged.";
        }
        catch (Exception e)
        {
            if (!_disposed && Id == projectId) { _exportError = e.Message; _exportStatus = null; }
        }
        finally
        {
            _exporting = false;
            _exportCancellation = null;
            if (!_disposed && Id == projectId && _exportStatus == "Saving the cut before exporting…") _exportStatus = null;
        }
    }

    private async Task DownloadExport()
    {
        if (_disposed || _downloadingExport || _completedExport is not { } result || result.ProjectId != Id) return;
        _downloadingExport = true;
        _exportError = null;
        StateHasChanged();
        try
        {
            // JS only initiates a download of an existing resource. It does not wait
            // for FFmpeg or buffer an entire movie through JS interop/a Blob.
            await JS.InvokeVoidAsync("lumibelleShots.downloadResource", _lifetime.Token,
                result.FileName, result.Url);
        }
        catch (Exception e)
        {
            if (!_disposed && Id == result.ProjectId) _exportError = e.Message;
        }
        finally { _downloadingExport = false; }
    }
}
