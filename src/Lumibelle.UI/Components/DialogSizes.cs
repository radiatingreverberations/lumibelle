using MudBlazor;

namespace lumibelle.Components;

/// <summary>
/// The dialog sizes from the design system: small (520px) for confirmations and short forms,
/// medium (800px) for focused tasks, large (1200px) for pickers and editors, and media for review
/// surfaces that may span the viewport. Every size closes on Escape and never on a backdrop click,
/// so a stray click cannot discard staged work. <see cref="InlineDialog"/> routes Escape through the
/// dialog's own close handler; service dialogs use MudBlazor's.
/// </summary>
public static class DialogSizes
{
    public static readonly DialogOptions Small = Create(MaxWidth.Small);
    public static readonly DialogOptions Medium = Create(MaxWidth.Medium);
    public static readonly DialogOptions Large = Create(MaxWidth.Large);
    /// <summary>Large, plus the class hook for media review layouts that may use the full viewport.</summary>
    public static readonly DialogOptions Media = Create(MaxWidth.ExtraLarge);

    private static DialogOptions Create(MaxWidth width) => new()
    {
        MaxWidth = width,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = false,
        // Pages update their URL while dialogs are open (selection, remembered positions). MudBlazor would treat
        // that as navigation and lose track of an inline dialog, which then can no longer be closed. Dialogs close
        // with their page instead.
        CloseOnNavigation = false,
    };
}
