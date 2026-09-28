using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class AiTextRepairActionTests : BunitContext, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.RepairActionTests", Guid.NewGuid().ToString("N"));
    private readonly FileAiJobStore _store;
    private readonly AiJobCoordinator _queue;
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;
    public AiTextRepairActionTests()
    {
        Services.AddMudServices(); Services.AddSingleton(TimeProvider.System); JSInterop.Mode = JSRuntimeMode.Loose;
        _store = new(new StorageTestEnvironment(_root), TimeProvider.System);
        _queue = new(_store, new FakeAiSettingsStore(), [], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        Services.AddSingleton<IAiJobStore>(_store); Services.AddSingleton(_queue);
        // Do not render until each test has registered its repair service/review gate.
    }
    private Task<AiJobHeader> Add(string label, Guid? project = null) => _store.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant,
        AiBackend.ComfyUI, new(project ?? Guid.NewGuid()), "QA project", label, Guid.NewGuid(), new { prompt = "Captured" }), _ct);
    void IDisposable.Dispose()
    {
        _queue.Dispose(); base.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class RepairActionService(AiTextRepairOffer offer) : IAiTextRepairService
    {
        public int Captures, Connections;
        public bool RejectConnection;
        public Task<AiTextRepairOffer?> InspectAsync(Guid id, CancellationToken ct = default) => Task.FromResult<AiTextRepairOffer?>(offer);
        public Task<AiJobSubmission> CaptureAsync(Guid sourceId, Guid id, Guid tab, Guid? previousRepairId = null, CancellationToken ct = default)
        { Captures++; throw new WorkspaceStoreException("Saved response changed. No repair was submitted."); }
        public Task ConnectReviewAsync(Guid id, CancellationToken ct = default)
        { Connections++; if (RejectConnection) throw new WorkspaceStoreException("The target changed; your edits are preserved."); return Task.CompletedTask; }
    }
    private void AddRepairServices(RepairActionService service)
    {
        Services.AddSingleton<IAiTextRepairService>(service);
        Services.AddSingleton(AiTextRepairTests.Proxy<IAiReviewGate>((method, _) => method.Name switch {
            nameof(IAiReviewGate.TabIdAsync) => Task.FromResult(Guid.NewGuid()),
            nameof(IAiReviewGate.TryOpenAsync) => Task.FromResult(false),
            _ => Task.CompletedTask
        }));
    }
    private async Task<AiJobHeader> RepairSource()
    {
        var added = await Add("Invalid prompt");
        var failed = await _store.UpdateAsync(added.Id, j => j with { State = AiJobState.NeedsAttention,
            Recovery = AiJobRecovery.GenerateAgain, Error = "Missing heading", FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        await _queue.RefreshAsync(_ct); return failed;
    }
    [Fact]
    public async Task RepairButtonRenderingNeverSubmitsOrChangesTheReview()
    {
        var source = await RepairSource(); var service = new RepairActionService(new(source, "Captured model", null)); AddRepairServices(service);
        var view = Render<AiTextRepairAction>(p => p.Add(c => c.JobId, source.Id));
        view.WaitForAssertion(() => Assert.Contains("Fix validation", view.Markup));
        Assert.Contains("no images, audio or video", view.Markup); Assert.Contains("Normal provider charges", view.Markup);
        Assert.Equal(0, service.Captures); Assert.Equal(0, service.Connections);
        Assert.Single((await _store.ReadAsync(_ct)).Jobs);
    }
    [Fact]
    public async Task StaleRepairClickShowsTheErrorWithoutCreatingARequest()
    {
        var source = await RepairSource(); var service = new RepairActionService(new(source, "Captured model", null)); AddRepairServices(service);
        var view = Render<AiTextRepairAction>(p => p.Add(c => c.JobId, source.Id));
        await view.InvokeAsync(() => view.Find("button").ClickAsync(new()));
        view.WaitForAssertion(() => Assert.Contains("No repair was submitted", view.Markup));
        Assert.Equal(1, service.Captures); Assert.Equal(0, service.Connections);
        Assert.Single((await _store.ReadAsync(_ct)).Jobs);
    }
    [Fact]
    public async Task CompletedRepairIsShownInsteadOfSubmittingAnotherOne()
    {
        var source = await RepairSource(); var child = await Add("Fix validation", source.Target.ProjectId);
        child = await _store.UpdateAsync(child.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        await _store.WriteArtifactAsync(child.Id, AiJobArtifact.Result, new AiTextJobResult("Corrected saved text", true, "stop"), _ct);
        await _queue.RefreshAsync(_ct);
        var service = new RepairActionService(new(source, "Captured model", null, child)); AddRepairServices(service);
        var view = Render<AiTextRepairAction>(p => p.Add(c => c.JobId, source.Id));
        view.WaitForAssertion(() => Assert.Contains("Corrected saved text", view.Markup));
        Assert.Contains("Review repair", view.Markup);
        Assert.DoesNotContain(view.FindAll("button"), b => b.TextContent.Trim() == "Fix validation");
        Assert.Equal(0, service.Captures); Assert.Equal(0, service.Connections);
    }
    [Fact]
    public async Task ChangedTargetBlocksRepairNavigationWithoutDiscardingTheResult()
    {
        var source = await RepairSource(); var child = await Add("Fix validation", source.Target.ProjectId);
        child = await _store.UpdateAsync(child.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct);
        await _store.WriteArtifactAsync(child.Id, AiJobArtifact.Result, new AiTextJobResult("Retained correction", true, "stop"), _ct);
        await _queue.RefreshAsync(_ct);
        var service = new RepairActionService(new(source, "Captured model", null, child)) { RejectConnection = true }; AddRepairServices(service);
        var navigation = Services.GetRequiredService<NavigationManager>(); var before = navigation.Uri;
        var view = Render<AiTextRepairAction>(p => p.Add(c => c.JobId, source.Id));
        view.WaitForAssertion(() => Assert.Contains("Review repair", view.Markup));
        await view.InvokeAsync(() => view.FindAll("button").Single(b => b.TextContent.Trim() == "Review repair").ClickAsync(new()));
        Assert.Equal(before, navigation.Uri); Assert.Equal(0, service.Captures); Assert.Equal(1, service.Connections);
        view.WaitForAssertion(() => Assert.Contains("your edits are preserved", view.Markup));
        Assert.Equal("Retained correction", (await _store.ReadArtifactAsync<AiTextJobResult>(child.Id, AiJobArtifact.Result, _ct))!.Raw);
    }
}
