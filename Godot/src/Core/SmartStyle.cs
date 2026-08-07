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
        Color background = themeForeground;
        Color foreground = themeBackground;

        return new SmartStyle
        {
            Text = new Color(1f, 1f, 1f, 1f),
            TextDisabled = new Color(0.5f, 0.5f, 0.5f, 1f),

            WindowBg = Variant(background, 0.35f, 0.8f, 0.54f),
            ChildBg = Variant(background, 0.35f, 0.8f, 0.54f),
            PopupBg = Variant(background, 0.15f, 0.9f, 0.94f),

            Border = Variant(foreground, 0.6f, 1.0f, 0.50f),

            FrameBg = Variant(background, 0.35f, 0.8f, 0.54f),
            FrameBgHovered = Variant(foreground, 0.65f, 0.9f, 0.40f),
            FrameBgActive = Variant(foreground, 0.75f, 1.0f, 0.67f),

            TitleBg = Variant(background, 0.12f, 0.9f, 1.00f),
            MenuBarBg = Variant(background, 0.15f, 0.9f, 1.00f),

            Button = Variant(foreground, 0.70f, 0.9f, 0.40f),
            ButtonHovered = Variant(foreground, 0.80f, 1.0f, 1.00f),
            ButtonActive = Variant(foreground, 0.65f, 0.9f, 1.00f),

            Header = Variant(foreground, 0.55f, 0.9f, 0.31f),
            Separator = Variant(foreground, 0.40f, 0.8f, 0.50f),

            Tab = Variant(background, 0.45f, 0.8f, 0.86f),
            TabHovered = Variant(foreground, 0.80f, 1.0f, 0.80f),
            TabSelected = Variant(foreground, 0.58f, 0.9f, 1.00f),
            TabDimmed = Variant(background, 0.15f, 0.7f, 0.97f),
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
