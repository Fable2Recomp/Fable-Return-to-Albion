using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Refii.Launcher.Recompile;

/// <summary>
/// The ReXGlue code generator, as the library <c>refii_recompiler</c> in bin/.
/// </summary>
/// <remarks>
/// IN THIS PROCESS, NOT A CHILD. The generator was an executable under bin/recompile/bin
/// that imported the runtime from bin/ through a PATH the launcher set, and carried its own
/// copy of the MSVC runtime so it could start at all. On a machine whose DLL search policy
/// did not honour that PATH it died at process start, before any output, and the launcher
/// could only report a generator that would not run. As a library beside the runtime it
/// imports, the loader resolves it from its own directory the way every other refii_*
/// library is already resolved, and there is one copy of the runtime and one of the CRT.
///
/// Resolved through <see cref="Refii.Core.Engine_NativeLibraries"/> like the host bridge, so
/// the dev tree and an export find it the same way.
///
/// PAUSE AND STOP RIDE ON THE PROGRESS CALLBACK. The SDK's generator has no cancellation
/// point of its own, and the process that used to be killed is now this one, so the
/// callback is where the <see cref="BuildControl"/> is consulted: it blocks there while the
/// build is paused, and returns a stop request that the library raises out of the generator
/// at that event. Both take effect at the generator's phase boundaries — register, scan,
/// discover, gap-fill, merge, validate, write — which is as fine as the SDK reports.
/// </remarks>
internal static class Recompiler
{
	private const string Library = "refii_recompiler";

	// Mirrors refii_recompiler/recompiler.h.
	private const int AbiVersion = 1;

	private const int EventBinary = 1;
	private const int EventModuleStarted = 2;
	private const int EventPhase = 3;
	private const int EventModuleFinished = 4;
	private const int EventProjectPhase = 5;
	private const int EventProjectPhaseFinished = 6;
	private const int EventLog = 7;

	private const int ResultOk = 0;
	private const int ResultStopped = 2;

	// Returns 0 to continue, nonzero to stop; see refii_recompiler_progress_fn.
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int ProgressFn(IntPtr user, int eventKind, IntPtr detail, int index, int total);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int refii_recompiler_v1_abi_version();

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr refii_recompiler_v1_sdk_version();

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int refii_recompiler_v1_codegen(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string manifestPath,
		ProgressFn progress, IntPtr user, byte[] error, nuint errorCapacity);

	static Recompiler() => Refii.Core.Engine_NativeLibraries.Install();

	/// <summary>The SDK version the generator was built from, for the build record.</summary>
	public static string SdkVersion()
	{
		try
		{
			return Marshal.PtrToStringUTF8(refii_recompiler_v1_sdk_version()) ?? string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// Generates the sources the manifest describes. Blocks the calling thread for the
	/// length of the run; <paramref name="status"/> and <paramref name="log"/> are invoked
	/// on that thread.
	/// </summary>
	/// <param name="manifestPath">The effective manifest, with absolute paths.</param>
	/// <param name="control">Consulted at every event: holds the thread while paused, and
	/// turns a stop into the generator unwinding.</param>
	/// <param name="status">One line describing where the generator is, for the UI.</param>
	/// <param name="log">Every line the generator logged, for the launcher log.</param>
	public static GameCodeBuild.Result Generate(
		string manifestPath, BuildControl control, Action<string> status, Action<string> log)
	{
		int abi;
		try
		{
			abi = refii_recompiler_v1_abi_version();
		}
		catch (DllNotFoundException exception)
		{
			return new GameCodeBuild.Result(false,
				"The code generator library (" +
				Refii.Core.Engine_NativeLibraries.FileNameFor(Library) +
				") could not be loaded from " + GameCodeBuild.BinDirectory + ": " +
				exception.Message);
		}
		catch (EntryPointNotFoundException)
		{
			abi = 0;
		}

		if (abi != AbiVersion)
		{
			return new GameCodeBuild.Result(false,
				"The code generator library in " + GameCodeBuild.BinDirectory +
				" is a different version from this launcher (ABI " + abi + ", expected " +
				AbiVersion + "). Copy the whole extracted folder, not just part of it.");
		}

		string module = string.Empty;

		// A local so it is reachable for the whole call: the delegate's native thunk lives
		// as long as the delegate does, and the generator calls back into it until it returns.
		ProgressFn progress = (_, eventKind, detailPointer, index, total) =>
		{
			// NEVER THROW FROM HERE: this frame sits above native ones, and an exception
			// crossing them is a process abort. Everything below reports through its return.
			control.WaitWhilePaused();
			if (control.IsStopping)
			{
				return 1;
			}

			string detail = detailPointer == IntPtr.Zero
				? string.Empty
				: Marshal.PtrToStringUTF8(detailPointer) ?? string.Empty;

			switch (eventKind)
			{
				// THE STATUS IS SHORT: it is drawn into a 280-unit button beside the pause and
				// stop controls, so it carries the phase and nothing else. The module name
				// and the binary's identity go to the log, where there is room for them.
				case EventBinary:
					log("binary: " + detail);
					break;
				case EventModuleStarted:
					module = detail;
					log("module " + (index + 1) + "/" + total + ": " + detail);
					break;
				case EventPhase:
					status(detail);
					break;
				case EventModuleFinished:
					log(module + " finished in " + index + " ms");
					break;
				case EventProjectPhase:
					module = string.Empty;
					status(detail);
					break;
				case EventProjectPhaseFinished:
					break;
				case EventLog:
					log(detail);
					break;
			}

			return 0;
		};

		var error = new byte[4096];
		int code;
		try
		{
			code = refii_recompiler_v1_codegen(
				manifestPath, progress, IntPtr.Zero, error, (nuint)error.Length);
		}
		catch (Exception exception)
		{
			return new GameCodeBuild.Result(false, exception.Message);
		}
		finally
		{
			GC.KeepAlive(progress);
		}

		if (code == ResultOk)
		{
			return new GameCodeBuild.Result(true, string.Empty);
		}

		if (code == ResultStopped)
		{
			return new GameCodeBuild.Result(false, "stopped", Stopped: true);
		}

		int length = Array.IndexOf(error, (byte)0);
		string message = Encoding.UTF8.GetString(error, 0, length < 0 ? error.Length : length);
		return new GameCodeBuild.Result(false,
			message.Length == 0 ? "the generator failed without a message" : message);
	}
}
