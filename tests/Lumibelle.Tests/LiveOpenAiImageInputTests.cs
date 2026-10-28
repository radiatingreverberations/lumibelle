using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Services;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

// Opt-in diagnostic: only synthetic images leave the machine; saved settings are never changed.
public sealed class LiveOpenAiImageInputTests
{
    [Fact]
    public async Task CompareMultipleImageAnswersWithShortAndReasoningSizedBudgets()
    {
        var directory = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_IMAGE_TEST_SETTINGS");
        var server = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_IMAGE_TEST_URL");
        var model = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_IMAGE_TEST_MODEL");
        var report = Environment.GetEnvironmentVariable("LUMIBELLE_LIVE_IMAGE_TEST_OUT");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(report))
            Assert.Skip("Set LUMIBELLE_LIVE_IMAGE_TEST_SETTINGS, URL, MODEL and OUT for a bounded synthetic two-image diagnostic.");
        var services = new ServiceCollection();
        services.AddLogging(); services.AddDataProtection().SetApplicationName("Lumibelle");
        using var provider = services.BuildServiceProvider();
        var store = new FileAiSettingsStore(new ApplicationPaths(directory),
            new global::lumibelle.WebSecretProtector(provider.GetRequiredService<IDataProtectionProvider>()));
        var key = await store.ReadVllmKeyAsync(server, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(key), "The saved connection needs a key.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var probe = ComfyTextCapabilities.CreateImageProbe(2, colors: true);
        var content = probe.Images.Select(bytes => (object)new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(bytes) } })
            .Append(new { type = "text", text = probe.Question }).ToArray();
        Directory.CreateDirectory(report);
        var passedWithLargerBudget = false;
        foreach (var tokens in new[] { 256, 2048 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, VllmConnection.NormalizeUrl(server) + "/chat/completions") {
                Content = JsonContent.Create(new { model, messages = new[] { new { role = "user", content } }, max_tokens = tokens, temperature = .01, stream = false })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"Synthetic image diagnostic returned HTTP {(int)response.StatusCode}.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var root = json.RootElement;
            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var answer = message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var passed = probe.Matches(answer);
            if (tokens == 2048) passedWithLargerBudget = passed;
            await File.WriteAllTextAsync(Path.Combine(report, $"two-images-{tokens}.json"), JsonSerializer.Serialize(new {
                model, tokens, question = probe.Question, expectedCodes = probe.Codes, passed, answer,
                finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.Clone() : default,
                provider = root.TryGetProperty("provider", out var route) ? route.Clone() : (JsonElement?)null,
                usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : (JsonElement?)null
            }, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken);
        }
        Assert.True(passedWithLargerBudget, "The larger-budget two-image result did not pass; inspect its saved synthetic receipt.");
    }
}
