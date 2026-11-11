using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    private static string ImportDirectory(string dir, Guid id) => Path.Combine(dir, "voice-imports", id.ToString("D"));
    public async Task<VoiceImportDraft> StageVoiceAsync(Guid projectId, Guid assetId, Stream input, string fileName, H3Settings settings, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AudioTypes.TryGetValue(extension, out var type)) throw new WorkspaceStoreException("Choose WAV, MP3, FLAC, M4A, or Ogg audio, at most 50 MB.");
        var dir = await files.DirectoryAsync(projectId, ct);
        if (!(await ReadAsync(dir, projectId, ct)).Assets.Any(a => a.Id == assetId && a.Category == AssetCategory.Character)) throw new WorkspaceStoreException("Choose a character for this voice.");
        var id = Guid.NewGuid(); var staging = ImportDirectory(dir, id); Directory.CreateDirectory(staging);
        try
        {
            var file = id.ToString("N") + extension; var path = Path.Combine(staging, file);
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                { total += read; if (total > 50L * 1024 * 1024) throw new WorkspaceStoreException("Voice files must be at most 50 MB."); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            }
            var media = new ProductionMediaTools(); var duration = await media.AudioDurationAsync(path, settings, ct);
            await media.PrepareAudioPreviewAsync(path, Path.Combine(staging, "preview.mp3"), settings, ct);
            var draft = new VoiceImportDraft(id, assetId, file, type, Path.GetFileNameWithoutExtension(fileName).Trim(), duration, clock.GetUtcNow());
            await AtomicJsonFile.WriteAsync(Path.Combine(staging, "draft.json"), draft, ct);
            return draft;
        }
        catch { DeleteImportDirectory(staging); throw; }
    }
    private async Task<VoiceImportDraft> ReadImportAsync(string dir, Guid id, CancellationToken ct)
    {
        var draft = await AtomicJsonFile.ReadAsync<VoiceImportDraft>(Path.Combine(ImportDirectory(dir, id), "draft.json"), ct);
        if (draft is null || draft.Id != id || draft.AssetId == Guid.Empty || !AudioTypes.ContainsKey(Path.GetExtension(draft.FileName)) ||
            draft.FileName != id.ToString("N") + Path.GetExtension(draft.FileName) || !double.IsFinite(draft.Duration) || draft.Duration is < 1 or > 3600 || draft.CreatedUtc.AddDays(1) <= clock.GetUtcNow())
            throw new WorkspaceStoreException("This voice import expired or is unavailable. Choose the file again.");
        return draft;
    }
    public Task<AssetLibrary> CommitVoiceAsync(Guid projectId, Guid importId, string name, double start, double end, long expectedRevision, CancellationToken ct = default)
        => CommitVoiceAsync(projectId, importId, name, start, end, false, expectedRevision, ct);
    public async Task<AssetLibrary> CommitVoiceAsync(Guid projectId, Guid importId, string name, double start, double end, bool makeDefault, long expectedRevision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end - start is < 1 or > 15)
            throw new WorkspaceStoreException("Name the recording and choose a 1–15 second excerpt within it.");
        name = name.Trim();
        // Preserve fingerprints for historical ordinary imports.
        var fingerprint = Convert.ToHexString(SHA256.HashData(makeDefault
            ? JsonSerializer.SerializeToUtf8Bytes(new { importId, name, start, end, makeDefault })
            : JsonSerializer.SerializeToUtf8Bytes(new { importId, name, start, end })));
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        var library = await ReadAsync(dir, projectId, ct);
        if (library.VoiceImportReceipts.FirstOrDefault(r => r.Id == importId) is { } receipt)
        {
            if (receipt.Fingerprint != fingerprint) throw new WorkspaceStoreException("This import was already saved with different values. Edit its saved excerpt instead.");
            return library;
        }
        EnsureRevision(library, expectedRevision);
        var draft = await ReadImportAsync(dir, importId, ct);
        if (end > draft.Duration + .01 || !library.Assets.Any(a => a.Id == draft.AssetId && a.Category == AssetCategory.Character)) throw new WorkspaceStoreException("The excerpt is outside the recording or its character is no longer available.");
        var voice = new VoiceReference { Id = draft.Id, AssetId = draft.AssetId, Name = name, Start = start, ExcerptDuration = end - start, Duration = draft.Duration,
            FileName = draft.FileName, ContentType = draft.ContentType, SourceReel = draft.SourceReel, CreatedUtc = clock.GetUtcNow() };
        var destination = VoicePath(dir, voice); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = ImportDirectory(dir, importId);
        try
        {
            // Retain staging until manifest publication succeeds, including failed-copy retries.
            File.Copy(Path.Combine(staging, draft.FileName), destination, true);
            File.Copy(Path.Combine(staging, "preview.mp3"), destination + ".preview.mp3", true);
            var saved = await PublishAsync(dir, library with { Voices = [.. library.Voices, voice],
                Assets = library.Assets.Select(a => makeDefault && a.Id == voice.AssetId ? a with { DefaultVoiceId = voice.Id } : a).ToList(),
                VoiceImportReceipts = [.. library.VoiceImportReceipts, new(importId, fingerprint)] }, library.Revision, ct);
            DeleteImportDirectory(staging); return saved;
        }
        catch (Exception e)
        {
            TryDelete(destination); TryDelete(destination + ".preview.mp3");
            if (e is IOException or UnauthorizedAccessException) throw new WorkspaceStoreException("Couldn’t save this voice. Its import draft remains available for retry.", e);
            throw;
        }
    }
    public async Task CancelVoiceImportAsync(Guid projectId, Guid importId, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        DeleteImportDirectory(ImportDirectory(dir, importId));
    }
    private static void DeleteImportDirectory(string staging)
    { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Startup/hourly cleanup retries abandoned imports. */ } }
    public async Task<AssetMedia?> OpenVoicePreviewAsync(Guid projectId, Guid id, H3Settings settings, bool staged = false, bool trash = false, CancellationToken ct = default)
    {
        var dir = await files.DirectoryAsync(projectId, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
        string path; DateTimeOffset created;
        if (staged)
        {
            var draft = await ReadImportAsync(dir, id, ct); created = draft.CreatedUtc; path = Path.Combine(ImportDirectory(dir, id), "preview.mp3");
        }
        else
        {
            var library = await ReadAsync(dir, projectId, ct);
            var voice = trash ? library.VoiceTrash.FirstOrDefault(t => t.Id == id && !t.Purging && t.ExpiresUtc > clock.GetUtcNow())?.Voice : library.Voices.FirstOrDefault(v => v.Id == id);
            if (voice is null || !File.Exists(VoicePath(dir, voice))) return null;
            created = voice.CreatedUtc; path = VoicePath(dir, voice) + ".preview.mp3";
            if (!File.Exists(path))
            {
                var temp = path + ".tmp.mp3";
                try { await new ProductionMediaTools().PrepareAudioPreviewAsync(VoicePath(dir, voice), temp, settings, ct); DurableFile.Flush(temp); File.Move(temp, path); }
                finally { TryDelete(temp); }
            }
        }
        try { return new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete), "audio/mpeg", created); }
        catch (FileNotFoundException) { return null; }
    }
    public async Task<IReadOnlyList<ImageTrashIssue>> CleanupVoiceImportsAsync(CancellationToken ct = default)
    {
        List<ImageTrashIssue> issues = [];
        foreach (var project in (await files.ListProjectsAsync(ct)).Projects)
        {
            try
            {
                var dir = await files.DirectoryAsync(project.Id, ct); using var gate = await ProjectFiles.LockAsync(dir, ct);
                var root = Path.Combine(dir, "voice-imports"); if (!Directory.Exists(root)) continue;
                foreach (var staging in Directory.GetDirectories(root))
                {
                    if (!Guid.TryParse(Path.GetFileName(staging), out _)) continue;
                    var draft = await AtomicJsonFile.ReadAsync<VoiceImportDraft>(Path.Combine(staging, "draft.json"), ct);
                    var created = draft?.CreatedUtc ?? new DateTimeOffset(Directory.GetCreationTimeUtc(staging));
                    if (created.AddDays(1) > clock.GetUtcNow()) continue;
                    DeleteImportDirectory(staging);
                    if (Directory.Exists(staging)) issues.Add(new(project.Id, "An expired voice import could not be removed. Cleanup will retry."));
                }
            }
            catch (lumibelle.Services.Storage.ProjectUpgradeRequiredException) { }
            catch (Exception e) when (e is WorkspaceStoreException or IOException or UnauthorizedAccessException) { issues.Add(new(project.Id, "Couldn’t clean up expired voice imports. Cleanup will retry.")); }
        }
        return issues;
    }
}
