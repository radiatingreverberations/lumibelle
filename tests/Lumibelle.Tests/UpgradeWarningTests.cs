using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupArchiveWarningsOmitTracesOnlyForUpgradeGates(bool gated)
    {
        var f = Fixture();
        var directory = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [Ready()], 0, ct: _ct);
        if (gated) TestProjectDatabase.Execute(directory, "PRAGMA user_version = 1");
        else TestProjectDatabase.Damage(directory);

        var archives = new UpgradeWarningLogger<LegacyVideoArchiveRecovery>();
        await new LegacyVideoArchiveRecovery(f.Files, f.Shots, archives).StartAsync(_ct);
        var warning = Assert.Single(archives.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(gated, warning.Error is null);
        Assert.Contains(f.Project.Id.ToString(), warning.Message);

        var cleanup = new UpgradeWarningLogger<FrameArchiveCleanupRecovery>();
        using var recovery = new FrameArchiveCleanupRecovery(new FrameArchiveCleanupStore(f.Files, f.Shots), cleanup);
        await recovery.StartAsync(_ct);
        try
        {
            await cleanup.Logged.Task.WaitAsync(TimeSpan.FromSeconds(8), _ct);
            var cleanupWarning = Assert.Single(cleanup.Entries);
            Assert.Equal(LogLevel.Warning, cleanupWarning.Level);
            Assert.Equal(gated, cleanupWarning.Error is null);
        }
        finally { await recovery.StopAsync(CancellationToken.None); }
    }
}

public sealed partial class AiJobCoordinatorTests
{
    [Theory]
    [InlineData("upgrade", false)]
    [InlineData("nested-upgrade", false)]
    [InlineData("mixed", true)]
    [InlineData("unexpected", true)]
    public async Task ObserverWarningsRetainUnexpectedFailuresAndOmitUpgradeTraces(string failure, bool trace)
    {
        var job = await Store.EnqueueAsync(Request(), _ct);
        var handler = new FakeHandler();
        var logger = new UpgradeWarningLogger<AiJobCoordinator>();
        var queue = new AiJobCoordinator(Store, _settings, [handler], TimeProvider.System, logger);
        _coordinators.Add(queue);
        await queue.StartAsync(_ct);
        var invocation = await handler.Next(_ct);
        var upgrade = new ProjectUpgradeRequiredException(_root, 1);
        Exception error = failure switch
        {
            "upgrade" => upgrade,
            "nested-upgrade" => new AggregateException(new AggregateException(upgrade), new ProjectUpgradeRequiredException(_root, 1)),
            "mixed" => new AggregateException(upgrade, new IOException("Unexpected output failure")),
            _ => new IOException("Unexpected output failure")
        };
        invocation.Done.SetException(error);
        await State(job.Id, j => j.State == lumibelle.Models.AiJobState.NeedsAttention);
        await logger.Logged.Task.WaitAsync(TimeSpan.FromSeconds(8), _ct);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(trace, warning.Error is not null);
        if (trace) Assert.Contains(error, Assert.IsType<AggregateException>(warning.Error).InnerExceptions);
    }
}
