using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
namespace Lumibelle.Testing;

public sealed class MockVideoGenerator(IAssetStore? assets = null, IShotStore? shots = null, lumibelle.Services.Production.IReferenceVideoStore? referenceVideos = null) : IVideoGenerator
{
    public ConcurrentQueue<VideoSnapshot> Submitted { get; } = new();
    public int SubmitCount, DownloadCount;
    public bool FailTransfer, MissingJob, UncertainSubmission;
    public int DelayMilliseconds = 500;
    public Func<Task>? OnSubmit;
    public Func<CancellationToken, Task>? OnObserve;
    public Func<CancellationToken, Task<bool>>? OnCancel;
    public H3Configuration? Catalog;
    public Func<AiSettings, ComfyLoraCheck>? OptionalLoraCatalog;
    public int CheckCalls;
    public Task<H3Configuration> CheckAsync(AiSettings s, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CheckCalls);
        return Task.FromResult(Catalog ?? new H3Configuration(true, true, "Mock H3 Standard and Turbo are ready.", [s.H3.Model], [s.H3.Encoder], [s.H3.VideoVae,s.H3.AudioVae], [s.H3.TurboLora,s.H3.Turbo8StepLora,"h3/"+new H3Settings().Turbo8StepLora])
        { DiscoverySucceeded = true, VideoReferenceIssue = null, StartFrameIssue = null, Presets = H3Presets.Keys.Select(key => new H3PresetSetup(key, null, key switch {
                "larry" => [H3Presets.Checkpoint("larry", s.H3)!], "pdd" => [H3Presets.Checkpoint("pdd", s.H3)!], "turbo4" => [s.H3.TurboLora, "custom/renamed-ref2va-turbo.safetensors"],
                "turbo8" => [s.H3.Turbo8StepLora, "h3/"+new H3Settings().Turbo8StepLora, "custom/renamed-ref2va-turbo.safetensors"], _ => [] })).ToArray(), OptionalLoras = OptionalLoraCatalog?.Invoke(s) ?? new(true, "Mock H3 LoRAs ready.", ["h3/character.safetensors", "h3/styles/film.safetensors"]), Performance = new() { PyTorchIssue=null, KitchenIssue=null, SageIssue=null, SolIssue=null, FastArchiveIssue=null },
            PackageCaptureReady=true, RefinementIssue=null, LatentUpscalers=["mock-h3-3d.safetensors"],
            PreviewUpscaling=new(H3UpscalerImplementation.Plus, s.H3.LatentUpscaler == "mock-h3-3d.safetensors" ? null : "Choose an installed learned 3D upscaler checkpoint under Preview upscaling and Save."),
            Turbo8StepReady=true, InstalledModels=[s.H3.Model,"custom/renamed-ref2va.safetensors"], InstalledEncoders=[s.H3.Encoder,"custom/renamed-h3-encoder.safetensors"], InstalledVaes=[s.H3.VideoVae,s.H3.AudioVae,"custom/renamed-vae.safetensors"], InstalledLoras=[s.H3.TurboLora,s.H3.Turbo8StepLora,"h3/"+new H3Settings().Turbo8StepLora,"custom/renamed-ref2va-turbo.safetensors",H3Presets.Checkpoint("larry", s.H3)!,H3HyperFlow.Checkpoint(s.H3)],
            // The preset and upscaler node packs are installed; KJNodes for SageAttention is not.
            Nodes=new[] { H3Requirements.LarryNodes, H3Requirements.PddNodes, H3Requirements.SpectrumNodes, H3Requirements.UpscalerNodes }.Select(r => r.Node!).ToHashSet() });
    }
    public async Task ValidateInputsAsync(VideoSnapshot s, CancellationToken ct)
    {
        if (assets is null || shots is null) return;
        ShotLooks.Validate(s.Shot, await assets.LoadAsync(s.ProjectId, ct));
        foreach (var b in s.Shot.Images)
        {
            await using var media = b.Kind == ShotImageKind.AssetImage ? await assets.OpenImageAsync(s.ProjectId,b.AssetId,b.MediaId,ct) : throw new WorkspaceStoreException("The standalone frame was removed. Replace it with an asset image.");
            if (media is null) throw new WorkspaceStoreException("Restore or replace the missing reference.");
        }
    }
    public async Task PrepareAsync(VideoRun run, string path, CancellationToken ct)
    {
        if (run.Snapshot.Reel?.Recipe.Name.Contains("MEMORY_FAILURE", StringComparison.Ordinal) == true)
            throw new OutOfMemoryException("Mock application memory failure");
        if (run.Snapshot.Reel?.Recipe.Name.Contains("SLOW_PREPARATION", StringComparison.Ordinal) == true)
            await Task.Delay(30000, ct);
        await ValidateInputsAsync(run.Snapshot,ct); run.Inputs.Clear();
        var folder=Path.Combine(path,"inputs");Directory.CreateDirectory(folder);
        foreach(var reference in run.Snapshot.Shot.Images)
        {
            var name=$"image-{run.Inputs.Count:D2}.png";
            if(assets is not null)
            {
                await using var source=await assets.OpenImageAsync(run.Snapshot.ProjectId,reference.AssetId,reference.MediaId,ct)
                    ?? throw new WorkspaceStoreException("Mock reference is unavailable.");
                await File.WriteAllBytesAsync(Path.Combine(folder,name),await ComfyReferenceImageEditor.PrepareSourcePngAsync(source.Content,reference.Crop,ct),ct);
            }
            else { using var image=new Image<Rgb24>(16,16);await image.SaveAsPngAsync(Path.Combine(folder,name),ct); }
            run.Inputs.Add(new(name,false));
        }
        if (run.Snapshot.Shot.ContinuityFrame is { } continuity)
        {
            var name=$"image-{run.Inputs.Count:D2}.png";
            if (shots is not null) await File.WriteAllBytesAsync(Path.Combine(folder,name),await lumibelle.Services.Production.ProductionInputs.ContinuityPngAsync(run.Snapshot.ProjectId,continuity,shots,ct),ct);
            else { using var image=new Image<Rgb24>(16,16);await image.SaveAsPngAsync(Path.Combine(folder,name),ct); }
            run.Inputs.Add(new(name,false));
        }
        if (run.Snapshot.Shot.Videos.Count > 0)
            await (referenceVideos ?? throw new WorkspaceStoreException("Missing reference video fixture store.")).PrepareAsync(run.Snapshot.ProjectId, run.Snapshot.Shot, folder, run.Inputs, run.Snapshot.Settings, ct);
        foreach(var voice in run.Snapshot.Shot.Voices)
        {
            // The mocked generator does not consume audio. Real excerpt preparation
            // and decoding are exercised in the production media-tools tests.
            var name=$"voice-{run.Inputs.Count:D2}.wav";
            await File.WriteAllBytesAsync(Path.Combine(folder,name),[1,2,3],ct);run.Inputs.Add(new(name,true));
        }
        if (run.Snapshot.Shot.StartFrame is { } start)
        {
            const string name = "start-frame.png";
            if (shots is not null)
            {
                await using var frame = await shots.OpenAsync(run.Snapshot.ProjectId, start.TakeId, ShotTrashKind.Take, start.Frame, ct: ct)
                    ?? throw new WorkspaceStoreException("The take this shot starts from is in Trash or was deleted.");
                await using var target = File.Create(Path.Combine(folder, name)); await frame.Content.CopyToAsync(target, ct);
            }
            else { using var image=new Image<Rgb24>(16,16);await image.SaveAsPngAsync(Path.Combine(folder,name),ct); }
            run.Inputs.Add(new(name, false) { Kind = VideoInputKind.StartFrame });
        }
        run.InputsPrepared=true;
    }
    public async Task<string> SubmitAsync(VideoRun run, VideoCandidate c, CancellationToken ct)
    {
        Interlocked.Increment(ref SubmitCount); Submitted.Enqueue(ShotCopy.Of(run.Snapshot));
        if (OnSubmit is not null) await OnSubmit();
        if (UncertainSubmission) throw new HttpRequestException("Connection lost during submission");
        return Guid.NewGuid().ToString();
    }
    public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(VideoRun run, VideoCandidate c, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new(new(GenerationPhase.Generating,"Mock sampling"));
        if (OnObserve is not null) await OnObserve(ct);
        await Task.Delay(DelayMilliseconds,ct);
        if (run.Snapshot.PreviewUpscale is not null) { yield return new(new(GenerationPhase.Finalizing,"Upscaling video…")); await Task.Delay(250, ct); }
        if (run.Snapshot.Shot.Description.Contains("MOCK_COMFY_CRASH")) throw new HttpRequestException("ComfyUI connection failed.");
        yield return new(new(GenerationPhase.Finalizing,"Mock frames ready"),c.PromptId,JsonSerializer.SerializeToElement(new { outputs = new { } }),true);
    }
    public Task<bool> ExistsAsync(VideoRun r,VideoCandidate c,CancellationToken ct) => Task.FromResult(!MissingJob);
    public Task<bool> CancelAsync(VideoRun r,VideoCandidate c,CancellationToken ct) => OnCancel?.Invoke(ct)
        ?? (r.Snapshot.Shot.Description.Contains("MOCK_COMFY_CRASH") ? Task.FromException<bool>(new HttpRequestException("ComfyUI is offline.")) : Task.FromResult(true));
    public async Task<ShotTake> DownloadAsync(VideoRun r, VideoCandidate c,string dir,Func<string,Task> progress,CancellationToken ct)
    {
        Interlocked.Increment(ref DownloadCount); Directory.CreateDirectory(dir);
        if (FailTransfer) throw new IOException("Mock interrupted transfer");
        var s=r.Snapshot;
        var size=RefinementPolicy.OutputSize(r);
        using var image=new Image<Rgb24>(size.Width,size.Height,new Rgb24((byte)(50+c.Number*30),70,100));
        if (s.Shot.Description.Contains("KEYFRAME_FIXTURE"))
            for (var y = 0; y < size.Height; y++) for (var x = 0; x < size.Width; x++)
                image[x, y] = (x / 12 + y / 12) % 2 == 0 ? new Rgb24(220, 160, 90) : new Rgb24(30, 70, 120);
        using var buffer=new MemoryStream(); await image.SaveAsPngAsync(buffer,ct);var bytes=buffer.ToArray();
        var frames = r.Refinement is not null || s.OutputPolicy?.SaveLosslessFrames != false ? await MockFrameArchive.WriteAsync(dir,s.FrameCount,size.Width,size.Height,ct) : new List<ShotFrame>();
        await progress("Mock lossless WebP archive saved");
        var video=Path.Combine(dir,"video.mp4");
        if(!File.Exists(video))
        {
            var still=Path.Combine(dir,"mock-input.png");await File.WriteAllBytesAsync(still,bytes,ct);
            var info=new ProcessStartInfo(s.Settings.Ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-hide_banner","-loglevel","error","-y","-loop","1","-framerate","24","-i",still,"-f","lavfi","-i","anullsrc=r=32000:cl=stereo","-frames:v",s.FrameCount.ToString(),"-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-shortest",video})info.ArgumentList.Add(arg);
            using var p=Process.Start(info)!;var errors=p.StandardError.ReadToEndAsync(ct);await p.WaitForExitAsync(ct);if(p.ExitCode!=0)throw new IOException(await errors);
            File.Delete(still);
        }
        var package=s.CaptureRefinementData || r.Refinement is not null ? await MockRefinementPackage.WriteAsync(dir,s,r.Refinement,ct) : null;
        return new(){Id=c.TakeId,ShotId=s.Shot.Id,RunId=r.Id,Candidate=c.Number,Seed=c.Seed,CreatedUtc=DateTimeOffset.UtcNow,Width=size.Width,Height=size.Height,Directory=c.TakeId.ToString("D"),Frames=frames,Bytes=new FileInfo(video).Length+frames.DistinctBy(f=>f.FileName).Sum(f=>f.Bytes)+(package?.Bytes??0),Snapshot=ShotCopy.Of(s),Refinement=r.Refinement,RefinementPackage=package};
    }
}
