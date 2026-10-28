using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task MissingSavedEditLoraIsRetainedButClearlyDistinguishedFromDiscoveredFiles()
    {
        var initial = _settings.Value;
        var missing = initial.ComfyImageEditLora;
        _imageEditor.Loras = [];
        _imageGenerator.Result = _ => new(true, "Creation is ready.",
            [new(initial.ComfyImageModel, initial.ComfyImageModel)],
            [new(initial.ComfyImageTextEncoder, initial.ComfyImageTextEncoder)],
            [new(initial.ComfyImageVae, initial.ComfyImageVae)]);
        _imageEditor.Result = _ => new(false, $"Editing unavailable: identity-edit LoRA '{missing}'.", _imageEditor.Loras ?? []);
        var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-images").ClickAsync();
        await page.Find("#ai-image-Krea2").ClickAsync();
        Assert.Contains("saved selection", page.Find("#comfy-image-edit-lora option").TextContent);
        Assert.DoesNotContain("not confirmed", page.Find("#comfy-image-edit-lora option").TextContent);

        await Refresh();
        page.WaitForAssertion(() =>
        {
            Assert.Equal(missing, page.Find("#comfy-image-edit-lora").GetAttribute("value"));
            Assert.Contains("not confirmed available", page.Find("#comfy-image-edit-lora option").TextContent);
            var status = page.Find("#image-availability-status").TextContent;
            Assert.Contains("Create images:", status); Assert.Contains("Creation is ready.", status);
            Assert.Contains("Edit images:", status); Assert.Contains(missing, status);
            Assert.DoesNotContain("not confirmed", page.Find("#comfy-image-model option").TextContent);
        });
        Assert.Equal(initial, _settings.Value);

        // A later successful refresh removes the fallback label, without changing the saved choice.
        _imageEditor.Loras = [new(missing, missing)];
        _imageEditor.Result = _ => new(true, "Editing is ready.", _imageEditor.Loras ?? []);
        await Refresh();
        page.WaitForAssertion(() => Assert.Equal(missing, page.Find("#comfy-image-edit-lora option").TextContent));
        await page.Find("#comfy-image-edit-lora").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "another.safetensors" });
        Assert.DoesNotContain("Editing is ready.", page.Find("#image-availability-status").TextContent);
        Assert.Contains("Refresh image models", page.Find("#image-availability-status").TextContent);
        Assert.Equal(initial, _settings.Value);

        Task Refresh() => page.Find("#image-workflow-form").QuerySelectorAll("button")
            .Single(button => button.TextContent.Trim() == "Refresh image models").ClickAsync();
    }

    [Fact]
    public async Task FailedImageDiscoveryDoesNotClaimSavedFilesAreMissingAndWorkflowSwitchResetsCheck()
    {
        _imageGenerator.Result = _ => new(false, "Couldn’t reach ComfyUI.", [], [], []);
        _imageEditor.Result = _ => new(false, "Couldn’t reach ComfyUI.", []);
        var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-images").ClickAsync();
        await page.Find("#ai-image-Krea2").ClickAsync();
        await page.Find("#image-workflow-form").QuerySelectorAll("button")
            .Single(button => button.TextContent.Trim() == "Refresh image models").ClickAsync();
        page.WaitForAssertion(() => Assert.Contains("Couldn’t reach ComfyUI", page.Find("#image-availability-status").TextContent));
        Assert.Contains("not confirmed available", page.Find("#comfy-image-edit-lora option").TextContent);
        Assert.DoesNotContain("not found", page.Find("#comfy-image-edit-lora option").TextContent);
        await page.Find("#ai-image-MossImage").ClickAsync();
        Assert.Empty(page.FindAll("#image-availability-status"));
        Assert.Contains("saved selection", page.Find("#comfy-image-model option").TextContent);
    }
}
