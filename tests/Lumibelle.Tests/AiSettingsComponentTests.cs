using Bunit;
using lumibelle.Components;
using lumibelle.Components.Pages;
using lumibelle.Components.Script;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using System.Runtime.CompilerServices;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed partial class AiSettingsComponentTests : BunitContext
{
    private readonly ModelTestQueueFixture _modelQueue = new();
    protected override void Dispose(bool disposing) { if (disposing) _modelQueue.Dispose(); base.Dispose(disposing); }
    private readonly FakeAiSettingsStore _settings = new();
    private readonly FakeProviders _providers = new();
    private readonly FakeReferenceImageGenerator _imageGenerator = new();
    private readonly FakeReferenceImageEditor _imageEditor = new();
    public AiSettingsComponentTests()
    {
        _modelQueue.Register(Services);
        Services.AddMudServices();
        Services.AddSingleton<IAiSettingsStore>(_settings); Services.AddSingleton<IAiProviderRegistry>(_providers);
        Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
        Services.AddSingleton<IReferenceImageGenerator>(_imageGenerator);
        Services.AddSingleton<IReferenceImageEditor>(_imageEditor);
        Services.AddSingleton<IComfyLoraCatalog>(new FakeLoraCatalog());
        Services.AddSingleton<ApplicationSession>(); Services.AddSingleton(TimeProvider.System); Services.AddSingleton<MarkdownRenderer>();
        Services.AddSingleton<lumibelle.Services.IProjectStore>(new FakeProjectStore());
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true).SetResult(true);
        Render<MudPopoverProvider>(); _modelQueue.Start(Services);
    }
    private IRenderedComponent<MudDialogProvider>? _testHost;
    private IRenderedComponent<ComfyModelDialog> TestDialog(string model)
    {
        var host = _testHost ??= Render<MudDialogProvider>();
        host.InvokeAsync(async () => await Services.GetRequiredService<IDialogService>().ShowAsync<ComfyModelDialog>("Model test", new DialogParameters
        {
            [nameof(ComfyModelDialog.Model)] = new TextModelReference(AiBackend.ComfyUI, model, model, _settings.Value.ComfyUrl),
            [nameof(ComfyModelDialog.SettingsSnapshot)] = _settings.Value,
            [nameof(ComfyModelDialog.Check)] = new AiConnectionCheck(true, "Connected", _providers.Models!, _providers.BackendVersion),
        })).GetAwaiter().GetResult();
        return host.FindComponent<ComfyModelDialog>();
    }
    [Fact]
    public void ImageWorkflowsKeepSeparateDraftsAndSaveOnlyTheirOwnSettings()
    {
        var initial = _settings.Value;
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-connections").Click(); page.Find("#comfy-url").Change("http://draft.invalid:8188");
        page.Find("#ai-tab-images").Click();
        Assert.Equal("true", page.Find($"#ai-image-{initial.DefaultImageWorkflow}").GetAttribute("aria-selected"));
        page.Find("#ai-image-Flux2Klein9bKv").Click();
        Assert.Empty(page.FindAll("#comfy-image-edit-lora"));
        Assert.Equal(initial.FluxKleinModel, page.Find("#comfy-image-model").GetAttribute("value"));
        _imageGenerator.DiffusionModels = [new("folder/flux-2-klein-9b-kv-fp8.safetensors", "Klein")];
        _imageGenerator.TextEncoders = [new(initial.FluxKleinTextEncoder, "Qwen3")];
        _imageGenerator.Vaes = [new(initial.FluxKleinVae, "FLUX2")];
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Refresh image models").Click();
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, _imageGenerator.CheckedSettings[0].DefaultImageWorkflow);
        Assert.Equal(initial.ComfyUrl, _imageGenerator.CheckedSettings[0].ComfyUrl);
        _settings.SaveError = new WorkspaceStoreException("Disk full"); page.Find("#image-workflow-form").Submit();
        Assert.Contains("Disk full", page.Markup); Assert.Equal(initial.FluxKleinModel, _settings.Value.FluxKleinModel);
        Assert.Equal("folder/flux-2-klein-9b-kv-fp8.safetensors", page.Find("#comfy-image-model").GetAttribute("value"));
        _settings.SaveError = null; page.Find("#image-workflow-form").Submit();
        // Saving a workflow stores its own models without making it the default.
        Assert.Equal("folder/flux-2-klein-9b-kv-fp8.safetensors", _settings.Value.FluxKleinModel);
        Assert.Equal(ImageWorkflow.Krea2, _settings.Value.DefaultImageWorkflow);
        Assert.Equal(initial.ComfyUrl, _settings.Value.ComfyUrl); Assert.Equal(initial.ComfyImageModel, _settings.Value.ComfyImageModel);
        page.Find("#ai-image-Krea2").Click();
        Assert.Equal(initial.ComfyImageModel, page.Find("#comfy-image-model").GetAttribute("value"));
        page.Find("#comfy-image-model").Change("draft/krea.safetensors");
        page.Find("#ai-image-Flux2Klein9bKv").Click(); page.Find("#ai-image-Krea2").Click();
        Assert.Equal("draft/krea.safetensors", page.Find("#comfy-image-model").GetAttribute("value"));
        page.Find("#image-workflow-form").QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        Assert.Equal(initial.ComfyImageModel, page.Find("#comfy-image-model").GetAttribute("value"));
        page.Find("#ai-image-defaults").Click();
        page.Find("#image-workflow").Change("Flux2Klein9bKv"); page.Find("#image-timeout").Change("900");
        page.Find("#image-defaults-form").Submit();
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, _settings.Value.DefaultImageWorkflow); Assert.Equal(900, _settings.Value.ImageTimeoutSeconds);
        Assert.Equal(initial.ComfyImageModel, _settings.Value.ComfyImageModel);
    }

    [Fact]
    public void SettingsShowConfiguredStateWithoutReturningSavedKey()
    {
        var projectId = Guid.NewGuid();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/settings/ai?projectId={projectId:D}");
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-connections").Click();
        page.Find("#ai-provider-openrouter").Click();
        Assert.Contains("API key is configured", page.Markup);
        Assert.DoesNotContain("test-only-key", page.Markup);
        Assert.Equal("text", page.Find("#openrouter-key").GetAttribute("type"));
        page.Find("#openrouter-key").Change("replacement-key");
        page.Find("#connection-openrouter-form").Submit();
        Assert.Equal("replacement-key", _settings.Key);
        Assert.DoesNotContain("replacement-key", page.Markup);
        Assert.Contains($"projectId={projectId:D}", navigation.Uri);
        Assert.Contains("tab=connections", navigation.Uri);
        Assert.Contains("OpenRouter connection saved", page.Markup);
    }

    [Fact]
    public void SettingsKeepSavedComfyModelReadOnlyUntilConnectionCheck()
    {
        var benchmark = new ComfyTextModelBenchmark(DateTimeOffset.UtcNow, "Saved GPU", 0, 20L << 30,
            1L << 30, 9L << 30, 256L << 20, 8L << 30, 256, 254, 4.32, true, false);
        _settings.Value = _settings.Value with
        {
            ComfyTextModelVerifications =
            [
                new(_settings.Value.ComfyUrl, "0.34.0", _settings.Value.ComfyModel, DateTimeOffset.UtcNow, [benchmark])
            ]
        };

        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();

        Assert.Contains(_settings.Value.ComfyModel, page.Markup);
        Assert.Contains("4.3 tokens/s", page.Markup);
        Assert.False(page.Find(".model-star").HasAttribute("disabled"));
        Assert.Empty(page.FindAll(".advanced-model-test"));
        page.Find("button[aria-label='Refresh ComfyUI models']").Click();
        Assert.False(page.Find(".model-star").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackFromSettingsDiscardsDraftAndReturnsToStoryOrHub(bool fromStory)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var projectId = Guid.NewGuid();
        navigation.NavigateTo(fromStory ? $"/settings/ai?projectId={projectId:D}" : "/settings/ai");
        var savedSettings = _settings.Value;
        var savedKey = _settings.Key;
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-connections").Click();
        page.Find("#comfy-url").Change("not a valid URL");
        page.Find("#ai-provider-openrouter").Click(); page.Find("#openrouter-key").Change("discard-this-key");
        navigation.NavigateTo(page.Find(".writing-back").GetAttribute("href")!);
        Assert.Equal(savedSettings, _settings.Value);
        Assert.Equal(savedKey, _settings.Key);
        Assert.Equal(fromStory ? $"http://localhost/projects/{projectId:D}/script" : "http://localhost/", navigation.Uri);
    }

    [Fact]
    public void ImageSettingsPersistAndReturnToAssets()
    {
        var navigation = Services.GetRequiredService<NavigationManager>(); var projectId = Guid.NewGuid();
        navigation.NavigateTo($"/settings/ai?projectId={projectId:D}&returnTo=assets");
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-images").Click();
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Refresh image models").Click();
        Assert.Equal(_settings.Value.ComfyImageModel, page.Find("#comfy-image-model").GetAttribute("value"));
        page.Find("#ai-image-defaults").Click(); page.Find("#image-timeout").Change("900"); page.Find("#image-defaults-form").Submit();
        Assert.Equal(900, _settings.Value.ImageTimeoutSeconds);
        Assert.Equal($"/projects/{projectId:D}/assets", page.Find(".writing-back").GetAttribute("href"));
        Assert.Contains("/settings/ai", navigation.Uri);
    }

    [Fact]
    public void ImageSettingsPreferDetectedIdentityEditV12AndPersistExactPath()
    {
        _settings.Value = _settings.Value with { ComfyImageEditLora = "missing.safetensors" };
        _imageEditor.Loras =
        [
            new("Krea2/krea2_identity_edit_experimental.safetensors", "Experimental"),
            new("Krea2/krea2_identity_edit_v1_2.safetensors", "Version 1.2")
        ];
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-images").Click();

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Refresh image models").Click();

        page.WaitForAssertion(() => Assert.Equal("Krea2/krea2_identity_edit_v1_2.safetensors",
            page.Find("#comfy-image-edit-lora").GetAttribute("value")));
        page.FindAll("form").Single(f => f.Closest("[hidden]") is null).Submit();
        Assert.Equal("Krea2/krea2_identity_edit_v1_2.safetensors", _settings.Value.ComfyImageEditLora);
    }

    [Fact]
    public void ImageEditReadinessIsRecheckedAfterSelectingDiscoveredLoraFallback()
    {
        const string imageModel = "models/krea2_turbo_int8_convrot.safetensors";
        const string encoder = "encoders/qwen3vl_4b_fp8_scaled.safetensors";
        const string vae = "vae/qwen_image_vae.safetensors";
        const string detected = "Krea2/krea2_identity_edit_v1_2.safetensors";
        _settings.Value = _settings.Value with
        {
            ComfyImageModel = "missing-model.safetensors", ComfyImageTextEncoder = "missing-encoder.safetensors",
            ComfyImageVae = "missing-vae.safetensors", ComfyImageEditLora = "missing-lora.safetensors"
        };
        _imageGenerator.DiffusionModels = [new(imageModel, "Krea 2")];
        _imageGenerator.TextEncoders = [new(encoder, "Qwen")];
        _imageGenerator.Vaes = [new(vae, "VAE")];
        _imageGenerator.Result = settings => new(
            settings.ComfyImageModel == imageModel && settings.ComfyImageTextEncoder == encoder && settings.ComfyImageVae == vae,
            settings.ComfyImageModel == imageModel ? "Base models ready after fallback." : "Configured base models unavailable.",
            _imageGenerator.DiffusionModels, _imageGenerator.TextEncoders, _imageGenerator.Vaes);
        _imageEditor.Loras = [new(detected, "Version 1.2")];
        _imageEditor.Result = settings => new(
            settings.ComfyImageEditLora == detected,
            settings.ComfyImageEditLora == detected ? "Ready after fallback." : "Configured LoRA unavailable.",
            _imageEditor.Loras);
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-images").Click();

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Refresh image models").Click();

        page.WaitForAssertion(() => Assert.Contains("Base models ready after fallback.", page.Markup));
        Assert.Contains("Ready after fallback.", page.Markup);
        Assert.Equal(2, _imageGenerator.CheckedSettings.Count);
        Assert.Equal("missing-model.safetensors", _imageGenerator.CheckedSettings[0].ComfyImageModel);
        Assert.Equal(imageModel, _imageGenerator.CheckedSettings[1].ComfyImageModel);
        Assert.Equal(2, _imageEditor.CheckedSettings.Count);
        Assert.Equal("missing-lora.safetensors", _imageEditor.CheckedSettings[0].ComfyImageEditLora);
        Assert.Equal(detected, _imageEditor.CheckedSettings[1].ComfyImageEditLora);
    }

    [Fact]
    public void UntestedComfyModelsCannotBeStarred()
    {
        _providers.Models = [new("qwen.safetensors", "Qwen")];
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-text").Click();
        page.Find("button[aria-label='Refresh ComfyUI models']").Click();
        Assert.True(page.Find("button[aria-label='Star Qwen']").HasAttribute("disabled"));
        Assert.Empty(_settings.Value.StarredTextModels);
        Assert.Equal("gemma4_e4b_it_fp8_scaled.safetensors", _settings.Value.ComfyModel);
    }

    [Fact]
    public void SuccessfulComfyModelTestEnablesSaveAndPersistsVerification()
    {
        const string model = "qwen3.5_9b_uncensored_int8_convrot.safetensors";
        _providers.Models = [new(_settings.Value.ComfyModel, "Gemma", AiModelVerificationState.Untested),
            new(model, "Qwen 3.5 9B", AiModelVerificationState.Untested)];
        var navigation = Services.GetRequiredService<NavigationManager>();
        var page = TestDialog(model);

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Contains("Test selected model", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Contains("Verified with ComfyUI 0.34.0", page.Markup));
        Assert.NotEqual(model, _settings.Value.ComfyModel);
        var verification = Assert.Single(_settings.Value.ComfyTextModelVerifications);
        Assert.Equal(model, verification.Model);
        Assert.Equal("0.34.0", verification.ComfyVersion);
        var benchmark = Assert.Single(verification.Benchmarks!);
        Assert.False(benchmark.CustomPrompt);
        Assert.Equal(256, benchmark.TokenLimit);
        Assert.Contains("16.0 tokens/s", page.Find(".model-benchmark").TextContent);
        Assert.Contains("8.0 GiB additional device VRAM", page.Find(".model-benchmark").TextContent);
        Assert.Equal("http://localhost/", navigation.Uri);
    }

    [Fact]
    public void SuccessfulModelTestAdoptsVersionReportedDuringExecution()
    {
        const string model = "qwen_3_4b.safetensors";
        _providers.BackendVersion = "0.33.0";
        _providers.Models = [new(model, "Qwen", AiModelVerificationState.Untested)];
        var page = TestDialog(model);
        _providers.VerificationVersion = "0.34.0";

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Contains("Test selected model", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Contains("Verified with ComfyUI 0.34.0", page.Markup));
        Assert.NotEmpty(_settings.Value.ComfyTextModelVerifications);
    }

    [Fact]
    public void AdvancedComfyModelTestUsesCustomMessageAndShowsSavedPlainTextResponse()
    {
        const string model = "qwen3.5_9b_uncensored_int8_convrot.safetensors";
        const string prompt = "Answer this spicy compatibility question directly.";
        _providers.Models = [new(model, "Qwen 3.5 9B", AiModelVerificationState.Untested)];
        _providers.TestResponse = "Allowed reply with <script>not executable</script>.";
        var page = TestDialog(model);
        page.Find("#advanced-test-prompt").Input(prompt);
        page.Find("#advanced-test-tokens").Input("384");

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Run advanced test").Click();

        page.WaitForAssertion(() => Assert.Equal(_providers.TestResponse, page.Find(".advanced-model-test-result pre").TextContent));
        Assert.Equal(prompt, _providers.LastTestPrompt);
        Assert.Equal(384, _providers.LastTestMaxTokens);
        Assert.Empty(page.FindAll(".advanced-model-test-result script"));
        page.WaitForAssertion(() => Assert.Contains("Verified with ComfyUI 0.34.0", page.Markup));
        Assert.Contains("384-token advanced test", page.Markup);
        Assert.NotEmpty(_settings.Value.ComfyTextModelVerifications);
    }

    [Fact]
    public void FailedAdvancedComfyModelTestRetainsMessageAndSelection()
    {
        const string model = "oldt5_xxl_fp8_e4m3fn_scaled.safetensors";
        const string prompt = "Keep this custom test message.";
        _providers.Models = [new(model, "T5", AiModelVerificationState.Untested)];
        _providers.VerificationFails = true;
        var page = TestDialog(model);
        page.Find("#advanced-test-prompt").Input(prompt);

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Run advanced test").Click();

        page.WaitForAssertion(() => Assert.Contains("cannot run the text workflow", page.Markup));
        Assert.Equal(prompt, page.Find("#advanced-test-prompt").GetAttribute("value"));
        Assert.Equal(model, page.Instance.Model.Model);
        Assert.Empty(_settings.Value.ComfyTextModelVerifications);
    }

    [Fact]
    public void FailedComfyModelTestRetainsSelectionAndKeepsSaveBlocked()
    {
        const string model = "oldt5_xxl_fp8_e4m3fn_scaled.safetensors";
        _providers.Models = [new(model, "T5", AiModelVerificationState.Untested)];
        _providers.VerificationFails = true;
        var page = TestDialog(model);

        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Contains("Test selected model", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => Assert.Contains("cannot run the text workflow", page.Markup));
        Assert.Equal(model, page.Instance.Model.Model);
        Assert.DoesNotContain("Cancel test", page.Markup);
        Assert.Empty(_settings.Value.ComfyTextModelVerifications);
    }

    [Fact]
    public async Task PendingComfyModelTestShowsProgressPreventsDuplicatesAndCanBeCancelled()
    {
        const string model = "qwen_3_8b_fp8mixed.safetensors";
        _providers.Models = [new(model, "Qwen 3 8B", AiModelVerificationState.Untested)];
        _providers.VerificationWaits = true;
        var page = TestDialog(model);

        var request = page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Contains("Test selected model", StringComparison.Ordinal)).ClickAsync(new());
        page.WaitForAssertion(() => Assert.Contains("0 / 256 tokens", page.Markup));
        Assert.Equal(1, _providers.VerificationCalls);
        Assert.True(page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Contains("Test selected model", StringComparison.Ordinal)).HasAttribute("disabled"));
        await page.InvokeAsync(() => page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(button => button.TextContent.Trim() == "Cancel test").ClickAsync(new()));
        await request;
        page.WaitForAssertion(() => Assert.Contains("Model test cancelled", page.Markup));
        Assert.Equal(1, _providers.VerificationCalls);
        Assert.Equal(model, page.Instance.Model.Model);
    }

}

internal sealed class FakeProviders : IAiProviderRegistry
{
    public Func<AiBackend, Task<AiConnectionCheck>>? CheckResult { get; set; }
    public List<AiBackend> CheckedBackends { get; } = [];
    public bool Success { get; set; } = true;
    public IReadOnlyList<AiModel>? Models { get; set; }
    public string BackendVersion { get; set; } = "0.34.0";
    public string? VerificationVersion { get; set; }
    public bool VerificationFails { get; set; }
    public bool VerificationWaits { get; set; }
    public TaskCompletionSource? VerificationRelease { get; set; }
    public CancellationToken VerificationToken { get; private set; }
    public int VerificationCalls { get; private set; }
    public int CheckCalls { get; private set; }
    public string? LastTestPrompt { get; private set; }
    public int? LastTestMaxTokens { get; private set; }
    public string TestResponse { get; set; } = "The model answered freely.";
    public Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default)
    {
        CheckCalls++;
        CheckedBackends.Add(backend);
        if (CheckResult is not null) return CheckResult(backend);
        return Task.FromResult(new AiConnectionCheck(Success, Success ? "Connected" : "Not configured",
            Models ?? [new(backend == AiBackend.ComfyUI ? settings.ComfyModel : settings.OpenRouterModel, "Configured model", AiModelVerificationState.Untested)],
            backend == AiBackend.ComfyUI ? BackendVersion : null));
    }
    public IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(string model, AiSettings settings,
        CancellationToken cancellationToken = default) =>
        RunTestAsync(model, settings, new(AiProviderRegistry.StandardBenchmarkPrompt, AiProviderRegistry.StandardBenchmarkTokens),
            includeResponse: false, cancellationToken);
    public IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(string model, AiSettings settings,
        ComfyTextModelTestRequest request, CancellationToken cancellationToken = default) =>
        RunTestAsync(model, settings, request, includeResponse: true, cancellationToken);
    private async IAsyncEnumerable<AiModelVerificationUpdate> RunTestAsync(string model, AiSettings settings,
        ComfyTextModelTestRequest request, bool includeResponse, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        VerificationCalls++; VerificationToken = cancellationToken;
        LastTestPrompt = request.Prompt; LastTestMaxTokens = request.MaxOutputTokens;
        yield return new(Progress: new(GenerationPhase.Generating, "Generating text", 0, request.MaxOutputTokens, "tokens", TimeSpan.FromSeconds(1)));
        if (VerificationRelease is not null) await VerificationRelease.Task.WaitAsync(cancellationToken);
        if (VerificationWaits) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (VerificationFails) throw new AiGenerationException("This model cannot run the text workflow.");
        var benchmark = new ComfyTextModelBenchmark(DateTimeOffset.UtcNow, "Test GPU", 0, 20L << 30,
            1L << 30, 9L << 30, 256L << 20, 8L << 30, request.MaxOutputTokens,
            request.MaxOutputTokens, 16, true, includeResponse);
        yield return new(Verification: new(AiProviderRegistry.NormalizeComfyUrl(settings.ComfyUrl), VerificationVersion ?? BackendVersion, model,
                DateTimeOffset.UtcNow, [benchmark]),
            Response: includeResponse ? TestResponse : null);
    }
}
internal sealed class FakeReferenceImageGenerator : IReferenceImageGenerator
{
    public IReadOnlyList<AiModel>? DiffusionModels { get; set; }
    public IReadOnlyList<AiModel>? TextEncoders { get; set; }
    public IReadOnlyList<AiModel>? Vaes { get; set; }
    public Func<AiSettings, ComfyImageConfiguration>? Result { get; set; }
    public List<AiSettings> CheckedSettings { get; } = [];
    public Task<ComfyImageConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= new();
        CheckedSettings.Add(settings);
        return Task.FromResult(Result?.Invoke(settings) ?? new ComfyImageConfiguration(true, "Ready",
            DiffusionModels ?? [new(settings.ComfyImageModel, "Krea 2")],
            TextEncoders ?? [new(settings.ComfyImageTextEncoder, "Qwen")],
            Vaes ?? [new(settings.ComfyImageVae, "VAE")]));
    }
    public async IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
}

internal sealed class FakeReferenceImageEditor : IReferenceImageEditor
{
    public IReadOnlyList<AiModel>? Loras { get; set; }
    public Func<AiSettings, ComfyImageEditConfiguration>? Result { get; set; }
    public List<AiSettings> CheckedSettings { get; } = [];
    public Task<ComfyImageEditConfiguration> CheckAsync(AiSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new();
        CheckedSettings.Add(settings);
        return Task.FromResult(Result?.Invoke(settings) ?? new ComfyImageEditConfiguration(true, "Ready",
            Loras ?? [new(settings.ComfyImageEditLora, "Krea 2 Identity Edit")]));
    }
    public async IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
}
