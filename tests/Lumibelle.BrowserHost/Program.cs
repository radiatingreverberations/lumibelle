using lumibelle;
using lumibelle.Services.Shots;
using lumibelle.Services.Production;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Components;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using MudBlazor.Services;
using Microsoft.AspNetCore.DataProtection;

// A separate test executable: real UI and file stores, isolated library, entirely mocked AI.
DisplayCulture.Apply();
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var root = Environment.GetEnvironmentVariable("LUMIBELLE_BROWSER_DATA")
    ?? Path.Combine(Path.GetTempPath(), "Lumibelle.BrowserTests", Guid.NewGuid().ToString("N"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = "Development" });
builder.Logging.ClearProviders(); builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Error);
builder.Services.AddLumibelleWeb(new ApplicationPaths(root, root), exclusive: false);
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddSingleton<IAiSettingsStore, MockSettings>();
builder.Services.AddSingleton<IAiProviderRegistry, MockProviders>();
builder.Services.AddSingleton<IReferenceImageGenerator, MockImages>(); builder.Services.AddSingleton<IReferenceImageEditor, MockImages>();
builder.Services.AddSingleton<MockLoraCatalog>(); builder.Services.AddSingleton<IComfyLoraCatalog>(s => s.GetRequiredService<MockLoraCatalog>());
builder.Services.AddSingleton<IVideoGenerator>(s => new Lumibelle.Testing.MockVideoGenerator(s.GetRequiredService<IAssetStore>(),s.GetRequiredService<IShotStore>(),s.GetRequiredService<IReferenceVideoStore>()) { DelayMilliseconds = 1800, OptionalLoraCatalog = settings => s.GetRequiredService<MockLoraCatalog>().CheckAsync(settings).GetAwaiter().GetResult() });
builder.Services.AddSingleton<MockCapacityState>();
foreach (var backend in new[] { "ComfyUI", "OpenRouter" })
    builder.Services.AddHttpClient(backend, client => client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(s => new MockCapacityHandler(s.GetRequiredService<MockCapacityState>()));
builder.Services.AddSingleton<IModelTestRunner, Lumibelle.Testing.MockModelTestRunner>();
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IAiJobHandler>(builder.Services);
builder.Services.AddSingleton<IAiJobHandler, AiTextJobHandler>();
builder.Services.AddSingleton<IAiJobHandler, AiModelTestJobHandler>();
builder.Services.AddSingleton<IAiJobHandler, CodexImageHandler>();
builder.Services.AddSingleton<IAiJobHandler, Lumibelle.Testing.MockVideoJobHandler>();
builder.Services.AddSingleton<Lumibelle.Testing.MockCodexTransport>(s => new() { Delay = 1200 });
builder.Services.AddSingleton<ICodexTransportFactory>(s => s.GetRequiredService<Lumibelle.Testing.MockCodexTransport>());
builder.Services.AddSingleton<FileProductionStore>();
builder.Services.AddSingleton<ReferenceTestProductionStore>();
builder.Services.AddSingleton<IProductionStore>(s => s.GetRequiredService<ReferenceTestProductionStore>());
builder.Services.AddSingleton<ReferenceTestReelStore>();
builder.Services.AddSingleton<IAssetReelStore>(s => s.GetRequiredService<ReferenceTestReelStore>());
var app = builder.Build();
app.MapLumibelleWeb();
app.MapCutFixtures();
app.MapStorageFixtures();
app.MapReferencePickerFixtures();
app.MapCompositionRecoveryFixtures();
app.MapGet("/fixtures/ai-jobs", async (IAiJobStore jobs) => (await jobs.ReadAsync()).Jobs.Select(j => new { j.Id, j.Target, Kind = j.Kind.ToString(), State = j.State.ToString(), j.Unread, j.CancelRequested, j.ActivityClearedUtc }));
app.MapGet("/fixtures/ai-jobs/{id:guid}/review", async (Guid id, IAiJobReviewStore reviews) => await reviews.LoadAsync(id));
app.MapPost("/fixtures/ai-jobs/{id:guid}/unread", async (Guid id, IAiJobStore jobs, AiJobCoordinator coordinator) => {
    await jobs.UpdateAsync(id, j => j with { Unread = true }); await coordinator.RefreshAsync(); return Results.Ok();
});
app.MapPost("/fixtures/text-queue", async (bool paused, AiJobCoordinator jobs) => { await jobs.SetPausedAsync(AiBackend.OpenRouter, paused); return Results.Ok(); });
app.MapGet("/fixtures/capacity", (MockCapacityState state) => new { state.ComfyChecks, state.RouterChecks });
app.MapGet("/fixtures/codex", (Lumibelle.Testing.MockCodexTransport mock) => new { mock.Starts, mock.Threads, mock.Turns, inputs = mock.Inputs });
app.MapPost("/fixtures/text-catalog", (bool enabled, bool failed, IAiProviderRegistry registry) => { var mock = (MockProviders)registry; mock.ComparisonCatalog = enabled; mock.CatalogFailure = failed; return Results.Ok(); });
app.MapGet("/fixtures/text-catalog", async (IAiProviderRegistry registry, IAiSettingsStore store) => { var settings = await store.LoadAsync(); return new { checks = ((MockProviders)registry).Checks, settings.DefaultBackend, settings.ComfyModel, settings.OpenRouterModel, settings.StarredTextModels, settings.TextModelAliases, settings.ComfyTextModels }; });
app.MapPost("/fixtures/codex-reset", async (IAiSettingsStore settings, IAiJobStore jobs, Lumibelle.Testing.MockCodexTransport mock) => { var current = await settings.LoadAsync(); await settings.SaveAsync(current with { Codex = new(), DefaultImageWorkflow = ImageWorkflow.Krea2, DefaultBackend = AiBackend.OpenRouter, StarredTextModels = current.StarredTextModels.Where(m => m.Backend != AiBackend.Codex).ToList() }); mock.Used = 15; mock.QuotaTurn = false; await jobs.SetPausedAsync(AiBackend.Codex, false); return Results.Ok(); });
app.MapPost("/fixtures/codex-quota", (double used, Lumibelle.Testing.MockCodexTransport mock) => { mock.Used = used; mock.QuotaTurn = used >= 100; mock.Emit("account/rateLimits/updated", new { rateLimits = new { limitId = "codex", limitName = "Codex", primary = new { usedPercent = used, windowDurationMins = 300 } } }); return Results.Ok(); });
app.MapGet("/fixtures/enhancements", () => MockChat.Enhancements.ToArray());
app.MapGet("/fixtures/guidance", () => MockChat.Guidance.ToArray());
app.MapGet("/fixtures/compositions", () => MockChat.Compositions.ToArray());
app.MapGet("/fixtures/reel-compositions", () => MockChat.ReelCompositions.ToArray());
app.MapPost("/fixtures/{id:guid}/regeneration-source", async (Guid id, IAssetStore assets, IAssetReelStore reels, AiReelCapture capture, AiJobCoordinator jobs) => {
    var owner = (await assets.LoadAsync(id)).Assets.First();
    var draft = ReferenceReels.NewDraft(owner); draft.Duration = 5; draft.NativeResolution = false;
    draft.Images = [new() { AssetId = owner.Id, MediaId = owner.Images.First().Id, InferUsage = true }];
    var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
    draft = await reels.SaveDraftAsync(id, draft, 0);
    return await jobs.EnqueueAsync(await capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), id, draft.Id, draft.Revision, 1));
});
app.MapGet("/fixtures/{id:guid}/production", async (Guid id, IProductionStore production) => await production.LoadAsync(id));
app.MapGet("/fixtures/generation-setups", async (IGenerationSetupStore store) => await store.LoadAsync());
app.MapPost("/fixtures/generation-setups/reset", async (ApplicationPaths paths) => {
    await AtomicJsonFile.WriteAsync(Path.Combine(paths.Data, "generation-setups.json"), new GenerationSetupLibrary(), default);
    return Results.Ok();
});
app.MapPost("/fixtures/{id:guid}/legacy-setup-inputs", async (Guid id, IProductionStore production, ProjectFiles files, IAssetStore assets) => {
    var document = await production.InitializeAsync(id); var setup = document.Compositions.First();
    var owner = (await assets.LoadAsync(id)).Assets.First(a => a.Images.Count > 0);
    setup.GenerationSetupId = null; setup.GenerationSetupVersion = 0;
    setup.Prompt = "The earlier default prompt.";
    setup.Shot.Images = [new() { AssetId = owner.Id, MediaId = owner.Images[0].Id, Name = owner.Name }];
    var alternative = setup.Copy(); alternative.Id = Guid.NewGuid(); alternative.Name = "Earlier alternative";
    alternative.Prompt = "A different preserved prompt."; alternative.Shot.Images.Clear(); alternative.Shot.Resolution = VideoResolution.Detail;
    document.Compositions.Add(alternative); document.SchemaVersion = 2;
    await AtomicJsonFile.WriteAsync(Path.Combine(await files.DirectoryAsync(id, default), "production.json"), document, default);
    return Results.Ok();
});
app.MapPost("/fixtures/{id:guid}/take-generation-setup", async (Guid id, IProductionStore production) => {
    var document = await production.InitializeAsync(id); var setup = document.Compositions.First();
    setup.Shot.Aspect = "16:9"; setup.Shot.AspectOverride = "16:9";
    setup.Prompt = H3Policy.Compile(setup.Shot); setup.TakeCount = 2; setup.Seed = 52;
    return await production.SaveAsync(id, setup, setup.Version);
});
app.MapPost("/fixtures/{id:guid}/production-shot", async (Guid id, IShotStore shots, IScriptStore scripts) => {
    var approved = await scripts.LoadApprovedAsync(id); var d = await shots.LoadAsync(id);
    var source = new Shot { Title = "Production test", Description = "Juniper looks up and speaks.", Duration = 1, ApprovedScriptId = approved!.Id, SceneId = approved.Blocks[0].Id,
        SourceBlockIds = approved.Blocks.Select(b => b.Id).ToList(), SourceExcerpt = ScriptStructure.Markdown(approved.Blocks), Dialogue = [new() { Speaker = "JUNIPER", Text = "You called?" }], Atmosphere = "Quiet room" };
    return await shots.SaveAsync(id, [..d.Shots, source], d.Revision);
});
app.MapGet("/fixtures/{id:guid}/preferences", async (Guid id, IProjectAiPreferencesStore store) => await store.LoadAsync(id));
app.MapGet("/fixtures/ai-jobs/{id:guid}/text-selection", async (Guid id, IAiJobStore store) => {
    var job = (await store.ReadAsync()).Jobs.Single(j => j.Id == id);
    var request = AiTextJobHandler.Read(job, await store.ReadSnapshotAsync(id));
    return new { request.Model, SelectionSource = request.SelectionSource?.ToString(), request.FollowsDefault, request.Task, Text = request.Messages.SelectMany(m => m.Parts).Where(p => p.Text is not null).Select(p => p.Text).ToArray() };
});
app.MapPost("/fixtures/{id:guid}/environment-reel-owner", async (Guid id, IAssetStore assets) => {
    var library = await assets.LoadAsync(id); var owner = library.Assets[0];
    return await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == owner.Id ? a with {
        Category = AssetCategory.Environment, Name = "Study", Description = "A study with a desk beneath the window.",
        PreservationGuidance = "Keep the brick wall and the doorway opposite the desk." } : a).ToList() }, library.Revision);
});
app.MapPost("/fixtures/{id:guid}/prop-reel-owner", async (Guid id, IAssetStore assets) => {
    var library = await assets.LoadAsync(id); var owner = library.Assets[0];
    return await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == owner.Id ? a with {
        Category = AssetCategory.Prop, Name = "Armchair", Description = "A green velvet armchair with brass feet.",
        PreservationGuidance = "Keep the buttoned back and curved arms." } : a).ToList() }, library.Revision);
});
app.MapPost("/fixtures/{id:guid}/rename-project", async (Guid id, string name, IProjectStore projects) => {
    var project = await projects.GetAsync(id); return await projects.UpdateAsync(project!, new(name, project!.Description));
});
app.MapGet("/fixtures/{id:guid}/project-route", async (Guid id, IProjectRoutes routes) => await routes.ResolveAsync(id.ToString()));
app.MapGet("/fixtures/new", async (IProjectStore projects) => await projects.CreateAsync(new("Juniper finds a key", "Mocked script walkthrough")));
app.MapGet("/fixtures/{id:guid}/script-source", async (Guid id, IScriptStore scripts) => await scripts.CaptureSourceAsync(id));
app.MapPost("/fixtures/{id:guid}/approved", async (Guid id, IScriptStore scripts) =>
{
    var d = await scripts.LoadAsync(id);
    d = await scripts.SaveAsync(d with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene,"INT. KITCHEN — NIGHT"),ScriptBlock.Create(ScriptBlockKind.Action,"Juniper appears in a saucepan."),ScriptBlock.Create(ScriptBlockKind.Character,"JUNIPER"),ScriptBlock.Create(ScriptBlockKind.Dialogue,"You called?")] }, d.Revision);
    return await scripts.ApproveAsync(id,d.Revision);
});
app.MapPost("/fixtures/{id:guid}/coverage-script", async (Guid id, IScriptStore scripts, string mode = "initial") =>
{
    var d = await scripts.LoadAsync(id);
    if (mode == "initial") d = d with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Act, "ACT 1"), ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "Mira enters the room."), ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. EMPTY FIELD - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "EMPTY_COVERAGE: Nothing reusable appears.")] };
    else if (mode == "reformat") d = d with { Blocks = [d.Blocks[0], d.Blocks[3], d.Blocks[4], d.Blocks[1], d.Blocks[2] with { Spans = [new(d.Blocks[2].Text, true)] }] };
    else { var i = d.Blocks.FindIndex(b => b.Text.StartsWith("Mira enters")); d.Blocks[i] = d.Blocks[i] with { Spans = [new("Mira enters and waves.")] }; }
    d = await scripts.SaveAsync(d, d.Revision);
    if (mode != "draft") await scripts.ApproveAsync(id, d.Revision);
    return await scripts.LoadApprovedAsync(id);
});
app.MapPost("/fixtures/{id:guid}/looks-script", async (Guid id, IScriptStore scripts, bool many = false) =>
{
    var d = await scripts.LoadAsync(id);
    d = await scripts.SaveAsync(d with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. BEDROOM — NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "Juniper has dark eyes and a round face. She wears an Everyday gray hoodie, then changes into white and gold Gala costume armor." + (many ? " UX_REVIEW" : "")), ScriptBlock.Create(ScriptBlockKind.Character, "JUNIPER"), ScriptBlock.Create(ScriptBlockKind.Dialogue, "You called?")] }, d.Revision);
    return await scripts.ApproveAsync(id, d.Revision);
});
app.MapPost("/fixtures/{id:guid}/look-description", async (Guid id, Guid lookId, string description, IAssetStore store) =>
{
    var library = await store.LoadAsync(id);
    return await store.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Looks = a.Looks.Select(l => l.Id == lookId ? l with { Description = description } : l).ToArray() }).ToList() }, library.Revision);
});
app.MapGet("/fixtures/{id:guid}/shots", async (Guid id, IShotStore store) => await store.LoadAsync(id));
app.MapGet("/fixtures/{id:guid}/project", async (Guid id, IProjectStore store) => await store.GetAsync(id));
app.MapPost("/fixtures/{id:guid}/reference-setups", async (Guid id, IAssetStore assets, IShotStore shots, IScriptStore scripts) =>
{
    var library = await assets.LoadAsync(id); var character = library.Assets[0];
    var everyday = new CharacterLook { Name = "Everyday", Description = "Gray hoodie", PreservationGuidance = "Keep the gray fabric." };
    var gala = new CharacterLook { Name = "Gala transformation with elaborate white and gold armor", Description = "White and gold armor", PreservationGuidance = "Keep the gold trim." };
    library = await assets.SaveAsync(library with { Assets = library.Assets.Select(a => a.Id == character.Id ? a with { Looks = [everyday, gala], PreservationGuidance = "Keep her face and dark hair." } : a with { Category = AssetCategory.Environment }).ToList() }, library.Revision);
    foreach (var look in new[] { everyday, gala })
    {
        using var image = new MemoryStream(MockImages.Png());
        library = await assets.AddImageAsync(id, character.Id, image, new(look.Name + ".png", [], AssetImageOrigin.Imported, LookId: look.Id), library.Revision);
    }
    var approved = (await scripts.LoadApprovedAsync(id))!; var scene = ScriptStructure.Sections(approved.Blocks).First(s => s.Kind == ScriptBlockKind.Scene);
    var speaker = Guid.NewGuid();
    var first = new Shot { Title = "Arrival", ApprovedScriptId = approved.Id, SceneId = scene.Id, SceneTitle = scene.Title, Duration = 1, Description = "Juniper looks toward the door.",
        Characters = [new(speaker, "JUNIPER") { Appearance = new(character.Id, everyday.Id) }], Dialogue = [new() { Speaker = "JUNIPER", Text = "You called?" }] };
    var second = first.Copy(); second.Id = Guid.NewGuid(); second.Title = "Transformation";
    second.Characters = [new(Guid.NewGuid(), "JUNIPER") { Appearance = new(character.Id, everyday.Id, gala.Id) }];
    var doc = await shots.LoadAsync(id); await shots.SaveAsync(id, [first, second], doc.Revision);
    return library;
});
app.MapPost("/fixtures/{id:guid}/reel-look-draft", async (Guid id, IAssetStore assets, IAssetReelStore reels) => {
    var owner = (await assets.LoadAsync(id)).Assets.First(a => a.Looks.Count > 0);
    var draft = ReferenceReels.NewDraft(owner, owner.Looks[0].Id); draft.Prompt = "Saved look recipe."; draft.UseGuidance = "Saved guidance.";
    return await reels.SaveDraftAsync(id, draft, 0);
});
app.MapPost("/fixtures/{id:guid}/planning-looks", async (Guid id, IAssetStore store) =>
{
    var library = await store.LoadAsync(id);
    library.Assets.Add(new() { Id = Guid.NewGuid(), Name = "Juniper", Category = AssetCategory.Character, Looks = [new() { Name = "Everyday" }, new() { Name = "Gala costume" }] });
    return await store.SaveAsync(library, library.Revision);
});
app.MapPost("/fixtures/{id:guid}/reference-workspace", async (Guid id, IShotStore shots, IAssetStore assets, IVoiceStore voices, bool silent = false) =>
{
    var library = await assets.LoadAsync(id); var asset = library.Assets[0];
    using var wave = new MemoryStream(); using (var writer = new BinaryWriter(wave, System.Text.Encoding.UTF8, true))
    {
        var bytes = 32000 * 2 * 8;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(32000); writer.Write(64000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
    }
    wave.Position = 0; library = await voices.AddVoiceAsync(id, asset.Id, wave, "voice.wav", "Quiet voice", 0, 3, new(), library.Revision);
    var doc = await shots.LoadAsync(id); var shot = new Shot { Title = "Reference draft", Duration = 1, Description = "A quiet continuous take.",
        Dialogue = silent ? [] : [new() { Speaker = "JUNIPER", Text = "Hello." }],
        Images = [new() { AssetId = asset.Id, MediaId = asset.Images[0].Id, Name = asset.Name }],
        Voices = [new() { AssetId = asset.Id, VoiceId = library.Voices[0].Id, Speaker = "JUNIPER", Start = 0, Duration = 3 }] };
    return await shots.SaveAsync(id, [shot, new() { Title = "Second shot", Duration = 1, Description = "Another quiet take." }], doc.Revision);
});
app.MapPost("/fixtures/{id:guid}/change-shot-reference", async (Guid id, IShotStore store) =>
{
    var doc = await store.LoadAsync(id); doc.Shots[0].Images[0].Role = "Changed in another tab";
    return await store.SaveAsync(id, doc.Shots, doc.Revision);
});
app.MapGet("/fixtures/{id:guid}/video-runs", async (Guid id, IShotStore store, IAiJobStore jobs, AiJobCoordinator queue) =>
{
    var document = await store.LoadAsync(id); var headers = (await jobs.ReadAsync()).Jobs.Where(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == id).ToArray();
    var runs = (await store.RunsAsync(id)).ToList();
    foreach (var job in headers.Where(j => j.Batch?.RootId == j.Id))
        runs.Add(AiVideoBatchReview.Project((await jobs.ReadSnapshotAsync(job.Id)).Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!, headers, document, queue.Progress));
    return runs.OrderByDescending(r => r.CreatedUtc);
});
app.MapPost("/fixtures/lora-file", (string file, bool missing, MockLoraCatalog catalog) => { if (missing) catalog.Missing.Add(file); else catalog.Missing.Remove(file); return Results.Ok(); });
app.MapPost("/fixtures/video-companion", async (bool available, IVideoGenerator video, IAiSettingsStore settings) =>
{
    var mock = (Lumibelle.Testing.MockVideoGenerator)video;
    mock.Catalog = available ? null : (await mock.CheckAsync(await settings.LoadAsync())) with
        { PackageCaptureReady = false, RefinementIssue = "Install the optional Lumibelle H3 companion to enable refinement." };
    return Results.Ok();
});
app.MapPost("/fixtures/video-upscaler", async (bool available, IVideoGenerator video, IAiSettingsStore settings) =>
{
    var mock = (Lumibelle.Testing.MockVideoGenerator)video;
    mock.Catalog = available ? null : (await mock.CheckAsync(await settings.LoadAsync())) with
        { PreviewUpscaling = new(null, "Preview upscaler is unavailable. Refresh Video models after restoring the installed node.") };
    return Results.Ok();
});
app.MapPost("/fixtures/video-preset", async (string key, bool available, IVideoGenerator video, IAiSettingsStore settings) =>
{
    var mock = (Lumibelle.Testing.MockVideoGenerator)video;
    if (available) mock.Catalog = null;
    else
    {
        var catalog = await mock.CheckAsync(await settings.LoadAsync());
        mock.Catalog = catalog with { Presets = catalog.Presets.Select(p => p.Key == key ? p with { Issue = "The selected preset node is unavailable. Install its package and refresh." } : p).ToArray() };
    }
    return Results.Ok();
});
app.MapGet("/fixtures/{id:guid}", async (Guid id, IScriptStore scripts, IAssetStore assets, IAssistantHistoryStore history) =>
    new { script = await scripts.LoadAsync(id), approved = await scripts.LoadApprovedAsync(id), assets = await assets.LoadAsync(id), history = await history.LoadAsync(id) });
app.MapPost("/fixtures/{id:guid}/trash/{imageId:guid}", async (Guid id, Guid imageId, IAssetStore store) =>
{
    var library = await store.LoadAsync(id); var asset = library.Assets.Single(a => a.Images.Any(i => i.Id == imageId));
    return await store.DeleteImageAsync(id, asset.Id, imageId, library.Revision);
});
app.MapPost("/fixtures/{id:guid}/trash-many", async (Guid id, IAssetStore store) =>
{
    var library = await store.LoadAsync(id); var assetId = library.Assets[0].Id; List<Guid> ids = [];
    for (var i = 0; i < 55; i++)
    {
        await using var content = new MemoryStream(MockImages.Png(i % 3));
        library = await store.AddImageAsync(id, assetId, content, new("take.png", ["A long costume description with embroidery and tiny silver buttons"], AssetImageOrigin.Generated, new()), library.Revision);
        ids.Add(library.Assets[0].Images[^1].Id);
    }
    return await store.DeleteImagesAsync(id, assetId, ids, library.Revision);
});
app.MapPost("/fixtures/{id:guid}/touch-assets", async (Guid id, IAssetStore store) =>
{
    var library = await store.LoadAsync(id);
    return await store.SaveAsync(library, library.Revision);
});
app.MapPost("/fixtures/{id:guid}/large", async (Guid id, IScriptStore scripts) =>
{
    var doc = await scripts.LoadAsync(id); List<ScriptBlock> blocks = [];
    for (int i = 1; i <= 160; i++) { blocks.Add(ScriptBlock.Create(ScriptBlockKind.Scene, $"INT. THE EXTRAORDINARILY LONG ROOM NAME {i} — NIGHT")); blocks.Add(ScriptBlock.Create(ScriptBlockKind.Action, string.Join(" ", Enumerable.Repeat("A mouse considers the saucepan.", 12)))); }
    return await scripts.SaveAsync(doc with { Blocks = blocks }, doc.Revision);
});
app.MapPost("/fixtures/{id:guid}/revision-script", async (Guid id, IScriptStore scripts) =>
{
    var doc = await scripts.LoadAsync(id); List<ScriptBlock> blocks = [];
    for (var act = 1; act <= 3; act++)
    {
        blocks.Add(ScriptBlock.Create(ScriptBlockKind.Act, $"ACT {act}"));
        for (var scene = 1; scene <= 2; scene++)
        {
            blocks.Add(ScriptBlock.Create(ScriptBlockKind.Scene, $"INT. ROOM {act}-{scene} — NIGHT"));
            for (var line = 0; line < 8; line++) blocks.Add(ScriptBlock.Create(ScriptBlockKind.Action, $"Action {act}-{scene}-{line}. The mouse waits by the window and watches the rain."));
        }
    }
    return await scripts.SaveAsync(doc with { Blocks = blocks }, doc.Revision);
});
app.MapPost("/fixtures/{id:guid}/request-history", async (Guid id, IScriptStore scripts, IAssistantHistoryStore history) =>
{
    var doc = await scripts.LoadAsync(id); var target = new ScriptTarget(ScriptScope.Document, "Historical whole script", doc.Blocks);
    for (var i = 0; i < 24; i++)
        await history.SaveRunAsync(id, new AssistantRun { CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-i), Operation = WritingOperation.Revise,
            Target = target, AppliedTarget = i == 0 ? new(ScriptScope.Document, "Retargeted captured script", [ScriptBlock.Create(ScriptBlockKind.Action, "Before retargeted application")]) : null,
            Applied = i == 0, Status = i == 1 ? AssistantRunStatus.Failed : AssistantRunStatus.Completed,
            Instructions = $"Request {i}", Output = i == 1 ? "Malformed response for inspection" : "Historical writing", Error = i == 1 ? "Invalid response" : null,
            Proposal = i == 1 ? null : [ScriptBlock.Create(ScriptBlockKind.Action, $"Applied text {i}")], Model = "mock/script" });
    return Results.Ok();
});
app.MapPost("/fixtures/{id:guid}/images", async (Guid id, IAssetStore store, bool patterned = false, bool environment = false) =>
{
    var library = await store.LoadAsync(id); var now = DateTimeOffset.UtcNow;
    var person = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Juniper", CreatedUtc = now, UpdatedUtc = now };
    var outfit = new ReferenceAsset { Id = Guid.NewGuid(), Name = "An extraordinarily long reference name for the embroidered raincoat with silver buttons", Category = environment ? AssetCategory.Environment : AssetCategory.Character, CreatedUtc = now, UpdatedUtc = now };
    library = await store.SaveAsync(library with { Assets = [person, outfit] }, library.Revision);
    foreach (var asset in library.Assets.ToArray())
    {
        await using var image = new MemoryStream(patterned ? MockImages.CropPattern(asset.Id == person.Id ? 64 : 120, 80) : MockImages.Png());
        library = await store.AddImageAsync(id, asset.Id, image, new("reference.png", [asset.Name], AssetImageOrigin.Imported), library.Revision);
    }
    return library;
});
app.MapPost("/fixtures/{id:guid}/ux-images", async (Guid id, IAssetStore store, int count = 100) =>
{
    var library = await store.LoadAsync(id); var asset = library.Assets[0];
    for (var i = 0; i < Math.Clamp(count, 1, 100); i++)
    {
        using var image = new MemoryStream(MockImages.Png());
        library = await store.AddImageAsync(id, asset.Id, image, new($"View {i + 1}.png", [$"view {i + 1}"], AssetImageOrigin.Imported), library.Revision);
    }
    return library;
});
app.Run();

sealed class MockSettings : IAiSettingsStore
{
    private AiSettings _value = new() { H3 = new() { LatentUpscaler = "mock-h3-3d.safetensors" }, DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "mock/script", HasOpenRouterKey = true,
        StarredTextModels = [new(AiBackend.OpenRouter, "mock/alternate", "Alternate mock model")] };
    public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
    public Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("mock-only");
    public Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken cancellationToken = default)
    { if (settings.Revision != _value.Revision) throw new WorkspaceConflictException(); return Task.FromResult(_value = settings with { Revision = settings.Revision + 1 }); }
}
sealed class MockLoraCatalog : IComfyLoraCatalog
{
    public HashSet<string> Missing { get; } = [];
    public Task<ComfyLoraCheck> CheckAsync(AiSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(new ComfyLoraCheck(true, "Mock installed LoRAs refreshed.",
        new[] { "character.safetensors", "styles/ink.safetensors", "klein/style.safetensors", "h3/character.safetensors", "h3/styles/film.safetensors", new H3Settings().Turbo8StepLora }.Concat(Enumerable.Range(1, 50).Select(i => $"training/very-long-character-checkpoint-name-step{i:000000}.safetensors")).Where(file => !Missing.Contains(file)).ToArray()));
}
sealed class MockProviders(ICodexClient codex) : IAiProviderRegistry
{
    public bool ComparisonCatalog { get; set; }
    public bool CatalogFailure { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<AiBackend, int> Checks { get; } = new();
    public Task<IChatClient> CreateAsync(AiBackend backend, string model, AiSettings settings, CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(new MockChat(model, backend));
    public async Task<AiConnectionCheck> CheckAsync(AiBackend backend, AiSettings settings, string? replacementKey = null, CancellationToken cancellationToken = default)
    {
        Checks.AddOrUpdate(backend, 1, (_, count) => count + 1);
        if (backend == AiBackend.OpenRouter) await Task.Delay(200, cancellationToken);
        if (backend == AiBackend.OpenRouter && CatalogFailure) return new(false, "Mock OpenRouter connection unavailable", []);
        if (backend == AiBackend.Codex) { var check = await codex.CheckAsync(settings.Codex, cancellationToken); return new(check.Success, check.Message, check.Models, check.Version); }
        if (backend == AiBackend.OpenRouter && ComparisonCatalog)
        {
            var models = Enumerable.Range(1, 30).Select(i => new AiModel($"catalog/model-{i:D2}", $"Catalog model {i:D2}", SupportsImages: i == 30,
                Catalog: new("A comparison model for writing and editing. Supports longer stories and structured output.", i * 32000, 16384, i == 30,
                    new((31 - i) / 10000000m, i / 10000000m, new Dictionary<string, decimal?> { ["web_search"] = 0.005m }), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i), i))).ToList();
            models.Add(new("catalog/free:free", "Free writing model", Catalog: new("Free text rates subject to usage limits.", 128000, 8192, Pricing: new(0, 0, new Dictionary<string, decimal?>()))));
            models.Add(new("catalog/conditional", "Conditional context model", Catalog: new(ContextLength: 1000000, Pricing: new(0.000001m, 0.000002m, new Dictionary<string, decimal?>(), Conditional: true))));
            models.Add(new("openrouter/auto", "Auto routing", Catalog: new(Pricing: new(null, null, new Dictionary<string, decimal?>(), Variable: true))));
            models.AddRange([new("mock/script", "Mock script writer"), new("mock/alternate", "Alternate mock model", SupportsImages: true)]);
            return new(true, "Comparison catalog ready", models);
        }
        return new(true, "Mock provider ready", [new("mock/script", "Mock script writer"), new("mock/alternate", "Alternate mock model", SupportsImages: true)], backend == AiBackend.ComfyUI ? "mock-version" : null);
    }
    public IAsyncEnumerable<AiModelVerificationUpdate> VerifyComfyTextModelAsync(string model, AiSettings settings, CancellationToken cancellationToken = default) => Test(model, settings, new(AiProviderRegistry.StandardBenchmarkPrompt,
        ComfyTextSettings.Resolve(new(AiBackend.ComfyUI, model, model, settings.ComfyUrl), settings).MaxOutputTokens), false, cancellationToken);
    public IAsyncEnumerable<AiModelVerificationUpdate> TestComfyTextModelAsync(string model, AiSettings settings, ComfyTextModelTestRequest request, CancellationToken cancellationToken = default) => Test(model, settings, request, true, cancellationToken);
    private static async IAsyncEnumerable<AiModelVerificationUpdate> Test(string model, AiSettings settings, ComfyTextModelTestRequest request, bool advanced, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new(Progress: new(GenerationPhase.Generating, "Generating mock model test", 0, request.MaxOutputTokens, "tokens"));
        await Task.Delay(1600, ct);
        var benchmark = new ComfyTextModelBenchmark(DateTimeOffset.UtcNow, "Mock GPU", 0, 20L << 30, 1L << 30, 9L << 30,
            256L << 20, 8L << 30, request.MaxOutputTokens, request.MaxOutputTokens, 16, true, advanced) { ContextTokens = advanced ? null : ComfyTextBenchmark.ContextTokens, BytesPerReplyToken = advanced ? null : 80 * 1024,
                BytesPerPromptToken = advanced ? null : 360 * 1024, CapacityContextTokens = advanced ? null : ComfyTextBenchmark.LargeContextTokens,
                CapacityPeakVramUsedBytes = advanced ? null : 12L << 30 };
        yield return new(Verification: new(settings.ComfyUrl, "mock-version", model, DateTimeOffset.UtcNow, [benchmark]), Response: advanced ? "Mock response: " + request.Prompt : null);
    }
}
sealed class MockChat(string model, AiBackend backend) : IChatClient, IProgressReportingChatClient
{
    public async IAsyncEnumerable<ProgressingChatUpdate> GetStreamingResponseWithProgressAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)) yield return new(update);
        if (backend == AiBackend.OpenRouter) yield return new(OpenRouterUsage: new("mock-generation", model, "Mock provider", 42, 20, 5, 0, 0.0000123m));
    }

    public static System.Collections.Concurrent.ConcurrentQueue<object> Compositions = new();
    public static System.Collections.Concurrent.ConcurrentQueue<object> ReelCompositions = new();
    public static System.Collections.Concurrent.ConcurrentQueue<object> Enhancements = new();
    public static System.Collections.Concurrent.ConcurrentQueue<object> Guidance = new();
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList(); var last = list[^1].Text ?? ""; string output;
        if ((list[0].Text ?? "").Contains("Compose an environment reference reel.") || (list[0].Text ?? "").Contains("Compose a prop reference reel."))
        {
            using var context = JsonDocument.Parse(last);
            var draft = context.RootElement.GetProperty("draft").Deserialize<ReferenceReelDraft>(AtomicJsonFile.Options)!;
            var images = list.SelectMany(m => m.Contents).OfType<DataContent>().Select(data => { var info = SixLabors.ImageSharp.Image.Identify(data.Data.Span); return new { info.Width, info.Height }; }).ToArray();
            ReelCompositions.Enqueue(new { model, context = context.RootElement.Clone(), images });
            var pair = ReferenceReels.Preset(draft);
            if (last.Contains("EMPTY_REEL_MUSIC")) pair = pair with { Prompt = pair.Prompt.Replace("No non-diegetic music.", "") };
            if (last.Contains("WRONG_REEL_DURATION")) pair = pair with { Prompt = pair.Prompt.Replace("5.167 seconds", "5 seconds") };
            output = last.Contains("INVALID_PAIR") ? "{\"prompt\":\"incomplete\"}" : JsonSerializer.Serialize(pair, AtomicJsonFile.Options);
        }
        else if ((list[0].Text ?? "").Contains("Compose a character reference reel."))
        {
            using var context = JsonDocument.Parse(last);
            var request = context.RootElement.GetProperty("request").Deserialize<ReelCompositionRequest>(AtomicJsonFile.Options)!;
            var images = list.SelectMany(m => m.Contents).OfType<DataContent>().Select(data => { var info = SixLabors.ImageSharp.Image.Identify(data.Data.Span); return new { info.Width, info.Height }; }).ToArray();
            ReelCompositions.Enqueue(new { model, context = context.RootElement.Clone(), images });
            var pair = ReferenceReels.Preset(request.Draft with { Framing = request.Draft.Framing == ReelFraming.Custom ? ReelFraming.ContinuousTurn : request.Draft.Framing });
            pair = pair with { Prompt = pair.Prompt.Replace("Neutral studio background", "Focus on the hands. Neutral studio background"),
                UseGuidance = pair.UseGuidance + " Pay attention to the hands." };
            if (last.Contains("EMPTY_REEL_MUSIC")) pair = pair with { Prompt = pair.Prompt.Replace("No non-diegetic music.", "") };
            if (last.Contains("WRONG_REEL_DURATION")) pair = pair with { Prompt = pair.Prompt.Replace("5.167 seconds", "5 seconds") };
            output = last.Contains("INVALID_PAIR") ? "{\"prompt\":\"incomplete\"}" : JsonSerializer.Serialize(pair, AtomicJsonFile.Options);
        }
        else if ((list[0].Text ?? "").Contains("Lumibelle's production director"))
        {
            using var context = JsonDocument.Parse(last); var root = context.RootElement;
            var images = list.SelectMany(m => m.Contents).OfType<DataContent>().Select(data => { var info = SixLabors.ImageSharp.Image.Identify(data.Data.Span); return new { info.Width, info.Height }; }).ToArray();
            Compositions.Enqueue(new { model, context = root.Clone(), images });
            var shot = root.GetProperty("shot").Deserialize<Shot>(AtomicJsonFile.Options)!;
            shot.ApprovedScriptId = Guid.NewGuid(); shot.SceneId = Guid.NewGuid();
            shot.Description = "Vision-composed staging: the camera eases toward the subject, using the selected room and appearance references. " + root.GetProperty("directingNotes").GetString() + " " + root.GetProperty("revisionNotes").GetString();
            foreach (var r in root.GetProperty("references").EnumerateArray()) shot.Images.Add(new ShotImageBinding {
                AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = r.GetProperty("name").GetString()!,
                AiUseHint = r.GetProperty("aiUseHint").GetString()! });
            foreach (var v in root.GetProperty("voices").EnumerateArray()) shot.Voices.Add(new() { VoiceId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Speaker = v.GetProperty("speaker").GetString()!, Start = v.GetProperty("start").GetDouble(), Duration = v.GetProperty("duration").GetDouble() });
            if (root.TryGetProperty("reelKeyframes", out var keyframes)) foreach (var frame in keyframes.EnumerateArray())
                shot.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = frame.GetProperty("reel").GetString()!, AiUseHint = "Auto" });
            var appearances = root.GetProperty("appearances").Deserialize<List<ShotAppearanceContext>>(AtomicJsonFile.Options)!;
            var composedPrompt = H3Policy.Compile(shot, null, appearances);
            if (root.TryGetProperty("videos", out var videoRefs))
            {
                var offset = videoRefs.EnumerateArray().Count(v => v.GetProperty("useSoundtrack").GetBoolean()) + (root.TryGetProperty("reelAudio", out var directAudio) ? directAudio.GetArrayLength() : 0);
                for (var n = shot.Voices.Count; n > 0; n--) composedPrompt = composedPrompt.Replace($"<Audio {n}>", $"<Audio {n + offset}>");
                var definitions = new System.Text.StringBuilder();
                foreach (var v in videoRefs.EnumerateArray())
                {
                    definitions.AppendLine($"<Video {v.GetProperty("video").GetInt32()}> supplies the reference content described by the author: {v.GetProperty("authorProvidedDescription").GetString()}");
                    if (v.GetProperty("audio").ValueKind == JsonValueKind.Number)
                    {
                        var speaker = v.GetProperty("speaker").GetString(); var number = shot.Dialogue.Select(d => d.Speaker).Distinct().ToList().IndexOf(speaker ?? "") + 1;
                        definitions.AppendLine($"<Audio {v.GetProperty("audio").GetInt32()}> supplies voice timbre" + (number > 0 ? $" for {speaker} (S{number})." : "."));
                    }
                }
                if (root.TryGetProperty("reelAudio", out var reelAudio)) foreach (var a in reelAudio.EnumerateArray()) {
                    var speaker = a.GetProperty("speaker").GetString(); var number = shot.Dialogue.Select(d => d.Speaker).Distinct().ToList().IndexOf(speaker ?? "") + 1;
                    definitions.AppendLine($"<Audio {a.GetProperty("audio").GetInt32()}> supplies voice timbre" + (number > 0 ? $" for {speaker} (S{number})." : "."));
                }
                composedPrompt = composedPrompt.Replace("subject_definitions:", "subject_definitions:\n" + definitions);
            }
            output = JsonSerializer.Serialize(new { prompt = composedPrompt, referenceUsage = "Selected images are inspected; video descriptions are author-provided context, not video analysis." });
            if (root.GetProperty("directingNotes").GetString()?.Contains("INVALID_COMPOSITION") == true) output = "{\"prompt\":\"incomplete\",\"referenceUsage\":\"Invalid fixture response\"}";
        }
        else if ((list[0].Text ?? "").Contains("Lumibelle's preservation guidance assistant"))
        {
            using var context = JsonDocument.Parse(last);
            var images = list.SelectMany(m => m.Contents).OfType<DataContent>().Select(data => {
                var info = SixLabors.ImageSharp.Image.Identify(data.Data.Span); return new { info.Width, info.Height }; }).ToArray();
            Guidance.Enqueue(new { model, context = context.RootElement.Clone(), images });
            output = last.Contains("NEEDS_INPUT") ? "{\"kind\":\"NeedsInput\",\"text\":\"Which features should stay consistent?\"}"
                : "{\"kind\":\"Prompt\",\"text\":\"Preserve the recognizable features described for this scope.\"}";
        }
        else if ((list[0].Text ?? "").Contains("Lumibelle's image prompt enhancer"))
        {
            using var context = JsonDocument.Parse(last);
            var prompt = context.RootElement.GetProperty("authorRequest").GetString()!;
            var images = list.SelectMany(m => m.Contents).OfType<DataContent>().Select(data => {
                var info = SixLabors.ImageSharp.Image.Identify(data.Data.Span); return new { info.Width, info.Height }; }).ToArray();
            Enhancements.Enqueue(new { model, context = context.RootElement.Clone(), images });
            output = prompt.Contains("NEEDS_INPUT") ? "NEEDS_INPUT: What exact text should the sign display?"
                : prompt.Contains("NEEDS_SETUP") ? "NEEDS_SETUP: Expand the canvas first."
                : "A carefully composed illustration. " + prompt;
        }
        else if ((list[0].Text ?? "").Contains("plan cinematic coverage", StringComparison.OrdinalIgnoreCase))
        {
            using var context = JsonDocument.Parse(last);
            output = JsonSerializer.Serialize(context.RootElement.GetProperty("scenes").EnumerateArray().Select(scene => new {
                title = "The quiet arrival", sceneId = scene.GetProperty("sceneId").GetString(),
                sourceBlockIds = scene.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("id").GetString())
                    .Concat(last.Contains("DRIFTED_SOURCE_IDS") ? new[] { Guid.NewGuid().ToString() } : []).ToArray(),
                duration = 1.0, description = last.Contains("EDIT_AFTER_TAKE")
                    ? "End holding on the face, leaving the screenplay’s cut to black for the edit after this take."
                    : "The camera holds while Juniper looks up, then speaks.", characters = new[] { new { name = "JUNIPER" } },
                dialogue = scene.GetProperty("blocks").EnumerateArray().Where(b => string.Equals(b.GetProperty("kind").GetString(), "dialogue", StringComparison.OrdinalIgnoreCase)).Select(b => new { speaker = "JUNIPER", language = "English", text = last.Contains("DIALOGUE_VARIANT") ? "You called me?" : b.GetProperty("text").GetString() }).ToArray(),
                atmosphere = "A quiet room", music = "No music" }));
            var lookCharacter = context.RootElement.TryGetProperty("assets", out var assetList) ? assetList.EnumerateArray().FirstOrDefault(a => a.TryGetProperty("looks", out var looks) && looks.GetArrayLength() >= 2) : default;
            if (lookCharacter.ValueKind == JsonValueKind.Object)
            {
                var proposed = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray();
                foreach (var shot in proposed)
                    shot!["characters"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new[] { new { name = "JUNIPER", appearance = new { assetId = lookCharacter.GetProperty("id").GetString(), lookId = lookCharacter.GetProperty("looks")[0].GetProperty("id").GetString(), endLookId = last.Contains("TRANSITION") ? lookCharacter.GetProperty("looks")[1].GetProperty("id").GetString() : null } } }));
                output = proposed.ToJsonString();
            }
            if (last.Contains("NULL_LOOK") && context.RootElement.TryGetProperty("assets", out _))
            {
                var asset = context.RootElement.GetProperty("assets")[0];
                var proposed = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray();
                foreach (var shot in proposed)
                    shot!["characters"] = JsonSerializer.SerializeToNode(new[] { new { name = "JUNIPER", appearance = new {
                        assetId = last.Contains("UNKNOWN_ASSET") ? Guid.NewGuid().ToString() : asset.GetProperty("id").GetString(),
                        lookId = (string?)null, endLookId = (string?)null } } });
                output = proposed.ToJsonString();
            }
            if (last.Contains("FENCED")) output = "```json\n" + output + "\n```";
        }
        else if ((list[0].Text ?? "").Contains("identify reusable visual assets"))
        {
            var scene = Regex.Match(last, @"Scene \[([a-f0-9-]+)\]").Groups[1].Value;
            output = $$"""[{"category":"character","name":"Juniper","description":"A tiny mouse with a striped scarf.","suggestedTags":["face","full body"],"evidence":[{"label":"Kitchen","sceneId":"{{scene}}","excerpt":"A tiny mouse appears"}],"matchedAssetId":null}]""";
            if (last.Contains("Gala costume"))
            {
                var existing = Regex.Match(last, @"([a-f0-9-]+) \| Character \| Juniper \|").Groups[1].Value;
                var match = Guid.TryParse(existing, out var assetId) ? (Guid?)assetId : null;
                var evidence = new[] { new { label = "Bedroom", sceneId = scene, excerpt = "Everyday gray hoodie, then white and gold Gala costume armor" } };
                output = JsonSerializer.Serialize(new[] { new { category = "character", name = "Juniper", description = "Dark eyes and a round face.", preservationGuidance = "Preserve her facial identity.", matchedAssetId = match, evidence, looks = new[] { new { name = "Everyday", description = "Gray hoodie.", preservationGuidance = "Soft gray fabric.", evidence }, new { name = "Gala costume", description = "White and gold armor.", preservationGuidance = "Gold trim and white armor.", evidence } } } });
            }
            if (last.Contains("UX_REVIEW"))
            {
                var proposals = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray();
                for (var i = 1; i <= 12; i++) proposals.Add(JsonSerializer.SerializeToNode(new { category = "prop", name = $"Reference object {i}", description = "A recognizable object described in the script.", evidence = new[] { new { label = "Bedroom", sceneId = scene, excerpt = "An object in the bedroom" } } }));
                output = proposals.ToJsonString();
            }
            if (last.Split("EXISTING ASSETS")[0].Contains("EMPTY_COVERAGE") && !last.Split("EXISTING ASSETS")[0].Contains("Mira enters")) output = "[]";
        }
        else if (last.Contains("Return readable Markdown")) output = "A simple summoning mishap could turn into a warm dinner scene.";
        else
        {
            var blocks = last.Contains("Scope: Passage") ? new[] { ScriptBlock.Create(ScriptBlockKind.Action, "A tiny mouse politely bows") }
                : last.Contains("Return only new blocks") ? new[] { ScriptBlock.Create(ScriptBlockKind.Action, "The kettle applauds.") }
                : last.Contains("Scope: Scene") ? new[] { ScriptBlock.Create(ScriptBlockKind.Scene, "INT. KITCHEN — NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "The mouse politely requests a smaller spoon.") }
                : last.Contains("Scope: Act") ? new[] { ScriptBlock.Create(ScriptBlockKind.Act, "ACT 3 — REVISED"), ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM — NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "The mouse opens the window.") }
                : new[] { ScriptBlock.Create(ScriptBlockKind.Scene, "INT. KITCHEN — NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "A tiny mouse appears in a saucepan."), ScriptBlock.Create(ScriptBlockKind.Character, "JUNIPER"), ScriptBlock.Create(ScriptBlockKind.Dialogue, "You called?"), ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. GARDEN — DAWN"), ScriptBlock.Create(ScriptBlockKind.Action, "They share breakfast under the apple tree.") };
            output = JsonSerializer.Serialize(blocks.Select(b => new { kind = b.Kind.ToString(), spans = b.Spans.Select(s => new { text = s.Text, bold = s.Bold, italic = s.Italic }) }));
            if (last.Contains("FENCED_COMMENTARY")) output = "**Here is the screenplay:**\n\n```json\n" + output + "\n```";
        }
        if (last.Contains(ScriptEdits.Instructions, StringComparison.Ordinal))
        {
            var targetJson = last.Split("\nTARGET\n")[1].Split("\n\nAuthor instructions:")[0];
            var target = JsonSerializer.Deserialize<ScriptTarget>(targetJson, AtomicJsonFile.Options)!;
            var block = target.OriginalBlocks.First(b => b.Kind == ScriptBlockKind.Action);
            var text = target.Scope == ScriptScope.Passage ? "A tiny mouse politely bows" : block.Text + (last.Contains("SECOND_CHANGE") ? " Second detail." : " First detail.");
            output = JsonSerializer.Serialize(new { version = 1, operations = new[] { new { kind = "replace", startId = block.Id, startOffset = target.Scope == ScriptScope.Passage ? (int?)target.StartOffset : null, endOffset = target.Scope == ScriptScope.Passage ? (int?)target.EndOffset : null, blocks = new[] { new { kind = block.Kind.ToString(), spans = new[] { new { text } } } } } } });
        }
        if (last.Contains("INVALID")) output = "{\"kind\":\"Prompt\",\"text\":\"unfinished";
        if (last.Contains("SILENT_START")) await Task.Delay(2500, cancellationToken);
        for (int i = 0; i < output.Length; i += 80)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, output.Substring(i, Math.Min(80, output.Length - i)));
            await Task.Delay(last.Contains("SLOW") ? 600 : 20, cancellationToken);
        }
        yield return new ChatResponseUpdate { FinishReason = last.Contains("TRUNCATED") ? ChatFinishReason.Length : ChatFinishReason.Stop };
    }
}
sealed class MockImages : IReferenceImageGenerator, IReferenceImageEditor
{
    public static byte[] CropPattern(int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(width, height);
        SixLabors.ImageSharp.PixelFormats.Rgb24[] colors = [new(240, 40, 40), new(40, 200, 40), new(40, 40, 240), new(240, 200, 40)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) image[x, y] = colors[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
        using var stream = new MemoryStream(); image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder()); return stream.ToArray();
    }

    public static byte[] Png(int variant = 0)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(variant == 2 ? 120 : 64, 80, new((byte)(210 - variant * 40), 160, 190));
        using var stream = new MemoryStream(); image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder()); return stream.ToArray();
    }
    public Task<ComfyImageConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= new(); var klein = settings.DefaultImageWorkflow == ImageWorkflow.Flux2Klein9bKv;
        return Task.FromResult(new ComfyImageConfiguration(true, "Mock image workflow ready.",
            [new(klein ? settings.FluxKleinModel : settings.ComfyImageModel, "Mock diffusion model")],
            [new(klein ? settings.FluxKleinTextEncoder : settings.ComfyImageTextEncoder, "Mock text encoder")],
            [new(klein ? settings.FluxKleinVae : settings.ComfyImageVae, "Mock VAE")]));
    }
    Task<ComfyImageEditConfiguration> IReferenceImageEditor.CheckAsync(AiSettings? settings, CancellationToken cancellationToken) => Task.FromResult(new ComfyImageEditConfiguration(true, "Mock editing ready.", [], settings?.DefaultImageWorkflow is ImageWorkflow.Flux2Klein9bKv or ImageWorkflow.CodexImages ? 8 : 2));
    public async IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new("Generating mock image", 1, 1, Progress: new(GenerationPhase.Generating, "Generating mock image"));
        await Task.Delay(request.Prompt.Contains("SLOW") ? 1500 : 100, cancellationToken);
        yield return new("Saving", 1, 1, Png(), "mock.png", new() { Workflow = request.Workflow ?? ImageWorkflow.Krea2, Prompt = request.Prompt, AspectRatio = request.AspectRatio,
            Loras = request.Loras.Where(s => s.Enabled && s.Strength != 0).Select(s => new AppliedLora(s.Reference, s.Strength)).ToArray() });
    }
    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, Stream source, CancellationToken cancellationToken = default) =>
        EditAsync(request, [new(request.SourceAssetId, request.SourceImageId, source)], cancellationToken);
    public async IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var captured = sources.Select(source => new AssetImageReference(source.AssetId, source.ImageId)).ToArray();
        for (var candidate = 1; candidate <= request.Count; candidate++)
        {
        yield return new("Editing mock image", candidate, request.Count, Progress: new(GenerationPhase.Generating, "Editing mock image"));
        await Task.Delay(candidate > 1 && request.Prompt.Contains("SLOW") ? 15000 : 1200, cancellationToken);
        if (request.Prompt.Contains("EMPTY")) yield break;
        if (candidate > 1 && request.Prompt.Contains("FAIL")) throw new AiGenerationException("Mock edit failed after the first take.");
        yield return new("Saving", candidate, request.Count, request.Regions?.Count > 0 ? CropPattern(512,512) : Png(candidate), "mock-edit.png", new()
        {
            Workflow = request.Workflow ?? ImageWorkflow.Krea2, Prompt = request.Prompt, Steps = 4, Seed = (request.Seed ?? 11) + candidate - 1,
            Loras = request.Loras.Where(s => s.Enabled && s.Strength != 0).Select(s => new AppliedLora(s.Reference, s.Strength)).ToArray(),
            Edit = new() { SourceAssetId = request.SourceAssetId, SourceImageId = request.SourceImageId, SourceCrop = request.SourceCrop, References = captured, ReferenceCrops = request.ReferenceCrops,
                BaseReferenceBoost = request.Workflow != ImageWorkflow.Flux2Klein9bKv && captured.Length == 2 ? request.BaseReferenceBoost : null,
                Lora = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? "" : "krea2_identity_edit_v1_2.safetensors",
                LoraStrength = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? 0 : 1,
                ReferenceBoost = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? 0 : request.ReferenceBoost,
                GroundingPixels = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? 0 : 768,
                FitMode = request.Workflow == ImageWorkflow.Flux2Klein9bKv ? "reference-latent" : "fit" }
        });
        }
    }
}
