using lumibelle.Models;

namespace lumibelle.Services.Assets;

public static class FrameCropText
{
    // A saved frame crop as people check it: the kept pixels, where they start, and the full frame they came from.
    public static string Describe(int frameWidth, int frameHeight, ImageCropRegion crop) =>
        ImageGeometry.CropPixels(frameWidth, frameHeight, crop) is var r ? $"{r.Width} × {r.Height} at {r.X}, {r.Y} of the {frameWidth} × {frameHeight} frame" : "";
}

public enum AssetMediaKind { Image, Reel, Voice }
public enum AssetCreationKind { Image, Reel }
public sealed record AssetMediaSelection(AssetMediaKind Kind, Guid Id);
public sealed class AssetsPresentation
{
    public AssetMediaSelection? Selection { get; set; }
    public AssetCreationKind Creation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Edit => Selection is not null;
    public string Filter { get; set; } = "All";
    public string Search { get; set; } = "";
    public string LookFilter { get; set; } = "all";
    public bool Multiple { get; set; }
    public bool GroupByLook { get; set; } = true;
}

public sealed record AssetGalleryItem(AssetMediaSelection Key, string Name, DateTimeOffset CreatedUtc,
    Guid? LookId, AssetImage? Image = null, AssetReferenceReel? Reel = null, VoiceReference? Voice = null)
{
    public static IEnumerable<AssetGalleryItem> For(ReferenceAsset asset, AssetLibrary library) =>
        asset.Images.Select(i => new AssetGalleryItem(new(AssetMediaKind.Image, i.Id), i.Name ?? (i.Tags.Count > 0 ? string.Join(", ", i.Tags) : asset.Name), i.CreatedUtc, i.LookId, Image: i))
        .Concat(library.Reels.Where(r => r.AssetId == asset.Id).Select(r => new AssetGalleryItem(new(AssetMediaKind.Reel, r.Id), r.Name, r.CreatedUtc, r.LookId, Reel: r)))
        .Concat(library.Voices.Where(v => v.AssetId == asset.Id).Select(v => new AssetGalleryItem(new(AssetMediaKind.Voice, v.Id), v.Name, v.CreatedUtc, null, Voice: v)))
        .OrderByDescending(i => i.CreatedUtc).ThenBy(i => i.Key.Kind).ThenBy(i => i.Key.Id);
}
