using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace lumibelle.Services.AI;

public sealed partial class AiProviderRegistry(IHttpClientFactory clients, IAiSettingsStore settingsStore, IComfyExecutionMonitor comfyMonitor, ICodexClient? codex = null, IClaudeCodeClient? claude = null) : IAiProviderRegistry, IModelTestRunner
{
    // Reply limit of standard benchmarks captured before they used the model's own setting.
    internal const int LegacyBenchmarkTokens = 256;
    internal const string StandardBenchmarkPrompt = "Write a continuous fictional description of a quiet forest at dawn in approximately 220 words. Do not use headings or mention this request.";

    public async Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default)
    {
        FileAiSettingsStore.Validate(settings);
        if (string.IsNullOrWhiteSpace(model)) throw new AiGenerationException("Choose a model in AI settings first.");
        if (backend == AiBackend.Codex) return new CodexChatClient(codex ?? throw new AiGenerationException("Codex is not configured."), settings.Codex, model);
        if (backend == AiBackend.ClaudeCode) return new ClaudeCodeChatClient(claude ?? throw new AiGenerationException("Claude Code is not configured."), settings.ClaudeCode, model);
        if (backend == AiBackend.ComfyUI)
        {
            var http = clients.CreateClient("ComfyUI");
            http.BaseAddress = new Uri(NormalizeComfyUrl(settings.ComfyUrl) + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
            var reference = new TextModelReference(AiBackend.ComfyUI, model, model, settings.ComfyUrl);
            return new ComfyChatClient(http, model, comfyMonitor, ComfyTextVision.Mode(reference, settings),
                ComfyTextCapabilities.SystemPromptVersions(reference, settings), ComfyTextSettings.BatchImageSide(reference, settings));
        }
        if (backend != AiBackend.OpenRouter) throw new AiGenerationException("This AI backend is not available.");
        var key = await settingsStore.ReadOpenRouterKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) throw new AiGenerationException("Add an OpenRouter API key in AI settings first.");
        var transportClient = clients.CreateClient("OpenRouter");
        transportClient.Timeout = Timeout.InfiniteTimeSpan;
        var client = new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1"),
            Transport = new HttpClientPipelineTransport(transportClient),
            RetryPolicy = new ClientRetryPolicy(0),
            // The SDK applies this to individual network reads, not the lifetime of
            // the stream. The watchdog separately rejects heartbeat-only streams.
            NetworkTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        });
        return new OwnedChatClient(client.AsIChatClient(), transportClient);
    }

    public async Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default)
    {
        FileAiSettingsStore.Validate(settings);
        if (backend == AiBackend.Codex)
        {
            if (codex is null) return new(false, "Codex is not configured.", []);
            var check = await codex.CheckAsync(settings.Codex, cancellationToken);
            return new(check.Success, check.Message, check.Models, check.Version);
        }
        if (backend == AiBackend.ClaudeCode)
        {
            if (claude is null) return new(false, "Claude Code is not configured.", []);
            var check = await claude.CheckAsync(settings.ClaudeCode, cancellationToken);
            return new(check.Success, check.Message, check.Models, check.Version);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = clients.CreateClient(backend.ToString());
            if (backend == AiBackend.ComfyUI)
            {
                http.BaseAddress = new Uri(NormalizeComfyUrl(settings.ComfyUrl) + "/");
                var comfyCatalog = await ReadComfyCatalogAsync(http, timeout.Token);
                if (!comfyCatalog.HasRequiredNodes)
                    return new(false, "Update ComfyUI: the built-in workflow needs CLIPLoader, TextGenerate, and PreviewAny.", []);
                var normalizedUrl = NormalizeComfyUrl(settings.ComfyUrl);
                var verified = settings.ComfyTextModelVerifications
                    .Where(item => string.Equals(NormalizeComfyUrl(item.ComfyUrl), normalizedUrl, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.ComfyVersion, comfyCatalog.Version, StringComparison.Ordinal))
                    .Select(item => item.Model).ToHashSet(StringComparer.Ordinal);
                var models = comfyCatalog.Models.Select(name => new AiModel(name, name, verified.Contains(name)
                    ? AiModelVerificationState.Verified : AiModelVerificationState.Untested,
                    SupportsImages: comfyCatalog.Vision.Supports(ComfyTextVision.Mode(new(AiBackend.ComfyUI, name, name, settings.ComfyUrl), settings, comfyCatalog.Version)))).ToArray();
                return new(models.Length > 0,
                    models.Length > 0
                        ? $"Connected to ComfyUI {comfyCatalog.Version}. {models.Length:N0} text-encoder models found."
                        : $"Connected to ComfyUI {comfyCatalog.Version}, but CLIPLoader did not advertise any text encoders.",
                    models, comfyCatalog.Version);
            }
            var key = string.IsNullOrWhiteSpace(replacementKey) ? await settingsStore.ReadOpenRouterKeyAsync(timeout.Token) : replacementKey.Trim();
            if (string.IsNullOrEmpty(key)) return new(false, "Enter an OpenRouter API key to check this connection.", []);
            http.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var check = await http.GetAsync("key", timeout.Token);
            if (!check.IsSuccessStatusCode) return new(false, AiErrors.HttpStatus((int)check.StatusCode), []);
            using var catalog = await http.GetAsync("models?sort=most-popular", timeout.Token);
            catalog.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await catalog.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var choices = json.RootElement.GetProperty("data").EnumerateArray()
                .Where(item => !item.TryGetProperty("architecture", out var architecture) || !architecture.TryGetProperty("output_modalities", out var output)
                    || output.EnumerateArray().Any(value => value.GetString() == "text"))
                .Select((item, index) => new AiModel(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!, SupportsImages:
                    item.TryGetProperty("architecture", out var architecture) && architecture.TryGetProperty("input_modalities", out var inputs) &&
                    inputs.ValueKind == JsonValueKind.Array && inputs.EnumerateArray().Any(value => value.GetString() == "image"), Catalog: OpenRouterModelMetadata.Read(item, index + 1)))
                .OrderBy(item => item.Name).ToArray();
            return new(true, "Connected. The API key is valid; no text was generated.", choices);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Connection check timed out.", []); }
        catch (HttpRequestException) { return new(false, "Couldn’t reach the backend. Check its address and connection.", []); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(false, "The backend returned an unexpected model catalog.", []); }
    }

    public IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(
        string model,
        AiSettings settings,
        CancellationToken cancellationToken = default) =>
        RunComfyTextModelTestAsync(model, settings, new(StandardBenchmarkPrompt,
            ComfyTextSettings.Resolve(new(AiBackend.ComfyUI, model, model, settings.ComfyUrl), settings).MaxOutputTokens), includeResponse: false, cancellationToken);

    public IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(
        string model,
        AiSettings settings,
        ComfyTextModelTestRequest request,
        CancellationToken cancellationToken = default) =>
        RunComfyTextModelTestAsync(model, settings, request, includeResponse: true, cancellationToken);

    private async IAsyncEnumerable<AiModelVerificationUpdate> RunComfyTextModelTestAsync(
        string model,
        AiSettings settings,
        ComfyTextModelTestRequest request,
        bool includeResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FileAiSettingsStore.Validate(settings);
        if (string.IsNullOrWhiteSpace(model)) throw new AiGenerationException("Choose a ComfyUI text model to test.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new AiGenerationException("Enter a message for the advanced model test.");
        if (request.MaxOutputTokens is < 1 or > AiModelTestJobHandler.ComfyAdvancedMaxTokens)
            throw new AiGenerationException("Choose an advanced test output limit between 1 and 32,768 tokens.");

        using var deadline = new TextInactivityWatchdog(settings.TimeoutSeconds, cancellationToken);
        using var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new Uri(NormalizeComfyUrl(settings.ComfyUrl) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan;

        yield return new(Progress: new(GenerationPhase.Preparing, "Checking model and ComfyUI queue"));
        var catalog = await PrepareVerificationAsync(http, model, deadline.Token, cancellationToken);
        yield return new(Progress: new(GenerationPhase.Preparing, "Clearing ComfyUI model cache"));
        var cache = await ClearCachesAndReadBaselineAsync(http, catalog.Memory, deadline.Token, cancellationToken);
        var memory = cache.Baseline is null ? null : new ComfyMemoryTracker(cache.Baseline, cache.ClearConfirmed);
        await using var memorySampler = memory is null ? null : new ComfyMemorySampler(http, memory, deadline.Token);
        var tokens = new TokenRateTracker();
        var completed = false;
        string? response = null;
        var seed = Random.Shared.NextInt64(1, long.MaxValue);
        await foreach (var update in comfyMonitor.ExecuteAsync(http,
            clientId => ComfyChatClient.BuildWorkflow(model, includeResponse ? request.Prompt : ComfyTextBenchmark.Prompt(request.Prompt), request.MaxOutputTokens,
                includeResponse ? ComfyTextSettings.Resolve(new(AiBackend.ComfyUI, model, model, settings.ComfyUrl), settings).Temperature : 0.7f, seed, clientId),
            ComfyChatClient.ExecutionOptions, deadline.Token, cancellationToken))
        {
            deadline.Observe(update.Progress);
            if (update.Complete) deadline.Stop();
            tokens.Observe(update.Progress);
            yield return new(Progress: update.Progress);
            completed |= update.Complete;
            if (includeResponse && update.Complete && update.Job is { } job)
                response = ComfyChatClient.TryReadText(job, out var text) ? text : string.Empty;
        }

        if (!completed) throw new AiGenerationException("ComfyUI stopped the model test before it completed.");
        if (memorySampler is not null) await memorySampler.StopAsync();
        yield return new(Progress: new(GenerationPhase.Finalizing, "Checking system prompt and image support"));
        var capabilities = await ComfyTextCapabilities.ProbeAsync(http, model, catalog.SystemPromptInput, catalog.Vision,
            (_, workflow, ct) => RunProbeAsync(http, workflow, settings.TimeoutSeconds, ct), _ => Task.CompletedTask, cancellationToken);
        var benchmark = memory?.Build(request.MaxOutputTokens, tokens.GeneratedTokens, tokens.TokensPerSecond, includeResponse) ??
            new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, request.MaxOutputTokens,
                tokens.GeneratedTokens, tokens.TokensPerSecond, cache.ClearConfirmed, includeResponse);
        if (!includeResponse) benchmark = benchmark with { ContextTokens = ComfyTextBenchmark.ContextTokens };
        if (!includeResponse && cache.Baseline is { } baseline && benchmark.PeakTorchAllocatedBytes is { } fullPeak &&
            ComfyTextCapacity.BytesPerToken(fullPeak, 0, request.MaxOutputTokens) is not null)
        {
            // Same prompt, short limit: the difference in reserved memory is the cost of each additional token.
            yield return new(Progress: new(GenerationPhase.Finalizing, "Measuring memory per token"));
            var footprint = new ComfyMemoryTracker(baseline, cache.ClearConfirmed);
            string? measured;
            await using (var sampler = new ComfyMemorySampler(http, footprint, cancellationToken))
            {
                measured = await RunProbeAsync(http, clientId => ComfyChatClient.BuildWorkflow(model, ComfyTextBenchmark.Prompt(request.Prompt),
                    ComfyTextCapacity.FootprintTokens, 0.7f, seed, clientId), settings.TimeoutSeconds, cancellationToken);
                await sampler.StopAsync();
            }
            if (measured is not null)
                benchmark = benchmark with { BytesPerToken = ComfyTextCapacity.BytesPerToken(fullPeak, footprint.PeakTorchAllocatedBytes, request.MaxOutputTokens) };
        }
        var verification = new ComfyTextModelVerification(
            NormalizeComfyUrl(settings.ComfyUrl), catalog.Version, model, DateTimeOffset.UtcNow,
            [benchmark]) { Capabilities = capabilities };
        yield return new(Verification: verification, Response: includeResponse ? response ?? string.Empty : null);
    }

    private async Task<string?> RunProbeAsync(HttpClient http, Func<string, object> workflow, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var deadline = new TextInactivityWatchdog(timeoutSeconds, cancellationToken);
        try
        {
            await foreach (var update in comfyMonitor.ExecuteAsync(http, workflow, ComfyChatClient.ExecutionOptions, deadline.Token, cancellationToken))
            {
                deadline.Observe(update.Progress);
                if (update.Complete && update.Job is { } job) return ComfyChatClient.TryReadText(job, out var text) ? text : null;
            }
            return null;
        }
        // A rejected, failed or stalled probe means the capability was not observed; it does not fail the test.
        catch (Exception e) when (e is AiGenerationException or OperationCanceledException && !cancellationToken.IsCancellationRequested) { return null; }
    }

    public static string NormalizeComfyUrl(string url) => new Uri(url.Trim().TrimEnd('/') + "/", UriKind.Absolute).AbsoluteUri.TrimEnd('/');

    private static async Task<ComfyCatalog> PrepareVerificationAsync(HttpClient http, string model,
        CancellationToken operationToken, CancellationToken callerToken)
    {
        try
        {
            var catalog = await ReadComfyCatalogAsync(http, operationToken);
            if (!catalog.HasRequiredNodes)
                throw new AiGenerationException("Update ComfyUI: the model test needs CLIPLoader, TextGenerate, and PreviewAny.");
            if (!catalog.Models.Contains(model, StringComparer.Ordinal))
                throw new AiGenerationException("The selected model is no longer in ComfyUI’s CLIPLoader catalog. Refresh models and choose an available file.");
            return catalog;
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new AiGenerationException("The ComfyUI model test timed out before the workflow was submitted.");
        }
        catch (OperationCanceledException)
        {
            throw new AiCancellationException("Model test cancelled.", callerToken);
        }
        catch (HttpRequestException)
        {
            throw new AiGenerationException("Couldn’t reach ComfyUI to test this model. Check its address and connection.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiGenerationException("ComfyUI returned an unreadable model catalog or server version.");
        }
    }

    private static async Task<ComfyCatalog> ReadComfyCatalogAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var objectInfo = await http.GetAsync("object_info", cancellationToken);
        objectInfo.EnsureSuccessStatusCode();
        using var systemStats = await http.GetAsync("system_stats", cancellationToken);
        systemStats.EnsureSuccessStatusCode();
        using var nodes = await JsonDocument.ParseAsync(await objectInfo.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        using var stats = await JsonDocument.ParseAsync(await systemStats.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = nodes.RootElement;
        var hasNodes = root.TryGetProperty("TextGenerate", out _) && root.TryGetProperty("PreviewAny", out _) &&
            root.TryGetProperty("CLIPLoader", out _);
        var models = hasNodes
            ? root.GetProperty("CLIPLoader").GetProperty("input").GetProperty("required").GetProperty("clip_name")[0].EnumerateArray()
                .Select(item => item.GetString()).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var version = stats.RootElement.GetProperty("system").GetProperty("comfyui_version").GetString();
        if (string.IsNullOrWhiteSpace(version)) throw new JsonException("ComfyUI did not report its version.");
        return new(hasNodes, version, models, ReadPrimaryMemory(stats.RootElement), ComfyTextVision.Capabilities(root),
            ComfyTextCapabilities.SupportsSystemPromptInput(root));
    }

    private static async Task<CacheBaseline> ClearCachesAndReadBaselineAsync(HttpClient http, ComfyMemorySnapshot? initial,
        CancellationToken operationToken, CancellationToken callerToken)
    {
        try
        {
            using var queueResponse = await http.GetAsync("queue", operationToken);
            queueResponse.EnsureSuccessStatusCode();
            using var queue = await JsonDocument.ParseAsync(await queueResponse.Content.ReadAsStreamAsync(operationToken), cancellationToken: operationToken);
            if (!queue.RootElement.TryGetProperty("queue_running", out var running) || running.ValueKind != JsonValueKind.Array ||
                !queue.RootElement.TryGetProperty("queue_pending", out var pending) || pending.ValueKind != JsonValueKind.Array)
                throw new JsonException("ComfyUI returned an unreadable queue.");
            if (running.GetArrayLength() > 0 || pending.GetArrayLength() > 0)
                throw new AiGenerationException("ComfyUI is busy. Wait for its running and pending jobs before starting a cache-cleared model benchmark.");

            using var freeResponse = await http.PostAsJsonAsync("free", new { unload_models = true, free_memory = true }, operationToken);
            freeResponse.EnsureSuccessStatusCode();

            ComfyMemorySnapshot? latest = null;
            ComfyMemorySnapshot? previous = null;
            var stableSamples = 0;
            var clearConfirmed = IsAlreadyClear(initial);
            using var memoryWindow = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            memoryWindow.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    if (attempt > 0) await Task.Delay(100, memoryWindow.Token);
                    var sample = await TryReadPrimaryMemoryAsync(http, memoryWindow.Token);
                    if (sample is null) { stableSamples = 0; previous = null; continue; }
                    latest = sample;
                    clearConfirmed |= ReservedMemoryDropped(initial, latest);
                    stableSamples = previous is not null && MemoryIsStable(previous, latest) ? stableSamples + 1 : 0;
                    previous = latest;
                    if (stableSamples >= 2 && clearConfirmed) break;
                }
            }
            catch (OperationCanceledException) when (!operationToken.IsCancellationRequested) { }
            return new(latest, clearConfirmed && stableSamples >= 2);
        }
        catch (AiGenerationException) { throw; }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new AiGenerationException("The ComfyUI model benchmark timed out while preparing a clean cache.");
        }
        catch (OperationCanceledException)
        {
            throw new AiCancellationException("Model test cancelled.", callerToken);
        }
        catch (HttpRequestException)
        {
            throw new AiGenerationException("Couldn’t prepare ComfyUI for a cache-cleared model benchmark. Check the server connection.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiGenerationException("ComfyUI returned an unreadable queue or memory report before the model benchmark.");
        }
    }

    private static async Task<ComfyMemorySnapshot?> TryReadPrimaryMemoryAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("system_stats", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stats = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return ReadPrimaryMemory(stats.RootElement);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private static ComfyMemorySnapshot? ReadPrimaryMemory(JsonElement stats)
    {
        if (!stats.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return null;
        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("vram_total", out var totalValue) || !totalValue.TryGetInt64(out var total) || total <= 0 ||
                !device.TryGetProperty("vram_free", out var freeValue) || !freeValue.TryGetInt64(out var free)) continue;
            var name = device.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            int? index = device.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsedIndex) ? parsedIndex : null;
            long? torchReserved = device.TryGetProperty("torch_vram_total", out var torchTotal) && torchTotal.TryGetInt64(out var reserved) ? reserved : null;
            long? torchFree = device.TryGetProperty("torch_vram_free", out var torchFreeValue) && torchFreeValue.TryGetInt64(out var freeTorch) ? freeTorch : null;
            return new(name ?? "Primary GPU", index, total, Math.Clamp(free, 0, total), torchReserved, torchFree);
        }
        return null;
    }

    private static bool IsAlreadyClear(ComfyMemorySnapshot? snapshot) => snapshot?.TorchReservedBytes is >= 0 and <= 512L * 1024 * 1024;
    private static bool ReservedMemoryDropped(ComfyMemorySnapshot? before, ComfyMemorySnapshot after) =>
        before?.TorchReservedBytes is { } previous && after.TorchReservedBytes is { } current && previous - current >= 16L * 1024 * 1024;
    private static bool MemoryIsStable(ComfyMemorySnapshot previous, ComfyMemorySnapshot current) =>
        previous.DeviceName == current.DeviceName && Math.Abs(previous.VramFreeBytes - current.VramFreeBytes) < 16L * 1024 * 1024 &&
        (previous.TorchReservedBytes is null || current.TorchReservedBytes is null ||
            Math.Abs(previous.TorchReservedBytes.Value - current.TorchReservedBytes.Value) < 16L * 1024 * 1024);

    private sealed record ComfyCatalog(bool HasRequiredNodes, string Version, IReadOnlyList<string> Models, ComfyMemorySnapshot? Memory,
        ComfyVisionCapabilities Vision, bool SystemPromptInput);
    private sealed record CacheBaseline(ComfyMemorySnapshot? Baseline, bool ClearConfirmed);
    private sealed record ComfyMemorySnapshot(string DeviceName, int? DeviceIndex, long VramTotalBytes, long VramFreeBytes,
        long? TorchReservedBytes, long? TorchFreeBytes)
    {
        public long VramUsedBytes => Math.Max(0, VramTotalBytes - VramFreeBytes);
        public long? TorchAllocatedBytes => TorchReservedBytes is { } reserved && TorchFreeBytes is { } free
            ? Math.Max(0, reserved - free) : null;
    }

    private sealed class ComfyMemoryTracker(ComfyMemorySnapshot baseline, bool clearConfirmed)
    {
        private long _peakVramUsedBytes = baseline.VramUsedBytes;
        private long? _peakTorchAllocatedBytes = baseline.TorchAllocatedBytes;
        public long? PeakTorchAllocatedBytes => _peakTorchAllocatedBytes;
        public void Observe(ComfyMemorySnapshot sample)
        {
            if (sample.DeviceName != baseline.DeviceName || sample.DeviceIndex != baseline.DeviceIndex) return;
            _peakVramUsedBytes = Math.Max(_peakVramUsedBytes, sample.VramUsedBytes);
            if (sample.TorchAllocatedBytes is { } allocated)
                _peakTorchAllocatedBytes = Math.Max(_peakTorchAllocatedBytes ?? 0, allocated);
        }

        public ComfyTextModelBenchmark Build(int tokenLimit, int? generatedTokens, double? tokensPerSecond, bool customPrompt) =>
            new(DateTimeOffset.UtcNow, baseline.DeviceName, baseline.DeviceIndex, baseline.VramTotalBytes,
                baseline.VramUsedBytes, _peakVramUsedBytes, baseline.TorchAllocatedBytes, _peakTorchAllocatedBytes,
                tokenLimit, generatedTokens, tokensPerSecond, clearConfirmed, customPrompt);
    }

    private sealed class ComfyMemorySampler : IAsyncDisposable
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(1);
        private readonly HttpClient _http;
        private readonly ComfyMemoryTracker _tracker;
        private readonly CancellationTokenSource _stopping;
        private readonly Task _sampling;
        private bool _stopped;

        public ComfyMemorySampler(HttpClient http, ComfyMemoryTracker tracker, CancellationToken operationToken)
        {
            _http = http;
            _tracker = tracker;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            _sampling = SampleAsync();
        }

        private async Task SampleAsync()
        {
            try
            {
                while (true)
                {
                    using var sampleDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    sampleDeadline.CancelAfter(SampleTimeout);
                    try
                    {
                        if (await TryReadPrimaryMemoryAsync(_http, sampleDeadline.Token) is { } sample)
                            _tracker.Observe(sample);
                    }
                    catch (OperationCanceledException) when (!_stopping.IsCancellationRequested) { }
                    await Task.Delay(SampleInterval, _stopping.Token);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            _stopping.Cancel();
            await _sampling;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _stopping.Dispose();
        }
    }

    private sealed class TokenRateTracker
    {
        private double? _firstValue, _lastValue;
        private TimeSpan _firstElapsed, _lastElapsed;
        public int? GeneratedTokens => _lastValue is { } value ? Math.Max(0, (int)Math.Round(value)) : null;
        public double? TokensPerSecond
        {
            get
            {
                if (_firstValue is null || _lastValue is null) return null;
                var seconds = (_lastElapsed - _firstElapsed).TotalSeconds;
                var generated = _lastValue.Value - _firstValue.Value;
                return seconds > 0 && generated > 0 ? generated / seconds : null;
            }
        }
        public void Observe(GenerationProgress progress)
        {
            if (!progress.IsDeterminate || !string.Equals(progress.Unit, "tokens", StringComparison.OrdinalIgnoreCase) || progress.Current is null) return;
            var current = progress.Current.Value;
            if (_lastValue is not null && current < _lastValue)
            {
                _firstValue = null;
                _lastValue = null;
            }
            if (_lastValue is not null && current <= _lastValue) return;
            if (_firstValue is null && current > 0)
            {
                _firstValue = current;
                _firstElapsed = progress.Elapsed;
            }
            _lastValue = current;
            _lastElapsed = progress.Elapsed;
        }
    }

    private sealed class OwnedChatClient(IChatClient inner, HttpClient transport) : DelegatingChatClient(inner), IProgressReportingChatClient
    {
        public async IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return new(update, Activity: TextStreamActivity.Reasoning(update), OpenRouterUsage: OpenRouterUsageParser.Read(update));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) transport.Dispose();
        }
    }
}

public static class AiErrors
{
    public static string HttpStatus(int status) => status switch
    {
        401 => "The API key was rejected. Replace it in AI settings.",
        402 => "OpenRouter reports insufficient credits for this request.",
        403 => "This API key does not have access to the requested model.",
        404 => "The selected model or endpoint is unavailable. Refresh the model list.",
        400 or 413 or 422 => "The model rejected the request. Check the model, output limit, and context size; try including fewer scenes.",
        429 => "The backend’s rate limit was reached. Wait before retrying.",
        _ => "The AI backend could not complete this request. Your writing is unchanged."
    };
}
