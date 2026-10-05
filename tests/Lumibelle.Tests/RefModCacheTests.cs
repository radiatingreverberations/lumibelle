using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private const string CacheServerA = "http://cache-a:8188";
    private const string CacheServerB = "http://cache-b:8188";
    private sealed record CacheFixture(Guid Project, ProjectFiles Files, FileShotStore Shots,
        ReelRefModStore Store, ShotVideoBinding Binding, VideoSnapshot Snapshot,
        FileAiJobStore Jobs, AiJobContext Context, CacheTransport Http, ComfyRefModCache Cache,
        IReadOnlyList<byte[]> Pixels)
    {
        public string Scope => ComfyMultiTakeWorkflow.CandidateOperation(Context.Job.Batch!.Candidates[0]);
    }
    private async Task<CacheFixture> CacheSetup(bool otherServer = false, int count = 2)
    {
        var f = Fixture(); var store = new ReelRefModStore(f.Files); var binding = RefModBinding(count);
        List<byte[]> pixels = [];
        for (var i = 0; i < count; i++)
        {
            using var image = new Image<Rgb24>(640, 640, new Rgb24((byte)(30 + i * 10), 80, 120));
            using var output = new MemoryStream(); await image.SaveAsPngAsync(output, _ct); pixels.Add(output.ToArray());
        }
        var recipe = ReelRefMods.Recipe(binding, 640, 640, new H3Settings().VideoVae,
            pixels.Select(p => Convert.ToHexString(SHA256.HashData(p))).ToArray());
        var built = RefModRequest(binding, f.Project.Id) with { Recipe = recipe, ComfyUrl = CacheServerA };
        var input = Path.Combine(await store.BuildDirectoryAsync(f.Project.Id, built.JobId, _ct), "inputs");
        Directory.CreateDirectory(input);
        for (var i = 0; i < count; i++) await File.WriteAllBytesAsync(Path.Combine(input, ReelRefModStore.FrameName(i)), pixels[i], _ct);
        binding.RefMod = ComfyRefModClient.ReadOutput(RefModOutput(built), built);
        await store.PublishAsync(built, binding.RefMod, _ct);
        var shot = Ready() with { Videos = [binding] };
        var snapshot = Snapshot(f.Project.Id, shot) with { ComfyUrl = CacheServerA, TargetComfyUrl = otherServer ? CacheServerB : null };
        var jobs = new FileAiJobStore(Path.Combine(_root, "cache-jobs", Guid.NewGuid().ToString("N")), TimeProvider.System);
        var id = Guid.NewGuid();
        await jobs.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI,
            new(f.Project.Id, ShotId: shot.Id), "Test", "Video", Guid.NewGuid(), new { captured = snapshot })
            with { Batch = AiBatchDefinition.Create(id, 1, 42) }, _ct);
        var claimed = (await jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        var context = new AiJobContext(claimed, false, jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var http = new CacheTransport();
        var cache = new ComfyRefModCache(store, new(http), new(http));
        return new(f.Project.Id, f.Files, f.Shots, store, snapshot.Shot.Videos[0], snapshot, jobs, context, http, cache, pixels);
    }

    [Fact]
    public async Task RefModCacheHitDoesNotUploadOrBuild()
    {
        var f = await CacheSetup(); using var http = f.Http; http.Add(f.Binding.RefMod!);
        var resolved = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        Assert.Equal(f.Binding.RefMod!.FileName, resolved[f.Binding.Id].FileName);
        Assert.Equal(0, http.Builds); Assert.Empty(http.Uploaded);
        Assert.Empty((await f.Context.ExecutionAsync(_ct)).Submissions);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RefModCacheMissingOrWrongShapeRebuildsExactAcceptedImages(bool wrongShape)
    {
        var f = await CacheSetup(); using var http = f.Http;
        if (wrongShape) { http.Add(f.Binding.RefMod!); http.Present[(CacheServerA, f.Binding.RefMod!.FileName)]["t"] = 99; }
        var before = JsonSerializer.Serialize(f.Snapshot, AtomicJsonFile.Options);
        var resolved = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        Assert.Equal(1, http.Builds); Assert.Equal(f.Pixels.Count, http.Uploaded.Count);
        Assert.Equal(f.Binding.RefMod!.Recipe.FrameHashes, http.Uploaded.Values.Select(p => Convert.ToHexString(SHA256.HashData(p))));
        Assert.NotEqual(f.Binding.RefMod.FileName, resolved[f.Binding.Id].FileName);
        Assert.Equal(CacheServerA, resolved[f.Binding.Id].ComfyUrl);
        Assert.Equal(before, JsonSerializer.Serialize(f.Snapshot, AtomicJsonFile.Options));
        Assert.False((await f.Context.ExecutionAsync(_ct)).MayBeRunning);
        Assert.Equal(resolved[f.Binding.Id].FileName, (await f.Store.FindAsync(f.Project, f.Binding.RefMod.Recipe.Key, CacheServerA, _ct))!.FileName);
    }
    [Fact]
    public async Task RefModCacheMovedServerNeverContactsOriginalServer()
    {
        var f = await CacheSetup(true); using var http = f.Http; http.Add(f.Binding.RefMod!);
        var resolved = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        Assert.Equal(CacheServerB, resolved[f.Binding.Id].ComfyUrl); Assert.Equal(1, http.Builds);
        Assert.All(http.Requests, r => Assert.Equal(CacheServerB, r.Server));
        Assert.Equal(CacheServerA, f.Binding.RefMod!.ComfyUrl);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(f.Snapshot, 42, "client", [], resolved)).GetProperty("prompt");
        using var state = JsonDocument.Parse(graph.GetProperty("refmods").GetProperty("inputs").GetProperty("stack_state").GetString()!);
        Assert.Equal(resolved[f.Binding.Id].FileName, state.RootElement.GetProperty("picks")[0].GetProperty("visual").GetProperty("file").GetString());
    }
    [Fact]
    public async Task RefModCacheOneMoreReusesCacheButRebuildsAfterDeletion()
    {
        var f = await CacheSetup(); using var http = f.Http;
        var first = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        var second = await f.Cache.EnsureAsync(f.Context, f.Snapshot, "candidate/" + Guid.NewGuid(), _ct);
        Assert.Equal(1, http.Builds); Assert.Equal(first[f.Binding.Id].FileName, second[f.Binding.Id].FileName);
        http.Present.Clear();
        var third = await f.Cache.EnsureAsync(f.Context, f.Snapshot, "candidate/" + Guid.NewGuid(), _ct);
        Assert.Equal(2, http.Builds); Assert.NotEqual(first[f.Binding.Id].FileName, third[f.Binding.Id].FileName);
        Assert.Equal(f.Binding.RefMod!.Recipe.Key, third[f.Binding.Id].Recipe.Key);
    }
    [Fact]
    public async Task RefModCacheTwoTakesShareOnePreparedReferenceWithoutChangingPrompt()
    {
        var f = await CacheSetup(true, 6); using var http = f.Http;
        var prompt = f.Snapshot.Prompt; var fingerprint = f.Snapshot.Fingerprint;
        var resolved = await f.Cache.EnsureAsync(f.Context, f.Snapshot, ComfyMultiTakeWorkflow.Operation, _ct);
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), 2, 42).Candidates;
        var graph = JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(f.Snapshot, c.Seed, c.Id.ToString("D"), [], resolved), "client")).GetProperty("prompt");
        Assert.Equal(1, http.Builds);
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.LoadNode);
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.BuildNode);
        Assert.Equal(prompt, f.Snapshot.Prompt); Assert.Equal(fingerprint, f.Snapshot.Fingerprint);
        Assert.Equal("<Video 1>", ResolvedReferences.For(f.Snapshot.Shot).Label(f.Binding));
        Assert.All(candidates, c => Assert.Equal(2, graph.GetProperty(ComfyMultiTakeWorkflow.Node(c, "10")).GetProperty("inputs").GetProperty("latent_image")[1].GetInt32()));
    }
    [Theory] [InlineData("missing")] [InlineData("changed")]
    public async Task RefModCacheMissingOrChangedSourcesStopBeforeAnyNetwork(string problem)
    {
        var f = await CacheSetup(); using var http = f.Http;
        var file = Path.Combine(await f.Files.DirectoryAsync(f.Project, _ct), "refmod-previews", f.Binding.RefMod!.Recipe.Key, ReelRefModStore.FrameName(0));
        if (problem == "missing") File.Delete(file); else await File.WriteAllBytesAsync(file, [1, 2, 3], _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        Assert.Empty(http.Requests); Assert.Empty((await f.Context.ExecutionAsync(_ct)).Submissions);
    }
    [Fact]
    public async Task RefModCacheDifferentVaeIsNotSilentlySubstituted()
    {
        var f = await CacheSetup(); using var http = f.Http;
        var snapshot = f.Snapshot with { Settings = f.Snapshot.Settings with { VideoVae = "different.safetensors" } };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Cache.EnsureAsync(f.Context, snapshot, f.Scope, _ct));
        Assert.Empty(http.Requests);
    }
    [Fact]
    public async Task RefModCacheUnreadableRemoteLibraryIsNotAnAutomaticCacheMiss()
    {
        var f = await CacheSetup(); using var http = f.Http; http.FailLibrary = true;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        Assert.Equal(0, http.Builds); Assert.Empty(http.Uploaded);
    }
    [Fact]
    public async Task RefModCacheCorruptLocalHintIsDisposableButSourcesRemainRequired()
    {
        var f = await CacheSetup(); using var http = f.Http;
        var directory = Path.Combine(await f.Files.DirectoryAsync(f.Project, _ct), "refmod-previews", f.Binding.RefMod!.Recipe.Key);
        var receipt = Directory.GetFiles(directory, "server-*.json").Single();
        await File.WriteAllTextAsync(receipt, "not json", _ct);
        var result = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        Assert.Equal(1, http.Builds); Assert.Equal(f.Binding.RefMod.Recipe.Key, result[f.Binding.Id].Recipe.Key);
        Assert.Equal(f.Pixels[0], await f.Store.PreviewAsync(f.Project, f.Binding.RefMod, 0, _ct));
    }
    [Fact]
    public async Task RefModCacheRecoveryCannotEnterEnsurePath()
    {
        var f = await CacheSetup(); using var http = f.Http;
        var recovery = new AiJobContext(f.Context.Job, true, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Cache.EnsureAsync(recovery, f.Snapshot, f.Scope, _ct));
        Assert.Empty(http.Requests);
        Assert.False(await f.Cache.RecoverAsync(recovery, f.Snapshot, _ct));
    }
    [Fact]
    public async Task RefModCacheLostAcknowledgementRecoversOneBuildWithoutSourcesOrMorePosts()
    {
        var f = await CacheSetup(true); using var http = f.Http; http.LoseNextReply = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        Assert.Equal(1, http.Builds); Assert.True((await f.Context.ExecutionAsync(_ct)).MayBeRunning);
        var source = Path.Combine(await f.Files.DirectoryAsync(f.Project, _ct), "refmod-previews", f.Binding.RefMod!.Recipe.Key);
        Directory.Delete(source, true); http.Present.Clear(); // Completed output retrieval must not need this cache.
        var before = http.Requests.Count;
        var recovery = new AiJobContext(f.Context.Job, true, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        Assert.True(await f.Cache.RecoverAsync(recovery, f.Snapshot, _ct));
        Assert.Equal(1, http.Builds); Assert.False((await recovery.ExecutionAsync(_ct)).MayBeRunning);
        Assert.All(http.Requests.Skip(before), r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(http.Requests.Skip(before), r => r.Path.Contains("refmods") || r.Path.Contains("object_info"));
    }
    [Fact]
    public async Task RefModCacheRecoveredBuildCanContinueInFreshExecutionWithoutReencoding()
    {
        var f = await CacheSetup(true); using var http = f.Http; http.LoseNextReply = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        var recovery = new AiJobContext(f.Context.Job, true, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        Assert.True(await f.Cache.RecoverAsync(recovery, f.Snapshot, _ct));
        var resolved = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        Assert.Equal(1, http.Builds); Assert.Equal(f.Pixels.Count, http.Uploaded.Count);
        Assert.Equal(CacheServerB, resolved[f.Binding.Id].ComfyUrl);
    }
    [Fact]
    public async Task RefModCacheParentCancellationUsesOwnedExecutionServerAndNeverRebuilds()
    {
        var f = await CacheSetup(true); using var http = f.Http; http.LoseNextReply = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        var handler = new AiVideoJobHandler(f.Shots, null!, new CacheRecoveryAdapter(), http, new(http), TimeProvider.System);
        Assert.True(await handler.CancelRemoteAsync(f.Context, default, _ct));
        Assert.Equal(1, http.Builds); Assert.False((await f.Context.ExecutionAsync(_ct)).MayBeRunning);
        Assert.All(http.Requests, r => Assert.Equal(CacheServerB, r.Server));
        Assert.DoesNotContain(http.Requests, r => r.Path == "/interrupt");
    }
    [Fact]
    public async Task RefModCacheAcceptedOperationIsNotRebuiltWhenItsFileVanishes()
    {
        var f = await CacheSetup(); using var http = f.Http;
        await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct); http.Present.Clear();
        var exception = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        Assert.Equal(AiJobRecovery.GenerateAgain, exception.Recovery); Assert.Equal(1, http.Builds);
    }
    [Fact]
    public async Task RefModCacheWrongExecutionServerAndWrongRecipeCannotReachGraph()
    {
        var f = await CacheSetup(true); using var http = f.Http;
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(f.Snapshot, 42, "client", []));
        var resolved = await f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct);
        var wrong = new Dictionary<Guid, ReelRefModReference>(resolved) { [f.Binding.Id] = f.Binding.RefMod! };
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(f.Snapshot, 42, "client", [], wrong));
        var recipe = ReelRefMods.Recipe(f.Binding, 480, 832, f.Binding.RefMod!.Recipe.VaeName, f.Binding.RefMod.Recipe.FrameHashes);
        wrong[f.Binding.Id] = resolved[f.Binding.Id] with { Recipe = recipe };
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(f.Snapshot, 42, "client", [], wrong));
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(f.Snapshot, 42, "client", [], new Dictionary<Guid, ReelRefModReference>()));
    }
    [Fact]
    public void RefModCacheManualRebuildDoesNotChangeAcceptedBindingOrFingerprint()
    {
        var binding = RefModBinding(); var shot = Ready() with { Videos = [binding] };
        var before = JsonSerializer.Serialize(shot, AtomicJsonFile.Options); var fingerprint = H3Policy.Fingerprint(shot);
        var built = Guid.NewGuid(); var next = binding.RefMod! with { ComfyUrl = CacheServerB,
            BuildId = built, FileName = ReelRefMods.BuildStem(Guid.NewGuid(), built) };
        Assert.False(ReelRefMods.Attach(binding, next));
        Assert.Equal(before, JsonSerializer.Serialize(shot, AtomicJsonFile.Options));
        Assert.Equal(fingerprint, H3Policy.Fingerprint(shot));
    }
    [Fact]
    public void RefModCacheInitialAttachmentAndChangedCanvasRemainExplicitAuthoringChanges()
    {
        var binding = RefModBinding(); var first = binding.RefMod!; binding.RefMod = null;
        Assert.True(ReelRefMods.Attach(binding, first));
        var changed = first with { Recipe = ReelRefMods.Recipe(binding, 480, 832, first.Recipe.VaeName, first.Recipe.FrameHashes) };
        Assert.True(ReelRefMods.Attach(binding, changed)); Assert.Equal(480, binding.RefMod!.Recipe.Width);
        binding.Keyframes!.Frames.Reverse();
        Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.Attach(binding, changed));
    }
    [Fact]
    public void RefModCacheBuildNamesAreStableOnlyWithinOneCapturedOperation()
    {
        var reference = RefModBinding().RefMod!; var job = Guid.NewGuid(); var project = Guid.NewGuid();
        var a = ComfyRefModCache.BuildPlan(job, project, "candidate/1", CacheServerA, reference.Recipe);
        var same = ComfyRefModCache.BuildPlan(job, project, "candidate/1", CacheServerA + "/", reference.Recipe);
        Assert.Equal(a.Reference.BuildId, same.Reference.BuildId);
        Assert.NotEqual(a.Reference.BuildId, ComfyRefModCache.BuildPlan(Guid.NewGuid(), project, a.Scope, CacheServerA, reference.Recipe).Reference.BuildId);
        Assert.NotEqual(a.Reference.BuildId, ComfyRefModCache.BuildPlan(job, project, "candidate/2", CacheServerA, reference.Recipe).Reference.BuildId);
        Assert.NotEqual(a.Reference.BuildId, ComfyRefModCache.BuildPlan(job, project, a.Scope, CacheServerB, reference.Recipe).Reference.BuildId);
    }

    private async Task<(AiVideoJobRequest Request, AiJobContext Context, FileAiJobStore Jobs)> CacheVideoJob(CacheFixture f)
    {
        var size = VideoResolutions.Size(f.Snapshot.Shot);
        var snapshot = f.Snapshot with { Width = size.Width, Height = size.Height,
            Sampling = H3Policy.Sampling(f.Snapshot.Shot, f.Snapshot.Settings), Profile = H3Policy.Profile };
        var id = Guid.NewGuid(); var request = new AiVideoJobRequest(2, id, snapshot, []);
        AiVideoJobPolicy.Validate(request);
        var jobs = new FileAiJobStore(Path.Combine(_root, "cache-video-jobs", id.ToString("N")), TimeProvider.System);
        await jobs.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI,
            AiVideoJobHandler.Target(snapshot), "Test", "Video", Guid.NewGuid(), request)
            with { Batch = AiBatchDefinition.Create(id, 1, 42) }, _ct);
        var claimed = (await jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        return (request, new(claimed, false, jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct), jobs);
    }
    private async Task SaveCacheTestOperation(AiJobContext context, string operation, string server, object workflow, JsonElement output)
    {
        var client = Guid.NewGuid().ToString("D");
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request,
            new ComfySavedOperation(server, client, JsonSerializer.SerializeToElement(workflow), ComfyRefModClient.BuildOptions), _ct);
        await context.BeginRemoteAsync(operation, server, client);
        await context.AcceptRemoteAsync(operation, Guid.NewGuid().ToString("D"));
        await context.SaveOperationAsync(operation, AiOperationArtifact.Output, output, _ct);
    }
    [Fact]
    public async Task RefModCacheCompletedVideoRecoveryBypassesAllCachePreparation()
    {
        var f = await CacheSetup(true); using var http = f.Http; var job = await CacheVideoJob(f);
        var operation = ComfyMultiTakeWorkflow.CandidateOperation(job.Context.Job.Batch!.Candidates[0]);
        await SaveCacheTestOperation(job.Context, operation, job.Request.Snapshot.ExecutionComfyUrl,
            new { prompt = new { saved = "already submitted" } }, JsonSerializer.SerializeToElement(new { outputs = new { } }));
        Directory.Delete(Path.Combine(await f.Files.DirectoryAsync(f.Project, _ct), "refmod-previews"), true);
        var adapter = new CacheRecoveryAdapter();
        var handler = new AiVideoJobHandler(f.Shots, null!, adapter, http, new(http), TimeProvider.System);
        var recovered = new AiJobContext(job.Context.Job, true, job.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => handler.RecoverAsync(recovered,
            JsonSerializer.SerializeToElement(job.Request, AtomicJsonFile.Options), _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery);
        Assert.Equal("Saved-output download reached", failure.InnerException!.Message);
        Assert.Equal(1, adapter.Downloads); Assert.Equal(0, adapter.Preparations); Assert.Empty(http.Requests);
    }
    [Fact]
    public async Task RefModCacheParentRecoveryFinishesAcceptedCacheButDoesNotStartVideo()
    {
        var f = await CacheSetup(true); using var http = f.Http; var job = await CacheVideoJob(f);
        var scope = ComfyMultiTakeWorkflow.CandidateOperation(job.Context.Job.Batch!.Candidates[0]);
        var plan = ComfyRefModCache.BuildPlan(job.Context.Job.Id, f.Project, scope, CacheServerB, f.Binding.RefMod!.Recipe);
        var operation = ComfyRefModCache.Operation(job.Context.Job.Id, scope, CacheServerB, plan.Reference.Recipe.Key);
        await job.Context.SaveOperationAsync(operation + "/source", AiOperationArtifact.Request, plan, _ct);
        var output = JsonSerializer.SerializeToElement(new { outputs = new { build = new {
            refmod_saved = new[] { plan.Reference.FileName + ".safetensors" } } } });
        await SaveCacheTestOperation(job.Context, operation, CacheServerB, new { prompt = new { saved = "cache build" } }, output);
        var adapter = new CacheRecoveryAdapter(f.Cache);
        var handler = new AiVideoJobHandler(f.Shots, null!, adapter, http, new(http), TimeProvider.System);
        var recovered = new AiJobContext(job.Context.Job, true, job.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var result = await handler.RecoverAsync(recovered, JsonSerializer.SerializeToElement(job.Request, AtomicJsonFile.Options), _ct);
        Assert.Equal(AiJobState.Completed, result.State); Assert.Equal(0, result.CompletedCandidates);
        // This checkpoint lets the existing coordinator use a new non-recovery
        // context for never-submitted candidates; this recovery call made no prompt.
        Assert.Equal(1, adapter.Preparations); Assert.Equal(0, adapter.Downloads); Assert.Empty(http.Requests);
        Assert.Single((await recovered.ExecutionAsync(_ct)).Submissions);
        Assert.False((await recovered.ExecutionAsync(_ct)).MayBeRunning);
    }
    [Fact]
    public async Task RefModCacheReceiptWriteFailureRecoversWithoutAnotherBuild()
    {
        var f = await CacheSetup(true); using var http = f.Http;
        var directory = Path.Combine(await f.Files.DirectoryAsync(f.Project, _ct), "refmod-previews", f.Binding.RefMod!.Recipe.Key);
        var receipt = Path.Combine(directory, "server-" + ReelRefMods.Digest(CacheServerB) + ".json");
        Directory.CreateDirectory(receipt); // Blocks the atomic rename, not source reading.
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Cache.EnsureAsync(f.Context, f.Snapshot, f.Scope, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery); Assert.Equal(1, http.Builds);
        Directory.Delete(receipt);
        var recovered = new AiJobContext(f.Context.Job, true, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var before = http.Requests.Count;
        Assert.True(await f.Cache.RecoverAsync(recovered, f.Snapshot, _ct));
        Assert.Equal(1, http.Builds); Assert.Equal(before, http.Requests.Count);
        Assert.NotNull(await f.Store.FindAsync(f.Project, f.Binding.RefMod.Recipe.Key, CacheServerB, _ct));
    }
    private sealed class CacheRecoveryAdapter(ComfyRefModCache? cache = null) : IComfyVideoJobAdapter
    {
        public int Preparations, Downloads;
        public ComfyExecutionOptions Options => ComfyRefModClient.BuildOptions;
        public Task ValidateAsync(AiVideoJobRequest request, CancellationToken ct) => throw new InvalidOperationException("Recovery must not preflight new inference.");
        public Task<Func<string, object>> PrepareWorkflowAsync(AiVideoJobRequest request, AiBatchCandidate candidate, string directory, CancellationToken ct) =>
            throw new InvalidOperationException("Recovery must not build a video workflow.");
        public Task<bool> RecoverPreparationAsync(AiJobContext context, AiVideoJobRequest request, CancellationToken ct)
        {
            Preparations++;
            return (cache ?? throw new InvalidOperationException("Completed-video retrieval must not touch reference caches."))
                .RecoverAsync(context, request.Snapshot, ct);
        }
        public Task<ShotTake> DownloadAsync(AiVideoJobRequest request, VideoCandidate candidate, string directory, Func<string, Task> progress, CancellationToken ct)
        { Downloads++; throw new WorkspaceStoreException("Saved-output download reached"); }
    }

    // Real byte preparation/validation, durable journal and cache service. HTTP and
    // GPU execution are fakes; these tests do not assert generation fidelity or timing.
    private sealed class CacheTransport : HttpMessageHandler, IHttpClientFactory, IComfyExecutionMonitor
    {
        public List<(HttpMethod Method, string Server, string Path)> Requests { get; } = [];
        public Dictionary<string, byte[]> Uploaded { get; } = [];
        public Dictionary<(string Server, string File), JsonObject> Present { get; } = [];
        public Dictionary<string, JsonElement> History { get; } = [];
        public int Builds;
        public bool LoseNextReply, FailLibrary;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        public void Add(ReelRefModReference reference) => Present[(reference.ComfyUrl, reference.FileName)] =
            JsonNode.Parse(RefModLibrary(reference)["items"]![0]!["visual"]!.ToJsonString())!.AsObject();
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var server = request.RequestUri!.GetLeftPart(UriPartial.Authority); var path = request.RequestUri.AbsolutePath;
            Requests.Add((request.Method, server, path));
            if (request.Method == HttpMethod.Get && path == "/object_info") return Json(Catalog());
            if (request.Method == HttpMethod.Get && path == "/minimax_h3/refmods")
                return FailLibrary ? new(HttpStatusCode.ServiceUnavailable) : Json(new { items = Present.Where(p => p.Key.Server == server).Select(p => new { visual = p.Value }).ToArray() });
            if (request.Method == HttpMethod.Post && path == "/upload/image")
            {
                var parts = Assert.IsType<MultipartFormDataContent>(request.Content);
                var image = parts.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "image");
                var name = image.Headers.ContentDisposition!.FileName!.Trim('"');
                var folder = await parts.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "subfolder").ReadAsStringAsync(ct);
                Uploaded[folder + "/" + name] = await image.ReadAsByteArrayAsync(ct);
                return Json(new { name, subfolder = folder, type = "input" });
            }
            if (request.Method == HttpMethod.Post && path == "/prompt")
            {
                using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = sent.RootElement; var graph = root.GetProperty("prompt");
                Assert.Equal(2, graph.EnumerateObject().Count()); // VAELoader + Fantastic Create; no sampler.
                Assert.Equal(ComfyRefModClient.BuildNode, graph.GetProperty("build").GetProperty("class_type").GetString());
                var input = graph.GetProperty("build").GetProperty("inputs");
                using var sources = JsonDocument.Parse(input.GetProperty("source").GetString()!);
                Assert.All(sources.RootElement.EnumerateArray(), s => Assert.Equal("picture", s.GetProperty("kind").GetString()));
                var size = Image.Identify(Uploaded[sources.RootElement[0].GetProperty("file").GetString()!]);
                var stem = input.GetProperty("subfolder").GetString() + "/" + input.GetProperty("name").GetString();
                Present[(server, stem)] = new() { ["file"] = stem, ["kind"] = "video", ["mode"] = "encode", ["source"] = "stack",
                    ["t"] = sources.RootElement.GetArrayLength(), ["h"] = size.Height / 16, ["w"] = size.Width / 16 };
                var id = Guid.NewGuid().ToString("D");
                History[id] = JsonSerializer.SerializeToElement(new { status = new { completed = true, status_str = "success" },
                    prompt = new object[] { 1, id, graph.Clone(), new { client_id = root.GetProperty("client_id").GetString() }, new[] { "build" } },
                    outputs = new { build = new { refmod_saved = new[] { stem + ".safetensors", stem + ".png" } } } });
                Builds++;
                if (LoseNextReply) { LoseNextReply = false; throw new HttpRequestException("Lost acceptance acknowledgement"); }
                return Json(new { prompt_id = id });
            }
            if (request.Method == HttpMethod.Get && path == "/queue") return Json(new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() });
            if (request.Method == HttpMethod.Get && path == "/history") return Json(History);
            if (request.Method == HttpMethod.Get && path.StartsWith("/history/", StringComparison.Ordinal))
                return Json(History.TryGetValue(path[9..], out var result) ? new Dictionary<string, JsonElement> { [path[9..]] = result } : new());
            throw new InvalidOperationException("Unexpected cache request: " + request.Method + " " + request.RequestUri);
        }
        public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(HttpClient http, string promptId, string clientId,
            ComfyExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested();
            yield return new(new(GenerationPhase.Finalizing, "Fake build complete"), promptId, History[promptId], true);
        }
        public IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
            ComfyExecutionOptions options, CancellationToken operationToken, CancellationToken callerToken) => throw new NotSupportedException();
        private static JsonObject Catalog()
        {
            JsonObject Node(Dictionary<string, string> fields, params string[] outputs) => new() {
                ["input"] = new JsonObject { ["required"] = new JsonObject(fields.Select(f => KeyValuePair.Create<string, JsonNode?>(f.Key, new JsonArray(f.Value)))) },
                ["output"] = new JsonArray(outputs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
            var create = Node(new() { ["name"] = "STRING", ["subfolder"] = "STRING", ["source"] = "STRING", ["mode"] = "STRING",
                ["ref_resolution"] = "INT", ["grid"] = "INT", ["latent_frames"] = "INT", ["refinement_steps"] = "INT", ["max_tokens"] = "INT",
                ["audio_max_seconds"] = "FLOAT", ["concept_type"] = "STRING", ["description"] = "STRING", ["write_preview"] = "BOOLEAN", ["vae"] = "VAE" }, "H3_REF_MODS", "STRING");
            create["input"]!["required"]!["mode"] = new JsonArray(new JsonArray("Full Reference", "Compressed Reference"));
            return new() {
                [ComfyRefModClient.BuildNode] = create,
                [ComfyRefModClient.LoadNode] = Node(new() { ["stack_state"] = "STRING" }, "H3_REF_MODS", "STRING"),
                [ComfyRefModClient.MediaNode] = Node(new() { ["media_state"] = "STRING" }, "H3_REFS"),
                [ComfyRefModClient.EncodeNode] = Node(new() { ["clip"] = "CLIP", ["prompt"] = "STRING", ["width"] = "INT", ["height"] = "INT",
                    ["length"] = "INT", ["ref_image_size"] = "STRING", ["reference_fps"] = "FLOAT", ["max_total_tokens"] = "INT",
                    ["mods"] = "H3_REF_MODS", ["references"] = "H3_REFS", ["vae"] = "VAE", ["audio_vae"] = "VAE" }, "CONDITIONING", "STRING", "LATENT"),
                ["VAELoader"] = new JsonObject { ["input"] = new JsonObject { ["required"] = new JsonObject { ["vae_name"] = new JsonArray(new JsonArray(new H3Settings().VideoVae)) } } }
            };
        }
    }
}
