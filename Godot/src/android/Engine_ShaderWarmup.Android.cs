using Godot;

namespace Refii.Models;

/// <summary>
/// The phone's startup screen: the preparation screen's seal over moving smoke, with its music,
/// without the compiles.
/// </summary>
/// <remarks>
/// A phone warms its pipelines in <see cref="Engine_PipelineWarmup"/>, so <c>shader_warmup_enabled</c>
/// is off there and <see cref="Begin"/> finishes without a screen. The mount, the guest's boot and
/// its startup sequence then showed only the clear colour, half a minute on an older phone. This
/// puts the desktop's screen over that stretch; AndroidStartup decides when it hides and goes.
/// </remarks>
public partial class Engine_ShaderWarmup
{
	/// <summary>Above the touch controls (140) and below Options (150): nothing to press while it is up.</summary>
	private const int StartupScreenLayer = 145;

	private bool _screenOnly;
	private string _startupStatus = string.Empty;

	/// <summary>Shows the screen with <paramref name="status"/> beside the spinner, unless the warm-up's own is already up.</summary>
	public static void BeginStartupScreen(Node parent, string status)
	{
		if (_instance is { } live && IsInstanceValid(live))
		{
			return;
		}

		_instance = new Engine_ShaderWarmup
		{
			Name = "StartupScreen",
			_screenOnly = true,
			_startupStatus = status,
		};
		parent.AddChild(_instance);
	}

	/// <summary>Hides the startup screen and pauses its music while something else has the screen, a startup video.</summary>
	/// <param name="shown">Whether the screen is up.</param>
	/// <param name="music">Whether its music plays; never while the screen is down.</param>
	public static void ShowStartupScreen(bool shown, bool music)
	{
		if (_instance is not { _screenOnly: true } screen || !IsInstanceValid(screen) || screen._screen is null)
		{
			return;
		}

		screen._screen.Visible = shown;
		if (screen._musicPlayer is { } player && IsInstanceValid(player))
		{
			player.StreamPaused = !(shown && music);
		}
	}

	/// <summary>The startup screen's <see cref="_Ready"/>: the screen and its music, no viewport and no shaders.</summary>
	private bool ReadyStartupScreen()
	{
		if (!_screenOnly)
		{
			return false;
		}

		BuildScreen();
		_screen.Layer = StartupScreenLayer;
		_label.Text = _startupStatus;
		TryRequestWarmupMusic();
		return true;
	}
}
