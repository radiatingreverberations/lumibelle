using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Story;
using lumibelle.Services.Assets;


namespace lumibelle.Services.AI;

public sealed class FileAiSettingsStore(ApplicationPaths paths, ISecretProtector protection) : IAiSettingsStore
{
    public FileAiSettingsStore(IHostEnvironment environment, ISecretProtector protection) : this(ApplicationPaths.Legacy(environment), protection) { }
    private readonly string _path = paths.Settings;
    private readonly ISecretProtector _protector = protection;
    public async Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(cancellationToken);
        return stored.Settings with { HasOpenRouterKey = !string.IsNullOrEmpty(stored.ProtectedKey) };
    }

    public async Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        settings = settings with { LoraLibrary = LoraPolicy.CopyLibrary(settings.LoraLibrary), H3 = ShotCopy.Of(settings.H3), TextModelProfiles = settings.TextModelProfiles.ToList() };
        using var gate = await ProjectFiles.LockAsync(_path, cancellationToken);
        var current = await ReadAsync(cancellationToken);
        if (current.Settings.Revision != settings.Revision) throw new WorkspaceConflictException();
        var secret = current.ProtectedKey;
        if (removeKey) secret = null;
        else if (!string.IsNullOrWhiteSpace(replacementKey))
        {
            try { secret = _protector.Protect(replacementKey.Trim()); }
            catch (CryptographicException) { throw new WorkspaceStoreException("Couldn’t protect the API key using this account’s key ring. Settings have not been saved."); }
        }
        var saved = settings with { Revision = settings.Revision + 1, HasOpenRouterKey = secret is not null,
            QwenImage21 = settings.QwenImage21 is { } qwen ? QwenImage21Policy.Normalize(qwen) : null,
            LoraLibrary = settings.LoraLibrary.Select(d => d with { Reference = d.Reference with
            { ComfyUrl = AiProviderRegistry.NormalizeComfyUrl(d.Reference.ComfyUrl), Name = d.Reference.Name.Trim() }, TriggerText = d.TriggerText.Trim(), Tags = LoraPolicy.NormalizeTags(d.Tags) }).ToArray(),
            ComfyUrl = settings.ComfyUrl.Trim().TrimEnd('/'), OpenRouterModel = settings.OpenRouterModel.Trim(),
            ComfyImageModel = settings.ComfyImageModel.Trim(), ComfyImageTextEncoder = settings.ComfyImageTextEncoder.Trim(),
            ComfyImageVae = settings.ComfyImageVae.Trim(), ComfyImageEditLora = settings.ComfyImageEditLora.Trim(),
            FluxKleinModel = settings.FluxKleinModel.Trim(), FluxKleinTextEncoder = settings.FluxKleinTextEncoder.Trim(),
            FluxKleinVae = settings.FluxKleinVae.Trim(),
            StarredTextModels = settings.StarredTextModels.Select(TextModelPolicy.Normalize).DistinctBy(TextModelPolicy.Key).ToList(),
            TextModelProfiles = settings.TextModelProfiles.Select(TextModelPolicy.Normalize).ToList(),
            TextDefault = settings.TextDefault is { } selected ? TextModelPolicy.Normalize(selected) : null,
            TextModelAliases = settings.TextModelAliases.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.Ordinal),
            OpenRouterTextModelBenchmarks = settings.OpenRouterTextModelBenchmarks.DistinctBy(b => b.TestId)
                .GroupBy(b => b.Model, StringComparer.Ordinal).SelectMany(g => g.OrderByDescending(b => b.MeasuredUtc).Take(5)).ToList(),
            ComfyTextModelVerifications = settings.ComfyTextModelVerifications
                .Select(item => item with
                {
                    ComfyUrl = AiProviderRegistry.NormalizeComfyUrl(item.ComfyUrl),
                    Benchmarks = item.Benchmarks ?? []
                })
                .GroupBy(item => (item.ComfyUrl, item.ComfyVersion, item.Model))
                .Select(group =>
                {
                    var newest = group.MaxBy(item => item.VerifiedUtc)!;
                    var benchmarks = group.SelectMany(item => item.Benchmarks ?? [])
                        .OrderByDescending(item => item.MeasuredUtc).Take(5).ToList();
                    return newest with { Benchmarks = benchmarks, Capabilities = newest.Capabilities ??
                        group.Where(item => item.Capabilities is not null).MaxBy(item => item.VerifiedUtc)?.Capabilities };
                })
                .OrderByDescending(item => item.VerifiedUtc).ToList() };
        await AtomicJsonFile.WriteAsync(_path, new StoredSettings(1, saved with { HasOpenRouterKey = false }, secret), cancellationToken);
        return saved;
    }

    public async Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(cancellationToken);
        if (stored.ProtectedKey is null) return null;
        try { return _protector.Unprotect(stored.ProtectedKey); }
        catch (CryptographicException)
        { throw new AiGenerationException("This account cannot open the saved OpenRouter key. Replace it in AI settings."); }
    }

    private async Task<StoredSettings> ReadAsync(CancellationToken ct)
    {
        StoredSettings? stored;
        try { stored = await AtomicJsonFile.ReadAsync<StoredSettings>(_path, ct); }
        catch (WorkspaceStoreException e) when (e.InnerException is DirectoryNotFoundException && !File.Exists(Path.GetDirectoryName(_path)))
        { stored = null; }
        if (stored is null) return new(1, new(), null);
        if (stored.SchemaVersion != 1 || stored.Settings is null)
            throw new WorkspaceStoreException("AI settings use an unsupported format. They have not been replaced.");
        Validate(stored.Settings);
        return stored with
        {
            Settings = stored.Settings with
            {
                ComfyTextModelVerifications = stored.Settings.ComfyTextModelVerifications
                    .Select(item => item with { Benchmarks = item.Benchmarks ?? [] }).ToList()
            }
        };
    }

    public static void Validate(AiSettings settings)
    {
        QwenImage21Policy.ValidateSettings(settings.QwenImage21);
        ComfyTextSettings.Validate(settings);
        TextModelProfiles.ValidateSettings(settings);
        if (settings.OpenRouterTextModelBenchmarks is null || settings.OpenRouterTextModelBenchmarks.Any(b => b is null || b.TestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(b.Model) || b.Model.Length > 2000 || b.MeasuredUtc == default || b.TokenLimit is < 1 or > AiModelTestJobHandler.OpenRouterAdvancedMaxTokens ||
            !double.IsFinite(b.ElapsedSeconds) || b.ElapsedSeconds < 0 || b.FirstTextSeconds is { } first && (!double.IsFinite(first) || first < 0 || first > b.ElapsedSeconds) ||
            b.InputTokens is < 0 || b.OutputTokens is < 0 || b.ReasoningTokens is < 0 || b.CachedTokens is < 0 || b.Cost is < 0))
            throw new WorkspaceStoreException("A saved OpenRouter model test is invalid. Run the test again.");
        lumibelle.Services.Shots.H3Policy.ValidateSettings(settings.H3);
        if (settings.Codex is null || settings.Codex.Concurrency is < 1 or > 4 || settings.Codex.ExecutablePath is null ||
            settings.Codex.TextModel is null || settings.Codex.ImageModel is null)
            throw new WorkspaceStoreException("Check Codex settings and concurrency (1-4).");
        if (settings.ClaudeCode is null || settings.ClaudeCode.Concurrency is < 1 or > 4 || settings.ClaudeCode.ExecutablePath is null ||
            settings.ClaudeCode.TextModel is null || settings.ClaudeCode.TextEffort is { } claudeEffort && !ClaudeCodeClient.Efforts.Contains(claudeEffort))
            throw new WorkspaceStoreException("Check Claude Code settings and concurrency (1-4).");
        if (settings.SchemaVersion != 1 || settings.Revision < 0 || !Enum.IsDefined(settings.DefaultBackend) ||
            settings.ComfyModel is null || settings.OpenRouterModel is null || settings.ComfyImageModel is null ||
            settings.ComfyImageTextEncoder is null || settings.ComfyImageVae is null || settings.ComfyImageEditLora is null ||
            !Enum.IsDefined(settings.DefaultImageWorkflow) || settings.FluxKleinModel is null ||
            settings.FluxKleinTextEncoder is null || settings.FluxKleinVae is null ||
            settings.ComfyTextModelVerifications is null || settings.StarredTextModels is null || settings.TextModelAliases is null || LoraPolicy.InvalidLibrary(settings.LoraLibrary) ||
            !Uri.TryCreate(settings.ComfyUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !float.IsFinite(settings.Temperature) || settings.Temperature is < 0.01f or > 2 ||
            settings.MaxOutputTokens is < 1 or > 32768 || settings.TimeoutSeconds is < 5 or > 3600 ||
            settings.ImageTimeoutSeconds is < 30 or > 7200 || settings.OpenRouterConcurrency is < 1 or > 8)
            throw new WorkspaceStoreException("Check the server URL and advanced settings: temperature 0.01–2, output 1–32,768 tokens, text inactivity timeout 5–3,600 seconds, image timeout 30–7,200 seconds, OpenRouter concurrency 1–8.");

        foreach (var model in settings.StarredTextModels) TextModelPolicy.Validate(model);
        if (settings.TextModelAliases.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Trim().Length > 120))
            throw new WorkspaceStoreException("Model aliases must be at most 120 characters.");
        if (settings.LoraLibrary.Any(d => d.Reference.Workflow == LoraWorkflow.MiniMaxH3Ref2VA && LoraPolicy.ReservedH3(d.Reference.FileName, settings.H3)))
            throw new WorkspaceStoreException("H3 acceleration weights belong in Video models, not the optional LoRA library.");
        if (settings.ComfyTextModelVerifications.Any(item => item is null ||
            string.IsNullOrWhiteSpace(item.ComfyVersion) || string.IsNullOrWhiteSpace(item.Model) || item.VerifiedUtc == default ||
            !Uri.TryCreate(item.ComfyUrl, UriKind.Absolute, out var verifiedUri) || verifiedUri.Scheme is not ("http" or "https") ||
            verifiedUri.UserInfo.Length > 0 || verifiedUri.Query.Length > 0 || verifiedUri.Fragment.Length > 0 ||
            item.Capabilities is { } capabilities && !Enum.IsDefined(capabilities.Vision)))
            throw new WorkspaceStoreException("A saved ComfyUI text-model verification record is invalid. Refresh and test the model again.");

        if (settings.ComfyTextModelVerifications.SelectMany(item => item.Benchmarks ?? []).Any(IsInvalidBenchmark))
            throw new WorkspaceStoreException("A saved ComfyUI text-model benchmark is invalid. Run the model test again.");
    }

    private static bool IsInvalidBenchmark(ComfyTextModelBenchmark? benchmark)
    {
        if (benchmark is null || benchmark.MeasuredUtc == default || benchmark.TokenLimit is < 1 or > AiModelTestJobHandler.ComfyAdvancedMaxTokens ||
            benchmark.BytesPerToken is <= 0 || benchmark.ContextTokens is <= 0 ||
            benchmark.GeneratedTokens is < 0 || benchmark.TokensPerSecond is { } rate && (!double.IsFinite(rate) || rate <= 0))
            return true;

        var hasMemory = benchmark.DeviceName is not null || benchmark.DeviceIndex is not null ||
            benchmark.VramTotalBytes is not null || benchmark.BaselineVramUsedBytes is not null || benchmark.PeakVramUsedBytes is not null ||
            benchmark.BaselineTorchAllocatedBytes is not null || benchmark.PeakTorchAllocatedBytes is not null;
        if (!hasMemory) return false;

        return string.IsNullOrWhiteSpace(benchmark.DeviceName) || benchmark.VramTotalBytes is not > 0 ||
            benchmark.BaselineVramUsedBytes is not { } baselineVram || baselineVram < 0 ||
            benchmark.PeakVramUsedBytes is not { } peakVram || peakVram < baselineVram || peakVram > benchmark.VramTotalBytes ||
            benchmark.BaselineTorchAllocatedBytes is < 0 || benchmark.PeakTorchAllocatedBytes is < 0 ||
            benchmark.BaselineTorchAllocatedBytes is { } baselineTorch && benchmark.PeakTorchAllocatedBytes is { } peakTorch && peakTorch < baselineTorch;
    }

    public sealed record StoredSettings(int SchemaVersion, AiSettings Settings, string? ProtectedKey);
}
