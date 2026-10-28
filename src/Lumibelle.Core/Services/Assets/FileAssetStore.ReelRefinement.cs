using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task RetainRefinementPackageAsync(Guid project, AssetReferenceReel reel, string directory, CancellationToken ct = default)
    {
        if (reel.RefinementPackage is not { } package) return;
        var generation = reel.Generation ?? throw new WorkspaceStoreException("Missing captured reel generation.");
        var source = Path.Combine(directory, H3RefinementPackage.FileName);
        if (await RefinementPackages.InspectAsync(source, generation.Snapshot, reel.Refinement, ct) != package)
            throw new WorkspaceStoreException("The reel's refinement package does not match its saved output.");
        var folder = Path.Combine(await files.DirectoryAsync(project, ct), "reference-videos", reel.Media.Id.ToString("D"));
        Directory.CreateDirectory(folder);
        await CopyReelPackage(source, Path.Combine(folder, H3RefinementPackage.FileName), package, ct);
    }

    public async Task<TakeRefinement> CaptureRefinementAsync(Guid project, Guid reelId, Guid run, TakeRefinementMode mode,
        int width, int height, string upscaler, H3UpscalerImplementation implementation, CancellationToken ct = default)
    {
        if (run == Guid.Empty) throw new WorkspaceStoreException("Invalid refinement request identity.");
        var directory = await files.DirectoryAsync(project, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var library = await ReadAsync(directory, project, ct);
        var reel = library.Reels.SingleOrDefault(r => r.Id == reelId)
            ?? throw new WorkspaceStoreException("Restore the source reel before refining it.");
        var package = reel.RefinementPackage ?? throw new WorkspaceStoreException(ReferenceReels.NoRefinementLatents);
        var snapshot = reel.Generation?.Snapshot ?? throw new WorkspaceStoreException("This reel has no saved generation request.");
        var source = Path.Combine(directory, "reference-videos", reel.Media.Id.ToString("D"), H3RefinementPackage.FileName);
        if (await RefinementPackages.InspectAsync(source, snapshot, reel.Refinement, ct) != package)
            throw new WorkspaceStoreException("The source reel's refinement package changed or is corrupt.");
        var refinement = new TakeRefinement(reel.Id, package, mode, width, height, upscaler, implementation);
        RefinementPolicy.Validate(refinement, snapshot);
        var target = Path.Combine(await RunDirectoryAsync(project, run, ct), "inputs");
        if (Directory.Exists(target)) throw new WorkspaceStoreException("This refinement already has captured inputs. Retry its saved submission.");
        Directory.CreateDirectory(target);
        await CopyReelPackage(source, Path.Combine(target, H3RefinementPackage.FileName), package, ct);
        return refinement;
    }

    private static async Task CopyReelPackage(string source, string target, H3RefinementPackage package, CancellationToken ct)
    {
        if (File.Exists(target)) { await RefinementPackages.VerifyFileAsync(target, package.Bytes, package.Sha256, ct); return; }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await TakeTrimming.CopyVerifiedAsync(source, temporary, package.Bytes, package.Sha256, ct);
            File.Move(temporary, target);
        }
        finally { File.Delete(temporary); }
    }
}
