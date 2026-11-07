using lumibelle.Models;
using lumibelle.Services.Review;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class ReviewChecksTests
{
    [Fact]
    public void ACleanShotHasNoFindings()
    {
        var f = new Fixture(); var shot = f.Shot("Vera at the window"); var take = f.Take(shot, select: true);
        f.Cut.Clips.Add(CutClip.From(shot, take));
        Assert.DoesNotContain(f.Run(), x => x.Check is not (ReviewChecks.PromptReview or ReviewChecks.PromptText));
    }

    [Fact]
    public void ReportsAShotWithoutReferencesAndACharacterWithoutAPicture()
    {
        var f = new Fixture();
        var bare = f.Shot("No references", references: false);
        var other = f.Shot("Only the room"); f.Setup(other).Shot.Images[0].AssetId = f.Room.Id; f.Setup(other).Shot.Images[0].MediaId = f.Room.Images[0].Id;

        var findings = f.Run().Where(x => x.Check == ReviewChecks.References).ToArray();

        Assert.Contains(findings, x => x.ShotId == bare.Id && x.ShotNumber == 1 && x.Message.StartsWith("No pictures or reels"));
        Assert.Contains(findings, x => x.ShotId == other.Id && x.Message.Contains("no picture or reel of Vera Sappleton"));
        Assert.All(findings, x => Assert.Equal(ReviewSeverity.Problem, x.Severity));
    }

    [Fact]
    public void ReportsAReferenceThatLeftTheLibrary()
    {
        var f = new Fixture(); var shot = f.Shot("Missing picture");
        f.Setup(shot).Shot.Images.Add(new() { AssetId = f.Vera.Id, MediaId = Guid.NewGuid(), Name = "Old portrait" });
        Assert.Contains(f.Run(), x => x.Check == ReviewChecks.References && x.Message.Contains("Old portrait"));
    }

    [Fact]
    public void ReportsASpeakerWithNoVoice()
    {
        var f = new Fixture(); var shot = f.Shot("Vera speaks", voice: false);
        var finding = Assert.Single(f.Run(), x => x.Check == ReviewChecks.Voices);
        Assert.Equal(shot.Id, finding.ShotId);
        Assert.Contains("No voice is chosen for VERA", finding.Message);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(14, false)]
    [InlineData(14.5, true)]   // 362 frames is 15.083 seconds.
    [InlineData(15, true)]
    [InlineData(1.5, true)]    // 39 frames is 1.625 seconds.
    [InlineData(2, false)]
    public void ReportsDurationsThatLandOutsideTheRefineRange(double seconds, bool expected)
    {
        var message = ReviewChecks.DurationFinding(new Shot { Duration = seconds });
        Assert.Equal(expected, message is not null);
        if (seconds == 14.5) Assert.Contains("362 frames, 15.083 s", message);
    }

    [Theory]
    [InlineData("*(quietly, to himself)*", true)]
    [InlineData("(She sighs.)", true)]
    [InlineData("[beat]", true)]
    [InlineData("*laughs*", true)]
    [InlineData("(Quietly) I knew it.", false)]
    [InlineData("Do you see them? (She points.)", false)]
    [InlineData("I *never* said that.", false)]
    [InlineData("", false)]
    public void RecognisesAStageDirectionStoredAsALine(string text, bool expected) => Assert.Equal(expected, ReviewChecks.IsStageDirection(text));

    [Fact]
    public void ReportsStageDirectionsInDialogue()
    {
        var f = new Fixture(); var shot = f.Shot("Aside"); shot.Dialogue.Add(new() { Speaker = "VERA", Text = "*(quietly, to herself)*" });
        f.Setup(shot).Shot.Dialogue.Add(shot.Dialogue[^1]);
        var finding = Assert.Single(f.Run(), x => x.Check == ReviewChecks.Dialogue);
        Assert.Equal(ReviewSeverity.Look, finding.Severity);
        Assert.Contains("quietly, to herself", finding.Message);
    }

    [Fact]
    public void ReportsLeftoverJsonAndPlaceholderText()
    {
        var f = new Fixture(); var shot = f.Shot("Leftovers");
        f.Setup(shot).Prompt = "Vera stands.\n{\"overall_soundscape\": \"placeholder\"}";
        var messages = f.Run().Where(x => x.Check == ReviewChecks.PromptText).Select(x => x.Message).ToArray();
        Assert.Contains(messages, m => m.Contains("leftover JSON"));
        Assert.Contains(messages, m => m.Contains("placeholder"));
        f.Setup(shot).Prompt = "Vera stands by the open window. She says the word placeholders once.";
        Assert.DoesNotContain(f.Run(), x => x.Check == ReviewChecks.PromptText && (x.Message.Contains("JSON") || x.Message.Contains("“placeholder”")));
    }

    [Fact]
    public void ReportsMissingPromptAsAProblemAndUnreviewedAsALook()
    {
        var f = new Fixture(); var missing = f.Shot("No prompt"); f.Setup(missing).Prompt = "";
        var unreviewed = f.Shot("Unreviewed");
        var findings = f.Run().Where(x => x.Check == ReviewChecks.PromptReview).ToArray();
        Assert.Equal(ReviewSeverity.Problem, Assert.Single(findings, x => x.ShotId == missing.Id).Severity);
        Assert.Equal(ReviewSeverity.Look, Assert.Single(findings, x => x.ShotId == unreviewed.Id).Severity);
    }

    [Fact]
    public void ReportsASelectedTakeRenderedBeforeItsPromptChanged()
    {
        var f = new Fixture(); var shot = f.Shot("Rewritten"); var take = f.Take(shot, select: true);
        Assert.DoesNotContain(f.Run(), x => x.Check == ReviewChecks.TakeInputs);
        f.Setup(shot).Prompt += " She now holds a letter.";
        var finding = Assert.Single(f.Run(), x => x.Check == ReviewChecks.TakeInputs);
        Assert.Equal(take.Id, finding.TakeId);
        Assert.Contains("before the prompt changed", finding.Message);
    }

    [Fact]
    public void ReportsShotsWithoutASelectedTake()
    {
        var f = new Fixture(); var none = f.Shot("No takes"); var unpicked = f.Shot("Unpicked"); f.Take(unpicked); f.Take(unpicked);
        var findings = f.Run().Where(x => x.Check == ReviewChecks.Takes).ToArray();
        Assert.Equal("No takes yet.", Assert.Single(findings, x => x.ShotId == none.Id).Message);
        Assert.Contains("2 takes", Assert.Single(findings, x => x.ShotId == unpicked.Id).Message);
    }

    [Fact]
    public void ReportsShotFramesFromReplacedTrashedAndMissingTakes()
    {
        var f = new Fixture();
        var source = f.Shot("Source"); var old = f.Take(source); f.Take(source, select: true);
        var trashed = f.Take(source); f.Shots.Takes.Remove(trashed); f.Shots.Trash.Add(new() { Take = trashed });
        var uses = f.Shot("Uses frames");
        f.Setup(uses).Shot.ShotFrames = [new(Guid.NewGuid(), old.Id, 38, "Old end"), new(Guid.NewGuid(), trashed.Id, 0, "Trashed"), new(Guid.NewGuid(), Guid.NewGuid(), 0, "Gone")];
        f.Setup(uses).Shot.StartFrame = new(old.Id, 99);

        var findings = f.Run().Where(x => x.Check == ReviewChecks.ShotFrames).ToArray();

        Assert.All(findings, x => Assert.Equal(uses.Id, x.ShotId));
        Assert.Contains(findings, x => x.Severity == ReviewSeverity.Look && x.Message.Contains("“Old end”") && x.Message.Contains("shot #1, which is not that shot's selected take"));
        Assert.Contains(findings, x => x.Severity == ReviewSeverity.Problem && x.Message.Contains("“Trashed” comes from a take that is in Trash"));
        Assert.Contains(findings, x => x.Severity == ReviewSeverity.Problem && x.Message.Contains("“Gone” comes from a take that no longer exists"));
        Assert.Contains(findings, x => x.Severity == ReviewSeverity.Problem && x.Message.Contains("The starting frame is frame 100 of a take with 39 frames"));
    }

    [Fact]
    public void ReportsACutThatIgnoresTheSelectedTakes()
    {
        var f = new Fixture();
        var a = f.Shot("A"); var first = f.Take(a); f.Take(a, select: true);
        var b = f.Shot("B"); f.Take(b, select: true);
        Assert.Contains("The cut is empty, and 2 shots have a selected take", Assert.Single(f.Run(), x => x.Check == ReviewChecks.Cut).Message);

        f.Cut.Clips.Add(CutClip.From(a, first));
        var findings = f.Run().Where(x => x.Check == ReviewChecks.Cut).ToArray();
        Assert.Contains(findings, x => x.ShotId == a.Id && x.Severity == ReviewSeverity.Problem && x.Message.StartsWith("The cut uses another take"));
        Assert.Contains(findings, x => x.ShotId is null && x.Message == "shot #2 has a selected take and is not in the cut.");
    }

    [Fact]
    public void ReportsACharacterVoicedByDifferentRecordingsAcrossSelectedTakes()
    {
        var f = new Fixture();
        var a = f.Shot("A"); f.Take(a, select: true);
        var b = f.Shot("B"); f.Take(b, select: true);
        Assert.DoesNotContain(f.Run(), x => x.Check == ReviewChecks.VoiceConsistency);

        var other = new VoiceReference { AssetId = f.Vera.Id, Name = "Vera, older take" }; f.Assets.Voices.Add(other);
        var c = f.Shot("C"); f.Setup(c).Shot.Voices[0].VoiceId = other.Id; f.Take(c, select: true);

        var finding = Assert.Single(f.Run(), x => x.Check == ReviewChecks.VoiceConsistency);
        Assert.Contains("Vera Sappleton has 2 different voices", finding.Message);
        Assert.Contains("shots #1 and #2", finding.Message);
        Assert.Contains("Vera, older take in shot #3", finding.Message);
    }

    [Fact]
    public void OrdersShotsSoFrameSourcesRenderFirst()
    {
        var f = new Fixture();
        var seven = f.Shot("7"); var nine = f.Shot("9"); var eleven = f.Shot("11"); var free = f.Shot("free");
        f.Setup(seven).Shot.ShotFrames = [new(Guid.NewGuid(), f.Take(nine).Id, 0, "from 9")];
        f.Setup(eleven).Shot.StartFrame = new(f.Take(seven).Id, 38);
        // A frame from the shot's own earlier take orders nothing.
        f.Setup(free).Shot.ShotFrames = [new(Guid.NewGuid(), f.Take(free).Id, 0, "own")];

        var order = ShotFrameOrder.Build(f.Snapshot());

        Assert.Equal([[nine.Id, free.Id], [seven.Id], [eleven.Id]], order.Waves);
        Assert.Empty(order.Cycles);
        Assert.Equal([seven.Id], order.Dependants(nine.Id));
    }

    [Fact]
    public void ReportsShotsThatTakeFramesFromEachOther()
    {
        var f = new Fixture();
        var a = f.Shot("A"); var b = f.Shot("B"); var c = f.Shot("C"); var d = f.Shot("D");
        f.Setup(a).Shot.ShotFrames = [new(Guid.NewGuid(), f.Take(b).Id, 0, "from B")];
        f.Setup(b).Shot.ShotFrames = [new(Guid.NewGuid(), f.Take(a).Id, 0, "from A")];
        f.Setup(c).Shot.ShotFrames = [new(Guid.NewGuid(), f.Take(a).Id, 1, "after the cycle")];

        var order = ShotFrameOrder.Build(f.Snapshot());

        Assert.Equal([[d.Id]], order.Waves);
        Assert.Equal([a.Id, b.Id], Assert.Single(order.Cycles));
        Assert.Contains("#1 and #2 take frames from one another", Assert.Single(f.Run(), x => x.Check == ReviewChecks.Order).Message);
    }

    [Fact]
    public void ReportsACycleThatLeadsIntoAnotherCycle()
    {
        var f = new Fixture();
        var a = f.Shot("A"); var b = f.Shot("B"); var c = f.Shot("C"); var d = f.Shot("D");
        var (ta, tb, tc, td) = (f.Take(a).Id, f.Take(b).Id, f.Take(c).Id, f.Take(d).Id);
        f.Setup(a).Shot.ShotFrames = [new(Guid.NewGuid(), tb, 0, "from B")];
        f.Setup(b).Shot.ShotFrames = [new(Guid.NewGuid(), ta, 0, "from A")];
        // C waits for A, whose cycle is found first, and for D, which waits for C.
        f.Setup(c).Shot.ShotFrames = [new(Guid.NewGuid(), ta, 0, "from A"), new(Guid.NewGuid(), td, 0, "from D")];
        f.Setup(d).Shot.ShotFrames = [new(Guid.NewGuid(), tc, 0, "from C")];

        var order = ShotFrameOrder.Build(f.Snapshot());

        Assert.Empty(order.Waves);
        Assert.Equal([[a.Id, b.Id], [c.Id, d.Id]], order.Cycles);
    }

    [Theory]
    [InlineData(59.96, "1:00")]
    [InlineData(2.96, "0:03")]
    [InlineData(2.04, "0:02")]
    [InlineData(3.54, "0:03.5")]
    [InlineData(119.95, "2:00")]
    public void FormatsCutTimesRoundedAsAWhole(double seconds, string text) => Assert.Equal(text, CutTimes.Format(seconds));

    [Fact]
    public void LocatedAndListedFramesAreCountedFromOne()
    {
        var f = new Fixture();
        var a = f.Shot("A"); f.Cut.Clips.Add(CutClip.From(a, f.Take(a, frames: 48)));
        var snapshot = f.Snapshot();

        // Time zero is the take's first frame, which get_frames calls frame 1.
        Assert.Equal(1, System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Locate(snapshot, "0")).GetProperty("takeFrame").GetInt32());
        var frames = System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Cut(snapshot)).GetProperty("clips")[0].GetProperty("frames");
        Assert.Equal((1, 48), (frames.GetProperty("first").GetInt32(), frames.GetProperty("last").GetInt32()));
    }

    [Fact]
    public void FollowsAPictureThroughATakeInTrashToTheTakesRenderedFromIt()
    {
        var f = new Fixture(); var image = f.Room.Images[0];
        var a = f.Shot("A"); f.Setup(a).Shot.Images.Add(new() { AssetId = f.Room.Id, MediaId = image.Id, Name = "Room" });
        var aTake = f.Take(a); f.Setup(a).Shot.Images.Clear();
        var b = f.Shot("B"); f.Setup(b).Shot.ShotFrames = [new(Guid.NewGuid(), aTake.Id, 38, "A end")];
        var bTake = f.Take(b, select: true);
        // A's take goes to Trash; B's take still carries the picture through its frame.
        f.Shots.Takes.Remove(aTake); f.Shots.Trash.Add(new() { Take = aTake });

        var uses = ReferenceUsage.Find(f.Snapshot(), f.Room.Id, image.Id);

        var use = Assert.Single(uses);
        Assert.Equal((bTake.Id, "Selected take"), (use.TakeId!.Value, use.Kind));
        Assert.Contains("now in Trash", use.Detail);
    }

    [Fact]
    public void ReportsAnOutdatedClipBesideOneThatShowsTheSelectedTake()
    {
        var f = new Fixture();
        var a = f.Shot("A"); var old = f.Take(a); var selected = f.Take(a, select: true);
        f.Cut.Clips.Add(CutClip.From(a, selected)); f.Cut.Clips.Add(CutClip.From(a, old));

        var finding = Assert.Single(f.Run(), x => x.Check == ReviewChecks.Cut);

        Assert.Equal((a.Id, ReviewSeverity.Problem), (finding.ShotId, finding.Severity));
        Assert.Equal("1 of this shot's 2 clips in the cut uses another take than the one selected.", finding.Message);
    }

    [Fact]
    public void APlanReadNamesTheTakeAUseTakeStepSelects()
    {
        var f = new Fixture();
        var a = f.Shot("A"); f.Take(a, select: true); var other = f.Take(a);
        var step = new ReviewPlanStep { Kind = ReviewStepKind.UseTake, ShotId = a.Id, TakeId = other.Id, State = ReviewStepState.Proposed, Reason = "The other take holds the look." };
        var plan = new ReviewPlan { Title = "Swap", Steps = [step] };

        var whole = System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan));
        var single = System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan, step.Id));

        Assert.Equal(other.Id, whole.GetProperty("steps")[0].GetProperty("takeToUse").GetGuid());
        Assert.Equal(other.Id, single.GetProperty("takeToUse").GetGuid());
    }

    [Fact]
    public void APlanReadNamesTheFrameAStepRemoves()
    {
        var f = new Fixture();
        var a = f.Shot("A"); var b = f.Shot("B"); var take = f.Take(b);
        f.Setup(a).Shot.ShotFrames = [new(Guid.NewGuid(), take.Id, 0, "Start"), new(Guid.NewGuid(), take.Id, 38, "End")];
        var step = new ReviewPlanStep { Kind = ReviewStepKind.RemoveShotFrame, ShotId = a.Id, State = ReviewStepState.Proposed, Reason = "The end is not needed.",
            Frame = new() { TakeId = take.Id, Position = ReviewFramePosition.Index, Index = 38 } };
        var plan = new ReviewPlan { Title = "Drop", Steps = [step] };

        foreach (var read in new[] { System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan)).GetProperty("steps")[0],
            System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan, step.Id)) })
        {
            var frame = read.GetProperty("frame");
            Assert.Equal((take.Id, 39), (frame.GetProperty("take").GetGuid(), frame.GetProperty("number").GetInt32()));
        }
    }

    [Fact]
    public void APlanReadListsAnEditsFurtherReferencesInOrder()
    {
        var f = new Fixture();
        var references = new AssetImageReference[] { new(f.Room.Id, f.Room.Images[0].Id), new(f.Vera.Id, f.Vera.Images[0].Id) };
        var step = new ReviewPlanStep { Kind = ReviewStepKind.EditImage, AssetId = f.Vera.Id, ImageId = f.Vera.Images[0].Id, After = "Make it night.", Takes = 1,
            State = ReviewStepState.Proposed, Reason = "Night.", ReferenceImages = references };
        var plan = new ReviewPlan { Title = "Night", Steps = [step] };

        foreach (var read in new[] { System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan)).GetProperty("steps")[0],
            System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan, step.Id)) })
            Assert.Equal(references, read.GetProperty("references").EnumerateArray().Select(r => new AssetImageReference(r.GetProperty("asset").GetGuid(), r.GetProperty("image").GetGuid())));
    }

    [Fact]
    public void APlanReadNamesTheReplacementPictureById()
    {
        var f = new Fixture();
        var a = f.Shot("A");
        // Two images of one asset with the same name: only the ids tell them apart.
        var twin = new AssetImage { Id = Guid.NewGuid(), Name = f.Vera.Images[0].Name, FileName = "twin.png", ContentType = "image/png", Width = 64, Height = 64 };
        f.Vera.Images.Add(twin);
        var step = new ReviewPlanStep { Kind = ReviewStepKind.ReplacePicture, ShotId = a.Id, AssetId = f.Room.Id, ImageId = f.Room.Images[0].Id,
            NewAssetId = f.Vera.Id, NewImageId = twin.Id, State = ReviewStepState.Proposed, Reason = "The other portrait." };
        var plan = new ReviewPlan { Title = "Swap", Steps = [step] };

        foreach (var read in new[] { System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan)).GetProperty("steps")[0],
            System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan, step.Id)) })
            Assert.Equal((f.Vera.Id, twin.Id), (read.GetProperty("newAsset").GetGuid(), read.GetProperty("newImage").GetGuid()));
    }

    [Fact]
    public void FollowsAPictureThroughAReelGeneratedWithItIntoTakesAndTheirFrames()
    {
        var f = new Fixture(); var image = f.Room.Images[0];
        // A reel generated from a shot that used the picture.
        var generated = new Shot { Title = "Reel", Images = [new() { AssetId = f.Room.Id, MediaId = image.Id, Name = "Room" }] };
        var media = new ReferenceVideoMedia(Guid.NewGuid(), "sha", 3, 64, 64, 48, 24, 2, false);
        var snapshot = new VideoSnapshot(f.Assets.ProjectId, 1, generated, "prompt", "fingerprint", "http://localhost:8188", new(), 64, 64, 48);
        f.Assets.Reels.Add(new() { AssetId = f.Room.Id, Name = "Room walk", Media = media, Generation = new(null!, Guid.NewGuid(), Guid.NewGuid(), 1, 1, snapshot) });
        // A shot renders with the reel, and another takes a frame from that take.
        var a = f.Shot("A"); f.Setup(a).Shot.Videos = [new() { Media = media, Name = "Room walk" }]; var aTake = f.Take(a, select: true);
        var b = f.Shot("B"); f.Setup(b).Shot.ShotFrames = [new(Guid.NewGuid(), aTake.Id, 38, "A end")]; var bTake = f.Take(b, select: true);

        var uses = ReferenceUsage.Find(f.Snapshot(), f.Room.Id, image.Id);

        Assert.Single(uses, u => u.Kind == "Reel");
        Assert.Equal(a.Id, Assert.Single(uses, u => u.Kind == "Shot").ShotId);
        Assert.Equal([aTake.Id, bTake.Id], uses.Where(u => u.Kind == "Selected take").Select(u => u.TakeId!.Value));
        Assert.Contains("Room walk", uses.Single(u => u.TakeId == aTake.Id).Detail);
    }

    [Fact]
    public void APlanReadNamesTheVoiceRecordingById()
    {
        var f = new Fixture();
        var a = f.Shot("A");
        // Two recordings with the same name: only the ids tell them apart.
        var twin = new VoiceReference { AssetId = f.Vera.Id, Name = f.Voice.Name };
        f.Assets.Voices.Add(twin);
        var set = new ReviewPlanStep { Kind = ReviewStepKind.SetVoice, ShotId = a.Id, CharacterAssetId = f.Vera.Id, VoiceId = twin.Id, State = ReviewStepState.Proposed, Reason = "The other take of the line." };
        var none = set with { Id = Guid.NewGuid(), VoiceId = null, Reason = "No voice." };
        var plan = new ReviewPlan { Title = "Voices", Steps = [set, none] };

        foreach (var read in new[] { System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan)).GetProperty("steps")[0],
            System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan, set.Id)) })
            Assert.Equal((f.Vera.Id, twin.Id.ToString()), (read.GetProperty("character").GetGuid(), read.GetProperty("voice").GetString()));
        Assert.Equal("none", System.Text.Json.JsonSerializer.SerializeToElement(ReviewQueries.Plan(f.Snapshot(), plan, none.Id)).GetProperty("voice").GetString());
    }

    [Fact]
    public void FollowsAPictureThroughATakeFrameIntoAReelAndTheTakesThatUseIt()
    {
        var f = new Fixture(); var image = f.Room.Images[0];
        // Take A is rendered with the picture.
        var a = f.Shot("A"); f.Setup(a).Shot.Images.Add(new() { AssetId = f.Room.Id, MediaId = image.Id, Name = "Room" }); var aTake = f.Take(a);
        // A reel is generated from a shot that uses a frame of take A, not the picture itself.
        var generated = new Shot { Title = "Reel", ShotFrames = [new(Guid.NewGuid(), aTake.Id, 38, "A end")] };
        var media = new ReferenceVideoMedia(Guid.NewGuid(), "sha", 3, 64, 64, 48, 24, 2, false);
        var snapshot = new VideoSnapshot(f.Assets.ProjectId, 1, generated, "prompt", "fingerprint", "http://localhost:8188", new(), 64, 64, 48);
        f.Assets.Reels.Add(new() { AssetId = f.Room.Id, Name = "Room walk", Media = media, Generation = new(null!, Guid.NewGuid(), Guid.NewGuid(), 1, 1, snapshot) });
        // Take C is rendered with that reel.
        var c = f.Shot("C"); f.Setup(c).Shot.Videos = [new() { Media = media, Name = "Room walk" }]; var cTake = f.Take(c, select: true);

        var uses = ReferenceUsage.Find(f.Snapshot(), f.Room.Id, image.Id);

        Assert.Contains("frame from", Assert.Single(uses, u => u.Kind == "Reel").Detail);
        Assert.Contains("Room walk", Assert.Single(uses, u => u.TakeId == cTake.Id).Detail);
        Assert.Contains(uses, u => u.TakeId == aTake.Id);
    }

    [Theory]
    [InlineData("234.5", 234.5)]
    [InlineData("3:54", 234)]
    [InlineData("3:54.2", 234.2)]
    [InlineData("1:03:54", 3834)]
    public void ParsesCutTimes(string text, double seconds) => Assert.Equal(seconds, CutTimes.Parse(text), 3);

    [Theory]
    [InlineData("")]
    [InlineData("3:")]
    [InlineData("abc")]
    [InlineData("-4")]
    [InlineData("1:2:3:4")]
    public void RejectsUnreadableCutTimes(string text) => Assert.Throws<lumibelle.Services.Story.WorkspaceStoreException>(() => CutTimes.Parse(text));

    [Fact]
    public void LocatesATimeInTheCutAsAShotAndTakeFrame()
    {
        var f = new Fixture();
        var a = f.Shot("A"); var b = f.Shot("B");
        f.Cut.Clips.Add(CutClip.From(a, f.Take(a, frames: 48)));
        var trimmed = CutClip.From(b, f.Take(b, frames: 120)); trimmed.StartFrame = 24; trimmed.EndFrameExclusive = 72; f.Cut.Clips.Add(trimmed);

        Assert.Equal(4, CutTimes.Duration(f.Cut), 3);
        var first = CutTimes.Locate(f.Cut, 1.5)!;
        Assert.Equal((0, a.Id, 36), (first.ClipIndex, first.Clip.ShotId, first.TakeFrame));
        var second = CutTimes.Locate(f.Cut, 2.5)!;
        Assert.Equal((1, b.Id, 36, 2d), (second.ClipIndex, second.Clip.ShotId, second.TakeFrame, second.ClipStartSeconds));
        Assert.Equal(71, CutTimes.Locate(f.Cut, 4)!.TakeFrame);
        Assert.Null(CutTimes.Locate(f.Cut, 4.1));
        Assert.Equal("3:54", CutTimes.Format(234)); Assert.Equal("0:02.5", CutTimes.Format(2.5));
    }

    [Fact]
    public void FindsEverythingMadeFromAReferenceImageIncludingFramesTakenDownstream()
    {
        var f = new Fixture(); var image = f.Room.Images[0];
        var wide = f.Shot("Wide"); f.Setup(wide).Shot.Images.Add(new() { AssetId = f.Room.Id, MediaId = image.Id, Name = "Room" });
        var wideTake = f.Take(wide, select: true);
        // The shot stops selecting the picture; its rendered take still carries it.
        f.Setup(wide).Shot.Images.RemoveAt(1);
        var close = f.Shot("Close"); f.Setup(close).Shot.ShotFrames = [new(Guid.NewGuid(), wideTake.Id, 38, "Wide end")];
        var closeTake = f.Take(close, select: true);
        var later = f.Shot("Later"); f.Setup(later).Shot.StartFrame = new(closeTake.Id, 38); var laterTake = f.Take(later);
        var clean = f.Shot("Clean"); f.Take(clean, select: true);
        var still = f.Shot("Still selects it"); f.Setup(still).Shot.Images.Add(new() { AssetId = f.Room.Id, MediaId = image.Id, Name = "Room" });

        var uses = ReferenceUsage.Find(f.Snapshot(), f.Room.Id, image.Id);

        Assert.Equal(still.Id, Assert.Single(uses, u => u.Kind == "Shot").ShotId);
        Assert.Equal([wideTake.Id, closeTake.Id], uses.Where(u => u.Kind == "Selected take").Select(u => u.TakeId!.Value));
        Assert.Equal(laterTake.Id, Assert.Single(uses, u => u.Kind == "Take").TakeId);
        Assert.Contains("frame from", uses.Single(u => u.TakeId == closeTake.Id).Detail);
        Assert.DoesNotContain(uses, u => u.ShotId == clean.Id);
    }

    [Fact]
    public void ParsesFfmpegLevelsAndSilence()
    {
        const string log = """
            [silencedetect @ 000001] silence_start: 0
            [silencedetect @ 000001] silence_end: 1.25 | silence_duration: 1.25
            [silencedetect @ 000001] silence_start: 4.5
            [Parsed_volumedetect_0 @ 000002] n_samples: 320000
            [Parsed_volumedetect_0 @ 000002] mean_volume: -23.4 dB
            [Parsed_volumedetect_0 @ 000002] max_volume: -3.1 dB
            """;
        var levels = AudioLevels.Parse(log, 5);
        Assert.Equal((-23.4, -3.1), (levels.MeanDb, levels.PeakDb));
        Assert.Equal([(0, 1.25), (4.5, 5)], levels.Silences);
        Assert.Null(AudioLevels.Parse("mean_volume: -inf dB", 5).MeanDb);
    }

    [Fact]
    public void ReadsSilenceTimestampsWrittenInScientificNotation()
    {
        var levels = AudioLevels.Parse("[silencedetect @ 000001] silence_start: 2.08333e-05\n[silencedetect @ 000001] silence_end: 5 | silence_duration: 4.99998\n", 5);

        var silence = Assert.Single(levels.Silences);
        Assert.InRange(silence.Start, 0, .001);
        Assert.Equal(5, silence.End);
    }

    [Fact]
    public void ReportsTakesThatStandOutByEar()
    {
        var f = new Fixture();
        var shots = Enumerable.Range(0, 5).Select(i => f.Shot("S" + i)).ToArray(); var takes = shots.Select(s => f.Take(s, select: true)).ToArray();
        TakeAudioLevel Level(int i, double? mean, bool audio = true, params (double, double)[] silences) => new(shots[i].Id, takes[i].Id, audio, mean, mean + 12, 5, silences);
        var findings = ReviewMedia.LevelFindings(f.Snapshot(), [Level(0, -22), Level(1, -24), Level(2, -40), Level(3, null, false), Level(4, -23, true, (0.2, 4.8))]);

        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, x => x.ShotId == shots[2].Id && x.Message.Contains("16.5 dB quieter than the film's median of -23.5 dB"));
        Assert.Contains(findings, x => x.ShotId == shots[3].Id && x.Message == "The selected take has no soundtrack.");
        Assert.Contains(findings, x => x.ShotId == shots[4].Id && x.Message.Contains("4.6 of the take's 5 seconds are silent"));
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Epoch = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        public readonly ReferenceAsset Vera = new() { Id = Guid.NewGuid(), Name = "Vera Sappleton", Category = AssetCategory.Character, Images = [Image()] };
        public readonly ReferenceAsset Room = new() { Id = Guid.NewGuid(), Name = "Drawing room", Category = AssetCategory.Environment, Images = [Image()] };
        public readonly VoiceReference Voice;
        public readonly AssetLibrary Assets;
        public readonly ShotDocument Shots;
        public readonly ProductionDocument Production;
        public readonly CutDocument Cut;
        private int _minutes;
        public Fixture()
        {
            Voice = new() { AssetId = Vera.Id, Name = "Vera" };
            Assets = new() { ProjectId = Guid.NewGuid(), Assets = [Vera, Room], Voices = [Voice] };
            Shots = new() { ProjectId = Assets.ProjectId }; Production = new() { ProjectId = Assets.ProjectId }; Cut = new() { ProjectId = Assets.ProjectId };
        }
        public Shot Shot(string title, bool references = true, bool voice = true)
        {
            var shot = new Shot { Title = title, Description = "Vera stands by the window.", Duration = 5, Dialogue = [new() { Speaker = "VERA", Text = "Do you see them?" }] };
            Shots.Shots.Add(shot);
            var shared = shot.Copy();
            if (references) shared.Images = [new() { AssetId = Vera.Id, MediaId = Vera.Images[0].Id, Name = "Vera" }];
            if (voice) shared.Voices = [new() { AssetId = Vera.Id, VoiceId = Voice.Id, Speaker = "VERA" }];
            Production.Compositions.Add(new() { ShotId = shot.Id, Version = 1, Shot = shared, Prompt = "Vera stands by the open window." });
            return shot;
        }
        public ProductionComposition Setup(Shot shot) => Production.Compositions.Single(c => c.ShotId == shot.Id);
        public ShotTake Take(Shot shot, bool select = false, int frames = 39)
        {
            var setup = Setup(shot); var rendered = setup.Shot.Copy(); rendered.Id = shot.Id;
            var take = new ShotTake { ShotId = shot.Id, Candidate = Shots.Takes.Count(t => t.ShotId == shot.Id) + 1, Width = 832, Height = 480, CreatedUtc = Epoch.AddMinutes(_minutes++),
                Snapshot = new(Assets.ProjectId, 1, rendered, setup.Prompt, "fingerprint", "http://localhost:8188", new(), 832, 480, frames) {
                    Production = new(setup.Id, 1, setup.Name, new(Guid.NewGuid(), Epoch, setup.Prompt, "", "", ""), []),
                    ReferenceGuidance = ShotReferences.Resolve(rendered, Assets, Shots), Appearances = ShotLooks.Capture(rendered, Assets) } };
            Shots.Takes.Add(take);
            if (select) shot.SelectedTakeId = take.Id;
            return take;
        }
        public ProjectReviewSnapshot Snapshot() => ProjectReview.Compose(Shots, Production, Cut, Assets);
        public IReadOnlyList<ReviewFinding> Run() => ReviewChecks.Run(Snapshot());
        private static AssetImage Image() => new() { Id = Guid.NewGuid(), FileName = "image.png", ContentType = "image/png", Width = 640, Height = 480 };
    }
}
