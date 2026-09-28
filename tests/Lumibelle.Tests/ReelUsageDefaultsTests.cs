using System.Text.Json;
using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed class ReelUsageDefaultsTests
{
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;
    [Fact]
    public void OlderAssetsRetainKeyframeDefaultWithoutSerializingNewMetadata()
    {
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        Assert.Equal(ReelVisuals.Keyframes, ReelUsageDefaults.Initial(asset));
        Assert.DoesNotContain("defaultReelVisuals", JsonSerializer.Serialize(asset, AtomicJsonFile.Options));
        var restored = ShotCopy.Of(asset); Assert.Null(restored.DefaultReelVisuals);
    }
    [Theory] [InlineData(ReelVisuals.Keyframes)] [InlineData(ReelVisuals.RefMod)] [InlineData(ReelVisuals.FullReel)]
    public async Task AssetDefaultPersistsAndNeverChangesAnExistingShot(ReelVisuals mode)
    {
        using var f = new RefModOnDemandFixture(); var shots = JsonSerializer.Serialize(f.Shot, AtomicJsonFile.Options);
        var store = new FileAssetStore(f.Files, TimeProvider.System);
        var library = await store.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [f.Owner with { DefaultReelVisuals = mode }] }, 0, _ct);
        var reopened = await new FileAssetStore(f.Files, TimeProvider.System).LoadAsync(f.Project.Id, _ct);
        Assert.Equal(mode, Assert.Single(reopened.Assets).DefaultReelVisuals);
        Assert.Equal(mode, ReelUsageDefaults.Initial(reopened.Assets[0]));
        Assert.Equal(shots, JsonSerializer.Serialize(f.Shot, AtomicJsonFile.Options));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(library, 0, _ct));
    }
    [Theory] [InlineData(ReelVisuals.None)] [InlineData((ReelVisuals)99)]
    public async Task InvalidDefaultsAreRejectedWithoutPublishing(ReelVisuals mode)
    {
        using var f = new RefModOnDemandFixture();
        var asset = f.Owner with { DefaultReelVisuals = mode };
        Assert.False(ReelUsageDefaults.Valid(mode));
        Assert.Throws<WorkspaceStoreException>(() => ReelUsageDefaults.Initial(asset));
        var store = new FileAssetStore(f.Files, TimeProvider.System);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct));
        Assert.Empty((await store.LoadAsync(f.Project.Id, _ct)).Assets);
    }
}

[Trait("Category", "Component")]
public sealed class RefModOnDemandComponentTests : BunitContext
{
    private readonly RefModOnDemandFixture _fixture = new();
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;
    public RefModOnDemandComponentTests()
    {
        Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IReferenceVideoStore>(_fixture.Media);
        Services.AddSingleton<IAiSettingsStore>(_fixture.Settings);
        Services.AddSingleton(_fixture.Preparation);
        // No queue, HTTP client or RefMod builder is registered for these tests.
    }
    private IRenderedComponent<ShotReferenceEditor> Editor(Action<ReferenceSelection>? applied = null, Action? cancelled = null) =>
        Render<ShotReferenceEditor>(p => p.Add(c => c.Library, _fixture.Library).Add(c => c.Shot, _fixture.Shot)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true)
            .Add(c => c.Expanded, _fixture.Shot.Videos.FirstOrDefault()?.Id)
            .Add(c => c.ReferencesApplied, applied ?? (_ => { })).Add(c => c.Cancelled, cancelled ?? (() => { })));
    private static Shot Draft(IRenderedComponent<ShotReferenceEditor> view) => view.FindComponent<CharacterVoiceEditor>().Instance.Shot;
    private static Task Click(IRenderedComponent<ShotReferenceEditor> view, string text) => view.InvokeAsync(() =>
        view.FindAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new()));

    [Fact]
    public async Task SelectionAppliesWithNoManualRefModBuildControls()
    {
        ReferenceSelection? saved = null; var before = H3Policy.Fingerprint(_fixture.Shot); var view = Editor(s => saved = s);
        Assert.Empty(view.FindComponents<ReelRefModBuilder>());
        Assert.DoesNotContain(view.FindAll("button"), b => b.TextContent.Contains("Build Full") || b.TextContent.Contains("Prepare previews") || b.TextContent.Contains("Use built"));
        Assert.Equal(3, view.FindAll(".refmod-automatic .selected-keyframes > div").Count);
        await Click(view, "Apply changes"); view.WaitForAssertion(() => Assert.NotNull(saved));
        Assert.NotNull(Assert.Single(saved!.Inputs.Videos).RefMod); Assert.Equal(before, H3Policy.Fingerprint(_fixture.Shot));
        Assert.Equal(3, _fixture.Media.Reads);
    }
    [Fact]
    public async Task ParentSaveFailureKeepsPreparedInputsForRetry()
    {
        var fail = true; ReferenceSelection? saved = null;
        var view = Editor(s => { if (fail) throw new IOException("Disk full"); saved = s; });
        await Click(view, "Apply changes"); Assert.Null(saved); Assert.Contains("Disk full", view.Markup);
        var accepted = Draft(view).Videos[0].RefMod; Assert.NotNull(accepted); var reads = _fixture.Media.Reads;
        fail = false; _fixture.Media.FailReads = true;
        await Click(view, "Apply changes"); view.WaitForAssertion(() => Assert.NotNull(saved));
        Assert.Equal(reads, _fixture.Media.Reads); Assert.Equal(accepted!.Recipe.Key, saved!.Inputs.Videos[0].RefMod!.Recipe.Key);
    }
    [Fact]
    public async Task SourceFailureLeavesModeAndFramesAvailableForRetry()
    {
        _fixture.Media.FailReads = true; ReferenceSelection? saved = null; var view = Editor(s => saved = s);
        await Click(view, "Apply changes"); Assert.Null(saved); Assert.Contains("Source frame unavailable", view.Markup);
        Assert.Equal(ReelVisuals.RefMod, Draft(view).Videos[0].EffectiveVisuals); Assert.Null(Draft(view).Videos[0].RefMod);
        _fixture.Media.FailReads = false; await Click(view, "Apply changes"); Assert.NotNull(saved);
    }
    [Theory] [InlineData(ReelVisuals.Keyframes)] [InlineData(ReelVisuals.RefMod)] [InlineData(ReelVisuals.FullReel)]
    public async Task NewSelectionsUseAssetDefaultButLocalOverrideDoesNotChangeIt(ReelVisuals mode)
    {
        _fixture.Library.Assets[0] = _fixture.Owner with { DefaultReelVisuals = mode };
        _fixture.Shot.Videos.Clear(); var view = Editor();
        await view.InvokeAsync(() => view.Find($"[data-reel-id='{_fixture.Reel.Id}'] .add-reel").ClickAsync(new()));
        Assert.Equal(mode, Assert.Single(Draft(view).Videos).EffectiveVisuals);
        await view.InvokeAsync(() => view.Find("select[aria-label='Visuals']").ChangeAsync(new() { Value = "Keyframes" }));
        Assert.Equal(ReelVisuals.Keyframes, Draft(view).Videos[0].EffectiveVisuals);
        Assert.Equal(mode, _fixture.Owner.DefaultReelVisuals);
        await Click(view, "Use asset default"); Assert.Equal(mode, Draft(view).Videos[0].EffectiveVisuals);
        Assert.Empty(_fixture.Shot.Videos);
    }
    [Fact]
    public async Task FullReelDefaultDoesNotExtractKeyframesWhileAdding()
    {
        _fixture.Library.Assets[0] = _fixture.Owner with { DefaultReelVisuals = ReelVisuals.FullReel };
        _fixture.Library.Reels[0] = _fixture.Reel with { Keyframes = null };
        _fixture.Shot.Videos.Clear(); var view = Editor();
        await view.InvokeAsync(() => view.Find($"[data-reel-id='{_fixture.Reel.Id}'] .add-reel").ClickAsync(new()));
        Assert.Equal(ReelVisuals.FullReel, Assert.Single(Draft(view).Videos).EffectiveVisuals);
        Assert.Equal(0, _fixture.Media.Suggestions); Assert.Equal(0, _fixture.Media.Reads);
    }
    [Fact]
    public void NewDefaultDoesNotReinterpretAnExistingSelectionOnOpen()
    {
        _fixture.Library.Assets[0] = _fixture.Owner with { DefaultReelVisuals = ReelVisuals.FullReel };
        _fixture.Shot.Videos[0].Visuals = ReelVisuals.Keyframes; var before = H3Policy.Fingerprint(_fixture.Shot);
        var view = Editor(); Assert.Equal(ReelVisuals.Keyframes, Draft(view).Videos[0].EffectiveVisuals);
        Assert.Equal(before, H3Policy.Fingerprint(_fixture.Shot)); Assert.Equal(0, _fixture.Media.Reads);
    }
    [Fact]
    public async Task CancellingLocalPreparationNeverPublishesReferences()
    {
        _fixture.Media.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false; var cancelled = false; var view = Editor(_ => applied = true, () => cancelled = true);
        var apply = Click(view, "Apply changes");
        await _fixture.Media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        await Click(view, "Cancel"); await apply;
        Assert.True(cancelled); Assert.False(applied); Assert.Null(_fixture.Shot.Videos[0].RefMod);
    }
    [Theory] [InlineData(ReelVisuals.Keyframes)] [InlineData(ReelVisuals.RefMod)] [InlineData(ReelVisuals.FullReel)]
    public async Task ReelAuthoringInheritsVisualDefaultAndDoesNotImportAudio(ReelVisuals mode)
    {
        _fixture.Library.Assets[0] = _fixture.Owner with { DefaultReelVisuals = mode };
        _fixture.Shot.Videos.Clear(); Shot? saved = null;
        var view = Render<ShotReferenceEditor>(p => p.Add(c => c.Library, _fixture.Library).Add(c => c.Shot, _fixture.Shot)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true).Add(c => c.ReelAuthoring, true)
            .Add(c => c.Applied, (Shot s) => saved = s));
        await view.InvokeAsync(() => view.Find($"[data-reel-id='{_fixture.Reel.Id}'] .add-reel").ClickAsync(new()));
        Assert.Equal(mode.ToString(), view.Find("select[aria-label='Visuals']").GetAttribute("value"));
        Assert.DoesNotContain(view.FindAll("select[aria-label='Visuals'] option"), o => o.GetAttribute("value") == "None");
        await view.InvokeAsync(() => view.Find("select[aria-label='Visuals']").ChangeAsync(new() { Value = "Keyframes" }));
        await Click(view, "Use asset default");
        await Click(view, "Apply changes"); Assert.NotNull(saved);
        Assert.Equal(mode, saved!.Videos[0].EffectiveVisuals);
        Assert.Equal(mode == ReelVisuals.RefMod, saved.Videos[0].RefMod is not null);
        Assert.Equal(mode == ReelVisuals.RefMod ? 3 : 0, _fixture.Media.Reads);
        Assert.False(saved.Videos[0].UseSoundtrack); Assert.Empty(saved.Voices);
        Assert.Null(saved.CharacterVoices); Assert.Equal(mode, _fixture.Owner.DefaultReelVisuals);
        Assert.Empty(_fixture.Shot.Videos);
    }

    [Fact]
    public async Task ReelAuthoringOverrideIsLocalAndReopeningRetainsIt()
    {
        _fixture.Library.Assets[0] = _fixture.Owner with { DefaultReelVisuals = ReelVisuals.Keyframes };
        Shot? saved = null;
        var source = await _fixture.Preparation.CaptureAsync(_fixture.Project.Id, _fixture.Shot, _ct);
        var baseline = H3Policy.Fingerprint(source);
        IRenderedComponent<ShotReferenceEditor> Open() => Render<ShotReferenceEditor>(p => p.Add(c => c.Library, _fixture.Library)
            .Add(c => c.Shot, source).Add(c => c.Expanded, source.Videos[0].Id)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true).Add(c => c.ReelAuthoring, true)
            .Add(c => c.Applied, (Shot s) => saved = s));
        var cancelled = Open();
        await cancelled.InvokeAsync(() => cancelled.Find("select[aria-label='Visuals']").ChangeAsync(new() { Value = "FullReel" }));
        await Click(cancelled, "Cancel"); Assert.Null(saved); Assert.Equal(baseline, H3Policy.Fingerprint(source));
        cancelled.Dispose();
        var reopened = Open();
        Assert.Equal("RefMod", reopened.Find("select[aria-label='Visuals']").GetAttribute("value"));
        await reopened.InvokeAsync(() => reopened.Find("select[aria-label='Visuals']").ChangeAsync(new() { Value = "FullReel" }));
        await Click(reopened, "Apply changes");
        Assert.Equal(ReelVisuals.FullReel, Assert.Single(saved!.Videos).EffectiveVisuals);
        Assert.Equal(ReelVisuals.Keyframes, _fixture.Owner.DefaultReelVisuals);
        Assert.Equal(baseline, H3Policy.Fingerprint(source));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _fixture.Dispose();
    }
}

[Trait("Category", "Component")]
public sealed class ReelUsageDefaultSelectTests : BunitContext
{
    [Fact]
    public void ControlShowsThreeModesAndKeepsAssetImmutable()
    {
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var changes = new List<ReelVisuals?>();
        var view = Render<ReelUsageDefaultSelect>(p => p.Add(c => c.Asset, asset).Add(c => c.Changed, (ReelVisuals? mode) => changes.Add(mode)));
        Assert.Equal(new[] { "Keyframes", "RefMod", "FullReel" }, view.FindAll("option").Select(o => o.GetAttribute("value")));
        view.Find("select").Change("RefMod"); view.Find("select").Change("FullReel"); view.Find("select").Change("Keyframes");
        Assert.Equal(new ReelVisuals?[] { ReelVisuals.RefMod, ReelVisuals.FullReel, null }, changes);
        Assert.Null(asset.DefaultReelVisuals);
        view.Find("select").Change("None"); Assert.Equal(3, changes.Count);
    }
}
