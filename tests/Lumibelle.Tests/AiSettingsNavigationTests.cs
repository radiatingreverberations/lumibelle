using Bunit;
using lumibelle.Components.AI;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class AiSettingsNavigationTests : BunitContext
{
    private readonly FakeAiSettingsStore _settings = new();
    private readonly FakeProviders _providers = new();
    private readonly Lumibelle.Testing.MockCodexTransport _transport = new();
    private readonly PollClock _clock = new();
    public AiSettingsNavigationTests()
    {
        Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IAiSettingsStore>(_settings);
        Services.AddSingleton<IAiProviderRegistry>(_providers);
        Services.AddSingleton<IReferenceImageGenerator>(new FakeReferenceImageGenerator());
        Services.AddSingleton<IReferenceImageEditor>(new FakeReferenceImageEditor());
        Services.AddSingleton<IComfyLoraCatalog>(new FakeLoraCatalog());
        Services.AddSingleton<lumibelle.Services.Shots.IVideoGenerator>(new Lumibelle.Testing.MockVideoGenerator());
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<ICodexClient>(_ => new CodexClient(_transport, _clock));
    }
    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();
    private IRenderedComponent<AiSettingsPage> Open(string query = "")
    { Navigation.NavigateTo("/settings/ai" + query); return Render<AiSettingsPage>(); }

    [Fact]
    public void ConnectionsOpenFirstWithoutProviderRequests()
    {
        var page = Open();
        Assert.Equal(new[] { "Connections", "Text models", "Image models", "Video models", "LoRAs" },
            page.FindAll(".ai-settings-tabs button").Select(b => b.TextContent));
        Assert.Equal("true", page.Find("#ai-tab-connections").GetAttribute("aria-selected"));
        Assert.Equal("true", page.Find("#ai-provider-comfyui").GetAttribute("aria-selected"));
        Assert.Empty(page.FindAll("#openrouter-key, #codex-executable"));
        Assert.DoesNotContain("Your models, ready", page.Markup);
        Assert.Equal(0, _providers.CheckCalls); Assert.Empty(_transport.Calls);
    }

    [Theory]
    [InlineData("?tab=images", "images", "comfyui")]
    [InlineData("?tab=video", "video", "comfyui")]
    [InlineData("?tab=loras", "loras", "comfyui")]
    [InlineData("?tab=advanced", "text", "defaults")]
    [InlineData("?tab=text", "text", "comfyui")]
    [InlineData("?tab=connections&provider=codex", "connections", "codex")]
    [InlineData("?tab=unknown&provider=unknown", "connections", "comfyui")]
    public void NamedLinksSelectTheirSectionAndProvider(string query, string section, string provider)
    {
        var page = Open(query);
        Assert.Equal("true", page.Find($"#ai-tab-{section}").GetAttribute("aria-selected"));
        if (section == "connections") Assert.Equal("true", page.Find($"#ai-provider-{provider}").GetAttribute("aria-selected"));
        // Advanced is gone; its old links open the text Defaults & profiles entry.
        if (section == "text") Assert.Equal("true", page.Find($"#ai-text-provider-{provider}").GetAttribute("aria-selected"));
        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public void ProviderDraftsAndChecksSurviveTabsAndSaveIndependently()
    {
        var saved = _settings.Value;
        var page = Open();
        page.Find("#comfy-url").Change("http://draft.test:8188");
        page.Find("#connection-comfyui-form button").Click();
        var result = page.Find("#connection-comfyui-form [role=status]").TextContent;
        page.Find("#ai-provider-openrouter").Click();
        page.Find("#openrouter-key").Change("draft-key");
        page.Find("#ai-provider-codex").Click();
        page.Find("#codex-executable").Change("C:/tools/codex.exe");
        page.Find("#codex-concurrency").Change("3");
        page.Find("#ai-tab-images").Click(); page.Find("#ai-tab-connections").Click();
        Assert.Equal("true", page.Find("#ai-provider-codex").GetAttribute("aria-selected"));
        Assert.Equal("C:/tools/codex.exe", page.Find("#codex-executable").GetAttribute("value"));
        _settings.SaveError = new WorkspaceConflictException();
        page.Find("#ai-connection-codex form").Submit();
        Assert.Contains("Another tab", page.Markup);
        Assert.Equal("3", page.Find("#codex-concurrency").GetAttribute("value"));
        _settings.SaveError = null; page.Find("#ai-connection-codex form").Submit();
        Assert.Equal(3, _settings.Value.Codex.Concurrency);
        Assert.Equal(saved.ComfyUrl, _settings.Value.ComfyUrl); Assert.Equal("test-only-key", _settings.Key);
        page.Find("#ai-provider-comfyui").Click();
        Assert.Equal("http://draft.test:8188", page.Find("#comfy-url").GetAttribute("value"));
        Assert.Equal(result, page.Find("#connection-comfyui-form [role=status]").TextContent);
        Assert.True(page.Find("#ai-connection-codex").HasAttribute("hidden"));
        page.Find("#connection-comfyui-form").QuerySelectorAll("button").Single(b => b.TextContent == "Cancel").Click();
        page.Find("#ai-provider-openrouter").Click();
        Assert.Equal("draft-key", page.Find("#openrouter-key").GetAttribute("value"));
    }

    [Fact]
    public void SelectionUpdatesPreserveOriginAndRespondToQueryNavigation()
    {
        var origin = $"/projects/{Guid.NewGuid():D}/assets?asset=one#references";
        var page = Open("?returnUrl=" + Uri.EscapeDataString(origin));
        page.Find("#ai-provider-comfyui").KeyDown("ArrowDown");
        Assert.Equal("true", page.Find("#ai-provider-openrouter").GetAttribute("aria-selected"));
        page.Find("#ai-provider-openrouter").KeyDown("End");
        Assert.Equal("true", page.Find("#ai-provider-claudecode").GetAttribute("aria-selected"));
        page.Find("#ai-tab-connections").KeyDown("ArrowRight");
        // The URL keeps the page and tab; the provider selection stays in page state.
        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        Assert.Equal("text", query["tab"]); Assert.False(query.ContainsKey("provider"));
        Assert.Equal("true", page.Find("#ai-text-provider-claudecode").GetAttribute("aria-selected"));
        Assert.Equal(origin, query["returnUrl"]);
        Assert.Equal(origin, page.Find(".writing-back").GetAttribute("href"));
        Navigation.NavigateTo("/settings/ai?tab=connections&provider=openrouter");
        Assert.Equal("true", page.Find("#ai-provider-openrouter").GetAttribute("aria-selected"));
        page.Find("#ai-provider-openrouter").KeyDown("Home");
        page.Find("#ai-provider-comfyui").KeyDown("ArrowUp");
        Assert.Equal("true", page.Find("#ai-provider-claudecode").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task CodexAllowancePollsOnlyWhileItsPanelIsVisible()
    {
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true } };
        var page = Open("?tab=connections&provider=codex");
        await Services.GetRequiredService<ICodexClient>().CheckAsync(_settings.Value.Codex, Xunit.TestContext.Current.CancellationToken);
        var before = _transport.Calls.Count(c => c.Method == "account/rateLimits/read");
        var timer = Assert.Single(_clock.PollTimers);
        timer.Tick();
        page.WaitForAssertion(() => Assert.Equal(before + 1, _transport.Calls.Count(c => c.Method == "account/rateLimits/read")));
        var panel = page.FindComponent<CodexSettingsPanel>().Instance;
        page.Find("#ai-provider-comfyui").Click();
        Assert.True(timer.Disposed); Assert.Empty(page.FindComponents<lumibelle.Components.AI.CodexUsage>());
        timer.Tick(); Assert.Equal(before + 1, _transport.Calls.Count(c => c.Method == "account/rateLimits/read"));
        page.Find("#ai-provider-codex").Click();
        Assert.Same(panel, page.FindComponent<CodexSettingsPanel>().Instance);
        Assert.False(_clock.PollTimers.Last().Disposed);
        page.Find("#ai-tab-text").Click();
        Assert.True(_clock.PollTimers.Last().Disposed); Assert.Empty(page.FindComponents<lumibelle.Components.AI.CodexUsage>());
    }

    [Theory]
    [InlineData("", "Back to project")]
    [InlineData("/script", "Back to script")]
    [InlineData("/assets", "Back to assets")]
    [InlineData("/shots", "Back to shots")]
    [InlineData("/cut", "Back to cut")]
    [InlineData("/settings", "Back to project settings")]
    public void LinksReturnToEachProjectPageIncludingQueryAndFragment(string area, string label)
    {
        var origin = $"/projects/{Guid.NewGuid():D}{area}?selection=a%26b#details";
        Navigation.NavigateTo(origin);
        var link = AiSettingsNavigation.Link(Navigation);
        var query = QueryHelpers.ParseQuery(new Uri(new Uri("http://localhost"), link).Query);
        Assert.Equal(origin, query["returnUrl"]);
        Assert.Equal((origin, label), AiSettingsNavigation.Return(query["returnUrl"], null, null));
        Navigation.NavigateTo(link);
        Assert.Equal(link, AiSettingsNavigation.Link(Navigation));
    }

    [Theory]
    [InlineData("/projects/the-lantern-festival/shots", "Back to shots")]
    [InlineData("/projects/lumiere-%E5%85%89/assets?assetId=a#details", "Back to assets")]
    public void ReadableLocalProjectOriginsAreAccepted(string url, string label) => Assert.Equal((url, label), AiSettingsNavigation.Return(url, null, null));

    [Theory]
    [InlineData("https://outside.test/projects/00000000-0000-0000-0000-000000000001/assets")]
    [InlineData("//outside.test")]
    [InlineData("/projects/not%2Fa%2Fproject/assets")]
    [InlineData("/projects/00000000-0000-0000-0000-000000000001/unknown")]
    [InlineData("/projects/00000000-0000-0000-0000-000000000001//assets")]
    [InlineData("/projects/00000000-0000-0000-0000-000000000001/assets/../settings")]
    [InlineData("/projects/00000000-0000-0000-0000-000000000001/%2e%2e")]
    [InlineData("/projects/00000000-0000-0000-0000-000000000001/assets\\outside")]
    [InlineData("/settings/ai")]
    public void InvalidOriginsFallBackToLegacyContextOrProjects(string origin)
    {
        Assert.Equal(("/", "All projects"), AiSettingsNavigation.Return(origin, null, null));
        var id = Guid.NewGuid();
        Assert.Equal(($"/projects/{id:D}/shots", "Back to shots"), AiSettingsNavigation.Return(origin, id, "shots"));
        Assert.Equal(($"/projects/{id:D}/script", "Back to script"), AiSettingsNavigation.Return(null, id, null));
    }

    private sealed class PollClock : TimeProvider
    {
        public List<PollTimer> Timers { get; } = [];
        // The Codex client's own idle-close timer is not an allowance poll.
        public IEnumerable<PollTimer> PollTimers => Timers.Where(t => t.Callback.Target is not CodexClient);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new PollTimer(callback, state); Timers.Add(timer); return timer; }
    }
    private sealed class PollTimer(TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback => callback;
        public bool Disposed { get; private set; }
        public void Tick() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
