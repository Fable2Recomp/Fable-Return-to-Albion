using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Every thread: the HOST process's OS threads with their CPU share since the last refresh
/// (names from `/proc/self/task/*/comm` where the OS keeps them), and the GUEST's kernel threads
/// — pick one for its guest backtrace, innermost first, the same read `refii_threads` makes.
/// </summary>
public partial class GameDevTabThreads : GameDevTab
{
	public override string Title => "Threads";

	public override double RefreshSeconds => 1.0;

	private readonly GameDevTable _host =
		new(new[] { "thread", "tid", "cpu %", "cpu s", "state" }, sortColumn: 2);

	private readonly GameDevTable _guest =
		new(new[] { "thread", "tid", "role", "running", "priority", "hw thread", "lr" }, sortColumn: -1);

	private readonly Label _backtrace = Readout();
	private readonly Label _summary = Readout();
	private readonly Dictionary<int, double> _lastCpu = new();
	private readonly Stopwatch _wall = Stopwatch.StartNew();
	private double _lastWall;
	private uint _pickedGuest;

	public GameDevTabThreads()
	{
		AddChild(_summary);
		var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		AddChild(split);

		var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		left.AddChild(Caption("Host threads (OS) — CPU share of one core since the last refresh"));
		left.AddChild(_host);
		split.AddChild(left);

		var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		right.AddChild(Caption("Guest threads (kernel) — pick one for its backtrace"));
		right.AddChild(_guest);
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 160) };
		scroll.AddChild(_backtrace);
		right.AddChild(scroll);
		split.AddChild(right);

		_guest.Picked += row =>
		{
			if (uint.TryParse(row[1], out uint tid))
			{
				_pickedGuest = tid;
				ShowBacktrace();
			}
		};
	}

	private static Label Caption(string text) =>
		new() { Text = text, Modulate = new Color(0.7f, 0.85f, 1f) };

	public override void Refresh()
	{
		double now = _wall.Elapsed.TotalSeconds;
		double window = System.Math.Max(now - _lastWall, 1e-3);
		_lastWall = now;

		var hostRows = new List<string[]>();
		double busy = 0.0;
		using (Process process = Process.GetCurrentProcess())
		{
			foreach (ProcessThread thread in process.Threads)
			{
				double cpu;
				string state;
				try
				{
					cpu = thread.TotalProcessorTime.TotalSeconds;
					state = thread.ThreadState.ToString();
				}
				catch (System.Exception)
				{
					continue;
				}

				double share = _lastCpu.TryGetValue(thread.Id, out double before)
					? System.Math.Max(0.0, (cpu - before) / window * 100.0)
					: 0.0;
				_lastCpu[thread.Id] = cpu;
				busy += share;
				hostRows.Add(new[]
				{
					ThreadName(thread.Id), thread.Id.ToString(), $"{share:0.0}", $"{cpu:0.0}", state,
				});
			}

			_summary.Text = $"host process: {hostRows.Count} threads, {busy:0} % of one core in total, " +
				$"{System.Environment.ProcessorCount} logical cores   ·   working set " +
				$"{process.WorkingSet64 / 1048576.0:0} MB";
		}

		_host.Show(hostRows);

		var guestRows = new List<string[]>();
		foreach (HostBridge.GuestThreadInfo info in HostBridge.GetGuestThreads())
		{
			guestRows.Add(new[]
			{
				info.Name.Length != 0 ? info.Name : "(unnamed)",
				info.Tid.ToString(),
				info.IsMainThread ? "main" : info.IsGuestThread ? "guest" : "host",
				info.Running ? "yes" : "no",
				info.Priority.ToString(),
				info.Cpu.ToString(),
				$"{info.Lr:X8}",
			});
		}

		_guest.Show(guestRows);
		if (_pickedGuest != 0)
		{
			ShowBacktrace();
		}
	}

	private void ShowBacktrace()
	{
		var text = new System.Text.StringBuilder($"guest backtrace of {_pickedGuest}, innermost first:\n");
		foreach (uint frame in HostBridge.GetGuestBacktrace(_pickedGuest))
		{
			text.Append($"  {frame:X8}\n");
		}

		_backtrace.Text = text.ToString();
	}

	/// <summary>
	/// The OS's name for a thread, or its id. Linux keeps it in `comm`; Windows keeps it as the
	/// thread DESCRIPTION (`SetThreadDescription`, which Godot and .NET both call), read back with
	/// `GetThreadDescription` — Windows 10 1607 and later.
	/// </summary>
	private static string ThreadName(int tid)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsThreadName(tid) ?? $"thread {tid}";
		}

		try
		{
			string path = $"/proc/self/task/{tid}/comm";
			if (System.IO.File.Exists(path))
			{
				return System.IO.File.ReadAllText(path).Trim();
			}
		}
		catch (System.Exception)
		{
		}

		return $"thread {tid}";
	}

	private const uint ThreadQueryLimitedInformation = 0x0800;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

	[DllImport("kernel32.dll")]
	private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr memory);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	/// <summary>The thread's description, or null when it has none or the OS predates the call.</summary>
	private static string? WindowsThreadName(int tid)
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
}
