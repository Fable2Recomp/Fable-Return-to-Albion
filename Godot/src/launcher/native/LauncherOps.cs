using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

namespace Refii.Launcher.Native;

public enum LuaVerifyStatus
{
	Ok = 0,
	Modified = 1,
	Missing = 2,
	Extra = 3,
	BaselineMissing = 4,
	BaselineCorrupt = 5,
}

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
	internal static extern void refii_ops_rebuild_lua_workspace(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_extract_general_game_banks(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_extract_game_shaders(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_verify_lua_workspace(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_reset_lua_workspace(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void refii_ops_reset_lua_workspace_file(
		IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string relativePath);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_verify_lua_workspace_sync(
		IntPtr handle, byte[]? buffer, int capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_verify_entry_count(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_verify_entry_status(IntPtr handle, int index);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_verify_entry_path(
		IntPtr handle, int index, byte[]? buffer, int capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_verify_baseline_missing(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_verify_baseline_corrupt(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int refii_ops_describe_verify_status(
		int status, byte[]? buffer, int capacity);
}

public sealed class LuaVerifyEntry
{
	public string RelativePath = string.Empty;
	public LuaVerifyStatus Status;
	public string StatusText = string.Empty;
}

public sealed class LauncherOps : IDisposable
{
	private static bool _initialized;
	private static bool _available;

	private IntPtr _handle;

	private LauncherOps(IntPtr handle) => _handle = handle;

	public static bool Available => _available;

	private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
	{
		if (name != "refii_launcher_ops")
		{
			return IntPtr.Zero;
		}

		foreach (string directory in SearchDirectories())
		{
			string candidate = Path.Combine(directory, "refii_launcher_ops.dll");
			if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
			{
				return handle;
			}
		}

		return IntPtr.Zero;
	}

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
		try
		{
			NativeLibrary.SetDllImportResolver(
				typeof(LauncherOps).Assembly, Resolve);
		}
		catch (InvalidOperationException)
		{
		}

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

	public void RebuildLuaWorkspace() => LauncherOpsNative.refii_ops_rebuild_lua_workspace(_handle);
	public void ExtractGameBanks() =>
		LauncherOpsNative.refii_ops_extract_general_game_banks(_handle);
	public void CompileShaders() => LauncherOpsNative.refii_ops_extract_game_shaders(_handle);
	public void VerifyLuaWorkspace() => LauncherOpsNative.refii_ops_verify_lua_workspace(_handle);
	public void ResetLuaWorkspace() => LauncherOpsNative.refii_ops_reset_lua_workspace(_handle);

	public void ResetLuaWorkspaceFile(string relativePath) =>
		LauncherOpsNative.refii_ops_reset_lua_workspace_file(_handle, relativePath);

	public bool VerifyLuaWorkspaceSync(out string error)
	{
		if (_handle == IntPtr.Zero)
		{
			error = "launcher operations unavailable";
			return false;
		}

		var buffer = new byte[4096];
		bool ok =
			LauncherOpsNative.refii_ops_verify_lua_workspace_sync(_handle, buffer, buffer.Length) != 0;
		if (ok)
		{
			error = string.Empty;
			return true;
		}

		int length = Array.IndexOf(buffer, (byte)0);
		error = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
		return false;
	}

	public int BaselineMissing => _handle == IntPtr.Zero
		? 0
		: LauncherOpsNative.refii_ops_verify_baseline_missing(_handle);

	public int BaselineCorrupt => _handle == IntPtr.Zero
		? 0
		: LauncherOpsNative.refii_ops_verify_baseline_corrupt(_handle);

	public LuaVerifyEntry[] GetVerifyEntries()
	{
		if (_handle == IntPtr.Zero)
		{
			return Array.Empty<LuaVerifyEntry>();
		}

		int count = LauncherOpsNative.refii_ops_verify_entry_count(_handle);
		var entries = new LuaVerifyEntry[count];
		for (int index = 0; index < count; index++)
		{
			int captured = index;
			int status = LauncherOpsNative.refii_ops_verify_entry_status(_handle, captured);
			entries[index] = new LuaVerifyEntry
			{
				RelativePath = Read(
					(handle, buffer, capacity) =>
						LauncherOpsNative.refii_ops_verify_entry_path(
							handle, captured, buffer, capacity)),
				Status = (LuaVerifyStatus)status,
				StatusText = DescribeStatus(status),
			};
		}

		return entries;
	}

	private static string DescribeStatus(int status)
	{
		int needed = LauncherOpsNative.refii_ops_describe_verify_status(status, null, 0);
		if (needed <= 0)
		{
			return string.Empty;
		}

		var buffer = new byte[needed + 1];
		LauncherOpsNative.refii_ops_describe_verify_status(status, buffer, buffer.Length);
		return Encoding.UTF8.GetString(buffer, 0, needed);
	}

	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			LauncherOpsNative.refii_ops_progress_destroy(_handle);
			_handle = IntPtr.Zero;
		}
	}
}
