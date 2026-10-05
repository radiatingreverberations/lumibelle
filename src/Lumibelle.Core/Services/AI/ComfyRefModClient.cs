using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed class ComfyRefModClient(IHttpClientFactory clients)
{
    public const string BuildNode = "MiniMaxH3FantasticRefModCreate";
    public const string LoadNode = "MiniMaxH3RefModStack";
    public const string MediaNode = "MiniMaxH3MediaLoader";
    public const string EncodeNode = "MiniMaxH3FantasticRefModTextEncode";
    public const string LibraryRoute = "minimax_h3/refmods";
    private const string UploadFolder = "lumibelle/refmod-trials";
    public HttpClient Client(string server)
    {
        var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new(AiProviderRegistry.NormalizeComfyUrl(server) + "/");
        http.Timeout = Timeout.InfiniteTimeSpan; return http;
    }
    public async Task CheckAsync(string server, string vaeName, CancellationToken ct)
    {
        using var http = Client(server);
        using var response = await http.GetAsync("object_info", ct);
        response.EnsureSuccessStatusCode();
        using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (Inspect(catalog.RootElement) is { } issue) throw new WorkspaceStoreException(issue);
        try
        {
            var names = catalog.RootElement.GetProperty("VAELoader").GetProperty("input").GetProperty("required").GetProperty("vae_name")[0];
            if (!names.EnumerateArray().Any(n => n.GetString() == vaeName))
                throw new WorkspaceStoreException("The selected H3 video VAE is not installed on this ComfyUI server.");
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new WorkspaceStoreException("ComfyUI returned an incompatible VAE catalog. Refresh Video models.", e); }
    }
    public async Task<bool> ReferenceAvailableAsync(ReelRefModReference reference, CancellationToken ct)
    {
        ReelRefMods.Validate(reference);
        using var http = Client(reference.ComfyUrl);
        using var response = await http.GetAsync(LibraryRoute, ct);
        if (!response.IsSuccessStatusCode)
            throw new WorkspaceStoreException("The Fantastic RefMod library could not be read. Check the server/connection and restart ComfyUI after installing the pack. No build was submitted.");
        using var library = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ReferenceAvailable(library.RootElement, reference);
    }
    public static bool ReferenceAvailable(JsonElement library, ReelRefModReference reference)
    {
        ReelRefMods.Validate(reference);
        if (library.ValueKind != JsonValueKind.Object || !library.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new WorkspaceStoreException("Fantastic returned an incompatible RefMod library response.");
        var matches = items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object &&
            i.TryGetProperty("visual", out var v) && v.ValueKind == JsonValueKind.Object &&
            v.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() == reference.FileName).ToArray();
        if (matches.Length != 1) return false;
        var visual = matches[0].GetProperty("visual");
        bool Text(string key, string expected) => visual.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;
        bool Number(string key, int expected) => visual.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == expected;
        return Text("kind", "video") && Text("mode", "encode") && Text("source", "stack") &&
            Number("t", reference.Recipe.LatentFrames) && Number("w", reference.Recipe.Width / 16) && Number("h", reference.Recipe.Height / 16);
    }
    public async Task ValidateReferencesAsync(VideoSnapshot snapshot, CancellationToken ct,
        IReadOnlyDictionary<Guid, ReelRefModReference>? prepared = null)
    {
        var selected = ReelRefMods.ExecutionReferences(snapshot, prepared);
        if (selected.Count == 0) return;
        await CheckAsync(snapshot.ExecutionComfyUrl, snapshot.Settings.VideoVae, ct);
        using var http = Client(snapshot.ExecutionComfyUrl);
        using var response = await http.GetAsync(LibraryRoute, ct);
        if (!response.IsSuccessStatusCode) throw new WorkspaceStoreException("Could not check the Fantastic RefMod library. Check the connection before generating.");
        using var library = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var reference in selected.Values)
            if (!ReferenceAvailable(library.RootElement, reference))
                throw new WorkspaceStoreException("A prepared reference cache disappeared or changed before submission. Start a new generation to rebuild it from the accepted source images. No full-reel fallback will be used.");
    }
    public static async Task<string> UploadPictureAsync(HttpClient http, Guid job, int index, byte[] bytes, CancellationToken ct)
    {
        if (job == Guid.Empty || index < 0 || index >= 9 || bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024)
            throw new WorkspaceStoreException("The prepared RefMod picture is empty, too large, or has no build identity.");
        // Unique build names avoid replacing another request's files. Native uploads may reuse
        // the same bytes after a lost acknowledgement, or return a collision-safe suffix.
        var fileName = $"{job:N}-{index + 1:D2}.png";
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(bytes), "image", fileName);
        body.Add(new StringContent("input"), "type"); body.Add(new StringContent(UploadFolder), "subfolder");
        body.Add(new StringContent("false"), "overwrite");
        using var response = await http.PostAsync("upload/image", body, ct); response.EnsureSuccessStatusCode();
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = receipt.RootElement;
        var name = root.GetProperty("name").GetString();
        var subfolder = root.GetProperty("subfolder").GetString();
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
            !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || subfolder != UploadFolder ||
            root.GetProperty("type").GetString() != "input")
            throw new WorkspaceStoreException("ComfyUI returned an unexpected picture upload path.");
        return subfolder + "/" + name;
    }
    public static object BuildWorkflow(RefModBuildRequest request, IReadOnlyList<string> files, string clientId)
    {
        AiRefModJobHandler.Validate(request);
        return BuildWorkflow(request.ProjectId,
            new(request.Recipe, request.ComfyUrl, ReelRefMods.BuildStem(request.ProjectId, request.JobId), request.JobId), files, clientId);
    }
    public static object BuildWorkflow(Guid project, ReelRefModReference reference, IReadOnlyList<string> files, string clientId)
    {
        ReelRefMods.Validate(reference);
        if (project == Guid.Empty || reference.FileName != ReelRefMods.BuildStem(project, reference.BuildId))
            throw new WorkspaceStoreException("The cache build needs its own project filename.");
        var r = reference.Recipe;
        if (files.Count != r.LatentFrames || files.Any(string.IsNullOrWhiteSpace))
            throw new WorkspaceStoreException("Every captured angle must be uploaded before building the RefMod.");
        return new { client_id = clientId, prompt = new Dictionary<string, object> {
            ["vae"] = new { class_type = "VAELoader", inputs = new { vae_name = r.VaeName } },
            ["build"] = new { class_type = BuildNode, inputs = new {
                name = reference.BuildId.ToString("N"), subfolder = "lumibelle/" + project.ToString("N"),
                mode = "Full Reference", ref_resolution = Math.Min(r.Width, r.Height), grid = 16,
                latent_frames = 22, refinement_steps = 0, max_tokens = ReelRefMods.MaximumTotalTokens,
                audio_max_seconds = 1.0, concept_type = "generic", description = "Lumibelle selected-keyframe visual trial. Voice is separate.",
                write_preview = true, source = JsonSerializer.Serialize(files.Select(file => new { kind = "picture", file })),
                vae = new object[] { "vae", 0 }
            } }
        } };
    }
    public static ReelRefModReference ReadOutput(JsonElement job, RefModBuildRequest request)
    {
        AiRefModJobHandler.Validate(request);
        return ReadOutput(job, new ReelRefModReference(request.Recipe, request.ComfyUrl, ReelRefMods.BuildStem(request.ProjectId, request.JobId), request.JobId));
    }
    public static ReelRefModReference ReadOutput(JsonElement job, ReelRefModReference reference)
    {
        ReelRefMods.Validate(reference);
        var names = job.GetProperty("outputs").GetProperty("build").GetProperty("refmod_saved").EnumerateArray().Select(n => n.GetString()).ToArray();
        var stem = reference.FileName;
        if (names.Count(n => n == stem + ".safetensors") != 1 || names.Distinct().Count() != names.Length ||
            names.Any(n => n != stem + ".safetensors" && n != stem + ".png"))
            throw new WorkspaceStoreException("The completed build did not report its expected visual RefMod file. Inspect the saved request; do not resubmit it blindly.");
        // The Stack's file value is a relative STEM, whereas refmod_saved includes extensions.
        return reference;
    }
    public static string? Inspect(JsonElement catalog)
    {
        const string issue = "Install/update ComfyUI-Fantastic-MiniMaxH3-PromptBuilder, restart ComfyUI, then refresh Video models. No Lumibelle RefMod companion is required.";
        if (catalog.ValueKind != JsonValueKind.Object) return issue;
        bool Has(string type, string field) => catalog.TryGetProperty(type, out var n) && n.TryGetProperty("input", out var input) &&
            new[] { "required", "optional" }.Any(group => input.TryGetProperty(group, out var fields) && fields.TryGetProperty(field, out _));
        JsonElement Port(string type, string field)
        {
            if (!catalog.TryGetProperty(type, out var n) || !n.TryGetProperty("input", out var input)) return default;
            foreach (var group in new[] { "required", "optional" })
                if (input.TryGetProperty(group, out var fields) && fields.TryGetProperty(field, out var value)) return value;
            return default;
        }
        bool Typed(string type, string field, string expected)
        {
            var port = Port(type, field);
            return port.ValueKind == JsonValueKind.Array && port.GetArrayLength() > 0 &&
                port[0].ValueKind == JsonValueKind.String && port[0].GetString() == expected;
        }
        bool Outputs(string type, params string[] expected) => catalog.TryGetProperty(type, out var n) &&
            n.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array &&
            output.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String) && output.EnumerateArray().Select(v => v.GetString()).SequenceEqual(expected);
        if (!new[] { "clip", "prompt", "width", "height", "length", "ref_image_size", "reference_fps", "max_total_tokens", "mods", "references", "vae", "audio_vae" }.All(f => Has(EncodeNode, f)) ||
            !Has(LoadNode, "stack_state") || !Has(MediaNode, "media_state") ||
            !new[] { "name", "subfolder", "mode", "ref_resolution", "grid", "latent_frames", "refinement_steps", "max_tokens", "audio_max_seconds", "concept_type", "description", "write_preview", "source", "vae" }.All(f => Has(BuildNode, f)) ||
            !Outputs(EncodeNode, "CONDITIONING", "STRING", "LATENT") || !Outputs(LoadNode, "H3_REF_MODS", "STRING") ||
            !Outputs(MediaNode, "H3_REFS") || !Outputs(BuildNode, "H3_REF_MODS", "STRING") ||
            !Typed(LoadNode, "stack_state", "STRING") || !Typed(MediaNode, "media_state", "STRING") ||
            !Typed(BuildNode, "source", "STRING") || !Typed(BuildNode, "vae", "VAE") ||
            !Typed(EncodeNode, "clip", "CLIP") || !Typed(EncodeNode, "vae", "VAE") || !Typed(EncodeNode, "audio_vae", "VAE") ||
            !Typed(EncodeNode, "mods", "H3_REF_MODS") || !Typed(EncodeNode, "references", "H3_REFS")) return issue;
        var mode = Port(BuildNode, "mode");
        if (mode.ValueKind != JsonValueKind.Array || mode.GetArrayLength() == 0 || mode[0].ValueKind != JsonValueKind.Array ||
            !mode[0].EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "Full Reference")) return issue;
        return null;
    }
    public static readonly ComfyExecutionOptions BuildOptions = new(new Dictionary<string, ComfyNodeStage> {
        ["vae"] = new(GenerationPhase.Preparing, "Loading the selected H3 video VAE…"),
        ["build"] = new(GenerationPhase.Generating, "Encoding selected frames into one Full visual RefMod…")
    }, "RefMod build rejected. Check Fantastic nodes and the selected H3 VAE.", "RefMod build failed. Inspect the ComfyUI log.",
        "RefMod submission timed out.", "RefMod build timed out.", "Unreadable RefMod response.", "RefMod connection failed.");
}
