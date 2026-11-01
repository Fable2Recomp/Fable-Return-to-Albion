#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Assets.UI;
using Refii.Ui;

namespace Refii.EditorTools;

/// <summary>One HUD piece a layout editor lists: its id, where it stands with nothing stated over it, and what it is.</summary>
public sealed record HudLayoutPiece(string Id, Engine_HudModLayout.Move Under, string Detail);

/// <summary>
/// THE HUD PLACEMENT EDITOR — one control for every place HUD pieces are moved: the Mod Studio's UI
/// tab (its entries are a mod's <c>ui/hud.json</c> <c>layout</c> block) and the player's layout dock
/// (its entries are <c>user://hud/layout.json</c>). Both state the same entry per piece
/// (<see cref="Engine_HudModLayout.Move"/>); the owner says what the pieces are, what its file
/// states, and where a save goes. The anchor presets, re-anchoring in place and the diff are
/// <see cref="Engine_HudLayoutEdit"/>'s.
/// </summary>
[Tool]
public partial class UiLayoutEditor : VBoxContainer
{
	/// <summary>A move changed (the owner's preview re-places; its file is not written).</summary>
	public event Action? Edited;

	/// <summary>The selected piece changed.</summary>
	public event Action<string>? PieceSelected;

	/// <summary>Save was pressed; the owner writes <see cref="Changes"/>. Only with <see cref="Saves"/>.</summary>
	public event Action? SaveRequested;

	/// <summary>Reload was pressed. Only with <see cref="Saves"/>.</summary>
	public event Action? ReloadRequested;

	/// <summary>Whether the editor shows its own Save and Reload buttons (the player's dock); the Studio saves the tab.</summary>
	public bool Saves { get; init; }

	/// <summary>
	/// The window a re-anchor keeps a piece in place at — the Studio's preview window; the design
	/// window otherwise.
	/// </summary>
	public Vector2 ReferenceWindow { get; set; } = new(1280f, 720f);

	private readonly List<HudLayoutPiece> _pieces = new();
	private readonly Dictionary<string, Engine_HudModLayout.Move> _loaded = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Engine_HudModLayout.Move> _stated = new(StringComparer.Ordinal);

	private LineEdit _filter = null!;
	private ItemList _list = null!;
	private Label _detail = null!;
	private OptionButton _preset = null!;
	private SpinBox _anchorX = null!, _anchorY = null!, _offsetX = null!, _offsetY = null!;
	private CheckBox _keepPlace = null!;
	private Button _reset = null!;
	private Label _status = null!;
	private readonly List<string> _shown = new();
	private bool _filling;

	/// <summary>What this editor's entries are, for the status line.</summary>
	public string TargetName { get; set; } = string.Empty;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 6);

		if (Saves)
		{
			var buttons = new HBoxContainer();
			var reload = new Button { Text = "🔄 Reload", TooltipText = "Read the pieces and the saved arrangement again" };
			reload.Pressed += () => ReloadRequested?.Invoke();
			buttons.AddChild(reload);
			var save = new Button { Text = "💾 Save", TooltipText = "Write the pieces you moved; every other entry is kept as it is" };
			save.Pressed += () => SaveRequested?.Invoke();
			buttons.AddChild(save);
			AddChild(buttons);
		}

		_filter = new LineEdit { PlaceholderText = "Filter pieces…", ClearButtonEnabled = true };
		_filter.TextChanged += _ => Fill();
		AddChild(_filter);

		_list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 140) };
		_list.ItemSelected += _ => ShowSelected();
		AddChild(_list);

		_detail = ModStudioUiStyles.Note(string.Empty);
		AddChild(_detail);

		_preset = new OptionButton();
		foreach (Engine_HudLayoutEdit.Preset preset in Engine_HudLayoutEdit.Presets)
		{
			_preset.AddItem(preset.Label);
		}

		_preset.AddItem("Custom");
		_preset.ItemSelected += OnPreset;
		AddChild(ModStudioUiStyles.Field("Anchor", _preset));

		_keepPlace = new CheckBox
		{
			Text = "Keep the piece in place when re-anchoring",
			ButtonPressed = true,
			TooltipText = "A new anchor moves the offset so the piece stays where it is at the reference window",
		};
		AddChild(_keepPlace);

		_anchorX = Number(0, 1, 0.01);
		_anchorY = Number(0, 1, 0.01);
		AddChild(ModStudioUiStyles.Field("Anchor X / Y", Pair(_anchorX, _anchorY)));
		_offsetX = Number(-4000, 4000, 1);
		_offsetY = Number(-4000, 4000, 1);
		AddChild(ModStudioUiStyles.Field("Offset X / Y", Pair(_offsetX, _offsetY)));
		foreach (SpinBox box in new[] { _anchorX, _anchorY, _offsetX, _offsetY })
		{
			box.ValueChanged += _ => OnFields();
		}

		_reset = new Button { Text = "↺ Back to the place beneath", TooltipText = "Drop this entry: the piece goes back to where it stands without it" };
		_reset.Pressed += ResetSelected;
		AddChild(_reset);

		_status = ModStudioUiStyles.Note(string.Empty);
		AddChild(_status);
		Fill();
	}

	private static SpinBox Number(double min, double max, double step) =>
		new() { MinValue = min, MaxValue = max, Step = step, AllowGreater = true, AllowLesser = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };

	private static HBoxContainer Pair(Control a, Control b)
	{
		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		row.AddChild(a);
		row.AddChild(b);
		return row;
	}

	/// <summary>
	/// Lists <paramref name="pieces"/> with <paramref name="stated"/> (what the file states now) over
	/// them. The editor's changes are measured from <paramref name="stated"/>.
	/// </summary>
	public void Load(IEnumerable<HudLayoutPiece> pieces, IReadOnlyDictionary<string, Engine_HudModLayout.Move> stated)
	{
		_pieces.Clear();
		_pieces.AddRange(pieces);
		_loaded.Clear();
		_stated.Clear();
		foreach ((string id, Engine_HudModLayout.Move move) in stated)
		{
			_loaded[id] = move;
			_stated[id] = move;
		}

		if (IsNodeReady())
		{
			Fill();
		}
	}

	/// <summary>The entries as edited (the file's, with this session's moves).</summary>
	public IReadOnlyDictionary<string, Engine_HudModLayout.Move> Stated => _stated;

	/// <summary>What a save writes: the entries moved and the entries dropped since <see cref="Load"/>.</summary>
	public Engine_HudLayoutEdit.Changes Changes => Engine_HudLayoutEdit.Diff(_loaded, _stated);

	/// <summary>The listed pieces.</summary>
	public IReadOnlyList<HudLayoutPiece> Pieces => _pieces;

	public string? SelectedId =>
		_list.GetSelectedItems() is { Length: > 0 } selected && selected[0] < _shown.Count ? _shown[selected[0]] : null;

	/// <summary>Where a piece stands as edited: its entry, else the place beneath.</summary>
	public Engine_HudModLayout.Move PlaceOf(string id) =>
		_stated.TryGetValue(id, out Engine_HudModLayout.Move move) ? move :
		_pieces.FirstOrDefault(p => p.Id == id)?.Under ?? default;

	public bool Lists(string id) => _pieces.Any(p => p.Id == id);

	/// <summary>States a piece's place — a drag in the owner's preview.</summary>
	public void SetPlace(string id, Engine_HudModLayout.Move move)
	{
		_stated[id] = move;
		if (SelectedId == id)
		{
			ShowSelected();
		}
		else
		{
			Refresh(id);
		}

		Changed();
	}

	/// <summary>Selects a piece (a click in the owner's preview).</summary>
	public void Select(string id)
	{
		int at = _shown.IndexOf(id);
		if (at < 0 && _filter.Text.Length != 0)
		{
			_filter.Text = string.Empty;
			Fill();
			at = _shown.IndexOf(id);
		}

		if (at >= 0)
		{
			_list.Select(at);
			_list.EnsureCurrentIsVisible();
			ShowSelected();
		}
	}

	public void SetStatus(string text) => _status.Text = text;

	private void Fill()
	{
		string filter = _filter.Text.Trim();
		string? keep = SelectedId;
		_list.Clear();
		_shown.Clear();
		foreach (HudLayoutPiece piece in _pieces)
		{
			if (filter.Length != 0 && !piece.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			_shown.Add(piece.Id);
			_list.AddItem(Label(piece.Id));
		}

		if (keep is not null && _shown.IndexOf(keep) is >= 0 and var at)
		{
			_list.Select(at);
		}

		ShowSelected();
		SetStatus(_pieces.Count == 0
			? "No pieces to arrange yet."
			: $"{_pieces.Count} piece(s); {_stated.Count} placed by {TargetName}. Select one, or drag it in the preview.");
	}

	private string Label(string id) => (_stated.ContainsKey(id) ? "● " : "    ") + id;

	private void Refresh(string id)
	{
		int at = _shown.IndexOf(id);
		if (at >= 0)
		{
			_list.SetItemText(at, Label(id));
		}
	}

	private void ShowSelected()
	{
		string? id = SelectedId;
		bool any = id is not null;
		foreach (Control control in new Control[] { _preset, _anchorX, _anchorY, _offsetX, _offsetY })
		{
			if (control is SpinBox box)
			{
				box.Editable = any;
			}
			else if (control is OptionButton option)
			{
				option.Disabled = !any;
			}
		}

		_reset.Disabled = id is null || !_stated.ContainsKey(id);
		if (id is null)
		{
			_detail.Text = string.Empty;
			return;
		}

		HudLayoutPiece? piece = _pieces.FirstOrDefault(p => p.Id == id);
		Engine_HudModLayout.Move move = PlaceOf(id);
		_filling = true;
		int preset = Engine_HudLayoutEdit.PresetOf(move.AnchorX, move.AnchorY);
		_preset.Select(preset < 0 ? Engine_HudLayoutEdit.Presets.Length : preset);
		_anchorX.Value = move.AnchorX;
		_anchorY.Value = move.AnchorY;
		_offsetX.Value = move.OffsetX;
		_offsetY.Value = move.OffsetY;
		_filling = false;

		_detail.Text = (piece?.Detail is { Length: > 0 } detail ? detail + "\n" : string.Empty) +
			(_stated.ContainsKey(id)
				? $"Placed by {TargetName}."
				: $"Not placed by {TargetName}: at the place beneath ({Describe(piece?.Under ?? default)}).");
		Refresh(id);
		PieceSelected?.Invoke(id);
	}

	private static string Describe(Engine_HudModLayout.Move move) => string.Format(CultureInfo.InvariantCulture,
		"anchor {0:0.##}, {1:0.##}; offset {2:0.#}, {3:0.#}", move.AnchorX, move.AnchorY, move.OffsetX, move.OffsetY);

	private void OnPreset(long index)
	{
		if (_filling || SelectedId is not { } id || index >= Engine_HudLayoutEdit.Presets.Length)
		{
			return;
		}

		Engine_HudLayoutEdit.Preset preset = Engine_HudLayoutEdit.Presets[index];
		Engine_HudModLayout.Move move = PlaceOf(id);
		if (_keepPlace.ButtonPressed)
		{
			Vector2 extent = Engine_HudLayout.CanvasExtent(ReferenceWindow);
			move = Engine_HudLayoutEdit.Rebase(move, preset.X, preset.Y, extent.X, extent.Y, Engine_HudLayout.Scale(extent));
		}
		else
		{
			move = move with { AnchorX = preset.X, AnchorY = preset.Y };
		}

		_stated[id] = move;
		ShowSelected();
		Changed();
	}

	private void OnFields()
	{
		if (_filling || SelectedId is not { } id)
		{
			return;
		}

		_stated[id] = new Engine_HudModLayout.Move(
			(float)_anchorX.Value, (float)_anchorY.Value, (float)_offsetX.Value, (float)_offsetY.Value);
		_filling = true;
		int preset = Engine_HudLayoutEdit.PresetOf((float)_anchorX.Value, (float)_anchorY.Value);
		_preset.Select(preset < 0 ? Engine_HudLayoutEdit.Presets.Length : preset);
		_filling = false;
		_reset.Disabled = false;
		Refresh(id);
		Changed();
	}

	private void ResetSelected()
	{
		if (SelectedId is not { } id || !_stated.Remove(id))
		{
			return;
		}

		ShowSelected();
		Changed();
	}

	private void Changed()
	{
		Engine_HudLayoutEdit.Changes changes = Changes;
		SetStatus(changes.Empty
			? "No unsaved moves."
			: $"Unsaved: {changes.Set.Count} piece(s) moved, {changes.Removed.Count} returned to the place beneath.");
		Edited?.Invoke();
	}
}

/// <summary>
/// The player's arrangement: <c>user://hud/layout.json</c> (<see cref="Engine_HudLayout.LayoutPath"/>)
/// over the pieces the game last snapshotted (<see cref="Engine_HudLayout.SnapshotPath"/>): each
/// piece's place beneath the player's file — shipped, then the enabled mods' layout in load order —
/// which is where "back to the place beneath" returns it. A save writes only the entries the editor
/// changed; the file's other entries and fields stay.
/// </summary>
public static class PlayerHudLayoutTarget
{
	public const string Name = "your layout";

	/// <summary>
	/// Whether the last snapshot holds the arrangement beneath the player's file. False for one an
	/// older build wrote over it: its places are the player's own until the game runs once more.
	/// </summary>
	public static bool SnapshotIsBeneath() =>
		Engine_HudLayoutEdit.SnapshotIsBeneath(ReadText(Engine_HudLayout.SnapshotPath));

	/// <summary>The snapshot's pieces (each at its place beneath the player's file) and the player's own entries; null without a snapshot.</summary>
	public static (List<HudLayoutPiece> Pieces, Dictionary<string, Engine_HudModLayout.Move> Stated)? Load()
	{
		string snapshot = ReadText(Engine_HudLayout.SnapshotPath);
		if (snapshot.Length == 0)
		{
			return null;
		}

		var pieces = new List<HudLayoutPiece>();
		System.Text.Json.Nodes.JsonObject root = Engine_HudLayoutEdit.ParseObject(snapshot);
		Dictionary<string, Engine_HudModLayout.Move> placed = Engine_HudModLayout.ParsePlayerFile(snapshot);
		if (root[Engine_HudModLayout.PlayerSection] is System.Text.Json.Nodes.JsonObject entries)
		{
			foreach ((string id, System.Text.Json.Nodes.JsonNode? node) in entries)
			{
				if (!placed.TryGetValue(id, out Engine_HudModLayout.Move move) || node is not System.Text.Json.Nodes.JsonObject record)
				{
					continue;
				}

				string detail = $"art: {Engine_UiMenuEdit.Text(record, "art")}   value: {Engine_UiMenuEdit.Text(record, "value")}   " +
					$"drive: {Engine_UiMenuEdit.Text(record, "driven")}";
				pieces.Add(new HudLayoutPiece(id, move, detail));
			}
		}

		Dictionary<string, Engine_HudModLayout.Move> stated = Engine_HudModLayout.ParsePlayerFile(ReadText(Engine_HudLayout.LayoutPath));
		foreach (string id in stated.Keys.Where(id => pieces.All(p => p.Id != id)).ToList())
		{
			// The game moves only pieces the build has; an entry for one it lacks is kept but not listed.
			stated.Remove(id);
		}

		return (pieces, stated);
	}

	/// <summary>Writes <paramref name="changes"/> into the player's file; an error, or empty when written.</summary>
	public static string Save(Engine_HudLayoutEdit.Changes changes)
	{
		if (changes.Empty)
		{
			return string.Empty;
		}

		DirAccess.MakeDirRecursiveAbsolute("user://hud");
		string text = Engine_HudLayoutEdit.Apply(ReadText(Engine_HudLayout.LayoutPath), Engine_HudModLayout.PlayerSection, changes);
		using FileAccess? file = FileAccess.Open(Engine_HudLayout.LayoutPath, FileAccess.ModeFlags.Write);
		if (file is null)
		{
			return $"cannot write {Engine_HudLayout.LayoutPath}: {FileAccess.GetOpenError()}";
		}

		file.StoreString(text);
		return string.Empty;
	}

	internal static string ReadText(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			return string.Empty;
		}

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return file?.GetAsText() ?? string.Empty;
	}
}
#endif
