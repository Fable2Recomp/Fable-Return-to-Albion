using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// Modernized tabbed launcher settings panel with progressive disclosure, a pinned Look &amp;
/// Performance Preset hero card, instant cross-tab search, contextual sub-rows, and interactive
/// click-to-bind keybindings.
/// </summary>
[Tool]
public partial class SettingsPanel : VBoxContainer
{
	private readonly RuntimeSettingsModel _model = new();
	private Font? _font;
	private int _fontSize = 18;
	private SmartStyle _style = SmartStyle.FromTheme(
		new Color(0.02f, 0.1f, 0.2f, 1f), new Color(0.2f, 0.8f, 0.4f, 1f));

	// Retained for shared layout scaling and Android's SettingsPanel.GraphicsPreset.cs partial.
	private const int BaseNameColumnWidth = 145;
	private const int BaseDefaultColumnWidth = 75;
	private const int BaseEditorColumnWidth = 80;

	// Uniform right-hand control column width so dropdowns, sliders, inputs, toggles, and keycap slots align cleanly.
	internal static int ControlColumnWidth => LauncherScale.Px(196);
	private static int SliderReadoutWidth => LauncherScale.Px(62);

	private readonly HashSet<SettingsTab> _expandedAdvancedTabs = new();

	private Label _statusLabel = null!;
	private LineEdit _filterEdit = null!;
	private MenuButton _actionsMenu = null!;
	private HBoxContainer _tabBar = null!;
	private PanelContainer _searchBanner = null!;
	private Label _searchBannerLabel = null!;
	private OptionButton? _lookOption;
	private OptionButton? _desktopPresetOption;
	private Label? _desktopPresetSummary;
	private VBoxContainer _list = null!;
	private ScrollContainer _scroll = null!;
	private bool _built;
	private bool _syncingHero;

	private const int ActionReload = 1;
	private const int ActionResetTab = 2;
	private const int ActionResetAll = 3;
	private const int ActionToggleDev = 4;
	private const int ActionCopyPath = 5;

	/// <summary>Configures fonts and theme colors and (re)builds the settings panel UI.</summary>
	public void Configure(Font? font, int fontSize, SmartStyle style)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;

		CancelKeyCapture();
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

	/// <summary>Writes any pending edits to <c>refii.toml</c>.</summary>
	public void SaveIfDirty() => _model.Save();

	/// <summary>Reloads the active title's configuration file when the selected title changes.</summary>
	public void ReloadForTitle()
	{
		if (!_built)
		{
			return;
		}

		CancelKeyCapture();
		_model.Reload();
	}

	private void Build()
	{
		if (_built)
		{
			return;
		}

		_built = true;
		SetProcessInput(true);
		AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		_model.Initialize();

		if (_model.ActiveTab == SettingsTab.Developer && !_model.ShowDeveloperSettings)
		{
			_model.ActiveTab = SettingsTab.Display;
		}

		BuildTopChrome();
		Rebuild();
	}

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Button or CheckBox or CheckButton or LineEdit or SpinBox or OptionButton or HSlider)
		{
			control.MouseDefaultCursorShape = CursorShape.PointingHand;
		}
		if (control is Slider slider)
		{
			slider.Scrollable = false;
		}
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}

	private void BuildTopChrome()
	{
		// 1. Search + Actions Toolbar
		var searchRow = new HBoxContainer();
		searchRow.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		AddChild(searchRow);

		_filterEdit = new LineEdit
		{
			PlaceholderText = "Search all settings...",
			ClearButtonEnabled = true,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		Apply(_filterEdit);
		StyleSearchBox(_filterEdit);
		_filterEdit.Text = _model.Filter;
		_filterEdit.TextChanged += text =>
		{
			CancelKeyCapture();
			_model.Filter = text;
			Rebuild();
		};
		searchRow.AddChild(_filterEdit);

		_actionsMenu = new MenuButton
		{
			Text = "Actions \u25BE",
			Flat = false,
		};
		StyleSecondaryButton(_actionsMenu);
		PopupMenu popup = _actionsMenu.GetPopup();
		StylePopupMenu(popup);
		popup.IdPressed += OnActionMenuPressed;
		searchRow.AddChild(_actionsMenu);
		RefreshActionsMenu();

		// 2. Sub-Tab Bar (Display | Graphics | Gameplay | Controls | Keys [+ Dev])
		_tabBar = new HBoxContainer();
		_tabBar.AddThemeConstantOverride("separation", 4);
		AddChild(_tabBar);

		// 3. Active Search Banner (replaces tab bar while searching)
		_searchBanner = new PanelContainer { Visible = false };
		var bannerStyle = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Gold, 0.14f),
			BorderColor = new Color(LauncherTokens.Gold, 0.45f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp2,
			ContentMarginRight = LauncherTokens.Sp2,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		bannerStyle.SetBorderWidthAll(1);
		_searchBanner.AddThemeStyleboxOverride("panel", bannerStyle);

		var bannerRow = new HBoxContainer();
		bannerRow.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		_searchBanner.AddChild(bannerRow);

		_searchBannerLabel = MakeLabel(string.Empty);
		_searchBannerLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_searchBannerLabel.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 2));
		_searchBannerLabel.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		_searchBannerLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		bannerRow.AddChild(_searchBannerLabel);

		var clearSearchBtn = new Button { Text = "Clear" };
		StyleCompactButton(clearSearchBtn);
		clearSearchBtn.Pressed += () =>
		{
			_filterEdit.Text = string.Empty;
			_model.Filter = string.Empty;
			Rebuild();
		};
		bannerRow.AddChild(clearSearchBtn);
		AddChild(_searchBanner);

		// 4. Scrollable Settings Body
		var listBackground = new PanelContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		var listBox = new StyleBoxFlat
		{
			BgColor = _style.ChildBg,
			BorderColor = _style.Border,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 8,
			ContentMarginBottom = 8,
		};
		listBox.SetBorderWidthAll(1);
		listBackground.AddThemeStyleboxOverride("panel", listBox);
		AddChild(listBackground);

		_scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		listBackground.AddChild(_scroll);

		var scrollMargin = new MarginContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		scrollMargin.AddThemeConstantOverride("margin_left", LauncherScale.Px(2));
		scrollMargin.AddThemeConstantOverride("margin_top", LauncherScale.Px(2));
		scrollMargin.AddThemeConstantOverride("margin_right", LauncherScale.Px(14));
		scrollMargin.AddThemeConstantOverride("margin_bottom", LauncherScale.Px(4));
		_scroll.AddChild(scrollMargin);

		_list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_list.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		scrollMargin.AddChild(_list);

		// 5. Compact Footer Status Line
		_statusLabel = MakeLabel(
			"Changes save automatically; some graphics options apply next launch.");
		_statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_statusLabel.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
		_statusLabel.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
		AddChild(_statusLabel);
	}

	private void RefreshActionsMenu()
	{
		PopupMenu popup = _actionsMenu.GetPopup();
		popup.Clear();
		popup.AddItem("Reload from disk", ActionReload);
		popup.AddItem($"Reset {DesktopSettingMetadata.TabFullName(_model.ActiveTab)} tab", ActionResetTab);
		popup.AddItem("Reset all settings to defaults", ActionResetAll);
		popup.AddSeparator();
		popup.AddCheckItem("Show Debug & Profiling Tab", ActionToggleDev);
		int devIdx = popup.GetItemIndex(ActionToggleDev);
		if (devIdx >= 0)
		{
			popup.SetItemChecked(devIdx, _model.ShowDeveloperSettings);
		}
		if (ShowsConfigPath)
		{
			popup.AddSeparator();
			popup.AddItem("Copy config file paths", ActionCopyPath);
		}
	}

	private void OnActionMenuPressed(long id)
	{
		CancelKeyCapture();
		switch ((int)id)
		{
			case ActionReload:
				_model.Reload();
				_filterEdit.Text = _model.Filter;
				UpdateStatusText(_model.StatusMessage.Length != 0 ? _model.StatusMessage : "Reloaded settings from disk.");
				Rebuild();
				break;

			case ActionResetTab:
				_model.ResetTabToDefaults(_model.ActiveTab);
				UpdateStatusText($"Reset {DesktopSettingMetadata.TabFullName(_model.ActiveTab)} settings to defaults.");
				Rebuild();
				break;

			case ActionResetAll:
				_model.ResetAllToDefaults();
				UpdateStatusText("Reset all settings to defaults.");
				Rebuild();
				break;

			case ActionToggleDev:
				_model.ShowDeveloperSettings = !_model.ShowDeveloperSettings;
				if (!_model.ShowDeveloperSettings && _model.ActiveTab == SettingsTab.Developer)
				{
					_model.ActiveTab = SettingsTab.Display;
				}
				Rebuild();
				break;

			case ActionCopyPath:
			{
				string paths = string.IsNullOrEmpty(_model.SecondaryConfigPath)
					? _model.ConfigPath
					: _model.ConfigPath + "\n" + _model.SecondaryConfigPath;
				DisplayServer.ClipboardSet(paths);
				UpdateStatusText("Copied config paths: " + _model.ConfigPath + " & " + _model.SecondaryConfigPath);
				break;
			}
		}
	}

	private Control? MakeOwnerTitleBadge(string? ownerTitle)
	{
		if (string.IsNullOrEmpty(ownerTitle))
		{
			return null;
		}

		string displayName = ownerTitle!;
		foreach (Refii.Core.Engine_Title title in Refii.Core.Engine_Title.All)
		{
			if (string.Equals(title.Folder, ownerTitle, StringComparison.Ordinal))
			{
				displayName = title.Name;
				break;
			}
		}

		var pill = new PanelContainer
		{
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
			TooltipText = $"Applies specifically to {displayName} ({(ownerTitle == "Fable3" ? "refiii.toml" : "refii.toml")}).",
		};
		var style = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Gold, 0.14f),
			BorderColor = new Color(LauncherTokens.Gold, 0.45f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 1,
			ContentMarginBottom = 1,
		};
		style.SetBorderWidthAll(1);
		pill.AddThemeStyleboxOverride("panel", style);

		var label = MakeLabel(displayName);
		label.AddThemeFontSizeOverride("font_size", Math.Max(10, _fontSize - 4));
		label.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
		pill.AddChild(label);
		return pill;
	}

	private void UpdateStatusText(string? message)
	{
		if (_statusLabel is null || !IsInstanceValid(_statusLabel))
		{
			return;
		}

		_statusLabel.Text = !string.IsNullOrEmpty(message)
			? message!
			: "Changes save automatically; some graphics options apply next launch.";
	}

	private void RebuildTabButtons()
	{
		foreach (Node child in _tabBar.GetChildren())
		{
			child.QueueFree();
		}

		SettingsTab active = _model.ActiveTab;
		foreach (SettingsTab tab in Enum.GetValues<SettingsTab>())
		{
			if (tab == SettingsTab.Developer && !_model.ShowDeveloperSettings)
			{
				continue;
			}

			bool isSelected = tab == active;
			var btn = new Button
			{
				Text = DesktopSettingMetadata.TabTitle(tab),
				TooltipText = DesktopSettingMetadata.TabFullName(tab),
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				ClipText = true,
			};
			Apply(btn);
			btn.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 2));
			StyleTabButton(btn, isSelected);
			SettingsTab captured = tab;
			btn.Pressed += () =>
			{
				if (_model.ActiveTab == captured)
				{
					return;
				}

				CancelKeyCapture();
				_model.ActiveTab = captured;
				_scroll.ScrollVertical = 0;
				Rebuild();
			};
			_tabBar.AddChild(btn);
		}
	}

	private void Rebuild()
	{
		CancelKeyCapture();
		RefreshActionsMenu();
		if (!string.IsNullOrEmpty(_model.StatusMessage))
		{
			UpdateStatusText(_model.StatusMessage);
		}

		foreach (Node child in _list.GetChildren())
		{
			child.QueueFree();
		}

		_lookOption = null;
		_desktopPresetOption = null;
		_desktopPresetSummary = null;

		string query = (_model.Filter ?? string.Empty).Trim();
		bool searching = query.Length != 0;
		_tabBar.Visible = !searching;
		_searchBanner.Visible = searching;

		if (searching)
		{
			BuildSearchResults(query);
			return;
		}

		RebuildTabButtons();
		SettingsTab activeTab = _model.ActiveTab;

		// Hero Card on Display & Graphics tabs
		if (activeTab is SettingsTab.Display or SettingsTab.Graphics)
		{
			BuildHeroPresetCard();
		}

		if (activeTab == SettingsTab.Keybindings)
		{
			BuildKeybindingsTab();
			return;
		}

		(var essential, var advanced) = _model.BuildTabSections(activeTab);

		// Inject composite Window Mode into Display -> Window & Monitor
		bool injectedWindowMode = false;
		foreach ((string section, List<RuntimeCVarDefinition> rows) in essential)
		{
			VBoxContainer card = BuildSectionCard(section);
			if (activeTab == SettingsTab.Display &&
			    section == "Window & Monitor" &&
			    DesktopGraphicsPresets.Offered &&
			    !injectedWindowMode)
			{
				AddWindowModeRow(card);
				injectedWindowMode = true;
			}

			foreach (RuntimeCVarDefinition def in rows)
			{
				AddModernSettingRow(card, def);
			}

			_list.AddChild(card);
		}

		if (advanced.Count != 0)
		{
			BuildAdvancedAccordion(activeTab, advanced);
		}
	}

	private void BuildSearchResults(string query)
	{
		List<(SettingsTab Tab, string Section, List<RuntimeCVarDefinition> Rows)> results =
			_model.SearchAll(query, _model.ShowDeveloperSettings);
		int matchCount = results.Sum(g => g.Rows.Count);
		_searchBannerLabel.Text = matchCount == 0
			? $"No settings match \"{query}\""
			: $"{matchCount} {(matchCount == 1 ? "setting matches" : "settings match")} \"{query}\"";

		if (matchCount == 0)
		{
			Label empty = MakeLabel("Try searching for \"FSR\", \"shadows\", \"FPS\", \"HDR\", \"coop\", or an action name.");
			empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			empty.AddThemeColorOverride("font_color", _style.TextDisabled);
			_list.AddChild(empty);
			return;
		}

		foreach ((SettingsTab tab, string section, List<RuntimeCVarDefinition> rows) in results)
		{
			VBoxContainer card = BuildSectionCard($"{DesktopSettingMetadata.TabFullName(tab)} \u203A {section}");
			foreach (RuntimeCVarDefinition def in rows)
			{
				if (tab == SettingsTab.Keybindings && def.Name.StartsWith("key_", StringComparison.Ordinal))
				{
					AddKeybindingRowForDefinition(card, def);
				}
				else
				{
					AddModernSettingRow(card, def);
				}
			}

			_list.AddChild(card);
		}
	}

	private void BuildHeroPresetCard()
	{
		bool showLook = Refii.AndroidRuntime.AndroidLauncherSettings.ShowsLook;
		bool showDesktopPreset = DesktopGraphicsPresets.Offered;
		bool showPhonePreset = GraphicsPresets.Offered;
		if (!showLook && !showDesktopPreset && !showPhonePreset)
		{
			return;
		}

		var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		var cardStyle = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = LauncherTokens.Gold,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 8,
			ContentMarginBottom = 8,
		};
		cardStyle.SetBorderWidthAll(1);
		card.AddThemeStyleboxOverride("panel", cardStyle);

		var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		col.AddThemeConstantOverride("separation", 6);
		card.AddChild(col);

		int heroDropdownWidth = Math.Max(LauncherScale.Px(140), ControlColumnWidth - 10);

		if (showLook)
		{
			var lookRow = new HBoxContainer();
			lookRow.AddThemeConstantOverride("separation", 8);
			col.AddChild(lookRow);

			Label lookLabel = MakeLabel("Visual Look");
			lookLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			lookLabel.VerticalAlignment = VerticalAlignment.Center;
			lookLabel.TooltipText =
				"Modern (Fable) uses remastered PBR lighting & materials with Fable 2's stylized tonemapper.\n" +
				"Modern (Filmic) uses remastered PBR lighting & materials with an ACES filmic curve.\n" +
				"Classic (Original) restores the original 2008 lighting, shaders, and un-modernized materials.";
			lookRow.AddChild(lookLabel);

			_lookOption = new OptionButton
			{
				FitToLongestItem = false,
				TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
				CustomMinimumSize = new Vector2(heroDropdownWidth, 0),
			};
			StyleDropdown(_lookOption);
			_lookOption.AddItem("Modern (Fable)", 0);
			_lookOption.AddItem("Modern (Filmic)", 1);
			_lookOption.AddItem("Classic (Original)", 2);
			_lookOption.AddItem("Custom", 3);
			_lookOption.ItemSelected += index =>
			{
				if (_syncingHero)
				{
					return;
				}

				string? chosen = index switch
				{
					0 => "modern_fable",
					1 => "modern_filmic",
					2 => "classic",
					_ => null,
				};
				if (chosen is null)
				{
					return;
				}

				_model.ApplyLook(chosen);
				UpdateStatusText(_model.StatusMessage);
				Rebuild();
			};
			lookRow.AddChild(_lookOption);
			RefreshLook();
		}

		if (showDesktopPreset)
		{
			var presetRow = new HBoxContainer();
			presetRow.AddThemeConstantOverride("separation", 8);
			col.AddChild(presetRow);

			Label presetLabel = MakeLabel("Performance Preset");
			presetLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			presetLabel.VerticalAlignment = VerticalAlignment.Center;
			presetLabel.TooltipText =
				"Adjusts resolution scaling, GI, sun & lamp shadows, volumetric fog, and LOD distances together.\n" +
				"Does NOT alter your Visual Look or HD Enhanced Textures.";
			presetRow.AddChild(presetLabel);

			_desktopPresetOption = new OptionButton
			{
				FitToLongestItem = false,
				TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
				CustomMinimumSize = new Vector2(heroDropdownWidth, 0),
			};
			StyleDropdown(_desktopPresetOption);
			_desktopPresetOption.AddItem("Low (iGPU)", 0);
			_desktopPresetOption.AddItem("Balanced", 1);
			_desktopPresetOption.AddItem("High", 2);
			_desktopPresetOption.AddItem("Ultra", 3);
			_desktopPresetOption.AddItem("Custom", 4);
			_desktopPresetOption.ItemSelected += index =>
			{
				if (_syncingHero)
				{
					return;
				}

				string? chosen = index switch
				{
					0 => DesktopGraphicsPresets.Low,
					1 => DesktopGraphicsPresets.Balanced,
					2 => DesktopGraphicsPresets.High,
					3 => DesktopGraphicsPresets.Ultra,
					_ => null,
				};
				if (chosen is null)
				{
					return;
				}

				_model.ApplyDesktopPreset(chosen);
				UpdateStatusText($"Applied {DesktopGraphicsPresets.Label(chosen)} performance preset.");
				Rebuild();
			};
			presetRow.AddChild(_desktopPresetOption);

			_desktopPresetSummary = MakeLabel(string.Empty);
			_desktopPresetSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_desktopPresetSummary.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
			_desktopPresetSummary.AddThemeColorOverride("font_color", LauncherTokens.SepiaInk);
			col.AddChild(_desktopPresetSummary);
			RefreshDesktopPresetHero();
		}
		else if (showPhonePreset)
		{
			RuntimeCVarDefinition? def = RuntimeCVarSet.Active.FirstOrDefault(d => d.Name == GraphicsPresets.Key);
			if (def is not null)
			{
				var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
				grid.AddThemeConstantOverride("h_separation", 8);
				col.AddChild(grid);
				AddGraphicsPresetRow(grid, def);
			}
		}

		_list.AddChild(card);
	}

	/// <summary>The Look dropdown follows the switches; "Custom" shows only while they disagree.</summary>
	private void RefreshLook()
	{
		if (_lookOption is null || !IsInstanceValid(_lookOption))
		{
			return;
		}

		_syncingHero = true;
		string look = _model.CurrentLook();
		int index = look switch
		{
			"modern_fable" or "modern" => 0,
			"modern_filmic" => 1,
			"classic" => 2,
			_ => 3,
		};
		_lookOption.SetItemDisabled(3, look != "custom");
		_lookOption.Selected = index;
		_syncingHero = false;
	}

	private void RefreshDesktopPresetHero()
	{
		if (_desktopPresetOption is null || !IsInstanceValid(_desktopPresetOption))
		{
			return;
		}

		_syncingHero = true;
		string preset = _model.CurrentDesktopPreset();
		int index = preset switch
		{
			DesktopGraphicsPresets.Low => 0,
			DesktopGraphicsPresets.Balanced => 1,
			DesktopGraphicsPresets.High => 2,
			DesktopGraphicsPresets.Ultra => 3,
			_ => 4,
		};
		_desktopPresetOption.SetItemDisabled(4, preset != DesktopGraphicsPresets.Custom);
		_desktopPresetOption.Selected = index;
		if (_desktopPresetSummary is not null && IsInstanceValid(_desktopPresetSummary))
		{
			_desktopPresetSummary.Text = DesktopGraphicsPresets.Summary(preset);
		}
		_syncingHero = false;
	}

	private VBoxContainer BuildSectionCard(string title)
	{
		var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		col.AddThemeConstantOverride("separation", 4);

		Label header = MakeLabel(title);
		header.AddThemeColorOverride("font_color", LauncherTokens.GoldInk);
		header.AddThemeFontSizeOverride("font_size", Math.Max(13, _fontSize - 1));
		col.AddChild(header);

		var sep = new HSeparator();
		col.AddChild(sep);
		return col;
	}

	private void BuildAdvancedAccordion(
		SettingsTab tab,
		List<(string Section, List<RuntimeCVarDefinition> Rows)> advanced)
	{
		int totalCount = advanced.Sum(s => s.Rows.Count);
		bool expanded = _expandedAdvancedTabs.Contains(tab);

		var toggleBtn = new Button
		{
			Text = $"{(expanded ? "\u25BE" : "\u25B8")} Advanced {DesktopSettingMetadata.TabFullName(tab)} Tuning ({totalCount})",
			Alignment = HorizontalAlignment.Left,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		StyleSecondaryButton(toggleBtn);
		toggleBtn.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 2));
		_list.AddChild(toggleBtn);

		var body = new VBoxContainer
		{
			Visible = expanded,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		body.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		_list.AddChild(body);

		if (expanded)
		{
			PopulateAdvancedSections(body, advanced);
		}

		toggleBtn.Pressed += () =>
		{
			bool nowExpanded = _expandedAdvancedTabs.Add(tab);
			if (!nowExpanded)
			{
				_expandedAdvancedTabs.Remove(tab);
			}

			toggleBtn.Text = $"{(nowExpanded ? "\u25BE" : "\u25B8")} Advanced {DesktopSettingMetadata.TabFullName(tab)} Tuning ({totalCount})";
			body.Visible = nowExpanded;
			if (nowExpanded && body.GetChildCount() == 0)
			{
				PopulateAdvancedSections(body, advanced);
			}
		};
	}

	private void PopulateAdvancedSections(
		VBoxContainer container,
		List<(string Section, List<RuntimeCVarDefinition> Rows)> advanced)
	{
		foreach ((string section, List<RuntimeCVarDefinition> rows) in advanced)
		{
			VBoxContainer card = BuildSectionCard(section);
			foreach (RuntimeCVarDefinition def in rows)
			{
				AddModernSettingRow(card, def);
			}

			container.AddChild(card);
		}
	}

	private void AddWindowModeRow(VBoxContainer parent)
	{
		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);

		Label label = MakeLabel("Window Mode");
		label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		label.VerticalAlignment = VerticalAlignment.Center;
		label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		label.TooltipText =
			"Windowed opens a normal resizable desktop window.\n" +
			"Borderless Windowed fills the screen with instant Alt-Tab.\n" +
			"Exclusive Fullscreen gives the display exclusively to the game.";
		row.AddChild(label);

		string currentMode = _model.GetWindowMode();
		var resetBtn = BuildInlineResetButton(currentMode != "windowed", () =>
		{
			_model.ApplyWindowMode("windowed");
			UpdateStatusText(_model.StatusMessage);
			Rebuild();
		}, "Windowed");
		row.AddChild(resetBtn);

		var opt = new OptionButton
		{
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
		};
		StyleDropdown(opt);
		opt.AddItem("Windowed", 0);
		opt.AddItem("Borderless Windowed", 1);
		opt.AddItem("Exclusive Fullscreen", 2);
		opt.Selected = currentMode switch
		{
			"borderless" => 1,
			"fullscreen" => 2,
			_ => 0,
		};
		opt.ItemSelected += idx =>
		{
			string mode = idx switch
			{
				1 => "borderless",
				2 => "fullscreen",
				_ => "windowed",
			};
			_model.ApplyWindowMode(mode);
			UpdateStatusText(_model.StatusMessage);
			Rebuild();
		};
		row.AddChild(opt);
	}

	private void AddModernSettingRow(VBoxContainer parent, RuntimeCVarDefinition definition)
	{
		if (definition.Name == GraphicsPresets.Key)
		{
			var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			grid.AddThemeConstantOverride("h_separation", 8);
			parent.AddChild(grid);
			AddGraphicsPresetRow(grid, definition);
			return;
		}

		SettingPresentation meta = DesktopSettingMetadata.Classify(definition);
		bool isContextualChild = meta.ContextualParentKey is not null && string.IsNullOrWhiteSpace(_model.Filter);

		var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);

		string displayLabel = isContextualChild ? "  \u21B3 " + meta.Label : meta.Label;
		Label name = MakeLabel(displayLabel);
		name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		name.VerticalAlignment = VerticalAlignment.Center;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 1));
		name.TooltipText = BuildTooltip(definition, meta);
		row.AddChild(name);

		if (MakeOwnerTitleBadge(definition.OwnerTitle) is { } ownerBadge)
		{
			row.AddChild(ownerBadge);
		}

		string current = _model.Values.TryGetValue(definition.Name, out string? v) ? v : definition.DefaultValue;
		string baseline = BaselineFor(definition);
		bool isModified = !DesktopGraphicsPresets.SameValue(current, baseline);

		Button resetBtn = BuildInlineResetButton(isModified, () =>
		{
			Commit(definition, baseline, forceRebuild: true);
		}, baseline);
		row.AddChild(resetBtn);

		// Special inline modal buttons on Lighting Quality and HDR Output
		if (definition.Name == "lighting_preset")
		{
			AddLightingPresetControls(row, definition);
			return;
		}

		if (definition.Name == "hdr_output")
		{
			AddHdrControls(row, definition, current);
			return;
		}

		List<RuntimeCVarOption> options = RuntimeSettingsModel.OptionsFor(definition, _model.Values);
		Control editor = BuildModernEditor(definition, meta, current, options, resetBtn, baseline);
		row.AddChild(editor);
	}

	private void AddLightingPresetControls(HBoxContainer row, RuntimeCVarDefinition definition)
	{
		var group = new HBoxContainer
		{
			CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
		};
		group.AddThemeConstantOverride("separation", LauncherScale.Px(6));
		row.AddChild(group);

		var fineTuneBtn = new Button { Text = "Fine-Tune..." };
		StyleCompactButton(fineTuneBtn);
		fineTuneBtn.TooltipText = "Open Advanced Lighting & Shadow Range tuning dialog.";
		fineTuneBtn.Pressed += () =>
		{
			var dialog = new LightingSettingsDialog(_model, _style, _font, _fontSize, () =>
			{
				_model.RefreshDesktopPreset();
				_model.RefreshGraphicsPreset();
				UpdateStatusText(_model.StatusMessage);
				Rebuild();
			});
			Node top = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
			top.AddChild(dialog);
		};
		group.AddChild(fineTuneBtn);

		List<RuntimeCVarOption> options = RuntimeSettingsModel.OptionsFor(definition, _model.Values);
		string activePreset = _model.CurrentLightingPreset();
		var opt = new OptionButton
		{
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(LauncherScale.Px(96), 0),
		};
		StyleDropdown(opt);

		int selected = 0;
		int customIdx = -1;
		for (int i = 0; i < options.Count; i++)
		{
			opt.AddItem(options[i].Label, i);
			if (options[i].Value == activePreset) selected = i;
			if (options[i].Value == "custom") customIdx = i;
		}

		if (customIdx >= 0)
		{
			opt.SetItemDisabled(customIdx, activePreset != "custom");
		}
		opt.Selected = selected;
		opt.ItemSelected += idx =>
		{
			string chosen = options[(int)idx].Value;
			if (chosen == "custom")
			{
				return;
			}

			_model.ApplyLightingPreset(chosen);
			UpdateStatusText(_model.StatusMessage);
			Rebuild();
		};
		group.AddChild(opt);
	}

	private void AddHdrControls(HBoxContainer row, RuntimeCVarDefinition definition, string current)
	{
		bool isEnabled = RuntimeConfig.NormalizeBoolValue(current) == "true";

		var group = new HBoxContainer
		{
			CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
		};
		group.AddThemeConstantOverride("separation", LauncherScale.Px(6));
		group.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
		row.AddChild(group);

		var calibrateBtn = new Button
		{
			Text = "Calibrate...",
			Disabled = !isEnabled,
		};
		StyleCompactButton(calibrateBtn);
		calibrateBtn.TooltipText = "Configure HDR paper-white reference and peak display nits.";
		calibrateBtn.Pressed += () =>
		{
			var dialog = new HdrSettingsDialog(_model, _style, _font, _fontSize, () =>
			{
				UpdateStatusText(_model.StatusMessage);
			});
			Node top = (Node?)GetTree()?.Root.GetNodeOrNull("Launcher") ?? (Node?)GetWindow() ?? this;
			top.AddChild(dialog);
		};
		group.AddChild(calibrateBtn);

		var toggle = new CheckButton { ButtonPressed = isEnabled };
		Apply(toggle);
		toggle.Toggled += pressed =>
		{
			calibrateBtn.Disabled = !pressed;
			Commit(definition, pressed ? "true" : "false");
		};
		group.AddChild(toggle);
	}

	private Control BuildModernEditor(
		RuntimeCVarDefinition definition,
		SettingPresentation meta,
		string current,
		List<RuntimeCVarOption> options,
		Button resetBtn,
		string baseline)
	{
		if (RuntimeSettingsModel.IsLockedRenderingDriver(definition))
		{
			OptionButton locked = BuildDropdown(definition, "vulkan", options);
			locked.Disabled = true;
			locked.TooltipText = "Direct3D 12 is available on Windows only.";
			return locked;
		}

		if (options.Count != 0)
		{
			return BuildDropdown(definition, current, options, resetBtn, baseline);
		}

		if (definition.Type == RuntimeCVarType.Bool)
		{
			var toggleBox = new HBoxContainer
			{
				CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
			};
			toggleBox.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

			var toggle = new CheckButton
			{
				ButtonPressed = RuntimeConfig.NormalizeBoolValue(current) == "true",
			};
			Apply(toggle);
			toggle.Toggled += pressed =>
			{
				string val = pressed ? "true" : "false";
				resetBtn.Modulate = DesktopGraphicsPresets.SameValue(val, baseline) ? Colors.Transparent : Colors.White;
				resetBtn.Disabled = DesktopGraphicsPresets.SameValue(val, baseline);
				Commit(definition, val);
			};
			toggleBox.AddChild(toggle);
			return toggleBox;
		}

		if (meta.Range is { } range && definition.Type is RuntimeCVarType.Int or RuntimeCVarType.Double)
		{
			return BuildBoundedNumericEditor(definition, current, range, resetBtn, baseline);
		}

		if (definition.Type == RuntimeCVarType.Int)
		{
			var spin = new SpinBox
			{
				MinValue = -100000,
				MaxValue = 100000,
				Step = 1,
				Value = RuntimeSettingsModel.ParseInt(current, definition.DefaultValue),
				CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
			};
			Apply(spin);
			spin.ValueChanged += v =>
			{
				string val = ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
				UpdateResetVisibility(resetBtn, val, baseline);
				Commit(definition, val);
			};
			return spin;
		}

		if (definition.Type == RuntimeCVarType.Double)
		{
			var spin = new SpinBox
			{
				MinValue = -10000.0,
				MaxValue = 10000.0,
				Step = 0.05,
				Value = RuntimeSettingsModel.ParseDouble(current, definition.DefaultValue),
				CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
			};
			Apply(spin);
			spin.ValueChanged += v =>
			{
				string val = v.ToString("0.###", CultureInfo.InvariantCulture);
				UpdateResetVisibility(resetBtn, val, baseline);
				Commit(definition, val);
			};
			return spin;
		}

		var edit = new LineEdit
		{
			Text = current,
			CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
		};
		Apply(edit);
		StyleSearchBox(edit);
		edit.TextSubmitted += text =>
		{
			UpdateResetVisibility(resetBtn, text, baseline);
			Commit(definition, text);
		};
		edit.FocusExited += () =>
		{
			UpdateResetVisibility(resetBtn, edit.Text, baseline);
			Commit(definition, edit.Text);
		};
		return edit;
	}

	private Control BuildBoundedNumericEditor(
		RuntimeCVarDefinition definition,
		string current,
		NumericRange range,
		Button resetBtn,
		string baseline)
	{
		var box = new HBoxContainer
		{
			CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
		};
		box.AddThemeConstantOverride("separation", LauncherScale.Px(8));

		double val = RuntimeSettingsModel.ParseDouble(current, definition.DefaultValue);
		bool isFsrFixedScale = definition.Name == "render_scale_percent" &&
			_model.GetValueOrDefault("render_scaler", "bilinear").Trim().Trim('"') is "fsr" or "fsr2" &&
			_model.GetValueOrDefault("fsr_quality", "custom").Trim().Trim('"') != "custom";

		var slider = new HSlider
		{
			MinValue = range.Min,
			MaxValue = range.Max,
			Step = range.Step,
			Value = Math.Clamp(val, range.Min, range.Max),
			CustomMinimumSize = new Vector2(LauncherScale.Px(72), 0),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
			Editable = !isFsrFixedScale,
		};
		Apply(slider);

		var readout = MakeLabel(FormatNumericReadout(val, definition.Type == RuntimeCVarType.Int, range));
		readout.CustomMinimumSize = new Vector2(SliderReadoutWidth, 0);
		readout.HorizontalAlignment = HorizontalAlignment.Right;
		readout.VerticalAlignment = VerticalAlignment.Center;
		readout.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 2));
		readout.AddThemeColorOverride("font_color", isFsrFixedScale ? _style.TextDisabled : _style.Text);

		if (isFsrFixedScale)
		{
			string tip = "Controlled by FSR Quality Mode (choose Custom in FSR Quality Mode to set a manual scale).";
			slider.TooltipText = tip;
			readout.TooltipText = tip;
		}

		slider.ValueChanged += next =>
		{
			string formatted = definition.Type == RuntimeCVarType.Int
				? ((int)Math.Round(next)).ToString(CultureInfo.InvariantCulture)
				: next.ToString("0.###", CultureInfo.InvariantCulture);
			readout.Text = FormatNumericReadout(next, definition.Type == RuntimeCVarType.Int, range);
			UpdateResetVisibility(resetBtn, formatted, baseline);
			Commit(definition, formatted);
		};

		box.AddChild(slider);
		box.AddChild(readout);
		return box;
	}

	private static string FormatNumericReadout(double value, bool isInt, NumericRange range)
	{
		if (range.ZeroLabel is not null && Math.Abs(value) < 1e-6)
		{
			return range.ZeroLabel;
		}

		string num = isInt
			? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
			: value.ToString("0.##", CultureInfo.InvariantCulture);
		return num + range.Suffix;
	}

	private OptionButton BuildDropdown(
		RuntimeCVarDefinition definition,
		string current,
		List<RuntimeCVarOption> options,
		Button? resetBtn = null,
		string? baseline = null)
	{
		string display = current.Length == 0 ? definition.DefaultValue : current;
		var option = new OptionButton
		{
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			CustomMinimumSize = new Vector2(ControlColumnWidth, 0),
		};
		StyleDropdown(option);

		int selected = -1;
		for (int i = 0; i < options.Count; i++)
		{
			option.AddItem(options[i].Label);
			if (options[i].Value == display)
			{
				selected = i;
			}
		}

		if (selected < 0)
		{
			options.Add(new RuntimeCVarOption(display, display));
			option.AddItem(display);
			selected = options.Count - 1;
		}

		option.Selected = selected;
		option.ItemSelected += index =>
		{
			string chosen = options[(int)index].Value;
			if (resetBtn is not null && baseline is not null)
			{
				UpdateResetVisibility(resetBtn, chosen, baseline);
			}

			Commit(definition, chosen);
		};
		return option;
	}

	private Button BuildInlineResetButton(bool visible, Action onReset, string defaultDisplay)
	{
		var btn = new Button
		{
			Text = "\u21BA",
			Flat = true,
			Disabled = !visible,
			Modulate = visible ? Colors.White : Colors.Transparent,
			CustomMinimumSize = new Vector2(LauncherScale.Px(22), 0),
			TooltipText = $"Reset to default ({defaultDisplay})",
		};
		Apply(btn);
		btn.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 2));
		btn.AddThemeColorOverride("font_color", LauncherTokens.GoldInk);
		btn.AddThemeColorOverride("font_hover_color", LauncherTokens.Emerald);
		btn.Pressed += onReset;
		return btn;
	}

	private static void UpdateResetVisibility(Button resetBtn, string current, string baseline)
	{
		bool modified = !DesktopGraphicsPresets.SameValue(current, baseline);
		resetBtn.Disabled = !modified;
		resetBtn.Modulate = modified ? Colors.White : Colors.Transparent;
	}

	private static string BaselineFor(RuntimeCVarDefinition definition)
	{
		if (DesktopGraphicsPresets.Offered &&
		    DesktopGraphicsPresets.Values(DesktopGraphicsPresets.DefaultPreset) is { } presetVals &&
		    presetVals.TryGetValue(definition.Name, out string? presetDefault))
		{
			return presetDefault;
		}

		return definition.DefaultValue;
	}

	private static string BuildTooltip(RuntimeCVarDefinition definition, SettingPresentation meta)
	{
		string desc = RuntimeSettingsModel.FormatDescription(definition);
		string def = definition.DefaultValue.Length == 0 ? "(empty)" : definition.DefaultValue;
		if (!string.IsNullOrEmpty(meta.Summary) && !desc.StartsWith(meta.Summary, StringComparison.Ordinal))
		{
			return $"{meta.Summary}\n\n{desc}\n\nKey: {definition.Name}  |  Default: {def}";
		}

		return $"{desc}\n\nKey: {definition.Name}  |  Default: {def}";
	}

	private void Commit(RuntimeCVarDefinition definition, string value, bool forceRebuild = false)
	{
		_model.Values[definition.Name] = value;
		_model.Save();
		if (_model.RefreshDesktopPreset(definition.Name))
		{
			RefreshDesktopPresetHero();
		}
		if (definition.Name is "lighting_model" or "materials_modernized" or "modern_tone_curve")
		{
			RefreshLook();
		}
		RefreshGraphicsPreset(definition.Name); // SettingsPanel.GraphicsPreset.cs
		UpdateStatusText(_model.StatusMessage);

		bool contextualTrigger = definition.Name is
			"display_aspect" or "monitor" or "render_scaler" or "fsr_quality" or "dynamic_sky_and_weather";
		if (forceRebuild || contextualTrigger)
		{
			Rebuild();
		}
		else if (definition.Name == "hdr_output" && value == "true" && OperatingSystem.IsWindows())
		{
			if (_model.Values.TryGetValue("rendering_driver", out string? currentDriver) && currentDriver != "d3d12")
			{
				_model.Values["rendering_driver"] = "d3d12";
				_model.Save();
				Rebuild();
			}
		}
	}

	// ==================== STYLING HELPERS ====================

	private void StyleTabButton(Button button, bool selected)
	{
		var normal = new StyleBoxFlat
		{
			BgColor = selected ? LauncherTokens.Emerald : LauncherTokens.ParchmentLit,
			BorderColor = selected ? LauncherTokens.Emerald : new Color(LauncherTokens.Gold, 0.45f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		normal.SetBorderWidthAll(1);

		var hover = new StyleBoxFlat
		{
			BgColor = selected ? LauncherTokens.EmeraldLit : LauncherTokens.ParchmentRaised,
			BorderColor = selected ? LauncherTokens.EmeraldLit : LauncherTokens.Gold,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			CornerDetail = 4,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		hover.SetBorderWidthAll(1);

		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", hover);
		button.AddThemeStyleboxOverride("focus", hover);

		Color fg = selected ? LauncherTokens.Cream : LauncherTokens.Walnut;
		button.AddThemeColorOverride("font_color", fg);
		button.AddThemeColorOverride("font_hover_color", selected ? Colors.White : LauncherTokens.Oak);
		button.AddThemeColorOverride("font_pressed_color", fg);
		button.AddThemeColorOverride("font_focus_color", fg);
	}

	private void StyleSecondaryButton(Button button)
	{
		Apply(button);
		var normal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.55f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		normal.SetBorderWidthAll(1);

		var hover = (StyleBoxFlat)normal.Duplicate();
		hover.BorderColor = LauncherTokens.Gold;
		hover.BgColor = LauncherTokens.ParchmentRaised;

		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", hover);
		button.AddThemeStyleboxOverride("focus", hover);
		button.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		button.AddThemeColorOverride("font_hover_color", LauncherTokens.Oak);
		button.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		button.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);
	}

	private void StyleCompactButton(Button button)
	{
		StyleSecondaryButton(button);
		button.AddThemeFontSizeOverride("font_size", Math.Max(11, _fontSize - 3));
	}

	private void StyleSearchBox(LineEdit edit)
	{
		var box = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.55f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 8,
			ContentMarginRight = 8,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		box.SetBorderWidthAll(1);

		var focus = (StyleBoxFlat)box.Duplicate();
		focus.BorderColor = LauncherTokens.Emerald;

		edit.AddThemeStyleboxOverride("normal", box);
		edit.AddThemeStyleboxOverride("focus", focus);
		edit.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		edit.AddThemeColorOverride("font_placeholder_color", LauncherTokens.Sepia);
		edit.AddThemeColorOverride("caret_color", LauncherTokens.Walnut);
	}

	private void StyleDropdown(OptionButton option)
	{
		Apply(option);
		option.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 2));

		var normal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.55f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = 8,
			ContentMarginRight = 8,
			ContentMarginTop = 3,
			ContentMarginBottom = 3,
		};
		normal.SetBorderWidthAll(1);

		var hover = (StyleBoxFlat)normal.Duplicate();
		hover.BorderColor = LauncherTokens.Gold;

		option.AddThemeStyleboxOverride("normal", normal);
		option.AddThemeStyleboxOverride("hover", hover);
		option.AddThemeStyleboxOverride("pressed", hover);
		option.AddThemeStyleboxOverride("focus", hover);
		option.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_hover_color", LauncherTokens.Oak);
		option.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);

		StylePopupMenu(option.GetPopup());
	}

	private void StylePopupMenu(PopupMenu popup)
	{
		if (_font is not null)
		{
			popup.AddThemeFontOverride("font", _font);
		}
		popup.AddThemeFontSizeOverride("font_size", Math.Max(12, _fontSize - 2));

		var panel = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentRaised,
			BorderColor = LauncherTokens.Gold,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ShadowColor = new Color(0, 0, 0, 0.35f),
			ShadowSize = 8,
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 6,
			ContentMarginBottom = 6,
		};
		panel.SetBorderWidthAll(1);
		popup.AddThemeStyleboxOverride("panel", panel);

		var hover = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Gold, 0.22f),
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm - 2,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm - 2,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm - 2,
			CornerRadiusTopRight = LauncherTokens.RadiusSm - 2,
			ContentMarginLeft = 6,
			ContentMarginRight = 6,
			ContentMarginTop = 3,
			ContentMarginBottom = 3,
		};
		popup.AddThemeStyleboxOverride("hover", hover);
		popup.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		popup.AddThemeColorOverride("font_hover_color", LauncherTokens.Oak);
		popup.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
	}
}
