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
#include <mutex>
#include <string>
#include <vector>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

// The New Game commit. NOT hooked — we call the guest's own function, so this is the plain
// symbol rather than an `__imp__` original.
PPC_EXTERN_FUNC(sub_826E0D60);

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
    if (action.name == "New Game") {
      ran = StartNewGame(ctx, base, /*female=*/false);
    } else if (action.name == "New Game Female") {
      ran = StartNewGame(ctx, base, /*female=*/true);
    } else {
      // An unimplemented name is DROPPED, loudly. Requeueing it would grow the queue without
      // bound, and a silent drop is the failure that takes longest to notice — the menu would
      // look like it worked.
      REXLOG_INFO("Refii guest-tick pump: '{}' has no action — dropped", action.name);
      g_drained.fetch_add(1, std::memory_order_relaxed);
      continue;
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
}

}  // namespace refii::vfs

REX_EXTERN(sub_822C1BD0) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnGuestTick(ctx, base);

  __imp__sub_822C1BD0(ctx, base);
}
