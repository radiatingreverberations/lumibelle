using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiVideoJobCapture
{
    // A manually changed trim cannot alter an existing batch's immutable request.
    // Start a new batch from that batch's original full refinement inputs instead.
    public async Task<AiJobSubmission> CaptureTrimmedVersionAsync(Guid id, Guid tab, Guid projectId, Guid takeId, CancellationToken ct = default)
    {
        var take = (await shots.LoadAsync(projectId, ct)).Takes.SingleOrDefault(t => t.Id == takeId)
            ?? throw new WorkspaceStoreException("Restore this take before making another version.");
        if (take.Trim is not { } trim || take.Refinement is null) throw new WorkspaceStoreException("Choose a trimmed refinement.");
        var store = jobs ?? throw new WorkspaceStoreException("Saved video requests are unavailable.");
        var job = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == take.RunId)
            ?? throw new WorkspaceStoreException("The original refinement request is unavailable. Use Refine to start from this take's retained latents.");
        var source = AiVideoJobHandler.Read(job, await store.ReadSnapshotAsync(job.Id, ct));
        if (source.Refinement is null || source.Snapshot.ProjectId != projectId)
            throw new WorkspaceStoreException("The full refinement request is unavailable.");
        var request = source with { BatchId = id, OutputTrim = new(trim.SourceStartFrame, trim.SourceEndFrameExclusive),
            Refinement = source.Refinement with { ParentTakeId = take.Id },
            DestinationShotId = take.ShotId == source.Snapshot.Shot.Id ? null : take.ShotId };
        AiVideoJobPolicy.Validate(request);
        var sourceDir = await shots.RunDirectoryAsync(projectId, source.BatchId, ct);
        var destination = await shots.RunDirectoryAsync(projectId, id, ct);
        var projectRoot = CapturedInputStore.ProjectRoot(sourceDir) ?? throw new WorkspaceStoreException("The source run is unavailable.");
        using var gate = await ProjectFiles.LockAsync(projectRoot, ct);
        var target = Path.Combine(destination, "inputs");
        if (Directory.Exists(target)) throw new WorkspaceStoreException("This version already has captured inputs.");
        // CopyPreparedInputs also verifies the full latent file; copy it before the prepared files.
        var package = source.Refinement.SourcePackage;
        var latent = Path.Combine(sourceDir, "inputs", H3RefinementPackage.FileName);
        await RefinementPackages.VerifyFileAsync(latent, package.Bytes, package.Sha256, ct);
        Directory.CreateDirectory(target);
        var temporary = Path.Combine(target, H3RefinementPackage.FileName + ".tmp");
        await using (var input = File.OpenRead(latent))
        await using (var output = File.Create(temporary)) { await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
        File.Move(temporary, Path.Combine(target, H3RefinementPackage.FileName));
        await AiVideoJobPolicy.CopyPreparedInputsAsync(source, sourceDir, request, destination, ct);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, destination, ct);
        var project = await projects.GetAsync(projectId, ct) ?? throw new WorkspaceStoreException("The project is unavailable.");
        return AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, AiVideoJobHandler.Target(request), project.Name,
            take.Snapshot.Shot.Title + " · " + take.Refinement.Mode + " · trimmed version", tab, request) with { Batch = AiBatchDefinition.Create(id, 1, null) };
    }
}
