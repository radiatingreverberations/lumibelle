using System.Runtime.CompilerServices;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed partial class ComfyCaptionImages
{
    public IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request, AiSettings settings, CancellationToken ct = default) =>
        RunAsync(settings.DefaultImageWorkflow, request.Prompt, request.AspectRatio, request.Count, request.Seed, request.Resolution, request.Loras, null, [], settings, ct);
    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources, AiSettings settings, CancellationToken ct = default) =>
        RunAsync(settings.DefaultImageWorkflow, request.Prompt, request.AspectRatio, request.Count, request.Seed, request.Resolution, request.Loras, request, sources.ToArray(), settings, ct);

    private async IAsyncEnumerable<ReferenceGenerationUpdate> RunAsync(ImageWorkflow workflow, string prompt, string aspect, int count, long? fixedSeed, int resolution,
        IReadOnlyList<LoraSelection> selections, ReferenceEditRequest? edit, IReadOnlyList<ReferenceImageSource> sources, AiSettings settings, [EnumeratorCancellation] CancellationToken ct)
    {
        selections = LoraPolicy.Capture(selections); settings = CaptionImagePolicy.Capture(settings, workflow); FileAiSettingsStore.Validate(settings);
        if (string.IsNullOrWhiteSpace(prompt) || count is < 1 or > 4 || fixedSeed is < 0 || fixedSeed > long.MaxValue - count + 1 || !ImageWorkflowLimits.SupportsResolution(workflow, resolution))
            throw new AiGenerationException("Describe the result and choose one to four candidates with a supported resolution and seed.");
        if (edit?.Regions is { Count: > 0 }) throw new AiGenerationException("Regional edits require the queued review workflow.");
        if (edit is not null) edit = ReferenceEditInputs.Capture(edit, sources, CaptionImagePolicy.MaximumFiboReferences);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(settings.ImageTimeoutSeconds));
        var check = await CheckAsync(settings, workflow, edit is not null, timeout.Token); if (!check.Success) throw new AiGenerationException(check.Message);
        var loras = await LoraPolicy.PrepareAsync(selections, settings, workflow, clients, timeout.Token);
        var pngs = new List<byte[]>();
        foreach (var source in sources) pngs.Add(await NativeImageInputs.PrepareAsync(source.Content, ReferenceEditInputs.CropFor(edit!, source), timeout.Token, workflow.Label()));
        var size = ImageAspectPolicy.Size(aspect, pngs.FirstOrDefault(), resolution); var encoded = pngs.Select(Convert.ToBase64String).ToArray();
        using var http = Client(settings);
        for (var candidate = 1; candidate <= count; candidate++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var seed = fixedSeed is null ? Random.Shared.NextInt64(1, long.MaxValue) : fixedSeed.Value + candidate - 1;
            var started = clock.GetUtcNow(); ComfyReferenceImageGenerator.ComfyOutput? output = null;
            await foreach (var update in monitor.ExecuteAsync(http,
                id => BuildWorkflow(settings, workflow, prompt.Trim(), seed, size.Width, size.Height, encoded, id, loras), ExecutionOptions, timeout.Token, ct))
            {
                yield return new(update.Progress.Label, candidate, count, Progress: update.Progress);
                if (update.Complete && update.Job is { } job) output = ComfyReferenceImageGenerator.ReadOutput(job, OutputNode);
            }
            if (output is null) throw new AiGenerationException("ComfyUI completed without an image output.");
            var image = await ComfyReferenceImageGenerator.DownloadHandledAsync(http, output, timeout, ct);
            var actual = ImageInspector.Inspect(image);
            if ((actual.Width, actual.Height) != size) throw new AiGenerationException("The image output dimensions differ from the captured canvas.");
            var metadata = CaptionImagePolicy.Metadata(settings, workflow, prompt, aspect, resolution, size, edit,
                sources.Select(s => new AssetImageReference(s.AssetId, s.ImageId)).ToArray(), loras, seed);
            yield return new("Saving candidate…", candidate, count, image, output.FileName, metadata,
                new(GenerationPhase.Saving, "Saving candidate…", Elapsed: clock.GetUtcNow() - started));
        }
    }
}
