using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Lumibelle.AssetTests", Guid.NewGuid().ToString("D"));
    private readonly AssetClock _clock = new();

    private (ProjectInfo Project, FileAssetStore Store) CreateStore(string name = "Assets")
    {
        var environment = new AssetEnvironment { ContentRootPath = _directory };
        var options = Options.Create(new ProjectStorageOptions { RootDirectory = "projects" });
        var projects = new FileProjectStore(options, environment, _clock, NullLogger<FileProjectStore>.Instance);
        var project = projects.CreateAsync(new(name)).GetAwaiter().GetResult();
        return (project, new(new ProjectFiles(options, environment, projects), _clock));
    }

    [Fact]
    public async Task BulkDiscardPublishesOnceAndPreservesSourceAndReferenceMedia()
    {
        var (project, store) = CreateStore(); var asset = Asset("Mira", "");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, TestContext.Current.CancellationToken);
        for (var i = 0; i < 4; i++)
        {
            await using var bytes = new MemoryStream(Png(4 + i, 3));
            saved = await store.AddImageAsync(project.Id, asset.Id, bytes,
                new("image.png", [], i < 2 ? AssetImageOrigin.Imported : AssetImageOrigin.Generated, i < 2 ? null : new()), saved.Revision, TestContext.Current.CancellationToken);
        }
        var inputs = saved.Assets[0].Images.Take(2).ToArray();
        var takes = saved.Assets[0].Images.Skip(2).ToArray();
        var previousRevision = saved.Revision;
        saved = (await store.DeleteImagesAsync(project.Id, asset.Id, takes.Select(i => i.Id).ToArray(), saved.Revision, TestContext.Current.CancellationToken)).Library;
        Assert.Equal(previousRevision + 1, saved.Revision);
        Assert.Equal(inputs.Select(i => i.Id), saved.Assets[0].Images.Select(i => i.Id));
        foreach (var image in inputs)
        { await using var media = await store.OpenImageAsync(project.Id, asset.Id, image.Id, TestContext.Current.CancellationToken); Assert.NotNull(media); }
        foreach (var image in takes)
            Assert.True(File.Exists(Path.Combine(_directory, "projects", project.Id.ToString("D"), "assets", asset.Id.ToString("D"), "images", image.FileName)));
        Assert.Equal(saved.Revision, (await CreateFreshStore().LoadAsync(project.Id, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task BulkDiscardRejectsConflictsMissingDuplicatesAndProtectedImagesBeforeChangingAnything()
    {
        var (project, store) = CreateStore(); var asset = Asset("Mira", "");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            await using var bytes = new MemoryStream(Png(4, 3));
            saved = await store.AddImageAsync(project.Id, asset.Id, bytes, new("image.png", [], i == 0 ? AssetImageOrigin.Imported : AssetImageOrigin.Generated, i == 0 ? null : new()), saved.Revision, TestContext.Current.CancellationToken);
        }
        var imported = saved.Assets[0].Images[0].Id; var take = saved.Assets[0].Images[1].Id; var approved = saved.Assets[0].Images[2].Id;
        saved = await store.SaveAsync(saved with { Assets = [saved.Assets[0] with { Images = saved.Assets[0].Images.Select(i => i.Id == approved ? i with { IsReference = true } : i).ToList() }] }, saved.Revision, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.DeleteImagesAsync(project.Id, asset.Id, [take], saved.Revision - 1, TestContext.Current.CancellationToken));
        foreach (var ids in new Guid[][] { [], [take, take], [take, Guid.NewGuid()], [take, imported], [take, approved] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.DeleteImagesAsync(project.Id, asset.Id, ids, saved.Revision, TestContext.Current.CancellationToken));
        var reopened = await store.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(saved.Revision, reopened.Revision); Assert.Equal(3, reopened.Assets[0].Images.Count);
        foreach (var image in reopened.Assets[0].Images)
        { await using var media = await store.OpenImageAsync(project.Id, asset.Id, image.Id, TestContext.Current.CancellationToken); Assert.NotNull(media); }
    }

    [Fact]
    public async Task BulkDiscardFailedPublicationLeavesManifestAndFilesIntact()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing locks reliably prevent the atomic rename.
        var (project, store) = CreateStore(); var asset = Asset("Mira", "");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [asset] }, 0, TestContext.Current.CancellationToken);
        await using var bytes = new MemoryStream(Png(4, 3));
        saved = await store.AddImageAsync(project.Id, asset.Id, bytes, new("take.png", [], AssetImageOrigin.Generated, new()), saved.Revision, TestContext.Current.CancellationToken);
        var take = saved.Assets[0].Images[0];
        var path = Path.Combine(_directory, "projects", project.Id.ToString("D"), "assets.json");
        await using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(path)!))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.DeleteImagesAsync(project.Id, asset.Id, [take.Id], saved.Revision, TestContext.Current.CancellationToken));
        Assert.Equal(saved.Revision, (await store.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Revision);
        await using var media = await store.OpenImageAsync(project.Id, asset.Id, take.Id, TestContext.Current.CancellationToken); Assert.NotNull(media);
    }

    [Fact]
    public async Task KleinLineageSurvivesStorageAndSourceAssetDeletionWithoutKreaControls()
    {
        var (project, store) = CreateStore(); var person = Asset("Mira", "Person"); var outfit = Asset("Coat", "Blue coat");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [person, outfit] }, 0, TestContext.Current.CancellationToken);
        await using var source = new MemoryStream(Png(3, 2));
        saved = await store.AddImageAsync(project.Id, person.Id, source, new("source.png", [], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken);
        await using var other = new MemoryStream(Png(2, 3));
        saved = await store.AddImageAsync(project.Id, outfit.Id, other, new("coat.png", [], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken);
        var refs = new[] { new AssetImageReference(person.Id, saved.Assets[0].Images[0].Id), new AssetImageReference(outfit.Id, saved.Assets[1].Images[0].Id) };
        var metadata = new AssetGenerationMetadata
        {
            Workflow = ImageWorkflow.Flux2Klein9bKv, Prompt = "Dress image 1 in image 2's coat", Steps = 4,
            Edit = new() { SourceAssetId = refs[0].AssetId, SourceImageId = refs[0].ImageId, References = refs,
                LoraStrength = 0, GroundingPixels = 0, ReferenceBoost = 0, FitMode = "reference-latent" }
        };
        await using var result = new MemoryStream(Png(4, 4));
        saved = await store.AddImageAsync(project.Id, person.Id, result, new("klein.png", [], AssetImageOrigin.Edited, metadata), saved.Revision, TestContext.Current.CancellationToken);
        var edited = saved.Assets[0].Images[^1];
        await store.DeleteAssetAsync(project.Id, outfit.Id, saved.Revision, TestContext.Current.CancellationToken);
        var reopened = await CreateFreshStore().LoadAsync(project.Id, TestContext.Current.CancellationToken);
        var kept = reopened.Assets[0].Images.Single(image => image.Id == edited.Id);
        Assert.Equal(ImageWorkflow.Flux2Klein9bKv, kept.Generation!.Workflow);
        Assert.Equal(refs, kept.Generation.Edit!.References); Assert.Empty(kept.Generation.Edit.Lora);
        var invalid = reopened with { Assets = [reopened.Assets[0] with { Images = [kept with
            { Generation = metadata with { Edit = metadata.Edit with { References = [refs[0], refs[0]] } } }] }] };
        Assert.Throws<WorkspaceStoreException>(() => FileAssetStore.Validate(invalid, project.Id));
        invalid = reopened with { Assets = [reopened.Assets[0] with { Images = [kept with
            { Generation = metadata with { Workflow = ImageWorkflow.Krea2 } }] }] };
        Assert.Throws<WorkspaceStoreException>(() => FileAssetStore.Validate(invalid, project.Id));
    }

    [Fact]
    public async Task OldProjectStartsEmptyAndUnicodeAssetOrderSurvivesFreshStore()
    {
        var (project, store) = CreateStore();
        var empty = await store.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Empty(empty.Assets); Assert.Equal(0, empty.Revision);
        var first = Asset("  Hero  ", "  明るい coat 🌲\n");
        var second = Asset("Cabin", "Night place") with { Category = AssetCategory.Environment };
        var saved = await store.SaveAsync(empty with { Assets = [first, second] }, 0, TestContext.Current.CancellationToken);
        var fresh = CreateFreshStore();
        var reopened = await fresh.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "Hero", "Cabin" }, reopened.Assets.Select(asset => asset.Name));
        Assert.Equal("  明るい coat 🌲\n", reopened.Assets[0].Description);
        Assert.Equal(saved.Revision, reopened.Revision);
    }

    [Fact]
    public async Task ConflictsAndMalformedManifestAreSurfacedWithoutReplacement()
    {
        var (project, store) = CreateStore();
        var first = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("One")] }, 0, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.SaveAsync(first with { Assets = [Asset("Two")] }, 0, TestContext.Current.CancellationToken));
        var directory = Path.Combine(_directory, "projects", project.Id.ToString("D"));
        TestProjectDatabase.Corrupt(directory, "assets");
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.LoadAsync(project.Id, TestContext.Current.CancellationToken));
        Assert.Equal("invalid", TestProjectDatabase.Header(directory, "assets"));
    }

    [Fact]
    public async Task ImageImportIsValidatedStoredAndReopenedWithMetadata()
    {
        var (project, store) = CreateStore();
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Mira")] }, 0, TestContext.Current.CancellationToken);
        await using var input = new MemoryStream(Png(3, 2));
        saved = await store.AddImageAsync(project.Id, saved.Assets[0].Id, input,
            new("outside/path.png", [" face ", "Face", "outfit: blue coat"], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken);
        var image = Assert.Single(saved.Assets[0].Images);
        Assert.Equal((3, 2), (image.Width, image.Height)); Assert.Equal("image/png", image.ContentType);
        Assert.Equal(new[] { "face", "outfit: blue coat" }, image.Tags);
        Assert.DoesNotContain("outside", image.FileName);
        await using var media = await store.OpenImageAsync(project.Id, saved.Assets[0].Id, image.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(media); Assert.Equal(Png(3, 2), await Bytes(media!.Content));
        Assert.Single((await CreateFreshStore().LoadAsync(project.Id, TestContext.Current.CancellationToken)).Assets[0].Images);
    }

    [Fact]
    public async Task CoverImpliesReferenceAndTrashHidesActiveMedia()
    {
        var (project, store) = CreateStore();
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Mira")] }, 0, TestContext.Current.CancellationToken);
        await using var input = new MemoryStream(Png(1, 1));
        saved = await store.AddImageAsync(project.Id, saved.Assets[0].Id, input, new("m.png", [], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken);
        var image = saved.Assets[0].Images[0];
        saved = await store.SaveAsync(saved with { Assets = [saved.Assets[0] with { Images = [image with { IsCover = true }] }] }, saved.Revision, TestContext.Current.CancellationToken);
        Assert.True(saved.Assets[0].Images[0].IsReference);
        saved = (await store.DeleteImageAsync(project.Id, saved.Assets[0].Id, image.Id, saved.Revision, TestContext.Current.CancellationToken)).Library;
        Assert.Null(await store.OpenImageAsync(project.Id, saved.Assets[0].Id, image.Id, TestContext.Current.CancellationToken));
        saved = await store.DeleteAssetAsync(project.Id, saved.Assets[0].Id, saved.Revision, TestContext.Current.CancellationToken);
        Assert.Empty(saved.Assets);
    }

    [Fact]
    public async Task EditedImageMetadataSurvivesReopenAndSourceDeletion()
    {
        var (project, store) = CreateStore();
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Mira")] }, 0,
            TestContext.Current.CancellationToken);
        await using var sourceBytes = new MemoryStream(Png(3, 5));
        saved = await store.AddImageAsync(project.Id, saved.Assets[0].Id, sourceBytes,
            new("source.png", ["face"], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken);
        var source = saved.Assets[0].Images[0];
        var generation = new AssetGenerationMetadata
        {
            Prompt = "Change the coat to blue", Seed = 123, AspectRatio = "16:9",
            DiffusionModel = "krea2_turbo_int8_convrot.safetensors", TextEncoder = "qwen3vl_4b_fp8_scaled.safetensors",
            Vae = "qwen_image_vae.safetensors", Steps = 10,
            Edit = new()
            {
                SourceAssetId = saved.Assets[0].Id, SourceImageId = source.Id,
                Lora = "krea2_identity_edit_v1_2.safetensors", LoraStrength = 1,
                ReferenceBoost = 4.5f, GroundingPixels = 768, FitMode = "fit",
                SourceCrop = new() { X = .125, Y = .2, Width = .75, Height = .6 }
            }
        };
        await using var editedBytes = new MemoryStream(Png(7, 4));
        saved = await store.AddImageAsync(project.Id, saved.Assets[0].Id, editedBytes,
            new("edited.png", ["face", "outfit: blue coat"], AssetImageOrigin.Edited, generation), saved.Revision,
            TestContext.Current.CancellationToken);
        var edited = saved.Assets[0].Images[^1];

        Assert.False(edited.IsReference); Assert.False(edited.IsCover);
        saved = (await store.DeleteImageAsync(project.Id, saved.Assets[0].Id, source.Id, saved.Revision,
            TestContext.Current.CancellationToken)).Library;
        var reopened = await CreateFreshStore().LoadAsync(project.Id, TestContext.Current.CancellationToken);
        var retained = Assert.Single(reopened.Assets[0].Images);
        Assert.Equal(AssetImageOrigin.Edited, retained.Origin);
        Assert.Equal(source.Id, retained.Generation!.Edit!.SourceImageId);
        Assert.Equal(4.5f, retained.Generation.Edit.ReferenceBoost);
        Assert.Equal(.125, retained.Generation.Edit.SourceCrop!.X);
        Assert.Equal(.6, retained.Generation.Edit.SourceCrop.Height);
        Assert.Null(await store.OpenImageAsync(project.Id, saved.Assets[0].Id, source.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidOrOversizedImageNeverPublishesMetadata()
    {
        var (project, store) = CreateStore();
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Mira")] }, 0, TestContext.Current.CancellationToken);
        await using var invalid = new MemoryStream("not an image"u8.ToArray());
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AddImageAsync(project.Id, saved.Assets[0].Id, invalid, new("x.png", [], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken));
        Assert.Empty((await store.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Assets[0].Images);
    }

    [Fact]
    public async Task InterruptedTemporaryAndOrphanImageFilesAreIgnored()
    {
        var (project, store) = CreateStore();
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Mira")] }, 0, TestContext.Current.CancellationToken);
        var projectDirectory = Path.Combine(_directory, "projects", project.Id.ToString("D"));
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "assets.json.interrupted.tmp"), "{broken", TestContext.Current.CancellationToken);
        var imageDirectory = Path.Combine(projectDirectory, "assets", saved.Assets[0].Id.ToString("D"), "images");
        Directory.CreateDirectory(imageDirectory); await File.WriteAllBytesAsync(Path.Combine(imageDirectory, "orphan.png"), Png(1, 1), TestContext.Current.CancellationToken);
        var reopened = await store.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        Assert.Empty(reopened.Assets[0].Images); Assert.Equal(saved.Revision, reopened.Revision);
    }

    [Fact]
    public void InspectorRecognizesPngJpegAndWebpHeaders()
    {
        Assert.Equal((3, 2), (ImageInspector.Inspect(Png(3, 2)).Width, ImageInspector.Inspect(Png(3, 2)).Height));
        Assert.Equal("image/jpeg", ImageInspector.Inspect(Jpeg(7, 5)).ContentType);
        Assert.Equal((11, 9), (ImageInspector.Inspect(WebP(11, 9)).Width, ImageInspector.Inspect(WebP(11, 9)).Height));
    }

    [Fact]
    public void InspectorReportsDimensionsAfterExifOrientation()
    {
        using var image = new Image<Rgba32>(40, 20);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);

        var info = ImageInspector.Inspect(stream.ToArray());

        Assert.Equal((20, 40), (info.Width, info.Height));
    }

    [Fact]
    public void InspectorRejectsTruncatedImageWithPlausibleHeader()
    {
        var truncated = new byte[33];
        new byte[] { 137,80,78,71,13,10,26,10 }.CopyTo(truncated, 0);
        truncated[11] = 13; "IHDR"u8.CopyTo(truncated.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(truncated.AsSpan(16,4), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(truncated.AsSpan(20,4), 2);

        Assert.Throws<WorkspaceStoreException>(() => ImageInspector.Inspect(truncated));
    }

    private FileAssetStore CreateFreshStore()
    {
        var environment = new AssetEnvironment { ContentRootPath = _directory };
        var options = Options.Create(new ProjectStorageOptions { RootDirectory = "projects" });
        var projects = new FileProjectStore(options, environment, _clock, NullLogger<FileProjectStore>.Instance);
        return new(new ProjectFiles(options, environment, projects), _clock);
    }
    private ReferenceAsset Asset(string name, string description = "") => new() { Id = Guid.NewGuid(), Category = AssetCategory.Character, Name = name, Description = description, CreatedUtc = _clock.Now, UpdatedUtc = _clock.Now };
    private static async Task<byte[]> Bytes(Stream stream) { await using var result = new MemoryStream(); await stream.CopyToAsync(result, TestContext.Current.CancellationToken); return result.ToArray(); }
    internal static byte[] Png(int width, int height) => ImageBytes(width, height, static (image, stream) => image.SaveAsPng(stream));
    internal static byte[] Jpeg(int width, int height) => ImageBytes(width, height, static (image, stream) => image.SaveAsJpeg(stream));
    internal static byte[] WebP(int width, int height) => ImageBytes(width, height, static (image, stream) => image.SaveAsWebp(stream));
    private static byte[] ImageBytes(int width, int height, Action<Image<Rgba32>, Stream> save)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        save(image, stream);
        return stream.ToArray();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class AssetClock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class AssetEnvironment : IHostEnvironment { public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Tests"; public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider(); }
}
