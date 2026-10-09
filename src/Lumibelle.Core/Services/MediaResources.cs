using System.Globalization;
using System.IO.Compression;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services;

public sealed record MediaResponse(int Status, IReadOnlyDictionary<string, string> Headers, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

// The same resource names and response semantics are used by HTTP and the native WebView.
public sealed class MediaResources(IAssetStore assets, IImageTrashStore trash, IShotStore shots, IVoiceStore voices, IAiSettingsStore settings,
    lumibelle.Services.Production.IReferenceVideoStore? referenceVideos = null, ICutExporter? cutExporter = null,
    lumibelle.Services.Projects.IProjectPackageService? projectPackages = null)
{
    public async Task<MediaResponse> GetAsync(string url, string method = "GET", IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        if (method is not ("GET" or "HEAD")) return Empty(405);
        AssetMedia? media;
        try { media = await OpenAsync(url, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or FileNotFoundException or DirectoryNotFoundException) { return Empty(404); }
        if (media is null) return Empty(404);
        try { return Respond(media, method, headers); }
        catch { await media.DisposeAsync(); throw; }
    }

    private async Task<AssetMedia?> OpenAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(new Uri("https://lumibelle.local"), url);
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last().ElementAtOrDefault(1) ?? ""));
        bool Flag(string key) => query.GetValueOrDefault(key) == "true";
        if (path.Length == 5 && path[0] == "downloads" && path[1] == "projects" &&
            Guid.TryParse(path[2], out var packageProject) && path[3] == "packages" &&
            path[4].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(path[4][..^4], out var packageId) && projectPackages is not null)
            return await projectPackages.OpenExportAsync(packageProject, packageId, ct).ConfigureAwait(false);
        if (path.Length == 5 && path[0] == "downloads" && path[1] == "projects" &&
            Guid.TryParse(path[2], out var exportProject) && path[3] == "cuts" &&
            path[4].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(path[4][..^4], out var exportId) && cutExporter is not null)
            return await cutExporter.OpenAsync(exportProject, exportId, ct).ConfigureAwait(false);
        if (path.Length < 4 || path[0] != "media" || !Guid.TryParse(path[2], out var project) || project == Guid.Empty) return null;
        if (path[1] == "trash" && path.Length == 4 && Guid.TryParse(path[3], out var trashId)) return await trash.OpenTrashImageAsync(project, trashId, ct).ConfigureAwait(false);
        if (path[1] == "production-trash" && path.Length == 5 && Guid.TryParse(path[4], out var deletedId) && Enum.TryParse<MediaTrashKind>(path[3], true, out var kind))
        {
            int? frame = int.TryParse(query.GetValueOrDefault("frame"), out var f) ? f : null;
            return kind == MediaTrashKind.Voice ? await voices.OpenVoiceAsync(project, deletedId, true, ct).ConfigureAwait(false)
                : kind == MediaTrashKind.Take ? await shots.OpenAsync(project, deletedId, ShotTrashKind.Take, frame, true, ct).ConfigureAwait(false) : null;
        }
        if (path[1] != "projects") return null;
        if (path.Length == 7 && path[3] == "assets" && path[5] == "images" && Guid.TryParse(path[4], out var asset) && Guid.TryParse(path[6], out var image))
            return await assets.OpenImageAsync(project, asset, image, ct).ConfigureAwait(false);
        if (path.Length < 5 || !Guid.TryParse(path[4], out var id)) return null;
        if (path[3] == "takes")
        {
            if (path.Length == 6 && path[5] == "join-preview") return await shots.OpenJoinPreviewAsync(project, id, ct).ConfigureAwait(false);
            if (path.Length == 5) return await shots.OpenAsync(project, id, ShotTrashKind.Take, ct: ct).ConfigureAwait(false);
            if (path.Length == 7 && path[5] == "frames" && int.TryParse(path[6], out var frame) && frame >= 0)
                return await shots.OpenAsync(project, id, ShotTrashKind.Take, frame, ct: ct).ConfigureAwait(false);
        }
        if (path.Length == 7 && path[3] == "reference-videos" && path[5] == "frames" && referenceVideos is not null &&
            int.TryParse(path[6], out var frameIndex) && double.TryParse(query.GetValueOrDefault("seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && query.GetValueOrDefault("source") is { } frameSource)
            return await referenceVideos.OpenFrameAsync(project, new(id, frameSource, frameIndex, seconds), (await settings.LoadAsync(ct).ConfigureAwait(false)).H3, ct).ConfigureAwait(false);
        if (path.Length == 6 && path[3] == "reference-videos" && path[5] == "thumbnail" && referenceVideos is not null)
            return await referenceVideos.OpenThumbnailAsync(project, id, (await settings.LoadAsync(ct).ConfigureAwait(false)).H3, ct).ConfigureAwait(false);
        if (path.Length != 5) return null;
        if (path[3] == "reference-videos" && referenceVideos is not null) return await referenceVideos.OpenAsync(project, id, ct).ConfigureAwait(false);
        if (path[3] == "voices") return await voices.OpenVoiceAsync(project, id, ct: ct).ConfigureAwait(false);
        if (path[3] == "voice-previews") return await voices.OpenVoicePreviewAsync(project, id, (await settings.LoadAsync(ct).ConfigureAwait(false)).H3, Flag("staged"), Flag("trash"), ct).ConfigureAwait(false);
        return null;
    }

    public static MediaResponse Respond(AssetMedia media, string method, IReadOnlyDictionary<string, string>? request = null)
    {
        string? Header(string name) => request?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = media.ContentType, ["Cache-Control"] = "no-store", ["Last-Modified"] = media.LastModified.ToUniversalTime().ToString("R") };
        var content = media.Content; var status = 200;
        if (Header("If-Modified-Since") is { } since && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified) &&
            media.LastModified.ToUnixTimeSeconds() <= modified.ToUnixTimeSeconds())
        { content.Dispose(); return new(304, headers, Stream.Null); }
        if (content.CanSeek)
        {
            var length = content.Length; var start = 0L; var end = length - 1;
            headers["Accept-Ranges"] = "bytes";
            var range = Header("Range"); var ifRange = Header("If-Range");
            if (ifRange is not null && ifRange != headers["Last-Modified"]) range = null;
            if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && !range.Contains(','))
            {
                var parts = range[6..].Split('-', 2); bool valid = parts.Length == 2 && length > 0;
                if (valid && parts[0].Length == 0)
                { valid = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) && suffix > 0; if (valid) start = Math.Max(0, length - suffix); }
                else if (valid)
                {
                    valid = long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) && start < length;
                    if (parts[1].Length > 0) valid &= long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) && end >= start;
                    end = Math.Min(end, length - 1);
                }
                if (!valid) { content.Dispose(); headers["Content-Range"] = $"bytes */{length}"; headers["Content-Length"] = "0"; return new(416, headers, Stream.Null); }
                status = 206; headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }
            var count = end - start + 1; headers["Content-Length"] = count.ToString(CultureInfo.InvariantCulture);
            content.Position = start;
            if (status == 206) content = new BoundedReadStream(content, count);
        }
        if (method == "HEAD") { content.Dispose(); content = Stream.Null; }
        return new(status, headers, content);
    }
    private static MediaResponse Empty(int status) => new(status, new Dictionary<string,string> { ["Cache-Control"] = "no-store", ["Content-Length"] = "0" }, Stream.Null);
}

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream inner;
    private readonly long start, length;
    public BoundedReadStream(Stream inner, long length) { this.inner = inner; start = inner.Position; this.length = length; }
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => inner.Position - start; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, length - Position)));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Math.Max(0, length - Position))], ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(Position + offset), SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (target < 0 || target > length) throw new IOException("Cannot seek outside the selected byte range.");
        inner.Position = checked(start + target); return target;
    }
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}
