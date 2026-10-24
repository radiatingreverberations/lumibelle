using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<TextModelReference> SourceModelAsync(TextModelReference model, CancellationToken ct = default)
    {
        var configured = await settings.LoadAsync(ct);
        model = VllmTextSettings.Apply(TextModelPolicy.WithDefaultEffort(model, configured), configured);
        // CLI providers expose effort, not an output-token control.
        if (!TextModelPolicy.UsesNamedEffort(model.Backend))
            model = model with { MaxOutputTokens = model.MaxOutputTokens ?? Math.Max(4096, (model.ReasoningMaxTokens ?? 0) + 2048) };
        TextModelPolicy.Validate(model);
        return model;
    }
    public async Task<int> SourceBudgetAsync(TextModelReference model, CancellationToken ct = default)
    {
        var configured = await settings.LoadAsync(ct);
        long context = 8192;
        if (providers is not null) {
            var check = await providers.CheckAsync(model.Backend, configured, cancellationToken: ct);
            if (TextModelPolicy.Issue(model, configured, check, true) is { } issue) throw new AiGenerationException(issue);
            context = TextModelPolicy.CatalogModel(model, check)?.Catalog?.ContextLength ?? context;
        }
        var budget = (int)Math.Min(SourceAnalysis.MaximumPromptBudget, context - (model.MaxOutputTokens ?? 4096) - 1024);
        if (budget < 3000) throw new WorkspaceStoreException("This model has too little context available for source analysis. Reduce its reply limit or choose a model with a larger context window.");
        return budget;
    }
    public async Task<AiJobSubmission> SourceAsync(Guid id, Guid tab, SourceAnalysisRequest request, TextModelReference model, int promptBudget, CancellationToken ct = default)
    {
        request = Copy(request);
        if (SourceAnalysis.RequestTokens(request) > promptBudget)
            throw new WorkspaceStoreException("This analysis step exceeds the captured model's context budget. Shorten the adaptation direction or choose a larger model.");
        var result = await BuildAsync(id, tab, AiJobKind.SourceAnalysis, new(request.ProjectId),
            request.Stage == SourceAnalysisStage.Read ? $"Read source · section {request.Inputs[0].Sources[0]}" :
                request.Stage == SourceAnalysisStage.Combine ? "Combine source analysis" : "Propose adaptation outline",
            request, model, false, Copy(await settings.LoadAsync(ct)), SourceAnalysis.Profile, SourceAnalysis.Messages(request), .3f, null, ct);
        var snapshot = result.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        if (ComfyTextCapacity.Assess(snapshot)?.Any(s => s.TooLarge) == true)
            throw new WorkspaceStoreException("This analysis step exceeds the measured model capacity. Choose a model with more room for source analysis.");
        return result;
    }
}
