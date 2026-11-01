using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The see-through look of the in-game Options panel and the menus it opens: dark rounded boxes with a
/// faint gold edge, so the world shows behind them while a setting changes it.
/// </summary>
public static class AndroidSeeThrough
{
    /// <summary>A dark rounded box at <paramref name="alpha"/> with a faint gold edge.</summary>
    public static StyleBoxFlat Box(float alpha, float margin)
    {
        var box = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.06f, 0.05f, alpha),
            BorderColor = new Color(0.85f, 0.7f, 0.4f, 0.45f),
        };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(10);
        box.SetContentMarginAll(margin);
        return box;
    }

    /// <summary>Gives a button see-through states.</summary>
    public static void Style(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Box(0.45f, 10f));
        button.AddThemeStyleboxOverride("hover", Box(0.45f, 10f));
        button.AddThemeStyleboxOverride("pressed", Box(0.7f, 10f));
        button.AddThemeStyleboxOverride("disabled", Box(0.2f, 10f));
    }

    /// <summary>A finger-sized see-through button that never takes keyboard or controller focus.</summary>
    public static Button Button(string text, Vector2 minimum, int fontSize = 20)
    {
        var button = new Button
        {
            Text = text, FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = minimum, MouseFilter = Control.MouseFilterEnum.Stop,
        };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        Style(button);
        return button;
    }

    /// <summary>A see-through panel, stopping touches that land on it.</summary>
    public static PanelContainer Panel(float alpha = 0.55f)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", Box(alpha, 14f));
        return panel;
    }

    /// <summary>A label at <paramref name="fontSize"/>.</summary>
    public static Label Label(string text, int fontSize = 18)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }
}
