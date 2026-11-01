using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3GenerationRecipes
{
    public const string Version = "h3-generation-recipe-v1";
    public const string ProbeNode = "LumibelleH3RecipeProbe", ReceiptNode = "LumibelleH3ExecutionReceipt";
    public const string ProbeId = "h3-recipe-probe", ReceiptId = "h3-execution-receipt";

    public static async Task<AiVideoJobRequest> StampAsync(AiVideoJobRequest request, IVideoGenerator generator, CancellationToken ct)
    {
        request = request with { Recipe = null, Snapshot = H3PikaFlexRecipe.Effective(H3PtdRecipe.Effective(H3SmdRecipe.Effective(H3AcceleratedCheckpoints.Effective(request.Snapshot)))) };
        var recipe = await generator.CaptureRecipeAsync(request, ct);
        H3AcceleratedCheckpoints.ValidateIdentity(request.Snapshot, recipe);
        H3SmdRecipe.ValidateIdentity(request.Snapshot, recipe, request.Refinement is null);
        H3PikaFlexRecipe.ValidateIdentity(request.Snapshot, recipe, request.Refinement is null);
        H3PtdRecipe.ValidateIdentity(request.Snapshot, recipe, request.Refinement is null);
        return request with { Recipe = recipe };
    }

    // The request already owns snapshots and prepared media. Include the effective refinement
    // override rather than mislabelling the source preview's sampling as the refinement recipe.
    public static JsonElement Parameters(AiVideoJobRequest request) => JsonSerializer.SerializeToElement(new {
        request.Version, request.BatchId, request.DestinationShotId, request.Snapshot, request.Inputs, request.OutputTrim,
        request.Refinement,
        EffectiveSampling = request.Refinement is { } r ? new H3Sampling("h3-refinement", r.Steps, "res_multistep", "simple", 12, 3, null, 0) : request.Snapshot.Sampling,
        EffectiveWidth = request.Refinement?.Width ?? request.Snapshot.Width,
        EffectiveHeight = request.Refinement?.Height ?? request.Snapshot.Height
    }, AtomicJsonFile.Options);
    public static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options)));
    public static void CheckNativeRequest(H3GenerationRecipe? recipe)
    {
        if (recipe?.ProbeAvailable == true)
            throw new WorkspaceStoreException("This historical request used the retired recorder nodes. Regenerate or capture a new request to use the native workflow.");
    }
    private static string Fingerprint(JsonElement parameters, IReadOnlyList<H3ModelIdentity> models, H3RuntimeIdentity runtime, bool probeAvailable)
        => Hash(JsonSerializer.SerializeToElement(new { Version, Parameters = parameters, Models = models, Runtime = runtime, ProbeAvailable = probeAvailable }, AtomicJsonFile.Options));

    public static H3GenerationRecipe Capture(AiVideoJobRequest request, H3RecipeEvidence? evidence = null)
    {
        var parameters = Parameters(request);
        var models = evidence?.Models ?? Models(request);
        var runtime = evidence?.Runtime ?? new(new Dictionary<string, string?> { ["runtime"] = null }, new Dictionary<string, string?>());
        return new(Version, Fingerprint(parameters, models, runtime, evidence is not null), parameters, models, runtime, evidence is not null);
    }

    public static IReadOnlyList<H3ModelIdentity> Models(AiVideoJobRequest request)
    {
        var s = request.Snapshot;
        List<H3ModelIdentity> models = [new("diffusion_models", s.Settings.Model), new("text_encoders", s.Settings.Encoder),
            new("vae", s.Settings.VideoVae), new("vae", s.Settings.AudioVae)];
        if (request.Refinement is null && (s.Preset?.Checkpoint ?? s.Sampling?.Lora) is { } checkpoint)
            models.Add(new(s.Preset?.Key == "pdd" ? "pdd_acc" : s.Preset?.Key == H3PtdRecipe.Key ? "ptd" : "loras", checkpoint));
        foreach (var lora in s.AppliedLoras ?? []) models.Add(new("loras", lora.Reference.FileName));
        if (request.Refinement is { } refine) models.Add(new("latent_upscale_models", refine.Upscaler));
        else if (s.PreviewUpscale is { } upscale) models.Add(new("latent_upscale_models", upscale.Checkpoint));
        return models.DistinctBy(m => (m.Category, m.FileName)).ToArray();
    }

    public static void Validate(AiVideoJobRequest request)
    {
        if (request.Recipe is not { } recipe) return; // Historical records have no recipe envelope.
        H3AcceleratedCheckpoints.ValidateIdentity(request.Snapshot, recipe);
        H3SmdRecipe.ValidateIdentity(request.Snapshot, recipe, request.Refinement is null);
        H3PikaFlexRecipe.ValidateIdentity(request.Snapshot, recipe, request.Refinement is null);
        H3PtdRecipe.ValidateIdentity(request.Snapshot, recipe, request.Refinement is null);
        if (recipe.Version != Version || Hash(Parameters(request)) != Hash(recipe.Parameters) ||
            recipe.Fingerprint != Fingerprint(recipe.Parameters, recipe.Models, recipe.Runtime, recipe.ProbeAvailable) ||
            !Models(request).Select(m => (m.Category, m.FileName)).SequenceEqual(recipe.Models.Select(m => (m.Category, m.FileName))) ||
            recipe.ProbeAvailable && recipe.Models.Any(m => m.Sha256?.Length != 64 || m.Sha256.Any(c => !Uri.IsHexDigit(c)) || m.Bytes is null or <= 0))
            throw new WorkspaceStoreException("The captured H3 generation recipe no longer matches its inputs. Capture a new request.");
    }

    public static void CheckEvidence(H3GenerationRecipe recipe, H3RecipeEvidence actual)
    {
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new H3RecipeEvidence(recipe.Models, recipe.Runtime), AtomicJsonFile.Options),
            JsonSerializer.SerializeToElement(actual, AtomicJsonFile.Options)))
            throw new WorkspaceStoreException("H3 model contents or runtime implementations changed after capture. Capture a new request to use the changed recipe.");
    }

    public static H3ExecutionReceipt ReadReceipt(JsonElement outputs, H3GenerationRecipe recipe, long seed, string attempt)
    {
        try { return ParseReceipt(outputs, recipe, seed, attempt); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException or JsonException) {
            throw new WorkspaceStoreException("The completed H3 output has no readable matching execution receipt. Retry its transfer without regenerating.", e);
        }
    }

    public static H3ExecutionReceipt BindReceipt(JsonElement outputs, H3GenerationRecipe recipe, long seed,
        JsonElement submittedWorkflow, string probeNode, string? promptId, string clientId)
    {
        // Explicit retries preserve the graph bytes, including its probe token, while
        // the transport owns a fresh client/prompt ID. Bind both identities separately.
        try {
            var probeAttempt = submittedWorkflow.GetProperty("prompt").GetProperty(probeNode)
                .GetProperty("inputs").GetProperty("attempt_id").GetString()
                ?? throw new WorkspaceStoreException("The submitted H3 probe identity is missing.");
            return ReadReceipt(outputs, recipe, seed, probeAttempt) with { ComfyPromptId = promptId, ComfyClientId = clientId };
        } catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) {
            throw new WorkspaceStoreException("The submitted H3 graph has no matching probe. Retry retrieval without regenerating.", e);
        }
    }

    private static H3ExecutionReceipt ParseReceipt(JsonElement outputs, H3GenerationRecipe recipe, long seed, string attempt)
    {
        var observation = outputs.GetProperty(ReceiptId).GetProperty("lumibelle_h3_receipt")[0];
        if (observation.GetProperty("recipeFingerprint").GetString() != recipe.Fingerprint || observation.GetProperty("seed").GetInt64() != seed ||
            observation.GetProperty("attemptId").GetString() != attempt || observation.GetProperty("status").GetString() != "sampling-complete")
            throw new WorkspaceStoreException("The H3 execution receipt belongs to another generation.");
        var evidence = observation.GetProperty("evidence").Deserialize<H3RecipeEvidence>(AtomicJsonFile.Options)
            ?? throw new WorkspaceStoreException("H3 execution evidence is missing.");
        CheckEvidence(recipe, evidence);
        if (observation.TryGetProperty("postSamplingEvidence", out var after))
            CheckEvidence(recipe, after.Deserialize<H3RecipeEvidence>(AtomicJsonFile.Options)
                ?? throw new WorkspaceStoreException("Post-sampling H3 execution evidence is missing."));
        var sigmas = observation.GetProperty("sigmas").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (sigmas.Length < 2 || sigmas.Any(v => !double.IsFinite(v))) throw new WorkspaceStoreException("The H3 sigma receipt is invalid.");
        return new("h3-execution-receipt-v1", recipe.Fingerprint, seed, attempt, "complete", sigmas, evidence, observation.Clone());
    }
}
