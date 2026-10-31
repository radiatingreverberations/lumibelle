using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

// Manually invoked benchmark utility; never included in CI or automated test suites.
// Export application-built API graphs only. Never enqueue or update a project/settings.
if (args.Length == 2 && args[0] == "--acceleration") { await AccelerationExport.Run(args[1]); return; }
if (args.Length == 3 && args[0] == "--motion-reference") { await MotionReferenceExport.Run(args[1], args[2]); return; }
if (args.Length == 3 && args[0] == "--motion-receipt-check") { MotionReferenceExport.CheckReceipt(args[1], args[2]); return; }
var directory = Path.GetFullPath(args.Single());
var settings = JsonSerializer.Deserialize<H3Settings>(File.ReadAllText(Path.Combine(directory, "h3-settings.json")), AtomicJsonFile.Options)!;
settings.LatentUpscaler = "minimax_h3_latent_upscaler_3d_fp16.safetensors";
var reference = Path.Combine(directory, "app-preview-reference.png");
var prompt = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "results/standard-04/manifest.json"))).RootElement.GetProperty("prompt").GetString()!;
var requests = Path.Combine(directory, "app-preview");
var cases = new List<(string Name, string Aspect, int Steps, bool Upscale, int Frames)>();
foreach (var steps in new[] { 20, 4, 8 }) foreach (var upscale in new[] { false, true })
    cases.Add(($"app-{steps}-{(upscale ? "upscaled" : "preview")}", "16:9", steps, upscale, 73));
cases.Add(("app-8-long", "16:9", 8, true, 141));
cases.Add(("app-8-portrait", "9:16", 8, true, 73));
cases.Add(("app-8-square", "1:1", 8, true, 73));
// Check every destination before changing the reference shared by the exported graphs.
foreach (var destination in cases.Select(test => Path.Combine(requests, test.Name + ".json")).Prepend(reference))
    if (Path.Exists(destination)) throw new IOException("Refusing to overwrite benchmark export " + destination);
Directory.CreateDirectory(requests);
using (var image = await Image.LoadAsync(Path.Combine(directory, "reference.jpg")))
await using (var destination = new FileStream(reference, FileMode.CreateNew, FileAccess.Write))
{
    image.Mutate(context => context.AutoOrient());
    await image.SaveAsPngAsync(destination, new PngEncoder { SkipMetadata = true });
}
foreach (var test in cases)
{
    var file = Path.Combine(requests, test.Name + ".json");
    var shot = new Shot { Title = "H3 preview upscaling QA", Description = "Frozen benchmark reference and head turn", Aspect = test.Aspect,
        Duration = test.Frames / 24d - 1e-9, ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid(), UpscalePreview = test.Upscale,
        Turbo = test.Steps != 20, TurboSteps = test.Steps == 8 ? 8 : 4,
        Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "QA woman" }] };
    var size = H3Policy.Size(test.Aspect, false);
    var capturedPrompt = prompt.Replace("3.042 seconds", (test.Frames / 24d).ToString("0.000", CultureInfo.InvariantCulture) + " seconds");
    var s = new VideoSnapshot(Guid.NewGuid(), 0, shot, capturedPrompt, H3Policy.Fingerprint(shot), "http://isolated.invalid", settings,
        size.Width, size.Height, test.Frames, H3Policy.Profile) { Sampling = H3Policy.Sampling(shot, settings), Performance = H3Performance.Capture(settings.Performance),
        PreviewUpscale = test.Upscale ? H3PreviewUpscaling.Capture(H3UpscalerImplementation.Plus, settings.LatentUpscaler, test.Aspect) : null };
    var client = Guid.NewGuid().ToString("D"); var graph = ComfyH3Video.BuildWorkflow(s, 20260910, client, [new(Path.GetFileName(reference), false)]);
    var output = H3PreviewUpscaling.OutputSize(s);
    await using var destination = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
    await JsonSerializer.SerializeAsync(destination, new { name = test.Name, snapshot = s, workflow = graph,
        expected = new { width = output.Width, height = output.Height, frames = test.Frames, fps = 24 }, seed = 20260910,
        assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(ComfyH3Video).Assembly.Location))) }, AtomicJsonFile.Options);
    Console.WriteLine(test.Name);
}
