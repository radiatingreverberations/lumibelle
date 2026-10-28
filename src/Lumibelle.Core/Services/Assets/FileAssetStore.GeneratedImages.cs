using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    public async Task<SavedAssetImage> PublishGeneratedImageAsync(Guid projectId, GeneratedImageInput input, Stream content,
        CancellationToken cancellationToken = default)
    {
        // Capture metadata before reading a caller-owned stream or waiting for a lock.
        input = JsonSerializer.Deserialize<GeneratedImageInput>(JsonSerializer.SerializeToUtf8Bytes(input, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        if (input is null || input.JobId == Guid.Empty || input.ImageId == Guid.Empty || input.AssetId == Guid.Empty || input.Image is null ||
            input.Image.Origin is not (AssetImageOrigin.Generated or AssetImageOrigin.Edited) || input.Image.Generation is not { } generation ||
            generation.AiJobId != input.JobId || generation.BatchId is null || generation.BatchId == Guid.Empty || generation.CandidateNumber is null or < 1 ||
            (input.Image.Origin == AssetImageOrigin.Edited) != (generation.Edit is not null))
            throw new WorkspaceStoreException("A generated candidate needs its exact job, image and destination identities.");
        var ct = cancellationToken;
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
        {
            if (memory.Length + read > MaximumImageBytes) throw new WorkspaceStoreException("The generated image exceeds 25 MB.");
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (memory.Length == 0) throw new WorkspaceStoreException("The generated image is empty.");
        var bytes = memory.GetBuffer().AsSpan(0, checked((int)memory.Length));
        var info = ImageInspector.Inspect(bytes);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Input = input, Content = Convert.ToHexString(SHA256.HashData(bytes)) }, AtomicJsonFile.Options)));
        var directory = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var current = await ReadAsync(directory, projectId, ct);
        if (current.ImagePublications.SingleOrDefault(r => r.ImageId == input.ImageId) is { } receipt)
        {
            if (receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This candidate identity already has a different saved image.");
            var owner = AssetImageLocations.RecordedOwner(current, new(receipt.AssetId, receipt.ImageId));
            return new(current, owner?.Id ?? receipt.AssetId, receipt.ImageId, owner is not null);
        }
        var asset = current.Assets.SingleOrDefault(a => a.Id == input.AssetId) ?? throw new WorkspaceStoreException("The destination asset was removed. Restore it before retrying this saved candidate.");
        if (current.Assets.Any(a => a.Images.Any(i => i.Id == input.ImageId)) || current.Trash.Any(t => t.Image.Id == input.ImageId) || current.ImageCopyReceipts.Any(r => r.ImageId == input.ImageId))
            throw new WorkspaceStoreException("The generated image identity is already in use.");
        if (input.Image.LookId is { } look && (LookPolicy.Find(asset, look) is null || generation.Look is null) ||
            generation.Look is { } target && (target.AssetId != asset.Id || target.LookId != input.Image.LookId))
            throw new WorkspaceStoreException("The destination look does not match the captured candidate.");
        var image = new AssetImage { Id = input.ImageId, FileName = input.ImageId.ToString("N") + info.Extension,
            ContentType = info.ContentType, Width = info.Width, Height = info.Height, Tags = NormalizeTags(input.Image.Tags),
            LookId = input.Image.LookId, Origin = input.Image.Origin, PendingReview = input.Image.PendingReview, Generation = generation, CreatedUtc = clock.GetUtcNow() };
        var next = current with { Assets = current.Assets.Select(a => a.Id == asset.Id ? a with { Images = [.. a.Images, image], UpdatedUtc = clock.GetUtcNow() } : a).ToList(),
            ImagePublications = [.. current.ImagePublications, new(input.JobId, image.Id, asset.Id, fingerprint)] };
        Validate(Normalize(next), projectId);
        var path = ImagePath(directory, asset.Id, image); var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            memory.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            { await memory.CopyToAsync(output, ct); await output.FlushAsync(ct); }
            DurableFile.Flush(temporary);
            File.Move(temporary, path, true); // replace only an orphan of this stable, unpublished candidate
            try { return new(await PublishAsync(directory, next, current.Revision, ct), asset.Id, image.Id, true); }
            catch { TryDelete(path); throw; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new WorkspaceStoreException("The generated candidate could not be saved. Retry publication without generating again.", e); }
        finally { TryDelete(temporary); }
    }
}
