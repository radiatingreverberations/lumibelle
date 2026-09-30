using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3Loras
{
    /// <summary>The LoRAs a take applies: the preset's, then the shot's own; for the same LoRA the shot's entry replaces the preset's.</summary>
    public static IReadOnlyList<LoraSelection> Selections(Shot shot) => Merge(shot.Loras, shot.ShotLoras);
    public static IReadOnlyList<LoraSelection> Merge(IReadOnlyList<LoraSelection>? preset, IReadOnlyList<LoraSelection>? own) =>
        own is not { Count: > 0 } ? preset ?? [] : [.. (preset ?? []).Where(p => !own.Any(o => LoraPolicy.Same(o.Reference, p.Reference))), .. own];
    public static IReadOnlyList<AppliedLora> Applied(VideoSnapshot snapshot) => snapshot.AppliedLoras ?? [];
    public static void ValidateSelections(Shot shot)
    {
        if (LoraPolicy.InvalidSelections(shot.Loras ?? []) || LoraPolicy.InvalidSelections(shot.ShotLoras ?? []) ||
            LoraPolicy.InvalidSelections(Selections(shot)) || Selections(shot).Any(s => s.Reference.Workflow != LoraWorkflow.MiniMaxH3Ref2VA))
            throw new WorkspaceStoreException("Use distinct H3 LoRAs with finite strengths between -100 and 100.");
    }
    public static IReadOnlyList<AppliedLora>? Capture(Shot shot, AiSettings settings, LoraVisibility visibility, ComfyLoraCheck? check)
    {
        ValidateSelections(shot); LoraPolicy.ValidateVisibility(visibility);
        var applied = new List<AppliedLora>();
        foreach (var item in Selections(shot).Where(s => s.Enabled && s.Strength != 0))
        {
            if (LoraPolicy.Issue(item, settings, LoraWorkflow.MiniMaxH3Ref2VA, check, visibility) is { } issue)
                throw new WorkspaceStoreException($"{item.Reference.Name}: {issue}");
            applied.Add(new(settings.LoraLibrary.Single(d => LoraPolicy.Same(d.Reference, item.Reference)).Reference, item.Strength));
        }
        return applied.Count == 0 ? null : Array.AsReadOnly(applied.ToArray());
    }
    public static void ValidateSnapshot(VideoSnapshot snapshot)
    {
        ValidateSelections(snapshot.Shot);
        var applied = Applied(snapshot);
        var selected = Selections(snapshot.Shot).Where(s => s.Enabled && s.Strength != 0).ToArray();
        if (LoraPolicy.InvalidApplied(applied, LoraWorkflow.MiniMaxH3Ref2VA) || selected.Length != applied.Count ||
            applied.Where((a, i) => !LoraPolicy.Same(a.Reference, selected[i].Reference) || a.Strength != selected[i].Strength ||
                AiProviderRegistry.NormalizeComfyUrl(a.Reference.ComfyUrl) != AiProviderRegistry.NormalizeComfyUrl(snapshot.ExecutionComfyUrl) ||
                LoraPolicy.ReservedH3(a.Reference.FileName, snapshot.Settings)).Any())
            throw new WorkspaceStoreException("The captured H3 LoRAs do not match the shot, server, or optional-LoRA contract. Start a new batch.");
    }
    public static void CheckSubmission(VideoSnapshot snapshot, ComfyLoraCheck? check)
    {
        ValidateSnapshot(snapshot);
        if (Applied(snapshot).Count == 0) return;
        if (check is not { Success: true }) throw new WorkspaceStoreException(check?.Message ?? "Refresh Video models to check the optional LoRA loader.");
        foreach (var item in Applied(snapshot))
        {
            if (!check.Files.Contains(item.Reference.FileName, StringComparer.Ordinal))
                throw new WorkspaceStoreException($"{item.Reference.Name}: the exact captured LoRA file '{item.Reference.FileName}' is missing. Restore it on {snapshot.ExecutionComfyUrl} and retry.");
            if (item.Strength < check.MinimumStrength || item.Strength > check.MaximumStrength)
                throw new WorkspaceStoreException($"{item.Reference.Name}: the loader supports strengths from {check.MinimumStrength} to {check.MaximumStrength}.");
        }
    }
}
