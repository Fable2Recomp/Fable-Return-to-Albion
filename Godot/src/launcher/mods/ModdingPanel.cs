using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Modding;

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

	private string _modsRoot = string.Empty;
	private string _statusMessage = string.Empty;
	private List<ModInstall> _mods = new();
	private List<string> _enabledMods = new();
	private readonly Dictionary<string, ModAssetSummary> _summaries = new();
	private readonly Dictionary<string, int> _conflictCounts = new();

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
			"Install loose-file mods as folders here. A mod can place files " +
			"directly in its folder, or under a files folder. Enabled mods " +
			"override matching game files; a mod that ships " +
			"refii_vfs/banks/<bank>/_manifest.tsv replaces that bank's contents. " +
			"Higher mods in the list win conflicts. Changes apply the next time " +
			"the game starts.");
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

	private static string ResolveModsRoot()
	{
		var values = RuntimeConfig.ReadValues(Installer.GetRuntimeConfigPath());
		if (values.TryGetValue("vfs_mods_root", out string? configured) && configured.Length != 0)
		{
			if (Path.IsPathRooted(configured))
			{
				return Path.GetFullPath(configured);
			}

			return Path.GetFullPath(Path.Combine(Installer.GetInstallBasePath(), configured));
		}

		return ModWorkspace.GetDefaultModsRoot(Installer.GetInstallBasePath());
	}

	private void RefreshState()
	{
		_modsRoot = ResolveModsRoot();
		_mods = ModWorkspace.DiscoverModInstalls(_modsRoot);
		_enabledMods = ModWorkspace.ReadEnabledModNames(_modsRoot);
		_summaries.Clear();
		_conflictCounts.Clear();

		var ownersByPath = new Dictionary<string, List<string>>();
		foreach (string name in _enabledMods)
		{
			ModInstall? mod = _mods.Find(installed => installed.Name == name);
			if (mod is null)
			{
				continue;
			}

			ModAssetSummary summary = ModWorkspace.SummarizeModAssets(
				ModWorkspace.GetModAssetRoot(mod));
			foreach (string path in summary.NormalizedPaths)
			{
				if (!ownersByPath.TryGetValue(path, out List<string>? owners))
				{
					owners = new List<string>();
					ownersByPath[path] = owners;
				}

				owners.Add(name);
			}

			_summaries[name] = summary;
		}

		foreach ((string _, List<string> owners) in ownersByPath)
		{
			if (owners.Count < 2)
			{
				continue;
			}

			foreach (string owner in owners)
			{
				_conflictCounts.TryGetValue(owner, out int count);
				_conflictCounts[owner] = count + 1;
			}
		}

		Rebuild();
	}

	private void SaveEnabledMods()
	{
		_statusMessage = ModWorkspace.WriteEnabledModNames(_modsRoot, _enabledMods, out string error)
			? "Updated enabled mods."
			: "Failed to update enabled mods: " + error;
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
		foreach (string name in _enabledMods)
		{
			ModInstall? mod = _mods.Find(installed => installed.Name == name);
			if (mod is not null)
			{
				ordered.Add(mod);
			}
		}

		foreach (ModInstall mod in _mods)
		{
			if (!_enabledMods.Contains(mod.Name))
			{
				ordered.Add(mod);
			}
		}

		var grid = new GridContainer { Columns = 6 };
		grid.AddThemeConstantOverride("h_separation", 8);
		grid.AddThemeConstantOverride("v_separation", 3);
		grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_list.AddChild(grid);

		foreach (string header in new[] { "Enabled", "Order", "Mod", "Files", "Conflicts", "Path" })
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
		int position = _enabledMods.IndexOf(mod.Name);
		bool enabled = position >= 0;

		var check = new CheckBox { ButtonPressed = enabled };
		Apply(check);
		ModInstall captured = mod;
		check.Toggled += pressed =>
		{
			if (pressed)
			{
				if (!_enabledMods.Contains(captured.Name))
				{
					_enabledMods.Add(captured.Name);
				}
			}
			else
			{
				_enabledMods.Remove(captured.Name);
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
				(_enabledMods[upIndex], _enabledMods[upIndex - 1]) =
					(_enabledMods[upIndex - 1], _enabledMods[upIndex]);
				SaveEnabledMods();
			};
			orderBox.AddChild(up);

			var down = new Button { Text = "v", Disabled = position + 1 >= _enabledMods.Count };
			Apply(down);
			int downIndex = position;
			down.Pressed += () =>
			{
				(_enabledMods[downIndex], _enabledMods[downIndex + 1]) =
					(_enabledMods[downIndex + 1], _enabledMods[downIndex]);
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

		string files = _summaries.TryGetValue(mod.Name, out ModAssetSummary? summary)
			? summary.FileCount.ToString()
			: "-";
		grid.AddChild(MakeLabel(files));

		string conflicts = _conflictCounts.TryGetValue(mod.Name, out int count)
			? count.ToString()
			: "0";
		grid.AddChild(MakeLabel(conflicts));

		Label pathLabel = MakeLabel(mod.Root.Replace('\\', '/'));
		pathLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		pathLabel.TooltipText = mod.Root;
		grid.AddChild(pathLabel);
	}

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}
}
