using System;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Native;

namespace Refii.Launcher.Lua;

[Tool]
public partial class ResetLuaFileDialog : Control
{
	private const int ListWidth = 700;
	private const int ListHeight = 380;

	private readonly LuaVerifyEntry[] _entries;
	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly SmartStyle _style;

	private LineEdit _filter = null!;
	private VBoxContainer _list = null!;

	public event Action<string>? ResetRequested;

	public ResetLuaFileDialog(LuaVerifyEntry[] entries, Font? font, int fontSize, SmartStyle style)
	{
		_entries = entries;
		_font = font;
		_fontSize = fontSize;
		_style = style;
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

		var frame = new PanelContainer();
		var box = new StyleBoxFlat { BgColor = _style.PopupBg };
		box.BorderColor = _style.Border;
		box.SetBorderWidthAll(1);
		box.ContentMarginLeft = 12;
		box.ContentMarginRight = 12;
		box.ContentMarginTop = 10;
		box.ContentMarginBottom = 12;
		frame.AddThemeStyleboxOverride("panel", box);
		centre.AddChild(frame);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 8);
		frame.AddChild(column);

		column.AddChild(MakeLabel(_entries.Length + " tracked Lua files"));

		var filterRow = new HBoxContainer();
		filterRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(filterRow);

		_filter = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		Apply(_filter);
		_filter.TextChanged += _ => Populate();
		filterRow.AddChild(_filter);
		filterRow.AddChild(MakeLabel("Filter"));

		var scroll = new ScrollContainer
		{
			CustomMinimumSize = new Vector2(ListWidth, ListHeight),
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		var scrollFrame = new PanelContainer();
		var scrollBox = new StyleBoxFlat { BgColor = _style.ChildBg };
		scrollBox.BorderColor = _style.Border;
		scrollBox.SetBorderWidthAll(1);
		scrollFrame.AddThemeStyleboxOverride("panel", scrollBox);
		scrollFrame.AddChild(scroll);
		column.AddChild(scrollFrame);

		_list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_list.AddThemeConstantOverride("separation", 4);
		scroll.AddChild(_list);

		var close = new Button { Text = "Close" };
		Apply(close);
		close.Pressed += QueueFree;
		column.AddChild(close);

		Populate();
	}

	private void Populate()
	{
		foreach (Node child in _list.GetChildren())
		{
			child.QueueFree();
		}

		string filter = _filter.Text.ToLowerInvariant();

		foreach (LuaVerifyEntry entry in _entries)
		{
			if (entry.Status == LuaVerifyStatus.BaselineMissing ||
				entry.Status == LuaVerifyStatus.BaselineCorrupt ||
				entry.RelativePath.Length == 0)
			{
				continue;
			}

			string haystack = (entry.RelativePath + " " + entry.StatusText).ToLowerInvariant();
			if (filter.Length != 0 && !haystack.Contains(filter))
			{
				continue;
			}

			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 8);

			var reset = new Button { Text = "Reset" };
			Apply(reset);
			string capturedPath = entry.RelativePath;
			reset.Pressed += () =>
			{
				ResetRequested?.Invoke(capturedPath);
				QueueFree();
			};
			row.AddChild(reset);

			Label text = MakeLabel("[" + entry.StatusText + "] " + entry.RelativePath);
			text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			row.AddChild(text);

			_list.AddChild(row);
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
