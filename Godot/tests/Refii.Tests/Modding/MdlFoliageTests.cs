namespace Refii.Tests.Modding;

using System;
using System.Buffers.Binary;
using System.IO;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

/// <summary>
/// Regression tests for <see cref="Engine_MdlFoliage"/> multi-part TypeC ground foliage parsing
/// (such as <c>RS_Rose_01.mdl</c>, <c>RS_Rose_02.mdl</c>, and toadstool/turnip models that place
/// a 2-byte <c>00 01</c> separator between mesh parts).
/// </summary>
[TestSuite]
public class MdlFoliageTests
{
    private static void WriteBeU16(Stream s, ushort v)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteBeU32(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, v);
        s.Write(buf);
    }

    private static byte[] BuildSyntheticTwoPartGroundFoliageMdl(uint vCount0, uint iCount0, uint vCount1, uint iCount1)
    {
        using var ms = new MemoryStream();

        // Pre-bone block (8 * u32)
        ms.Write(new byte[32]);
        WriteBeU32(ms, 0); // boneCount
        WriteBeU32(ms, 0); // transformCount
        ms.Write(new byte[40]); // bounds (10 * f32)
        WriteBeU32(ms, 2); // meshCount = 2
        ms.Write(new byte[8 + 12]); // post-meshCount + pre-tag
        ms.WriteByte(0); // tagCount = 0
        ms.Write(new byte[20]); // post-tag
        WriteBeU32(ms, 0); // unk6
        WriteBeU32(ms, 0); // stringBlockCount

        // Two minimal mesh records
        for (int m = 0; m < 2; m++)
        {
            WriteBeU32(ms, 0);
            ms.Write(System.Text.Encoding.ASCII.GetBytes($"part{m}\0"));
            ms.Write(new byte[4 + 4 + 21 + 4 + 12]); // 45-byte scalar block
            WriteBeU32(ms, 0); // materialCount = 0
        }

        // Foliage body (TypeC, !hasClassTag): starts directly with the 2-byte prefix (0x00 is non-alpha so ReadClassTag returns false)
        WriteBeU16(ms, 0x0001);

        // Mesh 0: counts + 48B vertices + u16 indices
        WriteBeU32(ms, 0);
        WriteBeU32(ms, 0);
        WriteBeU32(ms, iCount0);
        WriteBeU32(ms, vCount0);
        ms.Write(new byte[vCount0 * 48]);
        ms.Write(new byte[iCount0 * 2]);

        // 2-byte inter-mesh separator present in multi-part TypeC ground foliage
        WriteBeU16(ms, 0x0001);

        // Mesh 1: counts + 48B vertices + u16 indices
        WriteBeU32(ms, 0);
        WriteBeU32(ms, 0);
        WriteBeU32(ms, iCount1);
        WriteBeU32(ms, vCount1);
        ms.Write(new byte[vCount1 * 48]);
        ms.Write(new byte[iCount1 * 2]);

        return ms.ToArray();
    }

    [TestCase]
    public void MultiPartGroundFoliageWithoutClassTagParsesAllMeshPartsAcrossSeparator()
    {
        byte[] data = BuildSyntheticTwoPartGroundFoliageMdl(vCount0: 12, iCount0: 18, vCount1: 8, iCount1: 12);

        bool ok = Engine_MdlData.TryParseInfo(
            data, "Art/Environment/Foliage/dotXSI/RS_Rose_01/RS_Rose_01.mdl",
            out MdlInfo info, out string error);

        AssertThat(ok).IsTrue();
        AssertThat(error).IsEqual(string.Empty);
        AssertThat(info.MeshCount).IsEqual(2u);
        AssertThat(info.MeshBuffers.Count).IsEqual(2);

        MdlMeshBuffer m0 = info.MeshBuffers[0];
        MdlMeshBuffer m1 = info.MeshBuffers[1];

        AssertThat(m0.IsFoliagePath).IsTrue();
        AssertThat(m0.FoliageVertexStride).IsEqual(48);
        AssertThat(m0.VertexCount).IsEqual(12u);
        AssertThat(m0.FaceCount).IsEqual(18u);

        AssertThat(m1.IsFoliagePath).IsTrue();
        AssertThat(m1.FoliageVertexStride).IsEqual(48);
        AssertThat(m1.VertexCount).IsEqual(8u);
        AssertThat(m1.FaceCount).IsEqual(12u);
        AssertThat(m1.FaceOffset + (int)(m1.FaceCount * 2)).IsEqual(data.Length);
    }
}
