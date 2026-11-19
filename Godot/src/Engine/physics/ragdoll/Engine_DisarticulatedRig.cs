using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Physics;

/// <summary>
/// Host-driven rigid-body scatter, tumble, bounce, and reassembly simulation for disarticulated
/// ("crumbled") skeletons across titles (Fable 2 and Fable 3).
/// </summary>
/// <remarks>
/// Replaces low-rate (15/30 Hz) guest Havok ragdoll stepping on crumbled bone-pile models so
/// detached bones explode seamlessly from their exact last live animated transforms at display
/// refresh rate without any 4 m reach restriction, colliding against both landscape heightfields
/// (<see cref="Refii.Level.Engine_LandscapeGround"/>) and indoor/crypt slab floors.
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
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public float Radius;
        public bool IsRootWrapper;
        public bool Asleep;
        public int SleepSteps;
    }

    private const float Gravity = -9.81f;
    private const float AirDragPerSecond = 0.35f;
    private const float Restitution = 0.34f;
    private const float SlideDamping = 0.72f;
    private const float AngularRestitutionDrag = 0.65f;
    private const float SleepSpeedSq = 0.045f * 0.045f;
    private const int SleepStepsRequired = 10;
    private const float CryptSlabRadiusSq = 1.45f * 1.45f;
    private const float ReassemblyDuration = 0.25f;

    private BoneBody[] _bodies = Array.Empty<BoneBody>();
    private Transform3D[] _modelPoses = Array.Empty<Transform3D>();
    private float _elapsed;
    private float _peakSpread;
    private float _reassemblyElapsed;
    private bool _hasOriginGround;
    private float _originGroundY;
    private bool _originGroundSampled;
    private ulong _lastSteppedFrame = ulong.MaxValue;

    /// <summary>Simulated bone count in model order.</summary>
    public int BoneCount { get; private set; }

    /// <summary>Elapsed simulation time in seconds since shatter initialization.</summary>
    public float Elapsed => _elapsed;

    /// <summary>True when guest bones have pulled back tightly and the host is blending back.</summary>
    public bool IsReassembling { get; private set; }

    /// <summary>Normalised reassembly blend in [0, 1].</summary>
    public float ReassemblyBlend { get; private set; }

    /// <summary>True once reassembly blend reached 1.0 and guest/animator can resume ownership.</summary>
    public bool ReassemblyFinished { get; private set; }

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
    /// available (zero visual pop), and assigns deterministic radial scatter and spin impulses.
    /// </summary>
    public void Initialize(
        Transform3D entityWorld,
        IReadOnlyList<string> boneNames,
        ReadOnlySpan<Transform3D> restGlobals,
        ReadOnlySpan<int> parents,
        IReadOnlyDictionary<string, (Vector3 Pos, Quaternion Rot)>? liveBones,
        Vector3 inheritedVelocity,
        uint seed)
    {
        int count = boneNames.Count;
        BoneCount = count;
        if (_bodies.Length < count)
        {
            _bodies = new BoneBody[count];
            _modelPoses = new Transform3D[count];
        }

        _elapsed = 0f;
        _peakSpread = 0f;
        _reassemblyElapsed = 0f;
        IsReassembling = false;
        ReassemblyBlend = 0f;
        ReassemblyFinished = false;
        _originGroundSampled = false;
        _hasOriginGround = false;
        _originGroundY = entityWorld.Origin.Y;
        _lastSteppedFrame = ulong.MaxValue;

        Quaternion entityRot = entityWorld.Basis.Orthonormalized().GetRotationQuaternion();
        Vector3 centroid = Vector3.Zero;
        int dynamicCount = 0;
        int directChildrenOfZero = 0;
        for (int k = 1; k < count; ++k)
        {
            if (parents[k] == 0)
            {
                ++directChildrenOfZero;
            }
        }

        for (int m = 0; m < count; ++m)
        {
            bool rootWrapper = parents[m] < 0 && m == 0 &&
                (directChildrenOfZero >= 6 ||
                 boneNames[m].Contains("Locator", StringComparison.OrdinalIgnoreCase) ||
                 boneNames[m].Contains("Rig", StringComparison.OrdinalIgnoreCase) ||
                 boneNames[m].Contains("Root", StringComparison.OrdinalIgnoreCase));

            Vector3 pos;
            Quaternion rot;
            if (!rootWrapper && liveBones is not null && liveBones.TryGetValue(boneNames[m], out var matched))
            {
                pos = matched.Pos.IsFinite() ? matched.Pos : (entityWorld * restGlobals[m].Origin);
                rot = matched.Rot.IsFinite() && matched.Rot.LengthSquared() > 1e-6f
                    ? matched.Rot.Normalized()
                    : entityRot;
            }
            else
            {
                Transform3D worldRest = entityWorld * (m < restGlobals.Length ? restGlobals[m] : Transform3D.Identity);
                pos = worldRest.Origin.IsFinite() ? worldRest.Origin : entityWorld.Origin;
                rot = worldRest.Basis.Orthonormalized().GetRotationQuaternion();
            }

            // Ensure crumbled bones start slightly above entity origin if matched rest is at origin.
            float radius = GuessBoneRadius(boneNames[m]);
            _bodies[m] = new BoneBody
            {
                Position = pos,
                Rotation = rot,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
                Radius = radius,
                IsRootWrapper = rootWrapper,
                Asleep = rootWrapper,
                SleepSteps = 0,
            };

            if (!rootWrapper)
            {
                centroid += pos;
                ++dynamicCount;
            }
        }

        if (dynamicCount > 0)
        {
            centroid /= dynamicCount;
        }
        else
        {
            centroid = entityWorld.Origin + Vector3.Up * 0.8f;
        }

        Vector3 inherited = inheritedVelocity.IsFinite() ? inheritedVelocity * 0.45f : Vector3.Zero;
        for (int m = 0; m < count; ++m)
        {
            if (_bodies[m].IsRootWrapper)
            {
                continue;
            }

            uint rng = Hash(seed ^ (uint)(m * 0x9E3779B9u + 17));
            float rx = NextUnit(ref rng);
            float rz = NextUnit(ref rng);
            float ry = NextZeroToOne(ref rng);

            Vector3 radial = _bodies[m].Position - centroid;
            radial.Y = 0f;
            if (radial.LengthSquared() < 1e-4f)
            {
                radial = new Vector3(rx, 0f, rz);
            }

            if (radial.LengthSquared() > 1e-6f)
            {
                radial = radial.Normalized();
            }
            else
            {
                radial = Vector3.Forward;
            }

            float outwardSpeed = 1.9f + (NextZeroToOne(ref rng) * 2.1f);
            float upwardSpeed = 1.6f + (ry * 2.5f);
            Vector3 lateralJitter = new Vector3(rx, 0f, rz) * 0.85f;
            _bodies[m].LinearVelocity = inherited + (radial * outwardSpeed) + lateralJitter + (Vector3.Up * upwardSpeed);

            Vector3 spinAxis = new Vector3(NextUnit(ref rng), NextUnit(ref rng), NextUnit(ref rng));
            if (spinAxis.LengthSquared() < 1e-4f)
            {
                spinAxis = Vector3.Right;
            }
            else
            {
                spinAxis = spinAxis.Normalized();
            }

            float spinRate = 4.5f + (NextZeroToOne(ref rng) * 7.5f);
            _bodies[m].AngularVelocity = spinAxis * spinRate;
        }

        WriteModelPoses(entityWorld);
    }

    /// <summary>
    /// Advances the disarticulated simulation by <paramref name="dt"/> seconds using hybrid
    /// terrain heightfield + indoor/crypt slab floor collision and writes updated model-space poses.
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
            WriteModelPoses(entityWorld);
            return;
        }

        float clampedDt = Math.Clamp(dt, 0f, 0.05f);
        _elapsed += clampedDt;

        if (!_originGroundSampled && sampleGround is not null)
        {
            _originGroundSampled = true;
            _hasOriginGround = sampleGround(entityWorld.Origin, out Vector3 ogPos, out _);
            _originGroundY = _hasOriginGround ? ogPos.Y : entityWorld.Origin.Y;
        }

        bool originOnElevatedSlab = _hasOriginGround && entityWorld.Origin.Y > _originGroundY + 0.18f;
        float guestFloorY = entityWorld.Origin.Y;
        float meanGuestSpread = 0f;
        if (guestValidCount > 0)
        {
            float minY = float.MaxValue;
            float spreadSum = 0f;
            int spreadSamples = 0;
            for (int i = 0; i < guestWorldBones.Length; ++i)
            {
                Vector3 gp = guestWorldBones[i];
                if (!gp.IsFinite())
                {
                    continue;
                }

                if (gp.Y < minY && gp.Y >= entityWorld.Origin.Y - 4f)
                {
                    minY = gp.Y;
                }

                float dx = gp.X - entityWorld.Origin.X;
                float dz = gp.Z - entityWorld.Origin.Z;
                spreadSum += MathF.Sqrt((dx * dx) + (dz * dz));
                ++spreadSamples;
            }

            if (minY < float.MaxValue)
            {
                guestFloorY = MathF.Min(entityWorld.Origin.Y, minY);
            }

            if (spreadSamples > 0)
            {
                meanGuestSpread = spreadSum / spreadSamples;
                _peakSpread = MathF.Max(_peakSpread, meanGuestSpread);
            }
        }

        if (!IsReassembling && _elapsed > 0.7f && guestValidCount >= 4 && _peakSpread > 0.85f && meanGuestSpread < 0.40f)
        {
            IsReassembling = true;
            _reassemblyElapsed = 0f;
        }

        if (IsReassembling)
        {
            _reassemblyElapsed += clampedDt;
            ReassemblyBlend = Math.Clamp(_reassemblyElapsed / ReassemblyDuration, 0f, 1f);
            if (ReassemblyBlend >= 1f)
            {
                ReassemblyFinished = true;
            }
        }

        int substeps = clampedDt > (1f / 45f) ? 2 : 1;
        float subDt = clampedDt / substeps;
        float drag = MathF.Exp(-AirDragPerSecond * subDt);

        for (int step = 0; step < substeps; ++step)
        {
            for (int m = 0; m < BoneCount; ++m)
            {
                ref BoneBody body = ref _bodies[m];
                if (body.IsRootWrapper || body.Asleep)
                {
                    continue;
                }

                body.LinearVelocity.Y += Gravity * subDt;
                body.LinearVelocity *= drag;
                body.Position += body.LinearVelocity * subDt;

                float spinMag = body.AngularVelocity.Length();
                if (spinMag > 1e-4f)
                {
                    Vector3 axis = body.AngularVelocity / spinMag;
                    var dq = new Quaternion(axis, spinMag * subDt);
                    body.Rotation = (dq * body.Rotation).Normalized();
                }

                float floorY = guestFloorY;
                Vector3 floorNormal = Vector3.Up;
                if (sampleGround is not null && sampleGround(body.Position, out Vector3 groundPos, out Vector3 groundNormal))
                {
                    float dx = body.Position.X - entityWorld.Origin.X;
                    float dz = body.Position.Z - entityWorld.Origin.Z;
                    bool onCryptSlab = originOnElevatedSlab && ((dx * dx) + (dz * dz)) <= CryptSlabRadiusSq &&
                        entityWorld.Origin.Y > groundPos.Y + 0.15f;
                    if (onCryptSlab)
                    {
                        floorY = entityWorld.Origin.Y;
                        floorNormal = Vector3.Up;
                    }
                    else
                    {
                        floorY = groundPos.Y;
                        floorNormal = groundNormal.IsFinite() && groundNormal.LengthSquared() > 1e-4f
                            ? groundNormal.Normalized()
                            : Vector3.Up;
                    }
                }

                float surfaceY = floorY + body.Radius;
                if (body.Position.Y <= surfaceY)
                {
                    body.Position.Y = surfaceY;
                    float normalVel = body.LinearVelocity.Dot(floorNormal);
                    if (normalVel < 0f)
                    {
                        if (normalVel < -0.55f)
                        {
                            body.LinearVelocity -= floorNormal * ((1f + Restitution) * normalVel);
                            body.AngularVelocity *= AngularRestitutionDrag;
                        }
                        else
                        {
                            body.LinearVelocity -= floorNormal * normalVel;
                        }

                        // Tangential slide damping while on the surface.
                        Vector3 tangent = body.LinearVelocity - (floorNormal * body.LinearVelocity.Dot(floorNormal));
                        body.LinearVelocity = (floorNormal * body.LinearVelocity.Dot(floorNormal)) + (tangent * SlideDamping);
                        body.AngularVelocity *= SlideDamping;
                    }

                    if (body.LinearVelocity.LengthSquared() <= SleepSpeedSq &&
                        body.AngularVelocity.LengthSquared() <= 0.16f)
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
                else
                {
                    body.SleepSteps = 0;
                }
            }
        }

        WriteModelPoses(entityWorld, guestWorldBones, guestValidCount);
    }

    private void WriteModelPoses(Transform3D entityWorld, ReadOnlySpan<Vector3> guestWorldBones = default, int guestValidCount = 0)
    {
        Transform3D invEntity = entityWorld.AffineInverse();
        Basis invEntityBasis = invEntity.Basis;
        for (int m = 0; m < BoneCount; ++m)
        {
            if (_bodies[m].IsRootWrapper)
            {
                _modelPoses[m] = Transform3D.Identity;
                continue;
            }

            Vector3 worldPos = _bodies[m].Position;
            if (IsReassembling && ReassemblyBlend > 0f && m < guestWorldBones.Length && guestWorldBones[m].IsFinite())
            {
                worldPos = worldPos.Lerp(guestWorldBones[m], ReassemblyBlend);
            }

            Basis worldBasis = new Basis(_bodies[m].Rotation);
            _modelPoses[m] = new Transform3D(invEntityBasis * worldBasis, invEntity * worldPos);
        }
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
