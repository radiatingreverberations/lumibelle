using Bunit;
using Bunit.TestDoubles;
using lumibelle.Components.Assets;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ShotReferenceEditorTests : BunitContext
{
    public ShotReferenceEditorTests() { Services.AddMudServices(); Services.AddSingleton<IReferenceVideoStore>(new ReferenceEditorMediaFake()); Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore()); JSInterop.Mode = JSRuntimeMode.Loose; }
    [Fact]
    public async Task FailedApplyRetainsRoleCropAndGuidanceAndRetryPublishesOnlyDraft()
    {
        ComponentFactories.AddStub<ImageInputCropDialog>();
        var image = new AssetImage { Id = Guid.NewGuid(), Name = "Door", FileName = "door.png", ContentType = "image/png", Width = 32, Height = 32 };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment, Images = [image] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] };
        var shot = new Shot { Title = "A quiet room" }; bool fail = true; Shot? saved = null;
        var editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, shot).Add(c => c.Library, library)
            .Add(c => c.Applied, (Shot s) => { if (fail) throw new IOException("Disk unavailable"); saved = s; }));
        editor.Find(".picker-grid button").Click();
        editor.Find("select[aria-label='Use for']").Change("use:FirstFrame");
        editor.Find("[data-guidance-customize]").Click(); editor.Find("textarea").Input("Keep the doorway.");
        await editor.Find(".reference-crop-button").ClickAsync(new());
        var crop = editor.FindComponent<Stub<ImageInputCropDialog>>().Instance.Parameters;
        await editor.InvokeAsync(() => crop.Get(c => c.Applied).InvokeAsync(new ImageCropRegion { X = 0, Y = 0, Width = .5, Height = 1 }));
        editor.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").Click();
        Assert.Contains("Disk unavailable", editor.Find("[role=alert]").TextContent);
        Assert.Empty(shot.Images); Assert.Null(saved);
        Assert.Equal("Keep the doorway.", editor.Find("textarea").GetAttribute("value"));
        fail = false;
        editor.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").Click();
        Assert.Equal(ShotImageUse.FirstFrame, saved!.Images[0].Use); Assert.Equal(.5, saved.Images[0].Crop!.Width);
        Assert.Equal("Keep the doorway.", saved.Images[0].PreservationOverride); Assert.Empty(shot.Images);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AMissingPictureStillInTrashCanBeRestoredWithoutLosingPendingEdits(bool assetDeleted)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), Name = "Front", FileName = "front.png", ContentType = "image/png", Width = 32, Height = 48 };
        var kept = new AssetImage { Id = Guid.NewGuid(), Name = "Side", FileName = "side.png", ContentType = "image/png", Width = 32, Height = 48 };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Belle", Category = AssetCategory.Character, Images = assetDeleted ? [] : [kept] };
        var trash = new TrashedImage { Image = image, Asset = asset with { Images = [] }, DeletedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) };
        var trashed = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = assetDeleted ? [] : [asset], Trash = [trash] };
        var restored = trashed with { Assets = [asset with { Images = [.. asset.Images, image] }], Trash = [] };
        var shot = new Shot { Images = [new() { Kind = ShotImageKind.AssetImage, AssetId = asset.Id, MediaId = image.Id, Name = "Belle", InferUsage = true, AiUseHint = "Auto" }] };
        var requested = new List<Guid>(); Shot? saved = null;
        IRenderedComponent<ShotReferenceEditor> editor = null!;
        editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, shot).Add(c => c.Library, trashed).Add(c => c.AllowInference, true).Add(c => c.AllowReels, true)
            .Add(c => c.ReelAuthoring, true).Add(c => c.Applied, s => saved = s)
            .Add(c => c.RestoreTrashedImage, id => { requested.Add(id); editor.Render(q => q.Add(c => c.Library, restored)); return Task.CompletedTask; }));
        Assert.Contains("Restore or replace the unavailable reference.", editor.Find(".reference-issue").TextContent);
        Assert.Contains("In Trash · 30 days remaining", editor.Find(".reference-trash-restore").TextContent);
        await editor.Find("[aria-label='Picture 1: Reference settings']").ClickAsync(new());
        editor.Find("select[aria-label='AI use hint']").Change("Identity");
        await editor.FindAll("button").Single(b => b.TextContent == "Restore image").ClickAsync(new());
        Assert.Equal([trash.Id], requested);
        Assert.Empty(editor.FindAll(".reference-issue")); Assert.Empty(editor.FindAll(".reference-trash-restore"));
        Assert.Equal("Identity", editor.Find("select[aria-label='AI use hint']").GetAttribute("value"));
        editor.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").Click();
        Assert.Equal(image.Id, Assert.Single(saved!.Images).MediaId); Assert.Equal("Identity", saved.Images[0].AiUseHint);
    }

    [Theory]
    [InlineData("expired")] [InlineData("purging")] [InlineData("no-host")]
    public void OnlyRecoverableTrashWithARestoringHostOffersRestore(string scenario)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), Name = "Front", FileName = "front.png", ContentType = "image/png", Width = 32, Height = 48 };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Belle", Category = AssetCategory.Character };
        var trash = new TrashedImage { Image = image, Asset = asset, DeletedUtc = DateTimeOffset.UtcNow.AddDays(-31),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(scenario == "expired" ? -1 : 1), State = scenario == "purging" ? ImageTrashState.Purging : ImageTrashState.Recoverable };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset], Trash = [trash] };
        var shot = new Shot { Images = [new() { Kind = ShotImageKind.AssetImage, AssetId = asset.Id, MediaId = image.Id, Name = "Belle", InferUsage = true }] };
        var editor = Render<ShotReferenceEditor>(p => {
            p.Add(c => c.Shot, shot).Add(c => c.Library, library).Add(c => c.AllowInference, true);
            if (scenario != "no-host") p.Add(c => c.RestoreTrashedImage, _ => Task.CompletedTask);
        });
        Assert.Contains("Restore or replace the unavailable reference.", editor.Find(".reference-issue").TextContent);
        Assert.Empty(editor.FindAll(".reference-trash-restore"));
    }

    [Fact]
    public async Task CropFollowsItsReferenceAfterReorderingAndOuterCancelLeavesTheShotUntouched()
    {
        ComponentFactories.AddStub<ImageInputCropDialog>();
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment,
            Images = Enumerable.Range(1, 2).Select(n => new AssetImage { Id = Guid.NewGuid(), Name = $"View {n}", FileName = "view.png", ContentType = "image/png", Width = 320, Height = 240 }).ToList() };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] };
        var shot = new Shot();
        Shot? saved = null; var cancelled = false;
        var editor = Render<ShotReferenceEditor>(p => p.Add(c => c.Shot, shot).Add(c => c.Library, library).Add(c => c.AllowInference, true)
            .Add(c => c.Applied, s => saved = s).Add(c => c.Cancelled, () => cancelled = true));
        await editor.Find("[data-reference]").ClickAsync(new());
        await editor.Find("[data-reference]:not([disabled])").ClickAsync(new());
        await editor.Find("[aria-label='Picture 2: Crop image']").ClickAsync(new());
        var crop = editor.FindComponent<Stub<ImageInputCropDialog>>().Instance.Parameters;
        Assert.Equal(320, crop.Get(c => c.Width)); Assert.Equal(240, crop.Get(c => c.Height));
        var region = new ImageCropRegion { X = .25, Y = 0, Width = .5, Height = 1 };
        await editor.InvokeAsync(() => crop.Get(c => c.Applied).InvokeAsync(region));
        await editor.Find("[aria-label='Move Picture 2 up']").ClickAsync(new());
        Assert.Contains("Cropped", editor.Find(".reference-summary").TextContent);
        Assert.Equal(region, editor.FindComponents<CropThumbnail>()[0].Instance.Crop);
        await editor.Find("[aria-label='Picture 1: Crop image']").ClickAsync(new());
        crop = editor.FindComponent<Stub<ImageInputCropDialog>>().Instance.Parameters;
        Assert.Equal(region, crop.Get(c => c.InitialCrop));
        await editor.InvokeAsync(() => crop.Get(c => c.Cancelled).InvokeAsync());
        Assert.Equal(region, editor.FindComponents<CropThumbnail>()[0].Instance.Crop);
        await editor.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new());
        Assert.True(cancelled); Assert.Null(saved); Assert.Empty(shot.Images);
    }
}
