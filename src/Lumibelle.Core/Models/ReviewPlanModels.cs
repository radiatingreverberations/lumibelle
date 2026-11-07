using System.Text.Json.Serialization;

namespace lumibelle.Models;

/// <summary>Draft: still being written. Submitted: waiting for the director. Approved: running, or waiting for picks. Finished and Stopped are final.</summary>
public enum ReviewPlanState { Draft, Submitted, Approved, Finished, Stopped }

public enum ReviewStepKind { SetPrompt, MarkPromptReviewed, SetVoice, AddShotFrame, RemoveShotFrame, ReplacePicture, RemovePicture, UseTake, UpdateCut, GenerateTakes, EditImage }

/// <summary>
/// Proposed: waiting for a decision. Rejected: the director said no. Waiting: approved, not run yet. Running: its render is queued or
/// running. Pick: its takes or images are ready and the director has to choose one. Held: the shot changed since the step was written.
/// Skipped: a step it depends on was rejected or failed.
/// </summary>
public enum ReviewStepState { Proposed, Rejected, Waiting, Running, Pick, Done, Failed, Held, Skipped }

public enum ReviewFramePosition { First, Last, Index }

/// <summary>What is wrong, on which shot, and what shows it.</summary>
public sealed record ReviewPlanFinding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? ShotId { get; init; }
    public string Message { get; init; } = "";
    /// <summary>What shows it: quoted prompt text, a frame, a check result, or the director's note and its time in the cut.</summary>
    public string Evidence { get; init; } = "";
    public string ReportedBy { get; init; } = "";
}

/// <summary>A frame of a take that exists, or of the take the director will pick for another shot in the same plan.</summary>
public sealed record ReviewFrameSource
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? TakeId { get; init; }
    /// <summary>The shot whose pick supplies the frame, when its take does not exist yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? FromShotId { get; init; }
    public ReviewFramePosition Position { get; init; } = ReviewFramePosition.Last;
    /// <summary>Zero-based, for <see cref="ReviewFramePosition.Index"/>.</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int Resolve(ShotTake take) => Position switch { ReviewFramePosition.First => 0, ReviewFramePosition.Last => take.FrameCount - 1, _ => Index };
}

/// <summary>One change to the project, or one render. Runs only as part of an approved plan.</summary>
public sealed record ReviewPlanStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ReviewStepKind Kind { get; init; }
    /// <summary>The shot it changes. Null only for the cut.</summary>
    public Guid? ShotId { get; init; }
    public ReviewStepState State { get; init; }
    /// <summary>Why, in the proposer's words.</summary>
    public string Reason { get; init; } = "";
    public IReadOnlyList<Guid> FindingIds { get; init; } = [];
    public string ProposedBy { get; init; } = "";
    /// <summary>Why it failed, is held or was skipped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Message { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? FinishedUtc { get; init; }

    // SetPrompt: the prompt the step was written against, and the prompt it sets.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Before { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? After { get; init; }
    // SetVoice: the character, and the recording to use. No recording means None.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? CharacterAssetId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? VoiceId { get; init; }
    // ReplacePicture and RemovePicture: the picture selected now, and for a replacement the one to select instead.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? AssetId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ImageId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? NewAssetId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? NewImageId { get; init; }
    // AddShotFrame: where the frame comes from. RemoveShotFrame: the take and frame to remove.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ReviewFrameSource? Frame { get; init; }
    // EditImage: the asset image to edit is AssetId and ImageId, the instruction is After, the number of candidates is Takes, and the
    // picked candidate is NewImageId. ReplacePicture: the edit step whose picked image replaces the picture, when it does not exist yet.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? FromStepId { get; init; }
    public IReadOnlyList<Guid> ProducedImageIds { get; init; } = [];
    // UseTake: the take to select.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? TakeId { get; init; }
    // GenerateTakes: how many, at which size, and what came of it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Takes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public VideoResolution? Resolution { get; init; }
    // GenerateTakes: an H3 preset for this render only, leaving the setup as it is. EditImage on MiniMax H3: the preset it runs.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Preset { get; init; }
    // EditImage: the image model, the one named when proposed or else the project's default at approval, and any references after the source.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ImageWorkflow? Workflow { get; init; }
    public IReadOnlyList<AssetImageReference> ReferenceImages { get; init; } = [];
    // The global setup named when proposed, or else the one selected at approval. The approval covers it, so a later change of selection leaves it alone.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? GenerationSetupId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GenerationSetupName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? JobId { get; init; }
    public IReadOnlyList<Guid> ProducedTakeIds { get; init; } = [];
    /// <summary>The take or image the proposer advises picking.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? RecommendedTakeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Recommendation { get; init; }

    [JsonIgnore] public bool Decided => State is not (ReviewStepState.Proposed or ReviewStepState.Rejected);
    [JsonIgnore] public bool Open => State is ReviewStepState.Waiting or ReviewStepState.Running or ReviewStepState.Pick or ReviewStepState.Held;
    [JsonIgnore] public bool SpendsGpu => Kind is ReviewStepKind.GenerateTakes or ReviewStepKind.EditImage;
    /// <summary>The takes or images this step produced, to pick from.</summary>
    [JsonIgnore] public IReadOnlyList<Guid> Produced => Kind == ReviewStepKind.EditImage ? ProducedImageIds : ProducedTakeIds;
}

public sealed record ReviewPlanLogEntry(DateTimeOffset At, string Who, string What);

/// <summary>What approving a plan agrees to: renders, footage, changes to the project and image candidates.</summary>
public sealed record ReviewPlanTotals(int Takes, double FootageSeconds, int Changes, int Images = 0);

/// <summary>Findings and the steps proposed to fix them. Proposing changes nothing; steps run after the director approves.</summary>
public sealed record ReviewPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string ProposedBy { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public ReviewPlanState State { get; init; }
    /// <summary>Counts changes to what is proposed and decided. An approval names the revision it read.</summary>
    public long Revision { get; init; }
    public IReadOnlyList<ReviewPlanFinding> Findings { get; init; } = [];
    public IReadOnlyList<ReviewPlanStep> Steps { get; init; } = [];
    public IReadOnlyList<ReviewPlanLogEntry> Log { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? ApprovedUtc { get; init; }
    /// <summary>Lumibelle, or the name of the client the approval came through.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ApprovedFrom { get; init; }
    [JsonIgnore] public bool Active => State == ReviewPlanState.Approved;
    public ReviewPlanStep? Step(Guid id) => Steps.FirstOrDefault(s => s.Id == id);
}

public sealed record ReviewPlanDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public List<ReviewPlan> Plans { get; set; } = [];
}
