using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Fx;

/// <summary>
/// `fx_cpu_fastpaths` (<see cref="Engine_FxFastPaths"/>) in the effect system: the state and
/// steps only the fast step and emit use. Engine_FxSystem.cs calls each one where it stood,
/// behind its reading of the switch.
/// </summary>
public sealed partial class Engine_FxSystem
{
    private sealed partial class EmitterRuntime
    {
        /// <summary>
        /// The highest alpha any of <see cref="Visuals"/> can draw a particle at before its fade
        /// (<see cref="Engine_FxFastPaths.AlphaCeiling"/>); NaN until the fast emit first asks.
        /// </summary>
        public float AlphaCeiling = float.NaN;

        /// <summary>
        /// The particles as the last step left them, for the fast emit's whole-emitter test
        /// (<see cref="DrawsNothing"/>): their box in Godot's axes, whether every coordinate is
        /// finite and every intensity zero. Written by a step taken with the fast paths on, for
        /// <see cref="SummaryCount"/> particles; any other step and a rebase drop it.
        /// </summary>
        public bool SummaryValid;
        public int SummaryCount;
        public bool SummaryFinite;
        public bool SummaryDark;
        public Vector3 SummaryLo;
        public Vector3 SummaryHi;
    }

    private sealed partial class SurfaceSlots
    {
        /// <summary>The fast emit's box, as its exact corners until the surfaces end (<see cref="Engine_FxFastPaths.Bounds"/>).</summary>
        public Vector3 Lo;
        public Vector3 Hi;

        /// <summary>
        /// Which step-alpha uniforms the surfaces' moving vertices read: bit 0 full rate, bit 1
        /// half rate (<see cref="MotionClasses"/>). Both until a fast emit has looked.
        /// </summary>
        public int Motion = 3;
    }

    /// <summary>Of <see cref="SkipZeroAlpha"/>, those the fast emit rejected on fade × intensity alone.</summary>
    public static long SkipEarly;

    /// <summary>Emitters the fast emit passed over whole: every particle dark or past the fade.</summary>
    public static long SkipEmitters;

    /// <summary><see cref="Engine_FxFastPaths.Enabled"/> as the emit in progress read it.</summary>
    private bool _emitFast;

    /// <summary>The fast sort's output and packed keys (<see cref="Engine_FxFastPaths.SortQuadsBackToFront"/>), shared the same way.</summary>
    private static readonly List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> _fastSorted = new();
    private static ulong[] _sortKeys = System.Array.Empty<ulong>();

    /// <summary>
    /// Which of the step-alpha uniforms the last emit's surfaces on <paramref name="mesh"/> read:
    /// bit 0 when a full-rate vertex moved on its last step, bit 1 a half-rate one. A vertex that
    /// did not move is drawn where it is whatever the uniform says. Both (3) when the emit did not
    /// look (fast paths off); 0 for a mesh never emitted into.
    /// </summary>
    internal int MotionClasses(ArrayMesh? mesh) =>
        mesh is not null && _slots.TryGetValue(mesh, out SurfaceSlots? slots) ? slots.Motion : 0;

    /// <summary>
    /// An emitter's particles as a step taken with the fast paths on leaves them, gathered one
    /// particle at a time and stored as the emitter's summary when the step ends.
    /// </summary>
    private struct ParticleSummary
    {
        private float _lx, _ly, _lz, _ux, _uy, _uz;
        private bool _finite, _dark;

        public static ParticleSummary Begin() => new()
        {
            _lx = float.PositiveInfinity, _ly = float.PositiveInfinity, _lz = float.PositiveInfinity,
            _ux = float.NegativeInfinity, _uy = float.NegativeInfinity, _uz = float.NegativeInfinity,
            _finite = true, _dark = true,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ParticleState pt)
        {
            // Where the step left it, in Godot's axes as the emit reads it.
            float x = pt.Pos[0], y = pt.Pos[2], z = pt.Pos[1];
            _finite &= float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
            _lx = MathF.Min(_lx, x); _ly = MathF.Min(_ly, y); _lz = MathF.Min(_lz, z);
            _ux = MathF.Max(_ux, x); _uy = MathF.Max(_uy, y); _uz = MathF.Max(_uz, z);
            _dark &= pt.Intensity == 0f;
        }

        public readonly void Store(EmitterRuntime rt)
        {
            rt.SummaryLo = new Vector3(_lx, _ly, _lz);
            rt.SummaryHi = new Vector3(_ux, _uy, _uz);
            rt.SummaryFinite = _finite;
            rt.SummaryDark = _dark;
            rt.SummaryCount = rt.Particles.Count;
            rt.SummaryValid = true;
        }
    }

    /// <summary>The fast emit's ceiling on this emitter's keyframe alpha, folded once per emitter.</summary>
    private static float FastAlphaCeiling(EmitterRuntime rt)
    {
        int nvis = rt.Visuals.Count;
        if (nvis == 0) return float.PositiveInfinity;
        if (float.IsNaN(rt.AlphaCeiling))
        {
            float[] alphas = new float[nvis];
            for (int i = 0; i < nvis; i++) alphas[i] = rt.Visuals[i].Alpha;
            rt.AlphaCeiling = Engine_FxFastPaths.AlphaCeiling(alphas);
        }

        return rt.AlphaCeiling;
    }

    /// <summary>
    /// FAST, WHOLE EMITTER: every particle dark, or all of them past the fade-out, by the box the
    /// last step left them in. Each would be rejected on its fade term (a ribbon's every point at
    /// alpha zero), so none of them is read; they are counted as those rejections.
    /// </summary>
    private bool FastSkipsEmitter(EmitterRuntime rt, Vector3 cameraPosition, float alphaCeiling)
    {
        if (!DrawsNothing(rt, cameraPosition, alphaCeiling))
        {
            return false;
        }

        ++SkipEmitters;
        if (!rt.Ribbon)
        {
            SkipZeroAlpha += rt.Particles.Count;
            SkipEarly += rt.Particles.Count;
        }

        return true;
    }

    /// <summary>
    /// Whether no particle of the emitter can be drawn from <paramref name="cam"/>, from the
    /// summary its last step wrote: each one's fade × intensity term is then exactly zero, which
    /// with a finite <paramref name="ceiling"/> rejects it (and zeroes a ribbon's every point).
    /// Dark: every intensity zero against a finite fade. Past the fade-out: the box's nearest point
    /// beyond the far fade with rounding to spare.
    /// </summary>
    private bool DrawsNothing(EmitterRuntime rt, Vector3 cam, float ceiling)
    {
        if (!rt.SummaryValid || rt.SummaryCount != rt.Particles.Count || rt.SummaryCount == 0 ||
            !rt.SummaryFinite || !float.IsFinite(ceiling) || !cam.IsFinite())
            return false;

        bool fades = _distanceFade && rt.Def is not null;
        float[] fade = rt.Fade;
        if (fades && !(float.IsFinite(fade[0]) && float.IsFinite(fade[1]) && float.IsFinite(fade[2]) && float.IsFinite(fade[3])))
            return false;
        if (rt.SummaryDark) return true;
        return fades && FadeOut && Engine_FxFastPaths.BeyondFadeOut(rt.SummaryLo, rt.SummaryHi, cam, fade[3]);
    }

    /// <summary>
    /// FAST: the fade × intensity term first. It is the same product the alpha takes after it,
    /// and against the emitter's highest keyframe alpha it alone rejects most particles (an effect
    /// at zero intensity, one past its fade) before a keyframe is even chosen.
    /// </summary>
    /// <param name="fadeTerm">The term, for <see cref="FastRejectsAlpha"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool FastRejectsFade(EmitterRuntime rt, Vector3 ppos, Vector3 cameraPosition, float intensity, float alphaCeiling, out float fadeTerm)
    {
        fadeTerm = DistanceFadeFactor(rt, ppos, cameraPosition) * intensity;
        if (Engine_FxFastPaths.RejectedByFade(alphaCeiling, fadeTerm))
        {
            ++SkipZeroAlpha;
            ++SkipEarly;
            return true;
        }

        return false;
    }

    /// <summary>
    /// FAST: the whole alpha test before the size and colour; a particle that passes has the same
    /// alpha bit for bit (`a × term` either way). One that fails here counts as zero alpha even
    /// when its size is zero too.
    /// </summary>
    /// <param name="keyAlpha">The keyframe alpha, lerped as the original path lerps it.</param>
    /// <param name="a">The particle's alpha.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FastRejectsAlpha(float keyAlpha, float fadeTerm, out float a)
    {
        a = keyAlpha * fadeTerm;
        if (a <= 0.003f)
        {
            ++SkipZeroAlpha;
            return true;
        }

        return false;
    }

    /// <summary>
    /// FAST: only a batch that is drawn and whose blend depends on order is sorted, and into the
    /// scratch list rather than back into the batch.
    /// </summary>
    private static List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> FastSorted(
        List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list, string mode, Vector3 cameraPosition, bool sceneQuads) =>
        (sceneQuads || mode == "disp") && !Engine_FxFastPaths.OrderIndependent(mode)
            ? Engine_FxFastPaths.SortQuadsBackToFront(list, cameraPosition, _fastSorted, _order, ref _sortKeys)
            : list;

    /// <summary>FAST: plain minimum and maximum, one square root per batch. Always bounds the batch.</summary>
    private static bool FastBounds(SurfaceSlots slots, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list)
    {
        Engine_FxFastPaths.Bounds(CollectionsMarshal.AsSpan(list),
            ref slots.Lo, ref slots.Hi, ref slots.HasBounds, ref slots.MaxOffset, ref slots.Motion);
        return true;
    }

    /// <summary>The fast emit's exact corners as the surfaces' box, which the end of the emit states.</summary>
    private static void EndFastBounds(SurfaceSlots slots)
    {
        if (slots.HasBounds)
        {
            slots.Bounds = new Aabb(slots.Lo, slots.Hi - slots.Lo);
        }
    }
}
