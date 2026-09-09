using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Launcher.Core;

/// <summary>
/// The resolutions a monitor actually supports, asked of the platform rather than guessed.
/// </summary>
/// <remarks>
/// GODOT CANNOT ANSWER THIS. `DisplayServer` reports a screen's CURRENT size, refresh rate and
/// usable rect; there is no "every mode this output supports" call. Being a C# app, we can ask
/// the platform ourselves — no GDExtension and no host-bridge ABI bump, just a P/Invoke on
/// Windows and the X server's own tool on Linux.
///
/// EVERY PATH MAY RETURN NOTHING, and that is a normal answer, not a failure: a Wayland session,
/// a headless run, a machine without `xrandr`. The caller keeps its curated list in that case —
/// so this can only ever make the list MORE accurate, never empty it.
/// </remarks>
public static class DisplayModes
{
	/// <summary>
	/// Every distinct resolution the output currently showing <paramref name="currentSize"/>
	/// can display, largest first. Empty when the platform will not say.
	/// </summary>
	/// <remarks>
	/// MATCHED BY CURRENT SIZE, NOT BY INDEX. Godot's screen ordering and the platform's output
	/// ordering are separate enumerations and need not agree, so keying on the index would
	/// silently return another monitor's modes on a multi-head desk. The size the caller already
	/// has from `DisplayServer.ScreenGetSize` is the reliable join.
	/// </remarks>
	public static IReadOnlyList<Vector2I> ForScreen(Vector2I currentSize)
	{
		try
		{
			List<Vector2I> modes;
			if (OperatingSystem.IsWindows())
			{
				modes = FromWindows();
			}
			else if (IsWayland())
			{
				// NOT XRANDR UNDER WAYLAND, and skipping it is a fix rather than a gap.
				//
				// `xrandr` still runs through XWayland, but it describes XWAYLAND'S virtual
				// output — commonly a single mode, the current one. That is worse than knowing
				// nothing: one mode is a non-empty answer, so the caller would offer exactly one
				// resolution and hide every other size the window can legitimately be.
				//
				// It is also the wrong question on Wayland. The compositor owns modes; a client
				// cannot enumerate or set them portably, a window may be any size it likes, and
				// fullscreen uses whatever mode the compositor is already in. So the curated
				// list, filtered to the screen, is the right behaviour here — a convenience
				// rather than a constraint.
				//
				// Per-compositor enumeration does exist (wlr-randr on wlroots, kscreen-doctor
				// on KDE, org.gnome.Mutter.DisplayConfig on GNOME). None is present on this
				// machine, so none is written: an untestable parser that silently returns the
				// wrong list is worse than an honest fallback.
				modes = new List<Vector2I>();
			}
			else
			{
				modes = FromXrandr(currentSize);
			}

			modes.Sort((a, b) => b.X == a.X ? b.Y.CompareTo(a.Y) : b.X.CompareTo(a.X));
			return modes;
		}
		catch (Exception exception)
		{
			// Never fatal: the caller has a fallback list and a settings panel that must open.
			GD.Print($"[display] could not enumerate modes ({exception.GetType().Name}); " +
				"falling back to the curated list");
			return Array.Empty<Vector2I>();
		}
	}

	/// <summary>
	/// Whether this is a Wayland session. `XDG_SESSION_TYPE` is the session's own declaration;
	/// `WAYLAND_DISPLAY` catches a client started outside a login session that still talks
	/// Wayland.
	/// </summary>
	private static bool IsWayland() =>
		string.Equals(
			System.Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
			"wayland",
			StringComparison.OrdinalIgnoreCase) ||
		!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DevMode
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string DeviceName;
		public short SpecVersion;
		public short DriverVersion;
		public short Size;
		public short DriverExtra;
		public int Fields;
		public int PositionX;
		public int PositionY;
		public int DisplayOrientation;
		public int DisplayFixedOutput;
		public short Color;
		public short Duplex;
		public short YResolution;
		public short TrueTypeOption;
		public short Collate;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string FormName;
		public short LogPixels;
		public int BitsPerPel;
		public int PelsWidth;
		public int PelsHeight;
		public int DisplayFlags;
		public int DisplayFrequency;
		public int ICMMethod;
		public int ICMIntent;
		public int MediaType;
		public int DitherType;
		public int Reserved1;
		public int Reserved2;
		public int PanningWidth;
		public int PanningHeight;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode mode);

	/// <summary>The primary display's modes, through the Win32 enumeration.</summary>
	private static List<Vector2I> FromWindows()
	{
		var seen = new HashSet<Vector2I>();
		var modes = new List<Vector2I>();
		var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };

		for (int index = 0; EnumDisplaySettingsW(null, index, ref mode); index++)
		{
			var size = new Vector2I(mode.PelsWidth, mode.PelsHeight);
			if (size.X > 0 && size.Y > 0 && seen.Add(size))
			{
				modes.Add(size);
			}
		}

		return modes;
	}

	/// <summary>
	/// The X server's mode list for the output currently at <paramref name="currentSize"/>.
	/// </summary>
	/// <remarks>
	/// `xrandr --query` rather than libXrandr: the tool is present wherever RandR is, and its
	/// output is a stable, checkable format, where the library means marshalling several structs
	/// whose layout cannot be verified without running them. Its shape is
	///
	///     DP-0 connected 2560x1440+0+0 (normal ...) 597mm x 336mm
	///        2560x1440    165.00 + 144.00   120.00*   60.00
	///        1920x1080    119.88    60.00 ...
	///
	/// so an unindented line opens an output and the indented ones under it are its modes.
	/// </remarks>
	private static List<Vector2I> FromXrandr(Vector2I currentSize)
	{
		var start = new ProcessStartInfo("xrandr")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		start.ArgumentList.Add("--query");

		using Process? process = Process.Start(start);
		if (process is null)
		{
			return new List<Vector2I>();
		}

		string output = process.StandardOutput.ReadToEnd();
		process.WaitForExit(4000);

		var modes = new List<Vector2I>();
		var seen = new HashSet<Vector2I>();
		bool inWantedOutput = false;

		foreach (string raw in output.Split('\n'))
		{
			if (raw.Length == 0)
			{
				continue;
			}

			// Unindented: an output (or the "Screen 0:" header, which has no modes under it).
			if (!char.IsWhiteSpace(raw[0]))
			{
				inWantedOutput = raw.Contains(" connected", StringComparison.Ordinal) &&
					GeometryOf(raw) == currentSize;
				continue;
			}

			if (!inWantedOutput)
			{
				continue;
			}

			string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 0 && Parse(parts[0]) is { } size && seen.Add(size))
			{
				modes.Add(size);
			}
		}

		return modes;
	}

	/// <summary>The "2560x1440+0+0" geometry on a connected-output line, or zero.</summary>
	private static Vector2I GeometryOf(string line)
	{
		foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			int plus = token.IndexOf('+');
			if (plus > 0 && Parse(token[..plus]) is { } size)
			{
				return size;
			}
		}

		return Vector2I.Zero;
	}

	/// <summary>"1920x1080" to a size, or null when it is not one.</summary>
	private static Vector2I? Parse(string text)
	{
		int cross = text.IndexOf('x');
		return cross > 0 &&
			int.TryParse(text[..cross], out int width) &&
			int.TryParse(text[(cross + 1)..], out int height) &&
			width > 0 && height > 0
			? new Vector2I(width, height)
			: null;
	}
}
