using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using lumibelle.Models;

namespace lumibelle.Services.Shots;

internal static class H3PromptCompiler
{
    internal static string Compile(Shot shot, IReadOnlyList<ShotReferenceGuidance>? guidance, IReadOnlyList<ShotAppearanceContext>? appearances, bool motionContext = false)
    {
        var characters = ShotReferences.Characters(shot);
        var speakerNames = shot.Dialogue.Select(d => d.Speaker);
        if (motionContext) speakerNames = speakerNames.Concat(shot.Voices.Select(v => v.Speaker));
        var speakers = speakerNames.Select(name => ShotReferences.Character(shot, name)!).DistinctBy(c => c.Id).ToList();
        // Picture numbers follow submission order; several pictures may describe one subject.
        var groups = shot.Images.Select((image, index) => (Image: image, Picture: index + 1))
            .Where(x => !ReferenceSetups.IsAnchor(x.Image))
            .GroupBy(x => x.Image.RepresentsId is { } id ? $"character:{id}" :
                x.Image.Kind == ShotImageKind.AssetImage ? $"asset:{x.Image.AssetId}" : $"frame:{x.Image.Id}").ToList();
        var subjectByImage = new Dictionary<Guid, int>();
        var subjectByCharacter = new Dictionary<Guid, int>();
        var aliases = new List<(string Name, int Subject)>();
        var text = new StringBuilder("subject_definitions:\n");
        for (var i = 0; i < groups.Count; i++)
        {
            var subject = i + 1;
            var first = groups[i].First().Image;
            var character = characters.FirstOrDefault(c => c.Id == first.RepresentsId);
            var name = character?.Name ?? first.Name;
            var pictures = string.Join(" and ", groups[i].Select(x => $"<Picture {x.Picture}>"));
            text.AppendLine($"<Subject {subject}> is {name} from {pictures}.");
            if (character is not null) subjectByCharacter[character.Id] = subject;
            aliases.Add((name, subject));
            foreach (var entry in groups[i])
            {
                subjectByImage[entry.Image.Id] = subject;
                aliases.Add((entry.Image.Name, subject));
            }
        }
        var anchors = shot.Images.Select((image, index) => (Image: image, Picture: index + 1)).Where(x => ReferenceSetups.IsAnchor(x.Image)).ToArray();
        foreach (var anchor in anchors)
            text.AppendLine($"<Picture {anchor.Picture}> defines the {(anchor.Image.Use == ShotImageUse.FirstFrame ? "first" : "last")} frame of [Shot 1]. Use the submitted image, including its selected crop, as the whole-frame composition anchor.");
        string Speaker(string name)
        {
            var character = ShotReferences.Character(shot, name)!;
            var label = subjectByCharacter.TryGetValue(character.Id, out var subject) ? $"<Subject {subject}>" : character.Name;
            return $"{label} (S{speakers.FindIndex(c => c.Id == character.Id) + 1})";
        }
        // Preserve legacy compiled prompts verbatim. New RefMod requests include their
        // actual Video labels and independently numbered voice/reel-audio references.
        var sparse = lumibelle.Services.Production.ReelRefMods.Uses(shot);
        if (sparse)
        {
            var resolved = lumibelle.Services.Production.ResolvedReferences.For(shot);
            foreach (var picture in resolved.Pictures.Where(p => p.Keyframe is not null))
                text.AppendLine($"<Picture {picture.Number}> is a selected still of {picture.Reel!.Name}. {picture.Reel.Description} {picture.Keyframe!.Notes}");
            foreach (var video in resolved.Videos)
                text.AppendLine($"<Video {video.Number}> supplies the visual reference for {video.Reel.Name}. {video.Reel.Description} " +
                    (video.Reel.EffectiveVisuals == ReelVisuals.RefMod ? lumibelle.Services.Production.ReelRefMods.UseGuidance : "Use the setting or appearance, not the source camera movement or actions."));
            foreach (var audio in resolved.Audio.Where(a => a.Reel is not null))
                text.AppendLine($"<Audio {audio.Number}> supplies voice identity" +
                    (string.IsNullOrWhiteSpace(audio.Speaker) ? "." : $" for {Speaker(audio.Speaker)}.") + " Do not copy the source words or background sound.");
        }
        int VoiceNumber(int index) => sparse ? lumibelle.Services.Production.ReferenceVideos.VoiceNumber(shot, index) : index + 1;
        for (var i = 0; i < shot.Voices.Count; i++)
            text.AppendLine($"<Audio {VoiceNumber(i)}> is the voice-timbre reference for {Speaker(shot.Voices[i].Speaker)}. Follow the voice identity without copying the original words.");
        text.AppendLine("\nsummary:\n[" + (anchors.Length > 0 ? "keyframe completion + " : "") + "reference generation" + (shot.Voices.Count > 0 ? " + audio reference" : "") + "] " + shot.Title);
        text.AppendLine("\nretention_analysis:");
        foreach (var appearance in appearances ?? [])
        {
            var character = characters.Single(c => c.Id == appearance.CharacterId);
            var label = subjectByCharacter.TryGetValue(character.Id, out var number) ? $"<Subject {number}> ({character.Name})" : character.Name;
            text.AppendLine($"{label}: the same character throughout the take. Shared identity: {appearance.Start.IdentityNotes} {appearance.Start.IdentityGuidance}");
            if (appearance.End is { } end)
            {
                text.AppendLine($"{label} changes appearance during this continuous take, without a cut. Starting look — {appearance.Start.LookName}: {appearance.Start.Description} {appearance.Start.PreservationGuidance}");
                text.AppendLine($"Ending look — {end.LookName}: {end.Description} {end.PreservationGuidance}. Starting clothing and styling are allowed to change into the ending look; preserve physical identity, not the starting costume throughout. Follow the authored action without inventing a transformation mechanism.");
            }
            else text.AppendLine($"Current look — {appearance.Start.LookName}: {appearance.Start.Description} {appearance.Start.PreservationGuidance}");
            text.AppendLine("Look-specific clothing and styling take precedence over incidental wardrobe in shared notes. Image guidance applies only to its assigned appearance phase; explicit shot overrides take precedence for that reference.");
        }
        for (var i = 0; i < shot.Images.Count; i++)
        {
            var image = shot.Images[i];
            var preserve = guidance?.SingleOrDefault(g => g.BindingId == image.Id)?.Effective ?? image.PreservationOverride ?? image.Notes;
            if (ReferenceSetups.IsAnchor(image))
            {
                text.AppendLine($"<Picture {i + 1}> ([Shot 1] {(image.Use == ShotImageUse.FirstFrame ? "first" : "last")} frame): fully_preserved - Match the framing, camera position, subject placement and pose at this endpoint. Preserve this endpoint role, not a frozen image throughout the take. {preserve}");
                continue;
            }
            var phase = image.Purpose is { } purpose ? ShotLooks.Label(purpose) : image.Use is { } use ? ReferenceSetups.UseLabel(use) : image.Role;
            text.AppendLine($"<Subject {subjectByImage[image.Id]}> from <Picture {i + 1}>: reference role: {phase}. {preserve}");
            if (image.Purpose == ShotReferencePurpose.Identity) text.AppendLine("Use this picture only for physical identity; do not transfer its clothing, hairstyle, pose or environment unless explicitly requested in the shot override.");
        }
        for (var i = 0; i < shot.Voices.Count; i++) text.AppendLine($"<Audio {VoiceNumber(i)}>: reference — preserve speaker identity, not the source signal or words.");
        text.AppendLine("\ndetailed_description:");
        foreach (var anchor in anchors.Where(a => a.Image.Use == ShotImageUse.FirstFrame))
            text.AppendLine($"[Shot 1] The shot begins from <Picture {anchor.Picture}>, matching its opening composition before continuing the authored action naturally.");
        text.AppendLine($"[Shot 1] One continuous camera take lasting {H3Policy.Seconds(shot.Duration!.Value).ToString("0.###", CultureInfo.InvariantCulture)} seconds, without camera cuts or scene transitions. {Annotate(shot.Description, aliases)}");
        foreach (var anchor in anchors.Where(a => a.Image.Use == ShotImageUse.LastFrame))
            text.AppendLine($"The shot ends on <Picture {anchor.Picture}>: the movement arrives at its framing, subject placement and pose by the end of this same continuous take, without a cut.");
        foreach (var line in shot.Dialogue) text.AppendLine($"{Speaker(line.Speaker)} says <d>[{line.Language}] {line.Text}</d>");
        text.AppendLine("\noverall_soundscape:\n" + shot.Atmosphere);
        text.AppendLine("\nnon_diegetic_music:\n" + shot.Music);
        return text.ToString();
    }

    private static string Annotate(string description, List<(string Name, int Subject)> aliases)
    {
        var names = aliases.Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Select(a => a.Subject).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Subject, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return description;
        // Do not put labels inside visible lettering or quoted speech. Exact dialogue is compiled separately.
        var quoted = "\"(?:\\\\.|[^\"\\\\])*\"|“[^”]*”|‘[^’]*’|'[^'\\r\\n]*'";
        var pattern = quoted + "|(?<![\\p{L}\\p{N}_])(?:" + string.Join("|", names.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + ")(?![\\p{L}\\p{N}_])";
        return Regex.Replace(description, pattern, m => names.TryGetValue(m.Value, out var subject) ? $"{m.Value} (<Subject {subject}>)" : m.Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
