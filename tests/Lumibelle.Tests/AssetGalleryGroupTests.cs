using lumibelle.Models;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

public sealed class AssetGalleryGroupTests
{
    [Fact]
    public void GroupingKeepsImagesAndReelsTogetherAndVoicesSeparate()
    {
        var look = new CharacterLook { Name = "Evening", Description = "Formal clothes" };
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Alex", Category = AssetCategory.Character, Looks = [look] };
        var image = Item(AssetMediaKind.Image, look.Id);
        var reel = Item(AssetMediaKind.Reel, look.Id);
        var general = Item(AssetMediaKind.Image);
        var voice = Item(AssetMediaKind.Voice);
        var groups = AssetGalleryGroup.For(owner, [reel, general, image, voice], true).ToArray();
        Assert.Equal(["General", "Evening", "Voices"], groups.Select(g => g.Name));
        Assert.Equal([reel, image], groups[1].Items);
        Assert.Equal("Formal clothes", groups[1].Description);
        Assert.Equal(voice, Assert.Single(groups[2].Items));
    }

    [Fact]
    public void ArchivedAndMissingLooksRetainTheirReferencesWithoutInventingAssignments()
    {
        var archived = new CharacterLook { Name = "Old outfit", Archived = true };
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Alex", Category = AssetCategory.Character, Looks = [archived] };
        var oldImage = Item(AssetMediaKind.Image, archived.Id);
        var missing = Item(AssetMediaKind.Reel, Guid.NewGuid());
        var groups = AssetGalleryGroup.For(owner, [missing, oldImage], true).ToArray();
        Assert.True(groups[0].Archived);
        Assert.Equal(oldImage, Assert.Single(groups[0].Items));
        Assert.Equal("Unavailable look", groups[1].Name);
        Assert.Equal(missing, Assert.Single(groups[1].Items));
        Assert.Equal(2, groups.Sum(g => g.Items.Count));
    }

    [Theory]
    [InlineData(AssetCategory.Character, false)]
    [InlineData(AssetCategory.Environment, true)]
    public void FlatViewPreservesTheIncomingOrder(AssetCategory category, bool grouped)
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Asset", Category = category };
        var items = new[] { Item(AssetMediaKind.Reel), Item(AssetMediaKind.Image), Item(AssetMediaKind.Voice) };
        Assert.Equal(items, Assert.Single(AssetGalleryGroup.For(owner, items, grouped)).Items);
    }

    [Fact]
    public void RequestedEmptyLooksJoinTheSameGroupsWithoutEmptyArchivedLooks()
    {
        var first = new CharacterLook { Name = "Raincoat" };
        var empty = new CharacterLook { Name = "Evening" };
        var archived = new CharacterLook { Name = "Retired", Archived = true };
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character, Looks = [first, empty, archived] };
        var general = Item(AssetMediaKind.Image);
        var image = Item(AssetMediaKind.Image, first.Id);
        var groups = AssetGalleryGroup.For(owner, [image, general], true, new HashSet<Guid> { first.Id, empty.Id, archived.Id }).ToArray();
        Assert.Equal(new[] { "General", "Raincoat", "Evening" }, groups.Select(g => g.Name));
        Assert.Equal(image, Assert.Single(groups[1].Items)); Assert.Empty(groups[2].Items);
        Assert.Empty(AssetGalleryGroup.For(owner, [], true));
        Assert.Equal("Evening", Assert.Single(AssetGalleryGroup.For(owner, [], true, new HashSet<Guid> { empty.Id })).Name);
    }

    private static AssetGalleryItem Item(AssetMediaKind kind, Guid? look = null) =>
        new(new(kind, Guid.NewGuid()), kind.ToString(), DateTimeOffset.UtcNow, look);
}
