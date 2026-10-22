using System;
using System.Collections.Generic;
using Godot;
using Refii.Ui;

namespace Refii.Mods;

/// <summary>
/// A mod's input actions as a Controls page the player rebinds in the game: Options › Controls ›
/// the mod's title, beside the game's own Controls page. One row per device an action declares —
/// a key row where <c>input.json</c> binds keys, a pad row where it binds buttons or triggers — on
/// the shared host options page (<see cref="Engine_UiHostOptionsPage"/>: the same furniture, cursor,
/// stepping and prompts every host page has; nothing here lays out a row). Left and right step the
/// row through the mod's default, Unbound, then every bindable input; A writes the page to
/// refii.toml where the shipped keys live (<see cref="ModInput.KeyPrefix"/> / <see cref="ModInput.PadPrefix"/>
/// + the scoped action), B leaves it as it was. A row left at the default stores nothing, so a
/// later change to the mod's file reaches the player; the stored value otherwise wins
/// (<see cref="ModInput.EffectiveKeys"/>). The page binds one input per row; refii.toml itself takes
/// a comma list, shown here as its first entry. An axis has no row: it is never rebound.
/// </summary>
public static class ModInputUi
{
	/// <summary>The menu source the Controls fold is filled from: one row per mod with rebindable actions.</summary>
	public const string SourceName = "mod-controls";

	/// <summary>The command a mod's row carries; the suffix is the mod's name.</summary>
	public const string OpenCommandPrefix = "options:controls:";

	public enum Device
	{
		Keyboard,
		Pad,
	}

	/// <summary>The first two positions of every row.</summary>
	public const int DefaultIndex = 0;

	public const int UnboundIndex = 1;

	/// <summary>
	/// The keys a key row steps through, in the order the page shows them: letters, digits, the
	/// function keys, then the usual PC keys. Stored and shown by Godot's own key names, which the
	/// file's <c>keys</c> and refii.toml's <c>key_*</c> already use.
	/// </summary>
	public static readonly IReadOnlyList<Key> KeyCandidates = BuildKeyCandidates();

	/// <summary>The pad inputs a pad row steps through, by the names the file takes.</summary>
	public static readonly IReadOnlyList<string> PadCandidates = new[]
	{
		"A", "B", "X", "Y", "LB", "RB", "LT", "RT", "L3", "R3", "Start", "Back",
		"DpadUp", "DpadDown", "DpadLeft", "DpadRight",
	};

	private static IReadOnlyList<Key> BuildKeyCandidates()
	{
		var keys = new List<Key>();
		for (Key key = Key.A; key <= Key.Z; key++)
		{
			keys.Add(key);
		}

		for (Key key = Key.Key0; key <= Key.Key9; key++)
		{
			keys.Add(key);
		}

		for (Key key = Key.F1; key <= Key.F12; key++)
		{
			keys.Add(key);
		}

		keys.AddRange(new[]
		{
			Key.Space, Key.Enter, Key.Tab, Key.Shift, Key.Ctrl, Key.Alt, Key.Escape, Key.Backspace,
			Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.Pageup, Key.Pagedown,
			Key.Insert, Key.Delete, Key.Capslock, Key.Quoteleft, Key.Minus, Key.Equal, Key.Bracketleft,
			Key.Bracketright, Key.Backslash, Key.Semicolon, Key.Apostrophe, Key.Comma, Key.Period, Key.Slash,
			Key.Kp0, Key.Kp1, Key.Kp2, Key.Kp3, Key.Kp4, Key.Kp5, Key.Kp6, Key.Kp7, Key.Kp8, Key.Kp9,
			Key.KpAdd, Key.KpSubtract, Key.KpMultiply, Key.KpDivide, Key.KpPeriod, Key.KpEnter,
		});
		return keys;
	}

	/// <summary>A key's name as refii.toml and <c>input.json</c> spell it: Godot's own, what <c>OS.FindKeycodeFromString</c> reads back.</summary>
	public static string KeyName(Key key) => OS.GetKeycodeString(key);

	/// <summary>One row: an action on one device.</summary>
	public sealed record Row(ModInput.Action Action, Device Device)
	{
		/// <summary>The row's label: the action's label and the device.</summary>
		public string Label => Action.Label + (Device == Device.Keyboard ? " (key)" : " (pad)");

		/// <summary>The refii.toml key the row writes.</summary>
		public string SettingKey => Device == Device.Keyboard ? ModInput.KeySettingKey(Action) : ModInput.PadSettingKey(Action);

		/// <summary>Default, Unbound, then every candidate.</summary>
		public int Choices => 2 + (Device == Device.Keyboard ? KeyCandidates.Count : PadCandidates.Count);

		/// <summary>The mod's own binding as a comma list.</summary>
		public string DefaultText
		{
			get
			{
				if (Device == Device.Pad)
				{
					return ModInput.DeclaredPadText(Action);
				}

				var names = new List<string>();
				foreach (Key key in Action.Keys)
				{
					names.Add(KeyName(key));
				}

				return string.Join(",", names);
			}
		}

		/// <summary>The caption at an index: the default spelled out, Unbound, or the candidate's name.</summary>
		public string Caption(int index)
		{
			if (index == DefaultIndex)
			{
				return "Default (" + DefaultText + ")";
			}

			if (index == UnboundIndex)
			{
				return "Unbound";
			}

			return CandidateName(index - 2) ?? string.Empty;
		}

		/// <summary>A candidate's stored name, or null past the list.</summary>
		public string? CandidateName(int candidate)
		{
			if (candidate < 0)
			{
				return null;
			}

			if (Device == Device.Keyboard)
			{
				return candidate < KeyCandidates.Count ? KeyName(KeyCandidates[candidate]) : null;
			}

			return candidate < PadCandidates.Count ? PadCandidates[candidate] : null;
		}

		/// <summary>
		/// The index a stored value stands at: absent or empty is the default; <see cref="ModInput.Unbound"/>
		/// is Unbound; otherwise the candidate the list's FIRST name matches (case-insensitive), or
		/// the default when none does (a hand-written name the page cannot offer).
		/// </summary>
		public int IndexOfStored(string? stored)
		{
			string text = (stored ?? string.Empty).Trim().Trim('"').Trim();
			if (text.Length == 0)
			{
				return DefaultIndex;
			}

			if (text.Equals(ModInput.Unbound, StringComparison.OrdinalIgnoreCase))
			{
				return UnboundIndex;
			}

			string first = text;
			foreach (string name in ModInput.Names(text))
			{
				first = name;
				break;
			}

			for (int i = 0; ; i++)
			{
				string? candidate = CandidateName(i);
				if (candidate is null)
				{
					return DefaultIndex;
				}

				if (string.Equals(candidate, first, StringComparison.OrdinalIgnoreCase))
				{
					return i + 2;
				}
			}
		}

		/// <summary>What an index stores: null for the default (the key is removed), else the text.</summary>
		public string? StoredAt(int index) => index switch
		{
			DefaultIndex => null,
			UnboundIndex => ModInput.Unbound,
			_ => CandidateName(index - 2),
		};
	}

	/// <summary>One mod's page: its rebindable rows in the file's order, key row before pad row.</summary>
	public sealed class Page : Engine_UiHostOptionsPage
	{
		public Page(string mod, string title, IReadOnlyList<Row> bindings)
		{
			Mod = mod;
			Title = title;
			Bindings = bindings;
		}

		public string Mod { get; }

		/// <summary>The page's rows, top to bottom.</summary>
		public IReadOnlyList<Row> Bindings { get; }

		public override string Name => OpenCommandPrefix + Mod;
		public override string Title { get; }
		public override int Count => Bindings.Count;
		public override string RowTitle(int row) => Bindings[row].Label;
		public override int Choices(int row) => Bindings[row].Choices;
		public override string Caption(int row, int index) => Bindings[row].Caption(index);

		public override int[] Read() => Read(key => Refii.Core.Engine_Switches.String(key, string.Empty));

		/// <summary>Every row's index for the stored values <paramref name="read"/> gives (empty for unset).</summary>
		public int[] Read(Func<string, string> read)
		{
			var indices = new int[Bindings.Count];
			for (int i = 0; i < indices.Length; i++)
			{
				indices[i] = Bindings[i].IndexOfStored(read(Bindings[i].SettingKey));
			}

			return indices;
		}

		/// <summary>The values a set of indices stores, keyed by setting; a null value removes the key.</summary>
		public IReadOnlyDictionary<string, string?> Values(Func<int, int> index)
		{
			var values = new Dictionary<string, string?>(StringComparer.Ordinal);
			for (int i = 0; i < Bindings.Count; i++)
			{
				values[Bindings[i].SettingKey] = Bindings[i].StoredAt(index(i));
			}

			return values;
		}

		/// <summary>A: the page to refii.toml as one write, and the bindings in force at once.</summary>
		public override bool Commit(Func<int, int> index)
		{
			var write = new Dictionary<string, string>(StringComparer.Ordinal);
			var remove = new List<string>();
			foreach ((string key, string? value) in Values(index))
			{
				if (value is null)
				{
					remove.Add(key);
				}
				else
				{
					write[key] = value;
				}
			}

			if (!Engine_UiScreenActions.WriteSettings(write, remove))
			{
				return false;
			}

			ModInput.ReadBindings(Refii.Core.Engine_RefiiConfig.Read(out _));
			return true;
		}
	}

	/// <summary>The rows an action offers: a key row when it declares keys, a pad row when it declares buttons or triggers.</summary>
	public static IEnumerable<Row> RowsFor(ModInput.Action action)
	{
		if (action.Keys.Count != 0)
		{
			yield return new Row(action, Device.Keyboard);
		}

		if (action.Buttons.Count != 0 || action.Triggers.Count != 0)
		{
			yield return new Row(action, Device.Pad);
		}
	}

	/// <summary>Every enabled mod's page, in load order, from the actions declared now; a mod with nothing rebindable has none.</summary>
	public static IReadOnlyList<Page> Pages(IReadOnlyList<ModInput.Action> actions, Func<string, string> title)
	{
		var order = new List<string>();
		var rowsByMod = new Dictionary<string, List<Row>>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInput.Action action in actions)
		{
			if (!rowsByMod.TryGetValue(action.Mod, out List<Row>? rows))
			{
				rows = new List<Row>();
				rowsByMod[action.Mod] = rows;
				order.Add(action.Mod);
			}

			rows.AddRange(RowsFor(action));
		}

		var pages = new List<Page>();
		foreach (string mod in order)
		{
			if (rowsByMod[mod].Count != 0)
			{
				pages.Add(new Page(mod, title(mod), rowsByMod[mod]));
			}
		}

		return pages;
	}

	private static IReadOnlyList<Page>? _pages;

	private static IReadOnlyList<ModInput.Action>? _builtFor;

	/// <summary>The pages for the actions loaded now; rebuilt when the actions reload.</summary>
	public static IReadOnlyList<Page> Pages()
	{
		IReadOnlyList<ModInput.Action> actions = ModInput.All;
		if (_pages is null || !ReferenceEquals(_builtFor, actions))
		{
			var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (ModInstall install in ModSession.Resolve(Refii.Assets.Engine_GameAssets.ModsRoot).Installs)
			{
				titles[install.Name] = install.Metadata.Title.Length != 0 ? install.Metadata.Title : install.Name;
			}

			_pages = Pages(actions, mod => titles.TryGetValue(mod, out string? title) ? title : mod);
			_builtFor = actions;
		}

		return _pages;
	}

	/// <summary>The page a row's command suffix names, or null for a mod with nothing rebindable.</summary>
	public static Engine_UiHostOptionsPage? PageFor(string mod)
	{
		foreach (Page page in Pages())
		{
			if (string.Equals(page.Mod, mod, StringComparison.OrdinalIgnoreCase))
			{
				return page;
			}
		}

		return null;
	}

	/// <summary>The Controls fold's sourced rows: one per page, carrying the command that opens it.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (Page page in Pages())
		{
			rows.Add(new Engine_UiMenuSources.Row(
				Name: "modcontrols:" + page.Mod,
				Label: page.Title,
				Icon: string.Empty,
				EventType: 0,
				Id: 0u,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,
				Command: OpenCommandPrefix + page.Mod));
		}

		return rows;
	}
}
