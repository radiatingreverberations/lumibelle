namespace lumibelle.Components.Layout;

/// <summary>The save state of a studio document, shown in the project bar.</summary>
public enum SaveState { Loading, Saved, Unsaved, Saving, Failed, Conflict }

/// <summary>One vocabulary for save status across studios. A failed or conflicting save keeps the draft in the browser.</summary>
public static class SaveStatusText
{
    public const string Loading = "Loading…";
    public const string Saved = "Saved";
    public const string Unsaved = "Unsaved";
    public const string Saving = "Saving…";
    public const string Failed = "Save failed · draft retained";
    public const string Conflict = "Conflict · draft retained";

    public static string For(SaveState state) => state switch
    {
        SaveState.Loading => Loading,
        SaveState.Saved => Saved,
        SaveState.Unsaved => Unsaved,
        SaveState.Saving => Saving,
        SaveState.Failed => Failed,
        SaveState.Conflict => Conflict,
        _ => Saved,
    };

    public static bool IsProblem(SaveState state) => state is SaveState.Failed or SaveState.Conflict;
}
