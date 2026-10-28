using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VisualStyleEnhancementInheritsUnlessExplicitlyDisabled(bool disabled)
    {
        var preferences = new FakeProjectAiPreferencesStore();
        preferences.Values[_project] = new() { ProjectId = _project, VisualStyle = new() { Preset = VisualStylePreset.Anime, Direction = "Muted green palette." } };
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers, preferences: preferences);
        var original = Enhancement();
        var request = original with { Context = original.Context with { VisualStyle = disabled ? new() : null } };
        var submission = await capture.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), request, _ct);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var captured = snapshot.Payload<PromptEnhancementRequest>();
        Assert.Equal(disabled ? VisualStylePreset.Unspecified : VisualStylePreset.Anime, captured.Context.VisualStyle!.Preset);
        if (disabled) {
            Assert.DoesNotContain("Muted green palette.", string.Join("\n", snapshot.Messages.SelectMany(m => m.Parts).Select(p => p.Text)));
            Assert.Equal(original.Context.Fingerprint(), captured.Context.Fingerprint());
            Assert.Empty(PromptEnhancer.Changes(original.Context, captured.Context));
        } else {
            var messages = PromptEnhancer.BuildMessages(captured, "Guide", []);
            Assert.Contains("Apply visualStyleDirection", messages[0].Text);
            Assert.Contains("Muted green palette.", messages[1].Text);
            Assert.NotEqual(original.Context.Fingerprint(), captured.Context.Fingerprint());
            Assert.Contains("the visual style changed", PromptEnhancer.Changes(original.Context, captured.Context));
        }
    }
}

public sealed partial class TextModelStoreTests
{
    [Fact]
    public async Task VisualStylePersistsPerProjectAndDoesNotOverwriteOtherPreferences()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await Projects.CreateAsync(new("Style test"), ct);
        var other = await Projects.CreateAsync(new("Other style"), ct);
        var anime = new VisualStyle { Preset = VisualStylePreset.Anime, Direction = "  Muted green palette; crisp linework.  " };
        await Task.WhenAll(Preferences.SetVisualStyleAsync(project.Id, anime, 0, ct), Preferences.SetImageDefaultAsync(project.Id, ImageWorkflow.Krea2, 0, ct), Preferences.SetTextDefaultAsync(project.Id, Cloud, 0, ct));
        var saved = await Preferences.LoadAsync(project.Id, ct);
        Assert.Equal(VisualStylePreset.Anime, saved.VisualStyle.Preset); Assert.Equal(anime.Direction.Trim(), saved.VisualStyle.Direction);
        Assert.Equal(Cloud, saved.TextDefault); Assert.Equal(ImageWorkflow.Krea2, saved.ImageDefault);
        Assert.Equal(VisualStylePreset.Unspecified, (await Preferences.LoadAsync(other.Id, ct)).VisualStyle.Preset);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Preferences.SetVisualStyleAsync(project.Id, anime, 0, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Preferences.SetVisualStyleAsync(project.Id, anime with { Preset = (VisualStylePreset)999 }, 1, ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Preferences.SetVisualStyleAsync(project.Id, anime with { Direction = new('x', 4001) }, 1, ct));
        Assert.Equal(1, (await Preferences.LoadAsync(project.Id, ct)).VisualStyleRevision);
    }
}

public sealed partial class AssetStoreTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VisualStyleImageCaptureFreezesProjectAssetAndRunChoices(bool edit)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, edit);
        var preferences = new FakeProjectAiPreferencesStore();
        var anime = new VisualStyle { Preset = VisualStylePreset.Anime, Direction = "Muted green palette." };
        preferences.Values[f.Project.Id] = new() { ProjectId = f.Project.Id, VisualStyle = anime };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, preferences: preferences);
        async Task<AiImageJobRequest> Captured(VisualStyle? style = null)
        {
            var id = Guid.NewGuid(); var tab = Guid.NewGuid();
            var submission = edit ? await capture.EditAsync(id, tab, f.AssetId, new() { ProjectId = f.Project.Id, SourceAssetId = f.AssetId, SourceImageId = f.References[0].ImageId, Prompt = "A small green robot", AspectRatio = "1:1", VisualStyle = style }, [f.References[0]], ct)
                : await capture.CreateAsync(id, tab, f.AssetId, new() { ProjectId = f.Project.Id, Prompt = "A small green robot", AspectRatio = "1:1", VisualStyle = style }, ct);
            return submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        }
        var first = await Captured();
        Assert.Contains("Anime", VisualStyles.Label((first.Create?.VisualStyle ?? first.Edit!.VisualStyle)!.Preset));
        Assert.Contains("Muted green palette", first.Prompt); Assert.Equal("A small green robot", first.OriginalPrompt);
        var photo = new VisualStyle { Preset = VisualStylePreset.Photographic };
        preferences.Values[f.Project.Id] = preferences.Values[f.Project.Id] with { VisualStyle = photo };
        Assert.Contains("Anime rendering", first.Prompt); Assert.DoesNotContain("Photographic imagery", first.Prompt);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        var assetStyle = new VisualStyle { Preset = VisualStylePreset.Illustration, Direction = "Watercolour paper texture." };
        await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { VisualStyle = assetStyle }] }, library.Revision, ct);
        Assert.Contains("Watercolour paper texture", (await Captured()).Prompt);
        var overridden = await Captured(photo); Assert.Contains("Photographic imagery", overridden.Prompt); Assert.DoesNotContain("Watercolour paper texture", overridden.Prompt);
        Assert.Equal("A small green robot", (await Captured(new())).Prompt);
    }

    [Fact]
    public async Task VisualStylePlansAndReelsCaptureTheSameDirectionWithoutChangingIdentity()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var library = await ApproveReelInputs(f);
        var style = new VisualStyle { Preset = VisualStylePreset.Anime, Direction = "Restrained cel shading." };
        var images = AssetBatchPlan.Build(library, ImageWorkflow.Krea2, style);
        var reels = AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterNeutralTurntable, projectStyle: style);
        Assert.Equal(images[0].Item.Context, reels[0].Item.Context); Assert.Equal(style, reels[0].Item.Recipe!.VisualStyle);
        var workflow = ReelWorkflow(f, out var queue); using var owned = queue;
        var doc = await workflow.StartReelsAsync(f.Project.Id, Guid.NewGuid(), AssetBatchKind.CharacterReels, [reels[0].Item], null, TestContext.Current.CancellationToken);
        var video = doc.Runs[0].Pending!.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(style, video.Snapshot.Reel!.Recipe.CapturedVisualStyle);
        Assert.Contains("Restrained cel shading", video.Snapshot.Prompt);
        Assert.DoesNotContain("Restrained cel shading", library.Assets[0].Description);
        var recipe = video.Snapshot.Reel.Recipe;
        Assert.Equal(ReferenceReels.Fingerprint(recipe), ReferenceReels.Fingerprint(recipe with { CapturedVisualStyle = null }));
        Assert.Equal(style, AssetBatchPlan.Build(library, ImageWorkflow.Krea2, style)[0].Item.VisualStyle);
    }

    [Fact]
    public void VisualStyleKeepsStructuredCaptionsAndUnspecifiedLegacyPromptsIntact()
    {
        var raw = """{"short_description":"A green robot","text_render":"HELLO","objects":[{"description":"brass key"}]}""";
        var style = new VisualStyle { Preset = VisualStylePreset.Illustration, Direction = "Ink and watercolour." };
        using var parsed = JsonDocument.Parse(VisualStyles.ApplyImage(raw, style, ImageWorkflow.Fibo2));
        Assert.Equal("HELLO", parsed.RootElement.GetProperty("text_render").GetString());
        Assert.Contains("Ink and watercolour", parsed.RootElement.GetProperty("artistic_style").GetString());
        Assert.Equal("brass key", parsed.RootElement.GetProperty("objects")[0].GetProperty("description").GetString());
        Assert.Equal(raw, VisualStyles.ApplyImage(raw, new(), ImageWorkflow.Fibo2));
        Assert.Equal("Original prompt", VisualStyles.Apply("Original prompt", null));
        Assert.Equal("", VisualStyles.Brief(new() { Preset = VisualStylePreset.Unspecified, Direction = "Old notes" }));
    }
}
