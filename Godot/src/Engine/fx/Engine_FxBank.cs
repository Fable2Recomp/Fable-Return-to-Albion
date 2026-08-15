using System;
using System.Collections.Generic;

namespace Refii.Fx;

public sealed class FxTimeline
{
    public List<(float Time, float Value)> Keys { get; } = new();

    public float DefaultValue { get; set; }

    public bool IsEmpty => Keys.Count == 0;

    public float Eval(float t)
    {
        if (Keys.Count == 0)
        {
            return DefaultValue;
        }

        if (t <= Keys[0].Time)
        {
            return Keys[0].Value;
        }

        if (t >= Keys[^1].Time)
        {
            return Keys[^1].Value;
        }

        for (int i = 1; i < Keys.Count; i++)
        {
            if (t <= Keys[i].Time)
            {
                (float ta, float va) = Keys[i - 1];
                (float tb, float vb) = Keys[i];
                float span = tb - ta;
                float u = span > 1e-6f ? (t - ta) / span : 0f;
                return va + ((vb - va) * u);
            }
        }

        return Keys[^1].Value;
    }

    public float MaxValue()
    {
        float m = DefaultValue;
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

/// <summary>
/// A timeline whose value is a vector — four floats per key, stored <c>(time, x, y, z)</c>.
///
/// THE FIRST LANE IS THE TIME, which the shipped data settles rather than the format doing so:
/// across all 72 light entities it is 0.0 in every key and non-decreasing within every timeline,
/// while the other three read unmistakably as colours — (1.0, 0.8, 0.3) for a torch, (0.5, 0.6,
/// 1.0) for a cold blue. Read as four raw components instead, every light in the game would take
/// its red from a time.
/// </summary>
public sealed class FxVectorTimeline
{
    public List<(float Time, float X, float Y, float Z)> Keys { get; } = new();

    public bool IsEmpty => Keys.Count == 0;

    /// <summary>
    /// The value at <paramref name="t"/>, holding the first and last key beyond the ends — the
    /// same clamped-hold the scalar timelines use.
    /// </summary>
    public (float X, float Y, float Z) Eval(float t)
    {
        if (Keys.Count == 0)
        {
            return (0f, 0f, 0f);
        }

        if (t <= Keys[0].Time)
        {
            return (Keys[0].X, Keys[0].Y, Keys[0].Z);
        }

        if (t >= Keys[^1].Time)
        {
            return (Keys[^1].X, Keys[^1].Y, Keys[^1].Z);
        }

        for (int i = 1; i < Keys.Count; i++)
        {
            if (t > Keys[i].Time)
            {
                continue;
            }

            (float ta, float ax, float ay, float az) = Keys[i - 1];
            (float tb, float bx, float by, float bz) = Keys[i];
            float span = tb - ta;
            float u = span > 1e-6f ? (t - ta) / span : 0f;
            return (ax + ((bx - ax) * u), ay + ((by - ay) * u), az + ((bz - az) * u));
        }

        return (Keys[^1].X, Keys[^1].Y, Keys[^1].Z);
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
    /// Generic parameter 15 — the light's RANGE, the first of three floats handed to the
    /// descriptor builder (`sub_8323FA50` at 0x8323faa8, passed as arg 1 at 0x8323fc14).
    ///
    /// Named by matching that argument order against the placed-light path, which this engine
    /// already decoded: <c>sub_82A49D08</c> takes Range, FadeDistance and FadeRange in that
    /// order. The shipped values agree — 1.2 to 40 with a median of 5, which is the radius a
    /// torch or a brazier wants.
    /// </summary>
    public FxTimeline LightRange = new();

    /// <summary>
    /// Generic parameter 16 — the intensity the colour is scaled by. `sub_8323FA50` multiplies
    /// the colour vector by (this x the emitter's alive fraction) at 0x8323fb28, so a light fades
    /// out as its emitter runs down rather than snapping off.
    /// </summary>
    public FxTimeline LightIntensity = new();

    /// <summary>Generic parameter 17 — where the light stops being drawn, a CAMERA distance.</summary>
    public FxTimeline LightFadeDistance = new();

    /// <summary>Generic parameter 18 — the length of that fade.</summary>
    public FxTimeline LightFadeRange = new();

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
    public float[] OrAxis = { 0f, 0f, 1f };
    public bool OrRandAxis = true;
}

public sealed class FxSystemDef
{
    public uint Id;
    public uint Mode = 1;
    public List<FxVisual> Visuals { get; } = new();
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
public readonly record struct FxEffectPart(int Emitter, int System, uint Group, int Vector);

public sealed class FxEffect
{
    public uint NameHash;
    public string Name = string.Empty;
    public List<FxEffectPart> Parts { get; } = new();
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

    public static uint Fnv1Lower(string s)
    {
        uint h = 0x811C9DC5u;
        foreach (char c in s)
        {
            h *= 0x01000193u;
            h ^= (byte)char.ToLowerInvariant(c);
        }

        return h;
    }
}

public static class Engine_FxBank
{
    private const uint MarkVer = 0x4AD78C59u;
    private const uint Baad = 0xBAADF00Du;
    private const uint ChildMark = 0x37E185B9u;
    private const int Sec4Fixed = 0x1C917E;

    private static readonly float[] GenericDefaults =
    {
        0.95f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 1.0f, 15.0f,
        1.0f, 64.0f, 50.0f, 0.05f, 0.05f, 0.1f, 0.2f, 0.1f,
        0.785398185f, 0.0f, 0.0f, 0.0f,
    };

    private sealed class Reader
    {
        public readonly byte[] Data;
        public int O;
        public bool Bad;

        public Reader(byte[] data) => Data = data;

        public bool Have(int k) => O + k <= Data.Length;

        public uint U32()
        {
            if (!Have(4))
            {
                Bad = true;
                return 0;
            }

            uint v = ((uint)Data[O] << 24) | ((uint)Data[O + 1] << 16) |
                     ((uint)Data[O + 2] << 8) | Data[O + 3];
            O += 4;
            return v;
        }

        public float F32() => BitConverter.Int32BitsToSingle((int)U32());

        public byte U8()
        {
            if (!Have(1))
            {
                Bad = true;
                return 0;
            }

            return Data[O++];
        }

        public void Skip(int k)
        {
            O += k;
            if (O > Data.Length)
            {
                O = Data.Length;
                Bad = true;
            }
        }

        public uint Peek(int at)
        {
            if (at + 4 > Data.Length)
            {
                return 0;
            }

            return ((uint)Data[at] << 24) | ((uint)Data[at + 1] << 16) |
                   ((uint)Data[at + 2] << 8) | Data[at + 3];
        }
    }

    private static void OptBaad(Reader r)
    {
        if (r.Peek(r.O) == Baad)
        {
            r.Skip(4);
            r.U32();
        }
    }

    private static float GenericDefault(int idx) =>
        idx >= 0 && idx < GenericDefaults.Length ? GenericDefaults[idx] : 0f;

    private static FxTimeline ReadStl(Reader r, int idx)
    {
        var t = new FxTimeline { DefaultValue = GenericDefault(idx) };
        if (r.Peek(r.O) == MarkVer)
        {
            r.Skip(4);
            r.U32();
        }

        uint cnt = r.U32();
        if (cnt > 100000)
        {
            r.Bad = true;
            return t;
        }

        for (uint i = 0; i < cnt && !r.Bad; i++)
        {
            float time = r.F32();
            float value = r.F32();
            t.Keys.Add((time, value));
        }

        return t;
    }

    private static void SkipStl(Reader r, int idx) => ReadStl(r, idx);

    private static void SkipVtl(Reader r) => ReadVtl(r);

    /// <summary>
    /// A VECTOR timeline — the same shape as the scalar one but four floats per key.
    ///
    /// Only the light entities need their vector timeline read rather than stepped over, and they
    /// need it badly: vector parameter 3 is the light's COLOUR and its default is (0,0,0,0), so a
    /// light whose timeline we skip is a light that emits black.
    /// </summary>
    private static FxVectorTimeline ReadVtl(Reader r)
    {
        var t = new FxVectorTimeline();
        if (r.Peek(r.O) == MarkVer)
        {
            r.Skip(4);
            r.U32();
        }

        uint cnt = r.U32();
        if (cnt > 100000)
        {
            r.Bad = true;
            return t;
        }

        for (uint i = 0; i < cnt && !r.Bad; i++)
        {
            // The key's time shares the first lane with the value's X in the scalar form; here the
            // record is four floats and the walk only needs them kept in order.
            float x = r.F32();
            float y = r.F32();
            float z = r.F32();
            float w = r.F32();
            t.Keys.Add((x, y, z, w));
        }

        return t;
    }

    private static void ReadBase(Reader r, FxEmitter e)
    {
        r.U32();
        e.Pos = new[] { r.F32(), r.F32(), r.F32() };
        e.Quat = new[] { r.F32(), r.F32(), r.F32(), r.F32() };
        r.U8();
        float start = r.F32();
        r.F32();
        r.U32();

        e.StartTimeSecs = start >= 0f && start < 3600f ? start : 0f;
    }

    private static void ReadEmitterTail(Reader r, FxEmitter e)
    {
        r.U8();
        e.Rate = ReadStl(r, 2);
        e.RateSpread = ReadStl(r, 3);
        e.MaxActive = (int)r.U32();
        e.InitialNum = r.F32();

        // StartFadeInDistance / EndFadeInDistance / StartFadeOutDistance / EndFadeOutDistance.
        // LOD distances, not times — the tool's legacy clamp at 0x832446B8 pins the last two to
        // 25 and 32, which is exactly where the shipped medians sit.
        r.F32();
        r.F32();
        r.F32();
        r.F32();

        e.SortParticles = r.U8() != 0;
        e.ForceToFullSizeRenderBuffer = r.U8() != 0;
        e.AttachToRoot = r.U8() != 0;
        ReadBase(r, e);
    }

    private static void ReadStandard(Reader r, FxEmitter e)
    {
        OptBaad(r);
        if (e.SubType is 1 or 2)
        {
            e.SpawnRadius = r.F32();
            e.EmitOnSurface = r.U8() != 0;
            e.UseShapeSpawnDir = r.U8() != 0;
        }
        else if (e.SubType == 3)
        {
            r.U8();
        }

        e.Speed = ReadStl(r, 4);
        e.SpeedSpread = ReadStl(r, 5);
        SkipVtl(r);
        e.AccelDirScale = ReadStl(r, 6);
        e.GravityScale = ReadStl(r, 7);
        e.GlobalWindScale = ReadStl(r, 8);
        e.VelX = ReadStl(r, 9);
        e.VelY = ReadStl(r, 10);
        e.VelZ = ReadStl(r, 11);
        SkipVtl(r);
        e.Wind = ReadStl(r, 12);
        SkipStl(r, 13);
        SkipVtl(r);
        SkipStl(r, 14);
        r.U8();
        r.U8();
        r.F32();
        ReadEmitterTail(r, e);
        e.HasMotion = true;
    }

    private static void ReadLight(Reader r, FxEmitter e)
    {
        // `PositionalInteractable`, then the halo texture's name hash — the field `sub_8323FA50`
        // reads back from type+196 and passes to the descriptor alongside params 22 and 23.
        r.U8();
        r.U32();

        e.LightColour = ReadVtl(r);
        e.LightRange = ReadStl(r, 15);
        e.LightIntensity = ReadStl(r, 16);
        e.LightFadeDistance = ReadStl(r, 17);
        e.LightFadeRange = ReadStl(r, 18);

        // 19..21 are a vec3 and 22..23 a pair, both handed to the descriptor builder; their
        // meaning was not established, and a light renders correctly without them.
        foreach (int idx in new[] { 19, 20, 21, 22, 23, 0, 1 })
        {
            SkipStl(r, idx);
        }

        ReadBase(r, e);
        e.IsLight = true;
    }

    private static void ReadLight8(Reader r, FxEmitter e)
    {
        r.U8();
        r.U32();
        r.U32();
        r.U8();
        SkipVtl(r);
        foreach (int idx in new[] { 15, 24, 16, 17, 18, 19, 20, 21, 22, 23, 0, 1 })
        {
            SkipStl(r, idx);
        }

        ReadBase(r, e);
    }

    /// <summary>
    /// <c>CSplineObject::LoadBinary</c>. The field order is the WRITER's, `sub_8323D010`, whose
    /// sibling archive serialiser spells every name out.
    ///
    /// THE MAGIC IS OPTIONAL. The reader marks its position, reads a u32, and if it is not
    /// <c>0x50B5F001</c> it seeks BACK and leaves the version at 0 — so a spline with no header
    /// is legal and its last two flags are simply absent. All shipped data is version 4, but the
    /// rewind costs one comparison and a parser that assumed the header would desync the entire
    /// entity list on the first file that omitted it.
    /// </summary>
    private static void ReadSplineObject(Reader r, FxEmitter e)
    {
        const uint splineMagic = 0x50B5F001u;

        int mark = r.O;
        uint version = 0;
        if (r.U32() == splineMagic)
        {
            version = r.U32();
        }
        else
        {
            r.O = mark;
        }

        uint np = r.U32();
        if (np > 4096)
        {
            r.Bad = true;
            return;
        }

        var points = new float[np][];
        for (uint i = 0; i < np && !r.Bad; i++)
        {
            points[i] = new[] { r.F32(), r.F32(), r.F32() };
        }

        e.SplinePoints = points;
        e.SplineRandomGenerator = r.U8() != 0;
        e.SplineReverseFlow = r.U8() != 0;
        e.SplineRate = r.F32();
        e.SplineLooped = r.U8() != 0;
        e.SplineRandomStart = r.U8() != 0;

        // Version-gated exactly as the reader gates them.
        e.SplineStraight = version >= 3 && r.U8() != 0;
        e.SplineScaleEmissionByLength = version >= 4 && r.U8() != 0;
    }

    private static void ReadSplineEntity(Reader r, FxEmitter e)
    {
        // `PositionalInteractable` comes FIRST, before CSplineObject's own header. The reader
        // this replaces took a u32 and then a u8, which is the same 5 bytes in the wrong order —
        // it landed byte-exact on the file's last byte across all 2751 entities and still read
        // the flags off by one field.
        r.U8();
        ReadSplineObject(r, e);
        SkipStl(r, 0);
        SkipStl(r, 1);
        ReadBase(r, e);
        e.IsSpline = true;
    }

    private static void ReadSplineEmitter(Reader r, FxEmitter e)
    {
        ReadEmitterTail(r, e);
        r.U32();
        r.U32();
        r.U8();
        r.U8();
        r.F32();
        r.Skip(4);
        r.F32();
        r.F32();
        r.F32();
        r.F32();
        r.F32();
        r.U8();
        r.U8();
        e.HasMotion = true;
    }

    private static void ReadType3(Reader r, FxEmitter e)
    {
        r.U8();
        SkipStl(r, 25);
        r.F32();
        SkipStl(r, 0);
        SkipStl(r, 1);
        ReadBase(r, e);
    }

    private static void ReadAttractor(Reader r, FxEmitter e)
    {
        r.U8();
        e.AttractRadius = ReadStl(r, 26);
        e.AttractStrength = ReadStl(r, 27);
        e.GlobalAttractor = r.U8() != 0;
        SkipStl(r, 0);
        SkipStl(r, 1);
        ReadBase(r, e);
        e.IsAttractor = true;
    }

    private static bool ReadEntity(Reader r, FxEmitter e)
    {
        e.TypeId = r.U32();
        e.EntityId = r.U32();
        e.SubType = r.U32();
        switch (e.TypeId)
        {
            case 0: ReadStandard(r, e); break;
            case 3: ReadType3(r, e); break;
            case 4: ReadAttractor(r, e); break;
            case 5: ReadSplineEntity(r, e); break;
            case 6: ReadBase(r, e); break;
            case 7: ReadLight(r, e); break;
            case 8: ReadLight8(r, e); break;
            case 9: ReadSplineEmitter(r, e); break;
            default: return false;
        }

        return !r.Bad;
    }

    private static bool WalkNode(Reader r, FxVisual? outVis, ref bool outTextured)
    {
        uint nid = r.U32();
        r.U32();
        uint cnt = r.U32();
        if (r.Bad || cnt > 64)
        {
            r.Bad = true;
            return false;
        }

        bool hasMesh = false;
        for (uint i = 0; i < cnt && !r.Bad; i++)
        {
            uint tag = r.U32();
            switch (tag)
            {
                case 0x1:
                    break;
                case 0x2:
                {
                    float av = r.F32();
                    if (outVis is not null)
                    {
                        outVis.Alpha = av;
                        outVis.HasAlpha = true;
                    }

                    break;
                }

                case 0x400:
                {
                    float tw = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasScalarSize = true;
                        outVis.ScalarSize = tw;
                    }

                    break;
                }

                case 0x4:
                {
                    float cr = r.F32();
                    float cg = r.F32();
                    float cb = r.F32();
                    if (outVis is not null)
                    {
                        outVis.Color = new[] { cr, cg, cb };
                        outVis.HasColor = true;
                    }

                    break;
                }

                case 0x8:
                {
                    r.U32();
                    uint ln = r.U32();
                    if (r.Bad || ln > 512 || !r.Have((int)ln + 1))
                    {
                        r.Bad = true;
                        break;
                    }

                    string path = System.Text.Encoding.ASCII.GetString(
                        r.Data, r.O, (int)ln);
                    r.Skip((int)ln + 1);
                    uint nx = r.U32();
                    uint ny = r.U32();
                    byte rif = r.U8();
                    float spd = r.F32();
                    byte disp = r.U8();
                    r.U8();
                    r.F32();
                    if (outVis is not null)
                    {
                        outVis.Texture = path;
                        outVis.TexCols = nx is >= 1 and <= 16 ? (int)nx : 1;
                        outVis.TexRows = ny is >= 1 and <= 16 ? (int)ny : 1;
                        outVis.RandomFrame = rif != 0;
                        outVis.FrameSpeed = spd;
                        outVis.Displacement = disp != 0;
                        outVis.HasTexture = true;
                    }

                    outTextured = true;
                    break;
                }

                case 0x10:
                {
                    float sx = r.F32();
                    float sy = r.F32();
                    if (outVis is not null)
                    {
                        outVis.SizeX = sx;
                        outVis.SizeY = sy;
                        outVis.HasSize = true;
                    }

                    break;
                }

                case 0x10000:
                {
                    float gsx = r.F32();
                    float gsy = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasGlobalScale = true;
                        outVis.GlobalScale = new[] { gsx, gsy };
                    }

                    break;
                }

                case 0x20:
                {
                    uint sp = r.U32();
                    if (outVis is not null)
                    {
                        outVis.HasSpritePoints = true;
                        outVis.SpritePoints = (int)sp;
                    }

                    break;
                }
                case 0x800:
                {
                    uint tl = r.U32();
                    if (outVis is not null)
                    {
                        outVis.HasNumSegments = true;
                        outVis.NumSegments = tl is >= 1 and <= 512 ? (int)tl : 1;
                    }

                    break;
                }

                case 0x200:
                {
                    float speed = r.F32();
                    float spread = r.F32();
                    byte initRand = r.U8();
                    if (outVis is not null)
                    {
                        outVis.HasRotation = true;
                        outVis.RotSpeed = speed;
                        outVis.RotSpread = spread;
                        outVis.RotInitialRandom = initRand != 0;
                    }

                    break;
                }

                case 0x20000:
                {
                    float qx = r.F32();
                    float qy = r.F32();
                    float qz = r.F32();
                    float qw = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasAlignQuat = true;
                        outVis.AlignQuat = new[] { qx, qy, qz, qw };
                    }

                    break;
                }

                case 0x40:
                {
                    uint b = r.U32();
                    if (outVis is not null)
                    {
                        outVis.SrcBlend = (int)b;
                    }

                    break;
                }

                case 0x80:
                {
                    uint b = r.U32();
                    if (outVis is not null)
                    {
                        outVis.DstBlend = (int)b;
                    }

                    break;
                }

                case 0x100:
                {
                    uint op = r.U32();
                    if (outVis is not null)
                    {
                        outVis.HasBlendOp = true;
                        outVis.BlendOp = (int)op;
                    }

                    break;
                }

                case 0x1000:
                {
                    r.U32();
                    uint ln = r.U32();
                    if (r.Bad || ln > 512 || !r.Have((int)ln + 2))
                    {
                        r.Bad = true;
                        break;
                    }

                    string mpath = System.Text.Encoding.ASCII.GetString(
                        r.Data, r.O, (int)ln);
                    r.Skip((int)ln + 1 + 1);
                    if (outVis is not null)
                    {
                        outVis.HasMesh = true;
                        outVis.MeshPath = mpath;
                    }

                    hasMesh = true;
                    break;
                }

                case 0x2000:
                {
                    if (hasMesh)
                    {
                        float fq = r.F32();
                        float mn = r.F32();
                        float rg = r.F32();
                        if (outVis is not null)
                        {
                            outVis.HasRandScale = true;
                            outVis.RsFreq = fq;
                            outVis.RsMin = mn;
                            outVis.RsRange = rg;
                        }

                        break;
                    }

                    float per = r.F32();
                    float bx = r.F32();
                    float by = r.F32();
                    float sx2 = r.F32();
                    float sy2 = r.F32();
                    byte shared = r.U8();
                    if (outVis is not null)
                    {
                        outVis.HasFlicker = true;
                        outVis.FlickerPeriod = per;
                        outVis.FlickerBaseX = bx;
                        outVis.FlickerBaseY = by;
                        outVis.FlickerSpreadX = sx2;
                        outVis.FlickerSpreadY = sy2;
                        outVis.FlickerShared = shared != 0;
                    }

                    break;
                }

                case 0x4000:
                {
                    float osp = r.F32();
                    float ospr = r.F32();
                    byte ra = r.U8();
                    float ax = r.F32();
                    float ay = r.F32();
                    float az = r.F32();
                    byte rx = r.U8();
                    if (outVis is not null)
                    {
                        outVis.HasOrient = true;
                        outVis.OrSpeed = osp;
                        outVis.OrSpread = ospr;
                        outVis.OrRandAngle = ra != 0;
                        outVis.OrAxis = new[] { ax, ay, az };
                        outVis.OrRandAxis = rx != 0;
                    }

                    break;
                }

                case 0x8000:
                {
                    float si = r.F32();
                    if (outVis is not null)
                    {
                        outVis.HasSelfIllum = true;
                        outVis.SelfIllum = si;
                    }

                    break;
                }

                default:
                    r.Bad = true;
                    break;
            }
        }

        uint nid2 = r.U32();
        if (r.Bad || nid2 != nid)
        {
            r.Bad = true;
            return false;
        }

        return true;
    }

    public static bool Parse(byte[] bytes, FxBank outBank)
    {
        var r = new Reader(bytes);
        byte[] magic = System.Text.Encoding.ASCII.GetBytes("ParticleBankFile");
        if (bytes.Length < 0x2C)
        {
            outBank.Error = "too small";
            return false;
        }

        for (int i = 0; i < 16; i++)
        {
            if (bytes[i] != magic[i])
            {
                outBank.Error = "bad magic";
                return false;
            }
        }

        r.O = 16;
        if (r.Peek(r.O) == Baad)
        {
            r.Skip(4);
        }

        outBank.Version = r.U32();

        r.O = 0x20;
        r.U32();
        uint c2 = r.U32();
        bool sectionsOk = c2 > 0 && c2 < 200000;
        bool ignored = false;
        for (uint i = 0; sectionsOk && i < c2; i++)
        {
            if (!WalkNode(r, null, ref ignored))
            {
                sectionsOk = false;
                break;
            }
        }

        if (sectionsOk)
        {
            r.U32();
            uint c3 = r.U32();
            if (c3 == 0 || c3 > 200000)
            {
                sectionsOk = false;
            }

            for (uint i = 0; sectionsOk && i < c3; i++)
            {
                var sys = new FxSystemDef { Id = r.U32(), Mode = r.U32() };
                uint cc = r.U32();
                if (r.Bad || cc > 32)
                {
                    sectionsOk = false;
                    break;
                }

                var raw = new List<FxVisual>();
                for (uint j = 0; j < cc; j++)
                {
                    if (r.Peek(r.O) == ChildMark)
                    {
                        r.Skip(8);
                    }

                    float delay = r.F32();
                    var vis = new FxVisual { Delay = delay };
                    bool textured = false;
                    if (!WalkNode(r, vis, ref textured))
                    {
                        sectionsOk = false;
                        break;
                    }

                    raw.Add(vis);
                }

                if (!sectionsOk)
                {
                    break;
                }

                for (int k = 1; k < raw.Count; k++)
                {
                    FxVisual v = raw[k];
                    FxVisual p = raw[k - 1];
                    if (!v.HasAlpha && p.HasAlpha)
                    {
                        v.HasAlpha = true;
                        v.Alpha = p.Alpha;
                    }

                    if (!v.HasColor && p.HasColor)
                    {
                        v.HasColor = true;
                        v.Color = (float[])p.Color.Clone();
                    }

                    if (!v.HasTexture && p.HasTexture)
                    {
                        v.HasTexture = true;
                        v.Texture = p.Texture;
                        v.TexCols = p.TexCols;
                        v.TexRows = p.TexRows;
                        v.RandomFrame = p.RandomFrame;
                        v.FrameSpeed = p.FrameSpeed;
                        v.Displacement = p.Displacement;
                    }

                    if (!v.HasSize && p.HasSize)
                    {
                        v.HasSize = true;
                        v.SizeX = p.SizeX;
                        v.SizeY = p.SizeY;
                    }

                    if (v.SrcBlend < 0)
                    {
                        v.SrcBlend = p.SrcBlend;
                    }

                    if (v.DstBlend < 0)
                    {
                        v.DstBlend = p.DstBlend;
                    }

                    if (!v.HasRotation && p.HasRotation)
                    {
                        v.HasRotation = true;
                        v.RotSpeed = p.RotSpeed;
                        v.RotSpread = p.RotSpread;
                        v.RotInitialRandom = p.RotInitialRandom;
                    }

                    if (!v.HasFlicker && p.HasFlicker)
                    {
                        v.HasFlicker = true;
                        v.FlickerPeriod = p.FlickerPeriod;
                        v.FlickerBaseX = p.FlickerBaseX;
                        v.FlickerBaseY = p.FlickerBaseY;
                        v.FlickerSpreadX = p.FlickerSpreadX;
                        v.FlickerSpreadY = p.FlickerSpreadY;
                        v.FlickerShared = p.FlickerShared;
                    }

                    if (!v.HasScalarSize && p.HasScalarSize)
                    {
                        v.HasScalarSize = true;
                        v.ScalarSize = p.ScalarSize;
                    }

                    if (!v.HasNumSegments && p.HasNumSegments)
                    {
                        v.HasNumSegments = true;
                        v.NumSegments = p.NumSegments;
                    }

                    if (!v.HasAlignQuat && p.HasAlignQuat)
                    {
                        v.HasAlignQuat = true;
                        v.AlignQuat = (float[])p.AlignQuat.Clone();
                    }

                    if (!v.HasMesh && p.HasMesh)
                    {
                        v.HasMesh = true;
                        v.MeshPath = p.MeshPath;
                    }

                    if (!v.HasRandScale && p.HasRandScale)
                    {
                        v.HasRandScale = true;
                        v.RsFreq = p.RsFreq;
                        v.RsMin = p.RsMin;
                        v.RsRange = p.RsRange;
                    }

                    if (!v.HasOrient && p.HasOrient)
                    {
                        v.HasOrient = true;
                        v.OrSpeed = p.OrSpeed;
                        v.OrSpread = p.OrSpread;
                        v.OrRandAngle = p.OrRandAngle;
                        v.OrAxis = (float[])p.OrAxis.Clone();
                        v.OrRandAxis = p.OrRandAxis;
                    }
                }

                foreach (FxVisual v in raw)
                {
                    if (v.HasTexture || v.HasMesh)
                    {
                        sys.Visuals.Add(v);
                    }
                }

                r.Skip(12);
                uint id2 = r.U32();
                if (r.Bad || id2 != sys.Id)
                {
                    sectionsOk = false;
                    break;
                }

                outBank.Systems.Add(sys);
            }
        }

        int sec4At = sectionsOk ? r.O : Sec4Fixed;
        if (!sectionsOk)
        {
            outBank.Systems.Clear();
            outBank.Error = "sec2/3 walk failed; effects unavailable";
        }

        if (sec4At + 8 > bytes.Length)
        {
            outBank.Error = "no section 4";
            return false;
        }

        r.O = sec4At;
        r.Bad = false;
        r.U32();
        uint count = r.U32();
        if (count == 0 || count > 200000)
        {
            outBank.Error = "bad entity count";
            return false;
        }

        for (uint k = 0; k < count; k++)
        {
            var e = new FxEmitter();
            int start = r.O;
            if (!ReadEntity(r, e) || r.O <= start)
            {
                outBank.Error = $"entity parse stopped at #{k}";
                break;
            }

            outBank.Emitters.Add(e);
        }

        if (sectionsOk && outBank.Emitters.Count == count && r.O + 8 <= bytes.Length)
        {
            var eidToIdx = new Dictionary<uint, int>();
            for (int i = 0; i < outBank.Emitters.Count; i++)
            {
                eidToIdx[outBank.Emitters[i].EntityId] = i;
            }

            var sysToIdx = new Dictionary<uint, int>();
            for (int i = 0; i < outBank.Systems.Count; i++)
            {
                sysToIdx[outBank.Systems[i].Id] = i;
            }

            r.U32();
            r.U32();
            var typesInRecord = new List<uint>();
            while (r.O + 17 <= bytes.Length && !r.Bad)
            {
                int recStart = r.O;
                uint key = r.U32();
                r.U32();
                r.U32();
                byte cnt = r.U8();
                if (cnt > 64)
                {
                    r.O = recStart;
                    break;
                }

                var eff = new FxEffect { NameHash = key };
                bool recOk = true;
                typesInRecord.Clear();
                for (byte i = 0; i < cnt && recOk; i++)
                {
                    uint group = r.U32();
                    uint m = r.U32();
                    if (r.Bad || m > 16)
                    {
                        recOk = false;
                        break;
                    }

                    for (uint j = 0; j < m; j++)
                    {
                        uint eid = r.U32();
                        uint sid = r.U32();
                        r.U32();

                        // THE JOIN A LIVE INSTANCE NEEDS. `eid` is the entity TYPE's id hash —
                        // the very value a running instance carries at `*(type+4)` — so recording
                        // which effect claimed it is what lets the live feed name what it is
                        // looking at. Held aside until the record validates, so a torn record
                        // cannot poison the map.
                        typesInRecord.Add(eid);

                        int emitterIdx = eidToIdx.TryGetValue(eid, out int ei) ? ei : -1;
                        int systemIdx = sysToIdx.TryGetValue(sid, out int si) ? si : -1;
                        if (emitterIdx >= 0)
                        {
                            eff.Parts.Add(new FxEffectPart(emitterIdx, systemIdx, group, i));
                        }
                    }
                }

                uint key2 = r.U32();
                if (!recOk || r.Bad || key2 != key)
                {
                    r.O = recStart;
                    break;
                }

                outBank.Effects.Add(eff);

                // Committed only now the record has validated its trailing key.
                foreach (uint typeId in typesInRecord)
                {
                    if (!outBank.TypeToEffect.TryAdd(typeId, key))
                    {
                        outBank.AmbiguousTypes++;
                    }
                }
            }
        }

        outBank.Ok = outBank.Emitters.Count > 0;
        if (!outBank.Ok && outBank.Error.Length == 0)
        {
            outBank.Error = "no emitters parsed";
        }

        return outBank.Ok;
    }
}
