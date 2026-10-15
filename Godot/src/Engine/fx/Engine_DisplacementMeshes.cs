using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// Static displacement meshes (a placement whose static mesh states UseDisplacement — heat haze,
/// shimmer) for the modern renderer: drawn into the displacement layer's own world
/// (<see cref="Engine_FxDisplacement"/>) on fx_mesh_disp, and out of the main view.
/// </summary>
/// <remarks>
/// The title draws such a mesh in its displacement renderer alone, its own diffuse as the offset
/// (Engine_ClassicDisplacement). The level build notes the records
/// (Engine_ClassicDisplacement.NoteRecord); the prop streamer hands each node over once its mesh
/// is in. The layer renders only on frames something states it active, so this pump does while
/// any stands.
/// </remarks>
public static class Engine_DisplacementMeshes
{
    private static readonly StringName AlbedoName = "albedo_tex";

    private sealed class Held
    {
        public required MeshInstance3D Node;
        public required Mesh Mesh;
        public Engine_FxLayerInstance? Instance;
        public ShaderMaterial? Material;
        public Transform3D Placed;
        public bool Visible;
        /// <summary>A body's displacement parts: skinned on the body's skeleton, each surface on its own material.</summary>
        public bool Skinned;
    }

    private static readonly List<Held> Meshes = new();
    private static Shader? _shader;

    /// <summary>
    /// Takes <paramref name="node"/> into the displacement layer when it draws a noted record.
    /// False for any other. Main thread.
    /// </summary>
    public static bool Adopt(uint record, MeshInstance3D node, Mesh mesh)
    {
        if (Refii.Models.Engine_ClassicMaterial.Active || !Refii.Models.Engine_ClassicDisplacement.IsNoted(record))
        {
            return false;
        }
        // Out of the main view (its camera leaves the layer's bit out) and out of every shadow.
        node.Layers = Engine_FxDisplacement.LayerMask;
        node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        Engine_FxDisplacement.Ensure(node);
        Meshes.Add(new Held { Node = node, Mesh = mesh });
        return true;
    }

    /// <summary>
    /// A composite's displacement-only parts (GameEntities.AttachDisplacementParts): drawn into the
    /// layer on the body's skeleton, its surfaces on <see cref="PartMaterial"/>. Main thread.
    /// </summary>
    public static void AdoptParts(MeshInstance3D body, ArrayMesh parts)
    {
        Engine_FxDisplacement.Ensure(body);
        Meshes.Add(new Held { Node = body, Mesh = parts, Skinned = true });
    }

    private static readonly Dictionary<ulong, ShaderMaterial> PartMaterials = new();

    /// <summary>fx_mesh_disp over a body surface's own diffuse; one per source material.</summary>
    public static ShaderMaterial PartMaterial(Material? surface)
    {
        ulong key = surface?.GetInstanceId() ?? 0ul;
        if (!PartMaterials.TryGetValue(key, out ShaderMaterial? held))
        {
            _shader ??= GD.Load<Shader>(FableFx.MeshDispShaderPath);
            held = new ShaderMaterial { Shader = _shader };
            if (surface is ShaderMaterial source && source.GetShaderParameter(AlbedoName).As<Texture2D>() is { } albedo)
            {
                held.SetShaderParameter(AlbedoName, albedo);
            }
            PartMaterials[key] = held;
        }
        return held;
    }

    /// <summary>Keeps each mesh's layer instance with its node and keeps the layer awake. Main thread, once a frame.</summary>
    public static void Pump()
    {
        if (Meshes.Count == 0)
        {
            return;
        }
        Rid scenario = Engine_FxDisplacement.Scenario;
        int shown = 0;
        for (int i = Meshes.Count - 1; i >= 0; i--)
        {
            Held held = Meshes[i];
            if (!GodotObject.IsInstanceValid(held.Node) || !held.Node.IsInsideTree())
            {
                held.Instance?.Free();
                Meshes.RemoveAt(i);
                continue;
            }
            if (held.Instance is null && scenario.IsValid)
            {
                held.Material = held.Skinned ? null : MaterialFor(held.Mesh);
                held.Placed = held.Node.GlobalTransform;
                held.Visible = held.Node.IsVisibleInTree();
                held.Instance = Engine_FxLayerInstance.Create(held.Mesh.GetRid(), scenario, Engine_FxDisplacement.LayerMask,
                    0f, held.Placed, held.Visible, held.Material?.GetRid() ?? default);
                if (held.Skinned && held.Instance is not null && held.Node.GetSkinReference() is { } skin)
                {
                    RenderingServer.InstanceAttachSkeleton(held.Instance.Rid, skin.GetSkeleton());
                }
            }
            if (held.Instance is not null)
            {
                // Written only when they change: a placement stands still.
                Transform3D placed = held.Node.GlobalTransform;
                if (placed != held.Placed)
                {
                    held.Placed = placed;
                    held.Instance.SetTransform(placed);
                }
                bool visible = held.Node.IsVisibleInTree();
                if (visible != held.Visible)
                {
                    held.Visible = visible;
                    held.Instance.SetVisible(visible);
                }
                shown += visible ? 1 : 0;
            }
        }
        if (shown > 0)
        {
            Engine_FxDisplacement.NotifyActive();
        }
    }

    /// <summary>fx_mesh_disp over the mesh's own diffuse (its first surface's albedo).</summary>
    private static ShaderMaterial MaterialFor(Mesh mesh)
    {
        _shader ??= GD.Load<Shader>(FableFx.MeshDispShaderPath);
        var material = new ShaderMaterial { Shader = _shader };
        if (mesh.GetSurfaceCount() > 0 && mesh.SurfaceGetMaterial(0) is ShaderMaterial source &&
            source.GetShaderParameter(AlbedoName).As<Texture2D>() is { } albedo)
        {
            material.SetShaderParameter(AlbedoName, albedo);
        }
        return material;
    }
}
