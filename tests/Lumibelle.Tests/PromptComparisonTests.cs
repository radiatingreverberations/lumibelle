using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Services.Production;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class PromptComparisonTests
{
    [Theory]
    [InlineData("", "summary:\nA quiet room.\n")]
    [InlineData("subject_definitions:\r\n<Picture 1> — Åsa 👋\r\n\r\n<d>Hej!\nAndra raden.</d>\n", "subject_definitions:\r\n<Picture 2> — Åsa 👋\r\n\r\n<d>Hej!\nAndra raden.</d>\n")]
    [InlineData("First\nRemoved\nLast", "First\nLast")]
    [InlineData("Same\n\n", "Same\n\n")]
    public void ComparisonPreservesEveryCharacter(string before, string after)
    {
        var result = PromptComparison.Compare(before, after);
        Assert.Equal(before, string.Concat(result.Before.Select(b => b.Text)));
        Assert.Equal(after, string.Concat(result.After.Select(b => b.Text)));
        if (before != after) Assert.Contains(result.Before.Concat(result.After).SelectMany(b => b.Spans), s => s.Changed);
        else Assert.All(result.Before.Concat(result.After).SelectMany(b => b.Spans), s => Assert.False(s.Changed));
    }

    [Fact]
    public void LargePromptsHaveBoundedComparisonWithoutTruncatingText()
    {
        var before = string.Concat(Enumerable.Range(0, 1200).Select(i => $"Old line {i}\n"));
        var after = before.Replace("Old", "New");
        var result = PromptComparison.Compare(before, after);
        Assert.True(result.Simplified);
        Assert.Equal(before, string.Concat(result.Before.Select(b => b.Text)));
        Assert.Equal(after, string.Concat(result.After.Select(b => b.Text)));
    }

    [Fact]
    public void RenderedDiffEscapesTagsAndPreservesLiteralDialogueAndBlankLines()
    {
        using var context = new BunitContext();
        const string before = "subject_definitions:\n<Picture 1> Åsa 👋\n\n<d>Hello.\nAgain.</d>\n<script>alert(1)</script>";
        var after = before.Replace("Hello.", "Good morning.");
        var view = context.Render<PromptComparisonView>(p => p.Add(c => c.Before, before).Add(c => c.After, after));
        Assert.Equal(before, view.Find(".before pre").TextContent);
        Assert.Equal(after, view.Find(".after pre").TextContent);
        Assert.Empty(view.FindAll("script"));
        Assert.Contains("Hello", view.Find("del").TextContent);
        Assert.Contains("Good morning", view.Find("ins").TextContent);
        Assert.Equal("subject_definitions:\n", view.Find(".prompt-comparison-heading").TextContent);
    }
}
