#if TOOLS
using Godot;

namespace Refii.Assets.UI;

/// <summary>
/// Shared UI styling helpers for Fable 2 Mod Studio panels and dialogs.
/// </summary>
public static class ModStudioUiStyles
{
    /// <summary>
    /// Applies high-visibility danger/red theme styling to a button for destructive operations.
    /// </summary>
    public static void ApplyRedButtonStyle(Button btn)
    {
        var redNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.60f, 0.15f, 0.15f),
            BorderColor = new Color(0.85f, 0.25f, 0.25f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        var redHover = new StyleBoxFlat
        {
            BgColor = new Color(0.75f, 0.20f, 0.20f),
            BorderColor = new Color(1.0f, 0.40f, 0.40f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        var redPressed = new StyleBoxFlat
        {
            BgColor = new Color(0.45f, 0.10f, 0.10f),
            BorderColor = new Color(0.70f, 0.20f, 0.20f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        var redDisabled = new StyleBoxFlat
        {
            BgColor = new Color(0.25f, 0.15f, 0.15f, 0.5f),
            BorderColor = new Color(0.35f, 0.20f, 0.20f, 0.5f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };

        btn.AddThemeStyleboxOverride("normal", redNormal);
        btn.AddThemeStyleboxOverride("hover", redHover);
        btn.AddThemeStyleboxOverride("pressed", redPressed);
        btn.AddThemeStyleboxOverride("disabled", redDisabled);
        btn.AddThemeColorOverride("font_color", new Color(1.0f, 0.95f, 0.95f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1.0f, 1.0f, 1.0f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(0.9f, 0.8f, 0.8f));
        btn.AddThemeColorOverride("font_disabled_color", new Color(0.6f, 0.5f, 0.5f, 0.6f));
    }

    /// <summary>The metric-card look of the Home view: a dark slate panel with a thin border.</summary>
    public static StyleBoxFlat CardStyle(Color? border = null, Color? background = null) => new()
    {
        BgColor = background ?? new Color(0.16f, 0.18f, 0.22f),
        BorderColor = border ?? new Color(0.26f, 0.30f, 0.36f),
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        ContentMarginLeft = 10,
        ContentMarginRight = 10,
        ContentMarginTop = 8,
        ContentMarginBottom = 8
    };

    /// <summary>A card holding <paramref name="content"/> under a coloured title, as the Home and Guide views draw them.</summary>
    public static PanelContainer Card(string title, Color accent, Control content)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", CardStyle());
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        var heading = new Label { Text = title, Modulate = accent };
        heading.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(heading);
        column.AddChild(content);
        panel.AddChild(column);
        return panel;
    }

    /// <summary>A muted, wrapping explanatory line.</summary>
    public static Label Note(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.72f, 0.75f, 0.80f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        return label;
    }

    /// <summary>A labelled row: a fixed-width caption, then the control filling the rest.</summary>
    public static HBoxContainer Field(string caption, Control control, string tooltip = "")
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        var label = new Label
        {
            Text = caption,
            CustomMinimumSize = new Vector2(110, 0),
            Modulate = new Color(0.80f, 0.82f, 0.86f),
            TooltipText = tooltip,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        row.AddChild(label);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (tooltip.Length != 0)
        {
            control.TooltipText = tooltip;
        }

        row.AddChild(control);
        return row;
    }

    /// <summary>The validator's colours: an error, a warning, and a clean file.</summary>
    public static readonly Color ErrorColour = new(1.0f, 0.45f, 0.45f);
    public static readonly Color WarningColour = new(1.0f, 0.80f, 0.40f);
    public static readonly Color CleanColour = new(0.55f, 0.90f, 0.60f);
}
#endif
