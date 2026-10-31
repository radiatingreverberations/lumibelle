using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class StructuredReelCompositionTests
{
    internal static JsonObject Reply() => new() {
        ["sections"] = new JsonObject {
            ["subject_definitions"] = "<Subject 1> is Riley (S1). <Picture 1> supplies appearance.",
            ["summary"] = "A readable reference of the same subject.",
            ["retention_analysis"] = "<Subject 1>: fully_preserved.",
            ["detailed_description"] = new JsonArray("Hold a wide front view.", "At 00:02.000, cut to a face close-up."),
            ["overall_soundscape"] = "Only the intended voice, or silence when no speech is supplied.",
            ["non_diegetic_music"] = "No non-diegetic music."
        }, ["useGuidance"] = "Intended face and outfit reference. Review the generated result."
    };

    [Theory]
    [InlineData(ReelVoiceMode.Silent)] [InlineData(ReelVoiceMode.NewVoice)] [InlineData(ReelVoiceMode.ExistingRecording)]
    public void SectionedRepliesRenderHeadingsNumberingDurationAndCapturedDialogue(ReelVoiceMode voice)
    {
        var draft = ReferenceReelTests.Recipe(ReelFraming.BodyToFace, voice);
        draft.Line = "Wait here. Don’t move."; draft.Language = "English";
        var raw = Reply().ToJsonString();
        var pair = ReferenceReels.Parse(raw, draft);
        Assert.Equal(ProductionPolicy.Sections, ProductionPolicy.Sections.Where(s => pair.Prompt.Contains(s + ":")));
        Assert.Contains("Exactly 5.167 seconds total.", pair.Prompt);
        Assert.Contains("[Shot 1] Hold", pair.Prompt); Assert.Contains("[Shot 2] At 00:02.000", pair.Prompt);
        if (voice == ReelVoiceMode.Silent) Assert.DoesNotContain("<d>", pair.Prompt);
        else Assert.Single(System.Text.RegularExpressions.Regex.Matches(pair.Prompt, "Riley \\(S1\\) says <d>\\[English\\] Wait here. Don’t move.</d>"));
        var request = new ReelCompositionRequest(Guid.NewGuid(), draft, ReferenceReels.Fingerprint(draft),
            new(draft.AssetId, "Riley", "", "", null, "", "", ""), []);
        var snapshot = new AiTextJobRequest(3, AiJobKind.ReelComposition, new(AiBackend.OpenRouter, "mock", "Mock"), false,
            new(), ReferenceReels.Profile, .5f, 1, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), []);
        var result = AiTextResults.Parse(snapshot, raw, "stop");
        Assert.Null(result.Error); Assert.Equal(raw, result.Raw); Assert.Equal(pair, result.Read<ReelPromptPair>());
        Assert.Null(AiTextResults.Parse(snapshot, raw, "length").Value);
    }

    // Liberal like shot compositions: a readable reply is kept with review notes, so it waits for its author.
    [Theory]
    [InlineData("missing-section", "left out summary")] [InlineData("extra-section", "Ignored an unexpected response field, extra.")]
    [InlineData("one-view", "one text instead of a list")] [InlineData("blank-view", "Dropped empty views")]
    [InlineData("empty-views", "left out detailed_description")] [InlineData("empty-guidance", "left out useGuidance")]
    [InlineData("extra-field", "Ignored an unexpected response field, extra.")] [InlineData("camel-case", null)]
    public void ReadableSectionedResponsesWithShapeProblemsAreKeptWithNotes(string kind, string? note)
    {
        var raw = Reply(); var sections = raw["sections"]!.AsObject();
        switch (kind) {
            case "missing-section": sections.Remove("summary"); break;
            case "extra-section": sections["extra"] = "Unexpected"; break;
            case "one-view": sections["detailed_description"] = "Hold a wide front view."; break;
            case "blank-view": sections["detailed_description"] = new JsonArray("Hold a wide front view.", " "); break;
            case "empty-views": sections["detailed_description"] = new JsonArray(); break;
            case "empty-guidance": raw["useGuidance"] = ""; break;
            case "extra-field": raw["extra"] = "Unexpected"; break;
            case "camel-case":
                sections["subjectDefinitions"] = sections["subject_definitions"]!.DeepClone(); sections.Remove("subject_definitions");
                sections["DetailedDescription"] = sections["detailed_description"]!.DeepClone(); sections.Remove("detailed_description");
                raw["UseGuidance"] = raw["useGuidance"]!.DeepClone(); raw.Remove("useGuidance");
                break;
        }
        var draft = ReferenceReelTests.Recipe();
        var pair = ReferenceReels.ParsePair(raw.ToJsonString(), draft);
        if (note is null)
        {
            Assert.Null(pair.Notes);
            Assert.Equal(ProductionPolicy.Sections, ProductionPolicy.Sections.Where(s => pair.Prompt.Contains(s + ":")));
            Assert.Equal("Intended face and outfit reference. Review the generated result.", pair.UseGuidance);
            return;
        }
        Assert.Contains(pair.Notes!, n => n.Contains(note, StringComparison.Ordinal));
        // Any adjustment keeps the pair from applying itself.
        Assert.NotNull(ReferenceReels.PromptWarning(pair, draft));
        Assert.Equal(pair.Notes, JsonSerializer.Deserialize<ReelPromptPair>(JsonSerializer.Serialize(pair, AtomicJsonFile.Options), AtomicJsonFile.Options)!.Notes);
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""{"sections":"none","useGuidance":"Nothing usable."}""")]
    [InlineData("""{"sections":{"summary":" ","detailed_description":[]},"useGuidance":"Empty."}""")]
    public void UnreadableSectionedResponsesStayBlocking(string raw) =>
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ParsePair(raw, ReferenceReelTests.Recipe()));

    [Fact]
    public void NaturalReferenceNamesAreReviewNotesAndLegacyTextIsNeverRewritten()
    {
        var draft = ReferenceReelTests.Recipe(); var original = ReferenceReels.Preset(draft);
        var pair = original with { Prompt = original.Prompt.Replace("<Picture 1>", "Picture 1") };
        var raw = JsonSerializer.Serialize(pair, AtomicJsonFile.Options);
        Assert.Equal(pair, ReferenceReels.Parse(raw, draft));
        Assert.Contains("exact <Picture 1> reference marker", ReferenceReels.PromptWarning(pair, draft));
        draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
        ReferenceReels.Validate(draft, true);
        Assert.Equal(pair.Prompt, draft.Prompt);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidatePair(pair with { UseGuidance = "" }, draft));
        Assert.Throws<WorkspaceStoreException>(() => ReferenceReels.ValidatePair(pair with { Prompt = "" }, draft));
    }
}

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(OpenAiServerKind.Vllm, true, false)] [InlineData(OpenAiServerKind.Strata, false, false)]
    [InlineData(OpenAiServerKind.Standard, false, false)] [InlineData(OpenAiServerKind.Standard, true, true)]
    public async Task ReelCaptureConstrainsSupportedServersAndKeepsItsSectionContract(OpenAiServerKind kind, bool constrained, bool verified)
    {
        _settings.Value = _settings.Value with { VllmUrl = "http://llm-server:8000/v1",
            OpenAiConnections = [new() { Url = "http://llm-server:8000/v1", Kind = kind }] };
        var model = TextModelPolicy.Reference(AiBackend.Vllm, "test/model", "Test model", _settings.Value);
        if (verified) _settings.Value = _settings.Value with { VllmTextModelBenchmarks = [OpenAiStructuredOutputTests.Verified(model.VllmUrl!, model.Model)] };
        var id = Guid.NewGuid(); var draft = ReferenceReelTests.Recipe(); draft.AssetId = _asset.Id; draft.PendingJobId = id;
        draft.Images = [new() { AssetId = _asset.Id, MediaId = _asset.Images[0].Id }];
        _assets.Library = _assets.Library with { ReelDrafts = [draft] };
        var submitted = await Capture.ComposeReelAsync(id, Guid.NewGuid(), _project, draft.Id, draft.Revision, model, false, _ct);
        var snapshot = submitted.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(constrained, snapshot.ResponseSchema is not null);
        Assert.Equal(constrained, TextGenerationOptions.Captured(snapshot).RawRepresentationFactory is not null);
        var header = new AiJobHeader { Id = id, Kind = submitted.Kind, Backend = submitted.Backend, Target = submitted.Target,
            ProjectName = "Test", TargetName = "Reel", OriginTabId = Guid.NewGuid(), RequestFingerprint = new('A', 64) };
        AiTextJobHandler.Read(header, submitted.Snapshot);
        if (!constrained) return;
        var sections = snapshot.ResponseSchema!.Value.GetProperty("properties").GetProperty("sections");
        Assert.Equal(ProductionPolicy.Sections, sections.GetProperty("required").EnumerateArray().Select(s => s.GetString()));
        Assert.False(sections.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("Lumibelle adds the six headings", string.Join("\n", snapshot.Messages[0].Parts.Select(p => p.Text)));
        var invalid = AiTextResults.Parse(snapshot, "{\"sections\":{}}", "stop");
        var failed = header with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain };
        var repair = AiTextRepairs.Capture(failed, snapshot, invalid);
        Assert.True(JsonElement.DeepEquals(snapshot.ResponseSchema.Value, repair.Contract.GetProperty("outputSchema")));
        var submission = await Capture.RepairAsync(Guid.NewGuid(), Guid.NewGuid(), failed, snapshot, repair, _ct);
        var corrected = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.True(JsonElement.DeepEquals(snapshot.ResponseSchema.Value, corrected.ResponseSchema!.Value));
        Assert.False(corrected.InspectsImages);
    }
}
