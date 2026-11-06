using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiJobCoordinatorTests
{
    // Claude Code was removed as a provider. The queue an earlier build left must load, and its requests must not run or wait forever.
    [Fact]
    public async Task RequestsQueuedForClaudeCodeNeedAttentionAndNeverRun()
    {
        var waiting = await Store.EnqueueAsync(Request(AiBackend.Codex), _ct);
        var running = await Store.EnqueueAsync(Request(AiBackend.Codex, AiJobKind.AssetExtraction), _ct);
        var finished = await Store.EnqueueAsync(Request(AiBackend.Codex), _ct);
        var other = await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(Index, _ct))!;
        foreach (var job in file["jobs"]!.AsArray())
        {
            var id = Guid.Parse(job!["id"]!.GetValue<string>());
            if (id == other.Id) continue;
            job["backend"] = "ClaudeCode";
            if (id == running.Id) { job["state"] = "Running"; job["leaseId"] = Guid.NewGuid().ToString(); job["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"); }
            if (id == finished.Id) { job["state"] = "Completed"; job["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O"); }
        }
        file["paused"] = new JsonArray("ClaudeCode");
        await File.WriteAllTextAsync(Index, file.ToJsonString(), _ct);
        var saved = await Store.ReadAsync(_ct);
        Assert.Equal(3, saved.Jobs.Count(j => j.Backend == AiBackend.ClaudeCode)); Assert.Contains(AiBackend.ClaudeCode, saved.Paused);

        var handler = new FakeHandler(); var queue = await Start(handler);
        var only = await handler.Next(_ct); Assert.Equal(other.Id, only.Context.Job.Id);
        foreach (var id in new[] { waiting.Id, running.Id })
        {
            var job = await State(id, j => j.State == AiJobState.NeedsAttention);
            Assert.Equal(AiJobRecovery.None, job.Recovery); Assert.Equal(TextModelPolicy.RetiredRequest, job.Error);
            Assert.True(job.Unread); Assert.True(job.CanClearActivity); Assert.False(job.LocksTarget); Assert.False(job.HoldsProvider);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => queue.ResumeAsync(id, _ct));
        }
        var document = await Store.ReadAsync(_ct);
        Assert.Equal(AiJobState.Completed, document.Jobs.Single(j => j.Id == finished.Id).State);
        Assert.DoesNotContain(AiBackend.ClaudeCode, document.Paused);
        await only.Complete(); await State(other.Id, j => j.State == AiJobState.Completed);
        Assert.Single(handler.Calls);

        var refused = await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiBackend.ClaudeCode), _ct));
        Assert.Equal(TextModelPolicy.RetiredIssue, refused.Message);
    }
}
