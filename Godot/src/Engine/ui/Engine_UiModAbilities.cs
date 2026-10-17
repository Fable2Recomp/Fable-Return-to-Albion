using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// Abilities a MOD adds to the abilities book — mod state, not the guest's: the guest's ability
/// types are a fixed 1..14 enum written into its switches, so a new one cannot be data.
/// </summary>
/// <remarks>
/// <para>A mod ships <c>ui/abilities.json</c>:</para>
/// <code>
/// { "abilities": [
///   { "name": "SoulsCombatStamina", "category": "Strength",
///     "nameTag": "SOULS_COMBAT_STAMINA_NAME", "descriptionTag": "SOULS_COMBAT_STAMINA_DESC",
///     "maxLevel": 5,
///     "level": "souls_combat.stamina_level", "cost": "souls_combat.stamina_cost",
///     "stat": "souls_combat.stamina_next", "buy": "souls_combat.buy_stamina",
///     "refund": "souls_combat.refund_stamina", "refundValue": "souls_combat.stamina_refund",
///     "image": "art/gui/abilitytarot/souls_combat_stamina.tex" } ] }
/// </code>
/// <para><c>refund</c>, when stated, makes Y unlearn a level the same way: the mod is handed the
/// setting as an incrementing count and does the refund; <c>refundValue</c> is what it states a
/// refund would return, for the button. <c>image</c> is the card the page shows in the preview
/// frame — a mod ability has no movie, so the frame shows the still.</para>
/// <para>The row stands after the category's own abilities. Its LEVEL, next COST and the
/// description's <c>%1</c> are values the mod's hook publishes; pressing A hands the mod the
/// setting <c>buy</c> as an incrementing count (a setting holds one value, so a purchase is an
/// edge the mod sees as a change). The mod spends the experience and raises the level; the row
/// and page follow what it publishes next.</para>
/// </remarks>
public static class Engine_UiModAbilities
{
	public const string ModAbilitiesPath = "ui/abilities.json";

	/// <summary>The command a mod ability's row carries: <c>Mod:buy:&lt;setting&gt;</c>.</summary>
	public const string BuyAction = "Mod:buy:";

	public sealed record Ability(
		string Name,
		string Mod,
		string Category,
		string NameTag,
		string DescriptionTag,
		int MaxLevel,
		string LevelKey,
		string CostKey,
		string StatKey,
		string BuyKey,
		string RefundKey = "",
		string RefundValueKey = "",
		string Image = "");

	private static List<Ability>? _all;

	private static readonly Dictionary<string, int> _purchases = new(System.StringComparer.Ordinal);

	public static IReadOnlyList<Ability> All => _all ??= Load();

	/// <summary>Drops the loaded set; the next read loads it again.</summary>
	public static void Reload() => _all = null;

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

	/// <summary>The level the mod states; 0 until it has said.</summary>
	public static int Level(Ability ability) =>
		HostBridge.TryGetModValue(ability.LevelKey, out float level) ? (int)level : 0;

	/// <summary>The next level's cost the mod states, or null.</summary>
	public static int? Cost(Ability ability) =>
		HostBridge.TryGetModValue(ability.CostKey, out float cost) ? (int)cost : null;

	/// <summary>The description's <c>%1</c>, or empty.</summary>
	public static string Stat(Ability ability) =>
		ability.StatKey.Length != 0 && HostBridge.TryGetModValue(ability.StatKey, out float stat)
			? ((int)stat).ToString(System.Globalization.CultureInfo.InvariantCulture)
			: string.Empty;

	/// <summary>A press on the row whose command is <paramref name="action"/>.</summary>
	public static bool Buy(string action)
	{
		if (!action.StartsWith(BuyAction, System.StringComparison.Ordinal))
		{
			return false;
		}

		Send(action[BuyAction.Length..], "buy");
		return true;
	}

	/// <summary>Y on a mod ability's row; false when the ability states no refund.</summary>
	public static bool Refund(Ability ability)
	{
		if (ability.RefundKey.Length == 0)
		{
			return false;
		}

		Send(ability.RefundKey, "refund");
		return true;
	}

	/// <summary>
	/// Y on a mod ability's row, as the game's own unlearn runs it (TU1 sub_8246D510 and the
	/// screen's confirm before it): nothing to unlearn says so; otherwise the game's confirm
	/// question, and on yes the refund and the game's result notice — "some knowledge" for a
	/// level given up, "all knowledge" for the last. The game's own texts, from Babel, with the
	/// ability's name and the refund filled in.
	/// </summary>
	public static void AskRefund(Ability ability)
	{
		if (ability.RefundKey.Length == 0)
		{
			return;
		}

		Refii.Text.Engine_BabelDatabase? book = Engine_UiInventorySource.Strings;
		string Text(string tag)
		{
			string text = book is not null && book.TryGetString(tag, out string found) ? found : string.Empty;
			string name = book is not null && book.TryGetString(ability.NameTag, out string named) ? named : ability.Name;
			return text
				.Replace("%1", name, System.StringComparison.Ordinal)
				.Replace("%2", (RefundAmount(ability) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
					System.StringComparison.Ordinal);
		}

		int level = Level(ability);
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
			Refund(ability);
			Engine_UiHostBoxes.Tell(result);
		});
	}

	/// <summary>What a refund would return, as the mod states it, or null.</summary>
	public static int? RefundAmount(Ability ability) =>
		ability.RefundValueKey.Length != 0 && HostBridge.TryGetModValue(ability.RefundValueKey, out float value)
			? (int)value
			: null;

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

	/// <summary>An edge the mod sees as a change of the setting's value.</summary>
	private static void Send(string key, string what)
	{
		_purchases.TryGetValue(key, out int count);
		_purchases[key] = ++count;
		HostBridge.SetModSetting(key, count);
		GD.Print($"[mod-abilities] {what} '{key}' (press {count})");
	}

	private static List<Ability> Load()
	{
		var abilities = new List<Ability>();
		foreach ((string mod, string text) in Refii.Mods.ModEntries.ReadEnabledText(ModAbilitiesPath))
		{
			Variant parsed = Json.ParseString(text);
			if (parsed.VariantType != Variant.Type.Dictionary ||
				!parsed.AsGodotDictionary().TryGetValue("abilities", out Variant list) ||
				list.VariantType != Variant.Type.Array)
			{
				GD.PushWarning($"[mod-abilities] {mod}:{ModAbilitiesPath} has no \"abilities\" array; ignored");
				continue;
			}

			foreach (Variant value in list.AsGodotArray())
			{
				if (value.VariantType != Variant.Type.Dictionary)
				{
					continue;
				}

				var record = value.AsGodotDictionary();
				string Text(string key) => record.TryGetValue(key, out Variant v) ? v.AsString() : string.Empty;
				var ability = new Ability(
					Text("name"), mod, Text("category"), Text("nameTag"), Text("descriptionTag"),
					record.TryGetValue("maxLevel", out Variant max) ? max.AsInt32() : 5,
					Text("level"), Text("cost"), Text("stat"), Text("buy"),
					Text("refund"), Text("refundValue"), Text("image"));
				if (ability.Name.Length == 0 || ability.Category.Length == 0 || ability.BuyKey.Length == 0)
				{
					GD.PushWarning($"[mod-abilities] {mod}: an ability needs name, category and buy; skipped");
					continue;
				}

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
}
