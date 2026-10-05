using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ComfyH3Video
{
    internal Task ValidateRefModsAsync(VideoSnapshot snapshot, CancellationToken ct,
        IReadOnlyDictionary<Guid, ReelRefModReference>? prepared = null) =>
        ReelRefMods.Uses(snapshot.Shot) ? new ComfyRefModClient(clients).ValidateReferencesAsync(snapshot, ct, prepared) : Task.CompletedTask;

    internal static void RefModConditioning(VideoSnapshot snapshot, IReadOnlyList<PreparedVideoInput> inputs,
        Action<string, string, object> node, IReadOnlyDictionary<Guid, ReelRefModReference>? prepared = null)
    {
        static object Link(string id, int port = 0) => new object[] { id, port };
        var references = ResolvedReferences.For(snapshot.Shot).Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod).ToArray();
        if (references.Length == 0) throw new WorkspaceStoreException("No selected visual RefMod was supplied.");
        var materialized = ReelRefMods.ExecutionReferences(snapshot, prepared);
        // Fantastic resolves stems under models/refmods. No Create or Apply node is present
        // in a generation workflow. Missing caches are built by the owning queue job
        // before this workflow is captured; the authored snapshot is unchanged.
        var state = new { picks = references.Select(v => new { on = true,
            visual = new { file = materialized[v.Reel.Id].FileName, w = 1.0 } }), budget = ReelRefMods.MaximumTotalTokens };
        node("refmods", ComfyRefModClient.LoadNode, new { stack_state = JsonSerializer.Serialize(state) });
        // All audio is explicitly standalone on this experimental path, including already
        // extracted full-reel soundtracks. Preserve the resolver's Audio order, and never
        // extract a second soundtrack from an MP4 in the Media Loader.
        var media = inputs.Select(input => new {
            kind = input.EffectiveKind switch { VideoInputKind.Image => "picture", VideoInputKind.Video => "video",
                VideoInputKind.Audio or VideoInputKind.VideoSoundtrack => "audio",
                _ => throw new WorkspaceStoreException("Unsupported Fantastic media input.") },
            file = input.FileName, enabled = true, has_audio = false, audio_mode = "off"
        }).ToArray();
        if (media.Length > 0) node("refmedia", ComfyRefModClient.MediaNode, new { media_state = JsonSerializer.Serialize(media) });
        var encode = new Dictionary<string, object> { ["clip"] = Link("2"), ["vae"] = Link("3"), ["audio_vae"] = Link("4"),
            ["prompt"] = snapshot.Prompt, ["width"] = snapshot.Width, ["height"] = snapshot.Height, ["length"] = snapshot.FrameCount,
            ["ref_image_size"] = "match", ["reference_fps"] = 24.0, ["max_total_tokens"] = ReelRefMods.MaximumTotalTokens, ["mods"] = Link("refmods"),
            // Show H3's text encoder every selected keyframe. Fantastic's default shows the first and every 4th
            // after it, so a 3-keyframe RefMod was seen as one picture; ours hold at most nine, so all is cheap.
            ["stack_pictures"] = ComfyRefModClient.EncoderPictures };
        if (media.Length > 0) encode["references"] = Link("refmedia");
        node("5", ComfyRefModClient.EncodeNode, encode);
    }
}
