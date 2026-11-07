using System.ComponentModel;
using lumibelle.Models;
using lumibelle.Services.Review;
using lumibelle.Services.Story;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static lumibelle.Mcp.McpHosting;

namespace lumibelle.Mcp;

/// <summary>Tools that read a project. None changes anything, runs a model or uses the GPU.</summary>
[McpServerToolType]
public sealed class ReviewReadTools(ReviewQueries queries, ReviewMedia media)
{
    private const string Project = "The project's id, or its exact name.";
    private const string ShotName = "The shot's number in the shot list, such as #31, or its id.";

    [McpServerTool(Name = "list_projects", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists the projects in this Lumibelle library.")]
    public Task<string> ListProjects(CancellationToken ct) => Guard(async () => Text(await queries.ProjectsAsync(ct)));

    [McpServerTool(Name = "list_shots", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists every shot in order: number, title, scene, length, how many takes it has, which is selected, the prompt's review status and how many check findings it has.")]
    public Task<string> ListShots([Description(Project)] string project, CancellationToken ct) =>
        Guard(async () => Text(ReviewQueries.Shots(await queries.SnapshotAsync(project, ct))));

    [McpServerTool(Name = "get_shot", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("One shot as the director sees it: description, dialogue, the prompt, the numbered reference pictures, reels and voices, shot frames taken from other shots, which shots it must render after, and its check findings.")]
    public Task<string> GetShot([Description(Project)] string project, [Description(ShotName)] string shot, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        return Text(ReviewQueries.ShotDetail(snapshot, ReviewQueries.Shot(snapshot, shot)));
    });

    [McpServerTool(Name = "list_takes", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists a shot's takes, oldest first: id, size, length, whether it is the selected take, and what changed in the shot since it was rendered.")]
    public Task<string> ListTakes([Description(Project)] string project, [Description(ShotName)] string shot, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        return Text(ReviewQueries.Takes(snapshot, ReviewQueries.Shot(snapshot, shot)));
    });

    [McpServerTool(Name = "get_take", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("One take and what it was rendered with: the prompt, pictures, voices and shot frames as they were then. These may differ from the shot as it is now.")]
    public Task<string> GetTake([Description(Project)] string project, [Description("The take's id, from list_takes.")] string take, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        return Text(ReviewQueries.TakeDetail(snapshot, ReviewQueries.Take(snapshot, take)));
    });

    [McpServerTool(Name = "get_frames", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Shows a take as one image. With no frame: a contact sheet of frames spread evenly over the take, first and last included, read left to right, top to bottom. With a frame number (counted from 1): that single frame, larger.")]
    public Task<IEnumerable<ContentBlock>> GetFrames([Description(Project)] string project, [Description("The take's id, from list_takes.")] string take,
        [Description("How many frames on the sheet, 1 to 24. Six is usually enough.")] int count = 6,
        [Description("One frame to show on its own, counted from 1. Leave out for a sheet.")] int? frame = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var found = ReviewQueries.Take(snapshot, take);
        if (frame is { } number)
        {
            if (number < 1 || number > found.FrameCount) throw new WorkspaceStoreException($"This take has frames 1 to {found.FrameCount}.");
            return Picture(new { take = found.Id, frame = number, of = found.FrameCount }, await media.FrameAsync(snapshot.ProjectId, found.Id, number - 1, ct: ct));
        }
        return Sheet(await media.TakeSheetAsync(snapshot, found.Id, count, ct: ct));
    });

    [McpServerTool(Name = "get_scene_sheet", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("One image with the first and last frame of each shot's selected take, one row per shot, in shot order. Use it to read a scene from cut to cut. Up to twelve shots a sheet.")]
    public Task<IEnumerable<ContentBlock>> GetSceneSheet([Description(Project)] string project,
        [Description("The first shot, by number or id.")] string from, [Description("The last shot, by number or id. Leave out for the rest of the first shot's scene.")] string? to = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var first = ReviewQueries.Shot(snapshot, from);
        var last = to is null ? snapshot.Rows.Last(r => r.Number >= first.Number && r.Coverage.SceneId == first.Coverage.SceneId &&
            snapshot.Rows.Where(x => x.Number >= first.Number && x.Number <= r.Number).All(x => x.Coverage.SceneId == first.Coverage.SceneId)) : ReviewQueries.Shot(snapshot, to);
        if (last.Number < first.Number) throw new WorkspaceStoreException("Name the earlier shot first.");
        var shots = snapshot.Rows.Where(r => r.Number >= first.Number && r.Number <= last.Number).Select(r => r.Id).ToArray();
        return Sheet(await media.ShotsSheetAsync(snapshot, shots, ct: ct));
    });

    [McpServerTool(Name = "get_neighbour_sheet", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("One image of the cuts around a shot: the previous shot's last frame, this shot's first and last frames, and the next shot's first frame. Compare who is where, the light, costume and props across each cut.")]
    public Task<IEnumerable<ContentBlock>> GetNeighbourSheet([Description(Project)] string project, [Description(ShotName)] string shot,
        [Description("A take of this shot to look at instead of the selected one.")] string? take = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct); var row = ReviewQueries.Shot(snapshot, shot);
        return Sheet(await media.NeighbourSheetAsync(snapshot, row.Id, take is null ? null : ReviewQueries.Take(snapshot, take).Id, ct: ct));
    });

    [McpServerTool(Name = "get_audio_levels", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Measures the soundtrack of selected takes: mean and peak level and silent stretches. It tells you a take is silent or much quieter than the rest; it cannot tell you whose voice is heard or what is said. Decodes each take, so it takes a few seconds per shot.")]
    public Task<string> GetAudioLevels([Description(Project)] string project,
        [Description("The first shot to measure. Leave out for the whole film.")] string? from = null, [Description("The last shot to measure.")] string? to = null, CancellationToken ct = default) => Guard(async () =>
    {
        var snapshot = await queries.SnapshotAsync(project, ct);
        var first = from is null ? 1 : ReviewQueries.Shot(snapshot, from).Number; var last = to is null ? from is null ? int.MaxValue : first : ReviewQueries.Shot(snapshot, to).Number;
        return Text(await queries.AudioLevelsAsync(snapshot, snapshot.Rows.Where(r => r.Number >= first && r.Number <= last).Select(r => r.Id).ToArray(), ct));
    });

    [McpServerTool(Name = "get_cut", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("The cut: each clip with the time it starts, its shot and take, the first and last frames of the take it shows (counted from 1), and whether that take is still the shot's selected take.")]
    public Task<string> GetCut([Description(Project)] string project, CancellationToken ct) => Guard(async () => Text(ReviewQueries.Cut(await queries.SnapshotAsync(project, ct))));

    [McpServerTool(Name = "locate_cut_time", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Turns a time in the cut, as in a director's note such as “3:54, the table disappears”, into the shot, the take and the frame of that take on screen at that moment, counted from 1 as get_frames counts them.")]
    public Task<string> LocateCutTime([Description(Project)] string project, [Description("The time as minutes:seconds, such as 3:54, or as seconds.")] string time, CancellationToken ct) =>
        Guard(async () => Text(ReviewQueries.Locate(await queries.SnapshotAsync(project, ct), time)));

    [McpServerTool(Name = "list_assets", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists characters, environments and props with their pictures and voices, and which shots select each picture.")]
    public Task<string> ListAssets([Description(Project)] string project, CancellationToken ct) => Guard(async () => Text(ReviewQueries.Assets(await queries.SnapshotAsync(project, ct))));

    [McpServerTool(Name = "get_asset_image", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Shows one reference picture from Assets.")]
    public Task<IEnumerable<ContentBlock>> GetAssetImage([Description(Project)] string project, [Description("The asset's id, from list_assets.")] string asset,
        [Description("The image's id, from list_assets.")] string image, CancellationToken ct) => Guard(async () =>
    {
        var found = await queries.ProjectAsync(project, ct); var (assetId, imageId) = Ids(asset, image);
        return Picture(new { asset = assetId, image = imageId }, await queries.AssetImageAsync(found.Id, assetId, imageId, ct: ct));
    });

    [McpServerTool(Name = "find_reference_usage", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Everything made from one reference picture: the shots that select it now, the takes rendered with it, and the takes that took a frame from one of those. Use it before and after a picture is corrected; a frame from an older take keeps carrying the old picture's content.")]
    public Task<string> FindReferenceUsage([Description(Project)] string project, [Description("The asset's id, from list_assets.")] string asset,
        [Description("The image's id, from list_assets.")] string image, CancellationToken ct) => Guard(async () =>
    {
        var (assetId, imageId) = Ids(asset, image);
        return Text(ReviewQueries.Usage(await queries.SnapshotAsync(project, ct), assetId, imageId));
    });

    [McpServerTool(Name = "run_checks", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Runs every continuity check that needs no model: missing references and voices, voices that differ between takes, durations outside the refine range, stage directions stored as dialogue, leftover JSON in prompts, prompts to review, selected takes older than their inputs, shot frames from replaced takes, a cut that ignores the selected takes, and the order shots must render in. Findings are either problems or worth a look.")]
    public Task<string> RunChecks([Description(Project)] string project,
        [Description("Also measure each selected take's soundtrack. Slower: a few seconds per shot.")] bool sound = false, CancellationToken ct = default) =>
        Guard(async () => Text(await queries.ChecksAsync(await queries.SnapshotAsync(project, ct), sound, ct)));

    [McpServerTool(Name = "list_video_options", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists the global generation setups and H3 presets a GenerateTakes step can name, which setup is selected in Shots, what each preset is, and whether its files are installed on ComfyUI. Lumibelle does not rank them.")]
    public Task<string> ListVideoOptions(CancellationToken ct) => Guard(async () => Text(await queries.VideoOptionsAsync(ct)));

    [McpServerTool(Name = "list_image_models", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("Lists the image models an EditImage step can name, which is the project's default, whether each can edit, and how many images it takes. Lumibelle does not rank them.")]
    public Task<string> ListImageModels([Description(Project)] string project, CancellationToken ct) => Guard(async () => Text(await queries.ImageModelsAsync((await queries.ProjectAsync(project, ct)).Id, ct)));

    [McpServerTool(Name = "get_queue", ReadOnly = true, Idempotent = true, OpenWorld = false), Description("The AI queue: requests waiting, running or needing attention. Renders and model requests on ComfyUI share one lane.")]
    public Task<string> GetQueue([Description("Limit the list to one project, by id or exact name.")] string? project = null, CancellationToken ct = default) =>
        Guard(async () => Text(queries.Queue(project is null ? null : (await queries.ProjectAsync(project, ct)).Id)));

    private static (Guid Asset, Guid Image) Ids(string asset, string image) => Guid.TryParse(asset, out var assetId) && Guid.TryParse(image, out var imageId)
        ? (assetId, imageId) : throw new WorkspaceStoreException("Give the asset and image ids from list_assets.");
    private static IEnumerable<ContentBlock> Sheet(ReviewSheet sheet) => Picture(new { columns = sheet.Columns, read = "left to right, top to bottom",
        tiles = sheet.Tiles.Select(t => new { tile = t.Number, shows = t.Label, take = t.TakeId, frame = t.Frame + 1 }).ToArray() }, sheet.Jpeg);
}

/// <summary>The review procedure, offered to clients as a prompt they can run.</summary>
[McpServerPromptType]
public sealed class ReviewPrompts
{
    [McpServerPrompt(Name = "review"), Description("Review a project for continuity errors.")]
    public static string Review([Description("The project's id, or its exact name.")] string project) => $"""
        Review the Lumibelle project “{project}” for continuity errors.

        1. Call run_checks. Report its problems first; they are facts from the project's data.
        2. Read the film scene by scene with get_scene_sheet, and look at each cut you doubt with get_neighbour_sheet.
           Check who is on screen and where, costume, hair, props, the light and the time of day, and which side fixed things such as windows and doors are on.
        3. For each shot that looks wrong, call get_shot and compare the prompt and the reference pictures with what the frames show. Decide whether the cause is the prompt, a missing or wrong reference, or a frame taken from an older take.
        4. Think about the story across shots, not only about matches against the references: where each person was a moment ago, what they could have done in the time, and whether the room still holds what it held. A reference picture can itself be wrong.
        5. List what you found, most serious first, each with the shot number, what the frames show, and the cause you believe. Say what you could not judge: you saw stills, not motion, and heard nothing.

        If the director has notes on a cut, start from those instead: turn each time into a shot with locate_cut_time, then find the cause.
        """;
}
