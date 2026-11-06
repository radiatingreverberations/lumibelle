using System.Globalization;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>
/// Continuity checks that read stored project data only: no model, no media decoding, the same answer every time.
/// Each finding names its check so a page or an agent can group and filter them.
/// </summary>
public static partial class ReviewChecks
{
    public const string References = "references", Voices = "voices", VoiceConsistency = "voice-consistency", Duration = "duration",
        Dialogue = "dialogue", PromptText = "prompt-text", PromptReview = "prompt-review", TakeInputs = "take-inputs",
        ShotFrames = "shot-frames", Cut = "cut", Takes = "takes", Order = "order";

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [References] = "Missing references", [Voices] = "Voices not chosen", [VoiceConsistency] = "Voices that differ between takes",
        [Duration] = "Durations outside the refine range", [Dialogue] = "Stage directions stored as dialogue", [PromptText] = "Prompt text",
        [PromptReview] = "Prompts to review", [TakeInputs] = "Selected takes older than their inputs", [ShotFrames] = "Shot frames from replaced takes",
        [Cut] = "Cut and selected takes", [Takes] = "Shots without a selected take", [Order] = "Render order"
    };

    public static IReadOnlyList<ReviewFinding> Run(ProjectReviewSnapshot snapshot)
    {
        var findings = new List<ReviewFinding>();
        foreach (var row in snapshot.Rows)
        {
            ReviewFinding At(string check, ReviewSeverity severity, string message, Guid? take = null) =>
                new(check, severity, message) { ShotId = row.Id, ShotNumber = row.Number, TakeId = take };
            findings.AddRange(ReferenceFindings(snapshot, row).Select(m => At(References, ReviewSeverity.Problem, m)));
            findings.AddRange(VoiceFindings(snapshot, row).Select(m => At(Voices, ReviewSeverity.Problem, m)));
            if (DurationFinding(row.Coverage) is { } duration) findings.Add(At(Duration, ReviewSeverity.Look, duration));
            findings.AddRange(row.Coverage.Dialogue.Where(d => IsStageDirection(d.Text)).Select(d =>
                At(Dialogue, ReviewSeverity.Look, $"The line “{Short(d.Text)}” for {Speaker(d)} reads as a stage direction. Composition puts every dialogue line in the prompt as speech.")));
            findings.AddRange(PromptTextFindings(row).Select(m => At(PromptText, ReviewSeverity.Look, m)));
            if (row.Setup is not null && ProjectReview.PromptCheck(snapshot, row) is { NeedsAttention: true } check)
                findings.Add(At(PromptReview, check.State == PromptReferenceState.Missing ? ReviewSeverity.Problem : ReviewSeverity.Look, check.Detail));
            if (row.SelectedTake is { } selected)
            {
                var changes = ProjectReview.TakeChanges(snapshot, selected);
                var changed = new[] { (TakeInputChange.Script, "script"), (TakeInputChange.Prompt, "prompt"), (TakeInputChange.References, "references") }
                    .Where(c => changes.HasFlag(c.Item1)).Select(c => c.Item2).ToArray();
                if (changed.Length > 0) findings.Add(At(TakeInputs, ReviewSeverity.Look,
                    $"The selected take was rendered before the {List(changed)} changed.", selected.Id));
            }
            else findings.Add(At(Takes, ReviewSeverity.Look, row.Takes.Count == 0 ? "No takes yet." : $"None of this shot's {Count(row.Takes.Count, "take")} is selected."));
            findings.AddRange(ShotFrameFindings(snapshot, row).Select(f => At(ShotFrames, f.Severity, f.Message, f.Take)));
        }
        findings.AddRange(VoiceConsistencyFindings(snapshot));
        findings.AddRange(CutFindings(snapshot));
        foreach (var cycle in ShotFrameOrder.Build(snapshot).Cycles)
            findings.Add(new(Order, ReviewSeverity.Problem, $"Shots {List(cycle.Select(id => "#" + snapshot.Row(id)?.Number).ToArray())} take frames from one another, so none can be rendered first. Remove one of the shot frames."));
        return findings;
    }

    private static IEnumerable<string> ReferenceFindings(ProjectReviewSnapshot snapshot, ReviewShot row)
    {
        var shot = row.Shot; var resolved = ResolvedReferences.For(shot);
        if (resolved.Pictures.Count == 0 && resolved.Videos.Count == 0) yield return "No pictures or reels are selected as references.";
        foreach (var image in shot.Images.Where(i => ProductionPolicy.MediaIssue(i, snapshot.Assets) is not null))
            yield return $"The reference “{image.Name}” is no longer in Assets. Restore or replace it.";
        foreach (var character in ShotReferences.Characters(shot))
        {
            // Only a name that points at exactly one library character says who should be pictured.
            if (snapshot.Assets.Assets.Where(a => a.Category == AssetCategory.Character && DialogueSpeakers.Names(character.Name, a.Name)).ToArray() is not [var asset]) continue;
            if (resolved.Pictures.Count + resolved.Videos.Count > 0 && !shot.Images.Any(i => i.AssetId == asset.Id || i.RepresentsId == character.Id) &&
                !shot.Videos.Any(v => CharacterVoices.Owner(v, snapshot.Assets) == asset.Id))
                yield return $"{asset.Name} is in this shot, but no picture or reel of {asset.Name} is selected.";
        }
    }

    private static IEnumerable<string> VoiceFindings(ProjectReviewSnapshot snapshot, ReviewShot row)
    {
        foreach (var (speaker, character) in CharacterVoices.Unvoiced(row.Shot, snapshot.Assets)) yield return CharacterVoices.UnvoicedWarning(speaker, character);
        foreach (var choice in (row.Shot.CharacterVoices ?? []).Where(c => c.Source == CharacterVoiceSource.Unselected))
            yield return $"Choose a voice or None for {choice.CharacterName} in Manage references.";
    }

    // Planned length on the frame grid. Takes outside 2 to 15 seconds cannot be used as a refine source.
    public static string? DurationFinding(Shot shot)
    {
        if (shot.Duration is not { } seconds || !double.IsFinite(seconds) || seconds is < 1 or > 15) return null;
        var frames = H3Policy.Frames(seconds); var rendered = frames / 24d;
        return rendered is >= 2 and <= 15 ? null
            : $"At {Number(seconds)} s this shot renders {frames} frames, {Number(rendered)} s. Refining works on takes of 2 to 15 seconds; check the take can still be refined, or change the duration.";
    }

    [GeneratedRegex(@"^\s*[*_]*\s*(\(.*\)|\[.*\])\s*[*_]*\s*$", RegexOptions.Singleline)] private static partial Regex Parenthetical();
    [GeneratedRegex(@"^\s*\*[^*]+\*\s*$", RegexOptions.Singleline)] private static partial Regex Emphasised();
    /// <summary>A line that is only a parenthetical or only emphasis, such as <c>*(quietly, to himself)*</c>.</summary>
    public static bool IsStageDirection(string? text) => !string.IsNullOrWhiteSpace(text) && (Parenthetical().IsMatch(text) || Emphasised().IsMatch(text));

    [GeneratedRegex("\"[A-Za-z_][A-Za-z0-9_]*\"\\s*:\\s*(\"|\\{|\\[|-?\\d|true\\b|false\\b|null\\b)")] private static partial Regex JsonMember();
    [GeneratedRegex(@"\bplaceholder\b", RegexOptions.IgnoreCase)] private static partial Regex Placeholder();
    private static IEnumerable<string> PromptTextFindings(ReviewShot row)
    {
        var prompt = row.Prompt;
        if (string.IsNullOrWhiteSpace(prompt)) yield break;
        if (JsonMember().IsMatch(prompt) || prompt.Contains("```", StringComparison.Ordinal)) yield return "The prompt contains what looks like leftover JSON or a code block.";
        if (Placeholder().IsMatch(prompt)) yield return "The prompt contains the word “placeholder”.";
        if (ProductionPolicy.PromptWarning(prompt, row.Shot) is { } warning) yield return warning;
    }

    private static IEnumerable<(ReviewSeverity Severity, string Message, Guid? Take)> ShotFrameFindings(ProjectReviewSnapshot snapshot, ReviewShot row)
    {
        var frames = row.Shot.ShotFrames.Select(f => (f.TakeId, f.Frame, Name: $"Shot frame “{f.Name}”"));
        if (row.Shot.StartFrame is { } start) frames = frames.Append((start.TakeId, start.Frame, "The starting frame"));
        foreach (var (takeId, frame, name) in frames)
        {
            if (snapshot.Shots.Takes.FirstOrDefault(t => t.Id == takeId) is not { } take)
            {
                yield return (ReviewSeverity.Problem, snapshot.Shots.Trash.Any(e => e.Take?.Id == takeId)
                    ? $"{name} comes from a take that is in Trash." : $"{name} comes from a take that no longer exists.", takeId);
                continue;
            }
            if (frame >= take.FrameCount) yield return (ReviewSeverity.Problem, $"{name} is frame {frame + 1} of a take with {take.FrameCount} frames.", takeId);
            var owner = snapshot.Row(take.ShotId);
            if (owner is not null && owner.Coverage.SelectedTakeId != take.Id)
                yield return (ReviewSeverity.Look, $"{name} comes from {TakeDisplay.Label(take)} of shot #{owner.Number}, which is not that shot's selected take.", takeId);
        }
    }

    // One recording per character across the film: a different one in some takes is heard as a different voice.
    private static IEnumerable<ReviewFinding> VoiceConsistencyFindings(ProjectReviewSnapshot snapshot)
    {
        var uses = new List<(Guid Character, string Voice, string Name, int Shot)>();
        foreach (var row in snapshot.Rows)
        {
            if (row.SelectedTake is not { } take) continue;
            var rendered = take.Snapshot.Shot;
            foreach (var voice in rendered.Voices)
                uses.Add((voice.CharacterAssetId ?? voice.AssetId, $"{voice.VoiceId:N}:{Number(voice.Start)}:{Number(voice.Duration)}",
                    snapshot.Assets.Voices.FirstOrDefault(v => v.Id == voice.VoiceId)?.Name ?? "a recording no longer in Assets", row.Number));
            foreach (var reel in rendered.Videos.Where(v => v.UseSoundtrack))
                if (CharacterVoices.Owner(reel, snapshot.Assets) is { } owner) uses.Add((owner, "reel:" + reel.Media.Id.ToString("N"), $"the reel “{reel.Name}”", row.Number));
        }
        foreach (var character in uses.GroupBy(u => u.Character))
        {
            var voices = character.GroupBy(u => u.Voice).OrderByDescending(g => g.Count()).ToArray();
            if (voices.Length < 2) continue;
            var name = snapshot.Assets.Assets.FirstOrDefault(a => a.Id == character.Key)?.Name ?? "A character";
            yield return new(VoiceConsistency, ReviewSeverity.Look, $"{name} has {voices.Length} different voices in the selected takes: " +
                string.Join("; ", voices.Select(v => $"{v.First().Name} in {Shots(v.Select(u => u.Shot))}")) + ".");
        }
    }

    private static IEnumerable<ReviewFinding> CutFindings(ProjectReviewSnapshot snapshot)
    {
        var selected = snapshot.Rows.Where(r => r.SelectedTake is not null).ToArray();
        if (snapshot.Cut.Clips.Count == 0)
        {
            if (selected.Length > 0) yield return new(Cut, ReviewSeverity.Look, $"The cut is empty, and {Count(selected.Length, "shot")} have a selected take.");
            yield break;
        }
        foreach (var clips in snapshot.Cut.Clips.GroupBy(c => c.ShotId))
        {
            if (snapshot.Row(clips.Key) is not { } row) continue;
            if (row.Coverage.SelectedTakeId is { } take && clips.All(c => c.TakeId != take))
                yield return new(Cut, ReviewSeverity.Problem, "The cut uses another take than the one selected for this shot.") { ShotId = row.Id, ShotNumber = row.Number, TakeId = take };
        }
        var missing = selected.Where(r => snapshot.Cut.Clips.All(c => c.ShotId != r.Id)).ToArray();
        if (missing.Length > 0) yield return new(Cut, ReviewSeverity.Look, $"{Shots(missing.Select(r => r.Number))} {(missing.Length == 1 ? "has" : "have")} a selected take and {(missing.Length == 1 ? "is" : "are")} not in the cut.");
    }

    private static string Speaker(ShotDialogue line) => string.IsNullOrWhiteSpace(line.Speaker) ? "an unnamed speaker" : line.Speaker.Trim();
    private static string Short(string text) => (text = text.Trim().ReplaceLineEndings(" ")).Length <= 80 ? text : text[..77] + "…";
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Count(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
    private static string List(IReadOnlyList<string> items) => items.Count switch { 0 => "", 1 => items[0], 2 => $"{items[0]} and {items[1]}", _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1] };
    internal static string Shots(IEnumerable<int> numbers)
    {
        var ordered = numbers.Distinct().Order().ToArray();
        return (ordered.Length == 1 ? "shot " : "shots ") + List(ordered.Select(n => "#" + n).ToArray());
    }
}
