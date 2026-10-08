using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static LoraSelection VideoLora(string file = "h3/character.safetensors", float strength = .7f, string server = "http://localhost:8188") =>
        new(new(server, file, LoraWorkflow.MiniMaxH3Ref2VA, "H3 " + file), strength);
    private static VideoSnapshot WithLoras(VideoSnapshot snapshot, int count = 2)
    {
        var shot = snapshot.Shot.Copy();
        shot.Loras = Enumerable.Range(0, count).Select(i => VideoLora(i == 0 ? "h3/character.safetensors" : "h3/styles/film.safetensors", .7f + i, snapshot.ComfyUrl)).ToArray();
        return snapshot with { Shot = shot, Fingerprint = H3Policy.Fingerprint(shot), AppliedLoras = count == 0 ? null :
            shot.Loras.Select(l => new AppliedLora(l.Reference, l.Strength)).ToArray() };
    }
    [Theory]
    [InlineData(20, 0)] [InlineData(20, 1)] [InlineData(20, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    [InlineData(8, 0)] [InlineData(8, 1)] [InlineData(8, 2)]
    public void H3OptionalStacksPreserveConditioningSamplingAndPerformance(int steps, int count)
    {
        var shot = Ready(); shot.Turbo = steps != 20; shot.TurboSteps = steps == 8 ? 8 : 4;
        foreach (var backend in Enum.GetValues<H3AttentionBackend>())
        foreach (var sol in new[] { false, true })
        {
            var baseline = Snapshot(Guid.NewGuid(), shot) with { Performance = H3Performance.Capture(new() { Attention = backend, SolAttention = sol }) };
            var snapshot = WithLoras(baseline, count);
            JsonElement Graph(VideoSnapshot s) => JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(s, 42, "same", [])).GetProperty("prompt");
            var graph = Graph(snapshot); var original = Graph(baseline);
            JsonElement Input(string id, string field) => graph.GetProperty(id).GetProperty("inputs").GetProperty(field);
            var model = steps == 20 ? "1" : "16";
            for (var i = 1; i <= count; i++)
            {
                Assert.Equal(model, Input($"lora_{i}", "model")[0].GetString());
                Assert.Equal(snapshot.AppliedLoras![i - 1].Reference.FileName, Input($"lora_{i}", "lora_name").GetString());
                Assert.Equal(snapshot.AppliedLoras[i - 1].Strength, Input($"lora_{i}", "strength_model").GetSingle());
                model = $"lora_{i}";
            }
            if (steps == 8) { Assert.Equal(model, Input("18", "model")[0].GetString()); model = "18"; }
            if (backend != H3AttentionBackend.ServerDefault) { Assert.Equal(model, Input("30", "model")[0].GetString()); model = "30"; }
            if (sol) { Assert.Equal(model, Input("31", "model")[0].GetString()); model = "31"; }
            Assert.Equal(model, Input("6", "model")[0].GetString());
            foreach (var node in original.EnumerateObject().Where(n => n.Name is not ("6" or "18" or "30" or "31")))
                Assert.True(JsonElement.DeepEquals(node.Value, graph.GetProperty(node.Name)), "Changed existing node " + node.Name);
            if (steps != 20) Assert.Equal(1, Input("16", "strength_model").GetDouble());
            if (count == 0) Assert.True(JsonElement.DeepEquals(original, graph));
        }
    }
    [Theory]
    [InlineData(TakeRefinementMode.Refine)] [InlineData(TakeRefinementMode.Rework)]
    public void RefinementKeepsOptionalLorasDropsTurboAndReencodesAtTheNewSize(TakeRefinementMode mode)
    {
        var snapshot = WithLoras(PresetSnapshot("turbo8"));
        var package = new H3RefinementPackage(Guid.NewGuid(), 1024, new('A', 64), snapshot.Width, snapshot.Height, snapshot.FrameCount);
        var refinement = new TakeRefinement(Guid.NewGuid(), package, mode, 1344, 768, "upscaler.safetensors", H3UpscalerImplementation.Plus);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 1, Guid.NewGuid().ToString(), [],
            refine: new(refinement, "lumibelle-video.latent", "lumibelle-audio.latent"))).GetProperty("prompt");
        JsonElement Inputs(string node) => graph.GetProperty(node).GetProperty("inputs");
        string? From(string node, string input) => Inputs(node).GetProperty(input)[0].GetString();
        // Character and style LoRAs stay; the Turbo LoRA and its sigma shift don't.
        Assert.Equal("1", From("lora_1", "model")); Assert.Equal("lora_1", From("lora_2", "model"));
        Assert.Equal("lora_2", From("6", "model"));
        Assert.False(graph.TryGetProperty("16", out _)); Assert.False(graph.TryGetProperty("18", out _));
        // The prompt and references are encoded again at the new size.
        Assert.Equal(1344, Inputs("5").GetProperty("width").GetInt32()); Assert.Equal(768, Inputs("5").GetProperty("height").GetInt32());
        // A partial Standard pass from the saved, enlarged latent.
        Assert.Equal("res_multistep", Inputs("8").GetProperty("sampler_name").GetString());
        Assert.Equal(refinement.Steps, Inputs("9").GetProperty("steps").GetInt32());
        Assert.Equal(refinement.Denoise, Inputs("9").GetProperty("denoise").GetDouble());
        Assert.Equal(mode == TakeRefinementMode.Refine ? 7 : 13, refinement.Steps);
        Assert.Equal("lumibelle-video.latent", Inputs("30").GetProperty("latent").GetString());
        Assert.Equal("lumibelle-audio.latent", Inputs("31").GetProperty("latent").GetString());
        Assert.Equal(1344, Inputs("32").GetProperty("mode.width").GetInt32()); Assert.True(Inputs("32").TryGetProperty("keep_proportion", out _));
        Assert.Equal("33", From("10", "latent_image"));
        // Refine decodes and keeps the saved audio; Rework uses the newly sampled audio.
        var keep = mode == TakeRefinementMode.Refine;
        Assert.Equal(keep ? "35" : "10", From("12", "samples"));
        Assert.Equal(keep ? "31" : "20", From("22", "samples"));
        Assert.Equal("SaveLatent", graph.GetProperty("21").GetProperty("class_type").GetString());
        Assert.True(graph.TryGetProperty("15", out _));
    }
    [Theory]
    [InlineData("duplicate")] [InlineData("nan")] [InlineData("workflow")] [InlineData("server")] [InlineData("missing")]
    [InlineData("loader")] [InlineData("range")] [InlineData("turbo")] [InlineData("custom-turbo")] [InlineData("unregistered")] [InlineData("hidden")]
    public void H3LorasRejectInvalidOrUnavailableSelections(string scenario)
    {
        var shot = Ready(); var selection = VideoLora();
        var settings = new AiSettings(); var check = new ComfyLoraCheck(true, "Ready", [selection.Reference.FileName]);
        var visibility = new LoraVisibility();
        switch (scenario)
        {
            case "nan": selection = selection with { Strength = float.NaN }; break;
            case "workflow": selection = selection with { Reference = selection.Reference with { Workflow = LoraWorkflow.Krea2 } }; break;
            case "server": selection = selection with { Reference = selection.Reference with { ComfyUrl = "http://another.test" } }; break;
            case "missing": check = check with { Files = [] }; break;
            case "loader": check = check with { Success = false }; break;
            case "range": check = check with { MaximumStrength = .5f }; break;
            case "turbo": selection = selection with { Reference = selection.Reference with { FileName = settings.H3.Turbo8StepLora } }; break;
            case "custom-turbo": settings.H3.TurboLora = selection.Reference.FileName; break;
            case "hidden": visibility = new() { HiddenTags = ["private"] }; break;
        }
        shot.Loras = scenario == "duplicate" ? [selection, selection] : [selection];
        settings = settings with { LoraLibrary = scenario == "unregistered" ? [] : [new(selection.Reference) { Tags = ["private"] }] };
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.Capture(shot, settings, visibility, check));
    }
    [Fact]
    public void DisabledEntriesNeedNoCatalogAndOnlyEffectiveChangesInvalidateTakes()
    {
        var shot = Ready(); var fingerprint = H3Policy.Fingerprint(shot);
        shot.Loras = [VideoLora() with { Enabled = false }, VideoLora("zero.safetensors", 0)];
        Assert.Null(H3Loras.Capture(shot, new(), new(), null));
        Assert.Equal(fingerprint, H3Policy.Fingerprint(shot));
        shot.Loras = [VideoLora()]; Assert.NotEqual(fingerprint, H3Policy.Fingerprint(shot));
        var effective = H3Policy.Fingerprint(shot);
        shot.Loras = [VideoLora() with { Reference = VideoLora().Reference with { Name = "renamed" } }];
        Assert.Equal(effective, H3Policy.Fingerprint(shot));
    }
    [Fact]
    public void ShotLorasStackOnThePresetAndReplaceTheSameLora()
    {
        var shot = Ready(); var server = Snapshot(Guid.NewGuid(), shot).ComfyUrl;
        shot.Loras = [VideoLora("h3/style.safetensors", .7f, server), VideoLora("h3/character.safetensors", .5f, server)];
        var presetOnly = H3Policy.Fingerprint(shot);
        shot.ShotLoras = [VideoLora("h3/character.safetensors", 1.2f, server), VideoLora("h3/pose.safetensors", .3f, server)];

        Assert.Equal(["h3/style.safetensors", "h3/character.safetensors", "h3/pose.safetensors"], H3Loras.Selections(shot).Select(l => l.Reference.FileName));
        Assert.Equal([.7f, 1.2f, .3f], H3Loras.Selections(shot).Select(l => l.Strength));
        Assert.NotEqual(presetOnly, H3Policy.Fingerprint(shot));

        var settings = new AiSettings { ComfyUrl = server, LoraLibrary = [.. H3Loras.Selections(shot).Select(l => new LoraDefinition(l.Reference))] };
        var applied = H3Loras.Capture(shot, settings, new(), new(true, "Ready", [.. H3Loras.Selections(shot).Select(l => l.Reference.FileName)]))!;
        Assert.Equal([.7f, 1.2f, .3f], applied.Select(l => l.Strength));
        var snapshot = Snapshot(Guid.NewGuid(), shot) with { AppliedLoras = applied };
        H3Loras.ValidateSnapshot(snapshot);
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.ValidateSnapshot(snapshot with { Shot = snapshot.Shot with { ShotLoras = null } }));

        shot.ShotLoras = [VideoLora("h3/pose.safetensors"), VideoLora("h3/pose.safetensors")];
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.ValidateSelections(shot));
    }
    [Fact]
    public void ShotAndReelLorasAreSavedWithTheirContentAndAbsentOnesChangeNothing()
    {
        var composition = new ProductionComposition { ShotId = Guid.NewGuid() };
        composition.Shot.Loras = [VideoLora("h3/style.safetensors")];
        var legacy = JsonSerializer.SerializeToElement(ShotProductionContent.From(composition), AtomicJsonFile.Options);
        Assert.False(legacy.TryGetProperty("loras", out _));
        Assert.False(JsonSerializer.SerializeToElement(composition.Shot, AtomicJsonFile.Options).TryGetProperty("shotLoras", out _));

        composition.Shot.ShotLoras = [VideoLora("h3/pose.safetensors", .4f)];
        var content = ShotProductionContent.From(composition);
        Assert.Equal("h3/pose.safetensors", Assert.Single(content.Loras!).Reference.FileName);
        var other = new ProductionComposition { ShotId = composition.ShotId };
        other.Shot.Loras = [VideoLora("h3/other-preset.safetensors")];
        content.Apply(other);
        Assert.Equal("h3/pose.safetensors", Assert.Single(other.Shot.ShotLoras!).Reference.FileName);
        Assert.Equal("h3/other-preset.safetensors", Assert.Single(other.Shot.Loras!).Reference.FileName);

        var draft = new ReferenceReelDraft { Loras = [VideoLora("h3/style.safetensors")] };
        var fingerprint = ReferenceReels.Fingerprint(draft);
        Assert.Null(ReferenceReels.Inputs(draft).ShotLoras);
        draft.ReelLoras = [VideoLora("h3/style.safetensors", 1.5f)];
        Assert.NotEqual(fingerprint, ReferenceReels.Fingerprint(draft));
        Assert.Equal(1.5f, Assert.Single(H3Loras.Selections(ReferenceReels.Inputs(draft))).Strength);
    }
    [Fact]
    public void LegacySerializationRetainsImageAssignmentsAndAbsentVideoFields()
    {
        foreach (var workflow in new[] { ImageWorkflow.Krea2, ImageWorkflow.Flux2Klein9bKv })
        {
            var json = $$"""{"comfyUrl":"http://localhost:8188","fileName":"nested/file.safetensors","workflow":"{{workflow}}","name":"Legacy"}""";
            var reference = JsonSerializer.Deserialize<LoraReference>(json, AtomicJsonFile.Options)!;
            Assert.Equal(workflow.LoraWorkflow(), reference.Workflow);
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(json).RootElement, JsonSerializer.SerializeToElement(reference, AtomicJsonFile.Options)));
        }
        var legacy = Snapshot(Guid.NewGuid(), Ready());
        var serialized = JsonSerializer.SerializeToElement(ShotCopy.Of(legacy), AtomicJsonFile.Options);
        Assert.False(serialized.TryGetProperty("appliedLoras", out _));
        Assert.False(serialized.GetProperty("shot").TryGetProperty("loras", out _));
        H3Loras.CheckSubmission(legacy, null);
        var captured = WithLoras(legacy);
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.ValidateSnapshot(captured with { AppliedLoras = null }));
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.CheckSubmission(captured, new(true, "ready", [])));
        H3Loras.CheckSubmission(ShotCopy.Of(captured), new(true, "ready", captured.AppliedLoras!.Select(l => l.Reference.FileName).ToArray()));
    }
    [Fact]
    public void OptionalLoaderMalformedContractsDoNotBreakStandardReadiness()
    {
        var root = PerformanceCatalog(); root["LoraLoaderModelOnly"] = new JsonObject();
        var result = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), new());
        Assert.True(result.StandardReady); Assert.False(result.OptionalLoras!.Success);
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.CheckSubmission(WithLoras(Snapshot(Guid.NewGuid(), Ready())), result.OptionalLoras));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProductionAdapterRechecksCapturedFilesWithoutConsultingLibrary(bool refinement)
    {
        var root = PerformanceCatalog();
        root["LoraLoaderModelOnly"] = JsonNode.Parse("""{"input":{"required":{"model":["MODEL"],"lora_name":[["h3/character.safetensors","h3/styles/film.safetensors"]],"strength_model":["FLOAT",{"min":-2,"max":2}]}}}""");
        var snapshot = WithLoras(Snapshot(Guid.NewGuid(), Ready()));
        var http = new ScriptedHttpHandler((message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString()) });
        });
        var video = new ComfyH3Video(new TestHttpFactory(http), TestComfy.Monitor(), null!, null!, null!, null!);
        var adapter = new ComfyVideoJobAdapter(video);
        var request = new AiVideoJobRequest(1, Guid.NewGuid(), snapshot, []);
        // A missing optional file must be diagnosed on either path before uploads/submission.
        root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0] = new JsonArray("h3/character.safetensors");
        if (refinement) request = request with { Refinement = new(Guid.NewGuid(), new(Guid.NewGuid(), 1024, new('A', 64), 32, 32, snapshot.FrameCount), TakeRefinementMode.Refine, 32, 32, "upscaler.safetensors", H3UpscalerImplementation.Plus) };
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => adapter.ValidateAsync(request, _ct));
        Assert.Contains("h3/styles/film.safetensors", error.Message);
    }
    [Fact]
    public async Task ShotLorasDeepCopyRecoverAndRejectConcurrentWrites()
    {
        var f = Fixture(); var shot = Ready(); shot.Loras = [VideoLora()];
        var saved = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var copy = saved.Copy(); copy.Shots[0].Loras = [VideoLora(strength: .4f)];
        Assert.Equal(.7f, Assert.Single(saved.Shots[0].Loras!).Strength);
        var changed = await f.Shots.SaveAsync(f.Project.Id, copy.Shots, saved.Revision, "LoRA adjustment", _ct);
        Assert.Equal(.4f, Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots[0].Loras!).Strength);
        Assert.Equal(.7f, Assert.Single(changed.Recovery.Single(r => r.Reason == "LoRA adjustment").Shots[0].Loras!).Strength);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Shots.SaveAsync(f.Project.Id, saved.Shots, saved.Revision, ct: _ct));
        var other = Fixture(); Assert.Empty((await other.Shots.LoadAsync(other.Project.Id, _ct)).Shots);
    }
    [Fact]
    public async Task H3StackSurvivesQueueEditsExtensionRefinementAndTrash()
    {
        using var f = await QueuedVideoFixture.Create(this, 8);
        var selection = VideoLora(server: f.Settings.Value.ComfyUrl);
        f.Shot.Loras = [selection]; f.Settings.Value = f.Settings.Value with { LoraLibrary = [new(selection.Reference, .4f, "trigger")] };
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], document.Revision, ct: _ct);
        var submission = await f.Capture(2); var context = await f.Claim(submission);
        // Edits and removal of the registration must not replace the captured stack.
        f.Shot.Loras = null; f.Settings.Value = f.Settings.Value with { LoraLibrary = [] };
        document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], document.Revision, ct: _ct);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        await f.Jobs.UpdateAsync(submission.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        await f.Worker.ValidateExtensionAsync(context.Job, submission.Snapshot, _ct);
        await f.Jobs.ExtendBatchAsync(submission.Id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        var claim = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await f.Worker.ExecuteAsync(f.Context(claim, false), await f.Jobs.ReadSnapshotAsync(claim.Id, _ct), _ct);
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(f.Context(claim, true), await f.Jobs.ReadSnapshotAsync(claim.Id, _ct), _ct);
        f.NoNetwork = false;
        document = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Equal(3, document.Takes.Count);
        Assert.Null(document.Shots[0].Loras);
        Assert.All(document.Takes, t => Assert.Equal(selection.Strength, Assert.Single(t.Snapshot.AppliedLoras!).Strength));
        var source = document.Takes[0];
        var refine = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id,
            TakeRefinementMode.Refine, source.Width, source.Height, _ct);
        Assert.Equal(selection.Reference.FileName, Assert.Single(refine.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.AppliedLoras!).Reference.FileName);
        document = await f.Shots.DiscardAsync(f.Project.Id, source.Id, ShotTrashKind.Take, document.Revision, _ct);
        var trash = Assert.Single(document.Trash);
        Assert.Equal(selection.Strength, Assert.Single(trash.Take!.Snapshot.AppliedLoras!).Strength);
        document = await f.Shots.RestoreAsync(f.Project.Id, [trash.Id], document.Revision, _ct);
        Assert.Equal(selection.Reference, Assert.Single(document.Takes.Single(t => t.Id == source.Id).Snapshot.AppliedLoras!).Reference);
        Assert.All(f.Graphs, g => Assert.Equal(selection.Strength, g.GetProperty("prompt").GetProperty("lora_1").GetProperty("inputs").GetProperty("strength_model").GetSingle()));
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task H3RegistrationPreservesTagsCredentialsAndRejectsTurboFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var tags = new List<string> { "H3", "SFW" };
        var definition = new LoraDefinition(new("http://localhost:8188", "nested/h3-character.safetensors", LoraWorkflow.MiniMaxH3Ref2VA, "Character"), .65f, "exact trigger") { Tags = tags };
        var saved = await Store.SaveAsync(new() { LoraLibrary = [definition, LoraTests.Definition()] }, "key", cancellationToken: ct);
        tags.Clear();
        var reopened = (await Store.LoadAsync(ct)).LoraLibrary.First();
        Assert.Equal(definition.Reference, reopened.Reference); Assert.Equal(new[] { "h3", "sfw" }, reopened.Tags);
        Assert.Equal(.65f, reopened.DefaultStrength); Assert.Equal("exact trigger", reopened.TriggerText);
        Assert.Equal("key", await Store.ReadOpenRouterKeyAsync(ct));
        foreach (var file in new[] { saved.H3.TurboLora, "nested/" + saved.H3.Turbo8StepLora })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { LoraLibrary = [definition with { Reference = definition.Reference with { FileName = file } }] }, cancellationToken: ct));
        Assert.Equal(saved.Revision, (await Store.LoadAsync(ct)).Revision);
    }
}
