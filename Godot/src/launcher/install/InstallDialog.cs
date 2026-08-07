using System;
using System.IO;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

[Tool]
public partial class InstallDialog : Control
{
	private const int PopupWidth = 700;

	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly SmartStyle _style;

	private LineEdit _isoEdit = null!;
	private LineEdit _xexpEdit = null!;
	private Label _errorLabel = null!;
	private Button _startButton = null!;
	private PanelContainer _frame = null!;

	private string _dialogError = string.Empty;

	public event Action<string, string>? InstallRequested;

	public InstallDialog(Font? font, int fontSize, SmartStyle style)
	{
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

		_frame = new PanelContainer { CustomMinimumSize = new Vector2(PopupWidth, 0) };
		var box = new StyleBoxFlat { BgColor = _style.PopupBg };
		box.BorderColor = _style.Border;
		box.SetBorderWidthAll(1);
		box.ContentMarginLeft = 10;
		box.ContentMarginRight = 10;
		box.ContentMarginTop = 6;
		box.ContentMarginBottom = 8;
		_frame.AddThemeStyleboxOverride("panel", box);
		centre.AddChild(_frame);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 4);
		_frame.AddChild(column);

		Label heading = MakeLabel("Install Fable 2");
		var headingBar = new StyleBoxFlat { BgColor = _style.TitleBg };
		headingBar.ContentMarginLeft = 4;
		headingBar.ContentMarginRight = 4;
		headingBar.ContentMarginTop = 2;
		headingBar.ContentMarginBottom = 2;
		heading.AddThemeStyleboxOverride("normal", headingBar);
		column.AddChild(heading);

		Label intro = MakeLabel(
			"Select the Fable 2 ISO and the extracted game executable marker (.xexp).");
		intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		column.AddChild(intro);

		column.AddChild(MakeLabel("Game ISO"));
		_isoEdit = AddPathRow(column, () => BrowseForFile(
			"Select the Fable 2 ISO", new[] { "*.iso ; Disc image" }, _isoEdit));

		column.AddChild(MakeLabel("Game XEXP"));
		_xexpEdit = AddPathRow(column, () => BrowseForFile(
			"Select the game XEXP", new[] { "*.xexp ; Game marker" }, _xexpEdit));

		_errorLabel = MakeLabel(string.Empty);
		_errorLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_errorLabel.Visible = false;
		column.AddChild(_errorLabel);

		column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

		var buttonRow = new HBoxContainer();
		buttonRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(buttonRow);

		_startButton = MakeButton("Start Install", 160);
		_startButton.Disabled = true;
		_startButton.Pressed += OnStartPressed;
		buttonRow.AddChild(_startButton);

		Button cancel = MakeButton("Cancel", 120);
		cancel.Pressed += Dismiss;
		buttonRow.AddChild(cancel);

		RefreshState();
	}

	private void Dismiss()
	{
		_dialogError = string.Empty;
		QueueFree();
	}

	private Button MakeButton(string text, int minimumWidth)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(minimumWidth, 0),
		};
		Apply(button);
		button.AddThemeStyleboxOverride("normal", Fill(_style.Button));
		button.AddThemeStyleboxOverride("hover", Fill(_style.ButtonHovered));
		button.AddThemeStyleboxOverride("pressed", Fill(_style.ButtonActive));
		button.AddThemeStyleboxOverride("disabled", Fill(new Color(0.3f, 0.3f, 0.3f, 0.5f)));
		button.AddThemeColorOverride("font_color", _style.Text);
		button.AddThemeColorOverride("font_disabled_color", _style.TextDisabled);
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

	private LineEdit AddPathRow(VBoxContainer column, Action onBrowse)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);
		column.AddChild(row);

		var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		Apply(edit);
		edit.AddThemeStyleboxOverride("normal", Fill(_style.FrameBg));
		edit.AddThemeStyleboxOverride("focus", Fill(_style.FrameBgActive));
		edit.AddThemeColorOverride("font_color", _style.Text);
		edit.TextChanged += _ => RefreshState();
		row.AddChild(edit);

		Button browse = MakeButton("Browse", 110);
		browse.Pressed += onBrowse;
		row.AddChild(browse);

		return edit;
	}

	private void BrowseForFile(string title, string[] filters, LineEdit target)
	{
		var picker = new FileDialog
		{
			FileMode = FileDialog.FileModeEnum.OpenFile,
			Access = FileDialog.AccessEnum.Filesystem,
			Title = title,
			Filters = filters,
			Size = new Vector2I(820, 560),
		};

		picker.FileSelected += path =>
		{
			target.Text = path;
			_dialogError = string.Empty;
			RefreshState();
			picker.QueueFree();
		};
		picker.Canceled += () => picker.QueueFree();

		AddChild(picker);
		picker.PopupCentered();
	}

	private void OnStartPressed()
	{
		string isoPath = _isoEdit.Text;
		string xexpPath = _xexpEdit.Text;
		_dialogError = string.Empty;

		if (!File.Exists(isoPath))
		{
			_dialogError = "The selected ISO path does not exist.";
		}
		else if (!File.Exists(xexpPath))
		{
			_dialogError = "The selected XEXP path does not exist.";
		}
		else
		{
			InstallRequested?.Invoke(isoPath, xexpPath);
			QueueFree();
			return;
		}

		RefreshState();
	}

	private void RefreshState()
	{
		_startButton.Disabled = _isoEdit.Text.Length == 0 || _xexpEdit.Text.Length == 0;

		_errorLabel.Visible = _dialogError.Length != 0;
		_errorLabel.Text = _dialogError.Length == 0
			? string.Empty
			: "Picker error: " + _dialogError +
			  "\nYou can still paste the paths manually and continue.";
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
