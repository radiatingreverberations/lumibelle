using System.Text.Json;
using lumibelle.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace lumibelle.Services.AI;

/// <summary>Runs one probe workflow and returns its text, or null when ComfyUI rejected or failed it.</summary>
public delegate Task<string?> ComfyProbeRunner(string name, Func<string, object> workflowFactory, CancellationToken ct);

/// <summary>
/// Detects what a model honors on a given ComfyUI version. TextGenerate accepts system_prompt and images for
/// every model, but tokenizers that do not support them drop them silently, so the node schema alone is not
/// evidence. Each probe asks for a random code the model can only know through the channel being tested.
/// </summary>
public static class ComfyTextCapabilities
{
    public const int ProbeTokens = 48;
    private const float ProbeTemperature = 0.01f;
    internal const string SystemPromptQuestion = "What is the verification code? Reply with the code only.";
    internal const string ImageQuestion = "Read the number printed in the image. Reply with the digits only.";
    internal const string BatchQuestion = "Two images are attached, each showing a number. Reply with the number from the first image, " +
        "a space, then the number from the second image. Digits only.";

    public static bool SupportsSystemPromptInput(JsonElement nodes) => ComfyTextVision.HasInput(nodes, "TextGenerate", "system_prompt", "STRING");

    /// <summary>ComfyUI versions whose most recent test of this model confirmed the native system prompt.</summary>
    public static IReadOnlyList<string> SystemPromptVersions(TextModelReference model, AiSettings settings) =>
        model.Backend != AiBackend.ComfyUI ? [] : settings.ComfyTextModelVerifications
            .Where(item => TextModelPolicy.SameServer(item.ComfyUrl, model.ComfyUrl) && item.Model == model.Model && item.Capabilities is not null)
            .GroupBy(item => item.ComfyVersion, StringComparer.Ordinal)
            .Where(group => group.MaxBy(item => item.VerifiedUtc)!.Capabilities!.SystemPrompt)
            .Select(group => group.Key).ToArray();

    /// <summary>
    /// Confirms immediately before submission that the server still runs a version on which the model test observed
    /// the system prompt being honored. Otherwise the request keeps the role-marked transcript.
    /// </summary>
    public static async Task<bool> UseSystemPromptAsync(HttpClient http, IReadOnlyCollection<string> versions, CancellationToken ct)
    {
        if (versions.Count == 0) return false;
        using var response = await http.GetAsync("system_stats", ct);
        response.EnsureSuccessStatusCode();
        try
        {
            using var stats = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return stats.RootElement.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Object &&
                system.TryGetProperty("comfyui_version", out var version) && version.ValueKind == JsonValueKind.String &&
                versions.Contains(version.GetString());
        }
        catch (JsonException) { return false; }
    }

    public static async Task<ComfyTextModelCapabilities> ProbeAsync(HttpClient http, string model, bool systemPromptInput,
        ComfyVisionCapabilities vision, ComfyProbeRunner run, Func<string, Task> report, CancellationToken ct)
    {
        var systemPrompt = false;
        if (systemPromptInput)
        {
            await report("Checking system prompt support…");
            var code = Code(6);
            var seed = Seed();
            systemPrompt = Repeats(await run("system-prompt", client => ComfyChatClient.BuildWorkflow(model, SystemPromptQuestion, ProbeTokens,
                ProbeTemperature, seed, client, $"Verification code: {code}. When the user asks for the verification code, reply with exactly that code and nothing else."), ct), code);
        }
        var mode = ComfyVisionInput.Disabled;
        if (vision.SingleImage)
        {
            await report("Checking image input…");
            var code = Code(4);
            var seed = Seed();
            var uploaded = await ComfyTextVision.UploadAsync(http, model, ComfyVisionInput.SingleImage, [DigitImage(code)], ct);
            if (Repeats(await run("image", client => ComfyTextVision.BuildWorkflow(model, ImageQuestion, ProbeTokens, ProbeTemperature, seed, client, uploaded), ct), code))
            {
                mode = ComfyVisionInput.SingleImage;
                if (vision.ImageBatch)
                {
                    await report("Checking multiple-image input…");
                    string first = Code(4), second;
                    do second = Code(4); while (second == first);
                    var batchSeed = Seed();
                    var batch = await ComfyTextVision.UploadAsync(http, model, ComfyVisionInput.ImageBatch, [DigitImage(first), DigitImage(second)], ct);
                    if (Repeats(await run("image-batch", client => ComfyTextVision.BuildWorkflow(model, BatchQuestion, ProbeTokens, ProbeTemperature, batchSeed, client, batch), ct), first, second))
                        mode = ComfyVisionInput.ImageBatch;
                }
            }
        }
        return new(systemPrompt, mode);
    }

    /// <summary>True when every code appears, in order, among the response's digits.</summary>
    internal static bool Repeats(string? response, params string[] codes)
    {
        if (string.IsNullOrEmpty(response)) return false;
        var digits = new string(response.Where(char.IsAsciiDigit).ToArray());
        var position = 0;
        foreach (var code in codes)
        {
            var found = digits.IndexOf(code, position, StringComparison.Ordinal);
            if (found < 0) return false;
            position = found + code.Length;
        }
        return true;
    }

    private static string Code(int length) =>
        (char)('1' + Random.Shared.Next(9)) + string.Concat(Enumerable.Range(1, length - 1).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static long Seed() => Random.Shared.NextInt64(1, long.MaxValue);

    // 5×7 dot-matrix digits: no font dependency, and legible to vision encoders at this size.
    internal static readonly string[][] Glyphs =
    [
        ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
        ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ["01110", "10001", "10001", "01111", "00001", "00010", "01100"]
    ];

    internal static byte[] DigitImage(string digits)
    {
        const int cell = 20, gap = 40, margin = 60;
        var width = margin * 2 + digits.Length * 5 * cell + (digits.Length - 1) * gap;
        using var image = new Image<Rgba32>(width, margin * 2 + 7 * cell, new Rgba32(255, 255, 255, 255));
        for (var d = 0; d < digits.Length; d++)
        {
            var glyph = Glyphs[digits[d] - '0'];
            var left = margin + d * (5 * cell + gap);
            for (var row = 0; row < 7; row++)
                for (var column = 0; column < 5; column++)
                    if (glyph[row][column] == '1')
                        for (var y = 0; y < cell; y++)
                            for (var x = 0; x < cell; x++)
                                image[left + column * cell + x, margin + row * cell + y] = new Rgba32(0, 0, 0, 255);
        }
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }
}
