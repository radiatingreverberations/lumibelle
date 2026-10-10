using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class H3RequirementTests
{
    private static H3Configuration Catalog(H3Settings s, params string[] nodes) => new(true, true, "Checked", [], [], [], [])
    {
        DiscoverySucceeded = true, Nodes = nodes.ToHashSet(),
        InstalledModels = [s.Model, "custom/renamed-ref2va.safetensors"], InstalledEncoders = [s.Encoder], InstalledVaes = [s.VideoVae],
        InstalledLoras = [s.TurboLora], LatentUpscalers = [], Presets = [new("pdd", null, [H3Presets.PddCheckpoint])]
    };

    [Fact]
    public void StatusSaysWhetherTheChosenFileIsInstalledAndSuggested()
    {
        var s = new H3Settings();
        Assert.Equal(H3RequirementState.NotChecked, H3Requirements.Status(H3Requirements.Model, s, null).State);
        var catalog = Catalog(s, H3Requirements.LarryNodes.Node!);
        Assert.Equal(H3RequirementState.Installed, H3Requirements.Status(H3Requirements.Model, s, catalog).State);
        Assert.Equal(H3RequirementState.Missing, H3Requirements.Status(H3Requirements.AudioVae, s, catalog).State);
        Assert.Equal(H3RequirementState.Installed, H3Requirements.Status(H3Requirements.PddFile, s, catalog).State);
        Assert.Equal(H3RequirementState.Installed, H3Requirements.Status(H3Requirements.LarryNodes, s, catalog).State);
        Assert.Equal(H3RequirementState.Missing, H3Requirements.Status(H3Requirements.PddNodes, s, catalog).State);
        // A renamed or community file is allowed, but it isn't one Lumibelle suggests.
        H3Requirements.Select(H3Requirements.Model, s, "custom/renamed-ref2va.safetensors");
        Assert.Equal("custom/renamed-ref2va.safetensors", s.Model);
        Assert.Equal(H3RequirementState.OtherFile, H3Requirements.Status(H3Requirements.Model, s, catalog).State);
        // Subfolders don't stop a suggested file from being recognized.
        Assert.True(H3Requirements.Suggested(H3Requirements.TurboLora, "h3/" + s.TurboLora));
    }

    [Fact]
    public void EveryFileCanBeChosenAndEveryNodePackCanBeDetected()
    {
        foreach (var requirement in H3Requirements.All)
        {
            Assert.NotEmpty(requirement.Downloads);
            Assert.All(requirement.Downloads, d => Assert.StartsWith("https://", d.Url));
            if (requirement.Kind == H3RequirementKind.NodePack) { Assert.NotNull(requirement.Node); Assert.Equal("custom_nodes", requirement.Folder); continue; }
            var settings = new H3Settings();
            H3Requirements.Select(requirement, settings, "chosen.safetensors");
            Assert.Equal("chosen.safetensors", H3Requirements.Selected(requirement, settings));
        }
        Assert.All(H3Presets.Keys, key => Assert.All(H3Requirements.ForPreset(key), r => Assert.Contains(r, H3Requirements.All)));
        Assert.Equal(H3Requirements.All.Count, H3Requirements.All.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void ModelsSayWhichCommonCardHoldsThemFully()
    {
        int? Fit(string file) => H3Requirements.GpuFit(H3Requirements.Model, H3Requirements.Model.Downloads.Single(d => d.Name == file));
        Assert.Equal(24, Fit("minimax_h3_ref2va_pruned_int8_convrot.safetensors"));
        Assert.Equal(20, Fit("minimax_h3_ref2va_pruned_w6a8.safetensors"));
        Assert.Equal(20, Fit("minimax_h3_hybrid_fl2va_ref2va_b25-49_w6a8.safetensors"));
        Assert.Equal(16, Fit("minimax_h3_ref2va_pruned_w4a8_mixed.safetensors"));
        Assert.Contains("holds fully on a 24 GB card", H3Requirements.SizeHint(H3Requirements.Model, H3Requirements.Model.Downloads[0]));
        // The encoder and VAEs load at other times, so only models get a card size.
        Assert.Equal("Download size.", H3Requirements.SizeHint(H3Requirements.Encoder, H3Requirements.Encoder.Downloads[0]));
    }

    [Fact]
    public void TheManualListsEverySupportedFileAndNodePack()
    {
        var manual = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/manual-comfyui.md"));
        foreach (var download in H3Requirements.All.SelectMany(r => r.Downloads.Take(1)))
        {
            Assert.Contains(download.Name, manual);
            Assert.Contains(download.Url, manual);
        }
    }
}
