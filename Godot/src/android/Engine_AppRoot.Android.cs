using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// Android's app root.
/// </summary>
/// <remarks>
/// Android uses OS.GetUserDataDir because its installed package is immutable; the same
/// layout beneath that private root keeps config, assets, saves, logs, and caches writable.
/// </remarks>
public static partial class Engine_AppRoot
{
	/// <summary>
	/// Android maps res:// and its native libraries from an installed package. Neither
	/// is a writable desktop install tree. Keep user data in the engine-provided private
	/// directory; shipped libraries continue to use the Android linker's package search.
	/// </summary>
	private static string ResolveAndroid()
	{
		string privateRoot = AndroidPrivateRoot(OS.GetUserDataDir());
		GD.Print($"[android-path] app_root={privateRoot}; config={System.IO.Path.Combine(privateRoot, "refii.toml")}");
		return privateRoot;
	}

	/// <summary>Validates the Android engine's app-private writable directory.</summary>
	/// <param name="userDataDirectory">The physical pathname supplied by OS.GetUserDataDir.</param>
	/// <returns>The normalized root used by assets, saves, caches, logs, and launcher config.</returns>
	/// <exception cref="ArgumentException">The engine returned no private directory.</exception>
	internal static string AndroidPrivateRoot(string userDataDirectory)
	{
		if (string.IsNullOrWhiteSpace(userDataDirectory))
			throw new ArgumentException("Android private data directory is unavailable.", nameof(userDataDirectory));
		return Normalize(userDataDirectory);
	}
}
