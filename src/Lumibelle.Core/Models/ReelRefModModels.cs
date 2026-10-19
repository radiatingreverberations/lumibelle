namespace lumibelle.Models;

// Local source previews + a server filename, NOT a project-owned copy of the latent file.
public sealed record ReelRefModRecipe(string Protocol, string Key, string Selection, int Width, int Height,
    string VaeName, IReadOnlyList<string> FrameHashes)
{
    public int LatentFrames => FrameHashes.Count;
    public int Tokens => LatentFrames * (Width / 32) * (Height / 32);
}
public sealed record ReelRefModReference(ReelRefModRecipe Recipe, string ComfyUrl, string FileName, Guid BuildId);
public sealed record RefModBuildRequest(int Version, Guid JobId, Guid ProjectId, Guid AssetId,
    ReferenceVideoMedia Media, ReelKeyframeSet Keyframes, ReelRefModRecipe Recipe,
    string ComfyUrl, int TimeoutSeconds);
public sealed record RefModBuildResult(ReelRefModReference Reference);
public sealed record RefModPreparedBuild(AiJobSubmission Submission, IReadOnlyList<byte[]> Previews,
    ReelRefModReference? Existing);
public sealed record RefModInspectionFrame(int VideoNumber, int FrameNumber, string ReelName,
    string Notes, string Sha256, byte[] Png)
{
    public string? ViewName { get; init; }
}
