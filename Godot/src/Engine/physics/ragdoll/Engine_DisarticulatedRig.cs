using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Physics;

/// <summary>
/// Host-driven Center-of-Mass + two-endpoint capsule rigid-body scatter, tumble, clatter, and
/// reassembly simulation for disarticulated ("crumbled") skeletons across titles (Fable 2 and
/// Fable 3).
/// </summary>
/// <remarks>
/// Replaces low-rate guest Havok ragdoll stepping on rigid-piece bone models (such as Fable 2's
/// <c>Skeleton_Base.mdl</c> and Fable 3's flat-root crumble piles) with an energy-conserving
/// display-rate simulation. Only real geometry-bearing anatomical pieces (~17 on a humanoid
/// skeleton) are simulated; empty dummy bones and finger/toe phalanges follow their parent pieces
/// rigidly so hands and feet scatter intact. When a living creature recovers from crumble,
/// <see cref="BeginReassembly"/> arcs every scattered piece smoothly back to the entity's spawn /
/// rise pose.
/// </remarks>
public sealed class Engine_DisarticulatedRig
{
    /// <summary>
    /// Samples ground contact at a world position, returning <c>true</c> and writing the contact
    /// world position and surface normal when a terrain heightfield covers that XZ point.
    /// </summary>
    public delegate bool TrySampleGroundContact(Vector3 worldPos, out Vector3 contactPos, out Vector3 contactNormal);

    /// <summary>Live rigid-body state for a single disarticulated bone in world space.</summary>
    public struct BoneBody
    {
        /// <summary>World-space joint origin (synced from <see cref="ComPosition"/> minus rotated <see cref="LocalCom"/>).</summary>
        public Vector3 Position;

        /// <summary>World-space center of mass that linear and angular rigid-body dynamics integrate around.</summary>
        public Vector3 ComPosition;

        public Quaternion Rotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;

        /// <summary>Bone-local offset from the joint origin to the piece's center of mass.</summary>
        public Vector3 LocalCom;

        /// <summary>Bone-local unit principal shaft axis for two-endpoint capsule collision.</summary>
        public Vector3 LocalAxis;

        /// <summary>Half-length of the inner capsule segment along <see cref="LocalAxis"/> (0 for spheres).</summary>
        public float HalfLength;

        public float Radius;
        public float Mass;
        public float InvMass;
        public float InvInertia;
        public float Restitution;
        public bool IsSimulated;
        public bool IsRootWrapper;
        public bool Asleep;
        public int SleepSteps;
    }

    private const float Gravity = -9.81f;
    private const float AirDragPerSecond = 0.22f;
    private const float GroundSlideFrictionPerSec = 3.2f;
    private const float GroundAngularDecayPerSec = 4.5f;
    private const float GroundCoulombSlideDecel = 5.8f;
    private const float GroundRollingAngularDecel = 14.0f;
    private const float ImpactCoulombMu = 0.58f;
    private const float BounceMinNormalSpeed = 0.40f;
    private const float BoneCollisionRestitution = 0.16f;
    private const float BoneCollisionMinSpeed = 0.35f;
    private const float SleepSpeedSq = 0.055f * 0.055f;
    private const float SleepAngularSpeedSq = 0.25f * 0.25f;
    private const int SleepStepsRequired = 12;
    private const float CryptSlabRadiusSq = 1.45f * 1.45f;
    public const float DefaultReassemblyDuration = 0.32f;
    private const float MaxAngularSpeed = 14.0f;
    private const float MaxSubstepDt = 1f / 60f;

    private BoneBody[] _bodies = Array.Empty<BoneBody>();
    private Transform3D[] _modelPoses = Array.Empty<Transform3D>();
    private Transform3D[] _restGlobals = Array.Empty<Transform3D>();
    private Transform3D[] _localRest = Array.Empty<Transform3D>();
    private int[] _parents = Array.Empty<int>();
    private int[] _activeIndices = Array.Empty<int>();
    private Vector3[] _reassemblyStartJoint = Array.Empty<Vector3>();
    private Quaternion[] _reassemblyStartRot = Array.Empty<Quaternion>();
    private int _activeCount;
    private float _elapsed;
    private float _peakSpread;
    private float _reassemblyElapsed;
    private float _reassemblyDuration = DefaultReassemblyDuration;
    private float _spawnOriginY;
    private bool _hasOriginGround;
    private float _originGroundY;
    private bool _originGroundSampled;
    private ulong _lastSteppedFrame = ulong.MaxValue;

    /// <summary>Total bone count in model order (simulated pieces + rigid followers + root wrapper).</summary>
    public int BoneCount { get; private set; }

    /// <summary>Number of actively simulated rigid pieces (excluding empty dummies and welded digit followers).</summary>
    public int SimulatedPieceCount => _activeCount;

    /// <summary>Elapsed simulation time in seconds since shatter initialization.</summary>
    public float Elapsed => _elapsed;

    /// <summary>True while scattered bones are flying back to the entity's spawn / rise pose.</summary>
    public bool IsReassembling { get; private set; }

    /// <summary>Normalised reassembly progress in [0, 1].</summary>
    public float ReassemblyBlend { get; private set; }

    /// <summary>True once reassembly reached 1.0 and the living mesh / animator can resume ownership.</summary>
    public bool ReassemblyFinished { get; private set; }

    /// <summary>True once every simulated bone piece has clattered and settled to rest.</summary>
    public bool AllAsleep
    {
        get
        {
            for (int a = 0; a < _activeCount; ++a)
            {
                if (!_bodies[_activeIndices[a]].Asleep)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Latest simulated transforms in entity-local model space (length equals <see cref="BoneCount"/>).</summary>
    public ReadOnlySpan<Transform3D> ModelPoses => _modelPoses.AsSpan(0, BoneCount);

    /// <summary>Read-only span of live world-space bone states for inspection and tests.</summary>
    public ReadOnlySpan<BoneBody> Bodies => _bodies.AsSpan(0, BoneCount);

    /// <summary>Frame index on which <see cref="Step"/> last ran, preventing duplicate steps per frame.</summary>
    public ulong LastSteppedFrame
    {
        get => _lastSteppedFrame;
        set => _lastSteppedFrame = value;
    }

    /// <summary>
    /// Seeds every bone in the crumbled rig from matching live animated world transforms when
    /// available (zero visual pop) using procedural piece bounds when surface descriptors are omitted.
    /// </summary>
    public void Initialize(
        Transform3D entityWorld,
        IReadOnlyList<string> boneNames,
        ReadOnlySpan<Transform3D> restGlobals,
        ReadOnlySpan<int> parents,
        IReadOnlyDictionary<string, (Vector3 Pos, Quaternion Rot)>? liveBones,
        Vector3 inheritedVelocity,
        uint seed) =>
        Initialize(entityWorld, boneNames, restGlobals, parents, liveBones, inheritedVelocity, seed, default);

    /// <summary>
    /// Seeds every bone in the crumbled rig from matching live animated world transforms when
    /// available (zero visual pop), using precomputed per-bone COM and capsule descriptors from
    /// <see cref="Engine_MdlDisarticulation"/> when provided.
    /// </summary>
    public void Initialize(
        Transform3D entityWorld,
        IReadOnlyList<string> boneNames,
        ReadOnlySpan<Transform3D> restGlobals,
        ReadOnlySpan<int> parents,
        IReadOnlyDictionary<string, (Vector3 Pos, Quaternion Rot)>? liveBones,
        Vector3 inheritedVelocity,
        uint seed,
        ReadOnlySpan<Engine_MdlDisarticulation.BonePieceDescriptor> descriptors)
    {
        int count = boneNames.Count;
        BoneCount = count;
        EnsureCapacity(count);

        _elapsed = 0f;
        _peakSpread = 0f;
        _reassemblyElapsed = 0f;
        _reassemblyDuration = DefaultReassemblyDuration;
        IsReassembling = false;
        ReassemblyBlend = 0f;
        ReassemblyFinished = false;
        _originGroundSampled = false;
        _hasOriginGround = false;
        _spawnOriginY = entityWorld.Origin.IsFinite() ? entityWorld.Origin.Y : 0f;
        _originGroundY = _spawnOriginY;
        _lastSteppedFrame = ulong.MaxValue;
        _activeCount = 0;

        bool hasDescriptors = descriptors.Length == count;
        Quaternion entityRot = entityWorld.Basis.Orthonormalized().GetRotationQuaternion();
        int directChildrenOfZero = 0;
        for (int k = 1; k < count; ++k)
        {
            if (k < parents.Length && parents[k] == 0)
            {
                ++directChildrenOfZero;
            }
        }

        // Cache parent links, global rests, and local rest transforms for follower reconstruction and reassembly.
        for (int m = 0; m < count; ++m)
        {
            int p = m < parents.Length ? parents[m] : -1;
            _parents[m] = p;
            Transform3D rest = m < restGlobals.Length ? restGlobals[m] : Transform3D.Identity;
            _restGlobals[m] = rest;
            if (p >= 0 && p < m)
            {
                Transform3D parentRest = _restGlobals[p];
                _localRest[m] = parentRest.Basis.Determinant() is > 1e-6f or < -1e-6f
                    ? parentRest.AffineInverse() * rest
                    : rest;
            }
            else
            {
                _localRest[m] = rest;
            }
        }

        Vector3 centroid = Vector3.Zero;
        for (int m = 0; m < count; ++m)
        {
            bool rootWrapper;
            bool isSimulated;
            Vector3 localCom;
            Vector3 localAxis;
            float halfLength;
            float radius;
            float mass;

            if (hasDescriptors)
            {
                Engine_MdlDisarticulation.BonePieceDescriptor desc = descriptors[m];
                rootWrapper = desc.IsRootWrapper;
                isSimulated = desc.IsSimulated && !rootWrapper;
                localCom = desc.LocalCom.IsFinite() ? desc.LocalCom : Vector3.Zero;
                localAxis = desc.LocalAxis.IsFinite() && desc.LocalAxis.LengthSquared() > 1e-6f
                    ? desc.LocalAxis.Normalized()
                    : Vector3.Up;
                halfLength = float.IsFinite(desc.HalfLength) ? MathF.Max(0f, desc.HalfLength) : 0f;
                radius = float.IsFinite(desc.Radius) ? Math.Clamp(desc.Radius, 0.018f, 0.25f) : GuessBoneRadius(boneNames[m]);
                mass = float.IsFinite(desc.Mass) ? Math.Clamp(desc.Mass, 0.15f, 6.0f) : 1.0f;
            }
            else
            {
                rootWrapper = _parents[m] < 0 && m == 0 &&
                    (directChildrenOfZero >= 6 ||
                     boneNames[m].Contains("Locator", StringComparison.OrdinalIgnoreCase) ||
                     boneNames[m].Contains("Rig", StringComparison.OrdinalIgnoreCase) ||
                     boneNames[m].Contains("Root", StringComparison.OrdinalIgnoreCase));
                isSimulated = !rootWrapper;
                localCom = Vector3.Zero;
                localAxis = Vector3.Up;
                halfLength = 0f;
                radius = GuessBoneRadius(boneNames[m]);
                mass = GuessBoneMass(boneNames[m]);
            }

            Vector3 jointPos;
            Quaternion rot;
            if (!rootWrapper && liveBones is not null && liveBones.TryGetValue(boneNames[m], out var matched))
            {
                jointPos = matched.Pos.IsFinite() ? matched.Pos : (entityWorld * _restGlobals[m].Origin);
                rot = matched.Rot.IsFinite() && matched.Rot.LengthSquared() > 1e-6f
                    ? matched.Rot.Normalized()
                    : entityRot;
            }
            else
            {
                Transform3D worldRest = entityWorld * _restGlobals[m];
                jointPos = worldRest.Origin.IsFinite() ? worldRest.Origin : entityWorld.Origin;
                rot = worldRest.Basis.Orthonormalized().GetRotationQuaternion();
            }

            Vector3 comPos = jointPos + (rot * localCom);
            float inertia = ComputeCapsuleInertia(mass, radius, halfLength);

            _bodies[m] = new BoneBody
            {
                Position = jointPos,
                ComPosition = comPos,
                Rotation = rot,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
                LocalCom = localCom,
                LocalAxis = localAxis,
                HalfLength = halfLength,
                Radius = radius,
                Mass = mass,
                InvMass = 1f / mass,
                InvInertia = 1f / inertia,
                Restitution = 0.18f,
                IsSimulated = isSimulated,
                IsRootWrapper = rootWrapper,
                Asleep = !isSimulated,
                SleepSteps = 0,
            };

            if (isSimulated)
            {
                _activeIndices[_activeCount++] = m;
                centroid += comPos;
            }
        }

        if (_activeCount > 0)
        {
            centroid /= _activeCount;
        }
        else
        {
            centroid = entityWorld.Origin + (Vector3.Up * 0.8f);
        }

        // Inherit entity / knockback velocity faithfully so melee hits and Force Push scatter bones naturally.
        Vector3 inherited = inheritedVelocity.IsFinite() ? inheritedVelocity * 0.72f : Vector3.Zero;
        float inheritedSpeed = inherited.Length();
        if (inheritedSpeed > 7.5f)
        {
            inherited *= 7.5f / inheritedSpeed;
        }

        for (int a = 0; a < _activeCount; ++a)
        {
            int m = _activeIndices[a];
            ref BoneBody body = ref _bodies[m];

            uint rng = Hash(seed ^ (uint)((m * 0x9E3779B9u) + 17));
            float rx = NextUnit(ref rng);
            float rz = NextUnit(ref rng);
            float ry = NextZeroToOne(ref rng);
            float rOut = NextZeroToOne(ref rng);
            float rBounce = NextZeroToOne(ref rng);
            float rSpin = NextZeroToOne(ref rng);

            body.Restitution = GuessBoneRestitution(boneNames[m], rBounce);

            Vector3 radial = body.ComPosition - centroid;
            radial.Y = 0f;
            if (radial.LengthSquared() < 1e-4f)
            {
                radial = new Vector3(rx, 0f, rz);
            }

            radial = radial.LengthSquared() > 1e-6f ? radial.Normalized() : Vector3.Forward;

            // Natural collapse & outward separation (upper bones fan slightly further, lower bones collapse locally).
            float heightFrac = Math.Clamp((body.ComPosition.Y - _spawnOriginY) / 1.7f, 0f, 1f);
            float outwardSpeed = 0.45f + (0.65f * heightFrac) + (rOut * 0.75f);
            float upwardSpeed = (-0.08f + (0.28f * heightFrac)) + (ry * 0.28f);
            Vector3 lateralJitter = new Vector3(rx, 0f, rz) * 0.28f;
            body.LinearVelocity = inherited + (radial * outwardSpeed) + lateralJitter + (Vector3.Up * upwardSpeed);

            // Topple outward around the horizontal tangent perpendicular to radial direction plus subtle tumble.
            Vector3 toppleAxis = Vector3.Up.Cross(radial);
            Vector3 jitterAxis = new Vector3(NextUnit(ref rng), NextUnit(ref rng) * 0.5f, NextUnit(ref rng));
            Vector3 spinAxis = (toppleAxis * 0.75f) + (jitterAxis * 0.35f);
            spinAxis = spinAxis.LengthSquared() > 1e-4f ? spinAxis.Normalized() : Vector3.Right;
            float spinRate = 2.2f + (rSpin * 3.8f);
            body.AngularVelocity = spinAxis * spinRate;
        }

        WriteModelPoses(entityWorld);
    }

    /// <summary>
    /// Begins flying all scattered bone pieces smoothly back into the entity's spawn / rise pose
    /// over <paramref name="durationSeconds"/>. Safe to call repeatedly once already reassembling.
    /// </summary>
    public void BeginReassembly(float durationSeconds = DefaultReassemblyDuration)
    {
        if (IsReassembling)
        {
            return;
        }

        IsReassembling = true;
        ReassemblyFinished = false;
        ReassemblyBlend = 0f;
        _reassemblyElapsed = 0f;
        _reassemblyDuration = float.IsFinite(durationSeconds) && durationSeconds > 0.05f
            ? durationSeconds
            : DefaultReassemblyDuration;

        for (int a = 0; a < _activeCount; ++a)
        {
            int m = _activeIndices[a];
            ref BoneBody body = ref _bodies[m];
            _reassemblyStartJoint[m] = body.Position;
            _reassemblyStartRot[m] = body.Rotation;
            body.LinearVelocity = Vector3.Zero;
            body.AngularVelocity = Vector3.Zero;
            body.Asleep = false;
            body.SleepSteps = 0;
        }
    }

    /// <summary>
    /// Advances the disarticulated simulation by <paramref name="dt"/> seconds using COM + two-endpoint
    /// capsule collision against terrain heightfields and indoor/crypt slab floors, or advances
    /// reassembly flight back to <paramref name="entityWorld"/> when <see cref="IsReassembling"/> is true.
    /// </summary>
    public void Step(
        float dt,
        Transform3D entityWorld,
        TrySampleGroundContact? sampleGround,
        ReadOnlySpan<Vector3> guestWorldBones,
        int guestValidCount)
    {
        if (BoneCount == 0)
        {
            return;
        }

        if (!float.IsFinite(dt) || dt <= 0f)
        {
            WriteModelPoses(entityWorld, guestWorldBones, guestValidCount);
            return;
        }

        float clampedDt = Math.Clamp(dt, 0f, 0.05f);
        _elapsed += clampedDt;

        if (!_originGroundSampled && sampleGround is not null)
        {
            _originGroundSampled = true;
            _hasOriginGround = sampleGround(entityWorld.Origin, out Vector3 ogPos, out _);
            _originGroundY = _hasOriginGround ? ogPos.Y : _spawnOriginY;
        }

        bool originOnElevatedSlab = _hasOriginGround && _spawnOriginY > _originGroundY + 0.18f;
        float fallbackFloorY = _spawnOriginY;

        // Track guest bone spread solely to auto-detect guest ragdoll recovery (never to drag floorY downward).
        if (!IsReassembling && guestValidCount > 0)
        {
            float spreadSum = 0f;
            int spreadSamples = 0;
            for (int i = 0; i < guestWorldBones.Length; ++i)
            {
                Vector3 gp = guestWorldBones[i];
                if (!gp.IsFinite())
                {
                    continue;
                }

                float dx = gp.X - entityWorld.Origin.X;
                float dz = gp.Z - entityWorld.Origin.Z;
                spreadSum += MathF.Sqrt((dx * dx) + (dz * dz));
                ++spreadSamples;
            }

            if (spreadSamples > 0)
            {
                float meanGuestSpread = spreadSum / spreadSamples;
                _peakSpread = MathF.Max(_peakSpread, meanGuestSpread);
                if (_elapsed > 0.7f && guestValidCount >= 4 && _peakSpread > 0.85f && meanGuestSpread < 0.40f)
                {
                    BeginReassembly();
                }
            }
        }

        if (IsReassembling)
        {
            _reassemblyElapsed += clampedDt;
            ReassemblyBlend = Math.Clamp(_reassemblyElapsed / _reassemblyDuration, 0f, 1f);
            if (ReassemblyBlend >= 1f)
            {
                ReassemblyFinished = true;
            }

            WriteReassemblyPoses(entityWorld, guestWorldBones, guestValidCount);
            return;
        }

        int substeps = Math.Clamp((int)MathF.Ceiling(clampedDt / MaxSubstepDt), 1, 3);
        float subDt = clampedDt / substeps;
        float drag = MathF.Exp(-AirDragPerSecond * subDt);
        float slideDecay = MathF.Exp(-GroundSlideFrictionPerSec * subDt);
        float angDecay = MathF.Exp(-GroundAngularDecayPerSec * subDt);

        for (int step = 0; step < substeps; ++step)
        {
            // Pass 1: integrate gravity, air drag, COM translation, and rigid rotation.
            for (int a = 0; a < _activeCount; ++a)
            {
                ref BoneBody body = ref _bodies[_activeIndices[a]];
                if (body.Asleep)
                {
                    continue;
                }

                body.LinearVelocity.Y += Gravity * subDt;
                body.LinearVelocity *= drag;
                body.ComPosition += body.LinearVelocity * subDt;

                float spinMag = body.AngularVelocity.Length();
                if (spinMag > 1e-4f)
                {
                    if (spinMag > MaxAngularSpeed)
                    {
                        body.AngularVelocity *= MaxAngularSpeed / spinMag;
                        spinMag = MaxAngularSpeed;
                    }

                    Vector3 axis = body.AngularVelocity / spinMag;
                    var dq = new Quaternion(axis, spinMag * subDt);
                    body.Rotation = (dq * body.Rotation).Normalized();
                }
            }

            // Pass 2: capsule-to-capsule pairwise bone separation & passive clatter impulses across active pieces.
            for (int ai = 0; ai < _activeCount; ++ai)
            {
                ref BoneBody a = ref _bodies[_activeIndices[ai]];
                Vector3 axisA = a.HalfLength > 1e-4f ? (a.Rotation * a.LocalAxis) : Vector3.Up;
                Vector3 a0 = a.ComPosition - (axisA * a.HalfLength);
                Vector3 a1 = a.ComPosition + (axisA * a.HalfLength);

                for (int aj = ai + 1; aj < _activeCount; ++aj)
                {
                    ref BoneBody b = ref _bodies[_activeIndices[aj]];
                    if (a.Asleep && b.Asleep)
                    {
                        continue;
                    }

                    // Fast bounding-sphere early-out before closest-segment math.
                    float maxReach = a.HalfLength + a.Radius + b.HalfLength + b.Radius;
                    Vector3 comDelta = b.ComPosition - a.ComPosition;
                    if (comDelta.LengthSquared() >= maxReach * maxReach)
                    {
                        continue;
                    }

                    Vector3 axisB = b.HalfLength > 1e-4f ? (b.Rotation * b.LocalAxis) : Vector3.Up;
                    Vector3 b0 = b.ComPosition - (axisB * b.HalfLength);
                    Vector3 b1 = b.ComPosition + (axisB * b.HalfLength);

                    ClosestPointsOnSegments(a0, a1, b0, b1, out Vector3 pA, out Vector3 pB);
                    Vector3 delta = pB - pA;
                    float minDist = a.Radius + b.Radius;
                    float distSq = delta.LengthSquared();
                    if (distSq <= 1e-8f || distSq >= (minDist * minDist))
                    {
                        continue;
                    }

                    float dist = MathF.Sqrt(distSq);
                    Vector3 n = delta / dist;
                    float penetration = minDist - dist;

                    float invMassSum = a.InvMass + b.InvMass;
                    float ratioA;
                    float ratioB;
                    if (a.Asleep && !b.Asleep)
                    {
                        ratioA = 0.15f;
                        ratioB = 0.85f;
                    }
                    else if (!a.Asleep && b.Asleep)
                    {
                        ratioA = 0.85f;
                        ratioB = 0.15f;
                    }
                    else
                    {
                        ratioA = a.InvMass / invMassSum;
                        ratioB = b.InvMass / invMassSum;
                    }

                    a.ComPosition -= n * (penetration * ratioA);
                    b.ComPosition += n * (penetration * ratioB);

                    Vector3 rA = (pA + (n * a.Radius)) - a.ComPosition;
                    Vector3 rB = (pB - (n * b.Radius)) - b.ComPosition;
                    Vector3 vA = a.LinearVelocity + a.AngularVelocity.Cross(rA);
                    Vector3 vB = b.LinearVelocity + b.AngularVelocity.Cross(rB);
                    float relVel = (vB - vA).Dot(n);
                    if (relVel < 0f)
                    {
                        Vector3 rACrossN = rA.Cross(n);
                        Vector3 rBCrossN = rB.Cross(n);
                        float invEff = invMassSum +
                            (rACrossN.LengthSquared() * a.InvInertia) +
                            (rBCrossN.LengthSquared() * b.InvInertia);
                        float e = -relVel > BoneCollisionMinSpeed ? BoneCollisionRestitution : 0f;
                        float jImp = -(1f + e) * relVel / MathF.Max(invEff, 1e-5f);
                        Vector3 impulse = n * jImp;

                        a.LinearVelocity -= impulse * a.InvMass;
                        b.LinearVelocity += impulse * b.InvMass;
                        a.AngularVelocity -= rACrossN * (jImp * a.InvInertia);
                        b.AngularVelocity += rBCrossN * (jImp * b.InvInertia);

                        if (jImp * a.InvMass > 0.25f)
                        {
                            a.Asleep = false;
                            a.SleepSteps = 0;
                        }

                        if (jImp * b.InvMass > 0.25f)
                        {
                            b.Asleep = false;
                            b.SleepSteps = 0;
                        }
                    }

                    // Keep a0/a1 updated if bone A was pushed.
                    a0 = a.ComPosition - (axisA * a.HalfLength);
                    a1 = a.ComPosition + (axisA * a.HalfLength);
                }
            }

            // Pass 3: two-endpoint capsule ground contact with simultaneous 2-contact PGS impulse
            // resolution (eliminates seesaw rocking on flat/sloped ground) and strictly dissipative friction.
            for (int a = 0; a < _activeCount; ++a)
            {
                ref BoneBody body = ref _bodies[_activeIndices[a]];
                if (body.Asleep)
                {
                    continue;
                }

                bool hasCapsule = body.HalfLength > 1e-4f;
                Vector3 worldAxis = hasCapsule ? (body.Rotation * body.LocalAxis) : Vector3.Up;

                Vector3 e0 = hasCapsule ? body.ComPosition - (worldAxis * body.HalfLength) : body.ComPosition;
                ResolveFloorAt(e0, entityWorld.Origin, fallbackFloorY, originOnElevatedSlab, sampleGround, out float floorY0, out Vector3 n0);
                float pen0 = body.Radius - ((e0.Y - floorY0) * MathF.Max(n0.Y, 0.25f));

                Vector3 e1 = hasCapsule ? body.ComPosition + (worldAxis * body.HalfLength) : body.ComPosition;
                float pen1 = float.NegativeInfinity;
                Vector3 n1 = Vector3.Up;
                if (hasCapsule)
                {
                    ResolveFloorAt(e1, entityWorld.Origin, fallbackFloorY, originOnElevatedSlab, sampleGround, out float floorY1, out n1);
                    pen1 = body.Radius - ((e1.Y - floorY1) * MathF.Max(n1.Y, 0.25f));
                }

                bool hit0 = pen0 > 0f;
                bool hit1 = hasCapsule && pen1 > 0f;
                if (!hit0 && !hit1)
                {
                    body.SleepSteps = 0;
                    continue;
                }

                // Include the second endpoint in simultaneous PGS when within 4 mm contact skin so
                // horizontal shafts land flat cleanly without alternating single-end seesaw kicks.
                bool active0 = hit0 || (hit1 && pen0 > -0.004f);
                bool active1 = hit1 || (hasCapsule && hit0 && pen1 > -0.004f);

                // Positional projection along surface normal(s) before velocity solve.
                Vector3 avgNormal;
                if (hit0 && hit1)
                {
                    avgNormal = (n0 + n1).Normalized();
                    float maxPen = MathF.Max(pen0, pen1);
                    body.ComPosition += avgNormal * maxPen;
                }
                else if (hit0)
                {
                    avgNormal = n0;
                    body.ComPosition += n0 * pen0;
                }
                else
                {
                    avgNormal = n1;
                    body.ComPosition += n1 * pen1;
                }

                // Recompute contact lever arms relative to projected COM.
                Vector3 r0 = active0
                    ? ((hasCapsule ? body.ComPosition - (worldAxis * body.HalfLength) : body.ComPosition) - (n0 * body.Radius)) - body.ComPosition
                    : Vector3.Zero;
                Vector3 r1 = active1
                    ? ((body.ComPosition + (worldAxis * body.HalfLength)) - (n1 * body.Radius)) - body.ComPosition
                    : Vector3.Zero;

                Vector3 r0CrossN = active0 ? r0.Cross(n0) : Vector3.Zero;
                Vector3 r1CrossN = active1 ? r1.Cross(n1) : Vector3.Zero;
                float invEff0 = active0 ? MathF.Max(body.InvMass + (r0CrossN.LengthSquared() * body.InvInertia), 1e-5f) : 1f;
                float invEff1 = active1 ? MathF.Max(body.InvMass + (r1CrossN.LengthSquared() * body.InvInertia), 1e-5f) : 1f;

                float initVn0 = active0 ? (body.LinearVelocity + body.AngularVelocity.Cross(r0)).Dot(n0) : 0f;
                float initVn1 = active1 ? (body.LinearVelocity + body.AngularVelocity.Cross(r1)).Dot(n1) : 0f;
                float target0 = active0 && -initVn0 > BounceMinNormalSpeed ? body.Restitution * (-initVn0) : 0f;
                float target1 = active1 && -initVn1 > BounceMinNormalSpeed ? body.Restitution * (-initVn1) : 0f;

                float accJn0 = 0f;
                float accJn1 = 0f;
                int pgsIters = active0 && active1 ? 4 : 1;
                for (int iter = 0; iter < pgsIters; ++iter)
                {
                    if (active0)
                    {
                        float vn0 = (body.LinearVelocity + body.AngularVelocity.Cross(r0)).Dot(n0);
                        float dJn = (target0 - vn0) / invEff0;
                        float nextJn = MathF.Max(0f, accJn0 + dJn);
                        float applied = nextJn - accJn0;
                        accJn0 = nextJn;
                        if (applied != 0f)
                        {
                            body.LinearVelocity += n0 * (applied * body.InvMass);
                            body.AngularVelocity += r0CrossN * (applied * body.InvInertia);
                        }
                    }

                    if (active1)
                    {
                        float vn1 = (body.LinearVelocity + body.AngularVelocity.Cross(r1)).Dot(n1);
                        float dJn = (target1 - vn1) / invEff1;
                        float nextJn = MathF.Max(0f, accJn1 + dJn);
                        float applied = nextJn - accJn1;
                        accJn1 = nextJn;
                        if (applied != 0f)
                        {
                            body.LinearVelocity += n1 * (applied * body.InvMass);
                            body.AngularVelocity += r1CrossN * (applied * body.InvInertia);
                        }
                    }
                }

                // Apply passive Coulomb friction impulses at active contacts.
                if (active0 && accJn0 > 0f)
                {
                    ApplyContactFriction(ref body, r0, n0, accJn0);
                }

                if (active1 && accJn1 > 0f)
                {
                    ApplyContactFriction(ref body, r1, n1, accJn1);
                }

                // Purely dissipative Coulomb sliding friction + rolling resistance so bones hold
                // on natural slopes (up to ~30 deg) instead of creeping downhill forever.
                float vnAfter = body.LinearVelocity.Dot(avgNormal);
                Vector3 vTan = body.LinearVelocity - (avgNormal * vnAfter);
                vTan *= slideDecay;
                float vtLen = vTan.Length();
                float coulombStop = GroundCoulombSlideDecel * MathF.Max(avgNormal.Y, 0.25f) * subDt;
                vTan = vtLen > coulombStop && vtLen > 1e-5f
                    ? vTan * ((vtLen - coulombStop) / vtLen)
                    : Vector3.Zero;
                body.LinearVelocity = (vnAfter > 0f ? avgNormal * vnAfter : Vector3.Zero) + vTan;

                body.AngularVelocity *= angDecay;
                float spinLen = body.AngularVelocity.Length();
                float rollStop = GroundRollingAngularDecel * subDt;
                if (spinLen <= rollStop || spinLen <= 1e-5f)
                {
                    body.AngularVelocity = Vector3.Zero;
                }
                else
                {
                    float nextSpin = MathF.Min(spinLen - rollStop, MaxAngularSpeed);
                    body.AngularVelocity *= nextSpin / spinLen;
                }

                if (body.LinearVelocity.LengthSquared() <= SleepSpeedSq &&
                    body.AngularVelocity.LengthSquared() <= SleepAngularSpeedSq)
                {
                    if (++body.SleepSteps >= SleepStepsRequired)
                    {
                        body.LinearVelocity = Vector3.Zero;
                        body.AngularVelocity = Vector3.Zero;
                        body.Asleep = true;
                    }
                }
                else
                {
                    body.SleepSteps = 0;
                }
            }
        }

        // Sync world joint positions from integrated COM positions before writing model-space poses.
        for (int a = 0; a < _activeCount; ++a)
        {
            ref BoneBody body = ref _bodies[_activeIndices[a]];
            body.Position = body.ComPosition - (body.Rotation * body.LocalCom);
        }

        WriteModelPoses(entityWorld, guestWorldBones, guestValidCount);
    }

    private void WriteReassemblyPoses(
        Transform3D entityWorld,
        ReadOnlySpan<Vector3> guestWorldBones,
        int guestValidCount)
    {
        float t = ReassemblyBlend;
        // Cubic ease-in-out: smooth magnetic pull-in followed by a crisp lock into the rising pose.
        float ease = t < 0.5f
            ? 4f * t * t * t
            : 1f - (MathF.Pow((-2f * t) + 2f, 3f) * 0.5f);
        float arcSin = MathF.Sin(MathF.PI * t);

        Transform3D invEntity = entityWorld.AffineInverse();
        Basis invEntityBasis = invEntity.Basis;
        Vector3 entityOrigin = entityWorld.Origin;

        for (int m = 0; m < BoneCount; ++m)
        {
            ref BoneBody body = ref _bodies[m];
            if (body.IsRootWrapper)
            {
                _modelPoses[m] = Transform3D.Identity;
                continue;
            }

            if (!body.IsSimulated)
            {
                int parent = _parents[m];
                _modelPoses[m] = parent >= 0 && parent < m
                    ? _modelPoses[parent] * _localRest[m]
                    : _restGlobals[m];
                Transform3D worldFollower = entityWorld * _modelPoses[m];
                body.Position = worldFollower.Origin;
                body.Rotation = worldFollower.Basis.Orthonormalized().GetRotationQuaternion();
                body.ComPosition = body.Position;
                continue;
            }

            Transform3D targetWorld = entityWorld * _restGlobals[m];
            Vector3 targetJoint = targetWorld.Origin;
            if (guestValidCount > 0 &&
                m < guestWorldBones.Length &&
                guestWorldBones[m].IsFinite() &&
                guestWorldBones[m].DistanceTo(entityOrigin) <= 2.5f)
            {
                targetJoint = guestWorldBones[m];
            }

            Quaternion targetRot = targetWorld.Basis.Orthonormalized().GetRotationQuaternion();
            Vector3 startJoint = _reassemblyStartJoint[m];
            Quaternion startRot = _reassemblyStartRot[m];

            float dx = targetJoint.X - startJoint.X;
            float dz = targetJoint.Z - startJoint.Z;
            float horizDist = MathF.Sqrt((dx * dx) + (dz * dz));
            float arcLift = arcSin * MathF.Min(0.45f, horizDist * 0.22f);

            Vector3 worldJoint = startJoint.Lerp(targetJoint, ease) + (Vector3.Up * arcLift);
            Quaternion worldRot = startRot.Slerp(targetRot, ease).Normalized();

            body.Position = worldJoint;
            body.Rotation = worldRot;
            body.ComPosition = worldJoint + (worldRot * body.LocalCom);

            Basis worldBasis = new Basis(worldRot);
            _modelPoses[m] = new Transform3D(invEntityBasis * worldBasis, invEntity * worldJoint);
        }
    }

    private void WriteModelPoses(
        Transform3D entityWorld,
        ReadOnlySpan<Vector3> guestWorldBones = default,
        int guestValidCount = 0)
    {
        if (IsReassembling)
        {
            WriteReassemblyPoses(entityWorld, guestWorldBones, guestValidCount);
            return;
        }

        Transform3D invEntity = entityWorld.AffineInverse();
        Basis invEntityBasis = invEntity.Basis;
        for (int m = 0; m < BoneCount; ++m)
        {
            ref BoneBody body = ref _bodies[m];
            if (body.IsRootWrapper)
            {
                _modelPoses[m] = Transform3D.Identity;
                continue;
            }

            if (!body.IsSimulated)
            {
                int parent = _parents[m];
                _modelPoses[m] = parent >= 0 && parent < m
                    ? _modelPoses[parent] * _localRest[m]
                    : _restGlobals[m];
                Transform3D worldFollower = entityWorld * _modelPoses[m];
                body.Position = worldFollower.Origin;
                body.Rotation = worldFollower.Basis.Orthonormalized().GetRotationQuaternion();
                body.ComPosition = body.Position;
                continue;
            }

            Vector3 worldPos = body.Position;
            Basis worldBasis = new Basis(body.Rotation);
            _modelPoses[m] = new Transform3D(invEntityBasis * worldBasis, invEntity * worldPos);
        }
    }

    private void EnsureCapacity(int count)
    {
        if (_bodies.Length >= count)
        {
            return;
        }

        _bodies = new BoneBody[count];
        _modelPoses = new Transform3D[count];
        _restGlobals = new Transform3D[count];
        _localRest = new Transform3D[count];
        _parents = new int[count];
        _activeIndices = new int[count];
        _reassemblyStartJoint = new Vector3[count];
        _reassemblyStartRot = new Quaternion[count];
    }

    private static void ApplyContactFriction(ref BoneBody body, Vector3 r, Vector3 normal, float jn)
    {
        Vector3 vAfter = body.LinearVelocity + body.AngularVelocity.Cross(r);
        Vector3 vTan = vAfter - (normal * vAfter.Dot(normal));
        float vtLen = vTan.Length();
        if (vtLen <= 1e-5f)
        {
            return;
        }

        Vector3 tDir = vTan / vtLen;
        Vector3 rCrossT = r.Cross(tDir);
        float invEffT = body.InvMass + (rCrossT.LengthSquared() * body.InvInertia);
        float jt = MathF.Min(vtLen / MathF.Max(invEffT, 1e-5f), ImpactCoulombMu * jn);

        body.LinearVelocity -= tDir * (jt * body.InvMass);
        body.AngularVelocity -= rCrossT * (jt * body.InvInertia);
    }

    private static void ResolveFloorAt(
        Vector3 worldPos,
        Vector3 entityOrigin,
        float fallbackFloorY,
        bool originOnElevatedSlab,
        TrySampleGroundContact? sampleGround,
        out float floorY,
        out Vector3 floorNormal)
    {
        floorY = fallbackFloorY;
        floorNormal = Vector3.Up;

        if (sampleGround is null || !sampleGround(worldPos, out Vector3 groundPos, out Vector3 groundNormal))
        {
            return;
        }

        float dx = worldPos.X - entityOrigin.X;
        float dz = worldPos.Z - entityOrigin.Z;
        bool onCryptSlab = originOnElevatedSlab &&
            ((dx * dx) + (dz * dz)) <= CryptSlabRadiusSq &&
            fallbackFloorY > groundPos.Y + 0.15f;
        if (onCryptSlab)
        {
            floorY = fallbackFloorY;
            floorNormal = Vector3.Up;
            return;
        }

        floorY = groundPos.Y;
        floorNormal = groundNormal.IsFinite() && groundNormal.LengthSquared() > 1e-4f
            ? groundNormal.Normalized()
            : Vector3.Up;
    }

    /// <summary>Computes the closest points between two 3D line segments [p1,q1] and [p2,q2].</summary>
    private static void ClosestPointsOnSegments(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = d1.Dot(d1);
        float e = d2.Dot(d2);
        float f = d2.Dot(r);

        float s;
        float t;
        if (a <= 1e-8f && e <= 1e-8f)
        {
            c1 = p1;
            c2 = p2;
            return;
        }

        if (a <= 1e-8f)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = d1.Dot(r);
            if (e <= 1e-8f)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = d1.Dot(d2);
                float denom = (a * e) - (b * b);
                s = denom > 1e-8f ? Math.Clamp(((b * f) - (c * e)) / denom, 0f, 1f) : 0f;
                t = ((b * s) + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }

        c1 = p1 + (d1 * s);
        c2 = p2 + (d2 * t);
    }

    private static float ComputeCapsuleInertia(float mass, float radius, float halfLength)
    {
        float inertia = mass * ((0.4f * radius * radius) + ((1f / 3f) * halfLength * halfLength) + (0.5f * radius * halfLength));
        return MathF.Max(inertia, 0.002f * mass);
    }

    /// <summary>Estimates collision radius by bone role so skulls/torsos sit higher than digits.</summary>
    public static float GuessBoneRadius(string boneName)
    {
        if (string.IsNullOrEmpty(boneName))
        {
            return 0.07f;
        }

        if (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Skull", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Rib", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Pelvis", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Hip", StringComparison.OrdinalIgnoreCase))
        {
            return 0.11f;
        }

        if (boneName.Contains("Spine", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Thigh", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("UpperArm", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Clavicle", StringComparison.OrdinalIgnoreCase))
        {
            return 0.085f;
        }

        return 0.06f;
    }

    private static float GuessBoneMass(string boneName)
    {
        if (string.IsNullOrEmpty(boneName))
        {
            return 0.9f;
        }

        if (boneName.Contains("Rib", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Pelvis", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Hip", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Spine", StringComparison.OrdinalIgnoreCase))
        {
            return 2.2f;
        }

        if (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Skull", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Femur", StringComparison.OrdinalIgnoreCase) ||
            boneName.Contains("Thigh", StringComparison.OrdinalIgnoreCase))
        {
            return 1.3f;
        }

        return 0.7f;
    }

    /// <summary>Estimates realistic dry-bone restitution (low clatter bounce, never rubber-ball jumping).</summary>
    public static float GuessBoneRestitution(string boneName, float unitNoise)
    {
        float jitter = unitNoise * 0.05f;
        if (!string.IsNullOrEmpty(boneName) &&
            (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Skull", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Femur", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Thigh", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Tibia", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Shin", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Calf", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Humerus", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Forearm", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Ulna", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("UpperArm", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.19f + jitter;
        }

        if (!string.IsNullOrEmpty(boneName) &&
            (boneName.Contains("Pelvis", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Hip", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Rib", StringComparison.OrdinalIgnoreCase) ||
             boneName.Contains("Spine", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.14f + jitter;
        }

        return 0.16f + jitter;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x == 0u ? 1u : x;
    }

    private static float NextZeroToOne(ref uint state)
    {
        state = Hash(state);
        return (state & 0xFFFFu) / 65535.0f;
    }

    private static float NextUnit(ref uint state) => (NextZeroToOne(ref state) * 2.0f) - 1.0f;
}
