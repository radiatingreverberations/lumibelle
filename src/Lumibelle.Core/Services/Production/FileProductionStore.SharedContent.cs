using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed partial class FileProductionStore
{
    private static void HydrateSharedContent(ProductionDocument d)
    {
        if (d.SchemaVersion == 2)
        {
            d.ShotContent = [];
            d.EarlierSetupContent = [];
            foreach (var group in d.Compositions.GroupBy(c => c.ShotId))
            {
                // Prefer a populated, non-archived default over an empty alternative.
                var selected = group.OrderBy(c => c.Archived).ThenBy(c => string.IsNullOrWhiteSpace(c.Prompt))
                    .ThenBy(c => c.Name != "Default setup").First();
                var shared = ShotProductionContent.From(selected);
                d.ShotContent.Add(shared);
                foreach (var previous in group.Where(c => c.Id != selected.Id))
                {
                    var content = ShotProductionContent.From(previous);
                    if (ReferenceSetups.Hash(content) != ReferenceSetups.Hash(shared))
                        d.EarlierSetupContent.Add(new(previous.Id, previous.Name, content));
                }
            }
            d.SchemaVersion = 3;
        }
        if (d.ShotContent is null || d.EarlierSetupContent is null ||
            d.ShotContent.Any(Invalid) || d.ShotContent.Select(s => s.ShotId).Distinct().Count() != d.ShotContent.Count ||
            d.EarlierSetupContent.Any(s => s is null || Invalid(s.Content)) ||
            d.Compositions.Any(c => !d.ShotContent.Any(s => s.ShotId == c.ShotId)))
            throw new WorkspaceStoreException("The shot prompts or references are invalid. They have not been replaced.");
        foreach (var c in d.Compositions) d.ShotContent.Single(s => s.ShotId == c.ShotId).Apply(c);
    }

    private static bool Invalid(ShotProductionContent? s) => s is null || s.ShotId == Guid.Empty ||
        s.Prompt is null || s.DirectingNotes is null || s.RevisionNotes is null || s.ReferenceUsage is null ||
        s.Images is null || s.Voices is null || s.Videos is null || s.AspectOverride is not (null or "16:9" or "9:16" or "1:1") ||
        s.Images.Any(i => i is null) || s.Voices.Any(v => v is null) || s.Videos.Any(v => v is null) ||
        s.History is null || s.History.Any(h => h is null) || s.History.Select(h => h.Id).Distinct().Count() != s.History.Count ||
        s.AcceptedRevisionId is { } accepted && !s.History.Any(h => h.Id == accepted);

    private static async Task Publish(string dir, ProductionDocument d, CancellationToken ct, ProductionComposition? edited = null)
    {
        foreach (var group in d.Compositions.GroupBy(c => c.ShotId))
            if (!d.ShotContent.Any(s => s.ShotId == group.Key)) d.ShotContent.Add(ShotProductionContent.From(group.First()));
        if (edited is not null)
        {
            var prior = d.ShotContent.Single(s => s.ShotId == edited.ShotId);
            var next = ShotProductionContent.From(edited);
            if (ReferenceSetups.Hash(prior) != ReferenceSetups.Hash(next))
            {
                d.ShotContent[d.ShotContent.IndexOf(prior)] = next;
                // A stale editor on any setup must not overwrite newer shot content.
                foreach (var sibling in d.Compositions.Where(c => c.ShotId == edited.ShotId && c.Id != edited.Id)) sibling.Version++;
            }
        }
        HydrateSharedContent(d);
        var path = Path.Combine(dir, "production.json");
        var existing = await AtomicJsonFile.ReadAsync<ProductionDocument>(path, ct);
        if (existing?.SchemaVersion == 2)
        {
            var backup = Path.Combine(dir, "production-before-shared-inputs.json");
            if (!File.Exists(backup)) {
                var temporary = backup + ".tmp";
                File.Copy(path, temporary, overwrite: true);
                DurableFile.Flush(temporary);
                File.Move(temporary, backup);
            }
        }
        if (existing?.Compositions.Any(c => c.GenerationSetupId is null) == true && d.Compositions.Any(c => c.GenerationSetupId is not null))
        {
            var backup = Path.Combine(dir, "production-before-global-setups.json");
            if (!File.Exists(backup)) {
                var temporary = backup + ".tmp";
                File.Copy(path, temporary, overwrite: true);
                DurableFile.Flush(temporary);
                File.Move(temporary, backup);
            }
        }
        d.Revision++;
        // Composition is a runtime adapter. Persist only the settings it owns.
        await AtomicJsonFile.WriteAsync(path, new {
            d.SchemaVersion, d.ProjectId, d.Revision, d.ShotContent, d.EarlierSetupContent,
            Compositions = d.Compositions.Select(c => new {
                c.Id, c.ShotId, c.Name, c.GenerationSetupId, c.GenerationSetupVersion, c.Seed, c.TakeCount, c.OutputOverrides, c.Archived, c.Version, c.SourceFingerprint,
                Inputs = new {
                    c.Shot.Aspect, c.Shot.NativeResolution, c.Shot.Resolution,
                    c.Shot.UpscalePreview, c.Shot.GenerationPreset, c.Shot.SaveLosslessFrames, c.Shot.SaveLatents,
                    c.Shot.Turbo, c.Shot.TurboSteps, c.Shot.Loras
                }
            }).ToArray()
        }, ct);
    }
}
