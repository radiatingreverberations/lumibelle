using Bunit;
using lumibelle.Components;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Shots;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class VideoSettingsSectionTests : BunitContext
{
    public VideoSettingsSectionTests()
    {
        Services.AddMudServices(); Services.AddSingleton<IVideoGenerator>(new Lumibelle.Testing.MockVideoGenerator());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task SectionsShareOneDraftButEachSavesOnlyItsOwnFields()
    {
        var settings = new AiSettings(); H3Settings? saved = null;
        var panel = Render<VideoSettingsPanel>(p => p.Add(c => c.Settings, settings)
            .Add(c => c.SaveSettings, h3 => { saved = h3; return Task.FromResult(true); }));
        const string attention = "select[aria-label='H3 dense attention']";
        panel.Find(attention).Change("Sage");
        panel.Render(p => p.Add(c => c.Section, AiProviderSidebar.DefaultsKey));
        Assert.Empty(panel.FindAll(attention));
        panel.Find("input[type=number]").Change("900"); panel.FindAll("input:not([type])")[0].Change("C:/tools/ffmpeg.exe");
        await panel.InvokeAsync(() => panel.Find("form").Submit());
        Assert.Equal(900, saved!.TimeoutSeconds); Assert.Equal("C:/tools/ffmpeg.exe", saved.Ffmpeg);
        Assert.Equal(H3AttentionBackend.ServerDefault, saved.Performance.Attention);
        Assert.Contains("Video defaults saved.", panel.Markup);
        // The unsaved H3 change is still in the draft and saves from its own section.
        panel.Render(p => p.Add(c => c.Section, VideoSettingsPanel.H3Section));
        Assert.Equal("Sage", panel.Find(attention).GetAttribute("value"));
        await panel.InvokeAsync(() => panel.Find("form").Submit());
        Assert.Equal(H3AttentionBackend.Sage, saved.Performance.Attention); Assert.Equal(settings.H3.TimeoutSeconds, saved.TimeoutSeconds);
        panel.Render(p => p.Add(c => c.Section, VideoSettingsPanel.RefinementSection));
        Assert.Empty(panel.FindAll("button[type=submit]"));
        Assert.Contains("under Preview upscaling", panel.Markup);
    }
}
