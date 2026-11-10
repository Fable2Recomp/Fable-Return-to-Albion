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
	/// <param name="elevated">Run the game as Administrator, through <see cref="GameElevatedLaunch"/>
	/// (Windows; Tracy's advanced data). The handle is then the relay's, which exits with the game.</param>
	public static bool Spawn(bool elevated, out Refii.Launcher.Core.ProcessHandle handle, out string error)
	{
		var arguments = new List<string>();
		if (OS.HasFeature("editor"))
		{
			arguments.Add("--path");
			arguments.Add(ProjectSettings.GlobalizePath("res://"));
		}

		// The game's Godot log in the title's own logs folder, beside its guest logs (logs/Fable2,
		// logs/Fable3): two titles' runs never share one file. Engine flags go ahead of the rest.
		try { System.IO.Directory.CreateDirectory(Refii.Core.Engine_Title.Active.LogsDirectory); }
		catch (System.Exception) { }
		arguments.Add("--log-file");
		arguments.Add(Refii.Core.Engine_Title.Active.GodotLogPath);
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
		// refii.toml says. HDR output on Windows requires Direct3D 12 (Godot's Windows display
		// server does not support HDR swapchains under Vulkan).
		WriteRendererSettings(config);

		string driver = ResolveRenderingDriver(config, System.OperatingSystem.IsWindows());
		if (System.OperatingSystem.IsWindows()
			&& Refii.Core.Engine_RefiiConfig.Bool(config, "hdr_output", false)
			&& Refii.Core.Engine_RefiiConfig.String(config, "rendering_driver", "d3d12") == "vulkan")
		{
			GD.Print("[launch] HDR output is enabled: using Direct3D 12 rendering driver on Windows (Vulkan does not support HDR swapchains in Godot)");
		}
		arguments.Add("--rendering-driver");
		arguments.Add(driver);

		// Godot's per-pass GPU timestamps (`RENDER_TIMESTAMP`) are captured only under
		// `--gpu-profile`. The switch is not bound to script, so it rides the command line like
		// the thread model. Read by Engine_GpuPasses.
		if (Refii.Core.Engine_RefiiConfig.Bool(config, "gpu_profile", false))
		{
			arguments.Add("--gpu-profile");
		}

		// THE TITLE TRAVELS WITH THE PROCESS: the child reads its game from the first variable
		// (Engine_Title.Active) and the native payload loader opens the module the second names.
		var environment = new Dictionary<string, string>
		{
			[Marker] = "1",
			[Refii.Core.Engine_Title.EnvironmentVariable] = Refii.Core.Engine_Title.Active.Key,
			[Refii.Core.Engine_Title.PayloadEnvironmentVariable] = Refii.Core.Engine_Title.Active.ModuleName,
		};

		// Gen0 budget (32 MB). A gen1 pause is proportional to what survives it, so a smaller
		// budget trades frequency for shorter pauses. Read by the CLR at start-up, so it rides
		// the environment; a value already in the environment wins.
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

		// Ensure the bin directory is in PATH so child processes and their delay-loaded libraries
		// (such as TracyClient.dll) can be located by the operating system loader.
		string binDir = Refii.Core.Engine_AppRoot.BinDirectory;
		if (System.IO.Directory.Exists(binDir))
		{
			string currentPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			environment["PATH"] = binDir + System.IO.Path.PathSeparator + currentPath;
		}

		GD.Print($"[launch] {OS.GetExecutablePath()} {string.Join(' ', arguments)} env " +
			string.Join(' ', environment.Select(kv => kv.Key + "=" + kv.Value)) +
			(elevated ? " (as Administrator)" : string.Empty));

		if (elevated)
		{
			return GameElevatedLaunch.Launch(OS.GetExecutablePath(), arguments, environment, out handle, out error);
		}

		return Refii.Launcher.Core.Platform.LaunchProcess(
			string.Empty,
			OS.GetExecutablePath(),
			arguments,
			environment,
			out handle,
			out error);
	}

	/// <summary>Where `application/config/project_settings_override` points (project.godot).</summary>
	private const string RendererSettingsPath = "user://renderer.cfg";

	public const int DefaultStagingBufferMaxSizeMb = 128;
	public const int DefaultD3D12MaxResourceDescriptorsPerFrame = 131072;
	public const int DefaultD3D12MaxSamplerDescriptorsPerFrame = 4096;
	public const int DefaultD3D12MaxMiscDescriptorsPerFrame = 4096;

	/// <summary>
	/// Builds the `user://renderer.cfg` override content read by Godot before the child's code runs.
	/// Classic turns the depth pre-pass off: its cascades then draw each caster once, and the main
	/// world keeps its pre-pass through <c>Engine_DepthPrepassMarker</c>. Modern keeps the project
	/// default, because its picture depends on the setting itself, not only on the pre-pass.
	/// Staging buffer size is capped at 128 MB so permanently mapped D3D12 UPLOAD blocks do not
	/// starve MultiUmaBuffer persistent dynamic storage buffer allocations on unified-memory GPUs.
	/// </summary>
	public static string BuildRendererSettingsContent(
		IReadOnlyDictionary<string, string> config,
		string forcedLightingModel = "")
	{
		bool classic = Refii.Lighting.Engine_LightingFrame.IsClassic(
			forcedLightingModel.Length != 0
				? forcedLightingModel
				: Refii.Core.Engine_RefiiConfig.String(config, "lighting_model", "modern"));
		string common =
			"[rendering]\n\n" +
			$"rendering_device/staging_buffer/max_size_mb={DefaultStagingBufferMaxSizeMb}\n" +
			$"rendering_device/d3d12/max_resource_descriptors_per_frame={DefaultD3D12MaxResourceDescriptorsPerFrame}\n" +
			$"rendering_device/d3d12/max_sampler_descriptors_per_frame={DefaultD3D12MaxSamplerDescriptorsPerFrame}\n" +
			$"rendering_device/d3d12/max_misc_descriptors_per_frame={DefaultD3D12MaxMiscDescriptorsPerFrame}\n";
		return classic
			? common + "driver/depth_prepass/enable=false\n"
			: common;
	}

	private static void WriteRendererSettings(IReadOnlyDictionary<string, string> config)
	{
		string forced = OS.GetEnvironment("REFII_LIGHTING_MODEL");
		string path = ProjectSettings.GlobalizePath(RendererSettingsPath);
		try
		{
			string content = BuildRendererSettingsContent(config, forced);
			System.IO.File.WriteAllText(path, content);
		}
		catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
		{
			GD.PushWarning($"[launch] renderer settings not written to {path}: {ex.Message}");
		}
	}

	/// <summary>
	/// Chooses the graphics API passed to the child process.
	/// Direct3D 12 is Windows-only; everywhere else the driver is Vulkan whatever refii.toml says.
	/// On Windows, HDR output requires Direct3D 12 (Godot's Windows display server does not
	/// support HDR swapchains under Vulkan), so requesting HDR automatically selects Direct3D 12.
	/// </summary>
	public static string ResolveRenderingDriver(IReadOnlyDictionary<string, string> config, bool isWindows)
	{
		if (!isWindows)
		{
			return "vulkan";
		}

		bool hdrWanted = Refii.Core.Engine_RefiiConfig.Bool(config, "hdr_output", false);
		string configured = Refii.Core.Engine_RefiiConfig.String(config, "rendering_driver", "d3d12");
		if (hdrWanted && configured != "d3d12")
		{
			return "d3d12";
		}

		return configured == "d3d12" ? "d3d12" : "vulkan";
	}
}
