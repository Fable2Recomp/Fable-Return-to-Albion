namespace Refii.Ui;

/// <summary>
/// `menu/menulib.lua`'s animation functions, translated — the nine states a quick-wheel item
/// moves through. Pure lerps against `percent_complete`, no easing; bases and deltas arrive as
/// <see cref="Engine_UiWheelMetrics"/>, and `position.x`/`position.y` arrive as parameters so
/// this needs no element lookup.
/// </summary>
public static class Engine_UiWheelAnimation
{
	/// <summary>
	/// `InitAnimInfo(item, t, info)` — the parked state every animation starts from.
	///
	/// <code>
	/// percent_complete = t          image_opacity  = 100
	/// orb_scale        = OrbConst.SIZE_NORM        orb_tint    = 0
	/// inside_tint      = 0          rim_highlighted = false
	/// beam_opacity     = 100        highlight_opacity = 0
	/// title_opacity    = 90         frame_scale = FrameConst.SCALE_NORM
	/// folder_opacity   = 100        arrow_opacity = 0     child_opacity = 100
	/// manage_children  = false      detail_map_type = 0
	/// pivot_x = 0                   pivot_y = 0
	/// </code>
	///
	/// `title_opacity` 90 is `main.1 pc11..12` up[0], bound at `main pc16..17`.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo Init(
		float t, Engine_UiWheelMetrics m) =>
		new(
			PercentComplete: t,
			PivotX: 0f,
			PivotY: 0f,
			ImageOpacity: Engine_UiWheelConstants.ImageOpacityMax,
			OrbScale: m.SizeNorm,
			OrbTint: 0f,
			InsideTint: 0f,
			FrameScale: m.ScaleNorm,
			HighlightOpacity: 0f,
			TitleOpacity: Engine_UiWheelConstants.TitleOpacityRest,
			ChildOpacity: 100f,
			DetailMapType: 0,
			RimHighlighted: false,
			BeamOpacity: 100f,
			FolderOpacity: 100f,
			ArrowOpacity: 0f,
			ManageChildren: false);

	/// <summary>
	/// `AnimateNormalToHighlighted` — the orb the cursor has just arrived on.
	///
	/// <code>
	/// pivot_x       = -OrbConst.PIVOT_AMOUNT * t
	/// image_opacity = IMAGE_OPACITY_MIN + IMAGE_OPACITY_DELTA * t
	/// orb_scale     = OrbConst.SIZE_NORM + OrbConst.SIZE_NORM_HIGH_DELTA * t
	/// orb_tint = t ; inside_tint = t ; detail_map_type = 1
	/// title_opacity = 90 + 10 * t
	/// manage_children = true ; child_opacity = item.IsOpen and 100 or 0
	/// frame_scale   = FrameConst.SCALE_NORM + FrameConst.SCALE_NORM_HIGH_DELTA * t
	/// rim_highlighted = true
	/// </code>
	///
	/// `title_opacity` is `main.2 pc30..35` = up[1] + up[2] * t, bound at `main pc20..23` to 90, 10.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo NormalToHighlighted(
		float t, Engine_UiWheelMetrics m, bool isOpen) =>
		Init(t, m) with
		{
			PivotX = -m.PivotAmount * t,
			ImageOpacity = Engine_UiWheelConstants.ImageOpacityMin +
				(Engine_UiWheelConstants.ImageOpacityDelta * t),
			OrbScale = m.SizeNorm + (m.SizeNormHighDelta * t),
			OrbTint = t,
			InsideTint = t,
			DetailMapType = 1,
			TitleOpacity = Engine_UiWheelConstants.TitleOpacityRest +
				(Engine_UiWheelConstants.TitleOpacityDelta * t),
			ChildOpacity = isOpen ? 100f : 0f,
			FrameScale = m.ScaleNorm + (m.ScaleNormHighDelta * t),
			ManageChildren = true,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimateHighlightedToNormal` — the mirror. Not `1 - t` of the rise: it starts from the
	/// HIGH values and subtracts, so `SIZE_HIGH`/`SCALE_HIGH` are stated separately from the deltas.
	///
	/// <code>
	/// pivot_x       = -OrbConst.PIVOT_AMOUNT + OrbConst.PIVOT_AMOUNT * t
	/// image_opacity = IMAGE_OPACITY_MAX - IMAGE_OPACITY_DELTA * t
	/// orb_scale     = OrbConst.SIZE_HIGH  - OrbConst.SIZE_NORM_HIGH_DELTA * t
	/// orb_tint      = 1 - t ; inside_tint = orb_tint ; detail_map_type = 0
	/// title_opacity = 100 - 10 * t
	/// manage_children = true ; child_opacity = item.IsOpen and 100 or 0
	/// frame_scale   = FrameConst.SCALE_HIGH - FrameConst.SCALE_NORM_HIGH_DELTA * t
	/// </code>
	///
	/// `title_opacity` is `main.3 pc35..40` = up[1] - up[2] * t, bound at `main pc26..29` to
	/// 100, 10 — the same delta as the rise, off the band's top.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo HighlightedToNormal(
		float t, Engine_UiWheelMetrics m, bool isOpen)
	{
		float tint = 1f - t;
		return Init(t, m) with
		{
			PivotX = -m.PivotAmount + (m.PivotAmount * t),
			ImageOpacity = Engine_UiWheelConstants.ImageOpacityMax -
				(Engine_UiWheelConstants.ImageOpacityDelta * t),
			OrbScale = m.SizeHigh - (m.SizeNormHighDelta * t),
			OrbTint = tint,
			InsideTint = tint,
			DetailMapType = 0,
			TitleOpacity = Engine_UiWheelConstants.TitleOpacityTop -
				(Engine_UiWheelConstants.TitleOpacityDelta * t),
			ChildOpacity = isOpen ? 100f : 0f,
			FrameScale = m.ScaleHigh - (m.ScaleNormHighDelta * t),
			ManageChildren = true,
		};
	}

	/// <summary>
	/// `AnimateHighlightedToMaximised` — the orb opening into the ring's centre.
	///
	/// <code>
	/// pivot_x = position.x * t + (-OrbConst.PIVOT_AMOUNT + OrbConst.PIVOT_AMOUNT * t)
	/// pivot_y = (position.y + ExpandConst.HIERARCHY_GAP) * t
	/// image_opacity = 100
	/// orb_scale = OrbConst.SIZE_HIGH + OrbConst.SIZE_HIGH_MAX_DELTA * t
	/// orb_tint = 1 ; inside_tint = 1 - t ; detail_map_type = 1 ; title_opacity = 100
	/// manage_children = true ; child_opacity = 100 * t
	/// highlight_opacity = 100 * t
	/// frame_scale = FrameConst.SCALE_HIGH - FrameConst.SCALE_NORM_HIGH_DELTA * t
	/// rim_highlighted = true
	/// </code>
	///
	/// The frame shrinks on the way in: `main.5 pc124..133` is SCALE_HIGH minus the delta,
	/// landing on SCALE_NORM. pc121..123 sets `highlight_opacity = 100 * t`; pc166 `rim_highlighted`.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo HighlightedToMaximised(
		float t, Engine_UiWheelMetrics m, float positionX, float positionY) =>
		Init(t, m) with
		{
			PivotX = (positionX * t) + (-m.PivotAmount + (m.PivotAmount * t)),
			PivotY = (positionY + m.HierarchyGap) * t,
			OrbScale = m.SizeHigh + (m.SizeHighMaxDelta * t),
			OrbTint = 1f,
			InsideTint = 1f - t,
			DetailMapType = 1,
			TitleOpacity = 100f,
			ChildOpacity = 100f * t,
			HighlightOpacity = 100f * t,
			FrameScale = m.ScaleHigh - (m.ScaleNormHighDelta * t),
			ManageChildren = true,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimateMaximisedToHighlighted` — the same journey back.
	///
	/// <code>
	/// pivot_x = -OrbConst.PIVOT_AMOUNT * t + position.x * (1 - t)
	/// pivot_y = (position.y + ExpandConst.HIERARCHY_GAP) * (1 - t)
	/// image_opacity = 100
	/// orb_scale = OrbConst.SIZE_MAX - OrbConst.SIZE_HIGH_MAX_DELTA * t
	/// orb_tint = 1 ; inside_tint = t ; detail_map_type = 1 ; title_opacity = 100
	/// manage_children = true ; child_opacity = 100 - 100 * t
	/// highlight_opacity = 100 - 100 * t
	/// frame_scale = FrameConst.SCALE_NORM + FrameConst.SCALE_NORM_HIGH_DELTA * t
	/// rim_highlighted = true
	/// </code>
	///
	/// The distance term is dead code: `main.4 pc58..77` computes
	/// `sqrt(position.x^2 + position.y^2)` into R3, which is never read again. Reproduced by
	/// omission. The frame's base is SCALE_NORM (`main.4 pc82..91`), not SCALE_HIGH.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo MaximisedToHighlighted(
		float t, Engine_UiWheelMetrics m, float positionX, float positionY) =>
		Init(t, m) with
		{
			PivotX = (-m.PivotAmount * t) + (positionX * (1f - t)),
			PivotY = (positionY + m.HierarchyGap) * (1f - t),
			OrbScale = m.SizeMax - (m.SizeHighMaxDelta * t),
			OrbTint = 1f,
			InsideTint = t,
			DetailMapType = 1,
			TitleOpacity = 100f,
			ChildOpacity = 100f - (100f * t),
			HighlightOpacity = 100f - (100f * t),
			FrameScale = m.ScaleNorm + (m.ScaleNormHighDelta * t),
			ManageChildren = true,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimateMaximisedToParent` — the maximised orb shrinking back to a plain one as its
	/// parent takes over.
	///
	/// <code>
	/// pivot_x = position.x
	/// pivot_y = position.y + ExpandConst.HIERARCHY_GAP
	/// image_opacity = IMAGE_OPACITY_MAX - IMAGE_OPACITY_DELTA * t
	/// orb_scale = OrbConst.SIZE_MAX - OrbConst.SIZE_MAX_NORM_DELTA * t
	/// orb_tint = 1 ; inside_tint = 0 ; detail_map_type = 1 ; title_opacity = 90
	/// frame_scale = FrameConst.SCALE_NORM ; rim_highlighted = true
	/// </code>
	///
	/// `title_opacity` 90 is up[1], bound at `main pc38..45`.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo MaximisedToParent(
		float t, Engine_UiWheelMetrics m, float positionX, float positionY) =>
		Init(t, m) with
		{
			PivotX = positionX,
			PivotY = positionY + m.HierarchyGap,
			ImageOpacity = Engine_UiWheelConstants.ImageOpacityMax -
				(Engine_UiWheelConstants.ImageOpacityDelta * t),
			OrbScale = m.SizeMax - (m.SizeMaxNormDelta * t),
			OrbTint = 1f,
			InsideTint = 0f,
			DetailMapType = 1,
			TitleOpacity = Engine_UiWheelConstants.TitleOpacityRest,
			FrameScale = m.ScaleNorm,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimateParentToMaximised` — and back the other way, growing from norm to max.
	///
	/// <code>
	/// pivot_x = position.x
	/// pivot_y = position.y + ExpandConst.HIERARCHY_GAP
	/// image_opacity = IMAGE_OPACITY_MIN + IMAGE_OPACITY_DELTA * t
	/// orb_scale = OrbConst.SIZE_NORM + OrbConst.SIZE_MAX_NORM_DELTA * t
	/// orb_tint = 1 ; inside_tint = 0 ; detail_map_type = 1 ; title_opacity = 90
	/// frame_scale = FrameConst.SCALE_NORM ; rim_highlighted = true
	/// </code>
	///
	/// `title_opacity` 90 is up[1], bound at `main pc38..45`.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo ParentToMaximised(
		float t, Engine_UiWheelMetrics m, float positionX, float positionY) =>
		Init(t, m) with
		{
			PivotX = positionX,
			PivotY = positionY + m.HierarchyGap,
			ImageOpacity = Engine_UiWheelConstants.ImageOpacityMin +
				(Engine_UiWheelConstants.ImageOpacityDelta * t),
			OrbScale = m.SizeNorm + (m.SizeMaxNormDelta * t),
			OrbTint = 1f,
			InsideTint = 0f,
			DetailMapType = 1,
			TitleOpacity = Engine_UiWheelConstants.TitleOpacityRest,
			FrameScale = m.ScaleNorm,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimateRootIntro` — the wheel's first frame; every field is a constant, `t` unused.
	///
	/// <code>
	/// pivot_x = 0 ; pivot_y = 0 ; image_opacity = 100
	/// orb_scale = OrbConst.SIZE_MAX ; orb_tint = 1 ; inside_tint = 0
	/// detail_map_type = 1 ; title_opacity = 100 ; frame_scale = FrameConst.SCALE_NORM
	/// manage_children = true ; child_opacity = 100 ; arrow_opacity = 100
	/// rim_highlighted = true
	/// </code>
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo RootIntro(
		float t, Engine_UiWheelMetrics m) =>
		Init(t, m) with
		{
			OrbScale = m.SizeMax,
			OrbTint = 1f,
			InsideTint = 0f,
			DetailMapType = 1,
			TitleOpacity = 100f,
			FrameScale = m.ScaleNorm,
			ChildOpacity = 100f,
			ArrowOpacity = 100f,
			ManageChildren = true,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimatePressed` — the orb dips and returns across one `t`: it writes `orb_scale` twice,
	/// shrinking HIGH→PRESSED at double rate, then growing back from PRESSED past t = 0.5.
	///
	/// <code>
	/// pivot_x   = -OrbConst.PIVOT_AMOUNT
	/// orb_scale = OrbConst.SIZE_HIGH    - OrbConst.SIZE_HIGH_PRESSED_DELTA * (t * 2)
	/// orb_scale = OrbConst.SIZE_PRESSED + OrbConst.SIZE_HIGH_PRESSED_DELTA * ((t - 0.5) * 2)
	/// orb_tint  = 1 ; inside_tint = orb_tint ; detail_map_type = 1
	/// frame_scale = FrameConst.SCALE_HIGH ; rim_highlighted = true
	/// </code>
	///
	/// `inside_tint` does not ride `t`: `main.9 pc32..34` copies `orb_tint = 1` into it, so both
	/// tints stay full and only the scale moves.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo Pressed(
		float t, Engine_UiWheelMetrics m) =>
		Init(t, m) with
		{
			PivotX = -m.PivotAmount,
			OrbScale = t <= 0.5f
				? m.SizeHigh - (m.SizeHighPressedDelta * (t * 2f))
				: m.SizePressed + (m.SizeHighPressedDelta * ((t - 0.5f) * 2f)),
			OrbTint = 1f,
			InsideTint = 1f,
			DetailMapType = 1,
			FrameScale = m.ScaleHigh,
			RimHighlighted = true,
		};

	/// <summary>
	/// `AnimateReset` — back to the parked pose, with no interpolation.
	///
	/// <code>
	/// image_opacity = 100 ; orb_scale = OrbConst.SIZE_NORM ; frame_scale = FrameConst.SCALE_NORM
	/// </code>
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo Reset(
		float t, Engine_UiWheelMetrics m) => Init(t, m);

	/// <summary>Runs whichever state an item is in.</summary>
	public static Engine_UiWheelConstants.AnimationInfo For(
		Engine_UiWheelConstants.AnimationState state, float t, Engine_UiWheelMetrics m,
		bool isOpen = false, float positionX = 0f, float positionY = 0f) => state switch
	{
		Engine_UiWheelConstants.AnimationState.NormalToHighlighted =>
			NormalToHighlighted(t, m, isOpen),
		Engine_UiWheelConstants.AnimationState.HighlightedToNormal =>
			HighlightedToNormal(t, m, isOpen),
		Engine_UiWheelConstants.AnimationState.Pressed => Pressed(t, m),
		Engine_UiWheelConstants.AnimationState.HighlightedToMaximised =>
			HighlightedToMaximised(t, m, positionX, positionY),
		Engine_UiWheelConstants.AnimationState.MaximisedToHighlighted =>
			MaximisedToHighlighted(t, m, positionX, positionY),
		Engine_UiWheelConstants.AnimationState.MaximisedToParent =>
			MaximisedToParent(t, m, positionX, positionY),
		Engine_UiWheelConstants.AnimationState.ParentToMaximised =>
			ParentToMaximised(t, m, positionX, positionY),
		Engine_UiWheelConstants.AnimationState.RootIntro => RootIntro(t, m),
		_ => Reset(t, m),
	};
}
