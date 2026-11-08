using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Save;

namespace Refii.Launcher;

/// <summary>
/// The Saves tab: where the active title's saves are, and the saves the migration set aside past the
/// title's slot limit (<see cref="Engine_SaveMigration.ListOlderSaves"/>), each with a button that swaps
/// it back into the slots.
/// </summary>
/// <remarks>
/// The guest never enumerates Older saves, so this is the only way back to one. With every slot used,
/// the save chosen in "To make room" goes to Older saves first; nothing is ever deleted. A swap is
/// refused while the game runs: it holds the save folder.
/// </remarks>
public partial class SavesPanel : VBoxContainer
{
	private int _fontSize = 18;
	private Label _where = null!;
	private Label _status = null!;
	private OptionButton _outgoing = null!;
	private VBoxContainer _rows = null!;
	private bool _gameRunning;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 8);
		_where = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddChild(_where);

		var room = new HBoxContainer();
		room.AddChild(new Label { Text = "To make room:" });
		_outgoing = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		room.AddChild(_outgoing);
		AddChild(room);

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		_rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild(_rows);
		AddChild(scroll);

		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddChild(_status);
		Refresh(false);
	}

	public void Configure(int fontSize) => _fontSize = fontSize;

	/// <summary>Lists the active title's slots and older saves again.</summary>
	public void Refresh(bool gameRunning)
	{
		if (_rows is null)
		{
			return;
		}

		_gameRunning = gameRunning;
		Engine_Title title = Engine_Title.Active;
		string profile = Engine_AppRoot.SaveDirectory;
		string slots = Engine_SaveLayout.SaveGames(profile, title.TitleId);
		string[] inSlots = Directory.Exists(slots)
			? Directory.GetDirectories(slots).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToArray()
			: Array.Empty<string>();
		_where.Text = $"{title.Name}'s saves are in {Engine_SaveLayout.GameDirectory(profile, title.TitleId)}: " +
			$"{inSlots.Length} of {title.SaveSlots} slots used.";

		_outgoing.Clear();
		foreach (string name in inSlots)
		{
			_outgoing.AddItem(name);
		}

		_outgoing.Disabled = inSlots.Length < title.SaveSlots || gameRunning;

		foreach (Node child in _rows.GetChildren())
		{
			child.QueueFree();
		}

		List<Engine_SaveMigration.OlderSave> older = Engine_SaveMigration.ListOlderSaves(profile, title.TitleId);
		if (older.Count == 0)
		{
			_rows.AddChild(new Label { Text = "No older saves." });
		}

		foreach (Engine_SaveMigration.OlderSave save in older)
		{
			var row = new HBoxContainer();
			row.AddChild(new Label
			{
				Text = $"{save.Name} · {save.Label} · {save.Modified.ToLocalTime():yyyy-MM-dd HH:mm}",
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				ClipText = true,
			});
			var swap = new Button { Text = "Swap in", Disabled = gameRunning };
			swap.Pressed += () => SwapIn(save);
			row.AddChild(swap);
			_rows.AddChild(row);
		}

		foreach (Control control in GetChildren().OfType<Control>())
		{
			control.AddThemeFontSizeOverride("font_size", _fontSize);
		}
	}

	private void SwapIn(Engine_SaveMigration.OlderSave save)
	{
		if (_gameRunning)
		{
			return;
		}

		Engine_Title title = Engine_Title.Active;
		string? outgoing = _outgoing.ItemCount > 0 && _outgoing.Selected >= 0 ? _outgoing.GetItemText(_outgoing.Selected) : null;
		string? name = Engine_SaveMigration.SwapIn(Engine_AppRoot.SaveDirectory, title.TitleId, save, outgoing, out string error);
		_status.Text = name is null ? "Not swapped in: " + error : $"{save.Name} from {save.Label} is in the slots as {name}.";
		GD.Print("[saves] " + _status.Text);
		Refresh(_gameRunning);
	}
}
