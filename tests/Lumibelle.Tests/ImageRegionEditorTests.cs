using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ImageRegionEditorTests : BunitContext
{
    private readonly Guid _project = Guid.NewGuid();
    private readonly AssetImageReference _source = new(Guid.NewGuid(), Guid.NewGuid());
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    private RegionalImageSelection? _saved;
    private bool _applied, _cancelled;

    public ImageRegionEditorTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IAssetStore>(new FakeAssetStore(_project)
        {
            Library = new() { ProjectId = _project, Assets = [new() { Id = _source.AssetId, Name = "Character", Images =
                [new() { Id = _source.ImageId, FileName = "test.png", ContentType = "image/png", Width = 32, Height = 32 }] }] }
        });
        JSInterop.Mode = JSRuntimeMode.Loose;
        _dialogs = Render<MudDialogProvider>();
    }

    private async Task<RegionalImageSelection> Selection(bool edit = true)
    {
        using var stream = new MemoryStream(AssetStoreTests.Png(32, 32));
        return new()
        {
            Source = _source, SourceHash = RegionalImageEdits.Hash(await RegionalImageEdits.OriginalAsync(stream)),
            Width = 32, Height = 32, Mode = edit ? RegionalEditMode.Edit : RegionalEditMode.Protect,
            Strokes = edit ? [RegionalImageTests.Rect(false, 0, 0, 1, 1), RegionalImageTests.Rect(true, 0, 0, .25, 1)]
                : [RegionalImageTests.Rect(true, 0, 0, .25, 1)]
        };
    }

    private IRenderedComponent<ImageRegionEditor> Editor(bool edit, RegionalImageSelection initial, bool referenceOnly = false) =>
        Render<ImageRegionEditor>(p => p.Add(c => c.ProjectId, _project).Add(c => c.Source, _source)
            .Add(c => c.Initial, initial).Add(c => c.EditArea, edit).Add(c => c.ReferenceOnly, referenceOnly)
            .Add(c => c.Applied, result => { _saved = result; _applied = true; }).Add(c => c.Cancelled, () => _cancelled = true));

    private async Task Click(string text) => await _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PaintingOnlyChangesTheChosenLayer(bool edit)
    {
        var initial = await Selection(edit: !edit);
        var originalMasks = RegionalImageEdits.Masks(initial);
        var component = Editor(edit, initial);
        await component.InvokeAsync(() => component.Instance.Stroke(RegionalImageTests.Rect(edit, .5, .5, .75, .75)));
        await Click(edit ? "Apply edit area" : "Apply protection");
        Assert.True(_applied);
        Assert.Equal(RegionalEditMode.Edit, _saved!.Mode);
        Assert.Equal(initial.Strokes.Count + 1, _saved.Strokes.Count);
        Assert.Equal(!edit, _saved.Strokes[^1].Protect);
        var resultMasks = RegionalImageEdits.Masks(_saved);
        if (edit) Assert.Equal(originalMasks.Protect, resultMasks.Protect);
        else Assert.Equal(initial.Strokes.Where(s => !s.Protect).SelectMany(s => s.Points), _saved.Strokes.Where(s => !s.Protect).SelectMany(s => s.Points));
        Assert.Equal(originalMasks.Edit, RegionalImageEdits.Masks(initial).Edit);
        Assert.Equal(originalMasks.Protect, RegionalImageEdits.Masks(initial).Protect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingOneLayerPreservesTheOther(bool edit)
    {
        var initial = await Selection();
        Editor(edit, initial);
        await Click(edit ? "Remove edit area" : "Remove protection");
        Assert.True(_applied);
        Assert.NotNull(_saved);
        Assert.Equal(edit ? RegionalEditMode.Protect : RegionalEditMode.Edit, _saved.Mode);
        Assert.Equal(edit, Assert.Single(_saved.Strokes).Protect);
        Assert.Equal(initial.Context, _saved.Context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingTheOnlyLayerDisablesRegionalProcessing(bool edit)
    {
        var initial = await Selection(edit);
        if (edit) initial = initial with { Strokes = initial.Strokes.Where(s => !s.Protect).ToArray() };
        Editor(edit, initial);
        await Click(edit ? "Remove edit area" : "Remove protection");
        Assert.True(_applied);
        Assert.Null(_saved);
    }

    [Fact]
    public async Task ClearingProtectionCanBeUndoneAndKeepsTheEditSelection()
    {
        var initial = await Selection();
        Editor(false, initial);
        await Click("Clear protection");
        await Click("Undo");
        await Click("Apply protection");
        Assert.True(_applied);
        Assert.Equal(RegionalEditMode.Edit, _saved!.Mode);
        Assert.Equal(RegionalImageEdits.Masks(initial).Edit, RegionalImageEdits.Masks(_saved).Edit);
        Assert.Equal(RegionalImageEdits.Masks(initial).Protect, RegionalImageEdits.Masks(_saved).Protect);
    }

    [Fact]
    public async Task ApplyingClearedProtectionKeepsTheEditArea()
    {
        Editor(false, await Selection());
        await Click("Clear protection");
        await Click("Apply protection");
        Assert.True(_applied);
        Assert.Equal(RegionalEditMode.Edit, _saved!.Mode);
        Assert.False(Assert.Single(_saved.Strokes).Protect);
    }

    [Fact]
    public async Task ProtectionDoesNotAllowChangingTheInputCropAndCancelDoesNotPublish()
    {
        var initial = await Selection();
        var component = Editor(false, initial);
        Assert.DoesNotContain("Adjust input crop", _dialogs.Markup);
        Assert.DoesNotContain("Edit pixels", _dialogs.Markup);
        await component.InvokeAsync(() => component.Instance.Context(new() { X = 0, Y = 0, Width = .5, Height = 1 }));
        await component.InvokeAsync(() => component.Instance.Stroke(RegionalImageTests.Rect(true, .5, .5, .75, .75)));
        await Click("Cancel");
        Assert.True(_cancelled);
        Assert.False(_applied);
        Assert.Equal(1, initial.Context.Width);
        Assert.Equal(2, initial.Strokes.Count);
    }

    [Fact]
    public async Task AdditionalReferenceCanBeFullyProtected()
    {
        var component = Editor(false, await Selection(false), referenceOnly: true);
        await component.InvokeAsync(() => component.Instance.Stroke(RegionalImageTests.Rect(true, 0, 0, 1, 1)));
        await Click("Apply protection");
        Assert.True(_applied);
        Assert.All(RegionalImageEdits.Masks(_saved!).Protect, Assert.True);
    }

}
