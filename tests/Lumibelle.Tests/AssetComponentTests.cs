using System.Runtime.CompilerServices;
using System.Text.Json;
using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed partial class AssetComponentTests : BunitContext
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly FakeAssetStore _assets;
    private IRenderedComponent<MudBlazor.MudDialogProvider> _dialogs = null!;
    private IRenderedComponent<MudBlazor.MudPopoverProvider> _popovers = null!;
    private readonly IRenderedComponent<SectionOutlet> _actions;
    private readonly IRenderedComponent<SectionOutlet> _navigation;
    private readonly FakeAssetExtractor _extractor = new();
    private readonly ComponentImageGenerator _generator = new();
    private readonly ComponentImageEditor _editor = new();
    private readonly ComponentGuidanceHandler _guidance = new();
    private readonly string _queueRoot = Path.Combine(Path.GetTempPath(), "Lumibelle.AssetComponentQueue", Guid.NewGuid().ToString("N"));
    private AiJobCoordinator _queue = null!;
    private FileAiJobStore _jobs = null!;
    public AssetComponentTests()
    {
        ComponentFactories.AddStub<lumibelle.Components.Assets.ReferenceReelsPanel>();
        _assets = new(_projectId);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == _projectId ? FakeProjectStore.Project("Asset test") with { Id = id } : null) };
        Services.AddMudServices(); Services.AddSingleton<IProjectStore>(projects); Services.AddSingleton<lumibelle.Services.Projects.IProjectFolders>(new FakeProjectFolders()); Services.AddSingleton<lumibelle.Services.Projects.IProjectCompaction>(new FakeProjectCompaction()); Services.AddSingleton<IAssetStore>(_assets); Services.AddSingleton<IImageTrashStore>(_assets); Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IScriptStore>(new FakeScriptStore { Document = ScriptFixtures.Document(_projectId), Approved = ScriptFixtures.Approved(ScriptFixtures.Document(_projectId).Blocks, _projectId) });
        Services.AddSingleton<IAssetExtractor>(_extractor); Services.AddSingleton<IReferenceImageGenerator>(_generator);
        Services.AddSingleton<IReferenceImageEditor>(_editor);
        Services.AddSingleton<IVoiceStore>(new UnusedVoiceStore());
        Services.AddSingleton<lumibelle.Services.Production.IReferenceVideoStore>(new ReferenceEditorMediaFake());
        Services.AddSingleton<IComfyLoraCatalog>(new FakeLoraCatalog());
        Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
        Services.AddSingleton<WorkspacePositions>();
        Services.AddSingleton<IAiProviderRegistry>(new FakeProviders());
        Services.AddSingleton<IPromptEnhancer>(new FakePromptEnhancer());
        Services.AddSingleton<IGuidanceAssistant, GuidanceAssistant>();
        Services.AddSingleton<IAiSettingsStore>(new FakeAiSettingsStore { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } });
        _jobs = new(_queueRoot, TimeProvider.System);
        // Resolve the same fakes that tests modify after page creation.
        Services.AddSingleton<IAiJobStore>(_jobs); Services.AddSingleton<IAiReviewGate>(new ScriptReviewGate());
        Services.AddSingleton<IAiJobReviewStore>(new TestReviewDraftStore(new AiJobReviewStore(_jobs)));
        Services.AddSingleton<TextRequestReviews>();
        Services.AddSingleton<AiTextJobCapture>();
        Services.AddSingleton<AiImageJobCapture>();
        Services.AddSingleton(s => _queue = new(_jobs, s.GetRequiredService<IAiSettingsStore>(),
            [(FakePromptEnhancer)s.GetRequiredService<IPromptEnhancer>(), _extractor, _guidance, new Lumibelle.Testing.MockImageJobHandler(_assets, _generator, _editor)], TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiJobCoordinator>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose; _popovers = Render<MudBlazor.MudPopoverProvider>();
        _actions = Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        _navigation = Render<SectionOutlet>(p => p.Add(x => x.SectionName, "project-navigation"));
        Services.GetRequiredService<AiJobCoordinator>().StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private IRenderedComponent<AssetsStudio> Page(bool withDialogs = true)
    {
        if (withDialogs) _dialogs ??= Render<MudBlazor.MudDialogProvider>();
        return Render<AssetsStudio>(parameters => parameters.Add(page => page.Id, _projectId));
    }
    private async Task ImageActionAsync(IRenderedComponent<AssetsStudio> page, string action, Guid? imageId = null)
    {
        var card = imageId is { } id ? page.FindAll(".reference-card").Single(c => c.InnerHtml.Contains($"/images/{id:D}")) : page.Find(".reference-card");
        await page.InvokeAsync(() => card.QuerySelector(".reference-image-menu button")!.ClickAsync(new()));
        _popovers.WaitForElement("[role='menuitem']");
        await _popovers.InvokeAsync(() => _popovers.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == action).ClickAsync(new()));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_queueRoot)) Directory.Delete(_queueRoot, true);
    }

    [Fact]
    public async Task ReferencesCrossAssetsKeepOrderWhenSwitchingWorkflows()
    {
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "face.png", ContentType = "image/png", Width = 4, Height = 6 };
        var outfit = new AssetImage { Id = Guid.NewGuid(), FileName = "coat.png", ContentType = "image/png", Width = 6, Height = 4 };
        var person = Asset("Mira") with { Images = [source] }; var clothing = Asset("Raincoat") with { Images = [outfit] };
        _assets.Library = _assets.Library with { Assets = [person, clothing] };
        var page = Page(); page.WaitForElement("#asset-image-workflow");
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv");
        page.Find(".media-select").Click();
        Assert.Empty(page.FindAll("#reference-boost"));
        await AddReferenceImage(page, new(clothing.Id, outfit.Id));
        Assert.Contains("Image 2", page.FindAll(".compact-image-input")[1].TextContent); Assert.Contains("Raincoat", page.FindAll(".compact-image-input")[1].TextContent);
        Assert.DoesNotContain(page.FindAll("#additional-reference option"), option => option.GetAttribute("value") == $"{clothing.Id:D}/{outfit.Id:D}");
        page.Find("#image-prompt").Input("Use image 1's person and image 2's coat");
        page.Find("#asset-image-workflow").Change("Krea2");
        Assert.False(page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").HasAttribute("disabled"));
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv");
        _editor.WaitUntilCancelled = true;
        var run = page.InvokeAsync(() => page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Equal(1, _editor.Calls));
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, _editor.LastRequest!.Workflow);
        Assert.Equal(new[] { new AssetImageReference(person.Id, source.Id), new(clothing.Id, outfit.Id) }, _editor.LastReferences);
        // Once queued, the edit keeps its source but its instruction and references are cleared for the next request.
        page.WaitForAssertion(() => Assert.False(page.Find(".manage-image-inputs").HasAttribute("disabled")));
        Assert.False(page.Find("#asset-image-workflow").HasAttribute("disabled"));
        Assert.Contains("Image 1", Assert.Single(page.FindAll(".compact-image-input")).TextContent);
        await page.InvokeAsync(() => { page.Render(); return page.Find(".asset-image-request").QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync(new()); }); await run;
        page.WaitForAssertion(() => Assert.False(page.Find("#asset-image-workflow").HasAttribute("disabled")));
    }

    [Fact]
    public void EmptyLibrarySupportsValidationCancellationAndCreation()
    {
        var page = Page(); page.WaitForElement(".asset-empty");
        page.Find(".asset-empty button").Click(); _dialogs.WaitForElement("#create-asset-form").Submit(); Assert.Contains("Give the asset a name", _dialogs.Markup);
        _dialogs.FindAll(".asset-dialog button").Single(button => button.TextContent.Trim() == "Cancel").Click(); Assert.Equal(0, _assets.SaveCalls);
        page.Find(".asset-library-heading button").Click(); _dialogs.WaitForElement("#new-asset-name").Input("  Mira 光  "); _dialogs.Find("#new-asset-description").Input("  Red coat\n"); _dialogs.Find("#create-asset-form").Submit();
        page.WaitForAssertion(() => Assert.Equal("Mira 光", Assert.Single(_assets.Library.Assets).Name));
        Assert.Equal("  Red coat\n", _assets.Library.Assets[0].Description); Assert.Contains("Mira 光", page.Markup);
    }

    [Fact]
    public void EditingAutosavesAndFailureRetainsInput()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira")] };
        var page = Page(); page.WaitForElement("[aria-label='Edit asset details']").Click();
        _dialogs.WaitForElement("#asset-description");
        _assets.SaveError = new WorkspaceStoreException("Disk unavailable");
        _dialogs.Find("#asset-description").Input("Keep this visual note 🌲");
        page.WaitForAssertion(() => Assert.Contains("Disk unavailable", page.Markup), BunitDefaults.WaitTimeout(2));
        Assert.Equal("Keep this visual note 🌲", _dialogs.Find("#asset-description").GetAttribute("value"));
        _assets.SaveError = null; page.FindAll("button").Single(button => button.TextContent.Trim() == "Retry").Click();
        Assert.Equal("Keep this visual note 🌲", _assets.Library.Assets[0].Description); Assert.Contains("Saved", _actions.Markup);
    }

    [Fact]
    public async Task GeneratedTakeNeedsExplicitReferenceApprovalAndCoverImpliesApproval()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "A woman in a red coat")] };
        var page = Page(); page.WaitForElement("#image-prompt");
        page.Find("#image-tags").Input("face, outfit: red coat");
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images").Click();
        page.WaitForElement(".reference-card");
        var image = Assert.Single(_assets.Library.Assets[0].Images); Assert.False(image.IsReference);
        await ImageActionAsync(page, "Approve reference");
        await page.InvokeAsync(() => _actions.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync(new())); page.WaitForAssertion(() => Assert.True(_assets.Library.Assets[0].Images[0].IsReference));
        await ImageActionAsync(page, "Make cover");
        await page.InvokeAsync(() => _actions.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.True(_assets.Library.Assets[0].Images[0].IsCover)); page.WaitForAssertion(() => Assert.True(_assets.Library.Assets[0].Images[0].IsReference));
    }

    [Fact]
    public async Task EditingStartsFromSelectedImageAndCopiesTagsAndAspect()
    {
        var source = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 768, Height = 1344,
            Tags = ["face", "outfit: red coat"], Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow
        };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [source] }] };
        var page = Page(); page.WaitForElement(".reference-card");

        Assert.Empty(page.FindAll(".asset-tools-header [role=tab]"));
        Assert.Equal("Create image", page.Find("#assets-tool-heading").TextContent);
        page.Find("#image-prompt").Input("Keep the creation draft");
        await page.Find(".media-select").ClickAsync(new());

        Assert.Equal("Edit image", page.Find("#assets-tool-heading").TextContent);
        Assert.Equal("face, outfit: red coat", page.Find("#image-tags").GetAttribute("value"));
        Assert.Equal(ImageAspectPolicy.FromImage1, page.Find("#aspect").GetAttribute("value"));
        Assert.Contains("Image 1 · Source", page.Markup);
        page.Find("#image-prompt").Input("Keep this image edit");
        await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        Assert.Equal("Create image", page.Find("#assets-tool-heading").TextContent);
        Assert.Equal("Keep the creation draft", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("false", page.Find(".media-select").GetAttribute("aria-pressed"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.Equal("Keep this image edit", page.Find("#image-prompt").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.Equal("Create image", page.Find("#assets-tool-heading").TextContent);
        Assert.Equal("Keep the creation draft", page.Find("#image-prompt").GetAttribute("value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredSelectionDeterminesToolsDespiteLegacyModeFlag(bool selected)
    {
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 768, Height = 1344 };
        var asset = Asset("Mira", "Original creation prompt") with { Images = [source], SuggestedImageTags = ["portrait"] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var saved = JsonSerializer.Serialize(new { asset = asset.Id, presentations = new Dictionary<Guid, object> {
            [asset.Id] = new { Selection = selected ? new AssetMediaSelection(AssetMediaKind.Image, source.Id) : null, Edit = !selected }
        } });
        JSInterop.Setup<string?>("localStorage.getItem", WorkspacePositions.Key(_projectId, "assets")).SetResult(saved);
        await Services.GetRequiredService<WorkspacePositions>().LoadAsync(_projectId, "assets");
        var page = Page();
        page.WaitForAssertion(() => Assert.Equal(selected ? "Edit image" : "Create image", page.Find("#assets-tool-heading").TextContent));
        Assert.Equal(selected ? "true" : "false", page.Find(".media-select").GetAttribute("aria-pressed"));
        Assert.Equal(selected ? "Edit instruction" : "Image prompt", page.Find("label[for='image-prompt']").TextContent);
        if (selected) await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        Assert.Equal("Original creation prompt", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("portrait", page.Find("#image-tags").GetAttribute("value"));
        Assert.Equal("1:1", page.Find("#aspect").GetAttribute("value"));
    }

    [Fact]
    public async Task ClearCreationFieldsKeepsOutputSettingsAndDoesNotReturnAfterEditingAnImage()
    {
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 768, Height = 1344 };
        var asset = Asset("Mira", "Original asset description") with { Images = [source], SuggestedImageTags = ["portrait"] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page(); page.WaitForElement("#image-prompt");
        page.Find("#image-prompt").Input("A draft to clear");
        page.Find("#image-tags").Input("draft tag");
        page.Find("#fixed-seed").Input("invalid seed");
        page.Find("#aspect").Change("16:9"); page.Find("#candidate-count").Change("3");
        var workflow = page.Find("#asset-image-workflow").GetAttribute("value");
        await page.Find("[aria-label='Clear creation fields']").ClickAsync(new());
        Assert.Equal("", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("", page.Find("#image-tags").GetAttribute("value"));
        Assert.Equal("", page.Find("#fixed-seed").GetAttribute("value"));
        Assert.Equal("16:9", page.Find("#aspect").GetAttribute("value"));
        Assert.Equal("3", page.Find("#candidate-count").GetAttribute("value"));
        Assert.Equal(workflow, page.Find("#asset-image-workflow").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        page.Find("#image-prompt").Input("Independent edit draft");
        await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        Assert.Equal("", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("", page.Find("#image-tags").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.Equal("Independent edit draft", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("Original asset description", _assets.Library.Assets[0].Description);
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public void EditingValidatesPromptAndSavesUnapprovedLineage()
    {
        var source = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 768, Height = 1344,
            Tags = ["face"], Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow
        };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [source] }] };
        var page = Page(); page.WaitForElement(".reference-card");
        page.Find(".media-select").Click();
        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").HasAttribute("disabled"));

        page.Find("#image-prompt").Input("Put her in a blue coat");
        page.Find("#aspect").Change("16:9");
        page.Find("#reference-boost").Change("5.5");
        page.Find("#grounding-pixels").Change("896");
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").Click();

        page.WaitForAssertion(() => Assert.Equal(2, _assets.Library.Assets[0].Images.Count));
        var edited = _assets.Library.Assets[0].Images[^1];
        Assert.Equal(AssetImageOrigin.Edited, edited.Origin);
        Assert.False(edited.IsReference); Assert.False(edited.IsCover);
        Assert.Equal(source.Id, edited.Generation!.Edit!.SourceImageId);
        Assert.Equal(5.5f, edited.Generation.Edit.ReferenceBoost);
        Assert.Equal(896, edited.Generation.Edit.GroundingPixels);
        Assert.Equal("16:9", edited.Generation.AspectRatio);
    }

    [Fact]
    public async Task EditingCanCropTheSourceWithoutChangingTheStoredOriginal()
    {
        var source = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 400, Height = 300,
            Tags = ["face"], Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow
        };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [source] }] };
        var page = Page(); page.WaitForElement(".reference-card");
        page.Find(".media-select").Click();

        await OpenInputCrop(page, 0);
        Assert.Equal(4, _dialogs.FindAll(".crop-selection [data-crop-handle]").Count);
        _dialogs.Find(".image-copy-crop select").Change("1:1");
        _dialogs.Find("[aria-label='Crop zoom']").Change("1.75");
        _dialogs.Find("[aria-label='Crop horizontal position']").Change("100");
        _dialogs.Find("[aria-label='Crop vertical position']").Change("0");
        await FinishInputs("Apply changes");
        Assert.Contains("Cropped", page.Find(".compact-image-input").TextContent);
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("Keep only the selected portrait"));
        await page.InvokeAsync(() => page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").Click());
        page.WaitForAssertion(() => Assert.NotNull(_editor.LastRequest));

        var crop = _editor.LastRequest!.SourceCrop!;
        Assert.Equal(.75 / 1.75, crop.Width, 6); Assert.Equal(1d / 1.75, crop.Height, 6);
        Assert.Equal(1 - .75 / 1.75, crop.X, 6); Assert.Equal(0, crop.Y, 6);
        Assert.Equal(400, source.Width); Assert.Equal(300, source.Height);
        page.WaitForAssertion(() => Assert.Equal(crop, _assets.Library.Assets[0].Images[^1].Generation!.Edit!.SourceCrop));
    }

    [Fact]
    public void MissingEditDependenciesDisableOnlyEditing()
    {
        var source = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 4, Height = 3,
            Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow
        };
        _editor.Ready = false;
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Portrait") with { Images = [source] }] };
        var page = Page(); page.WaitForElement("#image-prompt");

        Assert.False(page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images").HasAttribute("disabled"));
        page.Find(".media-select").Click();
        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").HasAttribute("disabled"));
        Assert.Contains("Install editing nodes", page.Markup);
    }

    [Fact]
    public void MissingBaseImageModelAlsoDisablesEditing()
    {
        var source = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 4, Height = 3,
            Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow
        };
        _generator.Ready = false;
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Portrait") with { Images = [source] }] };
        var page = Page(); page.WaitForElement(".reference-card");

        page.Find(".media-select").Click();

        Assert.True(page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").HasAttribute("disabled"));
        Assert.Contains("base image model is unavailable", page.Markup);
    }

    [Fact]
    public async Task PendingEditShowsProgressPreventsDuplicatesAndCanBeCancelled()
    {
        var source = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 4, Height = 3,
            Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow
        };
        _editor.WaitUntilCancelled = true;
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [source] }] };
        var page = Page(); page.WaitForElement(".reference-card");
        page.Find(".media-select").Click();
        page.Find("#image-prompt").Input("Change the light");

        var editing = page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate edited images").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(1, _editor.Calls));
        page.WaitForAssertion(() => Assert.Contains("7 / 10 steps", page.Markup));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".asset-image-request")));
        Assert.Equal("", page.Find("#image-prompt").GetAttribute("value")); Assert.False(page.Find("#image-prompt").HasAttribute("disabled"));
        await page.InvokeAsync(() => page.Find(".asset-image-request").QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync(new()));
        await editing;
        Assert.Equal(1, _editor.Calls); page.WaitForAssertion(() => Assert.Contains("cancel", page.Markup, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportedImageEntersAsAnUnapprovedTake()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira")] };
        var page = Page(); page.WaitForElement("input[type='file']");

        page.FindComponents<InputFile>().Single(c => c.Instance.AdditionalAttributes?.TryGetValue("accept", out var accept) == true && accept.ToString()!.Contains("image/")).UploadFiles(
            InputFileContent.CreateFromBinary(AssetStoreTests.Png(4, 3), "portrait.png", contentType: "image/png"));

        page.WaitForAssertion(() => Assert.Single(_assets.Library.Assets[0].Images));
        var image = _assets.Library.Assets[0].Images[0];
        Assert.Equal(AssetImageOrigin.Imported, image.Origin);
        Assert.False(image.IsReference);
        Assert.False(image.IsCover);
        Assert.Single(page.FindAll(".asset-media-card")); Assert.Empty(page.FindAll(".reference-badge"));
    }

    [Fact]
    public void PreviewShowsImageDateAndExplicitCloseReturnsToLibrary()
    {
        var created = new DateTimeOffset(2026, 9, 4, 8, 30, 0, TimeSpan.Zero);
        var image = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "reference.png", ContentType = "image/png", Width = 1024, Height = 1024,
            Origin = AssetImageOrigin.Generated, CreatedUtc = created,
            Generation = new() { Prompt = "A portrait", Seed = 42, AspectRatio = "1:1", DiffusionModel = "krea2", TextEncoder = "qwen", Vae = "vae" }
        };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [image] }] };
        var page = Page(); page.WaitForElement(".reference-image");

        page.Find(".media-preview[aria-label^=\"Preview image\"]").Click();
        var dialog = _dialogs.WaitForElement(".image-review-dialog");
        Assert.Equal(created.ToString("O"), dialog.QuerySelector("time")!.GetAttribute("datetime"));
        Assert.Contains("Generated", dialog.TextContent);
        _dialogs.Find("button[aria-label='Close image review']").Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
        Assert.Single(page.FindAll(".reference-image"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedImagePreviewCanRevealItsExactCroppedSource(bool sourceInTrash)
    {
        var assetId = Guid.NewGuid(); var sourceId = Guid.NewGuid();
        var source = new AssetImage
        {
            Id = sourceId, FileName = "source.png", ContentType = "image/png", Width = 400, Height = 300,
            Tags = ["source"], Origin = AssetImageOrigin.Imported, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var crop = new ImageCropRegion { X = .25, Y = .1, Width = .5, Height = .8 };
        var edited = new AssetImage
        {
            Id = Guid.NewGuid(), FileName = "edited.png", ContentType = "image/png", Width = 800, Height = 600,
            Tags = ["edited"], Origin = AssetImageOrigin.Edited, CreatedUtc = DateTimeOffset.UtcNow,
            Generation = new()
            {
                Prompt = "Edit", Seed = 2, AspectRatio = "4:3", DiffusionModel = "krea2",
                TextEncoder = "qwen", Vae = "vae", Steps = 10,
                Edit = new()
                {
                    SourceAssetId = assetId, SourceImageId = sourceId, Lora = "identity.safetensors",
                    ReferenceBoost = 4, GroundingPixels = 768, FitMode = "fit", SourceCrop = crop
                }
            }
        };
        var asset = Asset("Mira") with { Id = assetId, Images = sourceInTrash ? [edited] : [source, edited] };
        var now = DateTimeOffset.UtcNow;
        var trash = new TrashedImage { Asset = asset with { Images = [] }, Image = source, DeletedUtc = now, ExpiresUtc = now.AddDays(30) };
        _assets.Library = _assets.Library with { Assets = [asset], Trash = sourceInTrash ? [trash] : [] };
        var page = Page(); page.WaitForElement(".reference-grid");
        page.FindAll(".reference-card").Single(card => card.QuerySelector("img")!.GetAttribute("alt")!.Contains("edited")).QuerySelector(".media-preview")!.Click();

        _dialogs.WaitForElement("button[aria-label='Compare with Source']").Click();
        var comparison = _dialogs.Find(".review-wipe");
        Assert.Contains("submitted crop", comparison.TextContent);
        Assert.Contains((sourceInTrash ? trash.Id : sourceId).ToString("D"), _dialogs.Find(".review-wipe-overlay img").GetAttribute("src"));
        Assert.Contains("left:-50%", _dialogs.Find(".review-wipe-overlay img").GetAttribute("style"));
        var reveal = _dialogs.Find(".compare-drag-range");
        Assert.Equal("50% of Source · submitted crop revealed", reveal.GetAttribute("aria-valuetext"));
        reveal.Input("75");
        Assert.Contains("75%", _dialogs.Find(".compare-drag-range").GetAttribute("aria-valuetext"));
        if (sourceInTrash)
        {
            ReviewButton("Restore source").Click();
            Assert.Empty(_assets.Library.Trash);
            Assert.Contains("75%", _dialogs.Find(".compare-drag-range").GetAttribute("aria-valuetext"));
            Assert.Contains("left:-50%", _dialogs.Find(".review-wipe-overlay img").GetAttribute("style"));
        }
        _dialogs.Find("button[aria-label='Compare with Source']").Click();
        Assert.Empty(_dialogs.FindAll(".review-wipe"));
    }

    [Fact]
    public async Task ExtractionUsesTheChosenTextModelAndBlocksMissingModels()
    {
        var cloud = new TextModelReference(AiBackend.OpenRouter, "test/alternate", "Alternate");
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        settings.Value = settings.Value with { StarredTextModels = [cloud] };
        var providers = (FakeProviders)Services.GetRequiredService<IAiProviderRegistry>();
        providers.Models = [new(cloud.Model, cloud.Name), new("test/model", "Default")];
        var page = Page(); await ExtractionClick(page, "Extract from script");
        await _dialogs.InvokeAsync(() => _dialogs.Find(".assist-composer-model .model-chip").ClickAsync(new()));
        _dialogs.Find("select[id^=text-model]").Change(TextModelPolicy.Key(cloud));
        providers.Success = false; _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh availability").Click();
        Assert.True(_dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Find assets").HasAttribute("disabled"));
        Assert.Equal(0, _extractor.Calls);
        providers.Success = true; _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh availability").Click();
        _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Done").Click();
        await ExtractionClick(page, "Find assets");
        page.WaitForAssertion(() => Assert.NotNull(_extractor.LastRequest), BunitDefaults.WaitTimeout(5));
        Assert.Equal(cloud.Model, _extractor.LastRequest!.Model); Assert.Equal(cloud.Backend, _extractor.LastRequest.Backend);
        Assert.Equal(cloud, _extractor.LastRequest.Selection);
        Assert.Equal("test/model", settings.Value.OpenRouterModel);
    }

    [Fact]
    public async Task StoryExtractionRequiresReviewThenCreatesAssetWithEvidenceAndTags()
    {
        _extractor.Result = new([new() { Category = AssetCategory.Character, Name = "Mira", Description = "Short hair and a red coat.", SuggestedTags = ["face", "full body"], Evidence = [new("Story", null, "Mira enters")] }], "[]");
        var page = Page(); page.WaitForElement(".asset-empty");
        await ExtractionClick(page, "Extract from script"); _dialogs.WaitForElement(".ai-assist-dialog");
        Assert.Contains("Approximate input", _dialogs.Markup); Assert.Empty(_assets.Library.Assets);
        await ExtractionClick(page, "Find assets"); page.WaitForElement(".extraction-proposal", BunitDefaults.WaitTimeout(5));
        Assert.Empty(_assets.Library.Assets);
        page.Find(".apply-extraction").Click();
        page.WaitForAssertion(() => Assert.Equal("Mira", Assert.Single(_assets.Library.Assets).Name));
        Assert.Equal(new[] { "face", "full body" }, _assets.Library.Assets[0].SuggestedImageTags); Assert.Single(_assets.Library.Assets[0].Evidence);
    }

    [Fact]
    public async Task StoryExtractionShowsTheSharedCompactProgressView()
    {
        _extractor.WaitForRelease = true;
        var page = Page(); page.WaitForElement(".asset-empty");
        await ExtractionClick(page, "Extract from script");
        _dialogs.WaitForElement(".ai-assist-dialog");

        var extracting = ExtractionClick(page, "Find assets");
        page.WaitForAssertion(() => Assert.Contains("400 / 2,048 tokens", page.Markup), BunitDefaults.WaitTimeout(5));
        Assert.Contains("Cancel request", page.Markup);
        _extractor.Release();
        await extracting;
    }

    [Fact]
    public async Task ExtractionCancellationPreservesThePartialResponseWithoutApplyingAssets()
    {
        _extractor.WaitForRelease = true;
        var page = Page(); page.WaitForElement(".asset-empty");
        await ExtractionClick(page, "Extract from script");
        _dialogs.WaitForElement(".ai-assist-dialog");

        var extracting = ExtractionClick(page, "Find assets");
        page.WaitForAssertion(() => Assert.Contains("400 / 2,048 tokens", page.Markup), BunitDefaults.WaitTimeout(5));
        await page.InvokeAsync(() => page.FindAll(".extraction-dialog button").Single(button => button.TextContent.Trim() == "Cancel request").ClickAsync(new()));
        await extracting;

        page.WaitForAssertion(() => Assert.Contains("cancelled", page.Markup));
        Assert.Contains("partial extraction", page.Markup); Assert.Empty(_assets.Library.Assets);
    }

    [Fact]
    public async Task SearchAndCategoryFiltersKeepIndependentAssets()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira"), Asset("Forest") with { Category = AssetCategory.Environment }] };
        var page = Page(); page.WaitForElement("#asset-search");
        page.Find("#asset-search").Input("forest"); Assert.Single(page.FindAll(".asset-choice")); Assert.Contains("Forest", page.Markup);
        page.Find("#asset-search").Input("");
        await page.Find(".asset-category-filter button").ClickAsync(new());
        _popovers.WaitForElement("[role=menuitemradio]");
        await _popovers.FindAll("[role=menuitemradio]").Single(item => item.TextContent.Contains("Characters")).ClickAsync(new());
        Assert.Single(page.FindAll(".asset-choice")); Assert.Contains("Mira", page.Markup);
        Assert.Equal("Filter assets: Characters", page.Find(".asset-category-filter button").GetAttribute("aria-label"));
        await page.Find(".asset-category-filter button").ClickAsync(new());
        _popovers.WaitForAssertion(() => Assert.Equal("true", _popovers.FindAll("[role=menuitemradio]").Single(item => item.TextContent.Contains("Characters")).GetAttribute("aria-checked")));
        await _popovers.FindAll("[role=menuitemradio]").Single(item => item.TextContent.Contains("All assets")).ClickAsync(new());
        Assert.Equal(2, page.FindAll(".asset-choice").Count);
        await page.Find(".asset-category-filter button").ClickAsync(new());
        await page.Find(".asset-library-search").TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal("false", page.Find(".asset-library-search").GetAttribute("data-workspace-menu-open"));
    }

    [Fact]
    public async Task PendingGenerationPreventsDuplicatesAndCanBeCancelled()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Portrait")] }; _generator.WaitUntilCancelled = true;
        var page = Page(); page.WaitForElement("#image-prompt");
        var running = page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(1, _generator.Calls));
        // The running request is listed on its own; the composer is clear and ready for the next one.
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".asset-image-request")));
        Assert.Equal("", page.Find("#image-prompt").GetAttribute("value")); Assert.False(page.Find("#image-prompt").HasAttribute("disabled"));
        await ClickCurrent(page, () => page.Find(".asset-image-request").QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Cancel")); await running;
        Assert.Equal(1, _generator.Calls); page.WaitForAssertion(() => Assert.Contains("cancel", page.Markup, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EditingWhileGenerationRunsIsSavedBeforeTheCandidateIsAttached()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Original notes")] };
        _generator.WaitForRelease = true;
        var page = Page(); page.WaitForElement("#image-prompt");

        var generating = page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(1, _generator.Calls));
        await page.InvokeAsync(() => page.Find("[aria-label='Edit asset details']").Click());
        _dialogs.WaitForElement("#asset-description");
        await _dialogs.InvokeAsync(() => _dialogs.Find("#asset-description").Input("Edit made while ComfyUI is running"));
        _generator.Release();
        await generating;

        page.WaitForAssertion(() => Assert.Equal("Edit made while ComfyUI is running", _assets.Library.Assets[0].Description));
        Assert.Single(_assets.Library.Assets[0].Images);
        Assert.Equal("Edit made while ComfyUI is running", _dialogs.Find("#asset-description").GetAttribute("value"));
    }

    [Fact]
    public async Task ImageGenerationShowsCompactStepProgressAndCandidateCount()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Portrait")] };
        _generator.WaitForRelease = true;
        var page = Page(); page.WaitForElement("#image-prompt");
        await page.InvokeAsync(() => page.Find("#candidate-count").ChangeAsync(new() { Value = "2" }));

        var generating = page.InvokeAsync(() => page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Contains("6 / 8 steps", page.Markup));
        Assert.Contains("1/2 · ", page.Markup);
        Assert.DoesNotContain("Candidate 1 of 2", page.Markup);
        Assert.DoesNotContain("≈2s left", page.Markup); // Historical estimates lack scope and observation time.
        _generator.Release();
        await generating;
    }

    [Fact]
    public async Task SavingProgressRendersBeforeImageStorageCompletes()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Portrait")] };
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _assets.BeforeSave = async () => { _assets.BeforeSave = null; saveStarted.SetResult(); await releaseSave.Task; };
        var page = Page(); page.WaitForElement("#image-prompt");

        var generating = page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images").ClickAsync(new());
        await saveStarted.Task;
        page.WaitForAssertion(() => Assert.Contains("Saving candidate", page.Markup));
        releaseSave.SetResult();
        await generating;
    }

    [Fact]
    public async Task NavigationWaitsForInflightSaveAndFlushesNewerEdits()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira")] };
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _assets.BeforeSave = async () => { _assets.BeforeSave = null; saveStarted.SetResult(); await releaseSave.Task; };
        var page = Page(withDialogs: false); page.WaitForElement("#asset-name");

        page.Find("#asset-name").Input("First edit");
        var firstSave = _actions.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync(new());
        await saveStarted.Task;
        page.Find("#asset-name").Input("Second edit");
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var navigationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        navigationManager.LocationChanged += (_, _) => navigationCompleted.TrySetResult();
        try
        {
            var destination = _navigation.Find(".project-tabs a[href$='/script']").GetAttribute("href")!;
            // Resolve the anchor as a browser would; bUnit treats rooted paths as file URIs on Windows.
            await page.InvokeAsync(() => navigationManager.NavigateTo(navigationManager.ToAbsoluteUri(destination).AbsoluteUri));
            Assert.False(navigationCompleted.Task.IsCompleted);
        }
        finally { releaseSave.TrySetResult(); }
        await Task.WhenAll(firstSave, navigationCompleted.Task).WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        Assert.EndsWith("/script", navigationManager.Uri);
        Assert.Equal("Second edit", _assets.Library.Assets[0].Name);
        Assert.Equal(2, _assets.SaveCalls);
        Assert.DoesNotContain("Conflict", page.Markup);
    }

    [Fact]
    public void MissingProjectPresentsUsefulReturn()
    {
        var page = Render<AssetsStudio>(parameters => parameters.Add(component => component.Id, Guid.NewGuid()));
        page.WaitForAssertion(() => Assert.Contains("Project not found", page.Markup)); Assert.NotNull(page.Find("a[href='/']"));
    }

    private static ReferenceAsset Asset(string name, string description = "") => new() { Id = Guid.NewGuid(), Name = name, Description = description, Category = AssetCategory.Character, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
}

internal sealed class FakeAssetStore(Guid projectId) : IAssetStore, IImageTrashStore
{
    public Task<AssetLibrary> FinishExtractionAsync(Guid id, ExtractionReviewInput review, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(review, AtomicJsonFile.Options)));
        if (Library.ExtractionReviews.FirstOrDefault(r => r.Id == review.Id) is { } previous)
            return previous.RequestFingerprint == fingerprint ? Task.FromResult(Library) : Task.FromException<AssetLibrary>(new WorkspaceStoreException("This review was already finished with different decisions."));
        var record = new AssetExtractionReview(review.Id, DateTimeOffset.UtcNow, review.ApprovedScriptId,
            review.SceneIds.Select(scene => new ReviewedAssetScene(scene, "Mock scene", new string('0', 64))).ToArray(), ExtractionCoverage.Summary(review.Proposals), fingerprint);
        return SaveAsync(AssetExtractionApply.Apply(Library, review.Proposals, DateTimeOffset.UtcNow) with { ExtractionReviews = [.. Library.ExtractionReviews, record] }, expectedRevision, cancellationToken);
    }
    public Task<SavedAssetImage> SaveDerivedImageAsync(Guid id, DerivedImageRequest request, long expectedRevision, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Derived-image operations use real file-store test fixtures.");
    public AssetLibrary Library { get; set; } = new() { ProjectId = projectId };
    public AssetLibrary Working { get; private set; } = new() { ProjectId = projectId };
    public int SaveCalls { get; private set; }
    public Exception? SaveError { get; set; }
    public Func<Task>? BeforeSave { get; set; }
    public Func<Task>? BeforeLoad { get; set; }
    public async Task<AssetLibrary> LoadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (BeforeLoad is not null) await BeforeLoad();
        Working = Library.Copy(); return Working.Copy();
    }
    public async Task<AssetLibrary> SaveAsync(AssetLibrary library, long expectedRevision, CancellationToken cancellationToken = default)
    {
        SaveCalls++; if (BeforeSave is not null) await BeforeSave();
        if (SaveError is not null) throw SaveError; if (expectedRevision != Library.Revision) throw new WorkspaceConflictException();
        Library = library with { Revision = expectedRevision + 1 }; Working = Library.Copy(); return Library.Copy();
    }
    public Task<AssetLibrary> AddImageAsync(Guid id, Guid assetId, Stream content, AssetImageInput input, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = Guid.NewGuid().ToString("N") + ".png", ContentType = "image/png", Width = 3, Height = 2, Tags = input.Tags.ToList(), Origin = input.Origin, Generation = input.Generation, CreatedUtc = DateTimeOffset.UtcNow };
        return SaveAsync(Library with { Assets = Library.Assets.Select(asset => asset.Id == assetId ? asset with { Images = [.. asset.Images, image] } : asset).ToList() }, expectedRevision, cancellationToken);
    }
    public async Task<SavedAssetImage> PublishGeneratedImageAsync(Guid id, GeneratedImageInput input, Stream content, CancellationToken cancellationToken = default)
    {
        if (Library.ImagePublications.Any(p => p.JobId == input.JobId && p.ImageId == input.ImageId))
            return new(Library.Copy(), input.AssetId, input.ImageId, Library.Assets.Any(a => a.Images.Any(i => i.Id == input.ImageId)));
        var image = new AssetImage { Id = input.ImageId, FileName = input.ImageId.ToString("N") + ".png", ContentType = "image/png", Width = 3, Height = 2,
            Tags = input.Image.Tags.ToList(), Origin = input.Image.Origin, Generation = input.Image.Generation, LookId = input.Image.LookId, CreatedUtc = DateTimeOffset.UtcNow };
        var saved = await SaveAsync(Library with { Assets = Library.Assets.Select(a => a.Id == input.AssetId ? a with { Images = [.. a.Images, image] } : a).ToList(),
            ImagePublications = [.. Library.ImagePublications, new(input.JobId, input.ImageId, input.AssetId, new string('0', 64))] }, Library.Revision, cancellationToken);
        return new(saved, input.AssetId, input.ImageId, true);
    }
    public Task<ImageTrashResult> DeleteImageAsync(Guid id, Guid assetId, Guid imageId, long expectedRevision, CancellationToken cancellationToken = default) => TrashAsync(assetId, [imageId], expectedRevision, cancellationToken);
    public Exception? DeleteError { get; set; }
    public Exception? RestoreError { get; set; }
    public int DeleteCalls { get; private set; }
    public Task<ImageTrashResult> DeleteImagesAsync(Guid id, Guid assetId, IReadOnlyCollection<Guid> ids, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (Library.Assets.Single(a => a.Id == assetId).Images.Any(i => ids.Contains(i.Id) && (i.IsReference || i.IsCover || i.Origin == AssetImageOrigin.Imported)))
            throw new WorkspaceStoreException("Only unapproved generated takes can be discarded here.");
        return TrashAsync(assetId, ids, expectedRevision, cancellationToken);
    }
    private async Task<ImageTrashResult> TrashAsync(Guid assetId, IReadOnlyCollection<Guid> ids, long revision, CancellationToken ct)
    {
        DeleteCalls++; if (DeleteError is not null) throw DeleteError;
        var asset = Library.Assets.Single(a => a.Id == assetId); var now = DateTimeOffset.UtcNow;
        var entries = asset.Images.Where(i => ids.Contains(i.Id)).Select(i => new TrashedImage
        { Asset = asset with { Images = [] }, Image = i, DeletedUtc = now, ExpiresUtc = now.AddDays(30) }).ToArray();
        var saved = await SaveAsync(Library with { Trash = [.. Library.Trash, .. entries], Assets = Library.Assets.Select(a => a.Id == assetId ? a with { Images = a.Images.Where(i => !ids.Contains(i.Id)).ToList() } : a).ToList() }, revision, ct);
        return new(saved, entries.Select(t => t.Id).ToArray());
    }
    public async Task<AssetLibrary> RestoreImagesAsync(Guid id, IReadOnlyCollection<Guid> ids, long revision, CancellationToken cancellationToken = default)
    {
        if (RestoreError is not null) throw RestoreError;
        var entries = Library.Trash.Where(t => ids.Contains(t.Id)).ToArray();
        if (entries.Any(t => !t.CanRestore(DateTimeOffset.UtcNow))) throw new WorkspaceStoreException("Expired.");
        return await SaveAsync(Library with { Trash = Library.Trash.Where(t => !ids.Contains(t.Id)).ToList(),
            Assets = Library.Assets.Select(a => a with { Images = [.. a.Images, .. entries.Where(t => t.Asset.Id == a.Id).Select(t => t.Image)] }).ToList() }, revision, cancellationToken);
    }
    public Task<ImageTrashLibrary> ListTrashAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ImageTrashLibrary(Library.Trash.Select(t => new ImageTrashRow(projectId, "Project", Library.Revision, t)).ToArray(), []));
    public Task<AssetMedia?> OpenTrashImageAsync(Guid id, Guid trashId, CancellationToken cancellationToken = default) => Task.FromResult<AssetMedia?>(null);
    public Task<ImagePurgeResult> PurgeImagesAsync(Guid id, IReadOnlyCollection<Guid> ids, long revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ImageTrashIssue>> CleanupExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ImageTrashIssue>>([]);
    public Task<AssetLibrary> DeleteAssetAsync(Guid id, Guid assetId, long expectedRevision, CancellationToken cancellationToken = default) => SaveAsync(Library with { Assets = Library.Assets.Where(asset => asset.Id != assetId).ToList() }, expectedRevision, cancellationToken);
    public Task<AssetMedia?> OpenImageAsync(Guid id, Guid assetId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var image = Library.Assets.SingleOrDefault(asset => asset.Id == assetId)?.Images.SingleOrDefault(value => value.Id == imageId);
        return Task.FromResult(image is null ? null : new AssetMedia(
            new MemoryStream(AssetStoreTests.Png(image.Width, image.Height)), image.ContentType, image.CreatedUtc));
    }
}

internal sealed class FakeAssetExtractor : IAssetExtractor, IAiJobHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.AssetExtraction];
    public CancellationToken Token { get; private set; }
    public Guid JobId { get; private set; }
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        Token = ct; JobId = context.Job.Id;
        var request = snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<AssetExtractionRequest>();
        await context.SaveResultAsync(new AiTextJobResult("partial extraction"));
        await foreach (var update in ExtractAsync(request, ct))
        {
            if (update.Progress is { } progress) await context.ReportAsync(new(progress));
            if (update.Result is { } result) await context.SaveResultAsync(new AiTextJobResult(result.RawText, true, "stop", JsonSerializer.SerializeToElement(result, AtomicJsonFile.Options), result.ValidationError));
        }
        return Result.ValidationError is null ? AiJobOutcome.Complete() : AiJobOutcome.Attention(Result.ValidationError, AiJobRecovery.GenerateAgain);
    }
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(AiJobOutcome.Attention("Interrupted extraction", AiJobRecovery.GenerateAgain));
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
    public AssetExtractionRequest? LastRequest { get; private set; }
    public AssetExtractionResult Result { get; set; } = new([], "[]");
    public int Calls { get; private set; }
    public bool WaitForRelease { get; set; }
    public bool UseScopedCancellationWarning { get; set; }
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _release.TrySetResult();
    public async IAsyncEnumerable<AssetExtractionUpdate> ExtractAsync(AssetExtractionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++; LastRequest = request;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new(Progress: new(GenerationPhase.Generating, "Generating text", 400, 2048, "tokens", TimeSpan.FromSeconds(5)));
        if (WaitForRelease)
        {
            try { await _release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (UseScopedCancellationWarning)
            { throw new AiCancellationException("Stopped waiting. ComfyUI execution may continue; any late result will be ignored.", cancellationToken); }
        }
        yield return new(Result: Result);
    }
}

internal sealed class ComponentImageGenerator : IReferenceImageGenerator
{
    public int Calls { get; private set; }
    public bool Ready { get; set; } = true;
    public bool WaitUntilCancelled { get; set; }
    public bool WaitForRelease { get; set; }
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _release.TrySetResult();
    public Func<Task>? BeforeCheck { get; set; }
    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default)
    {
        if (BeforeCheck is not null) await BeforeCheck();
        return new(Ready, Ready ? "Ready" : "The configured base image model is unavailable.", [], [], []);
    }
    public async IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        if (WaitUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return new("Generating image", 1, request.Count, Progress: new(GenerationPhase.Generating,
            "Generating image", 6, 8, "steps", TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2)));
        if (WaitForRelease) await _release.Task.WaitAsync(cancellationToken);
        yield return new("Saving candidate…", 1, request.Count, AssetStoreTests.Png(3, 2), "take.png", new() { Prompt = request.Prompt, Seed = request.Seed ?? 7, AspectRatio = request.AspectRatio, DiffusionModel = "krea2", TextEncoder = "qwen", Vae = "vae" }, new(GenerationPhase.Saving, "Saving candidate…"));
    }
}

internal sealed class ComponentImageEditor : IReferenceImageEditor
{
    public int KreaMaximumReferences { get; set; } = 2;
    public IReadOnlyList<AssetImageReference> LastReferences { get; private set; } = [];
    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources,
        CancellationToken cancellationToken = default)
    {
        LastReferences = sources.Select(source => new AssetImageReference(source.AssetId, source.ImageId)).ToArray();
        return EditAsync(request, sources[0].Content, cancellationToken);
    }
    public List<ReferenceEditRequest> Requests { get; } = [];
    public int? FailAfterCandidates { get; set; }
    public bool CancelAfterFirst { get; set; }
    public bool HoldAfterFirst { get; set; }
    private readonly TaskCompletionSource _releaseReview = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ReleaseReview() => _releaseReview.TrySetResult();
    public bool NoResults { get; set; }
    public int Calls { get; private set; }
    public ReferenceEditRequest? LastRequest { get; private set; }
    public bool Ready { get; set; } = true;
    public bool WaitUntilCancelled { get; set; }
    public Task<ComfyImageEditConfiguration> CheckAsync(AiSettings? settings = null,
        CancellationToken cancellationToken = default) => Task.FromResult(new ComfyImageEditConfiguration(Ready,
        Ready ? "Ready" : "Install editing nodes and refresh ComfyUI.",
        [new(settings?.ComfyImageEditLora ?? "krea2_identity_edit_v1_2.safetensors", "Krea 2 Identity Edit")], settings?.DefaultImageWorkflow == ImageWorkflow.Flux2Klein9bKv ? 8 : KreaMaximumReferences, "Update editing nodes for two images."));

    public async IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++; LastRequest = request; Requests.Add(request);
        yield return new("Editing image", 1, request.Count, Progress: new(GenerationPhase.Generating,
            "Editing image", 7, 10, "steps", TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(3)));
        if (WaitUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
        await Task.Yield();
        if (NoResults) yield break;
        for (var candidate = 1; candidate <= request.Count; candidate++)
        {
        yield return new("Saving edited candidate…", candidate, request.Count, AssetStoreTests.Png(3, 2), "edit.png",
            new()
            {
                Workflow = request.Workflow ?? ImageWorkflow.Krea2, Prompt = request.Prompt, Seed = request.Seed ?? 11, AspectRatio = request.AspectRatio,
                DiffusionModel = "krea2", TextEncoder = "qwen", Vae = "vae", Steps = 10,
                Edit = new()
                {
                    SourceAssetId = request.SourceAssetId, SourceImageId = request.SourceImageId,
                    Lora = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? "" : "krea2_identity_edit_v1_2.safetensors", LoraStrength = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? 0 : 1,
                    ReferenceBoost = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? 0 : request.ReferenceBoost, GroundingPixels = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? 0 : request.GroundingPixels, FitMode = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? "reference-latent" : "fit",
                    SourceCrop = request.SourceCrop, ReferenceCrops = request.ReferenceCrops, References = LastReferences,
                    BaseReferenceBoost = request.Workflow != ImageWorkflow.Flux2Klein9bKv && LastReferences.Count == 2 ? request.BaseReferenceBoost : null
                }
            }, new(GenerationPhase.Saving, "Saving edited candidate…"));
        if (FailAfterCandidates == candidate) throw new AiGenerationException("Mock provider failed after a completed take.");
        if (CancelAfterFirst) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (HoldAfterFirst && candidate == 1) await _releaseReview.Task.WaitAsync(cancellationToken);
        }
    }
}
