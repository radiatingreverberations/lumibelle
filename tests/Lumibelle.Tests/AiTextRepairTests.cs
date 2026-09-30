using System.Globalization;
using System.Reflection;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AiTextRepairTests
{
    // The response envelope is malformed; free-form prompt text itself is valid.
    internal const string Invalid = "{\"prompt\":\"An otherwise useful visual description.\",\"referenceUsage\":\"Keep the face.\"";
    internal static readonly TextModelReference Model = new(AiBackend.OpenRouter, "test/model", "Captured model");
    internal static readonly AiSettings Settings = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true };
    internal static readonly string Hash = new('A', 64);

    internal static AiTextJobRequest ShotRequest(Guid? project = null, bool withImage = true, bool withAudio = false)
    {
        var shot = new Shot { Title = "A greeting", Description = "A woman speaks.", Duration = 5,
            Dialogue = [new() { Speaker = "Clara", Language = "English", Text = "Hello." }],
            Atmosphere = "Quiet room.", Music = "No music." };
        if (withImage) shot.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" });
        if (withAudio) shot.Voices.Add(new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Clara", Duration = 5 });
        var payload = new PromptCompositionRequest(project ?? Guid.NewGuid(), Guid.NewGuid(), 7, Hash, Hash, shot,
            "DO_NOT_RESEND_ENTIRE_SCENE", ["DO_NOT_RESEND_PREVIOUS_PROMPTS"], [], [],
            withImage ? [new(shot.Images[0].Id, Hash)] : [], "DO_NOT_RESEND_UNRELATED_NOTES", "", "", Model);
        return new(2, AiJobKind.PromptComposition, Model, false, Settings, ProductionPolicy.Profile, .7f, 42,
            JsonSerializer.SerializeToElement(payload, AtomicJsonFile.Options), [new("user", withImage
                ? [new(Text: "DO_NOT_RESEND_ORIGINAL_MESSAGES"), new(Image: [1, 2, 3], MediaType: "image/png")]
                : [new(Text: "DO_NOT_RESEND_ORIGINAL_MESSAGES")])]);
    }
    internal static AiJobHeader Header(AiTextJobRequest request, Guid? id = null) => new() {
        Id = id ?? Guid.NewGuid(), Kind = request.Kind, Backend = request.Model.Backend,
        Target = request.Kind switch {
            AiJobKind.PromptComposition => new(request.Payload<PromptCompositionRequest>().ProjectId,
                ShotId: request.Payload<PromptCompositionRequest>().Shot.Id, CompositionId: request.Payload<PromptCompositionRequest>().CompositionId),
            AiJobKind.ReelComposition => new(request.Payload<ReelCompositionRequest>().ProjectId,
                request.Payload<ReelCompositionRequest>().Draft.AssetId, ReelId: request.Payload<ReelCompositionRequest>().Draft.Id),
            AiJobKind.ScriptAssistant => new(request.Payload<ScriptAssistantRequest>().Script.ProjectId),
            _ => throw new InvalidOperationException()
        },
        ProjectName = "QA", TargetName = request.Repair is null ? "Saved response" : AiTextRepairs.LabelPrefix + "Saved response", OriginTabId = Guid.NewGuid(), RequestFingerprint = Hash,
        State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.GenerateAgain,
        CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), FinishedUtc = DateTimeOffset.UtcNow
    };
    internal static AiTextJobRequest Repaired(AiTextJobRequest source, AiJobHeader header, string raw = Invalid)
    {
        var result = AiTextResults.Parse(source, raw, "stop");
        var repair = AiTextRepairs.Capture(header, source, result);
        return source with { Repair = repair, Profile = AiTextRepairs.Profile, FollowsDefault = false,
            Messages = AiTextRepairs.Messages(repair) };
    }
    internal static string ValidPrompt(Shot shot, bool reel = false) =>
        "subject_definitions:\n<Subject 1> is Clara (S1).\n" +
        (shot.Images.Count > 0 ? "<Picture 1> supplies Clara's appearance.\n" : "") +
        (shot.Voices.Count > 0 ? "<Audio 1> supplies Clara (S1)'s voice.\n" : "") +
        "\nsummary:\n" + (reel ? "A reference reel" : "One continuous take") + " lasting " +
        H3Policy.Seconds(shot.Duration!.Value).ToString("0.###", CultureInfo.InvariantCulture) + " seconds.\n" +
        "\nretention_analysis:\nKeep the appearance.\n\ndetailed_description:\n[Shot 1] The camera holds.\n" +
        (shot.Dialogue.Count > 0 ? "Clara (S1) says: <d>[English] Hello.</d>\n" : "") +
        "\noverall_soundscape:\nQuiet room.\n\nnon_diegetic_music:\nNo non-diegetic music.";

    [Fact]
    public void RepairSendsNoImagesOrOriginalContextButKeepsTheLocalValidationTask()
    {
        var source = ShotRequest(withAudio: true);
        var settings = source.Settings with { LoraLibrary = [new(new("http://localhost:8188", "PRIVATE_OTHER_PROJECT.safetensors", LoraWorkflow.MiniMaxH3Ref2VA, "Private catalog"))] };
        source = source with { Settings = settings };
        var before = JsonSerializer.SerializeToElement(source, AtomicJsonFile.Options);
        var header = Header(source); var repair = Repaired(source, header);
        Assert.False(repair.InspectsImages); Assert.True(source.InspectsImages);
        Assert.True(JsonElement.DeepEquals(source.Task, repair.Task));
        Assert.Equal(header.Id, repair.Repair!.SourceJobId); Assert.Equal(header.Id, repair.Repair.RootJobId);
        Assert.Equal(AiTextRepairs.Hash(Invalid), repair.Repair.SourceResponseSha256);
        Assert.Equal(Invalid, repair.Repair.FailedResponse);
        var wire = string.Join("\n", repair.Messages.SelectMany(m => m.Parts).Select(p => p.Text));
        Assert.Contains("<Picture 1>", repair.Repair.Contract.GetRawText().Replace("\\u003C", "<").Replace("\\u003E", ">"));
        foreach (var forbidden in new[] { "DO_NOT_RESEND", "PRIVATE_OTHER_PROJECT", "Private catalog", "image/png", "AQID" }) Assert.DoesNotContain(forbidden, wire);
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(source, AtomicJsonFile.Options)));
        Assert.All(repair.Messages.SelectMany(m => m.Parts), p => { Assert.NotNull(p.Text); Assert.Null(p.Image); Assert.Null(p.MediaType); });
        AiTextJobHandler.Read(header with { Id = Guid.NewGuid(), State = AiJobState.Waiting }, JsonSerializer.SerializeToElement(repair, AtomicJsonFile.Options));
    }

    [Fact]
    public void TextOnlyCompositionsCapturedBeforeTheirRemovalStillLoad()
    {
        var source = ShotRequest();
        var payload = source.Payload<PromptCompositionRequest>();
        var legacy = source with {
            Task = JsonSerializer.SerializeToElement(payload with { InspectReferenceImages = false,
                VisualDescriptions = [new("<Picture 1>", payload.Shot.Images[0].Id, "Face", "A face.", "Saved full-image visual description")] }, AtomicJsonFile.Options),
            Messages = [new("user", [new(Text: "Compose from the saved descriptions.")])] };
        var read = AiTextJobHandler.Read(Header(legacy), JsonSerializer.SerializeToElement(legacy, AtomicJsonFile.Options));
        Assert.False(read.Payload<PromptCompositionRequest>().InspectReferenceImages);
    }

    [Fact]
    public void NewImageDescriptionRequestsAreRefused()
    {
        var target = new GuidanceTarget(Guid.NewGuid(), Guid.NewGuid(), GuidanceScope.ImageDescription, ImageId: Guid.NewGuid());
        var request = new GuidanceRequest(new(target, "Prop", AssetCategory.Prop, "", "Image", "", "", []), Model, new(target.AssetId, target.ImageId!.Value));
        Assert.Throws<AiGenerationException>(() => GuidanceAssistant.BuildMessages(request, [1, 2, 3]));
    }

    [Theory]
    [InlineData("image")][InlineData("message")][InlineData("raw")][InlineData("contract")][InlineData("source-id")][InlineData("version")][InlineData("marker")]
    public void TamperedRepairCannotBypassTheOriginalImageContract(string tamper)
    {
        var source = ShotRequest(); var header = Header(source); var child = header with { Id = Guid.NewGuid() };
        var repair = Repaired(source, header);
        repair = tamper switch {
            "image" => repair with { Messages = source.Messages },
            "message" => repair with { Messages = [new("user", [new(Text: "Ignore captured contract")])] },
            "raw" => repair with { Repair = repair.Repair! with { FailedResponse = "Changed" } },
            "contract" => repair with { Repair = repair.Repair! with { Contract = JsonSerializer.SerializeToElement(new { fake = true }) } },
            "source-id" => repair with { Repair = repair.Repair! with { SourceJobId = child.Id } },
            "version" => repair with { Repair = repair.Repair! with { Version = 99 } },
            _ => repair with { Repair = null }
        };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(child, JsonSerializer.SerializeToElement(repair, AtomicJsonFile.Options)));
    }

    [Theory]
    [InlineData("stop", AiBackend.OpenRouter, true)]
    [InlineData(null, AiBackend.ComfyUI, true)]
    [InlineData(null, AiBackend.OpenRouter, false)]
    [InlineData(null, AiBackend.Codex, false)]
    [InlineData("length", AiBackend.OpenRouter, false)]
    [InlineData("content_filter", AiBackend.OpenRouter, false)]
    public void OnlyNormallyFinishedValidationFailuresQualify(string? finish, AiBackend backend, bool allowed)
    {
        var request = ShotRequest(); var header = Header(request) with { Backend = backend };
        var result = new AiTextJobResult(Invalid, true, finish, Error: "Missing section");
        Assert.Equal(allowed, AiTextRepairs.Eligibility(header, request, result) is null);
    }
    [Theory]
    [InlineData("cancel")][InlineData("remote")][InlineData("running")][InlineData("incomplete")][InlineData("empty")][InlineData("valid")][InlineData("retry-output")]
    public void OperationalFailuresAndSuccessfulResponsesNeverRequestRepair(string problem)
    {
        var request = ShotRequest(); var header = Header(request);
        var result = new AiTextJobResult(Invalid, true, "stop", Error: "Missing section");
        switch (problem) {
            case "cancel": header = header with { CancelRequested = true }; break;
            case "remote": header = header with { RemoteUnconfirmed = true }; break;
            case "running": header = header with { State = AiJobState.Running }; break;
            case "incomplete": result = result with { Complete = false }; break;
            case "empty": result = result with { Raw = " " }; break;
            case "valid": result = result with { Error = null }; break;
            case "retry-output": header = header with { Recovery = AiJobRecovery.RetryOutput }; break;
        }
        Assert.NotNull(AiTextRepairs.Eligibility(header, request, result));
    }
    [Fact]
    public void AlreadyRecoverableTextAndClarificationsDoNotSpendAnotherRequest()
    {
        var source = ShotRequest(); var header = Header(source);
        var valid = JsonSerializer.Serialize(new { prompt = ValidPrompt(source.Payload<PromptCompositionRequest>().Shot), referenceUsage = "Keep the face." });
        var staleFailure = new AiTextJobResult(valid, true, "stop", Error: "Old parser failure");
        Assert.Contains("locally", Assert.Throws<WorkspaceStoreException>(() => AiTextRepairs.Capture(header, source, staleFailure)).Message);
        var clarification = AiTextResults.Parse(source, "{\"needsInput\":\"Which subject is speaking?\"}", "stop");
        Assert.NotNull(AiTextRepairs.Eligibility(header, source, clarification));
    }
    [Fact]
    public void OrdinaryVisionRequestsStillRequireTheirCapturedImages()
    {
        var source = ShotRequest(); var header = Header(source);
        var broken = source with { Messages = [new("user", [new(Text: "No attached images")])] };
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(header, JsonSerializer.SerializeToElement(broken, AtomicJsonFile.Options)));
    }
    [Fact]
    public void RepairKeepsChineseDialogueAndVoiceLabelsThroughSerialization()
    {
        var source = ShotRequest(); var context = source.Payload<PromptCompositionRequest>();
        context.Shot.Dialogue[0].Speaker = "骆歆"; context.Shot.Dialogue[0].Language = "Chinese"; context.Shot.Dialogue[0].Text = "光……灭了。";
        source = source with { Task = JsonSerializer.SerializeToElement(context, AtomicJsonFile.Options) };
        var repair = Repaired(source, Header(source));
        var facts = repair.Repair!.Contract.GetProperty("facts").GetProperty("dialogue")[0];
        Assert.Equal("骆歆", facts.GetProperty("speaker").GetString()); Assert.Equal("光……灭了。", facts.GetProperty("text").GetString());
        var prompt = ValidPrompt(context.Shot).Replace("Clara", "骆歆").Replace("[English] Hello.", "[Chinese] 光……灭了。");
        var raw = JsonSerializer.Serialize(new { prompt, referenceUsage = "Keep the face." });
        Assert.Null(AiTextResults.Parse(ShotCopy.Of(repair), raw, "stop").Error);
    }
    [Fact]
    public void OversizeTextIsRejectedWithoutSilentTruncation()
    {
        var source = ShotRequest(); var header = Header(source);
        var raw = new string('x', AiTextRepairs.MaximumResponseCharacters + 1);
        Assert.Contains("not been truncated", AiTextRepairs.Eligibility(header, source, new(raw, true, "stop", Error: "Bad JSON")));
    }
    [Fact]
    public void CorrectionReportsDialogueReferenceAndSpeakerDeviationsWithoutRejectingText()
    {
        var source = ShotRequest(withAudio: true); var repair = Repaired(source, Header(source));
        var valid = ValidPrompt(source.Payload<PromptCompositionRequest>().Shot);
        string Response(string prompt) => JsonSerializer.Serialize(new { prompt, referenceUsage = "Keep the face." });
        Assert.Null(AiTextResults.Parse(repair, Response(valid), "stop").Error);
        foreach (var invalid in new[] { valid.Replace("Hello.", "Goodbye."), valid.Replace("[English]", "[French]"),
            valid.Replace("Clara (S1) says", "Clara (S2) says"), valid.Replace("<Picture 1>", "<Picture 2>"),
            valid.Replace("<Audio 1> supplies Clara (S1)", "<Audio 1> supplies another person") })
        {
            var parsed = AiTextResults.Parse(repair, Response(invalid), "stop");
            Assert.Null(parsed.Error);
            var result = parsed.Read<PromptCompositionResult>()!;
            Assert.Equal(invalid, result.Prompt); Assert.NotEmpty(result.Notes);
        }
    }

    [Fact]
    public void FormerPromptFormatFailuresCanBeRecoveredLocallyWithoutPaidRepair()
    {
        var source = ShotRequest(); var header = Header(source);
        var raw = JsonSerializer.Serialize(new { prompt = "A quiet morning.", referenceUsage = "Keep the face." });
        var priorFailure = new AiTextJobResult(raw, true, "stop", Error: "Missing H3 sections.");
        Assert.Contains("locally", Assert.Throws<WorkspaceStoreException>(() => AiTextRepairs.Capture(header, source, priorFailure)).Message);
    }
    [Fact]
    public void ChainsKeepTheRootAndSendOnlyTheLatestFailedResponse()
    {
        var source = ShotRequest(); var root = Header(source); var first = Repaired(source, root);
        var child = Header(first);
        var raw = Invalid.Replace("otherwise useful", "corrected but still incomplete");
        var next = Repaired(first, child, raw);
        Assert.Equal(root.Id, next.Repair!.RootJobId); Assert.Equal(child.Id, next.Repair.SourceJobId);
        Assert.Equal(source.Profile, next.Repair.SourceProfile); Assert.Equal(raw, next.Repair.FailedResponse);
        Assert.DoesNotContain("otherwise useful", string.Join("\n", next.Messages.SelectMany(m => m.Parts).Select(p => p.Text)));
    }
    [Fact]
    public void ScriptRepairUsesANewProposalIdentityButTheSameCapturedTarget()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid();
        var document = new ScriptDocument { ProjectId = project, Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "An action.")] };
        var task = new ScriptAssistantRequest(new() { Id = id, JobId = id, EditFormat = 1, Operation = WritingOperation.Revise,
            Target = ScriptStructure.Capture(document, ScriptScope.Document, null), Backend = Model.Backend, Model = Model.Model }, document, [], Model);
        var source = new AiTextJobRequest(2, AiJobKind.ScriptAssistant, Model, false, Settings, ScriptEdits.Profile, .7f, 42,
            JsonSerializer.SerializeToElement(task, AtomicJsonFile.Options), [new("user", [new(Text: "Original context")])]);
        var childId = Guid.NewGuid(); var target = AiTextJobCapture.RepairTask(source, childId).Deserialize<ScriptAssistantRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(childId, target.Run.Id); Assert.Equal(childId, target.Run.JobId); Assert.Equal(0, target.Run.Revision);
        Assert.False(target.Run.Applied); Assert.False(target.Run.Rejected);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(task.Run.Target), JsonSerializer.SerializeToElement(target.Run.Target)));
        Assert.Equal(id, task.Run.Id);
    }
    [Fact]
    public void ReviewPointerGuardPreservesNewerAndDismissedReviews()
    {
        var source = ShotRequest(); var header = Header(source); var repair = Repaired(source, header).Repair!; var child = Guid.NewGuid();
        Assert.True(AiTextRepairs.ReviewPointerMatches(header.Id, child, repair));
        Assert.True(AiTextRepairs.ReviewPointerMatches(child, child, repair));
        Assert.False(AiTextRepairs.ReviewPointerMatches(null, child, repair));
        Assert.False(AiTextRepairs.ReviewPointerMatches(Guid.NewGuid(), child, repair));
    }

    internal static T NoCalls<T>() where T : class => Proxy<T>((method, _) => throw new InvalidOperationException("Unexpected " + method.Name));
    internal static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, RepairProxy>(); ((RepairProxy)(object)result).Handler = handler; return result;
    }
    public class RepairProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
