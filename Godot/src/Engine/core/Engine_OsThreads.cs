using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Refii.Core;

/// <summary>
/// This process's OS threads by id: the name the OS keeps for one, and its CPU time.
/// </summary>
/// <remarks>
/// Linux and Android keep the name in `/proc/self/task/&lt;tid&gt;/comm` and the CPU time in `stat`
/// (utime + stime, in `CLOCK_TCK` ticks). Windows keeps the name as the thread DESCRIPTION
/// (`SetThreadDescription`, which Godot and .NET both call; read back with
/// `GetThreadDescription`, Windows 10 1607 and later) and the CPU time in `GetThreadTimes`
/// (kernel + user, 100 ns units). Both clocks accrue at the scheduler tick, so a share is only
/// meaningful over many ticks — ask over a second, not over a frame.
/// </remarks>
public static class Engine_OsThreads
{
	/// <summary>The OS's name for a thread, or null when it keeps none.</summary>
	public static string? Name(int tid)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsName(tid);
		}

		try
		{
			string path = $"/proc/self/task/{tid}/comm";
			return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>The id of this process's first thread with that exact name, or -1.</summary>
	public static int FindByName(string name)
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			foreach (ProcessThread thread in process.Threads)
			{
				if (Name(thread.Id) == name)
				{
					return thread.Id;
				}
			}
		}
		catch (Exception)
		{
		}

		return -1;
	}

	/// <summary>
	/// The thread's total CPU time in seconds, or NaN when it has exited or the OS does not say.
	/// </summary>
	public static double CpuSeconds(int tid)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsCpuSeconds(tid);
		}

		// .NET reports Android as its own OS, not Linux; its /proc is the same.
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
		{
			return double.NaN;
		}

		try
		{
			string stat = System.IO.File.ReadAllText($"/proc/self/task/{tid}/stat");
			int close = stat.LastIndexOf(')');
			string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return (long.Parse(fields[11]) + long.Parse(fields[12])) / LinuxClockTicksPerSecond;
		}
		catch (Exception)
		{
			return double.NaN;
		}
	}

	/// <summary>Linux `CLOCK_TCK`; 100 on every distribution kernel.</summary>
	private const double LinuxClockTicksPerSecond = 100.0;

	/// <summary>`FILETIME` units per second.</summary>
	private const double WindowsTicksPerSecond = 10_000_000.0;

	private const uint ThreadQueryLimitedInformation = 0x0800;
	private const uint StillActive = 259;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

	[DllImport("kernel32.dll")]
	private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

	[DllImport("kernel32.dll")]
	private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit,
		out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr memory);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	private static string? WindowsName(int tid)
	{
		IntPtr handle = IntPtr.Zero;
		try
		{
			handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)tid);
			if (handle == IntPtr.Zero || GetThreadDescription(handle, out IntPtr text) < 0)
			{
				return null;
			}

			string? name = Marshal.PtrToStringUni(text);
			LocalFree(text);
			return string.IsNullOrWhiteSpace(name) ? null : name;
		}
		catch (Exception)
		{
			// EntryPointNotFoundException before Windows 10 1607.
			return null;
		}
		finally
		{
			if (handle != IntPtr.Zero)
			{
				CloseHandle(handle);
			}
		}
	}

	private static double WindowsCpuSeconds(int tid)
	{
		IntPtr handle = IntPtr.Zero;
		try
		{
			handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)tid);
			if (handle == IntPtr.Zero ||
				!GetExitCodeThread(handle, out uint code) || code != StillActive ||
				!GetThreadTimes(handle, out _, out _, out long kernel, out long user))
			{
				return double.NaN;
			}

			return (kernel + user) / WindowsTicksPerSecond;
		}
		catch (Exception)
		{
			return double.NaN;
		}
		finally
		{
			if (handle != IntPtr.Zero)
			{
				CloseHandle(handle);
			}
		}
	}
}
