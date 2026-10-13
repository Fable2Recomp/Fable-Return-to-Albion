using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Refii.Launcher.Core;
using Refii.Launcher.Recompile;

namespace Refii.Launcher.Install;

/// <summary>
/// The one runner for everything the launcher derives from the player's own game: the game
/// data from the disc, the recompiled game code, the classic shaders and the modernized assets.
/// </summary>
/// <remarks>
/// ONE STATE, ONE PROGRESS, ONE PAUSE AND STOP. Each of these used to be its own flow in the
/// launcher with its own flags and status strings, and the install was a chain of them; a
/// fourth kind of work meant a fourth triplet of methods and a fourth set of conditions in every
/// refresh. Here a run is a list of steps, the UI reads <see cref="Label"/>,
/// <see cref="Status"/>, <see cref="Fraction"/> and <see cref="Error"/>, and whichever step is
/// running exposes its <see cref="Control"/> when it can be held.
///
/// STEPS RUN IN ORDER AND THE FIRST FAILURE OR STOP ENDS THE RUN. The install dialog and the
/// Install tab both build their list and hand it here; the tab's single buttons are one-step
/// lists.
///
/// THE WORKERS NEVER TOUCH A CONTROL. Progress callbacks arrive on worker threads and write
/// the strings here; the launcher paints them from its own tick.
/// </remarks>
public sealed class InstallPipeline
{
	public enum StepKind
	{
		/// <summary>Extract the ISO and install the title update beside it.</summary>
		GameData,
		/// <summary>Generate and compile the recompiled game module.</summary>
		GameCode,
		/// <summary>Translate the shader bank into the classic renderer's shaders.</summary>
		ClassicShaders,
		/// <summary>Build every modernized texture and material into the cache.</summary>
		ModernizedAssets,
	}

	/// <summary>One unit of a run. The disc paths matter to <see cref="StepKind.GameData"/> only.</summary>
	public sealed record Step(StepKind Kind, string IsoPath = "", string TitleUpdatePath = "");

	/// <summary>How a run ended. A stop is the player's choice and is not an error.</summary>
	public readonly record struct Outcome(bool Ok, bool Stopped, StepKind? FailedStep, string Error);

	private volatile bool _running;
	private volatile string _label = string.Empty;
	private volatile string _status = string.Empty;
	private volatile string _error = string.Empty;
	private float _fraction = -1.0f;
	private BuildControl? _control;
	private StepKind? _current;

	/// <summary>True from the first step's start to the run's end.</summary>
	public bool Running => _running;

	/// <summary>The running step's name, for the primary button and the Install tab.</summary>
	public string Label => _label;

	/// <summary>The running step's one-line status.</summary>
	public string Status => _status;

	/// <summary>The last run's failure, until the next run starts. Empty when none.</summary>
	public string Error => _error;

	/// <summary>0..1 of the running step, or -1 when it has no measure.</summary>
	public float Fraction => Volatile.Read(ref _fraction);

	/// <summary>The running step's pause and stop, while one can be held.</summary>
	public BuildControl? Control => _control;

	/// <summary>The step running now, or null.</summary>
	public StepKind? Current => _current;

	/// <summary>
	/// The status as shown, which says so when the step is held or stopping: the status line
	/// itself only moves when the work does.
	/// </summary>
	public string DisplayStatus()
	{
		BuildControl? control = _control;
		if (control is null)
		{
			return _status;
		}
		if (control.IsStopping)
		{
			return "Stopping... " + _status;
		}
		if (control.IsPaused)
		{
			return "Paused... " + _status;
		}
		return _status;
	}

	/// <summary>Holds the running step, or releases it.</summary>
	public void TogglePause(Action<string> log)
	{
		BuildControl? control = _control;
		if (control is null)
		{
			return;
		}
		if (control.IsPaused)
		{
			control.Resume();
			log(_label + " resumed.");
		}
		else
		{
			control.Pause();
			log(_label + " paused.");
		}
	}

	/// <summary>Asks the running step to stop. It ends at its next event.</summary>
	public void Stop(Action<string> log)
	{
		BuildControl? control = _control;
		if (control is null || control.IsStopping)
		{
			return;
		}
		control.Stop();
		log(_label + " stop requested.");
	}

	/// <summary>Runs the steps in order on the calling (main) thread's continuations.</summary>
	public async Task<Outcome> RunAsync(IReadOnlyList<Step> steps, Action<string> log)
	{
		if (_running)
		{
			return new Outcome(false, false, null, "another install step is running");
		}

		_running = true;
		_error = string.Empty;
		try
		{
			foreach (Step step in steps)
			{
				_current = step.Kind;
				Volatile.Write(ref _fraction, -1.0f);
				Outcome outcome = step.Kind switch
				{
					StepKind.GameData => await InstallGameDataAsync(step, log),
					StepKind.GameCode => await BuildGameCodeAsync(log),
					StepKind.ClassicShaders => await BuildShadersAsync(log),
					StepKind.ModernizedAssets => await BuildModernizedAssetsAsync(log),
					_ => new Outcome(false, false, step.Kind, "unknown step"),
				};
				if (!outcome.Ok)
				{
					return outcome;
				}
			}
			return new Outcome(true, false, null, string.Empty);
		}
		finally
		{
			_current = null;
			_control = null;
			_running = false;
			Volatile.Write(ref _fraction, -1.0f);
		}
	}

	private Outcome Fail(StepKind step, string stage, string error, Action<string> log)
	{
		_status = stage;
		_error = error.Length == 0 ? stage : error;
		log(_error);
		return new Outcome(false, false, step, _error);
	}

	private Outcome Stopped(string what, Action<string> log)
	{
		_label = string.Empty;
		_status = string.Empty;
		_error = string.Empty;
		log(what + " stopped.");
		return new Outcome(false, true, _current, "stopped");
	}

	private async Task<Outcome> InstallGameDataAsync(Step step, Action<string> log)
	{
		_label = "Installing";
		_status = "Starting installation...";

		if (!Installer.InstallBasePathLeavesWindowsPathRoom(
				Installer.GetInstallBasePath(), out string pathError))
		{
			return Fail(StepKind.GameData, "Install path is too long.", pathError, log);
		}

		string destination = Installer.GetGameDataDirectoryPath();
		_status = "Extracting ISO...";
		log("Extracting " + step.IsoPath + " to " + destination);

		(bool ok, string error) = await Task.Run(() =>
		{
			bool extracted = Xiso.XisoExtractor.ExtractImage(
				step.IsoPath,
				destination,
				out string extractError,
				progress =>
				{
					if (progress.TotalBytes > 0)
					{
						Volatile.Write(ref _fraction,
							Math.Clamp((float)progress.CopiedBytes / progress.TotalBytes, 0.0f, 1.0f));
					}
					string status = progress.TotalFiles <= 0
						? "Extracting ISO..."
						: "Extracting ISO... " + progress.CompletedFiles + "/" + progress.TotalFiles;
					_status = progress.CurrentFile.Length == 0
						? status
						: status + " (" + progress.CurrentFile + ")";
				});
			return (extracted, extractError);
		});

		if (!ok)
		{
			return Fail(StepKind.GameData, _status, error, log);
		}

		_status = step.TitleUpdatePath.Length == 0
			? "Finishing the game install..."
			: "Installing title update...";
		Volatile.Write(ref _fraction, -1.0f);

		if (!Installer.InstallTitleUpdate(
				step.TitleUpdatePath, Installer.GetGameMarkerPath(), out string updateError))
		{
			return Fail(StepKind.GameData, _status, updateError, log);
		}

		Installer.InvalidateReinstallStepStatus();
		log(Installer.CheckInstalledTitleUpdate(out string pair) ? pair : "Title update check: " + pair);
		return new Outcome(true, false, null, string.Empty);
	}

	/// <summary>
	/// The longest step by a wide margin — a compiler building roughly 150,000 functions —
	/// and not optional: the recompiled guest is derived from the player's own image.
	/// </summary>
	private async Task<Outcome> BuildGameCodeAsync(Action<string> log)
	{
		_label = "Building game code";
		_status = "Preparing game code...";
		Volatile.Write(ref _fraction, 0.0f);
		log("Building the recompiled game module from the installed executable.");

		GameCodeBuild.Result result;
		using (var control = new BuildControl())
		{
			_control = control;
			try
			{
				result = await GameCodeBuild.RunAsync(
					progress =>
					{
						_status = progress.Status;
						Volatile.Write(ref _fraction, progress.Fraction);
					},
					control);
			}
			finally
			{
				_control = null;
			}
		}

		GameCodeBuild.InvalidateStatus();
		if (result.Stopped)
		{
			return Stopped("Game code build", log);
		}
		if (!result.Ok)
		{
			return Fail(StepKind.GameCode, "Building game code failed.", result.Error, log);
		}
		log("Game code built: " + GameCodeBuild.ModulePath);
		return new Outcome(true, false, null, string.Empty);
	}

	private async Task<Outcome> BuildShadersAsync(Action<string> log)
	{
		_label = "Compiling shaders";
		_status = "Preparing shader compile...";
		Volatile.Write(ref _fraction, 0.0f);
		log("Compiling the shader banks from the installed game data.");

		GameCodeBuild.Result result;
		using (var control = new BuildControl())
		{
			_control = control;
			try
			{
				result = await Shaders.ShaderBuild.RunAsync(
					progress =>
					{
						_status = progress.Status;
						Volatile.Write(ref _fraction, progress.Fraction);
					},
					control, log);
			}
			finally
			{
				_control = null;
			}
		}

		if (result.Stopped)
		{
			return Stopped("Shader compile", log);
		}
		if (!result.Ok)
		{
			return Fail(StepKind.ClassicShaders, "Compiling shaders failed.", result.Error, log);
		}
		log("Shaders compiled: " + Shaders.ShaderBuild.ReportPath);
		return new Outcome(true, false, null, string.Empty);
	}

	/// <summary>
	/// Minutes of decoding and upscaling, on a background thread so closing the launcher
	/// mid-run cannot leave a thread holding the cache open; what was written stays written.
	/// </summary>
	private async Task<Outcome> BuildModernizedAssetsAsync(Action<string> log)
	{
		string directory = Refii.Core.Engine_GameData.Resolve();
		if (directory.Length == 0)
		{
			return Fail(StepKind.ModernizedAssets, "Building modernized assets",
				"No game data found to build modernized assets from.", log);
		}

		// REGISTERED HERE, because the launcher process has never touched a texture and so has
		// never configured the pipeline. Without it the backend is null and the whole run
		// silently does base decodes only — which looks exactly like a successful prebuild.
		Refii.Textures.Engine_TexEnhance.RegisterDefault();

		_label = "Building modernized assets";
		_status = "Finding what to build...";
		log($"Building modernized assets from {directory}");

		var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				Refii.Textures.Engine_TexPrebuild.Progress result =
					Refii.Textures.Engine_TexPrebuild.Run(
						directory,
						progress =>
						{
							if (progress.Total > 0)
							{
								Volatile.Write(ref _fraction, (float)progress.Done / progress.Total);
							}
							_status = progress.Total == 0
								? "Finding what to build..."
								: $"{progress.Done:N0} of {progress.Total:N0} " +
								  $"({100.0 * progress.Done / progress.Total:F0}%) — " +
								  $"built {progress.Built:N0}, failed {progress.Failed:N0}";
						});

				done.SetResult(result.Total == 0
					? "Nothing to build — no material records were found."
					: $"Built {result.Built:N0} of {result.Total:N0}" +
					  (result.Failed > 0 ? $", {result.Failed:N0} could not be read." : "."));
			}
			catch (Exception exception)
			{
				done.SetException(exception);
			}
		})
		{
			IsBackground = true,
			Name = "refii-prebuild",
			Priority = ThreadPriority.BelowNormal,
		};
		thread.Start();

		try
		{
			string summary = await done.Task;
			log("Modernized assets: " + summary);
			return new Outcome(true, false, null, string.Empty);
		}
		catch (Exception exception)
		{
			return Fail(StepKind.ModernizedAssets, "Building modernized assets failed.",
				exception.Message, log);
		}
	}
}
