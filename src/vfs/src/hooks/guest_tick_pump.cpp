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
#include <mutex>
#ifndef _WIN32
#include <signal.h>
#endif
#include <optional>
#include <string>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

// The New Game commit. NOT hooked — the guest's own function is called, so this is the plain
// symbol rather than an `__imp__` original.
PPC_EXTERN_FUNC(sub_826E0D60);

// The front end's own menu-action dispatcher, vtable slot +16 of 0x820D0934. Called directly:
// the slot is fixed.
PPC_EXTERN_FUNC(sub_826DFD58);

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
PPC_EXTERN_FUNC(sub_82441CB8);

PPC_EXTERN_FUNC(sub_82443308);

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
PPC_EXTERN_FUNC(sub_82379838);

// The screen-id -> handler map lookup, `*(GUI+28)` and a screen id in, handler object out.
// A find on the red-black tree at `guiOwner+68` (miss returns 0); registration is the
// guiOwner ctor `sub_8236D078`, which maps screen id 0 to the pause main menu handler
// (ctor `sub_826A1818`, vtable 0x820CDE18).
PPC_EXTERN_FUNC(sub_8236FFC0);

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
PPC_EXTERN_FUNC(sub_826A26E8);

// The ReceiveItemsScreen handler's SCREEN-INPUT method — vtable slot +24 of 0x820C0E08,
// `(this, code, held)`. The non-modal input route (vtable+28 is the modal one): code 36 (A)
// closes the card done, 39 (Y) flips it, 75 closes. The handler is the screen map's entry for
// id 8 (`sub_8236FFC0(guiOwner, 8)`).
PPC_EXTERN_FUNC(sub_82537128);

// The MenuBox accept — `sub_823DC720(ctl)`, ctl = *(guiOwner+56). Reads the selected index
// at ctl+0, checks the item's enabled byte, invokes the callback, and CLOSES/publishes
// itself (player message 35/101). Refuses a disabled item.
PPC_EXTERN_FUNC(sub_823DC720);

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
PPC_EXTERN_FUNC(sub_823CF3B0);
PPC_EXTERN_FUNC(sub_823CF8A8);
PPC_EXTERN_FUNC(sub_823D0090);
PPC_EXTERN_FUNC(sub_823CEDD8);

// The MenuBox close — `sub_823DC2B0(ctl)`. Publishes the result (value = ctl+13 ? 0 :
// selIdx+1) and tears the box down. Used for the host's cancel.
PPC_EXTERN_FUNC(sub_823DC2B0);

// The MenuBox selector — `sub_823DBB48(ctl, index)`: old selection to slide 1, new to slide 2,
// writes ctl+0. Keeps the guest's drawn selection in step with the host highlight.
PPC_EXTERN_FUNC(sub_823DBB48);

// The guest's directional stick router — held (0x1C..0x1F) and released (0x20..0x23). Both read
// the action code through their second argument (`switch (*a2)`) and never touch the first, so
// the host can drive them with a scratch word. They set the presentation attribute `String1` to
// "Up"/"Down"/"Left"/"Right" and run `"GUI:SetDirectionToHeld()"` /
// `"GUI:SetDirectionToReleased()"` on the context `sub_823DCBE0` picks; Lua polls that state in
// `g_Menu:onUpdate` and calls `ScrollUp` itself.
PPC_EXTERN_FUNC(sub_82404ED0);
PPC_EXTERN_FUNC(sub_82405030);

// The vtable+24 screen-input methods, per screen id. Named statically because the recompiler
// emits no indirect-call path; each is checked against its screen id (from the guest's `mgr+4`)
// before use.
PPC_EXTERN_FUNC(sub_826C2958);  // 14 abilities
PPC_EXTERN_FUNC(sub_826D2560);  // 15 items/weapons
PPC_EXTERN_FUNC(sub_826D5120);  // 16 clothing
PPC_EXTERN_FUNC(sub_826D96F0);  // 17 logbook
PPC_EXTERN_FUNC(sub_826DA228);  // 18 options
PPC_EXTERN_FUNC(sub_826A96B8);  // 21 crime
PPC_EXTERN_FUNC(sub_826AE760);  // 22 shop
PPC_EXTERN_FUNC(sub_826C63C8);  // 23 world map
PPC_EXTERN_FUNC(sub_826C4BC0);  // 25 gift
PPC_EXTERN_FUNC(sub_826B6D98);  // 26 selection
PPC_EXTERN_FUNC(sub_826B7770);  // 27 decoration
PPC_EXTERN_FUNC(sub_826CCBA8);  // 10 query screen (37 close, 38 rename)
PPC_EXTERN_FUNC(sub_826B2A18);  // 28 real estate (`*(0x820CEA58+24)`, read from the image)
PPC_EXTERN_FUNC(sub_826B9510);  // presentation string parameter setter (String1/String2)
PPC_EXTERN_FUNC(sub_8236F400);  // expression wheel: may open? (hero alive, mode 1/5/20/21/22)
PPC_EXTERN_FUNC(sub_8236F110);  // expression wheel: open (GameAction 40's body)
PPC_EXTERN_FUNC(sub_8236F340);  // expression wheel: close (GameAction 41's body)
PPC_EXTERN_FUNC(sub_826AA5D8);  // expression wheel: end a held expression (GameAction 25)
PPC_EXTERN_FUNC(sub_826AA230);  // expression wheel: accept (36) — enter a group / start the item
PPC_EXTERN_FUNC(sub_826AA510);  // expression wheel: back (37) — leave the group, or close at the top
PPC_EXTERN_FUNC(sub_826AA060);  // expression wheel: cursor from a stick vector (x, y)
PPC_EXTERN_FUNC(sub_82371860);  // GUI RegisterPlayer(GUI, player): ++GUI+0x65, binds the action consumer
PPC_EXTERN_FUNC(sub_823CDC08);  // 11 minigame completed (6)
PPC_EXTERN_FUNC(sub_823D3F98);  // 12 quest completion (6, player 1)

// The GUI input router — `(guiOwner, actionCode, pad)`. Its guard admits input while a
// query box is pending (`guiOwner+44` in {23,24,25,28}) and routes to the CURRENT screen
// handler's vtable+28; for the pause family that is `sub_826A2950`, which reloads +44 for
// both its switch and the callee (the pad argument is discarded) and answers in
// `sub_826A7738`: code 46 = confirm (save/quit per the box), 47 = cancel. The kick box
// (id 28) instead answers the generic accept/back codes 36/37 inline. Call the ROUTER, not the
// handler method, so the guest's own gate stays in the path.
PPC_EXTERN_FUNC(sub_8236E458);

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
PPC_EXTERN_FUNC(sub_82541F30);

// The Lua string runner, and the two LHString primitives that feed it.
//
// `sub_82426470(LHString* source, presentation)` either executes immediately — when
// `byte[GUI+100]` is set and the presentation's vtable+20 says it may — or queues the chunk at
// `GUI+88` for the next update. `sub_8222CED0(LHString* dest, const char* literal)` constructs
// one from a NUL-terminated guest string; `sub_82214DD8` releases it.
//
// It is silent on every failure path — a null context, a wrong context and a nil method are
// indistinguishable no-ops — so nothing is sent here without a read-back.
PPC_EXTERN_FUNC(sub_82426470);
PPC_EXTERN_FUNC(sub_8222CED0);

/// The abilities screen's event handler — `vtable+20` of the class `sub_826C1E80` builds.
/// Called by static dispatch (the recompiler emits no indirect-call path), but the live
/// object's slot is read and compared against this address first, so a screen whose slot holds
/// anything else is refused. Vtable `0x820CF4BC`: `+16` `0x826C23A8` (per-frame update), `+20`
/// `0x826C2660` (this), `+24` `0x826C2958` (ScreenAction).
PPC_EXTERN_FUNC(sub_82214DD8);
PPC_EXTERN_FUNC(sub_8223D320);   // GetMorality — the DISPLAYED value
PPC_EXTERN_FUNC(sub_8223D910);   // GetPurity   — the DISPLAYED value
PPC_EXTERN_FUNC(sub_821C7D30);   // GetRenown   — the DISPLAYED value
PPC_EXTERN_FUNC(sub_824C3570);   // appearance-modifier term, by kind
PPC_EXTERN_FUNC(sub_824C3AF8);   // preference term, by axis
PPC_EXTERN_FUNC(sub_824C3940);   // home bonus
PPC_EXTERN_FUNC(sub_824C4030);   // purity/corruption opinion
PPC_EXTERN_FUNC(sub_823C9000);   // region distance
PPC_EXTERN_FUNC(sub_823C8BE0);   // region travel time
PPC_EXTERN_FUNC(sub_823C6C18);   // region fine
PPC_EXTERN_FUNC(sub_823C6E50);   // region merchants
PPC_EXTERN_FUNC(sub_823C7030);   // region properties owned
PPC_EXTERN_FUNC(sub_823C7250);   // region chests
PPC_EXTERN_FUNC(sub_823C7450);   // region dig spots
PPC_EXTERN_FUNC(sub_823C7988);   // region dive spots
PPC_EXTERN_FUNC(sub_823C7690);   // region gargoyles
PPC_EXTERN_FUNC(sub_823C7BC8);   // region silver keys
PPC_EXTERN_FUNC(sub_8246FE20);   // region economy, as a STAR COUNT
PPC_EXTERN_FUNC(sub_826C2660);   // 14 abilities
PPC_EXTERN_FUNC(sub_826A26E8);   //  0 pause root
PPC_EXTERN_FUNC(sub_826D1800);   // 15 items
PPC_EXTERN_FUNC(sub_826D4D58);   // 16 clothing
PPC_EXTERN_FUNC(sub_826D9620);   // 17 logbook
PPC_EXTERN_FUNC(sub_826DA038);   // 18 options
PPC_EXTERN_FUNC(sub_826A92D8);   // 21 crime
PPC_EXTERN_FUNC(sub_826AE5C0);   // 22 shop
PPC_EXTERN_FUNC(sub_826C6110);   // 23 world map
PPC_EXTERN_FUNC(sub_826C45C8);   // 25 gift giving
PPC_EXTERN_FUNC(sub_826B65F0);   // 26 furniture, 27 decoration, 24 co-op — one folded body

// The presentation the GUI is currently driving — `sub_823DCBE0(*(GUI+56))`. Every attribute
// read and every Lua chunk in this file is scoped to whatever it returns.
PPC_EXTERN_FUNC(sub_823DCBE0);

// `sub_826B9210(LHString** name, int element)` -> float. Reads one float attribute off the
// Parameters element; `element == 0` means "resolve the current presentation yourself". Used
// here for exactly one thing: reading `EventQueueSize` back after a push.
PPC_EXTERN_FUNC(sub_826B9210);

// The matching writer — `sub_826B9440(f1 = value, r4 = LHString* name, r6 = presentation)`.
// Same element `sub_826B9210` reads from: presentation vtable slot 32 gives the parameter table
// at `pres+0x1EC`, and its own vtable slot 72 is the float setter. `r6 == 0` means "resolve the
// current presentation yourself"; `r3` and `r5` are read by neither. Called rather than poked:
// the setter also raises the dirty bit, marks the change list and runs the change callbacks.
PPC_EXTERN_FUNC(sub_826B9440);

namespace refii::vfs {

// The queue and the counters live in src/core/guest_tick_pump_state.cpp, which links into
// refii_vfs: the HOST posts and this side drains, so state beside the pump would be a copy the
// payload writes and the bridge never reads. Reached through the GuestTickPump* wrappers in
// vfs_hooks_private.h.

/// The front-end state object. `sub_826E0D60` dereferences it with NO null check —
/// `lwz r11, dword_83496AC0` then `lwz r9, 8(r11)` at 0x826E0E30 — so an action that runs
/// while the front end is down is a fault, not a no-op.
constexpr uint32_t kFrontEndState = 0x83496AC0u;

/// The signed-in user index, and the mask of which pads are signed in. Read exactly as the
/// guest's own New Game guard reads them at 0x826E0064/0x826E0068.
constexpr uint32_t kSignedInUserIndex = 0x83496AF8u;
constexpr uint32_t kSignedInUserMask = 0x83496A70u;

/// saveMgr = *(*(kFrontEndState + 0xC) + 0x28), per 0x826E0084..0x826E008C.
constexpr uint32_t kGameStateOffset = 0x0Cu;
constexpr uint32_t kSaveManagerOffset = 0x28u;

/// The interned `BOY_SELECTED` / `GIRL_SELECTED` event ids, interned at static init as
/// `sub_82189140(literal) & 0x7FFFFFFF`. Read the id the guest computed; never recompute it.
constexpr uint32_t kEventBoySelected = 0x8349B5A0u;
constexpr uint32_t kEventGirlSelected = 0x8349B5A4u;

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

  sub_82441CB8(ctx, base);

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

  sub_826E0D60(ctx, base);

  ctx = saved;

  // A game just started, so the saved-game count is stale; re-sampled when the front end is up.
  GuestTickPumpRequestSaveCount();
  return true;
}

/// The GUI object, and the main-menu screen hanging off it at +184. The vtable check is
/// load-bearing: `dword_83496B08` has two writers through the same base ctor, and once the game
/// has taken over it holds a BASE-ONLY object whose +184 is not a screen pointer.
constexpr uint32_t kGuiObject = 0x83496B08u;
constexpr uint32_t kFrontEndGuiVtable = 0x820A924Cu;
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
  sub_82443308(ctx, base);
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
constexpr uint32_t kSaveNamesBegin = 84u;
constexpr uint32_t kSaveNamesEnd = 88u;
constexpr uint32_t kSaveRecordsBegin = 148u;
constexpr uint32_t kSaveRecordStride = 40u;
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
  const uint32_t screen = MainMenuScreen(base);
  if (screen == 0) {
    return false;
  }

  const uint32_t begin = LoadGuestU32(base, screen + kSaveNamesBegin);
  const uint32_t end = LoadGuestU32(base, screen + kSaveNamesEnd);
  const uint32_t records = LoadGuestU32(base, screen + kSaveRecordsBegin);
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

  const uint32_t record = records + (index * kSaveRecordStride);
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

  sub_82379838(ctx, base);

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

/// "RegisterPlayer" — the guest's own `sub_82371860(GUI, player)`, run only while the in-game
/// GUI reports NO registered player (`*(u8*)(GUI+0x65) == 0`).
///
/// That byte gates the whole Gameface update: `sub_821A1140` advances every presentation's
/// timeline and drains the event queue only while `byte_83319409 && *(GUI+0x65)`. The guest
/// increments it from `sub_82314D30`, which also binds the player into the action consumer
/// (`sub_825E8DE0`/`sub_823D2508`) — the bound player at consumer+12 the GameAction chain needs.
constexpr const char* kActionRegisterPlayer = "RegisterPlayer";

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

/// "LoadSave:<index>" — pick a save out of the list the guest enumerated. An index, not a name:
/// the guest identifies a save by the ADDRESS of its handle slot inside its own vector, and the
/// index is the join it uses itself (`this+72`, moved by its own left/right handler).
constexpr const char* kActionLoadSavePrefix = "LoadSave:";

bool DispatchByName(PPCContext& ctx, uint8_t* base, const std::string& name,
                    uint32_t command = kCommandNone, uint32_t param = 0u) {
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

  sub_826DFD58(ctx, base);

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
  constexpr uint32_t kInGameGuiVtable = 0x82001610u;
  constexpr uint32_t kPauseHandlerVtable = 0x820CDE18u;
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
  sub_8236FFC0(ctx, base);
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

  sub_826A26E8(ctx, base);

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
  constexpr uint32_t kInGameGuiVtable = 0x82001610u;
  constexpr uint32_t kReceiveItemsVtable = 0x820C0E08u;
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
      LoadGuestU32(base, manager + 4u) != kReceiveItemsScreen ||
      LoadGuestU32(base, owner + 40u) != 1u) {
    REXLOG_INFO("Refii guest-tick pump: ReceiveItemsAction:{} off the card screen — dropped",
                code);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = kReceiveItemsScreen;
  sub_8236FFC0(ctx, base);
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

  sub_82537128(ctx, base);

  ctx = saved;
  return true;
}

/// The MenuBox's controller, or 0 when no box is open.
///
/// `ctl = *(guiOwner+56)` — a plain struct, no vtable — and `ctl+12` is its open flag. A
/// Lua-opened list (DisplayMenuBox) that can stand over any screen, so there is no modal latch
/// to gate on.
uint32_t MenuBoxController(uint8_t* base) {
  constexpr uint32_t kInGameGuiVtable = 0x82001610u;
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
  sub_823DBB48(ctx, base);
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
    sub_823DC2B0(ctx, base);
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
  sub_823DC720(ctx, base);
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

  // The box currently SHOWING sits at `owner+52`; `sub_822A5C50` reads the same word as its
  // first test of whether any box is up.
  const uint32_t box = LoadGuestU32(base, owner + 52u);
  if (!plausible(box)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box up — dropped", code);
    return true;
  }

  const uint32_t vtable = LoadGuestU32(base, box);
  const PPCContext saved = ctx;
  ctx.r3.u32 = box;
  ctx.r4.u32 = code;
  ctx.r5.u32 = 1u;  // player 1, the same index the screen dispatch uses

  switch (vtable) {
    case 0x820AE688u: sub_823CF3B0(ctx, base); break;  // QueryBox / YesNo
    case 0x820AE6A8u: sub_823CF8A8(ctx, base); break;  // NumericAdjuster
    case 0x820AE6C8u: sub_823D0090(ctx, base); break;  // QuestBoxParams
    case 0x820AE668u: sub_823CEDD8(ctx, base); break;  // MessageBox / Download / Divorce
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

/// The in-game GUI's roots, or false when no in-game screen is up.
bool InGameScreenRoots(uint8_t* base, uint32_t* out_owner, uint32_t* out_screen_id) {
  constexpr uint32_t kInGameGuiVtable = 0x82001610u;
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
  if (out_screen_id != nullptr) *out_screen_id = LoadGuestU32(base, manager + 4u);
  return true;
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
    sub_82404ED0(ctx, base);
  } else {
    sub_82405030(ctx, base);
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
  sub_8222CED0(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = 0u;      // `this` — read by nothing on this path
  work.r4.u32 = handle;  // the name
  work.r5.u32 = 0u;      // read by nothing
  work.r6.u32 = 0u;      // 0 = whichever presentation the GUI is currently driving
  work.f1.f64 = at_root ? 1.0 : 0.0;
  sub_826B9440(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  sub_82214DD8(work, base);

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
  ctx.r4.u32 = screen_id;
  sub_8236FFC0(ctx, base);
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
    case 14u: sub_826C2958(ctx, base); break;
    case 15u: sub_826D2560(ctx, base); break;
    case 16u: sub_826D5120(ctx, base); break;
    case 17u: sub_826D96F0(ctx, base); break;
    case 18u: sub_826DA228(ctx, base); break;
    case 21u: sub_826A96B8(ctx, base); break;
    case 22u: sub_826AE760(ctx, base); break;
    case 23u: sub_826C63C8(ctx, base); break;
    case 25u: sub_826C4BC0(ctx, base); break;
    case 26u: sub_826B6D98(ctx, base); break;
    case 27u: sub_826B7770(ctx, base); break;
    case 10u: sub_826CCBA8(ctx, base); break;
    case 28u: sub_826B2A18(ctx, base); break;
    case 11u: sub_823CDC08(ctx, base); break;
    case 12u: sub_823D3F98(ctx, base); break;
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
  constexpr uint32_t kGameRoot = 0x83496AB8u;

  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  if (slot >= kSlots || spell >= kSpells) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{} is out of range — dropped", slot,
                spell);
    return true;
  }

  const uint32_t game = LoadGuestU32(base, kGameRoot + 12u);
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

  sub_82541F30(ctx, base);

  ctx = saved;
  return true;
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
  sub_823DCBE0(ctx, base);
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
  sub_8222CED0(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = presentation;
  step("push");
  sub_82426470(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  step("LHString dtor");
  sub_82214DD8(work, base);

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
  sub_8222CED0(work, base);

  // The presentation is NOT optional here. `sub_826B9510`/`sub_826B9440` fall back to
  // `sub_823DCBE0(*(GUI+56))` when handed 0; this getter dereferences it unconditionally
  // (`lwz r11, 0(r29)` / `lwz r10, 0x38(r11)` / `bctrl` at 0x826B9228), so 0 faults.
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t stack = plausible(gui) ? LoadGuestU32(base, gui + 56u) : 0u;
  if (!plausible(stack)) {
    work.r1.u32 = frame;
    work.r3.u32 = handle;
    sub_82214DD8(work, base);
    return -1;
  }
  work.r1.u32 = frame;
  work.r3.u32 = stack;
  sub_823DCBE0(work, base);
  const uint32_t presentation = work.r3.u32;
  if (!plausible(presentation)) {
    work.r1.u32 = frame;
    work.r3.u32 = handle;
    sub_82214DD8(work, base);
    return -1;
  }

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = presentation;
  sub_826B9210(work, base);
  const double depth = work.f1.f64;

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  sub_82214DD8(work, base);

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
  sub_8222CED0(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = 0u;
  work.r4.u32 = handle;
  work.r5.u32 = 0u;
  work.r6.u32 = 0u;
  work.f1.f64 = value;
  sub_826B9440(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = handle;
  sub_82214DD8(work, base);
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
  sub_8222CED0(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = valueHandle;
  work.r4.u32 = valueText;
  work.r5.u32 = 0xFFFFFFFFu;  // the length, read by sub_822078A0: -1 = strlen. Must be set.
  sub_8222CED0(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = 0u;
  work.r4.u32 = nameHandle;
  work.r5.u32 = valueHandle;
  work.r6.u32 = 0u;
  sub_826B9510(work, base);

  work.r1.u32 = frame;
  work.r3.u32 = valueHandle;
  sub_82214DD8(work, base);
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  sub_82214DD8(work, base);
}

uint32_t PlayerObject(uint8_t* base);

/// See kActionRegisterPlayer.
bool DispatchRegisterPlayer(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t player = PlayerObject(base);
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
  sub_82371860(ctx, base);
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
  const uint32_t player = PlayerObject(base);
  if (!plausible(quick) || !plausible(wheel) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} with no quick menu / player — dropped", verb);
    return true;
  }

  const bool open = LoadGuestU8(base, quick + 35u) != 0u;
  const PPCContext saved = ctx;
  if (verb == "open") {
    if (open) {
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    sub_8236F400(ctx, base);
    const bool may = (ctx.r3.u32 & 0xFFu) != 0u;
    if (may) {
      ctx = saved;
      ctx.r3.u32 = quick;
      ctx.r4.u32 = player;
      sub_8236F110(ctx, base);
    }

    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:open — {}",
                may ? (LoadGuestU8(base, quick + 35u) != 0u ? "opened" : "refused by sub_8236F110")
                    : "refused by sub_8236F400 (hero busy or not in a walking mode)");
    return true;
  }

  if (verb == "close") {
    if (open) {
      ctx.r3.u32 = quick;
      ctx.r4.u32 = 1u;
      sub_8236F340(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:close — closed");
    }

    return true;
  }

  if (verb == "select") {
    if (open) {
      ctx.r3.u32 = wheel;
      ctx.r4.u32 = 1u;  // primary player
      sub_826AA230(ctx, base);
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
      sub_826AA510(ctx, base);
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
      sub_826AA5D8(ctx, base);
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
      sub_826AA060(ctx, base);
      ctx = saved;
    }

    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} is not a verb — dropped", verb);
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

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id)) {
    return false;
  }

  PPCContext probe = ctx;
  probe.r3.u32 = owner;
  probe.r4.u32 = screen_id;
  sub_8236FFC0(probe, base);
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
  if (slot == 0x826B65F0u && (screen_id == 26u || screen_id == 27u)) {
    return CommitThroughParameters(ctx, base, screen_id, name, type, id);
  }

  switch (slot) {
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
  sub_8222CED0(work, base);

  // After the constructor: it owns `+0` only.
  StoreGuestU32(base, event + 4u, type);
  StoreGuestU32(base, event + 8u, id);

  REXLOG_INFO("Refii guest-tick pump: dispatching '{}' (type {}, id 0x{:08X}) straight to "
              "screen {}'s handler 0x{:08X}", name, type, id, screen_id, slot);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handler;
  work.r4.u32 = event;
  switch (slot) {
    case 0x826C2660u: sub_826C2660(work, base); break;
    case 0x826A26E8u: sub_826A26E8(work, base); break;
    case 0x826D1800u: sub_826D1800(work, base); break;
    case 0x826D4D58u: sub_826D4D58(work, base); break;
    case 0x826D9620u: sub_826D9620(work, base); break;
    case 0x826DA038u: sub_826DA038(work, base); break;
    case 0x826A92D8u: sub_826A92D8(work, base); break;
    case 0x826AE5C0u: sub_826AE5C0(work, base); break;
    case 0x826C6110u: sub_826C6110(work, base); break;
    case 0x826C45C8u: sub_826C45C8(work, base); break;
    case 0x826B65F0u: sub_826B65F0(work, base); break;
    default: break;  // unreachable: the switch above already refused anything else
  }

  REXLOG_INFO("Refii guest-tick pump: '{}' RETURNED from the screen handler", name);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = event;
  sub_82214DD8(work, base);
  return true;
}

/// The hero entity, or 0 — the walk `DispatchSpellAssign` already does, named once.
uint32_t HeroEntity(uint8_t* base) {
  constexpr uint32_t kGameRoot = 0x83496AB8u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t game = LoadGuestU32(base, kGameRoot + 12u);
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t roster = plausible(party) ? LoadGuestU32(base, party) : 0u;
  const uint32_t entry = plausible(roster) ? LoadGuestU32(base, roster + 108u) : 0u;
  const uint32_t hero = plausible(entry) ? LoadGuestU32(base, entry) : 0u;

  if (!plausible(hero) || (LoadGuestU8(base, hero + 144u) & 0x40u) == 0u ||
      (LoadGuestU8(base, hero + 40u) & 0x01u) == 0u) {
    return 0u;
  }

  return hero;
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
  constexpr uint32_t kGameRoot = 0x83496AB8u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t game = LoadGuestU32(base, kGameRoot + 12u);
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
  constexpr uint32_t kTableLoaded = 0x83496C19u;
  constexpr uint32_t kTable = 0x833192A4u;

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
void SampleWorldMapRegion(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kWorldMapScreenId = 23u;
  constexpr uint32_t kGameRoot = 0x83496AB8u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t owner = 0;
  uint32_t screen_id = 0;
  if (!InGameScreenRoots(base, &owner, &screen_id) || screen_id != kWorldMapScreenId) {
    return;
  }

  // party -> region manager -> the level list, exactly the chain `sub_823C9000` walks.
  const uint32_t game = LoadGuestU32(base, kGameRoot + 12u);
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t regions = plausible(party) ? LoadGuestU32(base, party + 8u) : 0u;
  const uint32_t inner = plausible(regions) ? LoadGuestU32(base, regions + 4u) : 0u;
  const uint32_t list = plausible(inner) ? LoadGuestU32(base, inner + 12u) : 0u;
  if (!plausible(list)) {
    return;
  }

  // `sub_82518330` walks [list+8, list+12) with stride 8 keeping non-null first dwords; walked
  // here instead so no vector is allocated.
  const uint32_t begin = LoadGuestU32(base, list + 8u);
  const uint32_t end = LoadGuestU32(base, list + 12u);
  if (!plausible(begin) || end <= begin || (end - begin) > 0x4000u) {
    return;
  }

  WorldMapRegion region;
  uint32_t wanted = RequestedWorldMapRegion();
  uint32_t seen = 0;
  uint32_t name_handle = 0;
  for (uint32_t at = begin; at < end; at += 8u) {
    const uint32_t entry = LoadGuestU32(base, at);
    if (!plausible(entry)) {
      continue;
    }

    if (seen == wanted) {
      name_handle = at;  // the HANDLE's address, which is what an LHString* argument is
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

  // Claimed by lowering r1 so a callee's frame cannot land on the string it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t out = frame + 0x20u;
  const PPCContext saved = ctx;

  const auto provider = [&](void (*call)(PPCContext&, uint8_t*), char* into, std::size_t cap) {
    StoreGuestU32(base, out, 0u);

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    ctx.r4.u32 = name_handle;
    ctx.r5.u32 = 0u;  // travel time's "emit 0 hours" flag (sub_823C8CE0 via r5); the others ignore r5
    call(ctx, base);

    const std::string value = ReadGuestLhString(base, out);
    std::snprintf(into, cap, "%s", value.c_str());

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = out;
    sub_82214DD8(ctx, base);
    ctx = saved;
  };

  provider(sub_823C9000, region.distance, sizeof(region.distance));
  provider(sub_823C8BE0, region.travel, sizeof(region.travel));
  provider(sub_823C6C18, region.fine, sizeof(region.fine));
  provider(sub_823C7030, region.owned, sizeof(region.owned));
  provider(sub_823C7250, region.chests, sizeof(region.chests));
  provider(sub_823C7450, region.digspots, sizeof(region.digspots));
  provider(sub_823C7988, region.divespots, sizeof(region.divespots));
  provider(sub_823C7690, region.gargoyles, sizeof(region.gargoyles));
  provider(sub_823C7BC8, region.silver_keys, sizeof(region.silver_keys));
  provider(sub_823C6E50, region.merchants, sizeof(region.merchants));

  // The economy is a STAR COUNT, not a string, so it cannot go through `provider`:
  // `sub_8246FE20` takes the region name in r3 and RETURNS the rating there (`bl` at
  // 0x829AB364, then `addi r5, r3, 1` to make it a slide index). It must be called — there is
  // no per-region rating field; the number is averaged over the region's villages on every ask.
  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = name_handle;
  sub_8246FE20(ctx, base);
  region.stars = static_cast<int32_t>(ctx.r3.u32);
  ctx = saved;

  PublishWorldMapRegion(region);
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
    sub_824C3570(ctx, base);
    return static_cast<float>(ctx.f1.f64);
  };

  // `sub_824C3AF8(c15, axis)` — one preference axis.
  const auto preference = [&](uint32_t axis) {
    ctx = saved;
    ctx.r3.u32 = look;
    ctx.r4.u32 = axis;
    sub_824C3AF8(ctx, base);
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
  sub_824C4030(ctx, base);
  sampled.attractiveness[6] = static_cast<float>(ctx.f1.f64);   // purity/corruption

  sampled.attractiveness[7] = preference(3u);  // muscle
  sampled.attractiveness[8] = preference(1u);  // fatness
  sampled.attractiveness[9] = preference(4u);  // scars

  ctx = saved;
  ctx.r3.u32 = look;
  sub_824C3940(ctx, base);
  sampled.attractiveness[10] = static_cast<float>(ctx.f1.f64);  // home bonus

  ctx = saved;
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
  sub_8223D320(ctx, base);
  sampled.morality = static_cast<float>(ctx.f1.f64);

  ctx = saved;
  ctx.r3.u32 = stats;
  sub_8223D910(ctx, base);
  sampled.purity = static_cast<float>(ctx.f1.f64);

  ctx = saved;
  ctx.r3.u32 = stats;
  sub_821C7D30(ctx, base);
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
  }

  PublishHeroDerived(sampled);
}

/// Commits a menu row by identity — see kActionMenuEventPrefix.
bool DispatchMenuEvent(PPCContext& ctx, uint8_t* base, const std::string& name, uint32_t type,
                       uint32_t id) {
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
      handler_ctx.r4.u32 = screen_id;
      sub_8236FFC0(handler_ctx, base);
      const uint32_t handler = handler_ctx.r3.u32;
      if (handler >= 0x10000u && handler < 0xC0000000u) {
        StoreGuestU8(base, handler + 198u, 1u);
        REXLOG_INFO("Refii guest-tick pump: armed abilities handler 0x{:08X}+198 = 1 before "
                    "'{}' — an identity push with it clear is the QUERY arm, not the purchase",
                    handler, name);
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
  constexpr uint32_t kInGameGuiVtable = 0x82001610u;
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

  sub_8236E458(ctx, base);

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
    } else if (name.rfind(kActionGuiLuaPrefix, 0) == 0) {
      const std::string chunk = name.substr(std::strlen(kActionGuiLuaPrefix));
      ran = RunGuestLua(ctx, base, chunk);
      REXLOG_INFO("Refii guest-tick pump: GuiLua probe {} ({} bytes)",
                  ran ? "ran" : "had no live presentation", chunk.size());
      ran = true;
    } else if (name.rfind(kActionMenuEventPrefix, 0) == 0) {
      // `MenuEvent:<name>:<type>:<id>` — the name may itself contain no colon, so the two
      // numbers are taken from the END and everything before them is the name.
      const std::string body = name.substr(std::strlen(kActionMenuEventPrefix));
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
                              static_cast<uint32_t>(type), static_cast<uint32_t>(id));
    } else if (name.rfind(kActionQuickWheelPrefix, 0) == 0) {
      ran = DispatchQuickWheel(ctx, base, name.substr(std::strlen(kActionQuickWheelPrefix)));
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
      const std::string number = name.substr(std::strlen(kActionBoxActionPrefix));
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

      ran = DispatchDisplayBoxAction(ctx, base, static_cast<uint32_t>(value));
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
  uint32_t at = LoadGuestU32(base, 0x83496AB8u);
  at = step(at, 0x0Cu);
  at = step(at, 0x58u);
  at = step(at, 0x04u);
  at = step(at, 0x00u);
  return LooksLikeGuestPointer(at) && LoadGuestU8(base, at + 0xA8u) != 0;
}

/// The screen already brought down, so the finish fires once per load. Cleared when no screen
/// is up, which re-arms it for the next level change.
std::atomic<uint32_t> g_finished_loading_screen{0};

/// Bring the IN-GAME loading screen down — the host standing in for the present loop that would.
///
/// Nothing presents in this headless mode, so after an in-game level load the guest never runs
/// the render tick that fades its loading screen out and releases the game thread; it sits at
/// `fade=1 requested=0` with the loading music playing. The startup screen is finished by the
/// frontend bridge (`TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline`); this is the same
/// move for the in-game one, from the in-game tick (`sub_82276D10` -> the pump).
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
    g_finished_loading_screen.store(0, std::memory_order_relaxed);
    return;
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
    signal_ctx.lr = 0x8235E7D8u;
    ::__imp__sub_822C9388(signal_ctx, base);
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

  const uint32_t mode = LoadGuestU32(base, 0x83496AB8u);
  if (!LooksLikeGuestPointer(mode)) {
    return;
  }
  const uint32_t game = LoadGuestU32(base, mode + 0x0Cu);
  if (!LooksLikeGuestPointer(game)) {
    return;
  }

  // Only when the guest says the world is ready and the flag is clear — writing every tick
  // would mask the guest setting it.
  if (LoadGuestU8(base, game + 0x105u) == 0 || LoadGuestU8(base, game + 0x104u) != 0) {
    return;
  }

  StoreGuestU8(base, game + 0x104u, 1u);

  static std::atomic<uint32_t> armed{0};
  if (armed.fetch_add(1, std::memory_order_relaxed) < 4u) {
    REXLOG_INFO("Refii in-game simulation armed: game 0x{:08X} +0x104 0 -> 1 (+0x105={}), the "
                "game tick can now run the entity/animation/camera block",
                game, LoadGuestU8(base, game + 0x105u));
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

  // The menu capture settles here: a population runs to completion inside the GUI update that
  // began it, so by the next tick the captured rows are whole. There is no end marker to use
  // instead — the in-game lists never call `UpdateAfterSpawning`.
  refii_bridge_menu_settle();

  DrainOnGuestThread(ctx, base);

  // The hero sample, on a cadence — see SampleHeroDerived.
  if ((tick % 15u) == 0u) {
    SampleHeroDerived(ctx, base);
  }

  // The present loop that would normally fade the in-game loading screen out is off in this
  // headless mode, so the guest never releases the game thread from it. Do it here, from the
  // in-game tick, once the world it was loading is up.
  MaybeFinishInGameLoadingScreen(ctx, base);

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
    __imp__sub_82382558(subtitles_ctx, base);
    refii_bridge_front_end_note_subtitles(static_cast<int32_t>(subtitles_ctx.r3.u32));
  }
}

/// Reads a NUL-terminated guest string through an LHString handle.
///
/// The handle is ONE DWORD holding the character pointer — `sub_8222CED0(&h, literal)` fills it
/// and `sub_82370450` compares two of them with a plain `v8[0] == v7[0]`. Callers pass the
/// ADDRESS of that dword, so this is two loads, not one.
/// Reads a NUL-terminated guest string at an address, or empty when it is not text.
///
/// Tests for TEXT, not merely readable memory: a guest pointer read as characters is four
/// non-zero bytes and looks like a string to a loop that only stops at NUL, so the bytes must
/// be PRINTABLE — unprintables mean this was another pointer, not the string.
std::string ReadGuestChars(uint8_t* base, uint32_t at) {
  if (at < 0x10000u || at >= 0xC0000000u) {
    return std::string();
  }

  std::string out;
  for (uint32_t i = 0; i < 128u; ++i) {
    const uint8_t c = LoadGuestU8(base, at + i);
    if (c == 0) {
      break;
    }

    if (c < 0x20u || c > 0x7Eu) {
      return std::string();
    }

    out.push_back(static_cast<char>(c));
  }

  return out;
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

REX_EXTERN(sub_822C1BD0) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnGuestTick(ctx, base);

  __imp__sub_822C1BD0(ctx, base);
}

// The spawn, captured — the IN-GAME one, against GUI manager `dword_83496B08`.
REX_EXTERN(sub_826CE8D0) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnMenuSpawn(ctx, base, /*stack_rating=*/true);

  __imp__sub_826CE8D0(ctx, base);
}

// And the front end's — a second function with the same body against a different GUI manager
// (`dword_83496AC0`). These are the only two emitters of `g_Menu:SpawnButton()` in the image.
// Its `Float4` is hardcoded to 0.0: no front-end row has stars.
REX_EXTERN(sub_826E4798) {
  REX_FUNC_PROLOGUE();

  // EIGHT ARGUMENTS, NO STACK RATING — this emitter hard-codes Float4 = 0.0. See OnMenuSpawn.
  refii::vfs::OnMenuSpawn(ctx, base, /*stack_rating=*/false);

  __imp__sub_826E4798(ctx, base);
}

// The list boundary. `sub_826CE110` runs `"g_Menu:DeleteAllSpawnedMenuItems()"` and takes no
// arguments; it is the one marker both the in-game and front-end populations share. The END is
// not markable — only front-end lists call `UpdateAfterSpawning` — so the capture publishes per
// row rather than waiting for one.
REX_EXTERN(sub_826CE110) {
  REX_FUNC_PROLOGUE();

  refii_bridge_menu_spawn_begin();

  __imp__sub_826CE110(ctx, base);
}

// The other six boundaries — populators that fire `DeleteAllSpawnedMenuItems` INLINE rather
// than through the wrapper above: world map sub_829A2F40 @0x829a2fc8, abilities sub_829A45A0,
// shop sub_82994218, logbook sub_829B8648, gift-gold sub_82430468, front end sub_826E1F70.
// Without them a screen's rows join onto the previous population's. The string match in the
// exec hook (resource_manager_open.cpp) is the net for any emitter not listed here.
#define REFII_MENU_BOUNDARY_HOOK(fn)      \
  REX_EXTERN(fn) {                        \
    REX_FUNC_PROLOGUE();                  \
    refii_bridge_menu_spawn_begin();      \
    __imp__##fn(ctx, base);               \
  }

REFII_MENU_BOUNDARY_HOOK(sub_829A2F40)
REFII_MENU_BOUNDARY_HOOK(sub_829A45A0)
REFII_MENU_BOUNDARY_HOOK(sub_82994218)
REFII_MENU_BOUNDARY_HOOK(sub_829B8648)
REFII_MENU_BOUNDARY_HOOK(sub_82430468)
REFII_MENU_BOUNDARY_HOOK(sub_826E1F70)

#undef REFII_MENU_BOUNDARY_HOOK
