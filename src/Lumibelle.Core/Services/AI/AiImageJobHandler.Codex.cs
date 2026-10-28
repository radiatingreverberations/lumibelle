using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Services.AI;

public sealed partial class AiImageJobHandler
{
    private async Task CheckCodexAsync(AiImageJobRequest request, CancellationToken ct)
    {
        if (codex is null || request.Codex is null) throw new AiGenerationException("Codex is unavailable.");
        var now = CodexClient.Capture(await codex.CheckAsync(request.Settings.Codex, ct), request.Codex.Model, request.Codex.Effort, true);
        if (now != request.Codex) throw new AiGenerationException("The captured Codex account or version changed. Start a new batch explicitly.");
    }
    internal static IReadOnlyList<AiTextMessage> CodexMessages(AiImageJobRequest request)
    {
        var parts = new List<AiTextPart>
        {
            new(Text: "Generate exactly one image. Follow the author's instruction faithfully, including exact lettering, medium and explicit trigger text. " +
                "Do not invent unseen reference contents from labels or asset notes. " + (request.Edit is null ? "Create a new image." : "Edit image 1 (base), using the other images only as ordered references. Preserve unspecified details.") +
                " Requested aspect ratio: " + (request.AspectRatio == ImageAspectPolicy.FromImage1
                    ? "match Image 1's proportions, including its submitted crop" : request.AspectRatio) +
                ". Return an image through the native image generation tool. Do not crop or stretch a returned image to enforce the ratio."),
            new(Text: "Author's instruction:\n" + request.Prompt),
            new(Text: "Target context (background notes, not proof of reference contents):\n" + JsonSerializer.Serialize(request.Look))
        };
        if (request.Edit?.Regions?.Count > 0) parts.Add(new(Text: RegionalImageEdits.PlacementInstruction));
        foreach (var (image, index) in request.Inputs.Select((image, index) => (image, index)))
        {
            parts.Add(new(Text: $"Image {index + 1} · {(index == 0 ? "Base" : "Reference")} · {image.Context.AssetName}. Already prepared with its submitted crop. Background context: " + JsonSerializer.Serialize(image.Context)));
            parts.Add(new(Image: image.Png, MediaType: "image/png"));
        }
        return [new("user", parts)];
    }
    private async Task<AiJobOutcome> RunCodexAsync(AiJobContext context, AiImageJobRequest request, CancellationToken ct)
    {
        var completed = new List<AiImageCandidateResult>();
        while (true)
        {
            var current = await context.CurrentAsync(ct);
            var candidate = current.Batch!.Candidates.Skip(completed.Count).FirstOrDefault();
            if (candidate is null) return AiJobOutcome.BatchCheckpoint(completed.Count);
            var total = current.Batch.Candidates[^1].Number;
            var operation = "candidate/" + candidate.Id.ToString("D");
            var staged = await context.ReadOperationAsync<AiImageStaging>(operation, AiOperationArtifact.Image, ct);
            var library = await assets.LoadAsync(request.ProjectId, ct);
            if (library.ImagePublications.SingleOrDefault(r => r.ImageId == candidate.Id) is { } receipt)
            {
                if (receipt.JobId != context.Job.Id || receipt.AssetId != request.AssetId || staged is null)
                    throw new AiGenerationException("This candidate's publication receipt does not match its staged output.");
                completed.Add(new(candidate.Id, candidate.Number, candidate.Id, staged.Metadata));
                await SaveResultAsync(context, completed, ct); await context.MarkReviewableAsync(ct); continue;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Settings.ImageTimeoutSeconds), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var token = linked.Token;
            if (staged is null)
            {
                var output = await context.ReadOperationAsync<CodexImageOutput>(operation, AiOperationArtifact.Output, token);
                var receiptBefore = await context.ReadOperationAsync<CodexReceipt>(operation, AiOperationArtifact.Request, token);
                var completion = await context.ReadOperationAsync<CodexReceipt>(operation + "/complete", AiOperationArtifact.Request, token);
                var nativeOperation = operation + "/native-image" + (completion?.NativeImageCount > 0 ? "/" + completion.NativeImageCount : "");
                if (output?.Complete != true && completion is { Completed: true } &&
                    await context.ReadOperationAsync<CodexImageOutput>(nativeOperation, AiOperationArtifact.Output, token) is { Image.Length: > 0 } native)
                {
                    output = native with { Complete = true, Timing = completion.Timing ?? native.Timing };
                    await context.SaveOperationAsync(operation, AiOperationArtifact.Output, output, token);
                }
                if (output?.Complete != true)
                {
                    if (receiptBefore is not null) return AiJobOutcome.Attention("Codex was interrupted. Its saved response remains inspectable. Retry explicitly to generate again.", AiJobRecovery.GenerateAgain);
                    if (context.Recovering) return AiJobOutcome.BatchCheckpoint(completed.Count);
                    await ValidateInputsAsync(request, token); await CheckCodexAsync(request, token);
                    await context.SaveOperationAsync(operation, AiOperationArtifact.Request, new CodexReceipt(), token);
                    output = new();
                    var nativeImageCount = 0;
                    try
                    {
                        await foreach (var update in codex!.GenerateAsync(new(request.Settings.Codex, request.Codex!,
                            Path.Combine(context.Directory, "codex", candidate.Id.ToString("N")), CodexMessages(request), true), token))
                        {
                            if (update.ThreadId is not null) await context.SaveOperationAsync(operation + (update.Complete ? "/complete" : update.TurnId is null ? "/thread" : "/turn"), AiOperationArtifact.Request, new CodexReceipt(update.ThreadId, update.TurnId, update.Complete, update.Complete ? nativeImageCount : 0, update.Timing), token);
                            if (update.Text is not null || update.Image is not null || update.Complete)
                            {
                                output = output with { Text = output.Text + update.Text, Image = update.Image ?? output.Image, RevisedPrompt = update.RevisedPrompt ?? output.RevisedPrompt, Complete = update.Complete, Timing = update.Timing ?? output.Timing };
                                // Preserve native output before validation and publication. Recovery never asks Codex to reproduce it.
                                if (update.Complete) await context.SaveOperationAsync(operation, AiOperationArtifact.Output, output, token);
                                else if (update.Image is not null)
                                {
                                    await context.SaveOperationAsync(operation + "/native-image/" + ++nativeImageCount, AiOperationArtifact.Output, output, token);
                                }
                                else if (update.Text is not null) await context.SaveResultAsync(new AiImageJobResult(completed.ToArray(), output.Text), checkpoint: false);
                            }
                            if (update.Progress is { } progress) await context.ReportAsync(new(progress, candidate.Number, (await context.CurrentAsync(token)).Batch!.Candidates[^1].Number, CodexTiming: update.Timing), true);
                        }
                    }
                    finally
                    {
                        // A no-image failure can arrive before the streaming checkpoint interval.
                        if (output.Text.Length > 0 && !context.Cancellation.IsCancellationRequested)
                            await context.SaveResultAsync(new AiImageJobResult(completed.ToArray(), output.Text));
                    }
                }
                if (output is not { Complete: true, Image.Length: > 0 } || output.Image.LongLength > FileAssetStore.MaximumImageBytes)
                    throw new AiGenerationException("Codex did not return a completed image. Inspect its saved response and retry explicitly.");
                try
                {
                    var info = ImageInspector.Inspect(output.Image);
                    var metadata = AiImageJobPolicy.Metadata(request, context.Job.Id, candidate) with
                    {
                        Provider = AiBackend.Codex, Seed = 0, Steps = 0, DiffusionModel = "", TextEncoder = "", Vae = "",
                        Codex = new(request.Codex!.Model, request.Codex.Version, request.Codex.Effort, info.Width, info.Height, output.RevisedPrompt, output.Timing)
                    };
                    if (metadata.Edit is { } edit) metadata = metadata with { Edit = edit with { Lora = "", LoraStrength = 0, ReferenceBoost = 0, BaseReferenceBoost = null, GroundingPixels = 0, FitMode = "requested-aspect" } };
                    staged = new(candidate.Id, "codex" + info.Extension, output.Image, metadata);
                    await context.SaveOperationAsync(operation, AiOperationArtifact.Image, staged, token);
                }
                catch (Exception e) when (e is IOException or lumibelle.Services.Story.WorkspaceStoreException)
                { throw new AiJobRecoveryException("The native Codex output is saved. Retry staging without generating again. " + e.Message, AiJobRecovery.RetryOutput, e); }
            }
            if (staged.CandidateId != candidate.Id || staged.Metadata.Codex?.Model != request.Codex!.Model || staged.Metadata.AiJobId != context.Job.Id)
                throw new AiGenerationException("The saved Codex output does not match this candidate.");
            if (request.Regional is not null)
            {
                completed.Add(new(candidate.Id, candidate.Number, candidate.Id, staged.Metadata));
                await SaveResultAsync(context, completed, token);
                await context.MarkReviewableAsync(token);
                continue;
            }
            try
            {
                await context.ReportAsync(new(new(GenerationPhase.Saving, "Saving Codex image to Assets…"), candidate.Number, total, CodexTiming: staged.Metadata.Codex?.Timing), true);
                using var content = new MemoryStream(staged.Bytes, writable: false);
                await assets.PublishGeneratedImageAsync(request.ProjectId, new(context.Job.Id, candidate.Id, request.AssetId,
                    new(staged.FileName, request.Tags, request.Edit is null ? AssetImageOrigin.Generated : AssetImageOrigin.Edited, staged.Metadata, request.Look?.LookId,
                        (request.Create?.PendingReview ?? request.Edit?.PendingReview) == true ? true : null)), content, token);
                completed.Add(new(candidate.Id, candidate.Number, candidate.Id, staged.Metadata));
                await SaveResultAsync(context, completed, token); await context.MarkReviewableAsync(token);
            }
            catch (Exception e) when (e is IOException or lumibelle.Services.Story.WorkspaceStoreException)
            { throw new AiJobRecoveryException("The Codex image is staged. Retry saving without generating again. " + e.Message, AiJobRecovery.RetryOutput, e); }
        }
    }
}
