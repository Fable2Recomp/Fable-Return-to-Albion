using System.Collections.Generic;
using System.Globalization;
using Refii.Gdb;
using Refii.Mods;

namespace Refii.Titles.Fable2;

using Refii.Ui;
using Row = Refii.Ui.Engine_UiMenuSources.Row;

/// <summary>
/// HOST LISTS A MOD'S MENU CAN SOURCE, so a mod need not build the catalog itself: <c>expressions</c>
/// (the expressions the player's hero knows) and <c>spells_known</c> (the wills the hero can assign),
/// beside <c>abilities:&lt;category&gt;</c> and <c>inventory:usable</c> (whose rows read
/// <see cref="Inventory"/>). The argument is the player, 1 or 2; absent, the player whose menus are
/// open here (<see cref="Engine_UiModMenus.MenuPlayer"/>).
/// </summary>
/// <remarks>
/// <para>THE GUEST STATES THE LISTS: each is the guest tick's own answer to the query a script would
/// make (mod_commands.cpp <c>expressions P</c> — the performer's +0x88 vector — and <c>spells_known P</c>
/// — known AND offered on some tier, the quick wheel's own test — and <c>inventory P</c>, that
/// player's hero's stacks, paged), asked through
/// <see cref="ModHostQueries"/> while a list sourcing it is drawn, at most once per
/// <see cref="RefreshMs"/>; the rows follow the next answer.</para>
/// <para>Rows are TYPED: named <c>expression:0x%08x</c> / <c>will:&lt;spell&gt;</c>
/// (<see cref="ModRowSubject"/>), so the <c>mod_menu_row</c> a choice raises carries
/// <c>subject_kind</c>, <c>definition</c> and <c>spell</c>. They carry no command of their own: the
/// menu's <c>rowCommand</c> (a <c>Mod:lua:</c> call) runs for each.</para>
/// </remarks>
public static class Engine_UiModListSources
{
	public const string ExpressionsSource = "expressions";
	public const string SpellsKnownSource = "spells_known";

	/// <summary>How often a drawn list asks again.</summary>
	public const ulong RefreshMs = 1000;

	/// <summary>The icon every will row shows (the abilities' own, `dword_8349D3AC`).</summary>
	private const string WillIcon = @"Art\GUI\WheelIcons\icon_abilities.tga";

	private sealed class Answer
	{
		public readonly List<uint> Records = new();
		public uint Mask;
		public long Pending;
		public ulong AskedAt;
		public bool Asked;
	}

	private static readonly Answer[] Expressions = { new(), new() };
	private static readonly Answer[] Spells = { new(), new() };

	/// <summary>A player's stacks as the last whole <c>inventory P</c> answer stated them, and the pages of the one being read.</summary>
	private sealed class Stacks
	{
		public List<Fable2Bridge.InventoryRecord> Records = new();
		public List<Fable2Bridge.InventoryRecord> Reading = new();
		public long Pending;
		public ulong AskedAt;
		public bool Asked;
	}

	private static readonly Stacks[] Inventories = { new(), new() };

	/// <summary>
	/// Player <paramref name="player"/>'s hero's inventory (1 or 2; anything else the menus' owner
	/// here), as the guest tick answers <c>inventory P</c> — every page of it, from stack 0 until
	/// <c>next</c> reaches <c>total</c> — asked again at most once per <see cref="RefreshMs"/>; empty
	/// before the first answer.
	/// </summary>
	public static List<Fable2Bridge.InventoryRecord> Inventory(int player)
	{
		player = player is 1 or 2 ? player : (Engine_UiModMenus.MenuPlayer is 2 ? 2 : 1);
		Stacks stacks = Inventories[player - 1];
		ulong now = Godot.Time.GetTicksMsec();
		if ((stacks.Pending == 0 || now - stacks.AskedAt >= 10 * RefreshMs) &&
			(!stacks.Asked || now - stacks.AskedAt >= RefreshMs))
		{
			stacks.Asked = true;
			stacks.Reading = new List<Fable2Bridge.InventoryRecord>();
			AskInventory(stacks, player, 0, now);
		}

		return stacks.Records;
	}

	private static void AskInventory(Stacks stacks, int player, int offset, ulong now)
	{
		stacks.AskedAt = now;
		List<Fable2Bridge.InventoryRecord> reading = stacks.Reading;
		stacks.Pending = ModHostQueries.Ask("inventory",
			string.Create(CultureInfo.InvariantCulture, $"{player} {offset}"), fields =>
		{
			if (!ReferenceEquals(reading, stacks.Reading))
			{
				return;
			}

			stacks.Pending = 0;
			if (!(fields.TryGetValue("ok", out object? ok) && ok is true))
			{
				stacks.Records = new List<Fable2Bridge.InventoryRecord>();
				return;
			}

			int count = Int(fields, "count");
			for (int i = 1; i <= count; i++)
			{
				string n = i.ToString(CultureInfo.InvariantCulture);
				if (fields.TryGetValue("d" + n, out object? d) && d is string hex &&
					uint.TryParse(hex.StartsWith("0x") ? hex[2..] : hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint record))
				{
					reading.Add(new Fable2Bridge.InventoryRecord(record, Int(fields, "n" + n), Int(fields, "c" + n), Int(fields, "v" + n) != 0));
				}
			}

			int next = Int(fields, "next");
			if (next > Int(fields, "offset") && next < Int(fields, "total"))
			{
				AskInventory(stacks, player, next, Godot.Time.GetTicksMsec());
				return;
			}

			stacks.Records = reading;
		});
	}

	/// <summary>The <c>expressions[:p]</c> source.</summary>
	public static IReadOnlyList<Row> ExpressionRows(string argument)
	{
		int player = PlayerOf(argument);
		Answer answer = Expressions[player - 1];
		Refresh(answer, "expressions", player, fields =>
		{
			answer.Records.Clear();
			int count = Int(fields, "count");
			for (int i = 1; i <= count; i++)
			{
				if (fields.TryGetValue("e" + i.ToString(CultureInfo.InvariantCulture), out object? e) && e is string hex &&
					uint.TryParse(hex.StartsWith("0x") ? hex[2..] : hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint record) &&
					record != 0u)
				{
					answer.Records.Add(record);
				}
			}
		});

		var rows = new List<Row>(answer.Records.Count);
		foreach (uint record in answer.Records)
		{
			rows.Add(new Row(
				Name: ModRowSubject.ExpressionRow(record),
				Label: ExpressionLabel(record),
				Icon: string.Empty,
				EventType: 0,
				Id: record,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,
				FromMod: true));
		}

		return rows;
	}

	/// <summary>The <c>spells_known[:p]</c> source.</summary>
	public static IReadOnlyList<Row> SpellRows(string argument)
	{
		int player = PlayerOf(argument);
		Answer answer = Spells[player - 1];
		Refresh(answer, "spells_known", player, fields => answer.Mask = (uint)Int(fields, "mask"));

		var rows = new List<Row>();
		for (int spell = 0; spell < 8; spell++)
		{
			if ((answer.Mask & (1u << spell)) == 0)
			{
				continue;
			}

			rows.Add(new Row(
				Name: ModRowSubject.WillRow(spell),
				Label: Engine_UiAbilitySource.WillLabel(spell),
				Icon: WillIcon,
				EventType: 0,
				Id: (uint)(ModRowSubject.FirstWillAbility + spell),
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,
				FromMod: true));
		}

		return rows;
	}

	/// <summary>The argument's player (1 or 2), else the menus' owner here.</summary>
	private static int PlayerOf(string argument) =>
		int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int player) && player is 1 or 2
			? player
			: (Engine_UiModMenus.MenuPlayer is 2 ? 2 : 1);

	/// <summary>Asks again when nothing is in flight and the last ask is older than <see cref="RefreshMs"/>.</summary>
	private static void Refresh(Answer answer, string what, int player, System.Action<IReadOnlyDictionary<string, object>> take)
	{
		ulong now = Godot.Time.GetTicksMsec();
		if (answer.Pending != 0 && now - answer.AskedAt < 10 * RefreshMs)
		{
			return;
		}

		if (answer.Asked && now - answer.AskedAt < RefreshMs)
		{
			return;
		}

		answer.Asked = true;
		answer.AskedAt = now;
		answer.Pending = ModHostQueries.Ask(what, player.ToString(CultureInfo.InvariantCulture), fields =>
		{
			answer.Pending = 0;
			if (fields.TryGetValue("ok", out object? ok) && ok is true)
			{
				take(fields);
			}
		});
	}

	private static int Int(IReadOnlyDictionary<string, object> fields, string key) =>
		fields.TryGetValue(key, out object? value) && value is long l ? (int)l : 0;

	/// <summary>An expression's name as the logbook titles it: its record's NameTag through the string book.</summary>
	private static string ExpressionLabel(uint record)
	{
		if (Engine_GameGdb.Database is { } multi && multi.TryLookup(record, out GdbCursor at))
		{
			string label = Engine_UiLogbookPage.Babel(Refii.Core.Engine_TitleGame.Active.Strings,
				Engine_UiLogbookPage.Text(multi, at, "NameTag"));
			if (label.Length != 0)
			{
				return label;
			}
		}

		return ModRowSubject.Hex(record);
	}
}
