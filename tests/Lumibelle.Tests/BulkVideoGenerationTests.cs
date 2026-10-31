using System.Collections;
using System.Reflection;
using System.Text.Json;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkGenerationCapturesOverridesWithoutChangingSavedSetup(bool upscale)
    {
        var f = Fixture(); var (source, scripts) = ApprovedShot(f.Project.Id);
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock,
            new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = H3Policy.Compile(source);
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var before = JsonSerializer.Serialize(c, AtomicJsonFile.Options);
        var settings = new FakeAiSettingsStore(); settings.Value.H3.LatentUpscaler = "mock-h3-3d.safetensors";
        var capture = new AiVideoJobCapture(f.Shots, scripts, f.Assets, settings,
            new MockVideoGenerator(f.Assets, f.Shots), projects, new FakeProjectAiPreferencesStore(), store);

        var submission = await capture.CaptureCompositionWithPresetAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
            c.Id, c.Version, 3, 123, "turbo4", upscale ? null : VideoResolution.Native, upscale, _ct, c.GenerationSetupVersion);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        AiVideoJobPolicy.Validate(request);
        Assert.Equal("turbo4", request.Snapshot.Preset!.Key);
        Assert.Equal(upscale, request.Snapshot.Shot.UpscalePreview);
        Assert.Equal(upscale ? VideoResolution.Preview : VideoResolution.Native, VideoResolutions.Selected(request.Snapshot.Shot));
        Assert.Equal(c.Prompt, request.Snapshot.Prompt);
        Assert.Equal(new long[] { 123, 124, 125 }, submission.Batch!.Candidates.Select(x => x.Seed));
        Assert.Equal(before, JsonSerializer.Serialize((await store.LoadAsync(f.Project.Id, _ct)).Compositions[0], AtomicJsonFile.Options));
    }

    [Fact]
    public async Task BulkEnqueueRetryKeepsTheCapturedRequestAndBlocksClosingUntilAcknowledged()
    {
        var f = Fixture(); var (source, scripts) = ApprovedShot(f.Project.Id);
        var document = await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "bulk-jobs"), _clock);
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await production.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = H3Policy.Compile(source);
        var saved = await production.SaveAsync(f.Project.Id, c, c.Version, _ct); c = saved.Compositions[0];
        var settings = new FakeAiSettingsStore();
        var requests = new List<AiJobSubmission>();
        async Task<AiJobHeader> Enqueue(AiJobSubmission request, CancellationToken ct)
        {
            requests.Add(request);
            var result = await jobs.EnqueueAsync(request, ct);
            if (requests.Count == 1) throw new WorkspaceStoreException("Saved, but acknowledgement was lost.");
            return result;
        }
        var uncertainStore = AiTextRepairTests.Proxy<IAiJobStore>((method, args) => method.Name == nameof(IAiJobStore.EnqueueAsync)
            ? Enqueue((AiJobSubmission)args![0]!, (CancellationToken)args[1]!) : method.Invoke(jobs, args));
        var handler = AiTextRepairTests.Proxy<IAiJobHandler>((method, _) => method.Name == "get_Kinds"
            ? new[] { AiJobKind.Video } : throw new InvalidOperationException("The test must not execute remote work."));
        using var queue = new AiJobCoordinator(uncertainStore, settings, [handler], _clock, NullLogger<AiJobCoordinator>.Instance);
        var studio = new ProductionStudio();
        SetBulk(studio, "Id", f.Project.Id); SetBulk(studio, "AiJobs", queue);
        SetBulk(studio, "AiReviews", new ScriptReviewGate());
        SetBulk(studio, "VideoRequests", new AiVideoJobCapture(f.Shots, scripts, f.Assets, settings,
            new MockVideoGenerator(f.Assets, f.Shots), projects, new FakeProjectAiPreferencesStore(), production));
        SetBulk(studio, "_doc", document); SetBulk(studio, "_production", saved);
        SetBulk(studio, "_assets", await f.Assets.LoadAsync(f.Project.Id, _ct));
        SetBulk(studio, "_project", f.Project); SetBulk(studio, "_compositionId", c.Id);
        BulkMethod("OpenBulkGenerate").Invoke(studio, null);
        var target = Assert.Single(((IEnumerable)BulkField(studio, "_bulkGenerateTargets")!).Cast<object>());
        Task Queue(string preset) => (Task)BulkMethod("QueueCompositionWithPresetAsync").Invoke(studio, [target, preset, "current", 2])!;

        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Queue("standard"));
        Assert.Single((await jobs.ReadAsync(_ct)).Jobs);
        BulkMethod("CloseBulkGenerate").Invoke(studio, null);
        Assert.True((bool)BulkField(studio, "_bulkGenerateOpen")!);
        Assert.False(await (Task<bool>)BulkMethod("SaveForCloseAsync").Invoke(studio, null)!);

        // Even newer settings cannot alter an already-captured request on acknowledgement retry.
        await Queue("turbo8");
        Assert.Equal(2, requests.Count); Assert.Same(requests[0], requests[1]);
        Assert.Single((await jobs.ReadAsync(_ct)).Jobs);
        Assert.Equal("standard", requests[1].Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Preset!.Key);
        BulkMethod("CloseBulkGenerate").Invoke(studio, null);
        Assert.False((bool)BulkField(studio, "_bulkGenerateOpen")!);
    }

    private static readonly BindingFlags BulkFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static MethodInfo BulkMethod(string name) => typeof(ProductionStudio).GetMethod(name, BulkFlags)!;
    private static object? BulkField(ProductionStudio studio, string name) => typeof(ProductionStudio).GetField(name, BulkFlags)!.GetValue(studio);
    private static void SetBulk(ProductionStudio studio, string name, object value)
    {
        if (typeof(ProductionStudio).GetField(name, BulkFlags) is { } field) field.SetValue(studio, value);
        else typeof(ProductionStudio).GetProperty(name, BulkFlags)!.SetValue(studio, value);
    }
}
