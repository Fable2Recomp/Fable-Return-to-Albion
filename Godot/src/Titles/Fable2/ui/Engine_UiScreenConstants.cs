namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// `frontend.lua`, `hud/hud.lua` and `spellassigning.lua`, TRANSLATED — the per-screen scripts.
///
/// These three are the screens with logic of their own rather than a menu on them. The front end
/// numbers its pages; the HUD names the ABXY prompt's two slides; the spell assigner states the
/// paths, dimensions and slides its carousel runs on.
/// </summary>
public static class Engine_UiScreenConstants
{
	/// <summary>
	/// `frontend.lua`'s screen table — the front end's own page numbering.
	/// `frontendmainmenu.bgf` is seven pages under one `Layer`, reached as `Scene.Layer.newgame`,
	/// `.loadgame`, `.options`, `.multiplayer`, `.debug`, plus `Scene.Layer.pagenav` for the
	/// chrome. `COUNT` is 8, an end marker, not an eighth page.
	/// </summary>
	public enum FrontEndPage
	{
		PressStart = 1,
		Title = 2,
		NewGame = 3,
		LoadGame = 4,
		Option = 5,
		Multiplayer = 6,
		Debug = 7,

		/// <summary>`COUNT` — one past the last, as the script writes it.</summary>
		Count = 8,
	}

	/// <summary>The element path each page occupies, indexed by <see cref="FrontEndPage"/>.</summary>
	public static string? FrontEndPagePath(FrontEndPage page) => page switch
	{
		FrontEndPage.NewGame => "Scene.Layer.newgame",
		FrontEndPage.LoadGame => "Scene.Layer.loadgame",
		FrontEndPage.Option => "Scene.Layer.options",
		FrontEndPage.Multiplayer => "Scene.Layer.multiplayer",
		FrontEndPage.Debug => "Scene.Layer.debug",
		_ => null,
	};

	/// <summary>The page-navigation chrome, which is not a page.</summary>
	public const string FrontEndPageNavPath = "Scene.Layer.pagenav";

	/// <summary>
	/// `hud.lua` — the ABXY prompt component's two slides. Its button vocabulary is the same
	/// table as <see cref="Engine_UiGuiConstants.Button"/> and is not restated here.
	/// </summary>
	public const int AbxyShowSpellsSlide = 1;

	public const int AbxyHideSpellsSlide = 2;

	/// <summary>
	/// `spellassigning.lua` — the paths, dimensions and slides the will carousel runs on. Five
	/// rows by eight will types, the same 5×8 as the offering matrix at
	/// `spellMgr + 156/196 + 5*spell + row`.
	/// </summary>
	public const string SpellRootAdjusterPath = "parent.Group.Adjuster";

	public const string SpellAdjusterComponentPath = "parent.Group.Adjuster.Adjuster";

	public const string SpellBubbleRootPath = "parent.Group.Spell";

	/// <summary>`MAX_SPELL_LEVELS` — the five d-pad rows.</summary>
	public const int MaxSpellLevels = 5;

	/// <summary>`MAX_SPELL_TYPES` — the eight wills.</summary>
	public const int MaxSpellTypes = 8;

	/// <summary>`NUM_SCROLLER_BUBBLES` — SIX, not the five cells the carousel shows.</summary>
	public const int SpellScrollerBubbles = 6;

	/// <summary>The component's own fade slides.</summary>
	public const int SpellComponentFadeInSlide = 2;

	public const int SpellComponentFadeOutSlide = 3;

	/// <summary>Which way the carousel is sliding: static, left, right.</summary>
	public const int SpellSlideStatic = 1;

	public const int SpellSlideLeft = 2;

	public const int SpellSlideRight = 3;

	/// <summary>A row's own fade, and the bubble's active/inactive state.</summary>
	public const int SpellRowNoFadeSlide = 1;

	public const int SpellRowFadeInSlide = 2;

	public const int SpellRowFadeOutSlide = 3;

	public const int SpellBubbleActiveSlide = 3;

	public const int SpellBubbleInactiveSlide = 1;

	/// <summary>The script's own cursor origin — both 1-based, like every index in these
	/// scripts.</summary>
	public const int SpellFirstRow = 1;

	public const int SpellFirstColumn = 1;

	/// <summary>
	/// The eight wills, in the guest's own index order: icon stem then Babel name tag. From the
	/// binary — `sub_826B5D30` and `sub_826B50B8` unroll them as literal string tables, no
	/// data-driven path. The icon path is `Art\GUI\WheelIcons\Abilities\&lt;stem&gt;.tex`, built
	/// from a literal prefix and suffix as the guest builds it. Eight matches
	/// `spellassigning.lua`'s `MAX_SPELL_TYPES`.
	/// </summary>
	public static readonly (string Icon, string Tag)[] Wills =
	{
		("will_shock", "GUI_SCREEN_ABILITIES_WILL_LIGHTNING_NAME_LEVEL_06"),
		("will_blast", "GUI_SCREEN_ABILITIES_WILL_FIREBALL_NAME_LEVEL_06"),
		("icon_will_time_control", "GUI_SCREEN_ABILITIES_WILL_SLOWTIME_NAME_LEVEL_06"),
		("will_sword_of_damocles", "GUI_SCREEN_ABILITIES_WILL_SWORDS_NAME_LEVEL_06"),
		("will_vortex", "GUI_SCREEN_ABILITIES_WILL_VORTEX_NAME_LEVEL_06"),
		("will_chaos", "GUI_SCREEN_ABILITIES_WILL_CHAOS_NAME_LEVEL_06"),
		("will_force_push", "GUI_SCREEN_ABILITIES_WILL_FORCE_PUSH_NAME_LEVEL_06"),
		("will_dead_rising", "GUI_SCREEN_ABILITIES_WILL_DEAD_RISING_NAME_LEVEL_06"),
	};

	/// <summary>The folder the will icons live in.</summary>
	public const string WillIconFolder = "art\\gui\\wheelicons\\abilities\\";
}
