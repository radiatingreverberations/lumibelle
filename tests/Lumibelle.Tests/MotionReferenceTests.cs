using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("standard")] [InlineData(H3AcceleratedCheckpoints.Compact)]
    [InlineData(H3SmdRecipe.Key)] [InlineData(H3AcceleratedCheckpoints.Fast)] [InlineData(H3PtdRecipe.Key)]
    public async Task NewRecipesCaptureLocallyAndSubmitOnlyUpstreamNodes(string key)
    {
        var snapshot = H3PtdRecipe.Effective(H3SmdRecipe.Effective(H3AcceleratedCheckpoints.Effective(PresetSnapshot(key))));
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []);
        // Null dependencies deliberately fail if capture tries HTTP or filesystem access.
        var generator = new ComfyH3Video(null!, null!, null!, null!, null!, null!);
        var recipe = await generator.CaptureRecipeAsync(request, _ct);
        Assert.False(recipe.ProbeAvailable);
        Assert.All(recipe.Models, m => Assert.Null(m.Sha256));
        await generator.ValidateRecipeAsync(request with { Recipe = recipe }, _ct);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", [], recipe: recipe)).GetProperty("prompt");
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString()!.StartsWith("Lumibelle", StringComparison.Ordinal));
    }

    [Fact]
    public void MotionInstructionsReplaceWithoutAccumulatingAndKeepStructuredSections()
    {
        var shot = Ready() with { Duration = 5 }; var original = H3Policy.Compile(shot);
        var first = MotionReferencePolicy.Render(original, "Walk left, turn, then wave.", 2);
        var revised = MotionReferencePolicy.Render(original, "Hold camera still.", 2);
        Assert.Contains("Motion reference <Video 2>: Walk left, turn, then wave.", first);
        Assert.DoesNotContain("Walk left", revised);
        Assert.Equal(1, revised.Split("Motion reference <Video 2>:").Length - 1);
        Assert.Equal(original, MotionReferencePolicy.Render(original, "", 0));
        ProductionPolicy.ValidateGenerationPrompt(first); ProductionPolicy.ValidateGenerationPrompt(revised);
    }

    [Fact]
    public void MotionReferenceGraphExcludesDraftAudioAndPreservesOtherVoices()
    {
        var shot = Ready() with { Duration = 5 };
        shot.Dialogue = [new() { Speaker = "Riley", Text = "The package is here." }];
        shot.Videos = [Clip(true), Clip(false)];
        shot.Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 2 }];
        var basis = ReferenceSnapshot(shot);
        var origin = new MotionReferenceSource(Guid.NewGuid(), shot.Videos[1].Id, shot.Videos[1].Media, 124, 24, basis.Prompt, MotionReferencePolicy.DefaultInstructions);
        var snapshot = basis with { Production = null, Profile = MotionReferencePolicy.Profile, MotionReference = origin,
            Prompt = MotionReferencePolicy.Render(basis.Prompt, origin.Instructions, 2) };
        MotionReferencePolicy.Validate(snapshot);
        var inputs = ReferenceVideos.InputOrder(shot).Select((v, i) => new PreparedVideoInput($"input-{i}{AiVideoJobPolicy.Extension(v.Kind)}", v.Kind is VideoInputKind.Audio or VideoInputKind.VideoSoundtrack) { Kind = v.Kind, VideoIndex = v.VideoIndex }).ToArray();
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "attempt", inputs)).GetProperty("prompt");
        var conditioning = graph.GetProperty("5").GetProperty("inputs");
        Assert.True(conditioning.TryGetProperty("ref_videos.ref_video_1", out _));
        Assert.False(conditioning.TryGetProperty("ref_video_audios.ref_video_audio_1", out _));
        Assert.True(conditioning.TryGetProperty("ref_video_audios.ref_video_audio_0", out _));
        Assert.True(conditioning.TryGetProperty("ref_audios.ref_audio_0", out _));
        Assert.Contains("The package is here.", conditioning.GetProperty("prompt").GetString());
        Assert.Equal("VAEDecodeAudio", graph.GetProperty("12").GetProperty("class_type").GetString());
        Assert.Equal("5", graph.GetProperty("10").GetProperty("inputs").GetProperty("latent_image")[0].GetString());
        snapshot.Shot.Videos[1].UseSoundtrack = true;
        Assert.Throws<WorkspaceStoreException>(() => MotionReferencePolicy.Validate(snapshot));
    }

    [Fact]
    public async Task MotionReferenceCaptureOwnsMediaAndRetriesAfterSourceDeletion()
    {
        var f = Fixture(); var shot = Ready() with { Duration = 5 };
        shot.Dialogue = [new() { Speaker = "Riley", Text = "Hello." }];
        shot.Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 2 }];
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot); var take = Assert.Single(doc.Takes);
        // Latent/archive storage size is unrelated to the imported MP4 size.
        take.Bytes = 1024L * 1024 * 1024; Assert.Null(MotionReferencePolicy.Issue(take));
        var media = new MotionMediaFake(); var generator = new MockVideoGenerator(f.Assets, f.Shots, media);
        var capture = new AiVideoJobCapture(f.Shots, ApprovedShot(f.Project.Id).Scripts, f.Assets, new FakeAiSettingsStore(), generator,
            new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, new FakeProjectAiPreferencesStore(), referenceVideos: media);
        var draft = await capture.PrepareMotionReferenceAsync(f.Project.Id, take.Id, _ct);
        Assert.Equal(take.Id, draft.Source.TakeId); Assert.False(Assert.Single(draft.Shot.Videos).UseSoundtrack);
        Assert.Equal("standard", draft.Shot.GenerationPreset); Assert.True(draft.Shot.NativeResolution);
        var id = Guid.NewGuid();
        var submission = await capture.CaptureMotionReferenceAsync(id, Guid.NewGuid(), f.Project.Id,
            new(draft, draft.Source.BasePrompt, "Follow action order, generate clean dialogue."), _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(2, request.Version); Assert.NotNull(request.Recipe);
        // Saved latents are only for the optional latent upscaling path, so they are off by default.
        Assert.False(request.Snapshot.CaptureRefinementData);
        Assert.Equal("standard", request.Snapshot.Preset!.Key); Assert.Single(submission.Batch!.Candidates);
        Assert.Single(request.Inputs, i => i.EffectiveKind == VideoInputKind.Video);
        Assert.DoesNotContain(request.Inputs, i => i.EffectiveKind == VideoInputKind.VideoSoundtrack);
        Assert.Single(request.Inputs, i => i.EffectiveKind == VideoInputKind.Audio);
        var before = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, before.Revision, _ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, await f.Shots.RunDirectoryAsync(f.Project.Id, id, _ct), _ct);
        Assert.Equal(draft.Source.BasePrompt, request.Snapshot.MotionReference!.BasePrompt);
        Assert.Contains("Follow action order", request.Snapshot.Prompt);
        Assert.DoesNotContain("Follow action order", draft.Source.BasePrompt);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => capture.CaptureMotionReferenceAsync(id, Guid.NewGuid(), f.Project.Id,
            new(draft, draft.Source.BasePrompt, "Duplicate"), _ct));
    }

    [Fact]
    public void HistoricalRecipeRemainsReadableButNewGraphsUseNativeSamplingLinks()
    {
        var shot = Ready() with { Duration = 5, SaveLosslessFrames = false };
        var snapshot = ReferenceSnapshot(shot) with { OutputPolicy = new(false), Preset = H3Presets.Capture(shot, new()),
            Performance = H3Performance.Capture(new()) };
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, []);
        var evidence = new H3RecipeEvidence(H3GenerationRecipes.Models(request).Select(m => m with { Sha256 = new('A', 64), Bytes = 10, Quantization = "test" }).ToArray(),
            new(new Dictionary<string, string?> { ["torch"] = "test" }, new Dictionary<string, string?>()));
        request = request with { Recipe = H3GenerationRecipes.Capture(request, evidence) };
        H3GenerationRecipes.Validate(request);
        var roundTrip = JsonSerializer.Deserialize<AiVideoJobRequest>(JsonSerializer.Serialize(request, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        H3GenerationRecipes.Validate(roundTrip);
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(request with { Recipe = request.Recipe! with {
            Models = evidence.Models.Select(m => m with { Sha256 = new('B', 64) }).ToArray() } }));
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.Validate(request with { Snapshot = snapshot with { Prompt = snapshot.Prompt + "Edited" } }));
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.CheckEvidence(request.Recipe!, evidence with {
            Models = evidence.Models.Select(m => m with { Sha256 = new('B', 64) }).ToArray() }));
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(snapshot, 42, "attempt", [], recipe: request.Recipe));
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "attempt", [], recipe: H3GenerationRecipes.Capture(request))).GetProperty("prompt");
        Assert.Equal("9", graph.GetProperty("10").GetProperty("inputs").GetProperty("sigmas")[0].GetString());
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString()!.StartsWith("Lumibelle"));
        Assert.Equal("10", graph.GetProperty("11").GetProperty("inputs").GetProperty("samples")[0].GetString());
    }

    [Fact]
    public void RecipeReceiptBindsAnExplicitRetryWithoutChangingItsCapturedProbe()
    {
        var request = new AiVideoJobRequest(2, Guid.NewGuid(), ReferenceSnapshot(Ready() with { Duration = 5 }), []);
        var evidence = new H3RecipeEvidence(H3GenerationRecipes.Models(request), new(new Dictionary<string, string?>(), new Dictionary<string, string?>()));
        var recipe = H3GenerationRecipes.Capture(request, evidence);
        var workflow = JsonSerializer.SerializeToElement(new { client_id = "retry-client", prompt = new Dictionary<string, object> {
            [H3GenerationRecipes.ProbeId] = new { inputs = new { attempt_id = "original-probe" } }
        } });
        var outputs = JsonSerializer.SerializeToElement(new Dictionary<string, object> {
            [H3GenerationRecipes.ReceiptId] = new { lumibelle_h3_receipt = new[] { new {
                recipeFingerprint = recipe.Fingerprint, seed = 42L, attemptId = "original-probe", status = "sampling-complete",
                sigmas = new[] { 1d, .5d, 0d }, evidence
            } } }
        }, AtomicJsonFile.Options);
        var receipt = H3GenerationRecipes.BindReceipt(outputs, recipe, 42, workflow, H3GenerationRecipes.ProbeId, "new-prompt", "retry-client");
        Assert.Equal("original-probe", receipt.AttemptId);
        Assert.Equal("new-prompt", receipt.ComfyPromptId); Assert.Equal("retry-client", receipt.ComfyClientId);
        Assert.Equal(new[] { 1d, .5d, 0d }, receipt.Sigmas);
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.ReadReceipt(outputs, recipe, 42, "another-probe"));
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.CheckEvidence(recipe, evidence with {
            Runtime = new(new Dictionary<string, string?> { ["adapter-loader"] = "incompatible" }, new Dictionary<string, string?>()) }));
        Assert.Throws<WorkspaceStoreException>(() => H3GenerationRecipes.ReadReceipt(JsonSerializer.SerializeToElement(new { }), recipe, 42, "original-probe"));
    }

    private sealed class MotionMediaFake : IReferenceVideoStore
    {
        public int Width = 32, Height = 32;
        public Task<ReferenceVideoMedia> CopyTakeAsync(Guid project, Guid take, H3Settings settings, CancellationToken ct = default)
            => Task.FromResult(new ReferenceVideoMedia(Guid.NewGuid(), new('A', 64), 3, Width, Height, 124, 24, 124 / 24d, true));
        public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream s, string n, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => Task.FromResult<AssetMedia?>(null);
        public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => Task.CompletedTask;
        public async Task PrepareAsync(Guid project, Shot shot, string directory, List<PreparedVideoInput> inputs, H3Settings settings, CancellationToken ct = default)
        {
            for (var i = 0; i < shot.Videos.Count; i++) {
                var name = $"video-{i:D2}.mp4"; await File.WriteAllBytesAsync(Path.Combine(directory, name), [1, 2, 3], ct);
                inputs.Add(new(name, false) { Kind = VideoInputKind.Video, VideoIndex = i });
            }
        }
    }
}
