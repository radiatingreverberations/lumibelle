using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class ReelRefMods
{
    public const string Protocol = "fantastic-refmod-trial-v1";
    public const int MaximumTotalTokens = 49152;
    public static IReadOnlyList<(int Width, int Height)> Canvases { get; } = Array.AsReadOnly(new[] { (640, 640), (480, 832), (832, 480) });
    public static bool Uses(Shot shot) => shot.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.RefMod);
    public static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options)));
    public static string Selection(ShotVideoBinding reel)
    {
        ReferenceVideos.ValidateMedia(reel.Media);
        if (reel.Keyframes?.Frames is not { Count: >= 2 and <= 9 })
            throw new WorkspaceStoreException("A selected-keyframe RefMod needs two to nine distinct frames. Use a normal picture for one frame.");
        ReferenceVideos.ValidateKeyframes(reel.Media, reel.Keyframes);
        // Guidance and binding IDs do not affect pixels. Their current values still enter prompts.
        return Digest(new { reel.Media, Frames = reel.Keyframes.Frames.Select(f => new { f.Frame, f.Crop }) });
    }
    public static ReelRefModRecipe Recipe(ShotVideoBinding reel, int width, int height,
        string vaeName, IReadOnlyList<string> frameHashes)
    {
        var selection = Selection(reel);
        var key = Digest(new { Protocol, Selection = selection, Width = width, Height = height, VaeName = vaeName, FrameHashes = frameHashes });
        var recipe = new ReelRefModRecipe(Protocol, key, selection, width, height, vaeName, frameHashes.ToArray());
        Validate(recipe); return recipe;
    }
    public static void Validate(ReelRefModRecipe r)
    {
        if (r is null || r.Protocol != Protocol || !Hash(r.Key) || !Hash(r.Selection) ||
            !Canvases.Contains((r.Width, r.Height)) || string.IsNullOrWhiteSpace(r.VaeName) || r.VaeName.Length > 2048 ||
            r.FrameHashes is not { Count: >= 2 and <= 9 } || r.FrameHashes.Any(h => !Hash(h)))
            throw new WorkspaceStoreException("The saved RefMod recipe is invalid or belongs to another experiment. Prepare a new build explicitly.");
        if (r.Key != Digest(new { Protocol, r.Selection, r.Width, r.Height, r.VaeName, r.FrameHashes }))
            throw new WorkspaceStoreException("The RefMod source-preview recipe changed. Prepare a new build.");
    }
    public static bool RelativeStem(string? path) => path is { Length: > 0 and <= 500 } &&
        path.Split('/').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
    public static string BuildStem(Guid project, Guid job) => $"lumibelle/{project:N}/{job:N}";
    public static void Validate(ReelRefModReference reference)
    {
        if (reference is null) throw new WorkspaceStoreException("Build and use a RefMod before generating with this reference.");
        Validate(reference.Recipe);
        if (reference.BuildId == Guid.Empty || !RelativeStem(reference.FileName) ||
            !reference.FileName.EndsWith("/" + reference.BuildId.ToString("N"), StringComparison.Ordinal) ||
            AiProviderRegistry.NormalizeComfyUrl(reference.ComfyUrl) != reference.ComfyUrl)
            throw new WorkspaceStoreException("The saved RefMod needs its original ComfyUI server and relative filename.");
    }
    public static void ValidateVae(ReelRefModReference reference, string vaeName)
    {
        Validate(reference);
        if (reference.Recipe.VaeName != vaeName)
            throw new WorkspaceStoreException($"This RefMod reference was prepared with the H3 video VAE {reference.Recipe.VaeName}, but this batch uses {vaeName}. " +
                $"Select {reference.Recipe.VaeName} in AI settings → Video models and generate a new batch, or choose Prepare a new build on the reference to use {vaeName}.");
    }
    public static void ValidateServer(ReelRefModReference reference, string server, string vaeName)
    {
        ValidateVae(reference, vaeName);
        if (reference.ComfyUrl != AiProviderRegistry.NormalizeComfyUrl(server))
            throw new WorkspaceStoreException("The reference cache has not been prepared on the execution server. Generate through the video queue to rebuild it from the accepted images.");
    }
    // Resolved handles belong to this workflow only. Never replace the accepted
    // binding/recipe or mutate a saved snapshot just because a cache was rebuilt.
    public static IReadOnlyDictionary<Guid, ReelRefModReference> ExecutionReferences(VideoSnapshot snapshot,
        IReadOnlyDictionary<Guid, ReelRefModReference>? prepared = null)
    {
        var bindings = snapshot.Shot.Videos.Where(v => v.EffectiveVisuals == ReelVisuals.RefMod).ToArray();
        if (prepared is not null && (prepared.Count != bindings.Length || bindings.Any(b => !prepared.ContainsKey(b.Id))))
            throw new WorkspaceStoreException("Prepared reference caches do not match the selected references.");
        var result = new Dictionary<Guid, ReelRefModReference>();
        foreach (var binding in bindings)
        {
            ValidateBinding(binding, true);
            var reference = prepared is null ? binding.RefMod! : prepared[binding.Id];
            Validate(reference);
            if (reference.Recipe.Key != binding.RefMod!.Recipe.Key)
                throw new WorkspaceStoreException("A reference cache belongs to another accepted frame selection or recipe.");
            ValidateServer(reference, snapshot.ExecutionComfyUrl, snapshot.Settings.VideoVae);
            result.Add(binding.Id, reference);
        }
        return result;
    }
    public static bool Matches(ShotVideoBinding reel, ReelRefModReference? reference)
    {
        if (reference is null) return false;
        try { Validate(reference); return reference.Recipe.Selection == Selection(reel); }
        catch (WorkspaceStoreException) { return false; }
    }
    // Explicit UI attachment accepts a new logical reference, not a new cache
    // location. Rebuilding the same recipe must not invalidate prompt review.
    public static bool Attach(ShotVideoBinding binding, ReelRefModReference built)
    {
        if (!Matches(binding, built))
            throw new WorkspaceStoreException("The built reference does not match the current accepted frame selection.");
        if (Matches(binding, binding.RefMod) && binding.RefMod!.Recipe.Key == built.Recipe.Key) return false;
        binding.RefMod = ShotCopy.Of(built);
        return true;
    }
    public static void ValidateBinding(ShotVideoBinding reel, bool ready)
    {
        _ = Selection(reel);
        if (ready && !Matches(reel, reel.RefMod))
            throw new WorkspaceStoreException($"Apply the reference changes for {reel.Name} to capture its current frames and crops. Its RefMod will build automatically when generating. No full-reel fallback will be used.");
    }
    public const string UseGuidance = "Selected still views of the same subject, packed into one visual RefMod. Use only the indicated appearance or setting; do not reproduce the sequence as motion, cuts, actions or camera travel. Voice is a separate Audio reference.";
    public static string Summary(ShotVideoBinding reel) => Matches(reel, reel.RefMod)
        ? $"{reel.RefMod!.Recipe.LatentFrames} selected frames · {reel.RefMod.Recipe.Width} × {reel.RefMod.Recipe.Height} · {reel.RefMod.Recipe.Tokens:N0} visual reference tokens · 0 picture slots · 1 video reference · Built automatically when needed"
        : "Selected frames will be captured on Apply; RefMod builds when generating · 0 picture slots · 1 video reference";
}
