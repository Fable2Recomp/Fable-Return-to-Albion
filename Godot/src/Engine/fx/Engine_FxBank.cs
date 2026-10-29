using System;
using System.Collections.Generic;

namespace Refii.Fx;

public sealed class FxTimeline
{
    public List<(float Time, float Value)> Keys { get; } = new();

    public float DefaultValue { get; set; }

    public Xex.XexScalarTimeline Engine { get; set; } = new();

    public bool IsEmpty => Keys.Count == 0;

    public float Eval(float t) => Keys.Count == 0 ? DefaultValue : Engine.Eval((int)(t * Xex.XexConst.Fps));

    public float EvalFrame(int frame) => Keys.Count == 0 ? DefaultValue : Engine.Eval(frame);

    public float FirstOr(float fallback) => Keys.Count == 0 ? fallback : Keys[0].Value;

    public float MaxValue()
    {
        float m = Keys.Count == 0 ? DefaultValue : Keys[0].Value;
        foreach ((_, float v) in Keys)
        {
            if (v > m)
            {
                m = v;
            }
        }

        return m;
    }
}

public sealed class FxVectorTimeline
{
    public List<(float Time, float X, float Y, float Z)> Keys { get; } = new();

    public float[] DefaultValue { get; set; } = { 0f, 0f, 0f };

    public Xex.XexVecTimeline Engine { get; set; } = new();

    public bool IsEmpty => Keys.Count == 0;

    private readonly float[] _tmp = new float[4];

    public (float X, float Y, float Z) Eval(float t) => EvalFrame((int)(t * Xex.XexConst.Fps));

    public (float X, float Y, float Z) EvalFrame(int frame)
    {
        if (Keys.Count == 0)
        {
            return (DefaultValue[0], DefaultValue[1], DefaultValue[2]);
        }

        Engine.Eval(frame, _tmp);
        return (_tmp[0], _tmp[1], _tmp[2]);
    }
}

public sealed class FxEmitter
{
    public uint TypeId;
    public uint EntityId;
    public uint SubType;

    public float[] Pos = new float[3];
    public float[] Quat = { 0f, 0f, 0f, 1f };

    public float MaxActive;
    public float InitialNum;
    public float StartTimeSecs;

    /// <summary>
    /// The emitter shape's radius. Which shape it is a radius OF is <see cref="SubType"/> — the
    /// header's <c>EmitterShape</c> enum, and only the standard emitter consults it
    /// (<c>sub_8322A9B0</c>'s inner switch at 0x8322AA8C):
    /// 0 point, 1 <b>Sphere</b>, 2 <b>Disc</b>, 3 Mesh.
    /// </summary>
    public float SpawnRadius;

    /// <summary>
    /// Sphere: emit exactly ON the surface. Disc: emit exactly on the EDGE. Otherwise the radius
    /// is <c>rand01 * R</c> — uniform in RADIUS, not in volume or area, so the guest's own
    /// distribution is centre-heavy and reproducing it means NOT doing the usual cube-root
    /// correction. `sub_83224630` at 0x832247D4 and `sub_832240C0` at 0x832241DC.
    /// </summary>
    public bool EmitOnSurface;

    /// <summary>
    /// Take the spawn DIRECTION from the shape instead of the cone: the particle flies radially
    /// outward from the shape's centre. `0x82A04314` branches past the cone builder entirely when
    /// this is set, and Speed then scales the radial direction. For a Disc the radial direction is
    /// IN-PLANE (`sub_832240C0` at 0x83224278), not along the disc's normal.
    /// </summary>
    public bool UseShapeSpawnDir;

    public FxTimeline Rate = new();
    public FxTimeline RateSpread = new();
    public FxTimeline Speed = new();
    public FxTimeline SpeedSpread = new();

    /// <summary>
    /// Generic parameter 6 — the magnitude along the emitter's OWN authored acceleration
    /// direction, which is vector timeline 0 rotated into world space.
    ///
    /// NOT an X axis. These three were called AccX/AccY/AccZ, and that reading cost real time:
    /// it made 415 emitters look like they were accelerating "on Y" when what they are actually
    /// saying is "fall". `sub_8322D988` assembles ONE acceleration vector per emitter per frame
    /// out of three unrelated terms, and 6, 7 and 8 are the three weights, not three components.
    /// </summary>
    public FxTimeline AccelDirScale = new();

    /// <summary>
    /// Generic parameter 7 — THE GRAVITY MULTIPLIER. 1.0 means normal gravity.
    ///
    /// `sub_8322D988` scales the constant vector at `unk_8349E4D0` by this and adds it into the
    /// frame's acceleration (`vmulfp128 v28, v0, v29` at 0x8322dbe0, v0 = unk_8349E4D0). That
    /// global is built by `sub_832845B0` out of `flt_82099484` = 0.0 and `flt_82000E34`, and
    /// 0x82000E34 holds the bytes `C1 1C CC CD` — <b>-9.8</b>. The world is Z-up, so the vector
    /// is (0, 0, -9.8): straight down.
    ///
    /// 415 of the bank's 2277 standard emitters state it, and until it was integrated every
    /// spark, ember, splash and falling scrap in the game travelled in a straight line forever.
    /// </summary>
    public FxTimeline GravityScale = new();

    /// <summary>
    /// Generic parameter 8 — the multiplier on the GLOBAL WIND vector `unk_8349E4B0`.
    ///
    /// Parsed and deliberately not applied. That global is zeroed by `sub_83284538` and only
    /// written at runtime by the weather system (`sub_8260DD48`), so on a still day it is
    /// (0,0,0) and this term contributes nothing — which is every day, because we do not drive
    /// the weather system yet. When <c>ThemeParticleFX</c> lands this is where wind joins.
    /// </summary>
    public FxTimeline GlobalWindScale = new();

    /// <summary>
    /// Generic parameters 9, 10 and 11 — the spawn direction, as
    /// <c>(p9·cos θ, p10·sin θ, p11)</c> for one azimuth θ shared by X and Y. See
    /// <c>Engine_FxSystem.SpawnOne</c>, which also documents the degenerate case.
    /// </summary>
    public FxTimeline VelX = new();

    public FxTimeline VelY = new();

    public FxTimeline VelZ = new();

    /// <summary>Generic parameter 12. Read, not applied; the emitter-level damping pair.</summary>
    public FxTimeline Wind = new();

    public bool HasMotion;

    /// <summary>
    /// <c>SortParticles</c> at <c>CParticleEmitterType+220</c>. Read and deliberately not acted
    /// on: the guest's own per-particle depth sort is UNREACHABLE in the shipped build — its gate
    /// tests <c>*(*(this+0x20)+32) == 0</c> while the key-fill beside it requires the same word to
    /// be 1, so the key array is always null and the sorted-index pointer handed to the batch
    /// builder is always null. Our unsorted draw matches what actually ships.
    /// </summary>
    public bool SortParticles;

    /// <summary><c>ForceToFullSizeRenderBuffer</c> at +221. A render-target choice we do not make.</summary>
    public bool ForceToFullSizeRenderBuffer;

    /// <summary>
    /// <c>AttachToRoot</c> at <c>CParticleEmitterType+222</c> — WHICH SPACE THE PARTICLES LIVE IN,
    /// and the difference between a trail and a smear.
    ///
    /// `sub_8322F2A0` reads this byte and either binds the emitter to the effect's ROOT instance
    /// or clears that reference. Everything downstream follows from it: `sub_8322EE40` gives the
    /// spawn matrix relative to that ancestor when it is set and the emitter's FULL WORLD matrix
    /// when it is not, and `sub_8322F1E0` hands the particle container the ancestor's world matrix
    /// each frame — or IDENTITY when there is no ancestor.
    ///
    ///   true  -> particles are stored in the root's space and re-transformed every frame, so they
    ///            follow a moving effect rigidly.
    ///   false -> particles are stored in WORLD space with the spawn transform baked in, so a
    ///            moving emitter LEAVES THEM BEHIND.
    ///
    /// False on 1667 of the bank's 2303 emitters. It cannot be seen while an effect stands still,
    /// which is why it was invisible for static level ambience — but it is the whole difference
    /// between a projectile that lays down a trail and one that drags its smoke along with it.
    /// </summary>
    public bool AttachToRoot;

    /// <summary>
    /// This entity is a <c>CPointLightEntityType</c> — a REAL dynamic light, not a sprite.
    ///
    /// 72 of the bank's 2751 entities are these, and until now every one was parsed and thrown
    /// away: a torch emitted its flame and lit nothing. `sub_8323FA50` builds a light descriptor
    /// through `sub_82A49D08`, which is the SAME builder the placed level lights go through
    /// (`sub_825C2108` calls it at 0x825c2264), so an FX light is the same kind of object as a
    /// fixture in the level — it just belongs to an effect instead of to a placement.
    ///
    /// Type 8 is the spot-light sibling and does not ship: zero entities in the whole bank.
    /// </summary>
    public bool IsLight;

    /// <summary>
    /// Vector parameter 3 — the light's colour over time, as (r, g, b).
    ///
    /// ITS DEFAULT IS BLACK, which is why it has to be read rather than stepped over: an unread
    /// colour timeline is a light that emits nothing at all.
    ///
    /// EIGHT OF THE 72 STATE IT ON A 0..255 SCALE — (255, 50, 0), (150, 30, 0), (0, 50, 255) —
    /// while the other 64 use 0..1. The values are taken AS STATED rather than rescaled by a
    /// host rule: the guest multiplies this straight into its descriptor with no clamp, so those
    /// lights genuinely are enormously bright in the original too, and a "helpfully" normalised
    /// copy would be our invention rather than the game's.
    /// </summary>
    public FxVectorTimeline LightColour = new();

    /// <summary>
    /// Generic parameter 15 — the light's FALLOFF RADIUS: it lands in live+0x140, the slot the
    /// shader's attenuation coefficient inverts (1/(scale × +0x140) at 0x82a6cec0 / 0x8223b254).
    /// Point path: `li r4,0xF` at 0x8323fb98, passed f1 at the vcall. Spot path: `fmr f2,f30`
    /// at 0x8323f420 → desc+0x90 at 0x82a439c0. Shipped values 1.2–40, median 5. The two FX
    /// light classes (point off_82005968 / spot off_820F2440) have separate register files;
    /// param 17 is the cutoff, not the radius.
    /// </summary>
    public FxTimeline LightRange = new();

    /// <summary>
    /// Generic parameter 24 — the SPOT-CONE HALF-ANGLE in radians, SPOT entities only
    /// (`li r4,0x18` / `fmr f27,f1` at 0x8323f2e0/f0, passed as f1 at the vcall 0x8323f440 →
    /// desc+0x94 at 0x82a439bc → live+0x144). The FX packer takes cos(+0x144) and 1/(1−cos)
    /// as the cone constants (0x82a6cf7c–0x82a6cfb8); fov = 2× clamped to π (0x8228e35c). The
    /// spot AIMS along its node's world-matrix ROW 1 — the local +Y axis (row 1 loaded at
    /// 0x8323f404, negated into the cone-direction constant at 0x82a6ce5c). Point entities
    /// have no cone; empty here.
    /// </summary>
    public FxTimeline LightConeAngle = new();

    /// <summary>
    /// Generic parameter 16 — the intensity the colour is scaled by. `sub_8323FA50` multiplies
    /// the colour vector by (this x the emitter's alive fraction) at 0x8323fb28, so a light fades
    /// out as its emitter runs down rather than snapping off.
    /// </summary>
    public FxTimeline LightIntensity = new();

    /// <summary>
    /// Generic parameter 17 — the CAMERA distance at which the light reaches ZERO, capped at
    /// 1000: intensity × clamp((min(+0x148, 1000) − dist) / rampWidth, 0, 1) in the live
    /// update (0x821f0cd8–0x821f0d7c), and the renderer's selection test skips the light
    /// beyond the same radius (`sub_821FFB30`, 0x821ffb34). Shipped house value 64.
    /// </summary>
    public FxTimeline LightCutoff = new();

    /// <summary>
    /// Generic parameter 18 — the WIDTH of that intensity ramp, in metres INSIDE the cutoff:
    /// the light is full at cutoff − this and zero at cutoff (+0x14C read at 0x821f0cd0; a
    /// width ≤ 0 skips the ramp — then the selection test hard-cuts). Shipped house value 50,
    /// so a typical FX light fades from 14 m out to nothing at 64 m.
    /// </summary>
    public FxTimeline LightFadeRamp = new();

    /// <summary>
    /// The HALO sprite this light entity draws around itself — the texture the guest renders
    /// into its half-res glow layer (the one that reaches the frame ONLY through bloom), and
    /// the data-driven identity of a glow sprite. Zero = the entity states none. Parsed by
    /// Engine_FxBankXex for years and dropped before reaching this record; the glow layer is
    /// why it finally travels.
    /// </summary>
    public uint HaloTextureHash;

    /// <summary>
    /// This entity is a <c>CParticleAttractorType</c> — it pulls the effect's particles toward
    /// itself rather than emitting any of its own.
    ///
    /// 144 of the effects the shipped levels place carry one, which makes this the second largest
    /// gap in the system after splines. Without it the particles that should curl into a vortex,
    /// gather up a chimney or spiral round a fire simply fly straight.
    /// </summary>
    public bool IsAttractor;

    /// <summary>
    /// Generic parameter 26 — the radius of influence, and a hard cutoff rather than a falloff
    /// tail. `sub_8323C098` tests the SQUARED distance against it at 0x8323c120 and contributes
    /// nothing at all beyond it.
    /// </summary>
    public FxTimeline AttractRadius = new();

    /// <summary>
    /// Generic parameter 27 — the pull, ramping LINEARLY to zero at the radius:
    ///
    ///     F = Strength(t) * (1 - d/R) * normalize(attractorPos - particlePos)
    ///
    /// from `v14 = Strength * (1.0 - d/R)` at 0x8323c160 and the negation of the
    /// particle-to-attractor delta at 0x8323c2fc. Positive strength therefore ATTRACTS.
    ///
    /// Evaluated at the ATTRACTOR's own local time, not the particle's — `sub_8323C098` reads
    /// `*(a2 + 252)` off the attractor instance for both timelines.
    /// </summary>
    public FxTimeline AttractStrength = new();

    /// <summary>
    /// Whether this attractor joins the WORLD-wide list rather than only pulling on its own
    /// effect's particles. `sub_83229A98` sums two lists: the effect-local attractors at
    /// inst+220, and every attractor in the game whose flag is set, from the registry at
    /// `dword_8349E4A0`.
    ///
    /// Read, and deliberately not honoured: a cross-effect pull needs one shared world-space
    /// registry, and every effect here simulates in its own node's space. Following it per-effect
    /// would be a different force, not a partial one.
    /// </summary>
    public bool GlobalAttractor;

    /// <summary>
    /// This entity is a <c>CSplineEntityType</c> — a point that TRAVELS A PATH, and whatever the
    /// effect chains after it rides along.
    ///
    /// The biggest single gap in the system: 322 of the effects the shipped levels place contain
    /// one, and the second commonest shape in the whole bank is literally
    /// <c>(spline, emitter)</c> — 367 type-vectors of exactly that. Dropped, every one of those
    /// emitters sits still where it should be sweeping along a curve.
    /// </summary>
    public bool IsSpline;

    /// <summary>The control points, in the spline's own space. Median 2, max 11.</summary>
    public float[][] SplinePoints = System.Array.Empty<float[]>();

    /// <summary>How fast <c>t</c> travels, in units of t per SECOND.</summary>
    public float SplineRate;

    /// <summary>Wrap the control points, making the curve a closed loop.</summary>
    public bool SplineLooped;

    /// <summary>
    /// PING-PONG. At either end of the curve `sub_8323CEF0` flips the direction when this is set,
    /// and otherwise snaps <c>t</c> back to 0 and starts again.
    /// </summary>
    public bool SplineReverseFlow;

    /// <summary>Start at a random <c>t</c> instead of at the beginning.</summary>
    public bool SplineRandomStart;

    /// <summary>Piecewise LINEAR rather than Catmull-Rom.</summary>
    public bool SplineStraight;

    /// <summary>
    /// Replace <c>t</c> with a fresh random value EVERY evaluation, so the point scatters over
    /// the whole curve instead of travelling it — `sub_8322E270` reads the flag and substitutes.
    /// </summary>
    public bool SplineRandomGenerator;

    /// <summary>Read; only reachable through gameplay overriding a spline's points.</summary>
    public bool SplineScaleEmissionByLength;


    public FxTimeline[] Generic = MakeGeneric();

    public FxVectorTimeline Vec0 = new();
    public FxVectorTimeline Vec1 = new();
    public FxVectorTimeline Vec2 = new();

    public FxTimeline P13 = new();
    public FxTimeline P14 = new();

    public bool UseLife;
    public int LifetimeFrames;
    public float ProcessRadius = 32f;
    public float[] Fades = { 0.8f, 1f, 25f, 32f };
    public bool PositionalInteractable;

    public bool ShapeFlagA;

    public bool ShapeFlagB;

    public bool ReleaseTrail;
    public bool GlobalCollider;
    public float CollisionRestitution = 1f;

    public float AngularRotationRate;

    /// <summary>
    /// SPIN: radians per second every particle of this emitter turns about the emitter's own Z axis,
    /// through the emitter's origin, positive counter-clockwise seen down the axis. Applied after each
    /// step of the guest's integrator (`sub_8322CF40`: velocity += acceleration, the attractors'
    /// pull, damping; position += velocity) as one rotation of the particle's position about that
    /// axis and of its velocity with it, by spin ÷ 30 × the step's frames
    /// (`flt_8335F1D4[dword_83496DD4]`), in the frame the particles are stored in: the emitter's
    /// spawn matrix (`unk_8349E5F0`, row 2 the axis, row 3 the origin), which `sub_8322D988` copies
    /// before it steps the emitter (0x8322d9a8..0x8322d9d4, then `sub_8322F1E0` → slot 26).
    /// The bank's record has no field for it (a standard emitter's file states parameters 2..14
    /// only), so the guest reads it from the dress table beside its bank
    /// (<see cref="Engine_FxModEffects.DressKey"/>, Fable2/src/vfs/src/hooks/particle_motion.cpp). 0 for
    /// every shipped emitter: neither Fable II nor Fable III has a force that turns a particle about
    /// an axis — Fable III's twisters turn their EMITTERS on orbitals (type 3).
    /// </summary>
    public float Spin;

    public float SplineRateEmitter = 0.01f;
    public int AnimationMode;
    public int NumSplineSegments = 1;
    public float RandomiseAnimMagnitude = 0.5f;
    public float SmoothAnimationSpeed;
    public float JerkyAnimationRate;
    public bool RandomiseAnimMagnitudeFlag;
    public bool RandomiseAnimationRates;

    private static FxTimeline[] MakeGeneric()
    {
        var a = new FxTimeline[28];
        for (int i = 0; i < 28; i++)
        {
            a[i] = new FxTimeline();
        }

        return a;
    }
}

public sealed class FxVisual
{
    public float Delay;
    public string Texture = string.Empty;
    public int TexCols = 1;
    public int TexRows = 1;
    public bool RandomFrame;
    public float FrameSpeed;
    public bool Displacement;
    public bool HasColor;
    public float[] Color = { 1f, 1f, 1f };

    public bool HasAlpha;
    public float Alpha = 1f;

    public bool HasRotation;
    public float RotSpeed;
    public float RotSpread;
    public bool RotInitialRandom;

    public bool HasAlignQuat;
    public float[] AlignQuat = { 0f, 0f, 0f, 1f };

    public int SrcBlend = -1;
    public int DstBlend = -1;
    public bool HasSize;
    public float SizeX;
    public float SizeY;

    public bool HasFlicker;
    public float FlickerPeriod;
    public float FlickerBaseX = 1f;
    public float FlickerBaseY = 1f;
    public float FlickerSpreadX;
    public float FlickerSpreadY;
    public bool FlickerShared = true;

    public bool HasScalarSize;
    public float ScalarSize = 1f;
    public bool HasNumSegments;
    public int NumSegments = 1;
    public bool HasSelfIllum;
    public float SelfIllum = 1f;
    public bool HasGlobalScale;
    public float[] GlobalScale = { 1f, 1f };
    public bool HasSpritePoints;
    public int SpritePoints = 4;
    public bool HasBlendOp;
    public int BlendOp;

    public bool HasTexture;

    public bool HasMesh;
    public string MeshPath = string.Empty;

    public bool HasRandScale;
    public float RsFreq;
    public float RsMin = 1f;
    public float RsRange;

    public bool HasOrient;
    public float OrSpeed;
    public float OrSpread;
    public bool OrRandAngle;
    public float[] OrAxis = { 1f, 0f, 0f };
    public bool OrRandAxis = true;

    /// <summary>
    /// A mesh particle's REST ORIENTATION, the quaternion (x, y, z, w) its mesh is turned by in the
    /// emitter's frame before anything else — what lays a blade along its orbit and tilts it.
    /// Read from the system's FIRST visual, at the particle's birth, as the orient fields are.
    /// <para>The guest's contract (TU1; Fable III's `sub_83177C70` is the same): a mesh particle's
    /// visual is born in `sub_83220AC8`, which stores the orientation its caller hands it in r6:r7
    /// at particle+0x30 (0x83220dc0/0x83220dc4) — the emitter's spawn rotation
    /// (`sub_8223B9A0(q, unk_8349E5F0)` at 0x82a0b148, passed at 0x82a0b390) — and the initial
    /// angle about <see cref="OrAxis"/> at particle+0x40 (`sub_8223A070` at 0x83220d80). Each step
    /// `sub_832213E8` turns +0x30 by the axis-angle of the rate (0x832215bc), on the LEFT
    /// (`sub_822ADF78`: dst = arg ⊗ dst), so the spin is about <see cref="OrAxis"/> in the frame the
    /// particles are stored in; the draw `sub_821CEA80` sends (+0x30) ⊗ (+0x40) (0x821cee48..0x821ceebc).
    /// So drawn = spin(axis, Σrate) ⊗ emitterRotation ⊗ MeshAlign ⊗ angle(axis, initial).</para>
    /// <para>The bank's mesh visual has no alignment: `sub_83220AC8` reads only the segment's rate
    /// (+24), spread (+28), random-angle flag (+32), axis (+36..+44), random-axis flag (+48) and
    /// scale (+60, +64), so the guest reads it from the dress table keyed by the SYSTEM's id
    /// (`sub_83220AC8`'s r8 is the system; system+4 its id) and composes it onto r6:r7 before the
    /// visual is born (Fable2/src/vfs/src/hooks/particle_motion.cpp). Absent on every shipped visual, which the guest births at
    /// identity — and so does the modern draw: every mesh particle takes this composition.</para>
    /// </summary>
    public bool HasMeshAlign;

    public float[] MeshAlign = { 0f, 0f, 0f, 1f };

    public int DelayFrames;
    public float InvSpan;
    public float[] ColorSq = { 1f, 1f, 1f };
    public bool SaturationMask;
    public float SoftDepthContrast;
    public int FlickerFrames;
    public bool HasBlend;
    public bool HasTrailWidth;
    public float TrailWidth = 1f;
    public bool HasTrailLen;
    public int TrailLen = 1;
}

public sealed class FxSystemDef
{
    public uint Id;
    public uint Mode = 1;
    public List<FxVisual> Visuals { get; } = new();
    public float LoopStartFrames;
    public float LoopEndFrames;
    public int LoopCount;
    public int LastStartFrames;
    public int ChildCount;
}

/// <param name="Group">
/// The type-vector's <c>EntityPriorities</c> — an update-order BUCKET, not an identity. Its
/// values are 1..5 and they repeat across vectors, so it can NEVER be used to tell one
/// type-vector from the next: segmenting on it merges every neighbouring pair that happens to
/// share a priority.
/// </param>
/// <param name="Vector">
/// Which type-vector this part came from, counted per effect. THIS is the chain boundary — the
/// guest instantiates one chain per type-vector and resets its parent link at each boundary, so
/// the parenting is only correct if the boundary is.
/// </param>
public readonly record struct FxEffectPart(int Emitter, int System, uint Group, int Vector, int TrailSystem = -1);

public sealed class FxEffect
{
    public uint NameHash;
    public string Name = string.Empty;
    public bool ZSortEmitters;
    public uint UpdateFrequency;
    public List<FxEffectPart> Parts { get; } = new();

    /// <summary>
    /// A MOD's authored size multiplier (fx/effects/*.json <c>scale</c>), applied exactly where a
    /// node's <see cref="FableFx.EffectScale"/> applies: every position, motion and size of the
    /// instance. 1 for every shipped effect — the bank states no such field.
    /// </summary>
    public float Scale = 1f;

    /// <summary>
    /// A MOD's authored colour multiplier (fx/effects/*.json <c>tint</c>), applied where a node's
    /// <see cref="FableFx.Tint"/> applies: each particle's colour after lighting. White for every
    /// shipped effect.
    /// </summary>
    public float[] Tint = { 1f, 1f, 1f };

    /// <summary>
    /// AUTHORED PIECES the effect draws beside its parts (fx/effects/*.json <c>pieces</c>; a recipe's
    /// <c>pieces</c> become such an effect): embers, arcs, ring, blades, built by
    /// <see cref="Engine_FxRecipePieces.Build"/> under the <see cref="FableFx"/> drawing the effect.
    /// Empty for every shipped effect. The guest's effect record has no such kind, so the guest's
    /// bank never carries them (<see cref="Engine_FxModEffects.ComposeGuestBank"/>); an effect that
    /// must also play under the classic look states the same look with the bank's own entities
    /// (<see cref="Engine_FxRecipePieces"/>'s remarks map each kind to them).
    /// </summary>
    public List<Engine_FxRecipePieces.Piece> Pieces { get; } = new();
}

public sealed class FxBank
{
    public bool Ok;
    public string Error = string.Empty;
    public uint Version;

    public List<FxEmitter> Emitters { get; } = new();
    public List<FxSystemDef> Systems { get; } = new();
    public List<FxEffect> Effects { get; } = new();

    private Dictionary<uint, FxEffect>? _byHash;

    public FxEffect? FindByHash(uint hash)
    {
        _byHash ??= Build();
        return _byHash.TryGetValue(hash, out FxEffect? effect) ? effect : null;
    }

    public FxEffect? FindByName(string name) => FindByHash(Fnv1Lower(name));

    /// <summary>Drops the lookups built over <see cref="Effects"/> and <see cref="Emitters"/>; an overlay that replaced or added entries calls it.</summary>
    public void Reindex()
    {
        _byHash = null;
        _byEntity = null;
    }

    /// <summary>
    /// FNV-1 of <c>"$$reserved_root_name$$"</c> — the placeholder type the guest gives the
    /// SYNTHETIC ROOT it creates per type-vector (`sub_83232B60`). It belongs to no effect and
    /// appears in no file, so a live instance carrying it is structure, not content.
    /// </summary>
    public const uint ReservedRootType = 0x7B825CFAu;

    /// <summary>
    /// Which effect a live instance's TYPE belongs to.
    ///
    /// THE TWO HASHES ARE DIFFERENT KEY SPACES, and conflating them is why the live feed drew
    /// nothing on its first working run. A live instance names its type through
    /// <c>*(type+4)</c>, which is that type's key in the type registry at <c>dword_8349E688</c>;
    /// the particle bank is keyed by the EFFECT name, which is the key of a different registry at
    /// <c>dword_8349E568</c>. Zero of 40 observed hashes matched the bank, which is exactly what
    /// the binary predicts — not a bug in the walk.
    ///
    /// The join needs no guest memory at all, because it is already in the file: every triple in
    /// a section-5 record leads with that same type ID, and the record's own key is the effect
    /// hash the bank uses. So the bank can answer "which effect owns this type" directly.
    ///
    /// Measured on the shipped bank: 2750 distinct type IDs, of which 12 (0.44%) are claimed by
    /// more than one effect. Those are genuinely ambiguous and the first claim wins; the count is
    /// kept so the ambiguity is visible rather than silently resolved.
    /// </summary>
    public uint EffectForType(uint typeId) =>
        TypeToEffect.TryGetValue(typeId, out uint effect) ? effect : 0u;

    public Dictionary<uint, uint> TypeToEffect { get; } = new();

    private Dictionary<uint, FxEmitter>? _byEntity;

    /// <summary>The emitter a live instance's TYPE names (<c>*(type+4)</c> = the emitter's
    /// EntityId), or null — the way to know whether a node is a standard emitter before
    /// reading emitter-only slots off it.</summary>
    public FxEmitter? EmitterForType(uint typeId)
    {
        if (_byEntity is null)
        {
            _byEntity = new Dictionary<uint, FxEmitter>();
            foreach (FxEmitter emitter in Emitters)
            {
                _byEntity.TryAdd(emitter.EntityId, emitter);
            }
        }

        return _byEntity.TryGetValue(typeId, out FxEmitter? found) ? found : null;
    }

    /// <summary>How many type IDs more than one effect laid claim to.</summary>
    public int AmbiguousTypes { get; internal set; }

    /// <summary>
    /// How many <c>CPointLightEntityType</c>s this effect carries — the lights a torch casts as
    /// opposed to the flame it draws.
    ///
    /// Exists so the level spawn can REPORT the number. A light that fails to appear and a light
    /// the data never asked for look identical on screen, and 71 of the bank's 868 effects carry
    /// one, so "no glow here" is only evidence once you know whether this effect had one.
    /// </summary>
    public int LightCount(uint effectHash) => Composition(effectHash).Lights;

    /// <summary>
    /// What KINDS of part an effect is made of, counted by entity type.
    ///
    /// This exists to aim the remaining work. The bank-wide counts say what is in the FILE — 264
    /// spline entities, 77 orbitals, 72 lights, 34 attractors — but what matters is how many of
    /// them are in the effects a LEVEL actually places, and the two numbers turn out to be
    /// nothing alike: 71 of 868 effects carry a light, yet across all 83 shipped levels only 2
    /// spawned effects do. Implementing by file-census order would spend the effort in the wrong
    /// place.
    /// </summary>
    /// <param name="Lights">Type 7, <c>CPointLightEntityType</c> — a real dynamic light.</param>
    /// <param name="Attractors">Type 4 — pulls particles toward a point, linear falloff.</param>
    /// <param name="Splines">Type 5 — a point that travels a path; things parented to it ride along.</param>
    /// <param name="Orbitals">Type 3, <c>COrbitalEntityType</c> — orbits its parent.</param>
    public readonly record struct FxComposition(
        int Lights, int Attractors, int Splines, int Orbitals);

    public FxComposition Composition(uint effectHash)
    {
        FxEffect? effect = FindByHash(effectHash);
        if (effect is null)
        {
            return default;
        }

        int lights = 0;
        int attractors = 0;
        int splines = 0;
        int orbitals = 0;
        foreach (FxEffectPart part in effect.Parts)
        {
            if (part.Emitter < 0 || part.Emitter >= Emitters.Count)
            {
                continue;
            }

            switch (Emitters[part.Emitter].TypeId)
            {
                case 3: orbitals++; break;
                case 4: attractors++; break;
                case 5: splines++; break;
                case 7: lights++; break;
            }
        }

        return new FxComposition(lights, attractors, splines, orbitals);
    }

    private Dictionary<uint, FxEffect> Build()
    {
        var map = new Dictionary<uint, FxEffect>();
        foreach (FxEffect effect in Effects)
        {
            map[effect.NameHash] = effect;
        }

        return map;
    }

    public Xex.XexBank? ExactModel { get; internal set; }

    public static uint Fnv1Lower(string s) => Xex.XexBankParser.Fnv1LowerResourceName(s);

    /// <summary>An <c>FX_XXXXXXXX</c> token: an effect named by its hash, for the effects whose name no shipped file states.</summary>
    public static bool TryParseHashToken(string token, out uint hash)
    {
        hash = 0;
        return token.Length == 11 &&
            token.StartsWith("FX_", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(
                token.AsSpan(3),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out hash);
    }
}

public static class Engine_FxBank
{
    public static bool Parse(byte[] bytes, FxBank outBank)
    {
        var x = new Xex.XexBank();
        if (!Xex.XexBankParser.ParseBank(bytes, x))
        {
            outBank.Ok = false;
            outBank.Error = x.Error.Length == 0 ? "particle bank parse failed" : x.Error;
            return false;
        }

        outBank.ExactModel = x;
        outBank.Version = x.BankVersion;
        outBank.Emitters.Clear();
        outBank.Systems.Clear();
        outBank.Effects.Clear();
        outBank.TypeToEffect.Clear();
        outBank.AmbiguousTypes = 0;

        var eidToIdx = new Dictionary<uint, int>();
        foreach (Xex.XexEntityRef reference in x.EntityOrder)
        {
            var e = new FxEmitter();
            bool known = true;
            switch (reference.TypeId)
            {
                case 0:
                {
                    Xex.XexStandardEmitterType se = x.StdEmitters[reference.Index];
                    FillEmitterCommon(se, e);
                    e.SpawnRadius = se.Shape is Xex.XexEmitterShape.Sphere or Xex.XexEmitterShape.Disc ? se.ShapeRadius : 0f;
                    e.ShapeFlagA = se.ShapeFlagA;
                    e.ShapeFlagB = se.ShapeFlagB;
                    e.EmitOnSurface = se.ShapeFlagA;
                    e.UseShapeSpawnDir = se.ShapeFlagB;
                    e.ReleaseTrail = se.ReleaseTrail;
                    e.GlobalCollider = se.GlobalCollider;
                    e.CollisionRestitution = se.CollisionRestitution;
                    break;
                }

                case 9:
                {
                    Xex.XexSplineEmitterType se = x.SplineEmitters[reference.Index];
                    FillEmitterCommon(se, e);
                    e.SplineRateEmitter = se.Spline.Rate;
                    e.SplineRate = se.Spline.Rate;
                    e.SplineReverseFlow = se.Spline.ReverseFlow;
                    e.SplineLooped = se.Spline.LoopedSpline;
                    e.SplineRandomStart = se.Spline.UseRandomStartPoint;
                    e.SplineStraight = se.Spline.TreatAsStraightSegments;
                    e.SplineRandomGenerator = se.Spline.RandomGenerator;
                    e.AnimationMode = se.AnimationMode;
                    e.NumSplineSegments = se.NumSplineSegments;
                    e.RandomiseAnimMagnitude = se.RandomiseAnimMagnitude;
                    e.SmoothAnimationSpeed = se.SmoothAnimationSpeed;
                    e.JerkyAnimationRate = se.JerkyAnimationRate;
                    e.RandomiseAnimMagnitudeFlag = se.RandomiseAnimMagnitudeFlag;
                    e.RandomiseAnimationRates = se.RandomiseAnimationRates;
                    break;
                }

                case 3:
                {
                    Xex.XexOrbitalEntityType o = x.Orbitals[reference.Index];
                    FillEntityCommon(o, e);
                    e.PositionalInteractable = o.PositionalInteractable;
                    e.AngularRotationRate = o.AngularRotationRate;
                    break;
                }

                case 4:
                {
                    Xex.XexAttractorEntityType a = x.Attractors[reference.Index];
                    FillEntityCommon(a, e);
                    e.PositionalInteractable = a.PositionalInteractable;
                    e.GlobalAttractor = a.GlobalParticleAttractor;
                    break;
                }

                case 5:
                {
                    Xex.XexSplineEntityType sp = x.SplineEntities[reference.Index];
                    FillEntityCommon(sp, e);
                    e.PositionalInteractable = sp.PositionalInteractable;
                    e.SplinePoints = sp.Spline.Points.ToArray();
                    e.SplineRate = sp.Spline.Rate;
                    e.SplineReverseFlow = sp.Spline.ReverseFlow;
                    e.SplineLooped = sp.Spline.LoopedSpline;
                    e.SplineRandomStart = sp.Spline.UseRandomStartPoint;
                    e.SplineStraight = sp.Spline.TreatAsStraightSegments;
                    e.SplineRandomGenerator = sp.Spline.RandomGenerator;
                    e.SplineScaleEmissionByLength = sp.Spline.ScaleEmissionRatesBySplineLength;
                    break;
                }

                case 6: FillEntityCommon(x.Roots[reference.Index], e); break;
                case 7:
                {
                    Xex.XexPointLightEntityType l = x.PointLights[reference.Index];
                    FillEntityCommon(l, e);
                    e.PositionalInteractable = l.PositionalInteractable;
                    e.LightColour = ToVecTimeline(l.Vec[3], 3);
                    e.HaloTextureHash = l.HaloTextureHash;
                    break;
                }

                case 8:
                {
                    Xex.XexSpotLightEntityType l = x.SpotLights[reference.Index];
                    FillEntityCommon(l, e);
                    e.PositionalInteractable = l.PositionalInteractable;
                    e.LightColour = ToVecTimeline(l.Vec[3], 3);
                    e.HaloTextureHash = l.HaloTextureHash;
                    break;
                }

                default: known = false; break;
            }

            if (known)
            {
                LinkParameters(e);
            }

            eidToIdx[reference.EntityId] = outBank.Emitters.Count;
            outBank.Emitters.Add(e);
        }

        var sysToIdx = new Dictionary<uint, int>();
        foreach (Xex.XexSystemDef xs in x.Systems)
        {
            var sys = new FxSystemDef
            {
                Id = xs.Id,
                Mode = xs.MaterialType,
                LoopStartFrames = xs.LoopStartFrames,
                LoopEndFrames = xs.LoopEndFrames,
                LoopCount = (int)xs.LoopCount,
                ChildCount = xs.Children.Count,
            };
            foreach (Xex.XexSystemChild c in xs.Children)
            {
                sys.LastStartFrames = Math.Max(sys.LastStartFrames, c.StartFrames);
            }

            for (int i = 0; i < xs.Segments.Count; i++)
            {
                FxVisual v = VisualFromSegment(xs, i);
                if (v.HasTexture || v.HasMesh)
                {
                    sys.Visuals.Add(v);
                }
            }

            sysToIdx[xs.Id] = outBank.Systems.Count;
            outBank.Systems.Add(sys);
        }

        foreach (Xex.XexEffectCollection xe in x.Effects)
        {
            var eff = new FxEffect { NameHash = xe.Id, ZSortEmitters = xe.ZSortEmitters, UpdateFrequency = xe.UpdateFrequency };
            int chain = 0;
            foreach (Xex.XexEffectTypeVector tv in xe.TypeVectors)
            {
                foreach (Xex.XexEffectItem it in tv.Items)
                {
                    int emitter = eidToIdx.TryGetValue(it.EntityTypeId, out int ei) ? ei : -1;
                    int system = sysToIdx.TryGetValue(it.MaterialTimelineId, out int si) ? si : -1;
                    int trail = sysToIdx.TryGetValue(it.TrailMaterialTimelineId, out int ti) ? ti : -1;
                    if (emitter >= 0)
                    {
                        eff.Parts.Add(new FxEffectPart(emitter, system, tv.Priority, chain, trail));
                    }

                    if (!outBank.TypeToEffect.TryAdd(it.EntityTypeId, xe.Id) &&
                        outBank.TypeToEffect[it.EntityTypeId] != xe.Id)
                    {
                        outBank.AmbiguousTypes++;
                    }
                }

                chain++;
            }

            outBank.Effects.Add(eff);
        }

        outBank.Ok = outBank.Emitters.Count != 0;
        outBank.Error = outBank.Ok ? string.Empty : "no emitters parsed";
        return outBank.Ok;
    }

    private static FxTimeline ToTimeline(Xex.XexScalarTimeline t, int idx)
    {
        var o = new FxTimeline { DefaultValue = Xex.XexConst.ScalarParamDefaults[idx], Engine = t };
        if (t.Present)
        {
            foreach (Xex.XexScalarKey k in t.Keys)
            {
                o.Keys.Add((k.TimeSecs, k.Value));
            }
        }

        return o;
    }

    private static FxVectorTimeline ToVecTimeline(Xex.XexVecTimeline t, int idx)
    {
        var o = new FxVectorTimeline
        {
            DefaultValue = new[] { Xex.XexConst.VecParamDefaults[idx][0], Xex.XexConst.VecParamDefaults[idx][1], Xex.XexConst.VecParamDefaults[idx][2] },
            Engine = t,
        };
        if (t.Present)
        {
            foreach (Xex.XexVecKey k in t.Keys)
            {
                o.Keys.Add((k.TimeSecs, k.X, k.Y, k.Z));
            }
        }

        return o;
    }

    private static void FillEntityCommon(Xex.XexEntityType x, FxEmitter e)
    {
        e.TypeId = x.TypeId;
        e.EntityId = x.EntityId;
        e.SubType = x.SubType;
        e.Pos = new[] { x.Pos[0], x.Pos[1], x.Pos[2] };
        e.Quat = new[] { x.Quat[0], x.Quat[1], x.Quat[2], x.Quat[3] };
        e.UseLife = x.UseLife;
        e.LifetimeFrames = x.LifetimeFrames;
        e.ProcessRadius = x.ProcessRadius;
        for (int i = 0; i < 28; i++)
        {
            e.Generic[i] = ToTimeline(x.Scalar[i], i);
        }

        e.Vec0 = ToVecTimeline(x.Vec[0], 0);
        e.Vec1 = ToVecTimeline(x.Vec[1], 1);
        e.Vec2 = ToVecTimeline(x.Vec[2], 2);
    }

    private static void FillEmitterCommon(Xex.XexEmitterType x, FxEmitter e)
    {
        FillEntityCommon(x, e);
        e.PositionalInteractable = x.PositionalInteractable;
        e.MaxActive = x.MaxActiveParticles;
        e.InitialNum = x.InitialNumParticles;
        e.Fades = new[] { x.StartFadeIn, x.EndFadeIn, x.StartFadeOut, x.EndFadeOut };
        e.SortParticles = x.SortParticles;
        e.ForceToFullSizeRenderBuffer = x.ForceFullSizeRenderBuffer;
        e.AttachToRoot = x.AttachToRoot;
    }

    /// <summary>
    /// What an entity's TYPE states about it: its kind flags, and the named views over its
    /// generic parameters (<see cref="FxEmitter.Rate"/> IS <c>Generic[2]</c>, the same object).
    /// The one place they are derived, for an entity read from the bank and one authored by a
    /// mod (<see cref="Engine_FxEffectJson"/>). Type 0 standard and 9 spline emitters move and
    /// emit; 4 attracts; 5 is a spline; 7 point and 8 spot are lights, and only 8 has a cone.
    /// </summary>
    public static void LinkParameters(FxEmitter e)
    {
        bool emits = e.TypeId is 0 or 9;
        e.HasMotion = emits;
        e.IsAttractor = e.TypeId == 4;
        e.IsSpline = e.TypeId == 5;
        e.IsLight = e.TypeId is 7 or 8;
        if (emits)
        {
            e.Rate = e.Generic[2];
            e.RateSpread = e.Generic[3];
            e.Speed = e.Generic[4];
            e.SpeedSpread = e.Generic[5];
            e.AccelDirScale = e.Generic[6];
            e.GravityScale = e.Generic[7];
            e.GlobalWindScale = e.Generic[8];
            e.VelX = e.Generic[9];
            e.VelY = e.Generic[10];
            e.VelZ = e.Generic[11];
            e.Wind = e.Generic[12];
            e.P13 = e.Generic[13];
            e.P14 = e.Generic[14];
        }

        if (e.IsAttractor)
        {
            e.AttractRadius = e.Generic[26];
            e.AttractStrength = e.Generic[27];
        }

        if (e.IsLight)
        {
            e.LightRange = e.Generic[15];
            e.LightIntensity = e.Generic[16];
            e.LightCutoff = e.Generic[17];
            e.LightFadeRamp = e.Generic[18];
            if (e.TypeId == 8)
            {
                e.LightConeAngle = e.Generic[24];
            }
        }
    }

    /// <summary>Generic parameter <paramref name="index"/> from authored keys (time in seconds, value), evaluated as the bank's own.</summary>
    public static FxTimeline MakeTimeline(int index, IReadOnlyList<(float Time, float Value)> keys)
    {
        var engine = new Xex.XexScalarTimeline();
        var o = new FxTimeline { DefaultValue = Xex.XexConst.ScalarParamDefaults[index], Engine = engine };
        foreach ((float time, float value) in keys)
        {
            engine.Keys.Add(new Xex.XexScalarKey { TimeSecs = time, Value = value });
            o.Keys.Add((time, value));
        }

        Xex.XexBankParser.BakeScalar(engine, Xex.XexConst.ScalarParamDefaults[index]);
        if (!engine.Present)
        {
            o.Keys.Clear();  // as ToTimeline: a timeline that never starts is its default
        }

        return o;
    }

    /// <summary>Vector parameter <paramref name="index"/> (0..3) from authored keys, evaluated as the bank's own.</summary>
    public static FxVectorTimeline MakeVecTimeline(int index, IReadOnlyList<(float Time, float X, float Y, float Z)> keys)
    {
        float[] defaults = Xex.XexConst.VecParamDefaults[index];
        var engine = new Xex.XexVecTimeline();
        var o = new FxVectorTimeline { DefaultValue = new[] { defaults[0], defaults[1], defaults[2] }, Engine = engine };
        foreach ((float time, float x, float y, float z) in keys)
        {
            engine.Keys.Add(new Xex.XexVecKey { TimeSecs = time, X = x, Y = y, Z = z });
            o.Keys.Add((time, x, y, z));
        }

        Xex.XexBankParser.BakeVec(engine, defaults);
        if (!engine.Present)
        {
            o.Keys.Clear();
        }

        return o;
    }

    private static FxVisual VisualFromSegment(Xex.XexSystemDef sys, int i)
    {
        Xex.XexSegmentRecord r = sys.Segments[i];
        Xex.XexSystemChild child = sys.Children[sys.ChildOrder[i]];
        Xex.XexMaterial m = child.Material;
        var v = new FxVisual
        {
            Delay = child.StartSecs,
            DelayFrames = child.StartFrames,
            InvSpan = r.InvSpan,
            HasTexture = r.TexPath.Length != 0,
            Texture = r.TexPath,
            RandomFrame = r.RandomInitialFrame != 0,
            FrameSpeed = r.FrameChangeSpeed,
            Displacement = r.IsDispTex != 0,
            SaturationMask = r.SatMask != 0,
            SoftDepthContrast = r.SoftDepth,
        };

        if (v.HasTexture)
        {
            Xex.XexTexEntry? te = null;
            foreach (Xex.XexTexEntry t in sys.TexList)
            {
                if (t.TexHash == r.TexHash)
                {
                    te = t;
                }
            }

            if (te is not null)
            {
                v.TexCols = te.NumXDiv >= 1 ? te.NumXDiv : 1;
                v.TexRows = te.NumYDiv >= 1 ? te.NumYDiv : 1;
                if (sys.MaterialType == 1 && v.FrameSpeed == 0f)
                {
                    v.FrameSpeed = te.FrameChangeSpeed;
                }
            }

            if (m.Has(Xex.XexTag.DiffuseTexture))
            {
                v.TexCols = m.NumXDiv >= 1 ? (int)m.NumXDiv : 1;
                v.TexRows = m.NumYDiv >= 1 ? (int)m.NumYDiv : 1;
            }
        }

        v.HasColor = m.Has(Xex.XexTag.DiffuseColor) || i > 0;
        v.ColorSq = new[] { r.Rgb[0], r.Rgb[1], r.Rgb[2] };
        v.Color = new[]
        {
            MathF.Sqrt(MathF.Max(0f, r.Rgb[0])), MathF.Sqrt(MathF.Max(0f, r.Rgb[1])), MathF.Sqrt(MathF.Max(0f, r.Rgb[2])),
        };
        if (m.Has(Xex.XexTag.DiffuseColor))
        {
            v.Color = new[] { m.ColorFile[0], m.ColorFile[1], m.ColorFile[2] };
        }

        v.HasAlpha = true;
        v.Alpha = r.Alpha;
        v.SrcBlend = (int)r.SrcBlend;
        v.DstBlend = (int)r.DstBlend;
        v.BlendOp = (int)r.BlendOp;
        v.HasBlendOp = m.Has(Xex.XexTag.BlendOp);
        v.HasBlend = m.Has(Xex.XexTag.SrcBlendMode) || m.Has(Xex.XexTag.DestBlendMode) || m.Has(Xex.XexTag.BlendOp);
        v.HasSelfIllum = true;
        v.SelfIllum = r.SelfIllum;

        if (sys.MaterialType == 1)
        {
            v.HasSize = true;
            v.SizeX = r.Dims[0];
            v.SizeY = r.Dims[1];
            v.HasGlobalScale = true;
            v.GlobalScale = new[] { r.GlobalFrameScale[0], r.GlobalFrameScale[1] };
            v.HasSpritePoints = true;
            v.SpritePoints = (int)r.NumSpritePoints;
            v.HasRotation = r.RotSpeed != 0f || r.RotSpread != 0f || r.RotInitRandom != 0;
            v.RotSpeed = r.RotSpeed;
            v.RotSpread = r.RotSpread;
            v.RotInitialRandom = r.RotInitRandom != 0;
            v.HasAlignQuat = r.HasAlignQuat != 0;
            v.AlignQuat = new[] { r.AlignQuat[0], r.AlignQuat[1], r.AlignQuat[2], r.AlignQuat[3] };
            v.HasFlicker = r.RsFreqFrames != 0 || r.RsRange[0] != 0f || r.RsRange[1] != 0f || r.RsMin[0] != 1f || r.RsMin[1] != 1f;
            v.FlickerFrames = r.RsFreqFrames;
            v.FlickerPeriod = r.RsFreqFrames / Xex.XexConst.Fps;
            v.FlickerBaseX = r.RsMin[0];
            v.FlickerBaseY = r.RsMin[1];
            v.FlickerSpreadX = r.RsRange[0];
            v.FlickerSpreadY = r.RsRange[1];
            v.FlickerShared = r.RsLinked != 0;
        }
        else if (sys.MaterialType is 2 or 3)
        {
            v.HasTrailWidth = true;
            v.TrailWidth = r.Size;
            v.HasScalarSize = true;
            v.ScalarSize = r.Size;
            v.HasTrailLen = true;
            v.TrailLen = r.NumSegments >= 1 ? r.NumSegments : 1;
            v.HasNumSegments = true;
            v.NumSegments = v.TrailLen;
        }
        else if (sys.MaterialType == 4)
        {
            v.HasMesh = r.MeshPath.Length != 0;
            v.MeshPath = r.MeshPath;
            if (r.IsDispMesh != 0) v.Displacement = true;
            v.HasTrailWidth = true;
            v.TrailWidth = r.MeshScale;
            v.HasScalarSize = true;
            v.ScalarSize = r.MeshScale;
            v.HasRandScale = r.RsFreqFrames != 0 || r.MrsRange != 0f || r.MrsMin != 1f;
            v.RsFreq = r.RsFreqFrames / Xex.XexConst.Fps;
            v.RsMin = r.MrsMin;
            v.RsRange = r.MrsRange;
            v.HasOrient = true;
            v.OrSpeed = r.RotSpeed;
            v.OrSpread = r.RotSpread;
            v.OrRandAngle = r.RotInitRandom != 0;
            v.OrAxis = new[] { r.OrAxis[0], r.OrAxis[1], r.OrAxis[2] };
            v.OrRandAxis = r.OrInitRandomAxis != 0;
        }

        return v;
    }
}
