using lumibelle.Models;
using lumibelle.Services.Shots;

namespace lumibelle.Services.Review;

/// <summary>
/// Where a reference image is used: the shots that select it now, the takes that were rendered with it, and the
/// takes that took a shot frame from one of those, and reels generated from any of them with the takes that use those reels.
/// The later groups are how a corrected picture's old content survives in shots that no longer select the old picture.
/// </summary>
public static class ReferenceUsage
{
    public static IReadOnlyList<ReferenceUse> Find(ProjectReviewSnapshot snapshot, Guid assetId, Guid imageId)
    {
        bool Uses(Shot shot) => shot.Images.Any(i => i.Kind == ShotImageKind.AssetImage && i.AssetId == assetId && i.MediaId == imageId);
        var uses = new List<ReferenceUse>();
        // Takes in Trash are followed too, so a take rendered from a trashed one is still found; only takes outside Trash are reported.
        var trashed = snapshot.Shots.Trash.Select(e => e.Take).OfType<ShotTake>().Where(t => snapshot.Shots.Takes.All(x => x.Id != t.Id)).ToArray();
        var all = snapshot.Shots.Takes.Concat(trashed).ToArray();
        // What carries the picture, found together until nothing more is: takes rendered with it, with a frame of a take that
        // carries it, or with a reel that carries it; and reels generated from a shot that did any of these.
        var tainted = new Dictionary<Guid, string>();
        var reels = new Dictionary<Guid, (AssetReferenceReel Reel, string Why)>();
        var reelNames = new Dictionary<Guid, string>();
        string? Carries(Shot shot)
        {
            if (Uses(shot)) return "with it as a reference picture";
            if (shot.Videos.Select(v => v.Media?.Id).OfType<Guid>().Select(id => reelNames.GetValueOrDefault(id)).FirstOrDefault(name => name is not null) is { } reel)
                return $"with the reel “{reel}”, which carries it";
            var source = shot.ShotFrames.Select(f => f.TakeId).Append(shot.StartFrame?.TakeId ?? Guid.Empty).FirstOrDefault(tainted.ContainsKey);
            if (source == Guid.Empty) return null;
            var from = all.First(t => t.Id == source);
            return $"with a frame from {TakeDisplay.Label(from)} of shot #{snapshot.Row(from.ShotId)?.Number}{(trashed.Contains(from) ? ", now in Trash," : ",")} which carries it";
        }
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var take in all.Where(t => !tainted.ContainsKey(t.Id)))
                if (Carries(take.Snapshot.Shot) is { } why) { tainted[take.Id] = $"Rendered {why}."; grew = true; }
            foreach (var reel in snapshot.Assets.Reels.Where(r => !reels.ContainsKey(r.Id) && r.Generation?.Snapshot.Shot is not null))
                if (Carries(reel.Generation!.Snapshot.Shot) is { } why)
                {
                    reels[reel.Id] = (reel, why);
                    if (reel.Media is not null) reelNames.TryAdd(reel.Media.Id, reel.Name);
                    grew = true;
                }
        }

        foreach (var row in snapshot.Rows)
            if (Uses(row.Shot)) uses.Add(new("Shot", row.Id, row.Number, null, "Selected as a reference picture."));
            // A shot not rendered yet carries the picture into its next take through a reel or a frame that carries it.
            else if (Carries(row.Shot) is { } why) uses.Add(new("Shot", row.Id, row.Number, null, $"Would render {why}."));
        foreach (var (reel, why) in reels.Values)
            uses.Add(new("Reel", null, null, null, $"The reel “{reel.Name}” was generated {why}."));
        foreach (var take in snapshot.Shots.Takes.Where(t => tainted.ContainsKey(t.Id)))
        {
            var row = snapshot.Row(take.ShotId);
            uses.Add(new(row?.Coverage.SelectedTakeId == take.Id ? "Selected take" : "Take", take.ShotId, row?.Number, take.Id, tainted[take.Id]));
        }
        return uses;
    }
}
