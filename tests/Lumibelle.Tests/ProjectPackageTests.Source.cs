using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ProjectPackageTests
{
    [Fact]
    public async Task SourceMaterialAndOutlineReviewsTravelWithProjectAndUnfinishedAnalysisStaysPaused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = new Fixture(); var project = await source.Create();
        var store = new FileSourceStore(source.Files);
        var text = "Chapter 1\nEira finds the key.";
        var doc = await store.SaveAsync(project.Id, 0, "", text, "", ct);
        var run = SourceAnalysisTests.Run(text, SourceAnalysis.Split(text, 1000));
        var accepted = run with { Id = Guid.NewGuid(), State = SourceRunState.Accepted, Proposal = SourceAnalysisTests.Outline(), Reviewed = SourceAnalysisTests.Outline(), ReviewRevision = 1 };
        run = run with { AnalysisFromRunId = accepted.Id };
        await store.UpdateAsync(project.Id, d => Task.FromResult(d with { Runs = [accepted, run] }), ct);
        var scriptStore = new FileScriptStore(source.Files, TimeProvider.System);
        var script = await scriptStore.LoadAsync(project.Id, ct);
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER - DAY");
        var link = new ScriptSourceScene(accepted.Id, accepted.ReviewRevision, 0, 0);
        await scriptStore.SaveAsync(script with { Blocks = [scene], SourceSceneLinks = [new(link, [scene.Id])] }, script.Revision, cancellationToken: ct);
        var export = await source.Service.ExportAsync(project.Id, new(), ct: ct);
        using var target = new Fixture();
        await using var media = (await source.Service.OpenExportAsync(project.Id, export.Id, ct))!;
        var staged = await target.Service.StageImportAsync(media.Content, ct: ct);
        await target.Service.CommitImportAsync(staged.Token, ct);
        var imported = await new FileSourceStore(target.Files).LoadAsync(project.Id, ct);
        Assert.Equal(text, imported.Text);
        Assert.Equal(SourceRunState.Accepted, imported.Runs[0].State);
        Assert.Equal("The key", imported.Runs[0].Reviewed!.Title);
        Assert.Equal(SourceRunState.Paused, imported.Runs[1].State);
        Assert.Null(imported.Runs[1].Pending);
        Assert.Equal(accepted.Id, imported.Runs[1].AnalysisFromRunId);
        Assert.Equal(link, Assert.Single(ScriptAdaptation.InScript(await new FileScriptStore(target.Files, TimeProvider.System).LoadAsync(project.Id, ct))));
        Assert.Equal(SourceRunState.Running, (await store.LoadAsync(project.Id, ct)).Runs[1].State);
    }
}
