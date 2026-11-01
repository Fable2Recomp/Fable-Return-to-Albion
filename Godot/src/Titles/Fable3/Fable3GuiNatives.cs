namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The UI cut's routing table for Fable 3: what Godot does with each `GUI.*` native its game VM
/// calls (the seam: Fable3/src/hooks/src/gui_natives.cpp posts, the shared core drains,
/// <see cref="Engine_UiGuiNatives"/> dispatches).
/// </summary>
/// <remarks>
/// A call arrives with the name the script used and its arguments, BEFORE the guest's own
/// implementation runs; taking it does not suppress it, so every predicate the scripts read
/// back still answers out of the guest's own state and the guest's own box still takes the pad.
///
/// Three answers, as Fable 2's table has them: <c>Register</c> (Godot draws the surface it
/// affects), <c>Observe</c> (deliberately not owned here, with the reason — the census counts
/// these apart from the unrouted), neither (the census reports it with its arguments, so the
/// table is built from what the running game calls).
///
/// Argument shapes are decompiled facts of the Fable 3 IDB (disc build), per native below. The
/// text argument of every box native is resolved by one helper, <c>sub_82516418(out, L, first)</c>:
/// the tag as given through the text manager, then with a <c>TEXT_</c> prefix, else the literal;
/// each following argument substitutes <c>%N</c>, numbers as integers (sub_82300020), strings
/// resolved the same way — the shape the counter and timer routes below resolve by.
/// </remarks>
public partial class Fable3GuiNatives : Node
{
	/// <summary>The box the message natives name — `unk_835582B8`, "Unnamed", as Fable 2's.</summary>
	private const string UnnamedBox = Engine_UiGuiBoxes.MessageBoxName;

	private readonly Engine_UiGuiNatives _router = new();

	/// <summary>The boxes, counters and menu box the natives put up; <see cref="Fable3Boxes"/> draws it.</summary>
	public Engine_UiGuiBoxes Boxes { get; } = new();

	public override void _Ready() => Route();

	public override void _Process(double delta)
	{
		_router.Drive();
		Boxes.Tick(delta);
	}

	private void Route()
	{
		// The Gameface element writes (gui_element_hooks.cpp), by presentation and path.
		Fable3Elements.Register(_router);

		// ---- THE BOXES, AS THE BOX MANAGER SHOWS AND DROPS THEM (display_box_hooks.cpp) ------
		//
		// The natives only QUEUE a box; the moment it is on screen is the box manager's: its queue
		// tick sub_822878B0 promotes the front pending box into the showing slot (queue+20) and
		// calls Show, and releases it when its Update returns 0 — lifetime, Hide or
		// RemoveDisplayBox alike. Many boxes are queued by C++ and never pass a native, so the
		// statements are taken there, with the box's class (its vtable), its resolved text and the
		// per-class fields. The guest routes the pad to the showing box itself (41/42, 73, 31/32),
		// so Godot draws and never answers.
		_router.Register("@Box.Open", BoxOpened);
		_router.Register("@Box.Close", call => BoxClosed(Parse(call.Argument(1).Text)));
		_router.Register("@Box.Value", call =>
		{
			if (Boxes.Adjusting is { } adjusting)
			{
				adjusting.Value = (int)call.Argument(1).Number;
			}
		});

		const string queued = "queues a box; it is stated when the box manager shows it (@Box.Open) and when it drops it (@Box.Close)";
		foreach (string name in new[] { "DisplayMessageBox", "DisplayYesNoQuestion", "DisplayYesNoQuestionWithTextOverride", "DisplayQuestPrimaryQuestion", "DisplayNumericAdjusterParams", "DisplayQuestBoxParams", "DisplayButtonBox", "DisplayFoodItemQueryBox", "DisplayNatalQueryBox", "DisplayDivorceMessage", "DisplayIntermissionScreen", "DisplayIntermissionScreenWithLifetime", "DisplayItemNameWithCount", "DisplayNameFromItem", "RemoveDisplayBox", "RemoveAllDisplayBoxesWithName" })
		{
			_router.Observe(name, queued);
		}

		// ---- COUNTERS AND TIMERS: the registry at `*(H+0x48)` ---------------------------------
		//
		// `SetCounter(name, tag, first, second, flag)` (0x8251AA38) files a record into the counter
		// registry (sub_825E7860(H+72, FNV1(name), tag, (int)first, (int)second, flag)); the timers
		// (`SetTimer` 0x8251AF38, `SetTimerWithText` 0x8251B158 -> sub_825E7AF8, the count-up twin
		// 0x8251B180) file timed ones; `RemoveCounter`/`RemoveTimer` are one body (0x8251B1A8). The
		// registry's tick sub_825E7C28 states them as elements: each record's `Counter%d` toggle
		// (registry 9, 0x825e7c74), the joined text laid into the `Corner` box (0x825e7cdc) and the
		// `Corner` fader (0x825e7d10) — hud.bgf's `CornerBox`, drawn by the shared HUD view (Fable3Hud).
		const string counters = "files the counter registry *(H+72); its tick writes the CornerBox elements the shared HUD view draws";
		foreach (string name in new[] { "SetCounter", "SetTimer", "SetTimerWithText", "SetCountUpTimerWithText", "PauseTimer", "PlayTimer", "RemoveCounter", "RemoveTimer" })
		{
			_router.Observe(name, counters);
		}

		_router.Observe("RemoveCounterIfFastTravelOrChangeLevel",
			"marks the counter for removal on the guest's next travel; the removal itself is the guest's");

		// ---- THE MENU BOX: `*(SM+0x34)`, stated by its own open, input and close ---------------
		//
		// sub_825FA220 opens it with the rows DisplayMenuBox filled (TextTag, Enabled), the title and
		// the cancelable flag; sub_825FA030 moves the selection on the guest's own pad handling;
		// sub_825FA420 closes it and posts MESSAGE_EVENT_MENUBOX (65) with the 1-based row or 0.
		_router.Register("@MenuBox.Open", MenuBoxOpened);
		_router.Register("@MenuBox.Select", call =>
		{
			// Re-shown with the new row: the model bumps its generation on a show, not on a cursor move.
			if (Boxes.Menu is { } menu)
			{
				Boxes.ShowMenuBox(menu.Text, menu.Options, menu.Cancelable);
				Boxes.Menu!.Highlight = (int)call.Argument(0).Number;
			}
		});
		_router.Register("@MenuBox.Close", _ => Boxes.CloseMenuBox());
		_router.Observe("DisplayMenuBox", "fills the menu box's rows; stated when it opens (@MenuBox.Open)");
		_router.Observe("DisplayUncancelableMenuBox", "fills the menu box's rows; stated when it opens (@MenuBox.Open)");

		// ---- STATED ELSEWHERE: drawn from the guest's own state, not from the call --------------
		//
		// Fable 3's info boxes are TOPBOX messages: `DisplayInfoBoxEx(name, seconds, tag, …)`
		// (0x82518168) builds sub_824B4840(msg, FNV1(name), "", seconds*1000) and posts it on
		// channel 3 of the TopBox manager `*(H+0x4C)`; `DisplayInfoBoxParams` with a non-zero
		// DisplayBoxStyle posts channel 1/2/3 by style. The manager's publisher sub_8217D880 writes
		// the box's elements (texts through the `TopBox` box layout, toggles, slide sets) — hud.bgf's
		// `TopBoxRefined`, drawn by the shared HUD view (Fable3Hud).
		const string topBox = "a TopBox channel message — its publisher writes the TopBoxRefined elements the shared HUD view draws";
		foreach (string name in new[] { "DisplayInfoBox", "DisplayInfoBoxEx", "DisplayInfoBoxParams", "ShowTopBoxMessage", "TurnOffTopBox", "ClearTopBox", "SetTopBoxHidden", "HideInfoBox" })
		{
			_router.Observe(name, topBox);
		}

		// The fader `*(GUI+0x3C)`: the natives write its target and duration (sub_825D5700) and
		// the guest polls IsScreenFully* — progress stays the guest's; Engine_UiScreenFade draws
		// it from RefiiHostGetScreenFade.
		const string fade = "the fader's own state is drawn (RefiiHostGetScreenFade); the guest polls it, so it stays the guest's";
		foreach (string name in new[] { "FadeScreenIn", "FadeScreenOut", "FadeScreenOutToColour", "FadeScreenOutIfEntityIsVisible", "IsScreenFading", "IsScreenFullyFaded", "IsScreenFullyFadedIn", "IsScreenFadeLocked", "LockScreenFade" })
		{
			_router.Observe(name, fade);
		}

		// The HUD element registries at `*(*(H+0x24)+0x18)`: the writers publish by name hash and
		// hud_feed.cpp reads them in place (texts, meters) — the same channel the subtitler and
		// the TopBox publisher write through.
		const string registry = "a HUD registry write (sub_825E4028 / sub_825E4120 / meters) read in place by hud_feed.cpp";
		foreach (string name in new[] { "SetGUIText", "ToggleElement", "SetMeterRatioOverTime", "SetMeterPercentOverTime", "SetMeterPosition", "ResetMeterPosition", "FadeElementIn", "FadeElementOutFast", "FadeElementOutSlow" })
		{
			_router.Observe(name, registry);
		}

		// Movies: the Bink hooks state the open, rect and close (Fable3/src/hooks/src/movie_player.cpp).
		foreach (string name in new[] { "PlayMovieInternal", "PlaySilentMovieInternal", "PlayLocalisedMovieInternal", "PlayMovieWithGenderedVoiceInternal", "PlayLocalisedMovieWithGenderedVoiceInternal" })
		{
			_router.Observe(name, "the Bink player's open states the movie (movie_player.cpp)");
		}

		// The loading screen: level_feed.cpp states the screen, its fade and the art it picked.
		foreach (string name in new[] { "SetLoadingScreenBackgroundName", "SetLoadingScreenTextTagGroupName", "SetOpeningLoadingScreen" })
		{
			_router.Observe(name, "the loading screen object states itself (RefiiHostGetLoadingScreen / Art)");
		}

		// ---- THE INTERACTION MODE: its table, drawn by Fable3Screens -------------------------------
		//
		// `OpenInteractionMode(table)` (0x8251D940 -> sub_825C4348(IM, 3, …, table)) and
		// `RefreshInteractionMode(table)` (0x8251DFF0) carry the button slots (`A_Label`,
		// `A_Message`, …; sub_8295B338) and `ListOfOptions` (array of {TextTag, Icon, Message};
		// sub_8295B5E8), which the hook flattens to `ListOfOptions.N.TextTag`. The cursor is the
		// mode object's +116, read live; the guest answers A/B/X/Y itself (sub_8295A178).
		_router.Register("OpenInteractionMode", call => Fable3Interaction.Opened(call.Argument(0)));
		_router.Register("RefreshInteractionMode", call => Fable3Interaction.Refreshed(call.Argument(0)));
		_router.Register("RefreshInteractionModeForClient", call => Fable3Interaction.Refreshed(call.Argument(0)));
		_router.Register("CloseInteractionMode", _ => Fable3Interaction.Closed());
		_router.Register("CloseAnyNonCrimeRelatedInteractionModes", _ => Fable3Interaction.Closed());
		_router.Observe("AddInteractionModeText",
			"joined by the guest into the mode's one text box (sub_82958878), which reaches Fable3Screens as @Element.Text on the ….TextBox.Text element");
		_router.Observe("ClearInteractionModeText", "clears the same text box (sub_82957D00); the empty @Element.Text follows");

		// ---- THE WORLD ICONS AND THE EMOTION RING: the registry R and E, read live --------------
		//
		// `AddWorldIcon` and friends only QUEUE a request on R+88; R's Update accepts it, keeps its
		// lifetime and fades it by scale, and a timed icon expires inside that Update — so
		// Fable3WorldIcons draws the live containers (RefiiHostGetWorldSprites), not the calls.
		const string iconRegistry = "a request on the icon registry R (*(H+0x18)); R's own containers are drawn live (RefiiHostGetWorldSprites)";
		foreach (string name in new[] { "AddWorldIcon", "AddWorldIconWithOffset", "RemoveWorldIcon", "SetCanShowHintWorldIcons" })
		{
			_router.Observe(name, iconRegistry);
		}

		// `SetShouldDrawPlayerIcons(b)` (0x8251C0F0) writes the byte `*(H+80)+16` (0x8251C154);
		// RefiiHostGetWorldIcons reads it as flag bit 1, which gates the player markers (52/53).
		_router.Observe("SetShouldDrawPlayerIcons", "writes *(H+80)+16, read live as RefiiHostGetWorldIcons flag bit 1");

		// `Emotion.AddEmotion(table)` (0x825229F0 -> sub_825E9088) builds a record on E's list
		// and registers its type-77 icon on R; `Emotion.Close(table)` (0x82522A28 -> sub_825E9EB0)
		// erases the record, whose deleter removes the icon. Both are drawn from E and R live
		// (RefiiHostGetFable3EmotionChoices); the table is kept for the census of the choice
		// vocabulary (Name, Text, RecordName, GameAction, Holdable, ManuallyIncreasePower).
		_router.Observe("Emotion.AddEmotion", "a record on E (*(H+0x1C)) and its icon on R, both read live (RefiiHostGetFable3EmotionChoices)");
		_router.Observe("Emotion.Close", "erases the record on E; its icon goes with it (sub_825EBD70) — the live read drops the ring");

		// ---- THE GUEST'S OWN FLAGS, polled by its scripts ------------------------------------------
		//
		// `IsDPadEnabled()` (0x8251BE10 -> sub_825D7598 on the d-pad menu `*(H+48)`: bytes +40
		// enabled, +41/+42 suppressed) and `SetDPadEnabled(b)` (0x8251BDF0: `*(H+48)+40 = b`,
		// 0x8251BE00) are the d-pad menu's own state; what it shows reaches us as the
		// `dpad.dpad.dpadmenu` element's state (@Element.State).
		_router.Observe("IsDPadEnabled", "the d-pad menu's own byte (*(H+48)+40); its visibility is stated as the dpad.dpad.dpadmenu element");
		_router.Observe("SetDPadEnabled", "writes *(H+48)+40; the d-pad menu states itself through its elements");

		// `SetJobMultiplierLevel(level)` (0x8251CD90 -> sub_82950B60 on `*(H+68)+20`): commits the
		// level text on the box of the object's text element `obj+4` (sub_82B45268 at 0x82950bec)
		// and plays slide 1 from off / 2 up / 3 down on its element `obj+0` (sub_8227DA00 at
		// 0x82950c28 / 0x82950c64 / 0x82950c9c) — `Layer.JobMultiplier` in the r7 census. Element
		// writes, drawn by the shared HUD view's `JobMultiplier` group (Fable3Hud).
		_router.Observe("SetJobMultiplierLevel", "writes the JobMultiplier element's text and slides; the shared HUD view draws them");

		// `IsCounterShown(name)` (0x8251ADE0): FNV-1 of the name against the counter registry
		// `*(H+72)` (entries +16 hash, +4 shown; 0x8251AE3C..0x8251AE70) — the registry SetCounter
		// files; the guest polls it for its own flow.
		_router.Observe("IsCounterShown", "polls the counter registry *(H+72); the answer stays the guest's");

		StateVerdicts();
	}

	// ---- EVERY OTHER NATIVE'S VERDICT (the 270 `GUI.*` registrations; SEAM.md §9) ---------------
	//
	// The seam posts BEFORE the guest's implementation and never suppresses it (gui_natives.cpp),
	// so each native's verdict is one of: owned (registered above), observed with the reason the
	// guest's own result stands, or PENDING a Godot surface — those stay out of `Observe` so the
	// census keeps reporting them as NOT ROUTED with their arguments until the surface exists.

	/// <summary>
	/// The box-manager screens: each native constructs its screen class and queues it through
	/// sub_825C8528 — ShowHeroStatusScreen stores vtable 0x820C345C inline (0x825217B0), OpenStatsBox
	/// calls the Stats ctor sub_8296D2C8, ShowPromiseScreen sub_827A3290, ShowQuestsSummary
	/// sub_829735B0, ShowKingsSchedule sub_82972D58; the Close natives drop it (sub_825C8860 /
	/// sub_825C88F0). Shown and dropped by the box manager (@Box.Open/Close `screen:*`), drawn by
	/// Fable3StatedScreen and Fable3Options.
	/// </summary>
	private static readonly string[] ScreenNatives =
	{
		"ShowHeroStatusScreen", "OpenStatsBox", "ShowPromiseScreen", "ShowQuestsSummary", "ShowKingsSchedule",
		"OpenOptionsScreen", "OpenFrontEndOptionsMenu", "CloseStatsBox", "CloseKingsSchedule",
	};

	/// <summary>Registered with no body: impl 0x82C1BA40 is `blr` (nullsub_1), or (DisplayAcceptanceBox, 0x82518350..0x82518448) parses its arguments and calls no queue.</summary>
	private static readonly string[] StubNatives =
	{
		"AddHealthBar", "RemoveHealthBar", "DisplayFastTravelScreen", "SetCutsceneVignette", "SetOlympicsLevel",
		"SetOlympicsScoreForPlayer", "SetOlympicsTime", "DisplayAcceptanceBox",
	};

	/// <summary>Value-returning natives (dispatchers 0x821BF2A8 / 0x82192EE8 / 0x822E1288 / 0x82496868 / RAW returning): the script reads the guest's answer back.</summary>
	private static readonly string[] AnsweringNatives =
	{
		"Emotion.EmotionIconsAlreadyBeingHeld", "GetDistFromLineAndPointFromVertical", "GetDyeBrightness",
		"GetEntityPlayerIsInteractingWithInInteractionMode", "GetEntityPlayerIsInteractingWithInInteractionModeIDAsString",
		"GetEntityUIDAsString", "GetLevelIDForLevel", "GetNumberOfSubObjectives", "GetPointDistFromInfiniteLine3D",
		"GetPointDistFromLine3D", "GetPointProportionAlongLine", "GetPositionOnInfiniteLine3D", "IsAnyMenuOpen",
		"IsCrimeScreenActive", "IsDisplayBoxActive", "IsEntityOnScreen", "IsEpilogueScreenVisible", "IsFullCutscenePlaying",
		"IsFullPauseMenuDisabledForHero", "IsHero", "IsHeroStatusScreenOpen", "IsInInteractionModeMenuDecoration",
		"IsJoinOptionsOpen", "IsKingsScheduleOpen", "IsLoadingScreenActive", "IsMessageBoxOpen", "IsMiniStatementBoxActive",
		"IsMoviePlaying", "IsOrbChatBoxOpen", "IsPodiumInteractionMenuOpen", "IsPromiseScreenOpen", "IsReceiveItemOverlayOpen",
		"IsSafeForWeaponLeveling", "IsSoundCategoryPlaying", "IsStatsBoxOpen", "IsUIActive", "IsVisibilityOptionsOpen",
		"TranslateMeDo",
	};

	/// <summary>Sound cues the GUI namespace carries; the guest's audio plays them.</summary>
	private static readonly string[] SoundNatives = { "PlaySound", "PlaySoundInGameLayer", "PlaySoundWithCategory", "StopSoundCategoryPlaying" };

	/// <summary>Natives whose surface Godot does not draw yet, by surface: NOT ROUTED in the census on purpose.</summary>
	private static readonly (string Surface, string[] Names)[] PendingNatives =
	{
		("emotion icon tuning (radii, tilt, particle, rumble — R/E fields not read yet)", new[] { "Emotion.AddIcon", "Emotion.GetEmotionIconNames", "Emotion.SetActualSpriteScreenSize", "Emotion.SetCanAddSimEmotionIcons", "Emotion.SetCanAddSimEmotionIconsNonNetworked", "Emotion.SetMajorRadius", "Emotion.SetManualPowerIncreaseSpeed", "Emotion.SetMinorRadius", "Emotion.SetOffset", "Emotion.SetParticle", "Emotion.SetParticleOffset", "Emotion.SetRumbleAmplitude", "Emotion.SetRumbleSpeed", "Emotion.SetSpeed", "Emotion.SetTexture", "Emotion.SetTiltXAngle", "Emotion.SetTiltYAngle", "Emotion.SetTiltZAngle" }),
		("interaction mode extras", new[] { "AddInteractionModeTextForItem", "AddInteractionModeTextForReceivedGift", "AddInteractionModeTitleAndDescription", "OpenInteractionTextWithTitleAndScrollableDescription", "RenameEntityWithInteractionMode", "SetInteractionBumpersText", "SetInteractionBumpersVisibility", "SetInteractionModeWorldIconPositions", "SetInteractionTriggersText", "SetInteractionTriggersVisibility", "ShowInteractionBumpers", "ShowInteractionTriggers", "StartInteractionMiniGame", "ExecuteInteractionScript", "OpenPodiumInteractionMenu" }),
		("HUD widgets (guild seal, dying bar, follower bar, hourglass, money, sub-objectives, panels, d-pad)", new[] { "AddGUIldDpadShortcut", "ClearGUIldDpadShortcut", "SetDPadCenter", "SetHUDSuggestionMode", "HideDyingBar", "ShowDyingBar", "HideGuildSeal", "ShowGuildSeal", "SetGuildSealCounterValue", "SetGuildSealFillPercent", "SetFollowerBarAlwaysOnInMap", "SetFollowerBarSlide", "ShowFollowerBarForXSeconds", "SetHourGlassFillState", "SetShowHourGlass", "ShowPlayerMoney", "StopShowingPlayerMoney", "StopShowing2ndPlayerMoney", "AddSubObjective", "ClearSubObjectives", "HideSubObjectives", "RemoveSubObjective", "SetSubObjectiveCompletion", "ShowSubObjectives", "HideSideInfoPanel", "SetItemForInfoPanel", "TurnJobMultiplierOff", "ShowAugmentPanel", "ShowClothesPanel", "ShowWeaponPanel", "ShowMiscGUIElements", "ShowAchievementProgess", "SetExperienceShouldShowAbilities", "SetPixelsForDyeBarToMovePerTick", "ExecuteHudScript", "Create2dParticle", "SetMiniGameCounter" }),
		("box-manager boxes and screens without a view", new[] { "DisplayCornerBoxTradeItemInfo", "RemoveCornerBoxTradeItemInfo", "DisplayDownloadMessageBox", "DisplayMiniStatementBox", "DisplaySaveGameIncompatibilityMessageBox", "DisplayReceivedItem", "DisplayReceivedMoney", "CloseReceivedItemOverlay", "ShowDogStatsBox", "ShowWeaponInfoBox", "ShowWeddingPickerBox", "ShowInfoBoxWithCurrentJoinOptions", "ShowInfoBoxWithCurrentOrbChatOptions", "ShowInfoBoxWithCurrentOrbVisibilityOptions", "ShowInfoBoxWithTitleAndDescription", "ShowSaveInfoBoxFromStatue", "ShowJoinOptions", "ShowVisibilityOptions", "OpenMultiplayerPartnersBox", "OpenOrbChatBox", "CloseOrbChatBox", "ShowFriendsChannel", "OpenSaveMenu", "ExecuteScreenScript" }),
		("world map, real estate, epilogue and judgement screens", new[] { "DisplayWorldMapScreen", "RefreshWorldMap", "ToggleWorldMap", "ToggleTownMapPrototype", "SetShowWorldMapKingdomCash", "SetWorldMapExitDirectlyToGame", "DisplayRealEstateScreen", "DisplayRealEstateScreenUnloaded", "AddItemRewardToEpilogueScreen", "SetGoldRewardInEpilogueScreen", "ShowJudgementConsequences", "ClearJudgementConsequences", "ShowCreditsScreen" }),
		("guild / gifting flags", new[] { "SetCanCouchPlayerEnterGUIld", "SetCanGiveMoney", "SetCanReceivedItemsGoToGUIld" }),
		("cutscene presentation", new[] { "PlayFullCutscene", "StopFullCutscene", "SetInteractiveCutsceneBorder", "SetInteractiveCutsceneMode", "SetMovieAsSkippable", "UnlockScreenFade" }),
		("Natal (Kinect) screens", new[] { "EnableNatalCursor", "HideNatalExpressionMaker", "HideNatalShop", "HideNatalStatueMaker", "HideNatalStocks", "SetNatalExpressionMakerRecordingText", "SetNatalExpressionMakerState", "SetNatalStatueMakerCountdownText", "SetNatalStatueMakerState", "SetNatalStocksHeldText", "SetNatalStocksInfoText", "SetNatalStocksTopText", "ShowNatalExpressionMaker", "ShowNatalShop", "ShowNatalStatueMaker", "ShowNatalStocks", "UnloadNatalGUIScreens" }),
	};

	private void StateVerdicts()
	{
		foreach (string name in ScreenNatives)
		{
			_router.Observe(name, "queues a box-manager screen (sub_825C8528) — stated when it shows (@Box.Open screen:*), drawn by Fable3StatedScreen / Fable3Options");
		}

		foreach (string name in StubNatives)
		{
			_router.Observe(name, "the guest's implementation does nothing (nullsub_1 at 0x82C1BA40, or parses and queues nothing)");
		}

		foreach (string name in AnsweringNatives)
		{
			_router.Observe(name, "answers the script from the guest's own state; the seam never suppresses, so the guest's answer stands");
		}

		foreach (string name in SoundNatives)
		{
			_router.Observe(name, "a sound cue the guest's audio plays — no surface");
		}

		int pending = 0;
		foreach ((string _, string[] names) in PendingNatives)
		{
			pending += names.Length;
		}

		GD.Print($"[ui-cut] fable3 verdicts: {ScreenNatives.Length} screen opener(s), {StubNatives.Length} stub(s), " +
			$"{AnsweringNatives.Length} answering, {SoundNatives.Length} sound cue(s) observed; {pending} PENDING a Godot surface " +
			$"(reported NOT ROUTED when called) across {PendingNatives.Length} surface(s)");
	}

	/// <summary>The boxes on screen, by the guest's box address, so a close drops the right one.</summary>
	private readonly Dictionary<uint, Engine_UiGuiBoxes.Box> _shown = new();

	/// <summary>
	/// `@Box.Open(queue, box, kind, name, text, …)`: question boxes carry their yes/no tags, the
	/// adjuster its min/max/increment/value, an info card its title, accept and help tags and the
	/// two button flags. Every box stays until `@Box.Close` — the lifetime is the guest's.
	/// </summary>
	private void BoxOpened(Engine_UiModel.GuiNativeCall call)
	{
		uint address = Parse(call.Argument(1).Text);
		string kind = call.Argument(2).Text;
		string name = call.Argument(3).Text is { Length: > 0 } stated ? stated : UnnamedBox;
		string text = call.Argument(4).Text;
		BoxClosed(address);

		Engine_UiGuiBoxes.Box box;
		switch (kind)
		{
			case "question":
				box = Boxes.Question(name, text);
				if (call.Argument(5).Text.Length > 0 || call.Argument(6).Text.Length > 0)
				{
					box.Buttons = new Engine_UiGuiBoxes.BoxButtons(true, false, false, true, false, call.Argument(5).Text, call.Argument(6).Text);
				}
				break;
			case "adjuster":
				box = Boxes.Adjuster(text, (int)call.Argument(5).Number, (int)call.Argument(6).Number,
					(int)call.Argument(7).Number, (int)call.Argument(8).Number, 0, false);
				break;
			case "quest":
				box = Boxes.QuestBox(name, text, 0, 1);
				break;
			case "card":
				box = Boxes.InfoBoxParams(name, Engine_UiGuiText.Resolve(call.Argument(5).Text, Fable3Strings.Current), -1f, text, 1,
					new Engine_UiGuiBoxes.BoxButtons(call.Argument(8).Boolean, false, false, call.Argument(9).Boolean, false,
						call.Argument(6).Text, call.Argument(7).Text));
				break;
			case "unknown":
			case string screen when screen.StartsWith("screen:", System.StringComparison.Ordinal):
				// The box-manager SCREENS: Options (`screen:options`, drawn by Fable3Options from
				// its object), hero status, stats, promises, quest summary and the king's schedule
				// (element writes under their own root, listed by Fable3Screens), and any class
				// the hooks do not name yet — the census names it for the next table entry.
				if (address != 0u && _screens.Add(address))
				{
					_screenKinds[address] = kind;
					Fable3Interaction.ScreenBoxShown(+1, Fable3Interaction.RootOf(kind));
					GD.Print($"[fable3-screens] {kind} box {address:X8} '{name}' shown — text '{text}'");
				}

				return;
			default:
				box = Boxes.MessageBoxNamed(name, text);
				break;
		}

		if (address != 0u)
		{
			_shown[address] = box;
		}
	}

	/// <summary>The screen boxes showing, by the guest's box address, and each one's kind.</summary>
	private readonly HashSet<uint> _screens = new();
	private readonly Dictionary<uint, string> _screenKinds = new();

	private void BoxClosed(uint address)
	{
		if (address != 0u && _shown.Remove(address, out Engine_UiGuiBoxes.Box? box))
		{
			Boxes.Remove(box);
		}

		if (address != 0u && _screens.Remove(address))
		{
			_screenKinds.Remove(address, out string? kind);
			Fable3Interaction.ScreenBoxShown(-1, Fable3Interaction.RootOf(kind ?? "unknown"));
		}
	}

	/// <summary>`@MenuBox.Open(title, cancelable, selected, row…)`: a row is its TextTag, prefixed `+` or `-` for enabled.</summary>
	private void MenuBoxOpened(Engine_UiModel.GuiNativeCall call)
	{
		var options = new List<Engine_UiGuiBoxes.MenuOption>();
		if (call.Arguments is { Count: > 3 } arguments)
		{
			for (int i = 3; i < arguments.Count; ++i)
			{
				string row = arguments[i].Text;
				if (row.Length == 0)
				{
					continue;
				}

				options.Add(new Engine_UiGuiBoxes.MenuOption(
					Engine_UiGuiText.Resolve(row[1..], Fable3Strings.Current), row[0] == '+'));
			}
		}

		Boxes.ShowMenuBox(Engine_UiGuiText.Resolve(call.Argument(0).Text, Fable3Strings.Current), options, call.Argument(1).Boolean);
		Boxes.Menu!.Highlight = (int)call.Argument(2).Number;
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
