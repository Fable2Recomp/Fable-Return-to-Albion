using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Abilities a MOD adds to the abilities book — mod state, not the guest's: the guest's ability
/// types are a fixed 1..14 enum written into its switches, so a new one cannot be a guest ability.
/// It is DECLARED, and the engine runs it: the purchase, the unlearn, the prices, the save.
/// </summary>
/// <remarks>
/// <para>A mod ships <c>ui/abilities.json</c>:</para>
/// <code>
/// { "abilities": [
///   { "name": "SoulsCombatStamina", "category": "Strength",
///     "nameTag": "SOULS_COMBAT_STAMINA_NAME", "descriptionTag": "SOULS_COMBAT_STAMINA_DESC",
///     "maxLevel": 5, "levelKey": "souls_combat.stamina_ability",
///     "costLike": 3, "refund": "half", "stats": [100, 128, 185, 214, 242, 271],
///     "legacySaveKey": "SoulsStamina",
///     "image": "art/gui/abilitytarot/souls_combat_stamina.tex" } ] }
/// </code>
/// <para>THE LEVEL is each player's SAVED player value under <c>levelKey</c> (the player store:
/// <c>refii_mod_player_value</c>, <c>Refii.player_get</c>, <c>RefiiHost.player_value</c>,
/// <c>player:&lt;levelKey&gt;</c>): it goes home with that hero's own save and is the same on both
/// co-op machines. A mod applies the level's effect where it uses it, by reading that value; it
/// needs no purchase code of its own.</para>
/// <para>THE PURCHASE is the engine's (Fable2/src/vfs/src/hooks/mod_abilities.cpp), run on the guest tick
/// of the book's player's machine: A on the row posts <c>ability_buy</c>, the next level's price
/// is the game's own table (rex_CECStats_AbilityExperienceCost) of the ability type <c>costLike</c>
/// names, 1..14 (Souls Combat's 3 prices Stamina as Toughness is priced; the eight spells, 7..14,
/// share one curve), or of the type the mod setting <c>costLikeSetting</c> holds when it names one;
/// it is split evenly across the ability's disciplines (the first named takes the odd point), each
/// share drawn from its own pool and then from General as the shipped purchase draws one, and the
/// level is saved one higher. Y unlearns when <c>refund</c> is <c>"half"</c>: the shipped unlearn's
/// confirm and result boxes, half the given-up level's price back into General, the level saved
/// one lower. Either raises the <c>ModAbility</c> event first (native, Lua and Godot), which may
/// refuse it.</para>
/// <para>WHAT THE ROW SHOWS is the book's player's: the level, and beside it what the engine states
/// in the same store each tick — <c>&lt;levelKey&gt;.cost</c> (the next level's price, -1 at the
/// maximum), <c>.refund</c> (what Y gives back) and <c>.afford</c> (1 when A can buy). The
/// description's <c>%1</c> is <c>stats[level]</c> for the NEXT level (the last at the maximum), or
/// the value source <c>stat</c> names, read for the book's player. <c>image</c> is the card the
/// page shows in the preview frame.</para>
/// <para>A HYBRID ability draws on more than one discipline: <c>"category": "Hybrid",
/// "disciplines": ["Will", "Skill"]</c>; the page lights every one of its orbs, and
/// <c>totalXpTag</c> names the heading over their sum. A category only mods declare gets its row on
/// the abilities screen from the mod's <c>ui/templates.json</c> (a patch entry on
/// <c>AbilitiesMenu</c> with source <c>abilities:&lt;category&gt;</c>).</para>
/// <para>SAVES FROM BEFORE: <c>legacySaveKey</c> names the <c>RefiiModSave</c> key a save from
/// before kept player one's level in; a hero with no saved level takes that one.</para>
/// </remarks>
public static class Engine_UiModAbilities
{
	public const string ModAbilitiesPath = Refii.Mods.Engine_ModUiFiles.AbilitiesPath;

	/// <summary>The command a mod ability's row carries: <c>Mod:buy:&lt;ability name&gt;</c>.</summary>
	public const string BuyAction = "Mod:buy:";

	/// <summary>The unlearn rule that gives half the given-up level's price back (the shipped one).</summary>
	public const string HalfRefund = Refii.Mods.Engine_ModUiFiles.HalfRefund;

	public sealed record Ability(
		string Name,
		string Mod,
		string Category,
		string NameTag,
		string DescriptionTag,
		int MaxLevel,
		string LevelKey,
		int CostLike,
		string CostLikeSetting = "",
		bool Refunds = false,
		string LegacySaveKey = "",
		IReadOnlyList<float>? Stats = null,
		string Stat = "",
		string Image = "",
		IReadOnlyList<string>? Disciplines = null,
		string TotalXpTag = "");

	private static List<Ability>? _all;
	private static bool _declared;

	/// <summary>The disciplines an ability draws on: those it names, else its category.</summary>
	public static IReadOnlyList<string> DisciplinesOf(Ability ability) =>
		ability.Disciplines is { Count: > 0 } named ? named : new[] { ability.Category };

	/// <summary>
	/// A discipline's pool in hero component 164 — 1 Strength, 2 Skill, 3 Will, 0 for anything
	/// else (General is 0). `sub_824E4DD8`'s numbering.
	/// </summary>
	public static int Pool(string discipline) => discipline switch
	{
		"Strength" => 1,
		"Skill" => 2,
		"Will" => 3,
		_ => 0,
	};

	/// <summary>The pools the price is split across, in the declared order, each once; empty for General alone.</summary>
	public static List<int> PoolsOf(Ability ability)
	{
		var pools = new List<int>();
		foreach (string discipline in DisciplinesOf(ability))
		{
			int pool = Pool(discipline);
			if (pool > 0 && !pools.Contains(pool))
			{
				pools.Add(pool);
			}
		}

		return pools;
	}

	public static IReadOnlyList<Ability> All => _all ??= Load();

	/// <summary>Drops the loaded set; the next read loads it again and the next pump declares it again.</summary>
	public static void Reload()
	{
		_all = null;
		_declared = false;
	}

	public static bool TryGet(string name, out Ability ability)
	{
		foreach (Ability candidate in All)
		{
			if (candidate.Name == name)
			{
				ability = candidate;
				return true;
			}
		}

		ability = null!;
		return false;
	}

	/// <summary>The player a call names, or the book's player (whose menus are open here) for 0.</summary>
	private static int Reader(int player) => player is 1 or 2 ? player : Engine_UiModMenus.MenuPlayer;

	private static float? Stated(int player, string key) =>
		key.Length == 0 ? null : HostBridge.GetPlayerValue(Reader(player), key);

	/// <summary>The player's level, 0..maxLevel; 0 until they hold one.</summary>
	public static int Level(Ability ability, int player = 0) =>
		Stated(player, ability.LevelKey) is { } level ? Mathf.Clamp((int)level, 0, ability.MaxLevel) : 0;

	/// <summary>The next level's price as the engine states it for the player, or null (maxed, or not stated yet).</summary>
	public static int? Cost(Ability ability, int player = 0) =>
		Stated(player, ability.LevelKey + ".cost") is { } cost && cost > 0f ? (int)cost : null;

	/// <summary>Whether the engine states the player can buy the next level now.</summary>
	public static bool CanAfford(Ability ability, int player = 0) =>
		Stated(player, ability.LevelKey + ".afford") is { } afford && afford > 0f;

	/// <summary>What an unlearn would give back, as the engine states it, or null (no refund rule, nothing stated).</summary>
	public static int? RefundAmount(Ability ability, int player = 0) =>
		ability.Refunds && Stated(player, ability.LevelKey + ".refund") is { } value ? (int)value : null;

	/// <summary>
	/// The description's <c>%1</c> for the player: <c>stats</c> at the NEXT level (the last at the
	/// maximum), else the value <c>stat</c> names, read for the player; empty when neither states one.
	/// </summary>
	public static string Stat(Ability ability, int player = 0)
	{
		int level = Level(ability, player);
		if (ability.Stats is { Count: > 0 } stats)
		{
			int at = System.Math.Clamp(System.Math.Min(level + 1, ability.MaxLevel), 0, stats.Count - 1);
			return ((int)stats[at]).ToString(CultureInfo.InvariantCulture);
		}

		if (ability.Stat.Length == 0)
		{
			return string.Empty;
		}

		var context = new Engine_UiValueSources.Context
		{
			Player = Reader(player),
			Mod = key => HostBridge.TryGetModValue(key, out float value) ? value : null,
			PlayerValue = (p, key) => HostBridge.GetPlayerValue(p, key),
		};
		return Engine_UiValueSources.Number(ability.Stat, context) is { } stated
			? ((int)stated).ToString(CultureInfo.InvariantCulture)
			: string.Empty;
	}

	/// <summary>A press on the row whose command is <paramref name="action"/>, for the book's player (0) or a named one.</summary>
	public static bool Buy(string action, int player = 0)
	{
		if (!action.StartsWith(BuyAction, System.StringComparison.Ordinal) ||
			!TryGet(action[BuyAction.Length..], out Ability ability))
		{
			return false;
		}

		Post(PressLine("ability_buy", Reader(player), ability));
		return true;
	}

	/// <summary>The unlearn, for the book's player (0) or a named one; false when the ability states no refund.</summary>
	public static bool Refund(Ability ability, int player = 0)
	{
		if (!ability.Refunds)
		{
			return false;
		}

		Post(PressLine("ability_refund", Reader(player), ability));
		return true;
	}

	/// <summary>
	/// Y on a mod ability's row, as the game's own unlearn runs it (TU1 sub_8246D510 and the
	/// screen's confirm before it): nothing to unlearn says so; otherwise the game's confirm
	/// question, and on yes the refund and the game's result notice — "some knowledge" for a
	/// level given up, "all knowledge" for the last. The game's own texts, from Babel, with the
	/// ability's name and the refund filled in.
	/// </summary>
	public static void AskRefund(Ability ability, int player = 0)
	{
		if (!ability.Refunds)
		{
			return;
		}

		int reader = Reader(player);
		Refii.Text.Engine_BabelDatabase? book = Engine_UiInventorySource.Strings;
		string Text(string tag)
		{
			string text = book is not null && book.TryGetString(tag, out string found) ? found : string.Empty;
			string name = book is not null && book.TryGetString(ability.NameTag, out string named) ? named : ability.Name;
			return text
				.Replace("%1", name, System.StringComparison.Ordinal)
				.Replace("%2", (RefundAmount(ability, reader) ?? 0).ToString(CultureInfo.InvariantCulture),
					System.StringComparison.Ordinal);
		}

		int level = Level(ability, reader);
		if (level <= 0)
		{
			Engine_UiHostBoxes.Tell(Text("GUI_SCREEN_ABILITY_CANNOT_DISCARD_ABILITY"));
			return;
		}

		string result = Text(level == 1
			? "GUI_SCREEN_ABILITY_DISCARD_ALL_KNOWLEDGE_OF_ABILITY"
			: "GUI_SCREEN_ABILITY_DISCARD_SOME_KNOWLEDGE_OF_ABILITY");
		Engine_UiHostBoxes.Ask(Text("GUI_SCREEN_ABILITIES_DISCARD_CONFIRM"), () =>
		{
			Refund(ability, reader);
			Engine_UiHostBoxes.Tell(result);
		});
	}

	/// <summary>The card for the preview frame, by host texture key, or null.</summary>
	public static uint? ImageKey(Ability ability)
	{
		if (ability.Image.Length == 0)
		{
			return null;
		}

		string path = Refii.Textures.Engine_BnkTextureLoader.PathPrefix +
			ability.Image.Replace('\\', '/').ToLowerInvariant();
		return Refii.Textures.Engine_BnkTextureLoader.ReadBytes(path[Refii.Textures.Engine_BnkTextureLoader.PathPrefix.Length..]) is not null &&
			ResourceLoader.Load<Texture2D>(path) is { } card
				? Engine_UiHostTextures.Register(card)
				: null;
	}

	/// <summary>
	/// Declares the loaded abilities to the engine once per guest (and again after a remount);
	/// call every frame with whether the guest is up.
	/// </summary>
	public static void Pump(bool guestUp)
	{
		if (!guestUp)
		{
			_declared = false;
			return;
		}

		if (_declared)
		{
			return;
		}

		_declared = true;
		Post("ability_clear");
		foreach (Ability ability in All)
		{
			Post(DeclarationLine(ability));
		}
	}

	/// <summary>
	/// The engine's declaration of <paramref name="ability"/>:
	/// <c>ability_declare NAME MOD MAX COSTLIKE COSTLIKE_SETTING LEVELKEY POOLS REFUND LEGACY</c>,
	/// '-' for an empty word (Fable2/src/vfs/src/hooks/mod_abilities.cpp).
	/// </summary>
	public static string DeclarationLine(Ability ability)
	{
		static string W(string word) => word.Length == 0 || word.Contains(' ') ? "-" : word;
		List<int> pools = PoolsOf(ability);
		return string.Join(' ',
			"ability_declare", W(ability.Name), W(ability.Mod),
			ability.MaxLevel.ToString(CultureInfo.InvariantCulture),
			ability.CostLike.ToString(CultureInfo.InvariantCulture), W(ability.CostLikeSetting), W(ability.LevelKey),
			pools.Count == 0 ? "-" : string.Join(',', pools), ability.Refunds ? HalfRefund : "none",
			W(ability.LegacySaveKey));
	}

	/// <summary>A press for the engine: <c>ability_buy|ability_refund PLAYER NAME</c>.</summary>
	public static string PressLine(string verb, int player, Ability ability) =>
		string.Create(CultureInfo.InvariantCulture, $"{verb} {player} {ability.Name}");

	private static void Post(string line)
	{
		HostBridge.PostModCommand(line);
		GD.Print($"[mod-abilities] {line}");
	}

	private static List<Ability> Load()
	{
		var abilities = new List<Ability>();
		foreach ((string mod, string text) in Refii.Mods.ModEntries.ReadEnabledText(ModAbilitiesPath))
		{
			foreach (Ability ability in Parse(mod, text))
			{
				abilities.RemoveAll(existing => existing.Name == ability.Name);
				abilities.Add(ability);
			}
		}

		if (abilities.Count != 0)
		{
			GD.Print($"[mod-abilities] {abilities.Count} from mods: " +
				string.Join(", ", abilities.ConvertAll(a => $"{a.Name} ({a.Category}, {a.Mod})")));
		}

		return abilities;
	}

	/// <summary>One file's abilities; a record without a name, category, level key or costLike is skipped.</summary>
	public static List<Ability> Parse(string mod, string text)
	{
		var abilities = new List<Ability>();
		Variant parsed = Json.ParseString(text);
		if (parsed.VariantType != Variant.Type.Dictionary ||
			!parsed.AsGodotDictionary().TryGetValue("abilities", out Variant list) ||
			list.VariantType != Variant.Type.Array)
		{
			GD.PushWarning($"[mod-abilities] {mod}:{ModAbilitiesPath} has no \"abilities\" array; ignored");
			return abilities;
		}

		foreach (Variant value in list.AsGodotArray())
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = value.AsGodotDictionary();
			string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
			List<string>? disciplines = null;
			if (record.TryGetValue("disciplines", out Variant named) && named.VariantType == Variant.Type.Array)
			{
				disciplines = new List<string>();
				foreach (Variant discipline in named.AsGodotArray())
				{
					string label = discipline.AsString();
					if (label.Length != 0)
					{
						disciplines.Add(label);
					}
				}
			}

			List<float>? stats = null;
			if (record.TryGetValue("stats", out Variant statList) && statList.VariantType == Variant.Type.Array)
			{
				stats = new List<float>();
				foreach (Variant stat in statList.AsGodotArray())
				{
					stats.Add((float)stat.AsDouble());
				}
			}

			var ability = new Ability(
				Text("name"), mod, Text("category"), Text("nameTag"), Text("descriptionTag"),
				record.TryGetValue("maxLevel", out Variant max) ? max.AsInt32() : 5,
				Text("levelKey"),
				record.TryGetValue("costLike", out Variant like) ? like.AsInt32() : 0,
				Text("costLikeSetting"), Text("refund") == HalfRefund, Text("legacySaveKey"),
				stats, Text("stat"), Text("image"), disciplines, Text("totalXpTag"));
			if (ability.Name.Length == 0 || ability.Category.Length == 0 || ability.LevelKey.Length == 0 ||
				ability.CostLike is < 1 or > 14)
			{
				GD.PushWarning($"[mod-abilities] {mod}: an ability needs name, category, levelKey and costLike (1..14); skipped");
				continue;
			}

			abilities.Add(ability);
		}

		return abilities;
	}
}
