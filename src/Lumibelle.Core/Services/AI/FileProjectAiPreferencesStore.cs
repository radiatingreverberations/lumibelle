using lumibelle.Models;
using lumibelle.Services.Story;
using lumibelle.Services.Assets;

namespace lumibelle.Services.AI;

public sealed class FileProjectAiPreferencesStore(ProjectFiles files) : IProjectAiPreferencesStore
{
    public async Task<ProjectAiPreferences> SetVisualStyleAsync(Guid projectId, VisualStyle style, long expectedRevision, CancellationToken cancellationToken = default)
    {
        style = VisualStyles.Normalize(style);
        var path = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "ai-preferences.json");
        using var gate = await ProjectFiles.LockAsync(path, cancellationToken);
        var current = await ReadAsync(path, projectId, cancellationToken);
        if (current.VisualStyleRevision != expectedRevision) throw new WorkspaceConflictException();
        var updated = current with { VisualStyle = style, VisualStyleRevision = checked(expectedRevision + 1) };
        await AtomicJsonFile.WriteAsync(path, updated, cancellationToken);
        return updated;
    }
    public async Task<ProjectAiPreferences> SetImageDefaultAsync(Guid projectId, ImageWorkflow? workflow, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (workflow is { } value && !Enum.IsDefined(value)) throw new WorkspaceStoreException("Choose a supported image model.");
        var path = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "ai-preferences.json");
        using var gate = await ProjectFiles.LockAsync(path, cancellationToken);
        var current = await ReadAsync(path, projectId, cancellationToken);
        if (current.ImageDefaultRevision != expectedRevision) throw new WorkspaceConflictException();
        var updated = current with { ImageDefault = workflow, ImageDefaultRevision = checked(expectedRevision + 1) };
        await AtomicJsonFile.WriteAsync(path, updated, cancellationToken);
        return updated;
    }

    public async Task<ProjectAiPreferences> SetTextDefaultAsync(Guid projectId, TextModelReference? selection, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (selection is not null) { TextModelPolicy.Validate(selection); selection = TextModelPolicy.Normalize(selection); }
        var path = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "ai-preferences.json");
        using var gate = await ProjectFiles.LockAsync(path, cancellationToken);
        var current = await ReadAsync(path, projectId, cancellationToken);
        if (current.TextDefaultRevision != expectedRevision) throw new WorkspaceConflictException();
        var updated = current with { TextDefault = selection, TextDefaultRevision = checked(expectedRevision + 1) };
        await AtomicJsonFile.WriteAsync(path, updated, cancellationToken);
        return updated;
    }
    public async Task<ProjectAiPreferences> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "ai-preferences.json");
        return await ReadAsync(path, projectId, cancellationToken);
    }

    public async Task<ProjectAiPreferences> SetSelectionAsync(Guid projectId, TextAssistantStudio studio,
        TextModelReference? selection, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(studio)) throw new ArgumentOutOfRangeException(nameof(studio));
        if (selection is not null) { TextModelPolicy.Validate(selection); selection = TextModelPolicy.Normalize(selection); }
        var path = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "ai-preferences.json");
        using var gate = await ProjectFiles.LockAsync(path, cancellationToken);
        var current = await ReadAsync(path, projectId, cancellationToken);
        var updated = studio switch
        {
            TextAssistantStudio.Shots => current with { Shots = selection },
            TextAssistantStudio.Production => current with { Production = selection },
            TextAssistantStudio.Story => current with { Story = selection },
            TextAssistantStudio.AssetExtraction => current with { AssetExtraction = selection },
            TextAssistantStudio.PromptEnhancement => current with { PromptEnhancement = selection },
            _ => throw new ArgumentOutOfRangeException(nameof(studio))
        };
        await AtomicJsonFile.WriteAsync(path, updated, cancellationToken);
        return updated;
    }

    public async Task<ProjectAiPreferences> SetLoraVisibilityAsync(Guid projectId, LoraVisibility visibility,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        LoraPolicy.ValidateVisibility(visibility);
        var captured = LoraPolicy.NormalizeVisibility(visibility);
        var path = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "ai-preferences.json");
        using var gate = await ProjectFiles.LockAsync(path, cancellationToken);
        var current = await ReadAsync(path, projectId, cancellationToken);
        if (current.LoraVisibilityRevision != expectedRevision) throw new WorkspaceConflictException();
        var updated = current with { LoraVisibility = captured, LoraVisibilityRevision = checked(expectedRevision + 1) };
        await AtomicJsonFile.WriteAsync(path, updated, cancellationToken);
        return updated;
    }

    private static async Task<ProjectAiPreferences> ReadAsync(string path, Guid id, CancellationToken ct)
    {
        var preferences = await AtomicJsonFile.ReadAsync<ProjectAiPreferences>(path, ct) ?? new() { ProjectId = id };
        if (preferences.SchemaVersion is not (1 or 2) || preferences.ProjectId != id || preferences.LoraVisibilityRevision < 0 || preferences.TextDefaultRevision < 0 ||
            preferences.ImageDefaultRevision < 0 || preferences.VisualStyleRevision < 0 || preferences.VisualStyle is null || preferences.ImageDefault is { } workflow && !Enum.IsDefined(workflow))
            throw new WorkspaceStoreException("Project AI preferences use an invalid or unsupported format. They have not been replaced.");
        // Old studio choices remain readable, but no longer control new requests.
        if (preferences.SchemaVersion == 1) preferences = preferences with { SchemaVersion = 2, TextDefault = null, TextDefaultRevision = 0 };
        if (preferences.TextDefault is not null) TextModelPolicy.Validate(preferences.TextDefault);
        LoraPolicy.ValidateVisibility(preferences.LoraVisibility);
        VisualStyles.Validate(preferences.VisualStyle);
        return preferences;
    }
}
