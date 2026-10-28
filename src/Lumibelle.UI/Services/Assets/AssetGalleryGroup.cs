using lumibelle.Models;

namespace lumibelle.Services.Assets;

public sealed record AssetGalleryGroup(string Key, string Name, string Description, bool Archived, IReadOnlyList<AssetGalleryItem> Items)
{
    public static IEnumerable<AssetGalleryGroup> For(ReferenceAsset asset, IEnumerable<AssetGalleryItem> source, bool groupByLook,
        IReadOnlySet<Guid>? includeEmptyLooks = null)
    {
        var items = source.ToArray();
        if (!groupByLook || asset.Category != AssetCategory.Character)
        {
            yield return new("all", "References", "", false, items);
            yield break;
        }
        var visuals = items.Where(i => i.Key.Kind != AssetMediaKind.Voice).ToArray();
        var general = visuals.Where(i => i.LookId is null).ToArray();
        if (general.Length > 0) yield return new("general", "General", "References without a specific look", false, general);
        foreach (var look in asset.Looks)
        {
            var matches = visuals.Where(i => i.LookId == look.Id).ToArray();
            if (matches.Length > 0 || !look.Archived && includeEmptyLooks?.Contains(look.Id) == true)
                yield return new(look.Id.ToString(), look.Name, look.Description, look.Archived, matches);
        }
        foreach (var missing in visuals.Where(i => i.LookId is not null && asset.Looks.All(l => l.Id != i.LookId)).GroupBy(i => i.LookId))
            yield return new(missing.Key!.Value.ToString(), "Unavailable look", "Choose another look in reference details", false, missing.ToArray());
        var voices = items.Where(i => i.Key.Kind == AssetMediaKind.Voice).ToArray();
        if (voices.Length > 0) yield return new("voices", "Voices", "Recordings and excerpts for this character", false, voices);
    }
}
