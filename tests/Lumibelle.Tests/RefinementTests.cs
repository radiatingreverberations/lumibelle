using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task NewTakesKeepRefinementDataWheneverComfyUiCan()
    {
        using var f = await QueuedVideoFixture.Create(this, 20);
        f.Generator.Catalog = await f.Generator.CheckAsync(f.Settings.Value, _ct) with { PackageCaptureReady = true };
        var request = await f.Capture(1);
        Assert.True(request.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.CaptureRefinementData);
    }
    [Theory]
    [InlineData(false, 20)] [InlineData(false, 4)] [InlineData(false, 8)]
    [InlineData(true, 20)] [InlineData(true, 4)] [InlineData(true, 8)]
    public async Task OptionalCaptureIsFixedForAllCandidatesAndExtensions(bool companion, int steps)
    {
        using var f = await QueuedVideoFixture.Create(this, steps);
        var ready = await f.Generator.CheckAsync(f.Settings.Value, _ct);
        f.Generator.Catalog = ready with { PackageCaptureReady = companion };
        var request = await f.Capture(2);
        Assert.Equal(companion, request.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.CaptureRefinementData);
        // Installing/removing nodes after capture cannot rewrite remaining candidates.
        f.Generator.Catalog = ready with { PackageCaptureReady = !companion };
        var context = await f.Claim(request);
        await f.Worker.ExecuteAsync(context, request.Snapshot, _ct);
        await f.Jobs.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        await f.Worker.ValidateExtensionAsync(context.Job, request.Snapshot, _ct);
        await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        var claim = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(claim, false), await f.Jobs.ReadSnapshotAsync(claim.Id, _ct), _ct);
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(3, saved.Takes.Count);
        Assert.All(saved.Takes, take => {
            Assert.Equal(companion, take.RefinementPackage is not null);
            Assert.Equal(companion, take.Snapshot.CaptureRefinementData);
            Assert.Equal(H3Policy.Frames(f.Shot.Duration!.Value), take.FrameCount);
        });
        Assert.All(f.Graphs, submission => {
            var graph = submission.GetProperty("prompt");
            // Initial takes share one branching prompt and namespace their nodes as take_<n>_<id>;
            // an appended take keeps its own flat prompt, so derive each take from its decode node.
            var nodes = graph.EnumerateObject().ToArray();
            foreach (var prefix in nodes.Select(n => PrefixOf(n.Name)).Distinct(StringComparer.Ordinal))
            {
                string Name(string node) => prefix + node;
                if (!graph.TryGetProperty(Name("11"), out _)) continue;
                Assert.Equal(companion, graph.TryGetProperty(Name("21"), out _));
                Assert.Equal(Name("10"), graph.GetProperty(Name("11")).GetProperty("inputs").GetProperty("samples")[0].GetString());
            }
            if (!companion) Assert.DoesNotContain(nodes, n => n.Value.GetProperty("class_type").GetString()!.StartsWith("Lumibelle"));
        });
        static string PrefixOf(string name)
        {
            if (!name.StartsWith("take_", StringComparison.Ordinal)) return "";
            var separator = name.IndexOf('_', "take_".Length);
            return separator < 0 ? "" : name[..(separator + 1)];
        }
        if (!companion)
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id,
                saved.Takes[0].Id, TakeRefinementMode.Refine, saved.Takes[0].Width, saved.Takes[0].Height, _ct));
    }

    [Theory]
    [InlineData(832, 480)] [InlineData(480, 832)] [InlineData(640, 640)]
    [InlineData(1344, 768)] [InlineData(768, 1344)] [InlineData(992, 992)]
    public void RefinementSizesFollowSourceGridAndOfferNoDownscaling(int width, int height)
    {
        var sizes = RefinementPolicy.Sizes(width, height);
        Assert.Equal(4, sizes.Count); Assert.Equal((width, height), (sizes[0].Width, sizes[0].Height));
        foreach (var size in sizes)
        {
            Assert.Equal(0, size.Width % 32); Assert.Equal(0, size.Height % 32);
            Assert.InRange(Math.Abs(size.Width - size.Height * width / (double)height), 0, 48);
            if (size.Issue is null) { Assert.InRange(size.Width, width, width * 4); Assert.InRange(size.Height, height, height * 4); }
        }
        Assert.True(RefinementPolicy.DefaultSize(width, height).Pixels > (long)width * height);
        var largest = sizes[^1]; Assert.Equal("Same size", RefinementPolicy.DefaultSize(largest.Width, largest.Height).Name);
        Assert.All(RefinementPolicy.Sizes(32, 32).Skip(1), size => Assert.Contains("4×", size.Issue));
    }

    [Theory]
    [InlineData(TakeRefinementMode.Refine)] [InlineData(TakeRefinementMode.Rework)]
    public void RefinementGraphUsesOnlyStockNodesAndTheUpscalerPack(TakeRefinementMode mode)
    {
        var source = Snapshot(Guid.NewGuid(), Ready()) with { CaptureRefinementData = true };
        var package = new H3RefinementPackage(Guid.NewGuid(), 1024, new('A', 64), source.Width, source.Height, source.FrameCount);
        var size = RefinementPolicy.DefaultSize(source.Width, source.Height);
        var refine = new TakeRefinement(Guid.NewGuid(), package, mode, size.Width, size.Height, "nested/h3-3d.safetensors", H3UpscalerImplementation.Lbh);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(source, 812, Guid.NewGuid().ToString(), [], refine: new(refine, "v.latent", "a.latent"))).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString()!.StartsWith("Lumibelle", StringComparison.Ordinal));
        Assert.Equal(2, graph.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "LoadLatent"));
        Assert.Equal("30", Input("32", "latent")[0].GetString());
        Assert.Equal(size.Width, Input("32", "mode.width").GetInt32()); Assert.True(Input("32", "enable_temporal_chunking").GetBoolean());
        Assert.Equal(size.Width, Input("5", "width").GetInt32());
        Assert.Equal(refine.Denoise, Input("9", "denoise").GetDouble());
        Assert.True(Input("15", "lossless").GetBoolean()); Assert.Equal("11", Input("17", "images")[0].GetString());
    }

    [Fact]
    public void NewTakeGraphKeepsItsLatentsWithStockNodes()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready()) with { CaptureRefinementData = true };
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 12, Guid.NewGuid().ToString(), [])).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.Equal("LTXVSeparateAVLatent", graph.GetProperty("20").GetProperty("class_type").GetString());
        // The same sampler output that is decoded, so the kept audio matches the take exactly.
        Assert.Equal("10", Input("20", "av_latent")[0].GetString()); Assert.Equal(0, Input("20", "av_latent")[1].GetInt32());
        Assert.Equal("SaveLatent", graph.GetProperty("21").GetProperty("class_type").GetString());
        Assert.Equal("20", Input("21", "samples")[0].GetString()); Assert.Equal(0, Input("21", "samples")[1].GetInt32());
        Assert.Equal("20", Input("22", "samples")[0].GetString()); Assert.Equal(1, Input("22", "samples")[1].GetInt32());
        Assert.Equal("10", Input("11", "samples")[0].GetString()); Assert.Equal("10", Input("12", "samples")[0].GetString());
        var plain = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot with { CaptureRefinementData = false }, 12, Guid.NewGuid().ToString(), [])).GetProperty("prompt");
        Assert.False(plain.TryGetProperty("21", out _));
    }

    [Fact]
    public async Task RefinementPackageRoundTripsThroughComfyLatentFiles()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready()) with { CaptureRefinementData = true };
        var package = await MockRefinementPackage.WriteAsync(_root, snapshot, null, _ct);
        var path = Path.Combine(_root, H3RefinementPackage.FileName);
        var video = Path.Combine(_root, "video.latent"); var audio = Path.Combine(_root, "audio.latent");
        await RefinementPackages.SplitAsync(path, video, audio, _ct);
        var again = Path.Combine(_root, "again.safetensors");
        await RefinementPackages.ComposeAsync(video, audio, again, package.Id, snapshot.Width, snapshot.Height, snapshot.FrameCount, _ct);
        Assert.Equal(package with { Bytes = 0, Sha256 = "" }, (await RefinementPackages.InspectAsync(again, snapshot, null, _ct)) with { Bytes = 0, Sha256 = "" });
        // What ComfyUI's LoadLatent reads: the tensor plus the format marker, with nothing else.
        var header = await File.ReadAllBytesAsync(video, _ct);
        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header);
        var keys = JsonNode.Parse(header.AsSpan(8, length))!.AsObject().Select(p => p.Key).Order().ToArray();
        Assert.Equal(["latent_format_version_0", "latent_tensor"], keys);
    }

    [Fact]
    public async Task RefinementPackageRejectsChangedSizeTruncationAndChecksum()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready()) with { CaptureRefinementData = true };
        var package = await MockRefinementPackage.WriteAsync(_root, snapshot, null, _ct);
        var path = Path.Combine(_root, H3RefinementPackage.FileName);
        Assert.Equal(package, await RefinementPackages.InspectAsync(path, snapshot, null, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => RefinementPackages.InspectAsync(path, snapshot with { Width = snapshot.Width + 32 }, null, _ct));
        await using (var file = File.OpenWrite(path)) { file.Position = file.Length - 1; file.WriteByte(1); }
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => RefinementPackages.VerifyFileAsync(path, package.Bytes, package.Sha256, _ct));
        await using (var file = File.OpenWrite(path)) file.SetLength(9);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => RefinementPackages.InspectAsync(path, snapshot, null, _ct));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RefinementUsesSavedContextAfterEditsAndParentPurgeAndSurvivesRestart(bool moved)
    {
        using var f = await QueuedVideoFixture.Create(this, 8);
        var original = await f.Capture(); var context = await f.Claim(original);
        await f.Worker.ExecuteAsync(context, original.Snapshot, _ct);
        await f.Jobs.UpdateAsync(original.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var d = await f.Shots.LoadAsync(f.Project.Id, _ct); var source = Assert.Single(d.Takes);
        Assert.NotNull(source.RefinementPackage);
        f.Shot.Description = "New author edits must not enter refinement";
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], d.Revision, ct: _ct);
        var owner = source.ShotId;
        if (moved)
        {
            d = await f.Shots.LoadAsync(f.Project.Id, _ct);
            var destination = Ready(); owner = destination.Id; d.Shots.Add(destination);
            d = await f.Shots.SaveAsync(f.Project.Id, d.Shots, d.Revision, ct: _ct);
            await f.Shots.MoveTakesAsync(f.Project.Id, [source.Id], owner, d.Revision, _ct);
        }
        var size = RefinementPolicy.DefaultSize(source.Width, source.Height);
        var request = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id, TakeRefinementMode.Refine, size.Width, size.Height, _ct);
        var savedRequest = request.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(source.Snapshot.Prompt, savedRequest.Snapshot.Prompt); Assert.Equal(source.Snapshot.Fingerprint, savedRequest.Snapshot.Fingerprint);
        // Parent removal after enqueue cannot destroy the captured input package.
        d = await f.Shots.LoadAsync(f.Project.Id, _ct);
        d = await f.Shots.DiscardAsync(f.Project.Id, source.Id, ShotTrashKind.Take, d.Revision, _ct);
        await f.Shots.PurgeAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        var refinementContext = await f.Claim(request); f.Adapter.FailTransfer = true;
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(refinementContext, request.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery); Assert.Equal(2, f.Graphs.Count);
        f.Adapter.FailTransfer = false;
        await f.Worker.RecoverAsync(f.Context(refinementContext.Job, true), request.Snapshot, _ct);
        Assert.Equal(2, f.Graphs.Count);
        d = await f.Shots.LoadAsync(f.Project.Id, _ct); var refined = Assert.Single(d.Takes);
        Assert.Equal(owner, refined.ShotId);
        Assert.Equal("New author edits must not enter refinement", d.Shots[0].Description);
        Assert.Equal(source.Id, refined.Refinement!.ParentTakeId); Assert.Equal(source.Snapshot.Fingerprint, refined.Snapshot.Fingerprint);
        Assert.NotEqual(source.RefinementPackage!.Id, refined.RefinementPackage!.Id); Assert.Null(d.Shots[0].SelectedTakeId);
        await f.Jobs.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        await f.Worker.ValidateExtensionAsync(refinementContext.Job, request.Snapshot, _ct);
        var appended = await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        Assert.NotEqual(refined.Seed, appended.Candidate.Seed);
        var claim = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(claim, false), await f.Jobs.ReadSnapshotAsync(claim.Id, _ct), _ct);
        d = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Equal(2, d.Takes.Count);
        d = await f.Shots.DiscardAsync(f.Project.Id, refined.Id, ShotTrashKind.Take, d.Revision, _ct);
        d = await f.Shots.RestoreAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        Assert.Equal(refined.RefinementPackage, d.Takes.Single(t => t.Id == refined.Id).RefinementPackage);
        // A completed version can itself be refined, with no dependency on its purged parent.
        var next = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, refined.Id, TakeRefinementMode.Rework, refined.Width, refined.Height, _ct);
        Assert.Equal(refined.Id, next.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Refinement!.ParentTakeId);
    }

    [Fact]
    public async Task LegacyOrTrashedTakeCannotStartRefinementAndMissingPackageCannotPublish()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var take = d.Takes[0];
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.CaptureRefinementAsync(f.Project.Id, take.Id, Guid.NewGuid(), TakeRefinementMode.Refine, 32, 32, "model.safetensors", H3UpscalerImplementation.Plus, _ct));
        take.Snapshot = take.Snapshot with { CaptureRefinementData = true };
        Assert.Throws<WorkspaceStoreException>(() => FileShotStore.Validate(new() { ProjectId = f.Project.Id, Takes = [take] }, f.Project.Id));
    }
}
