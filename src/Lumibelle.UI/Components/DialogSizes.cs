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
        // CloseOnNavigation stays unset: MudBlazor then closes a dialog only when the path changes, so a link to
        // another page closes it, while the query updates pages make with a dialog open (selection, remembered
        // positions) keep it. Setting true closes on those updates and loses track of inline dialogs; false leaves
        // a dialog open after its page is gone.
    };
}
