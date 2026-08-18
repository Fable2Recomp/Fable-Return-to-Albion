using Godot;

namespace Refii.Launcher.Core;

public sealed class SmartStyle
{
    public Color Text { get; private init; }
    public Color TextDisabled { get; private init; }
    public Color WindowBg { get; private init; }
    public Color ChildBg { get; private init; }
    public Color PopupBg { get; private init; }
    public Color Border { get; private init; }
    public Color FrameBg { get; private init; }
    public Color FrameBgHovered { get; private init; }
    public Color FrameBgActive { get; private init; }
    public Color TitleBg { get; private init; }
    public Color MenuBarBg { get; private init; }
    public Color Button { get; private init; }
    public Color ButtonHovered { get; private init; }
    public Color ButtonActive { get; private init; }
    public Color Header { get; private init; }
    public Color Separator { get; private init; }
    public Color Tab { get; private init; }
    public Color TabHovered { get; private init; }
    public Color TabSelected { get; private init; }
    public Color TabDimmed { get; private init; }

    public static SmartStyle FromTheme(Color themeForeground, Color themeBackground)
    {
        // The brand chrome is fixed by the design language: oak, parchment, gold,
        // and Will Magic cyan as the hero action. The legacy config.cfg accent is
        // retained as a parameter for source compatibility only.

        return new SmartStyle
        {
            Text = LauncherTokens.Walnut,
            TextDisabled = LauncherTokens.Sepia,

            WindowBg = LauncherTokens.Parchment,
            ChildBg = LauncherTokens.ParchmentRaised,
            PopupBg = LauncherTokens.ParchmentRaised,

            Border = LauncherTokens.Gold,

            FrameBg = LauncherTokens.Parchment,
            FrameBgHovered = LauncherTokens.ParchmentRaised,
            FrameBgActive = LauncherTokens.ParchmentLit,

            TitleBg = new Color(LauncherTokens.Gold, 0.16f),
            MenuBarBg = LauncherTokens.Oak,

            // Hero action: a muted, earthy green (the "Good" green of Albion),
            // not the blinding tone. Gold does the trim; cream carries the label.
            Button = LauncherTokens.Emerald,
            ButtonHovered = LauncherTokens.EmeraldLit,
            ButtonActive = Variant(LauncherTokens.Emerald, 0.82f, 1.0f, 1.0f),

            Header = new Color(LauncherTokens.Gold, 0.14f),
            Separator = LauncherTokens.Sepia,

            Tab = LauncherTokens.Parchment,
            TabHovered = LauncherTokens.ParchmentRaised,
            TabSelected = LauncherTokens.Gold,
            TabDimmed = LauncherTokens.Sepia,
        };
    }

    public static Color Variant(Color baseColor, float brightnessMult, float saturationMult, float alpha = 1.0f)
    {
        float h = baseColor.H;
        float s = baseColor.S;
        float v = baseColor.V;

        v = Mathf.Clamp(v * brightnessMult, 0.0f, 1.0f);
        s = Mathf.Clamp(s * saturationMult, 0.0f, 1.0f);

        Color converted = Color.FromHsv(h, s, v);
        return new Color(converted.R, converted.G, converted.B, alpha);
    }
}
