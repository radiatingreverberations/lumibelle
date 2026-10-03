using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class TakeInputChangesTests
{
    [Fact]
    public void MovedTakeComparesTheDestinationShotsAuthoredInputs()
    {
        var f = new Fixture();
        f.Source.Id = Guid.NewGuid(); f.Setup.ShotId = f.Source.Id;
        f.Setup.Id = Guid.NewGuid(); f.Setup.Shot.Id = f.Source.Id;
        Assert.Equal(TakeInputChange.None, f.Compare());
        f.Setup.Prompt = "The destination's different prompt.";
        Assert.Equal(TakeInputChange.Prompt, f.Compare());
    }

    [Fact]
    public void UnchangedSnapshotSurvivesSerializationAndMetadataChanges()
    {
        var f = new Fixture();
        f.Snapshot = ShotCopy.Of(f.Snapshot);
        f.Setup.Version++; f.Setup.Name = "Renamed"; f.Setup.Seed = 42; f.Setup.TakeCount = 3;
        f.Setup.Archived = true; f.Setup.AcceptedRevisionId = Guid.NewGuid();
        f.Source.Title = "Renamed shot"; f.Source.ApprovedScriptId = Guid.NewGuid();
        f.Setup.Shot.Resolution = VideoResolution.Detail; f.Setup.Shot.Turbo = true;
        f.Setup.Shot.Images[0].Name = "Renamed image";
        f.Setup.Shot.Images[0].Id = Guid.NewGuid();
        f.Current[1] = f.Current[1] with { Spans = [new(f.Current[1].Text, Bold: true)] };
        f.Current[3] = f.Current[3] with { Spans = [new("An unrelated scene was rewritten.")] };
        Assert.Equal(TakeInputChange.None, f.Compare());
    }

    [Fact]
    public void SourceImageLookOrganizationDoesNotChangeTheShotsReferenceInputs()
    {
        var f = new Fixture();
        var owner = f.Assets.Assets[0];
        owner.Images[0] = owner.Images[0] with { LookId = owner.Looks[0].Id };
        Assert.Null(f.Snapshot.Shot.Images[0].LookId);
        Assert.Null(f.Setup.Shot.Images[0].LookId);
        Assert.Equal(TakeInputChange.None, f.Compare());

        // Explicitly assigning a look to the shot reference does change its inputs.
        f.Setup.Shot.Images[0].LookId = owner.Looks[0].Id;
        Assert.Equal(TakeInputChange.References, f.Compare());
    }

    [Fact]
    public void SceneEditsAndDeletionAreFlaggedAndRevertingClearsTheFlag()
    {
        var f = new Fixture();
        f.Current[1] = f.Current[1] with { Spans = [new("Riley leaves instead.")] };
        Assert.Equal(TakeInputChange.Script, f.Compare());
        f.Current = f.Original.Select(b => b.Copy()).ToList();
        Assert.Equal(TakeInputChange.None, f.Compare());
        f.Current.RemoveRange(0, 2);
        Assert.Equal(TakeInputChange.Script, f.Compare());
    }

    [Fact]
    public void ATakeIsComparedWithTheSceneAsItWasWhenGeneratedNotWhenItsShotWasPlanned()
    {
        // The script was edited after the shot was planned, then the take was generated.
        var f = new Fixture();
        f.Current[1] = f.Current[1] with { Spans = [new("Riley enters before anyone else.")] };
        Assert.Equal(TakeInputChange.Script, f.Compare());
        f.Snapshot = f.Snapshot with { SceneFingerprint = TakeInputChanges.SceneFingerprint(f.Current, f.Source.SceneId) };
        Assert.Equal(TakeInputChange.None, f.Compare());
        // A later edit marks it, and only edits within its scene do.
        f.Current[3] = f.Current[3] with { Spans = [new("Rain falls on the street.")] };
        Assert.Equal(TakeInputChange.None, f.Compare());
        f.Current[1] = f.Current[1] with { Spans = [new("Riley runs in.")] };
        Assert.Equal(TakeInputChange.Script, f.Compare());
    }

    [Theory]
    [InlineData("action")]
    [InlineData("dialogue")]
    [InlineData("duration")]
    public void ChangedShotContentIsFlagged(string edit)
    {
        var f = new Fixture();
        if (edit == "action") f.Source.Description = "Riley leaves.";
        if (edit == "dialogue") f.Source.Dialogue[0].Text = "Goodbye.";
        if (edit == "duration") f.Source.Duration = 8;
        Assert.Equal(TakeInputChange.Script, f.Compare());
    }

    [Fact]
    public void ExactPromptContentIsComparedWithoutLineEndingNoise()
    {
        var f = new Fixture();
        f.Setup.Prompt = "  Riley enters.\r\nA still camera.\n";
        Assert.Equal(TakeInputChange.None, f.Compare());
        f.Setup.Prompt = "Riley enters.\nA moving camera.";
        Assert.Equal(TakeInputChange.Prompt, f.Compare());
    }

    [Theory]
    [InlineData("image")]
    [InlineData("order")]
    [InlineData("crop")]
    [InlineData("hint")]
    [InlineData("guidance")]
    [InlineData("look")]
    [InlineData("voice")]
    [InlineData("reel")]
    [InlineData("excerpt")]
    [InlineData("removed-image")]
    [InlineData("removed-voice")]
    public void ReferenceChangesAreFlagged(string edit)
    {
        var f = new Fixture();
        switch (edit)
        {
            case "image": f.Setup.Shot.Images[0].MediaId = f.Setup.Shot.Images[1].MediaId; break;
            case "order": f.Setup.Shot.Images.Reverse(); break;
            case "crop": f.Setup.Shot.Images[0].Crop = new() { Width = .5 }; break;
            case "hint": f.Setup.Shot.Images[0].AiUseHint = "Setting"; break;
            case "guidance": f.Assets.Assets[0] = f.Assets.Assets[0] with { PreservationGuidance = "Keep her round glasses." }; break;
            case "look":
                var owner = f.Assets.Assets[0];
                f.Assets.Assets[0] = owner with { Looks = [owner.Looks[0] with { Description = "Red coat" }] };
                break;
            case "voice": f.Setup.Shot.Voices[0].Start = 2; break;
            case "reel": f.Setup.Shot.Videos[0].Description = "Use the room as the setting."; break;
            case "excerpt": f.Setup.Shot.Videos[0].AudioExcerpt = new(1, 2); break;
            case "removed-image": f.Assets.Assets[0].Images.RemoveAt(0); break;
            case "removed-voice": f.Assets.Voices.Clear(); break;
        }
        Assert.Equal(TakeInputChange.References, f.Compare());
    }

    [Fact]
    public void UnknownOriginalContextIsExplicitAndNeverComparedWithAnotherSetup()
    {
        var f = new Fixture();
        Assert.Equal(TakeInputChange.Unavailable, TakeInputChanges.Compare(f.Snapshot, f.Source, null, f.Assets, f.Shots, f.Original, f.Current));
        var another = f.Setup.Copy(); another.Id = Guid.NewGuid(); another.Prompt = "Unrelated"; another.Shot.Images.Clear();
        Assert.Equal(TakeInputChange.Unavailable, TakeInputChanges.Compare(f.Snapshot, f.Source, another, f.Assets, f.Shots, f.Original, f.Current));
        Assert.Equal(TakeInputChange.Unavailable, TakeInputChanges.Compare(f.Snapshot, f.Source, f.Setup, f.Assets, f.Shots, null, f.Current));
        f.Snapshot = f.Snapshot with { Production = null };
        Assert.Equal(TakeInputChange.Unavailable, f.Compare());
    }

    [Fact]
    public void IndependentChangesAreReportedTogether()
    {
        var f = new Fixture();
        f.Source.Description = "Changed";
        f.Setup.Prompt = "Changed";
        f.Setup.Shot.Images.Clear();
        Assert.Equal(TakeInputChange.Script | TakeInputChange.Prompt | TakeInputChange.References, f.Compare());
    }

    private sealed class Fixture
    {
        public List<ScriptBlock> Original = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM — DAY"),
            ScriptBlock.Create(ScriptBlockKind.Action, "Riley enters."), ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. STREET — DAY"),
            ScriptBlock.Create(ScriptBlockKind.Action, "Traffic passes.")];
        public List<ScriptBlock> Current;
        public Shot Source;
        public ShotDocument Shots;
        public AssetLibrary Assets;
        public ProductionComposition Setup;
        public VideoSnapshot Snapshot;
        public Fixture()
        {
            Current = Original.Select(b => b.Copy()).ToList();
            var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character,
                Looks = [new() { Name = "Everyday", Description = "Gray shirt" }], Images = [Image(), Image()] };
            var voice = new VoiceReference { AssetId = owner.Id };
            Assets = new() { ProjectId = Guid.NewGuid(), Assets = [owner], Voices = [voice] };
            Source = new() { SceneId = Original[0].Id, ApprovedScriptId = Guid.NewGuid(), Description = "Riley enters.", Duration = 5,
                Dialogue = [new() { Speaker = "Riley", Text = "Hello." }],
                Characters = [new(Guid.NewGuid(), "Riley") { Appearance = new(owner.Id, owner.Looks[0].Id) }],
                Images = owner.Images.Select(i => new ShotImageBinding { AssetId = owner.Id, MediaId = i.Id }).ToList(),
                Voices = [new() { AssetId = owner.Id, VoiceId = voice.Id, Speaker = "Riley" }],
                Videos = [new() { Media = new(Guid.NewGuid(), new string('a', 64), 1234, 640, 480, 120, 24, 5, true), UseSoundtrack = true }] };
            Shots = new() { ProjectId = Assets.ProjectId, Shots = [Source] };
            Setup = new() { ShotId = Source.Id, Shot = Source.Copy(), Prompt = "Riley enters.\nA still camera." };
            Snapshot = new(Assets.ProjectId, 1, Source.Copy(), Setup.Prompt, "fingerprint", "http://localhost:8188", new(), 640, 480, 121)
            {
                Production = new(Setup.Id, 1, Setup.Name, new(Guid.NewGuid(), DateTimeOffset.UtcNow, Setup.Prompt, "", "", ""), []),
                ReferenceGuidance = ShotReferences.Resolve(Source, Assets, Shots), Appearances = ShotLooks.Capture(Source, Assets)
            };
        }
        public TakeInputChange Compare() => TakeInputChanges.Compare(Snapshot, Source, Setup, Assets, Shots, Original, Current);
        private static AssetImage Image() => new() { Id = Guid.NewGuid(), FileName = "image.png", ContentType = "image/png", Width = 640, Height = 480 };
    }
}
