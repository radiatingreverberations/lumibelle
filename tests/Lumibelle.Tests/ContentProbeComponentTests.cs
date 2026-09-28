using AngleSharp.Dom;
using Bunit;
using lumibelle.Components.AI;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ContentProbeComponentTests
{
    private static IElement Button<T>(IRenderedComponent<T> view, string label) where T : IComponent =>
        view.FindAll("button").Single(b => b.TextContent.Trim() == label);
    private static AiJobCoordinator Register(BunitContext context, ContentProbeFixture f)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new ContentProbeJobHandler(f.Providers,
            new TestHttpFactory(new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"))),
            new ComfyJobExecution(TestComfy.Monitor()), TimeProvider.System);
        var queue = new AiJobCoordinator(f.Jobs, f.Settings, [handler], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        context.Services.AddSingleton<IContentProbeStore>(f.Probes);
        context.Services.AddSingleton<IAiJobStore>(f.Jobs);
        context.Services.AddSingleton<IAiSettingsStore>(f.Settings);
        context.Services.AddSingleton<IAiProviderRegistry>(f.Providers);
        context.Services.AddSingleton(f.Capture);
        context.Services.AddSingleton(queue);
        return queue; // Intentionally not started: UI tests cannot send inference.
    }
    [Fact]
    public void RatingHasFiveAccessibleValuesAndNoImplicitDefault()
    {
        using var context = new BunitContext(); var changes = new List<int?>();
        var view = context.Render<ContentProbeRating>(p => p.Add(c => c.ValueChanged, score => changes.Add(score)));
        Assert.Equal(5, view.FindAll("button[aria-pressed]").Count);
        Assert.All(view.FindAll("button[aria-pressed]"), b => Assert.Equal("false", b.GetAttribute("aria-pressed")));
        Assert.True(Button(view, "Clear").HasAttribute("disabled")); Assert.Empty(changes);
        Button(view, "4").Click(); Assert.Equal(4, Assert.Single(changes));
        var disabled = context.Render<ContentProbeRating>(p => p.Add(c => c.Disabled, true).Add(c => c.Value, 3));
        Assert.All(disabled.FindAll("button"), b => Assert.True(b.HasAttribute("disabled")));
    }
    [Fact]
    public async Task CustomTestSaveDoesNotGenerateAndItsMarkerCanBeDisabled()
    {
        using var f = new ContentProbeFixture(); using var context = new BunitContext();
        Register(context, f);
        var page = context.Render<ContentProbePage>();
        page.WaitForElement("#probe-library-title");
        Button(page, "New test").Click();
        page.Find("#probe-name").Change("Literal NO task");
        page.Find("#probe-category").Change("Custom classification");
        page.Find("#probe-prompt").Input("Return exactly NO.");
        page.Find("#probe-criteria").Change("The answer is exactly NO.");
        page.Find("#probe-refusal-marker").Change(false);
        page.Find("form.probe-editor").Submit();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("form.probe-editor")));
        var saved = Assert.Single((await f.Probes.LoadLibraryAsync(f.Ct)).Custom);
        Assert.False(saved.UseRefusalMarker); Assert.Equal("Return exactly NO.", saved.Prompt);
        Assert.Empty((await f.Jobs.ReadAsync(f.Ct)).Jobs); Assert.Equal(0, f.Providers.Creates);
    }
    [Fact]
    public async Task TwoProfilesOfOneModelCanBePreparedAndQueuedWithoutMergingThem()
    {
        using var f = new ContentProbeFixture(); using var context = new BunitContext();
        var first = ContentProbeFixture.Profile("Careful");
        var second = first with { ProfileId = Guid.NewGuid(), Name = "Fast", ReasoningEffort = "none" };
        f.Settings.Value = f.Settings.Value with { TextModelProfiles = [first, second] };
        Register(context, f);
        var page = context.Render<ContentProbePage>(); page.WaitForElement(".probe-model-choice");
        page.FindAll("input[aria-label^='Select test']")[0].Change(true);
        foreach (var labelText in page.FindAll(".probe-model-choice").Where(l => l.TextContent.Contains("· Profile")).Select(l => l.TextContent).ToArray())
            page.FindAll(".probe-model-choice").Single(l => l.TextContent == labelText).QuerySelector("input")!.Change(true);
        Button(page, "Prepare run").Click(); page.WaitForElement(".probe-confirmation");
        Assert.Equal(0, f.Providers.Creates); Assert.Empty((await f.Jobs.ReadAsync(f.Ct)).Jobs);
        Button(page, "Run 2 requests").Click();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tr[data-probe-job]").Count));
        var jobs = (await f.Jobs.ReadAsync(f.Ct)).Jobs;
        Assert.Equal(2, jobs.Count); Assert.All(jobs, j => Assert.Equal(AiJobState.Waiting, j.State));
        Assert.Equal(0, f.Providers.Creates);
    }
    [Fact]
    public async Task ResponseReviewEncodesTextSavesScoresAndPreservesAConflictingDraft()
    {
        using var f = new ContentProbeFixture(); using var context = new BunitContext();
        var row = await f.CompletedAsync(result: new("<script>alert('not executable')</script>", true, "stop"));
        Register(context, f);
        var page = context.Render<ContentProbePage>(); page.WaitForElement("tr[data-probe-job]");
        await Button(page, "Expand response").ClickAsync();
        Assert.Empty(page.FindAll(".probe-full-response script"));
        Assert.Contains("<script>", page.Find(".probe-full-response").TextContent);
        var details = page.Find(".probe-response-details");
        details.QuerySelectorAll("button").Single(b => b.TextContent == "4").Click();
        page.WaitForAssertion(() => Assert.Contains("Rating 4/5 saved", page.Markup));
        var saved = await f.Probes.LoadReviewAsync(row.Job.Id, f.Ct); Assert.Equal(4, saved.Score);
        page.Find("#probe-review-notes").Input("Keep this draft.");
        await f.Probes.SaveReviewAsync(saved with { Score = 5, Notes = "Other tab" }, f.Ct);
        Button(page, "Save notes").Click();
        page.WaitForAssertion(() => Assert.Contains("Another tab rated this response", page.Markup));
        Assert.Equal("Keep this draft.", page.Find("#probe-review-notes").GetAttribute("value"));
        Assert.Equal(5, (await f.Probes.LoadReviewAsync(row.Job.Id, f.Ct)).Score);
        Assert.Equal(0, f.Providers.Creates);
    }
}
