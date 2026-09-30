using System.Text;

namespace lumibelle.Services.AI;

/// <summary>
/// The standard ComfyUI benchmark. TextGenerate reserves its KV cache for the prompt plus max_length up front,
/// so peak VRAM depends on context size, not on how much text the model writes. The benchmark therefore sends a
/// script-sized context and uses the model's own reply-token setting; the model still stops at its natural end.
/// </summary>
public static class ComfyTextBenchmark
{
    /// <summary>Approximate prompt size; the exact count depends on the model's tokenizer.</summary>
    public const int ContextTokens = 8192;
    // Calibrated with ComfyUI's bundled tokenizers: 8,283 Qwen and 7,653 Llama 3 tokens.
    private const int Scenes = 64;

    public static string Prompt(string instruction) => Context + "\n\n" + instruction;

    internal static string Context { get; } = BuildContext();

    private static string BuildContext()
    {
        string[] places = ["KITCHEN", "RAILWAY PLATFORM", "LIGHTHOUSE", "HOSPITAL CORRIDOR", "ROOFTOP GARDEN", "BOOKSHOP", "HARBOUR", "OBSERVATORY", "LAUNDROMAT", "FOREST TRAIL", "MUSEUM ARCHIVE"];
        string[] times = ["DAY", "NIGHT", "DAWN", "DUSK", "LATER"];
        string[] names = ["MARGIT", "TOBIAS", "INES", "OKONKWO", "LIV", "ARVID", "SAGAL"];
        string[] actions =
        [
            "Rain taps against the window while a kettle begins to whistle somewhere out of view.",
            "A stack of unopened letters leans against a chipped blue mug, each envelope stamped with a different city.",
            "Fluorescent lights hum overhead and one of them flickers every few seconds, casting a nervous rhythm across the floor.",
            "The wind pushes a paper cup along the concrete until it settles against the base of a bench.",
            "Somebody has left a radio playing an old dance tune at low volume, the melody drifting in and out of static.",
            "Dust hangs in a narrow beam of light, and the smell of machine oil lingers from an earlier repair.",
            "A cat watches from the top of a filing cabinet, tail flicking whenever anyone raises their voice.",
            "Footsteps echo from the stairwell, then stop abruptly as if the person has changed their mind.",
            "Condensation runs down the glass, blurring the neon sign across the street into soft red streaks.",
            "An unfinished crossword lies on the table, three answers written in ink and then crossed out.",
            "Boats knock gently against the pier, their ropes creaking in the slow swell of the evening tide.",
            "Somewhere a clock chimes the quarter hour, a little late, as it has done for years."
        ];
        string[] lines =
        [
            "You said you would call before you came. I waited until the last train had gone.",
            "It isn't about the money. It never was. I just wanted someone to notice.",
            "Look at the map again. If we follow the river, we reach the old mill before dark.",
            "I kept the key. I don't know why. Maybe I thought you would come back for it.",
            "Nobody here remembers what the building was before the fire, but I do.",
            "If you're going to lie to me, at least make it a better story than that.",
            "We have until morning. After that, the whole town will know what we found.",
            "She told me to wait here and not to open the door for anyone. Not even you.",
            "The numbers don't add up. Someone moved the shipment twice before it arrived.",
            "I'm not angry. I'm tired. There's a difference, and you used to know it."
        ];
        var text = new StringBuilder("The following draft screenplay is background material. Read it, but do not summarize or continue it.\n\n");
        for (var scene = 1; scene <= Scenes; scene++)
        {
            text.Append(scene).Append(". ").Append(scene % 3 == 0 ? "EXT. " : "INT. ").Append(places[scene % places.Length])
                .Append(" – ").Append(times[scene % times.Length]).Append("\n\n");
            text.Append(actions[scene * 5 % actions.Length]).Append(' ').Append(actions[(scene * 7 + 3) % actions.Length]).Append("\n\n");
            for (var turn = 0; turn < 3; turn++)
                text.Append(names[(scene + turn * 2) % names.Length]).Append('\n').Append(lines[(scene * 3 + turn * 4) % lines.Length]).Append("\n\n");
        }
        return text.ToString().TrimEnd();
    }
}
