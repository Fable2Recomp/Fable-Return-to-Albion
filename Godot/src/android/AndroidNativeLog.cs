using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// Makes the NativeAOT runtime's last words readable in logcat.
/// </summary>
/// <remarks>
/// An Android app's stdout and stderr go nowhere. Under Mono that hides little; under the
/// NativeAOT export (linux-bionic) it hides everything the runtime says before it dies: the
/// unhandled-exception report, FailFast messages, and native shims that print and abort (the
/// OpenSSL one wrote "No usable version of libssl was found" and the only trace was a SIGABRT).
/// At assembly start, before any game code, this points file descriptors 1 and 2 at a pipe
/// whose lines go to logcat under the tag <c>refii-stdio</c>, and reports an unhandled managed
/// exception directly (tag <c>refii</c>, fatal) since the process may die before the pipe drains.
/// Only the linux-bionic runtime is changed; Mono and the desktop are untouched.
/// </remarks>
internal static class AndroidNativeLog
{
	private const int LogFatal = 7;
	private const int LogWarn = 5;

	[DllImport("liblog.so", EntryPoint = "__android_log_write")]
	private static extern int AndroidLogWrite(int priority, string tag, string text);

	[DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
	private static extern int Pipe([Out] int[] fds);

	[DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
	private static extern int Dup2(int oldFd, int newFd);

	[DllImport("libc", EntryPoint = "read", SetLastError = true)]
	private static extern nint Read(int fd, [Out] byte[] buffer, nint count);

	[DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int Fcntl(int fd, int command, int argument);

	private const int SetStatusFlags = 4; // F_SETFL
	private const int NonBlocking = 0x800; // O_NONBLOCK on arm64 Linux

	/// <summary>The pipe's read end once stdout and stderr point at it; -1 before or without.</summary>
	private static int _readFd = -1;

	private static int _pumping;

#pragma warning disable CA2255 // The earliest point managed code runs; it must precede the game's first call.
	[ModuleInitializer]
#pragma warning restore CA2255
	internal static void Install()
	{
		if (!Engine_Platform.IsLinuxBionic)
			return;

		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			try
			{
				AndroidLogWrite(LogFatal, "refii", "Unhandled managed exception: " + args.ExceptionObject);
			}
			catch
			{
			}
		};

		try
		{
			RedirectStdio();
		}
		catch (Exception error)
		{
			AndroidLogWrite(LogWarn, "refii", "stdout/stderr stay unlogged: " + error.Message);
		}
	}

	/// <remarks>
	/// NO THREAD STARTS HERE. A module initializer runs inside the runtime's own start-up, and
	/// `Thread.Start` waits for a thread that cannot run managed code until that start-up ends:
	/// the phone hung in `godotsharp_game_main_init` yielding for ever. The pipe is made
	/// non-blocking, so output written before <see cref="StartPump"/> fills its buffer and is
	/// then dropped instead of stalling the writer.
	/// </remarks>
	private static void RedirectStdio()
	{
		int[] fds = new int[2];
		if (Pipe(fds) != 0)
			throw new InvalidOperationException("pipe failed: errno " + Marshal.GetLastPInvokeError());
		int read = fds[0], write = fds[1];
		Fcntl(write, SetStatusFlags, NonBlocking);
		if (Dup2(write, 1) < 0 || Dup2(write, 2) < 0)
			throw new InvalidOperationException("dup2 failed: errno " + Marshal.GetLastPInvokeError());
		_readFd = read;
	}

	/// <summary>Starts draining the redirected output into logcat; the first entry point to run calls it.</summary>
	internal static void StartPump()
	{
		if (_readFd < 0 || Interlocked.Exchange(ref _pumping, 1) != 0)
			return;
		int read = _readFd;
		var pump = new Thread(() => Pump(read)) { IsBackground = true, Name = "refii-stdio" };
		pump.Start();
	}

	private static void Pump(int fd)
	{
		byte[] buffer = new byte[4096];
		var line = new StringBuilder();
		while (true)
		{
			nint count = Read(fd, buffer, buffer.Length);
			if (count <= 0)
			{
				if (count < 0 && Marshal.GetLastPInvokeError() == 4) // EINTR
					continue;
				return;
			}

			line.Append(Encoding.UTF8.GetString(buffer, 0, (int)count));
			string text = line.ToString();
			int end = text.LastIndexOf('\n');
			if (end < 0 && line.Length < 4000)
				continue;
			string complete = end < 0 ? text : text[..end];
			line.Clear();
			if (end >= 0)
				line.Append(text, end + 1, text.Length - end - 1);
			foreach (string row in complete.Split('\n'))
				AndroidLogWrite(LogWarn, "refii-stdio", row);
		}
	}
}
