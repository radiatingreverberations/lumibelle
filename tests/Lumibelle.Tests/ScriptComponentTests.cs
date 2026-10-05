using System.Text.Json;
using Bunit;
using lumibelle.Components;
using lumibelle.Components.Script;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ScriptComponentTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.ScriptComponents", Guid.NewGuid().ToString("N"));
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    private readonly FakeScriptStore _store = new();
    private readonly ScriptQueueHandler _assistant = new();
    private readonly ScriptReviewGate _reviews = new();
    private readonly FakeProviders _providers = new();
    private readonly List<AssistantRun> _applications = [];
    private readonly ScriptTestHistory _history;
    private readonly AiJobCoordinator _queue;
    private readonly FileAiJobStore _jobs;
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;
    public ScriptComponentTests()
    {
        Services.AddMudServices(); Services.AddSingleton(TimeProvider.System); Services.AddSingleton<MarkdownRenderer>();
        var environment = new StorageTestEnvironment(_root);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(FakeProjectStore.Project("QA script") with { Id = id }) };
        Directory.CreateDirectory(Path.Combine(_root, "App_Data", "Projects", _store.Document.ProjectId.ToString()));
        var files = new ProjectFiles(Options.Create(new ProjectStorageOptions()), environment, projects);
        _jobs = new(environment, TimeProvider.System);
        _history = new(new QueuedAssistantHistoryStore(new(files, new()), _jobs));
        var settings = new FakeAiSettingsStore { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
        var assets = new FakeAssetStore(_store.Document.ProjectId);
        var enhancer = new PromptEnhancer(_providers, settings, assets); var guidance = new GuidanceAssistant(_providers, settings, assets);
        _queue = new(_jobs, settings, [_assistant], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        Services.AddSingleton(_queue); Services.AddSingleton<IAiJobStore>(_jobs); Services.AddSingleton<IAiReviewGate>(_reviews);
        Services.AddSingleton(new AiTextJobCapture(settings, projects, assets, enhancer, guidance));
        Services.AddSingleton<IScriptStore>(_store); Services.AddSingleton<IAssistantHistoryStore>(_history); Services.AddSingleton<ApplicationSession>();
        Services.AddSingleton<IAiProviderRegistry>(_providers); Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
        Services.AddSingleton<IAiSettingsStore>(settings);
        JSInterop.Mode = JSRuntimeMode.Loose; _dialogs = Render<MudDialogProvider>();
        _queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    private IRenderedComponent<ScriptAssistantPanel> Panel(Guid? job = null)
    {
        var panel = Render<ScriptAssistantPanel>(p => p.Add(c => c.ProjectId, _store.Document.ProjectId).Add(c => c.RequestedJobId, job)
            .Add(c => c.Document, _store.Document).Add(c => c.Selection, new ScriptSelection(_store.Document.Blocks[1].Id, 0, _store.Document.Blocks[1].Id, 5))
            .Add(c => c.SelectTarget, scope => Task.FromResult(ScriptAssistantTarget.From(_store.Document, scope, new(_store.Document.Blocks[1].Id, 0, _store.Document.Blocks[1].Id, 5))))
            .Add(c => c.Capture, target => Task.FromResult(new ScriptRequestContext(_store.Document.Copy(), target.Capture(_store.Document))))
            .Add(c => c.Apply, run => { _applications.Add(run); return Task.FromResult(true); }));
        if (job is null) Controls(panel); return panel;
    }
    private IRenderedComponent<ScriptAssistantPanel> Controls(IRenderedComponent<ScriptAssistantPanel> panel) { panel.WaitForElement("#script-operation"); return panel; }
    private IRenderedComponent<MudDialogProvider> ModelControls(IRenderedComponent<ScriptAssistantPanel> panel)
    {
        panel.InvokeAsync(() => panel.Find(".assist-composer-model .model-chip").ClickAsync(new())).GetAwaiter().GetResult();
        _dialogs.WaitForElement("select[id^=text-model-]"); return _dialogs;
    }
    private async Task Click(IRenderedComponent<ScriptAssistantPanel> p, string label)
    {
        if (label is "View request" or "Review latest response") { await p.InvokeAsync(() => p.Find(".request-action-button").ClickAsync(new())); return; }
        if (label is "Revise" or "Draft script" or "Draft replacement") Controls(p);
        if (label is "Revise" or "Draft script") p.WaitForAssertion(() => Assert.False(p.FindAll("button").Single(b => (b.GetAttribute("aria-label") == label || b.TextContent.Trim() == label)).HasAttribute("disabled"), p.Markup));
        await p.InvokeAsync(() => p.FindAll("button").Concat(_dialogs.FindAll("button")).First(b => (b.GetAttribute("aria-label") == label || b.TextContent.Trim() == label)).ClickAsync(new()));
    }
    private async Task<AssistantRun> LastRun() => (await _history.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Last();
    private void Reviewed() => _dialogs.WaitForElement(".script-review", BunitDefaults.WaitTimeout(5));

    [Fact]
    public async Task DraftRequiresReviewAndReopeningKeepsHistory()
    {
        var panel = Panel(); await Click(panel, "Draft replacement"); await Click(panel, "Draft script"); Reviewed(); Assert.Empty(_applications);
        Assert.Empty(_dialogs.FindAll(".is-comparison")); await Click(panel, "Compare changes");
        _dialogs.WaitForAssertion(() => Assert.Contains("Original captured target", _dialogs.Markup)); Assert.Contains("Proposed script", _dialogs.Markup);
        await Click(panel, "Replace entire script"); Assert.Single(_applications); Assert.True((await LastRun()).Applied);
        await panel.Instance.DisposeAsync(); panel.Dispose(); var reopened = Panel(); reopened.WaitForAssertion(() => Assert.Equal("Revise", reopened.Find(".request-action-button").GetAttribute("aria-label"))); Assert.Equal(1, _assistant.Calls);
    }
    [Fact]
    public async Task ScopedRequestCapturesModelAndStaleTargetNeedsExplicitRetarget()
    {
        var model = new TextModelReference(AiBackend.OpenRouter, "test/alternate", "Alternate");
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>(); settings.Value = settings.Value with { StarredTextModels = [model] };
        _providers.Models = [new(model.Model, model.Name), new("test/model", "Default")];
        var panel = Panel(); await panel.InvokeAsync(() => ModelControls(panel).Find("select[id^=text-model-]").ChangeAsync(new() { Value = TextModelPolicy.Key(model) })); await Click(panel, "Done");
        await panel.InvokeAsync(() => Controls(panel).Find("#script-scope").ChangeAsync(new() { Value = "Scene" })); await Click(panel, "Revise");
        panel.WaitForAssertion(() => Assert.True(_assistant.Calls == 1, _queue.Error ?? panel.Markup), BunitDefaults.WaitTimeout(5)); Reviewed();
        Assert.Equal(model, _assistant.LastRequest!.Selection); Assert.Equal(model.Model, (await LastRun()).Model);
        var changed = _store.Document.Copy(); changed.Blocks[1] = changed.Blocks[1] with { Spans = [new("Changed scene action.")] }; _store.Document = changed;
        panel.Render(p => p.Add(c => c.Document, changed));
        Assert.True(_dialogs.FindAll("button").Single(b => b.TextContent == "Apply changes").HasAttribute("disabled"));
        await Click(panel, "Request fresh changes"); panel.WaitForAssertion(() => Assert.Equal(2, _assistant.Calls));
        Assert.Equal("Changed scene action.", _assistant.LastRequest!.Run.Target.OriginalBlocks[1].Text);
    }
    [Fact]
    public async Task PendingRequestDisablesSwitchingAndCancellationKeepsPartialOutput()
    {
        _assistant.Wait = true; var panel = Panel(); Controls(panel).Find("#script-instructions").Input("Keep this instruction");
        await Click(panel, "Draft replacement"); await Click(panel, "Draft script");
        panel.WaitForAssertion(() => Assert.Equal(1, _assistant.Calls)); panel.WaitForAssertion(() => Assert.Contains("Drafting script…", panel.Markup));
        // Cancelling before the mock has published its partial result cannot preserve that result.
        await _assistant.PartialSaved.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.True(panel.Find(".assist-composer-model .model-chip").HasAttribute("disabled")); Assert.True(Controls(panel).Find("#script-operation").HasAttribute("disabled"));
        // The running request opens in its review, which can cancel it.
        await Click(panel, "View request");
        _dialogs.WaitForAssertion(() => Assert.Contains(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Cancel request"), BunitDefaults.WaitTimeout(5));
        await Click(panel, "Cancel request");
        panel.WaitForAssertion(() => Assert.Equal("Draft script", panel.Find(".request-action-button").GetAttribute("aria-label")));
        var run = await LastRun(); Assert.Equal(AssistantRunStatus.Cancelled, run.Status); Assert.Contains("Partial", run.Output);
        Assert.Equal("Keep this instruction", Controls(panel).Find("#script-instructions").GetAttribute("value")); Assert.Empty(_applications);
    }
    [Fact]
    public async Task NavigationDoesNotCancelAndReopeningRestoresTheLockedContext()
    {
        _assistant.Wait = true; var panel = Panel(); Controls(panel).Find("#script-instructions").Input("Keep the rain gentle");
        await Click(panel, "Revise"); panel.WaitForAssertion(() => Assert.Equal(1, _assistant.Calls));
        var id = _assistant.JobId; await panel.Instance.DisposeAsync(); panel.Dispose(); Assert.False(_assistant.Token.IsCancellationRequested);
        var reopened = Panel(); reopened.WaitForAssertion(() => Assert.Equal("Keep the rain gentle", Controls(reopened).Find("#script-instructions").GetAttribute("value")));
        Assert.True(reopened.Find(".assist-composer-model .model-chip").HasAttribute("disabled"));
        _assistant.Release.TrySetResult(); reopened.WaitForAssertion(() => Assert.Contains("Review changes", reopened.Markup));
        Assert.Empty(_dialogs.FindAll(".script-review")); await Click(reopened, "Review latest response"); Reviewed();
        Assert.Equal(id, (await LastRun()).JobId); Assert.Equal(1, _assistant.Calls);
    }
    [Fact]
    public async Task ClosingRequestSuppressesAutomaticReviewWithoutCancellingAndExplicitRouteCanReopen()
    {
        _assistant.Wait = true; var panel = Panel(); await Click(panel, "Revise"); panel.WaitForAssertion(() => Assert.Equal(1, _assistant.Calls));
        await Click(panel, "View request"); _dialogs.WaitForElement(".raw-response"); await Click(panel, "Close");
        Assert.False(_assistant.Token.IsCancellationRequested); _assistant.Release.TrySetResult();
        panel.WaitForAssertion(() => Assert.Contains("Review changes", panel.Markup)); Assert.Empty(_dialogs.FindAll(".script-review"));
        await panel.Instance.DisposeAsync(); panel.Dispose(); _ = Panel(_assistant.JobId); Reviewed(); Assert.Equal(1, _assistant.Calls);
    }
    [Fact]
    public async Task MissingModelBlocksRequestsAndInvalidProposalCannotBeApplied()
    {
        _providers.Models = []; var panel = Panel(); Assert.True(Controls(panel).Find(".request-action-button").HasAttribute("disabled")); Assert.Equal(0, _assistant.Calls);
        await panel.Instance.DisposeAsync(); panel.Dispose(); _providers.Models = null; _assistant.Invalid = true; panel = Panel(); await Click(panel, "Draft replacement"); await Click(panel, "Draft script");
        panel.WaitForAssertion(() => Assert.True(panel.Markup.Contains("Invalid mock output"), _queue.Error ?? panel.Markup), BunitDefaults.WaitTimeout(5)); Assert.Empty(_dialogs.FindAll(".script-review"));
    }
    [Fact]
    public async Task InvalidResponseCanBeDismissedAndReturnsTheActionToIdle()
    {
        _assistant.Invalid = true; var panel = Panel();
        await Click(panel, "Draft replacement"); await Click(panel, "Draft script");
        panel.WaitForAssertion(() => Assert.True(panel.Markup.Contains("Invalid mock output"), _queue.Error ?? panel.Markup), BunitDefaults.WaitTimeout(5));
        Assert.Empty(_dialogs.FindAll(".script-review"));
        await Click(panel, "Review latest response");
        _dialogs.WaitForElement(".script-review-dialog");
        Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Apply changes");
        await Click(panel, "Dismiss");
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".script-review-dialog")));
        Assert.Empty(_applications);
        Assert.True((await LastRun()).Rejected);
        var action = Controls(panel).Find(".request-action-button");
        Assert.False(action.HasAttribute("disabled"));
        Assert.Equal("Draft script", action.GetAttribute("aria-label"));
    }
    [Fact]
    public async Task ReviewSaveFailureRetainsTheDecisionAndRetriesWithoutGeneration()
    {
        var panel = Panel(); await Click(panel, "Draft replacement"); await Click(panel, "Draft script"); Reviewed();
        _history.SaveError = new WorkspaceStoreException("disk full"); await Click(panel, "Replace entire script");
        _dialogs.WaitForAssertion(() => Assert.Contains("disk full", _dialogs.Markup));
        _history.SaveError = null; await Click(panel, "Retry saving history");
        Assert.Equal(1, _assistant.Calls); Assert.True((await LastRun()).Applied); Assert.Single(_applications);
    }
    [Fact]
    public async Task JsonCorrectionRetainsInvalidDraftAndSaveFailureThenRequiresExplicitApply()
    {
        _assistant.Invalid = true; var panel = Panel();
        await Click(panel, "Draft replacement"); await Click(panel, "Draft script");
        panel.WaitForElement(".request-action-button[aria-label='Needs attention']"); await Click(panel, "Review latest response");
        _dialogs.WaitForAssertion(() => Assert.Contains("Invalid JSON at line 1", _dialogs.Markup));
        await Click(panel, "Paste / edit JSON");
        const string invalid = """[{"kind":"Action","sp,ans":[]}]""";
        await panel.InvokeAsync(() => _dialogs.Find("#corrected-script-json").InputAsync(new() { Value = invalid }));
        await Click(panel, "Review corrected JSON");
        _dialogs.WaitForAssertion(() => Assert.Contains("Block 1: missing or invalid", _dialogs.Find("#script-json-error").TextContent));
        Assert.Empty(_applications); Assert.Single((await _history.LoadAsync(_store.Document.ProjectId, _ct)).Runs);
        await Click(panel, "Cancel JSON edit"); await Click(panel, "Paste / edit JSON");
        Assert.Equal(invalid, _dialogs.Find("#corrected-script-json").GetAttribute("value"));
        const string valid = """[{"kind":"Action","spans":[{"text":"A kettle whistles."}]}]""";
        await panel.InvokeAsync(() => _dialogs.Find("#corrected-script-json").InputAsync(new() { Value = valid }));
        _history.SaveError = new WorkspaceStoreException("disk full"); await Click(panel, "Review corrected JSON");
        _dialogs.WaitForAssertion(() => Assert.Contains("disk full", _dialogs.Find("#script-json-error").TextContent));
        Assert.Equal(valid, _dialogs.Find("#corrected-script-json").GetAttribute("value")); Assert.Empty(_applications);
        _history.SaveError = null; await Click(panel, "Review corrected JSON"); Reviewed();
        Assert.Contains("Pasted JSON", _dialogs.Markup); Assert.Empty(_applications); Assert.Equal(1, _assistant.Calls);
        await Click(panel, "Replace entire script"); Assert.Single(_applications);
        var history = (await _history.LoadAsync(_store.Document.ProjectId, _ct)).Runs;
        Assert.Equal(2, history.Count); Assert.True(history.Single(r => r.CorrectedFromRunId is not null).Applied);
        Assert.Equal("Invalid mock output", history.Single(r => r.JobId is not null).Error);
    }
    [Fact]
    public void PreviewEscapesMarkupAndRetainsEmphasis()
    {
        var preview = Render<ScriptPreview>(p => p.Add(c => c.Blocks, new[] { new ScriptBlock { Kind = ScriptBlockKind.Dialogue, Spans = [new("<script>alert(1)</script>", true, true)] } }));
        Assert.Empty(preview.FindAll("script")); Assert.Contains("alert(1)", preview.Find("strong em").TextContent);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.Dispose(disposing); if (disposing && Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
internal sealed class ScriptTestHistory(IAssistantHistoryStore inner) : IAssistantHistoryStore
{
    public Exception? SaveError { get; set; }
    public Task<AssistantHistory> LoadAsync(Guid id, CancellationToken cancellationToken = default) => inner.LoadAsync(id, cancellationToken);
    public Task<AssistantRun> SaveRunAsync(Guid id, AssistantRun run, CancellationToken cancellationToken = default) => SaveError is null
        ? inner.SaveRunAsync(id, run, cancellationToken) : Task.FromException<AssistantRun>(SaveError);
}
internal sealed class ScriptReviewGate : IAiReviewGate
{
    private readonly Guid _tab = Guid.NewGuid(); private readonly HashSet<Guid> _closed = [];
    public Task<Guid> TabIdAsync() => Task.FromResult(_tab);
    public Task<bool> TryOpenAsync(AiJobHeader job, ElementReference origin, bool automatic) => Task.FromResult(!automatic || job.OriginTabId == _tab && !_closed.Contains(job.Id));
    public Task CloseAsync(Guid id) { _closed.Add(id); return Task.CompletedTask; }
}
internal sealed class ScriptQueueHandler : IAiJobHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ScriptAssistant];
    public bool Wait { get; set; } public bool Invalid { get; set; }
    public int Calls { get; private set; } public Guid JobId { get; private set; } public CancellationToken Token { get; private set; }
    public ScriptAssistantRequest? LastRequest { get; private set; }
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PartialSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        LastRequest = snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<ScriptAssistantRequest>();
        JobId = context.Job.Id; Token = ct; Calls++;
        await context.SaveResultAsync(new AiTextJobResult("Partial mock output"));
        PartialSaved.TrySetResult();
        await context.ReportAsync(new(new(GenerationPhase.Generating, "Writing…")));
        if (Wait) await Release.Task.WaitAsync(ct);
        var error = Invalid ? "Invalid mock output" : null;
        var update = new AssistantUpdate(Complete: true, Blocks: Invalid ? null : ScriptFixtures.Document().Blocks, ValidationError: error);
        await context.SaveResultAsync(new AiTextJobResult("Complete mock output", true, "stop", JsonSerializer.SerializeToElement(update, AtomicJsonFile.Options), error));
        return Invalid ? AiJobOutcome.Attention(error!, AiJobRecovery.GenerateAgain) : AiJobOutcome.Complete();
    }
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(AiJobOutcome.Attention("Interrupted mock request", AiJobRecovery.GenerateAgain));
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
}
