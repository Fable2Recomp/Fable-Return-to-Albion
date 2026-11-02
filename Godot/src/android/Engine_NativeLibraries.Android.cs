using System;
using System.Runtime.InteropServices;

namespace Refii.Core;

/// <summary>Android: a title's instance from the APK's one flat native folder.</summary>
public static partial class Engine_NativeLibraries
{
	/// <summary>
	/// The APK file holding one of a title's instance libraries for the active title.
	///
	/// On desktop each title's instance sits in its own <c>bin/&lt;Folder&gt;/</c> under the same
	/// names. An APK extracts its natives into one folder, so Fable 3's bridge is built as
	/// <c>librefiii_hostbridge.so</c> (cmake/refii_android.cmake); Fable 2's keep their names.
	/// </summary>
	public static string AndroidFileNameFor(string library) =>
		library == "refii_hostbridge" && Engine_Title.Active == Engine_Title.Fable3
			? "librefiii_hostbridge.so"
			: FileNameFor(library);

	/// <summary>
	/// On the phone, loads Fable 3's bridge through the app's own library search. Fable 2's instance
	/// is left to the runtime's default probing, which finds its names in the APK's folder as before;
	/// elsewhere every instance comes from its title's bin folder.
	/// </summary>
	private static bool TryResolveAndroidInstance(string library, out IntPtr handle)
	{
		handle = IntPtr.Zero;
		if (!Engine_Platform.IsAndroid)
			return false;
		string file = AndroidFileNameFor(library);
		return file != FileNameFor(library) && NativeLibrary.TryLoad(file, out handle);
	}
}
