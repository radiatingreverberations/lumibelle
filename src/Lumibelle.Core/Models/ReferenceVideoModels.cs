namespace lumibelle.Models;

public sealed record ReferenceVideoMedia(Guid Id, string Sha256, long Bytes, int Width, int Height,
    int Frames, double Fps, double Duration, bool HasAudio);

public sealed record ShotVideoBinding
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OwnerAssetId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public ReferenceVideoMedia Media { get; set; } = null!;
    public string Name { get; set; } = "Character reference";
    public string Description { get; set; } = DefaultDescription;
    public bool UseSoundtrack { get; set; }
    public string? Speaker { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelVisuals? Visuals { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelRefModReference? RefMod { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelKeyframeSet? Keyframes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelAudioExcerpt? AudioExcerpt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AssetCategory? OwnerCategory { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ReelVisuals EffectiveVisuals => Visuals ?? ReelVisuals.FullReel;
    public const string DefaultDescription = "Single-character reference showing appearance from several angles and a clean speaking sample. Use for identity and voice timbre; do not copy the background, camera movement, actions, or spoken words.";
}

public enum VideoInputKind { Image, Video, VideoSoundtrack, Audio, StartFrame }
