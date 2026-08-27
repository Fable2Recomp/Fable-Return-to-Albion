namespace Refii.Ui;

/// <summary>
/// `menu/menustateandconstants.lua` and `menu/menulib.lua` translated — the quick wheel's model,
/// a different object from the expandable menu.
///
/// Both packages write the same `g_Menu` global: `menustateandconstants.dis pc0` sets
/// `g_Menu.ItemConst`, `expandablemenu/expandablemenustateandconstants.dis pc4` sets
/// `g_Menu.FolderConst` (pc178..186: 180 / 280 / 25 / 304 / 60, carried by
/// <see cref="Engine_UiMenuConstants"/>). The boundary between wheel and list is per FIELD, not
/// per table. The wheel clones at runtime — see <see cref="Engine_UiMenuCloning"/>.
///
/// `g_Menu.OrbConst.PIVOT_AMOUNT`, `OrbConst.SIZE_MAX`, `OrbConst.SIZE_HIGH_MAX_DELTA` and
/// `ExpandConst.HIERARCHY_GAP` are read by `menulib.lua`/`menucloning.lua` but assigned in no
/// shipped chunk, so the animation shapes below stay formulas with those four left open.
/// </summary>
public static class Engine_UiWheelConstants
{
	/// <summary>
	/// `g_Menu.ItemConst` — what a wheel entry is. Overlaps the list's `EventConst` at 41-45 and
	/// 201-203, but 20-23 here are learn/discard ability and spell where the list uses 20-23 for
	/// the four ability categories.
	/// </summary>
	public enum ItemType
	{
		Node = 0,
		Folder = 1,
		ExpressionButton = 2,
		MenuButton = 3,
		DogCommandButton = 4,
		InventoryItemButton = 5,
		QuestItemButton = 6,
		SuggestionButton = 7,
		SpellsButton = 8,

		LearnAbilityItem = 20,
		LearnSpellItem = 21,
		DiscardAbilityItem = 22,
		DiscardSpellItem = 23,

		BuyInventoryItem = 30,
		SellInventoryItem = 31,

		WorldMapRegionItem = 41,
		WorldMapQuestsItem = 42,
		WorldMapJobsItem = 43,
		WorldMapSalesItem = 44,
		WorldMapFamilyItem = 45,

		RegionStopItem = 50,

		Navigation = 201,
		ButtonRemove = 202,
		ButtonSet = 203,
	}

	/// <summary>`g_Menu.Timing.ANIMATE_TIME` — the wheel's, not the list's 0.15.</summary>
	public const float AnimateTime = 0.225f;

	/// <summary>`g_Menu.Timing.OPACITY_FADE_SPEED` — stated as `1 / ANIMATE_TIME`.</summary>
	public const float OpacityFadeSpeed = 1f / AnimateTime;

	/// <summary>`g_Menu.Const` — the orb image's opacity band and the ring's tint alpha.</summary>
	public const float ImageOpacityMax = 100f;

	public const float ImageOpacityMin = 80f;

	/// <summary>`IMAGE_OPACITY_DELTA` = 20 = MAX − MIN, stated separately by the script.</summary>
	public const float ImageOpacityDelta = 20f;

	public const float RingTintAlpha = 88f;

	/// <summary>
	/// Title-opacity band, 90..100 (not the image band's 80..100). `menulib.dis main pc4..6`
	/// loads 90/100 and computes the delta 10; pc16..55 bind them as the animation upvalues.
	/// </summary>
	public const float TitleOpacityRest = 90f;

	public const float TitleOpacityTop = 100f;

	/// <summary>Stated as the subtraction `100 - 90` at `main pc6`, not as a literal.</summary>
	public const float TitleOpacityDelta = TitleOpacityTop - TitleOpacityRest;

	/// <summary>
	/// Orb diffuse colour and tint scale. `menulib.dis main pc7..11` loads 255/153/0/102 and
	/// computes `102 / 255`; `main.12` writes up[0..2] into the Rim's and the captured orb
	/// material's `diffuse.r/g/b` and multiplies every `TintingAmount` by up[3].
	/// </summary>
	public const float OrbDiffuseR = 255f;

	public const float OrbDiffuseG = 153f;

	public const float OrbDiffuseB = 0f;

	/// <summary>The numerator of the tint scale, `main pc10`.</summary>
	public const float OrbTintAlpha = 102f;

	/// <summary>`102 / 255` = 0.4, stated as the division at `main pc11`.</summary>
	public const float OrbTintScale = OrbTintAlpha / OrbDiffuseR;

	/// <summary>
	/// The nine animation states a wheel item moves through. Each is per-field lerps against
	/// `percent_complete`, no easing. `AnimateNormalToHighlighted` is `menulib.dis main.2`,
	/// writes pc0..54:
	///
	/// <code>
	/// pc0..6    pivot_x         = -OrbConst.PIVOT_AMOUNT * t
	/// pc7..14   image_opacity   = IMAGE_OPACITY_MIN + IMAGE_OPACITY_DELTA * t
	/// pc15..24  orb_scale       = OrbConst.SIZE_NORM + OrbConst.SIZE_NORM_HIGH_DELTA * t
	/// pc25..26  orb_tint        = t
	/// pc27..28  inside_tint     = orb_tint
	/// pc29      detail_map_type = 1
	/// pc30..35  title_opacity   = 90 + 10 * t
	/// pc36      manage_children = true
	/// pc37..42  child_opacity   = item.IsOpen and 100 or 0
	/// pc43..52  frame_scale     = FrameConst.SCALE_NORM + FrameConst.SCALE_NORM_HIGH_DELTA * t
	/// pc53      rim_highlighted = true
	/// </code>
	///
	/// main.2 writes pivot_x alone; only the level transitions (main.4, main.5) write pivot_y and
	/// fold in `ExpandConst.HIERARCHY_GAP`.
	///
	/// `detail_map_type = 1` is the detail-map mode channel <see cref="Engine_UiSceneBuilder"/>
	/// consumes; mode 4 takes RGB from the detail map and alpha from the primary.
	/// </summary>
	public enum AnimationState
	{
		NormalToHighlighted = 0,
		HighlightedToNormal = 1,
		Pressed = 2,
		HighlightedToMaximised = 3,
		MaximisedToHighlighted = 4,
		MaximisedToParent = 5,
		ParentToMaximised = 6,
		RootIntro = 7,
		Reset = 8,
	}

	/// <summary>
	/// The per-item fields every animation writes — `InitAnimInfo`'s field set. A record rather
	/// than nine functions because the four constants they need are not in the shipped scripts.
	/// </summary>
	public readonly record struct AnimationInfo(
		float PercentComplete,
		float PivotX,
		float PivotY,
		float ImageOpacity,
		float OrbScale,
		float OrbTint,
		float InsideTint,
		float FrameScale,
		float HighlightOpacity,
		float TitleOpacity,
		float ChildOpacity,
		int DetailMapType,

		/// <summary>`rim_highlighted` — whether the orb wears its lit rim.</summary>
		bool RimHighlighted,

		/// <summary>`beam_opacity` — the spoke from the centre out to this orb.</summary>
		float BeamOpacity,

		/// <summary>`folder_opacity` and `arrow_opacity` — a category's own decoration and the
		/// arrow that says it opens.</summary>
		float FolderOpacity,
		float ArrowOpacity,

		/// <summary>
		/// `manage_children` — whether this animation owns its children's opacity. Set by the
		/// states that move an orb between levels; exactly one animation may claim a child.
		/// </summary>
		bool ManageChildren);
}
