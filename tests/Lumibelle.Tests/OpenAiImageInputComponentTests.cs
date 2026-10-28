using Bunit;
using lumibelle.Components;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AiModelComponentTests
{
    [Fact]
    public void VllmCatalogAndPickerDistinguishUnverifiedImageInput()
    {
        var model = new TextModelReference(AiBackend.Vllm, "test/model", "Local model") { VllmUrl = "http://llm-server:8000/v1" };
        _settings.Value = _settings.Value with { VllmUrl = model.VllmUrl!, TextDefault = model, StarredTextModels = [model] };
        _providers.Models = [new(model.Model, model.Name) { ImageInputKnown = false }];
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/ai?tab=text&provider=vllm");
        var page = Render<AiSettingsPage>();
        page.WaitForAssertion(() => Assert.Contains("Image input unverified", page.Find(".text-model-row").TextContent));
        var states = new List<TextModelSelectionState>();
        var picker = Picker(Guid.NewGuid(), TextAssistantStudio.Story, states);
        picker.WaitForAssertion(() => Assert.False(states.Last().ImageInputKnown));
        Assert.Contains("unverified", TextVisionPolicy.ImageIssue(states.Last()));
    }
}
