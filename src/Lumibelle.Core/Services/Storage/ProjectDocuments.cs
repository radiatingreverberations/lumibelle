namespace lumibelle.Services.Storage;

/// <summary>Every kind of project document kept in <c>project.sqlite</c>.</summary>
internal static class ProjectDocuments
{
    public static IReadOnlyList<IProjectDocumentStore> All =>
    [
        Shots.FileShotStore.Documents, Shots.FileCutStore.Documents, Shots.FileRefinementBatchStore.Documents,
        Production.FileProductionStore.Documents, Assets.FileAssetStore.Documents, Assets.FileAssetBatchStore.Documents,
        Story.FileScriptStore.Documents, Story.FileScriptStore.Snapshots, Story.FileSourceStore.Documents,
        Story.FileAssistantHistoryStore.Documents, Story.FileScriptDraftBatchStore.Documents,
        Production.FileProjectDubbingStore.Languages, Production.FileProjectDubbingStore.Documents, AI.FileProjectAiPreferencesStore.Documents
    ];

    /// <summary>Brings every document of a project folder in from legacy JSON now, instead of on first use.</summary>
    public static async Task MigrateAllAsync(string directory, Guid project, CancellationToken ct)
    {
        foreach (var store in All) await store.EnsureAsync(directory, project, ct);
    }
}
