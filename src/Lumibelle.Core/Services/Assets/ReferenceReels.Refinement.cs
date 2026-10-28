using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public const string NoRefinementLatents = "This reel has no saved latents. Regenerate at its source size and seed with Save latents enabled, then refine the new reel.";

    // Reuse the shared H3 refinement form and transport without adding anything to the shot library.
    public static ShotTake RefinementSource(AssetReferenceReel reel)
    {
        var generation = reel.Generation ?? throw new WorkspaceStoreException("This reel has no saved generation request.");
        return new() { Id = reel.Id, RunId = generation.BatchId, AiJobId = generation.JobId, Candidate = generation.Candidate,
            Seed = generation.Seed, Snapshot = ShotCopy.Of(generation.Snapshot), Width = reel.Media.Width, Height = reel.Media.Height,
            Fps = reel.Media.Fps, RefinementPackage = reel.RefinementPackage, Refinement = ShotCopy.Of(reel.Refinement) };
    }
}
