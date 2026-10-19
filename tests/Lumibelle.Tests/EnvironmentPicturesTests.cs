using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public class EnvironmentPicturesTests
{
    [Fact]
    public void ExplicitNoneAndOverridesSurviveCoverageRefreshAndReferenceCopy()
    {
        var (library, owner) = Fixture();
        var setup = new DraftingEnvironmentSetup { AssetId = owner.Id };
        var scene = new SceneReferenceSetup { Environment = setup };
        var source = new Shot { EnvironmentAssetId = owner.Id, EnvironmentInspection = setup };
        var target = new Shot { EnvironmentInspectionDisabled = true };
        ProductionPolicy.CopyCoverage(source, target);
        Assert.Null(DraftingEnvironments.Effective(target, scene));
        var restored = new Shot();
        ShotCopy.Of(SetupInputs.From(target)).Apply(restored);
        Assert.Null(DraftingEnvironments.Effective(restored, scene));
        Assert.Null(DraftingEnvironments.Effective(ReferenceCopies.Into(restored, source, library).Inputs, scene));
        target.EnvironmentInspectionDisabled = null;
        target.EnvironmentAssetId = Guid.NewGuid();
        ProductionPolicy.CopyCoverage(source, target);
        var effective = DraftingEnvironments.Effective(target, scene)!;
        Assert.Equal(target.EnvironmentAssetId, effective.AssetId);
        Assert.Empty(effective.Views);
        Assert.Equal(Guid.Empty, effective.Version);
    }

    private static (AssetLibrary Library, ReferenceAsset Owner) Fixture()
    {
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Category = AssetCategory.Environment, Name = "Plaza", Images = [
            new() { FileName = "a.png", ContentType = "image/png", Width = 32, Height = 16, Id = Guid.NewGuid(), Name = "Alternative", PreservationGuidance = "Different stone" },
            new() { FileName = "b.png", ContentType = "image/png", Width = 32, Height = 16, Id = Guid.NewGuid(), Name = "Approved wide", IsReference = true, PreservationGuidance = "Preserve the podium" }] };
        AssetReferenceReel Reel(string name) {
            var media = new ReferenceVideoMedia(Guid.NewGuid(), new('A', 64), 100, 32, 16, 72, 24, 3, false);
            return new() { AssetId = owner.Id, Name = name, Media = media, Keyframes = new() { Frames = [
                new() { Name = name + " front", Notes = "Facing the gate", Crop = new() { Width = .7 }, Frame = new(media.Id, media.Sha256, 0, 0) },
                new() { Name = name + " side", Notes = "Stairs at right", Frame = new(media.Id, media.Sha256, 24, 1) }] } };
        }
        return (new() { ProjectId = Guid.NewGuid(), Assets = [owner], Reels = [Reel("Perimeter"), Reel("Podium")] }, owner);
    }

    [Fact]
    public void MultipleReelsAndStillsAreManualPicturesWithoutVideoSlotsOrSourceMutation()
    {
        var (library, owner) = Fixture();
        var candidates = EnvironmentPictures.Candidates(library, owner.Id);
        Assert.Equal(6, candidates.Count);
        Assert.Equal("Approved wide", candidates[0].Name);
        var original = library.Copy(); var shot = new Shot();
        foreach (var candidate in candidates) EnvironmentPictures.Select(shot, candidate, true);
        Assert.Equal(6, ResolvedReferences.For(shot).Pictures.Count);
        Assert.Equal(0, ReferenceVideos.ReelSlotCount(shot));
        Assert.Empty(ResolvedReferences.For(shot).Audio);
        Assert.Equal(2, shot.Videos.Count);
        Assert.Equal("Facing the gate", shot.Videos[0].Keyframes!.Frames[0].Notes);
        Assert.Equal(.7, shot.Videos[0].Keyframes!.Frames[0].Crop!.Width);
        Assert.Equal(1, shot.Videos[0].Keyframes!.Frames[1].Frame.Seconds);
        Assert.Equal(ShotDirections.Hash(original), ShotDirections.Hash(library));
        EnvironmentPictures.Select(shot, candidates[0], true);
        Assert.Equal(6, ResolvedReferences.For(shot).Pictures.Count);
        foreach (var candidate in candidates.Where(c => c.Reel?.Id == library.Reels[0].Id)) EnvironmentPictures.Select(shot, candidate, false);
        Assert.Single(shot.Videos);
    }

    [Fact]
    public void NinePictureCeilingIncludesRetainedReferencesAndRejectsPictureTen()
    {
        var (library, owner) = Fixture(); var shot = new Shot { Images = Enumerable.Range(0, 8).Select(_ => new ShotImageBinding { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid() }).ToList() };
        var views = EnvironmentPictures.Candidates(library, owner.Id);
        EnvironmentPictures.Select(shot, views[0], true);
        var before = ShotDirections.Hash(shot);
        Assert.Throws<WorkspaceStoreException>(() => EnvironmentPictures.Select(shot, views[2], true));
        Assert.Equal(before, ShotDirections.Hash(shot));
    }

    [Fact]
    public void SelectingAFrameNeverConvertsExistingVideoOrRefMod()
    {
        var (library, owner) = Fixture(); var view = EnvironmentPictures.Candidates(library, owner.Id).First(c => c.Reel is not null);
        foreach (var mode in new[] { ReelVisuals.FullReel, ReelVisuals.RefMod }) {
            var shot = new Shot { Videos = [new() { Media = view.Reel!.Media, Visuals = mode }] };
            var before = ShotDirections.Hash(shot);
            Assert.Throws<WorkspaceStoreException>(() => EnvironmentPictures.Select(shot, view, true));
            Assert.Equal(before, ShotDirections.Hash(shot));
        }
    }
}
