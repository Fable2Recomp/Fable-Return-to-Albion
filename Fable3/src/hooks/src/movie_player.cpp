// refii - Fable 3's Bink players, stated into the shared movie state (core/movie_state.h) so
// Godot plays each movie natively, as it does Fable 2's.
//
// ONE PLAYER CLASS, THREE OWNERS. Every Fable 3 movie is a 256-byte Bink object B (vtable
// off_820E2B1C) opened by sub_82C3D2C0(B, source, audio, poolAlloc), drawn in the rect
// sub_82C3D538 states and closed by sub_82C3D450 (the only caller of BinkClose). The owners are
// the startup logo player (B = P+12 of a 276-byte player built by sub_8230B0F0, opened from a
// word-swapped .bik the guest has loaded), the script movie manager (opened by path from
// sub_8235CA30 after a Lua PlayMovie*) and PRIMITIVE_INSTANCE_MOVIE (opened by name, silent).
//
// B, as these hooks read it (all from the Fable 3 IDB):
//   B+32  HBINK         (stw r3,0x20(r31) at 0x82C3D304)
//   B+245 DONE          (stb r25,0xF5(r28) at 0x82C3E0DC, when FrameNum == Frames); read by the
//                       startup player (P+257, 0x8230B350) and the movie manager (0x8235CC70)
//   B+246 audio         (the opener's 3rd argument, stb r5,0xF6(r3) at 0x82C3D2D4): volumes
//                       0x8000 for all six tracks when set, 0 when clear (0x82C3D3C8..0x82C3D414)
// and the startup player's SKIP at P+272 (stb at 0x82313170; the player is torn down on it,
// 0x8230B35C) — B+260 from the Bink object, a field of the startup player only.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_BinkPlayer_Open);
REX_EXTERN(__imp__rex_BinkPlayer_Close);
REX_EXTERN(__imp__rex_BinkPlayer_SetDestRect);
REX_EXTERN(__imp__rex_StartupMoviePlayer_Ctor);

namespace {

constexpr uint32_t kDone = 245u;

/// The startup player's skip byte, from its Bink object: P+272 with B = P+12 (`addi r29,r3,0xC`
/// at 0x8230B130).
constexpr uint32_t kStartupPlayerBink = 12u;
constexpr uint32_t kStartupPlayerSkip = 272u;

/// The tracks every open selects: BinkSetSoundTrack(6, {0,1,2,3,4,5}) at 0x82C3D2E8, before
/// BinkOpen, unconditionally.
constexpr uint32_t kTrackIds[6] = {0u, 1u, 2u, 3u, 4u, 5u};

int32_t LoadGuestI32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return static_cast<int32_t>(__builtin_bswap32(value));
}

}  // namespace

// THE OPEN: the source, the sound, and the player live once the open succeeds.
//
// THE HOST PLAYS THE SOUND, from the same decode that produces the picture. The guest's audio
// argument is stated and then passed on as 0, which zeroes all six of the guest's own track
// volumes: without it the movie is heard twice, the guest's copy on its own decode's timing.
// The source is read BEFORE the call, which is free to clobber the argument registers.
REX_EXTERN(rex_BinkPlayer_Open) {
  const uint32_t player = ctx.r3.u32;

  refii_bridge_movie_sound(player, ctx.r5.u32 & 0xFFu, kTrackIds, 6u);
  ctx.r5.u64 = 0;
  refii_bridge_movie_requested(base, ctx.r4.u32);

  __imp__rex_BinkPlayer_Open(ctx, base);

  // 1 on success (li r3,1 at 0x82C3D43C), 0 when BinkOpen returned no handle.
  if (player != 0 && ctx.r3.u32 != 0) {
    refii_bridge_movie_open(player, player + kDone, 0u);
  }
}

// THE CLOSE EVERY PLAYER DIES THROUGH: retired FIRST, so the host stops reading through B before
// the owner frees it.
REX_EXTERN(rex_BinkPlayer_Close) {
  refii_bridge_movie_closed(ctx.r3.u32);
  __imp__rex_BinkPlayer_Close(ctx, base);
}

// THE RECT, as the guest states it: r4 points at four int32 — left, top, right, bottom — which
// the setter converts to floats at B+188..B+200. Every owner states one right after its open.
REX_EXTERN(rex_BinkPlayer_SetDestRect) {
  const uint32_t player = ctx.r3.u32;
  const uint32_t rect = ctx.r4.u32;
  __imp__rex_BinkPlayer_SetDestRect(ctx, base);

  if (player == 0 || rect == 0) {
    return;
  }

  refii_bridge_movie_rect(player, LoadGuestI32(base, rect), LoadGuestI32(base, rect + 4u),
                          LoadGuestI32(base, rect + 8u), LoadGuestI32(base, rect + 12u));
}

// THE STARTUP PLAYER has the one skip byte: stated once its constructor (which opens the movie)
// has returned. A logo is skippable only after P+4 seconds (50 at 0x8230B138; the check is
// `P+8 > P+4` at 0x8231315C), so in practice the guest plays it to its end.
REX_EXTERN(rex_StartupMoviePlayer_Ctor) {
  const uint32_t startup_player = ctx.r3.u32;
  __imp__rex_StartupMoviePlayer_Ctor(ctx, base);

  if (startup_player != 0) {
    refii_bridge_movie_skip_byte(startup_player + kStartupPlayerBink,
                                 startup_player + kStartupPlayerSkip);
  }
}
