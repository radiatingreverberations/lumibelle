using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.TextJobTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly Guid _project = Guid.NewGuid();
    private TextModelReference _model = new(AiBackend.OpenRouter, "test/model", "Captured model");
    private readonly ReferenceAsset _asset = new() { Id = Guid.NewGuid(), Name = "Juniper", Category = AssetCategory.Character, Description = "Shared identity notes",
        Looks = [new() { Name = "Everyday", Description = "A hoodie", PreservationGuidance = "Grey fabric" }],
        Images = [new() { Id = Guid.NewGuid(), FileName = "base.png", ContentType = "image/png", Width = 80, Height = 40 }, new() { Id = Guid.NewGuid(), FileName = "second.png", ContentType = "image/png", Width = 20, Height = 60 }] };
    private readonly FakeAiSettingsStore _settings = new() { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true, Temperature = .45f, MaxOutputTokens = 900 } };
    private readonly TextProviders _providers = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeAssetStore _assets;
    private readonly ScriptDocument _script;
    private FileAiJobStore Store => new(new StorageTestEnvironment(_root), TimeProvider.System);
    private PromptEnhancer Enhancer => new(_providers, _settings, _assets);
    private GuidanceAssistant Guidance => new(_providers, _settings, _assets);
    private ICodexClient? _codex;
    private AiTextJobCapture Capture => new(_settings, _projects, _assets, Enhancer, Guidance, _codex);
    private ApprovedScriptSnapshot Approved => new(Guid.NewGuid(), _project, 2, DateTimeOffset.UtcNow, "2 minutes", "h3-practical-v1", _script.Blocks);
    private GuidanceRequest GuidanceRequest => new(GuidanceContext.From(new(_project, _asset.Id, GuidanceScope.CharacterIdentity), _asset)!, _model);
    private PromptEnhancementRequest Enhancement(bool inspect = false) => new(new()
    {
        ProjectId = _project, AssetId = _asset.Id, AssetName = _asset.Name, Category = _asset.Category, VisualNotes = "Unproven background notes",
        Prompt = "Put the armor on the girl. Keep the sign \"ÖPPET\" and mouse_token.", ProtectedTriggers = ["mouse_token"],
        Workflow = ImageWorkflow.Flux2Klein9bKv, IsEdit = true, MaximumReferences = 8,
        References = [new(new(_asset.Id, _asset.Images[0].Id), "Image 1 · Base", "Base notes", new() { X = .25, Width = .5, Height = 1 }),
            new(new(_asset.Id, _asset.Images[1].Id), "Image 2 · Armor", "Reference notes", new() { Width = 1, Height = .5 })]
    }, _model, inspect);
    public AiTextJobTests()
    {
        _assets = new(_project) { Library = new() { ProjectId = _project, Assets = [_asset] } };
        _projects.Get = id => Task.FromResult<ProjectInfo?>(id == _project ? FakeProjectStore.Project("QA project") with { Id = id } : null);
        _script = new() { ProjectId = _project, Revision = 2, Brief = new() { Idea = "A small mouse listens to rain." },
            Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "A mouse listens to rain.")] };
    }
    private AiTextJobHandler Handler(HttpMessageHandler? http = null) => new(_providers,
        new TestHttpFactory(http ?? new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("Unexpected ComfyUI request"))),
        new ComfyJobExecution(TestComfy.Monitor()), _projects, Enhancer, Guidance, TimeProvider.System, _codex);
    private async Task<AiJobSubmission> Request(AiJobKind kind)
    {
        var id = Guid.NewGuid(); var tab = Guid.NewGuid(); var approved = Approved;
        return kind switch
        {
            AiJobKind.ScriptAssistant => await Capture.ScriptAsync(id, tab, new(new() { Id = id, Operation = WritingOperation.Revise, Backend = _model.Backend, Model = _model.Model, Target = ScriptStructure.Capture(_script, ScriptScope.Document, null) }, _script, [], _model), true, _ct),
            AiJobKind.AssetExtraction => await Capture.ExtractAsync(id, tab, new(approved, _assets.Library, [_script.Blocks[0].Id], _model.Backend, _model.Model, _model), true, _ct),
            AiJobKind.ShotPlanning => await Capture.PlanShotsAsync(id, tab, new(approved, _assets.Library, [_script.Blocks[0].Id], 15, "Keep it calm", _model), true, _ct),
            AiJobKind.PromptEnhancement => await Capture.EnhanceAsync(id, tab, Enhancement(), _ct),
            AiJobKind.Guidance => await Capture.GuidanceAsync(id, tab, GuidanceRequest, _ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
    private async Task<AiJobContext> Claim(AiJobSubmission request)
    {
        await Store.EnqueueAsync(request, _ct); var job = (await Store.ClaimNextAsync(request.Backend, 1, _ct))!;
        Assert.Equal(request.Id, job.Id); return Context(job, false);
    }
    private AiJobContext Context(AiJobHeader job, bool recovering) => new(job, recovering, Store, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
    private async Task<AiJobContext> Recover(AiJobContext context) => Context(await Store.UpdateAsync(context.Job.Id, j => j with { LeaseId = Guid.NewGuid(), Recovery = AiJobRecovery.CheckStatus }, _ct), true);
    private string Response(AiJobKind kind) => kind switch
    {
        AiJobKind.ScriptAssistant => "[{\"kind\":\"Scene\",\"spans\":[{\"text\":\"INT. ROOM - DAY\"}]},{\"kind\":\"Action\",\"spans\":[{\"text\":\"The mouse watches rain.\"}]}]",
        AiJobKind.AssetExtraction => "[]",
        AiJobKind.ShotPlanning => JsonSerializer.Serialize(new[] { new { title = "Rain", sceneId = _script.Blocks[0].Id, sourceBlockIds = _script.Blocks.Select(b => b.Id), duration = 2.0, description = "The camera holds as a mouse watches rain.", dialogue = Array.Empty<object>(), atmosphere = "Quiet", music = "" } }),
        _ => "{\"kind\":\"Prompt\",\"text\":\"Keep the character recognizable, preserving mouse_token and the sign ÖPPET.\"}"
    };
    [Theory] [InlineData(AiJobKind.ScriptAssistant)] [InlineData(AiJobKind.AssetExtraction)] [InlineData(AiJobKind.ShotPlanning)] [InlineData(AiJobKind.PromptEnhancement)] [InlineData(AiJobKind.Guidance)]
    public async Task EveryTextOperationProducesDurableTypedResultsWithoutApplyingAnything(AiJobKind kind)
    {
        var request = await Request(kind); var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        _providers.Chat.Output = Response(kind); var context = await Claim(request);
        var outcome = await Handler().ExecuteAsync(context, request.Snapshot, _ct);
        Assert.Equal(AiJobState.Completed, outcome.State);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.True(result!.Complete); Assert.NotNull(result.Value); Assert.Null(result.Error); Assert.Equal(Response(kind), result.Raw);
        Assert.Equal(2, captured.Version);
        Assert.Equal(captured.Seed, _providers.Chat.Options!.Seed);
        Assert.Null(_providers.Chat.Options.Temperature); Assert.Null(_providers.Chat.Options.MaxOutputTokens);
        Assert.Equal(0, _assets.SaveCalls); Assert.Equal("A mouse listens to rain.", _script.Blocks[1].Text);
        _providers.Created = 0; _providers.Chat.FailAfterText = true; _providers.OnCheck = () => throw new InvalidOperationException("Reopening needs no catalog check");
        var recovery = await Recover(context); Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(recovery, request.Snapshot, _ct)).State);
        var reopened = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.Equal(result.Value.Value.GetRawText(), reopened!.Value!.Value.GetRawText()); Assert.Equal(0, _providers.Created);
    }
    [Theory] [InlineData("stop")] [InlineData("length")] [InlineData("content_filter")] [InlineData(null)]
    public async Task WrappedScreenplayRequiresNormalCompletionAndRetainsTheExactResponse(string? finish)
    {
        var request = await Request(AiJobKind.ScriptAssistant);
        var raw = "**Here is the screenplay:**\n\n```json\n" + Response(AiJobKind.ScriptAssistant) + "\n```";
        _providers.Chat.Output = raw; _providers.Chat.Finish = finish is null ? null : new ChatFinishReason(finish);
        var outcome = await Handler().ExecuteAsync(await Claim(request), request.Snapshot, _ct);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.Equal(raw, result!.Raw); Assert.Equal("A mouse listens to rain.", _script.Blocks[1].Text);
        if (finish == "stop")
        {
            Assert.Equal(AiJobState.Completed, outcome.State); Assert.Null(result.Error);
            Assert.Equal(2, result.Read<AssistantUpdate>()!.Blocks!.Count);
        }
        else
        {
            Assert.Equal(AiJobState.NeedsAttention, outcome.State); Assert.NotNull(result.Error); Assert.Null(result.Value);
        }
    }
    [Theory] [InlineData(AiJobKind.ScriptAssistant)] [InlineData(AiJobKind.AssetExtraction)] [InlineData(AiJobKind.ShotPlanning)] [InlineData(AiJobKind.PromptEnhancement)] [InlineData(AiJobKind.Guidance)]
    public async Task ACompleteAnswerAfterReasoningAloudIsReadAndTheRawReplyKept(AiJobKind kind)
    {
        var request = await Request(kind);
        // Reasoning with bracketed prose before the answer, and a revision cut off by the reply limit after it.
        var reply = "Let me think about this [English] request carefully.\n\n" + Response(kind) + "\n\nWait, let me revise: [{\"kind\":\"Act";
        _providers.Chat.Output = reply;
        var outcome = await Handler().ExecuteAsync(await Claim(request), request.Snapshot, _ct);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.Equal(AiJobState.Completed, outcome.State); Assert.Null(result!.Error); Assert.NotNull(result.Value);
        Assert.Equal(reply, result.Raw);
    }
    [Fact]
    public void ANestedEmptyListInACutOffReplyIsNeverAnAnswer()
    {
        var request = Request(AiJobKind.AssetExtraction).GetAwaiter().GetResult().Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var cut = AiTextResults.Parse(request, "Here are the assets:\n[{\"name\":\"Mouse\",\"evidence\":[],\"notes\":\"unfin", "stop");
        Assert.NotNull(cut.Error); Assert.NotNull(cut.Value!.Value.GetProperty("validationError").GetString());
    }
    [Theory] [InlineData(AiJobKind.ScriptAssistant)] [InlineData(AiJobKind.AssetExtraction)]
    public async Task LegacyQueuedOpenRouterRequestsKeepCapturedOverrides(AiJobKind kind)
    {
        var request = await Request(kind);
        var legacy = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)! with { Version = 1 };
        request = request with { Snapshot = JsonSerializer.SerializeToElement(legacy, AtomicJsonFile.Options) };
        _settings.Value = _settings.Value with { Temperature = 1.8f, MaxOutputTokens = 32768 };
        _providers.Chat.Output = Response(kind);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(await Claim(request), request.Snapshot, _ct)).State);
        Assert.Equal(kind == AiJobKind.AssetExtraction ? .2f : .45f, _providers.Chat.Options!.Temperature);
        Assert.Equal(900, _providers.Chat.Options.MaxOutputTokens); Assert.Equal(legacy.Seed, _providers.Chat.Options.Seed);
    }

    [Theory] [InlineData(AiJobKind.ScriptAssistant)] [InlineData(AiJobKind.AssetExtraction)] [InlineData(AiJobKind.ShotPlanning)] [InlineData(AiJobKind.PromptEnhancement)] [InlineData(AiJobKind.Guidance)]
    public async Task CodexTextOperationsRetainValidatedResultsAndCapturedEffort(AiJobKind kind)
    {
        var mock = new Lumibelle.Testing.MockCodexTransport { Text = Response(kind) };
        await using var client = new CodexClient(mock, TimeProvider.System); _codex = client;
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, TextEffort = "low" }, DefaultBackend = AiBackend.Codex };
        _model = new(AiBackend.Codex, "mock-codex", "Codex QA", ReasoningEffort: "high"); _providers.Model = _model.Model;
        var request = await Request(kind); var context = await Claim(request);
        var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal("high", captured.Codex!.Effort);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, request.Snapshot, _ct)).State);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.True(result!.Complete); Assert.NotNull(result.Value); Assert.Null(result.Error); Assert.Equal(Response(kind), result.Raw);
        Assert.Equal(0, _assets.SaveCalls); Assert.Equal(0, _providers.Created);
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct)).State);
        Assert.Equal(1, mock.Turns);
    }
    [Fact]
    public async Task CodexDefaultEffortIsFrozenBeforeQueueing()
    {
        var mock = new Lumibelle.Testing.MockCodexTransport { Text = Response(AiJobKind.PromptEnhancement) };
        await using var client = new CodexClient(mock, TimeProvider.System); _codex = client;
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, TextEffort = "low" } };
        _model = new(AiBackend.Codex, "mock-codex", "Codex QA"); _providers.Model = _model.Model;
        var request = await Request(AiJobKind.PromptEnhancement);
        var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal("low", captured.Model.ReasoningEffort); Assert.Equal("low", captured.Codex!.Effort);
        _settings.Value = _settings.Value with { Codex = _settings.Value.Codex with { TextEffort = "high" } };
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(await Claim(request), request.Snapshot, _ct)).State);
        Assert.Equal("low", mock.Inputs.Single().GetProperty("effort").GetString());
    }
    [Fact]
    public async Task StreamedTextReportsReceivedCharactersAfterSavingItsPartialResult()
    {
        var request = await Request(AiJobKind.ScriptAssistant); var claimed = await Claim(request);
        var updates = new List<AiJobProgress>();
        var context = new AiJobContext(claimed.Job, false, Store, TimeProvider.System, (_, progress) => updates.Add(progress), _ => { }, _ct);
        _providers.Chat.Output = Response(AiJobKind.ScriptAssistant);
        await Handler().ExecuteAsync(context, request.Snapshot, _ct);
        var received = Assert.Single(updates, p => p.Progress.Unit == "characters");
        Assert.Equal(_providers.Chat.Output.Length, received.Progress.Current);
        Assert.False(received.Progress.IsDeterminate); Assert.Null(received.Progress.EstimatedRemaining);
        Assert.Contains("Receiving response", received.Progress.Label);
        Assert.Contains(updates, p => p.Progress.Label.Contains("Waiting for Captured model"));
    }
    [Fact]
    public async Task CaptureDetachesDraftsSettingsAndProfilesBeforeAsyncWork()
    {
        var projectReady = new TaskCompletionSource<ProjectInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _projects.Get = _ => projectReady.Task;
        var task = Request(AiJobKind.ScriptAssistant);
        _script.Blocks[1].Spans[0] = new("Changed while enqueueing"); _settings.Value.H3.Encoder = "changed encoder";
        _settings.Value = _settings.Value with { Temperature = 1.9f, MaxOutputTokens = 200, OpenRouterModel = "another/model" };
        projectReady.SetResult(FakeProjectStore.Project("QA project") with { Id = _project });
        var request = await task; var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal("A mouse listens to rain.", captured.Payload<ScriptAssistantRequest>().Script.Blocks[1].Text);
        Assert.Equal(.45f, captured.Temperature); Assert.Equal(900, captured.Settings.MaxOutputTokens); Assert.NotEqual("changed encoder", captured.Settings.H3.Encoder);
        Assert.Equal("test/model", captured.Model.Model); Assert.Equal("script-writing-v2", captured.Profile);
        Assert.DoesNotContain("Changed while enqueueing", string.Join(" ", captured.Messages.SelectMany(m => m.Parts).Select(p => p.Text)));
        Assert.DoesNotContain("test-only-key", request.Snapshot.GetRawText());
    }
    [Fact]
    public async Task VisionInputsKeepTheirOwnCropsAndOrderAndMustRemainActiveAtDispatch()
    {
        var request = await Capture.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), Enhancement(true), _ct);
        var snapshot = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var images = snapshot.Messages.SelectMany(m => m.Parts).Where(p => p.Image is not null).ToArray();
        Assert.Equal(2, images.Length);
        var first = SixLabors.ImageSharp.Image.Identify(images[0].Image!); var second = SixLabors.ImageSharp.Image.Identify(images[1].Image!);
        Assert.Equal((40, 40), (first.Width, first.Height)); Assert.Equal((20, 30), (second.Width, second.Height));
        var context = await Claim(request); _providers.Chat.Output = Response(AiJobKind.PromptEnhancement);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, request.Snapshot, _ct)).State);
        Assert.Equal(2, _providers.Chat.Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count());
        Assert.Contains("Image 1", _providers.Chat.Messages.Last().Text); Assert.Contains("Image 2", _providers.Chat.Messages.Last().Text);
        await Store.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed }, _ct);
        var next = await Capture.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), Enhancement(true), _ct);
        await _assets.DeleteImageAsync(_project, _asset.Id, _asset.Images[1].Id, _assets.Library.Revision, _ct);
        _providers.Created = 0; var waiting = await Claim(next);
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(waiting, next.Snapshot, _ct)); Assert.Equal(0, _providers.Created);
    }
    [Fact]
    public async Task VisionCapabilityChangesAndChangedGuidanceRequireExplicitRepair()
    {
        var inspected = await Capture.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), Enhancement(true), _ct);
        _providers.Vision = false; var context = await Claim(inspected);
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, inspected.Snapshot, _ct)); Assert.Equal(0, _providers.Created);
        await Store.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.Cancelled }, _ct);
        _providers.Vision = true; var suggestion = await Request(AiJobKind.Guidance); var guidanceContext = await Claim(suggestion);
        _assets.Library = _assets.Library with { Assets = [_asset with { PreservationGuidance = "Changed in another tab" }] };
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(guidanceContext, suggestion.Snapshot, _ct)); Assert.Equal(0, _providers.Created);
    }
    [Theory] [InlineData("length")] [InlineData("tool_calls")] [InlineData(null)]
    public async Task IncompletePaidOutputIsInspectableButNeverApplicable(string? finish)
    {
        var request = await Request(AiJobKind.Guidance); _providers.Chat.Output = Response(AiJobKind.Guidance); _providers.Chat.Finish = finish is null ? null : new ChatFinishReason(finish);
        var context = await Claim(request); var outcome = await Handler().ExecuteAsync(context, request.Snapshot, _ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State); var result = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.Null(result!.Value); Assert.NotEmpty(result.Raw); Assert.NotNull(result.Error);
    }
    [Fact]
    public async Task TriggerRemovalAndClarificationAreNotSilentlyTreatedAsPrompts()
    {
        var request = await Request(AiJobKind.PromptEnhancement); var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var removed = AiTextResults.Parse(captured, "{\"kind\":\"Prompt\",\"text\":\"A new description without the required trigger.\"}", "stop");
        Assert.Null(removed.Value); Assert.Contains("trigger", removed.Error);
        var question = AiTextResults.Parse(captured, "NEEDS_INPUT: Which armor should be used?", "stop");
        Assert.Equal(PromptEnhancementKind.NeedsInput, question.Read<PromptEnhancementResult>()!.Kind);
        var malformed = AiTextResults.Parse(captured, "{\"kind\":\"Prompt\",\"text\":\"cut", "stop"); Assert.Null(malformed.Value);
    }
    [Fact]
    public async Task PaidPartialAfterFailureIsRetainedAndRecoveryMakesNoSecondCall()
    {
        var request = await Request(AiJobKind.Guidance); _providers.Chat.FailAfterText = true; _providers.Chat.Output = "Partial paid response";
        var context = await Claim(request);
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, request.Snapshot, _ct));
        var partial = await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct);
        Assert.False(partial!.Complete); Assert.Equal("Partial paid response", partial.Raw);
        _providers.Created = 0; var recovered = await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct);
        Assert.Equal(AiJobRecovery.GenerateAgain, recovered.Recovery); Assert.Equal(0, _providers.Created);
    }
    [Fact]
    public async Task CompletedPaidOutputCanFinishParsingAfterRestartWithoutProviderAccess()
    {
        var request = await Request(AiJobKind.ScriptAssistant); var context = await Claim(request);
        await context.SaveOperationAsync("text", AiOperationArtifact.Output, new AiTextJobResult(Response(AiJobKind.ScriptAssistant), true, "stop"), _ct);
        _providers.OnCheck = () => throw new InvalidOperationException("No network during completed paid recovery");
        var result = await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct);
        Assert.Equal(AiJobState.Completed, result.State); Assert.Equal(0, _providers.Created);
        Assert.NotEmpty((await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!.Read<AssistantUpdate>()!.Blocks!);
    }
    [Fact]
    public async Task LocalTextUsesDurableSubmissionWithCapturedSeedAndNeverTheMutableDefault()
    {
        _settings.Value = _settings.Value with { DefaultBackend = AiBackend.ComfyUI, ComfyModel = "local/model", ComfyUrl = "http://original.test:8188" };
        var model = new TextModelReference(AiBackend.ComfyUI, "local/model", "Local model", _settings.Value.ComfyUrl);
        _providers.Model = model.Model;
        var request = await Capture.GuidanceAsync(Guid.NewGuid(), Guid.NewGuid(), GuidanceRequest with { Model = model, FollowsDefault = true }, _ct);
        var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        _settings.Value = _settings.Value with { ComfyModel = "other/model", ComfyUrl = "http://different.test:8188", Temperature = 1.8f };
        var promptId = Guid.NewGuid().ToString(); var posts = 0;
        using var http = new ScriptedHttpHandler(async (message, ct) =>
        {
            Assert.Equal("original.test", message.RequestUri!.Host);
            if (message.Method == HttpMethod.Post)
            {
                posts++; using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
                var graph = body.RootElement.GetProperty("prompt"); Assert.Equal(model.Model, graph.GetProperty("1").GetProperty("inputs").GetProperty("clip_name").GetString());
                var generation = graph.GetProperty("2").GetProperty("inputs"); Assert.Equal(captured.Seed, generation.GetProperty("sampling_mode.seed").GetInt64());
                Assert.Equal(.45f, generation.GetProperty("sampling_mode.temperature").GetSingle());
                Assert.Equal(900, generation.GetProperty("max_length").GetInt32());
                return Json(JsonSerializer.Serialize(new { prompt_id = promptId }));
            }
            return Json(JsonSerializer.Serialize(new Dictionary<string, object> { [promptId] = new { status = new { completed = true, status_str = "success" }, outputs = new Dictionary<string, object> { ["3"] = new { text = new[] { Response(AiJobKind.Guidance) } } } } }));
        });
        var context = await Claim(request); Assert.Equal(AiJobState.Completed, (await Handler(http).ExecuteAsync(context, request.Snapshot, _ct)).State);
        Assert.Equal(1, posts); Assert.Equal(0, _providers.Created); Assert.False((await context.ExecutionAsync(_ct)).MayBeRunning);
        Assert.Equal(AiJobState.Completed, (await Handler(http).RecoverAsync(await Recover(context), request.Snapshot, _ct)).State); Assert.Equal(1, posts);
    }
    [Theory]
    [InlineData(AiJobKind.ScriptAssistant)] [InlineData(AiJobKind.AssetExtraction)] [InlineData(AiJobKind.ShotPlanning)]
    [InlineData(AiJobKind.PromptEnhancement)] [InlineData(AiJobKind.Guidance)]
    public async Task EveryLocalTextPathCapturesModelOverridesAndKeepsExtractionTemperature(AiJobKind kind)
    {
        _model = new(AiBackend.ComfyUI, "local/model", "Local", _settings.Value.ComfyUrl);
        var key = TextModelPolicy.Key(_model);
        _settings.Value.ComfyTextModels[key] = new(8192, .9f);
        var request = await Request(kind); var context = await Claim(request);
        _settings.Value.ComfyTextModels[key] = new(1000, .3f);
        var restarted = new FileAiJobStore(new StorageTestEnvironment(_root), TimeProvider.System);
        var captured = request.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(8192, captured.Settings.MaxOutputTokens);
        Assert.Equal(kind == AiJobKind.AssetExtraction ? .2f : .9f, captured.Temperature);
        var options = TextGenerationOptions.Captured(captured);
        Assert.Equal(8192, options.MaxOutputTokens); Assert.Equal(captured.Temperature, options.Temperature);
        Assert.Equal(context.Job.RequestFingerprint, (await restarted.ReadAsync(_ct)).Jobs.Single().RequestFingerprint);
        Assert.Equal(8192, (await restarted.ReadSnapshotAsync(request.Id, _ct)).Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Settings.MaxOutputTokens);
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class TextProviders : IAiProviderRegistry
    {
        public readonly TextChat Chat = new(); public int Created; public bool Vision = true; public string Model = "test/model"; public Action? OnCheck;
        public Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default)
        { OnCheck?.Invoke(); return Task.FromResult(new AiConnectionCheck(true, "Available", [new(Model, "Test model", SupportsImages: Vision, ReasoningEfforts: ["low", "medium", "high"], DefaultEffort: "medium")], "v1")); }
        public Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default) { Assert.Equal(Model, model); Created++; return Task.FromResult<IChatClient>(Chat); }
        public IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(string model, AiSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(string model, AiSettings settings, ComfyTextModelTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class TextChat : IChatClient, IProgressReportingChatClient
    {
        public string Output = ""; public ChatFinishReason? Finish = ChatFinishReason.Stop; public bool FailAfterText;
        public List<ChatMessage> Messages = []; public ChatOptions? Options;
        public OpenRouterRequestUsage? Usage; public bool FailAfterUsage; public Action? AfterUsage;
        public async IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) yield return new(update);
            if (Usage is not null) { yield return new(OpenRouterUsage: Usage); yield return new(OpenRouterUsage: Usage); }
            AfterUsage?.Invoke();
            if (FailAfterUsage) throw new HttpRequestException("Disconnected after usage");
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Messages = messages.ToList(); Options = options; cancellationToken.ThrowIfCancellationRequested();
            yield return new(ChatRole.Assistant, Output);
            if (FailAfterText) throw new HttpRequestException("Provider disconnected");
            yield return new() { FinishReason = Finish };
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
