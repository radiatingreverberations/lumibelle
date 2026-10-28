using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class LiveCompatibleVisionSchemaTests
{
    [Fact]
    public async Task CompatibleProviderReadsTwoImagesWhileFollowingTheResponseSchema()
    {
        using var live = new LiveCompatibleSchemaConnection();
        var ct = TestContext.Current.CancellationToken;
        var probe = ComfyTextCapabilities.CreateImageProbe(2, colors: true);
        var row = new { type = "array", minItems = 5, maxItems = 5, items = new { type = "string", @enum = ComfyTextCapabilities.ProbeColors.Select(c => c.Name).ToArray() } };
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { first = row, second = row },
            required = new[] { "first", "second" }, additionalProperties = false });
        var message = new ChatMessage(ChatRole.User, probe.Images.Select(bytes => (AIContent)new DataContent(bytes, "image/png"))
            .Append(new TextContent(probe.Question + " Return the rows in the first and second arrays.")).ToList());
        var model = live.Model with { MaxOutputTokens = 2048 };
        var request = new AiTextJobRequest(3, AiJobKind.PromptComposition, model, false, live.Settings, "synthetic-vision-schema", .2f, 1,
            JsonSerializer.SerializeToElement(new { }), [AiTextMessage.Capture(message)]) { ResponseSchema = schema };
        using var client = await live.Registry.CreateAsync(AiBackend.Vllm, model.Model, live.Settings, ct);
        var raw = new StringBuilder(); string? finish = null;
        await foreach (var update in client.GetStreamingResponseAsync([message], TextGenerationOptions.Captured(request), ct)) {
            raw.Append(update.Text); if (update.FinishReason is { } reason) finish = reason.ToString();
        }
        await live.Save("two-images-with-schema", new { model.Model, finish, expected = probe.Codes, raw = raw.ToString() });
        Assert.Equal("stop", finish);
        using var json = JsonDocument.Parse(raw.ToString());
        var actual = new List<string>();
        foreach (var name in new[] { "first", "second" }) {
            var values = json.RootElement.GetProperty(name).EnumerateArray().Select(p => p.GetString()!).ToArray();
            Assert.Equal(5, values.Length);
            actual.Add(string.Concat(values.Select(v => Array.FindIndex(ComfyTextCapabilities.ProbeColors, c => c.Name == v))));
        }
        Assert.Equal(probe.Codes, actual);
    }
}
