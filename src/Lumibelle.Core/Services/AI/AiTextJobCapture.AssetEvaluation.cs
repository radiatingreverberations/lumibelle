using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> EvaluateAssetImagesAsync(Guid id, Guid tab, Guid project, Guid batch,
        AssetBatchItem item, TextModelReference model, AiSettings configured, CancellationToken ct = default)
    {
        configured = Copy(configured); model = Copy(model); item = Copy(item);
        if (!TextVisionPolicy.SupportsBackend(model.Backend)) throw new AiGenerationException(TextVisionPolicy.SetupHint);
        var check = await (providers ?? throw new AiGenerationException("Model availability is unavailable.")).CheckAsync(model.Backend, configured, cancellationToken: ct);
        if (TextModelPolicy.Issue(model, configured, check, requireTest: true) is { } issue) throw new AiGenerationException(issue);
        if (TextModelPolicy.CatalogModel(model, check)?.SupportsImages != true) throw new AiGenerationException(TextVisionPolicy.SetupHint);
        var (request, sheet) = await AssetImageEvaluator.CaptureAsync(assets, project, batch, item, ct);
        var submission = await BuildAsync(id, tab, AiJobKind.AssetImageEvaluation, new(project, item.Context.AssetId),
            item.Context.AssetName + " · Evaluate candidates", request, model, false, configured, AssetImageEvaluator.Profile,
            AssetImageEvaluator.Messages(request, sheet), .2f, null, ct);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        // Capacity fitting may resize images. Bind the review to the actual submitted pixels.
        request = request with { ContactSheetHash = AssetImageEvaluator.Hash(snapshot.Messages.SelectMany(m => m.Parts).Single(p => p.Image is not null).Image!) };
        return submission with { Snapshot = JsonSerializer.SerializeToElement(snapshot with { Task = JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options) }, AtomicJsonFile.Options) };
    }
}
