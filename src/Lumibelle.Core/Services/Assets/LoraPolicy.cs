using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static class LoraPolicy
{
    public static IReadOnlyList<string> ParseTags(string text) => NormalizeTags(text.Split(',', StringSplitOptions.RemoveEmptyEntries));
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => Array.AsReadOnly(tags.Select(t => t.Trim().ToLowerInvariant())
        .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    public static bool InvalidTags(IReadOnlyList<string>? tags) => tags is null || tags.Count > 32 ||
        tags.Any(t => t is null || t.Trim().Length is 0 or > 64 || t.Contains(',') || t.Any(char.IsControl));
    public static void ValidateVisibility(LoraVisibility? visibility)
    {
        if (visibility is null || InvalidTags(visibility.OnlyTags) || InvalidTags(visibility.HiddenTags))
            throw new WorkspaceStoreException("Use up to 32 tags per list, with 1–64 characters per tag and commas between tags.");
    }
    public static LoraVisibility NormalizeVisibility(LoraVisibility visibility) => new()
    { OnlyTags = NormalizeTags(visibility.OnlyTags), HiddenTags = NormalizeTags(visibility.HiddenTags) };
    public static IReadOnlyList<LoraDefinition> CopyLibrary(IReadOnlyList<LoraDefinition> library) =>
        Array.AsReadOnly(library.Select(d => d with { Tags = Array.AsReadOnly(d.Tags.ToArray()) }).ToArray());
    public static bool Visible(LoraDefinition definition, LoraVisibility visibility) =>
        !definition.Tags.Intersect(visibility.HiddenTags, StringComparer.OrdinalIgnoreCase).Any() &&
        (visibility.OnlyTags.Count == 0 || definition.Tags.Intersect(visibility.OnlyTags, StringComparer.OrdinalIgnoreCase).Any());
    public static string? VisibilityIssue(LoraSelection selection, AiSettings settings, LoraVisibility visibility)
    {
        var definition = settings.LoraLibrary.FirstOrDefault(d => Same(d.Reference, selection.Reference));
        return definition is not null && !Visible(definition, visibility)
            ? "Excluded by this project’s LoRA visibility. Disable or remove it, or change the filters in Project settings." : null;
    }
    public static string Key(LoraReference reference) => AiProviderRegistry.NormalizeComfyUrl(reference.ComfyUrl) + "\n" + reference.FileName;
    public static bool Same(LoraReference a, LoraReference b) => Key(a) == Key(b);
    public static bool IsIdentityEdit(string file) => file.Replace('\\', '/').Split('/')[^1].StartsWith("krea2_identity_edit", StringComparison.OrdinalIgnoreCase);
    public static bool IsH3Turbo(string file) => Path.GetFileName(file.Replace('\\', '/')).StartsWith("minimax_h3_", StringComparison.OrdinalIgnoreCase) && file.Contains("turbo", StringComparison.OrdinalIgnoreCase);
    public static bool ReservedH3(string file, H3Settings settings) => IsH3Turbo(file) || file == settings.TurboLora || file == settings.Turbo8StepLora || file == settings.LarryLora || file == settings.PddCheckpoint || file == settings.HyperFlowLora ||
        Path.GetFileName(file.Replace('\\', '/')).Contains("hyperflow", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(file.Replace('\\', '/')).Contains("pdd", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(file.Replace('\\', '/')).Contains("Ref2VA-Acc", StringComparison.OrdinalIgnoreCase);
    public static bool Invalid(LoraReference? reference) => reference is null || !Enum.IsDefined(reference.Workflow) ||
        string.IsNullOrWhiteSpace(reference.Name) || string.IsNullOrWhiteSpace(reference.FileName) || IsIdentityEdit(reference.FileName) ||
        !Uri.TryCreate(reference.ComfyUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
        url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0;
    public static bool InvalidStrength(float strength) => !float.IsFinite(strength) || strength is < -100 or > 100;
    public static bool InvalidLibrary(IReadOnlyList<LoraDefinition>? items) => items is null ||
        items.Any(d => d is null || Invalid(d.Reference) || InvalidStrength(d.DefaultStrength) || d.TriggerText is null || InvalidTags(d.Tags)) ||
        items.DistinctBy(d => Key(d.Reference)).Count() != items.Count;
    public static bool InvalidSelections(IReadOnlyList<LoraSelection>? items) => items is null ||
        items.Any(s => s is null || Invalid(s.Reference) || InvalidStrength(s.Strength)) ||
        items.DistinctBy(s => Key(s.Reference)).Count() != items.Count;
    public static bool InvalidApplied(IReadOnlyList<AppliedLora>? items, ImageWorkflow workflow) => workflow == ImageWorkflow.CodexImages ? items is null || items.Count != 0 : InvalidApplied(items, workflow.LoraWorkflow());
    public static bool InvalidApplied(IReadOnlyList<AppliedLora>? items, LoraWorkflow workflow) => items is null ||
        items.Any(s => s is null || Invalid(s.Reference) || InvalidStrength(s.Strength) || s.Strength == 0 || s.Reference.Workflow != workflow) ||
        items.DistinctBy(s => Key(s.Reference)).Count() != items.Count;
    public static IReadOnlyList<LoraSelection> Capture(IReadOnlyList<LoraSelection> items)
    {
        if (InvalidSelections(items)) throw new AiGenerationException("Check LoRA selections: use distinct files and finite strengths between -100 and 100.");
        return Array.AsReadOnly(items.ToArray());
    }
    public static string? Issue(LoraSelection item, AiSettings settings, ImageWorkflow workflow, ComfyLoraCheck? check, LoraVisibility? visibility = null)
        => Issue(item, settings, workflow.LoraWorkflow(), check, visibility);
    public static string? Issue(LoraSelection item, AiSettings settings, LoraWorkflow workflow, ComfyLoraCheck? check, LoraVisibility? visibility = null)
    {
        if (Invalid(item.Reference) || InvalidStrength(item.Strength)) return "This LoRA selection is invalid.";
        if (item.Reference.Workflow != workflow) return "This LoRA is assigned to another workflow.";
        if (workflow == LoraWorkflow.MossImage && item.Reference.FileName == settings.MossImage?.EditLora)
            return "The MOSS edit LoRA is managed by the image workflow. Remove this optional selection.";
        if (workflow == LoraWorkflow.MiniMaxH3Ref2VA && ReservedH3(item.Reference.FileName, settings.H3)) return "Acceleration weights are managed by the generation preset. Remove this optional selection.";
        if (AiProviderRegistry.NormalizeComfyUrl(item.Reference.ComfyUrl) != AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl)) return "This LoRA belongs to another ComfyUI server.";
        var definition = settings.LoraLibrary.FirstOrDefault(d => Same(d.Reference, item.Reference));
        if (definition is null) return "This LoRA is no longer registered. Manage LoRAs in AI settings.";
        if (definition.Reference.Workflow != workflow) return "This LoRA’s workflow assignment has changed.";
        if (visibility is not null && VisibilityIssue(item, settings, visibility) is { } visibilityIssue) return visibilityIssue;
        if (check is null) return "Refresh LoRAs to check availability.";
        if (!check.Success) return check.Message;
        if (!check.Files.Contains(item.Reference.FileName, StringComparer.Ordinal)) return "The exact LoRA file is missing from ComfyUI. Restore the file or replace this selection.";
        if (item.Strength < check.MinimumStrength || item.Strength > check.MaximumStrength) return $"The loader supports strengths from {check.MinimumStrength} to {check.MaximumStrength}.";
        return null;
    }
    internal static async Task<IReadOnlyList<AppliedLora>> PrepareAsync(IReadOnlyList<LoraSelection> items, AiSettings settings,
        ImageWorkflow workflow, IHttpClientFactory clients, CancellationToken ct)
    {
        var captured = Capture(items).Where(s => s.Enabled && s.Strength != 0).ToArray();
        if (captured.Length == 0) return [];
        var check = await new ComfyLoraCatalog(clients).CheckAsync(settings, ct);
        foreach (var item in captured)
            if (Issue(item, settings, workflow, check) is { } issue) throw new AiGenerationException($"{item.Reference.Name}: {issue}");
        return Array.AsReadOnly(captured.Select(s => new AppliedLora(settings.LoraLibrary.Single(d => Same(d.Reference, s.Reference)).Reference, s.Strength)).ToArray());
    }
    // All current graph node IDs are numeric; a distinct prefix keeps this chain independent of reference counts.
    internal static string AddNodes(Dictionary<string, object> nodes, string input, IEnumerable<AppliedLora> loras)
    {
        var index = 0;
        foreach (var lora in loras)
        {
            var id = $"lora_{++index}";
            nodes[id] = new { class_type = "LoraLoaderModelOnly", inputs = new { model = new object[] { input, 0 }, lora_name = lora.Reference.FileName, strength_model = lora.Strength } };
            input = id;
        }
        return input;
    }
}
