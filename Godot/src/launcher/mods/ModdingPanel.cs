using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
			"Install mods by dropping .f2mod packages directly into this folder. Enabled mods " +
			"override matching game records; higher in the list wins a conflict. Changes " +
			"apply the next time the game starts.");
		help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(help);

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		AddChild(row);

		var openFolder = new Button { Text = "Open Mods Folder" };
		Apply(openFolder);
		openFolder.Pressed += () =>
		{
			Directory.CreateDirectory(_modsRoot);
			OS.ShellOpen(_modsRoot);
		};
		row.AddChild(openFolder);

		var create = new Button { Text = "New Mod" };
		Apply(create);
		create.Pressed += OnNewMod;
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

		// Prune any enabled mods that no longer exist on disk:
		var installedNames = new HashSet<string>(_mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
		if (_order.Enabled.RemoveAll(name => !installedNames.Contains(name)) > 0)
		{
			_order.Write(_modsRoot, out _);
		}

		_tallies.Clear();

		var recordConflicts = Refii.Gdb.GdbPatchCompositor.DetectRecordConflicts(_modsRoot, _order, _mods);
		foreach ((string name, ModTally tally) in ModIndex.Tally(ModIndex.Predict(_order, _mods), recordConflicts))
		{
			_tallies[name] = tally;
		}

		Rebuild();
	}

	private void OnDeleteMod(ModInstall mod)
	{
		try
		{
			if (File.Exists(mod.PackagePath))
			{
				File.Delete(mod.PackagePath);
			}

			_order.Enabled.Remove(mod.Name);
			_order.Write(_modsRoot, out _);
			_statusMessage = $"Deleted '{mod.Name}.f2mod'.";
		}
		catch (System.Exception ex)
		{
			_statusMessage = $"Failed to delete '{mod.Name}': {ex.Message}";
		}

		RefreshState();
	}

	private void SaveEnabledMods()
	{
		_statusMessage = _order.Write(_modsRoot, out string error)
			? "Updated enabled mods."
			: "Failed to update enabled mods: " + error;
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

	private void OnNewMod()
	{
		try
		{
			Directory.CreateDirectory(_modsRoot);
			string baseName = "NewMod";
			string modName = baseName;
			int counter = 1;
			while (File.Exists(Path.Combine(_modsRoot, modName + ModWorkspace.PackageExtension)))
			{
				counter++;
				modName = $"{baseName}{counter}";
			}

			if (ModWorkspace.CreateModPackage(_modsRoot, modName, out string pkgPath, out string error))
			{
				_statusMessage = $"Created '{modName}.f2mod'.";
				if (!_order.Enabled.Contains(modName))
				{
					_order.Enabled.Add(modName);
					_order.Write(_modsRoot, out _);
				}
			}
			else
			{
				_statusMessage = "Failed to create mod: " + error;
			}
		}
		catch (System.Exception exception)
		{
			_statusMessage = "Failed to create mod: " + exception.Message;
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

		var grid = new GridContainer { Columns = 5 };
		grid.AddThemeConstantOverride("h_separation", 8);
		grid.AddThemeConstantOverride("v_separation", 4);
		grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_list.AddChild(grid);

		foreach (string header in new[] { "Active", "Order", "Mod", "Status", "Actions" })
		{
			Label label = MakeLabel(header);
			label.AddThemeColorOverride("font_color", _style.TabSelected);
			if (header is "Order" or "Status" or "Actions")
			{
				label.HorizontalAlignment = HorizontalAlignment.Center;
			}
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
		check.CustomMinimumSize = new Vector2(24, 24);
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
		orderBox.AddThemeConstantOverride("separation", 3);
		if (enabled)
		{
			Label numLabel = MakeLabel((position + 1).ToString());
			numLabel.CustomMinimumSize = new Vector2(16, 0);
			numLabel.HorizontalAlignment = HorizontalAlignment.Center;
			orderBox.AddChild(numLabel);

			var up = new Button { Text = "▲", Disabled = position == 0 };
			Apply(up);
			up.CustomMinimumSize = new Vector2(20, 20);
			int upIndex = position;
			up.Pressed += () =>
			{
				(_order.Enabled[upIndex], _order.Enabled[upIndex - 1]) =
					(_order.Enabled[upIndex - 1], _order.Enabled[upIndex]);
				SaveEnabledMods();
			};
			orderBox.AddChild(up);

			var down = new Button { Text = "▼", Disabled = position + 1 >= _order.Enabled.Count };
			Apply(down);
			down.CustomMinimumSize = new Vector2(20, 20);
			int downIndex = position;
			down.Pressed += () =>
			{
				(_order.Enabled[downIndex], _order.Enabled[downIndex + 1]) =
					(_order.Enabled[downIndex + 1], _order.Enabled[downIndex]);
				SaveEnabledMods();
			};
			orderBox.AddChild(down);
		}
		else
		{
			Label dash = MakeLabel("-");
			dash.CustomMinimumSize = new Vector2(58, 0);
			dash.HorizontalAlignment = HorizontalAlignment.Center;
			orderBox.AddChild(dash);
		}

		grid.AddChild(orderBox);

		string displayName = mod.Metadata.Title.Length == 0 ? mod.Name : mod.Metadata.Title;
		string label = mod.Metadata.Version.Length == 0
			? displayName
			: displayName + " (" + mod.Metadata.Version + ")";

		Label nameLabel = MakeLabel(label);
		nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		nameLabel.CustomMinimumSize = new Vector2(80, 0);
		nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

		var sb = new System.Text.StringBuilder();
		sb.AppendLine("Mod: " + displayName);
		sb.AppendLine("Folder: " + mod.Name);
		sb.AppendLine("Path: " + mod.Root.Replace('\\', '/'));
		if (mod.Metadata.Author.Length != 0)
		{
			sb.AppendLine("Author: " + mod.Metadata.Author);
		}
		if (mod.Metadata.Description.Length != 0)
		{
			sb.AppendLine();
			sb.AppendLine(mod.Metadata.Description);
		}
		nameLabel.TooltipText = sb.ToString().TrimEnd();
		grid.AddChild(nameLabel);

		// CONFLICTS / STATUS
		Control statusControl;
		if (_tallies.TryGetValue(mod.Name, out ModTally? tally) && (tally.Lost > 0 || tally.OverriddenRecords > 0))
		{
			int lostCount = tally.Lost;
			var lostBtn = new Button { Text = $"⚠ {lostCount} lost" };
			Apply(lostBtn);
			lostBtn.AddThemeColorOverride("font_color", new Color(0.95f, 0.55f, 0.15f)); // Amber warning
			lostBtn.CustomMinimumSize = new Vector2(60, 22);

			var tip = new System.Text.StringBuilder();
			tip.AppendLine($"[Modding Warning] {lostCount} modification(s) overridden by higher-priority mods:");
			foreach (string detail in tally.ConflictDetails)
			{
				tip.AppendLine($"  • {detail}");
			}
			if (tally.ConflictDetails.Count == 0)
			{
				tip.AppendLine("  • Overridden by another active mod higher in load order.");
			}
			tip.AppendLine();
			tip.Append("Click to view details of overridden modifications.");
			lostBtn.TooltipText = tip.ToString();

			ModInstall modCaptured = mod;
			ModTally tallyCaptured = tally;
			lostBtn.Pressed += () => ShowConflictDetailsDialog(modCaptured, tallyCaptured);
			statusControl = lostBtn;
		}
		else if (tally is not null && tally.Conflicts > 0)
		{
			var priorityLabel = MakeLabel("✓ Priority");
			priorityLabel.AddThemeColorOverride("font_color", new Color(0.35f, 0.85f, 0.35f)); // Light green
			priorityLabel.TooltipText = $"{tally.Conflicts} collision(s) resolved in favor of this mod.";
			priorityLabel.HorizontalAlignment = HorizontalAlignment.Center;
			priorityLabel.CustomMinimumSize = new Vector2(50, 0);
			statusControl = priorityLabel;
		}
		else
		{
			var okLabel = MakeLabel("✓ OK");
			okLabel.AddThemeColorOverride("font_color", _style.TabDimmed);
			okLabel.TooltipText = "No record conflicts with other enabled mods.";
			okLabel.HorizontalAlignment = HorizontalAlignment.Center;
			okLabel.CustomMinimumSize = new Vector2(50, 0);
			statusControl = okLabel;
		}
		grid.AddChild(statusControl);

		var actionsBox = new HBoxContainer();
		actionsBox.AddThemeConstantOverride("separation", 4);
		actionsBox.Alignment = BoxContainer.AlignmentMode.Center;

		var deleteBtn = new Button { Text = "🗑" };
		Apply(deleteBtn);
		deleteBtn.CustomMinimumSize = new Vector2(28, 22);
		deleteBtn.TooltipText = $"Delete '{mod.Name}.f2mod' package.";
		ModInstall targetMod = mod;
		deleteBtn.Pressed += () => OnDeleteMod(targetMod);
		actionsBox.AddChild(deleteBtn);

		grid.AddChild(actionsBox);
	}

	private void ShowConflictDetailsDialog(ModInstall mod, ModTally tally)
	{
		var dialog = new ModConflictDialog(mod, tally, _style, _font, _fontSize);
		Node top = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
		top.AddChild(dialog);
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
