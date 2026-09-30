using System.Text.RegularExpressions;
using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class ProductionPolicy
{
    public const string Profile = "h3-composed-v2";
    public static readonly string[] Sections = ["subject_definitions", "summary", "retention_analysis", "detailed_description", "overall_soundscape", "non_diegetic_music"];
    internal const string SectionPattern = @"(?m)^\s*(subject_definitions|summary|retention_analysis|detailed_description|overall_soundscape|non_diegetic_music):\s*";
    internal static string[] SectionNames(string prompt) => Regex.Matches(prompt, SectionPattern).Select(m => m.Groups[1].Value).ToArray();
    public static string SourceFingerprint(Shot s) => ReferenceSetups.Hash(new
    {
        s.Id, s.Title, s.ApprovedScriptId, s.SceneId, s.SceneTitle, s.SourceBlockIds, s.SourceExcerpt,
        s.Duration, s.Description, s.Dialogue, Characters = ShotReferences.Characters(s).Select(c => new { c.Id, c.Name }), s.Atmosphere, s.Music
    });
    public static Shot CoverageCopy(Shot source)
    {
        var s = new Shot(); CopyCoverage(source, s); s.SelectedTakeId = null; return s;
    }
    public static void CopyCoverage(Shot source, Shot target)
    {
        var looks = target.Characters.ToDictionary(c => c.Id, c => c.Appearance);
        target.Id = source.Id; target.Title = source.Title; target.ApprovedScriptId = source.ApprovedScriptId;
        target.SceneId = source.SceneId; target.SceneTitle = source.SceneTitle; target.SourceBlockIds = [..source.SourceBlockIds];
        target.SourceExcerpt = source.SourceExcerpt; target.Planning = source.Planning; target.Duration = source.Duration;
        target.Description = source.Description; target.Dialogue = ShotCopy.Of(source.Dialogue);
        target.Characters = ShotReferences.Characters(source).Select(c => c with { Appearance = looks.GetValueOrDefault(c.Id) }).ToList();
        target.Atmosphere = source.Atmosphere; target.Music = source.Music;
        foreach (var b in target.Images.Where(b => b.RepresentsId is { } id && target.Characters.All(c => c.Id != id))) { b.RepresentsId = null; b.Purpose = null; }
    }
    public static string ContextFingerprint(ProductionComposition c, AssetLibrary assets, ShotDocument shots, ProjectInfo project)
    {
        var source = shots.Shots.SingleOrDefault(s => s.Id == c.ShotId) ?? throw new WorkspaceStoreException("The source shot was removed. Restore it before continuing production.");
        var shot = ShotVideoDefaults.Capture(c.Shot, project);
        var records = shot.Images.Select(b =>
        {
            var owner = assets.Assets.FirstOrDefault(a => a.Id == b.AssetId);
            var image = owner?.Images.FirstOrDefault(i => i.Id == b.MediaId);
            return new { b.Id, Available = image is not null, image?.FileName, image?.Width, image?.Height, image?.CreatedUtc, image?.LookId };
        }).ToArray();
        var context = new { Source = SourceFingerprint(source), Content = SourceFingerprint(shot), shot.Characters,
            shot.Images, shot.Voices, shot.Aspect, shot.Loras, c.DirectingNotes, Records = records,
            Guidance = ShotReferences.Resolve(shot, assets, shots), Appearances = ShotLooks.Capture(shot, assets) };
        var fingerprint = shot.CharacterVoices is not null ? ReferenceSetups.Hash(new { Context = context, shot.Videos, shot.CharacterVoices })
            : shot.Videos.Count == 0 ? ReferenceSetups.Hash(context) : ReferenceSetups.Hash(new { Context = context, shot.Videos });
        // Shot LoRAs join the context only when chosen, so prompts reviewed before they existed keep their fingerprint.
        if (shot.ShotLoras is { Count: > 0 } own) fingerprint = ReferenceSetups.Hash(new { Base = fingerprint, ShotLoras = own });
        return CompositionDescriptions.Fingerprint(fingerprint, shot, assets);
    }
    // What changed since a composition request, in the author's terms, so a changed
    // response can be applied knowingly instead of silently refused.
    public static IReadOnlyList<string> CompositionChanges(PromptCompositionRequest request, ProductionComposition c,
        AssetLibrary assets, ShotDocument shots, ProjectInfo project, IEnumerable<CompositionInput> images)
    {
        var changes = new List<string>();
        var source = shots.Shots.SingleOrDefault(s => s.Id == c.ShotId) ?? throw new WorkspaceStoreException("The source shot was removed. Restore it before continuing production.");
        if (source.Title != request.Shot.Title) changes.Add("the shot was renamed");
        // Compare everything else as if the shot still had its requested title.
        var renamedShots = shots.Copy(); renamedShots.Shots.Single(s => s.Id == c.ShotId).Title = request.Shot.Title;
        var renamed = c.Copy(); renamed.Shot.Title = request.Shot.Title;
        var shot = ShotVideoDefaults.Capture(renamed.Shot, project);
        if (SourceFingerprint(shot) != SourceFingerprint(request.Shot)) changes.Add("the shot’s scene, action, dialogue or cast changed");
        if (c.DirectingNotes != request.DirectingNotes) changes.Add("the Direction for AI changed");
        var referencesChanged = !images.SequenceEqual(request.Images) || PromptReferenceFreshness.Fingerprint(shot, ShotReferences.Resolve(shot, assets, renamedShots))
            != PromptReferenceFreshness.Fingerprint(request.Shot, request.Guidance);
        if (referencesChanged) changes.Add("the references, crops or their guidance changed");
        if (c.Prompt != request.CurrentPrompt) changes.Add("the prompt was edited");
        if (c.RevisionNotes != request.RevisionNotes) changes.Add("the revision notes changed");
        if (changes.Count == (source.Title != request.Shot.Title ? 1 : 0) && request.ContextFingerprint != ContextFingerprint(renamed, assets, renamedShots, project))
            changes.Add("other shot inputs changed, such as looks, voices, LoRAs or aspect");
        return changes;
    }
    public static string? MediaIssue(ShotImageBinding b, AssetLibrary library) => b.Kind != ShotImageKind.AssetImage || !library.Assets.Any(a => a.Id == b.AssetId && a.Images.Any(i => i.Id == b.MediaId)) ? "Restore or replace the unavailable reference." : null;
    public static string? Issue(ProductionComposition c, AssetLibrary assets, ShotDocument shots, ProjectInfo project)
    {
        try
        {
            if (ShotPreparationIssue(c.Shot) is { } preparation) return preparation;
            H3Policy.Validate(ShotVideoDefaults.Capture(c.Shot, project), true);
            return PromptReviewIssue(c, assets, shots, project);
        }
        catch (WorkspaceStoreException e) { return e.Message; }
    }
    public static string? ShotPreparationIssue(Shot shot) =>
        shot.ApprovedScriptId is null || shot.SceneId is null ? "Choose a scene in Shot before generating."
        : string.IsNullOrWhiteSpace(shot.Description) ? "Add action and camera direction in Shot before generating."
        : shot.Duration is null ? "Set a duration in Shot before generating." : null;

    // Reviewing authored text does not require a shot to be ready for generation.
    public static string? PromptReviewIssue(ProductionComposition c, AssetLibrary assets, ShotDocument shots, ProjectInfo project)
    {
        try
        {
            if (c.Archived) return "This composition is archived.";
            var source = shots.Shots.SingleOrDefault(s => s.Id == c.ShotId);
            if (source is null) return "The source shot was removed. Restore it to continue.";

            var shot = ShotVideoDefaults.Capture(c.Shot, project); H3Policy.Validate(shot);
            foreach (var b in shot.Images) if (MediaIssue(b, assets) is { } issue) return issue;
            if (shot.Voices.Any(v => !assets.Voices.Any(a => a.Matches(v)))) return "Restore or replace unavailable voices.";
            if (string.IsNullOrWhiteSpace(c.Prompt)) return "Write a prompt before marking it reviewed.";
            ValidateGenerationPrompt(c.Prompt);
            return null;
        }
        catch (WorkspaceStoreException e) { return e.Message; }
    }
    private static bool SameDialogue(string value, string language, string text)
    {
        var end = value.IndexOf(']');
        if (!value.StartsWith('[') || end < 1 || !EquivalentDialogueText(value[(end + 1)..]).Equals(" " + EquivalentDialogueText(text), StringComparison.Ordinal)) return false;
        var actual = value[1..end];
        if (actual.Equals(language, StringComparison.OrdinalIgnoreCase)) return true;
        // ISO codes and their language names are equivalent; never rewrite the authored prompt.
        return CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0)
            .Any(c => Matches(c, actual) && Matches(c, language));
        static bool Matches(CultureInfo culture, string label) =>
            new[] { culture.Name, culture.EnglishName, culture.NativeName, culture.ThreeLetterISOLanguageName }
                .Contains(label, StringComparer.OrdinalIgnoreCase);
    }

    // Printer's punctuation is an encoding choice, not a wording change. Models routinely return
    // typographic apostrophes, quotes, dashes and ellipses for the same authored line, so compare an
    // equivalent form while keeping the words, their order and the language strict. The prompt is
    // never rewritten; only the comparison is normalized.
    private static string EquivalentDialogueText(string value) => value
        .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201A', '\'').Replace('\u201B', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u201E', '"').Replace('\u201F', '"')
        .Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2015', '-')
        .Replace("\u2026", "...").Replace('\u00A0', ' ');

    // Rounding a frame-derived duration to fewer decimals is an editorial slip, not a contract break.
    internal const double DurationRoundingSeconds = .005;
    // Returns true when the prompt states the duration. A near-miss is reported through the out note
    // so the author can correct the wording without losing an otherwise usable response.
    // H3 takes its output length from the frame count, not from this wording, so an ordinary shot may
    // state the authored Requested seconds as truthfully as the frame-derived value. The frame grid
    // rounds a request up to the next 17-frame step, so accepting the span between the two figures
    // admits either honest wording without admitting an unrelated number. Reels stay strict because
    // their cut timestamps are derived from the generated grid.
    private static bool HasDuration(string prompt, double requestedSeconds, double generatedSeconds, bool allowRequested, out string? note)
    {
        note = null;
        string[] integers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        // Consume whole word-number phrases so "twenty eight" cannot match just "eight".
        var word = "(?:" + string.Join("|", integers) + "|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|billion|point|minus|negative|plus)";
        var pattern = @"(?<![\w.+-])(\d+(?:\.\d+)?|" + word + @"(?:(?:\s+|-)(?:" + word + @"|and))*)(?:\s+|-)seconds?\b";
        foreach (Match match in Regex.Matches(prompt, pattern, RegexOptions.IgnoreCase))
        {
            var text = match.Groups[1].Value;
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var stated))
                stated = Array.FindIndex(integers, number => number.Equals(text, StringComparison.OrdinalIgnoreCase));
            // The model receives full frame-derived precision; millisecond rounding is equivalent.
            if (Math.Abs(stated - generatedSeconds) <= .0005) return true;
            if (note is null && Math.Abs(stated - generatedSeconds) <= DurationRoundingSeconds)
                note = $"The prompt states {text} seconds; the generated duration is {generatedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. The frame count is authoritative. Review the wording before generating; the measured duration may look slightly different.";
            // A shot may also state the Requested seconds it was given, or any value between that and the
            // generated figure. The note keeps the exact frame-derived duration visible for review.
            if (note is null && allowRequested && stated >= requestedSeconds - .0005 && stated <= generatedSeconds + DurationRoundingSeconds)
                note = $"The prompt states {text} seconds; the generated duration is {generatedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. The frame count is authoritative, so the output length stays as requested. Review the wording before generating if you want the text to match the measured duration exactly.";
        }
        return note is not null;
    }

    // H3 consumes natural-language text. Its recommended template is advisory, not an input schema.
    public static void ValidateGenerationPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 100000)
            throw new WorkspaceStoreException("Write a prompt under 100000 characters before generating.");
    }

    public static string? PromptWarning(string prompt, Shot shot, bool allowCuts = false)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        try { return ValidatePrompt(prompt, shot, allowCuts); }
        catch (WorkspaceStoreException e) { return e.Message; }
    }

    // Strict checks remain available to operations that actually parse the template, such as dubbing.
    // Saving, accepting, composing and generating use these checks only through PromptWarning.
    public static string? ValidatePrompt(string prompt, Shot shot, bool allowCuts = false)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 100000) throw new WorkspaceStoreException("Provide a complete H3 prompt under 100000 characters.");
        var matches = Regex.Matches(prompt, SectionPattern);
        var names = matches.Select(m => m.Groups[1].Value).ToArray();
        if (!names.SequenceEqual(Sections))
        {
            var missing = Sections.Except(names).ToArray();
            var repeated = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            var issues = new List<string>();
            if (missing.Length > 0) issues.Add("Missing H3 sections: " + string.Join(", ", missing) + ".");
            if (repeated.Length > 0) issues.Add("Repeated H3 sections: " + string.Join(", ", repeated) + ". Keep each heading once.");
            if (issues.Count == 0) issues.Add("Put the H3 sections in this order: " + string.Join(", ", Sections) + ".");
            throw new WorkspaceStoreException(string.Join(" ", issues));
        }
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length; var end = i + 1 < matches.Count ? matches[i + 1].Index : prompt.Length;
            if (string.IsNullOrWhiteSpace(prompt[start..end]) && !(i == 0 && ResolvedReferences.For(shot).InputOrder().Count == 0) && i is not (2 or 4))
                throw new WorkspaceStoreException($"Complete the {Sections[i]} section.");
        }
        foreach (Match m in Regex.Matches(prompt, @"<(Picture|Audio|Video)\s+(\d+)>", RegexOptions.IgnoreCase))
        {
            if (!int.TryParse(m.Groups[2].Value, out var n)) throw new WorkspaceStoreException("Invalid reference number."); var max = m.Groups[1].Value.ToLowerInvariant() switch { "picture" => ResolvedReferences.For(shot).Pictures.Count, "audio" => ReferenceVideos.AudioMappings(shot).Count(), _ => ResolvedReferences.For(shot).Videos.Count };
            if (n < 1 || n > max) throw new WorkspaceStoreException("The prompt names a reference that is not selected.");
        }
        for (var n = 1; n <= ResolvedReferences.For(shot).Pictures.Count; n++) if (!prompt.Contains($"<Picture {n}>")) throw new WorkspaceStoreException($"The prompt omits Picture {n}.");
        for (var n = 1; n <= ReferenceVideos.AudioMappings(shot).Count(); n++) if (!prompt.Contains($"<Audio {n}>")) throw new WorkspaceStoreException($"The prompt omits Audio {n}.");
        for (var n = 1; n <= ResolvedReferences.For(shot).Videos.Count; n++) if (!prompt.Contains($"<Video {n}>")) throw new WorkspaceStoreException($"The prompt omits Video {n}.");
        var dialogue = Regex.Matches(prompt, @"<d>(.*?)</d>", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToArray();
        if (dialogue.Length != shot.Dialogue.Count)
            throw new WorkspaceStoreException("Keep every dialogue line exactly, in order, with its original language. Do not add dialogue.");
        for (var i = 0; i < dialogue.Length; i++)
        {
            var expected = shot.Dialogue[i];
            if (SameDialogue(dialogue[i], expected.Language, expected.Text)) continue;
            if (!dialogue[i].StartsWith('[') || dialogue[i].IndexOf(']') <= 1)
            {
                var format = expected.Text.Length <= 160
                    ? $"Use: <d>[{expected.Language}] {expected.Text}</d>"
                    : $"Start the dialogue with <d>[{expected.Language}] followed by one space and the unchanged line, then </d>.";
                throw new WorkspaceStoreException($"Dialogue line {i + 1} is missing its language in square brackets. {format}");
            }
            throw new WorkspaceStoreException("Keep every dialogue line exactly, in order, with its original language. Do not add dialogue.");
        }
        if (!prompt.Contains("[Shot 1]") || !allowCuts && Regex.IsMatch(prompt, @"\[Shot\s+(?!1\])\d+\]", RegexOptions.IgnoreCase)) throw new WorkspaceStoreException("Compose one continuous shot, labelled [Shot 1].");
        var defs = prompt[(matches[0].Index + matches[0].Length)..matches[1].Index];
        var defined = Regex.Matches(defs, @"<Subject (\d+)>").Select(m => m.Value).ToHashSet();
        if (Regex.Matches(prompt, @"<Subject (\d+)>").Any(m => !defined.Contains(m.Value))) throw new WorkspaceStoreException("Define every subject before using its label.");
        var speakers = shot.Dialogue.Select(d => d.Speaker.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool MappedSpeaker(string text, string name, bool immediatelyBeforeDialogue = true)
        {
            var index = speakers.FindIndex(s => string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase)) + 1;
            var labels = new List<string> { Regex.Escape(name.Trim()) };
            foreach (var subject in defined)
                if (Regex.IsMatch(defs, Regex.Escape(subject) + @"[^\r\n]*\b" + Regex.Escape(name.Trim()) + @"\b", RegexOptions.IgnoreCase)) labels.Add(Regex.Escape(subject));
            return Regex.IsMatch(text, @"(?:" + string.Join("|", labels) + @")\s*\(S" + index + @"\)" + (immediatelyBeforeDialogue ? @"[^\r\n<>]*$" : ""), RegexOptions.IgnoreCase);
        }
        var lines = Regex.Matches(prompt, @"<d>(.*?)</d>", RegexOptions.Singleline);
        for (var i = 0; i < lines.Count; i++)
        {
            var before = prompt[..lines[i].Index];
            if (!MappedSpeaker(before[(before.LastIndexOf('\n') + 1)..], shot.Dialogue[i].Speaker))
                throw new WorkspaceStoreException("Place the correct character name or defined subject and its stable (S1), (S2) speaker label immediately before each dialogue line.");
        }
        foreach (var (number, speaker) in ReferenceVideos.AudioMappings(shot))
        {
            if (string.IsNullOrWhiteSpace(speaker)) continue;
            var tag = $"<Audio {number}>";
            var audioLine = defs.Split('\n').FirstOrDefault(l => l.Contains(tag));
            // Both "<Audio 1> is Riley (S1)'s voice" and "Riley (S1) uses <Audio 1>"
            // explicitly map the same input. The speaker must still be on its definition line.
            if (audioLine is null || !MappedSpeaker(audioLine, speaker, false))
                throw new WorkspaceStoreException($"Map Audio {number} to its assigned speaker in subject_definitions.");
        }
        var generatedSeconds = H3Policy.Seconds(shot.Duration!.Value);
        var seconds = generatedSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        // Reels derive their cut timestamps from the generated grid, so they must state it exactly.
        if (!HasDuration(prompt, shot.Duration!.Value, generatedSeconds, !allowCuts, out var durationNote))
            throw new WorkspaceStoreException(allowCuts ? $"In the generation prompt text, state the total reel duration: {seconds} seconds. Use the generated duration shown below Requested seconds; changing Requested seconds recalculates it." : $"State the shot's generated duration: {seconds} seconds.");
        if (!allowCuts && !Regex.IsMatch(prompt, @"\b(?:one|single) continuous (?:camera )?(?:take|shot)\b", RegexOptions.IgnoreCase))
            throw new WorkspaceStoreException("State that this is one continuous take.");
        return durationNote;
    }
}
