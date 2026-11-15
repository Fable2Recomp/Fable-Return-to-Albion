namespace Refii.Titles.Fable3;

using System.Text;
using Refii.Bnk;

/// <summary>
/// Fable 3's helper calls for mods' Lua: the gameplay half of the Fable 2 surface (health, gold,
/// items, stats, position, sound, spawning, time), written over the game's own natives as the shipped
/// scripts call them. Run in the game VM before any mod's init.lua (Fable3/src/hooks/src/
/// mod_lua_hooks.cpp runs <see cref="Key"/> first), so a mod finds them on <c>Refii</c>. Every call
/// takes a handle or a game Entity; a dead or unknown one answers nil and changes nothing.
/// </summary>
/// <remarks>
/// The natives' forms are the shipped scripts' own (read from the decompiled scripts, 2026-11-16):
/// <c>Money.Add(e, n, EMoneyChangeType.INCOME_TYPE_SCRIPT)</c> / <c>Money.Remove(e, n, type)</c>
/// (moneyenums.lua: INCOME_TYPE_SCRIPT 6, OUTGOING_TYPE_OTHER 31), <c>Health.Get/GetMax(e)</c>,
/// <c>Health.Modify(e, delta, source)</c>, <c>Physics.TeleportToPosition(e, CVector3(x, y, z))</c>,
/// <c>e:GetPosition()</c> (a vector with GetX/GetY/GetZ), <c>Inventory.AddItemOfType(e, name)</c>,
/// <c>Inventory.GetNumberOfItemsOfType(e, name)</c>, <c>Inventory.RemoveItemOfType(e, name)</c>,
/// <c>Stats.GetMorality/ModifyMorality(e, d)</c>, <c>Stats.GetRenownTotal(e)</c>,
/// <c>Stats.ModifyRenown(e, d)</c>, <c>Stats.GetNumberOfGuildSeals/ModifyGuildSeals(e, n)</c>,
/// <c>Sound.PlayEvent(e, cue, tag)</c>, <c>Debug.CreateEntityAtPosition(def, name, position)</c>,
/// <c>Timing.GetTimeOfDay()</c> / <c>Timing.SetTimeOfDay(h)</c>. Items are named by their definition
/// name ("ObjectInventoryWeaponRifleBasic"), as Fable 3's scripts name them.
/// </remarks>
public static class Fable3CoreLua
{
	public const string Key = "refii/core_init.lua";

	public const string Source =
		"Refii = Refii or {}\n" +
		"local E = function(e) return Refii.entity and Refii.entity(e) or nil end\n" +
		"local INCOME_SCRIPT, OUTGOING_OTHER = 6, 31\n" +
		"function Refii.local_hero() return Refii.player_hero(1) end\n" +
		"function Refii.health(e) local x = E(e) if x == nil then return nil end return Health.Get(x), Health.GetMax(x) end\n" +
		"function Refii.set_health(e, v) local x = E(e) if x == nil then return false end Health.Modify(x, v - Health.Get(x), x) return true end\n" +
		"function Refii.modify_health(e, delta, attacker) local x = E(e) if x == nil then return false end\n" +
		"\tHealth.Modify(x, delta, attacker ~= nil and E(attacker) or x) return true end\n" +
		"function Refii.set_invulnerable(e, on) local x = E(e) if x == nil then return false end Health.SetAsInvulnerable(x, on and true or false) return true end\n" +
		"function Refii.position(e) local x = E(e) if x == nil then return nil end local p = x:GetPosition() return p:GetX(), p:GetY(), p:GetZ() end\n" +
		"function Refii.set_position(e, px, py, pz) local x = E(e) if x == nil then return false end\n" +
		"\tPhysics.TeleportToPosition(x, CVector3(px, py, pz)) return true end\n" +
		"function Refii.name(e) local x = E(e) if x == nil then return nil end return x:GetName() end\n" +
		"function Refii.gold(e) local x = E(e) if x == nil then return nil end return Money.Get(x) end\n" +
		"function Refii.give_gold(e, delta) local x = E(e) if x == nil then return false end\n" +
		"\tif delta > 0 then Money.Add(x, delta, INCOME_SCRIPT) elseif delta < 0 then Money.Remove(x, - delta, OUTGOING_OTHER) end\n" +
		"\treturn true end\n" +
		"function Refii.item_count(e, def) local x = E(e) if x == nil then return 0 end return Inventory.GetNumberOfItemsOfType(x, def) end\n" +
		"function Refii.add_item(e, def, n) local x = E(e) if x == nil then return 0 end n = n or 1\n" +
		"\tfor i = 1, n do Inventory.AddItemOfType(x, def) end return n end\n" +
		"function Refii.remove_item(e, def, n) local x = E(e) if x == nil then return 0 end n = n or 1\n" +
		"\tlocal removed = 0 for i = 1, n do if Inventory.GetNumberOfItemsOfType(x, def) < 1 then break end\n" +
		"\tInventory.RemoveItemOfType(x, def) removed = removed + 1 end return removed end\n" +
		"function Refii.morality(e) local x = E(e) if x == nil then return nil end return Stats.GetMorality(x) end\n" +
		"function Refii.modify_morality(e, delta) local x = E(e) if x == nil then return false end Stats.ModifyMorality(x, delta) return true end\n" +
		"function Refii.renown(e) local x = E(e) if x == nil then return nil end return Stats.GetRenownTotal(x) end\n" +
		"function Refii.modify_renown(e, delta) local x = E(e) if x == nil then return false end Stats.ModifyRenown(x, delta) return true end\n" +
		"function Refii.guild_seals(e) local x = E(e) if x == nil then return nil end return Stats.GetNumberOfGuildSeals(x) end\n" +
		"function Refii.give_guild_seals(e, n) local x = E(e) if x == nil then return false end Stats.ModifyGuildSeals(x, n) return true end\n" +
		"function Refii.play_sound(cue, e, tag) local x = E(e) if x == nil then return false end Sound.PlayEvent(x, cue, tag or cue) return true end\n" +
		"function Refii.spawn(def, px, py, pz, name)\n" +
		"\tlocal x = Debug.CreateEntityAtPosition(def, name or def, CVector3(px, py, pz))\n" +
		"\tif x == nil then return nil end return Refii.handle(x) end\n" +
		"function Refii.time_of_day() return Timing.GetTimeOfDay() end\n" +
		"function Refii.set_time_of_day(hours) Timing.SetTimeOfDay(hours) return true end\n";

	/// <summary>Mounts the chunk at <see cref="Key"/>, owned by Refii, for the payload to read.</summary>
	public static void Attach(BnkFileSystem mounted)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(Source);
		mounted.AddVirtualFile(Key, Key, bytes.Length, () => bytes, "Refii");
	}
}
