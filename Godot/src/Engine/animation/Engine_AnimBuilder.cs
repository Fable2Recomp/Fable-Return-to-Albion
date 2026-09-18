using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Animations;

public static class Engine_AnimBuilder
{
    public const int MaxFrames = 4096;

    public static Animation? Build(
        AnimClip clip,
        Engine_AnimDecoder decoder,
        Skeleton3D skeleton,
        string skeletonNodePath)
    {
        if (clip.Fps <= 0.0f || clip.TocFrameCount == 0 || skeleton.GetBoneCount() == 0)
        {
            return null;
        }

        int frameCount = (int)Math.Min(clip.TocFrameCount, (uint)MaxFrames);
        int[] map = MapTracks(clip, skeleton);

        if (map.Length == 0)
        {
            return null;
        }

        var animation = new Animation
        {
            // THE GUEST'S DURATION IS (frames - 1) / fps — the time of the LAST KEY
            // (`sub_82283C30` stores exactly that at authoring-node +40), and its leaf times stop
            // there. frames / fps put a phantom interval after the last key, and a one-shot
            // playhead resting in it sampled Godot's loop interpolation back toward frame 0:
            // a door held 97% closed at "fully open" (b179).
            Length = MathF.Max(frameCount - 1, 1) / clip.Fps,
            // LINEAR so a looping leaf's seam interpolates from its last key back to its first.
            // The cost is that a sample taken at EXACTLY the length wraps to the first key,
            // which is why the animator keeps a one-shot playhead short of it (EndMargin).
            LoopMode = Animation.LoopModeEnum.Linear,
            ResourceName = Engine_AnimNaming.CleanClipName(clip.Name),
        };

        var rotationTrack = new int[map.Length];
        var positionTrack = new int[map.Length];

        for (int i = 0; i < map.Length; i++)
        {
            rotationTrack[i] = -1;
            positionTrack[i] = -1;

            if (map[i] < 0)
            {
                continue;
            }

            string path = $"{skeletonNodePath}:{skeleton.GetBoneName(map[i])}";

            rotationTrack[i] = animation.AddTrack(Animation.TrackType.Rotation3D);
            animation.TrackSetPath(rotationTrack[i], path);
            animation.TrackSetInterpolationType(
                rotationTrack[i], Animation.InterpolationType.Linear);

            // EVERY BONE BUT THE ROOT GETS ITS TRANSLATION, and the root gets none — the
            // guest's rule exactly. After sampling, `sub_82208040` (0x82208140..78) forces bone 0
            // to identity and keeps every other bone's sampled LOCAL transform; the decoded
            // translation is that transform, ABSOLUTE, not a delta from rest: the hip decodes at
            // z = 1.036 in WalkFootstep (rest height) and 0.621 in SitHoldBeerLoop (seat height),
            // and the spine segments decode at their constant bone lengths in every clip
            // (anim_probe, 2026-09-05). This used to be the inverse — a track for the root only,
            // composed onto rest — so no bone ever received a translation from a clip and every
            // seated NPC sat at standing height.
            if (skeleton.GetBoneParent(map[i]) >= 0)
            {
                positionTrack[i] = animation.AddTrack(Animation.TrackType.Position3D);
                animation.TrackSetPath(positionTrack[i], path);
                animation.TrackSetInterpolationType(
                    positionTrack[i], Animation.InterpolationType.Linear);
            }
        }

        var pose = new DecodedPose();

        for (int frame = 0; frame < frameCount; frame++)
        {
            float time = frame / clip.Fps;
            if (!decoder.Decode(clip, time, pose) || !pose.Ok)
            {
                continue;
            }

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0 || i >= pose.BoneCount || rotationTrack[i] < 0)
                {
                    continue;
                }

                var rotation = new Quaternion(
                    pose.BoneQuats[i * 4 + 0],
                    pose.BoneQuats[i * 4 + 1],
                    pose.BoneQuats[i * 4 + 2],
                    pose.BoneQuats[i * 4 + 3]);

                if (rotation.LengthSquared() <= 1e-12f)
                {
                    rotation = Quaternion.Identity;
                }
                else if (!rotation.IsNormalized())
                {
                    rotation = rotation.Normalized();
                }

                animation.RotationTrackInsertKey(
                    rotationTrack[i], time, Engine_AnimSkeleton.ToYUp(rotation));

                if (positionTrack[i] < 0)
                {
                    continue;
                }

                animation.PositionTrackInsertKey(
                    positionTrack[i], time, Engine_AnimSkeleton.ToYUp(
                        new Vector3(
                            pose.BoneTrans[i * 3 + 0],
                            pose.BoneTrans[i * 3 + 1],
                            pose.BoneTrans[i * 3 + 2])));
            }
        }

        return animation.GetTrackCount() > 0 ? animation : null;
    }

    /// <summary>A clip's decoded keys per CLIP TRACK, before any skeleton is involved.</summary>
    /// <remarks>
    /// Exactly the keys <see cref="Build"/> inserts: the same frames, times, normalisation and
    /// Y-up conversion. The skeleton only decides which bone a track lands on and whether that
    /// bone takes the translation (<see cref="MapTracks"/>), so these can be shared by every rig.
    /// A track index past the decoded pose's bone count has no keys, as in Build.
    /// </remarks>
    public sealed class DecodedKeys
    {
        public float Length;
        public float[][] Times = Array.Empty<float[]>();
        public Quaternion[][] Rotations = Array.Empty<Quaternion[]>();
        public Vector3[][] Positions = Array.Empty<Vector3[]>();
    }

    public static DecodedKeys? DecodeKeys(AnimClip clip, Engine_AnimDecoder decoder)
    {
        if (clip.Fps <= 0.0f || clip.TocFrameCount == 0 || clip.TrackMap is null || clip.TrackMap.Count == 0)
        {
            return null;
        }

        int frameCount = (int)Math.Min(clip.TocFrameCount, (uint)MaxFrames);
        int tracks = clip.TrackMap.Count;
        var times = new List<float>[tracks];
        var rotations = new List<Quaternion>[tracks];
        var positions = new List<Vector3>[tracks];
        for (int i = 0; i < tracks; i++)
        {
            times[i] = new List<float>(frameCount);
            rotations[i] = new List<Quaternion>(frameCount);
            positions[i] = new List<Vector3>(frameCount);
        }

        var pose = new DecodedPose();
        for (int frame = 0; frame < frameCount; frame++)
        {
            float time = frame / clip.Fps;
            if (!decoder.Decode(clip, time, pose) || !pose.Ok)
            {
                continue;
            }

            int n = Math.Min(tracks, pose.BoneCount);
            for (int i = 0; i < n; i++)
            {
                var rotation = new Quaternion(
                    pose.BoneQuats[i * 4 + 0],
                    pose.BoneQuats[i * 4 + 1],
                    pose.BoneQuats[i * 4 + 2],
                    pose.BoneQuats[i * 4 + 3]);

                if (rotation.LengthSquared() <= 1e-12f)
                {
                    rotation = Quaternion.Identity;
                }
                else if (!rotation.IsNormalized())
                {
                    rotation = rotation.Normalized();
                }

                times[i].Add(time);
                rotations[i].Add(Engine_AnimSkeleton.ToYUp(rotation));
                positions[i].Add(Engine_AnimSkeleton.ToYUp(new Vector3(
                    pose.BoneTrans[i * 3 + 0],
                    pose.BoneTrans[i * 3 + 1],
                    pose.BoneTrans[i * 3 + 2])));
            }
        }

        var keys = new DecodedKeys
        {
            Length = MathF.Max(frameCount - 1, 1) / clip.Fps,
            Times = new float[tracks][],
            Rotations = new Quaternion[tracks][],
            Positions = new Vector3[tracks][],
        };
        for (int i = 0; i < tracks; i++)
        {
            keys.Times[i] = times[i].ToArray();
            keys.Rotations[i] = rotations[i].ToArray();
            keys.Positions[i] = positions[i].ToArray();
        }

        return keys;
    }

    /// <summary>The skeleton's bone-name candidates for <see cref="MapTracks"/>; build once per skeleton.</summary>
    public static Dictionary<string, List<int>> BoneCandidates(Skeleton3D skeleton)
    {
        int boneCount = skeleton.GetBoneCount();
        var candidates = new Dictionary<string, List<int>>(boneCount * 2);

        void AddCandidate(string key, int index)
        {
            if (key.Length == 0)
            {
                return;
            }

            if (!candidates.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                candidates[key] = list;
            }

            if (!list.Contains(index))
            {
                list.Add(index);
            }
        }

        for (int i = 0; i < boneCount; i++)
        {
            string raw = Engine_AnimNaming.Plain(skeleton.GetBoneName(i));
            string bare = Engine_AnimNaming.WithoutShadow(raw);
            AddCandidate(raw, i);
            AddCandidate(bare, i);
            AddCandidate("shadow_" + bare, i);
        }

        return candidates;
    }

    private static int[] MapTracks(AnimClip clip, Skeleton3D skeleton) =>
        MapTracks(clip, skeleton, BoneCandidates(skeleton));

    /// <summary>Clip track → skeleton bone, or -1; empty when nothing matched.</summary>
    public static int[] MapTracks(AnimClip clip, Skeleton3D skeleton, Dictionary<string, List<int>> candidates)
    {
        if (clip.TrackMap is null || clip.TrackMap.Count == 0)
        {
            return Array.Empty<int>();
        }

        var resolved = new int[clip.TrackMap.Count];
        for (int i = 0; i < resolved.Length; i++)
        {
            resolved[i] = -1;
        }

        int matched = 0;

        for (int track = 0; track < resolved.Length; track++)
        {
            AnimTrackBone bone = clip.TrackMap[track];
            string norm = Engine_AnimNaming.Plain(bone.Name);

            if (norm.Length == 0 || norm == "locator")
            {
                resolved[track] = bone.Parent < 0 ? FirstRoot(skeleton, resolved, track) : -1;
                if (resolved[track] >= 0)
                {
                    matched++;
                }

                continue;
            }

            List<int> options = Options(candidates, norm);
            if (options.Count == 0)
            {
                continue;
            }

            if (options.Count == 1)
            {
                resolved[track] = options[0];
                matched++;
                continue;
            }

            int expectedParent = -2;
            if (bone.Parent < 0)
            {
                expectedParent = -1;
            }
            else if (bone.Parent < resolved.Length && resolved[bone.Parent] >= 0)
            {
                expectedParent = resolved[bone.Parent];
            }

            int chosen = -1;

            foreach (int option in options)
            {
                if (expectedParent != -2 &&
                    skeleton.GetBoneParent(option) == expectedParent &&
                    !AlreadyUsed(resolved, track, option))
                {
                    chosen = option;
                    break;
                }
            }

            if (chosen < 0)
            {
                foreach (int option in options)
                {
                    if (!AlreadyUsed(resolved, track, option))
                    {
                        chosen = option;
                        break;
                    }
                }
            }

            if (chosen < 0)
            {
                chosen = options[0];
            }

            resolved[track] = chosen;
            matched++;
        }

        return matched > 0 ? resolved : Array.Empty<int>();
    }

    private static List<int> Options(Dictionary<string, List<int>> candidates, string norm)
    {
        var options = new List<int>();

        void Append(string key)
        {
            if (!candidates.TryGetValue(key, out List<int>? list))
            {
                return;
            }

            foreach (int index in list)
            {
                if (!options.Contains(index))
                {
                    options.Add(index);
                }
            }
        }

        Append(norm);
        string bare = Engine_AnimNaming.WithoutShadow(norm);
        Append(bare);
        Append("shadow_" + bare);
        return options;
    }

    private static bool AlreadyUsed(int[] resolved, int track, int candidate)
    {
        for (int i = 0; i < resolved.Length; i++)
        {
            if (i != track && resolved[i] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static int FirstRoot(Skeleton3D skeleton, int[] resolved, int track)
    {
        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            if (skeleton.GetBoneParent(i) < 0 && !AlreadyUsed(resolved, track, i))
            {
                return i;
            }
        }

        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            if (skeleton.GetBoneParent(i) < 0)
            {
                return i;
            }
        }

        return -1;
    }
}
