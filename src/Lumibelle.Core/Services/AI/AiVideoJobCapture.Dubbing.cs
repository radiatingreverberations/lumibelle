using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiVideoJobCapture
{
    public async Task<AiJobSubmission> CaptureDubAsync(Guid id, Guid tab, Guid projectId, Guid variantId,
        long expectedVersion, CancellationToken ct = default)
    {
        var dubStore = dubbing ?? throw new WorkspaceStoreException("Language-variant storage is unavailable.");
        var variant = (await dubStore.LoadAsync(projectId, ct)).Variants.SingleOrDefault(v => v.Id == variantId)
            ?? throw new WorkspaceStoreException("Save and review the translated dialogue before generating.");
        if (variant.Version != expectedVersion) throw new WorkspaceConflictException();
        ShotDubbing.CheckLanguages(await dubStore.LanguagesAsync(projectId, ct), variant.Request);
        var source = await dubStore.SourceAsync(projectId, variant.Request.SourceTakeId, ct);
        var snapshot = ShotDubbing.Apply(source.Request.Snapshot, variant) with { MotionReference = null };
        if (snapshot.Production is { } setup && production is not null &&
            (await production.LoadAsync(projectId, ct)).Compositions.All(c => c.Id != setup.CompositionId || c.Archived || c.GenerationSetupArchived))
            throw new WorkspaceStoreException("Restore the master take's setup before rendering its language version.");
        // Keep the model configuration and server captured by the master. This is not an audio-only post-process.
        var check = await generator.CheckAsync(new AiSettings { ComfyUrl = snapshot.ExecutionComfyUrl, H3 = snapshot.Settings }, ct);
        H3Presets.CheckSubmission(snapshot, check); H3Loras.CheckSubmission(snapshot, check.OptionalLoras);
        var request = source.Request with { Version = 2, BatchId = id, Snapshot = snapshot, Inputs = ShotCopy.Of(source.Request.Inputs), Recipe = null,
            DestinationShotId = source.Take.ShotId != snapshot.Shot.Id ? source.Take.ShotId : null };
        AiVideoJobPolicy.Validate(request);
        var directory = await shots.RunDirectoryAsync(projectId, id, ct);
        var sourceDirectory = await shots.RunDirectoryAsync(projectId, source.Request.BatchId, ct);
        await AiVideoJobPolicy.CopyPreparedInputsAsync(source.Request, sourceDirectory, request, directory, ct);
        // The request owns its media now. Refuse a competing edit made during preparation; never re-read new pixels.
        var latest = (await dubStore.LoadAsync(projectId, ct)).Variants.SingleOrDefault(v => v.Id == variantId);
        if (latest?.Version != expectedVersion) throw new WorkspaceConflictException();
        ShotDubbing.CheckLanguages(await dubStore.LanguagesAsync(projectId, ct), variant.Request);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("Project unavailable.");
        request = await H3GenerationRecipes.StampAsync(request, generator, ct);
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request), project.Name,
            snapshot.Shot.Title + " · " + variant.Request.Target.Name + " dub", tab, request)
            with { Batch = AiBatchDefinition.Create(id, 1, source.Take.Seed) };
    }
}
