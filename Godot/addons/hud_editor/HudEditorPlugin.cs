#if TOOLS
using Godot;

namespace Refii.EditorTools;

/// <summary>
/// HUD layout editor dock: edits anchor + offset of the pieces `Engine_HudLayout.FromBindings`
/// derived into `user://hud/layout.snapshot.json`, writing `user://hud/layout.json` (read at boot).
/// </summary>
[Tool]
public partial class HudEditorPlugin : EditorPlugin
{
	private const string SnapshotPath = "user://hud/layout.snapshot.json";
	private const string LayoutPath = "user://hud/layout.json";

	private VBoxContainer? _dock;
	private ItemList? _list;
	private OptionButton? _anchorChoice;
	private SpinBox? _offsetX;
	private SpinBox? _offsetY;
	private Label? _detail;
	private Label? _status;

	/// <summary>Piece id → [anchorX, anchorY, offsetX, offsetY, art, value, driven].</summary>
	private readonly Godot.Collections.Dictionary _pieces = new();

	private readonly Godot.Collections.Array<string> _order = new();

	private bool _dirty;

	/// <summary>True while the fields are being written from the selection, not by the user.</summary>
	private bool _refreshing;

	/// <summary>The nine anchors the format snaps to, in reading order.</summary>
	private static readonly (string Label, float X, float Y)[] Anchors =
	{
		("Top Left", 0f, 0f), ("Top Centre", 0.5f, 0f), ("Top Right", 1f, 0f),
		("Middle Left", 0f, 0.5f), ("Centre", 0.5f, 0.5f), ("Middle Right", 1f, 0.5f),
		("Bottom Left", 0f, 1f), ("Bottom Centre", 0.5f, 1f), ("Bottom Right", 1f, 1f),
	};

	public override void _EnterTree()
	{
		_dock = new VBoxContainer { Name = "HUD Layout" };

		var buttons = new HBoxContainer();
		var reload = new Button { Text = "Reload" };
		reload.Pressed += Reload;
		buttons.AddChild(reload);

		var save = new Button { Text = "Save Layout" };
		save.Pressed += SaveLayout;
		buttons.AddChild(save);
		_dock.AddChild(buttons);

		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_dock.AddChild(_status);

		_list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_list.ItemSelected += _ => RefreshSelection();
		_dock.AddChild(_list);

		_detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_dock.AddChild(_detail);

		_anchorChoice = new OptionButton();
		foreach ((string label, _, _) in Anchors)
		{
			_anchorChoice.AddItem(label);
		}

		_anchorChoice.ItemSelected += _ => ApplyFields();
		_dock.AddChild(Labelled("Anchor", _anchorChoice));

		_offsetX = new SpinBox { MinValue = -4000, MaxValue = 4000, Step = 1 };
		_offsetY = new SpinBox { MinValue = -4000, MaxValue = 4000, Step = 1 };
		_offsetX.ValueChanged += _ => ApplyFields();
		_offsetY.ValueChanged += _ => ApplyFields();
		_dock.AddChild(Labelled("Offset X", _offsetX));
		_dock.AddChild(Labelled("Offset Y", _offsetY));

#pragma warning disable CS0618 // AddControlToDock is deprecated in Godot 4.4+ in favor of AddDock(EditorDock)
		AddControlToDock(DockSlot.RightUl, _dock);

		// Menu-template editor over `user://ui/templates.json`.
		_menus = new MenuEditorDock();
		AddControlToDock(DockSlot.RightBl, _menus);
#pragma warning restore CS0618

		Reload();
	}

	private MenuEditorDock? _menus;

	public override void _ExitTree()
	{
#pragma warning disable CS0618 // RemoveControlFromDocks is deprecated in Godot 4.4+ in favor of RemoveDock(EditorDock)
		if (_dock is not null)
		{
			RemoveControlFromDocks(_dock);
			_dock.QueueFree();
			_dock = null;
		}

		if (_menus is not null)
		{
			RemoveControlFromDocks(_menus);
			_menus.QueueFree();
			_menus = null;
		}
#pragma warning restore CS0618
	}

	private static HBoxContainer Labelled(string text, Control control)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = text, CustomMinimumSize = new Vector2(70, 0) });
		control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		row.AddChild(control);
		return row;
	}

	/// <summary>Loads the snapshot, then the saved arrangement over it (the game's order).</summary>
	private void Reload()
	{
		_pieces.Clear();
		_order.Clear();
		_list?.Clear();
		_dirty = false;

		if (!TakeFile(SnapshotPath, informational: true))
		{
			SetStatus($"No snapshot at {SnapshotPath} yet.\nRun the game once — it writes the " +
				"derived layout there on boot.");
			return;
		}

		TakeFile(LayoutPath, informational: false);

		foreach (string id in _order)
		{
			_list?.AddItem(id);
		}

		SetStatus($"{_order.Count} piece(s). Select one, arrange it, Save Layout.");
		RefreshSelection();
	}

	/// <summary>Reads one layout file into the table; informational fields only from the snapshot.</summary>
	private bool TakeFile(string path, bool informational)
	{
		if (!FileAccess.FileExists(path))
		{
			return false;
		}

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file is null)
		{
			return false;
		}

		var parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary ||
			!parsed.AsGodotDictionary().TryGetValue("pieces", out Variant piecesVariant) ||
			piecesVariant.VariantType != Variant.Type.Dictionary)
		{
			return false;
		}

		foreach (var entry in piecesVariant.AsGodotDictionary())
		{
			string id = entry.Key.AsString();
			if (entry.Value.VariantType != Variant.Type.Dictionary)
			{
				continue;
			}

			var record = entry.Value.AsGodotDictionary();
			var anchor = record.TryGetValue("anchor", out Variant a) ? a.AsGodotArray() : null;
			var offset = record.TryGetValue("offset", out Variant o) ? o.AsGodotArray() : null;
			if (anchor is not { Count: >= 2 } || offset is not { Count: >= 2 })
			{
				continue;
			}

			// The overlay only moves pieces the snapshot defined (the game's rule).
			if (!informational && !_pieces.ContainsKey(id))
			{
				continue;
			}

			if (!_pieces.ContainsKey(id))
			{
				_order.Add(id);
			}

			var known = _pieces.TryGetValue(id, out Variant existing)
				? existing.AsGodotArray()
				: new Godot.Collections.Array { 0f, 0f, 0f, 0f, "", "", "" };

			known[0] = (float)anchor[0].AsDouble();
			known[1] = (float)anchor[1].AsDouble();
			known[2] = (float)offset[0].AsDouble();
			known[3] = (float)offset[1].AsDouble();
			if (informational)
			{
				known[4] = record.TryGetValue("art", out Variant art) ? art.AsString() : "";
				known[5] = record.TryGetValue("value", out Variant value) ? value.AsString() : "";
				known[6] = record.TryGetValue("driven", out Variant driven) ? driven.AsString() : "";
			}

			_pieces[id] = known;
		}

		return true;
	}

	private string? SelectedId()
	{
		int[]? selected = _list?.GetSelectedItems();
		return selected is { Length: > 0 } && selected[0] < _order.Count
			? _order[selected[0]]
			: null;
	}

	private void RefreshSelection()
	{
		if (SelectedId() is not { } id ||
			!_pieces.TryGetValue(id, out Variant record))
		{
			if (_detail is not null)
			{
				_detail.Text = string.Empty;
			}
			return;
		}

		var fields = record.AsGodotArray();
		_refreshing = true;

		float ax = (float)fields[0].AsDouble();
		float ay = (float)fields[1].AsDouble();
		int best = 0;
		float bestDistance = float.MaxValue;
		for (int i = 0; i < Anchors.Length; ++i)
		{
			float d = Mathf.Abs(Anchors[i].X - ax) + Mathf.Abs(Anchors[i].Y - ay);
			if (d < bestDistance)
			{
				bestDistance = d;
				best = i;
			}
		}

		_anchorChoice?.Select(best);
		if (_offsetX is not null)
		{
			_offsetX.Value = fields[2].AsDouble();
		}

		if (_offsetY is not null)
		{
			_offsetY.Value = fields[3].AsDouble();
		}

		_refreshing = false;

		if (_detail is not null)
		{
			_detail.Text = $"art: {fields[4].AsString()}\nvalue: {fields[5].AsString()}" +
				$"\ndrive: {fields[6].AsString()}";
		}
	}

	private void ApplyFields()
	{
		if (_refreshing || SelectedId() is not { } id ||
			!_pieces.TryGetValue(id, out Variant record))
		{
			return;
		}

		var fields = record.AsGodotArray();
		int chosen = _anchorChoice?.Selected ?? 0;
		fields[0] = Anchors[chosen].X;
		fields[1] = Anchors[chosen].Y;
		fields[2] = (float)(_offsetX?.Value ?? 0);
		fields[3] = (float)(_offsetY?.Value ?? 0);
		_pieces[id] = fields;
		_dirty = true;
		SetStatus($"{id} moved — unsaved changes.");
	}

	/// <summary>Writes every piece's anchor and offset (what ApplyOverrides reads); no informational fields.</summary>
	private void SaveLayout()
	{
		DirAccess.MakeDirRecursiveAbsolute("user://hud");

		var pieces = new Godot.Collections.Dictionary();
		foreach (string id in _order)
		{
			var fields = _pieces[id].AsGodotArray();
			pieces[id] = new Godot.Collections.Dictionary
			{
				["anchor"] = new Godot.Collections.Array
					{ fields[0].AsDouble(), fields[1].AsDouble() },
				["offset"] = new Godot.Collections.Array
					{ fields[2].AsDouble(), fields[3].AsDouble() },
			};
		}

		var root = new Godot.Collections.Dictionary { ["pieces"] = pieces };
		using FileAccess? file = FileAccess.Open(LayoutPath, FileAccess.ModeFlags.Write);
		if (file is null)
		{
			SetStatus($"Cannot write {LayoutPath}: {FileAccess.GetOpenError()}");
			return;
		}

		file.StoreString(Json.Stringify(root, "\t"));
		_dirty = false;
		SetStatus($"Saved {_order.Count} piece(s) to {LayoutPath}. The game applies it on boot.");
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
