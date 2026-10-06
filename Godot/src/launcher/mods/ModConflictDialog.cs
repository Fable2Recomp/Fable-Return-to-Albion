using System;
using System.Collections.Generic;
using Godot;
using Refii.Launcher.Core;
using Refii.Mods;

namespace Refii.Launcher;

/// <summary>
/// Modal dialog displaying granular GDB record and property conflicts between installed mods,
/// styled consistently with the Lionhead/Fable launcher design language.
/// </summary>
[Tool]
public partial class ModConflictDialog : Control
{
	private readonly ModInstall _mod;
	private readonly ModTally _tally;
	private readonly SmartStyle _style;
	private readonly Font? _font;
	private readonly int _fontSize;

	public ModConflictDialog(ModInstall mod, ModTally tally, SmartStyle style, Font? font, int fontSize)
	{
		_mod = mod;
		_tally = tally;
		_style = style;
		_font = font;
		_fontSize = fontSize;
	}

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		// Semitransparent warm ink backdrop
		var dim = new ColorRect { Color = LauncherTokens.InkGlass };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		dim.GuiInput += @event =>
		{
			if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
			{
				Dismiss();
			}
		};
		AddChild(dim);

		var centre = new CenterContainer();
		centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		centre.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(centre);

		// Main dialog card
		var frame = new PanelContainer
		{
			CustomMinimumSize = new Vector2(620, 400),
			MouseFilter = MouseFilterEnum.Stop,
		};

		var frameBox = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentGlassStrong,
			BorderColor = LauncherTokens.Gold,
			BorderWidthLeft = 2,
			BorderWidthRight = 2,
			BorderWidthTop = 2,
			BorderWidthBottom = 2,
			CornerRadiusBottomLeft = LauncherTokens.RadiusMd,
			CornerRadiusBottomRight = LauncherTokens.RadiusMd,
			CornerRadiusTopLeft = LauncherTokens.RadiusMd,
			CornerRadiusTopRight = LauncherTokens.RadiusMd,
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = LauncherTokens.Sp3,
			ContentMarginBottom = LauncherTokens.Sp4,
			ShadowColor = new Color(0, 0, 0, 0.45f),
			ShadowSize = 12,
		};
		frame.AddThemeStyleboxOverride("panel", frameBox);
		centre.AddChild(frame);

		var rootCol = new VBoxContainer();
		rootCol.AddThemeConstantOverride("separation", LauncherTokens.Sp3);
		frame.AddChild(rootCol);

		bool isPriorityMode = _tally.Lost == 0 && _tally.Conflicts > 0;

		// Header row
		var header = new HBoxContainer();
		header.AddThemeConstantOverride("separation", LauncherTokens.Sp2);

		Label title = MakeLabel(isPriorityMode ? $"Mod Priority Details — {_mod.Name}" : $"Mod Conflicts — {_mod.Name}");
		title.AddThemeFontSizeOverride("font_size", _fontSize + 2);
		title.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		header.AddChild(title);

		var closeX = new Button
		{
			Text = "✕",
			CustomMinimumSize = new Vector2(28, 28),
			Flat = true,
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		Apply(closeX);
		closeX.AddThemeColorOverride("font_color", LauncherTokens.Sepia);
		closeX.AddThemeColorOverride("font_hover_color", LauncherTokens.Crimson);
		closeX.Pressed += Dismiss;
		header.AddChild(closeX);
		rootCol.AddChild(header);

		// Explanatory label
		Label desc = MakeLabel(isPriorityMode
			? $"The following modifications from lower-priority mods are overridden by '{_mod.Name}':"
			: $"The following modifications in '{_mod.Name}' are overridden by higher-priority mods:");
		desc.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		desc.AddThemeFontSizeOverride("font_size", _fontSize);
		desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		rootCol.AddChild(desc);

		// Scrollable list container
		var listFrame = new PanelContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(580, 200),
		};

		var innerStyle = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentRaised,
			BorderColor = new Color(LauncherTokens.Sepia, 0.35f),
			BorderWidthLeft = 1,
			BorderWidthRight = 1,
			BorderWidthTop = 1,
			BorderWidthBottom = 1,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp2,
			ContentMarginRight = LauncherTokens.Sp2,
			ContentMarginTop = LauncherTokens.Sp2,
			ContentMarginBottom = LauncherTokens.Sp2,
		};
		listFrame.AddThemeStyleboxOverride("panel", innerStyle);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		listFrame.AddChild(scroll);
		rootCol.AddChild(listFrame);

		var list = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		list.AddThemeConstantOverride("separation", LauncherTokens.Sp2);
		scroll.AddChild(list);

		IReadOnlyList<string> details = isPriorityMode ? _tally.WonConflictDetails : _tally.ConflictDetails;

		if (details.Count == 0)
		{
			var empty = MakeLabel(isPriorityMode
				? "• Prioritized over colliding changes in lower-priority mods."
				: "• Overridden by another active mod higher in load order.");
			empty.AddThemeColorOverride("font_color", LauncherTokens.Sepia);
			list.AddChild(empty);
		}
		else
		{
			foreach (string detail in details)
			{
				var itemCard = new PanelContainer
				{
					SizeFlagsHorizontal = SizeFlags.ExpandFill,
				};

				var itemStyle = new StyleBoxFlat
				{
					BgColor = LauncherTokens.ParchmentLit,
					BorderColor = new Color(LauncherTokens.Gold, 0.4f),
					BorderWidthLeft = 1,
					BorderWidthRight = 1,
					BorderWidthTop = 1,
					BorderWidthBottom = 1,
					CornerRadiusBottomLeft = 4,
					CornerRadiusBottomRight = 4,
					CornerRadiusTopLeft = 4,
					CornerRadiusTopRight = 4,
					ContentMarginLeft = LauncherTokens.Sp3,
					ContentMarginRight = LauncherTokens.Sp3,
					ContentMarginTop = LauncherTokens.Sp2,
					ContentMarginBottom = LauncherTokens.Sp2,
				};
				itemCard.AddThemeStyleboxOverride("panel", itemStyle);

				var cardCol = new VBoxContainer();
				cardCol.AddThemeConstantOverride("separation", 2);

				// Parse or display formatted detail:
				// Format is typically: "[PropertyName] on [Record] in [Database] (overridden by '[Winner]')"
				Label itemLabel = MakeLabel($"• {detail}");
				itemLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
				itemLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
				itemLabel.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
				itemLabel.AddThemeFontSizeOverride("font_size", _fontSize);
				cardCol.AddChild(itemLabel);

				itemCard.AddChild(cardCol);
				list.AddChild(itemCard);
			}
		}

		// Tip notice
		var tipBar = new HBoxContainer();
		Label tip = MakeLabel(isPriorityMode
			? $"Tip: '{_mod.Name}' is prioritized in the load order and wins these collisions over lower mods."
			: $"Tip: Move '{_mod.Name}' higher in the load order to prioritize its changes over conflicting mods.");
		tip.AddThemeColorOverride("font_color", LauncherTokens.Sepia);
		tip.AddThemeFontSizeOverride("font_size", _fontSize - 1);
		tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		tip.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		tipBar.AddChild(tip);
		rootCol.AddChild(tipBar);

		// Footer
		var footer = new HBoxContainer
		{
			Alignment = BoxContainer.AlignmentMode.End,
		};
		var ok = new Button
		{
			Text = "Close",
			CustomMinimumSize = new Vector2(90, 30),
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		Apply(ok);

		var btnNormal = new StyleBoxFlat
		{
			BgColor = LauncherTokens.Emerald,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};
		var btnHover = new StyleBoxFlat
		{
			BgColor = LauncherTokens.EmeraldLit,
			CornerRadiusBottomLeft = LauncherTokens.RadiusSm,
			CornerRadiusBottomRight = LauncherTokens.RadiusSm,
			CornerRadiusTopLeft = LauncherTokens.RadiusSm,
			CornerRadiusTopRight = LauncherTokens.RadiusSm,
			ContentMarginLeft = LauncherTokens.Sp4,
			ContentMarginRight = LauncherTokens.Sp4,
			ContentMarginTop = 4,
			ContentMarginBottom = 4,
		};

		ok.AddThemeStyleboxOverride("normal", btnNormal);
		ok.AddThemeStyleboxOverride("hover", btnHover);
		ok.AddThemeStyleboxOverride("pressed", btnHover);
		ok.AddThemeColorOverride("font_color", LauncherTokens.Cream);
		ok.AddThemeColorOverride("font_hover_color", Colors.White);
		ok.Pressed += Dismiss;

		footer.AddChild(ok);
		rootCol.AddChild(footer);
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			Dismiss();
			GetViewport().SetInputAsHandled();
		}
	}

	private void Dismiss() => QueueFree();

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
		return label;
	}
}
