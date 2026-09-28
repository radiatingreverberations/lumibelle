using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ComfyAccessPageTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static BunitContext Context(ComfyAccessFixture fixture, IComfyAccessProbe? probe = null)
    {
        var ui = new BunitContext();
        ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddSingleton<IComfyAccessCredentialStore>(fixture.Store);
        ui.Services.AddSingleton<IAiSettingsStore>(new AccessSettingsFake());
        ui.Services.AddSingleton<IComfyAccessProbe>(probe ?? new AccessProbeFake());
        return ui;
    }

    [Fact]
    public async Task OpeningShowsMetadataButNeverReloadsSavedTokenFields()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "saved-id.access", "saved-secret", 0, Ct);
        await using var ui = Context(f);
        var page = ui.Render<ComfyAccessPage>();
        page.WaitForElement("#comfy-access-client-id");
        Assert.Equal("", page.Find("#comfy-access-client-id").GetAttribute("value") ?? "");
        Assert.Equal("password", page.Find("#comfy-access-client-secret").GetAttribute("type"));
        Assert.DoesNotContain("saved-id.access", page.Markup); Assert.DoesNotContain("saved-secret", page.Markup);
        Assert.Contains("https://comfy.example", page.Markup);
    }

    [Fact]
    public async Task SaveClearsEnteredSecretsAndDoesNotChangeTheGenerationServer()
    {
        using var f = new ComfyAccessFixture();
        await using var ui = Context(f);
        var page = ui.Render<ComfyAccessPage>();
        page.WaitForElement("#comfy-access-url").Input("https://other.example/prefix");
        page.Find("#comfy-access-client-id").Input("new-id.access");
        page.Find("#comfy-access-client-secret").Input("new-secret");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Credentials saved.", page.Markup));
        Assert.DoesNotContain("new-id.access", page.Markup); Assert.DoesNotContain("new-secret", page.Markup);
        Assert.NotNull(await f.Store.ResolveAsync(new("wss://other.example/ws"), Ct));
        var settings = await ui.Services.GetRequiredService<IAiSettingsStore>().LoadAsync(Ct);
        Assert.Equal("https://comfy.example", settings.ComfyUrl);
    }

    [Fact]
    public async Task StaleSaveRetainsDraftAndReloadAllowsAnExplicitRetry()
    {
        using var f = new ComfyAccessFixture();
        await using var ui = Context(f);
        var page = ui.Render<ComfyAccessPage>();
        page.WaitForElement("#comfy-access-client-id").Input("draft-id");
        page.Find("#comfy-access-client-secret").Input("draft-secret");
        await f.Store.SaveAsync("https://comfy.example", "other-id", "other-secret", 0, Ct);
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("another window", page.Find("[role=alert]").TextContent));
        Assert.Equal("draft-secret", page.Find("#comfy-access-client-secret").GetAttribute("value"));
        Assert.Equal("other-secret", (await f.Store.ResolveAsync(new("https://comfy.example/prompt"), Ct))!.ClientSecret);
        Click(page, "Reload saved credentials");
        page.WaitForAssertion(() => Assert.Contains("reloaded", page.Markup));
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Credentials saved.", page.Markup));
        Assert.Equal("draft-secret", (await f.Store.ResolveAsync(new("https://comfy.example/prompt"), Ct))!.ClientSecret);
    }

    [Fact]
    public async Task RemovingRequiresConfirmationAndLeavesOtherOriginsAlone()
    {
        using var f = new ComfyAccessFixture();
        var first = await f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        await f.Store.SaveAsync("https://other.example", "other", "other-secret", first.Revision, Ct);
        await using var ui = Context(f);
        var page = ui.Render<ComfyAccessPage>();
        page.WaitForElement(".access-origin");
        await page.FindAll("button").First(b => b.TextContent == "Remove credentials").ClickAsync();
        Assert.Equal(2, (await f.Store.LoadAsync(Ct)).Entries.Count);
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel removal").ClickAsync();
        Assert.Equal(2, (await f.Store.LoadAsync(Ct)).Entries.Count);
        await page.FindAll("button").First(b => b.TextContent == "Remove credentials").ClickAsync();
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Confirm removal").ClickAsync();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".access-origin")));
        Assert.Equal("https://other.example", Assert.Single((await f.Store.LoadAsync(Ct)).Entries).Origin);
    }

    [Fact]
    public async Task TestUsesSavedCredentialsNotUnsubmittedTokenFieldsAndChecksBothTransports()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example", "saved-id", "saved-secret", 0, Ct);
        var checkedUrls = new List<string>();
        var probe = new AccessProbeFake(async (url, ct) =>
        {
            checkedUrls.Add(url);
            Assert.Equal("saved-secret", (await f.Store.ResolveAsync(new(url), ct))!.ClientSecret);
            return new(true, false, "HTTP passed", "WebSocket denied");
        });
        await using var ui = Context(f, probe);
        var page = ui.Render<ComfyAccessPage>();
        page.WaitForElement("#comfy-access-client-id").Input("unsaved-id");
        page.Find("#comfy-access-client-secret").Input("unsaved-secret");
        Click(page, "Test saved connection");
        page.WaitForAssertion(() => Assert.Contains("WebSocket denied", page.Markup));
        Assert.Contains("HTTP passed", page.Markup); Assert.Single(checkedUrls);
        page.Find("#comfy-access-url").Input("https://other.example");
        Assert.DoesNotContain("WebSocket denied", page.Markup);
    }

    private static void Click(IRenderedComponent<ComfyAccessPage> page, string text) =>
        page.FindAll("button").Single(b => b.TextContent.Trim() == text).Click();

    private sealed class AccessSettingsFake : IAiSettingsStore
    {
        public Task<AiSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AiSettings { ComfyUrl = "https://comfy.example" });
        public Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken ct = default) =>
            throw new InvalidOperationException("Access settings must not save generation settings.");
        public Task<string?> ReadOpenRouterKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class AccessProbeFake(Func<string, CancellationToken, Task<ComfyAccessCheck>>? check = null) : IComfyAccessProbe
    {
        public Task<ComfyAccessCheck> CheckAsync(string url, CancellationToken ct = default) => check is not null
            ? check(url, ct) : Task.FromResult(new ComfyAccessCheck(true, true, "HTTP passed", "WebSocket passed"));
    }
}
