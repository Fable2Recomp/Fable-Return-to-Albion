using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Editing;
using Refii.Mods;

namespace Refii.Launcher;

[Tool]
public partial class ModdingPanel : VBoxContainer
{
	private Font? _font;
	private int _fontSize = 18;
	private SmartStyle _style = SmartStyle.FromTheme(
		new Color(0.02f, 0.1f, 0.2f, 1f), new Color(0.2f, 0.8f, 0.4f, 1f));

	private Label _rootLabel = null!;
	private Label _statusLabel = null!;
	private VBoxContainer _list = null!;
	private bool _built;

	private Button _editorButton = null!;
	private bool _editorBusy;
	private string _modsRoot = string.Empty;
	private string _statusMessage = string.Empty;
	private List<ModInstall> _mods = new();
	private ModLoadOrder _order = new();
	private readonly Dictionary<string, int> _fileCounts = new();
	private readonly Dictionary<string, ModTally> _tallies = new();

	public void Configure(Font? font, int fontSize, SmartStyle style)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;

		if (_built)
		{
			foreach (Node child in GetChildren())
			{
				child.QueueFree();
			}

			_built = false;
		}

		Build();
	}

	private void Build()
	{
		if (_built)
		{
			return;
		}

		_built = true;
		AddThemeConstantOverride("separation", 6);

		_rootLabel = MakeLabel(string.Empty);
		_rootLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(_rootLabel);

		Label help = MakeLabel(
			"Install mods as folders here. A mod places files directly in its folder or " +
			"under a files folder, and may ship them packed as a .bnk instead. Enabled mods " +
			"override matching game files; higher in the list wins a conflict. Pack turns a " +
			"mod into a bank you can hand to someone. Changes apply the next time the game " +
			"starts.");
		help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(help);

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		AddChild(row);

		var create = new Button { Text = "Create Folder" };
		Apply(create);
		create.Pressed += OnCreateFolder;
		row.AddChild(create);

		var refresh = new Button { Text = "Refresh" };
		Apply(refresh);
		refresh.Pressed += () =>
		{
			_statusMessage = string.Empty;
			RefreshState();
		};
		row.AddChild(refresh);

		// THE EDITOR OPENS THIS INSTALL. It is the same project the game ships as, pointed at
		// the player's own `assets/`, so a mod exported there is the mod listed above.
		_editorButton = new Button();
		Apply(_editorButton);
		_editorButton.Pressed += OnEditor;
		row.AddChild(_editorButton);

		_statusLabel = MakeLabel(string.Empty);
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(_statusLabel);

		AddChild(new HSeparator());

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		AddChild(scroll);

		_list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_list.AddThemeConstantOverride("separation", 3);
		scroll.AddChild(_list);

		RefreshState();
	}

	/// <summary>
	/// Rereads the folder and folds the enabled mods' manifests.
	///
	/// THE SAME FOLD THE GAME RUNS, over manifests rather than a mount — the panel cannot mount,
	/// and a walk of every mod folder on every refresh answered a different question from the
	/// one the game answers at boot. `ModIndex.Predict` applies the same order and the same
	/// pins, so what this shows is what the mount will do.
	/// </summary>
	private void RefreshState()
	{
		_modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
		_mods = ModWorkspace.DiscoverModInstalls(_modsRoot);
		_order = ModLoadOrder.Read(_modsRoot);
		_fileCounts.Clear();
		_tallies.Clear();

		foreach (ModInstall mod in _mods)
		{
			if (_order.Enabled.Contains(mod.Name))
			{
				_fileCounts[mod.Name] = ModManifest.EnsureCurrent(mod).Entries.Count;
			}
		}

		foreach ((string name, ModTally tally) in ModIndex.Tally(ModIndex.Predict(_order, _mods)))
		{
			_tallies[name] = tally;
		}

		Rebuild();
	}

	private void SaveEnabledMods()
	{
		_statusMessage = _order.Write(_modsRoot, out string error)
			? "Updated enabled mods."
			: "Failed to update enabled mods: " + error;
		RefreshState();
	}

	private void OnPack(ModInstall mod)
	{
		bool bnkOk = ModExport.ToBank(mod, _modsRoot, out string packed, out string error);
		string zipDest = Path.Combine(mod.Root, mod.Name + ".f2zip");
		bool zipOk = ModExport.ToF2Zip(mod, zipDest, out string zipError);

		if (bnkOk && zipOk)
		{
			_statusMessage = $"Packed '{mod.Name}' to bank ({packed.Replace('\\', '/')}) and package ({zipDest.Replace('\\', '/')}).";
		}
		else if (bnkOk)
		{
			_statusMessage = $"Packed '{mod.Name}' into {packed.Replace('\\', '/')}. (.f2zip warning: {zipError})";
		}
		else if (zipOk)
		{
			_statusMessage = $"Packed '{mod.Name}' into package ({zipDest.Replace('\\', '/')}). (Bank warning: {error})";
		}
		else
		{
			_statusMessage = $"Failed to pack '{mod.Name}': {error}";
		}
		RefreshState();
	}

	/// <summary>
	/// Installs the editor if it is not here, otherwise opens it.
	///
	/// The install runs off the UI thread — it downloads and unpacks ~180 MB — and reports back
	/// through <see cref="ReportEditor"/>, which is deferred so the label is only ever touched
	/// on the main thread.
	/// </summary>
	private void OnEditor()
	{
		if (_editorBusy)
		{
			return;
		}

		EditorReadiness readiness = EditorWorkspace.Readiness(out string missing);
		if (readiness is EditorReadiness.NoGame or EditorReadiness.NoSdk or EditorReadiness.NoDotnet)
		{
			_statusMessage = missing;
			Rebuild();
			return;
		}

		if (readiness == EditorReadiness.Ready)
		{
			_statusMessage = EditorWorkspace.Launch(out string error)
				? "Opening the editor..."
				: "The editor did not start: " + error;
			Rebuild();
			return;
		}

		_editorBusy = true;
		_statusMessage = "Preparing the editor...";
		Rebuild();

		_ = System.Threading.Tasks.Task.Run(async () =>
		{
			(bool ok, string error) = await EditorInstall.RunAsync(
				status => CallDeferred(nameof(ReportEditor), status, false));
			CallDeferred(
				nameof(ReportEditor),
				ok ? "The editor is installed. Press Open Editor to start it." : error,
				true);
		});
	}

	private void ReportEditor(string message, bool finished)
	{
		_statusMessage = message;
		if (finished)
		{
			_editorBusy = false;
		}

		RefreshState();
	}

	private void OnCreateFolder()
	{
		try
		{
			Directory.CreateDirectory(_modsRoot);
			_statusMessage = "Mod folder is ready.";
		}
		catch (System.Exception exception)
		{
			_statusMessage = "Failed to create mod folder: " + exception.Message;
		}

		RefreshState();
	}

	private void Rebuild()
	{
		_rootLabel.Text = "Mod folder: " + _modsRoot.Replace('\\', '/');
		_statusLabel.Text = _statusMessage;
		UpdateEditorButton();

		foreach (Node child in _list.GetChildren())
		{
			child.QueueFree();
		}

		if (_mods.Count == 0)
		{
			_list.AddChild(MakeLabel("No mods found."));
			return;
		}

		var ordered = new List<ModInstall>();
		foreach (string name in _order.Enabled)
		{
			ModInstall? mod = _mods.Find(installed => installed.Name == name);
			if (mod is not null)
			{
				ordered.Add(mod);
			}
		}

		foreach (ModInstall mod in _mods)
		{
			if (!_order.Enabled.Contains(mod.Name))
			{
				ordered.Add(mod);
			}
		}

		var grid = new GridContainer { Columns = 7 };
		grid.AddThemeConstantOverride("h_separation", 8);
		grid.AddThemeConstantOverride("v_separation", 3);
		grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_list.AddChild(grid);

		foreach (string header in
			new[] { "Enabled", "Order", "Mod", "Files", "Conflicts", "Pack", "Path" })
		{
			Label label = MakeLabel(header);
			label.AddThemeColorOverride("font_color", _style.TabSelected);
			grid.AddChild(label);
		}

		foreach (ModInstall mod in ordered)
		{
			AddRow(grid, mod);
		}
	}

	private void AddRow(GridContainer grid, ModInstall mod)
	{
		int position = _order.Enabled.IndexOf(mod.Name);
		bool enabled = position >= 0;

		var check = new CheckBox { ButtonPressed = enabled };
		Apply(check);
		ModInstall captured = mod;
		check.Toggled += pressed =>
		{
			if (pressed)
			{
				if (!_order.Enabled.Contains(captured.Name))
				{
					_order.Enabled.Add(captured.Name);
				}
			}
			else
			{
				_order.Enabled.Remove(captured.Name);
			}

			SaveEnabledMods();
		};
		grid.AddChild(check);

		var orderBox = new HBoxContainer();
		orderBox.AddThemeConstantOverride("separation", 2);
		if (enabled)
		{
			orderBox.AddChild(MakeLabel((position + 1).ToString()));

			var up = new Button { Text = "^", Disabled = position == 0 };
			Apply(up);
			int upIndex = position;
			up.Pressed += () =>
			{
				(_order.Enabled[upIndex], _order.Enabled[upIndex - 1]) =
					(_order.Enabled[upIndex - 1], _order.Enabled[upIndex]);
				SaveEnabledMods();
			};
			orderBox.AddChild(up);

			var down = new Button { Text = "v", Disabled = position + 1 >= _order.Enabled.Count };
			Apply(down);
			int downIndex = position;
			down.Pressed += () =>
			{
				(_order.Enabled[downIndex], _order.Enabled[downIndex + 1]) =
					(_order.Enabled[downIndex + 1], _order.Enabled[downIndex]);
				SaveEnabledMods();
			};
			orderBox.AddChild(down);
		}

		grid.AddChild(orderBox);

		string displayName = mod.Metadata.Title.Length == 0 ? mod.Name : mod.Metadata.Title;
		string label = mod.Metadata.Version.Length == 0
			? displayName
			: displayName + " (" + mod.Metadata.Version + ")";

		Label nameLabel = MakeLabel(label);
		nameLabel.CustomMinimumSize = new Vector2(160, 0);
		nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

		string tooltip = "Folder: " + mod.Name;
		if (mod.Metadata.Author.Length != 0)
		{
			tooltip += "\nAuthor: " + mod.Metadata.Author;
		}

		if (mod.Metadata.Description.Length != 0)
		{
			tooltip += "\n" + mod.Metadata.Description;
		}

		nameLabel.TooltipText = tooltip;
		grid.AddChild(nameLabel);

		string files = _fileCounts.TryGetValue(mod.Name, out int fileCount)
			? fileCount.ToString()
			: "-";
		grid.AddChild(MakeLabel(files));

		// CONFLICTS, AND HOW MANY OF THEM THIS MOD LOST — the count alone says two mods touch a
		// file, not which one the player will actually see.
		Label conflictLabel = MakeLabel(
			_tallies.TryGetValue(mod.Name, out ModTally? tally)
				? tally.Conflicts == 0 ? "0" : $"{tally.Conflicts} ({tally.Lost} lost)"
				: "0");
		if (tally is not null && tally.Lost != 0)
		{
			conflictLabel.TooltipText =
				$"{tally.Lost} file(s) another enabled mod owns instead. Move '{mod.Name}' up " +
				"to win them.";
		}

		grid.AddChild(conflictLabel);

		var pack = new Button { Text = "Pack" };
		Apply(pack);
		pack.TooltipText = "Write this mod as a .bnk you can hand to someone.";
		ModInstall packed = mod;
		pack.Pressed += () => OnPack(packed);
		grid.AddChild(pack);

		Label pathLabel = MakeLabel(mod.Root.Replace('\\', '/'));
		pathLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		pathLabel.TooltipText = mod.Root;
		grid.AddChild(pathLabel);
	}

	/// <summary>
	/// The button says what pressing it does, and is disabled with a reason when it cannot.
	/// </summary>
	private void UpdateEditorButton()
	{
		if (_editorButton is null)
		{
			return;
		}

		if (_editorBusy)
		{
			_editorButton.Text = "Installing Editor...";
			_editorButton.Disabled = true;
			return;
		}

		EditorReadiness readiness = EditorWorkspace.Readiness(out string missing);
		_editorButton.Disabled = readiness is EditorReadiness.NoGame or EditorReadiness.NoSdk;
		_editorButton.Text = readiness switch
		{
			EditorReadiness.Ready => "Open Editor",
			EditorReadiness.NoEditor => "Install Editor",
			EditorReadiness.NoDotnet => "Open Editor",
			_ => "Editor Unavailable",
		};
		_editorButton.TooltipText = readiness == EditorReadiness.Ready
			? "Opens this install in Godot, with the mod tools loaded."
			: missing;
	}

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Button or CheckBox)
		{
			control.MouseDefaultCursorShape = CursorShape.PointingHand;
		}
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}
}
