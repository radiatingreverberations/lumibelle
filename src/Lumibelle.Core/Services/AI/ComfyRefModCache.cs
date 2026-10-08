using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;

namespace lumibelle.Services.AI;

// A cache build is a journaled operation of the video job that already holds the
// ComfyUI provider slot. Never enqueue another job and wait for the same slot.
public sealed class ComfyRefModCache(ReelRefModStore store, ComfyRefModClient remote, ComfyJobExecution comfy)
{
    public const string Prefix = "refmod-cache/v1/";
    public sealed record Plan(Guid ProjectId, string Scope, ReelRefModReference Reference);

    public static string Operation(Guid job, string scope, string server, string key) => Prefix +
        ReelRefMods.Digest(new { Job = job, Scope = scope, Server = AiProviderRegistry.NormalizeComfyUrl(server), Key = key });

    public static Plan BuildPlan(Guid job, Guid project, string scope, string server, ReelRefModRecipe recipe)
    {
        ReelRefMods.Validate(recipe);
        if (job == Guid.Empty || project == Guid.Empty || string.IsNullOrWhiteSpace(scope) || scope.Length > 200)
            throw new WorkspaceStoreException("Reference-cache preparation needs an owned video operation.");
        server = AiProviderRegistry.NormalizeComfyUrl(server);
        var operation = Operation(job, scope, server, recipe.Key);
        // Stable within this job/operation; an explicit new generation gets a new
        // filename. A saved/uncertain submission is always observed, never replaced.
        var build = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(operation)).AsSpan(0, 16));
        return new(project, scope, new(recipe, server, ReelRefMods.BuildStem(project, build), build));
    }

    public async Task<IReadOnlyDictionary<Guid, ReelRefModReference>> EnsureAsync(AiJobContext context,
        VideoSnapshot snapshot, string scope, CancellationToken ct)
    {
        if (context.Recovering)
            throw new WorkspaceStoreException("Output recovery cannot rebuild reference caches or submit video inference.");
        await context.CurrentAsync(ct);
        if (context.Job.Target.ProjectId != snapshot.ProjectId || context.Job.Kind is not (AiJobKind.Video or AiJobKind.ReelVideo))
            throw new WorkspaceStoreException("Reference-cache preparation must belong to the current video job.");
        var server = AiProviderRegistry.NormalizeComfyUrl(snapshot.ExecutionComfyUrl);
        var bindings = snapshot.Shot.Videos.Where(v => v.EffectiveVisuals == ReelVisuals.RefMod).ToArray();
        var sources = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal);
        // Each cache uses this batch's video VAE: a reference prepared with another VAE gets a cache of
        // its same accepted images encoded with this one.
        var recipes = bindings.ToDictionary(b => b.Id, b => ReelRefMods.ForVae(b.RefMod!.Recipe, snapshot.Settings.VideoVae));
        // Validate ALL source inputs before any remote build. Never re-extract a
        // later reel, recrop it, or generate a new appearance to repair this cache.
        foreach (var binding in bindings)
        {
            ReelRefMods.ValidateBinding(binding, true);
            var source = binding.RefMod!;
            if (sources.ContainsKey(recipes[binding.Id].Key)) continue;
            var frames = new List<byte[]>();
            for (var i = 0; i < source.Recipe.LatentFrames; i++)
            {
                var bytes = await store.PreviewAsync(snapshot.ProjectId, source, i, ct);
                var info = Image.Identify(bytes);
                if (info.Width != source.Recipe.Width || info.Height != source.Recipe.Height || bytes.Length > 16 * 1024 * 1024)
                    throw new WorkspaceStoreException("The accepted reference image does not match its captured canvas. Restore the original source images.");
                frames.Add(bytes);
            }
            sources.Add(recipes[binding.Id].Key, frames);
        }
        if (bindings.Length == 0) return new Dictionary<Guid, ReelRefModReference>();
        await remote.CheckAsync(server, snapshot.Settings.VideoVae, ct);
        var result = new Dictionary<Guid, ReelRefModReference>();
        var materialized = new Dictionary<string, ReelRefModReference>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var source = binding.RefMod!; var recipe = recipes[binding.Id];
            if (!materialized.ContainsKey(recipe.Key))
            {
                var plan = BuildPlan(context.Job.Id, snapshot.ProjectId, scope, server, recipe);
                var operation = Operation(context.Job.Id, scope, server, recipe.Key);
                var submitted = (await context.ExecutionAsync(ct)).Submissions.Any(s => s.Operation == operation);
                ReelRefModReference? reference = null;
                if (!submitted)
                {
                    var cached = await store.FindAsync(snapshot.ProjectId, recipe.Key, server, ct);
                    // The captured filename is only a hint. Do not contact its old
                    // server when generation is executing somewhere else, or use it with another VAE.
                    if (cached is null && source.ComfyUrl == server && source.Recipe.Key == recipe.Key) cached = source;
                    if (cached is not null && await remote.ReferenceAvailableAsync(cached, ct)) reference = cached;
                }
                if (reference is null)
                {
                    await context.ReportAsync(new(new(GenerationPhase.Preparing, $"Preparing reference cache for {binding.Name}…")), true);
                    using var http = remote.Client(server);
                    var uploaded = new List<string>();
                    if (!submitted)
                    {
                        await context.SaveOperationAsync(operation + "/source", AiOperationArtifact.Request, plan, ct);
                        var frames = sources[recipe.Key];
                        for (var i = 0; i < frames.Count; i++)
                            uploaded.Add(await ComfyRefModClient.UploadPictureAsync(http, plan.Reference.BuildId, i, frames[i], ct));
                    }
                    else await CheckPlanAsync(context, snapshot, operation, ct);
                    JsonElement? output = null;
                    var updates = submitted ? comfy.ObserveAsync(context, operation, http, ct: ct)
                        : comfy.ExecuteAsync(context, operation, http,
                            client => ComfyRefModClient.BuildWorkflow(plan.ProjectId, plan.Reference, uploaded, client),
                            ComfyRefModClient.BuildOptions, ct);
                    await foreach (var update in updates.WithCancellation(ct))
                    {
                        await context.ReportAsync(new(update.Progress));
                        if (update.Complete && update.Job is { } job) output = job;
                    }
                    if (output is null) throw new AiJobRecoveryException("Check the accepted reference-cache build before generating again.", AiJobRecovery.CheckStatus);
                    reference = ComfyRefModClient.ReadOutput(output.Value, plan.Reference);
                    if (!await remote.ReferenceAvailableAsync(reference, ct))
                        throw new AiJobRecoveryException("The completed reference cache is missing or incompatible. Start a new generation to rebuild it from the accepted images; this submitted operation will not be repeated.", AiJobRecovery.GenerateAgain);
                    await RememberAsync(context, snapshot.ProjectId, reference, ct);
                }
                ReelRefMods.ValidateServer(reference, server, snapshot.Settings.VideoVae);
                materialized.Add(recipe.Key, reference);
            }
            result.Add(binding.Id, materialized[recipe.Key]);
        }
        return result;
    }

    // Called only when recovering a job that has not submitted its next video
    // operation. Completed-video retrieval bypasses this path entirely. Even here
    // we only observe saved builds and save hints; we never read source images,
    // require that the cache still exists, upload, or call /prompt.
    public async Task<bool> RecoverAsync(AiJobContext context, VideoSnapshot snapshot, CancellationToken ct)
    {
        var operations = (await context.ExecutionAsync(ct)).Submissions
            .Where(s => s.Operation.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        foreach (var submission in operations)
        {
            var plan = await CheckPlanAsync(context, snapshot, submission.Operation, ct);
            if (submission.ServerUrl != plan.Reference.ComfyUrl)
                throw new WorkspaceStoreException("The reference-cache operation belongs to another server.");
            using var http = remote.Client(submission.ServerUrl);
            JsonElement? output = null;
            await foreach (var update in comfy.ObserveAsync(context, submission.Operation, http, ct: ct))
            {
                await context.ReportAsync(new(update.Progress));
                if (update.Complete && update.Job is { } job) output = job;
            }
            if (output is null) throw new AiJobRecoveryException("The accepted reference-cache build is still unconfirmed.", AiJobRecovery.CheckStatus);
            var reference = ComfyRefModClient.ReadOutput(output.Value, plan.Reference);
            await RememberAsync(context, snapshot.ProjectId, reference, ct);
        }
        return operations.Length > 0;
    }

    private static async Task<Plan> CheckPlanAsync(AiJobContext context, VideoSnapshot snapshot, string operation, CancellationToken ct)
    {
        var plan = await context.ReadOperationAsync<Plan>(operation + "/source", AiOperationArtifact.Request, ct)
            ?? throw new WorkspaceStoreException("The accepted cache build has no saved source recipe. Check its remote status; it will not be resubmitted.");
        ReelRefMods.Validate(plan.Reference);
        var expected = BuildPlan(context.Job.Id, snapshot.ProjectId, plan.Scope, snapshot.ExecutionComfyUrl, plan.Reference.Recipe);
        if (plan.ProjectId != snapshot.ProjectId || plan.Reference.BuildId != expected.Reference.BuildId ||
            plan.Reference.ComfyUrl != expected.Reference.ComfyUrl || plan.Reference.FileName != expected.Reference.FileName ||
            Operation(context.Job.Id, plan.Scope, plan.Reference.ComfyUrl, plan.Reference.Recipe.Key) != operation ||
            !snapshot.Shot.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.RefMod && v.RefMod is { } refMod &&
                ReelRefMods.ForVae(refMod.Recipe, snapshot.Settings.VideoVae).Key == plan.Reference.Recipe.Key))
            throw new WorkspaceStoreException("The reference-cache build does not match this captured video request.");
        ReelRefMods.ValidateVae(plan.Reference, snapshot.Settings.VideoVae);
        return plan;
    }

    private async Task RememberAsync(AiJobContext context, Guid project, ReelRefModReference reference, CancellationToken ct)
    {
        await context.CurrentAsync(ct);
        try { await store.RememberAsync(project, reference, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkspaceStoreException)
        { throw new AiJobRecoveryException("The reference cache was built, but its reuse receipt could not be saved. Retry output to recover the receipt without encoding again.", AiJobRecovery.RetryOutput, e); }
    }
}
