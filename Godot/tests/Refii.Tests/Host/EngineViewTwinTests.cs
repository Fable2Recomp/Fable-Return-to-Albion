namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Entities;
using Refii.Models;

/// <summary>
/// A rig's hidden bones per view: a mod's parts (every view, or one player's) and a hero's
/// first-person head, composed into what the pose hides, what each split view's twin collapses, the
/// layers that put the twins and the body in their views, and the collapse a twin's skin applies.
/// Pure: no node, no server.
/// </summary>
[TestSuite]
public class EngineViewTwinTests
{
    private const int FirstPerson = Engine_FirstPersonView.FirstPersonMode;

    // 0 root, 1 spine, 2 neck, 3 head, 4 jaw, 5 left arm, 6 eye (under head), 7 hand (under arm).
    private static readonly int[] Parents = { -1, 0, 1, 2, 3, 1, 3, 5 };
    private const int Neck = 2, Head = 3, Arm = 5, Hand = 7;

    private static readonly int[] None = System.Array.Empty<int>();

    [TestCase]
    public void TestTheHeadGoesOnlyInFirstPersonAndOnlyForAPlayer()
    {
        foreach (int player in new[] { 1, 2 })
        {
            AssertThat(Engine_FirstPersonView.HeadHidden(player, 0)).IsFalse();
            AssertThat(Engine_FirstPersonView.HeadHidden(player, FirstPerson)).IsTrue();
        }

        AssertThat(Engine_FirstPersonView.HeadHidden(0, FirstPerson)).IsFalse();

        // View 0 is player one's camera, view 1 player two's (GameSplitView).
        AssertThat(Engine_FirstPersonView.OwnView(1)).IsEqual(0);
        AssertThat(Engine_FirstPersonView.OwnView(2)).IsEqual(1);
    }

    [TestCase]
    public void TestOneViewIsDrawnForThePlayersThisMachineShows()
    {
        // Alone: player one's. A couch screen: both. Online host: player one's. Joiner: player two's.
        AssertThat(Engine_ViewHides.Viewed(1, localCoop: false, onlineClient: false)).IsTrue();
        AssertThat(Engine_ViewHides.Viewed(2, localCoop: false, onlineClient: false)).IsFalse();
        AssertThat(Engine_ViewHides.Viewed(1, localCoop: true, onlineClient: false)).IsTrue();
        AssertThat(Engine_ViewHides.Viewed(2, localCoop: true, onlineClient: false)).IsTrue();
        AssertThat(Engine_ViewHides.Viewed(1, localCoop: false, onlineClient: true)).IsFalse();
        AssertThat(Engine_ViewHides.Viewed(2, localCoop: false, onlineClient: true)).IsTrue();
        AssertThat(Engine_ViewHides.Viewed(0, localCoop: true, onlineClient: false)).IsFalse();
    }

    [TestCase]
    public void TestOneViewHidesOnThePoseForTheViewsPlayersAlone()
    {
        // Nothing hidden: nothing planned (no twin, no pose write).
        Engine_ViewHides.Plan nothing = Engine_ViewHides.Compose(None, None, None, perView: false, true, true, Parents);
        AssertThat(nothing.Pose.Length).IsEqual(0);
        AssertThat(nothing.PerView).IsFalse();

        // Alone, player one in first person and a mod hiding player two's arm: the head goes, the arm stays.
        Engine_ViewHides.Plan alone = Engine_ViewHides.Compose(None, new[] { Neck }, new[] { Arm }, perView: false, true, false, Parents);
        AssertThat(alone.Pose).IsEqual(new[] { Neck });
        AssertThat(alone.PerView).IsFalse();

        // A couch screen shows both players: both go, with the every-view part.
        Engine_ViewHides.Plan couch = Engine_ViewHides.Compose(new[] { Hand }, new[] { Neck }, new[] { Arm }, perView: false, true, true, Parents);
        AssertThat(couch.Pose).IsEqual(new[] { Neck, Arm, Hand });

        // An online joiner draws player two's view: player one's parts are the host's to hide.
        Engine_ViewHides.Plan joiner = Engine_ViewHides.Compose(None, new[] { Neck }, new[] { Arm }, perView: false, false, true, Parents);
        AssertThat(joiner.Pose).IsEqual(new[] { Arm });
    }

    [TestCase]
    public void TestSplitScreenHidesEachPlayersBonesInTheirViewAlone()
    {
        // Player one in first person: the neck in view 0 alone; the every-view hand on the pose.
        Engine_ViewHides.Plan one = Engine_ViewHides.Compose(new[] { Hand }, new[] { Neck }, None, perView: true, true, true, Parents);
        AssertThat(one.Pose).IsEqual(new[] { Hand });
        AssertThat(one.FirstView).IsEqual(new[] { Neck });
        AssertThat(one.SecondView.Length).IsEqual(0);
        AssertThat(one.View(0)).IsEqual(new[] { Neck });

        // The head and a mod's part for the same player compose into one twin's collapse.
        Engine_ViewHides.Plan composed = Engine_ViewHides.Compose(None, new[] { Neck, Arm }, None, perView: true, true, true, Parents);
        AssertThat(composed.FirstView).IsEqual(new[] { Neck, Arm });
        AssertThat(composed.Pose.Length).IsEqual(0);

        // Each player hides something: a twin in each view.
        Engine_ViewHides.Plan both = Engine_ViewHides.Compose(None, new[] { Neck }, new[] { Arm }, perView: true, true, true, Parents);
        AssertThat(both.FirstView).IsEqual(new[] { Neck });
        AssertThat(both.SecondView).IsEqual(new[] { Arm });

        // A bone both views hide is the pose's: no twin for it.
        Engine_ViewHides.Plan shared = Engine_ViewHides.Compose(None, new[] { Neck, Arm }, new[] { Arm }, perView: true, true, true, Parents);
        AssertThat(shared.Pose).IsEqual(new[] { Arm });
        AssertThat(shared.FirstView).IsEqual(new[] { Neck });
        AssertThat(shared.SecondView.Length).IsEqual(0);

        // A per-view bone already under a pose bone needs no twin.
        Engine_ViewHides.Plan covered = Engine_ViewHides.Compose(new[] { Neck }, new[] { Head }, new[] { Hand }, perView: true, true, true, Parents);
        AssertThat(covered.Pose).IsEqual(new[] { Neck });
        AssertThat(covered.FirstView.Length).IsEqual(0);
        AssertThat(covered.SecondView).IsEqual(new[] { Hand });

        // Repeats collapse and the views are sorted (a twin compares them as they are).
        Engine_ViewHides.Plan sorted = Engine_ViewHides.Compose(None, new[] { Arm, Neck, Arm }, None, perView: true, true, true, Parents);
        AssertThat(sorted.FirstView).IsEqual(new[] { Neck, Arm });
    }

    [TestCase]
    public void TestTheTwinsAndTheBodyAreDrawnInTheirViews()
    {
        const uint outdoor = 1u;
        uint indoor = Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask;

        // No twin: the body's own layers, every view.
        AssertThat(Engine_ViewHides.BodyLayers(false, false, outdoor)).IsEqual(outdoor);
        AssertThat(Engine_ViewHides.BodyLayers(false, false, indoor)).IsEqual(indoor);

        // Player one's twin outdoors: twin on player one's layer, body on player two's.
        AssertThat(Engine_ViewHides.TwinLayers(0, outdoor)).IsEqual(Engine_ClassicViews.FirstViewOnlyLayer);
        AssertThat(Engine_ViewHides.BodyLayers(true, false, outdoor)).IsEqual(Engine_ClassicViews.SecondViewOnlyLayer);

        // Player two's twin indoors: the interior pair, the same way round.
        AssertThat(Engine_ViewHides.TwinLayers(1, indoor)).IsEqual(Engine_ClassicViews.SecondViewIndoorLayer);
        AssertThat(Engine_ViewHides.BodyLayers(false, true, indoor)).IsEqual(Engine_ClassicViews.FirstViewIndoorLayer);

        // A twin in each view: the body in neither.
        AssertThat(Engine_ViewHides.BodyLayers(true, true, outdoor)).IsEqual(0u);
        AssertThat(Engine_ViewHides.BodyLayers(true, true, indoor)).IsEqual(0u);

        // The four one-view layers are distinct single bits, clear of the zone, caster and marker bits.
        uint[] layers =
        {
            Engine_ClassicViews.FirstViewOnlyLayer, Engine_ClassicViews.SecondViewOnlyLayer,
            Engine_ClassicViews.FirstViewIndoorLayer, Engine_ClassicViews.SecondViewIndoorLayer,
        };
        uint seen = 0u;
        foreach (uint layer in layers)
        {
            AssertThat(System.Numerics.BitOperations.PopCount(layer)).IsEqual(1);
            AssertThat(seen & layer).IsEqual(0u);
            seen |= layer;
        }

        uint taken = 1u | Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LightZones.IndoorLampHostMask |
            Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask | Refii.Level.Engine_ReflectionLayer.Mask |
            Engine_ClassicViews.SecondViewLayer;
        AssertThat(seen & taken).IsEqual(0u);
    }

    [TestCase]
    public void TestEachCollapsedBoneGoesToItsOutermostRoot()
    {
        // The neck and the head both hidden: everything under the neck goes to the neck's joint; the
        // arm's subtree to the arm's; the spine and root stay.
        int[] rootOf = Engine_ViewTwin.Collapse(Parents, new[] { Head, Neck, Arm });
        AssertThat(rootOf).IsEqual(new[] { -1, -1, Neck, Neck, Neck, Arm, Neck, Arm });
        AssertThat(Engine_ViewTwin.Collapse(Parents, None)).IsEqual(new[] { -1, -1, -1, -1, -1, -1, -1, -1 });
        AssertThat(Engine_ViewTwin.Collapse(Parents, new[] { 99, -3 })).IsEqual(new[] { -1, -1, -1, -1, -1, -1, -1, -1 });
    }

    [TestCase]
    public void TestTheSubtreeIsTheRootAndEverythingUnderIt()
    {
        // 0 root, 1 spine, 2 neck, 3 head, 4 jaw, 5 left arm, 6 eye (under head), 7 hand (under arm).
        int[] parents = { -1, 0, 1, 2, 3, 1, 3, 5 };
        bool[] inside = Engine_ViewTwin.Subtree(parents, 2);
        AssertThat(inside).IsEqual(new[] { false, false, true, true, true, false, true, false });
        AssertThat(Engine_ViewTwin.Subtree(parents, -1)).IsEqual(new bool[8]);
    }

    [TestCase]
    public void TestTheCollapsedBindPutsEveryVertexAtTheRootJoint()
    {
        // A neck at (0, 1.5, 0) and a head bone 0.1 above it, rotated; the head's bind carries a
        // morph scale (diag(s) * rest^-1), which the collapse composes with.
        var neckRest = new Transform3D(Basis.Identity, new Vector3(0f, 1.5f, 0f));
        var headRest = new Transform3D(new Basis(Vector3.Up, 0.4f), new Vector3(0f, 1.6f, 0f));
        var s = new Vector3(1.2f, 0.9f, 1.1f);
        Transform3D headBind = new Transform3D(Basis.FromScale(s), Vector3.Zero) * headRest.AffineInverse();
        Vector3 anchor = headRest.AffineInverse() * neckRest.Origin;
        Transform3D collapsed = Engine_ViewTwin.CollapsedBind(headBind, anchor);

        // At rest the head's vertices land on the neck joint, give or take Shrunk of the model.
        foreach (Vector3 v in new[] { new Vector3(0f, 1.7f, 0.1f), new Vector3(-0.08f, 1.62f, 0f), Vector3.Zero })
        {
            Vector3 skinned = headRest * (collapsed * v);
            AssertThat(skinned.DistanceTo(neckRest.Origin) < 0.003f).OverrideFailureMessage($"{v} -> {skinned}").IsTrue();
        }

        // The root's own bind collapses onto its joint wherever it is posed.
        Transform3D neckBind = neckRest.AffineInverse();
        Transform3D neckCollapsed = Engine_ViewTwin.CollapsedBind(neckBind, neckRest.AffineInverse() * neckRest.Origin);
        var posed = new Transform3D(new Basis(Vector3.Right, 0.6f), new Vector3(0.3f, 1.4f, 0.2f));
        Vector3 at = posed * (neckCollapsed * new Vector3(0.05f, 1.55f, 0.04f));
        AssertThat(at.DistanceTo(posed.Origin) < 0.003f).IsTrue();

        // Bones outside the subtree keep the rig's bind untouched (the twin copies those as they are),
        // and the collapse never inverts: the basis is the bind's, scaled.
        AssertThat(collapsed.Basis.Determinant() > 0f).IsTrue();
    }
}
