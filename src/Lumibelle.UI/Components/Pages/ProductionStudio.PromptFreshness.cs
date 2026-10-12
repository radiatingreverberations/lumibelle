using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private string _promptStatusFilter = "all";

    // Shots load before their prompts. Until this project's prompts are loaded, a
    // render must not report every shot as missing or unreviewed.
    private bool PromptStatusKnown => _production.ProjectId == Id;
    private PromptReferenceCheck CurrentPromptReferenceCheck => PromptReferenceCheckFor(_selected);
    private void ClearPromptFilters() { _promptStatusFilter = "all"; _filter = ""; }
    private int PromptAttentionCount => _doc.Shots.Count(s => PromptReviewReady(s.Id) || PromptReferenceCheckFor(s.Id).NeedsAttention);
    private bool MatchesPromptStatus(Shot shot) => _promptStatusFilter == "attention" && PromptReviewReady(shot.Id) ||
        PromptReferenceCheckFor(shot.Id).Matches(_promptStatusFilter);

    // Review ready opens the suggestion itself; closing it returns focus to the badge.
    private readonly Dictionary<Guid, ElementReference> _reviewReadyBadges = [];
    private ElementReference? _compositionReturnFocus;
    private async Task ReviewComposedPrompt(Guid shotId)
    {
        if (_selected != shotId) await Select(shotId);
        if (_selected == shotId && _compositionResult is not null && CompositionJob is { } job &&
            _reviewReadyBadges.TryGetValue(shotId, out var badge) && await AiReviews.TryOpenAsync(job, badge, automatic: false))
        { _compositionDialogJob = job.Id; _compositionReturnFocus = badge; return; }
        await ReviewShotPrompt(shotId);
    }

    // An AI-composed prompt waiting to be applied or discarded is the shot's most useful next step.
    private bool PromptReviewReady(Guid shotId)
    {
        var composition = Current is { } current && current.ShotId == shotId ? current
            : _production.Compositions.FirstOrDefault(c => c.ShotId == shotId);
        return composition?.ReviewJobId is { } job && composition.AppliedJobId != job &&
            AiJobs.View.Jobs.Any(j => j.Id == job && j.Kind == AiJobKind.PromptComposition && j.State == AiJobState.Completed);
    }
    private PromptReferenceCheck PromptReferenceCheckFor(Guid? shotId)
    {
        if (!PromptStatusKnown) return PromptReferenceCheck.Checking;
        // Prompt/reference content is shared by all generation setups for a shot.
        // Prefer the open adapter so unsaved edits are reflected immediately.
        var composition = Current is { } current && current.ShotId == shotId ? current
            : _production.Compositions.FirstOrDefault(c => c.ShotId == shotId);
        string? fingerprint = null;
        if (composition is not null && !string.IsNullOrWhiteSpace(composition.Prompt)) {
            try { fingerprint = PromptReferenceFreshness.Fingerprint(composition.Shot, ShotReferences.Resolve(composition.Shot, _assets, _doc)); }
            catch (Exception e) when (e is WorkspaceStoreException or InvalidOperationException) { /* Show review needed, not a false current state. */ }
        }
        return PromptReferenceFreshness.Compare(composition, fingerprint, composition is null ? null : ShotDirections.Fingerprint(_doc, composition.Shot));
    }
}
