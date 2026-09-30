namespace lumibelle.Models;

public sealed record ImageMetadataValues(string Name, string Tags, Guid? LookId, string PreservationGuidance)
{
    public static ImageMetadataValues From(AssetImage image) => new(image.Name ?? "", string.Join(", ", image.Tags), image.LookId,
        image.PreservationGuidance);
}

/// <summary>Only editable organization fields are included; provenance is never a draft.</summary>
public sealed record ImageMetadataEdit(Guid ProjectId, AssetImageReference Reference, ImageMetadataValues Original, ImageMetadataValues Value)
{
    public AssetImage Apply(ReferenceAsset asset)
    {
        var image = asset.Images.SingleOrDefault(i => i.Id == Reference.ImageId);
        if (asset.Id != Reference.AssetId || image is null) throw new InvalidOperationException("This image was moved or removed. Your details are still here; reopen its current location before applying.");
        var current = ImageMetadataValues.From(image);
        bool Conflict<T>(T before, T after, T latest) => !EqualityComparer<T>.Default.Equals(before, after) && !EqualityComparer<T>.Default.Equals(before, latest) && !EqualityComparer<T>.Default.Equals(after, latest);
        if (Conflict(Original.Name, Value.Name, current.Name) || Conflict(Original.Tags, Value.Tags, current.Tags) ||
            Conflict(Original.LookId, Value.LookId, current.LookId) || Conflict(Original.PreservationGuidance, Value.PreservationGuidance, current.PreservationGuidance))
            throw new InvalidOperationException("These details changed in another tab. Your draft is kept for copying. Cancel and reopen details to review the saved changes.");
        if (Value.LookId != Original.LookId && Value.LookId is { } look && (asset.Category != AssetCategory.Character || !asset.Looks.Any(l => l.Id == look && !l.Archived)))
            throw new InvalidOperationException("This look is unavailable or archived. Choose an active look or General / unassigned.");
        return image with
        {
            Name = Original.Name == Value.Name ? image.Name : string.IsNullOrWhiteSpace(Value.Name) ? null : Value.Name.Trim(),
            Tags = Original.Tags == Value.Tags ? image.Tags : Value.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            LookId = Original.LookId == Value.LookId ? image.LookId : Value.LookId,
            PreservationGuidance = Original.PreservationGuidance == Value.PreservationGuidance ? image.PreservationGuidance : Value.PreservationGuidance
        };
    }
}
