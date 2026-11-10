using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public static class ImageWorkflowLimits
{
    public static int MaximumPreparedImageBytes(ImageWorkflow workflow) => workflow == ImageWorkflow.MiniMaxH3Image ? 80 * 1024 * 1024 : workflow is ImageWorkflow.QwenImage21 or ImageWorkflow.Flux3ImageKlein or ImageWorkflow.Fibo2 or ImageWorkflow.MiniMaxH3
        ? QwenImage21Policy.MaximumEncodedSourceBytes : ComfyReferenceImageEditor.MaximumEncodedSourceBytes;
    // Used only before the exact workflow has been resolved; validate that workflow afterwards.
    public const int MaximumAnyReferences = 16;
    public static long MaximumOutputBytes(ImageWorkflow? workflow) => workflow == ImageWorkflow.MiniMaxH3Image ? 80L * 1024 * 1024 : FileAssetStore.MaximumImageBytes;
    public static bool SupportsResolution(ImageWorkflow workflow, int resolution) => ImageAspectPolicy.IsResolutionSupported(resolution) &&
        (workflow != ImageWorkflow.MiniMaxH3Image || resolution is 2048 or 4096) &&
        (resolution != 4096 || workflow is ImageWorkflow.Flux3ImageKlein or ImageWorkflow.MiniMaxH3Image) &&
        (workflow is not (ImageWorkflow.CodexImages or ImageWorkflow.QwenImage21) || resolution == 1024);
    public static int MaximumReferences(ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Krea2 => ComfyReferenceImageEditor.MaximumReferences,
        ImageWorkflow.Flux2Klein9bKv => ComfyFluxKleinImages.MaximumReferences,
        ImageWorkflow.CodexImages => 8,
        ImageWorkflow.QwenImage21 => 10,
        ImageWorkflow.MossImage => MossImagePolicy.MaximumReferences,
        ImageWorkflow.Flux3ImageKlein => Flux3ImagePolicy.MaximumReferences,
        ImageWorkflow.Fibo2 => CaptionImagePolicy.MaximumFiboReferences,
        ImageWorkflow.ZImage => 0,
        ImageWorkflow.MiniMaxH3Image => MiniMaxH3ImagePolicy.MaximumReferences,
        ImageWorkflow.MiniMaxH3 => H3ImagePolicy.MaximumReferences,
        _ => throw new AiGenerationException("This image workflow is unavailable.")
    };
}
