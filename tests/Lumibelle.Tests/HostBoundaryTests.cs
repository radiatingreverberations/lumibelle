using System.Reflection;
using System.Text;
using lumibelle;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumibelle.Tests;

public sealed class HostBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Lumibelle.HostTests", Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CoreAndUiHaveNoHostOrServerAssemblyDependencies()
    {
        var core = typeof(ScriptDocument).Assembly; var ui = typeof(UiAssets).Assembly;
        Assert.Equal("Lumibelle.Core", core.GetName().Name);
        Assert.Equal("Lumibelle.UI", ui.GetName().Name);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), a => a.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || a.Name.StartsWith("Microsoft.Maui", StringComparison.Ordinal) || a.Name is "Lumibelle.UI" or "Lumibelle.Web" or "Lumibelle");
        Assert.DoesNotContain(ui.GetReferencedAssemblies(), a => a.Name!.StartsWith("Microsoft.Maui", StringComparison.Ordinal) || a.Name is "Lumibelle.Web" or "Lumibelle" || a.Name.StartsWith("Microsoft.AspNetCore.Http", StringComparison.Ordinal) || a.Name.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedProfileAndCompanionIdentifiersRemainStable()
    {
        var resources = typeof(ScriptDocument).Assembly.GetManifestResourceNames();
        Assert.Contains(resources, r => r.StartsWith("lumibelle.Services.AI.PromptProfiles.", StringComparison.Ordinal));
        Assert.Contains("lumibelle.comfy_nodes.lumibelle_h3.__init__.py", resources);
        Assert.DoesNotContain(resources, r => r.StartsWith("Lumibelle.Core.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothDataAndProjectRootsAreExclusivelyOwnedAndReleased()
    {
        var paths = new ApplicationPaths(Path.Combine(root, "data"), Path.Combine(root, "library"));
        using var first = new WorkspaceOwnership(paths); await first.StartAsync(Ct);
        using var sameData = new WorkspaceOwnership(new(paths.Data, Path.Combine(root, "another-library")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => sameData.StartAsync(Ct));
        using var sameLibrary = new WorkspaceOwnership(new(Path.Combine(root, "another-data"), paths.Projects));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => sameLibrary.StartAsync(Ct));
        await first.StopAsync(Ct); await sameLibrary.StartAsync(Ct);
        Assert.True(Path.IsPathFullyQualified(paths.Settings)); Assert.True(Path.IsPathFullyQualified(paths.Temporary));
    }

    [Fact]
    public async Task SharedRegistrationsSaveAndReloadUnchangedScriptOutsideContentRoot()
    {
        var paths = new ApplicationPaths(Path.Combine(root, "data"), Path.Combine(root, "library"));
        ServiceProvider Provider()
        {
            var services = new ServiceCollection(); services.AddLogging();
            services.AddSingleton<ISecretProtector>(new WebSecretProtector(new EphemeralDataProtectionProvider()));
            services.AddLumibelleCore(paths); return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }
        Guid id; string file; byte[] before;
        await using (var provider = Provider())
        {
            var project = await provider.GetRequiredService<IProjectStore>().CreateAsync(new("Shared host fixture", "Disposable"), Ct); id = project.Id;
            var store = provider.GetRequiredService<IScriptStore>(); var doc = await store.LoadAsync(id, Ct);
            await store.SaveAsync(doc with { Blocks = ScriptFixtures.Document(id).Blocks }, doc.Revision, cancellationToken: Ct);
            file = Path.Combine(paths.Projects, id.ToString("D"), "script.json");
            before = await File.ReadAllBytesAsync(file, Ct);
        }
        await using (var provider = Provider())
        {
            var saved = await provider.GetRequiredService<IScriptStore>().LoadAsync(id, Ct);
            Assert.Equal("You called?", saved.Blocks[^1].Text);
            Assert.Equal(before, await File.ReadAllBytesAsync(file, Ct));
            Assert.Same(provider.GetRequiredService<IAssetStore>(), provider.GetRequiredService<IImageTrashStore>());
        }
    }

    [Fact]
    public void HistoricalCredentialsKeepTheirPurposeAndUnreadableKeysFail()
    {
        var provider = new EphemeralDataProtectionProvider();
        var legacy = provider.CreateProtector("Lumibelle.AiCredentials.v1").Protect("fixture-key");
        Assert.Equal("fixture-key", new WebSecretProtector(provider).Unprotect(legacy));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new WebSecretProtector(new EphemeralDataProtectionProvider()).Unprotect(legacy));
    }

    [Fact]
    public async Task CloseFlushesCurrentRegisteredEditorsAndFailureStopsShutdown()
    {
        var registry = new EditSessionRegistry(); var calls = new List<string>(); var succeeds = false;
        using var one = registry.Register(() => { calls.Add("script"); return Task.FromResult(succeeds); });
        using var two = registry.Register(() => { calls.Add("prompt"); return Task.FromResult(true); });
        Assert.False(await registry.SaveAllAsync()); Assert.Equal(["script"], calls);
        succeeds = true; calls.Clear(); Assert.True(await registry.SaveAllAsync()); Assert.Equal(["script", "prompt"], calls);
        one.Dispose(); calls.Clear(); Assert.True(await registry.SaveAllAsync()); Assert.Equal(["prompt"], calls);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
