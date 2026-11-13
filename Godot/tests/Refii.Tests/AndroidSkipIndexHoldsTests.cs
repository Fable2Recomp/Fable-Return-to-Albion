using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `android_skip_index_rig_props` and `android_skip_index_buildings` (AndroidSkipIndexHolds,
/// GameEntities.Android.cs) and the skip index's re-parks in the walk.
/// </summary>
[TestSuite]
public sealed class AndroidSkipIndexHoldsTests
{
    private static string FileText(string repoPath, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>On unless the setting reads false.</summary>
    [TestCase]
    public void TheHoldsAreOnUnlessSetOff()
    {
        var none = new Dictionary<string, string>();
        AssertThat(AndroidSkipIndexHolds.Holds(none, AndroidSkipIndexHolds.RigPropsKey)).IsTrue();
        var off = new Dictionary<string, string> { [AndroidSkipIndexHolds.RigPropsKey] = "false" };
        AssertThat(AndroidSkipIndexHolds.Holds(off, AndroidSkipIndexHolds.RigPropsKey)).IsFalse();
        AssertThat(AndroidSkipIndexHolds.Holds(off, AndroidSkipIndexHolds.BuildingsKey)).IsTrue();
    }

    /// <summary>Both keys are Tuning rows defaulting to on, with a player's name.</summary>
    [TestCase]
    public void TheKeysAreRegisteredWithTheirDefaultsAndPlayersNames()
    {
        foreach (string key in new[] { AndroidSkipIndexHolds.RigPropsKey, AndroidSkipIndexHolds.BuildingsKey })
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key).IsNotNull();
            AssertThat(row!.DefaultValue).IsEqual("true");
            AssertThat(row.Type).IsEqual(RuntimeCVarType.Bool);
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key).IsTrue();
        }
    }

    /// <summary>
    /// The desktop holds neither (the statics start off and only the Android host sets them), and
    /// the walk's hooks are in place: a held kind is asked on its own cadence, a rigged prop's pose
    /// gate is still asked, and a full-path row parks what its next walk would.
    /// </summary>
    [TestCase]
    public void TheDesktopHoldsNeitherAndTheHooksAreInPlace()
    {
        string partial = FileText("Godot/src/android/GameEntities.Android.cs");
        AssertThat(partial).Contains("public static bool HoldRigProps;");
        AssertThat(partial).Contains("public static bool HoldBuildings;");
        AssertThat(partial).Contains("bool inView = _heldGate >= 0 ? _heldGate != 0 : PosedThisTick(address, measurable, rigWorld, nowMs);");
        AssertThat(FileText("Godot/src/android/AndroidGameHost.cs")).Contains("AndroidSkipIndexHolds.Apply();");

        AssertThat(partial).Contains("_ => Refii.Entities.Engine_EntitySkipIndex.RecheckTicks(kind, ReconcileTicks, StillReconcileScale),");
        AssertThat(partial).Contains("Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp => RigPropRecheckTicks(tag),");
        AssertThat(partial).Contains("Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp => HoldsRigProp(address, tag),");

        string feed = FileText("Godot/src/game/GameEntities.cs");
        AssertThat(feed).Contains("HeldThisWalk(entity.Address, verdict, verdictTag))");
        AssertThat(feed).Contains("Due(address, RecheckFor(kind, tag)) ? HeldDue(kind) : HoldsKind(address, kind, tag);");
        AssertThat(feed).Contains("CountHeldRigProp(entity.Address, verdictTag, haveEye, toWorld, nowMs, ref posed, ref farHeld);");
        AssertThat(feed).Contains("ParkRigProp(entity, parkable, fastRow, writeBones);");
        AssertThat(feed).Contains("ParkAsNextWalk(entity, node!, multiRoute);");
        AssertThat(feed).Contains("BeginRigPropHolds(haveEye, toWorld, nowMs);");

        string server = FileText("Godot/src/game/GameDebugServer.cs");
        AssertThat(server).Contains("[\"rigPropsHeld\"] = GameEntities.LastWalkRigPropsHeld,");
        AssertThat(server).Contains("[\"reparked\"] = GameEntities.LastWalkReparked,");
        AssertThat(server).Contains("[\"rigPropMiss\"] = GameEntities.RigPropMissCounts(),");
    }

    /// <summary>
    /// A settled prop's verdict lasts to its settled-prop check, an unsettled one's (held only out of
    /// view) to the reconcile; both divide the reconcile, so no rigged prop is held across it.
    /// </summary>
    [TestCase]
    public void ARiggedPropIsReaskedOnItsOwnCheck()
    {
        AssertThat(AndroidSkipIndexHolds.RigPropRecheckTicks(unsettled: false, 30))
            .IsEqual(Refii.Entities.Engine_EntitySkipIndex.RigPropRecheckTicks);
        AssertThat(AndroidSkipIndexHolds.RigPropRecheckTicks(unsettled: true, 30)).IsEqual(30);
        AssertThat(30 % AndroidSkipIndexHolds.RigPropRecheckTicks(unsettled: false, 30)).IsEqual(0);
    }

    /// <summary>
    /// An unsettled prop's verdict answers only a walk its pose gate says it is out of view, the gate
    /// asked once (handed to the replay); it is parked only from a row that wrote no bones out of view.
    /// </summary>
    [TestCase]
    public void AnUnsettledPropIsHeldOnlyOutOfView()
    {
        string partial = FileText("Godot/src/android/GameEntities.Android.cs");
        AssertThat(partial).Contains("if (PosedThisTick(address, _holdEye, rigWorld, _holdNowMs))");
        AssertThat(partial).Contains("_heldGate = 0;");
        AssertThat(partial).Contains(": writeBones ? MissWrote");
        AssertThat(partial).Contains("_holdNowMs - seenMs <= RigHoldMs ? MissInView");
        // Never for a married or batched prop: its own skip answers its next walk.
        AssertThat(partial).Contains("_adoptions.ContainsKey(address) || (_batches?.Contains(address) ?? false) ? MissTwin");
    }
}
