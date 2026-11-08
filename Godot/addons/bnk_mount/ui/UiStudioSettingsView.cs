#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.Mods;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The UI tab's Settings page: the rows of the mod's options page (<c>settings.ui.json</c>, read by
/// <see cref="ModSettingsUi"/>) — label, description and type for each setting in
/// <c>settings.json</c>, a slider's range, a choice's options — and the page itself in the preview:
/// the game's options book (<c>optionsscreen</c>, built by <see cref="Engine_UiBookSet"/> on the
/// preview's host) with the edited file's page painted on it by the game's own
/// <see cref="Engine_UiHostOptionsPage.Paint(System.Func{int, int}, bool, int)"/>.
/// </summary>
/// <remarks>
/// The page is <see cref="ModSettingsUi.PreviewPage"/> over the files as edited, not the enabled
/// mods' composition; the book, its rows, captions, window, cursor and prompts are what the options
/// screen shows in game. ◀ ▶ under the window step the preview's indices only.
/// </remarks>
[Tool]
public partial class UiStudioSettingsView : VBoxContainer
{
	private static readonly string[] Types = { "slider", "toggle", "choice" };

	private readonly UiStudioDocs _docs;
	private readonly UiStudioPreview _preview;

	public event Action? Edited;

	private ItemList _keys = null!;
	private UiStudioFields _fields = null!;
	private VBoxContainer _options = null!;
	private Control _optionsCard = null!;
	private Button _describe = null!, _drop = null!;
	private Label _detail = null!;
	private bool _filling;
	private readonly Dictionary<int, int> _previewIndex = new();

	/// <summary>The page is the tab's: its book stands on the preview's host.</summary>
	private bool _active;

	/// <summary>The game's options book on the preview's host, once built.</summary>
	private Engine_UiOwnedBook? _book;

	/// <summary>The row the preview's cursor is on.</summary>
	private int _cursor;

	/// <summary>The stepping controls under the window.</summary>
	private VBoxContainer? _controls;

	/// <param name="preview">The tab's preview window; the options book stands on its host.</param>
	public UiStudioSettingsView(UiStudioDocs docs, UiStudioPreview preview)
	{
		_docs = docs;
		_preview = preview;
		_preview.HostLoaded += () =>
		{
			if (_active)
			{
				ShowPage();
			}
		};
	}

	/// <summary>The page is up: the options book stands in the preview.</summary>
	public void Activate()
	{
		_active = true;
		_preview.EnsureHost();
		_controls = new VBoxContainer();
		_controls.AddThemeConstantOverride("separation", 4);
		_preview.Below.AddChild(_controls);
		_preview.Wheel += OnWheel;
		ShowPage();
	}

	public void Deactivate()
	{
		_active = false;
		_preview.Wheel -= OnWheel;
		FreeBook();
		foreach (Node child in _preview.Toolbar.GetChildren().Concat(_preview.Below.GetChildren()))
		{
			child.QueueFree();
		}

		_controls = null;
	}

	private void FreeBook()
	{
		if (_book is not null && GodotObject.IsInstanceValid(_book))
		{
			_book.QueueFree();
		}

		_book = null;
	}

	private void OnWheel(int direction)
	{
		_cursor += direction;
		ShowPage();
	}

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 8);
		AddChild(ModStudioUiStyles.Note("settings.ui.json offers settings to the player as rows on the mod's options page, under " +
			"Options › Mods — keyed by the setting's name without the \"<mod>.\" prefix. Only described settings are shown; " +
			"the rest stay your tuning, and a mod with no settings.ui.json has no page."));
		_keys = new ItemList { CustomMinimumSize = new Vector2(0, 140), AutoHeight = true };
		_keys.ItemSelected += _ => ShowKey();
		AddChild(_keys);

		var actions = new HBoxContainer();
		_describe = new Button { Text = "✎ Describe this row" };
		_describe.Pressed += Describe;
		actions.AddChild(_describe);
		_drop = new Button { Text = "↺ Drop the description" };
		ModStudioUiStyles.ApplyRedButtonStyle(_drop);
		_drop.Pressed += Drop;
		actions.AddChild(_drop);
		AddChild(actions);
		_detail = ModStudioUiStyles.Note(string.Empty);
		AddChild(_detail);

		var fieldBox = new VBoxContainer();
		AddChild(fieldBox);
		_fields = new UiStudioFields(fieldBox);
		_fields.Add("label", "Label", UiStudioFields.Type.Text, "The row's caption on the page");
		_fields.Add("description", "Description", UiStudioFields.Type.Text, "One line on what the setting does");
		_fields.Add("type", "Type", UiStudioFields.Type.Choice, "slider: a range; toggle: on/off; choice: a list of labelled values", choices: Types);
		_fields.Add("min", "Min", UiStudioFields.Type.Number, "The slider's lowest value", new[] { "slider", string.Empty });
		_fields.Add("max", "Max", UiStudioFields.Type.Number, "The slider's highest value", new[] { "slider", string.Empty });
		_fields.Add("step", "Step", UiStudioFields.Type.Number, "How far left and right move it", new[] { "slider", string.Empty });
		_fields.Changed += key =>
		{
			if (key == "type")
			{
				ShowKey();
			}

			Changed();
		};

		var optionsBody = new VBoxContainer();
		_options = new VBoxContainer();
		optionsBody.AddChild(_options);
		var addOption = new Button { Text = "+ Option" };
		addOption.Pressed += AddOption;
		optionsBody.AddChild(addOption);
		_optionsCard = ModStudioUiStyles.Card("Options (stepped through in this order)", new Color(0.85f, 0.65f, 1.0f), optionsBody);
		AddChild(_optionsCard);
	}

	/// <summary>A settings.json's numbers by key; nothing for a mod that ships none.</summary>
	private static Dictionary<string, float> Parse(string mod, string text) =>
		string.IsNullOrWhiteSpace(text) ? new Dictionary<string, float>() : ModSettings.Parse(mod, text);

	/// <summary>The setting names: settings.json's (prefix stripped), then any the description names alone.</summary>
	private List<string> Keys()
	{
		var keys = new List<string>();
		foreach (string key in Parse(_docs.Mod, _docs.SettingsText).Keys)
		{
			keys.Add(key.StartsWith(_docs.Mod + ".", StringComparison.OrdinalIgnoreCase) ? key[(_docs.Mod.Length + 1)..] : key);
		}

		keys.AddRange(_docs.SettingsUi.Select(p => p.Key).Where(k => !keys.Contains(k)));
		return keys;
	}

	public void Reload()
	{
		if (!IsNodeReady())
		{
			return;
		}

		Fill(null);
	}

	private void Fill(string? select)
	{
		select ??= Selected();
		_filling = true;
		_keys.Clear();
		foreach (string key in Keys())
		{
			int at = _keys.AddItem((_docs.SettingsUi.ContainsKey(key) ? "● " : "    ") + key);
			_keys.SetItemMetadata(at, key);
			if (key == select)
			{
				_keys.Select(at);
			}
		}

		_filling = false;
		ShowKey();
		ShowPage();
	}

	private string? Selected() =>
		_keys.GetSelectedItems() is { Length: > 0 } selected ? _keys.GetItemMetadata(selected[0]).AsString() : null;

	private void ShowKey()
	{
		if (_filling)
		{
			return;
		}

		string? key = Selected();
		JsonObject? row = key is null ? null : _docs.SettingsUi[key] as JsonObject;
		string type = row is null ? string.Empty : Engine_UiMenuEdit.Text(row, "type");
		_fields.Bind(row, type);
		_describe.Disabled = key is null || row is not null;
		_drop.Disabled = row is null;
		_optionsCard.Visible = row is not null && type == "choice";
		bool inFile = key is not null && Parse(_docs.Mod, _docs.SettingsText).Keys
			.Any(k => k == key || k == _docs.Mod + "." + key);
		_detail.Text = key is null ? "Select a setting." :
			!inFile ? $"'{key}' has no value in settings.json — the page shows no row for it until it does." :
			row is null ? $"'{key}' is not described, so players do not see it — describe it to offer it on the page." :
			$"'{key}' as the page shows it.";
		FillOptions(row);
	}

	private void Describe()
	{
		if (Selected() is not { } key)
		{
			return;
		}

		_docs.SettingsUi[key] = new JsonObject { ["label"] = key.Replace('_', ' ') };
		Fill(key);
		Changed();
	}

	private void Drop()
	{
		if (Selected() is not { } key)
		{
			return;
		}

		_docs.SettingsUi.Remove(key);
		Fill(key);
		Changed();
	}

	// ---- choice options ----------------------------------------------------------------------

	private JsonArray Options(JsonObject row)
	{
		if (row["options"] is not JsonArray options)
		{
			options = new JsonArray();
			row["options"] = options;
		}

		return options;
	}

	private void FillOptions(JsonObject? row)
	{
		foreach (Node child in _options.GetChildren())
		{
			child.QueueFree();
		}

		if (row is null || Engine_UiMenuEdit.Text(row, "type") != "choice")
		{
			return;
		}

		JsonArray options = Options(row);
		for (int i = 0; i < options.Count; i++)
		{
			if (options[i] is not JsonObject option)
			{
				continue;
			}

			var line = new HBoxContainer();
			var value = new SpinBox { MinValue = -100000, MaxValue = 100000, Step = 0.01, Value = Engine_UiMenuEdit.Number(option, "value", 0), AllowGreater = true, AllowLesser = true };
			value.ValueChanged += v =>
			{
				option["value"] = v;
				Changed();
			};
			var label = new LineEdit { Text = Engine_UiMenuEdit.Text(option, "label"), PlaceholderText = "label", SizeFlagsHorizontal = SizeFlags.ExpandFill };
			label.TextChanged += text =>
			{
				option["label"] = text;
				Changed();
			};
			var remove = new Button { Text = "✕" };
			remove.Pressed += () =>
			{
				options.Remove(option);
				FillOptions(row);
				Changed();
			};
			line.AddChild(value);
			line.AddChild(label);
			line.AddChild(remove);
			_options.AddChild(line);
		}
	}

	private void AddOption()
	{
		if (Selected() is not { } key || _docs.SettingsUi[key] is not JsonObject row)
		{
			return;
		}

		JsonArray options = Options(row);
		double next = options.OfType<JsonObject>().Select(o => Engine_UiMenuEdit.Number(o, "value", 0)).DefaultIfEmpty(-1).Max() + 1;
		options.Add(new JsonObject { ["value"] = next, ["label"] = "Option " + next.ToString(CultureInfo.InvariantCulture) });
		FillOptions(row);
		Changed();
	}

	private void Changed()
	{
		ShowPage();
		Edited?.Invoke();
	}

	// ---- the page preview ---------------------------------------------------------------------

	/// <summary>
	/// The page as the options screen shows it, on the game's options book in the preview window;
	/// under the window, each row's label and caption with ◀ ▶ to step it.
	/// </summary>
	private void ShowPage()
	{
		if (!_active || _controls is null || !GodotObject.IsInstanceValid(_controls))
		{
			return;
		}

		foreach (Node child in _controls.GetChildren())
		{
			child.QueueFree();
		}

		if (_docs.Mod.Length == 0 || string.IsNullOrWhiteSpace(_docs.SettingsText))
		{
			HideBook();
			_controls.AddChild(ModStudioUiStyles.Note("The mod ships no settings.json, so it has no options page."));
			return;
		}

		string title = ModWorkspace.DiscoverModInstalls(Refii.Assets.Engine_GameAssets.ModsRoot)
			.FirstOrDefault(m => string.Equals(m.Name, _docs.Mod, StringComparison.OrdinalIgnoreCase)) is { } install &&
			install.Metadata.Title.Length != 0 ? ModText.TitleOf(install) : _docs.Mod;
		(ModSettingsUi.Page page, IReadOnlyList<float> values) =
			ModSettingsUi.PreviewPage(_docs.Mod, title, _docs.SettingsText, UiStudioDocs.Text(_docs.SettingsUi));
		if (page.Count == 0)
		{
			HideBook();
			_controls.AddChild(ModStudioUiStyles.Note("settings.json states no numbers, so the mod has no page."));
			return;
		}

		_cursor = Math.Clamp(_cursor, 0, page.Count - 1);
		int Index(int row) => _previewIndex.TryGetValue(row, out int stepped)
			? Math.Clamp(stepped, 0, page.Choices(row) - 1)
			: page.Settings[row].IndexOf(values[row]);

		for (int i = 0; i < page.Count; i++)
		{
			int row = i;
			ModSettingsUi.Row setting = page.Settings[row];
			int index = Index(row);
			var line = new HBoxContainer();
			var name = new Button
			{
				Text = (row == _cursor ? "▸ " : "   ") + page.RowTitle(row),
				Flat = true,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				Alignment = HorizontalAlignment.Left,
				TooltipText = setting.Description.Length != 0 ? setting.Description : "Put the page's cursor on this row",
			};
			name.Pressed += () =>
			{
				_cursor = row;
				ShowPage();
			};
			var left = new Button { Text = "◀", Disabled = index <= 0 };
			var caption = new Label { Text = page.Caption(row, index), CustomMinimumSize = new Vector2(120, 0), HorizontalAlignment = HorizontalAlignment.Center };
			var right = new Button { Text = "▶", Disabled = index >= page.Choices(row) - 1 };
			left.Pressed += () => Step(row, index - 1);
			right.Pressed += () => Step(row, index + 1);
			line.AddChild(name);
			line.AddChild(left);
			line.AddChild(caption);
			line.AddChild(right);
			_controls.AddChild(line);
		}

		_controls.AddChild(ModStudioUiStyles.Note("The page above is the game's options book with this file's page painted on it — " +
			"the same rows, captions, window and cursor as Options › Mods › " + page.Title + ", each starting at settings.json's value. " +
			"◀ ▶ step the preview only; the wheel moves the cursor."));
		PaintBook(page, Index);
	}

	/// <summary>Builds the options book on the preview's host once, then paints the page on it.</summary>
	private void PaintBook(ModSettingsUi.Page page, Func<int, int> index)
	{
		if (!_preview.Loaded || _preview.Host is not { } host)
		{
			_preview.SetStatus("Loading the game's UI…");
			return;
		}

		const uint screen = Engine_UiDetailPages.OptionsScreenId;
		if (_book is null || !GodotObject.IsInstanceValid(_book))
		{
			string presentation = Engine_UiTemplates.Screen(screen)?.Presentation ?? "optionsscreen";
			_book = new Engine_UiBookSet { Host = host }.Ensure(screen, presentation);
			if (_book is null)
			{
				_preview.SetStatus($"'{presentation}' is not in the banks; the options book cannot be shown.");
				return;
			}
		}

		// The options screen's own page switches and placements with no list row picked (Page1,
		// its contents on the leaf), as the game stands them, then the host page over them.
		_book.HostActive = Engine_UiDetailPages.Active(screen, null);
		_book.HostOpacity = Engine_UiDetailPages.Opacities(screen, null);
		_book.HostPositions = Engine_UiDetailPages.Positions(screen, default, null);
		_book.HostRotations = Engine_UiDetailPages.Rotations(screen, null);
		Engine_UiHostOptionsPage.Paint(_book, page.Paint(index, open: true, cursor: _cursor));
		_book.Visible = true;
		_book.SetLive(null, null, null);
		_preview.SetStatus($"'{page.Title}' on the game's options book — {page.Count} row(s).");
	}

	private void HideBook()
	{
		if (_book is not null && GodotObject.IsInstanceValid(_book))
		{
			_book.Visible = false;
		}
	}

	private void Step(int row, int index)
	{
		_previewIndex[row] = index;
		_cursor = row;
		ShowPage();
	}

	/// <summary>The validator's issues for the two files, and settings other enabled mods also state.</summary>
	public IEnumerable<(bool Error, string Text)> Issues()
	{
		foreach (ModIssue issue in ModValidate.Validate(ModSettingsUi.ModSettingsUiPath, UiStudioDocs.Text(_docs.SettingsUi))
			.Concat(ModValidate.Validate(ModSettings.ModSettingsPath, _docs.SettingsText)))
		{
			yield return (issue.Error, issue.ToString());
		}

		var keys = Parse(_docs.Mod, _docs.SettingsText).Keys.ToHashSet(StringComparer.Ordinal);
		foreach (string key in _docs.SettingsUi.Select(p => p.Key))
		{
			if (!keys.Contains(key) && !keys.Contains(_docs.Mod + "." + key))
			{
				yield return (false, $"settings.ui.json: '{key}' has no value in settings.json; the page shows no row for it");
			}
		}

		foreach ((string mod, string text) in _docs.Files(ModSettings.ModSettingsPath, _docs.SettingsText)
			.Where(f => !string.Equals(f.Mod, _docs.Mod, StringComparison.OrdinalIgnoreCase)))
		{
			foreach (string shared in Parse(mod, text).Keys.Where(keys.Contains))
			{
				yield return (false, $"settings.json: '{shared}' is also stated by mod '{mod}'; the higher one in the load order owns it");
			}
		}
	}
}
#endif
