using System.Text.Json;
using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class StreamingTextRequestComponentTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.StreamingPreviewTests", Guid.NewGuid().ToString("N"));
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly FileAiJobStore _jobs;
    private readonly AiJobCoordinator _queue;
    private readonly StreamingHandler _handler = new();
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    public StreamingTextRequestComponentTests()
    {
        var settings = new FakeAiSettingsStore { Value = new() { HasOpenRouterKey = true } };
        _jobs = new(_root, TimeProvider.System);
        _queue = new(_jobs, settings, [_handler], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        Services.AddMudServices(); Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IAiJobStore>(_jobs); Services.AddSingleton(_queue);
        Services.AddSingleton<IAiSettingsStore>(settings); Services.AddSingleton<IAiProviderRegistry>(new FakeProviders());
        Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<MudPopoverProvider>(); _dialogs = Render<MudDialogProvider>();
    }

    [Fact]
    public async Task SharedRequestInspectorRefreshesCardsWithoutAJobStateChangeOrParentRender()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _queue.StartAsync(ct);
        var model = new TextModelReference(AiBackend.OpenRouter, "mock/text", "Sample model");
        var captured = new AiTextJobRequest(3, AiJobKind.ScriptAssistant, model, false, new(), "preview-test", .7f, 0,
            JsonSerializer.SerializeToElement(new { }), []);
        var submission = AiJobSubmission.Create(Guid.NewGuid(), captured.Kind, model.Backend, new(_projectId), "Sample project", "Draft sequence", Guid.NewGuid(), captured);
        await _queue.EnqueueAsync(submission, ct);
        var context = await _handler.Ready.Task.WaitAsync(ct);
        await _queue.RefreshAsync(ct);
        var job = _queue.View.Jobs.Single(j => j.Id == submission.Id);
        var ui = Render<TextAssistance>(p => p.Add(c => c.ProjectId, _projectId).Add(c => c.Studio, TextAssistantStudio.Story)
            .Add(c => c.Request, new TextRequestPresentation(job, "Drafting…")));
        await ui.InvokeAsync(() => ui.Instance.OpenRequestAsync());
        _dialogs.WaitForAssertion(() => Assert.Contains("A river", _dialogs.Find("[aria-label='Live response preview']").TextContent));
        await StreamingHandler.Update(context, "{\"beats\":[\"Eira finds the key.\",\"A river leads to a garden");
        _dialogs.WaitForAssertion(() => Assert.Contains("A river leads to a garden", _dialogs.Find("[aria-label='Live response preview']").TextContent));
        Assert.Equal(job.Version, _queue.View.Jobs.Single(j => j.Id == job.Id).Version);
        Assert.Equal(2, _dialogs.FindAll(".streaming-preview-card").Count);
        Assert.Contains(_dialogs.FindAll("pre"), pre => pre.TextContent.Contains("{\"beats\":", StringComparison.Ordinal));
        Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Apply changes");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class StreamingHandler : IAiJobHandler
    {
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ScriptAssistant];
        public TaskCompletionSource<AiJobContext> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        {
            await Update(context, "{\"beats\":[\"Eira finds the key.\",\"A river");
            Ready.TrySetResult(context);
            await Task.Delay(Timeout.Infinite, ct);
            return AiJobOutcome.Complete();
        }
        public static async Task Update(AiJobContext context, string raw)
        {
            await context.SaveResultAsync(new AiTextJobResult(raw));
            await context.ReportAsync(new(new(GenerationPhase.Generating, "Receiving…", Current: raw.Length)));
        }
        public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(AiJobOutcome.Attention("Interrupted fixture", AiJobRecovery.GenerateAgain));
        public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
    }
}
