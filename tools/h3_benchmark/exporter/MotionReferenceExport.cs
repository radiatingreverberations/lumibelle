using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

internal static class MotionReferenceExport
{
    public static void CheckReceipt(string path, string name)
    {
        var root = Path.GetFullPath(path);
        var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "requests", name + ".json"))).RootElement;
        var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "results", name + ".json"))).RootElement;
        var recipe = request.GetProperty("request").Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Recipe!;
        var receipt = H3GenerationRecipes.BindReceipt(result.GetProperty("history").GetProperty("outputs"), recipe,
            request.GetProperty("seed").GetInt64(), request.GetProperty("workflow"), H3GenerationRecipes.ProbeId,
            result.GetProperty("promptId").GetString(), request.GetProperty("candidateId").GetString()!);
        Console.WriteLine($"Validated {receipt.RecipeFingerprint}: {receipt.Sigmas.Count - 1} sigma intervals, seed {receipt.Seed}");
    }
    public static async Task Run(string path, string name)
    {
        var preparationStartedUtc = DateTimeOffset.UtcNow;
        var preparationClock = System.Diagnostics.Stopwatch.StartNew();
        var root = Path.GetFullPath(path);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^(dialogue|motion|10eros|smd|fasth3)-(draft|control|final-[12]|refine)$")) throw new ArgumentException("Unknown promotion case.");
        var label = name.Split('-')[0]; var scene = label is "10eros" or "smd" or "fasth3" ? "dialogue" : label;
        var kind = name[(label.Length + 1)..];
        var output = Path.Combine(root, "requests", name + ".json");
        if (File.Exists(output)) throw new IOException("This case is already exported. Reuse its captured request.");
        var settings = JsonSerializer.Deserialize<H3Settings>(File.ReadAllText(Path.Combine(root, "h3-settings.json")), AtomicJsonFile.Options)!;
        settings = settings with { Performance = new(), LatentUpscaler = "minimax_h3_latent_upscaler_3d_fp16.safetensors" };
        var shot = new Shot { Title = scene + " promotion feasibility", Description = scene, Aspect = "16:9", Duration = 5,
            GenerationPreset = kind == "draft" ? "larry" : "standard", SaveLatents = kind == "control",
            Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" }, new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Outfit" }],
            Voices = scene == "dialogue" ? [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Subject 1", Duration = 4.248 }] : [] };
        if (scene == "dialogue") shot.Dialogue = [new() { Speaker = "Subject 1", Text = "The package is here. We can leave now.", Language = "English" }];
        VideoResolutions.Select(shot, kind.StartsWith("final") ? VideoResolution.Native : VideoResolution.Quick);
        var action = scene == "dialogue" ? "A locked medium close-up. She turns toward the camera and says, \"The package is here. We can leave now.\" Quiet room ambience, no music or cuts."
            : "A full-body shot. She walks briskly from left to right across a room, stops, turns toward the camera, and raises one arm to wave, in that order. Audible footsteps. No speech, music or cuts.";
        var prompt = $"""
            subject_definitions:
            <Subject 1> is the adult woman from <Picture 1>, wearing the outfit from <Picture 2>. {(scene == "dialogue" ? "Her voice matches <Audio 1>." : "")}
            summary:
            [reference generation] A woman in a quiet, uncluttered room.
            retention_analysis:
            Preserve her face, short auburn hair and glasses from <Picture 1>. Preserve the white, black and gold armor and boots from <Picture 2>.
            detailed_description:
            [Shot 1] One continuous camera take lasting 5.167 seconds. {action}
            overall_soundscape:
            {(scene == "dialogue" ? "Clear spoken words matching <Audio 1>. Quiet room ambience." : "Audible footsteps and quiet room ambience. No speech.")}
            non_diegetic_music:
            No music.
            """;
        var inputs = new List<AiVideoInput>();
        async Task<AiVideoInput> Input(string file, VideoInputKind type, int? index = null) {
            await using var stream = File.OpenRead(Path.Combine(root, file));
            return new(file.Replace("inputs/", ""), type is VideoInputKind.Audio or VideoInputKind.VideoSoundtrack, stream.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(stream))) { Kind = type, VideoIndex = index };
        }
        inputs.Add(await Input("inputs/face.png", VideoInputKind.Image)); inputs.Add(await Input("inputs/outfit.png", VideoInputKind.Image));
        MotionReferenceSource? origin = null;
        if (kind.StartsWith("final")) {
            var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "results", label + "-draft.json"))).RootElement;
            var clip = result.GetProperty("video").GetString()!;
            await using var source = File.OpenRead(Path.Combine(root, clip));
            var media = new ReferenceVideoMedia(Guid.NewGuid(), Convert.ToHexString(await SHA256.HashDataAsync(source)), source.Length, 608, 352, 124, 24, 124 / 24d, true);
            var binding = new ShotVideoBinding { Media = media, Name = "Draft blocking", Description = MotionReferencePolicy.DefaultInstructions, UseSoundtrack = false };
            shot.Videos.Add(binding);
            origin = new(Guid.Parse(result.GetProperty("candidateId").GetString()!), binding.Id, media, 124, 24, prompt, MotionReferencePolicy.DefaultInstructions);
            await new ProductionMediaTools().PrepareReferenceVideoAsync(Path.Combine(root, clip), Path.Combine(root, "inputs", label + "-motion.mp4"),
                null, media, 124, settings, CancellationToken.None);
            inputs.Add(await Input("inputs/" + label + "-motion.mp4", VideoInputKind.Video, 0));
            prompt = MotionReferencePolicy.Render(prompt, origin.Instructions, 1);
            shot.SaveLatents = true;
        }
        if (scene == "dialogue") inputs.Add(await Input("inputs/voice.wav", VideoInputKind.Audio));
        var size = VideoResolutions.Size(shot);
        var pictures = shot.Images.Select((b, i) => new CompositionInput(b.Id, inputs[i].Sha256)).ToArray();
        var snapshot = new VideoSnapshot(Guid.NewGuid(), 0, shot, prompt, H3Policy.Fingerprint(shot), "http://127.0.0.1:18188", settings, size.Width, size.Height, 124,
            origin is null ? ProductionPolicy.Profile : MotionReferencePolicy.Profile) {
            Production = origin is null ? new(Guid.NewGuid(), 1, "Frozen neutral comparison", new(Guid.NewGuid(), DateTimeOffset.UtcNow, prompt, "", "experiment", "experiment", Images: pictures), pictures) : null,
            MotionReference = origin, ReferenceGuidance = [], Appearances = [], OutputPolicy = new(false),
            Preset = H3Presets.Capture(shot, settings), Sampling = H3Policy.Sampling(shot, settings), Performance = H3Performance.Capture(new()), CaptureRefinementData = shot.SaveLatents
        };
        ComfyH3Video.RefineSource? refinement = null;
        TakeRefinement? refine = null;
        var retainedLatents = new List<object>();
        if (kind == "refine") {
            var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "requests", scene + "-control.json"))).RootElement;
            snapshot = control.GetProperty("request").Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot;
            var native = VideoResolutions.Size("16:9", VideoResolution.Native);
            var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "results", scene + "-control.json"))).RootElement;
            string LatentPath(string key) => Path.Combine(root, "outputs", result.GetProperty(key).GetString()!.Replace(" [output]", ""));
            var package = Path.Combine(root, "inputs", scene + "-control-refinement.safetensors");
            await RefinementPackages.ComposeAsync(LatentPath("videoLatent"), LatentPath("audioLatent"), package,
                Guid.Parse(result.GetProperty("candidateId").GetString()!), snapshot.Width, snapshot.Height, snapshot.FrameCount, CancellationToken.None);
            var identity = await RefinementPackages.InspectAsync(package, snapshot, null, CancellationToken.None);
            refine = new(identity.Id, identity, TakeRefinementMode.Refine, native.Width, native.Height, settings.LatentUpscaler, H3UpscalerImplementation.Plus);
            var video = scene + "-control-video.latent"; var audio = scene + "-control-audio.latent";
            await RefinementPackages.SplitAsync(package, Path.Combine(root, video), Path.Combine(root, audio), CancellationToken.None);
            foreach (var file in new[] { video, audio }) {
                await using var stream = File.OpenRead(Path.Combine(root, file));
                retainedLatents.Add(new { FileName = file, Bytes = stream.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)) });
            }
            refinement = new(refine, video, audio);
        }
        var id = Guid.NewGuid(); var seed = kind == "final-2" ? 20261031L : 20261030L;
        var request = new AiVideoJobRequest(2, id, snapshot, inputs) { Refinement = refine };
        request = request with { Recipe = H3GenerationRecipes.Capture(request) };
        AiVideoJobPolicy.Validate(request);
        var prepared = inputs.Select(i => new PreparedVideoInput("inputs/" + i.FileName, i.Audio) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        var workflow = ComfyH3Video.BuildWorkflow(snapshot, seed, id.ToString("D"), prepared, refine: refinement, recipe: request.Recipe);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { name, seed, candidateId = id, request, workflow, retainedLatents,
            preparationStartedUtc, capturedUtc = DateTimeOffset.UtcNow, preparationSeconds = preparationClock.Elapsed.TotalSeconds,
            expected = new { width = refine?.Width ?? snapshot.Width, height = refine?.Height ?? snapshot.Height, frames = 124, fps = 24 } }, AtomicJsonFile.Options));
        Console.WriteLine(output);
    }
}
