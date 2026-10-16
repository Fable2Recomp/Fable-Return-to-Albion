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
}
#endif
