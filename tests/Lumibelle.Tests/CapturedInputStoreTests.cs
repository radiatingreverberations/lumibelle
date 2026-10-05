using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class CapturedInputStoreTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("lumibelle-inputs-").FullName;
    public void Dispose() => Directory.Delete(_project, true);

    private string Run(string kind = "shots/runs") => Path.Combine(_project, kind, Guid.NewGuid().ToString("D"));
    private static AiVideoInput Write(string run, string name, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(run, "inputs"));
        File.WriteAllBytes(Path.Combine(run, "inputs", name), bytes);
        return new(name, false, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void RunsWithTheSameInputKeepOneStoredCopyAndFindItByHash()
    {
        byte[] picture = [1, 2, 3, 4], voice = [9, 9];
        string first = Run(), second = Run(), reel = Run("reel-runs");
        var input = Write(first, "image-00.png", picture); Write(second, "image-00.png", picture); Write(reel, "image-00.png", picture);
        var audio = Write(second, "voice-00.wav", voice);

        Assert.Equal(0, CapturedInputStore.Share(first, [input]));
        Assert.Equal(picture.Length + 0L, CapturedInputStore.Share(second, [input, audio]));
        Assert.Equal(picture.Length + 0L, CapturedInputStore.Share(reel, [input]));

        // One copy of each content, named by its hash; each run's inputs folder stays as its capture record.
        Assert.Equal(2, Directory.GetFiles(CapturedInputStore.Root(_project)).Length);
        foreach (var run in new[] { first, second, reel })
        {
            Assert.Empty(Directory.GetFiles(Path.Combine(run, "inputs")));
            Assert.Equal(picture, File.ReadAllBytes(CapturedInputStore.Resolve(run, input.FileName, input.Sha256)));
        }
        Assert.EndsWith(".wav", CapturedInputStore.Resolve(second, audio.FileName, audio.Sha256));
    }

    [Fact]
    public void ARunsOwnCopyComesFirstAndRefinementInputsAndOutsideFoldersStay()
    {
        var run = Run();
        var input = Write(run, "image-00.png", [5]);
        Assert.Equal(Path.Combine(run, "inputs", "image-00.png"), CapturedInputStore.Resolve(run, input.FileName, input.Sha256));
        // Refinement inputs are verified and uploaded by their own paths.
        var package = Write(run, H3RefinementPackage.FileName, [7]); var source = Write(run, "source.mp4", [8]);
        CapturedInputStore.Share(run, [input, package, source]);
        Assert.True(File.Exists(Path.Combine(run, "inputs", H3RefinementPackage.FileName)));
        Assert.True(File.Exists(Path.Combine(run, "inputs", "source.mp4")));
        Assert.False(File.Exists(Path.Combine(run, "inputs", "image-00.png")));
        // A folder that is not a project's generation folder has no store; its files stay.
        var elsewhere = Path.Combine(_project, "somewhere", "else");
        var kept = Write(elsewhere, "image-00.png", [6]);
        Assert.Equal(0, CapturedInputStore.Share(elsewhere, [kept]));
        Assert.True(File.Exists(Path.Combine(elsewhere, "inputs", "image-00.png")));
    }

    [Fact]
    public async Task RunsCapturedBeforeTheStoreShareTheirInputsByHashingThem()
    {
        string first = Run(), second = Run();
        var input = Write(first, "image-00.png", [1, 1, 1]); Write(second, "image-03.png", [1, 1, 1]);
        Assert.Equal(0, await CapturedInputStore.ShareExistingAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(3, await CapturedInputStore.ShareExistingAsync(second, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(CapturedInputStore.Root(_project)));
        Assert.Equal([1, 1, 1], File.ReadAllBytes(CapturedInputStore.Resolve(second, "image-03.png", input.Sha256)));
    }
}
