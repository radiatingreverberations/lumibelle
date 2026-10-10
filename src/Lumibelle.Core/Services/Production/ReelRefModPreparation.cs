using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Production;

// Accepts exact local build inputs. No HTTP client, queue, VAE or GPU dependency:
// only ComfyRefModCache, inside an owned video job, may materialize the cache.
public sealed class ReelRefModPreparation(IAssetStore assets, IReferenceVideoStore media,
    ReelRefModStore store, IAiSettingsStore settings)
{
    public async Task<Shot> CaptureAsync(Guid project, Shot selection, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (project == Guid.Empty) throw new WorkspaceStoreException("Choose a project for these reference frames.");
        var captured = selection.Copy();
        ReferenceVideos.Validate(captured);
        var pending = captured.Videos.Where(v => v.EffectiveVisuals == ReelVisuals.RefMod && !ReelRefMods.Matches(v, v.RefMod)).ToArray();
        if (pending.Length == 0) return captured;
        var library = await assets.LoadAsync(project, ct);
        if (library.ProjectId != project) throw new WorkspaceStoreException("The reference library belongs to another project.");
        var configured = ShotCopy.Of(await settings.LoadAsync(ct));
        var server = AiProviderRegistry.NormalizeComfyUrl(configured.ComfyUrl);
        var accepted = new Dictionary<string, ReelRefModReference>(StringComparer.Ordinal);
        foreach (var binding in pending)
        {
            ct.ThrowIfCancellationRequested();
            var reel = library.Reels.FirstOrDefault(r => r.Media == binding.Media)
                ?? throw new WorkspaceStoreException("Restore or select an available asset reel before accepting its RefMod frames.");
            if (binding.OwnerAssetId is { } owner && owner != reel.AssetId) throw new WorkspaceConflictException();
            // A changed frame/crop selection is a new explicit authoring choice.
            // Retain a formerly chosen canvas, but never silently repair a damaged recipe.
            if (binding.RefMod is { } prior) ReelRefMods.Validate(prior);
            var width = binding.RefMod?.Recipe.Width ?? 640;
            var height = binding.RefMod?.Recipe.Height ?? 640;
            var pixels = await PreparePixelsAsync(media, project, binding, configured.H3, width, height, ct);
            var recipe = ReelRefMods.Recipe(binding, width, height, configured.H3.VideoVae,
                pixels.Select(p => Convert.ToHexString(SHA256.HashData(p))).ToArray());
            await store.AcceptSourcesAsync(project, recipe, pixels, ct);
            if (!accepted.TryGetValue(recipe.Key, out var reference))
            {
                reference = await store.FindAsync(project, recipe.Key, server, ct)
                    ?? UnbuiltReference(project, recipe, server);
                accepted.Add(recipe.Key, reference);
            }
            // No completed-build receipt is manufactured. The reference contains the
            // accepted recipe and an advisory address; EnsureAsync checks/builds it.
            binding.RefMod = ShotCopy.Of(reference);
        }
        ReferenceVideos.Validate(captured, true);
        ct.ThrowIfCancellationRequested();
        return captured;
    }

    internal static ReelRefModReference UnbuiltReference(Guid project, ReelRefModRecipe recipe, string server)
    {
        // Stable local identity, not a submitted job. Cache builds use their own
        // journaled operation identity and never write a result for this address.
        var digest = ReelRefMods.Digest(new { Purpose = "refmod-source-v1", Project = project, recipe.Key, Server = server });
        var id = new Guid(Convert.FromHexString(digest).AsSpan(0, 16));
        var reference = new ReelRefModReference(recipe, server, ReelRefMods.BuildStem(project, id), id);
        ReelRefMods.Validate(reference);
        return reference;
    }

    internal static async Task<IReadOnlyList<byte[]>> PreparePixelsAsync(IReferenceVideoStore media, Guid project,
        ShotVideoBinding binding, H3Settings settings, int width, int height, CancellationToken ct)
    {
        if (!ReelRefMods.Canvases.Contains((width, height))) throw new WorkspaceStoreException("Choose a supported RefMod canvas.");
        _ = ReelRefMods.Selection(binding);
        await media.ValidateAsync(project, [binding], ct);
        await media.PrepareFramesAsync(project, binding.Keyframes!.Frames.Select(f => f.Frame), settings, ct);
        var result = new List<byte[]>();
        foreach (var frame in binding.Keyframes.Frames)
        {
            ct.ThrowIfCancellationRequested();
            await using var content = await media.OpenFrameAsync(project, frame.Frame, settings, ct);
            var cropped = await ComfyReferenceImageEditor.PrepareSourcePngAsync(content.Content, frame.Crop, ct);
            using var image = Image.Load(cropped);
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new(width, height), Mode = ResizeMode.Pad,
                PadColor = Color.FromRgb(240, 240, 240), Sampler = KnownResamplers.Lanczos3 }));
            using var output = new MemoryStream(); await image.SaveAsync(output, new PngEncoder(), ct);
            result.Add(output.ToArray());
        }
        // Do not accept pixels extracted while the underlying media was replaced.
        await media.ValidateAsync(project, [binding], ct);
        return result;
    }
}
