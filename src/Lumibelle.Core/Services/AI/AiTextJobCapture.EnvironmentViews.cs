using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<EnvironmentViewCapture> CaptureEnvironmentViewsAsync(Guid projectId, Guid compositionId, long version,
        bool includeSceneText, bool includeNearbyShots, CancellationToken ct = default, Guid? environmentAssetId = null)
    {
        var document = await production!.LoadAsync(projectId, ct);
        var c = document.Compositions.Single(x => x.Id == compositionId && !x.Archived);
        if (c.Version != version) throw new WorkspaceConflictException();
        var library = await assets.LoadAsync(projectId, ct);
        var owner = library.Assets.SingleOrDefault(a => a.Id == (environmentAssetId ?? c.Shot.EnvironmentAssetId) && a.Category == AssetCategory.Environment)
            ?? throw new WorkspaceStoreException("Choose an available environment asset.");
        var configured = Copy(await settings.LoadAsync(ct));
        var pictures = new Dictionary<string, byte[]>(); var pool = new List<EnvironmentViewCandidate>(); var warnings = new List<string>();
        foreach (var image in owner.Images.OrderByDescending(i => i.IsReference).ThenBy(i => i.Id)) {
            ct.ThrowIfCancellationRequested();
            try {
                var png = await PrepareImageAsync(projectId, new(owner.Id, image.Id), null, ct);
                var binding = ReferenceSetups.Bind(owner, image, c.Shot);
                binding.Role = "Environment"; binding.RepresentsId = null; binding.InferUsage = true;
                binding.Use = null; binding.Purpose = null;
                var id = "image/" + image.Id;
                pool.Add(new(id, image.Name ?? "Environment image", image.IsReference, image.PreservationGuidance, binding, null, null, ReelViewSuggestions.Hash(png)));
                pictures.Add(id, png);
            } catch (Exception e) when (e is IOException or WorkspaceStoreException or AiGenerationException) { warnings.Add($"{image.Name}: image unavailable; restore it in Assets."); }
        }
        foreach (var reel in library.Reels.Where(r => r.AssetId == owner.Id).OrderBy(r => r.Id)) {
            if (reel.Keyframes?.Frames.Count is not > 0) { warnings.Add($"{reel.Name}: needs keyframes. Choose viewpoints in Assets before using its other angles."); continue; }
            foreach (var frame in reel.Keyframes.Frames) {
                ct.ThrowIfCancellationRequested();
                try {
                    await using var media = await referenceVideos!.OpenFrameAsync(projectId, frame.Frame, configured.H3, ct);
                    var png = await ComfyReferenceImageEditor.PrepareSourcePngAsync(media.Content, frame.Crop, ct);
                    var id = $"reel/{reel.Id}/{frame.Id}";
                    pool.Add(new(id, ReelViewSuggestions.Label(frame, reel.Keyframes.Frames.IndexOf(frame) + 1), false,
                        frame.Notes, null, Copy(reel), Copy(frame), ReelViewSuggestions.Hash(png)));
                    pictures.Add(id, png);
                } catch (Exception e) when (e is IOException or WorkspaceStoreException or AiGenerationException) { warnings.Add($"{reel.Name} · {frame.Frame.Seconds:0.###} s: saved view unavailable."); }
            }
        }
        var source = await shots!.LoadAsync(projectId, ct); var shot = source.Shots.Single(s => s.Id == c.ShotId);
        var context = await ScriptContextAsync(projectId, document, source, shot, ct);
        var request = new EnvironmentViewRequest(2, projectId, c.Id, Guid.NewGuid(), owner.Id, owner.Name,
            string.Join("\n", owner.Description, owner.PreservationGuidance), c.Shot.Copy(), c.DirectingNotes,
            includeSceneText ? context.SceneText : "", includeNearbyShots ? context.NearbyShots : [],
            EnvironmentViews.Context(c.Shot, c.DirectingNotes), EnvironmentViews.Sources(library, owner.Id), pool, [], [], 9, false, warnings)
            { DirectionContext = ShotDirections.Context(source, c.Shot), IncludeSceneText = includeSceneText, IncludeNearbyShots = includeNearbyShots, CurrentPrompt = c.Prompt, RevisionNotes = c.RevisionNotes };
        if (EnvironmentViews.Sources(await assets.LoadAsync(projectId, ct), owner.Id) != request.SourceFingerprint)
            throw new WorkspaceStoreException("The environment changed during inspection preparation. Try again.");
        return new(request, pictures, configured);
    }
    public async Task<AiJobSubmission> SelectEnvironmentViewsAsync(Guid id, Guid tab, EnvironmentViewCapture capture,
        IReadOnlyList<string> candidates, IReadOnlyList<Guid> removed, bool final, TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        if (!TextVisionPolicy.SupportsBackend(model.Backend)) throw new WorkspaceStoreException(TextVisionPolicy.SetupHint);
        var r = capture.Request with { Candidates = candidates.ToArray(), RemovedBindings = removed.ToArray(),
            MaximumViews = EnvironmentViews.Available(capture.Request.Shot, removed), Final = final };
        EnvironmentViews.Validate(r);
        return await BuildAsync(id, tab, AiJobKind.EnvironmentViewSelection,
            new(r.ProjectId, ShotId: r.Shot.Id, CompositionId: r.CompositionId),
            r.AssetName + (final ? " · Choose environment views" : " · Inspect environment views"), r, model, followsDefault,
            Copy(capture.Settings), EnvironmentViews.Profile,
            EnvironmentViews.Messages(r, candidates.Select(c => capture.Pictures[c]).ToArray()), .3f, null, ct);
    }
    public async Task ValidateEnvironmentSelectionAsync(EnvironmentViewApplication application, Shot current, string notes, CancellationToken ct = default)
    {
        var r = application.Request;
        EnvironmentViews.Stage(application, current, notes, await assets.LoadAsync(r.ProjectId, ct));
        var document = await production!.LoadAsync(r.ProjectId, ct);
        var latest = document.Compositions.Single(c => c.Id == r.CompositionId && !c.Archived);
        EnvironmentViews.Stage(application, latest.Shot, latest.DirectingNotes, await assets.LoadAsync(r.ProjectId, ct));
        if (latest.Prompt != r.CurrentPrompt || latest.RevisionNotes != r.RevisionNotes)
            throw new WorkspaceStoreException("The prompt or revision notes changed. Review a fresh environment selection before applying.");
        var source = await shots!.LoadAsync(r.ProjectId, ct);
        var sourceShot = source.Shots.Single(s => s.Id == current.Id);
        if (ShotDirections.Hash(r.DirectionContext) != ShotDirections.Hash(ShotDirections.Context(source, latest.Shot)))
            throw new WorkspaceStoreException("The direction strategy or dependent shot context changed. Review a fresh environment selection.");
        var context = await ScriptContextAsync(r.ProjectId, document, source, sourceShot, ct);
        if ((r.IncludeSceneText && r.SceneContext != context.SceneText) ||
            (r.IncludeNearbyShots && !r.NearbyShots.SequenceEqual(context.NearbyShots)))
            throw new WorkspaceStoreException("The scene or nearby shots changed. Review a fresh environment selection before applying.");
        var configured = await settings.LoadAsync(ct);
        foreach (var c in r.Pool.Where(c => application.Selected.Contains(c.Id))) {
            byte[] png;
            if (c.Image is { } image) png = await PrepareImageAsync(r.ProjectId, new(image.AssetId, image.MediaId), image.Crop, ct);
            else {
                await using var media = await referenceVideos!.OpenFrameAsync(r.ProjectId, c.Keyframe!.Frame, configured.H3, ct);
                png = await ComfyReferenceImageEditor.PrepareSourcePngAsync(media.Content, c.Keyframe.Crop, ct);
            }
            if (ReelViewSuggestions.Hash(png) != c.Sha256) throw new WorkspaceStoreException("An environment picture changed. Review a fresh selection before applying.");
        }
    }
}
