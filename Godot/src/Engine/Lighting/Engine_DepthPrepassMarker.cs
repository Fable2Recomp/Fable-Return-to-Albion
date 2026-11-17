using Godot;

namespace Refii.Lighting;

/// <summary>
/// Keeps Godot's depth pre-pass in the views that need it and out of the ones that do not.
/// </summary>
/// <remarks>
/// The pre-pass is one project setting (`rendering/driver/depth_prepass/enable`), and with it on
/// every render draws its opaque list twice: depth first, then colour over the same list. The
/// main view needs it (the Pre-Opaque compositor effects read the depth before the opaque pass:
/// the classic screen shadow, the FX depth capture), but the classic sun cascades render a world
/// of casters whose depth alone is kept, and their colour pass writes the same depth by itself.
/// So the setting is off, and the main world carries this: one surface, always in view, that
/// uses the stencil in the opaque list, which makes Forward+ run the pre-pass for that render
/// (`RenderForwardClustered::_render_scene`: `force_depth_pre_pass = used_opaque_stencil`). It
/// discards every fragment and writes nothing. Worlds without it (the cascades) skip the pre-pass.
/// </remarks>
public static class Engine_DepthPrepassMarker
{
    private const string ShaderPath = "res://src/Engine/Lighting/depth_prepass_marker.gdshader";
    private static readonly StringName NodeName = "DepthPrepassMarker";
    private static bool _enabled = true;

    /// <summary>
    /// Enables or disables the forced Forward+ stencil depth pre-pass marker across active views
    /// (used by <c>dx12_skip_3d=depthprepass</c> isolation).
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            SyncAttached();
        }
    }

    private static readonly System.Collections.Generic.List<MeshInstance3D> AttachedMarkers = new();

    /// <summary>
    /// Returns true only when the marker is enabled AND Godot's native depth pre-pass setting is disabled
    /// (i.e. Classic mode, where sun cascade SubViewports disable global depth pre-pass).
    /// </summary>
    public static bool ShouldEnableMarker(bool enabled, bool nativeDepthPrepassEnabled) =>
        enabled && !nativeDepthPrepassEnabled;

    private static bool NativeDepthPrepassEnabled =>
        ProjectSettings.GetSetting("rendering/driver/depth_prepass/enable", true).AsBool();

    /// <summary>
    /// Whether the marker quad is effectively visible (enabled and native depth pre-pass disabled).
    /// </summary>
    public static bool EffectiveVisible => ShouldEnableMarker(_enabled, NativeDepthPrepassEnabled);

    private static void SyncAttached()
    {
        bool visible = EffectiveVisible;
        for (int i = AttachedMarkers.Count - 1; i >= 0; i--)
        {
            MeshInstance3D marker = AttachedMarkers[i];
            if (!GodotObject.IsInstanceValid(marker))
            {
                AttachedMarkers.RemoveAt(i);
                continue;
            }
            marker.Visible = visible;
        }
    }

    /// <summary>Puts the marker under <paramref name="parent"/> (any node in the main world). Idempotent.</summary>
    public static void Attach(Node3D parent)
    {
        if (NativeDepthPrepassEnabled || parent.HasNode(NodeName.ToString()) || GD.Load<Shader>(ShaderPath) is not { } shader)
        {
            return;
        }
        var quad = new QuadMesh { Size = new Vector2(0.01f, 0.01f) };
        quad.SurfaceSetMaterial(0, new ShaderMaterial { Shader = shader });
        var marker = new MeshInstance3D
        {
            Name = NodeName,
            Mesh = quad,
            Visible = EffectiveVisible,
            // Every view of this world: it is never culled, and it draws nothing.
            Layers = 0xFFFFF,
            CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            IgnoreOcclusionCulling = true,
        };
        AttachedMarkers.Add(marker);
        parent.AddChild(marker);
    }
}
