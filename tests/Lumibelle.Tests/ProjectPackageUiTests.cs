using Bunit;
using lumibelle;
using lumibelle.Components.Projects;
using lumibelle.Models;
using lumibelle.Services.Projects;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ProjectPackageUiTests : BunitContext
{
    private readonly PackageFake packages = new();
    private readonly HostFake host = new();
    public ProjectPackageUiTests() {
        Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProjectPackageService>(packages); Services.AddSingleton<IHostActions>(host);
    }
    private IRenderedComponent<ProjectTransferPanel> View(Action<ProjectInfo>? imported = null) =>
        Render<ProjectTransferPanel>(p => p.Add(c => c.Projects, new[] { packages.Project }).Add(c => c.Imported, v => imported?.Invoke(v)));
    private static Task Click(IRenderedComponent<ProjectTransferPanel> view, string text) =>
        view.InvokeAsync(() => view.FindAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new()));

    [Fact]
    public async Task PackageUiUsesExplicitTrashChoiceAndDownloadsOnlyPreparedArchive()
    {
        var view = View();
        await view.InvokeAsync(() => view.Find("#package-project").ChangeAsync(new() { Value = packages.Project.Id.ToString() }));
        await view.InvokeAsync(() => view.Find("input[type=checkbox]").ChangeAsync(new() { Value = false }));
        Assert.Equal(0, packages.Exports); Assert.Null(host.Url);
        await Click(view, "Prepare export");
        Assert.Equal(1, packages.Exports); Assert.False(packages.Options!.IncludeReferencedTrashImages); Assert.Null(host.Url);
        await Click(view, "Download project package"); Assert.Equal(packages.Export.Url, host.Url);
        Assert.Equal(0, packages.Imports);
    }
    [Fact]
    public async Task PackageUiOffersCompactSharingOptionsOffByDefault()
    {
        var view = View();
        await view.InvokeAsync(() => view.Find("#package-project").ChangeAsync(new() { Value = packages.Project.Id.ToString() }));
        Assert.False(view.Find("#package-lossless").HasAttribute("checked")); Assert.Empty(view.FindAll("#package-image-size"));
        await Click(view, "Prepare export");
        Assert.Equal(new ProjectExportOptions(), packages.Options);
        await view.InvokeAsync(() => view.Find("#package-lossless").ChangeAsync(new() { Value = true }));
        await view.InvokeAsync(() => view.Find("#package-compress").ChangeAsync(new() { Value = true }));
        await view.InvokeAsync(() => view.Find("#package-image-size").ChangeAsync(new() { Value = "1920" }));
        await Click(view, "Prepare export");
        Assert.Equal(new ProjectExportOptions(true, true, true, 1920), packages.Options);
        Assert.Contains("Lossless archives left out", view.Markup); Assert.Contains("12 images compressed · 3 reduced in size", view.Markup);
        await view.InvokeAsync(() => view.Find("#package-image-size").ChangeAsync(new() { Value = "0" }));
        await Click(view, "Prepare export");
        Assert.Null(packages.Options!.MaxImageDimension);
    }
    [Fact]
    public async Task PackageUiInspectsBeforeExplicitImportAndSurfacesCollision()
    {
        var accepted = 0; var view = View(_ => accepted++);
        view.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1,2,3], "project.zip", contentType: "application/zip"));
        Assert.Equal(0, packages.Inspects); Assert.Equal(0, packages.Imports);
        await Click(view, "Inspect package"); Assert.Equal(1, packages.Inspects); Assert.Equal(0, packages.Imports);
        await Click(view, "Import project"); Assert.Equal(1, packages.Imports); Assert.Equal(1, accepted);
        packages.Exists = true;
        await Click(view, "Inspect package");
        Assert.True(view.FindAll("button").Single(b => b.TextContent.Trim() == "Import project").HasAttribute("disabled"));
        Assert.Contains("already exists", view.Markup); Assert.Equal(1, packages.Imports);
    }
    [Fact]
    public async Task PackageUiFailedExportPreservesChoiceAndReportsError()
    {
        packages.Fail = true; var view = View();
        await view.InvokeAsync(() => view.Find("#package-project").ChangeAsync(new() { Value = packages.Project.Id.ToString() }));
        await Click(view, "Prepare export"); Assert.Contains("Source image is missing", view.Markup);
        packages.Fail = false; await Click(view, "Prepare export");
        Assert.Equal(2, packages.Exports); Assert.Equal(packages.Project.Id, packages.LastProject);
    }
    private sealed class HostFake : IHostActions
    {
        public string? Url;
        public bool IsDesktop => false;
        public Task SaveResourceAsync(string name, string url) { Url = url; return Task.CompletedTask; }
        public Task SaveTextAsync(string name, string text) => throw new NotSupportedException();
        public Task OpenExternalAsync(string url) => throw new NotSupportedException();
    }
    private sealed class PackageFake : IProjectPackageService
    {
        internal ProjectInfo Project = new() { Id = Guid.NewGuid(), Name = "Project", SchemaVersion = 1, CreatedUtc = DateTimeOffset.UtcNow };
        internal int Exports, Inspects, Imports; internal bool Fail, Exists; internal Guid LastProject;
        internal ProjectExportOptions? Options;
        private readonly Guid export = Guid.NewGuid();
        internal ProjectPackageExport Export => new(export, Project.Id, "project.zip", 123, new() { ProjectId = Project.Id, ExportedUtc = DateTimeOffset.UtcNow });
        public Task<ProjectPackageExport> ExportAsync(Guid project, ProjectExportOptions options, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default) {
            Exports++; LastProject = project; Options = options;
            var export = Export with { Manifest = Export.Manifest with { LeftOutLosslessArchives = options.LeaveOutLosslessArchives, LeftOutLosslessFiles = options.LeaveOutLosslessArchives ? 4 : 0,
                CompressedImages = options.CompressImages, RecompressedImages = options.CompressImages ? 12 : 0, ResizedImages = options.CompressImages ? 3 : 0 } };
            return Fail ? Task.FromException<ProjectPackageExport>(new WorkspaceStoreException("Source image is missing")) : Task.FromResult(export);
        }
        public Task<AssetMedia?> OpenExportAsync(Guid project, Guid export, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DiscardExportAsync(Guid project, Guid export, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProjectPackageImport> StageImportAsync(Stream content, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default) {
            Inspects++; return Task.FromResult(new ProjectPackageImport(Guid.NewGuid(), Project, 123, 1, [], Exists));
        }
        public Task<ProjectInfo> CommitImportAsync(Guid token, CancellationToken ct = default) { Imports++; return Task.FromResult(Project); }
        public Task DiscardImportAsync(Guid token, CancellationToken ct = default) => Task.CompletedTask;
    }
}
