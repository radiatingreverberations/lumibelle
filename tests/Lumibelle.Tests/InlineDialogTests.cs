using Bunit;
using lumibelle.Components;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class InlineDialogTests : BunitContext
{
    public InlineDialogTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; Render<MudPopoverProvider>(); }

    [Fact]
    public async Task AFastCloseAndReopenKeepsTheReopenedDialogVisible()
    {
        var dialogs = Render<MudDialogProvider>();
        var visible = true; var hidden = 0;
        var cut = Render<InlineDialog>(p => p.Add(d => d.Visible, true)
            .Add(d => d.VisibleChanged, v => { visible = v; if (!v) hidden++; })
            .Add(d => d.DialogContent, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"inline-dialog-content\">Open</p>"))));
        dialogs.WaitForElement("#inline-dialog-content");

        // Close and reopen within one dispatcher turn: the first opening's close
        // continuation can then only run after the reopen, as happens under load.
        await cut.InvokeAsync(() => { cut.Render(p => p.Add(d => d.Visible, false)); cut.Render(p => p.Add(d => d.Visible, true)); });
        await Task.Delay(200, Xunit.TestContext.Current.CancellationToken); await cut.InvokeAsync(() => { });

        Assert.True(visible); Assert.Equal(0, hidden);
        dialogs.WaitForElement("#inline-dialog-content");
    }

    [Fact]
    public async Task ClosingFromInsideTheDialogStillReportsHidden()
    {
        var dialogs = Render<MudDialogProvider>();
        // The report arrives through a continuation, not a render, so await it directly.
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Render<InlineDialog>(p => p.Add(d => d.Visible, true).Add(d => d.VisibleChanged, v => { if (!v) hidden.TrySetResult(); })
            .Add(d => d.DialogContent, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"inline-dialog-content\">Open</p>"))));
        dialogs.WaitForElement("#inline-dialog-content");
        await dialogs.InvokeAsync(() => ((IMudDialogInstance)dialogs.FindComponent<MudDialogContainer>().Instance).Close());
        await hidden.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
    }
}
