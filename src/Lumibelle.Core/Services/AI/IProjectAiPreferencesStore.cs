using lumibelle.Models;

namespace lumibelle.Services.AI;

public interface IProjectAiPreferencesStore
{
    Task<ProjectAiPreferences> LoadAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectAiPreferences> SetVisualStyleAsync(Guid projectId, VisualStyle style, long expectedRevision, CancellationToken cancellationToken = default)
        => throw new lumibelle.Services.Story.WorkspaceStoreException("Project visual style saving is unavailable.");
    Task<ProjectAiPreferences> SetTextDefaultAsync(Guid projectId, TextModelReference? selection, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<ProjectAiPreferences> SetImageDefaultAsync(Guid projectId, ImageWorkflow? workflow, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<ProjectAiPreferences> SetLoraVisibilityAsync(Guid projectId, LoraVisibility visibility, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<ProjectAiPreferences> SetSelectionAsync(Guid projectId, TextAssistantStudio studio, TextModelReference? selection,
        CancellationToken cancellationToken = default);
}
