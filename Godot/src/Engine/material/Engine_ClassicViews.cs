using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// The views the world is drawn through this frame, as the classic renderer and the guest feeds
/// that follow a camera see them: one, or two on split screen (Refii.Core.Engine_Views).
/// </summary>
/// <remarks>
/// <para>VIEW 0 IS THE CAMERA THAT DRAWS PLAYER ONE. Without split screen that is the window's
/// camera; on split screen the window draws no 3D and player one's SubViewport camera does — at
/// the window camera's placement, but with its own aspect and field of view — so every
/// view-space value of view 0 (a projection, a target size) is taken from it. View 1 is player
/// two's camera (<see cref="Refii.Core.Engine_Views.Second"/>).</para>
/// <para>ONE UPLOAD, A BLOCK PER VIEW. The two views render one after the other on the render
/// thread from what the main thread published once; nothing the main thread writes can differ
/// between them. So a value that belongs to the view is written once per view into its own block
/// of the constant file (<see cref="Engine_ClassicConstants.SetFrameView"/>), and the stage picks
/// the block by the camera drawing it: view 1's camera carries <see cref="SecondViewLayer"/> in
/// its cull mask (no node is on that layer, so it changes nothing that is drawn), and
/// xenos_common.gdshaderinc reads it from <c>CAMERA_VISIBLE_LAYERS</c>, which Godot fills per
/// render pass from the camera of that pass.</para>
/// <para>WHAT IS NOT PER VIEW TAKES BOTH. A cut or fade a node carries (it cannot differ between
/// two cameras that draw the same node) is decided by the nearest eye: what either player is
/// close to is drawn (<see cref="NearestEyeDistanceSquared"/>).</para>
/// </remarks>
public static class Engine_ClassicViews
{
    /// <summary>XR_SECOND_VIEW_LAYER in xenos_common.gdshaderinc: layer 29, on no node.</summary>
    public const uint SecondViewLayer = 1u << 28;

    /// <summary>The most views a frame draws.</summary>
    public const int Max = 2;

    /// <summary>Layer 30: drawn by player one's view alone while split (a view's own rain and snow).</summary>
    public const uint FirstViewOnlyLayer = 1u << 29;

    /// <summary>Layer 31: drawn by player two's view alone while split.</summary>
    public const uint SecondViewOnlyLayer = 1u << 30;

    /// <summary>
    /// Layer 12: drawn by player one's view alone while split, and lit as an interior
    /// (Engine_LightZones gives it to indoor lights and keeps it from outdoor lights and the sun).
    /// </summary>
    public const uint FirstViewIndoorLayer = 1u << 11;

    /// <summary>Layer 13: <see cref="FirstViewIndoorLayer"/> for player two's view.</summary>
    public const uint SecondViewIndoorLayer = 1u << 12;

    /// <summary>The interior pair of the one-view layers.</summary>
    public const uint IndoorViewLayers = FirstViewIndoorLayer | SecondViewIndoorLayer;

    /// <summary>Every layer a single view draws alone.</summary>
    private const uint OneViewLayers = FirstViewOnlyLayer | SecondViewOnlyLayer | IndoorViewLayers;

    /// <summary>
    /// The layers that put an instance whose own layers are <paramref name="layers"/> in view
    /// <paramref name="view"/> alone while split, lit as those layers are: an interior's
    /// (<see cref="Refii.Lighting.Engine_LightZones.IndoorMask"/>) takes the view's indoor layer,
    /// anything else the view's own layer, which every outdoor light and the sun reach.
    /// </summary>
    public static uint OnlyIn(int view, uint layers)
    {
        bool indoor = (layers & Refii.Lighting.Engine_LightZones.IndoorMask) != 0u;
        return view == 0
            ? (indoor ? FirstViewIndoorLayer : FirstViewOnlyLayer)
            : (indoor ? SecondViewIndoorLayer : SecondViewOnlyLayer);
    }

    /// <summary>
    /// The layers drawn by their own overlay cameras, never by a view: each overlay strips its
    /// layer from the camera it mirrors, and this copy runs after them, so it strips them too.
    /// </summary>
    private const uint OverlayLayers = Refii.Fx.Engine_FxAdditiveLayer.LayerMask |
        Refii.Fx.Engine_FxDisplacement.LayerMask | Refii.Entities.Engine_EntityHighlightLayer.LayerMask;

    private static readonly Camera3D?[] _cameras = new Camera3D?[Max];
    private static readonly Vector4[] _rows = new Vector4[4];
    private static readonly float[] _sixteen = new float[16];
    private static readonly float[] _four = new float[4];
    private static ulong _taggedFirst, _taggedSecond;

    /// <summary>Views drawn this frame: 1, or 2 on split screen.</summary>
    public static int Count { get; private set; } = 1;

    /// <summary>The camera drawing view <paramref name="view"/> this frame, null past <see cref="Count"/>.</summary>
    public static Camera3D? Camera(int view) => view >= 0 && view < Count ? _cameras[view] : null;

    /// <summary>
    /// Resolves this frame's views from the window's camera and Refii.Core.Engine_Views, and keeps
    /// the split cameras' cull masks on the window camera's (the layers the frame's systems add to
    /// it reach both views) with the second view's marker. Main thread, once a frame, before the
    /// feeds that follow a camera.
    /// </summary>
    public static void Begin(Camera3D? window)
    {
        Camera3D? first = null, second = null;
        if (Refii.Core.Engine_Views.Split &&
            Refii.Core.Engine_Views.FirstViewport is { } firstViewport && GodotObject.IsInstanceValid(firstViewport) &&
            firstViewport.GetCamera3D() is { } firstCamera && GodotObject.IsInstanceValid(firstCamera))
        {
            first = firstCamera;
            second = Refii.Core.Engine_Views.Second;
        }

        if (first is null || second is null)
        {
            Count = 1;
            _cameras[0] = window;
            _cameras[1] = null;
        }
        else
        {
            Count = 2;
            _cameras[0] = first;
            _cameras[1] = second;
            if (window is not null && GodotObject.IsInstanceValid(window))
            {
                uint mask = window.CullMask & ~(SecondViewLayer | OverlayLayers | OneViewLayers);
                uint firstMask = mask | FirstViewOnlyLayer | FirstViewIndoorLayer;
                uint secondMask = mask | SecondViewOnlyLayer | SecondViewIndoorLayer | SecondViewLayer;
                if (first.CullMask != firstMask)
                {
                    first.CullMask = firstMask;
                }
                if (second.CullMask != secondMask)
                {
                    second.CullMask = secondMask;
                }
            }
            Tag(first, 0, ref _taggedFirst);
            Tag(second, 1, ref _taggedSecond);
        }

        if (Engine_ClassicMaterial.Active)
        {
            Engine_ClassicConstants.SetViews(Count);
        }
    }

    /// <summary>The eye of view <paramref name="view"/>, Godot axes.</summary>
    public static Vector3 Eye(int view) => Camera(view)?.GlobalPosition ?? Vector3.Zero;

    /// <summary>The squared distance from a Godot point to the nearest view's eye; MaxValue with no view.</summary>
    public static float NearestEyeDistanceSquared(Vector3 point)
    {
        float best = float.MaxValue;
        for (int v = 0; v < Count; v++)
        {
            if (_cameras[v] is { } camera)
            {
                best = MathF.Min(best, camera.GlobalPosition.DistanceSquaredTo(point));
            }
        }
        return best;
    }

    /// <summary>The distance from a Godot point to the nearest view's eye.</summary>
    public static float NearestEyeDistance(Vector3 point) => MathF.Sqrt(NearestEyeDistanceSquared(point));

    /// <summary>
    /// Writes each view's host registers (the view-space material constants a composition reads
    /// per view, <see cref="Engine_ClassicRegisters.Ps.HostInverseViewProjection"/> and the VS
    /// host registers). Only the second view reads them (the first reads the materials' own), so
    /// only its block is written. Main thread, once a frame, after <see cref="Begin"/>, before
    /// the publish.
    /// </summary>
    public static void WriteHostRegisters()
    {
        for (int v = 1; v < Count; v++)
        {
            if (_cameras[v] is not { } camera || !GodotObject.IsInstanceValid(camera))
            {
                continue;
            }

            InverseViewProjection(camera, _rows);
            Rows(_rows);
            Engine_ClassicConstants.SetFrameView(v, true, Engine_ClassicRegisters.Ps.HostInverseViewProjection, _sixteen);

            Projection clip = Engine_ClassicShaders.GuestToClip(camera);
            ViewProjectionRows(clip, _rows);
            Rows(_rows);
            Engine_ClassicConstants.SetFrameView(v, false, Engine_ClassicRegisters.Vs.HostDecalViewProjection, _sixteen);

            Vector3 z = ToGuest(camera.GlobalTransform.Basis.Z);
            Four(v, Engine_ClassicRegisters.Vs.HostCameraZ, z.X, z.Y, z.Z, 0f);

            Vector3 eye = ToGuest(camera.GlobalPosition);
            Four(v, Engine_ClassicRegisters.Vs.HostBillboardEye, eye.X, eye.Y, eye.Z, 1f);
            Four(v, Engine_ClassicRegisters.Vs.HostStarsEye, eye.X, eye.Y, eye.Z, 0f);
            Vector2 flat = BillboardForward(camera);
            Four(v, Engine_ClassicRegisters.Vs.HostBillboardForward, flat.X, flat.Y, 0f, 0f);
        }
    }

    /// <summary>
    /// g_InverseViewProjection for a soft particle as the pixel shader reads it: it forms
    /// (x, y, z, 1) from the pixel and the [0, 1] depth, which is 2z - 1 in the camera's GL
    /// projection; the rows are those of (Godot world to guest world) x inverse(projection x
    /// view) x (that z remap).
    /// </summary>
    public static void InverseViewProjection(Camera3D camera, Span<Vector4> rows)
    {
        Projection view = new Projection(camera.GlobalTransform.AffineInverse());
        Projection projection = camera.GetCameraProjection();
        Projection remap = Projection.Identity;
        remap.Z = new Vector4(0f, 0f, 2f, 0f);
        remap.W = new Vector4(0f, 0f, -1f, 1f);
        var toGuest = new Projection(
            new Vector4(-1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));
        Projection m = toGuest * (projection * view).Inverse() * remap;
        for (int i = 0; i < 4; i++)
        {
            rows[i] = new Vector4(m.X[i], m.Y[i], m.Z[i], m.W[i]);
        }
    }

    /// <summary>A guest-to-clip projection's rows (g_MatViewProj as the decal stage reads it).</summary>
    public static void ViewProjectionRows(Projection clip, Span<Vector4> rows)
    {
        rows[0] = new Vector4(clip.X.X, clip.Y.X, clip.Z.X, clip.W.X);
        rows[1] = new Vector4(clip.X.Y, clip.Y.Y, clip.Z.Y, clip.W.Y);
        rows[2] = new Vector4(clip.X.Z, clip.Y.Z, clip.Z.Z, clip.W.Z);
        rows[3] = new Vector4(clip.X.W, clip.Y.W, clip.Z.W, clip.W.W);
    }

    /// <summary>The billboard cards' g_CameraZVector: the view's forward with its height dropped, normalised, guest axes.</summary>
    public static Vector2 BillboardForward(Camera3D camera)
    {
        Vector3 forward = ToGuest(-camera.GlobalBasis.Z);
        var flat = new Vector2(forward.X, forward.Y);
        return flat.LengthSquared() > 1e-8f ? flat.Normalized() : Vector2.Zero;
    }

    /// <summary>
    /// Tells the classic compositor effects on a split camera which view they draw (they read
    /// that view's registers and cascades on the render thread). Once per camera.
    /// </summary>
    private static void Tag(Camera3D camera, int view, ref ulong tagged)
    {
        ulong id = camera.GetInstanceId();
        if (tagged == id)
        {
            return;
        }
        tagged = id;
        if (camera.Compositor is not { } compositor)
        {
            return;
        }
        foreach (CompositorEffect effect in compositor.CompositorEffects)
        {
            if (effect is Engine_ClassicScreenShadow mask)
            {
                mask.View = view;
            }
        }
    }

    private static void Rows(Vector4[] rows)
    {
        for (int i = 0; i < 4; i++)
        {
            _sixteen[i * 4] = rows[i].X;
            _sixteen[i * 4 + 1] = rows[i].Y;
            _sixteen[i * 4 + 2] = rows[i].Z;
            _sixteen[i * 4 + 3] = rows[i].W;
        }
    }

    private static void Four(int view, int register, float x, float y, float z, float w)
    {
        _four[0] = x; _four[1] = y; _four[2] = z; _four[3] = w;
        Engine_ClassicConstants.SetFrameView(view, false, register, _four);
    }

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);
}
