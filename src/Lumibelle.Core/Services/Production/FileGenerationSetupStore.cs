using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public interface IGenerationSetupStore
{
    Task<GenerationSetupLibrary> LoadAsync(CancellationToken ct = default);
    Task<GenerationSetupLibrary> ImportAsync(ProductionDocument document, CancellationToken ct = default);
    Task<GenerationSetupLibrary> SaveAsync(GenerationSetup setup, long expectedVersion, CancellationToken ct = default);
    Task<GenerationSetupLibrary> SelectAsync(Guid id, CancellationToken ct = default);
}

public sealed class FileGenerationSetupStore(ApplicationPaths paths) : IGenerationSetupStore
{
    private readonly string _path = Path.Combine(paths.Data, "generation-setups.json");
    public async Task<GenerationSetupLibrary> LoadAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(paths.Data)) return new();
        var d = await AtomicJsonFile.ReadAsync<GenerationSetupLibrary>(_path, ct) ?? new();
        if (d.SchemaVersion != 1 || d.Setups is null || d.Imports is null ||
            d.Setups.Any(s => s is null || s.Id == Guid.Empty || s.Version < 1 || string.IsNullOrWhiteSpace(s.Name) || s.Settings is null ||
                s.Settings.TakeCount is < 1 or > 4 || s.Settings.Seed < 0) ||
            d.Setups.Select(s => s.Id).Distinct().Count() != d.Setups.Count ||
            d.Imports.Any(i => i is null || i.SourceName is null || !d.Setups.Any(s => s.Id == i.SetupId)) ||
            d.Imports.Select(i => (i.ProjectId, i.CompositionId)).Distinct().Count() != d.Imports.Count)
            throw new WorkspaceStoreException("The global generation setup library is invalid. It has not been replaced.");
        return d;
    }

    public async Task<GenerationSetupLibrary> ImportAsync(ProductionDocument document, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var d = await LoadAsync(ct); var changed = false;
        foreach (var c in document.Compositions.Where(c => c.GenerationSetupId is null || d.Setups.All(s => s.Id != c.GenerationSetupId)))
        {
            // A missing foreign preset must use its captured settings, even if this
            // library remembers an earlier import of the same project/composition.
            if (c.GenerationSetupId is null && d.Imports.Any(i => i.ProjectId == document.ProjectId && i.CompositionId == c.Id)) continue;
            var name = c.Name.Trim();
            var settings = GenerationSettings.From(c);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || settings.TakeCount is < 1 or > 4 || settings.Seed < 0)
                throw new WorkspaceStoreException("The project's captured generation setup is invalid. No presets were imported.");
            var setup = d.Setups.FirstOrDefault(s => (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                d.Imports.Any(i => i.SetupId == s.Id && i.SourceName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))) &&
                SameSettings(s.Settings, settings) && s.Archived == c.Archived);
            if (setup is null) {
                setup = new() { Name = UniqueName(d, name), Settings = settings, Archived = c.Archived, Version = 1 };
                d.Setups.Add(setup);
            }
            d.Imports.RemoveAll(i => i.ProjectId == document.ProjectId && i.CompositionId == c.Id);
            d.Imports.Add(new(document.ProjectId, c.Id, setup.Id, c.Name)); changed = true;
        }
        if (changed) await AtomicJsonFile.WriteAsync(_path, d, ct);
        return d;
    }

    public async Task<GenerationSetupLibrary> SaveAsync(GenerationSetup setup, long expectedVersion, CancellationToken ct = default)
    {
        var s = ShotCopy.Of(setup); s.Name = s.Name.Trim();
        if (s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 200 ||
            s.Settings is null || s.Settings.TakeCount is < 1 or > 4 || s.Settings.Seed < 0)
            throw new WorkspaceStoreException("Give the global setup a name and valid generation settings.");
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var d = await LoadAsync(ct); var old = d.Setups.SingleOrDefault(x => x.Id == s.Id);
        if ((old?.Version ?? 0) != expectedVersion) throw new WorkspaceConflictException();
        if (d.Setups.Any(x => x.Id != s.Id && x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)))
            throw new WorkspaceStoreException("A global setup already has that name. Choose another name.");
        s.Version = expectedVersion + 1;
        if (old is null) d.Setups.Add(s); else d.Setups[d.Setups.IndexOf(old)] = s;
        if (s.Archived && d.SelectedId == s.Id) d.SelectedId = d.Setups.FirstOrDefault(x => !x.Archived)?.Id;
        await AtomicJsonFile.WriteAsync(_path, d, ct); return d;
    }

    public async Task<GenerationSetupLibrary> SelectAsync(Guid id, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var d = await LoadAsync(ct);
        if (!d.Setups.Any(s => s.Id == id && !s.Archived)) throw new WorkspaceStoreException("Choose an active global setup.");
        if (d.SelectedId != id) { d.SelectedId = id; await AtomicJsonFile.WriteAsync(_path, d, ct); }
        return d;
    }
    public static bool SameSettings(GenerationSettings a, GenerationSettings b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    public static string UniqueName(GenerationSetupLibrary d, string name)
    {
        var candidate = name; var suffix = 2;
        while (d.Setups.Any(s => s.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{name[..Math.Min(name.Length, 180)]} ({suffix++})";
        return candidate;
    }
}
