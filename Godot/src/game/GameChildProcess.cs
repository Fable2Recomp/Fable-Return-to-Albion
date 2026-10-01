using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Game;

/// <summary>
/// The game runs in a CHILD PROCESS of the launcher, not inside it.
/// </summary>
/// <remarks>
/// THE GUEST CAN ONLY START ONCE PER PROCESS. `RefiiHostStartup` refuses a second attempt —
/// "guest already started in this process; the runtime's global state" — because the recompiled
/// binary takes fixed memory reservations and sets up global state at init. So while the old
/// in-process game WINDOW did leave the launcher standing when it closed, the launcher was then
/// permanently unable to start another game: Play worked exactly once per run of the app.
///
/// A child process gives every Play a clean address space, so that constraint stops being a
/// limit on the UI. It also means a guest crash takes the game down and not the launcher.
///
/// SAME BINARY, TWO ROLES. The child is this very executable, so it needs some way to know which
/// role to take. That marker is an ENVIRONMENT VARIABLE rather than a command-line flag: nothing
/// a user types or sees, and it cannot collide with the arguments Godot itself parses.
/// </remarks>
public static class GameChildProcess
{
	/// <summary>Set by the launcher on the child it spawns; never set by hand.</summary>
	private const string Marker = "REFII_GAME_CHILD";

	/// <summary>True when this process was spawned by the launcher to BE the game.</summary>
	public static bool IsChild => OS.GetEnvironment(Marker) == "1";

	/// <summary>
	/// Starts the game as a child of this process.
	///
	/// EDITOR AND EXPORT TAKE THE SAME PATH. `OS.GetExecutablePath()` is the packaged game in an
	/// export and the Godot binary in the editor; the only difference is that the editor build
	/// needs telling which project to open, so `--path` is added there. The behaviour either
	/// side is identical — spawn, minimise, restore on exit — rather than the editor keeping a
	/// second, in-process code path that would then be the one nobody tests.
	/// </summary>
	public static bool Spawn(out Refii.Launcher.Core.ProcessHandle handle, out string error)
	{
		var arguments = new List<string>();
		if (OS.HasFeature("editor"))
		{
			arguments.Add("--path");
			arguments.Add(ProjectSettings.GlobalizePath("res://"));
		}

		// `render_thread` is a Godot start-up option, not a runtime setting: the renderer's
		// thread model is fixed before the game's own code runs, so it rides the command line.
		IReadOnlyDictionary<string, string> config = Refii.Core.Engine_RefiiConfig.Read(out _);
		if (Refii.Core.Engine_RefiiConfig.Bool(config, "render_thread", true))
		{
			arguments.Add("--render-thread");
			arguments.Add("separate");
		}

		// The graphics API is fixed when the rendering device is created, so it rides the command
		// line too. Direct3D 12 is Windows-only; everywhere else the driver is Vulkan whatever
		// refii.toml says.
		string driver = System.OperatingSystem.IsWindows()
			&& Refii.Core.Engine_RefiiConfig.String(config, "rendering_driver", "vulkan") == "d3d12"
				? "d3d12"
				: "vulkan";
		arguments.Add("--rendering-driver");
		arguments.Add(driver);

		// Godot's per-pass GPU timestamps (`RENDER_TIMESTAMP`) are captured only under
		// `--gpu-profile`. The switch is not bound to script, so it rides the command line like
		// the thread model. Read by Engine_GpuPasses.
		if (Refii.Core.Engine_RefiiConfig.Bool(config, "gpu_profile", false))
		{
			arguments.Add("--gpu-profile");
		}

		// Gen0 budget (32 MB). A gen1 pause is proportional to what survives it, so a smaller
		// budget trades frequency for shorter pauses. Read by the CLR at start-up, so it rides
		// the environment; a value already in the environment wins.
		var environment = new Dictionary<string, string> { [Marker] = "1" };
		if (System.Environment.GetEnvironmentVariable("DOTNET_GCgen0size") is null)
		{
			environment["DOTNET_GCgen0size"] = "0x2000000";
		}

		// Server GC with `gc_heaps` heaps: the workstation collector scans the old generation's
		// dirty cards on one thread; server GC splits that mark across heaps. Not affinitized:
		// the guest's threads own their cores.
		int gcHeaps = Refii.Core.Engine_RefiiConfig.Int(config, "gc_heaps", 4);
		if (gcHeaps > 0 && System.Environment.GetEnvironmentVariable("DOTNET_gcServer") is null)
		{
			environment["DOTNET_gcServer"] = "1";
			environment["DOTNET_GCHeapCount"] = gcHeaps.ToString(System.Globalization.CultureInfo.InvariantCulture);
			environment["DOTNET_GCNoAffinitize"] = "1";

			// Server GC's gen0 budget defaults to min(segment/2, 200 MB) PER HEAP and GCgen0size
			// is only its floor; `GCGen0MaxBudget` caps it (gc.cpp init_static_data). The
			// capital G is required: Linux env lookup is case-sensitive.
			if (System.Environment.GetEnvironmentVariable("DOTNET_GCGen0MaxBudget") is null)
			{
				environment["DOTNET_GCGen0MaxBudget"] = "0x1000000";
			}
		}

		// RenderDoc's Vulkan layer attaches at instance creation, before any game code runs, and
		// wakes only on `ENABLE_VULKAN_RENDERDOC_CAPTURE=1`, so arming happens here: find an
		// install, register its layer manifest for this user, set the variable for the child.
		if (Refii.Core.Engine_RenderDoc.Arm(config, environment, out string renderDocNote))
		{
			GD.Print("[launch] " + renderDocNote);
		}
		else if (renderDocNote.Length != 0)
		{
			GD.Print("[launch] " + renderDocNote);
		}

		GD.Print($"[launch] {OS.GetExecutablePath()} {string.Join(' ', arguments)} env " +
			string.Join(' ', environment.Select(kv => kv.Key + "=" + kv.Value)));

		return Refii.Launcher.Core.Platform.LaunchProcess(
			string.Empty,
			OS.GetExecutablePath(),
			arguments,
			environment,
			out handle,
			out error);
	}
}
