using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

internal static class ProjectPackageRefinement
{
    // Packages carry only sizes as metadata, but rewrite the bounded JSON header to that known set
    // anyway, so nothing else a header might hold leaves the project. Tensor bytes stream verbatim.
    internal static async Task RewriteAsync(ProjectPackagePlan plan, IReadOnlyDictionary<Guid, ShotTake> originals,
        string scratch, CancellationToken ct)
    {
        var takes = AllTakes(plan.State).ToDictionary(t => t.Id);
        var done = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        async Task One(ShotTake take, int depth = 0)
        {
            if (depth > 128) throw new WorkspaceStoreException("Refinement provenance is too deep to transfer safely.");
            if (done.Contains(take.Id)) return;
            if (!visiting.Add(take.Id)) throw new WorkspaceStoreException("Cyclic refinement provenance in this project.");
            if (take.Refinement is { } refinement && takes.TryGetValue(refinement.ParentTakeId, out var parent))
            {
                await One(parent, depth + 1);
                if (parent.RefinementPackage is { } updated && originals[parent.Id].RefinementPackage is { } before && before.Id == refinement.SourcePackage.Id)
                    take.Refinement = refinement with { SourcePackage = updated };
            }
            if (take.RefinementPackage is not null)
            {
                var relative = $"shots/takes/{take.Id:D}/{H3RefinementPackage.FileName}";
                var source = plan.Sources[relative]; var original = originals[take.Id];
                var verified = await RefinementPackages.InspectAsync(source.Physical, original.Snapshot, original.Refinement, ct);
                if (verified != original.RefinementPackage) throw new WorkspaceStoreException("A retained refinement package changed before export.");
                var output = Path.Combine(scratch, take.Id.ToString("D") + ".safetensors");
                Directory.CreateDirectory(scratch);
                await RewriteHeaderAsync(source.Physical, output, take.Snapshot, take.Refinement, ct);
                var package = await RefinementPackages.InspectAsync(output, take.Snapshot, take.Refinement, ct);
                take.RefinementPackage = package;
                plan.Sources[relative] = new(relative, output, package.Bytes, package.Sha256);
            }
            // Take.Bytes includes MP4, distinct archive segments and the refinement file.
            // Header sanitization changes package size, so retain accurate storage accounting.
            long size = new FileInfo(plan.Sources[$"shots/takes/{take.Id:D}/video.mp4"].Physical).Length;
            foreach (var frame in take.Frames.DistinctBy(f => f.FileName))
                size = checked(size + new FileInfo(plan.Sources[$"shots/takes/{take.Id:D}/{frame.FileName}"].Physical).Length);
            take.Bytes = checked(size + (take.RefinementPackage?.Bytes ?? 0));
            visiting.Remove(take.Id); done.Add(take.Id);
        }
        foreach (var take in takes.Values) await One(take);
    }
    internal static IEnumerable<ShotTake> AllTakes(ProjectPackageState state) =>
        state.Shots.Takes.Concat(state.Shots.Trash.Where(t => t.Take is not null).Select(t => t.Take!));
    internal static async Task RewriteHeaderAsync(string source, string output, VideoSnapshot snapshot, TakeRefinement? refinement, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        var prefix = new byte[8]; await input.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (length is < 2 or > 16 * 1024 * 1024 || (long)length + 8 >= input.Length) throw new WorkspaceStoreException("Invalid refinement header.");
        var header = new byte[(int)length]; await input.ReadExactlyAsync(header, ct);
        ProjectPackageFormat.StrictJson(header);
        var root = JsonNode.Parse(header)!.AsObject();
        var metadata = JsonNode.Parse(root["__metadata__"]!["lumibelle"]!.GetValue<string>())!.AsObject();
        var clean = new JsonObject();
        foreach (var name in new[] { "version", "id", "fps", "width", "height", "frameCount" }) clean.Add(name, metadata[name]?.DeepClone());
        root["__metadata__"] = new JsonObject { ["lumibelle"] = clean.ToJsonString() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        var padded = checked((bytes.Length + 7) / 8 * 8);
        if (padded > 16 * 1024 * 1024) throw new WorkspaceStoreException("Sanitized refinement metadata is too large.");
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)padded);
        await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        await destination.WriteAsync(prefix, ct); await destination.WriteAsync(bytes, ct);
        if (padded != bytes.Length) await destination.WriteAsync(Enumerable.Repeat((byte)' ', padded - bytes.Length).ToArray(), ct);
        // Offsets are relative to this data region, so changing header length does not move
        // a tensor relative to its offsets. A follow-up InspectAsync checks the whole file.
        await input.CopyToAsync(destination, 131072, ct);
    }
}
