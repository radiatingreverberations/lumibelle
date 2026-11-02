using System.Globalization;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class CutExportPlanTests
{
    private static readonly VideoFileInfo Audio = new(64, 64, 120, 24, true);
    private static readonly VideoFileInfo Silent = Audio with { HasAudio = false };
    private static string Value(CutExportPlan plan, string name) => plan.Arguments[plan.Arguments.ToList().IndexOf(name) + 1];

    [Fact]
    public void MixedAudioPreservesExistingTracksAndFillsOnlySilentSegments()
    {
        var plan = CutExportPlan.Create([new("audio.mp4", 0, 24, 24), new("silent.mp4", 0, 24, 24)],
            new Dictionary<string, VideoFileInfo> { ["audio.mp4"] = Audio, ["silent.mp4"] = Silent }, "out.mp4");
        var filter = Value(plan, "-filter_complex");
        Assert.True(plan.HasAudio); Assert.DoesNotContain("-an", plan.Arguments);
        Assert.Contains("[0:a:0]", filter); Assert.DoesNotContain("[1:a:0]", filter);
        Assert.Contains("anullsrc=r=48000:cl=stereo", filter);
        Assert.Contains("apad,atrim=duration=1", filter);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1", filter);
        Assert.Equal(48, plan.Frames); Assert.Equal(2, plan.Duration);
    }

    [Fact]
    public void AllSilentCutsHaveExplicitTimingAndNoAudioGraph()
    {
        var plan = CutExportPlan.Create([new("a", 0, 24, 24), new("a", 24, 48, 24)],
            new Dictionary<string, VideoFileInfo> { ["a"] = Silent }, "out");
        Assert.False(plan.HasAudio); Assert.Contains("-an", plan.Arguments);
        Assert.Equal("24", Value(plan, "-r")); Assert.Equal("2", Value(plan, "-t"));
        Assert.Equal("cfr", Value(plan, "-fps_mode"));
        Assert.DoesNotContain("anullsrc", Value(plan, "-filter_complex"));
        Assert.Equal(2, plan.Arguments.Count(a => a == "-i"));
    }

    [Fact]
    public void TrimsUseExclusiveFrameBoundariesAndKeepAOneFrameClip()
    {
        var plan = CutExportPlan.Create([new("a", 47, 48, 24)],
            new Dictionary<string, VideoFileInfo> { ["a"] = Silent }, "out");
        Assert.Equal(1, plan.Frames);
        Assert.Contains("trim=start_frame=47:end_frame=48", Value(plan, "-filter_complex"));
        Assert.Contains("trim=end_frame=1", Value(plan, "-filter_complex"));
    }

    [Fact]
    public void MixedRatesAndDimensionsUseOneDefinedOutputGrid()
    {
        var plan = CutExportPlan.Create([new("a", 0, 24, 24), new("b", 0, 30, 30)],
            new Dictionary<string, VideoFileInfo> { ["a"] = Silent, ["b"] = new(128, 72, 60, 30, false) }, "out");
        Assert.Equal(48, plan.Frames); Assert.Equal(24, plan.Fps);
        Assert.Contains("scale=64:64", Value(plan, "-filter_complex"));
    }

    [Theory]
    [InlineData(-1, 24, 24)] [InlineData(24, 24, 24)] [InlineData(0, 121, 24)]
    [InlineData(0, 24, 0)] [InlineData(0, 24, double.NaN)] [InlineData(0, 24, 30)]
    public void InvalidOrStaleMetadataIsRejected(int start, int end, double fps)
    {
        Assert.Throws<WorkspaceStoreException>(() => CutExportPlan.Create([new("a", start, end, fps)],
            new Dictionary<string, VideoFileInfo> { ["a"] = Silent }, "out"));
    }

    [Fact]
    public void EmptyCutsAreRejectedBeforeAnEncoderCanStart() =>
        Assert.Throws<WorkspaceStoreException>(() => CutExportPlan.Create([], new Dictionary<string, VideoFileInfo>(), "out"));

    [Fact]
    public void ArgumentsAreCultureInvariantAndPathsStaySeparateFromFilterSyntax()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            const string source = "C:/my clips/quoted ' take.mp4";
            var plan = CutExportPlan.Create([new(source, 12, 36, 24)],
                new Dictionary<string, VideoFileInfo> { [source] = Audio }, "output movie.mp4");
            Assert.Contains(source, plan.Arguments);
            Assert.Equal("output movie.mp4", plan.Arguments[^1]);
            Assert.Contains("atrim=start=0.5:end=1.5", Value(plan, "-filter_complex"));
            Assert.DoesNotContain(source, Value(plan, "-filter_complex"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void LongCutsMoveTheFilterGraphOutOfTheWindowsCommandLine()
    {
        var source = "C:/cut/source.mp4";
        var segments = Enumerable.Range(0, 64)
            .Select(i => new CutExportSegment(source, 0, 124, 24))
            .ToArray();
        var plan = CutExportPlan.Create(segments, new Dictionary<string, VideoFileInfo> { [source] = new(1344, 768, 124, 24, true) }, "C:/cut/output.mp4");
        Assert.Contains("{filter-script}", plan.Arguments[plan.Arguments.ToList().IndexOf(CutExportPlan.FilterScriptOption) + 1]);
        Assert.DoesNotContain("-filter_complex", plan.Arguments);
        Assert.True(plan.Arguments.Sum(a => a.Length) + plan.Arguments.Count + 1 <= 32767);
        Assert.Contains("[63:v:0]", plan.FilterGraph);
    }

    [Fact]
    public void ShortCutsKeepTheFilterGraphInline()
    {
        var plan = CutExportPlan.Create([new("audio.mp4", 0, 24, 24)],
            new Dictionary<string, VideoFileInfo> { ["audio.mp4"] = Audio }, "out.mp4");
        Assert.DoesNotContain(CutExportPlan.FilterScriptOption, plan.Arguments);
        Assert.Contains("-filter_complex", plan.Arguments);
    }
}
