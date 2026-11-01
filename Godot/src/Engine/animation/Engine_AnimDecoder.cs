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

    private sealed class HeaderSlot
    {
        public int ClipOffset = -1;

        public Engine_AnimDataFile.ClipHeader? Header;
    }

    private readonly Block[] _cache = new Block[CacheBuckets];

    private readonly HeaderSlot[] _headers = new HeaderSlot[256];

    private readonly Engine_AnimDataFile _file;

    public Engine_AnimDecoder(Engine_AnimDataFile file)
    {
        _file = file;
        for (int i = 0; i < _cache.Length; i++)
        {
            _cache[i] = new Block();
        }
        for (int i = 0; i < _headers.Length; i++)
        {
            _headers[i] = new HeaderSlot();
        }
    }

    public void ClearCache()
    {
        for (int i = 0; i < _cache.Length; i++)
        {
            lock (_cache[i])
            {
                _cache[i].ClipOffset = -1;
            }
        }
        for (int i = 0; i < _headers.Length; i++)
        {
            lock (_headers[i])
            {
                _headers[i].ClipOffset = -1;
                _headers[i].Header = null;
            }
        }
    }

    public bool Decode(AnimClip clip, float timeSeconds, DecodedPose pose)
    {
        pose.Ok = false;

        if (!_file.IsOpen || clip.Fps <= 0.0f)
        {
            return false;
        }

        if (!_file.TryGetClipSpan(clip, out int offset, out int length))
        {
            return false;
        }

        HeaderSlot hSlot = _headers[((uint)offset >> 5) & 255u];
        Engine_AnimDataFile.ClipHeader header;
        lock (hSlot)
        {
            if (hSlot.ClipOffset == offset && hSlot.Header is not null)
            {
                header = hSlot.Header;
            }
            else
            {
                header = _file.ParseClipHeader(clip);
                hSlot.ClipOffset = offset;
                hSlot.Header = header;
            }
        }

        if (!header.Ok || header.BoneCount == 0)
        {
            return false;
        }

        if (header.BoneCount > 4096 || header.FrameCount > 65536)
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

        int bucket = (int)((((uint)offset >> 4) ^ blockIndex) & (CacheBuckets - 1));
        Block slot = _cache[bucket];

        lock (slot)
        {
            bool hit = slot.ClipOffset == offset &&
                       slot.BlockIndex == blockIndex &&
                       slot.BoneCount == header.BoneCount;

            if (!hit)
            {
                if (!DecodeBlock(header, offset, length, header.BoneCount, blockIndex, slot))
                {
                    slot.ClipOffset = -1;
                    return false;
                }

                slot.ClipOffset = offset;
                slot.BlockIndex = blockIndex;
                slot.BoneCount = header.BoneCount;
            }

            Interpolate(slot, fracInBlock, pose);
        }

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
            // Fable 2 seeks each bone to its directory entry; a Fable 3 body has no directory and
            // the cursor simply runs on (sub_821D4480: one cursor, `v54`, across every bone), so
            // a channel that cannot be walked ends the block rather than the bone.
            if (header.Sequential)
            {
                if (reader.CursorBits >= (uint)reader.Size * 8u)
                {
                    break;
                }
            }
            else
            {
                if (b >= header.BoneOffsets.Length)
                {
                    break;
                }

                reader.SeekBits(header.BoneOffsets[b]);
            }

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

            for (uint e = 0; e < countSel && !aborted; e++)
            {
                if (!DecodeChannel(header, ref reader, outBlock, blockIndex, uint.MaxValue, uint.MaxValue))
                {
                    aborted = true;
                }
            }

            if (aborted && header.Sequential)
            {
                break;
            }
        }

        RelocaliseModelSpaceTracks(header, outBlock);
        return true;
    }

    /// <summary>
    /// THE HANDS AND FEET ARE STORED IN MODEL SPACE. A Fable 3 body names up to four tracks at
    /// body+6..+9 (the biped's {8, 12, 15, 19}: hands and feet) whose samples are the bone's
    /// MODEL-space transform — a hand at hip height, ±0.27 m from the spine, in idle_rf_var001
    /// frame 0 — not a local one; the guest's <c>sub_82D4B428</c> composes the parent chain to
    /// the root with <c>sub_8222AD80</c> (0x82d4b... loop until the parent tag is 255) and then
    /// <c>sub_82D4AFD8</c> brings the bone under it: <c>q = conj(parent) ⊗ q</c>,
    /// <c>t = conj(parent)·(t − parent.t)</c>. Left as stored, a Godot skeleton that composes
    /// locals put each hand a metre from the wrist. Done per sample here, so every consumer of a
    /// decoded block sees locals, as it does for every other track.
    /// </summary>
    private static void RelocaliseModelSpaceTracks(Engine_AnimDataFile.ClipHeader header, Block block)
    {
        // The block's own BoneCount is stamped by Decode only after this returns (a fresh slot
        // reads 0 here); the header's count is what sized the block's data.
        if (header.ModelSpaceTracks.Length == 0 || header.Parents.Length == 0)
        {
            return;
        }

        Span<float> parentQ = stackalloc float[4];
        Span<float> parentT = stackalloc float[3];
        Span<float> q = stackalloc float[4];
        Span<float> t = stackalloc float[3];

        for (int f = 0; f < SamplesPerBlock; f++)
        {
            foreach (int track in header.ModelSpaceTracks)
            {
                if (track <= 0 || track >= header.Parents.Length || (uint)track >= header.BoneCount)
                {
                    continue;
                }

                int parent = header.Parents[track];
                if (parent < 0 || (uint)parent >= header.BoneCount)
                {
                    continue;
                }

                if (!ComposeToRoot(header, block, parent, f, parentQ, parentT))
                {
                    continue;
                }

                int at = (track * SamplesPerBlock + f) * FloatsPerBoneFrame;
                for (int i = 0; i < 4; i++)
                {
                    q[i] = block.Data[at + i];
                }

                for (int i = 0; i < 3; i++)
                {
                    t[i] = block.Data[at + 4 + i];
                }

                Localise(parentQ, parentT, q, t);
                for (int i = 0; i < 4; i++)
                {
                    block.Data[at + i] = q[i];
                }

                for (int i = 0; i < 3; i++)
                {
                    block.Data[at + 4 + i] = t[i];
                }
            }
        }
    }

    /// <summary>
    /// The model-space transform of <paramref name="bone"/> at sample <paramref name="f"/>: the
    /// chain's locals composed root-first, parent ∘ child, with no scale term (<c>sub_82D4E7C0</c>
    /// and <c>sub_8222AD80</c> read quaternion and translation only). False when a model-space
    /// track sits on the chain itself, whose local is not known until its own pass.
    /// </summary>
    private static bool ComposeToRoot(
        Engine_AnimDataFile.ClipHeader header, Block block, int bone, int f, Span<float> outQ, Span<float> outT)
    {
        Span<int> chain = stackalloc int[256];
        int depth = 0;
        for (int b = bone; b >= 0 && depth < chain.Length; b = header.Parents[b])
        {
            if ((uint)b >= header.BoneCount || Array.IndexOf(header.ModelSpaceTracks, b) >= 0)
            {
                return false;
            }

            chain[depth++] = b;
        }

        outQ[0] = 0f;
        outQ[1] = 0f;
        outQ[2] = 0f;
        outQ[3] = 1f;
        outT[0] = 0f;
        outT[1] = 0f;
        outT[2] = 0f;

        Span<float> childQ = stackalloc float[4];
        Span<float> childT = stackalloc float[3];
        for (int i = depth - 1; i >= 0; i--)
        {
            int at = (chain[i] * SamplesPerBlock + f) * FloatsPerBoneFrame;
            for (int k = 0; k < 4; k++)
            {
                childQ[k] = block.Data[at + k];
            }

            for (int k = 0; k < 3; k++)
            {
                childT[k] = block.Data[at + 4 + k];
            }

            Compose(outQ, outT, childQ, childT);
        }

        return true;
    }

    /// <summary>parent ∘ child in place: q = p ⊗ c, t = p.t + rotate(p.q, c.t).</summary>
    internal static void Compose(Span<float> pq, Span<float> pt, ReadOnlySpan<float> cq, ReadOnlySpan<float> ct)
    {
        Span<float> rotated = stackalloc float[3];
        Rotate(pq, ct, rotated);
        pt[0] += rotated[0];
        pt[1] += rotated[1];
        pt[2] += rotated[2];

        float x = pq[3] * cq[0] + pq[0] * cq[3] + pq[1] * cq[2] - pq[2] * cq[1];
        float y = pq[3] * cq[1] - pq[0] * cq[2] + pq[1] * cq[3] + pq[2] * cq[0];
        float z = pq[3] * cq[2] + pq[0] * cq[1] - pq[1] * cq[0] + pq[2] * cq[3];
        float w = pq[3] * cq[3] - pq[0] * cq[0] - pq[1] * cq[1] - pq[2] * cq[2];
        pq[0] = x;
        pq[1] = y;
        pq[2] = z;
        pq[3] = w;
    }

    /// <summary>A model-space (q, t) under the parent's model-space (pq, pt), in place.</summary>
    internal static void Localise(ReadOnlySpan<float> pq, ReadOnlySpan<float> pt, Span<float> q, Span<float> t)
    {
        Span<float> conj = stackalloc float[4];
        conj[0] = -pq[0];
        conj[1] = -pq[1];
        conj[2] = -pq[2];
        conj[3] = pq[3];

        Span<float> delta = stackalloc float[3];
        delta[0] = t[0] - pt[0];
        delta[1] = t[1] - pt[1];
        delta[2] = t[2] - pt[2];
        Rotate(conj, delta, t);

        float x = conj[3] * q[0] + conj[0] * q[3] + conj[1] * q[2] - conj[2] * q[1];
        float y = conj[3] * q[1] - conj[0] * q[2] + conj[1] * q[3] + conj[2] * q[0];
        float z = conj[3] * q[2] + conj[0] * q[1] - conj[1] * q[0] + conj[2] * q[3];
        float w = conj[3] * q[3] - conj[0] * q[0] - conj[1] * q[1] - conj[2] * q[2];
        q[0] = x;
        q[1] = y;
        q[2] = z;
        q[3] = w;
    }

    /// <summary>v rotated by the unit quaternion q (xyzw): v + 2·w·(q×v) + 2·q×(q×v).</summary>
    private static void Rotate(ReadOnlySpan<float> q, ReadOnlySpan<float> v, Span<float> result)
    {
        float cx = q[1] * v[2] - q[2] * v[1];
        float cy = q[2] * v[0] - q[0] * v[2];
        float cz = q[0] * v[1] - q[1] * v[0];
        float ccx = q[1] * cz - q[2] * cy;
        float ccy = q[2] * cx - q[0] * cz;
        float ccz = q[0] * cy - q[1] * cx;
        result[0] = v[0] + 2f * (q[3] * cx + ccx);
        result[1] = v[1] + 2f * (q[3] * cy + ccy);
        result[2] = v[2] + 2f * (q[3] * cz + ccz);
    }

    /// <summary>
    /// Whether a channel is stored at the translation width (24 bits) rather than the unit width
    /// (16): translation channels 4..6 always; the extra channels past the seventh
    /// (<c>channel == uint.MaxValue</c>) only in a Fable 3 body (sub_821D4480 reads them with
    /// `v54 += 24`; every one of the 1,672 shipped clips that has one closes on 24, none on 16).
    /// </summary>
    private static bool WorldWidth(Engine_AnimDataFile.ClipHeader header, uint channel) =>
        channel == uint.MaxValue ? header.WideExtraChannels : channel >= 4;

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
                bool useWorld = WorldWidth(header, channel);
                uint raw = reader.Read(useWorld ? 24u : 16u);
                float value = useWorld ? DequantWorld24(raw) : DequantUnit(raw);
                WriteChannelConstant(block, bone, channel, value);
                return true;
            }

            case 3:
                return DecodeMode3(
                    header, ref reader, block, blockIndex, bone, channel, WorldWidth(header, channel));

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
