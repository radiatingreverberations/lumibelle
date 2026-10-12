namespace lumibelle.Models;

// Saved identity and membership keep unavailable defaults explainable after a move or discard.
public sealed record PreferredImageReference(Guid ImageId, Guid? LookId, string Name);
public enum ReferenceSetupSource { AssetDefault, SceneSetup }
public enum ShotImageUse { Environment, ObjectAppearance, Composition, ContinuityState, FirstFrame, LastFrame }
public sealed record ReferenceSetupOrigin(ReferenceSetupSource Source, Guid AssetId, Guid? LookId,
    Guid? SceneId, string Version);
public sealed record SceneImageReference
{
    public ShotImageBinding Image { get; set; } = new();
    public Guid? CharacterAssetId { get; set; }
}
public sealed record SceneReferenceSetup
{
    public Guid SceneId { get; set; }
    public string SceneTitle { get; set; } = "";
    public Guid Version { get; set; } = Guid.NewGuid();
    public List<SceneImageReference> Images { get; set; } = [];
}
public sealed record ShotEditState(List<Shot> Shots, List<SceneReferenceSetup> SceneSetups)
{
    public List<DirectionStrategy> DirectionStrategies { get; init; } = [];
}
