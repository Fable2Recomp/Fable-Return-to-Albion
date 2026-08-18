using System;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// Asks how much of the machine the game-code compile may take.
/// </summary>
/// <remarks>
/// A DECISION, not a setting, which is why it is a dialog rather than a checkbox in a tab.
/// It is asked once, at the moment the player commits to a build that runs for a long time,
/// and it is the only thing about that build they can usefully choose. Buried in Settings it
/// would be found after the wait, not before it.
/// </remarks>
[Tool]
public partial class BuildSpeedDialog : Control
{
	private const int PopupWidth = 640;

	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly SmartStyle _style;
	private readonly string _heading;

	/// <summary>True to use every core.</summary>
	public event Action<bool>? Chosen;

	public BuildSpeedDialog(Font? font, int fontSize, SmartStyle style, string heading)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;
		_heading = heading;
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

		int cores = System.Environment.ProcessorCount;
		int half = Math.Max(1, cores / 2);

		Label intro = MakeLabel(
			"Your game's code has to be compiled on this machine — around 150,000 " +
			"functions. It takes a while, and it is only done once.");
		intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		column.AddChild(intro);

		column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

		Button half_button = MakeButton(
			"Use " + half + " of " + cores + " cores  —  slower, PC stays usable");
		half_button.Pressed += () => Choose(false);
		column.AddChild(half_button);

		Button all_button = MakeButton(
			"Use all " + cores + " cores  —  fastest, PC will be sluggish");
		all_button.Pressed += () => Choose(true);
		column.AddChild(all_button);

		column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

		Button cancel = MakeButton("Cancel");
		cancel.Pressed += QueueFree;
		column.AddChild(cancel);
	}

	private void Choose(bool useAllCores)
	{
		Chosen?.Invoke(useAllCores);
		QueueFree();
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
