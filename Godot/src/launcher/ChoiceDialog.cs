using System;
using System.Collections.Generic;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// A decision put to the player at the moment it matters: a heading, a paragraph, one button per
/// answer, and Cancel, which closes it without choosing.
/// </summary>
/// <remarks>
/// A DECISION, not a setting, which is why it is a dialog rather than a checkbox in a tab. It is
/// asked when the player commits to the thing it is about, where the answer is still useful;
/// buried in Settings it would be found afterwards.
/// </remarks>
[Tool]
public partial class ChoiceDialog : Control
{
	private const int PopupWidth = 640;

	public sealed record Choice(string Label, Action Chosen);

	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly SmartStyle _style;
	private readonly string _heading;
	private readonly string _text;
	private readonly IReadOnlyList<Choice> _choices;

	public ChoiceDialog(Font? font, int fontSize, SmartStyle style, string heading, string text,
		IReadOnlyList<Choice> choices)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;
		_heading = heading;
		_text = text;
		_choices = choices;
	}

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		var dim = new ColorRect { Color = new Color(0.8f, 0.8f, 0.8f, 0.35f) };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(dim);

		var centre = new CenterContainer();
		centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(centre);

		var frame = new PanelContainer { CustomMinimumSize = new Vector2(PopupWidth, 0) };
		var box = new StyleBoxFlat { BgColor = _style.PopupBg };
		box.BorderColor = _style.Border;
		box.SetBorderWidthAll(1);
		box.ContentMarginLeft = 10;
		box.ContentMarginRight = 10;
		box.ContentMarginTop = 6;
		box.ContentMarginBottom = 8;
		frame.AddThemeStyleboxOverride("panel", box);
		centre.AddChild(frame);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 4);
		frame.AddChild(column);

		Label headingLabel = MakeLabel(_heading);
		var headingBar = new StyleBoxFlat { BgColor = _style.TitleBg };
		headingBar.ContentMarginLeft = 4;
		headingBar.ContentMarginRight = 4;
		headingBar.ContentMarginTop = 2;
		headingBar.ContentMarginBottom = 2;
		headingLabel.AddThemeStyleboxOverride("normal", headingBar);
		column.AddChild(headingLabel);

		Label intro = MakeLabel(_text);
		intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		column.AddChild(intro);

		column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

		foreach (Choice choice in _choices)
		{
			Button button = MakeButton(choice.Label);
			button.Pressed += () =>
			{
				choice.Chosen();
				QueueFree();
			};
			column.AddChild(button);
		}

		column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

		Button cancel = MakeButton("Cancel");
		cancel.Pressed += QueueFree;
		column.AddChild(cancel);
	}

	private Button MakeButton(string text)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(0, 34),
			Alignment = HorizontalAlignment.Center,
		};
		Apply(button);
		button.AddThemeStyleboxOverride("normal", Fill(_style.Button));
		button.AddThemeStyleboxOverride("hover", Fill(_style.ButtonHovered));
		button.AddThemeStyleboxOverride("pressed", Fill(_style.ButtonActive));
		button.AddThemeColorOverride("font_color", _style.Text);
		return button;
	}

	private static StyleBoxFlat Fill(Color color)
	{
		var box = new StyleBoxFlat { BgColor = color };
		box.ContentMarginLeft = 8;
		box.ContentMarginRight = 8;
		box.ContentMarginTop = 3;
		box.ContentMarginBottom = 3;
		return box;
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
