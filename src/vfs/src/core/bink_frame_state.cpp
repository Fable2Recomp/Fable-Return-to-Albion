#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>

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
void NoteBinkVideoRequested(uint8_t* base, uint32_t data_guest) {
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
/// The player object is at `bink - 12` (sub_82389210 passes `player + 12` to the draw at
/// 0x82389260), so the done byte the guest writes as bink+221 is player+233 — the same
/// offset sub_82389210 tests at 0x82389230 before skipping the draw and destroying the
/// player.
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
  base[player + 233u] = 1u;

  static std::atomic<uint32_t> retired{0};
  if (retired.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii Bink retired by host: player=0x{:08X} (done byte at +233)", player);
  }

  return player;
}

/// Called from the Bink movie-open hook with the player pointer.
///
/// BINKFRAMEBUFFERS is inline at player+68 — sub_82A42D20 does
/// `v2 = a1 + 68; memset(a1 + 68, 0, 0x78); BinkGetFrameBuffersInfo(*(a1 + 64), v2)`.
void NoteBinkMovieOpen(uint32_t player) {
  if (player == 0) {
    return;
  }

  g_bink_player.store(player, std::memory_order_relaxed);

  static std::atomic<uint32_t> seen{0};
  if (seen.fetch_add(1) < 64u) {
    REXLOG_INFO("Refii Bink movie open: player=0x{:08X} frame_buffers=0x{:08X}", player,
                player + 68u);
  }

  SetBinkFrameBuffersGuestAddress(player + 68u);
}

}  // namespace refii::vfs
