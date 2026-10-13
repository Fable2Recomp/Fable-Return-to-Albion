using System;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Launcher.Recompile;

namespace Refii.Launcher.Shaders;

/// <summary>
/// The shader bank pipeline, as the library <c>refii_shader_sbk</c> in bin/.
/// </summary>
/// <remarks>
/// IN THIS PROCESS, NOT A CHILD, and not through a native operations library either: the
/// "Compile Shaders" action used to reach the pipeline through <c>refii_launcher_ops</c>, a
/// library this tree no longer builds, so the button logged "unavailable" and did nothing.
/// The pipeline itself is a library already (src/shader); this is its C ABI
/// (<c>shader/refii_shader_build.h</c>), resolved through
/// <see cref="Refii.Core.Engine_NativeLibraries"/> like the host bridge and the code
/// generator, so the dev tree and an export find it the same way.
///
/// PAUSE AND STOP RIDE ON THE PROGRESS CALLBACK, exactly as the code generator's do: the
/// pipeline runs on the calling thread, the callback blocks there while paused, and a stop
/// request is raised out of the pipeline at the next event.
/// </remarks>
internal static class ShaderPipeline
{
	// Underscored, as the resolver requires: it serves `refii_*` names only.
	private const string Library = "refii_shader_sbk";

	// Mirrors shader/refii_shader_build.h.
	private const int AbiVersion = 1;
	private const int EventProgress = 1;
	private const int EventLog = 2;
	private const int ResultOk = 0;
	private const int ResultStopped = 2;

	// Returns 0 to continue, nonzero to stop; see refii_shader_build_progress_fn.
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int ProgressFn(IntPtr user, int eventKind, IntPtr detail, int index, int total);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int refii_shader_build_v1_abi_version();

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int refii_shader_build_v1_run(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string bankDirectory,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string outputDirectory,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string targets,
		ProgressFn progress, IntPtr user, byte[] error, nuint errorCapacity);

	static ShaderPipeline() => Refii.Core.Engine_NativeLibraries.Install();

	/// <summary>One step of the pipeline, as the launcher draws it.</summary>
	/// <param name="Status">One short line: the phase and its count.</param>
	/// <param name="Fraction">0..1 of the whole run.</param>
	public readonly record struct Step(string Status, float Fraction);

	/// <summary>
	/// Extracts, translates and compiles the banks in <paramref name="bankDirectory"/> into
	/// <paramref name="outputDirectory"/>. Blocks the calling thread for the length of the run;
	/// <paramref name="step"/> and <paramref name="log"/> are invoked on that thread.
	/// </summary>
	/// <param name="targets">"auto" for the host's backend set, or a comma list.</param>
	/// <param name="control">Consulted at every event: holds the thread while paused, and
	/// turns a stop into the pipeline unwinding.</param>
	public static GameCodeBuild.Result Build(
		string bankDirectory, string outputDirectory, string targets, BuildControl control,
		Action<Step> step, Action<string> log)
	{
		int abi;
		try
		{
			abi = refii_shader_build_v1_abi_version();
		}
		catch (DllNotFoundException exception)
		{
			return new GameCodeBuild.Result(false,
				"The shader pipeline library (" +
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
				"The shader pipeline library in " + GameCodeBuild.BinDirectory +
				" is a different version from this launcher (ABI " + abi + ", expected " +
				AbiVersion + "). Copy the whole extracted folder, not just part of it.");
		}

		// A local so it is reachable for the whole call: the delegate's native thunk lives as
		// long as the delegate does, and the pipeline calls back into it until it returns.
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
				case EventProgress:
					step(Describe(detail, index, total));
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
			code = refii_shader_build_v1_run(
				bankDirectory, outputDirectory, targets, progress, IntPtr.Zero, error,
				(nuint)error.Length);
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
			message.Length == 0 ? "the shader pipeline failed without a message" : message);
	}

	/// <summary>
	/// The pipeline's phase tags as the launcher's words. Extraction is two banks and a blob
	/// pass; translation is the whole wait, so it owns the bar.
	/// </summary>
	private static Step Describe(string phase, int completed, int total)
	{
		switch (phase)
		{
			case "extracting_bank":
			case "bank_ready":
				return new Step("Reading shader banks " + completed + "/" + total + "...", 0.01f);
			case "extracting_blobs":
				return new Step("Extracting shader microcode...", 0.03f);
			case "compiling_shader":
			{
				float share = total > 0 ? (float)completed / total : 0.0f;
				return new Step("Translating shaders " + completed + "/" + total + "...",
					0.05f + 0.9f * share);
			}
			case "compile_complete":
				return new Step("Writing the shader report...", 0.96f);
			case "verifying":
				return new Step("Checking shader coverage...", 0.98f);
			default:
				return new Step(phase, total > 0 ? (float)completed / total : 0.0f);
		}
	}
}
