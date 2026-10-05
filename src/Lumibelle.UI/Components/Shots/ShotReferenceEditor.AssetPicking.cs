using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Shots;

public partial class ShotReferenceEditor
{
    [Parameter] public bool AssistPicking { get; set; }
    [Parameter] public string ReferencePrompt { get; set; } = "";
    [Parameter] public string ReferenceDirectingNotes { get; set; } = "";
    [Parameter] public Guid? RequestedAssetPickJobId { get; set; }
    private string? _assistedCatalogueFingerprint, _assistedDirectionFingerprint;
    private void StageAssistedReferences(AssetPickStage stage)
    {
        if (_busy || !AssistPicking || !AllowInference || !AllowReels || ReelAuthoring ||
            stage.ContextFingerprint != AssetPickCatalog.ContextFingerprint(Library.ProjectId, _draft, ReferencePrompt, ReferenceDirectingNotes) ||
            stage.CatalogueFingerprint != AssetPickCatalog.Hash(AssetPickCatalog.Capture(Library)))
            throw new WorkspaceStoreException("The pending reference selection changed. Request a new suggestion before staging it.");
        // Only reference fields are affected. Never replace coverage, dialogue,
        // generation settings, prompt content, or the parent editor's baseline.
        _draft.Images = ShotCopy.Of(stage.Draft.Inputs.Images);
        _draft.Videos = ShotCopy.Of(stage.Draft.Inputs.Videos);
        _draft.Voices = ShotCopy.Of(stage.Draft.Inputs.Voices);
        _draft.CharacterVoices = stage.Draft.Inputs.CharacterVoices is null ? null : ShotCopy.Of(stage.Draft.Inputs.CharacterVoices);
        if (stage.ReplaceExisting) _reelSources.Clear();
        foreach (var source in stage.Draft.ReelSources) _reelSources[source.Key] = source.Value;
        _assistedCatalogueFingerprint = stage.CatalogueFingerprint;
        _assistedDirectionFingerprint = AssetPickCatalog.DirectionFingerprint(_draft, ReferencePrompt, ReferenceDirectingNotes);
        _missing.Clear(); _replacing = _replacingVideo = _expanded = null; _keyframeBinding = null;
        _focusGuidance = _focusCustomize = false; _reelVoiceNotice = null;
        _originalVoiceOwners = CharacterVoices.Owners(_draft, Library).ToHashSet();
        _selectedTab = true; _error = null;
        _copyNotice = [("AI suggestions staged. Review the references and voice mappings, then Apply changes to save; Cancel leaves the shot unchanged.", false)];
    }
}
