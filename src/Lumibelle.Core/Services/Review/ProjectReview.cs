using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>
/// Reads a project the way a continuity review needs it: each shot with its prompt, references and takes.
/// Nothing here changes a project, runs a model or uses the GPU.
/// </summary>
public sealed class ProjectReview(IShotStore shots, IProductionStore production, ICutStore cuts, IAssetStore assets, IScriptStore scripts)
{
    public async Task<ProjectReviewSnapshot> LoadAsync(Guid projectId, CancellationToken ct = default)
    {
        var document = await shots.LoadAsync(projectId, ct);
        var prompts = await production.LoadAsync(projectId, ct);
        var library = await assets.LoadAsync(projectId, ct);
        var cut = await cuts.LoadAsync(projectId, ct);
        ScriptDocument? script = null;
        try { script = await scripts.LoadAsync(projectId, ct); }
        catch (WorkspaceStoreException) { /* Take comparisons report the script as unavailable. */ }
        var sources = new Dictionary<Guid, ScriptSourceSnapshot>();
        foreach (var id in document.Takes.Select(t => t.Snapshot.Shot.ApprovedScriptId).OfType<Guid>().Distinct())
            try { if (await scripts.LoadSourceAsync(projectId, id, ct) is { } source) sources[id] = source; }
            catch (WorkspaceStoreException) { }
        return Compose(document, prompts, cut, library, script, sources);
    }

    public static ProjectReviewSnapshot Compose(ShotDocument document, ProductionDocument prompts, CutDocument cut, AssetLibrary library,
        ScriptDocument? script = null, IReadOnlyDictionary<Guid, ScriptSourceSnapshot>? sources = null)
    {
        var takes = document.Takes.ToLookup(t => t.ShotId);
        var rows = document.Shots.Select((shot, index) => new ReviewShot(index + 1, shot,
            // Prompt and references are shared by a shot's setups; the first one in use carries them.
            prompts.Compositions.FirstOrDefault(c => c.ShotId == shot.Id && !c.Archived) ?? prompts.Compositions.FirstOrDefault(c => c.ShotId == shot.Id),
            takes[shot.Id].ToArray())).ToArray();
        return new(document.ProjectId, document, prompts, cut, library) { Rows = rows, Script = script, ScriptSources = sources ?? new Dictionary<Guid, ScriptSourceSnapshot>() };
    }

    /// <summary>What changed since a take was rendered, compared as the Shots studio compares it.</summary>
    public static TakeInputChange TakeChanges(ProjectReviewSnapshot snapshot, ShotTake take) => TakeInputChanges.Compare(take.Snapshot,
        snapshot.Shots.Shots.FirstOrDefault(s => s.Id == take.ShotId),
        snapshot.Production.Compositions.FirstOrDefault(c => take.ShotId == take.Snapshot.Shot.Id
            ? c.Id == take.Snapshot.Production?.CompositionId : c.ShotId == take.ShotId && !c.Archived),
        snapshot.Assets, snapshot.Shots,
        take.Snapshot.Shot.ApprovedScriptId is { } id ? snapshot.ScriptSources.GetValueOrDefault(id)?.Blocks : null, snapshot.Script?.Blocks);

    /// <summary>Whether the prompt was reviewed against the references it has now.</summary>
    public static PromptReferenceCheck PromptCheck(ProjectReviewSnapshot snapshot, ReviewShot row)
    {
        string? fingerprint = null;
        if (row.Setup is { } setup && !string.IsNullOrWhiteSpace(setup.Prompt))
            try { fingerprint = PromptReferenceFreshness.Fingerprint(setup.Shot, ShotReferences.Resolve(setup.Shot, snapshot.Assets, snapshot.Shots)); }
            catch (Exception e) when (e is WorkspaceStoreException or InvalidOperationException) { /* Report review needed, not a false current state. */ }
        return PromptReferenceFreshness.Compare(row.Setup, fingerprint, row.Setup is null ? null : ShotDirections.Fingerprint(snapshot.Shots, row.Setup.Shot));
    }
}
