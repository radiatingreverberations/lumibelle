using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using lumibelle.Services.Shots;

namespace lumibelle.Services.Assets;

public interface IAssetReelStore
{
    Task<ReferenceReelDraft?> StartFreshAfterSuccessAsync(AiJobHeader job, ReferenceReelDraft submitted, CancellationToken ct = default)
        => Task.FromResult<ReferenceReelDraft?>(null);
    Task<AssetLibrary> SaveKeyframesAsync(Guid project, AssetReferenceReel baseline, ReelKeyframeSet frames, long expectedRevision, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reel keyframe saving is unavailable.");
    Task<ReferenceReelDraft> SaveDraftAsync(Guid project, ReferenceReelDraft draft, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> SaveReelAsync(Guid project, AssetReferenceReel reel, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> EditReelDetailsAsync(Guid project, AssetReferenceReel baseline, string name, string guidance, long expectedRevision, CancellationToken ct = default, Guid? lookId = null, bool updateLook = false);
    Task<AssetLibrary> TrashReelAsync(Guid project, Guid reel, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> RestoreReelAsync(Guid project, Guid reel, long expectedRevision, CancellationToken ct = default);
    Task<AssetLibrary> PublishReelAsync(Guid project, AssetReferenceReel reel, CancellationToken ct = default);
    Task<bool> ApplyPairAsync(AiJobHeader job, ReelCompositionRequest request, ReelPromptPair pair, bool initialOnly, CancellationToken ct = default, bool acceptChangedInputs = false);
    Task<string> RunDirectoryAsync(Guid project, Guid batch, CancellationToken ct = default);
    Task<AssetLibrary> PurgeReelsAsync(Guid project, IReadOnlyCollection<Guid> reels, long expectedRevision, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Deleting reels permanently is unavailable.");
    Task<IReadOnlyDictionary<Guid, long>> ReelTrashBytesAsync(Guid project, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, long>>(new Dictionary<Guid, long>());
}

public sealed partial class FileAssetStore : IAssetReelStore
{
    public async Task<ReferenceReelDraft?> StartFreshAfterSuccessAsync(AiJobHeader job, ReferenceReelDraft submitted, CancellationToken ct = default)
    {
        if (job.Kind != AiJobKind.ReelVideo || job.State != AiJobState.Completed || job.CancelRequested || job.RemoteUnconfirmed ||
            job.Error is not null || job.Batch is not { Candidates.Count: > 0 } batch || batch.RootId != job.Id ||
            job.Target.ProjectId is not { } project || job.Target.AssetId != submitted.AssetId || job.Target.ReelId != submitted.Id) return null;
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct);
        var draft = current.ReelDrafts.LastOrDefault(d => d.AssetId == submitted.AssetId);
        if (draft?.Id != submitted.Id || draft.ResolvedJobs.Contains(job.Id) || ReferenceReels.Fingerprint(draft) != ReferenceReels.Fingerprint(submitted) ||
            !batch.Candidates.All(c => current.ReelPublications.Any(p => p.JobId == job.Id && p.ReelId == c.Id && p.Candidate == c.Number)) ||
            current.Assets.FirstOrDefault(a => a.Id == submitted.AssetId) is not { } owner) return null;
        // Keep the used recipe for request review. A new identity prevents old
        // completions, retries and One more from clearing the next draft.
        var completed = draft.Copy(); completed.ResolvedJobs.Add(job.Id); completed.Revision++;
        var fresh = ReferenceReels.NewDraft(owner, library: current); fresh.Revision = 1;
        if (draft.GenerationSetup is { } preset) ReelGenerationSetups.Apply(fresh, preset);
        await PublishAsync(directory, current with { ReelDrafts = [.. current.ReelDrafts.Where(d => d.Id != draft.Id), completed, fresh] }, current.Revision, ct);
        return fresh.Copy();
    }
    public async Task<AssetLibrary> SaveKeyframesAsync(Guid project, AssetReferenceReel baseline, ReelKeyframeSet frames, long expectedRevision, CancellationToken ct = default)
    {
        ReferenceVideos.ValidateKeyframes(baseline.Media, frames);
        frames = ShotCopy.Of(frames);
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct); EnsureRevision(current, expectedRevision);
        var old = current.Reels.SingleOrDefault(r => r.Id == baseline.Id && r.AssetId == baseline.AssetId) ?? throw new WorkspaceConflictException();
        bool Same(ReelKeyframeSet? a, ReelKeyframeSet? b) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(b, AtomicJsonFile.Options));
        if (Same(old.Keyframes, frames)) return current;
        if (old.Media != baseline.Media || !Same(old.Keyframes, baseline.Keyframes)) throw new WorkspaceStoreException("The reel's keyframes changed elsewhere. Your picks are retained; reopen the editor to review the saved set.");
        var updated = old with { Keyframes = frames };
        return await PublishAsync(directory, current with { Reels = current.Reels.Select(r => r.Id == old.Id ? updated : r).ToList() }, current.Revision, ct);
    }
    public async Task<string> RunDirectoryAsync(Guid project, Guid batch, CancellationToken ct = default) =>
        Path.Combine(await files.DirectoryAsync(project, ct), "reel-runs", batch.ToString("D"));

    public async Task<ReferenceReelDraft> SaveDraftAsync(Guid project, ReferenceReelDraft draft, long expectedRevision, CancellationToken ct = default)
    {
        draft = draft.Copy(); ReferenceReels.Validate(draft);
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct); ReferenceReels.ValidateOwner(draft, ReelOwner(current, draft.AssetId, draft.LookId));
        var old = current.ReelDrafts.SingleOrDefault(d => d.Id == draft.Id);
        if ((old?.Revision ?? 0) != expectedRevision || old is not null && (old.AssetId != draft.AssetId || old.LookId != draft.LookId)) throw new WorkspaceConflictException();
        draft.Revision = expectedRevision + 1;
        await PublishAsync(directory, current with { ReelDrafts = [.. current.ReelDrafts.Where(d => d.Id != draft.Id), draft] }, current.Revision, ct);
        return draft.Copy();
    }
    private static ReferenceAsset ReelOwner(AssetLibrary library, Guid assetId, Guid? lookId, bool allowArchived = false)
    {
        var asset = library.Assets.SingleOrDefault(a => a.Id == assetId && ReferenceReels.Supports(a.Category))
            ?? throw new WorkspaceStoreException("Restore the character, environment or prop before saving or generating reels.");
        if (lookId is not null && (asset.Category != AssetCategory.Character || !asset.Looks.Any(l => l.Id == lookId && (allowArchived || !l.Archived))))
            throw new WorkspaceStoreException("Choose an available look, or unarchive this look.");
        return asset;
    }
    public async Task<AssetLibrary> SaveReelAsync(Guid project, AssetReferenceReel reel, long expectedRevision, CancellationToken ct = default)
    {
        reel = ShotCopy.Of(reel);
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct); EnsureRevision(current, expectedRevision);
        var old = current.Reels.SingleOrDefault(r => r.Id == reel.Id);
        ReelOwner(current, reel.AssetId, reel.LookId, allowArchived: old is not null && old.LookId == reel.LookId);
        if (old is not null && (old.Media != reel.Media || old.AssetId != reel.AssetId || old.OriginalAssetId != reel.OriginalAssetId ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(old.Generation, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(reel.Generation, AtomicJsonFile.Options)) || old.SourceTakeId != reel.SourceTakeId || old.CreatedUtc != reel.CreatedUtc))
            throw new WorkspaceStoreException("Captured reel media and provenance cannot be changed. Import or generate a new clip.");
        if (old is null && (reel.Generation is not null || reel.OriginalAssetId is not null || current.ReelTrash.Any(t => t.Reel.Id == reel.Id)))
            throw new WorkspaceStoreException("Use publication or recovery to restore this reel.");
        return await PublishAsync(directory, current with { Reels = [.. current.Reels.Where(r => r.Id != reel.Id), reel] }, current.Revision, ct);
    }
    public async Task<AssetLibrary> EditReelDetailsAsync(Guid project, AssetReferenceReel baseline, string name, string guidance, long expectedRevision, CancellationToken ct = default, Guid? lookId = null, bool updateLook = false)
    {
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct); EnsureRevision(current, expectedRevision);
        var old = current.Reels.SingleOrDefault(r => r.Id == baseline.Id) ?? throw new WorkspaceConflictException();
        if (old.AssetId != baseline.AssetId) throw new WorkspaceStoreException("This reel was moved to another asset. Reopen its details there before saving.");
        // Rebase only the fields edited in this dialog. An unrelated edit in another
        // tab is retained; competing edits of the same field remain inspectable.
        static string Merge(string before, string edited, string saved, string field)
        {
            if (edited == before) return saved;
            if (saved != before && saved != edited)
                throw new WorkspaceStoreException($"The reel's {field} changed elsewhere. Your edits are retained. Close and reopen Details to review the saved version before trying again.");
            return edited;
        }
        var targetLook = old.LookId;
        if (updateLook && lookId != baseline.LookId) {
            if (old.LookId != baseline.LookId && old.LookId != lookId) throw new WorkspaceConflictException();
            ReelOwner(current, old.AssetId, lookId); targetLook = lookId;
        }
        var updated = old with { LookId = targetLook, Name = Merge(baseline.Name, name, old.Name, "name"), UseGuidance = Merge(baseline.UseGuidance, guidance, old.UseGuidance, "use guidance") };
        return await PublishAsync(directory, current with { Reels = current.Reels.Select(r => r.Id == old.Id ? updated : r).ToList() }, current.Revision, ct);
    }
    public async Task<AssetLibrary> PublishReelAsync(Guid project, AssetReferenceReel reel, CancellationToken ct = default)
    {
        reel = ShotCopy.Of(reel);
        var generation = reel.Generation ?? throw new WorkspaceStoreException("Missing captured reel generation.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(reel, AtomicJsonFile.Options)));
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct);
        if (current.ReelPublications.SingleOrDefault(r => r.ReelId == reel.Id) is { } receipt)
        {
            if (receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This reel candidate was already published with different content.");
            return current;
        }
        if (!current.Assets.Any(a => a.Id == reel.AssetId))
        {
            // The author may remove the destination while remote inference runs.
            // Retain the completed candidate in recovery without recreating it in
            // the active library or repeating inference.
            var owner = generation.Snapshot.Reel?.Owner ?? throw new WorkspaceStoreException("Restore the character, environment or prop before publishing this reel.");
            ReferenceReels.ValidateOwner(generation.Recipe, owner);
            return await PublishAsync(directory, current with {
                ReelTrash = [.. current.ReelTrash, TrashedReferenceReel.Removed(reel, owner with { Images = [], DefaultVoiceId = null }, clock.GetUtcNow())],
                ReelPublications = [.. current.ReelPublications, new(reel.Id, generation.JobId, generation.BatchId, generation.Candidate, fingerprint)]
            }, current.Revision, ct);
        }
        ReferenceReels.ValidateOwner(generation.Recipe, ReelOwner(current, reel.AssetId, reel.LookId, allowArchived: true));
        return await PublishAsync(directory, current with { Reels = [.. current.Reels, reel], ReelPublications = [.. current.ReelPublications,
            new(reel.Id, generation.JobId, generation.BatchId, generation.Candidate, fingerprint)] }, current.Revision, ct);
    }
    public async Task<AssetLibrary> TrashReelAsync(Guid project, Guid reel, long expectedRevision, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct); EnsureRevision(current, expectedRevision);
        var item = current.Reels.SingleOrDefault(r => r.Id == reel) ?? throw new WorkspaceConflictException();
        return await PublishAsync(directory, current with { Reels = current.Reels.Where(r => r.Id != reel).ToList(),
            ReelTrash = [.. current.ReelTrash, TrashedReferenceReel.Removed(item, ReelOwner(current, item.AssetId, item.LookId, true) with { Images = [], DefaultVoiceId = null }, clock.GetUtcNow())] }, current.Revision, ct);
    }
    public async Task<AssetLibrary> RestoreReelAsync(Guid project, Guid reel, long expectedRevision, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, project, ct); EnsureRevision(current, expectedRevision);
        var item = current.ReelTrash.SingleOrDefault(r => r.Reel.Id == reel) ?? throw new WorkspaceConflictException();
        if (item.Purging) throw new WorkspaceStoreException("This reel is being deleted permanently.");
        var owner = current.Assets.FirstOrDefault(a => a.Id == item.Reel.AssetId);
        var restored = owner is null ? item.Owner with { Images = [], DefaultVoiceId = null } : owner with { Looks = LookPolicy.RestoreLooks(owner, item.Owner, item.Reel.LookId) };
        return await PublishAsync(directory, current with { Assets = [.. current.Assets.Where(a => a.Id != restored.Id), restored],
            Reels = [.. current.Reels, item.Reel], ReelTrash = current.ReelTrash.Where(r => r.Reel.Id != reel).ToList() }, current.Revision, ct);
    }
    // Automatic application (initialOnly) fills an empty recipe only when nothing changed. An explicit
    // Apply names changes since the request and applies them when the author accepts (AssistedApply).
    public async Task<bool> ApplyPairAsync(AiJobHeader job, ReelCompositionRequest request, ReelPromptPair pair, bool initialOnly, CancellationToken ct = default, bool acceptChangedInputs = false)
    {
        if (job.Kind != AiJobKind.ReelComposition || job.State != AiJobState.Completed || job.CancelRequested ||
            job.Target.ProjectId != request.ProjectId || job.Target.AssetId != request.Draft.AssetId || job.Target.ReelId != request.Draft.Id) return false;
        ReferenceReels.ValidatePair(pair, request.Draft);
        var directory = await files.DirectoryAsync(request.ProjectId, ct); using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, request.ProjectId, ct);
        var draft = current.ReelDrafts.SingleOrDefault(d => d.Id == request.Draft.Id);
        // A resolved or superseded request is never applied.
        if (draft is null || draft.ResolvedJobs.Contains(job.Id) || draft.PendingJobId != job.Id ||
            initialOnly && (request.Draft.Prompt.Length != 0 || request.Draft.UseGuidance.Length != 0)) return false;
        var owner = current.Assets.FirstOrDefault(a => a.Id == draft.AssetId);
        if (owner is null) return false;
        if (LookPolicy.Find(owner, draft.LookId)?.Archived == true)
        { if (initialOnly) return false; throw new WorkspaceStoreException("This recipe’s look is archived. Restore the look before applying the pair."); }
        ReferenceReels.ValidateOwner(draft, owner);
        var images = await ProductionInputs.CaptureAsync(request.ProjectId, ReferenceReels.Inputs(draft), this, ct, referenceVideos, settings is null ? null : (await settings.LoadAsync(ct)).H3);
        var changes = ReferenceReels.PairChanges(request, draft, LookPolicy.Capture(owner, draft.LookId), images.Select(i => i.Identity),
            ShotReferences.Resolve(ReferenceReels.Inputs(draft), current, new()));
        if (!AssistedApply.Allows(changes, initialOnly, acceptChangedInputs, "the recipe")) return false;
        var next = draft.Copy(); next.Prompt = pair.Prompt; next.UseGuidance = pair.UseGuidance;
        // Texts written for other inputs stay flagged for checking; replacing edited text does not.
        next.CheckedInputs = changes.Any(c => c != ReferenceReels.PairTextEdited) ? null : ReferenceReels.InputsFingerprint(next);
        next.Revision++; next.ResolvedJobs.Add(job.Id);
        await PublishAsync(directory, current with { ReelDrafts = current.ReelDrafts.Select(d => d.Id == next.Id ? next : d).ToList() }, current.Revision, ct);
        return true;
    }
    private static void ValidateReels(AssetLibrary library)
    {
        if (library.Reels is null || library.ReelDrafts is null || library.ReelTrash is null || library.ReelPublications is null ||
            library.Reels.Select(r => r.Id).Concat(library.ReelTrash.Select(t => t.Reel.Id)).Distinct().Count() != library.Reels.Count + library.ReelTrash.Count ||
            library.ReelDrafts.Select(d => d.Id).Distinct().Count() != library.ReelDrafts.Count || library.ReelPublications.Select(r => r.ReelId).Distinct().Count() != library.ReelPublications.Count)
            throw new WorkspaceStoreException("Invalid reference reel identities.");
        foreach (var draft in library.ReelDrafts) ReferenceReels.Validate(draft);
        foreach (var active in library.Reels) {
            var owner = ReelOwner(library, active.AssetId, active.LookId, allowArchived: true);
            if (active.Generation is { } generation) ReferenceReels.ValidateOwner(generation.Recipe, owner with { Id = active.OriginalAssetId ?? active.AssetId });
        }
        if (library.ReelTrash.Any(t => t.ExpiresUtc is { } expires && expires - t.DeletedUtc != TimeSpan.FromDays(30)))
            throw new WorkspaceStoreException("Invalid reel Trash expiry.");
        foreach (var r in library.Reels.Concat(library.ReelTrash.Select(t => t.Reel)))
        {
            if (r.Id == Guid.Empty || r.AssetId == Guid.Empty || r.OriginalAssetId == Guid.Empty || r.LookId == Guid.Empty || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 500 || r.UseGuidance is null || r.UseGuidance.Length > 20000)
                throw new WorkspaceStoreException("Name each reel and keep use guidance under 20000 characters.");
            ReferenceVideos.ValidateMedia(r.Media);
            if (r.Generation is { } generation && (generation.Recipe.AssetId != (r.OriginalAssetId ?? r.AssetId) || generation.BatchId == Guid.Empty || generation.JobId == Guid.Empty || generation.Candidate < 1))
                throw new WorkspaceStoreException("Invalid captured reel destination or candidate.");
        }
    }
}
