using lumibelle.Models;
using System.Text.Json;

namespace lumibelle.Services.AI;

public sealed record ScriptBriefInspection(ScriptScenePlan? Plan, string[] Errors, string[] Warnings)
{
    public bool CanUse => Plan is not null && Errors.Length == 0;
}

public static partial class ScriptScenePlanning
{
    public const int MaximumItemCharacters = 1200;

    public static ScriptBriefInspection Inspect(string raw, string? finishReason = "stop")
    {
        var plan = Decode(raw) ?? AiJsonReply.Latest(raw, text => Decode(text) is { Beats: not null } candidate ? candidate : null);
        var inspected = InspectPlan(plan);
        if (plan is null && !string.IsNullOrWhiteSpace(raw)) {
            try { using var document = JsonDocument.Parse(raw); }
            catch (JsonException e) { inspected = inspected with { Errors = [$"Invalid JSON at line {(e.LineNumber ?? 0) + 1}, column {(e.BytePositionInLine ?? 0) + 1}. Complete or correct the brief object, then review the JSON again."] }; }
        }
        return finishReason == "stop" ? inspected : inspected with {
            Errors = [finishReason == "length" ? "The model reached its reply limit before finishing the brief. Retry planning with a larger reply limit or a shorter brief."
                : "The model did not finish this response normally. Retry planning before using it.", .. inspected.Errors]
        };
    }

    private static ScriptBriefInspection InspectPlan(ScriptScenePlan? plan)
    {
        if (plan is null) return new(null, ["The response is not a complete brief object. It needs beats, continuity, withhold, ending and timeCue fields."], []);
        List<string> errors = []; List<string> warnings = [];
        void Entries(string label, string[]? entries, int minimum, int? maximum = null, bool required = true)
        {
            if (entries is null) { if (required) errors.Add($"{label} is missing; it must be a list of text entries."); return; }
            if (entries.Length < minimum) errors.Add($"{label} needs at least {minimum} entry.");
            if (maximum is { } limit && entries.Length > limit)
                errors.Add($"The brief has {entries.Length} beats; the screenplay checklist supports up to {limit}. Split or combine the planning events before retrying.");
            for (var i = 0; i < entries.Length; i++) {
                var text = entries[i];
                if (string.IsNullOrWhiteSpace(text) || !text.EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit)) errors.Add($"{label} entry {i + 1} needs meaningful text.");
                else if (text.Length > MaximumItemCharacters) warnings.Add($"{label} entry {i + 1} has {text.Length:N0} characters; the recommended maximum is {MaximumItemCharacters:N0}. Several concise events are easier to check than one long retelling.");
            }
        }
        Entries("Beats", plan.Beats, 1, MaximumBeats);
        // These lists follow the sequence's facts and reveal boundaries. Their
        // combined text size, rather than an arbitrary entry count, uses the budget.
        Entries("Continuity", plan.Continuity, 0);
        Entries("Withhold", plan.Withhold, 0);
        Entries("Details", plan.Details, 0, required: false);
        Entries("Ending", [plan.Ending], 1);
        if (plan.TimeCue is null) errors.Add("timeCue is missing; use an empty string when no caption is needed.");
        else if (plan.TimeCue.Length > 200) warnings.Add($"The time cue has {plan.TimeCue.Length:N0} characters; a short on-screen caption is recommended.");
        if (plan.Evidence is { } evidence && (evidence.Length > 12 || evidence.Any(e => e is null || e.Section <= 0 ||
            string.IsNullOrWhiteSpace(e.Quote) || e.Quote.Length > MaximumEvidenceCharacters))) errors.Add("Source highlights need a positive section number and a short exact quotation, with at most 12 highlights.");
        var size = Size(plan);
        if (size.Characters > MaximumCharacters) warnings.Add($"The brief has {size.Characters:N0} characters; the recommended budget is {MaximumCharacters:N0}.");
        if (size.Tokens > MaximumTokens) warnings.Add($"The brief uses about {size.Tokens:N0} text tokens; the recommended budget is {MaximumTokens:N0}.");
        return new(plan, errors.ToArray(), warnings.ToArray());
    }
}
