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
    public string EffectName = string.Empty;
    public uint EffectHash;
    public bool Resolved;
}

public sealed class Engine_FxSystem
{
    public const int TrailHistory = 32;
    public const float RateToPerSec = 30f;
    public const float MinLife = 0.02f;
    public const float Exposure = 10f / 3f;

    private sealed class ParticleState
    {
        public float[] Pos = new float[3];
        public float[] Vel = new float[3];
        public float Age;
        public float Life;
        public float Rot;
        public float Spin;
        public float Seed;
        public float FlickX = 1f;
        public float FlickY = 1f;
        public int FlickFrame = -1;
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
        public int TrailLen = 8;

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
        public float[] OrAxis = { 0f, 0f, 1f };
        public bool OrRandAxis = true;
    }

    private sealed class EmitterRuntime
    {
        public FxEmitter? Def;
        public List<VisualRuntime> Visuals { get; } = new();
        public float Accum;
        public float Age;
        public float[] Origin = new float[3];
        public float YawCos = 1f;
        public float YawSin;
        public bool HasRot;
        public float[] RotGame = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        public float MaxVisualDelay;
        public float StartDelay;
        public float Scale = 1f;
        public bool Trail;
        public float SpawnRadius;
        public bool EmittedInitial;
        public List<ParticleState> Particles { get; } = new();
    }

    private sealed class InstanceState
    {
        public FxPlacement Place = new();
        public float Time;
        public List<EmitterRuntime> Emitters { get; } = new();
    }

    private readonly List<InstanceState> _instances = new();
    private readonly List<(Vector3 Pos, Vector2 Uv, Color Col)> _scratch = new();
    private uint _rng = 0x1234567u;
    private bool _loop = true;

    public int AliveCount()
    {
        int alive = 0;
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                alive += rt.Particles.Count;
            }
        }

        return alive;
    }

    public int TotalSpawned { get; private set; }

    /// <summary>
    /// ADDITIVE MEANS THE DESTINATION FACTOR IS ONE, and in this data that is the value 1.
    ///
    /// The bank's two blend fields are NOT D3DBLEND. They are an engine enum the guest runs
    /// through `Render_MapBlendFactorToD3D` (0x822D76F8, an identity switch) into
    /// `g_BlendFactorMapD3D` at 0x8331701C, which reads as u32 big-endian
    ///
    ///     [0, 1, 4, 5, 6, 7, 10, 11, 8, 9, 12, 13, 16]
    ///
    /// and the result is written straight into RB_BLENDCONTROL by
    /// `D3DDevice_SetRenderState_SrcBlend` (0x821DC158, `Value &amp; 0x1F`), so these are XENOS
    /// hardware blend factors: 0 ZERO, 1 ONE, 4 SRC_COLOR, 5 INV_SRC_COLOR, 6 SRC_ALPHA,
    /// 7 INV_SRC_ALPHA. The engine value indexes that table directly.
    ///
    /// The old test was `dst != 7 &amp;&amp; dst != 11` — hardware numbers applied to engine values.
    /// Across the whole bank the engine values present are only 0, 4, 5 and absent, so that test
    /// called EVERY visual additive. Smoke, steam and dust were being drawn as glowing adds.
    ///
    /// WHEN THE FIELDS ARE ABSENT the guest does not default to additive either. Both the
    /// binary loader (0x8321F488) and the material struct's constructor (0x83245768) seed
    /// src = 1 (ONE) and dst = 5 (INV_SRC_ALPHA) — premultiplied alpha, and the single largest
    /// population in the bank.
    /// </summary>
    private static bool IsAdditive(FxVisual vis)
    {
        const int one = 1;
        return vis.DstBlend == one;
    }

    private static bool UsesSourceAlpha(FxVisual vis)
    {
        const int sourceAlpha = 4;
        return vis.SrcBlend == sourceAlpha;
    }

    public static bool EffectSustains(FxBank bank, uint effectHash, float seconds = 12f)
    {
        var probe = new Engine_FxSystem();
        var placement = new FxPlacement { EffectHash = effectHash };
        probe.Build(bank, new[] { placement });
        if (!placement.Resolved)
        {
            return false;
        }

        probe.SetLoop(false);
        int steps = (int)(seconds * 30f);
        int clipEnd = -1;
        int lastSpawnStep = 0;
        int previousSpawned = 0;
        bool everAlive = false;

        for (int i = 0; i < steps; i++)
        {
            probe.Update(1f / 30f);

            if (probe.TotalSpawned > previousSpawned)
            {
                previousSpawned = probe.TotalSpawned;
                lastSpawnStep = i;
            }

            if (probe.AliveCount() > 0)
            {
                everAlive = true;
            }
            else if (everAlive && clipEnd < 0)
            {
                clipEnd = i;
                break;
            }
        }

        if (!everAlive)
        {
            return false;
        }

        if (clipEnd < 0)
        {
            return true;
        }

        return lastSpawnStep >= (int)(clipEnd * 0.5f);
    }

    public int ResolvedCount { get; private set; }

    public bool IsEmpty => _instances.Count == 0;

    public void SetLoop(bool loop) => _loop = loop;

    public IEnumerable<string> Textures()
    {
        var seen = new HashSet<string>();
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                foreach (VisualRuntime vis in rt.Visuals)
                {
                    if (vis.Texture.Length != 0 && seen.Add(vis.Texture))
                    {
                        yield return vis.Texture;
                    }
                }
            }
        }
    }

    public IEnumerable<string> MeshPaths()
    {
        var seen = new HashSet<string>();
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                foreach (VisualRuntime vis in rt.Visuals)
                {
                    if (vis.IsMesh && vis.MeshPath.Length != 0 && seen.Add(vis.MeshPath))
                    {
                        yield return vis.MeshPath;
                    }
                }
            }
        }
    }

    private float Frand()
    {
        _rng = (_rng * 1664525u) + 1013904223u;
        return ((_rng >> 8) & 0xFFFFFF) / (float)0x1000000;
    }

    private float Srand2() => (Frand() * 2f) - 1f;

    public void Clear()
    {
        _instances.Clear();
        ResolvedCount = 0;
    }

    public void Build(FxBank bank, IEnumerable<FxPlacement> placements)
    {
        Clear();
        foreach (FxPlacement place in placements)
        {
            if (place.EffectHash == 0)
            {
                place.EffectHash = FxBank.Fnv1Lower(place.EffectName);
            }

            var inst = new InstanceState { Place = place };
            float sc = place.Scale > 0.0001f ? place.Scale : 1f;
            float cy = MathF.Cos(place.Yaw);
            float sy = MathF.Sin(place.Yaw);

            FxEffect? effect = bank.FindByHash(place.EffectHash);
            if (effect is not null)
            {
                foreach (FxEffectPart part in effect.Parts)
                {
                    if (part.Emitter < 0 || part.Emitter >= bank.Emitters.Count)
                    {
                        continue;
                    }

                    FxEmitter em = bank.Emitters[part.Emitter];
                    if (!em.HasMotion)
                    {
                        continue;
                    }

                    var rt = new EmitterRuntime
                    {
                        Def = em,
                        Scale = sc,
                        StartDelay = em.StartTimeSecs,
                        YawCos = cy,
                        YawSin = sy,
                        HasRot = place.HasRot,
                        SpawnRadius = em.SpawnRadius * sc,
                    };
                    if (place.HasRot)
                    {
                        Array.Copy(place.Rot, rt.RotGame, 9);
                    }

                    float lx;
                    float ly;
                    float lz;
                    if (place.HasRot)
                    {
                        float[] m = place.Rot;
                        lx = (m[0] * em.Pos[0]) + (m[1] * em.Pos[1]) + (m[2] * em.Pos[2]);
                        ly = (m[3] * em.Pos[0]) + (m[4] * em.Pos[1]) + (m[5] * em.Pos[2]);
                        lz = (m[6] * em.Pos[0]) + (m[7] * em.Pos[1]) + (m[8] * em.Pos[2]);
                    }
                    else
                    {
                        lx = (em.Pos[0] * cy) - (em.Pos[1] * sy);
                        ly = (em.Pos[0] * sy) + (em.Pos[1] * cy);
                        lz = em.Pos[2];
                    }

                    rt.Origin[0] = place.Pos[0] + (lx * sc);
                    rt.Origin[1] = place.Pos[1] + (ly * sc);
                    rt.Origin[2] = place.Pos[2] + (lz * sc);

                    uint mode = 1;
                    if (part.System >= 0 && part.System < bank.Systems.Count)
                    {
                        FxSystemDef sys = bank.Systems[part.System];
                        mode = sys.Mode;
                        if (mode is >= 1 and <= 4)
                        {
                            rt.Trail = mode is 3;
                            foreach (FxVisual vis in sys.Visuals)
                            {
                                if (vis.Displacement)
                                {
                                    continue;
                                }

                                if (vis.Texture.Length == 0 && !vis.HasMesh)
                                {
                                    continue;
                                }

                                VisualRuntime v = BuildVisual(vis, sc, mode);
                                if (v.Delay > rt.MaxVisualDelay)
                                {
                                    rt.MaxVisualDelay = v.Delay;
                                }

                                rt.Visuals.Add(v);
                            }
                        }
                    }

                    if (rt.Visuals.Count == 0)
                    {
                        continue;
                    }

                    inst.Emitters.Add(rt);
                }
            }

            if (inst.Emitters.Count != 0)
            {
                place.Resolved = true;
                ResolvedCount++;
                _instances.Add(inst);
            }
        }
    }

    private static VisualRuntime BuildVisual(FxVisual vis, float sc, uint mode)
    {
        var v = new VisualRuntime
        {
            Texture = vis.Texture,
            Cols = vis.TexCols,
            Rows = vis.TexRows,
            FrameSpeed = vis.FrameSpeed,
            RandomFrame = vis.RandomFrame,
            Delay = vis.Delay,
            Additive = IsAdditive(vis),
            SrcAlpha = UsesSourceAlpha(vis),
            Alpha = vis.HasAlpha ? vis.Alpha : 1f,
            GlobalScaleX = vis.HasGlobalScale ? vis.GlobalScale[0] : 1f,
            GlobalScaleY = vis.HasGlobalScale ? vis.GlobalScale[1] : 1f,
            HasRotation = vis.HasRotation,
            RotSpeed = vis.RotSpeed,
            RotSpread = vis.RotSpread,
            RotInitialRandom = vis.RotInitialRandom,
            HasFlicker = vis.HasFlicker,
            TrailWidth = (vis.HasScalarSize ? vis.ScalarSize : 1f) * sc,
            TrailLen = Math.Clamp(vis.HasNumSegments ? vis.NumSegments : 1, 1, TrailHistory),
        };

        if (vis.HasColor)
        {
            v.Colour = (float[])vis.Color.Clone();
        }

        if (vis.HasFlicker)
        {
            v.FlickerFrames = Math.Max(1, (int)((vis.FlickerPeriod * 30f) + 0.5f));
            v.FlickerBaseX = vis.FlickerBaseX;
            v.FlickerBaseY = vis.FlickerBaseY;
            v.FlickerSpreadX = vis.FlickerSpreadX;
            v.FlickerSpreadY = vis.FlickerSpreadY;
            v.FlickerShared = vis.FlickerShared;
        }

        if (!vis.HasSize && mode == 2 && vis.HasScalarSize && vis.ScalarSize > 0f)
        {
            v.HalfX = vis.ScalarSize * 0.5f * sc;
            v.HalfY = vis.ScalarSize * 0.5f * sc;
        }
        else
        {
            float hx = vis.HasSize ? vis.SizeX : 1f;
            float hy = vis.HasSize ? vis.SizeY : 1f;
            if (hx <= 0f)
            {
                hx = 1f;
            }

            if (hy <= 0f)
            {
                hy = 1f;
            }

            v.HalfX = hx * 0.5f * sc;
            v.HalfY = hy * 0.5f * sc;
        }

        if (vis.HasMesh)
        {
            v.IsMesh = true;
            v.MeshPath = vis.MeshPath;
            v.MeshScale = (vis.HasScalarSize ? vis.ScalarSize : 1f) * sc;
            v.HasRandScale = vis.HasRandScale;
            if (vis.HasRandScale)
            {
                v.RsFrames = vis.RsFreq > 1e-4f
                    ? Math.Max(1, (int)((vis.RsFreq * 30f) + 0.5f))
                    : 0;
                v.RsMin = vis.RsMin;
                v.RsRange = vis.RsRange;
            }

            if (vis.HasOrient)
            {
                v.OrSpeed = vis.OrSpeed;
                v.OrSpread = vis.OrSpread;
                v.OrRandAngle = vis.OrRandAngle;
                v.OrAxis = (float[])vis.OrAxis.Clone();
                v.OrRandAxis = vis.OrRandAxis;
            }
        }

        return v;
    }

    private void SpawnOne(EmitterRuntime rt)
    {
        FxEmitter? d = rt.Def;
        var pt = new ParticleState();

        if (rt.SpawnRadius > 0f)
        {
            float sx;
            float sy;
            float sz;
            do
            {
                sx = Srand2();
                sy = Srand2();
                sz = Srand2();
            }
            while ((sx * sx) + (sy * sy) + (sz * sz) > 1f);
            pt.Pos[0] = rt.Origin[0] + (sx * rt.SpawnRadius);
            pt.Pos[1] = rt.Origin[1] + (sy * rt.SpawnRadius);
            pt.Pos[2] = rt.Origin[2] + (sz * rt.SpawnRadius);
        }
        else
        {
            Array.Copy(rt.Origin, pt.Pos, 3);
        }

        float phi = Frand() * MathF.Tau;
        float gx = 1f;
        float gy = 0f;
        float gz = 0f;
        if (d is not null)
        {
            gx = d.VelX.Eval(rt.Age) * MathF.Cos(phi);
            gy = d.VelY.Eval(rt.Age) * MathF.Sin(phi);
            gz = d.VelZ.Eval(rt.Age);
            float len = MathF.Sqrt((gx * gx) + (gy * gy) + (gz * gz));
            if (len > 1e-4f)
            {
                gx /= len;
                gy /= len;
                gz /= len;
            }
            else
            {
                gx = 1f;
                gy = 0f;
                gz = 0f;
            }

            float spd = d.Speed.Eval(rt.Age) + (Frand() * d.SpeedSpread.Eval(rt.Age));
            gx *= spd;
            gy *= spd;
            gz *= spd;
        }

        if (rt.HasRot)
        {
            float[] m = rt.RotGame;
            pt.Vel[0] = ((m[0] * gx) + (m[1] * gy) + (m[2] * gz)) * rt.Scale;
            pt.Vel[1] = ((m[3] * gx) + (m[4] * gy) + (m[5] * gz)) * rt.Scale;
            pt.Vel[2] = ((m[6] * gx) + (m[7] * gy) + (m[8] * gz)) * rt.Scale;
        }
        else
        {
            pt.Vel[0] = ((gx * rt.YawCos) - (gy * rt.YawSin)) * rt.Scale;
            pt.Vel[1] = ((gx * rt.YawSin) + (gy * rt.YawCos)) * rt.Scale;
            pt.Vel[2] = gz * rt.Scale;
        }

        pt.Life = MathF.Max(
            MinLife, rt.MaxVisualDelay > 1e-4f ? rt.MaxVisualDelay : 1.4f);

        VisualRuntime? v0 = rt.Visuals.Count != 0 ? rt.Visuals[0] : null;
        if (v0 is not null && v0.IsMesh)
        {
            pt.Rot = v0.OrRandAngle ? Frand() * MathF.Tau : 0f;
            pt.Spin = (v0.OrSpeed + (v0.OrSpread * Srand2())) * 30f;
            if (v0.OrRandAxis)
            {
                float ax;
                float ay;
                float az;
                float l2;
                do
                {
                    ax = Srand2();
                    ay = Srand2();
                    az = Srand2();
                    l2 = (ax * ax) + (ay * ay) + (az * az);
                }
                while (l2 > 1f || l2 < 1e-4f);
                float il = 1f / MathF.Sqrt(l2);
                pt.Axis[0] = ax * il;
                pt.Axis[1] = ay * il;
                pt.Axis[2] = az * il;
            }
            else
            {
                float lx = v0.OrAxis[0];
                float ly = v0.OrAxis[1];
                float lz = v0.OrAxis[2];
                float ln = MathF.Sqrt((lx * lx) + (ly * ly) + (lz * lz));
                if (ln < 1e-4f)
                {
                    lx = 0f;
                    ly = 0f;
                    lz = 1f;
                    ln = 1f;
                }

                pt.Axis[0] = lx / ln;
                pt.Axis[1] = ly / ln;
                pt.Axis[2] = lz / ln;
            }

            if (v0.HasRandScale)
            {
                pt.FlickX = v0.RsMin + (Frand() * v0.RsRange);
            }
        }
        else if (v0 is not null && v0.HasRotation)
        {
            pt.Rot = v0.RotInitialRandom ? Frand() * MathF.Tau : 0f;
            pt.Spin = (v0.RotSpeed + (v0.RotSpread * Srand2())) * 30f;
        }

        pt.Seed = Frand();
        if (rt.Trail)
        {
            pt.Hist[0] = new[] { pt.Pos[0], pt.Pos[1], pt.Pos[2] };
            pt.HistCount = 1;
        }

        rt.Particles.Add(pt);
        TotalSpawned++;
    }

    public void Update(float dt)
    {
        if (dt <= 0f)
        {
            dt = 1f / 60f;
        }

        dt = MathF.Min(dt, 0.1f);
        foreach (InstanceState inst in _instances)
        {
            inst.Time += dt;
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                FxEmitter? d = rt.Def;
                if (inst.Time < rt.StartDelay)
                {
                    continue;
                }

                rt.Age = inst.Time - rt.StartDelay;

                if (!rt.EmittedInitial)
                {
                    rt.EmittedInitial = true;
                    int init = d is not null ? (int)MathF.Max(0f, d.InitialNum) : 1;
                    init = Math.Min(init, 64);
                    for (int i = 0; i < init; i++)
                    {
                        SpawnOne(rt);
                    }
                }

                float rate = d is not null
                    ? (d.Rate.Eval(rt.Age) + (Frand() * d.RateSpread.Eval(rt.Age))) *
                      RateToPerSec
                    : 0f;
                float cap = d?.MaxActive ?? 500f;
                if (!(cap > 0f) || float.IsNaN(cap))
                {
                    cap = 500f;
                }

                cap = MathF.Min(cap, 2500f);

                rt.Accum += MathF.Max(rate, 0f) * dt;
                int guard = 600;
                while (rt.Accum >= 1f && guard-- > 0)
                {
                    rt.Accum -= 1f;
                    if (rt.Particles.Count >= (int)cap)
                    {
                        rt.Accum = 0f;
                        break;
                    }

                    SpawnOne(rt);
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    pt.Age += dt;
                    pt.Pos[0] += pt.Vel[0] * dt;
                    pt.Pos[1] += pt.Vel[1] * dt;
                    pt.Pos[2] += pt.Vel[2] * dt;
                    pt.Rot += pt.Spin * dt;

                    if (rt.Trail)
                    {
                        int n = Math.Min(pt.HistCount + 1, TrailHistory);
                        for (int k = n - 1; k > 0; k--)
                        {
                            pt.Hist[k] = pt.Hist[k - 1];
                        }

                        pt.Hist[0] = new[] { pt.Pos[0], pt.Pos[1], pt.Pos[2] };
                        pt.HistCount = n;
                    }

                    if (rt.Visuals.Count != 0)
                    {
                        int seg = 0;
                        for (int i = 0; i < rt.Visuals.Count; i++)
                        {
                            if (rt.Visuals[i].Delay <= pt.Age)
                            {
                                seg = i;
                            }
                        }

                        VisualRuntime vv = rt.Visuals[seg];
                        if (vv.IsMesh)
                        {
                            if (vv.HasRandScale && vv.RsFrames > 0)
                            {
                                int f = (int)((pt.Age * 30f) + 0.5f);
                                if (f != pt.FlickFrame)
                                {
                                    pt.FlickFrame = f;
                                    if (f % vv.RsFrames == 0)
                                    {
                                        pt.FlickX = vv.RsMin + (Frand() * vv.RsRange);
                                    }
                                }
                            }
                        }
                        else if (vv.HasFlicker && vv.FlickerFrames > 0)
                        {
                            int f = (int)((pt.Age * 30f) + 0.5f);
                            if (f != pt.FlickFrame)
                            {
                                pt.FlickFrame = f;
                                if (f % vv.FlickerFrames == 0)
                                {
                                    float r1 = Frand();
                                    pt.FlickX = vv.FlickerBaseX + (r1 * vv.FlickerSpreadX);
                                    float r2 = vv.FlickerShared ? r1 : Frand();
                                    pt.FlickY = vv.FlickerBaseY + (r2 * vv.FlickerSpreadY);
                                }
                            }
                        }
                    }
                }

                rt.Particles.RemoveAll(p => p.Age >= p.Life);
            }

            if (_loop && inst.Time > 1.5f)
            {
                int alive = 0;
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    alive += rt.Particles.Count;
                }

                if (alive == 0)
                {
                    inst.Time = 0f;
                    foreach (EmitterRuntime rt in inst.Emitters)
                    {
                        rt.Age = 0f;
                        rt.Accum = 0f;
                        rt.EmittedInitial = false;
                    }
                }
            }
        }
    }

    private static Vector3 ToGodot(float[] game) => new(game[0], game[2], game[1]);

    public sealed class MeshInstanceData
    {
        public string MeshPath = string.Empty;
        public List<(Transform3D Transform, Color Colour)> Instances { get; } = new();
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
        foreach (MeshInstanceData data in meshOut.Values)
        {
            data.Instances.Clear();
        }

        var batches =
            new Dictionary<(string Texture, bool Additive, bool SrcAlpha), List<(VisualRuntime, ParticleState, EmitterRuntime, float, VisualRuntime?, float, VisualRuntime)>>();

        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                foreach (ParticleState pt in rt.Particles)
                {
                    if (rt.Visuals.Count == 0)
                    {
                        continue;
                    }

                    int seg = 0;
                    for (int i = 0; i < rt.Visuals.Count; i++)
                    {
                        if (rt.Visuals[i].Delay <= pt.Age)
                        {
                            seg = i;
                        }
                    }

                    VisualRuntime va = rt.Visuals[seg];
                    VisualRuntime? vb = seg + 1 < rt.Visuals.Count
                        ? rt.Visuals[seg + 1]
                        : null;
                    float w = 0f;
                    if (vb is not null)
                    {
                        float span = vb.Delay - va.Delay;
                        w = span > 1e-4f
                            ? Math.Clamp((pt.Age - va.Delay) / span, 0f, 1f)
                            : 1f;
                    }

                    float alpha = vb is not null
                        ? va.Alpha + ((vb.Alpha - va.Alpha) * w)
                        : va.Alpha;
                    if (alpha <= 0.003f)
                    {
                        continue;
                    }

                    if (va.IsMesh)
                    {
                        EmitMeshParticle(va, vb, w, pt, alpha, meshOut);
                        continue;
                    }

                    if (vb is not null && vb.Texture != va.Texture)
                    {
                        Enqueue(batches, va, pt, rt, alpha * (1f - w), vb, w, va);
                        Enqueue(batches, vb, pt, rt, alpha * w, vb, w, va);
                    }
                    else
                    {
                        Enqueue(batches, va, pt, rt, alpha, vb, w, va);
                    }
                }
            }
        }

        foreach (((string texture, bool additive, bool srcAlpha), var list) in batches)
        {
            string suffix = additive ? (srcAlpha ? "|add" : "|addone") : "|mix";
            VisualBatchMaterial? material = materialLookup(texture + suffix);
            if (material is null)
            {
                continue;
            }

            _scratch.Clear();
            foreach ((VisualRuntime draw, ParticleState pt, EmitterRuntime rt,
                float alpha, VisualRuntime? vb, float w, VisualRuntime va) in list)
            {
                if (alpha <= 0.003f)
                {
                    continue;
                }

                if (rt.Trail)
                {
                    EmitTrail(_scratch, va, vb, w, pt, alpha, cameraPosition);
                }
                else
                {
                    EmitBillboard(_scratch, va, vb, w, pt, alpha, cameraRight, cameraUp, draw);
                }
            }

            if (_scratch.Count == 0)
            {
                continue;
            }

            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, material.Material);
            foreach ((Vector3 pos, Vector2 uv, Color col) in _scratch)
            {
                mesh.SurfaceSetColor(col);
                mesh.SurfaceSetUV(uv);
                mesh.SurfaceAddVertex(pos);
            }

            mesh.SurfaceEnd();
        }
    }

    private void EmitMeshParticle(
        VisualRuntime va,
        VisualRuntime? vb,
        float w,
        ParticleState pt,
        float alpha,
        Dictionary<string, MeshInstanceData> meshOut)
    {
        if (!meshOut.TryGetValue(va.MeshPath, out MeshInstanceData? data))
        {
            data = new MeshInstanceData { MeshPath = va.MeshPath };
            meshOut[va.MeshPath] = data;
        }

        float ms = vb is not null && vb.IsMesh
            ? va.MeshScale + ((vb.MeshScale - va.MeshScale) * w)
            : va.MeshScale;
        float s = ms * pt.FlickX;
        if (s <= 1e-5f)
        {
            return;
        }

        Vector3 axis = ToGodot(pt.Axis);
        if (axis.LengthSquared() < 1e-6f)
        {
            axis = Vector3.Up;
        }

        var basis = new Basis(axis.Normalized(), pt.Rot);
        basis = basis.Scaled(new Vector3(s, s, s));
        var transform = new Transform3D(basis, ToGodot(pt.Pos));

        float cr = va.Colour[0];
        float cg = va.Colour[1];
        float cb = va.Colour[2];
        if (vb is not null)
        {
            cr += (vb.Colour[0] - cr) * w;
            cg += (vb.Colour[1] - cg) * w;
            cb += (vb.Colour[2] - cb) * w;
        }

        data.Instances.Add((transform, new Color(cr, cg, cb, alpha)));
    }

    private void EmitTrail(
        List<(Vector3 Pos, Vector2 Uv, Color Col)> mesh,
        VisualRuntime va,
        VisualRuntime? vb,
        float w,
        ParticleState pt,
        float alpha,
        Vector3 cameraPosition)
    {
        float width = (vb is not null
            ? va.TrailWidth + ((vb.TrailWidth - va.TrailWidth) * w)
            : va.TrailWidth) * 0.5f;
        int want = Math.Max(va.TrailLen, vb?.TrailLen ?? 2);
        int n = Math.Min(pt.HistCount, want);
        if (n < 2 || width <= 0f)
        {
            return;
        }

        float cr = va.Colour[0];
        float cg = va.Colour[1];
        float cb = va.Colour[2];
        float inv = 1f / (n - 1);
        for (int k = 0; k + 1 < n; k++)
        {
            Vector3 p0 = ToGodot(pt.Hist[k]);
            Vector3 p1 = ToGodot(pt.Hist[k + 1]);
            Vector3 dir = p1 - p0;
            Vector3 eye = cameraPosition - p0;
            Vector3 side = dir.Cross(eye);
            float sl = side.Length();
            if (sl < 1e-6f)
            {
                continue;
            }

            side = side / sl * width;
            float f0 = alpha * (1f - (k * inv));
            float f1 = alpha * (1f - ((k + 1) * inv));
            float u0 = k * inv;
            float u1 = (k + 1) * inv;

            AddVert(mesh, p0 + side, new Vector2(u0, 0f), cr, cg, cb, f0);
            AddVert(mesh, p1 + side, new Vector2(u1, 0f), cr, cg, cb, f1);
            AddVert(mesh, p0 - side, new Vector2(u0, 1f), cr, cg, cb, f0);
            AddVert(mesh, p0 - side, new Vector2(u0, 1f), cr, cg, cb, f0);
            AddVert(mesh, p1 + side, new Vector2(u1, 0f), cr, cg, cb, f1);
            AddVert(mesh, p1 - side, new Vector2(u1, 1f), cr, cg, cb, f1);
        }
    }

    private static void Enqueue(
        Dictionary<(string Texture, bool Additive, bool SrcAlpha),
            List<(VisualRuntime, ParticleState, EmitterRuntime, float, VisualRuntime?, float, VisualRuntime)>> batches,
        VisualRuntime draw,
        ParticleState pt,
        EmitterRuntime rt,
        float alpha,
        VisualRuntime? vb,
        float w,
        VisualRuntime va)
    {
        (string, bool, bool) key = (draw.Texture, draw.Additive, draw.SrcAlpha);
        if (!batches.TryGetValue(key, out var list))
        {
            list = new List<(VisualRuntime, ParticleState, EmitterRuntime, float, VisualRuntime?, float, VisualRuntime)>();
            batches[key] = list;
        }

        list.Add((draw, pt, rt, alpha, vb, w, va));
    }

    private void EmitBillboard(
        List<(Vector3 Pos, Vector2 Uv, Color Col)> mesh,
        VisualRuntime va,
        VisualRuntime? vb,
        float w,
        ParticleState pt,
        float alpha,
        Vector3 cameraRight,
        Vector3 cameraUp,
        VisualRuntime draw)
    {
        float gx = vb is not null
            ? va.GlobalScaleX + ((vb.GlobalScaleX - va.GlobalScaleX) * w)
            : va.GlobalScaleX;
        float gy = vb is not null
            ? va.GlobalScaleY + ((vb.GlobalScaleY - va.GlobalScaleY) * w)
            : va.GlobalScaleY;
        float hx = (vb is not null ? va.HalfX + ((vb.HalfX - va.HalfX) * w) : va.HalfX) *
                   pt.FlickX * gx;
        float hy = (vb is not null ? va.HalfY + ((vb.HalfY - va.HalfY) * w) : va.HalfY) *
                   pt.FlickY * gy;
        if (hx <= 0f || hy <= 0f)
        {
            return;
        }

        float cr = va.Colour[0];
        float cg = va.Colour[1];
        float cb = va.Colour[2];
        if (vb is not null)
        {
            cr += (vb.Colour[0] - cr) * w;
            cg += (vb.Colour[1] - cg) * w;
            cb += (vb.Colour[2] - cb) * w;
        }

        float c = MathF.Cos(pt.Rot);
        float s = MathF.Sin(pt.Rot);
        Vector3 right = (cameraRight * c) + (cameraUp * s);
        Vector3 up = (-cameraRight * s) + (cameraUp * c);
        Vector3 centre = ToGodot(pt.Pos);

        int cols = Math.Max(1, draw.Cols);
        int rows = Math.Max(1, draw.Rows);
        int frames = cols * rows;
        float u0 = 0f;
        float v0 = 0f;
        float u1 = 1f;
        float v1 = 1f;
        if (frames > 1)
        {
            float tLife = pt.Life > 0f ? pt.Age / pt.Life : 1f;
            int f = draw.FrameSpeed > 0f
                ? (int)(pt.Age * draw.FrameSpeed * 30f)
                : (int)(tLife * frames);
            if (draw.RandomFrame)
            {
                f += (int)(pt.Seed * frames);
            }

            f = ((f % frames) + frames) % frames;
            int fx = f % cols;
            int fy = f / cols;
            u0 = (float)fx / cols;
            u1 = (float)(fx + 1) / cols;
            v0 = (float)fy / rows;
            v1 = (float)(fy + 1) / rows;
        }

        Vector3 rx = right * hx;
        Vector3 uy = up * hy;
        Vector3 a = centre - rx - uy;
        Vector3 b = centre + rx - uy;
        Vector3 cpt = centre - rx + uy;
        Vector3 d = centre + rx + uy;

        AddVert(mesh, a, new Vector2(u0, v1), cr, cg, cb, alpha);
        AddVert(mesh, d, new Vector2(u1, v0), cr, cg, cb, alpha);
        AddVert(mesh, b, new Vector2(u1, v1), cr, cg, cb, alpha);
        AddVert(mesh, a, new Vector2(u0, v1), cr, cg, cb, alpha);
        AddVert(mesh, cpt, new Vector2(u0, v0), cr, cg, cb, alpha);
        AddVert(mesh, d, new Vector2(u1, v0), cr, cg, cb, alpha);
    }

    private static void AddVert(
        List<(Vector3 Pos, Vector2 Uv, Color Col)> sink, Vector3 pos, Vector2 uv,
        float r, float g, float b, float a)
    {
        sink.Add((pos, uv, new Color(r, g, b, a)));
    }
}

public sealed class VisualBatchMaterial
{
    public required Material Material { get; init; }
}
