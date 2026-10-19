using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    [Parameter] public bool ScenesMode { get; set; }
    [Parameter] public Guid? RequestedSceneId { get; set; }
    private Guid? _sceneId, _handledScene;
    private string SceneUrl(Guid? id) => ProjectLink(Id, id is null ? "/scenes" : $"/scenes?sceneId={id}");
    private string SceneExcerpt => Scenes.FirstOrDefault(s => s.Id == _sceneId) is { } scene && _approved is not null
        ? ScriptStructure.Markdown(_approved.Blocks.Skip(scene.Start).Take(scene.Count)) : "";
    private async Task SelectPlanningScene(Guid id)
    {
        _sceneId = id;
        _planningComposerPrepared = false;
        await Remember(Id, "scenes", "scene", id);
        Navigation.NavigateTo(SceneUrl(id), replace: true);
    }
}
