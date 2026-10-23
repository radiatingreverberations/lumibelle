using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Services.AI;

public sealed record ScriptReferenceImage(string Label, string Details, byte[] Png);

public static class ScriptReferences
{
    public const int MaximumImages = 9;

    public static async Task<IReadOnlyList<ScriptReferenceImage>> CaptureAsync(IAssetStore assets, Guid project,
        IReadOnlyList<AssetImageReference> references, CancellationToken ct = default)
    {
        if (references.Count == 0) return [];
        if (references.Count > MaximumImages || references.Distinct().Count() != references.Count)
            throw new AiGenerationException($"Choose up to {MaximumImages} distinct writing reference images.");
        var library = await assets.LoadAsync(project, ct);
        var result = new List<ScriptReferenceImage>();
        foreach (var reference in references)
        {
            var asset = library.Assets.FirstOrDefault(a => a.Id == reference.AssetId);
            var image = asset?.Images.FirstOrDefault(i => i.Id == reference.ImageId);
            if (asset is null || image is null || image.LookId is { } lookId && !asset.Looks.Any(l => l.Id == lookId && !l.Archived))
                throw new AiGenerationException("A writing reference is unavailable or in Trash. Remove it or choose another image.");
            await using var media = await assets.OpenImageAsync(project, reference.AssetId, reference.ImageId, ct);
            if (media is null) throw new AiGenerationException("A writing reference image is missing. Remove it or choose another image.");
            result.Add(new($"Image {result.Count + 1} · {Label(asset, image)}", Description(asset, image),
                await ComfyReferenceImageEditor.PrepareSourcePngAsync(media.Content, null, ct)));
        }
        return result;
    }

    public static string Description(ReferenceAsset asset, AssetImage image)
    {
        var look = asset.Looks.FirstOrDefault(l => l.Id == image.LookId);
        return string.Join("\n", new[] { $"Asset: {asset.Name}", $"Category: {asset.Category}", asset.Description, asset.PreservationGuidance,
            look?.Name, look?.Description, look?.PreservationGuidance, image.PreservationGuidance }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public static string Label(ReferenceAsset asset, AssetImage image) => !string.IsNullOrWhiteSpace(image.Name) ? image.Name :
        image.Tags.Count > 0 ? string.Join(", ", image.Tags) : asset.Name;
}
