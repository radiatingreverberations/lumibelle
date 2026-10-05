using lumibelle.Services.Projects;

namespace Lumibelle.Tests;

public sealed class ProjectStorageUsageTests
{
    [Theory]
    [InlineData("shots/takes/9f0c/video.mp4", StorageKind.TakeVideos)]
    [InlineData("shots/takes/9f0c/archive-0003.webp", StorageKind.TakeArchives)]
    [InlineData("shots/takes/9f0c/refinement/latent.bin", StorageKind.TakeData)]
    [InlineData("shots/runs/71aa/inputs/image-00.png", StorageKind.GenerationRuns)]
    [InlineData("reel-runs/5c38/candidate-d388/archive-0000.webp", StorageKind.GenerationRuns)]
    [InlineData("reference-videos/4e2b/video.mp4", StorageKind.ReelVideos)]
    [InlineData("reference-videos/4e2b/lossless/archive-0000.webp", StorageKind.ReelArchives)]
    [InlineData("reference-videos/4e2b/thumbnail-v1.jpg", StorageKind.ReelFrames)]
    [InlineData("refmod-previews/0594AAF8/preview.png", StorageKind.ReelFrames)]
    [InlineData("assets/77d1/images/look.png", StorageKind.Images)]
    [InlineData("assets/77d1/voices/hello.wav", StorageKind.Voices)]
    [InlineData("shots.json", StorageKind.ProjectData)]
    [InlineData("script-sources/a1.json", StorageKind.ProjectData)]
    [InlineData("notes/readme.txt", StorageKind.Other)]
    public void FilesAreGroupedByWhatTheyAre(string path, StorageKind kind) => Assert.Equal(kind, ProjectStorageUsage.Classify(path));

    [Fact]
    public void MeasuringSumsEachKindAndListsTheLargestFiles()
    {
        var root = Directory.CreateTempSubdirectory("lumibelle-usage-").FullName;
        try
        {
            void Write(string path, int bytes) { var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, new byte[bytes]); }
            Write("shots/takes/a/video.mp4", 300); Write("shots/takes/b/video.mp4", 200);
            Write("shots/runs/r/inputs/image-00.png", 900); Write("shots.json", 10);
            var usage = ProjectStorageUsage.Measure(root, largest: 2);
            Assert.Equal(1410, usage.Total);
            Assert.Equal(StorageKind.GenerationRuns, usage.Categories[0].Kind);
            Assert.Equal(new StorageCategory(StorageKind.TakeVideos, 500, 2), usage.Categories.Single(c => c.Kind == StorageKind.TakeVideos));
            Assert.Equal(["shots/runs/r/inputs/image-00.png", "shots/takes/a/video.mp4"], usage.Largest.Select(f => f.Path));
        }
        finally { Directory.Delete(root, true); }
    }
}
