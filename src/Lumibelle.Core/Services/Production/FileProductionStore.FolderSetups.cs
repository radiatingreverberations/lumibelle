using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed partial class FileProductionStore
{
    // Called under the project lock before a folder is added to this library.
    // Existing local presets remain authoritative; foreign links use the settings
    // retained with each composition, without changing shot content or captured takes.
    internal static async Task ReconcileFolderSetupsAsync(string directory, Guid project, IGenerationSetupStore setups, CancellationToken ct)
    {
        var document = await AtomicJsonFile.ReadAsync<ProductionDocument>(Path.Combine(directory, "production.json"), ct);
        if (document is null || document.SchemaVersion == 1) return;
        ValidateDocument(document, project);
        var library = await setups.LoadAsync(ct);
        var missing = document.Compositions.Where(c => c.GenerationSetupId is { } id && library.Setups.All(s => s.Id != id)).ToArray();
        if (missing.Length == 0) return;
        library = await setups.ImportAsync(document, ct);
        foreach (var composition in missing)
        {
            var imported = library.Imports.Single(i => i.ProjectId == project && i.CompositionId == composition.Id);
            library.Setups.Single(s => s.Id == imported.SetupId).Apply(composition);
            composition.Version++;
        }
        await Publish(directory, document, ct);
    }
}
