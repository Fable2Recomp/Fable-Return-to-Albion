using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The UI model seam: the game installs a guest-backed source, the editor an authored one.
/// Every read has an absent answer (null / empty / 0); every lever returns the tick pump's
/// queue depth, never an outcome.
/// </summary>
public abstract class Engine_UiSource
{
	/// <summary>The source this session reads. Defaults to the authored stand-in; the game installs the guest-backed source once at startup.</summary>
	public static Engine_UiSource Current { get; set; } = new Engine_UiAuthoredSource();

	/// <summary>True when backed by a live guest. For reports only, never for logic.</summary>
	public virtual bool IsLive => false;

	// ---- THE SCREENS AND THEIR ELEMENTS ------------------------------------------------

	/// <summary>Every live presentation: address, root, name, state, active element count.</summary>
	public virtual List<Engine_UiModel.Presentation> GetPresentations() => new();

	/// <summary>One presentation's element array, or null when there is nothing to read.</summary>
	public virtual List<Engine_UiModel.UiElement>? GetUiElements(uint presentation = 0) => null;

	/// <summary>Element count without reading any element; how <see cref="Engine_UiElementState"/> notices a reloaded screen.</summary>
	public virtual uint GetUiElementCount(uint presentation = 0) => 0;

	/// <summary>The per-element texture hashes, or null. Positional against the element array.</summary>
	public virtual uint[]? GetUiTextureHashes(uint presentation, int elementCount) => null;

	/// <summary>A rectangle of live attributes — rows are elements, columns the ids asked for.</summary>
	public virtual Engine_UiModel.LiveAttribute[,]? GetUiAttributes(
		uint[] ids, uint presentation = 0) => null;

	/// <summary>The screen fade the guest is driving.</summary>
	public virtual Engine_UiModel.ScreenFade GetScreenFade() => default;

	/// <summary>Which in-game screens the guest considers current.</summary>
	public virtual Engine_UiModel.InGameScreens? GetInGameScreens() => null;

	// ---- THE MENUS ---------------------------------------------------------------------

	/// <summary>The spawned rows' identities, with the generation that dates them.</summary>
	public virtual List<Engine_UiModel.MenuRow> GetMenuRows(out ulong generation)
	{
		generation = 0;
		return new List<Engine_UiModel.MenuRow>();
	}

	/// <summary>
	/// The screen the captured rows belong to, or <see cref="Engine_UiMenuSources.AnyScreen"/>
	/// when no in-game screen states one. The capture is a single list; this is what says whose
	/// population it is. See <see cref="Engine_UiMenuSources.BoundScreen"/>.
	/// </summary>
	public virtual uint MenuScreenId() => Engine_UiMenuSources.AnyScreen;

	/// <summary>The Lua-opened option box, or null when none is open.</summary>
	public virtual Engine_UiModel.MenuBoxState? GetMenuBox() => null;

	/// <summary>The pause gate: underlying screen id, pending modal id and mode, current tab.</summary>
	public virtual Engine_UiModel.PauseState? GetPauseState() => null;

	/// <summary>The pause-root minimap's quest markers.</summary>
	public virtual List<Engine_UiModel.MapMarker> GetMapMarkers() =>
		new List<Engine_UiModel.MapMarker>();

	/// <summary>The spell-assignment blade's model.</summary>
	public virtual Engine_UiModel.SpellAssignState GetSpellAssign() => default;

	// ---- THE HUD -----------------------------------------------------------------------

	/// <summary>Meter values by FNV-1 name hash.</summary>
	public virtual Dictionary<uint, float> GetHudMeters() => new();

	/// <summary>The HUD's pulsers by `FNV1(PulserName)` — loop phase 0..1. See the bridge.</summary>
	public virtual Dictionary<uint, float> GetHudPulsers() => new();

	/// <summary>Text values by FNV-1 name hash.</summary>
	public virtual Dictionary<uint, string> GetHudTexts() => new();


	/// <summary>The Lua-owned text channel — the other of the HUD's two.</summary>
	public virtual Dictionary<uint, uint> GetHudScriptTexts() => new();

	/// <summary>The Lua-owned meter channel.</summary>
	public virtual Dictionary<uint, float> GetHudScriptMeters() => new();

	// ---- THE FRONT END'S OWN STATE -----------------------------------------------------

	/// <summary>Signed-in, saved-game count and the guest's own cap.</summary>
	public virtual Engine_UiModel.SaveState GetSaveState() => default;

	/// <summary>The installed languages, and which one is current.</summary>
	public virtual List<Engine_UiModel.Language> GetLanguages() =>
		new List<Engine_UiModel.Language>();

	/// <summary>The guest's achievement writes newer than <paramref name="afterSequence"/>, oldest first.</summary>
	public virtual Engine_UiModel.AchievementUnlock[] GetAchievementUnlocks(
		ulong afterSequence, int max = 16) =>
		System.Array.Empty<Engine_UiModel.AchievementUnlock>();

	/// <summary>The title's XDBF resource, or null before the module has launched.</summary>
	public virtual byte[]? GetTitleXdbf() => null;

	/// <summary>States the unlocked set (ids with unlock FILETIMEs) for the guest's own enumerator.</summary>
	public virtual void SetAchievementUnlocks(uint[] ids, ulong[] unlockFiletimes)
	{
	}

	/// <summary>How many times the guest has called `XamShowAchievementsUI` this session.</summary>
	public virtual uint GetAchievementsUiRequests() => 0;

	// ---- THE UI CUT --------------------------------------------------------------------

	/// <summary>The `GUI.*` native calls (name plus arguments) made since the last drain; an authored source posts the call a script would have made. See docs/ui_native_cut.md.</summary>
	public virtual System.Collections.Generic.List<Engine_UiModel.GuiNativeCall>
		TakeGuiNativeCalls() => new();

	/// <summary>Intercepted, pending and registration-resolved `GUI` name counts. <c>Named</c> is 0 until the guest's `RegisterGUI` has run.</summary>
	public virtual (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters() =>
		(0UL, 0u, 0u);

	/// <summary>True while a menu population is in flight; consumers hold their last whole population while set. See <see cref="GetMenuRows"/>.</summary>
	public virtual bool MenuPopulating() => false;

	/// <summary>The `g_Menu:` mutations the game's C++ has made since the last drain, with their staged attributes. See docs/ui_native_cut.md.</summary>
	public virtual System.Collections.Generic.List<Engine_UiModel.MenuChunk> TakeMenuChunks() =>
		new();

	/// <summary>Every element state change since the last drain, taken at the writers; the presentation's own dirty list is cleared and refilled during the guest frame and cannot be read from another thread.</summary>
	public virtual System.Collections.Generic.List<Engine_UiModel.UiElementChange>
		TakeUiElementChanges() => new();

	/// <summary>Recorded, pending and dropped element changes. This is a change log, not a statement queue: a non-zero <c>Dropped</c> means re-walk the elements.</summary>
	public virtual (ulong Recorded, uint Pending, ulong Dropped) UiElementCounters() =>
		(0ul, 0u, 0ul);

	// ---- THE LEVERS --------------------------------------------------------------------
	// Each returns what the bridge returns (queue depth or accepted flag), never an outcome.

	/// <summary>Asks for a front-end action by name.</summary>
	public virtual uint PostFrontEndAction(string name) => 0;

	/// <summary>
	/// Answers the guest's menu box: 0 cancels, 1..N pick that option. Runs the guest's commit
	/// on its thread — `sub_823DC720` (choose) / `sub_823DC2B0` (cancel) — which posts message
	/// event type 34, ExtraData = option index (0 = dismissed).
	/// </summary>
	public uint MenuBoxAnswer(int answer) =>
		PostFrontEndAction($"MenuBoxAnswer:{System.Math.Max(0, answer)}");

	/// <summary>Moves the guest's menu-box highlight to option 1..N without committing.</summary>
	public uint MenuBoxSelect(int option) =>
		PostFrontEndAction($"MenuBoxSelect:{System.Math.Max(0, option)}");

	/// <summary>One action code to whichever display box is up.</summary>
	public enum BoxAction
	{
		/// <summary>Step the numeric adjuster's value up.</summary>
		Up = 28,

		/// <summary>Step it down.</summary>
		Down = 29,

		/// <summary>Dismiss — what the quest box and the message box answer to.</summary>
		Dismiss = 36,

		/// <summary>Confirm. Yes on a query box, accept on the adjuster.</summary>
		Confirm = 46,

		/// <summary>Cancel. No on a query box.</summary>
		Cancel = 47,
	}

	/// <summary>
	/// Runs the guest's own box handler, which posts message event 36 (1/0 for yes/no) or
	/// 37/38 (the adjuster's number). Handlers gate on the box's open byte, so a late action
	/// is a no-op.
	/// </summary>
	public uint DisplayBoxAction(BoxAction action) =>
		PostFrontEndAction($"BoxAction:{(int)action}");

	/// <summary>Answers one `GUI` predicate instead of the guest. Only for a native whose setters this host owns; otherwise the answer is a guess.</summary>
	public virtual void SetPredicate(string name, bool value)
	{
	}

	/// <summary>Answers a float-returning predicate.</summary>
	public virtual void SetPredicate(string name, float value)
	{
	}

	/// <summary>
	/// Stops the guest running a native this host owns outright. Suppress only audited natives —
	/// some carry side effects (display box suspends gameplay, the minigame tally opens a screen).
	/// </summary>
	public virtual void SuppressNative(string name)
	{
	}

	/// <summary>One action code to the current in-game screen's own input method.</summary>
	public virtual uint ScreenAction(uint code, bool atRoot = false) => 0;

	/// <summary>Pushes a menu button event by identity.</summary>
	public virtual uint MenuEvent(string name, int eventType, uint id) => 0;

	/// <summary>Runs the shipped <c>SelectMenuItem</c> against the guest's own highlight.</summary>
	public virtual uint MenuSelect() => 0;

	/// <summary>Holds or releases the menu stick in one direction.</summary>
	public virtual uint MenuScroll(Engine_UiModel.MenuStick direction, bool held) => 0;

	/// <summary>Selects a pause tab. False when the source refused it.</summary>
	public virtual bool PauseTabSelect(uint tab) => false;

	/// <summary>Commits one spell into one slot on the assignment blade.</summary>
	public virtual uint AssignSpell(int slot, int spell) => 0;
}
