namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Mods;

/// <summary>
/// The GDScript bridge (Engine_RefiiHost) is held to the catalog: one signal per event, named by
/// its Lua name, in kind order; and the bus's field text parses to the typed Dictionary a script
/// reads.
/// </summary>
[TestSuite]
public class RefiiHostTests
{
    [TestCase]
    public void SignalListMatchesTheCatalog()
    {
        AssertThat(RefiiHost.SignalNames.Length).IsEqual(ModApi.Events.Count);
        for (int i = 0; i < ModApi.Events.Count; i++)
        {
            AssertThat(RefiiHost.SignalNames[i]).IsEqual(ModApi.Events[i].LuaName);
        }

        AssertThat(RefiiHost.SignalNames.Distinct().Count()).IsEqual(RefiiHost.SignalNames.Length);
        AssertThat(RefiiHost.SignalNames.Contains(RefiiHost.ModEventSignal)).IsFalse();
    }

    // The node's signals and methods are checked in the editor, not here: a Godot Node cannot be
    // constructed outside the engine (the test host crashes on the native call).

    [TestCase]
    public void FieldsParseToTypedValues()
    {
        var e = new Dictionary<string, object>(RefiiHost.ParseFieldsPlain(
            "victim=123,victim_player=1,attacker=0,damage=4.5,name='ALLOW_ACTION_INTERRUPT',unblockable=false,big=4000000000,tiny=1e-06,yes=true"));

        AssertThat(e["victim"]).IsEqual(123L);
        AssertThat(e["victim_player"]).IsEqual(1L);
        AssertThat(e["attacker"]).IsEqual(0L);
        AssertThat(e["damage"]).IsEqual(4.5);
        AssertThat(e["name"]).IsEqual("ALLOW_ACTION_INTERRUPT");
        AssertThat(e["unblockable"]).IsEqual(false);
        AssertThat(e["yes"]).IsEqual(true);
        AssertThat(e["big"]).IsEqual(4000000000L);
        AssertThat(e["tiny"] is double).IsTrue();
        AssertThat(e.Count).IsEqual(9);
    }

    [TestCase]
    public void FieldsParseTheEmptyAndTheOddCases()
    {
        AssertThat(RefiiHost.ParseFieldsPlain(string.Empty).Count).IsEqual(0);
        var e = new Dictionary<string, object>(RefiiHost.ParseFieldsPlain("seconds=0.0333,name='',k=-7"));
        AssertThat((double)e["seconds"] > 0.03).IsTrue();
        AssertThat(e["name"]).IsEqual(string.Empty);
        AssertThat(e["k"]).IsEqual(-7L);
    }

    [TestCase]
    public void GodotScriptPathsAreNotOverlays()
    {
        AssertThat(ModGodot.IsGodotScriptPath("godot/hud.gd")).IsTrue();
        AssertThat(ModGodot.IsGodotScriptPath("Godot/sub/hud.GD")).IsTrue();
        AssertThat(ModGodot.IsGodotScriptPath("/godot/hud.gd")).IsTrue();
        AssertThat(ModGodot.IsGodotScriptPath("godot/hud.png")).IsFalse();
        AssertThat(ModGodot.IsGodotScriptPath("mod/init.lua")).IsFalse();
        AssertThat(ModGodot.IsGodotScriptPath("hud.gd")).IsFalse();
    }
}
