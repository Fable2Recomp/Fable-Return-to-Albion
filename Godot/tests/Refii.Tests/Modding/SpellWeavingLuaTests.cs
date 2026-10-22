namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// SPELL WEAVING IS PURE LUA ON THE MOD API — the proof that the API is complete for it. The mod
/// ships no <c>native/</c> folder; every <c>Refii.*</c> name its Lua calls is in the catalog
/// (<see cref="ModApi.LuaHelpers"/>), every event it subscribes is a catalogued event, the chunk
/// compiles with the game's own compiler the way the init chunk is composed, and the keys it shares
/// with its declared UI (the pick's store, the ability's level, the HUD glyph) are the ones the
/// JSON states.
/// </summary>
[TestSuite]
public class SpellWeavingLuaTests
{
	[TestCase]
	public void TheModShipsNoNativeCode()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		AssertThat(Directory.Exists(Path.Combine(mod, "native"))).OverrideFailureMessage("mods-src/spell_weaving/native still exists").IsFalse();
		AssertThat(File.Exists(Path.Combine(mod, "mod", "init.lua"))).IsTrue();
		AssertThat(Directory.EnumerateFiles(mod, "*.cpp", SearchOption.AllDirectories).Any()).IsFalse();
		AssertThat(Directory.EnumerateFiles(mod, "*.h", SearchOption.AllDirectories).Any()).IsFalse();
		AssertThat(File.ReadAllText(Path.Combine(mod, "mod.toml")).Contains("native", StringComparison.OrdinalIgnoreCase)).IsFalse();
	}

	[TestCase]
	public void EveryRefiiCallIsInTheCatalog()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		var called = Regex.Matches(lua, @"Refii\.([a-z_]+)").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
		AssertThat(called.Count > 10).OverrideFailureMessage("the Lua calls almost nothing of the API").IsTrue();
		string catalog = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
		foreach (string name in called)
		{
			bool listed = catalog.Contains("Refii." + name + "(", StringComparison.Ordinal) ||
				catalog.Contains("Refii." + name + "\n", StringComparison.Ordinal) || catalog.EndsWith("Refii." + name, StringComparison.Ordinal);
			AssertThat(listed).OverrideFailureMessage($"Refii.{name} is not in ModApi.LuaHelpers").IsTrue();
		}

		// No private native (Refii._x), no raw read, no hook: the public surface only.
		AssertThat(Regex.IsMatch(lua, @"Refii\._")).IsFalse();

		var events = Regex.Matches(lua, @"Refii\.on\(""([a-z_]+)""").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
		foreach (string name in events)
		{
			AssertThat(ModApi.Find(name)).OverrideFailureMessage($"\"{name}\" is not a catalogued event").IsNotNull();
		}

		foreach (string required in new[]
		         {
			         "spell_launch", "spell_created", "hit", "hit_landed", "spell_pick", "spell_charge_level", "spell_launched",
			         "vortex_started", "vortex_ended", "fireball_impact", "tick", "lua_ready", "save_loaded",
		         })
		{
			AssertThat(events.Contains(required)).OverrideFailureMessage($"the Lua never subscribes {required}").IsTrue();
		}

		// The primitives H.15 added for exactly this port.
		foreach (string primitive in new[] { "spell_in_slot", "spell_parts", "cast_spell", "entity_tint", "play_fx_on", "play_fx", "stop_fx", "player_save", "player_set", "player_value", "player_here", "world_here", "event_replica", "ability_level" })
		{
			AssertThat(called.Contains(primitive)).OverrideFailureMessage($"the Lua never calls Refii.{primitive}").IsTrue();
		}
	}

	[TestCase]
	public void TheChunkCompilesWithTheGamesCompiler()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		string source = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		string wrapped = ModLua.Wrap("Spell Weaving", source);
		bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(wrapped, "@spell_weaving/mod/init.lua", out string error);
		if (!ok && error.Contains("not available", StringComparison.Ordinal))
		{
			// No refii_luadec beside the test host: the same compiler as the refii-luac32 tool, when built.
			string? tool = LuacTool();
			if (tool == null)
			{
				return;
			}

			var start = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
			foreach (string arg in new[] { "/dev/stdin", "-o", "/dev/null", "-n", "@spell_weaving/mod/init.lua" })
			{
				start.ArgumentList.Add(arg);
			}

			using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
			var stderr = process.StandardError.ReadToEndAsync();
			byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(wrapped);
			process.StandardInput.BaseStream.Write(utf8, 0, utf8.Length);
			process.StandardInput.Close();
			process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			ok = process.ExitCode == 0;
			error = stderr.Result.Trim();
		}

		AssertThat(ok).OverrideFailureMessage($"mod/init.lua does not compile: {error}").IsTrue();
	}

	[TestCase]
	public void TheLuaAndTheDeclaredUiShareTheirKeys()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		using JsonDocument wheel = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "ui", "spellwheel.json")));
		JsonElement pick = wheel.RootElement.GetProperty("picks")[0];
		AssertThat(pick.GetProperty("name").GetString()).IsEqual("spell_weaving.partner");
		AssertThat(lua.Contains("\"spell_weaving.partner\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(pick.GetProperty("store").GetString()).IsEqual("spell_weaving.partner_{slot}");
		AssertThat(lua.Contains("\"spell_weaving.partner_\" .. (slot + 1)", StringComparison.Ordinal)).IsTrue();
		AssertThat(pick.GetProperty("inEffect").GetString()).IsEqual("player:spell_weaving.partner_active_{slot}");
		AssertThat(lua.Contains("\"spell_weaving.partner_active_\" .. (slot + 1)", StringComparison.Ordinal)).IsTrue();
		AssertThat(pick.GetProperty("unlocked").GetString()).IsEqual("player:spell_weaving.ability");
		AssertThat(lua.Contains("\"spell_weaving.ability\"", StringComparison.Ordinal)).IsTrue();
		// The pick's exclusions are the Lua's refusal: Slow Time (2) and Raise Dead (7).
		AssertThat(string.Join(",", pick.GetProperty("exclude").EnumerateArray().Select(e => e.GetInt32()))).IsEqual("2,7");
		AssertThat(lua.Contains("t ~= kSlowTime and t ~= kDeadRising", StringComparison.Ordinal)).IsTrue();

		using JsonDocument hud = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "ui", "hud.json")));
		AssertThat(hud.RootElement.GetProperty("pieces")[0].GetProperty("index").GetString()).IsEqual("player:spell_weaving.hud_partner");
		AssertThat(lua.Contains("\"spell_weaving.hud_partner\"", StringComparison.Ordinal)).IsTrue();

		using JsonDocument abilities = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "ui", "abilities.json")));
		AssertThat(abilities.RootElement.GetProperty("abilities")[0].GetProperty("levelKey").GetString()).IsEqual("spell_weaving.ability");

		// Every literal setting the Lua reads is stated in settings.json; the sword tints by lane.
		using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "settings.json")));
		JsonElement weaving = settings.RootElement.GetProperty("spell_weaving");
		foreach (Match m in Regex.Matches(lua, @"Knob\(""([a-z_]+)"", "))
		{
			AssertThat(weaving.TryGetProperty(m.Groups[1].Value, out _)).OverrideFailureMessage($"settings.json lacks spell_weaving.{m.Groups[1].Value}").IsTrue();
		}

		foreach (string name in new[] { "fire", "shock", "chaos", "push" })
		{
			foreach (char lane in "rgb")
			{
				AssertThat(weaving.TryGetProperty($"sword_tint_{name}_{lane}", out _)).IsTrue();
			}
		}

		// The look names the Lua builds are the effects the mod ships: spell_weaving_<base>_<overlay>.
		var shipped = Directory.EnumerateFiles(Path.Combine(mod, "fx", "effects"), "*.json").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
		string[] names = { "shock", "fire", "blades", "vortex", "chaos", "push" };
		int[] rank = { 3, 2, 1, 0, 5, 4 };
		for (int i = 0; i < names.Length; i++)
		{
			for (int j = 0; j < names.Length; j++)
			{
				if (i != j && rank[i] < rank[j])
				{
					AssertThat(shipped.Contains($"spell_weaving_{names[i]}_{names[j]}")).OverrideFailureMessage($"no effect for {names[i]} + {names[j]}").IsTrue();
				}
			}
		}

		AssertThat(shipped.Count).IsEqual(15);
	}

	private static string? LuacTool()
	{
		string? at = ModFolder();
		if (at == null || !OperatingSystem.IsLinux())
		{
			return null;
		}

		string builds = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(at)!)!, "out", "build");
		return Directory.Exists(builds)
			? Directory.GetDirectories(builds).Select(d => Path.Combine(d, "tools", "refii-luac32")).Where(File.Exists)
				.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
			: null;
	}

	private static string? ModFolder()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string candidate = Path.Combine(at, "mods-src", "spell_weaving");
			if (Directory.Exists(Path.Combine(candidate, "fx", "effects")))
			{
				return candidate;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}
}
