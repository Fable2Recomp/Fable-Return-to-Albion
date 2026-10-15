using System;
using Godot;
using Refii.Host;
using Refii.Models;
using Refii.Textures;

namespace Refii.Level;

/// <summary>
/// The classic renderer's rain and snow: the title's own SKY_RAIN and SKY_SNOW pairs on the
/// geometry its draws build, with the constants they set.
/// </summary>
/// <remarks>
/// THE RAIN (`0x822803D0`). Gated on the scene theme's RAIN_DENSITY d > 0.05 (0x82280450); D, S
/// and W are d, RAIN_SIZE and the wind speed x 0.05, saturated. Six vertical curtains
/// (`sub_82A77840`) stand across the view at 6k + 1.5 ahead of the eye (k = 5..0, farthest
/// first), each cut where the side planes of the frustum cross it at its top and bottom planes'
/// heights, with a random texture scale (dist + 1.4 x rand/255) / dist from the LCG seeded 12345
/// (`dword_83321694`). Each is a quad of XYZ + UV: u from the end points along the camera's
/// right axis x (0.5 - 0.4 S), v from the heights x (0.21 - 0.18 S), each offset by a random /256
/// from the LCG seeded 1 (`dword_83321698`), the bottom row skewed by the wind plus jitter x 0.3 W
/// (0x822819A0..0x82281DD8). Constants: VS c8 the eye, c46 its view direction; PS c46 = c31 =
/// (0.25 x the light zone's colour, or 0.1, 0.25 (1 - D)) (0x82281474), c116..121 the three
/// nearest of the scene's lights within 20 (0x8228074C), colour x 0.25 x a fade from 15 to 20.
/// Blend ONE / INVSRCALPHA, depth test, no depth write, no cull. The guest also cuts the curtains
/// at the roofs of nearby buildings (`sub_82A771D0` on the roof list `sub_82A789B0` builds; the
/// rain passes each roof's corners reversed, which matches the test's winding, so the cut fires).
///
/// THE SNOW (`0x821BDE68`). Gated on SNOW_SIZE > 0.05. The box around the eye and the frustum's
/// corners 30 out, raised 2.5 and floored at 0 (0x821BE6C0..0x821BE6F0), is walked in 16-unit
/// blocks from (floor(min) &amp; ~15) + 3 (0x821BE748); a block inside the frustum with a corner
/// within 30 of the eye is a POINTLIST of 4096 whose vertex shader places each flake from its
/// index. Constants: c47 the clock (`dbl_83496E18`), c72 the eye, c75 (25, 30, width x size x
/// 0.109375, 1), c144 (wind x W x 0.7, fall speed x 0.7, 1), c128..143 / c73..74 the spell pushes
/// (the weather primitive's 8 slots, `sub_82A77F60`, through the weather export), per block c8 its
/// origin, c46 (16, 16, 16, 1), c113..127 its roof planes (up to 3 roofs of 5 planes over the
/// block, `0x821BF8B8`); PS c31 0.75 + 0.25 x the light
/// zone's colour. Blend ONE / ONE, point size 1..64.
///
/// Both random sequences step once per title frame (30 a second), as the draws did.
/// </remarks>
public partial class Engine_ClassicWeather : Node3D
{
    private const float Gate = 0.05f;
    private const int Curtains = 6;
    /// <summary>Curtain segments after the roof cut (`sub_82A771D0` stops at 32).</summary>
    private const int MaxSegments = 32;
    private const int SnowPoints = 4096;
    private const float SnowBlock = 16f;
    private const float SnowReach = 30f;
    private const int MaxBlocks = 96;
    private const int MaxLights = 64;
    private const double TitleFrame = 1.0 / 30.0;

    private const string RainVertex = "VSHADER_SKY_RAIN";
    private const string RainPixel = "PSHADER_SKY_RAIN";
    private const string SnowVertex = "VSHADER_SKY_SNOW";
    private const string SnowPixel = "PSHADER_SKY_SNOW";
    private const string RainTexture = "Globals/Engine/Textures/RainStreaks.tex";
    private const string SnowTexture = "Globals/Engine/Textures/Snowflakes.tex";

    private static readonly StringName GridOrigin = "vs_g_GridOrigin";

    private readonly float[] _weather = new float[HostBridge.ClassicWeatherHeader + MaxLights * 8];

    // Rain.
    private MeshInstance3D? _rain;
    private ArrayMesh? _rainMesh;
    private ShaderMaterial? _rainMaterial;
    private bool _rainTried;
    private readonly float[] _records = new float[MaxSegments * 7];
    /// <summary>Per segment, the curtain it was cut from (its random bytes are the curtain's).</summary>
    private readonly int[] _segmentCurtain = new int[MaxSegments];
    private readonly byte[] _curtainRandom = new byte[Curtains];
    private readonly byte[] _vertexRandom = new byte[Curtains * 4];
    private readonly byte[] _rainPositions = new byte[MaxSegments * 4 * 12];
    private readonly byte[] _rainUvs = new byte[MaxSegments * 4 * 8];
    private readonly Vector4[] _rainLights = new Vector4[6];
    private readonly (float Distance, int Index)[] _lightOrder = new (float, int)[MaxLights];
    private uint _curtainSeed = 12345u;
    private uint _vertexSeed = 1u;
    private long _tick = -1;

    // Snow.
    private ArrayMesh? _snowMesh;
    private ShaderMaterial? _snowMaterial;
    private bool _snowTried;
    private readonly MeshInstance3D?[] _blocks = new MeshInstance3D?[MaxBlocks];
    /// <summary>A block under roofs draws on a copy of the snow material with its own g_ClipShape.</summary>
    private readonly ShaderMaterial?[] _blockMaterials = new ShaderMaterial?[MaxBlocks];
    private readonly System.Collections.Generic.List<ShaderMaterial> _roofed = new();
    private readonly Vector4[] _blockClip = new Vector4[15];
    private readonly Vector4[] _push = new Vector4[16];
    private readonly Vector4[] _pushOverDistance = new Vector4[2];
    private readonly Vector4[] _clipShape = new Vector4[15];

    private readonly Plane[] _planes = new Plane[6];
    private readonly Vector3[] _corners = new Vector3[8];

    /// <summary>The live instance.</summary>
    public static Engine_ClassicWeather? Instance { get; private set; }

    /// <summary>Snow blocks drawn at the last pump.</summary>
    public int SnowBlocks { get; private set; }

    /// <summary>Whether the rain drew at the last pump.</summary>
    public bool Raining { get; private set; }

    /// <summary>The top light zone's colour (Z+0xD0) at the last pump; zero with no zone stacked.</summary>
    public static Vector3 ZoneColour { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        Name = "ClassicWeather";
        // The translated stages compose XR_FILE_TO_MODEL, which takes a guest point to Godot as
        // (x, z, -y); the world is (-x, z, y), so the node is that half-turn about Y and a
        // vertex is written as (x, z, -y) of its guest position (as Engine_ClassicSkyDome).
        TopLevel = true;
        Transform = new Transform3D(Basis.FromScale(new Vector3(-1f, 1f, -1f)), Vector3.Zero);
        for (int i = 0; i < _clipShape.Length; i++)
        {
            _clipShape[i] = new Vector4(0f, 0f, 0f, 1f);
        }
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>Draws this frame's rain and snow. Main thread, after the frame feed.</summary>
    public void Pump(Camera3D camera, Vector2 viewport)
    {
        if (!Engine_ClassicTheme.Live)
        {
            HideRain();
            HideSnow(0);
            return;
        }

        uint got = HostBridge.GetClassicWeather(_weather);
        bool zone = got >= HostBridge.ClassicWeatherHeader && _weather[0] != 0f;
        var zoneColour = zone ? new Vector3(_weather[1], _weather[2], _weather[3]) : Vector3.Zero;
        ZoneColour = zoneColour;
        StatePushes(got);
        float clock = got >= HostBridge.ClassicWeatherHeader ? _weather[4] : Engine_ClassicFeed.Seconds;
        int lights = got >= HostBridge.ClassicWeatherHeader
            ? Math.Clamp((int)_weather[5], 0, (int)((got - HostBridge.ClassicWeatherHeader) / 8))
            : 0;

        Vector4 wind = Engine_ClassicFeed.Wind;
        float w = Sat(wind.W * Gate);

        Godot.Collections.Array<Plane> frustum = camera.GetFrustum();
        for (int i = 0; i < 6 && i < frustum.Count; i++)
        {
            _planes[i] = ToGuest(frustum[i]);
        }
        ((Godot.Collections.Array)frustum).Dispose();

        long tick = (long)(Time.GetTicksMsec() / 1000.0 / TitleFrame);
        bool step = tick != _tick;
        _tick = tick;

        Vector3 eye = ToGuest(camera.GlobalPosition);
        Vector3 forward = ToGuest(-camera.GlobalBasis.Z);
        Vector3 right = ToGuest(camera.GlobalBasis.X);

        using (Refii.Core.Engine_FramePhases.Measure("classic.rain"))
        {
            PumpRain(eye, forward, right, wind, w, zone, zoneColour, lights, step);
        }
        using (Refii.Core.Engine_FramePhases.Measure("classic.snow"))
        {
            PumpSnow(camera, viewport, eye, wind, w, zone, zoneColour, clock);
        }
    }

    private void PumpRain(Vector3 eye, Vector3 forward, Vector3 right, Vector4 wind, float w,
        bool zone, Vector3 zoneColour, int lights, bool step)
    {
        float d = Engine_ClassicTheme.Param(74);
        float s = Engine_ClassicTheme.Param(73);
        if (!(d > Gate) || !EnsureRain())
        {
            HideRain();
            return;
        }
        float density = Sat(d), size = Sat(s);

        if (step)
        {
            for (int i = 0; i < Curtains; i++)
            {
                _curtainRandom[i] = (byte)((_curtainSeed >> 7) & 0xFF);
                _curtainSeed = _curtainSeed * 69069u + 1u;
            }
            for (int i = 0; i < _vertexRandom.Length; i++)
            {
                _vertexRandom[i] = (byte)((_vertexSeed >> 7) & 0xFF);
                _vertexSeed = _vertexSeed * 69069u + 1u;
            }
        }

        if (!BuildCurtains(eye, forward))
        {
            HideRain();
            return;
        }
        int segments = Curtains;
        for (int i = 0; i < Curtains; i++)
        {
            _segmentCurtain[i] = i;
        }
        segments = CutAtRoofs(eye, segments);

        float ru = 0.5f - size * 0.4f, rv = 0.21f - size * 0.18f;
        float skewAmount = 0.3f * w;
        for (int i = segments; i < MaxSegments; i++)
        {
            // An unused quad, collapsed.
            for (int k = 0; k < 4; k++)
            {
                Vertex(i * 4 + k, 0f, 0f, -10000f, 0f, 0f);
            }
        }
        for (int i = 0; i < segments; i++)
        {
            int c = _segmentCurtain[i];
            int r = i * 7;
            float ax = _records[r], ay = _records[r + 1], bx = _records[r + 2], by = _records[r + 3];
            float bottom = _records[r + 4], top = _records[r + 5], scale = _records[r + 6];
            float r0 = _vertexRandom[c * 4], r1 = _vertexRandom[c * 4 + 1];
            float r2 = _vertexRandom[c * 4 + 2], r3 = _vertexRandom[c * 4 + 3];
            var jitter = new Vector3(wind.X + (r0 / 255f - 0.5f) * 0.3f, wind.Y + (r1 / 255f - 0.5f) * 0.3f, 0f);
            float ua = (ax * right.X + ay * right.Y) * ru + r2 / 256f;
            float ub = (bx * right.X + by * right.Y) * ru + r2 / 256f;
            float vb = bottom * rv + r3 / 256f, vt = top * rv + r3 / 256f;
            float skew = (top - bottom) * jitter.Dot(right) * skewAmount;
            int v = i * 4;
            Vertex(v, ax, ay, bottom, (ua + skew) * scale, vb * scale);
            Vertex(v + 1, ax, ay, top, ua * scale, vt * scale);
            Vertex(v + 2, bx, by, bottom, (ub + skew) * scale, vb * scale);
            Vertex(v + 3, bx, by, top, ub * scale, vt * scale);
        }
        Rid mesh = _rainMesh!.GetRid();
        RenderingServer.MeshSurfaceUpdateVertexRegion(mesh, 0, 0, _rainPositions);
        RenderingServer.MeshSurfaceUpdateAttributeRegion(mesh, 0, 0, _rainUvs);

        Vector3 colour = zone ? zoneColour * 0.25f : new Vector3(0.1f, 0.1f, 0.1f);
        var tint = new Vector4(colour.X, colour.Y, colour.Z, 0.25f * (1f - density));
        ShaderMaterial m = _rainMaterial!;
        m.SetShaderParameter(VsCameraPosName, new Vector4(eye.X, eye.Y, eye.Z, 1f));
        m.SetShaderParameter(VsCameraDirName, new Vector4(forward.X, forward.Y, forward.Z, 0f));
        m.SetShaderParameter(PsRainDiffColourName, tint);
        m.SetShaderParameter(PsRainSpecColourName, tint);
        RainLights(eye, lights);
        m.SetShaderParameter(PsRainLightName, _rainLights);

        _rain!.Visible = true;
        Raining = true;
    }

    /// <summary>
    /// `sub_82A77840`: six curtains across the view, farthest first, as (a.xy, b.xy, bottom, top,
    /// texture scale). False when the view looks straight up or down, where the guest's builder
    /// returns 0 (and its caller draws whatever the stack held).
    /// </summary>
    private bool BuildCurtains(Vector3 eye, Vector3 forward)
    {
        float horizontal = forward.X * forward.X + forward.Y * forward.Y;
        if (horizontal < 0.01f)
        {
            return false;
        }
        float inverse = 1f / MathF.Sqrt(horizontal);
        var ahead = new Vector3(forward.X * inverse, forward.Y * inverse, 0f);
        var across = new Vector3(-ahead.Y, ahead.X, 0f);
        Plane left = _planes[2], topPlane = _planes[3], rightPlane = _planes[4], bottomPlane = _planes[5];

        for (int k = Curtains - 1; k >= 0; k--)
        {
            int r = (Curtains - 1 - k) * 7;
            float distance = 6f * k + 1.5f;
            Vector3 centre = eye + ahead * distance;
            float zTop = HeightOn(topPlane, centre.X, centre.Y);
            float zBottom = HeightOn(bottomPlane, centre.X, centre.Y);
            var high = new Vector3(centre.X, centre.Y, zTop);
            var low = new Vector3(centre.X, centre.Y, zBottom);
            float t0 = Hit(left, high, across), t1 = Hit(rightPlane, high, -across);
            float t2 = Hit(left, low, across), t3 = Hit(rightPlane, low, -across);
            Vector3 a = centre + across * MathF.Max(t0, t2);
            Vector3 b = centre - across * MathF.Max(t1, t3);
            _records[r] = a.X;
            _records[r + 1] = a.Y;
            _records[r + 2] = b.X;
            _records[r + 3] = b.Y;
            _records[r + 4] = zBottom;
            _records[r + 5] = zTop;
            _records[r + 6] = (distance + _curtainRandom[Curtains - 1 - k] * 0.0054901959f) / distance;
        }
        return true;
    }

    /// <summary>
    /// The rain's roof cut: the roofs around the curtains (`sub_82A789B0` on the curtains' box)
    /// and, roof by roof, `sub_82A771D0` with the corners passed reversed (0x822806A8..DC): a
    /// segment whose bottom is under the roof's top and which reaches inside it is split at the
    /// roof's edges; a piece whose middle is inside starts at the roof's top. Segments left with
    /// no height go. Returns the segments left.
    /// </summary>
    private int CutAtRoofs(Vector3 eye, int count)
    {
        if (Engine_ClassicRoofs.Count == 0)
        {
            return count;
        }
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < count; i++)
        {
            int r = i * 7;
            min = min.Min(new Vector3(MathF.Min(_records[r], _records[r + 2]), MathF.Min(_records[r + 1], _records[r + 3]), _records[r + 4]));
            max = max.Max(new Vector3(MathF.Max(_records[r], _records[r + 2]), MathF.Max(_records[r + 1], _records[r + 3]), _records[r + 5]));
        }
        Engine_ClassicRoofs.Build(min, max, eye);
        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (Engine_ClassicRoofs.Roof roof in Engine_ClassicRoofs.Roofs)
        {
            corners[0] = roof.C0;
            corners[1] = roof.C3;
            corners[2] = roof.C2;
            corners[3] = roof.C1;
            count = CutAtRoof(corners, roof.Top, count);
        }
        return count;
    }

    /// <summary>The right-handed cross of p against the edge c -> n; > 0 is inside for the reversed corners.</summary>
    private static float Side(Vector2 c, Vector2 n, Vector2 p) => (p.X - c.X) * (n.Y - c.Y) - (p.Y - c.Y) * (n.X - c.X);

    private static bool Inside(ReadOnlySpan<Vector2> corners, Vector2 p)
    {
        for (int i = 0; i < 4; i++)
        {
            if (Side(corners[i], corners[(i + 1) & 3], p) <= 0f)
            {
                return false;
            }
        }
        return true;
    }

    private int CutAtRoof(ReadOnlySpan<Vector2> corners, float top, int count)
    {
        Span<float> hits = stackalloc float[4];
        int original = count;
        for (int s = 0; s < original; s++)
        {
            int r = s * 7;
            if (!(_records[r + 4] < top))
            {
                continue;
            }
            var a = new Vector2(_records[r], _records[r + 1]);
            var b = new Vector2(_records[r + 2], _records[r + 3]);
            bool outside = false;
            for (int i = 0; i < 4 && !outside; i++)
            {
                Vector2 c = corners[i], n = corners[(i + 1) & 3];
                outside = Side(c, n, a) <= 0f && Side(c, n, b) <= 0f;
            }
            if (outside)
            {
                continue;
            }
            int found = 0;
            for (int i = 0; i < 4; i++)
            {
                if (Crossing(a, b, corners[i], corners[(i + 1) & 3], out float t))
                {
                    hits[found++] = t;
                }
            }
            if (found == 0)
            {
                if (Inside(corners, (a + b) * 0.5f))
                {
                    _records[r + 4] = top;
                }
                continue;
            }
            hits.Slice(0, found).Sort();
            float bottom = _records[r + 4], height = _records[r + 5], scale = _records[r + 6];
            for (int piece = 0; piece <= found && count < MaxSegments; piece++)
            {
                float t0 = piece == 0 ? 0f : hits[piece - 1];
                float t1 = piece == found ? 1f : hits[piece];
                Vector2 p0 = a.Lerp(b, t0), p1 = a.Lerp(b, t1);
                int o = count * 7;
                _records[o] = p0.X;
                _records[o + 1] = p0.Y;
                _records[o + 2] = p1.X;
                _records[o + 3] = p1.Y;
                _records[o + 4] = Inside(corners, (p0 + p1) * 0.5f) ? top : bottom;
                _records[o + 5] = height;
                _records[o + 6] = scale;
                _segmentCurtain[count] = _segmentCurtain[s];
                if (_records[o + 5] > _records[o + 4])
                {
                    ++count;
                }
            }
            _records[r + 4] = 0f;
            _records[r + 5] = 0f;
        }
        // Drop what has no height left, the last one moved into its place.
        for (int s = 0; s < count; s++)
        {
            int r = s * 7;
            if (_records[r + 5] <= _records[r + 4])
            {
                --count;
                Array.Copy(_records, count * 7, _records, r, 7);
                _segmentCurtain[s] = _segmentCurtain[count];
                --s;
            }
        }
        return count;
    }

    /// <summary>`sub_82A77050`: where the segment a -> b crosses the edge c -> n, as t along a -> b.</summary>
    private static bool Crossing(Vector2 a, Vector2 b, Vector2 c, Vector2 n, out float t)
    {
        t = 0f;
        Vector2 r = b - a, e = n - c;
        float denominator = r.X * e.Y - r.Y * e.X;
        if (MathF.Abs(denominator) < 1e-12f)
        {
            return false;
        }
        Vector2 q = c - a;
        t = (q.X * e.Y - q.Y * e.X) / denominator;
        float u = (q.X * r.Y - q.Y * r.X) / denominator;
        return t > 0f && t < 1f && u >= 0f && u <= 1f;
    }

    /// <summary>`sub_82A776D0`: how far along <paramref name="direction"/> the plane is, or -1 / -2.</summary>
    private static float Hit(Plane plane, Vector3 point, Vector3 direction)
    {
        float distance = plane.Normal.Dot(point) - plane.D;
        float along = plane.Normal.Dot(direction);
        if (distance == 0f)
        {
            return 0f;
        }
        if ((distance > 0f && along >= 0f) || (distance < 0f && along <= 0f))
        {
            return -1f;
        }
        if (MathF.Abs(along) < 0.001f)
        {
            return -2f;
        }
        return -distance / along;
    }

    /// <summary>The plane's height over (x, y), 0 for a near-vertical plane (`sub_82A77778`).</summary>
    private static float HeightOn(Plane plane, float x, float y)
    {
        float c = plane.Normal.Z;
        if (MathF.Abs(c) < 0.01f)
        {
            return 0f;
        }
        return -(plane.Normal.X * x + plane.Normal.Y * y - plane.D) / c;
    }

    /// <summary>The three nearest lights within 20 (0x8228074C), as `{PosRadius, Colour}`.</summary>
    private void RainLights(Vector3 eye, int count)
    {
        int kept = 0;
        for (int i = 0; i < count && i < MaxLights; i++)
        {
            int at = HostBridge.ClassicWeatherHeader + i * 8;
            var position = new Vector3(_weather[at], _weather[at + 1], _weather[at + 2]);
            float squared = eye.DistanceSquaredTo(position);
            if (squared < 400f)
            {
                _lightOrder[kept++] = (squared, at);
            }
        }
        Array.Sort(_lightOrder, 0, kept, System.Collections.Generic.Comparer<(float, int)>.Default);
        for (int slot = 0; slot < 3; slot++)
        {
            if (slot >= kept)
            {
                _rainLights[slot * 2] = Vector4.Zero;
                _rainLights[slot * 2 + 1] = Vector4.Zero;
                continue;
            }
            int at = _lightOrder[slot].Index;
            float distance = MathF.Sqrt(_lightOrder[slot].Distance);
            float radius = _weather[at + 3];
            float fade = Sat(1f - (distance - 15f) / 5f);
            if (distance < 5f)
            {
                fade *= (distance / 5f + 1f) * 0.5f;
            }
            float scale = 0.25f * fade;
            _rainLights[slot * 2] = new Vector4(_weather[at], _weather[at + 1], _weather[at + 2],
                radius != 0f ? 1f / (radius * radius) : 0f);
            _rainLights[slot * 2 + 1] = new Vector4(_weather[at + 4] * scale, _weather[at + 5] * scale,
                _weather[at + 6] * scale, 0f);
        }
    }

    private void Vertex(int index, float x, float y, float z, float u, float v)
    {
        // Guest (x, y, z) written as the model point (x, z, -y): see _Ready.
        Span<float> position = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            _rainPositions.AsSpan(index * 12, 12));
        position[0] = x;
        position[1] = z;
        position[2] = -y;
        Span<float> uv = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            _rainUvs.AsSpan(index * 8, 8));
        uv[0] = u;
        uv[1] = v;
    }

    private bool EnsureRain()
    {
        if (_rainMaterial is not null)
        {
            return true;
        }
        if (_rainTried)
        {
            return false;
        }
        _rainTried = true;

        Shader? shader = Engine_ClassicShaders.Compose(RainVertex, RainPixel,
            "unshaded, cull_disabled, depth_draw_never, blend_premul_alpha");
        // A translation from before the rain's own constants were named reads them from frame
        // registers the feed fills with other things: until the shaders are rebuilt, no rain.
        if (shader is null || !shader.Code.Contains("vs_g_CameraPos", StringComparison.Ordinal))
        {
            GD.Print("[classic] the rain shaders predate their named constants; rain stays off until the shaders are compiled again");
            return false;
        }
        var material = new ShaderMaterial { Shader = shader };
        // The draw names `Textures\RainStreaks.tex`; the title's engine textures resolve under
        // Globals\Engine, which is where the bank holds it.
        Texture2D? streaks = Engine_TexLibrary.Get(RainTexture, TexSemantic.Unknown,
            Engine_BnkMeshLoader.ReadBytes, out _);
        if (streaks is null)
        {
            GD.Print($"[classic] {RainTexture} did not load; no rain");
            return false;
        }
        material.SetShaderParameter(PsRainTextureSamplerName, streaks);

        // A quad per segment, two triangles each as the draw indexes them (b, b+1, b+2, b+3, b+2, b+1).
        var indices = new int[MaxSegments * 6];
        for (int i = 0; i < MaxSegments; i++)
        {
            int b = i * 4;
            indices[i * 6] = b;
            indices[i * 6 + 1] = b + 1;
            indices[i * 6 + 2] = b + 2;
            indices[i * 6 + 3] = b + 3;
            indices[i * 6 + 4] = b + 2;
            indices[i * 6 + 5] = b + 1;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[MaxSegments * 4];
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[MaxSegments * 4];
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        arrays.Dispose();
        mesh.SurfaceSetMaterial(0, material);

        // The regions written each frame are the uncompressed streams: 12 bytes of position,
        // 8 of UV, per vertex.
        long format = (long)mesh.SurfaceGetFormat(0);
        if (RenderingServer.MeshSurfaceGetFormatVertexStride((RenderingServer.ArrayFormat)format, MaxSegments * 4) != 12 ||
            RenderingServer.MeshSurfaceGetFormatAttributeStride((RenderingServer.ArrayFormat)format, MaxSegments * 4) != 8)
        {
            GD.Print("[classic] the rain mesh's streams are not the expected layout; no rain");
            return false;
        }

        _rainMesh = mesh;
        _rain = new MeshInstance3D
        {
            Name = "ClassicRain",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = Engine_ClassicParticles.Layer,
            // The curtains move with the eye every frame; the mesh's own box is the empty one.
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_rain);
        _rainMaterial = material;
        return true;
    }

    private void HideRain()
    {
        if (_rain is not null)
        {
            _rain.Visible = false;
        }
        Raining = false;
    }

    private void PumpSnow(Camera3D camera, Vector2 viewport, Vector3 eye, Vector4 wind, float w,
        bool zone, Vector3 zoneColour, float clock)
    {
        float sizeRaw = Engine_ClassicTheme.Param(71);
        if (!(sizeRaw > Gate) || !EnsureSnow())
        {
            HideSnow(0);
            return;
        }
        float size = Sat(sizeRaw), fall = Sat(Engine_ClassicTheme.Param(72));

        // The region: the eye and the frustum's four corners 30 out, raised 2.5, floored at 0.
        Vector3 min = eye, max = eye;
        Vector2 extent = camera.GetViewport().GetVisibleRect().Size;
        for (int c = 0; c < 4; c++)
        {
            var screen = new Vector2((c & 1) != 0 ? extent.X : 0f, (c & 2) != 0 ? extent.Y : 0f);
            Vector3 corner = ToGuest(camera.ProjectPosition(screen, SnowReach));
            min = min.Min(corner);
            max = max.Max(corner);
        }
        max.Z += 2.5f;
        if (min.Z < 0f)
        {
            min.Z = 0f;
        }
        int sx = ((int)MathF.Floor(min.X) & ~15) + 3, sy = ((int)MathF.Floor(min.Y) & ~15) + 3, sz = ((int)MathF.Floor(min.Z) & ~15) + 3;
        int ex = (int)MathF.Floor(max.X) + 1, ey = (int)MathF.Floor(max.Y) + 1, ez = (int)MathF.Floor(max.Z) + 1;

        bool roofs = Engine_ClassicRoofs.Count != 0;
        if (roofs)
        {
            Engine_ClassicRoofs.Build(min, max, eye);
            roofs = Engine_ClassicRoofs.Roofs.Count != 0;
        }
        _roofed.Clear();
        int used = 0;
        for (int x = sx; x < ex && used < MaxBlocks; x += (int)SnowBlock)
        {
            for (int y = sy; y < ey && used < MaxBlocks; y += (int)SnowBlock)
            {
                for (int z = sz; z < ez && used < MaxBlocks; z += (int)SnowBlock)
                {
                    var origin = new Vector3(x, y, z);
                    if (!InFrustum(origin - new Vector3(0f, 2.5f, 0f), origin + new Vector3(SnowBlock, SnowBlock, SnowBlock)) ||
                        !CornerWithin(origin, eye))
                    {
                        continue;
                    }
                    int index = used++;
                    MeshInstance3D block = Block(index);
                    block.SetInstanceShaderParameter(GridOrigin, new Vector4(x, y, z, 0f));
                    block.Visible = true;
                    if (roofs && BlockRoofs(origin))
                    {
                        ShaderMaterial own = _blockMaterials[index] ??= (ShaderMaterial)_snowMaterial!.Duplicate();
                        own.SetShaderParameter(VsClipShapeName, _blockClip);
                        block.MaterialOverride = own;
                        _roofed.Add(own);
                    }
                    else if (block.MaterialOverride is not null)
                    {
                        block.MaterialOverride = null;
                    }
                }
            }
        }
        HideSnow(used);
        SnowBlocks = used;

        SnowShared(_snowMaterial!, eye, wind, w, size, fall, viewport, zone, zoneColour, clock);
        foreach (ShaderMaterial own in _roofed)
        {
            SnowShared(own, eye, wind, w, size, fall, viewport, zone, zoneColour, clock);
            own.SetShaderParameter(VsPushVectorName, _push);
            own.SetShaderParameter(VsPushVectorOverDistName, _pushOverDistance);
        }
    }

    /// <summary>
    /// A block's roof planes (`0x821BF8B8..0x821BFBF0`): up to three roofs whose box meets the
    /// block and which no edge leaves wholly outside, each as its four sides (normal (dy, -dx, 0)
    /// of the edge, outward for the corners' anticlockwise order) and its top (0, 0, 1, -top); a
    /// flake inside all five of one roof is not drawn. An unused roof is (0, 0, 0, 1) then zeros.
    /// False when no roof covers the block.
    /// </summary>
    private bool BlockRoofs(Vector3 origin)
    {
        Vector3 blockMax = origin + new Vector3(SnowBlock, SnowBlock, SnowBlock);
        Span<Vector2> corners = stackalloc Vector2[4];
        int shapes = 0;
        Array.Clear(_blockClip);
        foreach (Engine_ClassicRoofs.Roof roof in Engine_ClassicRoofs.Roofs)
        {
            if (shapes == 3)
            {
                break;
            }
            if (roof.Max.X < origin.X || roof.Min.X > blockMax.X || roof.Max.Y < origin.Y || roof.Min.Y > blockMax.Y ||
                roof.Max.Z < origin.Z || roof.Min.Z > blockMax.Z)
            {
                continue;
            }
            corners[0] = roof.C0;
            corners[1] = roof.C1;
            corners[2] = roof.C2;
            corners[3] = roof.C3;
            bool clear = false;
            for (int i = 0; i < 4 && !clear; i++)
            {
                Vector2 c = corners[i], n = corners[(i + 1) & 3];
                clear = Side(c, n, new Vector2(origin.X, origin.Y)) >= 0f && Side(c, n, new Vector2(blockMax.X, origin.Y)) >= 0f &&
                    Side(c, n, new Vector2(origin.X, blockMax.Y)) >= 0f && Side(c, n, new Vector2(blockMax.X, blockMax.Y)) >= 0f;
            }
            if (clear)
            {
                continue;
            }
            for (int i = 0; i < 4; i++)
            {
                Vector2 c = corners[i], d = corners[(i + 1) & 3] - c;
                var normal = new Vector2(d.Y, -d.X);
                float length = normal.Length();
                normal = length > 1e-4f ? normal / length : Vector2.Zero;
                _blockClip[shapes * 5 + i] = new Vector4(normal.X, normal.Y, 0f, -(normal.X * c.X + normal.Y * c.Y));
            }
            _blockClip[shapes * 5 + 4] = new Vector4(0f, 0f, 1f, -roof.Top);
            ++shapes;
        }
        for (int k = shapes; k < 3; k++)
        {
            _blockClip[k * 5] = new Vector4(0f, 0f, 0f, 1f);
        }
        return shapes != 0;
    }

    private static void SnowShared(ShaderMaterial m, Vector3 eye, Vector4 wind, float w, float size, float fall,
        Vector2 viewport, bool zone, Vector3 zoneColour, float clock)
    {
        m.SetShaderParameter(VsTimeName, new Vector4(clock, clock, clock, clock));
        m.SetShaderParameter(VsCamPosName, new Vector4(eye.X, eye.Y, eye.Z, 1f));
        m.SetShaderParameter(VsSnowParams1Name, new Vector4(SnowReach - 5f, SnowReach, viewport.X * size * 0.109375f, 1f));
        m.SetShaderParameter(VsSnowParams2Name, new Vector4(wind.X * w * 0.7f, wind.Y * w * 0.7f, fall * 0.7f, 1f));
        // The device's POINTSIZE_MIN / MAX (1, 64) were pixels of the title's 1280-wide target.
        float points = viewport.X / 1280f;
        m.SetShaderParameter(XrPointRangeName, new Vector2(points, 64f * points));
        Vector3 colour = zone ? Vector3.One * 0.75f + zoneColour * 0.25f : Vector3.One;
        m.SetShaderParameter(PsSnowColourName, new Vector4(colour.X, colour.Y, colour.Z, 1f));
    }

    private bool InFrustum(Vector3 min, Vector3 max)
    {
        foreach (Plane plane in _planes)
        {
            // The corner farthest against the plane's normal: if even it is outside, all are.
            var nearest = new Vector3(
                plane.Normal.X >= 0f ? min.X : max.X,
                plane.Normal.Y >= 0f ? min.Y : max.Y,
                plane.Normal.Z >= 0f ? min.Z : max.Z);
            if (plane.Normal.Dot(nearest) - plane.D > 0f)
            {
                return false;
            }
        }
        return true;
    }

    private static bool CornerWithin(Vector3 origin, Vector3 eye)
    {
        for (int c = 0; c < 8; c++)
        {
            var corner = origin + new Vector3((c & 1) * SnowBlock, ((c >> 1) & 1) * SnowBlock, ((c >> 2) & 1) * SnowBlock);
            if (corner.DistanceSquaredTo(eye) <= SnowReach * SnowReach)
            {
                return true;
            }
        }
        return false;
    }

    private MeshInstance3D Block(int index)
    {
        MeshInstance3D? block = _blocks[index];
        if (block is null)
        {
            block = new MeshInstance3D
            {
                Name = $"ClassicSnow{index}",
                Mesh = _snowMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = Engine_ClassicParticles.Layer,
                // The vertex shader places every flake from its index and the block's origin.
                ExtraCullMargin = 16384f,
            };
            AddChild(block);
            _blocks[index] = block;
        }
        return block;
    }

    private void HideSnow(int from)
    {
        for (int i = from; i < MaxBlocks; i++)
        {
            if (_blocks[i] is { Visible: true } block)
            {
                block.Visible = false;
            }
        }
        if (from == 0)
        {
            SnowBlocks = 0;
        }
    }

    private bool EnsureSnow()
    {
        if (_snowMaterial is not null)
        {
            return true;
        }
        if (_snowTried)
        {
            return false;
        }
        _snowTried = true;

        const string defines =
            "#define XR_ALPHA_OUT(a) 0.0\n" +
            "#define XR_PARAM_GEN_ZW POINT_COORD\n" +
            "uniform vec2 xr_point_range = vec2(1.0, 64.0);\n" +
            "#define XR_POINT_SIZE(s) clamp((s), xr_point_range.x, xr_point_range.y)";
        Shader? shader = Engine_ClassicShaders.Compose(SnowVertex, SnowPixel,
            "unshaded, cull_disabled, depth_draw_never, blend_premul_alpha", 0, defines);
        // Before the index input, the named constants and the point size the flakes collapse.
        if (shader is null || !shader.Code.Contains("instance uniform vec4 vs_g_GridOrigin", StringComparison.Ordinal) ||
            !shader.Code.Contains("POINT_SIZE", StringComparison.Ordinal))
        {
            GD.Print("[classic] the snow shaders predate the point draw; snow stays off until the shaders are compiled again");
            return false;
        }
        var material = new ShaderMaterial { Shader = shader, RenderPriority = 10 };
        Texture2D? flakes = Engine_TexLibrary.Get(SnowTexture, TexSemantic.Unknown,
            Engine_BnkMeshLoader.ReadBytes, out _);
        if (flakes is null)
        {
            GD.Print($"[classic] {SnowTexture} did not load; no snow");
            return false;
        }
        material.SetShaderParameter(PsSnowTextureSamplerName, flakes);
        material.SetShaderParameter(VsGridSizeName, new Vector4(SnowBlock, SnowBlock, SnowBlock, 1f));
        material.SetShaderParameter(VsClipShapeName, _clipShape);
        material.SetShaderParameter(VsPushVectorName, _push);
        material.SetShaderParameter(VsPushVectorOverDistName, _pushOverDistance);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[SnowPoints];
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        arrays.Dispose();
        mesh.SurfaceSetMaterial(0, material);
        _snowMesh = mesh;
        _snowMaterial = material;
        return true;
    }

    /// <summary>
    /// The snow's spell pushes as the guest's weather primitive holds them (header [8..79]): 8
    /// slots of (start, clock + t1), (end, clock + t2) for g_PushVector and 1 / d for
    /// g_PushVectorOverDist. Written to the snow material when they move.
    /// </summary>
    private void StatePushes(uint got)
    {
        if (got < HostBridge.ClassicWeatherHeader)
        {
            return;
        }
        bool changed = false;
        for (int i = 0; i < _push.Length; i++)
        {
            var v = new Vector4(_weather[8 + i * 4], _weather[9 + i * 4], _weather[10 + i * 4], _weather[11 + i * 4]);
            changed |= v != _push[i];
            _push[i] = v;
        }
        for (int i = 0; i < _pushOverDistance.Length; i++)
        {
            var v = new Vector4(_weather[72 + i * 4], _weather[73 + i * 4], _weather[74 + i * 4], _weather[75 + i * 4]);
            changed |= v != _pushOverDistance[i];
            _pushOverDistance[i] = v;
        }
        if (changed && _snowMaterial is not null)
        {
            _snowMaterial.SetShaderParameter(VsPushVectorName, _push);
            _snowMaterial.SetShaderParameter(VsPushVectorOverDistName, _pushOverDistance);
        }
    }

    private static float Sat(float value) => Math.Clamp(value, 0f, 1f);

    private static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

    /// <summary>A Godot plane (outward normal, n.p = d) in the guest's axes; the map is orthogonal.</summary>
    private static Plane ToGuest(Plane godot) => new(ToGuest(godot.Normal), godot.D);

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName VsCameraPosName = "vs_g_CameraPos";
    private static readonly StringName VsCameraDirName = "vs_g_CameraDir";
    private static readonly StringName PsRainDiffColourName = "ps_g_RainDiffColour";
    private static readonly StringName PsRainSpecColourName = "ps_g_RainSpecColour";
    private static readonly StringName PsRainLightName = "ps_g_RainLight";
    private static readonly StringName PsRainTextureSamplerName = "ps_g_RainTextureSampler";
    private static readonly StringName VsClipShapeName = "vs_g_ClipShape";
    private static readonly StringName VsPushVectorName = "vs_g_PushVector";
    private static readonly StringName VsPushVectorOverDistName = "vs_g_PushVectorOverDist";
    private static readonly StringName VsTimeName = "vs_g_Time";
    private static readonly StringName VsCamPosName = "vs_g_CamPos";
    private static readonly StringName VsSnowParams1Name = "vs_g_SnowParams1";
    private static readonly StringName VsSnowParams2Name = "vs_g_SnowParams2";
    private static readonly StringName XrPointRangeName = "xr_point_range";
    private static readonly StringName PsSnowColourName = "ps_g_SnowColour";
    private static readonly StringName PsSnowTextureSamplerName = "ps_g_SnowTextureSampler";
    private static readonly StringName VsGridSizeName = "vs_g_GridSize";
}
