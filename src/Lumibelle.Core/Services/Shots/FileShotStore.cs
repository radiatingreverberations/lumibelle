using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public interface IShotStore
{
    Task<ShotDocument> LoadAsync(Guid projectId, CancellationToken ct = default);
    Task<ShotDocument> SaveAsync(Guid projectId, IReadOnlyList<Shot> shots, long expectedRevision, string reason = "Edit shots", CancellationToken ct = default);
    Task<ShotDocument> SaveWorkspaceAsync(Guid projectId, IReadOnlyList<Shot> shots, IReadOnlyList<SceneReferenceSetup> setups, long expectedRevision, string reason = "Edit shots", CancellationToken ct = default)
        => setups.Count == 0 ? SaveAsync(projectId, shots, expectedRevision, reason, ct) : throw new WorkspaceStoreException("Scene reference setup storage is unavailable.");
    Task<ShotDocument> SaveImageReferencesAsync(Guid projectId, IReadOnlyList<Shot> shots, long expectedRevision, long assetRevision, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reference review storage is unavailable.");
    Task<ShotDocument> ApplyPlanningAsync(Guid projectId, Guid jobId, IReadOnlyList<Shot> shots, long expectedRevision, CancellationToken ct = default);
    Task<ShotDocument> DeleteShotsAsync(Guid projectId, IReadOnlyCollection<Guid> shotIds, long expectedRevision, bool clearProductionSelections = false, CancellationToken ct = default);
    Task<ShotDocument> RecoverAsync(Guid projectId, Guid recoveryId, long expectedRevision, CancellationToken ct = default);
    Task<ShotDocument> PublishTakeAsync(Guid projectId, ShotTake take, string stagingDirectory, CancellationToken ct = default);
    Task<(TakeExtensionRequest Source, TakeMotionContext Motion)> CaptureExtensionAsync(Guid projectId, Guid takeId, Guid runId, int endFrameExclusive, double addedSeconds, bool combine, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Motion-aware extension capture is unavailable.");
    Task<(TakeExtensionRequest Source, TakeMotionContext Motion)> CaptureLeadInAsync(Guid projectId, Guid takeId, Guid runId, int startFrame, double addedSeconds, bool combine, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Lead-in capture is unavailable.");
    Task<ShotDocument> PublishExtensionAsync(Guid projectId, AiVideoJobRequest request, Guid fullTakeId, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Extension publication is unavailable.");
    Task<AiVideoJobRequest> CaptureExtensionVersionAsync(Guid projectId, Guid takeId, Guid runId, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Extension replay is unavailable.");
    Task<ShotDocument> TrimTakeAsync(Guid projectId, TakeTrimRequest request, long? expectedRevision = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Take trimming is unavailable.");
    Task<ShotDocument> MoveTakesAsync(Guid projectId, IReadOnlyCollection<Guid> takeIds, Guid destinationShotId, long expectedRevision, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Moving takes is unavailable.");
    Task<ShotDocument> DiscardAsync(Guid projectId, Guid mediaId, ShotTrashKind kind, long expectedRevision, CancellationToken ct = default);
    Task<ShotDocument> RestoreAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision, CancellationToken ct = default);
    Task<ShotDocument> PurgeAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision, CancellationToken ct = default);
    Task<ShotDocument> RemoveFrameArchivesAsync(Guid projectId, IReadOnlyCollection<Guid> takeIds, long expectedRevision, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Frame archive cleanup is unavailable.");
    Task<ShotDocument> ResumeFrameArchiveCleanupAsync(Guid projectId, CancellationToken ct = default)
        => LoadAsync(projectId, ct);
    Task<AssetMedia?> OpenJoinPreviewAsync(Guid projectId, Guid takeId, CancellationToken ct = default) => Task.FromResult<AssetMedia?>(null);
    Task<AssetMedia?> OpenAsync(Guid projectId, Guid mediaId, ShotTrashKind kind, int? frame = null, bool trash = false, CancellationToken ct = default);
    Task<string> RunDirectoryAsync(Guid projectId, Guid runId, CancellationToken ct = default);
    Task SaveRunAsync(VideoRun run, CancellationToken ct = default);
    Task<IReadOnlyList<VideoRun>> RunsAsync(Guid projectId, CancellationToken ct = default);
    Task<TakeRefinement> CaptureRefinementAsync(Guid projectId, Guid takeId, Guid runId, TakeRefinementMode mode, int width, int height, string upscaler,
        H3UpscalerImplementation implementation, CancellationToken ct = default) => throw new WorkspaceStoreException("Refinement storage is unavailable.");
}

public sealed partial class FileShotStore(ProjectFiles files, TimeProvider clock, TakeFrameReader? frameReader = null, IProductionMediaTools? mediaTools = null, lumibelle.Services.AI.IAiJobStore? jobs = null) : IShotStore
{
    private static string Manifest(string directory) => Path.Combine(directory, "shots.json");
    private static void Revision(ShotDocument d, long expected) { if (d.Revision != expected) throw new WorkspaceConflictException(); }
    public async Task<ShotDocument> LoadAsync(Guid projectId, CancellationToken ct = default) => await Read(await files.DirectoryAsync(projectId, ct), projectId, ct);
    private static async Task<ShotDocument> Read(string dir, Guid id, CancellationToken ct)
    {
        var doc = await AtomicJsonFile.ReadAsync<ShotDocument>(Manifest(dir), ct) ?? new() { ProjectId = id };
        if (doc.SchemaVersion == 1)
        {
            // Before schema 2 the application default was always 16:9. Compare to
            // that historical default even if project settings changed before migration.
            foreach (var shot in doc.Shots.Concat(doc.Recovery.SelectMany(r => r.Shots)).Concat(doc.Trash.Where(t => t.Owner is not null).Select(t => t.Owner!)))
                shot.AspectOverride = shot.Aspect == "16:9" ? null : shot.Aspect;
            doc.SchemaVersion = 2;
        }
        Validate(doc, id); return doc.Copy();
    }
    public static void Validate(ShotDocument d, Guid id)
    {
        if (d.TrimPublications is null || d.TrimPublications.Any(r => r is null || r.ResultId == Guid.Empty || r.TakeId == Guid.Empty || r.ResultId == r.TakeId || r.StartFrame < 0 || r.EndFrameExclusive <= r.StartFrame) || d.TrimPublications.DistinctBy(r => r.ResultId).Count() != d.TrimPublications.Count)
            throw new WorkspaceStoreException("Invalid trim publication receipts.");
        if (d.ExtensionPublications is null || d.ExtensionPublications.Any(r => r.ResultId == Guid.Empty || r.FullTakeId == Guid.Empty || !RefinementPolicy.Hash(r.Fingerprint)) || d.ExtensionPublications.DistinctBy(r => r.ResultId).Count() != d.ExtensionPublications.Count)
            throw new WorkspaceStoreException("Invalid extension publication receipts.");
        if (id == Guid.Empty || d.ProjectId != id || d.SchemaVersion != 2 || d.SceneSetups is null || d.SceneSetups.Any(s => s is null || s.SceneId == Guid.Empty || s.Version == Guid.Empty || s.Images is null || s.Images.Any(i => i is null || i.Image is null)) || d.SceneSetups.Select(s => s.SceneId).Distinct().Count() != d.SceneSetups.Count || d.Revision < 0 || d.Shots is null || d.Takes is null || d.Trash is null || d.Recovery is null || d.Shots.Any(x => x is null) || d.Takes.Any(x => x is null) || d.Trash.Any(x => x is null) ||
            d.Shots.Select(s => s.Id).Distinct().Count() != d.Shots.Count || d.Takes.Select(t => t.Id).Distinct().Count() != d.Takes.Count ||
            d.Trash.Select(t => t.Id).Distinct().Count() != d.Trash.Count || d.PlanningReviews is null ||
            d.PlanningReviews.Any(r => r is null || r.JobId == Guid.Empty || r.ShotIds is null || r.ShotIds.Count == 0 || r.ShotIds.Any(id => id == Guid.Empty) || r.ShotIds.Distinct().Count() != r.ShotIds.Count || r.Fingerprint?.Length != 64) ||
            d.PlanningReviews.Select(r => r.JobId).Distinct().Count() != d.PlanningReviews.Count || d.TakePublications is null ||
            d.TakePublications.Any(r => r is null || r.TakeId == Guid.Empty || r.RunId == Guid.Empty || r.ShotId == Guid.Empty || r.JobId == Guid.Empty || r.Candidate < 1 || r.Fingerprint?.Length != 64) ||
            d.TakePublications.Select(r => r.TakeId).Distinct().Count() != d.TakePublications.Count)
            throw new WorkspaceStoreException("The shots document is invalid or unsupported. It has not been replaced.");
        foreach (var shot in d.Shots) { H3Policy.Validate(shot); if (shot.SelectedTakeId is { } selected && !d.Takes.Any(t => t.Id == selected && t.ShotId == shot.Id)) throw new WorkspaceStoreException("The selected take no longer exists."); }
        foreach (var t in d.Takes.Concat(d.Trash.Where(t => t.Take is not null).Select(t => t.Take!)))
        {
            TakeTrimming.Validate(t);
            TakeBundles.Validate(t);
            if (t.Snapshot.Motion is { } motion) H3Motion.Validate(motion, t.Snapshot);
            if (t.Id == Guid.Empty || t.ShotId == Guid.Empty || t.Directory != t.Id.ToString("D") || t.Frames is null || t.Snapshot is null || t.FrameCount < 1 || t.Composition is null && t.FrameCount > 362 || t.Frames.Count != (t.Composition is null && t.HasLosslessFrames ? t.FrameCount : 0) || t.Snapshot.Shot.Id == Guid.Empty || t.RunId == Guid.Empty ||
                t.Frames.Where((f, i) => f is null || f.Index != i || f.Bytes <= 0 ||
                    f.FileName != LosslessFrameArchive.FileName(i / LosslessFrameArchive.SegmentFrames) || f.ArchiveFrameIndex is < 0 or >= LosslessFrameArchive.SegmentFrames ||
                    (i % LosslessFrameArchive.SegmentFrames == 0 ? f.ArchiveFrameIndex != 0 : f.ArchiveFrameIndex < t.Frames[i - 1].ArchiveFrameIndex || f.ArchiveFrameIndex > t.Frames[i - 1].ArchiveFrameIndex + 1)).Any() ||
                t.Width <= 0 || t.Height <= 0 || t.Fps != 24 || t.Snapshot.ProjectId != id)
                throw new WorkspaceStoreException("Invalid take frame manifest.");
            if (t.Refinement is { } refinement) RefinementPolicy.Validate(refinement, t.Snapshot);
            lumibelle.Services.Production.ShotDubbing.ValidateSnapshot(t.Snapshot);
            H3Loras.ValidateSnapshot(t.Snapshot);
            H3Presets.Validate(t.Snapshot);
            ValidateArchiveRemoval(t);
            H3PreviewUpscaling.Validate(t.Snapshot);
            if (t.Snapshot.PreviewUpscale is not null && (t.Width, t.Height) != H3PreviewUpscaling.OutputSize(t.Snapshot, t.Refinement))
                throw new WorkspaceStoreException("The upscaled take does not match its captured output dimensions.");
            if (t.RefinementPackage is { } package && (package.Id == Guid.Empty || package.Bytes <= 0 || !RefinementPolicy.Hash(package.Sha256) ||
                package.Width != t.Width || package.Height != t.Height || package.FrameCount != t.Snapshot.FrameCount) ||
                (t.Refinement is not null || t.Snapshot.CaptureRefinementData) && t.RefinementPackage is null)
                throw new WorkspaceStoreException("Invalid or missing take refinement package.");
        }
        foreach (var t in d.Trash)
            if (t.Id == Guid.Empty || t.ExpiresUtc - t.DeletedUtc != TimeSpan.FromDays(30) || t.Kind != ShotTrashKind.Take ||
                t.Take is null || t.Owner is null || t.Owner.Id != t.Take.ShotId || d.Takes.Any(x => x.Id == t.Take.Id))
                throw new WorkspaceStoreException("Invalid shot Trash record.");
    }
    private async Task<ShotDocument> Publish(string dir, ShotDocument d, CancellationToken ct)
    {
        d.Revision++; d.UpdatedUtc = clock.GetUtcNow(); Validate(d, d.ProjectId);
        await AtomicJsonFile.WriteAsync(Manifest(dir), d, ct); return d.Copy();
    }
    public Task<ShotDocument> SaveAsync(Guid projectId, IReadOnlyList<Shot> shots, long expectedRevision, string reason = "Edit shots", CancellationToken ct = default)
        => SaveCore(projectId, shots, null, expectedRevision, reason, ct);
    public Task<ShotDocument> SaveWorkspaceAsync(Guid projectId, IReadOnlyList<Shot> shots, IReadOnlyList<SceneReferenceSetup> setups, long expectedRevision, string reason = "Edit shots", CancellationToken ct = default)
        => SaveCore(projectId, shots, setups, expectedRevision, reason, ct);
    public Task<ShotDocument> SaveImageReferencesAsync(Guid projectId, IReadOnlyList<Shot> shots, long expectedRevision, long assetRevision, CancellationToken ct = default)
        => SaveCore(projectId, shots, null, expectedRevision, "Edit image references", ct, assetRevision);
    private async Task<ShotDocument> SaveCore(Guid projectId, IReadOnlyList<Shot> shots, IReadOnlyList<SceneReferenceSetup>? setups, long expectedRevision, string reason, CancellationToken ct, long? assetRevision = null)
    {
        var captured = ShotCopy.Of(shots.ToList()); foreach (var s in captured) H3Policy.Validate(s);
        var capturedSetups = setups is null ? null : ShotCopy.Of(setups.ToList());
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct); Revision(d, expectedRevision);
        if (assetRevision is { } expectedAssets)
        {
            var assets = await new lumibelle.Services.Assets.FileAssetStore(files, clock).LoadAsync(projectId, ct);
            if (assets.Revision != expectedAssets) throw new WorkspaceConflictException();
            foreach (var shot in captured.Where(s => ReferenceSetups.Hash(s.Images) != ReferenceSetups.Hash(d.Shots.FirstOrDefault(x => x.Id == s.Id)?.Images))) ShotLooks.Validate(shot, assets);
        }
        if (capturedSetups is not null)
        {
            var assets = await new lumibelle.Services.Assets.FileAssetStore(files, clock).LoadAsync(projectId, ct);
            var changedSetups = capturedSetups.Where(s => ReferenceSetups.Hash(s) != ReferenceSetups.Hash(d.SceneSetups.FirstOrDefault(x => x.SceneId == s.SceneId))).ToArray();
            var approved = changedSetups.Length > 0 ? await new FileScriptStore(files, clock).LoadAsync(projectId, ct) : null;
            foreach (var setup in changedSetups)
            {
                if (approved is null || !ScriptStructure.Sections(approved.Blocks).Any(s => s.Kind == ScriptBlockKind.Scene && s.Id == setup.SceneId))
                    throw new WorkspaceStoreException("The setup scene is no longer in the saved script. Choose an available script scene.");
                ReferenceSetups.ValidateSetup(setup, assets);
            }
        }
        var removed = d.Shots.Where(s => captured.All(x => x.Id != s.Id)).Select(s => s.Id).ToHashSet();
        if (removed.Count > 0 && jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == projectId && j.Target.ShotId is { } target && removed.Contains(target) && (j.State is AiJobState.Waiting or AiJobState.Running || j.RemoteUnconfirmed)))
            throw new WorkspaceStoreException("A removed shot has an active production request. Wait or cancel it before deleting.");
        d.Recovery.Insert(0, new(Guid.NewGuid(), clock.GetUtcNow(), reason, ShotCopy.Of(d.Shots)) { SceneSetups = ShotCopy.Of(d.SceneSetups) }); d.Recovery = d.Recovery.Take(30).ToList();
        foreach (var deleted in d.Shots.Where(s => captured.All(x => x.Id != s.Id)))
        {
            if (deleted.SelectedTakeId is not null) throw new WorkspaceStoreException("Deselect the production take before deleting this shot.");
            foreach (var take in d.Takes.Where(t => t.ShotId == deleted.Id).ToArray()) { TrashTake(d, take, deleted); d.Takes.Remove(take); }
        }
        d.Shots = captured; if (capturedSetups is not null) d.SceneSetups = capturedSetups; return await Publish(dir, d, ct);
    }
    public async Task<ShotDocument> DeleteShotsAsync(Guid projectId, IReadOnlyCollection<Guid> shotIds, long expectedRevision, bool clearProductionSelections = false, CancellationToken ct = default)
    {
        var ids = shotIds.ToHashSet();
        if (ids.Count == 0 || ids.Count != shotIds.Count || ids.Contains(Guid.Empty)) throw new WorkspaceStoreException("Choose distinct shots to delete.");
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct); Revision(d, expectedRevision);
        if (jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == projectId && j.Target.ShotId is { } target && ids.Contains(target) && (j.State is AiJobState.Waiting or AiJobState.Running || j.RemoteUnconfirmed))) throw new WorkspaceStoreException("A selected shot has an active production request. Wait or cancel it before deleting.");
        var selected = d.Shots.Where(s => ids.Contains(s.Id)).ToArray();
        if (selected.Length != ids.Count) throw new WorkspaceStoreException("The selected shots changed. Review the list before deleting.");
        if (!clearProductionSelections && selected.Any(s => s.SelectedTakeId is not null))
            throw new WorkspaceStoreException("Confirm clearing the selected production takes before deleting these shots.");
        d.Recovery.Insert(0, new(Guid.NewGuid(), clock.GetUtcNow(), $"Before deleting {ids.Count} shot(s)", ShotCopy.Of(d.Shots)) { SceneSetups = ShotCopy.Of(d.SceneSetups) });
        d.Recovery = d.Recovery.Take(30).ToList();
        // Capture every original position before removing anything. The manifest is the
        // only publication; videos, frame archives and refinement packages stay in place.
        foreach (var take in d.Takes.Where(t => ids.Contains(t.ShotId)))
            TrashTake(d, take, selected.Single(s => s.Id == take.ShotId));
        d.Takes.RemoveAll(t => ids.Contains(t.ShotId));
        d.Shots.RemoveAll(s => ids.Contains(s.Id));
        return await Publish(dir, d, ct);
    }
    public async Task<ShotDocument> ApplyPlanningAsync(Guid projectId, Guid jobId, IReadOnlyList<Shot> shots, long expectedRevision, CancellationToken ct = default)
    {
        var captured = ShotCopy.Of(shots.ToList());
        if (jobId == Guid.Empty || captured.Count == 0 || captured.Select(s => s.Id).Distinct().Count() != captured.Count)
            throw new WorkspaceStoreException("A reviewed breakdown needs a request identity and distinct shots.");
        foreach (var shot in captured) H3Policy.Validate(shot, true);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(captured, AtomicJsonFile.Options)));
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct);
        // A lost acknowledgement must not add duplicates, even after Undo, recovery or deletion.
        if (d.PlanningReviews.FirstOrDefault(r => r.JobId == jobId) is { } applied)
        {
            if (applied.Fingerprint != fingerprint) throw new WorkspaceStoreException("This breakdown was already added with different review decisions. Its shots were not added again.");
            return d;
        }
        Revision(d, expectedRevision);
        if (captured.Any(s => d.Shots.Any(existing => existing.Id == s.Id))) throw new WorkspaceStoreException("A proposed shot already exists.");
        var assets = await new lumibelle.Services.Assets.FileAssetStore(files, clock).LoadAsync(projectId, ct);
        foreach (var shot in captured) ShotLooks.Validate(shot, assets);
        d.Recovery.Insert(0, new(Guid.NewGuid(), clock.GetUtcNow(), "Add reviewed breakdown", ShotCopy.Of(d.Shots)) { SceneSetups = ShotCopy.Of(d.SceneSetups) }); d.Recovery = d.Recovery.Take(30).ToList();
        d.Shots.AddRange(captured);
        d.PlanningReviews.Add(new(jobId, clock.GetUtcNow(), captured.Select(s => s.Id).ToArray(), fingerprint));
        return await Publish(dir, d, ct);
    }
    public async Task<ShotDocument> RecoverAsync(Guid projectId, Guid recoveryId, long expectedRevision, CancellationToken ct = default)
    {
        var d = await LoadAsync(projectId, ct); Revision(d, expectedRevision);
        var recovered = ShotCopy.Of(d.Recovery.SingleOrDefault(r => r.Id == recoveryId)?.Shots ?? throw new WorkspaceStoreException("Recovery version not found."));
        foreach (var s in recovered) if (!d.Takes.Any(t => t.Id == s.SelectedTakeId && t.ShotId == s.Id)) s.SelectedTakeId = null;
        return await SaveWorkspaceAsync(projectId, recovered, d.Recovery.Single(r => r.Id == recoveryId).SceneSetups, expectedRevision, "Restore recovery", ct);
    }
    private void TrashTake(ShotDocument d, ShotTake take, Shot owner)
    {
        var copy = owner.Copy(); copy.SelectedTakeId = null; var now = clock.GetUtcNow();
        d.Trash.Add(new() { Kind = ShotTrashKind.Take, Take = take, Owner = copy, OwnerPosition = d.Shots.FindIndex(s => s.Id == owner.Id), Position = d.Takes.IndexOf(take), DeletedUtc = now, ExpiresUtc = now.AddDays(30) });
    }
    public async Task<ShotDocument> PublishTakeAsync(Guid projectId, ShotTake take, string stagingDirectory, CancellationToken ct = default)
    {
        take = ShotCopy.Of(take);
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct);
        return await PublishTakeCore(projectId, take, stagingDirectory, dir, d, ct);
    }
    private async Task<ShotDocument> PublishTakeCore(Guid projectId, ShotTake take, string stagingDirectory, string dir, ShotDocument d, CancellationToken ct)
    {
        take.Directory = take.Id.ToString("D");
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(take, AtomicJsonFile.Options)));
        if (d.TakePublications.SingleOrDefault(r => r.TakeId == take.Id) is { } receipt)
        {
            if (receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This take identity was already published with different content.");
            // Completion survives discard, permanent purge, and removal of its shot.
            return d;
        }
        if (d.Takes.Any(t => t.Id == take.Id) || d.Trash.Any(t => t.Take?.Id == take.Id)) return d;
        take.Directory = take.Id.ToString("D");
        Validate(new ShotDocument { ProjectId = projectId, Takes = [take] }, projectId);
        var target = Path.Combine(dir, "shots", "takes", take.Id.ToString("D"));
        var stage = Path.GetFullPath(stagingDirectory);
        if (!stage.StartsWith(Path.GetFullPath(Path.Combine(dir, "shots", "runs")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceStoreException("Invalid take staging directory.");
        if (!Directory.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); DurableFile.FlushDirectory(stage); await DurableFile.MoveDirectoryAsync(stage, target, ct); }
        RequireFiles(target, take);
        if (take.RefinementPackage is { } package)
        {
            var validated = await RefinementPackages.InspectAsync(Path.Combine(target, H3RefinementPackage.FileName), take.Snapshot, take.Refinement, ct);
            if (validated != package) throw new WorkspaceStoreException("The refinement package no longer matches the take.");
        }
        take.Directory = take.Id.ToString("D");
        if (d.Shots.Any(s => s.Id == take.ShotId)) d.Takes.Add(take);
        else TrashTake(d, take, take.Snapshot.Shot);
        d.TakePublications.Add(new(take.Id, take.RunId, take.ShotId, take.AiJobId, take.Candidate, fingerprint, clock.GetUtcNow()));
        // If publication fails, the complete directory remains for an idempotent retry.
        return await Publish(dir, d, ct);
    }
    private static void RequireFiles(string dir, ShotTake take)
    {
        if (TakeBundles.Files(take).Any(f => !File.Exists(TakeBundles.Under(dir, f)))) throw new WorkspaceStoreException("The retained take bundle is incomplete. Retry saving without generating again.");
        if (take.RetainedSource is { } retained && retained.Inputs.Any(i => !File.Exists(Path.Combine(dir, TakeTrimming.InputsFolder, i.FileName))))
            throw new WorkspaceStoreException("The retained refinement inputs are incomplete.");
        if (!File.Exists(Path.Combine(dir, "video.mp4")) || take.Frames.DistinctBy(f => f.FileName).Any(f => !File.Exists(Path.Combine(dir, f.FileName))) ||
            take.RefinementPackage is not null && !File.Exists(Path.Combine(dir, H3RefinementPackage.FileName)))
            throw new WorkspaceStoreException("The take archive is incomplete. Retry downloading without regenerating.");
    }
    public async Task<ShotDocument> DiscardAsync(Guid projectId, Guid mediaId, ShotTrashKind kind, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct); Revision(d, expectedRevision);
        if (kind == ShotTrashKind.Take)
        {
            var take = d.Takes.SingleOrDefault(t => t.Id == mediaId) ?? throw new WorkspaceStoreException("Take no longer available.");
            var owner = d.Shots.Single(s => s.Id == take.ShotId);
            if (owner.SelectedTakeId == take.Id) throw new WorkspaceStoreException("Deselect the production take before discarding it.");
            if (d.Shots.FirstOrDefault(s => s.StartFrame?.TakeId == take.Id) is { } continuation)
                throw new WorkspaceStoreException($"“{continuation.Title}” starts from a frame of this take. Remove its starting frame before discarding the take.");
            TrashTake(d, take, owner); d.Takes.Remove(take);
        }
        else throw new WorkspaceStoreException("Unsupported media type.");
        return await Publish(dir, d, ct);
    }
    public async Task<ShotDocument> RestoreAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct); Revision(d, expectedRevision); var entries = Selected(d, trashIds);
        foreach (var t in entries)
        {
            if (t.Purging || t.ExpiresUtc <= clock.GetUtcNow()) throw new WorkspaceStoreException("This media has expired or is being purged.");
            if (t.Take is { } take)
            {
                var folder = Path.Combine(dir, "shots", "takes", take.Directory);
                RequireFiles(folder, take);
                if (take.RefinementPackage is { } package)
                    await RefinementPackages.VerifyFileAsync(Path.Combine(folder, H3RefinementPackage.FileName), package.Bytes, package.Sha256, ct);
            }
        }
        foreach (var t in entries.OrderBy(t => t.Position))
        {
            if (t.Take is { } take)
            {
                if (d.Shots.All(s => s.Id != take.ShotId)) d.Shots.Insert(t.OwnerPosition < 0 ? d.Shots.Count : Math.Min(t.OwnerPosition, d.Shots.Count), t.Owner!.Copy());
                d.Takes.Insert(Math.Clamp(t.Position, 0, d.Takes.Count), take);
            }
            d.Trash.Remove(t);
        }
        return await Publish(dir, d, ct);
    }
    private static List<ShotTrashEntry> Selected(ShotDocument d, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new WorkspaceStoreException("Choose distinct Trash items.");
        var selected = d.Trash.Where(t => ids.Contains(t.Id)).ToList();
        if (selected.Count != ids.Count) throw new WorkspaceStoreException("Trash changed. Refresh and retry."); return selected;
    }
    public async Task<ShotDocument> PurgeAsync(Guid projectId, IReadOnlyCollection<Guid> trashIds, long expectedRevision, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, projectId, ct); Revision(d, expectedRevision);
        foreach (var item in Selected(d, trashIds)) { item.Purging = true; item.Error = null; }
        d = await Publish(dir, d, ct);
        foreach (var item in Selected(d, trashIds))
        {
            try
            {
                if (item.Take is { } t)
                {
                    var folder = Path.GetFullPath(Path.Combine(dir, "shots", "takes", t.Directory));
                    if (!folder.StartsWith(Path.GetFullPath(Path.Combine(dir, "shots", "takes")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid take path.");
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                d.Trash.Remove(item);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { item.Error = "Cleanup failed. Close apps using these files and retry."; }
        }
        return await Publish(dir, d, ct);
    }
    public async Task<AssetMedia?> OpenAsync(Guid projectId, Guid mediaId, ShotTrashKind kind, int? frame = null, bool trash = false, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct);
        string path, type; DateTimeOffset created;
        if (kind == ShotTrashKind.Take)
        {
            var take = trash
                ? (await Read(dir, projectId, ct)).Trash.FirstOrDefault(t => t.Id == mediaId && !t.Purging && t.ExpiresUtc > clock.GetUtcNow())?.Take
                : await files.MediaIndex.FindAsync<ShotTake>(Manifest(dir), mediaId.ToString("D"),
                    async token => (await Read(dir, projectId, token)).Takes.ToDictionary(t => t.Id.ToString("D")), ct);
            if (take is null || frame is < 0 || frame >= take.FrameCount) return null;
            path = Path.Combine(dir, "shots", "takes", take.Directory, "video.mp4");
            type = frame.HasValue ? "image/png" : "video/mp4"; created = take.CreatedUtc;
            if (frame is { } f)
            {
                try { return new(await (frameReader ?? TakeFrameReader.Shared).OpenAsync(Path.GetDirectoryName(path)!, take, f, ct), type, created); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or WorkspaceStoreException or SixLabors.ImageSharp.InvalidImageContentException or SixLabors.ImageSharp.UnknownImageFormatException) { return null; }
            }
        }
        else return null;
        try { return new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), type, created); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
    public async Task<string> RunDirectoryAsync(Guid projectId, Guid runId, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) throw new WorkspaceStoreException("Invalid run ID.");
        return Path.Combine(await files.DirectoryAsync(projectId, ct), "shots", "runs", runId.ToString("D"));
    }
    public async Task<TakeRefinement> CaptureRefinementAsync(Guid projectId, Guid takeId, Guid runId, TakeRefinementMode mode, int width, int height, string upscaler,
        H3UpscalerImplementation implementation, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var doc = await Read(dir, projectId, ct);
        var take = doc.Takes.SingleOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("Restore the source take from Trash before refining it.");
        var package = take.RefinementPackage ?? throw new WorkspaceStoreException(TakeDisplay.NoLatents);
        var source = Path.Combine(dir, "shots", "takes", take.Directory);
        var checkedPackage = await RefinementPackages.InspectAsync(Path.Combine(source, H3RefinementPackage.FileName), take.Snapshot, take.Refinement, ct);
        if (checkedPackage != package) throw new WorkspaceStoreException("The source refinement package changed or is corrupt.");
        var target = Path.Combine(await RunDirectoryAsync(projectId, runId, ct), "inputs");
        if (Directory.Exists(target)) throw new WorkspaceStoreException("This refinement request already has captured inputs. Retry its saved enqueue request.");
        Directory.CreateDirectory(target);
        async Task Copy(string input, string output)
        {
            await using (var from = File.OpenRead(Path.Combine(source, input)))
            await using (var to = File.Create(Path.Combine(target, output + ".tmp"))) { await from.CopyToAsync(to, ct); await to.FlushAsync(ct); to.Flush(true); }
            File.Move(Path.Combine(target, output + ".tmp"), Path.Combine(target, output));
        }
        await Copy(H3RefinementPackage.FileName, H3RefinementPackage.FileName);
        if (take.RetainedSource is { } retained) {
            foreach (var input in retained.Inputs) {
                var inputPath = Path.Combine(source, TakeTrimming.InputsFolder, input.FileName);
                await RefinementPackages.VerifyFileAsync(inputPath, input.Bytes, input.Sha256, ct);
                await Copy(Path.Combine(TakeTrimming.InputsFolder, input.FileName), input.FileName);
            }
        }
        foreach (var input in take.Snapshot.Motion?.Files ?? []) {
            await RefinementPackages.VerifyFileAsync(Path.Combine(source, TakeTrimming.InputsFolder, input.FileName), input.Bytes, input.Sha256, ct);
            await Copy(Path.Combine(TakeTrimming.InputsFolder, input.FileName), input.FileName);
        }
        if (take.Composition is not null) {
            if ((width, height) != (take.Width, take.Height)) throw new WorkspaceStoreException("Extension refinement keeps the combined take's dimensions.");
            var capture = ShotCopy.Of(take); capture.Extension = null;
            await TakeBundles.CopyAsync(capture, source, Path.Combine(await RunDirectoryAsync(projectId, runId, ct), H3Motion.SourceFolder), ct);
        }
        var captured = new TakeRefinement(take.Id, package, mode, width, height, upscaler, implementation);
        RefinementPolicy.Validate(captured, take.Snapshot);
        return captured;
    }
    private static void ValidateRun(VideoRun run)
    {
        if (run.Id == Guid.Empty || run.Snapshot is null || run.Snapshot.ProjectId == Guid.Empty || run.Snapshot.Prompt is null || run.Snapshot.Fingerprint is null ||
            run.Candidates is not { Count: > 0 } || run.Inputs is null || run.Candidates.Any(c => c is null || c.Number < 1 || c.TakeId == Guid.Empty || !Enum.IsDefined(c.State) ||
                !Guid.TryParse(c.ClientId, out _) || c.PromptId is not null && !Guid.TryParse(c.PromptId, out _)) ||
            run.Candidates.Select(c => c.Number).Distinct().Count() != run.Candidates.Count || run.Candidates.Select(c => c.TakeId).Distinct().Count() != run.Candidates.Count ||
            run.Inputs.Any(i => i is null || string.IsNullOrWhiteSpace(i.FileName) || i.FileName != Path.GetFileName(i.FileName) || i.FileName.Contains('\\') || i.FileName.Contains('/') || Path.GetExtension(i.FileName) != lumibelle.Services.AI.AiVideoJobPolicy.Extension(i.EffectiveKind)))
            throw new WorkspaceStoreException("Invalid video run record. It has not been submitted.");
        H3Policy.Validate(run.Snapshot.Shot, true, motionContext: run.Snapshot.Motion is not null); H3Policy.ValidateSettings(run.Snapshot.Settings);
        H3PreviewUpscaling.Validate(run.Snapshot);
        if (run.Snapshot.Appearances is null || run.Snapshot.Appearances.Any(a => a is null || a.Start is null) ||
            !run.Snapshot.Shot.Characters.Where(c => c.Appearance is not null).Select(c => c.Id).SequenceEqual(run.Snapshot.Appearances.Select(a => a.CharacterId)))
            throw new WorkspaceStoreException("Invalid captured character appearances.");
        if (run.Snapshot.ReferenceGuidance is null || run.Snapshot.ReferenceGuidance.Any(g => g is null || g.AssetDefault is null || g.ImageDefault is null) ||
            run.Snapshot.Profile == H3Policy.Profile && !run.Snapshot.ReferenceGuidance.Select(g => g.BindingId).SequenceEqual(run.Snapshot.Shot.Images.Select(i => i.Id)))
            throw new WorkspaceStoreException("Invalid captured reference guidance.");
        if (run.Snapshot.FrameCount != H3Policy.Frames(run.Snapshot.Shot.Duration!.Value) || run.Snapshot.Width < 32 || run.Snapshot.Height < 32 ||
            run.Snapshot.Width % 32 != 0 || run.Snapshot.Height % 32 != 0 || (long)run.Snapshot.Width * run.Snapshot.Height > 1048576)
            throw new WorkspaceStoreException("Invalid captured video dimensions or duration.");
    }
    public async Task SaveRunAsync(VideoRun run, CancellationToken ct = default)
    {
        ValidateRun(run);
        var directory = await RunDirectoryAsync(run.Snapshot.ProjectId, run.Id, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        for (var attempt = 0; ; attempt++)
        {
            try { await AtomicJsonFile.WriteAsync(Path.Combine(directory, "run.json"), run.Copy(), ct); break; }
            catch (WorkspaceStoreException e) when (attempt < 4 && e.InnerException is IOException or UnauthorizedAccessException)
            { await Task.Delay(50 * (attempt + 1), ct); }
        }
    }
    public async Task<IReadOnlyList<VideoRun>> RunsAsync(Guid projectId, CancellationToken ct = default)
    {
        var root = Path.Combine(await files.DirectoryAsync(projectId, ct), "shots", "runs"); if (!Directory.Exists(root)) return [];
        List<VideoRun> result = [];
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out var id)) continue;
            using var gate = await ProjectFiles.LockAsync(dir, ct);
            var r = await AtomicJsonFile.ReadAsync<VideoRun>(Path.Combine(dir, "run.json"), ct);
            if (r is null) continue;
            if (r.Id != id || r.Snapshot.ProjectId != projectId) throw new WorkspaceStoreException("Invalid video run identity.");
            ValidateRun(r); result.Add(r);
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToList();
    }
}
