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
        // Reels generated with the picture carry it into every shot and take that uses them, by the reel's media.
        var reels = snapshot.Assets.Reels.Where(r => r.Generation?.Snapshot.Shot is { } shot && Uses(shot)).ToArray();
        var reelMedia = reels.Where(r => r.Media is not null).DistinctBy(r => r.Media.Id).ToDictionary(r => r.Media.Id, r => r.Name);
        string? Reel(Shot shot) => shot.Videos.Select(v => v.Media?.Id).OfType<Guid>().Select(id => reelMedia.GetValueOrDefault(id)).FirstOrDefault(name => name is not null);
        foreach (var row in snapshot.Rows)
            if (Uses(row.Shot)) uses.Add(new("Shot", row.Id, row.Number, null, "Selected as a reference picture."));
            else if (Reel(row.Shot) is { } name) uses.Add(new("Shot", row.Id, row.Number, null, $"Uses the reel “{name}”, which was generated with it."));
        foreach (var reel in reels)
            uses.Add(new("Reel", null, null, null, $"The reel “{reel.Name}” was generated with it."));

        // Takes rendered with the picture, then takes that took a frame from any take already found. Takes in Trash are
        // followed too, so a take rendered from a trashed one is still found; only takes outside Trash are reported.
        var trashed = snapshot.Shots.Trash.Select(e => e.Take).OfType<ShotTake>().Where(t => snapshot.Shots.Takes.All(x => x.Id != t.Id)).ToArray();
        var all = snapshot.Shots.Takes.Concat(trashed).ToArray();
        var tainted = new Dictionary<Guid, string>();
        foreach (var take in all)
            if (Uses(take.Snapshot.Shot)) tainted[take.Id] = "Rendered with it as a reference picture.";
            else if (Reel(take.Snapshot.Shot) is { } name) tainted[take.Id] = $"Rendered with the reel “{name}”, which was generated with it.";
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var take in all.Where(t => !tainted.ContainsKey(t.Id)))
            {
                var rendered = take.Snapshot.Shot;
                var source = rendered.ShotFrames.Select(f => f.TakeId).Append(rendered.StartFrame?.TakeId ?? Guid.Empty).FirstOrDefault(tainted.ContainsKey);
                if (source == Guid.Empty) continue;
                var from = all.First(t => t.Id == source);
                tainted[take.Id] = $"Rendered with a frame from {TakeDisplay.Label(from)} of shot #{snapshot.Row(from.ShotId)?.Number}{(trashed.Contains(from) ? ", now in Trash," : ",")} which carries it.";
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
