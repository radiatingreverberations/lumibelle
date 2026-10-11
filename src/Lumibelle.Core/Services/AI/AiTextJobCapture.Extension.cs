using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> ComposeExtensionAsync(Guid id, Guid tab, Guid projectId, TakeExtensionOptions options, TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        if (!TextVisionPolicy.SupportsBackend(model.Backend)) throw new AiGenerationException(TextVisionPolicy.SetupHint);
        var store = shots ?? throw new WorkspaceStoreException("Shot storage is unavailable.");
        var leading = options.Direction == TakeExtensionDirection.Before;
        var (captured, motion) = leading
            ? await store.CaptureLeadInAsync(projectId, options.SourceTakeId, id, options.StartFrame, options.AddedSeconds, options.Combine, ct)
            : await store.CaptureExtensionAsync(projectId, options.SourceTakeId, id, options.EndFrameExclusive, options.AddedSeconds, options.Combine, ct);
        var run = await store.RunDirectoryAsync(projectId, id, ct);
        try {
        var take = captured.Source; var contextTake = leading ? H3Motion.Head(take, options.StartFrame).Segment : H3Motion.Tail(take, options.EndFrameExclusive).Segment; var shot = AiVideoJobCapture.ExtensionShot(contextTake.Source, options, motion.GenerationFrames);
        shot.Id = take.ShotId;
        var directory = Path.Combine(await store.RunDirectoryAsync(projectId, id, ct), H3Motion.SourceFolder);
        var contextFolder = take.Composition is null ? directory : Path.Combine(directory, "segments", contextTake.Key.ToString("D"));
        var images = new List<byte[]>(); var identities = new List<CompositionInput>();
        if (options.References is { } references) {
            AiVideoJobCapture.ValidateExtensionReferences(shot, references, await assets.LoadAsync(projectId, ct), await store.LoadAsync(projectId, ct));
            var selected = await ProductionInputs.CaptureAsync(projectId, shot, assets, ct, referenceVideos, contextTake.Source.Snapshot.Settings, store);
            images.AddRange(selected.Select(i => i.Bytes)); identities.AddRange(selected.Select(i => i.Identity));
        }
        else {
            var inputs = contextTake.Source.RetainedSource!.Inputs.Where(i => i.EffectiveKind == VideoInputKind.Image).ToArray();
            var pictures = ResolvedReferences.For(shot).Pictures;
            for (var i = 0; i < inputs.Length; i++) {
                images.Add(await File.ReadAllBytesAsync(Path.Combine(contextFolder, TakeTrimming.InputsFolder, inputs[i].FileName), ct));
                identities.Add(new(pictures[i].BindingId, inputs[i].Sha256));
            }
        }
        var stills = new List<byte[]>(); var stillIds = new List<CompositionInput>();
        foreach (var frame in new[] { motion.StartFrame, (motion.StartFrame + motion.EndFrameExclusive - 1) / 2, motion.EndFrameExclusive - 1 }.Distinct()) {
            await using var stream = await TakeFrameReader.Shared.OpenAsync(directory, take, frame, ct);
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, ct); var pixels = buffer.ToArray();
            stills.Add(pixels); stillIds.Add(new(take.Id, Convert.ToHexString(SHA256.HashData(pixels))));
        }
        var mods = ReelRefMods.Uses(shot) ? await (refmods ?? throw new WorkspaceStoreException("RefMod preview storage is unavailable.")).InspectionAsync(projectId, shot, ct) : Array.Empty<RefModInspectionFrame>();
        var request = new PromptCompositionRequest(projectId, id, 1, take.Snapshot.Fingerprint, take.Snapshot.Fingerprint, shot, "", [], options.References?.Guidance ?? contextTake.Source.Snapshot.ReferenceGuidance!, options.References?.Appearances ?? contextTake.Source.Snapshot.Appearances!, identities,
            leading ? "Generate the preceding action leading into the captured opening motion." : "Continue the captured motion into the next action.", options.Prompt ?? "", "", model, followsDefault) {
                PrecedingAction = leading ? null : contextTake.Source.Snapshot.Shot.Description,
                FollowingAction = leading ? contextTake.Source.Snapshot.Shot.Description : null, MotionStills = stillIds };
        return await BuildAsync(id, tab, AiJobKind.PromptComposition, new(projectId, ShotId: take.ShotId, CompositionId: id), shot.Title + " · Compose extension", request,
            model, followsDefault, Copy(await settings.LoadAsync(ct)), ProductionPolicy.Profile, PromptComposer.BuildMessages(request, images, mods, motionStills: stills), .7f, null, ct);
        } finally {
            // Text requests own the encoded image attachments. The temporary full take is no longer needed.
            foreach (var folder in new[] { Path.Combine(run, H3Motion.SourceFolder), Path.Combine(run, "inputs") })
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
