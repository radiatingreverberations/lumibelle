using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// One prompt for the originally requested candidates, not a larger latent batch.
// The versioned operation and node names are also the recovery/output-routing contract.
public static class ComfyMultiTakeWorkflow
{
    public const string Operation = "initial-takes/v1";
    public static string CandidateOperation(AiBatchCandidate candidate) => "candidate/" + candidate.Id.ToString("D");
    public static string Prefix(AiBatchCandidate candidate) => "take_" + candidate.Number.ToString(CultureInfo.InvariantCulture) + "_";
    public static string Node(AiBatchCandidate candidate, string node) => Prefix(candidate) + node;

    public static IReadOnlyList<AiBatchCandidate>? Group(AiBatchDefinition batch, AiBatchCandidate candidate,
        AiJobExecution execution, bool staged, bool recovering)
    {
        // Appends (including continuations dispatched as a new job) keep the old path.
        var initial = batch.Candidates.Where(c => c.AppendCommandId is null).ToArray();
        if (initial.Length < 2 || !initial.Any(c => c.Id == candidate.Id)) return null;
        if (execution.Submissions.Any(s => s.Operation == Operation)) return initial;
        // Do not change the execution plan of a pre-upgrade, partially processed batch.
        if (recovering || staged || candidate.Id != initial[0].Id ||
            execution.Submissions.Any(s => initial.Any(c => s.Operation == CandidateOperation(c)))) return null;
        return initial;
    }

    public static object Build(IReadOnlyList<AiBatchCandidate> candidates, Func<AiBatchCandidate, object> build, string clientId)
    {
        if (candidates.Count is < 2 or > 4 || candidates.Any(c => c.Id == Guid.Empty || c.Number < 1) ||
            candidates.Select(c => c.Id).Distinct().Count() != candidates.Count ||
            candidates.Select(c => c.Number).Distinct().Count() != candidates.Count)
            throw new WorkspaceStoreException("A shared workflow needs two to four distinct captured candidates.");
        var workflows = candidates.Select(c => JsonSerializer.SerializeToNode(build(c), AtomicJsonFile.Options) as JsonObject
            ?? throw new WorkspaceStoreException("The generated ComfyUI workflow is invalid.")).ToArray();
        var graphs = workflows.Select(w => w["prompt"] as JsonObject
            ?? throw new WorkspaceStoreException("The generated ComfyUI graph is missing.")).ToArray();
        var first = graphs[0];
        if (first.Count == 0 || first.Any(n => n.Key.StartsWith("take_", StringComparison.Ordinal)))
            throw new WorkspaceStoreException("The generated graph has invalid or reserved node identities.");
        if (graphs.Any(g => g.Count != first.Count || first.Any(n => !g.ContainsKey(n.Key))))
            throw new WorkspaceStoreException("Candidate workflows must have the same node structure.");

        var local = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (id, node) in first)
        {
            if (node is not JsonObject n || n["class_type"] is not JsonValue || n["inputs"] is not JsonObject)
                throw new WorkspaceStoreException("The generated graph contains an invalid node.");
            var type = n["class_type"]!.GetValue<string>();
            if (graphs.Any(g => g[id] is not JsonObject other || other["inputs"] is not JsonObject ||
                other["class_type"]?.GetValue<string>() != type))
                throw new WorkspaceStoreException("Candidate node types must match.");
            dependencies[id] = graphs.SelectMany(g => Links(g[id]!["inputs"]))
                .Select(link => link[0]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (dependencies[id].Any(d => !first.ContainsKey(d)))
                throw new WorkspaceStoreException("The generated graph contains a dangling input link.");
            // Never coalesce stochastic execution or output side effects, even for equal seeds.
            if (type is "KSampler" or "KSamplerAdvanced" or "SamplerCustomAdvanced" or "RandomNoise" or
                "PreviewImage" || type.StartsWith("Save", StringComparison.Ordinal) ||
                graphs.Skip(1).Any(g => !JsonNode.DeepEquals(node, g[id]))) local.Add(id);
        }
        // Any node depending on a candidate-local result is candidate-local as well.
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, inputs) in dependencies)
                if (inputs.Any(local.Contains) && local.Add(id)) changed = true;
        } while (changed);

        var merged = new JsonObject();
        foreach (var (id, node) in first.Where(n => !local.Contains(n.Key))) merged.Add(id, node!.DeepClone());
        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var (id, node) in graphs[i].Where(n => local.Contains(n.Key)))
            {
                var copy = node!.DeepClone();
                foreach (var link in Links(copy["inputs"]))
                {
                    var source = link[0]!.GetValue<string>();
                    if (local.Contains(source)) link[0] = Node(candidates[i], source);
                }
                merged.Add(Node(candidates[i], id), copy);
            }
        }
        var result = (JsonObject)workflows[0].DeepClone();
        result["client_id"] = clientId;
        result["prompt"] = merged;
        return result;
    }

    private static IEnumerable<JsonArray> Links(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            if (array.Count == 2 && array[0] is JsonValue id && id.TryGetValue<string>(out _) &&
                array[1] is JsonValue port && port.TryGetValue<int>(out _)) { yield return array; yield break; }
            foreach (var child in array)
                foreach (var link in Links(child)) yield return link;
        }
        else if (node is JsonObject obj)
            foreach (var child in obj)
                foreach (var link in Links(child.Value)) yield return link;
    }

    public static JsonElement Output(JsonElement history, AiBatchCandidate candidate)
    {
        if (!history.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
            throw new AiGenerationException("The shared workflow has no readable output receipt.");
        var prefix = Prefix(candidate);
        var own = outputs.EnumerateObject().Where(o => o.Name.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(o => o.Name[prefix.Length..], o => o.Value.Clone(), StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new { outputs = own }, AtomicJsonFile.Options);
    }

    public static bool HasOutputs(JsonElement history, AiBatchCandidate candidate, IEnumerable<string> nodes) =>
        history.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object &&
        nodes.All(n => outputs.TryGetProperty(Node(candidate, n), out _));

    // Only authoritative failed history allows partial publication. A dropped connection
    // without such a receipt retains the normal uncertain-submission recovery behavior.
    public static async Task<JsonElement?> FailedOutputAsync(AiJobContext context, string operation, CancellationToken ct)
    {
        var history = await context.ReadOperationAsync<JsonElement?>(operation, AiOperationArtifact.Output, ct);
        return history is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("status", out var status) &&
            status.TryGetProperty("status_str", out var state) && state.GetString() == "error" ? history : null;
    }

    public static int? ProgressCandidate(GenerationProgress progress, IReadOnlyList<AiBatchCandidate> candidates) =>
        candidates.FirstOrDefault(c => progress.ExecutionStageId?.StartsWith(Prefix(c), StringComparison.Ordinal) == true)?.Number;

    public static ComfyExecutionOptions Options(ComfyExecutionOptions original, IReadOnlyList<AiBatchCandidate> candidates)
    {
        var nodes = original.Nodes.ToDictionary();
        var timings = original.TimingNodes.ToDictionary(t => t.Key, t => "Shared/" + t.Value);
        foreach (var candidate in candidates)
        {
            var label = $"Take {candidate.Number} · ";
            foreach (var (id, stage) in original.Nodes)
                nodes[Node(candidate, id)] = stage with { Label = label + stage.Label,
                    ProgressLabel = stage.ProgressLabel is null ? null : label + stage.ProgressLabel,
                    EstimateScope = stage.EstimateScope is null ? null : Prefix(candidate) + stage.EstimateScope };
            foreach (var (id, stage) in original.TimingNodes) timings[Node(candidate, id)] = Prefix(candidate) + stage;
        }
        return original with { Nodes = nodes, TimingNodes = timings };
    }

    public static double? Seconds(ComfyObservedTimings? observed, AiBatchCandidate candidate, string stage, bool first)
    {
        if (observed is null || stage == "Preparation" && observed.Partial) return null;
        double? own = observed.Seconds.TryGetValue(Prefix(candidate) + stage, out var value) ? value : null;
        // Shared preparation is charged once, not once per take. Missing observations stay null.
        double? shared = first && observed.Seconds.TryGetValue("Shared/" + stage, out var common) ? common : null;
        return own is null && shared is null ? null : (own ?? 0) + (shared ?? 0);
    }
}
