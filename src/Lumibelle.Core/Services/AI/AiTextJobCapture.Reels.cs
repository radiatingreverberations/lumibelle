using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using lumibelle.Services.Shots;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> ComposeReelAsync(Guid id, Guid tab, Guid project, Guid draftId, long revision,
        TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        var library = await assets.LoadAsync(project, ct);
        var draft = library.ReelDrafts.SingleOrDefault(d => d.Id == draftId)?.Copy() ?? throw new WorkspaceStoreException("Save the reel recipe first.");
        if (draft.Revision != revision || draft.PendingJobId != id) throw new WorkspaceConflictException();
        ReferenceReels.ValidateComposition(draft);
        if (!ReferenceReels.HasVisualReferences(draft)) throw new WorkspaceStoreException("Select visual references to compose a reference reel.");
        if (!TextVisionPolicy.SupportsBackend(model.Backend)) throw new WorkspaceStoreException(TextVisionPolicy.SetupHint);
        var owner = library.Assets.SingleOrDefault(a => a.Id == draft.AssetId) ?? throw new WorkspaceStoreException("Asset unavailable.");
        ReferenceReels.ValidateOwner(draft, owner);
        draft.CapturedVisualStyle = await VisualStyles.CaptureAsync(project, owner, draft.VisualStyle, preferences, ct);
        var character = LookPolicy.Capture(owner, draft.LookId); LookPolicy.ValidateTarget(library, character);
        var inputs = ReferenceReels.Inputs(draft);
        if (inputs.Videos.Count > 0)
            await (referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.")).ValidateAsync(project, inputs.Videos, ct);
        var images = await ProductionInputs.CaptureAsync(project, inputs, assets, ct, referenceVideos, (await settings.LoadAsync(ct)).H3);
        var modFrames = ReelRefMods.Uses(inputs)
            ? await (refmods ?? throw new WorkspaceStoreException("RefMod preview storage is unavailable.")).InspectionAsync(project, inputs, ct)
            : Array.Empty<RefModInspectionFrame>();
        var request = new ReelCompositionRequest(project, draft, ReferenceReels.Fingerprint(draft), character, images.Select(i => i.Identity).ToArray()) { ImageGuidance = ShotReferences.Resolve(inputs, library, new()) };
        var configured = Copy(await settings.LoadAsync(ct));
        var structured = await SupportsStructuredResponseAsync(model, configured, ct);
        return await BuildAsync(id, tab, AiJobKind.ReelComposition, new(project, draft.AssetId, ReelId: draft.Id),
            draft.Name + " · Compose reel", request, model, followsDefault, configured, draft.PresetVersion,
            ReferenceReels.Messages(request, images.Select(i => i.Bytes).ToArray(), modFrames, structured), .7f, request.Baseline, ct,
            responseSchema: structured ? ReferenceReels.ResponseSchemaFor(draft) : null);
    }
}
