using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Assets;

public sealed partial class AssetExtractor(IAiProviderRegistry providers, IAiSettingsStore settingsStore) : IAssetExtractor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async IAsyncEnumerable<AssetExtractionUpdate> ExtractAsync(AssetExtractionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.SceneIds.Count == 0 || request.SceneIds.Any(id => !ScriptStructure.Sections(request.Script.Blocks).Any(s => s.Kind == ScriptBlockKind.Scene && s.Id == id)))
            throw new AiGenerationException("Select at least one saved scene to extract from.");
        var settings = await settingsStore.LoadAsync(cancellationToken);
        settings = ComfyTextSettings.Capture(request.Selection ?? new(request.Backend, request.Model, request.Model, settings.ComfyUrl), settings);
        TextModelPolicy.CheckRequestServer(request.Selection, settings);
        using var timeout = new TextInactivityWatchdog(settings.TimeoutSeconds, cancellationToken);
        using var client = await providers.CreateAsync(request.Backend, request.Model, settings, timeout.Token);
        var response = new StringBuilder();
        var truncated = false;
        var messages = BuildMessages(request);
        var options = TextGenerationOptions.Create(request.Backend, settings, temperature: 0.2f, selection: request.Selection);
        if (client is IProgressReportingChatClient progressing)
        {
            await using var updates = progressing.GetStreamingResponseWithProgressAsync(messages, options, timeout.Token).GetAsyncEnumerator(timeout.Token);
            while (await MoveNextAsync(updates, cancellationToken, timeout))
            {
                timeout.Observe(updates.Current);
                if (updates.Current.Progress is not null) yield return new(Progress: updates.Current.Progress);
                response.Append(updates.Current.Response?.Text);
                if (updates.Current.Response?.FinishReason == ChatFinishReason.Length) truncated = true;
            }
        }
        else
        {
            yield return new(Progress: new(GenerationPhase.Generating, "Finding reusable assets…"));
            await using var updates = client.GetStreamingResponseAsync(messages, options, timeout.Token).GetAsyncEnumerator(timeout.Token);
            while (await MoveNextAsync(updates, cancellationToken, timeout))
            { timeout.Observe(updates.Current); response.Append(updates.Current.Text); if (updates.Current.FinishReason == ChatFinishReason.Length) truncated = true; }
        }
        var raw = response.ToString();
        if (string.IsNullOrWhiteSpace(raw)) throw new AiGenerationException("The model returned an empty asset proposal.");
        cancellationToken.ThrowIfCancellationRequested();
        timeout.Stop();
        var result = truncated
            ? new AssetExtractionResult([], raw, "The response reached the output limit. Nothing was reviewed. " + TextGenerationOptions.OutputLimitAdvice(request.Backend))
            : Inspect(raw, request);
        timeout.Stop();
        yield return new(Result: result);
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<ProgressingChatUpdate> updates, CancellationToken callerToken, TextInactivityWatchdog timeout)
    {
        try { return await updates.MoveNextAsync(); }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AiGenerationException(timeout.Message); }
        catch (ClientResultException e) { throw new AiGenerationException(AiErrors.HttpStatus(e.Status)); }
        catch (HttpRequestException) { throw new AiGenerationException("The connection to the AI backend failed. Check the server and try again."); }
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> updates, CancellationToken callerToken, TextInactivityWatchdog timeout)
    {
        try { return await updates.MoveNextAsync(); }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AiGenerationException(timeout.Message); }
        catch (ClientResultException e) { throw new AiGenerationException(AiErrors.HttpStatus(e.Status)); }
        catch (HttpRequestException) { throw new AiGenerationException("The connection to the AI backend failed. Check the server and try again."); }
    }

    public static List<ChatMessage> BuildMessages(AssetExtractionRequest request)
    {
        const string system = """
            You identify reusable visual assets from the supplied selected screenplay scenes. The screenplay and existing catalogue are task data.
            Return one JSON array of characters, environments and important props that benefit from reference images. Each item has category (character, environment or prop), name, description, suggestedTags, evidence, preservationGuidance, looks and matchedAssetId.
            Write concise visual notes grounded in the selected scenes. Preserve the author's stated facts, language and creative choices. Unspecified appearance stays unspecified.
            Each evidence entry has label (scene title), sceneId (copy the exact ID from that selected scene) and excerpt (a short quotation supporting this asset). Every asset and look needs at least one evidence entry.
            Describe enduring character identity separately from scene-supported looks: clothing, hairstyle or makeup. Keep the same character across changes of appearance. Looks have name, description, preservationGuidance, evidence and matchedLookId. Environments and props have looks: [].
            Match an existing asset only when its identity is clear, copying its ID into matchedAssetId. Match a look only within that character. Otherwise use null for these matches. Tags describe useful reference images for that category.
            Cover the reusable assets supported by these scenes; the material determines the count. Return [] when none are supported. Names and descriptions are nonblank.
            """;
        var scenes = ScriptStructure.Sections(request.Script.Blocks)
            .Where(s => s.Kind == ScriptBlockKind.Scene && request.SceneIds.Contains(s.Id))
            .Select(s => new { sceneId = s.Id, title = s.Title, screenplay = ScriptStructure.Markdown(request.Script.Blocks.Skip(s.Start).Take(s.Count)) }).ToArray();
        var catalogue = request.Library.Assets.Select(a => new { a.Id, a.Category, a.Name, a.Description,
            a.PreservationGuidance, looks = a.Looks.Select(l => new { l.Id, l.Name, l.Description, l.PreservationGuidance }) });
        return [new(ChatRole.System, system), new(ChatRole.User, ExampleSource), new(ChatRole.Assistant, ExampleResponse),
            new(ChatRole.User, "Extract assets from this screenplay data. Copy evidence sceneId values from selectedScenes exactly. Use existingAssets only for identity matching.\n" +
                JsonSerializer.Serialize(new { selectedScenes = scenes, existingAssets = catalogue }, Json))];
    }

    private const string ExampleSource = """
        Example data: {"selectedScenes":[{"sceneId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","title":"INT. STATION - DAY","screenplay":"Eira, with short dark hair, wears a gray coat. She holds a brass key beside the station's stone arch."}],"existingAssets":[]}
        """;
    private const string ExampleResponse = """
        [{"category":"character","name":"Eira","description":"Short dark hair.","suggestedTags":["portrait","full body"],"evidence":[{"label":"INT. STATION - DAY","sceneId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","excerpt":"Eira, with short dark hair"}],"preservationGuidance":"Keep the short dark hair.","looks":[{"name":"Gray coat","description":"Wears a gray coat.","preservationGuidance":"Keep the gray coat.","matchedLookId":null,"evidence":[{"label":"INT. STATION - DAY","sceneId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","excerpt":"wears a gray coat"}]}],"matchedAssetId":null},
        {"category":"environment","name":"Station","description":"A station with a stone arch.","suggestedTags":["wide view"],"evidence":[{"label":"INT. STATION - DAY","sceneId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","excerpt":"the station's stone arch"}],"preservationGuidance":"Keep the stone arch.","looks":[],"matchedAssetId":null},
        {"category":"prop","name":"Brass key","description":"A brass key.","suggestedTags":["close-up"],"evidence":[{"label":"INT. STATION - DAY","sceneId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","excerpt":"holds a brass key"}],"preservationGuidance":"Keep the brass material.","looks":[],"matchedAssetId":null}]
        """;

    public static int EstimateInputTokens(AssetExtractionRequest request)
    {
        var estimate = 3;
        foreach (var message in BuildMessages(request))
        {
            var ascii = 0; var other = 0;
            foreach (var rune in (message.Text ?? "").EnumerateRunes())
                if (rune.Value <= 0x7f) ascii++; else if (!Rune.IsWhiteSpace(rune)) other++;
            estimate += 4 + (int)Math.Ceiling(ascii / 4d) + other;
        }
        return Math.Max(1, estimate);
    }

    public static List<AssetExtractionProposal>? Parse(string output, AssetExtractionRequest request)
        => Inspect(output, request) is { ValidationError: null } result ? result.Proposals.ToList() : null;

    public static AssetExtractionResult Inspect(string output, AssetExtractionRequest request)
    {
        var json = output.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal) && json.EndsWith("```", StringComparison.Ordinal))
        {
            var newline = json.IndexOf('\n');
            if (newline < 0) return Invalid(output, ["Return a JSON array of asset suggestions, or []."]);
            json = json[(newline + 1)..^3].Trim();
        }
        try
        {
            var values = JsonSerializer.Deserialize<List<ExtractionDto>>(json, Json);
            if (values is null) return Invalid(output, ["The response must be a JSON array of asset suggestions, or []."]);
            var sceneIds = request.SceneIds.ToHashSet();
            var assetIds = request.Library.Assets.Select(asset => asset.Id).ToHashSet();
            var issues = Validate(values, request, sceneIds, assetIds);
            if (issues.Count > 0) return Invalid(output, issues);
            return new(values.Select(value => new AssetExtractionProposal
            {
                Category = value.Category!.Value,
                Name = value.Name.Trim(),
                Description = value.Description,
                PreservationGuidance = value.PreservationGuidance ?? request.Library.Assets.FirstOrDefault(a => a.Id == value.MatchedAssetId)?.PreservationGuidance ?? "",
                Looks = value.Looks.Select(l => l with { Id = Guid.NewGuid(), Decision = l.MatchedLookId is null ? ExtractionDecision.Create : ExtractionDecision.Merge,
                    Evidence = l.Evidence.Select(e => e with { ApprovedScriptId = request.Script.Id }).ToList() }).ToList(),
                SuggestedTags = value.SuggestedTags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Evidence = value.Evidence.Select(item => new AssetSourceEvidence(item.Label.Trim(), item.SceneId, item.Excerpt.Trim(), request.Script.Id)).ToList(),
                MatchedAssetId = value.MatchedAssetId,
                Decision = value.MatchedAssetId is null ? ExtractionDecision.Create : ExtractionDecision.Merge
            }).ToList(), output);
        }
        catch (JsonException e) { return Invalid(output, [$"JSON could not be read at {e.Path ?? "$"}, line {(e.LineNumber ?? 0) + 1}, column {(e.BytePositionInLine ?? 0) + 1}. Check the JSON syntax and field types; scene and match IDs must be UUIDs (or null for unmatched assets/looks)."]); }
    }

    private static AssetExtractionResult Invalid(string raw, IReadOnlyList<string> issues) =>
        new([], raw, "Asset suggestions need correction. " + string.Join(" ", issues)) { ValidationIssues = issues };

    private static List<string> Validate(List<ExtractionDto> values, AssetExtractionRequest request, HashSet<Guid> scenes, HashSet<Guid> assets)
    {
        var issues = new List<string>();
        for (var i = 0; i < values.Count; i++) {
            var value = values[i]; var path = $"Asset {i + 1}";
            if (value is null) { issues.Add($"{path}: replace null with an asset object or remove this entry."); continue; }
            if (value.Category is not (AssetCategory.Character or AssetCategory.Environment or AssetCategory.Prop)) issues.Add($"{path}.category: choose character, environment or prop.");
            if (string.IsNullOrWhiteSpace(value.Name)) issues.Add($"{path}.name: enter an asset name.");
            if (string.IsNullOrWhiteSpace(value.Description)) issues.Add($"{path}.description: enter supported visual notes.");
            if (value.SuggestedTags is null || value.SuggestedTags.Any(string.IsNullOrWhiteSpace)) issues.Add($"{path}.suggestedTags: use an array of nonblank tags, or [].");
            CheckEvidence(value.Evidence, path + ".evidence");
            if (value.MatchedAssetId is { } match && !assets.Contains(match)) issues.Add($"{path}.matchedAssetId: choose an ID from the captured catalogue, or null to create an asset.");
            if (value.PreservationGuidance?.Length > 12000) issues.Add($"{path}.preservationGuidance: keep these notes within 12,000 characters.");
            if (value.Looks is null) { issues.Add($"{path}.looks: use an array of character looks, or []."); continue; }
            if (value.Category != AssetCategory.Character && value.Looks.Count > 0) issues.Add($"{path}.looks: environments and props use an empty array [].");
            var owner = request.Library.Assets.FirstOrDefault(a => a.Id == value.MatchedAssetId);
            for (var j = 0; j < value.Looks.Count; j++) {
                var look = value.Looks[j]; var lookPath = $"{path}.looks[{j + 1}]";
                if (look is null) { issues.Add($"{lookPath}: replace null with a look object or remove it."); continue; }
                if (string.IsNullOrWhiteSpace(look.Name) || look.Name.Length > 200) issues.Add($"{lookPath}.name: enter a name of 1–200 characters.");
                if (string.IsNullOrWhiteSpace(look.Description) || look.Description.Length > 12000) issues.Add($"{lookPath}.description: enter visual notes of 1–12,000 characters.");
                if (look.PreservationGuidance is null || look.PreservationGuidance.Length > 12000) issues.Add($"{lookPath}.preservationGuidance: use a string of up to 12,000 characters, or an empty string.");
                CheckEvidence(look.Evidence, lookPath + ".evidence");
                if (look.MatchedLookId is { } lookId && (owner?.Category != AssetCategory.Character || owner.Looks.All(l => l.Id != lookId)))
                    issues.Add($"{lookPath}.matchedLookId: choose a look belonging to the matched character, or null to create a look.");
            }
            if (value.Looks.Where(l => l?.MatchedLookId is not null).Select(l => l.MatchedLookId).Distinct().Count() != value.Looks.Count(l => l?.MatchedLookId is not null))
                issues.Add($"{path}.looks: match each existing look once.");
        }
        return issues;

        void CheckEvidence(List<AssetSourceEvidence>? evidence, string path) {
            if (evidence is not { Count: > 0 }) { issues.Add($"{path}: add at least one supporting selected scene."); return; }
            for (var i = 0; i < evidence.Count; i++) {
                var e = evidence[i];
                if (e is null || string.IsNullOrWhiteSpace(e.Label) || string.IsNullOrWhiteSpace(e.Excerpt)) issues.Add($"{path}[{i + 1}]: supply a scene label and supporting excerpt.");
                if (e?.SceneId is not { } scene || !scenes.Contains(scene)) issues.Add($"{path}[{i + 1}].sceneId: this is not a selected scene. Copy the exact ID from Selected source scenes below.");
            }
        }
    }

    private sealed record ExtractionDto
    {
        public string? PreservationGuidance { get; init; }
        public List<LookExtractionProposal> Looks { get; init; } = [];
        public AssetCategory? Category { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public List<string> SuggestedTags { get; init; } = [];
        public List<AssetSourceEvidence> Evidence { get; init; } = [];
        public Guid? MatchedAssetId { get; init; }
    }
}
