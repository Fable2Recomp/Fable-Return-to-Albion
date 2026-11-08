using System;
using System.Globalization;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

public partial class Engine_UiDecorate : Node3D
{
    public const float Width = 430;
    public const float Height = 640;
    public const int PageSize = 7;
    public Rect2 PanelBounds { get; private set; } = new(-Width / 2, -Height / 2, Width, Height);
    private static readonly Color Ink = new(0.16f, 0.12f, 0.08f);
    private static readonly Color Red = new(0.45f, 0.13f, 0.07f);
    private Engine_UiTheme _theme = null!;
    private readonly MeshInstance3D[] _rows = new MeshInstance3D[PageSize];
    private readonly Rect2[] _hits = new Rect2[PageSize + 13];
    private readonly Node3D?[] _actions = new Node3D?[10];
    private MeshInstance3D _title = null!, _heading = null!, _page = null!, _collision = null!, _select = null!, _back = null!, _help = null!;
    private string _status = "", _headingText = "";
    private Node3D _body = null!, _frame = null!;
    private Vector2 _frameSize, _frameCenter;
    private float _canvasWidth = 1280;
    private bool _compact, _controller;
    private bool _placing;
    private Node3D _rotationRow = null!;
    private readonly MeshInstance3D[] _rotationValues = new MeshInstance3D[3];
    private readonly string[] _rotationTexts = { "", "", "" };
    private int _rotationAxis = -1;
    private bool _rotationEditing;
    private Vector3 _rotationDegrees = new(float.NaN, float.NaN, float.NaN);
    public bool HelpOpen { get; private set; }

    public void Build(Engine_UiTheme theme)
    {
        _theme = theme;
        _frame = theme.Art("MsgFrame") ?? throw new InvalidOperationException("Fable 2's menu frame is unavailable.");
        AddChild(_frame);
        Rect2 bounds = Engine_UiPointer.CanvasBounds(_frame) ?? throw new InvalidOperationException("The native frame has no bounds.");
        _frameCenter = bounds.GetCenter() - new Vector2(GlobalPosition.X, GlobalPosition.Y);
        _frameSize = bounds.Size;
        _title = Label("Decorate", 28, new Vector2(0, 270), 360, 1, Red);
        _heading = Label("Furniture inventory", 19, new Vector2(0, 232), 360, 1);
        _body = new Node3D();
        AddChild(_body);
        for (int i = 0; i < PageSize; i++)
        {
            float y = 178 - i * 39;
            _rows[i] = Label("", 17, new Vector2(-178, y), 356, parent: _body);
            _hits[i] = new Rect2(-185, y - 18, 370, 36);
        }
        _page = Label("", 15, new Vector2(0, -96), 360, 1);
        _select = Action(PageSize, "a_img", "Select / place", new Vector2(-177, -143));
        _back = Action(PageSize + 1, "b_img", "Deselect", new Vector2(18, -143));
        Action(PageSize + 2, "x_img", "Inventory", new Vector2(-177, -185));
        Action(PageSize + 7, "start.img", "Exit mode", new Vector2(18, -185));
        Action(PageSize + 3, "back.img", "Controls", new Vector2(-177, -227));
        Action(PageSize + 6, "right.img", "Hide UI", new Vector2(18, -227));
        _collision = Action(PageSize + 8, "dpad.img", "Collision: Off", new Vector2(-177, -269));
        Action(PageSize + 9, "y_img", "Put away", new Vector2(18, -269));
        _actions[9]!.Visible = false;
        _help = Label("", 16, new Vector2(-178, 175), 356, parent: _body);
        _help.Visible = false;
        _hits[PageSize + 4] = new Rect2(-185, -111, 150, 30);
        _hits[PageSize + 5] = new Rect2(35, -111, 150, 30);
        _rotationRow = new Node3D { Position = new Vector3(0, 60, 0), Visible = false }; AddChild(_rotationRow);
        for (int i = 0; i < 3; i++)
        {
            float x = (i - 1) * 126;
            Label(new[] { "X", "Y", "Z" }[i], 16, new Vector2(x, 28), 110, 1, parent: _rotationRow);
            _rotationRow.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(110, 32) },
                Position = new Vector3(x, 0, 9), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.45f, 0.3f, 0.13f, 0.15f) } });
            _rotationValues[i] = Label("0.0°", 18, new Vector2(x, 0), 108, 1, parent: _rotationRow);
            _hits[PageSize + 10 + i] = new Rect2(x - 55, 42, 110, 36);
        }
        ResizePanel(false);
    }

    private MeshInstance3D Label(string text, int size, Vector2 at, float width, int align = 0, Color? color = null, Node3D? parent = null)
    {
        var node = _theme.Label(text, size, color ?? Ink, horizontalAlign: align, boxWidth: width);
        node.Position = new Vector3(at.X, at.Y, 10);
        (parent ?? this).AddChild(node);
        return node;
    }

    private MeshInstance3D Action(int action, string tag, string text, Vector2 at, float width = 145)
    {
        var root = new Node3D { Position = new Vector3(at.X, at.Y, 0) }; AddChild(root);
        _actions[action - PageSize] = root;
        if (Engine_UiTextMarkup.Find(tag) is { } markup && _theme.Textures?.Invoke(markup.Texture + ".tex") is { } texture)
            root.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(30, 30) },
                MaterialOverride = Engine_UiTheme.IconMaterial(texture, 1), Position = new Vector3(15, 0, 10),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _hits[action] = new Rect2(at.X - 3, at.Y - 18, width + 36, 36);
        return Label(text, 16, new Vector2(36, 0), width, parent: root);
    }

    private void MoveAction(int action, Vector2 position)
    {
        if (_actions[action] is { } root) root.Position = new Vector3(position.X, position.Y, 0);
        _hits[PageSize + action] = new Rect2(position.X - 3, position.Y - 18, 181, 36);
    }

    private void ResizePanel(bool compact)
    {
        _compact = compact;
        _controller = !Engine_UiInputMode.KeyboardMouse;
        float height = compact ? _placing ? 400 : 280 : Height;
        PanelBounds = new Rect2(-Width / 2, -height / 2, Width, height);
        _frame.Scale = new Vector3(Width / _frameSize.X, height / _frameSize.Y, 1);
        _frame.Position = new Vector3(-_frameCenter.X * _frame.Scale.X, -_frameCenter.Y * _frame.Scale.Y, 0);
        _title.Position = new Vector3(0, compact ? _placing ? 164 : 104 : 270, 10);
        _heading.Position = new Vector3(0, compact ? _placing ? 124 : 64 : 232, 10);
        MoveAction(8, new Vector2(-177, compact ? -106 : -269));
        MoveAction(9, new Vector2(18, compact ? -106 : -269));
        MoveAction(0, new Vector2(-177, compact ? 20 : -143));
        MoveAction(1, new Vector2(18, compact ? 20 : -143));
        MoveAction(2, new Vector2(-177, compact ? -22 : -185));
        MoveAction(7, new Vector2(18, compact ? -22 : -185));
        MoveAction(3, new Vector2(-177, compact ? -64 : -227));
        MoveAction(6, new Vector2(18, compact ? -64 : -227));
        Layout(_canvasWidth);
    }

    public void ShowRows(IReadOnlyList<string> labels, int selected, bool room, bool placing, string target = "")
    {
        bool compact = !HelpOpen && (room || placing);
        bool changed = _placing != placing;
        _placing = placing;
        if (changed || _compact != compact || _controller == Engine_UiInputMode.KeyboardMouse) ResizePanel(compact);
        _rotationRow.Visible = placing && !HelpOpen;
        _actions[9]!.Visible = placing && !HelpOpen;
        _headingText = HelpOpen ? "Controls" : placing ? "Preview only â€” confirm to place"
            : room ? target.Length > 0 ? target : _controller ? "Aim at an object and press A" : Engine_UiInputMode.Touch ? TouchHint : "Click an object to select it"
            : "Furniture inventory";
        PaintHeading();
        int first = selected / PageSize * PageSize;
        for (int i = 0; i < PageSize; i++)
        {
            _rows[i].Visible = !HelpOpen && !compact;
            string text = first + i < labels.Count ? labels[first + i] : "";
            if (labels.Count == 0 && i == 0) text = room ? "No movable objects found" : "No furniture in your inventory";
            _theme.Paint(_rows[i], text, 17, room || placing ? Ink : first + i == selected ? Red : Ink, boxWidth: 356);
            _rows[i].Scale = Vector3.One * Math.Min(1, 356 / Math.Max(_rows[i].GetAabb().Size.X, 1));
        }
        _theme.Paint(_page, HelpOpen || room || placing ? "" : $"<    {first / PageSize + 1} / {Math.Max(1, (labels.Count + PageSize - 1) / PageSize)}    >", 15, Ink, horizontalAlign: 1, boxWidth: 360);
        _theme.Paint(_select, placing ? _rotationEditing ? "Done" : "Confirm" : "Select", 16, Ink, boxWidth: 145);
        _theme.Paint(_back, placing ? "Cancel" : HelpOpen || !room ? "Back" : "Deselect", 16, Ink, boxWidth: 145);
    }

    public void Help(bool open)
    {
        HelpOpen = open;
        _help.Visible = open;
<<<<<<< Updated upstream:Godot/src/Titles/Fable2/ui/Engine_UiDecorate.cs
        string text = Engine_UiInputMode.Touch ? TouchHelp : Engine_UiInputMode.KeyboardMouse
            ? "Click / Enter   Select or confirm\nMouse   Move preview\nWASD   Camera   /   RMB   Look\nQ / E   Camera down / up\nWheel   Move closer / farther\nComma / period   Rotate 45 degrees\nZ / C   Rotate   /   F / R   Item height\nClick X / Y / Z   Edit rotation\nEnter   Accept angle   /   Esc   Cancel\nShift   Faster   /   Ctrl   Slower\nDelete   Put away\nTab   Furniture inventory\nK   Collision on / off\nU   Hide UI   /   H   Controls\nF10   Exit Decorate mode"
=======
        string text = Engine_UiInputMode.KeyboardMouse
            ? "Click / Enter   Select or confirm\nMouse   Move preview\nWASD   Camera   /   RMB   Look\nQ / E   Camera down / up\nWheel   Item up / down; otherwise zoom\nComma / period   Rotate 45 degrees\nZ / C   Rotate   /   F / R   Item height\nClick X / Y / Z   Edit rotation\nEnter   Accept angle   /   Esc   Cancel\nShift   Faster   /   Ctrl   Slower\nDelete   Put away\nTab   Furniture inventory\nK   Collision on / off\nU   Hide UI   /   H   Controls\nF10   Exit Decorate mode"
>>>>>>> Stashed changes:Godot/src/Engine/ui/Engine_UiDecorate.cs
            : "Left stick   Move camera\nRight stick   Look around\nLT / RT   Camera down / up\nA   Select / confirm   /   B   Cancel\nLB / RB   Rotate preview\nD-pad up / down   Push / pull item\nD-pad left   Collision on / off\nY   Put away   /   Hold L3   Faster\nX   Furniture inventory\nView   Controls   /   R3   Hide UI\nMenu   Exit Decorate mode";
        _theme.Paint(_help, text, 15, Ink, verticalAlign: 0, boxWidth: 356);
    }

    public void Collision(bool enabled) => _theme.Paint(_collision, "Collision: " + (enabled ? "On" : "Off"), 16, Ink, boxWidth: 145);
    public void ShowRotation(string text, bool editing) => ShowRotation(text, editing, 1);
    public void ShowRotation(string text, bool editing, int axis)
    {
        if (_rotationTexts[axis] == text && _rotationEditing == editing && _rotationAxis == axis) return;
        _rotationTexts[axis] = text; _rotationEditing = editing; _rotationAxis = axis;
        if (_placing) _theme.Paint(_select, editing ? "Done" : "Confirm", 16, Ink, boxWidth: 145);
        _theme.Paint(_rotationValues[axis], text + (editing ? " |" : "°"), 18, editing ? Red : Ink, horizontalAlign: 1, boxWidth: 108);
    }
    public void ShowRotation(float degrees) => ShowRotation(new Vector3(0, degrees, 0));
    public void ShowRotation(Vector3 degrees)
    {
        int previousEdit = _rotationEditing ? _rotationAxis : -1;
        for (int i = 0; i < 3; i++)
        {
            float rounded = MathF.Round(degrees[i], 1);
            if (_rotationDegrees[i] == rounded && previousEdit != i) continue;
            _rotationDegrees[i] = rounded;
            ShowRotation(rounded.ToString("0.0", CultureInfo.InvariantCulture), false, i);
        }
    }
    public void Status(string text)
    {
        if (_status == text) return;
        _status = text;
        PaintHeading();
    }
    private void PaintHeading()
    {
        _theme.Paint(_heading, _status.Length > 0 && !HelpOpen ? _status : _headingText, 19,
            _status.Length > 0 && !HelpOpen ? Red : Ink, horizontalAlign: 1, boxWidth: 360);
        _heading.Scale = Vector3.One * Math.Min(1, 360 / Math.Max(_heading.GetAabb().Size.X, 1));
    }
    public void Layout(float canvasWidth)
    {
        _canvasWidth = canvasWidth;
        Position = new Vector3(-canvasWidth / 2 + 20 + Width / 2, _compact ? -340 + PanelBounds.Size.Y / 2 : 0, 0);
    }
    public int Hit(Vector2 point)
    {
        for (int i = HelpOpen || _compact ? PageSize : 0; i < _hits.Length; i++)
        {
            if ((HelpOpen || _compact) && (i == PageSize + 4 || i == PageSize + 5)) continue;
            if (i >= PageSize + 9 && (!_placing || HelpOpen)) continue;
            if (_hits[i].HasPoint(point)) return i;
        }
        return -1;
    }
}
