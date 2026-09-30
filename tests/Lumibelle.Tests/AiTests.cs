using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class AiTests
{
    [Fact]
    public void MarkdownPreviewKeepsFormattingAndRemovesExecutableAndEmbeddedContent()
    {
        var html = new MarkdownRenderer().Render("# Title\n\n**Strong** [safe](https://example.org) [bad](javascript:alert%281%29) ![external](https://example.org/pixel)\n\n<script>alert(1)</script>");
        Assert.Contains("<h1>Title</h1>", html); Assert.Contains("<strong>Strong</strong>", html);
        Assert.Contains("href=\"https://example.org\"", html);
        Assert.DoesNotContain("href=\"javascript:", html); Assert.DoesNotContain("<img", html); Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task OpenRouterUsesCompatibleStreamingEndpointAndNoImplicitRetry()
    {
        var stream = "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test/model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello \"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test/model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"forest\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") }));
        var settings = new FakeAiSettingsStore { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor());
        var assistant = new ScriptAssistant(registry, settings);
        var request = new ScriptAssistantRequest(new() { Backend = AiBackend.OpenRouter, Model = "test/model", Operation = WritingOperation.Revise }, new() { ProjectId = Guid.NewGuid() }, []);
        var result = new StringBuilder();
        await foreach (var update in assistant.GenerateAsync(request, TestContext.Current.CancellationToken)) result.Append(update.Text);
        Assert.Equal("Hello forest", result.ToString());
        var call = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/chat/completions", call.Path);
        using var body = JsonDocument.Parse(call.Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("test/model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Theory]
    [InlineData(401, "API key")]
    [InlineData(429, "rate limit")]
    [InlineData(400, "context size")]
    public async Task OpenRouterErrorsAreActionableAndNeverResubmitted(int status, string expected)
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"sensitive backend details\"}}") }));
        var settings = new FakeAiSettingsStore();
        var assistant = new ScriptAssistant(new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor()), settings);
        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var _ in assistant.GenerateAsync(new(new() { Backend = AiBackend.OpenRouter, Model = "test/model" }, new() { ProjectId = Guid.NewGuid() }, []), TestContext.Current.CancellationToken)) { }
        });
        Assert.Contains(expected, error.Message); Assert.DoesNotContain("sensitive", error.Message); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConnectionCheckAuthenticatesWithoutGeneratingAndFiltersModels()
    {
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/key") ? "{}" :
            "{\"data\":[{\"id\":\"text/model\",\"name\":\"Text\",\"architecture\":{\"output_modalities\":[\"text\"]}},{\"id\":\"image/model\",\"name\":\"Image\",\"architecture\":{\"output_modalities\":[\"image\"]}}]}")));
        var check = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.OpenRouter, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(check.Success); Assert.Equal("text/model", Assert.Single(check.Models).Id);
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Fact]
    public async Task OpenRouterDiscoveryIncludesOptionalCatalogMetadataWithoutGeneration()
    {
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/key") ? "{}" : """
            {"data":[{"id":"test/vision","name":"Vision writer","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},
            "context_length":128000,"supported_parameters":["reasoning_effort"],"pricing":{"prompt":"0.0000001","completion":"0.0000002"}},
            {"id":"test/unknown","name":"No metadata"}]}
            """)));
        var check = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.OpenRouter, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(check.Success); Assert.Equal(2, check.Models.Count);
        var model = check.Models.Single(m => m.Id == "test/vision");
        Assert.True(model.SupportsImages); Assert.True(model.Catalog!.SupportsReasoning);
        Assert.Equal(1, model.Catalog.PopularityOrder);
        Assert.Equal(128000, model.Catalog.ContextLength); Assert.Equal(0.0000001m, model.Catalog.Pricing!.InputPerToken);
        Assert.Null(check.Models.Single(m => m.Id == "test/unknown").Catalog!.Pricing!.InputPerToken);
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Fact]
    public async Task ComfyDiscoveryReturnsEveryExactClipLoaderModelAndUsesExactVerificationKey()
    {
        string[] names = ["gemma4_e4b_it_fp8_scaled.safetensors", "qwen_3_4b.safetensors", "oldt5_xxl_fp8_e4m3fn_scaled.safetensors",
            "clip_l.safetensors", "custom text encoder.bin"];
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("/object_info", StringComparison.Ordinal)
                ? ComfyCatalog(names)
                : ComfyStats("0.34.0"))));
        var settings = new AiSettings
        {
            ComfyUrl = "http://COMFY:80/base/",
            ComfyTextModelVerifications =
            [
                new("http://comfy/base", "0.34.0", "qwen_3_4b.safetensors", DateTimeOffset.UtcNow),
                new("http://comfy/base", "0.33.0", "clip_l.safetensors", DateTimeOffset.UtcNow),
                new("http://other/base", "0.34.0", "oldt5_xxl_fp8_e4m3fn_scaled.safetensors", DateTimeOffset.UtcNow)
            ]
        };

        var check = await new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.ComfyUI, settings, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(check.Success);
        Assert.Equal("0.34.0", check.BackendVersion);
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), check.Models.Select(model => model.Id));
        Assert.Equal(AiModelVerificationState.Verified, check.Models.Single(model => model.Id == "qwen_3_4b.safetensors").Verification);
        Assert.All(check.Models.Where(model => model.Id != "qwen_3_4b.safetensors"),
            model => Assert.Equal(AiModelVerificationState.Untested, model.Verification));
    }

    [Fact]
    public async Task ComfyProviderCanCreateClientForNonGemmaModel()
    {
        var registry = new AiProviderRegistry(new TestHttpFactory(new ScriptedHttpHandler((_, _) =>
            Task.FromResult(JsonResponse("{}")))), new FakeAiSettingsStore(), TestComfy.Monitor());

        using var client = await registry.CreateAsync(AiBackend.ComfyUI, "qwen3.5_9b_uncensored_int8_convrot.safetensors", new(),
            TestContext.Current.CancellationToken);

        Assert.IsType<ComfyChatClient>(client);
    }

    [Fact]
    public async Task ComfyModelProbeUsesCacheClearedStandardBenchmarkAndAcceptsEmptyOutput()
    {
        var promptId = Guid.NewGuid().ToString("D");
        const string model = "qwen_3_4b.safetensors";
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/object_info" => ComfyCatalog(model),
            "/system_stats" => ComfyStats("0.34.0"),
            "/queue" => "{\"queue_running\":[],\"queue_pending\":[]}",
            "/free" => "{}",
            "/prompt" => $"{{\"prompt_id\":\"{promptId}\"}}",
            _ => $"{{\"{promptId}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"3\":{{\"text\":[\"\"]}}}}}}}}"
        })));
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor());
        var updates = new List<AiModelVerificationUpdate>();

        await foreach (var update in registry.VerifyComfyTextModelAsync(model, new(), TestContext.Current.CancellationToken))
            updates.Add(update);

        var verification = Assert.Single(updates, update => update.Verification is not null).Verification!;
        Assert.Equal(model, verification.Model);
        Assert.Equal("0.34.0", verification.ComfyVersion);
        var benchmark = Assert.Single(verification.Benchmarks!);
        Assert.Equal(2048, benchmark.TokenLimit);
        Assert.Equal(ComfyTextBenchmark.ContextTokens, benchmark.ContextTokens);
        Assert.False(benchmark.CustomPrompt);
        Assert.True(benchmark.CacheClearConfirmed);
        // The benchmark comes first; a second run with the same prompt measures memory per token.
        using var submitted = JsonDocument.Parse(handler.Requests.First(request => request.Path == "/prompt").Body);
        var workflow = submitted.RootElement.GetProperty("prompt");
        Assert.Equal(model, workflow.GetProperty("1").GetProperty("inputs").GetProperty("clip_name").GetString());
        Assert.Equal(2048, workflow.GetProperty("2").GetProperty("inputs").GetProperty("max_length").GetInt32());
        Assert.False(workflow.GetProperty("2").GetProperty("inputs").GetProperty("thinking").GetBoolean());
        // The benchmark reserves a script-sized context so peak VRAM reflects real requests.
        Assert.Equal(ComfyTextBenchmark.Prompt(AiProviderRegistry.StandardBenchmarkPrompt), workflow.GetProperty("2").GetProperty("inputs").GetProperty("prompt").GetString());
        Assert.True(Guid.TryParse(submitted.RootElement.GetProperty("client_id").GetString(), out _));
        var free = handler.Requests.Single(request => request.Path == "/free");
        using var freeBody = JsonDocument.Parse(free.Body);
        Assert.True(freeBody.RootElement.GetProperty("unload_models").GetBoolean());
        Assert.True(freeBody.RootElement.GetProperty("free_memory").GetBoolean());
        Assert.True(handler.Requests.IndexOf(free) < handler.Requests.FindIndex(request => request.Path == "/prompt"));
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("interrupt", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(384)] [InlineData(32768)]
    public async Task AdvancedComfyModelTestUsesCustomPromptLimitAndReturnsText(int tokenLimit)
    {
        var promptId = Guid.NewGuid().ToString("D");
        const string model = "qwen3.5_9b_uncensored_int8_convrot.safetensors";
        const string prompt = "Respond to this exact compatibility prompt.";
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/object_info" => ComfyCatalog(model),
            "/system_stats" => ComfyStats("0.34.0"),
            "/queue" => "{\"queue_running\":[],\"queue_pending\":[]}",
            "/free" => "{}",
            "/prompt" => $"{{\"prompt_id\":\"{promptId}\"}}",
            _ => $"{{\"{promptId}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"3\":{{\"text\":[\"A direct answer.\"]}}}}}}}}"
        })));
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor());
        var updates = new List<AiModelVerificationUpdate>();

        await foreach (var update in registry.TestComfyTextModelAsync(model,
            new AiSettings { Temperature = 0.85f }, new(prompt, tokenLimit), TestContext.Current.CancellationToken))
            updates.Add(update);

        var completed = Assert.Single(updates, update => update.Verification is not null);
        Assert.Equal("A direct answer.", completed.Response);
        Assert.Equal(model, completed.Verification!.Model);
        Assert.True(Assert.Single(completed.Verification.Benchmarks!).CustomPrompt);
        using var submitted = JsonDocument.Parse(handler.Requests.Single(request => request.Path == "/prompt").Body);
        var inputs = submitted.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs");
        Assert.Equal(prompt, inputs.GetProperty("prompt").GetString());
        Assert.Equal(tokenLimit, inputs.GetProperty("max_length").GetInt32());
        Assert.Equal(0.85, inputs.GetProperty("sampling_mode.temperature").GetDouble(), 2);
    }

    [Fact]
    public async Task ComfyModelProbeRejectsMissingCatalogEntryWithoutSubmitting()
    {
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath == "/object_info" ? ComfyCatalog("available.safetensors") : ComfyStats("0.34.0"))));
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor());

        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var _ in registry.VerifyComfyTextModelAsync("missing.safetensors", new(), TestContext.Current.CancellationToken)) { }
        });

        Assert.Contains("no longer", error.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task ComfyModelBenchmarkRefusesToClearCachesWhileQueueIsBusy()
    {
        const string model = "qwen_3_4b.safetensors";
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/object_info" => ComfyCatalog(model),
            "/system_stats" => ComfyStats("0.34.0"),
            "/queue" => "{\"queue_running\":[[1,\"other-prompt\"]],\"queue_pending\":[]}",
            _ => "{}"
        })));
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), TestComfy.Monitor());

        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var _ in registry.VerifyComfyTextModelAsync(model, new(), TestContext.Current.CancellationToken)) { }
        });

        Assert.Contains("busy", error.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Path is "/free" or "/prompt");
    }

    [Fact]
    public async Task ComfyModelBenchmarkRecordsObservedTokenRateAndMemoryPeak()
    {
        const string model = "qwen_3_4b.safetensors";
        var statsCalls = 0;
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/object_info" => ComfyCatalog(model),
            "/system_stats" => ++statsCalls == 1
                ? ComfyStats("0.34.0", 10L << 30, 10L << 30, 1L << 30)
                : statsCalls <= 4
                    ? ComfyStats("0.34.0", 19L << 30, 256L << 20, 128L << 20)
                    : ComfyStats("0.34.0", 11L << 30, 9L << 30, 1L << 30),
            "/queue" => "{\"queue_running\":[],\"queue_pending\":[]}",
            "/free" => "{}",
            _ => "{}"
        })));
        var monitor = new BenchmarkComfyMonitor();
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), monitor);

        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(model, new(), TestContext.Current.CancellationToken))
            verification = update.Verification ?? verification;

        var benchmark = Assert.Single(verification!.Benchmarks!);
        Assert.Equal(16, benchmark.TokensPerSecond);
        Assert.Equal(256, benchmark.GeneratedTokens);
        Assert.Equal(1L << 30, benchmark.BaselineVramUsedBytes);
        Assert.Equal(9L << 30, benchmark.PeakVramUsedBytes);
        Assert.Equal(8L << 30, benchmark.PeakTorchAllocatedBytes);
        Assert.True(benchmark.CacheClearConfirmed);
        using var workflow = JsonDocument.Parse(JsonSerializer.Serialize(monitor.Workflows[0]));
        Assert.Equal(2048, workflow.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs").GetProperty("max_length").GetInt32());
    }

    [Fact]
    public async Task ComfyModelBenchmarkKeepsTokenRateWhenMemoryStatsAreUnavailable()
    {
        const string model = "qwen_3_4b.safetensors";
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/object_info" => ComfyCatalog(model),
            "/system_stats" => JsonSerializer.Serialize(new { system = new { comfyui_version = "0.34.0" }, devices = Array.Empty<object>() }),
            "/queue" => "{\"queue_running\":[],\"queue_pending\":[]}",
            "/free" => "{}",
            _ => "{}"
        })));
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), new BenchmarkComfyMonitor());

        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(model, new(), TestContext.Current.CancellationToken))
            verification = update.Verification ?? verification;

        var benchmark = Assert.Single(verification!.Benchmarks!);
        Assert.Equal(16, benchmark.TokensPerSecond);
        Assert.Equal(256, benchmark.GeneratedTokens);
        Assert.Null(benchmark.DeviceName);
        Assert.Null(benchmark.VramTotalBytes);
    }

    [Fact]
    public async Task SlowOptionalMemorySampleDoesNotBlockCompletedBenchmark()
    {
        const string model = "qwen_3_4b.safetensors";
        var statsCalls = 0;
        var handler = new ScriptedHttpHandler(async (request, cancellationToken) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/object_info": return JsonResponse(ComfyCatalog(model));
                case "/queue": return JsonResponse("{\"queue_running\":[],\"queue_pending\":[]}");
                case "/free": return JsonResponse("{}");
                case "/system_stats" when ++statsCalls <= 4: return JsonResponse(ComfyStats("0.34.0"));
                case "/system_stats":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("The sampler should be cancelled after generation completes.");
                default: return JsonResponse("{}");
            }
        });
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), new BenchmarkComfyMonitor());

        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(model, new(), TestContext.Current.CancellationToken))
            verification = update.Verification ?? verification;

        Assert.NotNull(verification);
        Assert.Equal(16, Assert.Single(verification.Benchmarks!).TokensPerSecond);
    }

    [Fact]
    public async Task StalledPostClearMemoryProbeFallsBackToTokenOnlyBenchmark()
    {
        const string model = "qwen_3_4b.safetensors";
        var statsCalls = 0;
        var handler = new ScriptedHttpHandler(async (request, cancellationToken) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/object_info": return JsonResponse(ComfyCatalog(model));
                case "/system_stats" when ++statsCalls == 1: return JsonResponse(ComfyStats("0.34.0"));
                case "/system_stats":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("The optional memory probe should time out independently.");
                case "/queue": return JsonResponse("{\"queue_running\":[],\"queue_pending\":[]}");
                case "/free": return JsonResponse("{}");
                default: return JsonResponse("{}");
            }
        });
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), new FakeAiSettingsStore(), new BenchmarkComfyMonitor());

        ComfyTextModelVerification? verification = null;
        await foreach (var update in registry.VerifyComfyTextModelAsync(model, new(), TestContext.Current.CancellationToken))
            verification = update.Verification ?? verification;

        var benchmark = Assert.Single(verification!.Benchmarks!);
        Assert.Equal(16, benchmark.TokensPerSecond);
        Assert.Null(benchmark.DeviceName);
    }

    [Fact]
    public async Task ComfyWorkflowUsesNativeNodesAndExtractsOnlyItsOwnOutput()
    {
        var id = Guid.NewGuid().ToString("D");
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.Method == HttpMethod.Post ? $"{{\"prompt_id\":\"{id}\"}}" :
            $"{{\"{id}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"3\":{{\"text\":[\"A lantern glowed.\"]}}}}}}}}")));
        using var client = new ComfyChatClient(new HttpClient(handler) { BaseAddress = new Uri("http://comfy/") }, "gemma4_test.safetensors", TestComfy.Monitor());
        var result = await client.GetResponseAsync([new(ChatRole.User, "A seed")], new() { MaxOutputTokens = 64 }, TestContext.Current.CancellationToken);
        Assert.Equal("A lantern glowed.", result.Text);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var nodes = body.RootElement.GetProperty("prompt");
        Assert.Equal("stable_diffusion", nodes.GetProperty("1").GetProperty("inputs").GetProperty("type").GetString());
        Assert.Equal("TextGenerate", nodes.GetProperty("2").GetProperty("class_type").GetString());
        Assert.False(nodes.GetProperty("2").GetProperty("inputs").GetProperty("thinking").GetBoolean());
        Assert.Equal(64, nodes.GetProperty("2").GetProperty("inputs").GetProperty("max_length").GetInt32());
        Assert.Equal($"/history/{id}", handler.Requests[1].Path);
    }

    [Theory]
    [InlineData("\"status\":{\"status_str\":\"error\"}", "could not execute")]
    [InlineData("\"status\":{\"completed\":true}", "without the expected")]
    [InlineData("\"outputs\":{\"3\":{\"text\":[\"\"]}}", "empty response")]
    public async Task ComfyExecutionFailuresDoNotBecomeSuccessfulText(string job, string message)
    {
        var id = Guid.NewGuid().ToString("D");
        var handler = new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(request.Method == HttpMethod.Post ? $"{{\"prompt_id\":\"{id}\"}}" : $"{{\"{id}\":{{\"status\":{{\"completed\":true}}, {job}}}}}")));
        using var client = new ComfyChatClient(new HttpClient(handler) { BaseAddress = new Uri("http://comfy/") }, "gemma4_test.safetensors", TestComfy.Monitor());
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => client.GetResponseAsync([new(ChatRole.User, "seed")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData(AiBackend.ComfyUI)]
    [InlineData(AiBackend.OpenRouter)]
    public async Task DeadlineStopsWaitingWithoutRetryingOrInterruptingUnrelatedJobs(AiBackend backend)
    {
        var id = Guid.NewGuid().ToString("D");
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/prompt") return JsonResponse($"{{\"prompt_id\":\"{id}\"}}");
            if (path.EndsWith("/cancel")) return new(HttpStatusCode.NotFound);
            if (path == "/queue") return JsonResponse("{}");
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("The deadline must cancel this request.");
        });
        var settings = new FakeAiSettingsStore { Value = new() { TimeoutSeconds = 5 } };
        var assistant = new ScriptAssistant(new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor()), settings);
        var run = new AssistantRun { Backend = backend, Model = backend == AiBackend.ComfyUI ? "gemma4_test.safetensors" : "test/model" };
        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var _ in assistant.GenerateAsync(new(run, new() { ProjectId = Guid.NewGuid() }, []), TestContext.Current.CancellationToken)) { }
        });
        Assert.Contains("No new text or generation progress", error.Message);
        if (backend == AiBackend.ComfyUI)
        {
            Assert.Contains("may continue", error.Message);
            Assert.Equal(new[] { "/prompt", $"/history/{id}", $"/api/jobs/{id}/cancel", "/queue" }, handler.Requests.Select(request => request.Path));
            Assert.Contains(id, handler.Requests.Last().Body);
        }
        else Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComfyCancellationTargetsOnlyItsOwnJob(bool supported)
    {
        var id = Guid.NewGuid().ToString("D");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/prompt") return JsonResponse($"{{\"prompt_id\":\"{id}\"}}");
            if (request.Method == HttpMethod.Get) { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return new(request.RequestUri.AbsolutePath.EndsWith("/cancel") && !supported ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        });
        using var client = new ComfyChatClient(new HttpClient(handler) { BaseAddress = new Uri("http://comfy/") }, "gemma4_test.safetensors", TestComfy.Monitor());
        var error = await Assert.ThrowsAsync<AiCancellationException>(() => client.GetResponseAsync([new(ChatRole.User, "seed")], cancellationToken: cancellation.Token));
        Assert.Contains(handler.Requests, request => request.Path == $"/api/jobs/{id}/cancel");
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("interrupt"));
        if (!supported) { Assert.Contains("may continue", error.Message); Assert.Contains(id, handler.Requests.Last().Body); }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string ComfyCatalog(params string[] models) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["CLIPLoader"] = new { input = new { required = new { clip_name = new object[] { models, new { } } } } },
        ["TextGenerate"] = new { input = new { required = new { max_length = new object[] { "INT", new { max = 32768 } } } } },
        ["PreviewAny"] = new { }
    });

    private static string ComfyStats(string version, long vramFree = 19L << 30, long torchTotal = 256L << 20,
        long torchFree = 128L << 20) => JsonSerializer.Serialize(new
        {
            system = new { comfyui_version = version },
            devices = new[] { new { name = "Test GPU", type = "cuda", index = 0, vram_total = 20L << 30,
                vram_free = vramFree, torch_vram_total = torchTotal, torch_vram_free = torchFree } }
        });
}

internal sealed class BenchmarkComfyMonitor : IComfyExecutionMonitor
{
    public object? Workflow => Workflows.LastOrDefault();
    public List<object> Workflows { get; } = [];

    public async IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory,
        ComfyExecutionOptions options, [EnumeratorCancellation] CancellationToken operationToken,
        CancellationToken callerToken)
    {
        Workflows.Add(workflowFactory("benchmark-client"));
        await Task.Yield();
        yield return new(new(GenerationPhase.Generating, "Generating text", 0, 256, "tokens", TimeSpan.FromSeconds(2)));
        yield return new(new(GenerationPhase.Generating, "Generating text", 128, 256, "tokens", TimeSpan.FromSeconds(14)));
        yield return new(new(GenerationPhase.Generating, "Generating text", 256, 256, "tokens", TimeSpan.FromSeconds(22)));
        using var job = JsonDocument.Parse("{\"status\":{\"completed\":true,\"status_str\":\"success\"}}");
        yield return new(new(GenerationPhase.Completed, "Completed", Elapsed: TimeSpan.FromSeconds(22)),
            "benchmark-prompt", job.RootElement.Clone(), true);
    }
}

internal sealed class FakeAiSettingsStore : IAiSettingsStore
{
    public Exception? LoadError { get; set; }
    public Exception? SaveError { get; set; }
    public Func<Task>? BeforeSave { get; set; }
    public int SaveCalls { get; private set; }
    public AiSettings Value { get; set; } = new() { HasOpenRouterKey = true };
    public string? Key { get; set; } = "test-only-key";
    public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) => LoadError is { } error ? Task.FromException<AiSettings>(error) : Task.FromResult(Value);
    public Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Key);
    public async Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken cancellationToken = default)
    { SaveCalls++; if (BeforeSave is not null) await BeforeSave(); if (SaveError is not null) throw SaveError; if (removeKey) Key = null; else if (!string.IsNullOrWhiteSpace(replacementKey)) Key = replacementKey; Value = settings with { HasOpenRouterKey = Key is not null }; return Value; }
}

internal sealed class TestHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}
internal sealed record CapturedHttpRequest(string Method, string Path, string Body);
internal sealed class ScriptedHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public List<CapturedHttpRequest> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
        return await send(request, cancellationToken);
    }
}

internal static class TestComfy
{
    public static IComfyExecutionMonitor Monitor(TimeProvider? clock = null) =>
        new ComfyExecutionMonitor(new UnavailableComfyWebSocketFactory(), clock ?? TimeProvider.System);
}

internal sealed class UnavailableComfyWebSocketFactory : IComfyWebSocketFactory
{
    public IComfyWebSocket Create() => new UnavailableComfyWebSocket();

    private sealed class UnavailableComfyWebSocket : IComfyWebSocket
    {
        public WebSocketState State => WebSocketState.Closed;
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.FromException(new WebSocketException("Unavailable in HTTP-only test."));
        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
