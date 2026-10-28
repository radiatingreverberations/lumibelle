namespace lumibelle.Models;

public enum ImageTrashState { Recoverable, Purging }

public sealed record TrashedImage
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? AssetOrder { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? ImageOrder { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DiscardOperationId { get; init; }
    [System.Text.Json.Serialization.JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    public required AssetImage Image { get; init; }
    public required ReferenceAsset Asset { get; init; }
    public DateTimeOffset DeletedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public ImageTrashState State { get; init; }
    public string? CleanupError { get; init; }
    public bool CanRestore(DateTimeOffset now) => State == ImageTrashState.Recoverable && now < ExpiresUtc;
    public string Remaining(DateTimeOffset now) => State == ImageTrashState.Purging ? "Pending permanent deletion" :
        now >= ExpiresUtc ? "Expired · awaiting cleanup" : $"{Math.Ceiling((ExpiresUtc - now).TotalDays):0} days remaining";
}

public sealed record ImageTrashResult(AssetLibrary Library, IReadOnlyList<Guid> TrashIds);
public sealed record ImageTrashRow(Guid ProjectId, string ProjectName, long Revision, TrashedImage Entry);
public sealed record ImageTrashIssue(Guid ProjectId, string Message);
public sealed record ImageTrashLibrary(IReadOnlyList<ImageTrashRow> Images, IReadOnlyList<ImageTrashIssue> Issues);
public sealed record ImagePurgeResult(AssetLibrary Library, IReadOnlyList<Guid> RemovedIds, IReadOnlyList<string> Errors);

public enum ReviewImageState { Active, Trashed, Unavailable }
public sealed record ReviewImageResolution(ReviewImageState State, AssetImage? Image, string? MediaUrl,
    string? AssetName = null, TrashedImage? Trash = null)
{
    public static ReviewImageResolution Resolve(AssetLibrary library, ImageReviewEntry entry)
    {
        var reference = entry.Reference;
        var asset = library.Assets.FirstOrDefault(a => a.Id == reference.AssetId);
        if (!entry.IsTake && asset?.Images.Any(i => i.Id == reference.ImageId) != true)
            asset = AssetImageLocations.RecordedOwner(library, reference) ?? asset;
        if (asset?.Images.FirstOrDefault(i => i.Id == reference.ImageId) is { } image)
            return new(ReviewImageState.Active, image,
                $"/media/projects/{library.ProjectId:D}/assets/{asset.Id:D}/images/{reference.ImageId:D}", asset.Name);
        // Only recorded inputs remain visible in review after being trashed.
        var trash = !entry.IsTake ? library.Trash.FirstOrDefault(t => AssetImageLocations.Matches(t.Asset.Id, t.Image, reference)) : null;
        return trash is { State: ImageTrashState.Recoverable }
            ? new(ReviewImageState.Trashed, trash.Image, $"/media/trash/{library.ProjectId:D}/{trash.Id:D}", trash.Asset.Name, trash)
            : new(ReviewImageState.Unavailable, null, null, asset?.Name, trash);
    }
}
