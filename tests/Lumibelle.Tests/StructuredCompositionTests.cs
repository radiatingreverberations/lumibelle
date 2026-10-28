using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static JsonObject SectionedReply(params string[] passages) => new() {
        // Reverse the fields: rendering must not depend on JSON property order.
        ["sections"] = new JsonObject {
            ["non_diegetic_music"] = "No non-diegetic music.", ["overall_soundscape"] = "Quiet room tone.",
            ["detailed_description"] = JsonSerializer.SerializeToNode(passages), ["retention_analysis"] = "Preserve the supplied appearance.",
            ["summary"] = "[reference generation] A quiet conversation.", ["subject_definitions"] = "Mira (S1) and Lee (S2) are the speakers."
        }, ["referenceUsage"] = "No reference media supplied."
    };

    [Fact]
    public void SectionedCompositionRendersExactSavedDialogueLanguagesSpeakerOrderAndTiming()
    {
        var shot = Ready() with { Duration = 6, Dialogue = [
            new() { Speaker = "Mira", Language = "en", Text = "Keep \"this\" exactly—please." },
            new() { Speaker = "Lee", Language = "Swedish", Text = "Ja, vi väntar." },
            new() { Speaker = "Mira", Language = "English", Text = "Good.\nThen we wait." }
        ] };
        var raw = SectionedReply("Mira puts down her cup.", "Lee looks at the clock.", "Mira nods.", "They settle into silence.").ToJsonString();
        var parsed = PromptComposer.Parse(raw, SoundRequest(shot));
        Assert.Empty(parsed.Notes);
        Assert.Equal(ProductionPolicy.Sections, ProductionPolicy.SectionNames(parsed.Prompt));
        Assert.Contains("[Shot 1] One continuous camera take lasting 6.583 seconds", parsed.Prompt);
        var lines = Regex.Matches(parsed.Prompt, @"<d>(.*?)</d>", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(shot.Dialogue.Select(line => $"[{line.Language}] {line.Text}"), lines);
        Assert.Equal(2, Regex.Matches(parsed.Prompt, @"Mira \(S1\) says").Count);
        Assert.Contains("Lee (S2) says", parsed.Prompt);
        Assert.True(parsed.Prompt.IndexOf("puts down her cup", StringComparison.Ordinal) < parsed.Prompt.IndexOf("<d>", StringComparison.Ordinal));
        Assert.True(parsed.Prompt.IndexOf("settle into silence", StringComparison.Ordinal) > parsed.Prompt.LastIndexOf("</d>", StringComparison.Ordinal));
    }

    [Fact]
    public void SilentAndConsecutiveDialogueShotsNeedNoInventedFillerOrTags()
    {
        var shot = Ready();
        var silent = PromptComposer.Parse(SectionedReply("The mouse looks up.").ToJsonString(), SoundRequest(shot));
        Assert.DoesNotContain("<d>", silent.Prompt); Assert.Empty(silent.Notes);
        shot.Dialogue = [new() { Speaker = "Mira", Language = "English", Text = "Ready?" }, new() { Speaker = "Lee", Language = "English", Text = "Ready." }];
        var paired = PromptComposer.Parse(SectionedReply("Mira turns.", "", "").ToJsonString(), SoundRequest(shot));
        Assert.Empty(paired.Notes); Assert.Equal(2, Regex.Matches(paired.Prompt, "<d>").Count);
    }

    [Theory]
    [InlineData("missing-section")] [InlineData("unknown-section")] [InlineData("empty-section")]
    [InlineData("too-many-passages")] [InlineData("too-few-passages")] [InlineData("wrong-type")]
    [InlineData("missing-usage")] [InlineData("duplicate-section")]
    public void MalformedSectionedRepliesCannotBeApplied(string defect)
    {
        var reply = SectionedReply("The mouse waits."); var sections = reply["sections"]!.AsObject();
        switch (defect)
        {
            case "missing-section": sections.Remove("summary"); break;
            case "unknown-section": sections["extra"] = "Unexpected"; break;
            case "empty-section": sections["summary"] = " "; break;
            case "too-many-passages": sections["detailed_description"] = new JsonArray("One", "Two"); break;
            case "too-few-passages": sections["detailed_description"] = new JsonArray(); break;
            case "wrong-type": sections["detailed_description"] = "Flat prose"; break;
            case "missing-usage": reply.Remove("referenceUsage"); break;
        }
        var raw = reply.ToJsonString();
        if (defect == "duplicate-section") raw = raw.Replace("\"summary\":", "\"summary\":\"Duplicate\",\"summary\":", StringComparison.Ordinal);
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.Parse(raw, SoundRequest(Ready())));
    }

    [Fact]
    public void ReferenceQualityFindingsRemainAdvisoryInAWellFormedSectionedReply()
    {
        var parsed = PromptComposer.Parse(SectionedReply("The mouse looks at <Picture 9>.").ToJsonString(), SoundRequest(Ready()));
        Assert.Contains("reference that is not selected", Assert.Single(parsed.Notes));
    }

    [Fact]
    public void StructuredInstructionsLeaveDialogueMarkupAndDurationToTheRenderer()
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Mira", Language = "English", Text = "Wait here." }];
        var messages = PromptComposer.BuildMessages(SoundRequest(shot), [], structured: true);
        Assert.Contains("exactly 2 action passages", messages[0].Text);
        Assert.DoesNotContain("Use exactly NAME", messages[0].Text);
        Assert.DoesNotContain("<d>[Language]", messages[0].Text);
        Assert.DoesNotContain("exactly two string fields", messages[0].Text);
        Assert.Contains("Lumibelle adds the [Shot 1]", messages[0].Text);
        var data = string.Join("\n", messages[1].Contents.OfType<TextContent>().Select(p => p.Text));
        Assert.Contains("Dialogue insertion plan", data); Assert.Contains("afterPassage", data); Assert.Contains("(S1)", data);
        Assert.DoesNotContain("Dialogue insertion plan", PromptComposer.BuildMessages(SoundRequest(shot), [])[1].Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositionWithoutRecordingsOmitsAudioReferenceRulesAndDisabledVoiceChoices(bool structured)
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Mira", Text = "Wait here." }];
        shot.CharacterVoices = [new() { AssetId = Guid.NewGuid(), CharacterName = "Mira", Source = CharacterVoiceSource.None },
            new() { AssetId = Guid.NewGuid(), CharacterName = "Lee", Source = CharacterVoiceSource.Unselected }];
        var messages = PromptComposer.BuildMessages(SoundRequest(shot), [], structured: structured);
        Assert.Contains("No reference recordings are selected", messages[0].Text);
        Assert.DoesNotContain("<Audio N>", messages[0].Text);
        Assert.DoesNotContain("Audio definition line", messages[0].Text);
        Assert.DoesNotContain("supplied voice slots", messages[0].Text);
        Assert.Contains("overall_soundscape", messages[0].Text); Assert.Contains("non_diegetic_music", messages[0].Text);
        using var data = JsonDocument.Parse(messages[1].Contents.OfType<TextContent>().First().Text);
        Assert.Empty(data.RootElement.GetProperty("characterVoices").EnumerateArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositionIncludesAudioGuidanceOnlyForSelectedRecordings(bool structured)
    {
        var (_, _, _, shot) = CharacterVoiceTests.Fixture();
        var withAudio = PromptComposer.BuildMessages(SoundRequest(shot), [], structured: structured)[0].Text;
        Assert.Contains("<Audio 1>", withAudio);
        Assert.Contains("On each mapped Audio definition line", withAudio);
        shot.Videos[0].UseSoundtrack = false;
        var withoutAudio = PromptComposer.BuildMessages(SoundRequest(shot), [], structured: structured)[0].Text;
        Assert.DoesNotContain("<Audio N>", withoutAudio); Assert.DoesNotContain("<Audio 1>", withoutAudio);
        Assert.DoesNotContain("On each mapped Audio definition line", withoutAudio);
        Assert.Contains("No reference recordings are selected", withoutAudio);
    }

    [Theory]
    [InlineData(OpenAiServerKind.Vllm, true, false)] [InlineData(OpenAiServerKind.Strata, false, false)] [InlineData(OpenAiServerKind.Standard, false, false)]
    [InlineData(OpenAiServerKind.Standard, true, true)]
    public async Task CompositionCaptureConstrainsSupportedServersAndPreservesTheCapturedDialogue(OpenAiServerKind kind, bool constrained, bool verified)
    {
        var f = Fixture(); var shot = Ready() with { ApprovedScriptId = null, SceneId = null };
        shot.Dialogue = [new() { Speaker = "Mira", Language = "English", Text = "Wait here." }];
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var production = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "section-jobs"), _clock));
        var composition = (await production.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        var settings = new FakeAiSettingsStore { Value = new() { VllmUrl = "http://llm-server:8000/v1",
            OpenAiConnections = [new() { Url = "http://llm-server:8000/v1", Kind = kind }] } };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "test/model", "Local model", settings.Value);
        if (verified) settings.Value = settings.Value with { VllmTextModelBenchmarks = [OpenAiStructuredOutputTests.Verified(model.VllmUrl!, model.Model)] };
        var capture = new AiTextJobCapture(settings, projects, f.Assets, null!, null!, production: production, shots: f.Shots);
        var submission = await capture.ComposeAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, composition.Id, composition.Version, model, false, _ct);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(constrained, snapshot.ResponseSchema is not null);
        Assert.Equal(constrained, TextGenerationOptions.Captured(snapshot).RawRepresentationFactory is not null);
        Assert.Null(snapshot.ResponseGrammar);
        Assert.NotNull(AiTextJobHandler.Read(new() { Id = submission.Id, Kind = AiJobKind.PromptComposition, Backend = AiBackend.Vllm,
            Target = submission.Target, ProjectName = "Test", TargetName = "Compose", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" }, submission.Snapshot));
        if (constrained)
        {
            Assert.Equal(3, snapshot.Version);
            var schema = snapshot.ResponseSchema!.Value.GetProperty("anyOf")[0].GetProperty("properties").GetProperty("sections");
            Assert.Equal(ProductionPolicy.Sections, schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var detail = schema.GetProperty("properties").GetProperty("detailed_description");
            Assert.Equal(2, detail.GetProperty("minItems").GetInt32()); Assert.Equal(2, detail.GetProperty("maxItems").GetInt32());
        }
        shot.Dialogue[0].Text = "Changed later.";
        var raw = SectionedReply("Mira turns.", "She waits.").ToJsonString();
        var result = AiTextResults.Parse(snapshot, raw, "stop");
        Assert.Null(result.Error); Assert.Equal(raw, result.Raw);
        Assert.Contains("[English] Wait here.", result.Read<PromptCompositionResult>()!.Prompt);
        Assert.DoesNotContain("Changed later", result.Read<PromptCompositionResult>()!.Prompt);
        Assert.Equal(composition.Prompt, (await production.LoadAsync(f.Project.Id, _ct)).Compositions[0].Prompt);
    }

    [Fact]
    public async Task SectionSchemaIsSentThroughTheActualStreamingOpenAiClient()
    {
        var shot = Ready(); var schema = PromptComposer.ResponseSchemaFor(shot);
        var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("data: {\"id\":\"test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"local/model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        }));
        var settings = new AiSettings { VllmUrl = "http://llm-server:8000/v1" };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "local/model", "Local model", settings);
        var request = new AiTextJobRequest(3, AiJobKind.PromptComposition, model, false, settings, ProductionPolicy.Profile, .7f, 1,
            JsonSerializer.SerializeToElement(SoundRequest(shot)), [new("user", [new(Text: "Compose a neutral shot.")])]) { ResponseSchema = schema };
        using var client = await new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor()).CreateAsync(AiBackend.Vllm, model.Model, settings, _ct);
        await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "Compose a neutral shot.")], TextGenerationOptions.Captured(request), _ct)) { }
        using var body = JsonDocument.Parse(Assert.Single(http.Requests).Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        Assert.True(JsonElement.DeepEquals(schema, body.RootElement.GetProperty("structured_outputs").GetProperty("json")));
    }

    [Fact]
    public void LegacyCompositionRepliesAndClarificationRequestsStillWork()
    {
        var shot = Ready(); var legacy = H3Policy.Compile(shot);
        var raw = JsonSerializer.Serialize(new { prompt = legacy, referenceUsage = "No references." });
        Assert.Equal(legacy, PromptComposer.Parse("```json\n" + raw + "\n```", SoundRequest(shot)).Prompt);
        var error = Assert.Throws<WorkspaceStoreException>(() => PromptComposer.Parse("{\"needsInput\":\"Choose the intended reference.\"}", SoundRequest(shot)));
        Assert.Contains("Choose the intended reference", error.Message);
    }

    [Fact]
    public async Task LiveVllmSectionedCompositionUsesTheCompiledClientAndPassesOptionalValidation()
    {
        var url = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SECTIONED_URL");
        var modelId = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SECTIONED_MODEL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(modelId)) Assert.Skip("Set LUMIBELLE_LIVE_SECTIONED_URL and LUMIBELLE_LIVE_SECTIONED_MODEL for a neutral live test.");
        var settings = new AiSettings { VllmUrl = url };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, modelId, "Local model", settings) with { MaxOutputTokens = 1400, Temperature = .2f };
        var shot = Ready() with { Title = "Tea", Description = "Mira waits beside a kettle, glances toward the doorway and asks a question.",
            Duration = 6, Dialogue = [new() { Speaker = "Mira", Language = "English", Text = "Is the tea ready?" }] };
        shot.CharacterVoices = [new() { AssetId = Guid.NewGuid(), CharacterName = "Mira", Source = CharacterVoiceSource.None },
            new() { AssetId = Guid.NewGuid(), CharacterName = "Lee", Source = CharacterVoiceSource.None }];
        var composition = SoundRequest(shot) with { Model = model };
        var messages = PromptComposer.BuildMessages(composition, [], structured: true);
        messages.Add(new(ChatRole.User, "For this diagnostic, use one brief sentence in each prose section and very short action passages. The subject waits beside a kettle in a quiet room. No references or music."));
        var request = new AiTextJobRequest(3, AiJobKind.PromptComposition, model, false, settings, ProductionPolicy.Profile, .2f, 1,
            JsonSerializer.SerializeToElement(composition, AtomicJsonFile.Options), messages.Select(AiTextMessage.Capture).ToArray()) { ResponseSchema = PromptComposer.ResponseSchemaFor(shot) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct); timeout.CancelAfter(TimeSpan.FromSeconds(180));
        using var client = await new AiProviderRegistry(new TestHttpFactory(new HttpClientHandler()), new FakeAiSettingsStore(), TestComfy.Monitor()).CreateAsync(AiBackend.Vllm, modelId, settings, timeout.Token);
        var raw = new StringBuilder(); string? finish = null;
        await foreach (var update in client.GetStreamingResponseAsync(messages, TextGenerationOptions.Captured(request), timeout.Token))
        { raw.Append(update.Text); if (update.FinishReason is { } reason) finish = reason.ToString(); }
        var result = AiTextResults.Parse(request, raw.ToString(), finish);
        Assert.Equal("stop", finish); Assert.Null(result.Error);
        var value = result.Read<PromptCompositionResult>()!;
        Assert.Empty(value.Notes);
        Assert.DoesNotContain("<Audio", value.Prompt);
        Assert.Contains("Mira (S1) says <d>[English] Is the tea ready?</d>", value.Prompt);
        if (Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_SECTIONED_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { finish, error = result.Error, notes = value.Notes.Count,
                sections = ProductionPolicy.SectionNames(value.Prompt), promptCharacters = value.Prompt.Length, rawCharacters = raw.Length }, AtomicJsonFile.Options), timeout.Token);
    }
}
