using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Lumibelle.Tests;

internal static class BunitClicks
{
    /// <summary>
    /// Finds an element and clicks it on the renderer's dispatcher. Background work (the AI queue, saves, model tests) keeps re-rendering
    /// these components from other threads. bUnit parses a component's DOM lazily on first read and a render discards it without a lock,
    /// so a read from the test thread that overlaps a render can cache the old DOM, whose handlers that render replaced. A click from the
    /// test thread can also wait behind a render, and bUnit's synchronous Click then drops the failure. On the dispatcher no render can
    /// interleave, so tests whose pages re-render from background work read the DOM and raise events there, or in WaitFor checks.
    /// </summary>
    /// <param name="awaitHandler">False for a handler that keeps running, such as one awaiting the dialog it opens: the click is then only dispatched.</param>
    public static Task ClickCurrent<TComponent>(this IRenderedComponent<TComponent> rendered, Func<IElement> find, bool awaitHandler = true) where TComponent : IComponent
        => awaitHandler ? rendered.InvokeAsync(() => find().ClickAsync(new())) : rendered.InvokeAsync(() => find().Click());
}
