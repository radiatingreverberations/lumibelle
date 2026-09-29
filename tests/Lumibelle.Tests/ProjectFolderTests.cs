using System.IO.Compression;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Projects;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class ProjectFolderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumibelle-folders-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly List<Library> _libraries = [];
    private string Outside(string name) { var path = Path.Combine(_directory, "outside", name); Directory.CreateDirectory(path); return path; }
    private Library NewLibrary(bool lease = false, bool copy = false) { var library = new Library(Path.Combine(_directory, "library-" + _libraries.Count), lease, copy); _libraries.Add(library); return library; }

    [Fact]
    public async Task AProjectFolderOpensAndIsEditedInPlace()
    {
        var source = NewLibrary(); var project = await source.Projects.CreateAsync(new("Night train"), _ct);
        var folder = Path.Combine(Outside("films"), "Night train");
        Copy(source.Root(project), folder);
        var library = NewLibrary();
        Assert.Equal(project, await library.Folders.OpenAsync(folder, _ct));
        Assert.Equal(project.Id, Assert.Single((await library.Projects.ListAsync(_ct)).Projects).Id);
        Assert.Equal(folder, await library.Files.DirectoryAsync(project.Id, _ct));
        Assert.Equal(folder, (await library.Folders.LocationAsync(project.Id, _ct))!.Path);
        var renamed = await library.Projects.UpdateAsync(project, new("Night train, recut", "In place"), _ct);
        Assert.Equal("Night train, recut", (await FileProjectStore.ReadManifestAsync(folder, _ct)).Name);
        Assert.Equal(renamed, await library.Projects.GetAsync(project.Id, _ct));
        Assert.False(Directory.Exists(Path.Combine(library.Paths.Projects, project.Id.ToString("D"))));
    }

    [Fact]
    public async Task AnUnzippedExportOpensFromItsProjectFolder()
    {
        var source = NewLibrary(); var project = await source.Projects.CreateAsync(new("Exported"), _ct);
        var export = await source.Packages.ExportAsync(project.Id, new(), ct: _ct);
        var folder = Outside("unzipped");
        await using (var media = (await source.Packages.OpenExportAsync(project.Id, export.Id, _ct))!) ZipFile.ExtractToDirectory(media.Content, folder);
        var library = NewLibrary();
        await library.Folders.OpenAsync(folder, _ct);
        Assert.Equal(Path.Combine(folder, "project"), await library.Files.DirectoryAsync(project.Id, _ct));
        Assert.Equal(project.Id, (await new FileAssetStore(library.Files, TimeProvider.System).LoadAsync(project.Id, _ct)).ProjectId);
        // A linked project exports like one in the library, and its ID cannot be imported beside it.
        var again = await library.Packages.ExportAsync(project.Id, new(), ct: _ct);
        await using var zip = (await library.Packages.OpenExportAsync(project.Id, again.Id, _ct))!;
        var staged = await library.Packages.StageImportAsync(zip.Content, ct: _ct);
        Assert.True(staged.AlreadyExists);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Packages.CommitImportAsync(staged.Token, _ct));
        Assert.Contains("already in the library", error.Message);
    }

    [Fact]
    public async Task ACompactExportOpensFromItsProjectFolder()
    {
        var source = NewLibrary(); var project = await source.Projects.CreateAsync(new("Shared example"), _ct);
        var id = Guid.NewGuid();
        var image = new AssetImage { Id = id, FileName = id.ToString("N") + ".png", ContentType = "image/png", Width = 400, Height = 300, CreatedUtc = DateTimeOffset.UtcNow };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Lantern", Category = AssetCategory.Prop, Images = [image] };
        await AtomicJsonFile.WriteAsync(Path.Combine(source.Root(project), "assets.json"), new AssetLibrary { ProjectId = project.Id, Assets = [asset] }, _ct);
        var file = Path.Combine(source.Root(project), "assets", asset.Id.ToString("D"), "images", image.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllBytesAsync(file, ProjectPackageTests.Photo(400, 300), _ct);
        var export = await source.Packages.ExportAsync(project.Id, new(LeaveOutLosslessArchives: true, CompressImages: true, MaxImageDimension: 256), ct: _ct);
        var folder = Outside("compact");
        await using (var media = (await source.Packages.OpenExportAsync(project.Id, export.Id, _ct))!) ZipFile.ExtractToDirectory(media.Content, folder);
        var library = NewLibrary();
        await library.Folders.OpenAsync(folder, _ct);
        var assets = new FileAssetStore(library.Files, TimeProvider.System);
        var opened = Assert.Single(Assert.Single((await assets.LoadAsync(project.Id, _ct)).Assets).Images);
        Assert.Equal((image.Id.ToString("N") + ".webp", "image/webp", 256, 192), (opened.FileName, opened.ContentType, opened.Width, opened.Height));
        await using var picture = await assets.OpenImageAsync(project.Id, asset.Id, image.Id, _ct);
        Assert.Equal("image/webp", picture!.ContentType);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task FoldersThatAreNotNewProjectsAreRefused()
    {
        var library = NewLibrary(); var project = await library.Projects.CreateAsync(new("Only once"), _ct);
        var copy = Path.Combine(Outside("copies"), "Only once"); Copy(library.Root(project), copy);
        Assert.Contains("already in the library", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.OpenAsync(copy, _ct))).Message);
        Assert.Contains("outside this library", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.OpenAsync(library.Root(project), _ct))).Message);
        Assert.Contains("isn’t a Lumibelle project", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.OpenAsync(Outside("empty"), _ct))).Message);
        Assert.Contains("full path", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.OpenAsync("relative/folder", _ct))).Message);
        Assert.Contains("doesn’t exist", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.OpenAsync(Path.Combine(_directory, "missing"), _ct))).Message);

        var other = NewLibrary(); await other.Folders.OpenAsync(copy, _ct);
        Assert.Contains("already in the library", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => other.Folders.OpenAsync(copy, _ct))).Message);
        Assert.Single((await other.Projects.ListAsync(_ct)).Projects);
    }

    [Fact]
    public async Task AnUnavailableFolderIsAProjectIssueUntilRemoved()
    {
        var source = NewLibrary(); var project = await source.Projects.CreateAsync(new("Unplugged"), _ct);
        var folder = Path.Combine(Outside("drive"), "Unplugged"); Copy(source.Root(project), folder);
        var library = NewLibrary(); var other = await library.Projects.CreateAsync(new("Still here"), _ct);
        await library.Folders.OpenAsync(folder, _ct);
        Directory.Move(folder, folder + " (away)");
        var listed = await library.Projects.ListAsync(_ct);
        Assert.Equal(other.Id, Assert.Single(listed.Projects).Id);
        Assert.Contains(folder, Assert.Single(listed.Issues, i => i.ProjectId == project.Id).Message);
        await Assert.ThrowsAsync<ProjectStoreException>(() => library.Projects.GetAsync(project.Id, _ct));
        await library.Folders.RemoveAsync(project.Id, _ct);
        Assert.Empty((await library.Projects.ListAsync(_ct)).Issues);
        Assert.True(File.Exists(Path.Combine(folder + " (away)", "project.json")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.RemoveAsync(other.Id, _ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALibraryProjectMovesOutAndCanBeRemovedAndOpenedAgain(bool copy)
    {
        var library = NewLibrary(copy: copy); var project = await library.Projects.CreateAsync(new("Moving: day/night"), _ct);
        var media = Path.Combine(library.Root(project), "assets", "image.png");
        Directory.CreateDirectory(Path.GetDirectoryName(media)!); await File.WriteAllBytesAsync(media, [1, 2, 3], _ct);
        Directory.CreateDirectory(Path.Combine(library.Root(project), "empty-folder"));
        var destination = Outside("synced");
        Directory.CreateDirectory(Path.Combine(destination, "Moving day night"));
        var location = await library.Folders.MoveOutAsync(project.Id, destination, ct: _ct);
        Assert.Equal(Path.Combine(destination, "Moving day night 2"), location.Path);
        Assert.False(Directory.Exists(library.Root(project)));
        Assert.Empty(Directory.GetDirectories(library.Paths.Projects, ".moved-*"));
        Assert.Empty(Directory.GetDirectories(destination, ".lumibelle-moving-*"));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(location.Path, "assets", "image.png"), _ct));
        Assert.True(Directory.Exists(Path.Combine(location.Path, "empty-folder")));
        Assert.Equal(project, Assert.Single((await library.Projects.ListAsync(_ct)).Projects));
        Assert.Equal(location.Path, await library.Files.DirectoryAsync(project.Id, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.MoveOutAsync(project.Id, destination, ct: _ct));

        await library.Folders.RemoveAsync(project.Id, _ct);
        Assert.Empty((await library.Projects.ListAsync(_ct)).Projects);
        Assert.True(File.Exists(Path.Combine(location.Path, "project.json")));
        await library.Folders.OpenAsync(location.Path, _ct);
        Assert.Equal(project, Assert.Single((await library.Projects.ListAsync(_ct)).Projects));
    }

    [Fact]
    public async Task AProjectWithActiveAiRequestsIsNotMoved()
    {
        var library = NewLibrary(); var project = await library.Projects.CreateAsync(new("Busy"), _ct);
        await library.Jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant, AiBackend.ComfyUI, new(project.Id),
            "Busy", "Captured request", Guid.NewGuid(), new { prompt = "Input" }), _ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => library.Folders.MoveOutAsync(project.Id, Outside("target"), ct: _ct));
        Assert.Contains("AI requests", error.Message);
        Assert.True(File.Exists(Path.Combine(library.Root(project), "project.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_directory, "outside", "target")));
    }

    [Fact]
    public async Task OnlyOneLibraryAtATimeUsesAProjectFolder()
    {
        var source = NewLibrary(); var project = await source.Projects.CreateAsync(new("Shared drive"), _ct);
        var folder = Path.Combine(Outside("shared"), "Shared drive"); Copy(source.Root(project), folder);
        var first = NewLibrary(lease: true); var second = NewLibrary(lease: true);
        await first.Folders.OpenAsync(folder, _ct);
        Assert.Contains("open in another Lumibelle library", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => second.Folders.OpenAsync(folder, _ct))).Message);
        first.Locations.Dispose();
        await second.Folders.OpenAsync(folder, _ct);
        Assert.Contains("open in another Lumibelle library", Assert.Single((await first.Projects.ListAsync(_ct)).Issues).Message);
    }

    [Fact]
    public async Task ARunningLibraryPreventsLinkingItsInternalProjects()
    {
        var source = NewLibrary(lease: true); var destination = NewLibrary(lease: true);
        using var owner = new WorkspaceOwnership(source.Paths);
        await owner.StartAsync(_ct);
        var project = await source.Projects.CreateAsync(new("Owned project"), _ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => destination.Folders.OpenAsync(source.Root(project), _ct));
        Assert.Contains("open in another Lumibelle library", error.Message);
        Assert.Empty(await destination.Folders.ListAsync(_ct));
        Assert.Equal("Still owned", (await source.Projects.UpdateAsync(project, new("Still owned"), _ct)).Name);
        await owner.StopAsync(_ct);
        Assert.Equal(project.Id, (await destination.Folders.OpenAsync(source.Root(project), _ct)).Id);
    }

    [Fact]
    public async Task ALinkedInternalProjectPreventsItsOriginalLibraryStarting()
    {
        var source = NewLibrary(lease: true); var destination = NewLibrary(lease: true);
        var project = await source.Projects.CreateAsync(new("Linked first"), _ct);
        await destination.Folders.OpenAsync(source.Root(project), _ct);
        using var owner = new WorkspaceOwnership(source.Paths);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => owner.StartAsync(_ct));
        await destination.Folders.RemoveAsync(project.Id, _ct);
        await owner.StartAsync(_ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => destination.Folders.OpenAsync(source.Root(project), _ct));
    }

    [Fact]
    public async Task RestartedLinkedLibrariesRecheckTheOriginalLibraryOwner()
    {
        var source = NewLibrary(lease: true); var destination = NewLibrary(lease: true);
        var project = await source.Projects.CreateAsync(new("Reopened link"), _ct);
        await destination.Folders.OpenAsync(source.Root(project), _ct);
        destination.Locations.Dispose();
        using var owner = new WorkspaceOwnership(source.Paths);
        await owner.StartAsync(_ct);
        await Assert.ThrowsAsync<ProjectStoreException>(() => destination.Projects.GetAsync(project.Id, _ct));
        Assert.Single((await destination.Projects.ListAsync(_ct)).Issues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingAProjectToAnotherLibraryPreservesItsShotSettings(bool copy)
    {
        var source = NewLibrary(lease: true, copy: copy); var destination = NewLibrary(lease: true);
        using var sourceOwner = new WorkspaceOwnership(source.Paths);
        await sourceOwner.StartAsync(_ct);
        var project = await source.Projects.CreateAsync(new("Portable shots"), _ct);
        var shot = new Shot { Title = "Platform" };
        await source.Shots.SaveAsync(project.Id, [shot], 0, ct: _ct);
        var composition = (await source.Production.InitializeAsync(project.Id, _ct)).Compositions.Single();
        composition.Name = "Evening"; composition.TakeCount = 3; composition.Seed = 42;
        composition.Shot.Resolution = VideoResolution.Quick;
        composition.Prompt = "The train pulls in."; composition.DirectingNotes = "Keep the wide view.";
        composition = (await source.Production.SaveAsync(project.Id, composition, composition.Version, _ct)).Compositions.Single();
        var settings = GenerationSettings.From(composition);
        var content = ShotProductionContent.From(composition);
        var unrelated = (await destination.Setups.SaveAsync(new() { Name = "Evening", Settings = new() { TakeCount = 1 } }, 0, _ct)).Setups.Single();
        // A prior import mapping must not reconnect this project's foreign preset to unrelated settings.
        var previous = composition.Copy(); previous.GenerationSetupId = null; previous.TakeCount = 1;
        previous.Seed = null; previous.Shot.Resolution = null;
        await destination.Setups.ImportAsync(new() { ProjectId = project.Id, Compositions = [previous] }, _ct);

        var moved = await source.Folders.MoveOutAsync(project.Id, Outside("moved shots"), ct: _ct);
        await source.Folders.RemoveAsync(project.Id, _ct);
        await destination.Folders.OpenAsync(moved.Path, _ct);
        var opened = (await destination.Production.InitializeAsync(project.Id, _ct)).Compositions.Single();
        Assert.Equal(composition.Id, opened.Id); Assert.Equal(shot.Id, opened.ShotId);
        Assert.True(FileGenerationSetupStore.SameSettings(settings, GenerationSettings.From(opened)));
        Assert.Equal(ReferenceSetups.Hash(content), ReferenceSetups.Hash(ShotProductionContent.From(opened)));
        Assert.NotEqual(unrelated.Id, opened.GenerationSetupId);
        Assert.Equal(1, (await destination.Setups.LoadAsync(_ct)).Setups.Single(s => s.Id == unrelated.Id).Settings.TakeCount);
        var productionPath = Path.Combine(moved.Path, "production.json");
        var saved = await File.ReadAllBytesAsync(productionPath, _ct);
        await destination.Folders.RemoveAsync(project.Id, _ct);
        await destination.Folders.OpenAsync(moved.Path, _ct);
        Assert.Equal(opened.GenerationSetupId, (await destination.Production.InitializeAsync(project.Id, _ct)).Compositions.Single().GenerationSetupId);
        Assert.Equal(saved, await File.ReadAllBytesAsync(productionPath, _ct));
        Assert.Equal(2, (await destination.Setups.LoadAsync(_ct)).Setups.Count);
    }

    [Theory]
    [InlineData("Night train", "Night train")]
    [InlineData("A/B: c?", "A B c")]
    [InlineData("CON", "Lumibelle project")]
    [InlineData("...", "Lumibelle project")]
    [InlineData(" trailing. ", "trailing")]
    public void MovedFoldersAreNamedAfterTheProject(string name, string folder) => Assert.Equal(folder, ProjectFolders.FolderName(name));

    private static void Copy(string source, string target)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    public void Dispose()
    {
        foreach (var library in _libraries) library.Locations.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class Library
    {
        internal ApplicationPaths Paths { get; }
        internal ProjectLocations Locations { get; }
        internal FileProjectStore Projects { get; }
        internal ProjectFiles Files { get; }
        internal FileAiJobStore Jobs { get; }
        internal ProjectFolders Folders { get; }
        internal ProjectPackageService Packages { get; }
        internal FileGenerationSetupStore Setups { get; }
        internal FileShotStore Shots { get; }
        internal FileProductionStore Production { get; }
        internal Library(string directory, bool lease, bool copy = false)
        {
            Paths = new(directory); Directory.CreateDirectory(Paths.Projects);
            Locations = new(Paths, lease);
            Projects = new(Paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance, Locations);
            Files = new(Paths, Projects, null, Locations);
            Jobs = new(Paths.Jobs, TimeProvider.System);
            Setups = new(Paths);
            Shots = new(Files, TimeProvider.System);
            Production = new(Files, Shots, new FileAssetStore(Files, TimeProvider.System), Projects, TimeProvider.System, Jobs, generationSetups: Setups);
            Folders = new(Paths, Locations, Projects, TimeProvider.System, Jobs) { CopyAcrossDrives = copy };
            Packages = new(Paths, Files, Projects, new NoSetups(), TimeProvider.System, Jobs);
        }
        internal string Root(ProjectInfo project) => Path.Combine(Paths.Projects, project.Id.ToString("D"));
    }

    private sealed class NoSetups : IGenerationSetupStore
    {
        public Task<GenerationSetupLibrary> LoadAsync(CancellationToken ct = default) => Task.FromResult(new GenerationSetupLibrary());
        public Task<GenerationSetupLibrary> ImportAsync(ProductionDocument document, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GenerationSetupLibrary> SaveAsync(GenerationSetup setup, long expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GenerationSetupLibrary> SelectAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
