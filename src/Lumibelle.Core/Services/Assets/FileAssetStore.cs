using lumibelle.Models;
using lumibelle.Services.Story;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore(ProjectFiles files, TimeProvider clock, lumibelle.Services.Shots.TakeFrameReader? frameReader = null,
    lumibelle.Services.Production.IReferenceVideoStore? referenceVideos = null, IAiSettingsStore? settings = null) : IAssetStore, IImageTrashStore
{
    public const long MaximumImageBytes = 25 * 1024 * 1024;

    public async Task<AssetLibrary> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        return await ReadAsync(directory, projectId, cancellationToken);
    }

    public async Task<AssetLibrary> SaveAsync(AssetLibrary library, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(library with { Voices = [], VoiceTrash = [], ExtractionReviews = [], VoiceImportReceipts = [], ImagePublications = [] });
        Validate(normalized with { Assets = normalized.Assets.Select(a => a with { DefaultVoiceId = null }).ToList() }, normalized.ProjectId);
        var directory = await files.DirectoryAsync(normalized.ProjectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, normalized.ProjectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        foreach (var asset in normalized.Assets)
            LookPolicy.ValidatePreferredChanges(current.Assets.FirstOrDefault(a => a.Id == asset.Id), asset, normalized);
        // Ordinary metadata saves may not add/remove images or remove whole assets.
        foreach (var asset in current.Assets)
        {
            var next = normalized.Assets.SingleOrDefault(a => a.Id == asset.Id);
            if (next is null || !asset.Images.Select(i => i.Id).ToHashSet().SetEquals(next.Images.Select(i => i.Id)))
                throw new WorkspaceStoreException("Use image or asset deletion to move images to Trash.");
            if (asset.Looks.Any(l => next.Looks.All(n => n.Id != l.Id))) throw new WorkspaceStoreException("Archive looks instead of deleting their identities.");
            foreach (var image in next.Images)
            {
                if (image.VisualDescription is { Length: > 12000 })
                    throw new WorkspaceStoreException("Keep each visual description under 12000 characters.");
                if (image.FileName != asset.Images.Single(i => i.Id == image.Id).FileName)
                    throw new WorkspaceStoreException("Stored image paths cannot be changed.");
                var original = asset.Images.Single(i => i.Id == image.Id);
                if (image.StorageAssetId != original.StorageAssetId || !image.PreviousAssetIds.SequenceEqual(original.PreviousAssetIds))
                    throw new WorkspaceStoreException("Use Move to asset to change an image's stored location.");
                if (image.Generation?.AiJobId != original.Generation?.AiJobId || image.Generation?.BatchId != original.Generation?.BatchId ||
                    image.Generation?.CandidateNumber != original.Generation?.CandidateNumber)
                    throw new WorkspaceStoreException("An image's captured batch identity cannot be changed.");
                if (image.Source != original.Source || image.Origin != original.Origin)
                    throw new WorkspaceStoreException("Image source provenance cannot be changed.");
                if (image.Generation?.Look != original.Generation?.Look || !(image.Generation?.Edit?.ReferenceLooks ?? []).SequenceEqual(original.Generation?.Edit?.ReferenceLooks ?? [])) throw new WorkspaceStoreException("Captured generation look context cannot be changed.");
                if (image.LookId != original.LookId && LookPolicy.Find(next, image.LookId)?.Archived == true)
                    throw new WorkspaceStoreException("Unarchive this look before assigning images to it.");
            }
        }
        if (normalized.Assets.Any(a => current.Assets.All(c => c.Id != a.Id) && a.Images.Count != 0))
            throw new WorkspaceStoreException("Import images using the image store.");
        return await PublishAsync(directory, normalized with { AssetReuseReceipts = current.AssetReuseReceipts, AssetMoveReceipts = current.AssetMoveReceipts, Reels = current.Reels, ReelDrafts = current.ReelDrafts, ReelTrash = current.ReelTrash, ReelPublications = current.ReelPublications, ImagePublications = current.ImagePublications, VoiceImportReceipts = current.VoiceImportReceipts, ExtractionReviews = current.ExtractionReviews, ImageCopyReceipts = current.ImageCopyReceipts, Trash = current.Trash, Voices = current.Voices, VoiceTrash = current.VoiceTrash }, current.Revision, cancellationToken);
    }

    public async Task<AssetLibrary> AddImageAsync(Guid projectId, Guid assetId, Stream content, AssetImageInput input,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        var asset = current.Assets.SingleOrDefault(item => item.Id == assetId)
            ?? throw new WorkspaceStoreException("That asset no longer exists.");
        if (input.LookId is { } lookId && LookPolicy.Find(asset, lookId) is null)
            throw new WorkspaceStoreException("The target look no longer exists.");
        if (input.Generation?.Look is { } context && (context.AssetId != assetId || context.LookId != input.LookId))
            throw new WorkspaceStoreException("The image destination does not match its captured look.");
        if (LookPolicy.Find(asset, input.LookId)?.Archived == true && input.Generation?.Look is null)
            throw new WorkspaceStoreException("Unarchive the look before importing an image.");

        await using var memory = new MemoryStream();
        try
        {
            await content.CopyToAsync(memory, cancellationToken);
            if (memory.Length is 0 or > MaximumImageBytes)
                throw new WorkspaceStoreException("Choose a PNG, JPEG, or WebP image no larger than 25 MB.");
            var info = ImageInspector.Inspect(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
            var imageId = Guid.NewGuid();
            var fileName = $"{imageId:N}{info.Extension}";
            var imageDirectory = Path.Combine(directory, "assets", assetId.ToString("D"), "images");
            var finalPath = Path.Combine(imageDirectory, fileName);
            var temporary = finalPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(imageDirectory);
                memory.Position = 0;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous))
                {
                    await memory.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                DurableFile.Flush(temporary);
                File.Move(temporary, finalPath);
                var image = new AssetImage
                {
                    Id = imageId,
                    LookId = input.LookId,
                    FileName = fileName,
                    ContentType = info.ContentType,
                    Width = info.Width, Height = info.Height,
                    Tags = NormalizeTags(input.Tags),
                    Origin = input.Origin,
                    Generation = input.Generation,
                    CreatedUtc = clock.GetUtcNow()
                };
                var next = current with { Assets = current.Assets.Select(item => item.Id == assetId
                    ? item with { Images = [.. item.Images, image], UpdatedUtc = clock.GetUtcNow() }
                    : item).ToList() };
                try { return await PublishAsync(directory, next, current.Revision, cancellationToken); }
                catch { TryDelete(finalPath); throw; }
            }
            finally { TryDelete(temporary); }
        }
        catch (IOException e)
        { throw new WorkspaceStoreException("Couldn’t store the image. Check folder permissions and free disk space.", e); }
        catch (UnauthorizedAccessException e)
        { throw new WorkspaceStoreException("Couldn’t store the image. Check folder permissions.", e); }
    }

    public Task<ImageTrashResult> DeleteImageAsync(Guid projectId, Guid assetId, Guid imageId, long expectedRevision,
        CancellationToken cancellationToken = default) => TrashImagesAsync(projectId, assetId, [imageId], expectedRevision, false, cancellationToken);

    public Task<ImageTrashResult> DeleteImagesAsync(Guid projectId, Guid assetId, IReadOnlyCollection<Guid> imageIds,
        long expectedRevision, CancellationToken cancellationToken = default) =>
        TrashImagesAsync(projectId, assetId, imageIds, expectedRevision, true, cancellationToken);

    private async Task<ImageTrashResult> TrashImagesAsync(Guid projectId, Guid assetId, IReadOnlyCollection<Guid> imageIds,
        long expectedRevision, bool takesOnly, CancellationToken ct)
    {
        var ids = DistinctIds(imageIds);
        var directory = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, projectId, ct);
        EnsureRevision(current, expectedRevision);
        var asset = current.Assets.SingleOrDefault(a => a.Id == assetId)
            ?? throw new WorkspaceStoreException("That asset no longer exists.");
        var images = asset.Images.Where(i => ids.Contains(i.Id)).ToArray();
        if (images.Length != ids.Count) throw new WorkspaceStoreException("A selected image is no longer available. Reload before retrying.");
        if (takesOnly && images.Any(i => i.IsReference || i.IsCover || i.Origin == AssetImageOrigin.Imported))
            throw new WorkspaceStoreException("Only unapproved generated takes can be discarded here. Manage other images from the library.");
        var trashed = images.Select(i => TrashEntry(asset, i)).ToArray();
        var next = current with
        {
            Assets = current.Assets.Select(a => a.Id == assetId ? a with
            { Images = a.Images.Where(i => !ids.Contains(i.Id)).ToList(), UpdatedUtc = clock.GetUtcNow() } : a).ToList(),
            Trash = [.. current.Trash, .. trashed]
        };
        return new(await PublishAsync(directory, next, current.Revision, ct), trashed.Select(t => t.Id).ToArray());
    }

    public Task<AssetLibrary> DeleteAssetAsync(Guid projectId, Guid assetId, long expectedRevision,
        CancellationToken cancellationToken = default) => DeleteAssetsAsync(projectId, [assetId], expectedRevision, cancellationToken);

    public async Task<AssetLibrary> DeleteAssetsAsync(Guid projectId, IReadOnlyCollection<Guid> assetIds, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var ids = assetIds.ToHashSet();
        if (ids.Count == 0 || ids.Count != assetIds.Count || ids.Contains(Guid.Empty))
            throw new WorkspaceStoreException("Choose distinct assets to delete.");
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var current = await ReadAsync(directory, projectId, cancellationToken);
        EnsureRevision(current, expectedRevision);
        var selected = current.Assets.Where(a => ids.Contains(a.Id)).ToArray();
        if (selected.Length != ids.Count) throw new WorkspaceStoreException("A selected asset no longer exists. Review the latest library before retrying.");
        return await PublishAsync(directory, current with
        {
            Assets = current.Assets.Where(a => !ids.Contains(a.Id)).ToList(),
            Reels = current.Reels.Where(r => !ids.Contains(r.AssetId)).ToList(),
            ReelTrash = [.. current.ReelTrash, .. current.Reels.Where(r => ids.Contains(r.AssetId)).Select(r => TrashedReferenceReel.Removed(r, selected.Single(a => a.Id == r.AssetId) with { Images = [], DefaultVoiceId = null }, clock.GetUtcNow()))],
            Trash = [.. current.Trash, .. selected.SelectMany(asset => asset.Images.Select(i => TrashEntry(asset, i)))],
            Voices = current.Voices.Where(v => !ids.Contains(v.AssetId)).ToList(),
            VoiceTrash = [.. current.VoiceTrash, .. current.Voices.Where(v => ids.Contains(v.AssetId)).Select(v => TrashVoice(current, v))]
        }, current.Revision, cancellationToken);
    }

    public async Task<AssetMedia?> OpenImageAsync(Guid projectId, Guid assetId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        var image = await files.MediaIndex.FindAsync<StoredImage>(Path.Combine(directory, "assets.json"), $"{assetId:D}/{imageId:D}",
            async ct => (await ReadAsync(directory, projectId, ct)).Assets
                .SelectMany(a => a.Images.Select(i => new KeyValuePair<string, StoredImage>($"{a.Id:D}/{i.Id:D}",
                    new(ImagePath(directory, a.Id, i), i.ContentType, i.CreatedUtc)))).ToDictionary(), cancellationToken);
        if (image is null) return null;
        try
        {
            var stream = new FileStream(image.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(stream, image.ContentType, image.CreatedUtc);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new WorkspaceStoreException("Couldn’t read the stored image.", e); }
    }

    private sealed record StoredImage(string Path, string ContentType, DateTimeOffset CreatedUtc);

    private async Task<AssetLibrary> PublishAsync(string directory, AssetLibrary library, long revision, CancellationToken ct)
    {
        var saved = Normalize(library) with { Revision = revision + 1, UpdatedUtc = clock.GetUtcNow() };
        Validate(saved, saved.ProjectId);
        await AtomicJsonFile.WriteAsync(Path.Combine(directory, "assets.json"), saved, ct);
        return saved.Copy();
    }

    private static async Task<AssetLibrary> ReadAsync(string directory, Guid projectId, CancellationToken ct)
    {
        var library = await AtomicJsonFile.ReadAsync<AssetLibrary>(Path.Combine(directory, "assets.json"), ct)
            ?? new AssetLibrary { ProjectId = projectId };
        Validate(library, projectId);
        return library.Copy();
    }

    public static void Validate(AssetLibrary library, Guid projectId)
    {
        AssetReusePolicy.ValidateReceipts(library);
        ValidateExtractionReviews(library);
        ValidateReels(library);
        if (library.ImagePublications is null || library.ImagePublications.Any(r => r is null || r.JobId == Guid.Empty || r.ImageId == Guid.Empty || r.AssetId == Guid.Empty || r.Fingerprint?.Length != 64) ||
            library.ImagePublications.Select(r => r.ImageId).Distinct().Count() != library.ImagePublications.Count)
            throw new WorkspaceStoreException("Invalid generated image receipts.");
        if (library.ImageCopyReceipts is null || library.ImageCopyReceipts.Any(r => r is null || r.ImageId == Guid.Empty || r.AssetId == Guid.Empty || r.Fingerprint?.Length != 64) ||
            library.ImageCopyReceipts.Select(r => r.ImageId).Distinct().Count() != library.ImageCopyReceipts.Count)
            throw new WorkspaceStoreException("Invalid image-copy receipts.");
        ValidateVoices(library);
        if (projectId == Guid.Empty || library.ProjectId != projectId || library.SchemaVersion != 1 || library.Revision < 0 ||
            library.Assets is null || library.Assets.Any(InvalidAsset) ||
            library.Assets.Select(asset => asset.Id).Distinct().Count() != library.Assets.Count ||
            library.Trash is null || library.Trash.Any(t => t is null || t.Id == Guid.Empty || t.Image is null ||
                t.Asset is null || t.Asset.Images is null || t.Asset.Images.Count != 0 || InvalidAsset(t.Asset with { Images = [t.Image] }) ||
                t.DeletedUtc == default || t.ExpiresUtc - t.DeletedUtc != TimeSpan.FromDays(30) || !Enum.IsDefined(t.State)) ||
            library.Trash.Select(t => t.Id).Distinct().Count() != library.Trash.Count ||
            library.Trash.Select(t => (t.Asset.Id, t.Image.Id)).Distinct().Count() != library.Trash.Count ||
            library.Trash.Any(t => library.Assets.Any(a => a.Id == t.Asset.Id && a.Images.Any(i => i.Id == t.Image.Id))))
            throw new WorkspaceStoreException("The assets file is invalid or uses an unsupported format. It has not been replaced.");
        var images = library.Assets.SelectMany(a => a.Images).Concat(library.Trash.Select(t => t.Image)).ToArray();
        if (images.Select(i => i.Id).Distinct().Count() != images.Length)
            throw new WorkspaceStoreException("An image identity belongs to more than one asset or Trash entry.");
        var paths = library.Assets.SelectMany(a => a.Images.Select(i => $"{i.StorageAssetId ?? a.Id:D}/{i.FileName}"))
            .Concat(library.Trash.Select(t => $"{t.Image.StorageAssetId ?? t.Asset.Id:D}/{t.Image.FileName}")).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new WorkspaceStoreException("Two image records reference the same file. The library has not been changed.");
    }

    private static bool InvalidAsset(ReferenceAsset asset)
    {
        if (asset is not null && (asset.Loras is null || asset.Loras.Any(pair => (!Enum.IsDefined(pair.Key) || pair.Key == ImageWorkflow.CodexImages) ||
            LoraPolicy.InvalidSelections(pair.Value) || pair.Value.Any(s => s.Reference.Workflow != pair.Key.LoraWorkflow())))) return true;
        if (asset is null || LookPolicy.Invalid(asset) || asset.Id == Guid.Empty || string.IsNullOrWhiteSpace(asset.Name) || asset.Name != asset.Name.Trim() ||
            asset.Description is null || asset.PreservationGuidance is null || asset.PreservationGuidance.Length > 12000 || asset.SuggestedImageTags is null || asset.Evidence is null || asset.Images is null || !Enum.IsDefined(asset.Category)) return true;
        if (!lumibelle.Services.Production.ReelUsageDefaults.Valid(asset.DefaultReelVisuals)) return true;
        if (asset.SuggestedImageTags.Any(tag => string.IsNullOrWhiteSpace(tag)) ||
            asset.Evidence.Any(source => source is null || source.Label is null || source.Excerpt is null) ||
            asset.Images.Count(image => image.IsCover) > 1 || asset.Images.Select(image => image.Id).Distinct().Count() != asset.Images.Count) return true;
        return asset.Images.Any(image => image is null || image.Id == Guid.Empty || string.IsNullOrWhiteSpace(image.FileName) ||
            image.StorageAssetId == Guid.Empty || image.PreviousAssetIds is null || image.PreviousAssetIds.Contains(Guid.Empty) ||
            image.PreviousAssetIds.Distinct().Count() != image.PreviousAssetIds.Count ||
            image.Name?.Length > 240 || InvalidImageSource(image) || InvalidRegionalImage(image) ||
            QwenImage21Policy.InvalidMetadata(image.Generation, image.Width, image.Height) ||
            image.Generation is { } resolutionMetadata && (!ImageAspectPolicy.IsResolutionSupported(resolutionMetadata.Resolution) ||
                resolutionMetadata.Workflow is ImageWorkflow.CodexImages or ImageWorkflow.QwenImage21 && resolutionMetadata.Resolution != 1024) ||
            image.LookId is not null && LookPolicy.Find(asset, image.LookId) is null ||
            Path.GetFileName(image.FileName) != image.FileName || image.Width <= 0 || image.Height <= 0 ||
            image.ContentType is not ("image/png" or "image/jpeg" or "image/webp") || image.Tags is null || image.PreservationGuidance is null || image.PreservationGuidance.Length > 12000 ||
            image.Tags.Any(tag => string.IsNullOrWhiteSpace(tag)) || image.IsCover && !image.IsReference ||
            !Enum.IsDefined(image.Origin) || image.Origin is AssetImageOrigin.Generated or AssetImageOrigin.Edited && image.Generation is null ||
            image.Generation is { } generation && (!Enum.IsDefined(generation.Workflow) || generation.Workflow == ImageWorkflow.CodexImages && (generation.Codex is null || generation.Edit?.Regional is null && (generation.Codex.Width != image.Width || generation.Codex.Height != image.Height)) || LoraPolicy.InvalidApplied(generation.Loras, generation.Workflow) ||
                (generation.AiJobId is not null || generation.BatchId is not null || generation.CandidateNumber is not null) &&
                    (generation.AiJobId is null || generation.AiJobId == Guid.Empty || generation.BatchId is null || generation.BatchId == Guid.Empty || generation.CandidateNumber is null or < 1)) ||
            image.Origin == AssetImageOrigin.Edited && InvalidEdit(image.Generation));
    }

    private static bool InvalidRegionalImage(AssetImage image)
    {
        var edit = image.Generation?.Edit;
        try
        {
            if (edit?.Regions is { } regions)
            {
                if (regions.DistinctBy(r => r.Source).Count() != regions.Count || regions.Any(r => !edit.References.Contains(r.Source))) return true;
                foreach (var region in regions) RegionalImageEdits.Validate(region);
            }
            if (edit?.Regional is { } regional)
            {
                RegionalImageEdits.Validate(regional.Selection);
                RegionalImageEdits.ValidateColour(regional.Colour);
                return image.Origin != AssetImageOrigin.Edited || regional.EdgeBlend is < 0 or > 32 || regional.ColourMatched && regional.Colour?.MatchOriginal != true ||
                    regional.Selection.Source != new AssetImageReference(edit.SourceAssetId, edit.SourceImageId) ||
                    regional.Selection.Width != image.Width || regional.Selection.Height != image.Height ||
                    regional.Canvas.CanvasWidth < 1 || regional.Canvas.CanvasHeight < 1 || regional.Canvas.Width < 1 || regional.Canvas.Height < 1;
            }
            return false;
        }
        catch (Exception e) when (e is AiGenerationException or ArgumentException or NullReferenceException) { return true; }
    }

    private static bool InvalidEdit(AssetGenerationMetadata? generation)
    {
        var edit = generation?.Edit;
        if (edit is null || edit.SourceAssetId == Guid.Empty || edit.SourceImageId == Guid.Empty ||
            edit.SourceCrop is not null && InvalidCrop(edit.SourceCrop) || edit.References is null ||
            edit.References.Any(reference => reference is null || reference.AssetId == Guid.Empty || reference.ImageId == Guid.Empty) ||
            edit.References.Distinct().Count() != edit.References.Count || edit.ReferenceCrops is null || edit.ReferenceLooks is null ||
            edit.ReferenceLooks.Count > 0 && (edit.ReferenceLooks.Any(r => r is null || r.Context is null || r.Reference is null || r.Context.AssetId != r.Reference.AssetId) || !edit.ReferenceLooks.Select(r => r.Reference).SequenceEqual(edit.References)) ||
            edit.ReferenceCrops.Any(c => c is null || c.Reference is null || c.Crop is null || InvalidCrop(c.Crop) ||
                !edit.References.Skip(1).Contains(c.Reference)) ||
            edit.ReferenceCrops.DistinctBy(c => c.Reference).Count() != edit.ReferenceCrops.Count) return true;
        if (generation!.Workflow == ImageWorkflow.QwenImage21) return QwenImage21Policy.InvalidEdit(edit);
        if (generation!.Workflow == ImageWorkflow.CodexImages)
            return generation.Codex is null || edit.References.Count is < 1 or > 8 || edit.References[0] != new AssetImageReference(edit.SourceAssetId, edit.SourceImageId) || edit.FitMode != "requested-aspect";
        if (generation!.Workflow == ImageWorkflow.Flux2Klein9bKv)
            return edit.References.Count is < 1 or > ComfyFluxKleinImages.MaximumReferences ||
                edit.References[0] != new AssetImageReference(edit.SourceAssetId, edit.SourceImageId) ||
                edit.BaseReferenceBoost is not null || edit.Lora != "" || edit.LoraStrength != 0 || edit.ReferenceBoost != 0 || edit.GroundingPixels != 0 || edit.FitMode != "reference-latent";
        return edit.References.Count > ComfyReferenceImageEditor.MaximumReferences ||
            edit.References.Count > 0 && edit.References[0] != new AssetImageReference(edit.SourceAssetId, edit.SourceImageId) ||
            edit.References.Count == 2 && edit.BaseReferenceBoost is null ||
            edit.BaseReferenceBoost is { } boost && (edit.References.Count != 2 || !float.IsFinite(boost) || boost is < 0 or > 10) ||
            edit.Lora is null || edit.LoraStrength != 0 && string.IsNullOrWhiteSpace(edit.Lora) || !float.IsFinite(edit.LoraStrength) || !float.IsFinite(edit.ReferenceBoost) ||
            edit.LoraStrength is < -100 or > 100 || edit.ReferenceBoost is < 0 or > 10 ||
            edit.GroundingPixels is < 384 or > 1024 || edit.FitMode != "fit";
    }

    private static bool InvalidCrop(ImageCropRegion crop) => !double.IsFinite(crop.X) || !double.IsFinite(crop.Y) ||
        !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height) || crop.X < 0 || crop.Y < 0 ||
        crop.Width <= 0 || crop.Height <= 0 || crop.X + crop.Width > 1.0000001 || crop.Y + crop.Height > 1.0000001;

    private static AssetLibrary Normalize(AssetLibrary library) => library with { Assets = library.Assets.Select(asset => asset with
    {
        Name = asset.Name.Trim(),
        SuggestedImageTags = NormalizeTags(asset.SuggestedImageTags),
        Images = asset.Images.Select(image => image with { Tags = NormalizeTags(image.Tags), IsReference = image.IsReference || image.IsCover }).ToList()
    }).ToList() };

    private static List<string> NormalizeTags(IEnumerable<string> tags) => tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void EnsureRevision(AssetLibrary current, long expected)
    {
        if (current.Revision != expected) throw new WorkspaceConflictException();
    }

    private static string ImagePath(string directory, Guid assetId, AssetImage image) =>
        Path.Combine(directory, "assets", (image.StorageAssetId ?? assetId).ToString("D"), "images", image.FileName);

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
