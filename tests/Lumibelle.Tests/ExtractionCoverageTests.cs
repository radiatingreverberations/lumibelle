using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private FileScriptStore ExtractionScripts()
    {
        var environment = new AssetEnvironment { ContentRootPath = _directory };
        var options = Options.Create(new ProjectStorageOptions { RootDirectory = "projects" });
        var projects = new FileProjectStore(options, environment, _clock, NullLogger<FileProjectStore>.Instance);
        return new(new ProjectFiles(options, environment, projects), _clock);
    }
    private async Task<ApprovedScriptSnapshot> ExtractionSource(Guid id)
    {
        var scripts = ExtractionScripts(); var doc = await scripts.LoadAsync(id, TestContext.Current.CancellationToken);
        doc = await scripts.SaveAsync(doc with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "Mira enters."),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. PARK - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "A tree shakes.")] }, doc.Revision, cancellationToken: TestContext.Current.CancellationToken);
        await scripts.ApproveAsync(id, doc.Revision, TestContext.Current.CancellationToken);
        return (await scripts.LoadApprovedAsync(id, cancellationToken: TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task ExtractionReviewIsAtomicIdempotentAndPreservedByOrdinaryImageSaves()
    {
        var (project, store) = CreateStore(); var source = await ExtractionSource(project.Id); var first = source.Blocks[0].Id;
        var proposal = new AssetExtractionProposal { Name = "Mira", Description = "Dark hair.", Category = AssetCategory.Character,
            Evidence = [new("Room", first, "Mira enters.", source.Id)], Looks = [new() { Name = "Everyday", Description = "A hoodie." }] };
        var input = new ExtractionReviewInput(Guid.NewGuid(), source.Id, [first], [proposal]);
        var saved = await store.FinishExtractionAsync(project.Id, input, 0, TestContext.Current.CancellationToken);
        Assert.Equal(1, saved.Revision); Assert.Single(saved.Assets); Assert.Single(saved.Assets[0].Looks);
        Assert.Equal(1, saved.ExtractionReviews[0].Decisions.Created); Assert.Equal(1, saved.ExtractionReviews[0].Decisions.LooksCreated);
        Assert.Equal(SceneCoverageState.Current, ExtractionCoverage.State(ExtractionCoverage.Scenes(source)[0], saved.ExtractionReviews));
        Assert.Equal(SceneCoverageState.NotReviewed, ExtractionCoverage.State(ExtractionCoverage.Scenes(source)[1], saved.ExtractionReviews));
        var retry = await store.FinishExtractionAsync(project.Id, input, 0, TestContext.Current.CancellationToken);
        Assert.Equal(saved.Revision, retry.Revision); Assert.Single(retry.Assets);
        proposal.Name = "Different decision";
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.FinishExtractionAsync(project.Id, input, saved.Revision, TestContext.Current.CancellationToken));
        var copy = saved.Copy(); ((ReviewedAssetScene[])copy.ExtractionReviews[0].Scenes)[0] = copy.ExtractionReviews[0].Scenes[0] with { Title = "Changed copy" };
        Assert.NotEqual(copy.ExtractionReviews[0].Scenes[0].Title, saved.ExtractionReviews[0].Scenes[0].Title);
        saved = await store.SaveAsync(saved with { ExtractionReviews = [] }, saved.Revision, TestContext.Current.CancellationToken);
        await using var image = new MemoryStream(Png(2, 2));
        saved = await store.AddImageAsync(project.Id, saved.Assets[0].Id, image, new("image.png", [], AssetImageOrigin.Imported), saved.Revision, TestContext.Current.CancellationToken);
        saved = (await store.DeleteImageAsync(project.Id, saved.Assets[0].Id, saved.Assets[0].Images[0].Id, saved.Revision, TestContext.Current.CancellationToken)).Library;
        Assert.Single(saved.ExtractionReviews); Assert.Single((await CreateFreshStore().LoadAsync(project.Id, TestContext.Current.CancellationToken)).ExtractionReviews);
    }

    [Fact]
    public async Task EmptyAndAllSkippedReviewsKeepCapturedApprovalAndDetectLaterChanges()
    {
        var (project, store) = CreateStore(); var source = await ExtractionSource(project.Id);
        var input = new ExtractionReviewInput(Guid.NewGuid(), source.Id, [source.Blocks[0].Id], []);
        var saved = await store.FinishExtractionAsync(project.Id, input, 0, TestContext.Current.CancellationToken);
        var scripts = ExtractionScripts(); var doc = await scripts.LoadAsync(project.Id, TestContext.Current.CancellationToken);
        // Unchanged content may have different emphasis, block IDs and a different scene order.
        var reformatted = source with { Id = Guid.NewGuid(), Blocks = [source.Blocks[2], source.Blocks[3], source.Blocks[0], source.Blocks[1] with { Id = Guid.NewGuid(), Spans = [new("Mira enters.", true)] }] };
        Assert.Equal(SceneCoverageState.Current, ExtractionCoverage.State(ExtractionCoverage.Scenes(reformatted)[1], saved.ExtractionReviews));
        doc.Blocks[1] = doc.Blocks[1] with { Spans = [new("Mira leaves.")] };
        doc = await scripts.SaveAsync(doc, doc.Revision, cancellationToken: TestContext.Current.CancellationToken);
        await scripts.ApproveAsync(project.Id, doc.Revision, TestContext.Current.CancellationToken);
        var latest = (await scripts.LoadApprovedAsync(project.Id, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(SceneCoverageState.Changed, ExtractionCoverage.State(ExtractionCoverage.Scenes(latest)[0], saved.ExtractionReviews));
        saved = await store.FinishExtractionAsync(project.Id, new(Guid.NewGuid(), source.Id, [source.Blocks[0].Id], [new() { Decision = ExtractionDecision.Skip }]), saved.Revision, TestContext.Current.CancellationToken);
        Assert.Empty(saved.Assets); Assert.Equal(source.Id, saved.ExtractionReviews[^1].ApprovedScriptId);
        Assert.Equal(SceneCoverageState.Changed, ExtractionCoverage.State(ExtractionCoverage.Scenes(latest)[0], saved.ExtractionReviews));
        saved = await store.FinishExtractionAsync(project.Id, new(Guid.NewGuid(), latest.Id, [latest.Blocks[0].Id], []), saved.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(SceneCoverageState.Current, ExtractionCoverage.State(ExtractionCoverage.Scenes(latest)[0], saved.ExtractionReviews));
        var typeChange = latest with { Blocks = latest.Blocks.Select(b => b == latest.Blocks[1] ? b with { Kind = ScriptBlockKind.Dialogue } : b).ToList() };
        Assert.Equal(SceneCoverageState.Changed, ExtractionCoverage.State(ExtractionCoverage.Scenes(typeChange)[0], saved.ExtractionReviews));
    }

    [Fact]
    public async Task FailedExtractionPublicationAndConflictsDoNotAdvanceCoverage()
    {
        var (project, store) = CreateStore(); var source = await ExtractionSource(project.Id);
        var saved = await store.SaveAsync(new() { ProjectId = project.Id }, 0, TestContext.Current.CancellationToken);
        var input = new ExtractionReviewInput(Guid.NewGuid(), source.Id, [source.Blocks[0].Id], []);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.FinishExtractionAsync(project.Id, input, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.FinishExtractionAsync(project.Id, input with { SceneIds = [Guid.NewGuid()] }, saved.Revision, TestContext.Current.CancellationToken));
        var path = Path.Combine(_directory, "projects", project.Id.ToString("D"), "assets.json");
        await using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(path)!))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.FinishExtractionAsync(project.Id, input, saved.Revision, TestContext.Current.CancellationToken));
        Assert.Empty((await store.LoadAsync(project.Id, TestContext.Current.CancellationToken)).ExtractionReviews);
        saved = await store.FinishExtractionAsync(project.Id, input, saved.Revision, TestContext.Current.CancellationToken);
        Assert.Single(saved.ExtractionReviews);
    }

    [Fact]
    public void EmptyExtractionIsValidButMalformedEmptyResponsesAreNot()
    {
        var source = ScriptFixtures.Approved([ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - DAY")]);
        var request = new AssetExtractionRequest(source, new() { ProjectId = source.ProjectId }, [source.Blocks[0].Id], AiBackend.ComfyUI, "mock");
        Assert.Empty(AssetExtractor.Parse("[]", request)!);
        Assert.Null(AssetExtractor.Parse("null", request)); Assert.Null(AssetExtractor.Parse("[", request)); Assert.Null(AssetExtractor.Parse("{}", request));
    }
}
