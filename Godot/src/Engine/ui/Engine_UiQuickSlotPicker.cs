using System;
using System.Linq;
using Godot;

namespace Refii.Ui;

public partial class Engine_UiQuickSlotPicker : Node3D
{
    private Engine_UiTheme _theme = null!;
    private Engine_UiElementResolve _resolve = null!;
    private Func<string, uint> _icon = null!;
    private readonly Engine_UiCloneBinding[] _rings = new Engine_UiCloneBinding[3];
    private readonly uint[][] _textures = new uint[3][];
    private readonly uint[][] _defaults = new uint[3][];
    private readonly MeshInstance3D[] _labels = new MeshInstance3D[12];
    private readonly MeshInstance3D[] _banks = new MeshInstance3D[3];
    private readonly Rect2[] _hitBoxes = new Rect2[12];
    private readonly int[] _ordinals = new int[4];
    private MeshInstance3D _title = null!, _status = null!, _help = null!;
    private Engine_UiQuickSlotAssignment? _assignment;
    private uint _revision;
    private int _bank = -1;
    private static readonly Color Gold = new(0.96f, 0.84f, 0.56f);

    public void Build(Engine_UiTheme theme, Engine_UiScreen screen, Func<uint, Texture2D?> resolveTexture,
        Func<string, uint> icon, uint[]? contextualTextures = null)
    {
        _theme = theme;
        _icon = icon;
        var menu = screen.Nodes.First(n => n.Name == "dpadmenu");
        var group = menu.Children.First(n => n.Name == "group");
        var bindings = Engine_UiBindings.Read(screen.Document);
        int[] slides = new int[screen.Nodes.Length];
        foreach (var slide in screen.Document.Slides)
            if (!slide.IsMaster) slides[slide.OwnerElement] = 1;
        var active = Enumerable.Repeat(true, slides.Length).ToArray();
        var opacity = new float?[slides.Length];
        new Engine_UiQuickSlotHud(screen, bindings).Apply(slides, active, opacity);
        _resolve = new Engine_UiElementResolve(screen);
        _resolve.Apply(slides, active, opacity);
        string[] directions = { "Up", "Down", "Left", "Right" };
        for (int d = 0; d < 4; d++)
        {
            int slot = bindings.Elements(Engine_UiBindings.Kind.SlideToggle, "DPad" + directions[d]).First();
            _ordinals[d] = screen.Nodes[slot].Children.First(n => n.Quad is not null).Ordinal;
        }
        for (int bank = 0; bank < 3; bank++)
        {
            var ring = theme.ArtForOrdinal(group.Name, group.Ordinal) ?? throw new InvalidOperationException("The native D-pad artwork is unavailable.");
            AddChild(ring);
            ring.Position = new Vector3((bank - 1) * 340, 15, 0);
            ring.Scale = new Vector3(2.1f, 2.1f, 1);
            var bound = _rings[bank] = new Engine_UiCloneBinding { ResolveTexture = resolveTexture };
            bound.Bind(ring, screen);
            _textures[bank] = new uint[slides.Length];
            foreach (int ordinal in _ordinals)
                _textures[bank][ordinal] = bank == 0 && contextualTextures is not null && ordinal < contextualTextures.Length && contextualTextures[ordinal] != 0
                    ? contextualTextures[ordinal] : screen.Nodes[ordinal].Quad!.TextureHash;
            bound.Apply(_resolve, _textures[bank]);
            _defaults[bank] = (uint[])_textures[bank].Clone();
            for (int d = 0; d < 4; d++)
            {
                int slot = bank * 4 + d;
                var node = bound.NodeFor(_ordinals[d])!;
                Transform3D transform = node.Transform;
                for (Node? parent = node.GetParent(); parent is Node3D p && p != this; parent = p.GetParent())
                    transform = p.Transform * transform;
                Vector2 at = new(transform.Origin.X, transform.Origin.Y);
                if (resolveTexture(screen.Nodes[_ordinals[d]].Quad!.TextureHash) is { } empty)
                    AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(88, 88) },
                        Position = new Vector3(at.X, at.Y, -10), MaterialOverride = Engine_UiTheme.IconMaterial(empty, 1),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                _hitBoxes[slot] = new Rect2(at - new Vector2(42, 42), new Vector2(84, 84));
                _labels[slot] = Label("", 17, new Vector2(at.X, at.Y + (d == 0 ? 55 : -58)), 152);
            }
            _banks[bank] = Label(bank == 0 ? "<dpad.img> D-pad" : bank == 1 ? "<lefttrigger.img> + D-pad" : "<righttrigger.img> + D-pad",
                26, new Vector2((bank - 1) * 340, 190));
        }
        _title = Label("", 30, new Vector2(0, 280), 940);
        _status = Label("", 23, new Vector2(0, -235), 1000);
        _help = Label("", 21, new Vector2(0, -290), 1000);
    }

    private MeshInstance3D Label(string text, int size, Vector2 at, float width = 300)
    {
        var label = _theme.Label(text, size, Gold, horizontalAlign: 1, boxWidth: width, wrapMode: 1);
        label.Position = new Vector3(at.X, at.Y, 40);
        AddChild(label);
        return label;
    }

    public void ShowAssignment(Engine_UiQuickSlotAssignment assignment)
    {
        if (!ReferenceEquals(_assignment, assignment) || _revision != assignment.Revision)
        {
            _assignment = assignment;
            _revision = assignment.Revision;
            _theme.Paint(_title, "Assign to quickslot\n" + assignment.Selected.Label, 30, Gold, horizontalAlign: 1, boxWidth: 940);
            _theme.Paint(_status, assignment.Status, 23, Gold, horizontalAlign: 1, boxWidth: 1000);
            _theme.Paint(_help, "<b_img> Back" + (assignment.AssignedMask != 0 ? "     <x_img> Remove assignment" : ""), 21, Gold, horizontalAlign: 1);
            for (int slot = 0; slot < 12; slot++)
            {
                var choice = assignment.Slots[slot];
                _textures[slot / 4][_ordinals[slot % 4]] = _defaults[slot / 4][_ordinals[slot % 4]];
                if (choice.Binding.Assigned)
                {
                    uint texture = _icon(choice.Icon);
                    if (texture != 0) _textures[slot / 4][_ordinals[slot % 4]] = texture;
                }
                bool assigned = (assignment.AssignedMask & (1 << slot)) != 0;
                _theme.Paint(_labels[slot], choice.Label, 17, assigned ? Gold : new Color(0.9f, 0.9f, 0.85f), horizontalAlign: 1, boxWidth: 152, wrapMode: 1);
            }
            for (int bank = 0; bank < 3; bank++) _rings[bank].Apply(_resolve, _textures[bank]);
        }
        if (_bank != assignment.CurrentBank)
        {
            _bank = assignment.CurrentBank;
            for (int bank = 0; bank < 3; bank++)
            {
                string text = bank == 0 ? "<dpad.img> D-pad" : bank == 1 ? "<lefttrigger.img> + D-pad" : "<righttrigger.img> + D-pad";
                _theme.Paint(_banks[bank], text, 26, bank == _bank ? Gold : new Color(0.65f, 0.66f, 0.67f), horizontalAlign: 1);
            }
        }
    }

    public int SlotAt(Vector2 point)
    {
        for (int i = 0; i < _hitBoxes.Length; i++) if (_hitBoxes[i].HasPoint(point)) return i;
        if (point.Y is >= -316 and <= -269 && Math.Abs(point.X) <= 250)
            return _assignment?.AssignedMask != 0 && point.X >= 0 ? -3 : -2;
        return -1;
    }
}
