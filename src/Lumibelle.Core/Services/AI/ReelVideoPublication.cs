using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// Destination adapter: the queue and Comfy execution remain shared with takes.
public sealed class ReelVideoPublication(IAssetStore assets, IAssetReelStore reels, IReferenceVideoStore media)
{
    public Task<string> DirectoryAsync(VideoSnapshot snapshot, Guid batch, CancellationToken ct) => reels.RunDirectoryAsync(snapshot.ProjectId, batch, ct);
    public async Task ValidateAsync(VideoSnapshot snapshot, CancellationToken ct)
    {
        var library = await assets.LoadAsync(snapshot.ProjectId, ct);
        LookPolicy.ValidateTarget(library, snapshot.Reel!.Character);
        ReferenceReels.ValidateOwner(snapshot.Reel.Recipe, library.Assets.Single(a => a.Id == snapshot.Reel.Recipe.AssetId));
    }
    public async Task<bool> PublishedAsync(VideoSnapshot snapshot, Guid candidate, Guid job, Guid batch, int number, CancellationToken ct)
    {
        var receipt = (await assets.LoadAsync(snapshot.ProjectId, ct)).ReelPublications.SingleOrDefault(r => r.ReelId == candidate);
        if (receipt is null) return false;
        if (receipt.JobId != job || receipt.BatchId != batch || receipt.Candidate != number)
            throw new WorkspaceStoreException("This reel candidate belongs to another request.");
        return true;
    }
    public async Task PublishAsync(ShotTake output, string directory, CancellationToken ct)
    {
        var s = output.Snapshot; var recipe = s.Reel!.Recipe;
        // Persist the immutable media identity before publishing metadata, so failed
        // publication retries cannot create a new reel or change its media identity.
        var receiptPath = Path.Combine(directory, "reel-media.json");
        var stored = await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(receiptPath, ct);
        if (stored is null)
        {
            await using var content = File.OpenRead(Path.Combine(directory, "video.mp4"));
            stored = await media.ImportAsync(s.ProjectId, content, "reel.mp4", s.Settings, ct);
            await AtomicJsonFile.WriteAsync(receiptPath, stored, ct);
        }
        if (output.HasLosslessFrames) await media.PublishArchiveAsync(s.ProjectId, stored, output, directory, ct);
        var reel = new AssetReferenceReel { Id = output.Id, AssetId = recipe.AssetId, LookId = recipe.LookId, Name = recipe.Name,
            UseGuidance = recipe.UseGuidance, Media = stored, CreatedUtc = output.CreatedUtc,
            Generation = new(recipe.Copy(), output.RunId, output.AiJobId!.Value, output.Candidate, output.Seed, ShotCopy.Of(s)),
            RefinementPackage = output.RefinementPackage, Refinement = ShotCopy.Of(output.Refinement) };
        await reels.RetainRefinementPackageAsync(s.ProjectId, reel, directory, ct);
        await reels.PublishReelAsync(s.ProjectId, reel, ct);
    }
}
