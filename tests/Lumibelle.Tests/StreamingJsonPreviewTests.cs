using System.Text.Json;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class StreamingJsonPreviewTests
{
    [Fact]
    public void CompletedItemsAndAnUnfinishedStringAppearBeforeTheResponseCloses()
    {
        const string raw = "{\"beats\":[\"Eira finds the key.\",\"She follows the ri";
        var preview = StreamingJsonPreview.Read(raw);
        var beats = preview.Root!.Property("beats")!;
        Assert.False(preview.Root.Complete); Assert.False(beats.Complete);
        Assert.Equal("Eira finds the key.", beats.Children[0].Text); Assert.True(beats.Children[0].Complete);
        Assert.Equal("She follows the ri", beats.Children[1].Text); Assert.False(beats.Children[1].Complete);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(raw));
    }

    [Fact]
    public void EachPrefixOfNestedEscapedJsonCanBePreviewedAndTheWholeValueIsFaithful()
    {
        const string raw = "{\"scenes\":[{\"heading\":\"EXT. RIVER - DAY\",\"beatIds\":[\"B1\"],\"blocks\":[{\"kind\":\"Action\",\"spans\":[{\"text\":\"Eira says \\\"hello\\\".\\nA path: C:\\\\garden. \\u00c5 \\uD83C\\uDF3F\"}]}]}],\"ready\":true,\"count\":2,\"empty\":null}";
        for (var i = 0; i <= raw.Length; i++) StreamingJsonPreview.Read(raw[..i]);
        var root = StreamingJsonPreview.Read(raw).Root!;
        Assert.True(root.Complete);
        var scene = Assert.Single(root.Property("scenes")!.Children);
        Assert.True(scene.Complete);
        var text = Assert.Single(Assert.Single(scene.Property("blocks")!.Children).Property("spans")!.Children).Property("text")!;
        Assert.Equal("Eira says \"hello\".\nA path: C:\\garden. Å 🌿", text.Text);
        Assert.Equal("Yes", root.Property("ready")!.Text); Assert.Equal("2", root.Property("count")!.Text); Assert.Equal("None", root.Property("empty")!.Text);
    }

    [Theory]
    [InlineData("{\"beats\":[\"A \\\"key\\\"\\nwaits\\", "A \"key\"\nwaits")]
    [InlineData("{\"beats\":[\"A \\u00", "A ")]
    [InlineData("{\"beats\":[\"A \\u00c5", "A Å")]
    [InlineData("{\"beats\":[\"A \\uD83C", "A ")]
    [InlineData("{\"beats\":[\"A \\uD83C\\uDF3F", "A 🌿")]
    [InlineData("{\"beats\":[\"first\", \"next", "next")]
    public void PartialStringsDecodeOnlyCharactersAlreadyReceived(string raw, string expected)
    {
        var part = StreamingJsonPreview.Read(raw).Root!.Property("beats")!.Children[^1];
        Assert.Equal(expected, part.Text); Assert.False(part.Complete);
    }

    [Fact]
    public void IncompletePropertyNamesAreNotMistakenForValues()
    {
        var root = StreamingJsonPreview.Read("{\"beats\":[\"A key.\"],\"contin").Root!;
        Assert.Single(root.Children);
        Assert.Equal("A key.", root.Property("beats")!.Children[0].Text);
    }

    [Fact]
    public void FencesAndInvalidSuffixesDoNotChangeTheSavedResponseOrInventContent()
    {
        const string raw = "\u0060\u0060\u0060json\n{\"beats\":[\"A key.\"], broken \"invented\":\"never shown\"";
        var root = StreamingJsonPreview.Read(raw).Root!;
        Assert.Equal("A key.", root.Property("beats")!.Children[0].Text);
        Assert.Null(root.Property("invented")); Assert.False(root.Complete);
        Assert.Null(StreamingJsonPreview.Read("Still thinking about a key.").Root);
        Assert.Null(StreamingJsonPreview.Read("\u0060\u0060\u0060json").Root);
    }

    [Fact]
    public void PreviewWorkIsBoundedForLargeOrDeepResponses()
    {
        var large = StreamingJsonPreview.Read("{\"beats\":[\"" + new string('x', StreamingJsonPreview.MaximumCharacters));
        Assert.True(large.Limited);
        var many = StreamingJsonPreview.Read("[" + string.Join(",", Enumerable.Repeat("\"key\"", 10_000)) + "]");
        Assert.True(many.Limited); Assert.InRange(many.Root!.Children.Count, 1, 4096);
        StreamingJsonPreview.Read(new string('[', 200) + new string(']', 200));
    }
}
