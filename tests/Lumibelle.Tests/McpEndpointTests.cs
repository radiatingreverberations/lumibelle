using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using lumibelle;
using lumibelle.Mcp;
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

    [Theory]
    [InlineData("http://localhost:5183", true)]
    [InlineData("http://127.0.0.1:5183", true)]
    [InlineData("http://[::1]:5183", true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://localhost.example.com", false)]
    [InlineData("null", false)]
    public void AcceptsOnlyLoopbackOrigins(string origin, bool expected) => Assert.Equal(expected, McpHosting.LocalOrigin(origin));
}
