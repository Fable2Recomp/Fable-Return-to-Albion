using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

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
    private readonly Rect2[] _hits = new Rect2[PageSize + 9];
    private readonly Node3D?[] _actions = new Node3D?[9];
    private MeshInstance3D _title = null!, _heading = null!, _page = null!, _collision = null!, _select = null!, _back = null!, _help = null!;
    private string _status = "", _headingText = "";
    private Node3D _body = null!, _frame = null!;
    private Vector2 _frameSize, _frameCenter;
    private float _canvasWidth = 1280;
    private bool _compact, _controller;
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
        _collision = Action(PageSize + 8, "dpad.img", "Collision: Off", new Vector2(-177, -269), 320);
        _help = Label("", 16, new Vector2(-178, 175), 356, parent: _body);
        _help.Visible = false;
        _hits[PageSize + 4] = new Rect2(-185, -111, 150, 30);
        _hits[PageSize + 5] = new Rect2(35, -111, 150, 30);
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
        float height = compact ? 280 : Height;
        PanelBounds = new Rect2(-Width / 2, -height / 2, Width, height);
        _frame.Scale = new Vector3(Width / _frameSize.X, height / _frameSize.Y, 1);
        _frame.Position = new Vector3(-_frameCenter.X * _frame.Scale.X, -_frameCenter.Y * _frame.Scale.Y, 0);
        _title.Position = new Vector3(0, compact ? 104 : 270, 10);
        _heading.Position = new Vector3(0, compact ? 64 : 232, 10);
        MoveAction(8, new Vector2(-177, compact ? -106 : -269));
        _hits[PageSize + 8] = new Rect2(-180, compact ? -124 : -287, 360, 36);
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
        if (_compact != compact || _controller == Engine_UiInputMode.KeyboardMouse) ResizePanel(compact);
        _headingText = HelpOpen ? "Controls" : placing ? "Preview only — confirm to place"
            : room ? target.Length > 0 ? target : _controller ? "Aim at an object and press A" : "Click an object to select it"
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
        _theme.Paint(_select, placing ? "Confirm" : "Select", 16, Ink, boxWidth: 145);
        _theme.Paint(_back, placing ? "Cancel" : HelpOpen || !room ? "Back" : "Deselect", 16, Ink, boxWidth: 145);
    }

    public void Help(bool open)
    {
        HelpOpen = open;
        _help.Visible = open;
        string text = Engine_UiInputMode.KeyboardMouse
            ? "Click / Enter   Select or confirm\nMouse   Move preview\nEsc   Deselect / back\nWASD   Move camera\nHold RMB   Look around\nWheel   Move closer / farther\nQ / E   Camera down / up\nComma / period   Rotate 45 degrees\nZ / C   Rotate   /   F / R   Item height\nShift   Faster   /   Ctrl   Slower\nTab   Furniture inventory\nK   Collision on / off\nU   Hide UI   /   H   Controls\nF10   Exit Decorate mode"
            : "Left stick   Move camera\nRight stick   Look around\nLT / RT   Camera down / up\nA   Select / confirm placement\nB   Cancel move / back\nLB / RB   Rotate preview\nD-pad up / down   Push / pull item\nD-pad left   Collision on / off\nHold Y   Slower, precise movement\nHold L3   Faster movement\nX   Furniture inventory\nView   Controls   /   R3   Hide UI\nMenu   Exit Decorate mode";
        _theme.Paint(_help, text, 15, Ink, verticalAlign: 0, boxWidth: 356);
    }

    public void Collision(bool enabled) => _theme.Paint(_collision, "Collision: " + (enabled ? "On" : "Off"), 16, Ink, boxWidth: 320);
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
        Position = new Vector3(-canvasWidth / 2 + 20 + Width / 2, _compact ? -200 : 0, 0);
    }
    public int Hit(Vector2 point)
    {
        for (int i = HelpOpen || _compact ? PageSize : 0; i < _hits.Length; i++)
        {
            if ((HelpOpen || _compact) && (i == PageSize + 4 || i == PageSize + 5)) continue;
            if (_hits[i].HasPoint(point)) return i;
        }
        return -1;
    }
}
