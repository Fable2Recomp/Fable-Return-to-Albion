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
