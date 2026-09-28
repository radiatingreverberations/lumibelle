using System.Text.Json;
using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class AutomaticReelVoiceComponentTests : BunitContext
{
    public AutomaticReelVoiceComponentTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IReferenceVideoStore>(new ReferenceEditorMediaFake());
        Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore());
        ComponentFactories.AddStub<ReelRefModBuilder>(); // No GPU/queue in picker interaction tests.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
    private static string Saved(Shot shot) => JsonSerializer.Serialize(shot, AtomicJsonFile.Options);
    private IRenderedComponent<ShotReferenceEditor> Editor(AutomaticReelVoiceFixture f, Action<ReferenceSelection>? save = null,
        Action? cancel = null) => Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, f.Shot).Add(c => c.Library, f.Library)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true).Add(c => c.Expanded, (Guid?)f.Binding.Id)
            .Add(c => c.ReferencesApplied, save ?? (_ => { })).Add(c => c.Cancelled, cancel ?? (() => { })));
    private static void Click(IRenderedComponent<ShotReferenceEditor> view, string text) =>
        view.FindAll("button").Single(b => b.TextContent.Trim() == text).Click();
    private static Shot Draft(IRenderedComponent<ShotReferenceEditor> view) => view.FindComponent<CharacterVoiceEditor>().Instance.Shot;

    [Fact]
    public void PickingReelStagesOriginalVoiceAndPublishesBothOnlyOnApply()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.Shot.Videos.Clear(); ReferenceSelection? saved = null;
        var before = Saved(f.Shot); var view = Editor(f, s => saved = s);
        view.Find($"[data-reel-id='{f.Reel.Id}'] .add-reel").Click();
        var draft = Draft(view); var selected = Assert.Single(draft.Voices);
        Assert.Equal(f.Recording.Id, selected.VoiceId); Assert.Equal(2, selected.Start); Assert.Equal(4, selected.Duration);
        Assert.Contains("original recording", view.Find(".reel-voice-notice").TextContent);
        Assert.Contains("<Audio 1>", view.Find(".video-reference-list .reference-summary").TextContent);
        Assert.Equal(before, Saved(f.Shot)); Assert.Null(saved);
        Click(view, "Apply changes"); Assert.NotNull(saved);
        Assert.Equal(f.Recording.Id, Assert.Single(saved!.Inputs.Voices).VoiceId);
        Assert.False(Assert.Single(saved.Inputs.Videos).UseSoundtrack);
        Assert.Equal(before, Saved(f.Shot));
    }

    [Fact]
    public void SwitchingAnExistingReelToRefModStagesItsVoiceWithoutChangingTheSourceShot()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.UseRefMod(); f.Binding.Visuals = ReelVisuals.Keyframes;
        var before = Saved(f.Shot); ReferenceSelection? saved = null; var view = Editor(f, s => saved = s);
        Assert.Empty(Draft(view).Voices); Assert.Equal(before, Saved(f.Shot));
        view.Find("select[aria-label='Visuals']").Change("RefMod");
        Assert.Equal(f.Recording.Id, Assert.Single(Draft(view).Voices).VoiceId);
        Click(view, "Apply changes");
        Assert.Equal(ReelVisuals.RefMod, Assert.Single(saved!.Inputs.Videos).EffectiveVisuals);
        Assert.Equal(f.Recording.Id, Assert.Single(saved.Inputs.Voices).VoiceId);
        Assert.Equal(before, Saved(f.Shot));
    }

    [Fact]
    public void ExplicitNoneSurvivesRefModToggleAndReopening()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.UseRefMod(); f.Binding.Visuals = ReelVisuals.Keyframes;
        CharacterVoices.Set(f.Shot, new() { AssetId = f.Character.Id, CharacterName = f.Character.Name,
            Source = CharacterVoiceSource.None }, f.Library);
        var view = Editor(f);
        view.Find("select[aria-label='Visuals']").Change("RefMod");
        view.Find("select[aria-label='Visuals']").Change("Keyframes");
        view.Find("select[aria-label='Visuals']").Change("RefMod");
        Assert.Empty(ResolvedReferences.For(Draft(view)).Audio);
        Assert.Equal(CharacterVoiceSource.None, Assert.Single(Draft(view).CharacterVoices!).Source);
        var reopened = Editor(f); Assert.Empty(ResolvedReferences.For(Draft(reopened)).Audio);
    }

    [Fact]
    public void CancelDiscardsAutomaticAudioAndVisualChangesTogether()
    {
        var f = AutomaticReelVoiceFixture.Create(ReelVoiceMode.NewVoice); f.Shot.Videos.Clear();
        var before = Saved(f.Shot); var cancelled = false; var saved = false;
        var view = Editor(f, _ => saved = true, () => cancelled = true);
        view.Find($"[data-reel-id='{f.Reel.Id}'] .add-reel").Click();
        Assert.Single(ResolvedReferences.For(Draft(view)).Audio);
        Click(view, "Cancel"); Assert.True(cancelled); Assert.False(saved); Assert.Equal(before, Saved(f.Shot));
    }

    [Fact]
    public void MissingOriginalStopsApplyRatherThanAttachingGeneratedAudio()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.Shot.Videos.Clear(); f.Library.Voices.Clear();
        var saved = false; var view = Editor(f, _ => saved = true);
        view.Find($"[data-reel-id='{f.Reel.Id}'] .add-reel").Click();
        Click(view, "Apply changes");
        Assert.False(saved); Assert.Contains(view.FindAll("[role='alert']"), n => n.TextContent.Contains("Restore the unavailable recording"));
        Assert.Equal(f.Recording.Id, Assert.Single(Draft(view).Voices).VoiceId);
        Assert.False(Assert.Single(Draft(view).Videos).UseSoundtrack);
    }

    [Fact]
    public void PickingDoesNotLetDefaultInitializationOverwriteALegacySpeaker()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.Shot.Videos.Clear();
        f.Library.Assets[0] = f.Character with { DefaultVoiceId = f.Recording.Id };
        var legacy = new ShotVoiceBinding { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Start = 1, Duration = 2 };
        f.Shot.Voices.Add(legacy);
        var view = Editor(f);
        view.Find($"[data-reel-id='{f.Reel.Id}'] .add-reel").Click();
        Assert.Equal(legacy, Assert.Single(Draft(view).Voices));
        Assert.Null(Draft(view).CharacterVoices);
    }

    [Fact]
    public void OpeningAlreadySelectedRefModDoesNotRewriteSavedReferences()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.UseRefMod();
        var before = Saved(f.Shot); var view = Editor(f);
        Assert.Equal(before, Saved(f.Shot)); Assert.Empty(Draft(view).Voices);
        Assert.Empty(view.FindAll(".reel-voice-notice"));
    }

    [Fact]
    public void ReelAuthoringPicturePickerNeverImportsAudio()
    {
        var f = AutomaticReelVoiceFixture.Create(); f.Shot.Videos.Clear(); Shot? saved = null;
        var view = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, f.Shot).Add(c => c.Library, f.Library)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true).Add(c => c.ReelAuthoring, true)
            .Add(c => c.Applied, (Shot shot) => saved = shot));
        view.Find($"[data-reel-id='{f.Reel.Id}'] .add-reel").Click(); Click(view, "Apply changes");
        Assert.NotNull(saved); Assert.Empty(saved!.Voices); Assert.Null(saved.CharacterVoices);
        Assert.False(Assert.Single(saved.Videos).UseSoundtrack); Assert.Empty(view.FindAll(".character-voice-editor"));
    }
}
