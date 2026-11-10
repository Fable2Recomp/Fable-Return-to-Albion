using System;
using Godot;
using Refii.Core;

namespace Refii.Ui;

public partial class Engine_UiPhotoMode : Node3D
{
    public const float Width = 460;
    public const float Height = 592;
    public const float HelpHeight = 384;
    private static readonly Color Ink = new(0.16f, 0.12f, 0.08f);
    private static readonly Color SelectedInk = new(0.45f, 0.13f, 0.07f);
    private readonly MeshInstance3D[] _names = new MeshInstance3D[8];
    private readonly MeshInstance3D[] _values = new MeshInstance3D[8];
    private readonly MeshInstance3D[] _help = new MeshInstance3D[7];
    private readonly Rect2[] _hitBoxes = new Rect2[22];
    private Engine_UiTheme _theme = null!;
    private Node3D _panel = null!, _helpDialog = null!;
    private MeshInstance3D _status = null!;
    private MeshInstance3D _band = null!;

    /// <summary>The first setting's height, and the step to the next, in the panel's units.</summary>
    private const float FirstRowY = 152, RowStep = 32;
    private int _inputGeneration = -1;
    public bool HelpOpen { get; private set; }

    public void Build(Engine_UiTheme theme)
    {
        _theme = theme;
        _panel = new Node3D { Name = "PhotoSettings" };
        _helpDialog = new Node3D { Name = "PhotoControlsHelp" };
        AddChild(_panel);
        AddChild(_helpDialog);
        Frame(_panel, Height);
        // Behind the settings' text, so the selected one stands out (ShowSettings moves it).
        _band = Engine_UiTheme.SelectionBand(new Vector2(424, 30));
        _panel.AddChild(_band);
        Label("Photo Mode", 28, new Vector2(0, 230), 380, 1, SelectedInk);
        Label("Game paused", 16, new Vector2(0, 200), 380, 1);
        for (int i = 0; i < 8; i++)
        {
            float y = FirstRowY - i * RowStep;
            _names[i] = Label(Engine_PhotoMode.Settings[i], 19, new Vector2(-190, y), 192);
            _values[i] = Label("", 18, new Vector2(110, y), 126, 1);
            Label("<", 20, new Vector2(32, y), 30, 1);
            Label(">", 20, new Vector2(188, y), 30, 1);
            _hitBoxes[i * 2] = new Rect2(14, y - 15, 36, 30);
            _hitBoxes[i * 2 + 1] = new Rect2(170, y - 15, 36, 30);
        }
        Action(16, "a_img", "Take photo", new Vector2(-170, -132));
        Action(17, "x_img", "Reset", new Vector2(24, -132));
        Action(18, "y_img", "Hide UI", new Vector2(-170, -174));
        Action(19, "b_img", "Back", new Vector2(24, -174));
        Action(20, "back.img", "Controls", new Vector2(-85, -216));
        _status = Label("Photos: Pictures / Refii", 13, new Vector2(0, -254), 380, 1);
        Frame(_helpDialog, HelpHeight);
        Label("Controls", 26, new Vector2(0, 140), 380, 1, SelectedInk, _helpDialog);
        for (int i = 0; i < _help.Length; i++)
            _help[i] = Label("", 16, new Vector2(-184, 94 - i * 28), 368, parent: _helpDialog);
        Action(21, "b_img", "Back to Photo Mode", new Vector2(-145, -132), _helpDialog, 260);
        RefreshDevice();
        SetHelpOpen(false);
    }

    private void Frame(Node3D parent, float height)
    {
        Node3D frame = _theme.Art("MsgFrame") ?? throw new InvalidOperationException("The native menu parchment is unavailable.");
        frame.Name = "NativePhotoDialog";
        parent.AddChild(frame);
        Rect2 bounds = Engine_UiPointer.CanvasBounds(frame)
            ?? throw new InvalidOperationException("The native menu parchment has no visible artwork.");
        Vector2 center = bounds.GetCenter() - new Vector2(parent.GlobalPosition.X, parent.GlobalPosition.Y);
        frame.Scale = new Vector3(Width / bounds.Size.X, height / bounds.Size.Y, 1);
        frame.Position = new Vector3(-center.X * frame.Scale.X, -center.Y * frame.Scale.Y, 0);
    }

    private MeshInstance3D Label(string text, int size, Vector2 at, float width, int align = 0, Color? color = null, Node3D? parent = null)
    {
        var label = _theme.Label(text, size, color ?? Ink, horizontalAlign: align, boxWidth: width);
        label.Position = new Vector3(at.X, at.Y, 10);
        (parent ?? _panel).AddChild(label);
        return label;
    }

    private void Action(int action, string tag, string text, Vector2 at, Node3D? parent = null, float width = 145)
    {
        if (Engine_UiTextMarkup.Find(tag) is not { } markup || _theme.Textures?.Invoke(markup.Texture + ".tex") is not { } texture)
            throw new InvalidOperationException("The native Photo Mode button artwork is unavailable.");
        (parent ?? _panel).AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(36, 36) },
            MaterialOverride = Engine_UiTheme.IconMaterial(texture, 1), Position = new Vector3(at.X + 18, at.Y, 10),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        Label(text, 18, at + new Vector2(43, 0), width, parent: parent);
        _hitBoxes[action] = new Rect2(at.X - 3, at.Y - 19, width + 43, 38);
    }

    public void ShowSettings(Engine_PhotoMode settings)
    {
        for (int i = 0; i < 8; i++)
        {
            Color color = i == settings.Selected ? SelectedInk : Ink;
            _theme.Paint(_names[i], Engine_PhotoMode.Settings[i], 19, color, boxWidth: 192);
            _theme.Paint(_values[i], settings.Display(i), 18, color, horizontalAlign: 1, boxWidth: 126);
            _values[i].Scale = Vector3.One * Math.Min(1, 126 / Math.Max(_values[i].GetAabb().Size.X, 1));
        }

        _band.Position = new Vector3(8, FirstRowY - settings.Selected * RowStep, 5);
    }

    public void RefreshDevice()
    {
        if (_inputGeneration == Engine_UiInputMode.Generation) return;
        _inputGeneration = Engine_UiInputMode.Generation;
        string[] help = Engine_UiInputMode.Touch
            ? TouchHelp() // android/Engine_UiPhotoMode.Touch.cs
            : Engine_UiInputMode.KeyboardMouse
            ? new[] { "WASD  Move     Hold RMB  Look", "Q / E  Lower / raise     Wheel  Zoom",
                "Shift  Faster     Ctrl  Slower", "Arrows  Select / adjust", "PgUp / PgDn  Change filter",
                "Enter  Take photo     R  Reset", "H  Hide UI     F11  Exit Photo Mode" }
            : new[] { "<left.img> Move      <right.img> Look", "<lefttrigger.img> Lower      <righttrigger.img> Raise",
                "L3  Hold for faster movement", "<dpad.img> Select / adjust", "<leftbutton.img> / <rightbutton.img> Change filter",
                "<a_img> Take photo      <x_img> Reset", Refii.Game.GamePhotoMode.PadComboOpens
                    ? "<y_img> Hide UI     L3 + R3  Exit Photo Mode"
                    : "<y_img> Hide UI     <b_img> Exit Photo Mode" };
        for (int i = 0; i < _help.Length; i++)
            _theme.Paint(_help[i], help[i], 16, Ink, boxWidth: 368);
    }

    public void SetHelpOpen(bool open)
    {
        HelpOpen = open;
        _panel.Visible = !open;
        _helpDialog.Visible = open;
    }

    public void Layout(float canvasWidth)
    {
        Position = new Vector3(-canvasWidth / 2 + 20 + Width / 2, 0, 0);
        _helpDialog.Position = new Vector3(-Position.X, 0, 0);
    }

    public void SetStatus(string text) => _theme.Paint(_status, text, 13, SelectedInk, horizontalAlign: 1, boxWidth: 380);

    public int ActionAt(Vector2 point)
    {
        if (HelpOpen)
            return _hitBoxes[21].HasPoint(point - new Vector2(_helpDialog.Position.X, _helpDialog.Position.Y)) ? 21 : -1;
        for (int i = 0; i < 21; i++)
            if (_hitBoxes[i].HasPoint(point)) return i;
        return -1;
    }
}
