using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ShotDubbingTests
{
    internal static readonly ProjectLanguage English = new("en", "English");
    internal static readonly ProjectLanguage Swedish = new("sv", "Swedish");
    internal static (VideoSnapshot Source, ShotDubRequest Request) Fixture(Guid? projectId = null, bool media = false)
    {
        var project = projectId ?? Guid.NewGuid();
        var shot = new Shot { Title = "At the door", ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid(),
            Characters = [new(Guid.NewGuid(), "Alice")],
            Duration = 5, Description = "Alice waits by the door.", SourceExcerpt = "A short exchange at the door.",
            Dialogue = [new() { Speaker = "Alice", Language = "English", Text = "We are leaving." },
                        new() { Speaker = "Alice", Language = "English", Text = "Are you ready?" }] };
        if (media)
        {
            shot.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Appearance", Crop = new() { X = .1, Y = .1, Width = .8, Height = .8 } });
            shot.Voices.Add(new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Alice", Start = 1, Duration = 3 });
        }
        var prompt = "subject_definitions:\n<Subject 1> Alice, an adult wearing a coat." +
            (media ? " Uses <Picture 1>.\nAlice (S1) uses <Audio 1> as her voice." : "") + "\n" +
            "summary:\nOne continuous take lasting 5 seconds.\n" +
            "retention_analysis:\nKeep the face, coat and door.\n" +
            "detailed_description:\n[Shot 1] Locked camera. Alice (S1): <d>[English] We are leaving.</d>\n" +
            "Alice (S1): <d>[English] Are you ready?</d>\n" +
            "overall_soundscape:\nQuiet room tone.\nnon_diegetic_music:\nNo background music.";
        ProductionPolicy.ValidatePrompt(prompt, shot);
        var settings = new H3Settings(); var size = VideoResolutions.Size(shot);
        var images = shot.Images.Select(i => new CompositionInput(i.Id, new string('A', 64))).ToArray();
        var revision = new CompositionPromptRevision(Guid.NewGuid(), DateTimeOffset.UtcNow, prompt, "Keep the references.",
            new string('B', 64), ProductionPolicy.SourceFingerprint(shot), Images: images);
        var source = new VideoSnapshot(project, 0, shot, prompt, H3Policy.Fingerprint(shot), "http://comfy.test:8188",
            settings, size.Width, size.Height, H3Policy.Frames(shot.Duration.Value), ProductionPolicy.Profile) {
            Production = new(Guid.NewGuid(), 1, "Master setup", revision, images),
            OutputPolicy = new(false), Sampling = H3Policy.Sampling(shot, settings), Preset = H3Presets.Capture(shot, settings),
            Performance = H3Performance.Capture(H3Presets.NewPerformance(settings))
        };
        var languages = new ProjectLanguages { ProjectId = project, Main = English, Dubs = [Swedish] };
        var request = new ShotDubRequest(1, project, shot.Id, Guid.NewGuid(), Guid.NewGuid(), 0,
            ShotDubbing.Hash(source), ShotDubbing.References(source), English, Swedish, prompt,
            ShotCopy.Of(shot.Dialogue), shot.Duration.Value, shot.SourceExcerpt, "", ShotDubbing.LanguageFingerprint(languages, Swedish));
        return (source, request);
    }
    internal static ShotDubTranslation Translation(ShotDubRequest r) => new(
        [new(r.Dialogue[0].Id, "Nu åker vi."), new(r.Dialogue[1].Id, "Är du redo?")], ["Review spoken pacing."]);
    internal static ShotDubVariant Variant(ShotDubRequest r) => new(r.VariantId, 1, r, Translation(r),
        ShotDubbing.Prompt(r, Translation(r).Lines), DateTimeOffset.UtcNow);
    private static string Raw(ShotDubTranslation t) => JsonSerializer.Serialize(new { version = 1, lines = t.Lines.Select(l => new { id = l.Id, text = l.Text }), notes = t.Notes });
    private static string OutsideDialogue(string prompt) => Regex.Replace(prompt, @"<d>.*?</d>", "<d>UNCHANGED SLOT</d>", RegexOptions.Singleline);

    [Theory]
    [InlineData(" sv ", " Swedish ", "sv", "Swedish")]
    [InlineData("zh-CN", "Mandarin Chinese", "zh-cn", "Mandarin Chinese")]
    [InlineData("pt-BR", "Brazilian Portuguese", "pt-br", "Brazilian Portuguese")]
    public void LanguageCodesAreNormalizedWithoutGuessingTheMaster(string code, string name, string expectedCode, string expectedName)
    {
        Assert.Equal(new(expectedCode, expectedName), ShotDubbing.Language(new(code, name)));
        Assert.Null(ShotDubbing.Normalize(new() { ProjectId = Guid.NewGuid() }).Main);
    }
    [Theory]
    [InlineData("", "English")][InlineData("english", "English")][InlineData("en_US", "English")]
    [InlineData("en", "")][InlineData("en", "<d>English")][InlineData("en", "English\n")]
    public void BadLanguagesDoNotEnterSpeechTags(string code, string name)
    {
        // Trailing whitespace is deliberately normalized, so use an internal control character.
        if (name == "English\n") name = "Eng\nlish";
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Language(new(code, name)));
    }
    [Theory]
    [InlineData("duplicate")][InlineData("main")][InlineData("missing-main")][InlineData("too-many")][InlineData("notes")]
    public void InvalidProjectLanguageSetsAreRejected(string kind)
    {
        var p = new ProjectLanguages { ProjectId = Guid.NewGuid(), Main = English, Dubs = [Swedish] };
        p = kind switch {
            "duplicate" => p with { Dubs = [Swedish, new("SV", "Swedish")] },
            "main" => p with { Dubs = [English] },
            "missing-main" => p with { Main = null },
            "too-many" => p with { Dubs = Enumerable.Range(0, 17).Select(i => new ProjectLanguage("sv-" + i.ToString("00"), "Swedish " + i)).ToArray() },
            _ => p with { TranslationNotes = new string('x', 12001) }
        };
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Normalize(p));
    }
    [Fact]
    public void OnlyChangesToTheUsedLanguagePairOrGlossaryStaleTheRequest()
    {
        var (_, r) = Fixture(); var p = new ProjectLanguages { ProjectId = r.ProjectId, Main = English, Dubs = [Swedish] };
        ShotDubbing.CheckLanguages(p with { Dubs = [Swedish, new("de", "German")], Revision = 5 }, r);
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.CheckLanguages(p with { TranslationNotes = "A new glossary" }, r));
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.CheckLanguages(p with { Main = new("fr", "French") }, r));
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.CheckLanguages(p with { Dubs = [new("sv", "Svenska")] }, r));
    }
    [Fact]
    public void StrictTranslationPreservesAllNonDialogueBytesIncludingCrlfAndDollarSigns()
    {
        var (_, r) = Fixture(); r = r with { SourcePrompt = r.SourcePrompt.Replace("\n", "\r\n") };
        var translation = Translation(r) with { Lines = [new(r.Dialogue[0].Id, "Det kostar $5, {Alice}."), new(r.Dialogue[1].Id, "Är du redo?")] };
        var parsed = ShotDubbing.Parse(Raw(translation), r);
        var result = ShotDubbing.Prompt(r, parsed.Lines);
        Assert.Equal(OutsideDialogue(r.SourcePrompt), OutsideDialogue(result));
        Assert.Contains("<d>[Swedish] Det kostar $5, {Alice}.</d>", result);
        Assert.Contains("Alice (S1):", result);
    }
    [Theory]
    [InlineData("missing")][InlineData("extra")][InlineData("duplicate")][InlineData("reordered")][InlineData("invented")]
    public void LineIdentityAndOrderCannotChange(string kind)
    {
        var (_, r) = Fixture(); var valid = Translation(r).Lines;
        IReadOnlyList<DubLine> lines = kind switch {
            "missing" => valid.Take(1).ToArray(), "extra" => [.. valid, new(Guid.NewGuid(), "Extra")],
            "duplicate" => [valid[0], valid[0]], "reordered" => valid.Reverse().ToArray(),
            _ => [new(Guid.NewGuid(), valid[0].Text), valid[1]]
        };
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Parse(Raw(new(lines, [])), r));
    }
    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("one\ntwo")][InlineData("one\ttwo")][InlineData("<Audio 3>")][InlineData("</d><d>[English] Bad")]
    public void SpeechCannotInjectPromptMarkup(string text)
    {
        var (_, r) = Fixture(); var t = Translation(r) with { Lines = [new(r.Dialogue[0].Id, text), Translation(r).Lines[1]] };
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Prompt(r, t.Lines));
    }
    [Theory]
    [InlineData("not-json")][InlineData("{\"version\":1}")][InlineData("{\"version\":1,\"lines\":[],\"notes\":[],\"prompt\":\"new scene\"}")]
    [InlineData("{\"version\":1,\"version\":1,\"lines\":[],\"notes\":[]}")]
    [InlineData("{\"version\":2,\"lines\":[],\"notes\":[]}")]
    public void MalformedOrExpandedOutputContractsAreNotApplied(string raw)
    { var (_, r) = Fixture(); Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Parse(raw, r)); }
    [Fact]
    public void PromptsAreTextOnlyAndReturnNoRewriteOrReferenceFields()
    {
        var (_, r) = Fixture(); var messages = ShotDubbing.Messages(r);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages.SelectMany(m => m.Contents), c => c is Microsoft.Extensions.AI.DataContent);
        Assert.Contains(r.SourcePrompt.Split('\n')[0], messages[1].Text);
        Assert.Contains("Return exactly JSON", messages[0].Text);
    }
    [Fact]
    public void ApplyingADubCopiesTheSnapshotAndPreservesEveryReferenceAndSetting()
    {
        var (source, r) = Fixture(media: true); var before = ShotDubbing.Hash(source); var variant = Variant(r);
        var result = ShotDubbing.Apply(source, variant);
        Assert.Equal(before, ShotDubbing.Hash(source));
        Assert.NotSame(source.Shot, result.Shot);
        Assert.Equal(ShotDubbing.References(source), ShotDubbing.References(result));
        Assert.Equal(OutsideDialogue(source.Prompt), OutsideDialogue(result.Prompt));
        Assert.True(ShotDubbing.Same(source.Shot.Images, result.Shot.Images));
        Assert.True(ShotDubbing.Same(source.Shot.Voices, result.Shot.Voices));
        Assert.True(ShotDubbing.Same(source.Shot.Videos, result.Shot.Videos));
        Assert.Equal(source.Production!.Images, result.Production!.Images);
        Assert.Equal(source.FrameCount, result.FrameCount);
        Assert.Equal(source.ComfyUrl, result.ComfyUrl);
        Assert.Equal(r.SourceTakeId, result.Dub!.SourceTakeId);
        Assert.All(result.Shot.Dialogue, d => Assert.Equal("Swedish", d.Language));
        Assert.Equal(source.Shot.Dialogue.Select(d => (d.Id, d.Speaker)), result.Shot.Dialogue.Select(d => (d.Id, d.Speaker)));
        Assert.DoesNotContain("\"dub\"", JsonSerializer.Serialize(source, AtomicJsonFile.Options));
        ShotDubbing.ValidateSnapshot(ShotCopy.Of(result));
    }
    [Theory]
    [InlineData("prompt")][InlineData("voice")][InlineData("image")][InlineData("speaker")][InlineData("language")][InlineData("duration")]
    public void CapturedDubCorruptionIsRejected(string kind)
    {
        var (source, r) = Fixture(media: true); var result = ShotDubbing.Apply(source, Variant(r));
        switch (kind) {
            case "prompt": result = result with { Prompt = result.Prompt.Replace("Locked camera.", "Moving camera.") }; break;
            case "voice": result.Shot.Voices[0].Start += 1; break;
            case "image": result.Shot.Images[0].Crop = null; break;
            case "speaker": result.Shot.Dialogue[0].Speaker = "Bob"; break;
            case "language": result.Shot.Dialogue[0].Language = "English"; break;
            default: result.Shot.Duration = 6; break;
        }
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.ValidateSnapshot(result));
    }
    [Fact]
    public void TakesRenderedUnderOlderPromptRulesStayTranslatable()
    {
        // The master already rendered with this prompt; only its dialogue blocks are replaced, so newer
        // authoring rules elsewhere in the prompt do not block a language version.
        var (source, r) = Fixture();
        var older = source with { Prompt = source.Prompt.Replace("[Shot 1] ", "") };
        Assert.NotNull(ProductionPolicy.PromptWarning(older.Prompt, older.Shot));
        Assert.Null(ShotDubbing.SourceIssue(new() { AiJobId = Guid.NewGuid(), Snapshot = older }));
        var request = r with { SourcePrompt = older.Prompt, SourceFingerprint = ShotDubbing.Hash(older) };
        var dub = ShotDubbing.Apply(older, Variant(request));
        Assert.Contains("<d>[Swedish] Nu åker vi.</d>", dub.Prompt);
        Assert.Equal(OutsideDialogue(older.Prompt), OutsideDialogue(dub.Prompt));
        // A prompt whose dialogue blocks do not match the lines still cannot be translated.
        var missing = source with { Prompt = source.Prompt.Replace("Alice (S1): <d>[English] Are you ready?</d>\n", "") };
        Assert.Contains("<d> block per dialogue line", ShotDubbing.SourceIssue(new() { AiJobId = Guid.NewGuid(), Snapshot = missing }));
    }
    [Fact]
    public void ChangedMasterCannotReceiveAnOldTranslationAndDubsCannotBecomeTranslationSources()
    {
        var (source, r) = Fixture(); var variant = Variant(r); var changed = source with { Prompt = source.Prompt + " " };
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Apply(changed, variant));
        var result = ShotDubbing.Apply(source, variant);
        Assert.Throws<WorkspaceStoreException>(() => ShotDubbing.Apply(result, variant));
        Assert.NotNull(ShotDubbing.SourceIssue(new() { AiJobId = Guid.NewGuid(), Snapshot = result }));
    }
    [Fact]
    public void LatestMasterCannotSelectANewerDubAndMissingDubsDoNotFallBackToMasterSpeech()
    {
        var (source, r) = Fixture(); var master = new ShotTake { ShotId = source.Shot.Id, Snapshot = source, CreatedUtc = DateTimeOffset.UtcNow };
        var dub = master with { Id = Guid.NewGuid(), Snapshot = ShotDubbing.Apply(source, Variant(r)), CreatedUtc = master.CreatedUtc.AddHours(1) };
        var silent = new Shot { Title = "Silent cutaway" }; var silentTake = master with { Id = Guid.NewGuid(), ShotId = silent.Id, Snapshot = source with { Shot = silent } };
        var d = new ShotDocument { ProjectId = r.ProjectId, Shots = [source.Shot, silent], Takes = [master, dub, silentTake] };
        Assert.Equal(master.Id, CutTakeSelection.LatestByShot(d)[source.Shot.Id]);
        Assert.Equal(dub.Id, CutTakeSelection.LatestByShot(d, language: "sv")[source.Shot.Id]);
        Assert.Equal(silentTake.Id, CutTakeSelection.LatestByShot(d, language: "sv")[silent.Id]);
        Assert.False(CutTakeSelection.LatestByShot(d, language: "de").ContainsKey(source.Shot.Id));
        Assert.True(TakeLanguages.Matches(dub, "*")); Assert.False(TakeLanguages.Matches(dub, null));
        Assert.Equal(Swedish, Assert.Single(TakeLanguages.Choices(d.Takes)));
        Assert.Contains("[Swedish]", TakeDisplay.Label(dub));
    }
    [Fact]
    public void TranslationTargetsRequireMasterTakeIdentityAndDoNotAliasTheMasterComposer()
    {
        var target = new AiJobTarget(Guid.NewGuid(), ShotId: Guid.NewGuid(), TakeId: Guid.NewGuid());
        target.Validate(AiJobKind.ShotTranslation);
        Assert.Throws<WorkspaceStoreException>(() => (target with { TakeId = null }).Validate(AiJobKind.ShotTranslation));
        Assert.NotEqual(target.LockKey(AiJobKind.Video), target.LockKey(AiJobKind.ShotTranslation));
        Assert.Equal(15, (int)AiJobKind.AssetPicking); Assert.Equal(16, (int)AiJobKind.ShotTranslation);
    }
}
