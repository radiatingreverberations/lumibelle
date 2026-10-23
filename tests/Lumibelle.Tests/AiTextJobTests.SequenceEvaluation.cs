using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    // Opt-in evaluation of production capture, grammar, review and repair against a
    // user-configured local server. No project data or saved server credentials are read.
    [Fact]
    public async Task LiveLocalModelDraftsAndReviewsASyntheticSequence()
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_SEQUENCE_EVAL_URL");
        var modelId = Environment.GetEnvironmentVariable("LUMIBELLE_SEQUENCE_EVAL_MODEL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(modelId)) Assert.Skip("Set LUMIBELLE_SEQUENCE_EVAL_URL and LUMIBELLE_SEQUENCE_EVAL_MODEL for the local-model evaluation.");
        const string sourceText = "Chapter 1\nAt dawn, Eira finds a brass key beside a closed river gate. A traveller wearing a grey coat asks whether it belongs to her. Eira says she has never seen it before. The traveller points out a matching leaf emblem on the key and gate, then leaves without giving a name. Eira tries the key. The gate opens onto a garden, and she steps through.";
        const string summary = "Eira finds the key, exchanges words with an unidentified traveller, discovers the matching emblem, opens the gate and enters the garden.";
        _model = new(AiBackend.Vllm, modelId, "Local evaluation model") { VllmUrl = url, MaxOutputTokens = 3072, Temperature = .2f };
        _settings.Value = _settings.Value with { VllmUrl = url, TimeoutSeconds = 900 };
        var live = new AiProviderRegistry(new LiveSequenceHttpFactory(), _settings, TestComfy.Monitor());
        var catalog = await live.CheckAsync(AiBackend.Vllm, _settings.Value, cancellationToken: _ct);
        Assert.True(catalog.Success, catalog.Message);
        _providers.Model = modelId; _providers.ContextLength = catalog.Models.Single(m => m.Id == modelId).Catalog?.ContextLength;
        _providers.ClientFactory = ct => live.CreateAsync(AiBackend.Vllm, modelId, _settings.Value, ct);
        await SourceStore.SaveAsync(_project, 0, "The river gate", sourceText, "A quiet mystery, told through visible action and dialogue.", _ct);
        var source = SourceAnalysisTests.Run(sourceText, SourceAnalysis.Split(sourceText, 1000)) with {
            State = SourceRunState.Accepted,
            Title = "The river gate",
            Fingerprint = SourceAnalysis.Fingerprint("The river gate", sourceText, "A quiet mystery, told through visible action and dialogue."),
            Proposal = new() { Title = "The river gate", Premise = "An unexpected key leads Eira into a garden.", Approach = "One compact dramatic sequence.",
                Acts = [new() { Title = "Discovery", Purpose = "Discover the key's purpose.", Scenes = [new() { Title = "The gate", Summary = summary, Sources = [1] }] }] },
            Direction = "A quiet mystery, told through visible action and dialogue."
        };
        await SourceStore.UpdateAsync(_project, d => Task.FromResult(d with { Runs = [source] }), _ct);
        var script = new ScriptDocument { ProjectId = _project };
        var payload = new ScriptAssistantRequest(new() { Backend = _model.Backend, Model = modelId, Operation = WritingOperation.Continue,
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null), SourceFingerprint = ScriptStructure.ContextFingerprint(script),
            Instructions = "Write one compact screenplay sequence, around 200–300 words. The traveller's identity remains unknown." }, script, [], _model) {
            OutlineSelection = new(source.Id, source.ReviewRevision, 0, 0, true)
        };
        var submission = await SourceScriptCapture.ScriptAsync(payload.Run.Id, Guid.NewGuid(), payload, false, _ct);
        var context = await Claim(submission);
        AiJobOutcome? outcome = null;
        try {
            outcome = await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
            Assert.Equal(AiJobState.Completed, outcome.State);
            var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
            Assert.Equal(ScriptSequenceReviewState.Passed, result.SequenceReview!.State);
            Assert.NotEmpty(result.Read<AssistantUpdate>()!.Blocks!);
        }
        finally {
            if (Environment.GetEnvironmentVariable("LUMIBELLE_SEQUENCE_EVAL_OUT") is { Length: > 0 } path) {
                Directory.CreateDirectory(path);
                var result = await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct);
                await File.WriteAllTextAsync(Path.Combine(path, "result.json"), JsonSerializer.Serialize(new { Model = modelId, Calls = _providers.Created, Outcome = outcome, Result = result }, AtomicJsonFile.Options), _ct);
                foreach (var operation in new[] { "scene-plan", "text", "sequence-review-0-0", "sequence-review-0-1", "sequence-repair-1", "sequence-review-1-0", "sequence-review-1-1" }) {
                    var output = await context.ReadOperationAsync<AiTextJobResult>(operation, AiOperationArtifact.Output, _ct);
                    if (output is not null) await File.WriteAllTextAsync(Path.Combine(path, operation + ".json"), JsonSerializer.Serialize(output, AtomicJsonFile.Options), _ct);
                }
            }
        }
    }
    private sealed class LiveSequenceHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }
}
