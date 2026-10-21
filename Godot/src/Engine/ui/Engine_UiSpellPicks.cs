using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// THE SPELL SLOT PICKS — what a player chooses per spell slot on the spell selectors (the quick
/// wheel and the abilities blade), in ONE data form for the game's own pick and a mod's.
/// </summary>
/// <remarks>
/// <para>The game's own pick is the slot's spell (<see cref="Game"/>, <c>"store": "game"</c>): the
/// guest keeps it in the spell manager's slot table and the selectors' carousel writes it through
/// the guest's own assignment. Every assignment the guest makes as a choice — the carousel (each
/// step is in the slot the moment it is made), the scripts' spell-table natives, this side's
/// <see cref="Choose"/> — goes through the guest's one slot writer, which raises <c>SpellPick</c>
/// with pick <c>"spell"</c> and mod <c>""</c> before it stores (src/vfs/src/hooks/mod_abilities.cpp
/// <c>rex_CECSpellManager_SetSpellSlot</c>); a handler may change the will or keep the slot as it was.
/// The template's defaults, the session's copy of the other machine's pick and a save load are not
/// picks and raise nothing. A mod adds picks in <c>ui/spellwheel.json</c>:</para>
/// <code>
/// { "picks": [
///   { "name": "spell_weaving.partner",
///     "store": "spell_weaving.partner_{slot}",
///     "inEffect": "player:spell_weaving.partner_active_{slot}",
///     "unlocked": "player:spell_weaving.ability",
///     "exclude": [2, 7], "empty": "art/gui/hud/spell_weaving_slot.tex", "scale": 0.55,
///     "apply": "SPELL_WEAVING_SET_PARTNER", "remove": "SPELL_WEAVING_REMOVE_PARTNER" } ] }
/// </code>
/// <para><c>store</c> is the SAVED player value each player's pick lives under (<c>{slot}</c> the
/// slot's number, 1..5; -1 none): the engine writes it — X on a will picks it for the highlighted
/// slot, Y clears it — after raising the <c>SpellPick</c> event (native, Lua, Godot), which may
/// refuse or change the pick (src/vfs/src/hooks/mod_abilities.cpp). Nothing else writes it, so a
/// mod reads its players' picks from the player store and never takes a command.
/// <c>inEffect</c> and <c>unlocked</c> are value sources (<see cref="Engine_UiValueSources"/>) read
/// for the selectors' player: what the mod states is in effect (drawn full; the stored pick that is
/// not in effect is drawn faint), and a positive value once the player has the pick (the spell page
/// says nothing about it before). <c>exclude</c> lists the wills that can never be picked (the slot
/// whose own will is one of them draws its frame greyed). <c>apply</c> and <c>remove</c> are the
/// Babel tags of the X and Y prompts on the abilities blade.</para>
/// <para>The selectors draw EVERY enabled mod's pick under each slot, one small icon per pick in
/// load order (<see cref="Layout"/>). X and Y act on one of them, each player's
/// <see cref="Active"/> pick; a press with nothing to do on it, or LB/RB on the abilities blade,
/// steps to the next (<see cref="StepCursor"/>).</para>
/// </remarks>
public static class Engine_UiSpellPicks
{
	public const string SpellWheelPath = "ui/spellwheel.json";

	/// <summary>The store of the game's own pick: the guest's slot table.</summary>
	public const string GameStore = "game";

	/// <summary>A <c>{slot}</c> in a key: the slot's number, 1..5.</summary>
	public const string SlotToken = "{slot}";

	public sealed record Pick(
		string Name,
		string Mod,
		string Store,
		string InEffect = "",
		string Unlocked = "",
		IReadOnlySet<int>? Excluded = null,
		string Empty = "",
		float Scale = 0.55f,
		string Apply = "",
		string Remove = "")
	{
		/// <summary>Whether the guest keeps this pick (the slot's own spell), not the player store.</summary>
		public bool IsGame => Store == GameStore;

		public bool Excludes(int will) => Excluded is { } excluded && excluded.Contains(will);
	}

	/// <summary>The game's own pick: the slot's spell, in the guest's slot table.</summary>
	public static readonly Pick Game = new("spell", string.Empty, GameStore);

	private static List<Pick>? _mods;

	/// <summary>The enabled mods' picks, lowest priority first.</summary>
	public static IReadOnlyList<Pick> Mods => _mods ??= Load();

	/// <summary>Each player's pick cursor (index 1 and 2): which of <see cref="Mods"/> X and Y act on.</summary>
	private static readonly int[] _cursor = new int[3];

	/// <summary>The index into <see cref="Mods"/> X and Y act on for <paramref name="player"/>, 0..count-1.</summary>
	public static int Cursor(int player, int count) =>
		count <= 0 ? 0 : Math.Clamp(_cursor[player is 2 ? 2 : 1], 0, count - 1);

	/// <summary>The pick X and Y act on for <paramref name="player"/>; null when no mod states one.</summary>
	public static Pick? Active(int player) => Mods.Count == 0 ? null : Mods[Cursor(player, Mods.Count)];

	/// <summary>Moves <paramref name="player"/>'s cursor <paramref name="delta"/> picks on, wrapping; returns the new index.</summary>
	public static int StepCursor(int player, int delta, int count)
	{
		int at = count <= 0 ? 0 : ((Cursor(player, count) + delta) % count + count) % count;
		_cursor[player is 2 ? 2 : 1] = at;
		return at;
	}

	/// <summary>Drops the loaded picks; the next read loads them again.</summary>
	public static void Reload() => _mods = null;

	/// <summary>The gap between two pick icons, as a fraction of one icon's size.</summary>
	public const float LayoutPitch = 1.05f;

	/// <summary>How far past the slot icon's edge the pick icons may reach, as a fraction of the slot icon.</summary>
	public const float LayoutOverhang = 0.1f;

	/// <summary>
	/// WHERE EACH PICK SITS under a slot: <paramref name="count"/> icons, in load order, in the
	/// slot icon's own units (its centre the origin, its size 1, y up). One pick sits where it always
	/// has, overhanging the lower-right corner at <paramref name="scale"/>; more share a row that
	/// ends at that corner and runs left, each drawn at scale ÷ √count so together they cover no more
	/// of the slot's own spell than one pick does, and smaller still to keep within the slot icon
	/// plus <see cref="LayoutOverhang"/> — breaking into up to three rows (the last at the bottom,
	/// each filled from the left) when that keeps them larger. Children of the slot icon, they scale
	/// with it at every resolution, aspect and split.
	/// </summary>
	public static (float X, float Y, float Scale)[] Layout(int count, float scale)
	{
		if (count <= 0)
		{
			return System.Array.Empty<(float, float, float)>();
		}

		int bestRows = 1;
		float best = 0f;
		for (int rows = 1; rows <= System.Math.Min(3, count); ++rows)
		{
			int columns = (count + rows - 1) / rows;
			float fit = System.Math.Min(Fit(columns), Fit(rows));
			float s = System.Math.Min(scale / System.MathF.Sqrt(count), fit);
			if (s > best + 1e-4f)
			{
				best = s;
				bestRows = rows;
			}
		}

		int perRow = (count + bestRows - 1) / bestRows;
		float corner = 0.5f - (best * 0.35f);
		var at = new (float, float, float)[count];
		for (int i = 0; i < count; ++i)
		{
			int row = i / perRow;
			int inRow = System.Math.Min(perRow, count - (row * perRow));
			int column = i - (row * perRow);
			float x = corner - ((inRow - 1 - column) * LayoutPitch * best);
			float y = -corner + ((bestRows - 1 - row) * LayoutPitch * best);
			at[i] = (x, y, best);
		}

		return at;

		// n icons from the corner across the slot: corner - (n-1)·pitch·s - s/2 >= -(1/2 + overhang).
		static float Fit(int n) => (1f + LayoutOverhang) / (0.85f + (LayoutPitch * (n - 1)));
	}

	/// <summary>A key with its <c>{slot}</c> filled in — the slot's number, 1..5.</summary>
	public static string KeyFor(string template, int slot) =>
		template.Replace(SlotToken, (slot + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

	/// <summary>The engine's command for a pick: <c>spell_pick PLAYER SLOT SPELL STORE PICK MOD</c>.</summary>
	public static string PickLine(Pick pick, int player, int slot, int will) =>
		string.Join(' ', "spell_pick", player.ToString(CultureInfo.InvariantCulture),
			slot.ToString(CultureInfo.InvariantCulture), will.ToString(CultureInfo.InvariantCulture),
			KeyFor(pick.Store, slot), pick.Name.Length == 0 ? "-" : pick.Name, pick.Mod.Length == 0 ? "-" : pick.Mod);

	/// <summary>
	/// The one way a pick is made: the game's own through the guest's assignment (whose writer raises
	/// the event), a stored one through the engine's <c>spell_pick</c> (the event, then the saved
	/// value). -1 clears a stored pick.
	/// </summary>
	public static void Choose(Pick pick, int player, int slot, int will)
	{
		if (slot < 0 || slot >= Engine_UiModel.SpellAssignState.Slots || player is not (1 or 2))
		{
			return;
		}

		if (pick.IsGame)
		{
			if (will >= 0)
			{
				Engine_UiSource.Current.AssignSpell(slot, will, player);
			}

			return;
		}

		HostBridge.PostModCommand(PickLine(pick, player, slot, will));
	}

	/// <summary>The will stored for <paramref name="player"/> on <paramref name="slot"/>, or -1.</summary>
	public static int Stored(Pick pick, int player, int slot) =>
		!pick.IsGame && HostBridge.GetPlayerValue(player, KeyFor(pick.Store, slot)) is { } value
			? Will(value)
			: -1;

	/// <summary>The will the pick's <c>inEffect</c> states for the player on the slot, or -1.</summary>
	public static int InEffect(Pick pick, int player, int slot) =>
		pick.InEffect.Length != 0 && Engine_UiValueSources.Number(KeyFor(pick.InEffect, slot), Readers(player)) is { } value
			? Will(value)
			: -1;

	/// <summary>Whether the player has the pick: its <c>unlocked</c> source is positive (no source: from the start).</summary>
	public static bool Unlocked(Pick pick, int player) =>
		pick.Unlocked.Length == 0 ||
		Engine_UiValueSources.Number(pick.Unlocked, Readers(player)) is > 0f;

	private static int Will(float value) =>
		value >= 0f && value < Engine_UiModel.SpellAssignState.Spells ? (int)value : -1;

	private static Engine_UiValueSources.Context Readers(int player) => new()
	{
		Player = player is 2 ? 2 : 1,
		Mod = key => HostBridge.TryGetModValue(key, out float value) ? value : null,
		PlayerValue = (p, key) => HostBridge.GetPlayerValue(p, key),
	};

	private static List<Pick> Load()
	{
		var picks = new List<Pick>();
		foreach ((string mod, string text) in Refii.Mods.ModEntries.ReadEnabledText(SpellWheelPath))
		{
			foreach (Pick pick in Parse(mod, text))
			{
				picks.RemoveAll(existing => existing.Name == pick.Name);
				picks.Add(pick);
			}
		}

		if (picks.Count != 0)
		{
			GD.Print($"[spell-picks] {picks.Count} from mods: " +
				string.Join(", ", picks.ConvertAll(p => $"{p.Name} ({p.Mod}, {p.Store})")));
		}

		return picks;
	}

	/// <summary>One file's picks; a pick without a name or a store is skipped.</summary>
	public static List<Pick> Parse(string mod, string text)
	{
		var picks = new List<Pick>();
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary ||
			!parsed.AsGodotDictionary().TryGetValue("picks", out Variant list) || list.VariantType != Variant.Type.Array)
		{
			GD.PushWarning($"[spell-picks] {mod}:{SpellWheelPath} has no \"picks\" list; ignored");
			return picks;
		}

		foreach (Variant value in list.AsGodotArray())
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = value.AsGodotDictionary();
			string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
			var excluded = new HashSet<int>();
			if (record.TryGetValue("exclude", out Variant exclude) && exclude.VariantType == Variant.Type.Array)
			{
				foreach (Variant item in exclude.AsGodotArray())
				{
					excluded.Add(item.AsInt32());
				}
			}

			float scale = record.TryGetValue("scale", out Variant s) && s.VariantType is Variant.Type.Float or Variant.Type.Int
				? Mathf.Clamp((float)s.AsDouble(), 0.2f, 1f)
				: 0.55f;
			var pick = new Pick(Text("name"), mod, Text("store"), Text("inEffect"), Text("unlocked"), excluded,
				Text("empty"), scale, Text("apply"), Text("remove"));
			if (pick.Name.Length == 0 || pick.Store.Length == 0 || pick.IsGame)
			{
				GD.PushWarning($"[spell-picks] {mod}: a pick needs a name and a player store key; skipped");
				continue;
			}

			picks.Add(pick);
		}

		return picks;
	}
}
