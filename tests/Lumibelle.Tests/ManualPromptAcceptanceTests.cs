using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("duration", true)]
    [InlineData("duration", false)]
    [InlineData("direction", true)]
    [InlineData("direction", false)]
    public async Task PromptReviewDoesNotRequireGenerationReadiness(string missing, bool linked)
    {
        var f = Fixture(); var (source, scripts) = ApprovedShot(f.Project.Id);
        if (missing == "duration") source.Duration = null;
        if (missing == "direction") source.Description = "";
        if (!linked) { source.SceneId = null; source.ApprovedScriptId = null; source.SceneTitle = ""; source.SourceBlockIds = []; source.SourceExcerpt = ""; }
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = "A quiet morning. Keep the camera still.";
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var shots = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Null(ProductionPolicy.PromptReviewIssue(c, library, shots, f.Project));
        Assert.NotNull(ProductionPolicy.Issue(c, library, shots, f.Project));

        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        Assert.Equal(c.Prompt, c.Accepted!.Prompt);
        Assert.NotNull(c.Accepted.ReferenceFingerprint);
        Assert.Equal(ProductionPolicy.SourceFingerprint(source), c.SourceFingerprint);
        var unchanged = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots);
        Assert.Equal(source.Duration, unchanged.Duration);
        Assert.Equal(source.Description, unchanged.Description);
        Assert.Equal(source.SceneId, unchanged.SceneId);
        var capture = new AiVideoJobCapture(f.Shots, scripts, f.Assets, new FakeAiSettingsStore(),
            new MockVideoGenerator(f.Assets, f.Shots), projects, new FakeProjectAiPreferencesStore(), store);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => capture.CaptureCompositionAsync(
            Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, c.Id, c.Version, 1, 123, _ct));
        Assert.Empty((await jobs.ReadAsync(_ct)).Jobs);
    }

    [Theory]
    [InlineData("freeform")]
    [InlineData("dialogue")]
    [InlineData("reference")]
    [InlineData("sections")]
    public async Task PromptDeviationsCanBeSavedAcceptedAndCapturedWithoutRewriting(string kind)
    {
        var f = Fixture(); var (source, scripts) = ApprovedShot(f.Project.Id);
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        var full = H3Policy.Compile(source);
        var prompt = kind switch {
            "dialogue" => full + "\nRiley says <d>Some extra words without a language label</d>",
            "reference" => full + "\nUse <Picture 42> for the room.",
            "sections" => full.Replace("summary:", "my description:"),
            _ => "A quiet morning in the office. She enters, smiles, and says hello.\nKeep the camera still." };
        Assert.NotNull(ProductionPolicy.PromptWarning(prompt, source));
        var parsed = PromptComposer.Parse(SoundResponse(prompt), SoundRequest(source));
        Assert.Equal(prompt, parsed.Prompt); Assert.NotEmpty(parsed.Notes);
        var recovered = PromptComposer.RecoverResponse(SoundResponse(prompt), SoundRequest(source));
        Assert.Equal(prompt, recovered.Result.Prompt); Assert.Empty(recovered.AddedText);

        var failed = await jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition,
            AiBackend.OpenRouter, new(f.Project.Id, ShotId: source.Id, CompositionId: c.Id), "Project", "Compose",
            Guid.NewGuid(), new { prompt }), _ct);
        var artifact = new AiTextJobResult(SoundResponse(prompt), true, "stop", Error: "Old format check failed.");
        await jobs.WriteArtifactAsync(failed.Id, AiJobArtifact.Result, artifact, _ct);
        await jobs.UpdateAsync(failed.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain }, _ct);
        c.Prompt = prompt; c.ReviewJobId = failed.Id;
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        c = (await store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct)).Compositions[0];
        Assert.Equal(prompt, c.Accepted!.Prompt); Assert.Null(c.Accepted.JobId); Assert.Null(c.ReviewJobId);
        Assert.Equal(prompt, (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Prompt);
        Assert.Equal(artifact, await jobs.ReadArtifactAsync<AiTextJobResult>(failed.Id, AiJobArtifact.Result, _ct));
        Assert.Equal(AiJobState.NeedsAttention, Assert.Single((await jobs.ReadAsync(_ct)).Jobs).State);

        var capture = new AiVideoJobCapture(f.Shots, scripts, f.Assets, new FakeAiSettingsStore(),
            new MockVideoGenerator(f.Assets, f.Shots), projects, new FakeProjectAiPreferencesStore(), store);
        var submission = await capture.CaptureCompositionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, c.Id, c.Version, 1, 123, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(prompt, request.Snapshot.Prompt); AiVideoJobPolicy.Validate(request);
    }

    [Fact]
    public async Task FreeformDraftsDoNotBypassUnavailableMediaOrActiveRequests()
    {
        var f = Fixture(); var source = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        c.Prompt = "A quiet morning.";
        c.Shot.Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Missing" }];
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        Assert.Contains("reference", ProductionPolicy.Issue(c, await f.Assets.LoadAsync(f.Project.Id, _ct), await f.Shots.LoadAsync(f.Project.Id, _ct), f.Project));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct));
        c.Shot.Images.Clear();
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        await jobs.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
            new(f.Project.Id, ShotId: source.Id, CompositionId: c.Id), "Project", "Compose", Guid.NewGuid(), new { test = true }), _ct);
        Assert.Contains("active", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AcceptAsync(f.Project.Id, c.Id, c.Version, ct: _ct))).Message);
        Assert.Null((await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Accepted);
    }
}
