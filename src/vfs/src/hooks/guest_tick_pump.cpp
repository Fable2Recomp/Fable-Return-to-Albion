// refii — the guest tick pump
//
// A place for the HOST to run work ON THE GUEST'S OWN TICK THREAD.
//
// WHY THIS HAS TO EXIST. Godot owns the front end now: our item list, our highlight, our
// navigation. Making an entry DO something means the guest has to act, and every route to
// that is a guest call. Everything the bridge has ever done is a memory read or write issued
// from Godot's thread — that distinction is the property the whole hosted arrangement has
// relied on, and the first cross-thread guest call would end it. A call that works nine times
// in ten is worse than the feature being absent, because the tenth is a corrupted front end
// that looks like a rendering bug.
//
// So the host does not call the guest. It POSTS, and the guest picks the work up itself, on
// its own thread, at a point in its own tick where its state is coherent.
//
// THE SITE, and why this one.
//
// `sub_822C1BD0` is a seven-instruction thunk that forwards through `[this+0x24]`. It is
// reached only through slot 5 (offset 0x14) of the vtable on `dword_8349E9EC`, and the image
// loads that slot at exactly three places — one per tick loop:
//
//     0x82377B68  in sub_82377A00   boot movies, 60 Hz
//     0x82319E88  in sub_82319BE8   front end,   30 Hz  (dbl_83319630)
//     0x82277190  in sub_82276D10   in game,     15 Hz  (dbl_83319668)
//
// `xrefs_to 0x822C1BD0` returns ONE reference and it is data (`0x8200282C`, a vtable slot),
// so there is no direct caller anywhere and the three sites above are the complete reachable
// set. That is what makes it the right pump: exactly once per tick, in every state, and
// nowhere else. The two obvious alternatives are both worse — `sub_822F4CE8` is a STATE
// machine that iterates about three times in a session, not a frame loop, and `sub_82379170`
// (the GUI's own update) stops firing the moment the game leaves the front end.
//
// THE THREAD. It is not the process's initial thread. `sub_822EA1C0` spawns the worker and
// then parks:
//
//     822ea1f4  addi r3, r10, sub_822EA228@l
//     822ea1f8  bl   beginthread
//     822ea1fc  li   r4, -1
//     822ea204  bl   sub_821969C8          ; WaitForSingleObjectEx(h, INFINITE, 0)
//
// so the initial thread is blocked for the entire run and the tick lives on the spawned
// thread, which names itself "GameThread" and pins itself to hardware thread 0. A pump on the
// initial thread would pump a thread that is asleep.
//
// IT MUST TOLERATE AN EMPTY WORLD. This fires during the boot movies, before the front-end
// state exists — `dword_83496AC0` is written only when that state is constructed — so every
// pointer this reads is checked, and an action that cannot run yet stays queued rather than
// being dropped or forced.
//
// THE ACTIONS it carries are named strings, matched in `RunPendingActions` below. An action
// the pump does not know is dropped and logged rather than guessed at.

#include "vfs_hooks_private.h"

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

// The New Game commit. NOT hooked — we call the guest's own function, so this is the plain
// symbol rather than an `__imp__` original.
PPC_EXTERN_FUNC(sub_826E0D60);

// The front end's own menu-action dispatcher, vtable slot +16 of 0x820D0934. Called directly
// rather than through the vtable: the slot is fixed, and reading a vtable to reach a function
// we already know the address of is indirection with nothing on the other end of it.
PPC_EXTERN_FUNC(sub_826DFD58);

// "How many saved games does the signed-in user have?" — the guest's own spawn-time probe.
//
// THIS IS THE ONE THE FRONT END USES, and picking it over its two siblings is the whole point:
//
//   sub_82443308  no parameters, no device, no overlapped structure. Enumerates content type 1
//                 for user `dword_83496AF8` with DeviceID 0 — ALL devices — and returns the
//                 item count. Closes its handle unconditionally.
//   sub_824431E8  same enumeration, but reads a DeviceID out of the save manager's map at
//                 `saveMgr+0x7EC`, and returns 0 when that map has no entry.
//   sub_82443048  same again, and fills a vector of save NAMES.
//
// The last two are useless to a host poll, because the map they read is only ever written by
// `sub_82441CB8` — which calls `j_XamShowDeviceSelectorUI` and blocks on
// `XGetOverlappedResult(..., bWait=1)`. Asking them before the user has been through a device
// picker returns a confident zero. `sub_82443308` asks the question directly and is why the
// guest's own Load Game guard uses it rather than the vector it fills later.
PPC_EXTERN_FUNC(sub_82443308);

// Frontend_SetStartGameTarget — the LOAD commit, and it commits by itself.
//
// Verified in its own decompile rather than taken second hand, because this one starts a level
// load and a wrong argument is worse than a blank card:
//
//   823798ac  sub_822651A0(&g_PendingLoadSaveName, a2)   ; a2 = the save NAME
//   8237989c  sub_822651A0(&g_StartLevelName,      a3)   ; a3 = the LEVEL to load
//   823798e0  *(*(dword_83496AC0 + 12) + 92) = 1         ; the run loop's exit flag
//   823798e8  sub_82319928(*(dword_83496AC0 + 12))       ; builds the next game state
//
// BOTH ARGUMENTS ARE HANDLE SLOTS, NOT STRINGS. `sub_822651A0` copies a 4-byte handle and bumps
// the refcount at `*handle + 12`, so it must be handed the ADDRESS of a live handle — pointing
// it at character data would refcount whatever the first word of the text happened to be. `a1`
// is never referenced in the body.
//
// It also dereferences `dword_83496AC0 + 12` with NO null check, so it is only safe while the
// front end is up — the same precondition StartNewGame already enforces.
PPC_EXTERN_FUNC(sub_82379838);

namespace refii::vfs {

namespace {

/// One piece of posted work. `name` is the front end's own identity for a menu entry — the
/// guest dispatches on the NAME STRING, a strcmp chain, not on an index or a table, which is
/// what lets a host-authored entry name a guest action at all.
struct PendingAction {
  std::string name;
};

std::mutex g_mutex;
std::vector<PendingAction> g_pending;

/// Ticks observed, for the diagnostic below. Relaxed: it is a counter nobody makes a decision
/// on, and making it ordered would put a barrier in a function that runs every frame.
std::atomic<uint64_t> g_ticks{0};

/// Actions taken off the queue. Separate from ticks so "the pump never ran" and "the pump ran
/// and the queue was always empty" are distinguishable — a single number cannot tell those
/// apart, and they need completely different fixes.
std::atomic<uint64_t> g_drained{0};

/// The saved-game count, sampled by calling the guest's own probe on this thread.
///
/// -1 means "not asked yet" and is NOT the same as 0. A host that cannot tell them apart draws
/// a menu with Load Game missing and no way to know whether that is the answer or the absence
/// of one — which is exactly the bug this replaces, where an empty vector stood in for both.
std::atomic<int32_t> g_save_count{-1};

/// Set to ask for a fresh sample. Starts true so the first front-end tick takes one, and is set
/// again whenever the host knows the answer has changed — after a game is started, say.
///
/// Sampled rather than polled every tick because the probe is not free: it allocates a 46 320
/// byte guest stack frame and walks up to 150 content entries. The guest itself asks exactly
/// once, at menu spawn.
std::atomic<bool> g_resample{true};

}  // namespace

/// Queues work to run on the guest's tick thread. Safe from any thread; returns the queue
/// depth after the post, so a caller can notice a pump that has stopped draining.
uint32_t PostGuestTickAction(const char* name) {
  if (name == nullptr || name[0] == '\0') {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_mutex);
  g_pending.push_back(PendingAction{std::string(name)});
  return static_cast<uint32_t>(g_pending.size());
}

/// Ticks seen and actions drained, for the host's diagnostics.
void GuestTickPumpCounters(uint64_t* out_ticks, uint64_t* out_drained,
                           uint32_t* out_queued) {
  if (out_ticks != nullptr) {
    *out_ticks = g_ticks.load(std::memory_order_relaxed);
  }
  if (out_drained != nullptr) {
    *out_drained = g_drained.load(std::memory_order_relaxed);
  }
  if (out_queued != nullptr) {
    std::lock_guard<std::mutex> lock(g_mutex);
    *out_queued = static_cast<uint32_t>(g_pending.size());
  }
}

/// The saved-game count as last sampled, or -1 if it has not been sampled yet.
int32_t GuestSavedGameCount() {
  return g_save_count.load(std::memory_order_relaxed);
}

/// Asks for a fresh sample on the next front-end tick. Safe from any thread.
void RefreshGuestSavedGameCount() {
  g_resample.store(true, std::memory_order_relaxed);
}

/// The front-end state object. `sub_826E0D60` dereferences it with NO null check —
/// `lwz r11, dword_83496AC0` then `lwz r9, 8(r11)` at 0x826E0E30 — so an action that runs
/// while the front end is down is a fault, not a no-op.
constexpr uint32_t kFrontEndState = 0x83496AC0u;

/// The interned `BOY_SELECTED` / `GIRL_SELECTED` event ids.
///
/// Interned at static init as `sub_82189140(literal) & 0x7FFFFFFF` and stored here, so the
/// host READS the id the guest computed rather than recomputing the hash. That is the
/// difference between following the guest's contract and reimplementing it — a hash we
/// derived ourselves would be a second implementation of an interning scheme we cannot see.
constexpr uint32_t kEventBoySelected = 0x8349B5A0u;
constexpr uint32_t kEventGirlSelected = 0x8349B5A4u;

/// Starts a new game, on the guest's own thread.
///
/// WHY THIS FUNCTION AND NOT THE OBVIOUS ONES. The two nearby candidates are both wrong:
///
///   * `GUI_FrontendRoot_MenuActionDispatch` (0x826DFD58) only reaches the GENDER CARD. Its
///     "New Game" branch finds the `choosecard` element by name and calls
///     `sub_82426CA8(element, screen+20, 1)` at 0x826E021C — GotoSlide, slide 1, the card's
///     `Intro` — then writes `screen+24 = 2` at 0x826E022C and fires the line
///     "TEXT_QUEST_QC010_FRONT_END_20". (It is NOT an attribute write; the attribute setter
///     in this file is `sub_82425DD0` and nothing on this path calls it.) Substate 2 has no
///     path to a game start at all: the commit only happens once the presentation's own
///     Behavior element fires BOY_SELECTED or GIRL_SELECTED back into the handler below. It
///     is also a virtual method — vtable slot +16 of 0x820D0934 — with no direct call site
///     to imitate.
///
///     GODOT ASKS THE QUESTION INSTEAD. `Engine_FrontEndCardChoice` draws that same
///     `choosecard` art, owns the left/right selection the guest would have driven through
///     slides 2..5, and posts "New Game" or "New Game Female" here — which is why this pump
///     takes the event directly and does not need the substate machine at all.
///   * `Frontend_SetStartGameTarget` (0x82379838) is the LOAD path. All three of its callers
///     are load paths, and it does not set the entrance, scenario or quest a new game needs.
///
/// The real commit is `sub_82379628` — world "Albion", level "BWSSlums", entrance
/// "QC010_ChildhoodStart", and an empty `g_PendingLoadSaveName`, which is what makes
/// `Game_InitDatabases` take the new-game branch instead of loading a save. `sub_826E0D60`
/// is its only caller, and it takes an EVENT rather than a string, which makes it the
/// cheapest correct entry point.
///
/// A PURE MEMORY WRITE CANNOT DO THIS, which is why the pump had to exist: the commit
/// allocates (`sub_82319928` publishes a new in-game state object) and the start-game globals
/// are refcounted LHStrings. Allocation is not a write.
///
/// THE ARGUMENTS, read off the disassembly rather than a decompile:
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
bool StartNewGame(PPCContext& ctx, uint8_t* base, bool female) {
  const uint32_t frontend = LoadGuestU32(base, kFrontEndState);
  if (frontend == 0) {
    return false;
  }

  const uint32_t event =
      LoadGuestU32(base, female ? kEventGirlSelected : kEventBoySelected);
  if (event == 0) {
    // Static init has not interned the events yet. Queued rather than sent, because sending
    // id 0 would fall off the end of the compare chain and silently do nothing — the failure
    // that looks like success.
    return false;
  }

  // A frame of our own on the GUEST stack, claimed by lowering r1.
  //
  // The record cannot live in host memory: `r4` is dereferenced by guest code, so it has to
  // be a guest address. It cannot live BELOW the stack pointer either — `sub_826E0D60` frames
  // down from r1 and `sub_82379628` goes deeper still, so anything parked under the current
  // sp is overwritten by the very call it is an argument to. Lowering r1 first makes the gap
  // ours: the callees frame below OUR frame and never reach into it.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x40u) & ~0xFu;
  const uint32_t record = frame + 0x20u;

  StoreGuestU32(base, record + 0u, 0u);
  StoreGuestU32(base, record + 4u, event);

  // Whole-context save/restore. The pump is standing in the middle of `sub_822C1BD0`, whose
  // own delegation still needs its `r3` — clobbering registers for our call and handing the
  // wreckage to the original is how a pump corrupts the function it is riding on.
  const PPCContext saved = ctx;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = 0u;  // don't-care, per the disassembly above
  ctx.r4.u32 = record;

  sub_826E0D60(ctx, base);

  ctx = saved;

  // A game just started, so the saved-game count is stale. It will not be re-sampled until the
  // front end is up again, which is exactly when it next matters.
  g_resample.store(true, std::memory_order_relaxed);
  return true;
}

/// The GUI object, and the main-menu screen hanging off it at +184.
///
/// The vtable check is the one from `RefiiHostGetFrontEnd`, and it is load-bearing here too:
/// `dword_83496B08` has two writers through the same base ctor, and once the game has taken
/// over it holds a BASE-ONLY object whose +184 is not a screen pointer at all. Dispatching a
/// menu action into that would hand `sub_826DFD58` a `this` it immediately calls a virtual on.
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
/// GATED ON THE FRONT END BEING UP, though the probe itself needs nothing of the sort. Two
/// reasons, and the second is the load-bearing one:
///
///   * it is when the guest asks. `sub_826E2220` calls this at menu-spawn time and nowhere
///     else, so sampling there is sampling the same moment with the same answer.
///   * the SLOT LIMIT is not valid before then. `dword_83319620` holds 150 in the image and is
///     overwritten with 6 by `sub_82378618` — the front-end enter — so a count taken earlier
///     would be compared against a limit 25x too large. The two have to come from the same
///     side of that store.
///
/// NO SCRATCH FRAME, unlike every other call in this file. `sub_82443308` takes no arguments
/// and frames its own 46 320 bytes downward from r1 after an `RtlCheckStack12` probe, so the
/// discipline the other calls need — claim a gap the callee cannot frame into — has nothing to
/// protect here. The context save/restore still applies: we are standing inside
/// `sub_822C1BD0`, whose own delegation still needs its registers.
void SampleSavedGameCount(PPCContext& ctx, uint8_t* base) {
  if (!g_resample.load(std::memory_order_relaxed)) {
    return;
  }
  if (MainMenuScreen(base) == 0) {
    // Front end not up. Left pending rather than answered with a zero — see g_save_count.
    return;
  }

  g_resample.store(false, std::memory_order_relaxed);

  const PPCContext saved = ctx;
  sub_82443308(ctx, base);
  const int32_t count = static_cast<int32_t>(ctx.r3.u32);
  ctx = saved;

  g_save_count.store(count, std::memory_order_relaxed);
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
/// THE ADDRESSES ARE THE ARGUMENTS. `Frontend_SetStartGameTarget` takes pointers to live handle
/// slots inside the guest's own vectors — so this passes `begin + 4*index` and
/// `records + 40*index + 12` rather than copying anything. There is nothing to allocate and no
/// scratch frame to claim, which is why this is the one commit in this file with no stack work.
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
///
/// THIS IS THE GENERAL ROUTE, and it exists because the alternative was five special cases.
/// `sub_826DFD58` is a strcmp chain over the front end's own item names — "Load Game",
/// "Downloadable Content", "SubtitlesOn", "SubtitlesOff" — so naming one is the entire
/// contract. An entry the guest was never shipped with simply matches nothing and does
/// nothing, which is exactly the behaviour a host-authored menu wants.
///
/// THE RECORD IS THREE WORDS AND NEEDS NO GUEST OBJECT. `a2+0` is a pointer to a string body
/// whose own `+0` is a `char*`; `a2+4` is a command id; `a2+8` an extra param. The dispatcher
/// dereferences `a2[0]` exactly once (`lwz r3, 0(r28)` at 0x826DFFEC feeding the strcmp at
/// 0x826E0040), never retains the record, never frees it, and overwrites its own copy of `r4`
/// before the name chain even begins. So a scratch frame on the guest stack is sufficient —
/// there is no refcount to honour and no allocator to involve.
///
/// `a2+4` MUST NOT BE 302, 141 OR 140. Those three are consumed by a switch ahead of the name
/// chain — 302 is Language (and reads `a2+8`), 140/141 are the menu slide sounds — so a stray
/// value there would perform something other than the name asked for. Zero falls through to
/// the names and is then never read again.
///
/// Returns false when the front end is not up, leaving the action queued.
/// The dispatcher's three ID-based cases, which are decided BEFORE the name chain is reached.
///
/// 140 and 141 are the menu's own slide sounds — they fire "SE_GUI_SLIDE_MENU_UP"
/// (`dword_83497DD8`) and "SE_GUI_SLIDE_MENU_DOWN" (`dword_83497DDC`) on the emitter at
/// `*(*(dword_83496AC0+8)+0x50)` and do nothing else. That is the whole reason a host that owns
/// the menu can still make the game's own movement sound: it asks the guest for it by the same
/// id its shipped Lua would have used, rather than reaching for the audio system directly.
///
/// 302 is Language: it reads `a2+8` as the index into the guest's language table and is the
/// ONLY way to reach that action — the language entries are spawned with this command id and
/// never appear in the dispatcher's name chain at all, which is why naming "Language" did
/// nothing.
constexpr uint32_t kCommandNone = 0u;
constexpr uint32_t kCommandSetLanguage = 302u;
constexpr uint32_t kCommandSlideMenuUp = 140u;
constexpr uint32_t kCommandSlideMenuDown = 141u;

/// Host-only names for the two sounds. The guest has no such menu entries — on its side these
/// are command ids, not names — so these are the pump's vocabulary, the way "New Game Female"
/// already is.
constexpr const char* kActionSlideUp = "MenuSlideUp";
constexpr const char* kActionSlideDown = "MenuSlideDown";

/// "Language:<index>" — the one action that needs a NUMBER as well as an identity.
///
/// A name with the index on the end rather than a second export, because the queue is a queue
/// of names and every other action fits that. The guest's own language entries are spawned one
/// per installed language with command 302 and the row index, so this is the same pair, carried
/// the only way this pump carries anything.
constexpr const char* kActionLanguagePrefix = "Language:";

/// "LoadSave:<index>" — pick a save out of the list the guest enumerated.
///
/// An index rather than a name, because the guest identifies a save by the ADDRESS of its
/// handle slot inside its own vector, not by its text. Sending the name would mean finding it
/// again on this side and hoping the two orderings agree; the index is the join the guest
/// itself uses (`this+72`, moved by its own left/right handler).
constexpr const char* kActionLoadSavePrefix = "LoadSave:";

bool DispatchByName(PPCContext& ctx, uint8_t* base, const std::string& name,
                    uint32_t command = kCommandNone, uint32_t param = 0u) {
  const uint32_t screen = MainMenuScreen(base);
  if (screen == 0) {
    return false;
  }

  // A frame of our own on the GUEST stack, claimed by lowering r1 — the same discipline
  // StartNewGame uses and for the same reason: the callee frames below OUR frame and would
  // otherwise overwrite the very argument it was passed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x80u) & ~0xFu;

  // Laid out upward from the frame base so nothing overlaps: text, then the 4-byte body that
  // points at it, then the 12-byte record that points at the body.
  const uint32_t text = frame + 0x20u;
  const uint32_t body = frame + 0x60u;
  const uint32_t record = frame + 0x68u;

  // 0x40 bytes for the name, which every shipped menu entry fits inside with room to spare.
  // Refused rather than truncated: a truncated name would silently match a DIFFERENT branch
  // of the strcmp chain, which is worse than not acting at all.
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
  // `a2+8` is read on exactly one path — case 302, which passes it to `sub_82382C68` as the
  // LANGUAGE INDEX. Every other branch ignores it, so zero is the right default and this is
  // meaningful only alongside kCommandSetLanguage.
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

/// Takes the whole queue under the lock and runs it outside.
///
/// Swapped rather than popped one at a time because an action will eventually make guest
/// calls, and holding a host mutex across guest code is how a host lock ends up on a guest
/// thread's critical path. It also means a post made WHILE the pump is running lands in the
/// next tick rather than extending this one indefinitely.
void DrainOnGuestThread(PPCContext& ctx, uint8_t* base) {
  std::vector<PendingAction> taken;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_pending.empty()) {
      return;
    }
    taken.swap(g_pending);
  }

  std::vector<PendingAction> deferred;

  for (const PendingAction& action : taken) {
    bool ran = false;

    // Dispatch BY NAME, because that is the guest's own contract for the front end — its
    // dispatcher is a strcmp chain over these strings, not an index or a table. Keeping the
    // host side keyed the same way is what lets a Godot-authored entry name a guest action,
    // and lets an entry the guest never had simply never match.
    // THE TWO NEW-GAME NAMES ARE THE EXCEPTION, and deliberately so. The guest's own "New
    // Game" branch does not start a game — it opens the gender card and waits for the
    // presentation to answer. Godot asks that question itself (Engine_FrontEndCardChoice), so
    // these two carry the ANSWER and go straight to the commit rather than back through a
    // dispatcher that would put the card up again behind ours.
    if (action.name == "New Game") {
      ran = StartNewGame(ctx, base, /*female=*/false);
    } else if (action.name == "New Game Female") {
      ran = StartNewGame(ctx, base, /*female=*/true);
    } else if (action.name.rfind(kActionLanguagePrefix, 0) == 0) {
      // The name carries the index; the dispatcher takes it from `a2+8` and the name is never
      // looked at on this path, so it is left empty rather than sent as "Language:3".
      const std::string index = action.name.substr(std::strlen(kActionLanguagePrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no language index — dropped", action.name);
        g_drained.fetch_add(1, std::memory_order_relaxed);
        continue;
      }

      ran = DispatchByName(ctx, base, std::string(), kCommandSetLanguage,
                           static_cast<uint32_t>(value));
    } else if (action.name.rfind(kActionLoadSavePrefix, 0) == 0) {
      const std::string index = action.name.substr(std::strlen(kActionLoadSavePrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no save index — dropped", action.name);
        g_drained.fetch_add(1, std::memory_order_relaxed);
        continue;
      }

      ran = LoadSavedGame(ctx, base, static_cast<uint32_t>(value));
    } else if (action.name == kActionSlideUp) {
      // Name empty on purpose: the switch takes the id before the name chain is reached, so
      // the string is never dereferenced on this path.
      ran = DispatchByName(ctx, base, std::string(), kCommandSlideMenuUp);
    } else if (action.name == kActionSlideDown) {
      ran = DispatchByName(ctx, base, std::string(), kCommandSlideMenuDown);
    } else {
      // Everything else goes through the guest's own by-name dispatcher. A name it was never
      // shipped with matches nothing in the strcmp chain and does nothing — which is the right
      // outcome for a host-authored entry, and is why there is no host-side list to keep in
      // step with the guest's.
      ran = DispatchByName(ctx, base, action.name);
    }

    if (ran) {
      REXLOG_INFO("Refii guest-tick pump: ran '{}' on the guest tick thread", action.name);
      g_drained.fetch_add(1, std::memory_order_relaxed);
    } else {
      // The world is not ready — the front-end state does not exist yet, or the event ids
      // have not been interned. Held rather than dropped or forced: the pump runs from the
      // boot movies onward, so "too early" is the normal case for the first few hundred
      // ticks, and forcing it would dereference a null.
      deferred.push_back(action);
    }
  }

  if (!deferred.empty()) {
    std::lock_guard<std::mutex> lock(g_mutex);
    // Front of the queue: an action posted while this one was waiting must not overtake it.
    g_pending.insert(g_pending.begin(), deferred.begin(), deferred.end());
  }
}

/// The in-game loading screen the guest stood up, or 0 when none is up.
///
/// Same three-root walk the host bridge's `loading_screen_object` uses — `sub_82388380`'s own
/// fallback chain (0x83496AB8, then 0x83496AC0, then 0x8335C4C4) -> app -> +8 context -> +36
/// manager -> +4 screen. `manager+4` is 0 when there is no screen, which is the ordinary case
/// out of game and during the front end.
uint32_t ResolveInGameLoadingScreen(uint8_t* base) {
  for (uint32_t root : {0x83496AB8u, 0x83496AC0u, 0x8335C4C4u}) {
    const uint32_t app = LoadGuestU32(base, root);
    if (!LooksLikeGuestPointer(app)) continue;
    const uint32_t owner = LoadGuestU32(base, app + 8u);
    if (!LooksLikeGuestPointer(owner)) continue;
    const uint32_t manager = LoadGuestU32(base, owner + 36u);
    if (!LooksLikeGuestPointer(manager)) continue;
    const uint32_t screen = LoadGuestU32(base, manager + 4u);
    if (LooksLikeGuestPointer(screen)) return screen;
  }
  return 0;
}

/// Is a game WORLD up and populated — `World + 0xA8`, the bit both the travel and load-from-save
/// paths set. The same walk `RefiiHostWorldIsActive` uses. False through boot and the front end,
/// which is exactly what keeps the finish below off the startup loading owner.
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

/// The screen object we have already brought down, so the finish fires once per load and not
/// every tick. Cleared when no screen is up, which re-arms it for the next level change.
std::atomic<uint32_t> g_finished_loading_screen{0};

/// Bring the IN-GAME loading screen down — the host standing in for the present loop that would.
///
/// In this headless embedded mode nothing presents, so after an in-game level load the guest
/// never runs the render tick that fades its loading screen out and releases the game thread: it
/// sits at `fade=1 requested=0` forever with the loading music playing, which is the black-screen
/// stall. The STARTUP screen is finished synthetically by the frontend bridge
/// (`TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline`); this is that same move for the
/// in-game one, and it runs from the in-game tick (`sub_82276D10` -> the pump) where it belongs.
///
/// Gated on the game world being active so it can ONLY touch an in-game screen. It mirrors the
/// frontend bridge's proven sequence: keep the frame past the fade's ten-tick guard, drop the
/// fade, request teardown, run the guest's own fade/teardown tick, and — if one tick does not
/// latch `done` — finish it directly and pulse the event `sub_82361458` blocks the game thread on.
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
  if (g_finished_loading_screen.load(std::memory_order_relaxed) == screen) {
    return;
  }
  if (LoadGuestU8(base, screen + 177u) != 0) {
    g_finished_loading_screen.store(screen, std::memory_order_relaxed);
    return;
  }

  if (LoadGuestU32(base, screen + 168u) < 10u) {
    StoreGuestU32(base, screen + 168u, 10u);
  }
  StoreGuestU32(base, screen + 172u, 0u);

  // DO NOT DRIVE `sub_8235E5A0` FROM THIS THREAD. It deadlocks, every time, and the stack is
  // unambiguous:
  //
  //   rex_lhGameLoop_Tick -> sub_822C1BD0 -> OnGuestTick -> MaybeFinishInGameLoadingScreen
  //     -> sub_8235E5A0 -> rex_lhRenderer_BeginGPUFrame -> RtlEnterCriticalSection  [blocks]
  //
  // The screen's own tick reaches into the renderer to open a GPU frame, and that lock is held
  // by the 3D Engine thread, which is parked forever inside `rex_lhRenderer_RenderFrame` ->
  // `sub_822DFE08` (the command-stream drain) spinning on SwitchToThread — there is no GPU to
  // drain the ring, so it never returns and never releases. The game thread then waits on it
  // for ever: the 15 Hz tick stops, `dword_83319650` stays frozen, and because that counter is
  // what gates the world update, the camera snapshot is never refreshed either. The "camera is
  // out of whack" symptom is this deadlock, one remove.
  //
  // So do the teardown by hand instead, which is what the fallback below always did anyway:
  // latch `done` directly and pulse the event `sub_82361458` blocks the game thread on. Pure
  // memory writes plus one event signal, no renderer lock, nothing that can block.
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
/// and everything behind that gate is the in-game world: the entity advance, the animation
/// pose build, the camera update. Measured in this port after a level load, `+0x105` is 1 (the
/// world is ready) and **`+0x104` is 0** — so the tick loops for ever doing none of it. Particle
/// systems live OUTSIDE the gate, on `sub_821DFDD0`, which is why FX animate happily while the
/// hero, the NPCs and the camera are frozen: the entity path was entered exactly once, before
/// the flag went to zero, and never again.
///
/// The guest sets this itself from `sub_82312EE8` (`li r11,1; stb r11,260(r3)`) and its siblings
/// on the game object, all of which run off the front-end/gameplay transition that this hosted,
/// no-present build never completes. Setting it here is the same class of nudge as the loading
/// screen finish above: the host standing in for a transition the guest cannot reach on its own.
///
/// Gated on the world genuinely being up (`InGameWorldActive`) and on the guest's own
/// `+0x105`, so this can only ever start a simulation the guest already considers ready — it
/// cannot force one during boot, the front end, or a level change. Off-switch
/// `REFII_ARM_INGAME_SIM=0`.
void MaybeArmInGameSimulation(PPCContext& ctx, uint8_t* base) {
  (void)ctx;
  if (base == nullptr) {
    return;
  }

  // OFF BY DEFAULT — THIS DOES NOT WORK, and the negative result is the useful part.
  //
  // Forcing `+0x104` to 1 does open the gate, and the guest then faults within about half a
  // second (0xC0000005). So the flag being clear is a SYMPTOM, not the cause: the game object
  // has not completed its transition into gameplay, and the block behind the gate dereferences
  // state that transition is supposed to have built. Setting the flag just runs that code
  // against a half-built object.
  //
  // Kept, opt-in via `REFII_ARM_INGAME_SIM=1`, purely so nobody has to rediscover that the
  // obvious shortcut is a dead end. The real fix is upstream: make the guest actually complete
  // the front-end -> gameplay transition, which in this hosted build it never does.
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

  // Only when the guest itself says the world is ready, and only when the flag is actually
  // clear — a write every tick would be a pointless store and would mask the guest setting it.
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

void FinishStuckInGameLoadingScreenFromHost(uint8_t* base) {
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

  static uint32_t watched = 0;
  static uint32_t stuck_steps = 0;

  const bool stuck = screen != 0 && LoadGuestU8(base, screen + 177u) != 0 &&
                     LoadGuestU32(base, screen + 172u) != 0u;
  if (!stuck) {
    watched = 0;
    stuck_steps = 0;
    return;
  }

  if (screen != watched) {
    watched = screen;
    stuck_steps = 0;
  }

  constexpr uint32_t kStuckSteps = 30u;
  if (stuck_steps < kStuckSteps) {
    ++stuck_steps;
    return;
  }

  if (LoadGuestU32(base, screen + 168u) < 10u) {
    StoreGuestU32(base, screen + 168u, 10u);
  }
  StoreGuestU32(base, screen + 172u, 0u);
  StoreGuestU8(base, screen + 176u, 0u);
  StoreGuestU8(base, screen + 177u, 1u);

  const uint32_t event = LoadGuestU32(base, screen + 180u);
  if (event != 0u) {
    rex::kernel::xboxkrnl::xeNtSetEvent(event, nullptr);
  }

  watched = 0;
  stuck_steps = 0;

  static std::atomic<uint32_t> logged{0};
  if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
    REXLOG_INFO("Refii in-game loading screen finished from host step "
                "(screen=0x{:08X} event=0x{:08X})",
                screen, event);
  }
}

/// One tick's worth of pump work. Split out so the hook body stays the delegation it must be.
void OnGuestTick(PPCContext& ctx, uint8_t* base) {
  const uint64_t tick = g_ticks.fetch_add(1, std::memory_order_relaxed);

  // The pump announcing itself, once and then rarely.
  //
  // The first tick is the one that proves the hook landed at all; the later samples are
  // spaced far enough apart to fall in different STATES — the boot movies run at 60 Hz, the
  // front end at 30 and the game at 15, so a report at tick 1, 600 and 6000 is a report from
  // roughly each of the three tick loops. A single once-only line could not distinguish "the
  // pump runs" from "the pump ran during the logos and stopped".
  if (tick == 0 || tick == 600 || tick == 6000 || tick == 60000) {
    REXLOG_INFO("Refii guest-tick pump: tick {} on the guest tick thread (drained {})",
                tick + 1, g_drained.load(std::memory_order_relaxed));
  }

  DrainOnGuestThread(ctx, base);

  // The present loop that would normally fade the in-game loading screen out is off in this
  // headless mode, so the guest never releases the game thread from it. Do it here, from the
  // in-game tick, once the world it was loading is up.
  MaybeFinishInGameLoadingScreen(ctx, base);

  MaybeArmInGameSimulation(ctx, base);

  // After the drain, so a "New Game" that just ran is reflected in the next answer rather than
  // the one before it.
  SampleSavedGameCount(ctx, base);
}

}  // namespace refii::vfs

REX_EXTERN(sub_822C1BD0) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnGuestTick(ctx, base);

  __imp__sub_822C1BD0(ctx, base);
}
