using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public static class TextModelPolicy
{
    public static string DisplayName(TextModelReference model, AiSettings settings, string? catalogName = null) =>
        model.ProfileId is not null ? model.Name :
        settings.TextModelAliases.TryGetValue(Key(model), out var alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias : catalogName ?? model.Name;

    public static string PickerLabel(TextModelReference model, AiSettings settings, IEnumerable<TextModelReference> choices,
        Func<TextModelReference, string?> catalogName)
    {
        var name = DisplayName(model, settings, catalogName(model));
        var provider = model.Backend == AiBackend.Vllm ? OpenAiConnections.Name(settings, model.VllmUrl) : ProviderName(model.Backend);
        if (model.ProfileId is not null)
            return $"{provider} · {name} · {model.Model}{(Server(model) is { } server ? $" · {server}" : "")} · {TextModelProfiles.Describe(model)}";
        var duplicates = choices.Where(other => other.Backend == model.Backend &&
            (model.Backend != AiBackend.Vllm || OpenAiConnections.Name(settings, other.VllmUrl).Equals(provider, StringComparison.OrdinalIgnoreCase)) &&
            DisplayName(other, settings, catalogName(other)).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var suffix = duplicates.Any(other => !Same(other, model)) ? $" · {model.Model}" : "";
        if (duplicates.Any(other => !Same(other, model) && other.Model == model.Model)) suffix += $" · {Server(model)}";
        return $"{provider} · {name}{suffix}";
    }
    public static string ProviderName(AiBackend backend) => backend switch { AiBackend.ClaudeCode => "Claude Code", AiBackend.Vllm => "OpenAI-compatible", _ => backend.ToString() };
    public static string? Server(TextModelReference model) => model.Backend switch { AiBackend.ComfyUI => model.ComfyUrl, AiBackend.Vllm => model.VllmUrl, _ => null };
    public static TextModelReference Reference(AiBackend backend, string id, string name, AiSettings settings) =>
        new(backend, id, name, backend == AiBackend.ComfyUI ? settings.ComfyUrl : null)
        { VllmUrl = backend == AiBackend.Vllm ? settings.VllmUrl : null };
    public static TextModelReference Reference(AiBackend backend, AiModel model, AiSettings settings) =>
        Reference(backend, model.Id, model.Name, settings) with { VllmUrl = backend == AiBackend.Vllm ? model.ServerUrl ?? settings.VllmUrl : null };
    public static AiModel? CatalogModel(TextModelReference model, AiConnectionCheck? check) => check?.Models.FirstOrDefault(m =>
        m.Id == model.Model && (model.Backend != AiBackend.Vllm || m.ServerUrl is null || SameVllmServer(m.ServerUrl, model.VllmUrl)));
    public static void CheckRequestServer(TextModelReference? selection, AiSettings settings)
    {
        if (selection?.Backend == AiBackend.ComfyUI && !SameServer(selection.ComfyUrl, settings.ComfyUrl))
            throw new AiGenerationException("The ComfyUI server changed. Refresh the model picker before trying again.");
        if (selection?.Backend == AiBackend.Vllm) _ = OpenAiConnections.Capture(selection, settings);
    }
    public static TextModelReference Default(AiSettings settings) => settings.TextDefault ?? (settings.DefaultBackend switch
    {
        AiBackend.ComfyUI => new(AiBackend.ComfyUI, settings.ComfyModel, settings.ComfyModel, settings.ComfyUrl),
        AiBackend.Codex => new(AiBackend.Codex, settings.Codex.TextModel, settings.Codex.TextModel, ReasoningEffort: settings.Codex.TextEffort),
        AiBackend.ClaudeCode => new(AiBackend.ClaudeCode, settings.ClaudeCode.TextModel, settings.ClaudeCode.TextModel, ReasoningEffort: settings.ClaudeCode.TextEffort),
        AiBackend.Vllm => Reference(AiBackend.Vllm, "", "", settings),
        _ => new(AiBackend.OpenRouter, settings.OpenRouterModel, settings.OpenRouterModel)
    });

    public static string Key(TextModelReference model) => $"{model.Backend}\n{model.Model}\n{(model.Backend == AiBackend.ComfyUI ? AiProviderRegistry.NormalizeComfyUrl(model.ComfyUrl!).ToUpperInvariant() : model.Backend == AiBackend.Vllm ? VllmConnection.NormalizeUrl(model.VllmUrl!) : "")}";
    public static bool SameVllmServer(string? left, string? right) => left is not null && right is not null &&
        VllmConnection.NormalizeUrl(left) == VllmConnection.NormalizeUrl(right);
    public static TextModelReference WithDefaultEffort(TextModelReference model, AiSettings settings) =>
        UsesNamedEffort(model.Backend) && model.ProfileId is null && model.ReasoningEffort is null
            ? model with { ReasoningEffort = DefaultEffort(model.Backend, settings) }
            : model;
    // The CLI providers take a named reasoning effort with a global text default.
    public static bool UsesNamedEffort(AiBackend backend) => backend is AiBackend.Codex or AiBackend.ClaudeCode;
    public static string? DefaultEffort(AiBackend backend, AiSettings settings) => backend switch
    {
        AiBackend.Codex => settings.Codex.TextEffort, AiBackend.ClaudeCode => settings.ClaudeCode.TextEffort, _ => null
    };
    public static bool Same(TextModelReference? left, TextModelReference? right) =>
        left is not null && right is not null && Key(left) == Key(right);
    public static bool SameServer(string? left, string? right) => left is not null && right is not null &&
        string.Equals(AiProviderRegistry.NormalizeComfyUrl(left), AiProviderRegistry.NormalizeComfyUrl(right), StringComparison.OrdinalIgnoreCase);
    public static TextModelReference Normalize(TextModelReference model) => model with
    {
        Model = model.Model.Trim(), Name = model.Name.Trim(),
        ComfyUrl = model.Backend == AiBackend.ComfyUI ? AiProviderRegistry.NormalizeComfyUrl(model.ComfyUrl!) : null,
        VllmUrl = model.Backend == AiBackend.Vllm ? VllmConnection.NormalizeUrl(model.VllmUrl!) : null
    };
    public static void Validate(TextModelReference? model)
    {
        if (model is null || !Enum.IsDefined(model.Backend) || string.IsNullOrWhiteSpace(model.Model) || string.IsNullOrWhiteSpace(model.Name) ||
            (model.Backend == AiBackend.ComfyUI && (!Uri.TryCreate(model.ComfyUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)))
            throw new WorkspaceStoreException("A saved text-model choice is invalid. Check its provider, model, and server address.");
        TextModelProfiles.ValidateReference(model);
        if (model.Backend == AiBackend.Vllm) VllmConnection.ValidateUrl(model.VllmUrl);
    }
    public static ComfyTextModelVerification? Verification(TextModelReference model, AiSettings settings, string? version = null) =>
        model.Backend != AiBackend.ComfyUI ? null : settings.ComfyTextModelVerifications
            .Where(item => SameServer(item.ComfyUrl, model.ComfyUrl) && item.Model == model.Model && (version is null || item.ComfyVersion == version))
            .MaxBy(item => item.VerifiedUtc);

    public static string? Issue(TextModelReference model, AiSettings settings, AiConnectionCheck? check, bool requireTest = true)
    {
        if (string.IsNullOrWhiteSpace(model.Model)) return "Choose a model in AI settings.";
        try { Validate(model); }
        catch (WorkspaceStoreException e) { return e.Message; }
        if (model.Backend == AiBackend.ComfyUI && !SameServer(model.ComfyUrl, settings.ComfyUrl)) return "This model belongs to a different ComfyUI server.";
        if (model.Backend == AiBackend.OpenRouter && !settings.HasOpenRouterKey) return "Add an OpenRouter key in Connections.";
        if (model.Backend == AiBackend.Vllm)
        {
            var connection = OpenAiConnections.Find(settings, model.VllmUrl);
            if (connection is null) return "This model belongs to a removed or different OpenAI-compatible connection.";
            if (!connection.Enabled) return $"Enable {connection.Name} in Connections.";
            if (check?.Connections?.FirstOrDefault(c => SameVllmServer(c.Server, model.VllmUrl)) is { Success: false } failed) return failed.Message;
        }
        if (model.Backend == AiBackend.Codex && !settings.Codex.Enabled) return "Enable Codex in Connections.";
        if (model.Backend == AiBackend.ClaudeCode && !settings.ClaudeCode.Enabled) return "Enable Claude Code in Connections.";
        if (check is null) return "Refresh this provider to check availability.";
        if (!check.Success) return check.Message;
        if (CatalogModel(model, check) is not { } catalogModel) return "This model is no longer available. Choose another model.";
        if (UsesNamedEffort(model.Backend) && model.ReasoningEffort is { } effort && catalogModel.ReasoningEfforts?.Contains(effort) != true) return "Choose a supported reasoning effort for this model.";
        if (TextModelProfiles.CatalogIssue(model, catalogModel) is { } profileIssue) return profileIssue;
        if (requireTest && model.Backend == AiBackend.ComfyUI &&
            (check.BackendVersion is null || Verification(model, settings, check.BackendVersion) is null))
            return "Test this model with the current ComfyUI version before selecting it.";
        return null;
    }
}
