using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed record ShotProjectCopyRequest(Guid Id, Guid SourceProjectId, Guid DestinationProjectId,
    IReadOnlyList<Guid> ShotIds, long ShotRevision, long ProductionRevision, long ScriptRevision);
public sealed record ShotProjectCopyPreview(long ShotRevision, long ProductionRevision, long ScriptRevision,
    int Shots, int Takes, int Assets, int Scenes);
public sealed record ShotProjectCopyReceipt(Guid Id, string Fingerprint, Guid SourceProjectId,
    IReadOnlyList<Guid> ShotIds, DateTimeOffset CopiedUtc);
public sealed record ShotProjectCopyResult(Guid ProjectId, IReadOnlyList<Guid> ShotIds);
public sealed record ShotProjectCopySetup(ProductionComposition Composition, Shot Shot);
public sealed record ShotProjectCopyPackage(ShotProjectCopyRequest Request, List<Shot> Shots,
    List<ShotProjectCopySetup> Setups, List<ScriptBlock> Blocks, List<ShotTake> Takes,
    List<AssetReuseCommand> Assets, Dictionary<Guid, Guid> Identities)
{
    public List<ScriptSourceSnapshot> ScriptSources { get; init; } = [];
}

public interface IShotProjectCopyStore
{
    Task<ShotProjectCopyRequest?> FindPendingAsync(ShotProjectCopyRequest selection, CancellationToken ct = default);
    Task<ShotProjectCopyPreview> PreviewAsync(Guid source, IReadOnlyCollection<Guid> shots, CancellationToken ct = default);
    Task<ShotProjectCopyResult> CopyAsync(ShotProjectCopyRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
}

// Copies own their media. Stable operation identities make retries safe across every publication;
// shots.json is published last, once its script, production inputs and media are installed.
public sealed class ShotProjectCopyStore(ProjectFiles files, IShotStore shots, IProductionStore production,
    IScriptStore scripts, IAssetStore assets, IAssetReuseStore reuse, TimeProvider clock, IAiJobStore? jobs = null,
    IReferenceVideoStore? referenceVideos = null, IAiSettingsStore? settings = null) : IShotProjectCopyStore
{
    private sealed record Source(ShotDocument Shots, ProductionDocument Production, ScriptDocument Script, AssetLibrary Assets);

    public async Task<ShotProjectCopyRequest?> FindPendingAsync(ShotProjectCopyRequest selection, CancellationToken ct = default)
    {
        var destination = await files.DirectoryAsync(selection.DestinationProjectId, ct);
        var directory = Path.Combine(destination, "shot-copy-operations");
        if (!Directory.Exists(directory)) return null;
        var completed = (await shots.LoadAsync(selection.DestinationProjectId, ct)).ProjectCopies.Select(r => r.Id).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc)) {
            var plan = await AtomicJsonFile.ReadAsync<ShotProjectCopyPackage>(path, ct);
            if (plan is null || completed.Contains(plan.Request.Id)) continue;
            if (AssetReusePolicy.Hash(plan.Request with { Id = selection.Id }) == AssetReusePolicy.Hash(selection))
                return plan.Request;
        }
        return null;
    }

    private async Task<Source> Read(Guid source, CancellationToken ct) => new(await shots.LoadAsync(source, ct),
        await production.LoadAsync(source, ct), await scripts.LoadAsync(source, ct), await assets.LoadAsync(source, ct));

    private static Shot[] Selected(Source source, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new WorkspaceStoreException("Choose distinct shots to copy.");
        var selected = source.Shots.Shots.Where(s => ids.Contains(s.Id)).ToArray();
        if (selected.Length != ids.Count) throw new WorkspaceConflictException();
        return selected;
    }

    private static Shot[] Inputs(Source source, Shot[] selected) => selected.Concat(source.Production.Compositions
        .Where(c => selected.Any(s => s.Id == c.ShotId)).Select(c => c.Shot)).ToArray();

    private static Guid[] AssetIds(Source source, Shot[] selected)
    {
        var ids = new HashSet<Guid>();
        foreach (var shot in Inputs(source, selected)) {
            foreach (var image in shot.Images) ids.Add(image.AssetId);
            foreach (var voice in shot.Voices) { ids.Add(voice.AssetId); if (voice.CharacterAssetId is { } character) ids.Add(character); }
            foreach (var voice in shot.CharacterVoices ?? []) ids.Add(voice.AssetId);
            foreach (var character in shot.Characters.Where(c => c.Appearance is not null)) ids.Add(character.Appearance!.AssetId);
            foreach (var video in shot.Videos) {
                var owner = video.OwnerAssetId ?? source.Assets.Reels.FirstOrDefault(r => r.Media.Id == video.Media.Id)?.AssetId;
                if (owner is null) throw new WorkspaceStoreException($"The reel {video.Name} has no library asset. Save it to Assets before copying this shot.");
                ids.Add(owner.Value);
            }
        }
        if (ids.Any(id => source.Assets.Assets.All(a => a.Id != id)))
            throw new WorkspaceStoreException("A referenced asset is unavailable. Restore it before copying these shots.");
        return ids.Order().ToArray();
    }

    private static List<ScriptBlock> Blocks(Source source, Shot[] selected)
    {
        var scenes = selected.Where(s => s.SceneId is not null).Select(s => s.SceneId!.Value).ToHashSet();
        var sections = ScriptStructure.Sections(source.Script.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene && scenes.Contains(s.Id)).ToArray();
        if (sections.Length != scenes.Count)
            throw new WorkspaceStoreException("A shot's script scene is no longer in the saved script. Restore or unlink that scene before copying.");
        var blocks = sections.SelectMany(s => source.Script.Blocks.Skip(s.Start).Take(s.Count)).Select(b => b.Copy()).ToList();
        // Include enclosing act headings, without pulling in unrelated scenes.
        foreach (var act in sections.Select(s => s.ActId).OfType<Guid>().Distinct()) {
            var heading = source.Script.Blocks.Single(b => b.Id == act);
            blocks.Add(heading.Copy());
        }
        return blocks.OrderBy(b => source.Script.Blocks.FindIndex(s => s.Id == b.Id)).ToList();
    }

    private static ShotTake[] Takes(Source source, Shot[] selected)
    {
        var ids = source.Shots.Takes.Where(t => selected.Any(s => s.Id == t.ShotId)).Select(t => t.Id).ToHashSet();
        foreach (var shot in Inputs(source, selected)) {
            if (shot.StartFrame is { } start) ids.Add(start.TakeId);
            if (shot.ContinuityFrame is { } continuity) ids.Add(continuity.TakeId);
        }
        var result = source.Shots.Takes.Where(t => ids.Contains(t.Id)).ToArray();
        if (result.Length != ids.Count) throw new WorkspaceStoreException("A starting or continuity frame's saved take is missing. Restore it before copying.");
        return result;
    }

    public async Task<ShotProjectCopyPreview> PreviewAsync(Guid source, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(source, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var data = await Read(source, ct); var selected = Selected(data, ids);
        return new(data.Shots.Revision, data.Production.Revision, data.Script.Revision, selected.Length,
            Takes(data, selected).Length, AssetIds(data, selected).Length, Blocks(data, selected).Count(b => b.Kind == ScriptBlockKind.Scene));
    }

    public async Task<ShotProjectCopyResult> CopyAsync(ShotProjectCopyRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (request.Id == Guid.Empty || request.SourceProjectId == request.DestinationProjectId)
            throw new WorkspaceStoreException("Choose another project to receive the copied shots.");
        var destination = await files.DirectoryAsync(request.DestinationProjectId, ct);
        var source = await files.DirectoryAsync(request.SourceProjectId, ct);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new WorkspaceStoreException("Choose a different project folder.");
        var operation = Path.Combine(destination, "shot-copy-operations", request.Id.ToString("D") + ".json");
        using var operationGate = await ProjectFiles.LockAsync(operation, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(operation)!);
        var fingerprint = AssetReusePolicy.Hash(request);
        var prior = (await shots.LoadAsync(request.DestinationProjectId, ct)).ProjectCopies.FirstOrDefault(r => r.Id == request.Id);
        if (prior is not null) return Result(prior, request, fingerprint);
        var package = await AtomicJsonFile.ReadAsync<ShotProjectCopyPackage>(operation, ct);
        if (package is null) {
            using var sourceGate = await ProjectFiles.LockAsync(source, ct);
            var data = await Read(request.SourceProjectId, ct);
            if (data.Shots.Revision != request.ShotRevision || data.Production.Revision != request.ProductionRevision || data.Script.Revision != request.ScriptRevision)
                throw new WorkspaceStoreException("The source shots, references or script changed. Review the copy again.");
            package = Capture(request, data);
            await CaptureRequests(package, ct);
            await AtomicJsonFile.WriteAsync(operation, package, ct);
        }
        if (AssetReusePolicy.Hash(package.Request) != fingerprint) throw new WorkspaceStoreException("This copy operation belongs to another selection.");

        for (var i = 0; i < package.Assets.Count; i++) {
            progress?.Report($"Copying reference asset {i + 1} of {package.Assets.Count}…");
            var copied = await reuse.ReuseAsync(package.Assets[i], ct);
            if (!copied.Available) throw new WorkspaceStoreException("A previously copied reference was removed. Restore it in the destination before retrying.");
        }
        await CopyRefModSources(package, ct);
        foreach (var take in package.Takes) {
            progress?.Report($"Copying saved take {take.Candidate}…");
            var targetId = package.Identities[take.Id];
            await CopyTake(take, source, destination, targetId, ct);
            if (take.CopyRequest is { } captured) await CopyInputs(captured, source, destination, package.Identities, ct);
        }

        progress?.Report("Adding script scenes and shots…");
        using var destinationGate = await ProjectFiles.LockAsync(destination, ct);
        var latest = await shots.LoadAsync(request.DestinationProjectId, ct);
        prior = latest.ProjectCopies.FirstOrDefault(r => r.Id == request.Id);
        if (prior is not null) return Result(prior, request, fingerprint);
        var targetScript = await scripts.LoadAsync(request.DestinationProjectId, ct);
        var targetProduction = await production.LoadAsync(request.DestinationProjectId, ct);
        var targetAssets = await assets.LoadAsync(request.DestinationProjectId, ct);
        var copiedShots = Remap(package.Shots, package.Identities, request.DestinationProjectId);
        var copiedTakes = Remap(package.Takes, package.Identities, request.DestinationProjectId);
        foreach (var take in copiedTakes) {
            // Dependency takes can support a continuity picture without adding an unrelated shot.
            if (!copiedShots.Any(s => s.Id == take.ShotId)) take.ShotId = copiedShots[0].Id;
            take.Directory = take.Id.ToString("D");
        }
        foreach (var shot in copiedShots) {
            shot.ApprovedScriptId = null; shot.Planning = null;
            shot.AspectOverride ??= shot.Aspect; // Keep framing when destination defaults differ.
            await RelinkReferences(shot, targetAssets, destination, ct);
        }
        var blocks = Remap(package.Blocks, package.Identities, request.DestinationProjectId);
        foreach (var block in blocks) {
            var existing = targetScript.Blocks.FirstOrDefault(b => b.Id == block.Id);
            if (existing is not null && AssetReusePolicy.Hash(existing) != AssetReusePolicy.Hash(block))
                throw new WorkspaceStoreException("A partially copied script scene was edited. Restore it before retrying this copy.");
        }
        var additions = blocks.Where(b => targetScript.Blocks.All(existing => existing.Id != b.Id)).ToList();
        var nextScript = targetScript with { SchemaVersion = 2, Revision = targetScript.Revision + 1, UpdatedUtc = clock.GetUtcNow(), Blocks = [.. targetScript.Blocks, .. additions] };
        FileScriptStore.Validate(nextScript, request.DestinationProjectId);
        foreach (var captured in Remap(package.ScriptSources, package.Identities, request.DestinationProjectId))
            await AtomicJsonFile.WriteAsync(Path.Combine(destination, "script-sources", captured.Id.ToString("D") + ".json"), captured, ct);
        foreach (var setup in package.Setups) {
            var original = setup.Composition;
            var composition = Remap(original, package.Identities, request.DestinationProjectId);
            composition.Shot = copiedShots.Single(s => s.Id == composition.ShotId).Copy();
            Remap(SetupInputs.From(setup.Shot), package.Identities, request.DestinationProjectId).Apply(composition.Shot);
            composition.Shot.ApprovedScriptId = null;
            await RelinkReferences(composition.Shot, targetAssets, destination, ct);
            composition.SourceFingerprint = ProductionPolicy.SourceFingerprint(composition.Shot);
            composition.AppliedJobId = null; composition.ReviewJobId = null; composition.AcceptedRevisionId = null;
            composition.History = []; composition.Version = 1;
            if (targetProduction.Compositions.All(c => c.Id != composition.Id)) targetProduction.Compositions.Add(composition);
            if (targetProduction.ShotContent.All(c => c.ShotId != composition.ShotId)) targetProduction.ShotContent.Add(ShotProductionContent.From(composition));
        }
        if (copiedShots.Any(s => latest.Shots.Any(existing => existing.Id == s.Id)) || copiedTakes.Any(t => latest.Takes.Any(existing => existing.Id == t.Id)))
            throw new WorkspaceStoreException("A copied shot identity already exists without its acknowledgement. Nothing was overwritten.");
        latest.Recovery.Insert(0, new(Guid.NewGuid(), clock.GetUtcNow(), "Before copying shots from another project", ShotCopy.Of(latest.Shots)) { SceneSetups = ShotCopy.Of(latest.SceneSetups) });
        latest.Recovery = latest.Recovery.Take(30).ToList();
        latest.Shots.AddRange(copiedShots); latest.Takes.AddRange(copiedTakes);
        latest.ProjectCopies.Add(new(request.Id, fingerprint, request.SourceProjectId, copiedShots.Select(s => s.Id).ToArray(), clock.GetUtcNow()));
        latest.Revision++; latest.UpdatedUtc = clock.GetUtcNow();
        FileShotStore.Validate(latest, request.DestinationProjectId);
        targetProduction.Revision++;
        // Dependencies may survive an interrupted publication; the saved operation resumes them.
        if (additions.Count > 0) {
            var recovery = new ScriptRecovery(Guid.NewGuid(), "Before copying shot scenes", clock.GetUtcNow(), targetScript);
            await AtomicJsonFile.WriteAsync(Path.Combine(destination, "script-history", recovery.Id.ToString("D") + ".json"), recovery, ct);
            await AtomicJsonFile.WriteAsync(Path.Combine(destination, "script.json"), nextScript, ct);
        }
        await AtomicJsonFile.WriteAsync(Path.Combine(destination, "production.json"), targetProduction, ct);
        await AtomicJsonFile.WriteAsync(Path.Combine(destination, "shots.json"), latest, ct);
        return new(request.DestinationProjectId, copiedShots.Select(s => s.Id).ToArray());
    }

    private static ShotProjectCopyResult Result(ShotProjectCopyReceipt receipt, ShotProjectCopyRequest request, string fingerprint)
    {
        if (receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This copy operation belongs to another selection.");
        return new(request.DestinationProjectId, receipt.ShotIds);
    }

    private static ShotProjectCopyPackage Capture(ShotProjectCopyRequest request, Source data)
    {
        var selected = Selected(data, request.ShotIds); var blocks = Blocks(data, selected); var takes = Takes(data, selected);
        var map = new Dictionary<Guid, Guid> { [request.SourceProjectId] = request.DestinationProjectId };
        void Add(Guid id, string kind) => map.TryAdd(id, AssetReusePolicy.Identity(request.Id, kind, id));
        foreach (var shot in selected) Add(shot.Id, "shot");
        foreach (var block in blocks) Add(block.Id, "script-block");
        var compositions = data.Production.Compositions.Where(c => selected.Any(s => s.Id == c.ShotId)).Select(c => c.Copy()).ToList();
        foreach (var composition in compositions) Add(composition.Id, "composition");
        foreach (var take in takes) Add(take.Id, "take");
        List<AssetReuseCommand> commands = [];
        foreach (var assetId in AssetIds(data, selected)) {
            var commandId = AssetReusePolicy.Identity(request.Id, "asset-command", assetId);
            var selection = new AssetReuseSelection(request.SourceProjectId, assetId);
            var content = AssetReusePolicy.Capture(data.Assets, selection);
            commands.Add(new(commandId, selection, null, AssetReusePolicy.Hash(content), new(request.DestinationProjectId, content.Asset.Name)));
            void AssetId(Guid id, string kind) => map[id] = AssetReusePolicy.Identity(commandId, kind, id);
            AssetId(assetId, "asset");
            foreach (var image in content.Asset.Images) AssetId(image.Id, "image");
            foreach (var look in content.Asset.Looks) AssetId(look.Id, "look");
            foreach (var voice in content.Voices) AssetId(voice.Id, "voice");
            foreach (var reel in content.Reels) { AssetId(reel.Id, "reel"); AssetId(reel.Media.Id, "reel-media"); }
        }
        var copiedTakes = ShotCopy.Of(takes.ToList());
        foreach (var take in copiedTakes) {
            take.CopySource = new(request.SourceProjectId, take.Id, ShotCopy.Of(take.Snapshot));
            // A copied take is not a new publication by the original AI request.
            take.AiJobId = null;
        }
        return new(request, selected.Select(s => s.Copy()).ToList(), compositions.Select(c => new ShotProjectCopySetup(c, c.Shot.Copy())).ToList(), blocks, copiedTakes, commands, map);
    }

    private static T Remap<T>(T value, IReadOnlyDictionary<Guid, Guid> map, Guid destination)
    {
        var node = JsonSerializer.SerializeToNode(value, AtomicJsonFile.Options)!;
        void Visit(JsonNode current) {
            if (current is JsonObject obj) {
                var accepted = obj["refMod"] is not null && obj.ContainsKey("keyframes") && obj.ContainsKey("media")
                    ? obj.Deserialize<ShotVideoBinding>(AtomicJsonFile.Options) : null;
                if (accepted is not null && !ReelRefMods.Matches(accepted, accepted.RefMod)) accepted = null;
                foreach (var key in obj.Select(p => p.Key).Where(k => k is not ("copySource" or "refinementPackage" or "sourcePackage" or "refinementInput" or "key")).ToArray()) Replace(obj[key], v => obj[key] = v);
                if (accepted is not null) {
                    var mapped = obj.Deserialize<ShotVideoBinding>(AtomicJsonFile.Options)!;
                    var prior = accepted.RefMod!;
                    var recipe = ReelRefMods.Recipe(mapped, prior.Recipe.Width, prior.Recipe.Height, prior.Recipe.VaeName, prior.Recipe.FrameHashes);
                    obj["refMod"] = JsonSerializer.SerializeToNode(ReelRefModPreparation.UnbuiltReference(destination, recipe, prior.ComfyUrl), AtomicJsonFile.Options);
                }
                if (obj.ContainsKey("comfyUrl") && obj.ContainsKey("frameCount") && obj["shot"] is { } shot)
                    obj["fingerprint"] = H3Policy.Fingerprint(shot.Deserialize<Shot>(AtomicJsonFile.Options)!);
            }
            else if (current is JsonArray array) for (var i = 0; i < array.Count; i++) { var index = i; Replace(array[i], v => array[index] = v); }
        }
        void Replace(JsonNode? child, Action<JsonNode> replace) {
            if (child is JsonValue scalar && scalar.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && map.TryGetValue(id, out var mapped)) replace(JsonValue.Create(mapped)!);
            else if (child is not null) Visit(child);
        }
        Visit(node);
        return node.Deserialize<T>(AtomicJsonFile.Options)!;
    }

    private async Task CopyRefModSources(ShotProjectCopyPackage package, CancellationToken ct)
    {
        var store = new ReelRefModStore(files);
        var copied = new HashSet<string>();
        var contexts = package.Takes.SelectMany(t => TakeBundles.Contexts(t)).Select(c => c.Take.Snapshot.Shot)
            .Concat(package.Takes.Where(t => t.CopyRequest?.Extension is not null)
                .SelectMany(t => TakeBundles.Contexts(t.CopyRequest!.Extension!.Source)).Select(c => c.Take.Snapshot.Shot));
        foreach (var binding in contexts.SelectMany(s => s.Videos).Where(v => v.EffectiveVisuals == ReelVisuals.RefMod)) {
            ReelRefMods.ValidateBinding(binding, true);
            var mapped = Remap(binding, package.Identities, package.Request.DestinationProjectId);
            var recipe = mapped.RefMod!.Recipe;
            if (!copied.Add(recipe.Key)) continue;
            var pixels = new List<byte[]>();
            for (var i = 0; i < recipe.LatentFrames; i++)
                pixels.Add(await store.PreviewAsync(package.Request.SourceProjectId, binding.RefMod!, i, ct));
            await store.AcceptSourcesAsync(package.Request.DestinationProjectId, recipe, pixels, ct);
        }
    }

    private async Task CaptureRequests(ShotProjectCopyPackage package, CancellationToken ct)
    {
        var directory = await files.DirectoryAsync(package.Request.SourceProjectId, ct);
        foreach (var id in package.Takes.SelectMany(t => TakeBundles.Contexts(t)).Select(t => t.Take.Snapshot.Shot.ApprovedScriptId).OfType<Guid>().Distinct()) {
            if (!File.Exists(Path.Combine(directory, "script-sources", id.ToString("D") + ".json")) &&
                !File.Exists(Path.Combine(directory, "script-approved", id.ToString("D") + ".json"))) continue;
            var captured = await scripts.LoadSourceAsync(package.Request.SourceProjectId, id, ct);
            if (captured is null) continue; // Older takes may already lack their historical script.
            package.ScriptSources.Add(captured);
            package.Identities.TryAdd(id, AssetReusePolicy.Identity(package.Request.Id, "script-source", id));
            foreach (var block in captured.Blocks)
                package.Identities.TryAdd(block.Id, AssetReusePolicy.Identity(package.Request.Id, "script-block", block.Id));
        }
        var queue = jobs is null ? null : await jobs.ReadAsync(ct);
        var original = await shots.LoadAsync(package.Request.SourceProjectId, ct);
        foreach (var take in package.Takes) {
            var stored = original.Takes.Single(t => t.Id == take.Id);
            var job = queue?.Jobs.FirstOrDefault(j => j.Id == stored.AiJobId);
            var request = stored.CopyRequest ?? (job is null ? null : AiVideoJobHandler.Read(job, await jobs!.ReadSnapshotAsync(job.Id, ct)));
            if (request is null) continue; // Older imported takes can still be played, trimmed and extended.
            if (request.BatchId != take.RunId || AssetReusePolicy.Hash(request.Snapshot) != AssetReusePolicy.Hash(take.Snapshot))
                throw new WorkspaceStoreException("A saved take does not match its captured inputs. Review it before copying.");
            take.CopyRequest = ShotCopy.Of(request);
            package.Identities.TryAdd(take.RunId, AssetReusePolicy.Identity(package.Request.Id, "run", take.RunId));
        }
    }

    private async Task RelinkReferences(Shot shot, AssetLibrary library, string directory, CancellationToken ct)
    {
        foreach (var image in shot.Images) image.SetupOrigin = null;
        foreach (var video in shot.Videos) {
            var reel = library.Reels.FirstOrDefault(r => r.Media.Id == video.Media.Id && (video.OwnerAssetId is null || r.AssetId == video.OwnerAssetId));
            if (reel is null) throw new WorkspaceStoreException($"The copied reel {video.Name} is missing from its destination asset.");
            video.OwnerAssetId = reel.AssetId; video.Media = reel.Media;
            video.RefMod = null; // A server-side cache is rebuilt for the destination's selection.
            if (video.Keyframes is not { } frames) continue;
            var root = TakeBundles.Under(directory, $"reference-videos/{video.Media.Id:D}/media.json");
            var archivePath = Path.Combine(Path.GetDirectoryName(root)!, "frame-archive.json");
            var archive = File.Exists(archivePath) ? await AtomicJsonFile.ReadAsync<ReelFrameArchive>(archivePath, ct) : null;
            var hasArchive = archive is not null && archive.Files.All(f => File.Exists(TakeBundles.Under(Path.GetDirectoryName(root)!, "lossless/" + f.FileName)));
            for (var i = 0; i < frames.Frames.Count; i++) {
                var frame = frames.Frames[i];
                if (frame.Frame.Source != video.Media.Sha256 && (archive is null || frame.Frame.Source != AssetReusePolicy.Hash(archive)))
                    throw new WorkspaceStoreException("A selected reel frame's source changed. Reselect it before copying.");
                if (frame.Frame.Source == video.Media.Sha256 || hasArchive || File.Exists(Path.Combine(Path.GetDirectoryName(root)!, FileReferenceVideoStore.FrameFileName(frame.Frame)))) continue;
                // Reuse already resolved library picks, including MP4 fallbacks after compaction.
                var saved = reel.Keyframes?.Frames.FirstOrDefault(f => f.Frame.Index == frame.Frame.Index);
                var identity = saved?.Frame;
                if (identity is null) {
                    var store = referenceVideos ?? throw new WorkspaceStoreException("Reel frame extraction is unavailable. Restore the missing reference pictures before copying.");
                    var catalog = await store.FrameCatalogAsync(library.ProjectId, video.Media, settings is null ? new() : (await settings.LoadAsync(ct)).H3, ct);
                    identity = new(video.Media.Id, catalog.Source, frame.Frame.Index, catalog.Timestamps[frame.Frame.Index]);
                }
                frames.Frames[i] = frame with { Frame = identity };
            }
        }
        foreach (var image in shot.Images)
            if (!library.Assets.Any(a => a.Id == image.AssetId && a.Images.Any(i => i.Id == image.MediaId)))
                throw new WorkspaceStoreException($"The copied image {image.Name} is missing from its destination asset.");
        ReferenceVideos.Validate(shot);
    }

    private static async Task CopyInputs(AiVideoJobRequest request, string source, string destination, IReadOnlyDictionary<Guid, Guid> map, CancellationToken ct)
    {
        var run = $"shots/runs/{request.BatchId:D}";
        var copied = Remap(request, map, map[request.Snapshot.ProjectId]);
        var targetRun = $"shots/runs/{copied.BatchId:D}";
        var runDirectory = Path.GetDirectoryName(TakeBundles.Under(source, run + "/run.json"))!;
        async Task CopyFile(string path, string relative, long bytes, string hash) {
            var target = TakeBundles.Under(destination, targetRun + "/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) await RefinementPackages.VerifyFileAsync(target, bytes, hash, ct);
            else {
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await TakeTrimming.CopyVerifiedAsync(path, temporary, bytes, hash, ct); File.Move(temporary, target); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        foreach (var input in request.Inputs) {
            AiVideoJobPolicy.ValidateFileName(input.FileName, input.EffectiveKind);
            var path = CapturedInputStore.Resolve(runDirectory, input.FileName, input.Sha256);
            // Validate the resolved shared input's path too, including linked-file protection.
            path = TakeBundles.Under(source, Path.GetRelativePath(source, path).Replace('\\', '/'));
            await CopyFile(path, "inputs/" + input.FileName, input.Bytes, input.Sha256);
        }
        foreach (var motion in request.Snapshot.Motion?.Files ?? []) {
            await CopyFile(TakeBundles.Under(source, run + "/inputs/" + motion.FileName), "inputs/" + motion.FileName, motion.Bytes, motion.Sha256);
        }
        foreach (var retained in request.Extension?.SourceFiles ?? [])
            await CopyFile(TakeBundles.Under(source, run + "/" + H3Motion.SourceFolder + "/" + retained.FileName), H3Motion.SourceFolder + "/" + retained.FileName, retained.Bytes, retained.Sha256);
        if (request.Refinement is { } refinement)
            await CopyFile(TakeBundles.Under(source, run + "/inputs/" + H3RefinementPackage.FileName), "inputs/" + H3RefinementPackage.FileName, refinement.SourcePackage.Bytes, refinement.SourcePackage.Sha256);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(copied, Path.GetDirectoryName(TakeBundles.Under(destination, targetRun + "/run.json"))!, ct);
    }

    private static async Task CopyTake(ShotTake take, string source, string destination, Guid id, CancellationToken ct)
    {
        var original = TakeBundles.Under(source, $"shots/takes/{take.Directory}/video.mp4");
        var target = TakeBundles.Under(destination, $"shots/takes/{id:D}/video.mp4");
        var staging = Path.Combine(destination, "shot-copy-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try {
            if (Directory.Exists(Path.GetDirectoryName(target))) {
                foreach (var relative in TakeBundles.Files(take).Distinct()) {
                    var from = TakeBundles.Under(Path.GetDirectoryName(original)!, relative);
                    await RefinementPackages.VerifyFileAsync(TakeBundles.Under(Path.GetDirectoryName(target)!, relative), new FileInfo(from).Length, await CapturedInputStore.HashAsync(from, ct), ct);
                }
                return;
            }
            await TakeBundles.CopyAsync(take, Path.GetDirectoryName(original)!, staging, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(target)!)!);
            await DurableFile.MoveDirectoryAsync(staging, Path.GetDirectoryName(target)!, ct);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
