using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Animations;

public static class Engine_AnimBank
{
    public const string TocPath = "data/fable2_anims.animation_toc";

    public const string TocExtension = ".animation_toc";

    private const uint ExpectedMajor = 1;

    private const uint ExpectedMinor = 5;

    public static bool TryParse(byte[] bytes, out List<AnimClip> clips)
    {
        clips = new List<AnimClip>();

        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        var reader = new Engine_AnimReader(bytes);

        if (!reader.Match("AnimBank"))
        {
            GD.PushWarning("[anim] not an AnimBank table of contents");
            return false;
        }

        uint major = reader.U32();
        uint minor = reader.U32();
        uint recordCount = reader.U32();
        uint specialCount = reader.U32();
        uint stringCount = reader.U32();

        if (!reader.Ok)
        {
            GD.PushWarning("[anim] table of contents header is truncated");
            return false;
        }

        if (major != ExpectedMajor || minor != ExpectedMinor)
        {
            GD.PushWarning($"[anim] unsupported bank version {major}/{minor}");
            return false;
        }

        var strings = new List<string>((int)Math.Min(stringCount, 1u << 20));
        for (uint i = 0; i < stringCount; i++)
        {
            strings.Add(reader.CStr());
            if (!reader.Ok)
            {
                GD.PushWarning($"[anim] string table truncated at {i}");
                return false;
            }
        }

        for (uint i = 0; i < recordCount; i++)
        {
            var clip = new AnimClip
            {
                Key0 = reader.U32(),
                Key1 = reader.U32(),
                DataOffset = reader.U32(),
                TocFrameCount = reader.U32(),
            };
            clip.Fps = reader.F32();
            uint eventCount = reader.U32();

            if (!reader.Ok)
            {
                GD.PushWarning($"[anim] clip record truncated at {i}");
                return false;
            }

            for (uint e = 0; e < eventCount; e++)
            {
                var entry = new AnimEvent { Time = reader.F32() };
                entry.Name = Lookup(reader.U32(), strings);
                entry.Param = Lookup(reader.U32(), strings);
                clip.Events.Add(entry);
            }

            if (!reader.Ok)
            {
                GD.PushWarning($"[anim] clip events truncated at {i}");
                return false;
            }

            clip.Name = $"id_{clip.Key0:X8}";
            clips.Add(clip);
        }

        AssignDataSizes(clips);
        AssignTrackMaps(reader, specialCount, strings, clips);

        GD.Print($"[anim] bank: {clips.Count} clip(s), {strings.Count} string(s)");
        return clips.Count > 0;
    }

    private static void AssignDataSizes(List<AnimClip> clips)
    {
        var order = new List<int>(clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            order.Add(i);
        }

        order.Sort((a, b) => clips[a].DataOffset.CompareTo(clips[b].DataOffset));

        for (int i = 0; i < order.Count; i++)
        {
            AnimClip clip = clips[order[i]];
            if (i + 1 < order.Count)
            {
                uint next = clips[order[i + 1]].DataOffset;
                clip.DataSizeBytes = next > clip.DataOffset ? next - clip.DataOffset : 0;
            }
            else
            {
                clip.DataSizeBytes = 0;
            }
        }
    }

    private static void AssignTrackMaps(
        Engine_AnimReader reader,
        uint specialCount,
        List<string> strings,
        List<AnimClip> clips)
    {
        var trackMaps = new Dictionary<uint, List<AnimTrackBone>>();

        for (uint i = 0; i < specialCount; i++)
        {
            var header = new uint[8];
            for (int h = 0; h < 8; h++)
            {
                header[h] = reader.U32();
            }

            if (!reader.Ok)
            {
                break;
            }

            uint argCount = reader.U32();
            var bones = new List<AnimTrackBone>((int)Math.Min(argCount, 4096u));

            for (uint a = 0; a < argCount; a++)
            {
                uint nameIndex = reader.U32();
                uint parentIndex = reader.U32();
                bones.Add(new AnimTrackBone
                {
                    Name = Lookup(nameIndex, strings),
                    Parent = parentIndex == 0xFFFFFFFFu ? -1 : (int)parentIndex,
                });
            }

            if (!reader.Ok)
            {
                break;
            }

            trackMaps[header[6]] = bones;
        }

        foreach (AnimClip clip in clips)
        {
            if (trackMaps.TryGetValue(clip.Key1, out List<AnimTrackBone>? bones))
            {
                clip.TrackMap = bones;
            }
        }
    }

    private static string Lookup(uint index, List<string> strings) =>
        index == 0xFFFFFFFFu || index >= strings.Count ? string.Empty : strings[(int)index];
}
