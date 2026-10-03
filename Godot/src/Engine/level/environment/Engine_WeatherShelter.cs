using System;
using Godot;
using Refii.Host;

namespace Refii.Level;

/// <summary>
/// Keeps what falls out of the building the hero is in.
///
/// A REMASTER CHOICE, NOT THE GUEST'S. Its rain and snow have no indoor test at all (see
/// <see cref="Engine_Weather"/>) and it paints some buildings with the street's own day set,
/// so it states snow inside them — a Knothole Island shop reads SnowSize 0.53. What it does
/// state is WHICH BUILDING the hero stands in (<see cref="HostBridge.HeroBuilding"/>), which
/// it uses to dim its light shafts indoors. That entity's bounds are the dry volume: the
/// precipitation shaders drop every flake inside <see cref="ToBox"/>, so it still falls
/// outside the windows. The guest's weather and the dynamic one both read this.
/// </summary>
public static class Engine_WeatherShelter
{
    /// <summary>Seconds the haze, and the fall when no bounds are known, take across a doorway.</summary>
    private const float FadeSeconds = 0.75f;

    private const float SampleSeconds = 0.1f;

    /// <summary>1 outdoors, 0 in a building: what is in the AIR around the hero (haze).</summary>
    public static float Exposure { get; private set; } = 1f;

    /// <summary>
    /// The scale on what falls: 1 while <see cref="ToBox"/> does the work, and
    /// <see cref="Exposure"/> in a building whose bounds could not be read.
    /// </summary>
    public static float Falling => _building != 0 && _boxOf != _building ? Exposure : 1f;

    /// <summary>
    /// World to the dry volume's unit box: a point is inside when every component is within
    /// ±1. The last building's box stands after the hero leaves, so looking back through its
    /// door shows a dry room.
    /// </summary>
    public static Transform3D ToBox { get; private set; } = Transform3D.Identity;

    public static bool HasBox => _boxOf != 0;

    /// <summary>Bumped whenever <see cref="ToBox"/> or <see cref="HasBox"/> changes.</summary>
    public static int Version { get; private set; }

    private static uint _building;
    private static uint _boxOf;
    private static float _untilSample;

    public static void Tick(float delta)
    {
        _untilSample -= delta;
        if (_untilSample <= 0f)
        {
            _untilSample = SampleSeconds;
            if (HostBridge.HeroBuilding() is { } building && building != _building)
            {
                _building = building;
                GD.Print(building != 0
                    ? $"[weather] hero entered building 0x{building:X8}"
                    : "[weather] hero left the building");
            }

            if (_building != 0 && _boxOf != _building && TryBox(_building, out Transform3D toBox))
            {
                ToBox = toBox;
                _boxOf = _building;
                Version++;
            }
        }

        Exposure = Mathf.MoveToward(Exposure, _building != 0 ? 0f : 1f, delta / FadeSeconds);
    }

    /// <summary>Forgets the dry volume; a level's buildings do not outlive it.</summary>
    public static void Reset()
    {
        if (_boxOf != 0)
        {
            _boxOf = 0;
            Version++;
        }

        _building = 0;
        Exposure = 1f;
    }

    private static bool TryBox(uint building, out Transform3D toBox)
    {
        toBox = Transform3D.Identity;
        if (Refii.Game.GameEntities.Current?.NodeFor(building) is not { } node || !node.IsInsideTree())
        {
            return false;
        }

        bool any = false;
        Aabb bounds = default;
        if (node.Mesh is { } mesh)
        {
            bounds = mesh.GetAabb();
            any = true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D { Mesh: { } childMesh } part)
            {
                Aabb local = part.Transform * childMesh.GetAabb();
                bounds = any ? bounds.Merge(local) : local;
                any = true;
            }
        }

        if (!any || bounds.Size.X <= 0f || bounds.Size.Y <= 0f || bounds.Size.Z <= 0f)
        {
            return false;
        }

        var box = new Transform3D(Basis.FromScale(bounds.Size * 0.5f), bounds.GetCenter());
        toBox = (node.GlobalTransform * box).AffineInverse();
        GD.Print($"[weather] building 0x{building:X8} keeps {bounds.Size.X:0.0} x {bounds.Size.Y:0.0} x " +
            $"{bounds.Size.Z:0.0} m dry");
        return true;
    }
}
