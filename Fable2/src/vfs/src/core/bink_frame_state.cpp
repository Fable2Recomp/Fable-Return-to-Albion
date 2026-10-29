#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>

#include "core/payload_bridge_fable2.h"
#include "core/guest_build.h"

// Fable 2's Bink players, stated into the shared movie state (src/runtime, core/movie_state.h).
//
// The movie the guest wants — its request, sound, rect, and the bytes that end it — is the same
// contract for every title and lives in refii_runtime. What is Fable 2's is HOW its players say
// it: the offsets of the done and skip bytes, the owner byte that says whether a player has a
// skip byte at all, and the Bink statics its cutscene player writes the track selection into.
// The Bink hooks (boot_lifecycle.cpp, frontend_video_handoff.cpp) call the entries below, which
// read those and state the shared calls.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET, for the frame-buffer address: the
// hook that writes it compiles into librefii_generated.so while the host bridge that reads it
// links librefii_vfs.so, and both link librefii_vfs.

namespace refii::vfs {

namespace {

/// Relaxed on purpose. This is a pointer to a long-lived block the guest reuses, and the
/// reader re-reads every field through it each poll — catching the previous value for one
/// frame costs one stale video frame, which is cheaper than synchronising a decode
/// thread against a render loop.
std::atomic<uint32_t> g_bink_frame_buffers{0};

}  // namespace

uint32_t BinkFrameBuffersGuestAddress() {
  return g_bink_frame_buffers.load(std::memory_order_relaxed);
}

void SetBinkFrameBuffersGuestAddress(uint32_t guest_address) {
  g_bink_frame_buffers.store(guest_address, std::memory_order_relaxed);
}

}  // namespace refii::vfs

namespace {

// ---------------------------------------------------------------------------------
// The movie's SOUND, as the guest states it
// ---------------------------------------------------------------------------------
//
// Track selection is `BinkSetSoundTrack(count, ids)`, which the title's Bink build inlines:
// its arguments exist only as Bink's own statics, written by the caller immediately before
// the open — the cutscene player `sub_823C1658` stores `dword_834FC4C0 = 0`,
// `dword_834FC4B8 = 2`, `dword_834FC4C4 = *(movie+12)` at 0x823C1990..0x823C1998 and ORs
// BINKSNDTRACK (0x4000) into the open flags at 0x823C19A4. `BinkOpen` (`sub_832BB4B8`)
// honours them only under that flag; without it 0x832BB4F8 resets them to one track, id 0.
//
// The voice id is chosen by `sub_823C0D90`: 1, or 2 for a gendered native when the hero's
// gender component reads 1, then 3/4 in place of 1/2 when the current language is not
// "en-uk" (0x823C1308..0x823C1350). Track 0 is the music and effects bed.
//
// Volumes arrive after the open through `sub_82A41D88(B, id, volume)`; Bink's own default
// for a track nobody set is full.
constexpr uint32_t kBinkSoundTrackFlag = 0x4000u;
constexpr refii::guest::Addr kBinkTrackCount{0x834FC4B8u};
constexpr refii::guest::Addr kBinkTrackIds{0x834FC4C0u};
constexpr uint32_t kMaxStatedTracks = 8u;

uint32_t ReadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

// THE BASE. Everything below is relative to what sub_82A41B48 receives in r3, which is NOT the
// movie player: sub_82389040 allocates a 248-byte player P and calls sub_82A41B48(P + 12, ...)
// at 0x8238910C. So the pointer the hooks hand over — call it B — is the embedded Bink wrapper
// at P+12.
//
// Getting that wrong is not a subtle error. Reading the skip flag as B+244 lands at P+256,
// EIGHT BYTES PAST THE END of the allocation, in heap metadata — which read nonzero immediately
// and reported every movie as skipped the instant it opened. The framebuffer address is
// genuinely B+68 (sub_82A42D20 does `v2 = a1 + 68` on this same base), which is why that path
// has always worked and masked the mistake.
//
// B-relative, all IDA-proven:
//   B+64  dword  HBINK               (stored at 0x82A41BFC)
//   B+68  BINKFRAMEBUFFERS, inline   (sub_82A42D20: `memset(a1 + 68, 0, 0x78)`)
//   B+221 byte   DONE                (set at 0x82A42CF0 when FrameNum == Frames)
//   B+223 byte   OWNER               (the opener's 4th argument, `stb r6, 0xDF(r30)` 0x82A41B64)
//   B+224 dword  open flags
//   B+232 byte   SKIP                (set at 0x82377E04 / 0x826E3064 / 0x826E35D8)
//
// ONLY SKIP SAYS THE GUEST ENDED IT. B+232 is set when the movie is abandoned — a button press,
// gated on elapsed > minimum play time — and nothing but the constructors clears it. B+221 is
// set by the guest's own decode on its own clock, and is also the byte the host writes to
// retire a movie, so it is the done byte, not a skip.
//
// THE OWNER BYTE DECIDES WHETHER B+232 EXISTS: 0 from the two front-end wrappers (startup queue
// 0x82389100, attract 0x82388F98 — 248-byte players carrying the skip flag), 1 from the
// standalone 228-byte objects (cutscene 0x823C19D0, the UI sprite's movie item 0x82A83E88) which
// END four bytes before +232.
constexpr uint32_t kDone = 221u;
constexpr uint32_t kOwner = 223u;
constexpr uint32_t kOpenFlags = 224u;
constexpr uint32_t kSkip = 232u;
constexpr uint32_t kFrameBuffers = 68u;

}  // namespace

/// The open hook, before the call: the sound the guest asked for and the tracks it selected.
extern "C" void refii_bridge_note_bink_sound(uint8_t* base, uint32_t player, uint32_t sound) {
  if (base == nullptr || player == 0) {
    return;
  }

  uint32_t ids[kMaxStatedTracks] = {};
  uint32_t count = 1;
  if ((ReadGuestU32(base, player + kOpenFlags) & kBinkSoundTrackFlag) != 0) {
    count = std::min(ReadGuestU32(base, kBinkTrackCount), kMaxStatedTracks);
    for (uint32_t i = 0; i < count; ++i) {
      ids[i] = ReadGuestU32(base, kBinkTrackIds + (i * 4u));
    }
  }

  refii_bridge_movie_sound(player, sound, ids, count);
}

extern "C" void refii_bridge_note_bink_volume(uint32_t player, uint32_t track_id, float volume) {
  refii_bridge_movie_volume(player, track_id, volume);
}

extern "C" void refii_bridge_note_bink_video_requested(uint8_t* base, uint32_t data_guest) {
  refii_bridge_movie_requested(base, data_guest);
}

/// The open hook, after the call: the player is live, with its done byte, and its skip byte
/// when its class has one.
extern "C" void refii_bridge_note_bink_movie_open(uint32_t player, uint8_t* base_for_open) {
  if (player == 0) {
    return;
  }

  const uint8_t owner = base_for_open == nullptr ? 0xFFu : base_for_open[player + kOwner];
  const uint32_t skip = owner == 0u ? player + kSkip : 0u;
  refii_bridge_movie_open(player, player + kDone, skip);

  static std::atomic<uint32_t> seen{0};
  if (seen.fetch_add(1) < 64u && base_for_open != nullptr) {
    // The CLASS is logged too, because it decides whether the skip byte beside it is even in
    // bounds: 0x04000400 startup queue, 0x00000400 attract, 0x00004400 in-game cutscene (a
    // 228-byte player with no skip field). A value that is none of those means the host is
    // looking at a shape it does not know, and every offset past +224 is then a guess.
    REXLOG_INFO("Refii Bink movie open: bink=0x{:08X} player=0x{:08X} frame_buffers=0x{:08X} "
                "class(+224)=0x{:08X} owner(+223)={}",
                player, player - 12u, player + kFrameBuffers,
                ReadGuestU32(base_for_open, player + kOpenFlags), owner);
  }

  refii::vfs::SetBinkFrameBuffersGuestAddress(player + kFrameBuffers);
}

/// The close hooks, first thing: the player is freed a few instructions later
/// (`sub_82389210`: close at 0x8238930C, free at 0x82389314), so the host stops reading
/// through it now.
extern "C" void refii_bridge_note_bink_movie_closed(uint32_t player) {
  refii_bridge_movie_closed(player);
}

/// The `sub_82A41E30` hook: the rect the guest stated for `player`.
extern "C" void refii_bridge_note_bink_movie_rect(uint32_t player, int32_t left, int32_t top,
                                                  int32_t right, int32_t bottom) {
  refii_bridge_movie_rect(player, left, top, right, bottom);
}
