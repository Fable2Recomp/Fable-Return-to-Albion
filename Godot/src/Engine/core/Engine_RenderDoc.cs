using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Refii.Core;

/// <summary>
/// RenderDoc for the running game: the launcher arms its Vulkan capture layer for a run, and
/// the game takes the layer's in-application API so a frame capture can be triggered from the
/// debug channel and found afterwards.
/// </summary>
/// <remarks>
/// No engine change and no injection. RenderDoc's layer is an ordinary implicit Vulkan layer:
/// the loader adds it at instance creation when its manifest is registered for the user and
/// `ENABLE_VULKAN_RENDERDOC_CAPTURE=1` is in the environment, and it stays dormant otherwise. The
/// launcher (<see cref="Arm"/>) finds an install, writes that manifest with the install's
/// absolute library path, and sets the variable on the child. Inside the child
/// (<see cref="Install"/>) the library is already loaded, so `RENDERDOC_GetAPI` answers with the
/// function table the capture calls go through.
///
/// The table is read by slot, not by a struct definition: `RENDERDOC_API_1_6_0` is a plain array
/// of function pointers and every later version appends to it, so the indices below are stable
/// for as long as the version asked for is honoured.
/// </remarks>
public static class Engine_RenderDoc
{
	/// <summary>The layer's own wake-up variable, from its manifest's `enable_environment`.</summary>
	public const string EnableVariable = "ENABLE_VULKAN_RENDERDOC_CAPTURE";

	/// <summary>Where the launcher found the library, so the child loads exactly that one.</summary>
	public const string LibraryVariable = "REFII_RENDERDOC_LIB";

	private const int ApiVersion160 = 10600;

	// Slots in RENDERDOC_API_1_6_0, counted from the header (renderdoc_app.h, 1.46).
	private const int SlotGetApiVersion = 0;
	private const int SlotSetCaptureOptionU32 = 1;
	private const int SlotSetCaptureFilePathTemplate = 11;
	private const int SlotGetNumCaptures = 13;
	private const int SlotGetCapture = 14;
	private const int SlotTriggerCapture = 15;
	private const int SlotIsFrameCapturing = 20;

	// eRENDERDOC_Option_* ids.
	private const int OptionApiValidation = 2;
	private const int OptionCaptureCallstacks = 3;
	private const int OptionDebugOutputMute = 11;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int GetApiDelegate(int version, out IntPtr api);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void GetApiVersionDelegate(out int major, out int minor, out int patch);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int SetCaptureOptionU32Delegate(int option, uint value);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void SetCaptureFilePathTemplateDelegate(
		[MarshalAs(UnmanagedType.LPStr)] string template);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate uint GetNumCapturesDelegate();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate uint GetCaptureDelegate(uint index, IntPtr filename, IntPtr pathLength, IntPtr timestamp);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void TriggerCaptureDelegate();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate uint IsFrameCapturingDelegate();

	private static TriggerCaptureDelegate? _trigger;
	private static IsFrameCapturingDelegate? _isCapturing;
	private static GetNumCapturesDelegate? _numCaptures;
	private static GetCaptureDelegate? _getCapture;

	/// <summary>True once the in-application API is in hand.</summary>
	public static bool Armed => _trigger is not null;

	/// <summary>The RenderDoc version that answered, as `major.minor.patch`.</summary>
	public static string Version { get; private set; } = string.Empty;

	/// <summary>Where captures land; `refii_frameN.rdc` inside it.</summary>
	public static string CaptureDirectory { get; private set; } = string.Empty;

	/// <summary>The install's root (holding bin/ and lib/), when the launcher named the library.</summary>
	public static string InstallRoot { get; private set; } = string.Empty;

	/// <summary>
	/// The game's half: takes the layer's API when the launcher armed it. False and silent when
	/// this run was not armed; false with a note when it was and the layer did not attach.
	/// </summary>
	public static bool Install(string captureDirectory, out string note)
	{
		note = string.Empty;
		if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
		{
			return false;
		}

		// The copy the loader brought in first: a plain name resolves to the library already in
		// the process under its SONAME, whereas the launcher's path could map a second RenderDoc
		// when another manifest won the loader's pick. The path is the fallback, and names the
		// install the tools beside it come from.
		string? named = Environment.GetEnvironmentVariable(LibraryVariable);
		NativeLibrary.TryLoad(OperatingSystem.IsWindows() ? "renderdoc.dll" : "librenderdoc.so", out IntPtr handle);
		if (!string.IsNullOrEmpty(named))
		{
			if (handle == IntPtr.Zero)
			{
				NativeLibrary.TryLoad(named, out handle);
			}

			string? lib = Path.GetDirectoryName(named);
			InstallRoot = lib is null ? string.Empty : Path.GetDirectoryName(lib) ?? string.Empty;
		}

		if (handle == IntPtr.Zero)
		{
			note = "armed by the launcher, but the RenderDoc library is not in the process — the " +
				"Vulkan layer did not attach (is the manifest registered and the library path right?)";
			return false;
		}

		if (!NativeLibrary.TryGetExport(handle, "RENDERDOC_GetAPI", out IntPtr getApiAddress))
		{
			note = "the loaded RenderDoc library has no RENDERDOC_GetAPI";
			return false;
		}

		var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiAddress);
		if (getApi(ApiVersion160, out IntPtr api) != 1 || api == IntPtr.Zero)
		{
			note = "RENDERDOC_GetAPI refused API version 1.6.0";
			return false;
		}

		T Slot<T>(int index) =>
			Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(api, index * IntPtr.Size));

		Slot<GetApiVersionDelegate>(SlotGetApiVersion)(out int major, out int minor, out int patch);
		Version = $"{major}.{minor}.{patch}";

		Directory.CreateDirectory(captureDirectory);
		CaptureDirectory = captureDirectory;
		Slot<SetCaptureFilePathTemplateDelegate>(SlotSetCaptureFilePathTemplate)(
			Path.Combine(captureDirectory, "refii"));

		// What a pass-timing capture needs and nothing that slows the frame: no callstacks per
		// API call, no validation layer, and the layer's own chatter kept out of the log.
		var setOption = Slot<SetCaptureOptionU32Delegate>(SlotSetCaptureOptionU32);
		setOption(OptionCaptureCallstacks, 0u);
		setOption(OptionApiValidation, 0u);
		setOption(OptionDebugOutputMute, 1u);

		_trigger = Slot<TriggerCaptureDelegate>(SlotTriggerCapture);
		_isCapturing = Slot<IsFrameCapturingDelegate>(SlotIsFrameCapturing);
		_numCaptures = Slot<GetNumCapturesDelegate>(SlotGetNumCaptures);
		_getCapture = Slot<GetCaptureDelegate>(SlotGetCapture);

		note = $"armed — RenderDoc {Version}, captures land in {captureDirectory} " +
			"(F12, or the debug channel's `capture` op)";
		return true;
	}

	/// <summary>Captures the next frame. False when not armed.</summary>
	public static bool TriggerCapture()
	{
		if (_trigger is null)
		{
			return false;
		}

		_trigger();
		return true;
	}

	/// <summary>Whether a capture is in progress right now.</summary>
	public static bool IsCapturing => _isCapturing is not null && _isCapturing() != 0u;

	/// <summary>Every capture this process has written, oldest first.</summary>
	public static List<(string Path, ulong Timestamp)> Captures()
	{
		var captures = new List<(string, ulong)>();
		if (_numCaptures is null || _getCapture is null)
		{
			return captures;
		}

		uint count = _numCaptures();
		IntPtr length = Marshal.AllocHGlobal(sizeof(uint));
		IntPtr stamp = Marshal.AllocHGlobal(sizeof(ulong));
		try
		{
			for (uint i = 0; i < count; ++i)
			{
				// Two calls, as the API is shaped: the first with no buffer answers the length.
				if (_getCapture(i, IntPtr.Zero, length, stamp) == 0u)
				{
					continue;
				}

				int bytes = Marshal.ReadInt32(length);
				IntPtr buffer = Marshal.AllocHGlobal(bytes + 1);
				try
				{
					if (_getCapture(i, buffer, length, stamp) != 0u)
					{
						string? path = Marshal.PtrToStringAnsi(buffer);
						if (!string.IsNullOrEmpty(path))
						{
							captures.Add((path, (ulong)Marshal.ReadInt64(stamp)));
						}
					}
				}
				finally
				{
					Marshal.FreeHGlobal(buffer);
				}
			}
		}
		finally
		{
			Marshal.FreeHGlobal(length);
			Marshal.FreeHGlobal(stamp);
		}

		return captures;
	}

	/// <summary>
	/// The launcher's half: when `renderdoc = true`, finds an install, registers its layer
	/// manifest for this user and sets the child's environment. False and silent when the key is
	/// off; false with a note when it is on and no install can be found.
	/// </summary>
	public static bool Arm(
		IReadOnlyDictionary<string, string> config,
		IDictionary<string, string> environment,
		out string note)
	{
		note = string.Empty;
		if (!Engine_RefiiConfig.Bool(config, "renderdoc", false))
		{
			return false;
		}

		string configured = Engine_RefiiConfig.String(config, "renderdoc_path", string.Empty);

		// A manifest already registered system-wide wins: the loader keeps the first manifest it
		// meets for a layer name and system directories precede the user's, so that install
		// attaches whatever this writes. Use it and write nothing; a second manifest for the same
		// layer only earns "non-matching layer" warnings from RenderDoc's tools.
		string? registered = configured.Length == 0 ? RegisteredInstall() : null;
		if (registered is not null)
		{
			environment[EnableVariable] = "1";
			environment[LibraryVariable] = LibraryOf(registered);
			note = $"RenderDoc armed from {registered} (already registered system-wide)";
			return true;
		}

		string? root = FindInstall(configured);
		if (root is null)
		{
			note = "renderdoc = true but no RenderDoc install was found: set renderdoc_path to the " +
				"unpacked renderdoc_1.x directory (https://renderdoc.org/builds), or put " +
				"renderdoccmd on PATH, or unpack one under ~/.local/share/refii/renderdoc/";
			return false;
		}

		string library = LibraryOf(root);
		if (OperatingSystem.IsLinux())
		{
			string? manifest = RegisterLayer(root, library, out string manifestNote);
			if (manifest is null)
			{
				note = manifestNote;
				return false;
			}

			note = $"RenderDoc armed from {root} (layer manifest {manifest})";
		}
		else
		{
			// Windows installs register their layer machine-wide; only the wake-up is ours.
			note = $"RenderDoc armed from {root}";
		}

		environment[EnableVariable] = "1";
		environment[LibraryVariable] = library;
		return true;
	}

	/// <summary>
	/// The install a manifest outside the user's own directory already names, when its library
	/// exists: the loader's search order puts those first. Linux only; Windows installs register
	/// through the registry and are found on PATH.
	/// </summary>
	private static string? RegisteredInstall()
	{
		if (!OperatingSystem.IsLinux())
		{
			return null;
		}

		var directories = new List<string>();
		string configDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") is { Length: > 0 } xdg
			? xdg
			: "/etc/xdg";
		foreach (string dir in configDirs.Split(':'))
		{
			if (dir.Length != 0)
			{
				directories.Add(Path.Combine(dir, "vulkan", "implicit_layer.d"));
			}
		}

		directories.Add("/etc/vulkan/implicit_layer.d");
		foreach (string directory in directories)
		{
			string manifest = Path.Combine(directory, "renderdoc_capture.json");
			if (!File.Exists(manifest))
			{
				continue;
			}

			try
			{
				Match match = Regex.Match(File.ReadAllText(manifest), "\"library_path\"\\s*:\\s*\"([^\"]*)\"");
				if (!match.Success)
				{
					continue;
				}

				string library = match.Groups[1].Value;
				if (!Path.IsPathRooted(library) || !File.Exists(library))
				{
					continue;
				}

				string? lib = Path.GetDirectoryName(library);
				string? root = lib is null ? null : Path.GetDirectoryName(lib);
				if (root is not null && File.Exists(LibraryOf(root)))
				{
					return root;
				}
			}
			catch (Exception)
			{
				// A manifest that cannot be read is not one the loader will use either.
			}
		}

		return null;
	}

	private static string LibraryOf(string root) =>
		OperatingSystem.IsWindows()
			? Path.Combine(root, "renderdoc.dll")
			: Path.Combine(root, "lib", "librenderdoc.so");

	/// <summary>
	/// An install root: the configured path, then the newest under the user's tools folder,
	/// then whatever `renderdoccmd` on PATH belongs to.
	/// </summary>
	private static string? FindInstall(string configured)
	{
		var candidates = new List<string>();
		if (configured.Length != 0)
		{
			candidates.Add(Expand(configured));
		}

		string tools = Path.Combine(DataHome(), "refii", "renderdoc");
		if (Directory.Exists(tools))
		{
			var unpacked = new List<string>(Directory.GetDirectories(tools));
			unpacked.Sort(string.CompareOrdinal);
			unpacked.Reverse();
			candidates.AddRange(unpacked);
		}

		string? path = Environment.GetEnvironmentVariable("PATH");
		if (path is not null)
		{
			string tool = OperatingSystem.IsWindows() ? "renderdoccmd.exe" : "renderdoccmd";
			foreach (string dir in path.Split(Path.PathSeparator))
			{
				if (dir.Length != 0 && File.Exists(Path.Combine(dir, tool)))
				{
					candidates.Add(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir);
				}
			}
		}

		foreach (string candidate in candidates)
		{
			if (File.Exists(LibraryOf(candidate)))
			{
				return candidate;
			}
		}

		return null;
	}

	private static string Expand(string path) =>
		path.StartsWith("~/", StringComparison.Ordinal)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
			: path;

	private static string DataHome() =>
		Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
			? xdg
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

	/// <summary>
	/// Writes the install's own layer manifest into the user's implicit-layer directory with the
	/// library path made absolute — what `renderdoccmd vulkanlayer --register --user` does.
	/// Rewritten only when the content differs, so a stable install touches nothing.
	/// </summary>
	private static string? RegisterLayer(string root, string library, out string note)
	{
		note = string.Empty;
		string source = Path.Combine(root, "etc", "vulkan", "implicit_layer.d", "renderdoc_capture.json");
		if (!File.Exists(source))
		{
			note = $"the RenderDoc install at {root} has no etc/vulkan/implicit_layer.d/renderdoc_capture.json";
			return null;
		}

		string manifest = Regex.Replace(
			File.ReadAllText(source),
			"\"library_path\"\\s*:\\s*\"[^\"]*\"",
			"\"library_path\": \"" + library.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");

		string directory = Path.Combine(DataHome(), "vulkan", "implicit_layer.d");
		string target = Path.Combine(directory, "renderdoc_capture.json");
		try
		{
			Directory.CreateDirectory(directory);
			if (!File.Exists(target) || File.ReadAllText(target) != manifest)
			{
				File.WriteAllText(target, manifest);
			}
		}
		catch (Exception error)
		{
			note = $"could not register the RenderDoc layer at {target}: {error.Message}";
			return null;
		}

		return target;
	}
}
