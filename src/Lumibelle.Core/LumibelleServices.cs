using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
namespace lumibelle;
public static class LumibelleServices
{
    public static IServiceCollection AddLumibelleCore(this IServiceCollection services, ApplicationPaths paths, bool exclusive = true)
    {
        services.AddSingleton(paths);
        services.AddSingleton<ILoggerProvider>(new ApplicationFileLoggerProvider(paths));
        services.AddSingleton<MediaResources>();
        services.AddSingleton<Services.Projects.IProjectPackageService, Services.Projects.ProjectPackageService>();
        services.Configure<ProjectStorageOptions>(o => o.RootDirectory = paths.Projects);
        if (exclusive) services.AddHostedService<WorkspaceOwnership>();
        services.AddSingleton(TimeProvider.System);
        // Only the exclusive owner of a library locks the outside project folders it uses.
        services.AddSingleton(_ => new ProjectLocations(paths, lease: exclusive));
        services.AddSingleton<IProjectStore>(s => new FileProjectStore(paths, s.GetRequiredService<TimeProvider>(), s.GetRequiredService<ILogger<FileProjectStore>>(), s.GetRequiredService<ProjectLocations>()));
        services.AddSingleton<IProjectRoutes, FileProjectRoutes>();
        services.AddSingleton(s => new ProjectFiles(paths, s.GetRequiredService<IProjectStore>(), s.GetRequiredService<ILogger<SqliteMediaIndex>>(), s.GetRequiredService<ProjectLocations>()));
        services.AddSingleton<Services.Projects.IProjectFolders, Services.Projects.ProjectFolders>();
        services.AddSingleton<Services.Projects.IProjectCompaction, Services.Projects.ProjectCompaction>();
        services.AddSingleton<ApplicationSession>();
        services.AddSingleton<IScriptStore, FileScriptStore>();
        services.AddSingleton<IAssistantHistoryStore, FileAssistantHistoryStore>();
        services.AddSingleton<IAiSettingsStore>(s => new FileAiSettingsStore(paths, s.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<IProjectAiPreferencesStore, FileProjectAiPreferencesStore>();
        services.AddComfyAccess();
        services.AddSingleton<IComfyExecutionMonitor, ComfyExecutionMonitor>();
        services.AddSingleton<AiProviderRegistry>();
        services.AddSingleton<IAiProviderRegistry>(s => s.GetRequiredService<AiProviderRegistry>());
        services.AddSingleton<IModelTestRunner>(s => s.GetRequiredService<AiProviderRegistry>());
        services.AddSingleton<IScriptAssistant, ScriptAssistant>();
        services.AddSingleton<IPromptEnhancer, PromptEnhancer>();
        services.AddSingleton<IGuidanceAssistant, GuidanceAssistant>();
        services.AddSingleton<FileAssetStore>();
        services.AddSingleton<IAssetStore>(s => s.GetRequiredService<FileAssetStore>());
        services.AddSingleton<IImageTrashStore>(s => s.GetRequiredService<FileAssetStore>());
        services.AddShots();
        services.AddSingleton<Services.Production.IProjectDubbingStore, Services.Production.FileProjectDubbingStore>();
        services.AddSingleton<IAssetExtractor, AssetExtractor>();
        services.AddSingleton<ComfyReferenceImageGenerator>();
        services.AddSingleton<ComfyReferenceImageEditor>();
        services.AddSingleton<ComfyFluxKleinImages>();
        services.AddSingleton<ComfyQwenImage21>();
        services.AddSingleton<ComfyImageService>();
        services.AddSingleton<IComfyLoraCatalog, ComfyLoraCatalog>();
        services.AddSingleton<IReferenceImageGenerator>(services => services.GetRequiredService<ComfyImageService>());
        services.AddSingleton<IReferenceImageEditor>(services => services.GetRequiredService<ComfyImageService>());
        services.AddHttpClient("OpenRouter", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddAiJobQueue();
        services.AddSingleton<IAiTextRepairService, AiTextRepairService>();
        services.AddSingleton<IAiJobHandler, AiModelTestJobHandler>();
        services.AddSingleton<IComfyImageJobAdapter, ComfyImageJobAdapter>();
        services.AddSingleton<IAiJobHandler, AiImageJobHandler>();
        services.AddSingleton<IComfyVideoJobAdapter, ComfyVideoJobAdapter>();
        services.AddSingleton<IAiJobHandler, AiVideoJobHandler>();
        services.AddSingleton<Services.Production.ReelRefModStore>();
        services.AddSingleton<Services.Production.ReelRefModPreparation>();
        services.AddSingleton<ComfyRefModClient>();
        services.AddSingleton<ComfyRefModCache>();
        services.AddSingleton<VisualBriefCache>();
        services.AddSingleton<AiRefModCapture>();
        services.AddSingleton<IAiJobHandler, AiRefModJobHandler>();

        return services;
    }
}
