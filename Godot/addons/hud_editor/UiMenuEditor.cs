#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.Assets.UI;
using Refii.Ui;

namespace Refii.EditorTools;

/// <summary>
/// Where a <see cref="UiMenuEditor"/>'s records come from and go: the player's
/// <c>user://ui/templates.json</c> (<see cref="PlayerMenuTarget"/>) or a mod's <c>ui/templates.json</c>
/// through the editor API (the Mod Studio's UI tab).
/// </summary>
public interface IUiMenuTarget
{
	/// <summary>Whose records these are, for the status line ("your templates", "mod 'x'").</summary>
	string Name { get; }

	/// <summary>The menus the editor lists.</summary>
	IReadOnlyList<string> MenuNames();

	/// <summary>One line on what editing <paramref name="menu"/> writes.</summary>
	string Describe(string menu);

	/// <summary>The record to start from — the menu as it composes now — or null for a name with none.</summary>
	JsonObject? Load(string menu);

	/// <summary>Writes the records the editor changed (whole, as edited); an error, or empty when written.</summary>
	string Save(IReadOnlyDictionary<string, JsonObject> changed);

	/// <summary>Whether the target can hold a menu of its own (<see cref="Create"/>).</summary>
	bool CanCreate { get; }

	/// <summary>Adds an empty menu of the target's own; an error, or empty.</summary>
	string Create(string menu);

	/// <summary>The label of the action that drops the target's statement of a menu, or null when it states none.</summary>
	string? DropLabel(string menu);

	/// <summary>Drops the target's statement of a menu (the record beneath stands again); an error, or empty.</summary>
	string Drop(string menu);
}

/// <summary>
/// THE MENU EDITOR — one control for every place <c>ui/templates.json</c> menus are edited: the Mod
/// Studio's UI tab and the player's menu dock. It edits a menu RECORD held as JSON exactly as read,
/// so every field round-trips — the row fields it shows (name, tag, the command with its
/// <c>Opens:</c> and <c>Mod:lua:</c> helpers, opens, source, icon, eventType, rating, hostOnly, drop,
/// counted), the menu's own (highlight, title, source, rowCommand) and anything else a record
/// carries. Only the records it changed are handed to the target. Structure edits and the row
/// rules are <see cref="Engine_UiMenuEdit"/>'s.
/// </summary>
[Tool]
public partial class UiMenuEditor : VBoxContainer
{
	/// <summary>A record changed; the owner's preview redraws it.</summary>
	public event Action<string, JsonObject>? Edited;

	/// <summary>The menu shown changed.</summary>
	public event Action<string>? MenuShown;

	/// <summary>A save or a drop wrote the target.</summary>
	public event Action? Saved;

	/// <summary>Whether the editor shows its own Save and Reload buttons (the player's dock); the Studio saves the tab.</summary>
	public bool Saves { get; init; }

	private IUiMenuTarget? _target;
	private readonly Dictionary<string, JsonObject> _loaded = new(StringComparer.Ordinal);
	private readonly Dictionary<string, JsonObject> _edited = new(StringComparer.Ordinal);
	private List<string> _names = new();
	private string? _menu;
	private int[]? _row;
	private bool _filling;

	private LineEdit _filter = null!;
	private OptionButton _menuChoice = null!;
	private Label _menuNote = null!;
	private Button _create = null!, _drop = null!;
	private LineEdit _highlight = null!, _title = null!, _menuSource = null!, _rowCommand = null!;
	private Tree _tree = null!;
	private LineEdit _name = null!, _tag = null!, _opens = null!, _source = null!, _icon = null!;
	private OptionButton _actionKind = null!;
	private LineEdit _opensMenu = null!, _luaFunction = null!, _luaArgument = null!, _rawCommand = null!;
	private MenuButton _pickMenu = null!, _pickOpens = null!;
	private Control _opensRow = null!, _luaRow = null!, _rawRow = null!;
	private Label _commandCheck = null!;
	private SpinBox _eventType = null!, _rating = null!;
	private CheckBox _hostOnly = null!, _dropRow = null!, _counted = null!;
	private VBoxContainer _rowFields = null!;
	private Control _rowCard = null!;
	private Label _status = null!;
	private ConfirmationDialog? _createDialog;
	private LineEdit? _createName;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 6);

		if (Saves)
		{
			var buttons = new HBoxContainer();
			var reload = new Button { Text = "🔄 Reload", TooltipText = "Read the menus again; unsaved edits are dropped" };
			reload.Pressed += Reload;
			buttons.AddChild(reload);
			var save = new Button { Text = "💾 Save", TooltipText = "Write the menus you changed; every other record is kept as it is" };
			save.Pressed += () => SetStatus(Save() is { Length: > 0 } error ? "⚠ " + error : "Saved.");
			buttons.AddChild(save);
			AddChild(buttons);
		}

		var pick = new HBoxContainer();
		_filter = new LineEdit { PlaceholderText = "Filter menus…", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(120, 0) };
		_filter.TextChanged += _ => ListMenus();
		pick.AddChild(_filter);
		_menuChoice = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
		_menuChoice.ItemSelected += _ => ShowMenu(_menuChoice.GetItemText(_menuChoice.Selected));
		pick.AddChild(_menuChoice);
		_create = new Button { Text = "+ Menu", TooltipText = "A new menu of this mod's own; a row opens it with \"opens\" or Opens:<menu>" };
		_create.Pressed += AskCreate;
		pick.AddChild(_create);
		_drop = new Button();
		ModStudioUiStyles.ApplyRedButtonStyle(_drop);
		_drop.Pressed += DropMenu;
		pick.AddChild(_drop);
		AddChild(pick);

		_menuNote = ModStudioUiStyles.Note(string.Empty);
		AddChild(_menuNote);

		var menuFields = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_highlight = MenuField(menuFields, "highlight", "Highlight", "The entry highlighted when the menu opens");
		_title = MenuField(menuFields, "title", "Title", "The heading over the list — a Babel tag or literal text");
		_menuSource = MenuField(menuFields, "source", "Source", "A menu source that fills the list's rows at runtime");
		_rowCommand = MenuField(menuFields, "rowCommand", "Row command", "The command every sourced row runs unless it names its own");
		AddChild(menuFields);

		var structure = new HFlowContainer();
		structure.AddThemeConstantOverride("h_separation", 4);
		AddButton(structure, "+ Row", "A row after the selected one", () => AddRow(child: false));
		AddButton(structure, "+ Child", "A row under the selected one", () => AddRow(child: true));
		AddButton(structure, "→ Nest", "Make the row a child of the row above it", () => Restructure(path => Engine_UiMenuEdit.Nest(Entries(), path)));
		AddButton(structure, "← Promote", "Move the row out to its parent's level", () => Restructure(path => Engine_UiMenuEdit.Promote(Entries(), path)));
		AddButton(structure, "↑", "Move up among its siblings", () => Restructure(path => Engine_UiMenuEdit.Move(Entries(), path, -1)));
		AddButton(structure, "↓", "Move down among its siblings", () => Restructure(path => Engine_UiMenuEdit.Move(Entries(), path, 1)));
		var remove = new Button { Text = "🗑 Row", TooltipText = "Remove the row and everything under it" };
		ModStudioUiStyles.ApplyRedButtonStyle(remove);
		remove.Pressed += RemoveRow;
		structure.AddChild(remove);
		AddChild(structure);

		_tree = new Tree
		{
			Columns = 3,
			ColumnTitlesVisible = true,
			HideRoot = true,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 160),
		};
		_tree.SetColumnTitle(0, "Name");
		_tree.SetColumnTitle(1, "Tag / Label");
		_tree.SetColumnTitle(2, "Does");
		_tree.ItemSelected += OnRowSelected;
		AddChild(_tree);

		AddChild(BuildRowFields());

		_status = ModStudioUiStyles.Note(string.Empty);
		AddChild(_status);
		Refresh();
	}

	private static void AddButton(Container into, string text, string tip, Action pressed)
	{
		var button = new Button { Text = text, TooltipText = tip };
		button.Pressed += pressed;
		into.AddChild(button);
	}

	private LineEdit MenuField(GridContainer grid, string key, string caption, string tip)
	{
		grid.AddChild(new Label { Text = caption, TooltipText = tip, MouseFilter = MouseFilterEnum.Pass });
		var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = tip };
		edit.TextChanged += text =>
		{
			if (!_filling && Current() is { } record)
			{
				record[key] = text;
				Touched();
			}
		};
		grid.AddChild(edit);
		return edit;
	}

	private Control BuildRowFields()
	{
		_rowFields = new VBoxContainer();
		_rowFields.AddThemeConstantOverride("separation", 4);

		_name = RowText("name", "Name", "The row's identity: what a dispatch recognises and what patches address. Unique in the menu.");
		_tag = RowText("tag", "Tag / label", "A Babel tag, or literal text shown as written");

		_actionKind = new OptionButton();
		_actionKind.AddItem("Nothing");
		_actionKind.AddItem("Opens a menu (Opens:)");
		_actionKind.AddItem("Calls a Lua function (Mod:lua:)");
		_actionKind.AddItem("Another command");
		_actionKind.ItemSelected += _ => { ShowActionRows(); WriteCommand(); };
		_rowFields.AddChild(ModStudioUiStyles.Field("Command", _actionKind, "What A on the row does"));

		_opensMenu = new LineEdit { PlaceholderText = "menu name" };
		_opensMenu.TextChanged += _ => WriteCommand();
		_pickMenu = MenuPicker(name => _opensMenu.Text = name);
		_opensRow = ModStudioUiStyles.Field("  menu", Join(_opensMenu, _pickMenu), "The menu Opens: shows over this list; B returns");
		_rowFields.AddChild(_opensRow);

		_luaFunction = new LineEdit { PlaceholderText = "my_mod.on_row" };
		_luaFunction.TextChanged += _ => WriteCommand();
		_luaArgument = new LineEdit { PlaceholderText = "argument (optional)" };
		_luaArgument.TextChanged += _ => WriteCommand();
		_luaRow = ModStudioUiStyles.Field("  function", Join(_luaFunction, _luaArgument),
			"A global function of the mod's Lua, or a dotted path through its tables; called as function(arg, e) in game");
		_rowFields.AddChild(_luaRow);

		_rawCommand = new LineEdit();
		_rawCommand.TextChanged += _ => WriteCommand();
		_rawRow = ModStudioUiStyles.Field("  command", _rawCommand, "A host action or the list's own command");
		_rowFields.AddChild(_rawRow);

		_commandCheck = ModStudioUiStyles.Note(string.Empty);
		_commandCheck.Modulate = ModStudioUiStyles.ErrorColour;
		_rowFields.AddChild(_commandCheck);

		_opens = new LineEdit();
		_opens.TextChanged += text => SetRow("opens", text);
		_pickOpens = MenuPicker(name => _opens.Text = name);
		_rowFields.AddChild(ModStudioUiStyles.Field("Opens", Join(_opens, _pickOpens),
			"A menu (or screen) the row opens; a fold's children come first"));

		_source = RowText("source", "Source", "A menu source that fills this row's children at runtime");
		_icon = RowText("icon", "Icon", "A .tex path under the game's art root");

		_eventType = new SpinBox { MinValue = 0, MaxValue = 100000, Step = 1, AllowGreater = true };
		_eventType.ValueChanged += value => SetRow("eventType", JsonValue.Create((int)value));
		_rating = new SpinBox { MinValue = 0, MaxValue = 64, Step = 1, AllowGreater = true };
		_rating.ValueChanged += value => SetRow("rating", JsonValue.Create((int)value));
		_rowFields.AddChild(ModStudioUiStyles.Field("Event / rating", Join(_eventType, _rating),
			$"g_Menu.EventConst the row pushes (301 submenu); the rating slide ({Engine_UiMenuConstants.EmptyRatingSlide} draws none)"));

		var flags = new HBoxContainer();
		_hostOnly = Flag(flags, "hostOnly", "Host only", false, "A row only Godot shows; the guest has no such row");
		_dropRow = Flag(flags, "drop", "Drop", false, "A row the guest spawns that this list does not show");
		_counted = Flag(flags, "counted", "Counted", true, "A category whose caption carries (N)");
		_rowFields.AddChild(flags);

		_rowCard = ModStudioUiStyles.Card("Row", new Color(0.95f, 0.80f, 0.45f), _rowFields);
		return _rowCard;
	}

	private static HBoxContainer Join(Control a, Control b)
	{
		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		a.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		row.AddChild(a);
		row.AddChild(b);
		return row;
	}

	private MenuButton MenuPicker(Action<string> chosen)
	{
		var button = new MenuButton { Text = "▾", TooltipText = "Pick a menu", Flat = false };
		button.AboutToPopup += () =>
		{
			PopupMenu popup = button.GetPopup();
			popup.Clear();
			foreach (string name in _names)
			{
				popup.AddItem(name);
			}
		};
		button.GetPopup().IndexPressed += index => chosen(button.GetPopup().GetItemText((int)index));
		return button;
	}

	private LineEdit RowText(string key, string caption, string tip)
	{
		var edit = new LineEdit();
		edit.TextChanged += text => SetRow(key, text);
		_rowFields.AddChild(ModStudioUiStyles.Field(caption, edit, tip));
		return edit;
	}

	private CheckBox Flag(HBoxContainer into, string key, string text, bool fallback, string tip)
	{
		var box = new CheckBox { Text = text, TooltipText = tip, ButtonPressed = fallback };
		box.Toggled += on => SetRow(key, JsonValue.Create(on));
		into.AddChild(box);
		return box;
	}

	// ---------------------------------------------------------------------------------------
	// Binding and state
	// ---------------------------------------------------------------------------------------

	/// <summary>Points the editor at a target and lists its menus; unsaved edits are dropped.</summary>
	public void Bind(IUiMenuTarget target)
	{
		_target = target;
		Reload();
	}

	/// <summary>Reads the target again; unsaved edits are dropped.</summary>
	public void Reload()
	{
		_loaded.Clear();
		_edited.Clear();
		Refresh();
	}

	private void Refresh()
	{
		if (!IsNodeReady())
		{
			return;
		}

		_names = _target?.MenuNames().ToList() ?? new List<string>();
		_create.Visible = _target?.CanCreate == true;
		ListMenus();
	}

	/// <summary>The records changed since they were read, by name.</summary>
	public Dictionary<string, JsonObject> ChangedRecords()
	{
		var changed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
		foreach (string name in Engine_UiMenuEdit.Changed(_loaded, _edited))
		{
			changed[name] = _edited[name];
		}

		return changed;
	}

	/// <summary>A record as edited, or as the target composes it when the editor has not opened it.</summary>
	public JsonObject? Record(string name) =>
		_edited.TryGetValue(name, out JsonObject? record) ? record : _target?.Load(name);

	/// <summary>The menu shown now, or null.</summary>
	public string? CurrentMenu => _menu;

	/// <summary>The menus the target lists.</summary>
	public IReadOnlyList<string> Names => _names;

	/// <summary>Hands the changed records to the target; an error, or empty when written (or nothing changed).</summary>
	public string Save()
	{
		if (_target is null)
		{
			return "nothing to save to";
		}

		Dictionary<string, JsonObject> changed = ChangedRecords();
		if (changed.Count == 0)
		{
			return string.Empty;
		}

		string error = _target.Save(changed);
		if (error.Length == 0)
		{
			foreach ((string name, JsonObject record) in changed)
			{
				_loaded[name] = (JsonObject)record.DeepClone();
			}

			SetStatus($"Saved {changed.Count} menu(s) to {_target.Name}.");
			Saved?.Invoke();
		}

		return error;
	}

	public void SetStatus(string text)
	{
		if (IsNodeReady())
		{
			_status.Text = text;
		}
	}

	private void ListMenus()
	{
		string filter = _filter.Text.Trim();
		_menuChoice.Clear();
		int select = -1;
		foreach (string name in _names)
		{
			if (filter.Length != 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			_menuChoice.AddItem(_edited.TryGetValue(name, out JsonObject? record) &&
				(!_loaded.TryGetValue(name, out JsonObject? loaded) || !Engine_UiMenuEdit.Same(loaded, record))
					? name + " •"
					: name);
			if (name == _menu)
			{
				select = _menuChoice.ItemCount - 1;
			}
		}

		if (_menuChoice.ItemCount == 0)
		{
			ShowMenu(null);
			return;
		}

		_menuChoice.Select(select < 0 ? 0 : select);
		ShowMenu(select < 0 ? MenuAt(0) : _menu);
	}

	private string MenuAt(int index) => _menuChoice.GetItemText(index).TrimEnd(' ', '•');

	/// <summary>Shows <paramref name="name"/>, reading it from the target the first time.</summary>
	public void ShowMenu(string? name)
	{
		name = name?.TrimEnd(' ', '•');
		_menu = name;
		_row = null;
		if (name is not null && !_edited.ContainsKey(name) && _target?.Load(name) is { } record)
		{
			_loaded[name] = (JsonObject)record.DeepClone();
			_edited[name] = record;
		}

		JsonObject? current = Current();
		_filling = true;
		_highlight.Text = current is null ? string.Empty : Engine_UiMenuEdit.Text(current, "highlight");
		_title.Text = current is null ? string.Empty : Engine_UiMenuEdit.Text(current, "title");
		_menuSource.Text = current is null ? string.Empty : Engine_UiMenuEdit.Text(current, "source");
		_rowCommand.Text = current is null ? string.Empty : Engine_UiMenuEdit.Text(current, "rowCommand");
		_filling = false;

		_menuNote.Text = name is null || _target is null ? "No menu." : _target.Describe(name);
		string? drop = name is null ? null : _target?.DropLabel(name);
		_drop.Visible = drop is not null;
		_drop.Text = drop ?? string.Empty;

		Render();
		if (name is not null)
		{
			MenuShown?.Invoke(name);
		}
	}

	private JsonObject? Current() => _menu is not null && _edited.TryGetValue(_menu, out JsonObject? record) ? record : null;

	private JsonArray Entries()
	{
		JsonObject current = Current()!;
		if (current[Engine_UiMenuEdit.EntriesKey] is not JsonArray entries)
		{
			entries = new JsonArray();
			current[Engine_UiMenuEdit.EntriesKey] = entries;
		}

		return entries;
	}

	// ---------------------------------------------------------------------------------------
	// The tree
	// ---------------------------------------------------------------------------------------

	private void Render()
	{
		_tree.Clear();
		TreeItem root = _tree.CreateItem();
		if (Current() is not null)
		{
			RenderList(root, Entries(), Array.Empty<int>());
		}

		ShowRow();
	}

	private void RenderList(TreeItem parent, JsonArray list, int[] path)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] is not JsonObject row)
			{
				continue;
			}

			int[] at = path.Append(i).ToArray();
			TreeItem item = _tree.CreateItem(parent);
			Paint(item, row);
			item.SetMetadata(0, string.Join('/', at));
			if (_row is not null && _row.SequenceEqual(at))
			{
				item.Select(0);
			}

			if (row[Engine_UiMenuEdit.ChildrenKey] is JsonArray children)
			{
				RenderList(item, children, at);
			}
		}
	}

	private static void Paint(TreeItem item, JsonObject row)
	{
		item.SetText(0, Engine_UiMenuEdit.Name(row));
		item.SetText(1, Engine_UiMenuEdit.Text(row, "tag"));
		string command = Engine_UiMenuEdit.Text(row, "command");
		string opens = Engine_UiMenuEdit.Text(row, "opens");
		item.SetText(2, command.Length != 0 ? command : opens.Length != 0 ? "opens " + opens : string.Empty);
		bool bad = Engine_UiMenuEdit.Check(command).Length != 0 || Engine_UiMenuEdit.Name(row).Length == 0;
		item.SetCustomColor(0, bad ? ModStudioUiStyles.ErrorColour : new Color(0.92f, 0.92f, 0.92f));
		if (Engine_UiMenuEdit.Flag(row, "drop", false))
		{
			item.SetCustomColor(1, new Color(0.6f, 0.6f, 0.6f));
		}
	}

	private void OnRowSelected()
	{
		if (_tree.GetSelected() is not { } item)
		{
			return;
		}

		_row = item.GetMetadata(0).AsString().Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
		ShowRow();
	}

	private JsonObject? SelectedRow() =>
		_row is not null && Current() is not null ? Engine_UiMenuEdit.RowAt(Entries(), _row) : null;

	private void ShowRow()
	{
		JsonObject? row = SelectedRow();
		_rowCard.Visible = row is not null;
		if (row is null)
		{
			return;
		}

		_filling = true;
		_name.Text = Engine_UiMenuEdit.Name(row);
		_tag.Text = Engine_UiMenuEdit.Text(row, "tag");
		_opens.Text = Engine_UiMenuEdit.Text(row, "opens");
		_source.Text = Engine_UiMenuEdit.Text(row, "source");
		_icon.Text = Engine_UiMenuEdit.Text(row, "icon");
		_eventType.Value = Engine_UiMenuEdit.Number(row, "eventType", 0);
		_rating.Value = Engine_UiMenuEdit.Number(row, "rating", Engine_UiMenuConstants.EmptyRatingSlide);
		_hostOnly.ButtonPressed = Engine_UiMenuEdit.Flag(row, "hostOnly", false);
		_dropRow.ButtonPressed = Engine_UiMenuEdit.Flag(row, "drop", false);
		_counted.ButtonPressed = Engine_UiMenuEdit.Flag(row, "counted", true);

		string command = Engine_UiMenuEdit.Text(row, "command");
		_opensMenu.Text = string.Empty;
		_luaFunction.Text = string.Empty;
		_luaArgument.Text = string.Empty;
		_rawCommand.Text = string.Empty;
		switch (Engine_UiMenuEdit.KindOf(command))
		{
			case Engine_UiMenuEdit.CommandKind.Opens:
				_actionKind.Select(1);
				_opensMenu.Text = command[Engine_UiModMenuCommand.OpensPrefix.Length..];
				break;
			case Engine_UiMenuEdit.CommandKind.Lua:
				_actionKind.Select(2);
				string body = command[Engine_UiModMenuCommand.LuaPrefix.Length..];
				int colon = body.IndexOf(':');
				_luaFunction.Text = colon < 0 ? body : body[..colon];
				_luaArgument.Text = colon < 0 ? string.Empty : body[(colon + 1)..];
				break;
			case Engine_UiMenuEdit.CommandKind.Other:
				_actionKind.Select(3);
				_rawCommand.Text = command;
				break;
			default:
				_actionKind.Select(0);
				break;
		}

		_filling = false;
		ShowActionRows();
		_commandCheck.Text = Engine_UiMenuEdit.Check(command);
	}

	private void ShowActionRows()
	{
		_opensRow.Visible = _actionKind.Selected == 1;
		_luaRow.Visible = _actionKind.Selected == 2;
		_rawRow.Visible = _actionKind.Selected == 3;
	}

	private void WriteCommand()
	{
		if (_filling)
		{
			return;
		}

		string? command = _actionKind.Selected switch
		{
			1 => Engine_UiModMenuCommand.OpensPrefix + _opensMenu.Text.Trim(),
			2 => Engine_UiModMenuCommand.LuaPrefix + _luaFunction.Text.Trim() +
				(_luaArgument.Text.Length == 0 ? string.Empty : ":" + _luaArgument.Text),
			3 => _rawCommand.Text,
			_ => null,
		};
		_commandCheck.Text = Engine_UiMenuEdit.Check(command);
		SetRow("command", command is null ? null : JsonValue.Create(command));
	}

	private void SetRow(string key, string text) => SetRow(key, JsonValue.Create(text));

	private void SetRow(string key, JsonNode? value)
	{
		if (_filling || SelectedRow() is not { } row)
		{
			return;
		}

		Engine_UiMenuEdit.SetField(row, key, value);
		if (_tree.GetSelected() is { } item)
		{
			Paint(item, row);
		}

		if (key == Engine_UiMenuEdit.NameKey)
		{
			string name = Engine_UiMenuEdit.Name(row);
			SetStatus(name.Length == 0 ? "⚠ A row needs a name." :
				Engine_UiMenuEdit.NameTaken(Entries(), name, row) ? $"⚠ Another row is already named '{name}' — patches address rows by name." :
				string.Empty);
		}

		Touched();
	}

	private void AddRow(bool child)
	{
		if (Current() is null)
		{
			return;
		}

		string name = "NewRow";
		for (int n = 2; Engine_UiMenuEdit.NameTaken(Entries(), name); n++)
		{
			name = "NewRow" + n.ToString(CultureInfo.InvariantCulture);
		}

		_row = Engine_UiMenuEdit.Add(Entries(), _row, new JsonObject { ["name"] = name, ["tag"] = "New row" }, child);
		Render();
		Touched();
	}

	private void Restructure(Func<int[], int[]?> operation)
	{
		if (_row is null || Current() is null)
		{
			return;
		}

		if (operation(_row) is not { } moved)
		{
			SetStatus("That row cannot move that way.");
			return;
		}

		_row = moved;
		Render();
		Touched();
	}

	private void RemoveRow()
	{
		if (_row is null || Current() is null || !Engine_UiMenuEdit.Remove(Entries(), _row))
		{
			return;
		}

		_row = null;
		Render();
		Touched();
	}

	private void Touched()
	{
		if (_menu is null || Current() is not { } record)
		{
			return;
		}

		int changed = Engine_UiMenuEdit.Changed(_loaded, _edited).Count;
		SetStatus(changed == 0 ? "No unsaved changes." : $"Unsaved: {changed} menu(s) changed.");
		Edited?.Invoke(_menu, record);
	}

	// ---------------------------------------------------------------------------------------
	// Menus of the target's own
	// ---------------------------------------------------------------------------------------

	private void AskCreate()
	{
		if (_target is null)
		{
			return;
		}

		if (_createDialog is null)
		{
			_createDialog = new ConfirmationDialog { Title = "New menu", OkButtonText = "Create" };
			_createName = new LineEdit { PlaceholderText = "my_mod.menu" };
			_createDialog.AddChild(_createName);
			_createDialog.RegisterTextEnter(_createName);
			_createDialog.Confirmed += () =>
			{
				string name = _createName.Text.Trim();
				if (name.Length == 0 || _target is null)
				{
					return;
				}

				string error = _target.Create(name);
				SetStatus(error.Length == 0 ? $"Menu '{name}' created." : "⚠ " + error);
				if (error.Length == 0)
				{
					_menu = name;
					Refresh();
					Saved?.Invoke();
				}
			};
			AddChild(_createDialog);
		}

		_createName!.Text = string.Empty;
		_createDialog.PopupCentered(new Vector2I(320, 90));
		_createName.GrabFocus();
	}

	private void DropMenu()
	{
		if (_target is null || _menu is null)
		{
			return;
		}

		string name = _menu;
		string error = _target.Drop(name);
		SetStatus(error.Length == 0 ? $"'{name}': {_target.DropLabel(name) ?? "dropped"} done." : "⚠ " + error);
		if (error.Length == 0)
		{
			_loaded.Remove(name);
			_edited.Remove(name);
			Refresh();
			Saved?.Invoke();
		}
	}
}

/// <summary>
/// The player's menus: <c>user://ui/templates.json</c> (<see cref="Engine_UiTemplates.OverridePath"/>)
/// over the records the game last snapshotted (<see cref="Engine_UiTemplates.SnapshotPath"/>). A
/// save writes only the menus the editor changed, whole, keeping every other record and section of
/// the file.
/// </summary>
public sealed class PlayerMenuTarget : IUiMenuTarget
{
	private JsonObject _snapshot = new();
	private JsonObject _file = new();

	public PlayerMenuTarget() => Read();

	private void Read()
	{
		_snapshot = Engine_HudLayoutEdit.ParseObject(PlayerHudLayoutTarget.ReadText(Engine_UiTemplates.SnapshotPath));
		_file = Engine_HudLayoutEdit.ParseObject(PlayerHudLayoutTarget.ReadText(Engine_UiTemplates.OverridePath));
	}

	public string Name => Engine_UiTemplates.OverridePath;

	public IReadOnlyList<string> MenuNames()
	{
		Read();
		return Menus(_snapshot).Concat(Menus(_file)).Distinct(StringComparer.Ordinal)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static IEnumerable<string> Menus(JsonObject root) =>
		root["menus"] is JsonObject menus ? menus.Select(pair => pair.Key) : Enumerable.Empty<string>();

	public string Describe(string menu) => Record(_file, menu) is not null
		? "Your file states this menu whole; the game reads it over its own on boot."
		: "As the game last ran it. Saving writes this menu whole into your file.";

	public JsonObject? Load(string menu) =>
		(Record(_file, menu) ?? Record(_snapshot, menu))?.DeepClone() as JsonObject;

	private static JsonObject? Record(JsonObject root, string menu) =>
		root["menus"] is JsonObject menus && menus[menu] is JsonObject record ? record : null;

	public string Save(IReadOnlyDictionary<string, JsonObject> changed)
	{
		string text = Engine_UiMenuEdit.ApplyMenus(PlayerHudLayoutTarget.ReadText(Engine_UiTemplates.OverridePath), changed);
		return Write(text);
	}

	public bool CanCreate => false;

	public string Create(string menu) => "your file only restates the game's menus";

	public string? DropLabel(string menu) => Record(_file, menu) is not null ? "↺ Revert to the game's" : null;

	public string Drop(string menu)
	{
		JsonObject root = Engine_HudLayoutEdit.ParseObject(PlayerHudLayoutTarget.ReadText(Engine_UiTemplates.OverridePath));
		if (root["menus"] is JsonObject menus)
		{
			menus.Remove(menu);
		}

		return Write(root.ToJsonString(Engine_HudLayoutEdit.Indented) + "\n");
	}

	private string Write(string text)
	{
		DirAccess.MakeDirRecursiveAbsolute("user://ui");
		using FileAccess? file = FileAccess.Open(Engine_UiTemplates.OverridePath, FileAccess.ModeFlags.Write);
		if (file is null)
		{
			return $"cannot write {Engine_UiTemplates.OverridePath}: {FileAccess.GetOpenError()}";
		}

		file.StoreString(text);
		Read();
		return string.Empty;
	}
}
#endif
