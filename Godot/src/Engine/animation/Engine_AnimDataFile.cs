using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Animations;

public sealed class Engine_AnimDataFile
{
    public const string DataPath = "data/fable2_anims.animation_data";

    public const string DataExtension = ".animation_data";

    public const uint ExpectedMagic = 0xCEA5EBEDu;

    public const uint ExpectedVersion = 7u;

    private byte[] _blob = Array.Empty<byte>();

    public bool IsOpen => _blob.Length != 0;

    public int Size => _blob.Length;

    public sealed class ClipHeader
    {
        public uint Magic { get; set; }

        public uint Version { get; set; }

        public uint TrackCount { get; set; }

        public uint BoneCount { get; set; }

        public uint FrameCount { get; set; }

        public uint BoneIndexBits { get; set; }

        public int PackedBodyOffset { get; set; }

        public uint[] BoneOffsets { get; set; } = Array.Empty<uint>();

        /// <summary>
        /// The bones follow one another in the bitstream with no offset directory — Fable 3's
        /// AnimFile v8 body (Engine_CsaFile). Fable 2's animation_data seeks each bone.
        /// </summary>
        public bool Sequential { get; set; }

        /// <summary>Extra channels past the seventh are 24-bit (Fable 3), not 16-bit.</summary>
        public bool WideExtraChannels { get; set; }

        /// <summary>Each track's parent track (-1 = root), when the clip states its skeleton.</summary>
        public int[] Parents { get; set; } = Array.Empty<int>();

        /// <summary>
        /// Tracks the body stores in MODEL space (body+6..+9, 0xFF = none): the hands and feet of a
        /// biped. The decoder re-localises them against the composed parent chain as the guest's
        /// <c>sub_82D4B428</c> does (compose the chain with <c>sub_8222AD80</c> up to the root, then
        /// <c>sub_82D4AFD8</c>: conjugate(parent) ⊗ model, model.t − parent.t rotated back).
        /// </summary>
        public int[] ModelSpaceTracks { get; set; } = Array.Empty<int>();

        public bool Ok { get; set; }
    }

    public bool Open(byte[] bytes)
    {
        _blob = Array.Empty<byte>();

        if (bytes is null || bytes.Length < 0x1C)
        {
            GD.PushWarning("[anim] animation data is too short");
            return false;
        }

        // Two blobs open here: Fable 2's animation_data (one magic for the file) and the Fable 3
        // aggregate Engine_AnimCatalog builds out of every .csa, whose first clip's own magic
        // leads it. Each clip then states its own format to ParseClipHeader.
        if (Engine_CsaFile.HasMagic(bytes))
        {
            Attach(bytes);
            GD.Print($"[anim] AnimFile v8 clips opened ({_blob.Length} bytes)");
            return true;
        }

        uint magic = Engine_AnimReader.ReadU32Be(bytes, 0);
        uint version = Engine_AnimReader.ReadU32Be(bytes, 4);

        if (magic != ExpectedMagic)
        {
            GD.PushWarning($"[anim] bad data magic 0x{magic:X8}");
            return false;
        }

        if (version != ExpectedVersion)
        {
            GD.PushWarning($"[anim] unsupported data version {version}");
            return false;
        }

        _blob = bytes;
        GD.Print($"[anim] data opened ({_blob.Length} bytes)");
        return true;
    }

    public void Close() => _blob = Array.Empty<byte>();

    /// <summary>Holds <paramref name="bytes"/> as the data without checking or logging — for a test's synthetic clip.</summary>
    public void Attach(byte[] bytes) => _blob = bytes ?? Array.Empty<byte>();

    public bool TryGetClipSpan(AnimClip clip, out int offset, out int length)
    {
        offset = 0;
        length = 0;

        if (_blob.Length == 0 || clip.DataOffset >= (uint)_blob.Length)
        {
            return false;
        }

        offset = (int)clip.DataOffset;
        int remaining = _blob.Length - offset;
        length = clip.DataSizeBytes != 0
            ? (int)Math.Min(clip.DataSizeBytes, (uint)remaining)
            : remaining;

        return length > 0;
    }

    public byte[] Blob => _blob;

    public ClipHeader ParseClipHeader(AnimClip clip)
    {
        var header = new ClipHeader();

        if (!TryGetClipSpan(clip, out int offset, out int length) || length < 0x1C)
        {
            return header;
        }

        // Fable 3's clip (Engine_CsaFile): the header carries its own skeleton and events; the
        // body's prefix states the counts the codec needs, and the bitstream starts at body+10
        // with the bones in sequence (sub_821D4480, `v53 = v8 + 10`).
        if (Engine_CsaFile.HasMagic(_blob, offset))
        {
            if (!Engine_CsaFile.TryParse(_blob, offset, length, out Engine_CsaFile.Clip csa, out _))
            {
                return header;
            }

            header.Magic = ExpectedMagic;
            header.Version = Engine_CsaFile.ExpectedVersion;
            header.TrackCount = (uint)csa.Bones.Count;
            header.BoneCount = header.TrackCount;
            header.FrameCount = (uint)csa.FrameCount;
            header.BoneIndexBits = (uint)csa.FrameIndexBits;
            header.PackedBodyOffset = csa.BitstreamOffset - offset;
            header.Sequential = true;
            header.WideExtraChannels = true;

            var parents = new int[csa.Bones.Count];
            for (int i = 0; i < parents.Length; i++)
            {
                parents[i] = csa.Bones[i].Parent;
            }

            header.Parents = parents;
            // THE MODEL-SPACE SET IS FIVE BYTES, SORTED. sub_82D4B178 collects body+1 (the
            // root-motion bone) and body+6..+9, drops 0xFF and duplicates, and bubble-sorts them
            // ascending (0x82d4b1dc..0x82d4b344); sub_82D4B428 re-localises them in that order,
            // so a model-space parent is local before its child composes through it. body+1 is
            // re-localised like the rest — its extra branch only copies the sample out as root
            // motion — so it stays in the pose.
            var special = new List<int>(5);
            for (int i = -1; i < csa.SpecialBones.Length; i++)
            {
                byte index = i < 0 ? csa.RootByte : csa.SpecialBones[i];
                if (index != 0xFF && index < parents.Length && !special.Contains(index))
                {
                    special.Add(index);
                }
            }

            special.Sort();
            header.ModelSpaceTracks = special.ToArray();
            header.Ok = header.BoneCount != 0 && header.FrameCount != 0;
            return header;
        }

        header.Magic = Engine_AnimReader.ReadU32Be(_blob, offset + 0x00);
        header.Version = Engine_AnimReader.ReadU32Be(_blob, offset + 0x04);
        header.TrackCount = Engine_AnimReader.ReadU32Be(_blob, offset + 0x0C);
        header.BoneCount = header.TrackCount;
        header.FrameCount = Engine_AnimReader.ReadU32Be(_blob, offset + 0x10);
        header.BoneIndexBits = Engine_AnimReader.ReadU32Be(_blob, offset + 0x14);

        if (header.Magic != ExpectedMagic || header.Version != ExpectedVersion)
        {
            return header;
        }

        if (header.BoneCount == 0 || header.FrameCount == 0)
        {
            return header;
        }

        const int directoryOffset = 24;
        long directoryEnd = directoryOffset + (long)header.BoneCount * 4;
        if (directoryEnd > length)
        {
            return header;
        }

        header.PackedBodyOffset = (int)directoryEnd;
        var offsets = new uint[header.BoneCount];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = Engine_AnimReader.ReadU32Be(_blob, offset + directoryOffset + i * 4);
        }

        header.BoneOffsets = offsets;
        header.Ok = true;
        return header;
    }
}
