using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Save;

namespace Refii.Launcher;

/// <summary>
/// The Saves tab: where the selected title's saves are, and the saves the migration set aside past the
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
	private Font? _font;
	private SmartStyle? _style;
	private Engine_Title _selectedTitle = Engine_Title.Active;
	private GameScopeSelector? _scopeSelector;
	private Label _where = null!;
	private Label _status = null!;
	private OptionButton _outgoing = null!;
	private VBoxContainer _rows = null!;
	private bool _gameRunning;
	private bool _scopeInteractive = true;

	/// <summary>Currently selected game title in the Saves tab.</summary>
	public Engine_Title SelectedTitle => _selectedTitle;

	/// <summary>Raised when the user selects another title in the segmented scope bar.</summary>
	public event Action<Engine_Title>? TitleChanged;

	/// <summary>Switches the Saves tab's scoped game title without firing <see cref="TitleChanged"/>.</summary>
	public void SetTitle(Engine_Title title)
	{
		if (_selectedTitle == title)
		{
			_scopeSelector?.SetSelectedTitle(title, notify: false);
			_scopeSelector?.Refresh();
			return;
		}

		_selectedTitle = title;
		_scopeSelector?.SetSelectedTitle(title, notify: false);
		if (_status is not null)
		{
			_status.Text = string.Empty;
		}

		Refresh(_gameRunning);
	}

	/// <summary>Enables or disables user title switching on the top scope selector.</summary>
	public void SetScopeInteractive(bool interactive)
	{
		_scopeInteractive = interactive;
		if (_scopeSelector is not null)
		{
			_scopeSelector.Interactive = interactive;
		}
	}

	private static string DescribeTitleSaveBadge(Engine_Title title)
	{
		try
		{
			string profile = Engine_AppRoot.SaveDirectory;
			string slots = Engine_SaveLayout.SaveGames(profile, title.TitleId);
			int used = Directory.Exists(slots) ? Directory.GetDirectories(slots).Length : 0;
			return $"{used}/{title.SaveSlots} slots";
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 8);

		_scopeSelector = new GameScopeSelector(_selectedTitle)
		{
			BadgeProvider = DescribeTitleSaveBadge,
			Interactive = _scopeInteractive,
		};
		_scopeSelector.Configure(_font, _fontSize);
		_scopeSelector.TitleSelected += title =>
		{
			SetTitle(title);
			TitleChanged?.Invoke(title);
		};
		AddChild(_scopeSelector);

		_where = Styled(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
		AddChild(_where);

		var headerRow = new HBoxContainer();
		headerRow.AddThemeConstantOverride("separation", 8);
		var roomLabel = Styled(new Label { Text = "To make room:" });
		headerRow.AddChild(roomLabel);
		_outgoing = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		headerRow.AddChild(_outgoing);

		var openSaves = Styled(new Button { Text = "Open Saves Folder" });
		openSaves.Pressed += () =>
		{
			string dir = Engine_SaveLayout.GameDirectory(Engine_AppRoot.SaveDirectory, _selectedTitle.TitleId);
			Directory.CreateDirectory(dir);
			OS.ShellOpen(dir);
		};
		headerRow.AddChild(openSaves);
		AddChild(headerRow);

		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var scrollMargin = new MarginContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		scrollMargin.AddThemeConstantOverride("margin_right", LauncherScale.Px(12));
		scrollMargin.AddThemeConstantOverride("margin_bottom", LauncherScale.Px(4));
		scroll.AddChild(scrollMargin);
		_rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scrollMargin.AddChild(_rows);
		AddChild(scroll);

		_status = Styled(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
		AddChild(_status);
		Refresh(false);
	}

	/// <summary>The drawer's font, size and colours, as the Modding and Install tabs take them.</summary>
	public void Configure(Font? font, int fontSize, SmartStyle style)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;
		_scopeSelector?.Configure(font, fontSize);
		if (_rows is not null)
		{
			foreach (Label label in FindChildren("*", nameof(Label), true, false).OfType<Label>())
			{
				Styled(label);
			}

			Refresh(_gameRunning);
		}
	}

	private T Styled<T>(T control) where T : Control
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Label && _style is not null)
		{
			control.AddThemeColorOverride("font_color", _style.Text);
		}

		if (control is BaseButton)
		{
			control.MouseDefaultCursorShape = CursorShape.PointingHand;
		}

		return control;
	}

	/// <summary>Lists <see cref="SelectedTitle"/>'s slots and older saves again.</summary>
	public void Refresh(bool gameRunning)
	{
		if (_rows is null)
		{
			return;
		}

		_gameRunning = gameRunning;
		_scopeSelector?.Refresh();

		Engine_Title title = _selectedTitle;
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
			_rows.AddChild(Styled(new Label { Text = "No older saves." }));
		}

		foreach (Engine_SaveMigration.OlderSave save in older)
		{
			var row = new HBoxContainer();
			row.AddChild(Styled(new Label
			{
				Text = $"{save.Name} · {save.Label} · {save.Modified.ToLocalTime():yyyy-MM-dd HH:mm}",
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				ClipText = true,
			}));
			var swap = Styled(new Button { Text = "Swap in", Disabled = gameRunning });
			swap.Pressed += () => SwapIn(save);
			row.AddChild(swap);
			_rows.AddChild(row);
		}

		Styled(_outgoing);
	}

	private void SwapIn(Engine_SaveMigration.OlderSave save)
	{
		if (_gameRunning)
		{
			return;
		}

		Engine_Title title = _selectedTitle;
		string? outgoing = _outgoing.ItemCount > 0 && _outgoing.Selected >= 0 ? _outgoing.GetItemText(_outgoing.Selected) : null;
		string? name = Engine_SaveMigration.SwapIn(Engine_AppRoot.SaveDirectory, title.TitleId, save, outgoing, out string error);
		_status.Text = name is null ? "Not swapped in: " + error : $"{save.Name} from {save.Label} is in the slots as {name}.";
		GD.Print("[saves] " + _status.Text);
		Refresh(_gameRunning);
	}
}
