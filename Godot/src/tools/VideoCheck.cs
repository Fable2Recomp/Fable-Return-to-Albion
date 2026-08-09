using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

namespace Refii.Launcher.Tools;

/// <summary>
/// Drives the guest and shows its Bink video — nothing else.
///
/// Deliberately WITHOUT the UI. Loading the GUI (fonts, the Babel string database, the
/// texture set) happens on the main thread and stalls the step loop for long enough that
/// the guest's startup sequence takes a different path and never opens a movie: the same
/// build opens one under host_driven and does not under game_window. Isolating video from
/// the UI is what makes "is video working" answerable at all.
/// </summary>
public partial class VideoCheck : Node
{
	[Export] public double WatchSeconds { get; set; } = 40.0;

	/// <summary>When to grab the screenshot, in seconds since the guest started.</summary>
	[Export] public double ShotAtSeconds { get; set; } = 14.0;

	private double _elapsed;
	private bool _driving;
	private string _shot = string.Empty;
	private bool _shotTaken;

	public override void _Ready()
	{
		_shot = OS.GetEnvironment("REFII_VIDEO_SHOT");
		if (double.TryParse(OS.GetEnvironment("REFII_VIDEO_SHOT_AT"), out double at) && at > 0)
		{
			ShotAtSeconds = at;
		}

		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0 || HostBridge.TryGetAbiVersion() != HostBridge.ExpectedAbiVersion)
		{
			GD.Print("no game data, or host bridge ABI mismatch.");
			GetTree().Quit(1);
			return;
		}

		GD.Print("[video-check] initialising guest...");
		HostBridge.ProbeResult init = HostBridge.Initialize(dataRoot);
		GD.Print($"[video-check] {init.Message}");
		if (!init.Success)
		{
			GetTree().Quit(1);
			return;
		}

		_driving = true;
		AddChild(new GameVideo { Name = "GameVideo" });
	}

	public override void _Process(double delta)
	{
		if (!_driving)
		{
			return;
		}

		if (!HostBridge.Step())
		{
			GD.Print($"[video-check] guest stopped: {HostBridge.HostedStatus}");
			Finish();
			return;
		}

		_elapsed += delta;

		// Shoot WHILE the video is up, not at the end. The logos finish well before the
		// watch window does, and a shot taken afterwards catches whatever replaced them.
		// A burst rather than one shot: the logos are short, and a single sample that
		// lands on a fade reads as "the decode is wrong" when it is merely early or late.
		if (_shot.Length != 0 && _shots < 6 && _elapsed >= ShotAtSeconds + (_shots * 1.5))
		{
			TakeShot();
		}

		if (_elapsed >= WatchSeconds)
		{
			Finish();
		}
	}

	private int _shots;

	private void TakeShot()
	{
		Image image = GetViewport().GetTexture().GetImage();
		string dir = System.IO.Path.GetDirectoryName(_shot)!;
		System.IO.Directory.CreateDirectory(dir);
		string path = System.IO.Path.Combine(
			dir,
			$"{System.IO.Path.GetFileNameWithoutExtension(_shot)}_{_shots}{System.IO.Path.GetExtension(_shot)}");
		image.SavePng(path);
		GD.Print($"[video-check] shot {_shots} at {_elapsed:F1}s -> {path}");
		++_shots;
	}

	private void Finish()
	{
		_driving = false;
		GD.Print($"[video-check] shutdown: {HostBridge.Shutdown().Message}");
		GetTree().Quit(0);
	}
}
