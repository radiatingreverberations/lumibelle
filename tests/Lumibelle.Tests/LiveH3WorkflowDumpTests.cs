using System.Text.Json;
using lumibelle;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

/// <summary>
/// Manual experiment helpers: write the ComfyUI workflow an H3 take would submit, without submitting it.
/// </summary>
public sealed class LiveH3WorkflowDumpTests
{
    /// <summary>Set LUMIBELLE_LIVE_VIDEO_JOB to a saved video job directory (read only) and LUMIBELLE_LIVE_OUT to the output path.
    /// Inputs are named as UploadAsync names them, so they must be uploaded under lumibelle/ by the caller.</summary>
    [Fact]
    public async Task SavedVideoJobWorkflowIsWritten()
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_VIDEO_JOB");
        var output = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_OUT");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(output)) Assert.Skip("Set LUMIBELLE_LIVE_VIDEO_JOB and LUMIBELLE_LIVE_OUT.");
        var ct = TestContext.Current.CancellationToken;
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "request.json"), ct));
        var request = saved.RootElement.GetProperty("request").Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var inputs = request.Inputs.Select(i => new PreparedVideoInput("lumibelle/h3-" + i.Sha256.ToLowerInvariant()[..32] + Path.GetExtension(i.FileName).ToLowerInvariant(), i.Audio)
            { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        var workflow = ComfyH3Video.BuildWorkflow(request.Snapshot, 1, Guid.NewGuid().ToString("D"), inputs);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    /// <summary>
    /// Replays a saved take's snapshot with its reference videos left out: prepares and uploads its pictures and voices
    /// through the app's own services (read only against LUMIBELLE_LIVE_DATA) and writes the workflow.
    /// Set LUMIBELLE_LIVE_DATA, LUMIBELLE_LIVE_PROJECT, LUMIBELLE_LIVE_TAKE and LUMIBELLE_LIVE_OUT; optionally
    /// LUMIBELLE_LIVE_PROMPT (a prompt file) and LUMIBELLE_LIVE_FRAMES.
    /// </summary>
    [Fact]
    public async Task SavedTakeWorkflowWithoutReelsIsWritten()
    {
        var data = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_DATA");
        var output = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_OUT");
        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(output)) Assert.Skip("Set LUMIBELLE_LIVE_DATA, LUMIBELLE_LIVE_PROJECT, LUMIBELLE_LIVE_TAKE and LUMIBELLE_LIVE_OUT.");
        var ct = TestContext.Current.CancellationToken;
        var project = Guid.Parse(Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PROJECT")!);
        var takeId = Guid.Parse(Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_TAKE")!);
        var services = new ServiceCollection().AddLogging().AddSingleton<lumibelle.Services.AI.ISecretProtector, PlainProtector>().AddLumibelleCore(new ApplicationPaths(data), exclusive: false).BuildServiceProvider();
        await using var _ = services;
        var take = (await services.GetRequiredService<IShotStore>().LoadAsync(project, ct)).Takes.Single(t => t.Id == takeId);
        var snapshot = take.Snapshot with { Shot = take.Snapshot.Shot with { Videos = [] } };
        if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_PROMPT") is { Length: > 0 } prompt) snapshot = snapshot with { Prompt = await File.ReadAllTextAsync(prompt, ct) };
        if (int.TryParse(Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_FRAMES"), out var frames)) snapshot = snapshot with { FrameCount = frames };
        var generator = new ComfyH3Video(services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<lumibelle.Services.AI.IComfyExecutionMonitor>(),
            services.GetRequiredService<lumibelle.Services.Assets.FileAssetStore>(), services.GetRequiredService<lumibelle.Services.Assets.FileAssetStore>(),
            services.GetRequiredService<IShotStore>(), services.GetRequiredService<IProductionMediaTools>());
        var directory = Directory.CreateTempSubdirectory("lumibelle-h3-").FullName;
        var run = new VideoRun { Snapshot = snapshot };
        await generator.PrepareAsync(run, directory, ct);
        var uploaded = await generator.UploadAsync(run, directory, ct);
        var workflow = ComfyH3Video.BuildWorkflow(snapshot, take.Seed, Guid.NewGuid().ToString("D"), uploaded);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
    // Secrets are not needed for a local ComfyUI; nothing is saved.
    private sealed class PlainProtector : lumibelle.Services.AI.ISecretProtector { public string Protect(string value) => value; public string Unprotect(string value) => value; }
}
