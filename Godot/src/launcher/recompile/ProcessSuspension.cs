using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Refii.Launcher.Recompile;

/// <summary>
/// Freezes and thaws a process and everything it spawned.
/// </summary>
/// <remarks>
/// THE TREE, NOT THE PROCESS. <c>zig c++</c> is a driver: it starts a child that does the
/// compiling, so suspending the one we started would stop a process that is waiting and leave
/// the one burning the core running. Children are found by parent id at the moment of the
/// call, suspended after their parent (which can then spawn nothing new) and resumed before it.
///
/// Linux: SIGSTOP and SIGCONT, which the kernel applies regardless of what the process is
/// doing. Windows: NtSuspendProcess and NtResumeProcess, the calls the debugger and Process
/// Explorer use; undocumented in name only, unchanged since Windows 2000.
///
/// A process that exits between the walk and the signal is simply missed; a pid reused in
/// that window would be signalled wrongly, which is the same hazard every tree operation on
/// pids carries and is why this is only ever applied to compilers we started ourselves.
/// </remarks>
internal static class ProcessSuspension
{
	public static void Suspend(Process process)
	{
		foreach (int pid in Tree(process))
		{
			Signal(pid, suspend: true);
		}
	}

	public static void Resume(Process process)
	{
		List<int> tree = Tree(process);
		tree.Reverse();
		foreach (int pid in tree)
		{
			Signal(pid, suspend: false);
		}
	}

	/// <summary>The process followed by its descendants, parents before children.</summary>
	private static List<int> Tree(Process process)
	{
		var result = new List<int>();
		int root;
		try
		{
			if (process.HasExited)
			{
				return result;
			}
			root = process.Id;
		}
		catch (Exception)
		{
			return result;
		}

		Dictionary<int, List<int>> children = ChildrenByParent();
		var pending = new Stack<int>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			int pid = pending.Pop();
			if (result.Contains(pid))
			{
				continue;
			}
			result.Add(pid);
			if (children.TryGetValue(pid, out List<int>? kids))
			{
				foreach (int kid in kids)
				{
					pending.Push(kid);
				}
			}
		}

		return result;
	}

	private static Dictionary<int, List<int>> ChildrenByParent()
	{
		var map = new Dictionary<int, List<int>>();
		void Add(int parent, int child)
		{
			if (!map.TryGetValue(parent, out List<int>? list))
			{
				list = new List<int>();
				map[parent] = list;
			}
			list.Add(child);
		}

		if (OperatingSystem.IsWindows())
		{
			IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
			if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
			{
				return map;
			}
			try
			{
				var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
				if (Process32FirstW(snapshot, ref entry))
				{
					do
					{
						Add((int)entry.th32ParentProcessID, (int)entry.th32ProcessID);
					}
					while (Process32NextW(snapshot, ref entry));
				}
			}
			finally
			{
				CloseHandle(snapshot);
			}
			return map;
		}

		if (OperatingSystem.IsLinux())
		{
			// /proc/<pid>/stat: "pid (comm) state ppid ...". The comm can hold spaces and
			// parentheses, so the fields are read from after its closing parenthesis.
			foreach (string directory in SafeEnumerate("/proc"))
			{
				if (!int.TryParse(Path.GetFileName(directory), out int pid))
				{
					continue;
				}
				try
				{
					string stat = File.ReadAllText(Path.Combine(directory, "stat"));
					int close = stat.LastIndexOf(')');
					if (close < 0)
					{
						continue;
					}
					string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (fields.Length > 1 && int.TryParse(fields[1], out int parent))
					{
						Add(parent, pid);
					}
				}
				catch (Exception)
				{
				}
			}
		}

		return map;
	}

	private static IEnumerable<string> SafeEnumerate(string directory)
	{
		try
		{
			return Directory.EnumerateDirectories(directory);
		}
		catch (Exception)
		{
			return Array.Empty<string>();
		}
	}

	private static void Signal(int pid, bool suspend)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
				if (handle == IntPtr.Zero)
				{
					return;
				}
				try
				{
					if (suspend)
					{
						NtSuspendProcess(handle);
					}
					else
					{
						NtResumeProcess(handle);
					}
				}
				finally
				{
					CloseHandle(handle);
				}
			}
			else if (OperatingSystem.IsLinux())
			{
				kill(pid, suspend ? SIGSTOP : SIGCONT);
			}
		}
		catch (Exception)
		{
		}
	}

	private const int SIGCONT = 18;
	private const int SIGSTOP = 19;

	[DllImport("libc", SetLastError = true)]
	private static extern int kill(int pid, int signal);

	private const uint PROCESS_SUSPEND_RESUME = 0x0800;
	private const uint TH32CS_SNAPPROCESS = 0x2;

	[DllImport("ntdll.dll")]
	private static extern int NtSuspendProcess(IntPtr handle);

	[DllImport("ntdll.dll")]
	private static extern int NtResumeProcess(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct PROCESSENTRY32W
	{
		public uint dwSize;
		public uint cntUsage;
		public uint th32ProcessID;
		public IntPtr th32DefaultHeapID;
		public uint th32ModuleID;
		public uint cntThreads;
		public uint th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}
}
