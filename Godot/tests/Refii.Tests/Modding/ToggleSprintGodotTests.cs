namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Toggle Sprint is a Godot script on the mod API: the toggle is a <c>game_action</c> verdict that
/// cancels SPRINT_SET (66) while sprinting and SPRINT_RELEASE (67) otherwise, on the game's own sprint
/// flag read inside the verdict (<c>host.read("sprinting")</c>, as a Lua handler reads
/// <c>Refii.sprinting</c>); standing still ends the sprint through <c>end_sprint</c> on the hero's own
/// <c>query velocity</c>; both players, where each is played and in play. It ships no Lua and no
/// native code, every RefiiHost member it uses exists with that argument count, every event and answer
/// field it reads is stated, its data files validate, and the old Shift tap is gone.
/// </summary>
[TestSuite]
public class ToggleSprintGodotTests
{
    private const string Mod = "toggle_sprint";
    private const string File = "toggle_sprint.gd";

    [TestCase]
    public void ShipsOneGodotScriptAndNoLua()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        GodotModScripts.AssertShips(GodotModScripts.ModFolder(root, Mod), Array.Empty<string>(), new[] { "godot/" + File });
    }

    [TestCase]
    public void EveryHostMemberExistsAndTheWorkaroundsAreGone()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string text = GodotModScripts.Script(root, Mod, File);
        string code = GodotModScripts.Code(text);
        var called = GodotModScripts.AssertHostContract(root, File, text);
        foreach (string name in new[] { "on_verdict", "remove_verdict", "read", "ask", "end_sprint", "in_play", "player_here", "hero", "setting", "constants" })
        {
            AssertThat(called.Contains(name)).OverrideFailureMessage(File + " never calls host." + name).IsTrue();
        }

        var verdicts = GodotModScripts.Verdicts(code);
        AssertThat(verdicts.Count == 1 && verdicts[0] == ("game_action", "_on_game_action")).IsTrue();

        // The flag is read in the verdict, as the press arrives: no copy of it asked per tick.
        var reads = GodotModScripts.Reads(code);
        AssertThat(reads.Count == 1 && reads[0] == ("sprinting", "_on_game_action")).IsTrue();
        AssertThat(GodotModScripts.Asked(code).Contains("sprinting")).OverrideFailureMessage(File + " still asks the sprint flag per tick").IsFalse();
        foreach (string gone in new[] { "epoch", "asking", "_state(", "var sprinting" })
        {
            AssertThat(code.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage(File + " still keeps the cached flag: " + gone).IsFalse();
        }
        AssertThat(GodotModScripts.Constants(code).ToList()).ContainsExactlyInAnyOrder("ACTION_SPRINT_SET", "ACTION_SPRINT_RELEASE");

        // Both players: the verdict takes the event's player, the tick walks both.
        AssertThat(code.Contains("for p in [1, 2]:", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("e.get(\"player\"", StringComparison.Ordinal)).IsTrue();

        // 0.1's workarounds: a Shift tap through Godot's input (player one, focus-bound, a guessed hold)
        // and a polled request counter from Lua.
        foreach (string gone in new[] { "parse_input_event", "KEY_SHIFT", "InputEventKey", "get_value", "park_request", "run_lua", "call_lua", "HOLD_SECONDS" })
        {
            AssertThat(code.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage(File + " still uses " + gone).IsFalse();
        }
    }

    [TestCase]
    public void EveryEventAndAnswerFieldIsStated()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        GodotModScripts.AssertEvents(File, code,
            ("game_action", new[] { "action", "player" }),
            ("tick", new[] { "seconds" }),
            ("level_loaded", Array.Empty<string>()));
        GodotModScripts.AssertAnswers(root, File, code,
            ("sprinting", new[] { "sprinting" }),
            ("velocity", new[] { "ok", "x", "y" }));
    }

    [TestCase]
    public void TheDataFilesValidateAndTheSettingIsRead()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string folder = GodotModScripts.ModFolder(root, Mod);
        (int checkedFiles, var warnings) = GodotModScripts.AssertDataValidates(folder);
        AssertThat(checkedFiles).IsEqual(3);   // settings.json, settings.ui.json, mod.toml
        AssertThat(warnings.Count).IsEqual(0);

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        AssertThat(code.Contains("host.setting(\"toggle_sprint.stop_after\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(System.IO.File.ReadAllText(Path.Combine(folder, "settings.json")).Contains("\"stop_after\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(System.IO.File.ReadAllText(Path.Combine(folder, "README.md")).Contains("written as a Godot script", StringComparison.Ordinal)).IsTrue();
    }
}
