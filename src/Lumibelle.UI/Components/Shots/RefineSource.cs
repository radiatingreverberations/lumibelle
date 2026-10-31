using lumibelle.Models;

namespace lumibelle.Components.Shots;

// A clip offered for Refine from video, described before its draft is prepared.
public sealed record RefineSource(RefinementSourceKind Kind, Guid Id, string Name, string Thumbnail, double Seconds, int Width, int Height, bool Edited = false)
{
    public static RefineSource From(Guid project, ShotTake take, string name) =>
        new(RefinementSourceKind.Take, take.Id, name, $"/media/projects/{project}/takes/{take.Id}/frames/0", take.FrameCount / take.Fps, take.Width, take.Height,
            take.Trim is not null || take.Composition is not null);
    public static RefineSource From(Guid project, AssetReferenceReel reel) =>
        new(RefinementSourceKind.Reel, reel.Id, reel.Name, lumibelle.Services.Production.ReferenceVideos.ThumbnailUrl(project, reel.Media.Id), reel.Media.Duration, reel.Media.Width, reel.Media.Height);
}
