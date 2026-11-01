using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Animations;

/// <summary>
/// Fable 3's clip file, "AnimFile" version 8 (`.csa`): the header, the clip's own skeleton, its
/// events and where the packed body starts. Pure parsing, no engine types, so a test can feed it
/// a synthetic buffer.
/// </summary>
/// <remarks>
/// Fable 3 disc XEX: the loader is <c>sub_82D528A0</c> → header <c>sub_82D51D90</c>, gate
/// <c>sub_82D51BD0</c> (the 8 magic bytes are stored and never compared; the u16 version must be 8,
/// <c>0x82d51cd0</c>), skeleton <c>sub_82D533C8</c>, then <c>sub_82D51258</c> rounds the stream up to
/// 16 bytes and the remainder is kept raw as the body (clip+28). The body's own prefix is read by
/// the decoder <c>sub_821D4480</c>: +2 u8 bones, +3 u8 frame-index bits, +4 u16 frames, and the
/// bitstream base at body+10 (<c>v53 = v8 + 10</c>). The codec is Fable 2's
/// (<see cref="Engine_AnimDecoder"/>) with bones laid out sequentially — no per-bone offset
/// directory — and 24-bit extra channels. Layout and the evidence: anim/CSA_SPEC.md.
/// </remarks>
public static class Engine_CsaFile
{
    public const string Extension = ".csa";

    public const int MagicLength = 8;

    public const ushort ExpectedVersion = 8;

    /// <summary>The body prefix: marker, root byte, bones, frame bits, frames u16, four special bones.</summary>
    public const int BodyPrefixBytes = 10;

    public const byte BodyMarker = 0xAD;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AnimFile");

    public sealed class Bone
    {
        public uint NameHash { get; set; }

        public int Parent { get; set; } = -1;
    }

    public sealed class Event
    {
        public float Time { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public sealed class Clip
    {
        public float Fps { get; set; }

        public int FrameCount { get; set; }

        /// <summary>{skeleton id hash, cumulative bone count} per LOD tier (sub_82D533C8).</summary>
        public List<(uint Hash, uint BoneCount)> Tiers { get; } = new();

        public List<Bone> Bones { get; } = new();

        public List<Event> Events { get; } = new();

        /// <summary>Offset of the 0xAD body marker within the file.</summary>
        public int BodyOffset { get; set; }

        /// <summary>The decoder's frame-index width (body+3), the page-record field width in curves.</summary>
        public int FrameIndexBits { get; set; }

        /// <summary>Body+1, the root-motion bone (0xFF = none): stored in model space like
        /// <see cref="SpecialBones"/>, and also copied out as root motion (sub_82D4B178/sub_82D4B428).</summary>
        public byte RootByte { get; set; }

        /// <summary>Body+6..+9, bones stored in MODEL space (0xFF = none; hands and feet on
        /// bipeds); they do not move the bitstream cursor.</summary>
        public byte[] SpecialBones { get; set; } = new byte[4];

        /// <summary>Offset of the first bitstream word, body+10.</summary>
        public int BitstreamOffset => BodyOffset + BodyPrefixBytes;

        public float DurationSeconds => Fps > 0.0f && FrameCount > 1 ? (FrameCount - 1) / Fps : 0.0f;
    }

    /// <summary>Whether <paramref name="bytes"/> at <paramref name="offset"/> starts with "AnimFile".</summary>
    public static bool HasMagic(byte[] bytes, int offset = 0)
    {
        if (bytes is null || offset < 0 || offset + MagicLength > bytes.Length)
        {
            return false;
        }

        for (int i = 0; i < MagicLength; i++)
        {
            if (bytes[offset + i] != Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses one clip. False with <paramref name="error"/> set when the version is not 8 or the
    /// layout does not hold (a count past the end, a body without its marker, a body whose bone
    /// or frame count disagrees with the header).
    /// </summary>
    public static bool TryParse(byte[] bytes, out Clip clip, out string error) =>
        TryParse(bytes, 0, bytes?.Length ?? 0, out clip, out error);

    /// <summary>Parses the clip held in <paramref name="bytes"/> at [offset, offset+length).</summary>
    public static bool TryParse(byte[] bytes, int offset, int length, out Clip clip, out string error)
    {
        clip = new Clip();
        error = string.Empty;

        if (bytes is null || offset < 0 || length < 0 || offset + length > bytes.Length)
        {
            error = "no data";
            return false;
        }

        var reader = new Engine_AnimReader(bytes) { Position = offset };
        int end = offset + length;

        if (!HasMagic(bytes, offset))
        {
            error = "not an AnimFile";
            return false;
        }

        reader.Position = offset + MagicLength;
        uint version = reader.U16();
        if (version != ExpectedVersion)
        {
            error = $"version {version}, expected {ExpectedVersion}";
            return false;
        }

        reader.U8();
        reader.U8();
        reader.U32();
        reader.U32();
        clip.Fps = reader.F32();
        clip.FrameCount = (int)reader.U16();
        uint eventCount = reader.U16();
        reader.U32();
        reader.U32();
        reader.U32();

        // The skeleton (sub_82D533C8): two words, the two counts, two words, tiers, bones.
        reader.U32();
        reader.U32();
        uint tierCount = reader.U16();
        uint boneCount = reader.U16();
        reader.U32();
        reader.U32();

        if (!reader.Ok || reader.Position > end)
        {
            error = "header truncated";
            return false;
        }

        if (boneCount > 255)
        {
            error = $"{boneCount} bones, the body stores the count in a byte";
            return false;
        }

        for (uint i = 0; i < tierCount; i++)
        {
            uint hash = reader.U32();
            uint count = reader.U32();
            clip.Tiers.Add((hash, count));
        }

        for (uint i = 0; i < boneCount; i++)
        {
            uint hash = reader.U32();
            int parent = (int)reader.U32();
            clip.Bones.Add(new Bone { NameHash = hash, Parent = parent });
        }

        if (!reader.Ok || reader.Position > end)
        {
            error = "skeleton truncated";
            return false;
        }

        // Events: {f32 seconds, u16 name offset, u16 name length}, then the names back to back.
        var pending = new List<(float Time, int Offset, int Length)>((int)Math.Min(eventCount, 4096u));
        int namesLength = 0;
        for (uint i = 0; i < eventCount; i++)
        {
            float time = reader.F32();
            int nameOffset = (int)reader.U16();
            int nameLength = (int)reader.U16();
            pending.Add((time, nameOffset, nameLength));
            namesLength += nameLength;
        }

        if (!reader.Ok || reader.Position + namesLength > end)
        {
            error = "events truncated";
            return false;
        }

        int namesStart = reader.Position;
        foreach ((float time, int nameOffset, int nameLength) in pending)
        {
            string name = nameOffset + nameLength <= namesLength
                ? Encoding.Latin1.GetString(bytes, namesStart + nameOffset, nameLength)
                : string.Empty;
            clip.Events.Add(new Event { Time = time, Name = name });
        }

        reader.Position = namesStart + namesLength;

        // sub_82D51258: the stream rounds up to 16 bytes, measured from the file start.
        int body = offset + (((reader.Position - offset) + 15) & ~15);
        if (body + BodyPrefixBytes > end)
        {
            error = "no body";
            return false;
        }

        if (bytes[body] != BodyMarker)
        {
            error = $"body marker 0x{bytes[body]:X2} at {body - offset}";
            return false;
        }

        clip.BodyOffset = body;
        clip.RootByte = bytes[body + 1];
        int bodyBones = bytes[body + 2];
        clip.FrameIndexBits = bytes[body + 3];
        int bodyFrames = (bytes[body + 4] << 8) | bytes[body + 5];
        Array.Copy(bytes, body + 6, clip.SpecialBones, 0, 4);

        if (bodyBones != clip.Bones.Count)
        {
            error = $"body has {bodyBones} bones, the skeleton {clip.Bones.Count}";
            return false;
        }

        if (bodyFrames != clip.FrameCount)
        {
            error = $"body has {bodyFrames} frames, the header {clip.FrameCount}";
            return false;
        }

        return true;
    }

    /// <summary>The frame-index width the writer stores at body+3: the smallest b with frames &lt; 2^b, at least 1.</summary>
    public static int FrameIndexBitsFor(int frameCount)
    {
        int bits = 1;
        while (frameCount >= (1 << bits) && bits < 31)
        {
            bits++;
        }

        return bits;
    }

    /// <summary>The Godot bone name for a hashed skeleton bone, as Engine_MdlEngine47 and RefiiHostGetEntityBoneNames spell it.</summary>
    public static string BoneName(uint hash) => $"0x{hash:X8}";
}
