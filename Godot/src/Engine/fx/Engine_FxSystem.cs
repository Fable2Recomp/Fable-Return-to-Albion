using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

public sealed class FxPlacement
{
    public float[] Pos = new float[3];
    public float Yaw;
    public float Scale = 1f;
    public bool HasRot;
    public float[] Rot = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
    public float[] Socket = new float[3];
    public string EffectName = string.Empty;
    public uint EffectHash;
    public bool Resolved;

    public float MaxVisibilityDistance;
}

public sealed class Engine_FxSystem
{
    public const int TrailHistory = 48;
    public const float RateToPerSec = 30f;
    public const float MinLife = 0.02f;
    public const float Exposure = 10f / 3f;
    public const float Gravity = -9.8f;
    private const float Fps = 30f;
    private const float Dt = 1f / 30f;
    private const float FrameDt = 1f / 30f;
    private const float TwoPi = 6.28318530717959f;

    private sealed class ParticleState
    {
        public float[] Pos = new float[3];

        /// <summary>
        /// Where the particle was at the start of the last simulation step, and whether that is
        /// known (false for a particle born in that step). The billboard shader draws between the
        /// two by the effect's sub-step fraction, so particles move at the display's rate while the
        /// simulation keeps the authored 30 frames a second.
        /// </summary>
        public float[] PrevPos = new float[3];
        public bool HasPrev;
        public float[] Vel = new float[3];
        public float Age;
        public float KeyAge;
        public float Life;

        /// <summary>The instance intensity at this particle's birth (guest particle+0x48).</summary>
        public float Intensity = 1f;
        public int Seg = 1;
        public int LoopIter;
        public float Rot;
        public float Spin;
        public float SpinNext;
        public float Seed;
        public float FlickX = 1f;
        public float FlickY = 1f;
        public float[] TexFrame = new float[4];
        public float[] Axis = new float[3];
        public float[][] Hist = new float[TrailHistory][];
        public int HistCount;
    }

    private sealed class VisualRuntime
    {
        public string Texture = string.Empty;
        public int Cols = 1;
        public int Rows = 1;
        public float FrameSpeed;
        public bool RandomFrame;
        public string BlendMode = "mix";
        public bool SatMask;
        public float SoftDepth;
        public float[] Colour = { 1f, 1f, 1f };
        public float Alpha = 1f;
        public float SelfIllum = 1f;
        public float HalfX = 0.5f;
        public float HalfY = 0.5f;
        public float GlobalScaleX = 1f;
        public float GlobalScaleY = 1f;
        public float Delay;
        public int DelayFrames;
        public float InvSpan;
        public int TexSlot;

        public bool HasRotation;
        public float RotSpeed;
        public float RotSpread;
        public bool RotInitialRandom;

        public bool HasFlicker;
        public int FlickerFrames;
        public float FlickerBaseX = 1f;
        public float FlickerBaseY = 1f;
        public float FlickerSpreadX;
        public float FlickerSpreadY;
        public bool FlickerShared = true;

        public float TrailWidth = 1f;
        public int TrailLen = 1;

        public bool Aligned;
        public float[] AxisR = { 1f, 0f, 0f };
        public float[] AxisU = { 0f, 1f, 0f };

        public bool IsMesh;
        public string MeshPath = string.Empty;

        /// <summary>
        /// The key this visual's instances are bucketed under in the emit map, folded once so the
        /// displacement suffix is not concatenated per particle.
        /// </summary>
        public string? MeshKey;
        public float MeshScale = 1f;
        public bool HasRandScale;
        public int RsFrames;
        public float RsMin = 1f;
        public float RsRange;
        public float OrSpeed;
        public float OrSpread;
        public bool OrRandAngle;
        public float[] OrAxis = { 1f, 0f, 0f };
        public bool OrRandAxis = true;
    }

    private sealed class ChainNode
    {
        public FxEmitter? Def;
        public float[] Quat = { 0f, 0f, 0f, 1f };
        public float[] Pos = new float[3];
        public float Angle;
        public float T = -1f;
        public float Dir = 1f;
        public float[] LocalRot = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };

        public ChainNode Clone()
        {
            var c = new ChainNode { Def = Def, Angle = Angle, T = T, Dir = Dir };
            Array.Copy(Quat, c.Quat, 4);
            Array.Copy(Pos, c.Pos, 3);
            Array.Copy(LocalRot, c.LocalRot, 9);
            return c;
        }
    }

    private sealed class AttractorRuntime
    {
        public FxEmitter? Def;
        public List<ChainNode> Chain = new();
        public float[] WorldPos = new float[3];
        public float Age;
        public bool Global;
        public int ChainId = -1;
    }

    private sealed class LightRuntime
    {
        public FxEmitter? Def;
        public List<ChainNode> Chain = new();
        public float[] WorldPos = new float[3];
        public float Age;
        public float Scale = 1f;

        /// <summary>
        /// The flicker walk's state in [-1, 1], the render light's +0x1BC. Zero at birth
        /// (`82b1c074 stfs f0, 0x1BC`), so a light opens at its authored brightness.
        /// </summary>
        public float Phase;

        /// <summary>The light as it stood at the start of the last step, for the draw between steps.</summary>
        public float[] PrevWorldPos = new float[3];
        public float PrevAge;
        public float PrevPhase;
        public bool HasPrev;
    }

    private sealed class EmitterRuntime
    {
        public FxEmitter? Def;
        public List<VisualRuntime> Visuals { get; } = new();
        public List<ChainNode> Chain = new();
        public int ChainId = -1;

        public float Accum;
        public float Age;
        public bool FirstFrame = true;
        public float[] PrevPos = new float[3];
        public float[] PosW = new float[3];
        public float[] RotW = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        public float Scale = 1f;

        public float[] PlacePos = new float[3];
        public float[] PlaceRot = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };

        public bool Trail;
        public int Mode = 1;
        public bool Ribbon;
        public bool Frozen;
        public int[] SlotFrames = new int[4];
        public bool[] SlotRandom = new bool[4];
        public List<VisualRuntime> TrailVisuals { get; } = new();
        public float TrailLife = -1f;
        public float LifeFrames = -1f;
        public float LoopStart;
        public float LoopEnd;
        public int LoopCount;
        public bool PositionalInteractable;
        public List<int> Attractors = new();

        public float[] WindDisp = new float[3];
        public float[] VelMul = { 1f, 1f, 1f };
        public float[] Fade = { 0.8f, 1f, 25f, 32f };
        public int ZOrder;

        public bool Beam;
        public List<float[]> BeamBase = new();
        public List<float[]> BeamPts = new();
        public List<float[]> BeamWander = new();
        public float BeamSegLen;
        public float BeamJerkTimer;

        public List<ParticleState> Particles { get; } = new();
    }

    private sealed class InstanceState
    {
        public FxPlacement Place = new();
        public float[] PlacePos = new float[3];
        public float[] PlaceRot = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        public float Scale = 1f;
        public float[] Lit = { 1f, 1f, 1f };
        public float Time;
        public int Frame;
        public int Step = 1;
        public float DtSec = 1f / 30f;
        public float SharedRand = -1f;
        public List<EmitterRuntime> Emitters { get; } = new();
        public List<AttractorRuntime> Attractors { get; } = new();
        public List<LightRuntime> Lights { get; } = new();
    }

    private readonly List<InstanceState> _instances = new();
    private readonly List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> _scratch = new();
    private uint _rng = 0x1234567u;
    private bool _loop = true;
    private float _intensity = 1f;

    /// <summary>
    /// On, because the guest's particle vertex shaders fade every particle by its camera
    /// distance, unconditionally: 0038 lines 1358–1378 compute
    /// `saturate((d − FadeIn.x)·FadeIn.z) × saturate((FadeOut.x − d)·FadeOut.y)` with
    /// d = |g_CameraPosition − vertex| and fold it into the vertex alpha (0040/0042 the same).
    /// The constants are the emitter's four fade distances (resource +204..+216 → bindings
    /// 320/321), default 0.8, 1, 25, 32 — so a torch flame is gone 32 m from the eye.
    /// </summary>
    private bool _distanceFade = true;
    private Transform3D? _previousGlobal;

    /// <summary>
    /// World gravity in the simulation's own (guest-layout, node-local) space, per unit of
    /// <see cref="Gravity"/>. The guest adds `unk_8349E4D0 × p7` in WORLD space and carries the
    /// sum into storage space through the inverted root matrix (`sub_8322D988`: `sub_8322EEC8`
    /// then `sub_8227CB80`, the rows applied at 0x8322dc5c..0x8322dc74 — identity for a
    /// world-space emitter). Our storage space is the node's, so down is the node's inverse
    /// basis applied to world down.
    /// </summary>
    private readonly float[] _down = { 0f, 0f, 1f };
    private bool _detachedParticles;

    public int ResolvedCount { get; private set; }
    public bool IsEmpty => _instances.Count == 0;
    public void SetLoop(bool loop) => _loop = loop;

    /// <summary>
    /// The instance intensity new particles are born with — the guest's <c>handle+52</c>,
    /// stored per element at +408 by <c>sub_832323E0</c> and read by the spawn routines
    /// (<c>sub_82A03D58</c> at 0x82a04090) into particle+0x48. Particles already born keep
    /// theirs, which is what makes a change travel rather than snap.
    /// </summary>
    public void SetIntensity(float intensity) =>
        _intensity = float.IsFinite(intensity) ? MathF.Max(intensity, 0f) : 1f;

    private float _sizeScale = 1f;

    /// <summary>The live size scale — the guest's element+412: folded into the keyframe global
    /// scales (binding 338) at draw time, and multiplied into the spawn-shape offset and the
    /// per-update acceleration by the simulation itself.</summary>
    public void SetSizeScale(float scale) =>
        _sizeScale = float.IsFinite(scale) && scale > 0f ? scale : 1f;
    public void SetDistanceFade(bool on) => _distanceFade = on;
    public int TotalSpawned { get; private set; }

    private static void QuatToMat(float[] q, float[] m)
    {
        float x = q[0], y = q[1], z = q[2], w = q[3];
        float xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
        m[0] = 1 - (2 * (yy + zz)); m[1] = 2 * (xy - wz); m[2] = 2 * (xz + wy);
        m[3] = 2 * (xy + wz); m[4] = 1 - (2 * (xx + zz)); m[5] = 2 * (yz - wx);
        m[6] = 2 * (xz - wy); m[7] = 2 * (yz + wx); m[8] = 1 - (2 * (xx + yy));
    }

    [ThreadStatic] private static float[]? _matMulScratch;

    private static void MatMul(float[] a, float[] b, float[] o)
    {
        float[] t = _matMulScratch ??= new float[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                t[(r * 3) + c] = (a[(r * 3) + 0] * b[c]) + (a[(r * 3) + 1] * b[3 + c]) + (a[(r * 3) + 2] * b[6 + c]);
            }
        }

        Array.Copy(t, o, 9);
    }

    private static void MatVec(float[] m, float[] v, float[] o)
    {
        float x = v[0], y = v[1], z = v[2];
        o[0] = (m[0] * x) + (m[1] * y) + (m[2] * z);
        o[1] = (m[3] * x) + (m[4] * y) + (m[5] * z);
        o[2] = (m[6] * x) + (m[7] * y) + (m[8] * z);
    }

    private static void AxisAngleMat(float[] axis, float ang, float[] m)
    {
        float c = MathF.Cos(ang), s = MathF.Sin(ang), ic = 1f - c;
        float ax = axis[0], ay = axis[1], az = axis[2];
        m[0] = c + (ax * ax * ic); m[1] = (ax * ay * ic) - (az * s); m[2] = (ax * az * ic) + (ay * s);
        m[3] = (ay * ax * ic) + (az * s); m[4] = c + (ay * ay * ic); m[5] = (ay * az * ic) - (ax * s);
        m[6] = (az * ax * ic) - (ay * s); m[7] = (az * ay * ic) + (ax * s); m[8] = c + (az * az * ic);
    }

    private static float VLen(float[] v) => MathF.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));

    private static bool VNorm(float[] v, float eps)
    {
        float l = VLen(v);
        if (l <= eps) return false;
        v[0] /= l; v[1] /= l; v[2] /= l;
        return true;
    }

    [ThreadStatic] private static float[]? _splineLinA;
    [ThreadStatic] private static float[]? _splineLinB;

    private static void SplineEval(float[][] p, bool looped, bool straight, float t, float[] o)
    {
        int n = p.Length;
        if (n <= 0) { o[0] = o[1] = o[2] = 0; return; }
        if (n == 1 || t <= 0f) { Array.Copy(p[0], o, 3); return; }
        if (t >= 1f) { Array.Copy(p[n - 1], o, 3); return; }
        bool loop = looped && n > 2;
        int segs = loop ? n : n - 1;
        float u = segs * t;
        int i = (int)u;
        float s = u - i;
        float[] P0, P1, P2, P3;

        // Two scratch endpoints rather than an array per call: runs per spline particle per frame
        // and needs only the two extrapolated control points at once.
        float[] linA = _splineLinA ??= new float[3];
        float[] linB = _splineLinB ??= new float[3];
        static float[] Lin(float[] into, float[] a, float[] b, float ka, float kb)
        {
            into[0] = (a[0] * ka) + (b[0] * kb);
            into[1] = (a[1] * ka) + (b[1] * kb);
            into[2] = (a[2] * ka) + (b[2] * kb);
            return into;
        }
        if (loop)
        {
            if (i >= n) i = n - 1;
            if (i == 0) { P1 = p[0]; P0 = p[n - 1]; P2 = p[1]; P3 = p[2 % n]; }
            else if (i == n - 1) { P1 = p[n - 1]; P0 = p[n - 2]; P2 = p[0]; P3 = p[1]; }
            else if (i == n - 2) { P1 = p[n - 2]; P0 = p[n - 3]; P2 = p[n - 1]; P3 = p[0]; }
            else { P1 = p[i]; P0 = p[i - 1]; P2 = p[i + 1]; P3 = p[i + 2]; }
        }
        else
        {
            if (i >= n - 1) i = n - 2;
            if (i == 0) { P1 = p[0]; P2 = p[1]; P0 = Lin(linA, p[0], p[1], 2, -1); P3 = n == 2 ? Lin(linB, p[1], p[0], 2, -1) : p[2]; }
            else if (i == n - 2) { P1 = p[i]; P2 = p[i + 1]; P0 = p[i - 1]; P3 = Lin(linB, p[i + 1], p[i], 2, -1); }
            else { P1 = p[i]; P2 = p[i + 1]; P0 = p[i - 1]; P3 = p[i + 2]; }
        }

        if (straight)
        {
            for (int k = 0; k < 3; k++) o[k] = P1[k] + ((P2[k] - P1[k]) * s);
            return;
        }

        for (int k = 0; k < 3; k++)
        {
            float c1 = 0.5f * (P2[k] - P0[k]);
            float c2 = P0[k] - (2.5f * P1[k]) + (2f * P2[k]) - (0.5f * P3[k]);
            float c3 = (-0.5f * P0[k]) + (1.5f * P1[k]) - (1.5f * P2[k]) + (0.5f * P3[k]);
            o[k] = P1[k] + (c1 * s) + (c2 * s * s) + (c3 * s * s * s);
        }
    }

    private float Frand()
    {
        _rng = (_rng * 1664525u) + 1013904223u;
        return ((_rng >> 8) & 0xFFFFFF) / (float)0x1000000;
    }

    private float Srand2() => (Frand() * 2f) - 1f;

    private static Vector3 ToGodot(float[] game) => new(game[0], game[2], game[1]);

    /// <summary>
    /// Fills a spawned instance's `Lit` colour from the scene. The guest's particle vertex
    /// shader lights a particle as `lerp(g_GlobalAmbient + point lights, keyframeColour,
    /// selfIllum)` (blob 0038) — left unbound, `Lit` stays (1,1,1) and every particle with
    /// selfIllum &lt; 1 lerps toward WHITE instead of toward scene light, which reads as smoke,
    /// dust and spell debris all glowing. Bound by <see cref="BindAmbientToScene"/>.
    /// </summary>
    public static Action<float[], float[]>? AmbientProvider;

    /// <summary>The guest's full lit term: theme ambient plus nearby placed lights.</summary>
    public static void BindAmbientToScene()
    {
        // Game (x, y, z) to Godot world (-x, z, y) — the region root's mirror composed with
        // the axis swap, the same mapping every placement takes.
        AmbientProvider = static (pos, lit) =>
            SceneLightAt(new Godot.Vector3(-pos[0], pos[2], pos[1]), lit);
    }

    /// <summary>
    /// The scene light at a Godot world position — theme ambient plus every registered light
    /// in range, the `g_GlobalAmbient + point lights` the guest's particle vertex shader
    /// lerps from. False, with <paramref name="lit"/> untouched, while no sky ambient is up.
    /// </summary>
    public static bool SceneLightAt(Godot.Vector3 world, float[] lit)
    {
        Refii.Lighting.LightingFrame? frame =
            Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting;
        if (frame is null || !frame.HasAmbient)
        {
            return false;
        }

        lit[0] = frame.AmbientColour[0] * frame.AmbientEnergy;
        lit[1] = frame.AmbientColour[1] * frame.AmbientEnergy;
        lit[2] = frame.AmbientColour[2] * frame.AmbientEnergy;
        Refii.Lighting.Engine_LevelLights.AddLightsAt(world, lit);
        return true;
    }

    public static bool DebugEmit;

    /// <summary>
    /// What this system holds, per effect rather than per process; the global counters beside
    /// DebugEmit cannot separate the live feed from the level effects.
    /// </summary>
    public (int Emitters, int Visuals, int TrailVisuals, int Particles) Census()
    {
        int emitters = 0, visuals = 0, trails = 0, particles = 0;
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                ++emitters;
                visuals += rt.Visuals.Count;
                trails += rt.TrailVisuals.Count;
                particles += rt.Particles.Count;
            }
        }

        return (emitters, visuals, trails, particles);
    }

    /// <summary>Whether any visual is ONE/ONE — the blend the guest routes to its additive
    /// effects surface rather than the scene.</summary>
    public bool HasAdditive
    {
        get
        {
            foreach (InstanceState inst in _instances)
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    foreach (VisualRuntime v in rt.Visuals) if (v.BlendMode == "addone") return true;
                    foreach (VisualRuntime v in rt.TrailVisuals) if (v.BlendMode == "addone") return true;
                }
            return false;
        }
    }

    public bool HasDisplacement
    {
        get
        {
            foreach (InstanceState inst in _instances)
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    foreach (VisualRuntime v in rt.Visuals) if (v.BlendMode == "disp") return true;
                    foreach (VisualRuntime v in rt.TrailVisuals) if (v.BlendMode == "disp") return true;
                }
            return false;
        }
    }

    public void SetAmbient(float r, float g, float b)
    {
        foreach (InstanceState inst in _instances)
        {
            inst.Lit[0] = r; inst.Lit[1] = g; inst.Lit[2] = b;
        }
    }

    public static string BlendModeFor(int src, int dst, int op)
    {
        if (src < 0 || src > 10) src = 1;
        if (dst < 0 || dst > 10) dst = 1;
        if (op < 0 || op > 4) op = 0;
        if (op == 2) return src == 1 && dst == 5 ? "revsub" : "sub";
        if (op != 0) return "mix";
        if (src == 1 && dst == 5) return "mix";
        if (src == 1 && dst == 1) return "addone";
        if (src == 4 && dst == 1) return "add";
        if (src == 4 && dst == 5) return "mixalpha";
        if (src == 0 && dst == 5) return "darken";
        return "mix";
    }

    private void UpdateChain(List<ChainNode> chain, InstanceState inst, float[] placePos, float[] placeRot, float scale, float[] outPos, float[] outRot)
    {
        // Scratch is per system, and a system steps on one thread at a time.
        float[] R = _chainR;
        Array.Copy(placeRot, R, 9);
        float[] t = _chainT;
        t[0] = placePos[0]; t[1] = placePos[1]; t[2] = placePos[2];
        float[] lp = _chainLp;
        float[] lps = _chainLps;
        float[] w = _chainW;
        foreach (ChainNode node in chain)
        {
            FxEmitter? d = node.Def;
            lp[0] = lp[1] = lp[2] = 0f;
            if (d is not null)
            {
                switch (d.TypeId)
                {
                    case 3:
                    {
                        node.Angle += inst.Step * d.AngularRotationRate;
                        if (node.Angle >= TwoPi) node.Angle -= TwoPi;
                        if (node.Angle <= -TwoPi) node.Angle += TwoPi;
                        float[] zax = _chainZax;
                        float[] yax = _chainYax;
                        MatVec(node.LocalRot, UnitZ, zax);
                        MatVec(node.LocalRot, UnitY, yax);
                        float radius = d.Generic[25].EvalFrame(inst.Frame);
                        float[] basePt = _chainBase;
                        basePt[0] = node.Pos[0] + (yax[0] * radius);
                        basePt[1] = node.Pos[1] + (yax[1] * radius);
                        basePt[2] = node.Pos[2] + (yax[2] * radius);
                        float[] Ra = _chainRa;
                        AxisAngleMat(zax, node.Angle, Ra);
                        MatVec(Ra, basePt, lp);
                        break;
                    }

                    case 5:
                    {
                        if (node.T < 0f) node.T = d.SplineRandomStart && inst.SharedRand >= 0f ? inst.SharedRand : 0f;
                        float tt = node.T;
                        if (d.SplineRandomGenerator) tt = Frand();
                        SplineEval(d.SplinePoints, d.SplineLooped, d.SplineStraight, tt, lp);
                        node.T += inst.Step * d.SplineRate * node.Dir;
                        if (node.T < 0f || node.T > 1f)
                        {
                            if (d.SplineReverseFlow) node.Dir = -node.Dir; else node.T = 0f;
                        }

                        node.T = Math.Clamp(node.T, 0f, 1f);
                        break;
                    }

                    default:
                        lp[0] = node.Pos[0]; lp[1] = node.Pos[1]; lp[2] = node.Pos[2];
                        break;
                }
            }

            lps[0] = lp[0] * scale; lps[1] = lp[1] * scale; lps[2] = lp[2] * scale;
            MatVec(R, lps, w);
            t[0] += w[0]; t[1] += w[1]; t[2] += w[2];
            MatMul(R, node.LocalRot, R);
        }

        outPos[0] = t[0]; outPos[1] = t[1]; outPos[2] = t[2];
        Array.Copy(R, outRot, 9);
    }

    private readonly float[] _chainR = new float[9];
    private readonly float[] _chainRa = new float[9];
    private readonly float[] _chainT = new float[3];
    private readonly float[] _chainLp = new float[3];
    private readonly float[] _chainLps = new float[3];
    private readonly float[] _chainW = new float[3];
    private readonly float[] _chainZax = new float[3];
    private readonly float[] _chainYax = new float[3];
    private readonly float[] _chainBase = new float[3];
    private static readonly float[] UnitZ = { 0f, 0f, 1f };
    private static readonly float[] UnitY = { 0f, 1f, 0f };

    /// <summary>
    /// Why a particle produced no quad: which of the emission loop's silent `continue`s took it.
    /// </summary>
    public static long SkipZeroSize;

    public static long SkipZeroAlpha;

    public static long EmittedBillboards;

    /// <summary>Emitters added to the transform chain (fewer than two children).</summary>
    public static long ChainedNoVisuals;

    /// <summary>Chain links that also have something to draw.</summary>
    public static long ChainedButDrawable;

    /// <summary>Emitters that reached BuildVisuals, and those that came back with nothing.</summary>
    public static long VisualsBuilt;

    public static long BuiltButEmpty;

    private void BuildVisuals(FxSystemDef sysd, float sc, float[] rotW, List<VisualRuntime> outv)
    {
        if (sysd.Visuals.Count == 0) return;
        FxVisual first = sysd.Visuals[0];
        string blendMode = first.Displacement ? "disp" : BlendModeFor(first.SrcBlend, first.DstBlend, first.BlendOp);
        bool satMask = first.SaturationMask;
        float softDepth = first.SoftDepthContrast > 0f ? first.SoftDepthContrast : 0f;
        foreach (FxVisual vis in sysd.Visuals)
        {
            VisualRuntime? v = BuildVisual(vis, sc, rotW);
            if (v is null) continue;
            v.BlendMode = blendMode;
            v.SatMask = satMask;
            v.SoftDepth = softDepth;
            outv.Add(v);
        }
    }

    private VisualRuntime? BuildVisual(FxVisual vis, float sc, float[] rotW)
    {
        if (vis.Texture.Length == 0 && !vis.HasMesh) return null;
        var v = new VisualRuntime
        {
            Delay = vis.Delay,
            DelayFrames = vis.DelayFrames,
            InvSpan = vis.InvSpan,
            Colour = new[] { vis.ColorSq[0], vis.ColorSq[1], vis.ColorSq[2] },
            Alpha = vis.HasAlpha ? vis.Alpha : 1f,
            SelfIllum = vis.SelfIllum,
        };
        if (vis.HasMesh)
        {
            v.IsMesh = true;
            v.MeshPath = vis.MeshPath;
            v.MeshScale = (vis.HasTrailWidth ? vis.TrailWidth : 1f) * sc;
            v.HasRandScale = vis.HasRandScale;
            if (vis.HasRandScale)
            {
                v.RsFrames = vis.RsFreq > 1e-4f ? Math.Max(1, (int)((vis.RsFreq * Fps) + 0.5f)) : 0;
                v.RsMin = vis.RsMin;
                v.RsRange = vis.RsRange;
            }

            if (vis.HasOrient)
            {
                v.OrSpeed = vis.OrSpeed;
                v.OrSpread = vis.OrSpread;
                v.OrRandAngle = vis.OrRandAngle;
                v.OrAxis = new[] { vis.OrAxis[0], vis.OrAxis[1], vis.OrAxis[2] };
                v.OrRandAxis = vis.OrRandAxis;
            }

            return v;
        }

        v.Texture = vis.Texture;
        v.Cols = vis.TexCols; v.Rows = vis.TexRows;
        v.FrameSpeed = vis.FrameSpeed;
        v.RandomFrame = vis.RandomFrame;
        v.HasRotation = vis.HasRotation;
        v.RotSpeed = vis.RotSpeed;
        v.RotSpread = vis.RotSpread;
        v.RotInitialRandom = vis.RotInitialRandom;
        v.HasFlicker = vis.HasFlicker;
        if (vis.HasFlicker)
        {
            v.FlickerFrames = vis.FlickerFrames;
            v.FlickerBaseX = vis.FlickerBaseX;
            v.FlickerBaseY = vis.FlickerBaseY;
            v.FlickerSpreadX = vis.FlickerSpreadX;
            v.FlickerSpreadY = vis.FlickerSpreadY;
            v.FlickerShared = vis.FlickerShared;
        }

        v.TrailWidth = (vis.HasTrailWidth ? vis.TrailWidth : 1f) * sc;
        v.TrailLen = Math.Clamp(vis.HasTrailLen ? vis.TrailLen : 1, 1, 16);
        if (vis.HasAlignQuat)
        {
            var qm = new float[9];
            QuatToMat(vis.AlignQuat, qm);
            var rg = new float[3]; var ug = new float[3]; var rg2 = new float[3]; var ug2 = new float[3];
            MatVec(qm, new[] { 1f, 0f, 0f }, rg); MatVec(qm, new[] { 0f, 1f, 0f }, ug);
            MatVec(rotW, rg, rg2); MatVec(rotW, ug, ug2);
            v.AxisR = rg2; v.AxisU = ug2;
            v.Aligned = true;
        }

        float hx = vis.HasSize ? vis.SizeX : 1f;
        float hy = vis.HasSize ? vis.SizeY : 1f;
        if (hx <= 0f) hx = 1f;
        if (hy <= 0f) hy = 1f;
        v.HalfX = hx * 0.5f * sc;
        v.HalfY = hy * 0.5f * sc;
        if (vis.HasGlobalScale) { v.GlobalScaleX = vis.GlobalScale[0]; v.GlobalScaleY = vis.GlobalScale[1]; }
        return v;
    }

    public void Build(FxBank bank, IEnumerable<FxPlacement> placements)
    {
        _instances.Clear();
        ResolvedCount = 0;
        _detachedParticles = false;
        _previousGlobal = null;

        foreach (FxPlacement p in placements)
        {
            if (p.EffectHash == 0 && p.EffectName.Length != 0) p.EffectHash = FxBank.Fnv1Lower(p.EffectName);
            var inst = new InstanceState { Place = p };
            float sc = p.Scale > 0.0001f ? p.Scale : 1f;
            float cy = MathF.Cos(p.Yaw), sy = MathF.Sin(p.Yaw);
            var placeRot = new float[9];
            if (p.HasRot) Array.Copy(p.Rot, placeRot, 9);
            else
            {
                placeRot[0] = cy; placeRot[1] = -sy; placeRot[2] = 0;
                placeRot[3] = sy; placeRot[4] = cy; placeRot[5] = 0;
                placeRot[6] = 0; placeRot[7] = 0; placeRot[8] = 1;
            }

            var rsk = new float[3];
            MatVec(placeRot, p.Socket, rsk);
            var placePos = new[] { p.Pos[0] + (sc * rsk[0]), p.Pos[1] + (sc * rsk[1]), p.Pos[2] + (sc * rsk[2]) };
            Array.Copy(placePos, inst.PlacePos, 3);
            inst.Lit[0] = inst.Lit[1] = inst.Lit[2] = 1f;
            AmbientProvider?.Invoke(placePos, inst.Lit);
            Array.Copy(placeRot, inst.PlaceRot, 9);
            inst.Scale = sc;

            FxEffect? eff = bank.FindByHash(p.EffectHash);
            if (eff is null) continue;
            // The field IS the guest's group index into its two rate columns, copied verbatim
            // with no clamp: `desc+32` to `def+60` to `inst+12`. Group 0 updates at 15 Hz with
            // dt 1/15 and advances 2 authored frames; group 1 at 30 Hz with dt 1/30 and 1 frame.
            // Anything above 1 reads PAST the guest's own arrays, so it is folded to group 1
            // here rather than reproduced.
            inst.Step = eff.UpdateFrequency >= 1 ? 1 : 2;
            inst.DtSec = eff.UpdateFrequency >= 1 ? 1f / 30f : 1f / 15f;

            var chain = new List<ChainNode>();
            int curChain = -1;
            foreach (FxEffectPart part in eff.Parts)
            {
                if (part.Emitter < 0 || part.Emitter >= bank.Emitters.Count) continue;
                FxEmitter em = bank.Emitters[part.Emitter];
                if (part.Vector != curChain) { chain.Clear(); curChain = part.Vector; }

                var node = new ChainNode { Def = em };
                Array.Copy(em.Quat, node.Quat, 4);
                Array.Copy(em.Pos, node.Pos, 3);
                QuatToMat(node.Quat, node.LocalRot);

                if (em.TypeId == 4)
                {
                    var at = new AttractorRuntime { Def = em, Global = em.GlobalAttractor, ChainId = part.Vector };
                    foreach (ChainNode c in chain) at.Chain.Add(c.Clone());
                    at.Chain.Add(node.Clone());
                    inst.Attractors.Add(at);
                    chain.Add(node);
                    continue;
                }

                if (em.TypeId == 7 || em.TypeId == 8)
                {
                    var lt = new LightRuntime { Def = em, Scale = sc };
                    foreach (ChainNode c in chain) lt.Chain.Add(c.Clone());
                    lt.Chain.Add(node.Clone());
                    var rot0 = new float[9];
                    UpdateChain(lt.Chain, inst, placePos, placeRot, sc, lt.WorldPos, rot0);
                    foreach (ChainNode c in lt.Chain) { c.Angle = 0f; c.T = -1f; }
                    inst.Lights.Add(lt);
                    chain.Add(node);
                    continue;
                }

                if (!em.HasMotion) { chain.Add(node); continue; }

                var rt = new EmitterRuntime { Def = em, Beam = em.TypeId == 9, ChainId = part.Vector, Scale = sc };
                foreach (ChainNode c in chain) rt.Chain.Add(c.Clone());
                rt.Chain.Add(node.Clone());
                Array.Copy(placePos, rt.PlacePos, 3);
                Array.Copy(placeRot, rt.PlaceRot, 9);
                rt.Accum = MathF.Max(0f, em.InitialNum);
                rt.PositionalInteractable = em.PositionalInteractable;
                UpdateChain(rt.Chain, inst, placePos, placeRot, sc, rt.PosW, rt.RotW);
                foreach (ChainNode c in rt.Chain) { c.Angle = 0f; c.T = -1f; }
                Array.Copy(rt.PosW, rt.PrevPos, 3);

                if (part.System >= 0 && part.System < bank.Systems.Count && bank.Systems[part.System].Mode is >= 1 and <= 4)
                {
                    FxSystemDef sysd = bank.Systems[part.System];
                    rt.Mode = (int)sysd.Mode;
                    rt.Ribbon = sysd.Mode is 2 or 3;
                    rt.Frozen = false;
                    rt.LoopStart = sysd.LoopStartFrames;
                    rt.LoopEnd = sysd.LoopEndFrames;
                    rt.LoopCount = sysd.LoopCount;
                    // Chaining and drawing are independent: `Engine_FxBank` fills `ChildCount`
                    // from `xs.Children` and `Visuals` from `xs.Segments`, and nothing ties them.
                    // A system with under two children still chains (the transform hierarchy),
                    // and also builds visuals when it has any (e.g. a spell charge: one emitter
                    // with segments and no child hierarchy).
                    bool chained = sysd.ChildCount < 2;
                    if (chained)
                    {
                        ++ChainedNoVisuals;
                        chain.Add(node);
                        if (sysd.Visuals.Count == 0)
                        {
                            continue;
                        }

                        ++ChainedButDrawable;
                    }

                    ++VisualsBuilt;
                    rt.LifeFrames = sysd.LastStartFrames;
                    BuildVisuals(sysd, sc, rt.RotW, rt.Visuals);
                    if (rt.Visuals.Count == 0)
                    {
                        ++BuiltButEmpty;
                    }
                    var slots = new List<string>();
                    foreach (VisualRuntime v in rt.Visuals)
                    {
                        if (v.IsMesh || v.Texture.Length == 0) { v.TexSlot = 0; continue; }
                        int si = slots.IndexOf(v.Texture);
                        if (si < 0)
                        {
                            si = slots.Count;
                            if (si < 4)
                            {
                                slots.Add(v.Texture);
                                rt.SlotFrames[si] = Math.Max(1, v.Cols) * Math.Max(1, v.Rows);
                                rt.SlotRandom[si] = v.RandomFrame;
                            }
                        }

                        v.TexSlot = Math.Min(si, 3);
                    }
                }

                if (em.ReleaseTrail && part.TrailSystem >= 0 && part.TrailSystem < bank.Systems.Count)
                {
                    FxSystemDef tsys = bank.Systems[part.TrailSystem];
                    if (tsys.Mode == 2 && tsys.ChildCount >= 2)
                    {
                        BuildVisuals(tsys, sc, rt.RotW, rt.TrailVisuals);

                        rt.Trail = rt.TrailVisuals.Count != 0;
                        rt.TrailLife = tsys.LastStartFrames;
                    }
                }

                if (rt.Visuals.Count == 0) { chain.Add(node); continue; }
                Array.Copy(em.Fades, rt.Fade, 4);
                if (p.MaxVisibilityDistance > 0f)
                {
                    float band = rt.Fade[3] - rt.Fade[2];
                    rt.Fade[3] = p.MaxVisibilityDistance;
                    rt.Fade[2] -= band;
                    rt.Fade[1] = MathF.Min(rt.Fade[1], rt.Fade[2]);
                    rt.Fade[0] = MathF.Min(rt.Fade[0], rt.Fade[1]);
                }

                rt.ZOrder = eff.ZSortEmitters ? part.Vector + 1 : 0;
                if (!em.AttachToRoot) _detachedParticles = true;
                if (rt.Beam) BeamInit(rt);
                inst.Emitters.Add(rt);
                chain.Add(node);
            }

            for (int ei = 0; ei < inst.Emitters.Count; ei++)
            {
                EmitterRuntime rt = inst.Emitters[ei];
                for (int ai = 0; ai < inst.Attractors.Count; ai++)
                {
                    AttractorRuntime at = inst.Attractors[ai];
                    if (at.Global || rt.PositionalInteractable) rt.Attractors.Add(ai);
                }
            }

            if (inst.Emitters.Count != 0 || inst.Lights.Count != 0)
            {
                inst.Place.Resolved = true;
                p.Resolved = true;
                ResolvedCount++;
                _instances.Add(inst);
            }
        }
    }

    private readonly float[] _dirScratch = new float[3];
    private readonly float[] _perpScratch = new float[3];

    /// <summary>
    /// The particle-to-attractor vector, reused: this is the innermost simulation loop and must
    /// not allocate.
    /// </summary>
    private readonly float[] _attractDelta = new float[3];

    private void ComputeDir(EmitterRuntime rt, float[] o)
    {
        FxEmitter d = rt.Def!;
        int f = (int)rt.Age;
        float phi = Frand() * TwoPi;
        float[] dir = _dirScratch;
        dir[0] = d.Generic[9].EvalFrame(f) * MathF.Cos(phi);
        dir[1] = d.Generic[10].EvalFrame(f) * MathF.Sin(phi);
        dir[2] = d.Generic[11].EvalFrame(f);
        float l2 = (dir[0] * dir[0]) + (dir[1] * dir[1]) + (dir[2] * dir[2]);
        if (l2 >= 1.19209290e-7f) VNorm(dir, 1e-4f);
        else { dir[0] = 0; dir[1] = 0; dir[2] = 1; }
        float r = Frand();
        if (dir[2] <= 0.9995f)
        {
            float z = Math.Clamp(dir[2], -1f, 1f);
            float theta = MathF.Acos(z) * r;
            float[] perp = _perpScratch;
            perp[0] = -dir[0] * z;
            perp[1] = -dir[1] * z;
            perp[2] = 1f - (z * z);
            VNorm(perp, 1e-4f);
            float sn = MathF.Sin(theta), cs = MathF.Cos(theta);
            for (int k = 0; k < 3; k++) dir[k] = (dir[k] * cs) + (perp[k] * sn);
        }
        else
        {
            dir[0] = ((0f - dir[0]) * r) + dir[0];
            dir[1] = ((0f - dir[1]) * r) + dir[1];
            dir[2] = ((1f - dir[2]) * r) + dir[2];
            VNorm(dir, 1e-4f);
        }

        MatVec(rt.RotW, dir, o);
    }

    private void StepParticle(InstanceState inst, EmitterRuntime rt, ParticleState pt, bool hasAttractors, bool hasP14, bool hasVec0)
    {
        float step = inst.Step;
        float dts = inst.DtSec;
        pt.Vel[0] += rt.WindDisp[0]; pt.Vel[1] += rt.WindDisp[1]; pt.Vel[2] += rt.WindDisp[2];
        if (hasAttractors)
        {
            foreach (int ai in rt.Attractors)
            {
                AttractorRuntime at = inst.Attractors[ai];
                FxEmitter ad = at.Def!;
                int af = (int)at.Age;
                float rad = ad.Generic[26].EvalFrame(af);
                float[] dv = _attractDelta;
                dv[0] = pt.Pos[0] - at.WorldPos[0];
                dv[1] = pt.Pos[1] - at.WorldPos[1];
                dv[2] = pt.Pos[2] - at.WorldPos[2];
                float d2 = (dv[0] * dv[0]) + (dv[1] * dv[1]) + (dv[2] * dv[2]);
                if (rad > 0f && d2 <= rad * rad)
                {
                    float dist = MathF.Sqrt(d2);
                    float s = ad.Generic[27].EvalFrame(af) * (1f - (dist / rad));
                    if (dist < 1e-8f) { dv[0] = Srand2(); dv[1] = Srand2(); dv[2] = Srand2(); }
                    if (VNorm(dv, 1e-4f))
                    {
                        float k = -s * dts * dts;
                        pt.Vel[0] += dv[0] * k; pt.Vel[1] += dv[1] * k; pt.Vel[2] += dv[2] * k;
                    }
                }
            }
        }

        pt.Vel[0] *= rt.VelMul[0]; pt.Vel[1] *= rt.VelMul[1]; pt.Vel[2] *= rt.VelMul[2];
        float vpx = pt.Vel[0], vpy = pt.Vel[1], vpz = pt.Vel[2];
        if (hasP14)
        {
            FxEmitter d = rt.Def!;
            int pf = (int)pt.Age;
            float k = 1f - ((1f - d.Generic[14].EvalFrame(pf)) * (1f / step));
            if (hasVec0) k *= k;
            (float v1x, float v1y, float v1z) = d.Vec1.EvalFrame(pf);
            float invStep = 1f / step;
            vpx = pt.Vel[0] * invStep; vpy = pt.Vel[1] * invStep; vpz = pt.Vel[2] * invStep;
            pt.Vel[0] *= v1x * k; pt.Vel[1] *= v1y * k; pt.Vel[2] *= v1z * k;
        }

        pt.Pos[0] += vpx; pt.Pos[1] += vpy; pt.Pos[2] += vpz;
    }

    private void RollSize(VisualRuntime v0, ParticleState pt)
    {
        if (v0.IsMesh)
        {
            pt.FlickX = v0.RsMin + (Frand() * v0.RsRange);
            pt.FlickY = pt.FlickX;
            return;
        }

        float r1 = Frand();
        pt.FlickX = v0.FlickerBaseX + (r1 * v0.FlickerSpreadX);
        float r2 = v0.FlickerShared ? r1 : Frand();
        pt.FlickY = v0.FlickerBaseY + (r2 * v0.FlickerSpreadY);
    }

    private void InitParticleVisual(EmitterRuntime rt, ParticleState pt, float step)
    {
        pt.Seg = 0;
        pt.LoopIter = 0;
        pt.Rot = 0f;
        pt.Spin = 0f;
        pt.SpinNext = 0f;
        pt.FlickX = 1f;
        pt.FlickY = 1f;
        for (int j = 0; j < 4; j++) pt.TexFrame[j] = 0f;
        pt.Seed = Frand();
        VisualRuntime? v0 = rt.Visuals.Count == 0 ? null : rt.Visuals[0];
        if (v0 is null) return;
        if (v0.IsMesh)
        {
            pt.Rot = v0.OrRandAngle ? Frand() * TwoPi : 0f;
            if (v0.OrRandAxis)
            {
                float ax, ay, az, l2;
                do { ax = Srand2(); ay = Srand2(); az = Srand2(); l2 = (ax * ax) + (ay * ay) + (az * az); } while (l2 > 1f || l2 < 1e-8f);
                float il = 1f / MathF.Sqrt(l2);
                pt.Axis[0] = ax * il; pt.Axis[1] = ay * il; pt.Axis[2] = az * il;
            }
            else
            {
                // The authored axis is normalised in place, so it is copied rather than used —
                // into scratch, not into a fresh array per spawned mesh particle.
                float[] al = _spawnAxis;
                al[0] = v0.OrAxis[0]; al[1] = v0.OrAxis[1]; al[2] = v0.OrAxis[2];
                if (!VNorm(al, 1e-4f)) { al[0] = 0; al[1] = 0; al[2] = 1; }
                MatVec(rt.RotW, al, pt.Axis);
            }

            pt.FlickX = v0.HasRandScale ? v0.RsMin + (Frand() * v0.RsRange) : 1f;
            pt.FlickY = pt.FlickX;
            float w = (step * v0.OrSpeed) + (Frand() * v0.OrSpread);
            pt.Spin = w;
            pt.SpinNext = w;
            return;
        }

        float r = Frand();
        for (int j = 0; j < 4; j++) pt.TexFrame[j] = rt.SlotRandom[j] ? rt.SlotFrames[j] * r : 0f;
        pt.Rot = v0.RotInitialRandom ? Frand() * TwoPi : 0f;
        if (v0.HasFlicker) RollSize(v0, pt);
        float rr = Frand();
        float sp = (step * v0.RotSpeed) + ((rr * v0.RotSpread) - (v0.RotSpread * 0.5f));
        pt.Spin = sp;
        pt.SpinNext = sp;
    }

    private void SpawnOne(InstanceState inst, EmitterRuntime rt, float frac)
    {
        FxEmitter d = rt.Def!;
        ParticleState pt = RentParticle();
        float[] off = _spawnOff;
        float[] dirw = _spawnDir;
        Array.Clear(off);
        Array.Clear(dirw);
        bool dirFromShape = false;
        if (d.SubType == 1 && d.SpawnRadius > 0f)
        {
            float z = (Frand() * 2f) - 1f;
            float phi = Frand() * TwoPi;
            float s = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
            float[] o = _spawnO;
            o[0] = MathF.Cos(phi) * s; o[1] = MathF.Sin(phi) * s; o[2] = z;
            float r = d.ShapeFlagA ? d.SpawnRadius : Frand() * d.SpawnRadius;
            o[0] *= r; o[1] *= r; o[2] *= r;
            MatVec(rt.RotW, o, off);
            if (d.ShapeFlagB) { float[] od = _spawnOd; od[0] = off[0]; od[1] = off[1]; od[2] = off[2]; if (VNorm(od, 1e-4f)) { dirw[0] = od[0]; dirw[1] = od[1]; dirw[2] = od[2]; dirFromShape = true; } }
        }
        else if (d.SubType == 2 && d.SpawnRadius > 0f)
        {
            float phi = Frand() * TwoPi;
            float[] o = _spawnO;
            o[0] = MathF.Cos(phi); o[1] = MathF.Sin(phi); o[2] = 0f;
            float r = d.ShapeFlagA ? d.SpawnRadius : Frand() * d.SpawnRadius;
            o[0] *= r; o[1] *= r;
            MatVec(rt.RotW, o, off);
            if (d.ShapeFlagB) { float[] od = _spawnOd; od[0] = off[0]; od[1] = off[1]; od[2] = off[2]; if (VNorm(od, 1e-4f)) { dirw[0] = od[0]; dirw[1] = od[1]; dirw[2] = od[2]; dirFromShape = true; } }
        }

        if (!dirFromShape) ComputeDir(rt, dirw);

        // The shape offset takes the element's SetScale too: `sub_82A03D58` 0x82a03ec8
        // `vmaddfp v0, v7, v6, v8` = offset × splat(node+0x19C) + the lerped position.
        for (int k = 0; k < 3; k++) pt.Pos[k] = (off[k] * rt.Scale * _sizeScale) + ((rt.PosW[k] - rt.PrevPos[k]) * frac) + rt.PrevPos[k];

        int f = (int)rt.Age;
        float dts = inst.DtSec;
        float speed = d.Generic[4].EvalFrame(f) + (Frand() * d.Generic[5].EvalFrame(f));
        pt.Vel[0] = dirw[0] * speed * dts;
        pt.Vel[1] = dirw[1] * speed * dts;
        pt.Vel[2] = dirw[2] * speed * dts;

        bool hasAttr = rt.Attractors.Count != 0;
        bool hasP14 = !d.Generic[14].IsEmpty;
        bool hasVec0 = !d.Vec0.IsEmpty;
        StepParticle(inst, rt, pt, hasAttr, hasP14, hasVec0);
        pt.Life = rt.LoopCount == -1 ? (d.UseLife ? d.LifetimeFrames : -1f) : rt.LifeFrames;

        InitParticleVisual(rt, pt, inst.Step);
        pt.HistCount = 0;
        if (rt.Trail)
        {
            float[] head = pt.Hist[0] ??= new float[3];
            head[0] = pt.Pos[0]; head[1] = pt.Pos[1]; head[2] = pt.Pos[2];
            pt.HistCount = 1;
        }

        rt.Particles.Add(pt);
        TotalSpawned++;
    }

    // Pooled particles. A particle lives for seconds, so a fresh one (object, four vectors, a
    // history array per trail step) would be promoted out of gen0. Dead particles return here
    // and are rented back with their arrays; only the scalar state is reset.
    private readonly Stack<ParticleState> _particlePool = new();
    private readonly float[] _spawnOff = new float[3];
    private readonly float[] _spawnDir = new float[3];
    private readonly float[] _spawnO = new float[3];
    private readonly float[] _spawnOd = new float[3];
    private readonly float[] _spawnAxis = new float[3];
    private readonly float[] _beamDir = new float[3];

    /// <summary>Beam scratch: a segment's offset and its base-to-current delta, reused per
    /// segment per tick.</summary>
    private readonly float[] _beamOff = new float[3];
    private readonly float[] _beamDelta = new float[3];

    private ParticleState RentParticle()
    {
        ParticleState pt = _particlePool.Count != 0 ? _particlePool.Pop() : new ParticleState();
        pt.HasPrev = false;
        pt.Age = 0f;
        pt.KeyAge = 0f;
        pt.Life = 0f;
        pt.Intensity = _intensity;
        pt.Seg = 1;
        pt.LoopIter = 0;
        pt.Rot = 0f;
        pt.Spin = 0f;
        pt.SpinNext = 0f;
        pt.Seed = 0f;
        pt.FlickX = 1f;
        pt.FlickY = 1f;
        pt.HistCount = 0;
        Array.Clear(pt.Pos);
        Array.Clear(pt.Vel);
        Array.Clear(pt.TexFrame);
        Array.Clear(pt.Axis);
        return pt;
    }

    private void ReturnParticle(ParticleState pt)
    {
        if (_particlePool.Count < 4096)
        {
            _particlePool.Push(pt);
        }
    }

    private void BeamRandomOffset(EmitterRuntime rt, float[] o)
    {
        FxEmitter d = rt.Def!;
        float[] dir = _beamDir;
        do { dir[0] = Srand2(); dir[1] = Srand2(); dir[2] = Srand2(); } while (!VNorm(dir, 1e-4f));
        float sc = rt.BeamSegLen * d.RandomiseAnimMagnitude;
        if (d.RandomiseAnimMagnitudeFlag) sc = Frand() * sc * sc;
        o[0] = dir[0] * sc; o[1] = dir[1] * sc; o[2] = dir[2] * sc;
    }

    private void BeamInit(EmitterRuntime rt)
    {
        FxEmitter d = rt.Def!;
        int nseg = Math.Max(1, d.NumSplineSegments);
        var endw = new float[3];
        MatVec(rt.RotW, new[] { 0f, 3f, 0f }, endw);
        var P0 = new[] { rt.PosW[0], rt.PosW[1], rt.PosW[2] };
        var P1 = new[] { rt.PosW[0] + (endw[0] * rt.Scale), rt.PosW[1] + (endw[1] * rt.Scale), rt.PosW[2] + (endw[2] * rt.Scale) };
        var stepv = new[] { (P1[0] - P0[0]) / nseg, (P1[1] - P0[1]) / nseg, (P1[2] - P0[2]) / nseg };
        rt.BeamSegLen = VLen(stepv);
        rt.BeamBase.Clear(); rt.BeamPts.Clear(); rt.BeamWander.Clear();
        for (int i = 0; i <= nseg; i++) rt.BeamBase.Add(new[] { P0[0] + (i * stepv[0]), P0[1] + (i * stepv[1]), P0[2] + (i * stepv[2]) });
        foreach (float[] b in rt.BeamBase) rt.BeamPts.Add(new[] { b[0], b[1], b[2] });
        for (int i = 1; i < nseg; i++)
        {
            var off = new float[3];
            BeamRandomOffset(rt, off);
            for (int c = 0; c < 3; c++) rt.BeamPts[i][c] += off[c];
        }

        if (d.AnimationMode == 2)
        {
            for (int i = 0; i < nseg - 1; i++)
            {
                var v = new float[3];
                do { v[0] = Srand2(); v[1] = Srand2(); v[2] = Srand2(); } while (!VNorm(v, 1e-4f));
                rt.BeamWander.Add(v);
            }
        }

        rt.BeamJerkTimer = 0f;
    }

    private void BeamTick(InstanceState inst, EmitterRuntime rt)
    {
        FxEmitter d = rt.Def!;
        int n = rt.BeamPts.Count;
        if (n < 2) return;
        if (rt.BeamJerkTimer > 0f) rt.BeamJerkTimer -= inst.DtSec;
        switch (d.AnimationMode)
        {
            case 1:
                if (rt.BeamJerkTimer <= 0f)
                {
                    float[] off = _beamOff;
                    for (int i = 1; i <= n - 2; i++)
                    {
                        BeamRandomOffset(rt, off);
                        for (int c = 0; c < 3; c++) rt.BeamPts[i][c] = rt.BeamBase[i][c] + off[c];
                    }
                }

                break;
            case 2:
            {
                float thresh = d.RandomiseAnimMagnitude * rt.BeamSegLen;
                float speed = d.SmoothAnimationSpeed;
                for (int i = 1; i <= n - 2 && i - 1 < rt.BeamWander.Count; i++)
                {
                    float[] dv = _beamDelta;
                    dv[0] = rt.BeamBase[i][0] - rt.BeamPts[i][0];
                    dv[1] = rt.BeamBase[i][1] - rt.BeamPts[i][1];
                    dv[2] = rt.BeamBase[i][2] - rt.BeamPts[i][2];
                    float dist = VLen(dv);
                    float[] w = rt.BeamWander[i - 1];
                    float k = speed;
                    if (dist > thresh) { w[0] = -w[0]; w[1] = -w[1]; w[2] = -w[2]; k = 2f * speed; }
                    for (int c = 0; c < 3; c++) rt.BeamPts[i][c] += w[c] * k * rt.BeamSegLen;
                }

                break;
            }

            default: break;
        }

        if (rt.BeamJerkTimer <= 0f)
        {
            float j = d.JerkyAnimationRate;
            if (d.RandomiseAnimationRates) j = Frand() * j;
            rt.BeamJerkTimer = j;
        }

        while (rt.Particles.Count > n)
        {
            ReturnParticle(rt.Particles[rt.Particles.Count - 1]);
            rt.Particles.RemoveAt(rt.Particles.Count - 1);
        }
        while (rt.Particles.Count < n)
        {
            ParticleState pt = RentParticle();
            pt.Life = rt.LoopCount == -1 ? (d.UseLife ? d.LifetimeFrames : -1f) : rt.LifeFrames;
            InitParticleVisual(rt, pt, inst.Step);
            rt.Particles.Add(pt);
            TotalSpawned++;
        }

        if (d.AnimationMode <= 2)
        {
            for (int i = 0; i < n; i++)
            {
                rt.Particles[i].Pos[0] = rt.BeamPts[i][0];
                rt.Particles[i].Pos[1] = rt.BeamPts[i][1];
                rt.Particles[i].Pos[2] = rt.BeamPts[i][2];
            }
        }
    }

    private readonly float[] _tickRot = new float[9];
    private readonly float[] _tickV0L = new float[3];
    private readonly float[] _tickV0W = new float[3];
    private readonly float[] _tickAcc = new float[3];

    private void Tick(InstanceState inst)
    {
        int istep = inst.Step;
        float step = istep;
        float dts = inst.DtSec;
        inst.Frame += istep;

        // The step's starting point, for the draw between steps.
        foreach (EmitterRuntime held in inst.Emitters)
        {
            foreach (ParticleState live in held.Particles)
            {
                live.PrevPos[0] = live.Pos[0]; live.PrevPos[1] = live.Pos[1]; live.PrevPos[2] = live.Pos[2];
                live.HasPrev = true;
            }
        }
        if (inst.SharedRand < 0f) inst.SharedRand = Frand();

        float[] rotTmp = _tickRot;
        foreach (AttractorRuntime at in inst.Attractors)
        {
            at.Age += step;
            UpdateChain(at.Chain, inst, inst.PlacePos, inst.PlaceRot, inst.Scale, at.WorldPos, rotTmp);
        }

        foreach (LightRuntime lt in inst.Lights)
        {
            lt.PrevWorldPos[0] = lt.WorldPos[0]; lt.PrevWorldPos[1] = lt.WorldPos[1]; lt.PrevWorldPos[2] = lt.WorldPos[2];
            lt.PrevAge = lt.Age;
            lt.PrevPhase = lt.Phase;
            lt.HasPrev = true;
            lt.Age += step;
            UpdateChain(lt.Chain, inst, inst.PlacePos, inst.PlaceRot, inst.Scale, lt.WorldPos, rotTmp);

            // The same flicker walk as a placed fixture: one light class, one update
            // (`sub_821F0A50`, vtable slot 25): draw uniformly from [0, 2), step the phase by
            // ((rand − 1) − phase) × rate × dt·30, clamp to [−1, 1]. Params 19/20/21 are the
            // radius range, the brightness range and the rate (`sub_82A49D08` stores them at
            // cmd+0x34/+0x30/+0x38 → light+0xE4/+0xE0/+0xE8), defaults 0.05/0.05/0.1 — so
            // every effect light breathes a little unless its data says otherwise.
            if (lt.Def is { } ld)
            {
                int lf = (int)lt.Age;
                float rate = ld.Generic[21].EvalFrame(lf);
                if (rate > 0f)
                {
                    float target = (Frand() * 2f) - 1f;
                    lt.Phase = Math.Clamp(lt.Phase + ((target - lt.Phase) * rate * step), -1f, 1f);
                }
            }
        }

        float[] v0l = _tickV0L;
        float[] v0w = _tickV0W;
        foreach (EmitterRuntime rt in inst.Emitters)
        {
            FxEmitter d = rt.Def!;
            int fOld = (int)rt.Age;

            Array.Copy(rt.PosW, rt.PrevPos, 3);
            UpdateChain(rt.Chain, inst, rt.PlacePos, rt.PlaceRot, rt.Scale, rt.PosW, rt.RotW);
            if (rt.FirstFrame) Array.Copy(rt.PosW, rt.PrevPos, 3);

            (float v1x, float v1y, float v1z) = d.Vec1.EvalFrame(fOld);
            float m12 = d.Generic[12].EvalFrame(fOld);
            float m13 = d.Generic[13].EvalFrame(fOld);
            rt.VelMul[0] = m13 * v1x * m12; rt.VelMul[1] = m13 * v1y * m12; rt.VelMul[2] = m13 * v1z * m12;

            (v0l[0], v0l[1], v0l[2]) = d.Vec0.EvalFrame(fOld);
            MatVec(rt.RotW, v0l, v0w);
            float g6 = d.Generic[6].EvalFrame(fOld);
            float g7 = d.Generic[7].EvalFrame(fOld);
            float[] acc = _tickAcc;
            acc[0] = (v0w[0] * g6) + (_down[0] * Gravity * g7);
            acc[1] = (v0w[1] * g6) + (_down[1] * Gravity * g7);
            acc[2] = (v0w[2] * g6) + (_down[2] * Gravity * g7);
            // × node+412 as well (`sub_8322D988` 0x8322dc38 `lvlx v18, r31, 0x19C` into the
            // displacement product).
            for (int k = 0; k < 3; k++) rt.WindDisp[k] = acc[k] * rt.Scale * _sizeScale * dts * dts;

            rt.Age += step;
            int f = (int)rt.Age;

            bool windowOpen = !d.UseLife || rt.Age <= d.LifetimeFrames;
            bool infLoop = rt.LoopCount == -1;
            bool useLife = d.UseLife;
            float lifeFrames = d.LifetimeFrames;
            int nvis = rt.Visuals.Count;
            int kept = 0;
            for (int i = 0; i < rt.Particles.Count; i++)
            {
                ParticleState p = rt.Particles[i];
                bool dead = !infLoop ? p.Age >= p.Life : (useLife && lifeFrames <= (p.LoopIter + 1) * p.Age);
                if (dead)
                {
                    ReturnParticle(p);
                    continue;
                }

                rt.Particles[kept++] = p;
            }

            rt.Particles.RemoveRange(kept, rt.Particles.Count - kept);
            if (rt.Beam)
            {
                BeamTick(inst, rt);
                rt.FirstFrame = false;
                foreach (ParticleState pt in rt.Particles)
                {
                    while (pt.Seg < nvis && pt.Age >= rt.Visuals[pt.Seg].DelayFrames) pt.Seg++;
                    pt.KeyAge = pt.Age;
                    pt.Age += step;
                }

                continue;
            }

            if (windowOpen)
            {
                float spread = d.Generic[3].EvalFrame(f) * step;
                float r = Frand() * spread;
                float rate = d.Generic[2].EvalFrame(f);
                if (rt.Mode != 3) { r *= step; rate *= step; }
                rt.Accum += rate + r;
                int count = rt.Particles.Count;
                int n = (int)rt.Accum;
                int maxActive = (int)d.MaxActive;
                if (maxActive >= 0 && count + n > maxActive) rt.Accum -= count + n - maxActive;
                if (rt.Accum >= 1f)
                {
                    float inv = 1f / rt.Accum;
                    float frac = 1f;
                    int guard = 4096;
                    while (rt.Accum >= 1f && guard-- > 0)
                    {
                        rt.Accum -= 1f;
                        SpawnOne(inst, rt, frac);
                        frac -= inv;
                    }
                }
            }
            else
            {
                rt.Accum = 0f;
            }

            rt.FirstFrame = false;

            bool hasAttr = rt.Attractors.Count != 0;
            bool hasP14 = !d.Generic[14].IsEmpty;
            bool hasVec0 = !d.Vec0.IsEmpty;
            VisualRuntime? v0 = nvis > 0 ? rt.Visuals[0] : null;
            foreach (ParticleState pt in rt.Particles)
            {
                if (rt.LoopCount != 0 && rt.LoopEnd <= pt.Age)
                {
                    if (rt.LoopCount < 0 || pt.LoopIter < rt.LoopCount) { pt.Age = rt.LoopStart; pt.Seg = 1; pt.LoopIter++; }
                    else pt.LoopIter = 0;
                }

                bool crossed = false;
                while (pt.Seg < nvis && pt.Age >= rt.Visuals[pt.Seg].DelayFrames) { pt.Seg++; crossed = true; }
                int kb = nvis <= 1 ? 0 : Math.Clamp(pt.Seg, 1, nvis - 1);
                int ka = Math.Max(kb - 1, 0);
                float t = 0f;
                if (nvis > 1 && kb > ka)
                {
                    t = (pt.Age - rt.Visuals[ka].DelayFrames) * rt.Visuals[kb].InvSpan;
                    t = Math.Clamp(t, 0f, 1f);
                }

                if (crossed && v0 is not null && nvis > 1)
                {
                    VisualRuntime vp = rt.Visuals[ka];
                    VisualRuntime vk = rt.Visuals[kb];
                    if (vk.IsMesh)
                    {
                        pt.Spin = pt.SpinNext;
                        pt.SpinNext = (step * vk.OrSpeed) + (Frand() * vk.OrSpread);
                    }
                    else if (vp.RotSpeed != vk.RotSpeed)
                    {
                        pt.Spin = pt.SpinNext;
                        pt.SpinNext = (step * vk.RotSpeed) + ((Frand() * vk.RotSpread) - (vk.RotSpread * 0.5f));
                    }
                }

                if (!rt.Frozen) StepParticle(inst, rt, pt, hasAttr, hasP14, hasVec0);
                float ang = pt.Rot + (pt.Spin + ((pt.SpinNext - pt.Spin) * t));
                if (v0 is not null && v0.IsMesh) pt.Rot = ang;
                else pt.Rot = ang - (TwoPi * MathF.Round(ang / TwoPi));

                if (v0 is not null && !v0.IsMesh && nvis > 0)
                {
                    float fcsA = rt.Visuals[ka].FrameSpeed;
                    float fcsB = rt.Visuals[kb].FrameSpeed;
                    float fade = fcsA + ((fcsB - fcsA) * t);
                    for (int j = 0; j < 4; j++)
                    {
                        float fr = pt.TexFrame[j] + fade;
                        pt.TexFrame[j] = fr < rt.SlotFrames[j] ? fr : 0f;
                    }
                }

                if (rt.Trail)
                {
                    int hn = Math.Min(pt.HistCount + 1, TrailHistory);
                    float[]? spare = pt.Hist[hn - 1];
                    for (int k = hn - 1; k > 0; k--) pt.Hist[k] = pt.Hist[k - 1];
                    float[] head = spare ?? new float[3];
                    head[0] = pt.Pos[0]; head[1] = pt.Pos[1]; head[2] = pt.Pos[2];
                    pt.Hist[0] = head;
                    pt.HistCount = hn;
                }

                pt.KeyAge = pt.Age;
                pt.Age += step;

                if (v0 is not null)
                {
                    int rsFrames = v0.IsMesh ? v0.RsFrames : v0.FlickerFrames;
                    bool rsOn = v0.IsMesh ? v0.HasRandScale : v0.HasFlicker;
                    if (rsOn && rsFrames > 0)
                    {
                        int clk = (int)MathF.Floor(pt.Age + 0.5f);
                        if (clk % rsFrames == 0) RollSize(v0, pt);
                    }
                }
            }
        }

        if (_loop && inst.Time > 1.5f)
        {
            int alive = 0;
            foreach (EmitterRuntime rt in inst.Emitters) alive += rt.Particles.Count;
            if (alive == 0)
            {
                inst.Time = 0f;
                inst.Frame = 0;
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    rt.Age = 0f;
                    rt.Accum = rt.Def is not null ? MathF.Max(0f, rt.Def.InitialNum) : 0f;
                    rt.FirstFrame = true;
                    foreach (ChainNode n in rt.Chain) { n.Angle = 0f; n.T = -1f; n.Dir = 1f; }
                }

                foreach (AttractorRuntime at in inst.Attractors) { at.Age = 0f; foreach (ChainNode n in at.Chain) { n.Angle = 0f; n.T = -1f; n.Dir = 1f; } }
                foreach (LightRuntime lt in inst.Lights) { lt.Age = 0f; foreach (ChainNode n in lt.Chain) { n.Angle = 0f; n.T = -1f; n.Dir = 1f; } }
            }
        }
    }

    /// <summary>Simulation steps taken by the last <see cref="Update"/>, over every instance.</summary>
    public int LastSteps { get; private set; }

    public void Update(float dt, Transform3D? nodeGlobal = null)
    {
        LastSteps = 0;
        if (nodeGlobal is { } current)
        {
            if (_previousGlobal is { } previous && _detachedParticles)
            {
                Transform3D delta = current.AffineInverse() * previous;
                if (!delta.IsEqualApprox(Transform3D.Identity)) Rebase(delta);
            }

            _previousGlobal = current;

            // Godot world up into node-local, then Godot (x, y, z) back to the sim's guest
            // layout (x, z, y); `Gravity` carries the sign. The DIRECTION only: a placement's
            // scale already scales every other term of the simulation with the node.
            Vector3 up = current.Basis.Inverse() * Vector3.Up;
            if (up.IsFinite() && !up.IsZeroApprox())
            {
                up = up.Normalized();
                _down[0] = up.X; _down[1] = up.Z; _down[2] = up.Y;
            }
        }

        if (dt <= 0f) dt = 1f / 60f;
        dt = MathF.Min(dt, 0.25f);
        // AN AUTHORED FRAME IS 1/30 s FOR EVERY EMITTER, and `UpdateFrequency` is a simulation
        // LOD rather than a speed. The guest keeps two parallel arrays indexed by that field:
        // update rate 15 or 30 Hz, physics dt 1/15 or 1/30, and frames advanced per update 2.0
        // (0x8209B480) or 1.0 (0x82099490). Its advance is a single `fmadds f11, f12, f0, f13`
        // at 0x8322f21c writing the emitter's frame counter at +0xFC, so both columns come out at
        // 15x2 = 30x1 = 30 authored frames a second.
        //
        // ONE ACCUMULATOR FOR THE EFFECT. A full-rate instance steps on every 1/30 s base step, a
        // half-rate one (Step 2) on every second; sharing the phase is what lets the billboard
        // shader draw every particle of the effect between its last two steps from two fractions
        // (StepAlpha, StepAlphaHalf) instead of one per instance.
        foreach (InstanceState inst in _instances)
        {
            inst.Time += dt;
        }

        _baseAccum += dt;
        int steps = 0;
        while (_baseAccum >= FrameDt && steps < 8)
        {
            _baseAccum -= FrameDt;
            ++_baseSteps;
            foreach (InstanceState inst in _instances)
            {
                if (inst.Step <= 1 || _baseSteps % inst.Step == 0)
                {
                    Tick(inst);
                    ++LastSteps;
                }
            }

            steps++;
        }

        if (steps == 8) _baseAccum = 0f;
    }

    private float _baseAccum;
    private long _baseSteps;

    /// <summary>How far between its last two steps a full-rate emitter is drawn, 0 to 1.</summary>
    public float StepAlpha => Math.Clamp(_baseAccum / FrameDt, 0f, 1f);

    /// <summary>The same for a half-rate emitter (Step 2), which steps on even base steps.</summary>
    public float StepAlphaHalf =>
        Math.Clamp(((_baseSteps % 2 == 1 ? FrameDt : 0f) + _baseAccum) / (2f * FrameDt), 0f, 1f);

    private void Rebase(Transform3D delta)
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Def is null || rt.Def.AttachToRoot) continue;
                foreach (ParticleState pt in rt.Particles)
                {
                    Vector3 pos = delta * new Vector3(pt.Pos[0], pt.Pos[2], pt.Pos[1]);
                    pt.Pos[0] = pos.X; pt.Pos[1] = pos.Z; pt.Pos[2] = pos.Y;
                    Vector3 prev = delta * new Vector3(pt.PrevPos[0], pt.PrevPos[2], pt.PrevPos[1]);
                    pt.PrevPos[0] = prev.X; pt.PrevPos[1] = prev.Z; pt.PrevPos[2] = prev.Y;
                    Vector3 vel = delta.Basis * new Vector3(pt.Vel[0], pt.Vel[2], pt.Vel[1]);
                    pt.Vel[0] = vel.X; pt.Vel[1] = vel.Z; pt.Vel[2] = vel.Y;
                    for (int k = 0; k < pt.HistCount; k++)
                    {
                        float[]? h = pt.Hist[k];
                        if (h is null) continue;
                        Vector3 hp = delta * new Vector3(h[0], h[2], h[1]);
                        h[0] = hp.X; h[1] = hp.Z; h[2] = hp.Y;
                    }
                }
            }
        }
    }

    /// <summary>Every live particle's position in Godot space, for the offline tools.</summary>
    public IEnumerable<Vector3> ParticlePositions()
    {
        foreach (InstanceState inst in _instances)
            foreach (EmitterRuntime rt in inst.Emitters)
                foreach (ParticleState pt in rt.Particles)
                    yield return ToGodot(pt.Pos);
    }

    public int AliveCount()
    {
        int alive = 0;
        foreach (InstanceState inst in _instances) foreach (EmitterRuntime rt in inst.Emitters) alive += rt.Particles.Count;
        return alive;
    }

    public static bool EffectSustains(FxBank bank, uint effectHash, float seconds = 12f)
    {
        var probe = new Engine_FxSystem();
        var placement = new FxPlacement { EffectHash = effectHash };
        probe.Build(bank, new[] { placement });
        if (!placement.Resolved) return false;
        probe.SetLoop(false);
        int steps = (int)(seconds * 30f);
        int clipEnd = -1;
        int lastSpawnStep = 0;
        int previousSpawned = 0;
        bool everAlive = false;
        for (int i = 0; i < steps; i++)
        {
            probe.Update(1f / 30f);
            if (probe.TotalSpawned > previousSpawned) { previousSpawned = probe.TotalSpawned; lastSpawnStep = i; }
            if (probe.AliveCount() > 0) everAlive = true;
            else if (everAlive && clipEnd < 0) { clipEnd = i; break; }
        }

        if (!everAlive) return false;
        if (clipEnd < 0) return true;
        return lastSpawnStep >= (int)(clipEnd * 0.5f);
    }

    public IEnumerable<string> Textures()
    {
        var seen = new HashSet<string>();
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                foreach (VisualRuntime vis in rt.Visuals) if (vis.Texture.Length != 0 && seen.Add(vis.Texture)) yield return vis.Texture;
                foreach (VisualRuntime vis in rt.TrailVisuals) if (vis.Texture.Length != 0 && seen.Add(vis.Texture)) yield return vis.Texture;
            }
        }
    }

    public IEnumerable<string> MeshPaths()
    {
        var seen = new HashSet<string>();
        foreach (InstanceState inst in _instances)
            foreach (EmitterRuntime rt in inst.Emitters)
                foreach (VisualRuntime vis in rt.Visuals)
                    if (vis.IsMesh && vis.MeshPath.Length != 0 && seen.Add(vis.MeshPath)) yield return vis.MeshPath;
    }

    public void Clear()
    {
        _instances.Clear();
        ResolvedCount = 0;
        _previousGlobal = null;
        _detachedParticles = false;
    }

    /// <summary>
    /// Range is the falloff radius (param 15); Cutoff the camera distance at which the guest's
    /// intensity reaches zero (param 17, capped 1000); Ramp the width of that fade inside the
    /// cutoff (param 18); ConeAngle the spot half-angle in radians (param 24, spots only —
    /// zero means no cone).
    /// </summary>
    public readonly record struct FxLightState(Vector3 Position, Color Colour, float Range, float Energy, float Cutoff, float Ramp, float ConeAngle);

    public int LightCount
    {
        get { int total = 0; foreach (InstanceState inst in _instances) total += inst.Lights.Count; return total; }
    }

    public FxLightState LightAt(int index)
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (LightRuntime light in inst.Lights)
            {
                if (index-- != 0) continue;
                FxEmitter? d = light.Def;
                if (d is null) return default;
                FxLightState now = LightState(d, light.Age, light.Phase, light.WorldPos);
                if (!light.HasPrev)
                {
                    return now;
                }

                // Between the last two steps, as the particles are drawn: on Godot's clock, at the
                // effect's sub-step fraction for this instance's rate.
                FxLightState before = LightState(d, light.PrevAge, light.PrevPhase, light.PrevWorldPos);
                float a = inst.Step > 1 ? StepAlphaHalf : StepAlpha;
                return new FxLightState(
                    before.Position.Lerp(now.Position, a),
                    before.Colour.Lerp(now.Colour, a),
                    before.Range + ((now.Range - before.Range) * a),
                    before.Energy + ((now.Energy - before.Energy) * a),
                    now.Cutoff,
                    now.Ramp,
                    now.ConeAngle);
            }
        }

        return default;
    }

    private static FxLightState LightState(FxEmitter d, float age, float phase, float[] worldPos)
    {
        int f = (int)age;
        (float r, float g, float b) = d.LightColour.EvalFrame(f);

        // Brightness = 1 + phase × p20, clamped to [0, 2] (0x821f0c4c, 0x821f0ca8);
        // radius = 1 + phase × p19 (0x821f0c84), taken by a point light outright and by
        // a spot only past two flags whose meaning is not established, so point only —
        // the same reading the placed fixtures take.
        float brightness = Math.Clamp(1f + (phase * d.Generic[20].EvalFrame(f)), 0f, 2f);
        float radius = d.TypeId == 7 ? 1f + (phase * d.Generic[19].EvalFrame(f)) : 1f;
        float energy = d.LightIntensity.EvalFrame(f) * brightness;

        // Not scaled by the effect's scale: `sub_8323FA50` hands param 15 to the proxy
        // unmultiplied (`fmr f1, f30` → `stfs f31, 0x90`), and the colour path is
        // colour × intensity × kill-fade only. Only the position takes the transform.
        return new FxLightState(
            ToGodot(worldPos),
            new Color(r, g, b),
            MathF.Max(0f, d.LightRange.EvalFrame(f) * radius),
            energy,
            d.LightCutoff.EvalFrame(f),
            d.LightFadeRamp.EvalFrame(f),
            d.LightConeAngle.EvalFrame(f));
    }

    /// <summary>
    /// The halo texture the light at <paramref name="index"/> states, or 0 — the same walk as
    /// <see cref="LightAt"/>, so the indices agree with the lights FableFx built.
    /// </summary>
    public uint HaloAt(int index)
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (LightRuntime light in inst.Lights)
            {
                if (index-- != 0) continue;
                return light.Def?.HaloTextureHash ?? 0;
            }
        }

        return 0;
    }

    public sealed class MeshInstanceData
    {
        public string MeshPath = string.Empty;
        public bool Displacement;
        /// <summary>
        /// Each mesh particle at its last step, its colour, and its last step's displacement with
        /// the rate class in w — placed between steps every frame (`FableFx.PlaceMeshParticles`).
        /// </summary>
        public List<(Transform3D Transform, Color Colour, Vector4 Motion)> Instances { get; } = new();
    }

    private readonly struct DrawKey : IEquatable<DrawKey>
    {
        public readonly string Texture; public readonly string Mode; public readonly bool Sat; public readonly float Soft; public readonly int ZOrder;
        public DrawKey(string t, string m, bool sat, float soft, int z) { Texture = t; Mode = m; Sat = sat; Soft = soft; ZOrder = z; }
        public bool Equals(DrawKey o) => Texture == o.Texture && Mode == o.Mode && Sat == o.Sat && Soft == o.Soft && ZOrder == o.ZOrder;
        public override bool Equals(object? o) => o is DrawKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Texture, Mode, Sat, Soft, ZOrder);
    }

    /// <summary>
    /// The modern model's multiplier on the fade-out distances, the same doubling the placed
    /// lights' cutoff takes (the authored distances suit the original draw distance). 1.0
    /// follows the shipped data.
    /// </summary>
    public static float FadeReachScale = 1f;

    private float DistanceFadeFactor(EmitterRuntime rt, Vector3 p, Vector3 cam)
    {
        if (!_distanceFade || rt.Def is null) return 1f;
        float dist = (cam - p).Length();
        float[] fade = rt.Fade;
        float reach = FadeReachScale > 0f ? FadeReachScale : 1f;
        float f2 = fade[2] * reach, f3 = fade[3] * reach;
        float fi = fade[1] > fade[0] ? Math.Clamp((dist - fade[0]) / (fade[1] - fade[0]), 0f, 1f) : (dist >= fade[1] ? 1f : 0f);
        float fo = f3 > f2 ? Math.Clamp((f3 - dist) / (f3 - f2), 0f, 1f) : (dist <= f3 ? 1f : 0f);
        return fi * fo;
    }

    public void EmitGeometry(
        ArrayMesh mesh,
        Func<string, VisualBatchMaterial?> materialLookup,
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Dictionary<string, MeshInstanceData> meshOut,
        ArrayMesh? dispMesh = null,
        ArrayMesh? addMesh = null)
    {
        BeginSurfaces(mesh);
        if (dispMesh is not null) BeginSurfaces(dispMesh);
        if (addMesh is not null) BeginSurfaces(addMesh);
        foreach (MeshInstanceData data in meshOut.Values) data.Instances.Clear();

        // Pooled: the batch lists, the trail scratch and the key order live with the system.
        Dictionary<DrawKey, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>> batches = _batches;
        foreach (List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> pooled in batches.Values) pooled.Clear();
        // The delegate is cached: converting a method to a Func allocates, and this is used once
        // per ribbon inside the particle loop.
        System.Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>> Sink =
            _sink ??= SinkFor;

        List<Vector3> pts = _pts;
        List<float> ages = _ages;
        List<Vector4> motions = _motions;
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                int nvis = rt.Visuals.Count;
                if (rt.Trail && rt.TrailVisuals.Count != 0)
                {
                    foreach (ParticleState pt in rt.Particles)
                    {
                        pts.Clear(); ages.Clear(); motions.Clear();
                        float rateClass = inst.Step > 1 ? 1f : 0f;
                        for (int k = 0; k < pt.HistCount; k++)
                        {
                            float hage = k * inst.Step;
                            if (rt.TrailLife > 0f && hage >= rt.TrailLife) break;
                            float[]? h = pt.Hist[k];
                            if (h is null) break;
                            pts.Add(ToGodot(h)); ages.Add(hage);

                            // The history shifts one slot a step, so between steps every point
                            // flows from the slot behind it to its own: the trail slides.
                            float[]? behind = k + 1 < pt.HistCount ? pt.Hist[k + 1] : null;
                            Vector3 flow = behind is null ? Vector3.Zero : ToGodot(h) - ToGodot(behind);
                            motions.Add(new Vector4(flow.X, flow.Y, flow.Z, rateClass));
                        }

                        EmitRibbon(Sink, rt, rt.TrailVisuals, pts, ages, motions, cameraPosition, false, inst.Lit, pt.Intensity);
                    }
                }

                if (rt.Ribbon)
                {
                    pts.Clear(); ages.Clear(); motions.Clear();
                    float ribbonIntensity = 1f;
                    float chainClass = inst.Step > 1 ? 1f : 0f;
                    foreach (ParticleState pt in rt.Particles)
                    {
                        pts.Add(ToGodot(pt.Pos)); ages.Add(pt.KeyAge); ribbonIntensity = pt.Intensity;
                        Vector3 moved = pt.HasPrev ? ToGodot(pt.Pos) - ToGodot(pt.PrevPos) : Vector3.Zero;
                        motions.Add(new Vector4(moved.X, moved.Y, moved.Z, chainClass));
                    }

                    EmitRibbon(Sink, rt, rt.Visuals, pts, ages, motions, cameraPosition, true, inst.Lit, ribbonIntensity);
                    continue;
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    if (nvis == 0) continue;
                    Vector3 ppos = ToGodot(pt.Pos);
                    float tLife = pt.Life > 0f ? Math.Clamp(pt.KeyAge / pt.Life, 0f, 1f) : 0f;
                    int segB = nvis <= 1 ? 0 : Math.Clamp(pt.Seg, 1, nvis - 1);
                    int segA = Math.Max(segB - 1, 0);
                    VisualRuntime va = rt.Visuals[segA];
                    VisualRuntime? vb = nvis > 1 && segB > segA ? rt.Visuals[segB] : null;
                    float w = 0f;
                    if (vb is not null) w = Math.Clamp((pt.KeyAge - va.DelayFrames) * vb.InvSpan, 0f, 1f);
                    float Lerp(float x, float y) => x + ((y - x) * w);

                    float hx = (vb is not null ? Lerp(va.HalfX, vb.HalfX) : va.HalfX) * pt.FlickX * (vb is not null ? Lerp(va.GlobalScaleX, vb.GlobalScaleX) : va.GlobalScaleX) * _sizeScale;
                    float hy = (vb is not null ? Lerp(va.HalfY, vb.HalfY) : va.HalfY) * pt.FlickY * (vb is not null ? Lerp(va.GlobalScaleY, vb.GlobalScaleY) : va.GlobalScaleY) * _sizeScale;
                    if (hx <= 0f || hy <= 0f) { ++SkipZeroSize; continue; }
                    float si = va.BlendMode == "disp" ? 1f : (vb is not null ? Lerp(va.SelfIllum, vb.SelfIllum) : va.SelfIllum);
                    // One scratch, not one per particle: both emitters copy it into a Color
                    // before returning, so reuse is safe.
                    float[] col = _emitCol;
                    col[0] = inst.Lit[0] + (((vb is not null ? Lerp(va.Colour[0], vb.Colour[0]) : va.Colour[0]) - inst.Lit[0]) * si);
                    col[1] = inst.Lit[1] + (((vb is not null ? Lerp(va.Colour[1], vb.Colour[1]) : va.Colour[1]) - inst.Lit[1]) * si);
                    col[2] = inst.Lit[2] + (((vb is not null ? Lerp(va.Colour[2], vb.Colour[2]) : va.Colour[2]) - inst.Lit[2]) * si);
                    float a = vb is not null ? Lerp(va.Alpha, vb.Alpha) : va.Alpha;
                    // The instance intensity is an alpha term: 0038 `r5.w = fade × FadeOut.z`
                    // (binding 321.z = the batch's intensity), `oColor0.w = r5.w × keyframe.a`;
                    // the pixel shader then multiplies rgb by that alpha as well.
                    a *= DistanceFadeFactor(rt, ppos, cameraPosition) * pt.Intensity;
                    if (a <= 0.003f) { ++SkipZeroAlpha; continue; }

                    // The last step's displacement, and whether this emitter steps at half rate:
                    // a particle is drawn at `centre - motion x (1 - alpha)` between steps.
                    Vector4 motion = pt.HasPrev
                        ? new Vector4(pt.Pos[0] - pt.PrevPos[0], pt.Pos[2] - pt.PrevPos[2], pt.Pos[1] - pt.PrevPos[1],
                            inst.Step > 1 ? 1f : 0f)
                        : new Vector4(0f, 0f, 0f, inst.Step > 1 ? 1f : 0f);

                    if (va.IsMesh)
                    {
                        EmitMeshParticle(va, vb, w, pt, a, col, meshOut, motion);
                        continue;
                    }

                    if (vb is not null && vb.Texture != va.Texture)
                    {
                        EmitBillboard(Sink(va, rt.ZOrder), va, ppos, pt, hx, hy, col, a * (1f - w), tLife, cameraRight, cameraUp, motion);
                        EmitBillboard(Sink(vb, rt.ZOrder), vb, ppos, pt, hx, hy, col, a * w, tLife, cameraRight, cameraUp, motion);
                    }
                    else
                    {
                        ++EmittedBillboards;
                        EmitBillboard(Sink(va, rt.ZOrder), va, ppos, pt, hx, hy, col, a, tLife, cameraRight, cameraUp, motion);
                    }
                }
            }
        }

        List<DrawKey> keys = _keys;
        keys.Clear();
        foreach (DrawKey k in batches.Keys) keys.Add(k);
        keys.Sort(ByZOrder);
        foreach (DrawKey key in keys)
        {
            List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list = batches[key];
            if (list.Count == 0) continue;
            SortQuadsBackToFront(list, cameraPosition);
            if (DebugEmit && list.Count > 0) GD.Print($"[fx-emit] {key.Texture} mode={key.Mode} sat={key.Sat} soft={key.Soft} z={key.ZOrder} verts={list.Count} v0={list[0].Pos} uv0={list[0].Uv} c0={list[0].Col} v1={list[1].Pos} v2={list[2].Pos}");
            if (key.Mode == "disp")
            {
                if (dispMesh is null) continue;
                VisualBatchMaterial? dispMaterial = BatchMaterial(materialLookup, key, "disp");
                if (dispMaterial is null) continue;
                Flush(dispMesh, dispMaterial.Material, list);
                continue;
            }

            // ONE/ONE goes to the additive layer when one is up: the guest's submit
            // (`sub_822749E0` 0x82274b88) sends SrcBlend==ONE && DestBlend==ONE to bucket 5,
            // the half-res A8R8G8B8 surface, and every other blend into the scene.
            if (key.Mode == "addone" && addMesh is not null)
            {
                VisualBatchMaterial? layerMaterial = BatchMaterial(materialLookup, key, "addlayer");
                if (layerMaterial is null) continue;
                Flush(addMesh, layerMaterial.Material, list);
                continue;
            }

            if (key.Mode == "revsub")
            {
                EmitPass(mesh, materialLookup, key, "darken", list);
                EmitPass(mesh, materialLookup, key, "sub", list);
            }
            else
            {
                EmitPass(mesh, materialLookup, key, key.Mode, list);
            }
        }

        EndSurfaces(mesh);
        if (dispMesh is not null) EndSurfaces(dispMesh);
        if (addMesh is not null) EndSurfaces(addMesh);
    }

    private void EmitPass(ArrayMesh mesh, Func<string, VisualBatchMaterial?> materialLookup, DrawKey key, string pass, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list)
    {
        VisualBatchMaterial? material = BatchMaterial(materialLookup, key, pass);
        if (material is not null) Flush(mesh, material.Material, list);
    }

    /// <summary>Batches dropped because no material could be built — each one is lost geometry.</summary>
    public static long BatchMaterialMisses;

    public static long BatchMaterialHits;

    /// <summary>The material names that could not be resolved — the texture or pass at fault.</summary>
    public static readonly System.Collections.Generic.HashSet<string> MissedNames = new();

    /// <summary>The material for a batch and pass, looked up once: the name is built from the
    /// key ("texture|pass[|sat][|softN]", "|disp", "|addlayer[|sat][|softN]") and a hit is kept;
    /// a miss is asked again, since a material can arrive later.</summary>
    private VisualBatchMaterial? BatchMaterial(Func<string, VisualBatchMaterial?> lookup, DrawKey key, string pass)
    {
        if (_materials.TryGetValue((key, pass), out VisualBatchMaterial? hit)) return hit;
        string suffix = (key.Sat ? "|sat" : string.Empty) + (key.Soft > 0f ? "|soft" + key.Soft.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        string name = pass == "disp" ? key.Texture + "|disp" : key.Texture + "|" + pass + suffix;
        VisualBatchMaterial? found = lookup(name);
        if (found is null)
        {
            // A null here is lost geometry: every caller `continue`s on it. Counted and named so
            // the loss is visible.
            ++BatchMaterialMisses;
            if (MissedNames.Count < 24)
            {
                MissedNames.Add(name);
            }

            return null;
        }

        ++BatchMaterialHits;
        _materials[(key, pass)] = found;
        return found;
    }

    private readonly Dictionary<(DrawKey Key, string Pass), VisualBatchMaterial> _materials = new();

    /// <summary>One surface from one batch: the arrays for a single crossing.</summary>
    private sealed class FlushArrays
    {
        public Vector3[] Positions = System.Array.Empty<Vector3>();
        public Vector2[] Uvs = System.Array.Empty<Vector2>();
        public Color[] Colours = System.Array.Empty<Color>();
        public Vector2[] Offsets = System.Array.Empty<Vector2>();

        /// <summary>CUSTOM0, four floats a vertex: the last step's displacement and the rate class.</summary>
        public float[] Motion = System.Array.Empty<float>();
    }

    /// <summary>
    /// Keyed by length and shared by every system: Godot wants exact-length arrays, so a batch
    /// crossing a bucket boundary finds both sizes already built. `AddSurfaceFromArrays`
    /// marshals these into native memory before returning, so nothing holds a set past its own
    /// Flush; like the sort scratch above, this relies on geometry being emitted on the main
    /// thread only.
    /// </summary>
    private static readonly Dictionary<int, FlushArrays> FlushPool = new();

    /// <summary>
    /// The surfaces persist; only their bytes change. Building a surface goes through a
    /// `Godot.Collections.Array` of packed arrays, and every packed-array Variant registers a
    /// `WeakReference` in GodotSharp's DisposablesTracker, whose GC handle .NET frees inside the
    /// collection pause.
    ///
    /// So a batch that fits the surface already at its slot is written straight into the vertex
    /// and attribute buffers from a span (no Array, no Variant, no handle), and a surface is only
    /// rebuilt when a batch outgrows it. Capacities are 96 vertices times a power of two; the
    /// unused tail is degenerate.
    /// </summary>
    private sealed class SurfaceSlots
    {
        public readonly List<int> Capacity = new();
        public int Used;
        public Aabb Bounds;
        public bool HasBounds;

        /// <summary>The largest corner offset emitted this frame: how far a quad reaches from its centre.</summary>
        public float MaxOffset;

        /// <summary>The box last written to the mesh, so an unchanged one is not written again.</summary>
        public Aabb? StatedBox;
    }

    private readonly Dictionary<ArrayMesh, SurfaceSlots> _slots = new();

    private SurfaceSlots SlotsOf(ArrayMesh mesh)
    {
        if (!_slots.TryGetValue(mesh, out SurfaceSlots? slots))
        {
            slots = new SurfaceSlots();
            _slots[mesh] = slots;
        }

        return slots;
    }

    private void BeginSurfaces(ArrayMesh mesh)
    {
        // The bounds are tracked on both build paths: the rebuilt surface's own box covers only
        // the particle CENTRES (the corners are added in the shader), so it is stated either way.
        SurfaceSlots slots = SlotsOf(mesh);
        slots.HasBounds = false;
        slots.MaxOffset = 0f;
        if (!InPlaceLayout.Supported)
        {
            mesh.ClearSurfaces();
            return;
        }

        slots.Used = 0;

        // Anyone else may have cleared the mesh (FableFx drops the additive one when the layer
        // goes away): the slot table follows the surfaces that actually exist.
        int existing = mesh.GetSurfaceCount();
        if (slots.Capacity.Count > existing)
        {
            slots.Capacity.RemoveRange(existing, slots.Capacity.Count - existing);
        }
        else if (slots.Capacity.Count < existing)
        {
            mesh.ClearSurfaces();
            slots.Capacity.Clear();
        }
    }

    private void EndSurfaces(ArrayMesh mesh)
    {
        SurfaceSlots slots = SlotsOf(mesh);
        if (InPlaceLayout.Supported)
        {
            TrimSurfaces(mesh, slots, slots.Used);
        }

        // The whole quad, stated. Surfaces written in place keep the bounds they were built with,
        // and a rebuilt surface's box covers only the particle centres, so the mesh states this
        // frame's: the centres grown by the largest corner offset. A cull margin wide enough to
        // cover the corners instead puts every FX leaf in every light's and shadow pass's reach.
        //
        // Written only when it changed: every write is an instance update to the renderer (a BVH
        // move and a re-pair) and most effects are frozen most frames.
        Aabb box = slots.HasBounds ? slots.Bounds.Grow(slots.MaxOffset) : default;
        if (slots.StatedBox is { } stated && stated == box)
        {
            return;
        }

        slots.StatedBox = box;
        Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
        mesh.CustomAabb = box;
    }

    private static void TrimSurfaces(ArrayMesh mesh, SurfaceSlots slots, int keep)
    {
        for (int i = slots.Capacity.Count - 1; i >= keep; i--)
        {
            mesh.SurfaceRemove(i);
        }

        if (slots.Capacity.Count > keep)
        {
            slots.Capacity.RemoveRange(keep, slots.Capacity.Count - keep);
        }
    }

    private void Flush(ArrayMesh mesh, Material material, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list)
    {
        int n = list.Count;
        SurfaceSlots slots = SlotsOf(mesh);
        for (int i = 0; i < n; i++)
        {
            (Vector3 p, _, _, Vector2 off, _) = list[i];
            if (!slots.HasBounds)
            {
                slots.Bounds = new Aabb(p, Vector3.Zero);
                slots.HasBounds = true;
            }
            else
            {
                slots.Bounds = slots.Bounds.Expand(p);
            }

            // The corner offset is added along the camera's unit axes in the vertex shader
            // (fx_billboard.gdshaderinc), so a vertex lies within |off| of its centre whatever
            // the camera does; an axis-aligned quad is pre-expanded and carries a zero offset.
            float reach = off.Length();
            if (reach > slots.MaxOffset)
            {
                slots.MaxOffset = reach;
            }
        }

        if (!InPlaceLayout.Supported)
        {
            FlushBuild(mesh, material, list, n % 3 == 0 ? (n + FlushBucket - 1) / FlushBucket * FlushBucket : n);
            return;
        }

        if (n == 0)
        {
            return;
        }

        int slot = slots.Used++;

        if (slot < slots.Capacity.Count && slots.Capacity[slot] >= n)
        {
            WriteInPlace(mesh, slot, slots.Capacity[slot], list);
            mesh.SurfaceSetMaterial(slot, material);
            return;
        }

        // Outgrown or new: every surface from this slot on is rebuilt, since surfaces cannot be
        // replaced in the middle of a mesh.
        TrimSurfaces(mesh, slots, slot);
        int capacity = FlushBucket;
        while (capacity < n)
        {
            capacity *= 2;
        }

        FlushBuild(mesh, material, list, capacity);
        slots.Capacity.Add(capacity);
        InPlaceLayout.Learn(mesh, slot, capacity);
    }

    /// <summary>The byte layout the in-place write needs, learned from the first surface built.</summary>
    private static class InPlaceLayout
    {
        /// <summary>False once a surface was built with a layout the writer does not know.</summary>
        public static bool Supported = true;

        public static bool Known;
        public static int AttributeStride;
        public static int ColourOffset;
        public static int ColourBytes;
        public static int UvOffset;
        public static int Uv2Offset;
        public static int Custom0Offset;

        public static void Learn(ArrayMesh mesh, int surface, int capacity)
        {
            if (Known || !Supported)
            {
                return;
            }

            RenderingServer.ArrayFormat format = (RenderingServer.ArrayFormat)mesh.SurfaceGetFormat(surface);
            int vertexStride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride(format, capacity);
            AttributeStride = (int)RenderingServer.MeshSurfaceGetFormatAttributeStride(format, capacity);
            ColourOffset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.Color);
            UvOffset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.TexUV);
            Uv2Offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.TexUV2);
            Custom0Offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.Custom0);
            ColourBytes = UvOffset - ColourOffset;

            // Position as three floats, colour as RGBA8 or four floats, both UVs as two floats:
            // anything else (compressed attributes) keeps the rebuild-every-frame path.
            Supported = vertexStride == 12 &&
                (ColourBytes == 4 || ColourBytes == 16) &&
                Uv2Offset - UvOffset == 8 &&
                Custom0Offset - Uv2Offset == 8 &&
                AttributeStride == Custom0Offset + 16;
            Known = true;
            GD.Print($"[fx] in-place surface writes {(Supported ? "ON" : "OFF")}: vertex stride {vertexStride}, " +
                $"attribute stride {AttributeStride}, colour +{ColourOffset}/{ColourBytes}B, uv +{UvOffset}, uv2 +{Uv2Offset}, custom0 +{Custom0Offset}");
        }
    }

    private byte[] _vertexBytes = System.Array.Empty<byte>();
    private byte[] _attributeBytes = System.Array.Empty<byte>();

    private void WriteInPlace(ArrayMesh mesh, int surface, int capacity, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list)
    {
        int n = list.Count;
        int stride = InPlaceLayout.AttributeStride;
        int vertexBytes = capacity * 12;
        int attributeBytes = capacity * stride;
        if (_vertexBytes.Length < vertexBytes) _vertexBytes = new byte[vertexBytes];
        if (_attributeBytes.Length < attributeBytes) _attributeBytes = new byte[attributeBytes];

        Span<byte> vertices = _vertexBytes.AsSpan(0, vertexBytes);
        Span<byte> attributes = _attributeBytes.AsSpan(0, attributeBytes);
        int colourAt = InPlaceLayout.ColourOffset;
        bool colour8 = InPlaceLayout.ColourBytes == 4;
        int uvAt = InPlaceLayout.UvOffset;
        int uv2At = InPlaceLayout.Uv2Offset;
        int motionAt = InPlaceLayout.Custom0Offset;

        // The tail repeats the last vertex with no colour: zero-area triangles.
        Vector3 lastPos = list[n - 1].Pos;
        Vector2 lastUv = list[n - 1].Uv;
        for (int i = 0; i < capacity; i++)
        {
            Vector3 pos;
            Vector2 uv;
            Color col;
            Vector2 off;
            Vector4 motion;
            if (i < n)
            {
                (pos, uv, col, off, motion) = list[i];
            }
            else
            {
                pos = lastPos;
                uv = lastUv;
                col = new Color(0f, 0f, 0f, 0f);
                off = Vector2.Zero;
                motion = Vector4.Zero;
            }

            System.Runtime.InteropServices.MemoryMarshal.Write(vertices.Slice(i * 12, 12), in pos);
            Span<byte> a = attributes.Slice(i * stride, stride);
            if (colour8)
            {
                // Godot's own conversion: each channel ×255, clamped, truncated to a byte.
                a[colourAt + 0] = (byte)Math.Clamp(col.R * 255f, 0f, 255f);
                a[colourAt + 1] = (byte)Math.Clamp(col.G * 255f, 0f, 255f);
                a[colourAt + 2] = (byte)Math.Clamp(col.B * 255f, 0f, 255f);
                a[colourAt + 3] = (byte)Math.Clamp(col.A * 255f, 0f, 255f);
            }
            else
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(a.Slice(colourAt, 16), in col);
            }

            System.Runtime.InteropServices.MemoryMarshal.Write(a.Slice(uvAt, 8), in uv);
            System.Runtime.InteropServices.MemoryMarshal.Write(a.Slice(uv2At, 8), in off);
            System.Runtime.InteropServices.MemoryMarshal.Write(a.Slice(motionAt, 16), in motion);
        }

        Rid rid = mesh.GetRid();
        RenderingServer.MeshSurfaceUpdateVertexRegion(rid, surface, 0, (ReadOnlySpan<byte>)vertices);
        RenderingServer.MeshSurfaceUpdateAttributeRegion(rid, surface, 0, (ReadOnlySpan<byte>)attributes);
    }

    /// <summary>Builds one surface of <paramref name="padded"/> vertices from the batch.</summary>
    private void FlushBuild(ArrayMesh mesh, Material material, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list, int padded)
    {
        int n = list.Count;
        if (!FlushPool.TryGetValue(padded, out FlushArrays? arraysFor))
        {
            arraysFor = new FlushArrays
            {
                Positions = new Vector3[padded],
                Uvs = new Vector2[padded],
                Colours = new Color[padded],
                Offsets = new Vector2[padded],
                Motion = new float[padded * 4],
            };
            FlushPool[padded] = arraysFor;
        }

        Vector3[] positions = arraysFor.Positions;
        Vector2[] uvs = arraysFor.Uvs;
        Color[] colours = arraysFor.Colours;
        Vector2[] offsets = arraysFor.Offsets;
        float[] motions = arraysFor.Motion;
        for (int i = 0; i < n; i++)
        {
            (Vector3 pos, Vector2 uv, Color col, Vector2 off, Vector4 motion) = list[i];
            positions[i] = pos;
            uvs[i] = uv;
            colours[i] = col;
            offsets[i] = off;
            motions[(i * 4) + 0] = motion.X;
            motions[(i * 4) + 1] = motion.Y;
            motions[(i * 4) + 2] = motion.Z;
            motions[(i * 4) + 3] = motion.W;
        }

        if (padded > n && n > 0)
        {
            Vector3 lastPos = positions[n - 1];
            Vector2 lastUv = uvs[n - 1];
            var none = new Color(0f, 0f, 0f, 0f);
            for (int i = n; i < padded; i++)
            {
                positions[i] = lastPos;
                uvs[i] = lastUv;
                colours[i] = none;
                offsets[i] = Vector2.Zero;
                motions[(i * 4) + 0] = 0f;
                motions[(i * 4) + 1] = 0f;
                motions[(i * 4) + 2] = 0f;
                motions[(i * 4) + 3] = 0f;
            }
        }

        // Each packed-array Variant carries a finalizable Disposer registered in GodotSharp's
        // DisposablesTracker; left to the finalizer, all four survive their first GC. Disposed
        // here they die in gen0; the Array holds its own reference to the packed data.
        Variant vPositions = positions, vUvs = uvs, vColours = colours, vOffsets = offsets, vMotion = motions;
        Godot.Collections.Array arrays = _surfaceArrays;
        arrays[(int)Mesh.ArrayType.Vertex] = vPositions;
        arrays[(int)Mesh.ArrayType.TexUV] = vUvs;
        arrays[(int)Mesh.ArrayType.Color] = vColours;
        arrays[(int)Mesh.ArrayType.TexUV2] = vOffsets;
        arrays[(int)Mesh.ArrayType.Custom0] = vMotion;
        vPositions.Dispose();
        vUvs.Dispose();
        vColours.Dispose();
        vOffsets.Dispose();
        vMotion.Dispose();
        // The defaults for the last two build a fresh Array and Dictionary per call.
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, _noBlendShapes, _noLods, MotionFormat);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    private static readonly Godot.Collections.Array _surfaceArrays = MakeSurfaceArrays();

    /// <summary>CUSTOM0 as four floats: the motion the billboard shader draws between steps by.</summary>
    private static readonly Mesh.ArrayFormat MotionFormat =
        (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
    private static readonly Godot.Collections.Array<Godot.Collections.Array> _noBlendShapes = new();
    private static readonly Godot.Collections.Dictionary _noLods = new();

    /// <summary>Vertices per flush bucket: 16 quads.</summary>
    private const int FlushBucket = 96;

    private static Godot.Collections.Array MakeSurfaceArrays()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        return arrays;
    }

    private readonly Dictionary<DrawKey, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>> _batches = new();
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _ages = new();

    /// <summary>Each ribbon control point's last-step motion and rate class, beside <see cref="_pts"/>.</summary>
    private readonly List<Vector4> _motions = new();
    private readonly List<DrawKey> _keys = new();
    private static readonly Comparison<DrawKey> ByZOrder = (l, r) => l.ZOrder.CompareTo(r.ZOrder);
    private static readonly Comparison<(float D, int Q)> FarthestFirst = (l, r) => r.D.CompareTo(l.D);

    /// <summary>Sort scratch, shared: geometry is emitted on the main thread only.</summary>
    private static readonly List<(float D, int Q)> _order = new();
    private static readonly List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> _sorted = new();

    private static void SortQuadsBackToFront(List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> verts, Vector3 eye)
    {
        int quads = verts.Count / 6;
        if (quads < 2) return;
        List<(float D, int Q)> order = _order;
        order.Clear();
        for (int q = 0; q < quads; q++)
        {
            Vector3 c = 0.5f * (verts[q * 6].Pos + verts[(q * 6) + 1].Pos);
            order.Add(((c - eye).LengthSquared(), q));
        }

        order.Sort(FarthestFirst);
        List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> sorted = _sorted;
        sorted.Clear();
        foreach ((_, int q) in order) for (int k = 0; k < 6; k++) sorted.Add(verts[(q * 6) + k]);
        verts.Clear();
        verts.AddRange(sorted);
    }

    private void EmitMeshParticle(VisualRuntime va, VisualRuntime? vb, float w, ParticleState pt, float alpha, float[] col, Dictionary<string, MeshInstanceData> meshOut, Vector4 motion)
    {
        bool disp = va.BlendMode == "disp";
        string meshKey = va.MeshKey ??= disp ? va.MeshPath + "|disp" : va.MeshPath;
        if (!meshOut.TryGetValue(meshKey, out MeshInstanceData? data))
        {
            data = new MeshInstanceData { MeshPath = va.MeshPath, Displacement = disp };
            meshOut[meshKey] = data;
        }
        if (disp) Engine_FxDisplacement.NotifyActive();

        float ms = vb is not null && vb.IsMesh ? va.MeshScale + ((vb.MeshScale - va.MeshScale) * w) : va.MeshScale;
        float s = ms * pt.FlickX * _sizeScale;
        if (s <= 1e-5f) return;
        Vector3 axis = ToGodot(pt.Axis);
        if (axis.LengthSquared() < 1e-6f) axis = Vector3.Up;
        var basis = new Basis(axis.Normalized(), pt.Rot).Scaled(new Vector3(s, s, s));
        data.Instances.Add((new Transform3D(basis, ToGodot(pt.Pos)), new Color(col[0], col[1], col[2], alpha), motion));
    }

    private static void EmitBillboard(
        List<(Vector3, Vector2, Color, Vector2, Vector4)> sink, VisualRuntime vis, Vector3 centre, ParticleState pt, float hx, float hy,
        float[] col, float alpha, float tLife, Vector3 cameraRight, Vector3 cameraUp, Vector4 motion)
    {
        if (vis.Texture.Length == 0 || alpha <= 0.003f) return;
        float c = MathF.Cos(pt.Rot), s = MathF.Sin(pt.Rot);
        Vector3 right, up;
        if (vis.Aligned)
        {
            right = ToGodot(vis.AxisR);
            up = ToGodot(vis.AxisU);
        }
        else
        {
            right = (cameraRight * c) + (cameraUp * s);
            up = (-cameraRight * s) + (cameraUp * c);
        }

        int cols = Math.Max(1, vis.Cols), rows = Math.Max(1, vis.Rows), frames = cols * rows;
        float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;
        if (frames > 1)
        {
            int f = (int)pt.TexFrame[Math.Clamp(vis.TexSlot, 0, 3)];
            f = ((f % frames) + frames) % frames;
            int fx = f % cols, fy = f / cols;
            u0 = (float)fx / cols; u1 = (float)(fx + 1) / cols;
            v0 = (float)fy / rows; v1 = (float)(fy + 1) / rows;
        }

        var colr = new Color(col[0], col[1], col[2], alpha);

        // A camera-facing quad is emitted as its centre plus corner offsets, and the vertex
        // shader (`fx_billboard.gdshaderinc`) does the facing. The offsets are in the camera's
        // right/up axes, which the shader receives as instance uniforms in this node's space, so
        // camera rotation does not change this geometry. Particle roll is folded in here because
        // it does not depend on the camera.
        //
        // An axis-aligned visual is not camera-facing: its quad is built out here and carries a
        // zero offset.
        if (vis.Aligned)
        {
            Vector3 arx = right * hx, auy = up * hy;
            Vector3 aa = centre - arx - auy, ab = centre + arx - auy;
            Vector3 ac = centre - arx + auy, ad = centre + arx + auy;
            sink.Add((aa, new Vector2(u0, v1), colr, Vector2.Zero, motion));
            sink.Add((ad, new Vector2(u1, v0), colr, Vector2.Zero, motion));
            sink.Add((ab, new Vector2(u1, v1), colr, Vector2.Zero, motion));
            sink.Add((aa, new Vector2(u0, v1), colr, Vector2.Zero, motion));
            sink.Add((ac, new Vector2(u0, v0), colr, Vector2.Zero, motion));
            sink.Add((ad, new Vector2(u1, v0), colr, Vector2.Zero, motion));
            return;
        }

        // The same four corners, expressed in the rotated right/up frame: (±hx, ±hy) turned by
        // the particle's own roll. `c` and `s` are that roll, already computed above.
        var oA = new Vector2((-hx * c) + (hy * s), (-hx * s) - (hy * c));
        var oB = new Vector2((hx * c) + (hy * s), (hx * s) - (hy * c));
        var oC = new Vector2((-hx * c) - (hy * s), (-hx * s) + (hy * c));
        var oD = new Vector2((hx * c) - (hy * s), (hx * s) + (hy * c));
        sink.Add((centre, new Vector2(u0, v1), colr, oA, motion));
        sink.Add((centre, new Vector2(u1, v0), colr, oD, motion));
        sink.Add((centre, new Vector2(u1, v1), colr, oB, motion));
        sink.Add((centre, new Vector2(u0, v1), colr, oA, motion));
        sink.Add((centre, new Vector2(u0, v0), colr, oC, motion));
        sink.Add((centre, new Vector2(u1, v0), colr, oD, motion));
    }

    private readonly float[] _emitCol = new float[3];

    private System.Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>>? _sink;

    /// <summary>The batch a visual's quads go in, created on first sight and pooled thereafter.</summary>
    private List<(Vector3, Vector2, Color, Vector2, Vector4)> SinkFor(VisualRuntime v, int z)
    {
        var key = new DrawKey(v.Texture, v.BlendMode, v.SatMask, v.SoftDepth, z);
        if (!_batches.TryGetValue(key, out List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>? list))
        {
            list = new List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>();
            _batches[key] = list;
        }

        return list;
    }

    private float[] _ribAttrW = System.Array.Empty<float>();
    private float[] _ribAttrA = System.Array.Empty<float>();
    private Vector3[] _ribAttrC = System.Array.Empty<Vector3>();
    private int[] _ribAttrTex = System.Array.Empty<int>();
    private Vector3[] _ribP = System.Array.Empty<Vector3>();
    private Vector3[] _ribSide = System.Array.Empty<Vector3>();
    private Vector3[] _ribC = System.Array.Empty<Vector3>();
    private float[] _ribA = System.Array.Empty<float>();
    private int[] _ribTex = System.Array.Empty<int>();
    private Vector4[] _ribM = System.Array.Empty<Vector4>();

    private static float[] RentF(ref float[] slot, int n)
    {
        if (slot.Length < n) slot = new float[n];
        return slot;
    }

    private static int[] RentI(ref int[] slot, int n)
    {
        if (slot.Length < n) slot = new int[n];
        return slot;
    }

    private static Vector3[] RentV(ref Vector3[] slot, int n)
    {
        if (slot.Length < n) slot = new Vector3[n];
        return slot;
    }

    private void EmitRibbon(
        Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>> sink, EmitterRuntime rt, List<VisualRuntime> vis,
        List<Vector3> pts, List<float> ages, List<Vector4> motions, Vector3 eye, bool constWidth, float[] lit, float intensity)
    {
        int n = pts.Count;
        if (n < 2 || vis.Count == 0) return;
        int nvis = vis.Count;

        // Pooled, not per call: grown to the high-water mark and reused; only the first
        // `n`/`ribs` entries are ever read.
        float[] attrW = RentF(ref _ribAttrW, n);
        float[] attrA = RentF(ref _ribAttrA, n);
        Vector3[] attrC = RentV(ref _ribAttrC, n);
        int[] attrTex = RentI(ref _ribAttrTex, n);
        for (int k = 0; k < n; k++)
        {
            float age = ages[k];
            int seg = 1;
            while (seg < nvis && age >= vis[seg].DelayFrames) seg++;
            int sb = nvis <= 1 ? 0 : Math.Min(seg, nvis - 1);
            int sa = Math.Max(sb - 1, 0);
            VisualRuntime va = vis[sa], vb = vis[sb];
            float w = sb > sa ? Math.Clamp((age - va.DelayFrames) * vb.InvSpan, 0f, 1f) : 0f;
            attrW[k] = (constWidth ? vis[0].TrailWidth * 0.5f : (va.TrailWidth + ((vb.TrailWidth - va.TrailWidth) * w)) * 0.5f) * _sizeScale;
            float si = va.SelfIllum + ((vb.SelfIllum - va.SelfIllum) * w);
            attrC[k] = new Vector3(
                lit[0] + ((va.Colour[0] + ((vb.Colour[0] - va.Colour[0]) * w) - lit[0]) * si),
                lit[1] + ((va.Colour[1] + ((vb.Colour[1] - va.Colour[1]) * w) - lit[1]) * si),
                lit[2] + ((va.Colour[2] + ((vb.Colour[2] - va.Colour[2]) * w) - lit[2]) * si));
            attrA[k] = (va.Alpha + ((vb.Alpha - va.Alpha) * w)) * DistanceFadeFactor(rt, pts[k], eye) * intensity;
            attrTex[k] = sa;
        }

        int nseg = Math.Max(1, vis[0].TrailLen);
        Vector3 pre = pts[0] - (pts[1] - pts[0]);
        Vector3 post = pts[n - 1] + (pts[n - 1] - pts[n - 2]);
        Vector3 Ctrl(int i) => i < 0 ? pre : i >= n ? post : pts[i];
        int ribs = ((n - 1) * nseg) + 1;
        Vector3[] rp = RentV(ref _ribP, ribs);
        Vector3[] rside = RentV(ref _ribSide, ribs);
        Vector3[] rc = RentV(ref _ribC, ribs);
        float[] ra = RentF(ref _ribA, ribs);
        int[] rtex = RentI(ref _ribTex, ribs);
        Vector4[] rm = _ribM.Length >= ribs ? _ribM : (_ribM = new Vector4[ribs]);
        for (int j = 0; j < ribs; j++)
        {
            int span = Math.Min(j / nseg, n - 2);
            float sfrac = (float)(j - (span * nseg)) / nseg;
            if (j == ribs - 1) { span = n - 2; sfrac = 1f; }
            Vector3 p0 = Ctrl(span - 1), p1 = Ctrl(span), p2 = Ctrl(span + 1), p3 = Ctrl(span + 2);
            Vector3 c1 = 0.5f * (p2 - p0);
            Vector3 c2 = p0 - (2.5f * p1) + (2f * p2) - (0.5f * p3);
            Vector3 c3 = (-0.5f * p0) + (1.5f * p1) - (1.5f * p2) + (0.5f * p3);
            Vector3 pos = p1 + (c1 * sfrac) + (c2 * sfrac * sfrac) + (c3 * sfrac * sfrac * sfrac);
            Vector3 tan = c1 + (2f * c2 * sfrac) + (3f * c3 * sfrac * sfrac);
            if (tan.LengthSquared() < 1e-12f) tan = pts[span + 1] - pts[span];
            tan = tan.LengthSquared() > 1e-12f ? tan.Normalized() : Vector3.Up;
            Vector3 side = tan.Cross(eye - pos);
            side = side.LengthSquared() > 1e-12f ? side.Normalized() : Vector3.Up;
            int a1 = Math.Min(span + 1, n - 1);
            float wj = attrW[span] + ((attrW[a1] - attrW[span]) * sfrac);
            rp[j] = pos; rside[j] = side * wj;
            rc[j] = attrC[span] + ((attrC[a1] - attrC[span]) * sfrac);
            ra[j] = attrA[span] + ((attrA[a1] - attrA[span]) * sfrac);
            rtex[j] = attrTex[span];
            rm[j] = motions[span].Lerp(motions[a1], sfrac);
        }

        for (int j = 0; j + 1 < ribs; j++)
        {
            if (ra[j] <= 0.003f && ra[j + 1] <= 0.003f) continue;
            VisualRuntime tv = vis[rtex[j]];
            if (tv.Texture.Length == 0) continue;
            List<(Vector3, Vector2, Color, Vector2, Vector4)> list = sink(tv, rt.ZOrder);
            float u0 = (float)j / (ribs - 1), u1 = (float)(j + 1) / (ribs - 1);
            var c0 = new Color(rc[j].X, rc[j].Y, rc[j].Z, MathF.Max(ra[j], 0f));
            var c1 = new Color(rc[j + 1].X, rc[j + 1].Y, rc[j + 1].Z, MathF.Max(ra[j + 1], 0f));
            list.Add((rp[j] + rside[j], new Vector2(u0, 0f), c0, Vector2.Zero, rm[j]));
            list.Add((rp[j + 1] + rside[j + 1], new Vector2(u1, 0f), c1, Vector2.Zero, rm[j + 1]));
            list.Add((rp[j] - rside[j], new Vector2(u0, 1f), c0, Vector2.Zero, rm[j]));
            list.Add((rp[j] - rside[j], new Vector2(u0, 1f), c0, Vector2.Zero, rm[j]));
            list.Add((rp[j + 1] + rside[j + 1], new Vector2(u1, 0f), c1, Vector2.Zero, rm[j + 1]));
            list.Add((rp[j + 1] - rside[j + 1], new Vector2(u1, 1f), c1, Vector2.Zero, rm[j + 1]));
        }
    }
}

public sealed class VisualBatchMaterial
{
    public required Material Material { get; init; }
}
