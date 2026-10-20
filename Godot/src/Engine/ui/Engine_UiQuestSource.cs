using System.Collections.Generic;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Ui;

/// <summary>
/// THE WORLD MAP'S QUESTS AND JOBS, read from the quest log rather than from the guest's menu.
///
/// They are ONE list. `sub_8246E800` (quests) and `sub_8246EA08` (jobs) are byte-identical
/// except for their last call: both walk component 102's flat map, apply the same five filters,
/// and differ only in whether they keep the definition's GDB `Type == 5` or reject it. Type 5 is
/// a Job.
///
/// The five filters are the guest's, in its own order:
///
///     Unlocked           quest+12 == 1   sub_82469330
///     not Completed      quest+21 == 0   sub_82469428
///     not Failed         quest+22 == 0   sub_82469530
///     AppearOnWorldMap   quest+77 == 1   sub_82469628
///     Type is/isn't 5    GDB "Type"      sub_82469720
///
/// The rows the guest shows are then SORTED by the definition's `DisplayPriority`, descending
/// (`sub_82529258` with comparator `sub_8251C9C0`) — container order is not display order, so
/// that sort is reproduced here rather than left to chance.
///
/// The name is the definition's `NameTag` through the string book, exactly as `sub_82522CB0`
/// reads it (`sub_821B24F8(quest+4)` then the GDB property), and a definition that resolves to
/// nothing keeps the guest's own fallback shape rather than drawing an empty row.
/// </summary>
public static class Engine_UiQuestSource
{
	/// <summary>`inventory`'s sibling: `quests` and `jobs` name the two halves of one list.</summary>
	public const string QuestsSource = "quests";

	public const string JobsSource = "jobs";

	/// <summary>The definition `Type` that makes a quest a JOB — `sub_82469720`'s test.</summary>
	private const uint JobType = 5u;

	/// <summary>`globals.gdb`, for the quest definitions. Shares the inventory source's read.</summary>
	private static GdbMulti? Globals => Engine_UiInventorySource.Globals;

	private static Refii.Text.Engine_BabelDatabase? Strings => Engine_UiInventorySource.Strings;

	private static readonly uint NameTagField = Engine_GameHash.Fnv1("NameTag");

	/// <summary>
	/// `DescriptionTag`, NOT `DescriptionTextTag`. The shipped quest record spells it without
	/// the `Text`, and the wrong spelling read nothing at all — the world map's quest page
	/// printed its "Description:" heading with a blank under it for every quest.
	/// </summary>
	private static readonly uint DescriptionField = Engine_GameHash.Fnv1("DescriptionTag");
	private static readonly uint TypeField = Engine_GameHash.Fnv1("Type");
	private static readonly uint PriorityField = Engine_GameHash.Fnv1("DisplayPriority");

	private static readonly List<Engine_UiMenuSources.Row> None = new();

	public static string Report { get; private set; } = "not read yet";

	/// <summary>The world map's `Regions` — see <see cref="Regions"/>.</summary>
	public const string RegionsSource = "regions";

	/// <summary>
	/// The fast-travel regions with at least one active stop. The host returns FNV-1 of each
	/// region's NAME and that hash is the Babel key directly — `sub_8230E0E8` hashes the name and
	/// asks the TextManager, with no GDB anywhere on the path, so `LevelNameTextTags` is not
	/// involved and neither is any string crossing the ABI.
	///
	/// A hash that resolves to nothing is dropped rather than drawn: it means the name was read
	/// wrongly, and an empty row is a better failure than a wrong one.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Regions(string argument)
	{
		List<(uint NameHash, uint Ordinal)> regions =
			Refii.Host.HostBridge.GetFastTravelRegions();
		if (regions.Count == 0)
		{
			return None;
		}

		var rows = new List<Engine_UiMenuSources.Row>(regions.Count);
		int unresolved = 0;
		foreach ((uint key, uint ordinal) in regions)
		{
			if (Strings is not { } book || !book.TryGetString(key, out string name) ||
				name.Length == 0)
			{
				++unresolved;
				continue;
			}

			rows.Add(new Engine_UiMenuSources.Row(
				Name: $"Region:{key:X8}",
				Label: name,
				Icon: QuestIcon,

				// `sub_829A3A30` spawns regions with 41.
				EventType: RegionEventType,

				// THE ORDINAL PLUS 999, WHICH IS THE GUEST'S OWN IDENTITY — not the name hash,
				// which is what this used to send. `sub_829A3A30` spawns each region with
				// `Float1 = index + 999`, and the handler `sub_826C7E90` recovers the index with
				// `*(event + 8) - 999` and indexes the region container with it directly. A name
				// hash there is an enormous index into a list of seven.
				Id: ordinal + RegionIdBias,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false));
		}

		Report = $"{rows.Count} region(s) of {regions.Count} with an active stop" +
			(unresolved == 0 ? string.Empty : $", {unresolved} whose name did not resolve");
		Godot.GD.Print($"[quest-source] {Report}");
		return rows;
	}

	private const int RegionEventType = 41;

	/// <summary>`sub_829A3A30` adds 999 to the index; `sub_826C7E90` takes it back off.</summary>
	private const uint RegionIdBias = 999u;

	/// <summary>The world map's `Family` — see <see cref="Family"/>.</summary>
	public const string FamilySource = "family";

	/// <summary>
	/// The hero's family, one row per member, name only.
	///
	/// The guest's own builder is `sub_829A42C8`, and it is unusually plain: it runs the
	/// collector `sub_8246F020`, takes `(end - begin) / 52` records and passes ONE field of each
	/// to `sub_826CE8D0` — the display name at `record+8`, as both `String1` and `String2`, with
	/// the shared logbook icon, parent `Family` and the constant 45 in `Float3`. The 52-byte
	/// record also carries the member's mood bands, an event line and two fast-travel graph
	/// answers; none of them reach this list. They belong to the family PAGE.
	///
	/// The names come pre-resolved from the host: see `HostBridge.GetFamily`.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Family(string argument)
	{
		List<string> members = Refii.Host.HostBridge.GetFamily();
		if (members.Count == 0)
		{
			Say($"no family — the hero states none, or none is resident");
			return None;
		}

		var rows = new List<Engine_UiMenuSources.Row>(members.Count);
		for (int i = 0; i < members.Count; ++i)
		{
			rows.Add(new Engine_UiMenuSources.Row(
				// The guest's row name IS the member's name — `sub_829A3260` addresses a row by
				// exactly this string when it emphasises the currently selected member.
				Name: members[i],
				Label: members[i],
				Icon: QuestIcon,
				EventType: FamilyEventType,

				// `Float1` is the LOOP INDEX, not a record key — the guest has nothing else to
				// identify the row by, and neither have we. The index is the guest's own: both
				// this list and the A handler's (`sub_826C6F40` -> `sub_8246F020`) walk the
				// family manager's member list in order, so the identity push IS the guest's
				// event 45 — "trail to this family member?" (box 6), the trail override on yes
				// (`sub_825215A8`), and "fast travel there?" (box 11) when it applies.
				Id: (uint)i,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: true));
		}

		Say($"{rows.Count} family member(s)");
		return rows;
	}

	/// <summary>`sub_829A42C8` spawns family rows with 45 in `Float3`.</summary>
	private const int FamilyEventType = 45;

	private static string _said = string.Empty;

	private static void Say(string report)
	{
		if (_said == report)
		{
			return;
		}

		_said = report;
		Report = report;
		Godot.GD.Print($"[quest-source] {report}");
	}

	/// <summary>The world map's `Sales` — see <see cref="Sales"/>.</summary>
	public const string SalesSource = "sales";

	/// <summary>
	/// The active sales and shortages. The record's `NameTagHash` is a raw FNV-1 text key, so
	/// this resolves it against the book exactly as `sub_829A40C0` hands it to the TextManager.
	/// Both passes are listed together, which is what the guest's own `Sales (2)` counts.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Sales(string argument)
	{
		List<(uint NameKey, bool IsShortage)> sales = Refii.Host.HostBridge.GetSales();
		if (sales.Count == 0)
		{
			return None;
		}

		var rows = new List<Engine_UiMenuSources.Row>(sales.Count);
		int unresolved = 0;
		foreach ((uint key, bool shortage) in sales)
		{
			if (Strings is not { } book || !book.TryGetString(key, out string name) ||
				name.Length == 0)
			{
				++unresolved;
				continue;
			}

			rows.Add(new Engine_UiMenuSources.Row(

				// The guest uniquifies `String1` with the record id because two events can share
				// a name; ours uses the key and the pass, which is unique for the same reason.
				Name: $"Sale:{key:X8}:{(shortage ? 1 : 0)}",
				Label: name,
				Icon: QuestIcon,
				EventType: SalesEventType,
				Id: key,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,

				// A is the guest's event 44, whose id is the ordinal in ITS sales list; the pump
				// finds this key on this pass there — see `Sale:select` in the pump. The guest
				// asks "trail to this sale?" (box 5), writes the trail override on yes, and may
				// ask "fast travel there?" (box 10).
				Command: $"{SaleAction}select:{key}:{(shortage ? 1 : 0)}"));
		}

		Report = $"{rows.Count} sale(s)/shortage(s) of {sales.Count}" +
			(unresolved == 0 ? string.Empty : $", {unresolved} whose name did not resolve");
		Godot.GD.Print($"[quest-source] {Report}");
		return rows;
	}

	/// <summary>`sub_829A40C0` spawns sales with 44.</summary>
	private const int SalesEventType = 44;

	/// <summary>The prefix of a sales row's host action — see the pump's `Sale:select`.</summary>
	public const string SaleAction = "Sale:";

	public static IReadOnlyList<Engine_UiMenuSources.Row> Quests(string argument) =>
		Rows(jobs: false);

	public static IReadOnlyList<Engine_UiMenuSources.Row> Jobs(string argument) =>
		Rows(jobs: true);

	private static IReadOnlyList<Engine_UiMenuSources.Row> Rows(bool jobs)
	{
		List<Refii.Host.HostBridge.QuestEntry> log = Refii.Host.HostBridge.GetQuestLog();
		if (log.Count == 0)
		{
			Report = "the hero states no quest log";
			return None;
		}

		var kept = new List<(Refii.Host.HostBridge.QuestEntry Entry, int Priority, string Label)>();
		foreach (Refii.Host.HostBridge.QuestEntry quest in log)
		{
			// The guest's own four state filters, before the type split.
			if (!quest.Unlocked || quest.Completed || quest.Failed || !quest.OnWorldMap)
			{
				continue;
			}

			if (IsJob(quest.RecordId) != jobs)
			{
				continue;
			}

			kept.Add((quest, PriorityOf(quest.RecordId), LabelOf(quest.RecordId)));
		}

		// DESCENDING by DisplayPriority, the guest's own ordering.
		kept.Sort((a, b) => b.Priority.CompareTo(a.Priority));

		// THE TRAIL FOLLOWS THE PRIMARY QUEST, and a sale or family member can hold it instead:
		// while that override stands, `sub_829A3260` shows the primary quest as merely active
		// (0x829a3770..0x829a37a8). Emphasis 1 means "what the trail follows".
		bool overridden = Refii.Host.HostBridge.QuestTrailOverridden();

		var rows = new List<Engine_UiMenuSources.Row>(kept.Count);
		foreach ((Refii.Host.HostBridge.QuestEntry quest, int _, string label) in kept)
		{
			rows.Add(new Engine_UiMenuSources.Row(
				Name: $"Quest:{quest.RecordId:X8}",
				Label: label,
				Icon: QuestIcon,
				EventType: jobs ? JobEventType : QuestEventType,

				// The guest's row id is its own loop ordinal, which only its re-walk can use;
				// ours is the definition key, which means something on both sides.
				Id: quest.RecordId,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,

				// A is the guest's own event 42 (jobs: 43) on this row, delivered by the pump with
				// the ordinal the guest's own list gives this record: "make this your primary
				// quest?" (box 3 / 4), then `SetAsPrimary`, then "fast travel there?" (box 8 / 9).
				Command: $"{QuestAction}select:{quest.RecordId}",

				// `sub_829A3BA0`: 1 PRIMARY_QUEST, 2 ACTIVE_QUEST, else none.
				Emphasis: quest.Primary && !overridden
					? Engine_UiMenuConstants.Emphasis.PrimaryQuest
					: quest.Active
						? Engine_UiMenuConstants.Emphasis.ActiveQuest
						: Engine_UiMenuConstants.Emphasis.None,

				// A quest or job not yet seen (`Quest+78 == 0`) puts `<exclamation.img>` on its
				// header — `sub_826CFD48` counts these two lists only; the row itself takes none.
				Unviewed: !quest.Viewed));
		}

		Report = $"{rows.Count} {(jobs ? "job" : "quest")}(s) of {log.Count} log entr(y/ies)";
		Godot.GD.Print($"[quest-source] {Report}");
		return rows;
	}

	/// <summary>
	/// The prefix of a quest row's host action: `Quest:select:&lt;record&gt;` goes to the tick
	/// pump, which finds the record in the guest's own world-map list and delivers the row's
	/// event 42 to screen 23 with that ordinal, so the guest asks its questions and commits.
	/// </summary>
	public const string QuestAction = "Quest:";

	/// <summary>`dword_8349D354` — every world-map child row states the same icon.</summary>
	private const string QuestIcon = @"Art\GUI\WheelIcons\icon_logbook.tga";

	/// <summary>`sub_829A3BA0` spawns quests with 42, `sub_829A3E98` jobs with 43.</summary>
	private const int QuestEventType = 42;

	private const int JobEventType = 43;

	/// <summary>
	/// Whether this definition is a JOB — the GDB `Type` the guest's own filter reads.
	///
	/// `Type` IS AN ENUM (field type 5), not an int. Reading it as an int matched nothing, and a
	/// failed read reports "not a job" — so the one job in the log was filed under Quests and the
	/// counts came out 3/0 where the guest shows 2/1. `DisplayPriority` beside it really is an
	/// int (type 1) and `NameTag` a string (type 4); the three do not share a type.
	/// </summary>
	private static bool IsJob(uint definition) =>
		TryNumber(definition, TypeField, out uint type) && type == JobType;

	private static int PriorityOf(uint definition) =>
		TryNumber(definition, PriorityField, out uint priority) ? (int)priority : 0;

	/// <summary>
	/// A whole number, WHICHEVER of the two numeric types it was authored as.
	///
	/// The shipped quest base states `Type` as `int` and `DisplayPriority` as `enum`, side by
	/// side in one record — and both are a plain u32 in the container. Demanding one type read
	/// nothing for the other and failed SILENTLY: every quest sorted at priority 0, so the
	/// list came out in container order rather than the guest's.
	/// </summary>
	private static bool TryNumber(uint definition, uint field, out uint value) =>
		TryDefinitionField(definition, field, (int)GdbFieldType.Int, out value) ||
		TryDefinitionField(definition, field, (int)GdbFieldType.Enum, out value);

	/// <summary>
	/// The quest's DESCRIPTION, resolved the way the label is: the definition's
	/// `DescriptionTextTag` string field through Babel. Empty when the record states none —
	/// which is data, not an error; the page simply has nothing to say.
	/// </summary>
	public static string DescriptionOf(uint definition) =>
		TryDefinitionField(definition, DescriptionField, (int)GdbFieldType.String,
			out uint tagHash) &&
		Globals is { } globals &&
		globals.TryLookup(definition, out GdbCursor cursor) &&
		cursor.View.Names.TryGetValue(tagHash, out string? tag) && tag.Length != 0 &&
		Strings is { } book && book.TryGetString(tag, out string resolved)
			? resolved
			: string.Empty;

	private static string LabelOf(uint definition)
	{
		if (!TryDefinitionField(definition, NameTagField, (int)GdbFieldType.String,
				out uint tagHash) ||
			Globals is not { } globals ||
			!globals.TryLookup(definition, out GdbCursor cursor) ||
			!cursor.View.Names.TryGetValue(tagHash, out string? tag) || tag.Length == 0)
		{
			// `sub_829A3BA0` falls back to "!!!" plus the pointer when the name is under two
			// characters; ours says which definition rather than which address.
			return $"!!!{definition:X8}";
		}

		return Strings is { } book && book.TryGetString(tag, out string resolved) &&
			resolved.Length != 0
				? resolved
				: tag;
	}

	/// <summary>
	/// One inherited field off a quest DEFINITION. Unlike an item, a quest's record IS the
	/// definition — `quest+4` is the GDB record hash and `sub_82522CB0` reads `NameTag` straight
	/// off it, with no component hop.
	/// </summary>
	private static bool TryDefinitionField(uint definition, uint field, int type, out uint value)
	{
		value = 0u;
		return Globals is { } globals &&
			globals.TryLookup(definition, out GdbCursor cursor) &&
			globals.TryFindInherited(cursor, field, type, out _, out value, out _);
	}
}
