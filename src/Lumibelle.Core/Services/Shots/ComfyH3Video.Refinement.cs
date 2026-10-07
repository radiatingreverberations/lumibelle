using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

// Take refinement with stock ComfyUI nodes: each take keeps its latents through SaveLatent, and a
// refinement loads them back with LoadLatent. Enlarging the video needs the learned 3D upscaler.
public sealed partial class ComfyH3Video
{
    private static (bool Capture, string? Issue, string[] Models) InspectRefinement(JsonElement root, string? standardIssue, H3PreviewUpscaleCapability upscaling)
    {
        bool Type(string node, string field, string type) => Input(root, node, field) is { ValueKind: JsonValueKind.Array } input &&
            input.GetArrayLength() > 0 && input[0].ValueKind == JsonValueKind.String && input[0].GetString() == type;
        var capture = Type("SaveLatent", "samples", "LATENT") && Type("SaveLatent", "filename_prefix", "STRING") &&
            Type("LTXVSeparateAVLatent", "av_latent", "LATENT") && Type("LTXVConcatAVLatent", "video_latent", "LATENT") && Type("LTXVConcatAVLatent", "audio_latent", "LATENT");
        var load = Input(root, "LoadLatent", "latent").ValueKind == JsonValueKind.Array;
        var issue = !capture || !load ? "Update ComfyUI for its built-in SaveLatent, LoadLatent and audio/video latent nodes, then check again."
            : standardIssue ?? upscaling.Issue;
        return (capture, issue, Options(root, H3PreviewUpscaling.Node, "model_name"));
    }

    // Splits the package into the two .latent files LoadLatent reads and uploads them to ComfyUI's input folder.
    // LoadLatent lists only that folder's top level, so the files are named by content there.
    public async Task<(string Video, string Audio)> UploadRefinementAsync(string server, string package, string scratch, CancellationToken ct)
    {
        Directory.CreateDirectory(scratch);
        var video = Path.Combine(scratch, "video.latent"); var audio = Path.Combine(scratch, "audio.latent");
        try
        {
            await RefinementPackages.SplitAsync(package, video, audio, ct);
            using var http = Client(server);
            async Task<string> Upload(string path)
            {
                await using var stream = File.OpenRead(path);
                using var body = new MultipartFormDataContent(); body.Add(new StreamContent(stream), "image", "lumibelle-" + await UploadNameAsync(stream, path, ct));
                body.Add(new StringContent("input"), "type"); body.Add(new StringContent("true"), "overwrite");
                using var response = await http.PostAsync("upload/image", body, ct); response.EnsureSuccessStatusCode();
                using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var name = receipt.RootElement.GetProperty("name").GetString();
                var sub = receipt.RootElement.TryGetProperty("subfolder", out var f) ? f.GetString() : "";
                if (string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(sub)) throw new WorkspaceStoreException("ComfyUI stored the refinement data somewhere LoadLatent can't read.");
                return name;
            }
            return (await Upload(video), await Upload(audio));
        }
        finally { foreach (var file in new[] { video, audio }) if (File.Exists(file)) File.Delete(file); }
    }

    private static async Task<H3RefinementPackage> DownloadPackageAsync(HttpClient http, JsonElement outputs, VideoRun run, Guid id, string directory, Func<string, Task> progress, CancellationToken ct)
    {
        var path = Path.Combine(directory, H3RefinementPackage.FileName);
        if (File.Exists(path))
        {
            try
            {
                var previous = await RefinementPackages.InspectAsync(path, run.Snapshot, run.Refinement, ct);
                if (previous.Id == id) return previous;
            }
            catch (WorkspaceStoreException) { }
            File.Delete(path);
        }
        await progress("Downloading refinement data…");
        async Task<string> Fetch(string node, string name)
        {
            var latents = outputs.GetProperty(node).GetProperty("latents");
            if (latents.GetArrayLength() != 1) throw new WorkspaceStoreException("ComfyUI returned no refinement data for this take.");
            var target = Path.Combine(directory, name);
            using var response = await http.GetAsync(ViewUrl(latents[0]), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > RefinementPackages.MaximumBytes) throw new WorkspaceStoreException("The refinement data exceeds 8 GB.");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(target);
            var buffer = new byte[1024 * 1024]; int count; long total = 0;
            while ((count = await source.ReadAsync(buffer, ct)) != 0)
            {
                total += count; if (total > RefinementPackages.MaximumBytes) throw new WorkspaceStoreException("The refinement data exceeds 8 GB.");
                await file.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            return target;
        }
        var video = await Fetch("21", "latent-video.tmp"); var audio = await Fetch("22", "latent-audio.tmp");
        var (width, height) = H3PreviewUpscaling.OutputSize(run.Snapshot, run.Refinement);
        try { await RefinementPackages.ComposeAsync(video, audio, path + ".tmp", id, width, height, run.Snapshot.FrameCount, ct); }
        finally { File.Delete(video); File.Delete(audio); }
        var package = await RefinementPackages.InspectAsync(path + ".tmp", run.Snapshot, run.Refinement, ct);
        File.Move(path + ".tmp", path, true);
        return package;
    }
}
