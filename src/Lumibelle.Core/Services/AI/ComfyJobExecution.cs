using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record ComfySavedOperation(string ServerUrl, string ClientId, JsonElement Workflow, ComfyExecutionOptions Options);

// Shared by local text, image, model-test and video handlers. A submission, its
// observation, and output retrieval are separate durable operations.
public sealed partial class ComfyJobExecution(IComfyExecutionMonitor monitor)
{
    public async IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(AiJobContext context, string operation, HttpClient http,
        Func<string, object> workflowFactory, ComfyExecutionOptions options, [EnumeratorCancellation] CancellationToken ct, Action? onProviderCompleted = null)
    {
        if (context.Recovering) throw new AiGenerationException("Use saved-job observation during recovery; no new submission is allowed.");
        var server = Address(http);
        // A pause can happen after capturing the graph but before BeginRemote.
        // Reuse that immutable graph/client ID rather than overwrite its request.
        var prepared = await context.ReadOperationAsync<ComfySavedOperation>(operation, AiOperationArtifact.Request, ct);
        var clientId = prepared?.ClientId ?? Guid.NewGuid().ToString("D");
        var workflow = JsonSerializer.SerializeToElement(workflowFactory(clientId), AtomicJsonFile.Options);
        if (prepared is not null && (prepared.ServerUrl != server || !JsonElement.DeepEquals(prepared.Workflow, workflow)))
            throw new AiGenerationException("The prepared workflow changed. Start a new request instead of replacing its captured inputs.");
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request, prepared ?? new ComfySavedOperation(server, clientId, workflow, options), ct);
        ct.ThrowIfCancellationRequested();
        await context.BeginRemoteAsync(operation, server, clientId);
        using var response = await SubmitAsync(context, operation, http, workflow, ct);
        if (!response.IsSuccessStatusCode)
        {
            // A rejected graph is safe to repair. Server/transport failures may have
            // happened after acceptance and must retain the uncertain receipt.
            if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
                await context.FinishRemoteAsync(operation, cancelled: true);
            throw new AiGenerationException(options.RejectedMessage);
        }
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!receipt.RootElement.TryGetProperty("prompt_id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var promptId))
            throw new AiGenerationException("ComfyUI returned no valid acceptance receipt. Check its queue before retrying.");
        await context.AcceptRemoteAsync(operation, promptId.ToString("D"));
        yield return new(new(GenerationPhase.Queued, "Queued in ComfyUI…"), promptId.ToString("D"));
        await foreach (var update in ObserveAsync(context, operation, http, checkQueue: false, ct: ct, onProviderCompleted: onProviderCompleted)) yield return update;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(AiJobContext context, string operation,
        HttpClient http, JsonElement workflow, CancellationToken ct)
    {
        try { return await http.PostAsJsonAsync("prompt", workflow, ct); }
        catch (ComfyAccessException e) when (e.DefinitelyNotSubmitted)
        {
            // Only the submission call may retire this intent. The same 403 during
            // history/output retrieval says nothing about an already accepted job.
            // Unknown redirects, HTML, 5xx and lost responses stay uncertain.
            await context.FinishRemoteAsync(operation, cancelled: true);
            throw;
        }
    }

    public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(AiJobContext context, string operation, HttpClient http,
        bool checkQueue = true, [EnumeratorCancellation] CancellationToken ct = default, Action? onProviderCompleted = null)
    {
        var saved = await context.ReadOperationAsync<ComfySavedOperation>(operation, AiOperationArtifact.Request, ct)
            ?? throw new AiGenerationException("The saved ComfyUI workflow is missing. It cannot be regenerated automatically.");
        if (Address(http) != saved.ServerUrl) throw new AiGenerationException("Reconnect to this request's original ComfyUI server to retrieve it.");
        var submission = (await context.ExecutionAsync(ct)).Submissions.SingleOrDefault(s => s.Operation == operation)
            ?? throw new AiGenerationException("This workflow has no saved submission intent.");
        if (submission.ServerUrl != saved.ServerUrl || submission.ClientId != saved.ClientId || saved.Options is null || saved.Workflow.ValueKind != JsonValueKind.Object)
            throw new AiGenerationException("The workflow and submission receipt do not match.");
        if (submission.PromptId is null) submission = await FindReceiptAsync(context, submission, saved, http, ct) ?? submission;
        if (await RetryStoppedAsync(context, operation, submission, http, ct))
            saved = (await context.ReadOperationAsync<ComfySavedOperation>(operation, AiOperationArtifact.Request, ct))!;
        // Reconciliation may have found the original receipt without replaying it.
        submission = (await context.ExecutionAsync(ct)).Submissions.Single(s => s.Operation == operation);
        if (submission.PromptId is null)
        {
            context.MissingRemoteOperation = operation;
            throw new AiGenerationException("Submission acceptance is unknown. Use Retry when connected to authorize checking and restarting missing work; ordinary Check status never repeats inference.");
        }
        var output = await context.ReadOperationAsync<JsonElement?>(operation, AiOperationArtifact.Output, ct);
        if (output is { } published)
        {
            onProviderCompleted?.Invoke();
            if (submission.MayBeRunning) await context.FinishRemoteAsync(operation);
            if (IsFailed(published)) throw new AiJobRecoveryException(ComfyExecutionMonitor.ExecutionFailureMessage(saved.Options, published), AiJobRecovery.GenerateAgain);
            yield return Complete(submission.PromptId, published); yield break;
        }
        if (checkQueue)
        {
            using var history = await GetJsonAsync(http, "history/" + Uri.EscapeDataString(submission.PromptId), ct);
            if (history.RootElement.TryGetProperty(submission.PromptId, out var job))
            {
                if (IsFailed(job))
                {
                    onProviderCompleted?.Invoke();
                    await SaveOutputAsync(context, operation, job, ct);
                    throw new AiJobRecoveryException(ComfyExecutionMonitor.ExecutionFailureMessage(saved.Options, job), AiJobRecovery.GenerateAgain);
                }
                if (IsComplete(job))
                {
                    onProviderCompleted?.Invoke();
                    await SaveOutputAsync(context, operation, job, ct);
                    yield return Complete(submission.PromptId, job.Clone()); yield break;
                }
            }
            using var queue = await GetJsonAsync(http, "queue", ct);
            if (!Queued(queue.RootElement, submission.PromptId))
            {
                context.MissingRemoteOperation = operation;
                throw new AiGenerationException("The accepted job is missing from ComfyUI queue and history. Check the server or cancel it locally; no inference has been repeated.");
            }
        }
        await using var observer = monitor.ObserveAsync(http, submission.PromptId, submission.ClientId, saved.Options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            AiGenerationException? observationFailure = null; bool moved;
            try { moved = await observer.MoveNextAsync(); }
            catch (AiGenerationException e) when (!ct.IsCancellationRequested) { moved = false; observationFailure = e; }
            if (observationFailure is not null)
            {
                // Execution errors and connection loss use the same monitor exception.
                // Only authoritative terminal history releases the provider reservation.
                using var history = await GetJsonAsync(http, "history/" + Uri.EscapeDataString(submission.PromptId), ct);
                if (history.RootElement.TryGetProperty(submission.PromptId, out var terminal) && (IsFailed(terminal) || IsComplete(terminal)))
                {
                    onProviderCompleted?.Invoke();
                    await SaveOutputAsync(context, operation, terminal, ct);
                    if (IsFailed(terminal)) throw new AiJobRecoveryException(ComfyExecutionMonitor.ExecutionFailureMessage(saved.Options, terminal), AiJobRecovery.GenerateAgain);
                    yield return Complete(submission.PromptId, terminal.Clone()); yield break;
                }
                throw observationFailure;
            }
            if (!moved) break;
            var update = observer.Current;
            if (update.Complete && update.Job is { } result)
            {
                onProviderCompleted?.Invoke();
                await SaveOutputAsync(context, operation, result, ct);
                if (update.Timings is { } timings)
                {
                    try { await context.SaveOperationAsync(operation, AiOperationArtifact.Timings, timings, ct); }
                    catch (Exception e) when (e is WorkspaceStoreException or IOException)
                    { throw new AiJobRecoveryException("ComfyUI completed this request, but its observed timings could not be saved. Retry retrieval without generating again; missing timings will be unavailable.", AiJobRecovery.RetryOutput, e); }
                }
            }
            yield return update;
        }
    }
    private static async Task SaveOutputAsync(AiJobContext context, string operation, JsonElement job, CancellationToken ct)
    {
        // Mark the remote workload complete first. Failure to save an already finished
        // output should not masquerade as an uncertain inference request.
        await context.FinishRemoteAsync(operation);
        try { await context.SaveOperationAsync(operation, AiOperationArtifact.Output, job, ct); }
        catch (Exception e) when (e is WorkspaceStoreException or IOException)
        { throw new AiJobRecoveryException("ComfyUI completed this request, but its output receipt could not be saved. Retry retrieval without generating again.", AiJobRecovery.RetryOutput, e); }
    }
    public async Task<JsonElement?> ReadStoppedOutputAsync(AiJobContext context, string operation, HttpClient http, CancellationToken ct)
    {
        // This path never observes, retries, or submits inference. Only terminal
        // history belonging to the exact captured server/prompt may be imported.
        var submission = (await context.ExecutionAsync(ct)).Submissions.Single(s => s.Operation == operation);
        if (Address(http) != submission.ServerUrl || submission.MayBeRunning)
            throw new AiGenerationException("Confirm the original ComfyUI request has stopped before recovering its outputs.");
        var saved = await context.ReadOperationAsync<JsonElement?>(operation, AiOperationArtifact.Output, ct);
        if (saved is { } output && (IsComplete(output) || IsFailed(output))) return output;
        if (submission.PromptId is null) return null;
        using var history = await GetJsonAsync(http, "history/" + Uri.EscapeDataString(submission.PromptId), ct);
        if (!history.RootElement.TryGetProperty(submission.PromptId, out var job)) return null; // A deleted queued request has no outputs.
        if (!IsComplete(job) && !IsFailed(job))
            throw new AiGenerationException("ComfyUI has not published the stopped request's final history yet. Retry retrieving its completed takes.");
        await context.SaveOperationAsync(operation, AiOperationArtifact.Output, job, ct);
        return job.Clone();
    }

    public async Task<bool> CancelAsync(AiJobContext context, Func<string, HttpClient> clientForServer, CancellationToken ct)
    {
        var submissions = (await context.ExecutionAsync(ct)).Submissions.Where(s => s.MayBeRunning).ToArray();
        var confirmed = true;
        foreach (var original in submissions)
        {
            var submission = original;
            using var http = clientForServer(submission.ServerUrl);
            if (Address(http) != submission.ServerUrl) throw new AiGenerationException("Cancellation must use the original ComfyUI server.");
            if (submission.PromptId is null)
            {
                var saved = await context.ReadOperationAsync<ComfySavedOperation>(submission.Operation, AiOperationArtifact.Request, ct);
                if (saved is not null) submission = await FindReceiptAsync(context, submission, saved, http, ct) ?? submission;
                if (submission.PromptId is null)
                {
                    if (saved is not null && (await context.IsComfyStopRequestedAsync(ct) ||
                        context.Clock.GetUtcNow() - submission.StartedUtc >= ComfyQueuePolicy.LostRemoteGrace))
                    {
                        // Pause-and-retain is an explicit stop/restart intent, and a
                        // lost submission this old cannot still be arriving. Check once
                        // more before retiring an unacknowledged legacy request.
                        submission = await FindReceiptAsync(context, submission, saved, http, ct) ?? submission;
                        if (submission.PromptId is null) { await context.FinishRemoteAsync(submission.Operation, cancelled: true); continue; }
                    }
                    else { confirmed = false; continue; }
                }
            }
            // A prior cancel may have succeeded after its immediate queue check, or
            // ComfyUI may have restarted. Confirm absence before sending another cancel.
            using (var before = await GetJsonAsync(http, "queue", ct))
            {
                if (!Queued(before.RootElement, submission.PromptId))
                { await context.FinishRemoteAsync(submission.Operation, cancelled: true); continue; }
            }
            using var direct = await http.PostAsJsonAsync($"api/jobs/{Uri.EscapeDataString(submission.PromptId)}/cancel", new { }, ct);
            if (!direct.IsSuccessStatusCode)
            {
                using var remove = await http.PostAsJsonAsync("queue", new { delete = new[] { submission.PromptId } }, ct);
                if (!remove.IsSuccessStatusCode) { confirmed = false; continue; }
            }
            using var queue = await GetJsonAsync(http, "queue", ct);
            if (Queued(queue.RootElement, submission.PromptId)) confirmed = false;
            else await context.FinishRemoteAsync(submission.Operation, cancelled: true);
        }
        return confirmed;
    }
    private static async Task<AiRemoteSubmission?> FindReceiptAsync(AiJobContext context, AiRemoteSubmission submission, ComfySavedOperation saved, HttpClient http, CancellationToken ct)
    {
        if (saved.ServerUrl != submission.ServerUrl || saved.ClientId != submission.ClientId ||
            !saved.Workflow.TryGetProperty("prompt", out var graph)) throw new AiGenerationException("The saved workflow cannot identify its owned submission.");
        var matches = new HashSet<string>(StringComparer.Ordinal);
        void Consider(JsonElement row)
        {
            // ComfyUI records (number, prompt_id, prompt, extra_data, outputs).
            // Require both our unique client ID and exact graph, never a similar prompt.
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4 || row[1].ValueKind != JsonValueKind.String ||
                row[3].ValueKind != JsonValueKind.Object || !row[3].TryGetProperty("client_id", out var client) ||
                client.ValueKind != JsonValueKind.String || client.GetString() != submission.ClientId || !JsonElement.DeepEquals(row[2], graph)) return;
            if (Guid.TryParse(row[1].GetString(), out var id)) matches.Add(id.ToString("D"));
        }
        using var queue = await GetJsonAsync(http, "queue", ct);
        foreach (var key in new[] { "queue_running", "queue_pending" })
        {
            if (!queue.RootElement.TryGetProperty(key, out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new AiGenerationException("ComfyUI's queue is unreadable; the submission remains unconfirmed.");
            foreach (var row in rows.EnumerateArray()) Consider(row);
        }
        // A receipt outside a truncated recent-history page is not proof of absence.
        using var history = await GetJsonAsync(http, "history", ct);
        if (history.RootElement.ValueKind != JsonValueKind.Object) throw new AiGenerationException("ComfyUI history is unreadable; the submission remains unconfirmed.");
        foreach (var item in history.RootElement.EnumerateObject())
            if (item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("prompt", out var row)) Consider(row);
        if (matches.Count > 1) throw new AiGenerationException("More than one ComfyUI job matches this owned workflow. Inspect the server queue before retrying; no work was resubmitted.");
        if (matches.Count == 0) return null;
        var promptId = matches.Single(); await context.AcceptRemoteAsync(submission.Operation, promptId);
        return submission with { PromptId = promptId, State = AiRemoteState.Accepted };
    }
    private static string Address(HttpClient http) => http.BaseAddress is { } address && address.Scheme is "http" or "https" &&
        address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0
        ? address.AbsoluteUri.TrimEnd('/') : throw new AiGenerationException("A valid ComfyUI server address is required.");
    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string path, CancellationToken ct)
    {
        // Status probes must not inherit a multi-hour generation timeout while
        // reconnecting. Keep caller cancellation distinct from a failed probe.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.GetAsync(path, timeout.Token);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiGenerationException("ComfyUI status check timed out. Remote state remains unconfirmed; no new workflow was submitted by this check."); }
    }
    private static bool Queued(JsonElement queue, string promptId)
    {
        var found = false;
        foreach (var key in new[] { "queue_running", "queue_pending" })
        {
            if (!queue.TryGetProperty(key, out var rows) || rows.ValueKind != JsonValueKind.Array) throw new AiGenerationException("ComfyUI returned an unreadable queue; remote state is still unconfirmed.");
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2 || row[1].ValueKind != JsonValueKind.String)
                    throw new AiGenerationException("ComfyUI returned an unreadable queued job; remote state is still unconfirmed.");
                found |= row[1].GetString() == promptId;
            }
        }
        return found;
    }
    private static bool IsFailed(JsonElement job) => job.TryGetProperty("status", out var status) &&
        status.TryGetProperty("status_str", out var state) && state.GetString() == "error";
    private static bool IsComplete(JsonElement job) => job.TryGetProperty("status", out var status) &&
        status.TryGetProperty("completed", out var complete) && complete.ValueKind == JsonValueKind.True;
    private static ComfyExecutionUpdate Complete(string promptId, JsonElement job) => new(new(GenerationPhase.Finalizing, "Retrieving saved ComfyUI result…"), promptId, job, true);
}
