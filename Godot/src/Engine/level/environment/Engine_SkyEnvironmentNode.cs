using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;
using Refii.Models;

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
        Reevaluate();
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
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

        AnimateSky((float)delta);
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

        if (LightingGdbs.Count != 0 &&
            Engine_ThemeTimeline.TryExtractSkyBlended(
                LightingGdbs, _dayFraction, out SkyTheme timeTheme))
        {
            Theme = timeTheme;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);

        (AtmosphereState domeState, ImageTexture domeLut) =
            Engine_SkyEnvironment.ComputeDome(frame);

        if (Environment?.Sky?.SkyMaterial is ShaderMaterial material)
        {
            Engine_SkyEnvironment.ApplyDomeUniforms(material, frame, domeState, domeLut);
        }

        Node3D? water = GetParent()?.GetNodeOrNull<Node3D>(
            Engine_LevelScene.WaterNodeName);
        if (water is not null)
        {
            foreach (Node child in water.GetChildren())
            {
                if (child is MeshInstance3D waterMesh &&
                    waterMesh.MaterialOverride is ShaderMaterial waterMaterial)
                {
                    Engine_SkyEnvironment.ApplyWaterFrame(
                        waterMaterial, frame, domeState, domeLut);
                }
            }
        }

        Node3D? celestial = GetParent()?.GetNodeOrNull<Node3D>(
            Engine_CelestialElements.NodeName);
        if (celestial is not null)
        {
            Engine_CelestialElements.Update(celestial, Theme, frame);
        }

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

        if (LightingGdbs.Count == 0 ||
            !Engine_ThemeTimeline.TryExtractLightingBlended(
                LightingGdbs, _dayFraction, out LightingTheme lightingTheme))
        {
            return;
        }

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
            Engine_LevelBakedLighting.RefreshInstanceLighting(
                region.GetParent() ?? region, lighting);
        }
    }

    private CloudTheme? _cloudTheme;
    private SkyFrame? _frame;
    private readonly float[] _cloudUvX = new float[4];
    private readonly float[] _cloudUvY = new float[4];
    private readonly Dictionary<int, (byte[] Alpha, int W, int H)> _cloudAlpha = new();

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

        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera is null && Engine.IsEditorHint())
        {
            camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }

        Vector3 right = camera?.GlobalTransform.Basis.X ?? Vector3.Right;
        Vector3 up = camera?.GlobalTransform.Basis.Y ?? Vector3.Up;

        if (celestial.GetNodeOrNull<MeshInstance3D>("MoonGlare") is
                { MaterialOverride: ShaderMaterial moonGlare })
        {
            var moonDir = new Vector3(
                -frame.MoonDirection[0], frame.MoonDirection[1], frame.MoonDirection[2]);
            float vis = CloudVisibility(clouds, cloudTheme, moonDir, right, up);
            moonGlare.SetShaderParameter("element_vis", vis);
        }

        if (celestial.GetNodeOrNull<MeshInstance3D>("SunGlare") is
                { MaterialOverride: ShaderMaterial sunGlare })
        {
            var sunDir = new Vector3(
                -frame.SunDirection[0], frame.SunDirection[1], frame.SunDirection[2]);
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
