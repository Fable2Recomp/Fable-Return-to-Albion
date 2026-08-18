using Godot;

namespace Refii.Launcher.Core;

/// <summary>
/// Fluid scale: every size in the launcher derives from the viewport, like a
/// responsive site. Design base is 1280x760 (Unit 1.0); bigger windows and
/// higher-DPI displays scale everything up, smaller windows scale down to a
/// readable floor. Fonts re-rasterize at the target size, so scaling stays crisp.
/// </summary>
public static class LauncherScale
{
	public const float BaseWidth = 1280.0f;
	public const float BaseHeight = 760.0f;

	// Floors and ceilings keep type legible at extreme window sizes.
	private const float MinUnit = 0.8f;
	private const float MaxUnit = 2.2f;

	public static float Unit { get; private set; } = 1.0f;

	public static void Update(Vector2 viewport)
	{
		float unit = Mathf.Min(viewport.X / BaseWidth, viewport.Y / BaseHeight);
		Unit = Mathf.Clamp(unit, MinUnit, MaxUnit);
	}

	/// <summary>Scaled layout unit (pixels, rounded).</summary>
	public static int Px(float designPx) =>
		Mathf.Max(1, Mathf.RoundToInt(designPx * Unit));

	/// <summary>Scaled font size with a legibility floor.</summary>
	public static int Font(float designPx) =>
		Mathf.Max(10, Mathf.RoundToInt(designPx * Unit));
}
