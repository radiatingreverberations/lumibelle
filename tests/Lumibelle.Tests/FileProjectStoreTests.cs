using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed class FileProjectStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lumibelle.Tests", Guid.NewGuid().ToString("D"));
    private readonly TestClock _clock = new();

    private FileProjectStore Store(string root = "projects") => new(
        Options.Create(new ProjectStorageOptions { RootDirectory = root }),
        new TestEnvironment { ContentRootPath = _directory }, _clock, NullLogger<FileProjectStore>.Instance);

    [Fact]
    public async Task MissingLibraryIsEmptyWithoutCreatingFiles()
    {
        var library = await Store().ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(library.Projects);
        Assert.Empty(library.Issues);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task BlankNameIsRejectedBeforeWriting(string name)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Store().CreateAsync(new(name), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task CreatedProjectSurvivesFreshStoreAndPreservesUnicode()
    {
        var project = await Store().CreateAsync(new("  Lumière / 光  ", "  Scène 1\nEn skog 🌲  "), TestContext.Current.CancellationToken);
        var reopened = await Store().GetAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(project, reopened);
        Assert.Equal("Lumière / 光", project.Name);
        Assert.Equal("Scène 1\nEn skog 🌲", project.Description);
        Assert.Equal(_clock.GetUtcNow(), project.CreatedUtc);
        Assert.Equal(1, project.SchemaVersion);
        Assert.Equal(project, Assert.Single((await Store().ListAsync(TestContext.Current.CancellationToken)).Projects));
        var folder = Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "projects")));
        Assert.Equal(project.Id.ToString("D"), Path.GetFileName(folder));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "project.json"), TestContext.Current.CancellationToken));
        Assert.Equal(project.Id, manifest.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task DuplicateNamesAreIndependentAndNewestProjectsComeFirst()
    {
        var first = await Store().CreateAsync(new("Same name"), TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddMinutes(1);
        var second = await Store().CreateAsync(new("Same name"), TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.Description);
        Assert.Equal(new[] { second, first }, (await Store().ListAsync(TestContext.Current.CancellationToken)).Projects);
    }

    [Theory]
    [InlineData(VisualStylePreset.Photographic)]
    [InlineData(VisualStylePreset.Anime)]
    [InlineData(VisualStylePreset.Custom)]
    public async Task CreationPublishesVisualStyleWithTheProjectAndDetailEditsPreserveIt(VisualStylePreset preset)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var project = await store.CreateAsync(new("A consistent look") { VisualStyle = new() { Preset = preset, Direction = "  Soft morning light.  " } }, ct);
        var files = new lumibelle.Services.Story.ProjectFiles(Options.Create(new ProjectStorageOptions { RootDirectory = "projects" }), new TestEnvironment { ContentRootPath = _directory }, Store());
        var preferences = new lumibelle.Services.AI.FileProjectAiPreferencesStore(files);
        var initial = await preferences.LoadAsync(project.Id, ct);
        Assert.Equal(preset, initial.VisualStyle.Preset); Assert.Equal("Soft morning light.", initial.VisualStyle.Direction);
        Assert.Equal(0, initial.VisualStyleRevision);
        await store.UpdateAsync(project, new("Updated title", "New notes"), ct);
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(await preferences.LoadAsync(project.Id, ct)));
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(_directory, "projects")), p => Path.GetFileName(p).StartsWith(".creating-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidCreationStyleIsRejectedBeforePublishingAnyProject()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var style in new[] { new VisualStyle { Preset = (VisualStylePreset)999 }, new VisualStyle { Preset = VisualStylePreset.Custom, Direction = new('x', 4001) } })
            await Assert.ThrowsAsync<ValidationException>(() => Store().CreateAsync(new("Invalid style") { VisualStyle = style }, ct));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task UnknownProjectIsMissing()
    {
        Assert.Null(await Store().GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task BadManifestIsReportedWithoutHidingHealthyProjects(string json)
    {
        var healthy = await Store().CreateAsync(new("Healthy"), TestContext.Current.CancellationToken);
        var badId = Guid.NewGuid();
        var badFolder = Path.Combine(_directory, "projects", badId.ToString("D"));
        Directory.CreateDirectory(badFolder);
        await File.WriteAllTextAsync(Path.Combine(badFolder, "project.json"), json, TestContext.Current.CancellationToken);

        var library = await Store().ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(healthy, Assert.Single(library.Projects));
        Assert.Equal(badId, Assert.Single(library.Issues).ProjectId);
        await Assert.ThrowsAsync<ProjectStoreException>(() => Store().GetAsync(badId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedSchemaAndMismatchedIdentityAreReported()
    {
        var project = await Store().CreateAsync(new("Future project"), TestContext.Current.CancellationToken);
        var manifestPath = Path.Combine(_directory, "projects", project.Id.ToString("D"), "project.json");
        foreach (var invalid in new[] { project with { SchemaVersion = 999 }, project with { Id = Guid.NewGuid() } })
        {
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web)), TestContext.Current.CancellationToken);
            var library = await Store().ListAsync(TestContext.Current.CancellationToken);
            Assert.Empty(library.Projects);
            Assert.Single(library.Issues);
        }
    }

    [Fact]
    public async Task InterruptedStagingWriteIsIgnoredAndMissingPublishedManifestIsReported()
    {
        var root = Path.Combine(_directory, "projects");
        var staging = Path.Combine(root, $".creating-{Guid.NewGuid():D}");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "project.json"), "{", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("D")));
        var library = await Store().ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(library.Projects);
        Assert.Single(library.Issues);
    }

    [Fact]
    public async Task StorageFailuresAreSurfaced()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "projects"), "A file blocks the library directory", TestContext.Current.CancellationToken);
        var saveError = await Assert.ThrowsAsync<ProjectStoreException>(() => Store().CreateAsync(new("Not saved"), TestContext.Current.CancellationToken));
        Assert.NotNull(saveError.InnerException);
        await Assert.ThrowsAsync<ProjectStoreException>(() => Store().ListAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ProjectStoreException>(() => Store().GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledCreationDoesNotPublishProject()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().CreateAsync(new("Cancelled"), cancellation.Token));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task UpdatedDetailsPersistWithoutChangingIdentityOrProjectContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Original", "Old description"), ct);
        var folder = Path.Combine(_directory, "projects", original.Id.ToString("D"));
        await File.WriteAllTextAsync(Path.Combine(folder, "script.json"), "script content", ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "assets.json"), "asset content", ct);
        _clock.Now = _clock.Now.AddDays(1);

        var updated = await Store().UpdateAsync(original, new("  Lumière / 光  ", "  First line\nSecond line 🌲  "), ct);

        Assert.Equal(original with { Name = "Lumière / 光", Description = "First line\nSecond line 🌲" }, updated);
        Assert.Equal(updated, await Store().GetAsync(original.Id, ct));
        Assert.Equal(updated, Assert.Single((await Store().ListAsync(ct)).Projects));
        Assert.Equal("script content", await File.ReadAllTextAsync(Path.Combine(folder, "script.json"), ct));
        Assert.Equal("asset content", await File.ReadAllTextAsync(Path.Combine(folder, "assets.json"), ct));
        Assert.Equal(folder, Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "projects"))));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        var cleared = await Store().UpdateAsync(updated, new(updated.Name, " \n "), ct);
        Assert.Null(cleared.Description);
        Assert.Equal(cleared, await Store().GetAsync(original.Id, ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    public async Task InvalidUpdateRetainsSavedDetails(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Keep me", "Keep this too"), ct);
        await Assert.ThrowsAsync<ValidationException>(() => Store().UpdateAsync(original, new(name, "Changed"), ct));
        Assert.Equal(original, await Store().GetAsync(original.Id, ct));
    }

    [Fact]
    public async Task StaleOrConcurrentUpdatesDoNotOverwriteNewerDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Original"), ct);
        async Task<bool> TryUpdate(string name)
        {
            try { await Store().UpdateAsync(original, new(name), ct); return true; }
            catch (ProjectStoreException) { return false; }
        }
        var results = await Task.WhenAll(TryUpdate("First"), TryUpdate("Second"));
        Assert.Single(results, saved => saved);
        var saved = await Store().GetAsync(original.Id, ct);
        Assert.Equal(results[0] ? "First" : "Second", saved!.Name);
        var error = await Assert.ThrowsAsync<ProjectStoreException>(() => Store().UpdateAsync(original, new("Stale"), ct));
        Assert.Contains("details changed", error.Message);
        Assert.Equal(saved, await Store().GetAsync(original.Id, ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":999}")]
    public async Task MissingOrInvalidManifestIsNotReplacedByAnUpdate(string? content)
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Original"), ct);
        var path = Path.Combine(_directory, "projects", original.Id.ToString("D"), "project.json");
        if (content is null) File.Delete(path);
        else await File.WriteAllTextAsync(path, content, ct);
        await Assert.ThrowsAsync<ProjectStoreException>(() => Store().UpdateAsync(original, new("Changed"), ct));
        if (content is null) Assert.False(File.Exists(path));
        else Assert.Equal(content, await File.ReadAllTextAsync(path, ct));
    }

    [Fact]
    public async Task CancelledUpdateRetainsSavedDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Keep me"), ct);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().UpdateAsync(original, new("Cancelled"), cancellation.Token));
        Assert.Equal(original, await Store().GetAsync(original.Id, ct));
    }

    [Fact]
    public async Task VideoAspectPersistsWithoutBeingResetByMetadataEditsAndRejectsStaleDrafts()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Film", "Notes"), ct);
        var portrait = await Store().UpdateAsync(original, new(original.Name, original.Description, "9:16"), ct);
        Assert.Equal("9:16", (await Store().GetAsync(original.Id, ct))!.VideoAspect);
        var renamed = await Store().UpdateAsync(portrait, new("Film renamed", "New notes"), ct);
        Assert.Equal("9:16", renamed.VideoAspect);
        await Assert.ThrowsAsync<ProjectStoreException>(() => Store().UpdateAsync(portrait, new(portrait.Name, portrait.Description, "1:1"), ct));
        await Assert.ThrowsAsync<ValidationException>(() => Store().UpdateAsync(renamed, new(renamed.Name, renamed.Description, "4:3"), ct));
        Assert.Equal(renamed, await Store().GetAsync(original.Id, ct));
    }

    [Fact]
    public async Task FailedReplacementRetainsThePreviousManifestAndCleansUpTemporaryFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store().CreateAsync(new("Keep me"), ct);
        var folder = Path.Combine(_directory, "projects", original.Id.ToString("D"));
        var path = Path.Combine(folder, "project.json");
        // Windows readers must permit deletion for atomic replacement to succeed.
        if (!OperatingSystem.IsWindows()) return;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<ProjectStoreException>(() => Store().UpdateAsync(original, new("Changed"), ct));
        Assert.Equal(original, await Store().GetAsync(original.Id, ct));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Lumibelle.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
