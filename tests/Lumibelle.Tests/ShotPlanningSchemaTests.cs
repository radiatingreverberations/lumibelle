using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ShotPlanningSchemaTests
{
    internal static ShotPlanningRequest Request(bool single = false)
    {
        var doc = ScriptFixtures.Document();
        return new(ScriptFixtures.Approved(doc.Blocks, doc.ProjectId), new() { ProjectId = doc.ProjectId }, [doc.Blocks[0].Id], 8,
            "Use one concise shot for this tiny scene.", new(AiBackend.OpenRouter, "mock", "Mock")) {
            CoverageOnly = true, SingleShot = single ? new(Guid.NewGuid(), 0, [], null) : null
        };
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShotSchemaCapturesBoundsExactSourceChoicesAndTheCompleteDirectionShape(bool single)
    {
        var request = Request(single); var schema = ShotPlanner.ResponseSchemaFor(request);
        Assert.Equal(new[] { "contractVersion", "sceneStrategies", "shots" }, schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        var shots = schema.GetProperty("properties").GetProperty("shots");
        Assert.Equal(single ? 1 : 1000, shots.GetProperty("maxItems").GetInt32());
        var fields = shots.GetProperty("items").GetProperty("properties");
        Assert.Equal(8, fields.GetProperty("duration").GetProperty("maximum").GetDouble());
        Assert.Equal(1, fields.GetProperty("duration").GetProperty("minimum").GetDouble());
        Assert.Equal(request.SceneIds.Select(g => g.ToString()), fields.GetProperty("sceneId").GetProperty("enum").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(request.Script.Blocks.Select(b => b.Id.ToString()), fields.GetProperty("sourceBlockIds").GetProperty("items").GetProperty("enum").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(new[] { "speaker", "language", "text" }, fields.GetProperty("dialogue").GetProperty("items").GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(new[] { "intent", "staging", "editorial", "advanced" }, fields.GetProperty("direction").GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        var editorial = fields.GetProperty("direction").GetProperty("properties").GetProperty("editorial");
        Assert.Contains(editorial.GetProperty("required").EnumerateArray(), p => p.GetString() == "timing");
        Assert.Equal(1, editorial.GetProperty("properties").GetProperty("timing").GetProperty("minLength").GetInt32());
        Assert.False(fields.GetProperty("characters").GetProperty("items").GetProperty("properties").TryGetProperty("appearance", out _));
        var retained = request with { Strategies = [new() { SceneId = request.SceneIds[0], Approach = "Keep the existing approach." }] };
        Assert.Equal(single ? 0 : 1, ShotPlanner.ResponseSchemaFor(retained).GetProperty("properties").GetProperty("sceneStrategies").GetProperty("minItems").GetInt32());
    }

    [Fact]
    public void SourceEnumsExcludeUnselectedScenesAndUnsupportedAppearanceAssignments()
    {
        var request = Request(); var blocks = request.Script.Blocks.ToList();
        var otherScene = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. GARDEN — DAY");
        blocks.Add(otherScene); blocks.Add(ScriptBlock.Create(ScriptBlockKind.Action, "A bird lands."));
        request = request with { Script = request.Script with { Blocks = blocks } };
        var schema = ShotPlanner.ResponseSchemaFor(request).GetRawText();
        Assert.DoesNotContain(otherScene.Id.ToString(), schema);
        Assert.DoesNotContain(blocks[^1].Id.ToString(), schema);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShorterInstructionsKeepTheSameCapturedTaskAndDirectingGuidance(bool single)
    {
        var request = Request(single);
        var original = ShotPlanner.BuildMessages(request); var structured = ShotPlanner.BuildMessages(request, structured: true);
        Assert.Equal(original[1].Text, structured[1].Text);
        Assert.DoesNotContain("effectiveMaximumSeconds", structured[1].Text);
        Assert.Contains("requestedMaximumSeconds", structured[1].Text);
        Assert.True(structured[0].Text.Length < original[0].Text.Length);
        Assert.DoesNotContain("dialogue [{", structured[0].Text); Assert.DoesNotContain("contractVersion:", structured[0].Text);
        Assert.DoesNotContain("direction:{", structured[0].Text); Assert.DoesNotContain("\"duration\":5.0", structured[0].Text);
        Assert.Contains("sceneStrategies contains sceneId, approach, dramaticProgression, visualProgression, audienceInformation and spatialAnchors", structured[0].Text);
        Assert.Contains("intent holds purpose, reason, attention and relationships", structured[0].Text);
        Assert.Contains("every shot needs a distinct reason", structured[0].Text);
        Assert.Contains("Authored instructions override style", structured[0].Text);
        if (single) { Assert.Contains("alreadySpokenDialogue", structured[0].Text); Assert.Contains("before nextShot begins", structured[0].Text); }
    }

    [Fact]
    public void EnvironmentClaimsCanOnlyCiteAttachedViewsAndMetadataCannotClaimVisualSupport()
    {
        var request = Request(); var asset = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3 };
        var first = new EnvironmentViewCandidate("image/first", "Front", true, "", new() { AssetId = asset, MediaId = Guid.NewGuid() }, null, null, ReelViewSuggestions.Hash(bytes));
        var other = first with { Id = "image/unattached", Sha256 = new('B', 64) };
        var pool = new EnvironmentSourcePool(request.Script.ProjectId, asset, "Room", "A simple room.", "source", [first, other], [], new Dictionary<string, byte[]> { [first.Id] = bytes, [other.Id] = bytes });
        var environment = DraftingEnvironments.Snapshot(request.SceneIds[0], new() { AssetId = asset, Views = [first, other] }, pool);
        request = request with { Environments = [environment], InspectionImageHashes = [first.Sha256] };
        var schema = ShotPlanner.ResponseSchemaFor(request).GetRawText();
        Assert.Contains("Supported", schema); Assert.Contains(first.Id, schema); Assert.DoesNotContain(other.Id, schema);
        var metadata = request with { InspectionImageHashes = [] };
        Assert.DoesNotContain("Supported", ShotPlanner.ResponseSchemaFor(metadata).GetRawText());
        Assert.DoesNotContain("environmentAssessment:{", ShotPlanner.BuildMessages(metadata, structured: true)[0].Text);
        Assert.Contains("Additional environment view needed", ShotPlanner.BuildMessages(metadata, structured: true)[0].Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LiveVllmDraftShotsUseTheCompiledSchemaAndRetainTheSavedSource(bool single)
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_SCHEMA_URL");
        var modelId = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_SCHEMA_MODEL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(modelId)) Assert.Skip("Set LUMIBELLE_LIVE_SHOT_SCHEMA_URL and LUMIBELLE_LIVE_SHOT_SCHEMA_MODEL for neutral live drafting.");
        var ct = TestContext.Current.CancellationToken;
        var settings = new AiSettings { VllmUrl = url };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, modelId, "Local model", settings) with { MaxOutputTokens = 4096, Temperature = .2f };
        var request = Request(single) with { Selection = model,
            Instructions = "Use one concise shot for this tiny scene. For this diagnostic, use one short phrase per direction or strategy field." };
        var messages = ShotPlanner.BuildMessages(request, structured: true);
        var captured = new AiTextJobRequest(3, AiJobKind.ShotPlanning, model, false, settings, ShotPlanner.Profile(request), .2f, 1,
            JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), messages.Select(AiTextMessage.Capture).ToArray()) { ResponseSchema = ShotPlanner.ResponseSchemaFor(request) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(360));
        using var client = await new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), new FakeAiSettingsStore(), TestComfy.Monitor()).CreateAsync(AiBackend.Vllm, modelId, settings, timeout.Token);
        var report = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SHOT_SCHEMA_REPORT");
        var suffix = single ? "-single" : "-scene";
        var raw = new StringBuilder(); string? finish = null; var updates = 0; var reasoningUpdates = 0;
        await foreach (var progress in ((IProgressReportingChatClient)client).GetStreamingResponseWithProgressAsync(messages, TextGenerationOptions.Captured(captured), timeout.Token))
        {
            if (progress.Activity) reasoningUpdates++;
            if (progress.Response is { } update) { raw.Append(update.Text); if (update.FinishReason is { } reason) finish = reason.ToString(); }
            if (report is { Length: > 0 } && ++updates % 32 == 0) {
                await File.WriteAllTextAsync(report + suffix + "-raw.json", raw.ToString(), timeout.Token);
                await File.WriteAllTextAsync(report + suffix + "-status.json", JsonSerializer.Serialize(new { finish, updates, reasoningUpdates, rawCharacters = raw.Length }, AtomicJsonFile.Options), timeout.Token);
            }
        }
        var result = AiTextResults.Parse(captured, raw.ToString(), finish);
        if (report is { Length: > 0 }) {
            await File.WriteAllTextAsync(report + suffix + "-raw.json", raw.ToString(), timeout.Token);
            await File.WriteAllTextAsync(report + suffix + "-status.json", JsonSerializer.Serialize(new { finish,
                error = result.Error, updates, reasoningUpdates, rawCharacters = raw.Length }, AtomicJsonFile.Options), timeout.Token);
        }
        Assert.Equal("stop", finish); Assert.Null(result.Error);
        var plan = result.Read<ShotPlanningResult>()!; Assert.Single(plan.Shots);
        Assert.All(plan.Shots, shot => { Assert.Equal(request.SceneIds[0], shot.SceneId); Assert.InRange(shot.Duration!.Value, 1, request.MaximumSeconds); });
        if (report is { Length: > 0 })
            await File.WriteAllTextAsync(report + (single ? "-single.json" : "-scene.json"), JsonSerializer.Serialize(new { finish,
                shots = plan.Shots.Count, strategies = plan.Strategies.Count, uncoveredLines = plan.UncoveredDialogue.Count,
                sourceNotes = plan.SourceNotes.Count, directionNotes = plan.DirectionNotes.Count, dialogueNotes = plan.DialogueNotes.Count,
                coverageNotes = plan.CoverageNotes.Count, rawCharacters = raw.Length }, AtomicJsonFile.Options), timeout.Token);
    }
}

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(OpenAiServerKind.Vllm, false)] [InlineData(OpenAiServerKind.Vllm, true)]
    [InlineData(OpenAiServerKind.Standard, false)] [InlineData(OpenAiServerKind.Strata, true)]
    public async Task DraftShotsCaptureConstrainsOnlyNativeVllmAndRecoveryDoesNotResubmit(OpenAiServerKind kind, bool single)
    {
        _settings.Value = _settings.Value with { OpenAiConnections = [new() { Url = _settings.Value.VllmUrl, Kind = kind }] };
        _model = TextModelPolicy.Reference(AiBackend.Vllm, "test/model", "Local model", _settings.Value);
        var request = new ShotPlanningRequest(Approved, _assets.Library, [_script.Blocks[0].Id], 8, "Keep it calm.", _model) {
            CoverageOnly = true, SingleShot = single ? new(Guid.NewGuid(), 0, [], null) : null
        };
        var submission = await Capture.PlanShotsAsync(Guid.NewGuid(), Guid.NewGuid(), request, false, _ct);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(kind == OpenAiServerKind.Vllm, captured.ResponseSchema is not null);
        _providers.Chat.Output = Response(AiJobKind.ShotPlanning); var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Equal(kind == OpenAiServerKind.Vllm, _providers.Chat.Options!.RawRepresentationFactory is not null);
        _providers.Created = 0; _providers.OnCheck = () => throw new InvalidOperationException("Recovery must not connect.");
        Assert.Equal(AiJobState.Completed, (await Handler().RecoverAsync(await Recover(context), submission.Snapshot, _ct)).State);
        Assert.Equal(0, _providers.Created);
        var retained = await context.ReadOperationAsync<AiTextJobResult>("text", AiOperationArtifact.Output, _ct);
        Assert.Equal(_providers.Chat.Output, retained!.Raw);
        Assert.Null(retained.Error);
    }
}
