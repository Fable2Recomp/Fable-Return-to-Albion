using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's SHADOW SPHERES: the soft darkening the title multiplies into the scene
/// under the hero, the dog and the villagers, drawn with its own SPHERE_SSAO pair.
/// </summary>
/// <remarks>
/// THE TITLE'S CONTRACT (TU1, verified):
///   - `Render_EntityShadowSphere_Update` 0x8222A8A8 states one sphere per such entity: radius
///     0.5 (hero, dog) or 0.4 (villager), centre the entity + 0.1 up, moved every tick; the
///     render scene keeps it as a category-36 object with its last two keyframes
///     (HostBridge.GetClassicShadowSpheres).
///   - The draw `sub_821E9F60` (scene category SHADOW_SPHERE, after the opaque scene and before
///     decals and particles): only within 20 m of the eye, g_Intensity (c47) = clamp(20 - d, 0, 1),
///     g_Contrast (c46) = 0.25, g_Sphere (c31) = (centre, radius), g_InverseViewProjection, the
///     resolved depth (s13, 1 - z), VS 180 / PS 181 over a unit sphere scaled by the radius,
///     blended DESTCOLOR / ZERO with no depth write: a multiply of the scene.
/// HERE: one node per sphere on the main view, its row carrying c31/c46/c47; Godot's own depth is
/// the title's 1 - z. The two keyframes are blended by the playhead every drawn entity uses
/// (Engine_DrawnFrame), where the title blends them by its render clock. Back faces with no depth
/// test, so the sphere covers its pixels with the eye inside it too; the pixel shader decides
/// what darkens from the scene depth.
/// </remarks>
public partial class Engine_ClassicShadowSpheres : Node
{
    private const float Reach = 20f;
    private const int MaxSpheres = 128;
    private const int SphereRegister = 31, ContrastRegister = 46, IntensityRegister = 47;
    private const string VertexEntry = "VSHADER_SPHERE_SSAO";
    private const string PixelEntry = "PSHADER_SPHERE_SSAO";
    private const string RenderMode =
        "unshaded, blend_mul, depth_draw_never, depth_test_disabled, cull_front, fog_disabled, shadows_disabled";

    private static readonly StringName RowName = "xr_light_row";
    private static readonly StringName InverseViewProjectionName = "ps_g_InverseViewProjection";

    private readonly float[] _raw = new float[MaxSpheres * Refii.Host.HostBridge.ClassicShadowSphereStride];
    private readonly List<(MeshInstance3D Node, int Row)> _pool = new();
    private readonly Vector4[] _inverse = new Vector4[4];
    private readonly Godot.Collections.Array _inverseRows = new() { Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero };
    private readonly float[] _four = new float[4];
    private ShaderMaterial? _material;
    private SphereMesh? _mesh;
    private bool _reportedMissing;

    /// <summary>The live instance.</summary>
    public static Engine_ClassicShadowSpheres? Instance { get; private set; }

    /// <summary>Spheres drawn on the last frame.</summary>
    public static int Drawn { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicShadowSpheres";
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        foreach ((MeshInstance3D _, int row) in _pool)
        {
            Engine_ClassicConstants.ReleaseRow(row);
        }
        _pool.Clear();
    }

    private bool EnsureMaterial()
    {
        if (_material is not null)
        {
            return true;
        }
        Shader? shader = Engine_ClassicShaders.Compose(VertexEntry, PixelEntry, RenderMode);
        if (shader is null)
        {
            if (!_reportedMissing)
            {
                _reportedMissing = true;
                GD.Print("[classic] shadow spheres: no " + VertexEntry + " / " + PixelEntry + " pair; none drawn");
            }
            return false;
        }
        // The title's unit sphere is 144 vertices, 256 triangles (16 segments, 9 rings).
        _mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 16, Rings = 8 };
        _material = new ShaderMaterial { Shader = shader, RenderPriority = -100 };
        return true;
    }

    /// <summary>Draws this frame's spheres from the main camera. Main thread, after the frame feed.</summary>
    public void Pump(Camera3D camera)
    {
        uint count = Refii.Host.HostBridge.GetClassicShadowSpheres(_raw);
        if (count == 0 || !EnsureMaterial())
        {
            Hide(0);
            Drawn = 0;
            return;
        }
        Engine_ClassicViews.InverseViewProjection(camera, _inverse);
        for (int i = 0; i < 4; i++)
        {
            _inverseRows[i] = _inverse[i];
        }
        _material!.SetShaderParameter(InverseViewProjectionName, _inverseRows);

        float alpha = Math.Clamp(Refii.Entities.Engine_DrawnFrame.Alpha, 0f, 1f);
        if (!float.IsFinite(alpha))
        {
            alpha = 1f;
        }
        Vector3 eye = Engine_ClassicShadows.ToGuest(camera.GlobalPosition);
        const int s = Refii.Host.HostBridge.ClassicShadowSphereStride;
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            int at = i * s;
            var previous = new Vector3(_raw[at], _raw[at + 1], _raw[at + 2]);
            var newest = new Vector3(_raw[at + 4], _raw[at + 5], _raw[at + 6]);
            Vector3 centre = previous.Lerp(newest, alpha);
            float radius = Mathf.Lerp(_raw[at + 3], _raw[at + 7], alpha);
            float distance = centre.DistanceTo(eye);
            if (!(radius > 0f) || !centre.IsFinite() || distance > Reach)
            {
                continue;
            }
            if (!Take(used, out MeshInstance3D node, out int row))
            {
                break;
            }
            ++used;
            _four[0] = centre.X; _four[1] = centre.Y; _four[2] = centre.Z; _four[3] = radius;
            Engine_ClassicConstants.SetRowBlock(true, row, SphereRegister, _four);
            _four[0] = _four[1] = _four[2] = _four[3] = 0.25f;
            Engine_ClassicConstants.SetRowBlock(true, row, ContrastRegister, _four);
            float intensity = Math.Clamp(Reach - distance, 0f, 1f);
            _four[0] = _four[1] = _four[2] = _four[3] = intensity;
            Engine_ClassicConstants.SetRowBlock(true, row, IntensityRegister, _four);
            node.GlobalTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(radius, radius, radius)),
                Engine_ClassicShadows.ToGodot(centre));
            node.Visible = true;
        }
        Hide(used);
        Drawn = used;
    }

    /// <summary>The pool's node <paramref name="index"/> with its own row, made on first use.</summary>
    private bool Take(int index, out MeshInstance3D node, out int row)
    {
        if (index < _pool.Count)
        {
            (node, row) = _pool[index];
            return true;
        }
        row = Engine_ClassicConstants.AcquireRow();
        if (row == 0)
        {
            node = null!;
            return false;
        }
        node = new MeshInstance3D
        {
            Mesh = _mesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = 1u,
        };
        AddChild(node);
        node.SetInstanceShaderParameter(RowName, row);
        _pool.Add((node, row));
        return true;
    }

    private void Hide(int from)
    {
        for (int i = from; i < _pool.Count; i++)
        {
            _pool[i].Node.Visible = false;
        }
    }
}
