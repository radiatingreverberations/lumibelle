using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    // Called only after the service has read and checked the saved failed result.
    // BuildAsync reuses normal provider capture/account checks; no image store or
    // original multimodal message builder is called.
    internal async Task<AiJobSubmission> RepairAsync(Guid id, Guid tab, AiJobHeader source,
        AiTextJobRequest request, AiTextRepair repair, CancellationToken ct)
    {
        if (id == Guid.Empty || id == source.Id || repair.SourceJobId != source.Id)
            throw new WorkspaceStoreException("A repair requires a new request identity.");
        var task = RepairTask(request, id);
        var label = AiTextRepairs.LabelPrefix + (request.Kind == AiJobKind.ScriptAssistant ? "Script response" : source.TargetName.Replace(AiTextRepairs.LabelPrefix, "", StringComparison.Ordinal));
        var submission = await BuildAsync(id, tab, request.Kind, source.Target,
            label[..Math.Min(label.Length, 500)],
            task, request.Model, false, Copy(request.Settings), AiTextRepairs.Profile,
            AiTextRepairs.Messages(repair).Select(m => m.ToMessage()).ToList(),
            request.Temperature, request.GuidanceBaseline, ct,
            responseSchema: request.Kind == AiJobKind.ReelComposition ? request.ResponseSchema : null);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!
            with { Repair = repair, SelectionSource = TextModelSelectionSource.RequestOverride };
        submission = submission with { Snapshot = JsonSerializer.SerializeToElement(captured, AtomicJsonFile.Options) };
        AiTextJobHandler.Read(source with { Id = id, Backend = submission.Backend }, submission.Snapshot);
        return submission;
    }

    internal static JsonElement RepairTask(AiTextJobRequest source, Guid id)
    {
        if (source.Kind != AiJobKind.ScriptAssistant) return source.Task.Clone();
        var script = source.Payload<ScriptAssistantRequest>();
        // The parser target stays frozen, but this new proposal must not reuse the
        // failed run's identity or review decisions in QueuedAssistantHistoryStore.
        var run = script.Run with { Id = id, JobId = id, Revision = 0, CreatedUtc = DateTimeOffset.UtcNow,
            Status = AssistantRunStatus.Running, Output = "", Error = null, Proposal = null, Edits = null,
            Applied = false, Rejected = false, AppliedTarget = null, CorrectedFromRunId = null, ManuallyReviewed = false };
        return JsonSerializer.SerializeToElement(script with { Run = run }, AtomicJsonFile.Options);
    }
}
