namespace Refii.Tests.Modding;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// A live setting reaches the running Lua (hard_mods_plan H.16): <see cref="ModSettings.Set"/> —
/// the one seam the Options › Mods page, a Godot script's <c>set_setting</c> and the debug
/// channel's <c>mod-setting</c> cross — posts the assignment into the game VM's
/// <c>RefiiSettings</c> (and the front end's state while no game is up), and the preamble composes
/// the table from the values IN FORCE, overrides included, so a later chunk starts from them.
/// </summary>
[TestSuite]
public class ModSettingsLuaTests
{
    [TestCase]
    public void TheAssignmentIsOneLineOfLua()
    {
        AssertThat(ModSettings.LuaAssignment("souls_combat.swing_cost", 18.5f))
            .IsEqual("if RefiiSettings then RefiiSettings[\"souls_combat.swing_cost\"] = 18.5 end");
        AssertThat(ModSettings.LuaAssignment("spell_weaving.woven", 1f))
            .IsEqual("if RefiiSettings then RefiiSettings[\"spell_weaving.woven\"] = 1 end");
        // A quote in a key is escaped; a key no one line can carry is refused; a non-number is 0.
        AssertThat(ModSettings.LuaAssignment("a\"b", 2f)).IsEqual("if RefiiSettings then RefiiSettings[\"a\\\"b\"] = 2 end");
        AssertThat(ModSettings.LuaAssignment("a\nb", 2f)).IsEqual(string.Empty);
        AssertThat(ModSettings.LuaAssignment(string.Empty, 2f)).IsEqual(string.Empty);
        AssertThat(ModSettings.LuaAssignment("k", float.NaN)).IsEqual("if RefiiSettings then RefiiSettings[\"k\"] = 0 end");
        AssertThat(ModSettings.LuaAssignment("k", -0.25f)).IsEqual("if RefiiSettings then RefiiSettings[\"k\"] = -0.25 end");
    }

    [TestCase]
    public void TheAssignmentCompilesWithTheGamesCompiler()
    {
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(
            ModSettings.LuaAssignment("souls_combat.swing_cost", 18.5f), "@mod-setting", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;
        }

        AssertThat(ok).OverrideFailureMessage("does not compile: " + error).IsTrue();
    }

    [TestCase]
    public void TheSeamPostsToBothVmsAndThePreambleComposesWhatIsInForce()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // Pinned in the source: ModSettings touches the Godot runtime (FileAccess), so its
        // behaviour is held by its text, as ModApiTests holds the preamble's.
        string settings = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModSettings.cs"));
        AssertThat(settings.Contains("HostBridge.PostModCommand(\"run_lua \" + assignment);", StringComparison.Ordinal)).IsTrue();
        AssertThat(settings.Contains("HostBridge.PostModCommand(ModLua.FrontendCommand + assignment);", StringComparison.Ordinal)).IsTrue();
        // Persist (the options page) and the Godot surface and the debug channel all go through Set.
        AssertThat(settings.Contains("Set(key, value);\n\t\tSavePlayer();", StringComparison.Ordinal)).IsTrue();
        string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("public void set_setting(string key, float value) => ModSettings.Set(key, value);", StringComparison.Ordinal)).IsTrue();
        string debug = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameDebugServer.cs"));
        AssertThat(debug.Contains("Refii.Mods.ModSettings.Set(parts[1], setting);", StringComparison.Ordinal)).IsTrue();
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("foreach ((string key, float value) in ModSettings.InForce)", StringComparison.Ordinal)).IsTrue();
        // Both example mods read their settings at the call, so the new value is in effect at once.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Fable2", "mods-src", "souls_combat", "mod"), "*.lua"))
        {
            string text = File.ReadAllText(file);
            if (Path.GetFileName(file) is "stamina.lua" or "roll.lua" or "guard.lua" or "mana.lua" or "poise.lua" or "commitment.lua" or "cast_recovery.lua" or "lockon.lua")
            {
                AssertThat(text.Contains("Setting(\"", StringComparison.Ordinal)).OverrideFailureMessage(file).IsTrue();
            }
        }

        string weaving = File.ReadAllText(Path.Combine(root, "Fable2", "mods-src", "spell_weaving", "mod", "init.lua"));
        AssertThat(weaving.Contains("return Refii.setting(\"spell_weaving.\" .. key, default)", StringComparison.Ordinal)).IsTrue();
    }

    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }
}
