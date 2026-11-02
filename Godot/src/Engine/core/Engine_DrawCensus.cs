using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// What the main view draws, by owner and material family: surfaces, the passes their material
/// chains add, and how many draws remain once Godot merges equal (mesh, surface, material) keys.
/// </summary>
/// <remarks>
/// For the debug channel's `drawcensus` op. Godot merges instances only when mesh, surface and
/// material all match; the gap between passes and distinct keys is what instancing already saves,
/// and the keys are the draws left to remove. A surface whose material repeats on another surface
/// of the same node is counted apart: those could share one surface without changing a pixel.
/// </remarks>
public static class Engine_DrawCensus
{
    private sealed class Tally
    {
        public int Nodes, Surfaces, Passes, LightPasses, SameMaterialSurfaces;
        public readonly HashSet<(ulong Mesh, int Surface, ulong Material)> Keys = new();
        public int BatchPasses;
        /// <summary>Distinct pass materials: the floor a merge by material could reach, per row.</summary>
        public readonly HashSet<ulong> Materials = new();
        /// <summary>Distinct shaders among them: what differs once per-placement values move to vertices.</summary>
        public readonly HashSet<ulong> Shaders = new();
    }

    public static Dictionary<string, object?> Describe(Node root, Camera3D camera)
    {
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        var tallies = new SortedDictionary<string, Tally>();
        var owners = new Dictionary<ulong, string>();
        var seenMaterials = new HashSet<ulong>();
        foreach (Node found in root.FindChildren("*", nameof(GeometryInstance3D), true, false))
        {
            if (found is not GeometryInstance3D g || !g.IsVisibleInTree() || (g.Layers & 1u) == 0 ||
                g.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
            {
                continue;
            }
            Mesh? mesh = g switch { MeshInstance3D mi => mi.Mesh, MultiMeshInstance3D mm => mm.Multimesh?.Mesh, _ => null };
            if (mesh is null || !Inside(planes, g.GlobalTransform * g.GetAabb()))
            {
                continue;
            }
            string owner = OwnerOf(g, owners);
            seenMaterials.Clear();
            bool batch = g is MultiMeshInstance3D;
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                Material? m = g.MaterialOverride ?? (g is MeshInstance3D mi2 ? mi2.GetSurfaceOverrideMaterial(i) : null) ?? mesh.SurfaceGetMaterial(i);
                string family = m is null ? "none" : m.GetMeta("xenos_family", "other").AsString();
                Tally t = TallyOf(tallies, owner + (batch ? " batch:" : " ") + family);
                t.Surfaces++;
                if (m is not null && !seenMaterials.Add(m.GetInstanceId()))
                {
                    t.SameMaterialSurfaces++;
                }
                int passes = 0;
                for (Material? pass = m; pass is not null && passes < 32; pass = pass.NextPass)
                {
                    passes++;
                    t.Materials.Add(pass.GetRid().Id);
                    if (pass is ShaderMaterial shaded && shaded.Shader is { } shader)
                    {
                        t.Shaders.Add(shader.GetRid().Id);
                    }
                    if (pass.HasMeta("xenos_group_variant"))
                    {
                        t.LightPasses++;
                    }
                    if (batch)
                    {
                        t.BatchPasses++;
                    }
                    else
                    {
                        t.Keys.Add((mesh.GetRid().Id, i, pass.GetRid().Id));
                    }
                }
                t.Passes += passes;
            }
            TallyOf(tallies, owner + (batch ? " batch:" : " ") + "nodes").Nodes++;
        }

        var rows = new List<Dictionary<string, object?>>();
        int passesTotal = 0, drawsTotal = 0;
        foreach ((string name, Tally t) in tallies)
        {
            int draws = t.Keys.Count + t.BatchPasses;
            passesTotal += t.Passes;
            drawsTotal += draws;
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["nodes"] = t.Nodes,
                ["surfaces"] = t.Surfaces,
                ["passes"] = t.Passes,
                ["lightPasses"] = t.LightPasses,
                ["mergedDraws"] = draws,
                ["sameMaterialSurfaces"] = t.SameMaterialSurfaces,
                ["materials"] = t.Materials.Count,
                ["shaders"] = t.Shaders.Count,
            });
        }
        return new Dictionary<string, object?>
        {
            ["passes"] = passesTotal,
            ["mergedDraws"] = drawsTotal,
            ["rows"] = rows,
        };
    }

    private static Tally TallyOf(SortedDictionary<string, Tally> tallies, string key)
    {
        if (!tallies.TryGetValue(key, out Tally? t))
        {
            t = new Tally();
            tallies[key] = t;
        }
        return t;
    }

    /// <summary>The nearest ancestor that is one of ours, by type name: who put the node there.</summary>
    internal static string OwnerOf(Node node, Dictionary<ulong, string> cache)
    {
        for (Node? at = node.GetParent(); at is not null; at = at.GetParent())
        {
            if (cache.TryGetValue(at.GetInstanceId(), out string? known))
            {
                return known;
            }
            string? ns = at.GetType().Namespace;
            if (ns is not null && ns.StartsWith("Refii", System.StringComparison.Ordinal))
            {
                cache[at.GetInstanceId()] = at.GetType().Name;
                return at.GetType().Name;
            }
        }
        return "other";
    }

    private static bool Inside(Godot.Collections.Array<Plane> planes, Aabb box)
    {
        Vector3 c = box.GetCenter(), h = box.Size * 0.5f;
        foreach (Plane plane in planes)
        {
            float r = Mathf.Abs(plane.Normal.X) * h.X + Mathf.Abs(plane.Normal.Y) * h.Y + Mathf.Abs(plane.Normal.Z) * h.Z;
            if (plane.DistanceTo(c) > r)
            {
                return false;
            }
        }
        return true;
    }
}
