using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

namespace Refii.Launcher.Native;

internal static class LauncherOpsNative
{
	private const string Library = "refii_launcher_ops";

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_initialize(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string launcherDirectory);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr refii_ops_progress_create();

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_progress_destroy(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_is_extracting(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_is_complete(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_has_error(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_operation_label(IntPtr handle, byte[]? buffer, int capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_status_message(IntPtr handle, byte[]? buffer, int capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_status_history(IntPtr handle, byte[]? buffer, int capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_error_message(IntPtr handle, byte[]? buffer, int capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_extract_general_game_banks(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_extract_game_shaders(IntPtr handle);
}

public sealed class LauncherOps : IDisposable
{
	private static bool _initialized;
	private static bool _available;

	private IntPtr _handle;

	private LauncherOps(IntPtr handle) => _handle = handle;

	public static bool Available => _available;

	private static IEnumerable<string> SearchDirectories()
	{
		string hostDirectory =
			Path.GetDirectoryName(OS.GetExecutablePath()) ?? AppContext.BaseDirectory;

		yield return hostDirectory;
		yield return Path.Combine(hostDirectory, "bin");

		if (OS.HasFeature("template"))
		{
			yield break;
		}

		string projectDir = ProjectSettings.GlobalizePath("res://");
		yield return Path.Combine(projectDir, "build", "bin");
		yield return Path.Combine(projectDir, "bin");

		string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));
		foreach (string configuration in new[] { "RelWithDebInfo", "Release", "Debug" })
		{
			yield return Path.Combine(
				repoRoot, "out", "build", "win-amd64-release", configuration, "lib");
		}
	}

	public static bool Initialize(string launcherDirectory)
	{
		if (_initialized)
		{
			return _available;
		}

		_initialized = true;

		// One resolver for the whole assembly — see Engine_NativeLibraries. This contributes the
		// launcher's own directories to it rather than installing a second.
		foreach (string directory in SearchDirectories())
		{
			Refii.Core.Engine_NativeLibraries.AddSearchPath(directory);
		}

		Refii.Core.Engine_NativeLibraries.Install();

		try
		{
			LauncherOpsNative.refii_ops_initialize(launcherDirectory);
			_available = true;
		}
		catch (DllNotFoundException)
		{
			_available = false;
		}
		catch (EntryPointNotFoundException)
		{
			_available = false;
		}

		return _available;
	}

	public static LauncherOps? Create()
	{
		if (!_available)
		{
			return null;
		}

		IntPtr handle = LauncherOpsNative.refii_ops_progress_create();
		return handle == IntPtr.Zero ? null : new LauncherOps(handle);
	}

	private delegate int StringReader(IntPtr handle, byte[]? buffer, int capacity);

	private string Read(StringReader reader)
	{
		if (_handle == IntPtr.Zero)
		{
			return string.Empty;
		}

		int needed = reader(_handle, null, 0);
		if (needed <= 0)
		{
			return string.Empty;
		}

		var buffer = new byte[needed + 1];
		reader(_handle, buffer, buffer.Length);
		return Encoding.UTF8.GetString(buffer, 0, needed);
	}

	public bool IsExtracting => _handle != IntPtr.Zero &&
								LauncherOpsNative.refii_ops_is_extracting(_handle) != 0;

	public bool IsComplete => _handle != IntPtr.Zero &&
							  LauncherOpsNative.refii_ops_is_complete(_handle) != 0;

	public bool HasError => _handle != IntPtr.Zero &&
							LauncherOpsNative.refii_ops_has_error(_handle) != 0;

	public string OperationLabel => Read(LauncherOpsNative.refii_ops_operation_label);
	public string StatusMessage => Read(LauncherOpsNative.refii_ops_status_message);
	public string StatusHistory => Read(LauncherOpsNative.refii_ops_status_history);
	public string ErrorMessage => Read(LauncherOpsNative.refii_ops_error_message);

	public void ExtractGameBanks() =>
		LauncherOpsNative.refii_ops_extract_general_game_banks(_handle);
	public void CompileShaders() => LauncherOpsNative.refii_ops_extract_game_shaders(_handle);

	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			LauncherOpsNative.refii_ops_progress_destroy(_handle);
			_handle = IntPtr.Zero;
		}
	}
}
