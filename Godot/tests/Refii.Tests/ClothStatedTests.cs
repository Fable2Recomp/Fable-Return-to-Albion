using System;
using GdUnit4;
using Refii.Host;
using Refii.Physics;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The cloth a title's guest states (Fable 3): the piece rows its bridge writes, decoded by
/// <see cref="HostBridge.DecodeClothPieces"/>, and a synthetic piece's published vertices brought
/// into the mesh's space and blended at the draw tick by <see cref="Engine_ClothStated"/>.
/// </summary>
[TestSuite]
public sealed class ClothStatedTests
{
    private const int Floats = HostBridge.ClothVertexFloats;

    /// <summary>One published vertex: position xyz 1, normal xyz 0, tangent xyz handedness.</summary>
    private static float[] Vertex(float px, float py, float pz, float nx, float ny, float nz,
        float tx, float ty, float tz, float handedness) =>
        new[] { px, py, pz, 1f, nx, ny, nz, 0f, tx, ty, tz, handedness };

    private static float[] Piece(params float[][] vertices)
    {
        var floats = new float[vertices.Length * Floats];
        for (int v = 0; v < vertices.Length; v++)
        {
            Array.Copy(vertices[v], 0, floats, v * Floats, Floats);
        }

        return floats;
    }

    [TestCase]
    public void RowsDecodeWordForWord()
    {
        uint[] words =
        {
            0x45323110u, 0xE3C5D2A7u, 0u, 3u, 230u, 17u, 9001u, 9000u,
            0x44D5C1B0u, 0x32F79A3Fu, 2u, 0u, 70u, 4u, 9001u, 0u,
        };
        var pieces = new HostBridge.GuestClothPiece[4];

        int count = HostBridge.DecodeClothPieces(words, 2, pieces);

        AssertThat(count).IsEqual(2);
        AssertThat(pieces[0]).IsEqual(new HostBridge.GuestClothPiece(
            0x45323110u, 0xE3C5D2A7u, 0, true, true, 230, 17u, 9001u, 9000u));
        AssertThat(pieces[1]).IsEqual(new HostBridge.GuestClothPiece(
            0x44D5C1B0u, 0x32F79A3Fu, 2, false, false, 70, 4u, 9001u, 0u));
    }

    [TestCase]
    public void RowsNeverReadPastTheWords()
    {
        uint[] words = { 1u, 2u, 3u, 0u, 4u, 5u, 6u, 0u, 7u };
        var pieces = new HostBridge.GuestClothPiece[4];

        AssertThat(HostBridge.DecodeClothPieces(words, 4, pieces)).IsEqual(1);
    }

    /// <summary>A skinned instance numbers its list from zero; the model's records put rigid first.</summary>
    [TestCase]
    public void SkinnedSlotsFollowTheRigidRecords()
    {
        AssertThat(Engine_ClothStated.Record(false, 2, 5)).IsEqual(2);
        AssertThat(Engine_ClothStated.Record(true, 2, 5)).IsEqual(7);
        AssertThat(Engine_ClothStated.Record(true, 0, 0)).IsEqual(0);
    }

    [TestCase]
    public void FractionRunsFromThePreviousTickToTheCurrent()
    {
        AssertThat(Engine_ClothStated.Fraction(99.25, 99u, 100u)).IsEqual(0.25f);
        AssertThat(Engine_ClothStated.Fraction(98.0, 99u, 100u)).IsEqual(0f);
        AssertThat(Engine_ClothStated.Fraction(101.0, 99u, 100u)).IsEqual(1f);
        AssertThat(Engine_ClothStated.Fraction(99.5, 0u, 100u)).IsEqual(1f);
        AssertThat(Engine_ClothStated.Fraction(double.NaN, 99u, 100u)).IsEqual(1f);
    }

    /// <summary>
    /// The guest's model space is the file's Z-up; the drawn mesh maps (x, y, z) to (x, z, -y), as
    /// the mesh reader maps every vertex. Positions, normals and tangents all go through it; the
    /// tangent's handedness is kept.
    /// </summary>
    [TestCase]
    public void CurrentPoseLandsInTheMeshSpace()
    {
        float[] current = Piece(
            Vertex(-0.18f, -0.09f, 0.96f, 0f, -1f, 0f, 1f, 0f, 0f, -1f),
            Vertex(0.5f, 2f, 3f, 0f, 0f, 1f, 0f, 1f, 0f, 1f));
        var positions = new PhysVec4[2];
        var normals = new PhysVec4[2];
        var tangents = new PhysVec4[2];

        (PhysVec4 min, PhysVec4 max) = Engine_ClothStated.Blend(
            ReadOnlySpan<float>.Empty, current, 1f, 2, positions, normals, tangents);

        AssertThat(positions[0].X).IsEqual(-0.18f);
        AssertThat(positions[0].Y).IsEqual(0.96f);
        AssertThat(positions[0].Z).IsEqual(0.09f);
        AssertThat(positions[1].Y).IsEqual(3f);
        AssertThat(positions[1].Z).IsEqual(-2f);
        AssertThat(normals[0].Z).IsEqual(1f);
        AssertThat(normals[1].Y).IsEqual(1f);
        AssertThat(tangents[1].Z).IsEqual(-1f);
        AssertThat(tangents[0].W).IsEqual(-1f);
        AssertThat(min.X).IsEqual(-0.18f);
        AssertThat(max.Y).IsEqual(3f);
        AssertThat(min.Z).IsEqual(-2f);
    }

    [TestCase]
    public void PreviousTickBlendsTowardTheCurrent()
    {
        float[] previous = Piece(Vertex(0f, 0f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 1f));
        float[] current = Piece(Vertex(2f, 4f, 6f, 0f, 0f, 1f, 1f, 0f, 0f, -1f));
        var positions = new PhysVec4[1];
        var normals = new PhysVec4[1];
        var tangents = new PhysVec4[1];

        Engine_ClothStated.Blend(previous, current, 0.25f, 1, positions, normals, tangents);

        AssertThat(positions[0].X).IsEqual(0.5f);
        AssertThat(positions[0].Y).IsEqual(1.5f);
        AssertThat(positions[0].Z).IsEqual(-1f);
        AssertThat(tangents[0].W).IsEqual(-1f);
    }

    [TestCase]
    public void AShortPreviousPoseIsNotBlended()
    {
        float[] current = Piece(
            Vertex(1f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f),
            Vertex(2f, 2f, 2f, 0f, 0f, 1f, 1f, 0f, 0f, 1f));
        float[] previous = Piece(Vertex(9f, 9f, 9f, 0f, 0f, 1f, 1f, 0f, 0f, 1f));
        var positions = new PhysVec4[2];
        var normals = new PhysVec4[2];
        var tangents = new PhysVec4[2];

        Engine_ClothStated.Blend(previous, current, 0.5f, 2, positions, normals, tangents);

        AssertThat(positions[0].X).IsEqual(1f);
        AssertThat(positions[1].Y).IsEqual(2f);
    }
}
