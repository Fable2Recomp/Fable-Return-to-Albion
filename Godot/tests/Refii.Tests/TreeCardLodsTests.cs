using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Thinned tree levels: trunk kept whole, leaf cards dropped as whole components.</summary>
[TestSuite]
public sealed class TreeCardLodsTests
{
    /// <summary>A trunk strip of <paramref name="trunkTriangles"/> connected triangles, then
    /// <paramref name="cards"/> separate two-triangle quads.</summary>
    private static (int[] Indices, int Vertices) Tree(int trunkTriangles, int cards)
    {
        var indices = new List<int>();
        for (int t = 0; t < trunkTriangles; t++)
        {
            indices.AddRange(new[] { t, t + 1, t + 2 });
        }

        int next = trunkTriangles + 2;
        for (int c = 0; c < cards; c++, next += 4)
        {
            indices.AddRange(new[] { next, next + 1, next + 2, next + 2, next + 1, next + 3 });
        }

        return (indices.ToArray(), next);
    }

    private static HashSet<(int, int, int)> Triangles(int[] indices)
    {
        var set = new HashSet<(int, int, int)>();
        for (int i = 0; i + 2 < indices.Length; i += 3) set.Add((indices[i], indices[i + 1], indices[i + 2]));
        return set;
    }

    [TestCase]
    public void LevelsKeepTheTrunkAndHalveThenQuarterTheCards()
    {
        (int[] indices, int vertices) = Tree(40, 400);
        List<(float Key, int[] Indices)> levels = Engine_TreeCardLods.Build(indices, vertices, 10f);
        AssertThat(levels.Count).IsEqual(2);
        AssertThat(levels[0].Key).IsEqualApprox(0.4f, 1e-5f);
        AssertThat(levels[1].Key).IsEqualApprox(0.8f, 1e-5f);

        var trunk = Triangles(indices.Take(40 * 3).ToArray());
        foreach ((_, int[] level) in levels)
        {
            AssertThat(level.Length % 6).IsEqual(0 + (40 * 3) % 6);
            AssertThat(trunk.IsSubsetOf(Triangles(level))).IsTrue();
        }

        int cards0 = (levels[0].Indices.Length - (40 * 3)) / 6;
        int cards1 = (levels[1].Indices.Length - (40 * 3)) / 6;
        AssertThat(cards0).IsBetween(180, 220);
        AssertThat(cards1).IsBetween(80, 120);
        // The coarser level's cards are a subset of the finer one's.
        AssertThat(Triangles(levels[1].Indices).IsSubsetOf(Triangles(levels[0].Indices))).IsTrue();
    }

    [TestCase]
    public void FarLevelsGoOnToAnEighthAndASixteenth()
    {
        (int[] indices, int vertices) = Tree(40, 800);
        List<(float Key, int[] Indices)> levels = Engine_TreeCardLods.Build(indices, vertices, 10f, far: true);
        AssertThat(levels.Count).IsEqual(4);
        AssertThat(levels[2].Key).IsEqualApprox(1.6f, 1e-5f);
        AssertThat(levels[3].Key).IsEqualApprox(3.2f, 1e-5f);

        int cards2 = (levels[2].Indices.Length - (40 * 3)) / 6;
        int cards3 = (levels[3].Indices.Length - (40 * 3)) / 6;
        AssertThat(cards2).IsBetween(80, 120);
        AssertThat(cards3).IsBetween(35, 65);
        AssertThat(Triangles(levels[3].Indices).IsSubsetOf(Triangles(levels[2].Indices))).IsTrue();
        AssertThat(Triangles(levels[2].Indices).IsSubsetOf(Triangles(levels[1].Indices))).IsTrue();

        // The first two are exactly the default ones.
        List<(float Key, int[] Indices)> near = Engine_TreeCardLods.Build(indices, vertices, 10f);
        AssertThat(levels[0].Indices.SequenceEqual(near[0].Indices)).IsTrue();
        AssertThat(levels[1].Indices.SequenceEqual(near[1].Indices)).IsTrue();
    }

    [TestCase]
    public void AFarLevelThatSavesTooLittleIsDropped()
    {
        // Half boughs: only the quarter level saves 30% on the one before; the half, eighth and
        // sixteenth are dropped.
        (int[] indices, int vertices) = Tree(300, 150);
        List<(float Key, int[] Indices)> levels = Engine_TreeCardLods.Build(indices, vertices, 10f, far: true);
        AssertThat(levels.Count).IsLess(4);
        int previous = indices.Length;
        foreach ((_, int[] level) in levels)
        {
            AssertThat(level.Length).IsLessEqual(previous * 7 / 10);
            previous = level.Length;
        }
    }

    [TestCase]
    public void CardsGoWholeNeverHalfAQuad()
    {
        (int[] indices, int vertices) = Tree(0, 300);
        foreach ((_, int[] level) in Engine_TreeCardLods.Build(indices, vertices, 5f))
        {
            AssertThat(level.Length % 6).IsEqual(0);
            for (int i = 0; i < level.Length; i += 6)
            {
                AssertThat(level[i + 3]).IsEqual(level[i + 2]);
            }
        }
    }

    [TestCase]
    public void AMeshWithoutCardsGetsNoLevel()
    {
        (int[] indices, int vertices) = Tree(500, 0);
        AssertThat(Engine_TreeCardLods.Build(indices, vertices, 10f).Count).IsEqual(0);
    }

    [TestCase]
    public void BadInputGetsNoLevel()
    {
        (int[] indices, int vertices) = Tree(10, 100);
        AssertThat(Engine_TreeCardLods.Build(indices, vertices - 1, 10f).Count).IsEqual(0);
        AssertThat(Engine_TreeCardLods.Build(indices, vertices, float.NaN).Count).IsEqual(0);
        AssertThat(Engine_TreeCardLods.Build(new int[9], 3, 10f).Count).IsEqual(0);
    }
}
