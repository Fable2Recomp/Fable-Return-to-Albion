// refii — the guest's pub-game pump with the Scaleform module taken out.
//
// `sub_82467B08` runs once per guest render frame (called from `sub_82388380` at 0x82388654,
// just before Present) and drives states 2-8 of the manager at 0x834C20B8:
//
//   2  HeapCreate, the physical reserve (`sub_824674A8`), the sound emitter (`sub_829FD018`),
//      then `sub_82468378` creates the GFx module and loads `<name>\game.swf`   -> 3 (6 if +136)
//   3/4  Advance + Display the movie (`sub_82467AA8`)
//   5  movie EndSave (`game->vf+36`), Advance + Display                        -> 3
//   6  release game and module, free the emitter, HeapDestroy, free the reserve -> 9
//   7  movie EndGame (`game->vf+20(0)`)                                         -> 8
//   8  Advance + Display, re-send EndGame every 10 frames, wait for GameOver
//
// Godot plays the game, so the movie must never exist: left alone, the guest loads the real
// game.swf 30 frames after state 2 and runs it invisibly under Godot's board, reading the same
// pad, playing its own sounds and writing the profile. When the host has claimed the pub games
// this replaces 2-8 and keeps everything that is not the movie: the emitter the sounds play
// through, the state transitions, and the guest's own handlers for what the movie would have
// said. Each command below is one branch of the fscommand handler `sub_82EF2AC0`, calling the
// same function that branch calls, on the same thread.
//
// Unclaimed (standalone, or a host older than the claim), the original runs untouched.

#include <cstdint>
#include <cstring>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(__imp__sub_82467B08);

namespace {

constexpr uint32_t kManagerCountdownOffset = 4u;   // frames left before the pump acts
constexpr uint32_t kManagerLoadingOffset = 24u;    // non-zero while `sub_82468378` runs
constexpr uint32_t kManagerStateOffset = 52u;
constexpr uint32_t kManagerEmitterOffset = 64u;
constexpr uint32_t kManagerProfileOffset = 124u;
constexpr uint32_t kManagerAbortOffset = 136u;     // byte; set by the GameFlow command's abort

constexpr uint32_t kSoundManagerGlobal = 0x83496D9Cu;

constexpr uint32_t kSetState = 0x82467E40u;          // (mgr, state) under mgr+68
constexpr uint32_t kCreateEmitter = 0x829FD018u;     // (soundMgr, v1 position) -> emitter
constexpr uint32_t kFreeEmitter = 0x829FD168u;       // (soundMgr, emitter, 1)
constexpr uint32_t kPlaySound = 0x82467668u;         // (mgr, char* cue, f1, f2, f3)
constexpr uint32_t kSetSoundVolume = 0x82467790u;    // (mgr, f1) — category 0, mgr+128
constexpr uint32_t kSetMusicVolume = 0x824677D8u;    // (mgr, f1) — category 1, mgr+132
constexpr uint32_t kPauseSounds = 0x82467820u;       // (mgr, bool) — the "PUB_GAMES" voices
constexpr uint32_t kProfileSetGold = 0x82EE8640u;    // (profile, int64) +0x108, dirty +0x364
constexpr uint32_t kProfileSetDebt = 0x82EE8810u;    // (profile, int64) +0x130
constexpr uint32_t kProfileSetRank = 0x82EE86B0u;    // (profile, int64) +0x110
constexpr uint32_t kProfileAddPoints = 0x82EE87A8u;  // (profile, index, int64) +0x118[index] +=

// Command kinds — HostBridge.PubGameCommand on the C# side.
constexpr uint32_t kCommandPlaySound = 1u;
constexpr uint32_t kCommandPauseSound = 2u;
constexpr uint32_t kCommandHoldings = 3u;
constexpr uint32_t kCommandPoints = 4u;
constexpr uint32_t kCommandRank = 5u;
constexpr uint32_t kCommandSoundVolume = 6u;
constexpr uint32_t kCommandMusicVolume = 7u;
constexpr uint32_t kCommandGameOver = 8u;

// Events — what the movie would have been told. HostBridge.PubGameEvent on the C# side.
constexpr uint32_t kEventRunning = 1u;      // 2 -> 3: the session is up
constexpr uint32_t kEventSaveEnded = 2u;    // state 5: `pubGame.EndSave`
constexpr uint32_t kEventSaveSkipped = 4u;  // holdings arrived outside state 3: no save follows
constexpr uint32_t kEventEndGame = 8u;      // state 7: `pubGame.EndGame`, the guest aborting

/// GameOver was handed to the guest this session; a save completing after it must not put the
/// session back to 3. The original has the same race between its render and game threads.
bool g_game_over = false;

uint32_t State(uint8_t* base, uint32_t manager) {
  return refii::vfs::LoadGuestU32(base, manager + kManagerStateOffset);
}

/// One guest call with the caller's registers restored afterwards.
template <typename Setup>
void Call(PPCContext& ctx, uint8_t* base, uint32_t address, Setup setup) {
  PPCFunc* function = refii::vfs::FindGuestFunction(address);
  if (function == nullptr) {
    return;
  }

  const PPCContext saved = ctx;
  setup(ctx);
  function(ctx, base);
  ctx = saved;
}

void SetState(PPCContext& ctx, uint8_t* base, uint32_t manager, uint32_t state) {
  Call(ctx, base, kSetState, [&](PPCContext& c) {
    c.r3.u64 = manager;
    c.r4.u64 = state;
  });
}

void PlaySound(PPCContext& ctx, uint8_t* base, uint32_t manager, const char* cue,
               const double* numbers) {
  if (refii::vfs::LoadGuestU32(base, manager + kManagerEmitterOffset) == 0u) {
    return;
  }

  // The cue name goes on the guest's stack, well below the caller's frame: the callee's
  // register saves land below r1 before it moves it, so both the string and the new r1 sit a
  // full frame under the live one.
  const uint32_t sp = static_cast<uint32_t>(ctx.r1.u64);
  const uint32_t text = (sp - 1024u) & ~15u;
  const size_t length = std::strlen(cue);
  for (size_t i = 0; i <= length && i < 255; ++i) {
    refii::vfs::StoreGuestU8(base, text + static_cast<uint32_t>(i),
                             i < length ? static_cast<uint8_t>(cue[i]) : 0u);
  }
  refii::vfs::StoreGuestU8(base, text + 255u, 0u);

  const uint32_t frame = (text - 512u) & ~15u;
  refii::vfs::StoreGuestU32(base, frame, sp);

  Call(ctx, base, kPlaySound, [&](PPCContext& c) {
    c.r1.u64 = frame;
    c.r3.u64 = manager;
    c.r4.u64 = text;
    c.f1.f64 = numbers[0];
    c.f2.f64 = numbers[1];
    c.f3.f64 = numbers[2];
  });
}

/// Hands the queue to the guest's own handlers — the branches of `sub_82EF2AC0`.
void Drain(PPCContext& ctx, uint8_t* base, uint32_t manager, bool live) {
  uint32_t kind = 0;
  char text[256];
  double numbers[3];
  int64_t integers[2];
  while (refii_bridge_pubgame_take_command(&kind, text, sizeof(text), numbers, integers) != 0) {
    if (!live) {
      continue;
    }

    const uint32_t profile = refii::vfs::LoadGuestU32(base, manager + kManagerProfileOffset);
    const bool has_profile = profile >= 0x10000u && profile < 0xC0000000u;
    switch (kind) {
      case kCommandPlaySound:
        PlaySound(ctx, base, manager, text, numbers);
        break;

      case kCommandPauseSound:
        if (refii::vfs::LoadGuestU32(base, manager + kManagerEmitterOffset) != 0u) {
          Call(ctx, base, kPauseSounds, [&](PPCContext& c) {
            c.r3.u64 = manager;
            c.r4.u64 = integers[0] != 0 ? 1u : 0u;
          });
        }
        break;

      case kCommandHoldings:
        // `gold:G; debt:D` — both setters, then manager slot 0 (`sub_824675E0`), which from
        // state 3 invokes the movie's StartSave and moves to 4 so the tick saves. Godot's game
        // was told StartSave by the host when it sent this; say so when no save will follow.
        if (has_profile) {
          Call(ctx, base, kProfileSetGold, [&](PPCContext& c) {
            c.r3.u64 = profile;
            c.r4.s64 = integers[0];
          });
          Call(ctx, base, kProfileSetDebt, [&](PPCContext& c) {
            c.r3.u64 = profile;
            c.r4.s64 = integers[1];
          });
        }

        if (!g_game_over && State(base, manager) == 3u) {
          SetState(ctx, base, manager, 4u);
        } else {
          refii_bridge_pubgame_note_event(kEventSaveSkipped);
        }
        break;

      case kCommandPoints:
        // `points:N` — added into the per-game slot `game+80`, the settings' gameIndex.
        if (has_profile) {
          Call(ctx, base, kProfileAddPoints, [&](PPCContext& c) {
            c.r3.u64 = profile;
            c.r4.u64 = static_cast<uint32_t>(integers[1]);
            c.r5.s64 = integers[0];
          });
        }
        break;

      case kCommandRank:
        if (has_profile) {
          Call(ctx, base, kProfileSetRank, [&](PPCContext& c) {
            c.r3.u64 = profile;
            c.r4.s64 = integers[0];
          });
        }
        break;

      case kCommandSoundVolume:
      case kCommandMusicVolume:
        Call(ctx, base, kind == kCommandSoundVolume ? kSetSoundVolume : kSetMusicVolume,
             [&](PPCContext& c) {
               c.r3.u64 = manager;
               c.f1.f64 = numbers[0];
             });
        break;

      case kCommandGameOver:
        // `sub_82EF3B60` acts once; the manager's slot +12 is `sub_82467660` -> state 6.
        if (!g_game_over) {
          g_game_over = true;
          SetState(ctx, base, manager, 6u);
        }
        break;

      default:
        break;
    }
  }
}

}  // namespace

REX_EXTERN(sub_82467B08) {
  REX_FUNC_PROLOGUE();

  if (refii_bridge_pubgame_claimed() == 0) {
    __imp__sub_82467B08(ctx, base);
    return;
  }

  const uint32_t manager = ctx.r3.u32;

  // The same two gates as the original (0x82467B30, 0x82467B88): the countdown case 1 arms,
  // and the loading guard. The countdown is written by the tick under mgr+68 only on the way
  // into state 2, so reading it bare costs at most one frame.
  const uint32_t countdown = refii::vfs::LoadGuestU32(base, manager + kManagerCountdownOffset);
  if (countdown != 0u) {
    refii::vfs::StoreGuestU32(base, manager + kManagerCountdownOffset, countdown - 1u);
    return;
  }

  if (refii::vfs::LoadGuestU32(base, manager + kManagerLoadingOffset) != 0u) {
    return;
  }

  const uint32_t state = State(base, manager);
  switch (state) {
    case 2: {
      // Only the emitter: the heap, the physical reserve and the module exist for the movie.
      g_game_over = false;
      const uint32_t sound = refii::vfs::LoadGuestU32(base, kSoundManagerGlobal);
      if (sound != 0u) {
        PPCFunc* create = refii::vfs::FindGuestFunction(kCreateEmitter);
        if (create != nullptr) {
          const PPCContext saved = ctx;
          ctx.r3.u64 = sound;
          std::memset(&ctx.v1, 0, sizeof(ctx.v1));
          create(ctx, base);
          const uint32_t emitter = ctx.r3.u32;
          ctx = saved;
          refii::vfs::StoreGuestU32(base, manager + kManagerEmitterOffset, emitter);
        }
      }

      const bool aborted = refii::vfs::LoadGuestU8(base, manager + kManagerAbortOffset) != 0u;
      SetState(ctx, base, manager, aborted ? 6u : 3u);
      if (!aborted) {
        refii_bridge_pubgame_note_event(kEventRunning);
      }
      break;
    }

    case 3:
    case 4:
    case 8:
      Drain(ctx, base, manager, true);
      break;

    case 5:
      refii_bridge_pubgame_note_event(kEventSaveEnded);
      Drain(ctx, base, manager, true);
      if (State(base, manager) == 5u) {
        SetState(ctx, base, manager, g_game_over ? 6u : 3u);
      }
      break;

    case 6: {
      const uint32_t emitter = refii::vfs::LoadGuestU32(base, manager + kManagerEmitterOffset);
      const uint32_t sound = refii::vfs::LoadGuestU32(base, kSoundManagerGlobal);
      if (emitter != 0u && sound != 0u) {
        Call(ctx, base, kFreeEmitter, [&](PPCContext& c) {
          c.r3.u64 = sound;
          c.r4.u64 = emitter;
          c.r5.u64 = 1u;
        });
      }

      refii::vfs::StoreGuestU32(base, manager + kManagerEmitterOffset, 0u);
      SetState(ctx, base, manager, 9u);
      break;
    }

    case 7:
      // The guest is ending the session itself (a GameFlow command pushed over it). Godot's game
      // gets `pubGame.EndGame` and answers with GameOver like any other ending.
      refii_bridge_pubgame_note_event(kEventEndGame);
      Drain(ctx, base, manager, true);
      if (State(base, manager) == 7u) {
        SetState(ctx, base, manager, 8u);
      }
      break;

    default:
      // Outside a session: nothing a stale post could still mean.
      Drain(ctx, base, manager, false);
      break;
  }
}
