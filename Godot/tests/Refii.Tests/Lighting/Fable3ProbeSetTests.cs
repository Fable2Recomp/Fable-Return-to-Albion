namespace Refii.Tests.Lighting;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Lighting;

/// <summary>
/// Fable 3's .flpb v2 probe set: the containing cell's probe blended with its linked neighbours'
/// by inverse square distance (sub_821C22B0), and nothing outside every cell.
/// </summary>
[TestSuite]
public class Fable3ProbeSetTests
{
    private static void U32(List<byte> into, uint v)
    {
        into.Add((byte)(v >> 24));
        into.Add((byte)(v >> 16));
        into.Add((byte)(v >> 8));
        into.Add((byte)v);
    }

    private static void F32(List<byte> into, float v) => U32(into, unchecked((uint)BitConverter.SingleToInt32Bits(v)));

    private static void Vec(List<byte> into, float x, float y, float z)
    {
        F32(into, x);
        F32(into, y);
        F32(into, z);
    }

    /// <summary>An axis-aligned box cell [x0,x1] x [0,1] x [0,1]: six inward planes, the corners on the first face.</summary>
    private static void BoxCell(List<byte> into, float x0, float x1, int[] links)
    {
        (Vector3 N, float W)[] planes =
        {
            (new Vector3(1, 0, 0), -x0), (new Vector3(-1, 0, 0), x1),
            (new Vector3(0, 1, 0), 0f), (new Vector3(0, -1, 0), 1f),
            (new Vector3(0, 0, 1), 0f), (new Vector3(0, 0, -1), 1f),
        };
        U32(into, (uint)planes.Length);
        for (int f = 0; f < planes.Length; f++)
        {
            Vec(into, planes[f].N.X, planes[f].N.Y, planes[f].N.Z);
            F32(into, planes[f].W);
            if (f == 0)
            {
                U32(into, 8);
                for (int c = 0; c < 8; c++)
                {
                    Vec(into, (c & 1) != 0 ? x1 : x0, (c & 2) != 0 ? 1f : 0f, (c & 4) != 0 ? 1f : 0f);
                }
            }
            else
            {
                U32(into, 0);
            }
        }

        U32(into, (uint)links.Length);
        foreach (int link in links)
        {
            U32(into, unchecked((uint)link));
        }
    }

    /// <summary>Two unit cells side by side on X: probe 0 red at (0.5, 0.5, 0.5), probe 1 green at (1.5, 0.5, 0.5).
    /// Cell 0 links to cell 1; cell 1 links to nothing (and carries a -1 link, which is no neighbour).</summary>
    private static byte[] TwoCellSet()
    {
        var bytes = new List<byte>();
        U32(bytes, Engine_LightProbeField.ProbeSetVersion);
        U32(bytes, 2);
        Vec(bytes, 0.5f, 0.5f, 0.5f);
        for (int c = 0; c < LightProbeField.ShCoefficients; c++)
        {
            Vec(bytes, c == 0 ? 1f : 0f, 0f, 0f);
        }

        Vec(bytes, 1.5f, 0.5f, 0.5f);
        for (int c = 0; c < LightProbeField.ShCoefficients; c++)
        {
            Vec(bytes, 0f, c == 0 ? 1f : 0f, 0f);
        }

        U32(bytes, 2);
        BoxCell(bytes, 0f, 1f, new[] { 1 });
        BoxCell(bytes, 1f, 2f, new[] { -1 });
        return bytes.ToArray();
    }

    [TestCase]
    public void Parse_KeepsTheNeighbourLinks()
    {
        bool parsed = Engine_LightProbeField.TryParseProbeSet(TwoCellSet(), out LightProbeField field, out string error);
        AssertThat(error).IsEmpty();
        AssertThat(parsed).IsTrue();
        AssertThat(field.HasCells).IsTrue();
        AssertThat(field.CellLinks.Length).IsEqual(2);
        AssertThat(field.CellLinks[0]).ContainsExactly(1);
        AssertThat(field.CellLinks[1]).ContainsExactly(-1);
        AssertThat(field.CellBoundsMin).IsEqual(new Vector3(0, 0, 0));
        AssertThat(field.CellBoundsMax).IsEqual(new Vector3(2, 1, 1));
    }

    [TestCase]
    public void Sample_BlendsTheContainingCellWithItsLinkedNeighbourByInverseSquareDistance()
    {
        Engine_LightProbeField.TryParseProbeSet(TwoCellSet(), out LightProbeField field, out _);

        // In cell 0, 0.25 from probe 0 (w = 16) and 0.75 from probe 1 (w = 16/9): red 0.9, green 0.1.
        ProbeSample blended = field.Sample(new Vector3(0.75f, 0.5f, 0.5f));
        AssertThat(blended.R.X).IsEqualApprox(0.9f, 1e-4f);
        AssertThat(blended.G.X).IsEqualApprox(0.1f, 1e-4f);
        AssertThat(blended.B.X).IsEqualApprox(0f, 1e-6f);
    }

    [TestCase]
    public void Sample_TakesTheProbeAloneWhereTheCellLinksToNothing()
    {
        Engine_LightProbeField.TryParseProbeSet(TwoCellSet(), out LightProbeField field, out _);

        ProbeSample alone = field.Sample(new Vector3(1.75f, 0.5f, 0.5f));
        AssertThat(alone.R.X).IsEqualApprox(0f, 1e-6f);
        AssertThat(alone.G.X).IsEqualApprox(1f, 1e-6f);
    }

    [TestCase]
    public void Sample_OutsideEveryCellIsNothing()
    {
        Engine_LightProbeField.TryParseProbeSet(TwoCellSet(), out LightProbeField field, out _);

        ProbeSample none = field.Sample(new Vector3(0.5f, 1.5f, 0.5f));
        AssertThat(none.R.X).IsEqualApprox(0f, 1e-6f);
        AssertThat(none.G.X).IsEqualApprox(0f, 1e-6f);
    }
}
