using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class VideoResolutions
{
    public static IReadOnlyList<VideoResolution> Choices { get; } = Array.AsReadOnly(Enum.GetValues<VideoResolution>());
    public static VideoResolution Selected(ReferenceReelDraft recipe) => recipe.Resolution ?? (recipe.NativeResolution ? VideoResolution.Native : VideoResolution.Preview);
    public static VideoResolution Selected(Shot shot) => shot.Resolution ?? (shot.NativeResolution ? VideoResolution.Native : VideoResolution.Preview);
    public static void Select(Shot shot, VideoResolution resolution)
    {
        if (!Enum.IsDefined(resolution)) throw new WorkspaceStoreException("Choose a supported video resolution.");
        shot.NativeResolution = resolution == VideoResolution.Native;
        shot.Resolution = resolution is VideoResolution.Preview or VideoResolution.Native ? null : resolution;
        shot.UpscalePreview = false;
    }
    public static (int Width, int Height) Size(Shot shot) => Size(shot.Aspect, Selected(shot));
    public static string Key(VideoResolution resolution) => resolution.ToString().ToLowerInvariant();
    public static bool TryParse(string? key, out VideoResolution resolution) => Enum.TryParse(key, true, out resolution) && Enum.IsDefined(resolution);
    public static void Select(ReferenceReelDraft recipe, VideoResolution resolution)
    {
        if (!Enum.IsDefined(resolution)) throw new WorkspaceStoreException("Choose a supported reel resolution.");
        recipe.NativeResolution = resolution == VideoResolution.Native;
        // Keep the existing preview/native representation and historical fingerprints.
        recipe.Resolution = resolution is VideoResolution.Preview or VideoResolution.Native ? null : resolution;
    }
    public static (int Width, int Height) Size(ReferenceReelDraft recipe) => Size(recipe.Aspect, Selected(recipe));
    public static (int Width, int Height) Size(string aspect, VideoResolution resolution) => (aspect, resolution) switch
    {
        (_, VideoResolution.Preview) => H3Policy.Size(aspect, false),
        (_, VideoResolution.Native) => H3Policy.Size(aspect, true),
        ("16:9", VideoResolution.Quick) => (608, 352), ("9:16", VideoResolution.Quick) => (352, 608), ("1:1", VideoResolution.Quick) => (448, 448),
        ("16:9", VideoResolution.Detail) => (1120, 640), ("9:16", VideoResolution.Detail) => (640, 1120), ("1:1", VideoResolution.Detail) => (832, 832),
        ("16:9", VideoResolution.FullHd) => (1920, 1088), ("9:16", VideoResolution.FullHd) => (1088, 1920), ("1:1", VideoResolution.FullHd) => (1440, 1440),
        ("16:9", VideoResolution.Qhd) => (2560, 1440), ("9:16", VideoResolution.Qhd) => (1440, 2560), ("1:1", VideoResolution.Qhd) => (1920, 1920),
        _ => throw new WorkspaceStoreException("Choose a supported reel resolution and landscape, portrait, or square aspect.")
    };
    public static string Label(VideoResolution resolution) => resolution switch
    {
        VideoResolution.Quick => "Quick · 0.2 MP", VideoResolution.Preview => "Preview · 0.4 MP",
        VideoResolution.Detail => "Detail · 0.7 MP", VideoResolution.Native => "Native · 1 MP",
        VideoResolution.FullHd => "1080p-class · 2.1 MP", VideoResolution.Qhd => "1440p-class · 3.7 MP",
        _ => throw new WorkspaceStoreException("Choose a supported reel resolution.")
    };
    public static string Option(string aspect, VideoResolution resolution)
    {
        var size = Size(aspect, resolution);
        return $"{Label(resolution)} · {size.Width} × {size.Height}";
    }
    public static string Fingerprint(ReferenceReelDraft recipe)
    {
        var fingerprint = H3Policy.Fingerprint(ReferenceReels.Inputs(recipe));
        if (Selected(recipe) is VideoResolution.Preview or VideoResolution.Native) return fingerprint;
        var size = Size(recipe);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fingerprint}|{size.Width}x{size.Height}")));
    }
    public static string Badge(AssetReferenceReel reel)
    {
        var media = reel.Media; var mp = Badge(media);
        // Use the saved media's actual dimensions, including imported and older reels.
        if (reel.Generation is { } generated && Selected(generated.Recipe) == VideoResolution.Native &&
            (media.Width, media.Height) == Size(generated.Recipe)) return "Native · " + mp;
        return mp;
    }
    public static string Badge(ReferenceVideoMedia media) =>
        ((long)media.Width * media.Height / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MP";
    public static string Dimensions(AssetReferenceReel reel) => $"{reel.Media.Width} × {reel.Media.Height}";
}
