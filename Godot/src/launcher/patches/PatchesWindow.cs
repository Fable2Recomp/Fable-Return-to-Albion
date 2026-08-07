using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

[Tool]
public partial class PatchesWindow : Control
{
	private const int PanelWidth = 460;
	private const int PanelHeight = 520;

	private readonly Patches _patches;
	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly SmartStyle _style;

	public PatchesWindow(Patches patches, Font? font, int fontSize, SmartStyle style)
	{
		_patches = patches;
		_font = font;
		_fontSize = fontSize;
		_style = style;
	}

	public override void _Ready()
	{
		Position = new Vector2(40, 60);
		Size = new Vector2(PanelWidth, PanelHeight);
		MouseFilter = MouseFilterEnum.Pass;

		var frame = new PanelContainer();
		frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		var box = new StyleBoxFlat { BgColor = _style.WindowBg };
		box.BorderColor = _style.Border;
		box.SetBorderWidthAll(1);
		box.ContentMarginLeft = 10;
		box.ContentMarginRight = 10;
		box.ContentMarginTop = 6;
		box.ContentMarginBottom = 8;
		frame.AddThemeStyleboxOverride("panel", box);
		AddChild(frame);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		frame.AddChild(column);

		Label heading = MakeLabel("Patches and Mods");
		var headingBar = new StyleBoxFlat { BgColor = _style.TitleBg };
		headingBar.ContentMarginLeft = 4;
		headingBar.ContentMarginRight = 4;
		headingBar.ContentMarginTop = 2;
		headingBar.ContentMarginBottom = 2;
		heading.AddThemeStyleboxOverride("normal", headingBar);
		column.AddChild(heading);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		column.AddChild(scroll);

		var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		list.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(list);

		if (_patches.Items.Count == 0)
		{
			list.AddChild(MakeLabel("No patches available"));
			return;
		}

		foreach (Patch patch in _patches.Items)
		{
			if (patch.Type == "bool")
			{
				var check = new CheckBox
				{
					Text = patch.Name,
					ButtonPressed = patch.Enabled,
					TooltipText = patch.Description,
				};
				Apply(check);
				Patch captured = patch;
				check.Toggled += pressed => captured.Enabled = pressed;
				list.AddChild(check);
			}
			else if (patch.Type == "int")
			{
				Label name = MakeLabel(patch.Name);
				name.TooltipText = patch.Description;
				list.AddChild(name);

				var spin = new SpinBox
				{
					MinValue = 0,
					MaxValue = int.MaxValue,
					Step = 1,
					Value = patch.IntValue,
					TooltipText = patch.Description,
				};
				Apply(spin);
				Patch captured = patch;
				spin.ValueChanged += value => captured.IntValue = (int)value;
				list.AddChild(spin);
			}
		}
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
