using System.Text.Json;
using System.Text.Json.Serialization;

namespace Refii.Core;

/// <summary>
/// Compile-time JSON contracts for the engine records that are read back as well as written.
/// </summary>
/// <remarks>
/// Reflection-based System.Text.Json cannot deserialize these under NativeAOT (the linux-bionic
/// Android export); see <see cref="Engine_Json"/>. The generated contracts are the default ones,
/// so the files are unchanged, and every runtime uses them.
/// </remarks>
[JsonSerializable(typeof(Refii.Game.GameDebugServer.PadFrame))]
[JsonSerializable(typeof(Refii.Lighting.Engine_LevelShadowPrebuild.ShadowBakeManifest))]
internal sealed partial class Engine_JsonContext : JsonSerializerContext
{
	/// <summary>The same contracts, written indented.</summary>
	public static Engine_JsonContext Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}
