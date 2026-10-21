#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Refii.EditorTools;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// THE UI TAB of the Mod Studio — the active mod's UI files edited against the game's own UI:
/// <list type="bullet">
/// <item>HUD (<see cref="UiStudioHudView"/>): <c>ui/hud.json</c> — the mod's pieces, the game's pieces
/// it hides, scales, copies and moves, over the game's HUD view.</item>
/// <item>Menus (<see cref="UiStudioMenusView"/>): the mod's menus and patches and its screens, over
/// the game's list view.</item>
/// <item>Modals (<see cref="UiStudioModalsView"/>): who answers each box.</item>
/// <item>Settings (<see cref="UiStudioSettingsView"/>): the mod's options page.</item>
/// </list>
/// Each page shows the schema check (<see cref="Refii.Mods.ModValidate"/>) of its file as it would be
/// saved and the conflicts with the other enabled mods the engine's composition records. Save
/// writes every changed entry through <see cref="RefiiModApi"/> — the calls a script makes — and
/// the mods re-mount once.
/// </summary>
[Tool]
public partial class UiStudioView : MarginContainer
{
	private readonly UiStudioDocs _docs = new();
	private UiStudioPreview _preview = null!;
	private TabContainer _tabs = null!;
	private UiStudioHudView _hud = null!;
	private UiStudioMenusView _menus = null!;
	private UiStudioModalsView _modals = null!;
	private UiStudioSettingsView _settings = null!;
	private Label _modLabel = null!;
	private Label _status = null!;
	private VBoxContainer _issues = null!;
	private Timer _issuesTimer = null!;
	private int _activeTab = -1;
	private bool _loaded;

	private const int HudTab = 0;
	private const int MenusTab = 1;
	private const int ModalsTab = 2;
	private const int SettingsTab = 3;
	private static readonly string[] TabNames = { "🎯 HUD", "📜 Menus", "💬 Modals", "⚙ Settings" };

	public override void _Ready()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.ExpandFill;

		var split = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(split);

		var left = new VBoxContainer { CustomMinimumSize = new Vector2(500, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
		left.AddThemeConstantOverride("separation", 8);
		split.AddChild(left);

		var header = new HBoxContainer();
		_modLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = new Color(0.40f, 0.70f, 1.0f) };
		header.AddChild(_modLabel);
		var reload = new Button { Text = "🔄 Reload", TooltipText = "Read the mod's UI files again and reload the preview; unsaved edits are dropped" };
		reload.Pressed += () =>
		{
			_preview.ResetHost();
			Load();
			ActivateTab(_tabs.CurrentTab, force: true);
		};
		header.AddChild(reload);
		var save = new Button { Text = "💾 Save UI", TooltipText = "Write every changed entry into the active mod — through the editor API, checked, re-mounted once" };
		save.Pressed += Save;
		header.AddChild(save);
		left.AddChild(header);

		// The preview is built first: the pages bind to it.
		var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(360, 0) };
		_preview = new UiStudioPreview();
		right.AddChild(_preview);

		_tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		_hud = new UiStudioHudView(_docs, _preview) { Name = "HUD" };
		_menus = new UiStudioMenusView(_docs, _preview) { Name = "Menus" };
		_modals = new UiStudioModalsView(_docs) { Name = "Modals" };
		_settings = new UiStudioSettingsView(_docs, _preview) { Name = "Settings" };
		foreach (Control page in new Control[] { _hud, _menus, _modals, _settings })
		{
			page.SizeFlagsVertical = SizeFlags.ExpandFill;
			_tabs.AddChild(page);
		}

		for (int i = 0; i < TabNames.Length; i++)
		{
			_tabs.SetTabTitle(i, TabNames[i]);
		}

		_tabs.TabChanged += tab =>
		{
			if (IsVisibleInTree())
			{
				ActivateTab((int)tab, force: false);
			}
		};
		left.AddChild(_tabs);

		_hud.Edited += Touched;
		_menus.Edited += Touched;
		_modals.Edited += Touched;
		_settings.Edited += Touched;

		var issuesBody = new VBoxContainer();
		_issues = new VBoxContainer();
		var issuesScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 90), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		issuesScroll.AddChild(_issues);
		_issues.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		issuesBody.AddChild(issuesScroll);
		left.AddChild(ModStudioUiStyles.Card("🔎 Check and conflicts", new Color(0.95f, 0.80f, 0.45f), issuesBody));

		_status = ModStudioUiStyles.Note(string.Empty);
		left.AddChild(_status);

		split.AddChild(right);

		_issuesTimer = new Timer { OneShot = true, WaitTime = 0.4 };
		_issuesTimer.Timeout += ShowIssues;
		AddChild(_issuesTimer);

		VisibilityChanged += () =>
		{
			if (IsVisibleInTree())
			{
				if (!_loaded)
				{
					Load();
				}

				ActivateTab(_tabs.CurrentTab, force: true);
			}
			else
			{
				DeactivateTab();
			}
		};
	}

	/// <summary>The hub's refresh: another mod became active, or the mod's files changed outside the tab.</summary>
	public void Refresh()
	{
		if (!IsNodeReady() || !_loaded)
		{
			return;
		}

		if (!_docs.Stale())
		{
			return;
		}

		bool sameMod = string.Equals(_docs.Api.GetActiveMod(), _docs.Mod, StringComparison.OrdinalIgnoreCase);
		if (sameMod && Dirty())
		{
			SetStatus("The mod's files changed on disk; your unsaved UI edits are kept — Reload to take the files instead.");
			return;
		}

		Load();
		if (IsVisibleInTree())
		{
			ActivateTab(_tabs.CurrentTab, force: true);
		}
	}

	/// <summary>Opens a page: 0 HUD, 1 Menus, 2 Modals, 3 Settings.</summary>
	public void ShowPage(int page)
	{
		_tabs.CurrentTab = Math.Clamp(page, 0, TabNames.Length - 1);
	}

	private void Load()
	{
		_loaded = true;
		_docs.Load();
		_modLabel.Text = _docs.Mod.Length == 0 ? "No active mod — create or choose one first." : $"UI of '{_docs.Mod}'";
		_hud.Reload();
		_menus.Reload();
		_modals.Reload();
		_settings.Reload();
		Marks();
		_issuesTimer.Start();
	}

	private bool Dirty() =>
		_docs.HudChanged || _docs.TemplatesChanged || _docs.SettingsChanged || _menus.Editor.ChangedRecords().Count != 0;

	private void ActivateTab(int tab, bool force)
	{
		if (tab == _activeTab && !force)
		{
			return;
		}

		DeactivateTab();
		_activeTab = tab;
		switch (tab)
		{
			case HudTab:
				_hud.Activate();
				break;
			case MenusTab:
				_menus.Activate();
				break;
			case SettingsTab:
				_settings.Activate();
				break;
			case ModalsTab:
				_preview.SetStatus("A box is the game's own; who answers it has no picture. The records are listed on the left.");
				break;
		}

		ShowIssues();
	}

	private void DeactivateTab()
	{
		switch (_activeTab)
		{
			case HudTab:
				_hud.Deactivate();
				break;
			case MenusTab:
				_menus.Deactivate();
				break;
			case SettingsTab:
				_settings.Deactivate();
				break;
		}

		_activeTab = -1;
	}

	private void Touched()
	{
		Marks();
		_issuesTimer.Start();
	}

	/// <summary>A • on each page whose file has unsaved edits.</summary>
	private void Marks()
	{
		bool[] dirty =
		{
			_docs.HudChanged,
			_menus.Editor.ChangedRecords().Count != 0 || _docs.ScreensChanged,
			_docs.ModalsChanged,
			_docs.SettingsChanged,
		};
		for (int i = 0; i < TabNames.Length; i++)
		{
			_tabs.SetTabTitle(i, TabNames[i] + (dirty[i] ? " •" : string.Empty));
		}
	}

	/// <summary>The current page's check and conflicts, as its file would be saved now.</summary>
	private void ShowIssues()
	{
		foreach (Node child in _issues.GetChildren())
		{
			child.QueueFree();
		}

		if (_docs.Mod.Length == 0)
		{
			return;
		}

		var lines = new List<(bool Error, string Text)>();
		switch (_tabs.CurrentTab)
		{
			case HudTab:
				lines.AddRange(_hud.Issues());
				break;
			case MenusTab:
			case ModalsTab:
			{
				string text = _docs.TemplatesText(_menus.Editor.ChangedRecords());
				IReadOnlyList<Engine_UiTemplates.ModConflict> conflicts =
					Engine_UiTemplates.Compose(_docs.Files(Engine_UiTemplates.ModTemplatesPath, text)).Conflicts;
				lines.AddRange(_tabs.CurrentTab == MenusTab ? _menus.Issues(text, conflicts) : _modals.Issues(text, conflicts));
				break;
			}

			case SettingsTab:
				lines.AddRange(_settings.Issues());
				break;
		}

		if (lines.Count == 0)
		{
			var clean = ModStudioUiStyles.Note("✓ Clean, and no conflicts with the enabled mods.");
			clean.Modulate = ModStudioUiStyles.CleanColour;
			_issues.AddChild(clean);
			return;
		}

		foreach ((bool error, string text) in lines.Distinct())
		{
			var line = ModStudioUiStyles.Note((error ? "✖ " : "⚠ ") + text);
			line.Modulate = error ? ModStudioUiStyles.ErrorColour : ModStudioUiStyles.WarningColour;
			_issues.AddChild(line);
		}
	}

	/// <summary>A composition conflict in words, from the active mod's side; null when the mod is not in it.</summary>
	public static string? DescribeConflict(Engine_UiTemplates.ModConflict conflict, string mod)
	{
		if (string.Equals(conflict.Winner, mod, StringComparison.OrdinalIgnoreCase))
		{
			return $"{conflict.Piece}: this mod's statement wins over mod '{conflict.Loser}'";
		}

		return string.Equals(conflict.Loser, mod, StringComparison.OrdinalIgnoreCase)
			? $"{conflict.Piece}: mod '{conflict.Winner}' (higher in the load order) replaces this mod's statement"
			: null;
	}

	/// <summary>Writes every changed entry of every page; the mods re-mount once at the end.</summary>
	private void Save()
	{
		if (_docs.Mod.Length == 0)
		{
			SetStatus("No active mod to save into.");
			return;
		}

		bool hud = _docs.HudChanged;
		bool templates = _docs.TemplatesChanged;
		bool settings = _docs.SettingsChanged;
		int menus = _menus.Editor.ChangedRecords().Count;
		if (!hud && !templates && !settings && menus == 0)
		{
			SetStatus("Nothing to save.");
			return;
		}

		string error = string.Empty;
		_docs.Api.BeginChanges();
		try
		{
			if (hud)
			{
				error = _docs.SaveHud();
			}

			if (error.Length == 0 && templates)
			{
				error = _docs.SaveTemplates();
			}

			if (error.Length == 0 && settings)
			{
				error = _docs.SaveSettings();
			}

			if (error.Length == 0 && menus != 0)
			{
				error = _menus.Editor.Save();
			}
		}
		finally
		{
			_docs.Api.EndChanges();
		}

		if (error.Length != 0)
		{
			SetStatus("⚠ " + error);
			Marks();
			return;
		}

		Load();
		ActivateTab(_tabs.CurrentTab, force: true);
		SetStatus($"Saved into '{_docs.Mod}'" +
			(hud ? " — ui/hud.json" : string.Empty) +
			(templates || menus != 0 ? " — ui/templates.json" : string.Empty) +
			(settings ? " — settings.ui.json" : string.Empty) +
			". The game reads them when it builds its UI.");
	}

	private void SetStatus(string text) => _status.Text = text;
}
#endif
