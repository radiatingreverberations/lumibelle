namespace lumibelle.Models;

public enum TakeRefinementMode { Refine, Rework }
// The take's video and audio latents, saved by stock ComfyUI SaveLatent nodes and combined into one file.
public sealed record H3RefinementPackage(Guid Id, long Bytes, string Sha256, int Width, int Height, int FrameCount)
{
    public const string FileName = "refinement.safetensors";
}
// Source context stays in VideoSnapshot; these are the second pass's settings only.
public sealed record TakeRefinement(Guid ParentTakeId, H3RefinementPackage SourcePackage,
    TakeRefinementMode Mode, int Width, int Height, string Upscaler, H3UpscalerImplementation Implementation)
{
    public string Profile { get; init; } = "h3-refinement-stock-v1";
    // Refine keeps more of the take and its audio; Rework changes more and regenerates audio.
    public double Denoise => Mode == TakeRefinementMode.Refine ? .35 : .65;
    // ComfyUI's BasicScheduler keeps the step count when denoise is lowered, so these are the
    // steps of a 20-step schedule that a partial pass actually runs.
    public int Steps => (int)Math.Round(20 * Denoise);
}
public sealed record RefinementSize(string Name, int Width, int Height, bool Experimental, string? Issue = null)
{
    public long Pixels => (long)Width * Height;
}
