using lumibelle.Models;
using lumibelle.Services.Shots;

namespace lumibelle.Services.Review;

/// <summary>
/// Where a reference image is used: the shots that select it now, the takes that were rendered with it, and the
/// takes that took a shot frame from one of those. The last group is how a corrected picture's old content
/// survives in shots that no longer select the old picture.
/// </summary>
public static class ReferenceUsage
{
    public static IReadOnlyList<ReferenceUse> Find(ProjectReviewSnapshot snapshot, Guid assetId, Guid imageId)
    {
        bool Uses(Shot shot) => shot.Images.Any(i => i.Kind == ShotImageKind.AssetImage && i.AssetId == assetId && i.MediaId == imageId);
        var uses = new List<ReferenceUse>();
        foreach (var row in snapshot.Rows.Where(r => Uses(r.Shot)))
            uses.Add(new("Shot", row.Id, row.Number, null, "Selected as a reference picture."));
        foreach (var reel in snapshot.Assets.Reels.Where(r => r.Generation?.Snapshot.Shot is { } shot && Uses(shot)))
            uses.Add(new("Reel", null, null, null, $"The reel “{reel.Name}” was generated with it."));

        // Takes rendered with the picture, then takes that took a frame from any take already found.
        var tainted = new Dictionary<Guid, string>();
        foreach (var take in snapshot.Shots.Takes.Where(t => Uses(t.Snapshot.Shot))) tainted[take.Id] = "Rendered with it as a reference picture.";
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var take in snapshot.Shots.Takes.Where(t => !tainted.ContainsKey(t.Id)))
            {
                var rendered = take.Snapshot.Shot;
                var source = rendered.ShotFrames.Select(f => f.TakeId).Append(rendered.StartFrame?.TakeId ?? Guid.Empty).FirstOrDefault(tainted.ContainsKey);
                if (source == Guid.Empty) continue;
                var from = snapshot.Shots.Takes.First(t => t.Id == source);
                tainted[take.Id] = $"Rendered with a frame from {TakeDisplay.Label(from)} of shot #{snapshot.Row(from.ShotId)?.Number}, which carries it.";
                grew = true;
            }
        }
        foreach (var take in snapshot.Shots.Takes.Where(t => tainted.ContainsKey(t.Id)))
        {
            var row = snapshot.Row(take.ShotId);
            uses.Add(new(row?.Coverage.SelectedTakeId == take.Id ? "Selected take" : "Take", take.ShotId, row?.Number, take.Id, tainted[take.Id]));
        }
        return uses;
    }
}
