using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(ImageWorkflow.Krea2, false)] [InlineData(ImageWorkflow.Krea2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false)] [InlineData(ImageWorkflow.Flux2Klein9bKv, true)]
    public async Task QueuedLoraOrderAndStrengthsRemainExactAcrossCandidates(ImageWorkflow workflow, bool editing)
    {
        using var f = await ImageJobFixture.Create(this, workflow, editing); var ct = TestContext.Current.CancellationToken;
        var first = new LoraReference(f.Settings.Value.ComfyUrl, "characters/mouse.safetensors", workflow, "Mouse");
        var second = first with { FileName = "styles/ink.safetensors", Name = "Ink" };
        var disabled = first with { FileName = "styles/unused.safetensors", Name = "Unused" };
        f.Settings.Value = f.Settings.Value with { LoraLibrary = [new(first), new(second), new(disabled)] };
        var selections = new List<LoraSelection> { new(first, .75f), new(disabled, 1, false), new(second, 1.25f) };
        var request = await f.Capture(2, selections); var context = await f.Claim(request);
        selections.Clear();
        f.Settings.Value = f.Settings.Value with { LoraLibrary = [new(first with { Name = "New friendly name" }, 9), new(second, 9)] };
        await f.Worker.ExecuteAsync(context, request.Snapshot, ct);
        Assert.All(f.Graphs, graph =>
        {
            var nodes = graph.GetProperty("prompt");
            Assert.Equal(first.FileName, nodes.GetProperty("lora_1").GetProperty("inputs").GetProperty("lora_name").GetString());
            Assert.Equal(.75f, nodes.GetProperty("lora_1").GetProperty("inputs").GetProperty("strength_model").GetSingle());
            Assert.Equal(second.FileName, nodes.GetProperty("lora_2").GetProperty("inputs").GetProperty("lora_name").GetString());
            Assert.Equal("lora_1", nodes.GetProperty("lora_2").GetProperty("inputs").GetProperty("model")[0].GetString());
            Assert.False(nodes.TryGetProperty("lora_3", out _));
            if (workflow == ImageWorkflow.Krea2 && editing)
            {
                Assert.Equal(1, nodes.GetProperty("4").GetProperty("inputs").GetProperty("strength_model").GetSingle());
                Assert.Equal("4", nodes.GetProperty("lora_1").GetProperty("inputs").GetProperty("model")[0].GetString());
                Assert.Equal("lora_2", nodes.GetProperty("8").GetProperty("inputs").GetProperty("model")[0].GetString());
            }
            if (workflow == ImageWorkflow.Flux2Klein9bKv)
            {
                var cache = nodes.EnumerateObject().Single(n => n.Value.GetProperty("class_type").GetString() == "FluxKVCache");
                Assert.Equal("lora_2", cache.Value.GetProperty("inputs").GetProperty("model")[0].GetString());
            }
        });
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation is not null);
        Assert.All(images, i => Assert.Equal(new[] { new AppliedLora(first, .75f), new AppliedLora(second, 1.25f) }, i.Generation!.Loras));
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2, false, 8, "9")]
    [InlineData(ImageWorkflow.Krea2, true, 10, "13")]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false, 4, "14")]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, true, 4, "14")]
    public async Task QueuedImagesUseCapturedGraphsInputsAndAttribution(ImageWorkflow workflow, bool editing, int steps, string outputNode)
    {
        using var f = await ImageJobFixture.Create(this, workflow, editing); var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(2); var context = await f.Claim(request);
        var captured = request.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        if (editing)
        {
            Assert.Equal(f.References, captured.Inputs.Select(i => i.Reference));
            Assert.Equal((40, 40), (ImageInspector.Inspect(captured.Inputs[0].Png).Width, ImageInspector.Inspect(captured.Inputs[0].Png).Height));
            Assert.Equal((20, 30), (ImageInspector.Inspect(captured.Inputs[1].Png).Width, ImageInspector.Inspect(captured.Inputs[1].Png).Height));
        }
        f.Settings.Value = f.Settings.Value with { ComfyUrl = "http://another-server.test", ComfyImageModel = "changed.safetensors" };
        var outcome = await f.Worker.ExecuteAsync(context, request.Snapshot, ct);
        Assert.Equal(2, outcome.CompletedCandidates); Assert.Equal(1, f.Posts); Assert.Equal(2, f.Views);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == context.Job.Id).ToArray();
        Assert.Equal(2, images.Length); Assert.All(images, i =>
        {
            Assert.Equal(steps, i.Generation!.Steps); Assert.Equal(workflow, i.Generation.Workflow); Assert.Equal("Original instruction · keep mouse_token", i.Generation.Prompt);
            Assert.Equal(captured.Look, i.Generation.Look); Assert.Equal(request.Id, i.Generation.BatchId); Assert.False(i.IsReference);
        });
        Assert.Equal(new[] { 123L, 124L }, images.Select(i => i.Generation!.Seed));
        Assert.All(f.Graphs, graph =>
        {
            foreach (var candidate in request.Batch!.Candidates)
                Assert.Equal("PreviewImage", graph.GetProperty("prompt").GetProperty(ComfyMultiTakeWorkflow.Node(candidate, outputNode)).GetProperty("class_type").GetString());
            Assert.DoesNotContain("changed.safetensors", graph.GetRawText());
        });
        if (editing) Assert.All(images, i => Assert.Equal(f.References, i.Generation!.Edit!.References));
        Assert.Equal(2, (await context.ReadAsync<AiImageJobResult>(AiJobArtifact.Result, ct))!.Candidates.Count);
        Assert.True((await f.Jobs.ReadAsync(ct)).Jobs[0].Unread);
    }

    [Fact]
    public async Task ImageDownloadRetryReusesReceiptAndCompletedDiscardsNeverRegenerate()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false); var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(); var context = await f.Claim(request); f.FailDownloads = 1;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery); Assert.False((await context.ExecutionAsync(ct)).MayBeRunning);
        var recovered = await f.Recover(context);
        Assert.Equal(1, (await f.Worker.RecoverAsync(recovered, request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, f.Posts); Assert.Equal(2, f.Views);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct); var image = Assert.Single(library.Assets[0].Images, i => i.Generation is not null);
        var trash = await f.Assets.DeleteImageAsync(f.Project.Id, f.AssetId, image.Id, library.Revision, ct);
        await f.Assets.PurgeImagesAsync(f.Project.Id, trash.TrashIds, trash.Library.Revision, ct);
        f.NoNetwork = true;
        Assert.Equal(1, (await f.Worker.RecoverAsync(await f.Recover(recovered), request.Snapshot, ct)).CompletedCandidates);
        Assert.DoesNotContain((await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images, i => i.Generation is not null);
        Assert.Equal(image.Id, Assert.Single((await recovered.ReadAsync<AiImageJobResult>(AiJobArtifact.Result, ct))!.Candidates).ImageId);
    }

    [Fact]
    public async Task ImagePublicationRetryUsesStagedBytesAndPreservesConcurrentDraftEdits()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Flux2Klein9bKv, true); var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(); var context = await f.Claim(request);
        FileStream? locked = null;
        f.OnDownload = () => { locked = new FileStream(Path.Combine(_directory, "projects", f.Project.Id.ToString("D"), "assets.json"), FileMode.Open, FileAccess.Read, FileShare.Read); return Task.CompletedTask; };
        try
        {
            var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct));
            Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery);
        }
        finally { locked?.Dispose(); }
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        await f.Assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == f.AssetId ? a with { Description = "An authored change while saving" } : a).ToList() }, library.Revision, ct);
        f.NoNetwork = true;
        Assert.Equal(1, (await f.Worker.RecoverAsync(await f.Recover(context), request.Snapshot, ct)).CompletedCandidates);
        library = await f.Assets.LoadAsync(f.Project.Id, ct);
        Assert.Equal("An authored change while saving", library.Assets[0].Description); Assert.Equal(1, f.Posts); Assert.Equal(1, f.Views);
        Assert.Equal("Original identity", library.Assets[0].Images.Single(i => i.Generation is not null).Generation!.Look!.IdentityNotes);
    }

    [Fact]
    public async Task ArchivedOrTrashedInputsAndMissingCapabilitiesBlockBeforeSubmission()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, true); var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(); var context = await f.Claim(request);
        f.Adapter.Failure = "Two-image node support is unavailable";
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct)); Assert.Equal(0, f.Posts);
        f.Adapter.Failure = null;
        var library = await f.Assets.LoadAsync(f.Project.Id, ct); var reference = f.References[1];
        var deleted = await f.Assets.DeleteImageAsync(f.Project.Id, reference.AssetId, reference.ImageId, library.Revision, ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ValidateExtensionAsync(context.Job, request.Snapshot, ct)); Assert.Equal(0, f.Posts);
        await f.Assets.RestoreImagesAsync(f.Project.Id, deleted.TrashIds, deleted.Library.Revision, ct);
        Assert.Equal(1, (await f.Worker.ExecuteAsync(context, request.Snapshot, ct)).CompletedCandidates);
    }

    [Fact]
    public async Task ImageBatchRecoveryRetrievesAllSubmittedCandidatesWithoutRepeatingInference()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false); var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(2); var context = await f.Claim(request); f.FailDownloads = 1;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct));
        var recovery = await f.Recover(context);
        Assert.Equal(2, (await f.Worker.RecoverAsync(recovery, request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, f.Posts);
        var normal = f.Context(recovery.Job, false);
        Assert.Equal(2, (await f.Worker.ExecuteAsync(normal, request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, f.Posts); Assert.Equal(2, (await normal.ReadAsync<AiImageJobResult>(AiJobArtifact.Result, ct))!.Candidates.Count);
    }

    [Fact]
    public async Task ImageProgressRecordsActualTakeOrderAcrossRecovery()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false); var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(2); var context = await f.Claim(request);
        await context.ReportAsync(new(new(GenerationPhase.Preparing, "Preparing references"), 1, 2));
        await context.ReportAsync(new(new(GenerationPhase.Generating, "Take 2 · Generating image", 1, 25, "steps"), 2, 2));
        Assert.Equal(new[] { 2 }, (await context.ReadAsync<AiJobProgress>(AiJobArtifact.Progress, ct))!.CandidateExecutionOrder);

        var recovered = f.Context(context.Job, true);
        await recovered.ReportAsync(new(new(GenerationPhase.Generating, "Take 1 · Generating image", 1, 25, "steps"), 1, 2));
        Assert.Equal(new[] { 2, 1 }, (await recovered.ReadAsync<AiJobProgress>(AiJobArtifact.Progress, ct))!.CandidateExecutionOrder);
    }

    private sealed class ImageJobFixture : IDisposable
    {
        private readonly bool _editing;
        private readonly ImageWorkflow _workflow;
        private readonly ScriptedHttpHandler _http;
        private readonly Dictionary<string, string> _history = [];
        public ProjectInfo Project { get; }
        public FileAssetStore Assets { get; }
        public Guid AssetId { get; private set; }
        public List<AssetImageReference> References { get; } = [];
        public FakeAiSettingsStore Settings { get; } = new() { Value = new() { ComfyUrl = "http://comfy.test:8188" } };
        public FileAiJobStore Jobs { get; }
        public ImageAdapter Adapter { get; }
        public AiImageJobHandler Worker { get; }
        public List<JsonElement> Graphs { get; } = [];
        public int Posts, Views, FailDownloads;
        public bool NoNetwork, FailWorkflow;
        public Func<string, bool>? IncludeOutput;
        public List<string> DownloadQueries { get; } = [];
        public Func<Task>? OnDownload;
        public byte[]? OutputPng;
        private CancellationToken Ct => TestContext.Current.CancellationToken;
        private ImageJobFixture(AssetStoreTests owner, ImageWorkflow workflow, bool editing)
        {
            _workflow = workflow; _editing = editing; (Project, Assets) = owner.CreateStore();
            Jobs = new(Path.Combine(owner._directory, "jobs"), TimeProvider.System);
            _http = new(async (request, ct) =>
            {
                if (NoNetwork) throw new InvalidOperationException("Recovery must use local staged bytes");
                Assert.Equal("comfy.test", request.RequestUri!.Host);
                if (request.Method == HttpMethod.Post)
                {
                    Posts++; var graph = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone(); Graphs.Add(graph);
                    var prompt = Guid.NewGuid().ToString("D");
                    _history[prompt] = JsonSerializer.Serialize(new Dictionary<string, object>
                    { [prompt] = ComfyBatchHistory.Job(graph, IncludeOutput, FailWorkflow) });
                    return Json(JsonSerializer.Serialize(new { prompt_id = prompt }));
                }
                if (request.RequestUri.AbsolutePath.StartsWith("/history/")) return Json(_history[request.RequestUri.Segments[^1]]);
                if (request.RequestUri.AbsolutePath == "/view")
                {
                    DownloadQueries.Add(request.RequestUri.Query);
                    Views++; if (FailDownloads-- > 0) return new(HttpStatusCode.ServiceUnavailable);
                    if (OnDownload is not null) await OnDownload();
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(OutputPng ?? Png(1024, 1024)) };
                }
                throw new InvalidOperationException(request.RequestUri.ToString());
            });
            var clients = new TestHttpFactory(_http); var monitor = TestComfy.Monitor();
            var real = new ComfyImageJobAdapter(new(clients, Settings, TimeProvider.System, monitor), new(clients, Settings, TimeProvider.System, monitor),
                new(clients, TimeProvider.System, monitor), clients, Settings, new FakeProjectAiPreferencesStore());
            Adapter = new(real, workflow is ImageWorkflow.MossImage or ImageWorkflow.Flux3ImageKlein or ImageWorkflow.Fibo2 or ImageWorkflow.ZImage ? "9" : workflow == ImageWorkflow.Flux2Klein9bKv ? "14" : editing ? "13" : "9");
            Worker = new(Assets, Adapter, clients, new(monitor), TimeProvider.System);
        }
        public static async Task<ImageJobFixture> Create(AssetStoreTests owner, ImageWorkflow workflow, bool editing)
        {
            var f = new ImageJobFixture(owner, workflow, editing); var asset = owner.Asset("Juniper", "Original identity"); f.AssetId = asset.Id;
            var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, f.Ct);
            foreach (var size in new[] { (80, 40), (20, 60) })
            {
                using var bytes = new MemoryStream(Png(size.Item1, size.Item2));
                library = await f.Assets.AddImageAsync(f.Project.Id, f.AssetId, bytes, new("reference.png", [], AssetImageOrigin.Imported), library.Revision, f.Ct);
                f.References.Add(new(f.AssetId, library.Assets[0].Images[^1].Id));
            }
            return f;
        }
        public Task<AiJobSubmission> Capture(int count = 1, IReadOnlyList<LoraSelection>? loras = null, IReadOnlyList<RegionalImageSelection>? regions = null,
            QwenImage21Options? qwen = null, string aspect = "1:1")
        {
            var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == Project.Id ? Project : null) };
            var capture = new AiImageJobCapture(Settings, projects, Assets); var id = Guid.NewGuid(); var tab = Guid.NewGuid();
            return _editing ? capture.EditAsync(id, tab, AssetId, new() { ProjectId = Project.Id, Workflow = _workflow,
                    SourceAssetId = References[0].AssetId, SourceImageId = References[0].ImageId, Prompt = "Original instruction · keep mouse_token", AspectRatio = aspect, Count = count, Seed = 123, QwenImage21 = qwen,
                    SourceCrop = new() { X = .25, Width = .5 }, ReferenceCrops = [new(References[1], new() { Height = .5 })], Loras = loras ?? [], Regions = regions }, References, Ct)
                : capture.CreateAsync(id, tab, AssetId, new() { ProjectId = Project.Id, Workflow = _workflow, Prompt = "Original instruction · keep mouse_token", AspectRatio = "1:1", Count = count, Seed = 123, Loras = loras ?? [] }, Ct);
        }
        public async Task<AiJobContext> Claim(AiJobSubmission request)
        { await Jobs.EnqueueAsync(request, Ct); return Context((await Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct))!, false); }
        public AiJobContext Context(AiJobHeader job, bool recovering) => new(job, recovering, Jobs, TimeProvider.System, (_, _) => { }, _ => { }, Ct);
        public async Task<AiJobContext> Recover(AiJobContext context) => Context(await Jobs.UpdateAsync(context.Job.Id,
            j => j with { LeaseId = Guid.NewGuid(), Recovery = AiJobRecovery.RetryOutput }, Ct), true);
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        public void Dispose() => _http.Dispose();
    }
    private sealed class ImageAdapter(ComfyImageJobAdapter real, string output) : IComfyImageJobAdapter
    {
        public string Output => output;
        public string? Failure;
        public Task ValidateAsync(AiImageJobRequest request, CancellationToken ct) => Failure is null ? Task.CompletedTask : Task.FromException(new AiGenerationException(Failure));
        public object Build(AiImageJobRequest request, AiBatchCandidate candidate, string clientId) => real.Build(request, candidate, clientId);
        public ComfyExecutionOptions Options(AiImageJobRequest request) => real.Options(request);
        public string OutputNode(AiImageJobRequest request) => real.OutputNode(request);
    }
}
