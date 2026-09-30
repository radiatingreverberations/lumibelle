using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class H3UploadNameTests
{
    [Fact]
    public async Task IdenticalReferencesKeepOneUploadNameAcrossBatches()
    {
        async Task<(string Name, long Position)> Name(byte[] content, string file)
        {
            using var stream = new MemoryStream(content);
            var name = await ComfyH3Video.UploadNameAsync(stream, file, CancellationToken.None);
            return (name, stream.Position);
        }
        var first = await Name([1, 2, 3], "picture-1.png");
        Assert.Equal(first, await Name([1, 2, 3], "picture-1.png"));
        // The input's own name does not matter, only its content and type.
        Assert.Equal(first.Name, (await Name([1, 2, 3], "picture-2.PNG")).Name);
        Assert.NotEqual(first.Name, (await Name([1, 2, 4], "picture-1.png")).Name);
        Assert.EndsWith(".wav", (await Name([1, 2, 3], "audio-1.wav")).Name);
        Assert.Matches("^h3-[0-9a-f]{32}\\.png$", first.Name);
        // The stream is rewound so the upload sends the whole file.
        Assert.Equal(0, first.Position);
    }
}
