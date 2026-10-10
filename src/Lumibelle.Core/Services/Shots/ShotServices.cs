using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class ShotServices
{
    public static IServiceCollection AddShots(this IServiceCollection services)
    {
        services.AddSingleton<lumibelle.Services.Assets.IAssetReelStore>(s => s.GetRequiredService<lumibelle.Services.Assets.FileAssetStore>());
        services.AddSingleton<IVoiceStore>(s => s.GetRequiredService<FileAssetStore>());
        services.AddSingleton<IShotStore, FileShotStore>();
        services.AddSingleton<IShotProjectCopyStore, ShotProjectCopyStore>();
        services.AddSingleton<IAssetReuseStore>(s => s.GetRequiredService<FileAssetStore>());
        services.AddSingleton<lumibelle.Services.Production.IProductionStore, lumibelle.Services.Production.FileProductionStore>();
        services.AddSingleton<lumibelle.Services.Production.IGenerationSetupStore, lumibelle.Services.Production.FileGenerationSetupStore>();
        services.AddSingleton<ICutStore, FileCutStore>();
        services.AddSingleton<ICutExporter, CutExporter>();
        services.AddHostedService<LegacyVideoArchiveRecovery>();
        services.AddSingleton<IShotPlanner, ShotPlanner>();
        services.AddSingleton<IProductionMediaTools, ProductionMediaTools>();
        services.AddSingleton<lumibelle.Services.Production.IReferenceVideoStore, lumibelle.Services.Production.FileReferenceVideoStore>();
        services.AddSingleton<lumibelle.Services.Production.ReelReplacement>();
        services.AddSingleton<TakeFrameReader>();
        services.AddSingleton<ComfyH3Video>();
        services.AddSingleton<IVideoGenerator>(s => s.GetRequiredService<ComfyH3Video>());
        services.AddSingleton<IMediaTrashStore, MediaTrashStore>();
        services.AddSingleton<IFrameArchiveCleanupStore, FrameArchiveCleanupStore>();
        services.AddHostedService<FrameArchiveCleanupRecovery>();
        services.AddHostedService<MediaTrashCleanupService>();
        return services;
    }

}
