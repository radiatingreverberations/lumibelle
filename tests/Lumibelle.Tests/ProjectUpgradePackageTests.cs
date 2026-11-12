using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ProjectPackageTests
{
    [Fact]
    public async Task LegacyPackageScriptIsUpgradedBeforePublication()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = new Fixture(); var project = await source.Create();
        // Construct the JSON document shape emitted by an older package exporter.
        await source.Save(project, "script.json", new ScriptDocument
            { SchemaVersion = 1, ProjectId = project.Id, Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "A train arrives.")] });
        var export = await source.Service.ExportAsync(project.Id, new(), ct: ct);
        await using var package = (await source.Service.OpenExportAsync(project.Id, export.Id, ct))!;
        using var target = new Fixture();
        var staged = await target.Service.StageImportAsync(package.Content, ct: ct);
        Assert.Empty((await target.Projects.ListAsync(ct)).Projects);
        await target.Service.CommitImportAsync(staged.Token, ct);
        Assert.Equal(2, lumibelle.Services.Storage.ProjectFormat.Inspect(target.Root(project)));
        var script = await new FileScriptStore(target.Files, TimeProvider.System).LoadAsync(project.Id, ct);
        Assert.Equal(2, script.SchemaVersion);
        Assert.Equal("A train arrives.", Assert.Single(script.Blocks).Text);
    }
}
