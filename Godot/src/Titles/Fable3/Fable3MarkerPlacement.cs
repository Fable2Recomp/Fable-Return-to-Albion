namespace Refii.Titles.Fable3;

using Godot;

/// <summary>
/// THE CO-OP MARKERS' SCREEN RULES (types 52/53, sub_82B51B58), the parts that need no camera:
/// the edge clamp and the corner the on-screen lift is measured from. Window pixels, y down — the
/// guest's projection sub_82287A08 maps NDC y to `(y0 + y1) / 2 - (y1 - y0) / 2 * y`
/// (0x82287BDC..0x82287BE0), so its viewport rect's y1 is the bottom edge.
/// </summary>
public static class Fable3MarkerPlacement
{
	/// <summary>The icon's side in hud.bgf units: `Contents.Icon.ImageModel`, scale 0.375 of the 100-unit quad on every slide.</summary>
	public const float Side = 37.5f;

	/// <summary>The icon's height above the element's root on `Fade In`: `Contents.Icon` position.y 25.</summary>
	public const float IconLift = 25f;

	/// <summary>
	/// sub_82B519C8: the pixel held inside the viewport rect (x0, y0, x1, y1) = (0, 0, w, h) less its
	/// margins — x in [x0 + 0.1w + 10, x1 - 0.1(x1 - (x0 + 0.1w)) - 10] (0x82B51A24, 0x82B51A2C /
	/// 0x82B51A8C), y in [y0 + 0.1h + 40, y1 - 0.09h] (0x82B51A14 / 0x82B51A50, 0x82B51A28) — then
	/// unprojected at its own depth (sub_82269260 at 0x82B51B48). The 10 and 40 are the guest's
	/// 720-line pixels, scaled to the window. No rotation: the record's `Contents.Arrow` (+20, resolved
	/// 0x82B526C8) is read by no code, and the `Clamped` slide that activates it is never played.
	/// </summary>
	public static Vector2 Clamp(Vector2 pixel, Vector2 window)
	{
		float line = window.Y / 720f;
		float left = (window.X * 0.1f) + (10f * line);
		float right = window.X - (window.X * 0.09f) - (10f * line);
		float top = (window.Y * 0.1f) + (40f * line);
		float bottom = window.Y - (window.Y * 0.09f);
		return new Vector2(Mathf.Min(Mathf.Max(pixel.X, left), right), Mathf.Min(Mathf.Max(pixel.Y, top), bottom));
	}

	/// <summary>
	/// The corner sub_82B529E8 measures the lift from: the bounds' (right, bottom) — sub_8281AA20's
	/// union keeps left, top, right, bottom with y up (0x8281ACC4..0x8281ACFC), and the corner built
	/// at 0x82B52A70 / 0x82B52A78 is (right, bottom). The element's visible content is
	/// `Contents.Icon.ImageModel`, <see cref="Side"/> square about y <see cref="IconLift"/>: right
	/// +18.75, bottom +6.25 above the root (the empty `Title` has no area, which the union's area
	/// test skips at 0x8281ACBC). <paramref name="placed"/> is the root as last placed;
	/// <paramref name="unit"/> window pixels per hud.bgf unit.
	/// </summary>
	public static Vector2 LiftCorner(Vector2 placed, float unit) =>
		new(placed.X + (Side * 0.5f * unit), placed.Y - ((IconLift - (Side * 0.5f)) * unit));
}
