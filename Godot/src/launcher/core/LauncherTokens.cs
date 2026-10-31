using Godot;

namespace Refii.Launcher.Core;

/// <summary>
/// Design tokens for the Lionhead/Fable launcher language. Design-only: colors, spacing,
/// radius and type scale. No behavior. Mirrors the token tables in the redesign report.
/// </summary>
public static class LauncherTokens
{
	// Palette (proposed, applied): oak base, parchment surfaces, guild gold trim,
	// emerald hero action, Will-cyan glow, semantic status colors.
	public static readonly Color Oak = new("#1c1512");
	public static readonly Color OakRaised = new("#241d16");
	public static readonly Color OakDim = new("#2a221a");
	public static readonly Color Parchment = new("#e3d5ba");
	public static readonly Color ParchmentRaised = new("#ede2cb");
	public static readonly Color ParchmentLit = new("#f3e9d5");
	public static readonly Color Gold = new("#daa520");
	public static readonly Color GoldBright = new("#e8c55a");
	public static readonly Color Walnut = new("#2b221b");
	public static readonly Color Sepia = new("#8c7b6b");
	public static readonly Color Cream = new("#e8dcc2");
	public static readonly Color Emerald = new("#276b44");    // muted earthy "good" green
	public static readonly Color EmeraldLit = new("#2c7a4e"); // hover, still earthy
	public static readonly Color WillCyan = new("#00e5ff");
	public static readonly Color Sunset = new("#e67e22");
	public static readonly Color Crimson = new("#900c3f");

	// Glass surfaces: translucent so the artwork stays the hero. The artwork
	// is the canvas; UI floats above it.
	public static readonly Color ParchmentGlass = new Color(Parchment, 0.78f);
	public static readonly Color ParchmentGlassStrong = new Color(Parchment, 0.92f);
	public static readonly Color OakGlass = new Color(Oak, 0.62f);
	public static readonly Color InkGlass = new Color(0.10f, 0.08f, 0.05f, 0.72f);
	/// <summary>The header card: a light, neutral grey wash that keeps cream text readable over any artwork.</summary>
	public static readonly Color SlateGlass = new Color(0.22f, 0.22f, 0.22f, 0.68f);

	// Spacing, 4px grid.
	public const int Sp1 = 4;
	public const int Sp2 = 8;
	public const int Sp3 = 12;
	public const int Sp4 = 16;
	public const int Sp6 = 24;
	public const int Sp8 = 32;

	// Radius. ONE system: soft rectangles for floating glass surfaces, a
	// pill for the tiny notice tag, and a 6px primary action. No full-pill
	// buttons, no rounded-everything.
	public const int RadiusSm = 6;    // primary action
	public const int RadiusMd = 10;   // floating drawer, dialogs
	public const int RadiusPill = 999; // notice tag only

	// Type scale. Display face is the bundled fable2.ttf (wrapped in a FontVariation
	// for tracked spacing); everything else is the system UI face (no font override).
	public const int DisplaySize = 22;
	public const int InfoTitleSize = 20;
	public const int WordmarkSize = 18;
	public const int BodySize = 14;
	public const int CaptionSize = 12;

	// Accent text readable on parchment (the raw Gold/Sepia fail AA on it).
	public static readonly Color GoldInk = new("#6b5312");
	public static readonly Color SepiaInk = new("#5a4e3e");
}
