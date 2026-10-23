using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class JsonOutputProgressTests
{
    [Fact]
    public void DetectsASeparatorWhitespaceLoopAcrossChunks()
    {
        var progress = new JsonOutputProgress();
        Assert.True(progress.Observe("[" + new string(' ', 200)));
        Assert.False(progress.Observe(new string('\n', 312)));
    }

    [Fact]
    public void MeaningfulJsonResetsTheSeparatorRun()
    {
        var progress = new JsonOutputProgress();
        Assert.True(progress.Observe("[" + new string(' ', 510)));
        Assert.True(progress.Observe("\"a\"," + new string(' ', 510)));
        Assert.True(progress.Observe("\"b\"]"));
    }

    [Fact]
    public void PreservesProseWhitespaceAndTracksEscapedQuotesAcrossChunks()
    {
        var progress = new JsonOutputProgress();
        Assert.True(progress.Observe("{\"text\":\"A \\"));
        Assert.True(progress.Observe("\"" + new string(' ', 10000)));
        Assert.True(progress.Observe("line.\""));
        Assert.False(progress.Observe(new string(' ', JsonOutputProgress.MaximumSeparatorWhitespace)));
    }
}
