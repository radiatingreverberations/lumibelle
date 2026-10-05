using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class CutTakeSelectionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiltersBeforeSelectingLatestPerShotRegardlessOfListOrder()
    {
        var first = new Shot(); var second = new Shot();
        var oldest = Take(first, 1344, 768, 1);
        var latestMatching = Take(first, 1344, 768, 2);
        var newestPreview = Take(first, 832, 480, 3);
        var other = Take(second, 768, 1344, 1);
        var document = new ShotDocument { Shots = [first, second], Takes = [newestPreview, other, latestMatching, oldest] };

        var selected = CutTakeSelection.LatestByShot(document, "1.0");

        Assert.Equal(2, selected.Count);
        Assert.Equal(latestMatching.Id, selected[first.Id]);
        Assert.Equal(other.Id, selected[second.Id]);
        Assert.Equal(newestPreview.Id, CutTakeSelection.LatestByShot(document)[first.Id]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnyResolutionSelectsLatestIncludingLegacyUnknownDimensions(string? resolution)
    {
        var shot = new Shot(); var known = Take(shot, 1344, 768, 1); var legacy = Take(shot, 0, 0, 2);
        var document = new ShotDocument { Shots = [shot], Takes = [legacy, known] };

        Assert.Equal(legacy.Id, Assert.Single(CutTakeSelection.LatestByShot(document, resolution)).Value);
        Assert.Equal(known.Id, Assert.Single(CutTakeSelection.LatestByShot(document, "1.0")).Value);
    }

    [Fact]
    public void OmitsUnmatchedEmptyDeletedAndTrashedShotsWithoutFallback()
    {
        var matching = new Shot(); var previewOnly = new Shot(); var empty = new Shot(); var deleted = new Shot();
        var available = Take(matching, 1344, 768, 1);
        var document = new ShotDocument
        {
            Shots = [matching, previewOnly, empty],
            Takes = [Take(previewOnly, 832, 480, 4), Take(deleted, 1344, 768, 5), available],
            Trash = [new() { Take = Take(matching, 1344, 768, 6) }, new() { Take = Take(empty, 1344, 768, 7) }]
        };

        var selected = Assert.Single(CutTakeSelection.LatestByShot(document, "1.0"));

        Assert.Equal(matching.Id, selected.Key);
        Assert.Equal(available.Id, selected.Value);
        Assert.Empty(CutTakeSelection.LatestByShot(document, "2.0"));
    }

    [Fact]
    public void UsesSavedOutputRatherThanTheSnapshotPresetOrInputDimensions()
    {
        var shot = new Shot { Resolution = VideoResolution.Quick };
        var take = Take(shot, 1344, 768, 1);
        take.Snapshot = take.Snapshot with { Width = 608, Height = 352 };
        var document = new ShotDocument { Shots = [shot], Takes = [take] };

        Assert.Equal("1.0", CutTakeSelection.ResolutionKey(take));
        Assert.Equal(take.Id, Assert.Single(CutTakeSelection.LatestByShot(document, "1.0")).Value);
        Assert.Empty(CutTakeSelection.LatestByShot(document, "0.2"));
    }

    [Theory]
    [InlineData(1344, 768, "1.0")]
    [InlineData(768, 1344, "1.0")]
    [InlineData(1024, 1024, "1.0")]
    [InlineData(608, 352, "0.2")]
    [InlineData(832, 480, "0.4")]
    [InlineData(1120, 640, "0.7")]
    [InlineData(3840, 2160, "8.3")]
    public void ResolutionMatchesTheDisplayedMegapixels(int width, int height, string expected)
    {
        var take = Take(new Shot(), width, height);

        Assert.Equal(expected, CutTakeSelection.ResolutionKey(take));
        Assert.EndsWith(expected + " MP", TakeDisplay.Badge(take));
        Assert.Equal(expected + " MP", TakeDisplay.Megapixels(take));
        // Quick and Preview are draft sizes, to be regenerated at Detail or Native.
        Assert.Equal(expected is "0.2" or "0.4", TakeDisplay.IsDraft(take));
    }

    [Theory]
    [InlineData(0, 768)]
    [InlineData(1344, 0)]
    [InlineData(-1, 768)]
    [InlineData(-1344, -768)]
    public void InvalidDimensionsCannotMatchAResolution(int width, int height)
    {
        var shot = new Shot(); var take = Take(shot, width, height);
        var document = new ShotDocument { Shots = [shot], Takes = [take] };

        Assert.Null(CutTakeSelection.ResolutionKey(take));
        Assert.Empty(CutTakeSelection.ResolutionChoices(document));
        Assert.Empty(CutTakeSelection.LatestByShot(document, "1.0"));
    }

    [Fact]
    public void ResolutionChoicesAreDistinctNumericallySortedAndLimitedToActiveShots()
    {
        var shot = new Shot(); var deleted = new Shot();
        var document = new ShotDocument
        {
            Shots = [shot],
            Takes = [Take(shot, 4000, 3000), Take(shot, 1344, 768), Take(shot, 832, 480),
                Take(shot, 768, 1344), Take(shot, 1920, 1080), Take(shot, 0, 0), Take(deleted, 10000, 10000)],
            Trash = [new() { Take = Take(shot, 5000, 5000) }]
        };

        Assert.Equal(["0.4", "1.0", "2.1", "12.0"], CutTakeSelection.ResolutionChoices(document));
    }

    [Fact]
    public void EqualTimestampsUseStableTakeIdTieBreakNotEnumerationOrder()
    {
        var shot = new Shot();
        var lower = Take(shot, 1344, 768) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var higher = Take(shot, 1344, 768) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var document = new ShotDocument { Shots = [shot], Takes = [lower, higher] };

        Assert.Equal(higher.Id, Assert.Single(CutTakeSelection.LatestByShot(document)).Value);
        document.Takes.Reverse();
        Assert.Equal(higher.Id, Assert.Single(CutTakeSelection.LatestByShot(document)).Value);
    }

    [Fact]
    public void ResolutionKeysAreInvariantAndMultiplicationDoesNotOverflowInt32()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var shot = new Shot();
            var document = new ShotDocument { Shots = [shot], Takes = [Take(shot, 50000, 50000), Take(shot, 1344, 768)] };

            Assert.Equal(["1.0", "2500.0"], CutTakeSelection.ResolutionChoices(document));
            Assert.Single(CutTakeSelection.LatestByShot(document, "1.0"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void SelectionDoesNotMutateProductionSelectionsOrSavedTakes()
    {
        var shot = new Shot(); var old = Take(shot, 832, 480, 1); var latest = Take(shot, 1344, 768, 2);
        shot.SelectedTakeId = old.Id;
        var document = new ShotDocument { Shots = [shot], Takes = [old, latest] };
        var before = JsonSerializer.Serialize(document);

        _ = CutTakeSelection.ResolutionChoices(document);
        Assert.Equal(latest.Id, Assert.Single(CutTakeSelection.LatestByShot(document, "1.0")).Value);

        Assert.Equal(before, JsonSerializer.Serialize(document));
    }

    [Fact]
    public void EmptyDocumentsHaveNoChoicesOrSelections()
    {
        Assert.Empty(CutTakeSelection.ResolutionChoices(new()));
        Assert.Empty(CutTakeSelection.LatestByShot(new()));
    }

    private static ShotTake Take(Shot shot, int width, int height, int minutes = 0) => new()
    {
        ShotId = shot.Id, Width = width, Height = height, CreatedUtc = Epoch.AddMinutes(minutes),
        Snapshot = new(Guid.Empty, 0, shot with { }, "", "", "http://localhost:8188", new(), width, height, 39)
    };
}
