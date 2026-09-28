using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ReferenceCopyComponentTests : BunitContext
{
    public ReferenceCopyComponentTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IReferenceVideoStore>(new ReferenceEditorMediaFake());
        Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static (AssetLibrary Library, Shot Source, Shot Target) Fixture()
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        AssetReferenceReel Reel(string name) => new() { AssetId = owner.Id, Name = name, UseGuidance = name,
            Media = new(Guid.NewGuid(), new('a', 64), 1000, 160, 96, 120, 24, 5, false) };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner], Reels = [Reel("Pending"), Reel("Copied")] };
        var source = new Shot { Title = "Source", Duration = 5, Videos = [new() { Name = "Captured guidance", Description = "Preserve this view",
            Media = library.Reels[1].Media, OwnerAssetId = owner.Id, OwnerCategory = owner.Category, UseSoundtrack = false }] };
        return (library, source, new Shot { Title = "Destination", Duration = 5 });
    }
    private static void Click(IRenderedComponent<ShotReferenceEditor> editor, string label) =>
        editor.FindAll("button").Single(b => b.TextContent.Trim() == label).Click();

    [Fact]
    public void CopyingIsOnlyAPendingDraftAndCancelDoesNotMutateEitherShot()
    {
        var (library, source, target) = Fixture(); var cancelled = false; ReferenceSelection? applied = null;
        var originalId = source.Videos[0].Id;
        var editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, target).Add(c => c.Library, library)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true).Add(c => c.CopySources, new[] { source })
            .Add(c => c.Cancelled, () => cancelled = true).Add(c => c.ReferencesApplied, (ReferenceSelection s) => applied = s));
        editor.Find("select[aria-label='Copy references from']").Change(source.Id.ToString());
        Assert.Single(editor.FindAll(".video-reference-list li"));
        Assert.Empty(target.Videos); Assert.Equal(originalId, source.Videos[0].Id); Assert.Null(applied);
        Click(editor, "Cancel");
        Assert.True(cancelled); Assert.Empty(target.Videos); Assert.Null(applied);
    }

    [Fact]
    public void CopyOverAPendingSelectionClearsReplacementAndOldReelSourceTracking()
    {
        var (library, source, target) = Fixture(); ReferenceSelection? applied = null;
        var editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, target).Add(c => c.Library, library)
            .Add(c => c.AllowReels, true).Add(c => c.AllowInference, true).Add(c => c.CopySources, new[] { source })
            .Add(c => c.ReferencesApplied, (ReferenceSelection s) => applied = s));
        editor.Find($"[data-reel-id='{library.Reels[0].Id}'] .add-reel").Click();
        Click(editor, "Replace reel");
        Assert.Contains("Choose the replacement reel", editor.Markup);
        editor.Find("select[aria-label='Copy references from']").Change(source.Id.ToString());
        Assert.DoesNotContain("Choose the replacement reel", editor.Markup);
        var firstId = editor.Find("[data-video-reference-id]").GetAttribute("data-video-reference-id");
        // Resetting the chooser permits selecting the same source again.
        editor.Find("select[aria-label='Copy references from']").Change(source.Id.ToString());
        Assert.NotEqual(firstId, editor.Find("[data-video-reference-id]").GetAttribute("data-video-reference-id"));
        Click(editor, "Apply changes");
        Assert.NotNull(applied); Assert.Empty(applied.ReelSources);
        var video = Assert.Single(applied.Inputs.Videos);
        Assert.Equal(source.Videos[0].Media, video.Media); Assert.Equal("Preserve this view", video.Description);
        Assert.NotEqual(source.Videos[0].Id, video.Id); Assert.Empty(target.Videos);
    }
}
