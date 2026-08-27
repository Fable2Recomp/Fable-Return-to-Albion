using System.Collections.Generic;
using System.Linq;
using Refii.Gdb;

namespace Refii.Ui;

/// <summary>
/// Screen 17's per-row detail pages, owned.
///
/// Two change detectors (`sub_826D9168`), both seeded to -1: `screen+96` vs the row's EventType
/// selects which tab page is up; `screen+100` vs the composite key drives the detail fill. Only
/// the second is ours to replace.
///
/// Page paths are `Foreground.Book.Pages.&lt;Subject&gt;Page` in `logbookscreen.bgf` — a `PageN`
/// path resolves to nothing on this screen.
/// </summary>
public static class Engine_UiLogbookPage
{

	/// <summary>
	/// The help page's sixteen tutorial subjects, in table order — index-parallel `_NAME` and
	/// `_DESC` tag tables; the row's key is `index + 239` (`sub_829BDB58` at `0x829BDD2C` indexes
	/// `unk_8349E0C0 + 4*key - 956`, 956 = 239*4). Order comes from each slot's static
	/// initialiser, not address order (the literals descend). Both fields are tags: the handler
	/// ignores the spawner's localised String2 and re-resolves String1.
	/// </summary>
	private static readonly string[] HelpSubjects =
	{
		"CAMERA", "MELEE", "BRUTAL", "WILL", "RANGED", "SKILL", "SAFETY", "HOUSE",
		"SHOP", "PEOPLE", "DOG", "MARRIAGE", "EXPERIENCE", "COOP", "RENOWN", "FOLLOWERS",
	};


	private const int QuestsEventType = 62;

	/// <summary>The key of the first completed-quest row — `sub_829B8B18` spawns `i + 1000`.</summary>
	private const int QuestKeyBase = 1000;

	private const string QuestTitle = "CompletedQuestsPage.Contents.Title";

	private const string QuestDescription = "CompletedQuestsPage.Contents.Quests.DescriptionValue";

	private const int HelpEventType = 69;

	/// <summary>The key of the first help row — `sub_829B8D88` spawns `i + 239`.</summary>
	private const int HelpKeyBase = 239;

	private const string HelpTitle = "HelpPage.Contents.Title";

	private const string HelpText = "HelpPage.Contents.LogBook.Text";

	private const string ExpressionTitle = "ExpressionsPage.Title";

	private const string ExpressionDescription = "ExpressionsPage.Contents.Description";

	/// <summary>
	/// The EventType the guest maps to the expressions tab — `sub_826D9168`'s page table:
	/// 60/64 morality, <b>61 expressions</b>, 62 completed quests, 65 renown, 66
	/// attractiveness, 67 stats, 68 dog, 69 help.
	/// </summary>
	private const int ExpressionsEventType = 61;

	/// <summary>Morality shares a page with two EventTypes; renown has one.</summary>
	private static readonly int[] MoralityEventTypes = { 60, 64 };

	private const int RenownEventType = 65;

	/// <summary>
	/// The band edges the guest tests the NORMALISED value against — `sub_829B91F0` at
	/// `0x829b9280`..`0x829b92c0`. Below the low edge and above the high edge are the named
	/// extremes; between them is NEUTRAL, on both axes.
	/// </summary>
	private const float LowBand = 0.375f;

	private const float HighBand = 0.625f;

	/// <summary>
	/// The eleven attractiveness rows, in the order the sampler fills them — which is the order
	/// the page's own `Attractiveness.` prefix concatenates.
	/// </summary>
	/// <summary>
	/// The 93 stat rows, as (counter id, element path under the Stats container). The id indexes
	/// the counter array, not this list, so gaps (10, 12, 21, 26, …) are counters the page does
	/// not display; the guest holds the same pairing in a `std::map<int, pair<LHString,LHString>>`
	/// and names the element `group + suffix`.
	///
	/// The pairing must be read off the key INSERT order and the suffix-global reference order,
	/// not off the decompiler's value stores (it aliases key and string pointer through one stack
	/// slot) and not off global address order (money key 22 is out of sequence). Each global's
	/// string comes from its own static initialiser (`sub_8222CED0(&global, ".Suffix")`).
	/// </summary>
	private static readonly (int Id, string Path)[] StatRows =
	{
		// combat
		(2,"combat.DecapitationValue"),(3,"combat.GroinshotsValue"),(4,"combat.EnemiesDisarmedValue"),
		(5,"combat.ReversalsValue"),(6,"combat.DamageDealtValue"),(7,"combat.TotalManaSpentValue"),
		(8,"combat.DamageTakenValue"),(9,"combat.DeathsValue"),(11,"combat.ResurrectionPhialsValue"),
		(13,"combat.HitsTakenValue"),(14,"combat.AttacksBlockedValue"),(15,"combat.FlourishesDoneValue"),
		(16,"combat.CoopStrikesValue"),
		// kills
		(109,"kills.TotalValue"),(110,"kills.BansheesKilledValue"),(111,"kills.BalverinesKilledValue"),
		(112,"kills.BanditsKilledValue"),(113,"kills.BeetlesKilledValue"),(114,"kills.TrollsKilledValue"),
		(115,"kills.ShardsKilledValue"),(116,"kills.ShadowsKilledValue"),
		(117,"kills.HollowmenKilledValue"),(118,"kills.GuardsKilledValue"),
		(119,"kills.LucienGuardsKilledValue"),(120,"kills.LucienSoldiersKilledValue"),
		(121,"kills.HobbesKilledValue"),(122,"kills.VillagersKilledValue"),
		(123,"kills.HighwayMenKilledValue"),
		// money
		(17,"money.MoneyPropertiesOwnedValue"),(18,"money.MoneyEarnedValue"),(19,"money.MoneySpentValue"),
		(20,"money.MoneyRightNowValue"),(22,"money.MoneyMadeMageDiceValue"),
		(23,"money.MoneyMadeBartendingValue"),(24,"money.MoneyMadeWoodchoppingValue"),
		(25,"money.MoneyMadeBlacksmithingValue"),(27,"money.MoneyMadeKeystoneValue"),
		(28,"money.MoneyMadeWizardsTowerValue"),(29,"money.MoneyMadeRealEstateValue"),
		(30,"money.MoneyMadeAssasinationsValue"),(31,"money.MoneyMadeBountyHuntsValue"),
		(32,"money.MoneyMadeSlaveryValue"),
		// adventure
		(33,"adventure.TotalRenownValue"),(106,"adventure.MostTrophyRenownValue"),
		(35,"adventure.GoldDonatedToTempleValue"),(36,"adventure.PeopleSacrificedToEvilValue"),
		(37,"adventure.AssassinationsCompletedValue"),(38,"adventure.BountiesCollectedValue"),
		(39,"adventure.SlavesRescuedValue"),(40,"adventure.PeoplePutIntoSlaveryValue"),
		(41,"adventure.MostGoodPointsValue"),(42,"adventure.MostEvilPointsValue"),
		(43,"adventure.MostNobilityPointsValue"),(44,"adventure.MostCorruptionPointsValue"),
		(45,"adventure.TotalExpEarnedValue"),(46,"adventure.TotalStrengthExpEarnedValue"),
		(47,"adventure.TotalSkillExpEarnedValue"),(48,"adventure.TotalWillExpEarnedValue"),

		// `Genearal` is the SHIPPED SPELLING. The element in `logbookscreen.bgf` carries the
		// typo, so correcting it here would resolve to nothing at all.
		(49,"adventure.TotalGenearalExpEarnedValue"),

		// games — note 83 is registered LAST despite its lower id
		(50,"games.TopScoreWestcliffShootingValue"),(96,"games.BiggestWinMageDiceValue"),
		(97,"games.BiggestWinKeystoneValue"),(98,"games.BiggestWinWizardsTowerValue"),
		(83,"games.BestFullCrucibleTimeValue"),
		// relationships
		(54,"relationships.NumPeopleFallenInLoveWithYouValue"),(55,"relationships.TimesMarriedValue"),
		(56,"relationships.TimesDivorcedValue"),(57,"relationships.TimesWidowedValue"),
		(58,"relationships.NumChildrenValue"),(59,"relationships.MostLoveEarnedValue"),
		(60,"relationships.MostHumourEarnedValue"),(61,"relationships.MostFearEarnedValue"),
		(62,"relationships.MostHateEarnedValue"),(63,"relationships.TimesMaritalSexValue"),
		(64,"relationships.TimesExtraMaritalSexValue"),(65,"relationships.TimesPaidForSexValue"),
		(66,"relationships.NumNpcRewardsCollectedValue"),
		(67,"relationships.MostCoopExpressionBonusesValue"),
		// crime
		(69,"crime.NumCrimesCommitedValue"),(70,"crime.CrimeFinesLeviedValue"),
		(71,"crime.CrimeFinesPaidValue"),(72,"crime.CrimeFinesPaidCommunityServiceValue"),
		// other
		(74,"other.TotalFoodDrinkConsumedValue"),(75,"other.TotalAlcoholConsumedValue"),
		(76,"other.TotalPotionsConsumedValue"),(77,"other.AmountOfTimeDrunkValue"),
		(78,"other.TotalVomitsValue"),(79,"other.TotalDistanceTraveledValue"),
		(80,"other.NumChickensKickedValue"),(81,"other.FurthestChickenKickValue"),
		(0,"other.ExpressionsValue"),(84,"other.STDsValue"),(1,"other.TimePlayedValue"),
	};

	private static readonly string[] AttractivenessRows =
	{
		"Attractiveness.ClothingValue",
		"Attractiveness.HairValue",
		"Attractiveness.BeardValue",
		"Attractiveness.MoustacheValue",
		"Attractiveness.MakeupValue",
		"Attractiveness.TattooValue",
		"Attractiveness.PurityCorruptionValue",
		"Attractiveness.MuscleValue",
		"Attractiveness.FatnessValue",
		"Attractiveness.ScarsValue",
		"Attractiveness.HomeBonusValue",
	};

	/// <summary>The detail values for the highlighted row, or blanks.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroDerived hero,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(10)
		{
			[ExpressionTitle] = string.Empty,
			[ExpressionDescription] = string.Empty,
		};

		// The hero pages do not depend on the cursor: morality, renown and the title are player
		// state, filled whatever row is highlighted.
		HeroPages(values, hero, strings);
		HelpPage(values, row, strings);
		QuestsPage(values, row, quests, strings);

		// A row on any other tab leaves this page alone rather than blanking it — the guest
		// shows one page at a time and the others keep whatever they last held.
		if (row is null || row.EventType != ExpressionsEventType)
		{
			return values;
		}

		// On this page the row key is the GDB record HASH, not the name: spawner `sub_829B8888`
		// resolves via `sub_821DA348` and passes the result as the row id; consumer
		// `sub_829BCDF0` takes it straight back to `sub_821DA348` (`0x829bce1c`). Other books
		// key the same composite differently (entity handle, record name), so each states its own.
		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);

		GdbMulti? byHash = Engine_GameGdb.Database;
		GdbCursor at = default;
		bool found = byHash is not null && byHash.TryLookup(id, out at);

		// The name is tried second rather than not at all: it costs one lookup and covers a
		// row whose id we failed to capture at spawn time.
		if (!found && !Engine_GameGdb.TryLookup(row.Name ?? string.Empty, out at))
		{
			return values;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return values;
		}

		string title = Babel(strings, Text(multi, at, "NameTag"));
		values[ExpressionTitle] = title.Length != 0 ? title : row.StatedLabel ?? string.Empty;
		values[ExpressionDescription] =
			Babel(strings, Text(multi, at, "ExpressionDescriptionTextTag"));

		return values;
	}

	/// <summary>
	/// The completed-quests page: one quest's rewards, description and conclusion.
	///
	/// Markup is the guest's token for token — no newlines; `&lt;br&gt;` breaks, `&lt;s1&gt;…&lt;/s1&gt;` is the
	/// heading style, money uses `&lt;gold_img&gt;` and renown `&lt;bullet.img&gt;` (not interchangeable).
	/// Rewards precede the description: `sub_829BD5C8` runs before `sub_829BD8F8` into one
	/// accumulator. Item bullets are a GDB walk (`sub_82523078` enumerates the quest record's
	/// `RewardItems` sub-record with inheritance), so they need nothing live from the guest.
	/// </summary>
	private static void QuestsPage(
		Dictionary<string, string> values,
		Engine_UiExpandableMenu.Item? row,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values[QuestTitle] = string.Empty;
		values[QuestDescription] = string.Empty;

		if (row is null || row.EventType != QuestsEventType || quests.Count == 0)
		{
			return;
		}

		uint key = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		long index = (long)key - QuestKeyBase;
		if (index < 0 || index >= quests.Count)
		{
			return;
		}

		// Descending by `DisplayPriority`, the guest's comparator. The bridge hands the list over
		// filtered but unsorted; the sort must be stable to keep bridge order among equals.
		var ordered = new List<Refii.Host.HostBridge.CompletedQuest>(quests);
		ordered = ordered
			.Select((quest, at) => (quest, at))
			.OrderByDescending(pair => Priority(pair.quest.Record))
			.ThenBy(pair => pair.at)
			.Select(pair => pair.quest)
			.ToList();

		Refii.Host.HostBridge.CompletedQuest chosen = ordered[(int)index];
		if (!Engine_GameGdb.TryLookupHash(chosen.Record, out GdbCursor at))
		{
			return;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return;
		}

		values[QuestTitle] = Babel(strings, Text(multi, at, "NameTag"));

		var text = new System.Text.StringBuilder();

		// `sub_829BD5C8` shows the header when items are non-empty OR money > 0 OR renown > 0
		// (0x829BD624).
		List<string> items = RewardItems(multi, at, strings);
		bool money = chosen.Money > 0;
		bool renown = chosen.Renown > 0;
		if (items.Count > 0 || money || renown)
		{
			text.Append("<s1>")
				.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_REWARDS"))
				.Append("</s1><br>");

			// One `<br>` per item, then one more for the list: the trailing break at
			// 0x829BD7A0 sits inside the `count > 0` arm.
			if (items.Count > 0)
			{
				foreach (string name in items)
				{
					text.Append("<bullet.img> ").Append(name).Append("<br>");
				}

				text.Append("<br>");
			}

			if (money)
			{
				text.Append("<gold_img> ")
					.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_MONEY"))
					.Append(' ')
					.Append(chosen.Money.ToString(System.Globalization.CultureInfo.InvariantCulture))
					.Append("<br><br>");
			}

			if (renown)
			{
				text.Append("<bullet.img> ")
					.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_RENOWN"))
					.Append(' ')
					.Append(chosen.Renown.ToString(System.Globalization.CultureInfo.InvariantCulture))
					.Append("<br><br>");
			}
		}

		text.Append("<s1>")
			.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_DESCRIPTION"))
			.Append("</s1><br>")
			.Append(Babel(strings, Text(multi, at, "DescriptionTag")))
			.Append("<br><br>");

		if (!string.IsNullOrEmpty(chosen.Conclusion))
		{
			text.Append("<s1>")
				.Append(Babel(strings, "GUI_SCREEN_LOGBOOK_COMPLETED_QUESTS_CONCLUSION"))
				.Append("</s1><br>")
				.Append(Babel(strings, chosen.Conclusion))
				.Append("<br><br>");
		}

		values[QuestDescription] = text.ToString();
	}

	/// <summary>
	/// A quest's reward item names, already localised, in the guest's own order.
	///
	/// No type filter: `sub_82523078` reads every enumerated property's value as a record hash
	/// (`v11 = *v10` at 0x8252312C) and lets the lookup fail. The real filter is that a record
	/// with no `InventoryItemComponent` produces no bullet (`if (v14)` at 0x829BD6F8).
	/// `NameTag` holds a Babel key, resolved by `sub_8230E0E8` at 0x829BD758.
	/// </summary>
	private static List<string> RewardItems(
		GdbMulti multi, GdbCursor quest, Refii.Text.Engine_BabelDatabase? strings)
	{
		var names = new List<string>();
		if (!Engine_AbilityRecords.TryRef(multi, quest, "RewardItems", out GdbCursor rewards))
		{
			return names;
		}

		foreach ((uint _, GdbFieldType _, uint raw) in multi.InheritedFields(rewards))
		{
			if (raw == 0u || !multi.TryLookup(raw, out GdbCursor item) ||
				!Engine_AbilityRecords.TryRef(multi, item, "InventoryItemComponent", out GdbCursor part))
			{
				continue;
			}

			string name = Babel(strings, Text(multi, part, "NameTag"));
			if (name.Length != 0)
			{
				names.Add(name);
			}
		}

		return names;
	}

	/// <summary>A quest record's `DisplayPriority`, 0 when absent — the guest's own default.</summary>
	private static int Priority(uint record) =>
		Engine_GameGdb.TryLookupHash(record, out GdbCursor at)
			? Engine_AbilityRecords.IntOn(at, "DisplayPriority")
			: 0;

	/// <summary>
	/// The help page — a subject's name and description straight from Babel; no GDB record and
	/// no hero state, just two tag tables and an index.
	/// </summary>
	private static void HelpPage(
		Dictionary<string, string> values,
		Engine_UiExpandableMenu.Item? row,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values[HelpTitle] = string.Empty;
		values[HelpText] = string.Empty;

		if (row is null || row.EventType != HelpEventType)
		{
			return;
		}

		// The key is unsigned in the guest: below the base it wraps and fails the bound rather
		// than indexing backwards.
		uint key = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		long index = (long)key - HelpKeyBase;
		if (index < 0 || index >= HelpSubjects.Length)
		{
			return;
		}

		string subject = HelpSubjects[index];
		values[HelpTitle] = Babel(strings, $"TEXT_TUTORIAL_MENU_{subject}_NAME");
		values[HelpText] = Babel(strings, $"TEXT_TUTORIAL_MENU_{subject}_DESC");
	}

	/// <summary>
	/// The morality and renown pages, from the guest's own accessors.
	///
	/// Bands are tested on the accessor's answer, never the raw field: `sub_8223D320` adds
	/// component 57's modifier maps plus the GDB `EvilMoralityAddition`/`HolyMoralityAddition`
	/// constants for a flagged hero, and with edges at 0.375/0.625 the raw field names the wrong
	/// personality near either one. Hence `RefiiHostGetHeroDerived` beside `…GetHeroVitals`.
	/// </summary>
	private static void HeroPages(
		Dictionary<string, string> values,
		Refii.Host.HostBridge.HeroDerived hero,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values["MoralityPage.Contents.LogBook.Morality.Name"] = string.Empty;
		values["MoralityPage.Contents.LogBook.Morality.Desc"] = string.Empty;
		values["MoralityPage.Contents.LogBook.Title"] = string.Empty;
		values["RenownPage.Contents.LogBook.Title"] = string.Empty;
		values["AttractivenessPage.Contents.LogBook.Title"] = string.Empty;
		values["StatsPage.Contents.LogBook.Title"] = string.Empty;
		values["DogPage.Contents.LogBook.Title"] = string.Empty;
		values["Dog.CombatAmount"] = string.Empty;
		values["Dog.SearchingAmount"] = string.Empty;
		values["Dog.CurrentStateAmount"] = string.Empty;

		foreach ((int _, string path) in StatRows)
		{
			values[$"Stats.{path}"] = string.Empty;
		}

		foreach (string row in AttractivenessRows)
		{
			values[row] = string.Empty;
		}
		values["RenownPage.Contents.LogBook.Renown.CurrentStateValue"] = string.Empty;

		// The four fixed axis captions are authored tags and do not depend on the hero at all.
		values["MoralityPage.Contents.LogBook.Morality.Evil"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_EVIL");
		values["MoralityPage.Contents.LogBook.Morality.Good"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_GOOD");
		values["MoralityPage.Contents.LogBook.Morality.Corrupt"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_CORRUPT");
		values["MoralityPage.Contents.LogBook.Morality.Pure"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_PURE");

		if (!hero.Known)
		{
			return;
		}

		string moral = Band(hero.MoralityFraction, "EVIL", "GOOD");
		string pure = Band(hero.PurityFraction, "CORRUPT", "PURE");

		// `(morality, purity)` in THAT order — the guest composes the tag the same way.
		values["MoralityPage.Contents.LogBook.Morality.Name"] =
			Babel(strings, $"GUI_SCREEN_PERSONALITY_MORALITY_{moral}_{pure}_NAME");
		values["MoralityPage.Contents.LogBook.Morality.Desc"] =
			Babel(strings, $"GUI_SCREEN_PERSONALITY_MORALITY_{moral}_{pure}_DESC");

		string title = Title(hero.TitleTag, strings);
		values["MoralityPage.Contents.LogBook.Title"] = title;
		values["RenownPage.Contents.LogBook.Title"] = title;

		values["RenownPage.Contents.LogBook.Renown.CurrentStateValue"] =
			hero.Renown.ToString(System.Globalization.CultureInfo.InvariantCulture);

		values["AttractivenessPage.Contents.LogBook.Title"] = title;
		values["StatsPage.Contents.LogBook.Title"] = title;
		values["DogPage.Contents.LogBook.Title"] =
			hero.DogKnown ? hero.DogName ?? string.Empty : string.Empty;

		if (hero.StatsKnown)
		{
			foreach ((int id, string path) in StatRows)
			{
				values[$"Stats.{path}"] =
					hero.Stat(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
			}
		}

		if (hero.DogKnown)
		{
			values["Dog.CombatAmount"] =
				hero.DogCombat.ToString(System.Globalization.CultureInfo.InvariantCulture);
			values["Dog.SearchingAmount"] =
				hero.DogSearching.ToString(System.Globalization.CultureInfo.InvariantCulture);
			values["Dog.CurrentStateAmount"] = Babel(strings, DogStateTag(hero.DogState));
		}

		// Each accessor ends in `clamp(v * 100, +/-100)` and `sub_829BABF0` formats it with a
		// plain wide "%d": truncate, never round.
		for (int row = 0; row < AttractivenessRows.Length; ++row)
		{
			values[AttractivenessRows[row]] =
				row < hero.Attractiveness.Length
					? ((int)hero.Attractiveness[row]).ToString(
						System.Globalization.CultureInfo.InvariantCulture)
					: string.Empty;
		}
	}

	/// <summary>
	/// The dog's state caption. Eight tags, indexed by `c97+276`, which the guest clamps to 0
	/// for anything at or above 8 — so an unknown state reads NORMAL rather than blank.
	/// </summary>
	private static string DogStateTag(int state) => state switch
	{
		1 => "GUI_SCREEN_LOGBOOK_DOG_STATE_HAPPY",
		2 => "GUI_SCREEN_LOGBOOK_DOG_STATE_ALERT",
		3 => "GUI_SCREEN_LOGBOOK_DOG_STATE_ANGRY",
		4 => "GUI_SCREEN_LOGBOOK_DOG_STATE_HEELING",
		5 => "GUI_SCREEN_LOGBOOK_DOG_STATE_SCARED",
		6 => "GUI_SCREEN_LOGBOOK_DOG_STATE_SAD",
		7 => "GUI_SCREEN_LOGBOOK_DOG_STATE_LEADING",
		_ => "GUI_SCREEN_LOGBOOK_DOG_STATE_NORMAL",
	};

	/// <summary>Which of the three bands a normalised value falls in.</summary>
	private static string Band(float fraction, string low, string high) =>
		fraction < LowBand ? low : fraction > HighBand ? high : "NEUTRAL";

	/// <summary>
	/// The hero's title, resolved from the GDB record hash the pump published.
	///
	/// The pump must not call `sub_824D8A10`: it allocates (refcounts through `dword_83497088`
	/// and builds a string) and `operator new` on the guest tick thread hangs. It publishes the
	/// raw handle at `c57+92`; the chain is walked here against our own GDB —
	/// record -> `HeroTitleComponent` (reference) -> `TextTag`, a Babel tag, not localised text.
	/// </summary>
	private static string Title(string handle, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (string.IsNullOrEmpty(handle) ||
			!uint.TryParse(handle, System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out uint record) ||
			record == 0u)
		{
			return string.Empty;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null || !multi.TryLookup(record, out GdbCursor at))
		{
			return string.Empty;
		}

		if (!multi.TryFindInherited(
				at, Refii.Core.Engine_GameHash.Fnv1("HeroTitleComponent"),
				(int)GdbFieldType.Reference, out _, out uint component, out _) ||
			!multi.TryLookup(component, out GdbCursor componentAt))
		{
			return string.Empty;
		}

		return Babel(strings, Text(multi, componentAt, "TextTag"));
	}

	/// <summary>A string property's text, following the record's parent chain.</summary>
	private static string Text(GdbMulti multi, GdbCursor at, string property)
	{
		if (!multi.TryFindInherited(
				at, Refii.Core.Engine_GameHash.Fnv1(property), (int)GdbFieldType.String,
				out GdbCursor owner, out uint raw, out _) ||
			!owner.Valid)
		{
			return string.Empty;
		}

		return owner.View.Names.TryGetValue(raw, out string? text) ? text : string.Empty;
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && !string.IsNullOrEmpty(tag) &&
		strings.TryGetString(tag, out string value)
			? value
			: string.Empty;
}
