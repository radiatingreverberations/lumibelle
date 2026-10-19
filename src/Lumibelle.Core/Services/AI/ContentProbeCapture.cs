using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed class ContentProbeCapture(IAiSettingsStore settings, IContentProbeStore probes,
    IAiProviderRegistry providers, ICodexClient? codex = null)
{
    public async Task<IReadOnlyList<AiJobSubmission>> PrepareAsync(Guid batchId, Guid tabId,
        IReadOnlyList<Guid> probeIds, IReadOnlyList<TextModelReference> models, int repetitions,
        long libraryRevision, long settingsRevision, CancellationToken ct = default)
    {
        // Freeze caller-owned collections before any await. Preparation does catalog
        // checks only; the user must explicitly enqueue the returned immutable plan.
        if (batchId == Guid.Empty || tabId == Guid.Empty || probeIds is null || models is null ||
            repetitions is < 1 or > 3 || probeIds.Count == 0 || models.Count == 0 ||
            (long)probeIds.Count * models.Count * repetitions > ContentProbePolicy.MaximumBatch)
            throw new WorkspaceStoreException($"Choose tests and configurations, with at most {ContentProbePolicy.MaximumBatch} requests per run and 1–3 repetitions.");
        var ids = probeIds.ToArray(); var selected = models.ToArray();
        foreach (var model in selected) TextModelPolicy.Validate(model);
        if (ids.Distinct().Count() != ids.Length || selected.DistinctBy(TextModelProfiles.ChoiceKey).Count() != selected.Length)
            throw new WorkspaceStoreException("Choose distinct tests and configurations.");
        var configured = Copy(await settings.LoadAsync(ct));
        var library = await probes.LoadLibraryAsync(ct);
        if (configured.Revision != settingsRevision || library.Revision != libraryRevision)
            throw new WorkspaceStoreException("The saved tests or profiles changed. Refresh and review the selections before preparing again.");
        var definitions = ContentProbeBuiltIns.All(library);
        var tests = ids.Select(id => definitions.SingleOrDefault(p => p.Id == id && p.Enabled)
            ?? throw new WorkspaceStoreException("A selected test was disabled or removed. Refresh the library.")).Select(ContentProbePolicy.Copy).ToArray();
        var checks = new Dictionary<AiBackend, AiConnectionCheck>();
        foreach (var backend in selected.Select(m => m.Backend).Distinct())
            checks[backend] = await providers.CheckAsync(backend, configured, cancellationToken: ct);
        CodexConnection? codexCheck = selected.Any(m => m.Backend == AiBackend.Codex)
            ? await (codex ?? throw new AiGenerationException("Codex is not configured.")).CheckAsync(configured.Codex, ct) : null;
        var submissions = new List<AiJobSubmission>();
        foreach (var original in selected)
        {
            var model = TextModelPolicy.WithDefaultEffort(TextModelPolicy.Normalize(original), configured);
            model = VllmTextSettings.Apply(model, configured);
            TextModelPolicy.CheckRequestServer(model, configured);
            if (TextModelPolicy.Issue(model, configured, checks[model.Backend]) is { } issue) throw new AiGenerationException(model.Name + ": " + issue);
            var capturedSettings = ComfyTextSettings.Capture(model, configured);
            var capture = model.Backend == AiBackend.Codex ? CodexClient.Capture(codexCheck!, model.Model, model.ReasoningEffort) : null;
            foreach (var test in tests)
                for (var iteration = 1; iteration <= repetitions; iteration++)
                {
                    var request = new ContentProbeRequest(1, Guid.NewGuid(), batchId, iteration, test,
                        ContentProbePolicy.Prompt(test), model, capturedSettings, Random.Shared.NextInt64(1, long.MaxValue), capture);
                    submissions.Add(Submission(request, tabId));
                }
        }
        return submissions;
    }

    public static AiJobSubmission Submission(ContentProbeRequest request, Guid tabId)
    {
        ContentProbePolicy.ValidateRequest(request);
        if (tabId == Guid.Empty) throw new WorkspaceStoreException("A test run needs a tab identity.");
        return AiJobSubmission.Create(request.JobId, AiJobKind.ContentProbe, request.Model.Backend,
            new(ModelKey: request.JobId.ToString("N")), "Text model tests", request.Probe.Name + " · " + request.Model.Name + " · run " + request.Iteration,
            tabId, request);
    }

    public static AiJobSubmission Repeat(ContentProbeRequest request, Guid tabId) => Submission(request with
    { JobId = Guid.NewGuid(), BatchId = Guid.NewGuid(), Iteration = 1, Seed = Random.Shared.NextInt64(1, long.MaxValue) }, tabId);

    private static AiSettings Copy(AiSettings settings) => JsonSerializer.Deserialize<AiSettings>(
        JsonSerializer.SerializeToUtf8Bytes(settings, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
}
