using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Services.Story;

namespace lumibelle.Services.Storage;

// Released format-2 rules. Keep these raw JSON transformations fixed when runtime models evolve.
internal sealed class DocumentVersionsStep : IProjectFormatStep
{
    public int To => 2;
    public string Summary => "Bring saved documents up to date and preserve existing shot work";

    public async Task ApplyAsync(ProjectUpgradeContext context, CancellationToken ct)
    {
        var versions = context.Versions();
        foreach (var (kind, version) in versions)
        {
            var maximum = kind switch { "production" => 4, "shots" or "script" or "ai_preferences" => 2, _ => 1 };
            if (!ProjectDocuments.All.Any(s => s.Kind == kind) || version < 1 || version > maximum)
                throw new WorkspaceStoreException($"The stored {kind} document uses unsupported schema version {version}. Upgrade was stopped.");
        }
        foreach (var kind in new[] { "script", "ai_preferences", "shots" })
        {
            if (versions.GetValueOrDefault(kind) != 1) continue;
            var document = (await context.LoadAsync(kind, complete: kind == "shots", ct))!;
            if (kind == "shots") UpgradeShots(document);
            if (kind == "ai_preferences") { document["textDefault"] = null; document["textDefaultRevision"] = 0; }
            document["schemaVersion"] = 2;
            context.Replace(kind, document);
        }
        var productionVersion = versions.GetValueOrDefault("production");
        if (productionVersion == 4) return;
        var production = await context.LoadAsync("production", true, ct) ?? new JsonObject
            { ["projectId"] = context.Project, ["revision"] = 0, ["compositions"] = new JsonArray() };
        if (productionVersion < 2)
        {
            // Keep compatible identities and settings. Unmappable records remain in the original backup.
            var compatible = Items(production, "compositions").Where(Compatible).Select(c => c.DeepClone()).ToArray();
            production["compositions"] = new JsonArray(compatible);
            var shots = await context.LoadAsync("shots", false, ct);
            foreach (var shot in Items(shots, "shots"))
                if (!Items(production, "compositions").Any(c => Identity(c["shotId"]) == Identity(shot["id"])))
                    ((JsonArray)production["compositions"]!).Add(new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString("D"), ["shotId"] = shot["id"]!.DeepClone(), ["name"] = "Default setup", ["version"] = 1L,
                        ["inputs"] = new JsonObject(), ["history"] = new JsonArray()
                    });
        }
        ValidateProduction(production, productionVersion);
        if (productionVersion < 3) UpgradeSharedContent(production);
        UpgradeEnvironment(production);
        foreach (var composition in Items(production, "compositions"))
        {
            foreach (var key in new[] { "prompt", "directingNotes", "revisionNotes", "referenceUsage", "appliedJobId", "reviewJobId", "acceptedRevisionId", "history" })
                composition.Remove(key);
            if (composition["inputs"] is JsonObject inputs)
                foreach (var key in new[] { "environmentAssetId", "environmentStaging", "environmentInspectionDisabled", "environmentInspection", "characterVoices", "images", "voices", "videos" })
                    inputs.Remove(key);
        }
        production["schemaVersion"] = 4;
        context.Replace("production", production);
    }

    private static IEnumerable<JsonObject> Items(JsonObject? document, string name) =>
        (document?[name] as JsonArray ?? []).OfType<JsonObject>();
    private static Guid? Identity(JsonNode? value) => value?.Deserialize<Guid>();
    private static string? Text(JsonObject o, string name) => o[name]?.GetValue<string>();
    private static bool Compatible(JsonObject c) => Guid.TryParse(Text(c, "id"), out var id) && id != Guid.Empty &&
        Guid.TryParse(Text(c, "shotId"), out var shot) && shot != Guid.Empty && c["inputs"] is JsonObject &&
        c["version"]?.GetValue<long>() >= 1 && !string.IsNullOrWhiteSpace(Text(c, "name"));

    private static void ValidateProduction(JsonObject production, int version)
    {
        if (production["compositions"] is not JsonArray compositions || compositions.Any(c => c is not JsonObject o || !Compatible(o)) ||
            compositions.OfType<JsonObject>().Select(c => Identity(c["id"])).Distinct().Count() != compositions.Count)
            throw new WorkspaceStoreException("The saved production setups are invalid. Upgrade was stopped and their backup has been kept.");
        if (version == 3 && (production["shotContent"] is not JsonArray shared || production["earlierSetupContent"] is not JsonArray ||
            shared.Any(c => c is not JsonObject) || compositions.OfType<JsonObject>().Any(c => !shared.OfType<JsonObject>().Any(s => Identity(s["shotId"]) == Identity(c["shotId"])))))
            throw new WorkspaceStoreException("The saved shared shot content is invalid. Upgrade was stopped and its backup has been kept.");
    }

    private static void UpgradeShots(JsonObject document)
    {
        var shots = Items(document, "shots")
            .Concat(Items(document, "trash").Select(t => t["owner"]).OfType<JsonObject>())
            .Concat(Items(document, "recovery").SelectMany(r => Items(r, "shots")));
        foreach (var shot in shots) shot["aspectOverride"] = Text(shot, "aspect") == "16:9" ? null : shot["aspect"]?.DeepClone();
    }

    private static JsonObject Content(JsonObject composition)
    {
        var content = new JsonObject { ["shotId"] = JsonValue.Create(Identity(composition["shotId"])) };
        foreach (var key in new[] { "prompt", "directingNotes", "revisionNotes", "referenceUsage" }) content[key] = composition[key]?.DeepClone() ?? JsonValue.Create("");
        foreach (var key in new[] { "appliedJobId", "reviewJobId", "acceptedRevisionId", "history" }) content[key] = composition[key]?.DeepClone();
        content["history"] ??= new JsonArray();
        var inputs = composition["inputs"] as JsonObject;
        foreach (var key in new[] { "environmentAssetId", "environmentStaging", "environmentInspectionDisabled", "environmentInspection", "characterVoices", "images", "voices", "videos" })
            if (inputs?[key] is { } value) content[key] = value.DeepClone();
        if (inputs?["environmentAssetId"] is { } environment) content["environmentAssetId"] = JsonValue.Create(Identity(environment));
        foreach (var key in new[] { "images", "voices", "videos" }) content[key] ??= new JsonArray();
        return content;
    }

    private static void UpgradeSharedContent(JsonObject production)
    {
        var shared = new JsonArray(); var earlier = new JsonArray();
        foreach (var group in Items(production, "compositions").GroupBy(c => Identity(c["shotId"])))
        {
            var selected = group.OrderBy(c => c["archived"]?.GetValue<bool>() ?? false)
                .ThenBy(c => string.IsNullOrWhiteSpace(Text(c, "prompt"))).ThenBy(c => Text(c, "name") != "Default setup").First();
            var content = Content(selected); shared.Add(content);
            foreach (var other in group.Where(c => c != selected))
            {
                var previous = Content(other);
                if (!JsonNode.DeepEquals(content, previous)) earlier.Add(new JsonObject
                    { ["setupId"] = other["id"]?.DeepClone(), ["setupName"] = other["name"]?.DeepClone(), ["content"] = previous });
            }
        }
        production["shotContent"] = shared; production["earlierSetupContent"] = earlier;
    }

    private static void UpgradeEnvironment(JsonObject production)
    {
        var earlier = production["earlierSetupContent"] as JsonArray ?? throw new WorkspaceStoreException("The production recovery content is invalid.");
        foreach (var content in Items(production, "shotContent"))
        {
            var setups = Items(production, "compositions").Where(c => Identity(c["shotId"]) == Identity(content["shotId"])).ToArray();
            var environments = setups.Where(c => !(c["archived"]?.GetValue<bool>() ?? false))
                .Select(c => Identity((c["inputs"] as JsonObject)?["environmentAssetId"])).OfType<Guid>().Distinct().ToArray();
            if (content["environmentAssetId"] is null && environments.Length == 1) content["environmentAssetId"] = JsonValue.Create(environments[0]);
            foreach (var setup in setups)
            {
                var environment = (setup["inputs"] as JsonObject)?["environmentAssetId"];
                if (environment is null || Identity(environment) == Identity(content["environmentAssetId"])) continue;
                if (earlier.OfType<JsonObject>().Any(e => Identity(e["setupId"]) == Identity(setup["id"]) && Identity(e["content"]?["environmentAssetId"]) == Identity(environment))) continue;
                var previous = (JsonObject)content.DeepClone(); previous["environmentAssetId"] = environment.DeepClone();
                earlier.Add(new JsonObject { ["setupId"] = setup["id"]?.DeepClone(), ["setupName"] = setup["name"]?.DeepClone(), ["content"] = previous });
            }
        }
    }
}
