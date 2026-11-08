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
