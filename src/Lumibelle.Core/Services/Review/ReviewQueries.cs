using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>
/// The read side of the review tools: plain answers about a project, in the director's terms. Shots are named by
/// their number in the shot list ("#31") and carry their id. Nothing here changes a project.
/// </summary>
public sealed class ReviewQueries(IProjectStore projects, ProjectReview review, ReviewMedia media, IAssetStore assets, AiJobCoordinator jobs,
    IGenerationSetupStore setups, IVideoGenerator generator, IAiSettingsStore settings, IProjectAiPreferencesStore preferences)
{
    public async Task<object> ProjectsAsync(CancellationToken ct = default) =>
        (await projects.ListAsync(ct)).Projects.Select(p => new { id = p.Id, name = p.Name, description = p.Description }).ToArray();

    /// <summary>A project by id or by its exact name.</summary>
    public async Task<ProjectInfo> ProjectAsync(string project, CancellationToken ct = default)
    {
        var all = (await projects.ListAsync(ct)).Projects;
        if (Guid.TryParse(project, out var id) && all.FirstOrDefault(p => p.Id == id) is { } byId) return byId;
        return all.Where(p => p.Name.Equals(project?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray() is [var named] ? named
            : throw new WorkspaceStoreException("No project has that id or name. Call list_projects for the ids.");
    }

    public async Task<ProjectReviewSnapshot> SnapshotAsync(string project, CancellationToken ct = default) =>
        await review.LoadAsync((await ProjectAsync(project, ct)).Id, ct);

    /// <summary>A shot by number ("#31" or "31") or id.</summary>
    public static ReviewShot Shot(ProjectReviewSnapshot snapshot, string shot)
    {
        var text = (shot ?? "").Trim().TrimStart('#');
        var row = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? snapshot.Rows.FirstOrDefault(r => r.Number == number)
            : Guid.TryParse(text, out var id) ? snapshot.Row(id) : null;
        return row ?? throw new WorkspaceStoreException($"This project has no shot {shot}. It has {snapshot.Rows.Count} shots; name one by number, such as #1, or by id.");
    }

    public static ShotTake Take(ProjectReviewSnapshot snapshot, string take) =>
        Guid.TryParse(take, out var id) && snapshot.Shots.Takes.FirstOrDefault(t => t.Id == id) is { } found ? found
            : throw new WorkspaceStoreException("That take is not in this project. It may be in Trash. Call list_takes for the ids.");

    public static object Shots(ProjectReviewSnapshot snapshot)
    {
        var findings = ReviewChecks.Run(snapshot).Where(f => f.ShotId is not null).ToLookup(f => f.ShotId!.Value);
        return snapshot.Rows.Select(r => new
        {
            number = r.Number, id = r.Id, title = r.Coverage.Title, scene = r.Coverage.SceneTitle, seconds = r.Coverage.Duration,
            takes = r.Takes.Count, selectedTake = r.Coverage.SelectedTakeId, prompt = ProjectReview.PromptCheck(snapshot, r).Label,
            findings = findings[r.Id].Count()
        }).ToArray();
    }

    public static object ShotDetail(ProjectReviewSnapshot snapshot, ReviewShot row)
    {
        var shot = row.Shot; var resolved = ResolvedReferences.For(shot); var order = ShotFrameOrder.Sources(snapshot);
        string Number(Guid id) => "#" + snapshot.Row(id)?.Number;
        return new
        {
            number = row.Number, id = row.Id, title = shot.Title, scene = shot.SceneTitle, seconds = shot.Duration, description = shot.Description,
            atmosphere = shot.Atmosphere, music = shot.Music,
            dialogue = shot.Dialogue.Select(d => new { speaker = d.Speaker, language = d.Language, text = d.Text }).ToArray(),
            characters = ShotReferences.Characters(shot).Select(c => c.Name).ToArray(),
            prompt = row.Prompt, promptStatus = ProjectReview.PromptCheck(snapshot, row).Detail, setup = row.Setup is null ? null : new { id = row.Setup.Id, version = row.Setup.Version, name = row.Setup.Name, takeCount = row.Setup.TakeCount },
            pictures = resolved.Pictures.Select(p => new
            {
                label = $"<Picture {p.Number}>", name = p.Image?.Name ?? p.Continuity?.Name ?? p.Reel?.Name,
                kind = p.Image is not null ? "asset image" : p.Continuity is not null ? "shot frame" : "reel keyframe",
                asset = p.Image is { } image ? snapshot.Assets.Assets.FirstOrDefault(a => a.Id == image.AssetId)?.Name : null,
                assetId = p.Image?.AssetId, imageId = p.Image?.MediaId, role = p.Image?.Role,
                // Frames are counted from 1 here, as propose_step and get_frames count them.
                fromTake = p.Continuity?.TakeId, frame = p.Continuity?.Frame + 1,
                fromShot = p.Continuity is { } frame && snapshot.OwnerOf(frame.TakeId) is { } owner ? "#" + owner.Number : null
            }).ToArray(),
            reels = resolved.Videos.Select(v => new { label = $"<Video {v.Number}>", name = v.Reel.Name, seconds = v.Reel.Media.Duration, soundtrack = v.Reel.UseSoundtrack }).ToArray(),
            audio = resolved.Audio.Select(a => new { label = $"<Audio {a.Number}>", speaker = a.Speaker, source = a.SourceName ?? snapshot.Assets.Voices.FirstOrDefault(v => v.Id == a.Voice?.VoiceId)?.Name }).ToArray(),
            characterVoices = (shot.CharacterVoices ?? []).Select(c => new { character = c.CharacterName, voice = CharacterVoices.Label(c), speaker = c.Speaker }).ToArray(),
            startFrame = shot.StartFrame is { } start ? new { take = start.TakeId, frame = start.Frame + 1, fromShot = snapshot.OwnerOf(start.TakeId) is { } source ? "#" + source.Number : null } : null,
            rendersAfter = order.GetValueOrDefault(row.Id)?.Select(Number).ToArray() ?? [],
            feeds = order.Where(p => p.Value.Contains(row.Id)).Select(p => Number(p.Key)).ToArray(),
            selectedTake = row.Coverage.SelectedTakeId, takes = row.Takes.Count,
            findings = ReviewChecks.Run(snapshot).Where(f => f.ShotId == row.Id).Select(Finding).ToArray()
        };
    }

    public static object Takes(ProjectReviewSnapshot snapshot, ReviewShot row) => row.Takes.Select(t => TakeSummary(snapshot, row, t)).ToArray();

    private static object TakeSummary(ProjectReviewSnapshot snapshot, ReviewShot row, ShotTake take) => new
    {
        id = take.Id, label = TakeDisplay.Label(take), created = take.CreatedUtc, selected = row.Coverage.SelectedTakeId == take.Id,
        size = TakeDisplay.Dimensions(take), frames = take.FrameCount, seconds = Math.Round(take.FrameCount / take.Fps, 3), seed = take.Seed,
        changedSince = Changes(ProjectReview.TakeChanges(snapshot, take))
    };

    public static object TakeDetail(ProjectReviewSnapshot snapshot, ShotTake take)
    {
        var row = snapshot.Row(take.ShotId); var rendered = take.Snapshot.Shot; var resolved = ResolvedReferences.For(rendered);
        return new
        {
            id = take.Id, shot = row is null ? null : "#" + row.Number, shotId = take.ShotId, label = TakeDisplay.Label(take), created = take.CreatedUtc,
            selected = row?.Coverage.SelectedTakeId == take.Id, size = TakeDisplay.Dimensions(take), frames = take.FrameCount, fps = take.Fps, seed = take.Seed,
            changedSince = Changes(ProjectReview.TakeChanges(snapshot, take)),
            // What the take was rendered with, which may differ from the shot as it is now.
            renderedWith = new
            {
                prompt = take.Snapshot.Prompt, seconds = rendered.Duration,
                pictures = resolved.Pictures.Select(p => new { label = $"<Picture {p.Number}>", name = p.Image?.Name ?? p.Continuity?.Name ?? p.Reel?.Name, assetId = p.Image?.AssetId, imageId = p.Image?.MediaId, fromTake = p.Continuity?.TakeId, frame = p.Continuity?.Frame + 1 }).ToArray(),
                voices = rendered.Voices.Select(v => new { speaker = v.Speaker, recording = snapshot.Assets.Voices.FirstOrDefault(x => x.Id == v.VoiceId)?.Name, voiceId = v.VoiceId }).ToArray(),
                reels = rendered.Videos.Select(v => new { name = v.Name, soundtrack = v.UseSoundtrack }).ToArray(),
                startFrame = rendered.StartFrame is { } start ? new { take = start.TakeId, frame = start.Frame + 1 } : null
            }
        };
    }

    private static string[] Changes(TakeInputChange changes) => new[] { (TakeInputChange.Script, "script"), (TakeInputChange.Prompt, "prompt"),
        (TakeInputChange.References, "references"), (TakeInputChange.Unavailable, "could not be compared") }.Where(c => changes.HasFlag(c.Item1)).Select(c => c.Item2).ToArray();

    public static object Finding(ReviewFinding f) => new { check = f.Check, severity = f.Severity == ReviewSeverity.Problem ? "problem" : "worth a look", shot = f.ShotNumber is { } n ? "#" + n : null, shotId = f.ShotId, take = f.TakeId, message = f.Message };

    /// <summary>Every data check, and the sound levels when asked: those decode each selected take with FFmpeg.</summary>
    public async Task<object> ChecksAsync(ProjectReviewSnapshot snapshot, bool sound, CancellationToken ct = default)
    {
        var findings = ReviewChecks.Run(snapshot).ToList();
        if (sound) findings.AddRange(ReviewMedia.LevelFindings(snapshot, await media.AudioLevelsAsync(snapshot, ct: ct)));
        var order = ShotFrameOrder.Build(snapshot);
        return new
        {
            shots = snapshot.Rows.Count, problems = findings.Count(f => f.Severity == ReviewSeverity.Problem), worthALook = findings.Count(f => f.Severity == ReviewSeverity.Look),
            soundChecked = sound,
            checks = findings.GroupBy(f => f.Check).Select(g => new { check = g.Key, title = ReviewChecks.Titles.GetValueOrDefault(g.Key) ?? "Sound levels", count = g.Count(), findings = g.Select(Finding).ToArray() }).ToArray(),
            // Only shots that depend on another, or are depended on, need ordering.
            renderOrder = order.Waves.Select((w, i) => new { wave = i + 1, shots = w.Where(s => order.Sources.ContainsKey(s) || order.Dependants(s).Count > 0).Select(s => "#" + snapshot.Row(s)?.Number).ToArray() }).Where(w => w.shots.Length > 0).ToArray()
        };
    }

    public async Task<object> AudioLevelsAsync(ProjectReviewSnapshot snapshot, IReadOnlyCollection<Guid>? shotIds, CancellationToken ct = default)
    {
        var levels = await media.AudioLevelsAsync(snapshot, shotIds, ct);
        return new
        {
            silenceThresholdDb = AudioLevels.SilenceDb,
            takes = levels.Select(l => new { shot = "#" + snapshot.Row(l.ShotId)?.Number, take = l.TakeId, hasAudio = l.HasAudio, meanDb = l.MeanDb, peakDb = l.PeakDb, seconds = Math.Round(l.Seconds, 3),
                silences = l.Silences.Select(s => new { start = Math.Round(s.Start, 2), end = Math.Round(s.End, 2) }).ToArray() }).ToArray(),
            findings = ReviewMedia.LevelFindings(snapshot, levels).Select(Finding).ToArray()
        };
    }

    public static object Cut(ProjectReviewSnapshot snapshot)
    {
        double start = 0;
        return new
        {
            revision = snapshot.Cut.Revision, seconds = Math.Round(CutTimes.Duration(snapshot.Cut), 3),
            clips = snapshot.Cut.Clips.Select((c, i) =>
            {
                var at = start; start += c.Duration; var row = snapshot.Row(c.ShotId);
                return new { index = i + 1, starts = CutTimes.Format(at), seconds = Math.Round(c.Duration, 3), shot = row is null ? null : "#" + row.Number, shotId = c.ShotId, title = c.ShotTitle,
                    take = c.TakeId, isSelectedTake = row?.Coverage.SelectedTakeId == c.TakeId, frames = new { first = c.StartFrame + 1, last = c.EndFrameExclusive } };
            }).ToArray()
        };
    }

    public static object Locate(ProjectReviewSnapshot snapshot, string time)
    {
        var seconds = CutTimes.Parse(time);
        var found = CutTimes.Locate(snapshot.Cut, seconds) ?? throw new WorkspaceStoreException(snapshot.Cut.Clips.Count == 0 ? "The cut is empty."
            : $"The cut is {CutTimes.Format(CutTimes.Duration(snapshot.Cut))} long; {time} is past its end.");
        var row = snapshot.Row(found.Clip.ShotId);
        return new { time = CutTimes.Format(seconds), clip = found.ClipIndex + 1, shot = row is null ? null : "#" + row.Number, shotId = found.Clip.ShotId, title = found.Clip.ShotTitle,
            take = found.Clip.TakeId, takeFrame = found.TakeFrame + 1, secondsIntoClip = Math.Round(found.SecondsIntoClip, 3), clipStarts = CutTimes.Format(found.ClipStartSeconds),
            // The cut may be older than the picks; a note on it is about the take it shows.
            isSelectedTake = row?.Coverage.SelectedTakeId == found.Clip.TakeId };
    }

    public static object Assets(ProjectReviewSnapshot snapshot) => snapshot.Assets.Assets.Select(a => new
    {
        id = a.Id, name = a.Name, category = a.Category.ToString(), description = a.Description,
        images = a.Images.Where(i => i.PendingReview != true).Select(i => new { id = i.Id, name = i.Name, origin = i.Origin.ToString(), size = $"{i.Width} × {i.Height}", cover = i.IsCover,
            editedFrom = i.Generation?.Edit?.SourceImageId, usedByShots = snapshot.Rows.Where(r => r.Shot.Images.Any(b => b.AssetId == a.Id && b.MediaId == i.Id)).Select(r => "#" + r.Number).ToArray() }).ToArray(),
        voices = snapshot.Assets.Voices.Where(v => v.AssetId == a.Id).Select(v => new { id = v.Id, name = v.Name, isDefault = a.DefaultVoiceId == v.Id }).ToArray()
    }).ToArray();

    public static object Usage(ProjectReviewSnapshot snapshot, Guid assetId, Guid imageId) => ReferenceUsage.Find(snapshot, assetId, imageId)
        .Select(u => new { kind = u.Kind, shot = u.ShotNumber is { } n ? "#" + n : null, shotId = u.ShotId, take = u.TakeId, detail = u.Detail }).ToArray();

    /// <summary>An asset image as a JPEG no larger than <paramref name="side"/> on its long edge.</summary>
    public async Task<byte[]> AssetImageAsync(Guid projectId, Guid assetId, Guid imageId, int side = 1024, CancellationToken ct = default)
    {
        await using var opened = await assets.OpenImageAsync(projectId, assetId, imageId, ct) ?? throw new WorkspaceStoreException("That image is not in Assets. It may be in Trash.");
        using var image = await SixLabors.ImageSharp.Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(opened.Content, ct);
        SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, c => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(c, new SixLabors.ImageSharp.Processing.ResizeOptions { Size = new(side, side), Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max }));
        using var output = new MemoryStream();
        await SixLabors.ImageSharp.ImageExtensions.SaveAsJpegAsync(image, output, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }, ct);
        return output.ToArray();
    }

    public static object Plans(IReadOnlyList<ReviewPlan> plans) => plans.Select(p => new
    {
        id = p.Id, title = p.Title, state = p.State.ToString(), revision = p.Revision, proposedBy = p.ProposedBy, created = p.CreatedUtc,
        findings = p.Findings.Count, steps = p.Steps.Count, waitingForPicks = p.Steps.Count(s => s.State == ReviewStepState.Pick)
    }).ToArray();

    /// <summary>A plan as a proposer or the director reads it. Prompt texts are left out unless one step is asked for.</summary>
    public static object Plan(ProjectReviewSnapshot snapshot, ReviewPlan plan, Guid? stepId = null, string? page = null)
    {
        var totals = ReviewPlans.Totals(plan, snapshot);
        object Step(ReviewPlanStep s, bool full) => new
        {
            id = s.Id, does = ReviewPlans.Describe(snapshot, s), kind = s.Kind.ToString(), shot = s.ShotId is { } shot && snapshot.Row(shot) is { } row ? "#" + row.Number : null,
            state = s.State.ToString(), reason = s.Reason, message = s.Message, findings = s.FindingIds.Count == 0 ? null : s.FindingIds,
            promptBefore = full ? s.Before : null, promptAfter = full ? s.After : null,
            // UseTake: the existing take the step selects, so a plan can be read in full before it is approved.
            takeToUse = s.Kind == ReviewStepKind.UseTake ? s.TakeId : null,
            // AddShotFrame and RemoveShotFrame: which frame, counted from 1 as get_shot and propose_step count it.
            frame = s.Frame is { } f ? new
            {
                take = f.TakeId, fromShot = f.FromShotId is { } from && snapshot.Row(from) is { } source ? "#" + source.Number : null,
                position = f.Position == ReviewFramePosition.Index ? null : f.Position.ToString().ToLowerInvariant(),
                number = f.Position == ReviewFramePosition.Index ? f.Index + 1 : (int?)null, name = string.IsNullOrEmpty(f.Name) ? null : f.Name
            } : null,
            job = s.JobId, takesToPickFrom = s.State == ReviewStepState.Pick && s.Kind != ReviewStepKind.EditImage ? s.ProducedTakeIds : null, pickedTake = s.State == ReviewStepState.Done && s.Kind == ReviewStepKind.GenerateTakes ? s.TakeId : null,
            asset = s.AssetId, image = s.ImageId, fromStep = s.FromStepId,
            imagesToPickFrom = s.State == ReviewStepState.Pick && s.Kind == ReviewStepKind.EditImage ? s.ProducedImageIds : null, pickedImage = s.State == ReviewStepState.Done && s.Kind == ReviewStepKind.EditImage ? s.NewImageId : null,
            recommendedTake = s.RecommendedTakeId, recommendation = s.Recommendation
        };
        if (stepId is { } id) return Step(plan.Step(id) ?? throw new WorkspaceStoreException("That step is not in this plan."), true);
        return new
        {
            id = plan.Id, title = plan.Title, state = plan.State.ToString(), revision = plan.Revision, page,
            // What an approval of this revision agrees to. approve_plan must repeat these exactly.
            totals = new { takes = totals.Takes, footageSeconds = totals.FootageSeconds, changes = totals.Changes, images = totals.Images,
                // The defaults that steps naming none will use. Present only when such a step is still to be approved.
                renderSetup = totals.RenderSetupId, renderSetupName = totals.RenderSetupId is null ? null : snapshot.RenderSetup?.Name, imageModel = totals.ImageModel?.ToString() },
            approvedFrom = plan.ApprovedFrom, approved = plan.ApprovedUtc,
            findings = plan.Findings.Select(f => new { id = f.Id, shot = f.ShotId is { } shot && snapshot.Row(shot) is { } row ? "#" + row.Number : null, message = f.Message, evidence = f.Evidence }).ToArray(),
            steps = plan.Steps.Select(s => Step(s, false)).ToArray(),
            log = plan.Log.TakeLast(20).Select(l => new { at = l.At, who = l.Who, what = l.What }).ToArray()
        };
    }

    /// <summary>The global setups and H3 presets a render can use, with what each is. Facts only: it ranks nothing.</summary>
    public async Task<object> VideoOptionsAsync(CancellationToken ct = default)
    {
        var library = await setups.LoadAsync(ct); var configured = await settings.LoadAsync(ct);
        var check = await generator.CheckAsync(configured, ct);
        var known = check.DiscoverySucceeded ? check.Presets.ToDictionary(p => p.Key) : null;
        return new
        {
            selectedSetup = library.Active()?.Name,
            setups = library.Setups.Where(s => !s.Archived).Select(s => new
            {
                id = s.Id, name = s.Name, preset = s.Settings.GenerationPreset is { } key ? H3Presets.Label(key) : null,
                resolution = s.Settings.Resolution?.ToString() ?? (s.Settings.NativeResolution ? nameof(VideoResolution.Native) : nameof(VideoResolution.Preview)), takes = s.Settings.TakeCount
            }).ToArray(),
            filesChecked = known is not null, filesMessage = known is null ? check.Message : null,
            presets = H3Presets.Offered.Select(key => new
            {
                key, label = H3Presets.Label(key), about = H3Presets.About(key),
                ready = known is null ? (bool?)null : known.TryGetValue(key, out var setup) && setup.Issue is null,
                issue = known?.GetValueOrDefault(key)?.Issue
            }).ToArray(),
            resolutions = Enum.GetNames<VideoResolution>()
        };
    }

    /// <summary>The image models an edit can name, with what each takes. Facts only: it ranks nothing.</summary>
    public async Task<object> ImageModelsAsync(Guid projectId, CancellationToken ct = default)
    {
        var configured = await settings.LoadAsync(ct);
        var current = await ProjectImageDefaults.ResolveAsync(projectId, null, configured, preferences, ct);
        return new
        {
            projectDefault = current.ToString(),
            filesChecked = false,
            note = "Whether a model's files are installed is checked when the edit is queued. A step that names a model without its files fails then.",
            models = Enum.GetValues<ImageWorkflow>().Where(w => w != ImageWorkflow.CodexImages || configured.Codex.Enabled).Select(w => new
            {
                key = w.ToString(), label = w.Label(), canEdit = ImageWorkflowLimits.MaximumReferences(w) > 0, maximumImages = ImageWorkflowLimits.MaximumReferences(w),
                isProjectDefault = w == current, usesPreset = w == ImageWorkflow.MiniMaxH3
            }).ToArray()
        };
    }

    public object Queue(Guid? projectId = null)
    {
        var view = jobs.View; var listed = view.Jobs.Where(j => (projectId is null || j.Target.ProjectId == projectId) && j.State is not (AiJobState.Completed or AiJobState.Cancelled)).ToArray();
        return new
        {
            paused = view.Paused.Select(p => p.ToString()).ToArray(),
            waiting = listed.Count(j => j.State == AiJobState.Waiting), running = listed.Count(j => j.State == AiJobState.Running), needsAttention = listed.Count(j => j.State == AiJobState.NeedsAttention),
            jobs = listed.Select(j => new { id = j.Id, kind = j.Kind.ToString(), state = j.State.ToString(), provider = j.Backend.ToString(), project = j.ProjectName, target = j.TargetName, shotId = j.Target.ShotId, created = j.CreatedUtc, started = j.StartedUtc, cancelling = j.CancelRequested ? true : (bool?)null, progress = jobs.Progress(j.Id)?.Progress is { } p ? p.Current is { } at && p.Maximum is { } of ? $"{p.Label} {at:0}/{of:0}" : p.Label : null }).ToArray()
        };
    }
}
