namespace lumibelle.Models;

// What produced a MiniMax H3 still. Recorded per image, so changing the policy cannot alter saved provenance.
public sealed record H3ImageOutput(string AudioVae, string Preset, string SamplingProfile, string Sampler, string Scheduler, string? Checkpoint,
    int Length, int LatentFrames, string RefImageSize, int Width, int Height);
