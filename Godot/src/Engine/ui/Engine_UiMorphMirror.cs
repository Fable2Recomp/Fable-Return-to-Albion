using System;
using Godot;
using Refii.Hero;

namespace Refii.Ui;

public partial class Engine_UiMorphMirror : Node3D
{
    public const float Width = 480;
    public const float Height = 640;
    private static readonly Color Ink = new(0.16f, 0.12f, 0.08f);
    private static readonly Color SelectedInk = new(0.45f, 0.13f, 0.07f);
    private readonly MeshInstance3D[] _names = new MeshInstance3D[10];
    private readonly MeshInstance3D[] _values = new MeshInstance3D[10];
    private readonly Node3D[] _rows = new Node3D[10];
    private readonly Rect2[] _hits = new Rect2[23];
    private Engine_UiTheme _theme = null!;
    private MeshInstance3D _status = null!;
    private MeshInstance3D _band = null!;
    private int _count;

    /// <summary>The first row's height, and the step to the next, in the panel's units.</summary>
    private const float FirstRowY = 177, RowStep = 32;

    public void Build(Engine_UiTheme theme)
    {
        _theme = theme;
        Node3D frame = theme.Art("MsgFrame") ?? throw new InvalidOperationException("The native menu parchment is unavailable.");
        AddChild(frame);
        Rect2 bounds = Engine_UiPointer.CanvasBounds(frame) ?? throw new InvalidOperationException("The native parchment is empty.");
        Vector2 center = bounds.GetCenter() - new Vector2(GlobalPosition.X, GlobalPosition.Y);
        frame.Scale = new Vector3(Width / bounds.Size.X, Height / bounds.Size.Y, 1);
        frame.Position = new Vector3(-center.X * frame.Scale.X, -center.Y * frame.Scale.Y, 0);
        // Behind the rows' text, so the selected row stands out (ShowSettings moves it).
        _band = Engine_UiTheme.SelectionBand(new Vector2(446, 30));
        AddChild(_band);
        Label(this, "Murgo's Mirror", 28, new Vector2(0, 259), 400, 1, SelectedInk);
        Label(this, "Shape your appearance", 17, new Vector2(0, 224), 400, 1);
        for (int i = 0; i < _rows.Length; i++)
        {
            float y = FirstRowY - i * RowStep;
            _rows[i] = new Node3D();
            AddChild(_rows[i]);
            _names[i] = Label(_rows[i], "", 18, new Vector2(-204, y), 212);
            _values[i] = Label(_rows[i], "", 17, new Vector2(112, y), 128, 1);
            Label(_rows[i], "<", 20, new Vector2(30, y), 28, 1);
            Label(_rows[i], ">", 20, new Vector2(200, y), 28, 1);
            _hits[i * 2] = new Rect2(12, y - 15, 36, 30);
            _hits[i * 2 + 1] = new Rect2(182, y - 15, 36, 30);
        }
        _status = Label(this, "", 15, new Vector2(0, -160), 405, 1);
        Action(20, "a_img", "Apply", new Vector2(-185, -215));
        Action(21, "b_img", "Cancel", new Vector2(20, -215));
        Action(22, "x_img", "Reset", new Vector2(-82, -262));
    }

    private MeshInstance3D Label(Node3D parent, string text, int size, Vector2 at, float width, int align = 0, Color? color = null)
    {
        var label = _theme.Label(text, size, color ?? Ink, horizontalAlign: align, boxWidth: width);
        label.Position = new Vector3(at.X, at.Y, 10);
        parent.AddChild(label);
        return label;
    }

    private void Action(int action, string tag, string text, Vector2 at)
    {
        if (Engine_UiTextMarkup.Find(tag) is not { } markup || _theme.Textures?.Invoke(markup.Texture + ".tex") is not { } texture)
            throw new InvalidOperationException("The native mirror button artwork is unavailable.");
        AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(36, 36) }, MaterialOverride = Engine_UiTheme.IconMaterial(texture, 1),
            Position = new Vector3(at.X + 18, at.Y, 10), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
        Label(this, text, 18, at + new Vector2(43, 0), 130);
        _hits[action] = new Rect2(at.X - 3, at.Y - 19, 175, 38);
    }

    public void ShowSettings(Engine_MorphMirrorSettings settings)
    {
        _count = settings.Count;
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i].Visible = i < _count;
            if (i >= _count) continue;
            Color color = i == settings.Selected ? SelectedInk : Ink;
            _theme.Paint(_names[i], settings.Title(i), 18, color, boxWidth: 212);
            _theme.Paint(_values[i], settings.Display(i), 17, color, horizontalAlign: 1, boxWidth: 128);
            _values[i].Scale = Vector3.One * Math.Min(1, 128 / Math.Max(_values[i].GetAabb().Size.X, 1));
        }
        _band.Visible = settings.Selected >= 0 && settings.Selected < _count;
        _band.Position = new Vector3(10, FirstRowY - settings.Selected * RowStep, 5);
        SetStatus(_count == 1 ? "Turn automatic morphs off to customise" : "");
    }

    public void Layout(float width) => Position = new Vector3(-width / 2 + 20 + Width / 2, 0, 0);
    public void SetStatus(string text) => _theme.Paint(_status, text, 15, SelectedInk, horizontalAlign: 1, boxWidth: 405);
    public int ActionAt(Vector2 point)
    {
        for (int i = 0; i < _hits.Length; i++)
            if ((i >= 20 || i < _count * 2) && _hits[i].HasPoint(point)) return i;
        return -1;
    }
}
