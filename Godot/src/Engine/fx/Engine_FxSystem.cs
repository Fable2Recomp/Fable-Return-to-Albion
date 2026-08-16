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
        public float[] Vel = new float[3];
        public float Age;
        public float Life;
        public int Seg = 1;
        public int LoopIter;
        public float Rot;
        public float Spin;
        public float Seed;
        public float FlickX = 1f;
        public float FlickY = 1f;
        public int FlickFrame;
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
        public bool Additive;
        public bool SrcAlpha;
        public float[] Colour = { 1f, 1f, 1f };
        public float Alpha = 1f;
        public float HalfX = 0.5f;
        public float HalfY = 0.5f;
        public float GlobalScaleX = 1f;
        public float GlobalScaleY = 1f;
        public float Delay;
        public int DelayFrames;
        public float InvSpan;

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
        public float Time;
        public float DtAccum;
        public int Frame;
        public float SharedRand = -1f;
        public List<EmitterRuntime> Emitters { get; } = new();
        public List<AttractorRuntime> Attractors { get; } = new();
        public List<LightRuntime> Lights { get; } = new();
    }

    private readonly List<InstanceState> _instances = new();
    private readonly List<(Vector3 Pos, Vector2 Uv, Color Col)> _scratch = new();
    private uint _rng = 0x1234567u;
    private bool _loop = true;
    private bool _distanceFade = false;
    private Transform3D? _previousGlobal;
    private bool _detachedParticles;

    public int ResolvedCount { get; private set; }
    public bool IsEmpty => _instances.Count == 0;
    public void SetLoop(bool loop) => _loop = loop;
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

    private static void MatMul(float[] a, float[] b, float[] o)
    {
        var t = new float[9];
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
        static float[] Lin(float[] a, float[] b, float ka, float kb) => new[] { (a[0] * ka) + (b[0] * kb), (a[1] * ka) + (b[1] * kb), (a[2] * ka) + (b[2] * kb) };
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
            if (i == 0) { P1 = p[0]; P2 = p[1]; P0 = Lin(p[0], p[1], 2, -1); P3 = n == 2 ? Lin(p[1], p[0], 2, -1) : p[2]; }
            else if (i == n - 2) { P1 = p[i]; P2 = p[i + 1]; P0 = p[i - 1]; P3 = Lin(p[i + 1], p[i], 2, -1); }
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

    private static bool IsAdditive(FxVisual vis) => vis.DstBlend == 1;

    private static bool UsesSourceAlpha(FxVisual vis) => vis.SrcBlend == 4;

    private void UpdateChain(List<ChainNode> chain, InstanceState inst, float[] placePos, float[] placeRot, float scale, float[] outPos, float[] outRot)
    {
        var R = new float[9];
        Array.Copy(placeRot, R, 9);
        var t = new[] { placePos[0], placePos[1], placePos[2] };
        var lp = new float[3];
        var lps = new float[3];
        var w = new float[3];
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
                        node.Angle += d.AngularRotationRate;
                        if (node.Angle >= TwoPi) node.Angle -= TwoPi;
                        if (node.Angle <= -TwoPi) node.Angle += TwoPi;
                        var zax = new float[3];
                        var yax = new float[3];
                        MatVec(node.LocalRot, new[] { 0f, 0f, 1f }, zax);
                        MatVec(node.LocalRot, new[] { 0f, 1f, 0f }, yax);
                        float radius = d.Generic[25].EvalFrame(inst.Frame);
                        var basePt = new[] { node.Pos[0] + (yax[0] * radius), node.Pos[1] + (yax[1] * radius), node.Pos[2] + (yax[2] * radius) };
                        var Ra = new float[9];
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
                        node.T += d.SplineRate * node.Dir;
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

    private VisualRuntime? BuildVisual(FxVisual vis, float sc, float[] rotW)
    {
        if (vis.Displacement) return null;
        if (vis.Texture.Length == 0 && !vis.HasMesh) return null;
        var v = new VisualRuntime
        {
            Delay = vis.Delay,
            DelayFrames = vis.DelayFrames,
            InvSpan = vis.InvSpan,
            Colour = new[] { vis.ColorSq[0], vis.ColorSq[1], vis.ColorSq[2] },
            Alpha = vis.HasAlpha ? vis.Alpha : 1f,
            Additive = IsAdditive(vis),
            SrcAlpha = UsesSourceAlpha(vis),
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
            Array.Copy(placeRot, inst.PlaceRot, 9);
            inst.Scale = sc;

            FxEffect? eff = bank.FindByHash(p.EffectHash);
            if (eff is null) continue;

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
                    if (sysd.ChildCount < 2) { chain.Add(node); continue; }
                    rt.LifeFrames = sysd.LastStartFrames;
                    foreach (FxVisual vis in sysd.Visuals)
                    {
                        VisualRuntime? v = BuildVisual(vis, sc, rt.RotW);
                        if (v is not null) rt.Visuals.Add(v);
                    }
                }

                if (em.ReleaseTrail && part.TrailSystem >= 0 && part.TrailSystem < bank.Systems.Count)
                {
                    FxSystemDef tsys = bank.Systems[part.TrailSystem];
                    if (tsys.Mode == 2 && tsys.ChildCount >= 2)
                    {
                        foreach (FxVisual vis in tsys.Visuals)
                        {
                            VisualRuntime? v = BuildVisual(vis, sc, rt.RotW);
                            if (v is not null) rt.TrailVisuals.Add(v);
                        }

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

    private void ComputeDir(EmitterRuntime rt, float[] o)
    {
        FxEmitter d = rt.Def!;
        int f = (int)rt.Age;
        float phi = Frand() * TwoPi;
        var dir = new[] { d.Generic[9].EvalFrame(f) * MathF.Cos(phi), d.Generic[10].EvalFrame(f) * MathF.Sin(phi), d.Generic[11].EvalFrame(f) };
        float l2 = (dir[0] * dir[0]) + (dir[1] * dir[1]) + (dir[2] * dir[2]);
        if (l2 >= 1.19209290e-7f) VNorm(dir, 1e-4f);
        else { dir[0] = 0; dir[1] = 0; dir[2] = 1; }
        float r = Frand();
        if (dir[2] <= 0.9995f)
        {
            float z = Math.Clamp(dir[2], -1f, 1f);
            float theta = MathF.Acos(z) * r;
            var perp = new[] { -dir[0] * z, -dir[1] * z, 1f - (z * z) };
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
        pt.Vel[0] += rt.WindDisp[0]; pt.Vel[1] += rt.WindDisp[1]; pt.Vel[2] += rt.WindDisp[2];
        if (hasAttractors)
        {
            foreach (int ai in rt.Attractors)
            {
                AttractorRuntime at = inst.Attractors[ai];
                FxEmitter ad = at.Def!;
                int af = (int)at.Age;
                float rad = ad.Generic[26].EvalFrame(af);
                var dv = new[] { pt.Pos[0] - at.WorldPos[0], pt.Pos[1] - at.WorldPos[1], pt.Pos[2] - at.WorldPos[2] };
                float d2 = (dv[0] * dv[0]) + (dv[1] * dv[1]) + (dv[2] * dv[2]);
                if (rad > 0f && d2 <= rad * rad)
                {
                    float dist = MathF.Sqrt(d2);
                    float s = ad.Generic[27].EvalFrame(af) * (1f - (dist / rad));
                    if (dist < 1e-8f) { dv[0] = Srand2(); dv[1] = Srand2(); dv[2] = Srand2(); }
                    if (VNorm(dv, 1e-4f))
                    {
                        float k = -s * Dt * Dt;
                        pt.Vel[0] += dv[0] * k; pt.Vel[1] += dv[1] * k; pt.Vel[2] += dv[2] * k;
                    }
                }
            }
        }

        pt.Vel[0] *= rt.VelMul[0]; pt.Vel[1] *= rt.VelMul[1]; pt.Vel[2] *= rt.VelMul[2];
        if (hasP14)
        {
            FxEmitter d = rt.Def!;
            int pf = (int)pt.Age;
            float k = 1f - (1f - d.Generic[14].EvalFrame(pf));
            if (hasVec0) k *= k;
            (float v1x, float v1y, float v1z) = d.Vec1.EvalFrame(pf);
            pt.Vel[0] *= v1x * k; pt.Vel[1] *= v1y * k; pt.Vel[2] *= v1z * k;
        }

        pt.Pos[0] += pt.Vel[0]; pt.Pos[1] += pt.Vel[1]; pt.Pos[2] += pt.Vel[2];
    }

    private void SpawnOne(InstanceState inst, EmitterRuntime rt, float frac)
    {
        FxEmitter d = rt.Def!;
        var pt = new ParticleState { Age = 0f, Seg = 1, LoopIter = 0 };
        var off = new float[3];
        var dirw = new float[3];
        bool dirFromShape = false;
        if (d.SubType == 1 && d.SpawnRadius > 0f)
        {
            float z = (Frand() * 2f) - 1f;
            float phi = Frand() * TwoPi;
            float s = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
            var o = new[] { MathF.Cos(phi) * s, MathF.Sin(phi) * s, z };
            float r = d.ShapeFlagA ? d.SpawnRadius : Frand() * d.SpawnRadius;
            o[0] *= r; o[1] *= r; o[2] *= r;
            MatVec(rt.RotW, o, off);
            if (d.ShapeFlagB) { var od = new[] { off[0], off[1], off[2] }; if (VNorm(od, 1e-4f)) { dirw = od; dirFromShape = true; } }
        }
        else if (d.SubType == 2 && d.SpawnRadius > 0f)
        {
            float phi = Frand() * TwoPi;
            var o = new[] { MathF.Cos(phi), MathF.Sin(phi), 0f };
            float r = d.ShapeFlagA ? d.SpawnRadius : Frand() * d.SpawnRadius;
            o[0] *= r; o[1] *= r;
            MatVec(rt.RotW, o, off);
            if (d.ShapeFlagB) { var od = new[] { off[0], off[1], off[2] }; if (VNorm(od, 1e-4f)) { dirw = od; dirFromShape = true; } }
        }

        if (!dirFromShape) ComputeDir(rt, dirw);

        for (int k = 0; k < 3; k++) pt.Pos[k] = (off[k] * rt.Scale) + ((rt.PosW[k] - rt.PrevPos[k]) * frac) + rt.PrevPos[k];

        int f = (int)rt.Age;
        float speed = d.Generic[4].EvalFrame(f) + (Frand() * d.Generic[5].EvalFrame(f));
        pt.Vel[0] = dirw[0] * speed * Dt;
        pt.Vel[1] = dirw[1] * speed * Dt;
        pt.Vel[2] = dirw[2] * speed * Dt;

        bool hasAttr = rt.Attractors.Count != 0;
        bool hasP14 = !d.Generic[14].IsEmpty;
        bool hasVec0 = !d.Vec0.IsEmpty;
        StepParticle(inst, rt, pt, hasAttr, hasP14, hasVec0);
        pt.Life = rt.LoopCount == -1 ? (d.UseLife ? d.LifetimeFrames : -1f) : rt.LifeFrames;

        VisualRuntime? v0 = rt.Visuals.Count == 0 ? null : rt.Visuals[0];
        if (v0 is not null && v0.IsMesh)
        {
            pt.Rot = v0.OrRandAngle ? Frand() * TwoPi : 0f;
            pt.Spin = v0.OrSpeed + ((Frand() - 0.5f) * v0.OrSpread);
            if (v0.OrRandAxis)
            {
                float ax, ay, az, l2;
                do { ax = Srand2(); ay = Srand2(); az = Srand2(); l2 = (ax * ax) + (ay * ay) + (az * az); } while (l2 > 1f || l2 < 1e-4f);
                float il = 1f / MathF.Sqrt(l2);
                pt.Axis[0] = ax * il; pt.Axis[1] = ay * il; pt.Axis[2] = az * il;
            }
            else
            {
                var al = new[] { v0.OrAxis[0], v0.OrAxis[1], v0.OrAxis[2] };
                if (!VNorm(al, 1e-4f)) { al[0] = 0; al[1] = 0; al[2] = 1; }
                MatVec(rt.RotW, al, pt.Axis);
            }
        }
        else if (v0 is not null && v0.HasRotation)
        {
            pt.Rot = v0.RotInitialRandom ? Frand() * TwoPi : 0f;
            pt.Spin = v0.RotSpeed + ((Frand() - 0.5f) * v0.RotSpread);
        }

        pt.Seed = Frand();
        pt.FlickX = 1f; pt.FlickY = 1f;
        if (v0 is not null && v0.IsMesh && v0.HasRandScale) pt.FlickX = v0.RsMin + (Frand() * v0.RsRange);
        else if (v0 is not null && v0.HasFlicker)
        {
            float r1 = Frand();
            pt.FlickX = v0.FlickerBaseX + (r1 * v0.FlickerSpreadX);
            float r2 = v0.FlickerShared ? r1 : Frand();
            pt.FlickY = v0.FlickerBaseY + (r2 * v0.FlickerSpreadY);
        }

        pt.FlickFrame = 0;
        pt.HistCount = 0;
        if (rt.Trail)
        {
            pt.Hist[0] = new[] { pt.Pos[0], pt.Pos[1], pt.Pos[2] };
            pt.HistCount = 1;
        }

        rt.Particles.Add(pt);
        TotalSpawned++;
    }

    private void BeamRandomOffset(EmitterRuntime rt, float[] o)
    {
        FxEmitter d = rt.Def!;
        var dir = new float[3];
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

    private void BeamTick(EmitterRuntime rt)
    {
        FxEmitter d = rt.Def!;
        int n = rt.BeamPts.Count;
        if (n < 2) return;
        if (rt.BeamJerkTimer > 0f) rt.BeamJerkTimer -= Dt;
        switch (d.AnimationMode)
        {
            case 1:
                if (rt.BeamJerkTimer <= 0f)
                {
                    for (int i = 1; i <= n - 2; i++)
                    {
                        var off = new float[3];
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
                    var dv = new[] { rt.BeamBase[i][0] - rt.BeamPts[i][0], rt.BeamBase[i][1] - rt.BeamPts[i][1], rt.BeamBase[i][2] - rt.BeamPts[i][2] };
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

        while (rt.Particles.Count > n) rt.Particles.RemoveAt(rt.Particles.Count - 1);
        while (rt.Particles.Count < n)
        {
            var pt = new ParticleState { Seg = 1, Age = 0f, Seed = Frand() };
            pt.Life = rt.LoopCount == -1 ? (d.UseLife ? d.LifetimeFrames : -1f) : rt.LifeFrames;
            VisualRuntime? v0 = rt.Visuals.Count == 0 ? null : rt.Visuals[0];
            if (v0 is not null && v0.HasRotation) { pt.Rot = v0.RotInitialRandom ? Frand() * TwoPi : 0f; pt.Spin = v0.RotSpeed + ((Frand() - 0.5f) * v0.RotSpread); }
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

    private void Tick(InstanceState inst)
    {
        inst.Frame++;
        if (inst.SharedRand < 0f) inst.SharedRand = Frand();

        var rotTmp = new float[9];
        foreach (AttractorRuntime at in inst.Attractors)
        {
            at.Age += 1f;
            UpdateChain(at.Chain, inst, inst.PlacePos, inst.PlaceRot, inst.Scale, at.WorldPos, rotTmp);
        }

        foreach (LightRuntime lt in inst.Lights)
        {
            lt.Age += 1f;
            UpdateChain(lt.Chain, inst, inst.PlacePos, inst.PlaceRot, inst.Scale, lt.WorldPos, rotTmp);
        }

        var v0l = new float[3];
        var v0w = new float[3];
        foreach (EmitterRuntime rt in inst.Emitters)
        {
            FxEmitter d = rt.Def!;
            rt.Age += 1f;
            int f = (int)rt.Age;

            Array.Copy(rt.PosW, rt.PrevPos, 3);
            UpdateChain(rt.Chain, inst, rt.PlacePos, rt.PlaceRot, rt.Scale, rt.PosW, rt.RotW);
            if (rt.FirstFrame) Array.Copy(rt.PosW, rt.PrevPos, 3);

            (float v1x, float v1y, float v1z) = d.Vec1.EvalFrame(f);
            float m12 = d.Generic[12].EvalFrame(f);
            float m13 = d.Generic[13].EvalFrame(f);
            rt.VelMul[0] = m13 * v1x * m12; rt.VelMul[1] = m13 * v1y * m12; rt.VelMul[2] = m13 * v1z * m12;

            (v0l[0], v0l[1], v0l[2]) = d.Vec0.EvalFrame(f);
            MatVec(rt.RotW, v0l, v0w);
            float g6 = d.Generic[6].EvalFrame(f);
            float g7 = d.Generic[7].EvalFrame(f);
            var acc = new[] { v0w[0] * g6, v0w[1] * g6, (v0w[2] * g6) + (Gravity * g7) };
            for (int k = 0; k < 3; k++) rt.WindDisp[k] = acc[k] * rt.Scale * Dt * Dt;

            bool windowOpen = !d.UseLife || rt.Age <= d.LifetimeFrames;
            int nvis = rt.Visuals.Count;
            if (rt.Beam)
            {
                BeamTick(rt);
                rt.FirstFrame = false;
                foreach (ParticleState pt in rt.Particles)
                {
                    pt.Age += 1f;
                    while (pt.Seg < nvis && pt.Age >= (int)rt.Visuals[pt.Seg].DelayFrames + 0.5f) pt.Seg++;
                }

                rt.Particles.RemoveAll(p => p.Life >= 0f && p.Age >= p.Life);
                continue;
            }

            if (windowOpen)
            {
                float rate = d.Generic[2].EvalFrame(f) + (Frand() * d.Generic[3].EvalFrame(f));
                rt.Accum += MathF.Max(rate, 0f);
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
            foreach (ParticleState pt in rt.Particles)
            {
                if (!rt.Frozen) StepParticle(inst, rt, pt, hasAttr, hasP14, hasVec0);
                pt.Rot += pt.Spin;
                if (rt.Trail)
                {
                    int hn = Math.Min(pt.HistCount + 1, TrailHistory);
                    for (int k = hn - 1; k > 0; k--) pt.Hist[k] = pt.Hist[k - 1];
                    pt.Hist[0] = new[] { pt.Pos[0], pt.Pos[1], pt.Pos[2] };
                    pt.HistCount = hn;
                }

                pt.Age += 1f;
                if (rt.LoopCount != 0 && rt.LoopEnd <= pt.Age)
                {
                    if (rt.LoopCount < 0 || pt.LoopIter < rt.LoopCount) { pt.Age = rt.LoopStart; pt.Seg = 1; pt.LoopIter++; }
                    else pt.LoopIter = 0;
                }

                bool crossed = false;
                while (pt.Seg < nvis && pt.Age >= (int)rt.Visuals[pt.Seg].DelayFrames + 0.5f) { pt.Seg++; crossed = true; }
                if (crossed && nvis != 0)
                {
                    VisualRuntime vv = rt.Visuals[Math.Min(pt.Seg, nvis - 1)];
                    if (vv.IsMesh) pt.Spin = vv.OrSpeed + ((Frand() - 0.5f) * vv.OrSpread);
                    else if (vv.HasRotation) pt.Spin = vv.RotSpeed + ((Frand() - 0.5f) * vv.RotSpread);
                }

                if (nvis != 0)
                {
                    VisualRuntime vv = rt.Visuals[Math.Min(Math.Max(pt.Seg - 1, 0), nvis - 1)];
                    pt.FlickFrame++;
                    if (vv.IsMesh)
                    {
                        if (vv.HasRandScale && vv.RsFrames > 0 && pt.FlickFrame % vv.RsFrames == 0) pt.FlickX = vv.RsMin + (Frand() * vv.RsRange);
                    }
                    else if (vv.HasFlicker && vv.FlickerFrames > 0 && pt.FlickFrame % vv.FlickerFrames == 0)
                    {
                        float r1 = Frand();
                        pt.FlickX = vv.FlickerBaseX + (r1 * vv.FlickerSpreadX);
                        float r2 = vv.FlickerShared ? r1 : Frand();
                        pt.FlickY = vv.FlickerBaseY + (r2 * vv.FlickerSpreadY);
                    }
                }
            }

            rt.Particles.RemoveAll(p => p.Life >= 0f && p.Age >= p.Life);
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

    public void Update(float dt, Transform3D? nodeGlobal = null)
    {
        if (nodeGlobal is { } current)
        {
            if (_previousGlobal is { } previous && _detachedParticles)
            {
                Transform3D delta = current.AffineInverse() * previous;
                if (!delta.IsEqualApprox(Transform3D.Identity)) Rebase(delta);
            }

            _previousGlobal = current;
        }

        if (dt <= 0f) dt = 1f / 60f;
        dt = MathF.Min(dt, 0.25f);
        foreach (InstanceState inst in _instances)
        {
            inst.Time += dt;
            inst.DtAccum += dt;
            int steps = 0;
            while (inst.DtAccum >= FrameDt && steps < 8)
            {
                inst.DtAccum -= FrameDt;
                Tick(inst);
                steps++;
            }

            if (steps == 8) inst.DtAccum = 0f;
        }
    }

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

    public readonly record struct FxLightState(Vector3 Position, Color Colour, float Range, float Energy, float FadeDistance, float FadeRange);

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
                int f = (int)light.Age;
                (float r, float g, float b) = d.LightColour.EvalFrame(f);
                float energy = d.LightIntensity.EvalFrame(f);
                return new FxLightState(
                    ToGodot(light.WorldPos),
                    new Color(r, g, b),
                    MathF.Max(0f, d.LightRange.EvalFrame(f)) * light.Scale,
                    energy,
                    d.LightFadeDistance.EvalFrame(f),
                    d.LightFadeRange.EvalFrame(f));
            }
        }

        return default;
    }

    public sealed class MeshInstanceData
    {
        public string MeshPath = string.Empty;
        public List<(Transform3D Transform, Color Colour)> Instances { get; } = new();
    }

    private readonly struct DrawKey : IEquatable<DrawKey>
    {
        public readonly string Texture; public readonly bool Additive; public readonly bool SrcAlpha; public readonly int ZOrder;
        public DrawKey(string t, bool a, bool s, int z) { Texture = t; Additive = a; SrcAlpha = s; ZOrder = z; }
        public bool Equals(DrawKey o) => Texture == o.Texture && Additive == o.Additive && SrcAlpha == o.SrcAlpha && ZOrder == o.ZOrder;
        public override bool Equals(object? o) => o is DrawKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Texture, Additive, SrcAlpha, ZOrder);
    }

    private float DistanceFadeFactor(EmitterRuntime rt, Vector3 p, Vector3 cam)
    {
        if (!_distanceFade || rt.Def is null) return 1f;
        float dist = (cam - p).Length();
        float[] fade = rt.Fade;
        float fi = fade[1] > fade[0] ? Math.Clamp((dist - fade[0]) / (fade[1] - fade[0]), 0f, 1f) : (dist >= fade[1] ? 1f : 0f);
        float fo = fade[3] > fade[2] ? Math.Clamp((fade[3] - dist) / (fade[3] - fade[2]), 0f, 1f) : (dist <= fade[3] ? 1f : 0f);
        return fi * fo;
    }

    public void EmitGeometry(
        ImmediateMesh mesh,
        Func<string, VisualBatchMaterial?> materialLookup,
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Dictionary<string, MeshInstanceData> meshOut)
    {
        mesh.ClearSurfaces();
        foreach (MeshInstanceData data in meshOut.Values) data.Instances.Clear();

        var batches = new Dictionary<DrawKey, List<(Vector3 Pos, Vector2 Uv, Color Col)>>();
        List<(Vector3, Vector2, Color)> Sink(VisualRuntime v, int z)
        {
            var key = new DrawKey(v.Texture, v.Additive, v.SrcAlpha, z);
            if (!batches.TryGetValue(key, out var list)) { list = new List<(Vector3, Vector2, Color)>(); batches[key] = list; }
            return list;
        }

        var pts = new List<Vector3>();
        var ages = new List<float>();
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                int nvis = rt.Visuals.Count;
                if (rt.Trail && rt.TrailVisuals.Count != 0)
                {
                    foreach (ParticleState pt in rt.Particles)
                    {
                        pts.Clear(); ages.Clear();
                        for (int k = 0; k < pt.HistCount; k++)
                        {
                            if (rt.TrailLife > 0f && k >= rt.TrailLife) break;
                            float[]? h = pt.Hist[k];
                            if (h is null) break;
                            pts.Add(ToGodot(h)); ages.Add(k);
                        }

                        EmitRibbon(Sink, rt, rt.TrailVisuals, pts, ages, cameraPosition, false);
                    }
                }

                if (rt.Ribbon)
                {
                    pts.Clear(); ages.Clear();
                    foreach (ParticleState pt in rt.Particles) { pts.Add(ToGodot(pt.Pos)); ages.Add(pt.Age); }
                    EmitRibbon(Sink, rt, rt.Visuals, pts, ages, cameraPosition, true);
                    continue;
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    if (nvis == 0) continue;
                    Vector3 ppos = ToGodot(pt.Pos);
                    float tLife = pt.Life > 0f ? Math.Clamp(pt.Age / pt.Life, 0f, 1f) : 0f;
                    int segB = nvis <= 1 ? 0 : Math.Clamp(pt.Seg, 1, nvis - 1);
                    int segA = Math.Max(segB - 1, 0);
                    VisualRuntime va = rt.Visuals[segA];
                    VisualRuntime? vb = nvis > 1 && segB > segA ? rt.Visuals[segB] : null;
                    float w = 0f;
                    if (vb is not null) w = Math.Clamp((pt.Age - va.DelayFrames) * vb.InvSpan, 0f, 1f);
                    float Lerp(float x, float y) => x + ((y - x) * w);

                    float hx = (vb is not null ? Lerp(va.HalfX, vb.HalfX) : va.HalfX) * pt.FlickX * (vb is not null ? Lerp(va.GlobalScaleX, vb.GlobalScaleX) : va.GlobalScaleX);
                    float hy = (vb is not null ? Lerp(va.HalfY, vb.HalfY) : va.HalfY) * pt.FlickY * (vb is not null ? Lerp(va.GlobalScaleY, vb.GlobalScaleY) : va.GlobalScaleY);
                    if (hx <= 0f || hy <= 0f) continue;
                    var col = new[]
                    {
                        vb is not null ? Lerp(va.Colour[0], vb.Colour[0]) : va.Colour[0],
                        vb is not null ? Lerp(va.Colour[1], vb.Colour[1]) : va.Colour[1],
                        vb is not null ? Lerp(va.Colour[2], vb.Colour[2]) : va.Colour[2],
                    };
                    float a = vb is not null ? Lerp(va.Alpha, vb.Alpha) : va.Alpha;
                    a *= DistanceFadeFactor(rt, ppos, cameraPosition);
                    if (a <= 0.003f) continue;

                    if (va.IsMesh)
                    {
                        EmitMeshParticle(va, vb, w, pt, a, col, meshOut);
                        continue;
                    }

                    if (vb is not null && vb.Texture != va.Texture)
                    {
                        EmitBillboard(Sink(va, rt.ZOrder), va, ppos, pt, hx, hy, col, a * (1f - w), tLife, cameraRight, cameraUp);
                        EmitBillboard(Sink(vb, rt.ZOrder), vb, ppos, pt, hx, hy, col, a * w, tLife, cameraRight, cameraUp);
                    }
                    else
                    {
                        EmitBillboard(Sink(va, rt.ZOrder), va, ppos, pt, hx, hy, col, a, tLife, cameraRight, cameraUp);
                    }
                }
            }
        }

        var keys = new List<DrawKey>(batches.Keys);
        keys.Sort((l, r) => l.ZOrder.CompareTo(r.ZOrder));
        foreach (DrawKey key in keys)
        {
            List<(Vector3 Pos, Vector2 Uv, Color Col)> list = batches[key];
            if (list.Count == 0) continue;
            string suffix = key.Additive ? (key.SrcAlpha ? "|add" : "|addone") : "|mix";
            VisualBatchMaterial? material = materialLookup(key.Texture + suffix);
            if (material is null) continue;
            SortQuadsBackToFront(list, cameraPosition);
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, material.Material);
            foreach ((Vector3 pos, Vector2 uv, Color colr) in list)
            {
                mesh.SurfaceSetColor(colr);
                mesh.SurfaceSetUV(uv);
                mesh.SurfaceAddVertex(pos);
            }

            mesh.SurfaceEnd();
        }
    }

    private static void SortQuadsBackToFront(List<(Vector3 Pos, Vector2 Uv, Color Col)> verts, Vector3 eye)
    {
        int quads = verts.Count / 6;
        if (quads < 2) return;
        var order = new List<(float D, int Q)>(quads);
        for (int q = 0; q < quads; q++)
        {
            Vector3 c = 0.5f * (verts[q * 6].Pos + verts[(q * 6) + 1].Pos);
            order.Add(((c - eye).LengthSquared(), q));
        }

        order.Sort((l, r) => r.D.CompareTo(l.D));
        var sorted = new List<(Vector3, Vector2, Color)>(verts.Count);
        foreach ((_, int q) in order) for (int k = 0; k < 6; k++) sorted.Add(verts[(q * 6) + k]);
        verts.Clear();
        verts.AddRange(sorted);
    }

    private void EmitMeshParticle(VisualRuntime va, VisualRuntime? vb, float w, ParticleState pt, float alpha, float[] col, Dictionary<string, MeshInstanceData> meshOut)
    {
        if (!meshOut.TryGetValue(va.MeshPath, out MeshInstanceData? data))
        {
            data = new MeshInstanceData { MeshPath = va.MeshPath };
            meshOut[va.MeshPath] = data;
        }

        float ms = vb is not null && vb.IsMesh ? va.MeshScale + ((vb.MeshScale - va.MeshScale) * w) : va.MeshScale;
        float s = ms * pt.FlickX;
        if (s <= 1e-5f) return;
        Vector3 axis = ToGodot(pt.Axis);
        if (axis.LengthSquared() < 1e-6f) axis = Vector3.Up;
        var basis = new Basis(axis.Normalized(), pt.Rot).Scaled(new Vector3(s, s, s));
        data.Instances.Add((new Transform3D(basis, ToGodot(pt.Pos)), new Color(col[0], col[1], col[2], alpha)));
    }

    private static void EmitBillboard(
        List<(Vector3, Vector2, Color)> sink, VisualRuntime vis, Vector3 centre, ParticleState pt, float hx, float hy,
        float[] col, float alpha, float tLife, Vector3 cameraRight, Vector3 cameraUp)
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
            int f = vis.FrameSpeed > 0f ? (int)(pt.Age * vis.FrameSpeed) : (int)(tLife * frames);
            if (vis.RandomFrame) f += (int)(pt.Seed * frames);
            f = ((f % frames) + frames) % frames;
            int fx = f % cols, fy = f / cols;
            u0 = (float)fx / cols; u1 = (float)(fx + 1) / cols;
            v0 = (float)fy / rows; v1 = (float)(fy + 1) / rows;
        }

        Vector3 rx = right * hx, uy = up * hy;
        Vector3 a = centre - rx - uy, b = centre + rx - uy, cpt = centre - rx + uy, d = centre + rx + uy;
        var colr = new Color(col[0], col[1], col[2], alpha);
        sink.Add((a, new Vector2(u0, v1), colr));
        sink.Add((d, new Vector2(u1, v0), colr));
        sink.Add((b, new Vector2(u1, v1), colr));
        sink.Add((a, new Vector2(u0, v1), colr));
        sink.Add((cpt, new Vector2(u0, v0), colr));
        sink.Add((d, new Vector2(u1, v0), colr));
    }

    private void EmitRibbon(
        Func<VisualRuntime, int, List<(Vector3, Vector2, Color)>> sink, EmitterRuntime rt, List<VisualRuntime> vis,
        List<Vector3> pts, List<float> ages, Vector3 eye, bool constWidth)
    {
        int n = pts.Count;
        if (n < 2 || vis.Count == 0) return;
        int nvis = vis.Count;
        var attrW = new float[n]; var attrA = new float[n]; var attrC = new Vector3[n]; var attrTex = new int[n];
        for (int k = 0; k < n; k++)
        {
            float age = ages[k];
            int seg = 1;
            while (seg < nvis && age >= vis[seg].DelayFrames) seg++;
            int sb = nvis <= 1 ? 0 : Math.Min(seg, nvis - 1);
            int sa = Math.Max(sb - 1, 0);
            VisualRuntime va = vis[sa], vb = vis[sb];
            float w = sb > sa ? Math.Clamp((age - va.DelayFrames) * vb.InvSpan, 0f, 1f) : 0f;
            attrW[k] = constWidth ? vis[0].TrailWidth * 0.5f : (va.TrailWidth + ((vb.TrailWidth - va.TrailWidth) * w)) * 0.5f;
            attrC[k] = new Vector3(va.Colour[0] + ((vb.Colour[0] - va.Colour[0]) * w), va.Colour[1] + ((vb.Colour[1] - va.Colour[1]) * w), va.Colour[2] + ((vb.Colour[2] - va.Colour[2]) * w));
            attrA[k] = (va.Alpha + ((vb.Alpha - va.Alpha) * w)) * DistanceFadeFactor(rt, pts[k], eye);
            attrTex[k] = sa;
        }

        int nseg = Math.Max(1, vis[0].TrailLen);
        Vector3 pre = pts[0] - (pts[1] - pts[0]);
        Vector3 post = pts[n - 1] + (pts[n - 1] - pts[n - 2]);
        Vector3 Ctrl(int i) => i < 0 ? pre : i >= n ? post : pts[i];
        int ribs = ((n - 1) * nseg) + 1;
        var rp = new Vector3[ribs]; var rside = new Vector3[ribs]; var rc = new Vector3[ribs]; var ra = new float[ribs]; var rtex = new int[ribs];
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
        }

        for (int j = 0; j + 1 < ribs; j++)
        {
            if (ra[j] <= 0.003f && ra[j + 1] <= 0.003f) continue;
            VisualRuntime tv = vis[rtex[j]];
            if (tv.Texture.Length == 0) continue;
            List<(Vector3, Vector2, Color)> list = sink(tv, rt.ZOrder);
            float u0 = (float)j / (ribs - 1), u1 = (float)(j + 1) / (ribs - 1);
            var c0 = new Color(rc[j].X, rc[j].Y, rc[j].Z, MathF.Max(ra[j], 0f));
            var c1 = new Color(rc[j + 1].X, rc[j + 1].Y, rc[j + 1].Z, MathF.Max(ra[j + 1], 0f));
            list.Add((rp[j] + rside[j], new Vector2(u0, 0f), c0));
            list.Add((rp[j + 1] + rside[j + 1], new Vector2(u1, 0f), c1));
            list.Add((rp[j] - rside[j], new Vector2(u0, 1f), c0));
            list.Add((rp[j] - rside[j], new Vector2(u0, 1f), c0));
            list.Add((rp[j + 1] + rside[j + 1], new Vector2(u1, 0f), c1));
            list.Add((rp[j + 1] - rside[j + 1], new Vector2(u1, 1f), c1));
        }
    }
}

public sealed class VisualBatchMaterial
{
    public required Material Material { get; init; }
}
