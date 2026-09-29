using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public interface IAssetReuseStore
{
    Task<AssetReuseResult> ReuseAsync(AssetReuseCommand command, CancellationToken ct = default);
    Task<SharedAssetEntry> PublishSharedAsync(Guid id, AssetReuseSelection source, string fingerprint, string name, CancellationToken ct = default);
    Task<IReadOnlyList<SharedAssetEntry>> ListSharedAsync(CancellationToken ct = default);
}

public sealed partial class FileAssetStore : IAssetReuseStore
{
    public async Task<AssetReuseResult> ReuseAsync(AssetReuseCommand command, CancellationToken ct = default)
    {
        AssetReusePolicy.Validate(command);
        var destination = await files.DirectoryAsync(command.Destination.ProjectId, ct);
        var operationPath = Path.Combine(destination, "asset-reuse-operations", command.Id.ToString("D") + ".json");
        using var operation = await ProjectFiles.LockAsync(operationPath, ct);
        var requestHash = AssetReusePolicy.Hash(command);
        var claimed = File.Exists(operationPath) ? await AtomicJsonFile.ReadAsync<AssetMoveReceipt>(operationPath, ct) : null;
        if (claimed is not null && (claimed.CommandId != command.Id || claimed.Fingerprint != requestHash))
            throw new WorkspaceStoreException("This copy command was already used for different inputs.");
        await AtomicJsonFile.WriteAsync(operationPath, new AssetMoveReceipt(command.Id, requestHash), ct);
        var latest = await ReadAsync(destination, command.Destination.ProjectId, ct);
        var receipt = latest.AssetReuseReceipts?.SingleOrDefault(r => r.CommandId == command.Id);
        if (receipt is null)
        {
            var sourceDirectory = command.Source is { } source ? await files.DirectoryAsync(source.ProjectId, ct) : null;
            using (await LockReusePathsAsync(new[] { destination, sourceDirectory }, ct))
            {
                latest = await ReadAsync(destination, command.Destination.ProjectId, ct);
                // Concurrent commands for other items may append while this command waits.
                receipt = latest.AssetReuseReceipts?.SingleOrDefault(r => r.CommandId == command.Id);
                if (receipt is null)
                {
                    var staging = Path.Combine(destination, "asset-reuse-staging", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    try
                    {
                        AssetReusePackage package; string contentRoot;
                        if (command.Source is { } selection)
                        {
                            var library = await ReadAsync(sourceDirectory!, selection.ProjectId, ct);
                            var content = AssetReusePolicy.Capture(library, selection);
                            if (AssetReusePolicy.Hash(content) != command.SourceFingerprint)
                                throw new WorkspaceStoreException("The copied item's metadata changed. Copy it again before transferring.");
                            package = await CaptureReusePackageAsync(command.Id, command.Destination.Name, content, sourceDirectory!, staging, ct);
                            contentRoot = staging;
                        }
                        else
                        {
                            contentRoot = SharedPath(command.SharedEntryId!.Value);
                            package = await ReadReusePackageAsync(contentRoot, ct);
                            if (package.Id != command.SharedEntryId || package.SourceFingerprint != command.SourceFingerprint)
                                throw new WorkspaceStoreException("The shared snapshot changed. Refresh the shared library.");
                        }
                        var imported = AssetReusePolicy.Import(package.Content, latest, command, clock.GetUtcNow());
                        // All files are verified and installed before the one visible metadata publication.
                        // On interrupted publication, unreferenced files are harmless and a retry verifies them.
                        var installed = await InstallReuseFilesAsync(package, contentRoot, destination, command, ct);
                        await AtomicJsonFile.WriteAsync(operationPath + ".files.json", new AssetReuseInstalledFiles(command.Id, requestHash, installed), ct);
                        latest = await PublishAsync(destination, imported.Library, latest.Revision, ct);
                        receipt = latest.AssetReuseReceipts!.Single(r => r.CommandId == command.Id);
                    }
                    finally { RemoveReuseStaging(staging); }
                }
            }
        }
        if (receipt.Fingerprint != requestHash) throw new WorkspaceStoreException("This copy receipt belongs to different inputs.");
        var result = ReuseResult(latest, receipt);
        if (!command.Move || !result.Available) return result;
        try { return await FinishReuseMoveAsync(command, receipt, ct); }
        catch (Exception e) when (e is WorkspaceStoreException or IOException or UnauthorizedAccessException)
        {
            // The destination is committed. Never delete it, or conceal a partial move as a failure to copy.
            return result with { Notice = "The destination copy is saved, but the source was not removed. " + e.Message + " Retry this move to finish without another copy." };
        }
    }

    public async Task<SharedAssetEntry> PublishSharedAsync(Guid id, AssetReuseSelection source, string fingerprint, string name, CancellationToken ct = default)
    {
        AssetReusePolicy.Validate(source);
        if (id == Guid.Empty || !AssetReusePolicy.IsHash(fingerprint) || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 240)
            throw new WorkspaceStoreException("Name the shared snapshot and choose an active source.");
        var final = SharedPath(id);
        using var entryGate = await ProjectFiles.LockAsync(final, ct);
        if (File.Exists(Path.Combine(final, "manifest.json")))
        {
            var prior = await ReadReusePackageAsync(final, ct);
            if (prior.Content.Source != source || prior.SourceFingerprint != fingerprint || prior.Name != name.Trim())
                throw new WorkspaceStoreException("This shared-publication identity belongs to another snapshot.");
            return SharedSummary(prior);
        }
        var project = await files.DirectoryAsync(source.ProjectId, ct);
        using var projectGate = await ProjectFiles.LockAsync(project, ct);
        var content = AssetReusePolicy.Capture(await ReadAsync(project, source.ProjectId, ct), source);
        if (AssetReusePolicy.Hash(content) != fingerprint) throw new WorkspaceStoreException("The asset changed. Review it before publishing a shared snapshot.");
        EnsureNoReuseLink(files.SharedAssetsDirectory);
        Directory.CreateDirectory(files.SharedAssetsDirectory);
        var staging = Path.Combine(files.SharedAssetsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var package = await CaptureReusePackageAsync(id, name.Trim(), content, project, staging, ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(staging, "manifest.json"), package, ct);
            ct.ThrowIfCancellationRequested();
            DurableFile.FlushDirectory(staging);
            Directory.Move(staging, final); // A shared entry becomes visible only when complete.
            return SharedSummary(package);
        }
        finally { RemoveReuseStaging(staging); }
    }
    public async Task<IReadOnlyList<SharedAssetEntry>> ListSharedAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(files.SharedAssetsDirectory)) return [];
        var result = new List<SharedAssetEntry>();
        foreach (var path in Directory.EnumerateDirectories(files.SharedAssetsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(path), "D", out var id)) continue; // Unpublished staging is never listed.
            var package = await ReadReusePackageAsync(path, ct);
            if (package.Id != id) throw new WorkspaceStoreException("A shared snapshot's directory does not match its identity.");
            result.Add(SharedSummary(package));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.CreatedUtc).ToArray();
    }
    private string SharedPath(Guid id) => id != Guid.Empty ? Path.Combine(files.SharedAssetsDirectory, id.ToString("D"))
        : throw new WorkspaceStoreException("Invalid shared asset identity.");
    private static SharedAssetEntry SharedSummary(AssetReusePackage p) => new(p.Id, p.Name, p.Content.Asset.Category, p.CreatedUtc,
        p.SourceFingerprint, p.Content.Source, p.Content.Asset.Images.Count, p.Content.Reels.Count, p.Content.Voices.Count, p.Files.Sum(f => f.Bytes));
    private static AssetReuseResult ReuseResult(AssetLibrary library, AssetReuseReceipt receipt)
    {
        var asset = library.Assets.FirstOrDefault(a => a.Id == receipt.AssetId);
        var available = asset is not null && (receipt.MediaId is null || asset.Images.Any(i => i.Id == receipt.MediaId) ||
            library.Reels.Any(r => r.Id == receipt.MediaId && r.AssetId == asset.Id) || library.Voices.Any(v => v.Id == receipt.MediaId && v.AssetId == asset.Id));
        return new(library, receipt.AssetId, receipt.MediaId, available, Notice: available ? null :
            "This copy was already saved and subsequently moved or removed. It has not been recreated.");
    }

    private async Task<AssetReuseResult> FinishReuseMoveAsync(AssetReuseCommand command, AssetReuseReceipt receipt, CancellationToken ct)
    {
        var source = command.Source!;
        var sourceDirectory = await files.DirectoryAsync(source.ProjectId, ct);
        var destination = await files.DirectoryAsync(command.Destination.ProjectId, ct);
        var queuePath = Path.Combine(files.AiJobsDirectory, "queue.json");
        using var gates = await LockReusePathsAsync(new[] { sourceDirectory, destination, queuePath }, ct);
        var current = await ReadAsync(sourceDirectory, source.ProjectId, ct);
        var target = await ReadAsync(destination, command.Destination.ProjectId, ct);
        var result = ReuseResult(target, receipt);
        if (!result.Available) return result;
        var prior = current.AssetMoveReceipts?.SingleOrDefault(r => r.CommandId == command.Id);
        if (prior is not null)
        {
            if (prior.Fingerprint != receipt.Fingerprint) throw new WorkspaceStoreException("The saved move acknowledgement belongs to another request.");
            return result with { SourceRemoved = true }; // Do not delete a source restored after this move.
        }
        var stillExists = source.Kind switch {
            AssetReuseKind.Asset => current.Assets.Any(a => a.Id == source.AssetId),
            AssetReuseKind.Image => current.Assets.SelectMany(a => a.Images).Any(i => i.Id == source.MediaId),
            AssetReuseKind.Reel => current.Reels.Any(r => r.Id == source.MediaId),
            _ => current.Voices.Any(v => v.Id == source.MediaId)
        };
        if (stillExists)
        {
            var content = AssetReusePolicy.Capture(current, source);
            if (AssetReusePolicy.Hash(content) != command.SourceFingerprint)
                throw new WorkspaceStoreException("The source was edited after copying; its newer version has been kept.");
            await VerifyReuseDestinationAsync(destination, command, content, target, ct);
            await EnsureReusableMoveAsync(content, current, sourceDirectory, queuePath, ct);
            var asset = current.Assets.Single(a => a.Id == source.AssetId);
            var imageIds = content.Asset.Images.Select(i => i.Id).ToHashSet();
            var reelIds = content.Reels.Select(r => r.Id).ToHashSet();
            var voiceIds = content.Voices.Select(v => v.Id).ToHashSet();
            current = current with {
                Assets = source.Kind == AssetReuseKind.Asset ? current.Assets.Where(a => a.Id != source.AssetId).ToList() :
                    current.Assets.Select(a => a.Id != asset.Id ? a : a with {
                        Images = a.Images.Where(i => !imageIds.Contains(i.Id)).ToList(),
                        DefaultVoiceId = a.DefaultVoiceId is { } id && voiceIds.Contains(id) ? null : a.DefaultVoiceId, UpdatedUtc = clock.GetUtcNow() }).ToList(),
                Trash = [.. current.Trash, .. content.Asset.Images.Select(i => TrashEntry(asset, i))],
                Reels = current.Reels.Where(r => !reelIds.Contains(r.Id)).ToList(),
                ReelTrash = [.. current.ReelTrash, .. content.Reels.Select(r => TrashedReferenceReel.Removed(r, asset with { Images = [], DefaultVoiceId = null }, clock.GetUtcNow()))],
                Voices = current.Voices.Where(v => !voiceIds.Contains(v.Id)).ToList(),
                VoiceTrash = [.. current.VoiceTrash, .. content.Voices.Select(v => TrashVoice(current, v))]
            };
        }
        current = current with { AssetMoveReceipts = [.. current.AssetMoveReceipts ?? [], new(command.Id, receipt.Fingerprint)] };
        await PublishAsync(sourceDirectory, current, current.Revision, ct);
        return result with { SourceRemoved = true, Notice = "Moved to the destination project. Source media is retained in its project's Trash." };
    }
    private static async Task EnsureReusableMoveAsync(AssetReuseContent content, AssetLibrary library, string directory, string queuePath, CancellationToken ct)
    {
        var ids = content.Asset.Images.Select(i => i.Id).Concat(content.Reels.SelectMany(r => new[] { r.Id, r.Media.Id }))
            .Concat(content.Voices.Select(v => v.Id)).ToHashSet();
        if (content.Source.Kind == AssetReuseKind.Asset) ids.Add(content.Asset.Id);
        foreach (var name in new[] { "shots.json", "production.json" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            var document = await AtomicJsonFile.ReadAsync<JsonElement>(path, ct);
            if (AssetReusePolicy.ContainsIdentity(document, ids))
                throw new WorkspaceStoreException("Saved shots or takes reference this item. Copy it instead of moving it.");
        }
        foreach (var draft in library.ReelDrafts.Where(d => d.AssetId != content.Source.AssetId || content.Source.Kind != AssetReuseKind.Asset))
            if (AssetReusePolicy.ContainsIdentity(JsonSerializer.SerializeToElement(new { draft.Images, draft.KeyframeReels, draft.Voice }, AtomicJsonFile.Options), ids))
                throw new WorkspaceStoreException("A reference-reel draft uses this item. Copy it instead of moving it.");
        if (File.Exists(queuePath))
        {
            var queue = await AtomicJsonFile.ReadAsync<AiQueueDocument>(queuePath, ct);
            if (queue is null || queue.Jobs is null || queue.Jobs.Any(j => j is null || j.Target is null))
                throw new WorkspaceStoreException("The AI queue could not be checked. The source has been kept.");
            if (queue.Jobs.Any(j => j.Target.ProjectId == content.Source.ProjectId && (j.LocksTarget || j.HoldsProvider)) == true)
                throw new WorkspaceStoreException("This source project has active or retained AI requests. Finish or cancel them before moving media.");
        }
    }
    private static async Task<IDisposable> LockReusePathsAsync(IEnumerable<string?> paths, CancellationToken ct)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in paths.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                leases.Add(await ProjectFiles.LockAsync(path, ct));
            return new ReuseLocks(leases);
        }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
    }
    private sealed class ReuseLocks(List<IDisposable> leases) : IDisposable
    { public void Dispose() { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); } }
    private static void RemoveReuseStaging(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Unpublished staging is safe to remove later. */ }
    }
}
