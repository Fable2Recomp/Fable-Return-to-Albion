namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's GUI fonts for the culture in force, read once per culture and shared by every screen.
/// </summary>
/// <remarks>
/// ONE LOAD, NOT ONE PER SCREEN PER FRAME. A font's atlas is decoded, supersampled and mipmapped
/// (Engine_BitmapFontTexture), about 80 MB and most of a second on a phone. The splash, the boxes,
/// the screens, the canvases and the world icons each loaded their own copy, and the per-frame
/// screens tried again on every frame their font was missing: on Android that held the game at about one frame a second and grew video
/// memory until the system killed it. A failure is logged once and kept until the culture changes.
/// </remarks>
public static class Fable3Fonts
{
	private static string? _culture;
	private static Engine_UiFontLibrary? _library;

	/// <summary>The library for a culture: the one already loaded when it is the same culture.</summary>
	public static Engine_UiFontLibrary For(string culture)
	{
		if (_library is not null && _culture == culture)
		{
			return _library;
		}

		_culture = culture;
		_library = Engine_UiFontLibrary.Load(Refii.Core.Engine_GameData.Resolve(), culture);
		GD.Print($"[fable3-fonts] {culture}: {_library.Directory} (small {_library.Get(UiFontBucket.Small) is not null}, " +
			$"normal {_library.Get(UiFontBucket.Normal) is not null}, large {_library.Get(UiFontBucket.Large) is not null})");
		foreach (string failure in _library.Failures)
		{
			GD.PushWarning("[fable3-fonts] " + failure);
		}

		return _library;
	}
}
