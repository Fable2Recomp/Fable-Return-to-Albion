using System;
using System.Threading.Tasks;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The game's own Tracy capture: start, stop, save and discard it, its frames, and the timeline of
/// every game thread, GPU context and core. The other Tracy tabs read the same capture.
/// </summary>
/// <remarks>
/// Everything is in-process (see GameTracy): the capture is held by the analysis Worker inside the
/// game and read through HostTracy. Following, the view tracks the newest frames of a live capture;
/// zooming, panning, scrolling the frames or picking one lets go.
/// </remarks>
public partial class GameDevTabTracy : GameDevTab
{
	public override string Title => "Tracy";

	public override double RefreshSeconds => 0.25;

	/// <summary>Frames a following view shows.</summary>
	private const int FollowFrames = 3;

	private readonly Label _status = Readout();
	private readonly Label _advanced = Readout();
	private readonly Button _start = new() { Text = "Start capture" };
	private readonly Button _stop = new() { Text = "Stop" };
	private readonly Button _save = new() { Text = "Save .tracy" };
	private readonly Button _discard = new() { Text = "Discard" };
	private readonly Button _folder = new() { Text = "Open captures folder" };
	private readonly CheckBox _follow = new() { Text = "Follow", ButtonPressed = true };
	private readonly SpinBox _limit = new() { MinValue = 256, MaxValue = 65536, Step = 256, Value = 2048, Suffix = "MB cap" };
	private readonly GameDevTracyFrames _frames = new();
	private readonly GameDevTracyTimeline _timeline = new();

	private Task<bool>? _saving;
	private string _savingPath = string.Empty;
	private string _lastSave = string.Empty;
	private HostTracy.CaptureState _shownState = HostTracy.CaptureState.None;
	private int _rowsAge;

	public GameDevTabTracy()
	{
		if (!GameTracy.Enabled)
		{
			Section("Tracy is off");
			Label off = Readout();
			off.Text = System.OperatingSystem.IsAndroid()
				? "The game's own profiler is not running in this session.\n\n" +
					"It starts with the game when `tracy` is on in the runtime refii.toml, read once at\n" +
					"launch, and when the Android game host starts it (GameTracy.Start).\n\n" +
					"Everything stays inside the game: no network, no other programs."
				: "The game's own profiler is not running in this session.\n\n" +
					"Turn on `tracy` in the launcher (Settings → Developer) and start the game again: it is\n" +
					"read once at launch. `tracy_advanced` adds CPU sampling with call stacks and context\n" +
					"switches for the game's threads (on Windows the launcher then asks to run as\n" +
					"Administrator).\n\n" +
					"Everything stays inside the game: no network, no other programs.";
			AddChild(off);
			return;
		}

		var controls = new HBoxContainer();
		controls.AddThemeConstantOverride("separation", 8);
		foreach (Control control in new Control[] { _start, _stop, _save, _discard, _folder, _limit, _follow })
		{
			controls.AddChild(control);
		}

		// Android has no file manager to hand a folder to; the captures are in the app's user data.
		_folder.Visible = !System.OperatingSystem.IsAndroid();

		AddChild(controls);
		AddChild(_status);
		AddChild(_advanced);
		AddChild(_frames);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		scroll.AddChild(_timeline);
		AddChild(scroll);

		_start.Pressed += StartCapture;
		_stop.Pressed += () =>
		{
			HostTracy.Stop();
			Refresh();
		};
		_save.Pressed += SaveCapture;
		_discard.Pressed += () =>
		{
			HostTracy.Discard();
			_frames.Clear();
			_timeline.ReloadRows();
			Refresh();
		};
		_folder.Pressed += () => OS.ShellOpen(GameTracy.CapturesDirectory);
		_follow.Toggled += on =>
		{
			_frames.Follow = on;
			if (on)
			{
				Refresh();
			}
		};
		_frames.UserMoved += LetGo;
		_timeline.UserMoved += LetGo;
		_frames.FrameSelected += (start, end, _) =>
		{
			LetGo();
			long margin = Math.Max(1, (end - start) / 50);
			_timeline.SetView(start - margin, end + margin);
		};

		_advanced.Text = AdvancedNote();
	}

	private void LetGo()
	{
		_follow.SetPressedNoSignal(false);
		_frames.Follow = false;
	}

	private void StartCapture()
	{
		if (!HostTracy.Start((long)_limit.Value * 1024 * 1024, GameTracy.Advanced))
		{
			_status.Text = "the capture did not start (Tracy's client is not running, or a capture is live)";
			return;
		}

		_lastSave = string.Empty;
		_frames.Clear();
		_follow.ButtonPressed = true;
		_frames.Follow = true;
		_timeline.ReloadRows();
		Refresh();
	}

	private void SaveCapture()
	{
		_savingPath = GameTracy.NewCapturePath("capture");
		string path = _savingPath;
		_saving = Task.Run(() => HostTracy.Save(path));
		Refresh();
	}

	public override void Refresh()
	{
		if (!GameTracy.Enabled)
		{
			return;
		}

		// Saving holds the capture: every query would wait on it, so nothing asks until it is done.
		if (_saving is { } saving)
		{
			if (!saving.IsCompleted)
			{
				_status.Text = $"saving {_savingPath} …";
				SetButtons(HostTracy.CaptureState.None, busy: true);
				return;
			}

			_lastSave = saving.Result ? $"saved {_savingPath}" : $"could not save {_savingPath}";
			_saving = null;
		}

		HostTracy.Status status = HostTracy.GetStatus();
		HostTracy.CaptureState state = status.Capture;
		SetButtons(state, busy: false);
		_status.Text = Describe(status) + (_lastSave.Length != 0 ? "\n" + _lastSave : string.Empty);

		if (state == HostTracy.CaptureState.None)
		{
			_shownState = state;
			return;
		}

		// Threads appear as the game runs; the row list follows a live capture every two seconds.
		bool live = state is HostTracy.CaptureState.Live or HostTracy.CaptureState.Connecting;
		if (state != _shownState || _timeline.RowCount == 0 || (live && ++_rowsAge >= 8))
		{
			_rowsAge = 0;
			_timeline.ReloadRows();
		}

		_shownState = state;
		_timeline.Origin = status.FirstNs;
		_frames.Reload();
		if (_follow.ButtonPressed && _frames.Newest(FollowFrames) is { } newest)
		{
			_timeline.SetView(newest.Start, newest.End);
		}
		else if (live)
		{
			_timeline.Invalidate();
		}
	}

	private void SetButtons(HostTracy.CaptureState state, bool busy)
	{
		bool live = state is HostTracy.CaptureState.Live or HostTracy.CaptureState.Connecting;
		_start.Disabled = busy || live;
		_stop.Disabled = busy || !live;
		_save.Disabled = busy || state != HostTracy.CaptureState.Stopped;
		_discard.Disabled = busy || state == HostTracy.CaptureState.None || live;
		_limit.Editable = !busy && !live;
	}

	private static string Describe(HostTracy.Status status)
	{
		string state = status.Capture switch
		{
			HostTracy.CaptureState.None => "no capture — Start records from now until Stop",
			HostTracy.CaptureState.Connecting => "starting",
			HostTracy.CaptureState.Live => "LIVE",
			_ => status.Failure != 0 ? $"stopped (the stream ended: failure {status.Failure})" : "stopped",
		};
		if (status.Capture == HostTracy.CaptureState.None)
		{
			return state;
		}

		string limit = status.MemoryLimit > 0 ? $" of {status.MemoryLimit / 1048576} MB" : string.Empty;
		return $"{state}   ·   {status.Frames:N0} frames   ·   {status.Zones:N0} zones   ·   " +
			$"{GameDevTracyTimeline.FormatNs(Math.Max(0, status.LastNs - status.FirstNs))}   ·   " +
			$"{status.Samples:N0} samples   ·   context switches {(status.ContextSwitches != 0 ? "yes" : "no")}   ·   " +
			$"Worker {status.MemoryBytes / 1048576} MB{limit}";
	}

	private static string AdvancedNote()
	{
		if (!GameTracy.Advanced)
		{
			return "advanced data off (tracy_advanced): zones, plots, frames, messages and GPU only";
		}

		if (System.OperatingSystem.IsWindows())
		{
			return GameElevatedLaunch.IsAdministrator()
				? "advanced data on: sampling with call stacks and context switches (running as Administrator)"
				: "advanced data on, but the game is not running as Administrator — Windows leaves sampling and context switches empty";
		}

		return "advanced data on: sampling with call stacks; context switches need kernel.perf_event_paranoid at -1";
	}
}
