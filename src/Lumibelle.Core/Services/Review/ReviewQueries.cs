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
public sealed class ReviewQueries(IProjectStore projects, ProjectReview review, ReviewMedia media, IAssetStore assets, AiJobCoordinator jobs)
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
                fromTake = p.Continuity?.TakeId, frame = p.Continuity?.Frame,
                fromShot = p.Continuity is { } frame && snapshot.OwnerOf(frame.TakeId) is { } owner ? "#" + owner.Number : null
            }).ToArray(),
            reels = resolved.Videos.Select(v => new { label = $"<Video {v.Number}>", name = v.Reel.Name, seconds = v.Reel.Media.Duration, soundtrack = v.Reel.UseSoundtrack }).ToArray(),
            audio = resolved.Audio.Select(a => new { label = $"<Audio {a.Number}>", speaker = a.Speaker, source = a.SourceName ?? snapshot.Assets.Voices.FirstOrDefault(v => v.Id == a.Voice?.VoiceId)?.Name }).ToArray(),
            characterVoices = (shot.CharacterVoices ?? []).Select(c => new { character = c.CharacterName, voice = CharacterVoices.Label(c), speaker = c.Speaker }).ToArray(),
            startFrame = shot.StartFrame is { } start ? new { take = start.TakeId, frame = start.Frame, fromShot = snapshot.OwnerOf(start.TakeId) is { } source ? "#" + source.Number : null } : null,
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
                pictures = resolved.Pictures.Select(p => new { label = $"<Picture {p.Number}>", name = p.Image?.Name ?? p.Continuity?.Name ?? p.Reel?.Name, assetId = p.Image?.AssetId, imageId = p.Image?.MediaId, fromTake = p.Continuity?.TakeId, frame = p.Continuity?.Frame }).ToArray(),
                voices = rendered.Voices.Select(v => new { speaker = v.Speaker, recording = snapshot.Assets.Voices.FirstOrDefault(x => x.Id == v.VoiceId)?.Name, voiceId = v.VoiceId }).ToArray(),
                reels = rendered.Videos.Select(v => new { name = v.Name, soundtrack = v.UseSoundtrack }).ToArray(),
                startFrame = rendered.StartFrame
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
                    take = c.TakeId, isSelectedTake = row?.Coverage.SelectedTakeId == c.TakeId, frames = new { from = c.StartFrame, toExclusive = c.EndFrameExclusive } };
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
            take = found.Clip.TakeId, takeFrame = found.TakeFrame, secondsIntoClip = Math.Round(found.SecondsIntoClip, 3), clipStarts = CutTimes.Format(found.ClipStartSeconds),
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

    public object Queue(Guid? projectId = null)
    {
        var view = jobs.View; var listed = view.Jobs.Where(j => (projectId is null || j.Target.ProjectId == projectId) && j.State is not (AiJobState.Completed or AiJobState.Cancelled)).ToArray();
        return new
        {
            paused = view.Paused.Select(p => p.ToString()).ToArray(),
            waiting = listed.Count(j => j.State == AiJobState.Waiting), running = listed.Count(j => j.State == AiJobState.Running), needsAttention = listed.Count(j => j.State == AiJobState.NeedsAttention),
            jobs = listed.Select(j => new { id = j.Id, kind = j.Kind.ToString(), state = j.State.ToString(), provider = j.Backend.ToString(), project = j.ProjectName, target = j.TargetName, shotId = j.Target.ShotId, created = j.CreatedUtc, started = j.StartedUtc }).ToArray()
        };
    }
}
