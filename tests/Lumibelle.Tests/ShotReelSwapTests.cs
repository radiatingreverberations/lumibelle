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
public sealed class ShotReelSwapTests : BunitContext
{
    public ShotReelSwapTests() { Services.AddMudServices(); Services.AddSingleton<IReferenceVideoStore>(new ReferenceEditorMediaFake()); Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore()); JSInterop.Mode = JSRuntimeMode.Loose; }
    private static ReferenceVideoMedia Media(int size) =>
        new(Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadRight(64, 'A'), 1000, size, size, 120, 24, 5, true);

    [Fact]
    public async Task ReplacingAReelInTheShotOffersSharperReelsFirstAndKeepsTheAttachmentsSettings()
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var other = owner with { Id = Guid.NewGuid(), Name = "Mira" };
        var quick = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley quick", Media = Media(448) };
        var detail = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley detail", Media = Media(832) };
        var native = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley native", Media = Media(992) };
        var theirs = new AssetReferenceReel { AssetId = other.Id, Name = "Mira native", Media = Media(992) };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner, other], Reels = [quick, detail, native, theirs] };
        var binding = new ShotVideoBinding { Media = quick.Media, Name = "Riley in the chair", Description = "Identity only",
            OwnerAssetId = owner.Id, OwnerCategory = AssetCategory.Character, Visuals = ReelVisuals.FullReel };
        ReferenceSelection? applied = null;
        var editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, new Shot { Title = "Chair", Videos = [binding] }).Add(c => c.Library, library)
            .Add(c => c.AllowReels, true).Add(c => c.ReferencesApplied, (ReferenceSelection s) => applied = s));
        Assert.Contains("0.2 MP", editor.Find("[data-video-reference-id]").TextContent);

        await editor.Find("[aria-label='Video 1: Replace reel']").ClickAsync(new());
        Assert.Equal(["Use Riley native · 1.0 MP", "Use Riley detail · 0.7 MP"],
            editor.FindAll(".reel-swap-choices button").Select(b => b.GetAttribute("aria-label")));
        await editor.FindAll(".reel-swap-choices button")[0].ClickAsync(new());
        Assert.Contains("1.0 MP", editor.Find("[data-video-reference-id]").TextContent);
        Assert.Contains("Uses Riley native · 1.0 MP instead of Riley quick · 0.2 MP", editor.Find(".reel-swap-result").TextContent);
        Assert.Empty(editor.FindAll(".reel-swap"));

        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new());
        var video = Assert.Single(applied!.Inputs.Videos);
        Assert.Equal((native.Media, binding.Id, "Riley in the chair", "Identity only", ReelVisuals.FullReel),
            (video.Media, video.Id, video.Name, video.Description, video.EffectiveVisuals));
        Assert.Equal(new ReelSwap(quick.Id, native.Id, false), Assert.Single(applied.Swaps));
    }

    [Fact]
    public async Task WhileReplacingTheReferenceListReplacesTheReelInsteadOfAddingOne()
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character, Looks = [new() { Name = "Elementalist" }] };
        var other = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character };
        var quick = new AssetReferenceReel { AssetId = owner.Id, Name = "Riley reference", Media = Media(448) };
        var elementalist = new AssetReferenceReel { AssetId = owner.Id, LookId = owner.Looks[0].Id, Name = "Riley · Elementalist", Media = Media(992) };
        var theirs = new AssetReferenceReel { AssetId = other.Id, Name = "Mira native", UseGuidance = "Mira's guidance", Media = Media(992) };
        var filler = new[] { Media(640), Media(640) }.Select((m, i) => new ShotVideoBinding { Media = m, Name = "Filler " + i, OwnerAssetId = other.Id, Visuals = ReelVisuals.FullReel });
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [owner, other], Reels = [quick, elementalist, theirs] };
        var binding = new ShotVideoBinding { Media = quick.Media, Name = "Riley reference", Description = "Identity only", OwnerAssetId = owner.Id,
            OwnerCategory = AssetCategory.Character, Visuals = ReelVisuals.FullReel };
        ReferenceSelection? applied = null;
        var editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, new Shot { Title = "Chair", Videos = [.. filler, binding] }).Add(c => c.Library, library)
            .Add(c => c.AllowReels, true).Add(c => c.ReferencesApplied, (ReferenceSelection s) => applied = s));
        // Three of three reels: the list cannot add another.
        Assert.True(editor.Find($"[data-reel-id='{elementalist.Id}'] .add-reel").HasAttribute("disabled"));

        await editor.Find("[aria-label='Video 3: Replace reel']").ClickAsync(new());
        Assert.Contains("Elementalist", editor.Find(".reel-swap-choices").TextContent);
        var card = editor.Find($"[data-reel-id='{elementalist.Id}'] .add-reel");
        Assert.Equal(("Replace Video 3", false), (card.TextContent.Trim(), card.HasAttribute("disabled")));
        await card.ClickAsync(new());
        Assert.Contains("1.0 MP", editor.FindAll("[data-video-reference-id]")[2].TextContent);
        Assert.Contains("Uses Riley · Elementalist · 1.0 MP instead of Riley reference · 0.2 MP", editor.Find(".reel-swap-result").TextContent);
        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new());
        var video = applied!.Inputs.Videos[2];
        Assert.Equal((elementalist.Media, binding.Id, "Riley reference", "Identity only"), (video.Media, video.Id, video.Name, video.Description));
        Assert.Equal(new ReelSwap(quick.Id, elementalist.Id, false), Assert.Single(applied.Swaps));

        // Another asset's reel replaces the reference from its own library defaults.
        await editor.Find("[aria-label='Video 3: Replace reel']").ClickAsync(new());
        await editor.Find($"[data-reel-id='{theirs.Id}'] .add-reel").ClickAsync(new());
        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new());
        video = applied.Inputs.Videos[2];
        Assert.Equal((theirs.Media, binding.Id, "Mira native", "Mira's guidance"), (video.Media, video.Id, video.Name, video.Description));
        Assert.Empty(applied.Swaps);
    }
}
