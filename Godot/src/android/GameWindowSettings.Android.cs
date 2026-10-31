using System.Collections.Generic;
using System.Runtime.InteropServices;
using Refii.Core;

namespace Refii.Game;

/// <summary>The window settings only the Android host applies.</summary>
/// <remarks>Auto layout, as the runtime already gives a struct holding a reference: its fields are
/// declared in two files, which a sequential layout cannot order (CS0282), and it never crosses
/// into native code.</remarks>
[StructLayout(LayoutKind.Auto)]
public readonly partial struct GameWindowSettings
{
	/// <summary>`hdr_2d`: the world's colour buffer in half floats (Godot's default); off it is
	/// 10-bit, half the bandwidth. Applied by the Android host only.</summary>
	public bool Hdr2D { get; init; }

	/// <summary>`canvas_lean`: the 2D pass's same-picture shortcuts (Engine_CanvasLean). Applied by
	/// the Android host only.</summary>
	public bool CanvasLean { get; init; }

	/// <summary>These settings with the Android host's keys, read from the same values.</summary>
	/// <param name="values">Values read by the runtime TOML parser.</param>
	private GameWindowSettings WithAndroidHostValues(IReadOnlyDictionary<string, string> values) => this with
	{
		Hdr2D = Engine_RefiiConfig.Bool(values, "hdr_2d", true),
		CanvasLean = Engine_RefiiConfig.Bool(values, Refii.Ui.Engine_CanvasLean.Key, false),
	};
}
