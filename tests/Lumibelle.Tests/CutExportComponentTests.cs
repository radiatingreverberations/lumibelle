using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private sealed class LunaUiExporter : ICutExporter
    {
        public int Calls;
        public long Revision;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public Exception? Failure;
        public CutExportRange? Range;
        public async Task<CutExportResult> ExportAsync(Guid projectId, long expectedRevision, CutExportRange? range, CancellationToken ct = default)
        {
            Calls++; Revision = expectedRevision; Range = range; Entered.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            if (Failure is not null) throw Failure;
            return new(Guid.NewGuid(), projectId, expectedRevision, DateTimeOffset.UtcNow.AddHours(1)) { Range = range };
        }
        public Task<AssetMedia?> OpenAsync(Guid projectId, Guid exportId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CutExportDoesNotRunWhenSavingTheDraftFails()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var document = await AddTake(f.Project.Id, f.Shots, shot);
        var store = new FileCutStore(f.Files, f.Shots, _clock);
        await store.SaveAsync(f.Project.Id, [CutClip.From(shot, document.Takes[0])], 0, _ct);
        var controlled = new ControlledCutStore(store) { Fail = true };
        var exporter = new LunaUiExporter();
        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddMudServices(); ui.Services.AddSingleton<ICutExporter>(exporter);
        ui.Services.AddSingleton<ICutStore>(controlled); ui.Services.AddSingleton<IShotStore>(f.Shots);
        ui.Services.AddSingleton<IProjectStore>(new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) });
        ui.JSInterop.SetupModule("./_content/Lumibelle.UI/cut-player.js").SetupModule("attach", _ => true);
        ui.Render<MudPopoverProvider>(); var dialogs = ui.Render<MudDialogProvider>();
        var actions = ui.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        var page = ui.Render<CutStudio>(p => p.Add(x => x.Id, f.Project.Id));
        page.WaitForElement(".cut-trims input").Change("2");
        await StartExport(page, actions, dialogs);
        Assert.Equal(0, exporter.Calls);
        Assert.Contains("Save failed · draft retained", actions.Markup);
        Assert.Equal("2", page.Find(".cut-trims input").GetAttribute("value"));
    }

    // Export MP4 asks which clips; Export in that dialog starts it.
    private static async Task StartExport(IRenderedComponent<CutStudio> page, IRenderedComponent<SectionOutlet> actions, IRenderedComponent<MudDialogProvider> dialogs)
    {
        await page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Export MP4").ClickAsync(new()));
        dialogs.WaitForElement(".cut-export-dialog");
        await page.InvokeAsync(() => dialogs.FindAll(".cut-export-dialog button").Single(b => b.TextContent.Trim() == "Export").ClickAsync(new()));
    }

    [Fact]
    public async Task CutExportCanBeCancelledAndShowsRendererErrorsWithoutA404()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var document = await AddTake(f.Project.Id, f.Shots, shot);
        var store = new FileCutStore(f.Files, f.Shots, _clock);
        var cut = await store.SaveAsync(f.Project.Id, [CutClip.From(shot, document.Takes[0])], 0, _ct);
        var exporter = new LunaUiExporter { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddMudServices(); ui.Services.AddSingleton<ICutExporter>(exporter);
        ui.Services.AddSingleton<ICutStore>(store); ui.Services.AddSingleton<IShotStore>(f.Shots);
        ui.Services.AddSingleton<IProjectStore>(new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) });
        ui.JSInterop.SetupModule("./_content/Lumibelle.UI/cut-player.js").SetupModule("attach", _ => true);
        ui.Render<MudPopoverProvider>(); var dialogs = ui.Render<MudDialogProvider>();
        var actions = ui.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        var page = ui.Render<CutStudio>(p => p.Add(x => x.Id, f.Project.Id));
        page.WaitForElement(".cut-trims input");
        var render = StartExport(page, actions, dialogs);
        await exporter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        await page.InvokeAsync(() => page.FindAll(".cut-export-notice button").Single(b => b.TextContent.Trim() == "Cancel export").ClickAsync(new()));
        await render;
        Assert.Contains("Export cancelled", page.Markup); Assert.Equal(cut.Revision, exporter.Revision);
        Assert.Null(exporter.Range); // One clip is the whole cut.
        exporter.Hold = null; exporter.Failure = new WorkspaceStoreException("FFmpeg diagnostic sentinel");
        await StartExport(page, actions, dialogs);
        Assert.Contains("FFmpeg diagnostic sentinel", page.Markup);
        Assert.DoesNotContain("could not be downloaded", page.Markup);
    }
}
