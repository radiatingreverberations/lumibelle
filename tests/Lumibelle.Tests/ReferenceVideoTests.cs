using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private const string ReferenceLine = "I thought we'd take the quiet road home before the rain starts again. All's well that ends well!";
    private static ShotVideoBinding Clip(bool audio = true) => new() { Name = "Riley reference", UseSoundtrack = audio,
        Media = new(Guid.NewGuid(), new('A', 64), 4, 160, 96, 124, 24, 124 / 24d, audio) };
    private static string ReferencePrompt(Shot shot)
    {
        var noVideos = shot.Copy(); noVideos.Videos.Clear(); noVideos.Voices.Clear(); noVideos.CharacterVoices = null;
        var prompt = H3Policy.Compile(noVideos);
        var definitions = string.Join("\n", shot.Videos.Select((v, n) => $"<Video {n + 1}> supplies additional identity views."));
        definitions += "\n" + string.Join("\n", ReferenceVideos.AudioMappings(shot).Select(a => $"<Audio {a.Number}> supplies voice timbre" +
            (string.IsNullOrWhiteSpace(a.Speaker) ? "." : $" for {a.Speaker} (S{shot.Dialogue.Select(d => d.Speaker).Distinct().ToList().IndexOf(a.Speaker) + 1}).")));
        return prompt.Replace("subject_definitions:", "subject_definitions:\n" + definitions);
    }
    private static VideoSnapshot ReferenceSnapshot(Shot shot)
    {
        var prompt = ReferencePrompt(shot); var size = H3Policy.Size(shot.Aspect, shot.NativeResolution);
        return new(Guid.NewGuid(), 1, shot.Copy(), prompt, H3Policy.Fingerprint(shot), "http://localhost:8188", new(), size.Width, size.Height, H3Policy.Frames(shot.Duration!.Value), ProductionPolicy.Profile)
        { ReferenceGuidance = [], Appearances = [], Sampling = H3Policy.Sampling(shot, new()),
            Production = new(Guid.NewGuid(), 1, "Setup", new(Guid.NewGuid(), DateTimeOffset.UtcNow, prompt, "", "context", "source"), []) };
    }

    [Theory]
    [InlineData(null)] [InlineData("colons")] [InlineData("duration")] [InlineData("requested-duration")] [InlineData("speaker")] [InlineData("dialogue")] [InlineData("references")]
    public async Task CharacterReferenceTemplateMatchesActualProductionValidator(string? defect)
    {
        var shot = Ready() with { Duration = 5, Aspect = "1:1" };
        shot.Dialogue = [new() { Speaker = "Riley", Text = ReferenceLine }];
        shot.Images = Enumerable.Range(0, 3).Select(_ => new ShotImageBinding { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid() }).ToList();
        var prompt = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "character-reference-prompt.txt"), _ct)).Replace("{{CHARACTER_NAME}}", "Riley");
        var error = defect switch { "colons" => "Missing H3 sections: subject_definitions", "duration" => "5.167 seconds", "speaker" => "speaker label", "dialogue" => "every dialogue line", "references" => "not selected", _ => null };
        switch (defect)
        {
            case "colons": prompt = prompt.Replace("subject_definitions:", "subject_definitions"); break;
            // An unrelated duration is still rejected; the authored Requested seconds is now acceptable.
            case "duration": prompt = prompt.Replace("5.167", "9"); break;
            case "requested-duration": prompt = prompt.Replace("5.167", "5"); break;
            case "speaker": shot.Dialogue[0].Speaker = "Mira"; break;
            case "dialogue": shot.Dialogue[0].Text = "Different words."; break;
            case "references": shot.Images.RemoveAt(2); break;
        }
        if (error is null) { ProductionPolicy.ValidatePrompt(prompt, shot); Assert.Contains($"<d>[English] {ReferenceLine}</d>", prompt); }
        else Assert.Contains(error, Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.ValidatePrompt(prompt, shot)).Message);
    }

    [Fact]
    public void VideoAudioNumbersAndNodeConnectionsAgreeWithoutDuplicateSoundtracks()
    {
        var shot = Ready() with { Duration = 5 };
        shot.Videos = [Clip(false), Clip()]; shot.Videos[1].Speaker = "Riley";
        shot.Dialogue = [new() { Speaker = "Riley", Text = "Hello." }, new() { Speaker = "Sam", Text = "Welcome." }];
        shot.Voices = [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Sam", Duration = 2 }];
        H3Policy.Validate(shot, true); ProductionPolicy.ValidatePrompt(ReferencePrompt(shot), shot);
        Assert.Null(ReferenceVideos.SoundtrackNumber(shot, 0)); Assert.Equal(1, ReferenceVideos.SoundtrackNumber(shot, 1)); Assert.Equal(2, ReferenceVideos.VoiceNumber(shot, 0));
        var inputs = ReferenceVideos.InputOrder(shot).Select((i, n) => new PreparedVideoInput($"file-{n}{AiVideoJobPolicy.Extension(i.Kind)}", i.Kind is VideoInputKind.Audio or VideoInputKind.VideoSoundtrack) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(ReferenceSnapshot(shot), 42, "test", inputs)).GetProperty("prompt");
        var conditioning = graph.GetProperty("5").GetProperty("inputs");
        Assert.Equal("101-frames", conditioning.GetProperty("ref_videos.ref_video_1")[0].GetString());
        Assert.Equal("102", conditioning.GetProperty("ref_video_audios.ref_video_audio_1")[0].GetString());
        Assert.False(conditioning.TryGetProperty("ref_video_audios.ref_video_audio_0", out _));
        Assert.Equal("103", conditioning.GetProperty("ref_audios.ref_audio_0")[0].GetString());
        Assert.False(conditioning.TryGetProperty("ref_audios.ref_audio_1", out _));
        Assert.Equal("LoadVideo", graph.GetProperty("100").GetProperty("class_type").GetString());
        Assert.Equal("GetVideoComponents", graph.GetProperty("101-frames").GetProperty("class_type").GetString());
        Assert.Equal(ReferencePrompt(shot), conditioning.GetProperty("prompt").GetString());
        shot.Videos.Reverse(); Assert.Equal(1, ReferenceVideos.SoundtrackNumber(shot, 0)); Assert.Null(ReferenceVideos.SoundtrackNumber(shot, 1));
    }

    [Fact]
    public void VideoContextUsesDescriptionsAndDoesNotAddInspectionImagesOrInventAudio()
    {
        var shot = Ready() with { Duration = 5 }; shot.Videos = [Clip(), Clip(false)];
        shot.Videos[0].Description = "Author describes a three-quarter view. 日本語";
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], [], [], [], "Direction", "", "", new(AiBackend.OpenRouter, "text", "Text"));
        var messages = PromptComposer.BuildMessages(request, []);
        Assert.Contains("NOT attached for inspection", messages[0].Text); Assert.Contains("Do not claim to watch videos", messages[0].Text);
        Assert.Empty(messages[1].Contents.OfType<Microsoft.Extensions.AI.DataContent>());
        using var json = JsonDocument.Parse(messages[1].Text!); var videos = json.RootElement.GetProperty("videos");
        Assert.Equal(shot.Videos[0].Description, videos[0].GetProperty("authorProvidedDescription").GetString());
        Assert.Equal(1, videos[0].GetProperty("audio").GetInt32()); Assert.Equal(JsonValueKind.Null, videos[1].GetProperty("audio").ValueKind);
        Assert.DoesNotContain(ReferenceLine, messages[1].Text!);
        var copied = new ProductionComposition { Shot = shot }.Copy(); copied.Shot.Videos[0].Description = "Independent";
        Assert.NotEqual(shot.Videos[0].Description, copied.Shot.Videos[0].Description);
        Assert.Empty(ProductionPolicy.CoverageCopy(shot).Videos);
    }

    [Theory]
    [InlineData("length")] [InlineData("silent")] [InlineData("speaker")] [InlineData("count")] [InlineData("total")]
    public void VideoReferenceLimitsAreActionable(string invalid)
    {
        var shot = Ready() with { Duration = 5 }; shot.Videos = [Clip()];
        switch (invalid)
        {
            case "length": shot.Videos[0].Media = shot.Videos[0].Media with { Duration = 30 }; break;
            case "silent": shot.Videos[0].Media = shot.Videos[0].Media with { HasAudio = false }; break;
            case "speaker": shot.Videos[0].Speaker = "Not in dialogue"; break;
            case "count": shot.Videos = [Clip(), Clip(), Clip(), Clip()]; break;
            case "total": shot.Videos = [Clip(), Clip(), Clip()]; break;
        }
        Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot, true));
        Assert.Empty(ProductionPolicy.CoverageCopy(shot).Videos);
    }

    [Fact]
    public void AnExceededLimitNamesItselfAndWhatAddsUpToIt()
    {
        var reel = Clip(false); reel.Visuals = ReelVisuals.Keyframes;
        reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 0, 0) }] };
        var shot = Ready() with { Duration = 5, Videos = [reel], Voices = [
            new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "RILEY", Duration = 10.144 },
            new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "GUARD", Duration = 10.144 }] };
        var audio = Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot)).Message;
        Assert.Equal($"Audio references can add up to 15 seconds in total; these add {20.288:0.#} s (RILEY {10.144:0.#} s, GUARD {10.144:0.#} s). Shorten an excerpt, for example with its audio duration.", audio);
        shot.Voices[1].Duration = 4.9; ReferenceVideos.Validate(shot);
        shot.Videos = [Clip(), Clip(), Clip(), Clip()];
        Assert.Equal("Use up to three reels; this shot has 4.", Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot)).Message);
    }

    [Fact]
    public async Task VideoAttachmentsAreIndependentAndRejectChangedFiles()
    {
        var f = Fixture(); var media = new ReferenceMediaFake(); var store = new FileReferenceVideoStore(f.Files, f.Shots, media);
        await using var original = new MemoryStream([1, 2, 3, 4]);
        var imported = await store.ImportAsync(f.Project.Id, original, "reference.mp4", new(), _ct);
        original.Position = 0; original.WriteByte(9);
        var binding = Clip(); binding.Media = imported;
        await store.ValidateAsync(f.Project.Id, [binding], _ct);
        await using (var opened = await store.OpenAsync(f.Project.Id, imported.Id, _ct))
        { Assert.Equal(1, opened!.Content.ReadByte()); await using var ranged = MediaResources.Respond(opened, "GET", new Dictionary<string, string> { ["Range"] = "bytes=1-2" }); Assert.Equal(206, ranged.Status); Assert.Equal(2, ranged.Content.Length); }
        var path = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", imported.Id.ToString(), "video.mp4");
        await File.WriteAllBytesAsync(path, [4, 3, 2, 1], _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ValidateAsync(f.Project.Id, [binding], _ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportAsync(f.Project.Id, new MemoryStream([1]), "cancel.mp4", new(), cancelled.Token));
    }

    [Fact]
    public async Task VideoManifestRoundTripsAndRetriesStayIndependentOfCurrentReferences()
    {
        var shot = Ready() with { Duration = 5 }; shot.Videos = [Clip()]; var snapshot = ReferenceSnapshot(shot);
        var folder = Path.Combine(_root, "capture"); Directory.CreateDirectory(Path.Combine(folder, "inputs"));
        List<AiVideoInput> inputs = [];
        foreach (var (kind, index) in ReferenceVideos.InputOrder(shot))
        {
            var file = kind + AiVideoJobPolicy.Extension(kind); byte[] bytes = [1, 2, 3, (byte)kind];
            await File.WriteAllBytesAsync(Path.Combine(folder, "inputs", file), bytes, _ct);
            inputs.Add(new(file, kind != VideoInputKind.Video, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))) { Kind = kind, VideoIndex = index });
        }
        var request = ShotCopy.Of(new AiVideoJobRequest(2, Guid.NewGuid(), snapshot, inputs));
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, folder, _ct);
        shot.Videos.Clear(); // Saved request is detached from edits/removal.
        Assert.Equal(2, AiVideoJobPolicy.Run(request).Inputs.Count);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, folder, _ct);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Inputs = inputs.AsEnumerable().Reverse().ToArray() }));
        await File.WriteAllBytesAsync(Path.Combine(folder, "inputs", inputs[0].FileName), [8, 7, 6, 5], _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => AiVideoJobPolicy.ValidatePreparedFilesAsync(request, folder, _ct));
        var legacy = JsonSerializer.SerializeToNode(Ready(), AtomicJsonFile.Options)!; legacy.AsObject().Remove("videos");
        var restored = legacy.Deserialize<Shot>(AtomicJsonFile.Options)!;
        Assert.Empty(restored.Videos); Assert.Equal(H3Policy.Fingerprint(restored), H3Policy.Fingerprint(restored.Copy()));
        Assert.Equal(VideoInputKind.Audio, JsonSerializer.Deserialize<AiVideoInput>("{\"fileName\":\"voice.wav\",\"audio\":true,\"bytes\":4,\"sha256\":\"a\"}", AtomicJsonFile.Options)!.EffectiveKind);
    }

    [Fact]
    public void VideoCapabilitiesAreRequiredOnlyForVideoSetups()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/h3-contract.json")))!;
        H3Configuration Check() => ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), new());
        Assert.True(Check().Ready(Ready())); Assert.NotNull(Check().VideoReferenceIssue);
        root["LoadVideo"] = JsonNode.Parse("""{"input":{"required":{"file":["COMBO",{}]}},"output":["VIDEO"]}""");
        root["GetVideoComponents"] = JsonNode.Parse("""{"input":{"required":{"video":["VIDEO",{}]}},"output":["IMAGE","AUDIO","FLOAT"]}""");
        Assert.Null(Check().VideoReferenceIssue);
        root["MiniMaxH3ReferenceToVideo"]!["input"]!["optional"]!["ref_videos"]![1]!["template"]!["max"] = 1;
        Assert.NotNull(Check().VideoReferenceIssue); Assert.True(Check().Ready(Ready()));
        Assert.False(Check().Ready(Ready() with { Videos = [Clip()] }));
    }

    [Fact]
    public async Task VideoReferencesStayWithTheShotAcrossSetupsConflictsArchivingAndRecovery()
    {
        var f = Fixture(); var source = Ready(); await f.Shots.SaveAsync(f.Project.Id, [source], 0, ct: _ct);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var jobs = new FileAiJobStore(Path.Combine(_root, "jobs"), _clock);
        var videos = new FileReferenceVideoStore(f.Files, f.Shots, new ReferenceMediaFake());
        var store = new FileProductionStore(f.Files, f.Shots, f.Assets, projects, _clock, jobs, videos);
        var c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions[0];
        await using var bytes = new MemoryStream([1, 2, 3, 4]);
        c.Shot.Videos = [new() { Media = await videos.ImportAsync(f.Project.Id, bytes, "ref.mp4", new(), _ct), UseSoundtrack = true }];
        c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions[0];
        var duplicate = c.Copy(); duplicate.Id = Guid.NewGuid(); duplicate.Name = "Alternative"; duplicate.Shot.Videos[0].Description = "Another intended use";
        var d = await store.SaveAsync(f.Project.Id, duplicate, 0, _ct); duplicate = d.Compositions.Single(x => x.Id == duplicate.Id);
        Assert.Equal(c.Shot.Videos[0].Description, duplicate.Shot.Videos[0].Description);
        Assert.Equal(c.Shot.Videos[0].Media.Id, duplicate.Shot.Videos[0].Media.Id);
        duplicate.Shot.Videos[0].Description = "Shared intended use";
        d = await store.SaveAsync(f.Project.Id, duplicate, duplicate.Version, _ct);
        Assert.All(d.Compositions, setup => Assert.Equal("Shared intended use", setup.Shot.Videos[0].Description));
        duplicate = d.Compositions.Single(x => x.Id == duplicate.Id);
        duplicate.Archived = true; await store.SaveAsync(f.Project.Id, duplicate, duplicate.Version, _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(f.Project.Id, duplicate, duplicate.Version, _ct));
        var coverage = await f.Shots.LoadAsync(f.Project.Id, _ct);
        coverage = await f.Shots.DeleteShotsAsync(f.Project.Id, [source.Id], coverage.Revision, ct: _ct);
        await f.Shots.RecoverAsync(f.Project.Id, coverage.Recovery[0].Id, coverage.Revision, _ct);
        c = (await store.InitializeAsync(f.Project.Id, _ct)).Compositions.Single(x => x.Id == c.Id);
        Assert.Single(c.Shot.Videos); await videos.ValidateAsync(f.Project.Id, c.Shot.Videos, _ct);
        var original = c.Copy(); c.Shot.Videos.Clear(); c = (await store.SaveAsync(f.Project.Id, c, c.Version, _ct)).Compositions.Single(x => x.Id == c.Id);
        original = (await store.SaveAsync(f.Project.Id, original, c.Version, _ct)).Compositions.Single(x => x.Id == c.Id);
        await videos.ValidateAsync(f.Project.Id, original.Shot.Videos, _ct); // Undo unlinks/relinks; files remain available.
    }

    [Fact]
    public async Task RealReferencePreparationHasSynchronizedAudioAndBoundedFrames()
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "synthetic.mp4");
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=size=320x192:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=32000:duration=3", "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var error = process.StandardError.ReadToEndAsync(_ct); await process.WaitForExitAsync(_ct); Assert.True(process.ExitCode == 0, await error);
        var tools = new ProductionMediaTools(); var f = Fixture(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        await using var file = File.OpenRead(source); var record = await store.ImportAsync(f.Project.Id, file, "source.mp4", new(), _ct);
        var shot = Ready() with { Duration = 2 }; shot.Videos = [new() { Media = record, UseSoundtrack = true }];
        var output = Path.Combine(_root, "prepared"); Directory.CreateDirectory(output); List<PreparedVideoInput> inputs = [];
        await store.PrepareAsync(f.Project.Id, shot, output, inputs, new(), _ct);
        var video = await tools.VideoInfoAsync(Path.Combine(output, inputs[0].FileName), new(), _ct);
        Assert.Equal(56, video.Frames); Assert.Equal(24, video.Fps); Assert.False(video.HasAudio); Assert.Equal((320, 192), (video.Width, video.Height));
        Assert.InRange(await tools.AudioDurationAsync(Path.Combine(output, inputs[1].FileName), new(), _ct), 56 / 24d - .001, 56 / 24d + .001);
        shot.Videos[0].UseSoundtrack = false; inputs.Clear(); await store.PrepareAsync(f.Project.Id, shot, output, inputs, new(), _ct); Assert.Single(inputs);
        await using var silent = File.OpenRead(Path.Combine(output, inputs[0].FileName));
        Assert.False((await store.ImportAsync(f.Project.Id, silent, "silent.mp4", new(), _ct)).HasAudio);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ImportAsync(f.Project.Id, new MemoryStream([1, 2, 3]), "corrupt.mp4", new(), _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => tools.PrepareReferenceVideoAsync(source, Path.Combine(output, "missing.mp4"), null, record, 56, new() { Ffmpeg = Path.Combine(_root, "missing-ffmpeg") }, _ct));
    }

    [Fact]
    public async Task PreparedVideoPreservesRotationAndDelayedSoundtrackTiming()
    {
        Directory.CreateDirectory(_root);
        async Task Ffmpeg(params string[] args)
        {
            var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) info.ArgumentList.Add(arg);
            using var p = Process.Start(info)!; var error = p.StandardError.ReadToEndAsync(_ct);
            await p.WaitForExitAsync(_ct); Assert.True(p.ExitCode == 0, await error);
        }
        var source = Path.Combine(_root, "offset.mp4"); var rotated = Path.Combine(_root, "rotated.mp4");
        await Ffmpeg("-f", "lavfi", "-i", "color=size=320x192:rate=24:duration=3", "-itsoffset", "0.5", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=32000:duration=2.5",
            "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-t", "3", source);
        await Ffmpeg("-display_rotation:v:0", "90", "-i", source, "-c", "copy", rotated);
        var f = Fixture(); var tools = new ProductionMediaTools(); var store = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        await using var file = File.OpenRead(rotated); var record = await store.ImportAsync(f.Project.Id, file, "rotated.mp4", new(), _ct);
        var video = Path.Combine(_root, "prepared.mp4"); var audio = Path.Combine(_root, "prepared.wav");
        await tools.PrepareReferenceVideoAsync(rotated, video, audio, record, 56, new(), _ct);
        var prepared = await tools.VideoInfoAsync(video, new(), _ct); Assert.Equal((192, 320), (prepared.Width, prepared.Height));
        using var reader = new BinaryReader(File.OpenRead(audio)); reader.BaseStream.Position = 12;
        byte[]? samples = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = new string(reader.ReadChars(4)); var count = reader.ReadInt32();
            if (chunk == "data") { samples = reader.ReadBytes(count); break; }
            reader.BaseStream.Position += count + (count % 2);
        }
        Assert.NotNull(samples);
        Assert.All(samples.Take(32000), b => Assert.Equal(0, b)); // First quarter second remains silent.
        Assert.Contains(samples.Skip(96000).Take(32000), b => b != 0); // Voice tone starts at its original offset.
    }

    private sealed class ReferenceMediaFake : IProductionMediaTools
    {
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult(new VideoFileInfo(160, 96, 124, 24, true));
        public Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
    }
}
