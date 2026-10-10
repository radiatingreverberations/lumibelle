using System.Text.Json;
using Bunit;
using lumibelle.Components;
using lumibelle.Components.Script;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

// Uses the repository's existing script-store, queue-handler and review-gate fakes.
// The reprocess path must never invoke that queue handler or the Apply callback itself.
[Trait("Category", "Component")]
public sealed class ScriptEditNormalizationComponentTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.ScriptNormalization", Guid.NewGuid().ToString("N"));
    private readonly FakeScriptStore _store = new();
    private readonly ScriptQueueHandler _assistant = new();
    private readonly List<AssistantRun> _applications = [];
    private readonly ScriptTestHistory _history;
    private readonly AiJobCoordinator _queue;
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;

    public ScriptEditNormalizationComponentTests()
    {
        Services.AddMudServices(); Services.AddSingleton(TimeProvider.System); Services.AddSingleton<MarkdownRenderer>();
        var environment = new StorageTestEnvironment(_root);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(FakeProjectStore.Project("Normalization") with { Id = id }) };
        Directory.CreateDirectory(Path.Combine(_root, "App_Data", "Projects", _store.Document.ProjectId.ToString()));
        var files = new ProjectFiles(Options.Create(new ProjectStorageOptions()), environment, projects);
        var jobs = new FileAiJobStore(environment, TimeProvider.System);
        _history = new(new QueuedAssistantHistoryStore(new(files, new()), jobs));
        var settings = new FakeAiSettingsStore { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
        var providers = new FakeProviders(); var assets = new FakeAssetStore(_store.Document.ProjectId);
        Services.AddSingleton<IAssetStore>(assets);
        Services.AddSingleton<IShotStore>(new FileShotStore(files, TimeProvider.System));
        _queue = new(jobs, settings, [_assistant], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        Services.AddSingleton(_queue); Services.AddSingleton<IAiJobStore>(jobs); Services.AddSingleton<IAiReviewGate>(new ScriptReviewGate());
        Services.AddSingleton(new AiTextJobCapture(settings, projects, assets, new PromptEnhancer(providers, settings, assets), new GuidanceAssistant(providers, settings, assets)));
        Services.AddSingleton<IScriptStore>(_store); Services.AddSingleton<IAssistantHistoryStore>(_history); Services.AddSingleton<ApplicationSession>();
        Services.AddSingleton<IAiProviderRegistry>(providers); Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
        Services.AddSingleton<IAiSettingsStore>(settings);
        JSInterop.Mode = JSRuntimeMode.Loose; _dialogs = Render<MudDialogProvider>();
        _queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task<(IRenderedComponent<ScriptAssistantPanel> Panel, AssistantRun Source)> OpenFailure(bool ambiguous = false, AssistantRunStatus status = AssistantRunStatus.Completed, string? error = null)
    {
        var doc = _store.Document; var block = doc.Blocks[1];
        object[] blocks = [new { kind = "Action", spans = new[] { new { text = "A changed action." } } }];
        var replace = new { kind = "replace", startId = block.Id, blocks };
        var insert = new { kind = "insert", anchorId = block.Id, side = "after", blocks };
        var raw = JsonSerializer.Serialize(new { version = 1, operations = ambiguous ? new object[] { replace, insert, insert } : [replace, insert] });
        var source = new AssistantRun { Status = status, Operation = WritingOperation.Revise, EditFormat = 1,
            Target = ScriptStructure.Capture(doc, ScriptScope.Document, null), Output = raw,
            Error = error ?? "An insertion or move is anchored to another edited block.", SourceRevision = doc.Revision,
            SourceFingerprint = ScriptStructure.ContextFingerprint(doc), Backend = AiBackend.OpenRouter, Model = "test/model",
            SessionId = Services.GetRequiredService<ApplicationSession>().Id };
        source = await _history.SaveRunAsync(doc.ProjectId, source, _ct);
        var panel = Render<ScriptAssistantPanel>(p => p.Add(c => c.ProjectId, doc.ProjectId).Add(c => c.Document, doc)
            .Add(c => c.Selection, new ScriptSelection(block.Id, 0, block.Id, 1))
            .Add(c => c.SelectTarget, scope => Task.FromResult(ScriptAssistantTarget.From(doc, scope, new(block.Id, 0, block.Id, 1))))
            .Add(c => c.Capture, target => Task.FromResult(new ScriptRequestContext(doc.Copy(), target.Capture(doc))))
            .Add(c => c.Apply, run => { _applications.Add(run); return Task.FromResult(true); }));
        panel.WaitForElement("#script-operation");
        await Click(panel, "Requests");
        _dialogs.WaitForElement(".assistant-run");
        await Click(panel, "Open response");
        _dialogs.WaitForElement(".script-review-dialog");
        return (panel, source);
    }
    private async Task Click(IRenderedComponent<ScriptAssistantPanel> panel, string text) =>
        await panel.InvokeAsync(() => panel.FindAll("button").Concat(_dialogs.FindAll("button"))
            .First(b => b.TextContent.Trim() == text).ClickAsync(new()));
    private async Task<AssistantHistory> History() => await _history.LoadAsync(_store.Document.ProjectId, _ct);

    [Fact]
    public async Task ReprocessSavedResponseShowsNoticeAndNeedsExplicitApplyWithoutAModelRequest()
    {
        var before = JsonSerializer.Serialize(_store.Document);
        var (panel, source) = await OpenFailure(); var original = JsonSerializer.Serialize(source);
        await Click(panel, "Reprocess saved response");
        _dialogs.WaitForElement(".script-edit-normalization");
        Assert.Contains("Proposed text and formatting are unchanged", _dialogs.Find(".script-edit-normalization").TextContent);
        Assert.Empty(_applications); Assert.Equal(0, _assistant.Calls); Assert.Equal(before, JsonSerializer.Serialize(_store.Document));
        var history = await History(); Assert.Equal(2, history.Runs.Count);
        Assert.Equal(original, JsonSerializer.Serialize(history.Runs.Single(r => r.Id == source.Id)));
        var corrected = history.Runs.Single(r => r.CorrectedFromRunId == source.Id);
        Assert.Equal(source.Output, corrected.Output); Assert.False(corrected.Applied); Assert.Single(corrected.Edits!.Operations);
        await Click(panel, "Apply changes"); Assert.Single(_applications); Assert.Equal(0, _assistant.Calls);
    }

    [Fact]
    public async Task SaveRetryUsesOriginalResponseWithoutOverwritingAnUnsubmittedJsonDraft()
    {
        var (panel, source) = await OpenFailure();
        await Click(panel, "Paste / edit JSON");
        const string draft = "Keep this unsaved manual correction";
        await panel.InvokeAsync(() => _dialogs.Find("#corrected-script-json").InputAsync(new() { Value = draft }));
        await Click(panel, "Cancel JSON edit");
        _history.SaveError = new WorkspaceStoreException("Disk unavailable");
        await Click(panel, "Reprocess saved response");
        _dialogs.WaitForAssertion(() => Assert.Contains("Disk unavailable", _dialogs.Markup));
        Assert.Single((await History()).Runs); Assert.Empty(_applications); Assert.Equal(0, _assistant.Calls);
        await Click(panel, "Paste / edit JSON"); Assert.Equal(draft, _dialogs.Find("#corrected-script-json").GetAttribute("value"));
        await Click(panel, "Cancel JSON edit"); _history.SaveError = null;
        await Click(panel, "Reprocess saved response"); _dialogs.WaitForElement(".script-edit-normalization");
        var history = await History(); Assert.Equal(2, history.Runs.Count);
        Assert.Equal(source.Output, history.Runs.Single(r => r.CorrectedFromRunId == source.Id).Output);
        Assert.Equal(0, _assistant.Calls); Assert.Empty(_applications);
    }

    [Fact]
    public async Task CompetingInsertionsRemainAnErrorWithoutCreatingAPartialProposal()
    {
        var before = JsonSerializer.Serialize(_store.Document);
        var (panel, source) = await OpenFailure(ambiguous: true);
        await Click(panel, "Reprocess saved response");
        _dialogs.WaitForAssertion(() => Assert.Contains("Multiple edits use the same insertion point", _dialogs.Markup));
        Assert.Single((await History()).Runs); Assert.Equal(source.Output, (await History()).Runs[0].Output);
        Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Apply changes");
        Assert.Equal(before, JsonSerializer.Serialize(_store.Document)); Assert.Equal(0, _assistant.Calls); Assert.Empty(_applications);
    }

    [Fact]
    public async Task ReprocessingDoesNotRetargetAnOldResponseToTheEditedScript()
    {
        var (panel, _) = await OpenFailure();
        var changed = _store.Document.Copy(); changed.Blocks[1] = changed.Blocks[1] with { Spans = [new("Edited after the request.")] };
        _store.Document = changed; panel.Render(p => p.Add(c => c.Document, changed));
        await Click(panel, "Reprocess saved response"); _dialogs.WaitForElement(".script-edit-normalization");
        Assert.True(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
        Assert.Contains("target changed", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_applications); Assert.Equal(0, _assistant.Calls);
    }

    [Theory]
    [InlineData(AssistantRunStatus.Cancelled)] [InlineData(AssistantRunStatus.Interrupted)] [InlineData(AssistantRunStatus.Failed)]
    public async Task IncompleteRequestsDoNotOfferAutomaticReprocessing(AssistantRunStatus status)
    {
        _ = await OpenFailure(status: status);
        Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Reprocess saved response");
        Assert.Empty(_applications); Assert.Equal(0, _assistant.Calls);
    }

    [Fact]
    public async Task OutputLimitFailureIsNotTurnedIntoACompleteProposalByReprocessing()
    {
        _ = await OpenFailure(error: "The response did not finish normally. Inspect it and retry; incomplete output cannot be applied.");
        Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Reprocess saved response");
        Assert.Single((await History()).Runs); Assert.Empty(_applications); Assert.Equal(0, _assistant.Calls);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
