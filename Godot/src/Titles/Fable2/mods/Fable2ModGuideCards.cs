namespace Refii.Titles.Fable2;

using System;
using System.Collections.Generic;
using Refii.Mods;

/// <summary>
/// The Modding Guide's hard-mod cards for Fable 2 (<see cref="ModGuidePages"/>): native code against
/// Fable 2's config names, and the bundled hard mods Souls Combat and Spell Weaving as worked
/// examples.
/// </summary>
public static class Fable2ModGuideCards
{
	/// <summary>The cards of the Script API page, after its own.</summary>
	private static readonly ModGuideCard[] ScriptApi =
	{
		new ModGuideCard(
			"⚙ Hard mods — native code",
			1.0f, 0.60f, 0.40f,
			"A hard mod ships C++ under native/. The launcher compiles it into the game code when the mod is enabled, after the player allows that mod's native code once per version.\n\n" +
			"SetNativeSource(fileName, source) — writes native/<fileName>.\n" +
			"GetNativeOverrides() — the game functions the mod overrides, read from its sources.\n" +
			"CheckNativeConflicts() — functions this mod replaces (its hook never calls the original) while refii or another enabled mod hooks them too; any one refuses the build.\n" +
			"GetNativeChains() — functions shared with other owners and the order their hooks run in.\n" +
			"GetNativeHeaderPath() — core/refii_mod.h, the header native mods compile against.\n\n" +
			"In the source:\n" +
			"  #include \"core/refii_mod.h\"\n" +
			"  REFII_MOD_ORIGINAL(rex_CPlayer_SetLookAtButtonDown);   // the game's own body, as __imp__<name>\n" +
			"  REX_EXTERN(rex_CPlayer_SetLookAtButtonDown) { REX_FUNC_PROLOGUE(); ...; __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base); }\n" +
			"  static void Tick(PPCContext* ctx, uint8_t* base, float seconds) { ... }\n" +
			"  REFII_MOD_ON_TICK(Tick);   // runs Tick once per game step\n\n" +
			"  • Hook functions by their config names (Fable2/versions/*/refii_config.toml), never sub_ addresses — the names hold for both game builds.\n" +
			"  • Shared functions chain. When two mods (or a mod and refii) hook one function, the launcher compiles each hook under its own symbol and links them in load order: the higher mod's __imp__<name> call reaches the lower mod's hook, and the lowest reaches the game. A hook that returns without calling the original stops the chain, which is what a gate means. A hook that never calls the original replaces the body and must be the only owner of that function.\n" +
			"  • Depend on another mod with requires = [\"souls_combat >= 0.1\"] in mod.toml. The required mod is placed beneath this one and its native/include/ is on this mod's include path, so its exported C functions and its published values (refii::mods::Published) are directly usable. after = [\"hd-ui\"] orders without requiring.\n" +
			"  • Helpers: refii_mod_player_hero, refii_mod_local_hero, refii_mod_component, refii_mod_run_game_lua, refii_mod_game_lua_int, refii_mod_lua_generation, refii::mods::LoadU32/LoadF32/StoreU8/StoreU32/StoreF32, IsPlayerHero, CallGuest, CallVirtual.\n" +
			"  • Values join native code to the UI: a value that is each player's goes in the player store (refii_mod_player_set(p, \"my_mod.mana\", v)) and is read as \"player:my_mod.mana\" for whichever player a HUD piece or page belongs to; one value for everyone is published (refii::mods::Publish) and read as \"mod:my_mod.mana\". Settings (refii::mods::Setting(key, fallback)) are tunables, never a command channel: a choice the UI makes is an engine pick or event."),
		new ModGuideCard(
			"🗡️ Worked example: Souls Combat",
			0.60f, 0.90f, 0.60f,
			"The bundled Souls Combat mod is built from exactly these pieces — mod/init.lua (enemy pacing), native/ (stamina, roll, guard, lock-on, mana), ui/hud.json (the mana and stamina copies of the health bar, one record each for both players), ui/abilities.json (the Stamina ability, run by the engine), its Babel text and its icons. " +
			"Its source folder is Fable2/mods-src/souls_combat in the refii tree; to take it apart or start a mod from it:\n\n" +
			"  var api := RefiiModApi.new()\n" +
			"  api.CreateMod(\"my_combat\", \"My Combat\", \"Me\", \"Souls Combat, my way\")\n" +
			"  api.ImportFolder(\"/path/to/Fable2/mods-src/souls_combat\")\n" +
			"  api.SetText(\"SOULS_COMBAT_STAMINA_NAME\", \"Endurance\")\n" +
			"  print(api.ReadText(\"ui/hud.json\"))   # a Set* call replaces the whole record it names, so start from what is there\n\n" +
			"Then enable it in the launcher's Mods panel; the game code is rebuilt with its native sources on the next Play."),
	};

	/// <summary>The events page's closing cards.</summary>
	private static readonly ModGuideCard[] EventsClosing =
	{
		new ModGuideCard(
			"🗡️ Worked examples: Souls Combat and Spell Weaving",
			0.60f, 0.90f, 0.60f,
			"Both bundled hard mods are built on these events (Fable2/mods-src/souls_combat, Fable2/mods-src/spell_weaving in the refii tree): stamina spends on MeleeSwing and RollStarted and refuses on AbilityRequest and Roll; the guard breaks through Block; poise absorbs through HitResponse; the lock-on toggles through GameAction; mana charges through SpellCharge, SpellChargeLevel and SpellLaunched; the weave stands its base in through SpellLaunch and casts the partner from HitLanded with refii_mod_cast_spell. " +
			"Neither hooks a game function by name: the look-at writer, the vortex and the fireball's impact are events too (look_at_button, vortex_started / vortex_ended, fireball_impact), so both mods are Lua on the API alone."),
	};

	/// <summary>Fable 2's hard-mod cards for the page with <paramref name="page"/>'s key.</summary>
	public static IReadOnlyList<ModGuideCard> For(string page, bool closing) =>
		(page, closing) switch
		{
			(ModGuidePages.ScriptApiKey, false) => ScriptApi,
			(ModGuidePages.EventsKey, true) => EventsClosing,
			_ => Array.Empty<ModGuideCard>(),
		};
}
