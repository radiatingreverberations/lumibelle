using Microsoft.AspNetCore.Components.WebView.Maui;
using Foundation;
using WebKit;
using System.Collections.Concurrent;
namespace Lumibelle.Desktop;

internal sealed class PlatformMediaAdapter(MediaResources media) : NSObject, IWKUrlSchemeHandler
{
    private readonly ConcurrentDictionary<nint, CancellationTokenSource> requests = new();
    public void Attach(BlazorWebView view)
    {
        view.BlazorWebViewInitializing += (_, args) => args.Configuration.SetUrlSchemeHandler(this, "lumibelle-media");
        view.WebResourceRequested += (_, args) =>
        {
            if (args.Uri?.AbsolutePath is not { } path || !(path.StartsWith("/media/") || path.StartsWith("/downloads/"))) return;
            // Feasibility gate: seeking through this redirect must be proven on a real WKWebView.
            args.SetResponse(307, "Temporary Redirect", new Dictionary<string, string> { ["Location"] = "lumibelle-media://0.0.0.1" + args.Uri.PathAndQuery, ["Content-Length"] = "0", ["Cache-Control"] = "no-store" }, Stream.Null);
        };
    }
    [Export("webView:startURLSchemeTask:")]
    public async void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask task)
    {
        var key = task.Handle; using var cancellation = new CancellationTokenSource(); requests[key] = cancellation;
        try
        {
            var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if (task.Request.Headers is { } h) foreach (var k in h.Keys) headers[k.ToString()] = h[k].ToString();
            if (task.Request.Url?.AbsoluteString is not { } url) throw new InvalidOperationException("The media request has no URL.");
            await using var response = await media.GetAsync(url, task.Request.HttpMethod ?? "GET", headers, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            using var nativeHeaders = new NSMutableDictionary();
            foreach (var header in response.Headers) nativeHeaders.Add((NSString)header.Key, (NSString)header.Value);
            nativeHeaders.Add((NSString)"Access-Control-Allow-Origin", (NSString)"*");
            nativeHeaders.Add((NSString)"Access-Control-Expose-Headers", (NSString)"Content-Length,Content-Range,Accept-Ranges,Last-Modified");
            using var nativeResponse = new NSHttpUrlResponse(task.Request.Url, response.Status, "HTTP/1.1", nativeHeaders);
            task.DidReceiveResponse(nativeResponse);
            var buffer = new byte[64 * 1024]; int count;
            while ((count = await response.Content.ReadAsync(buffer, cancellation.Token)) > 0)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var chunk = NSData.FromArray(buffer.AsSpan(0, count).ToArray()); task.DidReceiveData(chunk);
            }
            cancellation.Token.ThrowIfCancellationRequested(); task.DidFinish();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!cancellation.IsCancellationRequested) task.DidFailWithError(new NSError(new NSString("Lumibelle.Media"), 1)); }
        finally { requests.TryRemove(key, out _); }
    }
    [Export("webView:stopURLSchemeTask:")]
    public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask task)
    { if (requests.TryRemove(task.Handle, out var cancellation)) cancellation.Cancel(); }
    protected override void Dispose(bool disposing)
    { if (disposing) foreach (var cancellation in requests.Values) cancellation.Cancel(); base.Dispose(disposing); }
}
