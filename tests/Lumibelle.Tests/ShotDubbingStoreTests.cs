using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class ShotDubbingStoreTests : IDisposable
{
    [Fact]
    public async Task MovedMasterKeepsSavedTranslationsAndCanCaptureANewRevision()
    {
        var f = await SourceFixture();
        var before = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "", _ct);
        var variant = await f.Store.SaveAsync(before, ShotDubbingTests.Translation(before), 0, ct: _ct);
        var shots = new FileShotStore(f.Files, TimeProvider.System);
        var doc = await shots.LoadAsync(f.Project, _ct);
        var target = doc.Shots[0].Copy(); target.Id = Guid.NewGuid(); target.Title = "Another shot";
        doc.Shots.Add(target);
        doc = await shots.SaveAsync(f.Project, doc.Shots, doc.Revision, ct: _ct);
        await shots.MoveTakesAsync(f.Project, [f.Take.Id], target.Id, doc.Revision, _ct);
        var master = await f.Store.SourceAsync(f.Project, f.Take.Id, _ct);
        Assert.Equal(target.Id, master.Take.ShotId);
        ShotDubbing.Apply(master.Request.Snapshot, variant);
        var after = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "", _ct);
        Assert.Equal(target.Id, after.ShotId);
        Assert.Equal(before.SourceFingerprint, after.SourceFingerprint);
        await f.Store.SaveAsync(after, ShotDubbingTests.Translation(after), variant.Version, ct: _ct);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.DubbingTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private async Task<(ProjectInfo Project, ProjectFiles Files, FileAiJobStore Jobs, FileShotStore Shots, FileProjectDubbingStore Store)> StoreFixture()
    {
        var paths = new ApplicationPaths(_root);
        var projects = new FileProjectStore(paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance);
        var project = await projects.CreateAsync(new("Language test"), _ct);
        var files = new ProjectFiles(paths, projects);
        var jobs = new FileAiJobStore(Path.Combine(_root, "ai-jobs"), TimeProvider.System);
        var shots = new FileShotStore(files, TimeProvider.System);
        return (project, files, jobs, shots, new(files, shots, jobs, TimeProvider.System));
    }
    private async Task<(FileProjectDubbingStore Store, ProjectFiles Files, Guid Project, ShotTake Take)> SourceFixture()
    {
        var f = await StoreFixture(); var (snapshot, _) = ShotDubbingTests.Fixture(f.Project.Id);
        var batch = Guid.NewGuid(); var takeId = Guid.NewGuid();
        var request = new AiVideoJobRequest(2, batch, snapshot, []);
        AiVideoJobPolicy.Validate(request);
        await f.Jobs.EnqueueAsync(AiJobSubmission.Create(batch, AiJobKind.Video, AiBackend.ComfyUI,
            AiVideoJobHandler.Target(snapshot), f.Project.Name, "Master video", Guid.NewGuid(), request)
            with { Batch = new(batch, [new(takeId, 1, 123)]) }, _ct);
        await f.Jobs.UpdateAsync(batch, j => j with { State = AiJobState.Completed }, _ct);
        var take = new ShotTake { Id = takeId, AiJobId = batch, RunId = batch, ShotId = snapshot.Shot.Id,
            Snapshot = snapshot, Candidate = 1, Seed = 123, Width = snapshot.Width, Height = snapshot.Height,
            CreatedUtc = DateTimeOffset.UtcNow, Directory = takeId.ToString("D"), Bytes = 20 };
        await AtomicJsonFile.WriteAsync(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "shots.json"),
            new ShotDocument { ProjectId = f.Project.Id, Shots = [snapshot.Shot], Takes = [take] }, _ct);
        await f.Store.SaveLanguagesAsync(new() { ProjectId = f.Project.Id, Main = ShotDubbingTests.English, Dubs = [ShotDubbingTests.Swedish] }, 0, _ct);
        return (f.Store, f.Files, f.Project.Id, take);
    }
    [Fact]
    public async Task SavingLanguagesIsRevisionCheckedAndDoesNotCreateOrChangeScriptAndProduction()
    {
        var f = await StoreFixture(); var original = await f.Store.LanguagesAsync(f.Project.Id, _ct);
        Assert.Null(original.Main); Assert.Equal(0, original.Revision);
        var saved = await f.Store.SaveLanguagesAsync(original with { Main = new("EN", "English"), Dubs = [new("SV", "Swedish")] }, 0, _ct);
        Assert.Equal(1, saved.Revision); Assert.Equal("sv", saved.Dubs[0].Code);
        var reopened = await new FileProjectDubbingStore(f.Files, f.Shots, f.Jobs, TimeProvider.System).LanguagesAsync(f.Project.Id, _ct);
        Assert.True(ShotDubbing.Same(saved, reopened));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Store.SaveLanguagesAsync(original with { Main = ShotDubbingTests.English }, 0, _ct));
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        Assert.False(File.Exists(Path.Combine(dir, "production.json")));
        Assert.False(File.Exists(Path.Combine(dir, "shots.json")));
    }
    [Fact]
    public async Task ForeignLanguageFileIsNotSilentlyReplaced()
    {
        var f = await StoreFixture(); var path = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "languages.json");
        await AtomicJsonFile.WriteAsync(path, new ProjectLanguages { ProjectId = Guid.NewGuid(), Main = ShotDubbingTests.English }, _ct);
        var before = await File.ReadAllBytesAsync(path, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.LanguagesAsync(f.Project.Id, _ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, "legacy-json", "languages.json"), _ct));
    }
    [Fact]
    public async Task ConcurrentLanguageSavesHaveOneWinner()
    {
        var f = await StoreFixture();
        async Task<bool> Save(string name) {
            try { await f.Store.SaveLanguagesAsync(new() { ProjectId = f.Project.Id, Main = new("en", name) }, 0, _ct); return true; }
            catch (WorkspaceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Save("English"), Save("British English"));
        Assert.Single(results, x => x); Assert.Equal(1, (await f.Store.LanguagesAsync(f.Project.Id, _ct)).Revision);
    }
    [Fact]
    public async Task ManualVariantSaveIsIdempotentAndIndependentOfMasterAndMedia()
    {
        var f = await SourceFixture(); var dir = await f.Files.DirectoryAsync(f.Project, _ct);
        await TestProjectDatabase.SnapshotAsync(dir, f.Project, _ct); var before = TestProjectDatabase.Stamp(dir, "shots");
        var captured = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "Keep the humor.", _ct);
        var lines = ShotDubbingTests.Translation(captured);
        var saved = await f.Store.SaveAsync(captured, lines, 0, ct: _ct);
        var replay = await f.Store.SaveAsync(captured, lines, 0, ct: _ct);
        Assert.True(ShotDubbing.Same(saved, replay)); Assert.Equal(1, saved.Version);
        var document = await f.Store.LoadAsync(f.Project, _ct);
        Assert.Single(document.Variants); Assert.Equal(1, document.Revision);
        Assert.Equal(before, TestProjectDatabase.Stamp(dir, "shots"));
        Assert.False(File.Exists(Path.Combine(dir, "production.json")));
        Assert.False(Directory.Exists(Path.Combine(dir, "shots", "runs")));
    }
    [Fact]
    public async Task CompetingTranslationDraftDoesNotOverwriteSavedDialogue()
    {
        var f = await SourceFixture();
        var first = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "", _ct);
        var other = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "", _ct);
        await f.Store.SaveAsync(first, ShotDubbingTests.Translation(first), 0, ct: _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Store.SaveAsync(other, ShotDubbingTests.Translation(other), 0, ct: _ct));
        Assert.Equal(first.VariantId, Assert.Single((await f.Store.LoadAsync(f.Project, _ct)).Variants).Id);
    }
    [Fact]
    public async Task LanguageChangesRejectOldReviewAndRemovingSourceRetainsSavedTranslation()
    {
        var f = await SourceFixture();
        var r = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "", _ct);
        await f.Store.SaveAsync(r, ShotDubbingTests.Translation(r), 0, ct: _ct);
        var p = await f.Store.LanguagesAsync(f.Project, _ct);
        await f.Store.SaveLanguagesAsync(p with { TranslationNotes = "New glossary" }, p.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.SaveAsync(r, ShotDubbingTests.Translation(r), 1, ct: _ct));
        var dir = await f.Files.DirectoryAsync(f.Project, _ct);
        await FileShotStore.Documents.SaveAsync(dir, f.Project, new ShotDocument { ProjectId = f.Project, Shots = [f.Take.Snapshot.Shot] }, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.SourceAsync(f.Project, f.Take.Id, _ct));
        Assert.Single((await f.Store.LoadAsync(f.Project, _ct)).Variants);
    }
    [Fact]
    public async Task EditingOnlyTheStoredPromptIsRejectedOnReload()
    {
        var f = await SourceFixture(); var r = await f.Store.CaptureAsync(f.Project, f.Take.Id, "sv", "", _ct);
        await f.Store.SaveAsync(r, ShotDubbingTests.Translation(r), 0, ct: _ct);
        var d = await f.Store.LoadAsync(f.Project, _ct);
        d = d with { Variants = [d.Variants[0] with { Prompt = d.Variants[0].Prompt.Replace("Locked camera.", "Pan right.") }] };
        var dir = await f.Files.DirectoryAsync(f.Project, _ct);
        await FileProjectDubbingStore.Documents.SaveAsync(dir, f.Project, d, _ct); var before = TestProjectDatabase.Stamp(dir, "dubbing");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.LoadAsync(f.Project, _ct));
        Assert.Equal(before, TestProjectDatabase.Stamp(dir, "dubbing"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PreparedPixelsAndVoiceBytesAreCopiedExactlyOrFailOnCorruption(bool corrupt)
    {
        var (snapshot, r) = ShotDubbingTests.Fixture(media: true);
        byte[] pixels = [1, 2, 3, 4]; byte[] voice = [5, 6, 7, 8, 9];
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        var images = new[] { new CompositionInput(snapshot.Shot.Images[0].Id, Hash(pixels)) };
        snapshot = snapshot with { Production = snapshot.Production! with { Images = images, Revision = snapshot.Production.Revision with { Images = images } } };
        r = r with { SourceFingerprint = ShotDubbing.Hash(snapshot), ReferenceFingerprint = ShotDubbing.References(snapshot) };
        var source = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot,
            [new("reference.png", false, pixels.Length, Hash(pixels)), new("voice.wav", true, voice.Length, Hash(voice))]);
        var dub = source with { BatchId = Guid.NewGuid(), Snapshot = ShotDubbing.Apply(snapshot, ShotDubbingTests.Variant(r)), Inputs = ShotCopy.Of(source.Inputs) };
        AiVideoJobPolicy.Validate(source); AiVideoJobPolicy.Validate(dub);
        var sourceDir = Path.Combine(_root, "source"); var targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(sourceDir, "inputs"));
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "inputs", "reference.png"), corrupt ? [99] : pixels, _ct);
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "inputs", "voice.wav"), voice, _ct);
        if (corrupt) {
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => AiVideoJobPolicy.CopyPreparedInputsAsync(source, sourceDir, dub, targetDir, _ct));
            Assert.False(Directory.Exists(Path.Combine(targetDir, "inputs"))); return;
        }
        await AiVideoJobPolicy.CopyPreparedInputsAsync(source, sourceDir, dub, targetDir, _ct);
        Assert.Equal(pixels, await File.ReadAllBytesAsync(Path.Combine(targetDir, "inputs", "reference.png"), _ct));
        Assert.Equal(voice, await File.ReadAllBytesAsync(Path.Combine(targetDir, "inputs", "voice.wav"), _ct));
        Assert.Equal(source.Inputs, dub.Inputs);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => AiVideoJobPolicy.CopyPreparedInputsAsync(source, sourceDir, dub, targetDir, _ct));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PortableExportDoesNotSilentlyOmitLanguageMetadata(bool configured)
    {
        var f = await StoreFixture(); var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        if (configured) {
            await f.Store.SaveLanguagesAsync(new() { ProjectId = f.Project.Id, Main = ShotDubbingTests.English }, 0, _ct);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => FileProjectDubbingStore.CheckPortableExportAsync(dir, f.Project.Id, _ct));
        }
        else {
            await FileProjectDubbingStore.CheckPortableExportAsync(dir, f.Project.Id, _ct);
            Assert.False(File.Exists(Path.Combine(dir, "languages.json")));
            Assert.False(File.Exists(Path.Combine(dir, "dubbing.json")));
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
