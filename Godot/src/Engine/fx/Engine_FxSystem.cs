using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

/// <summary>
/// The Modern renderer's native particle simulation and geometry builder, used by <see cref="FableFx"/>.
/// </summary>
/// <remarks>
/// SEPARATION OF CONCERNS BETWEEN RENDERER MODES.
/// - Modern mode (<c>Engine_ClassicMaterial.Active == false</c>) uses <see cref="FableFx"/> and
///   <see cref="Engine_FxSystem"/> exclusively across the full 0..200m range: 30 Hz simulation,
///   persistent particle state with GPU sub-step interpolation (<c>fx_billboard.gdshaderinc</c>),
///   unclamped 32-bit float linear HDR vertex colours (<c>CUSTOM1</c>), dynamic FX lights,
///   ribbons/trails, and mod/recipe overlays.
/// - Classic mode (<c>Engine_ClassicMaterial.Active == true</c>) uses
///   <see cref="Refii.Models.Engine_ClassicParticles"/> exclusively to render the guest's 50m
///   <c>CParticleGroupRenderTwin</c> quad streams with translated Xenos microcode shaders.
/// - Both systems remain strictly separated at runtime while sharing pure static math, matrix
///   decomposition, and keyframe-table helpers hosted on this class.
/// </remarks>
public sealed partial class Engine_FxSystem
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
        /// Where the particle was at the start of the last simulation step (or its birth point on
        /// its first step). The billboard shader draws between <see cref="PrevPos"/> and
        /// <see cref="Pos"/> by the effect's sub-step fraction, so particles move at the display's
        /// refresh rate while the simulation keeps the authored 30 frames a second.
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

        /// <summary>
        /// A mesh particle's birth orientation (<see cref="FxVisual.MeshAlign"/>, identity when the
        /// visual states none): the emitter's rotation × the align (the guest's particle+0x30), the
        /// axis in the visual's own frame and its initial angle (the guest's +0x40).
        /// <see cref="HasBase"/> says whether this particle was born under a mesh visual.
        /// </summary>
        public bool HasBase;
        public float[]? Base;
        public float[] AxisBody = new float[3];
        public float Rot0;

        /// <summary>The trail's past positions, newest first; made the first time a trail emitter spawns this particle.</summary>
        public float[][]? Hist;
        public int HistCount;

        /// <summary>Second edge point for <c>Mode == 3</c> double-trail ribbons (e.g. weapon tip opposite <see cref="Pos"/>).</summary>
        public readonly float[] EdgeB = new float[3];
        public readonly float[] PrevEdgeB = new float[3];
        public bool HasDoubleEdge;
    }

    private sealed class VisualRuntime
    {
        /// <summary>The batch this visual last drew into, at <see cref="SinkZ"/> (<see cref="SinkFor"/>).</summary>
        public List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>? Sink;
        public int SinkZ;

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

        /// <summary>A mesh visual's rest orientation as a matrix (<see cref="FxVisual.MeshAlign"/>, identity when it states none); null for a sprite.</summary>
        public float[]? MeshAlign;
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
        public float[] PlacePos = new float[3];
        public float[] PlaceRot = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        public float Age;
        public bool Global;
        public int ChainId = -1;
    }

    private sealed class LightRuntime
    {
        public FxEmitter? Def;
        public List<ChainNode> Chain = new();
        public float[] WorldPos = new float[3];
        public float[] PlacePos = new float[3];
        public float[] PlaceRot = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        public int ChainId = -1;
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

        /// <summary>The curve reads behind <see cref="Engine_FxSystem.LightAt"/>, kept while the frame and phase hold.</summary>
        public LightCurves NowCurves;
        public LightCurves PrevCurves;
    }

    /// <summary>One light's curves read at a frame and flicker phase; the same inputs give the same values.</summary>
    private struct LightCurves
    {
        public bool Valid;
        public int Frame;
        public float Phase;
        public Color Colour;
        public float Range, Energy, Cutoff, Ramp, Cone, BaseRange;
    }

    private sealed partial class EmitterRuntime
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
        public bool HasWorldTrailOverride;
        public int BeamIndex = -1;
        public float[][]? BeamSplineOverride;
        public int BeamSplineSerial;
        public bool BeamSplineFresh;
        public List<float[]> BeamBase = new();
        public List<float[]> BeamPts = new();
        public List<float[]> BeamWander = new();
        public float BeamSegLen;

        /// <summary>The spline serial this beam's base was built from.</summary>
        public int BeamSerial = -1;
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

        /// <summary>The effect's authored tint (<see cref="FxEffect.Tint"/>), beside the node's.</summary>
        public float[] Tint = { 1f, 1f, 1f };
        public float Time;
        public int Frame;
        public int Step = 1;
        public float AuthoredStep = 1f;

        /// <summary>The size scale also scales launch speed and each chain node's offset
        /// (<see cref="FxBank.ScaleCarriesMotion"/>).</summary>
        public bool ScaleCarriesMotion;
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
    private bool _emitting = true;
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
    public bool HasDetachedParticles => _detachedParticles;
    public void SetLoop(bool loop) => _loop = loop;
    public void SetEmitting(bool emitting) => _emitting = emitting;

    /// <summary>
    /// Returns whether an active particle should expire on this step, ensuring infinite-loop
    /// (<c>LoopCount == -1</c>) emitters drain cleanly once <paramref name="emitting"/> turns false.
    /// </summary>
    public static bool ShouldParticleExpire(
        bool emitting,
        int loopCount,
        bool useLife,
        float emitterLifetimeFrames,
        float particleAge,
        float particleLife,
        float loopEndFrames,
        int loopIter)
    {
        if (loopCount != -1)
        {
            return particleAge >= particleLife;
        }

        bool lifeExpired = useLife && emitterLifetimeFrames <= (loopIter + 1) * particleAge;
        if (emitting)
        {
            return lifeExpired;
        }

        float drainEnd = particleLife > 0f
            ? particleLife
            : (loopEndFrames > 0f
                ? loopEndFrames
                : (useLife && emitterLifetimeFrames > 0f ? emitterLifetimeFrames : 1f));
        return particleAge >= drainEnd || lifeExpired;
    }

    /// <summary>
    /// Computes an individual particle's lifespan in authored frames (30 Hz):
    /// infinitely looping systems (<paramref name="loopCount"/> == -1) use the emitter lifetime when
    /// <paramref name="useLife"/> is set, whereas non-looping systems use the visual system's
    /// <paramref name="systemLifeFrames"/> (<c>LastStartFrames</c>) with fallback to the emitter
    /// lifetime only if the system has no positive frame span.
    /// </summary>
    public static float ComputeParticleLife(
        bool useLife, float emitterLifetimeFrames, int loopCount, float systemLifeFrames) =>
        loopCount == -1
            ? (useLife ? emitterLifetimeFrames : -1f)
            : (systemLifeFrames > 0f ? systemLifeFrames : (useLife ? emitterLifetimeFrames : -1f));

    /// <summary>
    /// The instance intensity new particles are born with — the guest's <c>handle+52</c>,
    /// stored per element at +408 by <c>sub_832323E0</c> and read by the spawn routines
    /// (<c>sub_82A03D58</c> at 0x82a04090) into particle+0x48. Particles already born keep
    /// theirs, which is what makes a change travel rather than snap.
    /// </summary>
    public void SetIntensity(float intensity) =>
        _intensity = float.IsFinite(intensity) ? MathF.Max(intensity, 0f) : 1f;

    /// <summary>A multiplier on every particle's colour after lighting — an FX recipe's tint.</summary>
    private readonly float[] _tint = { 1f, 1f, 1f };

    public void SetTint(float r, float g, float b)
    {
        _tint[0] = float.IsFinite(r) ? MathF.Max(r, 0f) : 1f;
        _tint[1] = float.IsFinite(g) ? MathF.Max(g, 0f) : 1f;
        _tint[2] = float.IsFinite(b) ? MathF.Max(b, 0f) : 1f;
    }

    /// <summary>
    /// A multiplier on every particle's alpha at draw time — a title's stated alpha (Fable 3's
    /// SetAlpha, uploaded with each draw), so it reaches particles already born.
    /// </summary>
    private float _liveAlpha = 1f;

    public void SetLiveAlpha(float alpha) =>
        _liveAlpha = float.IsFinite(alpha) ? MathF.Max(alpha, 0f) : 1f;

    /// <summary>
    /// Every live particle of the standard emitters (type 0) dies now; emission goes on. Fable 3's
    /// flush (opcode 22, sub_831BA3D0 ages each live particle to 999999.88).
    /// </summary>
    public void FlushParticles()
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Def is not { TypeId: 0 })
                {
                    continue;
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    ReturnParticle(pt);
                }

                rt.Particles.Clear();
            }
        }
    }

    /// <summary>
    /// The saturation mask on every standard emitter's (type 0) visuals, as Fable 3's opcode 10
    /// turns ApplySaturationMask on for the effect (sub_831B5B00).
    /// </summary>
    public void ForceSaturationMask()
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Def is not { TypeId: 0 })
                {
                    continue;
                }

                foreach (VisualRuntime v in rt.Visuals)
                {
                    if (!v.SatMask)
                    {
                        v.SatMask = true;
                        v.Sink = null;
                    }
                }

                foreach (VisualRuntime v in rt.TrailVisuals)
                {
                    if (!v.SatMask)
                    {
                        v.SatMask = true;
                        v.Sink = null;
                    }
                }
            }
        }
    }

    private float _sizeScale = 1f;
    private float[][]? _splineOverride;
    private bool _hasDoubleTrailSpan;
    private bool _doubleSpanHostDriven;
    private readonly float[] _doubleSpanA = new float[3];
    private readonly float[] _doubleSpanB = new float[3];
    private bool _hasGuestDoubleCursor;
    private uint _lastGuestDoubleTail;
    private uint _lastGuestDoubleCount;
    private int _doubleSpanSeq;
    private int _lastSpawnedDoubleSpanSeq = -1;
    private bool _liveWeaponTipDirty;
    private bool _doubleSpanFresh;

    /// <summary>True when an active <c>Mode == 3</c> double-trail span is driven by a host-animated weapon entity.</summary>
    public bool IsDoubleTrailHostDriven => _hasDoubleTrailSpan && _doubleSpanHostDriven;

    private bool _hasSingleTrailHead;
    private readonly float[] _singleTrailHead = new float[3];
    private bool _hasGuestSingleCursor;
    private uint _lastGuestSingleTail;
    private uint _lastGuestSingleCount;
    private int _singleHeadSeq;
    private int _lastSpawnedSingleHeadSeq = -1;
    private bool _singleHeadFresh;

    /// <summary>
    /// One entry from a live guest <c>Mode == 3</c> double-trail ring in node-local Godot space,
    /// carrying the exact edge pair and the guest's per-slot keyframe cursor (<c>entry + 28</c>).
    /// </summary>
    public readonly record struct DoubleTrailRingPoint(Vector3 EdgeA, Vector3 EdgeB, float KeyTime);

    private const int MaxDoubleTrailRingPoints = 64;
    private readonly Vector3[] _ringPtsA = new Vector3[MaxDoubleTrailRingPoints];
    private readonly Vector3[] _ringPtsB = new Vector3[MaxDoubleTrailRingPoints];
    private readonly float[] _ringKeys = new float[MaxDoubleTrailRingPoints];
    private int _ringCount;
    private bool _ringFresh;
    private bool _ringDirty;
    private float _ringBoundRadius;

    /// <summary>True when a direct guest <c>Mode == 3</c> double-trail ring snapshot is active.</summary>
    public bool HasDoubleTrailRing => _ringCount >= 2;

    /// <summary>Number of control points currently held in the direct guest double-trail ring.</summary>
    public int DoubleTrailRingCount => _ringCount;

    /// <summary>
    /// When true, <c>Mode == 3</c> double-trail emitters wait until <see cref="SetDoubleTrailSpan"/>
    /// supplies valid endpoints rather than spawning an emitter-basis fallback quad.
    /// </summary>
    public bool RequireDoubleTrailSpan { get; set; }

    /// <summary>
    /// When true, guest-driven <c>Mode == 2</c> single-trail emitters wait until
    /// <see cref="SetSingleTrailHead"/> supplies a valid head point rather than spawning at the root.
    /// </summary>
    public bool RequireSingleTrailHead { get; set; }

    /// <summary>
    /// When true, live <c>Type 9</c> spline/beam emitters (<c>CParticleSplineEmitterNode</c>) wait
    /// until <see cref="SetBeamSpline"/>, <see cref="SetBeamSplineForType"/>, or
    /// <see cref="SetSplineOverride"/> provides a valid baseline instead of drawing a 3m fallback bar.
    /// </summary>
    public bool RequireBeamSpline { get; set; }

    /// <summary>Number of <c>Type 9</c> beam/spline emitters built in this system.</summary>
    public int BeamEmitterCount { get; private set; }

    /// <summary>True when at least one <c>Type 9</c> beam/spline emitter exists in this system.</summary>
    public bool HasBeam => BeamEmitterCount > 0;

    private bool _beamDirty;
    private float _chainBoundRadius;

    /// <summary>
    /// Dynamic bounding sphere radius (in node-local metres) covering any distant stated splines,
    /// double-trail spans, single-trail heads, or multi-root chain offsets so off-root effects
    /// (such as a 12..18 m magic circle whose root stays at the Hero's feet) are not frustum-frozen.
    /// </summary>
    public float ActiveBoundRadius
    {
        get
        {
            float maxSq = _chainBoundRadius * _chainBoundRadius;
            if (_ringCount >= 2)
            {
                float rSq = _ringBoundRadius * _ringBoundRadius;
                if (rSq > maxSq) maxSq = rSq;
            }
            if (_hasDoubleTrailSpan)
            {
                float aSq = (_doubleSpanA[0] * _doubleSpanA[0]) + (_doubleSpanA[1] * _doubleSpanA[1]) + (_doubleSpanA[2] * _doubleSpanA[2]);
                float bSq = (_doubleSpanB[0] * _doubleSpanB[0]) + (_doubleSpanB[1] * _doubleSpanB[1]) + (_doubleSpanB[2] * _doubleSpanB[2]);
                if (aSq > maxSq) maxSq = aSq;
                if (bSq > maxSq) maxSq = bSq;
            }
            if (_hasSingleTrailHead)
            {
                float hSq = (_singleTrailHead[0] * _singleTrailHead[0]) + (_singleTrailHead[1] * _singleTrailHead[1]) + (_singleTrailHead[2] * _singleTrailHead[2]);
                if (hSq > maxSq) maxSq = hSq;
            }
            if (_splineOverride is { Length: > 0 })
            {
                float firstSq = (_splineOverride[0][0] * _splineOverride[0][0]) + (_splineOverride[0][1] * _splineOverride[0][1]) + (_splineOverride[0][2] * _splineOverride[0][2]);
                int mid = _splineOverride.Length >> 1;
                float midSq = (_splineOverride[mid][0] * _splineOverride[mid][0]) + (_splineOverride[mid][1] * _splineOverride[mid][1]) + (_splineOverride[mid][2] * _splineOverride[mid][2]);
                int last = _splineOverride.Length - 1;
                float lastSq = (_splineOverride[last][0] * _splineOverride[last][0]) + (_splineOverride[last][1] * _splineOverride[last][1]) + (_splineOverride[last][2] * _splineOverride[last][2]);
                if (firstSq > maxSq) maxSq = firstSq;
                if (midSq > maxSq) maxSq = midSq;
                if (lastSq > maxSq) maxSq = lastSq;
            }
            foreach (InstanceState inst in _instances)
            {
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    if (rt.HasWorldTrailOverride && rt.Particles.Count > 0)
                    {
                        ParticleState firstPt = rt.Particles[0];
                        float fSq = (firstPt.Pos[0] * firstPt.Pos[0]) + (firstPt.Pos[1] * firstPt.Pos[1]) + (firstPt.Pos[2] * firstPt.Pos[2]);
                        if (fSq > maxSq) maxSq = fSq;
                        ParticleState lastPt = rt.Particles[rt.Particles.Count - 1];
                        float lpSq = (lastPt.Pos[0] * lastPt.Pos[0]) + (lastPt.Pos[1] * lastPt.Pos[1]) + (lastPt.Pos[2] * lastPt.Pos[2]);
                        if (lpSq > maxSq) maxSq = lpSq;
                    }
                    if (rt.BeamSplineOverride is not { Length: > 0 } bp) continue;
                    int step = Math.Max(1, bp.Length / 4);
                    for (int i = 0; i < bp.Length; i += step)
                    {
                        float pSq = (bp[i][0] * bp[i][0]) + (bp[i][1] * bp[i][1]) + (bp[i][2] * bp[i][2]);
                        if (pSq > maxSq) maxSq = pSq;
                    }
                    int last = bp.Length - 1;
                    float lSq = (bp[last][0] * bp[last][0]) + (bp[last][1] * bp[last][1]) + (bp[last][2] * bp[last][2]);
                    if (lSq > maxSq) maxSq = lSq;
                }
            }
            return maxSq > 0f ? MathF.Sqrt(maxSq) + 4f : 0f;
        }
    }

    /// <summary>Returns and clears whether any beam spline or multi-root chain moved since the last emit.</summary>
    public bool ConsumeBeamDirty()
    {
        bool dirty = _beamDirty;
        _beamDirty = false;
        return dirty;
    }

    /// <summary>True when at least one emitter in this system renders as a ribbon (<c>Mode == 2</c> or <c>3</c>).</summary>
    public bool HasRibbon { get; private set; }

    /// <summary>True when at least one <c>Mode == 3</c> double trail is a melee weapon trail.</summary>
    public bool IsWeaponDoubleTrail { get; private set; }

    /// <summary>True when at least one emitter in this system runs in <c>Mode == 3</c> (double trail).</summary>
    public bool HasDoubleTrail
    {
        get
        {
            foreach (InstanceState inst in _instances)
            {
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    if (rt.Mode == 3)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Sets the two blade/span endpoints in node-local Godot space for host-driven <c>Mode == 3</c>
    /// double-trail spawning (converting to simulation layout <c>(x, z, y)</c>), with optional guest
    /// ring cursor metadata to deduplicate 15 Hz samples and detect ring resets.
    /// </summary>
    public void SetDoubleTrailSpan(
        Vector3 nodeLocalEdgeA,
        Vector3 nodeLocalEdgeB,
        bool hasGuestCursor = false,
        uint guestTail = 0u,
        uint guestCount = 0u)
    {
        float ax = nodeLocalEdgeA.X, ay = nodeLocalEdgeA.Z, az = nodeLocalEdgeA.Y;
        float bx = nodeLocalEdgeB.X, by = nodeLocalEdgeB.Z, bz = nodeLocalEdgeB.Y;

        if (hasGuestCursor)
        {
            if (_doubleSpanHostDriven)
            {
                _hasGuestDoubleCursor = false;
            }
            _doubleSpanHostDriven = false;
            if (_hasGuestDoubleCursor && (guestCount < _lastGuestDoubleCount || guestCount <= 1u))
            {
                ResetTrails(modeFilter: 3);
            }

            float dax = ax - _doubleSpanA[0], day = ay - _doubleSpanA[1], daz = az - _doubleSpanA[2];
            float dbx = bx - _doubleSpanB[0], dby = by - _doubleSpanB[1], dbz = bz - _doubleSpanB[2];
            bool advanced = !_hasGuestDoubleCursor ||
                            guestTail != _lastGuestDoubleTail ||
                            guestCount != _lastGuestDoubleCount ||
                            (dax * dax) + (day * day) + (daz * daz) + (dbx * dbx) + (dby * dby) + (dbz * dbz) > 1e-6f;
            if (advanced)
            {
                ++_doubleSpanSeq;
            }

            _hasGuestDoubleCursor = true;
            _lastGuestDoubleTail = guestTail;
            _lastGuestDoubleCount = guestCount;
        }
        else
        {
            _doubleSpanHostDriven = true;
            float dax = ax - _doubleSpanA[0], day = ay - _doubleSpanA[1], daz = az - _doubleSpanA[2];
            float dbx = bx - _doubleSpanB[0], dby = by - _doubleSpanB[1], dbz = bz - _doubleSpanB[2];
            if (_hasDoubleTrailSpan && IsWeaponDoubleTrail &&
                ((dax * dax) + (day * day) + (daz * daz) + (dbx * dbx) + (dby * dby) + (dbz * dbz)) > 1e-8f)
            {
                _liveWeaponTipDirty = true;
            }

            _hasGuestDoubleCursor = false;
            ++_doubleSpanSeq;
        }

        _doubleSpanA[0] = ax; _doubleSpanA[1] = ay; _doubleSpanA[2] = az;
        _doubleSpanB[0] = bx; _doubleSpanB[1] = by; _doubleSpanB[2] = bz;
        _hasDoubleTrailSpan = true;
        _doubleSpanFresh = true;
    }

    /// <summary>Returns and clears whether a weapon-locked double trail tip or guest double-trail ring moved since the last emit.</summary>
    public bool ConsumeLiveWeaponTipDirty()
    {
        bool dirty = _liveWeaponTipDirty || _ringDirty;
        _liveWeaponTipDirty = false;
        _ringDirty = false;
        return dirty;
    }

    /// <summary>
    /// Replaces the direct guest <c>Mode == 3</c> double-trail ring (oldest to newest, in
    /// node-local Godot coordinates) with exact per-slot <c>KeyTime</c> values from the guest
    /// simulation ring. Used for unlatched guest-driven double trails such as flying Blades swords.
    /// </summary>
    public void SetDoubleTrailRing(ReadOnlySpan<DoubleTrailRingPoint> nodeLocalPoints)
    {
        if (nodeLocalPoints.Length < 2)
        {
            ClearDoubleTrailRing();
            return;
        }

        int count = Math.Min(nodeLocalPoints.Length, MaxDoubleTrailRingPoints);
        bool changed = count != _ringCount;
        float maxSq = 0f;
        for (int i = 0; i < count; i++)
        {
            DoubleTrailRingPoint pt = nodeLocalPoints[i];
            if (!changed &&
                (_ringPtsA[i].DistanceSquaredTo(pt.EdgeA) > 1e-6f ||
                 _ringPtsB[i].DistanceSquaredTo(pt.EdgeB) > 1e-6f ||
                 MathF.Abs(_ringKeys[i] - pt.KeyTime) > 1e-4f))
            {
                changed = true;
            }

            _ringPtsA[i] = pt.EdgeA;
            _ringPtsB[i] = pt.EdgeB;
            _ringKeys[i] = pt.KeyTime;

            float aSq = pt.EdgeA.LengthSquared();
            float bSq = pt.EdgeB.LengthSquared();
            if (aSq > maxSq) maxSq = aSq;
            if (bSq > maxSq) maxSq = bSq;
        }

        _ringCount = count;
        _ringBoundRadius = MathF.Sqrt(maxSq);
        _ringFresh = true;
        _doubleSpanHostDriven = false;
        _detachedParticles = true;
        if (changed)
        {
            _ringDirty = true;
        }
    }

    /// <summary>Clears any direct guest <c>Mode == 3</c> double-trail ring override.</summary>
    public void ClearDoubleTrailRing()
    {
        if (_ringCount > 0)
        {
            _ringCount = 0;
            _ringBoundRadius = 0f;
            _ringFresh = false;
            _ringDirty = true;
        }
    }

    /// <summary>Clears any host-supplied double-trail blade span.</summary>
    public void ClearDoubleTrailSpan()
    {
        _hasDoubleTrailSpan = false;
        _doubleSpanHostDriven = false;
        _hasGuestDoubleCursor = false;
        _doubleSpanFresh = false;
    }

    /// <summary>
    /// Sets the guest-driven <c>Mode == 2</c> single-trail head point in node-local Godot space
    /// (converting to simulation layout <c>(x, z, y)</c>), with optional guest ring cursor metadata.
    /// </summary>
    public void SetSingleTrailHead(
        Vector3 nodeLocalHead,
        bool hasGuestCursor = false,
        uint guestTail = 0u,
        uint guestCount = 0u)
    {
        float hx = nodeLocalHead.X, hy = nodeLocalHead.Z, hz = nodeLocalHead.Y;
        if (hasGuestCursor)
        {
            if (_hasGuestSingleCursor && (guestCount < _lastGuestSingleCount || guestCount <= 1u))
            {
                ResetTrails(modeFilter: 2);
            }

            float dx = hx - _singleTrailHead[0], dy = hy - _singleTrailHead[1], dz = hz - _singleTrailHead[2];
            bool advanced = !_hasGuestSingleCursor ||
                            guestTail != _lastGuestSingleTail ||
                            guestCount != _lastGuestSingleCount ||
                            (dx * dx) + (dy * dy) + (dz * dz) > 1e-6f;
            if (advanced)
            {
                ++_singleHeadSeq;
            }

            _hasGuestSingleCursor = true;
            _lastGuestSingleTail = guestTail;
            _lastGuestSingleCount = guestCount;
        }
        else
        {
            _hasGuestSingleCursor = false;
            ++_singleHeadSeq;
        }

        _singleTrailHead[0] = hx; _singleTrailHead[1] = hy; _singleTrailHead[2] = hz;
        _hasSingleTrailHead = true;
        _singleHeadFresh = true;
    }

    /// <summary>Clears any guest-driven single-trail head state.</summary>
    public void ClearSingleTrailHead()
    {
        _hasSingleTrailHead = false;
        _hasGuestSingleCursor = false;
        _singleHeadFresh = false;
    }

    /// <summary>Flushes active ribbon particles (<c>Mode == 2</c> and/or <c>Mode == 3</c>) on guest trail reset.</summary>
    public void ResetTrails(int modeFilter = 0)
    {
        bool clearedAny = false;
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                bool match = modeFilter == 0 ? rt.Mode is 2 or 3 : rt.Mode == modeFilter;
                if (!match || rt.Particles.Count == 0)
                {
                    continue;
                }

                for (int i = 0; i < rt.Particles.Count; i++)
                {
                    ReturnParticle(rt.Particles[i]);
                }

                rt.Particles.Clear();
                rt.SummaryValid = false;
                clearedAny = true;
            }
        }

        if (modeFilter is 0 or 3)
        {
            _lastSpawnedDoubleSpanSeq = -1;
            if (_ringCount > 0)
            {
                _ringCount = 0;
                _ringBoundRadius = 0f;
                _ringFresh = false;
                clearedAny = true;
            }
        }

        if (modeFilter is 0 or 2)
        {
            _lastSpawnedSingleHeadSeq = -1;
        }

        if (clearedAny)
        {
            LastRebased = true;
        }
    }

    /// <summary>Moves whenever <see cref="_splineOverride"/>'s points change; a beam built from an
    /// older set restates itself (<see cref="BeamTick"/>).</summary>
    private int _splineSerial;

    /// <summary>The live size scale — the guest's element+412: folded into the keyframe global
    /// scales (binding 338) at draw time, and multiplied into the spawn-shape offset and the
    /// per-update acceleration by the simulation itself.</summary>
    public void SetSizeScale(float scale) =>
        _sizeScale = float.IsFinite(scale) && scale > 0f ? scale : 1f;
    public void SetDistanceFade(bool on) => _distanceFade = on;

    /// <summary>
    /// Overrides the control points of any Type 5 spline chain node (<c>CParticleSplineNode</c>,
    /// <c>vtable 0x820058C8</c>) with runtime endpoints in this effect node's local Godot space
    /// (corresponding to the guest's <c>sub_8322E858</c> override used by multi-dummy weapon
    /// augmentations such as <c>FX_64D9750B</c> and <c>FX_B0BF62DC</c>). Pass an empty span to
    /// clear the override and revert to the bank's authored spline points.
    /// </summary>
    public void SetSplineOverride(ReadOnlySpan<Vector3> nodeLocalPoints)
    {
        if (nodeLocalPoints.Length < 2)
        {
            _splineOverride = null;
            return;
        }

        bool wasNull = _splineOverride is null;
        bool changed = wasNull || _splineOverride!.Length != nodeLocalPoints.Length;
        if (_splineOverride is null || _splineOverride.Length != nodeLocalPoints.Length)
        {
            _splineOverride = new float[nodeLocalPoints.Length][];
            for (int i = 0; i < _splineOverride.Length; i++)
            {
                _splineOverride[i] = new float[3];
            }
        }

        for (int i = 0; i < nodeLocalPoints.Length; i++)
        {
            Vector3 p = nodeLocalPoints[i];
            // Godot node-local (x, y, z) -> simulation space (x, z, y) so ToGodot(sim) = (x, y, z).
            float[] at = _splineOverride[i];
            changed |= at[0] != p.X || at[1] != p.Z || at[2] != p.Y;
            at[0] = p.X;
            at[1] = p.Z;
            at[2] = p.Y;
        }

        if (changed)
        {
            ++_splineSerial;
            _beamDirty = true;
        }

        if (wasNull)
        {
            foreach (InstanceState inst in _instances)
            {
                if (inst.Frame != 0)
                {
                    continue;
                }

                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    UpdateChain(rt.Chain, inst, rt.PlacePos, rt.PlaceRot, rt.Scale, rt.PosW, rt.RotW);
                    Array.Copy(rt.PosW, rt.PrevPos, 3);
                }
            }
        }
    }

    private void ApplyBeamOverrideToEmitter(EmitterRuntime rt, ReadOnlySpan<Vector3> nodeLocalPoints)
    {
        if (nodeLocalPoints.Length < 2)
        {
            if (rt.BeamSplineOverride is not null)
            {
                rt.BeamSplineOverride = null;
                rt.BeamSplineFresh = false;
                ++rt.BeamSplineSerial;
                _beamDirty = true;
            }
            return;
        }

        bool changed = rt.BeamSplineOverride is null || rt.BeamSplineOverride.Length != nodeLocalPoints.Length;
        if (rt.BeamSplineOverride is null || rt.BeamSplineOverride.Length != nodeLocalPoints.Length)
        {
            rt.BeamSplineOverride = new float[nodeLocalPoints.Length][];
            for (int i = 0; i < rt.BeamSplineOverride.Length; i++)
            {
                rt.BeamSplineOverride[i] = new float[3];
            }
        }

        for (int i = 0; i < nodeLocalPoints.Length; i++)
        {
            Vector3 p = nodeLocalPoints[i];
            float[] at = rt.BeamSplineOverride[i];
            changed |= at[0] != p.X || at[1] != p.Z || at[2] != p.Y;
            at[0] = p.X;
            at[1] = p.Z;
            at[2] = p.Y;
        }

        rt.BeamSplineFresh = true;
        if (changed)
        {
            ++rt.BeamSplineSerial;
            _beamDirty = true;
        }
    }

    /// <summary>
    /// Sets or clears the runtime spline points (in node-local Godot space) for the <paramref name="beamIndex"/>-th
    /// <c>Type 9</c> beam emitter in this system.
    /// </summary>
    public void SetBeamSpline(int beamIndex, ReadOnlySpan<Vector3> nodeLocalPoints)
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Beam && rt.BeamIndex == beamIndex)
                {
                    ApplyBeamOverrideToEmitter(rt, nodeLocalPoints);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Sets or clears the runtime spline points (in node-local Godot space) for the <paramref name="occurrence"/>-th
    /// <c>Type 9</c> beam emitter matching <paramref name="emitterEntityId"/> (<c>FxEmitter.EntityId</c>),
    /// falling back to <paramref name="occurrence"/> by beam index if no entity ID matches. Returns the
    /// matched <c>BeamIndex</c> (<c>-1</c> if none).
    /// </summary>
    public int SetBeamSplineForType(uint emitterEntityId, int occurrence, ReadOnlySpan<Vector3> nodeLocalPoints)
    {
        if (emitterEntityId != 0u)
        {
            int seen = 0;
            foreach (InstanceState inst in _instances)
            {
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    if (!rt.Beam || rt.Def?.EntityId != emitterEntityId) continue;
                    if (seen == occurrence)
                    {
                        ApplyBeamOverrideToEmitter(rt, nodeLocalPoints);
                        return rt.BeamIndex;
                    }
                    seen++;
                }
            }
        }

        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Beam && rt.BeamIndex == occurrence)
                {
                    ApplyBeamOverrideToEmitter(rt, nodeLocalPoints);
                    return rt.BeamIndex;
                }
            }
        }

        return -1;
    }

    /// <summary>Clears all per-beam spline overrides across this system.</summary>
    public void ClearBeamSplines()
    {
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                if (rt.Beam && rt.BeamSplineOverride is not null)
                {
                    rt.BeamSplineOverride = null;
                    rt.BeamSplineFresh = false;
                    ++rt.BeamSplineSerial;
                    _beamDirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Updates per-chain (<c>part.Vector</c>) placement roots in node-local Godot space so multi-root
    /// directional spell effects (such as <c>FX_AE98BF4D</c>) place each type-vector at its own guest
    /// synthetic root instead of collapsing all sub-emitters onto root 0. Pass an empty span to reset
    /// all chains back to the instance placement.
    /// </summary>
    public void SetChainRoots(ReadOnlySpan<Transform3D> nodeLocalRoots)
    {
        if (nodeLocalRoots.Length == 0)
        {
            if (_chainBoundRadius == 0f)
            {
                return;
            }

            _chainBoundRadius = 0f;
            foreach (InstanceState inst in _instances)
            {
                foreach (EmitterRuntime rt in inst.Emitters)
                {
                    Array.Copy(inst.PlacePos, rt.PlacePos, 3);
                    Array.Copy(inst.PlaceRot, rt.PlaceRot, 9);
                }
                foreach (AttractorRuntime at in inst.Attractors)
                {
                    Array.Copy(inst.PlacePos, at.PlacePos, 3);
                    Array.Copy(inst.PlaceRot, at.PlaceRot, 9);
                }
                foreach (LightRuntime lt in inst.Lights)
                {
                    Array.Copy(inst.PlacePos, lt.PlacePos, 3);
                    Array.Copy(inst.PlaceRot, lt.PlaceRot, 9);
                }
            }
            _beamDirty = true;
            return;
        }

        float maxChainDistSq = 0f;
        bool anyChanged = false;
        Span<float> simPos = stackalloc float[3];
        Span<float> simRot = stackalloc float[9];

        static void GodotLocalToSimPlacement(in Transform3D root, Span<float> outPos, Span<float> outRot)
        {
            outPos[0] = root.Origin.X;
            outPos[1] = root.Origin.Z;
            outPos[2] = root.Origin.Y;

            // Simulation ToGodot swaps indices (1, 2): S * PlaceRot * S = root.Basis.
            Vector3 c0 = root.Basis.Column0;
            Vector3 c1 = root.Basis.Column1;
            Vector3 c2 = root.Basis.Column2;
            outRot[0] = c0.X; outRot[1] = c2.X; outRot[2] = c1.X;
            outRot[3] = c0.Z; outRot[4] = c2.Z; outRot[5] = c1.Z;
            outRot[6] = c0.Y; outRot[7] = c2.Y; outRot[8] = c1.Y;
        }

        for (int r = 0; r < nodeLocalRoots.Length; r++)
        {
            float dSq = nodeLocalRoots[r].Origin.LengthSquared();
            if (dSq > maxChainDistSq) maxChainDistSq = dSq;
        }
        _chainBoundRadius = MathF.Sqrt(maxChainDistSq);

        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                int idx = Math.Clamp(rt.ChainId, 0, nodeLocalRoots.Length - 1);
                GodotLocalToSimPlacement(in nodeLocalRoots[idx], simPos, simRot);
                for (int k = 0; k < 3; k++)
                {
                    if (MathF.Abs(rt.PlacePos[k] - simPos[k]) > 1e-4f)
                    {
                        anyChanged = true;
                        rt.PlacePos[k] = simPos[k];
                    }
                }
                for (int k = 0; k < 9; k++)
                {
                    if (MathF.Abs(rt.PlaceRot[k] - simRot[k]) > 1e-4f)
                    {
                        anyChanged = true;
                        rt.PlaceRot[k] = simRot[k];
                    }
                }
                if (inst.Frame == 0)
                {
                    UpdateChain(rt.Chain, inst, rt.PlacePos, rt.PlaceRot, rt.Scale, rt.PosW, rt.RotW);
                    Array.Copy(rt.PosW, rt.PrevPos, 3);
                }
            }
            foreach (AttractorRuntime at in inst.Attractors)
            {
                int idx = Math.Clamp(at.ChainId, 0, nodeLocalRoots.Length - 1);
                GodotLocalToSimPlacement(in nodeLocalRoots[idx], simPos, simRot);
                for (int k = 0; k < 3; k++) at.PlacePos[k] = simPos[k];
                for (int k = 0; k < 9; k++) at.PlaceRot[k] = simRot[k];
            }
            foreach (LightRuntime lt in inst.Lights)
            {
                int idx = Math.Clamp(lt.ChainId, 0, nodeLocalRoots.Length - 1);
                GodotLocalToSimPlacement(in nodeLocalRoots[idx], simPos, simRot);
                for (int k = 0; k < 3; k++) lt.PlacePos[k] = simPos[k];
                for (int k = 0; k < 9; k++) lt.PlaceRot[k] = simRot[k];
                if (inst.Frame == 0)
                {
                    var rot0 = new float[9];
                    UpdateChain(lt.Chain, inst, lt.PlacePos, lt.PlaceRot, lt.Scale, lt.WorldPos, rot0);
                }
            }
        }

        if (anyChanged)
        {
            _beamDirty = true;
        }
    }
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

    public bool HasMeshParticles
    {
        get
        {
            foreach (InstanceState inst in _instances)
                foreach (EmitterRuntime rt in inst.Emitters)
                    foreach (VisualRuntime v in rt.Visuals)
                        if (v.IsMesh) return true;
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
                        float[][] splinePoints = _splineOverride ?? d.SplinePoints;
                        bool looped = _splineOverride is null && d.SplineLooped;
                        bool straight = _splineOverride is not null || d.SplineStraight;
                        SplineEval(splinePoints, looped, straight, tt, lp);
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

            float nodeScale = inst.ScaleCarriesMotion ? scale * _sizeScale : scale;
            lps[0] = lp[0] * nodeScale; lps[1] = lp[1] * nodeScale; lps[2] = lp[2] * nodeScale;
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

    public static (string BlendMode, bool SatMask, float SoftDepth) ResolveVisualRenderState(FxVisual vis) => (
        vis.Displacement ? "disp" : BlendModeFor(vis.SrcBlend, vis.DstBlend, vis.BlendOp),
        vis.SaturationMask,
        vis.SoftDepthContrast > 0f ? vis.SoftDepthContrast : 0f);

    private void BuildVisuals(FxSystemDef sysd, float sc, float[] rotW, List<VisualRuntime> outv)
    {
        if (sysd.Visuals.Count == 0) return;
        foreach (FxVisual vis in sysd.Visuals)
        {
            VisualRuntime? v = BuildVisual(vis, sc, rotW);
            if (v is null) continue;
            (v.BlendMode, v.SatMask, v.SoftDepth) = ResolveVisualRenderState(vis);
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

            // EVERY mesh particle is drawn by the guest's contract; a visual stating no rest
            // orientation is born at identity, which is what the guest's own bank gives it.
            float[] q = vis.HasMeshAlign && vis.MeshAlign.Length >= 4
                ? new[] { vis.MeshAlign[0], vis.MeshAlign[1], vis.MeshAlign[2], vis.MeshAlign[3] }
                : new[] { 0f, 0f, 0f, 1f };
            float ql = MathF.Sqrt((q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]));
            if (ql <= 1e-6f)
            {
                q = new[] { 0f, 0f, 0f, 1f };
                ql = 1f;
            }

            for (int k = 0; k < 4; k++) q[k] /= ql;
            v.MeshAlign = new float[9];
            QuatToMat(q, v.MeshAlign);

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

    /// <summary>
    /// True when at least one visual or trail visual in this system has <c>SelfIllum &lt; 0.999f</c>
    /// and therefore mixes in scene lighting (<c>inst.Lit</c>). Fully self-illuminated effects
    /// (<c>SelfIllum &gt;= 1.0f</c>) multiply <c>inst.Lit</c> by zero and can skip <c>SceneLightAt</c>.
    /// </summary>
    public bool NeedsSceneLight { get; private set; }

    public void Build(FxBank bank, IEnumerable<FxPlacement> placements)
    {
        _instances.Clear();
        ResolvedCount = 0;
        _detachedParticles = false;
        _previousGlobal = null;
        NeedsSceneLight = false;
        HasRibbon = false;
        IsWeaponDoubleTrail = false;
        _hasDoubleTrailSpan = false;
        _doubleSpanHostDriven = false;
        _hasGuestDoubleCursor = false;
        _lastGuestDoubleTail = 0u;
        _lastGuestDoubleCount = 0u;
        _doubleSpanSeq = 0;
        _lastSpawnedDoubleSpanSeq = -1;
        _liveWeaponTipDirty = false;
        _doubleSpanFresh = false;
        _ringCount = 0;
        _ringFresh = false;
        _ringDirty = false;
        _ringBoundRadius = 0f;
        _hasSingleTrailHead = false;
        _hasGuestSingleCursor = false;
        _lastGuestSingleTail = 0u;
        _lastGuestSingleCount = 0u;
        _singleHeadSeq = 0;
        _lastSpawnedSingleHeadSeq = -1;
        _singleHeadFresh = false;

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
            Array.Copy(placeRot, inst.PlaceRot, 9);
            inst.Scale = sc;

            FxEffect? eff = bank.FindByHash(p.EffectHash);
            if (eff is null) continue;

            // A mod effect's authored scale and tint stand where the node's EffectScale and Tint
            // do; both are identity for every shipped effect. The socket offset above is the
            // placement's and keeps the placement's scale.
            if (eff.Scale > 0.0001f && eff.Scale != 1f)
            {
                sc *= eff.Scale;
                inst.Scale = sc;
            }

            Array.Copy(eff.Tint, inst.Tint, 3);
            // On Xbox 360, `UpdateFrequency == 0` was a 15 Hz simulation LOD (`dt = 1/15`, advancing
            // 2 authored frames per update) while `UpdateFrequency >= 1` ticked at 30 Hz (`dt = 1/30`,
            // advancing 1 authored frame per update). In Modern mode (`FableFx`), stepping at 15 Hz
            // caused visible 66.7ms keyframe stepping on short-lived particles (e.g. weapon sparkles)
            // and coarse polygonal gravity arcs (e.g. fountains). We step all emitters at full 30 Hz
            // (`Step = 1`, `DtSec = 1/30f`) while recording `AuthoredStep` (`2` for group 0, `1` for
            // group 1) so per-step drag, spawn spread, spin spread, and flipbook speed preserve their
            // exact authored per-second rates.
            // Fable 3 has no 15 Hz group (FxBank.FixedThirtyHertz).
            inst.AuthoredStep = bank.FixedThirtyHertz || eff.UpdateFrequency >= 1 ? 1f : 2f;
            inst.ScaleCarriesMotion = bank.ScaleCarriesMotion;
            inst.Step = 1;
            inst.DtSec = 1f / 30f;

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
                    Array.Copy(placePos, at.PlacePos, 3);
                    Array.Copy(placeRot, at.PlaceRot, 9);
                    foreach (ChainNode c in chain) at.Chain.Add(c.Clone());
                    at.Chain.Add(node.Clone());
                    inst.Attractors.Add(at);
                    chain.Add(node);
                    continue;
                }

                if (em.TypeId == 7 || em.TypeId == 8)
                {
                    var lt = new LightRuntime { Def = em, Scale = sc, ChainId = part.Vector };
                    Array.Copy(placePos, lt.PlacePos, 3);
                    Array.Copy(placeRot, lt.PlaceRot, 9);
                    foreach (ChainNode c in chain) lt.Chain.Add(c.Clone());
                    lt.Chain.Add(node.Clone());
                    var rot0 = new float[9];
                    UpdateChain(lt.Chain, inst, lt.PlacePos, lt.PlaceRot, sc, lt.WorldPos, rot0);
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
                if (!em.AttachToRoot || rt.Ribbon || rt.Beam) _detachedParticles = true;
                if (rt.Beam) BeamInit(rt);
                inst.Emitters.Add(rt);
                chain.Add(node);
            }

            bool instNeedsSceneLight = false;
            for (int ei = 0; ei < inst.Emitters.Count; ei++)
            {
                EmitterRuntime rt = inst.Emitters[ei];
                if (!instNeedsSceneLight)
                {
                    foreach (VisualRuntime v in rt.Visuals)
                    {
                        if (v.SelfIllum < 0.999f) { instNeedsSceneLight = true; break; }
                    }
                    if (!instNeedsSceneLight)
                    {
                        foreach (VisualRuntime v in rt.TrailVisuals)
                        {
                            if (v.SelfIllum < 0.999f) { instNeedsSceneLight = true; break; }
                        }
                    }
                }
                for (int ai = 0; ai < inst.Attractors.Count; ai++)
                {
                    AttractorRuntime at = inst.Attractors[ai];
                    if (at.Global || rt.PositionalInteractable) rt.Attractors.Add(ai);
                }
            }

            if (instNeedsSceneLight)
            {
                NeedsSceneLight = true;
                AmbientProvider?.Invoke(placePos, inst.Lit);
            }

            if (inst.Emitters.Count != 0 || inst.Lights.Count != 0)
            {
                inst.Place.Resolved = true;
                p.Resolved = true;
                ResolvedCount++;
                _instances.Add(inst);
            }
        }

        bool hasRibbon = false;
        bool isWeaponDoubleTrail = false;
        int beamCount = 0;
        foreach (InstanceState builtInst in _instances)
        {
            foreach (EmitterRuntime rt in builtInst.Emitters)
            {
                if (rt.Beam)
                {
                    rt.BeamIndex = beamCount++;
                }
                if (!rt.Ribbon) continue;
                hasRibbon = true;
                if (rt.Mode == 3)
                {
                    foreach (VisualRuntime vis in rt.Visuals)
                    {
                        if (vis.Texture.Contains("weapon", StringComparison.OrdinalIgnoreCase))
                        {
                            isWeaponDoubleTrail = true;
                            break;
                        }
                    }
                }
            }
        }

        HasRibbon = hasRibbon;
        IsWeaponDoubleTrail = isWeaponDoubleTrail;
        BeamEmitterCount = beamCount;
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
        float[] vel = pt.Vel, pos = pt.Pos, wind = rt.WindDisp, velMul = rt.VelMul;
        vel[0] += wind[0]; vel[1] += wind[1]; vel[2] += wind[2];
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

        vel[0] *= velMul[0]; vel[1] *= velMul[1]; vel[2] *= velMul[2];
        float vpx = vel[0], vpy = vel[1], vpz = vel[2];
        if (hasP14)
        {
            FxEmitter d = rt.Def!;
            int pf = (int)pt.Age;
            float k = 1f - ((1f - d.Generic[14].EvalFrame(pf)) * (1f / inst.AuthoredStep));
            if (hasVec0) k *= k;
            (float v1x, float v1y, float v1z) = d.Vec1.EvalFrame(pf);
            float invStep = 1f / step;
            vpx = pt.Vel[0] * invStep; vpy = pt.Vel[1] * invStep; vpz = pt.Vel[2] * invStep;
            float mx = v1x * k, my = v1y * k, mz = v1z * k;
            if (inst.AuthoredStep > 1f && step == 1f)
            {
                mx = rt.VelMul[0] != 0f ? MathF.CopySign(MathF.Sqrt(MathF.Abs(rt.VelMul[0] * mx)), mx) / rt.VelMul[0] : 0f;
                my = rt.VelMul[1] != 0f ? MathF.CopySign(MathF.Sqrt(MathF.Abs(rt.VelMul[1] * my)), my) / rt.VelMul[1] : 0f;
                mz = rt.VelMul[2] != 0f ? MathF.CopySign(MathF.Sqrt(MathF.Abs(rt.VelMul[2] * mz)), mz) / rt.VelMul[2] : 0f;
            }
            pt.Vel[0] *= mx; pt.Vel[1] *= my; pt.Vel[2] *= mz;
        }

        pos[0] += vpx; pos[1] += vpy; pos[2] += vpz;
        if (pt.HasDoubleEdge)
        {
            pt.EdgeB[0] += vpx; pt.EdgeB[1] += vpy; pt.EdgeB[2] += vpz;
        }
        if (rt.Def!.Spin != 0f) Spin(rt, pt, rt.Def.Spin / Fps * step);
    }

    /// <summary>
    /// <see cref="FxEmitter.Spin"/>, as the guest's hook applies it after each integrator step
    /// (Fable2/src/vfs/src/hooks/particle_motion.cpp): the particle's position turned about the emitter's
    /// own Z axis through its origin by <paramref name="angle"/>, and its velocity with it.
    /// </summary>
    private void Spin(EmitterRuntime rt, ParticleState pt, float angle)
    {
        float[] axis = _spinAxis;
        axis[0] = rt.RotW[2]; axis[1] = rt.RotW[5]; axis[2] = rt.RotW[8];
        if (!VNorm(axis, 1e-6f)) return;
        float[] m = _spinMat;
        AxisAngleMat(axis, angle, m);
        float[] d = _spinD, o = _spinO;
        d[0] = pt.Pos[0] - rt.PosW[0]; d[1] = pt.Pos[1] - rt.PosW[1]; d[2] = pt.Pos[2] - rt.PosW[2];
        MatVec(m, d, o);
        pt.Pos[0] = rt.PosW[0] + o[0]; pt.Pos[1] = rt.PosW[1] + o[1]; pt.Pos[2] = rt.PosW[2] + o[2];
        if (pt.HasDoubleEdge)
        {
            d[0] = pt.EdgeB[0] - rt.PosW[0]; d[1] = pt.EdgeB[1] - rt.PosW[1]; d[2] = pt.EdgeB[2] - rt.PosW[2];
            MatVec(m, d, o);
            pt.EdgeB[0] = rt.PosW[0] + o[0]; pt.EdgeB[1] = rt.PosW[1] + o[1]; pt.EdgeB[2] = rt.PosW[2] + o[2];
        }
        MatVec(m, pt.Vel, o);
        pt.Vel[0] = o[0]; pt.Vel[1] = o[1]; pt.Vel[2] = o[2];
    }

    private readonly float[] _spinAxis = new float[3];
    private readonly float[] _spinMat = new float[9];
    private readonly float[] _spinD = new float[3];
    private readonly float[] _spinO = new float[3];

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

    private void InitParticleVisual(EmitterRuntime rt, ParticleState pt, float step, float authoredStep = 1f)
    {
        float spreadScale = authoredStep > 0f ? step / authoredStep : 1f;
        pt.Seg = 0;
        pt.LoopIter = 0;
        pt.HasBase = false;
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
            // The guest's contract (FxVisual.MeshAlign): the axis is stated in the frame the
            // particles are stored in — the root's — and the visual's own frame alike; the
            // particle is born turned by the emitter's rotation, then the align.
            float[] raw = pt.AxisBody;
            if (v0.OrRandAxis)
            {
                float ax, ay, az, l2;
                do { ax = Srand2(); ay = Srand2(); az = Srand2(); l2 = (ax * ax) + (ay * ay) + (az * az); } while (l2 > 1f || l2 < 1e-8f);
                float il = 1f / MathF.Sqrt(l2);
                raw[0] = ax * il; raw[1] = ay * il; raw[2] = az * il;
            }
            else
            {
                raw[0] = v0.OrAxis[0]; raw[1] = v0.OrAxis[1]; raw[2] = v0.OrAxis[2];
                if (!VNorm(raw, 1e-4f)) { raw[0] = 0; raw[1] = 0; raw[2] = 1; }
            }

            MatVec(rt.PlaceRot, raw, pt.Axis);
            pt.Base ??= new float[9];
            MatMul(rt.RotW, v0.MeshAlign!, pt.Base);
            pt.Rot0 = pt.Rot;
            pt.HasBase = true;

            pt.FlickX = v0.HasRandScale ? v0.RsMin + (Frand() * v0.RsRange) : 1f;
            pt.FlickY = pt.FlickX;
            float w = (step * v0.OrSpeed) + (Frand() * v0.OrSpread * spreadScale);
            pt.Spin = w;
            pt.SpinNext = w;
            return;
        }

        float r = Frand();
        for (int j = 0; j < 4; j++) pt.TexFrame[j] = rt.SlotRandom[j] ? rt.SlotFrames[j] * r : 0f;
        pt.Rot = v0.RotInitialRandom ? Frand() * TwoPi : 0f;
        if (v0.HasFlicker) RollSize(v0, pt);
        float rr = Frand();
        float sp = (step * v0.RotSpeed) + (((rr * v0.RotSpread) - (v0.RotSpread * 0.5f)) * spreadScale);
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
        // Record the birth position into PrevPos before the initial StepParticle advance so newly
        // spawned particles interpolate smoothly from their spawn point across their very first step.
        pt.PrevPos[0] = pt.Pos[0]; pt.PrevPos[1] = pt.Pos[1]; pt.PrevPos[2] = pt.Pos[2];
        pt.HasPrev = true;

        int f = (int)rt.Age;
        float dts = inst.DtSec;
        float speed = d.Generic[4].EvalFrame(f) + (Frand() * d.Generic[5].EvalFrame(f));
        if (inst.ScaleCarriesMotion) speed *= _sizeScale;
        pt.Vel[0] = dirw[0] * speed * dts;
        pt.Vel[1] = dirw[1] * speed * dts;
        pt.Vel[2] = dirw[2] * speed * dts;

        bool hasAttr = rt.Attractors.Count != 0;
        bool hasP14 = !d.Generic[14].IsEmpty;
        bool hasVec0 = !d.Vec0.IsEmpty;
        StepParticle(inst, rt, pt, hasAttr, hasP14, hasVec0);
        pt.Life = ComputeParticleLife(d.UseLife, d.LifetimeFrames, rt.LoopCount, rt.LifeFrames);

        InitParticleVisual(rt, pt, inst.Step, inst.AuthoredStep);
        if (rt.Mode == 3)
        {
            if (!_doubleSpanHostDriven && _ringCount >= 2)
            {
                ReturnParticle(pt);
                return;
            }
            if (_hasDoubleTrailSpan)
            {
                if (_hasGuestDoubleCursor && _doubleSpanSeq == _lastSpawnedDoubleSpanSeq)
                {
                    ReturnParticle(pt);
                    return;
                }

                ParticleState? prev = rt.Particles.Count > 0 ? rt.Particles[rt.Particles.Count - 1] : null;
                if (prev is not null)
                {
                    float newMidX = 0.5f * (_doubleSpanA[0] + _doubleSpanB[0]);
                    float newMidY = 0.5f * (_doubleSpanA[1] + _doubleSpanB[1]);
                    float newMidZ = 0.5f * (_doubleSpanA[2] + _doubleSpanB[2]);
                    float prevMidX = 0.5f * (prev.Pos[0] + (prev.HasDoubleEdge ? prev.EdgeB[0] : prev.Pos[0]));
                    float prevMidY = 0.5f * (prev.Pos[1] + (prev.HasDoubleEdge ? prev.EdgeB[1] : prev.Pos[1]));
                    float prevMidZ = 0.5f * (prev.Pos[2] + (prev.HasDoubleEdge ? prev.EdgeB[2] : prev.Pos[2]));
                    float spanW = MathF.Sqrt(
                        ((_doubleSpanA[0] - _doubleSpanB[0]) * (_doubleSpanA[0] - _doubleSpanB[0])) +
                        ((_doubleSpanA[1] - _doubleSpanB[1]) * (_doubleSpanA[1] - _doubleSpanB[1])) +
                        ((_doubleSpanA[2] - _doubleSpanB[2]) * (_doubleSpanA[2] - _doubleSpanB[2])));
                    float maxJump = _hasGuestDoubleCursor
                        ? MathF.Max(7.5f, spanW * 6.0f)
                        : MathF.Max(3.5f, spanW * 4.5f);
                    float jdx = newMidX - prevMidX, jdy = newMidY - prevMidY, jdz = newMidZ - prevMidZ;
                    if ((jdx * jdx) + (jdy * jdy) + (jdz * jdz) > maxJump * maxJump)
                    {
                        for (int i = 0; i < rt.Particles.Count; i++)
                        {
                            ReturnParticle(rt.Particles[i]);
                        }
                        rt.Particles.Clear();
                        prev = null;
                    }
                }

                bool staticBreadcrumb = _doubleSpanHostDriven && IsWeaponDoubleTrail;
                for (int k = 0; k < 3; k++)
                {
                    pt.Pos[k] = _doubleSpanA[k];
                    pt.EdgeB[k] = _doubleSpanB[k];
                    pt.PrevPos[k] = (!staticBreadcrumb && prev is not null) ? prev.Pos[k] : _doubleSpanA[k];
                    pt.PrevEdgeB[k] = (!staticBreadcrumb && prev is not null) ? (prev.HasDoubleEdge ? prev.EdgeB[k] : prev.Pos[k]) : _doubleSpanB[k];
                    pt.Vel[k] = 0f;
                }
                pt.HasPrev = true;
                rt.HasWorldTrailOverride = true;
                _detachedParticles = true;
                _lastSpawnedDoubleSpanSeq = _doubleSpanSeq;
            }
            else if (RequireDoubleTrailSpan)
            {
                ReturnParticle(pt);
                return;
            }
            else
            {
                float hw = (rt.Visuals.Count > 0 ? rt.Visuals[0].TrailWidth : 1f) * 0.5f * _sizeScale;
                float sx = rt.RotW[2] * hw;
                float sy = rt.RotW[5] * hw;
                float sz = rt.RotW[8] * hw;
                if ((sx * sx) + (sy * sy) + (sz * sz) < 1e-8f)
                {
                    sz = hw;
                }

                pt.EdgeB[0] = pt.Pos[0] - sx;
                pt.EdgeB[1] = pt.Pos[1] - sy;
                pt.EdgeB[2] = pt.Pos[2] - sz;
                pt.Pos[0] += sx; pt.Pos[1] += sy; pt.Pos[2] += sz;
                pt.PrevPos[0] = pt.Pos[0]; pt.PrevPos[1] = pt.Pos[1]; pt.PrevPos[2] = pt.Pos[2];
                pt.PrevEdgeB[0] = pt.EdgeB[0]; pt.PrevEdgeB[1] = pt.EdgeB[1]; pt.PrevEdgeB[2] = pt.EdgeB[2];
            }

            pt.HasDoubleEdge = true;
        }
        else if (rt.Mode == 2 && !rt.Beam)
        {
            if (_hasSingleTrailHead)
            {
                if (_hasGuestSingleCursor && _singleHeadSeq == _lastSpawnedSingleHeadSeq)
                {
                    ReturnParticle(pt);
                    return;
                }

                ParticleState? prev = rt.Particles.Count > 0 ? rt.Particles[rt.Particles.Count - 1] : null;
                if (prev is not null)
                {
                    float jdx = _singleTrailHead[0] - prev.Pos[0];
                    float jdy = _singleTrailHead[1] - prev.Pos[1];
                    float jdz = _singleTrailHead[2] - prev.Pos[2];
                    float maxSingleJump = _hasGuestSingleCursor ? 7.5f : 3.5f;
                    if ((jdx * jdx) + (jdy * jdy) + (jdz * jdz) > maxSingleJump * maxSingleJump)
                    {
                        for (int i = 0; i < rt.Particles.Count; i++)
                        {
                            ReturnParticle(rt.Particles[i]);
                        }
                        rt.Particles.Clear();
                        prev = null;
                    }
                }

                for (int k = 0; k < 3; k++)
                {
                    pt.Pos[k] = _singleTrailHead[k];
                    pt.PrevPos[k] = prev is not null ? prev.Pos[k] : _singleTrailHead[k];
                    pt.Vel[k] = 0f;
                }
                pt.HasPrev = true;
                rt.HasWorldTrailOverride = true;
                _detachedParticles = true;
                _lastSpawnedSingleHeadSeq = _singleHeadSeq;
            }
            else if (RequireSingleTrailHead)
            {
                ReturnParticle(pt);
                return;
            }
        }
        pt.HistCount = 0;
        if (rt.Trail)
        {
            pt.Hist ??= new float[TrailHistory][];
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
    //
    // One pool per thread, shared by every system on it, rather than one per system: effects
    // come and go as the camera moves (a new system per guest effect), and a system's own pool
    // began empty and died with it, so every particle of every new effect was a new object
    // that lived long enough to be promoted and then had to be collected from an older
    // generation. Systems step on the main thread; the pool is per thread so a tool stepping
    // one elsewhere cannot share it.
    [ThreadStatic]
    private static Stack<ParticleState>? s_particlePool;

    private static Stack<ParticleState> _particlePool => s_particlePool ??= new Stack<ParticleState>();
    private readonly float[] _spawnOff = new float[3];
    private readonly float[] _spawnDir = new float[3];
    private readonly float[] _spawnO = new float[3];
    private readonly float[] _spawnOd = new float[3];
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
        pt.HasDoubleEdge = false;
        Array.Clear(pt.Pos);
        Array.Clear(pt.EdgeB);
        Array.Clear(pt.PrevEdgeB);
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

    private static float ComputeBeamSegLen(List<float[]> basePts)
    {
        int segs = basePts.Count - 1;
        if (segs <= 0)
        {
            return 0f;
        }

        float total = 0f;
        for (int i = 0; i < segs; i++)
        {
            float dx = basePts[i + 1][0] - basePts[i][0];
            float dy = basePts[i + 1][1] - basePts[i][1];
            float dz = basePts[i + 1][2] - basePts[i][2];
            total += MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        return total / segs;
    }

    private int ActiveBeamSerial(EmitterRuntime rt) =>
        rt.BeamSplineOverride is not null ? rt.BeamSplineSerial : (_splineOverride is not null ? _splineSerial : -1);

    /// <summary>
    /// A beam's undisplaced points into <paramref name="into"/>: a stated point list as given, two
    /// stated ends spread evenly over the authored segment count (sub_831B20E8), or the authored
    /// line from the emitter (unless <see cref="RequireBeamSpline"/> suppresses the unstated fallback).
    /// Returns the segment count.
    /// </summary>
    private int BeamBaseline(EmitterRuntime rt, List<float[]> into)
    {
        FxEmitter d = rt.Def!;
        into.Clear();
        float[][]? stated = rt.BeamSplineOverride ?? _splineOverride;
        if (stated is null && RequireBeamSpline)
        {
            return 0;
        }

        if (stated is { Length: > 2 })
        {
            foreach (float[] p in stated) into.Add(new[] { p[0], p[1], p[2] });
            return stated.Length - 1;
        }

        int nseg = Math.Max(1, d.NumSplineSegments);
        float[] P0, P1;
        if (stated is { Length: 2 })
        {
            P0 = stated[0];
            P1 = stated[1];
        }
        else
        {
            var endw = new float[3];
            MatVec(rt.RotW, new[] { 0f, 3f, 0f }, endw);
            P0 = new[] { rt.PosW[0], rt.PosW[1], rt.PosW[2] };
            P1 = new[] { rt.PosW[0] + (endw[0] * rt.Scale), rt.PosW[1] + (endw[1] * rt.Scale), rt.PosW[2] + (endw[2] * rt.Scale) };
        }

        var stepv = new[] { (P1[0] - P0[0]) / nseg, (P1[1] - P0[1]) / nseg, (P1[2] - P0[2]) / nseg };
        for (int i = 0; i <= nseg; i++) into.Add(new[] { P0[0] + (i * stepv[0]), P0[1] + (i * stepv[1]), P0[2] + (i * stepv[2]) });
        return nseg;
    }

    private readonly List<float[]> _beamRestated = new();

    /// <summary>
    /// New stated points for a built beam: the same count moves each point with its own base,
    /// keeping its displacement (sub_831B1DD8 updates in place); another count builds it again.
    /// </summary>
    private void BeamRestate(EmitterRuntime rt)
    {
        int nseg = BeamBaseline(rt, _beamRestated);
        if (nseg <= 0 || _beamRestated.Count != rt.BeamBase.Count || rt.BeamPts.Count != rt.BeamBase.Count)
        {
            BeamInit(rt);
            return;
        }

        for (int i = 0; i < _beamRestated.Count; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                rt.BeamPts[i][c] += _beamRestated[i][c] - rt.BeamBase[i][c];
                rt.BeamBase[i][c] = _beamRestated[i][c];
            }
        }

        rt.BeamSegLen = ComputeBeamSegLen(rt.BeamBase);
        rt.BeamSerial = ActiveBeamSerial(rt);
    }

    private void BeamInit(EmitterRuntime rt)
    {
        FxEmitter d = rt.Def!;
        rt.BeamSerial = ActiveBeamSerial(rt);
        int nseg = BeamBaseline(rt, rt.BeamBase);
        rt.BeamPts.Clear();
        rt.BeamWander.Clear();
        if (nseg <= 0 || rt.BeamBase.Count < 2)
        {
            rt.BeamSegLen = 0f;
            rt.BeamJerkTimer = 0f;
            return;
        }

        rt.BeamSegLen = ComputeBeamSegLen(rt.BeamBase);
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
        int activeSerial = ActiveBeamSerial(rt);
        if (rt.BeamSerial != activeSerial || (rt.BeamPts.Count < 2 && (!RequireBeamSpline || activeSerial >= 0)))
        {
            BeamRestate(rt);
        }

        if (RequireBeamSpline && activeSerial < 0)
        {
            for (int i = 0; i < rt.Particles.Count; i++)
            {
                ReturnParticle(rt.Particles[i]);
            }
            rt.Particles.Clear();
            return;
        }

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
            pt.Life = ComputeParticleLife(d.UseLife, d.LifetimeFrames, rt.LoopCount, rt.LifeFrames);
            InitParticleVisual(rt, pt, inst.Step);
            rt.Particles.Add(pt);
            TotalSpawned++;
        }

        if (d.AnimationMode <= 2)
        {
            for (int i = 0; i < n; i++)
            {
                ParticleState pt = rt.Particles[i];
                pt.Pos[0] = rt.BeamPts[i][0];
                pt.Pos[1] = rt.BeamPts[i][1];
                pt.Pos[2] = rt.BeamPts[i][2];
                if (!pt.HasPrev)
                {
                    pt.PrevPos[0] = pt.Pos[0];
                    pt.PrevPos[1] = pt.Pos[1];
                    pt.PrevPos[2] = pt.Pos[2];
                    pt.HasPrev = true;
                }
            }
        }
    }

    private readonly float[] _tickRot = new float[9];
    private readonly float[] _tickV0L = new float[3];
    private readonly float[] _tickV0W = new float[3];
    private readonly float[] _tickAcc = new float[3];

    private void Tick(InstanceState inst, bool lightsOnly = false)
    {
        int istep = inst.Step;
        float step = istep;
        float dts = inst.DtSec;
        inst.Frame += istep;

        if (lightsOnly)
        {
            foreach (EmitterRuntime held in inst.Emitters)
            {
                for (int i = 0; i < held.Particles.Count; i++)
                {
                    ReturnParticle(held.Particles[i]);
                }

                held.Particles.Clear();
                held.SummaryValid = false;
            }
        }

        if (inst.SharedRand < 0f) inst.SharedRand = Frand();

        float[] rotTmp = _tickRot;
        if (!lightsOnly)
        {
            foreach (AttractorRuntime at in inst.Attractors)
            {
                at.Age += step;
                UpdateChain(at.Chain, inst, at.PlacePos, at.PlaceRot, inst.Scale, at.WorldPos, rotTmp);
            }
        }

        foreach (LightRuntime lt in inst.Lights)
        {
            lt.PrevWorldPos[0] = lt.WorldPos[0]; lt.PrevWorldPos[1] = lt.WorldPos[1]; lt.PrevWorldPos[2] = lt.WorldPos[2];
            lt.PrevAge = lt.Age;
            lt.PrevPhase = lt.Phase;
            lt.HasPrev = true;
            lt.Age += step;
            UpdateChain(lt.Chain, inst, lt.PlacePos, lt.PlaceRot, lt.Scale, lt.WorldPos, rotTmp);

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

        if (lightsOnly)
        {
            return;
        }

        float spreadScale = inst.AuthoredStep > 0f ? step / inst.AuthoredStep : 1f;
        float[] v0l = _tickV0L;
        float[] v0w = _tickV0W;
        bool summarize = Engine_FxFastPaths.Enabled;
        foreach (EmitterRuntime rt in inst.Emitters)
        {
            FxEmitter d = rt.Def!;
            int fOld = (int)rt.Age;
            rt.SummaryValid = false;

            Array.Copy(rt.PosW, rt.PrevPos, 3);
            UpdateChain(rt.Chain, inst, rt.PlacePos, rt.PlaceRot, rt.Scale, rt.PosW, rt.RotW);
            if (rt.FirstFrame) Array.Copy(rt.PosW, rt.PrevPos, 3);

            (float v1x, float v1y, float v1z) = d.Vec1.EvalFrame(fOld);
            float m12 = d.Generic[12].EvalFrame(fOld);
            float m13 = d.Generic[13].EvalFrame(fOld);
            float vm0 = m13 * v1x * m12;
            float vm1 = m13 * v1y * m12;
            float vm2 = m13 * v1z * m12;
            if (d.Generic[14].IsEmpty && inst.AuthoredStep > 1f && step == 1f)
            {
                vm0 = MathF.CopySign(MathF.Sqrt(MathF.Abs(vm0)), vm0);
                vm1 = MathF.CopySign(MathF.Sqrt(MathF.Abs(vm1)), vm1);
                vm2 = MathF.CopySign(MathF.Sqrt(MathF.Abs(vm2)), vm2);
            }
            rt.VelMul[0] = vm0; rt.VelMul[1] = vm1; rt.VelMul[2] = vm2;

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

            bool sustainedBeam = rt.Beam && rt.BeamSplineOverride is not null;
            bool windowOpen = _emitting && (!d.UseLife || rt.Age <= d.LifetimeFrames || sustainedBeam || (rt.FirstFrame && d.InitialNum > 0f));
            bool useLife = d.UseLife && !sustainedBeam;
            float lifeFrames = d.LifetimeFrames;
            int nvis = rt.Visuals.Count;
            int kept = 0;
            Span<ParticleState> particles = CollectionsMarshal.AsSpan(rt.Particles);
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleState p = particles[i];
                bool dead = !(sustainedBeam && _emitting) && ShouldParticleExpire(
                    _emitting, rt.LoopCount, useLife, lifeFrames, p.Age, p.Life, rt.LoopEnd, p.LoopIter);
                if (dead)
                {
                    ReturnParticle(p);
                    continue;
                }

                // The step's starting point, for the draw between steps. Here rather than in a
                // pass of its own: nothing before this reads a particle, and each pass over the
                // particles is a cache miss per particle.
                float[] from = p.Pos, prev = p.PrevPos;
                prev[0] = from[0]; prev[1] = from[1]; prev[2] = from[2];
                if (p.HasDoubleEdge)
                {
                    p.PrevEdgeB[0] = p.EdgeB[0]; p.PrevEdgeB[1] = p.EdgeB[1]; p.PrevEdgeB[2] = p.EdgeB[2];
                }
                p.HasPrev = true;
                particles[kept++] = p;
            }

            rt.Particles.RemoveRange(kept, particles.Length - kept);
            if (rt.Beam)
            {
                if (windowOpen)
                {
                    BeamTick(inst, rt);
                    rt.FirstFrame = false;
                    foreach (ParticleState pt in rt.Particles)
                    {
                        if (rt.LoopCount != 0 && rt.LoopEnd > 0f && rt.LoopEnd <= pt.Age)
                        {
                            if ((rt.LoopCount < 0 && _emitting) || (rt.LoopCount > 0 && pt.LoopIter < rt.LoopCount))
                            {
                                pt.Age = rt.LoopStart;
                                pt.Seg = 1;
                                pt.LoopIter++;
                            }
                            else
                            {
                                pt.LoopIter = 0;
                            }
                        }
                        else if (sustainedBeam && rt.LoopCount == 0 && pt.Life > 0f && pt.Age >= pt.Life)
                        {
                            pt.Age = 0f;
                            pt.Seg = 1;
                        }

                        while (pt.Seg < nvis && pt.Age >= rt.Visuals[pt.Seg].DelayFrames) pt.Seg++;
                        pt.KeyAge = pt.Age;
                        pt.Age += step;
                    }
                }
                else
                {
                    for (int i = 0; i < rt.Particles.Count; i++)
                    {
                        ReturnParticle(rt.Particles[i]);
                    }
                    rt.Particles.Clear();
                    rt.FirstFrame = false;
                }

                continue;
            }

            if (windowOpen)
            {
                float spread = d.Generic[3].EvalFrame(f) * (rt.Mode != 3 ? inst.AuthoredStep : step);
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
            Span<VisualRuntime> visuals = CollectionsMarshal.AsSpan(rt.Visuals);
            int[] slotFrames = rt.SlotFrames;
            int rollFrames = v0 is null ? 0 : v0.IsMesh ? v0.RsFrames : v0.FlickerFrames;
            bool rolls = v0 is not null && (v0.IsMesh ? v0.HasRandScale : v0.HasFlicker) && rollFrames > 0;
            ParticleSummary summary = ParticleSummary.Begin();
            foreach (ParticleState pt in CollectionsMarshal.AsSpan(rt.Particles))
            {
                if (rt.LoopCount != 0 && rt.LoopEnd <= pt.Age)
                {
                    if ((rt.LoopCount < 0 && _emitting) || (rt.LoopCount > 0 && pt.LoopIter < rt.LoopCount))
                    {
                        pt.Age = rt.LoopStart;
                        pt.Seg = 1;
                        pt.LoopIter++;
                    }
                    else
                    {
                        pt.LoopIter = 0;
                    }
                }

                bool crossed = false;
                while (pt.Seg < nvis && pt.Age >= visuals[pt.Seg].DelayFrames) { pt.Seg++; crossed = true; }
                int kb = nvis <= 1 ? 0 : Math.Clamp(pt.Seg, 1, nvis - 1);
                int ka = Math.Max(kb - 1, 0);
                float t = 0f;
                if (nvis > 1 && kb > ka)
                {
                    t = (pt.Age - visuals[ka].DelayFrames) * visuals[kb].InvSpan;
                    t = Math.Clamp(t, 0f, 1f);
                }

                if (crossed && v0 is not null && nvis > 1)
                {
                    VisualRuntime vp = visuals[ka];
                    VisualRuntime vk = visuals[kb];
                    if (vk.IsMesh)
                    {
                        pt.Spin = pt.SpinNext;
                        pt.SpinNext = (step * vk.OrSpeed) + (Frand() * vk.OrSpread * spreadScale);
                    }
                    else if (vp.RotSpeed != vk.RotSpeed)
                    {
                        pt.Spin = pt.SpinNext;
                        pt.SpinNext = (step * vk.RotSpeed) + (((Frand() * vk.RotSpread) - (vk.RotSpread * 0.5f)) * spreadScale);
                    }
                }

                if (!rt.Frozen) StepParticle(inst, rt, pt, hasAttr, hasP14, hasVec0);
                float ang = pt.Rot + (pt.Spin + ((pt.SpinNext - pt.Spin) * t));
                if (v0 is not null && v0.IsMesh) pt.Rot = ang;
                else pt.Rot = ang - (TwoPi * MathF.Round(ang / TwoPi));

                if (v0 is not null && !v0.IsMesh && nvis > 0)
                {
                    float fcsA = visuals[ka].FrameSpeed;
                    float fcsB = visuals[kb].FrameSpeed;
                    float fade = (fcsA + ((fcsB - fcsA) * t)) * spreadScale;
                    float[] frames = pt.TexFrame;
                    for (int j = 0; j < 4; j++)
                    {
                        float fr = frames[j] + fade;
                        frames[j] = fr < slotFrames[j] ? fr : 0f;
                    }
                }

                if (rt.Trail)
                {
                    int hn = Math.Min(pt.HistCount + 1, TrailHistory);
                    float[][] hist = pt.Hist ??= new float[TrailHistory][];
                    float[]? spare = hist[hn - 1];
                    for (int k = hn - 1; k > 0; k--) hist[k] = hist[k - 1];
                    float[] head = spare ?? new float[3];
                    head[0] = pt.Pos[0]; head[1] = pt.Pos[1]; head[2] = pt.Pos[2];
                    hist[0] = head;
                    pt.HistCount = hn;
                }

                pt.KeyAge = pt.Age;
                pt.Age += step;

                if (rolls)
                {
                    int clk = (int)MathF.Floor(pt.Age + 0.5f);
                    if (clk % rollFrames == 0) RollSize(v0!, pt);
                }

                if (summarize) summary.Add(pt);
            }

            if (summarize) summary.Store(rt);
        }

        if (_loop && _emitting && inst.Time > 1.5f)
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

    /// <summary>
    /// True when the last <see cref="Update"/> rebased detached world-space particles because the
    /// effect node's global transform moved (e.g. weapon sparkles or character aura following an
    /// animated skeleton bone between simulation steps). <see cref="FableFx.Step"/> must re-emit
    /// geometry on that frame so the GPU vertices match the moved <c>MODEL_MATRIX</c>.
    /// </summary>
    public bool LastRebased { get; private set; }

    public void Update(float dt, Transform3D? nodeGlobal = null, bool lightsOnly = false)
    {
        LastSteps = 0;
        LastRebased = false;
        if (!lightsOnly && nodeGlobal is { } current)
        {
            // Same as last frame, the usual case: nothing to rebase, and the same up.
            if (_previousGlobal is not { } previous || previous != current)
            {
                if (_previousGlobal is { } moved && _detachedParticles)
                {
                    Transform3D delta = current.AffineInverse() * moved;
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
        }
        _doubleSpanFresh = false;
        _ringFresh = false;
        _singleHeadFresh = false;
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                rt.BeamSplineFresh = false;
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
                    Tick(inst, lightsOnly);
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
        bool rebasedAny = false;
        if (_ringCount >= 2 && !_ringFresh)
        {
            float maxSq = 0f;
            for (int i = 0; i < _ringCount; i++)
            {
                Vector3 a = delta * _ringPtsA[i];
                Vector3 b = delta * _ringPtsB[i];
                _ringPtsA[i] = a;
                _ringPtsB[i] = b;
                float aSq = a.LengthSquared();
                float bSq = b.LengthSquared();
                if (aSq > maxSq) maxSq = aSq;
                if (bSq > maxSq) maxSq = bSq;
            }
            _ringBoundRadius = MathF.Sqrt(maxSq);
            rebasedAny = true;
        }
        if (_hasDoubleTrailSpan && !_doubleSpanFresh)
        {
            Vector3 spanA = delta * new Vector3(_doubleSpanA[0], _doubleSpanA[2], _doubleSpanA[1]);
            _doubleSpanA[0] = spanA.X; _doubleSpanA[1] = spanA.Z; _doubleSpanA[2] = spanA.Y;
            Vector3 spanB = delta * new Vector3(_doubleSpanB[0], _doubleSpanB[2], _doubleSpanB[1]);
            _doubleSpanB[0] = spanB.X; _doubleSpanB[1] = spanB.Z; _doubleSpanB[2] = spanB.Y;
        }
        if (_hasSingleTrailHead && !_singleHeadFresh)
        {
            Vector3 head = delta * new Vector3(_singleTrailHead[0], _singleTrailHead[2], _singleTrailHead[1]);
            _singleTrailHead[0] = head.X; _singleTrailHead[1] = head.Z; _singleTrailHead[2] = head.Y;
        }
        foreach (InstanceState inst in _instances)
        {
            foreach (EmitterRuntime rt in inst.Emitters)
            {
                bool worldOverride = rt.HasWorldTrailOverride || (rt.Beam && rt.BeamSplineOverride is not null);
                if (rt.Def is null || (rt.Def.AttachToRoot && !worldOverride)) continue;
                if (rt.Beam && rt.BeamSplineOverride is not null && !rt.BeamSplineFresh)
                {
                    for (int i = 0; i < rt.BeamSplineOverride.Length; i++)
                    {
                        float[] bp = rt.BeamSplineOverride[i];
                        Vector3 movedBp = delta * new Vector3(bp[0], bp[2], bp[1]);
                        bp[0] = movedBp.X; bp[1] = movedBp.Z; bp[2] = movedBp.Y;
                    }
                    for (int i = 0; i < rt.BeamBase.Count; i++)
                    {
                        float[] bb = rt.BeamBase[i];
                        Vector3 movedBb = delta * new Vector3(bb[0], bb[2], bb[1]);
                        bb[0] = movedBb.X; bb[1] = movedBb.Z; bb[2] = movedBb.Y;
                    }
                    for (int i = 0; i < rt.BeamPts.Count; i++)
                    {
                        float[] bpt = rt.BeamPts[i];
                        Vector3 movedBpt = delta * new Vector3(bpt[0], bpt[2], bpt[1]);
                        bpt[0] = movedBpt.X; bpt[1] = movedBpt.Z; bpt[2] = movedBpt.Y;
                    }
                }
                // Transform the emitter's stored world position into the new node-local space so
                // `Array.Copy(rt.PosW, rt.PrevPos, 3)` on the next `Tick` preserves the true
                // previous emitter world position and `SpawnOne` interpolates `frac` smoothly
                // along a moving weapon blade.
                Vector3 emitterPos = delta * new Vector3(rt.PosW[0], rt.PosW[2], rt.PosW[1]);
                rt.PosW[0] = emitterPos.X; rt.PosW[1] = emitterPos.Z; rt.PosW[2] = emitterPos.Y;
                Vector3 emitterPrev = delta * new Vector3(rt.PrevPos[0], rt.PrevPos[2], rt.PrevPos[1]);
                rt.PrevPos[0] = emitterPrev.X; rt.PrevPos[1] = emitterPrev.Z; rt.PrevPos[2] = emitterPrev.Y;
                rt.SummaryValid = false;
                foreach (ParticleState pt in rt.Particles)
                {
                    rebasedAny = true;
                    Vector3 pos = delta * new Vector3(pt.Pos[0], pt.Pos[2], pt.Pos[1]);
                    pt.Pos[0] = pos.X; pt.Pos[1] = pos.Z; pt.Pos[2] = pos.Y;
                    Vector3 prev = delta * new Vector3(pt.PrevPos[0], pt.PrevPos[2], pt.PrevPos[1]);
                    pt.PrevPos[0] = prev.X; pt.PrevPos[1] = prev.Z; pt.PrevPos[2] = prev.Y;
                    if (pt.HasDoubleEdge)
                    {
                        Vector3 edgeB = delta * new Vector3(pt.EdgeB[0], pt.EdgeB[2], pt.EdgeB[1]);
                        pt.EdgeB[0] = edgeB.X; pt.EdgeB[1] = edgeB.Z; pt.EdgeB[2] = edgeB.Y;
                        Vector3 prevEdgeB = delta * new Vector3(pt.PrevEdgeB[0], pt.PrevEdgeB[2], pt.PrevEdgeB[1]);
                        pt.PrevEdgeB[0] = prevEdgeB.X; pt.PrevEdgeB[1] = prevEdgeB.Z; pt.PrevEdgeB[2] = prevEdgeB.Y;
                    }
                    Vector3 vel = delta.Basis * new Vector3(pt.Vel[0], pt.Vel[2], pt.Vel[1]);
                    pt.Vel[0] = vel.X; pt.Vel[1] = vel.Z; pt.Vel[2] = vel.Y;
                    for (int k = 0; k < pt.HistCount; k++)
                    {
                        float[]? h = pt.Hist![k];
                        if (h is null) continue;
                        Vector3 hp = delta * new Vector3(h[0], h[2], h[1]);
                        h[0] = hp.X; h[1] = hp.Z; h[2] = hp.Y;
                    }
                }
            }
        }

        if (rebasedAny)
        {
            LastRebased = true;
        }
    }

    /// <summary>
    /// Every live particle's current and previous position in Godot space, for offline tools and
    /// headless unit tests.
    /// </summary>
    public IEnumerable<(Vector3 PrevPos, Vector3 Pos, bool HasPrev)> ParticleMotionStates()
    {
        foreach (InstanceState inst in _instances)
            foreach (EmitterRuntime rt in inst.Emitters)
                foreach (ParticleState pt in rt.Particles)
                    yield return (ToGodot(pt.PrevPos), ToGodot(pt.Pos), pt.HasPrev);
    }

    /// <summary>Every live particle's position in Godot space, for the offline tools.</summary>
    public IEnumerable<Vector3> ParticlePositions()
    {
        foreach (InstanceState inst in _instances)
            foreach (EmitterRuntime rt in inst.Emitters)
                foreach (ParticleState pt in rt.Particles)
                    yield return ToGodot(pt.Pos);
    }

    /// <summary>
    /// Per emitter, for the offline tools: live particles, their height span (the guest's z) and
    /// their radius span about the emitter's origin, in the guest's units.
    /// </summary>
    public string DebugEmitterSpread()
    {
        var parts = new List<string>();
        foreach (InstanceState inst in _instances)
        {
            for (int i = 0; i < inst.Emitters.Count; i++)
            {
                EmitterRuntime rt = inst.Emitters[i];
                if (rt.Particles.Count == 0)
                {
                    parts.Add($"#{i}: none");
                    continue;
                }

                float zMin = float.MaxValue, zMax = float.MinValue, rMin = float.MaxValue, rMax = 0f;
                foreach (ParticleState pt in rt.Particles)
                {
                    float dx = pt.Pos[0] - rt.PosW[0], dy = pt.Pos[1] - rt.PosW[1];
                    float r = MathF.Sqrt((dx * dx) + (dy * dy));
                    zMin = MathF.Min(zMin, pt.Pos[2] - rt.PosW[2]); zMax = MathF.Max(zMax, pt.Pos[2] - rt.PosW[2]);
                    rMin = MathF.Min(rMin, r); rMax = MathF.Max(rMax, r);
                }

                string mesh = string.Empty;
                if (rt.Particles[0] is { HasBase: true, Base: { } b } p0)
                {
                    mesh = $" base [{string.Join(",", System.Linq.Enumerable.Select(b, x => x.ToString("0.00")))}] rot {p0.Rot - p0.Rot0:0.00} axis ({p0.Axis[0]:0.00},{p0.Axis[1]:0.00},{p0.Axis[2]:0.00})";
                }

                parts.Add($"#{i}: {rt.Particles.Count} z {zMin:0.00}..{zMax:0.00} r {rMin:0.00}..{rMax:0.00}{mesh}");
            }
        }

        return string.Join(" | ", parts);
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
        NeedsSceneLight = false;
        HasRibbon = false;
        IsWeaponDoubleTrail = false;
        _hasDoubleTrailSpan = false;
        _doubleSpanHostDriven = false;
        _hasGuestDoubleCursor = false;
        _lastGuestDoubleTail = 0u;
        _lastGuestDoubleCount = 0u;
        _doubleSpanSeq = 0;
        _lastSpawnedDoubleSpanSeq = -1;
        _liveWeaponTipDirty = false;
        _doubleSpanFresh = false;
        _ringCount = 0;
        _ringFresh = false;
        _ringDirty = false;
        _ringBoundRadius = 0f;
        _hasSingleTrailHead = false;
        _hasGuestSingleCursor = false;
        _lastGuestSingleTail = 0u;
        _lastGuestSingleCount = 0u;
        _singleHeadSeq = 0;
        _lastSpawnedSingleHeadSeq = -1;
        _singleHeadFresh = false;
    }

    /// <summary>
    /// Range is the falloff radius (param 15); Cutoff the camera distance at which the guest's
    /// intensity reaches zero (param 17, capped 1000); Ramp the width of that fade inside the
    /// cutoff (param 18); ConeAngle the spot half-angle in radians (param 24, spots only —
    /// zero means no cone); BaseRange is the un-flickered keyframe range so the host node's AABB
    /// remains stable across flicker steps.
    /// </summary>
    public readonly record struct FxLightState(Vector3 Position, Color Colour, float Range, float Energy, float Cutoff, float Ramp, float ConeAngle, float BaseRange = 0f);

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
                if (d is null || (d.UseLife && light.Age > d.LifetimeFrames)) return default;
                FxLightState now = LightState(d, light.Age, light.Phase, light.WorldPos, ref light.NowCurves);
                if (!light.HasPrev)
                {
                    return now;
                }

                // Between the last two steps, as the particles are drawn: on Godot's clock, at the
                // effect's sub-step fraction for this instance's rate.
                FxLightState before = LightState(d, light.PrevAge, light.PrevPhase, light.PrevWorldPos, ref light.PrevCurves);
                float a = inst.Step > 1 ? StepAlphaHalf : StepAlpha;
                return new FxLightState(
                    before.Position.Lerp(now.Position, a),
                    before.Colour.Lerp(now.Colour, a),
                    before.Range + ((now.Range - before.Range) * a),
                    before.Energy + ((now.Energy - before.Energy) * a),
                    now.Cutoff,
                    now.Ramp,
                    now.ConeAngle,
                    before.BaseRange + ((now.BaseRange - before.BaseRange) * a));
            }
        }

        return default;
    }

    private static FxLightState LightState(FxEmitter d, float age, float phase, float[] worldPos, ref LightCurves c)
    {
        if (d.UseLife && age > d.LifetimeFrames)
        {
            return default;
        }

        int f = (int)age;
        if (!c.Valid || c.Frame != f || c.Phase != phase)
        {
            (float r, float g, float b) = d.LightColour.EvalFrame(f);

            // Brightness = 1 + phase × p20, clamped to [0, 2] (0x821f0c4c, 0x821f0ca8);
            // radius = 1 + phase × p19 (0x821f0c84), taken by a point light outright and by
            // a spot only past two flags whose meaning is not established, so point only —
            // the same reading the placed fixtures take.
            float brightness = Math.Clamp(1f + (phase * d.Generic[20].EvalFrame(f)), 0f, 2f);
            float radius = d.TypeId == 7 ? 1f + (phase * d.Generic[19].EvalFrame(f)) : 1f;
            float baseRange = MathF.Max(0f, d.LightRange.EvalFrame(f));
            c.Valid = true;
            c.Frame = f;
            c.Phase = phase;
            c.Colour = new Color(r, g, b);
            c.Energy = d.LightIntensity.EvalFrame(f) * brightness;
            c.BaseRange = baseRange;
            c.Range = MathF.Max(0f, baseRange * radius);
            c.Cutoff = d.LightCutoff.EvalFrame(f);
            c.Ramp = d.LightFadeRamp.EvalFrame(f);
            c.Cone = d.LightConeAngle.EvalFrame(f);
        }

        // Not scaled by the effect's scale: `sub_8323FA50` hands param 15 to the proxy
        // unmultiplied (`fmr f1, f30` → `stfs f31, 0x90`), and the colour path is
        // colour × intensity × kill-fade only. Only the position takes the transform.
        return new FxLightState(ToGodot(worldPos), c.Colour, c.Range, c.Energy, c.Cutoff, c.Ramp, c.Cone, c.BaseRange);
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
    /// Whether effects fade out with distance as authored. The modern model keeps every effect in
    /// view drawn however far it is (user, 2026-10-16: "if you can see it you should be able to
    /// view it"); the authored fade-outs suit the original draw distance. The fade-in near the
    /// camera stays either way.
    /// </summary>
    public static bool FadeOut = true;

    private float DistanceFadeFactor(EmitterRuntime rt, Vector3 p, Vector3 cam)
    {
        if (!_distanceFade || rt.Def is null) return 1f;
        float dist = (cam - p).Length();
        float[] fade = rt.Fade;
        float f2 = fade[2], f3 = fade[3];
        float fi = fade[1] > fade[0] ? Math.Clamp((dist - fade[0]) / (fade[1] - fade[0]), 0f, 1f) : (dist >= fade[1] ? 1f : 0f);
        float fo = !FadeOut ? 1f : f3 > f2 ? Math.Clamp((f3 - dist) / (f3 - f2), 0f, 1f) : (dist <= f3 ? 1f : 0f);
        return fi * fo;
    }

    /// <summary>
    /// Test seam: each non-empty batch of the last <see cref="Assemble"/> (or emit, after its
    /// sort), keyed "texture|mode|sat|soft|z", with its blend mode.
    /// </summary>
    internal IEnumerable<(string Key, string Mode, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> Vertices)> AssembledBatches()
    {
        foreach (KeyValuePair<DrawKey, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>> batch in _batches)
        {
            if (batch.Value.Count == 0) continue;
            DrawKey k = batch.Key;
            yield return ($"{k.Texture}|{k.Mode}|{k.Sat}|{k.Soft.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{k.ZOrder}", k.Mode, batch.Value);
        }
    }

    /// <summary>
    /// When true, particles and horizontal axis-aligned ground quads conform to the loaded landscape
    /// heightfield (and stay flat on elevated bridges) via <see cref="Engine_FxGroundConform"/>.
    /// </summary>
    public bool GroundConform { get; set; }

    /// <summary>Assembles and submits effect geometry; narrow timings accumulate across the driver frame.</summary>
    public void EmitGeometry(
        ArrayMesh mesh,
        Func<string, VisualBatchMaterial?> materialLookup,
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Dictionary<string, MeshInstanceData> meshOut,
        ArrayMesh? dispMesh = null,
        ArrayMesh? addMesh = null,
        bool sceneQuads = true,
        Engine_FxGroundConform.GroundContext? groundConform = null)
    {
        long workAt = System.Diagnostics.Stopwatch.GetTimestamp();
        // One reading for the whole emit: the debug channel can flip it between frames.
        bool fast = Engine_FxFastPaths.Enabled;
        _emitFast = fast;
        BeginSurfaces(mesh);
        if (dispMesh is not null) BeginSurfaces(dispMesh);
        if (addMesh is not null) BeginSurfaces(addMesh);
        Assemble(cameraPosition, cameraRight, cameraUp, meshOut, sceneQuads, fast, groundConform);
        Dictionary<DrawKey, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>> batches = _batches;

        FableFx.FrameWork.Lap(6, ref workAt);
        List<DrawKey> keys = _keys;
        keys.Clear();
        foreach (DrawKey k in batches.Keys) keys.Add(k);
        keys.Sort(ByZOrder);
        foreach (DrawKey key in keys)
        {
            List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list = batches[key];
            if (list.Count == 0) continue;
            long sortAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!fast)
            {
                if (!Engine_FxFastPaths.OrderIndependent(key.Mode))
                {
                    SortQuadsBackToFront(list, cameraPosition);
                }
            }
            else
            {
                list = FastSorted(list, key.Mode, cameraPosition, sceneQuads);
            }

            FableFx.FrameWork.Lap(10, ref sortAt);
            if (DebugEmit && list.Count > 0) GD.Print($"[fx-emit] {key.Texture} mode={key.Mode} sat={key.Sat} soft={key.Soft} z={key.ZOrder} verts={list.Count} v0={list[0].Pos} uv0={list[0].Uv} c0={list[0].Col} v1={list[1].Pos} v2={list[2].Pos}");
            if (key.Mode == "disp")
            {
                if (dispMesh is null) continue;
                VisualBatchMaterial? dispMaterial = BatchMaterial(materialLookup, key, "disp");
                if (dispMaterial is null) continue;
                Flush(dispMesh, dispMaterial.Material, list);
                continue;
            }

            // The guest draws this effect's quads and trails itself: only displacement stays here.
            if (!sceneQuads) continue;

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
        FableFx.FrameWork.Lap(7, ref workAt);
    }

    /// <summary>
    /// Every particle of every instance into the pooled batches and the mesh-instance lists,
    /// against the camera's position and axes. Pure arithmetic, no Godot object: what
    /// <see cref="EmitGeometry"/> flushes, and what the tests compare with the fast paths on and off.
    /// </summary>
    /// <param name="fast">
    /// <see cref="Engine_FxFastPaths.Enabled"/> for this emit: a particle's fade and alpha are
    /// tested before its size and colour are computed, and most are rejected there. A particle
    /// that is drawn computes every term as before.
    /// </param>
    internal void Assemble(
        Vector3 cameraPosition,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Dictionary<string, MeshInstanceData> meshOut,
        bool sceneQuads,
        bool fast,
        Engine_FxGroundConform.GroundContext? groundConform = null)
    {
        foreach (MeshInstanceData data in meshOut.Values) data.Instances.Clear();

        // Pooled: the batch lists, the trail scratch and the key order live with the system.
        foreach (List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> pooled in _batches.Values) pooled.Clear();
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
                        int maxK = -1;
                        for (int k = 0; k < pt.HistCount; k++)
                        {
                            float hage = k * inst.Step;
                            if (rt.TrailLife > 0f && hage >= rt.TrailLife) break;
                            if (pt.Hist![k] is null) break;
                            maxK = k;
                        }

                        // Emit oldest-to-newest so V=0 sits at the fading tail and V=1 sits at
                        // the leading particle head, matching Mode 2/3 ribbons and trail textures.
                        for (int k = maxK; k >= 0; k--)
                        {
                            float hage = k * inst.Step;
                            float[] h = pt.Hist![k]!;
                            pts.Add(ToGodot(h)); ages.Add(hage);

                            // The history shifts one slot a step, so between steps every point
                            // flows from the slot behind it to its own: the trail slides.
                            float[]? behind = k + 1 < pt.HistCount ? pt.Hist![k + 1] : null;
                            Vector3 flow = behind is null ? Vector3.Zero : ToGodot(h) - ToGodot(behind);
                            motions.Add(new Vector4(flow.X, flow.Y, flow.Z, rateClass));
                        }

                        EmitRibbon(Sink, rt, rt.TrailVisuals, pts, ages, motions, cameraPosition, false, inst.Lit, inst.Tint, pt.Intensity * _liveAlpha);
                    }
                }

                // The fast emit's early rejections (Engine_FxSystem.FastPaths.cs): a whole emitter
                // here, a particle on its fade term and then on its alpha below.
                bool guestRingActive = rt.Mode == 3 && !_doubleSpanHostDriven && _ringCount >= 2;
                float alphaCeiling = fast ? FastAlphaCeiling(rt) * _liveAlpha : float.PositiveInfinity;
                if (fast && !guestRingActive && FastSkipsEmitter(rt, cameraPosition, alphaCeiling)) continue;

                if (rt.Ribbon)
                {
                    pts.Clear(); ages.Clear(); motions.Clear();
                    float ribbonIntensity = _intensity * _liveAlpha;
                    float chainClass = inst.Step > 1 ? 1f : 0f;
                    if (rt.Mode == 3)
                    {
                        List<Vector3> ptsB = _ptsB;
                        List<Vector4> motionsB = _motionsB;
                        ptsB.Clear(); motionsB.Clear();
                        if (guestRingActive)
                        {
                            // Leading-edge interpolation is driven directly on `Engine_DrawnFrame.DrawTick`
                            // in `GameFx` in lockstep with the 3D projectile mesh; keep `CUSTOM0.xyz == 0`
                            // so `fx_billboard.gdshaderinc` never applies an unsynchronized 30 Hz
                            // `fx_step_alpha` sawtooth pullback.
                            for (int i = 0; i < _ringCount; i++)
                            {
                                pts.Add(_ringPtsA[i]);
                                ptsB.Add(_ringPtsB[i]);
                                ages.Add(GuestKeyTimeToAge(rt.Visuals, _ringKeys[i]));
                                motions.Add(Vector4.Zero);
                                motionsB.Add(Vector4.Zero);
                            }

                            EmitDoubleRibbon(Sink, rt, rt.Visuals, pts, ptsB, ages, motions, motionsB, cameraPosition, inst.Lit, inst.Tint, ribbonIntensity);
                            continue;
                        }
                        foreach (ParticleState pt in rt.Particles)
                        {
                            Vector3 edgeA = ToGodot(pt.Pos);
                            Vector3 edgeB = ToGodot(pt.HasDoubleEdge ? pt.EdgeB : pt.Pos);
                            pts.Add(edgeA);
                            ptsB.Add(edgeB);
                            ages.Add(pt.KeyAge);
                            Vector3 movedA = pt.HasPrev ? edgeA - ToGodot(pt.PrevPos) : Vector3.Zero;
                            Vector3 movedB = pt.HasPrev && pt.HasDoubleEdge ? edgeB - ToGodot(pt.PrevEdgeB) : movedA;
                            motions.Add(new Vector4(movedA.X, movedA.Y, movedA.Z, chainClass));
                            motionsB.Add(new Vector4(movedB.X, movedB.Y, movedB.Z, chainClass));
                        }

                        if (_doubleSpanHostDriven && IsWeaponDoubleTrail && _hasDoubleTrailSpan && rt.Particles.Count >= 1)
                        {
                            Vector3 liveA = ToGodot(_doubleSpanA);
                            Vector3 liveB = ToGodot(_doubleSpanB);
                            int last = pts.Count - 1;
                            Vector3 lastA = pts[last];
                            Vector3 lastB = ptsB[last];
                            float tipMoveSq = lastA.DistanceSquaredTo(liveA) + lastB.DistanceSquaredTo(liveB);
                            if (tipMoveSq <= 1e-6f)
                            {
                                pts[last] = liveA;
                                ptsB[last] = liveB;
                                ages[last] = 0f;
                                motions[last] = new Vector4(0f, 0f, 0f, chainClass);
                                motionsB[last] = new Vector4(0f, 0f, 0f, chainClass);
                            }
                            else if (tipMoveSq < 16.0f)
                            {
                                pts.Add(liveA);
                                ptsB.Add(liveB);
                                ages.Add(0f);
                                motions.Add(new Vector4(0f, 0f, 0f, chainClass));
                                motionsB.Add(new Vector4(0f, 0f, 0f, chainClass));
                            }
                        }

                        EmitDoubleRibbon(Sink, rt, rt.Visuals, pts, ptsB, ages, motions, motionsB, cameraPosition, inst.Lit, inst.Tint, ribbonIntensity);
                        continue;
                    }

                    foreach (ParticleState pt in rt.Particles)
                    {
                        pts.Add(ToGodot(pt.Pos)); ages.Add(pt.KeyAge);
                        Vector3 moved = pt.HasPrev ? ToGodot(pt.Pos) - ToGodot(pt.PrevPos) : Vector3.Zero;
                        motions.Add(new Vector4(moved.X, moved.Y, moved.Z, chainClass));
                    }

                    EmitRibbon(Sink, rt, rt.Visuals, pts, ages, motions, cameraPosition, false, inst.Lit, inst.Tint, ribbonIntensity);
                    continue;
                }

                foreach (ParticleState pt in rt.Particles)
                {
                    if (nvis == 0) continue;
                    Vector3 ppos = ToGodot(pt.Pos);

                    float fadeTerm = 0f;
                    if (fast && FastRejectsFade(rt, ppos, cameraPosition, pt.Intensity * _liveAlpha, alphaCeiling, out fadeTerm)) continue;

                    float tLife = pt.Life > 0f ? Math.Clamp(pt.KeyAge / pt.Life, 0f, 1f) : 0f;
                    int segB = nvis <= 1 ? 0 : Math.Clamp(pt.Seg, 1, nvis - 1);
                    int segA = Math.Max(segB - 1, 0);
                    VisualRuntime va = rt.Visuals[segA];
                    VisualRuntime? vb = nvis > 1 && segB > segA ? rt.Visuals[segB] : null;
                    float w = 0f;
                    if (vb is not null) w = Math.Clamp((pt.KeyAge - va.DelayFrames) * vb.InvSpan, 0f, 1f);
                    float Lerp(float x, float y) => x + ((y - x) * w);

                    float a = 0f;
                    if (fast && FastRejectsAlpha(vb is not null ? Lerp(va.Alpha, vb.Alpha) : va.Alpha, fadeTerm, out a)) continue;

                    float hx = (vb is not null ? Lerp(va.HalfX, vb.HalfX) : va.HalfX) * pt.FlickX * (vb is not null ? Lerp(va.GlobalScaleX, vb.GlobalScaleX) : va.GlobalScaleX) * _sizeScale;
                    float hy = (vb is not null ? Lerp(va.HalfY, vb.HalfY) : va.HalfY) * pt.FlickY * (vb is not null ? Lerp(va.GlobalScaleY, vb.GlobalScaleY) : va.GlobalScaleY) * _sizeScale;
                    if (hx <= 0f || hy <= 0f) { ++SkipZeroSize; continue; }
                    float si = va.BlendMode == "disp" ? 1f : (vb is not null ? Lerp(va.SelfIllum, vb.SelfIllum) : va.SelfIllum);
                    float invBoost = si < 0.999f ? LitBoostCompensation(va.BlendMode) : 1f;
                    float litR = inst.Lit[0] * invBoost;
                    float litG = inst.Lit[1] * invBoost;
                    float litB = inst.Lit[2] * invBoost;
                    // One scratch, not one per particle: both emitters copy it into a Color
                    // before returning, so reuse is safe.
                    float[] col = _emitCol;
                    col[0] = litR + (((vb is not null ? Lerp(va.Colour[0], vb.Colour[0]) : va.Colour[0]) - litR) * si);
                    col[1] = litG + (((vb is not null ? Lerp(va.Colour[1], vb.Colour[1]) : va.Colour[1]) - litG) * si);
                    col[2] = litB + (((vb is not null ? Lerp(va.Colour[2], vb.Colour[2]) : va.Colour[2]) - litB) * si);
                    col[0] *= _tint[0] * inst.Tint[0];
                    col[1] *= _tint[1] * inst.Tint[1];
                    col[2] *= _tint[2] * inst.Tint[2];
                    if (!fast)
                    {
                        a = vb is not null ? Lerp(va.Alpha, vb.Alpha) : va.Alpha;
                        // The instance intensity is an alpha term: 0038 `r5.w = fade × FadeOut.z`
                        // (binding 321.z = the batch's intensity), `oColor0.w = r5.w × keyframe.a`;
                        // the pixel shader then multiplies rgb by that alpha as well.
                        a *= DistanceFadeFactor(rt, ppos, cameraPosition) * pt.Intensity * _liveAlpha;
                        if (a <= 0.003f) { ++SkipZeroAlpha; continue; }
                    }

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
                    if (!sceneQuads && va.BlendMode != "disp")
                    {
                        continue;
                    }

                    if (groundConform is { } gc && !va.Aligned)
                    {
                        float deltaY = gc.ComputeTerrainDeltaY(ppos);
                        if (pt.HasPrev)
                        {
                            Vector3 prevPos = ToGodot(pt.PrevPos);
                            float prevDeltaY = gc.ComputeTerrainDeltaY(prevPos);
                            motion.Y += deltaY - prevDeltaY;
                        }
                        ppos = new Vector3(ppos.X, ppos.Y + deltaY, ppos.Z);
                    }

                    if (vb is not null && vb.Texture != va.Texture)
                    {
                        EmitBillboard(Sink(va, rt.ZOrder), va, ppos, pt, hx, hy, col, a * (1f - w), tLife, cameraRight, cameraUp, motion, groundConform);
                        EmitBillboard(Sink(vb, rt.ZOrder), vb, ppos, pt, hx, hy, col, a * w, tLife, cameraRight, cameraUp, motion, groundConform);
                    }
                    else
                    {
                        ++EmittedBillboards;
                        EmitBillboard(Sink(va, rt.ZOrder), va, ppos, pt, hx, hy, col, a, tLife, cameraRight, cameraUp, motion, groundConform);
                    }
                }
            }
        }
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

        /// <summary>
        /// CUSTOM1, four 32-bit floats a vertex: unclamped linear HDR RGBA colour (bypassing Godot's
        /// 8-bit RGBA8 compression on <see cref="Mesh.ArrayType.Color"/>).
        /// </summary>
        public float[] Colours = System.Array.Empty<float>();
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
    private sealed partial class SurfaceSlots
    {
        public readonly List<int> Capacity = new();
        public readonly List<int> LastWritten = new();

        /// <summary>Whether a surface has gone up whole since it was built, so its tail is zeros on the server too.</summary>
        public readonly List<bool> Uploaded = new();
        public readonly List<Material?> Material = new();
        public readonly List<byte[]> VertexBytes = new();
        public readonly List<byte[]> AttributeBytes = new();
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
        slots.Motion = _emitFast ? 0 : 3;
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
            int removeCount = slots.Capacity.Count - existing;
            slots.Capacity.RemoveRange(existing, removeCount);
            slots.LastWritten.RemoveRange(existing, removeCount);
            slots.Uploaded.RemoveRange(existing, removeCount);
            slots.Material.RemoveRange(existing, removeCount);
            slots.VertexBytes.RemoveRange(existing, removeCount);
            slots.AttributeBytes.RemoveRange(existing, removeCount);
        }
        else if (slots.Capacity.Count < existing)
        {
            mesh.ClearSurfaces();
            slots.Capacity.Clear();
            slots.LastWritten.Clear();
            slots.Uploaded.Clear();
            slots.Material.Clear();
            slots.VertexBytes.Clear();
            slots.AttributeBytes.Clear();
        }
    }

    private void EndSurfaces(ArrayMesh mesh)
    {
        SurfaceSlots slots = SlotsOf(mesh);
        if (InPlaceLayout.Supported)
        {
            TrimSurfaces(mesh, slots, slots.Used);
        }

        if (_emitFast) EndFastBounds(slots);

        // The whole quad, stated. Surfaces written in place keep the bounds they were built with,
        // and a rebuilt surface's box covers only the particle centres, so the mesh states this
        // frame's: the centres grown by the largest corner offset. A cull margin wide enough to
        // cover the corners instead puts every FX leaf in every light's and shadow pass's reach.
        //
        // Written only when it changed: every write is an instance update to the renderer (a BVH
        // move and a re-pair) and most effects are frozen most frames.
        Aabb box = slots.HasBounds ? slots.Bounds.Grow(slots.MaxOffset) : default;
        if (slots.StatedBox is { } stated)
        {
            if (!slots.HasBounds)
            {
                if (stated == default)
                {
                    return;
                }
            }
            else if (stated != default && stated.Encloses(box))
            {
                return;
            }
        }

        Aabb nextBox = slots.HasBounds ? box.Grow(0.5f) : default;
        slots.StatedBox = nextBox;
        Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
        mesh.CustomAabb = nextBox;
    }

    private static void TrimSurfaces(ArrayMesh mesh, SurfaceSlots slots, int keep)
    {
        for (int i = slots.Capacity.Count - 1; i >= keep; i--)
        {
            mesh.SurfaceRemove(i);
        }

        if (slots.Capacity.Count > keep)
        {
            int removeCount = slots.Capacity.Count - keep;
            slots.Capacity.RemoveRange(keep, removeCount);
            slots.LastWritten.RemoveRange(keep, removeCount);
            slots.Uploaded.RemoveRange(keep, removeCount);
            slots.Material.RemoveRange(keep, removeCount);
            slots.VertexBytes.RemoveRange(keep, removeCount);
            slots.AttributeBytes.RemoveRange(keep, removeCount);
        }
    }

    /// <summary>Accumulates unchanged bounds and submits each particle batch through a retained surface.</summary>
    private void Flush(ArrayMesh mesh, Material material, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list)
    {
        int n = list.Count;
        SurfaceSlots slots = SlotsOf(mesh);
        long boundsAt = System.Diagnostics.Stopwatch.GetTimestamp();
        bool bounded = _emitFast && FastBounds(slots, list);
        bounded = bounded || Engine_FxGeometryWork.TryBounds(list, ref slots.Bounds, ref slots.HasBounds, ref slots.MaxOffset);
        for (int i = 0; !bounded && i < n; i++)
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

        FableFx.FrameWork.Lap(11, ref boundsAt);

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
            WriteInPlace(mesh, slots, slot, slots.Capacity[slot], list);
            if (!ReferenceEquals(slots.Material[slot], material))
            {
                mesh.SurfaceSetMaterial(slot, material);
                slots.Material[slot] = material;
            }
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
        slots.LastWritten.Add(0);
        slots.Uploaded.Add(false);
        slots.Material.Add(material);
        slots.VertexBytes.Add(System.Array.Empty<byte>());
        slots.AttributeBytes.Add(System.Array.Empty<byte>());
        InPlaceLayout.Learn(mesh, slot, capacity);
    }

    /// <summary>The byte layout the in-place write needs, learned from the first surface built.</summary>
    private static class InPlaceLayout
    {
        /// <summary>False once a surface was built with a layout the writer does not know.</summary>
        public static bool Supported = true;

        public static bool Known;
        public static int AttributeStride;
        public static int UvOffset;
        public static int Uv2Offset;
        public static int Custom0Offset;
        public static int Custom1Offset;

        public static void Learn(ArrayMesh mesh, int surface, int capacity)
        {
            if (Known || !Supported)
            {
                return;
            }

            RenderingServer.ArrayFormat format = (RenderingServer.ArrayFormat)mesh.SurfaceGetFormat(surface);
            int vertexStride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride(format, capacity);
            AttributeStride = (int)RenderingServer.MeshSurfaceGetFormatAttributeStride(format, capacity);
            UvOffset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.TexUV);
            Uv2Offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.TexUV2);
            Custom0Offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.Custom0);
            Custom1Offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, capacity, (int)Mesh.ArrayType.Custom1);

            // Position as three floats, both UVs as two floats, CUSTOM0 (motion) and CUSTOM1
            // (unclamped linear HDR RGBA colour) as four 32-bit floats each.
            Supported = vertexStride == 12 &&
                Uv2Offset - UvOffset == 8 &&
                Custom0Offset - Uv2Offset == 8 &&
                Custom1Offset - Custom0Offset == 16 &&
                AttributeStride == Custom1Offset + 16;
            Known = true;
            GD.Print($"[fx] in-place surface writes {(Supported ? "ON" : "OFF")}: vertex stride {vertexStride}, " +
                $"attribute stride {AttributeStride}, uv +{UvOffset}, uv2 +{Uv2Offset}, custom0 +{Custom0Offset}, custom1(hdr-col) +{Custom1Offset}");
        }
    }

    /// <summary>Packs and uploads a reusable surface, timing CPU packing separately from engine submission.</summary>
    private static void WriteInPlace(
        ArrayMesh mesh,
        SurfaceSlots slots,
        int surface,
        int capacity,
        List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list)
    {
        long workAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int n = list.Count;
        int stride = InPlaceLayout.AttributeStride;
        int vertexBytes = capacity * 12;
        int attributeBytes = capacity * stride;
        byte[] vBuf = slots.VertexBytes[surface];
        byte[] aBuf = slots.AttributeBytes[surface];
        int prevWritten = slots.LastWritten[surface];
        if (vBuf.Length < vertexBytes || aBuf.Length < attributeBytes)
        {
            vBuf = new byte[vertexBytes];
            aBuf = new byte[attributeBytes];
            slots.VertexBytes[surface] = vBuf;
            slots.AttributeBytes[surface] = aBuf;
            prevWritten = 0;
        }

        Span<byte> vertices = vBuf.AsSpan(0, vertexBytes);
        Span<byte> attributes = aBuf.AsSpan(0, attributeBytes);
        int uvAt = InPlaceLayout.UvOffset;
        int uv2At = InPlaceLayout.Uv2Offset;
        int motionAt = InPlaceLayout.Custom0Offset;
        int colourAt = InPlaceLayout.Custom1Offset;

        PackInPlace(CollectionsMarshal.AsSpan(list), vertices, attributes, stride, uvAt, uv2At, motionAt, colourAt);

        // Only clear newly unused tail vertices between n and prevWritten; all remaining tail
        // vertices up to capacity are already zeroed (degenerate zero-area triangles).
        if (n < prevWritten)
        {
            int clearEnd = System.Math.Min(prevWritten, capacity);
            if (clearEnd > n)
            {
                vertices.Slice(n * 12, (clearEnd - n) * 12).Clear();
                attributes.Slice(n * stride, (clearEnd - n) * stride).Clear();
            }
        }

        int sent = InPlaceUploadVertices(n, prevWritten, capacity, slots.Uploaded[surface]);
        slots.LastWritten[surface] = n;
        slots.Uploaded[surface] = true;
        FableFx.FrameWork.Lap(8, ref workAt);
        Rid rid = mesh.GetRid();
        RenderingServer.MeshSurfaceUpdateVertexRegion(rid, surface, 0, (ReadOnlySpan<byte>)vertices.Slice(0, sent * 12));
        RenderingServer.MeshSurfaceUpdateAttributeRegion(rid, surface, 0, (ReadOnlySpan<byte>)attributes.Slice(0, sent * stride));
        Refii.Core.Engine_SpikeNotes.Upload((sent * 12) + (sent * stride));
        FableFx.FrameWork.Lap(9, ref workAt);
    }

    /// <summary>
    /// How many vertices of a surface an in-place write sends, from its start: those of this batch
    /// and of the last, the rest being the zeros the server already holds. The first write to a
    /// surface goes whole, over the tail it was built with.
    /// </summary>
    internal static int InPlaceUploadVertices(int written, int lastWritten, int capacity, bool uploaded) =>
        !uploaded ? capacity : System.Math.Min(System.Math.Max(written, lastWritten), capacity);

    /// <summary>
    /// A batch into the vertex and attribute streams, with the batch's size checked once rather
    /// than by five slices a vertex. The offsets are <see cref="InPlaceLayout"/>'s, which holds
    /// the four attributes inside the stride.
    /// </summary>
    internal static void PackInPlace(
        ReadOnlySpan<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> batch,
        Span<byte> vertices,
        Span<byte> attributes,
        int stride,
        int uvAt,
        int uv2At,
        int motionAt,
        int colourAt)
    {
        ref byte vertexAt = ref MemoryMarshal.GetReference(vertices.Slice(0, batch.Length * 12));
        ref byte attributeAt = ref MemoryMarshal.GetReference(attributes.Slice(0, batch.Length * stride));
        for (int i = 0; i < batch.Length; i++)
        {
            ref readonly (Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion) v = ref batch[i];
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref vertexAt, i * 12), v.Pos);
            ref byte a = ref Unsafe.Add(ref attributeAt, i * stride);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref a, uvAt), v.Uv);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref a, uv2At), v.Off);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref a, motionAt), v.Motion);
            // Direct 16-byte IEEE float write into CUSTOM1: preserves unclamped linear HDR colours.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref a, colourAt), v.Col);
        }
    }

    /// <summary>Builds one surface of <paramref name="padded"/> vertices from the batch.</summary>
    private void FlushBuild(ArrayMesh mesh, Material material, List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list, int padded)
    {
        long builtFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        int n = list.Count;
        if (!FlushPool.TryGetValue(padded, out FlushArrays? arraysFor))
        {
            arraysFor = new FlushArrays
            {
                Positions = new Vector3[padded],
                Uvs = new Vector2[padded],
                Colours = new float[padded * 4],
                Offsets = new Vector2[padded],
                Motion = new float[padded * 4],
            };
            FlushPool[padded] = arraysFor;
        }

        Vector3[] positions = arraysFor.Positions;
        Vector2[] uvs = arraysFor.Uvs;
        float[] colours = arraysFor.Colours;
        Vector2[] offsets = arraysFor.Offsets;
        float[] motions = arraysFor.Motion;
        PackSurfaceBatch(list, padded, positions, uvs, colours, offsets, motions);

        // Each packed-array Variant carries a finalizable Disposer registered in GodotSharp's
        // DisposablesTracker; left to the finalizer, all four survive their first GC. Disposed
        // here they die in gen0; the Array holds its own reference to the packed data.
        Variant vPositions = positions, vUvs = uvs, vColours = colours, vOffsets = offsets, vMotion = motions;
        Godot.Collections.Array arrays = _surfaceArrays ??= MakeSurfaceArrays();
        arrays[(int)Mesh.ArrayType.Vertex] = vPositions;
        arrays[(int)Mesh.ArrayType.TexUV] = vUvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = vOffsets;
        arrays[(int)Mesh.ArrayType.Custom0] = vMotion;
        arrays[(int)Mesh.ArrayType.Custom1] = vColours;
        vPositions.Dispose();
        vUvs.Dispose();
        vColours.Dispose();
        vOffsets.Dispose();
        vMotion.Dispose();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays, null, null, SurfaceCustomFormat);
        if (mesh.GetSurfaceCount() > 0)
        {
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.FxSurfaceBuild, builtFrom);
    }

    /// <summary>
    /// Packs a batch of emitted vertices into the float streams uploaded to <c>VERTEX</c>,
    /// <c>UV</c>, <c>UV2</c>, <c>CUSTOM0</c> (motion), and <c>CUSTOM1</c> (unclamped linear HDR
    /// RGBA colour). Exposed for headless unit testing without <see cref="RenderingServer"/>.
    /// </summary>
    public static void PackSurfaceBatch(
        IReadOnlyList<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)> list,
        int padded,
        Span<Vector3> positions,
        Span<Vector2> uvs,
        Span<float> colours,
        Span<Vector2> offsets,
        Span<float> motions)
    {
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            (Vector3 pos, Vector2 uv, Color col, Vector2 off, Vector4 motion) = list[i];
            positions[i] = pos;
            uvs[i] = uv;
            colours[(i * 4) + 0] = col.R;
            colours[(i * 4) + 1] = col.G;
            colours[(i * 4) + 2] = col.B;
            colours[(i * 4) + 3] = col.A;
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
            for (int i = n; i < padded; i++)
            {
                positions[i] = lastPos;
                uvs[i] = lastUv;
                colours[(i * 4) + 0] = 0f;
                colours[(i * 4) + 1] = 0f;
                colours[(i * 4) + 2] = 0f;
                colours[(i * 4) + 3] = 0f;
                offsets[i] = Vector2.Zero;
                motions[(i * 4) + 0] = 0f;
                motions[(i * 4) + 1] = 0f;
                motions[(i * 4) + 2] = 0f;
                motions[(i * 4) + 3] = 0f;
            }
        }
    }

    /// <summary>
    /// Made at the first flush rather than with the class: a static Godot object in an initializer
    /// would reach the engine on the first touch of any static here, and the tests assemble
    /// geometry with no engine running.
    /// </summary>
    private static Godot.Collections.Array? _surfaceArrays;

    /// <summary>
    /// CUSTOM0 and CUSTOM1 as four 32-bit floats each: CUSTOM0 holds sub-step motion, CUSTOM1
    /// holds unclamped linear HDR RGBA vertex colour.
    /// </summary>
    private static readonly Mesh.ArrayFormat SurfaceCustomFormat =
        (Mesh.ArrayFormat)(
            ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift) |
            ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift));

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

    /// <summary>Retains the original particle distance ordering and per-vertex list copy.</summary>
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
        if (disp) Refii.AndroidRuntime.AndroidDisplacementGate.WakeMeshParticle(this, va.MeshPath, _previousGlobal, ToGodot(pt.Pos), va.MeshScale * _sizeScale);

        float ms = vb is not null && vb.IsMesh ? va.MeshScale + ((vb.MeshScale - va.MeshScale) * w) : va.MeshScale;
        float s = ms * pt.FlickX * _sizeScale;
        if (s <= 1e-5f) return;
        Basis basis;
        if (pt.HasBase && pt.Base is not null)
        {
            // The guest's draw (FxVisual.MeshAlign): spin(axis, Σrate) × birth × angle(axis, initial),
            // in the guest's axes, then into Godot's by the same y/z swap the mesh and positions take.
            float[] spin = _meshSpin, initial = _meshInitial, drawn = _meshDrawn;
            AxisAngleMat(pt.Axis, pt.Rot - pt.Rot0, spin);
            AxisAngleMat(pt.AxisBody, pt.Rot0, initial);
            MatMul(spin, pt.Base, drawn);
            MatMul(drawn, initial, drawn);
            basis = new Basis(
                new Vector3(drawn[0], drawn[6], drawn[3]),
                new Vector3(drawn[2], drawn[8], drawn[5]),
                new Vector3(drawn[1], drawn[7], drawn[4])).Scaled(new Vector3(s, s, s));
        }
        else
        {
            // Born under a sprite visual and drawn as a mesh by a later segment: it has no birth
            // orientation, so it turns about its axis alone.
            Vector3 axis = ToGodot(pt.Axis);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Up;
            basis = new Basis(axis.Normalized(), pt.Rot).Scaled(new Vector3(s, s, s));
        }

        data.Instances.Add((new Transform3D(basis, ToGodot(pt.Pos)), new Color(col[0], col[1], col[2], alpha), motion));
    }

    private static void EmitGroundConformingAlignedQuad(
        List<(Vector3, Vector2, Color, Vector2, Vector4)> sink,
        Vector3 centre, Vector3 right, Vector3 up, float hx, float hy,
        float u0, float v0, float u1, float v1, Color colr, Vector4 motion,
        in Engine_FxGroundConform.GroundContext gc)
    {
        int grid = Math.Clamp((int)MathF.Ceiling(MathF.Max(hx, hy) * 2f), 4, 16);
        int stride = grid + 1;
        int vertCount = stride * stride;
        Span<Vector3> verts = stackalloc Vector3[vertCount];
        Span<Vector2> uvs = stackalloc Vector2[vertCount];

        float invGrid = 1f / grid;
        for (int iz = 0; iz <= grid; iz++)
        {
            float tz = iz * invGrid;
            float fz = -1f + (2f * tz);
            float v = v1 + ((v0 - v1) * tz);
            Vector3 rowOff = up * (fz * hy);
            int rowBase = iz * stride;
            for (int ix = 0; ix <= grid; ix++)
            {
                float tx = ix * invGrid;
                float fx = -1f + (2f * tx);
                float u = u0 + ((u1 - u0) * tx);
                Vector3 p = centre + (right * (fx * hx)) + rowOff;
                float deltaY = gc.ComputeTerrainDeltaY(p);
                float conformedY = MathF.Max(p.Y + deltaY, deltaY + Engine_FxGroundConform.LiftMeters);
                verts[rowBase + ix] = new Vector3(p.X, conformedY, p.Z);
                uvs[rowBase + ix] = new Vector2(u, v);
            }
        }

        for (int iz = 0; iz < grid; iz++)
        {
            int row0 = iz * stride;
            int row1 = row0 + stride;
            for (int ix = 0; ix < grid; ix++)
            {
                int i00 = row0 + ix;
                int i10 = i00 + 1;
                int i01 = row1 + ix;
                int i11 = i01 + 1;
                sink.Add((verts[i00], uvs[i00], colr, Vector2.Zero, motion));
                sink.Add((verts[i11], uvs[i11], colr, Vector2.Zero, motion));
                sink.Add((verts[i10], uvs[i10], colr, Vector2.Zero, motion));
                sink.Add((verts[i00], uvs[i00], colr, Vector2.Zero, motion));
                sink.Add((verts[i01], uvs[i01], colr, Vector2.Zero, motion));
                sink.Add((verts[i11], uvs[i11], colr, Vector2.Zero, motion));
            }
        }
    }

    private static void EmitBillboard(
        List<(Vector3, Vector2, Color, Vector2, Vector4)> sink, VisualRuntime vis, Vector3 centre, ParticleState pt, float hx, float hy,
        float[] col, float alpha, float tLife, Vector3 cameraRight, Vector3 cameraUp, Vector4 motion,
        Engine_FxGroundConform.GroundContext? groundConform = null)
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
            if (groundConform is { } gc && MathF.Abs(right.Y) < 0.25f && MathF.Abs(up.Y) < 0.25f)
            {
                EmitGroundConformingAlignedQuad(sink, centre, right, up, hx, hy, u0, v0, u1, v1, colr, motion, gc);
                return;
            }

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
    private readonly float[] _meshSpin = new float[9];
    private readonly float[] _meshInitial = new float[9];
    private readonly float[] _meshDrawn = new float[9];

    private System.Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>>? _sink;

    /// <summary>The batch a visual's quads go in, created on first sight and pooled thereafter.</summary>
    private List<(Vector3, Vector2, Color, Vector2, Vector4)> SinkFor(VisualRuntime v, int z)
    {
        // Asked per particle and the key hashes two strings, so the visual keeps its answer: a
        // batch list stays with the system once made, and a visual's key fields do not change.
        if (v.Sink is { } kept && v.SinkZ == z)
        {
            return kept;
        }

        var key = new DrawKey(v.Texture, v.BlendMode, v.SatMask, v.SoftDepth, z);
        if (!_batches.TryGetValue(key, out List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>? list))
        {
            list = new List<(Vector3 Pos, Vector2 Uv, Color Col, Vector2 Off, Vector4 Motion)>();
            _batches[key] = list;
        }

        v.Sink = list;
        v.SinkZ = z;
        return list;
    }

    private float[] _ribAttrW = System.Array.Empty<float>();
    private float[] _ribAttrA = System.Array.Empty<float>();
    private Vector3[] _ribAttrC = System.Array.Empty<Vector3>();
    private int[] _ribAttrTex = System.Array.Empty<int>();
    private Vector3[] _ribC = System.Array.Empty<Vector3>();
    private float[] _ribA = System.Array.Empty<float>();
    private int[] _ribTex = System.Array.Empty<int>();
    private Vector4[] _ribM = System.Array.Empty<Vector4>();
    private readonly List<Vector3> _ptsB = new();
    private readonly List<Vector4> _motionsB = new();
    private Vector3[] _ribEdgeA = System.Array.Empty<Vector3>();
    private Vector3[] _ribEdgeB = System.Array.Empty<Vector3>();
    private Vector4[] _ribMA = System.Array.Empty<Vector4>();
    private Vector4[] _ribMB = System.Array.Empty<Vector4>();

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

    private static float GuestKeyTimeToAge(List<VisualRuntime> vis, float keyTime)
    {
        int nvis = vis.Count;
        if (nvis <= 1 || !float.IsFinite(keyTime))
        {
            return 0f;
        }

        float k = Math.Clamp(keyTime, 0f, nvis - 1);
        int k0 = Math.Min((int)k, nvis - 1);
        int k1 = Math.Min(k0 + 1, nvis - 1);
        float f = k - k0;
        return vis[k0].DelayFrames + ((vis[k1].DelayFrames - vis[k0].DelayFrames) * f);
    }

    /// <summary>
    /// Interpolates a rotating <c>Mode == 3</c> double-trail span <c>(prevA, prevB) → (currA, currB)</c>
    /// by lerping the span midpoint and half-width while spherically rotating (slerping) the half-span
    /// direction. Prevents rapid helical corkscrew spins (such as flying Blades' <c>120°–150°</c> per-step
    /// twist) from collapsing the leading tip width through the centerline at intermediate frames.
    /// </summary>
    public static void InterpolateDoubleTrailSpan(
        Vector3 prevA,
        Vector3 prevB,
        Vector3 currA,
        Vector3 currB,
        float t,
        out Vector3 outA,
        out Vector3 outB)
    {
        float alpha = Math.Clamp(t, 0f, 1f);
        if (alpha <= 1e-5f)
        {
            outA = prevA;
            outB = prevB;
            return;
        }
        if (alpha >= 1f - 1e-5f)
        {
            outA = currA;
            outB = currB;
            return;
        }

        Vector3 prevMid = 0.5f * (prevA + prevB);
        Vector3 currMid = 0.5f * (currA + currB);
        Vector3 mid = prevMid.Lerp(currMid, alpha);

        Vector3 prevHalf = 0.5f * (prevB - prevA);
        Vector3 currHalf = 0.5f * (currB - currA);
        float len0 = prevHalf.Length();
        float len1 = currHalf.Length();
        if (len0 <= 1e-5f || len1 <= 1e-5f)
        {
            outA = prevA.Lerp(currA, alpha);
            outB = prevB.Lerp(currB, alpha);
            return;
        }

        Vector3 half = SlerpHalfSpan(prevHalf, len0, currHalf, len1, alpha);
        outA = mid - half;
        outB = mid + half;
    }

    private static Vector3 SlerpHalfSpan(Vector3 half0, float len0, Vector3 half1, float len1, float alpha)
    {
        Vector3 dir0 = half0 / len0;
        Vector3 dir1 = half1 / len1;
        float dot = Math.Clamp(dir0.Dot(dir1), -1f, 1f);
        float angle = MathF.Acos(dot);
        Vector3 dir;
        if (angle <= 1e-4f)
        {
            dir = dir0;
        }
        else
        {
            Vector3 axis = dir0.Cross(dir1);
            float axisLenSq = axis.LengthSquared();
            if (axisLenSq > 1e-8f)
            {
                Vector3 axisUnit = axis / MathF.Sqrt(axisLenSq);
                float rot = angle * alpha;
                dir = (dir0 * MathF.Cos(rot)) + (axisUnit.Cross(dir0) * MathF.Sin(rot));
            }
            else
            {
                // Exact 180-degree opposite spin: rotate smoothly around an arbitrary axis orthogonal to dir0.
                Vector3 ortho = MathF.Abs(dir0.Y) < 0.9f ? Vector3.Up : Vector3.Right;
                Vector3 axisUnit = dir0.Cross(ortho).Normalized();
                float rot = MathF.PI * alpha;
                dir = (dir0 * MathF.Cos(rot)) + (axisUnit.Cross(dir0) * MathF.Sin(rot));
            }
        }

        float halfLen = len0 + ((len1 - len0) * alpha);
        return dir * halfLen;
    }

    private static int ResolveAdaptiveRibbonSegments(int authoredTrailLen, List<Vector3> ptsA, List<Vector3>? ptsB)
    {
        int baseSeg = Math.Max(1, authoredTrailLen);
        int n = ptsA.Count;
        if (n < 2 || baseSeg >= 24)
        {
            return baseSeg;
        }

        float maxChordSq = 0f;
        float maxTwistRad = 0f;
        for (int i = 0; i + 1 < n; i++)
        {
            float dSq = ptsA[i].DistanceSquaredTo(ptsA[i + 1]);
            if (ptsB is not null)
            {
                float bSq = ptsB[i].DistanceSquaredTo(ptsB[i + 1]);
                if (bSq > dSq) dSq = bSq;
            }
            if (dSq <= 36.0f && dSq > maxChordSq)
            {
                maxChordSq = dSq;
            }

            if (ptsB is not null && dSq <= 36.0f)
            {
                Vector3 s0 = ptsB[i] - ptsA[i];
                Vector3 s1 = ptsB[i + 1] - ptsA[i + 1];
                float l0Sq = s0.LengthSquared();
                float l1Sq = s1.LengthSquared();
                if (l0Sq > 1e-6f && l1Sq > 1e-6f)
                {
                    float dot = Math.Clamp(s0.Dot(s1) / MathF.Sqrt(l0Sq * l1Sq), -1f, 1f);
                    float ang = MathF.Acos(dot);
                    if (ang > maxTwistRad)
                    {
                        maxTwistRad = ang;
                    }
                }
            }
        }

        int adaptive = baseSeg;
        if (maxChordSq > 0.18f * 0.18f)
        {
            int chordSegs = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(maxChordSq) / 0.18f), 1, 12);
            if (chordSegs > adaptive)
            {
                adaptive = chordSegs;
            }
        }

        // Helical corkscrew double ribbons (e.g. flying Blades `FX_B2848F7C`, authored at `sub = 30`,
        // twisting 120°-150° per guest step) need angular-aware subdivision to stay smooth.
        if (maxTwistRad > 0.52f)
        {
            int twistSegs = Math.Clamp((int)MathF.Ceiling(maxTwistRad / (MathF.PI / 16f)), 1, 24);
            if (twistSegs > adaptive)
            {
                adaptive = twistSegs;
            }
        }

        return adaptive;
    }

    private void EmitRibbon(
        Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>> sink, EmitterRuntime rt, List<VisualRuntime> vis,
        List<Vector3> pts, List<float> ages, List<Vector4> motions, Vector3 eye, bool constWidth, float[] lit, float[] instTint, float intensity)
    {
        int n = pts.Count;
        if (n < 2 || vis.Count == 0) return;

        EvaluateRibbonAttributes(rt, vis, pts, null, ages, eye, lit, instTint, intensity, halfWidthScale: true, constWidth,
            out float[] attrW, out float[] attrA, out Vector3[] attrC, out int[] attrTex);

        int nseg = ResolveAdaptiveRibbonSegments(vis[0].TrailLen, pts, null);
        Vector3 pre = pts[0] - (pts[1] - pts[0]);
        Vector3 post = pts[n - 1] + (pts[n - 1] - pts[n - 2]);
        int ribs = ((n - 1) * nseg) + 1;
        Vector3[] rA = RentV(ref _ribEdgeA, ribs);
        Vector3[] rB = RentV(ref _ribEdgeB, ribs);
        Vector3[] rc = RentV(ref _ribC, ribs);
        float[] ra = RentF(ref _ribA, ribs);
        int[] rtex = RentI(ref _ribTex, ribs);
        Vector4[] rm = _ribM.Length >= ribs ? _ribM : (_ribM = new Vector4[ribs]);
        for (int j = 0; j < ribs; j++)
        {
            RibbonSpan(j, ribs, nseg, n, out int span, out float sfrac, out int a1);
            EvalCatmullRom(pts, pre, post, span, sfrac, out Vector3 pos, out Vector3 tan);
            if (tan.LengthSquared() < 1e-12f) tan = pts[span + 1] - pts[span];
            for (int d = 1; tan.LengthSquared() < 1e-12f && (span - d >= 0 || span + 1 + d < n); d++)
            {
                int i0 = Math.Max(0, span - d);
                int i1 = Math.Min(n - 1, span + 1 + d);
                tan = pts[i1] - pts[i0];
            }
            tan = tan.LengthSquared() > 1e-12f ? tan.Normalized() : Vector3.Up;
            Vector3 side = tan.Cross(eye - pos);
            side = side.LengthSquared() > 1e-12f ? side.Normalized() : Vector3.Up;
            float wj = attrW[span] + ((attrW[a1] - attrW[span]) * sfrac);
            Vector3 offset = side * wj;
            rA[j] = pos + offset;
            rB[j] = pos - offset;
            rc[j] = attrC[span] + ((attrC[a1] - attrC[span]) * sfrac);
            ra[j] = attrA[span] + ((attrA[a1] - attrA[span]) * sfrac);
            rtex[j] = attrTex[span];
            rm[j] = motions[span].Lerp(motions[a1], sfrac);
        }

        EmitRibbonQuads(sink, rt, vis, ribs, rA, rB, rc, ra, rtex, rm, rm);
    }

    private void EmitDoubleRibbon(
        Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>> sink, EmitterRuntime rt, List<VisualRuntime> vis,
        List<Vector3> ptsA, List<Vector3> ptsB, List<float> ages, List<Vector4> motionsA, List<Vector4> motionsB,
        Vector3 eye, float[] lit, float[] instTint, float intensity)
    {
        int n = ptsA.Count;
        if (n < 2 || ptsB.Count != n || vis.Count == 0) return;

        EvaluateRibbonAttributes(rt, vis, ptsA, ptsB, ages, eye, lit, instTint, intensity, halfWidthScale: false, constWidth: false,
            out float[] attrS, out float[] attrA, out Vector3[] attrC, out int[] attrTex);

        int nseg = ResolveAdaptiveRibbonSegments(vis[0].TrailLen, ptsA, ptsB);
        Vector3 preA = ptsA[0] - (ptsA[1] - ptsA[0]);
        Vector3 postA = ptsA[n - 1] + (ptsA[n - 1] - ptsA[n - 2]);
        Vector3 preB = ptsB[0] - (ptsB[1] - ptsB[0]);
        Vector3 postB = ptsB[n - 1] + (ptsB[n - 1] - ptsB[n - 2]);

        int ribs = ((n - 1) * nseg) + 1;
        Vector3[] rA = RentV(ref _ribEdgeA, ribs);
        Vector3[] rB = RentV(ref _ribEdgeB, ribs);
        Vector3[] rc = RentV(ref _ribC, ribs);
        float[] ra = RentF(ref _ribA, ribs);
        int[] rtex = RentI(ref _ribTex, ribs);
        Vector4[] rmA = _ribMA.Length >= ribs ? _ribMA : (_ribMA = new Vector4[ribs]);
        Vector4[] rmB = _ribMB.Length >= ribs ? _ribMB : (_ribMB = new Vector4[ribs]);

        for (int j = 0; j < ribs; j++)
        {
            RibbonSpan(j, ribs, nseg, n, out int span, out float sfrac, out int idx1);
            EvalCatmullRom(ptsA, preA, postA, span, sfrac, out Vector3 ea, out _);
            EvalCatmullRom(ptsB, preB, postB, span, sfrac, out Vector3 eb, out _);
            float sj = attrS[span] + ((attrS[idx1] - attrS[span]) * sfrac);
            Vector3 mid = 0.5f * (ea + eb);
            Vector3 half0 = 0.5f * (ptsB[span] - ptsA[span]);
            Vector3 half1 = 0.5f * (ptsB[idx1] - ptsA[idx1]);
            float len0 = half0.Length();
            float len1 = half1.Length();
            if (len0 > 1e-5f && len1 > 1e-5f && (half0.Dot(half1) / (len0 * len1)) < 0.995f)
            {
                Vector3 half = SlerpHalfSpan(half0, len0, half1, len1, sfrac) * sj;
                rA[j] = mid - half;
                rB[j] = mid + half;
            }
            else
            {
                rA[j] = mid + ((ea - mid) * sj);
                rB[j] = mid + ((eb - mid) * sj);
            }
            rc[j] = attrC[span] + ((attrC[idx1] - attrC[span]) * sfrac);
            ra[j] = attrA[span] + ((attrA[idx1] - attrA[span]) * sfrac);
            rtex[j] = attrTex[span];
            rmA[j] = motionsA[span].Lerp(motionsA[idx1], sfrac);
            rmB[j] = motionsB[span].Lerp(motionsB[idx1], sfrac);
        }

        EmitRibbonQuads(sink, rt, vis, ribs, rA, rB, rc, ra, rtex, rmA, rmB);
    }

    private static float LitBoostCompensation(string blendMode)
    {
        float boost = MathF.Max(Refii.Lighting.Engine_PostFxParams.EmissionBoost, 1f);
        if (boost <= 1.0001f)
        {
            return 1f;
        }

        if (blendMode == "add")
        {
            return 1f / boost;
        }

        if (blendMode == "addone")
        {
            return Engine_FxAdditiveLayer.Present ? 1f / (boost * boost) : 1f / boost;
        }

        return 1f;
    }

    public static float ComputeDoubleRibbonLitAttenuation(
        List<Vector3> ptsA, List<Vector3> ptsB, int k, Vector3 eye, string blendMode)
    {
        int n = ptsA.Count;
        if (n < 2 || ptsB.Count != n)
        {
            return 1f;
        }

        Vector3 spanVec = ptsA[k] - ptsB[k];
        float spanLenSq = spanVec.LengthSquared();
        int iPrev = Math.Max(0, k - 1);
        int iNext = Math.Min(n - 1, k + 1);
        Vector3 midK = 0.5f * (ptsA[k] + ptsB[k]);
        Vector3 midPrev = 0.5f * (ptsA[iPrev] + ptsB[iPrev]);
        Vector3 midNext = 0.5f * (ptsA[iNext] + ptsB[iNext]);
        Vector3 tanVec = midNext - midPrev;
        float tanLenSq = tanVec.LengthSquared();
        if (spanLenSq <= 1e-10f || tanLenSq <= 1e-10f)
        {
            return 1f;
        }

        Vector3 spanUnit = spanVec / MathF.Sqrt(spanLenSq);
        Vector3 tanUnit = tanVec / MathF.Sqrt(tanLenSq);
        // Xbox 360 DOUBLE_TRAILS_VERTEX_SHADER leaves cross(unit(edgeA - edgeB), unit(tangent))
        // un-normalised: 1.0 on broadside melee swings (span ⊥ motion), small on point-first flight.
        float normalMag = Math.Clamp(spanUnit.Cross(tanUnit).Length(), 0f, 1f);

        // Detect fast helical corkscrew twist between adjacent spans (e.g. flying Blades FX_B2848F7C
        // twisting ~120°-150° per step vs < 25° on melee sword arcs).
        float maxTwistRad = 0f;
        if (k > 0)
        {
            Vector3 prevSpan = ptsA[k - 1] - ptsB[k - 1];
            float pLenSq = prevSpan.LengthSquared();
            if (pLenSq > 1e-10f)
            {
                float d = Math.Clamp(spanUnit.Dot(prevSpan / MathF.Sqrt(pLenSq)), -1f, 1f);
                maxTwistRad = MathF.Acos(d);
            }
        }
        if (k + 1 < n)
        {
            Vector3 nextSpan = ptsA[k + 1] - ptsB[k + 1];
            float nLenSq = nextSpan.LengthSquared();
            if (nLenSq > 1e-10f)
            {
                float d = Math.Clamp(spanUnit.Dot(nextSpan / MathF.Sqrt(nLenSq)), -1f, 1f);
                float ang = MathF.Acos(d);
                if (ang > maxTwistRad) maxTwistRad = ang;
            }
        }

        float cylFactor = 1f;
        if (maxTwistRad > 0.55f)
        {
            float twistT = Math.Clamp((maxTwistRad - 0.55f) / 1.85f, 0f, 1f);
            cylFactor = 1f - (0.66f * twistT);
            if (blendMode == "addone")
            {
                // Guest ONE:ONE draws into an A8R8G8B8 UNORM target that saturates end-on corkscrew
                // self-overlap along the view ray, whereas our FP16 layer accumulates linearly.
                Vector3 toEye = eye - midK;
                float toEyeLenSq = toEye.LengthSquared();
                if (toEyeLenSq > 1e-6f)
                {
                    float endOn = MathF.Abs((toEye / MathF.Sqrt(toEyeLenSq)).Dot(tanUnit));
                    cylFactor *= 1f - (0.52f * endOn * endOn * twistT);
                }
            }
        }

        float effectiveMag = normalMag * cylFactor;
        return 0.20f + (0.80f * Math.Clamp(effectiveMag, 0f, 1f));
    }

    private void EvaluateRibbonAttributes(
        EmitterRuntime rt, List<VisualRuntime> vis, List<Vector3> ptsA, List<Vector3>? ptsB, List<float> ages,
        Vector3 eye, float[] lit, float[] instTint, float intensity, bool halfWidthScale, bool constWidth,
        out float[] attrW, out float[] attrA, out Vector3[] attrC, out int[] attrTex)
    {
        int n = ptsA.Count;
        int nvis = vis.Count;
        attrW = RentF(ref _ribAttrW, n);
        attrA = RentF(ref _ribAttrA, n);
        attrC = RentV(ref _ribAttrC, n);
        attrTex = RentI(ref _ribAttrTex, n);
        float tr = _tint[0] * instTint[0], tg = _tint[1] * instTint[1], tb = _tint[2] * instTint[2];
        for (int k = 0; k < n; k++)
        {
            float age = ages[k];
            int seg = 1;
            while (seg < nvis && age >= vis[seg].DelayFrames) seg++;
            int sb = nvis <= 1 ? 0 : Math.Min(seg, nvis - 1);
            int sa = Math.Max(sb - 1, 0);
            VisualRuntime va = vis[sa], vb = vis[sb];
            float w = sb > sa ? Math.Clamp((age - va.DelayFrames) * vb.InvSpan, 0f, 1f) : 0f;
            float width = constWidth ? vis[0].TrailWidth : (va.TrailWidth + ((vb.TrailWidth - va.TrailWidth) * w));
            attrW[k] = halfWidthScale ? width * 0.5f * _sizeScale : width;
            float si = va.SelfIllum + ((vb.SelfIllum - va.SelfIllum) * w);
            float litScale = si < 0.999f ? LitBoostCompensation(va.BlendMode) : 1f;
            if (ptsB is not null && si < 0.999f)
            {
                litScale *= ComputeDoubleRibbonLitAttenuation(ptsA, ptsB, k, eye, va.BlendMode);
            }

            float litR = lit[0] * litScale;
            float litG = lit[1] * litScale;
            float litB = lit[2] * litScale;
            attrC[k] = new Vector3(
                (litR + ((va.Colour[0] + ((vb.Colour[0] - va.Colour[0]) * w) - litR) * si)) * tr,
                (litG + ((va.Colour[1] + ((vb.Colour[1] - va.Colour[1]) * w) - litG) * si)) * tg,
                (litB + ((va.Colour[2] + ((vb.Colour[2] - va.Colour[2]) * w) - litB) * si)) * tb);
            Vector3 fadePos = ptsB is not null ? 0.5f * (ptsA[k] + ptsB[k]) : ptsA[k];
            attrA[k] = (va.Alpha + ((vb.Alpha - va.Alpha) * w)) * DistanceFadeFactor(rt, fadePos, eye) * intensity;
            attrTex[k] = sa;
        }
    }

    private static void RibbonSpan(int rib, int ribs, int nseg, int n, out int span, out float sfrac, out int nextIndex)
    {
        if (rib == ribs - 1)
        {
            span = n - 2;
            sfrac = 1f;
        }
        else
        {
            span = Math.Min(rib / nseg, n - 2);
            sfrac = (float)(rib - (span * nseg)) / nseg;
        }

        nextIndex = Math.Min(span + 1, n - 1);
    }

    private static void EvalCatmullRom(
        List<Vector3> pts, Vector3 pre, Vector3 post, int span, float sfrac, out Vector3 pos, out Vector3 tan)
    {
        int n = pts.Count;
        Vector3 Ctrl(int i) => i < 0 ? pre : i >= n ? post : pts[i];
        Vector3 p0 = Ctrl(span - 1), p1 = Ctrl(span), p2 = Ctrl(span + 1), p3 = Ctrl(span + 2);
        Vector3 c1 = 0.5f * (p2 - p0);
        Vector3 c2 = p0 - (2.5f * p1) + (2f * p2) - (0.5f * p3);
        Vector3 c3 = (-0.5f * p0) + (1.5f * p1) - (1.5f * p2) + (0.5f * p3);
        float s2 = sfrac * sfrac;
        pos = p1 + (c1 * sfrac) + (c2 * s2) + (c3 * s2 * sfrac);
        tan = c1 + (2f * c2 * sfrac) + (3f * c3 * s2);
    }

    private static void EmitRibbonQuads(
        Func<VisualRuntime, int, List<(Vector3, Vector2, Color, Vector2, Vector4)>> sink,
        EmitterRuntime rt, List<VisualRuntime> vis, int ribs,
        Vector3[] rA, Vector3[] rB, Vector3[] rc, float[] ra, int[] rtex, Vector4[] rmA, Vector4[] rmB)
    {
        for (int j = 0; j + 1 < ribs; j++)
        {
            if (ra[j] <= 0.003f && ra[j + 1] <= 0.003f) continue;
            if (rt.Ribbon)
            {
                Vector3 mid0 = 0.5f * (rA[j] + rB[j]);
                Vector3 mid1 = 0.5f * (rA[j + 1] + rB[j + 1]);
                if (mid0.DistanceSquaredTo(mid1) > 16.0f) continue;
            }
            VisualRuntime tv = vis[rtex[j]];
            if (tv.Texture.Length == 0) continue;
            List<(Vector3, Vector2, Color, Vector2, Vector4)> list = sink(tv, rt.ZOrder);
            float v0 = (float)j / (ribs - 1), v1 = (float)(j + 1) / (ribs - 1);
            var c0 = new Color(rc[j].X, rc[j].Y, rc[j].Z, MathF.Max(ra[j], 0f));
            var c1 = new Color(rc[j + 1].X, rc[j + 1].Y, rc[j + 1].Z, MathF.Max(ra[j + 1], 0f));
            list.Add((rA[j], new Vector2(0f, v0), c0, Vector2.Zero, rmA[j]));
            list.Add((rA[j + 1], new Vector2(0f, v1), c1, Vector2.Zero, rmA[j + 1]));
            list.Add((rB[j], new Vector2(1f, v0), c0, Vector2.Zero, rmB[j]));
            list.Add((rB[j], new Vector2(1f, v0), c0, Vector2.Zero, rmB[j]));
            list.Add((rA[j + 1], new Vector2(0f, v1), c1, Vector2.Zero, rmA[j + 1]));
            list.Add((rB[j + 1], new Vector2(1f, v1), c1, Vector2.Zero, rmB[j + 1]));
        }
    }

    /// <summary>Whether the guest has faded this effect's emitters out by distance.</summary>
    public static bool ShouldCullByFade(float lightFade) => lightFade <= 0.001f;

    /// <summary>
    /// Whether this effect should skip host particle geometry emission (either because the guest
    /// faded it out by distance or because the guest is drawing its particles and no displacement
    /// or un-twinned mesh particles remain on the host).
    /// </summary>
    public static bool ShouldRunLightsOnly(
        bool guestDrawn,
        bool hasDisplacement,
        bool guestMeshTwins,
        bool hasMeshParticles,
        float lightFade) =>
        ShouldCullByFade(lightFade) || (guestDrawn && !hasDisplacement && (guestMeshTwins || !hasMeshParticles));

    /// <summary>
    /// Whether `_addMesh` may be populated on this step. When `guestDrawn` is true (even if
    /// `_hasDisplacement` keeps `FableFx` stepping for `_dispMesh`), `_addMesh` must be treated as
    /// null and cleared so no frame-0 fallback additive surfaces remain frozen on screen.
    /// </summary>
    public static bool ShouldUseAdditiveMesh(bool guestDrawn, bool hasAddMesh, bool additiveLayerPresent) =>
        !guestDrawn && hasAddMesh && additiveLayerPresent;

    /// <summary>
    /// Fills the 20-entry shader keyframe tables from the up-to-19 keyframes in the material header,
    /// padding trailing slots with the final keyframe (`sub_821CA108` at `0x821CA250..0x821CA264`).
    /// </summary>
    public static void FillGuestKeyframeTables(
        ReadOnlySpan<float> buffer,
        int baseOffset,
        int keys,
        float scale,
        Span<Vector4> colours,
        Span<Vector4> scales,
        Span<Vector4> globalScales)
    {
        keys = Math.Clamp(keys, 0, 19);
        int count = Math.Min(20, Math.Min(colours.Length, Math.Min(scales.Length, globalScales.Length)));
        for (int k = 0; k < count; k++)
        {
            if (k < keys)
            {
                int o = baseOffset + k * 10;
                colours[k] = new Vector4(buffer[o], buffer[o + 1], buffer[o + 2], buffer[o + 3]);
                scales[k] = new Vector4(buffer[o + 4], buffer[o + 5], buffer[o + 8], buffer[o + 9]);
                globalScales[k] = new Vector4(buffer[o + 6] * scale, buffer[o + 7] * scale, 0f, 0f);
            }
            else if (keys > 0)
            {
                colours[k] = colours[keys - 1];
                scales[k] = scales[keys - 1];
                globalScales[k] = globalScales[keys - 1];
            }
            else
            {
                colours[k] = Vector4.Zero;
                scales[k] = Vector4.Zero;
                globalScales[k] = Vector4.Zero;
            }
        }
    }

    /// <summary>Near freeze distance used under Classic or when Modern lighting is inactive.</summary>
    public const float ClassicFreezeDistance = 50f;

    /// <summary>
    /// Extended in-frustum simulation reach under Modern lighting so distant flames in view (50..200 m)
    /// keep animating instead of freezing after the guest withdraws its 50 m render twin.
    /// </summary>
    public const float ModernFreezeDistance = 200f;

    /// <summary>Active in-frustum simulation distance for <see cref="FableFx"/>.</summary>
    public static float EffectiveFreezeDistance(bool modernModel) =>
        modernModel && !Refii.AndroidRuntime.AndroidLeanModern.Fx ? ModernFreezeDistance : ClassicFreezeDistance;

    /// <summary>
    /// Builds <c>vs_g_FadeOutDistanceInfo</c> (<c>R+384</c> slots <c>f2, f3, f4</c>). When
    /// <paramref name="disableDistanceFade"/> is true (Modern renderer + Modern lighting, matching
    /// <c>Engine_FxSystem.FadeOut == false</c>), disables the Xbox 360 pre-50 m alpha ramp while
    /// preserving the emitter's intensity multiplier <paramref name="f4"/> in <c>.z</c>.
    /// </summary>
    public static Vector4 BuildGuestFadeOutInfo(float f2, float f3, float f4, bool disableDistanceFade) =>
        disableDistanceFade
            ? new Vector4(1e6f, 1f, f4, 0f)
            : new Vector4(f3, f3 > f2 ? 1f / (f3 - f2) : 1f, f4, 0f);

    /// <summary>
    /// Proper right-handed (<c>det = +1</c>) Godot entity-mesh basis corresponding to an unrotated
    /// guest world placement (<c>rows = I</c>). Safe for <see cref="Refii.Core.Engine_TickTimeline"/>
    /// quaternion slerp without losing handedness.
    /// </summary>
    public static Basis EntityGuestIdentity =>
        new(new Vector3(-1f, 0f, 0f), Vector3.Up, new Vector3(0f, 0f, -1f));

    /// <summary>
    /// Simulation (<c>FableFx</c>) basis corresponding to an unrotated guest world placement
    /// (<c>det = -1</c>, axis swap <c>(x, z, y)</c>).
    /// </summary>
    public static Basis FxGuestIdentity =>
        new(new Vector3(-1f, 0f, 0f), Vector3.Up, new Vector3(0f, 0f, 1f));

    /// <summary>
    /// Converts between <c>GameEntities</c> mesh space (<c>det = +1</c>) and <c>FableFx</c>
    /// simulation space (<c>det = -1</c>) by negating basis column 2 (self-inverse involution,
    /// matching <c>Engine_LevelFx.SimFromMesh = diag(1, 1, -1)</c>).
    /// </summary>
    public static Transform3D EntityMeshToFxTransform(Transform3D entityWorld) =>
        new(new Basis(entityWorld.Basis.Column0, entityWorld.Basis.Column1, -entityWorld.Basis.Column2), entityWorld.Origin);

    /// <summary>Converts a <c>FableFx</c> simulation transform (<c>det = -1</c>) into proper <c>SO(3)</c> entity-mesh space (<c>det = +1</c>).</summary>
    public static Transform3D FxToEntityMeshTransform(Transform3D fxWorld) =>
        EntityMeshToFxTransform(fxWorld);

    /// <summary>
    /// True when a 16-float row-major guest matrix (<c>R+320</c>) is bit-exact 4x4 identity at the
    /// world origin — the signature of a world-space emitter whose particle positions are already
    /// baked in guest world coordinates.
    /// </summary>
    public static bool IsExactGuestIdentity4x4(ReadOnlySpan<float> m) =>
        m.Length >= 16 &&
        m[0] == 1f && m[1] == 0f && m[2] == 0f && m[3] == 0f &&
        m[4] == 0f && m[5] == 1f && m[6] == 0f && m[7] == 0f &&
        m[8] == 0f && m[9] == 0f && m[10] == 1f && m[11] == 0f &&
        m[12] == 0f && m[13] == 0f && m[14] == 0f && m[15] == 1f;

    private static Vector3 SwapGuestGodot(Vector3 v) => new(-v.X, v.Z, v.Y);

    /// <summary>
    /// Decomposes a guest row-major 4x4 world matrix (<c>R+320</c>) into an orthonormal right-handed
    /// (<c>det = +1</c>) entity-mesh <see cref="Transform3D"/> suitable for <see cref="Refii.Core.Engine_TickTimeline"/>
    /// plus per-row guest scale <c>(sx, sy, sz)</c>.
    /// </summary>
    public static bool TryDecomposeGuestWorldMatrix(
        ReadOnlySpan<float> m,
        out Transform3D entityWorld,
        out Vector3 guestScale,
        out bool identityBasis)
    {
        entityWorld = Transform3D.Identity;
        guestScale = Vector3.One;
        identityBasis = false;
        if (m.Length < 16)
        {
            return false;
        }

        var r0 = new Vector3(m[0], m[1], m[2]);
        var r1 = new Vector3(m[4], m[5], m[6]);
        var r2 = new Vector3(m[8], m[9], m[10]);
        var pos = new Vector3(m[12], m[13], m[14]);
        if (!r0.IsFinite() || !r1.IsFinite() || !r2.IsFinite() || !pos.IsFinite() || pos.LengthSquared() >= 1e12f)
        {
            return false;
        }

        identityBasis = r0 == new Vector3(1f, 0f, 0f) &&
                        r1 == new Vector3(0f, 1f, 0f) &&
                        r2 == new Vector3(0f, 0f, 1f);
        if (identityBasis)
        {
            entityWorld = new Transform3D(EntityGuestIdentity, SwapGuestGodot(pos));
            return true;
        }

        float sx = r0.Length();
        float sy = r1.Length();
        float sz = r2.Length();
        if (sx < 1e-5f || sy < 1e-5f || sz < 1e-5f)
        {
            return false;
        }

        Vector3 u0 = r0 / sx;
        Vector3 u1 = r1 / sy;
        Vector3 u2 = r2 / sz;
        float det = u0.Cross(u1).Dot(u2);
        if (!float.IsFinite(det) || MathF.Abs(det) < 0.01f)
        {
            return false;
        }

        if (det < 0f)
        {
            sz = -sz;
            u2 = -u2;
        }

        var basis = new Basis(SwapGuestGodot(u0), SwapGuestGodot(u2), -SwapGuestGodot(u1)).Orthonormalized();
        guestScale = new Vector3(sx, sy, sz);
        entityWorld = new Transform3D(basis, SwapGuestGodot(pos));
        return true;
    }

    /// <summary>
    /// Recomposes an orthonormal right-handed entity-mesh <see cref="Transform3D"/> and guest per-row
    /// scale back into the 3 transposed <c>vs_g_MatWorld</c> column vectors (<c>sub_82270CD0</c>).
    /// </summary>
    public static void ComposeGuestMatWorldColumns(
        Transform3D entityWorld,
        Vector3 guestScale,
        Span<Vector4> outColumns)
    {
        if (outColumns.Length < 3)
        {
            return;
        }

        Vector3 r0 = SwapGuestGodot(entityWorld.Basis.Column0) * guestScale.X;
        Vector3 r1 = SwapGuestGodot(-entityWorld.Basis.Column2) * guestScale.Y;
        Vector3 r2 = SwapGuestGodot(entityWorld.Basis.Column1) * guestScale.Z;
        Vector3 pos = SwapGuestGodot(entityWorld.Origin);

        outColumns[0] = new Vector4(r0.X, r1.X, r2.X, pos.X);
        outColumns[1] = new Vector4(r0.Y, r1.Y, r2.Y, pos.Y);
        outColumns[2] = new Vector4(r0.Z, r1.Z, r2.Z, pos.Z);
    }
}

public sealed class VisualBatchMaterial
{
    public required Material Material { get; init; }
}
