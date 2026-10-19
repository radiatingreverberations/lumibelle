using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

// Manual browsing uses metadata only. Visual evidence hashes are captured by drafting/Compose.
public static class EnvironmentPictures
{
    public static IReadOnlyList<EnvironmentViewCandidate> Candidates(AssetLibrary library, Guid assetId)
    {
        var owner = library.Assets.FirstOrDefault(a => a.Id == assetId && a.Category == AssetCategory.Environment);
        if (owner is null) return [];
        var images = owner.Images.OrderByDescending(i => i.IsReference).ThenBy(i => i.Id).Select(i =>
            new EnvironmentViewCandidate("image/" + i.Id, i.Name ?? owner.Name, i.IsReference, i.PreservationGuidance,
                new() { AssetId = owner.Id, MediaId = i.Id, Name = i.Name ?? owner.Name, Notes = i.PreservationGuidance,
                    LookId = i.LookId, Role = "Environment", InferUsage = true }, null, null, ""));
        var frames = library.Reels.Where(r => r.AssetId == assetId).OrderBy(r => r.Id).SelectMany(r =>
            (r.Keyframes?.Frames ?? []).Select((f, index) => new EnvironmentViewCandidate($"reel/{r.Id}/{f.Id}",
                ReelViewSuggestions.Label(f, index + 1), false, f.Notes, null, r, f, "")));
        return images.Concat(frames).ToArray();
    }

    public static bool Contains(Shot shot, EnvironmentViewCandidate view) => view.Image is { } image
        ? shot.Images.Any(i => i.AssetId == image.AssetId && i.MediaId == image.MediaId && i.Crop == image.Crop)
        : shot.Videos.Any(v => v.Media.Id == view.Reel!.Media.Id && v.EffectiveVisuals == ReelVisuals.Keyframes &&
            v.Keyframes?.Frames.Any(f => f.Frame.Index == view.Keyframe!.Frame.Index && f.Crop == view.Keyframe.Crop) == true);

    public static void Select(Shot shot, EnvironmentViewCandidate view, bool selected)
    {
        if (selected && Contains(shot, view)) return;
        if (selected && ResolvedReferences.For(shot).Pictures.Count >= 9)
            throw new WorkspaceStoreException("Up to 9 pictures. Remove a picture before adding another view.");
        if (view.Image is { } image) {
            if (selected) {
                if (shot.Images.Any(i => i.AssetId == image.AssetId && i.MediaId == image.MediaId)) throw new WorkspaceStoreException("This image is already attached with another crop. Edit its selected reference to change the crop.");
                var copy = ShotCopy.Of(image); copy.Id = Guid.NewGuid(); shot.Images.Add(copy); }
            else shot.Images.RemoveAll(i => i.AssetId == image.AssetId && i.MediaId == image.MediaId && i.Crop == image.Crop);
            return;
        }
        var reel = view.Reel!;
        var binding = shot.Videos.FirstOrDefault(v => v.Media.Id == reel.Media.Id);
        if (binding is not null && binding.EffectiveVisuals != ReelVisuals.Keyframes)
            throw new WorkspaceStoreException("This reel is already attached as video or RefMod. Change its visuals in selected references before adding individual pictures.");
        if (!selected) {
            binding?.Keyframes?.Frames.RemoveAll(f => f.Frame.Index == view.Keyframe!.Frame.Index && f.Crop == view.Keyframe.Crop);
            if (binding?.Keyframes?.Frames.Count == 0) shot.Videos.Remove(binding);
            return;
        }
        if (binding is null) {
            binding = new() { Media = ShotCopy.Of(reel.Media), Name = reel.Name, Description = reel.UseGuidance,
                OwnerAssetId = reel.AssetId, OwnerCategory = AssetCategory.Environment, UseSoundtrack = false,
                Visuals = ReelVisuals.Keyframes, Keyframes = new() };
            shot.Videos.Add(binding);
        }
        if (binding.Keyframes?.Frames.Any(f => f.Frame.Index == view.Keyframe!.Frame.Index) == true)
            throw new WorkspaceStoreException("This frame is already attached with another crop. Edit its selected reference to change the crop.");
        binding.Keyframes ??= new();
        binding.Keyframes.Frames.Add(ShotCopy.Of(view.Keyframe!));
    }
}
