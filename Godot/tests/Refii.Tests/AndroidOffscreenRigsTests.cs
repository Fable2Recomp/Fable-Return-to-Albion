using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `android_offscreen_rig_walks` (AndroidOffscreenRigs, GameEntities.TryDeferOffscreen) and the
/// entity walk's debug timers beside it.
/// </summary>
[TestSuite]
public sealed class AndroidOffscreenRigsTests
{
    private static string FileText(string repoPath, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>Below 2 or not a number is 1 (every walk); 2-8 floor to themselves; above 8 is 8.</summary>
    [TestCase]
    public void WalksAreClamped()
    {
        AssertThat(AndroidOffscreenRigs.Walks(0)).IsEqual(1);
        AssertThat(AndroidOffscreenRigs.Walks(1)).IsEqual(1);
        AssertThat(AndroidOffscreenRigs.Walks(1.9)).IsEqual(1);
        AssertThat(AndroidOffscreenRigs.Walks(-4)).IsEqual(1);
        AssertThat(AndroidOffscreenRigs.Walks(double.NaN)).IsEqual(1);
        AssertThat(AndroidOffscreenRigs.Walks(double.PositiveInfinity)).IsEqual(1);
        AssertThat(AndroidOffscreenRigs.Walks(2)).IsEqual(2);
        AssertThat(AndroidOffscreenRigs.Walks(3.7)).IsEqual(3);
        AssertThat(AndroidOffscreenRigs.Walks(8)).IsEqual(8);
        AssertThat(AndroidOffscreenRigs.Walks(40)).IsEqual(AndroidOffscreenRigs.MaxWalks);
    }

    /// <summary>The desktop's value walks every rig: the deferral is never asked for.</summary>
    [TestCase]
    public void TheDesktopWalksEveryRig()
    {
        // Read as source: GameEntities' static fields need the engine.
        AssertThat(FileText("Godot/src/android/GameEntities.Android.cs")).Contains("public static int OffscreenRigWalks = 1;");
        string feed = FileText("Godot/src/game/GameEntities.cs");
        AssertThat(feed).Contains("if (offscreenWalks > 1 && TryDeferOffscreen(entity, offscreenWalks, toWorld, nowMs, haveEye)) continue;");
    }

    /// <summary>The key is a Tuning row defaulting to 1, with a player's name.</summary>
    [TestCase]
    public void TheKeyIsRegisteredWithItsDefaultAndAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidOffscreenRigs.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("1");
        AssertThat(row.Type).IsEqual(RuntimeCVarType.Int);
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidOffscreenRigs.Key)).IsTrue();
    }

    /// <summary>
    /// The hooks: the host applies the lever; a deferred rig is walked again as changed and is
    /// forgotten when it goes; the deferral leaves principals, reconcile walks and the sun's shadows alone.
    /// </summary>
    [TestCase]
    public void TheHooksAreInPlace()
    {
        AssertThat(FileText("Godot/src/android/AndroidGameHost.cs")).Contains("AndroidOffscreenRigs.Apply();");

        string feed = FileText("Godot/src/game/GameEntities.cs");
        AssertThat(feed).Contains("if (_offscreenDeferred.Count != 0 && ResumesOffscreen(entity)) { restatedEntity = entity with { Changed = true }; entity = ref restatedEntity; }");
        AssertThat(feed).Contains("_offscreenDeferred.Remove(address);");

        string partial = FileText("Godot/src/android/GameEntities.Android.cs");
        AssertThat(partial).Contains("IsPrincipal(address)");
        AssertThat(partial).Contains("Due(address, ReconcileTicks)");
        AssertThat(partial).Contains("Refii.Lighting.Engine_LevelLighting.SunFarDepth > 0f");
        AssertThat(partial).Contains("nowMs - seenMs <= RigHoldMs");
        AssertThat(partial).Contains("_seen.Add(address);");
    }

    /// <summary>The walk's debug timers: the setup and tail laps, the sections and the row kinds.</summary>
    [TestCase]
    public void TheWalksTimersAreRecorded()
    {
        string feed = FileText("Godot/src/game/GameEntities.cs");
        foreach (string phase in new[]
                 {
                     "\"ent.setup\"", "\"ent.tail\"", "\"ent.prologue\"", "\"ent.skip\"", "\"ent.clip\"", "\"ent.drawn\"",
                     "\"ent.rest\"", "\"ent.batch\"", "\"ent.afterpose\"", "\"ent.rows.character\"", "\"ent.rows.rigprop\"",
                     "\"ent.rows.changed\"", "\"ent.rows.still\"",
                 })
        {
            AssertThat(feed).OverrideFailureMessage(phase).Contains(phase);
        }

        // "ent.loop" keeps its span (the perf overlay and the dev tab read it): the setup lap has its own clock.
        AssertThat(feed).Contains("long setupFrom = lap;");
        AssertThat(feed).Contains("Refii.Core.Engine_FramePhases.Lap(\"ent.setup\", ref setupFrom);");
        AssertThat(FileText("Godot/src/game/GameDebugServer.cs")).Contains("[\"offscreenDeferred\"] = GameEntities.OffscreenDeferred,");
    }
}
