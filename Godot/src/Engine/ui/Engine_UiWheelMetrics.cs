namespace Refii.Ui;

/// <summary>
/// The wheel's `OrbConst`, `FrameConst` and `ExpandConst` — the sizes its animations lerp
/// between.
///
/// These thirteen fields are READ by `menu/menulib.lua` and `menu/menucloning.lua` and ASSIGNED
/// by no shipped chunk (unlike `ItemConst`, `Timing` and `Const`, which
/// `menu/menustateandconstants.lua` assigns in full). They are not in the xex either: all
/// sixteen literals return zero hits in ASCII, UTF-16 and IDA's string cache, where the same
/// search finds `"SpawnButton"` at 0x820cfa53. Nor can C++ inject them — its only two channels
/// into Gameface Lua are a literal source string compiled by `sub_82426470` (a closed vocabulary
/// of 34 `g_Menu:*`/`GUI:*` calls, no assignments) and named element attributes via
/// `sub_826B9440`/`sub_826B9510`, whose only names are `String1..4` and `Float1..4`. They come
/// from a Lua chunk the game does not ship; do not search the binary again.
///
/// So this is a PARAMETER, left zero rather than filled with invented sizes: the animation
/// shapes in <see cref="Engine_UiWheelAnimation"/> are exact lerps of the form
/// `base + delta * t` against these.
///
/// The deltas are redundant with the bases and the script uses both, so
/// <see cref="IsConsistent"/> can check a candidate set against its own arithmetic.
/// </summary>
public sealed record Engine_UiWheelMetrics(
	float SizeNorm,
	float SizeHigh,
	float SizeMax,
	float SizePressed,
	float SizeNormHighDelta,
	float SizeHighMaxDelta,
	float SizeMaxNormDelta,
	float SizeHighPressedDelta,
	float PivotAmount,
	float ScaleNorm,
	float ScaleHigh,
	float ScaleNormHighDelta,
	float HierarchyGap)
{
	/// <summary>
	/// The stand-in used while the real values are unknown — everything zero, so an orb draws
	/// nothing rather than drawing at an invented size.
	/// </summary>
	public static readonly Engine_UiWheelMetrics Unknown =
		new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

	/// <summary>False while this is <see cref="Unknown"/> — nothing has supplied the sizes.</summary>
	public bool IsKnown => !Equals(Unknown);

	/// <summary>
	/// Whether the bases and the deltas agree, within a float's tolerance:
	/// `SIZE_NORM + SIZE_NORM_HIGH_DELTA == SIZE_HIGH`,
	/// `SIZE_HIGH + SIZE_HIGH_MAX_DELTA == SIZE_MAX`, and the same for the frame scales.
	/// </summary>
	public bool IsConsistent =>
		Near(SizeNorm + SizeNormHighDelta, SizeHigh) &&
		Near(SizeHigh + SizeHighMaxDelta, SizeMax) &&
		Near(ScaleNorm + ScaleNormHighDelta, ScaleHigh);

	private static bool Near(float a, float b) => System.Math.Abs(a - b) < 0.0001f;

	public string Describe() => IsKnown
		? $"orb norm {SizeNorm} high {SizeHigh} max {SizeMax} pressed {SizePressed}, " +
			$"pivot {PivotAmount}, frame {ScaleNorm}/{ScaleHigh}, gap {HierarchyGap}" +
			(IsConsistent ? string.Empty : " — INCONSISTENT: the deltas do not match the bases")
		: "NOT KNOWN — OrbConst/FrameConst/ExpandConst are read by the shipped scripts and " +
			"assigned by none of them; the wheel's animations hold still until they are found";
}
