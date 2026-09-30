using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;

namespace Lumibelle.Tests;

public sealed class PromptEnhancerTests
{
    private readonly Guid _project = Guid.NewGuid();
    private readonly ReferenceAsset _asset = new() { Id = Guid.NewGuid(), Name = "Mouse", Description = "A drawn mouse" };
    private readonly FakeAiSettingsStore _settings = new() { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
    private readonly EnhancementProviders _providers = new();
    private FakeAssetStore Store => new(_project) { Library = new() { ProjectId = _project, Assets = [_asset] } };
    private PromptEnhancementContext Context => new() { ProjectId = _project, AssetId = _asset.Id, Prompt = "A mouse under a chair, with the sign \"ÖPPET\".", AssetName = _asset.Name, VisualNotes = _asset.Description, Workflow = ImageWorkflow.Krea2, MaximumReferences = 2 };
    private static TextModelReference Model => new(AiBackend.OpenRouter, "test/model", "Test vision");
    private static Task<List<PromptEnhancementUpdate>> Run(IPromptEnhancer enhancer, PromptEnhancementRequest request) => RunWithCancellation(enhancer, request, TestContext.Current.CancellationToken);
    private static async Task<List<PromptEnhancementUpdate>> RunWithCancellation(IPromptEnhancer enhancer, PromptEnhancementRequest request, CancellationToken ct)
    { List<PromptEnhancementUpdate> result = []; await foreach (var update in enhancer.EnhanceAsync(request, ct)) result.Add(update); return result; }

    [Theory]
    [InlineData(ImageWorkflow.Krea2, false, "krea-create-v1", "Turbo")]
    [InlineData(ImageWorkflow.Krea2, true, "krea-edit-v1", "ref")]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false, "klein-create-v1", "40-70")]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, true, "klein-edit-v1", "Klein")]
    public void ProfilesAreBundledAndRouteByOperation(ImageWorkflow workflow, bool edit, string id, string content)
    { Assert.Equal(id, PromptProfiles.Id(workflow, edit)); Assert.Contains(content, PromptProfiles.Read(workflow, edit), StringComparison.OrdinalIgnoreCase); }

    [Fact]
    public void ChangesSinceARequestAreNamedForTheAuthor()
    {
        var was = Context with { TargetLook = new(_asset.Id, "Mouse", "A drawn mouse", "", Guid.NewGuid(), "Coat", "Red coat", "") };
        Assert.Empty(PromptEnhancer.Changes(was, was.Capture()));
        Assert.Equal([PromptEnhancer.PromptEdited], PromptEnhancer.Changes(was, was with { Prompt = "Edited" }));
        Assert.Equal(["the aspect ratio, resolution or workflow options changed"], PromptEnhancer.Changes(was, was with { AspectRatio = "16:9" }));
        Assert.Equal(["the target look changed"], PromptEnhancer.Changes(was, was with { TargetLook = was.TargetLook! with { Description = "Blue coat" } }));
        // A look carries its asset's identity; changing that is named as an asset change.
        Assert.Equal(["the asset’s name or notes changed"], PromptEnhancer.Changes(was, was with { VisualNotes = "Changed", TargetLook = was.TargetLook! with { IdentityNotes = "Changed" } }));
        Assert.Equal(["the LoRAs or their trigger words changed"], PromptEnhancer.Changes(was, was with { ProtectedTriggers = ["mouse_token"] }));
        Assert.Equal([PromptEnhancer.PromptEdited], PromptEnhancer.Changes(was, was with { Prompt = "Edited mouse_token", ProtectedTriggers = ["mouse_token"] }));
        Assert.Equal(["other prompt settings changed"], PromptEnhancer.Changes(was, was with { ProjectId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task TextOnlyUsesCapturedModelSettingsAndFaithfulContextWithoutImages()
    {
        _providers.BeforeCheck = () => { _settings.Value = _settings.Value with { Temperature = 1.5f, OpenRouterModel = "other/model" }; };
        var context = Context with { ProtectedTriggers = ["mouse_token"], Prompt = Context.Prompt + " mouse_token" };
        _providers.Chat.Output = "{\"kind\":\"Prompt\",\"text\":\"A mouse under a chair, with a sign reading \\\"ÖPPET\\\". mouse_token\"}";
        var updates = await Run(new PromptEnhancer(_providers, _settings, Store), new(context, Model, FollowsDefault: true));
        Assert.Equal(PromptEnhancementKind.Prompt, updates[^1].Result!.Kind);
        Assert.Equal("test/model", _providers.CreatedModel); Assert.Null(_providers.Chat.Options!.Temperature); Assert.Null(_providers.Chat.Options.MaxOutputTokens);
        Assert.Empty(_providers.Chat.Messages.SelectMany(m => m.Contents).OfType<DataContent>());
        using var data = JsonDocument.Parse(_providers.Chat.Messages[^1].Text!);
        Assert.Equal(context.Prompt, data.RootElement.GetProperty("authorRequest").GetString());
        Assert.Equal(context.VisualNotes, data.RootElement.GetProperty("asset").GetProperty("VisualNotes").GetString());
        Assert.False(data.RootElement.GetProperty("referenceImagesAttached").GetBoolean());
        Assert.Equal("mouse_token", data.RootElement.GetProperty("explicitTriggersToPreserve")[0].GetString());
        Assert.Equal(0, _settings.SaveCalls);
    }

    [Fact]
    public async Task VisionSendsBothIndependentlyCroppedImagesInOrderAndSnapshotsMutableInputs()
    {
        var first = new AssetImage { Id = Guid.NewGuid(), FileName = "first.png", ContentType = "image/png", Width = 100, Height = 60 };
        var second = first with { Id = Guid.NewGuid(), Width = 40, Height = 80 };
        var store = Store; store.Library = store.Library with { Assets = [_asset with { Images = [first, second] }] };
        var refs = new List<PromptReference>
        {
            new(new(_asset.Id, first.Id), "Base", "Scene", new() { Width = .5 }),
            new(new(_asset.Id, second.Id), "Donor", "Outfit", new() { Y = .5, Height = .5 })
        };
        _providers.BeforeCheck = refs.Clear;
        var updates = await Run(new PromptEnhancer(_providers, _settings, store), new(Context with { IsEdit = true, References = refs }, Model, true));
        Assert.NotNull(updates[^1].Result);
        var images = _providers.Chat.Messages[^1].Contents.OfType<DataContent>().ToArray(); Assert.Equal(2, images.Length);
        var a = Image.Identify(images[0].Data.Span); var b = Image.Identify(images[1].Data.Span);
        Assert.Equal((50, 60), (a.Width, a.Height)); Assert.Equal((40, 40), (b.Width, b.Height));
        using var data = JsonDocument.Parse(_providers.Chat.Messages[^1].Text!);
        Assert.Equal("Base", data.RootElement.GetProperty("references")[0].GetProperty("Label").GetString());
        Assert.Equal(2, data.RootElement.GetProperty("references")[1].GetProperty("imageNumber").GetInt32());
    }

    [Fact]
    public async Task CompletePlainTextArmorRewriteAppearsAsAnApplicableSuggestionWithoutChangingItsText()
    {
        const string prompt = "The girl from image 1 now wearing the detailed fantasy armor from image 2, including the metal breastplate, pauldrons, waist guard, arm cuffs, and white-and-gold fabric accents adapted to fit her body, while keeping her long dark brown hair, facial features, expression, white polka-dot dress silhouette underneath where visible, bare legs and feet, full-body standing pose, and studio background.";
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "girl.png", ContentType = "image/png", Width = 40, Height = 60 };
        var armor = source with { Id = Guid.NewGuid(), FileName = "armor.png" };
        var store = Store; store.Library = store.Library with { Assets = [_asset with { Images = [source, armor] }] };
        var context = Context with { Workflow = ImageWorkflow.Flux2Klein9bKv, IsEdit = true,
            Prompt = "put the armor on the girl with the white dress, but keep her dark hair etc",
            References = [new(new(_asset.Id, source.Id), "Base", "", null), new(new(_asset.Id, armor.Id), "Reference 2", "", null)] };
        _providers.Chat.Output = prompt;
        var updates = await Run(new PromptEnhancer(_providers, _settings, store), new(context, Model));
        Assert.Null(updates[^1].Error);
        Assert.Equal(new(PromptEnhancementKind.Prompt, prompt), updates[^1].Result);
        Assert.Equal(prompt, string.Concat(updates.Select(u => u.Text)));
        Assert.Contains("no heading, explanation, JSON, or code fence", _providers.Chat.Messages[0].Text);
        Assert.DoesNotContain("exactly one complete JSON object", _providers.Chat.Messages[0].Text);
    }

    [Fact]
    public async Task LocalModelsKeepTheExplicitEnvelopeWhenTheTransportHasNoStopReason()
    {
        var model = new TextModelReference(AiBackend.ComfyUI, "test/model", "Local model", _settings.Value.ComfyUrl);
        _providers.Chat.Finish = null;
        var result = await Run(new PromptEnhancer(_providers, _settings, Store), new(Context, model, FollowsDefault: true));
        Assert.Equal(PromptEnhancementKind.Prompt, result[^1].Result!.Kind);
        Assert.Contains("Return exactly one complete JSON object", _providers.Chat.Messages[0].Text);
        Assert.DoesNotContain("no heading, explanation, JSON, or code fence", _providers.Chat.Messages[0].Text);
        _providers.Chat.Output = "{\"kind\":\"Prompt\",\"text\":\"unfinished";
        result = await Run(new PromptEnhancer(_providers, _settings, Store), new(Context, model, FollowsDefault: true));
        Assert.Null(result[^1].Result);
    }

    [Theory]
    [InlineData("NEEDS_INPUT: Which person should wear the armor?", PromptEnhancementKind.NeedsInput, "Which person should wear the armor?")]
    [InlineData("NEEDS_SETUP: Expand the canvas first.", PromptEnhancementKind.NeedsSetup, "Expand the canvas first.")]
    [InlineData("Which image contains the armor?", PromptEnhancementKind.NeedsInput, "Which image contains the armor?")]
    public async Task PlainClarificationAndSetupResponsesNeverBecomeApplicablePrompts(string raw, PromptEnhancementKind kind, string text)
    {
        _providers.Chat.Output = raw;
        var updates = await Run(new PromptEnhancer(_providers, _settings, Store), new(Context, Model));
        Assert.Equal(new(kind, text), updates[^1].Result);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData(null)]
    public async Task PlainTextRequiresANormalFinishAndRemainsInspectableOtherwise(string? finish)
    {
        _providers.Chat.Output = "The girl wearing the armor from image 2, keeping her dark hair.";
        _providers.Chat.Finish = finish is null ? null : new ChatFinishReason(finish);
        var updates = await Run(new PromptEnhancer(_providers, _settings, Store), new(Context, Model));
        Assert.Null(updates[^1].Result); Assert.NotNull(updates[^1].Error);
        Assert.Contains(updates, u => u.Text == _providers.Chat.Output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"kind\":\"Prompt\",\"text\":\"unfinished")]
    [InlineData("NEEDS_INPUT:")]
    [InlineData("NEEDS_SETUP")]
    [InlineData("The girl wearing...")]
    [InlineData("The girl wearing the armor," )]
    [InlineData("Prompt option one.\n\nPrompt option two.")]
    public void NativeOutputDoesNotRescueBrokenOrIncompleteResponses(string raw) =>
        Assert.Null(PromptEnhancer.Parse(raw, allowPlainText: true));

    [Fact]
    public void ReadsTheCompleteAnswerAfterALocalModelReasonsAloud()
    {
        const string raw = """
            The request asks to make the arms visible in a neutral, relaxed pose by his side. I should keep the identity and outfit the same.

            Prompt: "Relax the man's arms so they hang naturally at his sides instead of being tucked behind his back."

            That's faithful. Keep it concise.

            {"kind":"Prompt","text":"Relax the man's arms so they hang naturally at his sides rather than behind his back, keeping his stance, outfit, and background otherwise unchanged."}
            """;
        var result = PromptEnhancer.Parse(raw);
        Assert.Equal(PromptEnhancementKind.Prompt, result!.Kind);
        Assert.StartsWith("Relax the man's arms so they hang naturally at his sides rather than", result.Text);
        // An answer cut off by the reply limit is still not a prompt, and an earlier complete answer wins over it.
        Assert.Null(PromptEnhancer.Parse("I will answer.\n\n{\"kind\":\"Prompt\",\"text\":\"unfinished", allowPlainText: true));
        Assert.Equal("First", PromptEnhancer.Parse("{\"kind\":\"Prompt\",\"text\":\"First\"}\nWait, let me revise.\n{\"kind\":\"Prompt\",\"text\":\"Sec")!.Text);
    }

    [Fact]
    public void ParsesJsonWrappedInASingleCodeFence()
    {
        var result = PromptEnhancer.Parse("```json\n{\"kind\":\"Prompt\",\"text\":\"Wrapped description\"}\n```");
        Assert.NotNull(result);
        Assert.Equal(PromptEnhancementKind.Prompt, result.Kind);
        Assert.Equal("Wrapped description", result.Text);
    }

    [Fact]
    public async Task NativePromptStillPreservesExplicitTriggersAndQuotedLettering()
    {
        const string text = "A mouse_token holding a sign reading \"ÖPPET?\".";
        _providers.Chat.Output = text;
        var enhancer = new PromptEnhancer(_providers, _settings, Store);
        var request = new PromptEnhancementRequest(Context with { ProtectedTriggers = ["mouse_token"] }, Model);
        var result = await Run(enhancer, request);
        Assert.Equal(text, result[^1].Result!.Text);
        _providers.Chat.Output = "A mouse holding a sign.";
        result = await Run(enhancer, request);
        Assert.Null(result[^1].Result); Assert.Contains("trigger", result[^1].Error);
    }

    [Theory]
    [InlineData("Prompt")]
    [InlineData("NeedsInput")]
    [InlineData("NeedsSetup")]
    public void ParsesOnlyExplicitResultKinds(string kind) => Assert.Equal(kind, PromptEnhancer.Parse($"{{\"kind\":\"{kind}\",\"text\":\"Useful text\"}}")!.Kind.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("{\"kind\":\"Prompt\",\"text\":\"unfinished")]
    [InlineData("{\"kind\":\"0\",\"text\":\"text\"}")]
    [InlineData("{\"kind\":\"Prompt\",\"text\":\"\"}")]
    [InlineData("{\"kind\":\"Prompt\",\"text\":\"NEEDS_INPUT: What color?\"}")]
    [InlineData("{\"kind\":\"Prompt\",\"text\":\"a\",\"extra\":1}")]
    [InlineData("NEEDS_SETUP: change the setup")]
    public void RejectsUnapplyableOutput(string raw) => Assert.Null(PromptEnhancer.Parse(raw));

    [Fact]
    public async Task TruncationAndLostTriggersKeepRawOutputButBlockApplication()
    {
        _providers.Chat.Finish = ChatFinishReason.Length;
        var result = await Run(new PromptEnhancer(_providers, _settings, Store), new(Context, Model));
        Assert.Null(result[^1].Result); Assert.Contains("output limit", result[^1].Error); Assert.Contains(result, u => u.Text is not null);
        _providers.Chat.Finish = ChatFinishReason.Stop;
        result = await Run(new PromptEnhancer(_providers, _settings, Store), new(Context with { ProtectedTriggers = ["exact_trigger"] }, Model));
        Assert.Null(result[^1].Result); Assert.Contains("trigger", result[^1].Error);
    }

    [Fact]
    public async Task ModelDisappearanceAndLostVisionCapabilityNeverFallBack()
    {
        _providers.Models = [];
        var enhancer = new PromptEnhancer(_providers, _settings, Store);
        await Assert.ThrowsAsync<AiGenerationException>(() => Run(enhancer, new(Context, Model)));
        _providers.Models = [new(Model.Model, Model.Name)];
        await Assert.ThrowsAsync<AiGenerationException>(() => Run(enhancer, new(Context with { IsEdit = true }, Model, true)));
        Assert.Null(_providers.CreatedModel);
    }

    [Fact]
    public async Task MissingTrashedDuplicateAndInvalidCroppedReferencesBlockBeforeGeneration()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "base.png", ContentType = "image/png", Width = 10, Height = 10 };
        var store = Store; store.Library = store.Library with { Assets = [_asset with { Images = [image] }] };
        var reference = new PromptReference(new(_asset.Id, image.Id), "Base", "", null);
        var enhancer = new PromptEnhancer(_providers, _settings, store);
        foreach (var references in new IReadOnlyList<PromptReference>[] {
            [reference, reference], [reference with { Crop = new() { X = double.NaN } }],
            [reference with { Image = new(_asset.Id, Guid.NewGuid()) }], [reference with { Available = false }] })
            await Assert.ThrowsAsync<AiGenerationException>(() => Run(enhancer, new(Context with { IsEdit = true, References = references }, Model)));
        await store.DeleteImageAsync(_project, _asset.Id, image.Id, store.Library.Revision, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AiGenerationException>(() => Run(enhancer, new(Context with { IsEdit = true, References = [reference] }, Model)));
        Assert.Null(_providers.CreatedModel);
    }

    [Fact]
    public async Task CancellationAndProviderFailureDoNotReturnApplicableResult()
    {
        using var cancel = new CancellationTokenSource(); _providers.Chat.Wait = true;
        var operation = RunWithCancellation(new PromptEnhancer(_providers, _settings, Store), new(Context, Model), cancel.Token);
        await _providers.Chat.Started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        _providers.Chat.Wait = false; _providers.Chat.Fail = true;
        await Assert.ThrowsAsync<AiGenerationException>(() => Run(new PromptEnhancer(_providers, _settings, Store), new(Context, Model)));
    }

    [Fact]
    public async Task ActualOpenRouterTransportCarriesPrivateImagesAndAdvertisesVision()
    {
        using var handler = new ScriptedHttpHandler((request, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/models")
                ? "{\"data\":[{\"id\":\"test/model\",\"name\":\"Vision\",\"architecture\":{\"input_modalities\":[\"text\",\"image\"],\"output_modalities\":[\"text\"]}},{\"id\":\"text/only\",\"name\":\"Text\"}]}"
                : request.RequestUri.AbsolutePath.EndsWith("/key") ? "{}"
                : "{\"id\":\"chat-test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test/model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
                Encoding.UTF8, "application/json")
        }));
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), _settings, TestComfy.Monitor());
        var check = await registry.CheckAsync(AiBackend.OpenRouter, _settings.Value, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(check.Models.Single(m => m.Id == "test/model").SupportsImages);
        Assert.False(check.Models.Single(m => m.Id == "text/only").SupportsImages);
        using var client = await registry.CreateAsync(AiBackend.OpenRouter, Model.Model, _settings.Value, TestContext.Current.CancellationToken);
        var context = Context with { IsEdit = true, References = [new(new(_asset.Id, Guid.NewGuid()), "Base", "", null)] };
        var messages = PromptEnhancer.BuildMessages(new(context, Model, true), PromptProfiles.Read(context.Workflow, true), [AssetStoreTests.Png(8, 4)]);
        var response = await client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("done", response.Text);
        var body = handler.Requests.Single(r => r.Method == "POST").Body;
        Assert.Contains("data:image/png;base64,", body); Assert.DoesNotContain("/media/projects/", body);
        using var sent = JsonDocument.Parse(body);
        var content = sent.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
    }
}

internal sealed class EnhancementProviders : IAiProviderRegistry
{
    public readonly EnhancementChat Chat = new();
    public IReadOnlyList<AiModel> Models = [new("test/model", "Test vision", SupportsImages: true)];
    public Action? BeforeCheck;
    public string? CreatedModel;
    public Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default)
    { BeforeCheck?.Invoke(); return Task.FromResult(new AiConnectionCheck(true, "Available", Models, "v1")); }
    public Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default)
    { CreatedModel = model; return Task.FromResult<IChatClient>(Chat); }
    public IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(string model, AiSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(string model, AiSettings settings, ComfyTextModelTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
internal sealed class EnhancementChat : IChatClient
{
    public List<ChatMessage> Messages = [];
    public ChatOptions? Options;
    public string Output = "{\"kind\":\"Prompt\",\"text\":\"A drawn mouse beneath a wooden chair.\"}";
    public ChatFinishReason? Finish = ChatFinishReason.Stop;
    public bool Wait, Fail;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Messages = messages.ToList(); Options = options; Started.TrySetResult();
        if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (Fail) throw new HttpRequestException("offline");
        yield return new(ChatRole.Assistant, Output); yield return new() { FinishReason = Finish };
    }
}
