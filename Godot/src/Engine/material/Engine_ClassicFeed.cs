using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's frame feed: the frame constants the title's render passes would have
/// set, written by name into <see cref="Engine_ClassicConstants"/> from the values the guest
/// states.
/// </summary>
/// <remarks>
/// NOT THE DEVICE'S SHADOW. The title's passes write these registers into the D3D device as they
/// run, but in this host the passes never run: the simulation thread publishes what it would
/// draw into the scene stream, the render thread keeps the twins, and the pass bodies are not
/// entered. The shadow under the device pointer stays all zeros for the whole run (measured,
/// Westcliff, 2026-10-12). So each register is taken from what the pass itself would have read:
/// the lighting globals and the atmosphere block through <see cref="Refii.Host.HostBridge.GetClassicFrame"/>,
/// the stated camera, the guest clock — and laid out as the pass lays it out
/// (<c>sub_82282320</c>, <c>sub_821B16F8</c>, <c>sub_821B1A88</c>, <c>sub_82263570</c>).
///
/// A guest world point (x, y, z) is Godot's (-x, z, y) (GameEntities.ToGodot, the level root's
/// (-1, 1, 1) scale, xenos_common.gdshaderinc <c>XR_GODOT_TO_GUEST_WORLD</c>); the guest's own
/// vectors are written as they are, Godot's are mapped on the way in.
/// </remarks>
public static class Engine_ClassicFeed
{
    private const int FrameFloats = 148;
    // The theme block's float4s, by the index `sub_83288CA0`'s table gives them.
    private const int ThemeBase = 36;
    private const int ThemeAmbientColour = 2;
    private const int ThemeLightmapColour = 3;
    private const int ThemeDarkeningColour = 4;
    private const int ThemeDistantShadowFactor = 5;
    private const int SunColour = 132;
    private const int SunDirection = 136;
    private static readonly float[] _frame = new float[FrameFloats];
    private static readonly float[] _four = new float[4];
    private static readonly float[] _block = new float[20];
    private static long _frames;
    private static long _fed;

    /// <summary>Writes this frame's constants. Main thread, once a frame, before Publish.</summary>
    /// <param name="camera">The drawn camera, which follows the guest's stated one.</param>
    public static void Pump(Camera3D? camera)
    {
        ++_frames;

        if (camera is not null)
        {
            Vector3 eye = ToGuest(camera.GlobalPosition);
            // PS c19.w is 0 and VS c9.w is 1 in the title's own banks (REFII_GUEST_RENDER run).
            Ps(Engine_ClassicRegisters.Ps.EyePosition, eye.X, eye.Y, eye.Z, 0f);
            Vs(Engine_ClassicRegisters.Vs.EyePosition, eye.X, eye.Y, eye.Z, 1f);

            // g_PerspectiveConstants: the projection's m[2][2] and m[3][2], the D3D depth
            // terms, from the camera's near and far.
            float near = camera.Near, far = camera.Far;
            float q = far / MathF.Max(far - near, 1e-6f);
            Ps(Engine_ClassicRegisters.Ps.PerspectiveConstants, q, -near * q, 0f, 0f);

            // PS c0 g_TileOffsetSize: the screen-space shadow mask's (and screen textures')
            // addressing, pixel * (1/W, 1/H) at the internal 3D resolution the mask is drawn at.
            Vector2 screen = Engine_ClassicScreenShadow.Size is { X: > 0, Y: > 0 } mask
                ? new Vector2(mask.X, mask.Y)
                : camera.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280f, 720f);
            Ps(Engine_ClassicRegisters.Ps.TileOffsetSize, 0f, 0f, 1f / MathF.Max(screen.X, 1f), 1f / MathF.Max(screen.Y, 1f));

            // The particle draw's camera (`sub_821CA108`): position, and the view's right and
            // up columns with w = 10, in the guest's axes.
            Vector3 right = ToGuest(camera.GlobalBasis.X), up = ToGuest(camera.GlobalBasis.Y);
            Vs(Engine_ClassicRegisters.Vs.CameraPosition, eye.X, eye.Y, eye.Z, 1f);
            Vs(Engine_ClassicRegisters.Vs.CameraRightVector, right.X, right.Y, right.Z, 10f);
            Vs(Engine_ClassicRegisters.Vs.CameraUpVector, up.X, up.Y, up.Z, 10f);
        }

        float time = Refii.Level.Engine_GuestAtmosphere.Seconds();
        Seconds = time;
        Ps(Engine_ClassicRegisters.Ps.SimulationTime, time, time, time, time);
        Vs(Engine_ClassicRegisters.Vs.SimulationTime, time, time, time, time);

        // Constants the passes write as literals.
        Ps(Engine_ClassicRegisters.Ps.GlobalTextureGammaRamp, 2.2f, 2.2f, 2.2f, 2.2f);
        // g_AtmosphericFactors as `sub_82214FB0` sets it: x = 1 (the pass fogs), y = 1 on the
        // first light group (its in-scatter), 0 on the additive groups; the frame's own row is
        // a first group.
        Ps(Engine_ClassicRegisters.Ps.AtmosphericFactors, 1f, 1f, 0f, 0f);
        // g_FadeBeginDistanceAndRange (PS c15), read by the tree and repeated families only. The
        // tree draw's value when its instance is not fading (`sub_8220AF90`, inst+0x3E == 0):
        // (0, 0, 0, 1), and the tree pixel shader multiplies its alpha by c15.w. For the repeated
        // family a zero range is no distance fade. The per-instance LOD and draw-distance fades
        // are not fed yet.
        Ps(Engine_ClassicRegisters.Ps.FadeBeginDistanceAndRange, 0f, 0f, 0f, 1f);

        if (Replay is { } replay && replay.Length >= FrameFloats)
        {
            Array.Copy(replay, _frame, FrameFloats);
        }
        else if (Refii.Host.HostBridge.GetClassicFrame(_frame) < FrameFloats)
        {
            return;
        }
        ++_fed;
        Refii.Level.Engine_GuestAtmosphere.Take(_frame);

        // The theme block when the scene is up, else the globals the passes cached from it.
        bool theme = HasScene();
        int ambient = theme ? Theme(ThemeAmbientColour) : 0;
        int lightmap = theme ? Theme(ThemeLightmapColour) : 8;
        int darkening = theme ? Theme(ThemeDarkeningColour) : 12;

        // Ambient, and the brightness lane every material ends `oC0 *= c20.w` with: for the main
        // view `*(*(0x8349F848) + 0x41AC)` (`sub_82282320` -> `sub_821B1958`), the title's
        // adaptive pre-exposure E (`sub_82191C18`). It is the GPU's value (the post chain's
        // reduce pass), so the lane carries the marker the shaders' constant fetch replaces.
        Ps(Engine_ClassicRegisters.Ps.GlobalAmbientAndBrightness, _frame[ambient], _frame[ambient + 1], _frame[ambient + 2], ExposureLane);
        Vs(Engine_ClassicRegisters.Vs.GlobalAmbientAndBrightness, _frame[ambient], _frame[ambient + 1], _frame[ambient + 2], ExposureLane);

        // The main light's travel direction, negated as the passes write it: toward the sun.
        // Both directional lights of the rig carry it (`sub_821DF670`: +304 =
        // normalize(-mainLightDir)), but `*(0x834A4D60)` is the first type-11 light in the
        // registry, which is not always one of them — live it read (0, 1, 0) while the title's
        // c23 held the sun's. The sun twin's own +304 ([136..138]) is the rig's.
        int dir = _frame[136] * _frame[136] + _frame[137] * _frame[137] + _frame[138] * _frame[138] > 1e-6f ? 136 : 4;
        float sx = -_frame[dir], sy = -_frame[dir + 1], sz = -_frame[dir + 2];
        Ps(Engine_ClassicRegisters.Ps.NormalMapLightAngleConstants, sx, sy, sz, 4f);
        Vs(Engine_ClassicRegisters.Vs.AmbientLightReferenceDirection, sx, sy, sz, 0f);

        // The lightmap factor with each stage's gamma, and the normal-map darkening colour.
        Ps(Engine_ClassicRegisters.Ps.LightmapFactorAndGamma, _frame[lightmap], _frame[lightmap + 1], _frame[lightmap + 2], 1.3f);
        Vs(Engine_ClassicRegisters.Vs.LightmapFactorAndGamma, _frame[lightmap], _frame[lightmap + 1], _frame[lightmap + 2], 2.86f);
        // `sub_821D1CA0` stores (O[4].xyz, 0): the w is zero, not the theme colour's 1.
        Ps(Engine_ClassicRegisters.Ps.AmbientNormalMapDarkeningColour, _frame[darkening], _frame[darkening + 1], _frame[darkening + 2], 0f);

        // g_ShadowRangeConstants: three literals and the theme's distant shadow factor
        // (`sub_821B7128`).
        float distant = theme ? _frame[Theme(ThemeDistantShadowFactor)] : 1f;
        Ps(Engine_ClassicRegisters.Ps.ShadowRangeConstants, 20f, 35f, 2f, distant);
        Refii.Level.Engine_ClassicVistaShadow.Pump(distant);

        // g_TreeConstants as `sub_82222CE8` writes them (0x82222CE8..0x82222F64): c15 = (R+0x250,
        // 0.63, 0.72, 0.32), c16 = (0.012, 0.06, 0.4, canopy ? 0.035 : -1), c17 = (wind . R+0x180,
        // wind . R+0x190, clamp(W+0xD44, 0, 10), R+0x254). Per tree, `xr_tree_constant` adds the
        // seed to c15.x and puts the wind in the tree's axes from HostWindDirection; the frame
        // carries the clock and R+0x254, which every tree integrates alike (`sub_822DAB20`:
        // += clamp(speed, 0, 10) * dt from a zeroed field, so the first step spans the whole
        // clock). c16.w is the material's (xr_tree_c16w): 0.035 for a canopy, -1 for a trunk.
        Refii.Level.Engine_GuestAtmosphere.StepTrees(time);
        float speed = Refii.Level.Engine_GuestAtmosphere.TreeSpeed;
        Vs(Engine_ClassicRegisters.Vs.TreeConstants, time, 0.63f, 0.72f, 0.32f);
        Vs(Engine_ClassicRegisters.Vs.TreeConstants + 1, 0.012f, 0.06f, 0.4f, -1f);
        Vs(Engine_ClassicRegisters.Vs.TreeConstants + 2, 0f, 0f, speed, Refii.Level.Engine_GuestAtmosphere.TreePhase);
        Vs(Engine_ClassicRegisters.Vs.HostWindDirection, theme ? _frame[144] : 0f, theme ? _frame[145] : 0f, theme ? _frame[146] : 0f, 0f);

        // The repeated family's wind (`sub_82224578`): c21 = (scene+3384 direction, 0), c22 =
        // 0.01 x the static row (-35, 153, 0, 0) at 0x834A1250; its per-draw c25/c26 follow.
        var wind = theme ? new Vector3(_frame[144], _frame[145], _frame[146]) : Vector3.Zero;
        Vs(Engine_ClassicRegisters.Vs.WindDirections, wind.X, wind.Y, wind.Z, 0f);
        Vs(Engine_ClassicRegisters.Vs.WindDirections + 1, -0.35f, 1.53f, 0f, 0f);
        if (theme)
        {
            Engine_ClassicWind.Pump(wind, _frame[147]);
        }

        // PS c50 g_AlphaStippleScreenUVFactor (`sub_82A73050`): the scene colour target's size
        // over 32, so the 32-texel stipple tiles the screen pixel for pixel.
        if (Godot.Engine.GetMainLoop() is SceneTree tree)
        {
            Vector2 size = tree.Root.GetVisibleRect().Size;
            Ps(Engine_ClassicRegisters.Ps.AlphaStippleScreenUVFactor, size.X / 32f, size.Y / 32f, 0f, 0f);
        }

        Engine_ClassicTheme.Pump();

        // The title's cloud layers: viewer and the main light (the sun twin's +208 colour and
        // +304 direction, as the rig's last directional light carries them).
        if (ViewCamera() is { } cloudCamera)
        {
            Vector3 eye = cloudCamera.GlobalPosition;
            Vector3 forward = -cloudCamera.GlobalTransform.Basis.Z;
            Refii.Level.Engine_ClassicClouds.Pump(
                new Vector3(-eye.X, eye.Z, eye.Y), new Vector3(-forward.X, forward.Z, forward.Y),
                new Vector3(_frame[132], _frame[133], _frame[134]), new Vector3(_frame[136], _frame[137], _frame[138]));
        }

        // The atmosphere block, five registers in both stages, and the two tables built from it.
        Array.Copy(_frame, 16, _block, 0, 20);
        Engine_ClassicConstants.SetFrame(true, Engine_ClassicRegisters.Ps.AtmosphereBetaRayleigh, _block);
        Engine_ClassicConstants.SetFrame(false, Engine_ClassicRegisters.Vs.AtmosphereBetaRayleigh, _block);
        if (theme)
        {
            Engine_ClassicAtmosphere.Update(_frame);
        }
    }

    /// <summary>The camera the frame is drawn from: the game's, or the editor's 3D view.</summary>
    private static Camera3D? ViewCamera()
    {
        if (Godot.Engine.GetMainLoop() is SceneTree tree && tree.Root.GetCamera3D() is { } camera)
        {
            return camera;
        }
#if TOOLS
        if (Godot.Engine.IsEditorHint())
        {
            return EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
        }
#endif
        return null;
    }


    /// <summary>`XR_EXPOSURE_LANE` in xenos_common.gdshaderinc: "this lane is the scene exposure".</summary>
    public const float ExposureLane = -12345f;

    /// <summary>A recorded frame (148 floats as the native export lays them out) read instead of the bridge.</summary>
    public static float[]? Replay;

    /// <summary>The raw frame as last read, for the debug channel's recorder.</summary>
    public static float[] RawFrame => _frame;

    /// <summary>The lighting object's ambient (G+208), the particle draw's g_GlobalAmbient base.</summary>
    public static Vector3 GlobalAmbient => new(_frame[0], _frame[1], _frame[2]);

    /// <summary>The scene's wind: unit direction xyz and speed w (O+0xD38..0xD44), guest axes.</summary>
    public static Vector4 Wind => Refii.Level.Engine_GuestAtmosphere.Wind;

    /// <summary>The scene's global mist strength (O+3040.x), 1 when the scene is not up.</summary>
    public static float GlobalMistStrength => Refii.Level.Engine_GuestAtmosphere.MistStrength;

    /// <summary>The frame time the constants were written with, seconds.</summary>
    public static float Seconds { get; private set; }

    /// <summary>Frames pumped, and frames that found the guest's lighting globals.</summary>
    public static (long Frames, long Fed) Counts => (_frames, _fed);

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    private static int Theme(int index) => ThemeBase + index * 4;

    /// <summary>The theme block was read: its MAIN_LIGHT_COLOUR alpha is 1 when written.</summary>
    private static bool HasScene() => _frame[ThemeBase + 3] != 0f;

    /// <summary>The sun light's colour and travel direction as the twin holds them, when up.</summary>
    public static bool TrySun(out Vector3 colour, out Vector3 direction, out float intensity)
    {
        colour = Refii.Level.Engine_DynamicSkyLighting.ResolveRadiance(new Vector3(_frame[SunColour], _frame[SunColour + 1], _frame[SunColour + 2]));
        direction = Refii.Level.Engine_DynamicSkyLighting.ResolveGuestTravel(new Vector3(_frame[SunDirection], _frame[SunDirection + 1], _frame[SunDirection + 2]));
        intensity = _frame[141];
        return _frame[SunColour + 3] != 0f;
    }

    private static void Ps(int register, float x, float y, float z, float w)
    {
        _four[0] = x; _four[1] = y; _four[2] = z; _four[3] = w;
        Engine_ClassicConstants.SetFrame(true, register, _four);
    }

    private static void Vs(int register, float x, float y, float z, float w)
    {
        _four[0] = x; _four[1] = y; _four[2] = z; _four[3] = w;
        Engine_ClassicConstants.SetFrame(false, register, _four);
    }
}
