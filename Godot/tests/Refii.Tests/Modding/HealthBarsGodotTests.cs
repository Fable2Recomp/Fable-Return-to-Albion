namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Health Bars is a Godot script on the mod API: world meters (<c>world_meter</c> / <c>mark_meter</c>)
/// over the enemies near each hero this machine plays, found by asking the guest (<c>position</c>,
/// <c>near</c>, <c>relation</c> against <c>RELATION_ENEMY</c>, <c>living</c>), restated from
/// <c>health_changed</c>, taken away on <c>entity_died</c> / <c>entity_removed</c>, gated by
/// <c>in_play</c>, per player on split screen. It ships no Lua and no native code, every RefiiHost
/// member it uses exists with that argument count, every event and answer field it reads is stated,
/// its data files validate, and the hand-drawn canvas is gone.
/// </summary>
[TestSuite]
public class HealthBarsGodotTests
{
    private const string Mod = "health_bars";
    private const string File = "health_bars.gd";

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
        foreach (string name in new[]
                 {
                     "world_meter", "mark_meter", "unmark", "ask", "in_play", "player_here", "hero", "split_screen",
                     "setting", "constants", "entity_value", "entity_set", "real_now",
                 })
        {
            AssertThat(called.Contains(name)).OverrideFailureMessage(File + " never calls host." + name).IsTrue();
        }

        AssertThat(GodotModScripts.Constants(code).ToList()).ContainsExactly("RELATION_ENEMY");
        AssertThat(GodotModScripts.Verdicts(code).Count).IsEqual(0);

        // Per player on split screen, one shared set otherwise; the meter's own style string.
        AssertThat(code.Contains("return [[1, [1]], [2, [2]]]", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("return [[0, [1, 2]]]", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("\" numbers\"", StringComparison.Ordinal) && code.Contains("\" x%.2f\"", StringComparison.Ordinal)).IsTrue();

        // 0.1's workarounds: a canvas of its own projecting the drawn body, an indexed published array.
        foreach (string gone in new[] { "CanvasLayer", "draw_rect", "unproject_position", "node_for", "get_value", "\"health_bars.n\"", "health_bars.e%d", "run_lua", "call_lua", "_process" })
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
            ("tick", new[] { "seconds" }),
            ("health_changed", new[] { "entity", "health", "max" }),
            ("entity_died", new[] { "entity" }),
            ("entity_removed", new[] { "entity" }),
            ("level_unloading", Array.Empty<string>()));
        AssertThat(GodotModScripts.Signals(code).Contains("setting_changed")).IsTrue();
        GodotModScripts.AssertAnswers(root, File, code,
            ("position", new[] { "ok", "x", "y", "z" }),
            ("near", new[] { "count", "total", "e" }),
            ("relation", new[] { "ok", "relation" }),
            ("living", new[] { "living" }),
            ("health", new[] { "ok", "current", "max" }));
    }

    [TestCase]
    public void TheDataFilesValidateAndEverySettingIsRead()
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
        string settings = System.IO.File.ReadAllText(Path.Combine(folder, "settings.json"));
        string page = System.IO.File.ReadAllText(Path.Combine(folder, "settings.ui.json"));
        var keys = Regex.Matches(settings, @"""([a-z_]+)"":").Select(m => m.Groups[1].Value).Where(k => k != Mod).ToList();
        AssertThat(keys).ContainsExactlyInAnyOrder("show", "range", "only_hurt", "numbers", "size");
        foreach (string key in keys)
        {
            AssertThat(code.Contains("host.setting(\"health_bars." + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("never read: " + key).IsTrue();
            AssertThat(page.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.ui.json lacks " + key).IsTrue();
        }

        AssertThat(System.IO.File.ReadAllText(Path.Combine(folder, "README.md")).Contains("written as a Godot script", StringComparison.Ordinal)).IsTrue();
    }
}
