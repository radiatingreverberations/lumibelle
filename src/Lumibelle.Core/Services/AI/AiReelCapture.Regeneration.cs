using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiReelCapture
{
    public Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid project, Guid reelId,
        VideoResolution resolution, CancellationToken ct = default)
        => CaptureRegenerationAsync(id, tab, project, reelId, resolution, true, null, ct);

    public Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid project, Guid reelId,
        VideoResolution resolution, bool keepSeed, long? seed, CancellationToken ct = default)
        => CaptureRegenerationAsync(id, tab, project, reelId, resolution, keepSeed, seed, 1, ct);

    public Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid project, Guid reelId,
        VideoResolution resolution, bool keepSeed, long? seed, int count, CancellationToken ct = default)
        => CaptureRegenerationAsync(id, tab, project, reelId, resolution, keepSeed, seed, count, null, ct);

    // saveLosslessFrames null keeps the source reel's choice.
    public Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid project, Guid reelId,
        VideoResolution resolution, bool keepSeed, long? seed, int count, bool? saveLosslessFrames, CancellationToken ct = default)
        => CaptureRegenerationAsync(id, tab, project, reelId, resolution, keepSeed, seed, count, saveLosslessFrames, null, ct);

    public async Task<AiJobSubmission> CaptureRegenerationAsync(Guid id, Guid tab, Guid project, Guid reelId,
        VideoResolution resolution, bool keepSeed, long? seed, int count, bool? saveLosslessFrames, bool? saveLatents, CancellationToken ct = default)
    {
        if (keepSeed && count != 1) throw new WorkspaceStoreException("Using the original seed requires exactly one take.");
        var store = jobs ?? throw new WorkspaceStoreException("Saved reel requests are unavailable.");
        var library = await assets.LoadAsync(project, ct);
        var reel = library.Reels.SingleOrDefault(r => r.Id == reelId)
            ?? throw new WorkspaceStoreException("Restore the source reel before regenerating it.");
        var generation = reel.Generation ?? throw new WorkspaceStoreException("This imported reel has no captured seed or request.");
        var batch = AiBatchDefinition.Create(id, count, keepSeed ? generation.Seed : seed);
        var source = (await store.ReadSnapshotAsync(generation.JobId, ct)).Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)
            ?? throw new WorkspaceStoreException("The source reel's captured request is unavailable.");
        AiVideoJobPolicy.Validate(source);
        var sourceJob = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == generation.JobId);
        if (source.Version != 3 || source.Snapshot.ProjectId != project || source.BatchId != generation.BatchId ||
            sourceJob?.Batch?.Candidates.Any(c => c.Id == reel.Id && c.Number == generation.Candidate && c.Seed == generation.Seed) != true ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source.Snapshot, AtomicJsonFile.Options),
                JsonSerializer.SerializeToElement(generation.Snapshot, AtomicJsonFile.Options)))
            throw new WorkspaceStoreException("The source reel does not match its captured request.");

        var owner = library.Assets.SingleOrDefault(a => a.Id == reel.AssetId)
            ?? throw new WorkspaceStoreException("Restore the reel's character, environment or prop first.");
        var recipe = source.Snapshot.Reel!.Recipe.Copy() with {
            Id = id, Revision = 0, PendingJobId = null, ResolvedJobs = [], CheckedInputs = null,
            AssetId = reel.AssetId, LookId = reel.LookId, Name = reel.Name
        };
        VideoResolutions.Select(recipe, resolution);
        if (saveLatents is { } keepLatents) recipe.SaveLatents = keepLatents;
        if (recipe.GenerationSetup is not null || recipe.OutputOverrides is not null)
            recipe.OutputOverrides = new() { TakeCount = count, Resolution = resolution };
        if (saveLosslessFrames is { } keepFrames && keepFrames != (source.Snapshot.OutputPolicy?.SaveLosslessFrames ?? true))
        {
            // Requests captured before output policies always kept frames and cannot record another choice.
            if (source.Snapshot.OutputPolicy is null) throw new WorkspaceStoreException("This reel's saved request always keeps lossless frames.");
            recipe.SaveLosslessFrames = keepFrames;
        }
        ReferenceReels.ValidateOwner(recipe, owner);
        var character = LookPolicy.Capture(owner, recipe.LookId);
        LookPolicy.ValidateTarget(library, character);
        var shot = ReferenceReels.Inputs(recipe);
        var size = VideoResolutions.Size(recipe);
        var snapshot = ShotCopy.Of(source.Snapshot) with {
            SourceRevision = 0, Shot = shot, Width = size.Width, Height = size.Height, Fingerprint = VideoResolutions.Fingerprint(recipe), PreviewUpscale = null,
            OutputPolicy = source.Snapshot.OutputPolicy is null ? null : new(shot.SaveLosslessFrames),
            CaptureRefinementData = shot.SaveLatents && !shot.UpscalePreview,
            Reel = new(recipe, character) { Owner = owner with { Images = [] },
                RegenerationSource = new(reel.Id, generation.Seed, source.Snapshot.Width, source.Snapshot.Height) }
        };
        var request = source with { BatchId = id, Snapshot = snapshot, Inputs = ShotCopy.Of(source.Inputs), Recipe = null };
        AiVideoJobPolicy.Validate(request);
        var configured = ShotCopy.Of(await settings.LoadAsync(ct));
        var target = AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl);
        var check = await generator.CheckAsync(new AiSettings { ComfyUrl = target, H3 = snapshot.Settings }, ct);
        if (!ReadyToSubmit(snapshot with { TargetComfyUrl = target }, check, out var currentIssue) &&
            target != snapshot.ComfyUrl)
        {
            var fallback = await generator.CheckAsync(new AiSettings { ComfyUrl = snapshot.ComfyUrl, H3 = snapshot.Settings }, ct);
            if (ReadyToSubmit(snapshot, fallback, out _))
            {
                check = fallback;
                target = snapshot.ComfyUrl;
            }
            else if (currentIssue is not null)
            {
                throw new WorkspaceStoreException($"The current ComfyUI connection could not run this reel, and the captured server was also unavailable. " +
                    $"Current ({target}): {currentIssue} Captured ({snapshot.ComfyUrl}): {fallback.Message}");
            }
        }
        if (!ReadyToSubmit(snapshot with { TargetComfyUrl = target }, check, out var issue)) throw new WorkspaceStoreException(issue ?? check.Message);
        snapshot = snapshot with { TargetComfyUrl = target == snapshot.ComfyUrl ? null : target };
        request = request with { Snapshot = snapshot };
        H3Presets.CheckSubmission(snapshot, check);
        H3Loras.CheckSubmission(snapshot, check.OptionalLoras);

        var sourceDirectory = await reels.RunDirectoryAsync(project, source.BatchId, ct);
        var directory = await reels.RunDirectoryAsync(project, id, ct);
        if (source.BatchId == id) throw new WorkspaceStoreException("Regeneration needs a new request identity.");
        await AiVideoJobPolicy.CopyPreparedInputsAsync(source, sourceDirectory, request, directory, ct);
        var current = await assets.LoadAsync(project, ct);
        if (current.Reels.SingleOrDefault(r => r.Id == reel.Id) is not { } latest || latest.AssetId != reel.AssetId || latest.LookId != reel.LookId)
            throw new WorkspaceConflictException();
        LookPolicy.ValidateTarget(current, character);
        var name = (await projects.GetAsync(project, ct))?.Name ?? throw new WorkspaceStoreException("Project unavailable.");
        request = await H3GenerationRecipes.StampAsync(request, generator, ct);
        return AiJobSubmission.Create(id, AiJobKind.ReelVideo, AiBackend.ComfyUI, new(project, recipe.AssetId, ReelId: recipe.Id),
            name, recipe.Name + " · Regenerate · " + size.Width + " × " + size.Height, tab, request)
            with { Batch = batch };
    }

    private static bool ReadyToSubmit(VideoSnapshot snapshot, H3Configuration check, out string? issue)
    {
        try
        {
            H3Presets.CheckSubmission(snapshot, check);
            H3Loras.CheckSubmission(snapshot, check.OptionalLoras);
            issue = null;
            return true;
        }
        catch (WorkspaceStoreException e) { issue = e.Message; return false; }
    }
}
