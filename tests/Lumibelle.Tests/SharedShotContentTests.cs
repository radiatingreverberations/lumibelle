using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task GenerationSetupsShareShotPromptAndReferencesButKeepTheirSettings()
    {
        var f = Fixture(); var source = Ready(); var other = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source, other], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        var doc = await store.InitializeAsync(f.Project.Id, _ct);
        var original = doc.Compositions.First(c => c.ShotId == source.Id);
        original.Prompt = "The shared shot prompt.";
        original.DirectingNotes = "Keep the camera still.";
        original.Shot.Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Room" }];
        original = (await store.SaveAsync(f.Project.Id, original, original.Version, _ct)).Compositions.First(c => c.Id == original.Id);
        var alternative = new ProductionComposition { ShotId = source.Id, Name = "High quality", TakeCount = 3, Seed = 123 };
        alternative.Shot.Resolution = VideoResolution.Detail;
        doc = await store.SaveAsync(f.Project.Id, alternative, 0, _ct);
        alternative = doc.Compositions.Single(c => c.Id == alternative.Id);
        Assert.Equal(original.Prompt, alternative.Prompt);
        Assert.Equal(original.Shot.Images, alternative.Shot.Images);
        Assert.Equal(original.DirectingNotes, alternative.DirectingNotes);
        Assert.Equal(VideoResolution.Detail, alternative.Shot.Resolution);
        Assert.NotEqual(alternative.Shot.Resolution, doc.Compositions.Single(c => c.Id == original.Id).Shot.Resolution);
        Assert.Empty(doc.Compositions.Single(c => c.ShotId == other.Id).Prompt);

        var stale = original.Copy();
        alternative.Prompt = "A revised prompt for the entire shot.";
        alternative.Shot.Images[0].Name = "Renamed room";
        doc = await store.SaveAsync(f.Project.Id, alternative, alternative.Version, _ct);
        Assert.All(doc.Compositions.Where(c => c.ShotId == source.Id), c => Assert.Equal(alternative.Prompt, c.Prompt));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(f.Project.Id, stale, stale.Version, _ct));
        original = doc.Compositions.Single(c => c.Id == original.Id);
        alternative = doc.Compositions.Single(c => c.Id == alternative.Id);
        var originalVersion = original.Version;
        alternative.Seed = 456; alternative.Shot.GenerationPreset = "turbo4";
        doc = await store.SaveAsync(f.Project.Id, alternative, alternative.Version, _ct);
        Assert.Equal(originalVersion, doc.Compositions.Single(c => c.Id == original.Id).Version);
        doc = await store.LoadAsync(f.Project.Id, _ct);
        Assert.All(doc.Compositions.Where(c => c.ShotId == source.Id), c => {
            Assert.Equal(alternative.Prompt, c.Prompt);
            Assert.Equal("Renamed room", Assert.Single(c.Shot.Images).Name);
        });
        var path = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "production.json");
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, _ct));
        foreach (var setup in saved.RootElement.GetProperty("compositions").EnumerateArray()) {
            Assert.False(setup.TryGetProperty("prompt", out _));
            Assert.False(setup.GetProperty("inputs").TryGetProperty("images", out _));
        }
        Assert.Equal(2, saved.RootElement.GetProperty("shotContent").GetArrayLength());
    }

    [Fact]
    public async Task SharedShotMigrationPreservesAlternateInputsHistoryMediaAndJobs()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var a = new ProductionComposition { ShotId = source.Id, Version = 4, Prompt = "Original default prompt." };
        var b = new ProductionComposition { ShotId = source.Id, Version = 7, Name = "Alternative", Prompt = "Earlier alternative prompt." };
        b.Shot.Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid() }];
        b.History.Add(new(Guid.NewGuid(), _clock.GetUtcNow(), b.Prompt, "Usage", "Context", "Source"));
        b.AcceptedRevisionId = b.History[0].Id;
        await AtomicJsonFile.WriteAsync(Path.Combine(dir, "production.json"), new ProductionDocument { SchemaVersion = 2, ProjectId = f.Project.Id, Revision = 9, Compositions = [a, b] }, _ct);
        var original = await File.ReadAllBytesAsync(Path.Combine(dir, "production.json"), _ct);
        var shots = await File.ReadAllBytesAsync(Path.Combine(dir, "shots.json"), _ct);
        var job = Guid.NewGuid();
        await jobs.EnqueueAsync(AiJobSubmission.Create(job, AiJobKind.PromptComposition, AiBackend.OpenRouter, new(f.Project.Id, ShotId: source.Id, CompositionId: b.Id), "Project", "Existing request", Guid.NewGuid(), new { preserved = true }), _ct);
        var migrated = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Equal(4, migrated.SchemaVersion);
        Assert.All(migrated.Compositions, c => Assert.Equal(a.Prompt, c.Prompt));
        var earlier = Assert.Single(migrated.EarlierSetupContent);
        Assert.Equal(b.Id, earlier.SetupId); Assert.Equal(b.Prompt, earlier.Content.Prompt);
        Assert.Equal(b.History, earlier.Content.History); Assert.Equal(b.Shot.Images, earlier.Content.Images);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(dir, "production-before-shared-inputs.json"), _ct));
        Assert.Equal(shots, await File.ReadAllBytesAsync(Path.Combine(dir, "shots.json"), _ct));
        Assert.Equal(job, Assert.Single((await jobs.ReadAsync(_ct)).Jobs).Id);
        var reopened = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Equal(migrated.Revision, reopened.Revision);
        Assert.Equal(JsonSerializer.Serialize(earlier), JsonSerializer.Serialize(Assert.Single(reopened.EarlierSetupContent)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvironmentMigrationRecoversOneLibraryAndPreservesConflictingSetupChoices(bool conflicting)
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        var environment = Guid.NewGuid();
        var first = new ProductionComposition { ShotId = source.Id, Version = 2 };
        var alternate = new ProductionComposition { ShotId = source.Id, Version = 3, Name = "Alternate" };
        first.Shot.EnvironmentAssetId = conflicting ? Guid.NewGuid() : null;
        alternate.Shot.EnvironmentAssetId = environment;
        // Version 3 stored the environment under each setup's Inputs, outside shared references.
        var content = ShotProductionContent.From(first); content.EnvironmentAssetId = null;
        content.Prompt = "Keep my authored prompt.";
        content.Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Shared character" }];
        var path = Path.Combine(dir, "production.json");
        await AtomicJsonFile.WriteAsync(path, new ProductionDocument {
            SchemaVersion = 3, ProjectId = f.Project.Id, Revision = 5,
            Compositions = [first, alternate], ShotContent = [content]
        }, _ct);
        var original = await File.ReadAllBytesAsync(path, _ct);
        var migrated = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Equal(4, migrated.SchemaVersion);
        Assert.All(migrated.Compositions, c => Assert.Equal(conflicting ? null : (Guid?)environment, c.Shot.EnvironmentAssetId));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(dir, "production-before-shared-environment.json"), _ct));
        if (conflicting) Assert.Equal(new[] { first.Shot.EnvironmentAssetId, alternate.Shot.EnvironmentAssetId }.Order(),
            migrated.EarlierSetupContent.Select(e => e.Content.EnvironmentAssetId).Order());
        else Assert.Empty(migrated.EarlierSetupContent);
        Assert.All(migrated.EarlierSetupContent, e => { Assert.Equal(content.Prompt, e.Content.Prompt); Assert.Equal(content.Images, e.Content.Images); });
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, _ct));
        Assert.All(saved.RootElement.GetProperty("compositions").EnumerateArray(), c => Assert.False(c.GetProperty("inputs").TryGetProperty("environmentAssetId", out _)));
        var edited = migrated.Compositions.Single(c => c.Id == alternate.Id);
        edited.Shot.EnvironmentAssetId = null;
        await store.SaveAsync(f.Project.Id, edited, edited.Version, _ct);
        Assert.All((await store.LoadAsync(f.Project.Id, _ct)).Compositions, c => Assert.Null(c.Shot.EnvironmentAssetId));
    }
}
