using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Physics;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class Engine_DisarticulatedRigTests
{
    private static readonly string[] CrumbleNames =
    {
        "0xA1B2C3D4",
        "Skull",
        "RibCage",
        "Pelvis",
        "Femur_L",
        "Femur_R",
        "Humerus_L",
        "Humerus_R",
    };

    private static readonly int[] CrumbleParents = { -1, 0, 0, 0, 0, 0, 0, 0 };

    private static Transform3D[] MakeRestGlobals(int count)
    {
        var rest = new Transform3D[count];
        for (int i = 0; i < count; ++i)
        {
            rest[i] = i == 0
                ? Transform3D.Identity
                : new Transform3D(Basis.Identity, new Vector3((i - 4) * 0.15f, 1.0f + (i * 0.05f), 0f));
        }

        return rest;
    }

    [TestCase]
    public void SeedsFromLiveBonesWithZeroDeltaAndFable3HashedRootWrapper()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(12f, 2f, -8f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var live = new Dictionary<string, (Vector3 Pos, Quaternion Rot)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skull"] = (new Vector3(12.2f, 3.75f, -7.8f), Quaternion.Identity),
            ["RibCage"] = (new Vector3(12.0f, 3.25f, -8.1f), Quaternion.Identity),
            ["Femur_L"] = (new Vector3(11.7f, 2.45f, -8.0f), Quaternion.Identity),
        };

        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, live, Vector3.Zero, 0x12345678u);

        AssertThat(rig.Bodies[0].IsRootWrapper).IsTrue();
        AssertThat(rig.ModelPoses[0]).IsEqual(Transform3D.Identity);
        AssertThat(rig.Bodies[1].IsRootWrapper).IsFalse();
        AssertThat(rig.Bodies[1].Position.DistanceTo(live["Skull"].Pos)).IsLess(1e-4f);
        AssertThat(rig.Bodies[2].Position.DistanceTo(live["RibCage"].Pos)).IsLess(1e-4f);
        AssertThat(rig.Bodies[4].Position.DistanceTo(live["Femur_L"].Pos)).IsLess(1e-4f);
    }

    [TestCase]
    public void IntegratesGravityAndBouncesOffSlopedGroundWithoutPenetration()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(0f, 3.0f, 0f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 42u);

        Engine_DisarticulatedRig.TrySampleGroundContact slopeGround = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            float y = 2.0f + (0.2f * pos.X);
            contact = new Vector3(pos.X, y, pos.Z);
            normal = new Vector3(-0.2f, 1.0f, 0.0f).Normalized();
            return true;
        };

        for (int step = 0; step < 180; ++step)
        {
            float dt = (step % 5 == 0) ? (1f / 30f) : (1f / 120f);
            rig.Step(dt, entityWorld, slopeGround, default, 0);
            for (int m = 1; m < rig.BoneCount; ++m)
            {
                Engine_DisarticulatedRig.BoneBody b = rig.Bodies[m];
                float floorY = 2.0f + (0.2f * b.Position.X);
                AssertThat(b.Position.Y).IsGreaterEqual(floorY + b.Radius - 1e-3f);
            }
        }
    }

    [TestCase]
    public void RespectsElevatedCryptSlabAndNullIndoorFloor()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(0f, 1.5f, 0f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 7u);

        // Outdoor terrain lies 1.5 m underneath an elevated stone crypt floor.
        Engine_DisarticulatedRig.TrySampleGroundContact underSlab = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0.0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        var slabGuest = new Vector3[CrumbleNames.Length];
        for (int i = 0; i < slabGuest.Length; ++i)
        {
            slabGuest[i] = new Vector3(0f, 1.5f, 0f);
        }

        for (int step = 0; step < 240; ++step)
        {
            rig.Step(1f / 60f, entityWorld, underSlab, slabGuest, slabGuest.Length);
        }

        for (int m = 1; m < rig.BoneCount; ++m)
        {
            float dx = rig.Bodies[m].Position.X;
            float dz = rig.Bodies[m].Position.Z;
            if ((dx * dx) + (dz * dz) < 1.4f * 1.4f)
            {
                AssertThat(rig.Bodies[m].Position.Y).IsGreaterEqual(1.5f + rig.Bodies[m].Radius - 1e-3f);
            }
        }

        // Indoor dungeon: sampleGround == null, guest floor guides landing height.
        var indoorEntity = new Transform3D(Basis.Identity, new Vector3(5f, 3.2f, 5f));
        var indoorRig = new Engine_DisarticulatedRig();
        indoorRig.Initialize(indoorEntity, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 99u);
        var indoorGuest = new Vector3[CrumbleNames.Length];
        for (int i = 0; i < indoorGuest.Length; ++i)
        {
            indoorGuest[i] = new Vector3(5f, 3.2f, 5f);
        }

        for (int step = 0; step < 180; ++step)
        {
            indoorRig.Step(1f / 60f, indoorEntity, null, indoorGuest, indoorGuest.Length);
        }

        for (int m = 1; m < indoorRig.BoneCount; ++m)
        {
            AssertThat(indoorRig.Bodies[m].Position.Y).IsGreaterEqual(3.2f + indoorRig.Bodies[m].Radius - 1e-3f);
        }
    }

    [TestCase]
    public void ScattersWithoutFourMeterCapAndSettlesToSleep()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        // Fast inherited horizontal velocity pushes scattered bones well beyond 4 m.
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, new Vector3(8f, 0f, 4f), 555u);

        Engine_DisarticulatedRig.TrySampleGroundContact flat = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        for (int step = 0; step < 480; ++step)
        {
            rig.Step(1f / 60f, entityWorld, flat, default, 0);
        }

        float maxHoriz = 0f;
        for (int m = 1; m < rig.BoneCount; ++m)
        {
            Vector3 p = rig.Bodies[m].Position;
            maxHoriz = MathF.Max(maxHoriz, MathF.Sqrt((p.X * p.X) + (p.Z * p.Z)));
            AssertThat(rig.Bodies[m].Asleep).IsTrue();
        }

        AssertThat(maxHoriz).IsGreater(4.0f);
    }

    [TestCase]
    public void ReassemblyBlendsAlignedBonesEvenWithUnmappedRootSlot()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(2f, 0f, 2f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 777u);

        Engine_DisarticulatedRig.TrySampleGroundContact flat = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        // Scatter outward (> 0.7s, peak spread > 0.85m).
        var spreadGuest = new Vector3[CrumbleNames.Length];
        spreadGuest[0] = new Vector3(float.NaN, float.NaN, float.NaN);
        for (int i = 1; i < spreadGuest.Length; ++i)
        {
            spreadGuest[i] = new Vector3(2f + ((i % 2 == 0 ? 1f : -1f) * 1.4f), 0.1f, 2f + (i * 0.25f));
        }
        for (int step = 0; step < 60; ++step)
        {
            rig.Step(1f / 60f, entityWorld, flat, spreadGuest, CrumbleNames.Length - 1);
        }
        AssertThat(rig.IsReassembling).IsFalse();

        // Guest pulls bones tightly back toward entity origin while slot 0 remains unmapped NaN.
        var gatheredGuest = new Vector3[CrumbleNames.Length];
        gatheredGuest[0] = new Vector3(float.NaN, float.NaN, float.NaN);
        for (int i = 1; i < gatheredGuest.Length; ++i)
        {
            gatheredGuest[i] = new Vector3(2f + (i * 0.01f), 0.8f + (i * 0.02f), 2f);
        }

        rig.Step(1f / 60f, entityWorld, flat, gatheredGuest, CrumbleNames.Length - 1);
        AssertThat(rig.IsReassembling).IsTrue();

        for (int step = 0; step < 20; ++step)
        {
            rig.Step(1f / 60f, entityWorld, flat, gatheredGuest, CrumbleNames.Length - 1);
        }

        AssertThat(rig.ReassemblyFinished).IsTrue();
        AssertThat(rig.ReassemblyBlend).IsEqualApprox(1.0f, 1e-4f);
        for (int m = 1; m < CrumbleNames.Length; ++m)
        {
            Vector3 finalWorld = (entityWorld * rig.ModelPoses[m]).Origin;
            AssertThat(finalWorld.DistanceTo(gatheredGuest[m])).IsLess(1e-3f);
        }
    }

    [TestCase]
    public void ReboundsUpwardOnGroundImpactBeforeSleeping()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(0f, 1.8f, 0f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 314159u);

        Engine_DisarticulatedRig.TrySampleGroundContact flat = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        var sawUpwardRebound = new bool[rig.BoneCount];
        var wasDescending = new bool[rig.BoneCount];
        for (int step = 0; step < 720; ++step)
        {
            rig.Step(1f / 120f, entityWorld, flat, default, 0);
            for (int m = 1; m < rig.BoneCount; ++m)
            {
                float vy = rig.Bodies[m].LinearVelocity.Y;
                if (vy < -0.8f)
                {
                    wasDescending[m] = true;
                }
                else if (wasDescending[m] && vy > 0.35f)
                {
                    sawUpwardRebound[m] = true;
                }
            }
        }

        int reboundingBones = 0;
        for (int m = 1; m < rig.BoneCount; ++m)
        {
            if (sawUpwardRebound[m])
            {
                ++reboundingBones;
            }
        }

        AssertThat(reboundingBones).IsGreaterEqual(4);
        AssertThat(rig.AllAsleep).IsTrue();
    }

    [TestCase]
    public void SeparatesOverlappingBonesWithoutFloorPenetrationAcrossFrameRates()
    {
        Engine_DisarticulatedRig.TrySampleGroundContact flat = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        var entityWorld = new Transform3D(Basis.Identity, Vector3.Zero);
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);

        var rig60 = new Engine_DisarticulatedRig();
        rig60.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, new Vector3(1.5f, 0f, 0f), 98765u);
        for (int s = 0; s < 240; ++s)
        {
            rig60.Step(1f / 60f, entityWorld, flat, default, 0);
        }

        var rig144 = new Engine_DisarticulatedRig();
        rig144.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, new Vector3(1.5f, 0f, 0f), 98765u);
        for (int s = 0; s < 576; ++s)
        {
            rig144.Step(1f / 144f, entityWorld, flat, default, 0);
        }

        AssertThat(rig60.AllAsleep).IsTrue();
        AssertThat(rig144.AllAsleep).IsTrue();

        for (int m = 1; m < rig60.BoneCount; ++m)
        {
            AssertThat(rig60.Bodies[m].Position.Y).IsGreaterEqual(rig60.Bodies[m].Radius - 1e-3f);
            AssertThat(rig144.Bodies[m].Position.Y).IsGreaterEqual(rig144.Bodies[m].Radius - 1e-3f);
            AssertThat(rig60.Bodies[m].Position.DistanceTo(rig144.Bodies[m].Position)).IsLess(0.65f);
        }
    }

    [TestCase]
    public void DissipatesMechanicalEnergyOnFlatGroundWithoutArtificialJumping()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 2026u);

        Engine_DisarticulatedRig.TrySampleGroundContact flat = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        static float TotalEnergy(Engine_DisarticulatedRig r)
        {
            float e = 0f;
            for (int m = 1; m < r.BoneCount; ++m)
            {
                Engine_DisarticulatedRig.BoneBody b = r.Bodies[m];
                if (!b.IsSimulated)
                {
                    continue;
                }

                float inertia = 1f / b.InvInertia;
                float ke = (0.5f * b.Mass * b.LinearVelocity.LengthSquared()) +
                    (0.5f * inertia * b.AngularVelocity.LengthSquared());
                float pe = b.Mass * 9.81f * MathF.Max(0f, b.ComPosition.Y - b.Radius);
                e += ke + pe;
            }

            return e;
        }

        float initialEnergy = TotalEnergy(rig);
        float prevEnergy = initialEnergy;
        for (int step = 0; step < 300; ++step)
        {
            rig.Step(1f / 60f, entityWorld, flat, default, 0);
            float curEnergy = TotalEnergy(rig);
            // Allow tiny discrete penetration-push jitter (< 0.15 J across 7 bones) while prohibiting energy-injecting jumps.
            AssertThat(curEnergy).IsLessEqual(prevEnergy + 0.15f);
            AssertThat(curEnergy).IsLessEqual(initialEnergy + 0.25f);
            prevEnergy = curEnergy;
        }

        AssertThat(rig.AllAsleep).IsTrue();
        AssertThat(prevEnergy).IsLess(0.1f);
    }

    [TestCase]
    public void IndoorFloorIgnoresFallingGuestHavokBones()
    {
        var indoorEntity = new Transform3D(Basis.Identity, new Vector3(5f, 3.2f, 5f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var indoorRig = new Engine_DisarticulatedRig();
        indoorRig.Initialize(indoorEntity, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 101u);

        // Simulate guest Havok ragdoll bones falling 3.5m to 60m through unmounted guest floor collision.
        var fallingGuest = new Vector3[CrumbleNames.Length];
        for (int i = 0; i < fallingGuest.Length; ++i)
        {
            fallingGuest[i] = new Vector3(5f + (i * 0.5f), i % 2 == 0 ? -0.3f : -55.0f, 5f);
        }

        for (int step = 0; step < 240; ++step)
        {
            indoorRig.Step(1f / 60f, indoorEntity, null, fallingGuest, fallingGuest.Length);
        }

        AssertThat(indoorRig.AllAsleep).IsTrue();
        for (int m = 1; m < indoorRig.BoneCount; ++m)
        {
            AssertThat(indoorRig.Bodies[m].Position.Y).IsGreaterEqual(3.2f + indoorRig.Bodies[m].Radius - 1e-3f);
        }
    }

    [TestCase]
    public void StandingKillScattersWidelyAndStatedBoundsEncloseAllBonesWithHysteresis()
    {
        var entityWorld = new Transform3D(Basis.Identity, new Vector3(4f, 0f, -6f));
        Transform3D[] rest = MakeRestGlobals(CrumbleNames.Length);
        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(entityWorld, CrumbleNames, rest, CrumbleParents, null, Vector3.Zero, 4242u);

        Engine_DisarticulatedRig.TrySampleGroundContact flat = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            contact = new Vector3(pos.X, 0f, pos.Z);
            normal = Vector3.Up;
            return true;
        };

        AssertThat(rig.TryUpdateStatedBounds(entityWorld, force: true, out Aabb bounds)).IsTrue();
        int writes = 1;
        Transform3D invEntity = entityWorld.AffineInverse();

        for (int step = 0; step < 300; ++step)
        {
            rig.Step(1f / 60f, entityWorld, flat, default, 0);
            if (rig.TryUpdateStatedBounds(entityWorld, force: false, out Aabb updated))
            {
                bounds = updated;
                ++writes;
            }

            for (int m = 1; m < rig.BoneCount; ++m)
            {
                Vector3 localCom = invEntity * rig.Bodies[m].ComPosition;
                AssertThat(bounds.HasPoint(localCom)).IsTrue();
            }
        }

        AssertThat(rig.AllAsleep).IsTrue();
        AssertThat(writes).IsLess(12);

        float maxHoriz = 0f;
        for (int m = 1; m < rig.BoneCount; ++m)
        {
            Vector3 d = rig.Bodies[m].ComPosition - entityWorld.Origin;
            maxHoriz = MathF.Max(maxHoriz, MathF.Sqrt((d.X * d.X) + (d.Z * d.Z)));
        }

        AssertThat(maxHoriz).IsGreater(1.4f);
    }
}
