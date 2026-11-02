using MudBlazor;

namespace lumibelle.Components.Layout;

public static class StudioTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#7D4F78",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#2F6847",
            Background = "#F7F6F7",
            Surface = "#FFFFFF",
            TextPrimary = "#252127",
            TextSecondary = "#716A73",
            Divider = "#E5E1E5",
            LinesInputs = "#8A818C",
            Error = "#B42318",
            Success = "#2F6847", Warning = "#805719", Info = "#286775"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#C99AC2", PrimaryContrastText = "#211E23",
            Secondary = "#9BC6AA", Background = "#18161A", Surface = "#211E23",
            TextPrimary = "#F2EDF3", TextSecondary = "#B6ABB8", Divider = "#403943",
            LinesInputs = "#86798A", Error = "#FF9B93", ErrorContrastText = "#211E23",
            Success = "#9BC6AA", SuccessContrastText = "#211E23",
            Warning = "#E5BE7D", WarningContrastText = "#211E23", Info = "#91CBD8", InfoContrastText = "#211E23",
            SecondaryContrastText = "#211E23",
            DrawerBackground = "#211E23", AppbarBackground = "#211E23"
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Geist", "Segoe UI", "system-ui", "sans-serif"], FontSize = "var(--lumi-font-size-ui)", LineHeight = "1.5" },
            Body2 = new Body2Typography { FontSize = "var(--lumi-font-size-meta)", LineHeight = "1.5" },
            Caption = new CaptionTypography { FontSize = "var(--lumi-font-size-small)", LineHeight = "1.5" },
            Button = new ButtonTypography { TextTransform = "none", FontWeight = "500", FontSize = "var(--lumi-font-size-compact)", LetterSpacing = "0" }
        }
    };
}
