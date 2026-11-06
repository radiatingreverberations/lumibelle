namespace lumibelle.Models;

/// <summary>Problem: the data says something is wrong. Look: worth the director's eye, never certain.</summary>
public enum ReviewSeverity { Problem, Look }

/// <summary>One thing a continuity check found. Checks read the project and change nothing.</summary>
public sealed record ReviewFinding(string Check, ReviewSeverity Severity, string Message)
{
    public Guid? ShotId { get; init; }
    /// <summary>The shot's position in the shot list, counted from 1, as the director refers to it.</summary>
    public int? ShotNumber { get; init; }
    public Guid? TakeId { get; init; }
}

/// <summary>A shot as the director sees it: coverage, the shared prompt and references, and its takes.</summary>
public sealed record ReviewShot(int Number, Shot Coverage, ProductionComposition? Setup, IReadOnlyList<ShotTake> Takes)
{
    public Guid Id => Coverage.Id;
    /// <summary>Coverage joined with the shot's prompt inputs and references. Coverage alone before a setup exists.</summary>
    public Shot Shot => Setup?.Shot ?? Coverage;
    public string Prompt => Setup?.Prompt ?? "";
    public ShotTake? SelectedTake => Takes.FirstOrDefault(t => t.Id == Coverage.SelectedTakeId);
}

/// <summary>Everything the checks read, loaded once.</summary>
public sealed record ProjectReviewSnapshot(Guid ProjectId, ShotDocument Shots, ProductionDocument Production, CutDocument Cut, AssetLibrary Assets)
{
    public IReadOnlyList<ReviewShot> Rows { get; init; } = [];
    /// <summary>The current script, or null when it could not be read.</summary>
    public ScriptDocument? Script { get; init; }
    /// <summary>The approved scripts the takes' shots were planned from, by snapshot id.</summary>
    public IReadOnlyDictionary<Guid, ScriptSourceSnapshot> ScriptSources { get; init; } = new Dictionary<Guid, ScriptSourceSnapshot>();
    public ReviewShot? Row(Guid shotId) => Rows.FirstOrDefault(r => r.Id == shotId);
    public ReviewShot? OwnerOf(Guid takeId) => Shots.Takes.FirstOrDefault(t => t.Id == takeId) is { } take ? Row(take.ShotId) : null;
}

/// <summary>The order shots have to be rendered in when one uses a frame from another's take.</summary>
public sealed record ShotRenderOrder(IReadOnlyList<IReadOnlyList<Guid>> Waves, IReadOnlyList<IReadOnlyList<Guid>> Cycles,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Sources)
{
    /// <summary>Shots that take a frame from this one, directly.</summary>
    public IReadOnlyList<Guid> Dependants(Guid shotId) => Sources.Where(p => p.Value.Contains(shotId)).Select(p => p.Key).ToArray();
}

/// <summary>Where a time in the cut falls: which clip, which shot, and which frame of the take.</summary>
public sealed record CutLocation(int ClipIndex, CutClip Clip, double SecondsIntoClip, int TakeFrame, double ClipStartSeconds);

/// <summary>A place a reference image is used.</summary>
public sealed record ReferenceUse(string Kind, Guid? ShotId, int? ShotNumber, Guid? TakeId, string Detail);

public sealed record ReviewSheetTile(int Number, string Label, Guid ShotId, Guid TakeId, int Frame);
/// <summary>A JPEG contact sheet with the map that says what each tile is, read left to right, top to bottom.</summary>
public sealed record ReviewSheet(byte[] Jpeg, int Columns, IReadOnlyList<ReviewSheetTile> Tiles);

/// <summary>Loudness of one take's soundtrack, from FFmpeg's volumedetect and silencedetect filters.</summary>
public sealed record TakeAudioLevel(Guid ShotId, Guid TakeId, bool HasAudio, double? MeanDb, double? PeakDb, double Seconds,
    IReadOnlyList<(double Start, double End)> Silences)
{
    public double SilentSeconds => Silences.Sum(s => s.End - s.Start);
}
