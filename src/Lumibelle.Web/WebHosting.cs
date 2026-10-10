using lumibelle.Components;
using lumibelle.Services;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.DataProtection;
namespace lumibelle;

public static class WebHosting
{
    public static IServiceCollection AddLumibelleWeb(this IServiceCollection services, ApplicationPaths paths, bool exclusive = true)
    {
        services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);
        services.AddDataProtection().SetApplicationName("Lumibelle");
        services.AddSingleton<ISecretProtector, WebSecretProtector>();
        services.AddLumibelleCore(paths, exclusive).AddLumibelleUI();
        return services;
    }
    public static void MapLumibelleWeb(this WebApplication app)
    {
        // SignalR closes its current connections when ApplicationStopping fires.
        // Browsers can immediately reconnect before Kestrel stops accepting requests;
        // that new connection missed SignalR's close pass and holds shutdown open.
        app.Use(async (http, next) => {
            if (http.Request.Path.StartsWithSegments("/_blazor") && app.Lifetime.ApplicationStopping.IsCancellationRequested) {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await next(http);
        });
        if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
        app.UseStatusCodePagesWithReExecute("/not-found");
        app.UseAntiforgery(); app.MapStaticAssets(); app.MapProductionMedia();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Routes).Assembly);
    }
    public static void MapProductionMedia(this WebApplication app)
    {
        foreach (var path in new[] { "/media/{**resource}", "/downloads/{**resource}" })
            app.MapMethods(path, ["GET", "HEAD"], async (HttpContext http, MediaResources media) =>
            {
                await using var response = await media.GetAsync(http.Request.Path + http.Request.QueryString, http.Request.Method,
                    http.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToString()), http.RequestAborted);
                http.Response.StatusCode = response.Status;
                foreach (var header in response.Headers) http.Response.Headers[header.Key] = header.Value;
                if (http.Request.Method != "HEAD") await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
            });
    }
}
