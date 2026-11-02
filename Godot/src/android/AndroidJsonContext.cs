using System.Text.Json.Serialization;

namespace Refii.AndroidRuntime;

/// <summary>
/// Compile-time JSON contracts for the Android records that are READ back as well as written.
/// </summary>
/// <remarks>
/// Reflection-based System.Text.Json cannot deserialize these records under NativeAOT (the
/// linux-bionic export): their positional constructors, nullable members and collections need
/// generic converters made at run time. The generated contracts are the same default contracts,
/// so the files are unchanged, and every runtime reads and writes through them. Records that are
/// only written go through <see cref="Refii.Core.Engine_Json"/>.
/// </remarks>
[JsonSerializable(typeof(AndroidInstallState.State))]
[JsonSerializable(typeof(AndroidPhoneCompiler.BundleManifest))]
[JsonSerializable(typeof(AndroidPhoneLauncher.DiagnosticRequest))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
[JsonSerializable(typeof(AndroidTouchCustomLayout.LayoutsFile))]
internal sealed partial class AndroidJsonContext : JsonSerializerContext
{
}
