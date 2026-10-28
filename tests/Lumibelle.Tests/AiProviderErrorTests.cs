using System.Net;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class AiProviderErrorTests
{
    [Fact]
    public void SharedCreditErrorsDoNotAssumeOpenRouter()
    {
        var detail = AiErrors.HttpStatus(402);
        Assert.Contains("HTTP 402", detail);
        Assert.Contains("balance", detail);
        Assert.DoesNotContain("OpenRouter", detail);
        Assert.Null(AiErrors.Display(null, "Studio AI"));
        Assert.Equal("An unrelated error.", AiErrors.Display("An unrelated error.", "Studio AI"));
    }

    [Theory]
    [InlineData(AiBackend.Vllm, "Studio AI")]
    [InlineData(AiBackend.OpenRouter, "OpenRouter")]
    public async Task StreamingPaymentErrorsNameTheCapturedConnectionAndDoNotRetry(AiBackend backend, string expectedProvider)
    {
        using var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired) {
            Content = new StringContent("{\"error\":{\"message\":\"sensitive account details\"}}")
        }));
        var settings = OpenAiConnections.Scope(new(), OpenAiConnectionTests.First with { Name = "Studio AI" });
        var registry = new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor());
        using var client = await registry.CreateAsync(backend, "test/model", settings, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<AiGenerationException>(async () => {
            await foreach (var _ in ((IProgressReportingChatClient)client).GetStreamingResponseWithProgressAsync(
                [new(ChatRole.User, "A neutral test.")], cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        Assert.Contains(expectedProvider, error.Message);
        Assert.Contains("HTTP 402", error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
        if (backend == AiBackend.Vllm) Assert.DoesNotContain("OpenRouter", error.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task ConnectionChecksAlsoUseTheConfiguredProviderName()
    {
        using var http = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)));
        var settings = OpenAiConnections.Scope(new(), OpenAiConnectionTests.First with { Name = "Studio AI" });
        var result = await new AiProviderRegistry(new TestHttpFactory(http), new FakeAiSettingsStore(), TestComfy.Monitor())
            .CheckAsync(AiBackend.Vllm, settings, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Contains("Studio AI", result.Message);
        Assert.DoesNotContain("OpenRouter", result.Message);
    }
}
