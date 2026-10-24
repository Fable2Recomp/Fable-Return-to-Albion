using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

public static class Engine_UiQuickSlotArt
{
    public const string Folder = "res://refii/ui/quickslots/";
    private static readonly Dictionary<string, Material> Materials = new();

    public static void Set(MeshInstance3D icon, string name, bool dark = false)
    {
        string key = dark ? name + ":dark" : name;
        if (!Materials.TryGetValue(key, out var material))
        {
            var texture = GD.Load<Texture2D>(Folder + name + ".png");
            var painted = Engine_UiTheme.IconMaterial(texture, 1f);
            if (dark) painted.AlbedoColor = name == "assigned" ? new Color(0.18f, 0.35f, 0.10f) : new Color(0.17f, 0.14f, 0.10f);
            Materials[key] = material = painted;
        }
        icon.MaterialOverride = material;
    }

    public static MeshInstance3D Icon(string name, float size, Vector3 at)
    {
        var icon = new MeshInstance3D { Mesh = new QuadMesh { Size = new(size, size) }, Position = at,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Set(icon, name);
        return icon;
    }
}

public partial class Engine_UiQuickSlotPrompt : Node3D
{
    public static Vector3 AnchorOffset(bool spell) => spell ? new(0, -40, 60) : new(-120, 0, 60);

    public void PlaceAt(Node3D anchor, bool spell)
    {
        var transform = anchor.GlobalTransform;
        Vector3 scale = transform.Basis.Scale;
        Vector3 offset = AnchorOffset(spell);
        GlobalPosition = transform.Origin + new Vector3(offset.X * scale.X, offset.Y * scale.Y, offset.Z);
        Scale = new Vector3(scale.X, scale.Y, 1);
    }

    private Engine_UiTheme? _theme;
    private MeshInstance3D? _label;
    private MeshInstance3D? _button;
    private int _mask = -1;
    private bool _spell, _onPage;

    public void SetState(Engine_UiTheme theme, int mask, bool spell, bool onPage)
    {
        if (_label is null)
        {
            _label = theme.Label("", 18, Colors.White, horizontalAlign: 2);
            _label.Name = "Text";
            _label.Position = new Vector3(-24, 1, 0);
            AddChild(_label);
            _button = new MeshInstance3D { Name = "Button", Mesh = new QuadMesh { Size = new(40, 40) },
                Position = new Vector3(1, -1, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_button);
        }
        if (_theme != theme || _mask != mask || _spell != spell || _onPage != onPage)
        {
            _theme = theme;
            _mask = mask;
            _spell = spell;
            _onPage = onPage;
            string button = spell ? "x_img" : "y_img";
            if (Engine_UiTextMarkup.Find(button) is { } tag && theme.Textures?.Invoke(tag.Texture + ".tex") is { } art)
                _button!.MaterialOverride = Engine_UiTheme.IconMaterial(art, 1f);
            string status = mask == 0 ? "Assign quickslot" : "Reassign quickslot";
            theme.Paint(_label, status, 18,
                onPage ? new Color(0.18f, 0.16f, 0.12f) : new Color(0.96f, 0.90f, 0.75f), horizontalAlign: 2);
        }
        Visible = true;
    }

    public bool Clicked()
    {
        if (!Engine_UiPointer.LeftPressed || Engine_UiPointer.Canvas is not { } pointer ||
            _label is null || Engine_UiPointer.CanvasBounds(this) is not { } bounds) return false;
        return bounds.Grow(8).HasPoint(pointer) && Engine_UiPointer.TakeLeft();
    }
}
