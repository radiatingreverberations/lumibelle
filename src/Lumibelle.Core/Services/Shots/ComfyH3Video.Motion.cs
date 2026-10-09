using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ComfyH3Video
{
    public sealed record MotionSource(TakeMotionContext Context, IReadOnlyDictionary<string, string> Files);
    public async Task CheckMotionAsync(string server, MotionContextRoute route, CancellationToken ct)
    {
        using var http = Client(server);
        using var document = JsonDocument.Parse(await http.GetStringAsync("object_info", ct));
        using var system = JsonDocument.Parse(await http.GetStringAsync("system_stats", ct));
        var version = system.RootElement.TryGetProperty("system", out var details) && details.TryGetProperty("comfyui_version", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        ValidateMotionContracts(document.RootElement, version, route);
    }
    public static void ValidateMotionContracts(JsonElement root, string? version, MotionContextRoute route)
    {
        if (!Version.TryParse(version?.Split('-')[0], out var parsed) || parsed < new Version(0, 35, 0))
            throw new WorkspaceStoreException("Motion-aware continuation requires a reported ComfyUI version of 0.35 or newer. Update the server, then refresh Video models.");
        var contracts = route == MotionContextRoute.SavedLatents
            ? new Dictionary<string, (string Output, (string Field, string Type)[] Inputs)> {
                ["LoadLatent"] = ("LATENT", [("latent", "COMBO")]), ["SetLatentNoiseMask"] = ("LATENT", [("samples", "LATENT"), ("mask", "MASK")]),
                ["LTXVConcatAVLatent"] = ("LATENT", [("video_latent", "LATENT"), ("audio_latent", "LATENT")]),
                ["SolidMask"] = ("MASK", [("value", "FLOAT"), ("width", "INT"), ("height", "INT")]),
                ["MaskToImage"] = ("IMAGE", [("mask", "MASK")]), ["RepeatImageBatch"] = ("IMAGE", [("image", "IMAGE"), ("amount", "INT")]),
                ["ImageBatch"] = ("IMAGE", [("image1", "IMAGE"), ("image2", "IMAGE")]), ["ImageToMask"] = ("MASK", [("image", "IMAGE"), ("channel", "COMBO")]),
                ["LoadImage"] = ("IMAGE", [("image", "COMBO")]) }
            : new Dictionary<string, (string Output, (string Field, string Type)[] Inputs)> {
                [StartFrameNode] = ("CONDITIONING", [("positive", "CONDITIONING"), ("latent", "LATENT"), ("frame_idx", "INT"), ("vae", "VAE"), ("audio_vae", "VAE"), ("image", "IMAGE"), ("audio", "AUDIO")]),
                ["LoadImage"] = ("IMAGE", [("image", "COMBO")]), ["ImageBatch"] = ("IMAGE", [("image1", "IMAGE"), ("image2", "IMAGE")]), ["LoadAudio"] = ("AUDIO", [("audio", "COMBO")]) };
        // Frame-guided generations also need these contracts when their full result is refined.
        foreach (var (name, contract) in new Dictionary<string, (string Output, (string Field, string Type)[] Inputs)> {
            ["SetLatentNoiseMask"] = ("LATENT", [("samples", "LATENT"), ("mask", "MASK")]),
            ["LTXVConcatAVLatent"] = ("LATENT", [("video_latent", "LATENT"), ("audio_latent", "LATENT")]),
            ["SolidMask"] = ("MASK", [("value", "FLOAT"), ("width", "INT"), ("height", "INT")]),
            ["MaskToImage"] = ("IMAGE", [("mask", "MASK")]),
            ["RepeatImageBatch"] = ("IMAGE", [("image", "IMAGE"), ("amount", "INT")]),
            ["ImageToMask"] = ("MASK", [("image", "IMAGE"), ("channel", "COMBO")]),
            ["MaskComposite"] = ("MASK", [("destination", "MASK"), ("source", "MASK"), ("x", "INT"), ("y", "INT"), ("operation", "COMBO")]) })
            contracts[name] = contract;
        foreach (var (node, contract) in contracts) {
            foreach (var (field, type) in contract.Inputs) {
                var input = Input(root, node, field);
                if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0 ||
                    (type == "COMBO" ? input[0].ValueKind != JsonValueKind.Array && !(input[0].ValueKind == JsonValueKind.String && input[0].GetString() == "COMBO") : input[0].ValueKind != JsonValueKind.String || input[0].GetString() != type))
                    throw new WorkspaceStoreException($"Update ComfyUI: motion-aware continuation requires the stock {node}.{field} ({type}) contract.");
            }
            if (!root.TryGetProperty(node, out var definition) || !definition.TryGetProperty("output", out var outputs) || outputs.ValueKind != JsonValueKind.Array || outputs.GetArrayLength() == 0 || outputs[0].ValueKind != JsonValueKind.String || outputs[0].GetString() != contract.Output)
                throw new WorkspaceStoreException($"Update ComfyUI: motion-aware continuation requires the stock {node} {contract.Output} output.");
        }
        if (!Options(root, "ImageToMask", "channel").Contains("red"))
            throw new WorkspaceStoreException("Update ComfyUI: ImageToMask must support the red channel for motion masks.");
        if (!Options(root, "MaskComposite", "operation").Contains("multiply"))
            throw new WorkspaceStoreException("Update ComfyUI: MaskComposite must support multiply for motion masks.");
    }
    public async Task<MotionSource?> UploadMotionAsync(VideoSnapshot snapshot, string directory, CancellationToken ct)
    {
        if (snapshot.Motion is not { } motion) return null;
        await CheckMotionAsync(snapshot.ExecutionComfyUrl, motion.Route, ct);
        using var http = Client(snapshot.ExecutionComfyUrl);
        var files = new Dictionary<string, string>();
        foreach (var file in motion.Files) {
            var path = Path.Combine(directory, "inputs", file.FileName);
            await RefinementPackages.VerifyFileAsync(path, file.Bytes, file.Sha256, ct);
            await using var stream = File.OpenRead(path);
            using var body = new MultipartFormDataContent();
            body.Add(new StreamContent(stream), "image", "lumibelle-" + await UploadNameAsync(stream, file.FileName, ct));
            body.Add(new StringContent("input"), "type"); body.Add(new StringContent("true"), "overwrite");
            using var response = await http.PostAsync("upload/image", body, ct); response.EnsureSuccessStatusCode();
            using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var name = receipt.RootElement.GetProperty("name").GetString()!;
            var sub = receipt.RootElement.TryGetProperty("subfolder", out var f) ? f.GetString() : "";
            if (!string.IsNullOrEmpty(sub)) throw new WorkspaceStoreException("The motion input was not stored where stock LoadLatent can read it.");
            files.Add(file.FileName, name);
        }
        return new(motion, files);
    }
    private static object ApplyMotion(MotionSource motion, int latentPort, Action<string, string, object> node, out string guided)
    {
        object Link(string id, int port = 0) => new object[] { id, port };
        guided = "5";
        if (motion.Context.Route != MotionContextRoute.SavedLatents) {
            string? batch = null;
            foreach (var frame in motion.Files.Keys.Where(f => f.StartsWith("motion-frame-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
                var id = "motion-" + frame;
                node(id, "LoadImage", new { image = motion.Files[frame] });
                if (batch is null) batch = id;
                else { node(id + "-batch", "ImageBatch", new { image1 = Link(batch), image2 = Link(id) }); batch = id + "-batch"; }
            }
            var values = new Dictionary<string, object> { ["positive"] = Link("5"), ["latent"] = Link("5", latentPort), ["frame_idx"] = motion.Context.Direction == TakeExtensionDirection.Before ? motion.Context.GenerationFrames - motion.Context.Frames : 0, ["vae"] = Link("3"), ["image"] = Link(batch!) };
            if (motion.Files.TryGetValue("motion-audio.wav", out var audio)) { node("motion-audio", "LoadAudio", new { audio }); values["audio"] = Link("motion-audio"); values["audio_vae"] = Link("4"); }
            node("motion-guide", StartFrameNode, values); guided = "motion-guide";
            return Link("5", latentPort);
        }
        node("motion-video", "LoadLatent", new { latent = motion.Files["motion-video.latent"] });
        node("motion-audio", "LoadLatent", new { latent = motion.Files["motion-audio.latent"] });
        AddMotionMasks(motion, node);
        node("motion-video-masked", "SetLatentNoiseMask", new { samples = Link("motion-video"), mask = Link("motion-video-mask") });
        node("motion-audio-masked", "SetLatentNoiseMask", new { samples = Link("motion-audio"), mask = Link("motion-audio-mask") });
        node("motion-av", "LTXVConcatAVLatent", new { video_latent = Link("motion-video-masked"), audio_latent = Link("motion-audio-masked") });
        return Link("motion-av");
    }
    private static void AddMotionMasks(MotionSource motion, Action<string, string, object> node)
    {
        object Link(string id) => new object[] { id, 0 };
        // Build the temporal batch in the graph: image codecs may collapse duplicate animation frames.
        foreach (var part in new[] { ("held", 0.0, H3Motion.VideoSteps(motion.Context.Frames)), ("new", 1.0, H3Motion.VideoSteps(motion.Context.GenerationFrames) - H3Motion.VideoSteps(motion.Context.Frames)) }) {
            node("motion-" + part.Item1, "SolidMask", new { value = part.Item2, width = 1, height = 1 });
            node("motion-" + part.Item1 + "-image", "MaskToImage", new { mask = Link("motion-" + part.Item1) });
            node("motion-" + part.Item1 + "-batch", "RepeatImageBatch", new { image = Link("motion-" + part.Item1 + "-image"), amount = part.Item3 });
        }
        var leading = motion.Context.Direction == TakeExtensionDirection.Before;
        node("motion-mask-batch", "ImageBatch", new { image1 = Link(leading ? "motion-new-batch" : "motion-held-batch"), image2 = Link(leading ? "motion-held-batch" : "motion-new-batch") });
        node("motion-video-mask", "ImageToMask", new { image = Link("motion-mask-batch"), channel = "red" });
        if (motion.Context.Route == MotionContextRoute.SavedLatents) {
            node("motion-audio-mask-image", "LoadImage", new { image = motion.Files["motion-audio-mask.png"] });
            node("motion-audio-mask", "ImageToMask", new { image = Link("motion-audio-mask-image"), channel = "red" });
        } else {
            node("motion-audio-new", "SolidMask", new { value = 1.0, width = H3Motion.AudioBoundary(motion.Context.GenerationFrames), height = 2 });
            node("motion-audio-held", "SolidMask", new { value = 0.0, width = H3Motion.AudioBoundary(motion.Context.Frames), height = 2 });
            node("motion-audio-mask", "MaskComposite", new { destination = Link("motion-audio-new"), source = Link("motion-audio-held"), x = leading ? H3Motion.AudioBoundary(motion.Context.GenerationFrames) - H3Motion.AudioBoundary(motion.Context.Frames) : 0, y = 0, operation = "multiply" });
        }
    }
}
