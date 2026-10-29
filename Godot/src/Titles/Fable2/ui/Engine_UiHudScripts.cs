using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Constants and rules from the seven `scripts/gameface/hud/*` Lua files that the rebuilt HUD
/// (<see cref="Engine_HudView"/>, <see cref="Engine_HudValues"/>) has to agree with.
/// `hud.lua`'s `State` block (PlayerMaxHealth=200 etc.) is demo data and is not carried.
/// `widescreenadapter.lua`'s zero-means-unstated rule lives in <see cref="Engine_UiWidescreen.Stated"/>.
/// </summary>
public static class Engine_UiHudScripts
{
	// hud.lua: g_HUD.Const.

	public const int ButtonTypeUp = 1;
	public const int ButtonTypeDown = 2;
	public const int ButtonTypeLeft = 3;
	public const int ButtonTypeRight = 4;
	public const int ButtonTypeA = 11;
	public const int ButtonTypeB = 12;
	public const int ButtonTypeX = 13;
	public const int ButtonTypeY = 14;

	/// <summary>The ABXY component's four slides.</summary>
	public const int AbxyShowSpellsSlide = 1;
	public const int AbxyHideSpellsSlide = 2;
	public const int AbxyShowButtonsSlide = 3;
	public const int AbxyHideButtonsSlide = 4;

	/// <summary>
	/// Glyphs `onInitialize` parks at opacity 0 (`hud.dis main.0 pc16..46`). Only the four face
	/// buttons are fetched (`pc0..8`), so the d-pad glyphs are never touched.
	/// </summary>
	public static readonly string[] ParkedButtons =
	{
		"AButton", "BButton", "XButton", "YButton",
	};

	/// <summary>
	/// `HUDRemoveTargetSpeakIcon` clears `AButton` (`hud.dis main.1 pc0..5`); speak and
	/// interact clear the same glyph. `BButton`/`YButton` are cleared by nothing.
	/// </summary>
	public const string SpeakIconElement = "AButton";

	/// <summary>`HUDRemoveTargetAttackIcon` clears `XButton` (`hud.dis main.2 pc0..5`).</summary>
	public const string AttackIconElement = "XButton";

	/// <summary>`HUDRemoveTargetInteractIcon` clears `AButton` (`hud.dis main.3 pc0..5`).</summary>
	public const string InteractIconElement = "AButton";

	/// <summary>`State.SafeFrame = getElement('SafeFrame', SCENE)` (`hud.dis main.0 pc11..15`); no script reads it back.</summary>
	public const string SafeFrameElement = "SafeFrame";

	/// <summary>
	/// `onInitialize` sets `active = false` on every child of `Scene.Layer.WorldIcons`
	/// (`hud.dis main.0 pc47..63`); the sixteen `WorldIconN` are also authored at y −800.
	/// </summary>
	public const string WorldIconPoolElement = "Scene.Layer.WorldIcons";

	/// <summary>The pool starts inactive; see <see cref="WorldIconPoolElement"/>.</summary>
	public const bool WorldIconPoolStartsInactive = true;

	/// <summary>
	/// The three `hudpositioningandscaling` fixups run only under `GUI.State.IsWidescreen`
	/// (`hud.dis main.0 pc64..77`); the gate belongs at <see cref="Engine_UiWidescreen.ApplyNamedFixups"/>'s caller.
	/// </summary>
	public const bool WidescreenFixupsAreGated = true;

	/// <summary>`Enable`/`Disable`: one opacity write on `Scene.Layer`.</summary>
	public const string WholeHudElement = "Scene.Layer";

	// hudquarterbackeventbuttons.lua: the quick-menu transition is a scene slide.

	/// <summary>`GameToQuickMenu` — `goToSlide(SCENE, 4)`.</summary>
	public const int SceneSlideQuickMenu = 4;

	/// <summary>`QuickMenuToGame` — `goToSlide(SCENE, 3)`.</summary>
	public const int SceneSlideGame = 3;

	// hudpositioningandscaling.lua.

	/// <summary>`GetWidescreenOffset` — a flat 145 canvas units.</summary>
	public const float WidescreenOffset = 145f;

	/// <summary>`GetScaleRatio` — 0.75, the 4:3 to 16:9 width ratio.</summary>
	public const float WidescreenScaleRatio = 0.75f;

	/// <summary>`ScaleScreenshotForWidescreen` writes exactly this scale.x.</summary>
	public const float WidescreenScreenshotScaleX = 12.8f;

	/// <summary>
	/// `RepositionPivot(element, x)` (`hudpositioningandscaling.dis main.2 pc0..7`): `pivot_x`
	/// is zeroed before it is read, so the result is `x`. No callers.
	/// </summary>
	public static float RepositionPivotX(float x) => x;

	// minimap.lua.

	/// <summary>The minimap's initial slide (`minimap.dis main pc8`).</summary>
	public const int MinimapInitialSlide = 1;

	/// <summary>`AK.fetchObjects(g_Minimap, 'Component')` (`minimap.dis main pc10..14`); nothing scripted reads it.</summary>
	public const string MinimapFetchedObject = "Component";

	/// <summary>
	/// `screencentertoggle.lua` setCenterOn/setCenterOff, latched. `centerXPos` is read off the
	/// marker (`CenterToggle [Behavior]` ord=371, `main.1 pc13..16`); position is read and
	/// written on its parent (`dpad [Component]` ord=369, `pc8..11`, `pc17..21`).
	/// </summary>
	public struct CenterToggle
	{
		private bool _isCenter;
		private float _previousX;

		/// <summary>True when the caller should move the parent to <paramref name="centerX"/>.</summary>
		public bool SetOn(float currentParentX, float centerX, out float moveTo)
		{
			moveTo = centerX;
			if (_isCenter)
			{
				return false;
			}

			_isCenter = true;
			_previousX = currentParentX;
			return true;
		}

		/// <summary>True when the caller should restore the parent to the remembered x.</summary>
		public bool SetOff(out float moveTo)
		{
			moveTo = _previousX;
			if (!_isCenter)
			{
				return false;
			}

			_isCenter = false;
			return true;
		}
	}

	/// <summary>
	/// `worldicons.lua` `SetSlide`: `String1` names the icon, `Float1` the slide (`worldicons.dis
	/// main.0 pc0..14`). It resolves against `WorldIconsGroup`, absent from `hud.bgf` (the pool
	/// parent is `WorldIcons`, ord=176), so shipped icons never change slide.
	/// </summary>
	public static (string IconName, int Slide) WorldIconSlide(string string1, float float1) =>
		(string1, (int)float1);
}
