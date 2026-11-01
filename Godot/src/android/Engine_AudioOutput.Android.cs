using System;
using Refii.Core;

namespace Refii.Audio;

/// <summary>Android: the host bridge the process already has mapped, for the guest's audio output.</summary>
public partial class Engine_AudioOutput
{
	/// <summary>The bridge's file as <c>/proc/self/maps</c> names it; see <see cref="MappedPath"/>.</summary>
	private static string? AndroidBridgePath() =>
		MappedPath(FileSystemMaps(), Engine_NativeLibraries.FileNameFor(BridgeLibrary));

	/// <summary>
	/// The file a library is mapped from, read off <c>/proc/self/maps</c>.
	///
	/// Android loads the shipped bridge through its linker's package search, not the resolver,
	/// so <see cref="Engine_NativeLibraries.LoadedPath"/> has nothing. The mapping names the
	/// extracted file the process already holds, which is the one Godot must open.
	/// </summary>
	internal static string? MappedPath(string? maps, string fileName)
	{
		if (string.IsNullOrEmpty(maps))
			return null;

		foreach (string line in maps.Split('\n'))
		{
			int slash = line.IndexOf('/');
			if (slash < 0)
				continue;

			string path = line[slash..].TrimEnd();
			if (path.EndsWith("/" + fileName, StringComparison.Ordinal))
				return path;
		}

		return null;
	}

	private static string? FileSystemMaps()
	{
		try { return System.IO.File.ReadAllText("/proc/self/maps"); }
		catch (Exception) { return null; }
	}
}
