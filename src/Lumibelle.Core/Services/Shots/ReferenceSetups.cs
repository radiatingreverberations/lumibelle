using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class ReferenceSetups
{
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options)));
    public static string UseLabel(ShotImageUse use) => use switch { ShotImageUse.Environment => "Environment", ShotImageUse.ObjectAppearance => "Object appearance", ShotImageUse.Composition => "Composition", ShotImageUse.FirstFrame => "First frame", ShotImageUse.LastFrame => "Last frame", _ => "Continuity state" };
    public static bool IsAnchor(ShotImageBinding image) => image.Use is ShotImageUse.FirstFrame or ShotImageUse.LastFrame;
    public static ShotImageUse NonCharacterUse(ShotImageBinding image) => image.Use ?? image.Role.Trim().ToLowerInvariant() switch
    { "environment" => ShotImageUse.Environment, "composition" => ShotImageUse.Composition, "continuity state" => ShotImageUse.ContinuityState, _ => ShotImageUse.ObjectAppearance };
    public static bool IsContinuity(Guid assetId, Guid imageId, AssetLibrary library)
    {
        var visited = new HashSet<(Guid, Guid)>();
        while (visited.Add((assetId, imageId)))
        {
            var reference = new AssetImageReference(assetId, imageId);
            var image = AssetImageLocations.RecordedOwner(library, reference)?.Images.FirstOrDefault(i => i.Id == imageId)
                ?? library.Trash.FirstOrDefault(t => AssetImageLocations.Matches(t.Asset.Id, t.Image, reference))?.Image;
            if (image?.Source?.Frame is not null || image?.Origin == AssetImageOrigin.VideoFrame) return true;
            if (image?.Source?.Crop is not { } crop) return false;
            assetId = crop.AssetId; imageId = crop.ImageId;
        }
        return true; // Broken cyclic lineage must not become an automatic default.
    }
    public static string? InputIssue(ShotImageBinding b, AssetLibrary library)
    {
        var asset = library.Assets.FirstOrDefault(a => a.Id == b.AssetId);
        var image = asset?.Images.FirstOrDefault(i => i.Id == b.MediaId);
        if (b.Kind != ShotImageKind.AssetImage || image is null) return "Unavailable · restore or replace this image.";
        if (image.LookId != b.LookId) return "Image moved to another look · repair this saved selection.";
        if (LookPolicy.Find(asset!, b.LookId)?.Archived == true) return "Archived look · unarchive or replace this selection.";
        return null;
    }
    public static ShotImageBinding Bind(ReferenceAsset asset, AssetImage image, Shot shot)
    {
        var character = ShotReferences.Characters(shot).FirstOrDefault(c => c.Appearance?.AssetId == asset.Id)
            ?? ShotReferences.Characters(shot).FirstOrDefault(c => shot.Images.Any(b => b.AssetId == asset.Id && b.RepresentsId == c.Id));
        var purpose = character is null ? (ShotReferencePurpose?)null :
            character.Appearance?.EndLookId == image.LookId && image.LookId is not null ? ShotReferencePurpose.EndingLook :
            character.Appearance?.LookId == image.LookId && image.LookId is not null ? character.Appearance!.EndLookId is null ? ShotReferencePurpose.CurrentLook : ShotReferencePurpose.StartingLook : ShotReferencePurpose.Identity;
        var use = image.Source?.Frame is not null ? ShotImageUse.ContinuityState : asset.Category == AssetCategory.Environment ? ShotImageUse.Environment : ShotImageUse.ObjectAppearance;
        return new() { AssetId = asset.Id, MediaId = image.Id, Name = image.Name ?? asset.Name, LookId = image.LookId,
            RepresentsId = character?.Id, Purpose = purpose, Use = character is null ? use : null, Role = character is null ? UseLabel(use) : ShotLooks.Label(purpose!.Value) };
    }
    public static void ValidateSetup(SceneReferenceSetup setup, AssetLibrary library)
    {
        if (setup.SceneId == Guid.Empty || setup.Version == Guid.Empty || setup.Images is null || setup.SceneTitle is null || setup.Images.Count > 100 ||
            setup.Images.Any(i => i is null || i.Image is null) || setup.Images.Select(i => (i.Image.AssetId, i.Image.MediaId)).Distinct().Count() != setup.Images.Count)
            throw new WorkspaceStoreException("Choose a scene and distinct images for its reference setup.");
        if (setup.Environment is { } environment && (environment.AssetId == Guid.Empty || environment.Version == Guid.Empty ||
            !library.Assets.Any(a => a.Id == environment.AssetId && a.Category == AssetCategory.Environment) ||
            environment.Views.Select(v => v.Id).Distinct().Count() != environment.Views.Count))
            throw new WorkspaceStoreException("Choose an available environment and distinct inspection views.");
        foreach (var item in setup.Images)
        {
            var b = item.Image;
            if (InputIssue(b, library) is { } issue) throw new WorkspaceStoreException(issue);
            if (IsContinuity(b.AssetId, b.MediaId, library) || NonCharacterUse(b) == ShotImageUse.ContinuityState)
                throw new WorkspaceStoreException("Continuity references stay with individual shots.");
            var asset = library.Assets.First(a => a.Id == b.AssetId);
            if (asset.Category == AssetCategory.Character && (item.CharacterAssetId != asset.Id || b.Purpose is not (ShotReferencePurpose.Identity or ShotReferencePurpose.CurrentLook)) ||
                asset.Category != AssetCategory.Character && (item.CharacterAssetId is not null || b.Purpose is not null))
                throw new WorkspaceStoreException("Use character identity or the matching look for character scene references.");
            var validation = new Shot { Images = [ShotCopy.Of(b)] };
            validation.Images[0].RepresentsId = b.Purpose is null ? null : asset.Id;
            if (b.Purpose is not null) validation.Characters.Add(new(asset.Id, asset.Name));
            H3Policy.Validate(validation);
        }
    }
}
