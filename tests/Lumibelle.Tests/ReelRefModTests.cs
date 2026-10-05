using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static ShotVideoBinding RefModBinding(int count = 6)
    {
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('A', 64), 100, 640, 640, 124, 24, 124 / 24d, true);
        var binding = new ShotVideoBinding { Media = media, Name = "Character angles", Visuals = ReelVisuals.RefMod,
            Keyframes = new() { Frames = Enumerable.Range(0, count)
                .Select(i => new ReelKeyframe { Frame = new(media.Id, media.Sha256, i, i / 24d), Notes = "Angle " + i }).ToList() } };
        var hashes = Enumerable.Range(0, count).Select(i => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("frame" + i)))).ToArray();
        var recipe = ReelRefMods.Recipe(binding, 640, 640, new H3Settings().VideoVae, hashes);
        var build = Guid.NewGuid();
        binding.RefMod = new(recipe, "http://localhost:8188", ReelRefMods.BuildStem(Guid.NewGuid(), build), build);
        return binding;
    }
    private static RefModBuildRequest RefModRequest(ShotVideoBinding? binding = null, Guid? project = null)
    {
        binding ??= RefModBinding(2);
        return new(2, Guid.NewGuid(), project ?? Guid.NewGuid(), Guid.NewGuid(), binding.Media, binding.Keyframes!, binding.RefMod!.Recipe, "http://localhost:8188", 300);
    }
    private static JsonElement RefModOutput(RefModBuildRequest r) => JsonSerializer.SerializeToElement(new {
        status = new { completed = true, status_str = "success" }, outputs = new {
            build = new { refmod_saved = new[] { ReelRefMods.BuildStem(r.ProjectId, r.JobId) + ".safetensors", ReelRefMods.BuildStem(r.ProjectId, r.JobId) + ".png" } }
        }
    });
    private static JsonObject RefModLibrary(ReelRefModReference reference) => new() {
        ["items"] = new JsonArray(new JsonObject { ["visual"] = new JsonObject {
            ["file"] = reference.FileName, ["kind"] = "video", ["mode"] = "encode", ["source"] = "stack",
            ["t"] = reference.Recipe.LatentFrames, ["w"] = reference.Recipe.Width / 16, ["h"] = reference.Recipe.Height / 16
        } })
    };
    private static JsonElement RefModGraph(Shot shot, IReadOnlyList<PreparedVideoInput>? inputs = null) =>
        JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(Snapshot(Guid.NewGuid(), shot), 42, Guid.NewGuid().ToString("D"), inputs ?? []), AtomicJsonFile.Options).GetProperty("prompt");

    [Fact]
    public void RefModTrialPreservesExistingEnumNumbersAndOmittedMetadata()
    {
        Assert.Equal(0, (int)ReelVisuals.FullReel); Assert.Equal(1, (int)ReelVisuals.Keyframes); Assert.Equal(2, (int)ReelVisuals.None);
        // No fake safetensors upload input; the starting frame is a real image appended after the existing kinds.
        Assert.Equal([VideoInputKind.Image, VideoInputKind.Video, VideoInputKind.VideoSoundtrack, VideoInputKind.Audio, VideoInputKind.StartFrame], Enum.GetValues<VideoInputKind>());
        Assert.DoesNotContain("refMod", JsonSerializer.Serialize(new ShotVideoBinding(), AtomicJsonFile.Options));
        var binding = RefModBinding(); var clone = ShotCopy.Of(binding);
        Assert.True(ReelRefMods.Matches(clone, clone.RefMod)); Assert.NotSame(binding.Keyframes, clone.Keyframes);
        Assert.Equal(binding.RefMod!.FileName, clone.RefMod!.FileName);
        Assert.Equal(2400, clone.RefMod.Recipe.Tokens);
        Assert.DoesNotContain("vaeSha256", JsonSerializer.Serialize(clone.RefMod, AtomicJsonFile.Options));
    }
    [Theory]
    [InlineData(2)] [InlineData(6)] [InlineData(9)]
    public void RefModTrialConsumesOneVideoAndNoPictureOrUploadSlots(int count)
    {
        var mod = RefModBinding(count); var shot = Ready() with { Videos = [mod] };
        shot.Images = Enumerable.Range(0, 9).Select(i => new ShotImageBinding { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Picture " + i }).ToList();
        ReferenceVideos.Validate(shot, true);
        var resolved = ResolvedReferences.For(shot);
        Assert.Equal(9, resolved.Pictures.Count); Assert.Single(resolved.Videos); Assert.Empty(resolved.Audio);
        Assert.Equal("<Video 1>", resolved.Label(mod));
        Assert.Equal(9, resolved.InputOrder().Count); Assert.All(resolved.InputOrder(), i => Assert.Equal(VideoInputKind.Image, i.Kind));
        mod.Visuals = ReelVisuals.Keyframes;
        Assert.Equal(9 + count, ResolvedReferences.For(shot).Pictures.Count);
        Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot, true));
    }
    [Fact]
    public void RefModTrialOrderingPreservesFullVideoAndSeparateAudioNumbers()
    {
        var mod = RefModBinding(); mod.UseSoundtrack = true; mod.AudioExcerpt = new(0, 2);
        var full = RefModBinding(); full.Visuals = ReelVisuals.FullReel; full.RefMod = null; full.UseSoundtrack = true;
        var shot = Ready() with { Videos = [mod, full] }; var refs = ResolvedReferences.For(shot);
        Assert.Equal(new[] { full.Id, mod.Id }, refs.Videos.Select(v => v.Reel.Id));
        Assert.Equal(new[] { full.Id, mod.Id }, refs.Audio.Select(a => a.Reel!.Id));
        Assert.Equal("<Video 2> · <Audio 2>", refs.Label(mod));
        Assert.Equal(new[] { (VideoInputKind.Video, (int?)1), (VideoInputKind.VideoSoundtrack, (int?)1), (VideoInputKind.Audio, (int?)0) }, refs.InputOrder());
        ReferenceVideos.Validate(shot, true);
        var graph = RefModGraph(shot, [new("lumibelle/full.mp4", false) { Kind = VideoInputKind.Video, VideoIndex = 1 },
            new("lumibelle/full.wav", true) { Kind = VideoInputKind.VideoSoundtrack, VideoIndex = 1 },
            new("lumibelle/excerpt.wav", true) { Kind = VideoInputKind.Audio, VideoIndex = 0 }]);
        using var media = JsonDocument.Parse(graph.GetProperty("refmedia").GetProperty("inputs").GetProperty("media_state").GetString()!);
        Assert.Equal(new[] { "video", "audio", "audio" }, media.RootElement.EnumerateArray().Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(new[] { "lumibelle/full.mp4", "lumibelle/full.wav", "lumibelle/excerpt.wav" }, media.RootElement.EnumerateArray().Select(i => i.GetProperty("file").GetString()));
        Assert.All(media.RootElement.EnumerateArray(), i => { Assert.False(i.GetProperty("has_audio").GetBoolean()); Assert.Equal("off", i.GetProperty("audio_mode").GetString()); });
    }
    [Fact]
    public void RefModTrialFrameOrderAndCropsInvalidateButProseAndBindingIdsDoNot()
    {
        var binding = RefModBinding(); var reference = binding.RefMod!;
        var copied = ShotCopy.Of(binding); copied.Id = Guid.NewGuid(); copied.Description = "A new intended use";
        copied.Keyframes!.Frames = copied.Keyframes.Frames.Select(f => f with { Id = Guid.NewGuid(), Notes = "New prose" }).ToList();
        Assert.True(ReelRefMods.Matches(copied, reference));
        copied.Keyframes.Frames.Reverse(); Assert.False(ReelRefMods.Matches(copied, reference));
        Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.ValidateBinding(copied, true));
        copied = ShotCopy.Of(binding); copied.Keyframes!.Frames[0].Crop = new() { Width = .5 };
        Assert.False(ReelRefMods.Matches(copied, reference));
        copied = ShotCopy.Of(binding); var first = copied.Keyframes!.Frames[0];
        copied.Keyframes.Frames[0] = first with { Frame = first.Frame with { Source = new('E', 64) } };
        Assert.False(ReelRefMods.Matches(copied, reference));
    }
    [Fact]
    public void RefModTrialCanvasVaeNameAndPreparedPixelsEnterSourceIdentity()
    {
        var binding = RefModBinding(); var r = binding.RefMod!.Recipe;
        Assert.NotEqual(r.Key, ReelRefMods.Recipe(binding, 640, 640, "other-video-vae.safetensors", r.FrameHashes).Key);
        Assert.NotEqual(r.Key, ReelRefMods.Recipe(binding, 480, 832, r.VaeName, r.FrameHashes).Key);
        var hashes = r.FrameHashes.ToArray(); hashes[0] = new('E', 64);
        Assert.NotEqual(r.Key, ReelRefMods.Recipe(binding, 640, 640, r.VaeName, hashes).Key);
        Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.Validate(r with { Width = 480 }));
    }
    [Theory]
    [InlineData("https://localhost:8188")] [InlineData("http://other:8188")] [InlineData("http://localhost:8189")] [InlineData("http://localhost:8188/other")]
    public void RefModTrialRejectsAnotherServerWithoutNetworkOrFallback(string server)
    {
        var reference = RefModBinding().RefMod!;
        Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.ValidateServer(reference, server, reference.Recipe.VaeName));
    }
    [Fact]
    public void RefModTrialRejectsAnotherVaeAndUnsafeOrMismatchedFilenames()
    {
        var reference = RefModBinding().RefMod!;
        ReelRefMods.ValidateServer(reference, reference.ComfyUrl + "/", reference.Recipe.VaeName);
        var vae = Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.ValidateServer(reference, reference.ComfyUrl, "another.safetensors"));
        Assert.Contains($"prepared with the H3 video VAE {reference.Recipe.VaeName}, but this batch uses another.safetensors", vae.Message);
        foreach (var file in new[] { "../model", "/absolute", "C:/model", "a//b", "file.safetensors", "model#0", reference.FileName + "x" })
            Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.Validate(reference with { FileName = file }));
    }
    [Fact]
    public void RefModTrialMissingBuildNeverFallsBackToAFullReel()
    {
        var mod = RefModBinding(); mod.RefMod = null;
        ReelRefMods.ValidateBinding(mod, false);
        Assert.Contains("No full-reel fallback", Assert.Throws<WorkspaceStoreException>(() => ReelRefMods.ValidateBinding(mod, true)).Message);
        Assert.Throws<WorkspaceStoreException>(() => RefModGraph(Ready() with { Videos = [mod] }));
    }
    [Fact]
    public void RefModTrialBuildUsesOnlyFantasticCreateAndIndependentPictureSources()
    {
        var r = RefModRequest(); var paths = r.Recipe.FrameHashes.Select((_, i) => $"lumibelle/refmod-trials/angle-{i}.png").ToArray();
        var graph = JsonSerializer.SerializeToElement(ComfyRefModClient.BuildWorkflow(r, paths, "client")).GetProperty("prompt");
        Assert.Equal(2, graph.EnumerateObject().Count());
        var build = graph.GetProperty("build"); Assert.Equal("MiniMaxH3FantasticRefModCreate", build.GetProperty("class_type").GetString());
        var input = build.GetProperty("inputs");
        Assert.Equal("Full Reference", input.GetProperty("mode").GetString()); Assert.Equal(0, input.GetProperty("refinement_steps").GetInt32());
        Assert.Equal(640, input.GetProperty("ref_resolution").GetInt32());
        Assert.False(input.TryGetProperty("image", out _)); Assert.False(input.TryGetProperty("audio", out _)); Assert.False(input.TryGetProperty("audio_vae", out _));
        using var sources = JsonDocument.Parse(input.GetProperty("source").GetString()!);
        Assert.Equal(r.Recipe.LatentFrames, sources.RootElement.GetArrayLength());
        Assert.All(sources.RootElement.EnumerateArray(), s => Assert.Equal("picture", s.GetProperty("kind").GetString()));
        Assert.Equal(paths, sources.RootElement.EnumerateArray().Select(s => s.GetProperty("file").GetString()));
        Assert.Throws<WorkspaceStoreException>(() => ComfyRefModClient.BuildWorkflow(r, [], "client"));
        var other = r with { JobId = Guid.NewGuid() };
        Assert.NotEqual(ReelRefMods.BuildStem(r.ProjectId, r.JobId), ReelRefMods.BuildStem(other.ProjectId, other.JobId));
    }
    [Fact]
    public void RefModTrialReadsFilenameReceiptAsAStemAndRejectsUnexpectedOutput()
    {
        var r = RefModRequest(); var reference = ComfyRefModClient.ReadOutput(RefModOutput(r), r);
        Assert.Equal(ReelRefMods.BuildStem(r.ProjectId, r.JobId), reference.FileName);
        Assert.DoesNotContain(".safetensors", reference.FileName); Assert.Equal(r.ComfyUrl, reference.ComfyUrl);
        var bad = JsonSerializer.SerializeToElement(new { outputs = new { build = new { refmod_saved = new[] { "unrelated.safetensors" } } } });
        Assert.Throws<WorkspaceStoreException>(() => ComfyRefModClient.ReadOutput(bad, r));
    }
    [Theory]
    [InlineData("missing")] [InlineData("kind")] [InlineData("mode")] [InlineData("source")] [InlineData("t")] [InlineData("w")] [InlineData("duplicate")]
    public void RefModTrialRemotePreflightRejectsMissingOrStructurallyDifferentFiles(string change)
    {
        var reference = RefModBinding().RefMod!; var library = RefModLibrary(reference);
        Assert.True(ComfyRefModClient.ReferenceAvailable(JsonSerializer.SerializeToElement(library), reference));
        var visual = library["items"]![0]!["visual"]!;
        if (change == "missing") library["items"] = new JsonArray();
        else if (change == "duplicate") library["items"]!.AsArray().Add(library["items"]![0]!.DeepClone());
        else if (change is "t" or "w") visual[change] = 1;
        else visual[change] = "different";
        Assert.False(ComfyRefModClient.ReferenceAvailable(JsonSerializer.SerializeToElement(library), reference));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RefModTrialGenerationKeepsNativePathAndChangesOnlyExplicitRefs(bool sparse)
    {
        var shot = Ready(); if (sparse) shot.Videos = [RefModBinding()];
        var graph = RefModGraph(shot);
        Assert.Equal(sparse ? ComfyRefModClient.EncodeNode : "MiniMaxH3ReferenceToVideo", graph.GetProperty("5").GetProperty("class_type").GetString());
        Assert.Equal(sparse ? 2 : 1, graph.GetProperty("10").GetProperty("inputs").GetProperty("latent_image")[1].GetInt32());
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.BuildNode);
        Assert.DoesNotContain("RefModApply", graph.GetRawText()); Assert.DoesNotContain("LumibelleH3RefMod", graph.GetRawText());
        if (sparse)
        {
            using var state = JsonDocument.Parse(graph.GetProperty("refmods").GetProperty("inputs").GetProperty("stack_state").GetString()!);
            var pick = Assert.Single(state.RootElement.GetProperty("picks").EnumerateArray());
            Assert.True(pick.GetProperty("on").GetBoolean()); Assert.False(pick.TryGetProperty("audio", out _));
            Assert.Equal("all", graph.GetProperty("5").GetProperty("inputs").GetProperty("stack_pictures").GetString());
            Assert.Equal(shot.Videos[0].RefMod!.FileName, pick.GetProperty("visual").GetProperty("file").GetString());
            Assert.Equal(1, pick.GetProperty("visual").GetProperty("w").GetDouble());
        }
    }
    [Fact]
    public void RefModTrialKeepsADirectFacePictureAndVoiceSeparateFromAngles()
    {
        var mod = RefModBinding(); mod.UseSoundtrack = true; mod.AudioExcerpt = new(0, 2);
        var shot = Ready() with { Videos = [mod], Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" }] };
        var graph = RefModGraph(shot, [new("face.png", false), new("voice.wav", true) { Kind = VideoInputKind.Audio, VideoIndex = 0 }]);
        using var media = JsonDocument.Parse(graph.GetProperty("refmedia").GetProperty("inputs").GetProperty("media_state").GetString()!);
        Assert.Equal(new[] { "picture", "audio" }, media.RootElement.EnumerateArray().Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal("<Video 1> · <Audio 1>", ResolvedReferences.For(shot).Label(mod));
        Assert.Single(ResolvedReferences.For(shot).Pictures);
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.BuildNode);
    }
    [Theory]
    [InlineData(2)] [InlineData(4)]
    public void RefModTrialMultiTakeSharesTheUpstreamStackAndConditioning(int count)
    {
        var shot = Ready() with { Videos = [RefModBinding()] }; var snapshot = Snapshot(Guid.NewGuid(), shot);
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), count, 42).Candidates;
        var graph = JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(snapshot, c.Seed, c.Id.ToString("D"), []), "client"), AtomicJsonFile.Options).GetProperty("prompt");
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.EncodeNode);
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.LoadNode);
        foreach (var candidate in candidates)
            Assert.Equal(2, graph.GetProperty(ComfyMultiTakeWorkflow.Node(candidate, "10")).GetProperty("inputs").GetProperty("latent_image")[1].GetInt32());
    }
    [Fact]
    public void RefModTrialCapabilitiesRequireUpstreamNotCompanionNodes()
    {
        var native = new H3Configuration(true, true, "ready", [], [], [], []);
        Assert.True(native.Ready(Ready())); Assert.False(native.Ready(Ready() with { Videos = [RefModBinding()] }));
        JsonObject Node(string[] fields, params string[] outputs) => new() {
            ["input"] = new JsonObject { ["required"] = new JsonObject(fields.Select(f => KeyValuePair.Create<string, JsonNode?>(f, new JsonArray("STRING")))) },
            ["output"] = new JsonArray(outputs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
        var catalog = new JsonObject {
            [ComfyRefModClient.EncodeNode] = Node(["clip", "prompt", "width", "height", "length", "ref_image_size", "reference_fps", "max_total_tokens", "mods", "references", "vae", "audio_vae"], "CONDITIONING", "STRING", "LATENT"),
            [ComfyRefModClient.LoadNode] = Node(["stack_state"], "H3_REF_MODS", "STRING"),
            [ComfyRefModClient.MediaNode] = Node(["media_state"], "H3_REFS"),
            [ComfyRefModClient.BuildNode] = Node(["name", "subfolder", "mode", "ref_resolution", "grid", "latent_frames", "refinement_steps", "max_tokens", "audio_max_seconds", "concept_type", "description", "write_preview", "source", "vae"], "H3_REF_MODS", "STRING") };
        foreach (var (node, field, type) in new[] {
            (ComfyRefModClient.EncodeNode, "clip", "CLIP"), (ComfyRefModClient.EncodeNode, "vae", "VAE"),
            (ComfyRefModClient.EncodeNode, "audio_vae", "VAE"), (ComfyRefModClient.EncodeNode, "mods", "H3_REF_MODS"),
            (ComfyRefModClient.EncodeNode, "references", "H3_REFS"), (ComfyRefModClient.BuildNode, "vae", "VAE") })
            catalog[node]!["input"]!["required"]![field] = new JsonArray(type);
        catalog[ComfyRefModClient.BuildNode]!["input"]!["required"]!["mode"] = new JsonArray(new JsonArray("Full Reference", "Compressed Reference"));
        // Fantastic before 1.8.0 has no stack_pictures, so its encoder would see only some of a RefMod's keyframes.
        Assert.NotNull(ComfyRefModClient.Inspect(JsonSerializer.SerializeToElement(catalog)));
        catalog[ComfyRefModClient.EncodeNode]!["input"]!["required"]!["stack_pictures"] = new JsonArray(new JsonArray("every 4th", "up to N", "all"));
        Assert.Null(ComfyRefModClient.Inspect(JsonSerializer.SerializeToElement(catalog)));
        catalog[ComfyRefModClient.EncodeNode]!["output"] = new JsonArray("CONDITIONING", "LATENT", "STRING");
        Assert.NotNull(ComfyRefModClient.Inspect(JsonSerializer.SerializeToElement(catalog)));
    }
    [Fact]
    public async Task RefModTrialRetainsPreviewsAndReceiptButNoLocalLatentFile()
    {
        var f = Fixture(); var store = new ReelRefModStore(f.Files); var mod = RefModBinding(2); var r = RefModRequest(mod, f.Project.Id);
        Assert.Null(await store.FindAsync(f.Project.Id, r.Recipe.Key, r.ComfyUrl, _ct));
        var input = Path.Combine(await store.BuildDirectoryAsync(f.Project.Id, r.JobId, _ct), "inputs"); Directory.CreateDirectory(input);
        for (var i = 0; i < 2; i++) await File.WriteAllBytesAsync(Path.Combine(input, ReelRefModStore.FrameName(i)), Encoding.UTF8.GetBytes("frame" + i), _ct);
        var reference = ComfyRefModClient.ReadOutput(RefModOutput(r), r);
        await store.PublishAsync(r, reference, _ct); await store.PublishAsync(r, reference, _ct);
        Assert.Equal(reference.FileName, (await store.FindAsync(f.Project.Id, r.Recipe.Key, r.ComfyUrl, _ct))!.FileName);
        Assert.Null(await store.FindAsync(f.Project.Id, r.Recipe.Key, "http://other:8188", _ct));
        Assert.Equal(Encoding.UTF8.GetBytes("frame1"), await store.PreviewAsync(f.Project.Id, reference, 1, _ct));
        Assert.Empty(Directory.EnumerateFiles(await f.Files.DirectoryAsync(f.Project.Id, _ct), "*.safetensors", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task RefModTrialUnsubmittedRecoveryNeverContactsProvider()
    {
        var f = Fixture(); var request = RefModRequest(project: f.Project.Id); var jobs = new FileAiJobStore(Path.Combine(_root, "refmod-jobs"), TimeProvider.System);
        var submission = AiJobSubmission.Create(request.JobId, AiJobKind.RefModBuild, AiBackend.ComfyUI,
            new(f.Project.Id, request.AssetId, ReelId: request.Media.Id), "Test", "RefMod", Guid.NewGuid(), request);
        await jobs.EnqueueAsync(submission, _ct);
        var claimed = (await jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        var context = new AiJobContext(claimed, true, jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var handler = new AiRefModJobHandler(new(f.Files), new(new RefModNoHttp()), new(new RefModNoMonitor()), TimeProvider.System);
        var outcome = await handler.RecoverAsync(context, submission.Snapshot, _ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State); Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery);
        Assert.Empty((await context.ExecutionAsync(_ct)).Submissions);
    }
    [Fact]
    public void RefModTrialComposerMapsSourcePreviewsToVideoNotPictureLabels()
    {
        var mod = RefModBinding(2); var shot = Ready() with { Videos = [mod] };
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "baseline", "source", shot,
            "scene", [], [], [], [], "", "", "", new(AiBackend.OpenRouter, "mock", "Mock"), false);
        var frames = Enumerable.Range(0, 2).Select(i => new RefModInspectionFrame(1, i + 1, mod.Name,
            mod.Keyframes!.Frames[i].Notes, mod.RefMod!.Recipe.FrameHashes[i], Encoding.UTF8.GetBytes("frame" + i))).ToArray();
        var messages = PromptComposer.BuildMessages(request, [], frames);
        Assert.Equal(3, messages[1].Contents.Count);
        using var payload = JsonDocument.Parse(messages[1].Text!);
        Assert.Equal(1, payload.RootElement.GetProperty("sparseVisualReferences")[0].GetProperty("video").GetInt32());
        Assert.Empty(payload.RootElement.GetProperty("references").EnumerateArray());
        Assert.Contains("never create Picture identifiers", messages[0].Text);
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(request, []));
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(request, [], frames.Reverse().ToArray()));
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(request, [], [frames[0] with { Png = [0] }, frames[1]]));
    }
    [Fact]
    public async Task RefModTrialAcceptedBuildRetriesReceiptWithoutUploadingOrResubmitting()
    {
        var f = Fixture(); var store = new ReelRefModStore(f.Files); var mod = RefModBinding(2); var r = RefModRequest(mod, f.Project.Id);
        var jobs = new FileAiJobStore(Path.Combine(_root, "accepted-refmod-jobs"), TimeProvider.System);
        var submission = AiJobSubmission.Create(r.JobId, AiJobKind.RefModBuild, AiBackend.ComfyUI,
            new(f.Project.Id, r.AssetId, ReelId: mod.Media.Id), "Test", "Build RefMod", Guid.NewGuid(), r);
        await jobs.EnqueueAsync(submission, _ct); var claimed = (await jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        var context = new AiJobContext(claimed, false, jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var clientId = Guid.NewGuid().ToString("D");
        var workflow = JsonSerializer.SerializeToElement(ComfyRefModClient.BuildWorkflow(r, ["angle1.png", "angle2.png"], clientId));
        await context.SaveOperationAsync(AiRefModJobHandler.Operation, AiOperationArtifact.Request, new ComfySavedOperation(r.ComfyUrl, clientId, workflow, ComfyRefModClient.BuildOptions), _ct);
        await context.BeginRemoteAsync(AiRefModJobHandler.Operation, r.ComfyUrl, clientId);
        await context.AcceptRemoteAsync(AiRefModJobHandler.Operation, Guid.NewGuid().ToString("D"));
        await context.SaveOperationAsync(AiRefModJobHandler.Operation, AiOperationArtifact.Output, RefModOutput(r), _ct);
        var input = Path.Combine(await store.BuildDirectoryAsync(f.Project.Id, r.JobId, _ct), "inputs"); Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, ReelRefModStore.FrameName(0)), Encoding.UTF8.GetBytes("frame0"), _ct);
        var reference = ComfyRefModClient.ReadOutput(RefModOutput(r), r);
        using var transport = new RefModLibraryOnly(RefModLibrary(reference));
        var recovered = new AiJobContext(claimed, true, jobs, TimeProvider.System, (_, _) => { }, _ => { }, _ct);
        var worker = new AiRefModJobHandler(store, new(transport), new(new RefModNoMonitor()), TimeProvider.System);
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => worker.RecoverAsync(recovered, submission.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery);
        await File.WriteAllBytesAsync(Path.Combine(input, ReelRefModStore.FrameName(1)), Encoding.UTF8.GetBytes("frame1"), _ct);
        Assert.Equal(AiJobState.Completed, (await worker.RecoverAsync(recovered, submission.Snapshot, _ct)).State);
        Assert.Equal(2, transport.Gets); Assert.Single((await recovered.ExecutionAsync(_ct)).Submissions);
        Assert.False((await recovered.ExecutionAsync(_ct)).MayBeRunning);
        Assert.Equal(reference.FileName, (await recovered.ReadAsync<RefModBuildResult>(AiJobArtifact.Result, _ct))!.Reference.FileName);
    }
    private sealed class RefModLibraryOnly(JsonObject library) : HttpMessageHandler, IHttpClientFactory
    {
        public int Gets;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("/minimax_h3/refmods", request.RequestUri!.AbsolutePath);
            Gets++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(library.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class RefModNoHttp : IHttpClientFactory
    { public HttpClient CreateClient(string name) => throw new InvalidOperationException("Recovery must not contact the provider."); }
    private sealed class RefModNoMonitor : IComfyExecutionMonitor
    {
        public IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
            ComfyExecutionOptions options, CancellationToken operationToken, CancellationToken callerToken) => throw new InvalidOperationException("Recovery must not encode.");
    }
}
