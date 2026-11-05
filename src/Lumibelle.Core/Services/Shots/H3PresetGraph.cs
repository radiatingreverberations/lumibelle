using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

// What a generation preset adds to an H3 graph, shared by the video and image workflows.
// The settings' Model must already be the file the preset runs on: see H3Presets.Model.
internal static class H3PresetGraph
{
    private static object Link(string id, int port = 0) => new object[] { id, port };

    public static string Adapters(string key, H3Settings settings, H3Sampling sampling, string model, Action<string, string, object> node)
    {
        if (key == "turbo4") { node("16", "LoraLoaderModelOnly", new { model = Link("1"), lora_name = sampling.Lora, strength_model = sampling.LoraStrength }); model = "16"; }
        if (key == H3SmdRecipe.Key) {
            if (!H3SmdRecipe.IsBase(settings.Model)) throw new WorkspaceStoreException("Capture SMD's selected pruned INT8 base before submission.");
            model = H3SmdRecipe.Apply(model, settings, node);
        }
        if (key == H3PikaFlexRecipe.Key) {
            if (!H3SmdRecipe.IsBase(settings.Model)) throw new WorkspaceStoreException("Capture Pika H3 Flex's selected pruned INT8 base before submission.");
            model = H3PikaFlexRecipe.Apply(model, settings, node);
        }
        if (key == H3PtdRecipe.Key) {
            if (!H3SmdRecipe.IsBase(settings.Model)) throw new WorkspaceStoreException("Capture PTD's selected pruned INT8 base before submission.");
            model = H3PtdRecipe.Apply(model, settings, node);
        }
        if (key is "larry" or "pdd")
        {
            if (key == "pdd") { node("50", "MiniMaxH3SigmaShift", new { model = Link(model), shift_video = 12.0, shift_audio = 3.0 }); model = "50"; }
            var presetInputs = H3Presets.Inputs(key, settings); presetInputs["model"] = Link(model);
            node("51", H3Presets.Node(key), presetInputs); model = "51";
        }
        return model;
    }

    public static void Sampler(string key, string samplerName, string model, Action<string, string, object> node)
    {
        if (key == "larry") node("8", "MiniMaxH3TurboSampler", new { });
        else if (key == H3PtdRecipe.Key) node("8", H3PtdRecipe.Sampler, new { model = Link(model) });
        else node("8", "KSamplerSelect", new { sampler_name = samplerName });
    }

    public static void Schedule(string key, H3Sampling sampling, string model, string scheduleModel, Action<string, string, object> node)
    {
        if (key == H3SmdRecipe.Key) node("9", "ManualSigmas", new { sigmas = H3SmdRecipe.Sigmas });
        else if (key == H3PikaFlexRecipe.Key) node("9", "ManualSigmas", new { sigmas = H3PikaFlexRecipe.Sigmas });
        else if (key == H3PtdRecipe.Key) node("9", H3PtdRecipe.Sigmas, new { model = Link(model) });
        else if (key != "pdd") node("9", "BasicScheduler", new { model = Link(scheduleModel), scheduler = sampling.Scheduler, steps = sampling.Steps, denoise = 1.0 });
    }

    public static object Sigmas(string key) => key == "pdd" ? Link("51", 1) : Link("9");
}
