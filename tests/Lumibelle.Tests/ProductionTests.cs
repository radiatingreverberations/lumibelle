using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task ProductionImportIsIdempotentAndAlternativesNeverEditCoverage()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        var originalBytes = await File.ReadAllBytesAsync(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "shots.json"), _ct);
        var first = await store.InitializeAsync(f.Project.Id, _ct); var second = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Equal(first.Revision, second.Revision); var c = Assert.Single(first.Compositions); Assert.Equal("Default setup", c.Name); Assert.Null(c.Accepted); Assert.Empty(c.Prompt);
        var copy = c.Copy(); copy.Id = Guid.NewGuid(); copy.Name = "Close view"; copy.Shot.Description = "A close view of the same arrival.";
        var saved = await store.SaveAsync(f.Project.Id, copy, 0, _ct);
        Assert.Equal(2, saved.Compositions.Count); Assert.Equal(source.Description, saved.Compositions[0].Shot.Description);
        Assert.Null(saved.Compositions[1].Accepted); Assert.Empty(saved.Compositions[1].History);
        var reset = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Equal(source.Description, reset.Shots[0].Description); Assert.Equal(source.Id, reset.Shots[0].Id);
        using var serialized = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "production.json"), _ct));
        Assert.False(serialized.RootElement.GetProperty("compositions")[0].TryGetProperty("shot", out _));
        var changed = c.Copy(); changed.DirectingNotes = "Stay close"; await store.SaveAsync(f.Project.Id, changed, c.Version, _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(f.Project.Id, c, c.Version, _ct));
    }

    [Fact]
    public async Task ProductionCapturesAcceptedPromptAndRequiresReviewAfterSourceChanges()
    {
        var f = Fixture(); var (source, scripts) = ApprovedShot(f.Project.Id);
        var doc = await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = H3Policy.Compile(source).Replace(source.Description, "A deliberate camera push reveals the mouse looking up.");
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        var capture = new AiVideoJobCapture(f.Shots, scripts, f.Assets, new FakeAiSettingsStore(), new MockVideoGenerator(f.Assets, f.Shots), projects, new FakeProjectAiPreferencesStore(), store);
        var submission = await capture.CaptureCompositionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, c.Id, c.Version, 2, 123, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(c.Prompt, request.Snapshot.Prompt); Assert.NotEqual(H3Policy.Compile(source), request.Snapshot.Prompt);
        Assert.Equal(c.Id, submission.Target.CompositionId); Assert.Equal(ProductionPolicy.Profile, request.Snapshot.Profile);
        AiVideoJobPolicy.Validate(request);
        var savedPrompt = request.Snapshot.Prompt;
        doc = await f.Shots.LoadAsync(f.Project.Id, _ct); doc.Shots[0].Description = "A different story action."; await f.Shots.SaveAsync(f.Project.Id, doc.Shots, doc.Revision, ct: _ct);
        var nextCapture = await capture.CaptureCompositionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, c.Id, c.Version, 1, null, _ct);
        Assert.Equal("A different story action.", nextCapture.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.Shot.Description);
        AiVideoJobPolicy.Validate(request); Assert.Equal(savedPrompt, request.Snapshot.Prompt);
        Assert.Null(ProductionPolicy.Issue(c, await f.Assets.LoadAsync(f.Project.Id, _ct), await f.Shots.LoadAsync(f.Project.Id, _ct), f.Project));
    }

    [Fact]
    public async Task ProductionInspectsOrderedCropsAndCapturesTheirIdentity()
    {
        var f = Fixture(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        for (var i = 0; i < 2; i++) { using var png = new MemoryStream(AssetStoreTests.Png(80, 40)); library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, png, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct); }
        var shot = Ready(); shot.Images = library.Assets[0].Images.Select((i, n) => new ShotImageBinding { AssetId = asset.Id, MediaId = i.Id, Name = "Room", Crop = n == 0 ? new() { Width = .5, Height = 1 } : new() { Width = 1, Height = .5 } }).ToList();
        var images = await ProductionInputs.CaptureAsync(f.Project.Id, shot, f.Assets, _ct);
        using var first = Image.Load(images[0].Bytes); using var second = Image.Load(images[1].Bytes);
        Assert.Equal((40, 40), (first.Width, first.Height)); Assert.Equal((80, 20), (second.Width, second.Height));
        var r = new PromptCompositionRequest(f.Project.Id, Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], ShotReferences.Resolve(shot, library, new()), [], images.Select(i => i.Identity).ToArray(), "Directing", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var messages = PromptComposer.BuildMessages(r, images.Select(i => i.Bytes).ToArray());
        Assert.Equal(2, messages[1].Contents.OfType<DataContent>().Count()); Assert.Contains("exact selected crops", messages[0].Text);
        Assert.Equal(images[0].Bytes, messages[1].Contents.OfType<DataContent>().First().Data.ToArray());
        var reversed = shot.Copy(); reversed.Images.Reverse();
        Assert.Equal(images.Reverse().Select(i => i.Identity), (await ProductionInputs.CaptureAsync(f.Project.Id, reversed, f.Assets, _ct)).Select(i => i.Identity));
    }

    [Theory]
    [InlineData("dialogue")]
    [InlineData("reference")]
    [InlineData("section")]
    [InlineData("extra-shot")]
    [InlineData("undefined-subject")]
    public void ProductionRejectsInvalidPromptContracts(string failure)
    {
        var shot = Ready(); shot.Dialogue.Add(new() { Speaker = "Mira", Language = "Swedish", Text = "Du kallade?" });
        var prompt = H3Policy.Compile(shot); ProductionPolicy.ValidatePrompt(prompt, shot);
        prompt = failure switch { "dialogue" => prompt.Replace("Du kallade?", "Hej!"), "reference" => prompt + "\n<Picture 9>", "section" => prompt.Replace("summary:", "overview:"), "extra-shot" => prompt + "\n[Shot 2]", _ => prompt + "\n<Subject 8>" };
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt, shot));
    }

    [Fact]
    public void CoveragePlanningDoesNotAssignAssetsAndKeepsLegacyParser()
    {
        var project = Guid.NewGuid(); var (_, scripts) = ApprovedShot(project);
        var request = new ShotPlanningRequest(scripts.Approved!, new() { ProjectId = project }, [scripts.Approved!.Blocks[0].Id], 5, "", new(AiBackend.OpenRouter, "test", "Test")) { CoverageOnly = true };
        var messages = ShotPlanner.BuildMessages(request);
        Assert.Contains("Do not assign assets", messages[0].Text); Assert.DoesNotContain("\"assets\"", messages[1].Text);
        Assert.Contains("looks", ShotPlanner.BuildMessages(request with { CoverageOnly = false })[0].Text);
        var s = Ready(); s.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Reference" });
        var coverage = ProductionPolicy.CoverageCopy(s); Assert.Empty(coverage.Images); Assert.Empty(coverage.Voices); Assert.Equal(s.Description, coverage.Description);
    }

    [Fact]
    public async Task ProductionSurvivesShotRemovalAndRecoveryWithoutReimport()
    {
        var f = Fixture(); var source = Ready(); var d = await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, new FileAiJobStore(Path.Combine(_root, "jobs"), _clock));
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        d = await f.Shots.LoadAsync(f.Project.Id, _ct);
        d = await f.Shots.DeleteShotsAsync(f.Project.Id, [source.Id], d.Revision, ct: _ct);
        Assert.Contains("removed", ProductionPolicy.Issue(c, await f.Assets.LoadAsync(f.Project.Id, _ct), d, f.Project));
        await f.Shots.RecoverAsync(f.Project.Id, d.Recovery[0].Id, d.Revision, _ct);
        Assert.Equal(c.Id, Assert.Single((await store.InitializeAsync(f.Project.Id, _ct)).Compositions).Id);
    }
    [Theory]
    [InlineData("speaker")]
    [InlineData("voice")]
    [InlineData("duration")]
    [InlineData("continuous")]
    public void ProductionChecksSpeakerVoiceDurationAndEndpoints(string failure)
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Mira", Text = "Hello." }, new() { Speaker = "Sam", Text = "Welcome." }];
        shot.Voices = [new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "Sam" }];
        shot.Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Use = ShotImageUse.FirstFrame, Role = "First frame" }];
        var prompt = H3Policy.Compile(shot); ProductionPolicy.ValidatePrompt(prompt, shot);
        prompt = failure switch {
            "speaker" => prompt.Replace("Mira (S1) says", "Sam (S2) says"),
            "voice" => prompt.Replace("reference for Sam (S2)", "reference for Mira (S1)"),
            "duration" => prompt.Replace(" seconds,", " minutes,"),
            "continuous" => prompt.Replace("One continuous camera take", "Two alternating camera takes"),
            _ => prompt.Replace("first frame", "last frame") };
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt, shot));
    }

    [Fact]
    public void ProductionAcceptsMultiplePicturesForOneCharacterWithoutForcingTheirUsage()
    {
        var shot = Ready(); var id = Guid.NewGuid(); shot.Characters = [new(id, "Mira")];
        shot.Dialogue = [new() { Speaker = "Mira", Text = "I am here." }];
        shot.Images = Enumerable.Range(0, 2).Select(_ => new ShotImageBinding { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), RepresentsId = id, InferUsage = true, Role = "Let AI decide" }).ToList();
        ProductionPolicy.ValidatePrompt(H3Policy.Compile(shot), shot);
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot with { Images = [shot.Images[0] with { Use = ShotImageUse.FirstFrame }] }));
    }

    [Theory]
    [InlineData("6.583333333333333 seconds", true)]
    [InlineData("6.583 seconds", true)]
    [InlineData("6.583-second", true)]
    [InlineData("6.584 seconds", true)]
    // A shot may state the authored Requested seconds; H3 takes its length from the frame count.
    [InlineData("6 seconds", true)]
    [InlineData("six seconds", true)]
    [InlineData("6.5 seconds", true)]
    [InlineData("16.583 seconds", false)]
    [InlineData("6.6 seconds", false)]
    [InlineData("6.583 minutes", false)]
    public void ProductionAcceptsEquivalentDurationPrecisionWithoutChangingTiming(string duration, bool valid)
    {
        var shot = Ready() with { Duration = 6 };
        var prompt = H3Policy.Compile(shot).Replace("6.583 seconds", duration);
        if (!valid) { Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt, shot)); return; }
        // The exact frame-derived form passes silently; any equivalent wording is reported so the
        // author can tighten it without losing an otherwise usable response.
        var note = ProductionPolicy.ValidatePrompt(prompt, shot);
        if (duration.StartsWith("6.583", StringComparison.Ordinal)) Assert.Null(note);
        else Assert.Contains("6.583 seconds", note);
    }

    [Theory]
    [InlineData("eight seconds", true)]
    [InlineData("eight-second", true)]
    [InlineData("EIGHT SECONDS", true)]
    [InlineData("8 seconds", true)]
    [InlineData("and eight seconds", true)]
    [InlineData("eight minutes", false)]
    [InlineData("seven seconds", false)]
    [InlineData("eighteen seconds", false)]
    [InlineData("twenty eight seconds", false)]
    [InlineData("twenty-eight seconds", false)]
    [InlineData("one hundred and eight seconds", false)]
    [InlineData("minus eight seconds", false)]
    [InlineData("eight point eight seconds", false)]
    public void ProductionRecognizesEquivalentNumberWordsWithoutMatchingPartOfAnotherDuration(string duration, bool valid)
    {
        var shot = Ready() with { Duration = 8 };
        var prompt = H3Policy.Compile(shot).Replace("8 seconds", duration);
        if (valid)
        {
            var raw = JsonSerializer.Serialize(new PromptCompositionResult(prompt, "Preserve the chosen view."), AtomicJsonFile.Options);
            var parsed = PromptComposer.Parse(raw, SoundRequest(shot));
            Assert.Equal(prompt, parsed.Prompt);
        }
        else Assert.Contains("8 seconds", Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt, shot)).Message);
    }

    [Fact]
    public void CompositionReadsTheLatestCompleteFencedAnswerAroundReasoning()
    {
        var shot = Ready() with { Duration = 8 };
        var prompt = H3Policy.Compile(shot);
        string Fenced(string usage) => "```json\n" + JsonSerializer.Serialize(new PromptCompositionResult(prompt, usage), AtomicJsonFile.Options) + "\n```";
        // Reasoning aloud, a draft, the final answer, then more checking cut off by the reply limit.
        var raw = "I need to analyze the shot carefully.\n\n" + Fenced("Draft usage.") + "\n\nWait, let me fix the usage.\n\n" + Fenced("Final usage.") +
            "\n\nWait, I need to double-check the six sections. Also checking: \"Use <Picture N> for";

        var parsed = PromptComposer.Parse(raw, SoundRequest(shot));

        Assert.Equal(prompt, parsed.Prompt);
        Assert.Equal("Final usage.", parsed.ReferenceUsage);
        Assert.Contains("not a complete composition", Assert.Throws<WorkspaceStoreException>(() =>
            PromptComposer.Parse("I need to analyze the shot.\n\n```json\n{\"prompt\": \"unfinished", SoundRequest(shot))).Message);
    }

    [Fact]
    public void NumberWordsDoNotReplaceFrameDerivedDurationOrContinuousTakeRequirements()
    {
        var rounded = Ready() with { Duration = 6 };
        // An ordinary shot accepts the authored Requested seconds...
        var shotPrompt = H3Policy.Compile(rounded).Replace("6.583 seconds", "six seconds");
        Assert.Contains("6.583 seconds", ProductionPolicy.ValidatePrompt(shotPrompt, rounded));
        // ...but a reel derives its cut timestamps from the generated grid and must state it exactly.
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(shotPrompt, rounded, allowCuts: true));
        var shot = Ready() with { Duration = 8 };
        var cuts = H3Policy.Compile(shot).Replace("8 seconds", "eight seconds").Replace("One continuous camera take", "Two alternating camera takes");
        Assert.Contains("one continuous take", Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(cuts, shot)).Message);
    }

    [Fact]
    public async Task ProductionReviewDetectsChangedCropsGuidanceAndIndependentSettings()
    {
        var f = Fixture(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(80, 40));
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, png, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var source = Ready(); source.Images = [ReferenceSetups.Bind(library.Assets[0], library.Assets[0].Images[0], source)];
        var doc = await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Shot.Images = source.Images; c.Prompt = H3Policy.Compile(source); c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        Assert.NotNull(c.Accepted!.Images); var accepted = c.Accepted;
        c.Shot.Images[0].Crop = new() { Width = .5, Height = 1 };
        c.Seed = 81; c.TakeCount = 3;
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        Assert.Null(ProductionPolicy.Issue(c, library, doc, f.Project));
        Assert.Equal(accepted.Prompt, c.Prompt);
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        Assert.NotEqual(accepted.Images![0].Sha256, c.Accepted!.Images![0].Sha256);
        library.Assets[0] = library.Assets[0] with { PreservationGuidance = "Keep the new window frame." };
        Assert.Null(ProductionPolicy.Issue(c, library, doc, f.Project));
        var reopened = (await new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs).LoadAsync(f.Project.Id, _ct)).Compositions[0];
        Assert.Equal(81, reopened.Seed); Assert.Equal(3, reopened.TakeCount); Assert.Equal(2, reopened.History.Count);
        Assert.Equal(c.AcceptedRevisionId, reopened.AcceptedRevisionId);
    }

    [Fact]
    public async Task ProductionActiveRequestsProtectSourcesAndCompositionArchiving()
    {
        var f = Fixture(); var source = Ready(); var doc = await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        await jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
            new(f.Project.Id, ShotId: source.Id, CompositionId: c.Id), "Project", "Compose", Guid.NewGuid(), new { test = true }), _ct);
        c.Archived = true;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(f.Project.Id, c, c.Version, _ct));
        doc = await f.Shots.LoadAsync(f.Project.Id, _ct);
        var guarded = new FileShotStore(f.Files, _clock, jobs: jobs);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => guarded.DeleteShotsAsync(f.Project.Id, [source.Id], doc.Revision, ct: _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => guarded.SaveAsync(f.Project.Id, [], doc.Revision, ct: _ct));
        Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots);
    }

}
