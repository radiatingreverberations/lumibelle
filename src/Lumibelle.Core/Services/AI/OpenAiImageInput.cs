using lumibelle.Models;

namespace lumibelle.Services.AI;

public static class OpenAiImageInput
{
    public static OpenAiImageInputTest? Latest(AiSettings settings, string server, string model) => settings.VllmTextModelBenchmarks
        .Where(b => b.Measurement.Model == model && TextModelPolicy.SameVllmServer(b.Server, server) && b.ImageInput?.Supported is not null)
        .OrderByDescending(b => b.ImageInput!.TestedUtc).Select(b => b.ImageInput).FirstOrDefault();

    public static string Label(AiModel model) => model.SupportsImages ? "Text + images" : model.ImageInputKnown ? "Text only" : "Image input unverified";

    public static string TestLabel(OpenAiImageInputTest? test) => test?.Supported switch {
        true => test.MultipleImages ? "Working · multiple images" : "Working · single image",
        false => "Unsupported by this server/model",
        _ => "Unverified"
    };
}
