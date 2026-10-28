using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;
internal sealed class FakeProjectAiPreferencesStore : IProjectAiPreferencesStore
{
    public Dictionary<Guid, ProjectAiPreferences> Values { get; } = [];
    public Exception? SaveError { get; set; }
    public Exception? LoadError { get; set; }
    public async Task<ProjectAiPreferences> SetVisualStyleAsync(Guid projectId, VisualStyle style, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        var current = await LoadAsync(projectId, cancellationToken);
        if (current.VisualStyleRevision != expectedRevision) throw new WorkspaceConflictException();
        return Values[projectId] = current with { VisualStyle = VisualStyles.Normalize(style), VisualStyleRevision = expectedRevision + 1 };
    }
    public async Task<ProjectAiPreferences> SetImageDefaultAsync(Guid projectId, ImageWorkflow? workflow, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        var current = await LoadAsync(projectId, cancellationToken);
        if (current.ImageDefaultRevision != expectedRevision) throw new WorkspaceConflictException();
        return Values[projectId] = current with { ImageDefault = workflow, ImageDefaultRevision = expectedRevision + 1 };
    }
    public async Task<ProjectAiPreferences> SetTextDefaultAsync(Guid projectId, TextModelReference? selection, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        var current = await LoadAsync(projectId, cancellationToken);
        if (current.TextDefaultRevision != expectedRevision) throw new WorkspaceConflictException();
        return Values[projectId] = current with { TextDefault = selection, TextDefaultRevision = expectedRevision + 1 };
    }
    public Task<ProjectAiPreferences> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        LoadError is not null ? Task.FromException<ProjectAiPreferences>(LoadError) : Task.FromResult(Values.GetValueOrDefault(projectId) ?? new() { ProjectId = projectId });
    public async Task<ProjectAiPreferences> SetLoraVisibilityAsync(Guid projectId, LoraVisibility visibility, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        var current = await LoadAsync(projectId, cancellationToken);
        if (current.LoraVisibilityRevision != expectedRevision) throw new WorkspaceConflictException();
        return Values[projectId] = current with { LoraVisibility = LoraPolicy.NormalizeVisibility(visibility), LoraVisibilityRevision = expectedRevision + 1 };
    }
    public async Task<ProjectAiPreferences> SetSelectionAsync(Guid projectId, TextAssistantStudio studio, TextModelReference? selection, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null) throw SaveError;
        var current = await LoadAsync(projectId, cancellationToken);
        return Values[projectId] = studio switch { TextAssistantStudio.Story => current with { Story = selection }, TextAssistantStudio.AssetExtraction => current with { AssetExtraction = selection }, TextAssistantStudio.PromptEnhancement => current with { PromptEnhancement = selection }, _ => throw new ArgumentOutOfRangeException(nameof(studio)) };
    }
}
