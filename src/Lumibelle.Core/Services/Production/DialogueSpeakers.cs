namespace lumibelle.Services.Production;

// Screenplay character cues carry extensions that are not part of the name: JAN (V.O.),
// JAN (O.S.) and JAN (CONT'D) are all Jan. Use these only to decide which character a
// speaker is; voice choices keep the speaker exactly as the dialogue writes it.
public static class DialogueSpeakers
{
    public static string Name(string? speaker)
    {
        var name = (speaker ?? "").Trim();
        while (name.EndsWith(')') && name.LastIndexOf('(') is > 0 and var open) name = name[..open].TrimEnd();
        return name;
    }

    public static HashSet<string> Words(string? name) =>
        Name(name).Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The same name, or every word of the speaker's name in the character's: JAN is Jan Sherwood.
    public static bool Names(string? speaker, string? character)
    {
        var name = Name(speaker);
        if (name.Length == 0) return false;
        if (name.Equals(Name(character), StringComparison.OrdinalIgnoreCase)) return true;
        var words = Words(name);
        return words.Count > 0 && words.IsSubsetOf(Words(character));
    }
}
