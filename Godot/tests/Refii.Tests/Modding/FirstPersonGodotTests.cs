namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Entities;
using Refii.Mods;

/// <summary>
/// First Person's switch is a Godot script on the camera API: each player's view is the game's own
/// first-person camera held through <c>camera_override</c> (field of view, pitch limit and the aim rule
/// as the hold's options), toggled by whoever pressed the action, stated again on
/// <c>setting_changed</c>, <c>cutscene</c>, <c>hero_ready</c>, <c>hero_gone</c> and <c>lua_ready</c>, never
/// on a shared screen. The game's camera is game Lua, so camera.lua stays and wraps only what the
/// camera takes no data for, per player, reading the hold from the player value
/// <c>first_person.held</c> the script states where each player is played. The head is the core's
/// (<see cref="Engine_FirstPersonView"/>). Every RefiiHost member the script uses exists with that
/// argument count, every <c>Refii.*</c> name camera.lua calls is in the catalog, the events and data
/// files are stated and camera.lua compiles with the game's own compiler.
/// </summary>
[TestSuite]
public class FirstPersonGodotTests
{
    private const string Mod = "first_person";
    private const string File = "first_person.gd";

    private static readonly Regex RefiiCall = new(@"\bRefii\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex LuaComment = new(@"--[^\n]*", RegexOptions.Compiled);

    private static string Read(string root, params string[] parts) =>
        System.IO.File.ReadAllText(Path.Combine(new[] { GodotModScripts.ModFolder(root, Mod) }.Concat(parts).ToArray()));

    private static string Camera(string root) => LuaComment.Replace(Read(root, "mod", "camera.lua"), string.Empty);

    /// <summary>R3 toggles on a controller and is taken from the game, except while L3 is held for Photo mode.</summary>
    [TestCase]
    public void TheControllerTogglesOnR3AndLeavesPhotoMode()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string script = Read(root, "godot", File);
        AssertThat(Read(root, "input.json")).Contains("\"buttons\": [\"R3\"]");
        AssertThat(script).Contains("host.claim_input(1, TOGGLE, not l3_held)");
        AssertThat(script).Contains("host.claim_input(2, TOGGLE, not l3_held)");
        AssertThat(script).Contains("JOY_BUTTON_LEFT_STICK");
        AssertThat(script).Contains("and not l3_held and");
    }

    [TestCase]
    public void ShipsTheGodotSwitchAndOnlyTheCameraLua()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        GodotModScripts.AssertShips(GodotModScripts.ModFolder(root, Mod), new[] { "mod/camera.lua" }, new[] { "godot/" + File });
    }

    [TestCase]
    public void EveryHostMemberExistsAndTheHoldIsPerPlayer()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string text = GodotModScripts.Script(root, Mod, File);
        string code = GodotModScripts.Code(text);
        var called = GodotModScripts.AssertHostContract(root, File, text);
        foreach (string name in new[] { "camera_override", "in_cutscene", "coop_mode", "split_screen", "player_here", "player_set", "setting", "constants" })
        {
            AssertThat(called.Contains(name)).OverrideFailureMessage(File + " never calls host." + name).IsTrue();
        }

        AssertThat(GodotModScripts.Constants(code).ToList()).ContainsExactlyInAnyOrder("CAMERA_FIRST_PERSON", "CUTSCENE_INTERACTIVE");

        // The hold is per player: stated for the pressing player and let go with 0; none on a shared screen.
        AssertThat(code.Contains("host.camera_override(p, first_person, { \"fov\": fov, \"pitch_limit\": pitch, \"aim\": aim })", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("host.camera_override(p, 0)", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("on[p] = not on[p]", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("return host.coop_mode() != 1 or host.split_screen()", StringComparison.Ordinal)).IsTrue();

        // camera.lua's state comes through the player store, written only where the player is played.
        AssertThat(code.Contains("const HELD := \"first_person.held\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(code, @"if host\.player_here\(p\):\s*\n\s*host\.player_set\(p, HELD, 1\.0 if held else 0\.0\)")).IsTrue();

        // The 0.1 workarounds stay gone, and no Lua is run from the script.
        foreach (string gone in new[] { "hide_part", "head_radius", "local_hero", "run_lua", "call_lua", "signal_lua", "node_for" })
        {
            AssertThat(code.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage(File + " still uses " + gone).IsFalse();
        }
    }

    [TestCase]
    public void TheCameraLuaReadsTheHoldTheScriptStates()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModApiHelper helper in ModApi.LuaHelpers)
        {
            foreach (Match m in RefiiCall.Matches(helper.Signature))
            {
                known.Add(m.Groups[1].Value);
            }
        }

        known.UnionWith(ModApi.Constants.Select(c => c.Name));
        string preamble = ModLua.RuntimePreamble(Array.Empty<(string, float)>());
        foreach (string bit in new[] { "CUTSCENE_FULL" })
        {
            AssertThat(preamble.Contains("Refii." + bit + " = ", StringComparison.Ordinal)).OverrideFailureMessage("the preamble lacks " + bit).IsTrue();
            known.Add(bit);
        }

        string camera = Camera(root);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in RefiiCall.Matches(camera))
        {
            used.Add(m.Groups[1].Value);
            AssertThat(known.Contains(m.Groups[1].Value)).OverrideFailureMessage($"camera.lua calls Refii.{m.Groups[1].Value}, which the catalog does not offer").IsTrue();
        }

        foreach (string name in new[] { "player_value", "player_of", "handle", "in_cutscene", "CUTSCENE_FULL", "setting" })
        {
            AssertThat(used.Contains(name)).OverrideFailureMessage("camera.lua never uses Refii." + name).IsTrue();
        }

        AssertThat(camera.Contains("Refii.player_value(p, \"first_person.held\", 0)", StringComparison.Ordinal)).IsTrue();

        // Nothing of init.lua's: no FirstPerson table, no Held mirror, no hold stated from Lua.
        foreach (string gone in new[] { "FirstPerson.", "F.Held", "camera_override", "FirstPersonCamera.IsValid", "IsMultiplayer", "head_radius", "hide_part", "kFull", "local_hero" })
        {
            AssertThat(camera.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage("camera.lua still uses " + gone).IsFalse();
        }

        // camera.lua keeps only the wraps the game's camera takes no data for.
        foreach (string kept in new[]
                 {
                     "FirstPersonCamera.Activate =", "FirstPersonCamera.ProcessGameAction =", "FirstPersonCamera.FrameUpdate =",
                     "set.BestCameraFunction =", "GetDesiredPosFromPlayer", "face_camera", "OverrideFocus",
                 })
        {
            AssertThat(camera.Contains(kept, StringComparison.Ordinal)).OverrideFailureMessage("camera.lua lacks " + kept).IsTrue();
        }

        AssertThat(camera.Contains("FadeEntityBasedOnPositionIfNeeded", StringComparison.Ordinal)).IsFalse();
        AssertThat(camera.Contains("SetDesiredFOV", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void EveryEventHeardHasTheFieldsRead()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        GodotModScripts.AssertEvents(File, code,
            ("input_action", new[] { "name", "player", "pressed" }),
            ("cutscene", new[] { "player", "kind" }),
            ("hero_ready", Array.Empty<string>()),
            ("hero_gone", Array.Empty<string>()),
            ("lua_ready", Array.Empty<string>()));
        AssertThat(GodotModScripts.Signals(code).Contains("setting_changed")).IsTrue();
    }

    [TestCase]
    public void TheDataFilesValidateAndEverySettingIsRead()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        (int checkedFiles, var warnings) = GodotModScripts.AssertDataValidates(GodotModScripts.ModFolder(root, Mod));
        AssertThat(checkedFiles).IsEqual(4);   // input.json, settings.json, settings.ui.json, mod.toml
        AssertThat(warnings.Count).IsEqual(0);

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        string input = Read(root, "input.json");
        AssertThat(Regex.Matches(input, @"^\s*""([a-z0-9_]+)""\s*:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList())
            .ContainsExactly("toggle");
        AssertThat(code.Contains("\"first_person.toggle\"", StringComparison.Ordinal)).IsTrue();

        string settings = Read(root, "settings.json");
        string page = Read(root, "settings.ui.json");
        string camera = Camera(root);
        var keys = Regex.Matches(settings, @"""([a-z_]+)"":").Select(m => m.Groups[1].Value).Where(k => k != Mod).ToList();
        AssertThat(keys).ContainsExactlyInAnyOrder("start_on", "fov", "eye_height", "eye_forward", "pitch_limit", "face_camera", "third_person_aim", "scenes");
        foreach (string key in keys)
        {
            AssertThat(code.Contains("_setting(\"" + key + "\"", StringComparison.Ordinal) || camera.Contains("Setting(\"" + key + "\"", StringComparison.Ordinal))
                .OverrideFailureMessage("never read: " + key).IsTrue();
            AssertThat(page.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.ui.json lacks " + key).IsTrue();
        }

        // The hold's options are stated again when their setting changes.
        foreach (string key in new[] { "fov", "pitch_limit", "third_person_aim", "scenes" })
        {
            AssertThat(code.Contains("\"first_person." + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("not restated on " + key).IsTrue();
        }

        AssertThat(Read(root, "README.md").Contains("written as a Godot script", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCoreHidesAHeroHeadExactlyInItsPlayersStatedFirstPerson()
    {
        AssertThat(Engine_FirstPersonView.FirstPersonMode).IsEqual(ModApi.Constants.Single(c => c.Name == "CAMERA_FIRST_PERSON").Value);

        // A hero's player is its player-map key; anything else is no player.
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40001000u, 0x40001000u, 0x40002000u)).IsEqual(1);
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40002000u, 0x40001000u, 0x40002000u)).IsEqual(2);
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40003000u, 0x40001000u, 0x40002000u)).IsEqual(0);
        AssertThat(Engine_FirstPersonView.PlayerOf(0u, 0u, 0u)).IsEqual(0);
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40001000u, 0x40001000u, 0u)).IsEqual(1);

        // Hidden only under CAMERA_FIRST_PERSON stated for that hero's own player.
        AssertThat(Engine_FirstPersonView.HeadHidden(1, 8)).IsTrue();
        AssertThat(Engine_FirstPersonView.HeadHidden(2, 8)).IsTrue();
        foreach (int mode in new[] { -1, 0, 1, 2, 3, 4, 7, 9, 10, 11 })
        {
            AssertThat(Engine_FirstPersonView.HeadHidden(1, mode)).OverrideFailureMessage("hidden under mode " + mode).IsFalse();
        }

        AssertThat(Engine_FirstPersonView.HeadHidden(0, 8)).IsFalse();
        AssertThat(Engine_FirstPersonView.HeadHidden(3, 8)).IsFalse();
    }

    [TestCase]
    public void TheHeadHiderFollowsTheStatedModeNotADistance()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string hider = System.IO.File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "entities", "Engine_FirstPersonHead.cs"));
        AssertThat(hider.Contains("Engine_FirstPersonView.HeadHidden(_player, mode)", StringComparison.Ordinal)).IsTrue();
        AssertThat(hider.Contains("camera_mode(_player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(hider.Contains("Radius", StringComparison.Ordinal)).IsFalse();
        AssertThat(hider.Contains("DistanceTo", StringComparison.Ordinal)).IsFalse();

        // The host hears camera_changed with or without a mod script loaded.
        string host = System.IO.File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("own = own.With(CameraChangedKind);", StringComparison.Ordinal)).IsTrue();

        string entities = System.IO.File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameEntities.cs"));
        AssertThat(entities.Contains("Engine_FirstPersonView.PlayerOf(entity.Address, _principals.One, _principals.Two)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCameraLuaCompilesWithTheGamesCompiler()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string text = Read(root, "mod", "camera.lua");
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(ModLua.Wrap(Mod, text), "@first_person/mod/camera.lua", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;
        }

        AssertThat(ok).OverrideFailureMessage("camera.lua does not compile: " + error).IsTrue();
        AssertThat(Refii.Lua.Engine_LuaDecompiler.TryCheckSource(ModLua.Wrap(Mod, text + "\nlocal function ("),
            "@first_person/mod/camera.lua", out _)).IsFalse();
    }
}
