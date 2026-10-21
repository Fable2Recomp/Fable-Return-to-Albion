using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Ui;

namespace Refii.Mods;

/// <summary>
/// A mod's settings as an options page the player tunes in the game, instead of editing
/// <c>settings.json</c>: every enabled mod that ships settings gets a row under the options
/// screen's Mods fold, and the row opens a host options page (<see cref="Engine_UiHostOptionsPage"/>)
/// with one value row per setting. Left and right step the value, applied live and saved at
/// once (<see cref="ModSettings.Persist"/>); A and B leave the page.
/// </summary>
/// <remarks>
/// <para>A mod describes its rows in an optional <c>settings.ui.json</c> beside
/// <c>settings.json</c>, keyed by the setting's name without the <c>&lt;mod&gt;.</c> prefix:</para>
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
/// <para>A setting the file does not describe still gets a row: its name as the label, a range
/// around the file's value (0 to twice it, in twentieths), and a toggle when the value is 0 or
/// 1. A mod author gets a page for free and refines it when they want to.</para>
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

	/// <summary>One labelled value of a <see cref="Kind.Choice"/> row.</summary>
	public readonly record struct Option(float Value, string Label);

	/// <summary>
	/// One value row: the setting, how it is shown and the range it steps over — or, for a
	/// <see cref="Kind.Choice"/> row, the <see cref="Options"/> it steps through.
	/// </summary>
	public sealed record Row(
		string Mod, string Key, string Label, Kind Kind, float Min, float Max, float Step, string Description,
		IReadOnlyList<Option>? Options = null)
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

		/// <summary>The caption at an index: On/Off, the option's label, or the number at the step's precision.</summary>
		public string Caption(int index)
		{
			if (Kind == Kind.Toggle)
			{
				return index != 0 ? "On" : "Off";
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

	private static List<Page>? _pages;

	private static IReadOnlyDictionary<string, float>? _builtFor;

	/// <summary>Every enabled mod's page, in load order; rebuilt when the settings are reloaded.</summary>
	public static IReadOnlyList<Page> Pages
	{
		get
		{
			IReadOnlyDictionary<string, float> defaults = ModSettings.Defaults;
			if (_pages is null || !ReferenceEquals(_builtFor, defaults))
			{
				_pages = Build(defaults);
				_builtFor = defaults;
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
				Command: OpenCommandPrefix + page.Mod));
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
		foreach (ModInstall install in ModWorkspace.DiscoverModInstalls(Refii.Assets.Engine_GameAssets.ModsRoot))
		{
			titles[install.Name] = install.Metadata.Title.Length != 0 ? install.Metadata.Title : install.Name;
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
	private static Row Describe(string mod, string key, string name, float file, Godot.Collections.Dictionary? spec)
	{
		string label = Text(spec, "label", name.Replace('_', ' '));
		string description = Text(spec, "description", string.Empty);
		string type = Text(spec, "type", string.Empty).Trim().ToLowerInvariant();

		if (type == "choice" && ChoiceOptions(spec) is { Count: > 0 } options)
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
	/// A choice row's <c>options</c>: each <c>{ "value": number, "label": text }</c>, in the file's
	/// order. An option with no number is skipped; one with no label is captioned by its number.
	/// </summary>
	private static List<Option> ChoiceOptions(Godot.Collections.Dictionary? spec)
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
			if (!option.TryGetValue("value", out Variant value) ||
				value.VariantType is not (Variant.Type.Float or Variant.Type.Int or Variant.Type.Bool))
			{
				continue;
			}

			float number = value.VariantType == Variant.Type.Bool ? (value.AsBool() ? 1f : 0f) : (float)value.AsDouble();
			options.Add(new Option(number, Text(option, "label", number.ToString(CultureInfo.InvariantCulture))));
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
