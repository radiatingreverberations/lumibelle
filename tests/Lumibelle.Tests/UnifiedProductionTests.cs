using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("en", "English")]
    [InlineData("English", "en")]
    [InlineData("sv", "Swedish")]
    [InlineData("sv", "svenska")]
    [InlineData("fr", "French")]
    public void CompositionAcceptsEquivalentLanguageLabelsWithoutChangingPrompt(string stored, string returned)
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Riley", Language = stored, Text = "Support is a mindset, not a location." }];
        var prompt = H3Policy.Compile(shot).Replace($"<d>[{stored}]", $"<d>[{returned}]");
        ProductionPolicy.ValidatePrompt(prompt, shot);
        Assert.Contains($"<d>[{returned}]", prompt);
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace("not a location", "a location"), shot));
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace($"<d>[{returned}]", "<d>[Japanese]"), shot));
    }

    [Theory]
    [InlineData("You know your kit doesn't have any charms.", "You know your kit doesn’t have any charms.")]
    [InlineData("You know your kit doesn’t have any charms.", "You know your kit doesn't have any charms.")]
    [InlineData("He said \"stay\"...", "He said “stay”…")]
    [InlineData("A long dash - here.", "A long dash — here.")]
    public void CompositionAcceptsEquivalentDialoguePunctuationWithoutChangingPrompt(string stored, string returned)
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Riley", Language = "en", Text = stored }];
        var prompt = H3Policy.Compile(shot).Replace(stored, returned);
        ProductionPolicy.ValidatePrompt(prompt, shot);
        Assert.Contains(returned, prompt);
        // Wording and order stay strict even when the printer's punctuation is equivalent.
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace(returned, returned + " Really."), shot));
    }

    [Fact]
    public void UnifiedAudioMappingsAllowFollowingSubjectReferences()
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Riley", Text = "Support is a mindset, not a location." }];
        shot.Voices = [new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "Riley" }];
        var prompt = H3Policy.Compile(shot);
        var lines = prompt.Split('\n');
        var index = Array.FindIndex(lines, l => l.StartsWith("<Audio 1>"));
        lines[index] += " Linked to <Subject 1>; use only for voice identity.";
        // Subject 1 is a real definition, not an invented mapping target.
        prompt = string.Join('\n', lines).Replace("subject_definitions:", "subject_definitions:\n<Subject 1> — Riley.");
        ProductionPolicy.ValidatePrompt(prompt, shot);
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace("Riley (S1)", "Riley (S2)"), shot));
    }

    [Theory]
    [InlineData("RILEY (S1) uses <Audio 1> as her sole voice-identity reference.")]
    [InlineData("RILEY (S1) maps to <Audio 1> for voice identity only, never its sample words.")]
    [InlineData("<Subject 1> (S1) uses <Audio 1> as the voice reference.")]
    [InlineData("<Audio 1> supplies RILEY (S1)'s voice identity.")]
    public void AudioMappingsAcceptEitherOrderAndRecoverTheExactSavedResponse(string mapping)
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Riley", Text = "Hello." }];
        shot.Voices = [new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = "Riley" }];
        var lines = H3Policy.Compile(shot).Split('\n');
        var index = Array.FindIndex(lines, l => l.StartsWith("<Audio 1>"));
        lines[index] = "<Subject 1> — Riley, the person shown in this shot. " + mapping;
        var prompt = string.Join('\n', lines);
        ProductionPolicy.ValidatePrompt(prompt, shot);
        var recovery = PromptComposer.RecoverResponse(SoundResponse(prompt), SoundRequest(shot));
        Assert.Equal(prompt, recovery.Result.Prompt); Assert.Empty(recovery.Sections); Assert.Empty(recovery.AddedText);
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace(mapping, mapping.Replace("(S1)", "(S2)")), shot));
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace(mapping, "Mira (S1) uses <Audio 1>."), shot));
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt.Replace(mapping, "Riley (S1) has a voice.\n<Audio 1> is an unmapped recording."), shot));
    }

    [Fact]
    public async Task UnifiedRealVideoPreparationDoesNotRequireLookLinks()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        using var png = new MemoryStream(AssetStoreTests.Png(80, 40));
        library = await f.Assets.AddImageAsync(f.Project.Id, owner.Id, png, new("room.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var shot = Ready(); shot.Images = [new() { AssetId = owner.Id, MediaId = library.Assets[0].Images[0].Id, LookId = Guid.NewGuid(), InferUsage = true, Name = "Room" }];
        var revision = new CompositionPromptRevision(Guid.NewGuid(), _clock.GetUtcNow(), H3Policy.Compile(shot), "", "", "");
        var snapshot = Snapshot(f.Project.Id, shot) with { Production = new(Guid.NewGuid(), 1, "Setup", revision, []) };
        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, null!);
        await generator.ValidateInputsAsync(snapshot, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.ValidateInputsAsync(snapshot with { Production = null }, _ct));
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("blank")]
    [InlineData("revision")]
    [InlineData("manual-edit")]
    [InlineData("direction")]
    [InlineData("source")]
    [InlineData("cancelled")]
    [InlineData("replaced")]
    [InlineData("clear")]
    [InlineData("video")]
    [InlineData("changed-video")]
    public async Task UnifiedCompositionAppliesOnlyAnUnchangedCapturedTarget(string scenario)
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var videoStore = new FileReferenceVideoStore(f.Files, f.Shots, new ReferenceMediaFake());
        FileProductionStore Open() => new(f.Files, f.Shots, f.Assets, projects, _clock, jobs, videoStore);
        var store = Open(); var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        await store.SaveAsync(f.Project.Id, new ProductionComposition { ShotId = source.Id, Name = "Another generation setup" }, 0, _ct);
        if (scenario is "video" or "changed-video")
        {
            await using var bytes = new MemoryStream([1, 2, 3, 4]);
            c.Shot.Videos = [new() { Media = await videoStore.ImportAsync(f.Project.Id, bytes, "reference.mp4", new(), _ct) }];
            c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        }
        if (scenario == "revision") { c.Prompt = H3Policy.Compile(source); c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0]; }
        if (scenario == "blank") { c.Prompt = " \n\t"; c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0]; }
        var model = new TextModelReference(AiBackend.OpenRouter, "test/model", "Test");
        var request = new PromptCompositionRequest(f.Project.Id, c.Id, c.Version,
            ProductionPolicy.ContextFingerprint(c, await f.Assets.LoadAsync(f.Project.Id, _ct), await f.Shots.LoadAsync(f.Project.Id, _ct), f.Project),
            c.SourceFingerprint, c.Shot, "Scene", [], [], [], [], c.DirectingNotes, c.Prompt, "", model);
        var snapshot = new AiTextJobRequest(2, AiJobKind.PromptComposition, model, false, new(), ProductionPolicy.Profile, .7f, 42,
            JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), PromptComposer.BuildMessages(request, []).Select(AiTextMessage.Capture).ToArray());
        var id = Guid.NewGuid();
        await jobs.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.PromptComposition, model.Backend, new(f.Project.Id, ShotId: source.Id, CompositionId: c.Id), "Project", "Compose", Guid.NewGuid(), snapshot), _ct);
        c.ReviewJobId = id; c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var proposed = new PromptCompositionResult((c.Shot.Videos.Count > 0 ? ReferencePrompt(c.Shot) : H3Policy.Compile(source)) + "\nA quiet room tone.", "No images supplied.");
        await jobs.WriteArtifactAsync(id, AiJobArtifact.Result, new AiTextJobResult("saved output", true, Value: JsonSerializer.SerializeToElement(proposed, AtomicJsonFile.Options)), _ct);
        await jobs.UpdateAsync(id, j => j with { State = AiJobState.Completed }, _ct);
        if (scenario == "changed-video")
            await File.WriteAllBytesAsync(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", c.Shot.Videos[0].Media.Id.ToString(), "video.mp4"), [4, 3, 2, 1], _ct);
        if (scenario == "cancelled") await jobs.UpdateAsync(id, j => j with { State = AiJobState.Cancelled, CancelRequested = true }, _ct);
        if (scenario == "source") { var d = await f.Shots.LoadAsync(f.Project.Id, _ct); d.Shots[0].Description += " Another action."; await f.Shots.SaveAsync(f.Project.Id, d.Shots, d.Revision, ct: _ct); }
        if (scenario is "manual-edit" or "direction" or "replaced" or "clear")
        {
            if (scenario == "manual-edit") c.Prompt = "My unsaved-quality draft is still worth keeping.";
            if (scenario == "direction") c.DirectingNotes = "Another idea";
            if (scenario == "replaced") c.ReviewJobId = Guid.NewGuid();
            if (scenario == "clear") c.ReviewJobId = null;
            c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        }
        // A new service instance represents reopening/restart; delivery is idempotent.
        var applied = (await Open().ApplyResultAsync(f.Project.Id, id, true, _ct)).Compositions[0];
        Assert.Equal(scenario is "initial" or "blank" or "video" ? proposed.Prompt : c.Prompt, applied.Prompt);
        Assert.Equal(scenario is "initial" or "blank" or "video" ? id : (Guid?)null, applied.AppliedJobId);
        var duplicate = (await Open().ApplyResultAsync(f.Project.Id, id, true, _ct)).Compositions[0];
        Assert.Equal(applied.Version, duplicate.Version);
        Assert.All((await Open().LoadAsync(f.Project.Id, _ct)).Compositions, setup => {
            Assert.Equal(applied.Prompt, setup.Prompt);
            Assert.Equal(applied.AppliedJobId, setup.AppliedJobId);
        });
        if (scenario == "changed-video") await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ApplyResultAsync(f.Project.Id, id, false, _ct));
        if (scenario == "revision")
        {
            applied = (await store.ApplyResultAsync(f.Project.Id, id, false, _ct)).Compositions[0];
            Assert.Equal(proposed.Prompt, applied.Prompt);
            Assert.Equal(id, applied.AppliedJobId);
        }
        Assert.NotNull(await jobs.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, _ct));
    }

    [Fact]
    public async Task ProductionInitializationPreservesAssetsCoverageScriptsAndQueueAndIsIdempotent()
    {
        var f = Fixture(); var source = Ready(); source.Characters = [new(Guid.NewGuid(), "Observer")];
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        var script = "{\"supplied\":\"An unchanged script. Å\"}";
        await File.WriteAllTextAsync(Path.Combine(dir, "script-test.json"), script, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        await jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter, new(f.Project.Id, ShotId: source.Id, CompositionId: Guid.NewGuid()), "Project", "Old setup", Guid.NewGuid(), new { old = true }), _ct);
        var old = (await jobs.ReadAsync(_ct)).Jobs[0]; await jobs.UpdateAsync(old.Id, j => j with { State = AiJobState.Completed }, _ct);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var first = await store.InitializeAsync(f.Project.Id, _ct); var second = await store.InitializeAsync(f.Project.Id, _ct);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(script, await File.ReadAllTextAsync(Path.Combine(dir, "script-test.json"), _ct));
        Assert.Equal(JsonSerializer.Serialize(library), JsonSerializer.Serialize(await f.Assets.LoadAsync(f.Project.Id, _ct)));
        Assert.Equal(ProductionPolicy.SourceFingerprint(source), ProductionPolicy.SourceFingerprint((await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots[0]));
        Assert.Equal(old.Id, Assert.Single((await jobs.ReadAsync(_ct)).Jobs).Id);
        Assert.Empty(first.Compositions[0].Prompt); Assert.Empty(first.Compositions[0].Inputs.Images);
    }
}
