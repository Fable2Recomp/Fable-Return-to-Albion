#if TOOLS
using Godot;

namespace Refii.EditorTools;

/// <summary>
/// Edits the `menus` section of `user://ui/templates.snapshot.json` + `user://ui/templates.json`,
/// writing whole records to the override file and preserving its `screens` section. Name is the
/// guest dispatch identity; Tag is the cosmetic label (a literal renders verbatim without Babel).
/// </summary>
public sealed partial class MenuEditorDock : VBoxContainer
{
	private const string SnapshotPath = "user://ui/templates.snapshot.json";
	private const string OverridePath = "user://ui/templates.json";

	private OptionButton? _menuChoice;
	private Tree? _entries;
	private Label? _status;

	/// <summary>Every menu as loaded, name → entries array (Godot JSON shapes).</summary>
	private readonly Godot.Collections.Dictionary _menus = new();

	private readonly Godot.Collections.Dictionary _highlights = new();

	public override void _Ready()
	{
		Name = "UI Menus";

		var buttons = new HBoxContainer();
		var reload = new Button { Text = "Reload" };
		reload.Pressed += Reload;
		buttons.AddChild(reload);

		var addEntry = new Button { Text = "Add Entry" };
		addEntry.Pressed += () => AddEntry(child: false);
		buttons.AddChild(addEntry);

		var addChild = new Button { Text = "Add Child" };
		addChild.Pressed += () => AddEntry(child: true);
		buttons.AddChild(addChild);

		var indent = new Button { Text = "→ Nest", TooltipText =
			"Make this row a child of the row above it" };
		indent.Pressed += Indent;
		buttons.AddChild(indent);

		var outdent = new Button { Text = "← Promote", TooltipText =
			"Move this row out to its parent's level" };
		outdent.Pressed += Outdent;
		buttons.AddChild(outdent);

		var up = new Button { Text = "↑" };
		up.Pressed += () => Move(down: false);
		buttons.AddChild(up);

		var down = new Button { Text = "↓" };
		down.Pressed += () => Move(down: true);
		buttons.AddChild(down);

		var remove = new Button { Text = "Remove" };
		remove.Pressed += RemoveSelected;
		buttons.AddChild(remove);

		var save = new Button { Text = "Save" };
		save.Pressed += Save;
		buttons.AddChild(save);
		AddChild(buttons);

		_menuChoice = new OptionButton();
		_menuChoice.ItemSelected += _ => ShowSelected();
		AddChild(_menuChoice);

		_entries = new Tree
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			Columns = 5,
			ColumnTitlesVisible = true,
			HideRoot = true,
		};
		_entries.SetColumnTitle(0, "Name");
		_entries.SetColumnTitle(1, "Tag / Label");
		_entries.SetColumnTitle(2, "Command / Opens");
		_entries.SetColumnTitle(3, "Source");
		_entries.SetColumnTitle(4, "Icon");
		AddChild(_entries);

		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddChild(_status);

		Reload();
	}

	private void Reload()
	{
		_menus.Clear();
		_highlights.Clear();
		_menuChoice?.Clear();

		// Snapshot first, override on top: the game's read order.
		TakeMenus(SnapshotPath);
		TakeMenus(OverridePath);

		foreach (var entry in _menus)
		{
			_menuChoice?.AddItem(entry.Key.AsString());
		}

		if (_menus.Count == 0)
		{
			SetStatus($"No menus at {SnapshotPath}.\nRun the game once — it snapshots every " +
				"template on boot.");
			return;
		}

		SetStatus($"{_menus.Count} menu(s). Edit cells by double-click; Save writes " +
			OverridePath);
		ShowSelected();
	}

	private void TakeMenus(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			return;
		}

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file is null)
		{
			return;
		}

		var parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary ||
			!parsed.AsGodotDictionary().TryGetValue("menus", out Variant menus) ||
			menus.VariantType != Variant.Type.Dictionary)
		{
			return;
		}

		foreach (var entry in menus.AsGodotDictionary())
		{
			if (entry.Value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = entry.Value.AsGodotDictionary();
			_menus[entry.Key] = record.TryGetValue("entries", out Variant entries)
				? entries
				: new Godot.Collections.Array();
			_highlights[entry.Key] = record.TryGetValue("highlight", out Variant highlight)
				? highlight
				: "";
		}
	}

	private string? SelectedMenu() =>
		_menuChoice is { Selected: >= 0 } choice ? choice.GetItemText(choice.Selected) : null;

	private void ShowSelected()
	{
		if (_entries is null || SelectedMenu() is not { } name ||
			!_menus.TryGetValue(name, out Variant entries))
		{
			return;
		}

		_entries.Clear();
		TreeItem root = _entries.CreateItem();
		if (entries.VariantType == Variant.Type.Array)
		{
			foreach (Variant entry in entries.AsGodotArray())
			{
				AddTreeEntry(root, entry);
			}
		}
	}

	private void AddTreeEntry(TreeItem parent, Variant entry)
	{
		if (entry.VariantType != Variant.Type.Dictionary)
		{
			return;
		}

		var record = entry.AsGodotDictionary();
		TreeItem item = _entries!.CreateItem(parent);
		item.SetText(0, record.TryGetValue("name", out Variant name) ? name.AsString() : "");
		item.SetText(1, record.TryGetValue("tag", out Variant tag) ? tag.AsString() : "");

		string action = record.TryGetValue("command", out Variant command)
			? command.AsString()
			: record.TryGetValue("opens", out Variant opens) ? $"opens:{opens.AsString()}" : "";
		item.SetText(2, action);
		item.SetText(3, record.TryGetValue("source", out Variant source)
			? source.AsString()
			: "");
		item.SetText(4, record.TryGetValue("icon", out Variant icon) ? icon.AsString() : "");

		for (int column = 0; column < Columns; ++column)
		{
			item.SetEditable(column, true);
		}

		if (record.TryGetValue("children", out Variant children) &&
			children.VariantType == Variant.Type.Array)
		{
			foreach (Variant child in children.AsGodotArray())
			{
				AddTreeEntry(item, child);
			}
		}
	}

	private void AddEntry(bool child)
	{
		if (_entries is null)
		{
			return;
		}

		TreeItem parent = child && _entries.GetSelected() is { } selected
			? selected
			: _entries.GetRoot();
		if (parent is null)
		{
			return;
		}

		TreeItem item = _entries.CreateItem(parent);
		item.SetText(0, "NewEntry");
		item.SetText(1, "New Entry");
		item.SetText(2, "");
		item.SetText(3, "");
		item.SetText(4, "");
		for (int column = 0; column < Columns; ++column)
		{
			item.SetEditable(column, true);
		}

		SetStatus("Entry added — unsaved.");
	}

	/// <summary>Editable columns per entry.</summary>
	private const int Columns = 5;

	/// <summary>Reparents a row and its subtree through the save writer/load reader —
	/// `TreeItem` has no reparent.</summary>
	private void Rehang(TreeItem item, TreeItem parent, TreeItem? after)
	{
		Godot.Collections.Dictionary record = EntryFromTree(item);
		item.GetParent()?.RemoveChild(item);
		item.Free();

		AddTreeEntry(parent, record);
		TreeItem? added = parent.GetFirstChild();
		while (added?.GetNext() is { } next)
		{
			added = next;
		}

		if (added is null)
		{
			return;
		}

		if (after is not null)
		{
			added.MoveAfter(after);
		}

		added.Select(0);
		_entries?.EnsureCursorIsVisible();
	}

	/// <summary>Nests the selected row under its previous sibling (same depth only).</summary>
	private void Indent()
	{
		if (_entries?.GetSelected() is not { } selected)
		{
			return;
		}

		TreeItem? previous = selected.GetPrevInTree();
		if (previous is null || previous == _entries.GetRoot() ||
			previous.GetParent() != selected.GetParent())
		{
			SetStatus("Nest needs a row directly above it at the same level.");
			return;
		}

		string name = selected.GetText(0);
		string into = previous.GetText(0);
		Rehang(selected, previous, after: null);
		SetStatus($"'{name}' is now a submenu of '{into}' — unsaved.");
	}

	/// <summary>Lifts the selected row to its parent's level, directly after the parent.</summary>
	private void Outdent()
	{
		if (_entries?.GetSelected() is not { } selected ||
			selected.GetParent() is not { } parent || parent == _entries.GetRoot())
		{
			SetStatus("That row is already at the top level.");
			return;
		}

		if (parent.GetParent() is not { } grandparent)
		{
			return;
		}

		string name = selected.GetText(0);
		Rehang(selected, grandparent, after: parent);
		SetStatus($"'{name}' moved out to the top level — unsaved.");
	}

	/// <summary>Reorders a row among its own siblings; the ends are a no-op.</summary>
	private void Move(bool down)
	{
		if (_entries?.GetSelected() is not { } selected)
		{
			return;
		}

		TreeItem? neighbour = down ? selected.GetNext() : selected.GetPrev();
		if (neighbour is null)
		{
			return;
		}

		if (down)
		{
			selected.MoveAfter(neighbour);
		}
		else
		{
			selected.MoveBefore(neighbour);
		}

		SetStatus($"'{selected.GetText(0)}' moved — unsaved.");
	}

	private void RemoveSelected()
	{
		if (_entries?.GetSelected() is { } selected && selected != _entries.GetRoot())
		{
			selected.GetParent()?.RemoveChild(selected);
			selected.Free();
			SetStatus("Entry removed — unsaved.");
		}
	}

	/// <summary>Writes every menu to the override file, preserving its `screens` section.</summary>
	private void Save()
	{
		if (_entries is null || SelectedMenu() is not { } name)
		{
			return;
		}

		var entries = new Godot.Collections.Array();
		for (TreeItem? item = _entries.GetRoot()?.GetFirstChild(); item is not null;
			item = item.GetNext())
		{
			entries.Add(EntryFromTree(item));
		}

		_menus[name] = entries;

		var menus = new Godot.Collections.Dictionary();
		foreach (var menu in _menus)
		{
			menus[menu.Key] = new Godot.Collections.Dictionary
			{
				["highlight"] = _highlights.TryGetValue(menu.Key, out Variant highlight)
					? highlight
					: "",
				["entries"] = menu.Value,
			};
		}

		// Preserve the screens half of an existing override.
		var root = new Godot.Collections.Dictionary { ["menus"] = menus };
		if (FileAccess.FileExists(OverridePath))
		{
			using FileAccess? existing = FileAccess.Open(OverridePath, FileAccess.ModeFlags.Read);
			if (existing is not null &&
				Json.ParseString(existing.GetAsText()) is
					{ VariantType: Variant.Type.Dictionary } parsed &&
				parsed.AsGodotDictionary().TryGetValue("screens", out Variant screens))
			{
				root["screens"] = screens;
			}
		}

		DirAccess.MakeDirRecursiveAbsolute("user://ui");
		using FileAccess? file = FileAccess.Open(OverridePath, FileAccess.ModeFlags.Write);
		if (file is null)
		{
			SetStatus($"Cannot write {OverridePath}: {FileAccess.GetOpenError()}");
			return;
		}

		file.StoreString(Json.Stringify(root, "\t"));
		SetStatus($"Saved {_menus.Count} menu(s) to {OverridePath}. The game applies on boot.");
	}

	private Godot.Collections.Dictionary EntryFromTree(TreeItem item)
	{
		var record = new Godot.Collections.Dictionary
		{
			["name"] = item.GetText(0),
			["tag"] = item.GetText(1),
		};

		string action = item.GetText(2);
		if (action.StartsWith("opens:", System.StringComparison.Ordinal))
		{
			record["opens"] = action["opens:".Length..];
		}
		else if (action.Length != 0)
		{
			record["command"] = action;
		}

		if (item.GetText(3) is { Length: > 0 } source)
		{
			record["source"] = source;
		}

		if (item.GetText(4) is { Length: > 0 } icon)
		{
			record["icon"] = icon;
		}

		var children = new Godot.Collections.Array();
		for (TreeItem? child = item.GetFirstChild(); child is not null; child = child.GetNext())
		{
			children.Add(EntryFromTree(child));
		}

		if (children.Count != 0)
		{
			record["children"] = children;
		}

		return record;
	}

	private void SetStatus(string text)
	{
		if (_status is not null)
		{
			_status.Text = text;
		}
	}
}
#endif
