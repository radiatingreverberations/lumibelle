using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public interface IProductionStore
{
    Task<ProductionDocument> LoadAsync(Guid project, CancellationToken ct = default);
    Task<ProductionDocument> InitializeAsync(Guid project, CancellationToken ct = default);
    Task<ProductionDocument> SaveAsync(Guid project, ProductionComposition composition, long expectedVersion, CancellationToken ct = default);
    Task<ProductionDocument> ApplyResultAsync(Guid project, Guid job, bool automatic, CancellationToken ct = default, bool acceptChangedInputs = false);
    Task<ProductionDocument> RecoverResponseAsync(Guid project, Guid job, long expectedVersion, CancellationToken ct = default);
    Task<ProductionDocument> AcceptAsync(Guid project, Guid composition, long expectedVersion, Guid? job = null, TextModelReference? model = null, CancellationToken ct = default);
    Task<ProductionDocument> SetAspectAsync(Guid project, IReadOnlyCollection<Guid> shots, string? aspect, CancellationToken ct = default);
    Task<ProductionDocument> ReplaceShotContentAsync(Guid project, long expectedRevision, IReadOnlyList<ShotProductionContent> content, CancellationToken ct = default);
}

public sealed partial class FileProductionStore(ProjectFiles files, IShotStore shots, IAssetStore assets, IProjectStore projects,
    TimeProvider clock, IAiJobStore jobs, IReferenceVideoStore? referenceVideos = null, IAiSettingsStore? settings = null,
    IGenerationSetupStore? generationSetups = null) : IProductionStore
{
    public async Task<ProductionDocument> LoadAsync(Guid project, CancellationToken ct = default)
        => await Read(await files.DirectoryAsync(project, ct), project, ct);
    private async Task<ProductionDocument> Read(string dir, Guid project, CancellationToken ct)
    {
        var d = await AtomicJsonFile.ReadAsync<ProductionDocument>(Path.Combine(dir, "production.json"), ct) ?? new() { ProjectId = project };
        ValidateDocument(d, project);
        var coverage = await shots.LoadAsync(project, ct);
        foreach (var c in d.Compositions) if (coverage.Shots.FirstOrDefault(s => s.Id == c.ShotId) is { } source)
            ProductionPolicy.CopyCoverage(source, c.Shot);
        if (generationSetups is not null && d.Compositions.Any(c => c.GenerationSetupId is not null)) {
            var library = await generationSetups.LoadAsync(ct);
            foreach (var c in d.Compositions.Where(c => c.GenerationSetupId is not null))
                (library.Setups.SingleOrDefault(s => s.Id == c.GenerationSetupId)
                    ?? throw new WorkspaceStoreException("A global generation setup is missing. Your saved shot settings have been retained.")).Apply(c);
        }
        return d;
    }
    private static void ValidateDocument(ProductionDocument d, Guid project)
    {
        if (d.SchemaVersion is not (2 or 3) || d.ProjectId != project || d.Revision < 0 || d.Compositions is null ||
            d.Compositions.Any(c => c is null || c.Id == Guid.Empty || c.ShotId == Guid.Empty || c.Version < 1 ||
                string.IsNullOrWhiteSpace(c.Name) || c.History is null || c.Prompt is null || c.DirectingNotes is null || c.RevisionNotes is null || c.ReferenceUsage is null ||
                c.History.Any(r => r is null) || c.History.Select(r => r.Id).Distinct().Count() != c.History.Count || c.AcceptedRevisionId is not null && c.Accepted is null) ||
            d.Compositions.Select(c => c.Id).Distinct().Count() != d.Compositions.Count)
            throw new WorkspaceStoreException("The production document is invalid. It has not been replaced.");
        HydrateSharedContent(d);
    }
    public async Task<ProductionDocument> InitializeAsync(Guid project, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var raw = await AtomicJsonFile.ReadAsync<ProductionDocument>(Path.Combine(dir, "production.json"), ct);
        var source = await shots.LoadAsync(project, ct);
        if (raw is null || raw.SchemaVersion < 2)
        {
            if ((await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == project && (j.Kind is AiJobKind.Video or AiJobKind.PromptComposition) && j.LocksTarget))
                throw new WorkspaceStoreException("Wait for active production requests before starting the new Shots workspace.");
            await jobs.ResetProductionAsync(project, ct);
            // Publish production.json last: interrupted resets repeat safely. Media files stay untouched.
            source.Shots = source.Shots.Select(ProductionPolicy.CoverageCopy).ToList();
            source.Takes.Clear(); source.Trash.Clear(); source.TakePublications.Clear(); source.SceneSetups.Clear();
            source.Recovery = source.Recovery.Select(r => r with { Shots = r.Shots.Select(ProductionPolicy.CoverageCopy).ToList(), SceneSetups = [] }).ToList();
            source.Revision++;
            await AtomicJsonFile.WriteAsync(Path.Combine(dir, "shots.json"), source, ct);
            var cut = await AtomicJsonFile.ReadAsync<CutDocument>(Path.Combine(dir, "cut.json"), ct) ?? new() { ProjectId = project };
            cut.Clips.Clear(); cut.Revision++;
            await AtomicJsonFile.WriteAsync(Path.Combine(dir, "cut.json"), cut, ct);
            raw = new() { ProjectId = project };
            await Publish(dir, raw, ct);
        }
        var d = await Read(dir, project, ct); var changed = raw.SchemaVersion == 2;
        foreach (var shot in source.Shots.Where(s => d.Compositions.All(c => c.ShotId != s.Id)))
        {
            d.Compositions.Add(new() { ShotId = shot.Id, Name = "Default setup", Version = 1,
                Shot = ProductionPolicy.CoverageCopy(shot), SourceFingerprint = ProductionPolicy.SourceFingerprint(shot) });
            changed = true;
        }
        if (generationSetups is not null && d.Compositions.Any(c => c.GenerationSetupId is null)) {
            var library = await generationSetups.ImportAsync(d, ct);
            foreach (var c in d.Compositions.Where(c => c.GenerationSetupId is null)) {
                var imported = library.Imports.Single(i => i.ProjectId == project && i.CompositionId == c.Id);
                library.Setups.Single(s => s.Id == imported.SetupId).Apply(c);
            }
            changed = true;
        }
        if (changed) await Publish(dir, d, ct);
        return d;
    }
    public async Task<ProductionDocument> SaveAsync(Guid project, ProductionComposition composition, long expectedVersion, CancellationToken ct = default)
    {
        var c = composition.Copy();
        if (c.OutputOverrides is { } output && (output.TakeCount is < 1 or > 4 ||
            output.Resolution is { } resolution && !Enum.IsDefined(resolution) ||
            output.UpscalePreview && output.Resolution != VideoResolution.Preview))
            throw new WorkspaceStoreException("Choose a valid resolution and one to four takes.");
        c.OutputOverrides?.Apply(c);
        ReferenceVideos.Validate(c.Shot);
        if (c.Shot.Videos.Count > 0) await (referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.")).ValidateAsync(project, c.Shot.Videos, ct);
        if (c.Id == Guid.Empty || c.TakeCount is < 1 or > 4 || c.Seed < 0 || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 200 || c.Prompt.Length > 100000 || c.DirectingNotes.Length > 20000 || c.RevisionNotes.Length > 20000)
            throw new WorkspaceStoreException("Give the composition a name and keep its text within the editor limits.");
        if (c.Shot.AspectOverride is not (null or "16:9" or "9:16" or "1:1")) throw new WorkspaceStoreException(AspectChoice);
        // Invalid prompt drafts remain saveable; generation performs capability validation.
        c.Shot.Id = c.ShotId;
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, project, ct); var old = d.Compositions.SingleOrDefault(x => x.Id == c.Id);
        if ((old?.Version ?? 0) != expectedVersion) throw new WorkspaceConflictException();
        if (!(await shots.LoadAsync(project, ct)).Shots.Any(s => s.Id == c.ShotId)) throw new WorkspaceStoreException("The source shot was removed. Restore it before editing this composition.");
        if (old is not null && old.ShotId != c.ShotId) throw new WorkspaceStoreException("A composition cannot be moved to another shot.");
        if (c.Archived && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == project && j.Target.CompositionId == c.Id && (j.State is AiJobState.Waiting or AiJobState.Running || j.RemoteUnconfirmed)))
            throw new WorkspaceStoreException("Wait for this composition's active requests, or cancel them before archiving.");
        if (old is null && d.ShotContent.FirstOrDefault(s => s.ShotId == c.ShotId) is { } shared) shared.Apply(c);
        else { c.History = old is null ? [] : ShotCopy.Of(old.History); c.AcceptedRevisionId = old?.AcceptedRevisionId; c.AppliedJobId = old?.AppliedJobId; }
        if (generationSetups is not null && c.GenerationSetupId is { } presetId) {
            var library = await generationSetups.LoadAsync(ct);
            var preset = library.Setups.SingleOrDefault(s => s.Id == presetId)
                ?? throw new WorkspaceStoreException("Global generation setup not found.");
            if (preset.Version != c.GenerationSetupVersion) throw new WorkspaceConflictException();
            // A shot stores its execution adapter; edits to generation choices update
            // the shared preset, excluding output overrides. Captured jobs and takes are never rewritten.
            var generation = GenerationSettings.ForPreset(c, preset.Settings);
            if (preset.Name != c.Name || !FileGenerationSetupStore.SameSettings(preset.Settings, generation)) {
                library = await generationSetups.SaveAsync(preset with { Name = c.Name, Settings = generation }, preset.Version, ct);
                library.Setups.Single(s => s.Id == presetId).Apply(c);
            }
        }
        c.Version = expectedVersion + 1; c.Shot.SelectedTakeId = null;
        if (old is null) d.Compositions.Add(c); else d.Compositions[d.Compositions.IndexOf(old)] = c;
        await Publish(dir, d, ct, c); return d;
    }
    private const string AspectChoice = "Choose landscape, portrait, or square for the shot aspect, or follow the project.";
    // Shares one shot's aspect with others, such as the rest of its scene. Open editors
    // for those shots must reload rather than overwrite the change.
    public async Task<ProductionDocument> SetAspectAsync(Guid project, IReadOnlyCollection<Guid> shots, string? aspect, CancellationToken ct = default)
    {
        if (aspect is not (null or "16:9" or "9:16" or "1:1")) throw new WorkspaceStoreException(AspectChoice);
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, project, ct); var changed = false;
        foreach (var content in d.ShotContent.Where(s => shots.Contains(s.ShotId) && s.AspectOverride != aspect))
        {
            content.AspectOverride = aspect; changed = true;
            foreach (var c in d.Compositions.Where(c => c.ShotId == content.ShotId)) c.Version++;
        }
        if (changed) await Publish(dir, d, ct);
        return d;
    }
    // Writes several shots' prompts and references at once, such as a reel replaced
    // across shots. Any save since the caller read the document is a conflict.
    public async Task<ProductionDocument> ReplaceShotContentAsync(Guid project, long expectedRevision, IReadOnlyList<ShotProductionContent> content, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, project, ct);
        if (d.Revision != expectedRevision) throw new WorkspaceConflictException();
        if (content.Count == 0) return d;
        if (content.Select(s => s.ShotId).Distinct().Count() != content.Count) throw new WorkspaceStoreException("Replace each shot's content once.");
        foreach (var next in content)
        {
            var prior = d.ShotContent.SingleOrDefault(s => s.ShotId == next.ShotId) ?? throw new WorkspaceStoreException("A shot's prompt and references are unavailable.");
            foreach (var c in d.Compositions.Where(c => c.ShotId == next.ShotId))
            {
                var edited = c.Copy(); ShotCopy.Of(next).Apply(edited);
                ReferenceVideos.Validate(edited.Shot);
                c.Version++;
            }
            d.ShotContent[d.ShotContent.IndexOf(prior)] = ShotCopy.Of(next);
        }
        if (referenceVideos is not null) await referenceVideos.ValidateAsync(project, content.SelectMany(s => s.Videos), ct);
        await Publish(dir, d, ct);
        return d;
    }
    public async Task<ProductionDocument> AcceptAsync(Guid project, Guid composition, long expectedVersion, Guid? job = null, TextModelReference? model = null, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, project, ct); var c = d.Compositions.SingleOrDefault(c => c.Id == composition) ?? throw new WorkspaceStoreException("Composition not found.");
        if (job is not null && c.Accepted is { } prior && prior.JobId == job && prior.Prompt == c.Prompt) return d;
        if (c.Version != expectedVersion) throw new WorkspaceConflictException();
        if (c.Archived) throw new WorkspaceStoreException("Restore this composition before accepting its prompt.");
        var source = await shots.LoadAsync(project, ct); var s = source.Shots.SingleOrDefault(s => s.Id == c.ShotId) ?? throw new WorkspaceStoreException("Source shot unavailable.");
        c.SourceFingerprint = ProductionPolicy.SourceFingerprint(s);
        var info = await projects.GetAsync(project, ct) ?? throw new WorkspaceStoreException("Project not found.");
        var library = await assets.LoadAsync(project, ct); var effective = ShotVideoDefaults.Capture(c.Shot, info);
        if (ProductionPolicy.PromptReviewIssue(c, library, source, info) is { } reviewIssue)
            throw new WorkspaceStoreException(reviewIssue);
        if ((await jobs.ReadAsync(ct)).Jobs.Any(j => j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == project && j.Target.ShotId == c.ShotId && j.LocksTarget))
            throw new WorkspaceStoreException("Wait for the active composition request, or cancel it before accepting this prompt.");
        if (effective.Videos.Count > 0) await (referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.")).ValidateAsync(project, effective.Videos, ct);
        foreach (var binding in effective.Images) if (lumibelle.Services.Production.ProductionPolicy.MediaIssue(binding, library) is { } issue) throw new WorkspaceStoreException(issue);
        var context = ProductionPolicy.ContextFingerprint(c, library, source, info);
        var imageData = await ProductionInputs.CaptureAsync(project, effective, assets, ct, referenceVideos, settings is null ? null : (await settings.LoadAsync(ct)).H3);
        if (job is { } id)
        {
            var header = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id && j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == project && j.Target.CompositionId == c.Id);
            if (header is not { State: AiJobState.Completed, CancelRequested: false }) throw new WorkspaceStoreException("Only a completed composition response can be applied.");
            var request = AiTextJobHandler.Read(header, await jobs.ReadSnapshotAsync(id, ct)).Payload<PromptCompositionRequest>();
            if (request.ContextFingerprint != context || !request.Images.SequenceEqual(imageData.Select(i => i.Identity))) throw new WorkspaceStoreException("The composition inputs changed after this request. Compose again or review a manual edit.");
            var artifact = await jobs.ReadArtifactAsync<AiTextJobResult>(id, AiJobArtifact.Result, ct);
            var proposed = artifact is { Complete: true, Error: null } ? artifact.Read<PromptCompositionResult>() : null;
            if (proposed is null || proposed.Prompt != c.Prompt || proposed.ReferenceUsage != c.ReferenceUsage)
                throw new WorkspaceStoreException("This draft differs from the saved AI response. Accept it as a manual edit, or review the original response.");
            model = request.Model;
        }
        var revision = new CompositionPromptRevision(Guid.NewGuid(), clock.GetUtcNow(), c.Prompt, c.ReferenceUsage, context, c.SourceFingerprint, job, model, imageData.Select(i => i.Identity).ToArray()) {
            ReferenceFingerprint = PromptReferenceFreshness.Fingerprint(effective, ShotReferences.Resolve(effective, library, source))
        };
        c.History.Add(revision); c.AcceptedRevisionId = revision.Id; c.ReviewJobId = null; c.Version++;
        await Publish(dir, d, ct, c); return d;
    }
    public async Task<ProductionDocument> ApplyResultAsync(Guid project, Guid job, bool automatic, CancellationToken ct = default, bool acceptChangedInputs = false)
    {
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, project, ct);
        var header = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == job && j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == project);
        if (header is null || header.CancelRequested || header.State is AiJobState.Cancelled or AiJobState.NeedsAttention) return d;
        var c = d.Compositions.SingleOrDefault(c => c.Id == header.Target.CompositionId);
        if (c is null || c.AppliedJobId == job || c.Archived) return d;
        var request = AiTextJobHandler.Read(header, await jobs.ReadSnapshotAsync(job, ct)).Payload<PromptCompositionRequest>();
        if (automatic && !string.IsNullOrWhiteSpace(request.CurrentPrompt)) return d;
        var artifact = await jobs.ReadArtifactAsync<AiTextJobResult>(job, AiJobArtifact.Result, ct);
        var result = artifact is { Complete: true, Error: null } ? artifact.Read<PromptCompositionResult>() : null;
        if (result is null) return d;
        var source = await shots.LoadAsync(project, ct); var library = await assets.LoadAsync(project, ct);
        var info = await projects.GetAsync(project, ct) ?? throw new WorkspaceStoreException("Project not found.");
        var images = await ProductionInputs.CaptureAsync(project, ShotVideoDefaults.Capture(c.Shot, info), assets, ct, referenceVideos, settings is null ? null : (await settings.LoadAsync(ct)).H3);
        // A superseded review is never applied. An explicit Apply may follow harmless saves (seed,
        // take count, or an unchanged draft). Automatic application keeps the exact revision guard.
        if (c.ReviewJobId != job) { if (automatic) return d; throw new WorkspaceConflictException(); }
        if (automatic && c.Version != request.CompositionVersion + 1) return d;
        var changes = ProductionPolicy.CompositionChanges(request, c, library, source, info, images.Select(i => i.Identity));
        if (!AssistedApply.Allows(changes, automatic, acceptChangedInputs, "the shot")) return d;
        if (c.Shot.Videos.Count > 0)
        {
            try { await (referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.")).ValidateAsync(project, c.Shot.Videos, ct); }
            catch (WorkspaceStoreException) when (automatic) { return d; }
        }
        ProductionPolicy.ValidateGenerationPrompt(result.Prompt);
        c.Prompt = result.Prompt; c.ReferenceUsage = result.ReferenceUsage; c.AppliedJobId = job; c.ReviewJobId = null;
        c.SourceFingerprint = ProductionPolicy.SourceFingerprint(c.Shot);
        var revision = new CompositionPromptRevision(Guid.NewGuid(), clock.GetUtcNow(), c.Prompt, c.ReferenceUsage, request.ContextFingerprint, c.SourceFingerprint, job, request.Model, request.Images) {
            ReferenceFingerprint = PromptReferenceFreshness.Fingerprint(request.Shot, request.Guidance)
        };
        c.History.Add(revision); c.AcceptedRevisionId = revision.Id; c.Version++;
        await Publish(dir, d, ct, c); return d;
    }
    public async Task<ProductionDocument> RecoverResponseAsync(Guid project, Guid job, long expectedVersion, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var d = await Read(dir, project, ct);
        var header = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == job && j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == project);
        if (header is not { State: AiJobState.NeedsAttention, Recovery: AiJobRecovery.GenerateAgain, RemoteUnconfirmed: false, CancelRequested: false })
            throw new WorkspaceStoreException("This request is not available for local response recovery.");
        var c = d.Compositions.SingleOrDefault(c => c.Id == header.Target.CompositionId) ?? throw new WorkspaceStoreException("Composition not found.");
        if (c.Version != expectedVersion || c.Archived || c.ReviewJobId != job) throw new WorkspaceConflictException();
        var request = AiTextJobHandler.Read(header, await jobs.ReadSnapshotAsync(job, ct)).Payload<PromptCompositionRequest>();
        var artifact = await jobs.ReadArtifactAsync<AiTextJobResult>(job, AiJobArtifact.Result, ct);
        if (artifact is not { Complete: true, Error: not null } || artifact.FinishReason is not (null or "stop"))
            throw new WorkspaceStoreException("An interrupted response cannot be recovered as a complete prompt.");
        var recovered = PromptComposer.RecoverResponse(artifact.Raw, request);
        var source = await shots.LoadAsync(project, ct); var library = await assets.LoadAsync(project, ct);
        var info = await projects.GetAsync(project, ct) ?? throw new WorkspaceStoreException("Project not found.");
        var effective = ShotVideoDefaults.Capture(c.Shot, info);
        var images = await ProductionInputs.CaptureAsync(project, effective, assets, ct, referenceVideos, settings is null ? null : (await settings.LoadAsync(ct)).H3);
        if (c.Prompt != request.CurrentPrompt || request.ContextFingerprint != ProductionPolicy.ContextFingerprint(c, library, source, info) ||
            !request.Images.SequenceEqual(images.Select(i => i.Identity)))
            throw new WorkspaceStoreException("The prompt, shot direction or references changed after this request. Copy the saved response for manual editing instead.");
        ProductionPolicy.ValidateGenerationPrompt(recovered.Result.Prompt);
        c.Prompt = recovered.Result.Prompt; c.ReferenceUsage = recovered.Result.ReferenceUsage;
        c.ReviewJobId = null; c.AcceptedRevisionId = null; c.Version++;
        // Keep the original failed request and raw response untouched. This is a draft for review.
        await Publish(dir, d, ct, c); return d;
    }
}

public static class ProductionInputs
{
    public static async Task<IReadOnlyList<(CompositionInput Identity, byte[] Bytes)>> CaptureAsync(Guid project, Shot shot, IAssetStore assets, CancellationToken ct, IReferenceVideoStore? reels = null, H3Settings? settings = null)
    {
        var result = new List<(CompositionInput, byte[])>();
        foreach (var b in shot.Images)
        {
            await using var source = await assets.OpenImageAsync(project, b.AssetId, b.MediaId, ct);
            if (source is null) throw new WorkspaceStoreException("A reference is unavailable. Restore or replace it.");
            var png = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source.Content, b.Crop, ct);
            result.Add((new(b.Id, Convert.ToHexString(SHA256.HashData(png))), png));
        }
        var keyframes = ResolvedReferences.For(shot).Pictures.Where(p => p.Keyframe is not null).ToArray();
        if (keyframes.Length > 0) await (reels ?? throw new WorkspaceStoreException("Reel frame storage is unavailable.")).PrepareFramesAsync(project, keyframes.Select(p => p.Keyframe!.Frame), settings ?? new(), ct);
        foreach (var picture in keyframes)
        {
            await using var source = await (reels ?? throw new WorkspaceStoreException("Reel frame storage is unavailable.")).OpenFrameAsync(project, picture.Keyframe!.Frame, settings ?? new(), ct);
            var png = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source.Content, picture.Keyframe.Crop, ct);
            result.Add((new(picture.BindingId, Convert.ToHexString(SHA256.HashData(png))), png));
        }
        return result;
    }
}
