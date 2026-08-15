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

    /// <summary>
    /// The constant the guest's gravity term scales, on the world's UP axis — which is game-space
    /// Z, so the acceleration is (0, 0, <c>Gravity</c>).
    ///
    /// `sub_832845B0` builds the global at <c>unk_8349E4D0</c> out of <c>flt_82099484</c> = 0.0
    /// and <c>flt_82000E34</c>; the four bytes at 0x82000E34 are <c>C1 1C CC CD</c>, which is
    /// -9.8. `sub_8322D988` multiplies it by generic parameter 7 and adds the result into the
    /// frame's acceleration (<c>vmulfp128 v28, v0, v29</c> at 0x8322dbe0).
    /// </summary>
    public const float Gravity = -9.8f;

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

        /// <summary>The header's <c>EmitterShape</c>: 0 point, 1 sphere, 2 disc, 3 mesh.</summary>
        public uint Shape;

        public bool EmitOnSurface;
        public bool UseShapeSpawnDir;

        /// <summary>
        /// The emitter TYPE's own orientation, as a 3x3 basis, or null when it is identity.
        ///
        /// `sub_83228F78` composes it with the instance's orientation (<c>q_instance (x) q_type</c>,
        /// loading the instance quat from inst+0x10 and the type's from type+0x10) and the result
        /// becomes the emitter's frame. The spawn direction is then multiplied through rows 0..2
        /// of that frame at the tail of `sub_8322CAF0` (0x8322cf0c-0x8322cf1c).
        ///
        /// 175 of the bank's emitters carry a non-identity one, and they cluster on
        /// (+/-0.7071, 0, 0, 0.7071) — a +/-90 degree turn about X, which is exactly what stands a
        /// "Z forward, Y up" authoring frame up in a Z-up world. Ignoring it aimed all 175 of
        /// them a right angle away from where the author pointed them.
        /// </summary>
        public float[]? TypeBasis;

        public bool EmittedInitial;

        /// <summary>The spline this emitter rides, or null when it stands still.</summary>
        public SplineRuntime? Spline;

        /// <summary>
        /// Where the emitter sits with its spline at the origin. <see cref="Origin"/> is this plus
        /// the spline's current point, recomputed every frame, so the two must stay separate.
        /// </summary>
        public float[] BaseOrigin = new float[3];

        public List<ParticleState> Particles { get; } = new();
    }

    /// <summary>
    /// A vector in the emitter's own axes, brought into the effect's space by the emitter type's
    /// orientation. The placement's rotation is applied after this, by the caller.
    /// </summary>
    private static void LocalToEffect(
        EmitterRuntime rt, float x, float y, float z,
        out float ox, out float oy, out float oz)
    {
        if (rt.TypeBasis is not { } m)
        {
            ox = x;
            oy = y;
            oz = z;
            return;
        }

        ox = (m[0] * x) + (m[1] * y) + (m[2] * z);
        oy = (m[3] * x) + (m[4] * y) + (m[5] * z);
        oz = (m[6] * x) + (m[7] * y) + (m[8] * z);
    }

    /// <summary>
    /// The 3x3 of a unit quaternion stored X, Y, Z, W — the order the file uses, confirmed by
    /// <c>CMaterialParamAlignmentQuat</c>'s own serializer naming its four floats X, Y, Z, W.
    /// Returns null for an identity (or unusable) rotation so the common case costs nothing.
    /// </summary>
    private static float[]? BasisFromQuat(float[] q)
    {
        float x = q[0];
        float y = q[1];
        float z = q[2];
        float w = q[3];

        float len2 = (x * x) + (y * y) + (z * z) + (w * w);
        if (!float.IsFinite(len2) || len2 < 1e-6f)
        {
            return null;
        }

        float inv = 1f / MathF.Sqrt(len2);
        x *= inv;
        y *= inv;
        z *= inv;
        w *= inv;

        if ((x * x) + (y * y) + (z * z) < 1e-8f)
        {
            return null;
        }

        return new[]
        {
            1f - (2f * ((y * y) + (z * z))), 2f * ((x * y) - (z * w)), 2f * ((x * z) + (y * w)),
            2f * ((x * y) + (z * w)), 1f - (2f * ((x * x) + (z * z))), 2f * ((y * z) - (x * w)),
            2f * ((x * z) - (y * w)), 2f * ((y * z) + (x * w)), 1f - (2f * ((x * x) + (y * y))),
        };
    }

    private sealed class InstanceState
    {
        public FxPlacement Place = new();
        public float Time;
        public List<EmitterRuntime> Emitters { get; } = new();

        /// <summary>
        /// The effect's LIGHTS, kept apart from its emitters because they are not emitters: they
        /// spawn nothing, own no particles and carry no visuals, so running them through the
        /// particle path would drop them twice over — once on <c>HasMotion</c> and again on
        /// having an empty visual list.
        /// </summary>
        public List<LightRuntime> Lights { get; } = new();

        /// <summary>
        /// The effect's own attractors. Scoped to the effect because that is the list
        /// `sub_83229A98` sums for a particle — the one at inst+220 — and the world-wide registry
        /// beside it only holds attractors that ask to be in it.
        /// </summary>
        public List<LightRuntime> Attractors { get; } = new();

        /// <summary>The paths this effect's emitters travel.</summary>
        public List<SplineRuntime> Splines { get; } = new();
    }

    /// <summary>
    /// A part of an effect that is placed but does not emit — a light or an attractor. Both take
    /// the same base block, so both are positioned the same way.
    /// </summary>
    private sealed class LightRuntime
    {
        public FxEmitter? Def;
        public float[] Origin = new float[3];
        public float Scale = 1f;
        public float StartDelay;
    }

    /// <summary>
    /// A spline the effect travels along, and the <c>t</c> it has reached.
    ///
    /// The point on the curve is the spline node's LOCAL translation, which every child inherits
    /// through the ordinary parent walk — there is no special "move the emitter" code in the
    /// guest, just a parent that happens to move.
    /// </summary>
    private sealed class SplineRuntime
    {
        public FxEmitter? Def;

        /// <summary>Where along the curve, in [0, 1].</summary>
        public float T;

        /// <summary>+1 or -1. Flipped at the ends when <c>ReverseFlow</c> is set.</summary>
        public float Sign = 1f;

        /// <summary>The current point, in the effect's space, ready to add to a child's origin.</summary>
        public float[] Offset = new float[3];
    }

    /// <summary>
    /// The point at <paramref name="t"/> on a spline, in the spline's own space.
    ///
    /// UNIFORM CATMULL-ROM with REFLECTED endpoints, which is what `sub_83235928` computes:
    ///
    ///     P(u) = B + 0.5(C-A)u + (A - 2.5B + 2C - 0.5D)u^2 + (-0.5A + 1.5B - 1.5C + 0.5D)u^3
    ///
    /// An open spline reflects rather than clamps at the ends — <c>A = 2P0 - P1</c> at the start
    /// and <c>D = 2P[N-1] - P[N-2]</c> at the finish. That detail is what makes the TWO-POINT
    /// case, which is the shipped median, collapse exactly to a straight lerp: substituting both
    /// reflections cancels the quadratic and cubic terms completely. Clamping instead would bow
    /// a line that should be straight.
    /// </summary>
    private static void EvalSpline(FxEmitter d, float t, float[] outXyz)
    {
        float[][] p = d.SplinePoints;
        int n = p.Length;
        if (n == 0)
        {
            outXyz[0] = 0f;
            outXyz[1] = 0f;
            outXyz[2] = 0f;
            return;
        }

        if (n == 1 || t <= 0f)
        {
            Array.Copy(p[t <= 0f ? 0 : 0], outXyz, 3);
            return;
        }

        if (t >= 1f)
        {
            Array.Copy(p[n - 1], outXyz, 3);
            return;
        }

        bool looped = d.SplineLooped && n > 2;
        int segments = looped ? n : n - 1;
        int i = (int)(segments * t);
        if (i >= segments)
        {
            i = segments - 1;
        }

        float u = (segments * t) - i;

        float[] At;
        float[] Bt = p[i % n];
        float[] Ct;
        float[] Dt;
        if (looped)
        {
            At = p[((i - 1) % n + n) % n];
            Ct = p[(i + 1) % n];
            Dt = p[(i + 2) % n];
        }
        else
        {
            Ct = p[Math.Min(i + 1, n - 1)];
            At = i == 0 ? Reflect(p[0], p[1]) : p[i - 1];
            Dt = i + 2 <= n - 1 ? p[i + 2] : Reflect(p[n - 1], p[n - 2]);
        }

        for (int axis = 0; axis < 3; axis++)
        {
            float a = At[axis];
            float b = Bt[axis];
            float c = Ct[axis];
            float dd = Dt[axis];

            if (d.SplineStraight)
            {
                outXyz[axis] = b + ((c - b) * u);
                continue;
            }

            float c1 = 0.5f * (c - a);
            float c2 = a - (2.5f * b) + (2f * c) - (0.5f * dd);
            float c3 = (-0.5f * a) + (1.5f * b) - (1.5f * c) + (0.5f * dd);
            outXyz[axis] = b + (c1 * u) + (c2 * u * u) + (c3 * u * u * u);
        }
    }

    /// <summary>The guest's endpoint reflection: <c>2*edge - inner</c>.</summary>
    private static float[] Reflect(float[] edge, float[] inner) => new[]
    {
        (2f * edge[0]) - inner[0],
        (2f * edge[1]) - inner[1],
        (2f * edge[2]) - inner[2],
    };

    /// <summary>One of an effect's lights, evaluated at the effect's current time.</summary>
    /// <param name="Position">
    /// In the effect's own space, Godot axes — the caller's node supplies the world transform.
    /// </param>
    /// <param name="Energy">
    /// Intensity times the emitter's alive fraction, which is how the guest fades a light out as
    /// its effect runs down (`sub_8323FA50` at 0x8323fb28) rather than snapping it off.
    /// </param>
    public readonly record struct FxLightState(
        Vector3 Position,
        Color Colour,
        float Range,
        float Energy,
        float FadeDistance,
        float FadeRange);

    /// <summary>How many lights this effect carries. Fixed once <see cref="Build"/> has run.</summary>
    public int LightCount
    {
        get
        {
            int total = 0;
            foreach (InstanceState inst in _instances)
            {
                total += inst.Lights.Count;
            }

            return total;
        }
    }

    /// <summary>
    /// The <paramref name="index"/>th light as it stands this frame.
    ///
    /// Evaluated rather than stored: every one of the four values is a TIMELINE, so a brazier
    /// whose colour warms and whose range breathes does so because this is re-read each frame.
    /// </summary>
    public FxLightState LightAt(int index)
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (LightRuntime light in inst.Lights)
            {
                if (index-- != 0)
                {
                    continue;
                }

                FxEmitter? d = light.Def;
                if (d is null)
                {
                    return default;
                }

                float age = MathF.Max(0f, inst.Time - light.StartDelay);
                (float r, float g, float b) = d.LightColour.Eval(age);

                // The guest scales the colour by intensity AND by how much of the emitter's life
                // is left. With no duration stated that fraction is 1.0, which is every ambient
                // light in the game.
                float energy = d.LightIntensity.Eval(age);

                return new FxLightState(
                    ToGodot(light.Origin),
                    new Color(r, g, b),
                    MathF.Max(0f, d.LightRange.Eval(age)) * light.Scale,
                    energy,
                    d.LightFadeDistance.Eval(age),
                    d.LightFadeRange.Eval(age));
            }
        }

        return default;
    }

    private readonly List<InstanceState> _instances = new();
    private readonly List<(Vector3 Pos, Vector2 Uv, Color Col)> _scratch = new();
    private uint _rng = 0x1234567u;
    private bool _loop = true;

    /// <summary>Where the owning node was last frame, for the world-space rebase.</summary>
    private Transform3D? _previousGlobal;

    /// <summary>Whether any emitter here states <c>AttachToRoot = false</c>.</summary>
    private bool _detachedParticles;

    /// <summary>
    /// Moves every world-space particle by <paramref name="delta"/>, cancelling out the node's own
    /// movement so they hold their place in the world.
    ///
    /// The arrays are in the GAME's axes and the delta is in Godot's, so each position makes the
    /// round trip. Velocities take the rotation only — a translation of the frame does not change
    /// how fast anything is going.
    /// </summary>
    private void Rebase(Transform3D delta)
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Def is null || rt.Def.AttachToRoot)
                {
                    continue;
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    var pos = new Vector3(pt.Pos[0], pt.Pos[2], pt.Pos[1]);
                    pos = delta * pos;
                    pt.Pos[0] = pos.X;
                    pt.Pos[1] = pos.Z;
                    pt.Pos[2] = pos.Y;

                    var vel = new Vector3(pt.Vel[0], pt.Vel[2], pt.Vel[1]);
                    vel = delta.Basis * vel;
                    pt.Vel[0] = vel.X;
                    pt.Vel[1] = vel.Z;
                    pt.Vel[2] = vel.Y;

                    // The trail history is a path through the world; it has to move with the
                    // particles or a ribbon snaps back to the emitter every time the node moves.
                    for (int k = 0; k < pt.HistCount; k++)
                    {
                        float[]? h = pt.Hist[k];
                        if (h is null)
                        {
                            continue;
                        }

                        var hp = new Vector3(h[0], h[2], h[1]);
                        hp = delta * hp;
                        h[0] = hp.X;
                        h[1] = hp.Z;
                        h[2] = hp.Y;
                    }
                }
            }
        }
    }

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
        _previousGlobal = null;
        _detachedParticles = false;
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
                // A TYPE-VECTOR IS A CHAIN, IN FILE ORDER, and that chain IS the parenting.
                //
                // `sub_83233300` walks each type-vector's entities in order and calls
                // `sub_83229458(previous, new)` on every one after the first, which writes
                // `new+0xEC = previous`. There is no parent field anywhere in the file — an
                // entity's parent is simply the entity before it. `sub_83229458` has exactly two
                // callers in the whole binary and neither is gameplay code.
                //
                // It also STOPS at the first emitter: types 0 and 1 break the inner loop, so a
                // type-vector reads `[spline, spline, ...] -> emitter` and anything after that
                // emitter is never instantiated. The shipped data agrees — of 2650 type-vectors
                // only 10 triples fall after their first emitter, and the second commonest shape
                // in the bank is `(spline, emitter)`, 367 of them.
                // SEGMENTED ON THE VECTOR INDEX, NOT ON `Group`. EntityPriorities is a priority
                // bucket whose values are 1..5 and repeat freely; segmenting on it merged every
                // adjacent pair of type-vectors that shared a priority and then threw away the
                // second one's emitter at the "close after the first emitter" rule. That cost 48
                // effects their ability to sustain — caught only because the whole-bank sustain
                // count is checked against its baseline after every change.
                int vector = -1;
                SplineRuntime? chained = null;
                bool groupClosed = false;

                foreach (FxEffectPart part in effect.Parts)
                {
                    if (part.Vector != vector)
                    {
                        vector = part.Vector;
                        chained = null;
                        groupClosed = false;
                    }
                    else if (groupClosed)
                    {
                        continue;
                    }

                    if (part.Emitter < 0 || part.Emitter >= bank.Emitters.Count)
                    {
                        continue;
                    }

                    FxEmitter em = bank.Emitters[part.Emitter];

                    if (em.IsSpline)
                    {
                        var spline = new SplineRuntime
                        {
                            Def = em,

                            // -1 is the guest's own "not started" sentinel; the first update
                            // resolves it to 0 or, with UseRandomStartPoint, anywhere on the curve.
                            T = em.SplineRandomStart ? Frand() : 0f,
                        };
                        inst.Splines.Add(spline);
                        chained = spline;
                        continue;
                    }

                    // A LIGHT IS PLACED THE SAME WAY AN EMITTER IS — same base block, same
                    // position, same start time — so it takes the same offset maths and then
                    // leaves the particle path entirely.
                    if (em.IsLight)
                    {
                        var lit = new LightRuntime
                        {
                            Def = em,
                            Scale = sc,
                            StartDelay = em.StartTimeSecs,
                        };
                        Offset(place, em, sc, cy, sy, lit.Origin);
                        inst.Lights.Add(lit);
                        continue;
                    }

                    // An attractor is placed exactly like an emitter and then pulls on every
                    // particle the same effect owns, rather than spawning any of its own.
                    if (em.IsAttractor)
                    {
                        var pull = new LightRuntime
                        {
                            Def = em,
                            Scale = sc,
                            StartDelay = em.StartTimeSecs,
                        };
                        Offset(place, em, sc, cy, sy, pull.Origin);
                        inst.Attractors.Add(pull);
                        continue;
                    }

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
                        Shape = em.SubType,
                        EmitOnSurface = em.EmitOnSurface,
                        UseShapeSpawnDir = em.UseShapeSpawnDir,
                        TypeBasis = BasisFromQuat(em.Quat),
                    };
                    if (place.HasRot)
                    {
                        Array.Copy(place.Rot, rt.RotGame, 9);
                    }

                    Offset(place, em, sc, cy, sy, rt.Origin);

                    uint mode = 1;
                    if (part.System >= 0 && part.System < bank.Systems.Count)
                    {
                        FxSystemDef sys = bank.Systems[part.System];
                        mode = sys.Mode;
                        if (mode is >= 1 and <= 4)
                        {
                            // MODE 2 IS A RIBBON, NOT A BILLBOARD — and it is 131 of the bank's
                            // 2231 systems, against 6 for mode 3.
                            //
                            // `Mode` is `CMaterialTimeline::MaterialType`, and the draw dispatcher
                            // `sub_821CA5E8` switches on it directly (0x821CA6B4 onward):
                            //
                            //     lwz   r11, 0xF0(r4)
                            //     cmpwi r11, 1 ; beq -> quad list          (billboard)
                            //     cmpwi r11, 2 ; beq -> Render_EffectStripDraw
                            //     cmpwi r11, 3 ; bne -> return            (nothing else draws)
                            //                    ... Render_ParticleStripDraw
                            //
                            // Both of the strip draws issue TRIANGLESTRIP over the particle's
                            // position history; only mode 1 expands four corners per particle.
                            // Drawing a mode-2 system as a camera-facing quad turns every ribbon
                            // — trails, streaks, the drawn-out part of a splash — into a clump of
                            // sprites sitting at the head of the path.
                            //
                            // Mode 4 is absent from the dispatcher entirely: it is mesh instancing,
                            // which the visuals' own mesh tag already routes for us.
                            rt.Trail = mode is 2 or 3;
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

                                VisualRuntime v = BuildVisual(vis, sc);
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

                    // The spline this emitter was chained behind, if any. Its point on the curve
                    // is added to the emitter's own offset every frame.
                    rt.Spline = chained;
                    Array.Copy(rt.Origin, rt.BaseOrigin, 3);

                    if (!em.AttachToRoot)
                    {
                        _detachedParticles = true;
                    }

                    // An emitter closes its type-vector.
                    groupClosed = true;
                    inst.Emitters.Add(rt);
                }
            }

            // A LIGHT ALONE IS A RESOLVED EFFECT. Not every effect draws particles — an effect
            // whose whole job is to light the room around a fire carries one CPointLightEntityType
            // and no emitter at all, and gating on emitters would report it unresolved and place
            // nothing.
            if (inst.Emitters.Count != 0 || inst.Lights.Count != 0)
            {
                place.Resolved = true;
                ResolvedCount++;
                _instances.Add(inst);
            }
        }
    }

    /// <summary>
    /// Where one part of an effect sits, in the effect's own space: the base block's position,
    /// turned by the placement's rotation and scaled.
    ///
    /// Shared by the emitters and the lights because the guest shares it — both are
    /// <c>CParticleSystemEntityType</c>s and both take their position out of the same base block,
    /// so a light that used its own copy of this would drift from the flame it belongs to the
    /// first time either was corrected.
    /// </summary>
    private static void Offset(
        FxPlacement place, FxEmitter em, float sc, float cy, float sy, float[] origin)
    {
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

        origin[0] = place.Pos[0] + (lx * sc);
        origin[1] = place.Pos[1] + (ly * sc);
        origin[2] = place.Pos[2] + (lz * sc);
    }

    /// <summary>
    /// Turns a vector by the placement's rotation — the same rotation <see cref="Offset"/> applies
    /// to an emitter's own position, kept in step with it deliberately: the spline point and the
    /// emitter offset are added together, so they must arrive in the same frame.
    /// </summary>
    private static void RotateLikePlacement(
        EmitterRuntime rt, float x, float y, float z,
        out float ox, out float oy, out float oz)
    {
        if (rt.HasRot)
        {
            float[] m = rt.RotGame;
            ox = (m[0] * x) + (m[1] * y) + (m[2] * z);
            oy = (m[3] * x) + (m[4] * y) + (m[5] * z);
            oz = (m[6] * x) + (m[7] * y) + (m[8] * z);
            return;
        }

        ox = (x * rt.YawCos) - (y * rt.YawSin);
        oy = (x * rt.YawSin) + (y * rt.YawCos);
        oz = z;
    }

    private static VisualRuntime BuildVisual(FxVisual vis, float sc)
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

        // A mode-2 system states no 2-D `Dimensions` because it is a RIBBON — its width is the
        // scalar `TrailSize`, which `TrailWidth` above already reads. This branch used to give it
        // a square billboard half-extent from that scalar, which was the right compensation while
        // mode 2 was drawn as a quad and is dead weight now that it is a strip. Kept out of the
        // size ladder entirely rather than left to feed a code path that no longer runs.
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

        // THE EMITTER SHAPE. SubType is the header's EmitterShape enum and 1 is NOT 2: sphere and
        // disc are different volumes, and treating them alike put 679 of the bank's emitters in
        // the wrong place. `sub_8322A9B0`'s switch at 0x8322AA8C installs a different vtable per
        // value; only the samplers below are reachable from a standard emitter.
        //
        // NOTE THE RADIUS DISTRIBUTION. Both samplers take `r = rand01 * R` — uniform in RADIUS —
        // which concentrates particles toward the centre. The textbook cube-root (sphere) and
        // square-root (disc) corrections that give a uniform DENSITY are exactly what the guest
        // does not do, so applying them would spread every puff wider than the author saw. The
        // rejection sampling this replaced was the uniform-density version.
        float shapeX = 0f;
        float shapeY = 0f;
        float shapeZ = 0f;
        bool radial = false;

        if (rt.SpawnRadius > 0f && rt.Shape is 1 or 2)
        {
            float radius = rt.EmitOnSurface ? rt.SpawnRadius : Frand() * rt.SpawnRadius;
            float phiShape = Frand() * MathF.Tau;

            if (rt.Shape == 1)
            {
                // Sphere — direction uniform on the unit sphere. `sub_83224630`: z is uniform in
                // [-1,1) (0x832246E4), then s = sqrt(1 - z*z) scales the azimuth (0x83224764).
                float z = (Frand() * 2f) - 1f;
                float s = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
                shapeX = MathF.Cos(phiShape) * s * radius;
                shapeY = MathF.Sin(phiShape) * s * radius;
                shapeZ = z * radius;
            }
            else
            {
                // Disc — a circle in the emitter's local XY plane, normal +Z. `sub_832240C0`
                // forces out.z = 0 at 0x832240F8.
                shapeX = MathF.Cos(phiShape) * radius;
                shapeY = MathF.Sin(phiShape) * radius;
                shapeZ = 0f;
            }

            radial = rt.UseShapeSpawnDir;
        }

        // The shape offset is in the emitter's LOCAL axes, so it turns with the emitter before it
        // is added to the origin — the same basis the spawn direction goes through below.
        if (shapeX != 0f || shapeY != 0f || shapeZ != 0f)
        {
            LocalToEffect(rt, shapeX, shapeY, shapeZ, out float wx, out float wy, out float wz);
            pt.Pos[0] = rt.Origin[0] + (wx * rt.Scale);
            pt.Pos[1] = rt.Origin[1] + (wy * rt.Scale);
            pt.Pos[2] = rt.Origin[2] + (wz * rt.Scale);
        }
        else
        {
            Array.Copy(rt.Origin, pt.Pos, 3);
        }

        // THE SPAWN DIRECTION, AND WHERE IT POINTS WHEN NOBODY STATES ONE.
        //
        // `sub_8322CAF0` builds it from three generic parameters and one shared azimuth:
        //
        //     dir.x = param9  * cos(theta)      /*0x8322cba8 -> 0x8322cbc0*/
        //     dir.y = param10 * sin(theta)      /*0x8322cbf8 -> 0x8322cc10*/
        //     dir.z = param11                   /*0x8322cc3c -> 0x8322cc48*/
        //
        // — each inserted into its own lane by the vperm masks at 0x82099170, 0x82099180 and
        // 0x82099190, whose bytes select lane 0, 1 and 2 respectively. That is exactly the
        // construction below, so the shape was right.
        //
        // WHAT WAS WRONG IS THE DEGENERATE CASE, and it is the commonest case in the game.
        // `vmsum3fp128 v7, v0, v0` takes the SQUARED length and the guest branches on it:
        //
        //     if (len2 >= 1.1920929e-7)  normalise            /*0x8322cc94, 0x8322ccc0*/
        //     else { v69[0] = 1.0;                            /*0x8322cc9c*/
        //            vspltw v12, v13, 0                       // splat 1.0 across all four lanes
        //            vand   v11, v12, v0 }                    // AND with the mask at 0x82099150
        //
        // The mask at 0x82099150 reads `00000000 00000000 ffffffff 00000000` — lane 2 alone
        // survives — so the fallback direction is (0, 0, 1): the emitter's local +Z, which is UP
        // in the game's Z-up space.
        //
        // This host used (1, 0, 0). Params 9, 10 and 11 all default to 0.0 in the table at
        // 0x8331DA20, and 977 of the bank's 2277 standard emitters state a real Speed and no
        // direction at all — so 43% of every emitter in the game was firing a jet SIDEWAYS along
        // +X where the author asked for a column straight up. Fires, torch flames, steam and
        // smoke all take this path.
        float phi = Frand() * MathF.Tau;
        float gx = 0f;
        float gy = 0f;
        float gz = 1f;
        if (d is not null)
        {
            gx = d.VelX.Eval(rt.Age) * MathF.Cos(phi);
            gy = d.VelY.Eval(rt.Age) * MathF.Sin(phi);
            gz = d.VelZ.Eval(rt.Age);

            // The guest's own threshold, against the SQUARED length as it tests it.
            float len2 = (gx * gx) + (gy * gy) + (gz * gz);
            if (len2 >= 1.1920929e-7f)
            {
                float inv = 1f / MathF.Sqrt(len2);
                gx *= inv;
                gy *= inv;
                gz *= inv;
            }
            else
            {
                gx = 0f;
                gy = 0f;
                gz = 1f;
            }

            // THE CONE IS FILLED, NOT HOLLOW — and this second stage is the whole difference.
            //
            // What the three parameters build above is the cone's EDGE. `sub_8322CAF0` then
            // rotates that edge vector back toward the +Z axis by a fresh random polar angle:
            //
            //     psi  = acos(clamp(dir.z, -1, 1)) * rand01        /*0x8322ce10, 0x8322ce84*/
            //     perp = normalize((0,0,1) - dir*dir.z)            /*0x8322ce4c, 0x8322cea4*/
            //     dir  = dir*cos(psi) + perp*sin(psi)              /*0x8322cef0*/
            //
            // acos(dir.z) IS the cone's half-angle — there is no separate spread parameter — and
            // psi uniform in [0, half-angle] fills the interior. Without it every emitter in the
            // game sprays a hollow shell with nothing down the middle, which reads as a ring or a
            // hoop where a plume was authored.
            if (gz <= 0.9995f)
            {
                float half = MathF.Acos(Math.Clamp(gz, -1f, 1f));
                float psi = half * Frand();

                // perp = normalize(+Z - dir*dir.z), which for a unit dir is (-x*z, -y*z, 1-z*z).
                float px = -gx * gz;
                float py = -gy * gz;
                float pz = 1f - (gz * gz);
                float pl2 = (px * px) + (py * py) + (pz * pz);
                if (pl2 >= 1.1920929e-7f)
                {
                    float pinv = 1f / MathF.Sqrt(pl2);
                    px *= pinv;
                    py *= pinv;
                    pz *= pinv;

                    float cp = MathF.Cos(psi);
                    float sp = MathF.Sin(psi);
                    gx = (gx * cp) + (px * sp);
                    gy = (gy * cp) + (py * sp);
                    gz = (gz * cp) + (pz * sp);
                }
            }
            else
            {
                // The near-degenerate branch the guest takes for an almost-collimated cone:
                // lerp toward +Z, then renormalise. /*0x8322cd90*/
                float t = Frand();
                gx += (0f - gx) * t;
                gy += (0f - gy) * t;
                gz += (1f - gz) * t;
                float nl2 = (gx * gx) + (gy * gy) + (gz * gz);
                if (nl2 >= 1.1920929e-7f)
                {
                    float ninv = 1f / MathF.Sqrt(nl2);
                    gx *= ninv;
                    gy *= ninv;
                    gz *= ninv;
                }
            }

            // UseShapeSpawnDir replaces the cone outright: the particle leaves radially from the
            // shape's centre. /*0x82A04314 branches past the cone builder*/
            if (radial)
            {
                float rl2 = (shapeX * shapeX) + (shapeY * shapeY) + (shapeZ * shapeZ);
                if (rl2 >= 1.1920929e-7f)
                {
                    float rinv = 1f / MathF.Sqrt(rl2);
                    gx = shapeX * rinv;
                    gy = shapeY * rinv;
                    gz = shapeZ * rinv;
                }
            }

            // SpeedSpread is ONE-SIDED and ADDITIVE — [0, spread), not +/-. `sub_82A04208`:
            // mag = P4(t) + rand01 * P5(t), applied AFTER the direction is normalised.
            float spd = d.Speed.Eval(rt.Age) + (Frand() * d.SpeedSpread.Eval(rt.Age));
            gx *= spd;
            gy *= spd;
            gz *= spd;
        }

        // THE EMITTER'S OWN ORIENTATION FIRST, then the placement's. The spawn direction is built
        // in the emitter's local axes and multiplied through rows 0..2 of the emitter frame at the
        // tail of `sub_8322CAF0`; the placement rotation is the outer hop that frame chain ends at.
        LocalToEffect(rt, gx, gy, gz, out gx, out gy, out gz);

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

    /// <summary>
    /// Advances the effect. <paramref name="nodeGlobal"/> is the owning node's world transform,
    /// and is only needed when that node can MOVE.
    /// </summary>
    public void Update(float dt, Transform3D? nodeGlobal = null)
    {
        if (dt <= 0f)
        {
            dt = 1f / 60f;
        }

        dt = MathF.Min(dt, 0.1f);

        // WHAT `AttachToRoot = false` MEANS, once the effect is capable of moving.
        //
        // Particles here live in the owning node's space, which is exactly the guest's
        // AttachToRoot=TRUE behaviour: the container is re-transformed by its ancestor every frame
        // and everything follows rigidly. For the 1667 emitters that state FALSE the guest instead
        // bakes the spawn transform in and hands the container IDENTITY, so the particles stay
        // where they were born and a moving emitter leaves them behind.
        //
        // Rather than keep two storage spaces, the node's movement is UNDONE on those particles:
        // shifting them by the inverse of this frame's node delta leaves their world position
        // exactly where it was, which is the same result. A node that does not move — every static
        // level effect — produces an identity delta and costs nothing.
        if (nodeGlobal is { } current)
        {
            if (_previousGlobal is { } previous && _detachedParticles)
            {
                Transform3D delta = current.AffineInverse() * previous;
                if (!delta.IsEqualApprox(Transform3D.Identity))
                {
                    Rebase(delta);
                }
            }

            _previousGlobal = current;
        }
        foreach (InstanceState inst in _instances)
        {
            inst.Time += dt;

            // THE SPLINES FIRST, because the emitters that ride them read the result. `t` steps by
            // `dt * Rate * sign`; at either end ReverseFlow flips the sign to ping-pong and its
            // absence snaps back to 0 (`sub_8323CEF0`).
            foreach (SplineRuntime spline in inst.Splines)
            {
                FxEmitter? sd = spline.Def;
                if (sd is null || sd.SplinePoints.Length == 0)
                {
                    continue;
                }

                // RATE IS PER 30 Hz TICK, NOT PER SECOND — the same tick base the emission rate
                // uses, which is why `RateToPerSec` already exists on this class.
                //
                // `sub_8323CEF0` steps `t += flt_8335F1D4[mode] * Rate * sign`, and that constant
                // is TICKS CONSUMED PER SIM STEP: 2.0 at 15 Hz, 1.0 at 30 Hz, so either way the
                // curve advances by Rate thirty times a second. Reading it as t-per-second made
                // every spline in the game run 30x too slow — the beehives' six looped paths take
                // 4 seconds each in the original and were taking well over two minutes here, which
                // reads as bees hanging motionless in the air rather than circling.
                spline.T += dt * sd.SplineRate * RateToPerSec * spline.Sign;
                if (spline.T < 0f || spline.T > 1f)
                {
                    if (sd.SplineReverseFlow)
                    {
                        spline.Sign = -spline.Sign;
                    }
                    else
                    {
                        spline.T = 0f;
                    }
                }

                spline.T = Math.Clamp(spline.T, 0f, 1f);

                // RandomGenerator does not travel the curve at all — it re-rolls `t` every
                // evaluation, scattering the point over the whole path.
                float at = sd.SplineRandomGenerator ? Frand() : spline.T;
                EvalSpline(sd, at, spline.Offset);
            }

            foreach (EmitterRuntime rt in inst.Emitters)
            {
                // The emitter's own offset plus wherever its parent spline has travelled to.
                if (rt.Spline is { Def: not null } ridden)
                {
                    // THROUGH THE PLACEMENT'S ROTATION, exactly as the emitter's own offset goes.
                    // The point comes out of the curve in the effect's local space; adding it raw
                    // leaves every rotated fixture flying its path in the unrotated orientation,
                    // so a beehive turned to face a wall sends its bees off in the original
                    // authoring direction instead.
                    RotateLikePlacement(
                        rt, ridden.Offset[0], ridden.Offset[1], ridden.Offset[2],
                        out float sx, out float sy, out float sz);

                    rt.Origin[0] = rt.BaseOrigin[0] + (sx * rt.Scale);
                    rt.Origin[1] = rt.BaseOrigin[1] + (sy * rt.Scale);
                    rt.Origin[2] = rt.BaseOrigin[2] + (sz * rt.Scale);
                }

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

                // ONE ACCELERATION PER EMITTER PER FRAME, shared by every particle it owns —
                // `sub_8322D988` assembles it once into the global at 0x8349E5D0 and
                // `sub_8322CF40` only ever adds that one vector, evaluated at the EMITTER's age
                // rather than each particle's.
                //
                // Of its three terms only gravity is live for us. The authored-direction term
                // needs vector timeline 0, which we skip; the global-wind term scales a vector
                // the weather system owns and that we never set, so it is (0,0,0) and drops out.
                float accZ = 0f;
                if (d is not null)
                {
                    float gravity = d.GravityScale.Eval(rt.Age);
                    if (gravity != 0f && float.IsFinite(gravity))
                    {
                        accZ = Gravity * gravity;
                    }
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    pt.Age += dt;

                    // Semi-implicit Euler, the guest's own order: velocity takes the acceleration
                    // first and position integrates the NEW velocity (`vel += acc; pos += vel` at
                    // 0x8322cf78 then 0x8322d08c). Integrating the old velocity instead lags every
                    // falling particle by a frame and shortens its arc.
                    pt.Vel[2] += accZ * dt;

                    // THE EFFECT'S ATTRACTORS, summed per particle. `sub_83229A98` adds every
                    // attractor's contribution before the damping and the position step, so they
                    // ride in beside gravity rather than replacing it.
                    foreach (LightRuntime pull in inst.Attractors)
                    {
                        FxEmitter? pd = pull.Def;
                        if (pd is null)
                        {
                            continue;
                        }

                        float pullAge = MathF.Max(0f, inst.Time - pull.StartDelay);
                        float radius = pd.AttractRadius.Eval(pullAge) * pull.Scale;
                        if (!(radius > 1e-4f))
                        {
                            continue;
                        }

                        float dx = pt.Pos[0] - pull.Origin[0];
                        float dy = pt.Pos[1] - pull.Origin[1];
                        float dz = pt.Pos[2] - pull.Origin[2];
                        float d2 = (dx * dx) + (dy * dy) + (dz * dz);

                        // A HARD CUTOFF, tested on the SQUARE — `if (d2 <= R*R)` at 0x8323c120.
                        // Outside the radius an attractor contributes exactly nothing, which is
                        // what keeps one effect's vortex from reaching across a room.
                        if (d2 > radius * radius)
                        {
                            continue;
                        }

                        float distance = MathF.Sqrt(d2);
                        if (distance < 1e-5f)
                        {
                            continue;
                        }

                        // Linear ramp to zero at the rim, and NEGATED — the guest builds the
                        // particle-to-attractor delta and then flips it (0x8323c2fc), so a
                        // positive strength pulls inward.
                        float force = pd.AttractStrength.Eval(pullAge) * (1f - (distance / radius));
                        float scale = -force * dt / distance;
                        pt.Vel[0] += dx * scale;
                        pt.Vel[1] += dy * scale;
                        pt.Vel[2] += dz * scale;
                    }

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
