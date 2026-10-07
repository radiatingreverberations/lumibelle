using System.Text.Json;

namespace Lumibelle.Tests;

// Server history fixture: key outputs by the actual submitted node identities,
// never by one hard-coded output node. This catches cross-candidate misrouting.
internal static class ComfyBatchHistory
{
    public static object Job(JsonElement workflow, Func<string, bool>? include = null, bool failed = false)
    {
        var outputs = new Dictionary<string, object>();
        foreach (var node in workflow.GetProperty("prompt").EnumerateObject())
        {
            if (include is not null && !include(node.Name)) continue;
            var type = node.Value.GetProperty("class_type").GetString();
            object File(string extension) => new { filename = node.Name + extension, subfolder = "", type = "output" };
            switch (type)
            {
                case "PreviewImage": outputs[node.Name] = new { images = new[] { File(".png") } }; break;
                case "SaveVideo": outputs[node.Name] = new { videos = new[] { File(".mp4") } }; break;
                case "SaveAnimatedWEBP": outputs[node.Name] = new { images = new[] { File(".webp") } }; break;
                case "SaveLatent": outputs[node.Name] = new { latents = new[] { File(".latent") } }; break;
            }
        }
        return new { status = new { completed = !failed, status_str = failed ? "error" : "success" }, outputs };
    }
}
