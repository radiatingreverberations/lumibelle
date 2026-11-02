using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetLinkIsConsumedAfterSelectionAndSelectionSurvivesReturning(bool editImage)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 3, Height = 2 };
        var target = Asset("Mira") with { Images = [image] };
        _assets.Library = _assets.Library with { Assets = [Asset("Room"), target] };
        await Services.GetRequiredService<WorkspacePositions>().LoadAsync(_projectId, "assets");
        var page = Page();
        page.WaitForAssertion(() => Assert.Contains("Room", page.Find(".asset-list-row.selected").TextContent));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var destination = $"/projects/{_projectId:D}/assets?view=details#references";
        var link = $"/projects/{_projectId:D}/assets?assetId={target.Id:D}&view=details" +
            (editImage ? $"&imageId={image.Id:D}&edit=true" : "") + "#references";
        await page.InvokeAsync(() => navigation.NavigateTo(navigation.ToAbsoluteUri(link).AbsoluteUri));
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedAssetId, target.Id)
            .Add(x => x.RequestedView, "details").Add(x => x.RequestedImageId, editImage ? image.Id : null)
            .Add(x => x.EditRequestedImage, editImage)));
        page.WaitForAssertion(() => Assert.Equal(destination, new Uri(navigation.Uri).PathAndQuery + new Uri(navigation.Uri).Fragment));
        Assert.Contains("Mira", page.Find(".asset-list-row.selected").TextContent);
        if (editImage) Assert.Equal("Edit image", page.Find(".asset-tool-shell .pane-title").TextContent);

        // Supply the cleaned query as the router does, without remounting the page.
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedAssetId, (Guid?)null)
            .Add(x => x.RequestedImageId, (Guid?)null).Add(x => x.EditRequestedImage, false)));
        Assert.True(_dialogs.Find(".asset-preservation").HasAttribute("open"));
        Assert.DoesNotContain("The requested image is unavailable", page.Markup);
        await page.InvokeAsync(() => navigation.NavigateTo(navigation.ToAbsoluteUri($"/projects/{_projectId:D}/script").AbsoluteUri));
        page.Dispose();
        navigation.NavigateTo(navigation.ToAbsoluteUri($"/projects/{_projectId:D}/assets").AbsoluteUri);
        page = Page(false);
        page.WaitForAssertion(() => Assert.Contains("Mira", page.Find(".asset-list-row.selected").TextContent));
        if (editImage) Assert.Equal("Edit image", page.Find(".asset-tool-shell .pane-title").TextContent);
        Assert.Equal(0, _assets.SaveCalls);
        Assert.Empty(_queue.View.Jobs);
    }
}
