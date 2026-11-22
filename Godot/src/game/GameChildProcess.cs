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

	/// <summary>Set by the launcher when spawning the child with <c>--render-thread separate</c>.</summary>
	public const string SeparateRenderThreadEnvironmentVariable = Refii.Textures.Engine_TextureUpload.SeparateRenderThreadEnvironmentVariable;

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
	public static bool Spawn(bool elevated, out Refii.Launcher.Core.ProcessHandle handle, out string error) =>
		Spawn(Refii.Core.Engine_Title.Active, elevated, out handle, out error);

	/// <summary>
	/// Starts <paramref name="title"/> as a child of this launcher process, passing its own
	/// log path, runtime config settings, <c>REFII_TITLE</c>, and <c>REFII_PAYLOAD_MODULE</c>.
	/// </summary>
	public static bool Spawn(
		Refii.Core.Engine_Title title,
		bool elevated,
		out Refii.Launcher.Core.ProcessHandle handle,
		out string error)
	{
		var arguments = new List<string>();
		if (OS.HasFeature("editor"))
		{
			arguments.Add("--path");
			arguments.Add(ProjectSettings.GlobalizePath("res://"));
		}

		// The game's Godot log in the title's own logs folder, beside its guest logs (logs/Fable2,
		// logs/Fable3): two titles' runs never share one file. Engine flags go ahead of the rest.
		try { System.IO.Directory.CreateDirectory(title.LogsDirectory); }
		catch (System.Exception) { }
		IReadOnlyDictionary<string, string> config = Refii.Core.Engine_RefiiConfig.ReadFor(title, out _);
		int maxLogFiles = System.Math.Max(1, Refii.Core.Engine_RefiiConfig.Int(config, "log_max_files", 20));
		RotateExistingLogFile(title.GodotLogPath, maxLogFiles);
		arguments.Add("--log-file");
		arguments.Add(title.GodotLogPath);

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

		// `render_thread` is a Godot start-up option, not a runtime setting: the renderer's
		// thread model is fixed before the game's own code runs, so it rides the command line.
		bool separateRenderThread = ShouldUseSeparateRenderThread(config, driver);
		if (separateRenderThread)
		{
			arguments.Add("--render-thread");
			arguments.Add("separate");
		}

		arguments.Add("--rendering-driver");
		arguments.Add(driver);

		// Godot's per-pass GPU timestamps (`RENDER_TIMESTAMP`) are captured only under
		// `--gpu-profile`. Omit `--gpu-profile` by default on Direct3D 12 (where safe-thread command
		// recording pays per-pass timestamp query resolve overhead without separate-thread overlap)
		// unless explicitly requested via REFII_D3D12_GPU_PROFILE=1. Read by Engine_GpuPasses.
		bool allowGpuProfile = !string.Equals(driver, "d3d12", System.StringComparison.OrdinalIgnoreCase) ||
			OS.GetEnvironment("REFII_D3D12_GPU_PROFILE") == "1";
		if (allowGpuProfile && Refii.Core.Engine_RefiiConfig.Bool(config, "gpu_profile", false))
		{
			arguments.Add("--gpu-profile");
		}

		// THE TITLE TRAVELS WITH THE PROCESS: the child reads its game from the first variable
		// (Engine_Title.Active) and the native payload loader opens the module the second names.
		var environment = new Dictionary<string, string>
		{
			[Marker] = "1",
			[Refii.Core.Engine_Title.EnvironmentVariable] = title.Key,
			[Refii.Core.Engine_Title.PayloadEnvironmentVariable] = title.ModuleName,
		};
		if (separateRenderThread)
		{
			// Godot strips `--render-thread separate` before `OS.GetCmdlineArgs()` reaches C#;
			// forwarding an explicit environment marker keeps Engine_TextureUpload accurate.
			environment[SeparateRenderThreadEnvironmentVariable] = "1";
		}

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

	/// <summary>Maximum staging buffer size in megabytes for upload pacing on unified memory GPUs.</summary>
	public const int DefaultStagingBufferMaxSizeMb = 64;

	/// <summary>
	/// Total D3D12 CBV/SRV/UAV descriptor heap capacity. Godot 4.7 allocates every live uniform set
	/// from this one heap for as long as the set lives; when it is full, sets fail to create and the
	/// release renderer faults drawing with a null one. 1,000,000 is the most a shader-visible heap
	/// may hold on resource binding tier 1 and 2 hardware, so it is also the ceiling for an override.
	/// </summary>
	public const int DefaultD3D12MaxResourceDescriptors = MaxD3D12ResourceDescriptors;

	/// <summary>Direct3D 12's shader-visible CBV/SRV/UAV heap limit on resource binding tiers 1 and 2.</summary>
	public const int MaxD3D12ResourceDescriptors = 1000000;

	/// <summary>Per-frame D3D12 CBV/SRV/UAV descriptor ring budget.</summary>
	public const int DefaultD3D12MaxResourceDescriptorsPerFrame = 65536;

	/// <summary>
	/// Shader-visible D3D12 sampler heap limit. Must never exceed Direct3D 12's architectural ceiling
	/// (<c>D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE = 2048</c>), or spec-strict drivers (such as Intel Arc)
	/// fail <c>ID3D12Device::CreateDescriptorHeap</c> with <c>0x80070057</c> (<c>E_INVALIDARG</c>).
	/// </summary>
	public const int DefaultD3D12MaxSamplerDescriptors = 2048;

	/// <summary>Per-frame shader-visible D3D12 sampler descriptor limit (capped at 2048 per D3D12 spec).</summary>
	public const int DefaultD3D12MaxSamplerDescriptorsPerFrame = 2048;

	/// <summary>Per-frame non-shader-visible RTV/DSV descriptor capacity.</summary>
	public const int DefaultD3D12MaxMiscDescriptorsPerFrame = 4096;

	/// <summary>
	/// Builds the `user://renderer.cfg` override content read by Godot before the child's code runs.
	/// Classic turns the depth pre-pass off: its cascades then draw each caster once, and the main
	/// world keeps its pre-pass through <c>Engine_DepthPrepassMarker</c>. Modern keeps the project
	/// default, because its picture depends on the setting itself, not only on the pre-pass.
	/// Staging buffer size is capped at 128 MB so permanently mapped D3D12 UPLOAD blocks do not
	/// starve MultiUmaBuffer persistent dynamic storage buffer allocations on unified-memory GPUs.
	/// Optional environment overrides (<c>REFII_D3D12_SOFT_SHADOW_QUALITY</c>,
	/// <c>REFII_D3D12_MAX_RESOURCE_DESCRIPTORS</c>, <c>REFII_D3D12_MAX_RESOURCE_DESCRIPTORS_PER_FRAME</c>)
	/// permit single-run D3D12 isolation without editing <c>project.godot</c>.
	/// </summary>
	public static string BuildRendererSettingsContent(
		IReadOnlyDictionary<string, string> config,
		string forcedLightingModel = "",
		IReadOnlyDictionary<string, string?>? envOverrides = null)
	{
		string? GetEnv(string key) => envOverrides is not null
			? (envOverrides.TryGetValue(key, out string? v) ? v : null)
			: System.Environment.GetEnvironmentVariable(key);

		int ReadPositiveIntOverride(string key, int fallback, int min, int max) =>
			int.TryParse(GetEnv(key), out int parsed) && parsed >= min
				? System.Math.Clamp(parsed, min, max)
				: fallback;

		bool classic = Refii.Lighting.Engine_LightingFrame.IsClassic(
			forcedLightingModel.Length != 0
				? forcedLightingModel
				: Refii.Core.Engine_RefiiConfig.String(config, "lighting_model", "modern"));
		int maxResDesc = ReadPositiveIntOverride(
			"REFII_D3D12_MAX_RESOURCE_DESCRIPTORS", DefaultD3D12MaxResourceDescriptors, 16384, MaxD3D12ResourceDescriptors);
		int maxResDescPerFrame = ReadPositiveIntOverride(
			"REFII_D3D12_MAX_RESOURCE_DESCRIPTORS_PER_FRAME", DefaultD3D12MaxResourceDescriptorsPerFrame, 8192, maxResDesc);

		var sb = new System.Text.StringBuilder();
		sb.Append("[rendering]\n\n");
		sb.Append($"rendering_device/staging_buffer/max_size_mb={DefaultStagingBufferMaxSizeMb}\n");
		sb.Append($"rendering_device/d3d12/max_resource_descriptors={maxResDesc}\n");
		sb.Append($"rendering_device/d3d12/max_sampler_descriptors={DefaultD3D12MaxSamplerDescriptors}\n");
		sb.Append($"rendering_device/d3d12/max_resource_descriptors_per_frame={maxResDescPerFrame}\n");
		sb.Append($"rendering_device/d3d12/max_sampler_descriptors_per_frame={DefaultD3D12MaxSamplerDescriptorsPerFrame}\n");
		sb.Append($"rendering_device/d3d12/max_misc_descriptors_per_frame={DefaultD3D12MaxMiscDescriptorsPerFrame}\n");
		if (int.TryParse(GetEnv("REFII_D3D12_SOFT_SHADOW_QUALITY"), out int softShadowQuality) &&
		    softShadowQuality is >= 0 and <= 5)
		{
			sb.Append($"lights_and_shadows/directional_shadow/soft_shadow_filter_quality={softShadowQuality}\n");
		}
		if (classic)
		{
			sb.Append("driver/depth_prepass/enable=false\n");
		}
		return sb.ToString();
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

	/// <summary>
	/// Renames an existing <c>godot.log</c> to a timestamped archive (<c>prev_godot_YYYY-MM-DDTHH-mm-ss.log</c>)
	/// before spawning a new child process so prior runs (e.g. a DX12 freeze before switching to Vulkan)
	/// are preserved without being matched by Godot's <c>RotatedFileLogger::clear_old_backups()</c>,
	/// while pruning archives beyond <paramref name="maxFiles"/>.
	/// </summary>
	public static void RotateExistingLogFile(string logPath, int maxFiles = 20)
	{
		if (string.IsNullOrWhiteSpace(logPath) || !System.IO.File.Exists(logPath))
		{
			return;
		}

		try
		{
			string? dir = System.IO.Path.GetDirectoryName(logPath);
			if (string.IsNullOrEmpty(dir))
			{
				return;
			}

			string stem = System.IO.Path.GetFileNameWithoutExtension(logPath);
			string ext = System.IO.Path.GetExtension(logPath);
			System.DateTime stamp = System.IO.File.GetLastWriteTime(logPath);
			string baseName = $"prev_{stem}_{stamp:yyyy-MM-ddTHH-mm-ss}";
			string dest = System.IO.Path.Combine(dir, baseName + ext);
			for (int suffix = 1; System.IO.File.Exists(dest); suffix++)
			{
				dest = System.IO.Path.Combine(dir, $"{baseName}_{suffix}{ext}");
			}

			System.IO.File.Move(logPath, dest);

			int keepArchives = System.Math.Max(0, maxFiles - 1);
			var archives = System.IO.Directory.GetFiles(dir, $"prev_{stem}_*{ext}")
				.Select(static p => new System.IO.FileInfo(p))
				.OrderByDescending(static f => f.LastWriteTimeUtc)
				.ThenByDescending(f => string.Equals(f.FullName, dest, System.StringComparison.OrdinalIgnoreCase))
				.ThenByDescending(static f => f.Name, System.StringComparer.OrdinalIgnoreCase)
				.Skip(keepArchives);
			foreach (System.IO.FileInfo old in archives)
			{
				try { old.Delete(); }
				catch (System.Exception) { }
			}
		}
		catch (System.Exception) { }
	}

	/// <summary>
	/// Whether to pass <c>--render-thread separate</c> to the spawned game process,
	/// honoring the <c>render_thread</c> CVar regardless of rendering driver.
	/// </summary>
	public static bool ShouldUseSeparateRenderThread(IReadOnlyDictionary<string, string> config, string resolvedDriver) =>
		Refii.Core.Engine_RefiiConfig.Bool(config, "render_thread", true);
}
