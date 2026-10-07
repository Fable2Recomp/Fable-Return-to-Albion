// refii — the guest tick pump: a place for the host to run work ON THE GUEST'S OWN TICK THREAD.
// The host never calls the guest cross-thread; it posts, and the guest picks the work up at a
// point in its own tick where its state is coherent.
//
// The site: `sub_822C1BD0`, a seven-instruction thunk forwarding through `[this+0x24]`, reached
// only through slot 5 (offset 0x14) of the vtable on `dword_8349E9EC`, loaded at exactly three
// places — one per tick loop:
//
//     0x82377B68  in sub_82377A00   boot movies, 60 Hz
//     0x82319E88  in sub_82319BE8   front end,   30 Hz  (dbl_83319630)
//     0x82277190  in sub_82276D10   in game,     15 Hz  (dbl_83319668)
//
// Its only xref is data (`0x8200282C`, the vtable slot), so those three are the complete
// reachable set: exactly once per tick, in every state, and nowhere else.
//
// The thread is not the process's initial one. `sub_822EA1C0` spawns the worker and then parks:
//
//     822ea1f4  addi r3, r10, sub_822EA228@l
//     822ea1f8  bl   beginthread
//     822ea1fc  li   r4, -1
//     822ea204  bl   sub_821969C8          ; WaitForSingleObjectEx(h, INFINITE, 0)
//
// so the tick lives on the spawned "GameThread", pinned to hardware thread 0.
//
// This fires during the boot movies, before the front-end state exists (`dword_83496AC0` is
// written only when it is constructed), so every pointer read here is checked and an action
// that cannot run yet stays queued. Actions are named strings, matched in `RunPendingActions`;
// an unknown action is dropped and logged.

#include "core/hero_derived.h"
#include "vfs_hooks_private.h"

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cmath>
#include <cstring>
#include <map>
#include <mutex>
#ifndef _WIN32
#include <signal.h>
#endif
#include <optional>
#include <string>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>

#include "core/guest_state.h"
#include "gui/gui_display_box.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

// The New Game commit. NOT hooked — the guest's own function is called, so this is the plain
// symbol rather than an `__imp__` original.
PPC_EXTERN_FUNC(rex_lhGui_FrontendRoot_HandleGuiEvent);

// The front end's own menu-action dispatcher, vtable slot +16 of 0x820D0934. Called directly:
// the slot is fixed.
PPC_EXTERN_FUNC(rex_lhGui_FrontendRoot_MenuActionDispatch);
// The front end's two dialog owners' input slots (vtable +12) — DispatchFrontEndDialog.
PPC_EXTERN_FUNC(rex_lhGui_FrontendRoot_OnScreenAction);
PPC_EXTERN_FUNC(rex_lhGui_FrontendTitle_OnScreenAction);

// "How many saved games does the signed-in user have?" — the probe the front end uses:
//
//   sub_82443308  no parameters, no device, no overlapped structure. Enumerates content type 1
//                 for user `dword_83496AF8` with DeviceID 0 — ALL devices — and returns the
//                 item count. Closes its handle unconditionally.
//   sub_824431E8  same enumeration, but reads a DeviceID out of the save manager's map at
//                 `saveMgr+0x7EC`, and returns 0 when that map has no entry.
//   sub_82443048  same again, and fills a vector of save NAMES.
//
// The last two answer 0 until `sub_82441CB8` has written that map, so only `sub_82443308` is
// usable from a host poll.
// The storage device picker, and the only writer of the save manager's device map:
//
//   82441d88  j_XamShowDeviceSelectorUI(userIndex, 1, 0, size, &deviceId, ov)
//   82441d98  XGetOverlappedResult(ov, 0, 1)        ; bWait=1 — IT BLOCKS
//   82441dac  slot = sub_8244A0C8(saveMgr + 2028, &userIndex)   ; 2028 = 0x7EC
//   82441dbc  *slot = deviceId
//   82441dcc  return deviceId != 0
//
// `saveMgr + 0x7EC` is measured from the FIRST ARGUMENT, which the guest builds at 0x826E008C
// as `*(*(0x83496AC0 + 0xC) + 0x28)`.
//
// Safe to block here: our XamShowDeviceSelectorUI returns X_ERROR_IO_PENDING and completes on
// the kernel dispatch thread via CompleteOverlappedDeferred. Costs 100ms, once, at New Game.
PPC_EXTERN_FUNC(rex_lhSaveManager_SelectStorageDevice);

PPC_EXTERN_FUNC(rex_lhSaveManager_CountSavedGamesAllDevices);

// Frontend_SetStartGameTarget — the LOAD commit, which commits by itself:
//
//   823798ac  sub_822651A0(&g_PendingLoadSaveName, a2)   ; a2 = the save NAME
//   8237989c  sub_822651A0(&g_StartLevelName,      a3)   ; a3 = the LEVEL to load
//   823798e0  *(*(dword_83496AC0 + 12) + 92) = 1         ; the run loop's exit flag
//   823798e8  sub_82319928(*(dword_83496AC0 + 12))       ; builds the next game state
//
// Both arguments are handle SLOTS, not strings: `sub_822651A0` copies a 4-byte handle and bumps
// the refcount at `*handle + 12`, so it must be handed the ADDRESS of a live handle. `a1` is
// never referenced. It dereferences `dword_83496AC0 + 12` with no null check, so it is only
// safe while the front end is up.
PPC_EXTERN_FUNC(rex_lhFrontEnd_SetStartGameTarget);

// The screen-id -> handler map lookup, `*(GUI+28)` and a screen id in, handler object out.
// A find on the red-black tree at `guiOwner+68` (miss returns 0); registration is the
// guiOwner ctor `sub_8236D078`, which maps screen id 0 to the pause main menu handler
// (ctor `sub_826A1818`, vtable 0x820CDE18).
PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);
PPC_EXTERN_FUNC(rex_lhGuiPresentation_GetRootElement);  // the presentation's scene-root element
PPC_EXTERN_FUNC(rex_lhGuiElement_GoToSlide);  // goToSlide(element, presentation, index)

// The pause handler's event method — vtable slot +20 of 0x820CDE18, `(this, event)`.
//
// The event is three words: `+0` a pointer to a string body whose own `+0` is a `char*`
// (dereferenced as `**(const char ***)a2`), `+4` the type, `+8` the param. Dispatch of the
// Saving group is PURELY {type == 2, param}: the name is only strstr/strcmpi'd against the
// tab names ("Map" and "Henchman" by SUBSTRING, Items/Weapons/Clothing/Abilities/Logbook/
// Options/Kick exact) and must match none of them; it is never retained. param 0 opens query
// box 23 (save and continue), 1 box 24 (save and quit), 2 box 25 (quit without saving) — or,
// when `sub_82448900` says there is no unsaved progress, param 2 QUITS IMMEDIATELY with no
// box at all. The opener `sub_8236FA68` requires the modal latch idle (`guiOwner+40 == 1`)
// and parks the query id at `guiOwner+44`; the screen id stays 0 while the box is up, and
// the guest's own input path answers it (action codes 46 confirm / 47 cancel).
PPC_EXTERN_FUNC(rex_lhGuiPauseRootScreen_OnMenuEvent);

// The ReceiveItemsScreen handler's SCREEN-INPUT method — vtable slot +24 of 0x820C0E08,
// `(this, code, held)`. The non-modal input route (vtable+28 is the modal one): code 36 (A)
// closes the card done, 39 (Y) flips it, 75 closes. The handler is the screen map's entry for
// id 8 (`sub_8236FFC0(guiOwner, 8)`).
PPC_EXTERN_FUNC(rex_lhGuiReceiveItemsScreen_OnScreenAction);

// The MenuBox accept — `sub_823DC720(ctl)`, ctl = *(guiOwner+56). Reads the selected index
// at ctl+0, checks the item's enabled byte, invokes the callback, and CLOSES/publishes
// itself (player message 35/101). Refuses a disabled item.
PPC_EXTERN_FUNC(rex_lhMenuBox_Accept);

// The four display-box input handlers — slot 3 of each box class's vtable. Named statically
// because the recompiler emits no indirect-call path; the discriminator is the box's own
// vtable, i.e. its class identity:
//
//   off_820AE688  QueryBox      (YesNo, PubGameYesNo, QuestPrimaryQuestion)  -> sub_823CF3B0
//   off_820AE6A8  NumericAdjuster                                            -> sub_823CF8A8
//   off_820AE6C8  QuestBoxParams                                             -> sub_823D0090
//   off_820AE668  MessageBox    (also Download and Divorce)                  -> sub_823CEDD8
//
// Two classes are deliberately absent: the AcceptanceBox (`off_820EBF3C`) has `li r3,0; blr` in
// both input slots (its interaction lives in the Gameface Lua behind
// `g_DisplayBox:OpenAsAcceptanceBox()`), and the ButtonBox (`off_820028F4`) is a no-op at slot 3
// and takes input at slot 4 with a different argument shape (an input-phase record).
PPC_EXTERN_FUNC(rex_lhDisplayQueryBox_HandleAction);
PPC_EXTERN_FUNC(rex_lhDisplayNumericAdjuster_HandleAction);
PPC_EXTERN_FUNC(rex_lhDisplayQuestBox_HandleAction);
PPC_EXTERN_FUNC(rex_lhDisplayMessageBox_HandleAction);

// The MenuBox close — `sub_823DC2B0(ctl)`. Publishes the result (value = ctl+13 ? 0 :
// selIdx+1) and tears the box down. Used for the host's cancel.
PPC_EXTERN_FUNC(rex_lhMenuBox_Close);

// The MenuBox selector — `sub_823DBB48(ctl, index)`: old selection to slide 1, new to slide 2,
// writes ctl+0. Keeps the guest's drawn selection in step with the host highlight.
PPC_EXTERN_FUNC(rex_lhMenuBox_SetSelection);

// The guest's directional stick router — held (0x1C..0x1F) and released (0x20..0x23). Both read
// the action code through their second argument (`switch (*a2)`) and never touch the first, so
// the host can drive them with a scratch word. They set the presentation attribute `String1` to
// "Up"/"Down"/"Left"/"Right" and run `"GUI:SetDirectionToHeld()"` /
// `"GUI:SetDirectionToReleased()"` on the context `sub_823DCBE0` picks; Lua polls that state in
// `g_Menu:onUpdate` and calls `ScrollUp` itself.
PPC_EXTERN_FUNC(rex_lhGuiInput_OnDirectionHeld);
PPC_EXTERN_FUNC(rex_lhGuiInput_OnDirectionReleased);

// The vtable+24 screen-input methods, per screen id. Named statically because the recompiler
// emits no indirect-call path; each is checked against its screen id (from the guest's `mgr+4`)
// before use.
PPC_EXTERN_FUNC(rex_lhGuiAbilitiesScreen_OnScreenAction);  // 14 abilities
PPC_EXTERN_FUNC(rex_lhGuiItemsScreen_OnScreenAction);  // 15 items/weapons
PPC_EXTERN_FUNC(rex_lhGuiClothingScreen_OnScreenAction);  // 16 clothing
PPC_EXTERN_FUNC(rex_lhGuiLogbookScreen_OnScreenAction);  // 17 logbook
PPC_EXTERN_FUNC(rex_lhGuiOptionsScreen_OnScreenAction);  // 18 options
PPC_EXTERN_FUNC(rex_lhGuiCrimeScreen_OnScreenAction);  // 21 crime
PPC_EXTERN_FUNC(rex_lhGuiShopScreen_OnScreenAction);  // 22 shop
PPC_EXTERN_FUNC(rex_lhGuiWorldMapScreen_OnScreenAction);  // 23 world map
PPC_EXTERN_FUNC(rex_lhGuiGiftGivingScreen_OnScreenAction);  // 25 gift
PPC_EXTERN_FUNC(rex_lhGuiSelectionScreen_OnScreenAction);  // 26 selection
PPC_EXTERN_FUNC(rex_lhGuiDecorationScreen_OnScreenAction);  // 27 decoration
PPC_EXTERN_FUNC(rex_lhGuiQueryScreen_OnScreenAction);  // 10 query screen (37 close, 38 rename)
PPC_EXTERN_FUNC(rex_lhGuiRealEstateScreen_OnScreenAction);  // 28 real estate (`*(0x820CEA58+24)`, read from the image)
PPC_EXTERN_FUNC(rex_lhGuiRealEstateScreen_OnMenuEvent);  // 28 real estate (`*(0x820CEA58+20)`, read from the image)
PPC_EXTERN_FUNC(rex_lhGuiPresentation_SetStringParam);  // presentation string parameter setter (String1/String2)
PPC_EXTERN_FUNC(rex_lhGuiManager_CanPlayerOpenQuickMenu);  // expression wheel: may open? (hero alive, mode 1/5/20/21/22)
PPC_EXTERN_FUNC(rex_lhGuiManager_OpenExpressionWheel);  // expression wheel: open (GameAction 40's body)
PPC_EXTERN_FUNC(rex_lhGuiManager_CloseExpressionWheel);  // expression wheel: close (GameAction 41's body)
PPC_EXTERN_FUNC(rex_lhGuiManager_OpenSpellsMenu);  // spells menu: open (GameAction 42's body)
PPC_EXTERN_FUNC(rex_lhGuiManager_CloseSpellsMenu);  // spells menu: close (GameAction 43's body)
PPC_EXTERN_FUNC(rex_lhGuiWillAssignWidget_OnScreenAction);  // will-assignment widget input: 26 stick, 28..31, 36 copy-down
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_EndHeldExpression);  // expression wheel: end a held expression (GameAction 25)
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_Accept);  // expression wheel: accept (36) — enter a group / start the item
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_Back);  // expression wheel: back (37) — leave the group, or close at the top
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_SetCursorFromStick);  // expression wheel: cursor from a stick vector (x, y)
PPC_EXTERN_FUNC(rex_lhGui_RegisterPlayer);  // GUI RegisterPlayer(GUI, player): ++GUI+0x65, binds the action consumer
PPC_EXTERN_FUNC(rex_lhGuiMinigameCompletedScreen_HandleAction);  // 11 minigame completed (6)
PPC_EXTERN_FUNC(rex_lhGuiQuestCompletionScreen_HandleAction);  // 12 quest completion (6, player 1)

// The GUI input router — `(guiOwner, actionCode, pad)`. Its guard admits input while a
// query box is pending (`guiOwner+44` in {23,24,25,28}) and routes to the CURRENT screen
// handler's vtable+28; for the pause family that is `sub_826A2950`, which reloads +44 for
// both its switch and the callee (the pad argument is discarded) and answers in
// `sub_826A7738`: code 46 = confirm (save/quit per the box), 47 = cancel. The kick box
// (id 28) instead answers the generic accept/back codes 36/37 inline. Call the ROUTER, not the
// handler method, so the guest's own gate stays in the path.
PPC_EXTERN_FUNC(rex_lhGuiManager_RouteModalInput);

// The spell assignment writer — `(spellManager, 0, slot, spellIndex, notify)`:
//
//   82541f58  stbx r10, r3, r31   ; byte[mgr + 128 + slot] = spell
//   82541f60  stb  r10, 5(r11)    ; byte[mgr + 133 + slot] = spell   (the mirror)
//   82541f7c  sub_822D25F0(*(mgr+4))    ; local player? then fire the UI/net event
//
// It validates nothing about the spell, so the caller must only send a pair the offering matrix
// admits. Used in preference to both wrappers: `sub_823B96E0` has only data references, so the
// recompiler may not emit it, and `sub_82541168` goes through `sub_82173EB0`, which CREATES the
// `CECSpellManager` component when it is absent.
PPC_EXTERN_FUNC(rex_CECSpellManager_SetSpellSlot);
PPC_EXTERN_FUNC(rex_lhWorldMap_BuildQuestList);  // the world map's filtered, sorted quest list — see DispatchQuestSelect
PPC_EXTERN_FUNC(rex_lhWorldMap_BuildJobList);  // the same list keeping only Type 5 — the JOBS
PPC_EXTERN_FUNC(rex_lhWorldMap_BuildSalesList);  // the world map's SALES rows (44 bytes: +0 NameTagHash, +16 multiplier, +24 RecordId)
PPC_EXTERN_FUNC(rex_lhWorldMapSaleVector_Destroy);  // frees that sales vector
PPC_EXTERN_FUNC(rex_lhWorld_ComputeWorldLevelFnv1Key);  // World_ComputeWorldLevelFnv1Key(world, "Albion", &levelName)
PPC_EXTERN_FUNC(rex_lhWorld_LookupScenarioNameByLevelKey);  // World_LookupLevelNameByKey(world, key) -> &scenario handle
PPC_EXTERN_FUNC(rex_lhPodVector_FreeStorage);  // frees that list, as sub_826C6940 does at 0x826c6a4c

// The Lua string runner, and the two LHString primitives that feed it.
//
// `sub_82426470(LHString* source, presentation)` either executes immediately — when
// `byte[GUI+100]` is set and the presentation's vtable+20 says it may — or queues the chunk at
// `GUI+88` for the next update. `sub_8222CED0(LHString* dest, const char* literal)` constructs
// one from a NUL-terminated guest string; `sub_82214DD8` releases it.
//
// It is silent on every failure path — a null context, a wrong context and a nil method are
// indistinguishable no-ops — so nothing is sent here without a read-back.
PPC_EXTERN_FUNC(rex_lhGui_RunLuaString);
PPC_EXTERN_FUNC(rex_lhCharString_CtorFromCStr);

// `sub_824E2F28(CECStats*, abilityId, rank, forceReplicate)` — the rank writer the purchase
// wrappers call once they have charged experience and raised their box. It CLAMPS for us
// (`sub_824E2D40` gives the ceiling, 0x824E2F48) and stores at 0x824E3240; `a4` forces the
// co-op replication at 0x824E39A4. Going straight to it skips the cost and the message box.
PPC_EXTERN_FUNC(rex_CECStats_SetAbilityLevel);

// `sub_824E0B40(CECStats*)` — the appearance flush. EVERY BRANCH IS PRESENCE-GUARDED, so it is
// safe on a body that lacks any of them: component 116 gets `+16 = 1` and a repack, component 15
// takes the two invalidation stamps at `+100`/`+104`, component 21 gets `sub_8241FEA8`.
PPC_EXTERN_FUNC(rex_CECStats_FlushAppearance);

// `sub_825B5F30(comp17, recordId)` — the face: resolves the id, matches it against the def's
// `CharacterCreationRecord` sections, writes `comp17+180` (0x825B60A4 / 0x825B60E0) and rebuilds
// through `sub_825B8AD0`. Verified in the decompile.
PPC_EXTERN_FUNC(rex_CECCompositeModel_ApplyCharacterCreationRecord);
PPC_EXTERN_FUNC(rex_CECWeaponSet_EquipWeaponByRecord);
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_RemoveWornByCategory);
PPC_EXTERN_FUNC(rex_CECWeaponSet_GetSlotNameForRecord);   // weapon-set: slot name for a record's WeaponType
PPC_EXTERN_FUNC(rex_CECInventory_InstantiateHeldRecord);   // inventory: instantiate a held record as an item entity
PPC_EXTERN_FUNC(rex_lhEntity_Activate);   // entity: activate (components' vtable+44, world registration)
PPC_EXTERN_FUNC(rex_CECHeldItems_AttachItemToSlot);   // held-items: attach an item to a named slot
PPC_EXTERN_FUNC(rex_CECWeaponSet_RecordDrawnItem);   // weapon-set: record the drawn item
PPC_EXTERN_FUNC(rex_CECWeaponSet_PlayWeaponDrawEffect);   // weapon-set: draw effect
PPC_EXTERN_FUNC(rex_CECHeldItems_ReleaseSlot);   // held-items: release a named slot, returning the item it held
PPC_EXTERN_FUNC(rex_lhNetManager_FindEntityByNetId);   // network: net id -> entity (client: the replication map; host: uid)
PPC_EXTERN_FUNC(rex_lhEntity_Destroy);   // entity: destroy
PPC_EXTERN_FUNC(rex_lhCoopScreen_SpawnPreviewMannequin);   // co-op screen: spawn the preview entity for the current pickbox

// Dressing player two. `sub_824F0370(ItemRef out[4], CECInventory*, defHash)` puts a def's item
// into the inventory (bumping the count of one already held); `sub_824BF068(comp15, defHash)`
// wears it, and ONLY if comp 13 already holds it (0x824BF1C4); `sub_82764DC0(item, &ref)`
// releases the ref `sub_824F0370` handed back. `sub_824EFFC8(comp13)` / `sub_824BEB78(comp15)`
// are creation's own `InitialItems` / `InitialModifiers` passes, the fallback when no kit names
// an outfit. All verified in the decompile; all present in the recompile.
PPC_EXTERN_FUNC(rex_CECInventory_AddItemByDefHash);
PPC_EXTERN_FUNC(rex_CECInventory_AddOneOfRecord);
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_WearItemByDefHash);
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);
PPC_EXTERN_FUNC(rex_CECInventory_AddInitialItems);
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_ApplyInitialModifiers);
PPC_EXTERN_FUNC(rex_lhPlayer_ChangeEntityType);  // Player::ChangeEntityType(Player*, LHString creatureName)
PPC_EXTERN_FUNC(rex_lhScriptGui_FadeScreenIn);  // FadeScreenIn(double seconds)

/// The abilities screen's event handler — `vtable+20` of the class `sub_826C1E80` builds.
/// Called by static dispatch (the recompiler emits no indirect-call path), but the live
/// object's slot is read and compared against this address first, so a screen whose slot holds
/// anything else is refused. Vtable `0x820CF4BC`: `+16` `0x826C23A8` (per-frame update), `+20`
/// `0x826C2660` (this), `+24` `0x826C2958` (ScreenAction).
PPC_EXTERN_FUNC(rex_lhCharString_Dtor);
PPC_EXTERN_FUNC(rex_lhWorldMap_BuildFamilyList);   // the family collector — see SampleFamily
PPC_EXTERN_FUNC(rex_lhFamilyMemberVector_Destroy);   // and its destructor; the two must be called as a pair
PPC_EXTERN_FUNC(rex_CECInventory_FindItemByRecordHash);   // inventory record -> item handle, BY RECORD HASH
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);   // release an item handle taken by sub_824F1C90
PPC_EXTERN_FUNC(rex_lhEntity_UseInventoryItem);   // use an item, by handle
PPC_EXTERN_FUNC(rex_lhGiftComponent_GiveGift);   // give a gift, by handle
PPC_EXTERN_FUNC(rex_lhGuiInventory_ActivateItemByCategory);   // ACTIVATE an item by handle — the guest's own category switch
PPC_EXTERN_FUNC(rex_lhHeroStats_GetMorality);   // GetMorality — the DISPLAYED value
PPC_EXTERN_FUNC(rex_lhHeroStats_GetPurity);   // GetPurity   — the DISPLAYED value
PPC_EXTERN_FUNC(rex_lhHeroStats_GetRenown);   // GetRenown   — the DISPLAYED value
PPC_EXTERN_FUNC(rex_lhGuiPauseMenu_IsTabAvailable);   // is this pause tab available — the guest's own predicate
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetModifierEffectTerm);   // appearance-modifier term, by kind
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetPreferenceTerm);   // preference term, by axis
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetHomeBonusTerm);   // home bonus
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetPurityOpinionTerm);   // purity/corruption opinion
PPC_EXTERN_FUNC(rex_lhWideString_Dtor);   // ~LHString — the WIDE release, what the filler uses on
                                 // every provider output (`sub_82214DD8` is the narrow one)
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatDistanceText);   // region distance
PPC_EXTERN_FUNC(rex_lhGuiText_FormatDaysRemaining);   // days remaining, ALREADY WORDED (takes a float in f1)
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatTravelTimeText);   // region travel time
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatFineText);   // region fine
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatMerchantsText);   // region merchants
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatPropertiesOwnedText);   // region properties owned
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatChestsText);   // region chests
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatDigSpotsText);   // region dig spots
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatDiveSpotsText);   // region dive spots
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatGargoylesText);   // region gargoyles
PPC_EXTERN_FUNC(rex_lhRegionStats_FormatSilverKeysText);   // region silver keys
PPC_EXTERN_FUNC(rex_lhRegionStats_GetEconomyRating);   // region economy, as a STAR COUNT
PPC_EXTERN_FUNC(rex_lhRegionManager_FindRegionOwningLevel);   // the region that OWNS a level, by level name
PPC_EXTERN_FUNC(rex_lhWorldMap_CanFastTravelTo);   // can this quest be fast-travelled to
PPC_EXTERN_FUNC(rex_lhQuestTrail_IsTrailActive);   // is a trail already running
PPC_EXTERN_FUNC(rex_lhQuestTrail_HasNoDestination);   // has the world no trail destination
PPC_EXTERN_FUNC(rex_lhCharString_OperatorNotEqual);   // LHString CCharString operator!= -- NEGATED, see below
PPC_EXTERN_FUNC(rex_lhQuestTrail_IsTargetingEntity);   // is the trail already pointing at this family member
PPC_EXTERN_FUNC(rex_lhFamily_GetFastTravelStopFromGameData);   // a member's fast-travel stop, from GAMEDATA
PPC_EXTERN_FUNC(rex_lhRegionManager_FindFastTravelStopForLevel);   // and from the live region registry, when GAMEDATA has none
PPC_EXTERN_FUNC(rex_lhCharString_Assign);   // CCharString assign
PPC_EXTERN_FUNC(rex_lhGuiAbilitiesScreen_OnMenuEvent);   // 14 abilities
PPC_EXTERN_FUNC(rex_lhGuiPauseRootScreen_OnMenuEvent);   //  0 pause root
PPC_EXTERN_FUNC(rex_lhGuiItemsScreen_OnMenuEvent);   // 15 items
PPC_EXTERN_FUNC(rex_lhGuiClothingScreen_OnMenuEvent);   // 16 clothing
PPC_EXTERN_FUNC(rex_lhGuiLogbookScreen_OnMenuEvent);   // 17 logbook
PPC_EXTERN_FUNC(rex_lhGuiOptionsScreen_OnMenuEvent);   // 18 options
PPC_EXTERN_FUNC(rex_lhGuiCrimeScreen_OnMenuEvent);   // 21 crime
PPC_EXTERN_FUNC(rex_lhGuiShopScreen_OnMenuEvent);   // 22 shop
PPC_EXTERN_FUNC(rex_lhGuiWorldMapScreen_OnMenuEvent);   // 23 world map
PPC_EXTERN_FUNC(rex_lhGuiGiftGivingScreen_OnMenuEvent);   // 25 gift giving
PPC_EXTERN_FUNC(rex_lhGuiScreen_OnMenuEventNavSoundsOnly);   // 26 furniture, 27 decoration, 24 co-op — one folded body

// The presentation the GUI is currently driving — `sub_823DCBE0(*(GUI+56))`. Every attribute
// read and every Lua chunk in this file is scoped to whatever it returns.
PPC_EXTERN_FUNC(rex_lhGuiPresentationSet_GetCurrent);

// `sub_826B9210(LHString** name, int element)` -> float. Reads one float attribute off the
// Parameters element; `element == 0` means "resolve the current presentation yourself". Used
// here for exactly one thing: reading `EventQueueSize` back after a push.
PPC_EXTERN_FUNC(rex_lhGuiPresentation_GetFloatParam);

// The matching writer — `sub_826B9440(f1 = value, r4 = LHString* name, r6 = presentation)`.
// Same element `sub_826B9210` reads from: presentation vtable slot 32 gives the parameter table
// at `pres+0x1EC`, and its own vtable slot 72 is the float setter. `r6 == 0` means "resolve the
// current presentation yourself"; `r3` and `r5` are read by neither. Called rather than poked:
// the setter also raises the dirty bit, marks the change list and runs the change callbacks.
PPC_EXTERN_FUNC(rex_lhGuiPresentation_SetFloatParam);

namespace refii::vfs {

// Defined with the co-op helpers further down; the dispatchers above them need these.
// (A declaration OUTSIDE this namespace named a different, undefined function — and the
// generated library refused to load on `_Z12IsCoopJoinerPh`.)
bool IsCoopJoiner(uint8_t* base);
bool CoopSessionLive(uint8_t* base);
uint32_t CoopNetworkManager(uint8_t* base);
void CoopRepairNetworkSlot(uint8_t* base);
void CoopApplyHero(PPCContext& ctx, uint8_t* base, int player, uint32_t entity);
bool EquipRecord(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t id, std::string& report);
void CoopAttachHeld(PPCContext& ctx, uint8_t* base, int player, uint32_t entity);

// The queue and the counters live in src/core/guest_tick_pump_state.cpp, which links into
// refii_vfs: the HOST posts and this side drains, so state beside the pump would be a copy the
// payload writes and the bridge never reads. Reached through the GuestTickPump* wrappers in
// vfs_hooks_private.h.

/// The front-end state object. `sub_826E0D60` dereferences it with NO null check —
/// `lwz r11, dword_83496AC0` then `lwz r9, 8(r11)` at 0x826E0E30 — so an action that runs
/// while the front end is down is a fault, not a no-op.
constexpr refii::guest::Addr kFrontEndState{0x83496AC0u};

/// The signed-in user index, and the mask of which pads are signed in. Read exactly as the
/// guest's own New Game guard reads them at 0x826E0064/0x826E0068.
constexpr refii::guest::Addr kSignedInUserIndex{0x83496AF8u};
constexpr refii::guest::Addr kSignedInUserMask{0x83496A70u};

/// saveMgr = *(*(kFrontEndState + 0xC) + 0x28), per 0x826E0084..0x826E008C.
constexpr uint32_t kGameStateOffset = 0x0Cu;
constexpr uint32_t kSaveManagerOffset = 0x28u;

/// The interned `BOY_SELECTED` / `GIRL_SELECTED` event ids, interned at static init as
/// `sub_82189140(literal) & 0x7FFFFFFF`. Read the id the guest computed; never recompute it.
constexpr refii::guest::Addr kEventBoySelected{0x8349B5A0u};
constexpr refii::guest::Addr kEventGirlSelected{0x8349B5A4u};

/// Starts a new game, on the guest's own thread.
///
/// Not `GUI_FrontendRoot_MenuActionDispatch` (0x826DFD58): its "New Game" branch only reaches
/// the GENDER CARD — GotoSlide 1 on the `choosecard` element (0x826E021C), `screen+24 = 2`
/// (0x826E022C) — and the commit happens only once the presentation's Behavior element fires
/// BOY_SELECTED or GIRL_SELECTED back into the handler below. Godot draws that card itself
/// (`Engine_FrontEndCardChoice`) and posts the event here. Not `Frontend_SetStartGameTarget`
/// (0x82379838) either: that is the LOAD path and sets no entrance, scenario or quest.
///
/// The real commit is `sub_82379628` — world "Albion", level "BWSSlums", entrance
/// "QC010_ChildhoodStart", and an empty `g_PendingLoadSaveName`, which makes
/// `Game_InitDatabases` take the new-game branch. `sub_826E0D60` is its only caller and takes
/// an EVENT rather than a string. It must run on the guest thread: the commit allocates
/// (`sub_82319928` publishes a new in-game state) and the start-game globals are refcounted
/// LHStrings.
///
/// The arguments, from the disassembly:
///
///   826e0d70  lwz  r11, 4(r4)          ; the ONLY thing read from r4
///   826e0e1c  li   r5, -1
///   826e0e20  addi r3, r1, var_10      ; r3 OVERWRITTEN before any read on this path
///   826e0e38  lwz  r3, 0x50(r9)        ; the frontend object comes from the GLOBAL
///   826e0e3c  bl   sub_82379628
///
/// So `r3` is a genuine don't-care here and `r4` need only be a guest pointer whose word at
/// `+4` is the event id. Note this is true ONLY on the SELECTED branches — the three earlier
/// comparisons in the same function (`BOY_ACTIVE` and friends) write through `r3` at
/// `stw r11, 0x18(r3)`, so passing a junk r3 with one of those ids would corrupt the screen.
///
/// Returns false when the front end is not up, leaving the action queued.
/// Runs the guest's own storage-device selection, so the game that starts can be saved.
///
/// `StartNewGame` bypasses `GUI_FrontendRoot_MenuActionDispatch`, which is where the guest's
/// three guards live; the second (0x826E0094) is this call. Without it the device map at
/// `saveMgr+0x7EC` stays empty and the game can never write a save. Only the DEVICE guard is
/// reproduced — the sign-in test is repeated because the picker is pointless without it, and
/// the slot cap belongs to the commit path. Idempotent: the guest also calls this on its Load
/// Game branch (0x826E03A0).
bool SelectStorageDevice(PPCContext& ctx, uint8_t* base) {
  const uint32_t game = LoadGuestU32(base, kFrontEndState);
  if (game == 0) {
    return false;
  }

  const uint32_t userIndex = LoadGuestU32(base, kSignedInUserIndex);
  const uint32_t signedIn = LoadGuestU32(base, kSignedInUserMask);
  if (((1u << userIndex) & signedIn) == 0) {
    // GUARD 1, 0x826E0078. Nobody is signed in, so there is no profile to pick a device for.
    return false;
  }

  const uint32_t state = LoadGuestU32(base, game + kGameStateOffset);
  if (state == 0) {
    return false;
  }
  const uint32_t saveManager = LoadGuestU32(base, state + kSaveManagerOffset);
  if (saveManager == 0) {
    return false;
  }

  // A gap on the guest stack, as StartNewGame claims: the callee frames below r1.
  const PPCContext saved = ctx;
  ctx.r1.u32 = (ctx.r1.u32 - 0x40u) & ~0xFu;
  ctx.r3.u32 = saveManager;
  ctx.r4.u32 = userIndex;
  ctx.r5.u32 = 1u;  // a3: compute the required size, as the guest passes at 0x826E0080

  rex_lhSaveManager_SelectStorageDevice(ctx, base);

  const bool selected = (ctx.r3.u32 & 0xFFu) != 0;
  ctx = saved;
  return selected;
}

bool StartNewGame(PPCContext& ctx, uint8_t* base, bool female) {
  const uint32_t frontend = LoadGuestU32(base, kFrontEndState);
  if (frontend == 0) {
    return false;
  }

  // Before the commit: the save manager records the device against the user, and the new game
  // reads it the first time it writes. Not fatal when it fails, as in the guest.
  if (!SelectStorageDevice(ctx, base)) {
    REXLOG_WARN(
        "Refii: no storage device selected; this game will not be able to save");
  }

  const uint32_t event =
      LoadGuestU32(base, female ? kEventGirlSelected : kEventBoySelected);
  if (event == 0) {
    // Static init has not interned the events yet. Queued rather than sent, because sending
    // id 0 would fall off the end of the compare chain and silently do nothing — the failure
    // that looks like success.
    return false;
  }

  // A frame of our own on the GUEST stack, claimed by lowering r1. `r4` is dereferenced by
  // guest code so the record must be a guest address, and it cannot sit below the current sp:
  // `sub_826E0D60` and `sub_82379628` frame down from r1 and would overwrite it.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x40u) & ~0xFu;
  const uint32_t record = frame + 0x20u;

  StoreGuestU32(base, record + 0u, 0u);
  StoreGuestU32(base, record + 4u, event);

  // Whole-context save/restore: the pump stands inside `sub_822C1BD0`, whose own delegation
  // still needs its `r3`.
  const PPCContext saved = ctx;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = 0u;  // don't-care, per the disassembly above
  ctx.r4.u32 = record;

  rex_lhGui_FrontendRoot_HandleGuiEvent(ctx, base);

  ctx = saved;

  // A game just started, so the saved-game count is stale; re-sampled when the front end is up.
  GuestTickPumpRequestSaveCount();
  return true;
}

/// The GUI object, and the main-menu screen hanging off it at +184. The vtable check is
/// load-bearing: `dword_83496B08` has two writers through the same base ctor, and once the game
/// has taken over it holds a BASE-ONLY object whose +184 is not a screen pointer.
constexpr refii::guest::Addr kGuiObject{0x83496B08u};
constexpr refii::guest::Addr kFrontEndGuiVtable{0x820A924Cu};
constexpr uint32_t kMainMenuScreenOffset = 184u;

uint32_t MainMenuScreen(uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (gui < 0x10000u || gui >= 0xC0000000u) {
    return 0;
  }

  if (LoadGuestU32(base, gui) != kFrontEndGuiVtable) {
    // The base-only instance — the game has taken the front end over.
    return 0;
  }

  const uint32_t screen = LoadGuestU32(base, gui + kMainMenuScreenOffset);
  return (screen >= 0x10000u && screen < 0xC0000000u) ? screen : 0;
}

/// Takes one saved-game count, on the guest's thread, by calling the guest's own probe.
///
/// Gated on the front end being up, though the probe is not: that is when the guest itself asks
/// (`sub_826E2220`, at menu-spawn), and the SLOT LIMIT `dword_83319620` holds 150 in the image
/// until `sub_82378618` — the front-end enter — overwrites it with 6, so count and limit must
/// come from the same side of that store.
///
/// No scratch frame here: `sub_82443308` takes no arguments and frames its own 46320 bytes
/// downward from r1 after an `RtlCheckStack12` probe. The context save/restore still applies.
void SampleSavedGameCount(PPCContext& ctx, uint8_t* base) {
  if (!GuestTickPumpWantsSaveCount()) {
    return;
  }
  if (MainMenuScreen(base) == 0) {
    // Front end not up: left pending rather than answered with a zero (-1 is not 0).
    return;
  }

  // Cleared BEFORE the probe: a request made while it runs must survive.
  GuestTickPumpClearSaveCountRequest();

  const PPCContext saved = ctx;
  rex_lhSaveManager_CountSavedGamesAllDevices(ctx, base);
  const int32_t count = static_cast<int32_t>(ctx.r3.u32);
  ctx = saved;

  GuestTickPumpNoteSaveCount(count);
  REXLOG_INFO("Refii guest-tick pump: the guest reports {} saved game(s)", count);
}

/// The front-end root screen's save list, as `sub_82443048` leaves it.
///
/// Two parallel arrays, both indexed by the same selection:
///
///   this+84   begin of a vector<handle>, 4-byte stride — the save FILE NAMES
///   this+88   its end, so the count is (end - begin) / 4
///   this+148  begin of a vector of EMBEDDED 40-byte records — the ChapterSave metadata
///
/// Within a record, `+12` is the LevelName handle and `+24` is the compatibility verdict that
/// `sub_82441780` computed from VersionNumber and CurrentEpisode. The guest's own confirm
/// refuses a save whose `+24` is zero (0x826E0480), so this does too.
///
/// THOSE ARE THE TU1 OFFSETS. Without the title update the same fields are 12 lower and the
/// record is 36 bytes: see refii::guest::FrontEndMenuField and SaveRecordStride.
constexpr uint32_t kSaveNamesBegin = 84u;
constexpr uint32_t kSaveNamesEnd = 88u;
constexpr uint32_t kSaveRecordsBegin = 148u;
constexpr uint32_t kSaveRecordLevelName = 12u;
constexpr uint32_t kSaveRecordCompatible = 24u;

/// Loads the save at `index`, by handing the guest the two handle slots it wants.
///
/// The addresses ARE the arguments: `Frontend_SetStartGameTarget` takes pointers to live handle
/// slots inside the guest's own vectors, so this passes `begin + 4*index` and
/// `records + 40*index + 12` — nothing to allocate and no scratch frame to claim.
///
/// Returns false when the front end is down or the list cannot support the index, leaving the
/// action queued rather than calling with a bad pointer.
bool LoadSavedGame(PPCContext& ctx, uint8_t* base, uint32_t index) {
  // DROPPED ONCE THE GAME IS UP. `byte_83496A5E` is the guest's own in-game flag (set by
  // Game_InitDatabases at 0x82311648, cleared by the game mode's shutdown). A load that was
  // still held when the game came up by another route is stale; held on, it fired the moment
  // Quit to Title brought the front end back with its save vectors filled, and re-loaded the
  // save from the title screen.
  if (base[refii::guest::Address(0x83496A5Eu)] != 0u) {
    REXLOG_INFO("Refii guest-tick pump: LoadSave:{} dropped — the game is already up", index);
    return true;
  }

  const uint32_t screen = MainMenuScreen(base);
  if (screen == 0) {
    return false;
  }

  const uint32_t begin = LoadGuestU32(base, screen + refii::guest::FrontEndMenuField(kSaveNamesBegin));
  const uint32_t end = LoadGuestU32(base, screen + refii::guest::FrontEndMenuField(kSaveNamesEnd));
  const uint32_t records = LoadGuestU32(base, screen + refii::guest::FrontEndMenuField(kSaveRecordsBegin));
  if (begin == 0 || end <= begin || records == 0) {
    // The vectors are filled only inside the dispatcher's own "Load Game" branch, so an empty
    // list here means that branch has not run yet — held, not failed.
    return false;
  }

  const uint32_t count = (end - begin) / 4u;
  if (index >= count) {
    REXLOG_INFO("Refii guest-tick pump: save {} is out of range (the guest lists {})", index,
                count);
    return true;  // Dropped rather than retried forever; the list will not grow on its own.
  }

  const uint32_t record = records + (index * refii::guest::SaveRecordStride());
  if (base[record + kSaveRecordCompatible] == 0) {
    // The guest's own gate at 0x826E0480. A save this byte rejects is the one that raises
    // GUI_CORRUPT_SAVE_GAME_WARNING rather than loading.
    REXLOG_INFO("Refii guest-tick pump: save {} is marked incompatible — refused", index);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = 0u;  // never referenced by the callee
  ctx.r4.u32 = begin + (index * 4u);
  ctx.r5.u32 = record + kSaveRecordLevelName;

  rex_lhFrontEnd_SetStartGameTarget(ctx, base);

  ctx = saved;
  return true;
}

/// Performs a menu entry the way the guest performs it: by NAME, through its own dispatcher.
/// `sub_826DFD58` is a strcmp chain over the front end's own item names ("Load Game",
/// "Downloadable Content", "SubtitlesOn", "SubtitlesOff"); an unknown name matches nothing.
///
/// The record is three words and needs no guest object: `a2+0` points at a string body whose
/// own `+0` is a `char*`, `a2+4` is a command id, `a2+8` an extra param. The dispatcher
/// dereferences `a2[0]` once (`lwz r3, 0(r28)` at 0x826DFFEC feeding the strcmp at 0x826E0040)
/// and never retains or frees the record, so a scratch frame on the guest stack suffices.
///
/// `a2+4` must not be 302, 141 or 140: those are consumed by a switch ahead of the name chain
/// (302 Language, reading `a2+8`; 140/141 the menu slide sounds). Zero falls through.
///
/// Returns false when the front end is not up, leaving the action queued.
/// The dispatcher's three ID-based cases, decided BEFORE the name chain is reached.
///
/// 140/141 are the menu's slide sounds — "SE_GUI_SLIDE_MENU_UP" (`dword_83497DD8`) and
/// "SE_GUI_SLIDE_MENU_DOWN" (`dword_83497DDC`) on the emitter at `*(*(dword_83496AC0+8)+0x50)`.
/// 302 is Language: it reads `a2+8` as the index into the guest's language table and is the
/// only route to that action — the language entries never appear in the name chain.
constexpr uint32_t kCommandNone = 0u;
constexpr uint32_t kCommandSetLanguage = 302u;
// The dispatcher's ordinary item activation: `switch (a2[1])` case 2 string-compares the item
// NAME (`GUI_FrontendRoot_MenuActionDispatch`, 0x826DFD58). Every root entry spawns with this
// id — `sub_826E2220` passes 2 for all of them.
constexpr uint32_t kCommandActivateItem = 2u;

// Subtitles sampling edges — see the sampler at the end of OnGuestTick.
uint32_t g_last_menu_screen = 0;
bool g_sample_subtitles_after_drain = false;
constexpr uint32_t kCommandSlideMenuUp = 140u;
constexpr uint32_t kCommandSlideMenuDown = 141u;

/// Host-only names for the two sounds: on the guest's side these are command ids, not names.
constexpr const char* kActionSlideUp = "MenuSlideUp";
constexpr const char* kActionSlideDown = "MenuSlideDown";

/// "Language:<index>" — the index rides in the name because the queue carries only names. The
/// guest spawns one language entry per installed language with command 302 and the row index.
constexpr const char* kActionLanguagePrefix = "Language:";

/// "PauseSaving:<param>" — one of the pause menu's Saving children, by the guest's own
/// {type 2, param} contract: 0 save-and-continue, 1 save-and-quit, 2 quit-without-saving.
/// A number, not a name: the guest's dispatch never reads the name on this path.
constexpr const char* kActionPauseSavingPrefix = "PauseSaving:";

/// "QuickWheel:open|close|select|back|release|point:<i>" — the expression wheel through the
/// guest's own routines, because the GameAction chain that would run them (40 hold / 41
/// release / 36 accept / 37 back / 25 end-held, `sub_824040B8` and `sub_82405190`) does not
/// fire here. Every gate stays the guest's: `sub_8236F400` (hero alive, not busy, player mode
/// 1/5/20/21/22), `QM+35` (open), `W+0` (state), and each routine's own cursor tests. The
/// STICK needs no lever — `sub_82402F70` routes the raw left-stick event to `sub_826AA060`
/// itself, gated on `QM+35`; `point:<i>` is the fallback that feeds the same routine the unit
/// vector of sector i (angle 3π/4 − iπ/4, 0 = NE clockwise to 7 = N).
constexpr const char* kActionQuickWheelPrefix = "QuickWheel:";

/// "SpellsMenu:open|close" — a DEBUG VERB, not the mechanism.
///
/// THE PREMISE THIS SHIPPED ON IS FALSE. It said "no GameAction arrives at the consumer at all",
/// so the host had to drive 42/43 itself. But `sub_8236F788` has exactly ONE code caller in the
/// image — 0x82404290, inside the GameAction dispatcher `sub_824040B8` case 0x2A — and the only
/// non-zero writers of the open flag QM+36/+37 are its own stores at 0x8236f814/0x8236f81c.
/// The flag goes set in a live run while this lever logs nothing but refusals, so the GUEST is
/// running 42/43 on its own and these two verbs RACE it. See the note in DispatchSpellsMenu for
/// what that race costs. Keep them for driving the blade from the debug channel; do not treat
/// them as the way the feature works. The shipped scheme binds 42 to XboxButton 16,
/// the RIGHT TRIGGER: `GameControlsCombatOnRelease` is an @alias onto a scheme that overrides
/// `GameActionGUISpellsMenu`'s input over a base of 10 (RB). That pairing is GDB data — the
/// binding vector ships EMPTY in `sub_8244B678` — so it cannot be read out of the executable.
constexpr const char* kActionSpellsMenuPrefix = "SpellsMenu:";

/// "SpellBlade:up|down|left|right|release|copydown" — one input into the QUICK SPELL BLADE,
/// the five-power-level widget the right trigger opens over the world.
///
/// IT IS WIRED ON THE ABILITIES SCREEN AND NOWHERE ELSE. `sub_826C2958` routes that screen's
/// input into `sub_826B5430(screen14 + 200, ...)` at 0x826c29a0, which `DispatchScreenAction`
/// reaches. The quick blade is the SAME class at `*(*(GUI+32)+92)` (player 1) / `+96` (player
/// 2), standing over the running game with no screen up — so `InGameScreenRoots` refuses it and
/// nothing else here can address it. `sub_82405190`, the guest's own route, needs an object
/// whose vtable slot 16 returns the player index; we have no such object and will not fabricate
/// one. `sub_826B5430` needs neither a vtable nor a player index, so it is the first
/// host-addressable link.
///
/// THE CODES ARE THE GUEST'S OWN GAMEACTION IDS, AND THE STICK IS NOT ONE OF THEM. `sub_826B5430`
/// switches on what `sub_828818E8(this + 132, code)` RETURNS (0x826b5450 / 0x826b5474): 28 UP
/// `++row`, 29 DOWN `--row`, 30/31 move the carousel and commit, 36 A copies down. Code 26, the
/// left stick, is resolved into a direction ONLY while the latch byte at widget+165 is set
/// (`if (!*((_BYTE *)a1 + 33)) goto LABEL_17;` at 0x82881914) — and the constructor
/// `sub_826B4358` writes that byte 1 at 0x826b43d4 and then 0 at 0x826b4410 (`stb r10, 0xA5(r3)`,
/// r10 the zero register), so it is compiled off for this widget and a 26 falls through to
/// nothing. A `stick` post moved the box exactly never. The guest moves it on the held d-pad ids,
/// which is what these verbs hand over.
///
/// POST THE HELD DIRECTION EVERY FRAME, AND `release` ONCE WHEN IT LETS GO. The repeat is the
/// widget's own latch (`sub_82881B28`: the first press lands at once, the second after 0.3 s,
/// then every 0.1 s, 0x826b43f0..0x826b4440), so the host keeps no clock; a second 28 inside
/// the window returns 0 (0x82881a48). Ids 32..35 reset the latch (0x82881a88..0x82881a9c), so
/// the next press after a release lands at once again, as the guest's would.
///
/// THE OPEN BYTE IS NOT TIDINESS. `*(HUD+92)` is allocated once at GUI construction and nulled
/// only in the destructor — open and close never touch it — but the row ceiling at widget+412
/// and the cell tables at widget+180..+408 are written ONLY by `sub_826B4848`, which runs as
/// part of opening. A plausible pointer over uninitialised tables is what the gate is for.
constexpr const char* kActionSpellBladePrefix = "SpellBlade:";

/// "RegisterPlayer" — the guest's own `sub_82371860(GUI, player)`, run only while the in-game
/// GUI reports NO registered player (`*(u8*)(GUI+0x65) == 0`).
///
/// That byte gates the whole Gameface update: `sub_821A1140` advances every presentation's
/// timeline and drains the event queue only while `byte_83319409 && *(GUI+0x65)`. The guest
/// increments it from `sub_82314D30`, which also binds the player into the action consumer
/// (`sub_825E8DE0`/`sub_823D2508`) — the bound player at consumer+12 the GameAction chain needs.
constexpr const char* kActionRegisterPlayer = "RegisterPlayer";

/// "CoopHeroBody:<male|female|child|femalechild|henchman...>" — retype player two's entity.
///
/// STAGE 1 of the native-hero work (docs/coop_native_hero_plan.md). The shipped game spawns
/// player two from one of eight fixed henchman creatures (`sub_8233C508`) and has no path for a
/// visitor's own hero at all. The body, though, is one guest call: `sub_82389FB8` is
/// `Player::ChangeEntityType`, the same entry the shipped debug menu drives as
/// `Player.ChangePlayerEntityType`, and it already tells the co-op manager (0x8233CB18).
///
/// This answers the one question nothing else can: whether a SECOND `CreatureHero` spawns
/// cleanly. Player one is EntityUID 1 and player two would be 2 through the same GDB path, and
/// the shipped game never spawns two.
constexpr const char* kActionCoopHeroBodyPrefix = "CoopHeroBody:";

/// "CoopHeroSkills:<hex>,<hex>,..." — player two's ability ranks, ids 0..14 in order (0 unused).
constexpr const char* kActionCoopHeroSkillsPrefix = "CoopHeroSkills:";

/// "CoopHeroBuild:<strength>,<skill>,<will>,<fat>,<height>,<tan>" — player two's BODY SHAPE.
constexpr const char* kActionCoopHeroBuildPrefix = "CoopHeroBuild:";

/// "CoopHeroStats:<money>,<gender>,<morality>,<purity>,<renown>,<15 ranks>,<4 exp>,<4 total>" —
/// a HeroSave block, applied the way `sub_8243AEF8` applies one it just loaded.
constexpr const char* kActionCoopHeroStatsPrefix = "CoopHeroStats:";

/// "CoopHeroFace:<hex record id>" — player two's character-creation record, on component 17.
constexpr const char* kActionCoopHeroFacePrefix = "CoopHeroFace:";

/// "CoopHeroEquip:<1|2>:<meleeHex>,<rangedHex>" — draw two weapons the player already OWNS.
/// `sub_824ABA28(comp28, recordId, 0)` is the guest's own equip-by-record (the handler behind
/// the weapons book's own row, `sub_826BA618`): it instantiates the record held in component 13
/// (`sub_824F26A8(comp13, id, "Weapon")`), attaches it to the held-item slots
/// (`sub_824FB5D8(comp24, slotName, item)`), records it in the weapon set (`sub_824AD108`) and
/// plays the draw effect. Verified in the decompile (0x824ABA28): with the third argument 0 and
/// the entity not mid-action it takes the direct path; the replication inside gates on
/// `sub_82214AF8(comp91)`, so on the joiner's copy of player one it stays local — which is the
/// copy the sync leaves armed by name only. Latched per (entity, pair): restated freely.
constexpr const char* kActionCoopHeroEquipPrefix = "CoopHeroEquip:";

/// `CoopHeroHeld:<1|2>:<meleeNetId>,<rangedNetId>` — net ids of the item entities the host's
/// player holds; a client attaches the replicas (CoopAttachHeld). Stored; the tick attaches.
constexpr const char* kActionCoopHeroHeldPrefix = "CoopHeroHeld:";

/// "CoopHeroApply:<1|2>:<generation>" — apply the slot's stored hero record to its body, ONCE per
/// (body, generation), in the verified order: give, wear, equip, then (player two) stats, skills,
/// build, face. The field actions only STORE from here on; this is the one apply. The generation
/// is the shell's hash of the whole record, so a changed outfit or weapon on the joiner is a new
/// generation and re-applies, while a restated identical record does nothing.
constexpr const char* kActionCoopHeroApplyPrefix = "CoopHeroApply:";

/// "CoopHeroWear:<1|2>:<hex>,<hex>,..." — the GDB def hashes a player's hero wears. Remembered,
/// not applied on receipt: the body is dressed whenever the guest (re)builds it.
constexpr const char* kActionCoopHeroWearPrefix = "CoopHeroWear:";

/// "CoopHeroWeapons:<meleeName>,<rangedName>" — the def names player two's own hero equips.
/// Answered from the guest's two weapon name tables by the hooks; the guest then creates,
/// carries, equips and replicates them itself.
constexpr const char* kActionCoopHeroWeaponsPrefix = "CoopHeroWeapons:";

/// "CoopHeroGive:<1|2>:<hex>:<count>,..." — records to put in a player's inventory, the WHOLE
/// set in one action. Remembered per player and given once per (entity, set).
constexpr const char* kActionCoopHeroGivePrefix = "CoopHeroGive:";

/// "ReceiveItemsAction:<code>" — one press on the received-item card, through its handler's
/// own screen-input method: 36 done, 39 flip, 75 close.
constexpr const char* kActionReceiveItemsPrefix = "ReceiveItemsAction:";

/// "MenuBoxSelect:<index>" — move the MenuBox highlight to a 0-based option, the guest's own
/// way, so the drawn card follows the host highlight.
constexpr const char* kActionMenuBoxSelectPrefix = "MenuBoxSelect:";

/// "MenuBoxAnswer:<n>" — n == 0 cancels the MenuBox; n >= 1 accepts option n-1. The guest's
/// accept/close does the publish (player message 35 subcode 101) and the teardown itself.
constexpr const char* kActionMenuBoxAnswerPrefix = "MenuBoxAnswer:";

/// One action code to whichever display box is up — see DispatchDisplayBoxAction.
constexpr const char* kActionBoxActionPrefix = "BoxAction:";

/// "FrontEndDialog:<code>" — answers the front end's own `dialog` box (36 accept, 37 cancel)
/// through the owning screen's input slot. See DispatchFrontEndDialog.
constexpr const char* kActionFrontEndDialogPrefix = "FrontEndDialog:";

/// "MenuScroll:up|down|left|right[:released]" — the DIRECTIONAL STICK, as the guest routes it.
///
/// The in-game lists keep their cursor in LUA: every screen's A press commits whatever
/// `g_Menu:GetHighlightedMenuItemData()` returns, and none implement up/down natively (screens
/// 22 and 25 swallow codes 28–35), so an owned highlight plus a plain A press commits the wrong
/// row. Never run `g_Menu:ScrollUp()` as a Lua string: it skips a gate and `sub_82426470`
/// cannot report failure.
///
/// The guest's path, at 0x82404ED0 and 0x82405030: for action 0x1C..0x1F it sets the
/// presentation attribute `String1` to the direction via `sub_826B9510` and runs
/// `"GUI:SetDirectionToHeld()"` on the context `sub_823DCBE0` returns; 0x20..0x23 are the
/// matching releases running `"GUI:SetDirectionToReleased()"`. Lua's `g_Menu:onUpdate` polls
/// `GUI:IsPushingUp()` and calls `ScrollUp` itself, keeping `UpDownInputDisabled` honoured and
/// the original repeat cadence. Both dispatchers take the code through a pointer and ignore
/// their first argument, so a scratch word suffices and the guest's own gates stay in the path.
constexpr const char* kActionMenuScrollPrefix = "MenuScroll:";

/// "ScreenAction:<code>" — one action code to the CURRENT in-game screen's own input method
/// (vtable+24): 36 select, 37 back, 39 the per-screen Y action, 72/73 tab prev/next.
constexpr const char* kActionScreenActionPrefix = "ScreenAction:";

/// "QueryAnswer:<code>" — answer the pending query box through the guest's own input router
/// (`sub_8236E458`). The CODE is the guest's action id, chosen host-side PER BOX ID: 46/47
/// for the saving/shop/real-estate confirms, 36/37 for the henchman and coop boxes, 39 for
/// the fast-travel confirm, 75 for the shop box's equip action. The router forwards it to
/// the UNDERLYING screen's handler — the openers never change the screen id — so this one
/// lever answers a box over any screen.
constexpr const char* kActionQueryAnswerPrefix = "QueryAnswer:";

/// "MenuEvent:<name>:<type>:<id>" — commit a menu row BY IDENTITY, which is what lets Godot own
/// the cursor: every list screen learns the chosen row from Lua (`sub_826C1B50` runs
/// `"g_Menu:GetHighlightedMenuItemData()"`), so a plain A press would commit Lua's row.
///
/// The shipped `g_Menu:SelectMenuItem` also sends identity, not an index:
///
///     if item.Ghosted then return end
///     GUI:PushButtonEvent(item.Name, item.EventType, item.IDLow, item.IDHigh)
///
/// `PushButtonEvent` is `guieventqueue.lua`, not a native: four parallel Lua arrays plus a float
/// attribute `EventQueueSize` on the Parameters element. The C++ drain `sub_82370450` pulls one
/// per tick through `sub_826C1810` (`GUI:PullEvent()`, rebuilding a 12-byte
/// `{LHString name, int32 type, int32 param1}`), compares the name against `NO_VALID_EVENT`,
/// and dispatches to the current screen's `vtable+20` — `sub_8236FFC0(*(GUI+28),
/// *(*(GUI+56)+4))` then `(*(*(u32*)screen + 20))(screen, &event)` at 0x823705E0.
///
/// That push is NOT the route taken: it goes through `sub_82426470`, which defers whenever
/// `guiObject+0x64` is clear, and that byte is cleared by the first GUI frame update and never
/// set again — so the chunk lands on a list nothing drains. `DispatchMenuEventDirect` builds the
/// same 12-byte event and calls the screen's `vtable+20` itself, reading the slot from the live
/// object. The Lua push is kept below only for classes the eleven-entry table does not cover.
///
/// `id` is the FULL 32-bit row id; the Lua halves are `id & 0xFFFF` and `id >> 16`, as
/// `sub_826CE8D0` split it into `Float1`/`Float2` at spawn. Ghosted rows are the caller's
/// problem: `SelectMenuItem`'s early return is bypassed here.
constexpr const char* kActionMenuEventPrefix = "MenuEvent:";

/// "MenuDiscard:<name>:<type>:<id>" — the same identity commit, but with the abilities
/// screen's `+198` byte CLEARED rather than set.
///
/// `sub_826C2D40` @0x826c2d78 branches on that byte: set -> `sub_8246C970`, the PURCHASE;
/// clear -> `sub_826C3A30` then `sub_8236FA68(M, text, 3, 2, 0)`, the DISCARD confirm. The
/// guest's own Y is `sub_826C2958` case 39, which stores 0 there (0x826c2b2c/0x826c2b30)
/// before pushing the event; case 36 (A) stores 1. So the arm is PART OF THE PRESS and cannot
/// be a constant — arming unconditionally turned every Y into a purchase and spent the
/// player's gold.
constexpr const char* kActionMenuDiscardPrefix = "MenuDiscard:";

/// `Inventory:<op>:<recordHash>` — see DispatchInventoryAction.
constexpr const char* kActionInventoryPrefix = "Inventory:";

/// `Ability:learn:<index>` — spend experience on one of the fourteen abilities.
constexpr const char* kActionAbilityPrefix = "Ability:";

/// `Shop:<buy|sell>:<ticket>` — see DispatchShopAction.
constexpr const char* kActionShopPrefix = "Shop:";

/// The SHIPPED commit — `g_Menu:SelectMenuItem()`, whole: an event AND A TOGGLE. It pushes the
/// highlighted row's `(Name, EventType, IDLow, IDHigh)` and, when the row has children, flips
/// `IsOpen` and fires the expand event that makes the Lua menu spawn that category's rows.
///
/// The identity push reproduces only the first half. Category rows (world map: `sub_826CF098`,
/// `li r9, 0x12D` = event type 301, the submenu marker) reach a handler with no case for 301,
/// so the fold never happens; the expansion lives entirely in the menu Lua.
///
/// This acts on the guest's OWN `CurrentHighlightIdx`, so it is only correct while the guest's
/// cursor and ours agree — which is why the identity push still exists.
constexpr const char* kActionMenuSelect = "MenuSelect";

// A diagnostic channel, not a gameplay path: runs the chunk verbatim on the current screen's
// presentation through the same `RunGuestLua` MenuSelect uses, so the guest's own Lua can be
// asked a question and answered through element text
// (`setAttribute(getElement(...), "textstring", tostring(<expr>))`). Behaviour gets a named
// action instead.
constexpr const char* kActionGuiLuaPrefix = "GuiLua:";

/// "SpellAssign:<slot>:<spell>" — put a will on one of the five d-pad slots.
///
/// The guest's widget commits on LEFT/RIGHT, not on A: `sub_826B5330` (code 31) moves the cell
/// cursor and calls the writer in the same breath, `sub_826B5240` (code 30) mirrors it. A
/// (`sub_826B4EF8`, code 36) instead copies the current row's spell into every LATER row that
/// also offers it.
///
/// `slot` is the row, 0..4, and `spell` is the guest's own 0..7 will index — not an ability id
/// and not a GDB row. Both are the numbers `sub_826B4848` builds its table from.
constexpr const char* kActionSpellAssignPrefix = "SpellAssign:";

/// "Quest:select:<record>" — A on a world-map quest row, delivered as the guest's own event.
///
/// The row's event (type 42, id = ORDINAL into the guest's filtered, sorted quest list) goes to
/// screen 23's handler `sub_826C6110`, which stores the type and id on the quick-menu object
/// (+84/+88, 0x826c6148/0x826c6150) and runs `sub_826C6940`: a quest already primary (or one in
/// the current level when fast travel is barred) goes straight to the travel path; any other
/// opens the query box id 3 "make this your primary quest?" (`sub_8236FA68(QM, text, 3, 3, 1)`
/// at 0x826c6ab4). The host draws that box and answers it through the modal router; the yes
/// (`sub_826C73B0`) re-reads the id from +88, runs `sub_8251CB30` = `QuestTracker.SetAsPrimary`
/// (primary flag on this quest and off every other, trail retargeted, banner), and then asks
/// "fast travel there?" (box id 8) when the quest is in another region and travel is allowed.
///
/// THE ORDINAL IS COMPUTED HERE, ON THE GUEST'S OWN LIST. `sub_8246E800` builds the list the
/// handler indexes — component 102's map through its five filters, sorted by DisplayPriority —
/// so the index of the quest whose definition (`quest+4`) is the record is exact by
/// construction, whatever the sort does with ties. The host's rows carry the record.
constexpr const char* kActionQuestPrefix = "Quest:";

/// "Sale:select:<nameTagHash>:<0|1>" — A on a world-map SALES row (1 = a shortage, the second
/// pass of `sub_8246EC10`). Same arrangement as the quests: the guest's own 44-byte row list is
/// rebuilt here, the row whose `+0` NameTagHash matches on the matching pass gives the ordinal,
/// and event 44 goes to screen 23's handler `sub_826C6CF0`, which asks "trail to this sale?"
/// (box 5), writes the TrailOverrideInfo on yes (`sub_82521698`) and may ask "fast travel
/// there?" (box 10). Jobs ride the quest verb: `sub_826C6B10` indexes `sub_8246EA08`'s list
/// (event 43), so `Quest:select` tries the quest list, then the jobs list.
constexpr const char* kActionSaleSelectPrefix = "Sale:select:";

/// "LoadSave:<index>" — pick a save out of the list the guest enumerated. An index, not a name:
/// the guest identifies a save by the ADDRESS of its handle slot inside its own vector, and the
/// index is the join it uses itself (`this+72`, moved by its own left/right handler).
constexpr const char* kActionLoadSavePrefix = "LoadSave:";

bool DispatchByName(PPCContext& ctx, uint8_t* base, const std::string& name,
                    uint32_t command = kCommandNone, uint32_t param = 0u) {
  // DROPPED ONCE THE GAME IS UP, as LoadSavedGame is: `byte_83496A5E` is the guest's in-game
  // flag (Game_InitDatabases, 0x82311648). A front-end action still held when the game came up
  // can never run — the front end is gone — and re-deferring it every tick held it for the whole
  // session, to fire on Quit to Title.
  if (base[refii::guest::Address(0x83496A5Eu)] != 0u) {
    REXLOG_INFO("Refii guest-tick pump: front-end action '{}' (command {}) dropped — the game "
                "is already up", name, command);
    return true;
  }

  const uint32_t screen = MainMenuScreen(base);
  if (screen == 0) {
    return false;
  }

  // A frame of our own on the GUEST stack, claimed by lowering r1, so the callee frames below
  // it instead of over the argument it was passed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x80u) & ~0xFu;

  // Laid out upward from the frame base so nothing overlaps: text, then the 4-byte body that
  // points at it, then the 12-byte record that points at the body.
  const uint32_t text = frame + 0x20u;
  const uint32_t body = frame + 0x60u;
  const uint32_t record = frame + 0x68u;

  // 0x40 bytes for the name. Refused rather than truncated: a truncated name would match a
  // DIFFERENT branch of the strcmp chain.
  if (name.size() + 1u > 0x40u) {
    REXLOG_INFO("Refii guest-tick pump: '{}' is too long to dispatch — dropped", name);
    return true;
  }

  for (size_t i = 0; i < name.size(); ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }
  base[text + name.size()] = 0;

  StoreGuestU32(base, body, text);
  StoreGuestU32(base, record + 0u, body);
  StoreGuestU32(base, record + 4u, command);
  // `a2+8` is read only on case 302, which passes it to `sub_82382C68` as the LANGUAGE INDEX;
  // every other branch ignores it.
  StoreGuestU32(base, record + 8u, param);

  // Whole-context save/restore. The pump is standing in the middle of `sub_822C1BD0`, whose
  // own delegation still needs its registers.
  const PPCContext saved = ctx;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = screen;
  ctx.r4.u32 = record;

  rex_lhGui_FrontendRoot_MenuActionDispatch(ctx, base);

  ctx = saved;
  return true;
}

/// One of the pause menu's Saving children, dispatched the way the guest's own drain
/// (`sub_82370450`) dispatches it: the pause handler's event method with {type 2, param}.
///
/// Drops rather than defers when the window is gone: the post is only made from the live pause
/// root, so a failed gate means a stale request, and re-queuing would fire a save dialog over
/// the next screen. The in-game vtable gate mirrors `MainMenuScreen`'s: `dword_83496B08` has
/// two writers, and only the in-game object (vtable 0x82001610) has a screen manager at +56.
bool DispatchPauseSavingEvent(PPCContext& ctx, uint8_t* base, uint32_t param) {
  const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
  const uint32_t kPauseHandlerVtable = refii::guest::Address(0x820CDE18u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != kInGameGuiVtable) {
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{} with no in-game GUI — dropped", param);
    return true;
  }

  const uint32_t manager = LoadGuestU32(base, gui + 56u);
  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!plausible(manager) || !plausible(owner) ||
      LoadGuestU32(base, manager + 4u) != 0u ||   // the pause ROOT is the active screen
      LoadGuestU32(base, owner + 44u) != 0u ||    // no query box pending
      LoadGuestU32(base, owner + 40u) != 1u) {    // the modal latch is idle
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{} outside the pause root — dropped",
                param);
    return true;
  }

  // The handler for screen id 0, out of the guest's own map, checked against the pause handler's
  // vtable before `sub_826A26E8` is called statically on it.
  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = 0u;
  rex_lhGuiManager_FindScreenHandler(ctx, base);
  const uint32_t handler = ctx.r3.u32;
  if (!plausible(handler) || LoadGuestU32(base, handler) != kPauseHandlerVtable) {
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: PauseSaving:{} found no pause handler — dropped",
                param);
    return true;
  }

  // The same scratch-frame layout as DispatchByName: text, the 4-byte body pointing at it, then
  // the three-word event. "Saving" matches none of the tab names, which is all the type-2 path
  // asks of the name.
  const uint32_t frame = (saved.r1.u32 - 0x80u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t body = frame + 0x60u;
  const uint32_t record = frame + 0x68u;

  static constexpr char kName[] = "Saving";
  for (size_t i = 0; i < sizeof(kName); ++i) {
    base[text + i] = static_cast<uint8_t>(kName[i]);
  }

  StoreGuestU32(base, body, text);
  StoreGuestU32(base, record + 0u, body);
  StoreGuestU32(base, record + 4u, 2u);
  StoreGuestU32(base, record + 8u, param);

  ctx.r1.u32 = frame;
  ctx.r3.u32 = handler;
  ctx.r4.u32 = record;

  rex_lhGuiPauseRootScreen_OnMenuEvent(ctx, base);

  ctx = saved;
  return true;
}

/// One press on the received-item card, through its handler's own screen-input method.
///
/// SCREEN input, not modal input: the card is a pushed screen (id 8) whose handler consumes
/// GameAction codes on vtable+24, not the modal router's +28, so the QueryAnswer lever cannot
/// reach it. The handler comes from the guest's screen map and its vtable is checked first.
/// Drops when stale: gated on the card being the current screen with the modal latch idle.
bool DispatchReceiveItemsAction(PPCContext& ctx, uint8_t* base, uint32_t code) {
  const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
  const uint32_t kReceiveItemsVtable = refii::guest::Address(0x820C0E08u);
  constexpr uint32_t kReceiveItemsScreen = 8u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != kInGameGuiVtable) {
    REXLOG_INFO("Refii guest-tick pump: ReceiveItemsAction:{} with no in-game GUI — dropped",
                code);
    return true;
  }

  const uint32_t manager = LoadGuestU32(base, gui + 56u);
  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!plausible(manager) || !plausible(owner) ||
      refii::guest::ScreenIdToTu1(LoadGuestU32(base, manager + 4u)) != kReceiveItemsScreen ||
      LoadGuestU32(base, owner + 40u) != 1u) {
    REXLOG_INFO("Refii guest-tick pump: ReceiveItemsAction:{} off the card screen — dropped",
                code);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = kReceiveItemsScreen;
  rex_lhGuiManager_FindScreenHandler(ctx, base);
  const uint32_t handler = ctx.r3.u32;
  if (!plausible(handler) || LoadGuestU32(base, handler) != kReceiveItemsVtable) {
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: ReceiveItemsAction:{} found no card handler — dropped",
                code);
    return true;
  }

  ctx.r3.u32 = handler;
  ctx.r4.u32 = code;
  // The third argument is the PLAYER SLOT, not a held flag: the guest's dispatcher passes
  // `inputObject->vtable+16()` (`sub_822A4E38`), which returns 1 or 2 — the value
  // `sub_8244BAD8` filters handlers by (a coop handler answers only on 2). Never 0.
  ctx.r5.u32 = 1u;  // player 1

  rex_lhGuiReceiveItemsScreen_OnScreenAction(ctx, base);

  ctx = saved;
  return true;
}

/// The MenuBox's controller, or 0 when no box is open.
///
/// `ctl = *(guiOwner+56)` — a plain struct, no vtable — and `ctl+12` is its open flag. A
/// Lua-opened list (DisplayMenuBox) that can stand over any screen, so there is no modal latch
/// to gate on.
uint32_t MenuBoxController(uint8_t* base) {
  const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != kInGameGuiVtable) {
    return 0u;
  }

  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!plausible(owner)) {
    return 0u;
  }

  const uint32_t control = LoadGuestU32(base, owner + 56u);
  if (!plausible(control) || base[control + 12u] == 0u) {
    return 0u;  // no box open
  }

  return control;
}

/// Moves the MenuBox highlight, the guest's own way — its selector also reslides the two
/// affected buttons, so the drawn card and the owned highlight cannot disagree.
bool DispatchMenuBoxSelect(PPCContext& ctx, uint8_t* base, uint32_t index) {
  const uint32_t control = MenuBoxController(base);
  if (control == 0u) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxSelect:{} with no menu box — dropped", index);
    return true;
  }

  const uint32_t begin = LoadGuestU32(base, control + 20u);
  const uint32_t end = LoadGuestU32(base, control + 24u);
  const uint32_t count = (begin == 0u || end < begin) ? 0u : (end - begin) / 8u;
  if (index >= count) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxSelect:{} past {} option(s) — dropped",
                index, count);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = control;
  ctx.r4.u32 = index;
  rex_lhMenuBox_SetSelection(ctx, base);
  ctx = saved;
  return true;
}

/// Answers the MenuBox: 0 cancels, n >= 1 accepts option n-1.
///
/// The guest's accept (`sub_823DC720`) re-checks the item's enabled byte, fires the callback,
/// publishes the result and tears the box down, so the host writes the index and makes ONE
/// call. Cancel sets the cancelled byte and calls the close, which publishes value 0.
bool DispatchMenuBoxAnswer(PPCContext& ctx, uint8_t* base, uint32_t answer) {
  const uint32_t control = MenuBoxController(base);
  if (control == 0u) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxAnswer:{} with no menu box — dropped", answer);
    return true;
  }

  const PPCContext saved = ctx;

  if (answer == 0u) {
    base[control + 13u] = 1u;  // the cancelled byte the close reads for its published value
    ctx.r3.u32 = control;
    rex_lhMenuBox_Close(ctx, base);
    ctx = saved;
    return true;
  }

  const uint32_t index = answer - 1u;
  const uint32_t begin = LoadGuestU32(base, control + 20u);
  const uint32_t end = LoadGuestU32(base, control + 24u);
  const uint32_t count = (begin == 0u || end < begin) ? 0u : (end - begin) / 8u;
  if (index >= count) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxAnswer:{} past {} option(s) — dropped",
                answer, count);
    return true;
  }

  StoreGuestU32(base, control + 0u, index);
  ctx.r3.u32 = control;
  rex_lhMenuBox_Accept(ctx, base);
  ctx = saved;
  return true;
}

/// Hands one action code to whichever display box is currently up.
///
/// A box's reply is not a return value: its handler posts a MESSAGE EVENT the scripts read with
/// `MessageEvents.IsMessagePosted`, carrying the answer as ExtraData (type 36 for a yes/no with
/// 1 or 0, 37/38 for the numeric adjuster with the chosen number). Only running the guest's own
/// handler produces that.
///
/// The codes: 46 confirms and 47 cancels on the query box and the adjuster; 28/29 step the
/// adjuster's value; 36 dismisses the quest box and the message box. Every handler gates on the
/// box's own open byte, so a late code is a no-op.
bool DispatchDisplayBoxAction(PPCContext& ctx, uint8_t* base, uint32_t code) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no GUI — dropped", code);
    return true;
  }

  const uint32_t manager = LoadGuestU32(base, gui + 28u);
  if (!plausible(manager)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box manager — dropped", code);
    return true;
  }

  // `owner = *(*(GUI+28)+52)` — a dereference, as `IsDisplayBoxActive` (0x82305f10) passes it
  // to `sub_822A5C50`.
  const uint32_t owner = LoadGuestU32(base, manager + 52u);
  if (!plausible(owner)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box owner — dropped", code);
    return true;
  }

  // The box currently SHOWING, by the router's own rule — see gui/gui_display_box.h.
  const uint32_t box = refii::vfs::ShowingDisplayBox(
      owner, [base](uint32_t at) { return LoadGuestU32(base, at); });
  if (!plausible(box)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box up — dropped", code);
    return true;
  }

  const uint32_t vtable = LoadGuestU32(base, box);
  const PPCContext saved = ctx;
  ctx.r3.u32 = box;
  ctx.r4.u32 = code;
  ctx.r5.u32 = 1u;  // player 1, the same index the screen dispatch uses

  switch (refii::guest::ToTu1(vtable)) {
    case 0x820AE688u: rex_lhDisplayQueryBox_HandleAction(ctx, base); break;  // QueryBox / YesNo
    case 0x820AE6A8u: rex_lhDisplayNumericAdjuster_HandleAction(ctx, base); break;  // NumericAdjuster
    case 0x820AE6C8u: rex_lhDisplayQuestBox_HandleAction(ctx, base); break;  // QuestBoxParams
    case 0x820AE668u: rex_lhDisplayMessageBox_HandleAction(ctx, base); break;  // MessageBox / Download / Divorce
    default:
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: BoxAction:{} — box 0x{:08X} has vtable 0x{:08X}, "
                  "which is not one this dispatches (the acceptance and button boxes take "
                  "their input elsewhere)",
                  code, box, vtable);
      return true;
  }

  ctx = saved;
  return true;
}

/// Answers the front end's `dialog` box. Every front-end warning goes through `sub_826E40E0`
/// (the main-menu root reaches it via slot +32 `sub_826E1C50`, the title directly), which
/// writes `dialog.Group.OK` / `yousure` / `cancel` and sets `+57`; the dialog's DIALOG_ACTIVE
/// event puts the root in mode 6 (`sub_826E1040`). The answer is the owner's own input slot
/// (+12): root `sub_826DEE40` (36 accept, 37 cancel when `+206`), title `sub_826E2F18` (36),
/// each taking only the pad in `dword_83496AF8`. Objects: `F = *(*(*(0x83496AC0)+8)+0x50)`,
/// root `*(F+0xB8)`, title `*(F+0xB4)` (0x8230FF60..0x8230FF98).
bool DispatchFrontEndDialog(PPCContext& ctx, uint8_t* base, uint32_t code) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t app = LoadGuestU32(base, refii::guest::Address(0x83496AC0u));
  const uint32_t shell = plausible(app) ? LoadGuestU32(base, app + 8u) : 0u;
  const uint32_t front = plausible(shell) ? LoadGuestU32(base, shell + 0x50u) : 0u;
  if (!plausible(front)) {
    REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{} with no front end — dropped", code);
    return true;
  }

  for (const uint32_t offset : {0xB8u, 0xB4u}) {
    const uint32_t owner = LoadGuestU32(base, front + offset);
    if (!plausible(owner) || refii::vfs::LoadGuestU8(base, owner + 57u) == 0u) {
      continue;
    }

    const uint32_t vtable = LoadGuestU32(base, owner);
    const PPCContext saved = ctx;
    ctx.r3.u32 = owner;
    ctx.r4.u32 = code;
    ctx.r5.u32 = LoadGuestU32(base, refii::guest::Address(0x83496AF8u));
    switch (refii::guest::ToTu1(vtable)) {
      case 0x820D0934u: rex_lhGui_FrontendRoot_OnScreenAction(ctx, base); break;  // main-menu root
      case 0x820D09C4u: rex_lhGui_FrontendTitle_OnScreenAction(ctx, base); break;  // title
      default:
        ctx = saved;
        REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{} — owner 0x{:08X} has vtable "
                    "0x{:08X}, not a dialog owner", code, owner, vtable);
        return true;
    }
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{} answered 0x{:08X}", code, owner);
    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{} with no dialog up — dropped", code);
  return true;
}

/// Hands one action code to a display MessageBox the host was told opened (vtable 0x820AE668,
/// see hooks/display_box_hooks.cpp), BY ADDRESS — the stated box, not whichever the router
/// would pick. The box is re-checked here, since the host's word is a frame old: still this
/// class and still open. Its handler `sub_823CEDD8` takes the pad from `box+24`, -1 for any
/// (0x823cee00).
bool DispatchStatedMessageBoxAction(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t code) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (!plausible(box) || LoadGuestU32(base, box) != refii::guest::Address(0x820AE668u) ||
      refii::vfs::LoadGuestU8(base, box + 12u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{}@{:08X} — no open MessageBox there, dropped",
                code, box);
    return true;
  }

  const uint32_t pad = LoadGuestU32(base, box + 24u);
  const PPCContext saved = ctx;
  ctx.r3.u32 = box;
  ctx.r4.u32 = code;
  ctx.r5.u32 = pad == 0xFFFFFFFFu ? 1u : pad;
  rex_lhDisplayMessageBox_HandleAction(ctx, base);
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: BoxAction:{}@{:08X} — answered the stated MessageBox",
              code, box);
  return true;
}

/// The in-game GUI's roots, or false when no in-game screen is up.
bool InGameScreenRoots(uint8_t* base, uint32_t* out_owner, uint32_t* out_screen_id) {
  const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != kInGameGuiVtable) {
    return false;
  }

  const uint32_t manager = LoadGuestU32(base, gui + 56u);
  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!plausible(manager) || !plausible(owner)) {
    return false;
  }

  // The modal latch must be idle: a box owns input while it is up, and its own lever answers.
  if (LoadGuestU32(base, owner + 40u) != 1u) {
    return false;
  }

  if (out_owner != nullptr) *out_owner = owner;
  if (out_screen_id != nullptr) *out_screen_id = refii::guest::ScreenIdToTu1(LoadGuestU32(base, manager + 4u));
  return true;
}

/// Shows the HUD again on the edges where the guest's own machine would have.
///
/// THE HUD'S VISIBILITY IS A MODE OBJECT, `M = *(GUI + 68)`, played by `sub_821764D8` each GUI
/// tick: every in-game screen open (`sub_8236E630` -> `sub_82405678`) puts the HUD root on
/// slide 2 and stores `*M = 4, M+5 = 0`; the guest's own screen EXIT (`sub_8236ED98`,
/// 0x8236f0a0..0x8236f0bc) stores `*M = 3, M+5 = 1`, and three ticks later the machine plays
/// root slide 1 — `sub_82426BA8(sub_82426518(pres), pres, 1)` on `pres = *(*(GUI+32)+4)`
/// (0x82176770..84) — and sets `M+4 = M+5`. Nothing on the level-load path writes M, and the
/// level's fresh HUD instance comes up on its authored (hidden) pose while M still reads
/// "shown, idle" (peeked live: *M 0, M+4 1, M+5 1 with UpperElements dark). Godot owns the
/// in-game screens, so the exit never runs for us either. Hence EDGES, not M: a screen just
/// closed, or the HUD presentation is a new instance — then the root slide is played the
/// guest's way and M is left exactly as its machine leaves it. The machine's own fade wait
/// (`sub_82387CE0`, a SleepEx loop) is not entered.
void ArmHudShow(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kNoScreen = 31u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  static uint32_t last_screen = kNoScreen;
  static uint32_t last_presentation = 0u;

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id)) {
    return;
  }

  const bool closed = last_screen != kNoScreen && screen_id == kNoScreen;
  last_screen = screen_id;
  if (screen_id != kNoScreen) {
    return;
  }

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t presentation = plausible(hud) ? LoadGuestU32(base, hud + 4u) : 0u;
  const uint32_t mode = plausible(gui) ? LoadGuestU32(base, gui + 68u) : 0u;
  if (!plausible(presentation) || !plausible(mode)) {
    return;
  }

  const bool fresh = presentation != last_presentation;
  last_presentation = presentation;
  if (!closed && !fresh) {
    return;
  }

  if (static_cast<int32_t>(LoadGuestU32(base, mode)) > 0) {
    return;  // the guest's own transition is playing; it will land on its own
  }

  PPCContext call = ctx;
  call.r3.u32 = presentation;
  rex_lhGuiPresentation_GetRootElement(call, base);
  const uint32_t root = call.r3.u32;
  if (!plausible(root)) {
    REXLOG_INFO("Refii guest-tick pump: HUD show — the HUD presentation names no root element");
    return;
  }

  call = ctx;
  call.r3.u32 = root;
  call.r4.u32 = presentation;
  call.r5.u32 = 1u;
  rex_lhGuiElement_GoToSlide(call, base);

  StoreGuestU32(base, mode, 0u);
  StoreGuestU8(base, mode + 4u, 1u);
  StoreGuestU8(base, mode + 5u, 1u);
  REXLOG_INFO("Refii guest-tick pump: HUD shown — root slide 1 played on HUD presentation 0x{:08X} "
              "({}), mode object left shown/idle",
              presentation, closed ? "a screen closed" : "a fresh HUD instance");
}

/// The guest's directional action codes — `sub_824047E8`'s cases. 0x1C..0x1F are the presses
/// ("held") and 0x20..0x23 the matching releases, same Up/Down/Left/Right order. Send BOTH: the
/// Lua stick state is a latch, so a press with no release scrolls for ever.
enum class MenuDirection : uint32_t { Up = 0x1Cu, Down = 0x1Du, Left = 0x1Eu, Right = 0x1Fu };

constexpr uint32_t kDirectionReleaseOffset = 4u;

/// Feeds one directional press or release to the guest's own stick router.
///
/// `sub_82404ED0` (held) and `sub_82405030` (released) both read the code through their SECOND
/// argument and never touch their first, so a scratch word is the whole call; the `String1`
/// write, the eligibility gate and the context resolution stay the guest's.
bool DispatchMenuDirection(PPCContext& ctx, uint8_t* base, MenuDirection direction, bool held) {
  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id)) {
    REXLOG_INFO("Refii guest-tick pump: MenuScroll with no in-game menu — dropped");
    return true;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x80u) & ~0xFu;
  const uint32_t code_word = frame + 0x20u;

  const uint32_t code =
      static_cast<uint32_t>(direction) + (held ? 0u : kDirectionReleaseOffset);
  StoreGuestU32(base, code_word, code);

  ctx.r1.u32 = frame;
  ctx.r3.u32 = 0u;  // the dispatchers ignore it; the guest's own callers pass a router
  ctx.r4.u32 = code_word;
  if (held) {
    rex_lhGuiInput_OnDirectionHeld(ctx, base);
  } else {
    rex_lhGuiInput_OnDirectionReleased(ctx, base);
  }

  ctx = saved;
  return true;
}

/// Answer `g_Menu:IsHighlightedItemAtRoot()` for the guest, because it cannot answer itself.
///
/// The predicate is unconditionally stale: `sub_826C1D98` posts the chunk that computes the
/// answer and reads `Float1` back about ten instructions later, with no reentrancy in between,
/// so no flush can land there. It reads whatever last wrote that slot — an earlier Lua answer,
/// the row index `sub_826CE8D0` writes before every `SpawnButton()`, or the `.bgf` default.
/// (`guiObject+0x64` is a per-TICK window: `sub_821A1140` re-opens it every frame at
/// `0x821A11D8` and `sub_82286FD8` closes it after flushing.)
///
/// So the host writes the answer — it owns the fold and the cursor. Both `sub_826C63C8`'s
/// `if (sub_826C1D98())` and `sub_826D5AF0`'s re-test then read it, and the guest performs its
/// own close with all its side effects (un-pause, screen-0 hand-back, `*(mgr+152) = 31`,
/// slide-3 transition).
///
/// `Float1` is a SHARED SCRATCH SLOT, not a return channel — `sub_826CE8D0` writes it before
/// every row spawn and `sub_826C1A50` writes 0/1 for emphasis — so write it immediately before
/// each dispatch, never once at setup.
void WriteAtRootAnswer(PPCContext& ctx, uint8_t* base, bool at_root) {
  const PPCContext saved = ctx;

  // The literal, then the LHString, then the write — the same three-step the chunk runner uses.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x100u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t handle = frame + 0x60u;

  static const char kName[] = "Float1";
  for (std::size_t i = 0; i < sizeof(kName); ++i) {
    base[text + i] = static_cast<uint8_t>(kName[i]);
  }
  StoreGuestU32(base, handle, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  rex_lhCharString_CtorFromCStr(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = 0u;      // `this` — read by nothing on this path
  work.r4.u32 = handle;  // the name
  work.r5.u32 = 0u;      // read by nothing
  work.r6.u32 = 0u;      // 0 = whichever presentation the GUI is currently driving
  work.f1.f64 = at_root ? 1.0 : 0.0;
  rex_lhGuiPresentation_SetFloatParam(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  rex_lhCharString_Dtor(work, base);

  ctx = saved;
}

/// One action code to the current in-game screen's own input method (vtable+24).
///
/// Named statically per screen id because the recompiler emits no indirect-call path; the id is
/// the guest's own `mgr+4` and each handler comes from the guest's own map. The third argument
/// is the PLAYER INDEX (1 or 2) — gift, selection and decoration `bnelr` unless it is 1.
bool DispatchScreenAction(PPCContext& ctx, uint8_t* base, uint32_t code, bool at_root) {
  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id)) {
    REXLOG_INFO("Refii guest-tick pump: ScreenAction:{} with no in-game screen — dropped", code);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = refii::guest::ScreenId(screen_id);  // the guest's map is keyed by ITS id
  rex_lhGuiManager_FindScreenHandler(ctx, base);
  const uint32_t handler = ctx.r3.u32;
  if (handler < 0x10000u || handler >= 0xC0000000u) {
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: ScreenAction:{} — screen {} has no handler",
                code, screen_id);
    return true;
  }

  // The back predicate, answered before the handler reads it: code 37 is the only one that
  // consults `IsHighlightedItemAtRoot()` (in the handler and again inside `sub_826D5AF0`) and
  // it cannot compute it. See WriteAtRootAnswer.
  if (code == 37u) {
    WriteAtRootAnswer(ctx, base, at_root);
    REXLOG_INFO("Refii guest-tick pump: ScreenAction:37 on screen {} with atRoot={} written "
                "to Float1 — the guest's own predicate cannot compute it",
                screen_id, at_root ? 1 : 0);
  }

  ctx.r3.u32 = handler;
  ctx.r4.u32 = code;
  ctx.r5.u32 = 1u;  // player 1

  switch (screen_id) {
    case 14u: rex_lhGuiAbilitiesScreen_OnScreenAction(ctx, base); break;
    case 15u: rex_lhGuiItemsScreen_OnScreenAction(ctx, base); break;
    case 16u: rex_lhGuiClothingScreen_OnScreenAction(ctx, base); break;
    case 17u: rex_lhGuiLogbookScreen_OnScreenAction(ctx, base); break;
    case 18u: rex_lhGuiOptionsScreen_OnScreenAction(ctx, base); break;
    case 21u: rex_lhGuiCrimeScreen_OnScreenAction(ctx, base); break;
    case 22u: rex_lhGuiShopScreen_OnScreenAction(ctx, base); break;
    case 23u: rex_lhGuiWorldMapScreen_OnScreenAction(ctx, base); break;
    case 25u: rex_lhGuiGiftGivingScreen_OnScreenAction(ctx, base); break;
    case 26u: rex_lhGuiSelectionScreen_OnScreenAction(ctx, base); break;
    case 27u: rex_lhGuiDecorationScreen_OnScreenAction(ctx, base); break;
    case 10u: rex_lhGuiQueryScreen_OnScreenAction(ctx, base); break;
    case 28u: rex_lhGuiRealEstateScreen_OnScreenAction(ctx, base); break;
    case 11u: rex_lhGuiMinigameCompletedScreen_HandleAction(ctx, base); break;
    case 12u: rex_lhGuiQuestCompletionScreen_HandleAction(ctx, base); break;
    default:
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: ScreenAction:{} — screen {} is not owned",
                  code, screen_id);
      return true;
  }

  ctx = saved;
  return true;
}

/// Answers the pending query box through the guest's own input router.
///
/// The CODE is chosen host-side PER BOX ID — `sub_826A7738` answers 46/47 for the saving
/// boxes while the henchman boxes answer 36/37, the fast-travel confirm is 39, the shop's
/// equip action 75 — and the router forwards it to the UNDERLYING screen handler's
/// vtable+28, so every side effect (the save, the purchase, the kick, the close sounds)
/// runs inside the guest's own handler. Never call the close primitives
/// (`sub_8236FEC8/FE38`) instead: they only close, skipping those side effects.
///
/// Drops when stale: the post is only made while a box is up. Gated on the modal latch being
/// non-idle — modes 2 (MessageBox), 3 (QueryBox) and 5 (numeric adjuster) all answer through
/// this router, whose own guard re-checks everything inside.
/// Writes one d-pad slot's will, resolving the hero's CECSpellManager first.
///
/// The chain is `sub_823B96E0`'s own, read rather than called (0x823B970C..0x823B9794):
///
///     v5   = *(*(*(0x83496AB8 + 12) + 88) + 4)
///     hero = *(*(v5) + 108)                       // one more load than it looks
///     guards: byte[hero+144] & 0x40, byte[hero+40] & 1
///     mgr  = component 56 of hero — the cached slot at hero+140, else the sorted
///            {id, pointer} pairs at hero+72 .. hero+76
///
/// Resolved here on the guest thread, not passed in: a pointer read on Godot's thread would be
/// a frame old, and a level change frees the hero underneath it. The RANGE check is ours — the
/// writer validates nothing, so slot > 4 scribbles past the assignment array and spell > 7 puts
/// an index with no icon-table entry on the d-pad.
bool DispatchSpellAssign(PPCContext& ctx, uint8_t* base, uint32_t slot, uint32_t spell) {
  constexpr uint32_t kSlots = 5u;
  constexpr uint32_t kSpells = 8u;
  constexpr uint32_t kSpellManagerComponent = 56u;
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);

  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  if (slot >= kSlots || spell >= kSpells) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{} is out of range — dropped", slot,
                spell);
    return true;
  }

  // `dword_83496AB8` is IDA's name for the WORD AT 0x83496AB8 — the game root is behind one
  // load, and `+12` is measured from that (the rule every other reader here follows).
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t roster = plausible(party) ? LoadGuestU32(base, party) : 0u;
  const uint32_t entry = plausible(roster) ? LoadGuestU32(base, roster + 108u) : 0u;
  const uint32_t hero = plausible(entry) ? LoadGuestU32(base, entry) : 0u;

  if (!plausible(hero) || (LoadGuestU8(base, hero + 144u) & 0x40u) == 0u ||
      (LoadGuestU8(base, hero + 40u) & 0x01u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{} with no hero — dropped", slot, spell);
    return true;
  }

  uint32_t manager = 0u;
  const uint32_t cache = LoadGuestU32(base, hero + 140u);
  const uint32_t table = LoadGuestU32(base, hero + 72u);
  if (plausible(cache) && plausible(table)) {
    manager = LoadGuestU32(
        base, table + (static_cast<uint32_t>(LoadGuestU8(base, cache + 56u)) << 3) + 4u);
  } else if (plausible(table)) {
    const uint32_t end = LoadGuestU32(base, hero + 76u);
    if (end > table && (end - table) <= 0x2000u) {
      for (uint32_t at = table; at < end; at += 8u) {
        if (LoadGuestU32(base, at) == kSpellManagerComponent) {
          manager = LoadGuestU32(base, at + 4u);
          break;
        }
      }
    }
  }

  if (!plausible(manager)) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{} with no CECSpellManager — dropped",
                slot, spell);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = manager;
  ctx.r4.u32 = 0u;    // the guest passes 0 here from every one of its own call sites
  ctx.r5.u32 = slot;
  ctx.r6.u32 = spell;
  ctx.r7.u32 = 1u;    // notify: fire the UI/net event for the local player

  rex_CECSpellManager_SetSpellSlot(ctx, base);

  ctx = saved;
  return true;
}

// Defined with the menu-event machinery below; the world map's select levers deliver through it.
bool DispatchMenuEventDirect(PPCContext& ctx, uint8_t* base, const std::string& name,
                             uint32_t type, uint32_t id);

/// Builds one of the world map's guest-side lists into a 16-byte vector on a claimed frame
/// (+4 begin, +8 end), runs `visit` over it, and frees it the way the guest does.
template <typename Visit>
void WithGuestVector(PPCContext& ctx, uint8_t* base, void (*build)(PPCContext&, uint8_t*),
                     void (*release)(PPCContext&, uint8_t*), Visit visit) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t vector = frame + 0x20u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, vector + (word * 4u), 0u);
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = vector;
  build(ctx, base);
  ctx = saved;

  const uint32_t begin = LoadGuestU32(base, vector + 4u);
  const uint32_t end = LoadGuestU32(base, vector + 8u);
  if (plausible(begin) && end > begin && (end - begin) <= 0x8000u) {
    visit(begin, end);
  }

  if (LoadGuestU32(base, vector + 4u) != 0u) {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = vector;
    release(ctx, base);
    ctx = saved;
  }
}

/// `Quest:select:<record>` — see kActionQuestPrefix. Quests first (event 42), then jobs (43).
bool DispatchQuestSelect(PPCContext& ctx, uint8_t* base, uint32_t record) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t ordinal = 0xFFFFFFFFu;
  uint32_t type = 0u;
  std::string name;
  const auto find = [&](uint32_t begin, uint32_t end) {
    for (uint32_t at = begin, index = 0; at + 4u <= end; at += 4u, ++index) {
      const uint32_t quest = LoadGuestU32(base, at);
      if (plausible(quest) && LoadGuestU32(base, quest + 4u) == record) {
        ordinal = index;
        const uint32_t handle = LoadGuestU32(base, quest);  // +0 QuestName
        const uint32_t chars = plausible(handle) ? LoadGuestU32(base, handle) : 0u;
        for (uint32_t i = 0; plausible(chars) && i < 96u; ++i) {
          const char c = static_cast<char>(LoadGuestU8(base, chars + i));
          if (c == 0) {
            break;
          }
          name.push_back(c);
        }
        break;
      }
    }
  };

  WithGuestVector(ctx, base, rex_lhWorldMap_BuildQuestList, rex_lhPodVector_FreeStorage, find);
  if (ordinal != 0xFFFFFFFFu) {
    type = 42u;
  } else {
    WithGuestVector(ctx, base, rex_lhWorldMap_BuildJobList, rex_lhPodVector_FreeStorage, find);
    if (ordinal != 0xFFFFFFFFu) {
      type = 43u;
    }
  }

  if (type == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Quest:select:{:08X} is in neither the quest nor the job "
                "list — dropped", record);
    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: Quest:select:{:08X} is '{}' at ordinal {} — event {} to "
              "screen 23", record, name, ordinal, type);
  return DispatchMenuEventDirect(ctx, base, name, type, ordinal);
}

/// `Sale:select:<nameTagHash>:<shortage>` — see kActionSaleSelectPrefix.
bool DispatchSaleSelect(PPCContext& ctx, uint8_t* base, uint32_t key, bool shortage) {
  uint32_t ordinal = 0xFFFFFFFFu;
  WithGuestVector(ctx, base, rex_lhWorldMap_BuildSalesList, rex_lhWorldMapSaleVector_Destroy, [&](uint32_t begin, uint32_t end) {
    constexpr uint32_t kRow = 44u;
    for (uint32_t at = begin, index = 0; at + kRow <= end; at += kRow, ++index) {
      if (LoadGuestU32(base, at) != key) {
        continue;
      }
      const uint32_t bits = LoadGuestU32(base, at + 16u);
      float multiplier = 1.0f;
      std::memcpy(&multiplier, &bits, sizeof(multiplier));
      if ((multiplier > 1.0f) == shortage) {
        ordinal = index;
        break;
      }
    }
  });

  if (ordinal == 0xFFFFFFFFu) {
    REXLOG_INFO("Refii guest-tick pump: Sale:select:{:08X}:{} is not in the sales list — dropped",
                key, shortage ? 1 : 0);
    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: Sale:select:{:08X}:{} at ordinal {} — event 44 to screen 23",
              key, shortage ? 1 : 0, ordinal);
  char label[32];
  std::snprintf(label, sizeof(label), "Sale:%08X", key);
  return DispatchMenuEventDirect(ctx, base, label, 44u, ordinal);
}

/// Runs one Lua chunk on the GUI's current presentation. Returns false when there is none.
///
/// The chunk is built into a scratch frame on the guest stack: `sub_8222CED0` wants a
/// NUL-terminated guest `char*`, and the LHString it produces must be released with
/// `sub_82214DD8`.
bool RunGuestLua(PPCContext& ctx, uint8_t* base, const std::string& chunk) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui)) {
    return false;
  }

  const uint32_t stack = LoadGuestU32(base, gui + 56u);
  if (!plausible(stack)) {
    return false;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = stack;
  rex_lhGuiPresentationSet_GetCurrent(ctx, base);
  const uint32_t presentation = ctx.r3.u32;
  ctx = saved;

  if (!plausible(presentation)) {
    return false;
  }

  // A chunk that cannot run is not posted at all. `sub_82426470` picks immediate or deferred
  // off one byte at `guiObject+0x64`: set calls the presentation's vtable+20 here, clear appends
  // to the command list at `GUI+88`. The GUI constructor sets it (0x8237119c) and the tail of
  // the GUI frame update clears it (`stb r9, 0x64(r30)`, 0x82287170) and never sets it back, so
  // from the second update on only the deferred arm is taken — and nothing drains that list.
  // Skipping avoids entering the guest (and its allocator lock) to do nothing.
  if (LoadGuestU8(base, gui + 100u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: guiObject+0x64 is clear, so '{}' would be DEFERRED onto "
                "the list at GUI+88 that nothing drains while it stays clear — not posted. The "
                "commit has to reach the screen's handler another way.",
                chunk);
    return false;
  }

  // Claimed by lowering r1, so the callee's own frame cannot land on the text it was handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t handle = frame + 0x1A0u;

  constexpr uint32_t kMaxChunk = 0x17Fu;
  if (chunk.size() + 1u > kMaxChunk) {
    REXLOG_INFO("Refii guest-tick pump: Lua chunk is too long ({} bytes) — dropped",
                chunk.size());
    return false;
  }

  for (std::size_t i = 0; i < chunk.size(); ++i) {
    base[text + i] = static_cast<uint8_t>(chunk[i]);
  }

  base[text + chunk.size()] = 0;
  StoreGuestU32(base, handle, 0u);

  // Logged before each call, not after the group: all three enter the guest and two are
  // allocator operations (the LHString constructor reaches `operator new` via
  // `sub_8222CED0` -> `sub_822078A0` -> 0x8221F258, the destructor the matching free), both
  // taking the guest heap lock at `sub_822D6C88`, a spin with no yield. A hang in any one of
  // them is otherwise indistinguishable.
  const auto step = [&](const char* what) {
    REXLOG_INFO("Refii guest-tick pump: Lua chunk step '{}' for {}", what, chunk);
  };

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  step("LHString ctor");
  rex_lhCharString_CtorFromCStr(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = presentation;
  step("push");
  rex_lhGui_RunLuaString(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  step("LHString dtor");
  rex_lhCharString_Dtor(work, base);

  step("done");
  return true;
}

/// One commit waiting to be confirmed on the NEXT tick — see DispatchMenuEvent.
struct MenuEventPending {
  std::string name;
  uint32_t type = 0;
  uint32_t id = 0;
  int depth_after_push = -1;
};

std::optional<MenuEventPending> _menuEventPending;

/// The GUI event queue's depth, as the Lua side reports it. -1 when it cannot be read.
///
/// `sub_82426470` swallows every failure, so this reads the attribute `GUI:PushButtonEvent`
/// writes on its way out (`setAttribute(g_Param.element, "EventQueueSize",
/// #GUI.State.EventQueue.Name)`): a push that does not move it did not run.
int GuestEventQueueSize(PPCContext& ctx, uint8_t* base) {
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x80u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t handle = frame + 0x60u;

  static constexpr char kName[] = "EventQueueSize";
  for (std::size_t i = 0; i < sizeof(kName); ++i) {
    base[text + i] = static_cast<uint8_t>(kName[i]);
  }

  StoreGuestU32(base, handle, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  rex_lhCharString_CtorFromCStr(work, base);

  // The presentation is NOT optional here. `sub_826B9510`/`sub_826B9440` fall back to
  // `sub_823DCBE0(*(GUI+56))` when handed 0; this getter dereferences it unconditionally
  // (`lwz r11, 0(r29)` / `lwz r10, 0x38(r11)` / `bctrl` at 0x826B9228), so 0 faults.
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t stack = plausible(gui) ? LoadGuestU32(base, gui + 56u) : 0u;
  if (!plausible(stack)) {
    work.r1.u32 = frame;
    work.r3.u32 = handle;
    rex_lhCharString_Dtor(work, base);
    return -1;
  }
  work.r1.u32 = frame;
  work.r3.u32 = stack;
  rex_lhGuiPresentationSet_GetCurrent(work, base);
  const uint32_t presentation = work.r3.u32;
  if (!plausible(presentation)) {
    work.r1.u32 = frame;
    work.r3.u32 = handle;
    rex_lhCharString_Dtor(work, base);
    return -1;
  }

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = presentation;
  rex_lhGuiPresentation_GetFloatParam(work, base);
  const double depth = work.f1.f64;

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  rex_lhCharString_Dtor(work, base);

  return depth < 0.0 || depth > 4096.0 ? -1 : static_cast<int>(depth);
}

/// Runs the current screen's own event handler on a host-built event.
///
/// The Lua route cannot work: `GUI:PushButtonEvent` reaches the guest through `sub_82426470`,
/// which runs a chunk immediately only while `guiObject+0x64` is set — set once by the GUI
/// constructor (`0x8237119c`), cleared by the first frame update (`0x82287170`), never set
/// again — so from the second update on every chunk lands on the undrained list at `GUI+88`.
///
/// This does what the guest's own drain does, one step later: `sub_82370450` builds a 12-byte
/// `{LHString name, int32 type, int32 id}` and calls the screen's `vtable+20` (`0x823705E0`).
/// Everything downstream (the arming check at `screen+198`, the purchase, the message box) is
/// the guest's own code. The slot is READ from the live object's vtable and compared against
/// the verified class; anything else is refused.
///
/// Returns false when the screen is not one we can dispatch to, so the caller can fall back.
/// Writes one float parameter on the current presentation — `sub_826B9440(value, this, &name,
/// unused, presentation)`; 0 for the presentation means "whichever the GUI is driving".
void WriteParameterFloat(PPCContext& ctx, uint8_t* base, uint32_t frame, const char* name,
                         double value) {
  const uint32_t text = frame + 0x20u;
  const uint32_t handle = frame + 0x160u;
  std::size_t i = 0;
  for (; name[i] != 0 && i < 0x3Fu; ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }
  base[text + i] = 0;
  StoreGuestU32(base, handle, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  rex_lhCharString_CtorFromCStr(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = 0u;
  work.r4.u32 = handle;
  work.r5.u32 = 0u;
  work.r6.u32 = 0u;
  work.f1.f64 = value;
  rex_lhGuiPresentation_SetFloatParam(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  rex_lhCharString_Dtor(work, base);
}

/// Writes one string parameter — `sub_826B9510(unused, &name, &value, presentation)`: r4 is
/// hashed as the ATTRIBUTE NAME, r5 is the value copied in (the guest's own `sub_826DEE40`
/// case 28 passes `&"String1", &"Up"` in that order).
void WriteParameterString(PPCContext& ctx, uint8_t* base, uint32_t frame, const char* name,
                          const std::string& value) {
  const uint32_t nameText = frame + 0x20u;
  const uint32_t valueText = frame + 0x60u;
  const uint32_t nameHandle = frame + 0x160u;
  const uint32_t valueHandle = frame + 0x168u;

  std::size_t i = 0;
  for (; name[i] != 0 && i < 0x3Fu; ++i) {
    base[nameText + i] = static_cast<uint8_t>(name[i]);
  }
  base[nameText + i] = 0;

  const std::size_t n = value.size() < 0xFFu ? value.size() : 0xFFu;
  for (std::size_t k = 0; k < n; ++k) {
    base[valueText + k] = static_cast<uint8_t>(value[k]);
  }
  base[valueText + n] = 0;
  StoreGuestU32(base, nameHandle, 0u);
  StoreGuestU32(base, valueHandle, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  work.r4.u32 = nameText;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  rex_lhCharString_CtorFromCStr(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = valueHandle;
  work.r4.u32 = valueText;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  rex_lhCharString_CtorFromCStr(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = 0u;
  work.r4.u32 = nameHandle;
  work.r5.u32 = valueHandle;
  work.r6.u32 = 0u;
  rex_lhGuiPresentation_SetStringParam(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = valueHandle;
  rex_lhCharString_Dtor(work, base);
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  rex_lhCharString_Dtor(work, base);
}

uint32_t PlayerObject(uint8_t* base);

/// Both are defined further down this file; the inventory dispatch below needs them here.
uint32_t HeroEntity(uint8_t* base);
uint32_t HeroComponent(uint8_t* base, uint32_t entity, uint32_t id);
std::string ReadStatedWideLhString(uint8_t* base, uint32_t handle);

/// `Inventory:<op>:<recordHash>` — WEAR, GIFT or USE an item the hero already has, by its GDB record
/// hash, with the guest's menus taking no part at all.
///
/// This is the seam that lets Godot own the item lists outright. Everything else on this screen
/// goes through `GUI:PushButtonEvent` into the current screen's `vtable+20`, which means the
/// guest's Gameface menu has to exist and have been populated first. These two do not: they are
/// the guest's own SCRIPT-COMMAND level entry points, bound as `AddAndReplaceItemOfRecordID` and
/// `Player:GiveGift`, and both are self-contained. Decompiled and verified rather than taken on
/// report:
///
///   sub_824BF068(comp15, hash)   `sub_821DA348` GDB lookup (0x824BF080), binary search for
///                                component 13 (0x824BF11C), `sub_824F1C90` -> EntityRef
///                                (0x824BF1B8), `sub_824BECF8` wear (0x824BF1D0),
///                                `sub_82764DC0` release (0x824BF1E4)
///   sub_823F32A0(comp33, hash)   the same shape via `sub_824F1C18` (0x823F33DC) and
///                                `sub_823F2718` give (0x823F33E8)
///
/// Both take the component and the hash and manage the refcounted handle themselves, so the host
/// supplies only what it already reads during its inventory walk. The record hash is a LOOKUP KEY
/// INTO THE HERO'S OWN INVENTORY, not a spawn — it cannot conjure an item the hero lacks.
///
/// Runs on the pump, i.e. on the guest's tick thread, because everything below it does.
/// `Inventory:dye:<dye record>:<target record>:<slot>` — the dye screen's own answer, without
/// the screen. `sub_826D3108(screen16, slot)` is what a press runs (events 84/86/87/88 with slot
/// 0, button 39 with slot 1) and it reads two item refs off the screen, the chosen dye at
/// screen16+228 and the focused target at screen16+244. Its body is reproduced with the same
/// calls, in the same order, on refs this builds:
///
///   sub_826CB120(_, target, slot)   the target's def has `DyeTextures` > `Layer1`/`Layer2`
///                                   for that slot, else nothing happens      (0x826d3144)
///   sub_826CAFA8(_, target, slot)   the slot is already dyed                 (0x826d3160)
///     -> sub_826CAE08(_, target, slot)   scrub it: THE PRESS IS A TOGGLE     (0x826d317c)
///     else sub_826CAB78(_, dye, target, slot)   r3 unused, r4 dye ref, r5 target ref,
///                                   r6 slot, no float arguments (prologue 0x826cab88..90)
///
/// An item ref is 16 bytes {item, index, 0, 0} REGISTERED BY ITS ADDRESS on the item
/// (`sub_82764D00(item, ref)`, released by `sub_82764DC0(item, ref)`), because the setter may
/// split one unit off a stack and rebinds every registered ref to the new object
/// (`sub_823A2040`). A WORN target is taken from component 15's applied list (+0x10..+0x14,
/// 24-byte records: item +0, index +4), so the garment on the body is the one dyed; anything
/// else is looked up in the inventory by record hash (`sub_824F1C90`). The dye is not consumed.
bool DispatchDye(PPCContext& ctx, uint8_t* base, const std::string& arguments) {
  unsigned long values[3] = {0ul, 0ul, 0ul};
  const char* cursor = arguments.c_str();
  for (unsigned long& value : values) {
    char* end = nullptr;
    value = std::strtoul(cursor, &end, 10);
    if (end == cursor) {
      REXLOG_INFO("Refii guest-tick pump: 'Inventory:dye:{}' is not dye:target:slot — dropped",
                  arguments);
      return true;
    }
    cursor = *end == ':' ? end + 1 : end;
  }

  const uint32_t dye_hash = static_cast<uint32_t>(values[0]);
  const uint32_t target_hash = static_cast<uint32_t>(values[1]);
  const uint32_t slot = values[2] != 0ul ? 1u : 0u;

  const uint32_t hero = HeroEntity(base);
  const uint32_t inventory = hero != 0u ? HeroComponent(base, hero, 13u) : 0u;
  const uint32_t appearance = hero != 0u ? HeroComponent(base, hero, 15u) : 0u;
  PPCFunc* find = FindGuestFunction(refii::guest::Address(0x824F1C90u));
  PPCFunc* retain = FindGuestFunction(refii::guest::Address(0x82764D00u));
  PPCFunc* release = FindGuestFunction(refii::guest::Address(0x82764DC0u));
  PPCFunc* has_layer = FindGuestFunction(refii::guest::Address(0x826CB120u));
  PPCFunc* is_dyed = FindGuestFunction(refii::guest::Address(0x826CAFA8u));
  PPCFunc* scrub = FindGuestFunction(refii::guest::Address(0x826CAE08u));
  PPCFunc* apply = FindGuestFunction(refii::guest::Address(0x826CAB78u));
  if (inventory == 0u || appearance == 0u || find == nullptr || retain == nullptr ||
      release == nullptr || has_layer == nullptr || is_dyed == nullptr || scrub == nullptr ||
      apply == nullptr) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:dye — no hero component 13/15, or the dye "
                "calls are not callable here (hero 0x{:08X})", hero);
    return true;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t dye_ref = frame + 0x20u;
  const uint32_t target_ref = frame + 0x40u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, dye_ref + (word * 4u), 0u);
    StoreGuestU32(base, target_ref + (word * 4u), 0u);
  }

  const auto call = [&](PPCFunc* function, uint32_t r3, uint32_t r4, uint32_t r5,
                        uint32_t r6) -> uint32_t {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = r3;
    ctx.r4.u32 = r4;
    ctx.r5.u32 = r5;
    ctx.r6.u32 = r6;
    function(ctx, base);
    const uint32_t result = ctx.r3.u32;
    ctx = saved;
    return result;
  };
  const auto drop = [&](uint32_t ref) {
    if (const uint32_t item = LoadGuestU32(base, ref); item != 0u) {
      call(release, item, ref, 0u, 0u);
    }
  };

  call(find, dye_ref, inventory, dye_hash, 0u);
  if (LoadGuestU32(base, dye_ref) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:dye — dye 0x{:08X} is not in the hero's "
                "inventory", dye_hash);
    return true;
  }

  // The garment on the body first. Snapshot begin/end and re-read them: a push reallocates.
  bool worn = false;
  const uint32_t begin = LoadGuestU32(base, appearance + 0x10u);
  const uint32_t end = LoadGuestU32(base, appearance + 0x14u);
  if (LooksLikeGuestPointer(begin) && end >= begin && ((end - begin) % 24u) == 0u &&
      (end - begin) <= 24u * 64u) {
    for (uint32_t at = begin; at < end; at += 24u) {
      const uint32_t item = LoadGuestU32(base, at);
      if (LooksLikeGuestPointer(item) && LoadGuestU32(base, item + 4u) == target_hash &&
          LoadGuestU32(base, appearance + 0x10u) == begin &&
          LoadGuestU32(base, appearance + 0x14u) == end) {
        StoreGuestU32(base, target_ref, item);
        StoreGuestU32(base, target_ref + 4u, LoadGuestU32(base, at + 4u));
        call(retain, item, target_ref, 0u, 0u);
        worn = true;
        break;
      }
    }
  }
  if (!worn) {
    call(find, target_ref, inventory, target_hash, 0u);
  }

  if (LoadGuestU32(base, target_ref) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:dye — target 0x{:08X} is neither worn nor in "
                "the hero's inventory", target_hash);
    drop(dye_ref);
    return true;
  }

  if ((call(has_layer, 0u, target_ref, slot, 0u) & 0xFFu) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:dye — target 0x{:08X} states no dye layer for "
                "slot {} (sub_826CB120)", target_hash, slot + 1u);
  } else if ((call(is_dyed, 0u, target_ref, slot, 0u) & 0xFFu) != 0u) {
    call(scrub, 0u, target_ref, slot, 0u);
    REXLOG_INFO("Refii guest-tick pump: Inventory:dye — slot {} of {} target 0x{:08X} was dyed; "
                "scrubbed (sub_826CAE08)", slot + 1u, worn ? "worn" : "carried", target_hash);
  } else {
    call(apply, 0u, dye_ref, target_ref, slot);
    REXLOG_INFO("Refii guest-tick pump: Inventory:dye — dyed slot {} of {} target 0x{:08X} with "
                "0x{:08X} (sub_826CAB78)", slot + 1u, worn ? "worn" : "carried", target_hash,
                dye_hash);
  }

  drop(target_ref);
  drop(dye_ref);
  return true;
}

uint32_t ScreenObjectRO(uint8_t* base, uint32_t screen_id);

/// One guest call on a frame claimed below `saved.r1`; returns r3 and restores the context.
uint32_t CallGuestAt(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                     uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u) {
  PPCFunc* fn = FindGuestFunction(address);
  if (fn == nullptr) {
    return 0u;
  }
  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = r3;
  ctx.r4.u32 = r4;
  ctx.r5.u32 = r5;
  ctx.ctr.u32 = address;
  fn(ctx, base);
  const uint32_t result = ctx.r3.u32;
  ctx = saved;
  return result;
}

/// Puts one inventory piece on `appearance` (a component 15) the way `sub_826CB218` does:
/// `sub_824C4258` "already worn?", else `sub_824BECF8`, which strips whatever overlaps the
/// piece's `BodyAreasCovered`. Returns true when it was put on.
bool WearPiece(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
               uint32_t appearance, uint32_t handle) {
  if ((CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824C4258u), appearance, handle) & 0xFFu) != 0u) {
    return false;
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824BECF8u), appearance, handle);
  return true;
}

/// Every piece of the suit at `suit` (an inventory handle) the hero carries in `inventory`,
/// put on `appearance`: `sub_826CC618` reads `SuitParts` into a vector (begin/end/capacity at
/// +4/+8/+12, `sub_8237C1D8`) and `sub_824F1C90` finds the first owned copy of each.
void WearSuitPieces(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                    uint32_t suit, uint32_t inventory, uint32_t appearance, uint32_t* owned,
                    uint32_t* worn) {
  const uint32_t parts = frame + 0x60u;
  const uint32_t piece = frame + 0x80u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, parts + (word * 4u), 0u);
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x826CC618u), 0u, suit, parts);

  const uint32_t begin = LoadGuestU32(base, parts + 4u);
  const uint32_t end = LoadGuestU32(base, parts + 8u);
  for (uint32_t at = begin; begin != 0u && at < end; at += 4u) {
    for (uint32_t word = 0; word < 4u; ++word) {
      StoreGuestU32(base, piece + (word * 4u), 0u);
    }
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824F1C90u), piece, inventory, LoadGuestU32(base, at));
    const uint32_t item = LoadGuestU32(base, piece);
    if (item == 0u) {
      continue;
    }

    ++*owned;
    if (WearPiece(ctx, base, saved, frame, appearance, piece)) {
      ++*worn;
    }
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82764DC0u), item, piece);
  }

  if (begin != 0u) {
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8221BD38u), begin);
  }
}

/// AN OUTFIT IS ITS PIECES. Screen 16's outfit row (event 89) is answered by `sub_826D3A10`,
/// whose work is `sub_826CBF10(_, &suitHandle, creature)`: every inventory item whose record is
/// one of the suit's `SuitParts` goes through `sub_826CB218`. The suit item itself is never
/// worn; slots no piece touches stay. Neither callee reads the first argument, so 0 stands in
/// for the screen's manager.
///
/// A joiner cannot take that call: it picks the player through the `game+140` chooser, the read
/// that faulted for `unequip` on a client. It runs the same inner pair on its own hero instead.
void DressOutfit(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                 uint32_t suit, uint32_t hero, uint32_t suit_hash) {
  if (!IsCoopJoiner(base)) {
    const uint32_t dressed = CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x826CBF10u), 0u, suit) & 0xFFu;
    REXLOG_INFO("Refii guest-tick pump: Inventory:outfit — sub_826CBF10 on suit 0x{:08X}: {}",
                suit_hash, dressed != 0u ? "pieces equipped" : "the hero owns none of its pieces");
    return;
  }

  const uint32_t inventory = HeroComponent(base, hero, 13u);
  const uint32_t appearance = HeroComponent(base, hero, 15u);
  if (inventory == 0u || appearance == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:outfit — joiner hero 0x{:08X} has no "
                "component 13/15", hero);
    return;
  }

  uint32_t owned = 0;
  uint32_t worn = 0;
  WearSuitPieces(ctx, base, saved, frame, suit, inventory, appearance, &owned, &worn);
  REXLOG_INFO("Refii guest-tick pump: Inventory:outfit — joiner dressed suit 0x{:08X}: {} of {} "
              "owned piece(s) put on", suit_hash, worn, owned);
}

/// THE CLOTHING SCREEN'S MANNEQUIN. Screen 16's open (`sub_826D2B88`) spawns a
/// `CreatureHeroMannequin*` through `sub_826D7650`, cloned from the hero in the hero's clothes,
/// and keeps only its TICKET at `screen+44`; `sub_826D8008(*(GUI+8), ticket)` resolves it. The
/// preview manager `*(GUI+8)` (vtable 0x82006CC0) holds 24-byte records at `*(pm+56)..*(pm+60)`:
/// `+4` the entity holder (`*holder` = entity), `+16` the ticket, `+20` active. 0 when screen 16
/// is not up.
uint32_t ClothingMannequin(uint8_t* base) {
  const uint32_t screen = ScreenObjectRO(base, 16u);
  if (screen == 0u) {
    return 0u;
  }

  const uint32_t ticket = LoadGuestU32(base, screen + 44u);
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t pm = LooksLikeGuestPointer(gui) ? LoadGuestU32(base, gui + 8u) : 0u;
  if (ticket == 0u || !LooksLikeGuestPointer(pm) || LoadGuestU32(base, pm) != refii::guest::Address(0x82006CC0u)) {
    return 0u;
  }

  const uint32_t begin = LoadGuestU32(base, pm + 56u);
  const uint32_t end = LoadGuestU32(base, pm + 60u);
  if (!LooksLikeGuestPointer(begin) || end < begin || end - begin > 24u * 16u) {
    return 0u;
  }

  for (uint32_t rec = begin; rec + 24u <= end; rec += 24u) {
    if (LoadGuestU32(base, rec + 16u) != ticket || base[rec + 20u] == 0u) {
      continue;
    }
    const uint32_t holder = LoadGuestU32(base, rec + 4u);
    const uint32_t entity = LooksLikeGuestPointer(holder) ? LoadGuestU32(base, holder) : 0u;
    return LooksLikeGuestPointer(entity) ? entity : 0u;
  }

  return 0u;
}

/// TRYING AN OUTFIT ON — `Inventory:tryon:<record>`, the remaster's preview of the highlighted
/// outfit on screen 16's mannequin. The original changes the mannequin only on a press; in dye
/// mode it already dresses it on HIGHLIGHT (`sub_826D31E0`), and that is the mechanism used here.
///
/// First the mannequin is put back in the hero's clothes, as `sub_826D3358` does: the worn list
/// copied (`sub_824C5FC8(manq15 + 12, hero15 + 12)`) with byte +61. A list copy rebuilds nothing,
/// so the four calls `sub_824BECF8` makes after its own list change follow (`sub_824C1FC8`,
/// `sub_824C09C0`, `sub_824C1730`, `sub_824C4780`). Then every clothing slot is emptied and the
/// record's pieces go on — every owned `SuitParts` piece for a suit (category 25), the garment
/// itself otherwise. Record 0 only resets.
void TryOnMannequin(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                    uint32_t hero, uint32_t record) {
  const uint32_t mannequin = ClothingMannequin(base);
  const uint32_t dummy = mannequin != 0u ? HeroComponent(base, mannequin, 15u) : 0u;
  const uint32_t worn = HeroComponent(base, hero, 15u);
  const uint32_t inventory = HeroComponent(base, hero, 13u);
  if (dummy == 0u || worn == 0u || inventory == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:tryon — no clothing mannequin (0x{:08X}) or "
                "hero component 13/15", mannequin);
    return;
  }

  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824C5FC8u), dummy + 12u, worn + 12u);
  base[dummy + 61u] = base[worn + 61u];
  for (const uint32_t rebuild : {refii::guest::Address(0x824C1FC8u), refii::guest::Address(0x824C09C0u), refii::guest::Address(0x824C1730u), refii::guest::Address(0x824C4780u)}) {
    CallGuestAt(ctx, base, saved, frame, rebuild, dummy);
  }

  if (record == 0u) {
    return;
  }

  // ONLY THE OUTFIT. Every clothing slot (categories 17..23) is emptied first through the
  // guest's own per-slot unequip `sub_824BF4D8(comp15, category)` — it drops the worn items of
  // that category and runs the same rebuild — so a slot the outfit leaves empty, or whose piece
  // the hero does not carry, shows nothing rather than what the hero has on.
  for (uint32_t category = 17u; category <= 23u; ++category) {
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824BF4D8u), dummy, category);
  }

  const uint32_t handle = frame + 0xA0u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, handle + (word * 4u), 0u);
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824F1C90u), handle, inventory, record);
  const uint32_t item = LoadGuestU32(base, handle);
  if (item == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:tryon — record 0x{:08X} is not carried", record);
    return;
  }

  uint32_t owned = 0;
  uint32_t put = 0;
  if (LoadGuestU32(base, item + 16u) == 25u) {
    WearSuitPieces(ctx, base, saved, frame, handle, inventory, dummy, &owned, &put);
  } else {
    owned = 1u;
    put = WearPiece(ctx, base, saved, frame, dummy, handle) ? 1u : 0u;
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82764DC0u), item, handle);
  REXLOG_INFO("Refii guest-tick pump: Inventory:tryon — mannequin 0x{:08X} wears 0x{:08X}: {} of "
              "{} owned piece(s) put on", mannequin, record, put, owned);
}

bool DispatchInventoryAction(PPCContext& ctx, uint8_t* base, const std::string& body) {
  if (body.rfind("dye:", 0) == 0) {
    return DispatchDye(ctx, base, body.substr(4));
  }

  const std::size_t colon = body.rfind(':');
  if (colon == std::string::npos) {
    REXLOG_INFO("Refii guest-tick pump: 'Inventory:{}' is not op:hash — dropped", body);
    return true;
  }

  const std::string op = body.substr(0, colon);
  char* end = nullptr;
  const unsigned long hash = std::strtoul(body.c_str() + colon + 1, &end, 10);
  // `tryon:0` is the one zero that means something: put the mannequin back in the hero's clothes.
  if (end == body.c_str() + colon + 1 || *end != '\0' || (hash == 0u && op != "tryon")) {
    REXLOG_INFO("Refii guest-tick pump: 'Inventory:{}' has no record hash — dropped", body);
    return true;
  }

  // TAKING SOMETHING OFF IS A CATEGORY, NOT A RECORD, and it resolves everything itself.
  //
  // The clothing screen spawns one `INV_ITEM_CLOTHES_NONE_<CAT>` row per slot with event types
  // 90..97 (`sub_829B3340`, `sub_829B2D58`), and answers a press on one through
  // `sub_826D3F40` -> `sub_826CC430(_, eventType)`. That call reads NOTHING from the screen:
  // it takes the hero out of `dword_83496AB8`, turns the event type into a category through
  // `sub_826CC340`'s jump table (0x826CC4DC), binary-searches component 15 and hands both to
  // `sub_824BF4D8` (0x826CC5FC). Its first argument is never read — the decompile assigns r3
  // and uses it nowhere — so 0 is as good as the manager the guest passes.
  //
  // The screen calls around it (`sub_826D48F0`, `sub_826D7F90`) drive the 3D preview and the
  // presentation; both are Godot's now.
  if (op == "unequip" && IsCoopJoiner(base)) {
    // THE INNER CALL, on the joiner's own hero. `sub_826CC430` picks the player through the
    // `game+140` chooser and faulted at 0x34 on a client (see CoopRepairNetworkSlot); what it
    // does after that is `sub_824BF4D8(comp15, slot)` with slot = `sub_826CC340(type - 91)`,
    // a seven-entry table returning 17..23 (0x826CC374: `li r3, 0x11` … `0x17`) — the
    // clothing categories. Verified in the decompile; client-safe (no manager read).
    const uint32_t entity = HeroEntity(base);
    const uint32_t appearance = entity != 0u ? HeroComponent(base, entity, 15u) : 0u;
    const uint32_t slot = static_cast<uint32_t>(hash) - 74u;
    if (appearance == 0u || slot < 17u || slot > 23u) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:unequip — no appearance component or "
                  "event type {} outside 91..97", hash);
      return true;
    }

    PPCContext work = ctx;
    work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
    work.r3.u32 = appearance;
    work.r4.u32 = slot;
    rex_CECAppearanceModifiers_RemoveWornByCategory(work, base);
    REXLOG_INFO("Refii guest-tick pump: Inventory:unequip — joiner cleared slot {} on its hero "
                "0x{:08X} directly (sub_824BF4D8)", slot, entity);
    return true;
  }

  if (op == "unequip") {
    PPCFunc* unequip = FindGuestFunction(refii::guest::Address(0x826CC430u));
    if (unequip == nullptr) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:unequip — 0x826CC430 is not callable here");
      return true;
    }

    const PPCContext saved_unequip = ctx;
    ctx.r3.u32 = 0u;
    ctx.r4.u32 = static_cast<uint32_t>(hash);
    // r5 is an entity that REPLACES the chosen player when non-zero (0x826cc44c `mr r31, r5`,
    // 0x826cc4c4..cc `cmplwi r31, 0` / `mr r9, r31`); 0 takes the player.
    ctx.r5.u32 = 0u;
    ctx.ctr.u32 = refii::guest::Address(0x826CC430u);
    unequip(ctx, base);
    ctx = saved_unequip;
    REXLOG_INFO("Refii guest-tick pump: Inventory:unequip ran sub_826CC430(event type {})", hash);
    return true;
  }

  // ONLY `wear` TAKES A RECORD HASH. This was one call for all three and two of them were wrong.
  //
  // `sub_824BF068` (wear) hands r4 straight to `sub_821DA348`, which stores it and looks it up
  // with `GDBManager_FindEntryByHash` (0x821DA394) — a hash. But `sub_823E1370` (use) and
  // `sub_823F32A0` (gift) both go through `sub_824F1C18`, whose third argument is a `void***`
  // that `sub_823FE8B8` dereferences TWICE to reach characters before calling
  // `NGDB_CRecord_Get_int` (0x823FE948) — a NAME. Passing a hash there dereferences the hash as
  // a pointer, and an FNV-1 value sits squarely inside the guest's plausible address range, so
  // it reads whatever is at 0x959C9372 and resolves nothing. Silently.
  //
  // Both name-taking wrappers exist only to turn the name into a hash and then reach the SAME
  // inner pair, so this calls the inner pair directly:
  //
  //   `sub_824F1C90(&handle, component13, hash)` walks the inventory list at `+16` matching
  //   `*(item + 4) == hash` (0x824F1CE4) — the item's own record hash — and takes a reference
  //   with `sub_82764D00`. Release it with `sub_82764DC0`, as both wrappers do.
  //   Then `sub_823E0FF8(entity, &handle, "")` uses it, or `sub_823F2718(component33, &handle)`
  //   gives it. `sub_823F2718` resolves the RECIPIENT itself, off the giver's property 77 and
  //   that object's vtable+68, so no target has to be supplied.
  const uint32_t hero = HeroEntity(base);
  if (hero == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — no hero", body);
    return true;
  }

  if (op == "tryon") {
    const PPCContext saved_tryon = ctx;
    TryOnMannequin(ctx, base, saved_tryon, (ctx.r1.u32 - 0x200u) & ~0xFu, hero,
                   static_cast<uint32_t>(hash));
    ctx = saved_tryon;
    return true;
  }

  if (op == "wear") {
    const uint32_t appearance = HeroComponent(base, hero, 15u);
    PPCFunc* wear = FindGuestFunction(refii::guest::Address(0x824BF068u));
    if (appearance == 0u || wear == nullptr) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:wear — no component 15, or 0x824BF068 is "
                  "not callable here");
      return true;
    }

    const PPCContext saved = ctx;
    ctx.r3.u32 = appearance;
    ctx.r4.u32 = static_cast<uint32_t>(hash);
    ctx.ctr.u32 = refii::guest::Address(0x824BF068u);
    wear(ctx, base);
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: Inventory:wear ran sub_824BF068(component 0x{:08X}, "
                "record 0x{:08X})", appearance, static_cast<uint32_t>(hash));
    return true;
  }

  if (op != "use" && op != "gift" && op != "equip" && op != "outfit") {
    REXLOG_INFO("Refii guest-tick pump: inventory op '{}' is not one this knows", op);
    return true;
  }

  // Component 13 is the inventory the handle is looked up in; component 33 is what the gift
  // itself is performed against, and `sub_823E0FF8` wants the owning ENTITY rather than either.
  const uint32_t inventory = HeroComponent(base, hero, 13u);
  const uint32_t giving = op == "gift" ? HeroComponent(base, hero, 33u) : 0u;
  if (inventory == 0u || (op == "gift" && giving == 0u)) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — no hero component 13/33 (hero 0x{:08X})",
                body, hero);
    return true;
  }

  // `equip` is the guest's OWN activation switch, and it covers more than weapons:
  // `sub_826C9AB8` reads the item's `Category` and routes it — 1 to `sub_826CA098` (the weapon
  // slots), 5/6 and the key-item set to `sub_826CA170` (use), 17..23 to `sub_826CB218` (wear).
  // It reads no screen, no menu and no Lua: its first argument is the object at
  // `*(gameRoot + 52)`, which holds the two equipped-weapon handles at `+0` and `+16`, and both
  // of the guest's own callers fetch it from exactly that global.
  //
  // This matters beyond convenience. The screen's own answer to event 3 (`sub_826D1800`) does
  // NOT act on the row it was handed — it reads `screen+396`, a handle the screen's Update
  // refills every frame from `g_Menu:GetHighlightedMenuItemData()`. So the guest equips whatever
  // ITS cursor is on, and pushing an identity at it equips the right thing only while the two
  // cursors agree. Naming the record removes that dependency entirely.
  //
  // `use` goes through the same switch, because that is the screen's own call for it: event 3
  // runs `sub_826C9AB8(slots, screen+396, 1)` at 0x826D1BEC for every category. Categories
  // 2..4/0x10/0x18/0x1A and 5/6 reach `sub_826CA170`, which uses the item (`sub_823E0FF8`,
  // 0x826ca20c) and then, when its InventoryItemComponent states `ExitPauseMenuOnUse`
  // (0x826ca250), leaves the pause menu itself: `sub_8236ED98(*(0x83496B08 + 28), 1)` at
  // 0x826ca278. `sub_823E0FF8` alone uses the item without that close; any confirmation is the
  // item's own use script (`OnCarriedActionUse*` :Update()).
  const uint32_t activate = refii::guest::Address(0x826C9AB8u);
  const bool joiner = IsCoopJoiner(base);
  const bool switched = op == "equip" || (op == "use" && !joiner);
  PPCFunc* find = FindGuestFunction(refii::guest::Address(0x824F1C90u));
  PPCFunc* release = FindGuestFunction(refii::guest::Address(0x82764DC0u));
  PPCFunc* act = FindGuestFunction(
      op == "gift" ? refii::guest::Address(0x823F2718u) : (switched ? activate : refii::guest::Address(0x823E0FF8u)));
  if (find == nullptr || release == nullptr || act == nullptr) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — the handle pair is not callable here",
                body);
    return true;
  }

  // Claimed by lowering r1 so a callee frames below the handle it is passed, not over it.
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t handle = frame + 0x20u;
  const uint32_t text = frame + 0x40u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, handle + (word * 4u), 0u);
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = handle;
  ctx.r4.u32 = inventory;
  ctx.r5.u32 = static_cast<uint32_t>(hash);
  find(ctx, base);
  ctx = saved;

  const uint32_t item = LoadGuestU32(base, handle);
  if (item == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — record 0x{:08X} is not in the hero's "
                "inventory", body, static_cast<uint32_t>(hash));
    return true;
  }

  if (op == "outfit") {
    DressOutfit(ctx, base, saved, frame, handle, hero, static_cast<uint32_t>(hash));
  } else if (op == "equip" && joiner) {
    // A JOINER EQUIPS PLAYER TWO DIRECTLY. The pending-record slot at `GUI+52` (record A) is
    // applied at pause exit by `sub_8236ED98` to the entity at EM+104 — player ONE — on every
    // side (0x8236ee98..0x8236eeb0); only `GUI+80` (record B) reaches EM+112, and nothing the
    // menus write goes there. So on a client the guest's own weapons book arms the host's
    // copy instead of the player's hero. `sub_824ABA28(comp28, recordId, 0)` is what that
    // pending record runs in the end (message class 0x820CF028 slot 3), on the right body.
    // The step-by-step equip (EquipRecord), because the guest's own `sub_824ABA28` leaves a
    // client's instantiated item unactivated and the attach declines it.
    std::string report;
    ctx = saved;
    const bool drawn = EquipRecord(ctx, base, hero, static_cast<uint32_t>(hash), report);
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: Inventory:equip — joiner {} record 0x{:08X} on its hero "
                "0x{:08X}:{}", drawn ? "drew" : "could NOT draw", static_cast<uint32_t>(hash),
                hero, report);
  } else if (switched) {
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    const uint32_t slots = root != 0u ? LoadGuestU32(base, root + 52u) : 0u;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = slots;
    ctx.r4.u32 = handle;
    ctx.r5.u32 = 1u;  // picks the local hero's weak-ref slot inside sub_826CA170
    act(ctx, base);
    ctx = saved;
  } else if (op == "gift") {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = giving;
    ctx.r4.u32 = handle;
    act(ctx, base);
    ctx = saved;
  } else {
    // The empty reason string `sub_823E1370` builds at 0x823E14CC before the use.
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = text;
    ctx.r4.u32 = refii::guest::Address(0x82000CA0u);   // byte_82000CA0, the empty literal
    ctx.r5.u32 = 0xFFFFFFFFu;   // -1 = NUL-terminated
    rex_lhCharString_CtorFromCStr(ctx, base);
    ctx = saved;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = LoadGuestU32(base, inventory + 4u);  // the owning entity
    ctx.r4.u32 = handle;
    ctx.r5.u32 = text;
    act(ctx, base);
    const bool used = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = text;
    rex_lhCharString_Dtor(ctx, base);
    ctx = saved;

    // A joiner's hero is not the one `sub_826CA170` resolves (`sub_8222C1B8`, the local
    // player), so the switch is not used here; its tail is, as `sub_826CA170` runs it after a
    // successful use (0x826ca224..0x826ca278): the item's InventoryItemComponent
    // (`sub_822A99E8(sub_821B24F8(item + 4), "InventoryItemComponent")`, 0x826ca238
    // `mr r4, r7` with r7 the literal at 0x820AF11C), its bool
    // `ExitPauseMenuOnUse` (`sub_82310290`, literal at 0x820D43B4), and on true
    // `sub_8236ED98(*(0x83496B08 + 28), 1)`.
    PPCFunc* record = FindGuestFunction(refii::guest::Address(0x821B24F8u));
    PPCFunc* component = FindGuestFunction(refii::guest::Address(0x822A99E8u));
    PPCFunc* flag = FindGuestFunction(refii::guest::Address(0x82310290u));
    PPCFunc* leave = FindGuestFunction(refii::guest::Address(0x8236ED98u));
    if (used && record != nullptr && component != nullptr && flag != nullptr &&
        leave != nullptr) {
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = LoadGuestU32(base, handle) + 4u;
      record(ctx, base);
      const uint32_t item_record = ctx.r3.u32;
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = item_record;
      ctx.r4.u32 = refii::guest::Address(0x820AF11Cu);  // "InventoryItemComponent"
      component(ctx, base);
      const uint32_t item_component = ctx.r3.u32;
      bool exits = false;
      if (item_component != 0u) {
        ctx.r1.u32 = frame;
        ctx.r3.u32 = item_component;
        ctx.r4.u32 = refii::guest::Address(0x820D43B4u);  // "ExitPauseMenuOnUse"
        flag(ctx, base);
        exits = (ctx.r3.u32 & 0xFFu) != 0u;
      }
      ctx = saved;

      const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
      const uint32_t gui = root != 0u ? LoadGuestU32(base, root + 28u) : 0u;
      if (exits && gui != 0u) {
        ctx.r1.u32 = frame;
        ctx.r3.u32 = gui;
        ctx.r4.u32 = 1u;
        leave(ctx, base);
        ctx = saved;
        REXLOG_INFO("Refii guest-tick pump: Inventory:use — the item states ExitPauseMenuOnUse; "
                    "left the pause menu (sub_8236ED98)");
      }
    }
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = LoadGuestU32(base, handle);
  ctx.r4.u32 = handle;
  release(ctx, base);
  ctx = saved;

  REXLOG_INFO("Refii guest-tick pump: Inventory:{} ran on item 0x{:08X} (record 0x{:08X}) — no "
              "GUI event, no screen", op, item, static_cast<uint32_t>(hash));
  return true;
}

/// BUYING AN ABILITY LEVEL — `Ability:learn:<1..14>`.
///
/// The abilities screen answers a press on one of its rows through `sub_826C2660`'s switch:
/// event types 20 and 21 reach `sub_826C2D40`, event type 22 reaches `sub_826C2E68`, and each
/// calls one two-argument function that does the whole transaction:
///
///   `sub_8246C970(index, whichHero)`   Strength and Skill (indices 1..6)
///   `sub_8246CF48(index, whichHero)`   Will (indices 7..14)
///
/// Both read NOTHING from the screen, the menu, Lua or `g_Param` — the hero comes out of
/// `dword_83496AB8`, and everything else off the hero's own components (57 abilities,
/// 164 experience). Inside, they take the max level from `sub_824E2D40`, test affordability
/// with `sub_824E4E28`, read `ExperienceCost` (GDB type 1) off the next level's record through
/// `sub_824E49D0`, spend it with `sub_8256E2F0` and raise the level with `sub_824E2F28` — which
/// is also where the morph recompute, the spell-slot rebuild and the stat writes happen. Calling
/// the outer function rather than the five primitives is what keeps all of that.
///
/// `whichHero` IS 1, NOT 0, and the difference is the whole call. The screen computes it as
/// `screen+4 == 0` — `lwz r11, 4(r31); cntlzw r10, r11; extrwi r29, r10, 1,26` at 0x826C2D58,
/// which is the "player index is zero" BIT, not the index. `sub_8246C970` then branches
/// `if (a2) hero = sub_8222C1B8(...) else hero = sub_828E5A78(...)`, so 0 would fetch the other
/// hero entirely.
///
/// The guest puts up its own result box (`sub_8236FA68`, `guiMgr+40 = 2`) either way — the
/// tutorial text on success, `GUI_SCREEN_ABILITY_YOU_DO_NOT_HAVE_ENOUGH_EXPERIENCE` or
/// `GUI_SCREEN_ABILITIES_MAXED` on refusal. That is the feedback, and it arrives through the
/// message-box path the host already mirrors.
bool DispatchAbilityAction(PPCContext& ctx, uint8_t* base, const std::string& body) {
  const std::size_t colon = body.rfind(':');
  if (colon == std::string::npos || body.compare(0, colon, "learn") != 0) {
    REXLOG_INFO("Refii guest-tick pump: 'Ability:{}' is not learn:<index> — dropped", body);
    return true;
  }

  char* end = nullptr;
  const unsigned long index = std::strtoul(body.c_str() + colon + 1, &end, 10);
  if (end == body.c_str() + colon + 1 || *end != '\0' || index < 1u || index > 14u) {
    REXLOG_INFO("Refii guest-tick pump: 'Ability:{}' names no ability 1..14 — dropped", body);
    return true;
  }

  // Strength 1..3 and Skill 4..6 share one entry point; Will 7..14 has its own.
  const uint32_t entry = index <= 6u ? refii::guest::Address(0x8246C970u) : refii::guest::Address(0x8246CF48u);
  PPCFunc* function = FindGuestFunction(entry);
  if (function == nullptr) {
    REXLOG_INFO("Refii guest-tick pump: Ability:learn:{} — 0x{:08X} is not callable here",
                index, entry);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = static_cast<uint32_t>(index);
  ctx.r4.u32 = 1u;
  ctx.ctr.u32 = entry;
  function(ctx, base);
  const bool bought = (ctx.r3.u32 & 0xFFu) != 0u;
  ctx = saved;

  REXLOG_INFO("Refii guest-tick pump: Ability:learn:{} ran 0x{:08X}(index {}, hero 1) — {}",
              index, entry, index,
              bought ? "bought" : "refused (maxed, or not enough experience)");
  return true;
}

/// The screen object the in-game GUI holds for `screen_id`, or 0 — READ ONLY.
///
/// `sub_8243BD60`, the guest's own lookup, **INSERTS** a default entry when the key is absent,
/// so calling it to ask a question would change the map. The map is `std::map<int, CScreen*>`
/// at `owner+68`: `owner+72` is the header node and each node is
/// {left +0, parent +4, right +8, key +12, value +16}. Descend left while the wanted id is
/// below the key, right otherwise; reaching the header again means "not open".
uint32_t ScreenObjectRO(uint8_t* base, uint32_t screen_id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != refii::guest::Address(0x82001610u)) {
    return 0u;
  }

  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  const uint32_t end_node = plausible(owner) ? LoadGuestU32(base, owner + 72u) : 0u;
  if (!plausible(end_node)) {
    return 0u;
  }

  uint32_t node = LoadGuestU32(base, end_node + 4u);
  for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
    const uint32_t key = refii::guest::ScreenIdToTu1(LoadGuestU32(base, node + 12u));
    if (key == screen_id) {
      const uint32_t screen = LoadGuestU32(base, node + 16u);
      return plausible(screen) ? screen : 0u;
    }

    node = LoadGuestU32(base, node + (screen_id < key ? 0u : 8u));
  }

  return 0u;
}

/// `Shop:<buy|sell>:<ticket>` — the guest's own transaction, run on its own thread.
///
/// THE ROW'S IDENTITY IS A TICKET, NOT A RECORD HASH. `sub_82880AC8` hands every populated row
/// a monotonic ticket and files the item behind it in a map; the ticket rides Gameface as two
/// 16-bit halves and comes back here whole. `sub_82880BC0` is the only way back to the item,
/// and it wants the MAP, `registry + 4` — verified at the guest's own call site
/// `sub_826AFF48`: `sub_82880BC0(*(*(dword_83496B08 + 28) + 60) + 4, ticket, out)`. It
/// `__trap()`s on a map that is not its own, so the derivation is not somewhere to guess.
///
/// THE GUARDS ARE OURS. `sub_82872A68`/`sub_82872D70` check nothing — no gold, no stock, no
/// sellability. Worse, a buyer with no `CECMoney` gets the item FREE: the transfer sits outside
/// that branch and `sub_8242E578` only clamps, it never refuses. So affordability is checked
/// first, through the guest's own `sub_8246DB30`, whose whole purpose that is.
bool DispatchShopAction(PPCContext& ctx, uint8_t* base, const std::string& body) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const std::size_t colon = body.rfind(':');
  const std::string op = colon == std::string::npos ? std::string() : body.substr(0, colon);
  if (colon == std::string::npos || (op != "buy" && op != "sell")) {
    REXLOG_INFO("Refii guest-tick pump: 'Shop:{}' is not buy|sell:<ticket> — dropped", body);
    return true;
  }

  char* end = nullptr;
  const unsigned long ticket = std::strtoul(body.c_str() + colon + 1, &end, 10);
  if (end == body.c_str() + colon + 1 || *end != '\0' || ticket == 0u) {
    // Ticket 0 is never issued: `sub_82880AC8` pre-increments before it files the item.
    REXLOG_INFO("Refii guest-tick pump: 'Shop:{}' names no ticket — dropped", body);
    return true;
  }

  // The shopkeeper: `screen+400..407` is ONE weak handle and the entity is behind TWO
  // dereferences (`sub_824692D8`). `screen+400` is the handle's tag, not a component.
  const uint32_t screen = ScreenObjectRO(base, 22u);
  const uint32_t holder = plausible(screen) ? LoadGuestU32(base, screen + 404u) : 0u;
  const uint32_t shopkeeper = plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  if (!plausible(shopkeeper)) {
    REXLOG_INFO("Refii guest-tick pump: Shop:{} — screen 22 states no shopkeeper "
                "(screen 0x{:08X})", body, screen);
    return true;
  }

  if ((LoadGuestU8(base, shopkeeper + 144u) & 0x40u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Shop:{} — the shopkeeper entity is not live", body);
    return true;
  }

  // `CECShopkeeper` is component 27, looked up on the ENTITY every time — the shop screen
  // never stores it.
  const uint32_t shop_component = HeroComponent(base, shopkeeper, 27u);
  const uint32_t hero = HeroEntity(base);
  if (shop_component == 0u || !plausible(hero)) {
    REXLOG_INFO("Refii guest-tick pump: Shop:{} — no CECShopkeeper(27) or no hero", body);
    return true;
  }

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t owner = plausible(root) ? LoadGuestU32(base, root + 28u) : 0u;
  const uint32_t registry = plausible(owner) ? LoadGuestU32(base, owner + 60u) : 0u;
  if (!plausible(registry)) {
    REXLOG_INFO("Refii guest-tick pump: Shop:{} — the row registry is not up", body);
    return true;
  }

  PPCFunc* lookup = FindGuestFunction(refii::guest::Address(0x82880BC0u));
  PPCFunc* affordable = FindGuestFunction(refii::guest::Address(0x8246DB30u));
  PPCFunc* commit = FindGuestFunction(op == "buy" ? refii::guest::Address(0x82872A68u) : refii::guest::Address(0x82872D70u));
  if (lookup == nullptr || commit == nullptr || (op == "buy" && affordable == nullptr)) {
    REXLOG_INFO("Refii guest-tick pump: Shop:{} — the transaction pair is not callable here",
                body);
    return true;
  }

  // Claimed by lowering r1 so a callee frames BELOW the scratch it is passed, not over it.
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t item = frame + 0x20u;   // the ItemRef the ticket resolves to (four words)
  const uint32_t bought = frame + 0x40u; // buy's out parameter
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, item + (word * 4u), 0u);
    StoreGuestU32(base, bought + (word * 4u), 0u);
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = registry + 4u;
  ctx.r4.u32 = static_cast<uint32_t>(ticket);
  ctx.r5.u32 = item;
  lookup(ctx, base);
  const bool found = (ctx.r3.u32 & 0xFFu) != 0u;
  ctx = saved;

  if (!found || LoadGuestU32(base, item) == 0u) {
    // A stale ticket is the normal case for a list repopulated since the row was drawn.
    REXLOG_INFO("Refii guest-tick pump: Shop:{} — ticket {} is not in this population", body,
                ticket);
    return true;
  }

  if (op == "buy") {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = shop_component;
    ctx.r4.u32 = item;
    affordable(ctx, base);
    const bool can_afford = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;

    if (!can_afford) {
      REXLOG_INFO("Refii guest-tick pump: Shop:buy:{} — the hero cannot afford it; "
                  "no transaction", ticket);
      return true;
    }
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = shop_component;
  ctx.r4.u32 = item;
  if (op == "buy") {
    ctx.r5.u32 = bought;
    ctx.r6.u32 = hero;
  } else {
    ctx.r5.u32 = hero;
  }

  commit(ctx, base);
  ctx = saved;

  REXLOG_INFO("Refii guest-tick pump: Shop:{}:{} ran 0x{:08X}(comp 0x{:08X}, item 0x{:08X}, "
              "hero 0x{:08X})", op, ticket, op == "buy" ? refii::guest::Address(0x82872A68u) : refii::guest::Address(0x82872D70u),
              shop_component, LoadGuestU32(base, item), hero);
  return true;
}

/// The `Player` with this id, out of the `PlayerManager` at `game+156`.
///
/// NOT `PlayerObject`, which walks `game+88` — the ENTITY/WORLD manager, the same
/// `**(*(game+88)+4)` that `sub_82389FB8` hands to `sub_82365258`. It answers a party holder
/// that suits the dog walk and is not a `Player` at all: its `+36` is a pointer where a
/// PlayerID belongs. Publishing it through `sub_82371860` put a non-Player into
/// `*(App+20)+12`, and the input dispatch then read it as one every frame —
/// `sub_82403350` -> `Player::GetEntity` (`sub_8229EE78`) -> `*(obj+16)` -> SIGSEGV.
///
/// The manager's vector is 12-byte `{PlayerID, Player*, control block}` entries between
/// `manager+8` and `manager+12`, sorted by id — the layout `sub_82446928` walks (0x82446A70)
/// and `sub_82314D30` inserts into. Walked here rather than calling `sub_82264230` so the
/// pump needs no guest call on the tick thread.
uint32_t PlayerWithId(uint8_t* base, uint32_t id) {
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  constexpr uint32_t kPlayerManager = 156u;
  constexpr uint32_t kEntryStride = 12u;
  constexpr uint32_t kMaxEntries = 8u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t manager = plausible(game) ? LoadGuestU32(base, game + kPlayerManager) : 0u;
  if (!plausible(manager)) {
    return 0u;
  }

  const uint32_t begin = LoadGuestU32(base, manager + 8u);
  const uint32_t end = LoadGuestU32(base, manager + 12u);
  if (!plausible(begin) || end < begin || ((end - begin) % kEntryStride) != 0u ||
      (end - begin) > kEntryStride * kMaxEntries) {
    return 0u;
  }

  for (uint32_t entry = begin; entry != end; entry += kEntryStride) {
    if (LoadGuestU32(base, entry) == id) {
      const uint32_t player = LoadGuestU32(base, entry + 4u);
      return plausible(player) ? player : 0u;
    }
  }

  return 0u;
}

/// The local `Player` — id 1 when it exists, else id 2.
///
/// "The party's first player", which several dispatches want, but as an actual `Player`. On a
/// JOINING instance only id 2 is present (a run's trace showed one publish, `id=2`, with
/// handler1 still 0), so the fallback is the henchman's own player, not a failure.
///
/// This replaced `PlayerObject` at every site that hands the value to a guest routine expecting
/// a `Player`. `PlayerObject` walks `game+88` and answers `**(*(game+88)+4)`, the ENTITY/WORLD
/// manager — `sub_82389FB8` gives that same expression to `sub_82365258` as the entity manager.
/// Its `+36` is a pointer where a PlayerID belongs, and `+16` is not a weak_ptr control block,
/// so `sub_8236F400` faulted on `v6 = *v5` (0x8236F428) reading `*(a2+16)`. The two only agreed
/// while our own `RegisterPlayer` was publishing that same wrong object into the handler slot.
/// Only `SampleDog` still wants `PlayerObject`: the dog handle is at `+196`, and a `Player` is
/// 0x48 bytes.
uint32_t FirstPlayer(uint8_t* base) {
  const uint32_t one = PlayerWithId(base, 1u);
  return one != 0u ? one : PlayerWithId(base, 2u);
}

/// The player THIS machine's pad drives — the guest's own rule in `sub_8240B2A0` (TU1
/// `sub_826B83D0`): id 1 when the net context's bytes +52 and +53 are both set (host, and solo
/// reads as host), else id 2 (a client). The host-owned menus — spells, blade, expression
/// wheel — act for this player: on a joiner `FirstPlayer` is the sync's copy of the HOST's
/// hero, so the spells menu opened for player one there and the joiner's own never drew.
uint32_t LocalPlayer(uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t net = plausible(game) ? LoadGuestU32(base, game + 140u) : 0u;
  const bool both = plausible(net) && base[net + 52u] != 0u && base[net + 53u] != 0u;
  const uint32_t player = PlayerWithId(base, both || !plausible(net) ? 1u : 2u);
  return player != 0u ? player : FirstPlayer(base);
}

/// Is player two standing in their own hero's body rather than the pickbox henchman?
///
/// A BIT, NOT A COMMAND: nothing applies it here. `coop_player_hooks.cpp` overrides
/// `sub_8233C508`, the guest's creature-name table for player two, and reads this to answer it —
/// so the guest's own spawn, re-skin, level-change and save-load paths all build the right body
/// without us racing any of them. The two Refii instances agree over Refii's own co-op channel;
/// the guest has no concept of this, so there is nothing of the title's to keep in step.
std::atomic<bool> g_coop_own_hero{false};

/// Defined at the foot of this file, outside `refii::vfs`, so the hooks payload can reach it.
extern "C" void RefiiCoopSetOwnHero(int on);

/// A component off an entity, by id — the guest's own layout.
///
/// `entity+0x24` is a presence bitmask (bit `id&31` of word `id>>5`); the `(id, ptr)` pairs run
/// from `entity+0x48` to `entity+0x4C`; `entity+0x8C`, when set, is a byte table mapping id to a
/// pair slot. `sub_8238CDC8`, the guest's own component writer, sets that bit at 0x8238D238.
uint32_t EntityComponent(uint8_t* base, uint32_t entity, uint32_t id) {
  if (id >= 256u || !LooksLikeGuestPointer(entity)) {
    return 0u;
  }

  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!LooksLikeGuestPointer(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return 0u;
  }

  const uint32_t accelerator = LoadGuestU32(base, entity + 0x8Cu);
  if (LooksLikeGuestPointer(accelerator)) {
    const uint32_t at = pairs + 8u * LoadGuestU8(base, accelerator + id);
    if (at + 8u <= pairs_end && LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return LooksLikeGuestPointer(component) ? component : 0u;
    }
  }

  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return LooksLikeGuestPointer(component) ? component : 0u;
    }
  }

  return 0u;
}

/// Player one's entity, or 0 — the adjacent handle object, `EM+104` with its pointer at `+108`
/// (`sub_8222C1B8`, the twin of the accessor below). Same layout, one slot earlier.
uint32_t CoopPlayerOneEntity(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };

  const uint32_t manager =
      step(step(step(step(LoadGuestU32(base, refii::guest::Address(0x83496AB8u)), 12u), 88u), 4u), 0u);
  const uint32_t entity = step(step(manager, 108u), 0u);
  return LooksLikeGuestPointer(entity) ? entity : 0u;
}

/// Player two's entity, or 0.
///
/// INLINED RATHER THAN CALLED. `sub_828E5A78` is the guest's own accessor and `sub_8246C970`
/// proves it IS player two — its `a2` is a bool and the FALSE arm (0x8246C9B8) takes this one,
/// the arm the shipped "HenchmanAbilities" screen drives, with player one on the adjacent slot
/// `EM+104`. But THE RECOMPILER NEVER EMITTED IT: it is the one address in this file's set that
/// `librefii_generated.so` does not define, and referencing it stopped that library loading at
/// all, which takes the whole guest down with it.
///
/// No call is needed. The body is two loads — the handle block at `EM+112` holds its object at
/// `EM+116`, and the entity is one more deref; the remaining branch is the stale-handle cleanup,
/// which returns 0 anyway.
uint32_t CoopPlayerTwoEntity(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };

  // `**(*(*(0x83496AB8)+12)+88)+4)`, exactly as `sub_8246C970` reads it at 0x8246C9A8.
  const uint32_t manager =
      step(step(step(step(LoadGuestU32(base, refii::guest::Address(0x83496AB8u)), 12u), 88u), 4u), 0u);

  const uint32_t held = step(manager, 116u);
  const uint32_t entity = step(held, 0u);
  return LooksLikeGuestPointer(entity) ? entity : 0u;
}

/// See kActionCoopHeroSkillsPrefix.
///
/// IDEMPOTENT AND RE-APPLIED, not fired once. Re-typing player two's entity rebuilds its
/// components from the GDB, so ranks written before a re-skin are gone after it — and the co-op
/// screen re-skins every frame. The host therefore restates this, and the pass writes only the
/// ids that actually differ, so it settles to a no-op once the body has stopped changing.
/// The stored hero record per slot — what the field actions carry, applied by CoopApplyHero.
struct CoopHeroRecord {
  uint32_t generation = 0u;   // the shell's hash of the whole record; 0 = nothing to apply
  uint32_t melee = 0u;        // weapon record ids to draw (CoopHeroEquip)
  uint32_t ranged = 0u;
  std::string stats;          // CoopHeroStats text (28 fields), player two only
  std::string skills;         // CoopHeroSkills text, player two only
  std::string build;          // CoopHeroBuild text, player two only
  uint32_t face = 0u;         // CoopHeroFace record, player two only
};
CoopHeroRecord g_coop_record[2];
std::map<uint32_t, std::string> g_coop_skills_applied;  // body -> ranks last applied
std::map<uint32_t, std::string> g_coop_build_applied;   // body -> morphs last applied

bool DispatchCoopHeroSkills(const std::string& levels) {
  g_coop_record[1].skills = levels;
  return true;
}

void ApplySkills(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& levels) {
  constexpr uint32_t kStatsComponent = 57u;
  constexpr uint32_t kFirstAbility = 1u;
  constexpr uint32_t kLastAbility = 14u;

  const uint32_t stats = entity != 0u ? EntityComponent(base, entity, kStatsComponent) : 0u;
  if (stats == 0u || levels.empty()) {
    return;
  }

  if (g_coop_skills_applied[entity] == levels) {
    return;
  }
  g_coop_skills_applied[entity] = levels;

  uint32_t id = 0u;
  uint32_t written = 0u;
  for (std::size_t at = 0; at <= levels.size() && id <= kLastAbility; ++id) {
    const std::size_t comma = levels.find(',', at);
    const std::string field = levels.substr(at, comma == std::string::npos ? comma : comma - at);

    at = comma == std::string::npos ? levels.size() + 1u : comma + 1u;
    if (id < kFirstAbility || field.empty()) {
      continue;
    }

    const uint32_t rank = static_cast<uint32_t>(std::strtoul(field.c_str(), nullptr, 16));

    // The rank slot the writer itself uses — `4 * (id + 68)` off the component, 0x824E2F58.
    if (LoadGuestU32(base, stats + 4u * (id + 68u)) == rank) {
      continue;
    }

    PPCContext work = ctx;
    work.r3.u32 = stats;
    work.r4.u32 = id;
    work.r5.u32 = rank;
    work.r6.u32 = 1u;  // force the replication to the peer
    rex_CECStats_SetAbilityLevel(work, base);
    ++written;
  }

  if (written != 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroSkills — {} rank(s) applied to player two "
                "(entity 0x{:08X}, stats 0x{:08X})", written, entity, stats);
  }
}

/// See kActionCoopHeroBuildPrefix.
///
/// The three body-morph percentages live on the SAME component as the ability ranks — CECStats,
/// id 57 — at `+0x40` strength, `+0x44` skill, `+0x48` will. Those offsets are not taken on
/// trust: `RefiiHostGetHeroVitals` has been reading the hero's own build from exactly them since
/// ABI 78.
///
/// Written directly rather than through the guest's own setters, which take DELTAS and fire
/// gossip events ("ModifyFat") — a character being applied is not a character being changed. The
/// flush afterwards is the guest's, so the fan-out to the morph and clothing components is its
/// own. Values are clamped here because a direct write skips the setters' clamp.
bool DispatchCoopHeroBuild(const std::string& build) {
  g_coop_record[1].build = build;
  return true;
}

void ApplyBuild(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& build) {
  constexpr uint32_t kStatsComponent = 57u;
  // strength, skill, will (vitals reader, ABI 78), then fat `+56` (`sub_824D7868`), height
  // `+60` (`sub_824D7A68`, the one -1..1 scalar) and tan `+52` (`sub_824D77F8`).
  constexpr uint32_t kSlots[] = {0x40u, 0x44u, 0x48u, 0x38u, 0x3Cu, 0x34u};
  constexpr float kLow[] = {0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f};

  const uint32_t stats = entity != 0u ? EntityComponent(base, entity, kStatsComponent) : 0u;
  if (stats == 0u || build.empty()) {
    return;
  }

  if (g_coop_build_applied[entity] == build) {
    return;
  }
  g_coop_build_applied[entity] = build;

  uint32_t written = 0u;
  std::size_t at = 0;
  for (uint32_t slot = 0; slot < 6u && at <= build.size(); ++slot) {
    const std::size_t comma = build.find(',', at);
    const std::string field = build.substr(at, comma == std::string::npos ? comma : comma - at);

    at = comma == std::string::npos ? build.size() + 1u : comma + 1u;
    if (field.empty()) {
      continue;
    }

    float value = std::strtof(field.c_str(), nullptr);
    value = value < kLow[slot] ? kLow[slot] : value > 1.0f ? 1.0f : value;

    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    if (LoadGuestU32(base, stats + kSlots[slot]) == bits) {
      continue;
    }

    StoreGuestU32(base, stats + kSlots[slot], bits);
    ++written;
  }

  if (written != 0u) {
    PPCContext work = ctx;
    work.r3.u32 = stats;
    rex_CECStats_FlushAppearance(work, base);

    REXLOG_INFO("Refii guest-tick pump: CoopHeroBuild — {} morph(s) applied to player two "
                "(entity 0x{:08X}, stats 0x{:08X})", written, entity, stats);
  }
}

bool IsCoopJoiner(uint8_t* base);

/// The outfits — GDB def hashes off each hero's worn walk — per player, and the entity + outfit
/// last dressed for each, so a body is dressed ONCE. The co-op screen rebuilds player two every
/// frame and `sub_824F0370` bumps a held item's count: without the latch it is a shirt a frame.
/// Index 0 is player one, 1 is player two.
// Index 2 is the co-op screen's PREVIEW entity, which wears player two's outfit.
std::vector<uint32_t> g_coop_wear[3];
uint32_t g_coop_dressed_entity[3] = {0u, 0u, 0u};
std::string g_coop_dressed_set[3];

/// Player two's weapon names, and the inventory chunks per player. See CoopRedressPlayer.
std::string g_coop_melee_name;
std::string g_coop_ranged_name;
std::vector<std::pair<uint32_t, uint32_t>> g_coop_give[3];
std::string g_coop_given_set[3];
uint32_t g_coop_given_entity[3] = {0u, 0u, 0u};

/// See kActionCoopHeroWeaponsPrefix.
bool DispatchCoopHeroWeapons(const std::string& text) {
  const std::size_t comma = text.find(',');
  g_coop_melee_name = comma == std::string::npos ? text : text.substr(0, comma);
  g_coop_ranged_name = comma == std::string::npos ? std::string() : text.substr(comma + 1u);
  return true;
}

/// See kActionCoopHeroGivePrefix. The action REPLACES the player's set — which is why it must
/// carry the whole inventory: two chunks in one tick left only the second one to give.
bool DispatchCoopHeroGive(const std::string& text) {
  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return true;
  }

  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return true;
  }

  std::vector<std::pair<uint32_t, uint32_t>> records;
  const std::string list = text.substr(colon + 1u);
  std::size_t at = 0;
  while (at <= list.size()) {
    const std::size_t comma = list.find(',', at);
    const std::string field = list.substr(at, comma == std::string::npos ? comma : comma - at);
    at = comma == std::string::npos ? list.size() + 1u : comma + 1u;
    const std::size_t split = field.find(':');
    if (split == std::string::npos) {
      continue;
    }

    const uint32_t record = static_cast<uint32_t>(std::strtoul(field.substr(0, split).c_str(), nullptr, 16));
    const uint32_t count = static_cast<uint32_t>(std::strtoul(field.substr(split + 1u).c_str(), nullptr, 10));
    if (record != 0u && count != 0u) {
      records.emplace_back(record, count);
    }
  }

  if (player == 2) {
    g_coop_give[2] = records;  // the preview wears what player two wears
  }
  g_coop_give[player - 1] = std::move(records);
  return true;
}

/// Puts a player's transported inventory into their body's inventory, once per (entity, set).
/// `sub_824F0820(out, comp13, record, notify)` adds exactly ONE — a held record's count is
/// bumped by one per call (0x824F08BC) — so a count is that many calls. Skipped for records the
/// body already holds at that count or more, so a restatement after a partial give completes
/// it rather than doubling it.
void CoopGivePlayer(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  const int slot = player - 1;
  if (entity == 0u || g_coop_give[slot].empty()) {
    return;
  }

  std::string set;
  for (const auto& record : g_coop_give[slot]) {
    set += std::to_string(record.first) + ':' + std::to_string(record.second) + ',';
  }

  if (entity == g_coop_given_entity[slot] && set == g_coop_given_set[slot]) {
    return;
  }

  const uint32_t inventory = EntityComponent(base, entity, 13u);
  if (inventory == 0u) {
    return;
  }

  g_coop_given_entity[slot] = entity;
  g_coop_given_set[slot] = set;

  // What the body already holds: the MS-STL list at `inv+16` (sentinel), item at `node+8`,
  // record at `item+4`, count at `item+12` — the layout the inventory export walks.
  const auto held = [&](uint32_t record) -> uint32_t {
    const uint32_t sentinel = LoadGuestU32(base, inventory + 16u);
    uint32_t node = LooksLikeGuestPointer(sentinel) ? LoadGuestU32(base, sentinel) : 0u;
    for (uint32_t hops = 0; hops < 4096u && LooksLikeGuestPointer(node) && node != sentinel; ++hops) {
      const uint32_t item = LoadGuestU32(base, node + 8u);
      if (LooksLikeGuestPointer(item) && LoadGuestU32(base, item + 4u) == record) {
        return LoadGuestU32(base, item + 12u);
      }

      node = LoadGuestU32(base, node);
    }

    return 0u;
  };

  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t ref = frame + 0x20u;
  uint32_t given = 0u;
  for (const auto& record : g_coop_give[slot]) {
    const uint32_t have = held(record.first);
    for (uint32_t n = have; n < record.second && n < 999u; ++n) {
      for (uint32_t i = 0; i < 4u; ++i) StoreGuestU32(base, ref + 4u * i, 0u);

      PPCContext give = ctx;
      give.r1.u32 = frame;
      give.r3.u32 = ref;
      give.r4.u32 = inventory;
      give.r5.u32 = record.first;
      give.r6.u32 = 1u;
      rex_CECInventory_AddOneOfRecord(give, base);

      const uint32_t item = LoadGuestU32(base, ref);
      if (item != 0u) {
        PPCContext release = ctx;
        release.r1.u32 = frame;
        release.r3.u32 = item;
        release.r4.u32 = ref;
        rex_lhTrackedObject_RemoveHandleRef(release, base);
        ++given;
      }
    }
  }

  REXLOG_INFO("Refii guest-tick pump: CoopGive — player {} 0x{:08X}: {} item(s) added across {} "
              "record(s)", player, entity, given, g_coop_give[slot].size());
}

/// See kActionCoopHeroWearPrefix. Remembers the set; the redress applies it.
bool DispatchCoopHeroWear(const std::string& text) {
  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return true;
  }

  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return true;
  }

  std::vector<uint32_t> hashes;
  const std::string list = text.substr(colon + 1u);
  std::size_t at = 0;
  while (at <= list.size()) {
    const std::size_t comma = list.find(',', at);
    const std::string field = list.substr(at, comma == std::string::npos ? comma : comma - at);
    at = comma == std::string::npos ? list.size() + 1u : comma + 1u;
    if (!field.empty()) {
      hashes.push_back(static_cast<uint32_t>(std::strtoul(field.c_str(), nullptr, 16)));
    }
  }

  if (player == 2) {
    g_coop_wear[2] = hashes;
  }
  g_coop_wear[player - 1] = std::move(hashes);
  return true;
}

/// Dresses one player's current body, once per (entity, outfit). With an outfit: give then wear
/// each hash. Without one: the def's own `InitialItems` then `InitialModifiers`, what a new game
/// does to player one.
void CoopRedressPlayer(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  const int slot = player - 1;
  if (entity == 0u) {
    return;
  }

  std::string set;
  for (uint32_t hash : g_coop_wear[slot]) {
    set += std::to_string(hash) + ',';
  }

  if (entity == g_coop_dressed_entity[slot] && set == g_coop_dressed_set[slot]) {
    return;
  }

  const uint32_t inventory = EntityComponent(base, entity, 13u);
  const uint32_t modifiers = EntityComponent(base, entity, 15u);
  if (inventory == 0u || modifiers == 0u) {
    return;  // not a dressable body yet — the next tick asks again
  }

  g_coop_dressed_entity[slot] = entity;
  g_coop_dressed_set[slot] = set;

  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t ref = frame + 0x20u;

  if (g_coop_wear[slot].empty()) {
    // NO OUTFIT, NO DRESS. The def's own `InitialItems`/`InitialModifiers` were tried here and
    // put `CreatureHero`'s creation-time defaults on a body that then also received the real
    // outfit when it arrived — wear adds, it never replaces. A bare body until the kit lands
    // is the honest state; the latch is left clear so the first named outfit dresses it.
    g_coop_dressed_entity[slot] = 0u;
    g_coop_dressed_set[slot].clear();
    return;
  }

  uint32_t worn = 0u;
  for (uint32_t hash : g_coop_wear[slot]) {
    for (uint32_t i = 0; i < 4u; ++i) StoreGuestU32(base, ref + 4u * i, 0u);

    PPCContext give = ctx;
    give.r1.u32 = frame;
    give.r3.u32 = ref;
    give.r4.u32 = inventory;
    give.r5.u32 = hash;
    rex_CECInventory_AddItemByDefHash(give, base);

    const uint32_t item = LoadGuestU32(base, ref);
    if (item == 0u) {
      continue;
    }

    PPCContext wear = ctx;
    wear.r1.u32 = frame;
    wear.r3.u32 = modifiers;
    wear.r4.u32 = hash;
    rex_CECAppearanceModifiers_WearItemByDefHash(wear, base);
    ++worn;

    PPCContext release = ctx;
    release.r1.u32 = frame;
    release.r3.u32 = item;
    release.r4.u32 = ref;
    rex_lhTrackedObject_RemoveHandleRef(release, base);
  }

  REXLOG_INFO("Refii guest-tick pump: CoopRedress — player {} 0x{:08X} dressed: {} of {} outfit "
              "item(s) given and worn", player, entity, worn, g_coop_wear[slot].size());
}

/// Both players, whenever a body exists that has not been dressed with the current outfit.
///
/// Neither of the guest's ways of giving a player a body dresses a hero body across the wire:
/// a RETYPE re-wears an empty stash (0x82365938), CREATION on a client runs its
/// `InitialItems` pass only after the gate byte is set (0x82315224), and the host's hero
/// APPEARANCE is never replicated to a client at all — only the weapon slots are
/// (`sub_824AD698`), which is why the joiner's player one stands armed and naked.
///
/// PLAYER ONE ONLY ON THE JOINER. On the host player one is the real hero, already dressed from
/// the save; giving those items again would raise their counts. Player two dresses on both
/// sides, only while standing in their own hero's body — a henchman brings its own clothes.
///
/// Called from the hooks after a body build and from the tick, keyed on the entity address, so
/// a body created by the join sync — a path no hook covers — is dressed on the next tick.
/// The co-op screen's preview entity and the screen handler that owns it: `gui+56` is the
/// current screen (its id at +4 must be 24), `gui+28` the owner whose map at +72 keys handlers
/// by screen id; the handler holds the entity at +276 (0x824388CC). The same walk the bridge's
/// mannequin export makes.
std::pair<uint32_t, uint32_t> CoopMannequin(uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  if (!LooksLikeGuestPointer(gui) || LoadGuestU32(base, gui) != refii::guest::Address(0x82001610u)) return {0u, 0u};
  const uint32_t manager = LoadGuestU32(base, gui + 56u);
  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!LooksLikeGuestPointer(manager) || !LooksLikeGuestPointer(owner)) return {0u, 0u};
  if (refii::guest::ScreenIdToTu1(LoadGuestU32(base, manager + 4u)) != 24u) return {0u, 0u};
  const uint32_t end_node = LoadGuestU32(base, owner + 72u);
  if (!LooksLikeGuestPointer(end_node)) return {0u, 0u};
  uint32_t node = LoadGuestU32(base, end_node + 4u);
  uint32_t handler = 0u;
  for (uint32_t step = 0; step < 64u && LooksLikeGuestPointer(node) && node != end_node; ++step) {
    const uint32_t key = refii::guest::ScreenIdToTu1(LoadGuestU32(base, node + 12u));
    if (key == 24u) { handler = LoadGuestU32(base, node + 16u); break; }
    node = LoadGuestU32(base, node + (24u < key ? 0u : 8u));
  }
  if (!LooksLikeGuestPointer(handler)) return {0u, 0u};
  const uint32_t entity = LoadGuestU32(base, handler + 276u);
  return {LooksLikeGuestPointer(entity) ? entity : 0u, handler};
}

/// The co-op screen's preview follows the own-hero bit: when the bit flips while the screen
/// is up the guest is asked to respawn the preview (`sub_82438818(handler)`, which now answers
/// the hero mannequin through the overridden name table), and a hero preview is dressed in
/// player two's outfit like the body itself. Not gated on a peer standing — it is a local
/// preview entity on a screen the local player is looking at.
void CoopDressMannequin(PPCContext& ctx, uint8_t* base) {
  static bool bit_seen = false;
  const bool bit = g_coop_own_hero.load(std::memory_order_relaxed);
  const auto [entity, handler] = CoopMannequin(base);
  if (handler == 0u) {
    return;
  }

  if (bit != bit_seen) {
    bit_seen = bit;
    PPCContext work = ctx;
    work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
    work.r3.u32 = handler;
    rex_lhCoopScreen_SpawnPreviewMannequin(work, base);
    REXLOG_INFO("Refii guest-tick pump: co-op preview respawned for own-hero bit {}", bit ? 1 : 0);
    return;  // the new entity is dressed on the next tick
  }

  if (bit && entity != 0u) {
    CoopGivePlayer(ctx, base, 3, entity);
    CoopRedressPlayer(ctx, base, 3, entity);
  } else {
    g_coop_dressed_entity[2] = 0u;
    g_coop_dressed_set[2].clear();
  }
}

// Defined with their owners further down.
extern std::map<uint32_t, uint32_t> g_coop_applied_generation;
extern std::map<uint32_t, std::pair<uint32_t, uint32_t>> g_coop_equip_latched;
extern uint32_t g_coop_stats_entity;
extern uint32_t g_coop_face_entity;
extern uint32_t g_coop_face_record;

/// Every per-body latch, dropped together when the peer goes: a re-joined player can land on a
/// RECYCLED entity address, and a latch that remembered the old body would skip the new one.
extern std::map<uint32_t, std::pair<uint64_t, uint64_t>> g_coop_held_attached;

void CoopResetLatches() {
  g_coop_held_attached.clear();
  g_coop_applied_generation.clear();
  g_coop_equip_latched.clear();
  g_coop_skills_applied.clear();
  g_coop_build_applied.clear();
  for (int slot = 0; slot < 3; ++slot) {
    g_coop_dressed_entity[slot] = 0u;
    g_coop_dressed_set[slot].clear();
    g_coop_given_entity[slot] = 0u;
    g_coop_given_set[slot].clear();
  }
  g_coop_stats_entity = 0u;
  g_coop_face_entity = 0u;
  g_coop_face_record = 0u;
}

/// ANIMATE THE CO-OP PREVIEW. The guest poses a preview entity only from the GUI's Gameface
/// render path (`sub_82379170` → gameface vt+48 → `sub_822CD208` → `sub_826D6C90`), which
/// this headless host never runs — and which CANNOT be called from the tick: it builds the
/// model instance against the render world and the present thread then called through a
/// freed pointer (both machines, 2 s after the first call). The simulation's own skeleton pass
/// (`sub_8220D1B8`, 0x8220D1B8, decompiled) has the lever instead: it reads a FORCE byte at
/// `comp3+132` (0x8220d4cc), poses the entity when it is set even though the visibility and
/// distance gates say no (0x8220d78c), then sets the pose-enable bit `comp3+144 |= 4` and
/// clears the byte (0x8220dc08–0x8220dc38) — so it is re-set here every tick while the co-op
/// screen holds a preview. Whatever idle the creature's own controller carries then advances
/// with the sim's step, and the feed's rig follows it.
/// Every preview mannequin the guest keeps, posed from the tick. Preview manager `*(GUI+8)`
/// (vtable 0x82006CC0): records `*(pm+56)..*(pm+60)`, 24 bytes each — `+4` entity holder
/// (`*holder` = entity), `+16` ticket, `+20` active (`sub_822CD208`, `sub_826D8008`). The guest
/// poses them only from the Gameface render path, so the motion force byte (comp3+132,
/// `sub_8220D1B8`) is re-set every tick.
void PosePreviewMannequins(uint8_t* base, uint32_t coop_entity) {
  static uint32_t said[8] = {};
  auto pose = [&](uint32_t entity, const char* what) {
    if (!LooksLikeGuestPointer(entity)) return;
    const uint32_t motion = EntityComponent(base, entity, 3u);
    if (motion == 0u) return;
    base[motion + 132u] = 1u;
    for (uint32_t& seen : said) {
      if (seen == entity) return;
    }
    for (uint32_t& seen : said) {
      if (seen == 0u) { seen = entity; break; }
    }
    REXLOG_INFO("Refii guest-tick pump: {} preview 0x{:08X} is posed by the simulation "
                "(force byte comp3+132 re-set each tick)", what, entity);
  };

  pose(coop_entity, "co-op henchman");

  const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  if (!LooksLikeGuestPointer(gui) || LoadGuestU32(base, gui) != refii::guest::Address(0x82001610u)) return;
  const uint32_t pm = LoadGuestU32(base, gui + 8u);
  if (!LooksLikeGuestPointer(pm) || LoadGuestU32(base, pm) != refii::guest::Address(0x82006CC0u)) return;
  const uint32_t begin = LoadGuestU32(base, pm + 56u);
  const uint32_t end = LoadGuestU32(base, pm + 60u);
  if (!LooksLikeGuestPointer(begin) || end < begin || end - begin > 24u * 16u) return;
  for (uint32_t rec = begin; rec + 24u <= end; rec += 24u) {
    if (base[rec + 20u] == 0u) continue;
    const uint32_t holder = LoadGuestU32(base, rec + 4u);
    if (!LooksLikeGuestPointer(holder)) continue;
    pose(LoadGuestU32(base, holder), "screen");
  }
}

void CoopAnimatePreview(PPCContext& ctx, uint8_t* base) {
  (void)ctx;
  const auto [entity, handler] = CoopMannequin(base);
  (void)handler;
  PosePreviewMannequins(base, entity);
  PublishClothingMannequin(ClothingMannequin(base));
}

void CoopRedress(PPCContext& ctx, uint8_t* base) {
  CoopRepairNetworkSlot(base);
  CoopDressMannequin(ctx, base);
  CoopAnimatePreview(ctx, base);
  static bool was_live = false;
  const bool live = CoopSessionLive(base);
  if (was_live && !live) {
    CoopResetLatches();
    REXLOG_INFO("Refii guest-tick pump: co-op — the peer is gone; every per-body latch dropped");
  }
  was_live = live;
  if (!live) {
    return;  // no peer standing: player two is being torn down or never existed
  }

  // Player one's record applies only on the joiner (its copy of the host's hero); player
  // two's only while player two stands in their own hero's body.
  if (IsCoopJoiner(base)) {
    CoopApplyHero(ctx, base, 1, CoopPlayerOneEntity(base));
    CoopAttachHeld(ctx, base, 1, CoopPlayerOneEntity(base));
  }

  if (g_coop_own_hero.load(std::memory_order_relaxed)) {
    CoopApplyHero(ctx, base, 2, CoopPlayerTwoEntity(base));
    if (IsCoopJoiner(base)) {
      CoopAttachHeld(ctx, base, 2, CoopPlayerTwoEntity(base));
    }
  } else {
    g_coop_dressed_entity[1] = 0u;
    g_coop_dressed_set[1].clear();
  }
}

/// See kActionCoopHeroStatsPrefix.
///
/// THE GUEST'S OWN APPLY, HANDED OUR BLOCK. `sub_8243AEF8` loads a chosen save's HeroSave into
/// `SaveMgr+1016` and then walks player two's components (`entity+72..76`, stride 8, component
/// at `+4`) calling each one's vtable slot 40 with that block (0x8243B02C–0x8243B060). The block
/// is what `sub_82440F78` deserialises: +0 Money, +4 Gender, +8 Morality, +12 Purity, +16 Renown,
/// +20..+76 HeroAbility0..14, an `Experience` float vector object at +80 (begin +84, end +88),
/// `TotalExperience` at +96 (begin +100, end +104), +112 SecondsPlayedAsHenchman, +116
/// CanUnlockAchievements. Built here on a scratch frame, the two arrays beside it, and walked
/// the same way — so money, morality, purity, renown, ranks and experience land on player two
/// on BOTH machines from the kit, with no save on the host and no picker on the joiner.
///
/// Once per (entity, block): the walk is what a save load does, and a save is loaded once.
uint32_t g_coop_stats_entity = 0u;
std::string g_coop_stats_set;

bool DispatchCoopHeroStats(const std::string& text) {
  g_coop_record[1].stats = text;
  return true;
}

void ApplyStats(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& text) {
  if (text.empty()) {
    return;
  }

  std::vector<std::string> fields;
  std::size_t at = 0;
  while (at <= text.size()) {
    const std::size_t comma = text.find(',', at);
    fields.push_back(text.substr(at, comma == std::string::npos ? comma : comma - at));
    at = comma == std::string::npos ? text.size() + 1u : comma + 1u;
  }

  // money, gender, morality, purity, renown, 15 ranks, 4 pools, 4 totals = 28 fields.
  if (fields.size() < 28u) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroStats has {} field(s), wants 28 — dropped",
                fields.size());
    return;
  }

  // ONCE PER BODY. The block is the chosen save's snapshot; from then on the host's world owns
  // player two's money and experience, and a restated block (its money field drifting with the
  // joiner's own reads) must not keep resetting them.
  if (entity == 0u || entity == g_coop_stats_entity) {
    return;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!LooksLikeGuestPointer(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return;
  }

  g_coop_stats_entity = entity;
  g_coop_stats_set = text;

  const auto as_int = [&](std::size_t i) -> uint32_t {
    return static_cast<uint32_t>(std::strtol(fields[i].c_str(), nullptr, 10));
  };
  const auto as_float_bits = [&](std::size_t i) -> uint32_t {
    const float value = std::strtof(fields[i].c_str(), nullptr);
    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
  };

  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t block = frame + 0x20u;      // 0x80 bytes
  const uint32_t pools = frame + 0xA0u;      // 4 floats
  const uint32_t totals = frame + 0xB0u;     // 4 floats
  for (uint32_t i = 0; i < 0x80u; i += 4u) StoreGuestU32(base, block + i, 0u);

  StoreGuestU32(base, block + 0u, as_int(0));            // Money
  StoreGuestU32(base, block + 4u, as_int(1));            // Gender, 2 = male
  StoreGuestU32(base, block + 8u, as_float_bits(2));     // Morality
  StoreGuestU32(base, block + 12u, as_float_bits(3));    // Purity
  StoreGuestU32(base, block + 16u, as_int(4));           // Renown
  for (uint32_t i = 0; i < 15u; ++i) {
    StoreGuestU32(base, block + 20u + 4u * i, as_int(5u + i));
  }
  for (uint32_t i = 0; i < 4u; ++i) {
    StoreGuestU32(base, pools + 4u * i, as_float_bits(20u + i));
    StoreGuestU32(base, totals + 4u * i, as_float_bits(24u + i));
  }
  StoreGuestU32(base, block + 84u, pools);              // Experience begin
  StoreGuestU32(base, block + 88u, pools + 16u);        // end
  StoreGuestU32(base, block + 92u, pools + 16u);        // capacity end
  StoreGuestU32(base, block + 100u, totals);            // TotalExperience begin
  StoreGuestU32(base, block + 104u, totals + 16u);
  StoreGuestU32(base, block + 108u, totals + 16u);
  StoreGuestU32(base, block + 112u, 0u);                // SecondsPlayedAsHenchman
  // A BYTE, not a word: `sub_82A1CE48` fills a bool, and on this big-endian guest a word of 1
  // would leave 0x00 at +116 and the 0x01 at +119.
  base[block + 116u] = 1u;                              // CanUnlockAchievements

  uint32_t called = 0u;
  for (uint32_t pair = pairs; pair + 8u <= pairs_end; pair += 8u) {
    const uint32_t component = LoadGuestU32(base, pair + 4u);
    if (!LooksLikeGuestPointer(component)) continue;
    const uint32_t vtable = LoadGuestU32(base, component);
    if (!LooksLikeGuestPointer(vtable)) continue;
    const uint32_t method = LoadGuestU32(base, vtable + 40u);
    PPCFunc* apply = FindGuestFunction(method);
    if (apply == nullptr) continue;

    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = component;
    work.r4.u32 = block;
    apply(work, base);
    ++called;
  }

  REXLOG_INFO("Refii guest-tick pump: CoopHeroStats — HeroSave block applied to player two "
              "0x{:08X} through {} component slot-40 method(s); money {} renown {}",
              entity, called, fields[0], fields[4]);
}

/// See kActionCoopHeroFacePrefix. Once per (entity, record).
uint32_t g_coop_face_entity = 0u;
uint32_t g_coop_face_record = 0u;

bool DispatchCoopHeroFace(const std::string& text) {
  g_coop_record[1].face = static_cast<uint32_t>(std::strtoul(text.c_str(), nullptr, 16));
  return true;
}

void ApplyFace(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t record) {
  if (record == 0u || entity == 0u ||
      (entity == g_coop_face_entity && record == g_coop_face_record)) {
    return;
  }

  const uint32_t morph = EntityComponent(base, entity, 17u);
  if (morph == 0u) {
    return;  // the body has no appearance-morph component yet; the next apply asks again
  }

  g_coop_face_entity = entity;
  g_coop_face_record = record;

  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
  work.r3.u32 = morph;
  work.r4.u32 = record;
  rex_CECCompositeModel_ApplyCharacterCreationRecord(work, base);

  REXLOG_INFO("Refii guest-tick pump: CoopHeroFace — record 0x{:08X} set on player two "
              "0x{:08X} (component 17 0x{:08X})", record, entity, morph);
}

/// See kActionCoopHeroEquipPrefix.
bool DispatchCoopHeroEquip(const std::string& text) {
  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return true;
  }

  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return true;
  }

  const std::string pair = text.substr(colon + 1u);
  const std::size_t comma = pair.find(',');
  g_coop_record[player - 1].melee =
      static_cast<uint32_t>(std::strtoul(pair.substr(0, comma).c_str(), nullptr, 16));
  g_coop_record[player - 1].ranged = comma == std::string::npos
      ? 0u
      : static_cast<uint32_t>(std::strtoul(pair.substr(comma + 1u).c_str(), nullptr, 16));
  return true;
}

/// The guest's own equip-by-record, STEP BY STEP, with the one thing it cannot do for us done
/// here. `sub_824ABA28(comp28, id, 0)` (0x824ABA28, decompiled) is: slot name from the record's
/// WeaponType (`sub_824AC920`), instantiate the held record (`sub_824F26A8` → `sub_824F2870` →
/// `sub_8238B860`), attach (`sub_824FB5D8(comp24, name, item)`), record (`sub_824AD108`), draw
/// effect (`sub_824AB170`). `sub_8238B860` (0x8238B860) skips ACTIVATION (`sub_8238BE08`) when the
/// manager says client and `+239` is clear — the sync's own flag — and the attach then declines
/// an unactivated item, which is what left swords on the ground. Here the item is activated
/// when the guest did not, each step reports, and nothing is retried: a pair is one attempt per
/// body, because every attempt instantiates an item.
/// One record onto one body: the guest's own equip-by-record sequence, step by step, with the
/// activation the guest skips on a client done here. Appends the outcome to `report`; returns
/// true when the item was attached. NO latch — the callers decide when to try.
bool EquipRecord(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t id, std::string& report) {
  const uint32_t weapons = EntityComponent(base, entity, 28u);
  const uint32_t inventory = EntityComponent(base, entity, 13u);
  const uint32_t held = EntityComponent(base, entity, 24u);
  char step[160];
  if (weapons == 0u || inventory == 0u || held == 0u) {
    std::snprintf(step, sizeof(step), " %08X: body lacks comp 28/13/24;", id);
    report += step;
    return false;
  }

  const uint32_t frame = (ctx.r1.u32 - 0x400u) & ~0xFu;
  const uint32_t handle = frame + 0x20u;     // 4 words: sub_824F1C90's item handle
  const uint32_t name = frame + 0x40u;       // LHString "Weapon", the item entity's name
  const uint32_t text = frame + 0x80u;
  static const char kWeapon[] = "Weapon";
  std::memcpy(base + text, kWeapon, sizeof(kWeapon));

  const auto call = [&](PPCFunc* fn, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u) -> uint32_t {
    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = r3;
    work.r4.u32 = r4;
    work.r5.u32 = r5;
    fn(work, base);
    return work.r3.u32;
  };

  // 1. Held? (a plain lookup, released again)
  for (uint32_t i = 0; i < 4u; ++i) StoreGuestU32(base, handle + 4u * i, 0u);
  call(rex_CECInventory_FindItemByRecordHash, handle, inventory, id);
  const uint32_t record = LoadGuestU32(base, handle);
  if (record == 0u) {
    std::snprintf(step, sizeof(step), " %08X: NOT HELD in comp 13;", id);
    report += step;
    return false;
  }
  call(rex_lhTrackedObject_RemoveHandleRef, record, handle);

  // 2. The slot name for the record's WeaponType.
  const uint32_t slot = call(rex_CECWeaponSet_GetSlotNameForRecord, weapons, id);
  const uint32_t slot_block = slot != 0u ? LoadGuestU32(base, slot) : 0u;
  if (slot_block == 0u || LoadGuestU32(base, slot_block + 4u) == 0u) {
    std::snprintf(step, sizeof(step), " %08X: no slot name (WeaponType unresolved);", id);
    report += step;
    return false;
  }

  // 3. Instantiate the held record, with the sync's activation flag raised on a client.
  call(rex_lhCharString_CtorFromCStr, name, text, 0xFFFFFFFFu);
  const uint32_t manager = CoopNetworkManager(base);
  const bool client = IsCoopJoiner(base);
  const uint8_t was = manager != 0u ? LoadGuestU8(base, manager + 239u) : 0u;
  if (client && manager != 0u) base[manager + 239u] = 1u;
  const uint32_t item = call(rex_CECInventory_InstantiateHeldRecord, inventory, id, name);
  if (client && manager != 0u) base[manager + 239u] = was;
  if (item == 0u) {
    call(rex_lhCharString_Dtor, name);
    std::snprintf(step, sizeof(step), " %08X: instantiate returned nothing;", id);
    report += step;
    return false;
  }

  bool activated = (LoadGuestU8(base, item + 144u) & 0x40u) != 0u;
  if (!activated) {
    call(rex_lhEntity_Activate, item);
    activated = (LoadGuestU8(base, item + 144u) & 0x40u) != 0u;
  }

  // 4. Release the slot's current item and destroy it — the guest's own `sub_824FDB70` on the
  // host. Without the release the new item attaches beside the old one.
  {
    const uint32_t old = call(rex_CECHeldItems_ReleaseSlot, held, slot);
    if (old != 0u && LooksLikeGuestPointer(old) && old != item &&
        (LoadGuestU8(base, old + 144u) & 0x40u) != 0u) {
      call(rex_lhEntity_Destroy, old);
    }
  }

  // 5. Attach, record, draw.
  call(rex_CECHeldItems_AttachItemToSlot, held, slot, item);
  call(rex_CECWeaponSet_RecordDrawnItem, weapons, item);
  call(rex_CECWeaponSet_PlayWeaponDrawEffect, weapons, item);
  call(rex_lhCharString_Dtor, name);
  std::snprintf(step, sizeof(step), " %08X: item 0x%08X %s, attached;", id, item,
                activated ? "active" : "NOT ACTIVE even after sub_8238BE08");
  report += step;
  return true;
}

/// Player two's / player one's two weapons from the record, ONE attempt per (body, pair) —
/// every attempt instantiates an item, so nothing here retries.
std::map<uint32_t, std::pair<uint32_t, uint32_t>> g_coop_equip_latched;

void ApplyEquip(PPCContext& ctx, uint8_t* base, int player, uint32_t entity, uint32_t melee,
                uint32_t ranged) {
  if (entity == 0u || (melee == 0u && ranged == 0u)) {
    return;
  }

  // A client never instantiates: the host's items replicate as entities and CoopAttachHeld
  // attaches those by net id.
  if (IsCoopJoiner(base)) {
    return;
  }

  const auto seen = g_coop_equip_latched.find(entity);
  if (seen != g_coop_equip_latched.end() && seen->second == std::make_pair(melee, ranged)) {
    return;
  }

  if (EntityComponent(base, entity, 28u) == 0u) {
    return;  // no weapon set yet; the next apply asks again
  }

  g_coop_equip_latched[entity] = std::make_pair(melee, ranged);

  std::string report;
  for (uint32_t id : {melee, ranged}) {
    if (id != 0u) EquipRecord(ctx, base, entity, id, report);
  }

  const uint32_t weapons = EntityComponent(base, entity, 28u);
  REXLOG_INFO("Refii guest-tick pump: CoopHeroEquip — player {} 0x{:08X}:{} weapon set 0x{:08X} "
              "now holds melee 0x{:08X}, ranged 0x{:08X}",
              player, entity, report, weapons, LoadGuestU32(base, weapons + 84u),
              LoadGuestU32(base, weapons + 88u));
}

/// The host's held items per player, as 64-bit net ids (the entity uid, high word `+0x14`,
/// low word `+0x18`): [player-1][0] melee, [1] ranged.
uint64_t g_coop_held[2][2] = {{0u, 0u}, {0u, 0u}};

/// Body → the (melee, ranged) net ids it was last attached with.
std::map<uint32_t, std::pair<uint64_t, uint64_t>> g_coop_held_attached;

/// See kActionCoopHeroHeldPrefix — stores; the tick attaches.
bool DispatchCoopHeroHeld(const std::string& text) {
  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return true;
  }

  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return true;
  }

  const std::string pair = text.substr(colon + 1u);
  const std::size_t comma = pair.find(',');
  g_coop_held[player - 1][0] =
      static_cast<uint64_t>(std::strtoull(pair.substr(0, comma).c_str(), nullptr, 16));
  g_coop_held[player - 1][1] = comma == std::string::npos
      ? 0u
      : static_cast<uint64_t>(std::strtoull(pair.substr(comma + 1u).c_str(), nullptr, 16));
  return true;
}

/// On a client: the host's replicated weapon entities, by net id, put on the body SILENTLY when
/// the guest's own attach did not land.
///
/// THE GUEST'S ATTACH RACES ITS OWN CREATE. The host's EquipRecord ends in `sub_824FB5D8`
/// (attach to slot), which on an authoritative holder sends a held-items message of type 1
/// (0x824fcb10); the client's handler `sub_824FEC48` resolves the replica with
/// `sub_82334A60(netmgr, netId)` and attaches it — but only an ACTIVE item (`+144 & 0x40`,
/// 0x824fb610), and a replica spawned by the create message just before it is not yet active
/// (`sub_8238B860` skips activation on a client), so the attach is dropped on the floor and
/// nothing retries it. Player one's weapons land because they were active long before the
/// join sync restated them; player two's are made at the moment they are equipped (run
/// 2026-10-07 20:31: both replicas present, "NOT on the body yet", for the whole session).
///
/// NEVER ATTACH WITH AUTHORITY FROM HERE. Both machines are authoritative for player two's
/// body (`sub_82214AF8`: the host because it is not locally controlled there, the client
/// because it is), so an attach made here would be sent to the host, whose `sub_824FB5D8`
/// finds the slot occupied (`sub_824F9AC8`) and RELEASES AND DESTROYS the occupant
/// (`sub_824FDB70`, 0x824fb878) — the very item — before re-attaching it; an earlier version
/// of this routine that released and attached with authority bounced one weapon off and the
/// other on every few seconds (run 19:21). The send is gated on the holder's CECNetwork
/// byte `+104` ("locally controlled", read by `sub_82214AF8` at 0x82214b78), so that byte is
/// cleared for the one call and restored: the attachment graph is updated here, nothing
/// crosses the wire, and the host keeps its own attachment, which it already has.
void CoopAttachHeld(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  if (entity == 0u) {
    return;
  }

  const uint64_t melee_net = g_coop_held[player - 1][0];
  const uint64_t ranged_net = g_coop_held[player - 1][1];
  if (melee_net == 0u && ranged_net == 0u) {
    return;
  }

  const auto seen = g_coop_held_attached.find(entity);
  if (seen != g_coop_held_attached.end() && seen->second == std::make_pair(melee_net, ranged_net)) {
    return;
  }

  const uint32_t manager = CoopNetworkManager(base);
  const uint32_t weapons = EntityComponent(base, entity, 28u);
  const uint32_t held = EntityComponent(base, entity, 24u);
  if (manager == 0u || weapons == 0u || held == 0u) {
    return;
  }

  const uint32_t frame = (ctx.r1.u32 - 0x400u) & ~0xFu;
  const auto call = [&](PPCFunc* fn, uint32_t r3, uint64_t r4 = 0u, uint32_t r5 = 0u) -> uint32_t {
    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = r3;
    work.r4.u64 = r4;
    work.r5.u32 = r5;
    fn(work, base);
    return work.r3.u32;
  };

  // What the body holds: component 24 entries of 12 bytes at `+16..+20`, holder at entry+8,
  // item = `*holder` (`sub_824FB4E8`).
  const auto held_here = [&](uint32_t item) -> bool {
    const uint32_t begin = LoadGuestU32(base, held + 16u);
    const uint32_t end = LoadGuestU32(base, held + 20u);
    if (!LooksLikeGuestPointer(begin) || end < begin || (end - begin) > 12u * 64u) return false;
    for (uint32_t at = begin; at + 12u <= end; at += 12u) {
      const uint32_t holder = LoadGuestU32(base, at + 8u);
      if (LooksLikeGuestPointer(holder) && LoadGuestU32(base, holder) == item) return true;
    }
    return false;
  };

  std::string report;
  char step[160];
  bool complete = true;
  for (uint64_t net : {melee_net, ranged_net}) {
    if (net == 0u) {
      continue;
    }

    const uint32_t item = call(rex_lhNetManager_FindEntityByNetId, manager, net);
    if (!LooksLikeGuestPointer(item) || (LoadGuestU8(base, item + 144u) & 0x40u) == 0u) {
      complete = false;
      continue;  // not replicated here yet
    }

    const uint32_t record = LoadGuestU32(base, item + 28u);
    if (held_here(item)) {
      std::snprintf(step, sizeof(step), " net %016llX: replica 0x%08X (record %08X) on the body;",
                    static_cast<unsigned long long>(net), item, record);
      report += step;
      continue;
    }

    const uint32_t slot = call(rex_CECWeaponSet_GetSlotNameForRecord, weapons, record);
    const uint32_t slot_block = slot != 0u ? LoadGuestU32(base, slot) : 0u;
    if (slot_block == 0u || LoadGuestU32(base, slot_block + 4u) == 0u) {
      std::snprintf(step, sizeof(step), " net %016llX: record %08X has no slot name;",
                    static_cast<unsigned long long>(net), record);
      report += step;
      complete = false;
      continue;
    }

    // Silently: the holder is not "locally controlled" for the length of the call.
    const uint32_t network = EntityComponent(base, entity, 91u);
    const uint8_t controlled = network != 0u ? LoadGuestU8(base, network + 104u) : 0u;
    if (network != 0u) base[network + 104u] = 0u;
    call(rex_CECHeldItems_AttachItemToSlot, held, slot, item);
    if (network != 0u) base[network + 104u] = controlled;

    const bool attached = held_here(item);
    if (!attached) {
      complete = false;
    }
    std::snprintf(step, sizeof(step), " net %016llX: replica 0x%08X (record %08X) %s;",
                  static_cast<unsigned long long>(net), item, record,
                  attached ? "attached here, silently" : "attach REFUSED");
    report += step;
  }

  static std::map<uint32_t, std::pair<uint64_t, uint64_t>> waited;
  if (!complete) {
    if (waited[entity] != std::make_pair(melee_net, ranged_net)) {
      waited[entity] = std::make_pair(melee_net, ranged_net);
      REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}: waiting for the "
                  "host's replicas (melee net {:016X}, ranged net {:016X}){}",
                  player, entity, melee_net, ranged_net, report);
    }
    return;
  }

  g_coop_held_attached[entity] = std::make_pair(melee_net, ranged_net);
  REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}:{} weapon set 0x{:08X} "
              "holds melee 0x{:08X}, ranged 0x{:08X}",
              player, entity, report, weapons, LoadGuestU32(base, weapons + 84u),
              LoadGuestU32(base, weapons + 88u));
}

/// Body → generation last applied. Cleared with the other latches when the session drops.
std::map<uint32_t, uint32_t> g_coop_applied_generation;

/// See kActionCoopHeroApplyPrefix — stores the generation; the tick applies.
bool DispatchCoopHeroApply(const std::string& text) {
  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return true;
  }

  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return true;
  }

  g_coop_record[player - 1].generation =
      static_cast<uint32_t>(std::strtoul(text.substr(colon + 1u).c_str(), nullptr, 16));
  return true;
}

/// THE ONE APPLY. Give the inventory, wear the outfit, draw the weapons, then for player two
/// the HeroSave block, ranks, body morphs and face — in that order, because wear needs the
/// records held, equip needs the weapons held, and the stats walk needs the body to be the one
/// they belong to. Once per (body, generation): every step is idempotent on its own, so a new
/// generation re-runs only what changed (a new outfit re-wears; the same weapons are not
/// re-drawn). A body the guest rebuilds (level change, retype) is a new entity and gets the
/// standing generation again from the tick.
void CoopApplyHero(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  CoopHeroRecord& record = g_coop_record[player - 1];
  if (entity == 0u || record.generation == 0u) {
    return;
  }

  if (g_coop_applied_generation[entity] == record.generation) {
    return;
  }

  CoopGivePlayer(ctx, base, player, entity);
  CoopRedressPlayer(ctx, base, player, entity);
  ApplyEquip(ctx, base, player, entity, record.melee, record.ranged);
  if (player == 2) {
    ApplyStats(ctx, base, entity, record.stats);
    ApplySkills(ctx, base, entity, record.skills);
    ApplyBuild(ctx, base, entity, record.build);
    ApplyFace(ctx, base, entity, record.face);
  }

  g_coop_applied_generation[entity] = record.generation;
  REXLOG_INFO("Refii guest-tick pump: CoopApplyHero — generation {:08X} applied to player {} "
              "0x{:08X}", record.generation, player, entity);
}

/// See kActionCoopHeroBodyPrefix.
bool DispatchCoopHeroBody(PPCContext& ctx, uint8_t* base, const std::string& which) {
  // The four hero bodies, from `scripts/Startup/DebugMenu.txt` — the readable index of spawnable
  // names, since GDB record names are FNV-1 hashes and appear nowhere as strings. The henchman
  // names are here too so a run can put the old body back without restarting.
  // `auto` takes the body from the co-op screen's own appearance pickbox, so pressing X on a
  // female henchman gives a female hero. The index lives at `coopMgr+260` (`sub_8233D090`
  // stores it at 0x8233D110) and maps the same way `sub_8233C508` reads it: 0-2 female,
  // 3-5 male, 6 male child, 7 female child.
  std::string choice = which;
  const char* henchman = nullptr;
  if (choice == "auto" || choice == "pickbox") {
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
      return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
    };

    const uint32_t coop = step(step(LoadGuestU32(base, refii::guest::Address(0x83496AB8u)), 12u), 140u);
    const uint32_t appearance =
        LooksLikeGuestPointer(coop) ? LoadGuestU32(base, coop + 260u) : 0u;

    if (choice == "pickbox") {
      // `sub_8233C508`'s own table, so going BACK lands on the henchman the pickbox is
      // actually showing rather than a fixed one.
      static const char* const kHenchmen[] = {
          "CreatureHeroHenchmanFemaleNeutral", "CreatureHeroHenchmanFemaleGood",
          "CreatureHeroHenchmanFemaleEvil",    "CreatureHeroHenchmanMaleNeutral",
          "CreatureHeroHenchmanMaleGood",      "CreatureHeroHenchmanMaleEvil",
          "CreatureHeroChild",                 "CreatureHeroFemaleChild",
      };

      henchman = kHenchmen[appearance < 8u ? appearance : 1u];
    } else {
      choice = appearance == 6u   ? "child"
               : appearance == 7u ? "femalechild"
               : appearance >= 3u ? "male"
                                  : "female";
    }
  }

  const char* creature =
      henchman != nullptr       ? henchman
      : choice == "male"        ? "CreatureHero"
      : choice == "female"      ? "CreatureHeroFemale"
      : choice == "child"       ? "CreatureHeroChild"
      : choice == "femalechild" ? "CreatureHeroFemaleChild"
      : choice == "henchman"    ? "CreatureHeroHenchmanFemaleGood"
                                : nullptr;
  if (creature == nullptr) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroBody:{} names no creature — dropped", which);
    return true;
  }

  // THE BIT IS THE MECHANISM; the call below is only a nudge.
  //
  // `sub_8233D090` is not a one-shot. `sub_82436548` re-runs it EVERY FRAME the co-op screen is
  // up (0x824365CC gates on `!screen+268 && screen+48 && screen+160`, then 0x824365DC →
  // `sub_82437168` → 0x8243720C), and `sub_823276D0` re-applies it from the manager's stored
  // index on level change and on save load. Retyping the entity ourselves would therefore be
  // undone within a frame on the screen, and again the moment play starts. What survives all of
  // that is answering `sub_8233C508` — the sole source of player two's creature name — which
  // `coop_player_hooks.cpp` overrides and this bit drives.
  RefiiCoopSetOwnHero(henchman == nullptr && choice != "henchman" ? 1 : 0);

  const uint32_t player = PlayerWithId(base, 2u);
  if (player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroBody:{} — no player two in the manager", which);
    return true;
  }

  // `sub_82389FB8` locks the Player's weak_ptr FIRST (0x82389FC4) and only retypes when it
  // resolves to a live entity. Player two is created with no entity attached (`a8 = 0` at
  // 0x82316C00) and `sub_8233D090` attaches it later (0x8233D698), so this is a no-op until the
  // henchman has actually spawned — call it after the join completes, not during.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t handle = frame + 0x1A0u;

  const std::size_t length = std::strlen(creature);
  for (std::size_t i = 0; i < length; ++i) {
    base[text + i] = static_cast<uint8_t>(creature[i]);
  }

  base[text + length] = 0;
  StoreGuestU32(base, handle, 0u);

  // The name is an LHString, not a char* — `sub_8233C508` builds the guest's own the same way.
  // r5 = -1 is the length `sub_822078A0` reads as strlen, and it must be set.
  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  rex_lhCharString_CtorFromCStr(work, base);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = player;
  work.r4.u32 = handle;
  rex_lhPlayer_ChangeEntityType(work, base);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  rex_lhCharString_Dtor(work, base);

  REXLOG_INFO("Refii guest-tick pump: CoopHeroBody:{} — asked player two (0x{:08X}) to become "
              "'{}'", which, player, creature);
  return true;
}

/// See kActionRegisterPlayer.
bool DispatchRegisterPlayer(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t player = PlayerWithId(base, 1u);
  if (!plausible(gui) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: RegisterPlayer with no GUI / player — dropped");
    return true;
  }

  const uint32_t before = LoadGuestU8(base, gui + 0x65u);
  if (before != 0u) {
    REXLOG_INFO("Refii guest-tick pump: RegisterPlayer — {} already registered, nothing done", before);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = gui;
  ctx.r4.u32 = player;
  rex_lhGui_RegisterPlayer(ctx, base);
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: RegisterPlayer — GUI+0x65 {} -> {} (player 0x{:08X})",
              before, LoadGuestU8(base, gui + 0x65u), player);
  return true;
}

/// The expression wheel's three levers — see kActionQuickWheelPrefix.
///
/// QM is `*(GUI+28)`; the controller W is `*(*(GUI+32)+60)`; the player object is the one
/// `sub_824040B8` hands the same routines (`a1[3]`, the consumer's bound player), which is the
/// party's first player — `PlayerObject`.
bool DispatchQuickWheel(PPCContext& ctx, uint8_t* base, const std::string& verb) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t wheel = plausible(hud) ? LoadGuestU32(base, hud + 60u) : 0u;
  const uint32_t player = LocalPlayer(base);
  if (!plausible(quick) || !plausible(wheel) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} with no quick menu / player — dropped", verb);
    return true;
  }

  const bool open = LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u;
  const PPCContext saved = ctx;
  if (verb == "open") {
    if (open) {
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    rex_lhGuiManager_CanPlayerOpenQuickMenu(ctx, base);
    const bool may = (ctx.r3.u32 & 0xFFu) != 0u;
    if (may) {
      ctx = saved;
      ctx.r3.u32 = quick;
      ctx.r4.u32 = player;
      rex_lhGuiManager_OpenExpressionWheel(ctx, base);
    }

    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:open — {}",
                may ? (LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u ? "opened" : "refused by sub_8236F110")
                    : "refused by sub_8236F400 (hero busy or not in a walking mode)");
    return true;
  }

  if (verb == "close") {
    if (open) {
      ctx.r3.u32 = quick;
      ctx.r4.u32 = 1u;
      rex_lhGuiManager_CloseExpressionWheel(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:close — closed");
    }

    return true;
  }

  if (verb == "select") {
    if (open) {
      ctx.r3.u32 = wheel;
      ctx.r4.u32 = 1u;  // primary player
      rex_lhGuiExpressionWheel_Accept(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:select — cursor {}/{} state {}",
                  static_cast<int32_t>(LoadGuestU32(base, wheel + 16u)),
                  static_cast<int32_t>(LoadGuestU32(base, wheel + 20u)),
                  LoadGuestU32(base, wheel));
    }

    return true;
  }

  if (verb == "back") {
    if (open) {
      ctx.r3.u32 = wheel;
      rex_lhGuiExpressionWheel_Back(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:back");
    }

    return true;
  }

  if (verb == "release") {
    // `sub_826AA5D8` ends a HELD expression (state 2) and puts the wheel back on slide 4;
    // it never closes. Harmless when nothing is held.
    if (open) {
      ctx.r3.u32 = wheel;
      ctx.r4.u32 = player;
      rex_lhGuiExpressionWheel_EndHeldExpression(ctx, base);
      ctx = saved;
    }

    return true;
  }

  if (verb.rfind("point:", 0) == 0) {
    char* end = nullptr;
    const unsigned long sector = std::strtoul(verb.c_str() + 6, &end, 10);
    if (end == verb.c_str() + 6 || *end != '\0' || sector > 7u) {
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} is not a sector 0..7 — dropped", verb);
      return true;
    }

    if (open && LoadGuestU32(base, wheel) == 1u) {
      // The unit vector `sub_826AA060` resolves to sector i: it negates y and compares
      // (x, -y) against (sin a, cos a), so x = sin a and y = -cos a land exactly on it.
      const double angle = 2.3561945 - (static_cast<double>(sector) * 0.7853982);
      const float x = static_cast<float>(std::sin(angle));
      const float y = static_cast<float>(-std::cos(angle));
      uint32_t xBits = 0;
      uint32_t yBits = 0;
      std::memcpy(&xBits, &x, sizeof(xBits));
      std::memcpy(&yBits, &y, sizeof(yBits));
      ctx.r3.u32 = wheel;
      // One 64-bit r4: `std r4` at 0x826AA078 then `lfs` from its first and second word —
      // x is the HIGH word, y the LOW. `Register` is a union, so `.u32` would set only y.
      ctx.r4.u64 = (static_cast<uint64_t>(xBits) << 32) | yBits;
      rex_lhGuiExpressionWheel_SetCursorFromStick(ctx, base);
      ctx = saved;
    }

    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} is not a verb — dropped", verb);
  return true;
}

/// The spells menu's two levers — see kActionSpellsMenuPrefix.
///
/// QM is `*(GUI+28)`, the SAME object the expression wheel rides; the player is `PlayerObject`,
/// which is the `a1[3]` that `sub_824040B8` hands these routines. Both derive the player INDEX
/// themselves from `*(player+36)`, so the lever passes none.
///
/// EVERY GATE STAYS THE GUEST'S, and all of them are inside `sub_8236F788`
/// (0x8236f79c..0x8236f7fc): the already-open flag at QM+36 (player 1) / QM+37 (player 2),
/// `sub_826B4448`, and `sub_8236F400` (hero alive, player mode 1/5/20/21/22). It returns 1
/// when it opened and 0 when it refused.
///
/// THE FIVE-WIDE AXIS IS THE POWER (CHARGE) LEVEL, NOT A RING OR A D-PAD SLOT, and
/// `sub_826B4448` sweeps the EIGHT SPELL TYPES at power level 0 only — its cursor starts at
/// `mgr+196` with no level term (0x826b4710) and strides 5 (0x826b473c), needing a type that
/// is both AWARDED (`mgr+156+5*type`) and PURCHASED (`mgr+196+5*type`), and it wants TWO
/// (`cmpwi r9, 1 / bgt`, 0x826b4730). So one will at level 0 is a correct refusal: there is
/// nothing to assign between. That is the gate the expression wheel does not have.
///
/// A REFUSAL HAS SEVEN CAUSES INSIDE `sub_826B4448` ALONE (no hero handle, entity flag 0x40,
/// component 118 absent, component 56 absent, `mgr+617` clear, `sub_8253E220`, then the
/// count), plus the already-open flag and `sub_8236F400`'s own four. The log below must NOT
/// name one of them: it cannot tell them apart from here.
bool DispatchSpellsMenu(PPCContext& ctx, uint8_t* base, const std::string& verb) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t player = LocalPlayer(base);
  if (!plausible(quick) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: SpellsMenu:{} with no quick menu / player — dropped",
                verb);
    return true;
  }

  const uint32_t which = LoadGuestU32(base, player + 36u);
  const bool open = LoadGuestU8(base, quick + refii::guest::GuiOwnerField(which == 2u ? 37u : 36u)) != 0u;
  const PPCContext saved = ctx;

  // THE PUMP MAY ONLY CLOSE WHAT THE PUMP OPENED — this is a control-loss bug, not tidiness.
  //
  // `sub_8236F968` DISARMS ITSELF BEFORE IT DOES THE WORK: it clears QM+36/+37 at 0x8236f9b8
  // unconditionally, and only then pops the id-5 `GuiBlock` consumer at 0x8236fa00, keyed on
  // `*(a2 + 36)` — the player object WE pass. Miss that pop and the consumer stays on the
  // player's stack for ever, because every other closer is gated on the flag just cleared
  // (`sub_824040B8` case 0x2B at 0x824042e8, `sub_8236EB68` at 0x8236ec88 / 0x8236ecb4). One
  // missed pop is unrecoverable.
  //
  // A stuck id-5 is exactly "opened the blade, closed it, lost all control": `sub_823D93E8`
  // rebuilds the action map from the TOP consumer's id, and the id-5 arm (0x823D96D0)
  // registers only 5, 9, 11, 1, 12 and 10 — movement and camera (0, 2, 6, 3, 4, 7) are simply
  // absent.
  //
  // AND THE GUEST RUNS 42/43 ITSELF. `sub_8236F788` has exactly ONE code caller in the image,
  // 0x82404290 inside the GameAction dispatcher, and the only non-zero writers of QM+36/+37
  // are its own stores at 0x8236f814/0x8236f81c — yet the flag goes set while this lever logs
  // nothing but refusals. So the two mechanisms race, and the host's must never close a blade
  // the guest opened.
  static bool pump_opened = false;

  if (verb == "open") {
    if (open) {
      // Already up, and not by us: the guest's case 0x2A did it, so its own 0x2B owns the
      // close and the pop that goes with it.
      pump_opened = false;
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    rex_lhGuiManager_OpenSpellsMenu(ctx, base);
    const bool opened = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;
    pump_opened = opened;
    REXLOG_INFO("Refii guest-tick pump: SpellsMenu:open — {}",
                opened ? "opened"
                       : "refused by sub_8236F788 — one of its own gates said no; the "
                         "commonest is sub_826B4448 wanting TWO spell types awarded AND "
                         "purchased at power level 0, but this cannot tell which fired");
    return true;
  }

  if (verb == "close") {
    if (!open) {
      pump_opened = false;
      return true;
    }

    if (!pump_opened) {
      REXLOG_INFO("Refii guest-tick pump: SpellsMenu:close — the GUEST opened this one, so its "
                  "own GameAction 43 owns the close; closing it here would clear the flag at "
                  "0x8236f9b8 and orphan the id-5 GuiBlock, which costs the player movement "
                  "and camera for the rest of the session");
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    rex_lhGuiManager_CloseSpellsMenu(ctx, base);
    ctx = saved;
    pump_opened = false;
    REXLOG_INFO("Refii guest-tick pump: SpellsMenu:close — closed");
    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: SpellsMenu:{} is not a verb — dropped", verb);
  return true;
}

/// The quick blade's lever — see kActionSpellBladePrefix.
///
/// `sub_826B5430(r3 = widget, r4 = code, r5 = player, r6 = screen)`. r5 is never read; r6 is
/// read only by case 36's `sub_826B4EF8(a1, a4)`. Both are passed as the guest passes them
/// (0x824052a0 / 0x8240526c) rather than zeroed, so the call is the guest's call.
bool DispatchSpellBlade(PPCContext& ctx, uint8_t* base, const std::string& verb) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t code = 0u;
  if (verb == "up") {
    code = 28u;  // `++row` toward the higher power levels (0x826b54a8)
  } else if (verb == "down") {
    code = 29u;  // `--row` (0x826b5538)
  } else if (verb == "left") {
    code = 30u;  // `sub_826B5240`: previous offered will, committed at once
  } else if (verb == "right") {
    code = 31u;  // `sub_826B5330`: next offered will, committed at once
  } else if (verb == "release") {
    code = 32u;  // any of 32..35 resets the repeat latch (0x82881a88)
  } else if (verb == "copydown") {
    code = 36u;  // `sub_826B4EF8`: copy this level's will down into every later level offering it
  } else if (verb == "stick") {
    static bool said = false;
    if (!said) {
      said = true;
      REXLOG_INFO("Refii guest-tick pump: SpellBlade:stick is dead — code 26 resolves to nothing "
                  "while widget+165 is 0, which sub_826B4358 leaves it at; post up/down/left/right");
    }
    return true;
  } else {
    REXLOG_INFO("Refii guest-tick pump: SpellBlade:{} is not a verb — dropped", verb);
    return true;
  }

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t player = LocalPlayer(base);

  // SILENT REFUSALS THROUGHOUT: `stick` is posted every frame a trigger is held, so a log line
  // on the ordinary "nothing to do" answer would bury the run.
  if (!plausible(quick) || !plausible(hud) || player == 0u) {
    return true;
  }

  // The aim/expression latch wins outright — `sub_82405190` tests it FIRST (0x824051b0) and
  // never reaches the blade while it is set. Same order here, or a held trigger would drive the
  // expression ring and the blade at once.
  if (LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u) {
    return true;
  }

  const bool two = LoadGuestU32(base, player + 36u) == 2u;
  if (LoadGuestU8(base, quick + refii::guest::GuiOwnerField(two ? 37u : 36u)) == 0u) {
    return true;  // closed: widget+412 and the cell tables are uninitialised heap
  }

  const uint32_t widget = LoadGuestU32(base, hud + (two ? 96u : 92u));
  if (!plausible(widget)) {
    return true;
  }

  const uint32_t row_before = LoadGuestU32(base, widget + 176u);
  const uint32_t cell_before = LoadGuestU32(base, widget + 200u + (row_before * 4u));

  const PPCContext saved = ctx;
  ctx.r3.u32 = widget;
  ctx.r4.u32 = code;
  ctx.r5.u32 = two ? 2u : 1u;
  ctx.r6.u32 = LoadGuestU32(base, hud + 4u);
  rex_lhGuiWillAssignWidget_OnScreenAction(ctx, base);
  ctx = saved;

  const uint32_t row_after = LoadGuestU32(base, widget + 176u);
  const uint32_t cell_after = LoadGuestU32(base, widget + 200u + (row_after * 4u));
  if (row_after != row_before || cell_after != cell_before) {
    // ONLY ON A CHANGE. Every other frame of a held stick is the guest's own repeat throttle
    // saying no, which is the normal answer and not worth a line.
    REXLOG_INFO("Refii guest-tick pump: SpellBlade:{} — row {} -> {}, cell {} -> {} "
                "(maxRow {}, analog byte {})",
                verb, row_before, row_after, cell_before, cell_after,
                static_cast<int32_t>(LoadGuestU32(base, widget + 412u)),
                LoadGuestU8(base, widget + 165u));
  }

  return true;
}

/// The 26/27 commit: the five parameters `sub_826C1B50` reads, then the input handler's 36.
bool CommitThroughParameters(PPCContext& ctx, uint8_t* base, uint32_t screen_id,
                             const std::string& name, uint32_t type, uint32_t id) {
  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;

  WriteParameterFloat(ctx, base, frame, "Float1", static_cast<double>(id & 0xFFFFu));
  WriteParameterFloat(ctx, base, frame, "Float2", static_cast<double>((id >> 16) & 0xFFFFu));
  WriteParameterFloat(ctx, base, frame, "Float3", static_cast<double>(type));
  WriteParameterString(ctx, base, frame, "String1", name);
  WriteParameterString(ctx, base, frame, "String2", name);

  REXLOG_INFO("Refii guest-tick pump: screen {} commits '{}' (type {}, id 0x{:08X}) through the "
              "parameters and code 36",
              screen_id, name, type, id);
  ctx = saved;
  return DispatchScreenAction(ctx, base, 36u, false);
}

bool DispatchMenuEventDirect(PPCContext& ctx, uint8_t* base, const std::string& name,
                             uint32_t type, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  // THE STALE MODAL LATCH IS CLEARED BEFORE THE GATE, NOT AFTER IT.
  //
  // `sub_8236FA68`, the box opener, acts only when the mode reads idle and otherwise does
  // nothing at all, silently (0x8236fa90). On the real estate screen Buy does not purchase —
  // `sub_826B2210` case 111 only raises a confirm box, mode 3 id 30 — so the FIRST A latched
  // the mode and every A after it was a no-op. Measured live: seventy-nine presses, three
  // boxes, one purchase. The mode also gates the close, since `sub_8236ED98` is guarded by
  // `sub_822A5D70` = `*(M + 40) != 1`, so a stuck mode wedges Back too.
  //
  // AND THE WEDGE IS SELF-SUSTAINING, which is why this cannot live below `InGameScreenRoots`:
  // that helper's own first act is `if (*(owner + 40) != 1) return false`. A clear placed after
  // it is unreachable by construction — the only code that could free the latch sat behind a
  // gate demanding the latch already free. The same gate silently disables every other pump
  // path while a mode is stuck.
  //
  // So resolve the owner directly and finish the guest's own clear (`sub_8236FF58` sets mode 1
  // AND id 0) first. A MenuEvent is only ever posted while the host has no box up, so both
  // fields are stale here by definition.
  {
    const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
    const uint32_t gui = LoadGuestU32(base, kGuiObject);
    if (plausible(gui) && LoadGuestU32(base, gui) == kInGameGuiVtable) {
      const uint32_t latch = LoadGuestU32(base, gui + 28u);
      if (plausible(latch)) {
        const uint32_t stale_mode = LoadGuestU32(base, latch + 40u);
        const uint32_t stale_box = LoadGuestU32(base, latch + 44u);
        if (stale_mode != 1u || stale_box != 0u) {
          StoreGuestU32(base, latch + 40u, 1u);
          StoreGuestU32(base, latch + 44u, 0u);
          REXLOG_INFO("Refii guest-tick pump: cleared stale modal latch (mode {}, box {}) at "
                      "owner+40/+44 before '{}' — the guest's own clear (sub_8236FF58) runs on "
                      "a screen change the host replaced",
                      stale_mode, stale_box, name);
        }
      }
    }
  }

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id)) {
    return false;
  }

  // THE BOX-ID LATCH GATES EVERY SCREEN HANDLER. `sub_826C2660` (and its siblings) returns
  // before reading the event unless `*(owner+44)` — the active box id — is zero. The guest
  // pairs it with the mode byte: the openers (`sub_8236FA68`/`sub_8236FCA8`) set both, and the
  // one clear site (`sub_8236E630` at 0x8236e71c) stores `+44 = 0; +40 = 1` together on the
  // screen change a box teardown causes. Godot owns the box lifecycle, so that screen change
  // never runs: the mode came back to 1 but the id stayed, and every identity commit after the
  // first box — the spell selector's type 23 included — was swallowed unread. A MenuEvent is
  // only sent while the host has no box up, so the id is stale by definition here; finish the
  // guest's own clear before dispatching.
  PPCContext probe = ctx;
  probe.r3.u32 = owner;
  probe.r4.u32 = refii::guest::ScreenId(screen_id);
  rex_lhGuiManager_FindScreenHandler(probe, base);
  const uint32_t handler = probe.r3.u32;
  if (!plausible(handler)) {
    return false;
  }

  const uint32_t vtable = LoadGuestU32(base, handler);
  if (!plausible(vtable)) {
    return false;
  }

  // The slot is READ from the live object and matched against the handlers this build can
  // reach (`0x820CF4BC+20` is `0x826C2660`, `0x820CFEB0+20` is `0x826D1800`, and so on). The
  // recompiler emits no indirect-call path, so `(*(*(u32*)screen + 20))(screen, &event)` has to
  // become a switch; keying on the slot rather than the screen id keeps the object in charge.
  //
  // `sub_826B65F0` is ONE folded body serving three vtables (26, 27 and co-op) and handles only
  // EventTypes 140 and 141; other types are dropped by the guest's own switch.
  const uint32_t slot = LoadGuestU32(base, vtable + 20u);

  // Furniture and decoration commit on code 36, not on the event: their shared events body
  // answers only the two scroll sounds, and the choice is made by the input handler's 36 arm
  // (`sub_826B6A18` for 26), which reads the row through `sub_826C1B50` — a DoString of
  // `g_Menu:GetHighlightedMenuItemData()` then five parameter reads (`Float1`, `Float2`,
  // `Float3`, `String1`, `String2`). The DoString is deferred and never lands, so the host
  // writes those five parameters itself and then sends the 36.
  if (slot == refii::guest::Address(0x826B65F0u) && (screen_id == 26u || screen_id == 27u)) {
    return CommitThroughParameters(ctx, base, screen_id, name, type, id);
  }

  // OPTIONS (18) opens its category page the same way. Its events body `sub_826DA038`
  // answers only 140/141 (the two scroll sounds); the page opens in the input handler
  // `sub_826DA228`'s 36 arm, which reads the row through `sub_826C1B50` and takes the page as
  // `Float3 - 70` (0x826da404) — the row's own EventType, 70..73 — then sets `screen+24`,
  // disables the expandable menu, puts the book and `Foreground.Book.Pages.PageN` on their
  // open slides and `Foreground.ExpandableMenu` on slide 2 (`sub_826D9FC8`). The arm runs
  // only while `screen+24` is 0, which is the list level the host is on when it commits.
  if (slot == refii::guest::Address(0x826DA038u) && screen_id == 18u && type >= 70u && type <= 73u) {
    return CommitThroughParameters(ctx, base, screen_id, name, type, id);
  }

  switch (refii::guest::ToTu1(slot)) {
    case 0x826C2660u: case 0x826A26E8u: case 0x826D1800u: case 0x826D4D58u:
    case 0x826D9620u: case 0x826DA038u: case 0x826A92D8u: case 0x826AE5C0u:
    case 0x826C6110u: case 0x826C45C8u: case 0x826B65F0u:
    case 0x826B2210u:  // 28 real estate — `*(0x820CEA58+20)`
      break;
    default:
      REXLOG_INFO("Refii guest-tick pump: screen {} dispatches events to 0x{:08X}, which is not "
                  "a handler this build can call directly — '{}' not committed",
                  screen_id, slot, name);
      return false;
  }

  // Claimed by lowering r1 so the callee's frame cannot land on the struct it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t event = frame + 0x1A0u;

  constexpr uint32_t kMaxName = 0x17Fu;
  if (name.size() + 1u > kMaxName) {
    return false;
  }

  for (std::size_t i = 0; i < name.size(); ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }

  base[text + name.size()] = 0;
  StoreGuestU32(base, event + 0u, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = event;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;  // "NUL-terminated" — the length the guest passes at 0x8246dab8
  rex_lhCharString_CtorFromCStr(work, base);

  // After the constructor: it owns `+0` only.
  StoreGuestU32(base, event + 4u, type);
  StoreGuestU32(base, event + 8u, id);

  REXLOG_INFO("Refii guest-tick pump: dispatching '{}' (type {}, id 0x{:08X}) straight to "
              "screen {}'s handler 0x{:08X}", name, type, id, screen_id, slot);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handler;
  work.r4.u32 = event;
  switch (refii::guest::ToTu1(slot)) {
    case 0x826C2660u: rex_lhGuiAbilitiesScreen_OnMenuEvent(work, base); break;
    case 0x826A26E8u: rex_lhGuiPauseRootScreen_OnMenuEvent(work, base); break;
    case 0x826D1800u: rex_lhGuiItemsScreen_OnMenuEvent(work, base); break;
    case 0x826D4D58u: rex_lhGuiClothingScreen_OnMenuEvent(work, base); break;
    case 0x826D9620u: rex_lhGuiLogbookScreen_OnMenuEvent(work, base); break;
    case 0x826DA038u: rex_lhGuiOptionsScreen_OnMenuEvent(work, base); break;
    case 0x826A92D8u: rex_lhGuiCrimeScreen_OnMenuEvent(work, base); break;
    case 0x826AE5C0u: rex_lhGuiShopScreen_OnMenuEvent(work, base); break;
    case 0x826C6110u: rex_lhGuiWorldMapScreen_OnMenuEvent(work, base); break;
    case 0x826C45C8u: rex_lhGuiGiftGivingScreen_OnMenuEvent(work, base); break;
    case 0x826B65F0u: rex_lhGuiScreen_OnMenuEventNavSoundsOnly(work, base); break;
    case 0x826B2210u: rex_lhGuiRealEstateScreen_OnMenuEvent(work, base); break;

    // NEVER SILENTLY. This default was commented "unreachable: the switch above already
    // refused anything else" and was not: the admission switch and this one are two hand-kept
    // lists of the same slots, and 0x826B2210 was added to that one alone. Screen 28 was
    // admitted, fell through here, and did nothing — while the two REXLOG lines bracketing
    // this switch, both OUTSIDE it, reported a dispatch and a return. Thirteen Buy presses
    // read as thirteen successful commits that raised no box.
    default:
      REXLOG_INFO("Refii guest-tick pump: screen {}'s handler 0x{:08X} is admitted but has no "
                  "call in the dispatch switch — '{}' was NOT delivered",
                  screen_id, slot, name);
      break;
  }

  REXLOG_INFO("Refii guest-tick pump: '{}' RETURNED from the screen handler", name);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = event;
  rex_lhCharString_Dtor(work, base);
  return true;
}

/// The hero entity, or 0 — the same walk the host bridge's `ResolvePlayerHero` runs, which the
/// renown, orb and quest-log reads prove live every frame. The old chain here skipped the
/// GAME-ROOT DEREF — `0x83496AB8` holds a POINTER, `*(root)+12` is the game — and read twelve
/// bytes past the global instead, so every sampler gated on it published nothing (a run named
/// the gate: "quest travel … refused at hero").
uint32_t HeroEntity(uint8_t* base) {
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (!plausible(game)) {
    return 0u;
  }

  // The manager itself (`g_XrnmNetContext`); the game object's copy at +0x8C is a fallback —
  // a title-joined client has that copy empty in game.
  uint32_t session = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  if (!plausible(session)) {
    session = LoadGuestU32(base, game + 0x8Cu);
  }
  if (!plausible(session)) {
    return 0u;
  }

  const uint32_t player_key =
      (LoadGuestU8(base, session + 0x34u) != 0u && LoadGuestU8(base, session + 0x35u) != 0u)
          ? 1u
          : 2u;

  const uint32_t player_map = LoadGuestU32(base, game + 0x9Cu);
  if (!plausible(player_map)) {
    return 0u;
  }

  uint32_t entry = LoadGuestU32(base, player_map + 8u);
  const uint32_t entries_end = LoadGuestU32(base, player_map + 12u);
  if (!plausible(entry) || entries_end < entry || (entries_end - entry) > 12u * 64u) {
    return 0u;
  }

  // Prefer the role's slot, fall back to whichever player record exists — see the bridge's
  // ResolvePlayerHero for why a fixed key 2 read nothing on a joiner before it joined.
  uint32_t record = 0u;
  uint32_t other = 0u;
  for (; entry < entries_end; entry += 12u) {
    const uint32_t key = LoadGuestU32(base, entry);
    if (key == player_key) {
      record = LoadGuestU32(base, entry + 4u);
      break;
    }
    if ((key == 1u || key == 2u) && other == 0u) {
      other = LoadGuestU32(base, entry + 4u);
    }
  }
  if (record == 0u) {
    record = other;
  }

  if (!plausible(record)) {
    return 0u;
  }

  const uint32_t control = LoadGuestU32(base, record + 16u);
  const uint32_t hero = plausible(control) ? LoadGuestU32(base, control) : 0u;
  return plausible(hero) ? hero : 0u;
}

/// One component of an entity, by id, or 0.
///
/// The presence bitmask is an ARRAY, not one word: `*(u32*)(entity + 36 + 4*(id>>5))` tested at
/// `id & 31`. Component 57 lives in the second word.
uint32_t HeroComponent(uint8_t* base, uint32_t entity, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (!plausible(entity) || id >= 256u) {
    return 0u;
  }

  const uint32_t present = LoadGuestU32(base, entity + 36u + (id >> 5) * 4u);
  if (((present >> (id & 31u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t table = LoadGuestU32(base, entity + 72u);
  const uint32_t end = LoadGuestU32(base, entity + 76u);
  if (!plausible(table) || end <= table || (end - table) > 0x2000u) {
    return 0u;
  }

  for (uint32_t at = table; at < end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }

  return 0u;
}

/// Samples the hero's DERIVED state — the values only the guest's own accessors can answer.
///
/// Called, not reproduced, for the three that need it: `sub_8223D320` adds the timed and
/// permanent modifier maps inside component 57, then the GDB constants
/// `EvilMoralityAddition`/`HolyMoralityAddition` when the hero carries alignment flags 7 and 6,
/// then clamps against `c57+20`/`+24` with NaN falling to the low bound. Renown's clamp is
/// BRANCH-LOCAL — a negative stored renown with no active modifier stays negative.
///
/// The TITLE is read rather than called: `sub_824D8A10` allocates (refcounts through
/// `dword_83497088` and builds a string), and the host can resolve the tag from the record hash
/// at `c57+92`. Sampled on a cadence, not every tick — the logbook's own fill runs once on open.
/// The player object — one hop above the hero. `HeroEntity` returns the hero ENTITY; the dog is
/// a sibling handle on the object that owns both, so it is not reachable from the hero.
uint32_t PlayerObject(uint8_t* base) {
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  // Root first: `*(0x83496AB8)`, then `+12` — read without the load this chain ended in
  // zeros and the dog page stayed blank (`SampleDog` said no dog while the guest's own
  // trick ticks were on screen; the live walk reaches the dog with the load, 2026-09-03).
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t player = plausible(party) ? LoadGuestU32(base, party) : 0u;
  return plausible(player) ? player : 0u;
}

/// The dog page's numbers — all plain reads off component 97, plus the name off component 20.
void SampleDog(uint8_t* base, HeroDerived& sampled) {
  constexpr uint32_t kDogHandle = 196u;
  constexpr uint32_t kDogStatsComponent = 97u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  sampled.dog_known = false;

  const uint32_t player = PlayerObject(base);
  const uint32_t handle = player != 0u ? LoadGuestU32(base, player + kDogHandle) : 0u;
  const uint32_t dog = plausible(handle) ? LoadGuestU32(base, handle) : 0u;
  if (!plausible(dog) || (LoadGuestU8(base, dog + 144u) & 0x40u) == 0u) {
    return;
  }

  const uint32_t stats = HeroComponent(base, dog, kDogStatsComponent);
  if (stats == 0u) {
    return;
  }

  sampled.dog_known = true;
  sampled.dog_searching = static_cast<int32_t>(LoadGuestU32(base, stats + 184u));
  sampled.dog_combat = static_cast<int32_t>(LoadGuestU32(base, stats + 188u));

  const uint32_t state = LoadGuestU32(base, stats + 276u);
  sampled.dog_state = state >= 8u ? 0 : static_cast<int32_t>(state);

  // THE TITLE IS THE DOG'S NAME. `sub_829BC750` hands component 20 to `sub_823A8148`, which
  // copies the LHString at `c20+52` when its block has a length and otherwise resolves the
  // Babel key at `c20+48` through the text manager. Both go across; the host resolves the key.
  constexpr uint32_t kDogNamingComponent = 20u;
  sampled.dog_name[0] = '\0';
  sampled.dog_name_key = 0u;
  const uint32_t naming = HeroComponent(base, dog, kDogNamingComponent);
  if (naming != 0u) {
    const uint32_t custom = LoadGuestU32(base, naming + 52u);
    if (plausible(custom) && LoadGuestU32(base, custom + 4u) != 0u) {
      std::snprintf(sampled.dog_name, sizeof(sampled.dog_name), "%s",
                    ReadStatedWideLhString(base, naming + 52u).c_str());
    }
    sampled.dog_name_key = LoadGuestU32(base, naming + 48u);
  }

  // The trick vector stores GDB record IDs, not name hashes: `sub_8219C5E8` runs `FNV1(name)`
  // through `NGDB_CRecord_Get_int(mgr, hash, 4)` first, so the stored key is that lookup's
  // record ID. The ids are published raw; the host resolves names against its own GDB.
  sampled.dog_trick_count = 0u;
  const uint32_t begin = LoadGuestU32(base, stats + 740u);
  const uint32_t end = LoadGuestU32(base, stats + 744u);
  if (plausible(begin) && end > begin && (end - begin) <= 0x400u) {
    uint32_t slot = 0;
    for (uint32_t at = begin; at < end && slot < 32u; at += 8u, ++slot) {
      sampled.dog_trick_ids[slot] = LoadGuestU32(base, at);
    }

    sampled.dog_trick_count = slot;
  } else {
    sampled.dog_trick_count = 0u;
  }
}

/// The hero-side damage multiplier the spell stats rows scale by — a pure table read.
///
/// `sub_82541B30` is a 28-instruction leaf — no calls, no allocation — so it is reproduced here
/// as three loads rather than called.
void SampleSpellDamage(uint8_t* base, uint32_t hero, HeroDerived& sampled) {
  constexpr uint32_t kMagicComponent = 118u;
  constexpr uint32_t kSpellManagerComponent = 56u;
  const uint32_t kTableLoaded = refii::guest::Address(0x83496C19u);
  const uint32_t kTable = refii::guest::Address(0x833192A4u);

  for (uint32_t level = 0; level < 6u; ++level) {
    sampled.spell_damage[level] = 1.0f;
  }

  const uint32_t magic = HeroComponent(base, hero, kMagicComponent);
  const uint32_t owner = magic != 0u ? LoadGuestU32(base, magic + 4u) : 0u;
  const uint32_t manager =
      owner != 0u ? HeroComponent(base, owner, kSpellManagerComponent) : 0u;
  if (manager == 0u || LoadGuestU8(base, kTableLoaded) == 0u) {
    return;
  }

  const int32_t tier_raw = static_cast<int32_t>(LoadGuestU32(base, manager + 420u));
  if (tier_raw < 0) {
    return;
  }

  const uint32_t tier = tier_raw > 2 ? 2u : static_cast<uint32_t>(tier_raw);
  for (uint32_t level = 0; level < 6u; ++level) {
    const uint32_t raw = LoadGuestU32(base, kTable + 4u * (11u * tier + level));
    std::memcpy(&sampled.spell_damage[level], &raw, sizeof(float));
  }
}

/// The world map's region page, sampled by CALLING the guest's own eleven providers.
///
/// They all share one shape — `(LHString* out, LHString* regionName)` — and each resolves the
/// hero, component 250 and the region key itself, returning the FINISHED string (miles or hours
/// with the right plural tag, a "found / total" pair, a comma-joined merchant list). Several of
/// the underlying numbers are not readable at all: distance and travel time are pathfinding
/// accessors and the star rating averages live entity state.
///
/// Gated on the map being up, because these allocate (`sub_821F0240` takes a temp string ref per
/// call). One region per sample, the one the host asked for.
/// THE WORLD MAP'S `Family` LIST, through the guest's own collector.
///
/// `sub_8246F020(vector)` is what `sub_829A42C8` calls before it spawns a single row, and it is
/// the whole read: hero out of `dword_83496AB8`, component 136, the intrusive list at
/// `comp136+16`, a 64-bit entity UID per node resolved against the entity registry, and the name
/// off component 20 of the live entity (`sub_823A8148`: the LH string at `comp20+52` when it has
/// a length, else `TextManager` on the tag at `comp20+48`) — with the record's own stored name as
/// the fallback for a member who is not resident. Walking that ourselves would be four guessed
/// offsets and a red-black tree; calling it is one function and the guest's own answer.
///
/// The vector it fills is 16 bytes: `[1]` begin, `[2]` end, `[3]` capacity, records of 52 bytes.
/// `sub_829A42C8` reads `(end - begin) / 52` (`divw` against `li r10, 0x34` at 0x829A4304) and
/// starts at `begin + 8`, stepping 13 dwords — so the display name is the LH string handle at
/// `record+8`, and it is the only field the list uses.
///
/// IT ALLOCATES, so it is paired with `sub_826C9450` on every path and gated on the world map
/// being up. That destructor runs `sub_8246FBD8` over each record and frees the block, which is
/// exactly what the screen does at 0x829A4520.
/// One Fable "LH string" of 16-bit characters at a handle whose LAYOUT IS KNOWN.
///
/// `handle` holds a block pointer; the block is `{ const wchar_t* chars, int length }`. That is
/// the shape `sub_82B3F898` reads — `v4 = *a2; length = *(v4 + 4); chars = *(wchar_t**)*a2`,
/// then `*(_WORD *)&chars[i]` per character at 0x82B3F910.
///
/// Distinct from `ReadGuestWideLhString` below, and deliberately: that one PROBES, chasing up to
/// three hops and rejecting a hop whose first units are not Latin, because it is handed labels
/// whose indirection depth is not known in advance. Here the depth is stated by the function
/// that built the record, so guessing it would only add ways to be wrong — and the length is
/// read rather than scanned for a NUL. `ReadGuestLhString` is the ASCII reader for the same two
/// hops and would return the high bytes as text.
std::string ReadStatedWideLhString(uint8_t* base, uint32_t handle) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (base == nullptr || !plausible(handle)) {
    return std::string();
  }

  const uint32_t block = LoadGuestU32(base, handle);
  if (!plausible(block)) {
    return std::string();
  }

  const uint32_t chars = LoadGuestU32(base, block);
  const uint32_t length = LoadGuestU32(base, block + 4u);
  if (!plausible(chars) || length == 0u || length > 256u) {
    return std::string();
  }

  std::string value;
  value.reserve(length);
  for (uint32_t i = 0; i < length; ++i) {
    const uint16_t unit = LoadGuestU16(base, chars + (i * 2u));
    if (unit == 0) {
      break;
    }

    if (unit < 0x80u) {
      value.push_back(static_cast<char>(unit));
    } else if (unit < 0x800u) {
      value.push_back(static_cast<char>(0xC0u | (unit >> 6)));
      value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    } else {
      value.push_back(static_cast<char>(0xE0u | (unit >> 12)));
      value.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
      value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    }
  }

  return value;
}

void SampleFamily(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kWorldMapScreenId = 23u;

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id) || screen_id != kWorldMapScreenId) {
    return;
  }


  // Claimed by lowering r1, so the collector's own frames land below the vector it is handed.
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t vector = frame + 0x20u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, vector + (word * 4u), 0u);
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = vector;
  rex_lhWorldMap_BuildFamilyList(ctx, base);
  ctx = saved;

  const uint32_t begin = LoadGuestU32(base, vector + 4u);
  const uint32_t end = LoadGuestU32(base, vector + 8u);

  FamilyMember members[kMaxFamilyMembers];
  uint32_t count = 0;
  if (begin != 0u && end > begin && ((end - begin) % 52u) == 0u &&
      (end - begin) <= (52u * kMaxFamilyMembers)) {
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

    // `root+12 -> +88 -> +4 -> +8 -> +4` is the travel system; the region container hangs off
    // its `+12` and the current level's handle ADDRESS off `+36 -> +4`, then `+ 172` OUTSIDE
    // the last dereference. Same chain the job page walks — see SampleQuestTravel.
    uint32_t travel_system = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    for (const uint32_t hop : {12u, 88u, 4u, 8u, 4u}) {
      travel_system = plausible(travel_system) ? LoadGuestU32(base, travel_system + hop) : 0u;
    }

    const uint32_t manager =
        plausible(travel_system) ? LoadGuestU32(base, travel_system + 12u) : 0u;

    uint32_t current_level = travel_system;
    for (const uint32_t hop : {36u, 4u}) {
      current_level = plausible(current_level) ? LoadGuestU32(base, current_level + hop) : 0u;
    }
    current_level = plausible(current_level) ? current_level + 172u : 0u;

    for (uint32_t at = begin; at < end && count < kMaxFamilyMembers; at += 52u) {
      FamilyMember& member = members[count];

      // Wide, not ASCII: the guest hands `record+8` to `sub_82B3F898`, which reads `_WORD`s.
      // The page filler `sub_829B04E0` sets all ten of these with `sub_82425DD0`, the setter
      // that takes an ALREADY FORMATTED string — so they are read, never rebuilt.
      const auto field = [&](uint32_t offset, char* into, std::size_t cap) {
        std::snprintf(into, cap, "%s", ReadStatedWideLhString(base, at + offset).c_str());
      };

      field(8u, member.name, sizeof(member.name));
      field(12u, member.children, sizeof(member.children));
      field(20u, member.distance, sizeof(member.distance));
      field(24u, member.travel, sizeof(member.travel));
      field(28u, member.note, sizeof(member.note));
      field(32u, member.daily_upkeep, sizeof(member.daily_upkeep));
      field(36u, member.total_upkeep, sizeof(member.total_upkeep));
      field(40u, member.happiness, sizeof(member.happiness));
      field(44u, member.home_happiness, sizeof(member.home_happiness));
      field(48u, member.upkeep_happiness, sizeof(member.upkeep_happiness));

      // THE HOME/TRAVEL PROMPT. `sub_829B08A8` builds the member's fast-travel STOP name, then
      // runs `sub_82470A78`'s tree over it. That tree is the FAMILY one and is not the jobs
      // page's: it tests `sub_82470FE8` (is the trail already on this member) where
      // `sub_824708D8` tests `sub_82470F80`, and it takes the row id as its last argument.
      //
      // `sub_829B08A8` passes 0 there, which makes one whole branch dead — the guest asks
      // `id != 0 && id == 0`. That is shipped behaviour on this page, not an omission, so the
      // branch is left out rather than reproduced with a value the family page never supplies.
      //
      // The stop name comes from GAMEDATA first and the live region registry only when that is
      // empty, exactly as the filler orders them.
      {
        const uint32_t name = at + 16u;
        const uint32_t stop = frame + 0x40u;
        const uint32_t spare = frame + 0x50u;
        StoreGuestU32(base, stop, 0u);
        StoreGuestU32(base, spare, 0u);

        const auto call2 = [&](void (*fn)(PPCContext&, uint8_t*), uint32_t r3, uint32_t r4) {
          ctx = saved;
          ctx.r1.u32 = frame;
          ctx.r3.u32 = r3;
          ctx.r4.u32 = r4;
          fn(ctx, base);
          const uint32_t answer = ctx.r3.u32;
          ctx = saved;
          return answer;
        };

        call2(rex_lhFamily_GetFastTravelStopFromGameData, stop, name);

        const uint32_t built = LoadGuestU32(base, stop);
        if (built == 0u || LoadGuestU32(base, built + 4u) == 0u) {
          call2(rex_lhRegionManager_FindFastTravelStopForLevel, spare, name);
          call2(rex_lhCharString_Assign, stop, spare);
          call2(rex_lhCharString_Dtor, spare, 0u);
        }

        const bool travels = (call2(rex_lhWorldMap_CanFastTravelTo, name, stop) & 0xFFu) != 0u;
        const bool on_trail = (call2(rex_lhQuestTrail_IsTargetingEntity, at, 0u) & 0xFFu) != 0u;
        const bool no_destination = (call2(rex_lhQuestTrail_HasNoDestination, 0u, 0u) & 0xFFu) != 0u;

        // `sub_82B3DE48` is `operator!=` — it returns the NEGATION of the equality test. Read
        // as `==` this inverts both halves of the tree.
        const bool differs = plausible(current_level)
                                 ? (call2(rex_lhCharString_OperatorNotEqual, name, current_level) & 0xFFu) != 0u
                                 : false;

        call2(rex_lhCharString_Dtor, stop, 0u);

        const char* tag = nullptr;
        if (travels) {
          tag = (on_trail || (!no_destination && differs))
                    ? "GUI_SCREEN_WORLD_MAP_MISC_TRAVEL"
                    : "GUI_SCREEN_WORLD_MAP_MISC_TRAIL_AND_TRAVEL";
        } else if (!on_trail && !(!no_destination && differs)) {
          tag = "GUI_SCREEN_WORLD_MAP_MISC_TRAIL";
        }

        if (tag != nullptr) {
          std::snprintf(member.prompt, sizeof(member.prompt), "%s", tag);
        }
      }

      // The region is the only field the record does not carry: `sub_829B04E0` asks which
      // region owns the member's level at `record+16`, and sets the element BY TAG.
      if (plausible(manager)) {
        ctx = saved;
        ctx.r1.u32 = frame;
        ctx.r3.u32 = manager;
        ctx.r4.u32 = at + 16u;
        rex_lhRegionManager_FindRegionOwningLevel(ctx, base);
        const uint32_t region = ctx.r3.u32;
        ctx = saved;

        std::snprintf(member.region, sizeof(member.region), "%s",
                      ReadGuestLhString(base, region).c_str());
      }

      ++count;
    }
  }

  PublishFamily(members, count);

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = vector;
  rex_lhFamilyMemberVector_Destroy(ctx, base);
  ctx = saved;
}

void SampleWorldMapRegion(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kWorldMapScreenId = 23u;
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id) || screen_id != kWorldMapScreenId) {
    return;
  }

  // party -> region manager -> the level list, exactly the chain `sub_823C9000` walks.
  //
  // THE FIRST DEREFERENCE IS THE GLOBAL'S OWN. `dword_83496AB8` HOLDS the app pointer; the
  // guest's own prologue is `lwz r11, dword_83496AB8 ; lwz r11, 0xC(r11)`, so the chain starts
  // at `*(0x83496AB8)` and only then takes `+12`. This read `*(0x83496AB8 + 12)` — the dword at
  // 0x83496AC4, a different global entirely — and every offset after it was measured from
  // whatever that happened to be. `RefiiHostGetFastTravelRegions` walks the same container the
  // correct way and returns seven named regions, which is what this has to agree with: the host
  // asks for a region BY THE ORDINAL that export hands out, so the two walks must be one walk.

  // WHY A SILENT BAIL IS BANNED HERE: the host read `count 0 / known false` for a whole
  // session and could not say WHICH of the seven reads below was refusing — every one of them
  // returns identically. One line per distinct failure, once each per run.
  static std::atomic<uint32_t> bail_said{0};
  const auto say_bail = [&](uint32_t which, uint32_t at, uint32_t value) {
    const uint32_t bit = 1u << which;
    if ((bail_said.fetch_or(bit) & bit) == 0u) {
      REXLOG_INFO("Refii world-map sample: bail {} at 0x{:08X} value 0x{:08X}", which, at,
                  value);
    }
  };

  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t regions = plausible(party) ? LoadGuestU32(base, party + 8u) : 0u;
  const uint32_t inner = plausible(regions) ? LoadGuestU32(base, regions + 4u) : 0u;
  const uint32_t list = plausible(inner) ? LoadGuestU32(base, inner + 12u) : 0u;
  if (!plausible(list)) {
    if (!plausible(root)) say_bail(0, kGameRoot, root);
    else if (!plausible(game)) say_bail(1, root + 12u, game);
    else if (!plausible(holder)) say_bail(2, game + 88u, holder);
    else if (!plausible(party)) say_bail(3, holder + 4u, party);
    else if (!plausible(regions)) say_bail(4, party + 8u, regions);
    else if (!plausible(inner)) say_bail(5, regions + 4u, inner);
    else say_bail(6, inner + 12u, list);
    return;
  }

  // `sub_82518330` walks [list+8, list+12) with stride 8 keeping NON-NULL first dwords; walked
  // here instead so no vector is allocated. Non-null, not plausible — an entry that is neither
  // still consumes an output index in the guest's walk, and this counter is compared against an
  // ordinal produced by that same rule.
  const uint32_t begin = LoadGuestU32(base, list + 8u);
  const uint32_t end = LoadGuestU32(base, list + 12u);
  if (!plausible(begin) || end <= begin || (end - begin) > 0x4000u ||
      ((end - begin) % 8u) != 0u) {
    say_bail(7, list + 8u, begin);
    say_bail(8, list + 12u, end);
    return;
  }

  WorldMapRegion region;
  uint32_t wanted = RequestedWorldMapRegion();
  uint32_t seen = 0;
  uint32_t name_handle = 0;
  for (uint32_t at = begin; at < end; at += 8u) {
    if (LoadGuestU32(base, at) == 0u) {
      continue;
    }

    if (seen == wanted) {
      // THE SLOT IS TWO DEREFERENCES FROM THE CHARACTERS, and this captured the slot's own
      // address — every read downstream was one level short, so the sample published with an
      // empty name while the walk itself was fine. `*slot` is the region object; its word 0
      // is the name handle, so `**slot` is the characters pointer — which is how
      // `sub_82519DF8` matches a region, `strcmpi(**v5, **a2)`. The providers do not take
      // this string at all; see the level lookup below.
      name_handle = LoadGuestU32(base, at);
    }

    ++seen;
  }

  region.count = seen;
  if (name_handle == 0u) {
    PublishWorldMapRegion(region);
    return;
  }

  region.known = true;
  region.index = wanted;
  std::snprintf(region.name, sizeof(region.name), "%s",
                ReadGuestLhString(base, name_handle).c_str());

  // THE PROVIDERS DO NOT TAKE THE REGION. They take the region's FIRST LEVEL, as an
  // `LHString*` — the address of a one-word handle, not the handle's value.
  //
  // `sub_829AB0E0` is the shipped region-page filler and it says so in two steps:
  //
  //     if ( v13 && ((*(v13 + 20) - *(v13 + 16)) >> 3) )      // the level vector, stride 8
  //     {
  //       sub_821F0240(&v65, **(_DWORD **)(v13 + 16));        // v65 = level[0]'s name
  //       ...
  //       sub_823C9000(&v67, &v65);                           // every provider takes &v65
  //
  // `sub_821F0240(dst, src)` is `*dst = *src` plus a refcount bump at `*src + 12`, so `src`
  // is a HANDLE ADDRESS; the value it is handed here, `*(*(v13 + 16))`, is therefore the
  // level object, whose word 0 is its name handle. Passing the object pointer straight
  // through is the same read without owning a reference.
  //
  // Two bugs died here at once. The argument was `*name_handle` — the region's own INNER,
  // one level too deep, so `*a2` came back as a characters pointer and `**a2` as the first
  // four bytes of text reinterpreted as an address; and it was the region's name at all,
  // when `sub_823C9000` feeds its argument to `World_ComputeWorldLevelFnv1Key` and wants a
  // LEVEL. Eleven fields read empty for it.
  const uint32_t levels = LoadGuestU32(base, name_handle + 16u);
  const uint32_t levels_end = LoadGuestU32(base, name_handle + 20u);
  if (!plausible(levels) || levels_end <= levels) {
    say_bail(9, name_handle + 16u, levels);
    PublishWorldMapRegion(region);
    return;
  }

  const uint32_t level = LoadGuestU32(base, levels);
  if (!plausible(level)) {
    say_bail(10, levels, level);
    PublishWorldMapRegion(region);
    return;
  }

  // Claimed by lowering r1 so a callee's frame cannot land on the string it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t out = frame + 0x20u;
  const PPCContext saved = ctx;

  const auto provider = [&](void (*call)(PPCContext&, uint8_t*), char* into, std::size_t cap,
                           uint32_t r5 = 0u) {
    StoreGuestU32(base, out, 0u);

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    ctx.r4.u32 = level;
    ctx.r5.u32 = r5;
    call(ctx, base);

    // WIDE, not ASCII. `out` holds the inner and the characters hang off inner+0 — that part
    // was right — but this is the guest's DISPLAY-TEXT class, UTF-16 big-endian, and the ASCII
    // reader stops on the first character's zero high byte. Eleven fields read empty for that
    // while the region NAME beside them read fine, because the name really is narrow: it is
    // what `sub_82519DF8` hands to `strcmpi`. The filler agrees — it releases these outputs
    // with `LHString_CCharString_CCharString_0` and its narrow paths with `sub_82214DD8`.
    //
    // `sub_823C90F8` proves the output is never empty when the call succeeds: below 1.01 miles
    // it is Babel's `GUI_SHARED_1_MILE`, otherwise `GUI_SHARED_X_MILES` formatted with the
    // number. An empty read was a read, not an answer.
    std::string value = ReadGuestWideString(base, out);

    // One raw look per run: the two words at `out`, so the convention question never has to
    // be re-argued from a blank page again.
    static std::atomic<bool> out_said{false};
    if (!out_said.exchange(true)) {
      REXLOG_INFO("Refii world-map provider out: [0]=0x{:08X} [1]=0x{:08X} read='{}'",
                  LoadGuestU32(base, out), LoadGuestU32(base, out + 4u), value);
    }

    std::snprintf(into, cap, "%s", value.c_str());

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    rex_lhWideString_Dtor(ctx, base);
    ctx = saved;
  };

  provider(rex_lhRegionStats_FormatDistanceText, region.distance, sizeof(region.distance));
  // `sub_823C8BE0(&v67, &v65, 1)` in the filler — the third argument is 1, not 0.
  provider(rex_lhRegionStats_FormatTravelTimeText, region.travel, sizeof(region.travel), 1u);
  provider(rex_lhRegionStats_FormatFineText, region.fine, sizeof(region.fine));
  provider(rex_lhRegionStats_FormatPropertiesOwnedText, region.owned, sizeof(region.owned));
  provider(rex_lhRegionStats_FormatChestsText, region.chests, sizeof(region.chests));
  provider(rex_lhRegionStats_FormatDigSpotsText, region.digspots, sizeof(region.digspots));
  provider(rex_lhRegionStats_FormatDiveSpotsText, region.divespots, sizeof(region.divespots));
  provider(rex_lhRegionStats_FormatGargoylesText, region.gargoyles, sizeof(region.gargoyles));
  provider(rex_lhRegionStats_FormatSilverKeysText, region.silver_keys, sizeof(region.silver_keys));
  provider(rex_lhRegionStats_FormatMerchantsText, region.merchants, sizeof(region.merchants));

  // The economy is a STAR COUNT, not a string, so it cannot go through `provider`:
  // `sub_8246FE20` takes the same level `LHString*` in r3 and RETURNS the rating there (`bl` at
  // 0x829AB364, then `addi r5, r3, 1` to make it a slide index). It must be called — there is
  // no per-region rating field; the number is averaged over the region's villages on every ask.
  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = level;
  rex_lhRegionStats_GetEconomyRating(ctx, base);
  region.stars = static_cast<int32_t>(ctx.r3.u32);
  ctx = saved;

  // THE MINIMAP PAGE'S KEYS, taken the way `sub_829ABFC0` takes them: the region's first level
  // name, its world/level key through the guest's own hasher, and the scenario the world names
  // for that key (`World+88` table; the lookup returns the address of the pair's string handle,
  // or an empty global when there is none).
  //
  // BOTH STRING ARGUMENTS ARE LHString HANDLES, NOT CHARACTERS. `sub_8231CA18(world, a2, a3)`
  // copies `a2` as a handle (`sub_821E3108` -> `sub_822651A0(a1, a2)`) and appends `a3` the
  // same way, then releases both. Handing it the bytes "Albion" made it treat 'Albi' as a
  // string object and free it, which took the game down from inside the heap's spin lock the
  // first time the map opened. So "Albion" is built as a guest string on the frame with the
  // guest's own constructor and released after, and the level goes as the providers take it:
  // `level` IS the handle pointer the guest's `&regionName` names.
  std::snprintf(region.level_name, sizeof(region.level_name), "%s",
                ReadGuestLhString(base, level).c_str());
  {
    const uint32_t world_text = frame + 0x100u;
    const uint32_t world_handle = frame + 0x110u;
    static const char kWorld[] = "Albion";
    for (uint32_t i = 0; i < sizeof(kWorld); ++i) {
      base[world_text + i] = static_cast<uint8_t>(kWorld[i]);
    }
    StoreGuestU32(base, world_handle, 0u);

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = world_handle;
    ctx.r4.u32 = world_text;
    ctx.r5.u32 = 0xFFFFFFFFu;  // NUL-terminated, as the event constructor is handed it
    rex_lhCharString_CtorFromCStr(ctx, base);

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = inner;
    ctx.r4.u32 = world_handle;
    ctx.r5.u32 = level;
    rex_lhWorld_ComputeWorldLevelFnv1Key(ctx, base);
    region.level_key = ctx.r3.u32;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = world_handle;
    rex_lhCharString_Dtor(ctx, base);

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = inner;
    ctx.r4.u32 = region.level_key;
    rex_lhWorld_LookupScenarioNameByLevelKey(ctx, base);
    const uint32_t scenario_slot = ctx.r3.u32;
    const uint32_t scenario_handle =
        plausible(scenario_slot) ? LoadGuestU32(base, scenario_slot) : 0u;
    std::snprintf(region.scenario, sizeof(region.scenario), "%s",
                  plausible(scenario_handle) ? ReadGuestLhString(base, scenario_handle).c_str()
                                             : "");
    ctx = saved;
  }

  PublishWorldMapRegion(region);
}

/// ONE JOB'S DISTANCE AND TRAVEL TIME — the world map's job page.
///
/// The two providers are the region page's own, and they take an `LHString*`. The quest's
/// `ObjectiveLevel` is INLINE on the quest object at `+132`, so `quest+132` is already the
/// handle address — no extra hop, unlike the region walk where the level had to be found under
/// the region record.
///
/// `sub_823C8BE0`'s third argument is **0** here where the region page passes 1
/// (`sub_829B02A0` at 0x829b0310). With 0 and a whole-hour count of zero, `sub_823C8CE0`
/// appends nothing at all — so a job less than an hour away shows an empty travel time. That is
/// the shipped behaviour and is kept rather than "corrected" into "0 hours".
void SampleQuestTravel(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kWorldMapScreenId = 23u;
  constexpr uint32_t kQuestLogComponent = 102u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id) || screen_id != kWorldMapScreenId) {
    return;
  }

  const uint32_t wanted = RequestedQuestTravel();
  QuestTravel journey;
  journey.record = wanted;
  if (wanted == 0u) {
    PublishQuestTravel(journey);
    return;
  }

  // A refused ask names its gate, once per requested record — a blank journey block on the
  // jobs page cannot otherwise say which premise failed.
  static uint32_t reported = 0u;
  const auto refuse = [&](const char* gate, uint32_t detail) {
    if (reported != wanted) {
      reported = wanted;
      REXLOG_INFO("Refii guest-tick pump: quest travel 0x{:08X} refused at {} (0x{:08X})",
                  wanted, gate, detail);
    }
    PublishQuestTravel(journey);
  };

  const uint32_t hero = HeroEntity(base);
  const uint32_t log = HeroComponent(base, hero, kQuestLogComponent);
  const uint32_t begin = plausible(log) ? LoadGuestU32(base, log + 0x10u) : 0u;
  const uint32_t end = plausible(log) ? LoadGuestU32(base, log + 0x14u) : 0u;
  if (!plausible(begin) || end <= begin || ((end - begin) % 12u) != 0u ||
      (end - begin) > 0x2000u) {
    refuse(hero == 0u ? "hero" : log == 0u ? "component 102" : "log vector", log);
    return;
  }

  uint32_t quest = 0;
  for (uint32_t at = begin; at < end; at += 12u) {
    const uint32_t candidate = LoadGuestU32(base, at + 4u);
    if (plausible(candidate) && LoadGuestU32(base, candidate + 4u) == wanted) {
      quest = candidate;
      break;
    }
  }

  if (quest == 0u) {
    refuse("record match", (end - begin) / 12u);
    return;
  }

  // The level handle is inline; an unset one is an empty string, not a failure.
  const uint32_t level = quest + 132u;
  if (LoadGuestU32(base, level) == 0u) {
    refuse("objective level", quest);
    return;
  }

  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t out = frame + 0x20u;
  const PPCContext saved = ctx;

  const auto provider = [&](void (*call)(PPCContext&, uint8_t*), char* into, std::size_t cap,
                            uint32_t r5) {
    StoreGuestU32(base, out, 0u);
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    ctx.r4.u32 = level;
    ctx.r5.u32 = r5;
    call(ctx, base);

    // WIDE, like every display string the guest builds — see ReadGuestWideString.
    std::snprintf(into, cap, "%s", ReadGuestWideString(base, out).c_str());

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    rex_lhWideString_Dtor(ctx, base);
    ctx = saved;
  };

  provider(rex_lhRegionStats_FormatDistanceText, journey.distance, sizeof(journey.distance), 0u);
  provider(rex_lhRegionStats_FormatTravelTimeText, journey.travel, sizeof(journey.travel), 0u);

  // "Time Remaining", WORDED BY THE GUEST. This one takes a FLOAT in f1 rather than a string
  // pointer in r4, so it cannot go through `provider` — and it must not be reproduced here:
  // `GUI_SHARED_X_DAYS` is a printf format string that `sub_82B3F250` fills, so a host-side
  // `%1` substitution finds no token and leaves the format itself on screen. Below 1.01 days
  // the guest answers `GUI_SHARED_1_DAY` instead, and it makes that choice too.
  {
    const uint32_t bits = LoadGuestU32(base, quest + 44u);
    float remaining = 0.0f;
    std::memcpy(&remaining, &bits, sizeof(remaining));

    StoreGuestU32(base, out, 0u);
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    ctx.f1.f64 = static_cast<double>(remaining);
    rex_lhGuiText_FormatDaysRemaining(ctx, base);
    std::snprintf(journey.days, sizeof(journey.days), "%s",
                  ReadGuestWideString(base, out).c_str());

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    rex_lhWideString_Dtor(ctx, base);
    ctx = saved;
  }

  // THE REGION IS A LOOKUP, NOT A FIELD. The job page's own filler `sub_829AFE58` asks the
  // region container which region owns the objective level:
  //
  //     v23 = sub_82518490(*(*(*(*(*(*(dword_83496AB8 + 12) + 88) + 4) + 8) + 4) + 12),
  //                        v10 + 132);
  //     sub_821F0240(&v35, v23);      // *dst = *src -> v23 is the region OBJECT
  //     sub_82425F40(v22, &v35);      // set-by-TAG, so the tag is what crosses
  //
  // `sub_82518490` walks the container in pairs of 8 (`v3 += 2`) testing each entry with
  // `sub_82516E48(entry, levelName)` and returns the matching region, or the empty-string
  // singleton `dword_834A5E78` when nothing owns the level. The tag is the region object's
  // word 0 — the same shape `ReadGuestLhString` already reads for the region page's name,
  // and the same BORROW: the filler takes its own reference with `sub_821F0240` and releases
  // that; reading through without one owes nothing.
  //
  // `root+12 -> +88 -> +4` is the prefix the sales list and the game clock already walk, so
  // only the last three hops are new.
  uint32_t manager = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  for (const uint32_t hop : {12u, 88u, 4u, 8u, 4u, 12u}) {
    manager = plausible(manager) ? LoadGuestU32(base, manager + hop) : 0u;
  }

  if (plausible(manager)) {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = manager;
    ctx.r4.u32 = level;
    rex_lhRegionManager_FindRegionOwningLevel(ctx, base);

    const uint32_t region = ctx.r3.u32;
    ctx = saved;

    // NARROW. The filler releases it with `sub_82214DD8`, not the wide destructor.
    std::snprintf(journey.region, sizeof(journey.region), "%s",
                  ReadGuestLhString(base, region).c_str());
  }

  // THE A-BUTTON PROMPT IS A DECISION, NOT A STRING. `sub_824708D8` picks between three tags
  // and a hidden button, and every input is guest travel state — whether a fast-travel marker
  // resolves, whether a trail is already running, whether the world has a trail destination at
  // all. Those are walks over the region and trail managers; reproducing them here would be
  // emulation. The pump calls the guest's own predicates and applies the guest's own tree:
  //
  //     if ( sub_824707E8(quest+132, quest+144) )                       // travel is offered
  //       (!Primary || sub_82470F80()) && (sub_82471438() || !differs)  -> TRAIL_AND_TRAVEL
  //                                                           else     -> TRAVEL
  //     else
  //       Primary && !sub_82470F80()                                    -> hidden
  //       !sub_82471438() && differs                                    -> hidden
  //                                                           else     -> TRAIL
  //
  // `differs` compares `quest+132` against the current level at `E+36 -> +4 -> +172`, off the
  // same object the region container hangs from.
  //
  // IT IS `operator!=`, NOT `operator==`. `sub_82B3DE48` computes equality and then RETURNS ITS
  // NEGATION — `clrlwi/cntlzw/extrwi` yields bit 5 of the leading-zero count, which is 1 exactly
  // when the equality test failed, and IDA names the symbol
  // `LHString::CCharString::operator!=`. Reading it as `==` inverts both branches below.
  const auto call_bool = [&](void (*fn)(PPCContext&, uint8_t*), uint32_t r3, uint32_t r4) {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = r3;
    ctx.r4.u32 = r4;
    fn(ctx, base);
    const bool answer = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;
    return answer;
  };

  uint32_t travel_system = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  for (const uint32_t hop : {12u, 88u, 4u, 8u, 4u}) {
    travel_system = plausible(travel_system) ? LoadGuestU32(base, travel_system + hop) : 0u;
  }

  uint32_t current_level = travel_system;
  for (const uint32_t hop : {36u, 4u}) {
    current_level = plausible(current_level) ? LoadGuestU32(base, current_level + hop) : 0u;
  }
  current_level = plausible(current_level) ? current_level + 172u : 0u;

  if (plausible(current_level)) {
    const bool travels = call_bool(rex_lhWorldMap_CanFastTravelTo, level, quest + 144u);
    const bool primary = LoadGuestU8(base, quest + 20u) != 0u;
    const bool trailing = call_bool(rex_lhQuestTrail_IsTrailActive, 0u, 0u);
    const bool no_destination = call_bool(rex_lhQuestTrail_HasNoDestination, 0u, 0u);
    // BOTH ARGUMENTS ARE HANDLE ADDRESSES. In `sub_824708D8` the `+ 172` sits OUTSIDE the
    // last dereference, so the comparison takes `G + 172` itself — the same shape as
    // `quest + 132` beside it. `sub_824707E8` reads the value there and compares it against
    // `*(quest + 132)`, which is the same test one level down.
    //
    // The result is "these are DIFFERENT", not "these are the same" — see above.
    const bool differs = call_bool(rex_lhCharString_OperatorNotEqual, level, current_level);

    const char* tag = nullptr;
    if (travels) {
      tag = (!primary || trailing) && (no_destination || !differs)
                ? "GUI_SCREEN_WORLD_MAP_MISC_TRAIL_AND_TRAVEL"
                : "GUI_SCREEN_WORLD_MAP_MISC_TRAVEL";
    } else if (!(primary && !trailing) && !(!no_destination && differs)) {
      tag = "GUI_SCREEN_WORLD_MAP_MISC_TRAIL";
    }

    if (tag != nullptr) {
      std::snprintf(journey.prompt, sizeof(journey.prompt), "%s", tag);
    }
  }

  journey.known = true;
  if (reported != wanted) {
    reported = wanted;
    REXLOG_INFO("Refii guest-tick pump: quest travel 0x{:08X} answered — distance '{}', days "
                "'{}', region '{}'", wanted, journey.distance, journey.days, journey.region);
  }
  PublishQuestTravel(journey);
}

/// ONE SHOP ROW'S ASKING PRICE — screen 22.
///
/// The item book shows an item's WORTH; a shop shows what it charges, and the two are different
/// numbers. `sub_82992F90` is the guest's own price display and states the whole contract:
///
///     screen 22 -> **(screen + 404)      the shopkeeper ENTITY, two dereferences
///     entity + 144 & 0x40                live
///     component 27                       CECShopkeeper, looked up every time
///     sell ? sub_828727B8 : sub_82872910 the price
///
/// Both weight a base price by the shopkeeper's `CECOpinionReaction` axes and the shop's
/// wealth (buy multiplies where sell divides), so there is no ratio to reproduce here — the
/// number is asked for.
///
/// The row is named by TICKET, resolved exactly as DispatchShopAction resolves it. A stale
/// ticket is the ordinary case for a list repopulated since the row was drawn, and answers
/// nothing rather than a wrong price.
void SampleShopPrice(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kShopScreenId = 22u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t owner_screen = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner_screen, &screen_id) || screen_id != kShopScreenId) {
    return;
  }

  bool sell = false;
  const uint32_t ticket = RequestedShopPrice(&sell);

  ShopPrice answer;
  answer.ticket = ticket;
  if (ticket == 0u) {
    PublishShopPrice(answer);
    return;
  }

  const uint32_t screen = ScreenObjectRO(base, kShopScreenId);
  const uint32_t holder = plausible(screen) ? LoadGuestU32(base, screen + 404u) : 0u;
  const uint32_t shopkeeper = plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  if (!plausible(shopkeeper) || (LoadGuestU8(base, shopkeeper + 144u) & 0x40u) == 0u) {
    PublishShopPrice(answer);
    return;
  }

  const uint32_t shop_component = HeroComponent(base, shopkeeper, 27u);
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t owner = plausible(root) ? LoadGuestU32(base, root + 28u) : 0u;
  const uint32_t registry = plausible(owner) ? LoadGuestU32(base, owner + 60u) : 0u;
  if (shop_component == 0u || !plausible(registry)) {
    PublishShopPrice(answer);
    return;
  }

  PPCFunc* lookup = FindGuestFunction(refii::guest::Address(0x82880BC0u));
  PPCFunc* price = FindGuestFunction(sell ? refii::guest::Address(0x828727B8u) : refii::guest::Address(0x82872910u));
  if (lookup == nullptr || price == nullptr) {
    PublishShopPrice(answer);
    return;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t item = frame + 0x20u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, item + (word * 4u), 0u);
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = registry + 4u;
  ctx.r4.u32 = ticket;
  ctx.r5.u32 = item;
  lookup(ctx, base);
  const bool found = (ctx.r3.u32 & 0xFFu) != 0u;
  ctx = saved;

  const uint32_t object = found ? LoadGuestU32(base, item) : 0u;
  if (!plausible(object)) {
    PublishShopPrice(answer);
    return;
  }

  // The item's record, as the price itself reads it: `sub_8296B310` resolves
  // `sub_821B24F8(*holder + 4)` — a lazy `{hash, cache}` ref whose first word is the hash.
  answer.record = LoadGuestU32(base, object + 4u);

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = shop_component;
  ctx.r4.u32 = item;
  price(ctx, base);
  answer.price = static_cast<int32_t>(ctx.r3.u32);
  ctx = saved;

  answer.known = true;
  PublishShopPrice(answer);
}

/// SCREEN 28'S ONE BUILDING — the real estate page's data, called rather than reproduced.
///
/// The screen's left column is a list of COMMANDS, not of properties: one building, resolved
/// when the screen opens, no selection-change event. So this samples a single record while the
/// screen stands and the host draws both pages off it.
///
/// The chain is the shipped filler's own (`sub_82998FB0`, verified instruction by instruction
/// rather than taken on report):
///
///     lwz r10, 0x34(r11) ; extrwi r9, r10, 1,17   -> hero-object component 142
///     bl  sub_8229EE78                            -> the building ENTITY
///     lwz r11, 0x2C(r29) ; extrwi r10, r11, 1,28  -> its component 67, the CECBuilding
///
/// (`entity+0x34` with bit 14 is id 128+14 = 142; `BE+0x2C` with bit 3 is 64+3 = 67 — the same
/// presence bitmap `HeroComponent` already implements.) Neither filler reads `screen+164` or
/// `screen+168`: both re-derive from globals every call, and the ctor only zeroes those fields.
///
/// EVERY NUMBER IS THE GUEST'S ANSWER. Value, economy, income, the sale floor, the purchase
/// price and the three not-owned deltas each walk something we have no business reproducing —
/// a prosperity term off a world component, a GDB adjustment array matched against the
/// building-type mask, an occupant-opinion term weighted over five personality axes. The host
/// gets the numbers and owns the presentation.
void SampleRealEstate(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kRealEstateScreenId = 28u;
  constexpr uint32_t kPropertiesComponent = 69u;   // the hero's ownership list
  constexpr uint32_t kSiteComponent = 77u;         // the "what am I standing at" component
  constexpr uint32_t kBuildingHandleComponent = 142u;
  constexpr uint32_t kBuildingComponent = 67u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t owner_screen = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner_screen, &screen_id) ||
      screen_id != kRealEstateScreenId) {
    return;
  }

  RealEstate estate;

  const uint32_t hero = HeroEntity(base);
  const uint32_t site = HeroComponent(base, hero, kSiteComponent);
  if (!plausible(site)) {
    PublishRealEstate(estate);
    return;
  }

  // Claimed low so a callee's frame cannot land on an out-parameter it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t out = frame + 0x20u;
  const PPCContext saved = ctx;

  const auto call = [&](PPCFunc* fn, uint32_t r3, uint32_t r4) -> uint32_t {
    if (fn == nullptr) {
      return 0u;
    }
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = r3;
    ctx.r4.u32 = r4;
    fn(ctx, base);
    const uint32_t result = ctx.r3.u32;
    ctx = saved;
    return result;
  };

  // `comp77->vtbl[0x44]()` — the site's own "which entity is this" accessor. Indirect because
  // the concrete class is chosen at runtime; the slot is not.
  const uint32_t site_vtable = LoadGuestU32(base, site);
  const uint32_t accessor = plausible(site_vtable) ? LoadGuestU32(base, site_vtable + 0x44u) : 0u;
  const uint32_t entity = call(FindGuestFunction(accessor), site, 0u);
  if (!plausible(entity) || (LoadGuestU8(base, entity + 0x90u) & 0x40u) == 0u) {
    PublishRealEstate(estate);
    return;
  }

  const uint32_t handle = HeroComponent(base, entity, kBuildingHandleComponent);
  const uint32_t building_entity = call(FindGuestFunction(refii::guest::Address(0x8229EE78u)), handle, 0u);
  const uint32_t building = HeroComponent(base, building_entity, kBuildingComponent);
  if (!plausible(building)) {
    PublishRealEstate(estate);
    return;
  }

  const uint32_t properties = HeroComponent(base, hero, kPropertiesComponent);

  estate.known = true;
  estate.type_mask = LoadGuestU32(base, building + 28u);

  // Both walk the ownership list at `properties+28` comparing the 64-bit entity id at
  // `BE+20`/`BE+24`, taking and releasing a reference per node — called, not reproduced,
  // because reproducing it would mean touching those refcounts by hand.
  estate.owned = call(FindGuestFunction(refii::guest::Address(0x828D14E8u)), properties, building_entity) != 0u;
  estate.has_tenant = call(FindGuestFunction(refii::guest::Address(0x828D2800u)), properties, building_entity) != 0u;

  estate.value = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282AEB0u)), building, 0u));
  estate.economy = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C1C0u)), building, 0u));
  estate.income = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C110u)), building, 0u));

  const int32_t floor_price =
      static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282BCA0u)), building, 0u));
  estate.sale_price = estate.owned
      ? floor_price
      : static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282BA48u)), building, 0u));
  estate.selling_fees = floor_price - estate.value;

  estate.not_vacant = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C470u)), building, 0u));
  estate.owner_killed = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C870u)), building, 0u));
  estate.hero_opinion = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C610u)), building, 0u));

  // DECORATION HAS NO FUNCTION — the filler computes it inline at 0x82999804 as the furniture
  // value scaled by the building's decoration multiplier, so this is the one number the host
  // side has to do itself.
  {
    const int32_t furniture = static_cast<int32_t>(LoadGuestU32(base, building + 44u));
    const uint32_t bits = LoadGuestU32(base, building + 116u);
    float multiplier = 0.0f;
    std::memcpy(&multiplier, &bits, sizeof(multiplier));
    estate.decoration = static_cast<int32_t>(static_cast<float>(furniture) * multiplier);
  }

  // The GDB record the page's text hangs off. `sub_821DA3B0` fills a `{hash, cachedPtr}` pair;
  // only the hash crosses, and the host resolves BuildingName, BuildingAddress,
  // BuildingAnecdotes, BasicSalePrice and CanSellOncePurchased from its own GDB.
  StoreGuestU32(base, out, 0u);
  StoreGuestU32(base, out + 4u, 0u);
  call(FindGuestFunction(refii::guest::Address(0x821DA3B0u)), out, building);
  estate.record = LoadGuestU32(base, out);

  // The bonuses hang off a DIFFERENT record. `sub_82917F60` runs the same `sub_821DA3B0`
  // indirection over component 184 and reads its `Bonuses` array, so the building's own record
  // does not carry them; both hashes have to cross.
  {
    constexpr uint32_t kBonusComponent = 184u;
    const uint32_t bonuses = HeroComponent(base, building_entity, kBonusComponent);
    if (plausible(bonuses)) {
      StoreGuestU32(base, out, 0u);
      StoreGuestU32(base, out + 4u, 0u);
      call(FindGuestFunction(refii::guest::Address(0x821DA3B0u)), out, bonuses);
      estate.bonuses_record = LoadGuestU32(base, out);
    }
  }

  // WHOEVER IS IN IT, on the guest's own three branches (`sub_82998FB0`, 0x8299959c on):
  //   hero owns it, rented out  -> the TENANT, `sub_8282D5D8`, else GUI_..._TENANT_OTHER;
  //   hero owns it, not rented  -> the hero's TITLE (below), never a creature name;
  //   hero does not own it      -> the owner, `sub_8282D410`, else GUI_..._OWNER_OTHER.
  // `sub_8282D410` answers the HERO for a hero-owned building, and `sub_823C8510` cannot name
  // the hero (it carries no component 20). `sub_823C8510` takes the entity in r4 — a lost
  // argument in the decompile, explicit in the filler's own `mr r4, r3` — and answers a WIDE
  // string.
  {
    const uint32_t occupant = estate.has_tenant
        ? call(FindGuestFunction(refii::guest::Address(0x8282D5D8u)), building, 0u)
        : estate.owned ? 0u
                       : call(FindGuestFunction(refii::guest::Address(0x8282D410u)), building, 0u);
    if (plausible(occupant)) {
      StoreGuestU32(base, out, 0u);
      call(FindGuestFunction(refii::guest::Address(0x823C8510u)), out, occupant);
      std::snprintf(estate.occupant, sizeof(estate.occupant), "%s",
                    ReadGuestWideString(base, out).c_str());
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = out;
      rex_lhWideString_Dtor(ctx, base);
      ctx = saved;
    }
  }

  // The hero's current title record — `*(comp57+92)`, the hash half of the lazy GDB ref
  // `sub_824D8A10` resolves; the host walks `HeroTitleComponent.TextTag` from its own GDB.
  {
    constexpr uint32_t kTitleComponent = 57u;
    const uint32_t titles = HeroComponent(base, hero, kTitleComponent);
    if (plausible(titles)) {
      estate.title_record = LoadGuestU32(base, titles + 92u);
    }
  }

  PublishRealEstate(estate);
}

/// The completed-quest list the logbook's page indexes into.
///
/// The guest rebuilds this every time (`sub_82470220`): it copies the quest-log component's
/// entries, sorts them DESCENDING by the GDB property `DisplayPriority`, then applies three
/// byte filters. The filters run here and the sort is left to the host, since filtering
/// preserves relative order.
///
/// The three filters are bytes on the quest object: `sub_82469330` tests `quest+12`,
/// `sub_82469428` tests `quest+21`, `sub_82469628` tests `quest+77`, each against 1. The source
/// list is resident — only the guest's OUTPUT copy is transient — so this walk allocates nothing.
void SampleQuests(uint8_t* base, uint32_t hero) {
  constexpr uint32_t kQuestLogComponent = 102u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t log = HeroComponent(base, hero, kQuestLogComponent);
  if (log == 0u) {
    PublishQuests(nullptr, 0u);
    return;
  }

  const uint32_t begin = LoadGuestU32(base, log + 0x10u);
  const uint32_t end = LoadGuestU32(base, log + 0x14u);
  if (!plausible(begin) || end <= begin || (end - begin) > 0x8000u) {
    PublishQuests(nullptr, 0u);
    return;
  }

  static QuestEntry entries[kMaxCompletedQuests];
  uint32_t count = 0;

  // Stride 12, and the quest pointer is at `+4` of each inline record.
  for (uint32_t at = begin; at < end && count < kMaxCompletedQuests; at += 12u) {
    const uint32_t quest = LoadGuestU32(base, at + 4u);
    if (!plausible(quest)) {
      continue;
    }

    if (LoadGuestU8(base, quest + 12u) != 1u || LoadGuestU8(base, quest + 21u) != 1u ||
        LoadGuestU8(base, quest + 77u) != 1u) {
      continue;
    }

    QuestEntry& entry = entries[count];
    entry = QuestEntry{};

    // `quest+4` is the GDB def handle `{hash, cachedPtr}` — the NAME and DESCRIPTION tags and
    // `DisplayPriority` all hang off the record, never off the quest object.
    entry.record = LoadGuestU32(base, quest + 4u);
    entry.money = static_cast<int32_t>(LoadGuestU32(base, quest + 36u));
    entry.renown = static_cast<int32_t>(LoadGuestU32(base, quest + 32u));

    const std::string conclusion = ReadGuestLhString(base, quest + 0x50u);
    std::snprintf(entry.conclusion, sizeof(entry.conclusion), "%s", conclusion.c_str());
    ++count;
  }

  PublishQuests(entries, count);
}

/// Which of the hero's garments are dyed, by the guest's own test.
///
/// `sub_826CAFA8(_, itemRef, slot)` reads only `*itemRef` (the item) and the item's type-2
/// component through `sub_823A1E18`, so the ref need not be registered. Worn items first
/// (component 15, +0x10..+0x14, 24-byte records, item at +0), then the inventory (component 13:
/// the list head at +16, node+0 next, node+8 the item — the walk `sub_824F1C90` makes). Record
/// hash at item+4, category at item+16; only what the dye list offers is asked about —
/// categories 9..11 and 17..24 (`sub_829B2168`).
void SampleDyeStates(PPCContext& ctx, uint8_t* base) {
  const uint32_t hero = HeroEntity(base);
  const uint32_t inventory = hero != 0u ? HeroComponent(base, hero, 13u) : 0u;
  const uint32_t appearance = hero != 0u ? HeroComponent(base, hero, 15u) : 0u;
  PPCFunc* is_dyed = FindGuestFunction(refii::guest::Address(0x826CAFA8u));
  if (inventory == 0u || is_dyed == nullptr) {
    refii_bridge_note_dye_states(nullptr, 0u);
    return;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t ref = frame + 0x20u;

  uint32_t words[kDyeStateItems * kDyeStateWords];
  uint32_t count = 0u;

  const auto offered = [](uint32_t category) {
    return (category >= 9u && category <= 11u) || (category >= 17u && category <= 24u);
  };
  const auto note = [&](uint32_t item, bool worn) {
    if (!LooksLikeGuestPointer(item) || count >= static_cast<uint32_t>(kDyeStateItems) ||
        !offered(LoadGuestU32(base, item + 16u))) {
      return;
    }
    const uint32_t record = LoadGuestU32(base, item + 4u);
    for (uint32_t i = 0u; i < count; ++i) {
      if (words[i * kDyeStateWords] == record) {
        return;
      }
    }

    uint32_t flags = worn ? 4u : 0u;
    for (uint32_t slot = 0u; slot < 2u; ++slot) {
      StoreGuestU32(base, ref, item);
      StoreGuestU32(base, ref + 4u, 0u);
      StoreGuestU32(base, ref + 8u, 0u);
      StoreGuestU32(base, ref + 12u, 0u);
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = 0u;
      ctx.r4.u32 = ref;
      ctx.r5.u32 = slot;
      is_dyed(ctx, base);
      if ((ctx.r3.u32 & 0xFFu) != 0u) {
        flags |= 1u << slot;
      }
    }
    ctx = saved;

    words[count * kDyeStateWords] = record;
    words[count * kDyeStateWords + 1u] = flags;
    ++count;
  };

  if (appearance != 0u) {
    const uint32_t begin = LoadGuestU32(base, appearance + 0x10u);
    const uint32_t end = LoadGuestU32(base, appearance + 0x14u);
    if (LooksLikeGuestPointer(begin) && end >= begin && ((end - begin) % 24u) == 0u &&
        (end - begin) <= 24u * 64u) {
      for (uint32_t at = begin; at < end; at += 24u) {
        note(LoadGuestU32(base, at), true);
      }
    }
  }

  const uint32_t head = LoadGuestU32(base, inventory + 16u);
  if (LooksLikeGuestPointer(head)) {
    uint32_t node = LoadGuestU32(base, head);
    for (uint32_t steps = 0u; steps < 4096u && node != head && LooksLikeGuestPointer(node);
         ++steps) {
      note(LoadGuestU32(base, node + 8u), false);
      node = LoadGuestU32(base, node);
    }
  }

  ctx = saved;
  refii_bridge_note_dye_states(words, count);
}

/// Which pause tabs the guest would offer, by calling its own predicate for tabs 2..8.
///
/// `sub_826B83D0(unused, tab)` is what the guest's tab navigation runs; there is no availability
/// mask in the image and `MenuCycler.bgf` has no per-tab element. Its first argument is dead
/// (r3 is overwritten at 0x826B8420 before any read), so 0 is passed.
///
/// Must run on the guest thread: with a stale control handle, 0x826B845C takes `sub_82193E38`,
/// which drops a refcount, may free the control block and nulls the handle.
///
/// Sampled on the GUI tick because the game-loop tick stops while the pause book is open.
void SamplePauseTabs(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kFirstTab = 2u;
  constexpr uint32_t kLastTab = 8u;

  // THE PREDICATE'S OWN PRECONDITION, checked here because we call it from outside the
  // game's flow. `sub_8240B2A0` (TU1 `sub_826B83D0`) picks the local player from the player
  // table -- id 1 when the net context's bytes +52 and +53 are both set, else id 2 -- takes
  // that player's entity handle at +12, and reads a flag byte at entity+144 with no null
  // check. The guest only ever runs it from pause-tab navigation, when that entity exists.
  // We run it every eighth GUI tick, and on a JOINER in the moment between the session
  // forming and the player's entity existing it read 0+144: the co-op CTD of 2026-10-07,
  // "read of guest 0x00000090" in rex_lhGuiPauseMenu_IsTabAvailable. Skipped until the
  // entity is there; the previous mask stands.
  {
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
    const uint32_t net = plausible(game) ? LoadGuestU32(base, game + 140u) : 0u;
    if (!plausible(net)) {
      return;
    }
    const bool both = base[net + 52u] != 0u && base[net + 53u] != 0u;
    const uint32_t player = PlayerWithId(base, both ? 1u : 2u);
    const uint32_t block = plausible(player) ? LoadGuestU32(base, player + 12u + 4u) : 0u;
    const uint32_t entity = plausible(block) ? LoadGuestU32(base, block) : 0u;
    if (!plausible(entity)) {
      return;
    }
  }

  const PPCContext saved = ctx;

  uint32_t mask = 0u;
  for (uint32_t tab = kFirstTab; tab <= kLastTab; ++tab) {
    ctx = saved;
    ctx.r3.u32 = 0u;
    ctx.r4.u32 = tab;
    rex_lhGuiPauseMenu_IsTabAvailable(ctx, base);
    if (ctx.r3.u32 != 0u) {
      mask |= 1u << tab;
    }
  }

  ctx = saved;

  // Tab 8 is unconditional in the predicate, so a sampled mask is never 0 and the host can use
  // 0 to mean "not sampled yet" without ambiguity.
  refii_bridge_note_pause_tabs(mask);
}

/// The attractiveness ratio as the page's writer `sub_829BA860` stated it (float bits). It is
/// not the sum of the eleven rows: the writer adds five preference axes (`sub_824C3AF8` with
/// 0, 1, 2, 4, 3) where the page displays three, plus the GDB "BalancingAugmentations" record's
/// "Attractiveness" times 100 when `sub_8223D888`/`sub_8223D570` say the hero carries it, then
/// clamps to [-100, 100] and states `(clamped + 100) * 0.0049999999` to
/// `sub_82881E38(page + 36, ratio)` at 0x829BABD0.
std::atomic<uint32_t> g_attractiveness_total_bits{0};
std::atomic<bool> g_attractiveness_total_known{false};

/// The eleven attractiveness contributions, CALLED rather than reproduced: each term is a vector
/// walk over the hero's appearance modifiers, a GDB chain (`AppearanceModifierComponent` ->
/// `DefaultMaleEffects`, overridden by `OverrideFemaleEffects` only when the female record
/// declares the property itself rather than inheriting it), a coverage weight computed against a
/// body-area table using a scratch `visited` byte, and a per-term clamp. `sub_824C3AF8` resolves
/// its `Effects` list through a runtime name registry, so it is not decidable from the image.
///
/// Gated on the logbook being up: these allocate (`sub_824C3260` copies a vector), and guest-heap
/// allocation on the tick thread is only worth paying while the page is open.
void SampleAttractiveness(PPCContext& ctx, uint8_t* base, uint32_t hero, HeroDerived& sampled) {
  constexpr uint32_t kAppearanceComponent = 15u;
  constexpr uint32_t kLogbookScreenId = 17u;

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id) || screen_id != kLogbookScreenId) {
    return;
  }

  const uint32_t look = HeroComponent(base, hero, kAppearanceComponent);
  if (look == 0u) {
    return;
  }

  const PPCContext saved = ctx;

  // `sub_824C3570(c15, kind)` — one appearance-modifier kind.
  const auto modifier = [&](uint32_t kind) {
    ctx = saved;
    ctx.r3.u32 = look;
    ctx.r4.u32 = kind;
    rex_CECAppearanceModifiers_GetModifierEffectTerm(ctx, base);
    return static_cast<float>(ctx.f1.f64);
  };

  // `sub_824C3AF8(c15, axis)` — one preference axis.
  const auto preference = [&](uint32_t axis) {
    ctx = saved;
    ctx.r3.u32 = look;
    ctx.r4.u32 = axis;
    rex_CECAppearanceModifiers_GetPreferenceTerm(ctx, base);
    return static_cast<float>(ctx.f1.f64);
  };

  // Clothing is the sum of kinds 17..23, as `sub_829BA860` stores at `screen+92`. Each kind is
  // already clamped to +/-100 by its accessor and the guest adds the clamped values.
  float clothing = 0.0f;
  for (uint32_t kind = 17u; kind <= 23u; ++kind) {
    clothing += modifier(kind);
  }

  // The page's own row order, which is the order the template names them in.
  sampled.attractiveness[0] = clothing;
  sampled.attractiveness[1] = modifier(9u);    // hair
  sampled.attractiveness[2] = modifier(10u);   // beard
  sampled.attractiveness[3] = modifier(11u);   // moustache
  sampled.attractiveness[4] = modifier(13u);   // make-up
  sampled.attractiveness[5] = modifier(12u);   // tattoos

  ctx = saved;
  ctx.r3.u32 = look;
  rex_CECAppearanceModifiers_GetPurityOpinionTerm(ctx, base);
  sampled.attractiveness[6] = static_cast<float>(ctx.f1.f64);   // purity/corruption

  sampled.attractiveness[7] = preference(3u);  // muscle
  sampled.attractiveness[8] = preference(1u);  // fatness
  sampled.attractiveness[9] = preference(4u);  // scars

  ctx = saved;
  ctx.r3.u32 = look;
  rex_CECAppearanceModifiers_GetHomeBonusTerm(ctx, base);
  sampled.attractiveness[10] = static_cast<float>(ctx.f1.f64);  // home bonus

  ctx = saved;

  // The total the page stated on its last write. Absent until the page has drawn once, which is
  // why it is a separate flag rather than a zero.
  if (g_attractiveness_total_known.load(std::memory_order_relaxed)) {
    const uint32_t bits = g_attractiveness_total_bits.load(std::memory_order_relaxed);
    std::memcpy(&sampled.attractiveness_fraction, &bits, sizeof(float));
  }
}

void SampleHeroDerived(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kStatsComponent = 57u;

  const uint32_t hero = HeroEntity(base);
  const uint32_t stats = hero != 0u ? HeroComponent(base, hero, kStatsComponent) : 0u;
  if (stats == 0u) {
    return;
  }

  HeroDerived sampled = HeroDerivedSnapshot();
  sampled.known = true;

  const PPCContext saved = ctx;

  ctx.r3.u32 = stats;
  rex_lhHeroStats_GetMorality(ctx, base);
  sampled.morality = static_cast<float>(ctx.f1.f64);

  ctx = saved;
  ctx.r3.u32 = stats;
  rex_lhHeroStats_GetPurity(ctx, base);
  sampled.purity = static_cast<float>(ctx.f1.f64);

  ctx = saved;
  ctx.r3.u32 = stats;
  rex_lhHeroStats_GetRenown(ctx, base);
  sampled.renown = static_cast<int32_t>(ctx.r3.u32);

  ctx = saved;

  // The ranges the bands are tested against. Plain fields, so they are read.
  const auto read_float = [base](uint32_t at) {
    const uint32_t raw = LoadGuestU32(base, at);
    float value = 0.0f;
    std::memcpy(&value, &raw, sizeof(value));
    return value;
  };

  sampled.morality_min = read_float(stats + 20u);
  sampled.morality_max = read_float(stats + 24u);
  sampled.purity_min = read_float(stats + 40u);
  sampled.purity_max = read_float(stats + 44u);

  // `c57+92` is a GDB def HANDLE, not a pointer: `{ u32 recordIdHash, u32 cachedEntry }`,
  // written as one 8-byte store by `sub_824D8568`. Publish the hash; the cached pointer is
  // meaningless outside the guest's address space.
  const uint32_t title_record = LoadGuestU32(base, stats + 92u);
  std::snprintf(sampled.title_tag, sizeof(sampled.title_tag), "%08X", title_record);

  // The stat counters — a flat read, so it runs every sample.
  for (uint32_t id = 0; id < HeroDerived::kStatCount; ++id) {
    sampled.stats[id] = static_cast<int32_t>(LoadGuestU32(base, stats + 348u + 4u * id));
  }

  sampled.stats_known = true;

  SampleDog(base, sampled);
  SampleQuests(base, hero);
  SampleWorldMapRegion(ctx, base);
  SampleFamily(ctx, base);
  SampleSpellDamage(base, hero, sampled);

  // The attractiveness block only while the logbook is up: it is the one sampler that allocates
  // from the guest heap. `sub_824C3AF8` builds an `LHString` from "PersonalityAdultBase" before
  // any guard, appends the axis name and grows and frees a vector — three heap round-trips per
  // axis — and `sub_824C4030` allocates unconditionally; all of it through `sub_8222CED0` ->
  // 0x8221F258 -> `sub_8221F2C0`. The page samples on activate, so a half-second delay after
  // opening is invisible.
  constexpr uint32_t kLogbookScreenId = 17u;
  uint32_t screen_owner = 0;
  uint32_t screen_id = 0;
  if (InGameScreenRoots(base, &screen_owner, &screen_id) && screen_id == kLogbookScreenId) {
    SampleAttractiveness(ctx, base, hero, sampled);
  } else {
    // Keep what was already published rather than zeroing: the store is a snapshot, and a page
    // reading it a frame after closing should see the last real answer.
    const HeroDerived previous = HeroDerivedSnapshot();
    for (uint32_t i = 0; i < HeroDerived::kAttractivenessCount; ++i) {
      sampled.attractiveness[i] = previous.attractiveness[i];
    }
    sampled.attractiveness_fraction = previous.attractiveness_fraction;
  }

  PublishHeroDerived(sampled);
}

/// Commits a menu row by identity — see kActionMenuEventPrefix.
bool DispatchMenuEvent(PPCContext& ctx, uint8_t* base, const std::string& name, uint32_t type,
                       uint32_t id, bool arm) {
  // The name goes into a Lua single-quoted literal, so anything that could close it early or
  // escape into the chunk is refused rather than sanitised — a mangled name would match a
  // different branch of somebody's strcmp, which is worse than not committing.
  for (char c : name) {
    if (c == '\'' || c == '\\' || c == '\n' || c == '\r' ||
        static_cast<unsigned char>(c) < 0x20u) {
      REXLOG_INFO("Refii guest-tick pump: MenuEvent name '{}' is not a safe Lua literal — dropped",
                  name);
      return true;
    }
  }

  // The arm comes with the press, because in the guest it comes before it. `sub_826C2958` (the
  // abilities screen's vtable+24) answers A and Y by writing one byte and only then firing the
  // event chain:
  //
  //     case 36: v4 = 1; goto LABEL_17;      // A — learn
  //     case 39: v4 = 0;
  //     LABEL_17: result[198] = v4; result = sub_826D58F0(result);
  //
  // and the consumer `sub_826C2D40` branches on that byte: armed goes to the purchase
  // `sub_8246C970`, clear to the "can you buy this?" query. The base is the HANDLER — the
  // object `sub_8236FFC0(owner, screenId)` returns, the same one DispatchScreenAction calls
  // vtable+24 on — not the screen record or the GUI.
  //
  // `screen+198` selects purchase over query for whoever handles the next type-20 event, so it
  // must only ever be set as the first half of a press actually delivered. The byte is already
  // 1 from the constructor (`stb r29, 0xC6(r31)` at `0x826c1fa4`) and only `ScreenAction(39)`
  // clears it, so writing it is normally a no-op.
  {
    uint32_t owner = 0;
    uint32_t screen_id = 0;
    if (InGameScreenRoots(base, &owner, &screen_id) && screen_id == 14u) {
      PPCContext handler_ctx = ctx;
      handler_ctx.r3.u32 = owner;
      handler_ctx.r4.u32 = refii::guest::ScreenId(screen_id);
      rex_lhGuiManager_FindScreenHandler(handler_ctx, base);
      const uint32_t handler = handler_ctx.r3.u32;
      if (handler >= 0x10000u && handler < 0xC0000000u) {
        StoreGuestU8(base, handler + 198u, arm ? 1u : 0u);
        REXLOG_INFO("Refii guest-tick pump: abilities handler 0x{:08X}+198 = {} before '{}' — "
                    "set is the PURCHASE arm (sub_8246C970), clear is the DISCARD confirm; "
                    "the guest's own A and Y store exactly this (sub_826C2958 cases 36/39)",
                    handler, arm ? 1 : 0, name);
      }
    }
  }

  std::string chunk = "GUI:PushButtonEvent('";
  chunk += name;
  chunk += "',";
  chunk += std::to_string(type);
  chunk += ',';
  chunk += std::to_string(id & 0xFFFFu);
  chunk += ',';
  chunk += std::to_string((id >> 16) & 0xFFFFu);
  chunk += ')';

  // The direct route first: it is the only one that executes (see DispatchMenuEventDirect).
  // No pending sample on this route — `ConfirmPendingMenuEvent` watches the Lua queue's depth,
  // which a direct dispatch never touches.
  if (DispatchMenuEventDirect(ctx, base, name, type, id)) {
    return true;
  }

  // The push is kept for screens whose handler this build cannot call, so their commits still
  // reach the queue the guest drains — on a frame where the immediate arm happens to be up.
  if (!RunGuestLua(ctx, base, chunk)) {
    // RunGuestLua already logged which refusal it was.
    REXLOG_INFO("Refii guest-tick pump: MenuEvent '{}' was not delivered — see the reason above",
                name);
    return true;
  }

  // The chunk is deferred, so its effect cannot be sampled here: `sub_82426470` appends to a
  // command list run later at the flush (`sub_821738A0`, from 0x82287154) whenever
  // `guiObject+0x64` is clear, which it is from the second GUI update on. Reading
  // `EventQueueSize` now would always return the pre-push value; the honest sample is one tick
  // later, via `_menuEventPending`.
  _menuEventPending = MenuEventPending{name, type, id, GuestEventQueueSize(ctx, base)};
  return true;
}

bool DispatchQueryAnswer(PPCContext& ctx, uint8_t* base, uint32_t code) {
  const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != kInGameGuiVtable) {
    REXLOG_INFO("Refii guest-tick pump: QueryAnswer:{} with no in-game GUI — dropped", code);
    return true;
  }

  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!plausible(owner) ||
      LoadGuestU32(base, owner + 40u) == 1u) {    // the modal latch is idle — no box
    // The gate is the MODE alone: query id 0 is a real box (the shop's tattoo notice), so
    // "+44 == 0" would drop a live answer. Modes 2 (MessageBox), 3 (QueryBox) and 5 (the
    // numeric adjuster) all answer through the same router.
    REXLOG_INFO("Refii guest-tick pump: QueryAnswer:{} with no box pending — dropped", code);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = code;
  ctx.r5.u32 = 0u;  // the pad slot; the pause handler replaces it with the query id itself

  rex_lhGuiManager_RouteModalInput(ctx, base);

  ctx = saved;
  return true;
}

/// Takes the whole queue under the lock and runs it outside.
///
/// Swapped rather than popped one at a time: actions make guest calls, and a host mutex must
/// never be held across guest code. A post made while the pump runs lands in the next tick.
void DrainOnGuestThread(PPCContext& ctx, uint8_t* base) {
  const uint32_t taken = GuestTickPumpBeginDrain();
  if (taken == 0) {
    return;
  }

  for (uint32_t slot = 0; slot < taken; ++slot) {
    const std::string name = GuestTickPumpTakenAction(slot);
    bool ran = false;

    // Logged before the call, not only after it: everything below dispatches into the guest on
    // its own thread, so an action that never returns would otherwise log nothing. The last
    // `dispatching '<name>'` with no matching `ran` names the action the thread is parked in.
    REXLOG_INFO("Refii guest-tick pump: dispatching '{}' ({} of {} this tick)",
                name, slot + 1, taken);

    // Dispatch BY NAME: the guest's front-end dispatcher is a strcmp chain over these strings,
    // so an entry the guest never had simply never matches. The two new-game names are the
    // exception — the guest's "New Game" branch only opens the gender card, which Godot draws
    // itself, so these carry the ANSWER straight to the commit.
    if (name == "New Game") {
      ran = StartNewGame(ctx, base, /*female=*/false);
    } else if (name == "New Game Female") {
      ran = StartNewGame(ctx, base, /*female=*/true);
    } else if (name.rfind(kActionLanguagePrefix, 0) == 0) {
      // The name carries the index; the dispatcher takes it from `a2+8` and the name is never
      // looked at on this path, so it is left empty rather than sent as "Language:3".
      const std::string index = name.substr(std::strlen(kActionLanguagePrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no language index — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchByName(ctx, base, std::string(), kCommandSetLanguage,
                           static_cast<uint32_t>(value));
    } else if (name.rfind(kActionLoadSavePrefix, 0) == 0) {
      const std::string index = name.substr(std::strlen(kActionLoadSavePrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no save index — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = LoadSavedGame(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionSaleSelectPrefix, 0) == 0) {
      // `<key>:<0|1>` — see kActionSaleSelectPrefix.
      const std::string rest = name.substr(std::strlen(kActionSaleSelectPrefix));
      const std::size_t colon = rest.find(':');
      char* keyEnd = nullptr;
      const unsigned long key = std::strtoul(rest.c_str(), &keyEnd, 0);
      if (colon == std::string::npos || keyEnd != rest.c_str() + colon) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no sale key — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = DispatchSaleSelect(ctx, base, static_cast<uint32_t>(key), rest.substr(colon + 1) == "1");
    } else if (name.rfind(kActionQuestPrefix, 0) == 0) {
      // `select:<record>` — see kActionQuestPrefix. The record is the quest definition's GDB
      // hash, decimal or 0x-hex, as the host's row carries it.
      const std::string rest = name.substr(std::strlen(kActionQuestPrefix));
      const std::string verb = "select:";
      if (rest.rfind(verb, 0) != 0) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not a quest verb — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      const std::string number = rest.substr(verb.size());
      char* numberEnd = nullptr;
      const unsigned long value = std::strtoul(number.c_str(), &numberEnd, 0);
      if (number.empty() || numberEnd == number.c_str() || *numberEnd != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no quest record — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = DispatchQuestSelect(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionSpellAssignPrefix, 0) == 0) {
      // Two numbers, `slot:spell` — see kActionSpellAssignPrefix.
      const std::string pair = name.substr(std::strlen(kActionSpellAssignPrefix));
      const std::size_t colon = pair.find(':');
      char* slotEnd = nullptr;
      char* spellEnd = nullptr;
      unsigned long slot = 0;
      unsigned long spell = 0;
      if (colon != std::string::npos) {
        const std::string slotText = pair.substr(0, colon);
        const std::string spellText = pair.substr(colon + 1);
        slot = std::strtoul(slotText.c_str(), &slotEnd, 10);
        spell = std::strtoul(spellText.c_str(), &spellEnd, 10);
        if (slotEnd == slotText.c_str() || *slotEnd != '\0' ||
            spellEnd == spellText.c_str() || *spellEnd != '\0') {
          slotEnd = nullptr;
        }
      }

      if (colon == std::string::npos || slotEnd == nullptr) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not slot:spell — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchSpellAssign(ctx, base, static_cast<uint32_t>(slot),
                                static_cast<uint32_t>(spell));
    } else if (name == kActionMenuSelect) {
      // `SelectMenuItem` minus its first line. The shipped body
      // (`expandablemenuinput.dis main.5`) is the ghost test, `GUI:PushButtonEvent`, and the
      // `#ChildItems > 0` fold with its expand or collapse event, prefixed by
      // `if self:WaitForInput() then return end`. `WaitForInput` (main.4) computes
      // `the_time - start_time`, and `the_time` has NO WRITER anywhere in `default.xex`, so it
      // is nil, the subtraction raises, `luaD_pcall` swallows it and `SelectMenuItem` aborts
      // before the event and the fold. Refusing a press mid-animation is the host's decision
      // anyway; everything else here is the shipped function verbatim.
      ran = RunGuestLua(
          ctx, base,
          "local m=g_Menu local i=m.VisibleInfo.VisibleElements[m.CurrentHighlightIdx] "
          "if i and not i.Ghosted then "
          "GUI:PushButtonEvent(i.Name,i.EventType,i.IDLow,i.IDHigh) "
          "if #i.ChildItems>0 then i.IsOpen=not i.IsOpen "
          "if i.IsOpen then m:FireExpandMenuItemEvent(m.CurrentHighlightIdx) "
          "else m:FireCollapseMenuItemEvent(m.CurrentHighlightIdx) end end end");
      if (!ran) {
        REXLOG_INFO("Refii guest-tick pump: MenuSelect with no live presentation — dropped");
        ran = true;
      }
    } else if (name.rfind("debug-call:", 0) == 0) {
      // `debug-call:ADDR[:r3[:r4[:r5[:r6]]]]` — one guest function, called on the guest's own
      // tick thread with the registers stated, result r3 recorded for the host. The debug
      // channel's REPL primitive; only the loopback channel posts these.
      std::vector<uint64_t> values;
      std::size_t from = std::strlen("debug-call:");
      bool parsed = true;
      while (from <= name.size()) {
        const std::size_t colon = name.find(':', from);
        const std::string part = name.substr(
            from, colon == std::string::npos ? std::string::npos : colon - from);
        char* end = nullptr;
        values.push_back(std::strtoull(part.c_str(), &end, 16));
        if (end == part.c_str() || *end != '\0') {
          parsed = false;
          break;
        }
        if (colon == std::string::npos) {
          break;
        }
        from = colon + 1;
      }

      if (!parsed || values.empty() || values.size() > 5) {
        NoteDebugCallResult(0, 0, "debug-call needs ADDR[:r3[:r4[:r5[:r6]]]], all hex");
      } else {
        const uint32_t target = static_cast<uint32_t>(values[0]);
        PPCFunc* function = FindGuestFunction(target);
        if (function == nullptr) {
          NoteDebugCallResult(target, 0, "no recompiled function at that address");
        } else {
          const PPCContext saved = ctx;
          if (values.size() > 1) ctx.r3.u64 = values[1];
          if (values.size() > 2) ctx.r4.u64 = values[2];
          if (values.size() > 3) ctx.r5.u64 = values[3];
          if (values.size() > 4) ctx.r6.u64 = values[4];
          function(ctx, base);
          const uint64_t result = ctx.r3.u64;
          ctx = saved;
          NoteDebugCallResult(target, result, "ok");
        }
      }
      ran = true;
    } else if (name.rfind("debug-lua:", 0) == 0) {
      // Arbitrary Lua against the live GUI state, result noted for the host. GuiLua: predates
      // this and stays; this variant exists so the caller can SEE whether the chunk ran.
      const std::string chunk = name.substr(std::strlen("debug-lua:"));
      const bool luaRan = RunGuestLua(ctx, base, chunk);
      NoteDebugCallResult(0, luaRan ? 1u : 0u,
                          luaRan ? "ok" : "no live presentation to run Lua in");
      ran = true;
    } else if (name.rfind(kActionGuiLuaPrefix, 0) == 0) {
      const std::string chunk = name.substr(std::strlen(kActionGuiLuaPrefix));
      ran = RunGuestLua(ctx, base, chunk);
      REXLOG_INFO("Refii guest-tick pump: GuiLua probe {} ({} bytes)",
                  ran ? "ran" : "had no live presentation", chunk.size());
      ran = true;
    } else if (name.rfind(kActionMenuEventPrefix, 0) == 0 ||
               name.rfind(kActionMenuDiscardPrefix, 0) == 0) {
      const bool arm = name.rfind(kActionMenuEventPrefix, 0) == 0;
      // `MenuEvent:<name>:<type>:<id>` — the name may itself contain no colon, so the two
      // numbers are taken from the END and everything before them is the name.
      const std::string body = name.substr(std::strlen(
          arm ? kActionMenuEventPrefix : kActionMenuDiscardPrefix));
      const std::size_t lastColon = body.rfind(':');
      const std::size_t firstColon =
          lastColon == std::string::npos ? std::string::npos : body.rfind(':', lastColon - 1);
      if (lastColon == std::string::npos || firstColon == std::string::npos) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not name:type:id — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      const std::string typeText = body.substr(firstColon + 1, lastColon - firstColon - 1);
      const std::string idText = body.substr(lastColon + 1);
      char* typeEnd = nullptr;
      char* idEnd = nullptr;
      const unsigned long type = std::strtoul(typeText.c_str(), &typeEnd, 10);
      const unsigned long id = std::strtoul(idText.c_str(), &idEnd, 10);
      if (typeEnd == typeText.c_str() || *typeEnd != '\0' ||
          idEnd == idText.c_str() || *idEnd != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no type/id — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchMenuEvent(ctx, base, body.substr(0, firstColon),
                              static_cast<uint32_t>(type), static_cast<uint32_t>(id), arm);
    } else if (name.rfind(kActionCoopHeroWeaponsPrefix, 0) == 0) {
      ran = DispatchCoopHeroWeapons(name.substr(std::strlen(kActionCoopHeroWeaponsPrefix)));
    } else if (name.rfind(kActionCoopHeroGivePrefix, 0) == 0) {
      ran = DispatchCoopHeroGive(name.substr(std::strlen(kActionCoopHeroGivePrefix)));
    } else if (name.rfind(kActionCoopHeroWearPrefix, 0) == 0) {
      ran = DispatchCoopHeroWear(name.substr(std::strlen(kActionCoopHeroWearPrefix)));
    } else if (name.rfind(kActionCoopHeroStatsPrefix, 0) == 0) {
      ran = DispatchCoopHeroStats(name.substr(std::strlen(kActionCoopHeroStatsPrefix)));
    } else if (name.rfind(kActionCoopHeroFacePrefix, 0) == 0) {
      ran = DispatchCoopHeroFace(name.substr(std::strlen(kActionCoopHeroFacePrefix)));
    } else if (name.rfind(kActionCoopHeroHeldPrefix, 0) == 0) {
      ran = DispatchCoopHeroHeld(name.substr(std::strlen(kActionCoopHeroHeldPrefix)));
    } else if (name.rfind(kActionCoopHeroEquipPrefix, 0) == 0) {
      ran = DispatchCoopHeroEquip(name.substr(std::strlen(kActionCoopHeroEquipPrefix)));
    } else if (name.rfind(kActionCoopHeroApplyPrefix, 0) == 0) {
      ran = DispatchCoopHeroApply(name.substr(std::strlen(kActionCoopHeroApplyPrefix)));
    } else if (name.rfind(kActionCoopHeroBuildPrefix, 0) == 0) {
      ran = DispatchCoopHeroBuild(name.substr(std::strlen(kActionCoopHeroBuildPrefix)));
    } else if (name.rfind(kActionCoopHeroSkillsPrefix, 0) == 0) {
      ran = DispatchCoopHeroSkills(name.substr(std::strlen(kActionCoopHeroSkillsPrefix)));
    } else if (name.rfind(kActionCoopHeroBodyPrefix, 0) == 0) {
      ran = DispatchCoopHeroBody(ctx, base,
                                 name.substr(std::strlen(kActionCoopHeroBodyPrefix)));
    } else if (name.rfind(kActionQuickWheelPrefix, 0) == 0) {
      ran = DispatchQuickWheel(ctx, base, name.substr(std::strlen(kActionQuickWheelPrefix)));
    } else if (name.rfind(kActionSpellBladePrefix, 0) == 0) {
      ran = DispatchSpellBlade(ctx, base, name.substr(std::strlen(kActionSpellBladePrefix)));
    } else if (name.rfind(kActionSpellsMenuPrefix, 0) == 0) {
      ran = DispatchSpellsMenu(ctx, base, name.substr(std::strlen(kActionSpellsMenuPrefix)));
    } else if (name.rfind(kActionInventoryPrefix, 0) == 0) {
      ran = DispatchInventoryAction(ctx, base,
                                    name.substr(std::strlen(kActionInventoryPrefix)));
    } else if (name.rfind(kActionAbilityPrefix, 0) == 0) {
      ran = DispatchAbilityAction(ctx, base,
                                  name.substr(std::strlen(kActionAbilityPrefix)));
    } else if (name.rfind(kActionShopPrefix, 0) == 0) {
      ran = DispatchShopAction(ctx, base, name.substr(std::strlen(kActionShopPrefix)));
    } else if (name == kActionRegisterPlayer) {
      ran = DispatchRegisterPlayer(ctx, base);
    } else if (name.rfind(kActionPauseSavingPrefix, 0) == 0) {
      const std::string index = name.substr(std::strlen(kActionPauseSavingPrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0' || value > 2u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no saving param — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchPauseSavingEvent(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionQueryAnswerPrefix, 0) == 0) {
      const std::string codeText = name.substr(std::strlen(kActionQueryAnswerPrefix));
      char* end = nullptr;
      const unsigned long code = std::strtoul(codeText.c_str(), &end, 10);
      // Only the codes the taxonomy's handlers answer — see the dispatcher. 28/29 are the
      // numeric adjuster's increment/decrement.
      if (end == codeText.c_str() || *end != '\0' ||
          (code != 28u && code != 29u && code != 36u && code != 37u &&
           code != 39u && code != 46u && code != 47u && code != 75u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no answer code — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchQueryAnswer(ctx, base, static_cast<uint32_t>(code));
    } else if (name.rfind(kActionReceiveItemsPrefix, 0) == 0) {
      const std::string codeText = name.substr(std::strlen(kActionReceiveItemsPrefix));
      char* end = nullptr;
      const unsigned long code = std::strtoul(codeText.c_str(), &end, 10);
      if (end == codeText.c_str() || *end != '\0' ||
          (code != 36u && code != 39u && code != 75u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no card code — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchReceiveItemsAction(ctx, base, static_cast<uint32_t>(code));
    } else if (name.rfind(kActionMenuScrollPrefix, 0) == 0) {
      std::string where = name.substr(std::strlen(kActionMenuScrollPrefix));

      // "<direction>" is a press; "<direction>:released" is the matching release. The latch is
      // the guest's, so both edges have to be sent — see kActionMenuScrollPrefix.
      bool held = true;
      const std::string::size_type edge = where.find(':');
      if (edge != std::string::npos) {
        held = where.substr(edge + 1) != "released";
        where = where.substr(0, edge);
      }

      MenuDirection direction = MenuDirection::Up;
      if (where == "up") {
        direction = MenuDirection::Up;
      } else if (where == "down") {
        direction = MenuDirection::Down;
      } else if (where == "left") {
        direction = MenuDirection::Left;
      } else if (where == "right") {
        direction = MenuDirection::Right;
      } else {
        REXLOG_INFO("Refii guest-tick pump: '{}' names no direction — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchMenuDirection(ctx, base, direction, held);
    } else if (name.rfind(kActionScreenActionPrefix, 0) == 0) {
      std::string codeText = name.substr(std::strlen(kActionScreenActionPrefix));

      // `ScreenAction:37:root` — the host's answer to a predicate the guest cannot evaluate,
      // carried on the action name so it travels with the press it belongs to.
      bool at_root = false;
      const std::string::size_type flag = codeText.find(':');
      if (flag != std::string::npos) {
        at_root = codeText.substr(flag + 1) == "root";
        codeText = codeText.substr(0, flag);
      }

      char* end = nullptr;
      const unsigned long code = std::strtoul(codeText.c_str(), &end, 10);
      // The codes these screens answer; anything else is a host defect. 28..31 are the
      // DIRECTIONAL presses: the list screens swallow them (their scrolling is Lua's
      // `onUpdate` polling `GUI:IsPushingUp()`, hence MenuScroll), but the OPTIONS page answers
      // them natively — 28/29 move the row, 30/31 change its VALUE (pickbox index or slider
      // float). 6 is the one-off panels' DISMISS: the minigame tally (`sub_823CDC08`) and the
      // quest completion card (`sub_823D3F98`) answer only it.
      if (end == codeText.c_str() || *end != '\0' ||
          (code != 6u && code != 28u && code != 29u && code != 30u && code != 31u &&
           code != 36u && code != 37u && code != 39u && code != 59u &&
           code != 72u && code != 73u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no screen action — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchScreenAction(ctx, base, static_cast<uint32_t>(code), at_root);
    } else if (name.rfind(kActionMenuBoxSelectPrefix, 0) == 0 ||
               name.rfind(kActionMenuBoxAnswerPrefix, 0) == 0) {
      const bool select = name.rfind(kActionMenuBoxSelectPrefix, 0) == 0;
      const std::string number = name.substr(std::strlen(
          select ? kActionMenuBoxSelectPrefix : kActionMenuBoxAnswerPrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(number.c_str(), &end, 10);
      // The box caps at six options, so an index past that is a host defect, not a state
      // race — refused rather than clamped.
      if (end == number.c_str() || *end != '\0' || value > 6u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no menu index — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = select
                ? DispatchMenuBoxSelect(ctx, base, static_cast<uint32_t>(value))
                : DispatchMenuBoxAnswer(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionBoxActionPrefix, 0) == 0) {
      // `BoxAction:<code>` answers the box the router would pick (`ShowingDisplayBox`);
      // `BoxAction:<code>@<hex>` answers the display MessageBox whose Open was stated.
      std::string number = name.substr(std::strlen(kActionBoxActionPrefix));
      uint32_t target = 0u;
      if (const size_t at = number.find('@'); at != std::string::npos) {
        target = static_cast<uint32_t>(std::strtoul(number.c_str() + at + 1, nullptr, 16));
        number.resize(at);
      }
      char* end = nullptr;
      const unsigned long value = std::strtoul(number.c_str(), &end, 10);
      // The codes the box handlers answer: 28/29 step the adjuster, 36 dismisses, 46 confirms,
      // 47 cancels. Anything else is refused rather than silently ignored by a handler.
      if (end == number.c_str() || *end != '\0' ||
          (value != 28u && value != 29u && value != 36u && value != 46u && value != 47u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no box action — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = target != 0u
                ? DispatchStatedMessageBoxAction(ctx, base, target, static_cast<uint32_t>(value))
                : DispatchDisplayBoxAction(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionFrontEndDialogPrefix, 0) == 0) {
      char* end = nullptr;
      const char* digits = name.c_str() + std::strlen(kActionFrontEndDialogPrefix);
      const unsigned long value = std::strtoul(digits, &end, 10);
      if (end == digits || *end != '\0' || (value != 36u && value != 37u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not a dialog answer — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchFrontEndDialog(ctx, base, static_cast<uint32_t>(value));
    } else if (name == "SubtitlesOn" || name == "SubtitlesOff" ||
               name == "Downloadable Content") {
      // Through the guest's own dispatcher: its id-2 branch compares the item name and does the
      // whole job — the subtitle arms write the setting (`sub_82443820`) and swap the emphasis,
      // and Downloadable Content raises its own no-profile dialog when nobody is signed in.
      ran = DispatchByName(ctx, base, name, kCommandActivateItem, 0u);
      if (ran && name[0] == 'S') {
        g_sample_subtitles_after_drain = true;
      }
    } else if (name == kActionSlideUp) {
      // Name empty on purpose: the switch takes the id before the name chain is reached, so
      // the string is never dereferenced on this path.
      ran = DispatchByName(ctx, base, std::string(), kCommandSlideMenuUp);
    } else if (name == kActionSlideDown) {
      ran = DispatchByName(ctx, base, std::string(), kCommandSlideMenuDown);
    } else {
      // Everything else goes through the guest's by-name dispatcher; an unshipped name matches
      // nothing in the strcmp chain, so there is no host-side list to keep in step.
      ran = DispatchByName(ctx, base, name);
    }

    if (ran) {
      REXLOG_INFO("Refii guest-tick pump: ran '{}' on the guest tick thread", name);
      GuestTickPumpNoteRan();
    } else {
      // The world is not ready — no front-end state, or the event ids are not interned yet.
      // Held rather than dropped or forced: the pump runs from the boot movies onward, so
      // "too early" is normal for the first few hundred ticks. Logged on a rising power of two
      // so a held action is distinguishable from one the drain never saw, without flooding.
      static std::atomic<uint64_t> deferrals{0};
      const uint64_t seen = deferrals.fetch_add(1, std::memory_order_relaxed) + 1;
      if ((seen & (seen - 1)) == 0) {
        REXLOG_INFO("Refii guest-tick pump: '{}' DEFERRED — the guest is not ready for it "
                    "(deferral #{}); the queue will hold it until it is",
                    name, seen);
      }

      GuestTickPumpDeferAction(name);
    }
  }

  // Puts the deferred ones back at the front of the queue and drops the batch.
  GuestTickPumpEndDrain();
}

// ResolveInGameLoadingScreen lives in src/core/guest_tick_pump_state.cpp and is reached through
// the wrapper in vfs_hooks_private.h — one copy, shared with the host's own finisher.

/// Is a game WORLD up and populated — `World + 0xA8`, the bit both the travel and load-from-save
/// paths set; the same walk `RefiiHostWorldIsActive` uses. False through boot and the front end,
/// which keeps the finish below off the startup loading owner.
bool InGameWorldActive(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };
  uint32_t at = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  at = step(at, 0x0Cu);
  at = step(at, 0x58u);
  at = step(at, 0x04u);
  at = step(at, 0x00u);
  return LooksLikeGuestPointer(at) && LoadGuestU8(base, at + 0xA8u) != 0;
}

/// Is this player the co-op JOINER — the co-op manager's own `+52` session and `+54` joiner
/// bytes, the same pair `RefiiHostGetCoopRole` reads and the same pair the guest's joiner-side
/// front-end teardown is gated on (`sub_82319BE8`, 0x82319DC0-0x82319E00).
/// The CNetworkManager itself — `g_XrnmNetContext` (0x83497490). `game+140` is only a counted
/// COPY of this pointer, filled by `sub_82319928` at a start commit; on a title-joined client it
/// read 0 in game (the joiner's level load builds a game object that path never fills), and
/// every guest chooser that reads `*(game+140)+52` faulted at 0x34. The global is the truth.
uint32_t CoopNetworkManager(uint8_t* base) {
  const uint32_t manager = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  return LooksLikeGuestPointer(manager) ? manager : 0u;
}

bool IsCoopJoiner(uint8_t* base) {
  const uint32_t coop = CoopNetworkManager(base);
  return coop != 0u && LoadGuestU8(base, coop + 52u) != 0 && LoadGuestU8(base, coop + 54u) != 0;
}

/// Whether a co-op session with a PEER is standing: `+52` session, not `+238` (the
/// lost-connection latch `sub_8232D4D8` sets first thing), and for a host `+221` ("Client
/// joined!", cleared in that same handler) or for a client `+54`. Everything the pump does to
/// player two's body is gated on this: the host's guest tears player two down inside
/// HandleLostConnection, and a tick that still held the address wrote through a freed entity
/// into code space.
bool CoopSessionLive(uint8_t* base) {
  const uint32_t coop = CoopNetworkManager(base);
  if (coop == 0u || LoadGuestU8(base, coop + 52u) == 0 || LoadGuestU8(base, coop + 238u) != 0) {
    return false;
  }

  const bool host = LoadGuestU8(base, coop + 53u) != 0;
  return host ? LoadGuestU8(base, coop + 221u) != 0 : LoadGuestU8(base, coop + 54u) != 0;
}

/// Puts the manager's counted pointer into `game+140/+144` when the guest left it empty — the
/// exact three words `sub_82319928` writes (0x82319928: `*(game+140) = g_XrnmNetContext;
/// *(game+144) = dword_83497494; ++*control`). The guest's own role choosers
/// (`(session && host) ? 1 : 2`, in `sub_829B1968`, `sub_826CC430`, `sub_821C7CE0` …) read the
/// copy, so a client with an empty copy cannot open its own weapons list or unequip a garment.
void CoopRepairNetworkSlot(uint8_t* base) {
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = LooksLikeGuestPointer(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (game == 0u || !LooksLikeGuestPointer(game)) {
    return;
  }

  const uint32_t manager = CoopNetworkManager(base);
  if (manager == 0u || LoadGuestU32(base, game + 140u) != 0u) {
    return;
  }

  const uint32_t control = LoadGuestU32(base, refii::guest::Address(0x83497494u));
  StoreGuestU32(base, game + 140u, manager);
  StoreGuestU32(base, game + 144u, control);
  if (LooksLikeGuestPointer(control)) {
    StoreGuestU32(base, control, LoadGuestU32(base, control) + 1u);
  }

  REXLOG_INFO("Refii guest-tick pump: co-op — game 0x{:08X} had no network manager; filled "
              "+140 with 0x{:08X} (control 0x{:08X}) the way sub_82319928 does", game, manager,
              control);
}

/// The fade the in-game loading screen is drawing — `screen+172`, the float
/// `RefiiHostGetLoadingScreen` reports and the one the finish below zeroes. 1.0 is a full cover.
float LoadingScreenFade(uint8_t* base, uint32_t screen) {
  const uint32_t raw = LoadGuestU32(base, screen + 172u);
  float fade = 0.0f;
  std::memcpy(&fade, &raw, sizeof(fade));
  return fade;
}

/// The co-op screen a joiner has already faded in for, so the fade-in happens once per showing.
std::atomic<uint32_t> g_coop_screen_faded_in{0};

/// FADE THE JOINER IN WHEN ITS OWN CO-OP SCREEN COMES UP — a DEADLOCK, not a cosmetic black.
///
/// A joining client loads the level behind a fade-out and the guest then opens screen 24 on it,
/// but nothing ever lifts the fade: `FadeScreenIn` (`sub_82305C08`) is something the HOST does
/// and BROADCASTS. Its body only reaches the send at 0x82305C98 when
/// `*(coopMgr+52) && *(coopMgr+53)` — the networked-HOST pair, tested at 0x82305C64 — so a
/// client never calls it for itself and only ever receives one.
///
/// Measured live: the joiner sat with `screenId 24` up and `[screen-fade] a1.00 t1.00` over it.
/// The player could not see, let alone accept, the screen the HOST was waiting on — so the host
/// sat on its own co-op screen for ever and neither side moved.
///
/// Calling the guest's own fade-in here runs `sub_823CE950` and skips the broadcast arm, which
/// is exactly what a client should do. Latched on the screen id so it fires once per showing and
/// cannot fight a fade the game starts later.
void MaybeFadeInCoopScreen(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kCoopScreenId = 24u;

  if (base == nullptr || !IsCoopJoiner(base)) {
    g_coop_screen_faded_in.store(0, std::memory_order_relaxed);
    return;
  }

  // The screen the manager says is up — `sub_8236E630` writes it at 0x8236EB20.
  const uint32_t app = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t owner = LooksLikeGuestPointer(app) ? LoadGuestU32(base, app + 56u) : 0u;
  const uint32_t current = LooksLikeGuestPointer(owner)
                               ? refii::guest::ScreenIdToTu1(LoadGuestU32(base, owner + 4u))
                               : 0u;

  if (current != kCoopScreenId) {
    g_coop_screen_faded_in.store(0, std::memory_order_relaxed);
    return;
  }

  if (g_coop_screen_faded_in.load(std::memory_order_relaxed) == kCoopScreenId) {
    return;
  }

  g_coop_screen_faded_in.store(kCoopScreenId, std::memory_order_relaxed);

  PPCContext work = ctx;
  work.f1.f64 = 0.5;  // seconds, the same shape the Lua native takes
  rex_lhScriptGui_FadeScreenIn(work, base);

  REXLOG_INFO("Refii co-op: the joiner's own screen 24 is up behind the host's fade — faded in "
              "here, because FadeScreenIn is the HOST's to broadcast (0x82305C64) and a client "
              "never calls it for itself");
}

/// The screen already brought down, so the finish fires once per load. Cleared when no screen
/// is up, which re-arms it for the next level change.
std::atomic<uint32_t> g_finished_loading_screen{0};

/// Bring the IN-GAME loading screen down — the host standing in for the present loop that would.
///
/// Nothing presents in this headless mode, so after an in-game level load the guest never runs
/// the render tick that fades its loading screen out and releases the game thread; it sits at
/// `fade=1 requested=0` with the loading music playing. This runs from the in-game tick
/// (`sub_82276D10` -> the pump).
///
/// Gated on the game world being active so it can only touch an in-game screen. Sequence: keep
/// the frame past the fade's ten-tick guard, drop the fade, request teardown, run the guest's
/// own fade/teardown tick, and if `done` does not latch, finish it directly and pulse the event
/// `sub_82361458` blocks the game thread on.
void MaybeFinishInGameLoadingScreen(PPCContext& ctx, uint8_t* base) {
  if (base == nullptr) {
    return;
  }

  static const bool enabled = [] {
    const char* value = std::getenv("REFII_FINISH_INGAME_LOADSCREEN");
    return value == nullptr || value[0] != '0';
  }();
  if (!enabled || !InGameWorldActive(base)) {
    return;
  }

  const uint32_t screen = ResolveInGameLoadingScreen(base);
  if (screen == 0) {
    g_finished_loading_screen.store(0, std::memory_order_relaxed);
    return;
  }
  // `+176` (requested) is the edge — not `done`, not the object address. It is the only field
  // that falls back to 0 between loads: `sub_82361458` sets it per shutdown and the completion
  // clears it (`sub_8235E5A0`, 0x8235E7CC, and the latch below). `+177` is write-once outside
  // the constructor `sub_8235CA90`, and the screen object is REUSED across loads, so latching
  // on either would stand this down permanently after the first level load.
  if (LoadGuestU8(base, screen + 176u) == 0) {
    // THE CO-OP JOINER NEVER ASKS, and its screen stays at a full cover for ever.
    //
    // Measured over three runs: a joining client sits at `fade=1 requested=False done=False`
    // with the world BUILT behind it — level construction finished, entities live, the camera
    // driven, frames presenting — and the only thing on screen is this screen's own black. One
    // run did request it and came down normally, so it is a RACE on the guest's side, not a
    // path that never runs.
    //
    // `+176` stays the edge for everyone else, because it is the only field that falls back to
    // 0 between loads and latching on `done` or the object address would stand this down
    // permanently after the first level change. The joiner gets a second trigger instead, and
    // one narrow enough not to touch the ordinary path: we are the joiner, the world is up and
    // populated (`InGameWorldActive`, checked above), and the screen is still covering
    // outright. A world that is not yet ready fails the first test, so this cannot uncover an
    // unfinished level.
    if (!IsCoopJoiner(base) || LoadingScreenFade(base, screen) < 0.999f) {
      g_finished_loading_screen.store(0, std::memory_order_relaxed);
      return;
    }

    REXLOG_INFO("Refii in-game loading screen: the co-op joiner never requested a shutdown and "
                "the world is up behind a full cover — finishing it here (screen=0x{:08X})",
                screen);
  }
  if (g_finished_loading_screen.load(std::memory_order_relaxed) == screen) {
    return;
  }

  if (LoadGuestU32(base, screen + 168u) < 10u) {
    StoreGuestU32(base, screen + 168u, 10u);
  }
  StoreGuestU32(base, screen + 172u, 0u);

  // Never drive `sub_8235E5A0` from this thread — it deadlocks:
  //
  //   rex_lhGameLoop_Tick -> sub_822C1BD0 -> OnGuestTick -> MaybeFinishInGameLoadingScreen
  //     -> sub_8235E5A0 -> rex_lhRenderer_BeginGPUFrame -> RtlEnterCriticalSection  [blocks]
  //
  // The screen's own tick opens a GPU frame, and that lock is held by the 3D Engine thread,
  // parked inside `rex_lhRenderer_RenderFrame` -> `sub_822DFE08` spinning on SwitchToThread
  // with no GPU to drain the ring. The game thread then waits for ever, the 15 Hz tick stops
  // and `dword_83319650` freezes, which also freezes the world update and the camera.
  //
  // Do the teardown by hand instead: latch `done` and pulse the event `sub_82361458` blocks on.
  // Pure memory writes plus one event signal, no renderer lock.
  StoreGuestU8(base, screen + 176u, 0u);
  StoreGuestU8(base, screen + 177u, 1u);
  const uint32_t event = LoadGuestU32(base, screen + 180u);
  if (LooksLikeGuestPointer(event)) {
    PPCContext signal_ctx = ctx;
    signal_ctx.r3.u32 = event;
    signal_ctx.lr = refii::guest::Address(0x8235E7D8u);
    ::__imp__rex_SetEvent(signal_ctx, base);
  }

  g_finished_loading_screen.store(screen, std::memory_order_relaxed);
  REXLOG_INFO("Refii in-game loading screen brought down (screen=0x{:08X})", screen);
}

/// Let the in-game tick actually simulate: `game+0x104`, the flag it gates all of it on.
///
/// `rex_lhGameLoop_Tick` (`sub_82276D10`) opens with
///
///     lbz r11, 260(r31)   ; game+0x104   -> if zero, SKIP
///     lbz r11, 261(r31)   ; game+0x105   -> if zero, SKIP
///
/// and everything behind that gate is the in-game world: the entity advance, the animation pose
/// build, the camera update. After a level load here `+0x105` is 1 and `+0x104` is 0, so the
/// tick does none of it; particle systems live OUTSIDE the gate, on `sub_821DFDD0`, which is why
/// FX animate while the hero, NPCs and camera are frozen.
///
/// The guest sets the flag from `sub_82312EE8` (`li r11,1; stb r11,260(r3)`) and siblings, all
/// on the front-end -> gameplay transition this hosted build never completes. Gated on
/// `InGameWorldActive` and on the guest's own `+0x105`. Off-switch `REFII_ARM_INGAME_SIM=0`.
void MaybeArmInGameSimulation(PPCContext& ctx, uint8_t* base) {
  (void)ctx;
  if (base == nullptr) {
    return;
  }

  // Off by default because it does not work: forcing `+0x104` to 1 opens the gate and the guest
  // faults within about half a second (0xC0000005). The clear flag is a SYMPTOM — the game
  // object has not completed its transition into gameplay, and the code behind the gate
  // dereferences state that transition builds. Opt in with `REFII_ARM_INGAME_SIM=1`.
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_ARM_INGAME_SIM");
    return value != nullptr && value[0] == '1';
  }();
  if (!enabled || !InGameWorldActive(base)) {
    return;
  }

  const uint32_t mode = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  if (!LooksLikeGuestPointer(mode)) {
    return;
  }
  const uint32_t game = LoadGuestU32(base, mode + 0x0Cu);
  if (!LooksLikeGuestPointer(game)) {
    return;
  }

  // Only when the guest says the world is ready and the flag is clear — writing every tick
  // would mask the guest setting it.
  if (LoadGuestU8(base, game + refii::guest::GameField(0x105u)) == 0 || LoadGuestU8(base, game + refii::guest::GameField(0x104u)) != 0) {
    return;
  }

  StoreGuestU8(base, game + refii::guest::GameField(0x104u), 1u);

  static std::atomic<uint32_t> armed{0};
  if (armed.fetch_add(1, std::memory_order_relaxed) < 4u) {
    REXLOG_INFO("Refii in-game simulation armed: game 0x{:08X} +0x104 0 -> 1 (+0x105={}), the "
                "game tick can now run the entity/animation/camera block",
                game, LoadGuestU8(base, game + refii::guest::GameField(0x105u)));
  }
}

// FinishStuckInGameLoadingScreenFromHost lives in src/core/guest_tick_pump_state.cpp: it runs on
// the HOST step thread, and refii_vfs is the only library both ends link. It needs no payload —
// guest memory through base plus the take-down event signal, no PPC call.

/// One tick's worth of pump work. Split out so the hook body stays the delegation it must be.
/// Reports whether the PREVIOUS tick's commit reached the Lua queue.
///
/// A tick later, because the push is deferred to the GUI's own command flush. The queue is
/// Lua-side and C++ is pull-only, so its depth is the only observable that moves.
void ConfirmPendingMenuEvent(PPCContext& ctx, uint8_t* base) {
  if (!_menuEventPending) {
    return;
  }

  const MenuEventPending pending = *_menuEventPending;
  _menuEventPending.reset();

  const int now = GuestEventQueueSize(ctx, base);
  if (pending.depth_after_push < 0 || now < 0) {
    return;
  }

  if (now > pending.depth_after_push) {
    return;
  }

  REXLOG_INFO(
      "Refii guest-tick pump: MenuEvent '{}' type={} id={} did NOT reach the queue "
      "(EventQueueSize {} -> {} across the flush) — the chunk never ran, or PushButtonEvent "
      "refused it",
      pending.name, pending.type, pending.id, pending.depth_after_push, now);
}

void OnGuestTick(PPCContext& ctx, uint8_t* base) {
  ConfirmPendingMenuEvent(ctx, base);

  const uint64_t tick = GuestTickPumpObserveTick();

  // The pump announcing itself, once and then rarely. Ticks 1, 600 and 6000 fall in different
  // states (boot movies 60 Hz, front end 30, game 15), so a once-only line could not tell "the
  // pump runs" from "the pump ran during the logos and stopped".
  if (tick == 0 || tick == 600 || tick == 6000 || tick == 60000) {
    REXLOG_INFO("Refii guest-tick pump: tick {} on the guest tick thread (drained {})",
                tick + 1, GuestTickPumpDrained());
  }

  // A stack overflow on this thread faults on its guard page, and a handler run on that same
  // stack cannot even be entered. Give the signal somewhere to run.
  EnsureAlternateSignalStack();

  // Raise Dead's creation effect, which the guest spawns and never kills. Here because both calls
  // re-enter the guest and this is an update context, which is where the guest makes the same two
  // calls for its other spells. It returns immediately when nothing is pending.
  SweepOrphanSpellFx(ctx, base);

  // The menu capture settles here: a population runs to completion inside the GUI update that
  // began it, so by the next tick the captured rows are whole. There is no end marker to use
  // instead — the in-game lists never call `UpdateAfterSpawning`.
  refii_bridge_menu_settle();

  DrainOnGuestThread(ctx, base);

  // The hero sample, on a cadence — see SampleHeroDerived.
  if ((tick % 15u) == 0u) {
    SampleHeroDerived(ctx, base);
    ArmHudShow(ctx, base);
  }

  // The present loop that would normally fade the in-game loading screen out is off in this
  // headless mode, so the guest never releases the game thread from it. Do it here, from the
  // in-game tick, once the world it was loading is up.
  MaybeFinishInGameLoadingScreen(ctx, base);
  MaybeFadeInCoopScreen(ctx, base);
  CoopRedress(ctx, base);

  MaybeArmInGameSimulation(ctx, base);

  // After the drain, so a "New Game" that just ran is reflected in the next answer rather than
  // the one before it.
  SampleSavedGameCount(ctx, base);

  // The subtitles setting, republished so the host's menu can mark the current choice.
  // ON EDGES ONLY, never per tick: the guest's reader (`sub_82382558` -> the profile-settings
  // machinery) wedges the tick thread if it collides with a load in progress. Sampled once when
  // the menu screen appears and once after a subtitles action ran.
  const uint32_t menu_screen = MainMenuScreen(base);
  const bool entered_menu = menu_screen != 0 && g_last_menu_screen == 0;
  g_last_menu_screen = menu_screen;

  if (entered_menu || (g_sample_subtitles_after_drain && menu_screen != 0)) {
    g_sample_subtitles_after_drain = false;
    PPCContext subtitles_ctx = ctx;
    __imp__rex_lhProfile_GetSubtitlesEnabled(subtitles_ctx, base);
    refii_bridge_front_end_note_subtitles(static_cast<int32_t>(subtitles_ctx.r3.u32));
  }
}

// `ReadGuestLhString` lives in core/guest_state.h — one implementation, shared with the
// menu-chunk hooks that need the same handle chase.

std::string ReadGuestWideLhString(uint8_t* base, uint32_t handle) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t at = handle;
  for (int hop = 0; hop < 3; ++hop) {
    if (!plausible(at)) {
      return std::string();
    }

    const uint32_t next = LoadGuestU32(base, at);
    if (plausible(next)) {
      std::string utf8;
      for (uint32_t i = 0; i < 192u; ++i) {
        const uint32_t unit = (static_cast<uint32_t>(base[next + (2u * i)]) << 8) |
                              base[next + (2u * i) + 1u];
        if (unit == 0u) {
          break;
        }

        // Is this hop characters or pointers? The handle chain holds guest addresses until the
        // last hop, and a 32-bit address read as two big-endian UTF-16 units lands in the CJK
        // block (0x404E3DF0 decodes as U+404E U+3DF0). Menu labels are Latin — every unit sits
        // below U+0250 — so requiring that of the first two units rejects a pointer table and
        // makes the loop take the next hop.
        if (i < 2u && (unit < 0x20u || unit >= 0x0250u)) {
          utf8.clear();
          break;
        }

        if (unit < 0x80u) {
          utf8 += static_cast<char>(unit);
        } else if (unit < 0x800u) {
          utf8 += static_cast<char>(0xC0u | (unit >> 6));
          utf8 += static_cast<char>(0x80u | (unit & 0x3Fu));
        } else {
          utf8 += static_cast<char>(0xE0u | (unit >> 12));
          utf8 += static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
          utf8 += static_cast<char>(0x80u | (unit & 0x3Fu));
        }
      }

      if (!utf8.empty()) {
        return utf8;
      }
    }

    at = next;
  }

  return std::string();
}

/// One spawned row. `stack_rating` says whether this emitter HAS a ninth argument.
///
/// The two emitters do NOT share a signature. `sub_826CE8D0` takes nine arguments, the ninth
/// (the rating) on the stack at caller_SP + 0x54 — which is why this runs before the recompiled
/// prologue. `sub_826E4798` takes exactly eight in r3..r10 and no stack arguments, hard-coding
/// `Float4 = 0.0`; reading r1+0x54 for it reads the caller's frame.
void OnMenuSpawn(PPCContext& ctx, uint8_t* base, bool stack_rating) {
  const std::string name = ReadGuestLhString(base, ctx.r4.u32);
  const std::string parent = ReadGuestLhString(base, ctx.r7.u32);
  const std::string label = ReadGuestWideLhString(base, ctx.r5.u32);
  const int32_t rating =
      stack_rating ? static_cast<int32_t>(LoadGuestU32(base, ctx.r1.u32 + 0x54u)) : 0;
  refii_bridge_menu_spawn_row_labelled(name.c_str(), parent.c_str(), ctx.r8.u32,
                                       static_cast<int32_t>(ctx.r9.u32), rating,
                                       label.c_str());
}
}  // namespace refii::vfs

REX_EXTERN(rex_lhTickDelegate_ForwardToInner) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnGuestTick(ctx, base);

  __imp__rex_lhTickDelegate_ForwardToInner(ctx, base);
}

// THE PAUSE HAS ITS OWN HEARTBEAT, AND THE PAUSE-SCREEN SAMPLERS MUST RIDE IT.
//
// `sub_822C1BD0` — the game-loop tick every sampler used to hang off — STOPS while the pause
// book is open: the pump's tick counter sat frozen at 1546 across eight seconds of an open
// world map. So a sampler gated on "screen 23 is up" could never fire — its precondition only
// holds while its carrier is stopped — and the region and family pages read blank forever
// while the hero and quest data (sampled in gameplay, before the pause) looked fine beside
// them.
//
// `sub_8229ABD8` is the pause-tab lever poller, run every GUI tick — the heartbeat that beats
// exactly when the game-loop tick does not. Cadenced: these samplers call guest providers, and
// every 8th GUI tick is twice a second at 15 Hz.
REX_EXTERN(rex_lhGuiManager_TickUpdate) {
  REX_FUNC_PROLOGUE();

  static std::atomic<uint32_t> gui_ticks{0};
  if ((gui_ticks.fetch_add(1, std::memory_order_relaxed) % 8u) == 0u) {
    refii::vfs::SampleWorldMapRegion(ctx, base);
    refii::vfs::SampleFamily(ctx, base);
    refii::vfs::SampleRealEstate(ctx, base);
    refii::vfs::SampleQuestTravel(ctx, base);
    refii::vfs::SampleShopPrice(ctx, base);
    refii::vfs::SamplePauseTabs(ctx, base);

    refii::vfs::SampleDyeStates(ctx, base);
  }

  __imp__rex_lhGuiManager_TickUpdate(ctx, base);
}

// The attractiveness ratio, read from the page's own store after its writer runs.
//
// `sub_829BA860` is the logbook attractiveness page's writer and the only caller of
// `sub_82881E38(page + 36, ratio)` for this meter. The meter stores the clamped ratio at
// `meter + 40` (0x82881E88), i.e. `page + 76`. The store is gated: `sub_82881E38` returns
// without writing unless `meter + 12` is set (0x82881E50), so the same gate applies here.
REX_EXTERN(rex_lhGui_LogbookAttractivenessPage_UpdateMeter) {
  REX_FUNC_PROLOGUE();

  const uint32_t page = ctx.r3.u32;

  __imp__rex_lhGui_LogbookAttractivenessPage_UpdateMeter(ctx, base);

  if (page == 0u) {
    return;
  }

  const uint32_t meter = page + 36u;
  if (refii::vfs::LoadGuestU8(base, meter + 12u) == 0u) {
    return;
  }

  refii::vfs::g_attractiveness_total_bits.store(refii::vfs::LoadGuestU32(base, meter + 40u),
                                                std::memory_order_relaxed);
  refii::vfs::g_attractiveness_total_known.store(true, std::memory_order_relaxed);
}

// The spawn, captured — the IN-GAME one, against GUI manager `dword_83496B08`.
REX_EXTERN(rex_lhGui_InGameMenu_SpawnButton) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnMenuSpawn(ctx, base, /*stack_rating=*/true);

  __imp__rex_lhGui_InGameMenu_SpawnButton(ctx, base);
}

// And the front end's — a second function with the same body against a different GUI manager
// (`dword_83496AC0`). These are the only two emitters of `g_Menu:SpawnButton()` in the image.
// Its `Float4` is hardcoded to 0.0: no front-end row has stars.
REX_EXTERN(rex_lhGui_Frontend_SpawnButton) {
  REX_FUNC_PROLOGUE();

  // EIGHT ARGUMENTS, NO STACK RATING — this emitter hard-codes Float4 = 0.0. See OnMenuSpawn.
  refii::vfs::OnMenuSpawn(ctx, base, /*stack_rating=*/false);

  __imp__rex_lhGui_Frontend_SpawnButton(ctx, base);
}

// The list boundary. `sub_826CE110` runs `"g_Menu:DeleteAllSpawnedMenuItems()"` and takes no
// arguments; it is the one marker both the in-game and front-end populations share. The END is
// not markable — only front-end lists call `UpdateAfterSpawning` — so the capture publishes per
// row rather than waiting for one.

/// The screen whose population is starting, as the GUEST states it: `manager+4` off the
/// in-game GUI. The FRONT END is a different GUI object, so `InGameScreenRoots` refuses it and
/// the capture is keyed "no in-game screen" — which is what stops front-end rows being handed
/// to an in-game template. See `refii_bridge_menu_spawn_begin_for`.
static uint32_t PopulatingScreenId(uint8_t* base) {
  uint32_t screen_id = 0;
  return refii::vfs::InGameScreenRoots(base, nullptr, &screen_id) ? screen_id : 0xFFFFFFFFu;
}

REX_EXTERN(rex_lhGui_InGameMenu_DeleteAllSpawnedMenuItems) {
  REX_FUNC_PROLOGUE();

  refii_bridge_menu_spawn_begin_for(PopulatingScreenId(base));

  __imp__rex_lhGui_InGameMenu_DeleteAllSpawnedMenuItems(ctx, base);
}

// The other six boundaries — populators that fire `DeleteAllSpawnedMenuItems` INLINE rather
// than through the wrapper above: world map sub_829A2F40 @0x829a2fc8, abilities sub_829A45A0,
// shop sub_82994218, logbook sub_829B8648, gift-gold sub_82430468, front end sub_826E1F70.
// Without them a screen's rows join onto the previous population's. The string match in the
// exec hook (resource_manager_open.cpp) is the net for any emitter not listed here.
#define REFII_MENU_BOUNDARY_HOOK(fn)                                            \
  REX_EXTERN(fn) {                                                             \
    REX_FUNC_PROLOGUE();                                                       \
    refii_bridge_menu_spawn_begin_for(PopulatingScreenId(base));               \
    __imp__##fn(ctx, base);                                                    \
  }

REFII_MENU_BOUNDARY_HOOK(rex_lhGuiWorldMapScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiAbilitiesScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiShopScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiLogbookScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiGiftGoldScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGui_FrontendRoot_PopulateMainMenu)

#undef REFII_MENU_BOUNDARY_HOOK

extern "C" {
void RefiiCoopSetOwnHero(int on) {
  refii::vfs::g_coop_own_hero.store(on != 0, std::memory_order_relaxed);
}

int RefiiCoopGetOwnHero(void) {
  return refii::vfs::g_coop_own_hero.load(std::memory_order_relaxed) ? 1 : 0;
}

/// Player two's weapon names for the hooks' table overrides; empty when none is set.
const char* RefiiCoopMeleeName(void) { return refii::vfs::g_coop_melee_name.c_str(); }
const char* RefiiCoopRangedName(void) { return refii::vfs::g_coop_ranged_name.c_str(); }

/// Dress player two's body after the guest has built or re-typed it — see CoopRedress.
void RefiiCoopRedress(void* ctx, uint8_t* base) {
  refii::vfs::CoopRedress(*static_cast<PPCContext*>(ctx), base);
}
}  // extern "C"
