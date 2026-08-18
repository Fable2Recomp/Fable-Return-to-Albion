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
            Length = frameCount / clip.Fps,
            LoopMode = Animation.LoopModeEnum.Linear,
            ResourceName = Engine_AnimNaming.CleanClipName(clip.Name),
        };

        var rotationTrack = new int[map.Length];
        var positionTrack = new int[map.Length];
        var positionRest = new Vector3[map.Length];

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

            if (skeleton.GetBoneParent(map[i]) < 0)
            {
                positionRest[i] = skeleton.GetBoneRest(map[i]).Origin;
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
                    positionTrack[i], time, positionRest[i] + Engine_AnimSkeleton.ToYUp(
                        new Vector3(
                            pose.BoneTrans[i * 3 + 0],
                            pose.BoneTrans[i * 3 + 1],
                            pose.BoneTrans[i * 3 + 2])));
            }
        }

        return animation.GetTrackCount() > 0 ? animation : null;
    }

    private static int[] MapTracks(AnimClip clip, Skeleton3D skeleton)
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
