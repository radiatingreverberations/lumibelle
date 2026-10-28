using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public static class ImageWorkflowLimits
{
    public static int MaximumPreparedImageBytes(ImageWorkflow workflow) => workflow is ImageWorkflow.QwenImage21 or ImageWorkflow.Flux3ImageKlein or ImageWorkflow.Fibo2
        ? QwenImage21Policy.MaximumEncodedSourceBytes : ComfyReferenceImageEditor.MaximumEncodedSourceBytes;
    // Used only before the exact workflow has been resolved; validate that workflow afterwards.
    public const int MaximumAnyReferences = 10;
    public static bool SupportsResolution(ImageWorkflow workflow, int resolution) => ImageAspectPolicy.IsResolutionSupported(resolution) &&
        (resolution != 4096 || workflow == ImageWorkflow.Flux3ImageKlein) &&
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
        _ => throw new AiGenerationException("This image workflow is unavailable.")
    };
}
