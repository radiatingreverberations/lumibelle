using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Story;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Script;

public partial class ScriptAssistantPanel
{
    [Inject] public FileSourceStore Sources { get; set; } = null!;
    [Parameter] public Func<SourceAnalysisRun, Task<bool>>? AddOutlineActs { get; set; }
    private SourceAnalysisRun? _sourcePlan;
    private bool NeedsOutlineActs => AddOutlineActs is not null && _sourcePlan is not null &&
        ScriptAdaptation.WithActHeadings(Document, _sourcePlan).Blocks.Count > Document.Blocks.Count;
    private async Task AddOutlineActsAsync()
    {
        if (_applying || _sourcePlan is null || AddOutlineActs is null) return;
        _applying = true;
        try
        {
            _error = await AddOutlineActs(_sourcePlan) ? null : "The act headings could not be saved. Check the editor’s save status.";
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        finally { _applying = false; }
    }
    private lumibelle.Services.AI.ScriptScenePlan? _scenePlan;
    private bool _scenePlanning;
    private async Task LoadRequestDetailsAsync(AiJobHeader header)
    {
        var request = AiTextJobHandler.Read(header, await JobStore.ReadSnapshotAsync(header.Id, _lifetime.Token));
        var result = await JobStore.ReadArtifactAsync<AiTextJobResult>(header.Id, AiJobArtifact.Result, _lifetime.Token);
        _scenePlan = result?.ScenePlan; _scenePlanning = result?.ScenePlanning == true;
        static string Transcript(IEnumerable<AiTextMessage> messages) => string.Join("\n\n", messages.Select(m => m.Role.ToUpperInvariant() + "\n" + string.Join("\n", m.Parts.Select(p => p.Text))));
        var draft = request.ScenePlanMessages is null ? null : await JobStore.ReadOperationAsync<AiTextJobRequest>(header.Id, "scene-draft", AiOperationArtifact.Request, _lifetime.Token);
        _requestDetails = "Captured model: " + System.Text.Json.JsonSerializer.Serialize(request.Model, AtomicJsonFile.Options) + "\nSelection: " + (request.SelectionSource?.ToString() ?? "Historical captured choice") + "\n\n" +
            (request.ScenePlanMessages is null ? "" : "SEQUENCE PLANNING INPUT\n" + Transcript(request.ScenePlanMessages) + "\n\nDRAFT INPUT\n") +
            Transcript(draft?.Messages ?? request.Messages);
    }
    private string? _sourcePlanError;
    private bool _sourcePlanLoaded, _useSourceOutline, _draftOutlineScene;
    private string _outlineFocus = "";
    private IReadOnlyList<SourceOutlineAct> OutlineActs => (_sourcePlan?.Reviewed ?? _sourcePlan?.Proposal)?.Acts ?? [];
    private int? SelectedOutlineAct => int.TryParse(_outlineFocus.Split(':')[0], out var act) && act >= 0 && act < OutlineActs.Count ? act : null;
    private string OutlineActValue => SelectedOutlineAct?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    private void ChooseOutlineAct(string? value)
    {
        if (!int.TryParse(value, out var act) || act < 0 || act >= OutlineActs.Count) { _outlineFocus = ""; return; }
        var scenes = OutlineScenes.Where(s => s.Act == act).ToArray();
        var next = scenes.FirstOrDefault(s => !s.InScript);
        if (next.Title is null) next = scenes.FirstOrDefault();
        _outlineFocus = next.Title is null ? "" : $"{next.Act}:{next.Scene}";
    }
    private string ActionValue => _draftOutlineScene ? "outline-scene" : Operation.ToString();
    private static string RunAction(AssistantRun run) => run.SourceScene is { } scene ? $"Draft sequence {scene.Act + 1}.{scene.Scene + 1}" : run.Operation.ToString();
    private AssistantRun? OutlineInspectionRun {
        get {
            if (OutlineSelection is not { Act: { } ai, Scene: { } si } choice) return null;
            var reference = new ScriptSourceScene(choice.RunId, choice.ReviewRevision, ai, si);
            var latest = _runs.Where(r => r.SourceScene == reference).OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
            return latest?.Id == _dismissedLatestId ? null : latest;
        }
    }
    private IEnumerable<(int Act, int Scene, string Title, bool InScript)> OutlineScenes => _sourcePlan is null ? [] :
        (_sourcePlan.Reviewed ?? _sourcePlan.Proposal)!.Acts.SelectMany((act, ai) => act.Scenes.Select((scene, si) =>
            (ai, si, scene.Title, ScriptAdaptation.InScript(Document).Contains(new(_sourcePlan.Id, _sourcePlan.ReviewRevision, ai, si)))));
    private ScriptOutlineSelection? OutlineSelection {
        get {
            if (!_useSourceOutline || _sourcePlan is null) return null;
            var pair = _outlineFocus.Split(':');
            return pair.Length == 2 && int.TryParse(pair[0], out var ai) && int.TryParse(pair[1], out var si)
                ? new(_sourcePlan.Id, _sourcePlan.ReviewRevision, ai, si, _draftOutlineScene)
                : new(_sourcePlan.Id, _sourcePlan.ReviewRevision, DraftScene: _draftOutlineScene);
        }
    }
    private bool SourcePlanReady => !_draftOutlineScene || OutlineSelection is { Act: { } ai, Scene: { } si } &&
        !ScriptAdaptation.InScript(Document).Contains(new(_sourcePlan!.Id, _sourcePlan.ReviewRevision, ai, si));
    private ScriptAdaptationContext? OutlineContext => OutlineSelection is { } selection && _sourcePlan is not null
        ? ScriptAdaptation.Context(_sourcePlan, Document, selection) : null;
    private async Task RefreshSourcePlan()
    {
        try {
            var doc = await Sources.LoadAsync(ProjectId, _lifetime.Token);
            var plan = ScriptAdaptation.Accepted(doc);
            var becameAvailable = _sourcePlan is null && plan is not null;
            if (_sourcePlan?.Id != plan?.Id || _sourcePlan?.ReviewRevision != plan?.ReviewRevision) _outlineFocus = "";
            _sourcePlan = plan;
            _sourcePlanError = plan is null && doc.Runs.Count > 0 ? "Accept a current outline in Source to use it here." : null;
            if ((!_sourcePlanLoaded || becameAvailable) && plan is not null) {
                UseSourceOutline(true);
            }
            if (_activeJob is { } active && _runs.FirstOrDefault(r => r.JobId == active.Id)?.SourceScene is { } scene &&
                scene.RunId == plan?.Id && scene.ReviewRevision == plan.ReviewRevision) {
                _useSourceOutline = _draftOutlineScene = true;
                _chosenOperation = null;
                _outlineFocus = $"{scene.Act}:{scene.Scene}";
            }
            if (plan is null) { _useSourceOutline = false; _draftOutlineScene = false; }
            _sourcePlanLoaded = true;
        } catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { _sourcePlanError = e.Message; }
    }
    private void ChooseNextOutlineScene(ScriptSourceScene? justApplied = null)
    {
        var remaining = OutlineScenes.Where(s => !s.InScript && !(justApplied is { } applied && applied.RunId == _sourcePlan?.Id &&
            applied.ReviewRevision == _sourcePlan.ReviewRevision && applied.Act == s.Act && applied.Scene == s.Scene)).ToArray();
        _outlineFocus = remaining.Length > 0 ? $"{remaining[0].Act}:{remaining[0].Scene}" : "";
        if (remaining.Length == 0) _draftOutlineScene = false;
    }
    private void ChooseAction(string? action)
    {
        var wasDraftingSequence = _draftOutlineScene;
        _draftOutlineScene = action == "outline-scene";
        if (_draftOutlineScene) {
            _chosenOperation = null;
            _useSourceOutline = true;
            if ((_outlineFocus.Length == 0 || !SourcePlanReady) && (!HasScript || OutlineScenes.Any(s => s.InScript))) ChooseNextOutlineScene();
        } else if (Enum.TryParse<WritingOperation>(action, out var operation)) {
            _chosenOperation = operation;
            if (operation is WritingOperation.Draft or WritingOperation.Outline) _outlineFocus = "";
            if (wasDraftingSequence && operation is WritingOperation.Revise or WritingOperation.Rewrite)
                FocusSourceForBlocks(Selection is { } selected ? [selected.AnchorBlockId, selected.HeadBlockId] : [], clearWhenUnknown: true);
        }
    }
    private void FocusSourceForBlocks(IEnumerable<Guid> blockIds, bool clearWhenUnknown = false)
    {
        if (!_useSourceOutline || _sourcePlan is null) return;
        var ids = blockIds.ToHashSet();
        var links = Document.SourceSceneLinks.Where(l => l.Scene.RunId == _sourcePlan.Id &&
            l.Scene.ReviewRevision == _sourcePlan.ReviewRevision && l.BlockIds.Any(ids.Contains)).Select(l => l.Scene).Distinct().ToArray();
        if (links.Length == 1) _outlineFocus = $"{links[0].Act}:{links[0].Scene}";
        else if (links.Length > 1 || clearWhenUnknown) _outlineFocus = "";
    }
    private void FocusSourceForTarget(ScriptAssistantTarget? target)
    {
        if (_draftOutlineScene || Operation is not (WritingOperation.Revise or WritingOperation.Rewrite)) return;
        if (target is { Scope: ScriptScope.Scene, SectionId: { } scene }) FocusSourceForBlocks([scene]);
        else if (target is { Scope: ScriptScope.Passage, Passage: { } passage })
            FocusSourceForBlocks(target.PassageBlockIds ?? [passage.AnchorBlockId, passage.HeadBlockId]);
    }
    private void UseSourceOutline(bool use)
    {
        _useSourceOutline = use;
        if (!use) _draftOutlineScene = false;
        else if (_chosenOperation is null or WritingOperation.Draft && (!HasScript || OutlineScenes.Any(s => s.InScript))) {
            _chosenOperation = null;
            _draftOutlineScene = true;
            if (_outlineFocus.Length == 0 || !SourcePlanReady) ChooseNextOutlineScene();
        }
        if (use) FocusSourceForTarget(_target);
    }
}
