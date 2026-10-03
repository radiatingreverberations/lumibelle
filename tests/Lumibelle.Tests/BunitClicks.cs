using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Lumibelle.Tests;

internal static class BunitClicks
{
    /// <summary>
    /// Background work (the AI queue, saves, model tests) keeps re-rendering these components. A click on an element found before such a
    /// render names a handler that no longer exists and dispatches nothing, so find the element again after re-rendering and retry.
    /// </summary>
    /// <param name="awaitHandler">False for a handler that keeps running, such as one awaiting the dialog it opens: the click is then only dispatched.</param>
    public static async Task ClickCurrent<TComponent>(this IRenderedComponent<TComponent> rendered, Func<IElement> find, bool awaitHandler = true) where TComponent : IComponent
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (awaitHandler) await rendered.InvokeAsync(() => find().ClickAsync(new()));
                else await rendered.InvokeAsync(() => find().Click());
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 10) { rendered.Render(); }
        }
    }
}
