using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ReelViewSuggestionTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static ReelViewSuggestionRequest Request(ReelKeyframeSet? current = null) {
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('a', 64), 10, 640, 360, 360, 24, 15, false);
        return new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), media, "Plaza", "Noxian plaza", "Stone podium and raised judge platform", "Reusable layout views",
            "Full perimeter with four holds", "Include entrance", "Choose opposite sides", 2, ReelViewSuggestions.Baseline(current ?? new()),
            [new(1, new() { Frame = new(media.Id, media.Sha256, 24, 1) }, ReelViewSuggestions.Hash(Png)),
             new(2, new() { Frame = new(media.Id, media.Sha256, 168, 7) }, ReelViewSuggestions.Hash(Png))], []);
    }
    private const string Reply = """{"summary":"Two complementary views","views":[{"candidate":2,"name":"Toward the gate","notes":"Podium foreground, gate beyond."},{"candidate":1,"name":"Toward judge platform","notes":"Raised platform beyond the podium."}],"warnings":["The second view is softer."]}""";
    [Fact] public void InspectsCandidatesInOrderAndTreatsCameraPlanAsIntent()
    {
        var r = Request(); var messages = ReelViewSuggestions.Messages(r, [Png, Png]);
        Assert.Equal(2, messages[1].Contents.OfType<DataContent>().Count());
        Assert.Contains("intent only", messages[0].Text); Assert.Contains("not invented compass directions", messages[0].Text);
        Assert.Contains("raised judge platform", messages[1].Text); Assert.Contains("Choose opposite sides", messages[1].Text);
        var result = ReelViewSuggestions.Parse(Reply, r);
        Assert.Equal([2, 1], result.Views.Select(v => v.Candidate)); Assert.Single(result.Warnings);
        var staged = ReelViewSuggestions.Stage(r, result.Views, new(), true);
        Assert.Equal(168, staged.Frames[0].Frame.Index); Assert.Equal("Toward the gate", staged.Frames[0].Name);
        Assert.Equal("Podium foreground, gate beyond.", staged.Frames[0].Notes);
    }
    [Theory]
    [InlineData("{\"summary\":\"x\",\"views\":[{\"candidate\":99,\"name\":\"Imaginary view\",\"notes\":\"x\"}],\"warnings\":[]}")]
    [InlineData("{\"summary\":\"x\",\"views\":[{\"candidate\":1,\"name\":\"A\",\"notes\":\"x\"},{\"candidate\":1,\"name\":\"B\",\"notes\":\"x\"}],\"warnings\":[]}")]
    [InlineData("{\"summary\":\"x\",\"views\":[{\"candidate\":1,\"name\":\"\",\"notes\":\"x\"}],\"warnings\":[]}")]
    [InlineData("{\"summary\":\"x\",\"views\":[]}")]
    public void RejectsIncompleteOrInventedSelections(string raw) => Assert.Throws<WorkspaceStoreException>(() => ReelViewSuggestions.Parse(raw, Request()));
    [Fact] public void AdditionsPreserveAuthoredCropsAndNotesAndStaleResultsCannotReplaceEdits()
    {
        var r = Request(); var authored = r.Candidates[0].Keyframe with { Name = "My view", Notes = "Keep this", Crop = new() { Width = .5 } };
        var current = new ReelKeyframeSet { Frames = [authored] }; r = r with { Baseline = ReelViewSuggestions.Baseline(current) };
        var result = ReelViewSuggestions.Parse(Reply, r);
        var added = ReelViewSuggestions.Stage(r, result.Views, current, false);
        Assert.Equal(2, added.Frames.Count); Assert.Equal("My view", added.Frames[0].Name); Assert.Equal(.5, added.Frames[0].Crop!.Width);
        Assert.Equal("Keep this", added.Frames[0].Notes); Assert.Single(current.Frames);
        authored.Notes = "Edited while waiting";
        Assert.Throws<WorkspaceStoreException>(() => ReelViewSuggestions.Stage(r, result.Views, current, true));
    }
    [Fact] public void SavedTextRequestsValidateTargetAndExactImageContentAndProduceTypedResults()
    {
        var r = Request(); var model = new TextModelReference(AiBackend.OpenRouter, "vision", "Vision");
        var request = new AiTextJobRequest(2, AiJobKind.ReelViewSuggestion, model, true,
            new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "vision", HasOpenRouterKey = true }, ReelViewSuggestions.Profile,
            .3f, 42, JsonSerializer.SerializeToElement(r, AtomicJsonFile.Options), ReelViewSuggestions.Messages(r, [Png, Png]).Select(AiTextMessage.Capture).ToArray());
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = request.Kind, Backend = model.Backend,
            Target = new(r.ProjectId, r.AssetId, ReelId: r.ReelId), ProjectName = "P", TargetName = "Plaza", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" };
        job.Target.Validate(job.Kind);
        Assert.Contains("view=reels", job.ReviewUrl); Assert.Contains("/views", job.Target.LockKey(job.Kind));
        Assert.Equal(request.Kind, AiTextJobHandler.Read(job, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)).Kind);
        var parsed = AiTextResults.Parse(request, Reply, ChatFinishReason.Stop.ToString());
        Assert.Null(parsed.Error); Assert.Equal(2, parsed.Read<ReelViewSuggestionResult>()!.Views.Count);
        var wrongImage = request with { Messages = [request.Messages[0], new("user", [new(Text: "context"), new(Image: [1], MediaType: "image/png"), new(Image: Png, MediaType: "image/png")])] };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(job, JsonSerializer.SerializeToElement(wrongImage, AtomicJsonFile.Options)));
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(job with { Target = job.Target with { ReelId = Guid.NewGuid() } }, JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options)));
    }
    [Fact] public void OlderKeyframesKeepTheirSerializedShape()
    {
        var frame = Request().Candidates[0].Keyframe;
        Assert.DoesNotContain("name", JsonSerializer.Serialize(frame, AtomicJsonFile.Options));
        Assert.Equal("Keyframe 1", ReelViewSuggestions.Label(frame, 1));
    }
    [Fact] public void CompleteFencedJsonIsAcceptedWithoutChangingTheSavedRawResponse()
    {
        var r = Request();
        var request = new AiTextJobRequest(2, AiJobKind.ReelViewSuggestion, new(AiBackend.OpenRouter, "vision", "Vision"), true,
            new(), ReelViewSuggestions.Profile, .3f, 42, JsonSerializer.SerializeToElement(r, AtomicJsonFile.Options), []);
        foreach (var raw in new[] { "```json\n" + Reply + "\n```", "I inspected the views.\n" + Reply }) {
            var result = AiTextResults.Parse(request, raw, ChatFinishReason.Stop.ToString());
            Assert.Null(result.Error); Assert.Equal(raw, result.Raw); Assert.Equal(2, result.Read<ReelViewSuggestionResult>()!.Views.Count);
        }
    }
}
