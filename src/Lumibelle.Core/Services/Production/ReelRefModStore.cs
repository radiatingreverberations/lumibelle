using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

// The fitted source PNGs and recipe are authoritative, hash-verified inputs.
// Server receipts are replaceable cache hints. No latent transfer/sync is required.
public sealed class ReelRefModStore(ProjectFiles files)
{
    public async Task<string> BuildDirectoryAsync(Guid project, Guid job, CancellationToken ct)
    {
        if (job == Guid.Empty) throw new WorkspaceStoreException("A RefMod build needs an identity.");
        return Path.Combine(await files.DirectoryAsync(project, ct), "refmod-builds", job.ToString("D"));
    }
    private async Task<string> PreviewDirectoryAsync(Guid project, string key, CancellationToken ct)
    {
        if (!ReelRefMods.Hash(key)) throw new WorkspaceStoreException("Invalid RefMod source identity.");
        return Path.Combine(await files.DirectoryAsync(project, ct), "refmod-previews", key);
    }
    private async Task<string> ReceiptPathAsync(Guid project, string key, string server, CancellationToken ct) =>
        Path.Combine(await PreviewDirectoryAsync(project, key, ct), "server-" + ReelRefMods.Digest(AiProviderRegistry.NormalizeComfyUrl(server)) + ".json");
    public async Task<ReelRefModReference?> FindAsync(Guid project, string key, string server, CancellationToken ct)
    {
        var path = await ReceiptPathAsync(project, key, server, ct);
        if (!File.Exists(path)) return null;
        ReelRefModReference? reference;
        try { reference = await AtomicJsonFile.ReadAsync<ReelRefModReference>(path, ct); }
        catch (WorkspaceStoreException e) when (e.InnerException is JsonException) { return null; }
        if (reference is null || string.IsNullOrWhiteSpace(reference.ComfyUrl)) return null;
        // A corrupt/mismatched cache hint is a miss, not lost source data. Storage
        // access errors still propagate; never ignore corrupt accepted source PNGs.
        try { ReelRefMods.Validate(reference); }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { return null; }
        return reference.Recipe.Key == key && reference.ComfyUrl == AiProviderRegistry.NormalizeComfyUrl(server)
            ? reference : null; // Caller still checks the file on this server.
    }
    public async Task RememberAsync(Guid project, ReelRefModReference reference, CancellationToken ct)
    {
        ReelRefMods.Validate(reference);
        if (reference.FileName != ReelRefMods.BuildStem(project, reference.BuildId))
            throw new WorkspaceStoreException("The reference-cache receipt belongs to another project.");
        var directory = await PreviewDirectoryAsync(project, reference.Recipe.Key, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        // This writes only an advisory cache location, never the accepted source PNGs.
        // Recovery can record a finished build even when sources are currently offline.
        await AtomicJsonFile.WriteAsync(await ReceiptPathAsync(project, reference.Recipe.Key, reference.ComfyUrl, ct), reference, ct);
    }
    // Initial acceptance stores only CPU-prepared inputs. An unavailable ComfyUI
    // server is irrelevant here; no server receipt or build job is created.
    public async Task AcceptSourcesAsync(Guid project, ReelRefModRecipe recipe, IReadOnlyList<byte[]> pixels, CancellationToken ct)
    {
        ReelRefMods.Validate(recipe);
        if (pixels is null || pixels.Count != recipe.LatentFrames || pixels.Any(p => p is null)) throw new WorkspaceStoreException("The selected reference images do not match the recipe.");
        // Own and validate every buffer before awaiting or writing any source file.
        var captured = pixels.Select(p => p.ToArray()).ToArray();
        for (var i = 0; i < captured.Length; i++)
        {
            CheckHash(captured[i], recipe.FrameHashes[i]);
            var image = SixLabors.ImageSharp.Image.Identify(captured[i]);
            if (image.Width != recipe.Width || image.Height != recipe.Height || captured[i].Length > 16 * 1024 * 1024)
                throw new WorkspaceStoreException("The prepared RefMod image does not match its canvas.");
        }
        ct.ThrowIfCancellationRequested();
        var directory = await PreviewDirectoryAsync(project, recipe.Key, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        Directory.CreateDirectory(directory);
        // Never overwrite corrupt accepted pixels. A previous partial acceptance
        // can safely fill missing files, but only with the same verified hashes.
        for (var i = 0; i < captured.Length; i++)
        {
            var path = Path.Combine(directory, FrameName(i));
            if (File.Exists(path)) CheckHash(await File.ReadAllBytesAsync(path, ct), recipe.FrameHashes[i]);
        }
        for (var i = 0; i < captured.Length; i++)
        {
            var path = Path.Combine(directory, FrameName(i));
            if (!File.Exists(path)) await WriteAsync(path, captured[i], ct);
        }
    }
    public async Task PublishAsync(RefModBuildRequest request, ReelRefModReference reference, CancellationToken ct)
    {
        ReelRefMods.Validate(reference);
        if (reference.BuildId != request.JobId || reference.Recipe.Key != request.Recipe.Key ||
            reference.ComfyUrl != request.ComfyUrl || reference.FileName != ReelRefMods.BuildStem(request.ProjectId, request.JobId))
            throw new WorkspaceStoreException("The RefMod receipt does not match this build.");
        var directory = await PreviewDirectoryAsync(request.ProjectId, request.Recipe.Key, ct);
        var input = Path.Combine(await BuildDirectoryAsync(request.ProjectId, request.JobId, ct), "inputs");
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        Directory.CreateDirectory(directory);
        for (var i = 0; i < request.Recipe.LatentFrames; i++)
        {
            var name = FrameName(i);
            var preview = await File.ReadAllBytesAsync(Path.Combine(input, name), ct);
            CheckHash(preview, request.Recipe.FrameHashes[i]);
            await WriteAsync(Path.Combine(directory, name), preview, ct);
        }
        await AtomicJsonFile.WriteAsync(await ReceiptPathAsync(request.ProjectId, request.Recipe.Key, request.ComfyUrl, ct), reference, ct);
    }
    public async Task<byte[]> PreviewAsync(Guid project, ReelRefModReference reference, int index, CancellationToken ct)
    {
        ReelRefMods.Validate(reference);
        if (index < 0 || index >= reference.Recipe.LatentFrames) throw new WorkspaceStoreException("Invalid RefMod preview index.");
        var path = Path.Combine(await PreviewDirectoryAsync(project, reference.Recipe.Key, ct), FrameName(index));
        if (!File.Exists(path)) throw new WorkspaceStoreException("An accepted RefMod source image is unavailable. Restore the project source images; the remote cache alone cannot reconstruct the approved inputs.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        CheckHash(bytes, reference.Recipe.FrameHashes[index]); return bytes;
    }
    public async Task<IReadOnlyList<RefModInspectionFrame>> InspectionAsync(Guid project, Shot shot, CancellationToken ct)
    {
        var result = new List<RefModInspectionFrame>();
        foreach (var video in ResolvedReferences.For(shot).Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod))
        {
            ReelRefMods.ValidateBinding(video.Reel, true);
            var reference = video.Reel.RefMod!;
            for (var i = 0; i < reference.Recipe.LatentFrames; i++)
                result.Add(new(video.Number, i + 1, video.Reel.Name, video.Reel.Keyframes!.Frames[i].Notes,
                    reference.Recipe.FrameHashes[i], await PreviewAsync(project, reference, i, ct)) { ViewName = video.Reel.Keyframes.Frames[i].Name });
        }
        return result;
    }
    public static string FrameName(int zeroBased) => $"frame-{zeroBased + 1:D2}.png";
    public static void CheckHash(byte[] bytes, string expected)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expected)
            throw new WorkspaceStoreException("A captured RefMod source preview changed. Restore its bytes or prepare a new build explicitly.");
    }
    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, ct); DurableFile.Flush(temporary); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
