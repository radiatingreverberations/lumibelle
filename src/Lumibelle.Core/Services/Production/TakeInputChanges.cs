using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

[Flags]
public enum TakeInputChange { None = 0, Script = 1, Prompt = 2, References = 4, Unavailable = 8 }

public static class TakeInputChanges
{
    public static TakeInputChange Compare(VideoSnapshot captured, Shot? source, ProductionComposition? setup,
        AssetLibrary assets, ShotDocument shots, IReadOnlyList<ScriptBlock>? originalScript, IReadOnlyList<ScriptBlock>? currentScript)
    {
        var changes = TakeInputChange.None;
        if (source is null) changes |= TakeInputChange.Unavailable;
        else if (Story(captured.Shot) != Story(source)) changes |= TakeInputChange.Script;

        // Compare only this scene, not the project-wide script revision or edits in other scenes.
        var originalScene = Scene(originalScript, captured.Shot.SceneId);
        if (originalScene is null || currentScript is null) changes |= TakeInputChange.Unavailable;
        else if (originalScene != Scene(currentScript, captured.Shot.SceneId)) changes |= TakeInputChange.Script;

        // The setup adapter carries the shot's current shared prompt and references.
        // Its generation settings do not affect whether the authored inputs changed.
        if (captured.Production is not { } origin || setup is null || source is null || setup.ShotId != source.Id ||
            source.Id == captured.Shot.Id && setup.Id != origin.CompositionId)
            return changes | TakeInputChange.Unavailable;
        if (Text(captured.Prompt) != Text(setup.Prompt)) changes |= TakeInputChange.Prompt;
        var current = setup.Shot;
        if (References(captured.Shot) != References(current) ||
            !captured.ReferenceGuidance.Select(g => (g.Effective, g.Phase)).SequenceEqual(ShotReferences.Resolve(current, assets, shots).Select(g => (g.Effective, g.Phase))) ||
            // Library organization is not the shot's captured look assignment. A
            // pre-existing mismatch must not mark every newly generated take stale.
            current.Images.Any(b => b.Kind != ShotImageKind.AssetImage || !assets.Assets.Any(a => a.Id == b.AssetId && a.Images.Any(i => i.Id == b.MediaId))) ||
            current.Voices.Any(b => !assets.Voices.Any(v => v.Matches(b))))
            changes |= TakeInputChange.References;
        try
        {
            if (!captured.Appearances.SequenceEqual(ShotLooks.Capture(current, assets))) changes |= TakeInputChange.References;
        }
        catch (WorkspaceStoreException) { changes |= TakeInputChange.References; }
        return changes;
    }

    private static string Text(string value) => value.Replace("\r\n", "\n").Trim();
    private static string Story(Shot shot) => ReferenceSetups.Hash(new
    {
        shot.SceneId, shot.Description, shot.Duration, shot.Atmosphere, shot.Music,
        Dialogue = shot.Dialogue.Select(d => new { d.Speaker, d.Language, d.Text }),
        Characters = ShotReferences.Characters(shot).Select(c => c.Name)
    });
    private static string? Scene(IReadOnlyList<ScriptBlock>? blocks, Guid? id)
    {
        if (blocks is null || id is null) return null;
        var scene = ScriptStructure.Sections(blocks).FirstOrDefault(s => s.Kind == ScriptBlockKind.Scene && s.Id == id);
        return scene is null ? null : ReferenceSetups.Hash(blocks.Skip(scene.Start).Take(scene.Count).Select(b => new { b.Kind, b.Text }));
    }
    private static string References(Shot shot)
    {
        var references = ReferenceSetups.Hash(new
        {
            Images = shot.Images.Select(b => new { b.Kind, b.AssetId, b.MediaId, b.Crop, b.AiUseHint, b.InferUsage, b.Use, b.Role, b.RepresentsId, b.LookId, b.Purpose }),
            Voices = shot.Voices.Select(b => new { b.VoiceId, b.AssetId, b.CharacterAssetId, b.Speaker, b.Start, b.Duration }),
            Videos = shot.Videos.Select(b => new { b.Media, b.Description, b.EffectiveVisuals, b.Keyframes, b.UseSoundtrack, b.Speaker, b.AudioExcerpt })
        });
        // Joined only when set, so takes from before starting frames keep their comparison.
        if (shot.StartFrame is not null) references = ReferenceSetups.Hash(new { References = references, shot.StartFrame });
        return shot.ContinuityFrame is null ? references : ReferenceSetups.Hash(new { References = references, Continuity = new { shot.ContinuityFrame.TakeId, shot.ContinuityFrame.Frame } });
    }
}
