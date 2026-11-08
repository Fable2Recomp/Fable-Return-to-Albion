using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
	private readonly Dictionary<string, ModNativeSources> _native = new(StringComparer.OrdinalIgnoreCase);
	private List<ModNativeConflict> _nativeConflicts = new();
	private List<ModNativeChain> _nativeChains = new();
	private List<ModDependencyProblem> _dependencyProblems = new();
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

	/// <summary>
	/// Rebuilds the panel for the title now active: its mods folder, its packages and its
	/// extension in every label. The launcher calls it on a title switch.
	/// </summary>
	public void ReloadForTitle()
	{
		if (_built)
		{
			Configure(_font, _fontSize, _style);
		}
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
		_rootLabel.Visible = !Phone;
		AddChild(_rootLabel);

		string extension = Refii.Core.Engine_Title.Active.ModExtension;
		Label help = MakeLabel(Phone ? PhoneHelp(extension) :
			$"Install a mod package ({extension}) from a file or a link, or drop packages into this " +
			"folder. A mod is installed disabled: tick it to turn it on. Enabled mods override " +
			"matching game records; higher in the list wins a conflict. Changes apply the next " +
			"time the game starts.");
		help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		AddChild(help);

		var installRow = new HBoxContainer();
		installRow.AddThemeConstantOverride("separation", 6);
		AddChild(installRow);

		var installFile = new Button { Text = "Install from file…" };
		Apply(installFile);
		installFile.TooltipText = $"Install a {Refii.Core.Engine_Title.Active.ModExtension} package from this computer.";
		installFile.Pressed += OnInstallFromFile;
		installRow.AddChild(installFile);

		if (BuildPhoneInstallRow(installRow)) return;  // ModdingPanel.Android.cs

		var installUrl = new Button { Text = "Install from URL…" };
		Apply(installUrl);
		installUrl.TooltipText = $"Download and install a {Refii.Core.Engine_Title.Active.ModExtension} package from an https link.";
		installUrl.Pressed += OnInstallFromUrl;
		installRow.AddChild(installUrl);

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

		row.AddChild(RefreshButton());

		// THE EDITOR OPENS THIS INSTALL. It is the same project the game ships as, pointed at
		// the player's own `assets/`, so a mod exported there is the mod listed above.
		_editorButton = new Button();
		Apply(_editorButton);
		_editorButton.Pressed += OnEditor;
		row.AddChild(_editorButton);

		AddStatusAndList();
	}

	private Button RefreshButton()
	{
		var refresh = new Button { Text = "Refresh" };
		Apply(refresh);
		refresh.Pressed += () =>
		{
			_statusMessage = string.Empty;
			RefreshState();
		};
		return refresh;
	}

	/// <summary>The status line and the scrolling list of mods, under the buttons.</summary>
	private void AddStatusAndList()
	{
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

		// Synchronize _order.Order and _order.Enabled with installed mods:
		var installedNames = new HashSet<string>(_mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
		bool orderChanged = false;

		if (_order.Enabled.RemoveAll(name => !installedNames.Contains(name)) > 0)
		{
			orderChanged = true;
		}

		if (_order.Order.RemoveAll(name => !installedNames.Contains(name)) > 0)
		{
			orderChanged = true;
		}

		foreach (ModInstall mod in _mods)
		{
			if (!_order.Order.Contains(mod.Name, StringComparer.OrdinalIgnoreCase))
			{
				_order.Order.Add(mod.Name);
				orderChanged = true;
			}
		}

		// A required mod sits beneath the mod that needs it (ModDependencies).
		if (ModDependencies.Sort(_order, _mods, out string cycle))
		{
			orderChanged = true;
		}
		else if (cycle.Length != 0)
		{
			_statusMessage = "Mods require each other in a circle, so their order was left alone: " + cycle;
		}

		if (orderChanged)
		{
			_order.Write(_modsRoot, out _);
			if (_order.Enabled.Count == 0)
			{
				Refii.Gdb.GdbPatchCompositor.PurgeCache(_modsRoot);
			}
			else
			{
				var overlays = Refii.Mods.ModIndex.ResolveOverlays(_modsRoot, _order, _mods);
				Refii.Gdb.GdbPatchCompositor.Apply(null, overlays, _modsRoot);
			}
		}

		_tallies.Clear();

		_native.Clear();
		foreach (ModInstall mod in _mods)
		{
			if (ModNative.Read(mod) is { } native)
			{
				_native[mod.Name] = native;
			}
		}

		List<ModNativeSources> enabledNative = _order.Enabled
			.Where(name => _native.ContainsKey(name))
			.Select(name => _native[name])
			.ToList();
		Dictionary<string, string> refiiOverrides = enabledNative.Count == 0
			? new Dictionary<string, string>()
			: NativeModConsent.RefiiOverrides();
		_nativeConflicts = enabledNative.Count == 0
			? new List<ModNativeConflict>()
			: ModNative.FindConflicts(enabledNative, refiiOverrides);
		_nativeChains = enabledNative.Count == 0
			? new List<ModNativeChain>()
			: ModNative.PlanChains(enabledNative, refiiOverrides);
		_dependencyProblems = ModDependencies.Check(_order, _mods);

		var recordConflicts = Refii.Gdb.GdbPatchCompositor.DetectRecordConflicts(_modsRoot, _order, _mods);
		var rowConflicts = Refii.Mods.ModRowOverlays.DetectRowConflicts(_order, _mods);
		// Mod effects (fx/effects/*.json) two mods both state: an effect, an emitter or a system.
		rowConflicts.AddRange(Refii.Fx.Engine_FxModEffects.DetectConflicts(_order, _mods));
		foreach ((string name, ModTally tally) in ModIndex.Tally(ModIndex.Predict(_order, _mods), recordConflicts, rowConflicts))
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

			_order.Enabled.RemoveAll(name => string.Equals(name, mod.Name, StringComparison.OrdinalIgnoreCase));
			_order.Order.RemoveAll(name => string.Equals(name, mod.Name, StringComparison.OrdinalIgnoreCase));
			_statusMessage = $"Deleted '{mod.Name}{Refii.Core.Engine_Title.Active.ModExtension}'.";
			SaveEnabledMods();
		}
		catch (System.Exception ex)
		{
			_statusMessage = $"Failed to delete '{mod.Name}': {ex.Message}";
			RefreshState();
		}
	}

	private void SaveEnabledMods() => WriteOrder(null);

	/// <summary>
	/// Writes <see cref="_order"/> and recomposes the record overlays. <paramref name="status"/>
	/// replaces the default status line when the write succeeds.
	/// </summary>
	private void WriteOrder(string? status)
	{
		// Synchronize _order.Enabled to strictly follow _order.Order
		var newEnabled = new List<string>();
		foreach (string name in _order.Order)
		{
			if (_order.Enabled.Contains(name, StringComparer.OrdinalIgnoreCase))
			{
				newEnabled.Add(name);
			}
		}
		_order.Enabled.Clear();
		_order.Enabled.AddRange(newEnabled);

		_statusMessage = _order.Write(_modsRoot, out string error)
			? status ?? "Updated enabled mods."
			: "Failed to update enabled mods: " + error;

		_mods = ModWorkspace.DiscoverModInstalls(_modsRoot);
		if (_order.Enabled.Count == 0)
		{
			Refii.Gdb.GdbPatchCompositor.PurgeCache(_modsRoot);
		}
		else
		{
			var overlays = Refii.Mods.ModIndex.ResolveOverlays(_modsRoot, _order, _mods);
			Refii.Gdb.GdbPatchCompositor.Apply(null, overlays, _modsRoot);
		}
		RefreshState();
	}


	/// <summary>Picks a .f2mod on this computer and shows what it is before installing it.</summary>
	private void OnInstallFromFile()
	{
		if (Phone) { OnInstallFromPhone(); return; }

		var picker = new FileDialog
		{
			FileMode = FileDialog.FileModeEnum.OpenFile,
			Access = FileDialog.AccessEnum.Filesystem,
			Title = "Install Mod Package",
			Filters = new[] { $"*{Refii.Core.Engine_Title.Active.ModExtension} ; Refii mod package" },
			Size = new Vector2I(820, 560),
		};
		picker.FileSelected += path => ConfirmInstall(ModInstaller.Inspect(path, _modsRoot), downloaded: false);
		picker.VisibilityChanged += () =>
		{
			if (!picker.Visible)
			{
				picker.QueueFree();
			}
		};
		DialogHost().AddChild(picker);
		picker.PopupCentered();
	}

	/// <summary>
	/// Downloads an https link into the mods folder's <c>.cache/downloads/</c> with progress, then
	/// shows what it is before installing it. Cancel stops the download and removes the file.
	/// </summary>
	private void OnInstallFromUrl()
	{
		var dialog = new ConfirmationDialog
		{
			Title = "Install Mod from URL",
			OkButtonText = "Download",
			CancelButtonText = "Cancel",
			DialogHideOnOk = false,
			MinSize = new Vector2I(560, 0),
		};

		var box = new VBoxContainer();
		box.AddThemeConstantOverride("separation", 6);
		var prompt = new Label { Text = $"Paste an https link to a {Refii.Core.Engine_Title.Active.ModExtension} package:" };
		Apply(prompt);
		box.AddChild(prompt);
		var link = new LineEdit
		{
			PlaceholderText = $"https://example.com/my_mod{Refii.Core.Engine_Title.Active.ModExtension}",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		Apply(link);
		box.AddChild(link);
		var bar = new ProgressBar
		{
			MinValue = 0,
			MaxValue = 1,
			Step = 0.001,
			Visible = false,
			CustomMinimumSize = new Vector2(0, 20),
		};
		box.AddChild(bar);
		var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		Apply(status);
		box.AddChild(status);
		dialog.AddChild(box);

		CancellationTokenSource? running = null;
		bool closed = false;

		void Finished(ModInstallResult result)
		{
			running?.Dispose();
			running = null;
			if (closed || !GodotObject.IsInstanceValid(dialog))
			{
				ModInstaller.DiscardDownload(result.Path, _modsRoot);
				return;
			}

			if (!result.Ok)
			{
				ModInstaller.DiscardDownload(result.Path, _modsRoot);
				status.Text = result.Error;
				bar.Visible = false;
				link.Editable = true;
				dialog.GetOkButton().Disabled = false;
				return;
			}

			dialog.Hide();
			ConfirmInstall(result, downloaded: true);
		}

		void Start()
		{
			if (running is not null)
			{
				return;
			}

			string url = link.Text.Trim();
			if (url.Length == 0)
			{
				status.Text = "Paste a link first.";
				return;
			}

			running = new CancellationTokenSource();
			CancellationToken token = running.Token;
			link.Editable = false;
			dialog.GetOkButton().Disabled = true;
			bar.Visible = true;
			bar.Value = 0;
			status.Text = "Downloading…";

			var progress = new DeferredProgress(value =>
			{
				if (!GodotObject.IsInstanceValid(bar))
				{
					return;
				}

				bar.Indeterminate = value < 0;
				if (value >= 0)
				{
					bar.Value = value;
				}
			});
			string root = _modsRoot;
			_ = Task.Run(async () =>
			{
				ModInstallResult result = await ModInstaller.DownloadAsync(url, progress, token, root);
				Callable.From(() => Finished(result)).CallDeferred();
			});
		}

		dialog.Confirmed += Start;
		link.TextSubmitted += _ => Start();
		dialog.Canceled += () =>
		{
			closed = true;
			running?.Cancel();
		};
		dialog.VisibilityChanged += () =>
		{
			if (!dialog.Visible)
			{
				dialog.QueueFree();
			}
		};
		DialogHost().AddChild(dialog);
		dialog.PopupCentered();
		link.GrabFocus();
	}

	/// <summary>
	/// Shows the inspection — hard mod, unmet requirements, warnings — and installs only on
	/// Install. A downloaded package is removed from <c>.cache/downloads/</c> either way.
	/// </summary>
	private void ConfirmInstall(ModInstallResult preview, bool downloaded)
	{
		if (!preview.Ok)
		{
			if (downloaded)
			{
				ModInstaller.DiscardDownload(preview.Path, _modsRoot);
			}

			_statusMessage = "Could not install: " + preview.Error;
			Rebuild();
			return;
		}

		var dialog = new ConfirmationDialog
		{
			Title = preview.HardMod ? "Install a hard mod (native code)" : "Install mod",
			DialogText = ModInstaller.Describe(preview),
			OkButtonText = "Install",
			CancelButtonText = "Cancel",
			DialogAutowrap = true,
			MinSize = new Vector2I(560, 0),
		};
		dialog.Confirmed += () => Install(preview.Path, downloaded);
		dialog.Canceled += () =>
		{
			if (downloaded)
			{
				ModInstaller.DiscardDownload(preview.Path, _modsRoot);
			}
		};
		dialog.VisibilityChanged += () =>
		{
			if (!dialog.Visible)
			{
				dialog.QueueFree();
			}
		};
		DialogHost().AddChild(dialog);
		dialog.PopupCentered();
	}

	private void Install(string packagePath, bool downloaded)
	{
		ModInstallResult result = ModInstaller.InstallFromFile(packagePath, _modsRoot);
		if (downloaded)
		{
			ModInstaller.DiscardDownload(packagePath, _modsRoot);
		}

		if (!result.Ok)
		{
			_statusMessage = "Could not install: " + result.Error;
			RefreshState();
			return;
		}

		var message = new System.Text.StringBuilder();
		message.Append($"Installed '{result.Title}'");
		if (result.Version.Length != 0)
		{
			message.Append(' ').Append(result.Version);
		}

		message.Append(" (disabled — tick it to turn it on).");
		if (result.HardMod)
		{
			message.Append(" It is a hard mod: enabling it asks you to allow its native code.");
		}

		if (result.MissingDependencies.Count != 0)
		{
			message.Append(" It needs: ").Append(string.Join("; ", result.MissingDependencies)).Append('.');
		}

		foreach (string warning in result.Warnings)
		{
			message.Append(' ').Append(warning);
		}

		// The installer wrote the order; read it back so the save below recomposes from it.
		_order = ModLoadOrder.Read(_modsRoot);
		WriteOrder(message.ToString());
	}

	private Node DialogHost() =>
		(Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;

	/// <summary>Progress from a worker thread, delivered on the main thread.</summary>
	private sealed class DeferredProgress : IProgress<float>
	{
		private readonly Action<float> _report;

		public DeferredProgress(Action<float> report) => _report = report;

		public void Report(float value) => Callable.From(() => _report(value)).CallDeferred();
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
				_statusMessage = $"Created '{modName}{Refii.Core.Engine_Title.Active.ModExtension}'.";
				_order.RegisterMod(modName, enabled: true);
				SaveEnabledMods();
				return;
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
		if (DeferRowsOnPhone()) return;
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
		foreach (string name in _order.Order)
		{
			ModInstall? mod = _mods.Find(installed => string.Equals(installed.Name, name, StringComparison.OrdinalIgnoreCase));
			if (mod is not null)
			{
				ordered.Add(mod);
			}
		}

		foreach (ModInstall mod in _mods)
		{
			if (!ordered.Contains(mod))
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

		int enabledCounter = 0;
		for (int i = 0; i < ordered.Count; i++)
		{
			ModInstall mod = ordered[i];
			bool enabled = _order.Enabled.Contains(mod.Name, StringComparer.OrdinalIgnoreCase);
			int? rank = enabled ? ++enabledCounter : null;
			AddRow(grid, mod, i, ordered.Count, rank);
		}
	}

	private void AddRow(GridContainer grid, ModInstall mod, int rowIndex, int totalRows, int? displayRank)
	{
		bool enabled = displayRank.HasValue;

		var check = new CheckBox { ButtonPressed = enabled };
		Apply(check);
		check.CustomMinimumSize = Hit(new Vector2(24, 24));
		ModInstall captured = mod;
		check.Toggled += pressed =>
		{
			if (pressed)
			{
				List<ModDependencyProblem> needs = ModDependencies.CheckOne(captured, _order, _mods);
				if (needs.Count > 0)
				{
					check.SetPressedNoSignal(false);
					_statusMessage = $"'{captured.Name}' cannot be enabled: " +
						string.Join("; ", needs.Select(need => need.Message)) + ".";
					Rebuild();
					return;
				}
			}

			if (pressed && RefuseNativeModOnPhone(check, captured)) return;

			if (pressed && _native.TryGetValue(captured.Name, out ModNativeSources? native) &&
				!NativeModConsent.IsGranted(native))
			{
				check.SetPressedNoSignal(false);
				AskNativeConsent(native, () =>
				{
					if (!_order.Enabled.Contains(captured.Name, StringComparer.OrdinalIgnoreCase))
					{
						_order.Enabled.Add(captured.Name);
					}

					SaveEnabledMods();
				});
				return;
			}

			if (pressed)
			{
				if (!_order.Enabled.Contains(captured.Name, StringComparer.OrdinalIgnoreCase))
				{
					_order.Enabled.Add(captured.Name);
				}
			}
			else
			{
				_order.Enabled.RemoveAll(name => string.Equals(name, captured.Name, StringComparison.OrdinalIgnoreCase));
			}

			SaveEnabledMods();
		};
		grid.AddChild(check);

		var orderBox = new HBoxContainer();
		orderBox.AddThemeConstantOverride("separation", 3);

		Label rankLabel = MakeLabel(displayRank.HasValue ? displayRank.Value.ToString() : "-");
		rankLabel.CustomMinimumSize = new Vector2(16, 0);
		rankLabel.HorizontalAlignment = HorizontalAlignment.Center;
		orderBox.AddChild(rankLabel);

		var up = new Button { Text = "▲", Disabled = rowIndex == 0 };
		Apply(up);
		up.CustomMinimumSize = Hit(new Vector2(20, 20));
		int upIndex = rowIndex;
		up.Pressed += () =>
		{
			if (upIndex > 0 && upIndex < _order.Order.Count)
			{
				(_order.Order[upIndex], _order.Order[upIndex - 1]) =
					(_order.Order[upIndex - 1], _order.Order[upIndex]);
				SaveEnabledMods();
			}
		};
		orderBox.AddChild(up);

		var down = new Button { Text = "▼", Disabled = rowIndex >= totalRows - 1 };
		Apply(down);
		down.CustomMinimumSize = Hit(new Vector2(20, 20));
		int downIndex = rowIndex;
		down.Pressed += () =>
		{
			if (downIndex >= 0 && downIndex < _order.Order.Count - 1)
			{
				(_order.Order[downIndex], _order.Order[downIndex + 1]) =
					(_order.Order[downIndex + 1], _order.Order[downIndex]);
				SaveEnabledMods();
			}
		};
		orderBox.AddChild(down);

		grid.AddChild(orderBox);

		// mod.toml's name and description may name the mod's text ({KEY}): its own tables, in the install's culture.
		string displayName = ModText.TitleOf(mod);
		string label = mod.Metadata.Version.Length == 0
			? displayName
			: displayName + " (" + mod.Metadata.Version + ")";
		_native.TryGetValue(mod.Name, out ModNativeSources? nativeSources);
		if (nativeSources != null)
		{
			label += "  ⚙ native";
		}

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
			sb.AppendLine(ModText.ForMod(mod, mod.Metadata.Description));
		}
		if (mod.Metadata.Requires.Count != 0)
		{
			sb.AppendLine("Requires: " + string.Join(", ", mod.Metadata.Requires.Select(r => r.ToString())));
		}
		if (nativeSources != null)
		{
			sb.AppendLine();
			sb.AppendLine("Native code — hooks these game functions:");
			foreach (string function in nativeSources.Overrides)
			{
				string line = "  • " + function;
				if (nativeSources.Replaces.Contains(function))
				{
					line += " (replaces the game's body)";
				}
				ModNativeChain? chain = _nativeChains.FirstOrDefault(c => c.Function == function);
				if (chain != null)
				{
					line += " — shared: " + string.Join(" → ", chain.Owners) + " → game";
				}
				sb.AppendLine(line);
			}
		}
		nameLabel.TooltipText = sb.ToString().TrimEnd();
		grid.AddChild(nameLabel);

		// CONFLICTS / STATUS
		Control statusControl;
		List<ModNativeConflict> nativeConflicts = enabled
			? _nativeConflicts.Where(conflict =>
				string.Equals(conflict.First, mod.Name, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(conflict.Second, mod.Name, StringComparison.OrdinalIgnoreCase)).ToList()
			: new List<ModNativeConflict>();
		_tallies.TryGetValue(mod.Name, out ModTally? tally);
		if (Phone && nativeSources != null)
		{
			statusControl = NotOnPhoneLabel();
		}
		else if (enabled && nativeSources != null && !NativeModConsent.IsGranted(nativeSources))
		{
			// ENABLED BUT NOT CONSENTED: enabled before it carried native code, or before an update
			// changed it. Its data and Lua load; its native code is withheld from the build until
			// the player says yes here.
			var allow = new Button { Text = "⚙ Allow native" };
			Apply(allow);
			allow.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.2f));
			allow.TooltipText = "This mod's native code is not compiled into the game until you allow it.";
			ModNativeSources pending = nativeSources;
			allow.Pressed += () => AskNativeConsent(pending, SaveEnabledMods);
			statusControl = allow;
		}
		else if (enabled && _dependencyProblems.Any(problem =>
			string.Equals(problem.Mod, mod.Name, StringComparison.OrdinalIgnoreCase)))
		{
			var needsLabel = MakeLabel("⛔ needs");
			needsLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.3f, 0.25f));
			needsLabel.HorizontalAlignment = HorizontalAlignment.Center;
			needsLabel.CustomMinimumSize = new Vector2(60, 0);
			needsLabel.MouseFilter = MouseFilterEnum.Pass;
			var tip = new System.Text.StringBuilder();
			tip.AppendLine("This mod needs another mod that is not there. Its content still loads, but");
			tip.AppendLine("the game code build refuses while a native mod's requirement is unmet:");
			foreach (ModDependencyProblem problem in _dependencyProblems.Where(problem =>
				string.Equals(problem.Mod, mod.Name, StringComparison.OrdinalIgnoreCase)))
			{
				tip.AppendLine("  • " + problem.Message);
			}
			needsLabel.TooltipText = tip.ToString().TrimEnd();
			statusControl = needsLabel;
		}
		else if (nativeConflicts.Count > 0)
		{
			// A hook that never calls the game's body swallows every hook beneath it. Sharing a
			// function is otherwise a chain (ModNative), but this no order resolves: the game code
			// build refuses, so the row says so before Play does.
			var blockedLabel = MakeLabel("⛔ native");
			blockedLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.3f, 0.25f));
			blockedLabel.HorizontalAlignment = HorizontalAlignment.Center;
			blockedLabel.CustomMinimumSize = new Vector2(60, 0);
			blockedLabel.MouseFilter = MouseFilterEnum.Pass;
			var tip = new System.Text.StringBuilder();
			tip.AppendLine("A mod replaces the body of a game function that another enabled mod (or refii) also hooks,");
			tip.AppendLine("so that hook would never run. The game code cannot be built until one of them is disabled:");
			foreach (ModNativeConflict conflict in nativeConflicts)
			{
				tip.AppendLine($"  • {conflict.Function}: {conflict.First} replaces it over {conflict.Second}");
			}
			blockedLabel.TooltipText = tip.ToString().TrimEnd();
			statusControl = blockedLabel;
		}
		else if (tally is not null && (tally.Lost > 0 || tally.OverriddenRecords > 0))
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
			var priorityBtn = new Button { Text = "✓ Priority" };
			Apply(priorityBtn);
			priorityBtn.AddThemeColorOverride("font_color", new Color(0.35f, 0.85f, 0.35f)); // Light green
			priorityBtn.CustomMinimumSize = new Vector2(60, 22);

			var tip = new System.Text.StringBuilder();
			tip.AppendLine($"[Modding Priority] {tally.Conflicts} collision(s) resolved in favor of '{mod.Name}':");
			foreach (string detail in tally.WonConflictDetails)
			{
				tip.AppendLine($"  • {detail}");
			}
			if (tally.WonConflictDetails.Count == 0)
			{
				tip.AppendLine("  • Priority over colliding changes in lower-priority mods.");
			}
			tip.AppendLine();
			tip.Append("Click to view details of prioritized modifications.");
			priorityBtn.TooltipText = tip.ToString();

			ModInstall modCaptured = mod;
			ModTally tallyCaptured = tally;
			priorityBtn.Pressed += () => ShowConflictDetailsDialog(modCaptured, tallyCaptured);
			statusControl = priorityBtn;
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
		deleteBtn.CustomMinimumSize = Hit(new Vector2(28, 22));
		deleteBtn.TooltipText = $"Delete '{mod.Name}{Refii.Core.Engine_Title.Active.ModExtension}' package.";
		ModInstall targetMod = mod;
		deleteBtn.Pressed += () => OnDeleteMod(targetMod);
		actionsBox.AddChild(deleteBtn);

		grid.AddChild(actionsBox);
	}

	/// <summary>The one-time native code prompt; <paramref name="onGranted"/> runs only on consent.</summary>
	private void AskNativeConsent(ModNativeSources native, Action onGranted)
	{
		var dialog = new ConfirmationDialog
		{
			Title = "This mod contains native code",
			DialogText = NativeModConsent.PromptText(native),
			OkButtonText = "Enable",
			CancelButtonText = "Cancel",
			DialogAutowrap = true,
			MinSize = new Vector2I(520, 0),
		};
		dialog.Confirmed += () =>
		{
			NativeModConsent.Grant(native);
			onGranted();
		};
		dialog.VisibilityChanged += () =>
		{
			if (!dialog.Visible)
			{
				dialog.QueueFree();
			}
		};
		Node top = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
		top.AddChild(dialog);
		dialog.PopupCentered();
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
