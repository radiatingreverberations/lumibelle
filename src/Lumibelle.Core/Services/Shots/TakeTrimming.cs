using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class TakeTrimming
{
    public const string InputsFolder = "refinement-inputs";
    public const string RefinementInputFile = "refinement-source.safetensors";
    public static Guid OutputId(Guid fullTakeId) => new(SHA256.HashData(fullTakeId.ToByteArray().Concat("trim"u8.ToArray()).ToArray()).AsSpan(0, 16));
    public static void Range(int start, int end, int count)
    {
        if (start < 0 || end <= start || end > count) throw new WorkspaceStoreException("Choose at least one frame inside this take.");
    }
    public static void Validate(ShotTake take)
    {
        if (take.Snapshot is null) throw new WorkspaceStoreException("The take generation context is missing.");
        if (take.Trim is { } t) {
        Range(t.StartFrame, t.EndFrameExclusive, t.ParentFrameCount);
        if (take.Composition is null) Range(t.SourceStartFrame, t.SourceEndFrameExclusive, take.Snapshot.FrameCount);
        if (t.ParentTakeId == Guid.Empty || t.ParentTakeId == take.Id || take.Snapshot.FrameCount > 362 ||
            take.Composition is null && t.ParentFrameCount > take.Snapshot.FrameCount || t.SourceStartFrame < t.StartFrame ||
            t.EndFrameExclusive - t.StartFrame != t.SourceEndFrameExclusive - t.SourceStartFrame ||
            take.Composition is null && take.RefinementPackage is not null && take.RetainedSource is null)
            throw new WorkspaceStoreException("Invalid trimmed take provenance.");
        }
        if (take.RetainedSource is { } source) {
            if (source.RefinementInput is { } original && (take.Refinement?.SourcePackage != original || original.Bytes <= 0 || !RefinementPolicy.Hash(original.Sha256)))
                throw new WorkspaceStoreException("Invalid retained refinement replay package.");
            if (source.Inputs is null || source.Inputs.Count > 100 || source.Inputs.Any(i => i is null) || source.Inputs.DistinctBy(i => i.FileName).Count() != source.Inputs.Count)
                throw new WorkspaceStoreException("Invalid retained refinement inputs.");
            foreach (var input in source.Inputs) {
                AiVideoJobPolicy.ValidateFileName(input.FileName, input.EffectiveKind);
                if (input.Bytes <= 0 || !RefinementPolicy.Hash(input.Sha256)) throw new WorkspaceStoreException("Invalid retained refinement input.");
            }
        }
    }
    public static async Task CopyVerifiedAsync(string source, string target, long bytes, string hash, CancellationToken ct)
    {
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true)) {
            await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true);
        }
        await RefinementPackages.VerifyFileAsync(target, bytes, hash, ct);
    }
}
