#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>

#include "core/payload_bridge.h"

// Where the guest's Bink frame buffers live.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET. The hook that writes this
// (boot_lifecycle.cpp) compiles into librefii_generated.so, while the host bridge that
// reads it links librefii_vfs.so. Putting the state alongside the hook gives each
// library its own copy: the hook fired, the address was stored, and the bridge kept
// reading zero from a different global. Both libraries link librefii_vfs, so defining it
// here gives them one.

namespace refii::vfs {

namespace {

/// Relaxed on purpose. This is a pointer to a long-lived block the guest reuses, and the
/// reader re-reads every field through it each poll — catching the previous value for one
/// frame costs one stale video frame, which is cheaper than synchronising a decode
/// thread against a render loop.
std::atomic<uint32_t> g_bink_frame_buffers{0};

/// The live movie player object, so the host can retire the movie it has finished
/// showing. Cleared once retired: sub_82389210 destroys the player as soon as it sees
/// the flag, so the pointer is good for exactly one write.
std::atomic<uint32_t> g_bink_player{0};

}  // namespace

uint32_t BinkFrameBuffersGuestAddress() {
  return g_bink_frame_buffers.load(std::memory_order_relaxed);
}

void SetBinkFrameBuffersGuestAddress(uint32_t guest_address) {
  g_bink_frame_buffers.store(guest_address, std::memory_order_relaxed);
}

namespace {

/// The movie the guest most recently asked to play, and a counter that changes whenever
/// it does.
///
/// A generation counter rather than a "dirty" flag: the host polls, and a flag would make
/// two requests for the same file in a row indistinguishable from one — which is exactly
/// what the startup sequence does when a logo is replayed.
std::atomic<uint32_t> g_requested_address{0};
std::atomic<uint32_t> g_requested_size{0};
std::atomic<uint32_t> g_requested_swapped{0};
std::atomic<uint32_t> g_requested_generation{0};

/// The path form. Guarded rather than atomic because it is a string; it changes at most
/// once per movie, so the lock is uncontended.
std::mutex g_requested_name_mutex;
std::string g_requested_name;

/// Is this guest memory a plain path rather than a loaded file?
///
/// The title opens the startup logos from a buffer it has already read, but opens later
/// movies BY NAME — the argument is then "game:\\data\\art\\videos\\....bik". Both forms
/// arrive at the same call, so the pointer has to be classified rather than assumed.
bool LooksLikeMoviePath(const uint8_t* data, std::string* out) {
  std::string text;
  for (uint32_t i = 0; i < 260u; ++i) {
    const uint8_t c = data[i];
    if (c == 0) {
      break;
    }
    // Printable ASCII only. A path is single-byte here — the log showed "GAME:\\da" as
    // consecutive bytes, not UTF-16 — so any high byte means this is not a path.
    if (c < 32 || c > 126) {
      return false;
    }
    text.push_back(static_cast<char>(c));
  }

  if (text.size() < 5) {
    return false;
  }

  std::string lowered = text;
  for (char& c : lowered) {
    c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  }

  if (lowered.find(".bik") == std::string::npos) {
    return false;
  }

  *out = std::move(text);
  return true;
}

}  // namespace

void RequestedBinkVideo(uint32_t* out_address, uint32_t* out_size,
                        uint32_t* out_generation, uint32_t* out_swapped) {
  if (out_swapped != nullptr) {
    *out_swapped = g_requested_swapped.load(std::memory_order_relaxed);
  }
  if (out_address != nullptr) {
    *out_address = g_requested_address.load(std::memory_order_relaxed);
  }
  if (out_size != nullptr) {
    *out_size = g_requested_size.load(std::memory_order_relaxed);
  }
  if (out_generation != nullptr) {
    *out_generation = g_requested_generation.load(std::memory_order_acquire);
  }
}

uint32_t RequestedBinkVideoName(char* out, uint32_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_requested_name_mutex);
  const uint32_t length =
      static_cast<uint32_t>(std::min<size_t>(g_requested_name.size(), capacity - 1));
  std::memcpy(out, g_requested_name.data(), length);
  out[length] = '\0';
  return length;
}

/// Records the .bik the guest just handed to BinkOpen.
///
/// The second argument to the movie-open call is NOT a file name — it is a pointer to the
/// already-loaded .bik in guest memory. Reading it as a string produced "iKIB", which is
/// the file's own 'BIKi' magic byte-reversed, and that is what gave it away.
///
/// That is the better seam: the host needs no path mapping, no guess about whether the
/// install has an extra "data" level, and no second read of a file the guest has already
/// paid to load. The length comes from the container's own header — file_size at +4,
/// which the format stores as the total minus the 8 bytes ahead of it.
// C linkage, per core/payload_bridge.h — the caller is a hook inside the payload.
extern "C" void refii_bridge_note_bink_video_requested(uint8_t* base, uint32_t data_guest) {
  if (base == nullptr || data_guest == 0) {
    return;
  }

  const uint8_t* data = base + data_guest;

  // Every movie, not the first few. The startup sequence is four opens deep and the
  // interesting ones are at the END of it — a cap of three hid the fact that the guest
  // switches from passing a buffer to passing a path.
  static std::atomic<uint32_t> probed{0};
  if (probed.fetch_add(1) < 64u) {
    REXLOG_INFO(
        "Refii Bink candidate at 0x{:08X}: {:02X} {:02X} {:02X} {:02X} | "
        "{:02X} {:02X} {:02X} {:02X}  ('{}{}{}{}')",
        data_guest, data[0], data[1], data[2], data[3], data[4], data[5], data[6],
        data[7], (char)(data[0] >= 32 ? data[0] : '.'),
        (char)(data[1] >= 32 ? data[1] : '.'), (char)(data[2] >= 32 ? data[2] : '.'),
        (char)(data[3] >= 32 ? data[3] : '.'));
  }

  // Only accept something that really is a Bink 1 file: this pointer is taken on trust
  // from a register, and a wrong guess would have the host copying arbitrary guest memory.
  //
  // Both byte orders are accepted. The guest is big-endian and the .bik is little-endian
  // data loaded verbatim, so which way round 'BIKi' sits in memory depends on how the
  // loader moved it — and guessing wrong silently rejects every movie.
  const bool forward = data[0] == 'B' && data[1] == 'I' && data[2] == 'K';
  const bool reversed = data[3] == 'B' && data[2] == 'I' && data[1] == 'K';
  if (!forward && !reversed) {
    // Not a loaded file — the title also opens movies by NAME, and every movie after the
    // startup logos arrives that way. Publishing the path lets the host resolve it
    // through the same videos folder it already indexes.
    std::string path;
    if (!LooksLikeMoviePath(data, &path)) {
      return;
    }

    {
      std::lock_guard<std::mutex> lock(g_requested_name_mutex);
      g_requested_name = path;
    }

    // No buffer for this one: the host reads the file itself.
    g_requested_address.store(0, std::memory_order_relaxed);
    g_requested_size.store(0, std::memory_order_relaxed);
    g_requested_swapped.store(0, std::memory_order_relaxed);

    static std::atomic<uint32_t> named{0};
    if (named.fetch_add(1) < 64u) {
      REXLOG_INFO("Refii Bink requested by name: {}", path);
    }

    g_requested_generation.fetch_add(1, std::memory_order_release);
    return;
  }

  const uint32_t stated =
      forward ? ((uint32_t)data[4] | ((uint32_t)data[5] << 8) |
                 ((uint32_t)data[6] << 16) | ((uint32_t)data[7] << 24))
              : ((uint32_t)data[7] | ((uint32_t)data[6] << 8) |
                 ((uint32_t)data[5] << 16) | ((uint32_t)data[4] << 24));
  const uint64_t size = (uint64_t)stated + 8u;
  if (size < 64 || size > 512u * 1024u * 1024u) {
    return;
  }

  static std::atomic<uint32_t> seen{0};
  if (seen.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii Bink requested: data=0x{:08X} size={}", data_guest,
                (uint32_t)size);
  }

  {
    std::lock_guard<std::mutex> lock(g_requested_name_mutex);
    g_requested_name.clear();
  }

  g_requested_address.store(data_guest, std::memory_order_relaxed);
  g_requested_size.store((uint32_t)size, std::memory_order_relaxed);
  g_requested_swapped.store(reversed ? 1u : 0u, std::memory_order_relaxed);

  // Published last, so a reader that sees the new generation also sees the buffer it
  // describes.
  g_requested_generation.fetch_add(1, std::memory_order_release);
}

/// Is the guest still playing the movie it opened?
///
/// THE BASE. Everything here is relative to what sub_82A41B48 receives in r3, which is
/// NOT the movie player: sub_82389040 allocates a 248-byte player P and calls
/// sub_82A41B48(P + 12, ...) at 0x8238910C. So the pointer we record — call it B — is the
/// embedded Bink wrapper at P+12.
///
/// Getting that wrong is not a subtle error. Reading the skip flag as B+244 lands at
/// P+256, EIGHT BYTES PAST THE END of the allocation, in heap metadata — which read
/// nonzero immediately and reported every movie as skipped the instant it opened. The
/// framebuffer address is genuinely B+68 (sub_82A42D20 does `v2 = a1 + 68` on this same
/// base), which is why that path has always worked and masked the mistake.
///
/// B-relative, all IDA-proven:
///   B-8   float  minimum play time   (P+4,  5.0 for the logos, 0.0 for attract)
///   B-4   float  elapsed             (P+8,  accumulated at 0x82389238)
///   B+64  dword  HBINK               (stored at 0x82A41BFC)
///   B+221 byte   DONE                (set at 0x82A42CF0 when FrameNum == Frames)
///   B+232 byte   SKIP                (set at 0x82377E04 / 0x826E3064 / 0x826E35D8)
///
/// Both flags are zeroed by the constructor (0x823890C4 and 0x823890D4), so a recycled
/// allocation — and the block IS recycled, LIFO out of a 248-byte free list — cannot
/// carry a stale flag.

/// Only the SKIP flag counts, and the distinction matters.
///
/// B+232 (SKIP) is set when the movie is abandoned — a button press, gated on elapsed >
/// minimum play time (0x82377E04 for the logo sequence, 0x826E3064 and 0x826E35D8 for
/// attract). That is a decision about what the player watches, so the host follows it.
/// All three write literally 1, and nothing but the constructors ever clears it.
///
/// B+221 (DONE) is NOT usable here. The guest sets it when its own decode reaches the
/// last frame, and that decode is invisible work on its own clock — it can finish long
/// before the picture does. It is also the byte the host writes to retire a movie, so
/// treating it as "the guest ended this" would make the host stop the moment it asked the
/// guest to advance.
///
/// The two are disjoint by construction: the decode path never touches SKIP and the input
/// handlers never touch DONE, so SKIP alone means "the user abandoned it" with no false
/// positive from a movie simply ending.
///
/// Without this the host kept showing a movie the guest had already walked away from: the
/// attract clip ran a further 30 seconds over the top of the main menu the guest had
/// moved on to.
///
/// Returns 0 when no movie is live, 1 while the guest still wants it, 2 once the guest
/// has ended it.
/// The destination rect the guest stated for the live movie, in its own 2D screen space —
/// fullscreen vs embedded, stated at open, not inferred. `sub_82A41E30(bink, rect, z,
/// rotation, ...)` stores it as four big-endian floats measured from the bink player:
///   bink+8 x0, bink+12 y0, bink+16 x1, bink+20 y1 (bink+24 z, bink+28 rotation)
/// and the draw `sub_82A41F10` builds its quad corners from those four. Returns 0 when no
/// movie is live.
uint32_t BinkMovieDestRect(uint8_t* base, float* out_x0, float* out_y0,
                           float* out_x1, float* out_y1) {
  if (base == nullptr) {
    return 0;
  }

  const uint32_t player = g_bink_player.load(std::memory_order_relaxed);
  if (player == 0) {
    return 0;
  }

  const auto read_float = [base, player](uint32_t offset) {
    uint32_t word = 0;
    std::memcpy(&word, base + player + offset, sizeof(word));
    word = __builtin_bswap32(word);
    float value = 0.0f;
    std::memcpy(&value, &word, sizeof(value));
    return value;
  };

  if (out_x0 != nullptr) *out_x0 = read_float(8u);
  if (out_y0 != nullptr) *out_y0 = read_float(12u);
  if (out_x1 != nullptr) *out_x1 = read_float(16u);
  if (out_y1 != nullptr) *out_y1 = read_float(20u);
  return 1;
}

uint32_t BinkMovieState(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }

  const uint32_t player = g_bink_player.load(std::memory_order_relaxed);
  if (player == 0) {
    return 0;
  }

  // Reading a player the guest may have freed is safe — it is arena memory that stays
  // mapped — and a stale byte only costs one poll, because opening the next movie
  // republishes the pointer.
  const uint8_t done = base[player + 221u];
  const uint8_t skip = base[player + 232u];

  // Rate-limited raw dump. The skip flag reads nonzero for every movie AFTER the first,
  // which the constructor's zeroing at 0x823890D4 says is impossible — so one of the base,
  // the offset, or the lifetime assumption is wrong, and guessing which has already cost
  // two rounds. Print what is actually there.
  static std::atomic<uint32_t> polls{0};
  const uint32_t poll = polls.fetch_add(1, std::memory_order_relaxed);
  if (poll < 40u || (poll % 512u) == 0u) {
    REXLOG_INFO("Refii Bink poll #{} bink=0x{:08X} done(+221)={} skip(+232)={} "
                "raw[216..236]={:02X} {:02X} {:02X} {:02X} {:02X} {:02X} {:02X} {:02X} "
                "{:02X} {:02X} {:02X} {:02X} {:02X} {:02X} {:02X} {:02X} {:02X} {:02X} "
                "{:02X} {:02X} {:02X}",
                poll, player, done, skip,
                base[player + 216u], base[player + 217u], base[player + 218u],
                base[player + 219u], base[player + 220u], base[player + 221u],
                base[player + 222u], base[player + 223u], base[player + 224u],
                base[player + 225u], base[player + 226u], base[player + 227u],
                base[player + 228u], base[player + 229u], base[player + 230u],
                base[player + 231u], base[player + 232u], base[player + 233u],
                base[player + 234u], base[player + 235u], base[player + 236u]);
  }

  // THE SKIP BYTE IS REAL, BUT IT IS NOT ALWAYS THERE.
  //
  // Our handle is the BINK object `B`, and `B = P + 12` where `P` is the outer movie player
  // — verified in the constructor `sub_82389040`, which splits the two registers explicitly:
  //
  //     82389068  addi r31, r29, 0xC    ; r29 = P, r31 = B
  //     82389070  lfs  f0, flt_8209BE04 ; the skip threshold
  //     82389080  stfs f0, 4(r29)       ; P+4  = threshold seconds
  //     82389088  stfs f31, 8(r29)      ; P+8  = elapsed
  //
  // So `B+232` is `P+244`, and `P+244` IS the guest's own skip flag — the byte written by
  // every one of its skip sites, e.g. `0x826E3064` on the title screen's A press. It is NOT
  // `P+256` measured from this base — that is four bytes past the end of a 248-byte block.
  //
  // The catch, and the whole reason for the class test below: **only the front-end players
  // are 248 bytes.** The in-game cutscene player (`sub_823C1658`) is 228 and has no skip
  // field at all — reading `B+232` there is off the end of the block, and WRITING it would
  // corrupt coalescing-heap metadata. That, plus a stale `B` after the guest frees a player,
  // is the explanation for the non-zero-after-the-first-movie reading noted above.
  //
  // So the class is read first, from a value the guest itself states.
  uint32_t flags = 0;
  std::memcpy(&flags, base + player + 224u, sizeof(flags));
  flags = __builtin_bswap32(flags);

  // The flags dword the guest hands BinkOpen, set by whichever constructor built the player.
  // A stated value, not an address or a filename — which is what lets the host tell the
  // three shapes apart without a whitelist.
  [[maybe_unused]] constexpr uint32_t kStartupQueue = 0x04000400u;  // sub_82389040
  [[maybe_unused]] constexpr uint32_t kAttract = 0x00000400u;       // sub_82388ED0
  [[maybe_unused]] constexpr uint32_t kCutscene = 0x00004400u;      // sub_823C1658

  // THE OWNER BYTE DECIDES, NOT THE FLAGS. The opener `sub_82A41B48` stores its fourth
  // argument at B+223 (`stb r6, 0xDF(r30)`, 0x82A41B64): 0 from the two front-end wrappers
  // (startup queue 0x82389100, attract 0x82388F98 — 248-byte players carrying the skip flag
  // at B+232), 1 from the standalone 228-byte objects (cutscene 0x823C19D0, and the UI
  // sprite's movie item 0x82A83E88 — the ability preview) which END four bytes before +232.
  // The flags word cannot tell attract from the UI item: both open with 0x400, and reading
  // +232 on the item returned a heap neighbour and skipped the preview 11 ms in.
  const bool has_skip_field = base[player + 223u] == 0u;
  if (!has_skip_field) {
    // Cutscene, or a shape we do not recognise. Report playing and touch nothing else: an
    // unrecognised flags value is exactly when reading past a smaller block would happen.
    return 1u;
  }

  // 2 == the GUEST has decided this movie is over early. It sets this itself, from its own
  // gate `P+8 > P+4` (elapsed past the skip threshold) when the player presses A — which is
  // why the host must NOT decide skippability: feed the input, and the guest enforces
  // "unskippable" for free.
  return skip != 0 ? 2u : 1u;
}

/// Retires the movie the host has finished showing, so the guest advances with it.
///
/// IDA-proven. The startup sequence is a BLOCKING loop (sub_82377A00) whose only exit is
/// every movie having been played; it decides a movie is over inside
/// Render_DrawBinkMovieFrame (0x82A41F10) by comparing the guest's own
/// `hbink->FrameNum == hbink->Frames` at 0x82A42CD8-0x82A42CE4, and marks it by storing 1
/// to the done byte at 0x82A42CF0. Nothing GPU-side takes part in that decision.
///
/// Which is the problem: Godot is showing the movie, so the guest's decode is work whose
/// output nobody sees, and it runs on its own clock — the two drift, and the guest sat on
/// the logos for 45 seconds while the host had shown them in 8. Writing the same byte the
/// guest writes puts the sequence back under the clock the player is actually watching.
///
/// The byte is B+221 — the one Render_DrawBinkMovieFrame itself writes at 0x82A42CF0.
/// sub_82389210 tests the same byte as 0xE9 off the PLAYER, and 221 + 12 = 233 confirms
/// the two bases line up.
///
/// Returns the player it retired, or 0 when no movie is live.
uint32_t FinishCurrentBinkMovie(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }

  // Taken, not read: sub_82389210 frees the player the moment it sees the flag, so this
  // pointer is good for exactly one write and a second would land in freed memory.
  const uint32_t player = g_bink_player.exchange(0, std::memory_order_relaxed);
  if (player == 0) {
    return 0;
  }

  // A single byte, so endianness does not enter into it.
  base[player + 221u] = 1u;

  static std::atomic<uint32_t> retired{0};
  if (retired.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii Bink retired by host: bink=0x{:08X} player=0x{:08X} (done at +221)",
                player, player - 12u);
  }

  return player;
}

/// Called from the Bink CLOSE hook (`sub_82A41B00`) with the same BINK handle the open
/// published.
///
/// THE PLAYER DIES A FEW INSTRUCTIONS LATER. `sub_82389210` calls BinkClose at 0x8238930C,
/// frees the 248-byte player at 0x82389314 (`Render_FreeD3DObject`) and nulls the manager's
/// slot at 0x82389318. `BinkMovieDestRect` and `BinkMovieState` both read straight off
/// `g_bink_player` with no validity test beyond "non-zero", so a handle that outlives the
/// object hands the host freed heap — and freed heap read as a destination rect is a movie
/// the host then believes is not fullscreen.
///
/// Compare-exchange rather than a store: a close arriving for a player that is no longer the
/// live one must not clear a NEWER movie's publish. The startup sequence builds one player per
/// movie (a fresh `Render_AllocD3DObject(0xF8)` at 0x82377CB0 for each of the two logos), and
/// the allocator hands back the same block, so "the same address" is not "the same movie" —
/// only the current publish may be cleared.
extern "C" void refii_bridge_note_bink_movie_closed(uint32_t player) {
  if (player == 0) {
    return;
  }

  uint32_t expected = player;
  g_bink_player.compare_exchange_strong(expected, 0u, std::memory_order_relaxed);
}

/// Called from the Bink movie-open hook with the player pointer.
///
/// BINKFRAMEBUFFERS is inline at player+68 — sub_82A42D20 does
/// `v2 = a1 + 68; memset(a1 + 68, 0, 0x78); BinkGetFrameBuffersInfo(*(a1 + 64), v2)`.
extern "C" void refii_bridge_note_bink_movie_open(uint32_t player, uint8_t* base_for_open) {
  if (player == 0) {
    return;
  }

  g_bink_player.store(player, std::memory_order_relaxed);

  static std::atomic<uint32_t> seen{0};
  if (seen.fetch_add(1) < 64u) {
    // The CLASS is logged too, because it decides whether the skip byte beside it is even in
    // bounds: 0x04000400 startup queue, 0x00000400 attract, 0x00004400 in-game cutscene (a
    // 228-byte player with no skip field). A value that is none of those means the host is
    // looking at a shape it does not know, and every offset past +224 is then a guess.
    uint32_t open_flags = 0;
    if (base_for_open != nullptr) {
        std::memcpy(&open_flags, base_for_open + player + 224u, sizeof(open_flags));
        open_flags = __builtin_bswap32(open_flags);
    }

    // +223 is the owner byte the opener stores (0 front-end wrapper with a skip field at
    // +232, 1 standalone 228-byte object with none); +232 is only in bounds for the former.
    const uint8_t owner = base_for_open == nullptr ? 0xFFu : base_for_open[player + 223u];
    REXLOG_INFO("Refii Bink movie open: bink=0x{:08X} player=0x{:08X} frame_buffers=0x{:08X} "
                "class(+224)=0x{:08X} owner(+223)={} sound(+222)={} at-open done(+221)={} skip(+232)={}",
                player, player - 12u, player + 68u, open_flags, owner,
                base_for_open == nullptr ? 0xFFu : base_for_open[player + 222u],
                base_for_open == nullptr ? 0xFFu : base_for_open[player + 221u],
                owner == 0u ? base_for_open[player + 232u] : 0xFFu);
  }

  SetBinkFrameBuffersGuestAddress(player + 68u);
}

}  // namespace refii::vfs
