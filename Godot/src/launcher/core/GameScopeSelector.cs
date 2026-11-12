using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Launcher.Core;

/// <summary>
/// Reusable segmented game-scope selector (<c>[ Fable 2 | Fable 3 ]</c>) placed at the top of
/// per-title management tabs (<c>Modding</c>, <c>Install</c>, and <c>Saves</c>).
/// </summary>
public sealed partial class GameScopeSelector : PanelContainer
{
	private readonly Dictionary<Engine_Title, Button> _buttons = new();
	private readonly HBoxContainer _row;
	private Font? _font;
	private int _fontSize = LauncherTokens.BodySize;
	private Engine_Title _selectedTitle = Engine_Title.Fable2;
	private bool _interactive = true;

	/// <summary>Raised when the user clicks a different title segment.</summary>
	public event Action<Engine_Title>? TitleSelected;

	/// <summary>Optional callback supplying a short secondary status suffix per title (e.g. "2 mods" or "Installed").</summary>
	public Func<Engine_Title, string>? BadgeProvider { get; set; }

	/// <summary>Currently selected game title scope.</summary>
	public Engine_Title SelectedTitle => _selectedTitle;

	/// <summary>Whether segment buttons accept user clicks (e.g. false while an Android install is active).</summary>
	public bool Interactive
	{
		get => _interactive;
		set
		{
			if (_interactive == value)
			{
				return;
			}

			_interactive = value;
			Refresh();
		}
	}

	/// <summary>Initializes a new segmented selector over <see cref="Engine_Title.All"/>.</summary>
	public GameScopeSelector(Engine_Title? initialTitle = null)
	{
		_selectedTitle = initialTitle ?? Engine_Title.Active;
		SizeFlagsHorizontal = SizeFlags.ExpandFill;

		var track = new StyleBoxFlat
		{
			BgColor = new Color(LauncherTokens.Oak, 0.12f),
			BorderColor = new Color(LauncherTokens.Gold, 0.42f),
			ContentMarginLeft = 3,
			ContentMarginRight = 3,
			ContentMarginTop = 3,
			ContentMarginBottom = 3,
		};
		track.SetCornerRadiusAll(LauncherTokens.RadiusSm + 2);
		track.SetBorderWidthAll(1);
		AddThemeStyleboxOverride("panel", track);

		_row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_row.AddThemeConstantOverride("separation", 4);
		AddChild(_row);

		foreach (Engine_Title title in Engine_Title.All)
		{
			Engine_Title captured = title;
			var button = new Button
			{
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				CustomMinimumSize = new Vector2(120, 32),
				MouseDefaultCursorShape = CursorShape.PointingHand,
				Alignment = HorizontalAlignment.Center,
				TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
				ClipText = true,
			};
			button.Pressed += () => OnSegmentPressed(captured);
			_row.AddChild(button);
			_buttons[title] = button;
		}

		Refresh();
	}

	/// <summary>Applies responsive font and text sizing to all segment buttons.</summary>
	public void Configure(Font? font, int fontSize)
	{
		_font = font;
		_fontSize = fontSize;
		Refresh();
	}

	/// <summary>Updates the selected title segment, optionally invoking <see cref="TitleSelected"/>.</summary>
	public void SetSelectedTitle(Engine_Title title, bool notify = false)
	{
		bool changed = _selectedTitle != title;
		_selectedTitle = title;
		Refresh();
		if (changed && notify)
		{
			TitleSelected?.Invoke(title);
		}
	}

	/// <summary>Refreshes button captions, optional badges, tooltips, and active/inactive segment styling.</summary>
	public void Refresh()
	{
		foreach ((Engine_Title title, Button button) in _buttons)
		{
			bool selected = title == _selectedTitle;
			LauncherTitleScreen screen = LauncherTitleScreen.For(title);
			string badge = string.Empty;
			if (BadgeProvider is not null)
			{
				try
				{
					badge = BadgeProvider(title) ?? string.Empty;
				}
				catch (Exception)
				{
					badge = string.Empty;
				}
			}

			string label = title.Name;
			if (badge.Length != 0)
			{
				label += "  ·  " + badge;
			}
			else if (!string.IsNullOrEmpty(screen.StatusBadge))
			{
				label += " (" + screen.StatusBadge + ")";
			}

			button.Text = label;
			button.TooltipText = screen.StatusTooltip.Length != 0
				? $"{title.RemakeName} — {screen.StatusTooltip}"
				: $"Manage {title.Name}";
			button.Disabled = !_interactive && !selected;

			if (_font is not null)
			{
				button.AddThemeFontOverride("font", _font);
			}
			else
			{
				button.RemoveThemeFontOverride("font");
			}

			button.AddThemeFontSizeOverride("font_size", _fontSize);
			ApplySegmentStyle(button, selected);
		}
	}

	private void OnSegmentPressed(Engine_Title title)
	{
		if (!_interactive || title == _selectedTitle)
		{
			return;
		}

		_selectedTitle = title;
		Refresh();
		TitleSelected?.Invoke(title);
	}

	private static void ApplySegmentStyle(Button button, bool selected)
	{
		if (selected)
		{
			StyleBoxFlat active = SegmentBox(
				LauncherTokens.Emerald,
				new Color(LauncherTokens.GoldBright, 0.85f));
			StyleBoxFlat activeHover = SegmentBox(
				LauncherTokens.EmeraldLit,
				LauncherTokens.GoldBright);

			button.AddThemeStyleboxOverride("normal", active);
			button.AddThemeStyleboxOverride("hover", activeHover);
			button.AddThemeStyleboxOverride("pressed", active);
			button.AddThemeStyleboxOverride("disabled", active);
			button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
			button.AddThemeColorOverride("font_hover_color", LauncherTokens.Cream);
			button.AddThemeColorOverride("font_pressed_color", LauncherTokens.Cream);
			button.AddThemeColorOverride("font_disabled_color", LauncherTokens.Cream);
		}
		else
		{
			StyleBoxFlat inactive = SegmentBox(
				new Color(LauncherTokens.ParchmentLit, 0.55f),
				new Color(LauncherTokens.Gold, 0.25f));
			StyleBoxFlat inactiveHover = SegmentBox(
				new Color(LauncherTokens.ParchmentLit, 0.92f),
				new Color(LauncherTokens.Gold, 0.65f));
			StyleBoxFlat inactiveDisabled = SegmentBox(
				new Color(LauncherTokens.Parchment, 0.35f),
				new Color(LauncherTokens.Gold, 0.15f));

			button.AddThemeStyleboxOverride("normal", inactive);
			button.AddThemeStyleboxOverride("hover", inactiveHover);
			button.AddThemeStyleboxOverride("pressed", inactiveHover);
			button.AddThemeStyleboxOverride("disabled", inactiveDisabled);
			button.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
			button.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldInk);
			button.AddThemeColorOverride("font_pressed_color", LauncherTokens.GoldInk);
			button.AddThemeColorOverride("font_disabled_color", LauncherTokens.SepiaInk);
		}

		var focus = new StyleBoxFlat
		{
			BgColor = new Color(0, 0, 0, 0),
			BorderColor = LauncherTokens.GoldBright,
		};
		focus.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		focus.SetBorderWidthAll(2);
		button.AddThemeStyleboxOverride("focus", focus);
	}

	private static StyleBoxFlat SegmentBox(Color bg, Color border)
	{
		var box = new StyleBoxFlat
		{
			BgColor = bg,
			BorderColor = border,
			ContentMarginLeft = LauncherTokens.Sp3,
			ContentMarginRight = LauncherTokens.Sp3,
			ContentMarginTop = 5,
			ContentMarginBottom = 5,
		};
		box.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		box.SetBorderWidthAll(1);
		return box;
	}
}
