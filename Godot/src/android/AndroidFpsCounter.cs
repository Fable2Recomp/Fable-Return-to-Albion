using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The frame rate, a bare number, small, in the top left corner above the health bar, while <c>fps_counter</c> is on
/// (Options' FPS counter button). White with a dark outline, so it reads over sky and snow alike, and
/// a little see-through so it stays out of the way. Updated twice a second from Godot's own count.
/// </summary>
/// <remarks>
/// It stands <see cref="Inset"/> in from the top left, far enough in that a phone's rounded corner
/// doesn't clip it, left-justified so the number grows to the right. Not the display's safe area: a
/// camera hole at the middle of the left edge (a Galaxy S25+ in landscape) moves that whole edge in.
/// </remarks>
public partial class AndroidFpsCounter : CanvasLayer
{
    /// <summary>The setting's key in refii.toml.</summary>
    public const string Key = "fps_counter";

    private const double Every = 0.5;

    /// <summary>How far in from the top left corner: a share of the screen's width across, canvas units down.</summary>
    private static readonly Vector2 Inset = new(0.0235f, 6f);

    private Label? _label;
    private double _clock;

    /// <summary>Builds the label, hidden.</summary>
    public override void _Ready()
    {
        Layer = 150;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 18);
        _label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.85f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.7f));
        _label.AddThemeConstantOverride("outline_size", 5);
        AddChild(_label);
    }

    /// <summary>Shows or hides the counter.</summary>
    public void SetShown(bool shown)
    {
        Visible = shown;
        _clock = 0.0;
    }

    /// <summary>Rewrites the count every half second while shown.</summary>
    public override void _Process(double delta)
    {
        if (!Visible || _label is null) return;
        _clock -= delta;
        if (_clock > 0.0) return;
        _clock = Every;
        _label.Position = Corner();
        _label.Text = $"{Engine.GetFramesPerSecond():0}";
    }

    /// <summary>The label's place, <see cref="Inset"/> in from the top left, in canvas units.</summary>
    private Vector2 Corner() => new(GetViewport().GetVisibleRect().Size.X * Inset.X, Inset.Y);
}
