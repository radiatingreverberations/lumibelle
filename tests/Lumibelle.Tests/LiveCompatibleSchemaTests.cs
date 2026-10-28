using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

internal sealed class LiveCompatibleSchemaConnection : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly HttpClientHandler _http = new();
    public FileAiSettingsStore Store { get; }
    public TestHttpFactory Factory { get; }
    public AiProviderRegistry Registry { get; }
    public AiSettings Settings { get; }
    public TextModelReference Model { get; }
    public string Report { get; }

    public LiveCompatibleSchemaConnection()
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMPATIBLE_SETTINGS");
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMPATIBLE_URL");
        var model = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMPATIBLE_MODEL");
        var report = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMPATIBLE_OUT");
        if (new[] { directory, url, model, report }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set LUMIBELLE_LIVE_COMPATIBLE_SETTINGS, URL, MODEL and OUT for neutral live schema checks.");
        var services = new ServiceCollection(); services.AddLogging(); services.AddDataProtection().SetApplicationName("Lumibelle");
        _services = services.BuildServiceProvider();
        Store = new(new ApplicationPaths(directory!), new global::lumibelle.WebSecretProtector(_services.GetRequiredService<IDataProtectionProvider>()));
        Settings = new() { VllmUrl = url!, TimeoutSeconds = 180, OpenAiConnections = [new() {
            Url = url!, Kind = OpenAiServerKind.Standard, Name = "Compatible evaluation"
        }] };
        Model = new(AiBackend.Vllm, model!, "Compatible evaluation") { VllmUrl = url, MaxOutputTokens = 8192, Temperature = .2f };
        Factory = new(_http); Registry = new(Factory, Store, TestComfy.Monitor());
        Report = report!; Directory.CreateDirectory(Report);
    }

    public Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(Report, name + ".json"), JsonSerializer.Serialize(value, AtomicJsonFile.Options));
    public void Dispose() { _http.Dispose(); _services.Dispose(); }
}

public sealed class LiveCompatibleSchemaApplicationTests
{
    [Fact]
    public async Task RunningApplicationRecordsTheRequestedModelTestThroughItsNormalQueue()
    {
        if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMPATIBLE_QUEUE") != "1")
            Assert.Skip("Set LUMIBELLE_LIVE_COMPATIBLE_QUEUE=1 to enqueue an actual model test in the running application.");
        using var live = new LiveCompatibleSchemaConnection();
        var ct = TestContext.Current.CancellationToken;
        var settings = await live.Store.LoadAsync(ct);
        var model = live.Model with { MaxOutputTokens = 2048, Temperature = null };
        var submission = AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), model, settings);
        var jobs = new FileAiJobStore(Path.Combine(Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_COMPATIBLE_SETTINGS")!, "ai-jobs"), TimeProvider.System);
        var job = await jobs.EnqueueAsync(submission, ct);
        await live.Save("application-model-test", new { job.Id, job.State });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(4));
        while (job.State is AiJobState.Waiting or AiJobState.Running) {
            await Task.Delay(1000, timeout.Token);
            job = (await jobs.ReadAsync(timeout.Token)).Jobs.Single(j => j.Id == submission.Id);
        }
        var result = await jobs.ReadArtifactAsync<AiModelTestJobResult>(job.Id, AiJobArtifact.Result, ct);
        await live.Save("application-model-test", new { job.Id, job.State, result?.Error,
            Schema = result?.Vllm?.StructuredOutput, Images = result?.Vllm?.ImageInput });
        Assert.Equal(AiJobState.Completed, job.State);
        Assert.True(result?.Vllm?.StructuredOutput?.Supported);
        Assert.True(result?.Vllm?.ImageInput?.MultipleImages);
    }
}

public sealed partial class AiTextJobTests
{
    [Fact]
    public async Task LiveCompatibleProviderUsesSchemasForShotsCompositionAndReviewedSequences()
    {
        using var live = new LiveCompatibleSchemaConnection();
        _settings.Value = live.Settings; _model = live.Model;
        var catalog = await live.Registry.CheckAsync(AiBackend.Vllm, _settings.Value, cancellationToken: _ct);
        Assert.True(catalog.Success, catalog.Message);
        _providers.Model = _model.Model; _providers.ContextLength = catalog.Models.Single(m => m.Id == _model.Model).Catalog?.ContextLength;
        _providers.ClientFactory = ct => live.Registry.CreateAsync(AiBackend.Vllm, _model.Model, _settings.Value, ct);

        // Use the real model-test handler with an isolated settings store. Credentials are only read from the configured store.
        var benchmark = AiModelTestJobHandler.Capture(Guid.NewGuid(), Guid.NewGuid(), _model with { MaxOutputTokens = 2048 }, _settings.Value);
        var benchmarkRequest = benchmark.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)! with { TestImageInput = false };
        benchmark = benchmark with { Snapshot = JsonSerializer.SerializeToElement(benchmarkRequest, AtomicJsonFile.Options) };
        var benchmarkContext = await Claim(benchmark);
        var benchmarkWorker = new AiModelTestJobHandler(live.Registry, _settings, live.Factory, new(TestComfy.Monitor()), TimeProvider.System);
        var measured = await benchmarkWorker.ExecuteAsync(benchmarkContext, benchmark.Snapshot, _ct);
        var capability = (await benchmarkContext.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, _ct))!.Vllm!.StructuredOutput;
        await live.Save("schema-capability", new { Model = _model.Model, Outcome = measured.State, Capability = capability });
        Assert.True(capability?.Supported);
        await FinishLiveContext(benchmarkContext, measured);

        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: live.Registry);
        foreach (var single in new[] { false, true })
        {
            var source = LiveShotDraftingTests.Request(single, _model);
            var request = source with { Script = source.Script with { ProjectId = _project }, Assets = new() { ProjectId = _project } };
            var submission = await capture.PlanShotsAsync(Guid.NewGuid(), Guid.NewGuid(), request, false, _ct);
            var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
            Assert.NotNull(captured.ResponseSchema); Assert.Null(captured.ResponseGrammar);
            var context = await Claim(submission);
            var outcome = await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
            var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
            var plan = result.Read<ShotPlanningResult>();
            var stem = single ? "single-shot" : "scene-shots";
            await live.Save(stem + "-input", new { captured.Messages, captured.ResponseSchema });
            await live.Save(stem + "-output", result);
            await live.Save(stem + "-summary", new { Outcome = outcome.State, result.Error, Shots = plan?.Shots.Count,
                TotalSeconds = plan?.Shots.Sum(s => s.Duration), DialogueNotes = plan?.DialogueNotes, CoverageNotes = plan?.CoverageNotes });
            Assert.Equal(AiJobState.Completed, outcome.State); Assert.NotNull(plan);
            Assert.All(plan.Shots, s => Assert.InRange(s.Duration!.Value, 1, 10));
            if (single) Assert.Single(plan.Shots); else { Assert.True(plan.Shots.Count >= 3); Assert.True(plan.Shots.Sum(s => s.Duration) >= 24); }
            await FinishLiveContext(context, outcome);
        }

        var shot = new Shot { Title = "Tea", Description = "Mira waits beside a kettle and asks whether the tea is ready.", Duration = 6,
            Dialogue = [new() { Speaker = "Mira", Language = "English", Text = "Is the tea ready?" }] };
        var composition = new PromptCompositionRequest(_project, Guid.NewGuid(), 1, "context", "source", shot,
            "A quiet room.", [], [], [], [], "Use brief prose and action passages. No media references or music.", "", "", _model);
        var composed = new AiTextJobRequest(3, AiJobKind.PromptComposition, _model, false, _settings.Value, ProductionPolicy.Profile, .2f, 1,
            JsonSerializer.SerializeToElement(composition, AtomicJsonFile.Options), PromptComposer.BuildMessages(composition, [], structured: true).Select(AiTextMessage.Capture).ToArray()) {
                ResponseSchema = PromptComposer.ResponseSchemaFor(shot)
            };
        var text = new StringBuilder(); string? finish = null;
        using (var client = await live.Registry.CreateAsync(AiBackend.Vllm, _model.Model, _settings.Value, _ct))
            await foreach (var update in client.GetStreamingResponseAsync(composed.Messages.Select(m => m.ToMessage()), TextGenerationOptions.Captured(composed), _ct))
            { text.Append(update.Text); if (update.FinishReason is { } reason) finish = reason.ToString(); }
        var parsed = AiTextResults.Parse(composed, text.ToString(), finish);
        var prompt = parsed.Read<PromptCompositionResult>();
        await live.Save("composition-input", new { composed.Messages, composed.ResponseSchema });
        await live.Save("composition-output", parsed);
        await live.Save("composition-summary", new { parsed.Error, finish, Notes = prompt?.Notes,
            ExactDialogue = prompt?.Prompt.Contains("[English] Is the tea ready?", StringComparison.Ordinal) });
        Assert.Null(parsed.Error); Assert.NotNull(prompt);
        Assert.Contains("[English] Is the tea ready?", prompt.Prompt);
        Assert.DoesNotContain("<Audio", prompt.Prompt);

        // This exercises the durable planning, writing and review/repair stages with neutral fixture material.
        var accepted = await AcceptedScriptSource();
        var script = new ScriptDocument { ProjectId = _project };
        var payload = new ScriptAssistantRequest(new() { Backend = _model.Backend, Model = _model.Model, Operation = WritingOperation.Continue,
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null), SourceFingerprint = ScriptStructure.ContextFingerprint(script),
            Instructions = "Write a short focused screenplay sequence, about 80–120 words. End when Eira discovers the brass key. Keep later river and garden events unrevealed." }, script, [], _model) {
                OutlineSelection = new(accepted.Id, accepted.ReviewRevision, 0, 0, true)
            };
        var sequence = await SourceScriptCapture.ScriptAsync(payload.Run.Id, Guid.NewGuid(), payload, false, _ct);
        var sequenceContext = await Claim(sequence);
        AiJobOutcome? sequenceOutcome = null;
        try {
            sequenceOutcome = await Handler().ExecuteAsync(sequenceContext, sequence.Snapshot, _ct);
        } finally {
            var result = await sequenceContext.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct);
            await live.Save("sequence-output", result!);
            await live.Save("sequence-summary", new { Outcome = sequenceOutcome?.State, result?.Error, Review = result?.SequenceReview });
            foreach (var operation in new[] { "scene-plan", "scene-draft", "sequence-review-0-0", "sequence-review-0-1", "sequence-repair-1", "sequence-review-1-0", "sequence-review-1-1" }) {
                var input = await sequenceContext.ReadOperationAsync<AiTextJobRequest>(operation, AiOperationArtifact.Request, _ct);
                if (input is not null) await live.Save(operation + "-input", new { input.Messages, input.ResponseSchema, input.ResponseGrammar });
                var output = await sequenceContext.ReadOperationAsync<AiTextJobResult>(operation, AiOperationArtifact.Output, _ct);
                if (output is not null) await live.Save(operation + "-output", output);
            }
        }
        Assert.Equal(AiJobState.Completed, sequenceOutcome!.State);
        Assert.Equal(ScriptSequenceReviewState.Passed, (await sequenceContext.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!.SequenceReview!.State);
    }

    private Task<AiJobHeader> FinishLiveContext(AiJobContext context, AiJobOutcome outcome) => Store.UpdateAsync(context.Job.Id,
        j => j with { State = outcome.State, Recovery = outcome.Recovery, LeaseId = null, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
}
