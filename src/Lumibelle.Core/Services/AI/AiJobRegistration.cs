using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace lumibelle.Services.AI;

public static class AiJobRegistration
{
    public static IServiceCollection AddAiJobQueue(this IServiceCollection services, string? rootDirectory = null)
    {
        services.AddSingleton<IAiJobStore>(s => rootDirectory is null
            ? new FileAiJobStore(s.GetRequiredService<ApplicationPaths>().Jobs, s.GetRequiredService<TimeProvider>())
            : new FileAiJobStore(rootDirectory, s.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ICodexTransportFactory, CodexTransportFactory>();
        services.TryAddSingleton<ICodexClient, CodexClient>();
        services.TryAddSingleton<IClaudeCodeProcessFactory, ClaudeCodeProcessFactory>();
        services.TryAddSingleton<IClaudeCodeClient, ClaudeCodeClient>();
        services.AddSingleton<ComfyJobExecution>();
        services.AddSingleton<AiTextJobCapture>();
        services.AddSingleton<FileSourceStore>();
        services.AddSingleton<SourceWorkflow>();
        services.AddSingleton<FileScriptDraftBatchStore>();
        services.AddSingleton<ScriptDraftWorkflow>();
        services.AddSingleton<IContentProbeStore, FileContentProbeStore>();
        services.AddSingleton<ContentProbeCapture>();
        services.AddSingleton<AiImageJobCapture>();
        services.AddSingleton<AiVideoJobCapture>();
        services.AddSingleton<AiReelCapture>();
        services.AddSingleton<ReelVideoPublication>();
        services.AddSingleton<IAiJobReviewStore, AiJobReviewStore>();
        services.AddSingleton<IAiJobHandler, AiTextJobHandler>();
        services.AddSingleton<IAiJobHandler, ContentProbeJobHandler>();
        services.AddSingleton<AiJobCoordinator>();
        services.AddSingleton<AiProviderCapacityService>();
        services.AddSingleton<CodexAllowanceService>();
        services.AddHostedService(s => s.GetRequiredService<AiJobCoordinator>());
        services.AddHostedService<SourceWorkflowWorker>();
        services.AddHostedService<ScriptDraftWorkflowWorker>();
        services.AddHostedService<CodexLaunch>();
        services.AddHostedService<ClaudeCodeLaunch>();
        services.AddSingleton<FileAssistantHistoryStore>();
        services.Replace(ServiceDescriptor.Singleton<IAssistantHistoryStore, QueuedAssistantHistoryStore>());
        return services;
    }
}
