using System;
using System.Linq;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// The Updates tab: which published builds the app follows (<see cref="ReleaseUpdates"/>), what
/// the last look at the project's releases found, and the button that looks again or installs.
/// </summary>
/// <remarks>
/// The panel holds no update state of its own. The launcher states it every frame the tab shows
/// (<see cref="SetState"/>) and acts on the three requests the panel raises.
/// </remarks>
public partial class UpdatePanel : VBoxContainer
{
	private int _fontSize = 18;
	private Font? _font;
	private SmartStyle? _style;
	private OptionButton _channel = null!;
	private Label _installed = null!;
	private Label _status = null!;
	private Button _action = null!;
	private bool _installs;
	private bool _built;

	/// <summary>The player chose which published builds the app updates to.</summary>
	public event Action<UpdateChannel>? ChannelChanged;

	/// <summary>The player asked for another look at the project's releases.</summary>
	public event Action? CheckRequested;

	/// <summary>The player asked for the newest build to be downloaded and installed.</summary>
	public event Action? InstallRequested;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 10);

		Label explain = Styled(new Label
		{
			Text = "The app updates itself from the project's published builds. Updating replaces " +
				"the app's own files; your game data, saves, mods and settings are kept.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		});
		AddChild(explain);

		AddChild(Styled(new Label { Text = "Channel" }));
		_channel = Styled(new OptionButton
		{
			CustomMinimumSize = new Vector2(260, 36),
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
			TooltipText = "Release follows the numbered betas. Nightly follows the newest dev build, which is untested.",
		});
		_channel.AddItem(ReleaseUpdates.ChannelName(UpdateChannel.Release));
		_channel.AddItem(ReleaseUpdates.ChannelName(UpdateChannel.Nightly));
		_channel.Select(ReleaseUpdates.Channel == UpdateChannel.Nightly ? 1 : 0);
		_channel.ItemSelected += index =>
			ChannelChanged?.Invoke(index == 1 ? UpdateChannel.Nightly : UpdateChannel.Release);
		AddChild(_channel);

		AddChild(new HSeparator());

		_status = Styled(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
		AddChild(_status);

		_installed = Styled(new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart });
		AddChild(_installed);

		_action = Styled(new Button
		{
			Text = "Check for updates",
			CustomMinimumSize = new Vector2(220, 40),
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
		});
		_action.Pressed += () =>
		{
			if (_installs)
			{
				InstallRequested?.Invoke();
			}
			else
			{
				CheckRequested?.Invoke();
			}
		};
		AddChild(_action);

		_built = true;
	}

	/// <summary>Takes the drawer's font, size and colours, as its sibling panels do.</summary>
	public void Configure(Font? font, int fontSize, SmartStyle style)
	{
		_font = font;
		_fontSize = fontSize;
		_style = style;
		if (!_built)
		{
			return;
		}

		foreach (Control control in FindChildren("*", nameof(Control), true, false).OfType<Control>())
		{
			if (control is Label or BaseButton)
			{
				Styled(control);
			}
		}
	}

	/// <summary>
	/// States the tab: what the last look found (<paramref name="status"/>), what is installed
	/// (<paramref name="installed"/>), whether the button installs the newest build or looks
	/// again, and whether the controls can be used now.
	/// </summary>
	public void SetState(string status, string installed, bool installs, bool enabled)
	{
		if (!_built)
		{
			return;
		}

		_status.Text = status;
		_installed.Text = installed;
		_installed.Visible = installed.Length != 0;
		_installs = installs;
		_action.Text = installs ? "Update now" : "Check for updates";
		_action.Disabled = !enabled;
		_channel.Disabled = !enabled;
		int wanted = ReleaseUpdates.Channel == UpdateChannel.Nightly ? 1 : 0;
		if (_channel.Selected != wanted)
		{
			_channel.Select(wanted);
		}
	}

	private T Styled<T>(T control) where T : Control
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
		if (control is Label && _style is not null)
		{
			control.AddThemeColorOverride("font_color", _style.Text);
		}

		if (control is BaseButton)
		{
			control.MouseDefaultCursorShape = CursorShape.PointingHand;
		}

		return control;
	}
}
