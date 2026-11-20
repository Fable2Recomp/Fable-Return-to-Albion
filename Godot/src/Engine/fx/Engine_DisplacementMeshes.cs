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
/// any stands; a frame a view would draw for static meshes outside its frustum alone is taken
/// back in that frame (<see cref="Engine_FxDisplacement.SleepUnseen"/>).
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
        /// <summary>The mesh's own bounds, as the renderer culls it (no vertex shader moves it).</summary>
        public Aabb Bounds;
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

    /// <summary>
    /// Margin on every frustum plane, in metres, over the renderer's own cull of the same box: the
    /// test only ever keeps the layer awake for longer.
    /// </summary>
    public const float FrustumSlack = 1f;

    /// <summary>
    /// Whether <paramref name="local"/> under <paramref name="placed"/> can touch the volume inside
    /// ALL of <paramref name="planes"/> (outward-facing, <see cref="Camera3D.GetFrustum"/>), within
    /// <see cref="FrustumSlack"/>. Conservative: the world box of a rotated box only grows.
    /// </summary>
    public static bool BoxTouchesFrustum(Aabb local, Transform3D placed, Plane[] planes)
    {
        Aabb world = placed * local;
        Vector3 centre = world.GetCenter();
        Vector3 half = world.Size * 0.5f;
        foreach (Plane plane in planes)
        {
            Vector3 n = plane.Normal;
            float reach = Mathf.Abs(n.X) * half.X + Mathf.Abs(n.Y) * half.Y + Mathf.Abs(n.Z) * half.Z;
            if (plane.DistanceTo(centre) - reach > FrustumSlack)
            {
                return false;
            }
        }
        return true;
    }

    private static readonly Plane[] Frustum = new Plane[6];
    private static readonly System.Func<Camera3D, bool> SeenBy = AnyStaticShownIn;

    /// <summary>
    /// Whether a shown static mesh, as this frame's pump left its instance, touches the frustum of
    /// <paramref name="camera"/> (a layer view's own camera). True when the frustum cannot be read.
    /// </summary>
    private static bool AnyStaticShownIn(Camera3D camera)
    {
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        // Freed here, not left to the finalizer: one array a frame while a static mesh stands.
        using (var handle = (Godot.Collections.Array)planes)
        {
            if (planes.Count != Frustum.Length)
            {
                return true;
            }
            for (int i = 0; i < Frustum.Length; i++)
            {
                Frustum[i] = planes[i];
            }
        }
        foreach (Held held in Meshes)
        {
            if (!held.Skinned && held.Instance is not null && held.Visible &&
                BoxTouchesFrustum(held.Bounds, held.Placed, Frustum))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Keeps each mesh's layer instance with its node and keeps the layer awake. A shown skinned
    /// part wakes it as before; a shown static mesh wakes it too, but each view's frame is taken
    /// back when no static mesh is in that view's frustum (<see cref="Engine_FxDisplacement.SleepUnseen"/>):
    /// tested here, after the instances are placed for this frame, against the camera the view
    /// copied this frame. Main thread, once a frame.
    /// </summary>
    public static void Pump()
    {
        if (Meshes.Count == 0)
        {
            return;
        }
        Rid scenario = Engine_FxDisplacement.Scenario;
        int shownSkinned = 0;
        int shownStatic = 0;
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
                held.Bounds = held.Mesh.GetAabb();
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
                if (visible)
                {
                    // A skinned part's bounds follow its skeleton, which this does not know.
                    if (held.Skinned && Refii.AndroidRuntime.AndroidDisplacementGate.TakesBody(held.Node, held.Mesh, held.Placed)) continue;
                    if (held.Skinned) shownSkinned++;
                    else shownStatic++;
                    if (!held.Skinned) Refii.AndroidRuntime.AndroidDisplacementRect.NoteStatic(held.Bounds, held.Placed);
                }
            }
        }
        if (shownSkinned > 0)
        {
            Engine_FxDisplacement.NotifyActive();
        }
        Refii.AndroidRuntime.AndroidDisplacementGate.HoldIfSeen();
        Engine_FxDisplacement.SleepUnseen(SeenBy);
        Refii.AndroidRuntime.AndroidDisplacementGate.NoteStatic(shownStatic);
        if (shownStatic > 0)
        {
            Engine_FxDisplacement.NotifyStaticShown();
        }
    }

    private static readonly Dictionary<ulong, ShaderMaterial> MeshMaterials = new();

    /// <summary>
    /// fx_mesh_disp over the mesh's own diffuse (its first surface's albedo); one per albedo, as
    /// the material holds nothing else and is never written after it is made. The material keeps
    /// its albedo alive, so the key's instance id is never reused while it is held.
    /// </summary>
    private static ShaderMaterial MaterialFor(Mesh mesh)
    {
        Texture2D? albedo = mesh.GetSurfaceCount() > 0 && mesh.SurfaceGetMaterial(0) is ShaderMaterial source
            ? source.GetShaderParameter(AlbedoName).As<Texture2D>()
            : null;
        ulong key = albedo?.GetInstanceId() ?? 0ul;
        if (!MeshMaterials.TryGetValue(key, out ShaderMaterial? material))
        {
            _shader ??= GD.Load<Shader>(FableFx.MeshDispShaderPath);
            material = new ShaderMaterial { Shader = _shader };
            if (albedo is not null)
            {
                material.SetShaderParameter(AlbedoName, albedo);
            }
            MeshMaterials[key] = material;
        }
        return material;
    }
}
