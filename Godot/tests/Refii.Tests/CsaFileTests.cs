using System;
using System.Collections.Generic;
using System.Text;
using GdUnit4;
using Refii.Animations;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Engine_CsaFile against a synthetic AnimFile v8 buffer laid out as sub_82D51D90 reads one.</summary>
[TestSuite]
public sealed class CsaFileTests
{
    private static void U16(List<byte> b, int v)
    {
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static void U32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24));
        b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    private static void F32(List<byte> b, float v) => U32(b, BitConverter.SingleToUInt32Bits(v));

    /// <summary>Two bones, one tier, two events, a 33-frame body: every field lands where the guest reads it.</summary>
    private static byte[] Synthetic(ushort version = 8, int bodyBones = 2, byte marker = 0xAD)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("AnimFile"));
        U16(b, version);
        b.Add(0);
        b.Add(0);
        U32(b, 0);
        U32(b, 1);
        F32(b, 30.0f);
        U16(b, 33);
        U16(b, 2);
        U32(b, 0);
        U32(b, 0);
        U32(b, 0);
        U32(b, 0);
        U32(b, 1);
        U16(b, 1);
        U16(b, 2);
        U32(b, 0);
        U32(b, 0);
        U32(b, 0x2A4AD776u);
        U32(b, 2);
        U32(b, 0x2897988Du);
        U32(b, 0xFFFFFFFFu);
        U32(b, 0x6DAD352Au);
        U32(b, 0);
        F32(b, 0.01f);
        U16(b, 0);
        U16(b, 16);
        F32(b, 0.538f);
        U16(b, 16);
        U16(b, 12);
        b.AddRange(Encoding.ASCII.GetBytes("SE_FOOTSTEP;WALK"));
        b.AddRange(Encoding.ASCII.GetBytes("SE_MVMT_SOFT"));
        while (b.Count % 16 != 0)
        {
            b.Add(0);
        }

        int body = b.Count;
        b.Add(marker);
        b.Add(0xFF);
        b.Add((byte)bodyBones);
        b.Add((byte)Engine_CsaFile.FrameIndexBitsFor(33));
        U16(b, 33);
        b.AddRange(new byte[] { 8, 12, 15, 19 });
        for (int i = 0; i < 16; i++)
        {
            U32(b, 0);
        }

        AssertThat(body % 16).IsEqual(0);
        return b.ToArray();
    }

    [TestCase]
    public void ParsesHeaderSkeletonEventsAndBody()
    {
        byte[] bytes = Synthetic();
        AssertThat(Engine_CsaFile.TryParse(bytes, out Engine_CsaFile.Clip clip, out string error)).IsTrue();
        AssertThat(error).IsEmpty();
        AssertThat(clip.Fps).IsEqual(30.0f);
        AssertThat(clip.FrameCount).IsEqual(33);
        AssertThat(clip.DurationSeconds).IsEqual(32.0f / 30.0f);
        AssertThat(clip.Tiers.Count).IsEqual(1);
        AssertThat(clip.Tiers[0].Hash).IsEqual(0x2A4AD776u);
        AssertThat(clip.Tiers[0].BoneCount).IsEqual(2u);
        AssertThat(clip.Bones.Count).IsEqual(2);
        AssertThat(clip.Bones[0].NameHash).IsEqual(0x2897988Du);
        AssertThat(clip.Bones[0].Parent).IsEqual(-1);
        AssertThat(clip.Bones[1].NameHash).IsEqual(0x6DAD352Au);
        AssertThat(clip.Bones[1].Parent).IsEqual(0);
        AssertThat(clip.Events.Count).IsEqual(2);
        AssertThat(clip.Events[0].Name).IsEqual("SE_FOOTSTEP;WALK");
        AssertThat(clip.Events[1].Name).IsEqual("SE_MVMT_SOFT");
        AssertThat(clip.Events[1].Time).IsEqual(0.538f);
        AssertThat(clip.BodyOffset % 16).IsEqual(0);
        AssertThat(bytes[clip.BodyOffset]).IsEqual(Engine_CsaFile.BodyMarker);
        AssertThat(clip.RootByte).IsEqual((byte)0xFF);
        AssertThat(clip.FrameIndexBits).IsEqual(6);
        AssertThat(clip.SpecialBones).IsEqual(new byte[] { 8, 12, 15, 19 });
        AssertThat(clip.BitstreamOffset).IsEqual(clip.BodyOffset + Engine_CsaFile.BodyPrefixBytes);
        AssertThat(Engine_CsaFile.BoneName(clip.Bones[1].NameHash)).IsEqual("0x6DAD352A");
    }

    /// <summary>The guest's only gate is the version (sub_82D51BD0 at 0x82d51cd0).</summary>
    [TestCase]
    public void RejectsOtherVersions()
    {
        AssertThat(Engine_CsaFile.TryParse(Synthetic(version: 7), out _, out string error)).IsFalse();
        AssertThat(error).Contains("version 7");
    }

    [TestCase]
    public void RejectsBodyThatDisagreesWithTheSkeleton()
    {
        AssertThat(Engine_CsaFile.TryParse(Synthetic(bodyBones: 3), out _, out string error)).IsFalse();
        AssertThat(error).Contains("3 bones");
        AssertThat(Engine_CsaFile.TryParse(Synthetic(marker: 0x00), out _, out error)).IsFalse();
        AssertThat(error).Contains("marker");
    }

    [TestCase]
    public void ParsesAtAnOffsetInsideALargerBlob()
    {
        byte[] one = Synthetic();
        var blob = new byte[one.Length * 2];
        Array.Copy(one, 0, blob, one.Length, one.Length);
        AssertThat(Engine_CsaFile.HasMagic(blob, one.Length)).IsTrue();
        AssertThat(Engine_CsaFile.HasMagic(blob, 0)).IsFalse();
        AssertThat(Engine_CsaFile.TryParse(blob, one.Length, one.Length, out Engine_CsaFile.Clip clip, out _)).IsTrue();
        AssertThat(clip.BodyOffset).IsGreaterEqual(one.Length);
        AssertThat((clip.BodyOffset - one.Length) % 16).IsEqual(0);
    }

    /// <summary>A bit writer for the body's codec: LSB-first within big-endian words (Engine_AnimDecoder.BitReader).</summary>
    private sealed class BitWriter
    {
        private readonly List<uint> _words = new();
        private int _bits;

        public void Write(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int word = _bits >> 5;
                while (_words.Count <= word)
                {
                    _words.Add(0u);
                }

                if (((value >> i) & 1u) != 0u)
                {
                    _words[word] |= 1u << (_bits & 31);
                }

                _bits++;
            }
        }

        public void Bytes(List<byte> into)
        {
            foreach (uint w in _words)
            {
                U32(into, w);
            }

            U32(into, 0u);
        }
    }

    private static uint UnitRaw(float v) => (uint)Math.Round((v + 1.0) * 65535.0 / 2.0);

    private static uint WorldRaw(float v) => (uint)Math.Round((v + 256.0) * ((1 << 24) - 1) / 512.0);

    /// <summary>mode 2 constant channel at the unit (16-bit) or world (24-bit) width; 0 and 1 as modes 0/1.</summary>
    private static void Channel(BitWriter w, float v, bool world)
    {
        if (v == 0f)
        {
            w.Write(0u, 2);
            return;
        }

        if (v == 1f)
        {
            w.Write(1u, 2);
            return;
        }

        w.Write(2u, 2);
        w.Write(world ? WorldRaw(v) : UnitRaw(v), world ? 24 : 16);
    }

    /// <summary>
    /// A root, a forearm turned 90° about z at x = 0.26, and a hand the body stores in MODEL space
    /// (special byte 2) — the guest's hands and feet layout. Decoded, the hand is LOCAL under the
    /// forearm: (0.219, 0, 0) with no rotation, the bind length a hand has in ch_heromale_morph_body.
    /// </summary>
    private static byte[] SyntheticWithModelSpaceHand(byte rootByte = 0xFF, byte[]? special = null)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("AnimFile"));
        U16(b, 8);
        b.Add(0);
        b.Add(0);
        U32(b, 0);
        U32(b, 1);
        F32(b, 30.0f);
        U16(b, 2);
        U16(b, 0);
        U32(b, 0);
        U32(b, 0);
        U32(b, 0);
        U32(b, 0);
        U32(b, 1);
        U16(b, 1);
        U16(b, 3);
        U32(b, 0);
        U32(b, 0);
        U32(b, 0x11111111u);
        U32(b, 3);
        U32(b, 0x2897988Du);
        U32(b, 0xFFFFFFFFu);
        U32(b, 0x69428AE4u);
        U32(b, 0);
        U32(b, 0x25AC77B2u);
        U32(b, 1);
        while (b.Count % 16 != 0)
        {
            b.Add(0);
        }

        b.Add(0xAD);
        b.Add(rootByte);
        b.Add(3);
        b.Add((byte)Engine_CsaFile.FrameIndexBitsFor(2));
        U16(b, 2);
        b.AddRange(special ?? new byte[] { 2, 0xFF, 0xFF, 0xFF });

        const float half = 0.70710677f;
        var w = new BitWriter();
        // root: tag 255, no extras, identity.
        w.Write(255u, 8);
        w.Write(0u, 2);
        Channel(w, 0f, false);
        Channel(w, 0f, false);
        Channel(w, 0f, false);
        Channel(w, 1f, false);
        Channel(w, 0f, true);
        Channel(w, 0f, true);
        Channel(w, 0f, true);
        // forearm: tag 0, q = (0, 0, s, s) (90° about z), t = (0.26, 0, 0).
        w.Write(0u, 8);
        w.Write(0u, 2);
        Channel(w, 0f, false);
        Channel(w, 0f, false);
        Channel(w, half, false);
        Channel(w, half, false);
        Channel(w, 0.26f, true);
        Channel(w, 0f, true);
        Channel(w, 0f, true);
        // hand, MODEL space: q = forearm's, t = (0.26, 0, 0) + rotate(q, (0.219, 0, 0)) = (0.26, 0.219, 0).
        w.Write(1u, 8);
        w.Write(0u, 2);
        Channel(w, 0f, false);
        Channel(w, 0f, false);
        Channel(w, half, false);
        Channel(w, half, false);
        Channel(w, 0.26f, true);
        Channel(w, 0.219f, true);
        Channel(w, 0f, true);
        w.Bytes(b);
        return b.ToArray();
    }

    [TestCase]
    public void DecoderBringsModelSpaceHandUnderItsParent()
    {
        byte[] bytes = SyntheticWithModelSpaceHand();
        AssertThat(Engine_CsaFile.TryParse(bytes, out Engine_CsaFile.Clip parsed, out string error)).IsTrue();
        AssertThat(error).IsEmpty();
        AssertThat(parsed.SpecialBones[0]).IsEqual((byte)2);

        var file = new Engine_AnimDataFile();
        file.Attach(bytes);
        var clip = new AnimClip
        {
            Key0 = 1u, DataOffset = 0u, DataSizeBytes = (uint)bytes.Length, Fps = 30.0f, TocFrameCount = 2u,
        };
        Engine_AnimDataFile.ClipHeader header = file.ParseClipHeader(clip);
        AssertThat(header.Ok).IsTrue();
        AssertThat(header.Sequential).IsTrue();
        AssertThat(header.BoneCount).IsEqual(3u);
        AssertThat(header.ModelSpaceTracks).IsEqual(new[] { 2 });
        AssertThat(header.Parents).IsEqual(new[] { -1, 0, 1 });

        var decoder = new Engine_AnimDecoder(file);
        var pose = new DecodedPose();
        AssertThat(decoder.Decode(clip, 0.0f, pose)).IsTrue();
        AssertThat(pose.BoneCount).IsEqual(3);

        // The forearm as stored.
        AssertThat(pose.BoneTrans[3]).IsEqualApprox(0.26f, 2e-4f);
        AssertThat(pose.BoneQuats[4 + 2]).IsEqualApprox(0.70710677f, 2e-4f);
        AssertThat(pose.BoneQuats[4 + 3]).IsEqualApprox(0.70710677f, 2e-4f);

        // The hand re-localised: the bind length along the forearm's own x, and no rotation.
        AssertThat(pose.BoneTrans[6]).IsEqualApprox(0.219f, 2e-3f);
        AssertThat(pose.BoneTrans[7]).IsEqualApprox(0.0f, 2e-3f);
        AssertThat(pose.BoneTrans[8]).IsEqualApprox(0.0f, 2e-3f);
        AssertThat(Math.Abs(pose.BoneQuats[8 + 3])).IsEqualApprox(1.0f, 2e-3f);
        AssertThat(Math.Abs(pose.BoneQuats[8 + 2])).IsLess(2e-3f);
    }

    private static Engine_AnimDataFile.ClipHeader HeaderOf(byte[] bytes)
    {
        var file = new Engine_AnimDataFile();
        file.Attach(bytes);
        var clip = new AnimClip
        {
            Key0 = 1u, DataOffset = 0u, DataSizeBytes = (uint)bytes.Length, Fps = 30.0f, TocFrameCount = 2u,
        };
        return file.ParseClipHeader(clip);
    }

    /// <summary>
    /// The root-motion bone (body+1) is model space like body+6..+9: sub_82D4B178 puts it in the
    /// same list sub_82D4B428 re-localises, so a clip naming the hand only there decodes the same.
    /// </summary>
    [TestCase]
    public void RootMotionBoneIsReLocalisedToo()
    {
        byte[] bytes = SyntheticWithModelSpaceHand(2, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Engine_AnimDataFile.ClipHeader header = HeaderOf(bytes);
        AssertThat(header.ModelSpaceTracks).IsEqual(new[] { 2 });

        var file = new Engine_AnimDataFile();
        file.Attach(bytes);
        var clip = new AnimClip
        {
            Key0 = 1u, DataOffset = 0u, DataSizeBytes = (uint)bytes.Length, Fps = 30.0f, TocFrameCount = 2u,
        };
        var pose = new DecodedPose();
        AssertThat(new Engine_AnimDecoder(file).Decode(clip, 0.0f, pose)).IsTrue();
        AssertThat(pose.BoneTrans[6]).IsEqualApprox(0.219f, 2e-3f);
        AssertThat(pose.BoneTrans[7]).IsEqualApprox(0.0f, 2e-3f);
    }

    /// <summary>The set is deduplicated and ascending, as sub_82D4B178's bubble sort leaves it.</summary>
    [TestCase]
    public void ModelSpaceSetIsSortedAndUnique()
    {
        Engine_AnimDataFile.ClipHeader header = HeaderOf(SyntheticWithModelSpaceHand(2, new byte[] { 2, 1, 0xFF, 1 }));
        AssertThat(header.ModelSpaceTracks).IsEqual(new[] { 1, 2 });
    }

    /// <summary>Compose then Localise is the identity on a model-space transform — the guest's sub_8222AD80 / sub_82D4AFD8 pair.</summary>
    [TestCase]
    public void ComposeAndLocaliseAreInverses()
    {
        float[] pq = { 0.1f, 0.2f, 0.3f, 0.9273618f };
        float[] pt = { 0.5f, -0.25f, 1.0f };
        float[] cq = { 0.0f, 0.70710677f, 0.0f, 0.70710677f };
        float[] ct = { 0.219f, 0.01f, -0.02f };

        float[] gq = (float[])pq.Clone();
        float[] gt = (float[])pt.Clone();
        Engine_AnimDecoder.Compose(gq, gt, cq, ct);

        float[] lq = (float[])gq.Clone();
        float[] lt = (float[])gt.Clone();
        Engine_AnimDecoder.Localise(pq, pt, lq, lt);

        for (int i = 0; i < 4; i++)
        {
            AssertThat(lq[i]).IsEqualApprox(cq[i], 1e-5f);
        }

        for (int i = 0; i < 3; i++)
        {
            AssertThat(lt[i]).IsEqualApprox(ct[i], 1e-5f);
        }
    }

    [TestCase]
    public void FrameIndexBitsMatchTheWriter()
    {
        AssertThat(Engine_CsaFile.FrameIndexBitsFor(1)).IsEqual(1);
        AssertThat(Engine_CsaFile.FrameIndexBitsFor(2)).IsEqual(2);
        AssertThat(Engine_CsaFile.FrameIndexBitsFor(33)).IsEqual(6);
        AssertThat(Engine_CsaFile.FrameIndexBitsFor(89)).IsEqual(7);
    }
}
