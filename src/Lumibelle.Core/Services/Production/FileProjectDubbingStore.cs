using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public interface IProjectDubbingStore
{
    Task<ProjectLanguages> LanguagesAsync(Guid project, CancellationToken ct = default);
    Task<ProjectLanguages> SaveLanguagesAsync(ProjectLanguages languages, long expectedRevision, CancellationToken ct = default);
    Task<ShotDubDocument> LoadAsync(Guid project, CancellationToken ct = default);
    Task<ShotDubSource> SourceAsync(Guid project, Guid take, CancellationToken ct = default);
    Task<ShotDubRequest> CaptureAsync(Guid project, Guid take, string targetCode, string instructions, CancellationToken ct = default);
    Task<ShotDubVariant> SaveAsync(ShotDubRequest request, ShotDubTranslation translation, long expectedVersion,
        Guid? translationJobId = null, TextModelReference? model = null, CancellationToken ct = default);
}

public sealed class FileProjectDubbingStore(ProjectFiles files, IShotStore shots, IAiJobStore jobs, TimeProvider clock) : IProjectDubbingStore
{
    internal static readonly Storage.ProjectDocumentStore<ProjectLanguages> Languages = new(new()
    {
        Kind = "languages", Description = "the language versions", Legacy = ["languages.json"],
        Empty = id => new() { ProjectId = id }, Lists = []
    });
    internal static readonly Storage.ProjectDocumentStore<ShotDubDocument> Documents = new(new()
    {
        Kind = "dubbing", Description = "the language versions", Legacy = ["dubbing.json"],
        Empty = id => new() { ProjectId = id }, Lists = [new("variants")]
    });
    public static async Task CheckPortableExportAsync(string directory, Guid project, CancellationToken ct = default)
    {
        var languages = await ReadLanguages(directory, project, ct);
        var dubs = await Read(directory, project, ct);
        if (languages.Main is not null || languages.Dubs.Count > 0 || !string.IsNullOrEmpty(languages.TranslationNotes) || dubs.Variants.Count > 0)
            throw new WorkspaceStoreException("Project-package export does not yet preserve language settings and dub drafts. Back up the full application data and project folders instead. MP4 cut export remains available.");
    }

    public async Task<ProjectLanguages> LanguagesAsync(Guid project, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(project, ct);
        return await ReadLanguages(dir, project, ct);
    }
    private static async Task<ProjectLanguages> ReadLanguages(string dir, Guid project, CancellationToken ct)
    {
        var value = await Languages.LoadAsync(dir, project, ct);
        if (value.ProjectId != project) throw new WorkspaceStoreException("Language settings belong to another project.");
        return ShotDubbing.Normalize(value);
    }
    public async Task<ProjectLanguages> SaveLanguagesAsync(ProjectLanguages languages, long expectedRevision, CancellationToken ct = default)
    {
        languages = ShotDubbing.Normalize(ShotCopy.Of(languages));
        var dir = await files.DirectoryAsync(languages.ProjectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var current = await ReadLanguages(dir, languages.ProjectId, ct);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        var saved = languages with { Revision = current.Revision + 1 };
        await Languages.SaveAsync(dir, languages.ProjectId, saved, ct);
        return ShotCopy.Of(saved);
    }
    public async Task<ShotDubDocument> LoadAsync(Guid project, CancellationToken ct = default) =>
        await Read(await files.DirectoryAsync(project, ct), project, ct);
    private static async Task<ShotDubDocument> Read(string dir, Guid project, CancellationToken ct)
    {
        var d = await Documents.LoadAsync(dir, project, ct);
        if (d.SchemaVersion != 1 || d.ProjectId != project || d.Revision < 0 || d.Variants is null ||
            d.Variants.Any(v => v is null || v.Id == Guid.Empty || v.Version < 1 || v.Request is null || v.Request.Target is null || v.Request.ProjectId != project ||
                v.Id != v.Request.VariantId || v.UpdatedUtc == default || v.TranslationJobId == Guid.Empty) ||
            d.Variants.Select(v => v.Id).Distinct().Count() != d.Variants.Count ||
            d.Variants.Select(v => (v.Request.SourceTakeId, v.Request.Target.Code)).Distinct().Count() != d.Variants.Count)
            throw new WorkspaceStoreException("The saved language variants are invalid. They have not been replaced.");
        foreach (var variant in d.Variants)
        {
            ShotDubbing.ValidateTranslation(variant.Request, variant.Translation);
            if (variant.Prompt != ShotDubbing.Prompt(variant.Request, variant.Translation.Lines))
                throw new WorkspaceStoreException("A language variant contains edits outside its dialogue.");
        }
        return ShotCopy.Of(d);
    }
    public async Task<ShotDubSource> SourceAsync(Guid project, Guid takeId, CancellationToken ct = default)
    {
        var d = await shots.LoadAsync(project, ct);
        var take = ShotCopy.Of(d.Takes.SingleOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("Restore the master take before creating or rendering a language version."));
        if (ShotDubbing.SourceIssue(take) is { } issue) throw new WorkspaceStoreException(issue);
        if (d.Shots.All(s => s.Id != take.ShotId)) throw new WorkspaceStoreException("Restore the destination shot first.");
        var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == take.AiJobId && j.Kind == AiJobKind.Video)
            ?? throw new WorkspaceStoreException("The master take's saved generation request is unavailable.");
        var source = AiVideoJobHandler.Read(job, await jobs.ReadSnapshotAsync(job.Id, ct));
        if (source.Snapshot.ProjectId != project || source.BatchId != take.RunId || source.Refinement is not null ||
            job.Batch?.Candidates.Any(c => c.Id == take.Id && c.Number == take.Candidate && c.Seed == take.Seed) != true ||
            !ShotDubbing.Same(source.Snapshot, take.Snapshot))
            throw new WorkspaceStoreException("The master take does not match its immutable generation request.");
        return new(take, source);
    }
    public async Task<ShotDubRequest> CaptureAsync(Guid project, Guid take, string targetCode, string instructions, CancellationToken ct = default)
    {
        if (instructions is null || instructions.Length > 12000) throw new WorkspaceStoreException("Keep translation instructions under 12000 characters.");
        var source = await SourceAsync(project, take, ct);
        var languages = await LanguagesAsync(project, ct);
        var target = languages.Dubs.SingleOrDefault(l => string.Equals(l.Code, targetCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkspaceStoreException("Choose a dub language configured in Project settings.");
        var languageFingerprint = ShotDubbing.LanguageFingerprint(languages, target);
        var existing = (await LoadAsync(project, ct)).Variants.SingleOrDefault(v => v.Request.SourceTakeId == take && v.Request.Target.Code == target.Code);
        var snapshot = source.Request.Snapshot;
        var request = new ShotDubRequest(1, project, source.Take.ShotId, take, existing?.Id ?? Guid.NewGuid(), existing?.Version ?? 0,
            ShotDubbing.Hash(snapshot), ShotDubbing.References(snapshot), languages.Main!, target, snapshot.Prompt,
            ShotCopy.Of(snapshot.Shot.Dialogue), snapshot.Shot.Duration!.Value, snapshot.Shot.SourceExcerpt,
            string.Join("\n\n", new[] { languages.TranslationNotes, instructions }.Where(s => !string.IsNullOrWhiteSpace(s))), languageFingerprint);
        ShotDubbing.ValidateRequest(request); return request;
    }
    public async Task<ShotDubVariant> SaveAsync(ShotDubRequest request, ShotDubTranslation translation, long expectedVersion,
        Guid? translationJobId = null, TextModelReference? model = null, CancellationToken ct = default)
    {
        request = ShotCopy.Of(request); translation = ShotCopy.Of(translation);
        ShotDubbing.ValidateTranslation(request, translation);
        var source = await SourceAsync(request.ProjectId, request.SourceTakeId, ct);
        var prompt = ShotDubbing.Prompt(request, translation.Lines);
        // Apply performs the master snapshot and unchanged-reference checks before a manifest is touched.
        var proposed = new ShotDubVariant(request.VariantId, 1, request, translation, prompt, clock.GetUtcNow(), translationJobId, model);
        _ = ShotDubbing.Apply(source.Request.Snapshot, proposed);
        if (translationJobId is { } jobId)
        {
            if (expectedVersion != request.ExpectedVariantVersion)
                throw new WorkspaceConflictException();
            var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == jobId && j.Kind == AiJobKind.ShotTranslation);
            if (job is not { State: AiJobState.Completed, CancelRequested: false }) throw new WorkspaceStoreException("Only a completed translation suggestion can be applied.");
            var input = AiTextJobHandler.Read(job, await jobs.ReadSnapshotAsync(jobId, ct));
            var result = await jobs.ReadArtifactAsync<AiTextJobResult>(jobId, AiJobArtifact.Result, ct);
            if (!ShotDubbing.Same(request, input.Payload<ShotDubRequest>()) || result is not { Complete: true, Error: null } ||
                !ShotDubbing.Same(translation, result.Read<ShotDubTranslation>()))
                throw new WorkspaceStoreException("The edited translation differs from this AI response. Save it as a manual revision.");
            proposed = proposed with { Model = input.Model };
        }
        else proposed = proposed with { Model = null };
        var dir = await files.DirectoryAsync(request.ProjectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        ShotDubbing.CheckLanguages(await ReadLanguages(dir, request.ProjectId, ct), request);
        var d = await Read(dir, request.ProjectId, ct);
        var current = d.Variants.SingleOrDefault(v => v.Request.SourceTakeId == request.SourceTakeId && v.Request.Target.Code == request.Target.Code);
        // A lost save acknowledgement is idempotent, including on a later retry from another tab.
        if (current is not null && current.Id == request.VariantId && ShotDubbing.Same(current.Request, request) &&
            ShotDubbing.Same(current.Translation, translation) && current.TranslationJobId == translationJobId) return current;
        if ((current?.Version ?? 0) != expectedVersion || current is not null && current.Id != request.VariantId ||
            current is null && d.Variants.Any(v => v.Id == request.VariantId)) throw new WorkspaceConflictException();
        var saved = proposed with { Version = expectedVersion + 1 };
        d = d with { Revision = d.Revision + 1, Variants = [.. d.Variants.Where(v => v.Id != saved.Id), saved] };
        await Documents.SaveAsync(dir, request.ProjectId, d, ct); return ShotCopy.Of(saved);
    }
}
