using Bunit;
using Bunit.TestDoubles;
using lumibelle.Components.Shots;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class UnifiedReferenceEditorTests : BunitContext
{
    public UnifiedReferenceEditorTests() { Services.AddMudServices(); Services.AddSingleton<IReferenceVideoStore>(new ReferenceEditorMediaFake()); Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore()); JSInterop.Mode = JSRuntimeMode.Loose; }
    private static AssetLibrary Library()
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character,
            Images = [new() { Id = Guid.NewGuid(), Name = "Face", FileName = "face.png", ContentType = "image/png", Width = 32, Height = 32 }] };
        AssetReferenceReel Reel(string name, bool audio) => new() { AssetId = owner.Id, Name = name, UseGuidance = "Intended " + name,
            Media = new(Guid.NewGuid(), new('a', 64), 1000, 160, 96, 120, 24, 5, audio), CreatedUtc = DateTimeOffset.UtcNow };
        return new() { ProjectId = Guid.NewGuid(), Assets = [owner], Reels = [Reel("Angles", true), Reel("Silent", false)] };
    }
    private IRenderedComponent<ShotReferenceEditor> Editor(AssetLibrary library, Shot shot, Action<ReferenceSelection> save) =>
        Render<ShotReferenceEditor>(p => p.Add(c => c.Library, library).Add(c => c.Shot, shot).Add(c => c.AllowInference, true)
            .Add(c => c.AllowReels, true).Add(c => c.ReferencesApplied, save));
    private static void Click(IRenderedComponent<ShotReferenceEditor> e, string text) => e.FindAll("button").Single(b => b.TextContent.Trim() == text).Click();
    [Fact]
    public async Task UnifiedDraftRetainsBothMediaThroughFailureAndOnlyPublishesOnApply()
    {
        var library = Library(); var shot = new Shot { Duration = 5 }; ReferenceSelection? saved = null; var fail = true;
        var e = Editor(library, shot, result => { if (fail) throw new IOException("Disk unavailable"); saved = result; });
        ComponentFactories.AddStub<ImageInputCropDialog>();
        await e.Find("[data-reference]").ClickAsync(new());
        await e.Find(".reference-crop-button").ClickAsync(new());
        var crop = e.FindComponent<Stub<ImageInputCropDialog>>().Instance.Parameters;
        await e.InvokeAsync(() => crop.Get(c => c.Applied).InvokeAsync(new ImageCropRegion { X = 0, Y = 0, Width = .5, Height = 1 }));
        e.Find($"[data-reel-id='{library.Reels[0].Id}'] .add-reel").Click();
        Assert.False(e.Find(".video-reference-list fieldset").HasAttribute("disabled"));
        e.Find(".video-reference-list textarea").Input("Use only the profile and voice.");
        Click(e, "Apply changes");
        Assert.Contains("Disk unavailable", e.Find("[role=alert]").TextContent);
        Assert.Empty(shot.Images); Assert.Empty(shot.Videos); Assert.Null(saved);
        fail = false; Click(e, "Apply changes");
        Assert.Equal(.5, Assert.Single(saved!.Inputs.Images).Crop!.Width);
        var video = Assert.Single(saved.Inputs.Videos); Assert.Equal(library.Reels[0].Media, video.Media);
        Assert.Equal("Use only the profile and voice.", video.Description); Assert.False(video.UseSoundtrack);
        Assert.Equal(CharacterVoiceSource.None, Assert.Single(saved.Inputs.CharacterVoices!).Source);
        Assert.Equal(library.Reels[0].Id, saved.ReelSources[video.Id]);
        Assert.Empty(shot.Images); Assert.Empty(shot.Videos);
    }
    [Fact]
    public void PreviewNeverAddsAndReplacementKeepsPositionButResetsGuidanceAndSpeaker()
    {
        var library = Library(); var original = new ShotVideoBinding { Media = library.Reels[0].Media, Name = "Legacy", Description = "old", UseSoundtrack = true, Speaker = "Riley" };
        var shot = new Shot { Duration = 5, Videos = [original], Dialogue = [new() { Speaker = "Riley", Text = "Hello" }] }; ReferenceSelection? saved = null;
        var e = Editor(library, shot, s => saved = s);
        e.Find($"[data-reel-id='{library.Reels[1].Id}'] .reel-preview-button").Click();
        Assert.Single(e.FindAll(".video-reference-list li")); Assert.Single(e.FindAll(".picker-grid video"));
        e.Find(".video-reference-list .reference-settings-button").Click(); Click(e, "Replace reel");
        Assert.Empty(e.FindAll("[data-reference]"));
        Assert.Equal("Reels", e.Find(".media-filters [aria-pressed=true]").TextContent);
        e.Find($"[data-reel-id='{library.Reels[1].Id}'] .add-reel").Click(); Click(e, "Apply changes");
        var video = Assert.Single(saved!.Inputs.Videos); Assert.Equal(original.Id, video.Id);
        Assert.Equal(library.Reels[1].Media, video.Media); Assert.Equal(library.Reels[1].UseGuidance, video.Description);
        Assert.False(video.UseSoundtrack); Assert.Null(video.Speaker);
    }
    [Fact]
    public void LegacyClipsAndIndependentNumberingSurviveWithoutLibraryReels()
    {
        var library = Library(); var first = library.Reels[0]; var second = library.Reels[1]; library.Reels.Clear();
        var shot = new Shot { Duration = 5, Videos = [new() { Media = first.Media, Name = "Old clip", UseSoundtrack = true }, new() { Media = second.Media, Name = "Quiet" }] };
        ReferenceSelection? saved = null; var e = Editor(library, shot, s => saved = s);
        e.Find("[aria-label='Move Video 2 up']").Click(); Click(e, "Apply changes");
        Assert.Equal(second.Media.Id, saved!.Inputs.Videos[0].Media.Id); Assert.Equal(first.Media.Id, saved.Inputs.Videos[1].Media.Id);
        Assert.Contains("<Video 2>", e.FindAll(".video-reference-list li")[1].TextContent);
        Assert.Contains("<Audio 1>", e.FindAll(".video-reference-list li")[1].TextContent);
        Assert.Empty(saved.ReelSources);
    }
    [Fact]
    public void DuplicateMediaIsDisabledAndAudioLimitsRetainTheDraft()
    {
        var library = Library(); var shot = new Shot { Duration = 5, Voices = Enumerable.Range(0, 3).Select(n => new ShotVoiceBinding { AssetId = library.Assets[0].Id, VoiceId = Guid.NewGuid(), Speaker = "Speaker " + n, Duration = 1 }).ToList() };
        var saved = false; var e = Editor(library, shot, _ => saved = true);
        e.Find($"[data-reel-id='{library.Reels[0].Id}'] .add-reel").Click();
        Assert.True(e.Find($"[data-reel-id='{library.Reels[0].Id}'] .add-reel").HasAttribute("disabled"));
        e.Find(".soundtrack-choice input").Change(true); // Explicitly retaining competing legacy inputs is still allowed for review.
        Click(e, "Apply changes"); Assert.False(saved); Assert.Contains("Use up to three audio references, counting reel soundtracks; this shot has 4.", e.Find("[role=alert]").TextContent);
        e.Find(".soundtrack-choice input").Change(false); Click(e, "Apply changes"); Assert.True(saved);
    }
    [Fact]
    public void MixedBrowserFiltersAndPagesStableMediaWithoutLoadingVideos()
    {
        var library = Library(); var owner = library.Assets[0];
        var template = library.Reels[0]; library.Reels.Clear();
        library.Reels.AddRange(Enumerable.Range(0, 55).Select(n => template with { Id = Guid.NewGuid(), Name = $"Angle {n:00}" }));
        var picker = Render<ProjectImagePicker>(p => p.Add(c => c.Library, library).Add(c => c.IncludeReels, true).Add(c => c.BrowseOnly, true));
        Assert.Equal(48, picker.FindAll(".picker-grid > *").Count); Assert.Empty(picker.FindAll("video"));
        picker.FindAll("button").Single(b => b.TextContent.Trim() == "Show more").Click();
        Assert.Equal(56, picker.FindAll(".picker-grid > *").Count);
        var before = picker.FindAll("[data-reel-id]").Select(n => n.GetAttribute("data-reel-id")).ToArray();
        picker.FindAll(".media-filters button").Single(b => b.TextContent == "Images").Click();
        Assert.Empty(picker.FindAll("[data-reel-id]")); Assert.Single(picker.FindAll("[data-reference]"));
        picker.FindAll(".media-filters button").Single(b => b.TextContent == "Reels").Click();
        picker.FindAll("button").Single(b => b.TextContent.Trim() == "Show more").Click();
        Assert.Equal(before, picker.FindAll("[data-reel-id]").Select(n => n.GetAttribute("data-reel-id")));
        picker.Find("input[type=search]").Input("Angle 54"); Assert.Single(picker.FindAll("[data-reel-id]"));
        Assert.Empty(picker.FindAll("video"));
        picker.Find("input[type=search]").Input("missing"); Assert.Empty(picker.FindAll("[data-reel-id]"));
    }
    [Fact]
    public void AssetLookAndOriginFiltersApplyToTheirOwnMediaTypes()
    {
        var library = Library(); var owner = library.Assets[0];
        var look = new CharacterLook { Name = "Everyday" }; var archived = new CharacterLook { Name = "Old", Archived = true };
        library.Assets[0] = owner = owner with { Looks = [look, archived] };
        owner.Images[0] = owner.Images[0] with { LookId = look.Id, Origin = AssetImageOrigin.VideoFrame };
        library.Reels[0] = library.Reels[0] with { LookId = look.Id };
        library.Reels[1] = library.Reels[1] with { LookId = archived.Id };
        var picker = Render<ProjectImagePicker>(p => p.Add(c => c.Library, library).Add(c => c.IncludeReels, true).Add(c => c.BrowseOnly, true));
        Assert.Single(picker.FindAll("[data-reel-id]"));
        picker.Find("select[aria-label=Asset]").Change(owner.Id.ToString());
        picker.Find("select[aria-label=Look]").Change(look.Id.ToString());
        Assert.Single(picker.FindAll("[data-reference]")); Assert.Single(picker.FindAll("[data-reel-id]"));
        picker.FindAll(".media-filters button").Single(b => b.TextContent == "Images").Click();
        picker.Find("select[aria-label=Origin]").Change("Cropped"); Assert.Empty(picker.FindAll("[data-reference]"));
        picker.FindAll(".media-filters button").Single(b => b.TextContent == "All").Click();
        Assert.Empty(picker.FindAll("select[aria-label=Origin]"));
        Assert.Single(picker.FindAll("[data-reference]")); Assert.Single(picker.FindAll("[data-reel-id]"));
        picker.Find("select[aria-label=Look]").Change("general"); Assert.Empty(picker.FindAll(".picker-grid > *"));
    }
    [Fact]
    public void OrdinaryImagePickerCannotSelectReels()
    {
        var picker = Render<ProjectImagePicker>(p => p.Add(c => c.Library, Library()).Add(c => c.BrowseOnly, true));
        Assert.Single(picker.FindAll("[data-reference]")); Assert.Empty(picker.FindAll("[data-reel-id]")); Assert.Empty(picker.FindAll(".media-filters"));
    }
}
