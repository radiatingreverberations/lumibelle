using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class VllmModelTestJobTests
{
    private static bool IsTextPost(CapturedHttpRequest request) => request.Method == "POST" && JsonNode.Parse(request.Body)!["stream"]!.GetValue<bool>();
    private static bool IsImagePost(CapturedHttpRequest request) => request.Method == "POST" && !IsTextPost(request) && !JsonNode.Parse(request.Body)!.AsObject().ContainsKey("response_format");
    private static HttpResponseMessage ImageResponse(string answer) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = answer } } } }), System.Text.Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage AnswerImageProbe(JsonElement body)
    {
        var content = body.GetProperty("messages")[0].GetProperty("content");
        var colors = content.EnumerateArray().Last().GetProperty("text").GetString()!.Contains("colored squares", StringComparison.Ordinal);
        var answers = content.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "image_url").Select(p => {
            var url = p.GetProperty("image_url").GetProperty("url").GetString()!;
            Assert.StartsWith("data:image/png;base64,", url);
            var png = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
            return colors ? ComfyTextCapabilitiesTests.ReadColors(png) : ComfyTextCapabilitiesTests.ReadDigits(png);
        });
        return ImageResponse(string.Join(" ", answers));
    }

    [Fact]
    public async Task TestsSingleAndMultipleImagesWithTheSharedVisualChallengesAndRecoveryDoesNotReplayThem()
    {
        using var f = new Fixture(); var submission = f.Capture(); var context = await f.Claim(submission);
        Assert.True(submission.Snapshot.Deserialize<AiModelTestJobRequest>(AtomicJsonFile.Options)!.TestImageInput);
        Assert.Equal(AiJobState.Completed, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        var saved = Assert.Single(f.Settings.Value.VllmTextModelBenchmarks);
        Assert.True(saved.ImageInput!.Supported); Assert.True(saved.ImageInput.MultipleImages);
        Assert.Equal("Visible reply.", (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!.Response);
        Assert.Equal(new[] { 1, 2 }, f.Http.Requests.Where(IsImagePost).Select(r => {
            using var body = JsonDocument.Parse(r.Body);
            Assert.Equal(Fixture.Model.Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(256, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            return body.RootElement.GetProperty("messages")[0].GetProperty("content").GetArrayLength() - 1;
        }));
        var count = f.Http.Requests.Count;
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct);
        Assert.Equal(count, f.Http.Requests.Count);
        Assert.True(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!.Supported);
    }

    [Fact]
    public async Task MisreadDigitsUseTheSameColorFallbackAsComfy()
    {
        using var f = new Fixture { ImageReply = body => body.GetProperty("messages")[0].GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString()!.Contains("colored squares")
            ? AnswerImageProbe(body) : ImageResponse("I cannot read those digits.") };
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.True(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!.MultipleImages);
        Assert.Equal(4, f.Http.Requests.Count(IsImagePost));
    }

    [Fact]
    public async Task ATextOnlyAnswerDoesNotProveImageSupportOrFailTheTextTest()
    {
        using var f = new Fixture { ImageReply = _ => ImageResponse("I cannot see any image.") };
        var submission = f.Capture(); var context = await f.Claim(submission);
        Assert.Equal(AiJobState.Completed, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        var result = (await context.ReadAsync<AiModelTestJobResult>(AiJobArtifact.Result, Ct))!;
        Assert.Null(result.Vllm!.ImageInput!.Supported); Assert.Null(result.Error);
        Assert.True(result.Vllm.Measurement.Complete); Assert.Equal("Visible reply.", result.Response);
        Assert.Equal(2, f.Http.Requests.Count(IsImagePost));
    }

    [Theory]
    [InlineData(400, "This model does not support image input", false)]
    [InlineData(400, "Invalid generation option", null)]
    [InlineData(503, "Unavailable", null)]
    public async Task OnlyAnExplicitImageRejectionIsUnsupported(int status, string message, bool? supported)
    {
        using var f = new Fixture { ImageReply = _ => new((HttpStatusCode)status) {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), System.Text.Encoding.UTF8, "application/json") } };
        var submission = f.Capture(); var context = await f.Claim(submission);
        Assert.Equal(AiJobState.Completed, (await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct)).State);
        Assert.Equal(supported, Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!.Supported);
        Assert.Single(f.Http.Requests, IsImagePost);
    }

    [Fact]
    public async Task AServerThatOnlyAcceptsOneImageRemainsUsableForImageInput()
    {
        using var f = new Fixture { ImageReply = body => body.GetProperty("messages")[0].GetProperty("content").GetArrayLength() == 2
            ? AnswerImageProbe(body) : ImageResponse("Only one image was received.") };
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        var images = Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!;
        Assert.True(images.Supported); Assert.False(images.MultipleImages);
    }

    [Theory]
    [InlineData(96, 2048)] [InlineData(4096, 4096)] [InlineData(16000, 8192)]
    public async Task CompatibleServersHaveRoomForReasoningBeforeAnsweringTheImageProbe(int replyLimit, int imageLimit)
    {
        using var f = new Fixture();
        f.Settings.Value = f.Settings.Value with { OpenAiConnections = [new() {
            Id = Guid.NewGuid(), Name = "Compatible server", Url = Fixture.Model.VllmUrl!, Kind = OpenAiServerKind.Standard
        }] };
        f.Settings.Value.VllmTextModels[TextModelPolicy.Key(Fixture.Model)] = new(null, replyLimit);
        f.ImageReply = body => {
            Assert.Equal(imageLimit, body.GetProperty("max_tokens").GetInt32());
            Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
            return AnswerImageProbe(body);
        };
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.True(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!.MultipleImages);
    }

    [Fact]
    public async Task AnEmptyTruncatedImageAnswerRetainsItsUsageAndExplainsTheLimit()
    {
        using var f = new Fixture { ImageReply = _ => new(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { finish_reason = "length", message = new { content = "", reasoning_content = "private reasoning" } } },
                usage = new { completion_tokens = 256, completion_tokens_details = new { reasoning_tokens = 256 } }, provider = "test-route"
            }), System.Text.Encoding.UTF8, "application/json")
        } };
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        var images = Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!;
        Assert.Null(images.Supported);
        Assert.Contains("output-token limit", images.Detail);
        Assert.DoesNotContain("did not identify", images.Detail);
        Assert.Equal(2, f.Http.Requests.Count(IsImagePost));
        var output = await context.ReadOperationAsync<JsonElement>("model-test/image-1", AiOperationArtifact.Output, Ct);
        Assert.Equal("length", output.GetProperty("finishReason").GetString());
        Assert.Equal(256, output.GetProperty("tokenLimit").GetInt32());
        Assert.Equal(256, output.GetProperty("outputTokens").GetInt32());
        Assert.Equal(256, output.GetProperty("reasoningTokens").GetInt32());
        Assert.Equal("test-route", output.GetProperty("provider").GetString());
        Assert.DoesNotContain("private reasoning", output.GetRawText());
    }

    [Fact]
    public async Task TruncatedDigitReadingCanStillConfirmImagesWithTheColorChallenge()
    {
        using var f = new Fixture { ImageReply = body => body.GetProperty("messages")[0].GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString()!.Contains("colored squares")
            ? AnswerImageProbe(body) : new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"\"}}]}", System.Text.Encoding.UTF8, "application/json") } };
        var submission = f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.True(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput!.MultipleImages);
        Assert.Equal(4, f.Http.Requests.Count(IsImagePost));
    }

    [Fact]
    public async Task CancellingAnImageProbeRetainsTheTextAndRecoveryDoesNotRepeatInference()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var f = new Fixture { ImageReply = _ => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); } };
        var submission = f.Capture(); var context = await f.Claim(submission);
        var running = new AiJobContext(context.Job, false, f.Jobs, TimeProvider.System, (_, _) => { }, _ => { }, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Worker.ExecuteAsync(running, submission.Snapshot, cancel.Token));
        var count = f.Http.Requests.Count; f.NoNetwork = true;
        Assert.Equal(AiJobState.Completed, (await f.Worker.RecoverAsync(f.Recover(context), submission.Snapshot, Ct)).State);
        Assert.Equal(count, f.Http.Requests.Count);
        Assert.Null(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput);
    }

    [Fact]
    public async Task OlderCapturedTestsKeepTheirOriginalTextOnlyScope()
    {
        using var f = new Fixture(); var submission = f.Capture();
        var snapshot = JsonNode.Parse(submission.Snapshot.GetRawText())!;
        snapshot.AsObject().Remove("testImageInput");
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(snapshot) };
        var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, Ct);
        Assert.Single(f.Http.Requests, IsTextPost); Assert.DoesNotContain(f.Http.Requests, IsImagePost);
        Assert.Null(Assert.Single(f.Settings.Value.VllmTextModelBenchmarks).ImageInput);
    }
}
