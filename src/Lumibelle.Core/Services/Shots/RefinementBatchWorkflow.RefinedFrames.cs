using lumibelle.Models;

namespace lumibelle.Services.Shots;

// A later clip's shot frames and start frame may come from takes that are refined earlier in the same batch.
// With UseRefinedFrames, that clip waits for those finals and uses the same moment of each refined take.
public sealed partial class RefinementBatchWorkflow
{
    private abstract record Dependency;
    private sealed record Refined(ShotTake Take) : Dependency;
    private sealed record Pending : Dependency;
    private sealed record Unavailable : Dependency;

    private sealed record FrameResolution(MotionReferenceOptions Options, RefinementBatchItem? WaitFor, IReadOnlyList<string> Adjustments);

    private static RefinementBatchItem? SourceItem(RefinementBatch run, RefinementBatchItem item, Guid takeId) =>
        run.Items.FirstOrDefault(i => i.Kind == RefinementSourceKind.Take && i.Id != item.Id && i.SourceId == takeId);

    private static bool DependsOn(RefinementBatch run, RefinementBatchItem item, RefinementBatchItem other) =>
        item.Kind == RefinementSourceKind.Take && (item.Options.Draft.Shot.ShotFrames.Any(f => f.TakeId == other.SourceId) || item.Options.Draft.Shot.StartFrame?.TakeId == other.SourceId);

    // Whether following frame dependencies from one clip leads to another: any cycle through the asking clip, however long.
    private static bool Reaches(RefinementBatch run, RefinementBatchItem from, RefinementBatchItem to)
    {
        var seen = new HashSet<Guid>(); var next = new Stack<RefinementBatchItem>([from]);
        while (next.TryPop(out var current))
        {
            if (!seen.Add(current.Id)) continue;
            foreach (var other in run.Items.Where(i => i.Id != current.Id && DependsOn(run, current, i)))
            {
                if (other.Id == to.Id) return true;
                next.Push(other);
            }
        }
        return false;
    }

    // Without loading takes: whether a clip this item waits for may still produce a refined take.
    private static bool StillPending(RefinementBatch run, RefinementBatchItem item, RefinementBatchItem dependency, IReadOnlyList<AiJobHeader> queued) => dependency.State switch {
        RefinementItemState.Preparing or RefinementItemState.Ready => true,
        RefinementItemState.Submitted => dependency.ResultId is null && queued.SingleOrDefault(j => j.Id == dependency.Id) is { } job &&
            (Unfinished(job) || job.State == AiJobState.Completed),
        _ => false
    } && !Reaches(run, dependency, item);

    private Dependency Resolve(RefinementBatch run, RefinementBatchItem item, RefinementBatchItem dependency, ShotDocument doc, IReadOnlyList<AiJobHeader> queued)
    {
        // A cycle cannot wait for itself; its clips keep their draft frames.
        if (Reaches(run, dependency, item)) return new Unavailable();
        var take = dependency.ResultId is { } result ? doc.Takes.FirstOrDefault(t => t.Id == result)
            : doc.Takes.FirstOrDefault(t => t.AiJobId == dependency.Id && queued.Any(j => j.Id == dependency.Id && j.State == AiJobState.Completed));
        if (take is not null) return new Refined(take);
        return StillPending(run, item, dependency, queued) && !queued.Any(j => j.Id == dependency.Id && j.State == AiJobState.Completed) ? new Pending() : new Unavailable();
    }

    // The frames of this clip's final that later clips will use, so the render can keep them losslessly.
    private static IReadOnlyList<int>? FramesUsedLater(RefinementBatch run, RefinementBatchItem item)
    {
        if (!run.UseRefinedFrames || item.Kind != RefinementSourceKind.Take || item.Options.Draft.Shot.Duration is not { } duration) return null;
        var output = H3Policy.Frames(duration);
        var frames = run.Items.Where(other => other.Id != item.Id && other.Kind == RefinementSourceKind.Take && other.State == RefinementItemState.Preparing)
            .SelectMany(other => other.Options.Draft.Shot.ShotFrames.Where(f => f.TakeId == item.SourceId).Select(f => f.Frame)
                .Concat(other.Options.Draft.Shot.StartFrame is { } start && start.TakeId == item.SourceId ? [start.Frame] : []))
            .Select(f => Map(f, item.Options.Draft.Source.VisibleFrames, output)).Distinct().Order().ToArray();
        return frames.Length == 0 ? null : frames;
    }

    private static int Map(int frame, int sourceFrames, int refinedFrames) => MotionReferencePolicy.MapFrame(frame, sourceFrames, refinedFrames);

    private async Task<FrameResolution> ResolveRefinedFramesAsync(Guid project, RefinementBatch run, RefinementBatchItem item, CancellationToken ct)
    {
        var shot = item.Options.Draft.Shot;
        if (!run.UseRefinedFrames || item.Kind != RefinementSourceKind.Take ||
            !shot.ShotFrames.Any(f => SourceItem(run, item, f.TakeId) is not null) && !(shot.StartFrame is { } s && SourceItem(run, item, s.TakeId) is not null))
            return new(item.Options, null, []);
        var doc = await shots.LoadAsync(project, ct); var queued = (await jobs.ReadAsync(ct)).Jobs;
        var options = ShotCopy.Of(item.Options); var resolved = options.Draft.Shot;
        List<string> adjustments = [];
        for (var i = 0; i < resolved.ShotFrames.Count; i++)
        {
            var frame = resolved.ShotFrames[i];
            if (SourceItem(run, item, frame.TakeId) is not { } dependency) continue;
            switch (Resolve(run, item, dependency, doc, queued))
            {
                case Pending: return new(item.Options, dependency, []);
                case Refined(var take):
                    resolved.ShotFrames[i] = frame with { TakeId = take.Id, Frame = Map(frame.Frame, dependency.Options.Draft.Source.VisibleFrames, take.FrameCount), Name = $"{dependency.Name} · refined" };
                    adjustments.Add($"A shot frame uses the refined take of {dependency.Name}.");
                    break;
                default:
                    adjustments.Add($"A shot frame keeps the draft of {dependency.Name}: its refined take is unavailable.");
                    break;
            }
        }
        if (resolved.StartFrame is { } start && SourceItem(run, item, start.TakeId) is { } opening)
        {
            switch (Resolve(run, item, opening, doc, queued))
            {
                case Pending: return new(item.Options, opening, []);
                case Refined(var take):
                    resolved.StartFrame = start with { TakeId = take.Id, Frame = Map(start.Frame, opening.Options.Draft.Source.VisibleFrames, take.FrameCount) };
                    adjustments.Add($"The start frame uses the refined take of {opening.Name}.");
                    break;
                default:
                    adjustments.Add($"The start frame keeps the draft of {opening.Name}: its refined take is unavailable.");
                    break;
            }
        }
        return new(options, null, adjustments);
    }
}
