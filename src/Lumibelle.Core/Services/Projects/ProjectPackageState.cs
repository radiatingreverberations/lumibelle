using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

internal sealed class ProjectPackageState
{
    internal required ProjectInfo Project;
    internal required AssetLibrary Assets;
    internal required ShotDocument Shots;
    internal required ProductionDocument Production;
    internal required CutDocument Cut;
    internal AssistantHistory? History;
    internal Dictionary<string, byte[]> Other { get; } = new(StringComparer.Ordinal);

    internal static async Task<ProjectPackageState> ReadAsync(string root, Guid project, CancellationToken ct)
    {
        var info = await ProjectPackageFormat.ReadAsync<ProjectInfo>(root, "project.json", ct)
            ?? throw new WorkspaceStoreException("Project manifest missing.");
        if (info.Id != project || project == Guid.Empty || info.SchemaVersion != ProjectInfo.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(info.Name) || info.CreatedUtc == default || info.CreatedUtc.Offset != TimeSpan.Zero || info.VideoAspect is not ("16:9" or "9:16" or "1:1"))
            throw new WorkspaceStoreException("Invalid project manifest.");
        var result = new ProjectPackageState {
            Project = info,
            Assets = await ProjectPackageFormat.ReadAsync<AssetLibrary>(root, "assets.json", ct) ?? new() { ProjectId = project },
            Shots = await ProjectPackageFormat.ReadAsync<ShotDocument>(root, "shots.json", ct) ?? new() { ProjectId = project },
            Production = await ProjectPackageFormat.ReadAsync<ProductionDocument>(root, "production.json", ct) ?? new() { ProjectId = project },
            Cut = await ProjectPackageFormat.ReadAsync<CutDocument>(root, "cut.json", ct) ?? new() { ProjectId = project }
        };
        if (result.Shots.SchemaVersion == 1)
        {
            foreach (var shot in result.Shots.Shots.Concat(result.Shots.Recovery.SelectMany(r => r.Shots)).Concat(result.Shots.Trash.Where(t => t.Owner is not null).Select(t => t.Owner!)))
                shot.AspectOverride = shot.Aspect == "16:9" ? null : shot.Aspect;
            result.Shots.SchemaVersion = 2;
        }
        result.Validate();
        var script = await ProjectPackageFormat.ReadAsync<ScriptDocument>(root, "script.json", ct);
        if (script is not null) { FileScriptStore.Validate(script, project); result.Other.Add("script.json", ProjectPackageFormat.Json(script)); }
        var history = await ProjectPackageFormat.ReadAsync<AssistantHistory>(root, "script-assistant.json", ct);
        if (history is not null)
        {
            if (history.ProjectId != project || history.SchemaVersion != 1 || history.Runs is null) throw new WorkspaceStoreException("Invalid saved Script discussion history.");
            result.History = history;
        }
        var preferences = await ProjectPackageFormat.ReadAsync<ProjectAiPreferences>(root, "ai-preferences.json", ct);
        if (preferences is not null)
        {
            if (preferences.ProjectId != project || preferences.SchemaVersion is not (1 or 2)) throw new WorkspaceStoreException("Invalid project AI preferences.");
            preferences = preferences with { LoraVisibility = new() };
            result.Other.Add("ai-preferences.json", ProjectPackageFormat.Json(preferences));
        }
        long otherBytes = result.Other.Values.Sum(b => (long)b.Length);
        foreach (var folder in ProjectPackageFormat.ScriptFolders)
        {
            var dir = ProjectPackageFormat.Under(root, folder);
            if (!Directory.Exists(dir)) continue;
            ProjectPackageFormat.NoLinks(root, dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file); var relative = folder + "/" + name;
                if (!ProjectPackageFormat.Allowed(relative)) throw new WorkspaceStoreException("Invalid captured script filename.");
                var id = Guid.ParseExact(name[..^5], "D"); byte[] bytes;
                if (folder == "script-history") {
                    var r = (await ProjectPackageFormat.ReadAsync<ScriptRecovery>(root, relative, ct))!;
                    if (r.Id != id) throw new WorkspaceStoreException("Invalid script recovery identity.");
                    FileScriptStore.Validate(r.Document, project); bytes = ProjectPackageFormat.Json(r);
                } else if (folder == "script-approved") {
                    var r = (await ProjectPackageFormat.ReadAsync<ApprovedScriptSnapshot>(root, relative, ct))!;
                    if (r.Id != id || r.ProjectId != project || r.SourceRevision < 0) throw new WorkspaceStoreException("Invalid approved script identity.");
                    ScriptStructure.ValidateBlocks(r.Blocks); bytes = ProjectPackageFormat.Json(r);
                } else {
                    var r = (await ProjectPackageFormat.ReadAsync<ScriptSourceSnapshot>(root, relative, ct))!;
                    if (r.Id != id || r.ProjectId != project || r.SourceRevision < 0) throw new WorkspaceStoreException("Invalid script source identity.");
                    ScriptStructure.ValidateBlocks(r.Blocks); bytes = ProjectPackageFormat.Json(r);
                }
                result.Other.Add(relative, bytes);
                otherBytes = checked(otherBytes + bytes.Length);
                if (result.Other.Count > ProjectPackageFormat.MaxFiles || otherBytes > ProjectPackageFormat.MaxJsonTotalBytes)
                    throw new WorkspaceStoreException("The project has too much metadata for this package version.");
            }
        }
        return result;
    }
    internal void Validate()
    {
        FileAssetStore.Validate(Assets, Project.Id); FileShotStore.Validate(Shots, Project.Id); FileCutStore.Validate(Cut, Project.Id);
        var p = Production;
        if (p.ProjectId != Project.Id || p.SchemaVersion is not (2 or 3) || p.Revision < 0 || p.Compositions is null || p.ShotContent is null || p.EarlierSetupContent is null ||
            p.Compositions.Any(c => c is null || c.Id == Guid.Empty || c.ShotId == Guid.Empty || c.Version < 1 || c.TakeCount is < 1 or > 4 || c.Seed < 0 || c.Inputs is null) ||
            p.Compositions.Select(c => c.Id).Distinct().Count() != p.Compositions.Count)
            throw new WorkspaceStoreException("Unsupported production metadata. Open this project in Shots before exporting.");
        // Exercise the existing production validator as well during staged import (see ValidateStoresAsync).
    }
    internal void FlattenSetups(GenerationSetupLibrary setups)
    {
        foreach (var c in Production.Compositions)
        {
            if (c.GenerationSetupId is { } id)
            {
                var setup = setups.Setups.SingleOrDefault(s => s.Id == id)
                    ?? throw new WorkspaceStoreException("A referenced global generation setup is missing; export has not guessed its settings.");
                setup.Settings.Apply(c); c.Name = setup.Name; c.Archived |= setup.Archived;
            }
            // The destination will import these effective values as local global setups on opening Shots.
            // Never carry a live link to a potentially unrelated destination setup.
            c.GenerationSetupId = null; c.GenerationSetupVersion = 0; c.ReviewJobId = null;
        }
        foreach (var content in Production.ShotContent) content.ReviewJobId = null;
    }
    internal void Clean(ProjectPackagePrivacy privacy)
    {
        Assets = privacy.Clean(Assets); Shots = privacy.Clean(Shots); Production = privacy.Clean(Production);
        if (History is not null) History = ProjectPackageHistory.Portable(History);
        foreach (var draft in Assets.ReelDrafts) draft.PendingJobId = null;
    }
    internal Dictionary<string, byte[]> Documents()
    {
        var docs = new Dictionary<string, byte[]>(Other, StringComparer.Ordinal) {
            ["project.json"] = ProjectPackageFormat.Json(Project), ["assets.json"] = ProjectPackageFormat.Json(Assets),
            ["shots.json"] = ProjectPackageFormat.Json(Shots), ["production.json"] = ProjectPackageFormat.Json(Production),
            ["cut.json"] = ProjectPackageFormat.Json(Cut)
        };
        if (History is not null) docs.Add("script-assistant.json", ProjectPackageFormat.Json(History));
        if (docs.Values.Any(b => b.Length > ProjectPackageFormat.MaxJsonBytes) || docs.Values.Sum(b => (long)b.Length) > ProjectPackageFormat.MaxJsonTotalBytes)
            throw new WorkspaceStoreException("The project exceeds the portable metadata limit.");
        return docs;
    }
    internal async Task WriteAsync(string root, CancellationToken ct)
    {
        foreach (var (name, bytes) in Documents()) {
            var path = ProjectPackageFormat.Under(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
    }
    internal void RenewTrash(DateTimeOffset now)
    {
        Assets = Assets with {
            Trash = Assets.Trash.Select(t => t with { DeletedUtc = now, ExpiresUtc = now.AddDays(30) }).ToList(),
            VoiceTrash = Assets.VoiceTrash.Select(t => t with { DeletedUtc = now, ExpiresUtc = now.AddDays(30) }).ToList(),
            ReelTrash = Assets.ReelTrash.Select(t => t.ExpiresUtc is null ? t : t with { DeletedUtc = now, ExpiresUtc = now.AddDays(30) }).ToList()
        };
        foreach (var t in Shots.Trash) { t.DeletedUtc = now; t.ExpiresUtc = now.AddDays(30); }
    }
    internal static async Task ValidateStoresAsync(string root, ProjectInfo info, CancellationToken ct)
    {
        // Read the unpublished directory through the real stores without Initialize, workers,
        // global setup hydration, media decoders, or provider calls.
        var store = new StagedProjectStore(info); var paths = new ApplicationPaths(Path.GetDirectoryName(root)!, Path.GetDirectoryName(root)!);
        // ProjectFiles expects a directory named after the ID. The caller provides that layout.
        var files = new ProjectFiles(paths, store); var shots = new FileShotStore(files, TimeProvider.System);
        var assets = new FileAssetStore(files, TimeProvider.System);
        await shots.LoadAsync(info.Id, ct); await assets.LoadAsync(info.Id, ct);
        await new FileScriptStore(files, TimeProvider.System).LoadAsync(info.Id, ct);
        await new FileAssistantHistoryStore(files, new ApplicationSession()).LoadAsync(info.Id, ct);
        await new FileProjectAiPreferencesStore(files).LoadAsync(info.Id, ct);
        await new FileCutStore(files, shots, TimeProvider.System).LoadAsync(info.Id, ct);
        await new FileProductionStore(files, shots, assets, store, TimeProvider.System, null!).LoadAsync(info.Id, ct);
    }
    private sealed class StagedProjectStore(ProjectInfo project) : IProjectStore
    {
        public Task<ProjectInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ProjectInfo?>(id == project.Id ? project : null);
        public Task<ProjectLibrary> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProjectLibrary([project], []));
        public Task<ProjectInfo> CreateAsync(CreateProjectRequest r, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectInfo> UpdateAsync(ProjectInfo original, UpdateProjectRequest r, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
