using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

/// <summary>
/// Turns a queued request into one an earlier build captured for the removed Claude Code provider: the header, the captured
/// model and the fingerprint over both, so the store reads it back as it would a real one. New requests for it are refused.
/// </summary>
internal static class RetiredClaudeCodeJobs
{
    public static async Task RewriteAsync(FileAiJobStore store, Guid id, CancellationToken ct)
    {
        var header = (await store.ReadAsync(ct)).Jobs.Single(j => j.Id == id);
        var snapshotPath = Path.Combine(store.DirectoryFor(id), "request.json");
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath, ct))!.AsObject();
        var request = JsonDocument.Parse(snapshot["request"]!.ToJsonString().Replace($"\"{header.Backend}\"", "\"ClaudeCode\"")).RootElement.Clone();
        var batch = snapshot["initialBatch"] is { } initial ? initial.Deserialize<AiBatchDefinition>(AtomicJsonFile.Options) : null;
        var fingerprint = FileAiJobStore.Fingerprint(new(header.Id, header.Kind, AiBackend.ClaudeCode, header.Target, header.ProjectName, header.TargetName, header.OriginTabId, request, batch));
        snapshot["request"] = JsonNode.Parse(request.GetRawText()); snapshot["fingerprint"] = fingerprint;
        await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString(), ct);

        var index = Path.Combine(Path.GetDirectoryName(store.DirectoryFor(id))!, "queue.json");
        var queue = JsonNode.Parse(await File.ReadAllTextAsync(index, ct))!;
        var job = queue["jobs"]!.AsArray().Single(j => Guid.Parse(j!["id"]!.GetValue<string>()) == id)!;
        job["backend"] = "ClaudeCode"; job["requestFingerprint"] = fingerprint;
        await File.WriteAllTextAsync(index, queue.ToJsonString(), ct);
    }
}
