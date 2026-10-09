using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Projects;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Picture = SixLabors.ImageSharp.Image;

namespace Lumibelle.Tests;

public sealed partial class ProjectPackageTests
{
    private const string Secret = "PRIVATE-OTHER-PROJECT-DO-NOT-EXPORT";
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aRZcAAAAASUVORK5CYII=");
    private static LoraSelection Lora(string name, bool enabled = true, float strength = 1) =>
        new(new("http://localhost:8188", name + ".safetensors", LoraWorkflow.MiniMaxH3Ref2VA, name), strength, enabled);

    [Fact]
    public async Task PackageRoundTripPublishesOnlyAfterConfirmationAndKeepsProjectIdentity()
    {
        using var source = new Fixture(); var project = await source.Create();
        using var target = new Fixture();
        var export = await source.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken);
        await using var bytes = (await source.Service.OpenExportAsync(project.Id, export.Id, TestContext.Current.CancellationToken))!;
        var pending = await target.Service.StageImportAsync(bytes.Content, ct: TestContext.Current.CancellationToken);
        Assert.Empty((await target.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects);
        var imported = await target.Service.CommitImportAsync(pending.Token, TestContext.Current.CancellationToken);
        Assert.Equal(project, imported);
        Assert.Equal(project.Id, Assert.Single((await target.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects).Id);
        Assert.Equal(imported, await target.Service.CommitImportAsync(pending.Token, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(target.Paths.Jobs));
        Assert.False(File.Exists(target.Paths.Settings));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PackageIncludesOnlyReferencedTrashAndFollowsCroppedAncestry(bool include)
    {
        using var f = new Fixture(); var project = await f.Create();
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Character" };
        var ancestor = Image(); var parent = Image() with { Origin = AssetImageOrigin.Cropped,
            Source = new(Crop: new(project.Id, asset.Id, ancestor.Id, new(), 1, 1)) };
        var active = Image() with { Origin = AssetImageOrigin.Cropped, Source = new(Crop: new(project.Id, asset.Id, parent.Id, new(), 1, 1)) };
        var unrelated = Image(); var deleted = DateTimeOffset.UtcNow.AddDays(-90);
        TrashedImage Trash(AssetImage i) => new() { Image = i, Asset = asset, DeletedUtc = deleted, ExpiresUtc = deleted.AddDays(30) };
        var library = new AssetLibrary { ProjectId = project.Id, Assets = [asset with { Images = [active] }], Trash = [Trash(parent), Trash(ancestor), Trash(unrelated)] };
        await f.Save(project, "assets.json", library);
        foreach (var image in new[] { active, parent, ancestor, unrelated }) await f.Bytes(project, $"assets/{asset.Id:D}/images/{image.FileName}", Pixel);
        var original = await File.ReadAllBytesAsync(Path.Combine(f.Root(project), "assets.json"), TestContext.Current.CancellationToken);
        var package = await f.Service.ExportAsync(project.Id, new(include), ct: TestContext.Current.CancellationToken);
        var contents = await f.Unzip(project, package);
        var saved = ProjectPackageFormat.Parse<AssetLibrary>(contents["project/assets.json"]);
        Assert.Equal(include ? 2 : 0, saved.Trash.Count);
        Assert.DoesNotContain(saved.Trash, t => t.Image.Id == unrelated.Id);
        Assert.Equal(include, contents.ContainsKey($"project/assets/{asset.Id:D}/images/{parent.FileName}"));
        Assert.Equal(include, contents.ContainsKey($"project/assets/{asset.Id:D}/images/{ancestor.FileName}"));
        Assert.DoesNotContain(contents.Keys, k => k.Contains(unrelated.FileName));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(f.Root(project), "assets.json"), TestContext.Current.CancellationToken));
        using var target = new Fixture(); await using var stream = (await f.Service.OpenExportAsync(project.Id, package.Id, TestContext.Current.CancellationToken))!;
        var preview = await target.Service.StageImportAsync(stream.Content, ct: TestContext.Current.CancellationToken); await target.Service.CommitImportAsync(preview.Token, TestContext.Current.CancellationToken);
        var imported = await new FileAssetStore(target.Files, TimeProvider.System).LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(include ? 2 : 0, imported.Trash.Count);
        Assert.All(imported.Trash, t => { Assert.Equal(ImageTrashState.Recoverable, t.State); Assert.True(t.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(29)); });
        Assert.Single(imported.Assets[0].Images);
    }
    [Fact]
    public async Task PackageFlattensOnlyReferencedGlobalSetupAndDropsDisabledAndZeroLoras()
    {
        using var f = new Fixture(); var project = await f.Create(); var shot = Ready();
        var used = new GenerationSetup { Name = "Used setup", Version = 3, Settings = new() {
            GenerationPreset = "standard", Loras = [Lora("used"), Lora(Secret, false), Lora(Secret + "-zero", strength: 0)] } };
        f.Setups.Value.Setups.Add(used);
        f.Setups.Value.Setups.Add(new() { Name = Secret, Version = 1, Settings = new() { Loras = [Lora(Secret)] } });
        var composition = new ProductionComposition { ShotId = shot.Id, Version = 1, Name = "Stale local cache", GenerationSetupId = used.Id, GenerationSetupVersion = 1 };
        await f.Save(project, "shots.json", new ShotDocument { ProjectId = project.Id, Shots = [shot] });
        await f.Save(project, "production.json", new ProductionDocument { ProjectId = project.Id, Compositions = [composition], ShotContent = [new() { ShotId = shot.Id }] });
        var output = await f.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken); var contents = await f.Unzip(project, output);
        Assert.DoesNotContain(Secret, string.Join("\n", contents.Where(p => p.Key.EndsWith(".json")).Select(p => Encoding.UTF8.GetString(p.Value))));
        var p = ProjectPackageFormat.Parse<ProductionDocument>(contents["project/production.json"]);
        Assert.Null(p.Compositions[0].GenerationSetupId); Assert.Equal("Used setup", p.Compositions[0].Name);
        Assert.Equal("used.safetensors", Assert.Single(p.Compositions[0].Shot.Loras!).Reference.FileName);
        Assert.Equal(3, used.Settings.Loras!.Count); Assert.Equal(2, f.Setups.Value.Setups.Count);
    }
    [Theory]
    [InlineData("standard")][InlineData("turbo8")][InlineData("larry")][InlineData("pdd")][InlineData("hyperflow")]
    public void PackagePrivacyKeepsOnlyTheSelectedAccelerator(string key)
    {
        var snapshot = Snapshot(Guid.NewGuid(), key);
        var source = snapshot.Settings;
        source.TurboLora = key == "turbo4" ? "used.safetensors" : Secret;
        source.Turbo8StepLora = key == "turbo8" ? "used.safetensors" : Secret;
        source.LarryLora = key == "larry" ? "used.safetensors" : Secret;
        source.PddCheckpoint = key == "pdd" ? "used.safetensors" : Secret;
        source.HyperFlowLora = key == "hyperflow" ? H3HyperFlow.DefaultCheckpoint : Secret;
        snapshot = snapshot with { Preset = H3Presets.Capture(snapshot.Shot, source), Sampling = H3Policy.Sampling(snapshot.Shot, source) };
        var cleaned = new ProjectPackagePrivacy().Clean(snapshot);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(cleaned, AtomicJsonFile.Options));
        Assert.Equal(snapshot.Preset!.Checkpoint, cleaned.Preset!.Checkpoint);
        Assert.Equal(snapshot.Prompt, cleaned.Prompt); Assert.Equal(snapshot.Sampling, cleaned.Sampling);
        H3Presets.Validate(cleaned); H3Loras.ValidateSnapshot(cleaned);
        Assert.Contains(Secret, JsonSerializer.Serialize(snapshot, AtomicJsonFile.Options));
    }
    [Fact]
    public async Task PackageRefinementHeaderIsSanitizedWithoutChangingTensorBytesOrOriginal()
    {
        using var f = new Fixture(); var project = await f.Create();
        var snapshot = Snapshot(project.Id, "standard") with { CaptureRefinementData = true };
        snapshot.Settings.TurboLora = Secret; snapshot.Settings.Turbo8StepLora = Secret;
        snapshot.Settings.HyperFlowLora = Secret; snapshot.Settings.PddCheckpoint = Secret;
        var take = new ShotTake { Snapshot = snapshot, ShotId = snapshot.Shot.Id, RunId = Guid.NewGuid(), Candidate = 1,
            Width = snapshot.Width, Height = snapshot.Height, Bytes = 5 };
        take.Directory = take.Id.ToString("D");
        var relative = $"shots/takes/{take.Id:D}/refinement.safetensors";
        var raw = await TensorPackage(snapshot); await f.Bytes(project, relative, raw);
        take.RefinementPackage = await RefinementPackages.InspectAsync(Path.Combine(f.Root(project), relative), snapshot, null, TestContext.Current.CancellationToken);
        await f.Bytes(project, $"shots/takes/{take.Id:D}/video.mp4", [1,2,3,4,5]);
        await f.Save(project, "shots.json", new ShotDocument { ProjectId = project.Id, Shots = [snapshot.Shot], Takes = [take] });
        var export = await f.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken); var entries = await f.Unzip(project, export);
        var sanitized = entries["project/" + relative];
        static byte[] Payload(byte[] data) => data[(8 + (int)BinaryPrimitives.ReadUInt64LittleEndian(data))..];
        Assert.Equal(Payload(raw), Payload(sanitized));
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(sanitized));
        Assert.Equal(raw, await File.ReadAllBytesAsync(Path.Combine(f.Root(project), relative), TestContext.Current.CancellationToken));
        using var target = new Fixture(); await using var zip = (await f.Service.OpenExportAsync(project.Id, export.Id, TestContext.Current.CancellationToken))!;
        var pending = await target.Service.StageImportAsync(zip.Content, ct: TestContext.Current.CancellationToken); await target.Service.CommitImportAsync(pending.Token, TestContext.Current.CancellationToken);
        var doc = await new FileShotStore(target.Files, TimeProvider.System).LoadAsync(project.Id, TestContext.Current.CancellationToken);
        var imported = Assert.Single(doc.Takes);
        Assert.Equal(5 + imported.RefinementPackage!.Bytes, imported.Bytes);
        Assert.Equal(imported.RefinementPackage, await RefinementPackages.InspectAsync(Path.Combine(target.Root(project), relative), imported.Snapshot, imported.Refinement, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task PackageNeverCopiesGlobalCredentialsRunFoldersOrUnregisteredLooseImages()
    {
        using var f = new Fixture(); var p = await f.Create();
        await f.Bytes(p, "shots/runs/private/request.json", Encoding.UTF8.GetBytes(Secret));
        await f.Bytes(p, "refmod-builds/private/input.json", Encoding.UTF8.GetBytes(Secret));
        await f.Bytes(p, "loose.png", Encoding.UTF8.GetBytes(Secret));
        await File.WriteAllTextAsync(f.Paths.Settings, Secret, TestContext.Current.CancellationToken);
        var export = await f.Service.ExportAsync(p.Id, new(), ct: TestContext.Current.CancellationToken); var entries = await f.Unzip(p, export);
        Assert.DoesNotContain(entries.Values, b => Encoding.UTF8.GetString(b).Contains(Secret));
        Assert.DoesNotContain(entries.Keys, k => k.Contains("runs") || k.Contains("builds") || k.Contains("settings"));
    }
    [Fact]
    public async Task PackageCollisionAndPostInspectionTamperingDoNotPublishOrOverwrite()
    {
        using var f = new Fixture(); var p = await f.Create(); var export = await f.Service.ExportAsync(p.Id, new(), ct: TestContext.Current.CancellationToken);
        await using var own = (await f.Service.OpenExportAsync(p.Id, export.Id, TestContext.Current.CancellationToken))!;
        var duplicate = await f.Service.StageImportAsync(own.Content, ct: TestContext.Current.CancellationToken); Assert.True(duplicate.AlreadyExists);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.CommitImportAsync(duplicate.Token, TestContext.Current.CancellationToken));
        Assert.Equal(p, await f.Projects.GetAsync(p.Id, TestContext.Current.CancellationToken));
        using var target = new Fixture(); await using var transfer = (await f.Service.OpenExportAsync(p.Id, export.Id, TestContext.Current.CancellationToken))!;
        var staged = await target.Service.StageImportAsync(transfer.Content, ct: TestContext.Current.CancellationToken);
        var root = Path.Combine(target.Paths.Projects, ".importing-" + staged.Token.ToString("D"), p.Id.ToString("D"));
        await File.AppendAllTextAsync(Path.Combine(root, "project.json"), "tampered", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => target.Service.CommitImportAsync(staged.Token, TestContext.Current.CancellationToken));
        Assert.Empty((await target.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects);
    }
    [Theory]
    [InlineData("../outside")][InlineData("/absolute")][InlineData("C:/escape")][InlineData("a\\b")]
    [InlineData("a/../b")][InlineData("a//b")][InlineData("a/NUL.txt")][InlineData("a/name.")]
    [InlineData("a/%2e%2e/x")][InlineData("a/file:stream")]
    public void PackagePathsRejectTraversalAliasesAndDeviceNames(string name) => Assert.Throws<WorkspaceStoreException>(() => ProjectPackageFormat.Relative(name));
    [Theory]
    [InlineData("checksum")][InlineData("duplicate")][InlineData("extra")][InlineData("version")][InlineData("symlink")][InlineData("unreferenced")]
    public async Task PackageRejectsInvalidArchivesBeforePublication(string damage)
    {
        using var f = new Fixture(); var project = await f.Create(); var package = await f.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken);
        var entries = await f.Unzip(project, package);
        if (damage == "checksum") entries["project/project.json"] = Encoding.UTF8.GetBytes("{}");
        if (damage == "extra") entries["project/unrelated.txt"] = Encoding.UTF8.GetBytes(Secret);
        if (damage == "version") { var manifest = JsonNode.Parse(entries["manifest.json"])!; manifest["version"] = 999; entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()); }
        if (damage == "unreferenced") {
            var path = $"assets/{Guid.NewGuid():D}/images/{Guid.NewGuid():N}.png";
            entries["project/" + path] = Pixel;
            var manifest = ProjectPackageFormat.Parse<ProjectPackageManifest>(entries["manifest.json"]);
            entries["manifest.json"] = ProjectPackageFormat.Json(manifest with { Files = [..manifest.Files, new(path, Pixel.Length, Convert.ToHexString(SHA256.HashData(Pixel)))] });
        }
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)) {
            foreach (var (name, data) in entries) { var e = zip.CreateEntry(name); if (damage == "symlink" && name == "project/project.json") e.ExternalAttributes = unchecked((int)0xa1ff0000); using var s = e.Open(); s.Write(data); }
            if (damage == "duplicate") { using var s = zip.CreateEntry("project/project.json").Open(); s.Write(entries["project/project.json"]); }
        }
        buffer.Position = 0; using var target = new Fixture();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => target.Service.StageImportAsync(buffer, ct: TestContext.Current.CancellationToken));
        Assert.Empty((await target.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects);
        Assert.Empty(Directory.EnumerateDirectories(target.Paths.Projects, ".importing-*"));
    }
    [Fact]
    public async Task PackageDownloadUsesExistingResourceRangeSemanticsAndChecksProjectIdentity()
    {
        using var f = new Fixture(); var p = await f.Create(); var export = await f.Service.ExportAsync(p.Id, new(), ct: TestContext.Current.CancellationToken);
        Assert.Null(await f.Service.OpenExportAsync(Guid.NewGuid(), export.Id, TestContext.Current.CancellationToken));
        var resources = new MediaResources(null!, null!, null!, null!, null!, projectPackages: f.Service);
        await using var range = await resources.GetAsync(export.Url, "GET", new Dictionary<string,string> { ["Range"] = "bytes=0-3" }, TestContext.Current.CancellationToken);
        Assert.Equal(206, range.Status); Assert.Equal("application/zip", range.Headers["Content-Type"]);
        using var body = new MemoryStream(); await range.Content.CopyToAsync(body, TestContext.Current.CancellationToken); Assert.Equal(new byte[] { 80,75,3,4 }, body.ToArray());
    }
    [Fact]
    public async Task PackageCancelledImportLeavesNoPublishedProject()
    {
        using var f = new Fixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.StageImportAsync(new MemoryStream([1,2,3]), ct: cancelled.Token));
        Assert.Empty((await f.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects);
    }
    [Fact]
    public void PackageZeroStrengthEditRemovesFilenameWithoutWeakeningAppliedLoraValidation()
    {
        var project = Guid.NewGuid(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Asset" };
        var image = Image() with { Origin = AssetImageOrigin.Edited, Generation = new() { Workflow = ImageWorkflow.Krea2,
            Edit = new() { SourceAssetId = asset.Id, SourceImageId = Guid.NewGuid(), Lora = Secret, LoraStrength = 0 } } };
        var library = new AssetLibrary { ProjectId = project, Assets = [asset with { Images = [image] }] };
        var cleaned = new ProjectPackagePrivacy().Clean(library);
        Assert.Equal("", cleaned.Assets[0].Images[0].Generation!.Edit!.Lora);
        FileAssetStore.Validate(cleaned, project);
        var invalid = cleaned.Assets[0].Images[0];
        invalid = invalid with { Generation = invalid.Generation! with { Edit = invalid.Generation.Edit! with { LoraStrength = 1 } } };
        Assert.Throws<WorkspaceStoreException>(() => FileAssetStore.Validate(cleaned with { Assets = [asset with { Images = [invalid] }] }, project));
    }
    [Fact]
    public async Task PackageTransfersLocalScriptDiscussionWithoutExecutableJobLinks()
    {
        using var f = new Fixture(); var project = await f.Create();
        var run = new AssistantRun { JobId = Guid.NewGuid(), Output = "Saved discussion", Status = AssistantRunStatus.Completed,
            Revision = 2, Applied = true, Model = "test/model" };
        var running = new AssistantRun { Status = AssistantRunStatus.Running, Output = "Partial text", Model = "test/model" };
        await f.Save(project, "script-assistant.json", new AssistantHistory { ProjectId = project.Id, Runs = [run, running] });
        var before = await File.ReadAllBytesAsync(Path.Combine(f.Root(project), "script-assistant.json"), TestContext.Current.CancellationToken);
        var export = await f.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken); var entries = await f.Unzip(project, export);
        var history = ProjectPackageFormat.Parse<AssistantHistory>(entries["project/script-assistant.json"]);
        Assert.Equal(run.Id, history.Runs[0].Id); Assert.Null(history.Runs[0].JobId); Assert.True(history.Runs[0].Applied);
        Assert.Equal(run.Output, history.Runs[0].Output); Assert.Equal(AssistantRunStatus.Interrupted, history.Runs[1].Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(f.Root(project), "script-assistant.json"), TestContext.Current.CancellationToken));
        using var target = new Fixture(); await using var zip = (await f.Service.OpenExportAsync(project.Id, export.Id, TestContext.Current.CancellationToken))!;
        var stage = await target.Service.StageImportAsync(zip.Content, ct: TestContext.Current.CancellationToken); await target.Service.CommitImportAsync(stage.Token, TestContext.Current.CancellationToken);
        var saved = await new FileAssistantHistoryStore(target.Files, new ApplicationSession()).LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(history, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(saved, AtomicJsonFile.Options)));
        Assert.False(Directory.Exists(target.Paths.Jobs));
    }
    [Fact]
    public void PackageHistoryPreservesReviewedDecisionsButNotStaleResponseBodies()
    {
        var id = Guid.NewGuid();
        var captured = new AssistantRun { Id = id, JobId = id, Status = AssistantRunStatus.Completed, Output = "Final response" };
        var reviewed = captured with { Applied = true, Revision = 4, Output = "Stale cache" };
        var merged = ProjectPackageHistory.Merge(captured, reviewed);
        Assert.True(merged.Applied); Assert.Equal(4, merged.Revision); Assert.Equal("Final response", merged.Output);
        Assert.Throws<WorkspaceStoreException>(() => ProjectPackageHistory.Merge(captured, reviewed with { JobId = Guid.NewGuid() }));
        Assert.Equal(captured, ProjectPackageHistory.Merge(captured, null));
    }
    [Theory]
    [InlineData("{\"name\":1,\"name\":2}")][InlineData("{\"Name\":1,\"name\":2}")]
    public void PackageRejectsDuplicateJsonProperties(string json) =>
        Assert.Throws<WorkspaceStoreException>(() => ProjectPackageFormat.StrictJson(Encoding.UTF8.GetBytes(json)));
    [Fact]
    public async Task PackageAcceptsZip64IndexWithoutNeedingMultiGigabyteTestMedia()
    {
        using var f = new Fixture(); var project = await f.Create(); var export = await f.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken);
        await using var source = (await f.Service.OpenExportAsync(project.Id, export.Id, TestContext.Current.CancellationToken))!;
        using var buffer = new MemoryStream(); await source.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken); var bytes = buffer.ToArray();
        var end = bytes.Length - 22; Assert.Equal(0x06054b50U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 12));
        var start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        var record = new byte[56]; BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), count); BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), count);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), size); BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), start);
        var locator = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)end); BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8), ushort.MaxValue); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), uint.MaxValue); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), uint.MaxValue);
        using var zip64 = new MemoryStream(); zip64.Write(bytes.AsSpan(0, end)); zip64.Write(record); zip64.Write(locator); zip64.Write(bytes.AsSpan(end)); zip64.Position = 0;
        using var target = new Fixture(); var staged = await target.Service.StageImportAsync(zip64, ct: TestContext.Current.CancellationToken);
        Assert.Equal(project.Id, staged.Project.Id); Assert.Empty((await target.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects);
        await target.Service.DiscardImportAsync(staged.Token, TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task PackageCancellationDuringReceivingRemovesUnpublishedFiles()
    {
        using var target = new Fixture(); using var cancelled = new CancellationTokenSource();
        using var content = new CancelOnRead(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.Service.StageImportAsync(content, ct: cancelled.Token));
        Assert.Empty((await target.Projects.ListAsync(TestContext.Current.CancellationToken)).Projects);
        Assert.Empty(Directory.EnumerateDirectories(target.Paths.Projects, ".importing-*"));
    }
    [Fact]
    public async Task PackageCanLeaveOutLosslessArchivesAndStillOpenTakesAndReelKeyframes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new Fixture(); var project = await f.Create(); var tools = new FrameTools();
        var reels = new FileReferenceVideoStore(f.Files, new FileShotStore(f.Files, TimeProvider.System), tools);
        ReferenceVideoMedia media;
        await using (var input = new MemoryStream([1, 2, 3, 4])) media = await reels.ImportAsync(project.Id, input, "clip.mp4", new(), ct);
        var archiveSource = Path.Combine(f.Paths.Temporary, "archive-source"); Directory.CreateDirectory(archiveSource);
        var reelFrames = await MockFrameArchive.WriteAsync(archiveSource, media.Frames, media.Width, media.Height, ct);
        var reelOutput = new ShotTake { Snapshot = Snapshot(project.Id, "standard") with { FrameCount = media.Frames, OutputPolicy = new(true) },
            Width = media.Width, Height = media.Height, Frames = reelFrames };
        await reels.PublishArchiveAsync(project.Id, media, reelOutput, archiveSource, ct);
        var catalog = await reels.FrameCatalogAsync(project.Id, media, new(), ct); Assert.True(catalog.Lossless);
        ReelFrameIdentity Pick(int i) => new(media.Id, catalog.Source, i, catalog.Timestamps[i]);
        // Frame 5 was already prepared in the project; frame 30 must be extracted during export.
        await reels.PrepareFramesAsync(project.Id, [Pick(5)], new(), ct);
        var assets = new FileAssetStore(f.Files, TimeProvider.System);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await assets.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, ct);
        library = await assets.SaveReelAsync(project.Id, new AssetReferenceReel { AssetId = owner.Id, Media = media }, library.Revision, ct);
        await assets.SaveKeyframesAsync(project.Id, library.Reels.Single(),
            new() { Frames = [new() { Frame = Pick(5), Notes = "Doorway" }, new() { Frame = Pick(30), Notes = "Window" }] }, library.Revision, ct);
        var snapshot = Snapshot(project.Id, "standard", lossless: true);
        var take = new ShotTake { Snapshot = snapshot, ShotId = snapshot.Shot.Id, RunId = Guid.NewGuid(), Candidate = 1, Width = snapshot.Width, Height = snapshot.Height,
            Frames = Enumerable.Range(0, snapshot.FrameCount).Select(i => new ShotFrame(i, LosslessFrameArchive.FileName(i / 24), 7) { ArchiveFrameIndex = i % 24 }).ToList() };
        var segments = take.Frames.Select(x => x.FileName).Distinct().ToArray();
        take.Directory = take.Id.ToString("D"); take.Bytes = 5 + 7 * segments.Length;
        await f.Bytes(project, $"shots/takes/{take.Id:D}/video.mp4", [1, 2, 3, 4, 5]);
        foreach (var file in segments) await f.Bytes(project, $"shots/takes/{take.Id:D}/{file}", new byte[7]);
        await f.Save(project, "shots.json", new ShotDocument { ProjectId = project.Id, Shots = [snapshot.Shot], Takes = [take] });
        Assert.False(File.Exists(Path.Combine(f.Root(project), "reference-videos", media.Id.ToString("D"), FileReferenceVideoStore.FrameFileName(Pick(30)))));
        var before = Tree(f.Root(project));

        var export = await f.Service.ExportAsync(project.Id, new(LeaveOutLosslessArchives: true), ct: ct);
        var entries = await f.Unzip(project, export); var reelRoot = $"project/reference-videos/{media.Id:D}/";
        Assert.DoesNotContain(entries.Keys, k => k.Contains("/lossless/") || k.Contains("archive-"));
        Assert.Contains(reelRoot + "frame-archive.json", entries.Keys);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(f.Root(project), "reference-videos", media.Id.ToString("D"), FileReferenceVideoStore.FrameFileName(Pick(5))), ct),
            entries[reelRoot + FileReferenceVideoStore.FrameFileName(Pick(5))]);
        using (var extracted = Picture.Load<Rgb24>(entries[reelRoot + FileReferenceVideoStore.FrameFileName(Pick(30))])) Assert.Equal(new Rgb24(30, 0, 200), extracted[0, 0]);
        var saved = Assert.Single(ProjectPackageFormat.Parse<ShotDocument>(entries["project/shots.json"]).Takes);
        Assert.Empty(saved.Frames); Assert.False(saved.HasLosslessFrames); Assert.Equal(5, saved.Bytes);
        Assert.Equal(segments, saved.FrameArchiveRemoval!.Files.Select(x => x.FileName)); Assert.NotNull(saved.FrameArchiveRemoval.CompletedUtc);
        var manifest = ProjectPackageFormat.Parse<ProjectPackageManifest>(entries["manifest.json"]);
        Assert.True(manifest.LeftOutLosslessArchives); Assert.False(manifest.CompressedImages);
        Assert.Equal(segments.Length + reelFrames.Select(x => x.FileName).Distinct().Count(), manifest.LeftOutLosslessFiles);
        Assert.Equal(before, Tree(f.Root(project)));

        using var target = new Fixture();
        await using (var zip = (await f.Service.OpenExportAsync(project.Id, export.Id, ct))!) {
            var staged = await target.Service.StageImportAsync(zip.Content, ct: ct); await target.Service.CommitImportAsync(staged.Token, ct);
        }
        Assert.False(Assert.Single((await new FileShotStore(target.Files, TimeProvider.System).LoadAsync(project.Id, ct)).Takes).HasLosslessFrames);
        var imported = new FileReferenceVideoStore(target.Files, new FileShotStore(target.Files, TimeProvider.System), tools);
        // Saved keyframes open from their pictures; new picks come from the video.
        await using (var keyframe = await imported.OpenFrameAsync(project.Id, Pick(30), new(), ct)) {
            using var image = await Picture.LoadAsync<Rgb24>(keyframe.Content, ct); Assert.Equal(new Rgb24(30, 0, 200), image[0, 0]);
        }
        var video = await imported.FrameCatalogAsync(project.Id, media, new(), ct);
        Assert.False(video.Lossless); Assert.Equal(media.Sha256, video.Source);
        await using (var pick = await imported.OpenFrameAsync(project.Id, new(media.Id, video.Source, 12, video.Timestamps[12]), new(), ct)) Assert.True(pick.Content.Length > 0);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => imported.OpenFrameAsync(project.Id, Pick(40), new(), ct));
        Assert.Contains("not in this project", error.Message);
        // Exporting the compact project again keeps the keyframe pictures without asking for the option.
        var again = await target.Service.ExportAsync(project.Id, new(), ct: ct);
        Assert.Contains($"reference-videos/{media.Id:D}/{FileReferenceVideoStore.FrameFileName(Pick(30))}", again.Manifest.Files.Select(x => x.Path));
    }
    [Fact]
    public async Task PackageCanCompressImagesAndRewritesEveryFileDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new Fixture(); var project = await f.Create();
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Character" };
        // The crop records its trashed source's size, so the source keeps it; the free image is reduced.
        var source = Image() with { Width = 600, Height = 400 }; var free = Image() with { Width = 600, Height = 400 };
        var cropped = Image() with { Width = 300, Height = 200, Origin = AssetImageOrigin.Cropped,
            Source = new(Crop: new(project.Id, asset.Id, source.Id, new() { Width = .5, Height = .5 }, 600, 400)) };
        var id = Guid.NewGuid(); var photo = Image() with { Id = id, FileName = id.ToString("N") + ".jpeg", ContentType = "image/jpeg", Width = 600, Height = 400 };
        var deleted = DateTimeOffset.UtcNow.AddDays(-1);
        await f.Save(project, "assets.json", new AssetLibrary { ProjectId = project.Id, Assets = [asset with { Images = [free, cropped, photo] }],
            Trash = [new() { Image = source, Asset = asset, DeletedUtc = deleted, ExpiresUtc = deleted.AddDays(30) }] });
        foreach (var image in new[] { source, free, cropped, photo })
            await f.Bytes(project, $"assets/{asset.Id:D}/images/{image.FileName}", Photo(image.Width, image.Height, image == photo));
        var before = Tree(f.Root(project));

        var export = await f.Service.ExportAsync(project.Id, new(CompressImages: true, MaxImageDimension: 300), ct: ct);
        var entries = await f.Unzip(project, export);
        var library = ProjectPackageFormat.Parse<AssetLibrary>(entries["project/assets.json"]);
        void Check(AssetImage original, string type, string extension, int width, int height)
        {
            var image = library.Assets.SelectMany(a => a.Images).Concat(library.Trash.Select(t => t.Image)).Single(i => i.Id == original.Id);
            Assert.Equal((Path.GetFileNameWithoutExtension(original.FileName) + extension, type, width, height), (image.FileName, image.ContentType, image.Width, image.Height));
            var path = $"assets/{asset.Id:D}/images/{image.FileName}"; var bytes = entries["project/" + path];
            var info = ImageInspector.Inspect(bytes);
            Assert.Equal((type, extension, width, height), (info.ContentType, info.Extension, info.Width, info.Height));
            var listed = Assert.Single(export.Manifest.Files, x => x.Path == path);
            Assert.Equal((bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))), (listed.Bytes, listed.Sha256));
            Assert.DoesNotContain($"project/assets/{asset.Id:D}/images/{original.FileName}", entries.Keys);
        }
        Check(source, "image/webp", ".webp", 600, 400);
        Check(free, "image/webp", ".webp", 300, 200);
        Check(cropped, "image/webp", ".webp", 300, 200);
        Check(photo, "image/jpeg", ".jpg", 300, 200);
        Assert.Equal(cropped.Source, library.Assets[0].Images.Single(i => i.Id == cropped.Id).Source);
        var m = export.Manifest;
        Assert.Equal((true, 85, 300, 4, 2, false), (m.CompressedImages, m.ImageQuality!.Value, m.MaxImageDimension!.Value, m.RecompressedImages, m.ResizedImages, m.LeftOutLosslessArchives));
        Assert.Equal(before, Tree(f.Root(project)));

        using var target = new Fixture();
        await using (var zip = (await f.Service.OpenExportAsync(project.Id, export.Id, ct))!) {
            var staged = await target.Service.StageImportAsync(zip.Content, ct: ct); await target.Service.CommitImportAsync(staged.Token, ct);
        }
        var store = new FileAssetStore(target.Files, TimeProvider.System); var imported = await store.LoadAsync(project.Id, ct);
        var reduced = imported.Assets[0].Images.Single(i => i.Id == free.Id);
        Assert.Equal((300, 200), (reduced.Width, reduced.Height));
        Assert.Equal(source.Id, Assert.Single(imported.Trash).Image.Id);
        await using var opened = await store.OpenImageAsync(project.Id, asset.Id, free.Id, ct);
        Assert.Equal("image/webp", opened!.ContentType);
    }
    [Fact]
    public async Task PackageRejectsCompactionFieldsThatDisagreeWithTheOptions()
    {
        using var f = new Fixture(); var project = await f.Create(); var package = await f.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken);
        var entries = await f.Unzip(project, package);
        var manifest = ProjectPackageFormat.Parse<ProjectPackageManifest>(entries["manifest.json"]);
        entries["manifest.json"] = ProjectPackageFormat.Json(manifest with { RecompressedImages = 3 });
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
        buffer.Position = 0; using var target = new Fixture();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => target.Service.StageImportAsync(buffer, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Service.ExportAsync(project.Id, new(CompressImages: true, MaxImageDimension: 16), ct: TestContext.Current.CancellationToken));
    }
    private static Dictionary<string, string> Tree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    // A noisy gradient: large as PNG, small as lossy WebP.
    internal static byte[] Photo(int width, int height, bool jpeg = false)
    {
        var random = new Random(width * 31 + height);
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(rows => { for (var y = 0; y < rows.Height; y++) { var row = rows.GetRowSpan(y);
            for (var x = 0; x < row.Length; x++) row[x] = new((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)random.Next(96, 160)); } });
        using var output = new MemoryStream();
        if (jpeg) image.SaveAsJpeg(output); else image.SaveAsPng(output);
        return output.ToArray();
    }
    private sealed class FrameTools : IProductionMediaTools
    {
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult(new VideoFileInfo(32, 32, 48, 24, true));
        public Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<double>> ReelFrameTimesAsync(string source, H3Settings settings, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<double>>(Enumerable.Range(0, 48).Select(i => i / 24d).ToArray());
        public async Task ExtractReelFramesAsync(string source, IReadOnlyList<int> indices, string directory, int maximumEdge, H3Settings settings, CancellationToken ct)
        {
            for (var i = 0; i < indices.Count; i++) { using var image = new Image<Rgb24>(32, 32); await image.SaveAsPngAsync(Path.Combine(directory, $"{i:D6}.png"), ct); }
        }
    }
    private sealed class CancelOnRead(CancellationTokenSource cancellation) : MemoryStream(new byte[32])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var result = await base.ReadAsync(buffer, ct); cancellation.Cancel(); return result; }
    }
    private static AssetImage Image() { var id = Guid.NewGuid(); return new() { Id = id, FileName = id.ToString("N") + ".png", ContentType = "image/png", Width = 1, Height = 1, CreatedUtc = DateTimeOffset.UtcNow }; }
    private static Shot Ready() => new() { Title = "Shot", Description = "A room", Duration = 1, ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid() };
    private static VideoSnapshot Snapshot(Guid project, string key, bool lossless = false)
    {
        var shot = Ready(); shot.GenerationPreset = key; shot.SaveLosslessFrames = lossless; var settings = new H3Settings(); var size = VideoResolutions.Size(shot);
        return new(project, 1, shot, H3Policy.Compile(shot), H3Policy.Fingerprint(shot), "http://localhost:8188", settings, size.Width, size.Height, H3Policy.Frames(1), H3Policy.Profile) {
            Preset = H3Presets.Capture(shot, settings), Sampling = H3Policy.Sampling(shot, settings), OutputPolicy = new(lossless),
            Performance = H3Performance.Capture(H3Presets.NewPerformance(settings)) };
    }
    private static async Task<byte[]> TensorPackage(VideoSnapshot snapshot)
    {
        long[] video = [1,24,(snapshot.FrameCount - 5) / 17 * 5 + 2,snapshot.Height / 16,snapshot.Width / 16];
        long[] audio = [1,32,2,(long)Math.Round(snapshot.FrameCount / 24d * 40)]; long offset = 0;
        JsonObject Tensor(long[] shape) { var size = shape.Aggregate(2L, (a,b) => a*b); var first = offset; offset += size;
            return new() { ["dtype"] = "F16", ["shape"] = JsonSerializer.SerializeToNode(shape), ["data_offsets"] = new JsonArray(first, offset) }; }
        var root = new JsonObject { ["video"] = Tensor(video), ["audio"] = Tensor(audio) };
        var m = new { version = 2, id = Guid.NewGuid().ToString("D"), fps = 24, width = snapshot.Width, height = snapshot.Height, frameCount = snapshot.FrameCount };
        root["__metadata__"] = new JsonObject { ["lumibelle"] = JsonSerializer.Serialize(m, AtomicJsonFile.Options), ["unused-global-info"] = Secret };
        var header = JsonSerializer.SerializeToUtf8Bytes(root); using var output = new MemoryStream(); var prefix = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)header.Length); output.Write(prefix); output.Write(header); output.Write(new byte[(int)offset]);
        await Task.CompletedTask; return output.ToArray();
    }
    [Fact]
    public async Task TrimmedTakePackageRetainsFullLatentsAndInputsWithoutTheOriginalOrQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = new Fixture(); using var target = new Fixture(); var project = await source.Create();
        var snapshot = Snapshot(project.Id, "standard") with { CaptureRefinementData = true };
        var binding = new ShotImageBinding { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Reference" };
        snapshot.Shot.Images.Add(binding);
        IReadOnlyList<ShotReferenceGuidance> guidance = [new(binding.Id, "", "", null)];
        snapshot = snapshot with { Prompt = H3Policy.Compile(snapshot.Shot, guidance), Fingerprint = H3Policy.Fingerprint(snapshot.Shot), ReferenceGuidance = guidance };
        var packageBytes = await TensorPackage(snapshot); var takeId = Guid.NewGuid();
        await source.Bytes(project, $"shots/takes/{takeId:D}/{H3RefinementPackage.FileName}", packageBytes);
        var package = await RefinementPackages.InspectAsync(Path.Combine(source.Root(project), "shots", "takes", takeId.ToString("D"), H3RefinementPackage.FileName), snapshot, null, ct);
        var input = new AiVideoInput("picture.png", false, Pixel.Length, Convert.ToHexString(SHA256.HashData(Pixel)));
        var take = new ShotTake { Id = takeId, ShotId = snapshot.Shot.Id, RunId = Guid.NewGuid(), Candidate = 1, Directory = takeId.ToString("D"), Snapshot = snapshot,
            Width = snapshot.Width, Height = snapshot.Height, RefinementPackage = package, RetainedSource = new([input]),
            Trim = new(Guid.NewGuid(), snapshot.FrameCount, 5, 30, 5, 30, false), Bytes = 3 + package.Bytes + Pixel.Length };
        await source.Bytes(project, $"shots/takes/{takeId:D}/video.mp4", [1, 2, 3]);
        await source.Bytes(project, $"shots/takes/{takeId:D}/{TakeTrimming.InputsFolder}/picture.png", Pixel);
        await source.Save(project, "shots.json", new ShotDocument { ProjectId = project.Id, Shots = [snapshot.Shot], Takes = [take] });
        var export = await source.Service.ExportAsync(project.Id, new(), ct: TestContext.Current.CancellationToken);
        await using var bytes = (await source.Service.OpenExportAsync(project.Id, export.Id, ct))!;
        var pending = await target.Service.StageImportAsync(bytes.Content, ct: ct);
        await target.Service.CommitImportAsync(pending.Token, ct);
        var shots = new FileShotStore(target.Files, TimeProvider.System);
        var imported = Assert.Single((await shots.LoadAsync(project.Id, ct)).Takes);
        Assert.Equal(25, imported.FrameCount); Assert.Equal(snapshot.FrameCount, imported.RefinementPackage!.FrameCount);
        Assert.Equal(3 + imported.RefinementPackage.Bytes + Pixel.Length, imported.Bytes); Assert.NotNull(imported.RetainedSource);
        var importedBytes = await File.ReadAllBytesAsync(Path.Combine(target.Root(project), "shots", "takes", takeId.ToString("D"), H3RefinementPackage.FileName), ct);
        Assert.Equal(packageBytes[(8 + (int)BinaryPrimitives.ReadUInt64LittleEndian(packageBytes))..], importedBytes[(8 + (int)BinaryPrimitives.ReadUInt64LittleEndian(importedBytes))..]);
        var runId = Guid.NewGuid();
        var refinement = await shots.CaptureRefinementAsync(project.Id, takeId, runId, TakeRefinementMode.Refine, take.Width, take.Height, "mock.safetensors", H3UpscalerImplementation.Plus, ct);
        lumibelle.Services.AI.AiVideoJobPolicy.Validate(new(2, runId, imported.Snapshot, imported.RetainedSource!.Inputs) { Refinement = refinement, OutputTrim = new(5, 30) });
        Assert.Equal(Pixel, await File.ReadAllBytesAsync(Path.Combine(await shots.RunDirectoryAsync(project.Id, runId, ct), "inputs", input.FileName), ct));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "lumibelle-package-" + Guid.NewGuid().ToString("N"));
        internal ApplicationPaths Paths { get; }
        internal FileProjectStore Projects { get; }
        internal ProjectFiles Files { get; }
        internal SetupStore Setups { get; } = new();
        internal ProjectPackageService Service { get; }
        internal Fixture() {
            Paths = new(directory); Directory.CreateDirectory(Paths.Projects);
            Projects = new(Paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance); Files = new(Paths, Projects);
            Service = new(Paths, Files, Projects, Setups, TimeProvider.System);
        }
        internal Task<ProjectInfo> Create() => Projects.CreateAsync(new("Portable project"));
        internal string Root(ProjectInfo p) => Path.Combine(Paths.Projects, p.Id.ToString("D"));
        internal Task Save<T>(ProjectInfo p, string path, T value) => AtomicJsonFile.WriteAsync(Path.Combine(Root(p), path), value, default);
        internal async Task Bytes(ProjectInfo p, string path, byte[] bytes) { var full = Path.Combine(Root(p), path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); await File.WriteAllBytesAsync(full, bytes); }
        internal async Task<Dictionary<string, byte[]>> Unzip(ProjectInfo p, ProjectPackageExport package) {
            await using var source = (await Service.OpenExportAsync(p.Id, package.Id))!; using var zip = new ZipArchive(source.Content, ZipArchiveMode.Read);
            var result = new Dictionary<string,byte[]>(); foreach (var e in zip.Entries) { using var input = e.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output); result[e.FullName] = output.ToArray(); } return result;
        }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class SetupStore : IGenerationSetupStore
    {
        internal GenerationSetupLibrary Value = new();
        public Task<GenerationSetupLibrary> LoadAsync(CancellationToken ct = default) => Task.FromResult(ShotCopy.Of(Value));
        public Task<GenerationSetupLibrary> ImportAsync(ProductionDocument document, CancellationToken ct = default) => throw new InvalidOperationException("Import must not mutate the global setup store.");
        public Task<GenerationSetupLibrary> SaveAsync(GenerationSetup setup, long expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GenerationSetupLibrary> SelectAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
