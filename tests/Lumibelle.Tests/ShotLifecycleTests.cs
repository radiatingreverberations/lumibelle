using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Lumibelle.Testing;
namespace Lumibelle.Tests;
public sealed partial class ShotTests
{
    private static async Task Until(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while(!condition())await Task.Delay(20,timeout.Token);
    }
    private static (Shot Shot,FakeScriptStore Scripts) ApprovedShot(Guid project)
    {
        var d=ScriptFixtures.Document(project);var approved=ScriptFixtures.Approved(d.Blocks,project);var s=Ready();s.ApprovedScriptId=approved.Id;s.SceneId=d.Blocks[0].Id;s.SourceBlockIds=d.Blocks.Select(b=>b.Id).ToList();return(s,new(){Document=d,Approved=approved});
    }
    [Fact]
    public async Task LockedTakeBundleRetainsPurgeIntentAndCleansUpOnRetry()
    {
        var f=Fixture();var s=Ready();var d=await f.Shots.SaveAsync(f.Project.Id,[s],0,ct:_ct);d=await AddTake(f.Project.Id,f.Shots,s);var take=d.Takes[0];d=await f.Shots.DiscardAsync(f.Project.Id,take.Id,ShotTrashKind.Take,d.Revision,_ct);
        var path=Path.Combine(await f.Files.DirectoryAsync(f.Project.Id,_ct),"shots","takes",take.Directory,"video.mp4");
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            d=await f.Shots.PurgeAsync(f.Project.Id,[d.Trash[0].Id],d.Revision,_ct);Assert.True(d.Trash[0].Purging);Assert.NotNull(d.Trash[0].Error);
            await Assert.ThrowsAsync<WorkspaceStoreException>(()=>f.Shots.RestoreAsync(f.Project.Id,[d.Trash[0].Id],d.Revision,_ct));
        }
        var facade=new MediaTrashStore(f.Files,f.Assets,f.Assets,f.Assets,f.Shots,_clock);Assert.Empty(await facade.CleanupAsync(_ct));Assert.Empty((await f.Shots.LoadAsync(f.Project.Id,_ct)).Trash);
    }
    [Fact]
    public async Task ConflictingRestoreAndPurgeCannotBothPublish()
    {
        var f=Fixture();var s=Ready();var d=await f.Shots.SaveAsync(f.Project.Id,[s],0,ct:_ct);d=await AddTake(f.Project.Id,f.Shots,s);d=await f.Shots.DiscardAsync(f.Project.Id,d.Takes[0].Id,ShotTrashKind.Take,d.Revision,_ct);
        var id=d.Trash[0].Id;
        var results=await Task.WhenAll(Record.ExceptionAsync(async()=>await f.Shots.RestoreAsync(f.Project.Id,[id],d.Revision,_ct)).AsTask(),Record.ExceptionAsync(async()=>await f.Shots.PurgeAsync(f.Project.Id,[id],d.Revision,_ct)).AsTask());
        Assert.Single(results,e=>e is WorkspaceConflictException);Assert.Single(results,e=>e is null);
    }
    [Fact]
    public async Task FailedPublicationKeepsOldManifestAndMediaForRetry()
    {
        var f=Fixture();var s=Ready();var d=await f.Shots.SaveAsync(f.Project.Id,[s],0,ct:_ct);d=await AddTake(f.Project.Id,f.Shots,s);
        using(var locked=TestProjectDatabase.HoldReadOnly(await f.Files.DirectoryAsync(f.Project.Id,_ct)))
            await Assert.ThrowsAnyAsync<Exception>(()=>f.Shots.DiscardAsync(f.Project.Id,d.Takes[0].Id,ShotTrashKind.Take,d.Revision,_ct));
        var saved=await f.Shots.LoadAsync(f.Project.Id,_ct);Assert.Equal(d.Revision,saved.Revision);Assert.Single(saved.Takes);Assert.Empty(saved.Trash);
        await using var media=await f.Shots.OpenAsync(f.Project.Id,saved.Takes[0].Id,ShotTrashKind.Take,ct:_ct);Assert.NotNull(media);
    }
    [Fact]
    public async Task ShotsModelPreferencePreservesOtherStudiosAndLoraVisibility()
    {
        var f=Fixture();var store=new FileProjectAiPreferencesStore(f.Files);var model=new TextModelReference(AiBackend.OpenRouter,"test/shot","Shots model");
        await store.SetSelectionAsync(f.Project.Id,TextAssistantStudio.Story,model,_ct);
        await Task.WhenAll(store.SetSelectionAsync(f.Project.Id,TextAssistantStudio.Shots,model,_ct),store.SetSelectionAsync(f.Project.Id,TextAssistantStudio.PromptEnhancement,model,_ct));
        var saved=await store.LoadAsync(f.Project.Id,_ct);Assert.Equal(model,saved.Shots);Assert.Equal(model,saved.PromptEnhancement);Assert.Equal(model,saved.Story);
        await store.SetSelectionAsync(f.Project.Id,TextAssistantStudio.Shots,null,_ct);saved=await store.LoadAsync(f.Project.Id,_ct);Assert.Null(saved.Shots);Assert.Equal(model,saved.PromptEnhancement);
    }
    private static byte[] Wav(int seconds=2)
    {
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream);var size=seconds*32000*2;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+size);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(32000);w.Write(64000);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(size);w.Write(new byte[size]);return stream.ToArray();
    }
    [Fact]
    public async Task VoiceOriginalExcerptAndAssetRecreationSurviveTrash()
    {
        var f=Fixture();var asset=new ReferenceAsset(){Id=Guid.NewGuid(),Name="Mira",Category=AssetCategory.Character};var d=await f.Assets.SaveAsync(new(){ProjectId=f.Project.Id,Assets=[asset]},0,_ct);
        var original=Wav();using var audio=new MemoryStream(original);d=await f.Assets.AddVoiceAsync(f.Project.Id,asset.Id,audio,"voice.wav","Mira warm",.5,1,new(),d.Revision,_ct);var voice=d.Voices[0];
        var source=Path.Combine(_root,"original.wav");var prepared=Path.Combine(_root,"excerpt.wav");await File.WriteAllBytesAsync(source,original,_ct);await new ProductionMediaTools().PrepareVoiceAsync(source,prepared,.5,1,new(),_ct);
        Assert.InRange(await new ProductionMediaTools().AudioDurationAsync(prepared,new(),_ct),.999,1.001);Assert.DoesNotContain("Lavf",System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(prepared,_ct)));
        _clock.AdvanceOnRead=true;d=await f.Assets.DeleteAssetAsync(f.Project.Id,asset.Id,d.Revision,_ct);Assert.Empty(d.Assets);Assert.Empty(d.Voices);Assert.Single(d.VoiceTrash);
        d=await f.Assets.RestoreVoicesAsync(f.Project.Id,[d.VoiceTrash[0].Id],d.Revision,_ct);Assert.Equal(asset.Id,d.Assets[0].Id);Assert.Empty(d.Assets[0].Images);Assert.Equal(voice,d.Voices[0]);
        await using var restored=await f.Assets.OpenVoiceAsync(f.Project.Id,voice.Id,ct:_ct);using var output=new MemoryStream();await restored!.Content.CopyToAsync(output,_ct);Assert.Equal(original,output.ToArray());
        var deleted=await f.Assets.DiscardVoiceAsync(f.Project.Id,voice.Id,d.Revision,_ct);var resaved=await f.Assets.SaveAsync(d with{Revision=deleted.Revision},deleted.Revision,_ct);Assert.Empty(resaved.Voices);Assert.Single(resaved.VoiceTrash);
    }
    [Fact]
    public async Task InvalidAudioAndExcerptsNeverPublish()
    {
        var f=Fixture();var asset=new ReferenceAsset(){Id=Guid.NewGuid(),Name="Mira",Category=AssetCategory.Character};var d=await f.Assets.SaveAsync(new(){ProjectId=f.Project.Id,Assets=[asset]},0,_ct);
        foreach(var bytes in new[]{new byte[]{1,2,3},Wav()})
        {using var stream=new MemoryStream(bytes);await Assert.ThrowsAnyAsync<Exception>(()=>f.Assets.AddVoiceAsync(f.Project.Id,asset.Id,stream,"fake.wav","Voice",0,15,new(),d.Revision,_ct));}
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id,_ct)).Voices);
    }
}
