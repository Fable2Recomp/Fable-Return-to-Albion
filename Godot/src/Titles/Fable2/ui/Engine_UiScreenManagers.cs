using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The abilities screen's right-hand page — `managers/abilitiesmanager.lua` (the build
/// `guisetup.dis main pc172..180` dofiles; the top-level copy never loads).
///
/// `main.0` binds `Image`, `Name`, `Description`, `DescriptionComponent`. The visibility test
/// `main.2 pc21..26` reads `g_Menu.Const.MENU_ITEM_TYPE_MENU_BUTTON`, a key nothing ever writes
/// (the item types live in `g_Menu.ItemConst`), so the shipped comparison is
/// `nil == highlighted.MenuItemType`: the page shows for an item stating no type and parks for
/// one stating any.
///
/// The highlighted child is read from `ChildMenuItems` (`main.2 pc7`), not `ChildItems`.
///
/// `SetName`/`SetDescription` run their argument through `GUI:GetText`, so `String1`/`String2`
/// are Babel tags; `SetGraphic` takes `Float1`/`Float2` as the quad's UV origin.
/// </summary>
public sealed class Engine_UiAbilitiesManager
{
	/// <summary>The slide the description component shows details on.</summary>
	public const int DetailSlide = 1;

	/// <summary>The slide the description component parks on with nothing to describe.</summary>
	public const int ParkedSlide = 2;

	private Node3D? _image;
	private MeshInstance3D? _name;
	private MeshInstance3D? _description;
	private Node3D? _descriptionComponent;

	/// <summary>The theme the text is re-meshed through and tags are resolved against.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>The slide the description component is currently on.</summary>
	public int CurrentSlide { get; private set; } = DetailSlide;

	/// <summary>Whether the component's timeline has advanced — `getTimeContext().time ~= 0`.</summary>
	public bool TimelineRunning { get; set; }

	/// <summary>Whether the component's timeline is paused — `getTimeContext().paused`.</summary>
	public bool TimelinePaused { get; set; } = true;

	public bool IsBound => _descriptionComponent is not null;

	/// <summary>
	/// The four one-name `AK.fetchObjects` calls of
	/// `managers/abilitiesmanager.dis main.0 pc3..26`: `Image`, `Name`, `Description`,
	/// `DescriptionComponent`.
	/// </summary>
	public bool Bind(Node3D component)
	{
		_image = Engine_UiTheme.FindByName(component, "Image") as Node3D;
		_name = Engine_UiTheme.FindByName(component, "Name") as MeshInstance3D;
		_description = Engine_UiTheme.FindByName(component, "Description") as MeshInstance3D;
		_descriptionComponent =
			Engine_UiTheme.FindByName(component, "DescriptionComponent") as Node3D;
		return IsBound;
	}

	/// <summary>`onUpdate` — `main.1` is three instructions:
	/// `A0_0:UpdateDescriptionPageVisibility()`.</summary>
	public bool OnUpdate(
		bool wheelNodePresent, bool highlightedItemPresent, int? highlightedItemType) =>
		UpdateDescriptionPageVisibility(
			wheelNodePresent, highlightedItemPresent, highlightedItemType);

	/// <summary>
	/// `UpdateDescriptionPageVisibility` (`main.2`) — show the description for a highlighted
	/// item whose `MenuItemType` is nil, park it for one that states a type.
	///
	/// <paramref name="highlightedItemPresent"/> is the truth of
	/// `g_Menu.CurrWheelNode.ChildMenuItems[g_Menu.CurrWheelNode.HighlightedIdx]` (guard at
	/// `pc15`); <paramref name="highlightedItemType"/> is that child's stated `MenuItemType`
	/// (compared at `pc25`) — two different reads. True when the slide changed or the timeline
	/// started.
	/// </summary>
	public bool UpdateDescriptionPageVisibility(
		bool wheelNodePresent, bool highlightedItemPresent, int? highlightedItemType)
	{
		// `pc0..4`: `if nil == g_Menu.CurrWheelNode then return end`
		if (!IsBound || !wheelNodePresent)
		{
			return false;
		}

		// `pc12..14`: `A0_0.State.DescriptionComponent.slide` — read once, before the guard.
		int slide = CurrentSlide;

		// `pc15..16`: the highlighted child itself is the guard.
		if (!highlightedItemPresent)
		{
			return false;
		}

		// `pc21..26`: `g_Menu.Const.MENU_ITEM_TYPE_MENU_BUTTON` is nil, so this is
		// `nil == L1_1.MenuItemType`.
		if (highlightedItemType is null)
		{
			bool changed = false;

			// `pc27..34`: the slide-2 arm ends `JMP -> 54`, so the time check is the else.
			if (slide == ParkedSlide)
			{
				// `GUI_ComponentSetSlide(A0_0.State.DescriptionComponent, (1))`
				CurrentSlide = DetailSlide;
				changed = true;
			}
			else if (!TimelineRunning && TimelinePaused)
			{
				// `pc35..42`: `if getTimeContext().time == 0 then if paused then play()`
				TimelinePaused = false;
				changed = true;
			}

			return changed;
		}

		// `pc44..53`: `elseif 1 == L2_2 then if time ~= 0 then setSlide((2))` — park once moved.
		if (slide == DetailSlide && TimelineRunning)
		{
			CurrentSlide = ParkedSlide;
			return true;
		}

		return false;
	}

	/// <summary>`SetName` (`main.3`) — `setAttribute(Name, "textstring", GUI:GetText(tag))`.</summary>
	public void SetName(string tag) => SetTag(_name, tag);

	/// <summary>`SetDescription` (`main.4`) — a tag again, through `GUI:GetText`.</summary>
	public void SetDescription(string tag) => SetTag(_description, tag);

	/// <summary>
	/// `SetGraphic(u, v)` (`main.5 pc0..13`) — `State.Image` guards its own element, then
	/// `getChildByIndex(1)` twice and `positionu`/`positionv` on the grandchild.
	/// </summary>
	public void SetGraphic(float u, float v)
	{
		SetGrandchildUv(_image, u, v);
	}

	/// <summary>
	/// `UpdateDescriptionPage` (`main.6`) — the staged element's `String1`/`String2` tags and
	/// `Float1`/`Float2` UVs, in that order.
	/// </summary>
	public void UpdateDescriptionPage(string nameTag, string descriptionTag, float u, float v)
	{
		SetName(nameTag);
		SetDescription(descriptionTag);
		SetGraphic(u, v);
	}

	private void SetTag(MeshInstance3D? node, string tag)
	{
		if (node is null || !GodotObject.IsInstanceValid(node) || Theme is null)
		{
			return;
		}

		Theme.SetMeshText(node, Theme.Resolve(tag));
	}

	/// <summary>
	/// `graphic:getChildByIndex(1):getChildByIndex(1).positionu/v = u/v` — the first child's
	/// first child. `positionu`/`positionv` are the quad's UV origin, i.e. `uv1_offset` in
	/// `ui_quad.gdshader`, which the builder writes inset by half a texel.
	/// </summary>
	internal static void SetGrandchildUv(Node3D? graphic, float u, float v)
	{
		if (graphic is null || !GodotObject.IsInstanceValid(graphic) ||
			graphic.GetChildCount() == 0 ||
			graphic.GetChild(0) is not Node3D child || child.GetChildCount() == 0 ||
			child.GetChild(0) is not MeshInstance3D mesh ||
			mesh.MaterialOverride is not ShaderMaterial material)
		{
			return;
		}

		material.SetShaderParameter("uv1_offset", new Vector2(u, v));
	}

	public string Describe() =>
		$"abilities manager {(IsBound ? "bound" : "NOT bound")}, slide {CurrentSlide}" +
		(_image is null ? ", no image" : string.Empty) +
		(_name is null ? ", no name" : string.Empty) +
		(_description is null ? ", no description" : string.Empty);
}

/// <summary>
/// The quests screen and its interstitial pages — `managers/questsmanager.lua` (the build
/// `guisetup.dis main pc154..162` dofiles; the top-level copy never loads).
///
/// Four scene slides drive it: 1 full details (started / completed / failed), 2 the summary page
/// with the quest wheel, 3 and 4 the close animations out of each. `Display*` states the slide;
/// `HandleA`/`HandleB`/`HandleExit` move between them and push the navigation events C++ acts on
/// (`"ReturnToGame"`, `"ReturnToPauseMenu"`).
///
/// Element names, `main pc6..10`: `FullDetailsTitle`, `FullDetailsDescription`, `SumaryTitle`,
/// `SumaryDescription`, `ObjectivesAndBoasts` — the misspelling is the game's.
///
/// The title colour is the verdict: started and completed paint `GetPageTitleColor`, failed
/// paints `GetNegativeTextColor` (`main.9 pc9`).
///
/// Shipped quirks kept: the display title is
/// `GUI:GetText(GUI:GetText(tag1) .. " - " .. GUI:GetText(tag2))`, the outer call running over an
/// already-resolved string; and `SetGraphic` addresses an `IconGraphic` field the fetch never
/// stores, so it is unreachable.
/// </summary>
public sealed class Engine_UiQuestsManager
{
	/// <summary>Slide 1 — the full-details page.</summary>
	public const int SlideFullDetails = 1;

	/// <summary>Slide 2 — the summary page with the quest wheel.</summary>
	public const int SlideSummary = 2;

	/// <summary>Slide 3 — closing out of full details.</summary>
	public const int SlideClosedFromDetails = 3;

	/// <summary>Slide 4 — closing out of the summary.</summary>
	public const int SlideClosedFromSummary = 4;

	/// <summary>The button-legend icons the displays push — the guest's own art paths.</summary>
	public const string IconForward = "ABXY\\icon_forward";

	public const string IconBackward = "ABXY\\icon_backward";

	/// <summary>The three verdict tags the full-details title leads with.</summary>
	public const string NewQuestTitleTag = "TEXT_NEW_QUEST_TITLE";

	public const string CompletedQuestTitleTag = "TEXT_COMPLETED_A_QUEST_TITLE";

	public const string FailedQuestTitleTag = "TEXT_FAILED_A_QUEST_TITLE";

	/// <summary>The summary page's screen-name tag — `main.10 pc10`.</summary>
	public const string QuestsScreenTag = "GUI_QUESTS_SCREEN";

	private MeshInstance3D? _fullDetailsTitle;
	private MeshInstance3D? _fullDetailsDescription;
	private MeshInstance3D? _summaryTitle;
	private MeshInstance3D? _summaryDescription;
	private MeshInstance3D? _objectivesAndBoasts;

	/// <summary>
	/// `IconGraphic` — never fetched (`main pc6..10` states five names and this is not one), so
	/// `SetGraphic`'s guard always fails and this stays null.
	/// </summary>
	private readonly Node3D? _iconGraphic = null;

	/// <summary>The theme the text is re-meshed through and tags are resolved against.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>The GUI event queue the navigation and button-legend pushes land in.</summary>
	public Engine_UiGuiEventQueue? Events { get; set; }

	/// <summary>`WheelGraphHandleB()` — ascend one level of the quest wheel (`main.12 pc27`).</summary>
	public System.Action? WheelGraphHandleB { get; set; }

	/// <summary>`GUI.FLAG_WheelGraphIsAtRoot` — GUI state the host mirrors in. Initialised
	/// FALSE: `guistateandconstants.dis main pc219` writes `False`.</summary>
	public bool WheelGraphIsAtRoot { get; set; }

	/// <summary>`Presentation.scene.slide`. Zero until a display states one.</summary>
	public int SceneSlide { get; private set; }

	public bool IsBound => _fullDetailsTitle is not null || _summaryTitle is not null;

	/// <summary>
	/// The five names `AK.fetchObjects` is given at `main pc6..10`, spelled as shipped (`Sumary`
	/// included). No fallbacks: a document without these names does not bind.
	/// </summary>
	public bool Bind(Node3D component)
	{
		_fullDetailsTitle =
			Engine_UiTheme.FindByName(component, "FullDetailsTitle") as MeshInstance3D;
		_fullDetailsDescription =
			Engine_UiTheme.FindByName(component, "FullDetailsDescription") as MeshInstance3D;
		_summaryTitle = Engine_UiTheme.FindByName(component, "SumaryTitle") as MeshInstance3D;
		_summaryDescription =
			Engine_UiTheme.FindByName(component, "SumaryDescription") as MeshInstance3D;
		_objectivesAndBoasts =
			Engine_UiTheme.FindByName(component, "ObjectivesAndBoasts") as MeshInstance3D;
		return IsBound;
	}

	/// <summary>`onUpdate` — `main.0` is one instruction, `RETURN`.</summary>
	public void OnUpdate()
	{
	}

	/// <summary>`SetSumaryTitle` — a RESOLVED string; callers run `GUI:GetText` first.</summary>
	public void SetSumaryTitle(string display) => SetText(_summaryTitle, display);

	/// <summary>`SetFullDetailsTitle` — resolved.</summary>
	public void SetFullDetailsTitle(string display) => SetText(_fullDetailsTitle, display);

	/// <summary>`SetObjectivesAndBoasts` — `String2` verbatim, already display text.</summary>
	public void SetObjectivesAndBoasts(string display) => SetText(_objectivesAndBoasts, display);

	/// <summary>`SetFullDetailsDescription` — `String3` verbatim.</summary>
	public void SetFullDetailsDescription(string display) =>
		SetText(_fullDetailsDescription, display);

	/// <summary>`SetSumaryDescription` — `String2` verbatim.</summary>
	public void SetSumaryDescription(string display) => SetText(_summaryDescription, display);

	/// <summary>
	/// `SetGraphic(u, v)` (`main.6`) — unreachable as shipped: `pc0..3` guards on
	/// `g_QuestsManager.IconGraphic`, a field the fetch never stores.
	/// </summary>
	public void SetGraphic(float u, float v) =>
		Engine_UiAbilitiesManager.SetGrandchildUv(_iconGraphic, u, v);

	/// <summary>
	/// `DisplayQuestStart` (`main.7`) — slide 1, page-title colour, and the composed title
	/// `GetText(GetText("TEXT_NEW_QUEST_TITLE") .. " - " .. GetText(String1))`.
	/// Legend: A removed, B backward, X and Y removed.
	/// </summary>
	public void DisplayQuestStart(string titleTag, string objectivesAndBoasts, string description) =>
		DisplayFullDetails(
			NewQuestTitleTag, Engine_UiThemeText.PageTitle,
			titleTag, objectivesAndBoasts, description);

	/// <summary>
	/// `DisplayQuestCompleted` (`main.8`) — as started, with `TEXT_COMPLETED_A_QUEST_TITLE`.
	/// </summary>
	public void DisplayQuestCompleted(
		string titleTag, string objectivesAndBoasts, string description) =>
		DisplayFullDetails(
			CompletedQuestTitleTag, Engine_UiThemeText.PageTitle,
			titleTag, objectivesAndBoasts, description);

	/// <summary>
	/// `DisplayQuestFailed` (`main.9`) — the one that paints the title `GetNegativeTextColor`.
	/// </summary>
	public void DisplayQuestFailed(string titleTag, string objectivesAndBoasts, string description) =>
		DisplayFullDetails(
			FailedQuestTitleTag, Engine_UiThemeText.Negative,
			titleTag, objectivesAndBoasts, description);

	/// <summary>
	/// `DisplayAllQuests` (`main.10`) — slide 2, the summary page; no wheel-graph reset in this
	/// build. The `GUI_QUESTS_SCREEN` write lands before the colour and before `SetSumaryTitle`
	/// overwrites it with `GetText(String1)`, so it is transient; shipped order is kept. Legend:
	/// A forward, B backward, X and Y removed.
	/// </summary>
	public void DisplayAllQuests(string titleTag, string summaryDescription)
	{
		SceneSlide = SlideSummary;

		// `pc4..12` textstring, then `pc13..33` the colour channels, then `pc34..44` the title.
		SetSumaryTitle(Resolve(QuestsScreenTag));
		SetTitleColour(_summaryTitle, Engine_UiThemeText.PageTitle);
		SetSumaryTitle(Resolve(titleTag));
		SetSumaryDescription(summaryDescription);
		Events?.PushSetButtonEvent(
			IconForward, (int)Engine_UiGuiConstants.Button.A, 0);
		Events?.PushSetButtonEvent(
			IconBackward, (int)Engine_UiGuiConstants.Button.B, 0);
		RemoveButton(Engine_UiGuiConstants.Button.X);
		RemoveButton(Engine_UiGuiConstants.Button.Y);
	}

	/// <summary>
	/// `HandleA` (`main.11`) — on the full-details page A returns to the game and closes.
	/// `pc19..21` is an empty `2 == slide` arm: on the summary page A does nothing.
	/// </summary>
	public void HandleA()
	{
		if (SceneSlide == SlideFullDetails)
		{
			// `GUI:PushButtonEvent("ReturnToGame", GUI.Const.EVENT_TYPE_NAVIGATION, 0, 0)`
			Events?.PushButtonEvent(
				"ReturnToGame", Engine_UiGuiConstants.EventNavigation, 0, 0);
			SceneSlide = SlideClosedFromDetails;
		}
	}

	/// <summary>
	/// `HandleB` (`main.12 pc9..28`) — nothing on full details; on the summary page push
	/// `"ReturnToPauseMenu"` and go to slide 4 only when `GUI.FLAG_WheelGraphIsAtRoot`, otherwise
	/// `WheelGraphHandleB()`.
	/// </summary>
	public void HandleB()
	{
		if (SceneSlide != SlideSummary)
		{
			return;
		}

		if (WheelGraphIsAtRoot)
		{
			Events?.PushButtonEvent(
				"ReturnToPauseMenu", Engine_UiGuiConstants.EventNavigation, 0, 0);
			SceneSlide = SlideClosedFromSummary;
		}
		else
		{
			WheelGraphHandleB?.Invoke();
		}
	}

	/// <summary>
	/// `HandleEnter` — registered at `main pc56..57` and inert: `main.13` is six instructions,
	/// one empty `if 2 == getCurrentSlide(SCENE)` block and the return.
	/// </summary>
	public void HandleEnter()
	{
	}

	/// <summary>`HandleExit` (`main.14`) — close whichever page is up: slide 1 → 3, slide 2 → 4.</summary>
	public void HandleExit()
	{
		if (SceneSlide == SlideFullDetails)
		{
			SceneSlide = SlideClosedFromDetails;
		}
		else if (SceneSlide == SlideSummary)
		{
			SceneSlide = SlideClosedFromSummary;
		}
	}

	/// <summary>The staged `String1..3` as `QuarterbackOpen` leaves them; later steps read what
	/// earlier steps staged, so the carry-over is part of the behaviour.</summary>
	private string _stagedString1 = string.Empty;
	private string _stagedString2 = string.Empty;
	private string _stagedString3 = string.Empty;

	/// <summary>The test driver's step counter — `main.15`'s `up[0]`.</summary>
	private int _quarterbackStep;

	/// <summary>
	/// `QuarterbackOpen` (`main.15`) — shipped test scaffolding, registered as the screen's
	/// `"Open"` handler. Each Open steps a counter through placeholder quests; step 1 re-displays
	/// step 0's staged strings, step 3 keeps step 2's String3, step 4 exits and rewinds the
	/// counter to −1 so the post-increment restarts the cycle.
	/// </summary>
	public void QuarterbackOpen()
	{
		if (_quarterbackStep == 0)
		{
			_stagedString1 = "Quest title.";
			_stagedString2 = "This is the boast and objectives.";
			_stagedString3 = "This is the description.";
			DisplayQuestStart(_stagedString1, _stagedString2, _stagedString3);
		}
		else if (_quarterbackStep == 1)
		{
			DisplayQuestCompleted(_stagedString1, _stagedString2, _stagedString3);
		}
		else if (_quarterbackStep == 2)
		{
			_stagedString1 = "Fail quest";
			_stagedString2 = "This is the new boast and objectives.";
			_stagedString3 = "This is the new description.";
			DisplayQuestFailed(_stagedString1, _stagedString2, _stagedString3);
		}
		else if (_quarterbackStep == 3)
		{
			_stagedString1 = "Sumary quest";
			_stagedString2 = "This is the sumary description.";
			DisplayAllQuests(_stagedString1, _stagedString2);
		}
		else if (_quarterbackStep == 4)
		{
			HandleExit();
			_quarterbackStep = -1;
		}

		_quarterbackStep += 1;
	}

	/// <summary>The three verdict displays differ only in tag and colour — the shared body.</summary>
	private void DisplayFullDetails(
		string verdictTag, Color titleColour,
		string titleTag, string objectivesAndBoasts, string description)
	{
		// `goToSlide(SCENE, (1))`
		SceneSlide = SlideFullDetails;
		SetTitleColour(_fullDetailsTitle, titleColour);

		// `GUI:GetText((GUI:GetText(verdict) .. " - " .. GUI:GetText(String1)))` — the outer
		// GetText runs over the joined display string.
		SetFullDetailsTitle(Resolve(Resolve(verdictTag) + " - " + Resolve(titleTag)));
		SetObjectivesAndBoasts(objectivesAndBoasts);
		SetFullDetailsDescription(description);
		RemoveButton(Engine_UiGuiConstants.Button.A);
		Events?.PushSetButtonEvent(
			IconBackward, (int)Engine_UiGuiConstants.Button.B, 0);
		RemoveButton(Engine_UiGuiConstants.Button.X);
		RemoveButton(Engine_UiGuiConstants.Button.Y);
	}

	/// <summary>
	/// `GUI:PushRemoveButtonEvent(button)` — the shipped push writes name `"RemoveButton"` and
	/// the button id into `Param1Low`; see `guieventqueue.lua`.
	/// </summary>
	private void RemoveButton(Engine_UiGuiConstants.Button button) =>
		Events?.PushRemoveButtonEvent("RemoveButton", (int)button, 0);

	/// <summary>
	/// The `setAttribute(title, "textcolor.r/g/b", ...)` triple, stated as the mesh's
	/// `text_colour` metadata, which is where <see cref="Engine_UiTheme.SetMeshText"/> reads it.
	/// </summary>
	private static void SetTitleColour(MeshInstance3D? node, Color colour)
	{
		if (node is null || !GodotObject.IsInstanceValid(node))
		{
			return;
		}

		node.SetMeta("text_colour", colour);
	}

	private void SetText(MeshInstance3D? node, string display)
	{
		// `if g_QuestsManager.SumaryTitle then` — each setter guards its own element.
		if (node is null || !GodotObject.IsInstanceValid(node) || Theme is null)
		{
			return;
		}

		Theme.SetMeshText(node, display);
	}

	private string Resolve(string tag) => Theme is null ? tag : Theme.Resolve(tag);

	public string Describe() =>
		$"quests manager {(IsBound ? "bound" : "NOT bound")}, scene slide {SceneSlide}" +
		(_objectivesAndBoasts is null ? ", no objectives" : string.Empty) +
		$", quarterback step {_quarterbackStep}";
}

/// <summary>
/// The shop screen's right-hand page — `managers/shopscreenmanager.lua` (the build
/// `guisetup.dis main pc181..189` dofiles). That build reads the row's type as an element
/// attribute, `getAttribute(L1_1.element, "MenuItemType")` (`main.1 pc23..29`), compared against
/// `BUY_INVENTORY_ITEM` 30 / `SELL_INVENTORY_ITEM` 31
/// (`menu/menustateandconstants.dis main pc47/50`).
///
/// Five objects — `Name`, `Price`, `IconGraphic`, `Description`, `DescriptionComponent` — and the
/// abilities manager's show/park slide machine, keyed on the highlighted row's `MenuItemType`.
///
/// `SetName`/`SetDescription` resolve through `GUI:GetText` (`String1`/`String2` are tags);
/// `SetPrice` writes `String3` verbatim — the price is finished display text with its currency
/// mark.
/// </summary>
public sealed class Engine_UiShopScreenManager
{
	/// <summary>The slide the description component shows details on.</summary>
	public const int DetailSlide = 1;

	/// <summary>The slide the description component parks on.</summary>
	public const int ParkedSlide = 2;

	/// <summary>`g_Menu.ItemConst.BUY_INVENTORY_ITEM` — 30.</summary>
	public const int BuyItemType = (int)Engine_UiWheelConstants.ItemType.BuyInventoryItem;

	/// <summary>`g_Menu.ItemConst.SELL_INVENTORY_ITEM` — 31.</summary>
	public const int SellItemType = (int)Engine_UiWheelConstants.ItemType.SellInventoryItem;

	private MeshInstance3D? _name;
	private MeshInstance3D? _price;
	private Node3D? _iconGraphic;
	private MeshInstance3D? _description;
	private Node3D? _descriptionComponent;

	/// <summary>The theme the text is re-meshed through and tags are resolved against.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>The slide the description component is currently on.</summary>
	public int CurrentSlide { get; private set; } = DetailSlide;

	/// <summary>Whether the component's timeline has advanced — `getTime(...) ~= 0`.</summary>
	public bool TimelineRunning { get; set; }

	/// <summary>Whether the component's timeline is paused.</summary>
	public bool TimelinePaused { get; set; } = true;

	public bool IsBound => _descriptionComponent is not null;

	/// <summary>`AK.fetchObjects(State, "Name", "Price", "IconGraphic", "Description",
	/// "DescriptionComponent")` — `main pc15..19`.</summary>
	public bool Bind(Node3D component)
	{
		_name = Engine_UiTheme.FindByName(component, "Name") as MeshInstance3D;
		_price = Engine_UiTheme.FindByName(component, "Price") as MeshInstance3D;
		_iconGraphic = Engine_UiTheme.FindByName(component, "IconGraphic") as Node3D;
		_description = Engine_UiTheme.FindByName(component, "Description") as MeshInstance3D;
		_descriptionComponent =
			Engine_UiTheme.FindByName(component, "DescriptionComponent") as Node3D;
		return IsBound;
	}

	/// <summary>
	/// `onUpdate` — `main.0` calls `UpdateDescriptionPageVisibility()` with no gate; the
	/// `GUI.State.TilReadyToUpdate` gate belongs to the top-level build, which never loads.
	/// </summary>
	public bool OnUpdate(
		bool currentItemPresent, bool highlightedRowPresent, int? highlightedItemType) =>
		UpdateDescriptionPageVisibility(
			currentItemPresent, highlightedRowPresent, highlightedItemType);

	/// <summary>
	/// `UpdateDescriptionPageVisibility` (`main.1`) — show the description while a buy or sell
	/// row is highlighted, park it once its timeline has moved otherwise.
	///
	/// <paramref name="highlightedRowPresent"/> is the truth of
	/// `g_Menu.CurrentItem.ChildItems[g_Menu.CurrentItem.HighlightedIdx]` (guard at `pc16`);
	/// <paramref name="highlightedItemType"/> is `getAttribute(that row.element, "MenuItemType")`
	/// (compared at `pc30`/`pc35`) — two different reads. True when the slide changed or the
	/// timeline started.
	/// </summary>
	public bool UpdateDescriptionPageVisibility(
		bool currentItemPresent, bool highlightedRowPresent, int? highlightedItemType)
	{
		// `pc0..3`: `if nil ~= g_Menu.CurrentItem then`.
		if (!IsBound || !currentItemPresent)
		{
			return false;
		}

		// `pc11..15`: `getCurrentSlide(State.DescriptionComponent)` — read once, before the guard.
		int slide = CurrentSlide;

		if (!highlightedRowPresent)
		{
			return false;
		}

		if (highlightedItemType == BuyItemType || highlightedItemType == SellItemType)
		{
			bool changed = false;

			// `pc37..45`: the slide-2 arm ends `JMP -> 66`, so the time check is the else.
			if (slide == ParkedSlide)
			{
				// `goToSlide(DescriptionComponent, (1))`
				CurrentSlide = DetailSlide;
				changed = true;
			}
			else if (!TimelineRunning && TimelinePaused)
			{
				// `pc46..54`: `if getTime(...) == 0 then if paused then play(...)`
				TimelinePaused = false;
				changed = true;
			}

			return changed;
		}

		// `pc56..65`: `if 1 == slide then if getTime(...) ~= 0 then goToSlide(..., (2))`
		if (slide == DetailSlide && TimelineRunning)
		{
			CurrentSlide = ParkedSlide;
			return true;
		}

		return false;
	}

	/// <summary>`SetPrice` (`main.2`) — `String3` verbatim, a finished display string.</summary>
	public void SetPrice(string display) => SetText(_price, display);

	/// <summary>`SetName` (`main.3`) — a tag, through `GUI:GetText`.</summary>
	public void SetName(string tag) => SetText(_name, Resolve(tag));

	/// <summary>`SetDescription` (`main.4`) — a tag again.</summary>
	public void SetDescription(string tag) => SetText(_description, Resolve(tag));

	/// <summary>`SetGraphic(u, v)` (`main.5`) — `positionu`/`positionv` on `IconGraphic`'s first
	/// child's first child, the same write as the abilities manager's.</summary>
	public void SetGraphic(float u, float v) =>
		Engine_UiAbilitiesManager.SetGrandchildUv(_iconGraphic, u, v);

	/// <summary>
	/// `UpdateDescriptionPage` (`main.6`) — `String1` name tag, `String2` description tag,
	/// `String3` price display string, `Float1`/`Float2` icon UVs.
	/// </summary>
	public void UpdateDescriptionPage(
		string nameTag, string descriptionTag, string priceDisplay, float u, float v)
	{
		SetName(nameTag);
		SetDescription(descriptionTag);
		SetPrice(priceDisplay);
		SetGraphic(u, v);
	}

	private void SetText(MeshInstance3D? node, string display)
	{
		// `if g_ShopScreenManager.State.Price then` — each setter guards its own element.
		if (node is null || !GodotObject.IsInstanceValid(node) || Theme is null)
		{
			return;
		}

		Theme.SetMeshText(node, display);
	}

	private string Resolve(string tag) => Theme is null ? tag : Theme.Resolve(tag);

	public string Describe() =>
		$"shop manager {(IsBound ? "bound" : "NOT bound")}, slide {CurrentSlide}" +
		(_name is null ? ", no name" : string.Empty) +
		(_price is null ? ", no price" : string.Empty) +
		(_description is null ? ", no description" : string.Empty) +
		(_iconGraphic is null ? ", no icon" : string.Empty);
}

/// <summary>
/// The crime screen's charge sheet — `managers/crimescreenmanager.lua` (the build
/// `guisetup.dis main pc163..171` dofiles).
///
/// No slides, timeline, predicate or `onUpdate`: `main pc22..30` registers three setters.
/// `main pc3..10` fetches `PageTitle`, `CrimeDescription`, `YourMoney` and `ChargedWith`.
///
/// Two of the four elements are never written: `PageTitle` has no setter, and `YourMoney`'s
/// setter misses (see <see cref="SetMoneyText"/>). Both are still bound.
///
/// No `GetText` anywhere — all values arrive as finished display strings.
/// </summary>
public sealed class Engine_UiCrimeScreenManager
{
	private MeshInstance3D? _pageTitle;
	private MeshInstance3D? _crimeDescription;
	private MeshInstance3D? _yourMoney;
	private MeshInstance3D? _chargedWith;

	/// <summary>
	/// `MoneyText` — the field `SetMoneyText` writes through, which no fetch stores, so it is nil
	/// on every call.
	/// </summary>
	private readonly MeshInstance3D? _moneyText = null;

	/// <summary>The theme the text is re-meshed through.</summary>
	public Engine_UiTheme? Theme { get; set; }

	public bool IsBound => _chargedWith is not null;

	/// <summary>
	/// `AK.fetchObjects(g_CrimeScreenManager, "PageTitle", "CrimeDescription", "YourMoney",
	/// "ChargedWith")` — `main pc3..10`.
	/// </summary>
	public bool Bind(Node3D component)
	{
		_pageTitle = Engine_UiTheme.FindByName(component, "PageTitle") as MeshInstance3D;
		_crimeDescription =
			Engine_UiTheme.FindByName(component, "CrimeDescription") as MeshInstance3D;
		_yourMoney = Engine_UiTheme.FindByName(component, "YourMoney") as MeshInstance3D;
		_chargedWith = Engine_UiTheme.FindByName(component, "ChargedWith") as MeshInstance3D;
		return IsBound;
	}

	/// <summary>`SetChargedWithText` (`main.0`) — `String1` verbatim onto
	/// `State.ChargedWithText`, which is the `ChargedWith` element.</summary>
	public void SetChargedWithText(string display) => SetText(_chargedWith, display);

	/// <summary>
	/// `SetMoneyText` (`main.1 pc0..9`) — dead as shipped, and inert here: it writes through
	/// `g_CrimeScreenManager['MoneyText']` (`pc2`), a field no fetch stores (the fetch spells it
	/// `YourMoney`), so `setAttribute` is handed nil on every call.
	/// </summary>
	public void SetMoneyText(string display) => SetText(_moneyText, display);

	/// <summary>`SetCrimeDescriptionText` (`main.2`) — `String1` verbatim onto
	/// `CrimeDescription`.</summary>
	public void SetCrimeDescriptionText(string display) => SetText(_crimeDescription, display);

	private void SetText(MeshInstance3D? node, string display)
	{
		if (node is null || !GodotObject.IsInstanceValid(node) || Theme is null)
		{
			return;
		}

		Theme.SetMeshText(node, display);
	}

	public string Describe() =>
		$"crime manager {(IsBound ? "bound" : "NOT bound")}" +
		(_pageTitle is null ? ", no page title" : ", page title present (never driven — as shipped)") +
		(_crimeDescription is null ? ", no description" : string.Empty) +
		(_yourMoney is null ? ", no money" : ", money present (setter misses it — as shipped)");
}
