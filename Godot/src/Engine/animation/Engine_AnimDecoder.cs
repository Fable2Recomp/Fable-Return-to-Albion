using System;

namespace Refii.Animations;

public sealed class DecodedPose
{
    public float[] BoneQuats { get; set; } = Array.Empty<float>();

    public float[] BoneTrans { get; set; } = Array.Empty<float>();

    public int BoneCount { get; set; }

    public bool Ok { get; set; }
}

public sealed class Engine_AnimDecoder
{
    private const float Q16Scale = 2.0f / 65535.0f;

    private const float Q16Bias = -1.0f;

    private const int FloatsPerBoneFrame = 7;

    private const int BlockStride = 8;

    private const int SamplesPerBlock = 9;

    private const int CacheBuckets = 1024;

    private sealed class Block
    {
        public int ClipOffset = -1;

        public uint BlockIndex;

        public uint BoneCount;

        public float[] Data = Array.Empty<float>();
    }

    private readonly Block[] _cache = new Block[CacheBuckets];

    private readonly Engine_AnimDataFile _file;

    public Engine_AnimDecoder(Engine_AnimDataFile file)
    {
        _file = file;
        for (int i = 0; i < _cache.Length; i++)
        {
            _cache[i] = new Block();
        }
    }

    public void ClearCache()
    {
        for (int i = 0; i < _cache.Length; i++)
        {
            _cache[i] = new Block();
        }
    }

    public bool Decode(AnimClip clip, float timeSeconds, DecodedPose pose)
    {
        pose.Ok = false;

        if (!_file.IsOpen)
        {
            return false;
        }

        Engine_AnimDataFile.ClipHeader header = _file.ParseClipHeader(clip);
        if (!header.Ok || header.BoneCount == 0)
        {
            return false;
        }

        if (header.BoneCount > 4096 || header.FrameCount > 65536)
        {
            return false;
        }

        if (clip.Fps <= 0.0f)
        {
            return false;
        }

        uint frameCount = header.FrameCount != 0 ? header.FrameCount : 1;

        float frame = clip.Fps * timeSeconds;
        if (frame < 0.0f)
        {
            frame = 0.0f;
        }

        float frameMax = frameCount - 1 - 0.001f;
        if (frameMax > 0.0f && frame > frameMax)
        {
            frame = frameMax;
        }

        uint frameInt = (uint)frame;
        uint blockIndex = frameInt >> 3;
        float fracInBlock = frame - (blockIndex << 3);

        if (!_file.TryGetClipSpan(clip, out int offset, out int length))
        {
            return false;
        }

        int bucket = (int)((((uint)offset >> 4) ^ blockIndex) & (CacheBuckets - 1));
        Block slot = _cache[bucket];

        bool hit = slot.ClipOffset == offset &&
                   slot.BlockIndex == blockIndex &&
                   slot.BoneCount == header.BoneCount;

        if (!hit)
        {
            if (!DecodeBlock(header, offset, length, header.BoneCount, blockIndex, slot))
            {
                _cache[bucket] = new Block();
                return false;
            }

            slot.ClipOffset = offset;
            slot.BlockIndex = blockIndex;
            slot.BoneCount = header.BoneCount;
        }

        Interpolate(slot, fracInBlock, pose);
        pose.Ok = true;
        return true;
    }

    private static float DequantUnit(uint raw) => (raw & 0xFFFF) * Q16Scale + Q16Bias;

    private static float DequantUnitBits(uint raw, uint bits)
    {
        if (bits == 0)
        {
            return 0.0f;
        }

        if (bits >= 31)
        {
            bits = 30;
        }

        float maxRaw = (1u << (int)bits) - 1u;
        return raw * (2.0f / maxRaw) - 1.0f;
    }

    private static float DequantWorldBits(uint raw, uint bits)
    {
        if (bits == 0)
        {
            return 0.0f;
        }

        if (bits >= 31)
        {
            bits = 30;
        }

        float maxRaw = (1u << (int)bits) - 1u;
        return raw * (512.0f / maxRaw) - 256.0f;
    }

    private static float DequantWorld24(uint raw) => DequantWorldBits(raw & 0xFFFFFFu, 24);

    private static float Mode3CurveFactor(uint curveIndex, uint step, uint run)
    {
        if (run == 0)
        {
            return 0.0f;
        }

        float t = (float)step / run;
        float u = 1.0f - t;
        float b1 = 3.0f * u * u * t;
        float b2 = 3.0f * u * t * t;
        float b3 = t * t * t;

        float c1 = 1.0f / 3.0f;
        float c2 = 2.0f / 3.0f;

        if (curveIndex != 0)
        {
            uint pair = curveIndex - 1u;
            const uint linearPair = 32u * 64u + 32u;
            if (pair >= linearPair)
            {
                pair++;
            }

            uint a = pair / 64u;
            uint b = pair & 63u;
            float x = a * 0.03125f - 1.0f;
            float y = b * 0.03125f - 1.0f;
            c1 = Math.Abs(x) * x * 4.0f + (1.0f / 3.0f);
            c2 = Math.Abs(y) * y * 4.0f + (2.0f / 3.0f);
        }

        return b1 * c1 + b2 * c2 + b3;
    }

    private struct BitReader
    {
        public byte[] Data;

        public int Base;

        public int Size;

        public uint CursorBits;

        public void SeekBits(uint bitOffset) => CursorBits = bitOffset;

        public uint Read(uint n)
        {
            if (Data is null || n == 0 || n > 32)
            {
                return 0;
            }

            uint bitPos = CursorBits & 0x1F;
            int wordOff = (int)(CursorBits >> 5) * 4;

            if (wordOff + 4 > Size)
            {
                CursorBits += n;
                return 0;
            }

            uint lo = Engine_AnimReader.ReadU32Be(Data, Base + wordOff);
            uint outValue;

            if (bitPos + n > 32)
            {
                if (wordOff + 8 > Size)
                {
                    CursorBits += n;
                    return 0;
                }

                uint hi = Engine_AnimReader.ReadU32Be(Data, Base + wordOff + 4);
                outValue = (hi << (int)(32 - bitPos)) | (lo >> (int)bitPos);
            }
            else
            {
                outValue = lo >> (int)bitPos;
            }

            if (n < 32)
            {
                outValue &= (1u << (int)n) - 1u;
            }

            CursorBits += n;
            return outValue;
        }
    }

    private bool DecodeBlock(
        Engine_AnimDataFile.ClipHeader header,
        int clipOffset,
        int clipLength,
        uint boneCount,
        uint blockIndex,
        Block outBlock)
    {
        if (boneCount == 0 || boneCount > 4096)
        {
            return false;
        }

        int total = (int)boneCount * SamplesPerBlock * FloatsPerBoneFrame;
        if (outBlock.Data.Length != total)
        {
            outBlock.Data = new float[total];
        }
        else
        {
            Array.Clear(outBlock.Data, 0, total);
        }

        for (uint b = 0; b < boneCount; b++)
        {
            for (int f = 0; f < SamplesPerBlock; f++)
            {
                outBlock.Data[((int)b * SamplesPerBlock + f) * FloatsPerBoneFrame + 3] = 1.0f;
            }
        }

        int bodyOffset = header.PackedBodyOffset;
        if (bodyOffset > clipLength)
        {
            return false;
        }

        var reader = new BitReader
        {
            Data = _file.Blob,
            Base = clipOffset + bodyOffset,
            Size = clipLength - bodyOffset,
        };

        for (uint b = 0; b < header.BoneCount; b++)
        {
            if (b >= header.BoneOffsets.Length)
            {
                break;
            }

            reader.SeekBits(header.BoneOffsets[b]);
            reader.Read(8);
            uint countSel = reader.Read(2);

            bool aborted = false;
            for (uint ch = 0; ch < 7 && !aborted; ch++)
            {
                if (!DecodeChannel(header, ref reader, outBlock, blockIndex, b, ch))
                {
                    aborted = true;
                }
            }

            if (aborted)
            {
                continue;
            }

            for (uint e = 0; e < countSel && !aborted; e++)
            {
                if (!DecodeChannel(header, ref reader, outBlock, blockIndex, uint.MaxValue, uint.MaxValue))
                {
                    aborted = true;
                }
            }
        }

        return true;
    }

    private static void WriteChannelConstant(Block block, uint bone, uint channel, float value)
    {
        if (bone == uint.MaxValue || channel >= FloatsPerBoneFrame)
        {
            return;
        }

        for (int f = 0; f < SamplesPerBlock; f++)
        {
            block.Data[((int)bone * SamplesPerBlock + f) * FloatsPerBoneFrame + channel] = value;
        }
    }

    private bool DecodeChannel(
        Engine_AnimDataFile.ClipHeader header,
        ref BitReader reader,
        Block block,
        uint blockIndex,
        uint bone,
        uint channel)
    {
        uint mode = reader.Read(2);

        switch (mode)
        {
            case 0:
                WriteChannelConstant(block, bone, channel, 0.0f);
                return true;

            case 1:
                WriteChannelConstant(block, bone, channel, 1.0f);
                return true;

            case 2:
            {
                bool useWorld = channel >= 4 && channel != uint.MaxValue;
                uint raw = reader.Read(useWorld ? 24u : 16u);
                float value = useWorld ? DequantWorld24(raw) : DequantUnit(raw);
                WriteChannelConstant(block, bone, channel, value);
                return true;
            }

            case 3:
                return DecodeMode3(
                    header, ref reader, block, blockIndex, bone, channel,
                    channel >= 4 && channel != uint.MaxValue);

            default:
                return false;
        }
    }

    private bool DecodeMode3(
        Engine_AnimDataFile.ClipHeader header,
        ref BitReader reader,
        Block block,
        uint blockIndex,
        uint bone,
        uint channel,
        bool useWorld)
    {
        uint start = reader.CursorBits;
        uint firstBits = useWorld ? 24u : 16u;
        uint firstRaw = reader.Read(firstBits);
        float firstValue = useWorld
            ? DequantWorldBits(firstRaw, firstBits)
            : DequantUnitBits(firstRaw, firstBits);

        uint mid = start + firstBits;
        reader.SeekBits(mid);
        uint scaleRaw = reader.Read(12);
        float valueScale = scaleRaw * 0.00001f;
        uint packed = reader.Read(9);
        uint indexBits = packed & 0xFu;
        uint valueBits = packed >> 4;

        uint segmentCount = 0;
        uint indexPos = mid + 21u;
        reader.SeekBits(indexPos);
        if (indexBits != 0)
        {
            segmentCount = reader.Read(indexBits);
        }

        uint initialValueInt = valueBits != 0 ? reader.Read(valueBits) : 0u;

        uint framePageCount = header.FrameCount > 1 ? (header.FrameCount - 1u) >> 5 : 0u;
        uint pageTablePos = indexPos + indexBits + valueBits;
        uint segmentPos = pageTablePos + (indexBits + header.BoneIndexBits) * framePageCount;
        uint finalPos = segmentCount * (valueBits + 17u) + segmentPos;

        if ((finalPos + 7u) / 8u > (uint)reader.Size)
        {
            return false;
        }

        BitReader scan = reader;

        uint ReadBitsAt(uint bitOffset, uint count)
        {
            scan.SeekBits(bitOffset);
            return scan.Read(count);
        }

        float ValueFromInt(uint value) => firstValue + valueScale * value;

        uint ReadSegmentTarget(uint segmentIndex)
        {
            uint cursor = segmentPos + segmentIndex * (valueBits + 17u);
            return valueBits != 0 ? ReadBitsAt(cursor + 17u, valueBits) : 0u;
        }

        uint blockStart = blockIndex * BlockStride;
        if (blockStart >= header.FrameCount)
        {
            reader.SeekBits(finalPos);
            return true;
        }

        uint currentSegment = 0;
        uint prevFrame = 0;
        uint prevValueInt = initialValueInt;

        uint page = blockStart >> 5;
        if (page > 0)
        {
            uint pageRecord = pageTablePos + (page - 1u) * (indexBits + header.BoneIndexBits);
            currentSegment = indexBits != 0 ? ReadBitsAt(pageRecord, indexBits) : 0u;
            if (currentSegment > segmentCount)
            {
                currentSegment = segmentCount;
            }

            prevFrame = header.BoneIndexBits != 0
                ? ReadBitsAt(pageRecord + indexBits, header.BoneIndexBits)
                : 0u;

            prevValueInt = currentSegment != 0
                ? ReadSegmentTarget(currentSegment - 1u)
                : initialValueInt;
        }

        uint cursorBits = segmentPos + currentSegment * (valueBits + 17u);
        int written = 0;
        var frameWritten = new bool[SamplesPerBlock];

        while (currentSegment < segmentCount &&
               cursorBits + 17u <= finalPos &&
               written < SamplesPerBlock)
        {
            uint packedSegment = ReadBitsAt(cursorBits, 17);
            cursorBits += 17u;
            uint run = (packedSegment & 0x1Fu) + 1u;
            uint curveIndex = packedSegment >> 5;
            uint targetValueInt = valueBits != 0 ? ReadBitsAt(cursorBits, valueBits) : 0u;
            cursorBits += valueBits;

            uint nextFrame = prevFrame + run;
            float prevValue = ValueFromInt(prevValueInt);
            float nextValue = ValueFromInt(targetValueInt);

            for (uint f = 0; f < SamplesPerBlock; f++)
            {
                uint absFrame = blockStart + f;
                if (absFrame >= header.FrameCount)
                {
                    continue;
                }

                if (absFrame < prevFrame || absFrame > nextFrame)
                {
                    continue;
                }

                float t = Mode3CurveFactor(curveIndex, absFrame - prevFrame, run);
                float value = prevValue + (nextValue - prevValue) * t;

                if (bone != uint.MaxValue && channel < FloatsPerBoneFrame)
                {
                    block.Data[((int)bone * SamplesPerBlock + (int)f) * FloatsPerBoneFrame + channel] = value;
                }

                if (!frameWritten[f])
                {
                    frameWritten[f] = true;
                    written++;
                }
            }

            prevFrame = nextFrame;
            prevValueInt = targetValueInt;
            currentSegment++;
        }

        if (written == 0)
        {
            WriteChannelConstant(block, bone, channel, ValueFromInt(prevValueInt));
        }
        else if (bone != uint.MaxValue && channel < FloatsPerBoneFrame)
        {
            for (uint f = 0; f < SamplesPerBlock; f++)
            {
                uint absFrame = blockStart + f;
                if (absFrame >= header.FrameCount || frameWritten[f])
                {
                    continue;
                }

                block.Data[((int)bone * SamplesPerBlock + (int)f) * FloatsPerBoneFrame + channel] =
                    ValueFromInt(prevValueInt);
            }
        }

        reader.SeekBits(finalPos);
        return true;
    }

    private static void Interpolate(Block block, float fracFrame, DecodedPose pose)
    {
        int bones = (int)block.BoneCount;

        if (pose.BoneQuats.Length != bones * 4)
        {
            pose.BoneQuats = new float[bones * 4];
        }

        if (pose.BoneTrans.Length != bones * 3)
        {
            pose.BoneTrans = new float[bones * 3];
        }

        pose.BoneCount = bones;

        int f0 = (int)fracFrame;
        if (f0 >= SamplesPerBlock - 1)
        {
            f0 = SamplesPerBlock - 2;
        }

        int f1 = f0 + 1;
        float t = fracFrame - f0;
        float it = 1.0f - t;

        for (int b = 0; b < bones; b++)
        {
            int aBase = (b * SamplesPerBlock + f0) * FloatsPerBoneFrame;
            int bBase = (b * SamplesPerBlock + f1) * FloatsPerBoneFrame;

            float ax = block.Data[aBase + 0];
            float ay = block.Data[aBase + 1];
            float az = block.Data[aBase + 2];
            float aw = block.Data[aBase + 3];
            float bx = block.Data[bBase + 0];
            float by = block.Data[bBase + 1];
            float bz = block.Data[bBase + 2];
            float bw = block.Data[bBase + 3];

            if (ax * bx + ay * by + az * bz + aw * bw < 0.0f)
            {
                bx = -bx;
                by = -by;
                bz = -bz;
                bw = -bw;
            }

            float qx = ax * it + bx * t;
            float qy = ay * it + by * t;
            float qz = az * it + bz * t;
            float qw = aw * it + bw * t;

            float lengthSquared = qx * qx + qy * qy + qz * qz + qw * qw;
            float inverse = lengthSquared > 1e-12f ? 1.0f / MathF.Sqrt(lengthSquared) : 1.0f;

            pose.BoneQuats[b * 4 + 0] = qx * inverse;
            pose.BoneQuats[b * 4 + 1] = qy * inverse;
            pose.BoneQuats[b * 4 + 2] = qz * inverse;
            pose.BoneQuats[b * 4 + 3] = qw * inverse;

            pose.BoneTrans[b * 3 + 0] = block.Data[aBase + 4] * it + block.Data[bBase + 4] * t;
            pose.BoneTrans[b * 3 + 1] = block.Data[aBase + 5] * it + block.Data[bBase + 5] * t;
            pose.BoneTrans[b * 3 + 2] = block.Data[aBase + 6] * it + block.Data[bBase + 6] * t;
        }
    }
}
