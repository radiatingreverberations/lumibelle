using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class ShotDirections
{
    public static string StyleName(DirectingStyle style) => style switch {
        DirectingStyle.PrestigeDrama => "Prestige Drama", DirectingStyle.KineticAction => "Kinetic Action",
        DirectingStyle.EnsembleClassical => "Ensemble / Classical", DirectingStyle.PerformanceComedy => "Performance Comedy", _ => "Auto"
    };
    public static string StyleGuidance(DirectingStyle style) => style switch {
        DirectingStyle.PrestigeDrama => "Prioritise subtext, meaningful distance, selective intimacy and patient changes in audience attention. Earn isolation and close views through dramatic pressure.",
        DirectingStyle.KineticAction => "Prioritise readable objectives, obstacles, physical cause and effect, screen direction and geography. Change scale and energy with the action; movement must clarify it.",
        DirectingStyle.EnsembleClassical => "Prioritise shared compositions, expressive staging in depth, relationships and motivated reframing. Hold the group when interactions carry the beat; isolate only when the story earns it.",
        DirectingStyle.PerformanceComedy => "Prioritise legible setup, anticipation, reveal and reaction. Protect performance timing and negative space; a delayed reaction or held composition may be funnier than another cut.",
        _ => "Choose the directing approach that serves this scene's dramatic beats, performance and audience information. Let framing, staging and pace develop with the story."
    };
    public static string StyleDescription(DirectingStyle style) => style switch {
        DirectingStyle.EnsembleClassical => "Keep characters and their relationships visible. Favor group compositions, clear geography and restrained camera movement.",
        DirectingStyle.PerformanceComedy => "Let performances, reactions and comic timing determine the cuts. Keep physical jokes visible and hold for awkward silences.",
        DirectingStyle.PrestigeDrama => "Emphasize emotion, atmosphere, power relationships and carefully timed reveals. Use tighter framing when the dramatic stakes change.",
        DirectingStyle.KineticAction => "Build urgency through movement, changing viewpoints and rhythmic acceleration. Keep the action's geography understandable.",
        _ => "Let AI adapt the approach to each scene or beat and explain its choices in review. Recommended for most scenes."
    };
    public static string StyleExamples(DirectingStyle style) => style switch {
        DirectingStyle.EnsembleClassical => "Think Star Trek: TNG or Babylon 5.",
        DirectingStyle.PerformanceComedy => "Think Seinfeld or The Big Bang Theory.",
        DirectingStyle.PrestigeDrama => "Think Game of Thrones or The Wheel of Time.",
        DirectingStyle.KineticAction => "Think Alias or 24.", _ => "Auto can blend approaches as the scene develops."
    };
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, AtomicJsonFile.Options))));
    public static string Target(Shot shot) => Hash(new { shot.Title, shot.Description, shot.Duration, shot.SceneId,
        shot.SourceBlockIds, shot.SourceExcerpt, shot.Dialogue, shot.Characters, shot.Atmosphere, shot.Music, shot.Direction, shot.DirectionStrategyId });
    public static DirectionDependencies Dependencies(Shot shot, DirectionStrategy? strategy, IEnumerable<SceneShotSummary>? neighbours = null) => new(
        Hash(new { shot.Description, shot.SourceExcerpt, shot.SceneTitle }),
        Hash(strategy is null ? null : new { strategy.Approach, strategy.DramaticProgression, strategy.VisualProgression, strategy.AudienceInformation, strategy.SpatialAnchors }),
        Hash(shot.Characters.Select(c => c.Name)), Hash(shot.Dialogue.Select(d => new { d.Speaker, d.Language, d.Text })),
        Hash(shot.Duration), Hash((neighbours ?? []).Select(n => new { n.Title, n.Duration, n.Description, n.Dialogue, n.DirectionFingerprint })),
        Hash(new { shot.EnvironmentAssetId, shot.Images, shot.Videos, shot.ContinuityFrame, shot.StartFrame }));
    public static DirectionFreshness Freshness(Shot shot, DirectionStrategy? strategy, IEnumerable<SceneShotSummary>? neighbours = null)
    {
        if (shot.Direction?.Dependencies is not { } before) return new(false, false, false, false, false,
            shot.Direction is null ? [] : ["Direction has no dependency snapshot; review it before using its prescriptions."]);
        var now = Dependencies(shot, strategy, neighbours);
        var story = before.Story == now.Story && before.Strategy == now.Strategy;
        var dialogue = before.Dialogue == now.Dialogue; var cast = before.Cast == now.Cast;
        var adjacent = before.Neighbours == now.Neighbours;
        var intent = story && dialogue;
        var staging = story && cast;
        var editorial = story && dialogue && before.Duration == now.Duration && adjacent;
        var continuity = story && cast && adjacent;
        var support = staging && before.References == now.References;
        List<string> limitations = [];
        if (!intent) limitations.Add("Intent needs review after story, strategy or dialogue changes.");
        if (!staging) limitations.Add("Proposed staging needs review after story, strategy or cast changes.");
        if (!editorial) limitations.Add("Timing and cuts need review after story, strategy, dialogue, duration or neighbour changes.");
        if (!continuity) limitations.Add("Continuity needs review after story, strategy, cast or neighbour changes.");
        if (!support) limitations.Add("Reference support needs review; reference changes do not erase dramatic intent.");
        return new(intent, staging, editorial, continuity, support, limitations);
    }
    public static ShotDirectionContext? Context(Shot shot, DirectionStrategy? strategy, IEnumerable<SceneShotSummary>? neighbours = null)
    {
        if (shot.Direction is not { } direction && strategy is null) return null;
        var freshness = Freshness(shot, strategy, neighbours);
        return new(strategy, freshness.Intent ? shot.Direction?.Intent : null, freshness.Staging ? shot.Direction?.Staging : null,
            freshness.Editorial ? shot.Direction?.Editorial : null,
            freshness.Continuity && shot.Direction is { } d ? d.Advanced : null, freshness.Limitations);
    }
    public static string? Fingerprint(ShotDocument document, Shot shot) => shot.Direction is null && shot.DirectionStrategyId is null ? null
        : Hash(new { shot.Direction, Context = Context(document, shot) });
    public static DirectionStrategy? Strategy(ShotDocument document, Shot shot) => document.DirectionStrategies.FirstOrDefault(s => s.Id == shot.DirectionStrategyId);
    public static IReadOnlyList<SceneShotSummary> Neighbours(ShotDocument document, Shot shot)
    {
        var scene = document.Shots.Where(s => s.SceneId == shot.SceneId).ToList();
        var at = scene.FindIndex(s => s.Id == shot.Id);
        return at < 0 ? [] : scene.Where((_, i) => i == at - 1 || i == at + 1).Select(s => SceneShotSummary.From(s, Strategy(document, s))).ToArray();
    }
    public static void Review(Shot shot, DirectionStrategy? strategy, IEnumerable<SceneShotSummary>? neighbours = null)
    {
        if (shot.Direction is { } d) d.Dependencies = Dependencies(shot, strategy, neighbours);
    }
    public static ShotDirectionContext? Context(ShotDocument document, Shot shot) => Context(shot, Strategy(document, shot), Neighbours(document, shot));
    public static void ValidateStrategies(IEnumerable<DirectionStrategy> strategies)
    {
        var list = strategies.ToList();
        if (list.Any(s => s is null || s.Id == Guid.Empty || s.SceneId == Guid.Empty) || list.DistinctBy(s => s.Id).Count() != list.Count)
            throw new WorkspaceStoreException("Scene direction strategies need distinct identities and a source scene.");
    }
}
