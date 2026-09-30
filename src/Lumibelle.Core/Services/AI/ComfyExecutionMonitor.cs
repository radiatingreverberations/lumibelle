using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public sealed record ComfyNodeStage(
    GenerationPhase Phase,
    string Label,
    string? ProgressLabel = null,
    string? Unit = null,
    string? EstimateScope = null);

public sealed record ComfyExecutionOptions(
    IReadOnlyDictionary<string, ComfyNodeStage> Nodes,
    string RejectedMessage,
    string ExecutionErrorMessage,
    string TimeoutBeforeSubmitMessage,
    string TimeoutMessage,
    string UnreadableMessage,
    string ConnectionFailureMessage)
{
    public IReadOnlyDictionary<string, string> TimingNodes { get; init; } = new Dictionary<string, string>();
    /// <summary>Replaces the raw failure when ComfyUI reports that the GPU ran out of memory.</summary>
    public string? OutOfMemoryMessage { get; init; }
}

public sealed record ComfyExecutionUpdate(
    GenerationProgress Progress,
    string? PromptId = null,
    JsonElement? Job = null,
    bool Complete = false)
{
    public ComfyObservedTimings? Timings { get; init; }
}

public interface IComfyExecutionMonitor
{
    IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(HttpClient http, string promptId, string clientId, ComfyExecutionOptions options, CancellationToken ct) => throw new NotSupportedException();

    IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(
        HttpClient http,
        Func<string, object> workflowFactory,
        ComfyExecutionOptions options,
        CancellationToken operationToken,
        CancellationToken callerToken);
}

public interface IComfyWebSocketFactory
{
    IComfyWebSocket Create();
}

public interface IComfyWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);
}

public sealed class ClientComfyWebSocketFactory : IComfyWebSocketFactory
{
    public IComfyWebSocket Create() => new ClientComfyWebSocket();

    private sealed class ClientComfyWebSocket : IComfyWebSocket
    {
        private readonly ClientWebSocket _socket = new();
        public WebSocketState State => _socket.State;
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _socket.ConnectAsync(uri, cancellationToken);
        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            _socket.ReceiveAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ComfyExecutionMonitor(IComfyWebSocketFactory sockets, TimeProvider clock) : IComfyExecutionMonitor
{
    public IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(HttpClient http, string promptId, string clientId, ComfyExecutionOptions options, CancellationToken ct)
    {
        var output = Channel.CreateUnbounded<ComfyExecutionUpdate>();
        _ = RunAsync(output.Writer, http, _ => new { }, options, ct, ct, promptId, clientId, false);
        return output.Reader.ReadAllAsync();
    }

    private static readonly TimeSpan HistoryInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromSeconds(3);

    public IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(
        HttpClient http,
        Func<string, object> workflowFactory,
        ComfyExecutionOptions options,
        CancellationToken operationToken,
        CancellationToken callerToken)
    {
        var output = Channel.CreateUnbounded<ComfyExecutionUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _ = RunAsync(output.Writer, http, workflowFactory, options, operationToken, callerToken);
        return output.Reader.ReadAllAsync();
    }

    public static Uri BuildWebSocketUri(Uri baseAddress, string clientId)
    {
        var normalized = baseAddress.AbsoluteUri.EndsWith('/')
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/", UriKind.Absolute);
        var builder = new UriBuilder(new Uri(normalized, "ws"))
        {
            Scheme = normalized.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Query = "clientId=" + Uri.EscapeDataString(clientId)
        };
        return builder.Uri;
    }

    private async Task RunAsync(
        ChannelWriter<ComfyExecutionUpdate> output,
        HttpClient http,
        Func<string, object> workflowFactory,
        ComfyExecutionOptions options,
        CancellationToken operationToken,
        CancellationToken callerToken, string? existingPromptId = null, string? existingClientId = null, bool cancelJob = true)
    {
        string? promptId = existingPromptId;
        IComfyWebSocket? socket = null;
        using var producers = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        var tracker = new GenerationProgressTracker(clock);
        var timings = new ComfyTimingTracker(clock, options.TimingNodes);
        try
        {
            await output.WriteAsync(new(tracker.SetStage(existingPromptId is null ? GenerationPhase.Submitting : GenerationPhase.Preparing, "Connecting to ComfyUI…")), CancellationToken.None);
            var clientId = existingClientId ?? Guid.NewGuid().ToString("D");
            var live = false;
            socket = sockets.Create();
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(operationToken))
            {
                connect.CancelAfter(SocketConnectTimeout);
                try
                {
                    if (http.BaseAddress is null) throw new InvalidOperationException("The ComfyUI address is missing.");
                    await socket.ConnectAsync(BuildWebSocketUri(http.BaseAddress, clientId), connect.Token);
                    live = true;
                }
                catch (OperationCanceledException) when (!operationToken.IsCancellationRequested) { }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or InvalidOperationException) { }
            }
            tracker.SetLiveUpdatesAvailable(live);
            await output.WriteAsync(new(tracker.SetStage(existingPromptId is null ? GenerationPhase.Submitting : GenerationPhase.Preparing,
                existingPromptId is null ? "Submitting to ComfyUI…" : "Observing accepted ComfyUI job…")), CancellationToken.None);

            if (promptId is null)
            using (var body = JsonBody(workflowFactory(clientId)))
            using (var submitted = await http.PostAsync("prompt", body, operationToken))
            {
                if (!submitted.IsSuccessStatusCode) throw new AiGenerationException(options.RejectedMessage);
                using var receipt = await JsonDocument.ParseAsync(await submitted.Content.ReadAsStreamAsync(operationToken), cancellationToken: operationToken);
                if (!receipt.RootElement.TryGetProperty("prompt_id", out var id) || !Guid.TryParse(id.GetString(), out var parsed))
                    throw new AiGenerationException("ComfyUI returned an invalid job identifier.");
                promptId = parsed.ToString("D");
            }

            await output.WriteAsync(new(tracker.SetStage(GenerationPhase.Queued, "Queued in ComfyUI…"), promptId), CancellationToken.None);

            var signals = Channel.CreateUnbounded<MonitorSignal>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var historyTask = PollHistoryAsync(http, promptId, signals.Writer, producers.Token);
            var heartbeatTask = SendHeartbeatsAsync(signals.Writer, producers.Token);
            var socketTask = live && socket is not null
                ? ReadSocketAsync(socket, signals.Writer, producers.Token)
                : Task.CompletedTask;

            JsonElement? completedJob = null;
            Exception? failure = null;
            var forceUpdate = false;
            while (completedJob is null && failure is null)
            {
                await signals.Reader.WaitToReadAsync(operationToken);
                var processed = 0;
                while (processed++ < 256 && signals.Reader.TryRead(out var signal))
                {
                    switch (signal)
                    {
                        case HistoryResult history:
                            if (HasErrorStatus(history.Job)) failure = new AiGenerationException(ExecutionFailureMessage(options, history.Job));
                            else if (IsComplete(history.Job)) completedJob = history.Job;
                            break;
                        case MonitorFailure error:
                            failure = error.Error;
                            break;
                        case SocketUnavailable:
                            timings.Gap();
                            if (tracker.SetLiveUpdatesAvailable(false)) forceUpdate = true;
                            break;
                        case Heartbeat:
                            break;
                        case SocketPayload payload when tracker.LiveUpdatesAvailable:
                            try
                            {
                                var message = ParseSocketMessage(payload.Json, promptId);
                                if (message is not null)
                                {
                                    switch (message.Type)
                                    {
                                        case "execution_start": timings.Start(); break;
                                        case "executing": timings.Executing(message.NodeId); break;
                                        case "execution_success": timings.End(); break;
                                        case "execution_cached": timings.Cached(message.CachedNodes ?? []); break;
                                    }
                                    forceUpdate |= ApplySocketMessage(message, tracker, options, ref failure);
                                }
                            }
                            catch (JsonException)
                            {
                                timings.Gap();
                                if (tracker.SetLiveUpdatesAvailable(false)) forceUpdate = true;
                            }
                            break;
                    }

                    // Numeric updates may be coalesced, but stage, fallback, completion, and
                    // error signals must reach the UI before later signals overwrite them.
                    if (forceUpdate || completedJob is not null || failure is not null) break;
                }

                if (failure is not null) throw failure;
                if (completedJob is not null)
                {
                    var final = tracker.SetStage(GenerationPhase.Finalizing, "Finalizing ComfyUI result…");
                    await output.WriteAsync(new(final, promptId, completedJob, true)
                        { Timings = options.TimingNodes.Count == 0 ? null : timings.Snapshot() }, CancellationToken.None);
                    tracker.MarkEmitted();
                    break;
                }
                if (forceUpdate || tracker.ShouldEmit(RenderInterval))
                {
                    await output.WriteAsync(new(tracker.Snapshot(), promptId), CancellationToken.None);
                    tracker.MarkEmitted();
                    forceUpdate = false;
                }
            }

            producers.Cancel();
            output.TryComplete();
            await AwaitQuietly(historyTask, heartbeatTask);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            producers.Cancel();
            var cancelled = cancelJob && promptId is not null && await CancelJobAsync(http, promptId);
            Exception error;
            if (callerToken.IsCancellationRequested)
                error = new AiCancellationException(cancelled ? "Generation cancelled." :
                    "Stopped waiting. ComfyUI execution may continue; any late result will be ignored.", callerToken);
            else if (promptId is null)
                error = new AiGenerationException(options.TimeoutBeforeSubmitMessage);
            else
                error = new AiGenerationException(options.TimeoutMessage + (cancelled
                    ? " The submitted job was cancelled."
                    : " ComfyUI may continue running it; any late result will be ignored."));
            output.TryComplete(error);
        }
        catch (JsonException)
        {
            producers.Cancel();
            output.TryComplete(new AiGenerationException(options.UnreadableMessage));
        }
        catch (HttpRequestException)
        {
            producers.Cancel();
            output.TryComplete(new AiGenerationException(options.ConnectionFailureMessage));
        }
        catch (Exception error)
        {
            producers.Cancel();
            output.TryComplete(error);
        }
        finally
        {
            if (socket is not null) await socket.DisposeAsync();
        }
    }

    private static bool ApplySocketMessage(
        ComfySocketMessage message,
        GenerationProgressTracker tracker,
        ComfyExecutionOptions options,
        ref Exception? failure)
    {
        switch (message.Type)
        {
            case "execution_start":
                tracker.SetStage(GenerationPhase.Preparing, "Starting ComfyUI workflow…");
                return true;
            case "execution_error":
                failure = new AiGenerationException(FormatFailure(options, message.Failure));
                return true;
            case "execution_interrupted":
                failure = new AiGenerationException("ComfyUI interrupted the submitted workflow.");
                return true;
            case "execution_success":
                tracker.SetStage(GenerationPhase.Finalizing, "Finalizing ComfyUI result…");
                return true;
            case "executing" when message.NodeId is null:
                tracker.SetStage(GenerationPhase.Finalizing, "Finalizing ComfyUI result…");
                return true;
            case "executing":
                if (options.Nodes.TryGetValue(message.NodeId!, out var node))
                    tracker.SetStage(node.Phase, node.Label, message.NodeId);
                else
                    tracker.SetStage(GenerationPhase.Preparing, "Running ComfyUI workflow…", message.NodeId);
                return true;
            case "progress" when message.NodeId is not null && message.Current is not null && message.Maximum is > 0 &&
                options.Nodes.TryGetValue(message.NodeId, out var progressNode) && progressNode.ProgressLabel is not null:
                var firstProgress = !tracker.IsDeterminate;
                tracker.SetProgress(GenerationPhase.Generating, progressNode.ProgressLabel, message.Current.Value,
                    message.Maximum.Value, progressNode.Unit, message.NodeId, progressNode.EstimateScope);
                return firstProgress;
            default:
                return false;
        }
    }

    internal static ComfySocketMessage? ParseSocketMessage(string json, string promptId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement) || !root.TryGetProperty("data", out var data)) return null;
        var type = typeElement.GetString();
        if (type is null || !data.TryGetProperty("prompt_id", out var id) || id.GetString() != promptId) return null;
        if (type == "execution_error") return new(type, null, null, null) { Failure = ReadFailure(data) };
        if (type == "execution_cached" && data.TryGetProperty("nodes", out var cached) && cached.ValueKind == JsonValueKind.Array)
            return new(type, null, null, null) { CachedNodes = cached.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToArray() };

        if (type == "progress_state" && data.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in nodes.EnumerateObject())
            {
                var node = property.Value;
                if (!node.TryGetProperty("state", out var state) || state.GetString() != "running") continue;
                return new("progress", property.Name,
                    Number(node, "value"), Number(node, "max"));
            }
            return null;
        }

        string? nodeId = null;
        if (data.TryGetProperty("node", out var nodeElement) && nodeElement.ValueKind == JsonValueKind.String)
            nodeId = nodeElement.GetString();
        return new(type, nodeId, Number(data, "value"), Number(data, "max"));
    }

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool HasErrorStatus(JsonElement job) => job.TryGetProperty("status", out var status) &&
        status.TryGetProperty("status_str", out var state) && state.GetString() == "error";

    internal static string ExecutionFailureMessage(ComfyExecutionOptions options, JsonElement job)
    {
        if (job.TryGetProperty("status", out var status) && status.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (var message in messages.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Array && message.GetArrayLength() > 1 &&
                    message[0].ValueKind == JsonValueKind.String && message[0].GetString() == "execution_error")
                    return FormatFailure(options, ReadFailure(message[1]));
        return options.ExecutionErrorMessage;
    }

    private static ComfyFailure ReadFailure(JsonElement data)
    {
        string? Field(string name, int limit)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString()!.Trim();
            return text.Length == 0 ? null : text.Length <= limit ? text : text[..limit] + "…";
        }
        // Do not retain current_inputs, output tensors or traceback in UI errors.
        return new(Field("node_id", 80), Field("node_type", 160), Field("exception_type", 160), Field("exception_message", 1000));
    }

    private static string FormatFailure(ComfyExecutionOptions options, ComfyFailure? failure)
    {
        if (failure?.NodeType == Shots.H3Performance.SageNode)
            return "The selected SageAttention patch failed. Check KJNodes and a compatible sageattention package in ComfyUI's Python environment, then restart and refresh Video models. No other attention mode was selected.";
        if (failure is null || failure.Message is null && failure.ExceptionType is null) return options.ExecutionErrorMessage;
        if (options.OutOfMemoryMessage is { } outOfMemory && IsOutOfMemory(failure.ExceptionType + " " + failure.Message)) return outOfMemory;
        var node = failure.NodeType is { } type ? " at " + type : "";
        if (failure.NodeId is { } id) node += " (node " + id + ")";
        var reason = string.Join(": ", new[] { failure.ExceptionType, failure.Message }.Where(s => s is not null));
        return $"ComfyUI failed{node}: {reason}";
    }

    internal static bool IsOutOfMemory(string text) =>
        text.Contains("OutOfMemoryError", StringComparison.Ordinal) || text.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

    private static bool IsComplete(JsonElement job) => job.TryGetProperty("status", out var status) &&
        status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True;

    private static async Task PollHistoryAsync(HttpClient http, string promptId, ChannelWriter<MonitorSignal> writer, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var response = await http.GetAsync($"history/{promptId}", ct);
                response.EnsureSuccessStatusCode();
                using var history = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (history.RootElement.TryGetProperty(promptId, out var job))
                {
                    var clone = job.Clone();
                    await writer.WriteAsync(new HistoryResult(clone), ct);
                    if (HasErrorStatus(clone) || IsComplete(clone)) return;
                }
                await Task.Delay(HistoryInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { writer.TryWrite(new MonitorFailure(error)); }
    }

    private static async Task SendHeartbeatsAsync(ChannelWriter<MonitorSignal> writer, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, ct);
                await writer.WriteAsync(new Heartbeat(), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task ReadSocketAsync(IComfyWebSocket socket, ChannelWriter<MonitorSignal> writer, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                await using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        writer.TryWrite(new SocketUnavailable());
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text && message.Length + result.Count <= 1024 * 1024)
                        await message.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text && message.Length > 0)
                    await writer.WriteAsync(new SocketPayload(Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length))), ct);
            }
            writer.TryWrite(new SocketUnavailable());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            writer.TryWrite(new SocketUnavailable());
        }
    }

    private static async Task<bool> CancelJobAsync(HttpClient http, string promptId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var direct = await http.PostAsync($"api/jobs/{promptId}/cancel", JsonBody(new { }), timeout.Token);
            if (direct.IsSuccessStatusCode) return true;
            using var dequeue = await http.PostAsync("queue", JsonBody(new { delete = new[] { promptId } }), timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ComfyAccessException) { }
        // Authentication can expire while a job is running. A denied cancel stops
        // local observation without claiming the remote job stopped.
        return false;
    }

    private static async Task AwaitQuietly(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private static StringContent JsonBody<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    internal sealed record ComfySocketMessage(string Type, string? NodeId, double? Current, double? Maximum)
    {
        public IReadOnlyList<string>? CachedNodes { get; init; }
        public ComfyFailure? Failure { get; init; }
    }
    internal sealed record ComfyFailure(string? NodeId, string? NodeType, string? ExceptionType, string? Message);
    private abstract record MonitorSignal;
    private sealed record SocketPayload(string Json) : MonitorSignal;
    private sealed record SocketUnavailable : MonitorSignal;
    private sealed record HistoryResult(JsonElement Job) : MonitorSignal;
    private sealed record MonitorFailure(Exception Error) : MonitorSignal;
    private sealed record Heartbeat : MonitorSignal;
}

internal sealed class GenerationProgressTracker(TimeProvider clock)
{
    private readonly DateTimeOffset _started = clock.GetUtcNow();
    private readonly List<(double Seconds, double Units)> _intervals = [];
    private (DateTimeOffset At, double Value)? _lastAdvance;
    private string? _estimateScope;
    private DateTimeOffset _lastEmitted = clock.GetUtcNow();
    private GenerationPhase _phase = GenerationPhase.Submitting;
    private string _label = "Starting…";
    private string? _nodeId;
    private double? _current;
    private double? _maximum;
    private string? _unit;
    public bool LiveUpdatesAvailable { get; private set; } = true;
    public bool IsDeterminate => _current is not null && _maximum is > 0;

    public bool SetLiveUpdatesAvailable(bool value)
    {
        if (LiveUpdatesAvailable == value) return false;
        LiveUpdatesAvailable = value;
        ResetEstimate();
        return true;
    }

    public GenerationProgress SetStage(GenerationPhase phase, string label, string? nodeId = null)
    {
        if (_phase != phase || _label != label || _nodeId != nodeId)
        {
            ResetEstimate();
            _current = null;
            _maximum = null;
            _unit = null;
        }
        _phase = phase;
        _label = label;
        _nodeId = nodeId;
        return Snapshot();
    }

    private void ResetEstimate() { _intervals.Clear(); _lastAdvance = null; }

    public GenerationProgress SetProgress(GenerationPhase phase, string label, double current, double maximum, string? unit, string? nodeId, string? estimateScope = null)
    {
        var now = clock.GetUtcNow();
        if (_nodeId != nodeId || _phase != phase || _maximum != maximum || _label != label || _unit != unit ||
            _estimateScope != estimateScope || current < _current || !double.IsFinite(current) || !double.IsFinite(maximum) ||
            _intervals.Count >= 3 && _lastAdvance is { } previous && now - previous.At > StaleAfter()) ResetEstimate();
        _phase = phase;
        _label = label;
        _nodeId = nodeId;
        _current = current;
        _maximum = maximum;
        _unit = unit;
        _estimateScope = estimateScope;
        if (LiveUpdatesAvailable && estimateScope is not null && double.IsFinite(current) && double.IsFinite(maximum) && maximum > 0 && current >= 0 && current <= maximum)
        {
            if (_lastAdvance is { } last && current > last.Value && now > last.At)
            {
                _intervals.Add(((now - last.At).TotalSeconds, current - last.Value));
                if (_intervals.Count > 5) _intervals.RemoveAt(0);
                _lastAdvance = (now, current);
            }
            else if (_lastAdvance is null) _lastAdvance = (now, current);
        }
        return Snapshot();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
    private TimeSpan StaleAfter() => TimeSpan.FromSeconds(Math.Max(15, _intervals.Count == 0 ? 0 : 3 * Median(_intervals.Select(i => i.Seconds))));

    public GenerationProgress Snapshot()
    {
        var now = clock.GetUtcNow();
        TimeSpan? remaining = null;
        DateTimeOffset? observed = null, expires = null;
        if (LiveUpdatesAvailable && _estimateScope is not null && _lastAdvance is { } last && _intervals.Count >= 3 &&
            _intervals.Sum(i => i.Seconds) >= 2 && _current is not null && _maximum > _current && now - last.At <= StaleAfter())
        {
            var estimate = (_maximum.Value - _current.Value) * Median(_intervals.Select(i => i.Seconds / i.Units));
            if (double.IsFinite(estimate) && estimate is > 0 and <= 86400 && now - last.At < TimeSpan.FromSeconds(estimate))
            {
                remaining = TimeSpan.FromSeconds(estimate); observed = last.At; expires = last.At + StaleAfter();
            }
        }
        return new(_phase, _label, _current, _maximum, _unit, now - _started, remaining, LiveUpdatesAvailable)
            { ExecutionStageId = _nodeId, EstimateScope = remaining is null ? null : _estimateScope, EstimateObservedUtc = observed, EstimateExpiresUtc = expires };
    }

    public bool ShouldEmit(TimeSpan interval) => clock.GetUtcNow() - _lastEmitted >= interval;
    public void MarkEmitted() => _lastEmitted = clock.GetUtcNow();
}
