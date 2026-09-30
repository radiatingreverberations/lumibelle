using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static PromptCompositionRequest SoundRequest(Shot shot) => new(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot,
        "Scene", [], [], [], [], "", "", "", new(AiBackend.OpenRouter, "mock/script", "Mock model"));
    private static string SoundResponse(string prompt) => JsonSerializer.Serialize(new PromptCompositionResult(prompt, "Keep the selected visual references."), AtomicJsonFile.Options);

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("\n")]
    public void BlankNeedsInputDoesNotStopACompleteComposition(string blank)
    {
        var shot = Ready(); shot.Atmosphere = "Typing."; shot.Music = "None.";
        var prompt = H3Policy.Compile(shot);
        var raw = JsonSerializer.Serialize(new { prompt, referenceUsage = "Uses <Picture 1> for the room.", needsInput = blank }, AtomicJsonFile.Options);
        // A stray blank field is not a clarification request; the complete prompt stays usable.
        var parsed = PromptComposer.Parse(raw, SoundRequest(shot));
        Assert.Equal(prompt, parsed.Prompt);
        Assert.Equal("Uses <Picture 1> for the room.", parsed.ReferenceUsage);
    }

    [Fact]
    public void NonBlankNeedsInputStillAsksForAClarification()
    {
        var shot = Ready(); shot.Atmosphere = "Typing."; shot.Music = "None.";
        var raw = JsonSerializer.Serialize(new { prompt = H3Policy.Compile(shot), referenceUsage = "Uses <Picture 1>.", needsInput = "Which image shows the armor?" }, AtomicJsonFile.Options);
        var error = Assert.Throws<WorkspaceStoreException>(() => PromptComposer.Parse(raw, SoundRequest(shot)));
        Assert.Equal("Composition needs your input: Which image shows the armor?", error.Message);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void MissingFinalSoundSectionsCanBeRestoredWithoutRewritingTheVisualPrompt(int missing)
    {
        var shot = Ready(); shot.Atmosphere = "Awkward silence punctuated by typing and a regrettable sip."; shot.Music = "None.";
        var request = SoundRequest(shot); var full = H3Policy.Compile(shot);
        var partial = full[..full.IndexOf(missing == 2 ? "overall_soundscape:" : "non_diegetic_music:", StringComparison.Ordinal)].TrimEnd();
        var raw = SoundResponse(partial);
        var parsed = PromptComposer.Parse(raw, request);
        Assert.Equal(partial, parsed.Prompt);
        Assert.Contains("Missing H3 sections:", Assert.Single(parsed.Notes));
        var recovery = PromptComposer.RecoverSoundSections(raw, request);
        Assert.Equal(missing, recovery.Sections.Count); Assert.StartsWith(partial + "\n\n", recovery.Result.Prompt);
        Assert.Contains("non_diegetic_music:\nNone.", recovery.AddedText);
        if (missing == 2) Assert.Contains(shot.Atmosphere, recovery.AddedText);
        ProductionPolicy.ValidatePrompt(recovery.Result.Prompt, shot);
        Assert.Equal("Keep the selected visual references.", recovery.Result.ReferenceUsage);
        var messages = PromptComposer.BuildMessages(request, []);
        Assert.Contains("even when there is no dialogue", messages[0].Text);
        Assert.Contains("shot.Atmosphere and shot.Music", messages[0].Text);
        Assert.DoesNotContain("left out to keep this request small", messages[0].Text);
        // Reduced script context tells the composer not to invent the scene or neighbouring shots it did not receive.
        var reduced = PromptComposer.BuildMessages(request with { SceneContext = "", NearbyShots = [], ReducedScriptContext = true }, []);
        Assert.Contains("left out to keep this request small", reduced[0].Text);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("order")] [InlineData("middle")] [InlineData("dialogue")] [InlineData("empty sound")] [InlineData("already complete")]
    public void SoundRecoveryDoesNotMaskOtherFailuresOrInventMissingDirections(string issue)
    {
        var shot = Ready(); shot.Atmosphere = "Typing."; shot.Music = "None.";
        var full = H3Policy.Compile(shot);
        var partial = full[..full.IndexOf("overall_soundscape:", StringComparison.Ordinal)].TrimEnd();
        partial = issue switch {
            "duplicate" => "summary:\nDuplicate\n" + partial,
            "order" => partial.Replace("summary:", "TEMP:").Replace("retention_analysis:", "summary:").Replace("TEMP:", "retention_analysis:"),
            "middle" => partial.Replace("summary:", "overview:"),
            "dialogue" => partial + "\nMira (S1) says <d>[English] New dialogue.</d>",
            "already complete" => full,
            _ => partial };
        if (issue == "empty sound") shot.Music = "";
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.RecoverSoundSections(SoundResponse(partial), SoundRequest(shot)));
    }

    [Theory]
    [InlineData("none", false)] [InlineData("direction", false)] [InlineData("sound", false)] [InlineData("prompt", false)] [InlineData("version", false)]
    [InlineData("interrupted", false)] [InlineData("cancelled", false)]
    [InlineData("none", true)] [InlineData("direction", true)] [InlineData("sound", true)] [InlineData("prompt", true)] [InlineData("version", true)]
    [InlineData("interrupted", true)] [InlineData("cancelled", true)] [InlineData("invalid duration", true)]
    public async Task ResponseRecoveryPublishesOnlyAnUnacceptedDraftAndGuardsNewerWork(string change, bool numberWords)
    {
        var f = Fixture(); var shot = Ready() with { Duration = 8 }; shot.Atmosphere = "Typing and a sip."; shot.Music = "None.";
        var shots = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var request = SoundRequest(ShotVideoDefaults.Capture(c.Shot, f.Project)) with { ProjectId = f.Project.Id, CompositionId = c.Id, CompositionVersion = c.Version,
            ContextFingerprint = ProductionPolicy.ContextFingerprint(c, library, shots, f.Project) };
        var snapshot = new AiTextJobRequest(2, AiJobKind.PromptComposition, request.Model, false, new(), ProductionPolicy.Profile, .7f, 1,
            JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options), [new("system", [new(Text: "Saved instructions")])]);
        var job = AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.PromptComposition, AiBackend.OpenRouter,
            new(f.Project.Id, ShotId: shot.Id, CompositionId: c.Id), "Project", "Compose", Guid.NewGuid(), snapshot);
        await jobs.EnqueueAsync(job, _ct);
        var full = H3Policy.Compile(request.Shot);
        var response = numberWords ? full.Replace("8 seconds", change == "invalid duration" ? "seven seconds" : "eight seconds")
            : full[..full.IndexOf("overall_soundscape:", StringComparison.Ordinal)].TrimEnd();
        var result = new AiTextJobResult(SoundResponse(response), true, change == "interrupted" ? "length" : "stop", Error: numberWords ? "State one continuous take lasting 8 seconds, preserving the shot duration." : "Missing sections.");
        await jobs.WriteArtifactAsync(job.Id, AiJobArtifact.Result, result, _ct);
        await jobs.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain, CancelRequested = change == "cancelled" }, _ct);
        c.ReviewJobId = job.Id; c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var expectedVersion = c.Version;
        if (change == "direction") c.DirectingNotes = "A different angle.";
        if (change == "prompt") c.Prompt = "My newer draft.";
        if (change is "direction" or "prompt" or "version") c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        if (change == "sound") { var currentShots = await f.Shots.LoadAsync(f.Project.Id, _ct); currentShots.Shots[0].Music = "Piano."; await f.Shots.SaveAsync(f.Project.Id, currentShots.Shots, currentShots.Revision, ct: _ct); }
        if (change is not ("none" or "invalid duration")) await Assert.ThrowsAnyAsync<WorkspaceStoreException>(() => store.RecoverResponseAsync(f.Project.Id, job.Id, change == "version" ? expectedVersion : c.Version, _ct));
        else
        {
            var recovered = (await store.RecoverResponseAsync(f.Project.Id, job.Id, c.Version, _ct)).Compositions[0];
            var parsed = PromptComposer.RecoverResponse(result.Raw, request);
            Assert.Equal(parsed.Result.Prompt, recovered.Prompt);
            Assert.Equal(parsed.Result.ReferenceUsage, recovered.ReferenceUsage);
            Assert.Equal(response, recovered.Prompt); Assert.Empty(parsed.Sections); Assert.Empty(parsed.AddedText);
            Assert.Null(recovered.Accepted); Assert.Null(recovered.ReviewJobId); Assert.Empty(recovered.History);
            await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.RecoverResponseAsync(f.Project.Id, job.Id, c.Version, _ct));
            Assert.Equal(recovered.Prompt, (await store.LoadAsync(f.Project.Id, _ct)).Compositions[0].Prompt);
        }
        Assert.Equal(result, await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _ct));
        Assert.Single((await jobs.ReadAsync(_ct)).Jobs);
        Assert.Equal(AiJobState.NeedsAttention, (await jobs.ReadAsync(_ct)).Jobs[0].State);
    }
}
