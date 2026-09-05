using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;
using Refii.Models;
using Refii.Host;

namespace Refii.Level;

[Tool]
public partial class Engine_SkyEnvironmentNode : WorldEnvironment
{
    private float _dayFraction = 0.5f;

    [Export(PropertyHint.Range, "0,24,0.05,suffix:h")]
    public float TimeOfDay
    {
        get => _dayFraction * 24f;
        set
        {
            _dayFraction = value / 24f;
            if (IsNodeReady())
            {
                Reevaluate();
            }
        }
    }

    [Export]
    public string LevelGdbPath { get; set; } = string.Empty;

    public SkyTheme? Theme { get; set; }

    public List<byte[]> LightingGdbs { get; } = new();

    public DirectionalLight3D? Sun { get; set; }

    /// <summary>
    /// The region's lighting for the current time of day, or null before the first evaluation.
    ///
    /// Everything in the region that has to be lit reads it from here — the prop streamer asks
    /// for it as each mesh arrives. One node owns the answer, so the editor and the game cannot
    /// end up with two.
    /// </summary>
    public LightingFrame? Lighting { get; private set; }

    public ToneMapTheme? ToneMap => _toneMapTheme;

    public SkyFrame? Frame => _frame;

    /// <summary>
    /// The environment node for the world currently standing, or null before one is loaded.
    ///
    /// Godot allows one environment per World3D and Refii loads one region at a time, so this
    /// mirrors a real singleton rather than inventing one. It exists because the things that
    /// need the lighting are not all INSIDE the region: <c>GameEntities</c> hangs beside the
    /// region under <c>GameWorld</c>, carrying the guest's live characters, and a sibling
    /// cannot reach the region's environment by walking its own parents.
    ///
    /// If a second region is ever mounted at the same time, this becomes the wrong answer for
    /// one of them and the lookup has to become per-World3D.
    /// </summary>
    public static Engine_SkyEnvironmentNode? Current { get; private set; }

    /// <summary>
    /// Evaluate as the region stands up, in the EDITOR as well as in the game.
    ///
    /// The scene is saved with the sun and the environment already correct, but nothing
    /// standing in it carries any lighting: props are packed meshless and their instance
    /// parameters cannot be serialised. So the first evaluation is not a refresh, it is the
    /// only time the region is lit at all.
    ///
    /// It RETRIES, because the editor can lose the race. The theme is read back out of the
    /// banks through <c>Engine_BnkMeshLoader</c>, the addon mounts them asynchronously, and a
    /// level scene reopened at editor start arrives first. One attempt then finds no GDB, gives
    /// up, and the region stays on its defaults for the rest of the session with nothing said —
    /// which is exactly the kind of silent editor-only difference this is meant to remove.
    /// </summary>
    public override void _Ready()
    {
        Current = this;
        Refii.Fx.Engine_FxSystem.BindAmbientToScene();

        try
        {
            System.Collections.Generic.IReadOnlyDictionary<string, string> config =
                Refii.Core.Engine_RefiiConfig.Read(out _);
            RenderingServer.GlobalShaderParameterSet("fable_light_debug",
                Refii.Core.Engine_RefiiConfig.Int(config, "lighting_debug_view", 0));

            // ONE bake weight for every authored bake — terrain lightmap, vertex colours,
            // instance colours, interior textures — so the bridge, the rocks and the ground
            // they meet scale together instead of drifting into separate lighting worlds.
            RenderingServer.GlobalShaderParameterSet("fable_bake_weight",
                System.Math.Clamp(
                    Refii.Core.Engine_RefiiConfig.Float(config, "baked_light_weight", 0.45f),
                    0.05f, 2.0f));
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[lighting] lighting cvars unreadable: {e.Message}");
        }

        Engine_PostFxEffect.RegisterGlobals();
        if (Engine_PostFxParams.SunSourcesRequested)
        {
            Engine_PostFxSunSources.Ensure(this);
        }

        Reevaluate();
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }

        DetachEditorCompositor();
    }

    /// <summary>
    /// TWO JOBS SHARE THE FRAME, and they have opposite lifetimes.
    ///
    /// The mount RETRY above runs until the theme arrives and then never again. The sky's
    /// ANIMATION — cloud layers drifting on their authored velocities, and the sun and moon
    /// glare occluding against them — runs for as long as the region stands. Processing used to
    /// be switched off the moment the retry was satisfied, so wiring the animation into the same
    /// callback without this split would stop the clouds on the frame the region finished
    /// lighting itself.
    /// </summary>
    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("skyenvironmentnode");
        if (Lighting is null && _attempts <= MountAttempts)
        {
            if (++_attempts > MountAttempts)
            {
                GD.Print($"[lighting] {Name}: no environment theme after {MountAttempts} " +
                    "frames; the region is on shader defaults");
            }
            else
            {
                Reevaluate();
            }
        }

        FollowGuestClock();
        TrackBodies();
        AnimateSky((float)delta);
        if (Engine_PostFxParams.SunSourcesRequested)
        {
            Engine_PostFxSunSources.Ensure(this);
        }

        PublishPostFx();
        SyncEditorCompositor();
    }

    /// <summary>
    /// Frozen means the guest is not advancing time, so neither do we.
    /// </summary>
    private float _lastEvaluated = float.NegativeInfinity;

    /// <summary>Report a re-evaluation that costs more than a frame at 144 Hz.</summary>
    private const double ReevaluateWarnMs = 6.9;

    /// <summary>
    /// How far the day has to move before the themes are re-read: one game minute.
    /// </summary>
    /// <remarks>
    /// NOT EVERY FRAME, because <see cref="Reevaluate"/> is not a cheap function — it walks the
    /// GDB chain to re-extract and re-blend the sky, cloud and lighting themes, and doing that at
    /// display rate would cost more than everything it feeds. One minute of game time is 1/1440 of
    /// the day, which at the guest's default day speed is one real second: fine enough that the
    /// sun's movement is continuous to the eye and coarse enough to be free.
    ///
    /// The guest itself re-evaluates the day set every 15 Hz tick (`sub_82275F68` advances the
    /// clock at 0x822760C0 and calls `sub_8218EAF8` at 0x82276104), so this is coarser than the
    /// original by design rather than by accident — and it can afford to be, because its
    /// re-evaluation uploads a few shader constants where ours re-lights every prop in the region.
    ///
    /// FIVE MINUTES, NOT ONE, and the difference is entirely in the stutter rather than the image.
    /// A day is a full 360 degrees of azimuth, so five game minutes moves the sun about 1.25
    /// degrees — invisible — while cutting the number of times a frame has to absorb a GDB walk,
    /// a dome LUT rebuild and a pass over every prop by a factor of five. At the default day speed
    /// that is one every five real seconds instead of one every second.
    /// </remarks>
    private const float ReevaluateStep = 5.0f / 1440.0f;

    /// <summary>
    /// Drives the region's time of day from the guest's own world clock.
    /// </summary>
    /// <remarks>
    /// UNTIL THIS EXISTED THE WHOLE ENVIRONMENT WAS FROZEN. `Reevaluate` ran only while
    /// <c>Lighting</c> was null, so the first success was the last: the sun, the sky, the ambient,
    /// the fog and the cloud tint were all pinned to whichever day-set entry happened to be
    /// selected at level load, and the day advanced underneath with no effect whatsoever.
    ///
    /// A HOST-SIDE CLOCK WOULD NOT DO. The value has to be the guest's, because the guest is the
    /// thing that decides what time it is — it fast-forwards for sleep, it stops for cutscenes,
    /// and script sets it outright. Ticking our own would drift within one night's sleep.
    /// </remarks>
    private LightingTheme? _lightingTheme;

    /// <summary>
    /// Moves the sun, the moon and the light EVERY FRAME.
    ///
    /// <see cref="Reevaluate"/> only runs on a five-game-minute step because the theme extract and
    /// the dome's scatter table are expensive — but that cadence is also what the bodies moved on,
    /// so they stepped instead of travelling. Everything here is arithmetic on the theme already
    /// in hand: the orbit is trigonometry and the light is a basis, so it costs nothing to do per
    /// frame. The scatter LUT is deliberately NOT rebuilt here; it changes slowly and the coarse
    /// pass still owns it.
    /// </summary>
    private void TrackBodies()
    {
        if (Theme is null || !float.IsFinite(_dayFraction))
        {
            return;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);

        Vector3 dome = Engine_SkyFrame.ToDome(frame.SunDirection);
        if (Environment?.Sky?.SkyMaterial is ShaderMaterial material)
        {
            material.SetShaderParameter("sun_engine", dome);
        }

        RenderingServer.GlobalShaderParameterSet("atmos_sun", dome);

        if (GetParent()?.GetNodeOrNull<Node3D>(Engine_CelestialElements.NodeName) is { } celestial)
        {
            Engine_CelestialElements.Update(celestial, Theme, frame);
        }

        if (_lightingTheme is { } lightingTheme && ResolveSun() is { } sun)
        {
            LightingFrame lighting = Engine_LightingFrame.Evaluate(Theme, lightingTheme, frame);
            Lighting = lighting;
            Engine_LevelLighting.UpdateSun(sun, lighting);
        }
    }

    private void FollowGuestClock()
    {
        if (HostBridge.TimeOfDay() is not { } clock)
        {
            // No world clock yet — the front end, or a load in progress. Hold what we have rather
            // than snapping the sky to a default that the game is not at.
            return;
        }

        _dayFraction = clock.Fraction;

        // Wrapped distance, so midnight is not a jump. Crossing 0.999 -> 0.001 is two minutes of
        // game time, and comparing the raw difference would read it as most of a day and force a
        // rebuild every midnight.
        float moved = MathF.Abs(_dayFraction - _lastEvaluated);
        moved = MathF.Min(moved, 1.0f - moved);

        if (float.IsFinite(_lastEvaluated) && moved < ReevaluateStep)
        {
            return;
        }

        _lastEvaluated = _dayFraction;
        GD.Print($"[sky-clock] tod={_dayFraction * 24f:0.00}h (fraction {_dayFraction:0.000}) " +
            $"day {clock.DayCount}{(clock.Frozen ? ", FROZEN" : string.Empty)}");

        // WHERE THE SUN IS, in world space — the one direction every consumer takes.
        //
        // The celestial billboards read it through `VIEW_MATRIX` (world, no MODEL_MATRIX, so the
        // region root's mirror does not apply); the post-FX projects the same vector through the
        // camera; the light is the only node-parented consumer and pre-compensates the mirror
        // itself. Measuring the disc mesh's GlobalPosition here was wrong — it is placed in the
        // shader and its node never moves.
        if (Theme is { } probeTheme)
        {
            SkyFrame probe = Engine_SkyFrame.Evaluate(probeTheme, _dayFraction);
            Vector3 world = Engine_SkyFrame.ToGodot(probe.SunDirection).Normalized();
            GD.Print($"[sky-sun] world ({world.X:0.00},{world.Y:0.00},{world.Z:0.00}) " +
                $"elevation {Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(world.Y, -1f, 1f))):0.0}deg");
        }

        // TIMED, because "it stutters about once a second" and "Reevaluate is expensive" are two
        // different claims and only one of them is measured. If this is not the cost, the number
        // says so and the search moves elsewhere instead of the interval being tuned on a hunch.
        ulong began = Time.GetTicksUsec();
        Reevaluate();
        double ms = (Time.GetTicksUsec() - began) / 1000.0;

        if (ms > ReevaluateWarnMs)
        {
            // BROKEN DOWN BY PHASE. A single total cannot distinguish the theme extract from the
            // dome LUT upload from the instance walk, and those want completely different fixes.
            GD.Print(
                $"[lighting] re-evaluate took {ms:0.0} ms at tod={_dayFraction:0.0000} " +
                $"(theme {_msTheme:0.0}, dome {_msDome:0.0}, water {_msWater:0.0}, " +
                $"celestial {_msCelestial:0.0}, instances {_msInstances:0.0})");
        }
    }

    private bool _reportedSunAgreement;

    private double _msTheme;
    private double _msDome;
    private double _msWater;
    private double _msCelestial;
    private double _msInstances;

    private static double SinceMs(ref ulong mark)
    {
        ulong now = Time.GetTicksUsec();
        double ms = (now - mark) / 1000.0;
        mark = now;
        return ms;
    }

    /// <summary>Frames to keep asking for the theme. Generous — a cold bank mount is slow.</summary>
    private const int MountAttempts = 600;

    private int _attempts;

    public void Initialise(SkyTheme theme, float dayFraction, string levelGdbPath)
    {
        Theme = theme;
        _dayFraction = dayFraction;
        LevelGdbPath = levelGdbPath;
    }

    public void Reevaluate()
    {
        if (!EnsureTheme() || Theme is null)
        {
            return;
        }

        ulong mark = Time.GetTicksUsec();

        if (LightingGdbs.Count != 0 &&
            Engine_ThemeTimeline.TryExtractSkyBlended(
                LightingGdbs, _dayFraction, out SkyTheme timeTheme))
        {
            Theme = timeTheme;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);
        _msTheme = SinceMs(ref mark);

        (AtmosphereState domeState, ImageTexture domeLut) =
            Engine_SkyEnvironment.ComputeDome(frame, Theme);

        if (Environment?.Sky?.SkyMaterial is ShaderMaterial material)
        {
            Engine_SkyEnvironment.ApplyDomeUniforms(material, frame, domeState, domeLut);
        }

        // OUTSIDE the sky-material guard. The world is fogged whether or not this region drew a
        // dome, and a level whose sky material failed to build would otherwise lose its fog too —
        // which is the harder of the two failures to spot.
        Engine_SkyEnvironment.ApplyAtmosphereGlobals(frame, Theme, domeState, domeLut);

        _msDome = SinceMs(ref mark);

        Node3D? water = GetParent()?.GetNodeOrNull<Node3D>(
            Engine_LevelScene.WaterNodeName);
        if (water is not null)
        {
            WaterTheme waterTheme =
                LightingGdbs.Count != 0 &&
                Engine_ThemeTimeline.TryExtractWaterBlended(
                    LightingGdbs, _dayFraction, out WaterTheme blendedWater)
                    ? blendedWater
                    : WaterTheme.Default;

            foreach (Node child in water.GetChildren())
            {
                if (child is MeshInstance3D waterMesh &&
                    waterMesh.MaterialOverride is ShaderMaterial waterMaterial)
                {
                    Engine_SkyEnvironment.ApplyWaterFrame(
                        waterMaterial, frame, domeState, domeLut);
                    Engine_WaterMaterial.Apply(waterMaterial, waterTheme);
                }
            }
        }

        _msWater = SinceMs(ref mark);

        Node3D? celestial = GetParent()?.GetNodeOrNull<Node3D>(
            Engine_CelestialElements.NodeName);
        if (celestial is not null)
        {
            Engine_CelestialElements.Update(celestial, Theme, frame);
        }

        _msCelestial = SinceMs(ref mark);

        Node3D? clouds = GetParent()?.GetNodeOrNull<Node3D>("Clouds");
        if (clouds is not null && LightingGdbs.Count != 0 &&
            Engine_ThemeTimeline.TryExtractCloudsBlended(
                LightingGdbs, _dayFraction, out CloudTheme cloudTheme))
        {
            Engine_Clouds.Update(
                clouds, cloudTheme, frame.SunDirection, frame.MainLightColour);
            _cloudTheme = cloudTheme;
        }

        _frame = frame;

        if (LightingGdbs.Count != 0 &&
            Engine_ThemeTimeline.TryExtractToneMapBlended(
                LightingGdbs, _dayFraction, out ToneMapTheme toneMapTheme))
        {
            _toneMapTheme = toneMapTheme;
        }

        if (LightingGdbs.Count == 0 ||
            !Engine_ThemeTimeline.TryExtractLightingBlended(
                LightingGdbs, _dayFraction, out LightingTheme lightingTheme))
        {
            return;
        }

        _lightingTheme = lightingTheme;
        LightingFrame lighting = Engine_LightingFrame.Evaluate(Theme, lightingTheme, frame);
        Lighting = lighting;

        if (Environment is not null)
        {
            Engine_LevelLighting.ConfigureEnvironment(Environment, lighting);
        }

        DirectionalLight3D? sun = ResolveSun();
        if (sun is not null)
        {
            Engine_LevelLighting.UpdateSun(sun, lighting);

            // DOES THE LIGHT AGREE WITH THE DISC? Reported once, in WORLD space, because that is
            // the only space where the question is answerable.
            //
            // Both the sun disc and this light derive from the same stated SunDirection but reach
            // the screen through different sign conventions — the celestial elements log +0.44 in X
            // where the sky frame states -0.44 — and each may or may not sit under the level root's
            // Scale(-1, 1, 1). On paper the two cancel; the reported symptom is that they do not.
            // Reading the two nodes' actual global transforms settles it without guessing, and a
            // sign flipped on a hunch is a coin toss that would look like a fix either way.
            //
            // EXPECTED: `travel` is the direction light moves, so it should be the NEGATION of
            // `disc` (the direction toward the sun). A dot near -1 is correct; near +1 means the
            // light arrives from precisely the wrong side.
            if (!_reportedSunAgreement && sun.IsInsideTree())
            {
                Vector3 travel = -sun.GlobalTransform.Basis.Column2;
                Node3D? discOwner = GetParent()?.GetNodeOrNull<Node3D>(
                    Engine_CelestialElements.NodeName);
                Node3D? disc = discOwner?.GetNodeOrNull<Node3D>("SunDisc");

                if (disc is not null)
                {
                    _reportedSunAgreement = true;

                    // AGAINST THE RIG, NOT THE DISC. `UpdateSun` consumes the LIGHTING frame's
                    // direction, and the two are deliberately different quantities:
                    //
                    //   - the light rig turns on LightRigXYRotation alone
                    //   - the visible disc adds SunAxisXYRotationOffset and SunAxisZOffset
                    //
                    // Measured across the shipped themes they disagree on 209 of 348 samples, worst
                    // case 156.5 degrees — so a scene lit from almost opposite the visible sun can
                    // be entirely faithful. Comparing against the disc would mean "fixing" the rig
                    // until it matched, destroying a separation the game has on purpose.
                    Vector3 rig = new(
                        lighting.SunDirection[0],
                        lighting.SunDirection[1],
                        lighting.SunDirection[2]);

                    Vector3 discDir = new(
                        frame.SunDirection[0], frame.SunDirection[1], frame.SunDirection[2]);

                    rig = rig.LengthSquared() > 1e-8f ? rig.Normalized() : Vector3.Up;
                    discDir = discDir.LengthSquared() > 1e-8f ? discDir.Normalized() : Vector3.Up;

                    GD.Print(
                        $"[lighting] sun agreement: rig={rig} travel={travel} " +
                        $"rigDot={travel.Dot(rig):0.000} (expect -1.0; anything else is OUR bug) " +
                        $"| disc={discDir} rigVsDisc={Mathf.RadToDeg(rig.AngleTo(discDir)):0.0}deg " +
                        "(expected to differ; the game lights from the rig, not the disc)");
                }
            }
        }

        // The region and everything mounted ALONGSIDE it, not just the sun and the sky.
        //
        // Everything standing in the world carries the ambient of the moment it was lit, and
        // that moves with the sun. Refreshing only the region misses the live guest entities:
        // GameWorld hangs them beside the region so they can carry the guest-to-Godot map
        // themselves, exactly as the camera does, so they are cousins rather than descendants.
        // One level up covers both, and in the editor it simply reaches the scene the region
        // was opened in.
        if (GetParent() is Node region)
        {
            ulong instanceMark = Time.GetTicksUsec();
            Engine_LevelBakedLighting.RefreshInstanceLighting(
                region.GetParent() ?? region, lighting);
            _msInstances = SinceMs(ref instanceMark);
        }
    }

    private CloudTheme? _cloudTheme;
    private SkyFrame? _frame;
    private ToneMapTheme? _toneMapTheme;

    private readonly float[] _cloudUvX = new float[4];
    private readonly float[] _cloudUvY = new float[4];
    private readonly Dictionary<int, (byte[] Alpha, int W, int H)> _cloudAlpha = new();

    private Godot.Compositor? _editorCompositor;
    private Camera3D? _editorCamera;

    private void PublishPostFx()
    {
        Engine_PostFxParams.Publish(_toneMapTheme, Theme, _frame, ResolveCamera());
    }

    private void SyncEditorCompositor()
    {
        if (!Engine.IsEditorHint())
        {
            return;
        }

        Camera3D? camera = ResolveCamera();
        if (camera is null || !GodotObject.IsInstanceValid(camera))
        {
            return;
        }

        if (_editorCamera is not null && !ReferenceEquals(camera, _editorCamera))
        {
            DetachEditorCompositor();
        }

        _editorCompositor ??= Engine_LevelLighting.BuildCompositor();
        if (_editorCompositor is null)
        {
            return;
        }

        if (camera.Compositor != _editorCompositor)
        {
            camera.Compositor = _editorCompositor;
        }

        _editorCamera = camera;
    }

    private void DetachEditorCompositor()
    {
        if (_editorCamera is not null &&
            GodotObject.IsInstanceValid(_editorCamera) &&
            _editorCamera.Compositor == _editorCompositor)
        {
            _editorCamera.Compositor = null;
        }

        _editorCamera = null;
        _editorCompositor = null;
    }

    private Camera3D? ResolveCamera()
    {
        Camera3D? camera = GetViewport()?.GetCamera3D();
#if TOOLS
        if (camera is null && Engine.IsEditorHint())
        {
            camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }
#endif
        return camera;
    }

    private void AnimateSky(float delta)
    {
        Node3D? clouds = GetParent()?.GetNodeOrNull<Node3D>("Clouds");
        CloudTheme? cloudTheme = _cloudTheme;
        if (clouds is not null && cloudTheme is not null)
        {
            AdvanceCloudLayers(clouds, cloudTheme, delta);
        }

        SkyFrame? frame = _frame;
        Node3D? celestial = GetParent()?.GetNodeOrNull<Node3D>(
            Engine_CelestialElements.NodeName);
        if (celestial is null || frame is null)
        {
            return;
        }

        Camera3D? camera = ResolveCamera();

        Vector3 right = camera?.GlobalTransform.Basis.X ?? Vector3.Right;
        Vector3 up = camera?.GlobalTransform.Basis.Y ?? Vector3.Up;

        if (celestial.GetNodeOrNull<MeshInstance3D>("MoonGlare") is
                { MaterialOverride: ShaderMaterial moonGlare })
        {
            Vector3 moonDir = Engine_SkyFrame.ToGodot(frame.MoonDirection);
            float vis = CloudVisibility(clouds, cloudTheme, moonDir, right, up);
            moonGlare.SetShaderParameter("element_vis", vis);
        }

        if (celestial.GetNodeOrNull<MeshInstance3D>("SunGlare") is
                { MaterialOverride: ShaderMaterial sunGlare })
        {
            Vector3 sunDir = Engine_SkyFrame.ToGodot(frame.SunDirection);
            float vis = CloudVisibility(clouds, cloudTheme, sunDir, right, up);
            sunGlare.SetShaderParameter("element_vis", vis);
        }
    }

    private void AdvanceCloudLayers(Node3D clouds, CloudTheme theme, float delta)
    {
        foreach (Node child in clouds.GetChildren())
        {
            if (child is not MeshInstance3D mesh ||
                mesh.MaterialOverride is not ShaderMaterial material)
            {
                continue;
            }

            string name = child.Name.ToString();
            if (!name.StartsWith("CloudLayer", System.StringComparison.Ordinal) ||
                !int.TryParse(name.AsSpan("CloudLayer".Length), out int index) ||
                index < 0 || index >= theme.Layers.Length)
            {
                continue;
            }

            CloudLayerTheme layer = theme.Layers[index];
            _cloudUvX[index] = WrapUnit(
                _cloudUvX[index] +
                (layer.VelocityX * Engine_Clouds.VelocityScale * delta));
            _cloudUvY[index] = WrapUnit(
                _cloudUvY[index] +
                (layer.VelocityY * Engine_Clouds.VelocityScale * delta));

            Vector4 current = material.GetShaderParameter("uv_scale_offset").AsVector4();
            material.SetShaderParameter("uv_scale_offset", new Vector4(
                current.X, current.Y, _cloudUvX[index], _cloudUvY[index]));
        }
    }

    private static float WrapUnit(float value)
    {
        value -= Mathf.Floor(value);
        return float.IsFinite(value) ? value : 0f;
    }

    private float CloudVisibility(
        Node3D? clouds, CloudTheme? theme, Vector3 direction, Vector3 right, Vector3 up)
    {
        if (clouds is null || theme is null || direction.Y <= 0.02f)
        {
            return 1.0f;
        }

        Span<Vector2> offsets = stackalloc Vector2[]
        {
            new Vector2(0f, 0f), new Vector2(0.03f, 0f), new Vector2(-0.03f, 0f),
            new Vector2(0f, 0.03f), new Vector2(0f, -0.03f),
        };

        float visSum = 0f;
        foreach (Vector2 offset in offsets)
        {
            Vector3 d = direction + (right * offset.X) + (up * offset.Y);
            float vis = 1f;
            for (int i = 0; i < theme.Layers.Length && i < 4; i++)
            {
                CloudLayerTheme cfg = theme.Layers[i];
                if (cfg.Transparency <= 0.001f || cfg.SizeX <= 1f || cfg.SizeY <= 1f ||
                    d.Y <= 0.02f)
                {
                    continue;
                }

                if (!TryCloudAlpha(clouds, i, out (byte[] Alpha, int W, int H) map))
                {
                    continue;
                }

                float s = cfg.Height / d.Y;
                if (s <= 0f)
                {
                    continue;
                }

                float hx = d.X * s;
                float hz = d.Z * s;
                float dist = Mathf.Sqrt((hx * hx) + (hz * hz));
                float fade = 1f - Mathf.Clamp((dist - 1000f) * 0.001f, 0f, 1f);
                if (fade <= 0f)
                {
                    continue;
                }

                float u = (((hx + cfg.SizeX) / (2f * cfg.SizeX)) * cfg.TextureScaleX) +
                          _cloudUvX[i];
                float v = (((hz + cfg.SizeY) / (2f * cfg.SizeY)) * cfg.TextureScaleY) +
                          _cloudUvY[i];
                float a = SampleAlphaBilinear(map.Alpha, map.W, map.H, u, v);
                float coverage = Mathf.Clamp(a * cfg.Transparency * fade, 0f, 1f);
                vis *= 1f - coverage;
            }

            visSum += vis;
        }

        return Mathf.Clamp(visSum / 5f, 0f, 1f);
    }

    private bool TryCloudAlpha(
        Node3D clouds, int index, out (byte[] Alpha, int W, int H) map)
    {
        if (_cloudAlpha.TryGetValue(index, out map))
        {
            return map.Alpha.Length != 0;
        }

        map = (System.Array.Empty<byte>(), 0, 0);
        if (clouds.GetNodeOrNull<MeshInstance3D>($"CloudLayer{index}") is not
                { MaterialOverride: ShaderMaterial material })
        {
            _cloudAlpha[index] = map;
            return false;
        }

        var texture = material.GetShaderParameter("cloud_density").As<Texture2D>();
        Image? image = texture?.GetImage();
        if (image is null)
        {
            _cloudAlpha[index] = map;
            return false;
        }

        if (image.IsCompressed())
        {
            image.Decompress();
        }

        int w = image.GetWidth();
        int h = image.GetHeight();
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                alpha[(y * w) + x] = (byte)(image.GetPixel(x, y).A * 255f);
            }
        }

        map = (alpha, w, h);
        _cloudAlpha[index] = map;
        return true;
    }

    private static float SampleAlphaBilinear(byte[] alpha, int w, int h, float u, float v)
    {
        if (alpha.Length == 0 || w <= 0 || h <= 0)
        {
            return 0f;
        }

        u -= Mathf.Floor(u);
        v -= Mathf.Floor(v);
        float fx = (u * w) - 0.5f;
        float fy = (v * h) - 0.5f;
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        float tx = fx - x0;
        float ty = fy - y0;

        float Tap(int x, int y)
        {
            x %= w;
            if (x < 0)
            {
                x += w;
            }

            y %= h;
            if (y < 0)
            {
                y += h;
            }

            return alpha[(y * w) + x] / 255f;
        }

        float a00 = Tap(x0, y0);
        float a10 = Tap(x0 + 1, y0);
        float a01 = Tap(x0, y0 + 1);
        float a11 = Tap(x0 + 1, y0 + 1);
        return (((a00 * (1f - tx)) + (a10 * tx)) * (1f - ty)) +
               (((a01 * (1f - tx)) + (a11 * tx)) * ty);
    }

    private bool EnsureTheme()
    {
        if (Theme is not null && LightingGdbs.Count != 0)
        {
            return true;
        }

        List<byte[]> chain = Engine_SkyEnvironment.BuildGdbChain(
            LevelGdbPath, Engine_BnkMeshLoader.ReadBytes);
        if (chain.Count == 0)
        {
            return Theme is not null;
        }

        if (Theme is null)
        {
            if (!Engine_EnvironmentTheme.TryExtractSky(chain, out SkyTheme sky))
            {
                return false;
            }

            Theme = sky;
        }

        if (LightingGdbs.Count == 0)
        {
            LightingGdbs.AddRange(chain);
        }

        return true;
    }

    private DirectionalLight3D? ResolveSun()
    {
        if (Sun is not null && IsInstanceValid(Sun))
        {
            return Sun;
        }

        Sun = GetParent()?.GetNodeOrNull<DirectionalLight3D>(
            Engine_LevelLighting.SunNodeName);
        return Sun;
    }
}
