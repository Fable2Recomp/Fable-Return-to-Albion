using System;
using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.Models;
using Refii.Physics;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class Engine_MdlDisarticulationTests
{
    private static (string[] Names, int[] Parents, Transform3D[] RestGlobals, List<Engine_MdlDisarticulation.SurfaceSkinData> Surfaces)
        BuildSkeletonBaseFixture()
    {
        var names = new List<string>();
        var parents = new List<int>();
        var restGlobals = new List<Transform3D>();

        int AddBone(string name, int parent, Vector3 pos)
        {
            int idx = names.Count;
            names.Add(name);
            parents.Add(parent);
            restGlobals.Add(new Transform3D(Basis.Identity, pos));
            return idx;
        }

        int root = AddBone("CH_HeroMale_Morph_Body_Rig_Asset", -1, Vector3.Zero);
        int hip = AddBone("Shadow_Hip", root, new Vector3(0f, 1.00f, 0f));
        int spine = AddBone("Shadow_SpineControl", hip, new Vector3(0f, 1.12f, 0f));
        int spine1 = AddBone("Shadow_SpineControl1", spine, new Vector3(0f, 1.28f, 0f));
        int chest = AddBone("Shadow_Chest", spine1, new Vector3(0f, 1.45f, 0f)); // 0 vertices in Skeleton_Base.mdl
        int neck = AddBone("Shadow_Neck", chest, new Vector3(0f, 1.56f, 0f));
        int head = AddBone("Shadow_Head", neck, new Vector3(0f, 1.64f, 0f));

        int lClav = AddBone("Shadow_L_Clavicle", chest, new Vector3(0.06f, 1.50f, 0f));
        int lUpperArm = AddBone("Shadow_L_UpperArm", lClav, new Vector3(0.18f, 1.48f, 0f));
        int lForearm = AddBone("Shadow_L_Forearm", lUpperArm, new Vector3(0.46f, 1.48f, 0f));
        int lHand = AddBone("Shadow_L_Hand", lForearm, new Vector3(0.72f, 1.48f, 0f));

        int rClav = AddBone("Shadow_R_Clavicle", chest, new Vector3(-0.06f, 1.50f, 0f));
        int rUpperArm = AddBone("Shadow_R_UpperArm", rClav, new Vector3(-0.18f, 1.48f, 0f));
        int rForearm = AddBone("Shadow_R_Forearm", rUpperArm, new Vector3(-0.46f, 1.48f, 0f));
        int rHand = AddBone("Shadow_R_Hand", rForearm, new Vector3(-0.72f, 1.48f, 0f));

        int lThigh = AddBone("Shadow_L_Thigh", hip, new Vector3(0.11f, 1.04f, 0f));
        int lShin = AddBone("Shadow_L_Shin", lThigh, new Vector3(0.11f, 0.56f, 0f));
        int lHeel = AddBone("Shadow_L_Heel", lShin, new Vector3(0.11f, 0.09f, 0f));
        int lTip = AddBone("Shadow_L_Tip", lHeel, new Vector3(0.11f, 0.03f, -0.14f));

        int rThigh = AddBone("Shadow_R_Thigh", hip, new Vector3(-0.11f, 1.04f, 0f));
        int rShin = AddBone("Shadow_R_Shin", rThigh, new Vector3(-0.11f, 0.56f, 0f));
        int rHeel = AddBone("Shadow_R_Heel", rShin, new Vector3(-0.11f, 0.09f, 0f));
        int rTip = AddBone("Shadow_R_Tip", rHeel, new Vector3(-0.11f, 0.03f, -0.14f));

        // 30 finger phalanges (15 left, 15 right) matching Skeleton_Base.mdl.
        var fingerBones = new List<int>();
        string[] fingerNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
        for (int side = 0; side < 2; ++side)
        {
            int handIdx = side == 0 ? lHand : rHand;
            string prefix = side == 0 ? "Shadow_L_" : "Shadow_R_";
            float dir = side == 0 ? 1f : -1f;
            for (int f = 0; f < fingerNames.Length; ++f)
            {
                float zOff = (f - 2) * 0.018f;
                int p0 = AddBone($"{prefix}{fingerNames[f]}", handIdx, new Vector3(dir * 0.78f, 1.48f, zOff));
                int p1 = AddBone($"{prefix}{fingerNames[f]}1", p0, new Vector3(dir * 0.81f, 1.48f, zOff));
                int p2 = AddBone($"{prefix}{fingerNames[f]}2", p1, new Vector3(dir * 0.84f, 1.48f, zOff));
                fingerBones.Add(p0);
                fingerBones.Add(p1);
                fingerBones.Add(p2);
            }
        }

        // 12 empty dummy bones matching Skeleton_Base.mdl (65 bones total).
        for (int d = 0; d < 12; ++d)
        {
            AddBone($"FX_DummyObject_{d}", spine1, new Vector3(0f, 1.30f, 0f));
        }

        AssertThat(names.Count).IsEqual(65);

        // Build rigid single-bone box geometry for every populated anatomical bone (17 core + 2 tips + 30 fingers).
        var verts = new List<Vector3>();
        var bones = new List<int>();
        var weights = new List<float>();
        var indices = new List<int>();

        void AddBoneBox(int boneIdx, Vector3 minMesh, Vector3 maxMesh)
        {
            int start = verts.Count;
            Vector3[] corners =
            {
                new(minMesh.X, minMesh.Y, minMesh.Z),
                new(maxMesh.X, minMesh.Y, minMesh.Z),
                new(maxMesh.X, maxMesh.Y, minMesh.Z),
                new(minMesh.X, maxMesh.Y, minMesh.Z),
                new(minMesh.X, minMesh.Y, maxMesh.Z),
                new(maxMesh.X, minMesh.Y, maxMesh.Z),
                new(maxMesh.X, maxMesh.Y, maxMesh.Z),
                new(minMesh.X, maxMesh.Y, maxMesh.Z),
            };
            for (int i = 0; i < corners.Length; ++i)
            {
                verts.Add(corners[i]);
                bones.Add(boneIdx);
                bones.Add(0);
                bones.Add(0);
                bones.Add(0);
                weights.Add(1f);
                weights.Add(0f);
                weights.Add(0f);
                weights.Add(0f);
            }

            // Two representative rigid triangles owned solely by boneIdx.
            indices.Add(start + 0); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start + 4); indices.Add(start + 5); indices.Add(start + 6);
        }

        // Pelvis, lumbar, ribcage (offset upward from SpineControl1), neck, skull (offset upward from neck).
        AddBoneBox(hip, new Vector3(-0.14f, 0.92f, -0.09f), new Vector3(0.14f, 1.09f, 0.09f));
        AddBoneBox(spine, new Vector3(-0.07f, 1.09f, -0.06f), new Vector3(0.07f, 1.26f, 0.06f));
        AddBoneBox(spine1, new Vector3(-0.15f, 1.28f, -0.11f), new Vector3(0.15f, 1.62f, 0.11f));
        AddBoneBox(neck, new Vector3(-0.04f, 1.54f, -0.04f), new Vector3(0.04f, 1.64f, 0.04f));
        AddBoneBox(head, new Vector3(-0.09f, 1.66f, -0.11f), new Vector3(0.09f, 1.90f, 0.11f));

        // Left & right arms (horizontal shafts extending away from shoulder sockets).
        AddBoneBox(lClav, new Vector3(0.05f, 1.47f, -0.03f), new Vector3(0.18f, 1.53f, 0.03f));
        AddBoneBox(lUpperArm, new Vector3(0.18f, 1.44f, -0.04f), new Vector3(0.46f, 1.52f, 0.04f));
        AddBoneBox(lForearm, new Vector3(0.46f, 1.45f, -0.035f), new Vector3(0.72f, 1.51f, 0.035f));
        AddBoneBox(lHand, new Vector3(0.72f, 1.46f, -0.045f), new Vector3(0.79f, 1.50f, 0.045f));

        AddBoneBox(rClav, new Vector3(-0.18f, 1.47f, -0.03f), new Vector3(-0.05f, 1.53f, 0.03f));
        AddBoneBox(rUpperArm, new Vector3(-0.46f, 1.44f, -0.04f), new Vector3(-0.18f, 1.52f, 0.04f));
        AddBoneBox(rForearm, new Vector3(-0.72f, 1.45f, -0.035f), new Vector3(-0.46f, 1.51f, 0.035f));
        AddBoneBox(rHand, new Vector3(-0.79f, 1.46f, -0.045f), new Vector3(-0.72f, 1.50f, 0.045f));

        // Left & right legs (0.47m femur extending below hip socket at Y=1.04m).
        AddBoneBox(lThigh, new Vector3(0.065f, 0.57f, -0.045f), new Vector3(0.155f, 1.04f, 0.045f));
        AddBoneBox(lShin, new Vector3(0.07f, 0.09f, -0.04f), new Vector3(0.15f, 0.56f, 0.04f));
        AddBoneBox(lHeel, new Vector3(0.07f, 0.00f, -0.14f), new Vector3(0.15f, 0.09f, 0.05f));
        AddBoneBox(lTip, new Vector3(0.075f, 0.00f, -0.22f), new Vector3(0.145f, 0.05f, -0.14f));

        AddBoneBox(rThigh, new Vector3(-0.155f, 0.57f, -0.045f), new Vector3(-0.065f, 1.04f, 0.045f));
        AddBoneBox(rShin, new Vector3(-0.15f, 0.09f, -0.04f), new Vector3(-0.07f, 0.56f, 0.04f));
        AddBoneBox(rHeel, new Vector3(-0.15f, 0.00f, -0.14f), new Vector3(-0.07f, 0.09f, 0.05f));
        AddBoneBox(rTip, new Vector3(-0.145f, 0.00f, -0.22f), new Vector3(-0.075f, 0.05f, -0.14f));

        foreach (int fb in fingerBones)
        {
            Vector3 p = restGlobals[fb].Origin;
            AddBoneBox(fb, p - new Vector3(0.012f, 0.008f, 0.008f), p + new Vector3(0.012f, 0.008f, 0.008f));
        }

        var surfaces = new List<Engine_MdlDisarticulation.SurfaceSkinData>
        {
            new(verts.ToArray(), bones.ToArray(), weights.ToArray(), indices.ToArray()),
        };

        return (names.ToArray(), parents.ToArray(), restGlobals.ToArray(), surfaces);
    }

    [TestCase]
    public void Classifies65BoneSkeletonBaseHierarchyAsRigidPiecesWithIntactHandsAndFeet()
    {
        var (names, parents, restGlobals, surfaces) = BuildSkeletonBaseFixture();
        Engine_MdlDisarticulation.DisarticulatedMeshProfile profile =
            Engine_MdlDisarticulation.Analyze(names, parents, restGlobals, surfaces);

        AssertThat(profile.IsRigidPieceMesh).IsTrue();
        AssertThat(profile.Pieces.Length).IsEqual(65);
        // 19 physical rigid pieces: Hip, Spine, Spine1, Neck, Head + L/R (Clav, UpperArm, Forearm, Hand, Thigh, Shin, Heel).
        AssertThat(profile.SimulatedPieceCount).IsEqual(19);

        int rootIdx = Array.IndexOf(names, "CH_HeroMale_Morph_Body_Rig_Asset");
        int chestIdx = Array.IndexOf(names, "Shadow_Chest");
        int dummyIdx = Array.IndexOf(names, "FX_DummyObject_0");
        int lThighIdx = Array.IndexOf(names, "Shadow_L_Thigh");
        int lHandIdx = Array.IndexOf(names, "Shadow_L_Hand");
        int lThumbIdx = Array.IndexOf(names, "Shadow_L_Thumb2");
        int lHeelIdx = Array.IndexOf(names, "Shadow_L_Heel");
        int lTipIdx = Array.IndexOf(names, "Shadow_L_Tip");

        AssertThat(profile.Pieces[rootIdx].IsRootWrapper).IsTrue();
        AssertThat(profile.Pieces[rootIdx].IsSimulated).IsFalse();
        AssertThat(profile.Pieces[chestIdx].IsSimulated).IsFalse();
        AssertThat(profile.Pieces[dummyIdx].IsSimulated).IsFalse();

        // Femur COM sits ~0.235m below the hip socket along the vertical shaft, with a long capsule half-length.
        Engine_MdlDisarticulation.BonePieceDescriptor thigh = profile.Pieces[lThighIdx];
        AssertThat(thigh.IsSimulated).IsTrue();
        AssertThat(thigh.LocalCom.Y).IsEqualApprox(-0.235f, 0.02f);
        AssertThat(thigh.HalfLength).IsGreater(0.14f);
        AssertThat(MathF.Abs(thigh.LocalAxis.Y)).IsGreater(0.95f);

        // Hands and feet absorb their digit vertices so fingers/toes scatter intact rather than as shrapnel.
        AssertThat(profile.Pieces[lHandIdx].IsSimulated).IsTrue();
        AssertThat(profile.Pieces[lHandIdx].VertexCount).IsGreater(8 * 15);
        AssertThat(profile.Pieces[lThumbIdx].IsSimulated).IsFalse();
        AssertThat(profile.Pieces[lHeelIdx].IsSimulated).IsTrue();
        AssertThat(profile.Pieces[lHeelIdx].VertexCount).IsEqual(16);
        AssertThat(profile.Pieces[lTipIdx].IsSimulated).IsFalse();
    }

    [TestCase]
    public void RejectsSmoothSkinnedCharacterMeshesAndSmallRigidProps()
    {
        var (names, parents, restGlobals, surfaces) = BuildSkeletonBaseFixture();

        // Mutate a single vertex to carry multi-bone smooth skinning (60% spine, 40% hip).
        float[] smoothWeights = (float[])surfaces[0].Weights.Clone();
        int[] smoothBones = (int[])surfaces[0].Bones.Clone();
        smoothWeights[0] = 0.6f;
        smoothWeights[1] = 0.4f;
        smoothBones[1] = 2;
        var smoothSurfaces = new List<Engine_MdlDisarticulation.SurfaceSkinData>
        {
            new(surfaces[0].Vertices, smoothBones, smoothWeights, surfaces[0].Indices),
        };

        AssertThat(Engine_MdlDisarticulation.Analyze(names, parents, restGlobals, smoothSurfaces).IsRigidPieceMesh).IsFalse();

        // Mutate a triangle to bridge two distinct bones even with 100% single-bone vertex weights.
        int[] bridgedIndices = (int[])surfaces[0].Indices!.Clone();
        bridgedIndices[0] = 0;  // bone 1 (hip)
        bridgedIndices[1] = 8;  // bone 2 (spine)
        bridgedIndices[2] = 9;
        var bridgedSurfaces = new List<Engine_MdlDisarticulation.SurfaceSkinData>
        {
            new(surfaces[0].Vertices, surfaces[0].Bones, surfaces[0].Weights, bridgedIndices),
        };

        AssertThat(Engine_MdlDisarticulation.Analyze(names, parents, restGlobals, bridgedSurfaces).IsRigidPieceMesh).IsFalse();

        // Small 4-bone rigid prop (< 8 populated bones, chained parents) must NOT classify as a disarticulated skeleton.
        string[] propNames = { "Root", "Hinge", "DoorL", "DoorR" };
        int[] propParents = { -1, 0, 1, 1 };
        var propRest = new[]
        {
            Transform3D.Identity,
            new Transform3D(Basis.Identity, new Vector3(0f, 1f, 0f)),
            new Transform3D(Basis.Identity, new Vector3(-0.5f, 1f, 0f)),
            new Transform3D(Basis.Identity, new Vector3(0.5f, 1f, 0f)),
        };
        var propVerts = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
        var propBones = new[] { 2, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0 };
        var propWeights = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f };
        var propSurfaces = new List<Engine_MdlDisarticulation.SurfaceSkinData>
        {
            new(propVerts, propBones, propWeights, new[] { 0, 1, 2 }),
        };

        AssertThat(Engine_MdlDisarticulation.Analyze(propNames, propParents, propRest, propSurfaces).IsRigidPieceMesh).IsFalse();
    }

    [TestCase]
    public void EndToEndSkeletonBaseScattersWithIntactHandsAndFliesBackToSpawnOnReassembly()
    {
        var (names, parents, restGlobals, surfaces) = BuildSkeletonBaseFixture();
        Engine_MdlDisarticulation.DisarticulatedMeshProfile profile =
            Engine_MdlDisarticulation.Analyze(names, parents, restGlobals, surfaces);

        var crumbleWorld = new Transform3D(Basis.Identity, new Vector3(10f, 2.0f, -4f));
        var liveBones = new Dictionary<string, (Vector3 Pos, Quaternion Rot)>(StringComparer.OrdinalIgnoreCase);
        for (int m = 1; m < names.Length; ++m)
        {
            Transform3D worldRest = crumbleWorld * restGlobals[m];
            liveBones[names[m]] = (worldRest.Origin, Quaternion.Identity);
        }

        var rig = new Engine_DisarticulatedRig();
        rig.Initialize(crumbleWorld, names, restGlobals, parents, liveBones, new Vector3(1.2f, 0f, -0.5f), 0xB04E5u, profile.Pieces);

        // Zero frame-0 pop even with non-zero COM offsets (e.g. femur COM 0.235m below hip socket).
        AssertThat(rig.SimulatedPieceCount).IsEqual(19);
        int lThighIdx = Array.IndexOf(names, "Shadow_L_Thigh");
        int lHandIdx = Array.IndexOf(names, "Shadow_L_Hand");
        int lIndexIdx = Array.IndexOf(names, "Shadow_L_Index");
        int lIndex2Idx = Array.IndexOf(names, "Shadow_L_Index2");
        AssertThat(rig.Bodies[lThighIdx].Position.DistanceTo(liveBones["Shadow_L_Thigh"].Pos)).IsLess(1e-4f);
        AssertThat(rig.Bodies[lThighIdx].ComPosition.DistanceTo(rig.Bodies[lThighIdx].Position)).IsGreater(0.20f);

        Engine_DisarticulatedRig.TrySampleGroundContact slopeGround = (Vector3 pos, out Vector3 contact, out Vector3 normal) =>
        {
            float y = 2.0f + (0.18f * (pos.X - 10f));
            contact = new Vector3(pos.X, y, pos.Z);
            normal = new Vector3(-0.18f, 1.0f, 0.0f).Normalized();
            return true;
        };

        Transform3D expectedIndexRel = restGlobals[lHandIdx].AffineInverse() * restGlobals[lIndexIdx];
        Transform3D expectedIndex2Rel = restGlobals[lIndexIdx].AffineInverse() * restGlobals[lIndex2Idx];

        for (int step = 0; step < 240; ++step)
        {
            rig.Step(1f / 60f, crumbleWorld, slopeGround, default, 0);

            // Both capsule endpoints of every simulated piece must stay above the sloped terrain.
            for (int m = 0; m < rig.BoneCount; ++m)
            {
                Engine_DisarticulatedRig.BoneBody b = rig.Bodies[m];
                if (!b.IsSimulated)
                {
                    continue;
                }

                Vector3 axis = b.Rotation * b.LocalAxis;
                Vector3 e0 = b.ComPosition - (axis * b.HalfLength);
                Vector3 e1 = b.ComPosition + (axis * b.HalfLength);
                float floor0 = 2.0f + (0.18f * (e0.X - 10f));
                float floor1 = 2.0f + (0.18f * (e1.X - 10f));
                AssertThat(e0.Y).IsGreaterEqual(floor0 + b.Radius - 2e-3f);
                AssertThat(e1.Y).IsGreaterEqual(floor1 + b.Radius - 2e-3f);
            }

            // Finger phalanges must stay rigidly attached to their parent hand throughout scatter.
            Transform3D handPose = rig.ModelPoses[lHandIdx];
            Transform3D indexPose = rig.ModelPoses[lIndexIdx];
            Transform3D index2Pose = rig.ModelPoses[lIndex2Idx];
            AssertThat((handPose.AffineInverse() * indexPose).Origin.DistanceTo(expectedIndexRel.Origin)).IsLess(1e-4f);
            AssertThat((indexPose.AffineInverse() * index2Pose).Origin.DistanceTo(expectedIndex2Rel.Origin)).IsLess(1e-4f);
        }

        AssertThat(rig.AllAsleep).IsTrue();

        // Hollowman comes back to life at its spawn/rise transform: bones must lift off the ground and fly back.
        var respawnWorld = new Transform3D(new Basis(Vector3.Up, 0.7f), new Vector3(10.5f, 2.09f, -4.0f));
        rig.BeginReassembly(0.32f);
        AssertThat(rig.IsReassembling).IsTrue();
        AssertThat(rig.ReassemblyFinished).IsFalse();

        // Mid-flight check (~50% progress): bones should have moved off the ground toward respawnWorld.
        float peakMidLift = 0f;
        for (int step = 0; step < 10; ++step)
        {
            rig.Step(1f / 60f, respawnWorld, slopeGround, default, 0);
            peakMidLift = MathF.Max(peakMidLift, rig.Bodies[lThighIdx].Position.Y);
        }

        AssertThat(rig.ReassemblyFinished).IsFalse();
        AssertThat(peakMidLift).IsGreater(2.4f);

        // Complete flight to spawn pose.
        for (int step = 0; step < 12; ++step)
        {
            rig.Step(1f / 60f, respawnWorld, slopeGround, default, 0);
        }

        AssertThat(rig.ReassemblyFinished).IsTrue();
        AssertThat(rig.ReassemblyBlend).IsEqualApprox(1.0f, 1e-4f);

        // Every simulated bone AND welded finger/toe follower must land cleanly at respawnWorld * restGlobals[m].
        for (int m = 1; m < rig.BoneCount; ++m)
        {
            Transform3D expectedWorld = respawnWorld * restGlobals[m];
            Transform3D actualWorld = respawnWorld * rig.ModelPoses[m];
            AssertThat(actualWorld.Origin.DistanceTo(expectedWorld.Origin)).IsLess(1e-3f);
        }
    }
}
