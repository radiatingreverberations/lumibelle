using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using Lumibelle.Testing;

namespace Lumibelle.Tests;
public sealed partial class ShotTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Lumibelle.ShotTests",Guid.NewGuid().ToString("N"));
    private readonly ShotClock _clock=new();
    private readonly CancellationToken _ct=TestContext.Current.CancellationToken;
    private (ProjectInfo Project, ProjectFiles Files, FileShotStore Shots, FileAssetStore Assets) Fixture()
    {
        var env=new ShotEnvironment { ContentRootPath=_root };var options=Options.Create(new ProjectStorageOptions { RootDirectory="projects" });
        var projects=new FileProjectStore(options,env,_clock,NullLogger<FileProjectStore>.Instance);
        var project=projects.CreateAsync(new("Shots"),_ct).GetAwaiter().GetResult();
        var files=new ProjectFiles(options,env,projects);return(project,files,new(files,_clock),new(files,_clock));
    }
    private static Shot Ready()=>new(){Title="The arrival",Description="The camera holds as the mouse looks up.",Duration=1,ApprovedScriptId=Guid.NewGuid(),SceneId=Guid.NewGuid()};
    private static VideoSnapshot Snapshot(Guid project,Shot shot)=>new(project,1,shot.Copy(),H3Policy.Compile(shot),H3Policy.Fingerprint(shot),"http://localhost:8188",new(),32,32,H3Policy.Frames(shot.Duration!.Value));
    private async Task<ShotDocument> AddTake(Guid project,IShotStore store,Shot shot,int number=1)
    {
        var snapshot=Snapshot(project,shot);var run=Guid.NewGuid();var dir=Path.Combine(await store.RunDirectoryAsync(project,run,_ct),"candidate-"+number);Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir,"video.mp4"),[1,2,3],_ct);
        var frames=await MockFrameArchive.WriteAsync(dir,snapshot.FrameCount,32,32,_ct);
        return await store.PublishTakeAsync(project,new(){ShotId=shot.Id,RunId=run,Candidate=number,Snapshot=snapshot,Width=32,Height=32,Frames=frames,Bytes=3+frames.DistinctBy(f=>f.FileName).Sum(f=>f.Bytes)},dir,_ct);
    }
    [Theory]
    [InlineData(1,39)] [InlineData(1.63,56)] [InlineData(5,124)] [InlineData(10,243)] [InlineData(15,362)]
    public void DurationsSnapUpWithoutPreferredLength(double seconds,int frames)
    { Assert.Equal(frames,H3Policy.Frames(seconds));Assert.True(H3Policy.Seconds(seconds)>=seconds);Assert.Equal(0,(frames-5)%17); }
    [Theory] [InlineData(0)] [InlineData(15.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidDurationsRejected(double seconds)=>Assert.Throws<WorkspaceStoreException>(()=>H3Policy.Frames(seconds));
    [Theory] [InlineData(false)] [InlineData(true)]
    public void GraphKeepsJointDecodingAndIndependentLosslessWebpOutput(bool turbo)
    {
        var s=Ready();s.Turbo=turbo;s.Images=[new(){AssetId=Guid.NewGuid(),MediaId=Guid.NewGuid(),Name="Mira"}];s.Dialogue=[new(){Speaker="Mira",Text="You called?"}];s.Voices=[new(){AssetId=Guid.NewGuid(),VoiceId=Guid.NewGuid(),Speaker="Mira",Duration=1}];
        var graph=JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(Snapshot(Guid.NewGuid(),s),42,"run",[new("run/image.png",false),new("run/voice.wav",true)])).GetProperty("prompt");
        JsonElement Input(string node,string field)=>graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.Equal("17",Input("15","images")[0].GetString());Assert.Equal("11",Input("17","images")[0].GetString());Assert.Equal("11",Input("13","images")[0].GetString());
        Assert.Equal("SaveAnimatedWEBP",graph.GetProperty("15").GetProperty("class_type").GetString());Assert.True(Input("15","lossless").GetBoolean());Assert.Equal(24,Input("15","fps").GetInt32());Assert.Equal(24,Input("17","batch_size").GetInt32());
        Assert.False(graph.GetProperty("15").GetProperty("inputs").TryGetProperty("audio",out _));Assert.DoesNotContain("SaveImage",graph.ToString());
        Assert.Equal("10",Input("11","samples")[0].GetString());Assert.Equal("10",Input("12","samples")[0].GetString());
        Assert.Equal("100",Input("5","ref_images.ref_image_0")[0].GetString());Assert.Equal("101",Input("5","ref_audios.ref_audio_0")[0].GetString());
        Assert.Equal("h264",Input("14","format.codec").GetString());Assert.Equal("res_multistep",Input("8","sampler_name").GetString());Assert.Equal("simple",Input("9","scheduler").GetString());
        Assert.Equal(turbo?4:20,Input("9","steps").GetInt32());Assert.Equal(turbo?"16":"1",Input("6","model")[0].GetString());
        if(turbo) Assert.Equal(1,Input("16","strength_model").GetDouble());
        Assert.DoesNotContain("base64",graph.ToString());
    }
    [Fact]
    public void CapabilityCheckSeparatesTurboAndRejectsWrongWeightsOrMissingInputs()
    {
        var obj=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/h3-contract.json")))!;
        H3Configuration Check(H3Settings? s=null)=>ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(obj),s??new());
        Assert.True(Check().StandardReady,Check().Message);Assert.True(Check().TurboReady);
        Assert.True(Check(new(){TurboLora="missing"}).StandardReady);Assert.False(Check(new(){TurboLora="missing"}).TurboReady);
        Assert.False(Check(new(){Model="minimax_h3_fl2va.safetensors"}).StandardReady);
        obj["MiniMaxH3ReferenceToVideo"]!["input"]!["required"]!.AsObject().Remove("prompt");Assert.False(Check().StandardReady);
    }
    [Fact]
    public void ModelCatalogSuggestsRef2VaFinetunesAndMergesButNotFl2Va()
    {
        var obj=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/h3-contract.json")))!;
        var installed=obj["UNETLoader"]!["input"]!["required"]!["unet_name"]![0]!.AsArray();
        var renamed="h3\\minimax_h3_singularity_v1.3_ref2va_pruned_int8.safetensors";
        var hybrid="minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors";
        var fl2va="minimax_h3_fl2va_pruned_int8_convrot.safetensors";
        installed.Add(renamed);installed.Add(hybrid);installed.Add(fl2va);installed.Add("flux-2-klein-9b-kv-fp8.safetensors");
        H3Configuration Check(string model)=>ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(obj),new(){Model=model});
        var configuration=Check(new H3Settings().Model);
        Assert.True(configuration.StandardReady,configuration.Message);
        Assert.Contains(new H3Settings().Model,configuration.Models);Assert.Contains("minimax_h3_ref2va_pruned_int8_convrot.safetensors",configuration.Models);
        Assert.Contains(renamed,configuration.Models);Assert.Contains(hybrid,configuration.Models);
        Assert.DoesNotContain(fl2va,configuration.Models);Assert.DoesNotContain("flux-2-klein-9b-kv-fp8.safetensors",configuration.Models);
        Assert.True(Check(hybrid).StandardReady,Check(hybrid).Message);Assert.True(Check(renamed).StandardReady);
        Assert.False(Check(fl2va).StandardReady);
    }
    [Fact]
    public void EncoderCatalogAcceptsHyphenatedQuantizationsAndPreservesExactPaths()
    {
        var obj=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/h3-contract.json")))!;
        var installed=obj["CLIPLoader"]!["input"]!["required"]!["clip_name"]![0]!.AsArray();
        var encoder="h3/qwen3vl_32b_minimax_h3-w4a8_mixed.safetensors";
        var unrelated="qwen3vl_32b_minimax_h30-w4a8.safetensors";
        var community="qwen3vl_32b_h3_ultra_uncensored_heretic_int8_convrot.safetensors";
        var tail="qwen3vl_32b_h3_generation_tail_50_63_int8_convrot.safetensors";
        installed.Add(encoder);installed.Add(unrelated);installed.Add("qwen_3_4b.safetensors");installed.Add(community);installed.Add(tail);
        H3Configuration Check(string name)=>ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(obj),new(){Encoder=name});
        var configuration=Check(encoder);
        Assert.Contains(encoder,configuration.Encoders);Assert.Contains(new H3Settings().Encoder,configuration.Encoders);
        Assert.DoesNotContain(unrelated,configuration.Encoders);Assert.DoesNotContain("qwen_3_4b.safetensors",configuration.Encoders);
        Assert.True(configuration.StandardReady,configuration.Message);Assert.True(configuration.TurboReady);
        Assert.Contains(community,configuration.Encoders);Assert.True(Check(community).StandardReady);
        Assert.Contains(unrelated,configuration.InstalledEncoders);Assert.Contains(tail,configuration.InstalledEncoders);
        Assert.DoesNotContain(tail,configuration.Encoders);Assert.False(Check(tail).StandardReady);
        Assert.False(Check(Path.GetFileName(encoder)).StandardReady);Assert.False(Check("missing-encoder.safetensors").StandardReady);
    }
    [Fact]
    public void AdvancedCatalogSelectionsUseInstalledIdentitiesWithoutFilenameApproval()
    {
        var obj=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/h3-contract.json")))!;
        void Install(string node,string input,string file)=>obj[node]!["input"]!["required"]![input]![0]!.AsArray().Add(file);
        var settings=new H3Settings { Model="custom/ref-transformer.safetensors",Encoder="custom/conditioning.safetensors",VideoVae="custom/video.safetensors",AudioVae="custom/audio.safetensors",TurboLora="custom/ref-turbo.safetensors" };
        Install("UNETLoader","unet_name",settings.Model);Install("CLIPLoader","clip_name",settings.Encoder);
        Install("VAELoader","vae_name",settings.VideoVae);Install("VAELoader","vae_name",settings.AudioVae);Install("LoraLoaderModelOnly","lora_name",settings.TurboLora);
        H3Configuration Check(H3Settings s)=>ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(obj),s);
        var checkedFiles=Check(settings);Assert.True(checkedFiles.StandardReady);Assert.True(checkedFiles.TurboReady);
        Assert.DoesNotContain(settings.Model,checkedFiles.Models);Assert.Contains(settings.Model,checkedFiles.InstalledModels);
        Assert.DoesNotContain(settings.Encoder,checkedFiles.Encoders);Assert.Contains(settings.Encoder,checkedFiles.InstalledEncoders);
        Assert.Contains(settings.VideoVae,checkedFiles.InstalledVaes);Assert.Contains(settings.TurboLora,checkedFiles.InstalledLoras);
        Assert.Contains("compatibility has not been tested",checkedFiles.Message);
        var wrong="minimax_h3_fl2va_pruned.safetensors";Install("UNETLoader","unet_name",wrong);
        Assert.False(Check(settings with { Model=wrong }).StandardReady);
        Assert.False(Check(settings with { Encoder="missing.safetensors" }).StandardReady);
        Assert.False(Check(settings with { TurboLora="missing.safetensors" }).TurboReady);
        obj["MiniMaxH3ReferenceToVideo"]!["input"]!["required"]!.AsObject().Remove("prompt");Assert.False(Check(settings).StandardReady);
    }
    [Fact]
    public void CompilerIsDeterministicAndPreservesSpeakerAndReferenceOrder()
    {
        var shot=Ready();shot.Images=[new(){AssetId=Guid.NewGuid(),MediaId=Guid.NewGuid(),Name="Mira",Notes="White dress"},new(){Kind=ShotImageKind.ContinuityFrame,MediaId=Guid.NewGuid(),Name="End state",Role="Continuity state",Notes="Hand resting on door"}];
        shot.Dialogue=[new(){Speaker="Mira",Language="Swedish",Text="Du kallade?"},new(){Speaker="Pip",Text="Yes."}];shot.Voices=[new(){VoiceId=Guid.NewGuid(),AssetId=Guid.NewGuid(),Speaker="Pip",Duration=2}];
        var prompt=H3Policy.Compile(shot);Assert.Equal(prompt,H3Policy.Compile(shot.Copy()));Assert.Contains("<Picture 2>",prompt);Assert.Contains("Pip (S2)",prompt);Assert.Contains("<d>[Swedish] Du kallade?</d>",prompt);Assert.Contains("One continuous",prompt);Assert.DoesNotContain("[Shot 2]",prompt);
        var fingerprint=H3Policy.Fingerprint(shot);shot.SelectedTakeId=Guid.NewGuid();Assert.Equal(fingerprint,H3Policy.Fingerprint(shot));shot.Description+=" Now.";Assert.NotEqual(fingerprint,H3Policy.Fingerprint(shot));
    }
    [Fact]
    public void PlanningRetainsProposedDialogueAndFlagsDifferencesForManualReview()
    {
        var doc=ScriptFixtures.Document();var approved=ScriptFixtures.Approved(doc.Blocks,doc.ProjectId);
        var request=new ShotPlanningRequest(approved,new(){ProjectId=doc.ProjectId},[doc.Blocks[0].Id],8,"Keep the quiet pause",new(AiBackend.OpenRouter,"mock","Mock"));
        var shot=Ready();shot.SceneId=doc.Blocks[0].Id;shot.SourceBlockIds=doc.Blocks.Select(b=>b.Id).ToList();shot.Duration=7.9;shot.Dialogue=[new(){Speaker="MOUSE",Text="You called?"}];
        string Raw()=>JsonSerializer.Serialize(new[]{shot},AtomicJsonFile.Options);
        var parsed=ShotPlanner.Parse(Raw(),request);Assert.Null(parsed.Error);Assert.Empty(parsed.UncoveredDialogue);Assert.Equal(approved.Id,parsed.Shots[0].ApprovedScriptId);
        shot.Dialogue.Clear();Assert.Single(ShotPlanner.Parse(Raw(),request).UncoveredDialogue);
        shot.Dialogue=[new(){Speaker="MOUSE",Text="Invented words"}];var revised=ShotPlanner.Parse(Raw(),request);Assert.Null(revised.Error);Assert.Single(revised.DialogueNotes);Assert.Equal("Invented words", revised.Shots[0].Dialogue[0].Text);
        shot.Dialogue.Clear();shot.Duration=8.1;Assert.NotNull(ShotPlanner.Parse(Raw(),request).Error);
        shot.Duration=7.9;shot.Description=null!;
        var invalid=ShotPlanner.Parse(Raw(),request);Assert.NotNull(invalid.Error);Assert.Empty(invalid.Shots);Assert.Equal(Raw(),invalid.Raw);
        Assert.NotNull(ShotPlanner.Parse("[{",request).Error);
        Assert.Contains("NO preferred length",ShotPlanner.BuildMessages(request)[0].Text);
    }
    [Fact]
    public void FencedShotProposalsRetainRawResponseAndAllValidation()
    {
        var doc=ScriptFixtures.Document();var approved=ScriptFixtures.Approved(doc.Blocks,doc.ProjectId);
        var request=new ShotPlanningRequest(approved,new(){ProjectId=doc.ProjectId},[doc.Blocks[0].Id],15,"",new(AiBackend.OpenRouter,"mock","Mock"));
        var shot=Ready();shot.SceneId=doc.Blocks[0].Id;shot.SourceBlockIds=doc.Blocks.Select(b=>b.Id).ToList();shot.Duration=14;
        shot.Dialogue=[new(){Speaker="MOUSE",Text="You called?"}];
        string Json()=>JsonSerializer.Serialize(new[]{shot},AtomicJsonFile.Options);
        string Fenced()=>"```json\n"+Json()+"\n```";
        foreach(var raw in new[]{Json(),Fenced()," \r\n```JSON\r\n"+Json()+"\r\n```\t","```\n"+Json()+"\n```","```json "+Json()+" ```"})
        {
            var result=ShotPlanner.Parse(raw,request);Assert.Null(result.Error);Assert.Single(result.Shots);Assert.Equal(raw,result.Raw);
            Assert.Empty(result.UncoveredDialogue);Assert.Equal("You called?",result.Shots[0].Dialogue[0].Text);
            Assert.Equal(approved.Id,result.Shots[0].ApprovedScriptId);Assert.Equal(14,result.Shots[0].Duration);
        }
        foreach(var raw in new[]{"```json\n"+Json(),"```json\n[{\n```",Fenced()+"\nMore explanation",Fenced()+"\n"+Fenced(),"Here is the result:\n"+Fenced()})
        {
            var result=ShotPlanner.Parse(raw,request);Assert.NotNull(result.Error);Assert.Empty(result.Shots);Assert.Equal(raw,result.Raw);
        }
        shot.Duration=15.1;Assert.NotNull(ShotPlanner.Parse(Fenced(),request).Error);
        shot.Duration=14;shot.SourceBlockIds=[Guid.NewGuid()];Assert.NotNull(ShotPlanner.Parse(Fenced(),request).Error);
        // A partially unresolved reference list keeps the shot and reports the dropped references.
        shot.SourceBlockIds=[doc.Blocks[0].Id,Guid.NewGuid(),doc.Blocks[1].Id];
        var partial=ShotPlanner.Parse(Fenced(),request);
        Assert.Null(partial.Error);Assert.Single(partial.Shots);
        Assert.Equal([doc.Blocks[0].Id,doc.Blocks[1].Id],partial.Shots[0].SourceBlockIds);
        Assert.Single(partial.SourceNotes);Assert.Contains("1 of 3",Assert.Single(partial.SourceNotes));
        Assert.Contains("did not match a block in this scene",partial.SourceNotes[0]);
        Assert.Contains("A mouse appears in a saucepan.",partial.Shots[0].SourceExcerpt);
        shot.SourceBlockIds=doc.Blocks.Select(b=>b.Id).ToList();shot.Dialogue[0].Text="Invented words";
        var changedDialogue=ShotPlanner.Parse(Fenced(),request);Assert.Single(changedDialogue.Shots);Assert.Null(changedDialogue.Error);Assert.Single(changedDialogue.DialogueNotes);
    }
    [Theory]
    [InlineData("End holding on her face, leaving the screenplay’s cut to black for the edit after this take.")]
    [InlineData("The camera holds. No hard cut or crossfade within this take.")]
    [InlineData("Hold on the doorway, then cut to the window.")]
    [InlineData("[Shot 2] The camera holds as the mouse turns.")]
    [InlineData("[Shot 10] The camera holds as the mouse turns.")]
    [InlineData("At 00:03 the mouse turns toward the window.")]
    public void CameraWordingIsAdvisoryAndPreservesTheExactProposal(string description)
    {
        var doc = ScriptFixtures.Document();
        var request = new ShotPlanningRequest(ScriptFixtures.Approved(doc.Blocks, doc.ProjectId), new() { ProjectId = doc.ProjectId },
            [doc.Blocks[0].Id], 15, "", new(AiBackend.OpenRouter, "mock", "Mock"));
        var shot = Ready(); shot.SceneId = doc.Blocks[0].Id; shot.SourceBlockIds = doc.Blocks.Select(b => b.Id).ToList();
        shot.Duration = 5; shot.Description = description; shot.Dialogue = [new() { Speaker = "MOUSE", Text = "You called?" }];
        var raw = JsonSerializer.Serialize(new[] { shot }, AtomicJsonFile.Options);
        foreach (var coverageOnly in new[] { false, true })
        {
            var parsed = ShotPlanner.Parse(raw, request with { CoverageOnly = coverageOnly });
            Assert.Null(parsed.Error); Assert.Single(parsed.CoverageNotes);
            Assert.Equal(description, Assert.Single(parsed.Shots).Description);
            Assert.Equal("You called?", parsed.Shots[0].Dialogue[0].Text);
            Assert.Equal(raw, parsed.Raw); Assert.Empty(parsed.DialogueNotes); Assert.Empty(parsed.UncoveredDialogue);
        }
    }
    [Fact]
    public async Task RecoveryAndConcurrentMediaPreserveDraftAndConflictProtection()
    {
        var f=Fixture();var shot=Ready();var d=await f.Shots.SaveAsync(f.Project.Id,[shot],0,ct:_ct);var before=d.Revision;
        var media=await AddTake(f.Project.Id,f.Shots,shot);Assert.Equal(before+1,media.Revision);Assert.Equal(shot.Description,media.Shots[0].Description);
        await Assert.ThrowsAsync<WorkspaceConflictException>(()=>f.Shots.SaveAsync(f.Project.Id,[shot],before,ct:_ct));
        shot.Description="Changed";d=await f.Shots.SaveAsync(f.Project.Id,[shot],media.Revision,ct:_ct);var recovery=d.Recovery[0];
        d=await f.Shots.RecoverAsync(f.Project.Id,recovery.Id,d.Revision,_ct);Assert.NotEqual("Changed",d.Shots[0].Description);Assert.Single(d.Takes);
    }
    [Fact]
    public async Task SelectedTakeCannotBeDiscardedAndRestoreRecreatesOnlyItsOwner()
    {
        var f=Fixture();var shot=Ready();var d=await f.Shots.SaveAsync(f.Project.Id,[shot],0,ct:_ct);d=await AddTake(f.Project.Id,f.Shots,shot);d=await AddTake(f.Project.Id,f.Shots,shot,2);
        d.Shots[0].SelectedTakeId=d.Takes[0].Id;d=await f.Shots.SaveAsync(f.Project.Id,d.Shots,d.Revision,ct:_ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(()=>f.Shots.DiscardAsync(f.Project.Id,d.Takes[0].Id,ShotTrashKind.Take,d.Revision,_ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(()=>f.Shots.SaveAsync(f.Project.Id,[],d.Revision,ct:_ct));
        d.Shots[0].SelectedTakeId=null;d=await f.Shots.SaveAsync(f.Project.Id,d.Shots,d.Revision,ct:_ct);d=await f.Shots.SaveAsync(f.Project.Id,[],d.Revision,ct:_ct);Assert.Equal(2,d.Trash.Count);
        d=await f.Shots.RestoreAsync(f.Project.Id,[d.Trash[0].Id],d.Revision,_ct);Assert.Single(d.Shots);Assert.Single(d.Takes);Assert.Single(d.Trash);Assert.Equal(shot.Id,d.Shots[0].Id);
    }
    [Fact]
    public async Task ExpiryAndPurgeRaceHaveOneWinnerAndRestartCleanupCatchesUp()
    {
        var f=Fixture();var shot=Ready();var d=await f.Shots.SaveAsync(f.Project.Id,[shot],0,ct:_ct);d=await AddTake(f.Project.Id,f.Shots,shot);d=await f.Shots.DiscardAsync(f.Project.Id,d.Takes[0].Id,ShotTrashKind.Take,d.Revision,_ct);
        var id=d.Trash[0].Id;_clock.Now=d.Trash[0].ExpiresUtc;
        await Assert.ThrowsAsync<WorkspaceStoreException>(()=>f.Shots.RestoreAsync(f.Project.Id,[id],d.Revision,_ct));
        var facade=new MediaTrashStore(f.Files,f.Assets,f.Assets,f.Assets,new FileShotStore(f.Files,_clock),_clock);
        Assert.Empty(await facade.CleanupAsync(_ct));Assert.Empty((await f.Shots.LoadAsync(f.Project.Id,_ct)).Trash);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    private sealed class ShotClock:TimeProvider {public DateTimeOffset Now=new(2026,9,5,12,0,0,TimeSpan.Zero);public bool AdvanceOnRead;public override DateTimeOffset GetUtcNow(){var now=Now;if(AdvanceOnRead)Now=Now.AddMilliseconds(1);return now;}}
    private sealed class ShotEnvironment:IHostEnvironment {public string EnvironmentName{get;set;}="Test";public string ApplicationName{get;set;}="Test";public string ContentRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
}
