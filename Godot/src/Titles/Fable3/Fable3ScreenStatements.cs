namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// What the guest STATED for one box-manager screen, by its box: the cursor and choice fields its
/// Show and pad handler left (`@Screen.Fields`), its opener's arguments (`@Screen.Opened`) and the
/// rows its writers were called with (quest summary, king's schedule, co-op partners). The screen
/// models (<see cref="Fable3ScreenModel"/>, <see cref="Fable3BoxScreenModel"/>) draw the highlight
/// and the ghosting from this, never from the elements' slides.
/// </summary>
public sealed class Fable3ScreenState
{
	public static readonly Fable3ScreenState Empty = new();

	/// <summary>The field set's kind (screen_stated_hooks.cpp StateKindFields: join, chat, bed, …).</summary>
	public string Kind { get; set; } = string.Empty;

	/// <summary>The fields as the latest Show or handler left them.</summary>
	public int[] Fields { get; set; } = System.Array.Empty<int>();

	/// <summary>The fields as the Show left them (the starting cursor: join's saved setting), or null before the Show.</summary>
	public int[]? Shown { get; set; }

	/// <summary>The opener's / ctor's arguments: bed (offered, group, cycle), wedding (three affordable bytes).</summary>
	public int[] Opened { get; set; } = System.Array.Empty<int>();

	/// <summary>The partners' window rows by row path: sub_82B5D9E0's record (married, online, joinable).</summary>
	public Dictionary<string, (bool Married, bool Online, bool Joinable)> PartnerRows { get; } = new(System.StringComparer.Ordinal);

	/// <summary>The king's schedule rows 1..4: sub_82973328's tick slide and task tag.</summary>
	public SortedDictionary<int, (int Tick, uint Tag)> ScheduleRows { get; } = new();

	/// <summary>The quest summary's window rows 0..6: sub_82973FB0's quest, selected and last.</summary>
	public SortedDictionary<int, (uint Quest, bool Selected, bool Last)> QuestRows { get; } = new();

	/// <summary>The stats page as sub_8296EC88 was asked to move it (2 categories, 3 statistics, 4 friends), null before.</summary>
	public int? StatsPage { get; set; }

	/// <summary>The stats pages' windows by page (2 categories, 3 statistics, 4 friends): the first row, the list's count and the rows drawn.</summary>
	public Dictionary<int, (int Start, int Count, int Rows)> StatsWindows { get; } = new();

	/// <summary>The promise ctor's tag and accepted byte (sub_827A3290), null before it.</summary>
	public (uint Tag, bool Accepted)? Promise { get; set; }

	/// <summary>The save menu (no box): its row 1 Save, 2 Save and Quit, 3 Quit, 4 Character Management, and its `allowed`.</summary>
	public int SaveRow { get; set; }

	public bool SaveAllowed { get; set; } = true;

	public int Field(int index, int fallback) => index < Fields.Length ? Fields[index] : fallback;

	public int ShownField(int index, int fallback) => Shown is { } shown && index < shown.Length ? shown[index] : fallback;

	public bool OpenedFlag(int index, bool fallback) => index < Opened.Length ? Opened[index] != 0 : fallback;
}

/// <summary>
/// The box-manager screens' statements (Fable3/src/hooks/src/screen_stated_hooks.cpp), held per
/// box until the box closes: `@Screen.Fields(box, kind, shown, fields…)`, `@Screen.Opened(box,
/// kind, args…)`, `@Screen.PartnerRow(box, path, married, online, joinable)`,
/// `@Screen.ScheduleRow(box, row, tick, tag)`, `@Screen.QuestRow(box, row, quest, selected, last)`,
/// `@Screen.StatsPage(box, page)`, `@Screen.StatsWindow(box, page, start, count, rows)`,
/// `@Screen.Schedule(box, day)` and `@Screen.Promise(box, tag, accepted)`.
/// </summary>
public static class Fable3ScreenStatements
{
	private static readonly Dictionary<uint, Fable3ScreenState> Boxes = new();

	private static readonly Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> Routes = BuildRoutes();

	/// <summary>Bumped on every statement here.</summary>
	public static ulong Generation { get; private set; }

	public static System.Action<string> Log { get; set; } = message => GD.Print(message);

	/// <summary>The stated record of a box, or <see cref="Fable3ScreenState.Empty"/> when nothing was stated for it.</summary>
	public static Fable3ScreenState Of(uint box) =>
		Boxes.TryGetValue(box, out Fable3ScreenState? state) ? state : Fable3ScreenState.Empty;

	/// <summary>A box closed or was dropped: its address is the next box's.</summary>
	public static void Forget(uint box)
	{
		if (Boxes.Remove(box))
		{
			++Generation;
		}
	}

	public static void Clear()
	{
		Boxes.Clear();
		++Generation;
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (KeyValuePair<string, System.Action<Engine_UiModel.GuiNativeCall>> route in Routes)
		{
			System.Action<Engine_UiModel.GuiNativeCall> handler = route.Value;
			router.Register(route.Key, call => handler(call));
		}
	}

	/// <summary>Takes one statement as the router would (the tests' entry); false when it is not this model's.</summary>
	public static bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (!Routes.TryGetValue(call.Name, out System.Action<Engine_UiModel.GuiNativeCall>? handler))
		{
			return false;
		}

		handler(call);
		return true;
	}

	private static Fable3ScreenState At(Engine_UiModel.GuiNativeCall call)
	{
		uint box = Fable3Elements.Parse(call.Argument(0).Text);
		if (!Boxes.TryGetValue(box, out Fable3ScreenState? state))
		{
			if (Boxes.Count > 256)
			{
				Boxes.Clear();
			}

			Boxes[box] = state = new Fable3ScreenState();
		}

		++Generation;
		return state;
	}

	private static int[] Numbers(Engine_UiModel.GuiNativeCall call, int from)
	{
		int count = call.Arguments is { } arguments ? System.Math.Max(0, arguments.Count - from) : 0;
		var values = new int[count];
		for (int i = 0; i < count; ++i)
		{
			values[i] = (int)call.Argument(from + i).Number;
		}

		return values;
	}

	private static Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> BuildRoutes()
	{
		var routes = new Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>>(System.StringComparer.Ordinal);
		void Add(string name, System.Action<Engine_UiModel.GuiNativeCall> handler) => routes[name] = handler;

		Add("@Screen.Fields", call =>
		{
			Fable3ScreenState state = At(call);
			state.Kind = call.Argument(1).Text;
			state.Fields = Numbers(call, 3);
			if (call.Argument(2).Boolean)
			{
				state.Shown = state.Fields;
			}

			Log($"[fable3-screens] {state.Kind} box {call.Argument(0).Text} fields {string.Join(' ', state.Fields)}{(call.Argument(2).Boolean ? " (shown)" : string.Empty)}");
		});
		Add("@Screen.Opened", call =>
		{
			Fable3ScreenState state = At(call);
			state.Opened = Numbers(call, 2);
			Log($"[fable3-screens] {call.Argument(1).Text} box {call.Argument(0).Text} opened with {string.Join(' ', state.Opened)}");
		});
		Add("@Screen.PartnerRow", call => At(call).PartnerRows[call.Argument(1).Text] =
			(call.Argument(2).Boolean, call.Argument(3).Boolean, call.Argument(4).Boolean));
		Add("@Screen.ScheduleRow", call => At(call).ScheduleRows[(int)call.Argument(1).Number] =
			((int)call.Argument(2).Number, Fable3Elements.Parse(call.Argument(3).Text)));
		Add("@Screen.QuestRow", call => At(call).QuestRows[(int)call.Argument(1).Number] =
			(Fable3Elements.Parse(call.Argument(2).Text), call.Argument(3).Boolean, call.Argument(4).Boolean));
		Add("@Screen.StatsPage", call =>
		{
			At(call).StatsPage = (int)call.Argument(1).Number;
			Log($"[fable3-stated] stats page {(int)call.Argument(1).Number} (box {call.Argument(0).Text})");
		});
		Add("@Screen.Schedule", call => Log($"[fable3-stated] king's schedule day {(int)call.Argument(1).Number} (box {call.Argument(0).Text})"));
		Add("@Screen.StatsWindow", call => At(call).StatsWindows[(int)call.Argument(1).Number] =
			((int)call.Argument(2).Number, (int)call.Argument(3).Number, (int)call.Argument(4).Number));
		Add("@Screen.Promise", call =>
		{
			At(call).Promise = (Fable3Elements.Parse(call.Argument(1).Text), call.Argument(2).Boolean);
			Log($"[fable3-stated] promise tag {call.Argument(1).Text} accepted {call.Argument(2).Boolean}");
		});

		return routes;
	}
}
