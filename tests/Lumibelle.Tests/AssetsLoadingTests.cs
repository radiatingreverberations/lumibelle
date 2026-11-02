using Bunit;
using lumibelle.Components.Layout;
using lumibelle.Components.Pages;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task AssetsLoadingKeepsWorkspaceMountedWhileLibraryArrives()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _assets.BeforeLoad = () => release.Task;
        _assets.Library = _assets.Library with { Assets = [Asset("Mira")] };
        var page = Page();
        try
        {
            var workspace = page.FindComponent<StudioWorkspace>().Instance;
            Assert.Equal("true", page.Find(".studio-workspace").GetAttribute("aria-busy"));
            Assert.Single(page.FindAll(".workspace-left"));
            Assert.Single(page.FindAll(".workspace-center"));
            Assert.Single(page.FindAll(".workspace-right"));
            Assert.Empty(page.FindAll(".asset-empty, #asset-name, #image-prompt"));
            Assert.Single(page.FindAll(".workspace-center .loading-state"));
            Assert.Contains("Loading assets", page.Markup);
            Assert.Contains("Overview", _navigation.Markup);
            Assert.True(_actions.Find("button").HasAttribute("disabled"));

            await page.InvokeAsync(() => release.SetResult());
            page.WaitForElement("#asset-name");
            Assert.Equal("Mira", page.Find("#asset-name").GetAttribute("value"));
            Assert.Equal("false", page.Find(".studio-workspace").GetAttribute("aria-busy"));
            Assert.Same(workspace, page.FindComponent<StudioWorkspace>().Instance);
            Assert.DoesNotContain("Loading assets", page.Markup);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AssetsLoadingDefersDetailsLinkUntilProviderSetupCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _generator.BeforeCheck = () => { checking.TrySetResult(); return release.Task; };
        var asset = Asset("Mira", "Portrait notes");
        _assets.Library = _assets.Library with { Assets = [asset] };
        _dialogs = Render<MudBlazor.MudDialogProvider>();
        var page = Render<AssetsStudio>(p => p.Add(x => x.Id, _projectId)
            .Add(x => x.RequestedAssetId, asset.Id).Add(x => x.RequestedView, "details"));
        try
        {
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
            Assert.Equal("true", page.Find(".studio-workspace").GetAttribute("aria-busy"));
            Assert.Empty(page.FindAll("#asset-name, #image-prompt"));
            Assert.Empty(_dialogs.FindAll("#asset-details-name"));

            await page.InvokeAsync(() => release.SetResult());
            page.WaitForElement("#image-prompt");
            _dialogs.WaitForElement("#asset-details-name");
            Assert.Equal("Mira", _dialogs.Find("#asset-details-name").GetAttribute("value"));
            Assert.Equal("Portrait notes", page.Find("#image-prompt").GetAttribute("value"));
            Assert.Equal("false", page.Find(".studio-workspace").GetAttribute("aria-busy"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AssetsLoadingFailureCanRetryIntoWorkspace()
    {
        _assets.Library = _assets.Library with { Assets = [Asset("Mira")] };
        _assets.BeforeLoad = () => throw new WorkspaceStoreException("Library unavailable");
        var page = Page();
        page.WaitForAssertion(() => Assert.Contains("Library unavailable", page.Markup));
        Assert.Empty(page.FindAll(".studio-workspace"));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _assets.BeforeLoad = () => release.Task;
        try
        {
            var retry = page.Find("button").ClickAsync(new());
            page.WaitForElement(".studio-workspace[aria-busy='true']");
            await page.InvokeAsync(() => release.SetResult());
            await retry;
            page.WaitForElement("#asset-name");
            Assert.Equal("false", page.Find(".studio-workspace").GetAttribute("aria-busy"));
            Assert.DoesNotContain("Library unavailable", page.Markup);
        }
        finally { release.TrySetResult(); }
    }
}
