using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed class AiTextJobHandler(IAiProviderRegistry providers, IHttpClientFactory clients, ComfyJobExecution comfy,
    IProjectStore projects, IPromptEnhancer enhancer, IGuidanceAssistant guidance, TimeProvider clock, ICodexClient? codex = null, lumibelle.Services.Production.IProductionStore? production = null, IAssetReelStore? reels = null,
    VisualBriefCache? briefs = null) : IAiJobHandler
{
    private const string Operation = "text";
    // First step of a two-step composition; its ComfyUI output is the visual brief.
    private const string BriefOperation = "brief";
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ScriptAssistant, AiJobKind.AssetExtraction, AiJobKind.ShotPlanning, AiJobKind.PromptEnhancement, AiJobKind.Guidance, AiJobKind.PromptComposition, AiJobKind.ReelComposition, AiJobKind.AssetPicking, AiJobKind.ShotTranslation];

    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(context.Job, snapshot);
        using var timeout = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var output = new StringBuilder(); string? finish = null;
        OpenRouterRequestUsage? usage = null;
        DateTimeOffset? lastTextProgress = null;
        try
        {
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Checking the captured text model and inputs…")), checkpoint: true);
            await ValidateInputsAsync(context.Job, request, linked.Token);
            // Kept on every progress report, so the warning stays visible while the request runs.
            var capacityNotice = ComfyTextCapacity.Notice(request);
            var watch = request.Model.Backend == AiBackend.ComfyUI
                ? new ComfyGenerationWatch(ComfyGenerationWatch.ExpectedTokensPerSecond(request.Model, request.Settings)) : null;
            string? Notice(GenerationProgress? progress) =>
                string.Join(" ", new[] { capacityNotice, progress is null ? null : watch?.Observe(progress) }.Where(n => n is not null)) is { Length: > 0 } text ? text : null;
            await foreach (var update in GenerateAsync(context, request, timeout, linked.Token))
            {
                timeout.Observe(update);
                var previousUsage = usage;
                if (update.OpenRouterUsage is { } reported) usage = OpenRouterUsageParser.Merge(usage, reported);
                if (usage != previousUsage && usage is not null)
                {
                    // Billing receipts remain saveable if cancellation arrived with the final usage event.
                    try { await context.SaveObservedUsageAsync(usage); }
                    catch (Exception e) when (e is WorkspaceStoreException or OperationCanceledException) { /* Final publication also retains the report. */ }
                }
                if (update.Progress is { } progress) await context.ReportAsync(new(progress, Notice: Notice(progress)));
                if (update.Response?.FinishReason is { } reason && (finish is null || finish == ChatFinishReason.Stop.ToString())) finish = reason.ToString();
                if (!string.IsNullOrEmpty(update.Response?.Text))
                {
                    output.Append(update.Response.Text);
                    try { await context.SaveResultAsync(new AiTextJobResult(output.ToString(), FinishReason: finish) { OpenRouterUsage = usage }, checkpoint: false); }
                    catch (WorkspaceStoreException)
                    {
                        // Retain the stream in memory and keep observing it. A failed
                        // partial checkpoint must not trigger another paid request.
                        await context.ReportAsync(new(new(GenerationPhase.Generating, "Response arriving; waiting to save its checkpoint…"), Notice: Notice(null)));
                    }
                    var now = clock.GetUtcNow();
                    if (lastTextProgress is null || now - lastTextProgress >= TimeSpan.FromSeconds(2))
                    {
                        // Notify reviews after checkpointing the stream, without publishing every token.
                        await context.ReportAsync(new(new(GenerationPhase.Generating, "Receiving response from " + request.Model.Name + "…",
                            Current: output.Length, Unit: "characters", Elapsed: now - (context.Job.StartedUtc ?? now)), Notice: Notice(null)));
                        lastTextProgress = now;
                    }
                }

            }
            linked.Token.ThrowIfCancellationRequested();
            timeout.Stop();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (request.Model.Backend == AiBackend.ComfyUI)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelTimeout.Token);
                try { await comfy.CancelAsync(context, ComfyClient, cancel.Token); }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
            }
            throw new AiGenerationException(timeout.Expired ? timeout.Message : "The text provider timed out. Its saved partial response remains inspectable; retry explicitly.");
        }
        // Once a paid stream is complete, retain it as an immutable output before
        // parsing. Result-publication retry and restart then need no provider call.
        var raw = new AiTextJobResult(output.ToString(), true, finish) { OpenRouterUsage = usage };
        if (request.Model.Backend is AiBackend.OpenRouter or AiBackend.Codex or AiBackend.ClaudeCode)
            await PersistAsync(context, () => context.SaveOperationAsync(Operation, AiOperationArtifact.Output, raw, ct), ct);
        return await FinishAsync(context, request, raw with { VisualBrief = await BriefAsync(context, request, ct) }, ct);
    }
    public async Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        var request = Read(context.Job, snapshot);
        // Applying remains a studio concern. Reopening a completed proposal must retain
        // its generated block/shot/proposal identities, not run its parser again.
        if (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, ct) is { Complete: true } complete)
        {
            if (request.Repair is null && request.Kind == AiJobKind.PromptComposition && production is not null && complete.Error is null)
                await production.ApplyResultAsync(context.Job.Target.ProjectId!.Value, context.Job.Id, true, ct);
            if (request.Repair is null && request.Kind == AiJobKind.ReelComposition && reels is not null && complete.Error is null)
                await ApplyReel(context, request, complete, ct);
            return Outcome(complete);
        }
        if (request.Model.Backend is AiBackend.OpenRouter or AiBackend.Codex or AiBackend.ClaudeCode)
        {
            var saved = await context.ReadOperationAsync<AiTextJobResult>(Operation, AiOperationArtifact.Output, ct);
            return saved is { Complete: true } ? await FinishAsync(context, request, saved, ct)
                : AiJobOutcome.Attention("The text request was interrupted. Its saved response remains inspectable; retry explicitly to make another request.", AiJobRecovery.GenerateAgain);
        }
        using var timeout = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var http = ComfyClient(request.Model.ComfyUrl!);
        var submissions = (await context.ExecutionAsync(ct)).Submissions;
        if (request.TwoStep && submissions.All(s => s.Operation != Operation))
        {
            // Recovery never submits new work. Finish observing step one so its brief is cached for the next request.
            if (submissions.Any(s => s.Operation == BriefOperation))
                try
                {
                    await foreach (var update in comfy.ObserveAsync(context, BriefOperation, http, ct: linked.Token))
                    {
                        timeout.Observe(update.Progress);
                        await context.ReportAsync(new(Step(update.Progress, 1)));
                        if (update.Complete && update.Job is { } briefJob && ComfyChatClient.TryReadText(briefJob, out var text) &&
                            PromptComposer.ReadBrief(text) is { } brief && briefs is not null)
                            await briefs.WriteAsync(request.BriefKey!, brief, ct);
                    }
                }
                catch (Exception e) when (e is AiGenerationException or AiJobRecoveryException) { }
            return AiJobOutcome.Attention("The request stopped before composing. Generate again; a finished visual brief is reused.", AiJobRecovery.GenerateAgain);
        }
        try
        {
            await foreach (var update in comfy.ObserveAsync(context, Operation, http, ct: linked.Token, onProviderCompleted: timeout.Stop))
            {
                timeout.Observe(update.Progress);
                await context.ReportAsync(new(update.Progress));
                if (!update.Complete || update.Job is not { } job) continue;
                timeout.Stop();
                if (!ComfyChatClient.TryReadText(job, out var raw)) throw new AiJobRecoveryException("ComfyUI completed without the expected text output. Inspect it and retry explicitly.", AiJobRecovery.GenerateAgain);
                return await FinishAsync(context, request, new(raw, true) { VisualBrief = await BriefAsync(context, request, ct) }, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            try { await comfy.CancelAsync(context, ComfyClient, cancel.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or AiGenerationException or WorkspaceStoreException) { }
            throw new AiGenerationException(timeout.Message);
        }
        return AiJobOutcome.Attention("No completed text output is available yet. Check the accepted request again.", AiJobRecovery.CheckStatus);
    }
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) =>
        context.Job.Backend == AiBackend.ComfyUI ? comfy.CancelAsync(context, ComfyClient, ct) : Task.FromResult(true);

    private async IAsyncEnumerable<ProgressingChatUpdate> GenerateAsync(AiJobContext context, AiTextJobRequest request, TextInactivityWatchdog timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        if (request.Model.Backend == AiBackend.Codex)
        {
            if (request.Codex is null || codex is null) throw new AiGenerationException("The Codex request is incomplete.");
            await context.SaveOperationAsync(Operation, AiOperationArtifact.Request, new CodexReceipt(), ct);
            await foreach (var update in codex.GenerateAsync(new(request.Settings.Codex, request.Codex, Path.Combine(context.Directory, "codex"), request.Messages), ct))
            {
                if (update.ThreadId is not null) await context.SaveOperationAsync(Operation + (update.Complete ? "/complete" : update.TurnId is null ? "/thread" : "/turn"), AiOperationArtifact.Request, new CodexReceipt(update.ThreadId, update.TurnId, update.Complete), ct);
                if (update.Progress is not null) yield return new(Progress: update.Progress);
                if (update.Activity) yield return new(Activity: true);
                if (update.Text is not null) yield return new(new ChatResponseUpdate(ChatRole.Assistant, update.Text) { ModelId = request.Model.Model });
                if (update.Complete) yield return new(new ChatResponseUpdate(ChatRole.Assistant, "") { FinishReason = ChatFinishReason.Stop, ModelId = request.Model.Model });
            }
            yield break;
        }
        var messages = request.Messages.Select(m => m.ToMessage()).ToList();
        if (request.Model.Backend == AiBackend.ComfyUI)
        {
            using var http = ComfyClient(request.Model.ComfyUrl!);
            var nativeSystemPrompt = await ComfyTextCapabilities.UseSystemPromptAsync(http, ComfyTextCapabilities.SystemPromptVersions(request.Model, request.Settings), ct);
            if (request.TwoStep)
            {
                var brief = await BriefAsync(context, request, ct);
                if (brief is null)
                {
                    var briefInput = ComfyTextVision.Capture(request.BriefMessages!.Select(m => m.ToMessage()), nativeSystemPrompt);
                    yield return new(Progress: new(GenerationPhase.Preparing, "Step 1 of 2 · preparing reference images…"));
                    var briefImages = await ComfyTextVision.UploadAsync(http, request.Model.Model, ComfyTextVision.Mode(request.Model, request.Settings),
                        briefInput.Images, ct, ComfyTextSettings.BatchImageSide(request.Model, request.Settings));
                    var briefTokens = Math.Min(PromptComposer.BriefTokens, TextGenerationOptions.Captured(request).MaxOutputTokens!.Value);
                    await foreach (var update in comfy.ExecuteAsync(context, BriefOperation, http, client => ComfyTextVision.BuildWorkflow(request.Model.Model,
                        briefInput.Transcript, briefTokens, BriefTemperature, request.Seed, client, briefImages, briefInput.SystemPrompt), ComfyChatClient.ExecutionOptions, ct))
                    {
                        yield return new(Progress: Step(update.Progress, 1));
                        if (update.Complete && update.Job is { } job && ComfyChatClient.TryReadText(job, out var text)) brief = PromptComposer.ReadBrief(text);
                    }
                    if (brief is null) throw new AiJobRecoveryException("The first step returned no usable visual brief. Generate again, or turn off image sending and use saved descriptions.", AiJobRecovery.GenerateAgain);
                    if (briefs is not null) await briefs.WriteAsync(request.BriefKey!, brief, ct);
                }
                messages = PromptComposer.WithBrief(request.Messages, brief).Select(m => m.ToMessage()).ToList();
            }
            var input = ComfyTextVision.Capture(messages, nativeSystemPrompt);
            if (input.Images.Count > 0) yield return new(Progress: new(GenerationPhase.Preparing, "Preparing ComfyUI vision inputs…"));
            var uploaded = await ComfyTextVision.UploadAsync(http, request.Model.Model,
                ComfyTextVision.Mode(request.Model, request.Settings), input.Images, ct, ComfyTextSettings.BatchImageSide(request.Model, request.Settings));
            var localOptions = TextGenerationOptions.Captured(request);
            await foreach (var update in comfy.ExecuteAsync(context, Operation, http,
                client => ComfyTextVision.BuildWorkflow(request.Model.Model, input.Transcript, localOptions.MaxOutputTokens!.Value, localOptions.Temperature!.Value, request.Seed, client, uploaded, input.SystemPrompt), ComfyChatClient.ExecutionOptions, ct, onProviderCompleted: timeout.Stop))
            {
                yield return new(Progress: request.TwoStep ? Step(update.Progress, 2) : update.Progress);
                if (!update.Complete || update.Job is not { } job) continue;
                if (!ComfyChatClient.TryReadText(job, out var raw)) throw new AiJobRecoveryException("ComfyUI completed without the expected text output. Inspect it and retry explicitly.", AiJobRecovery.GenerateAgain);
                yield return new(new ChatResponseUpdate(ChatRole.Assistant, raw) { ModelId = request.Model.Model, ResponseId = update.PromptId, FinishReason = ChatFinishReason.Stop });
            }
            yield break;
        }
        using var client = await providers.CreateAsync(request.Model.Backend, request.Model.Model, request.Settings, ct);
        var options = TextGenerationOptions.Captured(request);
        yield return new(Progress: new(GenerationPhase.Generating, "Waiting for " + request.Model.Name + " to respond…"));
        await using var stream = PromptEnhancer.Stream(client, messages, options, ct).GetAsyncEnumerator(ct);
        while (await PromptEnhancer.MoveNext(stream, ct, timeout)) yield return stream.Current;
    }
    private async Task ValidateInputsAsync(AiJobHeader job, AiTextJobRequest request, CancellationToken ct)
    {
        if (await projects.GetAsync(job.Target.ProjectId!.Value, ct) is null) throw new AiGenerationException("The target project no longer exists. The saved request has not been redirected.");
        var check = await providers.CheckAsync(request.Model.Backend, request.Settings, cancellationToken: ct);
        if (TextModelPolicy.Issue(request.Model, request.Settings, check, !request.FollowsDefault) is { } issue) throw new AiGenerationException(issue);
        if (request.InspectsImages && (!TextVisionPolicy.SupportsBackend(request.Model.Backend) || check.Models.FirstOrDefault(m => m.Id == request.Model.Model)?.SupportsImages != true))
            throw new AiGenerationException(TextVisionPolicy.SetupHint + " The server must still advertise the required image nodes.");
        // A repair sends captured text only. Current media/target checks still run
        // when the resulting suggestion is explicitly applied in its studio.
        if (request.Repair is not null) return;
        if (request.Kind == AiJobKind.PromptEnhancement) await enhancer.ValidateInputsAsync(request.Payload<PromptEnhancementRequest>().Context, ct);
        if (request.Kind == AiJobKind.Guidance)
        {
            var payload = request.Payload<GuidanceRequest>();
            var current = (await guidance.ReadTargetAsync(payload.Context.Target, ct)).Fingerprint();
            if (current != request.GuidanceBaseline && current != payload.Context.Fingerprint())
                throw new AiGenerationException("The saved guidance target changed while this request waited. Review it and request a new suggestion.");
            if (payload.InspectionImage is { } image) await guidance.ValidateImageAsync(payload.Context.Target.ProjectId, image, ct);
        }
    }
    public static AiTextJobRequest Read(AiJobHeader job, JsonElement snapshot)
    {
        var request = snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("The saved text request is empty.");
        if (request.Version is not (1 or 2 or 3) || request.Kind != job.Kind || request.Model is null || request.Model.Backend != job.Backend || request.Settings is null ||
            request.Version < 3 && TextModelProfiles.RequiresSnapshotVersion3(request.Model) ||
            request.Seed < 1 || !float.IsFinite(request.Temperature) || request.Temperature is < 0 or > 2 ||
            request.Model.Backend == AiBackend.ComfyUI && request.Temperature < 0.01f || string.IsNullOrWhiteSpace(request.Profile) ||
            request.Messages is not { Count: > 0 } || request.Messages.Any(m => m is null || m.Role is not ("system" or "user" or "assistant") || m.Parts is null ||
                m.Parts.Any(p => p is null || (p.Text is not null) == (p.Image is not null) || p.Image is { Length: 0 } || p.Image is not null && p.MediaType != "image/png")))
            throw new WorkspaceStoreException("The saved text request contains invalid settings or messages.");
        TextModelPolicy.Validate(request.Model); TextModelPolicy.CheckRequestServer(request.Model, request.Settings); FileAiSettingsStore.Validate(request.Settings);
        var expected = request.Kind switch
        {
            AiJobKind.ScriptAssistant => new AiJobTarget(request.Payload<ScriptAssistantRequest>().Script.ProjectId),
            AiJobKind.AssetExtraction => new AiJobTarget(request.Payload<AssetExtractionRequest>().Script.ProjectId),
            AiJobKind.ShotPlanning => new AiJobTarget(request.Payload<ShotPlanningRequest>().Script.ProjectId),
            AiJobKind.ShotTranslation => new AiJobTarget(request.Payload<ShotDubRequest>().ProjectId,
                ShotId: request.Payload<ShotDubRequest>().ShotId, TakeId: request.Payload<ShotDubRequest>().SourceTakeId),
            AiJobKind.AssetPicking => new AiJobTarget(request.Payload<AssetPickRequest>().ProjectId, ShotId: request.Payload<AssetPickRequest>().Shot.Id),
            AiJobKind.ReelComposition => new AiJobTarget(request.Payload<ReelCompositionRequest>().ProjectId, request.Payload<ReelCompositionRequest>().Draft.AssetId, ReelId: request.Payload<ReelCompositionRequest>().Draft.Id),
            AiJobKind.PromptComposition => new AiJobTarget(request.Payload<PromptCompositionRequest>().ProjectId, ShotId: request.Payload<PromptCompositionRequest>().Shot.Id, CompositionId: request.Payload<PromptCompositionRequest>().CompositionId),
            AiJobKind.PromptEnhancement => new AiJobTarget(request.Payload<PromptEnhancementRequest>().Context.ProjectId, request.Payload<PromptEnhancementRequest>().Context.AssetId),
            AiJobKind.Guidance => GuidanceTarget(request.Payload<GuidanceRequest>().Context.Target),
            _ => throw new WorkspaceStoreException("Unknown text operation.")
        };
        if (expected != job.Target) throw new WorkspaceStoreException("The text request belongs to a different target.");
        if (request.Kind == AiJobKind.AssetPicking) AssetPicker.ValidateRequest(request.Payload<AssetPickRequest>());
        if (request.Kind == AiJobKind.ShotTranslation)
        {
            if (request.Profile != ShotDubbing.Profile) throw new WorkspaceStoreException("The translation contract is unavailable.");
            ShotDubbing.ValidateRequest(request.Payload<ShotDubRequest>());
        }
        AiTextRepairs.Validate(job, request);
        if (request.TwoStep && (request.Kind != AiJobKind.PromptComposition || request.Model.Backend != AiBackend.ComfyUI || request.Repair is not null ||
            request.BriefKey is not { Length: 64 } key || !key.All(char.IsAsciiHexDigit) || (request.BriefMessages is null) == (request.VisualBrief is null) ||
            request.VisualBrief is not null && PromptComposer.ReadBrief(request.VisualBrief) != request.VisualBrief ||
            request.Messages.Any(m => m.Parts.Any(p => p.Image is not null)) ||
            request.BriefMessages is { } briefMessages && (briefMessages.Count != 2 || briefMessages[0].Role != "system" || briefMessages[1].Role != "user" ||
                briefMessages.Any(m => m.Parts is null || m.Parts.Any(p => p is null || (p.Text is not null) == (p.Image is not null) || p.Image is { Length: 0 } ||
                    p.Image is not null && p.MediaType != "image/png")))) ||
            !request.TwoStep && (request.BriefMessages is not null || request.VisualBrief is not null))
            throw new WorkspaceStoreException("The saved two-step composition is incomplete.");
        var imageCount = request.Messages.Concat(request.BriefMessages ?? []).Sum(m => m.Parts.Count(p => p.Image is not null));
        var expectedImages = request.Repair is not null || request.VisualBrief is not null ? 0 : request.Kind switch
        {
            AiJobKind.PromptEnhancement => request.Payload<PromptEnhancementRequest>() is { InspectImages: true } p ? p.Context.References.Count : 0,
            AiJobKind.Guidance => request.Payload<GuidanceRequest>().InspectionImage is null ? 0 : 1,
            AiJobKind.PromptComposition => request.Payload<PromptCompositionRequest>().InspectReferenceImages == false
                ? 0 : request.Payload<PromptCompositionRequest>().Images.Count + RefModFrameCount(request),
            AiJobKind.ReelComposition => request.Payload<ReelCompositionRequest>().Images.Count + RefModFrameCount(request),
            _ => 0
        };
        if (imageCount != expectedImages || imageCount > 0 && !TextVisionPolicy.SupportsBackend(job.Backend))
            throw new WorkspaceStoreException("Inspection image content does not match the captured request.");
        ComfyTextVision.ValidateSnapshot(request);
        if (request.Repair is null && request.Kind == AiJobKind.PromptComposition)
            CompositionDescriptions.Validate(request.Payload<PromptCompositionRequest>());
        if (request.Repair is null && request.Kind == AiJobKind.Guidance)
            VisualDescriptionAssistance.ValidateTarget(request.Payload<GuidanceRequest>());
        return request;
    }
    private static int RefModFrameCount(AiTextJobRequest request) => (request.Kind switch {
            AiJobKind.PromptComposition => request.Payload<PromptCompositionRequest>().Shot.Videos,
            AiJobKind.ReelComposition => request.Payload<ReelCompositionRequest>().Draft.KeyframeReels ?? [],
            _ => new List<ShotVideoBinding>() })
            .Where(binding => binding.EffectiveVisuals == ReelVisuals.RefMod)
            .Sum(binding => binding.RefMod?.Recipe.FrameHashes.Count ?? 0);

    private const float BriefTemperature = 0.3f;
    private static GenerationProgress Step(GenerationProgress progress, int step) => progress with { Label = $"Step {step} of 2 · {progress.Label}" };

    /// <summary>The brief of a two-step composition: captured from the cache, or written by this job's first step.</summary>
    private static async Task<string?> BriefAsync(AiJobContext context, AiTextJobRequest request, CancellationToken ct)
    {
        if (!request.TwoStep) return null;
        if (request.VisualBrief is { } captured) return captured;
        var output = await context.ReadOperationAsync<JsonElement?>(BriefOperation, AiOperationArtifact.Output, ct);
        return output is { } job && ComfyChatClient.TryReadText(job, out var text) ? PromptComposer.ReadBrief(text) : null;
    }

    private static AiJobTarget GuidanceTarget(GuidanceTarget target) => new(target.ProjectId, target.AssetId, GuidanceScope: target.Scope, LookId: target.LookId, ImageId: target.ImageId);
    private async Task<AiJobOutcome> FinishAsync(AiJobContext context, AiTextJobRequest request, AiTextJobResult raw, CancellationToken ct)
    {
        var parsed = AiTextResults.Parse(request, raw.Raw, raw.FinishReason) with { OpenRouterUsage = raw.OpenRouterUsage, VisualBrief = raw.VisualBrief };
        await PersistAsync(context, () => context.SaveResultAsync(parsed), ct);
        if (request.Repair is null && request.Kind == AiJobKind.PromptComposition && production is not null && parsed.Error is null)
            await PersistAsync(context, async () => { await production.ApplyResultAsync(context.Job.Target.ProjectId!.Value, context.Job.Id, true, ct); }, ct);
        if (request.Repair is null && request.Kind == AiJobKind.ReelComposition && reels is not null && parsed.Error is null)
            await PersistAsync(context, () => ApplyReel(context, request, parsed, ct), ct);
        return Outcome(parsed);
    }
    private async Task ApplyReel(AiJobContext context, AiTextJobRequest request, AiTextJobResult result, CancellationToken ct)
    {
        var current = await context.CurrentAsync(ct);
        if (!current.CancelRequested && result.Read<ReelPromptPair>() is { } pair)
            await reels!.ApplyPairAsync(current with { State = AiJobState.Completed }, request.Payload<ReelCompositionRequest>(), pair, true, ct);
    }
    private async Task PersistAsync(AiJobContext context, Func<Task> publish, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { await publish(); return; }
            catch (WorkspaceStoreException e) when (e.InnerException is IOException or UnauthorizedAccessException)
            {
                await context.ReportAsync(new(new(GenerationPhase.Saving, "Response complete · waiting for storage; no further generation…")));
                await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
            }
        }
    }
    private static AiJobOutcome Outcome(AiTextJobResult result) => result.Error is null ? AiJobOutcome.Complete()
        : AiJobOutcome.Attention(result.Error, AiJobRecovery.GenerateAgain);
    private HttpClient ComfyClient(string server)
    { var http = clients.CreateClient("ComfyUI"); http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(server) + "/"); return http; }
}
