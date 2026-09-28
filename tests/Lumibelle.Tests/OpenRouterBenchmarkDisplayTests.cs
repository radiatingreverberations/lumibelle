using Bunit;
using lumibelle.Components;
using lumibelle.Models;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class OpenRouterBenchmarkDisplayTests : BunitContext
{
    private static OpenRouterTextModelBenchmark Sample() => new(Guid.NewGuid(), "liquid/lfm-2.5-2.6b:free", DateTimeOffset.UtcNow,
        256, false, 1.4, null, 37, 256, 256, 0, 0, "liquid/lfm-2.5-2.6b:free", "Liquid", "test-generation", "length", true);

    [Fact]
    public void LegacyReasoningOnlyTestShowsNoReplyAndNoSpeed()
    {
        var page = Render<OpenRouterBenchmarkDetails>(p => p.Add(c => c.Benchmark, Sample()));
        Assert.Contains("Reasoning only · no answer", page.Markup);
        Assert.Contains("0 reply tokens", page.Markup); Assert.Contains("256 reasoning tokens", page.Markup);
        Assert.Contains("token limit was reached during reasoning", page.Markup);
        Assert.DoesNotContain("tokens/s", page.Markup);
    }

    [Fact]
    public void ReplySpeedExcludesReasoningAndUnreliableBreakdownsStayUnknown()
    {
        var benchmark = Sample() with { ElapsedSeconds = 2, FirstTextSeconds = .5, OutputTokens = 42, ReasoningTokens = 5, HasResponseText = true, FinishReason = "stop" };
        var page = Render<OpenRouterBenchmarkDetails>(p => p.Add(c => c.Benchmark, benchmark));
        Assert.Contains("18.5 reply tokens/s", page.Markup); Assert.Contains("37 reply tokens", page.Markup); Assert.Contains("42 total output tokens", page.Markup);
        page.Render(p => p.Add(c => c.Benchmark, benchmark with { ReasoningTokens = 50 }));
        Assert.Contains("more reasoning tokens than total output", page.Markup); Assert.DoesNotContain("tokens/s", page.Markup);
        page.Render(p => p.Add(c => c.Benchmark, benchmark with { ReasoningTokens = null }));
        Assert.Contains("Reply received", page.Markup); Assert.Contains("unreported reasoning tokens", page.Markup); Assert.DoesNotContain("tokens/s", page.Markup);
    }
}
