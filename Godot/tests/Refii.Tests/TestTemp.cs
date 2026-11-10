namespace Refii.Tests;

using System;
using System.IO;

/// <summary>
/// The one temporary root every test writes under: <c>TMPDIR</c> when set (the build points it at
/// scratch on <c>/home</c>), else a folder in the user's cache on Linux, never the system temp on
/// the root partition. Ends in a separator, as <see cref="Path.GetTempPath"/> does.
/// </summary>
public static class TestTemp
{
	public static string Root { get; } = Resolve();

	private static string Resolve()
	{
		string? stated = Environment.GetEnvironmentVariable("TMPDIR");
		string root = !string.IsNullOrEmpty(stated) ? stated
			: OperatingSystem.IsWindows() ? Path.GetTempPath()
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "refii-tests");
		Directory.CreateDirectory(root);
		return Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
	}
}
