using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// The roofs the classic rain and snow are cut at: the guest's roof list, built the way its own
/// rain and snow draws build it every frame (`sub_82A789B0`), which in this host never run.
/// </summary>
/// <remarks>
/// THE TITLE'S RULE (verified 2026-10-15): a roof is a STATIC_MESH twin whose flag word carries
/// 0x2000 (twin+8 bit 14, `sub_82A78528` 0x82A78694) — the interior record of a building's
/// multi-mesh appearance (`sub_828F9BA0` 0x828F9E10 from the record's GDB `Interior`; level
/// placements never carry it). Kept when its world box meets the query box widened by 10 and each
/// side of its model's own box is within [1, 150]; at most 128. Per roof: the model box's top
/// corners (max, max), (min, max), (min, min), (max, min) at max.z through the instance's matrix,
/// the top = the highest corner's z (floored at 0, the entry's start value), and the corners' xy
/// box. Then the first roof over the eye (above it and containing its xy) is swapped to slot 0.
///
/// HERE the buildings are the entity nodes the entity feed makes (their node holds the guest's
/// placement), the interior records come from the guest's component
/// (<see cref="Refii.Host.HostBridge.GetEntityInteriorMeshes"/>) and each model's box from its
/// file header.
/// </remarks>
public static class Engine_ClassicRoofs
{
    public const int MaxRoofs = 128;
    private const float Widen = 10f;
    private const float MinSide = 1f, MaxSide = 150f;

    /// <summary>One roof: corners in the guest's xy, the top, and the corners' xy box (z folded with 0).</summary>
    public struct Roof
    {
        public Vector2 C0, C1, C2, C3;
        public float Top;
        public Vector3 Min, Max;

        public readonly Vector2 Corner(int i) => i switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };
    }

    private sealed class Building
    {
        public required Node3D Node;
        public readonly List<(Vector3 Min, Vector3 Max)> Boxes = new();
        public Transform3D CachedWorld;
        public bool HasCachedWorld;
        public readonly List<(Vector3 WMin, Vector3 WMax, Roof Roof)> CachedRoofs = new();
    }

    private static readonly Dictionary<uint, Building> Buildings = new();
    private static readonly Dictionary<uint, (Vector3 Min, Vector3 Max)?> ModelBoxes = new();
    private static readonly List<uint> Gone = new();

    /// <summary>The roof list of the last <see cref="Build"/>.</summary>
    public static readonly List<Roof> Roofs = new();

    /// <summary>Buildings known.</summary>
    public static int Count => Buildings.Count;

    /// <summary>Monotonically incremented whenever buildings are added, removed, or moved.</summary>
    public static int Version { get; private set; }

    /// <summary>An entity's node, once made; one with interior records becomes a building. Main thread.</summary>
    public static void Track(uint entity, Node3D node)
    {
        // Guest data, read for either renderer: the classic weather and the modern one both cut at it.
        uint[] interiors = Refii.Host.HostBridge.GetEntityInteriorMeshes(entity);
        if (interiors.Length == 0)
        {
            if (Buildings.Remove(entity))
            {
                ++Version;
            }
            return;
        }
        var building = new Building { Node = node };
        foreach (uint hash in interiors)
        {
            if (BoxOf(hash) is { } box)
            {
                building.Boxes.Add(box);
            }
        }
        if (building.Boxes.Count != 0)
        {
            Buildings[entity] = building;
            ++Version;
        }
    }

    /// <summary>A model's own box from its file header (min xyz, max xyz), in the file's axes.</summary>
    private static (Vector3 Min, Vector3 Max)? BoxOf(uint hash)
    {
        if (ModelBoxes.TryGetValue(hash, out (Vector3, Vector3)? known))
        {
            return known;
        }
        (Vector3, Vector3)? box = null;
        string? path = Engine_PropModelIndex.Resolve(hash, Array.Empty<string>());
        if (path is not null && Refii.Models.Engine_BnkMeshLoader.ReadBytes(path) is { } bytes &&
            Refii.Models.Engine_MdlData.TryParseInfo(bytes, path, out Refii.Models.MdlInfo info, out _) &&
            info.FileBoundingBox.Length == 6)
        {
            float[] b = info.FileBoundingBox;
            var a = new Vector3(b[0], b[1], b[2]);
            var c = new Vector3(b[3], b[4], b[5]);
            box = (new Vector3(MathF.Min(a.X, c.X), MathF.Min(a.Y, c.Y), MathF.Min(a.Z, c.Z)),
                new Vector3(MathF.Max(a.X, c.X), MathF.Max(a.Y, c.Y), MathF.Max(a.Z, c.Z)));
        }
        ModelBoxes[hash] = box;
        return box;
    }

    /// <summary>
    /// Builds <see cref="Roofs"/> for a query box (guest axes) and the eye (guest axes), as
    /// `sub_82A789B0` does. Main thread.
    /// </summary>
    public static void Build(Vector3 queryMin, Vector3 queryMax, Vector3 eye)
    {
        Roofs.Clear();
        queryMin -= new Vector3(Widen, Widen, Widen);
        queryMax += new Vector3(Widen, Widen, Widen);
        Gone.Clear();
        foreach ((uint entity, Building building) in Buildings)
        {
            if (!GodotObject.IsInstanceValid(building.Node) || !building.Node.IsInsideTree())
            {
                Gone.Add(entity);
                continue;
            }
            EnsureCachedWorld(building);
            for (int r = 0; r < building.CachedRoofs.Count; r++)
            {
                if (Roofs.Count >= MaxRoofs)
                {
                    break;
                }
                (Vector3 wMin, Vector3 wMax, Roof roof) = building.CachedRoofs[r];
                if (wMax.X < queryMin.X || wMin.X > queryMax.X || wMax.Y < queryMin.Y || wMin.Y > queryMax.Y ||
                    wMax.Z < queryMin.Z || wMin.Z > queryMax.Z)
                {
                    continue;
                }
                Roofs.Add(roof);
            }
        }
        if (Gone.Count != 0)
        {
            foreach (uint entity in Gone)
            {
                Buildings.Remove(entity);
            }
            ++Version;
        }

        // The roof over the eye leads (from index 1: slot 0 is already the first).
        for (int i = 1; i < Roofs.Count; i++)
        {
            Roof r = Roofs[i];
            if (r.Top > eye.Z && Contains(r, new Vector2(eye.X, eye.Y)))
            {
                (Roofs[0], Roofs[i]) = (Roofs[i], Roofs[0]);
                break;
            }
        }
    }

    /// <summary>Inside the corners as listed: no edge has the point on its positive side.</summary>
    public static bool Contains(in Roof roof, Vector2 p)
    {
        for (int i = 0; i < 4; i++)
        {
            Vector2 c = roof.Corner(i), n = roof.Corner((i + 1) & 3);
            if ((n.Y - c.Y) * (p.X - c.X) - (n.X - c.X) * (p.Y - c.Y) > 0f)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Collects all cached roofs whose bounding box overlaps the given query box (guest axes).
    /// Used by Engine_DynamicRoofMap for area-wide sheltering without frustum/slot-count limits.
    /// </summary>
    public static void CollectRoofsInBox(Vector3 queryMin, Vector3 queryMax, List<Roof> destination)
    {
        destination.Clear();
        foreach ((_, Building building) in Buildings)
        {
            if (!GodotObject.IsInstanceValid(building.Node) || !building.Node.IsInsideTree())
            {
                continue;
            }
            EnsureCachedWorld(building);
            for (int r = 0; r < building.CachedRoofs.Count; r++)
            {
                (Vector3 wMin, Vector3 wMax, Roof roof) = building.CachedRoofs[r];
                if (wMax.X < queryMin.X || wMin.X > queryMax.X ||
                    wMax.Y < queryMin.Y || wMin.Y > queryMax.Y ||
                    wMax.Z < queryMin.Z || wMin.Z > queryMax.Z)
                {
                    continue;
                }
                destination.Add(roof);
            }
        }
    }

    private static void EnsureCachedWorld(Building building)
    {
        if (building.HasCachedWorld)
        {
            return;
        }
        Transform3D world = building.Node.GlobalTransform;
        building.CachedWorld = world;
        building.HasCachedWorld = true;
        building.CachedRoofs.Clear();
        ++Version;
        ReadOnlySpan<int> order = stackalloc int[] { 0, 1, 3, 2 };
        foreach ((Vector3 min, Vector3 max) in building.Boxes)
        {
            Vector3 d = max - min;
            if (d.X < MinSide || d.Y < MinSide || d.Z < MinSide || d.X > MaxSide || d.Y > MaxSide || d.Z > MaxSide)
            {
                continue;
            }
            Vector3 wMin = new(float.MaxValue, float.MaxValue, float.MaxValue), wMax = new(float.MinValue, float.MinValue, float.MinValue);
            for (int k = 0; k < 8; k++)
            {
                var file = new Vector3((k & 1) != 0 ? max.X : min.X, (k & 2) != 0 ? max.Y : min.Y, (k & 4) != 0 ? max.Z : min.Z);
                Vector3 g = ToGuest(world * FileToModel(file));
                wMin = wMin.Min(g);
                wMax = wMax.Max(g);
            }
            var roof = new Roof { Top = 0f };
            for (int k = 0; k < 4; k++)
            {
                int flag = order[k];
                var file = new Vector3((flag & 1) != 0 ? min.X : max.X, (flag & 2) != 0 ? min.Y : max.Y, max.Z);
                Vector3 g = ToGuest(world * FileToModel(file));
                var xy = new Vector2(g.X, g.Y);
                switch (k)
                {
                    case 0: roof.C0 = xy; break;
                    case 1: roof.C1 = xy; break;
                    case 2: roof.C2 = xy; break;
                    default: roof.C3 = xy; break;
                }
                if (roof.Top < g.Z)
                {
                    roof.Top = g.Z;
                }
                if (k == 0)
                {
                    roof.Min = g;
                    roof.Max = g;
                }
                else
                {
                    var p = new Vector3(g.X, g.Y, 0f);
                    roof.Min = roof.Min.Min(p);
                    roof.Max = roof.Max.Max(p);
                }
            }
            building.CachedRoofs.Add((wMin, wMax, roof));
        }
    }

    private static Vector3 FileToModel(Vector3 file) => new(file.X, file.Z, -file.Y);

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);
}
