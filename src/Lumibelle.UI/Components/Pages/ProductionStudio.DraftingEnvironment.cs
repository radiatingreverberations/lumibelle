using lumibelle.Models;
using lumibelle.Components.Shots;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private SceneEnvironmentPicker? _draftEnvironmentPicker;
    private Guid _draftEnvironmentScene;
    private Guid? _draftEnvironmentShot;
    private string? DraftEnvironmentIssue(Shot shot) => ShotDirections.EnvironmentIssue(_doc, shot) ?? DraftingEnvironments.LiveMetadataIssue(shot.DraftingEnvironment, _assets);
    private ShotDirectionContext? CurrentDirectionContext(Shot shot) => ShotDirections.Context(_doc, shot, DraftEnvironmentIssue(shot));
    private DraftingEnvironmentSetup? SceneEnvironment(Guid scene) => _doc.SceneSetups.FirstOrDefault(s => s.SceneId == scene)?.Environment;
    private string EnvironmentLabel(DraftingEnvironmentSetup? setup) => setup is null ? "No environment · script only" :
        $"{_assets.Assets.FirstOrDefault(a => a.Id == setup.AssetId)?.Name ?? "Unavailable environment"} · {setup.Views.Count} inspection views";
    private async Task OpenDraftEnvironment(Guid sceneId, Shot? shot = null)
    {
        if (Scenes.FirstOrDefault(s => s.Id == sceneId) is not { } scene || _draftEnvironmentPicker is null) return;
        _draftEnvironmentScene = sceneId; _draftEnvironmentShot = shot?.Id;
        _returnToPlanning = _planningAssist?.IsOpen == true || _shotDraftAssist?.IsOpen == true;
        if (shot is null) { if (_planningAssist is not null) await _planningAssist.CloseAsync(); }
        else if (_shotDraftAssist is not null) await _shotDraftAssist.CloseAsync();
        var setup = DraftingEnvironments.Effective(shot is null ? null : Current?.Shot ?? shot, _doc.SceneSetups.FirstOrDefault(s => s.SceneId == sceneId));
        var text = ScriptStructure.Markdown(_approved!.Blocks.Skip(scene.Start).Take(scene.Count));
        await _draftEnvironmentPicker.OpenAsync(sceneId, scene.Title, text, shot is null ? _instructions : _shotDraftDirections, setup, shot?.Id, shot is null ? _model : _shotDraftModel);
    }
    private async Task SaveDraftEnvironment(DraftingEnvironmentSetup? setup)
    {
        Remember();
        if (_draftEnvironmentShot is { } id) {
            var shot = _doc.Shots.Single(s => s.Id == id);
            shot.EnvironmentInspection = ShotCopy.Of(setup);
            shot.EnvironmentInspectionDisabled = setup is null ? true : null;
            shot.EnvironmentAssetId = setup?.AssetId;
            if (Current?.ShotId == id) { Current.Shot.EnvironmentAssetId = setup?.AssetId; Current.Shot.EnvironmentInspection = ShotCopy.Of(setup); Current.Shot.EnvironmentInspectionDisabled = shot.EnvironmentInspectionDisabled; }
        } else {
            var scene = _doc.SceneSetups.FirstOrDefault(s => s.SceneId == _draftEnvironmentScene);
            if (scene is null) { scene = new() { SceneId = _draftEnvironmentScene, SceneTitle = Scenes.Single(s => s.Id == _draftEnvironmentScene).Title }; _doc.SceneSetups.Add(scene); }
            scene.Environment = ShotCopy.Of(setup); scene.Version = Guid.NewGuid();
        }
        CoverageChanged();
        if (!await Save()) throw new WorkspaceStoreException("The environment setup could not be saved. Your edits are retained.");
    }
    private bool _returnToPlanning;
    private Task ReturnToDrafting() => !_returnToPlanning ? Task.CompletedTask : _draftEnvironmentShot is null
        ? _planningAssist?.ResumeAsync() ?? Task.CompletedTask : _shotDraftAssist?.ResumeAsync() ?? Task.CompletedTask;
    private void ReviewEnvironment(IEnumerable<Shot> shots, AcceptedDraftingEnvironment value)
    {
        foreach (var shot in shots) shot.DraftingEnvironment = ShotCopy.Of(value);
        ReviewDirectionChanged();
    }
    private Task ReviewSingleEnvironment(AcceptedDraftingEnvironment value)
    {
        if (ShotDraftProposal is { } shot) shot.DraftingEnvironment = ShotCopy.Of(value);
        return SaveSingleDirectionReview();
    }
}
