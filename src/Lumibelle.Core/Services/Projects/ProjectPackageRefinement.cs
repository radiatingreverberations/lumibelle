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
        var updated = new Dictionary<Guid, H3RefinementPackage>();
        foreach (var take in AllTakes(plan.State)) {
            var before = TakeBundles.Contexts(originals[take.Id]).ToDictionary(c => c.Prefix, c => c.Take);
            foreach (var (context, prefix) in TakeBundles.Contexts(take)) {
                if (context.RetainedSource?.RefinementInput is { } replayPackage) {
                    var replayPath = $"shots/takes/{take.Id:D}/{prefix}{TakeTrimming.InputsFolder}/{TakeTrimming.RefinementInputFile}";
                    var replaySource = plan.Sources[replayPath];
                    await RefinementPackages.VerifyFileAsync(replaySource.Physical, replayPackage.Bytes, replayPackage.Sha256, ct);
                    Directory.CreateDirectory(scratch);
                    var replayOutput = Path.Combine(scratch, Guid.NewGuid().ToString("D") + ".safetensors");
                    var replaySnapshot = context.Snapshot with { Width = replayPackage.Width, Height = replayPackage.Height, FrameCount = replayPackage.FrameCount };
                    await RewriteHeaderAsync(replaySource.Physical, replayOutput, replaySnapshot, null, ct);
                    var retained = await RefinementPackages.InspectAsync(replayOutput, replaySnapshot, null, ct);
                    context.RetainedSource = context.RetainedSource with { RefinementInput = retained }; updated[retained.Id] = retained;
                    plan.Sources[replayPath] = new(replayPath, replayOutput, retained.Bytes, retained.Sha256);
                }
                if (context.RefinementPackage is not { } originalPackage) continue;
                var relative = $"shots/takes/{take.Id:D}/{prefix}{H3RefinementPackage.FileName}";
                var source = plan.Sources[relative]; var original = before[prefix];
                var verified = await RefinementPackages.InspectAsync(source.Physical, original.Snapshot, original.Refinement, ct);
                if (verified != original.RefinementPackage) throw new WorkspaceStoreException("A retained refinement package changed before export.");
                Directory.CreateDirectory(scratch);
                var output = Path.Combine(scratch, Guid.NewGuid().ToString("D") + ".safetensors");
                await RewriteHeaderAsync(source.Physical, output, context.Snapshot, context.Refinement, ct);
                var package = await RefinementPackages.InspectAsync(output, context.Snapshot, context.Refinement, ct);
                context.RefinementPackage = package; updated[package.Id] = package;
                plan.Sources[relative] = new(relative, output, package.Bytes, package.Sha256);
            }
        }
        foreach (var take in AllTakes(plan.State)) {
            foreach (var (context, prefix) in TakeBundles.Contexts(take)) {
                if (context.Refinement is { } refinement && updated.TryGetValue(refinement.SourcePackage.Id, out var package)) context.Refinement = refinement with { SourcePackage = package };
                if (context.Extension is { } extension) {
                    var manifest = new List<CapturedMotionFile>();
                    foreach (var file in extension.SourceFiles) {
                        var path = $"shots/takes/{take.Id:D}/{prefix}{H3Motion.SourceFolder}/{file.FileName}";
                        await using var stream = File.OpenRead(plan.Sources[path].Physical);
                        manifest.Add(new(file.FileName, stream.Length, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct))));
                    }
                    context.Extension = extension with { SourceFiles = manifest };
                }
                context.Bytes = TakeBundles.Files(context).Distinct().Sum(f => new FileInfo(plan.Sources[$"shots/takes/{take.Id:D}/{prefix}{f}"].Physical).Length);
            }
        }
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
