using Godot;
using Refii.Entities;
using Refii.Game;

namespace Refii.Save;

[Tool]
public partial class Engine_GoldenCock : Node3D
{
    public override void _EnterTree()
    {
        if (!Godot.Engine.IsEditorHint()) GameGoldenCock.Register(this);
    }

    public override void _ExitTree()
    {
        if (Godot.Engine.IsEditorHint()) return;
        SetHighlight(0x000000FFu);
        GameGoldenCock.Unregister(this);
    }

    public bool Contains(Vector3 hero)
    {
        var shape = GetNodeOrNull<CollisionShape3D>("Interaction/Sphere");
        return shape is { Disabled: false, Shape: SphereShape3D sphere } && IsVisibleInTree() &&
            (shape.GlobalTransform.AffineInverse() * hero).LengthSquared() <= sphere.Radius * sphere.Radius;
    }

    public void SetHighlight(uint word) =>
        Engine_EntityHighlight.Apply(this, word != 0x000000FFu,
            Color.Color8((byte)(word >> 8), (byte)(word >> 16), (byte)(word >> 24)), word);
}
