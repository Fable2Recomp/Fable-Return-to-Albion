using System;
using System.Runtime.Versioning;

namespace Refii.Core;

/// <summary>
/// Which OS this process runs on, where .NET's own answer depends on the managed runtime.
/// </summary>
public static class Engine_Platform
{
	/// <summary>
	/// True on Android under either managed runtime.
	/// </summary>
	/// <remarks>
	/// The default Android export runs Mono for the <c>android-arm64</c> runtime identifier, where
	/// <see cref="OperatingSystem.IsAndroid"/> is true. The NativeAOT export
	/// (<c>export-apk.py --nativeaot</c>) is built for <c>linux-bionic-arm64</c>, whose class
	/// library is a LINUX build: there <see cref="OperatingSystem.IsAndroid"/> is a constant
	/// false and <see cref="OperatingSystem.IsLinux"/> a constant true. Every Android export is
	/// compiled with <c>GODOT_ANDROID</c> (Godot.NET.Sdk defines it for
	/// <c>GodotTargetPlatform=android</c>), which covers both.
	/// </remarks>
	[SupportedOSPlatformGuard("android")]
	public static bool IsAndroid =>
#if GODOT_ANDROID
		true;
#else
		OperatingSystem.IsAndroid();
#endif

	/// <summary>
	/// Android on the linux-bionic class library, i.e. the NativeAOT export: built for Android,
	/// yet the runtime says Linux. Its platform services are a Linux build's (OpenSSL for
	/// cryptography, stderr for diagnostics), none of which an Android app has.
	/// </summary>
	public static bool IsLinuxBionic => IsAndroid && !OperatingSystem.IsAndroid();

	/// <summary>Desktop Linux: Linux, and not Android running the linux-bionic runtime.</summary>
	public static bool IsDesktopLinux => OperatingSystem.IsLinux() && !IsAndroid;
}
