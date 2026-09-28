using System.Text.Json;
using Bunit;
using lumibelle.Components;
using lumibelle.Services.Story;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class LoadFailureTests : BunitContext
{
    public LoadFailureTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; }

    private static WorkspaceStoreException Unreadable(Exception cause) =>
        new("Couldn’t read the shot list. Nothing was changed.", cause) { Data = { ["path"] = "C:/Films/Project/shots.json" } };

    [Theory]
    [InlineData("json", "The saved file is damaged")]
    [InlineData("denied", "isn’t allowed to open the file")]
    [InlineData("folder", "The project folder can’t be found")]
    [InlineData("busy", "Another program, such as a sync or backup tool")]
    public void AdviceFollowsTheCauseAndDetailsNameTheFile(string kind, string advice)
    {
        Exception cause = kind switch
        {
            "json" => new JsonException("Unexpected end of data."),
            "denied" => new UnauthorizedAccessException("Access denied."),
            "folder" => new DirectoryNotFoundException("Folder missing."),
            _ => new IOException("The file is in use."),
        };
        var ui = Render<LoadFailure>(p => p.Add(c => c.Title, "Couldn’t open Shots").Add(c => c.Error, Unreadable(cause)));
        Assert.Equal("Couldn’t open Shots", ui.Find("h1").TextContent);
        Assert.Equal("Couldn’t read the shot list. Nothing was changed.", ui.Find(".load-failure-message").TextContent);
        Assert.Contains(advice, ui.Markup);
        var details = ui.Find(".load-failure-details").TextContent;
        Assert.Contains("C:/Films/Project/shots.json", details); Assert.Contains(cause.Message, details);
    }

    [Fact]
    public void TryAgainRetriesAndPlainFailuresHaveNoTechnicalDetails()
    {
        var retries = 0;
        var ui = Render<LoadFailure>(p => p.Add(c => c.Title, "Couldn’t open the script")
            .Add(c => c.Error, new WorkspaceStoreException("The script is unavailable.")).Add(c => c.Retry, () => retries++));
        Assert.Empty(ui.FindAll("details"));
        Assert.Contains("Nothing has been changed. Try again in a moment.", ui.Markup);
        ui.FindAll("button").Single(b => b.TextContent.Trim() == "Try again").Click();
        Assert.Equal(1, retries);
    }
}
