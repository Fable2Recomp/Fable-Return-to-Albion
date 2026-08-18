using System;
using System.IO;

namespace Refii.Launcher.Recompile;

/// <summary>
/// The bundled Zig, which is the entire toolchain used to build the game code.
/// </summary>
/// <remarks>
/// ONE TOOL, AND WE SHIP IT. This previously resolved four things — a C compiler, a C++
/// compiler, ninja and cmake — of which only the last two could be bundled, so the player
/// had to install clang or Visual Studio themselves. A C++ toolchain is not redistributable
/// piecemeal: it needs headers, CRT objects and a linker, and on Windows those are
/// Microsoft's.
///
/// Zig is redistributable as a whole and carries all of it — clang, lld, musl and glibc
/// stubs for Linux, MinGW-w64 for Windows — so <c>zig c++</c> compiles and links on a
/// machine with nothing installed. See cmake/refii_fetch_zig.cmake for how it is pinned.
///
/// There is no PATH fallback and no discovery. A build that quietly used some other
/// compiler would produce a module built against unknown headers with an unknown ABI, on
/// the one boundary we can least afford to leave to the machine. If the bundle is absent
/// the install says so.
///
///   recompile/zig/zig      (zig.exe on Windows), with its lib/ beside it
///
/// NATIVE TARGET, deliberately: no <c>-target</c> is passed. The module is compiled on the
/// machine it will run on, and it has to link against the libraries this install shipped,
/// so zig detecting the host's own glibc is exactly right — naming a triple would pin a
/// libc version the shipped libraries were not built against.
///
/// On Windows that native target is the GNU/MinGW ABI, which is the half of Zig that needs
/// nothing preinstalled; the MSVC ABI would send us back to requiring Visual Studio. That
/// only holds while the payload imports no C++ symbols from our own libraries, which is
/// what src/vfs/include/core/payload_bridge.h exists to finish.
/// </remarks>
public readonly record struct Toolchain(string Zig, string Root)
{
	/// <summary>Where packaging stages the toolchain.</summary>
	public static string BundleDirectory =>
		Path.Combine(GameCodeBuild.RecompileDirectory, "zig");

	private static string Executable =>
		OperatingSystem.IsWindows() ? "zig.exe" : "zig";

	/// <summary>
	/// Resolves the bundled Zig, or explains what is missing.
	/// </summary>
	public static bool TryResolve(out Toolchain toolchain, out string error)
	{
		toolchain = default;

		string root = BundleDirectory;
		string binary = Path.Combine(root, Executable);

		if (!File.Exists(binary))
		{
			error =
				"The bundled toolchain is missing from this install (" + binary + ").\n\n" +
				"Fable 2's game code is compiled on this PC from your own copy of the game, " +
				"because it cannot be distributed ready-built. The compiler that does it " +
				"ships beside the app under bin/recompile/zig — if you copied the " +
				"executable on its own, copy the whole extracted folder instead.";
			return false;
		}

		// zig finds its own lib/ relative to the binary, and that directory is the libc,
		// libc++ and CRT sources rather than an optional extra: without it the compiler
		// starts and then fails on the first #include.
		if (!Directory.Exists(Path.Combine(root, "lib")))
		{
			error =
				"The bundled toolchain at " + root + " has no lib/ directory, so it cannot " +
				"compile anything. The install is incomplete — re-extract the download.";
			return false;
		}

		toolchain = new Toolchain(binary, root);
		error = string.Empty;
		return true;
	}

	/// <summary>The bundled toolchain's version, for the build record.</summary>
	public static string Version(Toolchain toolchain)
	{
		try
		{
			using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = toolchain.Zig,
				ArgumentList = { "version" },
				RedirectStandardOutput = true,
				UseShellExecute = false,

				// A console window for `zig version` is still a console window: this runs
				// while the build record is written, so on Windows it flashes one up in the
				// middle of an otherwise quiet phase.
				CreateNoWindow = true,
			});

			return process?.StandardOutput.ReadToEnd().Trim() ?? string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}
}
