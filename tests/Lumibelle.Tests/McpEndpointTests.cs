using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using lumibelle;
using lumibelle.Mcp;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Review;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Lumibelle.Tests;

/// <summary>The MCP endpoint over real HTTP on a loopback port, against a temporary library.</summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.McpTests", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private readonly StandInRenders _renders = new();
    private Uri _endpoint = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IAgentAccessStore Access => _app.Services.GetRequiredService<IAgentAccessStore>();

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ISecretProtector>(new WebSecretProtector(new EphemeralDataProtectionProvider()));
        builder.Services.AddLumibelleCore(new ApplicationPaths(Path.Combine(_root, "data"), Path.Combine(_root, "library")), exclusive: false);
        builder.Services.AddLumibelleMcp();
        builder.Services.AddSingleton<IReviewRenderQueue>(_renders);
        _app = builder.Build();
        _app.MapLumibelleMcp();
        await _app.StartAsync(Ct);
        _endpoint = new(new Uri(_app.Urls.Single()), McpHosting.Path);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None); await _app.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<string> EnableAsync(bool approval = false)
    {
        var settings = await Access.LoadAsync(Ct);
        await Access.SaveAsync(true, approval, settings.Revision, Ct);
        return (await Access.ReadTokenAsync(Ct))!;
    }

    private async Task<HttpResponseMessage> PostAsync(string? token, string? origin = null)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        { Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await http.SendAsync(request, Ct);
    }

    private async Task<McpClient> ConnectAsync(string token) => await McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = _endpoint, TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
    }), cancellationToken: Ct);

    private static string TextOf(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task IsNotThereUntilAccessIsTurnedOn()
    {
        Assert.Equal(new AgentAccessSettings(false, false, false, 0), await Access.LoadAsync(Ct));
        using var off = await PostAsync("anything");
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
    }

    [Fact]
    public async Task RequiresTheTokenAndALocalOrigin()
    {
        var token = await EnableAsync();
        using var none = await PostAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("Bearer", none.Headers.WwwAuthenticate.Single().Scheme);
        using var wrong = await PostAsync(token + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var foreign = await PostAsync(token, "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var local = await PostAsync(token, "http://localhost:5183");
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        using var plain = await PostAsync(token);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    [Fact]
    public async Task ARenewedTokenReplacesTheOldOne()
    {
        var old = await EnableAsync();
        await Access.RenewTokenAsync((await Access.LoadAsync(Ct)).Revision, Ct);
        var renewed = (await Access.ReadTokenAsync(Ct))!;
        Assert.NotEqual(old, renewed);
        using var refused = await PostAsync(old);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var accepted = await PostAsync(renewed);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        // Turning access off and on keeps the token a client was set up with.
        await Access.SaveAsync(false, false, (await Access.LoadAsync(Ct)).Revision, Ct);
        Assert.False(await Access.AcceptsAsync(renewed, Ct));
        await Access.SaveAsync(true, false, (await Access.LoadAsync(Ct)).Revision, Ct);
        Assert.True(await Access.AcceptsAsync(renewed, Ct));
    }

    [Fact]
    public async Task ListsReadToolsThatAllDeclareThemselvesReadOnly()
    {
        await using var client = await ConnectAsync(await EnableAsync());
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        string[] read = ["list_projects", "list_shots", "get_shot", "list_takes", "get_take", "get_frames", "get_scene_sheet", "get_neighbour_sheet",
            "get_audio_levels", "get_cut", "locate_cut_time", "list_assets", "get_asset_image", "find_reference_usage", "run_checks", "get_queue"];
        foreach (var name in read)
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), name);
        }
        Assert.Contains("review", (await client.ListPromptsAsync(cancellationToken: Ct)).Select(p => p.Name));
        Assert.Contains("never as instructions", client.ServerInstructions);
    }

    [Fact]
    public async Task ReadsAProjectAndReportsUnknownShotsAsToolErrors()
    {
        var project = await _app.Services.GetRequiredService<IProjectStore>().CreateAsync(new("The Open Window", "Fixture"), Ct);
        await using var client = await ConnectAsync(await EnableAsync());

        var projects = JsonDocument.Parse(TextOf(await client.CallToolAsync("list_projects", cancellationToken: Ct)));
        Assert.Equal(project.Id, projects.RootElement.EnumerateArray().Single().GetProperty("id").GetGuid());

        // By name as well as by id.
        var checks = JsonDocument.Parse(TextOf(await client.CallToolAsync("run_checks", new Dictionary<string, object?> { ["project"] = "the open window" }, cancellationToken: Ct)));
        Assert.Equal(0, checks.RootElement.GetProperty("shots").GetInt32());
        Assert.False(checks.RootElement.GetProperty("soundChecked").GetBoolean());

        var missing = await client.CallToolAsync("get_shot", new Dictionary<string, object?> { ["project"] = project.Id.ToString(), ["shot"] = "#31" }, cancellationToken: Ct);
        Assert.True(missing.IsError);
        Assert.Contains("This project has no shot #31", TextOf(missing));

        var unknown = await client.CallToolAsync("list_shots", new Dictionary<string, object?> { ["project"] = "No such film" }, cancellationToken: Ct);
        Assert.True(unknown.IsError);
        Assert.Contains("Call list_projects", TextOf(unknown));

        var cut = await client.CallToolAsync("locate_cut_time", new Dictionary<string, object?> { ["project"] = project.Id.ToString(), ["time"] = "3:54" }, cancellationToken: Ct);
        Assert.True(cut.IsError);
        Assert.Contains("The cut is empty", TextOf(cut));
    }

    private async Task<(Guid Project, Shot Shot)> ProjectWithShotAsync()
    {
        var project = await _app.Services.GetRequiredService<IProjectStore>().CreateAsync(new("The Open Window", "Fixture"), Ct);
        var shot = new Shot { Title = "The arrival", Description = "The camera holds as the mouse looks up.", Duration = 1 };
        await _app.Services.GetRequiredService<IShotStore>().SaveAsync(project.Id, [shot], 0, ct: Ct);
        var production = _app.Services.GetRequiredService<IProductionStore>();
        var setup = (await production.InitializeAsync(project.Id, Ct)).Compositions.Single();
        setup.Prompt = "The camera holds as the mouse looks up.";
        await production.SaveAsync(project.Id, setup, setup.Version, Ct);
        return (project.Id, shot);
    }

    private async Task<JsonElement> CallAsync(McpClient client, string tool, object arguments)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: Ct);
        Assert.False(result.IsError == true, TextOf(result));
        return JsonDocument.Parse(TextOf(result)).RootElement;
    }

    [Fact]
    public async Task TheToolsThatActAreHiddenAndRefusedUntilApprovalFromTheClientIsAllowed()
    {
        var token = await EnableAsync();
        await using (var client = await ConnectAsync(token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: Ct);
            Assert.DoesNotContain(tools, t => ReviewApprovalTools.Names.Contains(t.Name));
            Assert.Contains(tools, t => t.Name == "propose_step");
            // Calling a hidden tool by name is refused as well, with where approval happens instead.
            var refused = await client.CallToolAsync("approve_plan", new Dictionary<string, object?> { ["project"] = "x", ["plan"] = Guid.NewGuid().ToString(), ["revision"] = 1, ["takes"] = 0, ["footage_seconds"] = 0, ["changes"] = 0 }, cancellationToken: Ct);
            Assert.True(refused.IsError);
            Assert.Equal(ReviewApprovalTools.Off, TextOf(refused));
        }
        await Access.SaveAsync(true, true, (await Access.LoadAsync(Ct)).Revision, Ct);
        await using (var client = await ConnectAsync(token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: Ct);
            foreach (var name in ReviewApprovalTools.Names)
            {
                var tool = Assert.Single(tools, t => t.Name == name).ProtocolTool;
                // The mark Claude Code documents for a tool whose prompt must reach a person on every call.
                Assert.True(tool.Meta?["anthropic/requiresUserInteraction"]?.GetValue<bool>(), name);
                Assert.NotEqual(true, tool.Annotations?.ReadOnlyHint);
            }
        }
        // Turning agent access off also turns approval from the client off.
        var off = await Access.SaveAsync(false, true, (await Access.LoadAsync(Ct)).Revision, Ct);
        Assert.False(off.AllowClientApproval);
    }

    [Fact]
    public async Task APlanProposedOverMcpChangesNothingUntilAnApprovalThatMatchesIt()
    {
        var (project, shot) = await ProjectWithShotAsync();
        var production = _app.Services.GetRequiredService<IProductionStore>();
        var token = await EnableAsync(approval: true);
        await using var client = await ConnectAsync(token);
        var id = project.ToString();

        var plan = (await CallAsync(client, "create_plan", new { project = id, title = "Fix the letter" })).GetProperty("id").GetString()!;
        var finding = (await CallAsync(client, "add_finding", new { project = id, plan, shot = "#1", message = "The mouse holds nothing.", evidence = "Prompt: looks up" })).GetProperty("id").GetString()!;
        var step = await CallAsync(client, "propose_step", new { project = id, plan, kind = "SetPrompt", shot = "#1", reason = "Name the letter.", findings = new[] { finding }, edits = new[] { new { old = "looks up", @new = "looks up, holding a letter" } } });
        Assert.Equal("Change the prompt of #1 and mark it reviewed", step.GetProperty("does").GetString());
        await CallAsync(client, "propose_step", new { project = id, plan, kind = "UpdateCut", reason = "Show the picks." });

        // An edit that is not unique is refused when it is proposed, not when it runs.
        var ambiguous = await client.CallToolAsync("propose_step", new Dictionary<string, object?> { ["project"] = id, ["plan"] = plan, ["kind"] = "SetPrompt", ["shot"] = "#1", ["reason"] = "x",
            ["edits"] = new[] { new Dictionary<string, object?> { ["old"] = "s ", ["new"] = "z " } } }, cancellationToken: Ct);
        Assert.True(ambiguous.IsError);
        Assert.Contains("more than once", TextOf(ambiguous));

        var submitted = await CallAsync(client, "submit_plan", new { project = id, plan });
        Assert.Equal("Submitted", submitted.GetProperty("state").GetString());
        Assert.EndsWith($"/projects/{project:D}/review?planId={plan}", submitted.GetProperty("page").GetString());
        var totals = submitted.GetProperty("totals"); var revision = submitted.GetProperty("revision").GetInt64();
        Assert.Equal((0, 2), (totals.GetProperty("takes").GetInt32(), totals.GetProperty("changes").GetInt32()));
        var full = await CallAsync(client, "get_plan", new { project = id, plan, step = step.GetProperty("id").GetString() });
        Assert.Contains("holding a letter", full.GetProperty("promptAfter").GetString());
        Assert.Equal("The camera holds as the mouse looks up.", (await production.LoadAsync(project, Ct)).Compositions.Single().Prompt);

        // The totals shown to the director must be the plan's own.
        var wrong = await client.CallToolAsync("approve_plan", new Dictionary<string, object?> { ["project"] = id, ["plan"] = plan, ["revision"] = revision, ["takes"] = 0, ["footage_seconds"] = 0, ["changes"] = 1 }, cancellationToken: Ct);
        Assert.True(wrong.IsError);
        Assert.Contains("does not match the plan as it stands", TextOf(wrong));
        Assert.Equal("The camera holds as the mouse looks up.", (await production.LoadAsync(project, Ct)).Compositions.Single().Prompt);

        var approved = await CallAsync(client, "approve_plan", new { project = id, plan, revision, takes = 0, footage_seconds = 0, changes = 2 });

        Assert.Equal("Finished", approved.GetProperty("state").GetString());
        Assert.All(approved.GetProperty("steps").EnumerateArray(), s => Assert.Equal("Done", s.GetProperty("state").GetString()));
        var setup = (await production.LoadAsync(project, Ct)).Compositions.Single();
        Assert.Equal("The camera holds as the mouse looks up, holding a letter.", setup.Prompt);
        Assert.Equal(setup.Prompt, setup.Accepted?.Prompt);
        var saved = await _app.Services.GetRequiredService<ReviewPlanStore>().GetAsync(project, Guid.Parse(plan), Ct);
        Assert.NotEqual(ReviewPlans.Director, saved.ApprovedFrom);
        Assert.Contains(saved.Log, l => l.Who == saved.ApprovedFrom && l.What.StartsWith("Approved revision"));
        Assert.Equal(shot.Id, saved.Steps[0].ShotId);
    }

    [Fact]
    public async Task RenderedTakesAreRecommendedAndPickedOverMcpOnlyByNamingThem()
    {
        var (project, shot) = await ProjectWithShotAsync();
        var shots = _app.Services.GetRequiredService<IShotStore>();
        await using var client = await ConnectAsync(await EnableAsync(approval: true));
        var id = project.ToString();
        var plan = (await CallAsync(client, "create_plan", new { project = id, title = "Another take" })).GetProperty("id").GetString()!;
        var step = (await CallAsync(client, "propose_step", new { project = id, plan, kind = "GenerateTakes", shot = "#1", reason = "Try again.", takes = 2 })).GetProperty("id").GetString()!;
        var submitted = await CallAsync(client, "submit_plan", new { project = id, plan });
        var totals = submitted.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("takes").GetInt32());

        var approved = await CallAsync(client, "approve_plan", new { project = id, plan, revision = submitted.GetProperty("revision").GetInt64(), takes = 2,
            footage_seconds = totals.GetProperty("footageSeconds").GetDouble(), changes = 0 });
        Assert.Equal("Running", approved.GetProperty("steps")[0].GetProperty("state").GetString());
        var job = approved.GetProperty("steps")[0].GetProperty("job").GetGuid();
        Assert.Equal((shot.Id, 2), Assert.Single(_renders.Captured));

        // The render finishes: two takes are published for the job, as the video handler would.
        var takes = new List<string>();
        for (var n = 1; n <= 2; n++)
        {
            var current = (await shots.LoadAsync(project, Ct)).Shots.Single();
            var snapshot = new VideoSnapshot(project, 1, current.Copy(), "prompt", "fingerprint", "http://localhost:8188", new(), 32, 32, H3Policy.Frames(1));
            var run = Guid.NewGuid(); var directory = Path.Combine(await shots.RunDirectoryAsync(project, run, Ct), "candidate-" + n); Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), [1, 2, 3], Ct);
            var frames = await Lumibelle.Testing.MockFrameArchive.WriteAsync(directory, snapshot.FrameCount, 32, 32, Ct);
            var document = await shots.PublishTakeAsync(project, new() { ShotId = current.Id, RunId = run, Candidate = n, AiJobId = job, Snapshot = snapshot, Width = 32, Height = 32, Frames = frames, Bytes = 3 + frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) }, directory, Ct);
            takes.Add(document.Takes.Single(t => t.RunId == run).Id.ToString());
        }
        _renders.States[job] = AiJobState.Completed;
        await _app.Services.GetRequiredService<ReviewPlanRunner>().AdvanceWatchedAsync(ct: Ct);

        var waiting = await CallAsync(client, "get_plan", new { project = id, plan });
        Assert.Equal("Pick", waiting.GetProperty("steps")[0].GetProperty("state").GetString());
        Assert.Equal(takes, waiting.GetProperty("steps")[0].GetProperty("takesToPickFrom").EnumerateArray().Select(t => t.GetString()!));
        Assert.Null((await shots.LoadAsync(project, Ct)).Shots.Single().SelectedTakeId);

        await CallAsync(client, "recommend_take", new { project = id, plan, step, take = takes[1], reason = "The mouse looks up in time." });
        Assert.Null((await shots.LoadAsync(project, Ct)).Shots.Single().SelectedTakeId);

        // A take the plan did not render is refused, and nothing is selected.
        var wrong = await client.CallToolAsync("accept_picks", new Dictionary<string, object?> { ["project"] = id, ["plan"] = plan,
            ["picks"] = new[] { new Dictionary<string, object?> { ["shot"] = "#1", ["take"] = Guid.NewGuid().ToString() } } }, cancellationToken: Ct);
        Assert.True(wrong.IsError);
        Assert.Null((await shots.LoadAsync(project, Ct)).Shots.Single().SelectedTakeId);

        var picked = await CallAsync(client, "accept_picks", new { project = id, plan, picks = new[] { new { shot = "#1", take = takes[1] } } });
        Assert.Equal("Finished", picked.GetProperty("state").GetString());
        Assert.Equal(takes[1], (await shots.LoadAsync(project, Ct)).Shots.Single().SelectedTakeId.ToString());
    }

    [Fact]
    public async Task AnAgentCanStopCancelAndPauseWithoutApprovalButNotResume()
    {
        var (project, shot) = await ProjectWithShotAsync();
        var jobs = _app.Services.GetRequiredService<AiJobCoordinator>();
        await using var client = await ConnectAsync(await EnableAsync());
        var id = project.ToString();
        var names = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToArray();
        Assert.Contains("stop_plan", names); Assert.Contains("cancel_request", names); Assert.Contains("pause_queue", names);
        Assert.DoesNotContain("resume_queue", names); Assert.DoesNotContain("resume_request", names);

        // A plan approved in the app, its render running, stopped from the agent's conversation.
        var plans = _app.Services.GetRequiredService<ReviewPlans>();
        var plan = (await CallAsync(client, "create_plan", new { project = id, title = "Retake" })).GetProperty("id").GetString()!;
        await CallAsync(client, "propose_step", new { project = id, plan, kind = "GenerateTakes", shot = "#1", reason = "Try again." });
        await plans.ApproveAsync(project, Guid.Parse(plan), ReviewPlans.Director, ct: Ct);
        var job = (await _app.Services.GetRequiredService<ReviewPlanRunner>().AdvanceAsync(project, Guid.Parse(plan), Ct)).Steps[0].JobId!.Value;
        var stopped = await CallAsync(client, "stop_plan", new { project = id, plan });
        Assert.Equal("Stopped", stopped.GetProperty("state").GetString());
        Assert.Equal("Skipped", stopped.GetProperty("steps")[0].GetProperty("state").GetString());
        Assert.Equal(AiJobState.Cancelled, _renders.States[job]);

        // The queue itself: pause, and cancel a request by id.
        await CallAsync(client, "pause_queue", new { provider = "ComfyUI" });
        Assert.Contains(AiBackend.ComfyUI, jobs.View.Paused);
        var waiting = Guid.NewGuid();
        await jobs.EnqueueAsync(AiJobSubmission.Create(waiting, AiJobKind.Video, AiBackend.ComfyUI, new(project, ShotId: shot.Id, CompositionId: (await _app.Services.GetRequiredService<IProductionStore>().LoadAsync(project, Ct)).Compositions[0].Id), "Project", "Shot", Guid.NewGuid(), new { takes = 1 }), Ct);
        var queue = await CallAsync(client, "get_queue", new { project = id });
        Assert.Contains(queue.GetProperty("jobs").EnumerateArray(), j => j.GetProperty("id").GetGuid() == waiting);
        await CallAsync(client, "cancel_request", new { request = waiting.ToString() });
        Assert.Equal(AiJobState.Cancelled, jobs.View.Jobs.Single(j => j.Id == waiting).State);

        var unknown = await client.CallToolAsync("cancel_request", new Dictionary<string, object?> { ["request"] = Guid.NewGuid().ToString() }, cancellationToken: Ct);
        Assert.True(unknown.IsError);
        var retired = await client.CallToolAsync("pause_queue", new Dictionary<string, object?> { ["provider"] = "ClaudeCode" }, cancellationToken: Ct);
        Assert.True(retired.IsError);

        // Resuming can spend GPU time, so it waits for the director's switch.
        var refused = await client.CallToolAsync("resume_queue", new Dictionary<string, object?> { ["provider"] = "ComfyUI" }, cancellationToken: Ct);
        Assert.True(refused.IsError);
        Assert.Contains(AiBackend.ComfyUI, jobs.View.Paused);
        await Access.SaveAsync(true, true, (await Access.LoadAsync(Ct)).Revision, Ct);
        await CallAsync(client, "resume_queue", new { provider = "ComfyUI" });
        Assert.DoesNotContain(AiBackend.ComfyUI, jobs.View.Paused);
    }

    private sealed class StandInRenders : IReviewRenderQueue
    {
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, AiJobState> States { get; } = new();
        public List<(Guid Shot, int Takes)> Captured { get; } = [];
        public Task<AiJobSubmission> CaptureAsync(Guid id, Guid projectId, ProductionComposition setup, int takes, VideoResolution? resolution, CancellationToken ct)
        {
            Captured.Add((setup.ShotId, takes));
            return Task.FromResult(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI, new(projectId, ShotId: setup.ShotId), "Project", "Shot", Guid.Empty, new { takes }));
        }
        public Task EnqueueAsync(AiJobSubmission request, CancellationToken ct) { States[request.Id] = AiJobState.Waiting; return Task.CompletedTask; }
        public Task CancelAsync(Guid jobId, CancellationToken ct) { States[jobId] = AiJobState.Cancelled; return Task.CompletedTask; }
        public AiJobState? State(Guid jobId) => States.TryGetValue(jobId, out var state) ? state : null;
    }

    [Theory]
    [InlineData("http://localhost:5183", true)]
    [InlineData("http://127.0.0.1:5183", true)]
    [InlineData("http://[::1]:5183", true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://localhost.example.com", false)]
    [InlineData("null", false)]
    public void AcceptsOnlyLoopbackOrigins(string origin, bool expected) => Assert.Equal(expected, McpHosting.LocalOrigin(origin));
}
