using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services;
using lumibelle.Services.Projects;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradingPublishesDeferredLegacyArchivesWithoutRestarting(bool cancelRecovery)
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, shot), Candidates = [new() { Number = 1, Seed = 22, State = VideoCandidateState.Downloading }] };
        var candidate = run.Candidates[0];
        var staging = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, run.Id, _ct), "candidate-1"); Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "video.mp4"), [1, 2, 3], _ct);
        var frames = await MockFrameArchive.WriteAsync(staging, run.Snapshot.FrameCount, 32, 32, _ct);
        candidate.ArchivedTake = new() { Id = candidate.TakeId, ShotId = shot.Id, RunId = run.Id, Candidate = 1, Seed = 22,
            Snapshot = run.Snapshot, Width = 32, Height = 32, Frames = frames, Bytes = 3 + frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) };
        await f.Shots.SaveRunAsync(run, _ct);
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        TestProjectDatabase.Execute(directory, "PRAGMA user_version = 1");
        var recovery = new LegacyVideoArchiveRecovery(f.Files, f.Shots, NullLogger<LegacyVideoArchiveRecovery>.Instance);
        await recovery.StartAsync(_ct);
        Assert.Empty(TestProjectDatabase.Rows(directory, "shots", "takes"));
        var paths = new ApplicationPaths(Path.Combine(_root, "App_Data"), Path.Combine(_root, "projects"));
        var store = System.Reflection.DispatchProxy.Create<IShotStore, InterruptedArchiveStore>();
        var interrupted = (InterruptedArchiveStore)(object)store; interrupted.Inner = f.Shots;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        if (cancelRecovery) interrupted.BeforeRuns = () =>
        {
            Assert.Equal(2, lumibelle.Services.Storage.ProjectFormat.Inspect(directory));
            cancellation.Cancel();
        };
        var upgrades = new ProjectUpgrades(paths, f.Files, f.Files.Locations,
            archives: new LegacyVideoArchiveRecovery(f.Files, store, NullLogger<LegacyVideoArchiveRecovery>.Instance));
        if (cancelRecovery)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upgrades.UpgradeAsync(f.Project.Id, ct: cancellation.Token));
            Assert.Empty(TestProjectDatabase.Rows(directory, "shots", "takes"));
            Assert.False((await upgrades.StatusAsync(f.Project.Id, _ct)).Needed);
        }
        await upgrades.UpgradeAsync(f.Project.Id, ct: _ct);
        Assert.Equal(candidate.TakeId, Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes).Id);
        Assert.Equal(VideoCandidateState.Complete, (await f.Shots.RunsAsync(f.Project.Id, _ct)).Single().Candidates[0].State);
    }

    public class InterruptedArchiveStore : System.Reflection.DispatchProxy
    {
        public IShotStore Inner { get; set; } = null!;
        public Action? BeforeRuns { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IShotStore.RunsAsync) && BeforeRuns is { } before)
            { BeforeRuns = null; before(); }
            return method.Invoke(Inner, args);
        }
    }

    [Fact]
    public async Task EarlierCompletedArchivePublishesOnceAndNeverResurrectsADiscardedTake()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, shot), Candidates = [new() { Number = 1, Seed = 22, State = VideoCandidateState.Downloading }] };
        var candidate = run.Candidates[0];
        var staging = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, run.Id, _ct), "candidate-1"); Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "video.mp4"), [1, 2, 3], _ct);
        var frames = await MockFrameArchive.WriteAsync(staging, run.Snapshot.FrameCount, 32, 32, _ct);
        candidate.ArchivedTake = new() { Id = candidate.TakeId, ShotId = shot.Id, RunId = run.Id, Candidate = 1, Seed = 22,
            Snapshot = run.Snapshot, Width = 32, Height = 32, Frames = frames, Bytes = 3 + frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) };
        await f.Shots.SaveRunAsync(run, _ct);
        var recovery = new LegacyVideoArchiveRecovery(f.Files, f.Shots, NullLogger<LegacyVideoArchiveRecovery>.Instance);
        await recovery.RecoverProjectAsync(f.Project.Id, _ct); await recovery.RecoverProjectAsync(f.Project.Id, _ct);
        var doc = await f.Shots.LoadAsync(f.Project.Id, _ct); var take = Assert.Single(doc.Takes);
        Assert.Equal(candidate.TakeId, take.Id); Assert.Equal(run.Id, take.RunId); Assert.Null(take.AiJobId);
        Assert.Equal(VideoCandidateState.Complete, (await f.Shots.RunsAsync(f.Project.Id, _ct)).Single().Candidates[0].State);
        doc = await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, doc.Revision, _ct);
        // A crash after take publication but before updating the older run still
        // resolves the exact receipt, even after the take has been discarded.
        await f.Shots.SaveRunAsync(run, _ct); await recovery.RecoverProjectAsync(f.Project.Id, _ct);
        doc = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Empty(doc.Takes); Assert.Single(doc.Trash);
    }

    [Fact]
    public async Task EarlierIncompleteOrMismatchedArchiveCannotTriggerGenerationOrPublication()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, shot), Candidates = [
            new() { Number = 1, State = VideoCandidateState.Running, PromptId = Guid.NewGuid().ToString() },
            new() { Number = 2, State = VideoCandidateState.Submitting },
            new() { Number = 3, State = VideoCandidateState.Downloading, ArchivedTake = new() { Snapshot = Snapshot(f.Project.Id, shot) } }] };
        await f.Shots.SaveRunAsync(run, _ct);
        await new LegacyVideoArchiveRecovery(f.Files, f.Shots, NullLogger<LegacyVideoArchiveRecovery>.Instance).RecoverProjectAsync(f.Project.Id, _ct);
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var saved = (await f.Shots.RunsAsync(f.Project.Id, _ct)).Single();
        Assert.Equal(run.Candidates[0].PromptId, saved.Candidates[0].PromptId); Assert.Equal(VideoCandidateState.Running, saved.Candidates[0].State);
        Assert.NotNull(saved.Candidates[2].ArchivedTake);
    }
}
