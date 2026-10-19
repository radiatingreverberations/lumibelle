using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

/// <summary>Named, self-contained request configurations. Provider identity remains TextModelPolicy.Key.</summary>
public static class TextModelProfiles
{
    public static IReadOnlyList<string> OpenRouterEfforts { get; } =
        Array.AsReadOnly(new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" });

    // The catalog lists effort levels while reasoning is ON. Disabling reasoning is
    // separate: optional-thinking models can omit "none" from supported_efforts.
    // Keep the persisted "none" value for compatibility with existing profile snapshots.
    public static IReadOnlyList<string> OpenRouterEffortChoices(AiModel? catalog)
    {
        var info = catalog?.Catalog;
        if (info?.SupportedParameters is not null && !info.SupportsReasoning) return Array.Empty<string>();
        var efforts = info?.SupportedReasoningEfforts ?? OpenRouterEfforts;
        return (info?.ReasoningMandatory == true
            ? efforts.Where(effort => effort != "none")
            : new[] { "none" }.Concat(efforts)).Distinct(StringComparer.Ordinal).ToArray();
    }

    // Include configuration for saved snapshots: an edited profile and an older project
    // default may share an ID, but must still be independently selectable.
    public static string ChoiceKey(TextModelReference model) => model.ProfileId is { } id
        ? TextModelPolicy.Key(model) + "\nprofile:" + id.ToString("N") + "\n" + ConfigurationKey(model) + "\n" + model.Name
        : TextModelPolicy.Key(model);

    private static string ConfigurationKey(TextModelReference model) => string.Join("|",
        model.ReasoningEffort ?? "", model.ReasoningMaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
        model.Temperature?.ToString("R", CultureInfo.InvariantCulture) ?? "",
        model.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "");

    public static bool SameConfiguration(TextModelReference? left, TextModelReference? right) =>
        TextModelPolicy.Same(left, right) && left!.ProfileId == right!.ProfileId &&
        left.ReasoningEffort == right.ReasoningEffort && left.ReasoningMaxTokens == right.ReasoningMaxTokens &&
        left.Temperature == right.Temperature && left.MaxOutputTokens == right.MaxOutputTokens &&
        (left.ProfileId is null || left.Name == right.Name);

    public static bool RequiresSnapshotVersion3(TextModelReference model) => model.ProfileId is not null ||
        model.Temperature is not null || model.MaxOutputTokens is not null || model.ReasoningMaxTokens is not null ||
        model.Backend == AiBackend.OpenRouter && model.ReasoningEffort is not null;

    public static TextModelReference ModelOnly(TextModelReference model) =>
        new(model.Backend, model.Model, model.ProfileId is null ? model.Name : model.Model, model.ComfyUrl) { VllmUrl = model.VllmUrl };

    public static string Describe(TextModelReference model)
    {
        var values = new List<string>();
        if (model is { ProfileId: not null, ReasoningEffort: null } && TextModelPolicy.UsesNamedEffort(model.Backend))
            values.Add("model-default reasoning");
        if (model.ReasoningEffort is { Length: > 0 } effort)
            values.Add(effort == "none" ? "reasoning off" : "reasoning " + effort);
        if (model.ReasoningMaxTokens is { } budget)
            values.Add("thinking budget " + budget.ToString("N0", CultureInfo.InvariantCulture));
        if (model.Temperature is { } temperature)
            values.Add("temperature " + temperature.ToString("0.###", CultureInfo.InvariantCulture));
        if (model.MaxOutputTokens is { } output)
            values.Add("output limit " + output.ToString("N0", CultureInfo.InvariantCulture));
        return values.Count == 0 ? "No parameter overrides" : string.Join(" · ", values);
    }

    public static void ValidateReference(TextModelReference model)
    {
        if (model.ProfileId == Guid.Empty || model.ProfileId is not null &&
            (string.IsNullOrWhiteSpace(model.Name) || model.Name.Trim().Length > 120 || model.Name.Any(char.IsControl)))
            throw new WorkspaceStoreException("A text profile needs a valid ID and a name of at most 120 characters.");
        if (model.Temperature is { } temperature && (!float.IsFinite(temperature) || temperature > 2 ||
            temperature < (model.Backend == AiBackend.ComfyUI ? .01f : 0f)))
            throw new WorkspaceStoreException("Profile temperature must be 0–2 (0.01–2 for ComfyUI), or blank for defaults.");
        if (model.MaxOutputTokens is { } output && (output < 1 ||
            output > (model.Backend == AiBackend.ComfyUI ? 32768 : 1_000_000)))
            throw new WorkspaceStoreException("Profile output limit must be positive and at most 1,000,000 tokens (32,768 for ComfyUI).");
        if (model.ReasoningMaxTokens is < 1 or > 1_000_000)
            throw new WorkspaceStoreException("A thinking budget must be between 1 and 1,000,000 tokens.");
        if (model.ReasoningMaxTokens is not null && !string.IsNullOrEmpty(model.ReasoningEffort))
            throw new WorkspaceStoreException("Choose reasoning effort or a thinking token budget, not both.");
        if (model.ReasoningMaxTokens is { } budget && model.MaxOutputTokens is { } limit && budget >= limit)
            throw new WorkspaceStoreException("The output limit must exceed the thinking budget to leave room for the answer.");
        if (TextModelPolicy.UsesNamedEffort(model.Backend) && (model.Temperature is not null || model.MaxOutputTokens is not null))
            throw new WorkspaceStoreException($"{TextModelPolicy.ProviderName(model.Backend)} text profiles support reasoning effort, not temperature or output-limit overrides.");
        if (model.Backend == AiBackend.ClaudeCode && model.ReasoningEffort is { } claudeEffort && !ClaudeCodeClient.Efforts.Contains(claudeEffort))
            throw new WorkspaceStoreException("Choose a supported Claude Code reasoning effort or leave it at the model default.");
        if (model.Backend != AiBackend.OpenRouter && model.ReasoningMaxTokens is not null)
            throw new WorkspaceStoreException("Thinking token budgets are available only for OpenRouter text profiles.");
        if (model.Backend == AiBackend.ComfyUI && !string.IsNullOrEmpty(model.ReasoningEffort))
            throw new WorkspaceStoreException("The ComfyUI text workflow does not support reasoning effort.");
        if (model.Backend == AiBackend.Vllm && model.ReasoningEffort is not null)
            throw new WorkspaceStoreException("vLLM profiles currently support temperature and output limits. Configure reasoning on the server.");
        if (model.Backend == AiBackend.OpenRouter && model.ReasoningEffort is { } effort && !OpenRouterEfforts.Contains(effort))
            throw new WorkspaceStoreException("Choose a supported OpenRouter reasoning effort or leave it at the provider default.");
    }

    public static void ValidateSettings(AiSettings settings)
    {
        if (settings.TextModelProfiles is null)
            throw new WorkspaceStoreException("The text profile library is invalid.");
        var ids = new HashSet<Guid>();
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var profile in settings.TextModelProfiles)
        {
            if (profile is null) throw new WorkspaceStoreException("The text profile library contains a missing entry.");
            TextModelPolicy.Validate(profile);
            if (profile.ProfileId is not { } id || !ids.Add(id))
                throw new WorkspaceStoreException("Each saved text profile needs its own unique ID.");
            var key = TextModelPolicy.Key(TextModelPolicy.Normalize(profile));
            if (!names.TryGetValue(key, out var modelNames)) names[key] = modelNames = new(StringComparer.OrdinalIgnoreCase);
            if (!modelNames.Add(profile.Name.Trim()))
                throw new WorkspaceStoreException("Use different profile names for configurations of the same model.");
        }
        // Defaults and queued requests retain their snapshots after a profile is edited or deleted.
        if (settings.TextDefault is { } selected) TextModelPolicy.Validate(selected);
    }

    public static string? CatalogIssue(TextModelReference model, AiModel? catalog)
    {
        if (model.Backend != AiBackend.OpenRouter || catalog?.Catalog is not { } info) return null;
        if (model.Temperature is not null && info.SupportedParameters is { } parameters && !parameters.Contains("temperature"))
            return "This model does not advertise temperature control. Clear the profile temperature override.";
        if (model.MaxOutputTokens is { } output && info.MaxOutputTokens is { } maximum && output > maximum)
            return $"The profile output limit exceeds this model's reported maximum of {maximum:N0} tokens.";
        if ((model.ReasoningEffort is not null || model.ReasoningMaxTokens is not null) &&
            info.SupportedParameters is not null && !info.SupportsReasoning)
            return "This model does not advertise reasoning controls. Clear the profile reasoning override.";
        if (model.ReasoningEffort == "none" && info.ReasoningMandatory == true)
            return "This model requires reasoning and cannot use a reasoning-off profile.";
        // Off is validated by ReasoningMandatory above, not by the list of ON levels.
        if (model.ReasoningEffort is { } effort && effort != "none" &&
            info.SupportedReasoningEfforts is { } efforts && !efforts.Contains(effort))
            return "Choose a reasoning effort advertised by this model.";
        if (model.ReasoningMaxTokens is { } budget && info.MaxOutputTokens is { } total && budget >= total)
            return "The thinking budget must be below the model's reported output limit to leave room for an answer.";
        if (model.ReasoningMaxTokens is not null && info.SupportsReasoningBudget == false)
            return "This model does not advertise a thinking token budget. Use an available effort level instead.";
        return null;
    }
}
