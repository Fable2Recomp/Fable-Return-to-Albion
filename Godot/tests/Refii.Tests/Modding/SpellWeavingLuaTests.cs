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
			         "spell_launch", "spell_created", "hit_landed", "spell_pick", "spell_charge_level", "spell_charge_ended",
			         "vortex_started", "vortex_ended", "fireball_impact", "summoned", "entity_removed", "spell_ended",
			         "status_applying", "hero_ready", "ability_level", "mod_ability", "tick", "lua_ready", "save_loaded",
			         "vortex_caught", "hit",
		         })
		{
			AssertThat(events.Contains(required)).OverrideFailureMessage($"the Lua never subscribes {required}").IsTrue();
		}

		// The primitives H.15 and Part I added for exactly this port.
		foreach (string primitive in new[] { "spell_in_slot", "spell_parts", "cast_spell", "entity_tint", "play_fx_on", "play_fx", "stop_fx", "player_set", "player_value", "player_here", "world_here", "event_replica", "spells_known", "after", "now", "has_status", "entity_value", "entity_set", "apply_status", "knockback", "vortex_held", "spell_balance", "creature_tier", "living" })
		{
			AssertThat(called.Contains(primitive)).OverrideFailureMessage($"the Lua never calls Refii.{primitive}").IsTrue();
		}
	}

	/// <summary>
	/// Part I: the reconstruction is gone. A cast is known by the tag its launch wrote and its
	/// partner casts by that tag negated (no serial ring, no in-flight cast record, no binding by
	/// caster and type); the partner's damage is the cast's damage_scale and its status lasts
	/// weave_duration (status_applying); a spell ends on spell_ended, a charge on
	/// spell_charge_ended, a sword arrives on summoned; the delay is Refii.after; the pick's
	/// exclusion and the save from before are the engine's.
	/// </summary>
	[TestCase]
	public void TheCastIsStatedNotReconstructed()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		foreach (string gone in new[]
		         {
			         "weave_serials", "IsWeaveSerial", "casting =", "armed[", "ArmWeave", "kChargeHoldSeconds", "charging_age",
			         "w.age", "LiveSwords", "RefiiModSave", "player_save", "queued = queued", "rand * 75", "[kChaos] = 6.0",
			         "kFirstSpellAbility", "Refii.on(\"spell_launched\"",
			         // The partner's placement is cast_spell's `at`, not the mod's own reading of the Starts.
			         "start[3] = target[3] + 15", "facing = { 0, 0, -1 }", "start[i] = target[i] - dir[i]",
			         "Refii.position(cast.victim)",
		         })
		{
			AssertThat(lua.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage($"init.lua still has {gone}").IsFalse();
		}

		AssertThat(lua.Contains("e.tag = w.tag", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("{ at = at, tag = cast.tag, damage_scale = Knob(\"weave_damage\", 0.7) }", StringComparison.Ordinal)).IsTrue();
		// The vortex casts Blades by Fable III's rule: on a creature it catches (never a prop, a sword
		// or another spell's entity: the creature component, living), by the capture's tag, once per
		// creature per vortex, after Fable III's roll and at the moment it picks, held then and at the
		// cast (its own held list), at the Blades power whose own swords are Fable III's count.
		string caught = Regex.Match(lua, @"Refii\.on\(""vortex_caught"".*?\nend\)", RegexOptions.Singleline).Value;
		AssertThat(caught.Contains("local w = WeaveOf(e.tag, e.serial)", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("not e.caught", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("Refii.event_replica()", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("not Refii.world_here()", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("IsPartnerTag(e.tag)", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("if not LivingCreature(victim) or w.partnered[victim] or w.waiting[victim] then return end", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("local wait = VortexRoll(w)", StringComparison.Ordinal)).IsTrue();
		AssertThat(caught.Contains("Fresh(", StringComparison.Ordinal)).OverrideFailureMessage("a vortex's partner is once per creature per vortex, not a cooldown").IsFalse();
		AssertThat(caught.Contains("QueuePartner(", StringComparison.Ordinal)).OverrideFailureMessage("the capture rolls; the held step casts").IsFalse();
		AssertThat(lua.Contains("return e ~= nil and e ~= 0 and Refii.creature_tier(e) ~= nil and Refii.living(e)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("QueuePartner(w, VortexBladesLevel(w, hero), victim, ShotDelay(w), BaseOrigin(w, hero), serial)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("w.partnered[victim] = true", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("return math.random() * 100 <= Knob(\"vortex_affect_probability\", 100)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if not VortexAffects() then return nil end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("local first = math.max(duration * Knob(\"vortex_window_start\", 0.15), elapsed)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("local last = duration * Knob(\"vortex_window_end\", 0.6)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("local kVortexSwords = { 3, 4, 4, 4, 5 }", StringComparison.Ordinal)).IsTrue();
		// A creature the vortex cannot lift is cast on through its shudder hit, Fable III's sub_82A33C58:
		// the cast's hit_landed on a creature the live vortex does not hold, the same creature test, the
		// probability without the moment, the SAME partnered list, cast at once with the shot delay and
		// no held check at the cast.
		string shudder = Regex.Match(lua, @"local function VortexShudderPartner\(w, e\).*?\nend", RegexOptions.Singleline).Value;
		AssertThat(shudder.Contains("if not VortexShudder(w.serial, victim) or not LivingCreature(victim) or w.partnered[victim] then return end", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudder.Contains("if not VortexAffects() then return end", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudder.Contains("w.partnered[victim] = true", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudder.Contains("QueuePartner(w, VortexBladesLevel(w, hero), victim, ShotDelay(w), BaseOrigin(w, hero))", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudder.Contains("VortexRoll(", StringComparison.Ordinal)).OverrideFailureMessage("the shudder's roll is its probability only: the moment is discarded").IsFalse();
		AssertThat(shudder.Contains("Fresh(", StringComparison.Ordinal)).OverrideFailureMessage("once per creature per vortex, not a cooldown").IsFalse();
		string shudderTest = Regex.Match(lua, @"local function VortexShudder\(serial, victim\).*?\nend", RegexOptions.Singleline).Value;
		AssertThat(shudderTest.Contains("local held = Refii.vortex_held(serial)", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudderTest.Contains("if held == nil then return false end", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudderTest.Contains("if held[i] == victim then return false end", StringComparison.Ordinal)).IsTrue();
		// The helpers are locals declared before the hit_landed handler that calls them.
		AssertThat(lua.IndexOf("local function VortexShudderPartner(w, e)", StringComparison.Ordinal))
			.IsLess(lua.IndexOf("Refii.on(\"hit_landed\"", StringComparison.Ordinal));
		AssertThat(lua.Contains("local balance = Refii.spell_balance(hero, kSwords, power)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if balance ~= nil and balance.swords ~= nil and balance.swords <= want then level = power end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("local balance = Refii.spell_balance(e.caster, kVortex, e.level)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if w.base == kVortex then delay = Knob(\"shot_delay_vortex\", 0.25)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("* Knob(\"vortex_delay_cap\", 0.95), 0.01)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("tag = -w.tag", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("e.seconds = e.seconds * Knob(\"weave_duration\", 0.7)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("Refii.has_status(v.e, kStatusChaos)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("math.random()", StringComparison.Ordinal)).IsTrue();
		// Online, a cast is one identity (the core's pairing): a relayed hit carries this machine's
		// serial and the cast's tag and is found like any event; an unpaired one (host_serial nil) is
		// left alone. No weave is chosen by player and spell type.
		AssertThat(lua.Contains("WeaveForReplica", StringComparison.Ordinal)).IsFalse();
		AssertThat(lua.Contains("c.player == e.attacker_player and c.base == e.spell_type", StringComparison.Ordinal)).IsFalse();
		AssertThat(lua.Contains("local paired = Refii.host_serial(e.spell_serial)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if weaves[i].tag == tag or weaves[i].adopted == tag then return weaves[i] end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if IsBaseTag(tag) and tag ~= w.tag then w.adopted = tag end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("return kTagFirst + kTagHalf + W.tag_next", StringComparison.Ordinal)).IsTrue();
		// No spell id or ability number by hand: the named constants.
		AssertThat(Regex.IsMatch(lua, @"local k(Lightning|Fireball|SlowTime|Swords|Vortex|Chaos|ForcePush|DeadRising) = \d")).IsFalse();
		AssertThat(lua.Contains("local kLightning = Refii.SPELL_LIGHTNING", StringComparison.Ordinal)).IsTrue();

		// The engine takes a save from before's partners: the keys the Lua table used, per player.
		using JsonDocument wheel = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "ui", "spellwheel.json")));
		JsonElement pick = wheel.RootElement.GetProperty("picks")[0];
		AssertThat(pick.GetProperty("legacySaveKey").GetString()).IsEqual("SpellWeavingPartner{slot}");
		AssertThat(pick.GetProperty("legacySaveKeyTwo").GetString()).IsEqual("SpellWeavingPartner2_{slot}");

		using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "settings.json")));
		JsonElement weaving = settings.RootElement.GetProperty("spell_weaving");
		AssertThat(weaving.GetProperty("weave_damage").GetDouble()).IsEqual(0.7);
		AssertThat(weaving.GetProperty("weave_duration").GetDouble()).IsEqual(0.7);
	}

	/// <summary>
	/// The pairs as the user set them (2026-11-02): the vortex lifts only for Blades and casts Blades
	/// only on the creature it holds; with Fire, Shock, Chaos or Force Push it lifts nobody
	/// (vortex_started's writable lifts) and its one hit per creature carries the overlay — damage
	/// on hit, the status through the spell's own installer, Force Push's knockdown hit away from
	/// the twister; Force Push under Shock, Fireball or Blades is the base hit's knockdown, nothing
	/// cast and no push look (Shock + Force Push overlays Force Push's own hit effect on the bolt's
	/// victims).
	/// </summary>
	[TestCase]
	public void ThePairsRideTheBasesOwnHits()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		string started = Regex.Match(lua, @"Refii\.on\(""vortex_started"".*?\nend\)", RegexOptions.Singleline).Value;
		AssertThat(started.Contains("w.vortex_damage = e.damage", StringComparison.Ordinal)).IsTrue();
		AssertThat(started.Contains("if kVortexPass[w.overlay] ~= nil and Knob(\"woven\", 1) ~= 0 then e.lifts = false end", StringComparison.Ordinal)).IsTrue();
		AssertThat(ModApi.WritableFields.Contains((ModApi.Find("vortex_started")!.Kind, "lifts"))).IsTrue();
		// Fire, Shock, Chaos and Force Push pass through; Blades is no pass.
		string pass = Regex.Match(lua, @"local kVortexPass = \{.*?\n\}", RegexOptions.Singleline).Value;
		foreach (string overlay in new[] { "[kFireball]", "[kLightning]", "[kChaos]", "[kForcePush]" })
		{
			AssertThat(pass.Contains(overlay, StringComparison.Ordinal)).OverrideFailureMessage("no pass for " + overlay).IsTrue();
		}
		AssertThat(pass.Contains("kSwords", StringComparison.Ordinal)).IsFalse();

		string hit = Regex.Match(lua, @"Refii\.on\(""hit"".*?\nend\)", RegexOptions.Singleline).Value;
		AssertThat(hit.Contains("e.knockdown = true", StringComparison.Ordinal)).IsTrue();
		AssertThat(hit.Contains("e.damage = damage", StringComparison.Ordinal)).IsTrue();
		AssertThat(hit.Contains("Refii.event_replica()", StringComparison.Ordinal)).IsTrue();
		AssertThat(hit.Contains("not Refii.world_here()", StringComparison.Ordinal)).IsTrue();
		AssertThat(ModApi.Find("hit")!.Fields.Single(f => f.Name == "knockdown").Writable).IsTrue();
		AssertThat(lua.Contains("return overlay == kForcePush and (base == kLightning or base == kFireball or base == kSwords)", StringComparison.Ordinal)).IsTrue();

		string landed = Regex.Match(lua, @"Refii\.on\(""hit_landed"".*?\nend\)", RegexOptions.Singleline).Value;
		AssertThat(landed.Contains("if kVortexPass[w.overlay] ~= nil then PassThrough(w, e) else VortexShudderPartner(w, e) end", StringComparison.Ordinal)).IsTrue();
		AssertThat(landed.Contains("if KnockdownOverlay(w.base, w.overlay) or not Fresh(e.victim, w.tag, w.overlay) then return end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("Refii.apply_status(e.victim, pass.status, seconds, level, hero)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("Refii.knockback(victim, dx, dy, 0, 1)", StringComparison.Ordinal)).IsTrue();

		string caught = Regex.Match(lua, @"Refii\.on\(""vortex_caught"".*?\nend\)", RegexOptions.Singleline).Value;
		AssertThat(caught.Contains("kVortexPass[w.overlay] ~= nil then return end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if cast.held ~= 0 and not VortexHolds(cast.held, cast.victim) then return end", StringComparison.Ordinal)).IsTrue();

		// The looks: no pair effect where Force Push is only Fireball's or Blades' knockdown; the
		// bank's Force Push hit over the bolt's victims.
		AssertThat(lua.Contains("if KnockdownOverlay(base, overlay) and base ~= kLightning then return nil end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("local kPushHitFx = \"fxspl_forcepush_hit\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("Refii.play_fx_on(victim, kPushHitFx, { loop = false })", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if w.overlay == kChaos or KnockdownOverlay(w.base, w.overlay) then return end", StringComparison.Ordinal)).IsTrue();

		// The status times are the overlay's own: what its class's data read loads at that power and
		// the draw its Start adds (Refii.spell_balance), never the mod's own numbers.
		AssertThat(lua.Contains("local balance = hero and Refii.spell_balance(hero, w.overlay, level)", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("local seconds = (balance.status_seconds + math.random() * balance.status_spread) * Knob(\"weave_duration\", 0.7)", StringComparison.Ordinal)).IsTrue();
		using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "settings.json")));
		JsonElement weaving = settings.RootElement.GetProperty("spell_weaving");
		foreach (string key in new[] { "woven_burn_seconds", "woven_shock_seconds", "woven_chaos_seconds" })
		{
			AssertThat(weaving.TryGetProperty(key, out _)).OverrideFailureMessage("settings.json still has spell_weaving." + key).IsFalse();
			AssertThat(lua.Contains(key, StringComparison.Ordinal)).OverrideFailureMessage("the Lua still reads " + key).IsFalse();
		}

		foreach ((string key, double value) in new[] { ("vortex_affect_probability", 100.0), ("vortex_window_start", 0.15), ("vortex_window_end", 0.6), ("vortex_delay_cap", 0.95),
			         ("vortex_swords_1", 3.0), ("vortex_swords_2", 4.0), ("vortex_swords_3", 4.0), ("vortex_swords_4", 4.0), ("vortex_swords_5", 5.0) })
		{
			AssertThat(weaving.GetProperty(key).GetDouble()).OverrideFailureMessage("spell_weaving." + key).IsEqual(value);
		}
	}

	/// <summary>
	/// EVERY VORTEX PAIR AGAINST EVERY WAY THE VORTEX HITS (Fable 2 TU1 sub_8275ABE8 and the release).
	/// All reach rex_CECHitReceiver_OnHit, so `hit` and `hit_landed`; all but the drop carry the cast's
	/// serial, so its tag. The shudder (once per cast on a creature it cannot lift) is the hit a pair
	/// rides: a pass-through pair's damage and status or shove, Blades' swords. The held hit and the
	/// ragdoll hit are for entities on its held list, the pulse for props, and the drop carries no
	/// serial: none of them carries an overlay, and the Blades on a held creature come from its capture.
	/// </summary>
	[TestCase]
	public void EachVortexPairRidesItsShudderHit()
	{
		if (ModFolder() is not { } mod)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(mod, "mod", "init.lua"));
		string hit = Regex.Match(lua, @"Refii\.on\(""hit"".*?\nend\)", RegexOptions.Singleline).Value;
		string landed = Regex.Match(lua, @"Refii\.on\(""hit_landed"".*?\nend\)", RegexOptions.Singleline).Value;
		string passThrough = Regex.Match(lua, @"local function PassThrough\(w, e\).*?\nend", RegexOptions.Singleline).Value;
		string shudderPartner = Regex.Match(lua, @"local function VortexShudderPartner\(w, e\).*?\nend", RegexOptions.Singleline).Value;
		string pass = Regex.Match(lua, @"local kVortexPass = \{.*?\n\}", RegexOptions.Singleline).Value;
		const string ShudderGate = "if not VortexShudder(w.serial, e.victim) or not LivingCreature(e.victim) then return end";

		// The drop's hit names no cast (serial 0): no tag, nothing found, nothing ridden.
		AssertThat(hit.Contains("if not IsBaseTag(e.tag) or e.spell_type < 0", StringComparison.Ordinal)).IsTrue();
		AssertThat(landed.Contains("local w = WeaveOf(e.tag, e.spell_serial)", StringComparison.Ordinal)).IsTrue();
		AssertThat(landed.Contains("if w == nil or e.spell_type ~= w.base or w.overlay < 0 then return end", StringComparison.Ordinal)).IsTrue();

		// Fire, Shock, Chaos, Force Push: the extra damage before delivery and the overlay after it ride
		// the shudder on a living creature only — not the pulse on a prop, nor a hit on a prop it lifted.
		foreach ((string overlay, string status, bool extra) in new[]
		         {
			         ("[kFireball]", "kStatusBurning", true), ("[kLightning]", "kStatusElectrified", true),
			         ("[kChaos]", "kStatusChaos", false), ("[kForcePush]", "0", false),
		         })
		{
			AssertThat(pass.Contains($"{overlay} = {{ status = {status}, extra = {(extra ? "true" : "false")} }}", StringComparison.Ordinal))
				.OverrideFailureMessage("the vortex pass for " + overlay).IsTrue();
		}

		int damageGate = hit.IndexOf(ShudderGate, StringComparison.Ordinal);
		AssertThat(damageGate).OverrideFailureMessage("the pass-through damage rides the shudder only").IsGreater(-1);
		AssertThat(damageGate).IsLess(hit.IndexOf("local damage = math.max(e.damage, w.vortex_damage)", StringComparison.Ordinal));
		AssertThat(hit.Contains("if kVortexPass[w.overlay].extra then damage = damage * (1 + Knob(\"weave_damage\", 0.7)) end", StringComparison.Ordinal)).IsTrue();

		int statusGate = passThrough.IndexOf(ShudderGate, StringComparison.Ordinal);
		AssertThat(statusGate).OverrideFailureMessage("the pass-through status and shove ride the shudder only").IsGreater(-1);
		AssertThat(statusGate).IsLess(passThrough.IndexOf("Fresh(e.victim, w.tag, w.overlay)", StringComparison.Ordinal));
		AssertThat(passThrough.Contains("Refii.apply_status(e.victim, pass.status, seconds, level, hero)", StringComparison.Ordinal)).IsTrue();
		AssertThat(passThrough.Contains("Refii.knockback(victim, dx, dy, 0, 1)", StringComparison.Ordinal)).IsTrue();

		// Blades: the shudder casts the swords on a creature it cannot lift; the capture on one it holds.
		AssertThat(landed.Contains("if kVortexPass[w.overlay] ~= nil then PassThrough(w, e) else VortexShudderPartner(w, e) end", StringComparison.Ordinal)).IsTrue();
		AssertThat(shudderPartner.Contains("if not VortexShudder(w.serial, victim) or not LivingCreature(victim) or w.partnered[victim] then return end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("Refii.on(\"vortex_caught\"", StringComparison.Ordinal)).IsTrue();

		// The shudder test and the creature test are locals declared before every handler using them.
		int helpers = Math.Max(lua.IndexOf("local function VortexShudder(serial, victim)", StringComparison.Ordinal),
			lua.IndexOf("local function LivingCreature(e)", StringComparison.Ordinal));
		AssertThat(helpers).IsLess(lua.IndexOf("Refii.on(\"hit\"", StringComparison.Ordinal));
		AssertThat(helpers).IsLess(lua.IndexOf("local function PassThrough(w, e)", StringComparison.Ordinal));
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
		// The pick's exclusions (the engine refuses them on every path) are the Lua's unweavable two: Slow Time (2) and Raise Dead (7).
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

		// The look names the Lua builds are the effects the mod ships: spell_weaving_<base>_<overlay>,
		// but for Blades + Force Push and Fireball + Force Push, whose Force Push is the hits' knockdown
		// alone and plays no look (LookName's nil).
		var shipped = Directory.EnumerateFiles(Path.Combine(mod, "fx", "effects"), "*.json").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
		string[] names = { "shock", "fire", "blades", "vortex", "chaos", "push" };
		int[] rank = { 3, 2, 1, 0, 5, 4 };
		var unplayed = new HashSet<string>(StringComparer.Ordinal) { "spell_weaving_blades_push", "spell_weaving_fire_push" };
		for (int i = 0; i < names.Length; i++)
		{
			for (int j = 0; j < names.Length; j++)
			{
				if (i != j && rank[i] < rank[j])
				{
					string look = $"spell_weaving_{names[i]}_{names[j]}";
					AssertThat(shipped.Contains(look)).OverrideFailureMessage(unplayed.Contains(look) ? $"{look} plays nowhere and is not shipped" : $"no effect for {names[i]} + {names[j]}")
						.IsEqual(!unplayed.Contains(look));
				}
			}
		}

		AssertThat(shipped.Count).IsEqual(13);
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
			string candidate = Path.Combine(at, "Fable2", "mods-src", "spell_weaving");
			if (Directory.Exists(Path.Combine(candidate, "fx", "effects")))
			{
				return candidate;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}
}
