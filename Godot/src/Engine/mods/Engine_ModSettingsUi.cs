using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Ui;

namespace Refii.Mods;

/// <summary>
/// A mod's settings as an options page the player tunes in the game, instead of editing
/// <c>settings.json</c>: every enabled mod whose author offers settings to the player gets a row
/// under the options screen's Mods fold, and the row opens a host options page (<see cref="Engine_UiHostOptionsPage"/>)
/// with one value row per setting. Left and right step the value, applied live and saved at
/// once (<see cref="ModSettings.Persist"/>); A and B leave the page.
/// </summary>
/// <remarks>
/// <para>A mod OFFERS its rows in <c>settings.ui.json</c> beside <c>settings.json</c>, keyed by the setting's name without the <c>&lt;mod&gt;.</c> prefix:</para>
/// <code>
/// { "swing_cost": { "label": "Swing cost", "min": 0, "max": 100, "step": 1,
///                   "type": "slider", "description": "Stamina a melee swing spends." },
///   "woven":      { "label": "Weaving", "type": "toggle" },
///   "difficulty": { "label": "Difficulty", "type": "choice",
///                   "options": [ { "value": 0, "label": "Story" }, { "value": 1, "label": "Hard" } ] } }
/// </code>
/// <para>A <c>choice</c> row steps through a fixed list of labelled values, in the order the file
/// lists them; the setting holds the chosen option's number, and a value no option states shows
/// the nearest one.</para>
/// <para>Any label, description or option label may name the mod's text as <c>{KEY}</c> — a row of its
/// <c>language/&lt;culture&gt;/text/book.babel.csv</c> — drawn in the culture in force (<see cref="ModText"/>);
/// a plain string is drawn as written. The page's title is <c>mod.toml</c>'s name, the same way.</para>
/// <para>Only what the file describes is shown. A setting it leaves out is the author's tuning, not
/// the player's, and a mod with no <c>settings.ui.json</c> has no page at all.</para>
/// </remarks>
public static class ModSettingsUi
{
	public const string ModSettingsUiPath = "settings.ui.json";

	/// <summary>The menu source the options screen's Mods fold is filled from.</summary>
	public const string SourceName = "mod-settings";

	/// <summary>The command a mod's row carries; the suffix is the mod's name.</summary>
	public const string OpenCommandPrefix = "options:mod:";

	public enum Kind
	{
		Slider,
		Toggle,
		Choice,
	}

	/// <summary>
	/// One labelled value of a <see cref="Kind.Choice"/> row. A mod's setting holds a number
	/// (<see cref="Value"/>); a page over the game's config switches stores text (<see cref="Text"/>,
	/// with <see cref="Aliases"/> the other spellings a stored value may have, e.g. "true" for "on").
	/// A text option's <see cref="Value"/> is its position.
	/// </summary>
	public readonly record struct Option(float Value, string Label, string? Text = null, IReadOnlyList<string>? Aliases = null);

	/// <summary>
	/// One value row: the setting, how it is shown and the range it steps over — or, for a
	/// <see cref="Kind.Choice"/> row, the <see cref="Options"/> it steps through.
	/// </summary>
	public sealed record Row(
		string Mod, string Key, string Label, Kind Kind, float Min, float Max, float Step, string Description,
		IReadOnlyList<Option>? Options = null, string Default = "")
	{
		/// <summary>The value in force now — the player's, the live override's or the file's.</summary>
		public float Current => ModSettings.TryGet(Key, out float value) ? value : Min;

		/// <summary>How many positions the row's index takes.</summary>
		public int Choices => Kind switch
		{
			Kind.Toggle => 2,
			Kind.Choice => Math.Max(1, Options?.Count ?? 0),
			_ => (int)MathF.Round((Max - Min) / Step) + 1,
		};

		/// <summary>The value an index stands for.</summary>
		public float ValueAt(int index) => Kind switch
		{
			Kind.Toggle => index != 0 ? 1f : 0f,
			Kind.Choice => Options is { Count: > 0 } options ? options[Math.Clamp(index, 0, options.Count - 1)].Value : Min,
			_ => Math.Clamp(Min + index * Step, Min, Max),
		};

		/// <summary>The index nearest a value; for a choice, the option with the nearest value (the first of a tie).</summary>
		public int IndexOf(float value)
		{
			switch (Kind)
			{
				case Kind.Toggle:
					return value != 0f ? 1 : 0;

				case Kind.Choice:
					int best = 0;
					for (int i = 1; Options is not null && i < Options.Count; i++)
					{
						if (MathF.Abs(Options[i].Value - value) < MathF.Abs(Options[best].Value - value))
						{
							best = i;
						}
					}

					return best;

				default:
					return Math.Clamp((int)MathF.Round((value - Min) / Step), 0, Choices - 1);
			}
		}

		/// <summary>
		/// The index a STORED value stands at, for a page over text switches: a choice's option whose
		/// text or alias matches (case-insensitive), else the option nearest a number; a toggle on
		/// "true"/"1"/"on"; a slider at the nearest step. An empty value reads as <see cref="Default"/>.
		/// </summary>
		public int IndexOfStored(string? stored)
		{
			string text = (stored ?? string.Empty).Trim().Trim('"');
			if (text.Length == 0)
			{
				text = Default;
			}

			bool number = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed);
			switch (Kind)
			{
				case Kind.Toggle:
					return text.ToLowerInvariant() is "true" or "1" or "on" ? 1 : 0;

				case Kind.Choice when Options is { Count: > 0 } options && options.Any(o => o.Text is not null):
					for (int i = 0; i < options.Count; i++)
					{
						if (string.Equals(options[i].Text, text, StringComparison.OrdinalIgnoreCase) ||
							(options[i].Aliases?.Any(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase)) ?? false))
						{
							return i;
						}
					}

					int nearest = -1;
					for (int i = 0; number && i < options.Count; i++)
					{
						if (float.TryParse(options[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float at) &&
							(nearest < 0 || MathF.Abs(at - parsed) < MathF.Abs(float.Parse(options[nearest].Text!, CultureInfo.InvariantCulture) - parsed)))
						{
							nearest = i;
						}
					}

					return nearest >= 0 ? nearest : (text == Default ? 0 : IndexOfStored(Default));

				default:
					return number ? IndexOf(parsed) : 0;
			}
		}

		/// <summary>The text a page over switches stores for an index: the option's text, "true"/"false", or the number.</summary>
		public string StoredAt(int index) => Kind switch
		{
			Kind.Toggle => index != 0 ? "true" : "false",
			Kind.Choice when Options is { Count: > 0 } options &&
				options[Math.Clamp(index, 0, options.Count - 1)].Text is { } text => text,
			_ => ValueAt(index).ToString(CultureInfo.InvariantCulture),
		};

		/// <summary>The caption at an index: On/Off, the option's label, or the number at the step's precision.</summary>
		public string Caption(int index)
		{
			if (Kind == Kind.Toggle)
			{
				// The game's own words (the Options page's), through the mods' text lookup.
				return ModText.Word(index != 0 ? ModText.OnTag : ModText.OffTag);
			}

			if (Kind == Kind.Choice)
			{
				return Options is { Count: > 0 } options ? options[Math.Clamp(index, 0, options.Count - 1)].Label : string.Empty;
			}

			int decimals = Step >= 1f ? 0 : Math.Clamp((int)MathF.Ceiling(-MathF.Log10(Step)), 0, 3);
			return ValueAt(index).ToString("F" + decimals, CultureInfo.InvariantCulture);
		}
	}

	/// <summary>One mod's page: its title and its rows, in the order the file states them.</summary>
	public sealed class Page : Engine_UiHostOptionsPage
	{
		public Page(string mod, string title, IReadOnlyList<Row> settings)
		{
			Mod = mod;
			Title = title;
			Settings = settings;
		}

		public string Mod { get; }

		/// <summary>The page's value rows, top to bottom.</summary>
		public IReadOnlyList<Row> Settings { get; }

		public override string Name => OpenCommandPrefix + Mod;
		public override string Title { get; }
		public override int Count => Settings.Count;
		public override string RowTitle(int row) => Settings[row].Label;
		public override int Choices(int row) => Settings[row].Choices;
		public override string Caption(int row, int index) => Settings[row].Caption(index);

		public override int[] Read()
		{
			var indices = new int[Settings.Count];
			for (int i = 0; i < indices.Length; i++)
			{
				indices[i] = Settings[i].IndexOf(Settings[i].Current);
			}

			return indices;
		}

		/// <summary>Applied as it moves: the mod sees the value now and the file keeps it.</summary>
		public override void Changed(int row, int index)
		{
			Row setting = Settings[row];
			ModSettings.Persist(setting.Key, setting.ValueAt(index));
		}
	}

	/// <summary>
	/// A page over the GAME's own config switches (refii.toml, <see cref="Refii.Core.Engine_Switches"/>),
	/// described in exactly the shape a mod's <c>settings.ui.json</c> takes — the same parser, the
	/// same rows — with each entry keyed by the switch, its <c>default</c> what an unset switch reads
	/// as, and <c>choice</c> options that may store text. Values are committed together on A, as
	/// one config write; a refused write keeps the page open.
	/// </summary>
	public sealed class SwitchPage : Engine_UiHostOptionsPage
	{
		public SwitchPage(string name, string title, IReadOnlyList<Row> rows)
		{
			Name = name;
			Title = title;
			Settings = rows;
		}

		/// <summary>A page from its description: a JSON object of rows keyed by switch, in order.</summary>
		public static SwitchPage FromDescription(string name, string title, string description) =>
			new(name, title, DescribeRows(name, description));

		public IReadOnlyList<Row> Settings { get; }

		public override string Name { get; }
		public override string Title { get; }
		public override int Count => Settings.Count;
		public override string RowTitle(int row) => Settings[row].Label;
		public override int Choices(int row) => Settings[row].Choices;
		public override string Caption(int row, int index) => Settings[row].Caption(index);

		/// <summary>The row stating <paramref name="key"/>, or null.</summary>
		public Row? RowFor(string key) =>
			Settings.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal));

		public override int[] Read() => Read(key => Refii.Core.Engine_Switches.String(key, string.Empty));

		/// <summary>Every row's index for the stored values <paramref name="read"/> gives (empty for unset).</summary>
		public int[] Read(Func<string, string> read)
		{
			var indices = new int[Settings.Count];
			for (int i = 0; i < indices.Length; i++)
			{
				indices[i] = Settings[i].IndexOfStored(read(Settings[i].Key));
			}

			return indices;
		}

		/// <summary>The switch values a set of indices stands for, keyed by switch.</summary>
		public IReadOnlyDictionary<string, string> Values(Func<int, int> index)
		{
			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int i = 0; i < Settings.Count; i++)
			{
				values[Settings[i].Key] = Settings[i].StoredAt(index(i));
			}

			return values;
		}

		public override bool Commit(Func<int, int> index) => Engine_UiScreenActions.WriteSettings(Values(index));
	}

	/// <summary>
	/// The rows a description states, in its order, each keyed by the description's own key — the
	/// parse a <see cref="SwitchPage"/> is built from. A row with no <c>type</c> and no range reads
	/// as a toggle when its default does.
	/// </summary>
	public static IReadOnlyList<Row> DescribeRows(string owner, string description)
	{
		var rows = new List<Row>();
		if (Json.ParseString(description) is not { VariantType: Variant.Type.Dictionary } parsed)
		{
			GD.PushWarning($"[settings-page] {owner}: the description is not a JSON object");
			return rows;
		}

		foreach (KeyValuePair<Variant, Variant> entry in parsed.AsGodotDictionary())
		{
			Godot.Collections.Dictionary? spec = entry.Value.VariantType == Variant.Type.Dictionary
				? entry.Value.AsGodotDictionary()
				: null;
			string fallback = DefaultText(spec);
			float file = fallback.ToLowerInvariant() is "true" or "1" ? 1f
				: float.TryParse(fallback, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ? number
				: 0f;
			string key = entry.Key.AsString();
			rows.Add(Describe(owner, key, key, file, spec, switches: true));
		}

		return rows;
	}

	private static List<Page>? _pages;

	private static IReadOnlyDictionary<string, float>? _builtFor;

	private static string? _builtCulture;

	private static ModText.Tables? _builtTables;

	/// <summary>
	/// Every enabled mod's page, in load order; rebuilt when the settings are reloaded, and when the
	/// culture or the mounted text changes (the rows hold their text resolved, <see cref="ModText.Game"/>).
	/// </summary>
	public static IReadOnlyList<Page> Pages
	{
		get
		{
			IReadOnlyDictionary<string, float> defaults = ModSettings.Defaults;
			string culture = Refii.Text.Engine_GameCulture.Current;
			ModText.Tables tables = ModText.Mounted;
			if (_pages is null || !ReferenceEquals(_builtFor, defaults) || _builtCulture != culture || !ReferenceEquals(_builtTables, tables))
			{
				_pages = Build(defaults);
				_builtFor = defaults;
				_builtCulture = culture;
				_builtTables = tables;
			}

			return _pages;
		}
	}

	/// <summary>The page a row's command suffix names, or null for a mod with no settings.</summary>
	public static Engine_UiHostOptionsPage? PageFor(string mod)
	{
		foreach (Page page in Pages)
		{
			if (string.Equals(page.Mod, mod, StringComparison.OrdinalIgnoreCase))
			{
				return page;
			}
		}

		return null;
	}

	/// <summary>
	/// One mod's page as the game builds it from <c>settings.json</c> and <c>settings.ui.json</c>
	/// texts, with each row's value as the file states it — the Mod Studio's preview of a page it is
	/// editing, through the same row description the game's pages take.
	/// </summary>
	public static (Page Page, IReadOnlyList<float> Values) PreviewPage(
		string mod, string title, string settingsText, string settingsUiText)
	{
		Dictionary<string, float> file = ModSettings.Parse(mod, settingsText);
		Godot.Collections.Dictionary? ui = Json.ParseString(settingsUiText) is { VariantType: Variant.Type.Dictionary } parsed
			? parsed.AsGodotDictionary()
			: null;

		var rows = new List<Row>();
		var values = new List<float>();
		foreach ((string key, float value) in file)
		{
			string name = key.StartsWith(mod + ".", StringComparison.OrdinalIgnoreCase) ? key[(mod.Length + 1)..] : key;
			Godot.Collections.Dictionary? spec = null;
			if (ui is not null && (ui.TryGetValue(name, out Variant found) || ui.TryGetValue(key, out found)) &&
				found.VariantType == Variant.Type.Dictionary)
			{
				spec = found.AsGodotDictionary();
			}

			if (spec is null)
			{
				continue;
			}

			rows.Add(Describe(mod, key, name, value, spec));
			values.Add(value);
		}

		return (new Page(mod, title, rows), values);
	}

	/// <summary>The Mods fold's rows: one per page, carrying the command that opens it.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (Page page in Pages)
		{
			rows.Add(new Engine_UiMenuSources.Row(
				Name: "modsettings:" + page.Mod,
				Label: page.Title,
				Icon: string.Empty,
				EventType: 0,
				Id: 0u,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,
				Command: OpenCommandPrefix + page.Mod,

				// On the shipped options screen, but the guest has no row for a mod's page.
				FromMod: true));
		}

		return rows;
	}

	private static List<Page> Build(IReadOnlyDictionary<string, float> defaults)
	{
		// The keys each mod states, in the order the file states them (Owners is a map; the
		// order is taken from the files themselves).
		var order = new List<string>();
		var keysByMod = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		foreach ((string mod, string text) in ModEntries.ReadEnabledText(ModSettings.ModSettingsPath))
		{
			if (Json.ParseString(text) is not { VariantType: Variant.Type.Dictionary } parsed)
			{
				continue;
			}

			foreach (KeyValuePair<Variant, Variant> entry in parsed.AsGodotDictionary())
			{
				string key = entry.Key.AsString();
				if (entry.Value.VariantType == Variant.Type.Dictionary)
				{
					foreach (KeyValuePair<Variant, Variant> inner in entry.Value.AsGodotDictionary())
					{
						Claim(mod, key + "." + inner.Key.AsString());
					}
				}
				else
				{
					Claim(mod, key);
				}
			}
		}

		var described = new Dictionary<string, Godot.Collections.Dictionary>(StringComparer.OrdinalIgnoreCase);
		foreach ((string mod, string text) in ModEntries.ReadEnabledText(ModSettingsUiPath))
		{
			if (Json.ParseString(text) is { VariantType: Variant.Type.Dictionary } parsed)
			{
				described[mod] = parsed.AsGodotDictionary();
			}
			else
			{
				GD.PushWarning($"[mod-settings] {mod}:{ModSettingsUiPath} is not a JSON object; ignored");
			}
		}

		var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall install in ModSession.Resolve(Refii.Assets.Engine_GameAssets.ModsRoot).Installs)
		{
			titles[install.Name] = install.Metadata.Title.Length != 0 ? ModText.Game(install.Metadata.Title) : install.Name;
		}

		var pages = new List<Page>();
		foreach (string mod in order)
		{
			var rows = new List<Row>();
			described.TryGetValue(mod, out Godot.Collections.Dictionary? ui);
			foreach (string key in keysByMod[mod])
			{
				// A key a later mod restates belongs to that mod's page, not this one's.
				if (!defaults.TryGetValue(key, out float file) ||
					(ModSettings.Owners.TryGetValue(key, out string? owner) &&
					!string.Equals(owner, mod, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}

				string name = key.StartsWith(mod + ".", StringComparison.OrdinalIgnoreCase)
					? key[(mod.Length + 1)..]
					: key;
				Godot.Collections.Dictionary? spec = null;
				if (ui is not null &&
					(ui.TryGetValue(name, out Variant found) || ui.TryGetValue(key, out found)) &&
					found.VariantType == Variant.Type.Dictionary)
				{
					spec = found.AsGodotDictionary();
				}

				// OPEN ONLY WHAT THE AUTHOR DESCRIBED. A setting is the mod maker's tuning until
				// settings.ui.json offers it to the player, so a mod without that file has no page.
				if (spec is null)
				{
					continue;
				}

				rows.Add(Describe(mod, key, name, file, spec));
			}

			if (rows.Count != 0)
			{
				pages.Add(new Page(mod, titles.TryGetValue(mod, out string? title) ? title : mod, rows));
			}
		}

		return pages;

		void Claim(string mod, string key)
		{
			if (!keysByMod.TryGetValue(mod, out List<string>? keys))
			{
				keys = new List<string>();
				keysByMod[mod] = keys;
				order.Add(mod);
			}

			if (!keys.Contains(key))
			{
				keys.Add(key);
			}
		}
	}

	/// <summary>
	/// A row from its description, or from the file's value alone: a toggle when the value is 0
	/// or 1, else a range from 0 to twice the value (the other way for a negative one) in
	/// twentieths; a value of 0 steps 0..1 by 0.05.
	/// </summary>
	/// <param name="switches">The row is a game switch's (<see cref="SwitchPage"/>), whose choices may store
	/// text; a mod's setting holds a number, so its text options are skipped, as the validator says.</param>
	private static Row Describe(
		string mod, string key, string name, float file, Godot.Collections.Dictionary? spec, bool switches = false) =>
		DescribeRow(mod, key, name, file, spec, switches) with { Default = DefaultText(spec) };

	/// <summary>A row's <c>default</c> as text — what a page over switches reads when the key is unset.</summary>
	private static string DefaultText(Godot.Collections.Dictionary? spec)
	{
		if (spec is null || !spec.TryGetValue("default", out Variant value))
		{
			return string.Empty;
		}

		return value.VariantType switch
		{
			Variant.Type.Bool => value.AsBool() ? "true" : "false",
			Variant.Type.Float or Variant.Type.Int => value.AsDouble().ToString(CultureInfo.InvariantCulture),
			Variant.Type.String => value.AsString(),
			_ => string.Empty,
		};
	}

	private static Row DescribeRow(
		string mod, string key, string name, float file, Godot.Collections.Dictionary? spec, bool switches)
	{
		// A {KEY} in the label, the description or an option's label is the mod's text in the culture in force.
		string label = ModText.Game(Text(spec, "label", name.Replace('_', ' ')));
		string description = ModText.Game(Text(spec, "description", string.Empty));
		string type = Text(spec, "type", string.Empty).Trim().ToLowerInvariant();

		if (type == "choice" && ChoiceOptions(spec, switches) is { Count: > 0 } options)
		{
			float low = options.Min(o => o.Value);
			float high = options.Max(o => o.Value);
			return new Row(mod, key, label, Kind.Choice, low, high, 1f, description, options);
		}

		bool ranged = spec is not null && (spec.ContainsKey("min") || spec.ContainsKey("max") || spec.ContainsKey("step"));
		bool toggle = type == "toggle" || (type.Length == 0 && !ranged && (file == 0f || file == 1f));
		if (toggle)
		{
			return new Row(mod, key, label, Kind.Toggle, 0f, 1f, 1f, description);
		}

		float min = Number(spec, "min", MathF.Min(0f, 2f * file));
		float max = Number(spec, "max", MathF.Max(0f, 2f * file));
		if (max <= min)
		{
			max = min + (file != 0f ? MathF.Abs(file) * 2f : 1f);
		}

		float step = Number(spec, "step", file != 0f ? MathF.Abs(file) / 20f : 0.05f);
		if (step <= 0f)
		{
			step = (max - min) / 20f;
		}

		// The file's value must be reachable, or the page would move it on its first step.
		if (file < min)
		{
			min = file;
		}

		if (file > max)
		{
			max = file;
		}

		return new Row(mod, key, label, Kind.Slider, min, max, step, description);
	}

	/// <summary>
	/// A choice row's <c>options</c>: each <c>{ "value": number or text, "label": text, "aliases":
	/// [text…] }</c>, in the file's order. A text value is for a page over the game's config switches
	/// (<see cref="SwitchPage"/>); a mod's setting is a number, and the validator holds mods to that.
	/// An option with no value is skipped; one with no label is captioned by its value.
	/// </summary>
	private static List<Option> ChoiceOptions(Godot.Collections.Dictionary? spec, bool textValues)
	{
		var options = new List<Option>();
		if (spec is null || !spec.TryGetValue("options", out Variant list) || list.VariantType != Variant.Type.Array)
		{
			return options;
		}

		foreach (Variant item in list.AsGodotArray())
		{
			if (item.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			Godot.Collections.Dictionary option = item.AsGodotDictionary();
			if (!option.TryGetValue("value", out Variant value))
			{
				continue;
			}

			if (value.VariantType == Variant.Type.String && textValues)
			{
				var aliases = new List<string>();
				if (option.TryGetValue("aliases", out Variant names) && names.VariantType == Variant.Type.Array)
				{
					foreach (Variant alias in names.AsGodotArray())
					{
						if (alias.VariantType == Variant.Type.String)
						{
							aliases.Add(alias.AsString());
						}
					}
				}

				string text = value.AsString();
				options.Add(new Option(options.Count, ModText.Game(Text(option, "label", text)), text, aliases));
				continue;
			}

			if (value.VariantType is not (Variant.Type.Float or Variant.Type.Int or Variant.Type.Bool))
			{
				continue;
			}

			float number = value.VariantType == Variant.Type.Bool ? (value.AsBool() ? 1f : 0f) : (float)value.AsDouble();
			options.Add(new Option(number, ModText.Game(Text(option, "label", number.ToString(CultureInfo.InvariantCulture)))));
		}

		return options;
	}

	private static string Text(Godot.Collections.Dictionary? spec, string field, string fallback) =>
		spec is not null && spec.TryGetValue(field, out Variant value) && value.VariantType == Variant.Type.String
			? value.AsString()
			: fallback;

	private static float Number(Godot.Collections.Dictionary? spec, string field, float fallback) =>
		spec is not null && spec.TryGetValue(field, out Variant value) &&
		value.VariantType is Variant.Type.Float or Variant.Type.Int
			? (float)value.AsDouble()
			: fallback;
}
