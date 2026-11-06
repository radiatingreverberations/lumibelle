using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Review;

/// <summary>
/// Contact sheets and sound levels for a continuity review. Reads take media with FFmpeg on the CPU;
/// changes nothing, runs no model and uses no GPU.
/// </summary>
public sealed class ReviewMedia(IShotStore shots, ProjectFiles files, IProductionMediaTools media, IAiSettingsStore settings)
{
    public const int MaximumTiles = 24, DefaultTile = 480;

    /// <summary>Frames spread evenly over one take, first and last included.</summary>
    public Task<ReviewSheet> TakeSheetAsync(ProjectReviewSnapshot snapshot, Guid takeId, int frames = 6, int tile = DefaultTile, CancellationToken ct = default)
    {
        var take = snapshot.Shots.Takes.FirstOrDefault(t => t.Id == takeId) ?? throw new WorkspaceStoreException("That take is not in this project. It may be in Trash.");
        var number = snapshot.Row(take.ShotId)?.Number ?? 0;
        frames = Math.Clamp(frames, 1, Math.Min(MaximumTiles, take.FrameCount));
        var indices = frames == 1 ? [0] : Enumerable.Range(0, frames).Select(i => (int)Math.Round(i * (take.FrameCount - 1d) / (frames - 1))).Distinct().ToArray();
        return SheetAsync(snapshot.ProjectId, indices.Select(i => (take, i, $"#{number} frame {i + 1} of {take.FrameCount}")).ToArray(), Math.Min(3, indices.Length), tile, ct);
    }

    /// <summary>The first and last frame of each shot's selected take, in shot order: one row per shot.</summary>
    public Task<ReviewSheet> ShotsSheetAsync(ProjectReviewSnapshot snapshot, IReadOnlyList<Guid> shotIds, int tile = DefaultTile, CancellationToken ct = default)
    {
        var tiles = new List<(ShotTake, int, string)>();
        foreach (var row in snapshot.Rows.Where(r => shotIds.Contains(r.Id)))
        {
            if (row.SelectedTake is not { } take) continue;
            tiles.Add((take, 0, $"#{row.Number} first frame")); tiles.Add((take, take.FrameCount - 1, $"#{row.Number} last frame"));
        }
        if (tiles.Count == 0) throw new WorkspaceStoreException("None of these shots has a selected take.");
        if (tiles.Count > MaximumTiles) throw new WorkspaceStoreException($"A sheet holds up to {MaximumTiles / 2} shots. Ask for fewer shots at a time.");
        return SheetAsync(snapshot.ProjectId, tiles, 2, tile, ct);
    }

    /// <summary>The cut around one shot: the previous shot's last frame, this shot's first and last, the next shot's first.</summary>
    public Task<ReviewSheet> NeighbourSheetAsync(ProjectReviewSnapshot snapshot, Guid shotId, Guid? takeId = null, int tile = DefaultTile, CancellationToken ct = default)
    {
        var row = snapshot.Row(shotId) ?? throw new WorkspaceStoreException("That shot is not in this project.");
        var take = takeId is { } id ? row.Takes.FirstOrDefault(t => t.Id == id) ?? throw new WorkspaceStoreException("That take does not belong to this shot.")
            : row.SelectedTake ?? throw new WorkspaceStoreException("This shot has no selected take. Name the take to look at.");
        var tiles = new List<(ShotTake, int, string)>();
        if (snapshot.Rows.LastOrDefault(r => r.Number < row.Number && r.SelectedTake is not null) is { } previous)
            tiles.Add((previous.SelectedTake!, previous.SelectedTake!.FrameCount - 1, $"#{previous.Number} last frame (previous shot)"));
        tiles.Add((take, 0, $"#{row.Number} first frame")); tiles.Add((take, take.FrameCount - 1, $"#{row.Number} last frame"));
        if (snapshot.Rows.FirstOrDefault(r => r.Number > row.Number && r.SelectedTake is not null) is { } next)
            tiles.Add((next.SelectedTake!, 0, $"#{next.Number} first frame (next shot)"));
        return SheetAsync(snapshot.ProjectId, tiles, Math.Min(4, tiles.Count), tile, ct);
    }

    /// <summary>One frame of a take as a JPEG no larger than <paramref name="side"/> on its long edge.</summary>
    public async Task<byte[]> FrameAsync(Guid projectId, Guid takeId, int frame, int side = 1024, CancellationToken ct = default)
    {
        using var image = await LoadAsync(projectId, takeId, frame, ct);
        image.Mutate(c => c.Resize(new ResizeOptions { Size = new(side, side), Mode = ResizeMode.Max }));
        using var output = new MemoryStream(); await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 85 }, ct); return output.ToArray();
    }

    private async Task<ReviewSheet> SheetAsync(Guid projectId, IReadOnlyList<(ShotTake Take, int Frame, string Label)> tiles, int columns, int tile, CancellationToken ct)
    {
        tile = Math.Clamp(tile, 160, 768); const int gap = 8;
        var first = tiles[0].Take; var height = Math.Max(1, (int)Math.Round(tile * (double)first.Height / first.Width));
        var rows = (tiles.Count + columns - 1) / columns;
        using var sheet = new Image<Rgb24>(columns * (tile + gap) + gap, rows * (height + gap) + gap, new Rgb24(32, 32, 32));
        var map = new List<ReviewSheetTile>();
        for (var i = 0; i < tiles.Count; i++)
        {
            using var frame = await LoadAsync(projectId, tiles[i].Take.Id, tiles[i].Frame, ct);
            frame.Mutate(c => c.Resize(new ResizeOptions { Size = new(tile, height), Mode = ResizeMode.Pad, PadColor = Color.Black }));
            sheet.Mutate(c => c.DrawImage(frame, new Point(gap + i % columns * (tile + gap), gap + i / columns * (height + gap)), 1));
            map.Add(new(i + 1, tiles[i].Label, tiles[i].Take.ShotId, tiles[i].Take.Id, tiles[i].Frame));
        }
        using var output = new MemoryStream(); await sheet.SaveAsJpegAsync(output, new JpegEncoder { Quality = 80 }, ct);
        return new(output.ToArray(), columns, map);
    }

    private async Task<Image<Rgb24>> LoadAsync(Guid projectId, Guid takeId, int frame, CancellationToken ct)
    {
        await using var opened = await shots.OpenAsync(projectId, takeId, ShotTrashKind.Take, frame, ct: ct)
            ?? throw new WorkspaceStoreException("That frame could not be read. The take's video may be missing.");
        return await Image.LoadAsync<Rgb24>(opened.Content, ct);
    }

    /// <summary>The level of each selected take's soundtrack. Takes whose file cannot be read are left out.</summary>
    public async Task<IReadOnlyList<TakeAudioLevel>> AudioLevelsAsync(ProjectReviewSnapshot snapshot, IReadOnlyCollection<Guid>? shotIds = null, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(snapshot.ProjectId, ct); var h3 = (await settings.LoadAsync(ct)).H3;
        var levels = new List<TakeAudioLevel>();
        foreach (var row in snapshot.Rows.Where(r => shotIds is null || shotIds.Contains(r.Id)))
        {
            if (row.SelectedTake is not { } take) continue;
            var path = Path.Combine(directory, "shots", "takes", take.Directory, "video.mp4"); var seconds = take.FrameCount / take.Fps;
            if (!File.Exists(path)) continue;
            VideoFileInfo info;
            try { info = await media.VideoInfoAsync(path, h3, ct); }
            catch (WorkspaceStoreException) { continue; }
            if (!info.HasAudio) { levels.Add(new(row.Id, take.Id, false, null, null, seconds, [])); continue; }
            var measured = await media.AudioLevelsAsync(path, seconds, h3, ct);
            levels.Add(new(row.Id, take.Id, true, measured.MeanDb, measured.PeakDb, seconds, measured.Silences));
        }
        return levels;
    }

    public const string Levels = "audio-levels";
    public const double QuietDb = 10;
    /// <summary>Takes that stand out from the rest of the film by ear: no sound, much quieter than the median, or mostly silent.</summary>
    public static IReadOnlyList<ReviewFinding> LevelFindings(ProjectReviewSnapshot snapshot, IReadOnlyList<TakeAudioLevel> levels)
    {
        var findings = new List<ReviewFinding>();
        // Silent tracks say nothing about how loud the film is.
        var means = levels.Where(l => l.MeanDb is > AudioLevels.DigitalSilenceDb).Select(l => l.MeanDb!.Value).Order().ToArray();
        double? median = means.Length == 0 ? null : means.Length % 2 == 1 ? means[means.Length / 2] : (means[means.Length / 2 - 1] + means[means.Length / 2]) / 2;
        foreach (var level in levels)
        {
            var row = snapshot.Row(level.ShotId);
            void Add(string message) => findings.Add(new(Levels, ReviewSeverity.Look, message) { ShotId = level.ShotId, ShotNumber = row?.Number, TakeId = level.TakeId });
            static string Db(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " dB";
            if (!level.HasAudio) { Add("The selected take has no soundtrack."); continue; }
            if (level.MeanDb is null or <= AudioLevels.DigitalSilenceDb) { Add("The selected take's soundtrack is silent."); continue; }
            if (median is { } middle && means.Length >= 3 && level.MeanDb < middle - QuietDb)
                Add($"The selected take averages {Db(level.MeanDb.Value)}, {Db(middle - level.MeanDb.Value)} quieter than the film's median of {Db(middle)}.");
            if (row?.Coverage.Dialogue.Count > 0 && level.Seconds > 0 && level.SilentSeconds / level.Seconds >= .8)
                Add($"The shot has dialogue, but {level.SilentSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} of the take's {level.Seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} seconds are silent.");
        }
        return findings;
    }
}
