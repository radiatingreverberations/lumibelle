using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BulkInstructionsAreCapturedForCreateAndEditWithoutChangingAssetDirection(bool edit)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, edit);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        if (edit) library = await f.Assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with {
            Images = a.Images.Select((i, index) => i with { IsReference = index == 0 }).ToList()
        }).ToList() }, library.Revision, ct);
        var original = AssetBatchPlan.Build(library, ImageWorkflow.Krea2)[0].Item;
        var instructions = "Full-body view. Keep the entire figure visible with space around the subject.";
        var item = original with { AdditionalInstructions = "  " + instructions + "  ", VisualStyle = new() { Preset = VisualStylePreset.Photographic } };
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "9:16", 1024, null, ct);
        var run = doc.Runs[0];
        var captured = run.Pending!.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal("9:16", captured.AspectRatio);
        Assert.Equal(edit, captured.Edit is not null);
        Assert.Contains(instructions, captured.Prompt);
        Assert.Contains("Photographic imagery", captured.Prompt);
        Assert.Contains(instructions, captured.OriginalPrompt);
        Assert.Equal(original.Prompt, run.Items[0].Prompt);
        Assert.Equal(instructions, run.Items[0].AdditionalInstructions);
        var saved = (await workflow.LoadAsync(f.Project.Id, ct)).Runs[0];
        Assert.Equal(instructions, saved.Items[0].AdditionalInstructions);
        Assert.True(JsonElement.DeepEquals(run.Pending.Snapshot, saved.Pending!.Snapshot));
        var unchanged = await f.Assets.LoadAsync(f.Project.Id, ct);
        Assert.Equal(library.Assets[0].Description, unchanged.Assets[0].Description);
        Assert.DoesNotContain(instructions, original.Prompt);
    }

    [Theory] [InlineData(6001, 30)] [InlineData(3000, 28000)]
    public async Task BulkRejectsOversizedInstructionsBeforeQueueing(int instructionsLength, int promptLength)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var item = AssetBatchPlan.Build(await f.Assets.LoadAsync(f.Project.Id, ct), ImageWorkflow.Krea2)[0].Item with {
            Prompt = new string('p', promptLength), AdditionalInstructions = new string('i', instructionsLength)
        };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "1:1", 1024, null, ct));
        Assert.Empty((await f.Jobs.ReadAsync(ct)).Jobs);
        Assert.Empty((await workflow.LoadAsync(f.Project.Id, ct)).Runs);
    }

    [Fact]
    public void BulkInstructionsPreserveStructuredCaptionFieldsAndLegacyPrompts()
    {
        var raw = """{"short_description":"A green robot","text_render":"HELLO","objects":[{"description":"brass key"}]}""";
        var item = new AssetBatchItem { Context = LookPolicy.Capture(Asset("Juniper", "Green robot"), null), Prompt = raw };
        Assert.Equal(raw, AssetBatchPlan.ImagePrompt(item));
        var directed = item with { AdditionalInstructions = "Full-body view; keep the feet visible." };
        using var caption = JsonDocument.Parse(VisualStyles.ApplyImage(AssetBatchPlan.ImagePrompt(directed), new() { Preset = VisualStylePreset.Photographic }, ImageWorkflow.Fibo2));
        Assert.Equal("HELLO", caption.RootElement.GetProperty("text_render").GetString());
        Assert.Equal("brass key", caption.RootElement.GetProperty("objects")[0].GetProperty("description").GetString());
        Assert.Contains(directed.AdditionalInstructions, caption.RootElement.GetProperty("short_description").GetString());
        Assert.Equal(raw, item.Prompt);
    }
}
