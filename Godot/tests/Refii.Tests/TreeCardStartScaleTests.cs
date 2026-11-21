using System.Collections.Generic;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `tree_billboard_start_scale`: what a tree draws where. Up to the card start a tree draws exactly what it
/// draws without cards (the full model to its hand-over, its `_LOD1` beyond); only past the start does the
/// card replace the `_LOD1`. Each case runs a tree through the real stamp (Engine_PropDrawRange.TreeBand),
/// the cards' restamp (<see cref="AndroidTreeCardMaths.Restamped"/>) and the hand-over and far reach
/// (Engine_TreeReach.Cap), as Engine_LevelProps, AndroidTreeCards.Register and Engine_PropDrawRange.Apply do.
/// </summary>
[TestSuite]
public sealed class TreeCardStartScaleTests
{
    /// <summary>A span [from, to] in metres, or null when nothing is drawn.</summary>
    private readonly record struct Spans(Vector2? Detailed, Vector2? Far, Vector2? Card);

    /// <summary>
    /// The spans of one tree. <paramref name="placedNoLod"/>: a placed tree whose definition names no LOD
    /// model (the shipped trees: L 0), which borrows the level block's `_LOD1` when
    /// <see cref="Engine_TreeReach.PlacedTreeBorrows"/> says so; otherwise a level block that has its own.
    /// <paramref name="scale"/> is ignored without cards.
    /// </summary>
    private static Spans Of(float draw, float f, float lod, bool placedNoLod, bool cards, float scale,
        float handover = 1f, float reach = 0f)
    {
        bool hasLod = !placedNoLod || Engine_TreeReach.PlacedTreeBorrows(false, cards, scale);
        // A borrowed LOD model stands in for the impostor: the main model to F (Engine_LevelProps.StatePlacedTree).
        float stated = placedNoLod && hasLod ? f : lod;
        var range = new StatedRange(draw, 0.15f, 0f, 0f, f, stated);
        float cull = Mathf.Max(draw, f);

        Vector2? detailed = null, far = null, card = null;
        foreach (PropRole role in hasLod ? new[] { PropRole.Main, PropRole.Lod } : new[] { PropRole.Main })
        {
            if (Engine_PropDrawRange.TreeBand(range, role, hasLod, cards) is not { } band)
            {
                continue;
            }

            float[] node = { band[1] > 0f ? band[0] : 0f, 0f, band[3], 0f, 0f };
            bool impostor = role == (hasLod ? PropRole.Lod : PropRole.Main);
            if (cards && impostor)
            {
                (band, node) = AndroidTreeCardMaths.Restamped(band, node, f, cull, scale, true);
                float start = AndroidTreeCardMaths.Start(f, cull, scale);
                float end = AndroidTreeCardMaths.CardEnd(start, cull, reach);
                card = end > 0f ? new Vector2(0.75f * start, end) : null;
            }

            (band, _) = Engine_TreeReach.Cap(band, node, handover, reach);
            var span = new Vector2(band[1] > 0f ? band[0] : 0f, band[3]);
            if (role == PropRole.Main && hasLod)
            {
                detailed = span;
            }
            else if (role == PropRole.Lod)
            {
                far = span;
            }
            else
            {
                // A main model with no `_LOD1`: the full model is its own far model.
                detailed = span;
            }
        }

        return new Spans(detailed, far, card);
    }

    private static void Expect(Vector2? actual, float from, float to)
    {
        AssertThat(actual.HasValue).IsTrue();
        AssertThat(actual!.Value.X).IsEqualApprox(from, 1e-3f);
        AssertThat(actual.Value.Y).IsEqualApprox(to, 1e-3f);
    }

    [TestCase]
    public void APlacedTreeKeepsItsFullModelOnlyAtScaleOne()
    {
        // Classic keeps the title's impostor (the full model); without cards, or with cards past scale 1,
        // the `_LOD1` is borrowed as without cards.
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(classic: false, cards: false, 1f)).IsTrue();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(false, false, 3f)).IsTrue();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(false, true, 1f)).IsFalse();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(false, true, 0.5f)).IsFalse();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(false, true, 1.5f)).IsTrue();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(false, true, 3f)).IsTrue();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(true, true, 3f)).IsFalse();
        AssertThat(Engine_TreeReach.PlacedTreeBorrows(true, false, 1f)).IsFalse();
    }

    [TestCase]
    public void ThePlacedGraveyardTreeDrawsItsFarModelUpToTheCard()
    {
        // Bowerstone's placed trees: D 1024, F 64, L 0, no LOD model named.
        Spans off = Of(1024f, 64f, 0f, placedNoLod: true, cards: false, 1f);
        Expect(off.Detailed, 0f, 64f);
        Expect(off.Far, 56f, 1024f);
        AssertThat(off.Card.HasValue).IsFalse();

        // Scale 1, as it always was: the full model to F, then the card (fading in from 0.75 F).
        Spans one = Of(1024f, 64f, 0f, true, true, 1f);
        Expect(one.Detailed, 0f, 64f);
        AssertThat(one.Far.HasValue).IsFalse();
        Expect(one.Card, 48f, 1024f);

        // Scale 2 and 3: the full model still ends at F; the `_LOD1` carries the tree to the card's start.
        // Before the fix the full model ran to 128 / 192 m (2.83 M triangles at Ultra against 1.68 M without cards).
        Spans two = Of(1024f, 64f, 0f, true, true, 2f);
        Expect(two.Detailed, 0f, 64f);
        Expect(two.Far, 56f, 128f);
        Expect(two.Card, 96f, 1024f);

        Spans three = Of(1024f, 64f, 0f, true, true, 3f);
        Expect(three.Detailed, 0f, 64f);
        Expect(three.Far, 56f, 192f);
        Expect(three.Card, 144f, 1024f);
    }

    [TestCase]
    public void ALevelTreeWithItsOwnFarModelHandsOverAsWithoutCards()
    {
        // L 48, F 64, D 1024, hand-over at half (held at the 40 m floor), far reach 400 m.
        Spans off = Of(1024f, 64f, 48f, false, false, 1f, handover: 0.5f, reach: 400f);
        Expect(off.Detailed, 0f, 40f);
        Expect(off.Far, 35f, 400f);
        AssertThat(off.Card.HasValue).IsFalse();

        float[] scales = { 1f, 2f, 3f };
        foreach (float scale in scales)
        {
            Spans on = Of(1024f, 64f, 48f, false, true, scale, 0.5f, 400f);
            Expect(on.Detailed, 0f, 40f);
            Expect(on.Far, 35f, 64f * scale);
            Expect(on.Card, 0.75f * 64f * scale, 400f);
        }
    }

    [TestCase]
    public void TheFarModelIsTheRangeWithoutCardsClippedAtTheStart()
    {
        // Placed and level trees, hand-over at 1 and at half, far reach none and 400 m, scales 1.5 to 8.
        var trees = new List<(float Draw, float F, float Lod, bool Placed)>
        {
            (1024f, 64f, 0f, true), (1024f, 70f, 0f, true), (1024f, 64f, 48f, false), (512f, 96f, 80f, false),
        };
        float[] handovers = { 1f, 0.5f };
        float[] reaches = { 0f, 400f };
        float[] scales = { 1.5f, 2f, 3f, 8f };
        foreach ((float draw, float f, float lod, bool placed) in trees)
        {
            foreach (float handover in handovers)
            {
                foreach (float reach in reaches)
                {
                    Spans off = Of(draw, f, lod, placed, false, 1f, handover, reach);
                    foreach (float scale in scales)
                    {
                        Spans on = Of(draw, f, lod, placed, true, scale, handover, reach);
                        float start = AndroidTreeCardMaths.Start(f, Mathf.Max(draw, f), scale);
                        // The full model exactly as without cards, never further.
                        Expect(on.Detailed, off.Detailed!.Value.X, off.Detailed.Value.Y);
                        // The far model: the same start, its end at the card start (never past its own end without cards).
                        Expect(on.Far, off.Far!.Value.X, Mathf.Min(off.Far.Value.Y, start));
                    }
                }
            }
        }
    }

    [TestCase]
    public void AKindWithNoRoomForCardsKeepsItsFarModelNotItsFullModel()
    {
        // D 150 at scale 3: the start would be 192 m, past the cull, so it is the cull and no card is drawn.
        // The tree draws as without cards: the full model to F and the `_LOD1` to the cull (before the fix the
        // full model ran to 150 m).
        Spans off = Of(150f, 64f, 0f, true, false, 1f);
        Spans on = Of(150f, 64f, 0f, true, true, 3f);
        Expect(on.Detailed, 0f, 64f);
        Expect(on.Far, off.Far!.Value.X, off.Far.Value.Y);
        Expect(on.Far, 56f, 150f);
        AssertThat(on.Card.HasValue).IsFalse();

        // A far reach nearer than the start: no card, and the `_LOD1` ends at the reach, as without cards.
        Spans capped = Of(1024f, 64f, 0f, true, true, 3f, reach: 100f);
        Expect(capped.Detailed, 0f, 64f);
        Expect(capped.Far, 56f, 100f);
        AssertThat(capped.Card.HasValue).IsFalse();
    }

    [TestCase]
    public void TheRegionLineGivesTheStartsOfTheCardsDrawn()
    {
        // Ultra in the graveyard: every card from 192 m.
        AndroidTreeCardMaths.Spread ultra = AndroidTreeCardMaths.RegionSpread(
            new[] { (192f, 1024f), (192f, 1024f), (210f, 1024f) }, 0f);
        AssertThat(ultra.Describe(2, 3f)).IsEqual(
            "region: 3 cards of 2 tree models, cards from 192-210 m (start scale 3), cull 1024-1024 m");

        // A kind whose cull is its start draws no card, so its start is not a card start.
        AndroidTreeCardMaths.Spread one = AndroidTreeCardMaths.RegionSpread(new[] { (64f, 1024f), (70f, 70f) }, 0f);
        AssertThat(one.Describe(2, 1f)).IsEqual(
            "region: 1 cards of 2 tree models, cards from 64-64 m (start scale 1), cull 1024-1024 m, " +
            "1 placements without a card (cull within the start)");

        // tree_far_reach ends the cards nearer.
        AssertThat(AndroidTreeCardMaths.RegionSpread(new[] { (64f, 1024f) }, 400f).MaxCull).IsEqual(400f);
        AssertThat(AndroidTreeCardMaths.RegionSpread(new[] { (192f, 1024f) }, 100f).Describe(1, 3f)).IsEqual(
            "region: 0 cards of 1 tree models, no cards (start scale 3), 1 placements without a card (cull within the start)");
    }
}
