namespace Refii.Tests.Modding;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// A mod's Lua in several files (hard_mods_plan H.16): every <c>mod/*.lua</c> is composed — the
/// three entry points by role, the rest as modules a chunk reaches with <c>require(name)</c> —
/// and the require runtime, the module wrapper and the entry wrapper all compile with the game's
/// own compiler. The runtime's semantics (once, cached, nil as true, a cycle sees true, a missing
/// module errors by file name) are stated in <see cref="ModLua.RequireRuntime"/> and pinned here
/// by the text the preamble carries.
/// </summary>
[TestSuite]
public class ModLuaModulesTests
{
    [TestCase]
    public void EveryLuaUnderModIsComposed()
    {
        foreach (string path in new[] { "mod/init.lua", "mod/camera.lua", "mod/frontend.lua", "mod/stamina.lua", "mod/ai/wheel.lua", "MOD\\Lockon.LUA" })
        {
            AssertThat(ModLua.IsModLuaPath(path)).OverrideFailureMessage(path).IsTrue();
        }

        foreach (string path in new[] { "ui/hud.lua", "mod/", "mod/.lua", "init.lua", "mods/init.lua", "mod/notes.txt" })
        {
            AssertThat(ModLua.IsModLuaPath(path)).OverrideFailureMessage(path).IsFalse();
        }

        AssertThat(ModLua.IsEntryPath("mod/init.lua")).IsTrue();
        AssertThat(ModLua.IsEntryPath("mod/Camera.lua")).IsTrue();
        AssertThat(ModLua.IsEntryPath("mod/frontend.lua")).IsTrue();
        AssertThat(ModLua.IsEntryPath("mod/stamina.lua")).IsFalse();
    }

    [TestCase]
    public void AModuleIsNamedByItsPathUnderMod()
    {
        AssertThat(ModLua.TryModuleName("mod/stamina.lua", out string name)).IsTrue();
        AssertThat(name).IsEqual("stamina");
        AssertThat(ModLua.TryModuleName("mod/AI/Wheel.lua", out name)).IsTrue();
        AssertThat(name).IsEqual("ai/wheel");
        AssertThat(ModLua.TryModuleName("/mod/cast_recovery.lua", out name)).IsTrue();
        AssertThat(name).IsEqual("cast_recovery");
        // The entry points are not modules; nothing outside mod/ is.
        AssertThat(ModLua.TryModuleName("mod/init.lua", out _)).IsFalse();
        AssertThat(ModLua.TryModuleName("mod/camera.lua", out _)).IsFalse();
        AssertThat(ModLua.TryModuleName("mod/frontend.lua", out _)).IsFalse();
        AssertThat(ModLua.TryModuleName("ui/x.lua", out _)).IsFalse();
    }

    [TestCase]
    public void TheWrappersCarryTheModsOwnRequire()
    {
        string wrapped = ModLua.Wrap("my\"mod", "return 1");
        AssertThat(wrapped.StartsWith("do\nlocal require = Refii._require(\"my\\\"mod\")\nlocal ok, err = pcall(function()\nreturn 1\nend)", StringComparison.Ordinal)).IsTrue();
        AssertThat(wrapped.EndsWith("end\n", StringComparison.Ordinal)).IsTrue();

        string module = ModLua.WrapModule("mymod", "ai/wheel", "local t = {}\nreturn t");
        AssertThat(module).IsEqual(
            "local __scope = Refii._mod_scope(\"mymod\")\n" +
            "__scope.modules[\"ai/wheel\"] = function(...)\n" +
            "local t = {}\nreturn t\n" +
            "end\n");

        // The runtime: a fresh scope per registration, require by lower-cased name with . as /,
        // the cache, nil as true, the cycle guard, the error naming the file.
        string runtime = ModLua.RequireRuntime;
        AssertThat(runtime.Contains("function Refii._mod_scope(mod)", StringComparison.Ordinal)).IsTrue();
        AssertThat(runtime.Contains("local scope = { modules = {}, loaded = {} }", StringComparison.Ordinal)).IsTrue();
        AssertThat(runtime.Contains("name = string.lower((string.gsub(tostring(name), '%.', '/')))", StringComparison.Ordinal)).IsTrue();
        AssertThat(runtime.Contains("error('require: no mod/' .. name .. '.lua in ' .. tostring(mod), 2)", StringComparison.Ordinal)).IsTrue();
        AssertThat(runtime.Contains("scope.loaded[name] = true\n\t\tlocal result = module(name)\n\t\tif result == nil then result = true end", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheRuntimeAndBothWrappersCompileWithTheGamesCompiler()
    {
        string chunk = ModLua.RequireRuntime +
            "do\nlocal require = Refii._require(\"m\")\n" +
            ModLua.WrapModule("m", "common", "local C = {}\nfunction C.Setting(k, d) return Refii.setting(k, d) end\nreturn C") +
            ModLua.Wrap("m", "local C = require(\"common\")\nC.Setting(\"x\", 1)") +
            "end\n";
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource("Refii = {}\n" + chunk, "@modules", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;  // no compiler in this test host: the mount checks the chunk at run time
        }

        AssertThat(ok).OverrideFailureMessage("does not compile: " + error).IsTrue();
    }

    [TestCase]
    public void TheComposerReadsEveryFileOncePerPackage()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The composer's contract, pinned in the source: one package read serves the three chunks,
        // modules ride the init and front-end chunks (never the camera's, which shares the game
        // VM's registry), and a file that fails the compile check is left out by name.
        string source = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(source.Contains("List<ModSources> sources = Read(overlays);", StringComparison.Ordinal)).IsTrue();
        AssertThat(source.Contains("bool modules = mod.Modules.Count != 0 && key != CameraKey;", StringComparison.Ordinal)).IsTrue();
        AssertThat(source.Contains("Checked(overlay.Name, path, source, WrapModule(overlay.Name, name, source))", StringComparison.Ordinal)).IsTrue();
        AssertThat(source.Contains("text.Append(RequireRuntime);", StringComparison.Ordinal)).IsTrue();
        // The bank mount and the manifest skip every composed path, so a module is never served as an asset.
        string bank = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "bnk", "Engine_BnkFileSystem.cs"));
        AssertThat(bank.Contains("Refii.Mods.ModLua.IsModLuaPath(entry.Path)", StringComparison.Ordinal)).IsTrue();
        string manifest = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModManifest.cs"));
        AssertThat(manifest.Contains("ModLua.IsModLuaPath(normalized)", StringComparison.Ordinal)).IsTrue();
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
