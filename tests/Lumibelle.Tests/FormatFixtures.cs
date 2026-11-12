using System.Text.Json.Nodes;
using lumibelle.Services.Storage;

namespace Lumibelle.Tests;

// Frozen serialized inputs for project format 1. Add fixtures for new formats, never regenerate these from runtime models.
internal static class FormatFixtures
{
    internal const string Shot = "11111111-1111-1111-1111-111111111111";
    internal const string Setup = "22222222-2222-2222-2222-222222222222";
    internal static IReadOnlyDictionary<string, string> Json(Guid project) => new Dictionary<string, string>
    {
        ["script"] = """{"schemaVersion":1,"projectId":"PROJECT","revision":0,"brief":{"idea":"A trip","duration":"Short","style":"","productionProfile":""},"blocks":[],"appliedProposalIds":[],"sourceSceneLinks":[]}""".Replace("PROJECT", project.ToString()),
        ["ai_preferences"] = """{"schemaVersion":1,"projectId":"PROJECT","textDefault":null,"textDefaultRevision":7}""".Replace("PROJECT", project.ToString()),
        ["shots"] = """{"schemaVersion":1,"projectId":"PROJECT","revision":0,"shots":[{"id":"11111111-1111-1111-1111-111111111111","title":"Arrival","description":"A traveller arrives.","duration":2,"aspect":"9:16"}],"takes":[],"trash":[],"recovery":[],"sceneSetups":[]}""".Replace("PROJECT", project.ToString()),
        ["production"] = """{"schemaVersion":2,"projectId":"PROJECT","revision":3,"compositions":[{"id":"22222222-2222-2222-2222-222222222222","shotId":"11111111-1111-1111-1111-111111111111","version":1,"name":"Default setup","prompt":"Arrive at the station.","history":[],"inputs":{"aspect":"9:16","images":[],"voices":[],"videos":[]}}]}""".Replace("PROJECT", project.ToString())
    };

    internal static async Task LegacyOnly(string directory, Guid project, CancellationToken ct)
    {
        foreach (var (kind, json) in Json(project))
            await File.WriteAllTextAsync(Path.Combine(directory, kind == "ai_preferences" ? "ai-preferences.json" : kind + ".json"), json, ct);
    }

    internal static async Task Sqlite(string directory, Guid project, CancellationToken ct)
    {
        foreach (var (kind, json) in Json(project))
        {
            var store = ProjectDocuments.All.Single(s => s.Kind == kind);
            var prepared = store.Prepare(System.Text.Json.JsonSerializer.SerializeToElement(JsonNode.Parse(json)));
            ProjectDatabase.For(directory).Write(s => store.Apply(s, prepared, replace: true));
        }
        TestProjectDatabase.Execute(directory, "PRAGMA user_version = 1");
        DocumentCache.EvictDirectory(directory);
    }
}
