using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class OpenAiStructuredOutput
{
    public static OpenAiStructuredOutputTest? Latest(AiSettings settings, string server, string model) => settings.VllmTextModelBenchmarks
        .Where(b => b.Measurement.Model == model && TextModelPolicy.SameVllmServer(b.Server, server) && b.StructuredOutput?.Supported is not null)
        .OrderByDescending(b => b.StructuredOutput!.TestedUtc).Select(b => b.StructuredOutput).FirstOrDefault();

    public static IReadOnlyList<string>? Parameters(JsonElement model) => model.TryGetProperty("supported_parameters", out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray() : null;

    // response_format alone can mean JSON-object mode. Only the structured-output declaration proves schema support.
    public static bool? Advertised(JsonElement model) => Parameters(model)?.Contains("structured_outputs", StringComparer.Ordinal) == true ? true : null;

    public static string Label(OpenAiStructuredOutputTest test) => test.Supported switch {
        true => "Working", false => "Unsupported by this server/model", _ => "Unverified"
    };
}
