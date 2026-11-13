using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The debug channel's `charcensus`: what the characters in view cost the frame against the other
/// entities, as counts the GPU time can be read against.
/// </summary>
/// <remarks>
/// <para>A CHARACTER is a rigged entity body (<see cref="IsCharacterBody"/>) and every node under it:
/// attachments, cloth, view twins and fur shells. Everything else under the entities' root is a
/// rigid entity (crates, stall parts, doors). The shader ceiling's `characters` category uses the
/// same rule, so `{"shaderCeiling":{"characters":"hide"}}` removes exactly what this counts.</para>
/// <para>IN VIEW means visible in the tree, on the camera's layers, not cast-only, its box in the
/// frustum and its distance inside its visibility range. Triangles are LOD 0's (Godot may draw a
/// coarser level; the viewport's VisiblePrims says what it drew). Vertices of skinned nodes are the
/// skinning compute's work: Godot re-skins every visible skinned surface whose skeleton moved,
/// over all its vertices whatever the LOD (mesh_storage.cpp, update_mesh_instances).</para>
/// <para>PASSES are each surface's material chain (the next passes) plus an overlay's pass; a
/// furred body's shells are one node whose chain is the shell passes.</para>
/// </remarks>
public static class AndroidCharacterCensus
{
    /// <summary>Fewest skin binds a character's rig has; rigged props (doors, signs) have a handful.</summary>
    public const int CharacterBones = Refii.Game.GameDebugServer.CharacterBones;

    /// <summary>The distance bands bodies are counted in, metres from the camera.</summary>
    private static readonly float[] Bands = { 5f, 15f, 30f, 60f };

    private static readonly Regex RenderMode = new(@"(?m)^\s*render_mode\s+([^;]*);", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="node"/> is a character's body: a mesh on a rig of at least <see cref="CharacterBones"/>
    /// binds. The shader ceiling's own rule (GameDebugServer.IsCharacterBody), so both count the same bodies.
    /// </summary>
    public static bool IsCharacterBody(Node node) => Refii.Game.GameDebugServer.IsCharacterBody(node);

    /// <summary>The distance band index of <paramref name="metres"/> (0 nearest). Pure.</summary>
    public static int BandOf(float metres)
    {
        int band = 0;
        while (band < Bands.Length && metres >= Bands[band])
        {
            band++;
        }

        return band;
    }

    /// <summary>A shader's facts, read from its text once.</summary>
    private readonly record struct ShaderFacts(string Family, bool Lite, bool TwoSided, bool Blended);

    private sealed class Tally
    {
        public int Nodes, Surfaces, Passes, LitePasses, FullPasses, TwoSidedPasses, BlendedPasses;
        public int Collapsed, Fading, Overlays, SkinnedNodes;
        public long Triangles, Vertices, SkinnedVertices;
        public readonly SortedDictionary<string, int> Families = new(StringComparer.Ordinal);

        public Dictionary<string, object?> Row() => new()
        {
            ["nodes"] = Nodes,
            ["surfaces"] = Surfaces,
            ["passes"] = Passes,
            ["litePasses"] = LitePasses,
            ["fullPasses"] = FullPasses,
            ["twoSidedPasses"] = TwoSidedPasses,
            ["blendedPasses"] = BlendedPasses,
            ["collapsedSurfaces"] = Collapsed,
            ["fadingNodes"] = Fading,
            ["overlayNodes"] = Overlays,
            ["trianglesLod0"] = Triangles,
            ["vertices"] = Vertices,
            ["skinnedNodes"] = SkinnedNodes,
            ["skinnedVertices"] = SkinnedVertices,
            ["passesByShader"] = new Dictionary<string, int>(Families),
        };
    }

    /// <summary>The census of everything under <paramref name="entities"/> that <paramref name="camera"/> draws.</summary>
    public static Dictionary<string, object?> Describe(Node entities, Camera3D camera)
    {
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        Vector3 eye = camera.GlobalPosition;
        uint mask = camera.CullMask;
        var facts = new Dictionary<ulong, ShaderFacts>();
        var owners = new Dictionary<ulong, Node?>();
        var rows = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
        var bodies = new HashSet<ulong>();
        var bands = new int[Bands.Length + 1];
        foreach (Node found in entities.FindChildren("*", nameof(GeometryInstance3D), true, false))
        {
            if (found is not GeometryInstance3D g || !g.IsVisibleInTree() || (g.Layers & mask) == 0 ||
                g.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
            {
                continue;
            }

            Mesh? mesh = g switch { MeshInstance3D mi => mi.Mesh, MultiMeshInstance3D mm => mm.Multimesh?.Mesh, _ => null };
            if (mesh is null)
            {
                continue;
            }

            Aabb box = g.GlobalTransform * g.GetAabb();
            float distance = (box.GetCenter() - eye).Length();
            if (!Inside(planes, box) || !InRange(g, distance))
            {
                continue;
            }

            Node? body = OwnerOf(g, entities, owners);
            string row = body is null ? "rigid" : ReferenceEquals(body, g) ? "character body" : "character " + PartOf(g);
            if (!rows.TryGetValue(row, out Tally? t))
            {
                t = new Tally();
                rows[row] = t;
            }

            if (body is not null && bodies.Add(body.GetInstanceId()))
            {
                bands[BandOf(((Node3D)body).GlobalPosition.DistanceTo(eye))]++;
            }

            Count(t, g, mesh, facts);
        }

        var rowList = new Dictionary<string, object?>();
        foreach ((string name, Tally t) in rows)
        {
            rowList[name] = t.Row();
        }

        var bandRows = new Dictionary<string, int>();
        for (int b = 0; b < bands.Length; b++)
        {
            string from = b == 0 ? "0" : Bands[b - 1].ToString("0");
            bandRows[b < Bands.Length ? $"{from}-{Bands[b]:0}m" : $"{from}m+"] = bands[b];
        }

        (int furred, int shells) = Refii.Models.Engine_ModernFur.Counts;
        return new Dictionary<string, object?>
        {
            ["characters"] = bodies.Count,
            ["charactersByDistance"] = bandRows,
            ["rows"] = rowList,
            ["furredInstances"] = furred,
            ["furShellPasses"] = shells,
            [AndroidFurShells.Key] = AndroidFurShells.Current,
            [AndroidCharacterCull.Key] = AndroidCharacterCull.On,
            ["bodyVariantSurfacesRemoved"] = Refii.Entities.Engine_BodyVariants.SurfacesRemoved,
            ["levers"] = AndroidCharacterLevers.Census(),
            ["rigidLevers"] = AndroidEntityProps.Census(),
        };
    }

    /// <summary>The character body <paramref name="node"/> belongs to (itself, or the nearest such ancestor), or null.</summary>
    private static Node? OwnerOf(Node node, Node root, Dictionary<ulong, Node?> cache)
    {
        var path = new List<ulong>();
        Node? owner = null;
        for (Node? at = node; at is not null && !ReferenceEquals(at, root); at = at.GetParent())
        {
            ulong id = at.GetInstanceId();
            if (cache.TryGetValue(id, out Node? known))
            {
                owner = known;
                break;
            }

            path.Add(id);
            if (IsCharacterBody(at))
            {
                owner = at;
                break;
            }
        }

        foreach (ulong id in path)
        {
            cache[id] = owner;
        }

        return owner;
    }

    /// <summary>A character part's row: fur shells, a view twin, cloth, or an attachment.</summary>
    private static string PartOf(GeometryInstance3D node) => node switch
    {
        Refii.Physics.Engine_ClothBody => "cloth",
        _ when node.Name == "FurShells" => "fur shells",
        _ when node.Name == Refii.Entities.Engine_ViewTwin.NodeName => "view twin",
        MeshInstance3D { Skin: not null } => "skinned part",
        _ => "attachment",
    };

    private static void Count(Tally t, GeometryInstance3D g, Mesh mesh, Dictionary<ulong, ShaderFacts> facts)
    {
        t.Nodes++;
        if (g.Transparency > 0f)
        {
            t.Fading++;
        }

        bool skinned = g is MeshInstance3D { Skin: not null };
        if (skinned)
        {
            t.SkinnedNodes++;
        }

        int count = mesh.GetSurfaceCount();
        for (int i = 0; i < count; i++)
        {
            Material? surfaceOverride = (g as MeshInstance3D)?.GetSurfaceOverrideMaterial(i);
            if (Refii.Entities.Engine_HiddenClusters.IsCollapsed(surfaceOverride))
            {
                t.Collapsed++;
                continue;
            }

            t.Surfaces++;
            if (mesh is ArrayMesh arrays)
            {
                int vertices = arrays.SurfaceGetArrayLen(i);
                int indices = arrays.SurfaceGetArrayIndexLen(i);
                t.Triangles += (indices > 0 ? indices : vertices) / 3;
                t.Vertices += vertices;
                if (skinned)
                {
                    t.SkinnedVertices += vertices;
                }
            }

            Material? material = g.MaterialOverride ?? surfaceOverride ?? mesh.SurfaceGetMaterial(i);
            int guard = 0;
            for (Material? pass = material; pass is not null && guard++ < 32; pass = pass.NextPass)
            {
                CountPass(t, pass, facts);
            }
        }

        if (g.MaterialOverlay is { } overlay)
        {
            t.Overlays++;
            for (int i = 0; i < count; i++)
            {
                CountPass(t, overlay, facts);
            }
        }
    }

    private static void CountPass(Tally t, Material pass, Dictionary<ulong, ShaderFacts> facts)
    {
        t.Passes++;
        if (pass is not ShaderMaterial { Shader: { } shader })
        {
            t.Families["(not a shader material)"] = t.Families.GetValueOrDefault("(not a shader material)") + 1;
            return;
        }

        ShaderFacts f = FactsOf(shader, facts);
        t.Families[f.Family] = t.Families.GetValueOrDefault(f.Family) + 1;
        if (f.Lite)
        {
            t.LitePasses++;
        }
        else
        {
            t.FullPasses++;
        }

        if (f.TwoSided)
        {
            t.TwoSidedPasses++;
        }

        if (f.Blended)
        {
            t.BlendedPasses++;
        }
    }

    private static ShaderFacts FactsOf(Shader shader, Dictionary<ulong, ShaderFacts> cache)
    {
        ulong id = shader.GetInstanceId();
        if (cache.TryGetValue(id, out ShaderFacts known))
        {
            return known;
        }

        string path = Refii.Models.Engine_ShaderSpecialisation.PathOf(shader);
        string full = Refii.Models.Engine_ShaderVariant.FullPathOf(path);
        string code = shader.Code ?? string.Empty;
        Match mode = RenderMode.Match(code);
        string words = mode.Success ? mode.Groups[1].Value : string.Empty;
        string family = (full.Length != 0 ? System.IO.Path.GetFileNameWithoutExtension(full) : "(built in code)") +
            (Refii.Models.Engine_ShaderSpecialisation.DefinesOf(shader).Count != 0 ? " +spec" : string.Empty);
        var result = new ShaderFacts(
            family,
            Lite: path != full,
            TwoSided: Regex.IsMatch(words, @"\bcull_disabled\b"),
            Blended: Regex.IsMatch(words, @"\b(depth_draw_never|blend_add|blend_sub|blend_mul|blend_premul_alpha)\b"));
        cache[id] = result;
        return result;
    }

    /// <summary>Inside the node's visibility range, when it states one.</summary>
    private static bool InRange(GeometryInstance3D g, float distance)
    {
        if (g.VisibilityRangeBegin > 0f && distance < g.VisibilityRangeBegin - g.VisibilityRangeBeginMargin)
        {
            return false;
        }

        return g.VisibilityRangeEnd <= 0f || distance <= g.VisibilityRangeEnd + g.VisibilityRangeEndMargin;
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
