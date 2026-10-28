using lumibelle.Models;

namespace lumibelle.Services.Assets;

public interface IAssetStore
{
    Task<AssetLibrary> MarkImagesPendingReviewAsync(Guid projectId, IReadOnlyCollection<AssetImageReference> images, long expectedRevision,
        CancellationToken ct = default) => throw new NotSupportedException("Candidate review migration is unavailable.");
    Task<ImageTrashResult> DiscardPendingImagesAsync(Guid projectId, Guid operationId, IReadOnlyCollection<AssetImageReference> images, long expectedRevision,
        CancellationToken ct = default) => throw new NotSupportedException("Candidate review discard is unavailable.");
    Task<AssetLibrary> LoadAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<AssetLibrary> SaveAsync(AssetLibrary library, long expectedRevision, CancellationToken cancellationToken = default);
    Task<AssetLibrary> ReorderAssetsAsync(Guid projectId, IReadOnlyList<Guid> order, long expectedRevision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Asset ordering is not supported by this store.");
    Task<AssetLibrary> FinishExtractionAsync(Guid projectId, ExtractionReviewInput review, long expectedRevision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Extraction review publication is not supported by this store.");
    Task<AssetLibrary> AddImageAsync(Guid projectId, Guid assetId, Stream content, AssetImageInput input,
        long expectedRevision, CancellationToken cancellationToken = default);
    // A background candidate appends to the latest manifest, preserving concurrent edits.
    Task<SavedAssetImage> PublishGeneratedImageAsync(Guid projectId, GeneratedImageInput input, Stream content,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Background image publication is not supported by this store.");
    Task<SavedAssetImage> SaveDerivedImageAsync(Guid projectId, DerivedImageRequest request,
        long expectedRevision, CancellationToken cancellationToken = default);
    Task<AssetLibrary> MoveImagesAsync(Guid projectId, Guid sourceAssetId, IReadOnlyCollection<Guid> imageIds,
        ImageDestination destination, long expectedRevision, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Moving images is not supported by this store.");
    Task<AssetLibrary> MoveReelAsync(Guid projectId, Guid sourceAssetId, Guid reelId, Guid destinationAssetId,
        long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException("Moving reels is not supported by this store.");
    Task<AssetLibrary> MoveVoiceAsync(Guid projectId, Guid sourceAssetId, Guid voiceId, Guid destinationAssetId,
        long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException("Moving voices is not supported by this store.");
    // Moves images to recoverable Trash; files are retained for 30 days.
    Task<ImageTrashResult> DeleteImageAsync(Guid projectId, Guid assetId, Guid imageId, long expectedRevision,
        CancellationToken cancellationToken = default);
    // Discard unapproved generated takes together, with one revision-checked manifest publication.
    Task<ImageTrashResult> DeleteImagesAsync(Guid projectId, Guid assetId, IReadOnlyCollection<Guid> imageIds,
        long expectedRevision, CancellationToken cancellationToken = default);
    Task<AssetLibrary> DeleteAssetAsync(Guid projectId, Guid assetId, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<AssetLibrary> DeleteAssetsAsync(Guid projectId, IReadOnlyCollection<Guid> assetIds, long expectedRevision,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Bulk asset deletion is not supported by this store.");
    Task<AssetMedia?> OpenImageAsync(Guid projectId, Guid assetId, Guid imageId, CancellationToken cancellationToken = default);
}
