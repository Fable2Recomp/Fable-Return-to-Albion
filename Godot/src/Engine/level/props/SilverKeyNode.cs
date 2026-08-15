using System;
using Godot;
using Refii.Fx;

namespace Refii.Level;

[Tool]
public partial class SilverKeyNode : MeshInstance3D
{
    public const float BobPeriod = 3.0f;

    public const float BobAmplitude = 0.08f;

    public const float BobAltitude = 0.25f;

    public const float RotateDegSec = 45.0f;

    [Export]
    public Transform3D BaseTransform { get; set; } = Transform3D.Identity;

    private float _phase;
    private float _yaw;
    private bool _pivotSet;
    private Vector3 _pivot;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        const float twoPi = Mathf.Pi * 2f;

        _phase += (twoPi / BobPeriod) * dt;
        if (_phase > twoPi)
        {
            _phase -= twoPi;
        }

        _yaw += Mathf.DegToRad(RotateDegSec) * dt;
        if (_yaw > twoPi)
        {
            _yaw -= twoPi;
        }

        // THE PIVOT IS FOR THE SPIN, and only for the spin. It used to reposition a hardcoded
        // "KeyFx" child onto the mesh's centre too; the effect now comes from
        // <see cref="Refii.Level.Engine_LevelFx"/> and is placed by the `OffsetFromDummy` the
        // key's own data states, so moving it from here would be the host overriding the game.
        if (!_pivotSet && Mesh is not null)
        {
            _pivotSet = true;
            _pivot = Mesh.GetAabb().GetCenter();
        }

        float bob = (BobAmplitude * MathF.Sin(_phase)) + BobAltitude;

        Transform3D spun = BaseTransform *
            new Transform3D(Basis.Identity, _pivot) *
            new Transform3D(new Basis(Vector3.Up, _yaw), Vector3.Zero) *
            new Transform3D(Basis.Identity, -_pivot);
        spun.Origin += Vector3.Up * bob;
        Transform = spun;
    }
}
