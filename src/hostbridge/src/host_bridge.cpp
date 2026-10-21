// refii - host bridge
//
// A C ABI over the guest runtime, so Godot can host the recompiled guest IN ITS OWN
// PROCESS rather than spawning `refii` as a child.
//
// This is deliberately the thinnest possible surface. The first thing it has to answer
// is not "can the guest run" but "can the guest's ADDRESS SPACE exist here at all" —
// everything else is downstream of that, and if the answer is no the whole in-process
// approach is dead and we should find out in seconds rather than after wiring a window,
// an input pump and a frame loop.

#include <algorithm>
#include "core/guest_build.h"
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <csignal>
#ifndef _WIN32
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <setjmp.h>
#include <ucontext.h>
#include <unistd.h>
#include <ctime>
#endif
#include <cstring>
#include <span>
#include <exception>
#include <filesystem>
#include <fstream>
#include <set>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>

#include <SDL3/SDL_events.h>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <dbghelp.h>
#endif

#include <achievement/achievement_runtime.h>
#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>

namespace rex {
/// Defined in the runtime's logging.cpp and not declared by its header; the crash reporters
/// need it, since a record left in spdlog's queue when the process dies is never written.
void FlushLogging();
}  // namespace rex
#include <rex/system/kernel_state.h>

#include "core/host_asset_reader.h"
#include "core/precise_sleep.h"
#include "core/payload_bridge.h"
#include "core/property_condition.h"
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

// Runtime introspection for the debug channel — see the ReXGlue section beside RefiiHostPeek.
#include <rex/filesystem/entry.h>
#include <rex/filesystem/vfs.h>
#include <rex/perf/counter.h>
#include <rex/runtime.h>
#include <rex/system/thread_state.h>
#include <rex/system/xsocket.h>
#include <rex/system/xthread.h>

#include <array>
#include <set>
#include <utility>
#ifdef REXGLUE_ENABLE_PROFILING
#include <tracy/Tracy.hpp>
#include <tracy/TracyC.h>
#endif

#include "../../host_input.h"

#include "host_abi.h"
#include "host_tracy.h"
#include "core/traced_mutex.h"

// Included rather than redeclared like the Bink entries below: the audio exports marshal
// VoiceRecord by value, and two hand-written copies of a struct layout is exactly the
// kind of thing that goes wrong silently.
#include "core/audio_state.h"
#include "core/hero_derived.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"

// The alternate signal stack lives in the vfs library behind a C entry point since the
// payload-bridge change (adarec1994, 3f9784ae): `EnsureAlternateSignalStack` is now an inline
// wrapper there, so this library — a separate .so — calls the entry point itself.
extern "C" void refii_bridge_ensure_alternate_signal_stack(void);
extern "C" uint32_t refii_bridge_guest_frame(void);

namespace refii::vfs {
// Defined in src/vfs/src/core/bink_frame_state.cpp.
uint32_t BinkFrameBuffersGuestAddress();
void RequestedBinkVideo(uint32_t* out_address, uint32_t* out_size,
                        uint32_t* out_generation, uint32_t* out_swapped);
uint32_t RequestedBinkVideoName(char* out, uint32_t capacity);
uint32_t FinishCurrentBinkMovie(uint8_t* base);
uint32_t BinkMovieState(uint8_t* base);
uint32_t RequestedBinkSound(uint32_t* out_sound, uint32_t* out_ids, float* out_volumes,
                            uint32_t capacity);
uint32_t BinkMovieDestRect(uint8_t* base, float* out_x0, float* out_y0,
                           float* out_x1, float* out_y1);

// Defined in src/vfs/src/core/guest_tick_pump_state.cpp — the ONLY route from this thread onto
// the guest's own tick thread. See src/vfs/src/hooks/guest_tick_pump.cpp for why a memory
// write cannot do this job, and that state file for why the queue is in refii_vfs and not
// beside the pump hook that drains it.
uint32_t PostGuestTickAction(const char* name);
// src/vfs/src/core/mouse_look_state.cpp — the mouse's camera turn, posted here and taken by
// the payload before each camera update (src/vfs/src/hooks/mouse_look.cpp).
void MouseLookPost(float yaw_degrees, float pitch_degrees);
void MouseLookSetDevice(bool mouse);
uint32_t MouseLookState(uint32_t* out_tick, double* out_posted, double* out_taken,
                        double* out_absorbed, float* out_factor, float* out_pivot,
                        int32_t* out_mode, uint32_t* out_flicks);
uint64_t NewGameTransitionResult(uint32_t* generation);
uint32_t GuestTickPumpHeroStateApplied(uint32_t player);
uint32_t DebugCallResult(uint64_t* out_seq, uint32_t* out_addr, uint64_t* out_r3,
                         char* out_note, uint32_t note_capacity);
void GuestTickPumpCounters(uint64_t* out_ticks, uint64_t* out_drained,
                           uint32_t* out_queued);
int32_t GuestSavedGameCount();
void RefreshGuestSavedGameCount();
void FinishStuckInGameLoadingScreenFromHost(uint8_t* base);
int32_t FrontEndSubtitles();

/// Which pause tabs the guest's own predicate said it would offer, sampled on the GUI
/// tick. Bit N = tab N available, N in 2..8; 0 means not sampled yet.
uint32_t PauseTabsMask();

// Defined in src/vfs/src/core/stated_fx_state.cpp and
// src/vfs/src/core/target_highlight_state.cpp —
// what the guest's own setters stated, recorded by the hooks in the payload. Ordinary C++
// symbols because both ends of THIS call are our build; the payload's end crosses through the
// C entries in core/payload_bridge.h. These were dlsym'd out of the payload until 2026-09-18,
// which cannot compile on Windows and was never right on Linux either.
uint32_t StatedFxCount();
uint32_t StatedFxTransforms(uint32_t* out_words, uint32_t word_capacity);
uint32_t StatedHighlightFor(uint32_t component);
// src/vfs/src/core/entity_appearance_state.cpp — `out` takes kAppearanceWords words.
bool StatedAppearanceFor(uint32_t component, uint32_t* out);
// src/vfs/src/core/dye_state.cpp.
uint32_t SampledDyeStates(uint32_t* out, uint32_t word_capacity);
// src/vfs/src/core/part_morph_state.cpp.
uint32_t StatedPartMorphs(uint32_t entity, uint32_t* out, uint32_t capacity,
                          uint32_t* out_generation, uint64_t* out_statements);
// src/vfs/src/core/texture_layer_state.cpp.
uint32_t StatedTextureLayers(uint32_t entity, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation, uint64_t* out_statements);
// src/vfs/src/core/decal_state.cpp.
uint32_t StatedDecals(uint32_t* out, uint32_t capacity, uint32_t* out_serial);
uint32_t StatedFxMeshes(uint32_t* out, uint32_t capacity);
// src/vfs/src/core/item_dye_layer_state.cpp.
uint32_t StatedItemDyeLayers(uint32_t record, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation);

// Defined in src/vfs/src/core/pubgame_state.cpp — the pub-game seam. The host posts what
// Godot's game said and the pump replacement in the payload drains it on the guest's render
// thread; the events come back the other way.
void ClaimPubGames(bool claimed);
void PostPubGameCommand(uint32_t kind, const char* text, const double* numbers,
                        const int64_t* integers);
uint32_t TakePubGameEvents();

// The characters behind an LHString handle — two hops, see core/guest_state.h. Declared, not
// included: this file cannot see the vfs hook headers that header pulls in.

// The captured menu rows — see RefiiHostGetMenuRow. The host reads and the payload writes, so
// the store lives with the pump's other state, not beside the hook. Capacities are size_t to
// match the definition exactly; widening them here would be a silent ODR mismatch.
uint32_t MenuRowLabel(uint32_t index, char* out_label, size_t label_capacity);

/// The screen the published capture belongs to — the key that says whose population it is.
uint32_t MenuScreenId();
uint32_t MenuRows(uint32_t index, char* out_name, size_t name_capacity, char* out_parent,
                  size_t parent_capacity, uint32_t* out_id, int32_t* out_event_type,
                  uint64_t* out_generation, int32_t* out_rating);
}  // namespace refii::vfs
#include "refii_host.h"

#include "gui/gui_native_calls.h"
#include "gui/gui_display_box.h"
#include "gui/gui_menu_chunks.h"
#include "gui/gui_element_state.h"

namespace {
/// Guest memory, or nullptr when there is no guest — the check `rex::system::kernel_memory`
/// does not make. Its accessor is `kernel_state()->memory()` with no null test, so with no

/// kernel state the arrow faults inside the accessor, before any `memory == nullptr` guard in
/// this file can run. Every call in this file goes through here.
inline rex::memory::Memory* SafeKernelMemory() {
    rex::system::KernelState* state = rex::system::kernel_state();
    return state != nullptr ? state->memory() : nullptr;
}

// Copies into the caller's buffer and returns the length that WOULD have been written,
// so a caller can size a buffer with a null probe. Mirrors the convention the launcher

// ops ABI already uses.
int WriteOut(const std::string& text, char* out, std::size_t capacity) {
    const int needed = static_cast<int>(text.size());
    if (out != nullptr && capacity > 0) {
        const std::size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
        std::memcpy(out, text.data(), copied);
        out[copied] = '\0';
    }
    return needed;
}

/// The guest, once started. One per process — the runtime holds global state (the
/// memory map, the kernel, the MMIO handler), so a second boot in the same process is
/// not a thing that can work, and saying so is better than letting it half-run.
struct GuestSession {
    std::thread thread;
    std::atomic_bool running{false};
    std::atomic_bool started{false};
    std::atomic_int exit_code{-1};

    std::mutex status_mutex;
    std::string status{"not started"};

    void SetStatus(std::string text) {
        std::lock_guard<std::mutex> guard(status_mutex);
        status = std::move(text);
    }

    std::string GetStatus() {
        std::lock_guard<std::mutex> guard(status_mutex);
        return status;
    }
};

GuestSession& Session() {
    static GuestSession session;
    return session;
}

/// The guest when GODOT drives it, rather than the guest driving itself.
///
/// The difference matters. RunRefiiEmbedded owns a blocking SDL loop — on netplay,
/// SDL_WaitEvent — so hosting it means handing a thread over and getting it back when
/// the guest decides. Here the host keeps its thread: it initialises the guest once,
/// then pumps it a slice at a time from its own frame loop.

/// How long shutdown pumps the UI thread waiting for the guest to stop.
///
/// Generous on purpose: this runs once, when the player closes the game, and the
/// alternative to waiting is abandoning the runtime for the rest of the process.
constexpr std::chrono::seconds kShutdownPumpBudget{15};

struct HostedGuest {
    std::unique_ptr<rex::ui::SDLWindowedAppContext> context;
    std::unique_ptr<rex::ui::WindowedApp> app;
    bool initialized = false;
    bool finished = false;

    std::string status{"not initialized"};
};

HostedGuest& Hosted() {
    static HostedGuest hosted;
    return hosted;
}

/// Guest memory is not always THERE on Windows.
///
/// The guest space is RESERVED up front, committed as the guest touches it, and decommitted
/// as the guest frees it. Linux backs an untouched page with zeroes on read; Windows faults on
/// reserved-but-uncommitted memory, so a host peek at a global the guest has not written yet,
/// or through a pointer chain the guest has since torn down (a level change frees the old
/// "3D Engine" under `RefiiHostGetRenderFence`), takes the whole process down inside a
/// P/Invoke. Every host-side read of guest memory goes through here.
///
/// SEH rather than VirtualQuery-then-read: a query per read is a syscall the entity and UI
/// walks pay thousands of times a frame, and it still races the guest's own decommit between
/// the query and the read. A `__try` costs nothing on the path that does not fault.
///
/// A plain function with no C++ objects on purpose — neither MSVC nor clang will put SEH in a
/// frame that also needs unwinding. The copy itself is out of line so the faulting instruction
/// is a call the try range is guaranteed to cover, whatever the optimiser does with the body.
#ifdef _WIN32
__attribute__((noinline)) void CopyGuestBytesRaw(const uint8_t* source, void* out,
                                                 std::size_t size) {
    std::memcpy(out, source, size);
}

int GuestFaultFilter(unsigned long code) {
    return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR
               ? EXCEPTION_EXECUTE_HANDLER
               : EXCEPTION_CONTINUE_SEARCH;
}

bool CopyGuestBytesGuarded(const uint8_t* source, void* out, std::size_t size) {
    __try {
        CopyGuestBytesRaw(source, out, size);
        return true;
    } __except (GuestFaultFilter(GetExceptionCode())) {
        return false;
    }
}

__attribute__((noinline)) void StoreGuestBytesRaw(uint8_t* target, const void* value,
                                                  std::size_t size) {
    std::memcpy(target, value, size);
}

bool StoreGuestBytesGuarded(uint8_t* target, const void* value, std::size_t size) {
    __try {
        StoreGuestBytesRaw(target, value, size);
        return true;
    } __except (GuestFaultFilter(GetExceptionCode())) {
        return false;
    }
}

/// Non-zero while a guarded read is in flight, so the trace handler below does not report
/// the fault that this path exists to absorb.
thread_local int g_guarded_guest_reads = 0;
#endif

#ifndef _WIN32
/// The Linux guard, doing what `__try` does for the Windows build above: reserved and
/// decommitted guest pages are PROT_NONE, so the read runs under a per-thread jump target the
/// last-resort handler takes when a fault lands inside it. `sigsetjmp(…, 0)` keeps this to a
/// register save; the handler unblocks the signal itself. The target is published in a slot
/// the handler finds by tid, not in TLS: this library is dlopen'd, so its TLS is dynamic and
/// a first touch allocates, which a signal handler must not do.
struct GuardSlot {
    std::atomic<long> tid{0};
    sigjmp_buf* guard = nullptr;
};
constexpr std::size_t kGuardSlots = 64;
GuardSlot g_guard_slots[kGuardSlots];
thread_local GuardSlot* t_guard_slot = nullptr;

GuardSlot* GuardSlotForThisThread() {
    if (t_guard_slot != nullptr) {
        return t_guard_slot;
    }
    const long tid = static_cast<long>(::syscall(SYS_gettid));
    for (GuardSlot& slot : g_guard_slots) {
        long expected = 0;
        // A free slot, or the slot a dead thread with this tid left behind.
        if (slot.tid.compare_exchange_strong(expected, tid) || expected == tid) {
            t_guard_slot = &slot;
            return &slot;
        }
    }
    return nullptr;
}

/// The guard the faulting thread published, or null. Called from the signal handler.
sigjmp_buf* GuardForFaultingThread() {
    const long tid = static_cast<long>(::syscall(SYS_gettid));
    for (GuardSlot& slot : g_guard_slots) {
        if (slot.tid.load(std::memory_order_relaxed) == tid) {
            return slot.guard;
        }
    }
    return nullptr;
}

bool CopyGuestBytesGuarded(const uint8_t* source, void* out, std::size_t size) {
    GuardSlot* slot = GuardSlotForThisThread();
    if (slot == nullptr) {
        std::memcpy(out, source, size);
        return true;
    }
    sigjmp_buf guard;
    sigjmp_buf* previous = slot->guard;
    slot->guard = &guard;
    bool ok = false;
    if (sigsetjmp(guard, 0) == 0) {
        std::memcpy(out, source, size);
        ok = true;
    }
    slot->guard = previous;
    return ok;
}

bool StoreGuestBytesGuarded(uint8_t* target, const void* value, std::size_t size) {
    GuardSlot* slot = GuardSlotForThisThread();
    if (slot == nullptr) {
        std::memcpy(target, value, size);
        return true;
    }
    sigjmp_buf guard;
    sigjmp_buf* previous = slot->guard;
    slot->guard = &guard;
    bool ok = false;
    if (sigsetjmp(guard, 0) == 0) {
        std::memcpy(target, value, size);
        ok = true;
    }
    slot->guard = previous;
    return ok;
}
#endif

/// The first few unreadable reads are worth naming: this is the read that used to take
/// Windows down, and a stale chain that reads zero forever is a symptom worth a line.
///
/// The first twelve, then every power of two with the running count, so a stale chain read every
/// frame shows its rate instead of falling silent after the twelfth.
[[maybe_unused]] void ReportUnreadableGuestRead(uint32_t address, std::size_t size) {
    static std::atomic<uint32_t> reported{0};
    const uint32_t count = reported.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 12u || (count & (count - 1u)) == 0u) {
        REXLOG_INFO(
            "Refii host: guest 0x{:08X} (+{}) is not readable — uncommitted or freed; "
            "reading zeroes instead of faulting (#{} this run)",
            address, size, count);
    }
}

/// Copies `size` guest bytes at `address` into `out`, or zero-fills and returns false when
/// they are not readable. The one read primitive everything else here is built on.
bool CopyGuest(const uint8_t* base, uint32_t address, void* out, std::size_t size) {
    if (base == nullptr) {
        std::memset(out, 0, size);
        return false;
    }

#ifdef _WIN32
    // The probe scope sits OUTSIDE the `__try` function, which may hold no C++ objects: it tells
    // ReXGlue's vectored handler — which Windows runs before any `__except` — to leave a fault
    // here to the guard instead of locking, querying and logging it (EnterHostProbe).
    ++g_guarded_guest_reads;
    rex::runtime::EnterHostProbe();
    const bool ok = CopyGuestBytesGuarded(base + address, out, size);
    rex::runtime::LeaveHostProbe();
    --g_guarded_guest_reads;
#else
    const bool ok = CopyGuestBytesGuarded(base + address, out, size);
#endif
    if (!ok) {
        std::memset(out, 0, size);
        ReportUnreadableGuestRead(address, size);
        return false;
    }
    return true;
}

/// One guest byte, or 0 when it is not readable.
uint8_t GuestByteAt(const uint8_t* base, uint32_t address) {
    uint8_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return value;
}

/// One guest word, big-endian, or 0 when it is not readable.
uint32_t GuestWordAt(const uint8_t* base, uint32_t address) {
    uint32_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return __builtin_bswap32(value);
}

/// One guest halfword, big-endian, or 0 when it is not readable.
uint16_t GuestHalfAt(const uint8_t* base, uint32_t address) {
    uint16_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return __builtin_bswap16(value);
}

/// One guest doubleword, big-endian, or 0 when it is not readable.
uint64_t GuestWord64At(const uint8_t* base, uint32_t address) {
    uint64_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return __builtin_bswap64(value);
}

/// A guest string handle — `char***`, exactly two hops, NUL-terminated, capped. The same
/// shape `sub_826B9510` reads; a zero inner pointer is the guest's own empty string. Local on
/// purpose: the VFS library's reader of this shape is not exported, and calling it links as a
/// dangling lazy PLT entry that ends the process with "symbol lookup error" (exit 127).
std::string ReadGuestString(const uint8_t* base, uint32_t handle, std::size_t cap = 256) {
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };
    if (base == nullptr || !plausible(handle)) {
        return std::string();
    }
    const uint32_t inner = GuestWordAt(base, handle);
    if (inner == 0 || !plausible(inner)) {
        return std::string();
    }
    const uint32_t characters = GuestWordAt(base, inner);
    if (!plausible(characters)) {
        return std::string();
    }
    std::string out;
    for (std::size_t i = 0; i < cap; ++i) {
        const uint8_t c = GuestByteAt(base, characters + static_cast<uint32_t>(i));
        if (c == 0) {
            break;
        }
        out.push_back(static_cast<char>(c));
    }
    return out;
}

/// A guest WIDE string handle. The game has TWO string classes and they are not
/// interchangeable:
///
///   * `char***` — two hops to 8-bit characters. Element names, region names, the record names
///     at `record+8`. `ReadGuestString` above reads this one, and it is correct for it.
///   * The refcounted LHString — the SAME two hops, but the characters are UTF-16 and the block
///     carries its own length. Verified by decompile, not taken from a report: `sub_822D6408`
///     allocates a 16-byte block and counts its source as `_WORD`; `sub_82B3FDC8` finishes with
///     `a1[1] = count`, `*(_WORD *)(2*count + data) = 0` and `XMemCpy(data, src, 2 * count)`.
///     Its formatter `sub_8230AE78` prints through the wide literal at 0x820A3D7C, whose bytes
///     are `00 25 00 64 00 00` — `L"%d"`.
///
/// Block layout, from `sub_82B3FDC8`: `+0` character data, `+4` length in WIDE UNITS, `+8`
/// capacity in bytes with a flag in bit 0, `+12` refcount (the word `sub_822B88A8` reserves on
/// when it assigns a handle).
///
/// Reading this class byte-wise returns EMPTY for all ASCII content, because the high byte of
/// every unit is the 0 the walk stops at — the guest is big-endian. That is what left the good
/// and evil deed sprites with an icon and no amount.
///
/// UTF-16 BE out as UTF-8, basic plane only: a surrogate pair cannot appear in these strings,
/// and emitting a lone surrogate is worse than stopping at it.
std::string ReadGuestWideString(const uint8_t* base, uint32_t handle, uint32_t cap = 256) {
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };
    if (base == nullptr || !plausible(handle)) {
        return std::string();
    }
    const uint32_t block = GuestWordAt(base, handle);
    if (!plausible(block)) {
        return std::string();  // the guest's own empty string
    }
    const uint32_t characters = GuestWordAt(base, block);
    if (!plausible(characters)) {
        return std::string();
    }

    uint32_t length = GuestWordAt(base, block + 4u);
    if (length > cap) {
        length = cap;
    }

    std::string out;
    for (uint32_t c = 0; c < length; ++c) {
        const uint16_t unit = GuestHalfAt(base, characters + (c * 2u));
        if (unit == 0) {
            break;
        }

        if (unit < 0x80u) {
            out.push_back(static_cast<char>(unit));
        } else if (unit < 0x800u) {
            out.push_back(static_cast<char>(0xC0u | (unit >> 6)));
            out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        } else {
            out.push_back(static_cast<char>(0xE0u | (unit >> 12)));
            out.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        }
    }

    return out;
}

/// FNV-1 over a name, the hash Babel keys on — multiply then xor, 32-bit.
uint32_t RefiiFnv1(const std::string& text) {
    uint32_t hash = 0x811C9DC5u;
    for (const char c : text) {
        hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(c);
    }

    return hash;
}

/// Whether a fast-travel region has any stop that is ACTIVE and states a destination — the test
/// `sub_82518380` makes (`lbz r11, 0(r31)` at 0x8251841C, `lwz r11, 8(r31)` at 0x82518404) and the
/// one that decides whether the region gets a row at all.
bool RefiiRegionHasActiveStop(const uint8_t* base, uint32_t region) {
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    // Both vectors are pairs, stride 8 (`sub_82518380`): the outer walk is `v8 += 2` on an
    // `int**` (0x82518478) ending at `*(region+20)`, the inner `v11 += 2` on an `int*`
    // (0x82518468) ending at `level+12`. Only the first word of each pair is an entry.
    const uint32_t levels = GuestWordAt(base, region + 16u);
    const uint32_t levelsEnd = GuestWordAt(base, region + 20u);
    if (!plausible(levels) || !plausible(levelsEnd) || levelsEnd < levels ||
        ((levelsEnd - levels) % 8u) != 0u || ((levelsEnd - levels) / 8u) > 256u) {
        return false;
    }

    for (uint32_t levelSlot = levels; levelSlot < levelsEnd; levelSlot += 8u) {
        const uint32_t level = GuestWordAt(base, levelSlot);
        if (!plausible(level)) {
            continue;
        }

        const uint32_t stops = GuestWordAt(base, level + 8u);
        const uint32_t stopsEnd = GuestWordAt(base, level + 12u);
        if (!plausible(stops) || !plausible(stopsEnd) || stopsEnd < stops ||
            ((stopsEnd - stops) % 8u) != 0u || ((stopsEnd - stops) / 8u) > 256u) {
            continue;
        }

        for (uint32_t stopSlot = stops; stopSlot < stopsEnd; stopSlot += 8u) {
            const uint32_t stop = GuestWordAt(base, stopSlot);
            if (plausible(stop) && GuestByteAt(base, stop) != 0u &&
                !ReadGuestString(base, stop + 8u).empty()) {
                return true;
            }
        }
    }

    return false;
}

/// Stores `size` bytes into guest memory, or returns false when the page is not there. The
/// few host-side writes are latches on objects the guest handed over, so a miss is a stale
/// pointer, not a reason to fault.
bool StoreGuest(uint8_t* base, uint32_t address, const void* value, std::size_t size) {
    if (base == nullptr) {
        return false;
    }

#ifdef _WIN32
    ++g_guarded_guest_reads;
    const bool ok = StoreGuestBytesGuarded(base + address, value, size);
    --g_guarded_guest_reads;
#else
    const bool ok = StoreGuestBytesGuarded(base + address, value, size);
#endif
    if (!ok) {
        ReportUnreadableGuestRead(address, size);
    }
    return ok;
}

void SetEnvironmentValue(const char* name, const char* value) {
#ifdef _WIN32
    ::_putenv_s(name, value);
#else
    ::setenv(name, value, 1);
#endif
}

#ifndef _WIN32
/// The crash record. Godot's log is stdio-buffered and the guest log is an spdlog queue, so a
/// dying process loses the last few kilobytes of both. Written with open/write only, from
/// inside the signal handler, and closed before anything that could block.
namespace crash {
char g_path[4096] = {};


/// Beside the guest log: `ReXApp::OnInitialize` logs to `exe_dir/logs`.
void ResolvePath() {
    if (g_path[0] != '\0') {
        return;
    }
    std::error_code ec;
    std::filesystem::path exe = std::filesystem::read_symlink("/proc/self/exe", ec);
    std::filesystem::path dir = ec ? std::filesystem::current_path(ec) : exe.parent_path();
    std::filesystem::create_directories(dir / "logs", ec);
    const std::string path = (dir / "logs" / "refii_crash.log").string();
    std::snprintf(g_path, sizeof(g_path), "%s", path.c_str());
}

void Append(int fd, const char* text) {
    std::size_t left = std::strlen(text);
    while (left > 0) {
        const ssize_t written = ::write(fd, text, left);
        if (written <= 0) {
            return;
        }
        text += written;
        left -= static_cast<std::size_t>(written);
    }
}

/// Module and nearest symbol. A recompiled guest function names itself `sub_8XXXXXXX`,
/// which is the address IDA wants; managed (JIT) code has no module at all.
void DescribeAddress(int fd, uintptr_t pc) {
    Dl_info info{};
    char buffer[768];
    if (pc != 0 && ::dladdr(reinterpret_cast<void*>(pc), &info) != 0 &&
        info.dli_fname != nullptr) {
        const uintptr_t anchor = reinterpret_cast<uintptr_t>(
            info.dli_saddr != nullptr ? info.dli_saddr : info.dli_fbase);
        std::snprintf(buffer, sizeof(buffer), "  pc is %s+0x%lx in %s\n",
                      info.dli_sname != nullptr ? info.dli_sname : "<no symbol>",
                      static_cast<unsigned long>(pc - anchor), info.dli_fname);
    } else {
        std::snprintf(buffer, sizeof(buffer),
                      "  pc is in no loaded module (managed JIT code, or unmapped)\n");
    }
    Append(fd, buffer);
}

/// The faulting thread's GUEST stack, when it has one. ThreadState::Get() is the null-safe
/// TLS read; the walk is the title's own back chain (32-bit word at [r1], saved LR at
/// back-chain - 8, verified in the binary), pure loads against the always-mapped arena, so it
/// is as signal-safe as the host backtrace below it.
void DescribeGuestThread(int fd) {
    rex::runtime::ThreadState* state = rex::runtime::ThreadState::Get();
    rex::memory::Memory* memory = SafeKernelMemory();
    if (state == nullptr || state->context() == nullptr || memory == nullptr) {
        return;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    PPCContext* context = state->context();
    char buffer[96];
    std::snprintf(buffer, sizeof(buffer), "  guest tid=%u r1=%08X lr=%08X\n",
                  state->thread_id(), context->r1.u32, static_cast<uint32_t>(context->lr));
    Append(fd, buffer);
    uint32_t sp = context->r1.u32;
    for (int frame = 0; frame < 32 && sp >= 0x10000u && sp < 0xC0000000u; ++frame) {
        uint32_t back = 0;
        std::memcpy(&back, base + sp, sizeof(back));
        back = __builtin_bswap32(back);
        if (back == 0u || back <= sp || back >= 0xC0000000u) {
            break;
        }
        uint32_t lr = 0;
        std::memcpy(&lr, base + back - 8u, sizeof(lr));
        lr = __builtin_bswap32(lr);
        if (lr == 0u) {
            break;
        }
        std::snprintf(buffer, sizeof(buffer), "  guest frame %08X\n", lr);
        Append(fd, buffer);
        sp = back;
    }
}

/// One record: a stamped header, the pc named, and the calling thread's backtrace.
void WriteRecord(const char* line, uintptr_t pc) {
    const int fd = g_path[0] != '\0'
                       ? ::open(g_path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644)
                       : -1;
    if (fd < 0) {
        Append(2, line);
        Append(2, "\n");
        return;
    }
    char header[160];
    std::snprintf(header, sizeof(header), "\n=== %ld tid=%ld ", static_cast<long>(::time(nullptr)),
                  static_cast<long>(::syscall(SYS_gettid)));
    Append(fd, header);
    Append(fd, line);
    Append(fd, "\n");
    if (pc != 0) {
        DescribeAddress(fd, pc);
    }
    DescribeGuestThread(fd);
    void* frames[64];
    const int count = ::backtrace(frames, 64);
    ::backtrace_symbols_fd(frames, count, fd);
    ::close(fd);
}

/// The deadline is armed BEFORE the record, not only around the loggers. A thread that
/// aborts inside malloc or the dynamic loader still holds that lock, and the record's own
/// backtrace() can want it (its first call loads libgcc_s — primed at install for that
/// reason): a handler that never returns is a process that never ends. SIGALRM's default
/// action is a death, nothing else here uses it, and a run in which one thread hung in the
/// SIGABRT handler for ten minutes (2026-10-11, refii_crash.log cut off after the header)
/// is what this costs without it.
///
/// Cancelled on the way out, because a managed fault carries on running.
void Report(const char* line, uintptr_t pc) {
    ::alarm(5);
    WriteRecord(line, pc);
    REXLOG_INFO("{}", line);
    rex::FlushLogging();
    ::alarm(0);
}

/// `Report` for a thread that is dying: the deadline stays armed, so a death that then
/// stalls in abort(), in a chained handler or in a core dump still ends.
void ReportFatal(const char* line, uintptr_t pc) {
    ::alarm(5);
    WriteRecord(line, pc);
    // A live Tracy capture is saved before the process goes (bounded; the record is already
    // written), and the deadline is re-armed for what follows.
    refii_host_tracy::SaveOnCrash(3000);
    ::alarm(5);
    REXLOG_INFO("{}", line);
    rex::FlushLogging();
    ::alarm(5);
}
}  // namespace crash


/// The last-resort fault handler. The runtime's `ExceptionHandler` installs SIGSEGV/SIGILL over
/// .NET's and simply RETURNS when nothing claims a fault, so the faulting instruction
/// re-executes for ever.
///
/// Installed AFTER the runtime's, so it runs first and decides by address:
///   - inside the guest arena: chain to the runtime's handler. If the same thread faults three
///     times at the same pc and address within a few milliseconds, nobody is claiming it:
///     record it, restore the default action, and let the next fault kill the process.
///   - anywhere else: record it (a managed exception shows as pc in no module) and chain to
///     the handler .NET had installed, which dispatches a managed exception or re-raises.
namespace fault {
constexpr int kSignals[] = {SIGSEGV, SIGBUS, SIGILL, SIGFPE};
struct sigaction g_managed[NSIG] = {};

struct sigaction g_runtime[NSIG] = {};
bool g_managed_captured = false;
std::atomic<bool> g_installed{false};
std::atomic<int> g_reports{0};

/// What .NET left on these signals, captured before the runtime replaces any of them.
void CaptureManagedActions() {
    if (g_managed_captured) {
        return;
    }
    g_managed_captured = true;
    for (int sig : kSignals) {
        ::sigaction(sig, nullptr, &g_managed[sig]);
    }
}

uintptr_t ProgramCounter(void* context) {
    auto* ucontext = static_cast<ucontext_t*>(context);
#if defined(__x86_64__)
    return static_cast<uintptr_t>(ucontext->uc_mcontext.gregs[REG_RIP]);
#elif defined(__aarch64__)
    return static_cast<uintptr_t>(ucontext->uc_mcontext.pc);
#else
    (void)ucontext;
    return 0;
#endif
}

/// The virtual and the physical view, 4 GB each, back to back.
bool InGuestArena(uintptr_t address) {
    rex::memory::Memory* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return false;
    }
    const uintptr_t base = reinterpret_cast<uintptr_t>(memory->virtual_membase());
    return address - base < (uintptr_t{1} << 33);
}

/// The same thread, pc and address three times in five milliseconds: a re-armed watch page
/// takes a frame to refault, an unclaimed fault refaults immediately. One tracker, spin-locked,
/// because dynamic TLS is not signal-safe in a dlopen'd library.
struct Tracker {
    long tid;
    uintptr_t pc;
    uintptr_t address;
    int count;
    timespec first;
};
Tracker g_tracker = {};
std::atomic_flag g_tracker_lock = ATOMIC_FLAG_INIT;

bool IsSpinning(uintptr_t pc, uintptr_t address) {
    if (g_tracker_lock.test_and_set(std::memory_order_acquire)) {
        return false;
    }
    timespec now{};
    ::clock_gettime(CLOCK_MONOTONIC, &now);
    const long tid = static_cast<long>(::syscall(SYS_gettid));
    bool spinning = false;
    if (g_tracker.tid == tid && g_tracker.pc == pc && g_tracker.address == address) {
        const double ms = static_cast<double>(now.tv_sec - g_tracker.first.tv_sec) * 1e3 +
                          static_cast<double>(now.tv_nsec - g_tracker.first.tv_nsec) / 1e6;
        if (ms < 5.0) {
            spinning = ++g_tracker.count >= 3;
        } else {
            g_tracker = Tracker{tid, pc, address, 1, now};
        }
    } else {
        g_tracker = Tracker{tid, pc, address, 1, now};
    }
    g_tracker_lock.clear(std::memory_order_release);
    return spinning;
}

const char* Name(int sig) {
    switch (sig) {
        case SIGSEGV: return "SIGSEGV";
        case SIGBUS: return "SIGBUS";
        case SIGILL: return "SIGILL";
        case SIGFPE: return "SIGFPE";
        case SIGTERM: return "SIGTERM";
        case SIGINT: return "SIGINT";
        case SIGQUIT: return "SIGQUIT";
        case SIGPIPE: return "SIGPIPE";
        case SIGSYS: return "SIGSYS";
        case SIGTRAP: return "SIGTRAP";
        case SIGXCPU: return "SIGXCPU";
        case SIGXFSZ: return "SIGXFSZ";
        case SIGHUP: return "SIGHUP";
        default: return "signal";
    }
}

void Record(int sig, uintptr_t pc, uintptr_t address, const char* verdict) {
    if (g_reports.fetch_add(1, std::memory_order_relaxed) >= 8) {
        return;
    }
    char line[512];
    std::snprintf(line, sizeof(line), "[hostbridge] %s at pc=0x%lx address=0x%lx: %s", Name(sig),
                  static_cast<unsigned long>(pc), static_cast<unsigned long>(address), verdict);
    crash::Report(line, pc);
}

/// Runs the action that was installed before ours, or restores the default so the fault
/// reaches the kernel on re-execution.
void Chain(int sig, const struct sigaction& to, siginfo_t* info, void* context) {
    if ((to.sa_flags & SA_SIGINFO) != 0 && to.sa_sigaction != nullptr) {
        to.sa_sigaction(sig, info, context);
        return;
    }
    if ((to.sa_flags & SA_SIGINFO) == 0 && to.sa_handler == SIG_IGN) {
        return;
    }
    if ((to.sa_flags & SA_SIGINFO) == 0 && to.sa_handler != SIG_DFL && to.sa_handler != nullptr) {
        to.sa_handler(sig);
        return;
    }
    ::signal(sig, SIG_DFL);
}

void LastResort(int sig, siginfo_t* info, void* context) {
    const uintptr_t pc = ProgramCounter(context);
    const uintptr_t address = reinterpret_cast<uintptr_t>(info != nullptr ? info->si_addr : nullptr);

    // Unreadable guest memory reads as zeroes. The signal is blocked while this handler runs
    // and the jump does not go through sigreturn, so unblock it here.
    sigjmp_buf* guard = (sig == SIGSEGV || sig == SIGBUS) ? GuardForFaultingThread() : nullptr;
    if (guard != nullptr) {
        sigset_t mask;
        sigemptyset(&mask);
        sigaddset(&mask, sig);
        ::sigprocmask(SIG_UNBLOCK, &mask, nullptr);
        siglongjmp(*guard, 1);
    }

    if (IsSpinning(pc, address)) {
        Record(sig, pc, address,
               "UNCLAIMED — three identical faults on this thread; the runtime's handler declines "
               "it and returning would re-execute it for ever. Default action restored; the next "
               "one kills the process");
        refii_host_tracy::SaveOnCrash(3000);
        ::signal(sig, SIG_DFL);
        return;
    }

    if ((sig == SIGSEGV || sig == SIGBUS) && InGuestArena(address)) {
        Chain(sig, g_runtime[sig], info, context);
        return;
    }

    Record(sig, pc, address,
           "outside guest memory — a host fault, or a managed exception (pc in no module) that "
           "the .NET handler takes next");
    // A managed exception comes back from the chain and disarms. A native fault does not:
    // the .NET handler hands it to Godot's crash handler, which symbolises a backtrace and
    // re-raises, and if any of that stalls the deadline ends the process instead.
    ::alarm(10);
    Chain(sig, g_managed[sig], info, context);
    ::alarm(0);
}

/// The exits nothing else reports: a plain `exit()`, SIGTERM/SIGINT/SIGQUIT, and
/// SIGPIPE/SIGSYS/SIGTRAP/SIGXCPU/SIGXFSZ, whose default action dies without a word.
/// Recorded, then handed on unchanged.
constexpr int kExtraSignals[] = {SIGTERM, SIGINT, SIGQUIT, SIGPIPE, SIGSYS, SIGTRAP, SIGXCPU,
                                 SIGXFSZ, SIGHUP};

void OnExit() {
    char line[256];
    std::snprintf(line, sizeof(line),
                  "[hostbridge] exit() called on tid=%ld — the backtrace names the caller",
                  static_cast<long>(::syscall(SYS_gettid)));
    crash::Report(line, 0);
}

void LastResortExtra(int sig, siginfo_t* info, void* context) {
    const uintptr_t pc = ProgramCounter(context);
    char verdict[160];
    std::snprintf(verdict, sizeof(verdict),
                  "delivered (si_code=%d, sender pid=%ld) — handed to the previous handler",
                  info != nullptr ? info->si_code : 0,
                  static_cast<long>(info != nullptr ? info->si_pid : 0));
    Record(sig, pc, info != nullptr ? reinterpret_cast<uintptr_t>(info->si_addr) : 0u, verdict);
    if (sig == SIGPIPE) {
        return;  // a dead socket is not a reason to die; the write gets EPIPE instead
    }
    Chain(sig, g_runtime[sig], info, context);
}

/// After the runtime's `Memory` exists (its handler must sit BEHIND ours), on both boot paths.
void Install() {
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return;
    }
    crash::ResolvePath();
    refii_bridge_ensure_alternate_signal_stack();  // Godot's thread, the one that installs this
    for (int sig : kSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResort;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
    for (int sig : kExtraSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResortExtra;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
    std::atexit(OnExit);
    REXLOG_INFO("Refii host: last-resort fault reporter installed; records go to {}",
                crash::g_path);
}

/// After the runtime is destroyed. `~MMIOHandler` → `ExceptionHandler::Uninstall` restores
/// the sigaction it saved at install, which is .NET's — ours, installed over it, goes with
/// it, and the exit crash of 2026-10-11 (inside the driver's unload, after a complete
/// guest teardown) left no record for exactly that reason. Whatever is current becomes the
/// handler to chain to; no guest arena exists any more, so every fault is a host fault.
void Reinstall() {
    if (!g_installed.load()) {
        return;
    }
    for (int sig : kSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResort;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
    for (int sig : kExtraSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResortExtra;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
}
}  // namespace fault
#endif


/// Installs the fault reporter once the guest is up. No-op on Windows, where the vectored
/// handler below already reports faults.
void InstallLastResortFaultHandler() {
#ifndef _WIN32
    fault::Install();
#endif
}

/// A fail-fast inside the guest leaves no exception, no log line and no exit status —
/// only 0xC0000409 from ucrtbase. These name the reason before the process dies.
void ReportEmbeddedFailure(const char* kind, const std::string& detail) {
    // A fixed buffer and raw writes: this runs inside the SIGABRT handler, and the thread
    // that aborted may hold the heap or the stdio lock (glibc's "free(): invalid pointer"
    // aborts from inside free with the arena locked).
    char line[1024];
    if (detail.empty()) {
        std::snprintf(line, sizeof(line), "[hostbridge] %s\n", kind);
    } else {
        std::snprintf(line, sizeof(line), "[hostbridge] %s: %s\n", kind, detail.c_str());
    }

#ifndef _WIN32
    crash::Append(2, line);
    crash::ResolvePath();
    crash::ReportFatal(line, 0);
#else
    std::fputs(line, stderr);
#endif

#ifdef _WIN32
    void* frames[62];
    const USHORT captured = ::CaptureStackBackTrace(0, 62, frames, nullptr);
    const HANDLE process = ::GetCurrentProcess();
    ::SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_LOAD_LINES);
    ::SymInitialize(process, nullptr, TRUE);

    alignas(SYMBOL_INFO) char symbol_storage[sizeof(SYMBOL_INFO) + 512] = {};
    auto* symbol = reinterpret_cast<SYMBOL_INFO*>(symbol_storage);
    symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
    symbol->MaxNameLen = 511;

    for (USHORT frame = 0; frame < captured; ++frame) {
        const auto address = reinterpret_cast<DWORD64>(frames[frame]);
        const char* name = "??";
        if (::SymFromAddr(process, address, nullptr, symbol)) {
            name = symbol->Name;
        }

        HMODULE module = nullptr;
        const char* module_name = "?";
        unsigned long long rva = 0;
        if (::GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                     GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                 reinterpret_cast<LPCSTR>(frames[frame]), &module) &&
            module != nullptr) {
            static char module_path[MAX_PATH];
            if (::GetModuleFileNameA(module, module_path, MAX_PATH) != 0) {
                const char* slash = std::strrchr(module_path, '\\');
                module_name = slash != nullptr ? slash + 1 : module_path;
            }
            rva = address - reinterpret_cast<DWORD64>(module);
        }

        IMAGEHLP_LINE64 location = {};
        location.SizeOfStruct = sizeof(location);
        DWORD displacement = 0;
        if (::SymGetLineFromAddr64(process, address, &displacement, &location)) {
            std::fprintf(stderr, "  #%02u %s!%s (%s:%lu)\n", frame, module_name, name,
                         location.FileName, location.LineNumber);
        } else {
            std::fprintf(stderr, "  #%02u %s+0x%llx (nearest %s)\n", frame, module_name,
                         rva, name);
        }
    }
#endif

    std::fflush(stderr);
}

#ifdef _WIN32
/// MSVC keeps terminate handlers PER THREAD, so a throw on a guest worker never reaches
/// the one installed here. This catches the throw itself, before any unwinding.
LONG CALLBACK TraceThrowHandler(PEXCEPTION_POINTERS info) {
    if (info == nullptr || info->ExceptionRecord == nullptr) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    const DWORD code = info->ExceptionRecord->ExceptionCode;
    if (code == 0xE06D7363u) {
        // Opt-in: a throw is not necessarily fatal, and the guest throws in normal operation.
        static const bool trace_throws = [] {
            const char* value = std::getenv("REFII_HOST_TRACE_THROWS");
            return value != nullptr && value[0] == '1';
        }();
        if (trace_throws) {
            ReportEmbeddedFailure("C++ throw", {});
        }
    } else if (code == EXCEPTION_ACCESS_VIOLATION) {
        // A guarded read is about to absorb this one; it is not the crash this reports.
        if (g_guarded_guest_reads > 0) {
            return EXCEPTION_CONTINUE_SEARCH;
        }

        // The guest may fault by design for MMIO, so cap the noise.
        static std::atomic<int> reported{0};
        if (reported.fetch_add(1, std::memory_order_relaxed) < 3) {
            std::ostringstream detail;
            detail << "at 0x" << std::hex << info->ExceptionRecord->ExceptionAddress
                   << " accessing 0x" << info->ExceptionRecord->ExceptionInformation[1];
            ReportEmbeddedFailure("ACCESS VIOLATION", detail.str());
        }
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

/// A fault nothing handled, on its way out: the live Tracy capture is saved (bounded), then the
/// filter that was there before — the .NET runtime's or Godot's crash handler — runs exactly as
/// it would have. Only truly unhandled exceptions arrive here, unlike the vectored handler above.
LPTOP_LEVEL_EXCEPTION_FILTER g_previous_unhandled_filter = nullptr;

LONG WINAPI SaveTracyOnUnhandled(PEXCEPTION_POINTERS info) {
    refii_host_tracy::SaveOnCrash(3000);
    return g_previous_unhandled_filter != nullptr ? g_previous_unhandled_filter(info)
                                                  : EXCEPTION_CONTINUE_SEARCH;
}
#endif

void InstallEmbeddedFailureHandlers() {
    static bool installed = false;
    if (installed) {
        return;
    }
    installed = true;

#ifndef _WIN32
    fault::CaptureManagedActions();

    // A core for the deaths no handler sees: systemd-coredump logs the signal and a backtrace
    // in the journal for any core-producing death, as long as RLIMIT_CORE is not zero. Capped
    // because the guest arena is gigabytes; the kernel truncates at the limit.
    {
        rlimit core{};
        if (::getrlimit(RLIMIT_CORE, &core) == 0) {
            const rlim_t wanted = static_cast<rlim_t>(256u) * 1024u * 1024u;
            core.rlim_cur = core.rlim_max == RLIM_INFINITY ? wanted : std::min(wanted, core.rlim_max);
            ::setrlimit(RLIMIT_CORE, &core);
        }
    }
#endif

#ifdef _WIN32
    // Always on. A crash that prints nothing costs a whole test run to learn nothing from;
    // the handler is capped and only reports faults that were going to be fatal anyway.
    ::AddVectoredExceptionHandler(1, TraceThrowHandler);
    g_previous_unhandled_filter = ::SetUnhandledExceptionFilter(SaveTracyOnUnhandled);
#endif

#ifndef _WIN32
    // backtrace() loads libgcc_s on its first call, through the dynamic loader and the
    // heap. Taken here, on a healthy thread, so the call inside a signal handler is a walk
    // and nothing more.
    {
        void* frames[4];
        (void)::backtrace(frames, 4);
    }
#endif

    // abort() raises SIGABRT before it fail-fasts, and unlike terminate this handler is
    // process-wide — it is the only one that sees a guest worker die. The default action is
    // restored and the signal re-raised here rather than left to abort()'s own second raise,
    // so a SIGABRT that arrived any other way ends the process too.
    std::signal(SIGABRT, [](int) {
        ReportEmbeddedFailure("SIGABRT", {});
        std::signal(SIGABRT, SIG_DFL);
        std::raise(SIGABRT);
    });

    std::set_terminate([]() {
        std::string detail;
        if (std::exception_ptr active = std::current_exception()) {
            try {
                std::rethrow_exception(active);
            } catch (const std::exception& error) {
                detail = error.what();
            } catch (...) {
                detail = "non-std exception";
            }
        } else {
            detail = "no active exception";
        }
        ReportEmbeddedFailure("std::terminate", detail);
        std::abort();
    });

#ifdef _WIN32
    _set_invalid_parameter_handler([](const wchar_t*, const wchar_t*, const wchar_t*,
                                      unsigned int, uintptr_t) {
        ReportEmbeddedFailure("CRT invalid parameter", {});
        std::abort();
    });
#endif
}

/// Applies the switches that make the guest a simulation server rather than an app.
/// Shared by both entry points so they cannot drift.
void ApplyEmbeddedEnvironment() {
    InstallEmbeddedFailureHandlers();

    // No window, no overlays, no presentation.
    SetEnvironmentValue("REX_EMBED_HEADLESS", "1");
}

/// Guest globals and device fields the no-GPU latch needs. All IDA-proven against
/// `default.xex`; see ArmGuestNoGpuMode for the proof chain.
constexpr refii::guest::Addr kGuestDevicePointer{0x83360364u};  // g_D3DDevice_PresentModule
constexpr uint32_t kDeviceNoGpuFlagByte = 0x2ABDu;     // dev+0x2ABD
constexpr uint8_t kDeviceNoGpuFlagBit = 0x02u;         // "GPU is dead" latch
constexpr uint32_t kDeviceDebugConsumer = 0x541Cu;     // dev+0x541C, NULL in retail
constexpr uint32_t kDeviceOutstandingIbs = 0x2B00u;    // dev+0x2B00

// Atomics: written by the fast-arm watcher thread below as well as the step thread.
std::atomic<bool> g_no_gpu_armed{false};
std::atomic<uint32_t> g_guest_device{0};

std::atomic<bool> g_arm_watcher_stop{false};
std::thread g_arm_watcher;

/// The completion publisher's own thread. Published from the host's render loop, a present
/// waited up to a whole host frame for its completion, and the guest's frame became
/// present + host-frame: measured 14.4 guest frames/s under a 27 fps host (2026-09-04). A
/// 1 ms poll answers each present as the GPU would have, and the guest paces itself.
/// REFII_COMPLETION_THREAD=0 keeps the once-per-step publishing for comparison.
std::atomic<bool> g_completion_stop{false};
std::thread g_completion_thread;
refii::TracedMutex g_completion_mutex{"guest I/O completion"};
uint32_t g_completions_by_thread = 0;

/// Puts Fable 2's own D3D into the "no GPU present" mode it ships with.
///
/// THE PROBLEM. With no GPU behind it, the guest boots, initialises its ring buffer,
/// and then parks forever in `D3D_CDevice_BlockOnFence` (0x82242668) spinning on the
/// fence write-back at `*(*(dev+0x2A90))` that only a command processor would ever
/// advance. The spin does not yield — it pegs a core — and it is reached from
/// `D3DDevice_BlockUntilIdle` on the SECOND `D3D_InitializeRingBuffer` entry. That is
/// where every run has been stopping, ~34ms into boot, long before any game logic.
///
/// THE FIX IS THE GAME'S OWN. Bit 0x02 of `dev+0x2ABD` is a one-way latch that D3D
/// checks in three places:
///   * `D3D_CDevice_AddCallsToPrimaryBuffer` (0x822866F8) returns without emitting PM4,
///     without advancing the write index, and WITHOUT the doorbell store at 0x82286870
///     — the only `0x7FC80714` MMIO write in the image. D3D9 stops posting.
///   * `D3D_CDevice_SetFence` (0x822986EC) writes the fence and ring position into the
///     write-back block ITSELF, from the CPU, exactly as the GPU would have.
///   * `D3D_CBlocker_Check` (0x82BA1FF8) returns 0, so every GPU wait in the binary
///     exits on its first iteration — including the spin above.
/// Nothing in the executable ever clears the bit; a census of all 17 stores to
/// `dev+0x2ABD` found only `ori 2` and `ori 3` setters, and `StopWorkerQueue`'s
/// `andi 0xDF` preserves it.
///
/// WHY THE GUEST CANNOT DO IT ALONE. It tries. `D3D_CDevice_KickOff` sets this very bit
/// at 0x821E8FA0 when `VdIsHSIOTrainingSucceeded` reported failure — which our stub
/// does. But the store sits AFTER a `BlockOnFence` at 0x821E8F94, so the guest has to
/// survive the wait in order to reach the flag that would have skipped it. The hang
/// handler would also latch it, but only after `D3D_CBlocker_Check`'s 5000-tick
/// deadline, and that deadline is measured against a KTHREAD field the host never
/// advances, so it never expires. Hence: the host sets it.
///
/// SAFE TO ARM LATE. `D3D_CBlocker_Check` is re-evaluated every iteration of every
/// spin, so arming after the guest is already parked releases it just the same. That is
/// why polling from the step loop is sufficient and no hook is needed.
///
/// Requires `dev+0x541C == 0` (a debug/PIX command consumer, with no store anywhere in
/// the executable — NULL in retail). A non-NULL consumer would take precedence in both
/// `AddCallsToPrimaryBuffer` and `SetFence`, so the arm is skipped if one is present.
void ArmGuestNoGpuMode() {
    if (g_no_gpu_armed) {
        return;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    // Guest memory is big-endian; these are plain aligned loads the guest also does.
    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };

    const uint32_t device = load_be32(kGuestDevicePointer);
    if (device == 0) {
        // Device not created yet; try again next step.
        return;
    }

    if (load_be32(device + kDeviceDebugConsumer) != 0) {
        REXLOG_WARN(
            "Refii host: dev+0x541C is non-NULL; leaving the no-GPU latch alone");
        g_no_gpu_armed = true;
        return;
    }

    const uint8_t before = GuestByteAt(base, device + kDeviceNoGpuFlagByte);
    const uint8_t latched = static_cast<uint8_t>(before | kDeviceNoGpuFlagBit);
    StoreGuest(base, device + kDeviceNoGpuFlagByte, &latched, sizeof(latched));
    g_no_gpu_armed = true;
    g_guest_device = device;

    REXLOG_INFO(
        "Refii host: armed the guest's no-GPU mode (device=0x{:08X}, "
        "dev+0x2ABD 0x{:02X} -> 0x{:02X})",
        device, before, latched);
}

/// Keeps `D3DDevice_BlockUntilIdle` satisfiable.
///
/// This is the ONE GPU wait the no-GPU latch does not cover. `D3DDevice_BlockUntilIdle`
/// (loop at 0x82BA27A4) spins on `dev+0x2B00 == 0` with no CBlocker, no watchdog and no
/// escape hatch of any kind — the only unguarded spin of its class in the binary:
///
///     82ba27a4  lwz   r11, 0x2B00(r31)
///     82ba27a8  cmpwi cr6, r11, 0
///     82ba27ac  bne   cr6, loc_82BA27A4
///
/// `D3D_CDevice_QueueIndirectBuffer` INCREMENTS it, and nothing in the guest ever
/// decrements it — on hardware the GPU retired those indirect buffers. The increment is
/// by zero at four of its five call sites, but `D3D_StopWorkerQueue` passes one, and
/// that is reached from `D3DDevice_EndTiling` — a path Fable 2 does use. So the counter
/// can become nonzero and then never come back down, wedging the guest permanently with
/// no diagnostic.
///
/// Zeroing it every step is correct precisely BECAUSE there is no GPU: with nothing to
/// retire indirect buffers, "outstanding" can only ever grow. The binary's own hang
/// handler does exactly this write at 0x82BA7678.
/// Retires the guest's command-stream rings, which nothing else does headless.
///
/// The guest reserves ring space through `sub_8222ABE0`, and when the ring looks full it
/// SPINS — `Sleep(0)` with no timeout and no GPU-dead check:
///
///     while ( a1[9] >= a1[8] ) {                       /*0x8222ACB4*/
///       if ( v6 + a1[8] <= a1[9] ) break;
///       sub_82CC2E80(0);                                // Sleep(0)
///       sub_821A5BD0(a1); }                             // re-read the reader window
///
/// and `sub_821A5BD0` sources that window from exactly one place:
///
///     v2 = a1[16];  a1[9] = v2;                         /*0x821A5BF4 / 0x821A5C04*/
///
/// `a1[16]` is +64, the CONSUMER's read-back. Neither function consults `dev+0x2ABD`, so
/// the no-GPU latch above does not release this one — it is a producer waiting on a
/// consumer, not on hardware. With Godot rendering and the guest's commands going nowhere,
/// there is no consumer, so the reserve waits forever. Measured: a New Game parks here
/// before `GameFlow_QueueCommand(CmdLevelChange)` at 0x823115D0, which is why the level
/// change is never even queued.
///
/// The retire itself is not invented here. It mirrors the guest's own
/// SceneStreamSyncPublishCursor + SceneStreamRefresh pair, the same mapping the renderer
/// module already implements in RepairFableSceneStreamPublishHandoff — that code is correct
/// but lives behind REFII_BUILD_RENDERER_HOOKS, which is OFF by design because Godot
/// renders. Draining is a command-stream contract, not renderer work, so it belongs here
/// where it is always built.
///
/// Discarding the commands is the right semantic rather than a shortcut: the guest is a
/// simulation server, it states what it would have drawn, and a null device consumes and
/// drops it.
///
/// Four slots, `dword_8349F9B0[0..3]`. Field map (shared with the renderer's):
///   +8 capacity · +12 read cursor · +16 visible end · +20 read limit · +24 last read
///   +28 read cap · +32 write cursor · +36 write consumed · +40 write mark · +52 write wrap
///   +60 published end · +64 READER CONSUMED · +68 published cap
void RetireGuestSceneStreams() {
    // OFF unless asked for: measured harmful (it stops the startup logos playing) and its
    // premise was refuted — the ring is never full when the reserve is blocked. Kept only
    // because the field map below is correct and a real consumer will want it.
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_RETIRE_SCENE_STREAMS");
        return value != nullptr && std::string_view(value) == "1";
    }();
    if (!enabled) {
        return;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto write32 = [base](uint32_t at, uint32_t value) {
        const uint32_t raw = __builtin_bswap32(value);
        StoreGuest(base, at, &raw, sizeof(raw));
    };

    // A cursor must land inside its own ring. Anything else means we are looking at a slot
    // that is not a scene stream — half-built, freed, or simply another kind of object —
    // and writing to it would be scribbling on guest memory.
    constexpr uint32_t kMaxRingBytes = 16u * 1024u * 1024u;
    const auto sane_offset = [](uint32_t offset, uint32_t cap) {
        return cap != 0u && cap <= kMaxRingBytes && offset <= cap;
    };

    const uint32_t kSchedulerStreams = refii::guest::Address(0x8349F9B0u);

    for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t object = read32(kSchedulerStreams + slot * 4u);
        if (object < 0x10000u || object >= 0xC0000000u) {
            continue;
        }

        const uint32_t capacity = read32(object + 8u);
        const uint32_t write_cursor = read32(object + 32u);
        const uint32_t write_consumed = read32(object + 36u);
        const uint32_t write_mark = read32(object + 40u);
        const uint32_t write_wrap = read32(object + 52u);
        const uint32_t published_end = read32(object + 60u);
        const uint32_t reader_consumed = read32(object + 64u);
        const uint32_t published_cap = read32(object + 68u);

        if (!sane_offset(write_cursor, capacity) ||
            !sane_offset(write_mark, capacity) ||
            !sane_offset(write_wrap, capacity) ||
            !sane_offset(reader_consumed, capacity)) {
            continue;
        }

        // Publish the writer's boundary and import the reader's, exactly as the guest's own
        // publish does. This does not consume unread messages by itself.
        uint32_t next_published_end = write_mark;
        uint32_t next_published_cap = write_wrap;
        if (reader_consumed < write_cursor || reader_consumed > write_wrap) {
            next_published_cap = capacity;
        }
        if (next_published_end == next_published_cap) {
            next_published_end = 0u;
        }

        // The refresh half. The renderer's version stops at `last_read - 1`, because when
        // the renderer is built there is a real consumer moving the read head and this only
        // has to IMPORT its position. Headless there is no such consumer, so importing a
        // head that never moves publishes a ring that never drains: measured, this left
        // reader_consumed pinned at 0xEF while the writer advanced, which stalls the moment
        // the writer wraps onto it.
        //
        // So the host asserts what a null device actually did: it consumed everything. The
        // read head follows the write head, and reader_consumed sits ONE BEHIND it — the
        // ring's own "empty" encoding, the same `x == 0 ? capacity - 1 : x - 1` the guest
        // uses. That satisfies both of `sub_8222ABE0`'s spins at once, which is the tell
        // that it is the right state rather than a number that happens to unblock one:
        // the pre-wrap loop wants `write >= read` and the post-wrap loop wants the reader
        // not to be trapping the writer, and "empty" is the only state that is both.
        const uint32_t next_reader_consumed =
            write_cursor == 0u ? capacity - 1u : write_cursor - 1u;

        if (!sane_offset(next_reader_consumed, capacity)) {
            continue;
        }

        bool changed = false;
        const auto store_if = [&](uint32_t at, uint32_t was, uint32_t now) {
            if (was != now) {
                write32(at, now);
                changed = true;
            }
        };

        // PRODUCER-FACING FIELDS ONLY.
        //
        // An earlier version also wrote the consumer's own read state — +12 read cursor,
        // +16 visible end, +20 read limit, +24 last read, +28 read cap — mirroring what the
        // renderer's repair does. That killed the process within seconds of guest init, and
        // an A/B with REFII_RETIRE_SCENE_STREAMS=0 confirmed the write was the cause.
        //
        // The reason is that the guest's consumer thread is NOT absent, only starved: it is
        // the one spinning at 0x822DFF2C inside sub_822DFE08, which our own drain logger has
        // been reporting for months. Moving its read head from Godot's thread while it is
        // mid-read hands it a position it never asked for. Publishing the writer's boundary
        // and the space the producer may use is safe; steering the reader is not.
        store_if(object + 36u, write_consumed, reader_consumed);
        store_if(object + 60u, published_end, next_published_end);
        store_if(object + 68u, published_cap, next_published_cap);
        store_if(object + 64u, reader_consumed, next_reader_consumed);

        // Once per slot, so a run says the drain engaged without printing every step.
        static bool announced[4] = {false, false, false, false};
        if (changed && !announced[slot]) {
            announced[slot] = true;
            REXLOG_INFO(
                "Refii host: retiring guest scene stream slot {} (object=0x{:08X} "
                "capacity=0x{:X} write=0x{:X} reader_consumed 0x{:X} -> 0x{:X})",
                slot, object, capacity, write_cursor, reader_consumed,
                next_reader_consumed);
        }
    }
}

/// The guest's frame-sync state, from `Render_FrameCompletionCallback_INFERRED` (0x82B90180)
/// and its reader `sub_82B90048`. Two records of 24 bytes — a double buffer, not a ring.
/// Base = `kFrameSyncRecords + 24*i`:
/// `+0` u64 stamp (this frame), `+8` u64 stamp (the previous one, carried), `+16` u32 completed
/// count, `+20` u32 reader lock. `kFrameSyncIndex` names the newest published record.
constexpr refii::guest::Addr kPresentFrameCount{0x83497120u};
constexpr refii::guest::Addr kFrameSyncIndex{0x83497124u};
constexpr refii::guest::Addr kFrameSyncRecords{0x83491D98u};
constexpr uint32_t kFrameSyncStride = 24u;
constexpr uint32_t kFrameSyncSlots = 2u;

/// How many completions one step may report. A bound, not a rate — see the loop.
constexpr uint32_t kMaxCompletionsPerStep = 16u;

uint32_t g_frame_completions_signalled = 0;

/// Reports the swap completions that no GPU is here to report.
///
/// `Render_PresentFrame_INFERRED` bumps `g_PresentFrameCount` at 0x82B6F290; on hardware the
/// GPU then runs `Render_FrameCompletionCallback_INFERRED` to publish a new frame record.
/// `sub_82B6FCE8` reads both and hands `presented - completed + 1` to the 3D Engine thread as
/// its frame deadline, so completions frozen at zero make that term grow without bound and the
/// game thread parks for ever in `while (!*(BYTE*)(a1+168)) SleepEx(0,1)`.
///
/// The six steps are the callback's own, in its order, with the index published LAST exactly
/// as its `stwcx.` does: pick the slot the reader is not holding, carry the previous stamp
/// into `+8`, stamp `+0` with the guest timebase, set `+16` to the previous count + 1, then
/// publish the index.
void SignalGuestFrameCompletions() {
    // Only while standing in for the GPU: with a real submission path the guest's own callback
    // owns this state.
    if (!g_no_gpu_armed) {
        return;
    }

    // Two publishers (the step and the completion thread) must not race the double buffer.
    std::lock_guard publishers(g_completion_mutex);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto write32 = [base](uint32_t at, uint32_t value) {
        const uint32_t raw = __builtin_bswap32(value);
        StoreGuest(base, at, &raw, sizeof(raw));
    };

    const uint32_t presented = read32(kPresentFrameCount);

    // Caught up: nothing presented since the last step, so nothing to complete.
    uint32_t index = read32(kFrameSyncIndex);
    if (index >= kFrameSyncSlots) {
        // The callback only ever publishes 0 or 1; anything else is not this state.
        return;
    }

    for (uint32_t reported = 0; reported < kMaxCompletionsPerStep; ++reported) {
        const uint32_t current = kFrameSyncRecords + (index * kFrameSyncStride);
        const uint32_t completed = read32(current + 16u);

        // Count, not index: `presented` and `+16` are matching counters, caught up one per
        // iteration so a step spanning several presents leaves no permanent lag.
        if (completed >= presented) {
            break;
        }

        const uint32_t next = read32(kFrameSyncRecords + 20u) != 0u ? 1u : 0u;
        const uint32_t target = kFrameSyncRecords + (next * kFrameSyncStride);

        // The previous stamp moves into +8; +0 takes the time now. Two big-endian halves: the
        // guest's qword is stored high word first.
        write32(target + 8u, read32(current));
        write32(target + 12u, read32(current + 4u));

        // The guest's own clock: `mftb` in recompiled code is the payload's tick count (under the
        // driven time base, the virtual tick), so the stamp is in the units `sub_82B90048`'s
        // readers divide by `dbl_834970A8` to get seconds.
        const uint64_t now = refii_bridge_rex_query_guest_tick_count();
        write32(target, static_cast<uint32_t>(now >> 32));
        write32(target + 4u, static_cast<uint32_t>(now & 0xFFFFFFFFu));

        write32(target + 16u, completed + 1u);

        // Last, and on its own: the reader takes the index and then the record it names.
        write32(kFrameSyncIndex, next);
        index = next;
        ++g_frame_completions_signalled;
    }
}

void KeepGuestIdleCounterClear() {
    if (!g_no_gpu_armed || g_guest_device == 0) {
        return;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const uint32_t zero = 0;
    StoreGuest(base, g_guest_device + kDeviceOutstandingIbs, &zero, sizeof(zero));
}

std::vector<std::string> SplitArguments(const char* packed) {
    std::vector<std::string> arguments;
    if (packed == nullptr) {
        return arguments;
    }

    // Newline-separated: an argument can contain spaces (a path), so splitting on
    // whitespace would corrupt exactly the inputs that matter.
    std::istringstream stream{std::string(packed)};
    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty()) {
            arguments.push_back(line);
        }
    }
    return arguments;
}
}  // namespace


#if defined(REXGLUE_ENABLE_REXNET)
#include <rex/net/rexnet.h>
#include <rex/net/rexnet_ffi.h>
#include <rex/net/pseudonym.h>

namespace {
/// Appends one field, stripped of anything that could forge a delimiter.
void Field(std::string& row, const std::string& value, char terminator) {

    for (unsigned char c : value) {
        row.push_back(c < 0x20 || c == 0x7F ? ' ' : static_cast<char>(c));
    }
    row.push_back(terminator);
}

void FieldNum(std::string& row, uint64_t value, char terminator) {
    Field(row, std::to_string(value), terminator);
}

std::string PeerText(const RexNetPeerId& peer) {
    char buffer[256] = {0};
    const uint32_t written = rexnet_peer_id_string(&peer, buffer, sizeof(buffer));
    return written == 0 ? std::string() : std::string(buffer, written);
}

std::string HexOf(const std::array<uint8_t, 16>& id) {
    static const char* kDigits = "0123456789abcdef";
    std::string out;
    out.reserve(32);
    for (uint8_t b : id) {
        out.push_back(kDigits[b >> 4]);
        out.push_back(kDigits[b & 0xF]);
    }
    return out;
}

/// Are these the same peer? `RexNetPeerId` is a length-prefixed multihash.
bool SamePeerId(const RexNetPeerId& a, const RexNetPeerId& b) {
    return a.len == b.len && std::memcmp(a.bytes, b.bytes, a.len) == 0;
}

/// `g_XrnmNetContext` — IDA names it; `sub_822EDCF0` reads the admit gates off it.
constexpr refii::guest::Addr kXrnmNetContext{0x83497490u};

/// Are we hosting a session at all — the session machinery's byte, never ours to write.
constexpr uint32_t kNetHostingByte = 0xF3u;

/// Will we admit a joiner: `XboxLive.SetSessionJoinable`'s byte.
constexpr uint32_t kNetJoinableByte = 0xF5u;

/// The live node, or null when RexNet is off, not built in, or not up yet.
///
/// A RAW STATIC, NOT A REFERENCE. `RexNet::shared()` is `shared_instance_`, deleted by the
/// guest thread's last XNetCleanup (`DestroyShared`) with no refcount and no lock the SDK
/// exposes, so the bridge cannot hold the node alive across a call. What it can do: take the
/// pointer once at entry, null-check it, and re-check it (`NetStillUp`) between calls into the
/// node, so a teardown between two calls is noticed instead of dereferenced. A teardown DURING
/// a node call is the SDK's to serialise; the shell never calls here after it has stopped the
/// guest.
rex::net::RexNet* Net() { return rex::net::RexNet::shared(); }

/// The node taken at entry is still the shared instance.
bool NetStillUp(const rex::net::RexNet* net) { return net != nullptr && Net() == net; }

/// The standard guest-address bound for the net exports.
bool NetPlausible(uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; }

/// `g_XrnmNetContext`, or 0 when the guest has none yet.
uint32_t NetContext(const uint8_t* base) {
    const uint32_t context = base == nullptr ? 0u : GuestWordAt(base, kXrnmNetContext);
    return NetPlausible(context) ? context : 0u;
}

/// 32 hex digits to a 16-byte id; false on any other input.
bool ParseHex16(const char* text, std::array<uint8_t, 16>& out) {
    if (text == nullptr || std::strlen(text) != 32) {
        return false;
    }
    for (size_t i = 0; i < 16; ++i) {
        unsigned value = 0;
        for (size_t nibble = 0; nibble < 2; ++nibble) {
            const char c = text[i * 2 + nibble];
            unsigned digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else return false;
            value = (value << 4) | digit;
        }
        out[i] = static_cast<uint8_t>(value);
    }
    return true;
}

/// The guest is already a joiner in `host`'s `session_id`: the arm that resolved names that
/// host and session, and the guest's own bytes say it is in a session as a joiner
/// (`g_XrnmNetContext` +52 session, +54 joiner). Re-arming here made the guest run
/// JoinSessionFromInvite inside the session it was in (2026-10-07, 17:08:57), so both join
/// routes refuse on it.
bool GuestAlreadyJoined(rex::net::RexNet* net, const RexNetPeerId& host,
                        const std::array<uint8_t, 16>& session_id) {
    std::optional<rex::net::RexNet::AcceptedInvite> joined = net->accepted_invite();
    if (!joined.has_value() || !SamePeerId(joined->host, host) || joined->session_id != session_id) {
        return false;
    }
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    const uint32_t context = NetContext(base);
    return context != 0u && GuestByteAt(base, context + 52u) != 0u &&
           GuestByteAt(base, context + 54u) != 0u;
}

/// Resolves a peer id the shell named. Accepts the canonical peer string; a friend code is
/// parsed by the caller, which needs its embedded name anyway.
bool ParsePeer(const char* text, RexNetPeerId& out) {
    return text != nullptr && text[0] != '\0' && rexnet_peer_id_parse(text, &out);
}

uint32_t Emit(const std::string& text, char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0 || text.size() >= capacity) {
        return 0;
    }

    std::memcpy(out, text.data(), text.size());
    out[text.size()] = '\0';
    return static_cast<uint32_t>(text.size());
}

/// The bridge-side carry for a DRAINING reader. The node's Take* calls empty their queue on
/// read, so a record the caller's buffer could not hold would be lost; it is kept here and
/// emitted on the next call instead. Whole records only: the text handed out always ends on a
/// record boundary.
struct DrainCarry {
    std::mutex mutex;
    std::string pending;
    uint64_t dropped = 0;

    /// Bounded: past this the oldest whole records are dropped, counted, rather than growing
    /// without a reader.
    static constexpr size_t kMaxPending = 1u << 20;
};

/// Appends `fresh` to the carry and emits as many whole records as fit in `capacity - 1`
/// bytes; the rest waits. A single record longer than the buffer can never be emitted and is
/// dropped, counted, so it does not block every record behind it. 0 when nothing fits.
uint32_t EmitRecords(DrainCarry& carry, const std::string& fresh, char* out, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(carry.mutex);
    carry.pending += fresh;
    while (carry.pending.size() > DrainCarry::kMaxPending) {
        const size_t cut = carry.pending.find('\n');
        if (cut == std::string::npos) {
            carry.pending.clear();
            ++carry.dropped;
            break;
        }
        carry.pending.erase(0, cut + 1);
        ++carry.dropped;
    }
    if (out == nullptr || capacity == 0 || carry.pending.empty()) {
        return 0;
    }

    const size_t room = capacity - 1;
    size_t take = 0;
    if (carry.pending.size() <= room) {
        take = carry.pending.size();
    } else {
        const size_t cut = carry.pending.rfind('\n', room - 1);
        if (cut == std::string::npos) {
            // The first record alone overflows the buffer: drop it rather than hold the rest.
            const size_t first = carry.pending.find('\n');
            carry.pending.erase(0, first == std::string::npos ? carry.pending.size() : first + 1);
            ++carry.dropped;
            REXLOG_INFO("RefiiNet drain: record longer than the caller's buffer ({} bytes) dropped; {} so far",
                        capacity, carry.dropped);
            return 0;
        }
        take = cut + 1;
    }

    std::memcpy(out, carry.pending.data(), take);
    out[take] = '\0';
    carry.pending.erase(0, take);
    return static_cast<uint32_t>(take);
}
}  // namespace
#endif


extern "C" {
/// ABI version. Bump on any breaking change so a stale library is detected rather than
/// crashing the editor.

///
/// 4 adds RefiiHostSetControllerState / RefiiHostGetVibration.
/// 5 adds RefiiHostGetVideoFrame.
/// 6 adds RefiiHostGetVideoPlanes (GPU-side colour conversion).
/// 7 adds RefiiHostGetRequestedVideo.
/// 8 adds RefiiHostGetRequestedVideoName.
/// 9 adds RefiiHostFinishMovie.
/// 10 adds RefiiHostGetFrontEndState.
/// 11 adds RefiiHostGetFrontEnd.
/// 12 adds RefiiHostGetUiElements (13 replaced a truncating slides-only form).
/// 14 adds RefiiHostMovieState.
/// 15 adds RefiiHostGetCamera.
/// 16 adds RefiiHostGetUiAttributes.
/// 17 widens RefiiHostGetFrontEnd to NINE words — the ninth is the active screen's vtable,
///    which is what names the screen the front end hands over to.
/// 18 adds a tenth: the GUI object's own vtable, without which the other nine cannot be
///    known to be about a front-end GUI at all.
/// 19 adds RefiiHostIsInGame and RefiiHostGetRenderFence — the guest's own in-game flag,
///    and the silent fence the level load waits on.
/// 20 adds RefiiHostGetLoadingScreen — the take-down handshake the game thread blocks on.
/// 126 adds RefiiHostGetCoopShares — the co-op screen's gold/experience shares, so the host
///     owns those labels instead of taking text the guest stops writing.
/// 125 adds RefiiHostGetCoopRole — 0 no session, 1 host (solo reads as host), 2 joiner, off
///     the co-op manager's own session/host/joiner bytes. The pause menu and the native-hero
///     work both need to know which side they are running on, and nothing else in the bridge
///     said.
/// 23 adds RefiiHostGetStartLevel — the world/level/entrance the guest asked for.
/// 22 adds RefiiHostGetSceneStreams — the command-stream rings, read-only.
/// 21 adds RefiiHostGetInitMarkers — how far Game_InitDatabases has got, which it does not
///    say itself.
/// 24 adds RefiiHostGetEntities — every live entity's world transform and model.
/// 25 names the placement vtables RefiiHostGetEntities could not resolve.
/// 142 widens RefiiHostGetEntities to stride 20: the guest's previous transform (component key
///     3 at +0xA0 position, +0xB0 quaternion, latched by the tick's first act `sub_8222B2E0`)
///     and its interpolation enable at +0xC9, the pair the guest blends in `sub_822112D8`.
/// 155 widens the entity walk to 29 words: the renderable's stated APPEARANCE — alpha, camera
///     alpha, scale, tint and the drawable/saturation flags — as the guest's appearance setters
///     stated it (hooked at their common tail `sub_82573318` in entity_appearance.cpp).
/// 154 adds RefiiHostGetDisplayBox — the showing display box and its class, so the owned query
///     box stands up for `GUI.Display*Question` as well as the modal router's QueryBox.
/// 153 adds the pub-game seam: RefiiHostClaimPubGames, RefiiHostPostPubGameCommand,
///     RefiiHostTakePubGameEvents, RefiiHostGetPubGameProfile; drops RefiiHostSetPubGameResult.
/// 152 adds RefiiHostTraceGpuPasses — Godot's per-pass GPU timestamps as Tracy GPU zones and
///     plots, so the GPU frame is split on the same timeline as the CPU phases.
/// 151 widens RefiiHostGetRealEstate to fifteen words: the hero's TITLE record, which is how
///     the guest names the owner of a hero-owned building (`sub_82998FB0` -> `sub_824D8A10`).
/// 150 widens RefiiHostGetWorldSprites to eight words: the ENTITY through the sprite's handle
///     (`*(*(obj+28))`; `obj+24`, published until now, is the handle's vtable and the SAME on
///     every sprite) and the sprite OBJECT as its identity.
/// 149 adds RefiiHostGetItemAugments — the augment pairs socketed into one inventory record,
///     off the item's own type-1 extra record (what `sub_82469AE8` prints on the item page).
/// 148 takes the targeting highlight from the guest's own setter (`sub_825738F0`, hooked in
///     target_highlight.cpp) instead of polling the renderable; {0,0,0,255} is the actively
///     written clear value, so a poll cannot tell it from "never targeted".
/// 147 adds RefiiHostGetStatedFx / RefiiHostGetStatedFxCount — the effect transforms the guest
///     states through `sub_832325A8` and `sub_83231C40`, recorded by a hook.
/// 146 widens the entity walk to 21 words: the renderable's stated highlight RGBA (+96..99).
///     Highlighted iff those bytes differ from {0,0,0,255}.
/// 145 widens RefiiHostGetEntityAnimState to twelve words a leaf: the record's weight_previous
///     and weight_current. The guest crossfades the winner against the runner-up by
///     `(1 - alpha) * winner.weight_previous + alpha * winner.weight_current` (0x82211B08..
///     0x82211B24 in `sub_82211660`, reading 0x14 and 0x18 off the winner record).
/// 144 adds RefiiHostGetAnimWalkStats — why a rig is not clip-driven, split into "no component
///     bit", "component unresolved", "controller found" and "controller found but empty".
/// 143 adds RefiiHostGetCameraPair — the previous camera the guest latched at the game tick
///     (`game+0x10`, stored by the inlined run at `0x82277234`) beside the live one, the pair
///     `sub_8228FEF0` blends by the alpha `sub_822112D8` gets for bodies. The camera-manager
///     chain is entry+4 -> player, +4 -> view, +4 -> manager; the live block is `manager+0x90`.
/// 26 returns that unknown-vtable list as {vtable, count} pairs with an explicit length,
///    since a vtable word reading 0 is an observation the walk can make and 25's zero
///    terminator swallowed the rest of the list.
/// 27 adds RefiiHostGetEntityPose — a live character's bone pose and its parent table.
/// 28 adds RefiiHostGetEntityRestPose — the bind pose Godot needs to build a skin.
/// 29 widens RefiiHostGetUiElements to stride 4, adding the element TYPE byte so the ordinal
///    join between the guest walk and the .bgf can be CHECKED, not assumed.
/// 30 widens it to stride 5, adding the element FLAGS halfword — bit 0x1000 `globalactive`
///    is the guest's own draw gate.
/// 31 adds RefiiHostGetFrontEndModel — the front end as a MODEL (which entries exist, which
///    screen is up) rather than as widgets, for a natively rebuilt menu.
/// 33 adds RefiiHostGetEntityBoneNames. The guest skeleton and the model number their bones
///    differently (161 against 143 for the child hero), so the name is the only join.
/// 34 widens RefiiHostGetRenderFence with the five values that say WHICH arm holds the
///    engine-preloading flag, and renames its third word from "retired" to the deadline it
///    actually is.
/// 35 adds RefiiHostPostFrontEndAction and RefiiHostGuestTickPump — the first route for HOST
///    work onto the guest's own tick thread. A memory write cannot do this job: the
///    start-game commit ALLOCATES, and its globals are refcounted strings.
/// 36 adds RefiiHostGetEntityComponents — the {id, pointer} pairs one entity carries, which
///    is a live-memory fact no decompile answers.
/// 37 adds RefiiHostGetPresentations and gives RefiiHostGetUiElements a leading presentation
///    argument (0 keeps the old front-end meaning). The old walker went through `gui+188`, a
///    field only the derived front-end GUI has, so the HUD and the thirty in-game menus were
///    unreachable through it.
/// 38 widens RefiiHostGetPresentations to stride 5, adding whether the presentation is
///    actually SHOWING — the root element's `active` bit (0x2000), the freeze byte, and the
///    guest's own per-frame active-element count. Live in the registry is NOT visible; the
///    guest keeps closed menus standing up permanently.
/// 39 adds the presentation-level SUPPRESSION byte `pres+0x368` to the state word (bit 2).
///    That is the byte the scene draw itself reads, and the one the game writes to park a
///    menu; the update gate `+0x369` and the root `active` bit are not the whole contract.
/// 139 adds the stated MOON PHASE to RefiiHostGetTimeOfDay (`timing+0x38`, the field
///     `Timing.GetMoonPhase`/`sub_825A1BF0` returns); -1 when it could not be read.
/// 138 gives RefiiHostGetUiAttributes two leading columns per element — the element address
///     and its name hash — so the caller can check the ordinal join.
///     The row stride is now `2 + id_count * 2`.
/// 40 gives RefiiHostGetUiAttributes a leading presentation argument, matching
///    RefiiHostGetUiElements — opacity is how the game hides the in-game HUD, one
///    `setAttribute` of 0 on Scene.Layer multiplied down the tree.
/// 41 adds RefiiHostGetEntityMorphParts — the model hashes a COMPOSITE character is built
///    from, off CECGraphicAppearanceMorph (component 17). Those characters carry a live
///    skeleton and a zero model hash on component 6, so this is the only route to geometry.
/// 42 widens RefiiHostGetFrontEndModel to 11 words: how many saves the guest has ENUMERATED,
///    from the same vector its own "Load Game" branch tests.
/// 43 adds the audio surface. RefiiHostPullAudio drains the guest's MIXED output and, by
///    draining it, drives the guest's audio clock — see host_audio.cpp. The RefiiHostVoice*
///    calls expose individual voices so Godot can place each sound itself.
/// 44 adds RefiiHostGetSimTick (dword_83319650, the guest's own 15 Hz game tick) and WIDENS
///    RefiiHostGetEntityPose with `out_torn`. That widening is the reason this list matters:
///    changing a signature is as much an ABI break as adding an export, it went in without a
///    bump, and the two sides drifted to 45-against-44. The mismatch is not caught cleanly —
///    it segfaulted in UI startup with one line of log to explain it.
/// 45 adds RefiiHostGetLoadingScreenArt and RefiiHostWorldIsReady — which loading screen the
///    guest picked, and its own "the world is ready" byte.
/// 50 adds RefiiHostWorldIsActive — World+0xA8, the ONE "the world is up and populated" bit
///    that both the travel and the load-from-save paths write. Replaces a host-side timeout.
/// 49 adds RefiiHostDescribeGameFlowQueue — the queue as text, because a walk of it kept
///    coming back empty while a level change was demonstrably in flight.
/// 48 adds RefiiHostSetWorldBuildPending — the host answering "IsEnginePreLoading" so the
///    guest waits in ITS OWN loading screen (state 7) until Godot has mounted the region.
/// 47 adds RefiiHostGetLevelChange — the queued CmdLevelChange itself: its state word, its
///    destination and its fullLoad flag. game+0x105 is a rising edge only (cleared in state 3,
///    AFTER the state-2 entity teardown), so it cannot say when to free a scene.
/// 46 adds RefiiHostGetCurrentLevel — the level the guest is STANDING IN, read from the live
///    World and Level objects. The start globals it replaces are written only by game-START
///    paths, so polling them cannot see a door, a teleporter or fast travel.
/// 51 adds RefiiHostGetEntityLocomotion — the locomotion state the guest is ACTUALLY IN, by
///    name. The hero's locomotion machine IS an entity mode (component 21, the mode whose
///    GetModeType() is 2/3/4), and it keeps its current state as a named object, so the host
///    can follow the guest's own choice instead of re-deriving it from inputs it would have to
///    guess at. The velocity/control pair it tests against is NOT included, and deliberately:
///    that context is a stack temporary built per tick in sub_8228D058 and is not resident.
/// 67 adds RefiiHostGetUiPreviewObject — the model hash and placement of the 3D object a menu
///    panel shows.
/// 97 moves ACHIEVEMENTS to Godot: RefiiHostGetTitleXdbf, RefiiHostSetAchievementUnlocks and
///    RefiiHostGetAchievementsUiRequests join RefiiHostGetAchievementUnlocks; the definition and
///    icon exports are gone with the native store. See Engine_Achievements.
/// 68 adds the ACHIEVEMENT exports — unlocks, definitions and icon PNGs. The runtime half
///    stays native: it is guest-facing and owns the XDBF. See Engine_AchievementToast.
/// 66 adds RefiiHostGetWorldIcons — the world-icon queue and its two gates, the channel
///    property income (icon type 37) and every scripted GUI.AddWorldIcon travel on.
/// 65 adds RefiiHostGetMovieRect — the destination rect the guest states for the live movie,
///    so an embedded preview is known to be embedded AT OPEN, not a sampling tick later.
/// 64 adds RefiiHostGetMenuBox — the Lua-opened option list's MODEL (open flag, count,
///    selection, cancelable, per-option enabled bits), a plain struct at `*(guiOwner+56)`,
///    not element state.
/// 63 widens RefiiHostGetPauseState with the modal MODE (`guiOwner+40`: 1 idle, 2 MessageBox,
///    3 QueryBox, 5 numeric adjuster) — what separates a box the host owns from one it must
///    leave mirrored.
/// 62 adds RefiiHostPauseTabsAvailable — `sub_826B83D0` as pure reads; the guest's tab push
///    never re-checks availability, so the host must not offer a tab prev/next would skip.
/// 61 adds RefiiHostGetPauseState and RefiiHostPauseTabSelect — the owned pause menu's read
///    and its dispatch. The dispatch is the guest's own lever: a pure write the per-tick GUI
///    update polls, after which the guest calls `sub_826B89E0` itself.
/// 60 adds RefiiHostGetSubtitles — the front-end subtitles setting as the guest's own reader
///    (`sub_82382558`) last answered on the tick thread, so the menu can mark the current
///    choice the way `sub_826E2220` does with SetItemEmphasis.
/// 59 adds RefiiHostGetDeadEntities — the guest's own DeadEntities set, so a destroyed
///    placement is not resurrected as the region's build-time copy standing in its place.
/// 58 adds RefiiHostGetEntityMeshParts — the mesh list on CECGraphicAppearanceStaticMultipleMesh
///    (component 5). Buildings and multi-part scenery keep their geometry there, not in the
///    single ModelFile slot, so an entity carrying one read as "names no model" and the region's
///    build-time copy was the only thing drawing it.
/// 76 adds RefiiHostGetLuaMenu — the expandable menu's own Lua model, read out of the
///    presentation's `lua_State`.
/// 78 adds RefiiHostGetHeroVitals and RefiiHostGetHeroInventory — hero component data as pure
///    reads. Displayed morality/purity/renown and ability ids 2/3/6 are excluded: they are
///    buffed or config-derived and can only be CALLED.
/// 77 adds RefiiHostGetScreenFade — the screen fade as a colour Godot can draw. Read-only:
///    the guest advances it at 15 Hz and two of its readers hang a level change if it stops.
/// 79 adds RefiiHostGetGameLoopPacer — `rex_lhGameLoop_Tick`'s 15 Hz fixed-step arithmetic.
///    `game+0x110` is the sim time; 0x83497124 is a 0/1 SLOT INDEX, not a count.
/// 80 adds RefiiHostTakeGuiNativeCalls — the ~120 `GUI.*` natives, intercepted by
///    implementation pointer in the game VM's `luaD_precall` (`sub_82228730`), with their
///    ARGUMENTS. Taking a call does NOT suppress it. See docs/ui_native_cut.md.
/// 81 adds RefiiHostMenuPopulating — the menu capture's settle bracket; cleared from the guest
///    tick, the first moment a population is whole.
/// 82 adds RefiiHostTakeMenuChunks — the menu contract at the C++ boundary: attributes staged
///    onto the "Parameters" element, then a one-line Lua chunk reading them back. Hooking the
///    two stagers and the runner captures every mutation. See docs/ui_native_cut.md.
/// 83 adds RefiiHostTakeUiElementChanges — element state taken at its writers: `sub_82284798`
///    (attribute-service vtable slot +0x48) for attributes, `sub_82284BE8` for the element
///    flags (0x1000 draw gate, 0x2000 active), `sub_82BB5760` for the slide.
/// 84 also walks TABLE arguments in RefiiHostTakeGuiNativeCalls, using the GAME VM's Table
///    layout — node count at +8, node array at +0x14, 20-byte nodes — by node scan, since this
///    build's string hash is MurmurHash2.
/// 84 widens RefiiHostGetUiElements to stride 6, adding the ELEMENT ADDRESS, which joins the
///    ordinal-indexed walk to the change feed.
/// 87 adds RefiiHostGetCompletedQuests (filtered on the guest thread, sorted by the caller on
///    the GDB property `DisplayPriority`), RefiiHostGetCrimeScreen, RefiiHostGetWorldMapRegion
///    / RefiiHostSetWorldMapRegion (the pump samples the named region, read back one tick
///    later) and RefiiHostGetOptions (pages 1 and 2).
/// 92 adds RefiiHostSetAssetNameResolver — Godot's hash index, the other half of 90; the guest
///    names most resources by a 32-bit hash rather than by path.
/// 101 adds RefiiHostGetSales — the SalesAndShortages list off the world, as {NameTagHash,
///    isShortage}. `record+20` is a raw FNV-1 text key, so again no string crosses.
/// 100 adds RefiiHostGetFastTravelRegions — the regions with an active stop, as FNV-1 of their
///    names, which are their own Babel keys. No string crosses the boundary.
/// 104 adds RefiiHostGetInventoryScreenMode — screen 15 is ONE screen with a mode word at
///     `screen+372` (1 Items, 3 Weapons, 4 Clothing), so its list cannot be bound by id alone.
/// 103 widens RefiiHostGetFastTravelRegions to {nameHash, ordinal} pairs and fixes its stride.
///     The container is pairs, not pointers — `sub_82518330` steps 8 — and event 41's handler
///     `sub_826C7E90` identifies a region by `Float1 - 999` indexed into that walk, never by
///     name, so the ordinal has to cross.
/// 102 adds RefiiHostGetFamily — the world map's `Family` names. The pump calls the guest's own
///     collector `sub_8246F020` while screen 23 is up and frees it with `sub_826C9450`; this
///     export only reads the shared store. Walking it here would mean reproducing an entity-UID
///     map lookup and a component-20 name fallback we have no reason to own.
/// 99 adds RefiiHostGetQuestLog — component 102's flat map at +0x10/+0x14, stride 12, `Quest*`
///    at element+4; RecordID at quest+4 and the five state bytes packed into one word. Quests and
///    Jobs are this one list split by the definition's GDB `Type`.
/// 98 adds RefiiHostGetWornItems — the hero's worn vector at component 15 + 16..20, stride 24,
///    element[0] an Entity* whose +4 is the GDB record hash and +16 the category. Nothing
///    synchronises it and a push reallocates, so the walk re-reads begin/end and discards a
///    torn read.
/// 90 adds RefiiHostSetAssetReader — the first call that goes the other way, handing the guest
///    Godot's asset reader so one mount serves both sides.
/// 89 widens RefiiHostGetWorldMapRegion by the region's ECONOMY. Not a field: `sub_8246FE20`
///    averages the region's villages on every ask, so the pump calls it.
/// 88 adds RefiiHostGetOptionsSliders — options pages 3 and 4 off the live screen object, not
///    the profile blob (freed before the call returns): gamma at `page3+0x30` (`sub_829C07D0`),
///    the three volumes and the speaker mode at `page4+0x2C/0x30/0x34/0x38` (`sub_829C1488`).
/// 86 adds RefiiHostGetHeroDerived — the values ABI 78 excluded, sampled by the pump on the
///    guest tick thread; this export only reads the shared store.
/// 85 adds RefiiHostSetGuiPredicate — suppression: the thunk writes the host's value onto the
///    Lua stack and returns without calling through. Opt-in by name, because fifteen of the
///    twenty predicates read state the guest's own setters maintain.
/// 106 adds RefiiHostGetRealEstate — screen 28's one building. Its left column is a list of
///     COMMANDS, not properties, so there is nothing to select and nothing to key on: one
///     record, sampled while the screen stands. The guest's own value functions answer every
///     number because each of them walks a prosperity term, a GDB adjustment array or a
///     five-axis opinion weighting; the host owns all of the presentation.
/// 107 widens RefiiHostGetQuestLog to EIGHT words — the star rating, both rewards, the
///     objective's `%1` parameter, the days remaining and the task count, all plain fields on
///     the quest object — and adds RefiiHostGetQuestTasks, which crosses one quest's task
///     lines as Babel TAGS. The world map's quest and job pages are blank without them: the
///     quest page is a single concatenated string and the task lines are most of it.
/// 108 was to have added HUD slide and image registry readers and does not: neither has a
///     readable address. See the note above RefiiHostGetHudTexts — the writers are
///     write-through into a runtime slide controller, and the image hash lives on the live node
///     rather than on the binding record this file's walker returns. Slides and textures
///     already cross per element through the ordinary element walk.
/// 109 widens RefiiHostGetSales to FIVE words — the description and region Babel key hashes,
///     the days left (`Duration - today + activationDay`) and the raw price multiplier. Two
///     words were enough to build the ROW and left the sales PAGE entirely blank.
/// 110 adds RefiiHostSet/GetQuestTravel — a job's distance and travel time. The two providers
///     are the region page's own and take an `LHString*`; the quest's `ObjectiveLevel` is inline
///     at `quest+132`, so no extra hop. The travel provider takes 0 here where the region page
///     takes 1, and under an hour it appends nothing — an empty travel time is shipped behaviour.
/// 111 widens RefiiHostGetQuestTravel by the objective level's REGION and the A-button
///     PROMPT. Neither is a field on the quest. The region is a lookup — `sub_829AFE58` asks
///     the region container which region owns the level (`sub_82518490`) and sets the element
///     by TAG. The prompt is a DECISION over four guest predicates (`sub_824707E8`,
///     `sub_82470F80`, `sub_82471438` and a level comparison), so the pump runs the guest's
///     own tests and crosses the tag it lands on, or nothing when the button is hidden.
///     Both lines were declared in the page and never assigned. It also adds
///     RefiiHostGetFamilyDetails — the FamilyPage had no provider at all, and it needed no new
///     walk: the pump already collects the 52-byte records for the row list, and ten of the
///     page's eleven elements are formatted strings sitting in them. And
///     RefiiHostSet/GetShopPrice — the shop's `PriceValue`, which the item book could never
///     answer because what a shop CHARGES is not what an item is WORTH.
/// 116 adds RefiiHostGetAttachments — every stated parent/child/dummy attachment, so Godot can
///     hang a weapon off ITS OWN bone. The guest recomputes an attached entity's placement from
///     its own pose every update; with the host playing the animation, that placement no longer
///     describes the body the child is attached to.
/// 117 widens RefiiHostGetEntityAnimState to TEN words a leaf — overlay membership, the mask
///     node and the priority bitmask — and adds RefiiHostGetAnimMask, so the host composites
///     overlays per bone the way `sub_8220CB18` does instead of folding them as primaries.
///     It also appends component 7's two eye models to RefiiHostGetEntityMorphParts.
/// 118 adds RefiiHostGetTopBoxBody — the front TopBox record's body as its provider object
///     states it, so the host composes the box the way `sub_821787D0` does instead of reading a
///     registry string that outlives its record.
/// 119 adds RefiiHostGetBoneMorph — the entity's combined skeletal morph record (comp6+152):
/// per-bone translation offsets and scales the guest applies after the animation fold — and
/// RefiiHostGetEntityWornItems, the hero walk of ABI 98 for any entity, with gender and open flags.
/// 120 adds RefiiHostGetTargetedQuery — the targeted-citizen record at `*(*(GUI+32)+112)`, so
/// the host can restate registry 10 (SlideSetName) and the four registry-9 toggles the guest
/// publishes from it. Neither registry is walkable: both are write-through.
/// 127 adds RefiiNetCoopSend / RefiiNetCoopReceive — Refii's OWN co-op channel, on its own
/// port, carrying host state the title has no concept of (which body player two is standing in).
/// Nothing the guest sends or parses changes, so a save stays a save.
/// 128 adds RefiiHostGetCoopMannequin — the co-op screen's spawned henchman preview. Screen 24
/// does not use the 128-byte preview struct the other six do: `sub_82438818` spawns a live
/// `CoopScreenTempHenchmanToDisplay` entity, stores it at `screen+276` and a preview-manager
/// ticket at `screen+284`. There is no model hash to hand over; the host draws the entity.
/// 129 adds RefiiHostGetHeroWeapons — the two GDB record ids the hero's weapon set holds
/// (`CECWeaponSetManager`, id 28: `+84` melee, `+88` ranged), the same record-id space the
/// inventory keys on. What player two's own hero carries across the wire.
/// 130 adds RefiiHostGetHeroLooks — the hero's character-creation record (comp 17 + 180, what
/// `sub_825B5F30` writes) and the three CECStats body scalars the vitals reader does not carry:
/// tan +52, fat +56 (both 0..1), height +60 (-1..1) — `sub_824D77F8` / `sub_824D7868` /
/// `sub_824D7A68` are the clamped setters, verified.
/// 131 adds RefiiHostGetCoopHeroSave — whether a co-op hero save has been chosen (the guest's
/// own predicate `sub_8243AE90`: the LHString at `SaveMgr+4` has a length) and the HeroSave block
/// `sub_824482E0` read into `SaveMgr+1016` for it, flattened with its two experience vectors.
/// 132 adds RefiiHostGetCoopHeroSaveName — the chosen co-op hero save's file name, the
/// `szFileName` the guest keeps at `SaveMgr+4`, which is the save's directory under
/// `user_data_root/<xuid>/<title>/00000001/`. The host reads that save's own files from it.
/// 133 adds RefiiHostGetCoopPlayerEntities — both players' entity addresses off the player map
/// (`game+0x9C`, keys 1 and 2, record+16 → control → entity), so the host can name which rig is
/// player two's when a co-op body draws wrong.
/// 134 adds RefiiHostGetCoopSession — the co-op session as ONE record off the authoritative
/// CNetworkManager (`g_XrnmNetContext`, 0x83497490): session +52, host +53, client +54,
/// lost-connection latch +238 (set first thing in HandleLostConnection 0x8232D4D8), client
/// joined +221 (set on "Client joined!", cleared in that handler), peer count +100, and both
/// players' entities off the player map. The host's session model reads this and nothing else.
/// 135 adds RefiiHostGetCoopHeldUids — the net ids of the item entities a player holds, as the
/// export's own comment cites it ("ABI 135"); 32-bit until 170.
/// 136 unknown: no comment in the code names what it added.
/// 137 adds RefiiHostGetGuestFrame — the guest's render frame, counted by the hook on the
/// 30 Hz HF pass (`sub_821A61B0`) that writes every pose. Godot reads guest state once per
/// count and interpolates between counts.
/// 156 adds RefiiHostGetEntityDrawRange — an entity's stated draw distance and fade factor off
/// its renderable (component 3 +0x28 / +0x24), the D the guest fades it out by and cuts its shadow at.
/// 157 adds RefiiHostGetWeather — the four precipitation floats of the guest's BLENDED
///     environment theme (T+0x480 SnowSize, +0x490 SnowFallSpeed, +0x4A0 RainSize, +0x4B0
///     RainDensity), the record its own rain and snow draws gate on. Indoors the painted
///     theme yields nothing and these collapse to zero, which is the only reason a room is dry.
/// 158 adds RefiiHostGetMovieSound — the sound the guest states for the movie it opened: the
///     opener's `sound` argument, the track ids of its `BinkSetSoundTrack`, and the volume it
///     states for each through `sub_82A41D88`. The host plays those tracks from its own decode;
///     the guest's copy is opened muted.
/// 159 adds the character morph feeds, each taken where the guest states it inside
///     `sub_8220DE88`: RefiiHostGetEntityPartMorphs — a part's morph targets and weights
///     (`sub_82A48B90`); RefiiHostGetEntityTextureLayers — every texture composite
///     (`sub_825B38B0`) and overlay (`sub_8222AAC8`, command type 14); and
///     RefiiHostGetBoneMorphHeight — the morph record's bone-1 height factor.
/// 160 adds dyeing: RefiiHostGetDyeStates — which garments are dyed — and the pump action
///     `Inventory:dye:<dye>:<target>:<slot>`.
/// 161 RefiiHostGetShopPrice also returns the row's item record (`*(*holder + 4)`), so the shop
///     page reads the item the ticket names instead of guessing it from the row's label.
/// 165 adds RefiiHostGetQuestTrailOverride — whether the trail follows a sale or family member
///     instead of the primary quest, which decides the world map's row emphasis.
/// 166 adds RefiiHostGetMinimapLive, widens RefiiHostGetWorldMapRegion by the region's first
///     level name, key and scenario, and RefiiHostGetQuestLog by each quest's objective entity —
///     the world map's minimap page, drawn by the host.
/// 164 adds RefiiHostGetSpellWheel — the quick spell wheel's row, cells and lists as the guest's
///     own widget holds them, so the host draws the guest's cursor instead of keeping one.
/// 163 adds RefiiHostGetThemeStack — the themes the guest has pushed on its environment theme
///     stack, by name hash and live weight, so Time Control's `EnvThemeSlowTime2` (and every
///     other pushed theme) blends over the host's own tone map at the guest's stated weight.
/// 162 adds RefiiHostGetClothingMannequin — screen 16's preview mannequin, which the remaster
///     draws for an outfit row and dresses through the pump's `Inventory:tryon`.
/// 167 adds RefiiHostGuestBuild / RefiiHostGuestAddress / RefiiHostGuestAddressToTu1 -- which
/// executable is running (0 TU1, 1 unpatched) and the address translation between the two, and
/// reports every GUI screen id in TU1 numbering whichever build is running.
/// 168 adds RefiiNetOwnSession / RefiiNetJoinSession -- our session id for a Discord join
///     secret, and a join from that material without a friend's presence.
/// 169 moves Discord into RexNet: RefiiNetDiscordActivity / RefiiNetDiscordEnabled feed the
///     SDK's own Rich Presence (application id from the title's rexnet.toml); the join
///     secret and the join itself are the node's, RefiiNetJoinSession routes through it.
///     Also adds RefiiNetDeclined / RefiiNetDelivered — the peers that dismissed, and the
///     peers that acknowledged, a friend request we sent — and widens RefiiHostGetCoopSession
///     from 10 to 12 words (game+284 mode, player two's pad) for a caller whose buffer asks.
/// 170 widens RefiiHostGetCoopHeldUids to the entity's 64-bit uid.
/// 171 adds RefiiNetJoinRequests / RefiiNetJoinRequestReply — the Discord "ask to join" prompt
///     the node holds for an answer, and the answer — and RefiiNetNotices, the node's own
///     word on an invite that could not be delivered or a join the host never answered.
///     Also adds RefiiHostGetHeroStateApplied — the HeroState version the pump last applied
///     to a player's current body, the ack Godot sends the joiner.
/// 172 adds RefiiHostGetItemDyeLayers — the texture layers the guest's own dye builder states
///     for one inventory item, worn or carried, so the menu shows a dyed garment dyed.
/// 173 adds RefiiHostGetClassicFrame — the frame-constant sources the title's render passes
///     read (ambient, sun, lightmap factor, normal darkening, the atmosphere block, the theme
///     block), RefiiHostGetClassicLights (the scene's active light list) and
///     RefiiHostGetClassicMists (its mist volumes), for the classic renderer's feed; replaces
///     the device constant shadow read, which is empty here.
/// 174 adds RefiiHostGetClassicParticles — the render scene's particle groups as their twins
///     hold them (records, material, keyframes, transform), for the classic particle draw.
/// 175 adds RefiiHostGetThemeBlends — the script theme blend list (`BlendToEnvironmentTheme`,
///     `mgr+148`), which the guest folds before the stack; RefiiHostGetThemeStack reads only the
///     spell list at `mgr+164`.
/// 181 adds RefiiHostGetEntityCasterFlags — an entity's stated sun-shadow casting off its
///     renderable (component 3 +0x8C, +0xC1), for the classic cascades; RefiiHostGetClassicTrails
///     also writes the displacement trails, flagged in header [55]; RefiiHostGetEntityInteriorMeshes
///     — a building's interior records, the weather's roofs; RefiiHostGetClassicWeather's header
///     carries the snow's push slots; RefiiHostGetDisplacementQuads — the drunk warp's quad;
///     RefiiHostGetFxMeshes — the effects' mesh particles (hooks rex_lhFx_CreateMeshDescriptor,
///     rex_lhRenderTwin_Destroy); RefiiHostGetEntityDisplacementParts — a composite's
///     displacement-only parts (GDB DisplacementMeshes); RefiiHostGetSpotFrustums — the spots'
///     receive frustums.
/// 180 adds RefiiHostGetDecals — the decals the guest stated (src/hooks/decals.cpp) with their
///     owners' live matrices, for the host's decal draw.
/// 179 adds RefiiHostGetClassicWeather — the light zone, clock and lights the title's rain and
///     snow draws read, for the classic weather.
/// 182 adds RefiiHostTracy* (host_tracy.cpp) — the game's in-process Tracy capture: start, stop,
///     save, status and the dev menu's queries (frames, zones, statistics, plots, messages, GPU,
///     context switches, samples, disassembly).
/// 183 adds the Tracy lock and memory views (RefiiHostTracyGetLocks, GetMemoryPools,
///     GetMemoryLargest), Godot-side memory and locks (RefiiHostTraceAlloc/Free, TraceLock*),
///     find-zone and sample trees (RefiiHostTracyFindZone, GetZoneStatsRange, GetSampleTree),
///     RefiiHostTracyLive, and guest stack sampling (RefiiHostGuestSamplerStart/Stop).
/// 184 adds hard mods' named values: RefiiHostGetModValue (what a mod's hook published) and
///     RefiiHostSetModSetting (what Godot hands it), over src/vfs/src/core/mod_values.cpp.
/// 185 adds RefiiHostGetSpellBlade — the abilities book's own copy of the spell-assignment
///     widget (screen 14 + 200), read exactly as the quick wheel's is, so the host draws ONE
///     owned widget for both trays.
/// 186 adds RefiiHostGetNewGameState, RefiiHostGetSaveRoot and RefiiHostGetGoldenCockInteraction
///     (NG+ and the golden cock).
/// 187 widens RefiiHostGetActiveFx to 22 words: the entity that OWNS each effect node (the
///     emitter component's owner at comp85+4, reached handle+0x28 -> &comp85+12) and the entity
///     it is ATTACHED to (comp85+0x20's box), so the host binds an effect to the cast it belongs
///     to by entity rather than by where it stands.
/// 188 adds the mod event bus for Godot scripts and mod input: RefiiHostDrainModEvents and
///     RefiiHostSetModEventMask (events the payload posts for the kinds the host asked for),
///     RefiiHostPostModInput (a mod's input.json action edge into the guest's InputAction event),
///     RefiiHostPostModCommand (a script's helper call or query, run in the guest tick),
///     RefiiHostDrainModErrors (the mods' Lua errors), RefiiHostModHandleAddress (a handle's entity).
/// 189 adds Godot script verdicts: RefiiHostSetModVerdictHandler (a host function the payload
///     calls on the guest thread inside a seam) and RefiiHostSetModVerdictMask (which kinds).
/// 190 adds RefiiHostGetPlayerCamera — one player's own live camera (their camera manager's
///     +0x90 block), for split screen: local player two has a camera manager of their own there
///     (coop_hero_hooks.cpp); and RefiiHostGetMenuOwner — which player owns the open in-game GUI,
///     the guest's own `sub_8236E400` sampled each guest tick (guest_tick_pump.cpp).
/// 191 adds RefiiHostTakeExitRefused — the players a region exit refused because their
///     partner was out of range since the last call (coop_hero_hooks.cpp, the travel rule).
/// 192 adds RefiiHostGetPlayerTwoPrompts — the things whose use prompt answers player two; and a
///     pooled world icon's entity is now its prompted thing (it was the weak reference's first word);
///     and RefiiHostGetExitWaiting — online, this machine's player waits at an exit for the partner.
/// 193 adds RefiiHostGetExitRequest — the online joiner's newest region-exit use (sequence and
///     guest position), restated to the host, whose exit update fires the exit standing there.
/// 194 adds player state (src/vfs/src/core/player_state.cpp): RefiiHostPlayerStateOutgoing (what
///     this machine owes the co-op peer about the players' mod values), RefiiHostPlayerStateReceive
///     (one message from the peer) and RefiiHostPlayerStateDescribe (both players' values, for the
///     debug channel).
/// 195 adds partner consent (src/vfs/src/core/coop_consent.cpp): RefiiHostConsentTake (a co-op
///     script's question for the partner — sleep or sex on a bed, or a mod's own question with its
///     text) and RefiiHostConsentGive (the answer).
/// 196 adds RefiiHostGetCollectedKeys — the silver keys in the world a player has already taken
///     (core/coop_collectables.cpp): (entity address, mask) pairs, bit 0 player one, bit 1 player two.
/// 197 adds the player's stated status and signals over RexNet presence (Fable 3's orb status
///     and orb emotes): RefiiNetSetPlayerStatus, RefiiNetSendPlayerSignal, and RefiiNetFriends'
///     records gain each friend's status and last signal (10 fields).
/// 198 adds player two's HUD on split screen: RefiiHostGetPlayerTwoTargetedQuery (their own
///     targeted-citizen readout, from the villager's opinion of player two) and
///     RefiiHostGetPlayerTwoHud (their readout's title and d-pad icons, from the guest's own
///     builders called for hero two).
/// 199 adds the mod event masks' second word, kinds 64..127: RefiiHostSetModEventMaskHigh and
///     RefiiHostSetModVerdictMaskHigh (Crime, Achievement, CollectableTaken, PartnerConsent).
/// 200 adds RefiiHostGetDrunkPlayers — which heroes are past the drunk warp's threshold, so each
///     split view draws its own player's warp.
/// 201 adds per-player use prompts on split screen: RefiiHostGetPlayerOneHiddenPrompts (things only
///     player two sees; RefiiHostGetPlayerTwoPrompts is now every thing player two sees, both
///     players' in-range tests run) and RefiiHostGetPlayerTwoHold (player two's own hold meter).
/// 202 adds per-player opinion icons and hold prompts: RefiiHostGetWorldSprites flag 4 (an icon of
///     player two's, its key ending "_P2"), RefiiHostGetPlayerTwoHoldPrompt (player two's own hold
///     prompt), and RefiiHostGetEntityUid / RefiiHostGetOpinionOfUid (a villager's opinion of
///     either player by its network id, for the online joiner's targeted readout).
/// 203 adds RefiiNetPeerName and RefiiNetPartnerName — the name to show for a peer by RexNet's rule
///     (a friend's display name, anyone else's pseudonym); co-op no longer carries names.
/// 205 adds RefiiHostGetProperties (a player's properties with their condition and repair bill) and
///     RefiiHostTakePropertyNotice (disrepair and repair outcomes).
/// 204 adds RefiiHostGetPromptTitles — the use-prompt text the other player would be shown, per
///     thing, for each split half to draw its own player's text — and
///     RefiiHostGetClassicSceneLights (every light of the render scene, for the classic renderer's
///     gather for player two's eye); RefiiHostGetClassicLights carries each light's address at [18].
/// 206 adds Fable 3's player-to-player partnerships: RefiiHostGetPartnership / RefiiHostSetPartnership
///     (marriage and business partnership between the two heroes in the game), RefiiHostGetProperties
///     flag 64 (a building shared with the holder's partner), and property notices 4..6 (owed gold
///     paid, a sale's half to the partner, a purchase shared).
/// 207 adds RefiiHostTakeEstateIntent — an online joiner's real-estate actions for its hero two,
///     sent to the host and run there for the host's hero two (the pump's Estate: action).
/// 208 adds RefiiHostGetItemCount — how many of one item definition a player's hero carries (the
///     player marriage asks the proposer for a ring) — and RefiiHostGetPlayerValue (a player store
///     value, e.g. the heroes' home counts the household tick publishes).
/// 209 adds RefiiHostRequestPropertyEstate / RefiiHostGetPropertyEstate — screen 28's page numbers
///     for the property under the world map's cursor — RefiiHostSetPlayerValue (a saved player
///     value written from the host, e.g. a tip shown once), and each property's purchase price as an
///     eighth RefiiHostGetProperties field.
/// 210 adds RefiiHostUiDataGeneration — bumped when anything an open menu lists changes (an ability
///     level, experience, an item, gold, a drawn weapon, the pump's own item/ability/shop/repair
///     actions); Godot re-reads the open list when it moves.
/// 211 adds RefiiHostGetCoopFigures — both co-op screen figures: player two's henchman and the
///     player-one mannequin the original spawns for the left page (found by its preview ticket) —
///     and retires RefiiHostGetCoopMannequin, which it replaces — and RefiiHostGetCoopScreen: each
///     page's focus and accept, and the save picker's state and card text, for the host-drawn
///     co-op screen.
/// 212 adds RefiiHostPostMouseLook / RefiiHostGetMouseLook — the mouse's camera turn, handed to the
///     active camera's own ProcessGameAction once per camera update, and what the camera took of
///     it, for the host's display-rate prediction.
/// 213 changes RefiiHostGetMouseLook: what each camera update took of the posted turn, the yaw the
///     camera turned for it in that update, and how the host draws a turn not yet taken (orbit
///     about the stated focus, in place, or not at all) — the camera now applies the mouse's turn
///     whole in the update it is handed, so the host draws it exactly instead of predicting it.
/// 214 adds RefiiHostSetMouseLookDevice — whether the mouse is the player's look device: the camera
///     it drives keeps its heading as the hero moves and a target lock takes it as flicks — and
///     RefiiHostGetMouseLook states the flicks handed to a lock.
REFII_HOST_ABI_EXPORT int RefiiHostBridgeAbiVersion(void) { return 214; }

REFII_HOST_ABI_EXPORT uint32_t RefiiHostUiDataGeneration(void) { return refii_bridge_ui_data_generation(); }

/// The taken silver keys still in the world: (address, mask) pairs into `out`, at most `capacity`
/// words. Returns the pairs written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCollectedKeys(uint32_t* out, uint32_t capacity) {
    return out != nullptr ? refii_bridge_collectable_masks(out, capacity) : 0u;
}

/// The next question a co-op script put to the partner: 1 with its ticket, kind (1 sleep, 2 sex,
/// 0 a mod's own question, its words in `text`), asking player and detail (the sleep type); 0 when
/// none is waiting.
REFII_HOST_ABI_EXPORT int32_t RefiiHostConsentTake(uint32_t* ticket, int32_t* kind, int32_t* asker, int32_t* detail,
                                                   char* text, uint32_t capacity) {
    if (ticket == nullptr || kind == nullptr || asker == nullptr || detail == nullptr) {
        return 0;
    }
    return refii_bridge_consent_take(ticket, kind, asker, detail, text, capacity);
}

/// The partner's answer to a question taken with RefiiHostConsentTake.
REFII_HOST_ABI_EXPORT void RefiiHostConsentGive(uint32_t ticket, int32_t yes) { refii_bridge_consent_give(ticket, yes); }

/// The player who owns the open in-game GUI: 2 when player two opened it (the guest routes their
/// pause to the henchman root and only they may close it), else 1. Sampled once per guest tick;
/// meaningful while a menu is open.
REFII_HOST_ABI_EXPORT int RefiiHostGetMenuOwner(void) { return refii_bridge_menu_owner_two() != 0 ? 2 : 1; }

REFII_HOST_ABI_EXPORT int RefiiHostTakeExitRefused(void) { return refii_bridge_take_exit_refused(); }

/// The things whose use prompt answers player two this frame (coop_hero_hooks.cpp, the nearer
/// player rule), for seating their prompt icons in player two's half. Returns the count written.
REFII_HOST_ABI_EXPORT int RefiiHostGetExitWaiting(void) { return refii_bridge_exit_waiting(); }

/// The things whose use prompt only player two sees this frame (player one's half hides them):
/// each prompt's in-range test is run for both players (coop_hero_hooks.cpp). Returns the count.
REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerOneHiddenPrompts(uint32_t* out, int capacity) {
    return refii_bridge_prompts_one_hidden(out, capacity);
}

/// Player two's own hold-to-use meter on split screen: the fraction 0..1, and whether they hold A.
/// The guest keeps one, for player one; player two's is integrated at the guest's own rates and
/// completes through the guest's own use (coop_hero_hooks.cpp).
/// Player two's own hold-to-use prompt in local co-op (the guest keeps one, player one's; player
/// two's is kept beside it, coop_hero_hooks.cpp): 1 with its guest world point (3 floats) and
/// title (UTF-8), 0 when they have none.
REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoHoldPrompt(float* out_at, char* title, uint32_t capacity) {
    return refii_bridge_player_two_hold_prompt_get(out_at, title, capacity);
}

/// Local co-op: the use-prompt text the player the guest's prompt is NOT for would be shown, per
/// thing (coop_hero_hooks.cpp, the dry pass): `thing \t player \t title` lines, UTF-8. Returns
/// the byte count, 0 when none or the buffer is short.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPromptTitles(char* out, uint32_t capacity) {
    return refii_bridge_prompt_titles(out, capacity);
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoHold(float* out_fraction, int* out_pressed) {
    float fraction = 0.0f;
    int32_t pressed = 0;
    refii_bridge_player_two_hold(&fraction, &pressed);
    if (out_fraction != nullptr) *out_fraction = fraction;
    if (out_pressed != nullptr) *out_pressed = pressed;
    return 1;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetExitRequest(float* out_xyz) {
    return refii_bridge_exit_request(out_xyz);
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoPrompts(uint32_t* out, int capacity) {
    return refii_bridge_prompts_two(out, capacity);
}

REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetNewGameState(uint32_t* generation) {
    return refii::vfs::NewGameTransitionResult(generation);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveRoot(char* output, uint32_t capacity) {
    auto* state = rex::system::kernel_state();
    if (!output || capacity == 0 || !state || !state->emulator()) return 0;
    const auto path = state->emulator()->user_data_root().u8string();
    if (path.empty() || path.size() >= capacity) return 0;
    std::memcpy(output, path.data(), path.size());
    output[path.size()] = 0;
    return 1;
}

namespace {

/// The emulator's user_data_root, where the game's saves are, for the player state's save files.
std::string PlayerStateSaveRoot() {
    auto* state = rex::system::kernel_state();
    if (!state || !state->emulator()) {
        return {};
    }
    const auto path = state->emulator()->user_data_root().u8string();
    return std::string(path.begin(), path.end());
}

const bool g_player_state_rooted = [] {
    refii::players::SetSaveRootResolver(&PlayerStateSaveRoot);
    return true;
}();

}  // namespace

/// The players' mod values and the relayed mod events this machine owes the co-op peer, one
/// message per line (see core/player_state.h, core/event_relay.h). Called every frame; the caller
/// sends them only inside a session.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPlayerStateOutgoing(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }
    size_t written = refii::players::TakeOutgoing(out, capacity);
    written += refii::relay::TakeOutgoing(out + written, capacity - written);
    return static_cast<uint32_t>(written);
}

/// One co-op channel message; 1 when it was player state or a relayed event (taken), 0 for
/// anything else.
REFII_HOST_ABI_EXPORT int32_t RefiiHostPlayerStateReceive(const char* message, uint32_t length) {
    if (message == nullptr) {
        return 0;
    }
    const std::string_view text(message, length);
    return refii::relay::Receive(text) || refii::players::Receive(text) ? 1 : 0;
}

/// Both players' mod values and who owns them, as one line. Returns the bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPlayerStateDescribe(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }
    const std::string text = refii::players::Describe();
    const size_t length = std::min<size_t>(text.size(), capacity - 1);
    std::memcpy(out, text.data(), length);
    out[length] = 0;
    return static_cast<uint32_t>(length);
}

REFII_HOST_ABI_EXPORT int32_t RefiiHostGuestBuild(void) { return refii_guest_build(); }

extern "C" void RefiiVfsSetGuestRender(int32_t on);

/// Runs (1) or skips (0) the guest's own scene draw, or returns it to REFII_GUEST_RENDER (-1).
/// A reference instrument for the classic renderer: with the draw on, the title's constant
/// setters fill the device shadow. Additive and optional (the binding tolerates its absence),
/// so the ABI number is unchanged.
REFII_HOST_ABI_EXPORT void RefiiHostSetGuestRender(int32_t on) { RefiiVfsSetGuestRender(on); }

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddress(uint32_t tu1_address) {
    return refii_guest_address(tu1_address);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddressToTu1(uint32_t live_address) {
    return refii_guest_address_to_tu1(live_address);
}

/// THE SHAPE, PER PART, as the guest states it to `sub_82A48B90`: per morphed part [model hash,
/// count, then count x (target model hash, weight float bits)]; the part itself takes 1 - sum.
/// A part that is not listed draws as its own model. `out_statements` low word counts every
/// part submission seen (0 means the hook never ran), high word those that listed a target.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartMorphs(uint32_t entity, uint32_t* out_words,
                                                            uint32_t word_capacity,
                                                            uint32_t* out_generation,
                                                            uint64_t* out_statements) {
    return refii::vfs::StatedPartMorphs(entity, out_words, word_capacity, out_generation,
                                        out_statements);
}

/// WHICH GARMENTS ARE DYED, by the guest's own test `sub_826CAFA8`, sampled on the GUI tick for
/// what the hero wears and carries in categories 9..11 and 17..24. Two words per item: the
/// item's record hash, then flags — bit 0 slot 1 dyed, bit 1 slot 2 dyed, bit 2 worn. A worn
/// item is listed ahead of a carried one of the same record. Returns the item count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDyeStates(uint32_t* out_words, uint32_t word_capacity) {
    return refii::vfs::SampledDyeStates(out_words, word_capacity);
}

/// THE SKIN AND THE CLOTH. Per original texture the guest composites: [original texture id,
/// layer count | kind << 16, then count x 7 words {texture id, blend mode, weight bits,
/// has-tint, tint bytes B G R A, brightness bits, bump texture id}]. Kind 0 is a composite,
/// layers in draw order with layer 0 the base; kind 1 is the overlay list (scars, mode-0
/// morphs) the guest draws over the finished material. Covers replacement
/// and skin-set textures, stat morphs, tattoos and dyes. Returns the words written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityTextureLayers(uint32_t entity, uint32_t* out_words,
                                                               uint32_t word_capacity,
                                                               uint32_t* out_generation,
                                                               uint64_t* out_statements) {
    return refii::vfs::StatedTextureLayers(entity, out_words, word_capacity, out_generation,
                                           out_statements);
}

/// One inventory item's dye composites, as the guest's own dye builder states them for the
/// item's dye component — [original, count | kind << 16, count x 7 layer words] per texture,
/// the words RefiiHostGetEntityTextureLayers uses. Empty for an item that is not dyed. Returns
/// the words written; `out_generation` moves when the layers changed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetItemDyeLayers(uint32_t record, uint32_t* out_words,
                                                        uint32_t word_capacity,
                                                        uint32_t* out_generation) {
    return refii::vfs::StatedItemDyeLayers(record, out_words, word_capacity, out_generation);
}

/// Answers one `GUI` predicate from the host, by name.
///
/// `kind` is 0 to clear (the guest's implementation runs again), 1 boolean, 2 float. `bits` is
/// 0/1 for a boolean and the float's raw bits for a float.
REFII_HOST_ABI_EXPORT void RefiiHostSetGuiPredicate(const char* name, uint32_t kind,
                                                    uint32_t bits) {
    if (name == nullptr) {
        return;
    }

    refii::vfs::SetGuiNativeAnswer(name, static_cast<refii::vfs::GuiNativeAnswerKind>(kind), bits);
}

/// The pause-root minimap's quest markers — the std::map at `screen0+88`, IDA-settled.
///
/// Layout (MSVC map node, 96 bytes; `_Color/_Isnil` sit AFTER the value): +0 left, +4 parent,
/// +8 right, +12 4B pad, +16 KEY = texture object pointer, +32 float4 page-local quad centre,
/// +48 float2[4] corner UVs, +80 colour byte, +81 nil byte. Head pointer at `screen0+92`
/// (head[81]==1), size at `screen0+96`, built flag at `screen0+100`. Populated once per map
/// opening (`sub_826A3150`), emptied on deactivate — the only tear window — so sample only
/// while screen 0 is up.
///
/// At most THREE live: Quest OR QuestExit, plus one QuestGiver (unique-keyed by texture, so
/// all givers collapse to the last — shipped behaviour). Hero and dog are ordinary elements
/// and never appear here.
///
/// Type travels as the corner UVs. `out_hashes[i]` is the raw map KEY (a texture object
/// pointer, or -1 on a failed lookup), an opaque identity that is never dereferenced; the C#
/// side matches the corner UVs against the shipped `ReusableTokens` quads to pick the art.
///
/// `out_values` takes 10 floats per marker: x, y (page-local centre), then the four (u,v)
/// corners. Returns how many markers were written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMapMarkers(uint32_t* out_hashes,
                                                      float* out_values,
                                                      uint32_t cap) {
    if (out_hashes == nullptr || out_values == nullptr || cap == 0) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) {
        return 0;
    }

    const uint32_t owner = read32(gui + 28u);
    if (!plausible(owner)) {
        return 0;
    }

    // Screen 0's handler out of the id -> screen map at owner+68.
    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) {
        return 0;
    }

    uint32_t node = read32(end_node + 4u);
    uint32_t screen0 = 0;
    for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
        const uint32_t key = read32(node + 12u);
        if (key == 0u) {
            screen0 = read32(node + 16u);
            break;
        }

        node = read32(node + (0u < key ? 0u : 8u));
    }

    if (!plausible(screen0) || GuestByteAt(base, screen0 + 100u) != 1u) {
        return 0;
    }

    const uint32_t head = read32(screen0 + 92u);
    const uint32_t size = read32(screen0 + 96u);
    if (!plausible(head) || size == 0u || size > 8u) {
        return 0;
    }

    uint32_t written = 0;
    uint32_t at = read32(head + 0u);  // begin = leftmost
    for (uint32_t step = 0; step < size + 8u && plausible(at) && at != head && written < cap;
         ++step) {
        // The key is returned verbatim, never dereferenced: it is a texture OBJECT pointer (or
        // -1 on a failed lookup) and this walk runs on the host thread with unguarded reads.
        // The C# side identifies the marker type by its corner UVs instead.
        out_hashes[written] = read32(at + 16u);
        float* record = out_values + (written * 10u);
        record[0] = read_float(at + 32u);
        record[1] = read_float(at + 36u);
        for (uint32_t corner = 0; corner < 8u; ++corner) {
            record[2u + corner] = read_float(at + 48u + (corner * 4u));
        }

        ++written;

        // In-order successor.
        uint32_t right = read32(at + 8u);
        if (plausible(right) && GuestByteAt(base, right + 81u) == 0u) {
            at = right;
            for (uint32_t descend = 0; descend < 32u; ++descend) {
                const uint32_t left = read32(at + 0u);
                if (!plausible(left) || GuestByteAt(base, left + 81u) != 0u) {
                    break;
                }

                at = left;
            }
        } else {
            uint32_t parent = read32(at + 4u);
            uint32_t climb = 0;
            while (climb < 32u && plausible(parent) &&
                   GuestByteAt(base, parent + 81u) == 0u && at == read32(parent + 8u)) {
                at = parent;
                parent = read32(at + 4u);
                ++climb;
            }

            at = parent;
        }
    }

    return written;
}

/// The abilities screen's spell-assignment mode — the guest's own byte at `screen14+620`.
///
/// The only three writers are the ctor (0), `sub_826C2480` (1, via Lua UI event 23 off an
/// A-press on a spell row) and `sub_826C25C0` (0, on code 37 or deactivate) — the byte IS the
/// mode. Not inferrable from element state: the blade widget's `Adjuster` components are
/// authored-active in `abilitiesscreen.bgf`, so they read as open the moment the screen is.
///
/// Returns 1 in assignment mode, 0 on the normal list, -1 when the screen object does not
/// exist (never pushed yet, or the map walk failed).
REFII_HOST_ABI_EXPORT int RefiiHostGetAbilitiesMode(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return -1;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return -1;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) {
        return -1;
    }

    const uint32_t owner = read32(gui + 28u);
    if (!plausible(owner)) {
        return -1;
    }

    // The id -> screen-handler map at owner+68 — same read-only, step-bounded walk the
    // preview-object export uses (node +0 left, +8 right, +12 key, +16 value; end at +72).
    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) {
        return -1;
    }

    constexpr uint32_t kAbilitiesScreenId = 14u;
    uint32_t node = read32(end_node + 4u);
    for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
        const uint32_t key = refii::guest::ScreenIdToTu1(read32(node + 12u));
        if (key == kAbilitiesScreenId) {
            const uint32_t handler = read32(node + 16u);
            if (!plausible(handler)) {
                return -1;
            }

            return GuestByteAt(base, handler + 620u) != 0 ? 1 : 0;
        }

        node = read32(node + (kAbilitiesScreenId < key ? 0u : 8u));
    }

    return -1;
}

/// The screen fade, as a colour the host can draw. Read-only: the guest starts the fade
/// (`FadeScreenOut`/`In`/`OutToColour`) and advances it at 15 Hz in `sub_822C1FC0`

/// (`t += (1/15)/duration`, clamped; `dbl_83319660` is 15.0), and `CmdLevelChange_Update` /
/// `CmdLoadSaveGame_Update` both block on it, so writing `t` here would hang a level change.
///
/// The object is `*( *(0x83496B08) + 0x40 )` — TWO dereferences; one reads a different global:
///
///     82305ac4  lwz  r11, dword_83496B08@l(r11)   ; *(0x83496B08)
///     82305ac8  lwz  r3,  0x40(r11)               ; + 0x40
///     82305acc  stb  r10, 0x2C(r3)
///
/// It is not a pure fade manager — `+0x10`/`+0x14` are death-screen state and `+0x18..0x20`
/// the headline text. The colour is a lerp of two byte quads stored `[b, g, r, a]`, not RGBA:
/// `sub_822C7970` computes `out[i] = src[8+i] + t*(dst[12+i] - src[8+i])` truncated to bytes,
/// and the draw reassembles it as `(c[2], c[1], c[0], c[3]) / 255`; the truncation is
/// reproduced. `FadeScreenIn` targets `byte_83497BF4` = `00 00 00 00`, so "faded in" is
/// alpha zero.
///
/// <paramref name="out_rgba"/> takes four floats 0..1 in **RGBA** order (already unswizzled).
/// <paramref name="out_progress"/> takes `t`, <paramref name="out_locked"/> the lock at `+0x21`
/// that `LockScreenFade` writes and that `CmdLevelChange_Update` uses as its escape hatch.
///
/// Returns the fade object's address, or 0 before the GUI object exists.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetScreenFade(float* out_rgba, float* out_progress,
                                                     uint32_t* out_locked) {
    if (out_rgba != nullptr) {
        out_rgba[0] = 0.0f;
        out_rgba[1] = 0.0f;
        out_rgba[2] = 0.0f;
        out_rgba[3] = 0.0f;
    }
    if (out_progress != nullptr) {
        *out_progress = 0.0f;
    }
    if (out_locked != nullptr) {
        *out_locked = 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t owner = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    if (!plausible(owner)) {
        return 0;
    }

    const uint32_t fade = GuestWordAt(base, owner + 0x40u);
    if (!plausible(fade)) {
        return 0;
    }

    const uint32_t bits = GuestWordAt(base, fade);
    float t = 0.0f;
    std::memcpy(&t, &bits, sizeof(t));

    // Clamped: this reads a word another thread is writing, and a NaN would reach the colour.
    if (!(t > 0.0f)) {
        t = 0.0f;  // also catches NaN
    } else if (t > 1.0f) {
        t = 1.0f;
    }

    uint8_t lerped[4] = {0, 0, 0, 0};
    for (uint32_t i = 0; i < 4u; ++i) {
        const float from = static_cast<float>(GuestByteAt(base, fade + 8u + i));
        const float to = static_cast<float>(GuestByteAt(base, fade + 12u + i));
        lerped[i] = static_cast<uint8_t>(from + t * (to - from));
    }

    if (out_rgba != nullptr) {
        // [b, g, r, a] -> RGBA. See the note above.
        out_rgba[0] = static_cast<float>(lerped[2]) * (1.0f / 255.0f);
        out_rgba[1] = static_cast<float>(lerped[1]) * (1.0f / 255.0f);
        out_rgba[2] = static_cast<float>(lerped[0]) * (1.0f / 255.0f);
        out_rgba[3] = static_cast<float>(lerped[3]) * (1.0f / 255.0f);
    }

    if (out_progress != nullptr) {
        *out_progress = t;
    }

    if (out_locked != nullptr) {
        *out_locked = GuestByteAt(base, fade + 0x21u);
    }

    return fade;
}

// The hero and its components — shared by every proxy below. `extern "C++"` because
// this file's exports live inside one big `extern "C"` block.

extern "C++" {
namespace {
uint32_t PlayerEntityForKey(const uint8_t* base, uint32_t wanted);

/// The player's hero ENTITY, or 0 when there is not one yet. One walk, shared by every proxy
/// below; from `sub_826B83D0` / `sub_82264230`:
///
///   game = *(*(0x83496AB8) + 12); sess = *(game + 0x8C)
///   key  = (bytes sess+0x34 && sess+0x35) ? 1 : 2          -- the co-op slot choice
///   hero = PlayerEntityForKey(key)                          -- EM slot first, player map second
///
/// ONE RESOLVER: the key is chosen here, the entity for it comes from `PlayerEntityForKey`, so
/// the hero, player one and player two can never be different addresses for the same key.
/// Every failure is 0, meaning "no hero", not "error": before a save is loaded there is none.
uint32_t ResolvePlayerHero(const uint8_t* base) {
    // Change-keyed: the step that refused, so a blank readout names its cause once.
    static std::atomic<int> last_step{-1};
    const auto refuse = [&](int step, uint32_t detail) {
        if (last_step.exchange(step) != step) {
            REXLOG_INFO("Refii hero resolver: no hero — refused at step {} (detail {:08X})", step, detail);
        }
        return 0u;
    };

    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) return refuse(1, game_root);
    const uint32_t game = read32(game_root + 12u);
    if (!plausible(game)) return refuse(2, game);

    // THE MANAGER ITSELF, not the game object's counted copy at +0x8C: `g_XrnmNetContext`
    // (0x83497490). A title-joined client had the copy empty in game, so this read 0 there and
    // the joiner's whole hero pipeline — vitals, inventory, the weapons fold — went blank.
    uint32_t session = read32(refii::guest::Address(0x83497490u));
    if (!plausible(session)) session = read32(game + 0x8Cu);
    if (!plausible(session)) return refuse(3, session);
    // Local co-op with player two's pause open: the menus are player two's (the payload's
    // MenusActForPlayerTwo, the same owner sample).
    const bool menus_for_two = refii_bridge_menu_owner_two() != 0 && read32(game + 0x11Cu) == 2u;
    const uint32_t player_key =
        menus_for_two || !(read8(session + 0x34u) != 0u && read8(session + 0x35u) != 0u) ? 2u : 1u;

    // In a session at all: +0x34 is the "in a session" byte, +0x36 the joiner bit. Solo reads
    // as host (+0x34 and +0x35 set, +0x36 clear), so this is false for a single-player run.
    const bool in_session = read8(session + 0x34u) != 0u && read8(session + 0x36u) != 0u;

    // PREFER THE ROLE'S SLOT, FALL BACK TO THE OTHER OUTSIDE A SESSION. Key 2 is right for a
    // joiner IN a session, but the same instance before it joins has only its own solo hero,
    // and a fixed key 2 read nothing there — so the joiner's own hero was never captured for
    // the kit. Solo there is one hero, so the other slot is this player's; in a session it
    // would be the other player's hero, so the lookup refuses.
    uint32_t hero = PlayerEntityForKey(base, player_key);
    if (hero == 0u && !in_session) {
        hero = PlayerEntityForKey(base, player_key == 1u ? 2u : 1u);
    }
    if (hero == 0u) return refuse(4, player_key);
    if (last_step.exchange(0) != 0) {
        REXLOG_INFO("Refii hero resolver: player key {} → entity {:08X}", player_key, hero);
    }
    return hero;
}

/// The player map's record for key 1 or 2 — the sorted stride-12 map `{s32 key, u32 record,
/// u32}` at `game+0x9C` (+8 begin, +12 end), as `sub_82264230`'s lower_bound reads it — or 0.
/// The record is the Player object: `+16` its entity handle's control block, `+0x28` its pad.
uint32_t PlayerMapRecordForKey(const uint8_t* base, uint32_t wanted) {
    if (base == nullptr) return 0u;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) return 0u;
    const uint32_t game = read32(game_root + 12u);
    if (!plausible(game)) return 0u;
    const uint32_t player_map = read32(game + 0x9Cu);
    if (!plausible(player_map)) return 0u;
    uint32_t entry = read32(player_map + 8u);
    const uint32_t entries_end = read32(player_map + 12u);
    if (!plausible(entry) || entries_end < entry || (entries_end - entry) > 12u * 64u) return 0u;
    for (; entry < entries_end; entry += 12u) {
        if (read32(entry) != wanted) continue;
        const uint32_t record = read32(entry + 4u);
        return plausible(record) ? record : 0u;
    }
    return 0u;
}

/// The entity of player 1 or 2, or 0.
///
/// THE GUEST'S OWN ACCESSORS FIRST. The entity manager keeps the two player entities in its own
/// slots — `EM+104` (object at `+108`) is player one, `EM+112` (object at `+116`) is player two:
/// `sub_8222C1B8` / `sub_828E5A78`, filled by `sub_82363BA0` when the entity with UniqueID 1 or
/// 2 registers. The player map at `game+0x9C` is tried second: a CLIENT's map has no key 1 (the
/// host's hero is a replica there) and a HOST's map was seen holding only key 1 with player two
/// up (2026-10-07, live), so a walk that trusted it alone reported the partner as 0 — Godot
/// then drew, posed and ranged that body like a villager, and the held-weapon export found no
/// body to read.
uint32_t PlayerEntityForKey(const uint8_t* base, uint32_t wanted) {
    if (base == nullptr) return 0u;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) return 0u;
    const uint32_t game = read32(game_root + 12u);
    if (!plausible(game)) return 0u;

    if (wanted == 1u || wanted == 2u) {
        // `**(game+88)+4`: the entity manager, as `sub_8246C970` reads it at 0x8246C9A8.
        const uint32_t holder = read32(game + 88u);
        const uint32_t slot = plausible(holder) ? read32(holder + 4u) : 0u;
        const uint32_t manager = plausible(slot) ? read32(slot) : 0u;
        const uint32_t object = !plausible(manager) ? 0u
                                : wanted == 1u ? refii_bridge_hero_one_holder(base, manager)
                                               : read32(manager + 116u);
        const uint32_t entity = plausible(object) ? read32(object) : 0u;
        if (plausible(entity)) return entity;
    }

    const uint32_t record = PlayerMapRecordForKey(base, wanted);
    if (record == 0u) return 0u;
    const uint32_t control = read32(record + 16u);
    if (!plausible(control)) return 0u;
    const uint32_t hero = read32(control);
    return plausible(hero) ? hero : 0u;
}

/// One component off an entity by id, or 0 when the entity does not carry it.
///
/// Check the presence bitmask FIRST — eight dwords at `entity+0x24`. The engine's own slow
/// path returns garbage on a miss rather than null, so skipping it succeeds with rubbish.
///
/// Then the accelerator: a byte table at `entity+0x8C` indexed by component id, giving the slot
/// in the sorted stride-8 `{s32 id, u32 ptr}` vector at `entity+0x48`..`+0x4C`. When there is no
/// accelerator the vector is walked. The id is re-checked after the accelerator hop, which the
/// guest does not do: another thread may be rebuilding the table and a stale slot index would
/// hand back a different component.
uint32_t EntityComponent(const uint8_t* base, uint32_t entity, uint32_t id) {
    if (base == nullptr || id >= 256u) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    if (!plausible(entity)) {
        return 0u;
    }

    const uint32_t present = read32(entity + 0x24u + (id / 32u) * 4u);
    if (((present >> (id % 32u)) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t pairs = read32(entity + 0x48u);
    const uint32_t pairs_end = read32(entity + 0x4Cu);
    if (!plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
        return 0u;
    }

    const uint32_t accelerator = read32(entity + 0x8Cu);
    if (plausible(accelerator)) {
        const uint32_t slot = read8(accelerator + id);
        const uint32_t at = pairs + 8u * slot;
        if (at + 8u <= pairs_end && read32(at) == id) {
            const uint32_t component = read32(at + 4u);
            return plausible(component) ? component : 0u;
        }
    }

    for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
        if (read32(at) == id) {
            const uint32_t component = read32(at + 4u);
            return plausible(component) ? component : 0u;
        }
    }

    return 0u;
}

/// The screen object the manager holds for `screen_id`, or 0 when that screen is not live.
///
/// The map is `std::map<int, CScreen*>` at `owner+68`: `owner+72` is the header node, whose
/// `+4` is the root, and each node is {left +0, parent +4, right +8, key +12, value +16}.
/// The step is the map's own comparison: descend left while the wanted id is below the node's
/// key, right otherwise; running back into the header is "not present".
///
/// Every failure is 0 — a screen that is not open has no object.
static uint32_t ScreenObject(const uint8_t* base, uint32_t screen_id) {
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) {
        return 0u;
    }

    const uint32_t owner = read32(gui + 28u);
    if (!plausible(owner)) {
        return 0u;
    }

    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) {
        return 0u;
    }

    uint32_t node = read32(end_node + 4u);
    for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
        const uint32_t key = refii::guest::ScreenIdToTu1(read32(node + 12u));
        if (key == screen_id) {
            const uint32_t screen = read32(node + 16u);
            return plausible(screen) ? screen : 0u;
        }

        node = read32(node + (screen_id < key ? 0u : 8u));
    }

    return 0u;
}
}  // namespace
}  // extern "C++"

/// The hero's VITALS, as pure reads: the component data the C++ screen managers spawn their
/// rows from. Every field is a single aligned load in the guest, which is what makes it safe
/// to read from Godot's thread. From the accessors' own bytes:
///
///     GetHealth      0x829495D0  C0 23 00 14  lfs f1, 0x14(r3) ; blr
///     GetBaseHealth  0x828BF918  C0 23 00 18  lfs f1, 0x18(r3) ; blr
///     GetMaxHealth   0x82314BD0  C0 23 00 1C  lfs f1, 0x1C(r3) ; blr
///     GetMoney       0x82EB55A8  80 63 00 0C  lwz r3,  0x0C(r3) ; blr
///     Invulnerable   0x824CA750  88 63 01 31  lbz r3, 0x131(r3) ; blr
///     CantDie        0x824CA758  88 63 01 33  lbz r3, 0x133(r3) ; blr
///
/// `CECHealth+0x10` is NOT health: it is an incoming-damage multiplier initialised to 1.0.
/// Health starts at `+0x14`.
///
/// Three values are not answered here — they can only be CALLED:
///
///   * Displayed Morality, Purity and Renown: the stored fields are BASE, and the accessors add
///     real-estate and worship bonuses from the maps at `stats+0x84`/`+0x9C` (bonus types 11,
///     12 and 10) plus an Evil/Holy term from a global config string.
///   * Ability levels for ids 2, 3 and 6, which take buffed arms and clamp against a game-defs
///     ceiling. The raw slots are reported and flagged; only the displayed NUMBER is wrong.
///
/// Those accessors' map walks carry MSVC iterator-debug checks that TRAP (`twui r0, 0x16`) if
/// another thread mutates the map, so anything derived stays a thread-constrained call.
///
/// Layout. <paramref name="words"/> takes 17:
///
///     [0]      gold                    CECMoney+0x0C, i32
///     [1]      renown, BASE            CECStats+0x1C, i32
///     [2]      flags: bit0 invulnerable, bit1 cantDie
///     [3..16]  ability levels for ids 1..14, RAW (`stats+0x110 + 4*id`)
///
/// <paramref name="values"/> takes 20:
///
///     [0..2]   health, baseHealth, maxHealth
///     [3..4]   morality, purity — BASE
///     [5..8]   moralityMin, moralityMax, purityMin, purityMax
///     [9..11]  strength, skill, will — BODY MORPH percentages in [0,1], NOT experience
///     [12..15] experience AVAILABLE  per pool: General, Strength, Skill, Will
///     [16..19] experience TOTAL EARNED per pool, same order
///
/// Spent is `total - available`; the guest keeps no spent counter. Returns the hero entity, or
/// 0 when there is not one.
/// The hero's DERIVED state — what the guest's own accessors answer, not what its fields hold.
/// `sub_8223D320` adds the GDB script constants `EvilMoralityAddition`/`HolyMoralityAddition`
/// to the raw field plus real-estate and worship terms; `sub_821C7D30` adds modifier 10 and
/// clamps. The logbook's personality bands test at 0.375 and 0.625 of the normalised range.
/// The pump samples the accessors on the guest tick thread; this only reads the shared store.
///
/// Fills 5 words and 26 floats:
///   words  0 known · 1 renown · 2 dog present · 3 dog state index · 4 (unused) ·
///          5 dog combat level · 6 dog searching level · 7 learned-trick count ·
///          8 stat counters valid
///   floats 0..5 morality/min/max, purity/min/max · 6..16 the eleven attractiveness terms
///          17 attractiveness slider fraction 0..1 · 20..25 spell damage multiplier by LEVEL
///
/// `stats` takes the 124 logbook counters and `trick_ids` the learned tricks' GDB RECORD IDS
/// (not name hashes — the guest stores what its hash-to-id lookup returned).
///
/// Returns 1 once the pump has sampled a hero at least once.
/// The hero's COMPLETED QUESTS — one entry per row of the logbook's quests page.
///
/// Filtered on the guest thread (three byte tests on the quest object) and left UNSORTED. The
/// page's order is descending by the GDB property `DisplayPriority`; the guest sorts before
/// filtering, but filtering preserves relative order, so sorting afterwards is equivalent.
/// Returns the TOTAL count regardless of `capacity`.
/// The CRIME screen's arrest notice — screen 21's own fields, read where they live.
///
/// No sampling needed: the opener `sub_823C4128` composes the two notices into LHStrings on
/// the screen object and writes the fine beside them, so every value is a plain load off the
/// id->handler map walk at `*(GUI+28) + 68`. The strings are already Babel-resolved text.
///
///   `screen+32` the charge notice · `screen+36` the community-service detail ·
///   `screen+40` the fine · `screen+44` PayFine offered · `screen+45` CommunityService offered
///
/// Returns 1 when the crime screen exists.
/// The world map's region info page, for the region the host last asked for.
///
/// Two calls, one round trip: `RefiiHostSetWorldMapRegion` names the region and this reads
/// back what the pump sampled, one tick later — sampling calls eleven guest providers and must
/// happen on the guest thread. Every field is a finished string, because every provider
/// produces one (distance and travel time worded, counts paired, merchants comma-joined).
///
/// Returns the REGION COUNT, so a caller can bound its cursor; 0 means nothing sampled yet.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldMapRegion(uint32_t* out_index,
                                                          char* name, uint32_t name_capacity,
                                                          char* distance, uint32_t distance_capacity,
                                                          char* travel, uint32_t travel_capacity,
                                                          char* counts, uint32_t counts_capacity,
                                                          char* merchants,
                                                          uint32_t merchants_capacity,
                                                          int32_t* out_stars,
                                                          char* level_name,
                                                          uint32_t level_capacity,
                                                          uint32_t* out_level_key,
                                                          char* scenario,
                                                          uint32_t scenario_capacity) {
    const refii::vfs::WorldMapRegion region = refii::vfs::WorldMapRegionSnapshot();

    // The minimap page's keys — see the sampler. Null-safe so an older caller shape still works.
    if (level_name != nullptr && level_capacity != 0u) {
        WriteOut(region.level_name, level_name, level_capacity);
    }
    if (out_level_key != nullptr) {
        *out_level_key = region.level_key;
    }
    if (scenario != nullptr && scenario_capacity != 0u) {
        WriteOut(region.scenario, scenario, scenario_capacity);
    }

    // Which region this is; a caller that asked for another one is holding a stale answer.
    if (out_index != nullptr) *out_index = region.index;

    // The economy is a star count, not a string, so it rides beside the worded fields.
    if (out_stars != nullptr) *out_stars = region.stars;

    WriteOut(region.name, name, name_capacity);
    WriteOut(region.distance, distance, distance_capacity);
    WriteOut(region.travel, travel, travel_capacity);
    WriteOut(region.merchants, merchants, merchants_capacity);

    // The seven collectable counts as one tab-separated line, in the page's own order.
    std::string packed;
    packed.append(region.fine).append("\t");
    packed.append(region.owned).append("\t");
    packed.append(region.chests).append("\t");
    packed.append(region.digspots).append("\t");
    packed.append(region.divespots).append("\t");
    packed.append(region.gargoyles).append("\t");
    packed.append(region.silver_keys);
    WriteOut(packed, counts, counts_capacity);

    return region.count;
}

/// SCREEN 28'S ONE BUILDING — the real estate page, as data.
///
/// One record rather than a list: the screen's left column is a list of COMMANDS and there is a
/// single building, resolved when it opens. The pump samples it on the GUI tick while the screen
/// stands (`SampleRealEstate`); this only reads the shared store.
///
/// The numbers arrive already answered by the guest — value, economy, income, sale price and the
/// three not-owned price deltas each average or walk something the host has no business
/// reproducing. What the host does with them is entirely its own: the star bin, the
/// `&lt;gold_img&gt;` prefix, the negative recolour, which of the four Variable rows stands, and the
/// HOUSE-versus-SHOP legend are all decided from these fields.
///
/// `out_words` takes fifteen: record hash, type mask, owned, hasTenant, value, economy,
/// decoration, income, salePrice, sellingFees, notVacant, ownerKilled, heroOpinion, the
/// BONUSES record — a different record, off component 184 — and the hero's TITLE record, which
/// names the owner of a hero-owned building. Returns 1 when a building resolved.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetRealEstate(uint32_t* out_words, uint32_t word_capacity,
                                                     char* occupant, uint32_t occupant_capacity) {
    const refii::vfs::RealEstate estate = refii::vfs::RealEstateSnapshot();

    WriteOut(estate.occupant, occupant, occupant_capacity);

    const uint32_t words[] = {
        estate.record,
        estate.type_mask,
        estate.owned ? 1u : 0u,
        estate.has_tenant ? 1u : 0u,
        static_cast<uint32_t>(estate.value),
        static_cast<uint32_t>(estate.economy),
        static_cast<uint32_t>(estate.decoration),
        static_cast<uint32_t>(estate.income),
        static_cast<uint32_t>(estate.sale_price),
        static_cast<uint32_t>(estate.selling_fees),
        static_cast<uint32_t>(estate.not_vacant),
        static_cast<uint32_t>(estate.owner_killed),
        static_cast<uint32_t>(estate.hero_opinion),
        estate.bonuses_record,
        estate.title_record,
    };

    if (out_words != nullptr) {
        const uint32_t count =
            word_capacity < (sizeof(words) / sizeof(words[0])) ? word_capacity
                                                               : sizeof(words) / sizeof(words[0]);
        for (uint32_t i = 0; i < count; ++i) {
            out_words[i] = words[i];
        }
    }

    return estate.known ? 1u : 0u;
}

/// The OPTIONS screen's settings — pages 1 and 2, all of it plain loads.
///
/// Nothing here needs the guest thread: page 1's three accessors are each a field read behind
/// a component walk.
///
/// Bases differ per row. Four values live on the local player's component 33; SUBTITLES lives
/// on the GUI manager — `*(*(*(0x83496B08+32)+68)+4)` — a different root object entirely.
///
/// Breadcrumb size is a FLOAT, not an index. `sub_829BE5F8` walks component 33's owner to
/// component 162 and answers `byte[+331] ? flt_83319198 : 0.0f`; the row then picks the last of
/// {0.0, 0.5, 1.0, 1.5, 2.0} that is <= the value. `flt_83319198` is written by the commit
/// path, so it is read, never assumed.
///
/// Fills 6 words: 0 known · 1 subtitles · 2 tutorial boxes · 3 multiplayer orbs (0..2) ·
/// 4 auto-joinable · 5 camera look inverted; and one float: the breadcrumb size.
REFII_HOST_ABI_EXPORT int RefiiHostGetOptions(uint32_t* words, uint32_t word_capacity,
                                              float* out_breadcrumb) {
    if (out_breadcrumb != nullptr) *out_breadcrumb = 0.0f;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto word = [words, word_capacity](uint32_t slot, uint32_t value) {
        if (words != nullptr && slot < word_capacity) words[slot] = value;
    };

    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t settings = hero != 0u ? EntityComponent(base, hero, 33u) : 0u;
    if (settings == 0u) {
        word(0, 0u);
        return 0;
    }

    word(0, 1u);

    // SUBTITLES — the GUI manager's own byte, not component 33's.
    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    const uint32_t owner = plausible(gui) ? read32(gui + 32u) : 0u;
    const uint32_t block = plausible(owner) ? read32(owner + 68u) : 0u;
    word(1, plausible(block) ? GuestByteAt(base, block + 4u) : 0u);

    word(2, GuestByteAt(base, settings + 40u));                        // TutorialTextEnabled
    word(3, read32(settings + 72u));                                   // LivePresenceMode 0..2
    word(4, read32(settings + 76u) != 0u ? 1u : 0u);                   // MultiplayerJoinable
    word(5, GuestByteAt(base, settings + 42u));                        // CameraLookInverted

    // BREADCRUMB — component 162 on the object component 33 points at.
    const uint32_t linked = read32(settings + 320u);
    const uint32_t object = plausible(linked) ? read32(linked) : 0u;
    if (plausible(object) && (GuestByteAt(base, object + 144u) & 0x40u) != 0u) {
        const uint32_t trail = EntityComponent(base, object, 162u);
        if (trail != 0u && GuestByteAt(base, trail + 331u) != 0u && out_breadcrumb != nullptr) {
            const uint32_t raw = read32(refii::guest::Address(0x83319198u));
            float value = 0.0f;
            std::memcpy(&value, &raw, sizeof(value));
            *out_breadcrumb = value;
        }
    }

    return 1;
}

/// The options screen's SLIDER pages — gamma, the three volumes and the speaker mode.
///
/// Screen-scoped: these live on the two page controllers the options screen constructs
/// (`sub_826D9AC0` puts page 3 at `screen+312` and page 4 at `screen+408`), so they exist only
/// while the screen is open. The profile blob is no alternative — every reader allocates 40
/// bytes, reads, and frees before returning.
///
/// Values are 0..1, the slider's own clamp (`sub_82881E38`). The pip sits at
/// `position.x = value * 440 - 210`, which is the caller's arithmetic.
REFII_HOST_ABI_EXPORT int RefiiHostGetOptionsSliders(float* out_gamma, float* out_sfx,
                                                     float* out_music, float* out_speech,
                                                     uint32_t* out_speaker) {
    if (out_gamma != nullptr) *out_gamma = 0.0f;
    if (out_sfx != nullptr) *out_sfx = 0.0f;
    if (out_music != nullptr) *out_music = 0.0f;
    if (out_speech != nullptr) *out_speech = 0.0f;
    if (out_speaker != nullptr) *out_speaker = 0u;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    constexpr uint32_t kOptionsScreenId = 18u;
    const uint32_t screen = ScreenObject(base, kOptionsScreenId);
    if (screen == 0u) return 0;

    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };

    if (out_gamma != nullptr) *out_gamma = read_float(screen + 312u + 0x30u);
    if (out_sfx != nullptr) *out_sfx = read_float(screen + 408u + 0x2Cu);
    if (out_music != nullptr) *out_music = read_float(screen + 408u + 0x30u);
    if (out_speech != nullptr) *out_speech = read_float(screen + 408u + 0x34u);
    if (out_speaker != nullptr) *out_speaker = GuestWordAt(base, screen + 408u + 0x38u);
    return 1;
}

/// Installs Godot's asset reader, so the guest's loaders resolve through it and one mount
/// serves both sides.
///
/// `reader` is a C function pointer into managed code, called ON THE GUEST'S THREAD. It is
/// asked with a null buffer for the size, then again to fill one; it must be reentrant and must
/// not throw across the boundary. Passing nullptr unregisters, which Godot does on the way out
/// so a late guest read gets "no reader" rather than a torn-down runtime.
REFII_HOST_ABI_EXPORT void RefiiHostSetAssetReader(refii::vfs::HostAssetReaderFn reader) {
    refii::vfs::SetHostAssetReader(reader);
}

/// Installs Godot's asset override query — answers whether an asset is a virtual mod override.
/// When true, native VFS prioritizes Godot's virtual asset over loose files on disk.
REFII_HOST_ABI_EXPORT void RefiiHostSetAssetOverrideQuery(
        refii::vfs::HostAssetIsOverrideFn query) {
    refii::vfs::SetHostAssetOverrideQuery(query);
}

/// Installs Godot's HASH index — same direction and threading as RefiiHostSetAssetReader, but
/// installed independently. `resolver` writes the asset's path and returns its length, 0 for
/// "no such hash".
REFII_HOST_ABI_EXPORT void RefiiHostSetAssetNameResolver(
        refii::vfs::HostAssetNameForHashFn resolver) {
    refii::vfs::SetHostAssetNameResolver(resolver);
}

/// Names the region the pump should sample next — see RefiiHostGetWorldMapRegion.
REFII_HOST_ABI_EXPORT void RefiiHostSetWorldMapRegion(uint32_t index) {
    refii::vfs::RequestWorldMapRegion(index);
}

REFII_HOST_ABI_EXPORT int RefiiHostGetCrimeScreen(int32_t* out_fine, uint32_t* out_flags,
                                                  char* charge, uint32_t charge_capacity,
                                                  char* service, uint32_t service_capacity) {
    if (out_fine != nullptr) *out_fine = 0;
    if (out_flags != nullptr) *out_flags = 0u;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;

    const uint32_t owner = read32(gui + 28u);
    if (!plausible(owner)) return 0;

    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) return 0;

    constexpr uint32_t kCrimeScreenId = 21u;
    uint32_t node = read32(end_node + 4u);
    for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
        const uint32_t key = refii::guest::ScreenIdToTu1(read32(node + 12u));
        if (key == kCrimeScreenId) {
            const uint32_t screen = read32(node + 16u);
            if (!plausible(screen)) return 0;

            if (out_fine != nullptr) {
                *out_fine = static_cast<int32_t>(read32(screen + 40u));
            }

            if (out_flags != nullptr) {
                uint32_t flags = 0u;
                if (GuestByteAt(base, screen + 44u) != 0u) flags |= 1u;   // PayFine
                if (GuestByteAt(base, screen + 45u) != 0u) flags |= 2u;   // CommunityService
                *out_flags = flags;
            }

            WriteOut(ReadGuestString(base, screen + 32u), charge, charge_capacity);
            WriteOut(ReadGuestString(base, screen + 36u), service, service_capacity);
            return 1;
        }

        node = read32(node + (kCrimeScreenId < key ? 0u : 8u));
    }

    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCompletedQuests(uint32_t index, uint32_t* out_record,
                                                           int32_t* out_money,
                                                           int32_t* out_renown,
                                                           char* out_conclusion,
                                                           uint32_t conclusion_capacity) {
    static refii::vfs::QuestEntry entries[refii::vfs::kMaxCompletedQuests];
    const uint32_t total = refii::vfs::QuestSnapshot(entries, refii::vfs::kMaxCompletedQuests);

    if (index < total && index < refii::vfs::kMaxCompletedQuests) {
        const refii::vfs::QuestEntry& entry = entries[index];
        if (out_record != nullptr) *out_record = entry.record;
        if (out_money != nullptr) *out_money = entry.money;
        if (out_renown != nullptr) *out_renown = entry.renown;
        WriteOut(entry.conclusion, out_conclusion, conclusion_capacity);
    }

    return total;
}

/// A hard mod's published value by key ("combat.stamina"): 1 and the value when the mod's hook
/// has published one, 0 otherwise. The hook states it; nothing here samples the guest.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetModValue(const char* key, uint32_t key_length,
                                                    float* out_value) {
    if (key == nullptr) {
        return 0;
    }
    float value = 0.0f;
    if (!refii::mods::Published(std::string_view(key, key_length), value)) {
        return 0;
    }
    if (out_value != nullptr) {
        *out_value = value;
    }
    return 1;
}

/// Hands a hard mod a setting by key; its hook reads it with refii::mods::Setting.
REFII_HOST_ABI_EXPORT void RefiiHostSetModSetting(const char* key, uint32_t key_length,
                                                  float value) {
    if (key != nullptr) {
        refii::mods::SetSetting(std::string_view(key, key_length), value);
    }
}

/// Moves the mod events queued for the host into `out` as "<kind>\t<fields>\n" records (the
/// fields as the bus states them for Lua: numbers, true/false, 'strings'). Returns the bytes
/// written; an event that does not fit waits for the next call.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDrainModEvents(char* out, uint32_t capacity) {
    return static_cast<uint32_t>(refii::mods::DrainEventsForHost(out, capacity));
}

/// Which event kinds the host wants posted (bit = kind). 0 clears the queue.
REFII_HOST_ABI_EXPORT void RefiiHostSetModEventMask(uint64_t mask) {
    refii::mods::SetHostEventMask(mask);
}

/// The same for kinds 64..127 (bit = kind − 64).
REFII_HOST_ABI_EXPORT void RefiiHostSetModEventMaskHigh(uint64_t mask) {
    refii::mods::SetHostEventMaskHigh(mask);
}

/// A helper call or a query for the guest tick (the grammar is src/hooks/mod_commands.cpp); a
/// query's reply comes back through RefiiHostDrainModEvents under kind 1000 with its id.
REFII_HOST_ABI_EXPORT void RefiiHostPostModCommand(const char* line, uint32_t line_length) {
    if (line != nullptr) {
        refii::mods::PostCommandFromHost(std::string_view(line, line_length));
    }
}

/// The mods' Lua errors since the last call, one per line, oldest first. Returns the bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDrainModErrors(char* out, uint32_t capacity) {
    return static_cast<uint32_t>(refii::mods::DrainErrorsForHost(out, capacity));
}

/// The host's verdict handler for Godot scripts (null to remove), called on the guest thread.
REFII_HOST_ABI_EXPORT void RefiiHostSetModVerdictHandler(refii_mod_verdict_fn handler) {
    refii_bridge_mod_set_verdict_handler(handler);
}

/// Which event kinds the verdict handler is asked about (bit = kind).
REFII_HOST_ABI_EXPORT void RefiiHostSetModVerdictMask(uint64_t mask) {
    refii_bridge_mod_set_verdict_mask(mask);
}

/// The same for kinds 64..127 (bit = kind − 64).
REFII_HOST_ABI_EXPORT void RefiiHostSetModVerdictMaskHigh(uint64_t mask) {
    refii_bridge_mod_set_verdict_mask_high(mask);
}

/// The entity a mod handle stands for (what a Godot script holds), or 0.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostModHandleAddress(uint32_t handle) {
    return refii_bridge_mod_handle_address(handle);
}

/// A mod input action's edge for player 1 or 2; the guest raises InputAction on its next tick.
REFII_HOST_ABI_EXPORT void RefiiHostPostModInput(const char* action, uint32_t action_length,
                                                 int32_t player, int32_t pressed) {
    if (action != nullptr) {
        refii::mods::PostInputFromHost(std::string_view(action, action_length), player, pressed != 0);
    }
}

REFII_HOST_ABI_EXPORT int RefiiHostGetHeroDerived(uint32_t* words, uint32_t word_capacity,
                                                  float* values, uint32_t value_capacity,
                                                  char* title_tag, uint32_t title_capacity,
                                                  char* dog_name, uint32_t dog_capacity,
                                                  int32_t* stats, uint32_t stats_capacity,
                                                  uint32_t* trick_ids, uint32_t trick_capacity) {
    const refii::vfs::HeroDerived hero = refii::vfs::HeroDerivedSnapshot();

    const auto word = [words, word_capacity](uint32_t slot, uint32_t value) {
        if (words != nullptr && slot < word_capacity) words[slot] = value;
    };

    const auto real = [values, value_capacity](uint32_t slot, float value) {
        if (values != nullptr && slot < value_capacity) values[slot] = value;
    };

    word(0, hero.known ? 1u : 0u);
    word(1, static_cast<uint32_t>(hero.renown));
    word(2, hero.dog_known ? 1u : 0u);
    word(3, static_cast<uint32_t>(hero.dog_state));
    word(4, 0u);  // was a trick bitmask; the ids go across in their own buffer now

    real(0, hero.morality);
    real(1, hero.morality_min);
    real(2, hero.morality_max);
    real(3, hero.purity);
    real(4, hero.purity_min);
    real(5, hero.purity_max);

    for (uint32_t i = 0; i < 11u; ++i) {
        real(6u + i, hero.attractiveness[i]);
    }

    real(17, hero.attractiveness_fraction);
    // int32 levels 1..5, published as words; reading them as floats gives denormal garbage.
    word(5, static_cast<uint32_t>(hero.dog_combat));
    word(6, static_cast<uint32_t>(hero.dog_searching));
    word(7, hero.dog_trick_count);
    word(8, hero.stats_known ? 1u : 0u);
    word(9, hero.dog_name_key);
    word(10, hero.dog_definition);
    word(11, hero.dog_entity);

    for (uint32_t i = 0; i < 6u; ++i) {
        real(20u + i, hero.spell_damage[i]);
    }

    // Fixed-size char arrays in the store, already NUL-terminated by the sampler.
    if (stats != nullptr) {
        const uint32_t count =
            stats_capacity < refii::vfs::HeroDerived::kStatCount
                ? stats_capacity
                : refii::vfs::HeroDerived::kStatCount;
        for (uint32_t i = 0; i < count; ++i) {
            stats[i] = hero.stats[i];
        }
    }

    if (trick_ids != nullptr) {
        const uint32_t count = trick_capacity < hero.dog_trick_count ? trick_capacity
                                                                    : hero.dog_trick_count;
        for (uint32_t i = 0; i < count; ++i) {
            trick_ids[i] = hero.dog_trick_ids[i];
        }
    }

    WriteOut(hero.title_tag, title_tag, title_capacity);
    WriteOut(hero.dog_name, dog_name, dog_capacity);
    return hero.known ? 1 : 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroVitals(uint32_t* words, uint32_t word_capacity,
                                                     float* values, uint32_t value_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0;
    }

    const auto word = [&](uint32_t index, uint32_t value) {
        if (words != nullptr && index < word_capacity) {
            words[index] = value;
        }
    };
    const auto real = [&](uint32_t index, float value) {
        if (values != nullptr && index < value_capacity) {
            values[index] = value;
        }
    };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float out = 0.0f;
        std::memcpy(&out, &bits, sizeof(out));
        return out;
    };

    for (uint32_t i = 0; i < 17u; ++i) word(i, 0u);
    for (uint32_t i = 0; i < 20u; ++i) real(i, 0.0f);

    // Component ids are the ones each component's own registration stores beside its name —
    // CECInventory 13, CECHealth 36, CECStats 57, CECMoney 81, CECExperience 164.
    if (const uint32_t money = EntityComponent(base, hero, 81u)) {
        word(0, GuestWordAt(base, money + 0x0Cu));
    }

    if (const uint32_t health = EntityComponent(base, hero, 36u)) {
        real(0, read_float(health + 0x14u));
        real(1, read_float(health + 0x18u));
        real(2, read_float(health + 0x1Cu));

        uint32_t flags = 0u;
        if (GuestByteAt(base, health + 0x131u) != 0u) flags |= 1u;
        if (GuestByteAt(base, health + 0x133u) != 0u) flags |= 2u;
        word(2, flags);
    }

    if (const uint32_t stats = EntityComponent(base, hero, 57u)) {
        word(1, GuestWordAt(base, stats + 0x1Cu));

        real(3, read_float(stats + 0x10u));
        real(4, read_float(stats + 0x24u));
        real(5, read_float(stats + 0x14u));
        real(6, read_float(stats + 0x18u));
        real(7, read_float(stats + 0x28u));
        real(8, read_float(stats + 0x2Cu));

        real(9, read_float(stats + 0x40u));
        real(10, read_float(stats + 0x44u));
        real(11, read_float(stats + 0x48u));

        // Ids 1..14; slot 0 is never a hero ability, and the guest's own loop skips it.
        for (uint32_t id = 1u; id <= 14u; ++id) {
            word(2u + id, GuestWordAt(base, stats + 0x110u + 4u * id));
        }
    }

    // Two heap arrays, four floats each, through the vector headers based at +0x0C and +0x1C,
    // so the DATA pointers are at +0x10 and +0x20. Pools: General, Strength, Skill, Will.
    if (const uint32_t experience = EntityComponent(base, hero, 164u)) {
        const uint32_t available = GuestWordAt(base, experience + 0x10u);
        const uint32_t earned = GuestWordAt(base, experience + 0x20u);
        const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

        for (uint32_t pool = 0; pool < 4u; ++pool) {
            if (plausible(available)) {
                real(12u + pool, read_float(available + 4u * pool));
            }
            if (plausible(earned)) {
                real(16u + pool, read_float(earned + 4u * pool));
            }
        }
    }

    return hero;
}

/// The hero's INVENTORY — what the items, weapons, clothing, gift and shop screens are lists OF.
///
/// An MS-STL `std::list`, not an array, and `+0x14` is not the item count. `+0x10` is
/// `_Myhead`, a POINTER to a heap-allocated 16-byte self-linked sentinel node; `+0x14` is
/// `_Mysize`, the number of RECORDS, while the number of ITEMS is the sum of every record's
/// `Count`. From `GetNumberOfItems` (`sub_824F23D0`):
///
///     824f23d0  addi  r9, r3, 0xC      ; the list header is based at inv+0x0C
///     824f23d4  lwz   r8, 0x10(r3)     ; sentinel = *(inv+0x10)
///     824f23dc  lwz   r11, 4(r9)       ; == the same word
///     824f23e0  lwz   r11, 0(r11)      ; node = node->next
///     824f23f0  cmplw cr6, r11, r8
///     824f23f4  beqlr cr6              ; stop AT THE SENTINEL
///     824f2408  lwz   r7, 8(r11)       ; the item hangs at node+8
///     824f2410  lwz   r10, 0xC(r7)     ; Count
///     824f2414  add   r3, r10, r3      ; ...summed
///
/// (The `twui r0, 0x16` between those lines are MSVC iterator-debug traps; this walk bounds
/// its own hops instead.)
///
/// Walk hazards: `_Myhead` is written once in the constructor, so it is stable. `_Mysize` is
/// NOT atomic with the links — it is bumped before the pointers are patched — so no buffer is
/// sized from it and the walk terminates on the sentinel. An erase frees its node the moment a
/// `Count` reaches zero, so a captured `next` can dangle; every hop is re-validated and the
/// total is capped.
///
/// A RecordID is NOT unique across records: two share one id whenever the first still holds
/// instance data. Callers must group, not assume.
///
/// Four words per record — {recordId, count, category, flags} — where `recordId` is the FNV-1
/// GDB key, `category` is −1 when the item states none, and `flags` bit 0 is `Viewed` (the
/// new-item marker). Returns the number of records found, which may exceed what fitted.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroInventory(uint32_t* words, uint32_t word_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0;
    }

    const uint32_t inventory = EntityComponent(base, hero, 13u);
    if (inventory == 0u) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t sentinel = GuestWordAt(base, inventory + 0x10u);
    if (!plausible(sentinel)) {
        return 0;
    }

    // A ceiling on a walk over links another thread is patching.
    constexpr uint32_t kMaxRecords = 4096u;
    constexpr uint32_t kWordsPerRecord = 4u;

    uint32_t found = 0;
    uint32_t node = GuestWordAt(base, sentinel);

    while (plausible(node) && node != sentinel && found < kMaxRecords) {
        const uint32_t item = GuestWordAt(base, node + 8u);
        if (plausible(item)) {
            if (words != nullptr &&
                (found * kWordsPerRecord + kWordsPerRecord - 1u) < word_capacity) {
                uint32_t* row = words + found * kWordsPerRecord;
                row[0] = GuestWordAt(base, item + 0x04u);
                row[1] = GuestWordAt(base, item + 0x0Cu);
                row[2] = GuestWordAt(base, item + 0x10u);
                row[3] = GuestByteAt(base, item + 0x14u) != 0u ? 1u : 0u;
            }
            ++found;
        }

        node = GuestWordAt(base, node);
    }

    return found;
}

/// The AUGMENTS socketed into one inventory record — the weapon page's augment lines.
///
/// They are instance state, not GDB: `sub_82469AE8` (the category-1 handler of the details
/// fill `sub_824698D8`) takes the slot COUNT from the definition's
/// `AugmentableComponent.NumberOfSlots` and the CONTENTS from the item's type-1 extra record,
/// found by `sub_82764B30(item, 1)`:
///
///     item+28 / item+32   extras vector, stride 8, elem+0 = object; vtable slot 1 = its type
///     type 1              ctor `sub_82607BF8`: 20 bytes, vtable 0x820C7E40
///     rec+8 / rec+12      pairs, stride 16 (0x8246A030: `srawi r8, r9, 4`)
///     pair+0 / pair+8     PrimaryType / SecondaryType GDB record id (`sub_82608610` reads
///                         `NameTag` off each; secondary 0 when there is none)
///
/// The record is matched by its vtable, never by calling the guest's slot. Two words a pair.
/// Returns the pair count, or 0xFFFFFFFF when no inventory record of that id carries the
/// extra — which is "never seeded", where the guest falls back to the definition's
/// `InitialAugmentations`, NOT "no augments". A RecordID is not unique across records (a stack
/// splits when one instance takes instance data), so the first record HOLDING the extra wins.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetItemAugments(uint32_t record_id, uint32_t* words,
                                                        uint32_t word_capacity) {
    constexpr uint32_t kNone = 0xFFFFFFFFu;
    const uint32_t kAugmentRecordVtable = refii::guest::Address(0x820C7E40u);
    constexpr uint32_t kMaxRecords = 4096u;
    constexpr uint32_t kMaxExtras = 16u;
    constexpr uint32_t kMaxPairs = 16u;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return kNone;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return kNone;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t inventory = hero != 0u ? EntityComponent(base, hero, 13u) : 0u;
    if (inventory == 0u) {
        return kNone;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t sentinel = GuestWordAt(base, inventory + 0x10u);
    if (!plausible(sentinel)) {
        return kNone;
    }

    uint32_t hops = 0;
    for (uint32_t node = GuestWordAt(base, sentinel);
         plausible(node) && node != sentinel && hops < kMaxRecords;
         node = GuestWordAt(base, node), ++hops) {
        const uint32_t item = GuestWordAt(base, node + 8u);
        if (!plausible(item) || GuestWordAt(base, item + 0x04u) != record_id) {
            continue;
        }

        const uint32_t begin = GuestWordAt(base, item + 28u);
        const uint32_t end = GuestWordAt(base, item + 32u);
        if (!plausible(begin) || end < begin) {
            continue;
        }

        const uint32_t extras = std::min((end - begin) >> 3, kMaxExtras);
        for (uint32_t i = 0; i < extras; ++i) {
            const uint32_t record = GuestWordAt(base, begin + 8u * i);
            if (!plausible(record) || GuestWordAt(base, record) != kAugmentRecordVtable) {
                continue;
            }

            const uint32_t first = GuestWordAt(base, record + 8u);
            const uint32_t last = GuestWordAt(base, record + 12u);
            if (first == 0u) {
                return 0u;
            }
            if (!plausible(first) || last < first) {
                return kNone;
            }

            const uint32_t pairs = std::min((last - first) >> 4, kMaxPairs);
            for (uint32_t pair = 0; pair < pairs; ++pair) {
                if (words != nullptr && pair * 2u + 1u < word_capacity) {
                    words[pair * 2u] = GuestWordAt(base, first + 16u * pair);
                    words[pair * 2u + 1u] = GuestWordAt(base, first + 16u * pair + 8u);
                }
            }
            return pairs;
        }
    }

    return kNone;
}

/// The active SALES AND SHORTAGES — the world map's `Sales` rows. Two words each:
/// {nameTagHash, isShortage}.
///
/// `record+20` is `NameTagHash`, a RAW FNV-1 text key rather than a string — `sub_829A40C0`
/// copies it to a stack dword and hands its address straight to `TextManager_GetString`. So this
/// export hands across a hash, exactly like the regions one, and no string crosses the boundary.
///
/// THE CHAIN, verified in disassembly (`sub_8246EC10`, 0x8246EC54..0x8246EC78):
///
///     lwz r11, dword_83496AB8 ; lwz r11, 0xC(r11) ; lwz r10, 0x58(r11)
///     lwz r9,  4(r10)         ; lwz r11, 0x64(r9)     the SalesAndShortages manager
///     addi r30, r11, 0x14     ; lwz r8, 0x18(r11)     the list object and its sentinel
///     lwz r31, 0(r8)                                  the first node; node+0 is `next`
///
/// The record is at `node+8`, 56 bytes, and its serializer `sub_82602CB8` names every field:
/// `+20` NameTagHash, `+36` Active, `+40` PriceMultiplier, `+48` RecordId. Selection is
/// `Active != 0` and the multiplier off 1.0 — BELOW is a sale, ABOVE a shortage, and the world
/// map lists both.
///
/// Both passes are inside `sub_8246EC10`: it fills one temporary intrusive list twice, first
/// with `Active && multiplier < 1.0`, then `Active && multiplier > 1.0`, and emits rows in that
/// order. Exactly 1.0 is admitted by neither. The row fields it reads off each node — `k[7]`,
/// `k[8]`, `k[9]`, `k[3] - today + k[2]` and the float at `k+48` — are the five this export
/// hands across, measured from the same node base.
///
/// The manager is destroyed and reallocated wholesale on level reset (`sub_8234EF58`), so the
/// chain is re-walked every call and never cached; the walk is also bounded, because a torn
/// `next` on an intrusive list would otherwise loop forever.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSales(uint32_t* words, uint32_t word_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    static constexpr uint32_t kSalesHops[] = {0xCu, 0x58u, 0x4u, 0x64u};

    uint32_t at = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    for (const uint32_t hop : kSalesHops) {
        if (!plausible(at)) {
            return 0;
        }

        at = GuestWordAt(base, at + hop);
    }

    const uint32_t manager = at;
    if (!plausible(manager)) {
        return 0;
    }

    const uint32_t sentinel = GuestWordAt(base, manager + 0x18u);
    if (!plausible(sentinel)) {
        return 0;
    }

    constexpr uint32_t kMaxSales = 64u;

    // FIVE WORDS, not two. The sales PAGE shows a description, a region and a countdown beside
    // the title, and `sub_829AAB50` reads all of them off the same record: `+24` and `+28` are
    // Babel key hashes like `+20`, and the days left is
    // `Duration - currentDayCount + activationDay` — the day the sale started is at `+0` and
    // its length at `+4`. Two words made the row but left the page blank.
    constexpr uint32_t kWordsPerSale = 5u;

    // The world's game-time manager, for the day the countdown is measured against.
    uint32_t clock = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    for (const uint32_t hop : {0xCu, 0x58u, 0x4u, 0x14u}) {
        clock = plausible(clock) ? GuestWordAt(base, clock + hop) : 0u;
    }

    const uint32_t today = plausible(clock) ? GuestWordAt(base, clock + 8u) : 0u;

    // Sales first, then shortages: the guest's row order.
    uint32_t found = 0;

    const auto pass = [&](bool shortage) {
        uint32_t node = GuestWordAt(base, sentinel);

        for (uint32_t step = 0; step < kMaxSales && plausible(node) && node != sentinel; ++step) {
            const uint32_t record = node + 8u;

            if (GuestByteAt(base, record + 36u) != 0u) {
                const uint32_t raw = GuestWordAt(base, record + 40u);
                float multiplier = 1.0f;
                std::memcpy(&multiplier, &raw, sizeof(multiplier));

                // Strict on both sides: exactly 1.0 is neither, and the guest lists neither.
                if (shortage ? multiplier > 1.0f : multiplier < 1.0f) {
                    if (words != nullptr && (found * kWordsPerSale + 4u) < word_capacity) {
                        uint32_t* row = words + found * kWordsPerSale;
                        row[0] = GuestWordAt(base, record + 20u);   // NameTag key hash
                        row[1] = GuestWordAt(base, record + 24u);   // DescriptionTag key hash
                        row[2] = GuestWordAt(base, record + 28u);   // RegionTag key hash
                        row[3] = GuestWordAt(base, record + 4u) - today +
                                 GuestWordAt(base, record);         // days left
                        row[4] = raw;                               // PriceMultiplier, raw bits
                    }

                    ++found;
                }
            }

            node = GuestWordAt(base, node);
        }
    };

    pass(/*shortage=*/false);
    pass(/*shortage=*/true);

    return found;
}

/// The FAST-TRAVEL REGIONS that have at least one active stop — the world map's `Regions` rows.
/// One word each: FNV-1 of the region's name, which IS its Babel key, so the caller resolves the
/// display text against the shipped book and this never hands a string across the boundary. A
/// name we read wrongly therefore resolves to nothing and draws nothing, rather than drawing
/// rubbish.
///
/// THE CHAIN, verified in disassembly (`sub_829A3A30`, 0x829A3A4C..0x829A3A70):
///
///     lwz r11, dword_83496AB8   the app global
///     lwz r10, 0xC(r11)         +12
///     lwz r9,  0x58(r10)        +0x58
///     lwz r8,  4(r9)            +4    the world manager
///     lwz r25, 8(r8)            +8
///     lwz r7,  4(r25)           +4    the current world
///     lwz r3,  0xC(r7)          +0xC  the FastTravelManager
///
/// Below it: regions are `vector<Region*>` at FTM+8/+12 (stride 4 — a vector of POINTERS), a
/// region's name handle is `Region+0` and its levels `vector<Level*>` at Region+16/+20; a level's
/// stops are at Level+8/+12. A region is listed only when some stop is ACTIVE (`Stop+0` non-zero)
/// and states a destination (`Stop+8` non-empty) — that is what "Regions (7)" counts, not the
/// number of regions authored.
///
/// The topology is built once from `FastTravelList.txt` during boot and never grows again; only
/// `Stop+0`, one byte, changes at runtime. So there is no reallocation hazard here — but the
/// manager itself is released and replaced on level change (`sub_8231C358`, `sub_8234EF58`), so
/// the chain is re-walked every call and never cached.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFastTravelRegions(uint32_t* words,
                                                             uint32_t word_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    // The six hops above, in order. Each is checked before it is followed, so a chain that is
    // not up yet — between a level change releasing the manager and the next one building it —
    // returns nothing instead of dereferencing whatever is there.
    static constexpr uint32_t kFastTravelHops[] = {0xCu, 0x58u, 0x4u, 0x8u, 0x4u, 0xCu};

    uint32_t at = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    for (const uint32_t hop : kFastTravelHops) {
        if (!plausible(at)) {
            return 0;
        }

        at = GuestWordAt(base, at + hop);
    }

    const uint32_t manager = at;
    if (!plausible(manager)) {
        return 0;
    }

    // PAIRS, STRIDE 8 — this said "pointers, so stride 4" and it was wrong.
    //
    // `sub_82518330` is the guest's own walk of this container and it steps `v4 += 2` on a
    // `_DWORD*` (0x8251836C), taking `*v4` when non-zero. A stride of 4 reads the second word of
    // every pair as a region as well; those failed the plausibility test and were skipped, so the
    // list came out right by accident rather than by construction.
    const uint32_t begin = GuestWordAt(base, manager + 8u);
    const uint32_t end = GuestWordAt(base, manager + 12u);
    if (begin == 0u && end == 0u) {
        return 0;
    }

    constexpr uint32_t kMaxRegions = 128u;
    if (!plausible(begin) || !plausible(end) || end < begin ||
        ((end - begin) % 8u) != 0u || ((end - begin) / 8u) > kMaxRegions) {
        return 0;
    }

    // AND THE INDEX IS THE IDENTITY, not the name. `sub_826C7E90` — the handler for event 41 —
    // takes the row's Float1, subtracts the 999 the spawner added, and indexes THIS container's
    // walk output with it (`4 * v6 + v31`). It never looks at the name. So the ordinal a row
    // carries has to be its position in the guest's own output, which counts every non-null
    // entry — including regions with no active stop, which are walked but never spawned.
    uint32_t found = 0;
    uint32_t ordinal = 0;
    for (uint32_t slot = begin; slot < end; slot += 8u) {
        const uint32_t region = GuestWordAt(base, slot);
        if (region == 0u) {
            continue;
        }

        // Past the null test the entry is in the guest's output and has consumed an ordinal,
        // whatever we go on to decide about it.
        const uint32_t here = ordinal++;
        if (!plausible(region) || !RefiiRegionHasActiveStop(base, region)) {
            continue;
        }

        const std::string name = ReadGuestString(base, region);
        if (name.empty()) {
            continue;
        }

        if (words != nullptr && (found * 2u + 1u) < word_capacity) {
            words[found * 2u] = RefiiFnv1(name);
            words[found * 2u + 1u] = here;
        }

        ++found;
    }

    return found;
}

/// The hero's QUEST LOG — three words each: {recordId, flags, 0}.
///
/// `flags` bit 0 Unlocked, 1 Completed, 2 Failed, 3 AppearOnWorldMap, 4 Viewed. The world map's
/// Quests and Jobs rows are the same container filtered differently, and the split between them is
/// the quest definition's GDB `Type` (5 == Job), which the caller reads from `recordId` rather
/// than here — the host has no GDB.
///
/// THE CONTAINER, verified in disassembly (`sub_8251CA50`):
///
///     lwz r31, 0x10(r30)   begin
///     lwz r11, 0x14(r30)   end
///     lwz r11, 4(r31)      the Quest*   -- element + 4
///     addi r31, r31, 0xC   stride       -- 12 bytes per entry
///
/// It is a sorted flat map: element+0 is FNV-1 of the quest name, element+4 the `Quest*`,
/// element+8 unread by anything. The guest SORTS its own rows by the GDB `DisplayPriority` of each
/// quest afterwards (`sub_82529258` with comparator `sub_8251C9C0`), so container order is not
/// display order.
///
/// The quest's own fields, from the serializer `sub_825237C8` which names them: `+4` RecordID (the
/// GDB record hash — `sub_82522CB0` resolves it and reads `NameTag` off it), `+12` Unlocked, `+21`
/// Completed, `+22` Failed, `+77` AppearOnWorldMap, `+78` Viewed.
///
/// A PLAIN VECTOR WITH NO LOCK: accepting or dropping a quest reallocates it. Same treatment as
/// the worn vector — the bounds are re-read afterwards and a walk that saw them move is discarded.
/// The world map's family members, newest sample first-to-last. `out_names` is a flat block of
/// `stride`-byte NUL-terminated UTF-8 names; the return is the TOTAL the store holds, which may
/// exceed `capacity` so a caller can size its buffer from the first call.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFamily(char* out_names, uint32_t stride,
                                                  uint32_t capacity) {
    if (out_names == nullptr || stride == 0u || capacity == 0u) {
        return 0;
    }

    refii::vfs::FamilyMember members[refii::vfs::kMaxFamilyMembers];
    const uint32_t total = refii::vfs::FamilySnapshot(members, refii::vfs::kMaxFamilyMembers);
    const uint32_t copied = total < capacity ? total : capacity;
    for (uint32_t i = 0; i < copied; ++i) {
        std::snprintf(out_names + (static_cast<std::size_t>(i) * stride), stride, "%s",
                      members[i].name);
    }

    return total;
}

/// ONE SHOP ROW'S ASKING PRICE, by ticket.
///
/// What a shop charges is NOT the item's worth, so the item book's `ValueValue` cannot answer
/// for `PriceValue`. `sub_82992F90` — the guest's own price display — takes the shopkeeper off
/// screen 22, its `CECShopkeeper` (27), and calls `sub_82872910` to buy or `sub_828727B8` to
/// sell; both weight a base price by the shopkeeper's opinion axes and the shop's wealth, and
/// buy multiplies where sell divides, so there is no ratio to reproduce.
///
/// Named by the row's per-population TICKET. The pump answers a tick later while screen 22
/// stands, so the reply carries the ticket it is for and the page must check it.
REFII_HOST_ABI_EXPORT void RefiiHostSetShopPrice(uint32_t ticket, uint32_t sell) {
    refii::vfs::RequestShopPrice(ticket, sell != 0u);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetShopPrice(uint32_t* out_ticket, int32_t* out_price,
                                                     uint32_t* out_record) {
    const refii::vfs::ShopPrice answer = refii::vfs::ShopPriceSnapshot();

    if (out_ticket != nullptr) {
        *out_ticket = answer.ticket;
    }

    if (out_price != nullptr) {
        *out_price = answer.price;
    }

    if (out_record != nullptr) {
        *out_record = answer.record;
    }

    return answer.known ? 1u : 0u;
}

/// ONE FAMILY MEMBER'S PAGE, as eleven fixed-width strings.
///
/// `sub_829B04E0` is the family page and it is almost entirely a read: ten of its eleven text
/// elements are already-formatted `LHString`s at fixed offsets in the 52-byte record
/// `sub_8246F020` builds, set with `sub_82425DD0` (the setter that takes formatted text, not a
/// tag). The eleventh, the region, is `sub_82518490(regionMgr, record+16)` and IS a tag.
///
/// The page picks its member BY ORDINAL into that vector — the same ordinal the row carries —
/// so `index` is the row's id and nothing needs a key.
///
/// `out` is a flat block of `count` strings of `stride` bytes each, in this fixed order:
///
///     0 name              1 children          2 region (a TAG)    3 distance
///     4 travel            5 note              6 daily upkeep      7 total upkeep
///     8 happiness         9 home happiness   10 upkeep happiness  11 A-button (a TAG)
///
/// Field 11 is empty when the guest HIDES the button, which two of `sub_82470A78`'s branches do.
///
/// Returns how many fields were written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFamilyDetails(uint32_t index, char* out,
                                                         uint32_t stride, uint32_t count) {
    if (out == nullptr || stride == 0u || count == 0u) {
        return 0;
    }

    refii::vfs::FamilyMember members[refii::vfs::kMaxFamilyMembers];
    const uint32_t total = refii::vfs::FamilySnapshot(members, refii::vfs::kMaxFamilyMembers);
    if (index >= total) {
        return 0;
    }

    const refii::vfs::FamilyMember& member = members[index];
    const char* const fields[] = {
        member.name,      member.children,     member.region,
        member.distance,  member.travel,       member.note,
        member.daily_upkeep, member.total_upkeep, member.happiness,
        member.home_happiness, member.upkeep_happiness, member.prompt,
    };

    const uint32_t fields_count = static_cast<uint32_t>(sizeof(fields) / sizeof(fields[0]));
    const uint32_t written = count < fields_count ? count : fields_count;
    for (uint32_t i = 0; i < written; ++i) {
        std::snprintf(out + (static_cast<std::size_t>(i) * stride), stride, "%s", fields[i]);
    }

    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetQuestLog(uint32_t* words, uint32_t word_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0;
    }

    // 102 — QuestLog.
    const uint32_t log = EntityComponent(base, hero, 102u);
    if (log == 0u) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t begin = GuestWordAt(base, log + 0x10u);
    const uint32_t end = GuestWordAt(base, log + 0x14u);
    if (begin == 0u && end == 0u) {
        return 0;  // an empty log, not a failure
    }

    if (!plausible(begin) || !plausible(end) || end < begin) {
        return 0;
    }

    constexpr uint32_t kStride = 12u;
    constexpr uint32_t kMaxQuests = 512u;

    // EIGHT WORDS, not three. The world map's quest and job pages show a great deal more than
    // "does this quest exist": the star rating, both rewards, the objective's `%1` parameter
    // and the days remaining are all plain fields on the quest object, and the pages are blank
    // without them. Every offset is from the save serialiser `sub_825237C8`, which names them.
    // NINE since ABI 166: word 8 is the quest's OBJECTIVE ENTITY (`sub_82523500`: the weak
    // reference at +84, holder at +88, entity at the holder's +0), which the world map's
    // minimap page plots as an "other quest" marker (`sub_829ABFC0`, `minimap3`).
    constexpr uint32_t kWordsPerQuest = 9u;

    const uint32_t span = end - begin;
    if ((span % kStride) != 0u || (span / kStride) > kMaxQuests) {
        return 0;
    }

    uint32_t found = 0;
    for (uint32_t at = begin; at < end; at += kStride) {
        const uint32_t quest = GuestWordAt(base, at + 4u);
        if (!plausible(quest)) {
            continue;
        }

        if (words != nullptr && (found * kWordsPerQuest + 8u) < word_capacity) {
            uint32_t flags = 0u;
            flags |= GuestByteAt(base, quest + 12u) != 0u ? 1u : 0u;
            flags |= GuestByteAt(base, quest + 21u) != 0u ? 2u : 0u;
            flags |= GuestByteAt(base, quest + 22u) != 0u ? 4u : 0u;
            flags |= GuestByteAt(base, quest + 77u) != 0u ? 8u : 0u;
            flags |= GuestByteAt(base, quest + 78u) != 0u ? 16u : 0u;

            // `Primary` and `Active` pick the banner the quest page opens with.
            flags |= GuestByteAt(base, quest + 20u) != 0u ? 32u : 0u;
            flags |= GuestByteAt(base, quest + 13u) != 0u ? 64u : 0u;

            uint32_t* row = words + found * kWordsPerQuest;
            row[0] = GuestWordAt(base, quest + 4u);
            row[1] = flags;
            row[2] = GuestWordAt(base, quest + 28u);   // Rating — the star strip's slide
            row[3] = GuestWordAt(base, quest + 32u);   // RewardRenown
            row[4] = GuestWordAt(base, quest + 36u);   // RewardMoney
            row[5] = GuestWordAt(base, quest + 40u);   // CurrentObjectiveParameter, the `%1`
            row[6] = GuestWordAt(base, quest + 44u);   // DaysRemaining, raw float bits

            {
                const uint32_t holder = GuestWordAt(base, quest + 88u);
                const uint32_t target = plausible(holder) ? GuestWordAt(base, holder) : 0u;
                row[8] = plausible(target) ? target : 0u;
            }

            // How many task lines it has. The vector at +104/+108 is stride 8,
            // `{ LHString babelTag; u8 Succeeded; }`; the LAST entry is the current task and
            // everything before it is a completed one. The tags themselves cross through
            // RefiiHostGetQuestTasks, which only the highlighted quest ever needs.
            const uint32_t tasks_begin = GuestWordAt(base, quest + 104u);
            const uint32_t tasks_end = GuestWordAt(base, quest + 108u);
            row[7] = plausible(tasks_begin) && tasks_end > tasks_begin &&
                             ((tasks_end - tasks_begin) % 8u) == 0u &&
                             (tasks_end - tasks_begin) <= 0x400u
                         ? (tasks_end - tasks_begin) / 8u
                         : 0u;
        }

        ++found;
    }

    if (GuestWordAt(base, log + 0x10u) != begin || GuestWordAt(base, log + 0x14u) != end) {
        return 0;
    }

    return found;
}

/// Whether the golden trail is following a SALE or a FAMILY MEMBER rather than the primary
/// quest: the TrailOverrideInfo pointer at quest log +40 (component 102; `sub_8251CC00` frees
/// it when a quest is made primary, `sub_82520428` serialises it). The world map's re-emphasis
/// `sub_829A3260` (0x829a3770..0x829a37a8) shows the primary quest as merely ACTIVE while it is
/// set — emphasis 1 means "what the trail follows". Returns 1 when set, 0 when clear or unknown.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetQuestTrailOverride(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0u;
    }

    const uint32_t log = EntityComponent(base, hero, 102u);
    if (log == 0u) {
        return 0u;
    }

    const uint32_t override_info = GuestWordAt(base, log + 40u);
    return override_info >= 0x10000u && override_info < 0xC0000000u ? 1u : 0u;
}

/// WHAT THE WORLD MAP'S MINIMAP PAGE PLOTS FROM LIVE STATE — `sub_829ABFC0`'s inputs that are
/// not GDB: the current level's key and bounds, the dog, the primary quest's trail target, and
/// whether the primary quest is in this level.
///
///   [0]  current level key, `L+180` — the page plots the hero only when it equals the selected
///        region's key (`World_ComputeWorldLevelFnv1Key(World, "Albion", &firstLevel)`).
///   [1..4] `L+152, +156, +160, +164` — signed level bounds in metres (minX, minY, maxX, maxY);
///        the page's extent is `max(maxX − minX, maxY − minY)` and the projection divides the
///        RAW world position by it (0x829AEAE4..: `n = pos / extent; m = 240·(n − 0.5)`).
///   [5]  the dog entity: `sub_82363D38(roster)` = the weak reference at roster+192 (holder at
///        +196, entity at the holder's +0), drawn when its `+144 & 0x40` is set (`minimap11`).
///   [6]  1 when [7..9] hold a position: the trail target of the primary quest — the entity the
///        hero's component 33 weak-references at +316 (holder +320), its component 162, the
///        float4 at +32 (`sub_82551D40`). Plotted with `minimap1` when the primary quest is in
///        this level, `minimap17` (the exit) otherwise; the guest plots the same point for both.
///   [10] 1 when the primary quest's level name (`quest+132`) equals `L+172`, case-insensitive
///        (the `strcmpi` at 0x829AC...), 0 otherwise or when no quest is primary.
///   [11] the hero entity.
///   [12] how many TRAVELLING MERCHANTS follow, then their entities (`minimap10`): the page's
///        `sub_823235A0(mgr, &27, 2, out)` — list 2 of the entity manager (`*(*(party+12))+124`,
///        then `+ 12·2`: an intrusive list, node `{next, _, entity}`), each entity alive
///        (`+144 & 0x40`), of KIND 2 (`sub_822035E0(e, 2)`: the object at `e+120`, vtable slot
///        16 returns 2) and carrying component 27 (`sub_8250FD20`: bit 27 of the set at `e+36`),
///        then `sub_828738D8(comp27)`: the weak reference at comp27+16 (holder +20) alive, its
///        `+144 & 0x40` set and bit 26 of its `+36` set. The kind test is read, not called: the
///        slot's function is the two-word stub `li r3, 2; blr`.
///
/// `L = *(*(World+36)+4)`, `World = *(*(*(*(*(0x83496AB8+12)+88)+4)+8)+4)`, roster =
/// `*(*(*(*(0x83496AB8+12)+88)+4))`. Returns 1 when the level resolved; every other word is 0
/// when its chain did not.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMinimapLive(uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kWords = 13u;
    constexpr uint32_t kMaxMerchants = 32u;
    if (out == nullptr || capacity < kWords) {
        return 0u;
    }
    for (uint32_t i = 0; i < kWords; ++i) {
        out[i] = 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? load_be32(root + 12u) : 0u;
    const uint32_t holder = plausible(game) ? load_be32(game + 88u) : 0u;
    const uint32_t party = plausible(holder) ? load_be32(holder + 4u) : 0u;
    const uint32_t h = plausible(party) ? load_be32(party + 8u) : 0u;
    const uint32_t world = plausible(h) ? load_be32(h + 4u) : 0u;
    const uint32_t level_handle = plausible(world) ? load_be32(world + 36u) : 0u;
    const uint32_t level = plausible(level_handle) ? load_be32(level_handle + 4u) : 0u;
    if (!plausible(level)) {
        return 0u;
    }

    out[0] = load_be32(level + 180u);
    out[1] = load_be32(level + 152u);
    out[2] = load_be32(level + 156u);
    out[3] = load_be32(level + 160u);
    out[4] = load_be32(level + 164u);

    // The LOCAL player's dog, as the pump's dog sample found it: hero one's (players+196) or,
    // on a joiner, player two's own co-op dog.
    {
        const uint32_t dog = refii::vfs::HeroDerivedSnapshot().dog_entity;
        if (plausible(dog) && (GuestByteAt(base, dog + 144u) & 0x40u) != 0u) {
            out[5] = dog;
        }
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (!plausible(hero)) {
        return 1u;
    }
    out[11] = hero;

    const uint32_t comp33 = EntityComponent(base, hero, 33u);
    if (plausible(comp33)) {
        const uint32_t target_holder = load_be32(comp33 + 320u);
        const uint32_t target = plausible(target_holder) ? load_be32(target_holder) : 0u;
        const uint32_t comp162 = plausible(target) ? EntityComponent(base, target, 162u) : 0u;
        if (plausible(comp162)) {
            uint32_t bits[3];
            bool finite = true;
            for (uint32_t i = 0; i < 3u; ++i) {
                bits[i] = load_be32(comp162 + 32u + (4u * i));
                float f = 0.0f;
                std::memcpy(&f, &bits[i], sizeof(f));
                finite = finite && std::isfinite(f);
            }
            if (finite) {
                out[6] = 1u;
                out[7] = bits[0];
                out[8] = bits[1];
                out[9] = bits[2];
            }
        }
    }

    // The travelling merchants.
    {
        const uint32_t manager = plausible(party) ? party + 12u : 0u;
        const uint32_t inner = plausible(manager) ? load_be32(manager) : 0u;
        const uint32_t table = plausible(inner) ? load_be32(inner + 124u) : 0u;
        const uint32_t list = plausible(table) ? table + 24u : 0u;
        uint32_t node = plausible(list) ? load_be32(list) : 0u;
        const uint32_t tail = plausible(list) ? load_be32(list + 4u) : 0u;
        uint32_t count = 0u;
        for (uint32_t guard = 0; guard < 4096u && plausible(node) && count < kMaxMerchants; ++guard) {
            const uint32_t entity = load_be32(node + 8u);
            if (plausible(entity) && (GuestByteAt(base, entity + 144u) & 0x40u) != 0u) {
                // Component 27 present: bit 27 of the set at +36 (`sub_8250FD20` with &27).
                const bool has27 = (load_be32(entity + 36u) & (1u << 27)) != 0u;

                // Kind 2: `*(e+120)` -> vtable slot 16 -> `li r3, 2; blr`.
                bool kind2 = false;
                const uint32_t kind = load_be32(entity + 120u);
                const uint32_t kind_vtable = plausible(kind) ? load_be32(kind) : 0u;
                const uint32_t kind_fn = plausible(kind_vtable) ? load_be32(kind_vtable + 64u) : 0u;
                if (kind_fn >= refii::guest::Address(0x82000000u) && kind_fn < 0x83000000u) {
                    kind2 = load_be32(kind_fn) == 0x38600002u && load_be32(kind_fn + 4u) == 0x4E800020u;
                }

                if (has27 && kind2) {
                    const uint32_t comp27 = EntityComponent(base, entity, 27u);
                    const uint32_t owner_holder = plausible(comp27) ? load_be32(comp27 + 20u) : 0u;
                    const uint32_t owner = plausible(owner_holder) ? load_be32(owner_holder) : 0u;
                    if (plausible(owner) && (GuestByteAt(base, owner + 144u) & 0x40u) != 0u &&
                        (load_be32(owner + 36u) & (1u << 26)) != 0u &&
                        (kWords + count) < capacity) {
                        out[kWords + count] = entity;
                        ++count;
                    }
                }
            }

            if (node == tail) {
                break;
            }
            node = load_be32(node);
        }
        out[12] = count;
    }

    const uint32_t log = EntityComponent(base, hero, 102u);
    if (plausible(log)) {
        const uint32_t begin = load_be32(log + 16u);
        const uint32_t end = load_be32(log + 20u);
        if (plausible(begin) && end > begin && (end - begin) <= 0x10000u) {
            for (uint32_t at = begin; at + 12u <= end; at += 12u) {
                const uint32_t quest = load_be32(at + 4u);
                if (!plausible(quest) || GuestByteAt(base, quest + 20u) == 0u) {
                    continue;
                }
                const std::string quest_level = ReadGuestString(base, quest + 132u, 128u);
                const std::string here = ReadGuestString(base, level + 172u, 128u);
                if (!quest_level.empty() && quest_level.size() == here.size()) {
                    bool same = true;
                    for (std::size_t i = 0; i < here.size(); ++i) {
                        if (std::tolower(static_cast<unsigned char>(quest_level[i])) !=
                            std::tolower(static_cast<unsigned char>(here[i]))) {
                            same = false;
                            break;
                        }
                    }
                    out[10] = same ? 1u : 0u;
                }
                break;
            }
        }
    }

    return 1u;
}

/// ONE JOB'S JOURNEY — its distance and travel time, both already worded by the guest's own
/// providers. `RefiiHostSetQuestTravel` names the quest; the pump answers a tick later while
/// screen 23 stands, so the reply carries the record it is for and a page must check it.
REFII_HOST_ABI_EXPORT void RefiiHostSetQuestTravel(uint32_t record) {
    refii::vfs::RequestQuestTravel(record);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetQuestTravel(uint32_t* out_record, char* distance,
                                                       uint32_t distance_capacity, char* travel,
                                                       uint32_t travel_capacity, char* region,
                                                       uint32_t region_capacity, char* prompt,
                                                       uint32_t prompt_capacity, char* days,
                                                       uint32_t days_capacity) {
    const refii::vfs::QuestTravel journey = refii::vfs::QuestTravelSnapshot();

    if (out_record != nullptr) {
        *out_record = journey.record;
    }

    WriteOut(journey.distance, distance, distance_capacity);
    WriteOut(journey.travel, travel, travel_capacity);
    WriteOut(journey.region, region, region_capacity);
    WriteOut(journey.prompt, prompt, prompt_capacity);
    WriteOut(journey.days, days, days_capacity);
    return journey.known ? 1u : 0u;
}

/// ONE QUEST'S TASK LINES, as tab-separated Babel TAGS.
///
/// The world map's quest page is one concatenated string and the task lines are most of it:
/// `sub_829AFBC0` emits the LAST entry under "Current Task" and `sub_829AFCC0` bullets every
/// entry before it under "Completed Tasks". The vector is at `quest+104`/`+108`, stride 8, each
/// entry `{ LHString babelTag; u8 Succeeded; }` — and `Succeeded` is stored but never read by
/// the page: POSITION is what makes a task current rather than completed.
///
/// Tags cross, not sentences: `sub_8230E268` Babel-resolves each one and substitutes `%1` with
/// the quest's `CurrentObjectiveParameter`, and the host does both itself.
///
/// Keyed by RECORD ID rather than by index, because the list is rebuilt on every ask and an
/// index taken from a previous walk names a different quest.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetQuestTasks(uint32_t record_id, char* out,
                                                      uint32_t capacity) {
    if (out != nullptr && capacity > 0u) {
        out[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t log = hero != 0u ? EntityComponent(base, hero, 102u) : 0u;
    if (log == 0u) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t begin = GuestWordAt(base, log + 0x10u);
    const uint32_t end = GuestWordAt(base, log + 0x14u);
    if (!plausible(begin) || !plausible(end) || end < begin || ((end - begin) % 12u) != 0u) {
        return 0;
    }

    for (uint32_t at = begin; at < end; at += 12u) {
        const uint32_t quest = GuestWordAt(base, at + 4u);
        if (!plausible(quest) || GuestWordAt(base, quest + 4u) != record_id) {
            continue;
        }

        const uint32_t tasks = GuestWordAt(base, quest + 104u);
        const uint32_t tasks_end = GuestWordAt(base, quest + 108u);
        if (!plausible(tasks) || tasks_end <= tasks || ((tasks_end - tasks) % 8u) != 0u ||
            (tasks_end - tasks) > 0x400u) {
            return 0;
        }

        std::string packed;
        uint32_t count = 0;
        for (uint32_t entry = tasks; entry < tasks_end; entry += 8u) {
            if (count != 0u) {
                packed.push_back('\t');
            }

            // The tag is a NARROW string — a Babel key, not display text. Read with this
            // file's own local reader: the VFS library's is not exported, and calling it links
            // as a dangling lazy PLT entry that ends the process with a symbol lookup error.
            packed.append(ReadGuestString(base, entry));
            ++count;
        }

        WriteOut(packed, out, capacity);
        return count;
    }

    return 0;
}

/// The items the hero is currently WEARING, two words each — {recordId, category}.
///
/// Needed because a clothing list must not offer what is already on: the guest drops the worn
/// garment from its own list when the stack holds only one (`sub_8242FB28`). Godot builds that
/// list from the data areas now, so it needs the same fact.
///
/// THE CONTAINER, verified in disassembly rather than from the decompiler, which renders the
/// walk misleadingly (`IsWearingItemOfType`, 0x824C4380):
///
///     lwz r11, 0x10(r3)   begin        -> component + 16
///     lwz r7,  0x14(r3)   end          -> component + 20
///     lwz r10, 0(r11)     the item     -> element + 0 is an Entity*
///     lwz r10, 4(r10)     record hash  -> item + 4, the same field sub_824F1C90 matches
///     addi r11, r11, 0x18 stride       -> 24 bytes per element
///
/// `component + 12` is the container OBJECT — the MSVC checked-iterator base word, whose address
/// is the owner token in every `twui r0, 0x16` guard. It is never read for data and neither do
/// we. Category is `item + 16` (`sub_824C4418`). There is a SECOND identical vector at
/// `component + 28`, the pending list, which this must not walk.
///
/// NOTHING SYNCHRONISES THIS. No mutator or reader in the component takes a lock, so the guest
/// assumes single-threaded access, and a push REALLOCATES: `sub_824C6878` allocates, destroys the
/// old range and frees it before publishing the new pointers. A host walk interleaved with that
/// dereferences freed memory. So the begin pointer is re-read after the copy and a walk that saw
/// it move is discarded entirely rather than reported half-torn.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWornItems(uint32_t* words, uint32_t word_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t hero = ResolvePlayerHero(base);
    if (hero == 0u) {
        return 0;
    }

    // 15 — AppearanceModifierManager.
    const uint32_t appearance = EntityComponent(base, hero, 15u);
    if (appearance == 0u) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t begin = GuestWordAt(base, appearance + 16u);
    const uint32_t end = GuestWordAt(base, appearance + 20u);
    if (begin == 0u && end == 0u) {
        return 0;  // an empty vector, not a failure
    }

    if (!plausible(begin) || !plausible(end) || end < begin) {
        return 0;
    }

    constexpr uint32_t kStride = 24u;
    constexpr uint32_t kMaxItems = 256u;
    constexpr uint32_t kWordsPerItem = 2u;

    const uint32_t span = end - begin;
    if ((span % kStride) != 0u || (span / kStride) > kMaxItems) {
        return 0;
    }

    uint32_t found = 0;
    for (uint32_t at = begin; at < end; at += kStride) {
        const uint32_t item = GuestWordAt(base, at);
        if (!plausible(item)) {
            continue;
        }

        if (words != nullptr && (found * kWordsPerItem + 1u) < word_capacity) {
            words[found * kWordsPerItem] = GuestWordAt(base, item + 4u);
            words[found * kWordsPerItem + 1u] = GuestWordAt(base, item + 16u);
        }

        ++found;
    }

    // The buffer moved under us — a push reallocated mid-walk, so everything above may have come
    // out of freed memory. Report nothing rather than something wrong; the next poll is one frame
    // away and a clothing list briefly offering a worn item is a far smaller fault than acting on
    // garbage.
    if (GuestWordAt(base, appearance + 16u) != begin ||
        GuestWordAt(base, appearance + 20u) != end) {
        return 0;
    }

    return found;
}

/// States which directory owns this install, before the guest is brought up.
///
/// Embedded, GetExecutableFolder() is the HOST's directory — Godot's — so the guest's
/// refii.toml, saves and cache would land beside whatever binary is hosting it.
///
/// Must be called BEFORE RefiiHostInitialize: paths are resolved once, during startup.
REFII_HOST_ABI_EXPORT void RefiiHostSetAppRoot(const char* root) {
    RefiiSetAppRoot(root != nullptr && root[0] != '\0' ? std::filesystem::path{root}
                                                       : std::filesystem::path{});
}

// ---------------------------------------------------------------------------------
// Audio
// ---------------------------------------------------------------------------------

/// Takes up to `max_frames` mixed frames as interleaved stereo float32.
///
/// This call is the guest's audio clock: every frame taken credits the guest for one more.
/// Call it only with somewhere to put the audio — draining faster than playback lets the guest
/// run ahead of what is audible, and not calling it stalls the guest's audio thread (which
/// recovers by dropping after a timeout).
///
/// `out` must hold `max_frames * RefiiHostAudioFloatsPerFrame()` floats. Returns frames
/// written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPullAudio(float* out, uint32_t max_frames) {
    return refii::vfs::PopHostAudioFrames(out, max_frames);
}

/// The frame geometry, so the host does not hard-code what the binary states.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioSampleRate(void) { return refii::vfs::kGuestAudioSampleRate; }
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioChannels(void) { return refii::vfs::kHostAudioChannels; }
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioSamplesPerFrame(void) {
    return refii::vfs::kGuestAudioSamplesPerFrame;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioFloatsPerFrame(void) { return refii::vfs::kHostAudioFloatsPerFrame; }

/// Health. `pushed` settles at 187.5/s (48000/256) once the guest is producing; `dropped`
/// climbing means nothing is draining.
REFII_HOST_ABI_EXPORT void RefiiHostAudioStats(uint32_t* out_queued_frames, uint64_t* out_pushed,
                         uint64_t* out_popped, uint64_t* out_dropped,
                         int* out_driver_installed) {
    if (out_queued_frames != nullptr) {
        *out_queued_frames = refii::vfs::HostAudioQueuedFrames();
    }
    uint64_t pushed = 0;
    uint64_t popped = 0;
    uint64_t dropped = 0;
    refii::vfs::HostAudioCounters(&pushed, &popped, &dropped);
    if (out_pushed != nullptr) {
        *out_pushed = pushed;
    }
    if (out_popped != nullptr) {
        *out_popped = popped;
    }
    if (out_dropped != nullptr) {
        *out_dropped = dropped;
    }
    if (out_driver_installed != nullptr) {
        *out_driver_installed = refii::vfs::HostAudioDriverInstalled() ? 1 : 0;
    }
}

/// One live voice, flattened for P/Invoke. Mirrors refii::vfs::VoiceRecord field for
/// field; keep the two in step.
struct RefiiHostVoice {
    uint32_t handle;
    uint32_t generation;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t started;
    uint32_t retired;
    float volume;
    float pitch_ratio;
    uint32_t queued_bytes;
    uint32_t submitted_seq;
    float pan_left;
    float pan_right;
};

/// Snapshots the live voices. Pass a null buffer to count. Returns how many exist, which
/// may exceed `capacity` — the host should grow and ask again rather than lose voices.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetVoices(RefiiHostVoice* out, uint32_t capacity) {
    static_assert(sizeof(RefiiHostVoice) == sizeof(refii::vfs::VoiceRecord),
                  "RefiiHostVoice must mirror VoiceRecord exactly; a mismatch marshals "
                  "garbage into C# rather than failing to build");

    if (out == nullptr) {
        return refii::vfs::TakeVoiceRecords(nullptr, 0);
    }
    return refii::vfs::TakeVoiceRecords(reinterpret_cast<refii::vfs::VoiceRecord*>(out),
                                        capacity);
}

/// Drains one voice's PCM as interleaved 16-bit samples. `generation` guards against the
/// guest recycling a handle: a mismatch returns 0 rather than the wrong sound's audio.
/// Returns bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeVoicePcm(uint32_t handle, uint32_t generation, void* out,
                               uint32_t capacity) {
    return refii::vfs::TakeVoicePcm(handle, generation, out, capacity);
}

/// True once the voice hooks have seen any voice at all — distinguishes "Stage 2 is built
/// but the guest has not reached audio" from "Stage 2 is not running".
REFII_HOST_ABI_EXPORT int RefiiHostVoiceCaptureActive(void) { return refii::vfs::VoiceCaptureActive() ? 1 : 0; }

/// Hands per-voice playback to the host. While set, the guest's MIXED frame is discarded
/// instead of queued, since Godot is playing the individual voices. The mixer keeps running
/// and keeps being credited: it is the audio clock.
REFII_HOST_ABI_EXPORT void RefiiHostSetVoicePlaybackOwned(int owned) {
    refii::vfs::SetVoiceCaptureOwnsOutput(owned != 0);
}

/// Drains the `GUI.*` native calls intercepted since the last drain.
///
/// Each call arrives with the name the script used and the values it passed, before the
/// guest's own implementation has touched them. The encoding is flat: `out_utf8` receives
/// NUL-separated fields, `out_kinds` one byte per field. Each record is:
///
///     name \0 arg0 \0 arg1 \0 ... \0
///
/// with `out_kinds` holding the record's argument count followed by one kind byte per argument
/// (0 none, 1 number, 2 boolean, 3 string, 4 other). Numbers arrive as decimal text and
/// booleans as "0"/"1".
///
/// `lua_Number` is `float` in this title: the payload reads `*(float*)tv`, and taking eight
/// bytes as a double gives plausible garbage.
///
/// Returns the number of records written. Zero means nothing was called, which is the normal
/// answer on most frames.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeGuiNativeCalls(char* out_utf8, uint32_t utf8_capacity,
                                                           uint8_t* out_kinds,
                                                           uint32_t kinds_capacity,
                                                           uint32_t max_records) {
    if (out_utf8 == nullptr || out_kinds == nullptr || utf8_capacity == 0 || kinds_capacity == 0) {
        return 0;
    }

    std::vector<refii::vfs::GuiNativeCall> calls = refii::vfs::TakeGuiNativeCalls();
    uint32_t written = 0;
    uint32_t text_at = 0;
    uint32_t kind_at = 0;

    const auto put = [&](const std::string& field) {
        if (text_at + field.size() + 1u > utf8_capacity) {
            return false;
        }
        std::memcpy(out_utf8 + text_at, field.data(), field.size());
        text_at += static_cast<uint32_t>(field.size());
        out_utf8[text_at++] = '\0';
        return true;
    };

    for (const refii::vfs::GuiNativeCall& call : calls) {
        if (written >= max_records) {
            break;
        }
        if (kind_at + 1u + call.arguments.size() > kinds_capacity) {
            break;
        }

        const uint32_t text_mark = text_at;
        const uint32_t kind_mark = kind_at;
        bool ok = put(call.name);
        out_kinds[kind_at++] = static_cast<uint8_t>(call.arguments.size());

        // A table argument spends more than one kind byte, so each write tests for itself and
        // a record that runs out of either buffer is rolled back whole.
        const auto put_kind = [&](refii::vfs::GuiNativeArgument::Kind kind) {
            if (kind_at >= kinds_capacity) {
                return false;
            }
            out_kinds[kind_at++] = static_cast<uint8_t>(kind);
            return true;
        };

        for (const refii::vfs::GuiNativeArgument& argument : call.arguments) {
            if (!ok) break;
            ok = put_kind(argument.kind);
            if (!ok) break;
            switch (argument.kind) {
                case refii::vfs::GuiNativeArgument::Kind::kNumber:
                    ok = put(fmt::format("{}", argument.number));
                    break;
                case refii::vfs::GuiNativeArgument::Kind::kBoolean:
                    ok = put(argument.boolean ? "1" : "0");
                    break;
                case refii::vfs::GuiNativeArgument::Kind::kString:
                    ok = put(argument.text);
                    break;
                case refii::vfs::GuiNativeArgument::Kind::kTable:
                    // The field COUNT as a text field, then name/kind/value per field — the
                    // same flat two-buffer shape the rest of the record uses.
                    ok = put(fmt::format("{}", argument.fields.size()));
                    for (const refii::vfs::GuiNativeArgument::Field& field : argument.fields) {
                        if (!ok) break;
                        ok = put(field.name) && put_kind(field.kind);
                        if (!ok) break;
                        switch (field.kind) {
                            case refii::vfs::GuiNativeArgument::Kind::kNumber:
                                ok = put(fmt::format("{}", field.number));
                                break;
                            case refii::vfs::GuiNativeArgument::Kind::kBoolean:
                                ok = put(field.boolean ? "1" : "0");
                                break;
                            case refii::vfs::GuiNativeArgument::Kind::kString:
                                ok = put(field.text);
                                break;
                            default:
                                ok = put(std::string());
                                break;
                        }
                    }
                    break;
                default:
                    ok = put(std::string());
                    break;
            }
        }

        if (!ok) {
            // A record is all or nothing: half a call would attach the wrong argument list to
            // a name, so the partial write is rolled back and the drain stops here.
            text_at = text_mark;
            kind_at = kind_mark;
            break;
        }

        ++written;
    }

    return written;
}

/// Counters for the UI cut's own report: how many native calls have been intercepted, how many
/// are waiting, and how many `GUI` names the registration hook resolved.
///
/// `named` is 0 until `RegisterGUI` has run and settles at the registered count, which
/// separates "nothing intercepted" from "the namespace was never seen".
REFII_HOST_ABI_EXPORT void RefiiHostGuiNativeCounters(uint64_t* out_intercepted,
                                                      uint32_t* out_pending,
                                                      uint32_t* out_named) {
    refii::vfs::GuiNativeCounters(out_intercepted, out_pending, out_named);
}

/// True while a menu population is in flight and the captured rows are not yet whole.
///
/// A consumer that rebuilds its model from the capture must hold its last whole population
/// while this is set, or the screen blanks between `DeleteAllSpawnedMenuItems` and the last
/// `SpawnButton`.
REFII_HOST_ABI_EXPORT int RefiiHostMenuPopulating(void) {
    return refii_bridge_menu_populating();
}

/// Drains the `g_Menu:` chunk calls captured since the last drain.
///
/// Each record is a chunk plus what was staged for it. The encoding is flat so a whole batch
/// crosses in two buffers: `out_utf8` receives NUL-separated fields, `out_kinds` one byte per
/// field. Per record:
///
///     chunk \0 name0 \0 value0 \0 name1 \0 value1 \0 ...
///
/// with `out_kinds` holding the record's ATTRIBUTE COUNT followed by one kind byte per attribute
/// (0 = string, 1 = number). Numbers arrive as their decimal text.
///
/// The presentation is part of the identity and is written as the record's first field, before
/// the chunk, in hex: `menu/menuemphasisandghosting` defines a SECOND `SetItemEmphasis` and
/// `SetItemGhosting` for the non-expandable menu with different semantics (`Float1` as a
/// boolean rather than the 0..3 enum, the field named `Ghosting` rather than `Ghosted`), and
/// which one runs depends on the presentation the chunk was dispatched to.
///
/// Returns the number of records written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeMenuChunks(char* out_utf8, uint32_t utf8_capacity,
                                                       uint8_t* out_kinds, uint32_t kinds_capacity,
                                                       uint32_t max_records) {
    if (out_utf8 == nullptr || out_kinds == nullptr || utf8_capacity == 0 || kinds_capacity == 0) {
        return 0;
    }

    std::vector<refii::vfs::MenuChunkCall> calls = refii::vfs::TakeMenuChunkCalls();
    uint32_t written = 0;
    uint32_t text_at = 0;
    uint32_t kind_at = 0;

    const auto put = [&](const std::string& field) {
        if (text_at + field.size() + 1u > utf8_capacity) {
            return false;
        }
        std::memcpy(out_utf8 + text_at, field.data(), field.size());
        text_at += static_cast<uint32_t>(field.size());
        out_utf8[text_at++] = '\0';
        return true;
    };

    for (const refii::vfs::MenuChunkCall& call : calls) {
        if (written >= max_records || kind_at + 1u + call.staged.size() > kinds_capacity) {
            break;
        }

        const uint32_t text_mark = text_at;
        const uint32_t kind_mark = kind_at;
        bool ok = put(fmt::format("{:08X}", call.presentation)) && put(call.chunk);
        out_kinds[kind_at++] = static_cast<uint8_t>(call.staged.size());

        for (const refii::vfs::MenuChunkAttribute& attribute : call.staged) {
            if (!ok) break;
            out_kinds[kind_at++] = attribute.is_number ? 1u : 0u;
            ok = put(attribute.name) &&
                 put(attribute.is_number ? fmt::format("{}", attribute.number) : attribute.text);
        }

        if (!ok) {
            // A record is all or nothing: half a chunk attaches the wrong attributes to a
            // name. Roll back and stop.
            text_at = text_mark;
            kind_at = kind_mark;
            break;
        }

        ++written;
    }

    return written;
}

/// Recorded, pending and DROPPED menu-chunk counts. The queue has a ceiling, and a truncated
/// population is a menu silently missing rows.
REFII_HOST_ABI_EXPORT void RefiiHostMenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending,
                                                      uint64_t* out_dropped) {
    refii::vfs::MenuChunkCounters(out_recorded, out_pending, out_dropped);
}

/// Drains the element state changes captured since the last drain.
///
/// Five words per change: presentation, element pointer, element NAME HASH, id, value — plus
/// one kind byte (0 attribute, 1 flags, 2 slide). Flat and fixed-width: a screen mid-transition
/// writes thousands of attributes in a frame.
///
/// Key on (presentation, name hash), not on the pointer: `elem+0` is the name hash the guest's
/// own child lookup compares, and both presentation and element addresses recycle. The raw
/// pointer is valid for the frame only. The value is raw bits — an attribute's TYPE lives in
/// its record on the element, not in the write (float for the transform and opacity ids, int
/// for the rest).
///
/// Returns the number of changes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeUiElementChanges(uint32_t* out_words,
                                                             uint32_t word_capacity,
                                                             uint8_t* out_kinds,
                                                             uint32_t kind_capacity) {
    if (out_words == nullptr || out_kinds == nullptr) {
        return 0;
    }

    std::vector<refii::vfs::GuiElementChange> changes = refii::vfs::TakeGuiElementChanges();
    uint32_t written = 0;
    for (const refii::vfs::GuiElementChange& change : changes) {
        if ((written + 1u) * 5u > word_capacity || written + 1u > kind_capacity) {
            break;
        }

        uint32_t* row = out_words + (written * 5u);
        row[0] = change.presentation;
        row[1] = change.element;
        row[2] = change.name_hash;
        row[3] = change.id;
        row[4] = change.value;
        out_kinds[written] = static_cast<uint8_t>(change.kind);
        ++written;
    }

    return written;
}

/// Recorded, pending and DROPPED element changes. Unlike the statement queues, this is a
/// CHANGE LOG: a drop leaves the host permanently disagreeing about that element, so a
/// non-zero count is the signal to re-walk that presentation once.
REFII_HOST_ABI_EXPORT void RefiiHostUiElementCounters(uint64_t* out_recorded, uint32_t* out_pending,
                                                      uint64_t* out_dropped) {
    refii::vfs::GuiElementCounters(out_recorded, out_pending, out_dropped);
}

/// Asks the guest to perform a front-end action, BY NAME.
///
/// It does not happen here and it does not happen now. The name is queued, and the guest
/// picks it up on its own tick thread inside `sub_822C1BD0` — see guest_tick_pump.cpp. That
/// indirection is the entire point: this call is safe from Godot's thread precisely because
/// it touches no guest state, and the work lands where the guest's own code would do it.
///
/// Returns the queue depth after posting. A depth that climbs is a pump that has stopped
/// draining — usually because the front end is not up, which holds actions rather than
/// dropping them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPostFrontEndAction(const char* name) {
    return refii::vfs::PostGuestTickAction(name);
}

/// Adds the mouse's camera turn since the last post: degrees at the profile's middle camera
/// sensitivity, right and up positive. The payload hands all of it to the local player's active
/// camera before that camera's next update (src/vfs/src/hooks/mouse_look.cpp).
REFII_HOST_ABI_EXPORT void RefiiHostPostMouseLook(float yaw_degrees, float pitch_degrees) {
    if (std::isfinite(yaw_degrees) && std::isfinite(pitch_degrees)) {
        refii::vfs::MouseLookPost(yaw_degrees, pitch_degrees);
    }
}

/// Whether the mouse is the player's look device (the host's device switch, stated every frame).
/// While it is, the camera it drives keeps its heading as the hero moves, and a target lock takes
/// the mouse as flicks between targets (src/vfs/src/hooks/mouse_look.cpp); a pad keeps the shipped
/// camera.
REFII_HOST_ABI_EXPORT void RefiiHostSetMouseLookDevice(int32_t mouse) {
    refii::vfs::MouseLookSetDevice(mouse != 0);
}

/// The mouse's account as of the last camera update (src/vfs/src/core/mouse_look_state.cpp): the
/// yaw posted and the yaw taken (cumulative, one origin, posted degrees), the yaw the camera turned
/// for it (cumulative, the camera's degrees, rightward), the game tick of the last take, the
/// camera's degrees per posted degree, and how a turn not yet taken is drawn — `mode` 0 not at
/// all, 1 about the vertical through the focus `pivot` metres along the level line of sight, 2 in
/// place. Returns the state's sequence — odd while an update is between taking and stating, so a
/// read that straddles one can be told from a whole one. `out_flicks`: the target switches handed
/// to a target lock by the mouse, a run total.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMouseLook(uint32_t* out_tick, double* out_posted,
                                                      double* out_taken, double* out_absorbed,
                                                      float* out_factor, float* out_pivot,
                                                      int32_t* out_mode, uint32_t* out_flicks) {
    return refii::vfs::MouseLookState(out_tick, out_posted, out_taken, out_absorbed, out_factor,
                                      out_pivot, out_mode, out_flicks);
}

/// The front end's own storage criteria, so a host-drawn menu gates on what the guest gates on.
///
/// THREE NUMBERS, AND THEY COME FROM DIFFERENT PLACES ON PURPOSE.
///
/// `signed_in` is `(1 << *0x83496AF8) & *0x83496A70` — the active XAM user index against the
/// signed-in bitmask that `sub_8230AEE8` rebuilds on notification 0xA. Pure memory, so it is
/// read here rather than posted.
///
/// `limit` is `dword_83319620`. The image holds 150; `sub_82378618` — the front-end enter,
/// vtable slot 0x820A9260 — unconditionally stores 6 over it before the menu can dispatch. So
/// this is read live and is only meaningful once the front end has entered, which is the same
/// moment the count below becomes meaningful.
///
/// `count` CANNOT be read. It is the return value of `sub_82443308`, a guest call, so it comes
/// from the tick pump's sample — see SampleSavedGameCount in guest_tick_pump.cpp. -1 means the
/// pump has not taken one yet, and a caller must not read that as "no saves": the front-end
/// menu is drawn from this, and answering 0 too early is how Load Game goes missing on a
/// profile that has six saves.
///
/// These are the exact inputs to the guest's two decisions. Load Game spawns when
/// `signed_in && count > 0` (`sub_826E2220` at 0x826E2264..0x826E2278); New Game is refused
/// with GUI_REACHED_SAVE_GAME_LIMIT_WARNING when `count >= limit` (0x826E0148).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveState(uint32_t* out_signed_in, int32_t* out_count,
                               uint32_t* out_limit) {
    if (out_signed_in != nullptr) {
        *out_signed_in = 0;
    }
    if (out_count != nullptr) {
        *out_count = -1;
    }
    if (out_limit != nullptr) {
        *out_limit = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    const uint32_t kActiveUserIndex = refii::guest::Address(0x83496AF8u);
    const uint32_t kSignedInMask = refii::guest::Address(0x83496A70u);
    const uint32_t kSaveSlotLimit = refii::guest::Address(0x83319620u);

    const uint32_t user = read32(kActiveUserIndex);
    const uint32_t mask = read32(kSignedInMask);

    if (out_signed_in != nullptr) {
        // Guarded shift: the index is a 0..3 XAM user index, but it is guest memory and a
        // shift by 32 or more is undefined rather than merely wrong.
        *out_signed_in = (user < 32u && ((mask >> user) & 1u) != 0u) ? 1u : 0u;
    }
    if (out_count != nullptr) {
        *out_count = refii::vfs::GuestSavedGameCount();
    }
    if (out_limit != nullptr) {
        *out_limit = read32(kSaveSlotLimit);
    }
    return 1;
}

/// Asks the pump to re-take the saved-game count on its next front-end tick.
REFII_HOST_ABI_EXPORT void RefiiHostRefreshSaveState() {
    refii::vfs::RefreshGuestSavedGameCount();
}

/// The guest's installed-language table, so a host drawing the language menu draws the real one.
///
/// Base `dword_83319930`, stride 96, count `dword_83319934`, current index `dword_83319938`.
/// Within a row, `+0` is the ASCII locale code ("en-uk") and `+88` is a `WCHAR*` DISPLAY NAME.
/// The NAME is what the guest spawns its menu entry with — `sub_826E1C98` hands it straight to
/// the spawner at 0x826E1E88 and the front end resolves it through Babel afterwards — so that
/// is the string a host wants, not the locale code.
///
/// Names are written to `names` as NUL-separated UTF-8 in table order, so the caller splits on
/// the terminators. Returns the number of entries reported, or 0 when the table is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLanguages(uint32_t* out_current, char* names, uint32_t capacity) {
    if (out_current != nullptr) {
        *out_current = 0;
    }
    if (names != nullptr && capacity > 0) {
        names[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t kLanguageTable = refii::guest::Address(0x83319930u);
    const uint32_t kLanguageCount = refii::guest::Address(0x83319934u);
    const uint32_t kLanguageCurrent = refii::guest::Address(0x83319938u);
    constexpr uint32_t kLanguageStride = 96u;
    constexpr uint32_t kLanguageNameOffset = 88u;

    const uint32_t table = read32(kLanguageTable);
    const uint32_t count = read32(kLanguageCount);
    if (!plausible(table) || count == 0 || count > 64u) {
        return 0;
    }

    if (out_current != nullptr) {
        *out_current = read32(kLanguageCurrent);
    }

    uint32_t written = 0;
    uint32_t reported = 0;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t wide = read32(table + (i * kLanguageStride) + kLanguageNameOffset);
        ++reported;

        if (names == nullptr || capacity == 0) {
            continue;
        }

        // UTF-16 BE in guest memory, encoded out as UTF-8. Only the basic plane is handled:
        // a surrogate pair would be a language name outside it, which none of the shipped
        // locales are, and emitting a lone surrogate is worse than stopping at it.
        if (plausible(wide)) {
            for (uint32_t c = 0; c < 128u; ++c) {
                const uint16_t unit = GuestHalfAt(base, wide + (c * 2u));
                if (unit == 0) {
                    break;
                }

                char encoded[3];
                uint32_t length = 0;
                if (unit < 0x80u) {
                    encoded[0] = static_cast<char>(unit);
                    length = 1;
                } else if (unit < 0x800u) {
                    encoded[0] = static_cast<char>(0xC0u | (unit >> 6));
                    encoded[1] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    length = 2;
                } else {
                    encoded[0] = static_cast<char>(0xE0u | (unit >> 12));
                    encoded[1] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                    encoded[2] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    length = 3;
                }

                if (written + length + 1u >= capacity) {
                    break;
                }

                for (uint32_t b = 0; b < length; ++b) {
                    names[written++] = encoded[b];
                }
            }
        }

        if (written + 1u < capacity) {
            names[written++] = '\0';
        }
    }

    if (names != nullptr && written < capacity) {
        names[written] = '\0';
    }

    return reported;
}

/// Pump health: ticks observed, actions run, actions still queued.
///
/// Three numbers rather than one because they fail differently. Zero ticks is a hook that
/// never landed. Ticks climbing with a non-zero queue is the guest running but not yet ready
/// to take the action. Ticks climbing with an empty queue and no drains is nobody posting.
REFII_HOST_ABI_EXPORT void RefiiHostGuestTickPump(uint64_t* out_ticks, uint64_t* out_drained,
                            uint32_t* out_queued) {
    refii::vfs::GuestTickPumpCounters(out_ticks, out_drained, out_queued);
}

/// Publishes one controller's state, in XInput terms, for the guest to read.
///
/// Godot owns input the same way it owns the window: it reads the real device and pushes
/// a snapshot here, and the guest's XInput calls are served from it. Translating to Xbox
/// vocabulary happens once, on the way in.
///
/// `buttons` is a mask of X_INPUT_GAMEPAD_* bits; triggers are 0..255; thumbsticks are
/// -32768..32767 with +Y up, which is the Xbox convention and NOT Godot's screen-space
/// one — the caller flips it.
///
/// Safe to call every frame from the host's main thread while the guest reads on its
/// own; the slot is a seqlock, so neither side blocks the other.
REFII_HOST_ABI_EXPORT void RefiiHostSetControllerState(uint32_t user_index, int connected, uint32_t buttons,
                                 uint32_t left_trigger, uint32_t right_trigger,
                                 int32_t thumb_lx, int32_t thumb_ly, int32_t thumb_rx,
                                 int32_t thumb_ry) {
    const auto clamp_axis = [](int32_t value) {
        if (value < -32768) return static_cast<int16_t>(-32768);
        if (value > 32767) return static_cast<int16_t>(32767);
        return static_cast<int16_t>(value);
    };

    refii::host::ControllerState state;
    state.connected = connected != 0;
    state.buttons = static_cast<uint16_t>(buttons & 0xFFFFu);
    state.left_trigger = static_cast<uint8_t>(left_trigger > 255 ? 255 : left_trigger);
    state.right_trigger = static_cast<uint8_t>(right_trigger > 255 ? 255 : right_trigger);
    state.thumb_lx = clamp_axis(thumb_lx);
    state.thumb_ly = clamp_axis(thumb_ly);
    state.thumb_rx = clamp_axis(thumb_rx);
    state.thumb_ry = clamp_axis(thumb_ry);

    refii::host::PublishControllerState(user_index, state);
}

/// Copies the current Bink video frame out of guest memory as RGBA8.
///
/// Fable 2 decodes Bink on the CPU into four 8-bit LINEAR planes (Y, cR, cB, A) that the
/// GPU used to sample in place. With no GPU those planes are still produced and still
/// correct, so showing video needs no emulation at all — only this read and a colour
/// conversion. Layout confirmed against ExpandBink (0x832C49E8), whose FIRST ARGUMENT is
/// the BINKFRAMEBUFFERS block:
///
///   FB+4/+8    luma width/height        FB+12/+16  chroma width/height
///   FB+20      FrameNum (2 frames, alternating)
///   FB+24 + 48*n + 12*k + 4/+8         plane k Buffer / pitch  (k: 0=Y 1=cR 2=cB 3=A)
///
/// CHROMA IS NOT HALF LUMA — every dimension is independently (x+7) & ~7, so the chroma
/// sizes are read from the struct and the sample position is scaled by the real ratio.
///
/// Writes width*height*4 bytes of RGBA into `out_rgba`. Returns 1 on success, 0 when no
/// video is playing or the buffer is too small; `out_width`/`out_height` are filled in
/// either way when known, so a caller can size its buffer from a failed probe.
REFII_HOST_ABI_EXPORT int RefiiHostGetVideoFrame(uint8_t* out_rgba, uint32_t capacity, uint32_t* out_width,
                           uint32_t* out_height) {
    const uint32_t fb = refii::vfs::BinkFrameBuffersGuestAddress();
    if (fb == 0) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t luma_width = load_be32(fb + 4);
    const uint32_t luma_height = load_be32(fb + 8);
    const uint32_t chroma_width = load_be32(fb + 12);
    const uint32_t chroma_height = load_be32(fb + 16);
    const uint32_t total_frames = load_be32(fb + 0);
    // Read the OTHER frame, not the one FrameNum names.
    //
    // TotalFrames is 2 because this is a double buffer, and ExpandBink decodes INTO the
    // new frame and then sets FrameNum to it (`*(FB+20) = v52 ^ 1` at 0x832c4b3c, with
    // the expander's destination taken from that same index). So FrameNum is the frame
    // being written; reading it races the decoder and produces a picture assembled from
    // two different moments — the Lionhead logo came out sheared and banded, which reads
    // as a stride bug but is tearing. The previous frame is complete and stable.
    const uint32_t current = load_be32(fb + 20) & 1u;
    const uint32_t frame = total_frames > 1 ? (current ^ 1u) : current;

    // A STRUCTURAL plausibility gate, not just a size clamp.
    //

    // The block is only meaningful while a movie is live. Once a logo finishes and the
    // player is reused the same memory reads as whatever came next, and a pure size
    // clamp happily accepts it: a run produced a correct 1280x720 frame and then
    // "2258x2257", which is under any sane cap and would have been uploaded as a frame.
    // TotalFrames is the strongest discriminator — Bink uses 1 or 2 — and the pitch and
    // chroma relations have to hold for any real plane set.
    if (total_frames == 0 || total_frames > 2) {
        return 0;
    }

    if (luma_width < 16 || luma_height < 16 || luma_width > 4096 || luma_height > 4096 ||
        chroma_width == 0 || chroma_height == 0 || chroma_width > luma_width ||
        chroma_height > luma_height) {
        return 0;
    }

    // Dimensions are published only once the block has PASSED validation. Reporting them
    // earlier meant a caller that ignored the return value sized a texture from a torn
    // read — one run allocated 2258x2257 from a dead player's memory.
    if (out_width != nullptr) {
        *out_width = luma_width;
    }
    if (out_height != nullptr) {
        *out_height = luma_height;
    }

    // A null buffer is a SIZE PROBE and succeeds: the caller is asking whether video is
    // live and how big it is. Returning 0 here would make "no video playing" and "your
    // buffer is too small" indistinguishable, and the caller would size against garbage.
    if (out_rgba == nullptr) {
        return 1;
    }

    if (capacity < luma_width * luma_height * 4u) {
        return 0;
    }

    const uint32_t plane_base = fb + 24u + (48u * frame);
    const uint32_t y_ptr = load_be32(plane_base + 4);
    const uint32_t y_pitch = load_be32(plane_base + 8);
    const uint32_t cr_ptr = load_be32(plane_base + 12 + 4);
    const uint32_t cr_pitch = load_be32(plane_base + 12 + 8);
    const uint32_t cb_ptr = load_be32(plane_base + 24 + 4);
    const uint32_t cb_pitch = load_be32(plane_base + 24 + 8);

    // A plane's pitch cannot be narrower than its own width, and the buffers must sit in
    // the guest's 32-bit space with room for the whole plane.
    if (y_ptr == 0 || cr_ptr == 0 || cb_ptr == 0) {
        return 0;
    }

    if (y_pitch < luma_width || cr_pitch < chroma_width || cb_pitch < chroma_width) {
        return 0;
    }

    const auto plane_fits = [](uint32_t ptr, uint32_t pitch, uint32_t rows) {
        const uint64_t end = (uint64_t)ptr + ((uint64_t)pitch * rows);
        return ptr != 0 && end <= 0x100000000ull;
    };

    if (!plane_fits(y_ptr, y_pitch, luma_height) ||
        !plane_fits(cr_ptr, cr_pitch, chroma_height) ||
        !plane_fits(cb_ptr, cb_pitch, chroma_height)) {
        return 0;
    }

    // One-shot, AFTER validation: reporting before it burned the single log on the
    // window between the movie-open hook (which runs before the original fills the
    // block) and BinkGetFrameBuffersInfo actually populating it.
    {
        static std::atomic<bool> announced{false};
        if (!announced.exchange(true)) {
            REXLOG_INFO(
                "Refii video: total={} luma={}x{} chroma={}x{} frame={} "
                "y={{0x{:08X} pitch {}}} cr={{0x{:08X} pitch {}}} cb={{0x{:08X} pitch {}}}",
                total_frames, luma_width, luma_height, chroma_width, chroma_height,
                frame, y_ptr, y_pitch, cr_ptr, cr_pitch, cb_ptr, cb_pitch);
        }
    }

    // Rows are staged out of guest memory through the guarded copy rather than read in
    // place: a frame buffer the guest has just released must read as zeroes, not fault.
    std::vector<uint8_t> staged((std::size_t)luma_width + 2u * (std::size_t)chroma_width);
    uint8_t* y_row = staged.data();
    uint8_t* cr_row = y_row + luma_width;
    uint8_t* cb_row = cr_row + chroma_width;

    for (uint32_t row = 0; row < luma_height; ++row) {
        // Scale into chroma by the REAL ratio rather than assuming 2:1 — the planes are
        // 8-aligned independently, so at some sizes they are not exactly half.
        const uint32_t chroma_row =
            (uint32_t)((uint64_t)row * chroma_height / luma_height);
        CopyGuest(base, y_ptr + row * y_pitch, y_row, luma_width);
        CopyGuest(base, cr_ptr + chroma_row * cr_pitch, cr_row, chroma_width);
        CopyGuest(base, cb_ptr + chroma_row * cb_pitch, cb_row, chroma_width);
        uint8_t* out_row = out_rgba + ((uint64_t)row * luma_width * 4u);

        for (uint32_t col = 0; col < luma_width; ++col) {
            const uint32_t chroma_col =
                (uint32_t)((uint64_t)col * chroma_width / luma_width);

            // BT.601 studio-swing, which is what Bink encodes.
            const int32_t y = (int32_t)y_row[col] - 16;
            const int32_t u = (int32_t)cb_row[chroma_col] - 128;
            const int32_t v = (int32_t)cr_row[chroma_col] - 128;

            const int32_t c = 298 * y;
            const auto clamp = [](int32_t value) {
                if (value < 0) return (uint8_t)0;
                if (value > 255) return (uint8_t)255;
                return (uint8_t)value;
            };

            out_row[col * 4 + 0] = clamp((c + 409 * v + 128) >> 8);
            out_row[col * 4 + 1] = clamp((c - 100 * u - 208 * v + 128) >> 8);
            out_row[col * 4 + 2] = clamp((c + 516 * u + 128) >> 8);
            out_row[col * 4 + 3] = 255;
        }
    }

    return 1;
}

/// Copies the current Bink frame's PLANES out, for the GPU to combine.
///
/// The RGBA variant above converts 921,600 pixels on the CPU every frame. This hands the
/// three 8-bit planes over untouched — about 1.4 MB against 3.7 MB, with no arithmetic —
/// and lets a shader do the colour conversion where that work is free. It is also less
/// lossy: no intermediate rounding, and chroma is sampled by the GPU's own filtering
/// rather than by nearest-neighbour integer scaling.
///
/// Each plane is copied ROW BY ROW into a tightly packed destination, because the guest's
/// pitch is not the width — chroma here is 640 wide with a 768-byte pitch, and handing
/// that to a texture upload as-is produces a skewed image.
///
/// `out_desc` receives 6 uint32s: luma w, luma h, chroma w, chroma h, and the two plane
/// sizes actually written (luma, chroma). Returns 1 on success.
REFII_HOST_ABI_EXPORT int RefiiHostGetVideoPlanes(uint8_t* out_y, uint32_t y_capacity, uint8_t* out_cr,
                            uint32_t cr_capacity, uint8_t* out_cb, uint32_t cb_capacity,
                            uint32_t* out_desc) {
    const uint32_t fb = refii::vfs::BinkFrameBuffersGuestAddress();
    if (fb == 0) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t luma_width = load_be32(fb + 4);
    const uint32_t luma_height = load_be32(fb + 8);
    const uint32_t chroma_width = load_be32(fb + 12);
    const uint32_t chroma_height = load_be32(fb + 16);
    const uint32_t total_frames = load_be32(fb + 0);

    // Same double-buffer rule as the RGBA path: FrameNum names the frame being written.
    const uint32_t current = load_be32(fb + 20) & 1u;
    const uint32_t frame = total_frames > 1 ? (current ^ 1u) : current;

    if (total_frames == 0 || total_frames > 2) {
        return 0;
    }

    if (luma_width < 16 || luma_height < 16 || luma_width > 4096 || luma_height > 4096 ||
        chroma_width == 0 || chroma_height == 0 || chroma_width > luma_width ||
        chroma_height > luma_height) {
        return 0;
    }

    const uint32_t plane_base = fb + 24u + (48u * frame);
    const uint32_t y_ptr = load_be32(plane_base + 4);
    const uint32_t y_pitch = load_be32(plane_base + 8);
    const uint32_t cr_ptr = load_be32(plane_base + 16);
    const uint32_t cr_pitch = load_be32(plane_base + 20);
    const uint32_t cb_ptr = load_be32(plane_base + 28);
    const uint32_t cb_pitch = load_be32(plane_base + 32);

    if (y_ptr == 0 || cr_ptr == 0 || cb_ptr == 0 || y_pitch < luma_width ||
        cr_pitch < chroma_width || cb_pitch < chroma_width) {
        return 0;
    }

    const auto plane_fits = [](uint32_t ptr, uint32_t pitch, uint32_t rows) {
        return ptr != 0 && ((uint64_t)ptr + ((uint64_t)pitch * rows)) <= 0x100000000ull;
    };

    if (!plane_fits(y_ptr, y_pitch, luma_height) ||
        !plane_fits(cr_ptr, cr_pitch, chroma_height) ||
        !plane_fits(cb_ptr, cb_pitch, chroma_height)) {
        return 0;
    }

    const uint32_t luma_bytes = luma_width * luma_height;
    const uint32_t chroma_bytes = chroma_width * chroma_height;

    if (out_desc != nullptr) {
        out_desc[0] = luma_width;
        out_desc[1] = luma_height;
        out_desc[2] = chroma_width;
        out_desc[3] = chroma_height;
        out_desc[4] = luma_bytes;
        out_desc[5] = chroma_bytes;
    }

    // Null buffers are a size probe, as with the RGBA path.
    if (out_y == nullptr || out_cr == nullptr || out_cb == nullptr) {
        return 1;
    }

    if (y_capacity < luma_bytes || cr_capacity < chroma_bytes ||
        cb_capacity < chroma_bytes) {
        return 0;
    }

    const auto copy_plane = [&](uint32_t ptr, uint32_t pitch, uint32_t width,
                                uint32_t height, uint8_t* dst) {
        for (uint32_t row = 0; row < height; ++row) {
            CopyGuest(base, ptr + row * pitch, dst + ((std::size_t)row * width), width);
        }
    };

    copy_plane(y_ptr, y_pitch, luma_width, luma_height, out_y);
    copy_plane(cr_ptr, cr_pitch, chroma_width, chroma_height, out_cr);
    copy_plane(cb_ptr, cb_pitch, chroma_width, chroma_height, out_cb);
    return 1;
}

/// Copies the .bik the guest just handed to BinkOpen, so the HOST can play it.
///
/// For the startup logos the guest loads the file itself and passes a BUFFER, so this
/// hands the bytes straight over — no second read of a file already paid for. Every
/// movie AFTER the logos is opened by NAME instead; those carry no buffer and come back
/// from RefiiHostGetRequestedVideoName, which the host resolves itself.
///
/// Pass a null buffer to probe the size. `out_generation` changes on every request,
/// including a repeat of the same movie, because the startup sequence replays logos.
/// Returns 1 on success, 0 when nothing has been requested or the buffer is too small.
REFII_HOST_ABI_EXPORT int RefiiHostGetRequestedVideo(unsigned char* out, uint32_t capacity, uint32_t* out_size,
                               uint32_t* out_generation) {
    uint32_t address = 0;
    uint32_t size = 0;
    uint32_t generation = 0;
    uint32_t swapped = 0;
    refii::vfs::RequestedBinkVideo(&address, &size, &generation, &swapped);

    if (out_generation != nullptr) {
        *out_generation = generation;
    }
    if (out_size != nullptr) {
        *out_size = size;
    }

    if (address == 0 || size == 0) {
        return 0;
    }

    if (out == nullptr) {
        return 1;  // size probe
    }

    if (capacity < size) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // The guest's loader byte-swaps every DWORD on the way in — a big-endian console
    // reading a little-endian container — so the bytes in memory read 'iKIB' rather than
    // 'BIKi'. Handing that to FFmpeg decodes nothing. Swapping back on the way out
    // restores the file exactly as it is on disc. One guarded copy, then a vector swap in
    // place; a trailing partial dword keeps its order, as it was never a whole word to swap.
    CopyGuest(base, address, out, size);
    if (swapped != 0) {
        rex::memory::copy_and_swap_32_unaligned(out, out, size / 4u);
    }
    return 1;
}

/// The PATH form of the same request — "game:\data\art\videos\....bik".
///
/// The startup logos come through as a buffer; everything after them is opened by name,
/// and the guest never loads those itself. The host resolves the path against the videos
/// folder it already indexes, which is also the only way a movie the guest opens but
/// never reads can be played at all.
///
/// `out_generation` is the SAME counter as the buffer form: a request is one or the
/// other, so the host takes whichever of the two has content for the current generation.
/// Returns the length written, 0 when the last request carried a buffer instead.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetRequestedVideoName(char* out, uint32_t capacity,
                                        uint32_t* out_generation) {
    uint32_t generation = 0;
    refii::vfs::RequestedBinkVideo(nullptr, nullptr, &generation, nullptr);
    if (out_generation != nullptr) {
        *out_generation = generation;
    }

    return refii::vfs::RequestedBinkVideoName(out, capacity);
}

/// This player's role in a co-op session, off the network manager `g_XrnmNetContext`
/// (0x83497490; `*(*(0x83496AB8)+12)+140` is its counted copy, the fallback): `+52` in a
/// session, `+53` host, `+54` joiner. 0 no session, 1 host, 2 joiner. SOLO READS AS HOST:
/// a single-player game has +52 and +53 set, so 1 is "host or solo", not "co-op host".
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopRole(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    // The manager itself (`g_XrnmNetContext`, 0x83497490); `game+140` is only its counted copy
    // and was empty on a title-joined client in game, which read as role 0 there.
    uint32_t coop = GuestWordAt(base, refii::guest::Address(0x83497490u));
    if (!plausible(coop)) {
        const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
        const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
        coop = plausible(game) ? GuestWordAt(base, game + 140u) : 0u;
    }
    if (!plausible(coop) || GuestByteAt(base, coop + 52u) == 0u) return 0;
    if (GuestByteAt(base, coop + 53u) != 0u) return 1;
    return GuestByteAt(base, coop + 54u) != 0u ? 2 : 0;
}

/// The co-op screen's two share sliders, 0..1. THE GLOBALS ARE THE LIVE VALUE — `sub_82437168`
/// writes them at 0x82437238 (`flt_833193D4`, gold) and 0x82437230 (`flt_833193D8`,
/// experience); the manager's own copies at `+176`/`+172` sat at zero under a centred pip.
/// Returns 1 when a co-op manager exists to read against.
REFII_HOST_ABI_EXPORT int RefiiHostGetCoopShares(float* out_gold, float* out_experience) {
    if (out_gold != nullptr) *out_gold = 0.5f;
    if (out_experience != nullptr) *out_experience = 0.5f;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };
    // The manager itself, the game object's copy only as a fallback — see RefiiHostGetCoopRole.
    uint32_t coop = GuestWordAt(base, refii::guest::Address(0x83497490u));
    if (!plausible(coop)) {
        const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
        const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
        coop = plausible(game) ? GuestWordAt(base, game + 140u) : 0u;
    }
    if (!plausible(coop)) return 0;
    if (out_gold != nullptr) *out_gold = read_float(refii::guest::Address(0x833193D4u));
    if (out_experience != nullptr) *out_experience = read_float(refii::guest::Address(0x833193D8u));
    return 1;
}

/// Whether the guest is IN GAME, as the guest itself states it.
///
/// `byte_83496A5E` — set to 1 by `Game_InitDatabases` at 0x82311648 and cleared by the game
/// mode's shutdown at 0x82311860, with ~150 readers. This is the signal to use rather than
/// inferring from the front end going away: `dword_83496B08` is re-pointed at a 176-byte
/// base-only GUI at that moment, so every front-end field reads out of bounds.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostIsInGame(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    return GuestByteAt(base, refii::guest::Address(0x83496A5Eu));
}

/// The guest's SIMULATION TICK, as the guest itself counts it.
///
/// `dword_83319650` is incremented by the world update, and it is the only thing in the
/// process that states when the simulation has actually advanced. Everything else a host
/// might time against — the audio clock, Godot's frame delta, wall time — measures OUR
/// clocks, and the whole question this answers is how the guest's clock relates to ours.
///
/// Two uses, and they are why it is exported rather than inferred:
///
/// 1. RATE. Sampled against wall time it gives the simulation rate directly. The sim is
///    paced by the audio device (the guest blocks in `PushHostAudioFrame`), and Godot
///    presents on vsync; those are two independent clocks, so a state that advances N times
///    a second sampled M times a second shows every character stepping unevenly however
///    correct each individual sample is. Knowing N is the difference between fixing that and
///    guessing at it.
///
/// 2. COHERENCE. Read before and after a pose copy it brackets the read. The rig arrays are
///    memcpy'd out of guest memory from Godot's thread while the simulation thread writes
///    them, with no lock on either side — so a straddled read is possible by construction,
///    and this is the cheapest thing that can DETECT one. `RefiiHostGetCamera` solves the
///    same problem by reading twice and comparing, which costs a second copy of the payload;
///    a rig is up to 160 bones of 48 bytes and does not deserve that.
///
/// Zero means the guest is absent. Callers must compare for INEQUALITY and must not assume
/// the value only ever grows, or grows by one: whether a level load resets it is not settled,
/// and a rate derived from a backwards step would be nonsense reported as fact.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGuestFrame(void) {
    return refii_bridge_guest_frame();
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimTick(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t raw;
    raw = __builtin_bswap32(GuestWordAt(base, refii::guest::Address(0x83319650u)));
    return __builtin_bswap32(raw);
}

/// The guest's own simulation clock, in seconds, and its game-tick counter.
///
/// `sub_82276D10` is the in-game loop; it derives its step from two stated rates
/// (`dbl_83319660` = 15.0 game ticks/s, `dbl_83319668` = 30.0 HF/s, raised from the image's
/// 15.0 at 0x82310900, so the image bytes give the wrong HF rate) and advances its clock by
/// exactly 1/30 s per HF step:
///
///     0x82276f14  fmadd f2, f3, f27, f26     ; base + steps*(1/30)
///     0x82276f18  stfd  f2, 0x110(r31)       ; game+0x110 = the sim clock, seconds
///
/// More than one step behind, it advances one step and re-bases, discarding the lost time:
///
///     82276fa4  cmpwi cr6, r30, 1
///     82276fb0  ble   cr6, loc_82276FC8      ; owed <= 1 -> normal
///     82276fb4  addi  r29, r28, 1            ; advance exactly one
///     82276fb8  fmadd f0, f27, f24, f31      ; f24 = 0.0, so f0 = now
///     82276fbc  stfd  f0, 0x110(r31)         ; sim clock := now
///
/// So the tick rate is not a constant: 15/s while it keeps up, lower under load, with
/// discontinuous resets.
///
/// `out_tick` is `*(*(0x83496AB8) + 0x0C) + 0xFC`, measured from the second deref (the 288-byte
/// loop object), not the 16-byte game root. Zeroed at construction (0x82310630) and incremented
/// once per game tick at 0x821c9044 with no gate — unlike `dword_83319650`, whose increment
/// self-gates on the front GameFlow command matching (0x821d46e8), which is reset to 1 on world
/// teardown (0x8235075c) and saved as "WorldFrame" (0x82350A34).
///
/// Returns 0 when the world is not up, in which case neither output is written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimClock(double* out_seconds, uint32_t* out_tick) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    if (!plausible(root)) {
        return 0;
    }

    const uint32_t game = GuestWordAt(base, root + 0xCu);
    if (!plausible(game)) {
        return 0;
    }

    const uint64_t bits = GuestWord64At(base, game + 0x110u);
    double seconds = 0.0;
    std::memcpy(&seconds, &bits, sizeof(seconds));

    // Seconds since the loop based itself. Negative or non-finite is a torn read or a world
    // that has not started.
    if (!std::isfinite(seconds) || seconds < 0.0) {
        return 0;
    }

    if (out_seconds != nullptr) {
        *out_seconds = seconds;
    }

    if (out_tick != nullptr) {
        *out_tick = GuestWordAt(base, game + 0xFCu);
    }

    return 1;
}

/// The guest's time base, owned by the host: set the mode (negative leaves it), read the
/// counters, return the current mode.
///
/// Mode 0 is the real clock: the guest's loop runs its own wall-clock accumulator and, when it
/// falls behind, advances one step and re-bases, discarding the backlog (0x82276FA4). Mode 1
/// paces it — at most one high-frequency step between reads on a thread. Mode 2 drives it: time
/// moves only when the host grants steps, so the guest's tick rate is Godot's and its stepping
/// is deterministic (required for lockstep multiplayer).
///
/// `out_watchdog` counts lapses where no grant arrived and time resumed on its own so guest
/// threads on timed waits did not freeze. See rex_payload_bridge.h for the high-half fields.
REFII_HOST_ABI_EXPORT int RefiiHostSetTimeBaseMode(int mode, uint64_t* out_capped,
                                                   uint64_t* out_granted,
                                                   uint64_t* out_watchdog) {
    if (mode >= 0) {
        refii_bridge_rex_set_time_base_mode(mode);
    }

    refii_bridge_rex_time_base_stats(out_capped, out_granted, out_watchdog);
    return refii_bridge_rex_time_base_mode();
}

/// Grants the guest `steps` high-frequency steps of time. Driven mode only; ignored otherwise.
REFII_HOST_ABI_EXPORT void RefiiHostAdvanceTimeBase(uint32_t steps) {
    refii_bridge_rex_advance_time_base(steps);
}

/// Live-effect transforms refused this run: a torn read, or a node whose transform the guest
/// has never composed.
static std::atomic<uint32_t> g_fx_refused_transforms{0u};

/// EVERY PARTICLE EFFECT THE GUEST IS CURRENTLY PLAYING, so Godot can draw the ones it did not
/// place itself.
///
/// The level's ambient effects — torches, braziers, waterfalls — Godot resolves for itself out
/// of the GDB and spawns natively. Everything the guest starts at RUNTIME had no route to the
/// screen at all: spell casts, combat impacts, a fire catching. This is that route.
///
/// IT IS A MIRROR, NOT A HOOK, and that is only possible because the particle update sits on the
/// SIM path. The chain is `sub_82319BE8` (the wall-clock main loop) -> `sub_821DC270` ->
/// `sub_821DFDD0` (world update) -> `sub_83234C38` (CParticleSystemManager::Update) -> per
/// instance `sub_8322D988`. Nothing in it touches a present, a GPU wait or the render thread, so
/// the guest keeps ticking its particle systems perfectly well with our no-GPU latch set and
/// never drawing a pixel.
///
/// THE LIST is the flat vector every instance factory pushes into — `sub_8322A388`,
/// `sub_83230F00` and their siblings all end in `sub_82450DB8(&dword_8349E48C, ...)`. Its
/// removal side, `sub_83234338`, reads back `(dword_8349E494 - dword_8349E490) >> 3`, which
/// fixes the stride at 8: an instance pointer and its refcount node.
///
/// INDICES ARE NOT STABLE. That same function retires by swapping with the last element and
/// popping, so an effect's slot changes when an unrelated one ends. The instance POINTER is the
/// identity, which is why it is the first word out.
///
/// WHY THE TRANSFORM IS A WALK. `instance+0x50` looks like a world position and is not: it is
/// the authored local translation, before the type's own offset matrix and the uniform scale.
/// `sub_821DA198` composes those into the cached local 4x4 at `instance+0x70`, and
/// `sub_832295F0` concatenates that up the `+0xF0` parent chain to reach world. Mirroring
/// `+0x50` directly would put every runtime effect somewhere plausible and wrong.
/// The transforms stated through `sub_832325A8` (position) and `sub_83231C40` (orientation), as
/// recorded by the hook in `particle_transform.cpp`, keyed by the PSInstance pointer — the same
/// identity `RefiiHostGetActiveFx` reports. These setters are not the only writers of an
/// emitter's transform (see particle_transform.cpp); this is an instrument.
///
/// The hook that records these compiles into the payload, which this library must not link;
/// the table it writes lives in refii_vfs, which both link, so the read is a direct call and a
/// feed nothing has stated yet is simply empty.
///
/// Ten words a row: instance, sequence, four position lanes, four orientation lanes. All four
/// VMX lanes are carried because which lane holds x depends on the recompiler's register layout.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFx(uint32_t* out_words, uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 10u) {
        return 0u;
    }

    return refii::vfs::StatedFxTransforms(out_words, word_capacity);
}

/// How many transform statements the hook has seen since boot; 0 before the first.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFxCount(void) {
    return refii::vfs::StatedFxCount();
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveFx(uint32_t* out, uint32_t max_effects) {
    if (out == nullptr || max_effects == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto readf = [&read32](uint32_t at) {
        const uint32_t bits = read32(at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };

    /// A guest pointer we are willing to dereference. The walk runs while the guest is mutating
    /// the vector, so a torn read is expected and must be refused rather than followed.
    ///
    /// THE WHOLE 32-BIT SPACE IS VALID, and narrowing it is how this returned nothing at all on
    /// its first run. The bound used to be `>= 0x82000000 && < 0x90000000`, which is roughly the
    /// EXECUTABLE IMAGE's range — fine for a global, useless for a pointer to a heap object.
    /// Every particle instance is allocated at runtime and lands outside it, so every entry was
    /// rejected and the feed reported an empty world.
    ///
    /// This is the same test the rest of this file uses for a guest buffer: non-null, and inside
    /// the guest's own 4 GB space with room for what we are about to read.
    const auto plausible = [](uint32_t ptr) {
        return ptr >= 0x1000u && (uint64_t)ptr + 0x200ull <= 0x100000000ull;
    };

    // The composed world matrix is read from `node + 0x20`; see the read site below.

    const uint32_t kVectorBegin = refii::guest::Address(0x8349E490u);
    const uint32_t kVectorEnd = refii::guest::Address(0x8349E494u);

    const uint32_t kReservedRootName = refii::guest::Address(0x8349E678u);

    /// 22 words per node: instance pointer, name hash, flags, live-particle count, the world
    /// position (3 floats), the world basis rows 0..2 (9 floats), the effect handle, the
    /// effect's intensity (float), the element's scale (float, +412), its fade-out fraction
    /// (float), the entity that owns the effect and the entity it is attached to (0 when
    /// none). Must match `HostBridge.ActiveFxStride`; no compiler checks it.
    constexpr uint32_t kStride = 22u;

    // Both ends re-read together: the vector reallocates as it grows and no lock is taken.
    const uint32_t begin = read32(kVectorBegin);
    const uint32_t end = read32(kVectorEnd);
    if (!plausible(begin) || end < begin) {
        return 0u;
    }

    const uint32_t span = end - begin;
    if ((span % 8u) != 0u || span > 0x40000u) {
        return 0u;
    }

    const uint32_t reserved_root = read32(kReservedRootName);

    // The guest's own per-effect "still working" count.
    //
    // The retire pass `sub_83234338` walks this registry (stride 28, `v8 = (end - begin) / 28`),
    // asks each node its virtual has-work question (`sub_832299C8`), and on a yes decrements
    // record+24 (`v15 = v10[6] - 1; v10[6] = v15;`). At zero the record is erased and the
    // effect's owner is told, so `record+24 > 0` is the guest's stated liveness. The has-work
    // body is virtual and differs per particle class, so it is not transcribed here.
    struct EffectWork {
        uint32_t instance;
        uint32_t working;
    };
    EffectWork registry[512];
    uint32_t registry_count = 0u;
    {
        const uint32_t rbegin = read32(refii::guest::Address(0x8349E480u));
        const uint32_t rend = read32(refii::guest::Address(0x8349E484u));
        if (plausible(rbegin) && rend > rbegin && ((rend - rbegin) % 28u) == 0u &&
            (rend - rbegin) <= 28u * 512u) {
            for (uint32_t at = rbegin; at + 28u <= rend && registry_count < 512u; at += 28u) {
                const uint32_t instance = read32(at);
                if (!plausible(instance)) {
                    continue;
                }

                registry[registry_count].instance = instance;
                registry[registry_count].working = read32(at + 24u);
                ++registry_count;
            }
        }
    }

    uint32_t registry_hint = 0u;
    const uint32_t count = span / 8u;
    uint32_t written = 0u;

    for (uint32_t i = 0; i < count && written < max_effects; ++i) {
        const uint32_t node = read32(begin + i * 8u);
        if (!plausible(node)) {
            continue;
        }

        // `+0xC8` is "kill requested", set by `sub_83228E88` and cleared in the ctor. The retire
        // pass drops these the same tick; skipping them stops a dying effect flickering back.
        if (read8(node + 0xC8u) != 0u) {
            continue;
        }

        const uint32_t type = read32(node + 0xD0u);
        if (!plausible(type)) {
            continue;
        }

        // `type+4` is FNV-1 of the node's LOWERCASED name — `sub_8323BE18` lower-cases twice and
        // calls `rex_lhResourceName_ComputeHash`, and `sub_822CA588` is FNV-1 (multiply THEN
        // xor), the same hash the particle bank is keyed on.
        const uint32_t name_hash = read32(type + 4u);
        if (name_hash == 0u) {
            continue;
        }

        // The synthetic root is exported, flagged (flags & 8). `sub_83233300` gives every
        // type-vector one root carrying the reserved name, and its matrix is the effect's
        // placement: the positioners write it (`sub_832325A8` → elem+80, `sub_83231C40` →
        // elem+16/+32) and every emitter hangs below it. A child's world matrix includes its own
        // motion (a spline node moves along its spline each tick), so it is not the placement.
        const bool is_root = name_hash == reserved_root;

        // Still emitting: `+0xC4` counts down with -1 meaning forever, gated by the finite flag
        // at `+0x104` and the age/limit pair. Tells "spawning" from "running its last particles
        // out".
        const uint32_t remaining = read32(node + 0xC4u);
        const uint8_t finite = read8(node + 0x104u);
        const float age = readf(node + 0xFCu);
        const float limit = readf(node + 0x108u);
        const bool emitting = remaining != 0u && (finite == 0u || age <= limit);

        // Alive comes from the effect's stated working count. The node's handle is its effect.
        const uint32_t node_handle = read32(node + 0xE8u);
        const uint32_t live_particles = read32(node + 0x154u);
        bool alive = emitting || live_particles != 0u;
        if (registry_count != 0u && plausible(node_handle)) {
            for (uint32_t probe = 0u; probe < registry_count; ++probe) {
                const uint32_t at = (registry_hint + probe) % registry_count;
                if (registry[at].instance == node_handle) {
                    registry_hint = at;
                    alive = registry[at].working != 0u;
                    break;
                }
            }
        }

        // `node + 0x20` is the node's OWN transform, row-major — rows 0..2 the basis, row 3 the
        // translation with w = 1.0. It is WORLD only on a root: `sub_832295F0` composes a node's
        // world as +0x70 (`sub_821DA198`) concatenated up the +240 parent chain, and a root has
        // no parent. The host places effects from roots alone. Row 3 aliases
        // `node + 0x50`, the destination of the position setter `sub_832325A8`
        // (`stvx128 v12, r31, r24` with r24 = 0x50, after `stb r28, 0xB0(r31)`). The
        // orientation setter `sub_83231C40` writes the quaternion at `+0x10` and rebuilds these
        // rows; the per-tick writers land here too.
        float world[16];
        bool stated_finite = true;
        for (int i = 0; i < 16; ++i) {
            world[i] = readf(node + 0x20u + static_cast<uint32_t>(i) * 4u);
            if (!std::isfinite(world[i])) {
                stated_finite = false;
            }
        }

        // A torn or uncomposed matrix is refused and counted. The vector is read without a
        // lock, so a node retired mid-walk can hand back anything; a never-composed node
        // carries an all-zero matrix, not an identity.
        if (!stated_finite || (world[0] == 0.0f && world[5] == 0.0f && world[10] == 0.0f)) {
            g_fx_refused_transforms.fetch_add(1u, std::memory_order_relaxed);
            continue;
        }

        // Rows 0..2 carry the node's scale; the caller wants a rotation, so hand over the rows
        // normalised and let it build the basis.
        uint32_t* row = out + written * kStride;
        row[0] = node;
        row[1] = name_hash;

        // THE EFFECT INSTANCE THIS NODE BELONGS TO. One effect is many nodes — `sub_83233300`
        // builds one instance per entity in each type-vector plus a synthetic root for each, so
        // the beehive's six (spline, emitter) vectors are eighteen instances of one effect. They
        // all share this handle, and without it the host draws the whole effect once per node.
        const uint32_t handle = node_handle;
        row[16] = handle;
        row[2] = (alive || is_root ? 1u : 0u) | (read8(node + 0xB0u) != 0u ? 2u : 0u) |
                 (emitting ? 4u : 0u) | (is_root ? 8u : 0u);
        row[3] = live_particles;

        // The effect's intensity, `handle+52`. `sub_832323E0(inst, f)` stores it there
        // (0x832323f8 `stfs f31, 0x34(r28)`) and into every element's +408 (0x832324a8), and
        // the spawn routines bake it into each new particle (`sub_82A03D58` 0x82a04090 →
        // `sub_83228588` 0x832285f4, particle+0x48). The breadcrumber drives it every tick.
        // 1.0 is the value an untouched instance carries.
        float intensity = 1.0f;
        if (plausible(handle)) {
            const float stated = readf(handle + 52u);
            if (std::isfinite(stated)) {
                intensity = stated;
            }
        }

        {
            uint32_t bits;
            std::memcpy(&bits, &intensity, sizeof(bits));
            row[17] = bits;
        }

        // The element's scale, +412: `sub_83232290(inst, s)` (Lua `ParticleEmitter.SetScale`,
        // `ParticleAttacher.SetParticleScale`, the spell-cast action's `ParticleScale`) calls
        // every element's vtable slot 11 and `sub_83232108` reads it back from +412 — the
        // scalar the renderer folds into g_KeyFrameGlobalScales (binding 338). Meaningful on
        // a standard emitter only; the host decides by the node's type through the bank.
        // The fade-out: `KillParticleWithFadeOut` / `FadeParticles` set +192 = +196 =
        // frames (`sub_83228F00`), +196 counts down, and `sub_83228B48` multiplies the
        // element's intensity by +196/+192 (1.0 while +196 < 0, 0 when +192 == 0).
        {
            const float scale = readf(node + 412u);
            const int32_t fade_len = static_cast<int32_t>(read32(node + 192u));
            const int32_t fade_left = static_cast<int32_t>(read32(node + 196u));
            float fade = 1.0f;
            if (fade_left >= 0) {
                fade = fade_len > 0 ? static_cast<float>(fade_left) / static_cast<float>(fade_len) : 0.0f;
                if (fade > 1.0f) {
                    fade = 1.0f;
                }
            }

            uint32_t bits;
            std::memcpy(&bits, &scale, sizeof(bits));
            row[18] = bits;
            std::memcpy(&bits, &fade, sizeof(bits));
            row[19] = bits;
        }

        // WHOSE EFFECT. The handle (the ParticleSystemEntityInstance) keeps at +0x28 a pointer
        // to its emitter component's interface sub-object (`*(PSEI+40) = comp + 12`,
        // sub_82569C28 0x82569cbc; 0 when the emitter is spell-owned or disabled), whose first
        // word is the interface vtable the component's constructor writes (off_820C3158,
        // sub_82569900 0x82569948). The component (12 before it) keeps its owner entity at +4
        // (0x82569928) and, when the effect entity was attached to another, that target's
        // weak-ref box at +0x20 (sub_8256A0F0 0x8256a150). A vortex's orb reports the vortex
        // entity, a fireball's shot effects the shot entity.
        {
            uint32_t owner = 0u;
            uint32_t attached = 0u;
            if (plausible(handle)) {
                const uint32_t iface = read32(handle + 0x28u);
                if (plausible(iface) && iface >= 12u &&
                    read32(iface) == refii::guest::Address(0x820C3158u)) {
                    const uint32_t component = iface - 12u;
                    const uint32_t stated = read32(component + 4u);
                    owner = plausible(stated) ? stated : 0u;
                    const uint32_t box = read32(component + 0x20u);
                    const uint32_t target = plausible(box) ? read32(box) : 0u;
                    attached = plausible(target) ? target : 0u;
                }
            }
            row[20] = owner;
            row[21] = attached;
        }

        const float* translation = world + 12;
        for (int axis = 0; axis < 3; ++axis) {
            uint32_t bits;
            std::memcpy(&bits, translation + axis, sizeof(bits));
            row[4 + axis] = bits;
        }

        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) {
                uint32_t bits;
                std::memcpy(&bits, world + r * 4 + c, sizeof(bits));
                row[7 + r * 3 + c] = bits;
            }
        }

        ++written;
    }

    return written;
}

/// The guest's live world clock — `GameTimeManager`'s day fraction.
///
/// THE SKY AND THE LIGHTING HAVE TO FOLLOW THIS OR THEY FOLLOW NOTHING. Everything the host
/// draws from the environment theme — sun direction, sun colour, ambient, fog, cloud tint — is
/// selected and blended by a day fraction, and until this existed that fraction was whatever
/// day-set entry happened to be chosen at level load, frozen for the lifetime of the region.
///
/// The object names itself: `sub_82515D98` serialises it under the class tag "GameTimeManager"
/// and passes each field beside its own literal — "Time" at +4, "DayCount" at +8, "TimeStep" at
/// +12. The Lua binding registrar agrees independently, `Timing.GetTimeOfDay` being
/// `sub_8288B918`: `return *(float *)(a1 + 4);`.
///
/// The chain was read out of `sub_8218EAF8`, the day-set theme evaluator, which is the caller
/// that hands the fraction to `sub_8218E2F0`:
///
///     8218eb24  lwz  r9, 0xC(r11)     ; root  -> X
///     8218eb34  lwz  r8, 0x58(r9)     ; X+0x58 -> Y
///     8218eb38  lwz  r7, 4(r8)        ; Y+4    -> W
///     8218eb3c  lwz  r6, 0x14(r7)     ; W+0x14 -> T = GameTimeManager
///     8218eb40  lfs  f31, 4(r6)       ; T+4    = the day fraction
///
/// USING THE SAME CHAIN AS THE THEME SELECTOR IS THE POINT. `sub_821DF670` reaches the same T
/// by the same offsets to aim the sun, so a host that blends its themes on this value
/// reproduces the guest's own selection rather than approximating it.
///
/// THE CHAIN IS RE-WALKED EVERY CALL, NEVER CACHED. `sub_8234F320` stores T into W+0x14 after
/// virtually destroying whatever was there, and W's destructor writes a NULL back at
/// `0x823523D8` — so the whole subtree is torn down and rebuilt across a world re-init, and a
/// cached pointer would be a use-after-free rather than a stale number.
///
/// Returns 0 when the guest is absent or the chain has not been built yet; the outputs are
/// untouched in that case. Every link is checked because the pointer walk crosses objects the
/// guest itself only builds at game-database init.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTimeOfDay(float* out_fraction, int32_t* out_day, int32_t* out_frozen,
                                                     int32_t* out_moon_phase) {
    // Moon phase: `Timing.GetMoonPhase` is `sub_825A1BF0`, `return *(u32*)(a1 + 56)` on this
    // same timing object. -1 means "not read"; values outside 0..7 are refused.
    if (out_moon_phase != nullptr) {
        *out_moon_phase = -1;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    if (root == 0) {
        return 0;
    }

    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0) {
        return 0;
    }

    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0) {
        return 0;
    }

    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0) {
        return 0;
    }

    const uint32_t timing = load_be32(world + 0x14u);
    if (timing == 0) {
        return 0;
    }

    // THE VTABLE IS THE IDENTITY CHECK. 0x820BF8A8 is a one-slot vtable with exactly one
    // construction site in the whole image (0x8234F2C4), so nothing else can present it. Without
    // this a half-built chain hands back whatever the allocator left behind, and a garbage day
    // fraction reads as the sun being in the wrong place rather than as a bad pointer.
    if (load_be32(timing + 0x00u) != refii::guest::Address(0x820BF8A8u)) {
        return 0;
    }

    if (out_moon_phase != nullptr) {
        const uint32_t phase = load_be32(timing + 0x38u);
        if (phase <= 7u) {
            *out_moon_phase = static_cast<int32_t>(phase);
        }
    }

    const uint32_t raw_time = load_be32(timing + 0x04u);
    float fraction = 0.0f;
    std::memcpy(&fraction, &raw_time, sizeof(fraction));

    if (!(fraction >= 0.0f) || !(fraction < 1000.0f)) {
        // NaN or nonsense: the field is a plain float written without synchronisation, and a
        // torn or uninitialised read is better refused than propagated into the sun's angle.
        return 0;
    }

    // TRANSIENTLY >= 1.0 BY CONSTRUCTION. `sub_82298460` advances the fraction at 0x82298508 and
    // does not wrap it until 0x82298548, so a read landing in those sixteen instructions sees a
    // value past the end of the day. The guest's own wrap is a subtract of the truncated integer.
    fraction -= std::floor(fraction);

    if (out_fraction != nullptr) {
        *out_fraction = fraction;
    }

    if (out_day != nullptr) {
        *out_day = static_cast<int32_t>(load_be32(timing + 0x08u));
    }

    if (out_frozen != nullptr) {
        // T+0x24 is the count inside the "StopTimeTracker" at T+0x1C, and `sub_82298460` gates
        // the whole advance on it being zero.
        *out_frozen = load_be32(timing + 0x24u) != 0 ? 1 : 0;
    }

    return 1;
}

/// The guest's live precipitation — the four theme floats its rain and snow draws read.
///
/// THE GUEST HAS NO INDOOR TEST. Both draws gate on ONE value against `flt_820992D8`
/// (`3D 4C CC CD` = 0.05): snow on SnowSize (`0x821BDECC lvx128 v0, r6, 0x480` ...
/// `0x821BDF00 fcmpu cr6, f29, f0` / `bgt`), rain on RainDensity (`0x82280420 lvx128 v0, r6,
/// 0x4B0` ... `0x82280450 fcmpu cr6, f30, f0` / `ble` past the draw). No ceiling, portal,
/// raycast or room flag takes part. A building is dry because the painted day set under the
/// camera states no weather, the blend stack collapses and the record holds zeros — so a host
/// that draws weather from the LEVEL's day set alone rains indoors, and the fix is to read the
/// same blended record the guest reads.
///
/// The draws take a per-view render-thread copy of that record; the Lua native
/// `Weather::GetRainDensity` (`sub_8260DE88`) reads the game-side original directly, and its
/// chain is the one walked here:
///
///     8260de94  lwz  r11, dword_83496AB8   ; root
///     8260de98  lwz  r8, 0xC(r11)
///     8260de9c  lwz  r7, 0x58(r8)
///     8260dea0  lwz  r6, 4(r7)             ; World — the same W RefiiHostGetTimeOfDay reaches
///     8260dea4  lwz  r5, 0x50(r6)          ; environment theme manager
///     8260dea8  lwz  r4, 0xC(r5)           ; T, the blended record
///     8260deac  lvx128 v0, r4, 0x4B0       ; RainDensity.x
///
/// Theme values are float4 at a 16-byte stride with the value in .x; the descriptor order
/// SnowSize, SnowFallSpeed, RainSize, RainDensity is slots 71..74, i.e. T+0x480, +0x490,
/// +0x4A0, +0x4B0. The snow draw scales its sprite by the back-buffer width times +0x480
/// (`0x821BED00`) and puts +0x490 in the z of its velocity constant beside the wind
/// (`0x821BED50`), which is what fixes which of the two is the size.
///
/// Re-walked every call, never cached: the world subtree is torn down and rebuilt across a
/// world re-init. Returns 0 with the outputs untouched while the chain is unbuilt or a value
/// is not a sane float, so the caller keeps whatever it last had.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWeather(float* out_snow_size, float* out_snow_fall_speed,
                                                   float* out_rain_size, float* out_rain_density) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    if (root == 0) {
        return 0;
    }

    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0) {
        return 0;
    }

    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0) {
        return 0;
    }

    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0) {
        return 0;
    }

    const uint32_t theme_manager = load_be32(world + 0x50u);
    if (theme_manager == 0) {
        return 0;
    }

    const uint32_t blended = load_be32(theme_manager + 0x0Cu);
    if (blended == 0) {
        return 0;
    }

    float values[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    static constexpr uint32_t kSlots[4] = {0x480u, 0x490u, 0x4A0u, 0x4B0u};
    for (int i = 0; i < 4; ++i) {
        const uint32_t raw = load_be32(blended + kSlots[i]);
        std::memcpy(&values[i], &raw, sizeof(values[i]));
        // The theme evaluator lerps authored 0..1-ish values; anything else is a torn read or
        // an unbuilt record, and is refused rather than turned into a blizzard.
        if (!(values[i] >= 0.0f) || !(values[i] <= 100.0f)) {
            return 0;
        }
    }

    if (out_snow_size != nullptr) {
        *out_snow_size = values[0];
    }
    if (out_snow_fall_speed != nullptr) {
        *out_snow_fall_speed = values[1];
    }
    if (out_rain_size != nullptr) {
        *out_rain_size = values[2];
    }
    if (out_rain_density != nullptr) {
        *out_rain_density = values[3];
    }

    return 1;
}

/// The environment-theme STACK — what the guest has pushed over the day set this tick, each
/// theme by name and at the weight the guest is blending it at.
///
/// Every full-screen effect the game has is a theme on this stack: Time Control pushes
/// `EnvThemeSlowTime2` (`sub_827EC460`: `sub_82549640(mgr, name, 0.5, -1.0, 0.5)` then
/// `sub_82549810(mgr, name, 1.0, 0.5)` — in over half a second, held, and `sub_827EBB70` fades
/// it out over the spell's last half second), spell charging pushes `EnvThemeChargingLo/Hi`,
/// the banshee `BansheeAttackHigh`. The guest's own compose (`sub_8218EAF8`) lerps each stated
/// slot of each theme over its base at these weights; the host does the same over ITS base, so
/// nothing changes while the stack is empty and the stated weights alone decide the effect.
///
/// The walk is the manager's intrusive list (`sub_82549640` inserts at `mgr+164`, the tick
/// `sub_822C0E08` iterates it): `S = *(mgr+168)` is the sentinel, `node = *S`, `node = *node`
/// until it returns to S; the record starts at `node+8` — `+0` a refcounted name string
/// (`sub_822651A0` copies the pointer, characters at `*name`), `+4` the theme block, `+8` the
/// current weight, `+12` the target (`sub_8254A260` 0x8254a260: `+8 = 0`, `+12 = target`,
/// `+16 = hold ticks`, `+24 = Δ/tick`, `+28 = fade Δ/tick`). The name's FNV-1 is the theme's
/// GDB record hash (`sub_825483B8` resolves it the same way), which is what the host extracts
/// the theme by. `mgr = *(world+0x50)`, the object RefiiHostGetWeather already walks.
///
/// Writes 3 words per entry — name hash, weight bits, target bits — up to `max_entries`.
/// Returns the entries written; 0 while the world is unbuilt or the list is empty.
static uint32_t ReadThemeList(uint32_t* out_words, uint32_t max_entries, uint32_t sentinel_offset);

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeStack(uint32_t* out_words, uint32_t max_entries) {
    return ReadThemeList(out_words, max_entries, 168u);
}

/// The SCRIPT blend list — `EnvironmentTheme.BlendToEnvironmentTheme(name, weight, seconds)`
/// (`sub_82549B80`: find-or-insert in the list at `mgr+148`, sentinel `*(mgr+152)`), the
/// quest-stated looks: the Cursed Snowglobe's `PastMonochromeNoon`, combat failure, XP, the
/// balverine poison, Westcliff's night. `sub_8218EAF8` folds it as one group after the day set
/// and painted layers and BEFORE the stack: group weight `clamp(mgr+160)` (`sub_822C0E08`'s sum
/// of the node weights), each theme at its own weight. The list is saved with the game
/// (`sub_8254A090`, chunk "ScriptOverride"), so it is read here rather than hooked at the call.
/// Same node layout and output as RefiiHostGetThemeStack.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeBlends(uint32_t* out_words, uint32_t max_entries) {
    return ReadThemeList(out_words, max_entries, 152u);
}

static uint32_t ReadThemeList(uint32_t* out_words, uint32_t max_entries, uint32_t sentinel_offset) {
    if (out_words == nullptr || max_entries == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto load_be32 = [base](uint32_t guest_address) { return GuestWordAt(base, guest_address); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };

    const uint32_t root = load_be32(refii::guest::Address(0x83496AB8u));
    if (root == 0u) {
        return 0u;
    }
    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0u) {
        return 0u;
    }
    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0u) {
        return 0u;
    }
    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0u) {
        return 0u;
    }
    const uint32_t theme_manager = load_be32(world + 0x50u);
    if (!plausible(theme_manager)) {
        return 0u;
    }

    const uint32_t sentinel = load_be32(theme_manager + sentinel_offset);
    if (!plausible(sentinel)) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t node = load_be32(sentinel);
    // A theme stack deeper than this is a torn read, not a stack.
    for (uint32_t guard = 0; guard < 32u && plausible(node) && node != sentinel; ++guard) {
        const uint32_t record = node + 8u;
        const uint32_t name = load_be32(record);
        const uint32_t characters = plausible(name) ? load_be32(name) : 0u;
        std::string text;
        if (plausible(characters)) {
            for (uint32_t i = 0; i < 96u; ++i) {
                char c = 0;
                CopyGuest(base, characters + i, &c, 1u);
                if (c == 0) {
                    break;
                }
                text.push_back(c);
            }
        }

        const uint32_t weight_bits = load_be32(record + 8u);
        const uint32_t target_bits = load_be32(record + 12u);
        float weight = 0.0f;
        std::memcpy(&weight, &weight_bits, sizeof(weight));
        if (!text.empty() && std::isfinite(weight) && written < max_entries) {
            out_words[written * 3u] = RefiiFnv1(text);
            out_words[(written * 3u) + 1u] = weight_bits;
            out_words[(written * 3u) + 2u] = target_bits;
            ++written;
        }

        node = load_be32(node);
    }

    return written;
}

/// The render-frame fence the LEVEL LOAD waits on — state 7 of `CmdLevelChange_Update`.
///
/// `CmdLevelChange_Update` (0x823D5AA8) is a nine-state polled machine on `cmd+44`, and
/// every one of its gates fails SILENTLY: it returns each tick, logs nothing, and the load
/// simply never advances. State 7 is the one a host with no GPU has to answer for:
///
///     v83 = mode->vtbl[+16](dword_83496AB8);          /*0x823D65C8*/  -> *(*(a1+12)+104)
///     if (!sub_82387B38(*(v83 + 4))) { *(cmd+44) = 8; }
///
///     int sub_82387B38(int a1) {                       // under the render critical section
///       if (*(double *)(a1 + 152) <= *(double *)(a1 + 176) || *(BYTE *)(a1 + 600))
///         return 1;                                    // BUSY
///       return 0;                                      // idle — the load may proceed
///     }
///
/// A submitted-vs-retired pair plus a busy byte. With nothing retiring frames it answers
/// BUSY forever and the level change parks in state 7. This reports the three values so a
/// run can say which of them is holding, instead of the load being invisible.
///
/// Reads the chain rather than calling the guest's virtual: slot +16 is literally
/// `return *(*(a1 + 12) + 104);`. Fills manager, submitted, DEADLINE, busy, and five
/// discriminator words. Returns 1 when the chain resolved.
///
/// `+176` IS NOT "RETIRED". It has two writers only — the ctor, and `sub_82388B48` under the
/// `+56` critical section — and it is a render DEADLINE BARRIER: "a frame has been requested
/// for time T, stay busy until the render thread passes T". `+152` is the render thread's
/// current frame time. So `+176` sitting still while `+152` climbs past it is the NORMAL
/// resting state, not a stall; it means nobody has posted a new deadline.
REFII_HOST_ABI_EXPORT int RefiiHostGetRenderFence(uint32_t* out_manager, double* out_submitted,
                            double* out_retired, uint32_t* out_busy,
                            uint32_t* out_arms) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto read_double = [base](uint32_t at) {
        const uint64_t raw = GuestWord64At(base, at);
        double value = 0.0;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t mode = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }

    const uint32_t owner = read32(mode + 12u);
    if (!plausible(owner)) {
        return 0;
    }

    const uint32_t frame = read32(owner + 104u);
    if (!plausible(frame)) {
        return 0;
    }

    // sub_82387B38's argument is *(v83 + 4), not v83.
    const uint32_t manager = read32(frame + 4u);
    if (!plausible(manager)) {
        return 0;
    }

    if (out_manager != nullptr) *out_manager = manager;
    if (out_submitted != nullptr) *out_submitted = read_double(manager + 152u);
    if (out_retired != nullptr) *out_retired = read_double(manager + 176u);
    if (out_busy != nullptr) *out_busy = GuestByteAt(base, manager + 600u);

    // WHICH ARM is holding the load, if any. Reads only — nothing here writes guest memory.
    //
    // The object is the "3D Engine" (0x260 bytes, ctor sub_823877E8), and sub_82387B38 is its
    // own `IsEnginePreLoading` — the name comes from the guest's Lua registration in
    // sub_82387DC0, not from a guess. So state 7 of the level change is literally
    // "while (IsEnginePreLoading()) wait".
    //
    // `+600` carries NO state of its own: sub_82388380 re-copies `byte_83496E20` into it every
    // render-thread iteration (`lbz r30, byte_83496E20` / `stb r30, 0x258(r31)`). That global
    // has exactly ONE writer, sub_82181B68, reachable only from the 3D Engine thread, and it
    // is decided by three arms — none of which touches a present, flip, fence or render
    // callback, so nothing in the producer depends on the GPU.
    //
    // Which arm is holding takes a discriminator, and these five values are it:
    //   view/vector cursor `engine+116` and `engine+124` — both -1 means nothing is
    //     recomputing the flag and it is a STALE 1 (sub_82388040 sets +124 to -1 during the
    //     level load; only a re-submitted view clears it),
    //   `0x83496E34` in 1..4 means the resource-preload arm is holding,
    //   `0x8349FB5C` non-zero means the streamed-texture-residency arm is holding,
    //   `0x83496E20` should equal +600, and a disagreement means the mirror itself is stale.
    //
    // The third arm walks the scene container and TIMES ITSELF OUT, so it cannot hold this
    // forever and is not worth reporting.
    if (out_arms != nullptr) {
        out_arms[0] = read32(manager + 116u);
        out_arms[1] = read32(manager + 124u);
        out_arms[2] = GuestWordAt(base, refii::guest::Address(0x83496E34u));
        out_arms[3] = GuestWordAt(base, refii::guest::Address(0x8349FB5Cu));
        out_arms[4] = GuestByteAt(base, refii::guest::Address(0x83496E20u));
    }

    return 1;
}

/// The LOADING SCREEN's take-down handshake — the guest thread's one unbounded wait.
///
/// `sub_82361458` tears the loading screen down by asking for it and then blocking forever:
///
///     823614EC  stfs f0, 0xAC(r8)    ; obj+172 = 1.0f   fade
///     823614F0  stb  r10, 0xB0(r8)   ; obj+176 = 1      shutdown REQUESTED
///     823614FC  bl   RtlLeaveCriticalSection
///     82361528  li   r4, -1          ; INFINITE
///     8236152C  lwz  r3, 0xB4(r11)   ; obj+180 = the event
///     82361530  bl   sub_821969C8    ; WaitForSingleObjectEx(h, INFINITE, FALSE)
///
/// The only thing that ever signals that event is `sub_8235E5A0`, and only after the fade
/// counts 1.0 down to ~0 at 1/15 per tick — FIFTEEN ticks. Those ticks come from
/// `sub_82388380` on the guest's "3D Engine" thread, which ticks the loading screen and
/// then PRESENTS. With the renderer off and the guest never presenting, that loop does not
/// run, the fade never moves, the event is never set, and the game thread waits forever
/// with no log line and no error.
///
/// So this reports the handshake itself: fade, "shutdown requested", and the "done" latch
/// (obj+177) that `sub_8235E5A0` sets alongside the signal. A fade pinned at 1.0 with
/// requested=1 and done=0 IS the stall, stated by the guest's own fields.
///
/// Chain: `dword_83496AB8` -> +8 (the shared context) -> +36 (the manager) -> +4 (the
/// 400-byte screen, 0 when none). Fills manager, screen, fade, requested, done. Returns 1
/// when a screen object exists.
REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreen(uint32_t* out_manager, uint32_t* out_screen, float* out_fade,
                              uint32_t* out_requested, uint32_t* out_done) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto read_float = [&read32](uint32_t at) {
        const uint32_t raw = read32(at);
        float value = 0.0f;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    // The same priority the guest's own render loop uses at 0x8238860C: the game mode
    // first, then the front-end mode. Both carry the SAME context at +8, so the manager is
    // one object either way — but reading whichever is live avoids a null during the swap.
    uint32_t context = 0;
    // THREE ROOTS, NOT TWO — `sub_82388380` falls through 0x83496AB8, then 0x83496AC0, then
    // 0x8335C4C4 at 0x8238862C, and only gives up when all three are null.
    for (uint32_t mode_global : {refii::guest::Address(0x83496AB8u), refii::guest::Address(0x83496AC0u), refii::guest::Address(0x8335C4C4u)}) {
        const uint32_t mode = read32(mode_global);
        if (!plausible(mode)) {
            continue;
        }

        const uint32_t candidate = read32(mode + 8u);
        if (plausible(candidate)) {
            context = candidate;
            break;
        }
    }

    if (context == 0) {
        return 0;
    }

    const uint32_t manager = read32(context + 36u);
    if (!plausible(manager)) {
        return 0;
    }

    if (out_manager != nullptr) *out_manager = manager;

    // manager+4 is 0 when no loading screen exists. That is not a failure — it is the
    // answer "there is no screen to be waiting on".
    const uint32_t screen = read32(manager + 4u);
    if (out_screen != nullptr) *out_screen = screen;
    if (!plausible(screen)) {
        return 0;
    }

    if (out_fade != nullptr) *out_fade = read_float(screen + 172u);
    if (out_requested != nullptr) *out_requested = GuestByteAt(base, screen + 176u);
    if (out_done != nullptr) *out_done = GuestByteAt(base, screen + 177u);
    return 1;
}

/// WHICH loading screen the guest picked, so a host can draw the art the guest chose.
///
/// THE LOADING SCREEN IS NOT GAMEFACE. `sub_8235F108` submits its elements as direct textured
/// quads through `Render_GpuAccessLock_Enter` → `sub_821E1F40`; nothing in the module
/// (0x8235C000..0x82362000) touches a presentation. So there is no screen to mirror element by
/// element — the host draws the same textures itself, which is what makes this export the whole
/// interface rather than a hint.
///
/// TYPE is `*(u32*)(screen + 304)`, verified in `sub_8235F2F8`'s own switch at 0x8235F328. It
/// selects the art: 6 childhood, 7/8 saving, 9 loading, 0xA quitting, 0xB pubgames, 0xC
/// keystone, 0xD magedice, 0xE wizardstower; 1 is the gendered childhood pair chosen from
/// `g_StartHeroCreatureName`; 0/2/3/4/5 derive it from the destination region.
///
/// NAME is `screen + 272`, and it is an **LHString HANDLE, not a character buffer** — every
/// writer is `sub_822651A0(a1 + 272, <LHString>)`. So it needs TWO dereferences: the word at
/// +272 is a body pointer and the body's own first word is the `char*`. Reading it as a buffer,
/// or with one dereference, yields garbage that looks like a plausible string — the same shape
/// of mistake that once had every movie reported as already abandoned.
///
/// Returns 1 when a name was read. The raw pointers are reported too, so a run where the
/// indirection is wrong shows the addresses rather than silently producing an empty name.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLoadingScreenArt(uint32_t* out_type, uint32_t* out_body, char* name,
                                      uint32_t capacity) {
    if (out_type != nullptr) *out_type = 0;
    if (out_body != nullptr) *out_body = 0;
    if (name != nullptr && capacity > 0) name[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    uint32_t manager = 0;
    uint32_t screen = 0;
    float fade = 0.0f;
    uint32_t requested = 0;
    uint32_t done = 0;
    if (RefiiHostGetLoadingScreen(&manager, &screen, &fade, &requested, &done) == 0) {
        return 0;
    }

    if (out_type != nullptr) *out_type = read32(screen + 304u);

    const uint32_t body = read32(screen + 272u);
    if (out_body != nullptr) *out_body = body;
    if (!plausible(body)) {
        return 0;
    }

    const uint32_t text = read32(body);
    if (!plausible(text) || name == nullptr || capacity == 0) {
        return 0;
    }

    uint32_t i = 0;
    for (; i + 1u < capacity; ++i) {
        const char c = static_cast<char>(GuestByteAt(base, text + i));
        if (c == '\0') {
            break;
        }
        name[i] = c;
    }
    name[i] = '\0';
    return i > 0 ? 1u : 0u;
}

/// "The world is loaded and ready", which is NOT the loading screen coming down.
///
/// A byte at `game + 0x105`, where `game = *(u32*)(0x83496AB8 + 0x0C)`. `CmdLevelChange_Update`
/// (0x823D5AA8) clears it when the real load begins (state 3, 0x823D60C8) and sets it when the
/// nine-state machine finishes (state 9, 0x823D6704); the game object's constructor
/// (`sub_82310508`) starts it at 1 — "ready" is the BOOT state, before any world exists.
///
/// It IS read by the guest, at 0x82276E84 in the game tick `sub_82276D10`, which gates work on
/// `+0x104 && +0x105`. An earlier version of this comment claimed it was publish-only with no
/// reader in the image; that was wrong. Polling is still safe because we only ever READ it —
/// but nothing may ever WRITE it from the host, which "publish-only" invited.
///
/// USE IT AS A RISING EDGE ONLY. It is cleared in state 3 AFTER the blocking load returns,
/// while the entity teardown happened back in state 2 — so its falling edge is far too late to
/// free a scene by, and there is a window where it reads 1 with the old entities already gone.
/// `RefiiHostGetLevelChange` carries the command's own state word for that.
///
/// Distinct from `game + 0x104`, "a level change is pending", and from the Lua `IsLevelLoading`,
/// which compares the front gameflow command's type id to 4 and is therefore FALSE during a
/// load-from-save (type 5).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsReady(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t mode = 0;
    mode = __builtin_bswap32(GuestWordAt(base, refii::guest::Address(0x83496AB8u)));
    mode = __builtin_bswap32(mode);
    if (mode < 0x10000u || mode >= 0xC0000000u) {
        return 0;
    }

    const uint32_t game = GuestWordAt(base, mode + 0x0Cu);
    if (game < 0x10000u || game >= 0xC0000000u) {
        return 0;
    }

    return GuestByteAt(base, game + refii::guest::GameField(0x105u)) != 0 ? 1u : 0u;
}

/// Whether Godot is still building a region the guest must not be let into yet.
static std::atomic_bool g_world_build_pending{false};

/// The 3D Engine object — the thing state 7's fence is asked about.
///
/// `sub_823881C0` names this thread `"3D Engine"` at 0x823881D8, and the object is 608 bytes
/// (ctor `sub_823877E8`). The chain is a pure memory walk; the guest reaches it through a
/// virtual call whose body `sub_8220C1A0` is just `return *(*(a1 + 12) + 104);`, so nothing has
/// to be CALLED to follow it:
///
///     g      = *(0x83496AB8)
///     Game   = *(g    + 0x0C)
///     P      = *(Game + 0x68)      /* the 8-byte holder, filled at 0x82310CAC */
///     engine = *(P    + 0x04)      /* 823D65CC: lwz r3, 4(r3) off the CALL'S RESULT */
static uint32_t engine_object(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return (at >= 0x10000u && at < 0xC0000000u) ? u32(at + offset) : 0u;
    };

    uint32_t at = u32(refii::guest::Address(0x83496AB8u));
    at = step(at, 0x0Cu);
    at = step(at, 0x68u);
    at = step(at, 0x04u);
    return (at >= 0x10000u && at < 0xC0000000u) ? at : 0;
}

/// Arms or releases the render fence state 7 waits on, by writing the engine's `+176`.
///
/// THE FENCE, AND WHY THIS IS FOLLOWING THE CONTRACT RATHER THAN POKING. `sub_82387B38` is
/// bound to Lua by the shipped title as **"IsEnginePreLoading"**, with its blocking twin
/// `sub_82387CE0` as **"WaitForEngineToPreLoad"**. State 7 of `CmdLevelChange_Update` polls it
/// (0x823D65D0) and branches to the default case — which does nothing at all — while it is
/// nonzero. The title is asking its renderer whether the renderer has caught up. Godot IS the
/// renderer, and the honest answer while a region is still being built is "no".
///
/// The predicate is `engine+152 <= engine+176 || engine+600`, and it means "the engine's render
/// clock has not yet passed the last posted hand-off". `+152` is that clock, rewritten by the
/// 3D Engine thread every iteration (0x823885A0), and `+600` mirrors `byte_83496E20` the same
/// way (0x823885A4) — writing either is pointless. **`+176` is the one field with a single
/// writer (`sub_82388B48` @0x82388B80) and a single reader (the fence itself)**, and stamping
/// it then waiting is the guest's OWN idiom: `sub_821987B8` case 9 does exactly that at
/// 0x821988F0 / 0x8219892C.
///
/// POLARITY IS THE ONE THING THAT MUST NOT BE WRONG — 1 means BUSY. Held three ways: the spin
/// is `while (sub_82387B38(a1)) SleepEx(0,1)`, the call site does `bne -> default` on nonzero,
/// and the Lua name is a question whose "yes" is 1. Inverted, this deadlocks the title.
///
/// RE-ASSERTED EVERY TICK, because the world's own Update (`sub_8231D288`) stamps `+176` with
/// the real hand-off time as it drains the pending `.engine_level` — a hold written once would
/// be wiped the first time that ran.
///
/// RELEASE WRITES 0.0, not the current clock: the comparison is `<=`, so writing `+152` itself
/// would leave the fence closed.
static void apply_world_build_hold(const uint8_t* base);

/// ON by default. `REFII_HOLD_GUEST_FOR_WORLD=0` disables it if it ever misbehaves.
///
/// CAN HOLDING THIS WEDGE THE GUEST? No, and the reasoning is worth keeping because the shape
/// of it says otherwise. `sub_821987B8` runs from the main loop at 0x822771F0 every tick and
/// BEFORE the state machine at 0x82277264, and two of its cases spin on this same predicate —
/// case 9 calls `WaitForEngineToPreLoad` (0x8219892C) after RESTAMPING `+176` with the current
/// time at 0x821988F0, then waits for the render clock `+152` to pass that stamp. Since `+152`
/// comes from `sub_82B6FCE8`, which derives from D3D frame sync, that spin looks unbreakable
/// with no GPU.
///
/// IT IS NOT: the 3D Engine thread keeps its clock running headless — the value climbs steadily
/// in `[guest-fence] submitted=` — so the spin drains on its own.
///
/// What remains is bounded and intended: while WE hold, the spin waits for our release, and we
/// release on every path out of a build — success, failure and teardown. The wait is therefore
/// never longer than our own world build, which is the entire point of the mechanism.
static bool world_build_hold_enabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_HOLD_GUEST_FOR_WORLD");
        return value == nullptr || value[0] != '0';
    }();
    return enabled;
}

static void apply_world_build_hold(const uint8_t* base) {
    if (!world_build_hold_enabled()) {
        return;
    }

    const uint32_t engine = engine_object(base);
    if (engine == 0) {
        return;
    }

    // 1e30 seconds is far beyond any render clock the title will ever reach, and finite — an
    // infinity here would make the guest's own comparison behave differently under fcmpu.
    const double value = g_world_build_pending.load(std::memory_order_relaxed) ? 1e30 : 0.0;

    uint64_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    bits = __builtin_bswap64(bits);
    StoreGuest(const_cast<uint8_t*>(base), engine + 0xB0u, &bits, sizeof(bits));
}

/// THE ONE SIGNAL THAT COVERS BOTH LOAD PATHS: the world is activated and populated.
///
/// `World + 0xA8`, and it is what `game+0x105` only pretends to be. Set by the activator
/// `sub_823640E0` — `mr r25, r3` then `stb r10(=1), 0xA8(r25)` at **0x8236410C** — and cleared
/// by the world teardown `sub_82364698` at 0x82364ADC, which `World_LoadLevel_Execute` calls
/// through `sub_8231CB90` at the start of every load.
///
/// BOTH PATHS REACH IT, which is the whole point. `sub_82350F80` is the only caller of the
/// activator and has exactly two callers of its own: `CmdLevelChange_Update` state 5
/// (0x823D6210) for travel, and `CmdLoadSaveGame_Update` state 2 (0x823D7920) for a
/// load-from-save. So one poll answers "is the world up" however the guest got there — no
/// per-mechanism special case, and nothing to walk.
///
/// WHY NOT `game+0x105`: it IS written on both paths, but only at the TERMINAL state, behind
/// the render fence and the loading-screen gates (states 4/5 of the save command, 7/8 of the
/// travel one). A Godot-hosted front end may never satisfy those, so it reads false while the
/// world is in fact fully loaded and its entities activated — the load command has finished its
/// work and parked. It also BOOTS AS 1 (game ctor, 0x82310644), so it is only meaningful once
/// it has been seen to go 0.
///
/// WHY NOT `LevelInstance + 0xE0`: it looks ideal — written 1 on both paths right after
/// activation — but it is a CONSUMED one-shot. `sub_8231D288` reads it at 0x8231D2BC and clears
/// it at 0x8231D3D0, conditionally on an entrance string being non-empty. Polling it races the
/// consumer and its lifetime depends on level data.
///
/// OFFSETS, EACH AGAINST ITS OWN BASE, and note this is NOT the object `world_manager()`
/// returns — that one takes `+0x08` then `+0x04` from the same parent, this takes `+0x00`:
///
///     g     = *(0x83496AB8)
///     game  = *(g    + 0x0C)
///     A     = *(game + 0x58)
///     B     = *(A    + 0x04)      /* what sub_82350F80 is called with */
///     World = *(B    + 0x00)      /* what it passes on as sub_823640E0's arg0 */
///     flag  =  (uint8)World[0xA8]
///
/// Every hop is null before `Game_InitDatabases` has run, so each is checked. Returns 0 when
/// the world is down, mid-load, or does not exist yet — all three mean the same thing to a
/// host: do not mount against it.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsActive(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    uint32_t at = u32(refii::guest::Address(0x83496AB8u));
    at = step(at, 0x0Cu);
    at = step(at, 0x58u);
    at = step(at, 0x04u);
    at = step(at, 0x00u);
    if (!plausible(at)) {
        return 0;
    }

    return GuestByteAt(base, at + 0xA8u) != 0 ? 1u : 0u;
}

/// Describes the gameflow command queue as text, for when a walk of it comes back empty.
///
/// PRINT THE QUEUE RATHER THAN GUESS AT IT AGAIN. When `RefiiHostGetLevelChange` finds nothing
/// the question is always the same — wrong chain, wrong link offset, or genuinely no such
/// command — and each answer costs a run to test. Dumping the nodes and their command vtables
/// settles all three at once, which is how the load-from-save path was found to queue no
/// level-change command at all.
///
/// Writes `<count> node(s): vtable@node ...` and never fails loudly — an empty or unreadable
/// queue is itself the answer.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDescribeGameFlowQueue(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }

    out[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    const uint32_t p = step(u32(refii::guest::Address(0x83496AB8u)), 0x0Cu);
    const uint32_t holder = step(p, 0x80u);
    const uint32_t gameflow = step(holder, 0x04u);

    std::string text;
    char scratch[128];

    std::snprintf(scratch, sizeof(scratch), "p=0x%08X holder=0x%08X gf=0x%08X", p, holder,
                  gameflow);
    text += scratch;

    if (plausible(gameflow)) {
        const uint32_t sentinel = u32(gameflow + 0x04u);
        std::snprintf(scratch, sizeof(scratch), " sentinel=0x%08X", sentinel);
        text += scratch;

        // BOTH LINK DIRECTIONS, because which one walks this ring is exactly what is in doubt.
        // A doubly linked node makes one of them the way forward and the other the way back;
        // printing both costs nothing and settles it in one run instead of one run per guess.
        for (int direction = 0; direction < 2; ++direction) {
            const uint32_t link = direction == 0 ? 0x04u : 0x00u;
            std::snprintf(scratch, sizeof(scratch), " | +%u:", link);
            text += scratch;

            uint32_t node = plausible(sentinel) ? u32(sentinel + link) : 0u;
            for (uint32_t i = 0; i < 8u; ++i) {
                if (!plausible(node) || node == sentinel) {
                    text += node == sentinel ? " <end>" : " <bad>";
                    break;
                }

                const uint32_t command = u32(node + 0x08u);
                std::snprintf(scratch, sizeof(scratch), " [%08X vt=%08X]", command,
                              plausible(command) ? u32(command) : 0u);
                text += scratch;

                if (text.size() + 64u >= capacity) {
                    break;
                }

                node = u32(node + link);
            }
        }
    }

    const size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
    std::memcpy(out, text.data(), copied);
    out[copied] = '\0';
    return 1;
}

/// Holds the guest in its own loading screen while Godot builds the region.
///
/// Set it when a build starts and CLEAR IT WHEN THE REGION MOUNTS — including on the failure
/// path. A hold that is never released parks the guest at state 7 forever with its loading
/// music playing, which looks exactly like a hung load because that is what it is.
///
/// SAFE TO HOLD FOR TENS OF SECONDS, checked rather than hoped: no state of the nine reads the
/// command's start timestamp `cmd+56` and none contains an elapsed-time comparison, an assert
/// or a force-advance; the 3D Engine thread's only cross-thread wait is a one-shot display-init
/// gate; and the guest starts one of five random loading tracks in state 1 and fades it out over
/// 1000 ms in state 9 — an arbitrarily long load is the designed case. Audio keeps running
/// because state 7 is a poll on a thread that keeps ticking and the mixer is not one of the
/// title's own threads.
REFII_HOST_ABI_EXPORT void RefiiHostSetWorldBuildPending(uint32_t pending) {
    g_world_build_pending.store(pending != 0u, std::memory_order_relaxed);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base != nullptr) {
        apply_world_build_hold(base);
    }
}

/// THE LEVEL CHANGE ITSELF, read off the command the guest queued for it.
///
/// `RefiiHostWorldIsReady` is a good "the world has settled" latch and a BAD "a load has
/// started" one, and the difference is where a host frees its scene. The entity teardown
/// happens in **state 2** — `sub_823D6798` @0x823D5F9C -> `sub_8231CB90` @0x823D6B1C ->
/// `sub_82364698`, which walks every entity, tests bit 0x08 of `entity+0x90` ("destroy on
/// level change"), calls DestroyEntity and then drains the pending list through
/// `sub_822D46C8` — after which the old entities are unlinked and freed. But `game+0x105` is
/// not cleared until **state 3**, at 0x823D60C8, AFTER the blocking load has already returned.
///
/// So there is a window spanning state 2 and all of state 3 where the ready byte still reads 1
/// and the entities it implies are gone. A host that frees on the falling edge of that byte
/// keeps drawing a region whose guest-side entities no longer exist, and reads freed memory to
/// do it. The command's own state word is the honest signal.
///
/// THE QUEUE WALK. Verified from `World_RequestLevelChange` (0x82350B20), which emits the whole
/// chain inline before it queues:
///
///     v19 = *(p + 0x80)                  /* p = *( *(0x83496AB8) + 0x0C ), same base as above */
///     gf  = *(v19 + 4)                   /* the object GameFlow_QueueCommand is called with */
///     sentinel = *(gf + 4)
///     node     = *(sentinel + 4)         /* empty when node == sentinel */
///     cmd      = *(node + 8)
///
/// NOTE THE BASE: the gameflow lives at `p + 0x80`, NOT at `*(0x83496AB8) + 0x80`. One extra
/// hop, and quoting the offset against the wrong base is how this project has shipped a
/// plausible-looking wrong answer before.
///
/// COMMAND FIELDS, from the ctor `sub_823D5620` decompiled in full rather than taken on report:
///
///     *(cmd + 0x00) = &vtbl_CmdLevelChange (0x820AECE8)   <- the identity test
///      cmd + 0x14   = world name     Rep*   /*0x823D5664*/
///      cmd + 0x18   = level name     Rep*   /*0x823D5670*/
///      cmd + 0x1C   = entrance name  Rep*   /*0x823D567C*/
///      cmd + 0x24   = travel type           /*0x823D56B0*/  1 new game, 2 door, 5 teleporter,
///                                                           3/4 fast travel, 16 reload in place
///      cmd + 0x2C   = state, starts at 1    /*0x823D56BC*/  1..9, never 0
///      cmd + 0x32   = fullLoad, starts at 1 /*0x823D56C4*/
///
/// `cmd+0x1C` is the entrance and not a guess: state 5 copies it straight into the live level
/// at 0x823D61EC — `sub_822651A0(*(u32*)(level+4) + 176, cmd + 28)` — and Level+176 is the slot
/// the level ctor seeds with "PlayerStart".
///
/// FULLLOAD IS LOAD-BEARING FOR THE HOST. When `cmd+0x32` is 0 the guest does NOT tear anything
/// down: state 3 takes the `sub_823170C8` branch instead of the blocking load, and
/// `sub_82364698` is not reachable from it at all. That is a reposition inside the RESIDENT
/// level, so a host must leave its scene standing — freeing it there would rebuild a region the
/// guest never unloaded.
///
/// We only ever read a vtable pointer and a few words, so this is polling, not participation.
/// Returns 1 when a level change is at the head of the queue, 0 otherwise.
///
/// Forward-declared because the shared string reader is defined with the other level accessors
/// further down, and this belongs beside `RefiiHostWorldIsReady` — the call it exists to correct.
static bool read_guest_name(const uint8_t* base, uint32_t global, char* out, uint32_t capacity);

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLevelChange(char* world, char* level, char* entrance, uint32_t capacity,
                                 uint32_t* out_state, uint32_t* out_travel_type,
                                 uint32_t* out_full_load) {
    if (out_state != nullptr) *out_state = 0u;
    if (out_travel_type != nullptr) *out_travel_type = 0u;
    if (out_full_load != nullptr) *out_full_load = 0u;

    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    const uint32_t mode = u32(refii::guest::Address(0x83496AB8u));
    const uint32_t p = step(mode, 0x0Cu);
    const uint32_t gameflow = step(step(p, 0x80u), 0x04u);
    if (!plausible(gameflow)) {
        return 0;
    }

    const uint32_t sentinel = u32(gameflow + 0x04u);
    if (!plausible(sentinel)) {
        return 0;
    }

    // WALK THE QUEUE — the level change is not always at the head.
    //
    // Checking only the first node reported "no command in flight" while a level change was
    // very much in flight, and the host then waited for a state that could never arrive.
    // `World_RequestLevelChange` itself queues a SECOND command in front of it: at 0x82350C94
    // it allocates a 24-byte object with vtable `off_820AECA0` and queues that (0x82350CC8)
    // before constructing and queueing the level change (0x82350CF8 / 0x82350D08). Whatever
    // sits at the head is therefore not the command we are looking for.
    //
    // The list is circular through the sentinel, so a node equal to it ends the walk. The step
    // cap is a corruption guard, not a queue-length assumption: a torn `next` pointer would
    // otherwise spin here forever, inside a call the host makes every frame.
    constexpr uint32_t kMaxQueueWalk = 64u;

    uint32_t command = 0;
    uint32_t node = u32(sentinel + 0x04u);
    for (uint32_t step_count = 0; step_count < kMaxQueueWalk; ++step_count) {
        if (!plausible(node) || node == sentinel) {
            break;
        }

        // ONLY CmdLevelChange. 0x820AED0C IS NOT A SECOND FLAVOUR OF IT.
        //
        // The queue dump shows a load-from-save carrying `[vt=820AED0C] [vt=8200247C]
        // [vt=82002914]` and no CmdLevelChange at all, and it is tempting to accept 0x820AED0C
        // too since it sits in the same contiguous run of command vtables. It is NOT the same
        // shape: `Game_InitDatabases` allocates that object with `li r3, 0xC` at 0x823114C8 —
        // **twelve bytes**, holding a vtable at +0, `sub_823DA258` at +4 and one word at +8.
        // Reading `+0x2C` for a state or `+0x14` for a destination off it is thirty bytes past
        // the end of the allocation, and would return whatever the allocator had there.
        //
        // An adjacent vtable is not evidence of a shared layout. The initial load simply has no
        // level-change command to find, and the caller's own fallbacks cover that.
        const uint32_t candidate = u32(node + 0x08u);
        if (plausible(candidate) && u32(candidate) == refii::guest::Address(0x820AECE8u)) {
            command = candidate;
            break;
        }

        // THROUGH +4, which is the link the guest itself follows. `GameFlow_QueueCommand`
        // reaches its node as `*(*(gf + 4) + 4)` and ends on `node == sentinel`, so +4 is the
        // pointer that walks this ring; +0 would be the other direction of a doubly-linked
        // node and is not what any reader in the image uses. The ring is circular either way,
        // so following the guest's own link visits every command.
        node = u32(node + 0x04u);
    }

    if (command == 0) {
        return 0;  // no level change anywhere in the queue
    }

    if (out_state != nullptr) *out_state = u32(command + 0x2Cu);
    if (out_travel_type != nullptr) *out_travel_type = u32(command + 0x24u);
    if (out_full_load != nullptr) *out_full_load = GuestByteAt(base, command + 0x32u) != 0 ? 1u : 0u;

    read_guest_name(base, command + 0x14u, world, capacity);
    read_guest_name(base, command + 0x1Cu, entrance, capacity);
    read_guest_name(base, command + 0x18u, level, capacity);
    return 1;
}

/// How far the guest has got through starting a game, as four globals it maintains itself.
///
/// `Game_InitDatabases` (0x823108B8) is where a New Game actually begins, and it is silent:
/// it neither logs nor sets any progress flag until its very last statement. These four
/// bracket it:
///
///   out[0] `g_GDBManager`   0x83496DA0 — the game-database manager. The front end's Leave
///          NULLS it (`GDBManager_UnloadFilesByTagAndRebuild` then `g_GDBManager = 0` at
///          0x82319BC8) and Game_InitDatabases rebuilds it. Zero after the handover means
///          the databases have been torn down and not yet come back.
///   out[1] `byte_83496A5E`  — set 1 at 0x82311648, the LAST statement of the function.
///   out[2] `dword_83496AB8` — the game mode, built by sub_823130D8.
///   out[3] `dword_83496AC0` — the front-end mode; its own destructor zeroes it at
///          0x8231AA54, so 0 here means the front end is genuinely gone rather than hidden.
///
/// Together they say which side of the handover we are on and whether the databases came
/// back — without which "the guest went quiet" cannot be told apart from "the guest is
/// loading".
REFII_HOST_ABI_EXPORT void RefiiHostGetInitMarkers(uint32_t* out) {
    if (out == nullptr) {
        return;
    }

    for (int i = 0; i < 4; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    out[0] = GuestWordAt(base, refii::guest::Address(0x83496DA0u));
    out[1] = GuestByteAt(base, refii::guest::Address(0x83496A5Eu));
    out[2] = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    out[3] = GuestWordAt(base, refii::guest::Address(0x83496AC0u));
}

/// Every LIVE entity's world transform and model, for Godot to draw.
///
/// This is the plan's phase 1 — the state feed — and it is a PURE MEMORY READ. No guest
/// function is called, nothing is written, and it is safe to run from Godot's thread while
/// the guest simulates: a torn read costs one frame of one entity, never a crash.
///
/// THE LIST. `0x83496B28` holds a POINTER to the manager, not the header — that is the first
/// trap, and reading it as the header walks garbage. The header is the 12 bytes at mgr+0x08:
/// front +0x08, back +0x0C, live count +0x10. The node is embedded at entity+0x08 as
/// {next, prev, owner}, so the owner back-pointer is node+0x08. Verified against the guest's
/// own walk, sub_8238D3A0: `lwz r10, dword_83496B28` / `lwz r11, 8(r10)` / `lwz r11, 8(r6)`.
///
/// WHICH ENTITIES COUNT. `entity+0x90 & 0x40` is the LIVE bit — set at construction
/// (sub_8238B320 stores 0xCC) and cleared only by the despawn sub_8238D828 — and it is the
/// game's own universal validity guard, tested at 3714 sites against one or two for every
/// other bit. Membership is not drawability: the renderable (component 3) carries SetAsDrawable
/// at +197 and an alpha pair at +112/+116, stated to Godot as the appearance words below — the
/// walk reports the entity either way. The second half of the filter is `entity+0x7C != 0`,
/// the same thing despawn gates its deregistration on.
///
/// THE TRANSFORM. entity+0x7C is the placement component ("EC-2"), whose slot 0x40 returns a
/// position and 0x44 an orientation, both sret — caller's buffer in r3, `this` in r4, 16 bytes
/// written (sub_8222CD80 is the whole idiom: `li r11,0x20; lvx128 v0,r4,r11; stvx128 v0,r0,r3`).
/// We do NOT call them. There are exactly twelve EC-2 vtables and between them only four
/// position bodies and three orientation bodies, so the read is a switch on the vtable
/// pointer — a `.rdata` constant, stable across runs.
///
/// An UNKNOWN vtable is reported, never guessed. Only three delegate classes were enumerated
/// and the writer of EC2+0x20 was never found, so the set cannot be proven closed, and
/// defaulting to some plausible offset would put entities at silently wrong positions.
/// Unknown means skipped and counted.
///
/// THE MODEL. Carried by a graphic-appearance component, not by the entity: id 4 static mesh
/// at +0x18, id 6 animated at +0x20, ids 10 and 11 at +0x14. The value is a resource-name
/// hash, written once at load. Id 5 (multiple-mesh) holds a vector and is skipped.
///
/// Fills `stride` dwords per entity; returns how many were written.
///
/// `out_unknown_vtables` receives {vtable, entity count} PAIRS and the number of pairs comes
/// back in `out_unknown_vtable_count`. The count is explicit rather than a zero terminator
/// because zero is a value the walk can legitimately record — a placement component whose
/// vtable word reads 0 is the under-construction case, and terminating on it buries every
/// later entry. The per-vtable tally is what makes the list actionable: one address standing
/// for 300 entities is a different decision from thirty standing for a dozen each.
/// EVERY RIG ANIMATES. The guest's per-frame animation loop `sub_8220D1B8` skips an entity
/// whose visibility predicate `sub_822142E8(comp3, radius)` fails, and that predicate is a
/// cone test against the guest's own 4:3 camera (`sub_82294468`, `g_WidescreenMode` = 0),
/// refreshed every fourth frame. Godot draws a wider view, so characters at its edges stood
/// in their init pose — the skeleton reference copied LOCAL at component init — and walked
/// about in it. The predicate's first line is `if (*(BYTE*)(comp3 + 202)) return 1;`
/// (0x82214308): the shipped `SetAsAlwaysInView` script property. Set on every live entity
/// that has the component; the guest then evaluates and converts each frame.
/// REFII_ALWAYS_IN_VIEW=0 leaves the guest's cull alone.
uint32_t g_always_in_view_set = 0;
static void KeepAnimatedInView(uint8_t* base, uint32_t entity) {
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_ALWAYS_IN_VIEW");
        return value == nullptr || std::string_view(value) != "0";
    }();
    if (!enabled) {
        return;
    }

    constexpr uint8_t kVisibilityId = 3u;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (((read32(entity + 0x24u) >> kVisibilityId) & 1u) == 0u) {
        return;
    }

    // `EntityComponent` checks the presence bitmask first and re-checks the id after the
    // accelerator hop, so a table being rebuilt on another thread cannot hand back a
    // different component.
    const uint32_t component = EntityComponent(base, entity, kVisibilityId);

    if (!plausible(component)) {
        return;
    }

    // The radius at +40 is a finite positive float on this component (ctor default 10000);
    // anything else is not the component and is left alone.
    const uint32_t radius_bits = read32(component + 40u);
    float radius;
    std::memcpy(&radius, &radius_bits, sizeof(radius));
    if (!(radius > 0.0f) || !std::isfinite(radius)) {
        return;
    }

    if (read8(component + 202u) != 0u) {
        return;
    }

    const uint8_t one = 1u;
    StoreGuest(base, component + 202u, &one, sizeof(one));
    ++g_always_in_view_set;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntities(uint32_t* out, uint32_t max_entities, uint32_t* out_unknown,
                              uint32_t* out_unknown_vtables, uint32_t max_unknown_vtables,
                              uint32_t* out_unknown_vtable_count) {
    static uint32_t walk_serial = 0u;
    ++walk_serial;
    // Words 0..11 identity and placement, 12..18 the guest's previous transform, 19 its
    // interpolation enable, 20 the highlight RGBA, 21..28 the stated appearance. See the row
    // write below.
    constexpr uint32_t kEntityStride = 21u + kAppearanceWords;
    constexpr uint16_t kNoDirect = 0xFFFFu;

    /// The twelve EC-2 vtables, and how each states its placement.
    /// `pos`/`ori` are byte offsets into the component; kNoDirect means "not a direct read".
    struct Ec2Layout {
        uint32_t vtable;
        uint16_t pos;
        uint16_t ori;
        bool hybrid;    // sub_82213A08: cached at +0x70 unless it defers to the physics body
        bool delegate;  // sub_8220DDB0: the placement lives on the object at +0x20
    };

    const Ec2Layout kEc2Layouts[] = {
    {refii::guest::Address(0x82006F88u), 0x20u, kNoDirect, false, false},  // orientation is a global
    {refii::guest::Address(0x82007258u), 0x20u, 0x30u, false, false},
    {refii::guest::Address(0x820E6048u), 0x20u, 0x30u, false, false},
    {refii::guest::Address(0x820CA190u), 0x40u, 0x30u, false, false},
    {refii::guest::Address(0x82007038u), 0x70u, kNoDirect, true, false},
    {refii::guest::Address(0x820071A0u), 0x70u, kNoDirect, true, false},
    {refii::guest::Address(0x820C4B90u), 0x70u, kNoDirect, true, false},
    {refii::guest::Address(0x820070F0u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x82007308u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x820073B8u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x820C6E58u), kNoDirect, kNoDirect, false, true},
    {refii::guest::Address(0x820CB140u), kNoDirect, kNoDirect, false, true},  // the abstract base
};

    /// The delegate targets — the physics/proxy objects an EC-2 hands its placement to.
    ///
    /// The EC-2 getters do NOT call these at slot 0x40/0x44. `sub_8220DDB0` loads the object
    /// from EC2+0x20 and dispatches slot 0x0C for position; `sub_8220B940` dispatches slot
    /// 0x10 for orientation. A delegate vtable read as if it answered at 0x40 lands in the
    /// middle of a real vtable and names no class at all.
    ///
    /// `hop` is a CHAIN, not a special case, because the guest's is: a getter that is not a
    /// plain load holds a pointer at some offset and asks THAT object instead, and the object
    /// it names can do the same again. `0x82006A40` hops through +0x38 and dispatches on what
    /// it finds; `0x82005B80` hops through +0x2C and reads the vectors straight out.
    ///
    /// `dispatch` says which kind: true means look the object at `hop` up in this table again,
    /// false means read `pos`/`ori` out of it directly.
    ///
    /// A NULL at a hop is not a failure — the getters fall back to `flt_82099484`, which is
    /// 0.0, so the object genuinely is at the origin.
    static constexpr uint16_t kNoHop = 0xFFFFu;

    struct DelegateLayout {
        uint32_t vtable;
        uint16_t pos;
        uint16_t ori;
        uint16_t hop;
        bool dispatch;
    };

    const DelegateLayout kDelegateLayouts[] = {
        // slot 0x0C = sub_8255AFE0 `li r11,0x50`, slot 0x10 = sub_828CA2A0 `ld 0x60/0x68`.
        {refii::guest::Address(0x82006B30u), 0x50u, 0x60u, kNoHop, false},
        // slot 0x0C = sub_8222A4B0 `li r11,0x40`, slot 0x10 = sub_822082F8 `ld 0x50/0x58`.
        // 90 of the 388 unplaceable entities in BWSSlums were this class.
        {refii::guest::Address(0x820069C8u), 0x40u, 0x50u, kNoHop, false},
        // slot 0x0C = sub_8220B9E0 `lwz r4,0x38(r4)` then slot 0x14; slot 0x10 = sub_82232B70,
        // same hop then slot 0x18. 298 of the 388 were this one.
        {refii::guest::Address(0x82006A40u), 0u, 0u, 0x38u, true},
        // slot 0x14 = sub_8257A608 `li r11,0xF0`, slot 0x18 = sub_8257A618 `ld 0x100/0x108`.
        // Reached from 0x82006A40's hop and from the hybrid's +0x38.
        {refii::guest::Address(0x82005460u), 0xF0u, 0x100u, kNoHop, false},
        // slot 0x14 = sub_8220B9C8 `lwz r11,0x2C(r4); li r10,0x110`, slot 0x18 = sub_82232B38,
        // same hop then +0x150. The other thing 0x82006A40's +0x38 can hold — and what all
        // 298 of them actually held.
        {refii::guest::Address(0x82005B80u), 0x110u, 0x150u, 0x2Cu, false},
    };

    /// The delegate layout for a vtable, or null when it is not one we know.
    const auto find_delegate = [&](uint32_t vtable) -> const DelegateLayout* {
        for (const DelegateLayout& candidate : kDelegateLayouts) {
            if (candidate.vtable == vtable) {
                return &candidate;
            }
        }
        return nullptr;
    };

    /// The graphic-appearance components, and where each keeps its model hash.
    struct ModelSlot {
        uint8_t id;
        uint16_t offset;
    };

    static constexpr ModelSlot kModelSlots[] = {
        {4u, 0x18u},   // static mesh
        {6u, 0x20u},   // animated mesh
        {10u, 0x14u},  // portal
        {11u, 0x14u},  // screen-facing sprite
    };

    // Cleared up front so every early return below leaves the caller with "nothing unknown"
    // rather than whatever the last call wrote.
    if (out_unknown != nullptr) {
        *out_unknown = 0u;
    }

    if (out_unknown_vtable_count != nullptr) {
        *out_unknown_vtable_count = 0u;
    }

    if (out == nullptr || max_entities == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    // Guest pointers are big-endian and the address space is 32-bit; anything outside it is a
    // torn read rather than an object, so every hop is bounds-checked.
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read_vector = [&](uint32_t at, uint32_t* dst, int lanes) {
        for (int i = 0; i < lanes; ++i) {
            dst[i] = read32(at + (uint32_t)(i * 4));
        }
    };

    /// Reads four floats and accepts them ONLY if they are a unit quaternion.
    ///
    /// A ROTATION QUATERNION IS UNIT BY DEFINITION, and that is the test — not "some lane is
    /// non-zero", which is what the orientation fallback used to ask. That question passes for
    /// any field that merely has bits in it, and the field at `placement+0xE0` is not always a
    /// quaternion: measured live in BWSMarket, every composite character came back
    /// `(0, 0, z, w)` with x and y EXACTLY zero and z/w astronomically large or zero —
    /// `(0, 0, -8.98e34, 0)`, `(0, 0, 3.23e25, -3.57e17)`, `(0, 0, 19169.12, -0)`. The leading
    /// pair of zeros is the giveaway: that is a 16-byte read of something whose first eight
    /// bytes are not part of a rotation at all.
    ///
    /// Downstream that garbage is invisible rather than loud. `|q|` of infinity or zero trips
    /// the host's degenerate guard and yields identity, while `(0,0,19169,0)` normalises to a
    /// clean 180° — so the characters simply stand in one fixed direction and nothing reports a
    /// fault. Rejecting here lets the NEXT source have its turn instead.
    const auto unit_quaternion_at = [&](uint32_t at, uint32_t* dst) -> bool {
        float q[4];
        for (int i = 0; i < 4; ++i) {
            const uint32_t bits = read32(at + (uint32_t)(i * 4));
            std::memcpy(&q[i], &bits, sizeof(q[i]));
            if (!std::isfinite(q[i])) {
                return false;
            }
        }

        const float length = (q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]);
        if (!(length > 0.98f && length < 1.02f)) {
            return false;
        }

        for (int i = 0; i < 4; ++i) {
            dst[i] = read32(at + (uint32_t)(i * 4));
        }

        return true;
    };

    /// Walks a delegate chain to the object that actually holds the vectors.
    ///
    /// Returns false when the chain reaches a class this table does not know, leaving the
    /// offending vtable in `unrecognised` so the caller can NAME it rather than guess an
    /// offset. `source` comes back 0 when the chain bottomed out on the guest's own null
    /// fallback — that is the origin, and a real answer.
    const auto follow_delegate = [&](uint32_t object, uint32_t& source, uint16_t& pos,
                                     uint16_t& ori, uint32_t& unrecognised) -> bool {
        // Bounded. The longest real chain is body -> +0x38 -> +0x2C; a cycle in guest memory
        // must not become a hang in a read that runs every frame.
        for (int depth = 0; depth < 4; ++depth) {
            const DelegateLayout* layout = find_delegate(read32(object));
            if (layout == nullptr) {
                unrecognised = read32(object);
                return false;
            }

            if (layout->hop == kNoHop) {
                source = object;
                pos = layout->pos;
                ori = layout->ori;
                return true;
            }

            const uint32_t next = read32(object + layout->hop);
            if (!plausible(next)) {
                source = 0u;  // the guest's own zero-vector fallback
                pos = kNoDirect;
                ori = kNoDirect;
                return true;
            }

            if (!layout->dispatch) {
                source = next;
                pos = layout->pos;
                ori = layout->ori;
                return true;
            }

            object = next;
        }

        unrecognised = read32(object);
        return false;
    };

    const uint32_t manager = GuestWordAt(base, refii::guest::Address(0x83496B28u));
    if (!plausible(manager)) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t unknown = 0u;
    uint32_t unknown_vtable_count = 0u;

    // Naming the vtables we do not know is the whole point of counting them: it turns "27% of
    // entities are unplaceable" into a short list of addresses to look up and add.
    //
    // `max_unknown_vtables` is a pair capacity, not a word capacity.
    const auto note_unknown = [&](uint32_t vtable) {
        ++unknown;
        if (out_unknown_vtables == nullptr) {
            return;
        }
        for (uint32_t i = 0; i < unknown_vtable_count; ++i) {
            if (out_unknown_vtables[i * 2u] == vtable) {
                ++out_unknown_vtables[(i * 2u) + 1u];
                return;  // distinct only — the pair carries the tally
            }
        }
        if (unknown_vtable_count < max_unknown_vtables) {
            out_unknown_vtables[unknown_vtable_count * 2u] = vtable;
            out_unknown_vtables[(unknown_vtable_count * 2u) + 1u] = 1u;
            ++unknown_vtable_count;
        }
    };

    uint32_t node = read32(manager + 8u);

    // The live count bounds the walk, so a corrupt link cannot spin forever.
    const uint32_t counted = read32(manager + 0x10u);
    const uint32_t guard = counted > 0u && counted < 65536u ? counted + 16u : 65536u;

    for (uint32_t steps = 0u; plausible(node) && written < max_entities && steps < guard;
         ++steps) {
        const uint32_t next = read32(node + 0u);
        const uint32_t entity = read32(node + 8u);
        node = next;

        if (!plausible(entity)) {
            break;
        }

        if ((read8(entity + 0x90u) & 0x40u) == 0u) {
            continue;  // not LIVE
        }

        // Every thirtieth walk: the byte sticks once set, and a spawn waits a second at most.
        if ((walk_serial % 30u) == 0u) {
            KeepAnimatedInView(base, entity);
        }

        const uint32_t placement = read32(entity + 0x7Cu);
        if (!plausible(placement)) {
            continue;  // no placement component == not drawable
        }

        const uint32_t vtable = read32(placement);
        const Ec2Layout* layout = nullptr;
        for (const Ec2Layout& candidate : kEc2Layouts) {
            if (candidate.vtable == vtable) {
                layout = &candidate;
                break;
            }
        }

        if (layout == nullptr) {
            note_unknown(vtable);  // loudly, not guessed
            continue;
        }

        uint32_t position[4] = {0u, 0u, 0u, 0u};
        uint32_t orientation[4] = {0u, 0u, 0u, 0x3F800000u};  // identity (x,y,z,w)
        bool resolved = false;

        uint32_t source = placement;
        uint16_t position_offset = layout->pos;
        uint16_t orientation_offset = layout->ori;

        /// A CHARACTER'S FACING, out of its Havok shape phantom.
        ///
        /// THIS IS WHY EVERY HUMAN IN THE STREET FACED THE SAME WAY. The character placement
        /// classes (`CECPhysicsSimulationCharacterControlled` 0x820071A0 and
        /// `...CharacterNavigator` 0x82007038) do not store an orientation. Their getter
        /// `sub_82211110` hops `comp+0x20` -> `+0x38` and dispatches slot 0x18 on what it
        /// finds, which is a proxy with vtable 0x82005720 — a class this table did not know, so
        /// the chain walk failed and every character got the identity fallback, every frame.
        ///
        /// Position hid the bug. `sub_82213A08` caches the position at `comp+0x70` and falls
        /// back to it, so positions were right and people walked around correctly; there is no
        /// equivalent cache for orientation, so a missed delegate is total.
        ///
        /// Verified in-line, not taken from a report — the proxy's two getters differ only in
        /// the offset they read out of the SAME object:
        ///
        ///   sub_82210F88 (slot 0x18, orientation): lwz r11, 0x2C(r4) / addi r4, r11, 0xA0
        ///   sub_82213A80 (slot 0x14, position):    lwz r11, 0x2C(r4) / li r10, 0xD0
        ///
        /// So `proxy+0x2C` is an `hkpShapePhantom` and its `hkTransform` starts at `+0xA0`:
        /// three column-major `float4` basis vectors (world X, Y, Z) then the translation at
        /// `+0xD0`. There is NO stored quaternion — the guest derives one per call.
        ///
        /// Offsets are measured from the PROXY (`controller+0x38`), not the placement component.
        /// A character's position, out of the same phantom — `sub_82213A80`, the proxy's
        /// slot 0x14:
        ///
        ///   lwz r11, 0x2C(r4) / li r10, 0xD0 / lvx128 v0, r11, r10 / stvx128 v0, r0, r3
        ///
        /// This is the translation lane of the same `hkTransform` whose basis the orientation
        /// read above takes from `+0xA0`.
        const auto read_phantom_position = [&](uint32_t proxy, uint32_t* out) -> bool {
            if (!plausible(proxy) || read32(proxy) != refii::guest::Address(0x82005720u)) {
                return false;
            }

            const uint32_t phantom = read32(proxy + 0x2Cu);
            if (!plausible(phantom)) {
                return false;
            }

            uint32_t lanes[3];
            for (int i = 0; i < 3; ++i) {
                lanes[i] = read32(phantom + 0xD0u + (uint32_t)(i * 4));
                float value;
                std::memcpy(&value, &lanes[i], sizeof(value));
                if (!std::isfinite(value)) {
                    return false;
                }
            }

            for (int i = 0; i < 3; ++i) {
                out[i] = lanes[i];
            }

            return true;
        };

        const auto read_phantom_quaternion = [&](uint32_t proxy, uint32_t* out) -> bool {
            if (!plausible(proxy) || read32(proxy) != refii::guest::Address(0x82005720u)) {
                return false;
            }

            const uint32_t phantom = read32(proxy + 0x2Cu);
            if (!plausible(phantom)) {
                return false;
            }

            // Column-major: element (row, col) is float [col * 4 + row].
            float m[12];
            for (int i = 0; i < 12; ++i) {
                const uint32_t bits = read32(phantom + 0xA0u + (uint32_t)(i * 4));
                std::memcpy(&m[i], &bits, sizeof(m[i]));
                if (!std::isfinite(m[i])) {
                    return false;
                }
            }

            const float m00 = m[0], m10 = m[1], m20 = m[2];
            const float m01 = m[4], m11 = m[5], m21 = m[6];
            const float m02 = m[8], m12 = m[9], m22 = m[10];

            // Shepperd's method — the largest-denominator branch, so it never divides by a
            // value near zero. The guest's own extraction (`quaternionFromRotatation`
            // @0x82D56728) takes `x = m21 - m12`, `y = m02 - m20`, `z = m10 - m01`, which is
            // this indexing exactly; it is only reproduced rather than called because any
            // correct extraction yields the same ROTATION, and q and -q are the same rotation.
            float x, y, z, w;
            const float trace = m00 + m11 + m22;
            if (trace > 0.0f) {
                const float s = std::sqrt(trace + 1.0f) * 2.0f;
                w = 0.25f * s;
                x = (m21 - m12) / s;
                y = (m02 - m20) / s;
                z = (m10 - m01) / s;
            } else if (m00 > m11 && m00 > m22) {
                const float s = std::sqrt(1.0f + m00 - m11 - m22) * 2.0f;
                w = (m21 - m12) / s;
                x = 0.25f * s;
                y = (m01 + m10) / s;
                z = (m02 + m20) / s;
            } else if (m11 > m22) {
                const float s = std::sqrt(1.0f + m11 - m00 - m22) * 2.0f;
                w = (m02 - m20) / s;
                x = (m01 + m10) / s;
                y = 0.25f * s;
                z = (m12 + m21) / s;
            } else {
                const float s = std::sqrt(1.0f + m22 - m00 - m11) * 2.0f;
                w = (m10 - m01) / s;
                x = (m02 + m20) / s;
                y = (m12 + m21) / s;
                z = 0.25f * s;
            }

            if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) ||
                !std::isfinite(w)) {
                return false;
            }

            // AND IT HAS TO BE UNIT. `isfinite` is not enough: a garbage basis is still made of
            // finite floats, and Shepperd's turns one into a finite non-rotation that then
            // silently becomes a bogus facing. The extraction of a real rotation matrix is unit
            // by construction, so this only rejects input that was never a rotation.
            const float length = (x * x) + (y * y) + (z * z) + (w * w);
            if (!(length > 0.98f && length < 1.02f)) {
                return false;
            }

            // HOST ORDER, LIKE EVERY OTHER LANE — this used to byte-swap "back to the guest's
            // byte order, because everything downstream byte-swaps", and nothing downstream
            // does. `read32` has already swapped on the way in, the array is initialised to a
            // host-order `0x3F800000`, and the C# side reads each word straight through
            // `BitConverter.Int32BitsToSingle`. The extra swap was the whole facing bug.
            //
            // It hid because of what a character's quaternion looks like. Facing is yaw-only
            // about Z, so the real value is `(0, 0, sin, cos)` — and `bswap(0) == 0`, so the two
            // zero lanes survived the corruption untouched. That is why every sample read
            // `(0, 0, junk, junk)` with x and y EXACTLY zero: not a wrong offset, not a missing
            // delegate, just the two lanes that a byte swap cannot damage sitting next to the
            // two it destroys. `0.7f = 0x3F333333` comes back as `0x3333333F`, about 4.2e7.
            const float lanes[4] = {x, y, z, w};
            for (int i = 0; i < 4; ++i) {
                std::memcpy(&out[i], &lanes[i], sizeof(out[i]));
            }

            return true;
        };

        if (layout->hybrid) {
            // The two halves gate DIFFERENTLY and must be asked separately.
            //
            // sub_82213A08 (position) defers to the sub-object only when the component has
            // NOT latched +0xA4; otherwise the cached vector at +0x70 wins. sub_82211110
            // (orientation) has no +0xA4 test at all — it delegates whenever the chain is
            // there, and falls back to identity, never to +0x70.
            //
            // Tying them to one branch returns identity for every latched entity, which looks
            // exactly like "the orientation offset is wrong" and is really the wrong gate.
            const uint32_t body = read32(placement + 0x20u);
            const uint32_t sub = plausible(body) ? read32(body + 0x38u) : 0u;

            uint32_t chained = 0u;
            uint16_t chained_pos = kNoDirect;
            uint16_t chained_ori = kNoDirect;
            uint32_t unrecognised = 0u;

            // THE CHARACTER PROXY FIRST. `sub` here is what `sub_82211110` dispatches slot 0x18
            // on, and for a character that is the Havok proxy — which holds a transform, not a
            // quaternion, so the generic delegate walk cannot express it and falls through. That
            // failure is silent and total: no orientation, identity forever, every human in the
            // street facing the same way while walking around perfectly correctly.
            if (read_phantom_quaternion(sub, orientation)) {
                orientation_offset = kNoDirect;

                // The position from the same phantom, on the guest's own gate.
                //
                // `sub_82213A08` reads `comp+0x70` only when `comp+0xA4` is non-zero or the
                // chain is missing; otherwise it dispatches the proxy's slot 0x14, the phantom
                // translation. `+0xA4`'s only writer is placement slot 35, from the network
                // paths (a replicated ghost); locally simulated characters read 0.
                //
                // `sub_82211F18`, the placement's per-tick update, writes `+0x70` on its
                // grounded branch only; once the physics component's byte at +104 is set it
                // takes the ballistic branch (`sub_82277B40` into
                // `hkpCharacterProxy::integrate`) and `+0x70` is no longer written.
                if (read8(placement + 0xA4u) == 0u && read_phantom_position(sub, position)) {
                    position_offset = kNoDirect;
                    resolved = true;
                }
            } else if (unit_quaternion_at(placement + 0xE0u, orientation)) {
                // THE COMPONENT'S OWN CACHE, second and only if it has ever been written.
                //
                // `sub_825989D8` (vtable +0x74, SetOrientation) stores the quaternion at
                // comp+0xE0 as well as pushing it to the physics proxy, so it is the guest's
                // own record of the facing. It is NOT per-frame — the constructor
                // `sub_825967B8` leaves it all zeros rather than identity, and only an explicit
                // set ever writes it — so it is a fallback, never the primary. All-zero is the
                // "never written" state and is not a rotation; taking it would be worse than
                // identity, hence the guard.
                read_vector(placement + 0xE0u, orientation, 4);
                orientation_offset = kNoDirect;
            } else if (plausible(sub) &&
                follow_delegate(sub, chained, chained_pos, chained_ori, unrecognised) &&
                chained != 0u && chained_ori != kNoDirect) {
                // Orientation always follows the chain when it exists — but still only if what
                // is there is a rotation. Same invariant as the two sources above; a chain that
                // resolves to the wrong field is no more usable than one that fails to resolve.
                if (unit_quaternion_at(chained + chained_ori, orientation)) {
                    orientation_offset = kNoDirect;
                }

                if (read8(placement + 0xA4u) == 0u) {
                    source = chained;
                    position_offset = chained_pos;
                }
            }
        } else if (layout->delegate) {
            const uint32_t body = read32(placement + 0x20u);
            if (!plausible(body)) {
                // A null body is the guest's own zero-vector/identity case, a real answer.
                resolved = true;
            } else {
                uint32_t chained = 0u;
                uint16_t chained_pos = kNoDirect;
                uint16_t chained_ori = kNoDirect;
                uint32_t unrecognised = 0u;

                if (!follow_delegate(body, chained, chained_pos, chained_ori, unrecognised)) {
                    note_unknown(unrecognised);  // named, never guessed
                    continue;
                }

                if (chained == 0u) {
                    resolved = true;  // the chain bottomed out at the origin
                } else {
                    source = chained;
                    position_offset = chained_pos;
                    orientation_offset = chained_ori;
                }
            }
        }

        if (!resolved && position_offset != kNoDirect) {
            read_vector(source + position_offset, position, 3);
            resolved = true;
        }

        if (!resolved) {
            note_unknown(vtable);
            continue;
        }

        // THE DIRECT READ, AND THE ONE THAT WAS STILL LETTING GARBAGE THROUGH.
        //
        // The three sources above now each check that what they found is a rotation, but this
        // path is taken by every layout that names an orientation offset outright — and it was
        // reading four floats and trusting them. Measured after the other three were guarded:
        // characters still arrived as `(0, 0, -3.43e21, 0)`, 142,800 of them refused downstream,
        // because THIS is where theirs came from.
        //
        // Leaving `orientation` at the identity it was initialised to is the honest outcome: a
        // field that does not hold a unit quaternion is not an orientation, whatever the layout
        // table says its offset is.
        if (orientation_offset != kNoDirect &&
            !unit_quaternion_at(source + orientation_offset, orientation)) {
            orientation[0] = 0u;
            orientation[1] = 0u;
            orientation[2] = 0u;
            orientation[3] = 0x3F800000u;  // identity (x,y,z,w), guest byte order
        }

        // The model hash, off whichever graphic-appearance component the entity carries.
        uint32_t model = 0u;
        for (const ModelSlot& slot : kModelSlots) {
            const uint32_t component = EntityComponent(base, entity, slot.id);
            if (plausible(component)) {
                model = read32(component + slot.offset);
                if (model != 0u) {
                    break;
                }
            }
        }

        uint32_t* row = out + (written * kEntityStride);
        row[0] = position[0];
        row[1] = position[1];
        row[2] = position[2];
        row[3] = orientation[0];
        row[4] = orientation[1];
        row[5] = orientation[2];
        row[6] = orientation[3];
        row[7] = model;
        row[8] = entity;
        row[9] = read32(entity + 0x14u);  // UniqueID, high
        row[10] = read32(entity + 0x18u); // UniqueID, low
        row[11] = read32(entity + 0x1Cu); // GDB record hash

        // The pair the guest itself interpolates.
        //
        // Component key 3 is the guest's renderable, and the first act of every game tick
        // latches its transform there (`sub_821C8FB0` -> `sub_82231A28` -> `sub_8222B2E0`):
        // position to `obj+0xA0`, the orientation quaternion to `obj+0xB0`. The tick then moves
        // everything, so the provider returns the new value and `obj+0xA0` holds the previous
        // one. `sub_822112D8` draws `prev*(1-alpha) + live*alpha`, slerping the rotation.
        //
        // `obj+0xC9` is an enable, not a suppress:
        //
        //     8221138c  lbz    r29, 0xC9(r31)
        //     82211394  cmplwi cr6, r29, 0
        //     822113a0  beq    cr6, loc_822113F4   ; zero -> skip the blend entirely
        //
        // Set to 1 in the constructor (`82571f60`) and cleared only by the Havok rigid-body
        // component's init (`82970a0c`): "interpolate unless physics owns this".
        //
        // Lanes are sent as `read32` returns them (already swapped); the C# side reads each word
        // through `Int32BitsToSingle`. Same id as `kVisibilityId`, which is function-scoped.
        constexpr uint8_t kRenderableId = 3u;
        const uint32_t renderable = EntityComponent(base, entity, kRenderableId);
        row[12] = 0u;
        row[13] = 0u;
        row[14] = 0u;
        row[15] = 0u;
        row[16] = 0u;
        row[17] = 0u;
        row[18] = 0x3F800000u;  // identity quaternion w
        row[19] = 0u;           // no renderable -> no pair -> do not interpolate
        row[20] = 0x000000FFu;  // no renderable -> the guest's OFF colour, {0,0,0,255}

        // The appearance, as constructed (`sub_82571DF8`) unless a setter or a load stated
        // otherwise: alpha, camera alpha and scale 1.0, tint 1,1,1,1, drawable, unmasked.
        uint32_t* appearance = row + 21;
        appearance[0] = appearance[1] = appearance[2] = 0x3F800000u;
        appearance[3] = appearance[4] = appearance[5] = appearance[6] = 0x3F800000u;
        appearance[kAppearanceFlags] = kAppearanceDrawable;
        if (plausible(renderable)) {
            row[12] = read32(renderable + 0xA0u);
            row[13] = read32(renderable + 0xA4u);
            row[14] = read32(renderable + 0xA8u);
            row[15] = read32(renderable + 0xB0u);
            row[16] = read32(renderable + 0xB4u);
            row[17] = read32(renderable + 0xB8u);
            row[18] = read32(renderable + 0xBCu);
            row[19] = GuestByteAt(base, renderable + 0xC9u) != 0u ? 1u : 0u;

            // The targeting highlight. The colour lives on the renderable (component 3) as RGBA
            // at +96..99, written by `sub_825738F0` and submitted as a shader constant by
            // `sub_82572C18` only when it differs from `dword_8331B158` = {0,0,0,255}. The off
            // state is that colour, not a flag: the clear path `sub_82429BB0` writes
            // `byte_83319B68` = {0,0,0,255} and `+144`'s 0x20 bit stays set after the first
            // write, so the host compares the bytes.
            //
            // The colour stated to the setter (hooked in `target_highlight.cpp`, past the
            // component-159 redirect) wins; the component bytes are the fallback, and are what
            // a component the setter has never named reads back as.
            const uint32_t on_component =
                (static_cast<uint32_t>(GuestByteAt(base, renderable + 0x60u)) << 24) |
                (static_cast<uint32_t>(GuestByteAt(base, renderable + 0x61u)) << 16) |
                (static_cast<uint32_t>(GuestByteAt(base, renderable + 0x62u)) << 8) |
                static_cast<uint32_t>(GuestByteAt(base, renderable + 0x63u));
            const uint32_t said = refii::vfs::StatedHighlightFor(renderable);
            row[20] = said != 0u ? said : on_component;

            // What the guest's appearance setters stated for this renderable — every death fade
            // (DeathType 5/6/7 ramp SetAlpha), the corpse tint, hidden carry slots, SetScale.
            refii::vfs::StatedAppearanceFor(renderable, appearance);
        }

        ++written;
    }

    if (out_unknown != nullptr) {
        *out_unknown = unknown;
    }

    if (out_unknown_vtable_count != nullptr) {
        *out_unknown_vtable_count = unknown_vtable_count;
    }

    return written;
}

/// The COMPONENTS one entity carries, as {id, pointer} pairs.
///
/// Which components an entity carries is a live-memory fact, and no decompile answers it. A
/// character whose component 6 holds `*(comp6 + 0x20) == 0` has no model hash there and can
/// only be drawn through the parts list on `CECGraphicAppearanceMorph` (component id 17) —
/// see RefiiHostGetEntityMorphParts — so which of the two applies has to be measured first.
///
/// This reads no offset INSIDE any component, only the list, so it depends on no layout
/// beyond the walk itself.
///
/// The walk is the entity's own component vector at `entity+0x48`..`entity+0x4C`, 8-byte
/// `{id, pointer}` pairs — the same walk `RefiiHostGetEntities` falls back to when the
/// accelerator at `entity+0x8C` is not plausible.
///
/// Writes `count * 2` words: id then pointer. Returns the number of PAIRS written, which is
/// capped at `max_pairs`; 0 means the entity has no readable component list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityComponents(uint32_t entity, uint32_t* out_pairs,
                                      uint32_t max_pairs) {
    if (out_pairs == nullptr || max_pairs == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    const uint32_t components = read32(entity + 0x48u);
    const uint32_t components_end = read32(entity + 0x4Cu);
    if (!plausible(components) || components_end <= components) {
        return 0u;
    }

    // Bounded against a torn read the same way the entity walk is: a component vector that
    // claims to be enormous is a bad pointer pair, not an entity with thousands of parts.
    const uint32_t span = components_end - components;
    if (span > 0x4000u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = components; at + 8u <= components_end && written < max_pairs;
         at += 8u) {
        out_pairs[written * 2u] = read32(at);
        out_pairs[(written * 2u) + 1u] = read32(at + 4u);
        ++written;
    }

    return written;
}

/// How many words RefiiHostGetEntityLocomotion writes.
#define REFII_LOCOMOTION_WORDS 7u

/// How many words RefiiHostGetEntityAnimState writes per clip leaf.
#define REFII_ANIM_LEAF_WORDS 12u

/// THE BYTES OF ONE BONE MASK NODE — one per LIVE skeleton bone, 0..255, the weight the
/// overlay applies to that bone (`sub_82B7E3F8` writes `int(w*255+0.5)`, the bool setter 255
/// or 0; `sub_822A57D0` divides by 255 into the pose buffer's row2.W). Node layout
/// (`sub_82761CE8`): +28 skeleton id, +36 begin, +40 end of the byte vector. Returns the byte
/// count written (0 for a node that is not a mask or whose vector is not readable) and, when
/// asked, the skeleton id the mask was built against.
/// WHAT ANY ENTITY WEARS — RefiiHostGetWornItems for an entity other than the hero. The hider
/// the guest sends its mesh instance (`sub_82229BE0`) is built at commit time from the GDB, not
/// stored: for every record of the appearance-modifier component (id 15, the same vector the
/// hero walk reads) it merges (`sub_824C13A8`) the item's `ClustersCovered` property — or
/// `OverrideFemaleClustersCovered` when the gender component (id 12) holds 1 at +12, or the
/// `...Open...` variants when byte +61 of the component is set and the item's
/// `BodyAreasCovered` carries 0x800 — into the set of body cluster names its draw-list builder
/// (`sub_82274CE0`) skips. The host takes the worn hashes from here and the cluster names from
/// its own copy of the GDB. Writes 2 words per item: the GDB hash and a flags word (bit 0
/// female, bit 1 the component's open byte). Returns the item count; 0 on a torn walk.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityWornItems(uint32_t entity, uint32_t* out_words,
                                                           uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 2u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t appearance = EntityComponent(base, entity, 15u);
    if (appearance == 0u) {
        return 0u;
    }
    uint32_t flags = 0u;
    const uint32_t gender = EntityComponent(base, entity, 12u);
    if (gender != 0u && GuestWordAt(base, gender + 12u) == 1u) {
        flags |= 1u;
    }
    if (GuestByteAt(base, appearance + 61u) != 0u) {
        flags |= 2u;
    }
    const uint32_t begin = GuestWordAt(base, appearance + 16u);
    const uint32_t end = GuestWordAt(base, appearance + 20u);
    if (!plausible(begin) || !plausible(end) || end <= begin) {
        return 0u;
    }
    constexpr uint32_t kStride = 24u;
    const uint32_t span = end - begin;
    if ((span % kStride) != 0u || (span / kStride) > 256u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written + 2u <= word_capacity; at += kStride) {
        const uint32_t item = GuestWordAt(base, at);
        if (!plausible(item)) {
            continue;
        }
        const uint32_t hash = GuestWordAt(base, item + 4u);
        if (hash == 0u) {
            continue;
        }
        out_words[written++] = hash;
        out_words[written++] = flags;
    }
    if (GuestWordAt(base, appearance + 16u) != begin || GuestWordAt(base, appearance + 20u) != end) {
        return 0u;  // a push reallocated mid-walk
    }
    return written / 2u;
}

/// THE POST-FOLD AIM AND LOOK. After evaluating any animation node (`sub_822DC980`, the tail
/// past the type switch) the guest rewrites two bone chains: when the skeleton's aim flag
/// (+57) is set and the node carries a record at +8, each of the 5 bones listed at
/// skeleton+60 is blended (`sub_822D9738`) from its folded rotation toward a per-bone target
/// quaternion in the record (16 bytes each from +16) by the record's weight (+4) in the
/// record's mode (+0); when the look flag (+58) is set and the node carries a record at +12,
/// the same for the 6 bones at skeleton+80. The targets are the guest's IK solve — the head
/// turning to the hero, the arm aiming a gun — stated per tick; the host takes them as
/// data and applies the same blend after its own fold. The skeleton object is comp6+148.
///
/// Out words: [0] aim flag, [1] look flag, [2..6] aim bones, [7..12] look bones (guest
/// indices), then per op: [record uid, kind (0 aim, 1 look), mode, weight bits, record weight
/// bits, then 4 words per bone (x y z w bits) for 5 or 6 bones]. Returns the op count; the
/// header is always written when the entity has a skeleton.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimPostOps(uint32_t entity, uint32_t* out_words,
                                                       uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 13u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read32 = [&](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&](uint32_t at) { return GuestByteAt(base, at); };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t mesh = EntityComponent(base, entity, 6u);
    const uint32_t controller = EntityComponent(base, entity, 19u);
    if (mesh == 0u || controller == 0u) {
        return 0u;
    }
    const uint32_t skeleton = read32(mesh + 148u);
    if (!plausible(skeleton)) {
        return 0u;
    }
    out_words[0] = read8(skeleton + 57u);
    out_words[1] = read8(skeleton + 58u);
    for (uint32_t i = 0; i < 5u; ++i) {
        out_words[2u + i] = read32(skeleton + 60u + 4u * i);
    }
    for (uint32_t i = 0; i < 6u; ++i) {
        out_words[7u + i] = read32(skeleton + 80u + 4u * i);
    }
    uint32_t written = 13u;
    uint32_t ops = 0u;
    uint32_t record_uid = 0u;
    uint32_t record_weight_bits = 0u;
    const auto emit = [&](uint32_t kind, uint32_t record) {
        const uint32_t bones = kind == 0u ? 5u : 6u;
        const uint32_t words = 5u + 4u * bones;
        if (!plausible(record) || written + words > word_capacity) {
            return;
        }
        out_words[written++] = record_uid;
        out_words[written++] = kind;
        out_words[written++] = read32(record);
        out_words[written++] = read32(record + 4u);
        out_words[written++] = record_weight_bits;
        for (uint32_t b = 0; b < bones; ++b) {
            for (uint32_t k = 0; k < 4u; ++k) {
                out_words[written++] = read32(record + 16u + 16u * b + 4u * k);
            }
        }
        ++ops;
    };
    // Every node of every playing record's tree, in the shape the leaf export walks.
    uint32_t stack[64];
    const auto walk_tree = [&](uint32_t root) {
        int depth = 0;
        if (plausible(root)) {
            stack[depth++] = root;
        }
        int steps = 0;
        while (depth > 0 && ++steps < 512) {
            const uint32_t node = stack[--depth];
            if (!plausible(node)) {
                continue;
            }
            if (out_words[0] != 0u) {
                emit(0u, read32(node + 8u));
            }
            if (out_words[1] != 0u) {
                emit(1u, read32(node + 12u));
            }
            const auto push = [&](uint32_t child) {
                if (depth < 64 && plausible(child)) {
                    stack[depth++] = child;
                }
            };
            switch (read32(node + 20u)) {
                case 1u:
                case 6u:
                    push(read32(node + 24u));
                    push(read32(node + 28u));
                    break;
                case 2u: {
                    const uint32_t begin = read32(node + 28u);
                    const uint32_t end = read32(node + 32u);
                    if (plausible(begin) && end >= begin && end - begin <= 4u * 64u) {
                        for (uint32_t at = begin; at + 4u <= end; at += 4u) {
                            push(read32(at));
                        }
                    }
                    break;
                }
                case 3u:
                    push(read32(node + 28u));
                    push(read32(node + 24u));
                    break;
                case 4u:
                    push(read32(node + 24u));
                    break;
                case 5u:
                    push(read32(node + 28u));
                    break;
                default:
                    break;
            }
        }
    };
    // The record lists at comp19+96 (primaries) and +108 (overlays): std::list nodes, record
    // at node+8, tree root at record+36, weight at +20, uid at +32, state 2 = finished.
    const auto walk_list = [&](uint32_t list) {
        const uint32_t sentinel = read32(list + 4u);
        const uint32_t size = read32(list + 8u);
        if (!plausible(sentinel) || size == 0u || size > 64u) {
            return;
        }
        uint32_t node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (!plausible(record) || read32(record + 28u) == 2u) {
                continue;
            }
            record_uid = read32(record + 32u);
            record_weight_bits = read32(record + 20u);
            walk_tree(read32(record + 36u));
        }
    };
    const uint32_t root = read32(controller + 156u);
    if (plausible(root)) {
        record_uid = 0u;
        record_weight_bits = 0x3F800000u;
        walk_tree(root);
    }
    walk_list(controller + 96u);
    walk_list(controller + 108u);
    return ops;
}

/// WHAT EACH BUILT PART HIDES. The composite builder (`sub_8220DE88`, 0x8220E828..E8C0)
/// hides, on every part, the union of the cluster sets of every OTHER part whose sort layer is
/// strictly greater — a coat over a shirt, never a coat over itself. Both live on the
/// GraphicAppearanceMorph component's built vector (id 17, +0x38..+0x3C, 24-byte records):
/// model hash +0, an MSVC `std::set<LHString>` at +4 (head +8, size +12; nodes left/parent/
/// right/value/colour/isnil, the value an LHString handle whose object holds chars +0 and
/// length +4), the `ClusterSortLayer` +16 and the body-part byte +20. The guest built the
/// sets from the GDB with the wearer's gender and open state already applied
/// (`sub_8217EDE0`), so the host takes them as stated rather than re-deriving them.
/// Writes one line per part into out_text: `hash layer body name;name;...` (hash hex,
/// layer signed decimal, body 0/1), newline-separated. Returns the part count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartHides(uint32_t entity, char* out_text,
                                                           uint32_t text_capacity) {
    if (out_text == nullptr || text_capacity < 2u) {
        return 0u;
    }
    out_text[0] = '\0';
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read32 = [&](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&](uint32_t at) { return GuestByteAt(base, at); };
    if (!plausible(entity)) {
        return 0u;
    }
    const uint32_t morph = EntityComponent(base, entity, 17u);
    if (morph == 0u) {
        return 0u;
    }
    const uint32_t begin = read32(morph + 0x38u);
    const uint32_t end = read32(morph + 0x3Cu);
    if (!plausible(begin) || end <= begin || ((end - begin) % 24u) != 0u || (end - begin) > 24u * 64u) {
        return 0u;
    }
    uint32_t used = 0u;
    const auto put = [&](const char* text, uint32_t length) -> bool {
        if (used + length + 1u >= text_capacity) {
            return false;
        }
        std::memcpy(out_text + used, text, length);
        used += length;
        out_text[used] = '\0';
        return true;
    };
    uint32_t parts = 0u;
    for (uint32_t record = begin; record + 24u <= end; record += 24u) {
        char head_text[48];
        const int head_length = std::snprintf(head_text, sizeof(head_text), "%08X %d %u", read32(record),
                                              static_cast<int32_t>(read32(record + 16u)),
                                              read8(record + 20u) != 0u ? 1u : 0u);
        if (head_length <= 0 || !put(head_text, static_cast<uint32_t>(head_length))) {
            break;
        }
        const uint32_t set_head = read32(record + 8u);
        const uint32_t set_size = read32(record + 12u);
        if (plausible(set_head) && set_size != 0u && set_size <= 64u) {
            uint32_t stack[64];
            uint32_t depth = 0u;
            uint32_t budget = 256u;
            const uint32_t root = read32(set_head + 4u);
            if (plausible(root) && root != set_head) {
                stack[depth++] = root;
            }
            bool first = true;
            while (depth != 0u && budget-- != 0u) {
                const uint32_t node = stack[--depth];
                if (!plausible(node) || node == set_head) {
                    continue;
                }
                if (read8(node + 17u) == 0u) {
                    const uint32_t handle = read32(node + 12u);
                    if (plausible(handle)) {
                        const uint32_t chars = read32(handle);
                        const uint32_t length = read32(handle + 4u);
                        if (plausible(chars) && length != 0u && length < 96u) {
                            char name[96];
                            uint32_t n = 0u;
                            for (; n < length; ++n) {
                                const uint8_t c = read8(chars + n);
                                if (c < 0x20u || c > 0x7Eu) {
                                    break;
                                }
                                name[n] = static_cast<char>(c);
                            }
                            if (n == length) {
                                put(first ? " " : ";", 1u);
                                put(name, n);
                                first = false;
                            }
                        }
                    }
                }
                const uint32_t left = read32(node);
                const uint32_t right = read32(node + 8u);
                if (depth + 2u <= 64u) {
                    if (plausible(left) && left != set_head) stack[depth++] = left;
                    if (plausible(right) && right != set_head) stack[depth++] = right;
                }
            }
        }
        put("\n", 1u);
        ++parts;
    }
    return parts;
}

/// THE LIGHTING ZONE an entity's geometry is drawn in: a light lights a draw only when either
/// zone is 0 or both are equal (`sub_82AB6360`, 0x82AB6428-0x82AB644C). A CECBuilding's own
/// static geometry is in the zone of its definition hash, `entity+0x1C & 0x7FFFFFFF`, when
/// `entity+0x90 & 0x40` (StaticMesh AddToScene `sub_822370A8`, 0x82237260-0x822372B4), else 1;
/// everything else is in its renderable's zone at `+0xD0`, which `sub_822D8AA8` keeps by
/// `LightZoneMode` (+0xCC): the building under it, the owner's building, 1, or 0 for all.
/// Returns 1 (the mesh constructor's zone, `sub_82AA2658` 0x82AA26D8) for an entity with no
/// renderable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLightZone(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 1u;
    }
    constexpr uint32_t kBuilding = 67u;
    if (((GuestWordAt(base, entity + 0x24u + (kBuilding / 32u) * 4u) >> (kBuilding % 32u)) & 1u) != 0u) {
        const uint32_t building = EntityComponent(base, entity, kBuilding);
        if (building != 0u && (GuestWordAt(base, building + 0x1Cu) & (0x200u | 0x002u)) != 0u) {
            return 1u;
        }
        return (GuestByteAt(base, entity + 0x90u) & 0x40u) != 0u
            ? GuestWordAt(base, entity + 0x1Cu) & 0x7FFFFFFFu
            : 1u;
    }
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    return renderable != 0u ? GuestWordAt(base, renderable + 0xD0u) : 1u;
}

/// THE STATED SHADOW CASTING. The renderable (component 3) holds its definition's flag word at
/// +0x8C (`sub_8257CB20`, `sub_828E7798`, `sub_8293AA70`: 0x40 = ShadowCaster off, 0x80 =
/// DontCastDistantShadows; `sub_825720A8`: 0x8000 EnableLightZoneShadowCulling) and
/// DisableBackFaceCullingForShadows at +0xC1 (`sub_825720A8` 0x825721CC), which the static-mesh
/// descriptor adds as 0x100 (`sub_822370A8`). The twin init turns the word into the cascade mask
/// (`sub_82AA3C78`: near unless 0x40, far unless 0x40 or 0x80) and the 0x100 into twin+0x110, which
/// the cascade pass draws with culling off (`sub_8221EE60` 0x8221EF10).
/// Returns the word with 0x100 for a two-sided caster, or 0xFFFFFFFF without the component.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityCasterFlags(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0xFFFFFFFFu;
    }
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    if (renderable == 0u) {
        return 0xFFFFFFFFu;
    }
    uint32_t flags = GuestWordAt(base, renderable + 0x8Cu) & ~0x100u;
    if (GuestByteAt(base, renderable + 0xC1u) != 0u) {
        flags |= 0x100u;
    }
    return flags;
}

/// THE STATED DRAW DISTANCE. The renderable (component 3) holds, from its GDB definition,
/// `MaxDrawDistance` times the class multiplier at +0x28 and `DrawDistanceFadeRangeFactor` at
/// +0x24 (`sub_82572438` @0x825724cc / @0x825724e0, the first call of the loader
/// `sub_825720A8`). The multipliers (`flt_83319464..70`) are script-settable, so the product is
/// read here, never recomputed. The draw gathers (`sub_82219AB8` animated, `sub_8221E5F0`
/// rigid) fade the instance out from D·(1 − factor) to D and cut its shadow hard at D
/// (0x82219b28: shadow pass, `d > obj+0x124` returns).
/// Returns 1 and writes both when the entity carries the component, else 0.
REFII_HOST_ABI_EXPORT int RefiiHostGetEntityDrawRange(uint32_t entity, float* out_distance,
                                                      float* out_fade_factor) {
    if (out_distance == nullptr || out_fade_factor == nullptr) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    const uint32_t renderable = EntityComponent(base, entity, 3u);
    if (renderable == 0u) {
        return 0;
    }
    const auto readf = [base](uint32_t at) -> float {
        const uint32_t raw = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };
    *out_fade_factor = readf(renderable + 0x24u);
    *out_distance = readf(renderable + 0x28u);
    return 1;
}

/// How many words RefiiHostGetBoneMorph writes per entry: bone, offset xyz, scale xyz.
#define REFII_BONE_MORPH_WORDS 7u

/// THE BODY'S PROPORTIONS. After the fold the guest runs `sub_822BF9B8(pose, skel, tier, rec)`
/// with the entity's combined skeletal morph record — the smart pointer at comp6+152 — whose
/// entries (48 bytes: int bone @0, float4 offset @16, float4 scale @32; vector at rec+20..+24,
/// bone count at rec+12 which `sub_822BF9B8` requires to equal the skeleton's parent table;
/// sorted by bone, only bones that differ from identity) add a translation offset and a scale
/// per bone: `t += off / parent.scale` (bone 1 with rec+36 > 0 scales x/y and adds off.z), then
/// `s.xyz *= sc`. The record is rebuilt by the GraphicAppearanceMorph component's commit
/// (`sub_825B9060`) from the GDB SkeletalMorphs weighted by the live strengths — fat, thin,
/// strength, skill, will, morality, age — so it changes as the character does; it starts as
/// the model's bind record (comp6+160) and is never null for a skinned model. The guest's
/// model-space compose carries no scale (`sub_821F57D0` composes quat and translation only),
/// so a bone's scale is ITS OWN, not inherited; `sub_821E5AC0` pre-multiplies each child's
/// translation by its parent's scale instead. Words per entry: [0] bone index in the guest
/// skeleton, [1..3] offset xyz bits, [4..6] scale xyz bits. Returns the entry count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorph(uint32_t entity, uint32_t* out_words,
                                                     uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < REFII_BONE_MORPH_WORDS) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xE0000000u;
    };
    const auto read32 = [&](uint32_t address) { return GuestWordAt(base, address); };
    if (!plausible(entity)) {
        return 0u;
    }
    // Component 6 by the entity's own lookup: presence bit, then the accelerator, then the
    // vector — the same walk RefiiHostGetEntityAnimState makes for component 19.
    constexpr uint32_t kMeshId = 6u;
    if (((read32(entity + 0x24u) >> kMeshId) & 1u) == 0u) {
        return 0u;
    }
    const uint32_t mesh = EntityComponent(base, entity, kMeshId);
    if (!plausible(mesh)) {
        return 0u;
    }
    const uint32_t record = read32(mesh + 152u);
    if (!plausible(record)) {
        return 0u;
    }
    const uint32_t entries = read32(record + 20u);
    const uint32_t entries_end = read32(record + 24u);
    if (!plausible(entries) || entries_end < entries || (entries_end - entries) > 48u * 512u) {
        return 0u;
    }
    const uint32_t max_entries = word_capacity / REFII_BONE_MORPH_WORDS;
    uint32_t written = 0u;
    for (uint32_t at = entries; at + 48u <= entries_end && written < max_entries; at += 48u) {
        uint32_t* out = out_words + (written * REFII_BONE_MORPH_WORDS);
        out[0] = read32(at + 0u);
        out[1] = read32(at + 16u);
        out[2] = read32(at + 20u);
        out[3] = read32(at + 24u);
        out[4] = read32(at + 32u);
        out[5] = read32(at + 36u);
        out[6] = read32(at + 40u);
        ++written;
    }
    return written;
}

/// The morph record's height factor `h` (rec+36, float bits), 0 when there is none. With
/// h > 0 the guest rewrites bone 1 as t = (t.x*h, t.y*h, t.z + off.z) and skips the parent-scale
/// divide (`sub_822BF9B8`, 0x822bfa4c..0x822bfa9c). Written by `sub_82B7D9B0` for a model with
/// a RetargetSkeletonFile, zero otherwise.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorphHeight(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr || entity < 0x10000u || entity >= 0xE0000000u) {
        return 0u;
    }
    constexpr uint32_t kMeshId = 6u;
    if (((GuestWordAt(base, entity + 0x24u) >> kMeshId) & 1u) == 0u) {
        return 0u;
    }
    const uint32_t mesh = EntityComponent(base, entity, kMeshId);
    if (mesh < 0x10000u || mesh >= 0xE0000000u) {
        return 0u;
    }
    const uint32_t record = GuestWordAt(base, mesh + 152u);
    if (record < 0x10000u || record >= 0xE0000000u) {
        return 0u;
    }
    return GuestWordAt(base, record + 36u);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimMask(uint32_t node, uint8_t* out_bytes, uint32_t capacity,
                                                    uint32_t* out_skeleton_id) {
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = 0u;
    }
    if (out_bytes == nullptr || capacity == 0u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xE0000000u;
    };
    if (!plausible(node) || GuestWordAt(base, node + 20u) != 4u) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, node + 36u);
    const uint32_t end = GuestWordAt(base, node + 40u);
    if (!plausible(begin) || end < begin || end - begin > 1024u) {
        return 0u;
    }
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = GuestWordAt(base, node + 28u);
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written < capacity; ++at) {
        out_bytes[written++] = GuestByteAt(base, at);
    }
    return written;
}

/// The guest's STATED animation for one rig — the inputs to its own blend tree, so the host
/// can play the clips itself and own animation the way it owns the UI.
///
/// The guest does not sample through Havok: component 19 (the animation controller) holds a
/// std::list of LAYER RECORDS at `comp19+96` (sentinel at
/// +100, size at +104; node{next +0, record +8}); a second list at +108/+112/+116 carries
/// masked overlay tracks. Every offset below was decompiled for this export:
///   record (`sub_821B7BA0`): +8 priority (128 = full override), +12 track, +16 mask,
///     +20 weight, +24 previous weight, +28 state (0 live, 1 finishing, 2 dead), +36 node
///   node (`sub_822DC980`): +16 weight, +20 type;
///     0 clip leaf: +24 clip hash (rex_lhResourceName hash of the lower-cased name, the
///       animation TOC's key), +28 previous time, +32 current time (seconds), +36 loop byte
///     1 crossfade: +24 A, +28 B, +32 t          6 crossfade with per-child alphas +36/+40
///     2 weighted set: vector +28..+32 of children, each weighted by child+16
///     3 additive: +28 base, +24 additive layer (x child+16)   5 multiply: +28 base, +24 mult
///     4 bone permute: +24 child
///   top record (`sub_822013A8`): highest priority whose state != 2.
/// The fold the guest applies (`sub_82211660`): a priority-128 record overrides everything;
/// otherwise every live record contributes by its weight. This export FLATTENS each record's
/// tree to its clip leaves with the product of the weights along the path, tagging additive
/// leaves, and leaves the blend to the host.
///
/// TWO LISTS, TWO ROLES (`sub_82211660`, `sub_82232BF8`, `sub_8220CB18`): list 1 at +96 holds
/// PRIMARIES — one wins by priority (ties: first in the list), and the guest crossfades it
/// against the highest-weighted other primary by the winner's own weight; every bone the
/// winner's clip lacks is the skeleton REST pose (`sub_822351B0`, skel+144). List 2 at +108
/// holds OVERLAYS, sorted by track then priority, each gated by `(record+16 & top.priority)`
/// (+16 is a PRIORITY BITMASK, not a bone mask) and composited PER BONE through the type-4
/// mask node inside its own tree: `wb = mask[bone]/255 * weight`, rotation slerp, translation
/// and scale lerp, a zero byte leaving the base bone untouched. Folding an overlay as a
/// primary snaps every bone its clip does not cover to rest — the villager walking in a T pose
/// while waving.
///
/// Out words, REFII_ANIM_LEAF_WORDS (12) per leaf: [0] clip hash, [1] current time bits,
/// [2] previous time bits, [3] weight bits, [4] flags (bit0 loop, bit1 additive, bit2 OVERLAY,
/// bits 8..15 record priority, bits 16..23 track), [5] record uid, [6] blend-in seconds
/// bits, [7] blend-out seconds bits (the record's ramp objects at +40/+48: `+4` is the step
/// per 15 Hz tick, 1/(seconds·15); 0 when absent), [8] the nearest mask node above the leaf
/// (0 = none; read it with RefiiHostGetAnimMask), [9] the record's priority bitmask,
/// [10] the record's weight_previous bits, [11] its weight_current bits (1.0 for the +156
/// tree, which is not a record).
/// Returns the leaf count (0 = no controller or nothing playing). `out_flags`, when given:
/// bit0 the guest's bone-count LOD tier (`comp19+148 == 3`: within the LOD radius and recently
/// visible), bit1 has component 97 (dog paw/look IK, a procedural layer), bit2 the constant
/// `comp19+576` byte (always 1, carries nothing; kept so the numbering holds), bit3 physics owns
/// the pose (ragdoll mode 3 or 4). The replicated tree at +156 IS walked: it is the root when
/// the guest's own choice (`sub_82208D70`) picks it over the layer lists.

/// Why a rig had no clip leaves, split by cause — see RefiiHostGetEntityAnimState.
std::atomic<uint64_t> RefiiAnimNoComponentBit{0};
std::atomic<uint64_t> RefiiAnimComponentUnresolved{0};
std::atomic<uint64_t> RefiiAnimControllerFound{0};
std::atomic<uint64_t> RefiiAnimEmptyList{0};

/// Whether the guest's physics owns this entity's bones this tick: the ragdoll object reached as
/// `sub_8220A700` reaches it (entity+0x7C -> slot 23 -> `sub_8220A610`), enabled, in mode 3 or 4.
///
/// Modes 3 and 4 only — the readback's own test, at both of its sites:
///     8220ab88  lwz r11, 0x20(r?) / cmpwi 4 / cmpwi 3      ; sub_8220A700 gates the copy
///     8257dc24  lwz r11, 0x20(r29); cmpwi cr6, r11, 4 / 3  ; sub_8257DBC0 gates it again
/// Mode 2 ("SetKeyframed") is the opposite direction: the branch at 0x822053A0 that takes 2
/// and 3 together pushes the animation into the bodies (`sub_8227D920` / `sub_828C8D18`) and
/// never writes a bone back.
///
/// The host cannot call a guest virtual; slot 23 is `sub_828D4110` = `lwz r3,0x20(r3); blr`,
/// so `*(P+0x20)` is the same read. The vtable test on the ragdoll (0x820067F8, from its
/// constructor) is stricter than the guest's slot-2 predicate and fails closed.
static bool EntityPhysicsOwnsPose(const uint8_t* base, uint32_t entity) {
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const uint32_t kRagdollVtable = refii::guest::Address(0x820067F8u);
    if (!plausible(entity)) {
        return false;
    }

    const uint32_t navigator = GuestWordAt(base, entity + 0x7Cu);
    const uint32_t proxy = plausible(navigator) ? GuestWordAt(base, navigator + 0x20u) : 0u;
    if (!plausible(proxy)) {
        return false;
    }

    const uint32_t begin = GuestWordAt(base, proxy + 8u);
    const uint32_t end = GuestWordAt(base, proxy + 12u);
    if (!plausible(begin) || end < begin || (end - begin) > 8u * 64u || ((end - begin) % 8u) != 0u) {
        return false;
    }

    for (uint32_t at = begin; at < end; at += 8u) {
        const uint32_t ragdoll = GuestWordAt(base, at);
        if (!plausible(ragdoll) || GuestWordAt(base, ragdoll) != kRagdollVtable) {
            continue;
        }

        const uint32_t mode = GuestWordAt(base, ragdoll + 0x20u);
        return GuestByteAt(base, ragdoll + 0x24u) != 0u && (mode == 3u || mode == 4u);
    }

    return false;
}

/// Rigs posed by a linked animation (component 191) rather than their own controller; run total.
std::atomic<uint64_t> RefiiAnimLinked{0};

/// THE CLIP A LINKED ANIMATION PLAYS — component 191, as `sub_8253B608` evaluates it — written as
/// one anim-state leaf (REFII_ANIM_LEAF_WORDS words), or 0 when the guest would pose nothing.
/// See the caller for the chain.
static uint32_t EntityLinkedAnimLeaf(const uint8_t* base, uint32_t entity, uint32_t* out) {
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto readf = [read32](uint32_t at) -> float {
        const uint32_t raw = read32(at);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const uint32_t linked = EntityComponent(base, entity, 191u);
    if (!plausible(linked)) {
        return 0u;
    }

    // `sub_82173E38(comp191+12)`: the control block at +16, whose first word is the entity.
    const uint32_t control = read32(linked + 16u);
    const uint32_t source = plausible(control) ? read32(control) : 0u;
    if (!plausible(source) || (GuestByteAt(base, source + 144u) & 0x40u) == 0u) {
        return 0u;
    }

    const uint32_t controller = EntityComponent(base, source, 19u);
    if (!plausible(controller)) {
        return 0u;
    }

    // `sub_82232070`: type 0 is the leaf; 1 takes +28 at t >= 0.5 else +24; 3 and 4 take +24;
    // anything else has no dominant leaf.
    const auto dominant = [&](uint32_t node) -> uint32_t {
        for (int depth = 0; depth < 32 && plausible(node); ++depth) {
            switch (read32(node + 20u)) {
                case 0u:
                    return node;
                case 1u:
                    node = read32(node + (readf(node + 32u) >= 0.5f ? 28u : 24u));
                    break;
                case 3u:
                case 4u:
                    node = read32(node + 24u);
                    break;
                default:
                    return 0u;
            }
        }
        return 0u;
    };

    uint32_t leaf = 0u;
    bool from_tree = false;
    float time = 0.0f;
    float previous = 0.0f;
    const uint32_t tree = read32(controller + 156u);
    if (plausible(tree)) {
        leaf = dominant(tree);
        if (leaf == 0u) {
            return 0u;
        }
        // `LHTiming_GetTime() - comp19+164 + leaf+32` (0x8253b7ec..0x8253b800): seconds since the
        // tree's receipt, run on from the leaf's current time.
        const auto read_double = [&](uint32_t at) {
            const uint64_t bits = (static_cast<uint64_t>(read32(at)) << 32) | read32(at + 4u);
            double value = 0.0;
            std::memcpy(&value, &bits, sizeof(value));
            return value;
        };
        const double time_base = read_double(refii::guest::Address(0x834970A0u));
        const double frequency = read_double(refii::guest::Address(0x834970A8u));
        if (!std::isfinite(frequency) || frequency <= 0.0 || !std::isfinite(time_base)) {
            return 0u;
        }
        const double now = (static_cast<double>(refii_bridge_rex_query_guest_tick_count()) - time_base) / frequency;
        const float current = readf(leaf + 32u);
        float step = current - readf(leaf + 28u);
        if (!(step > 0.0f) || !std::isfinite(step)) {
            step = 1.0f / 15.0f;
        }
        time = static_cast<float>(now - readf(controller + 164u)) + current;
        previous = time - step;
        from_tree = true;
    } else {
        // `sub_822013A8`: the live record (state +28 != 2) of highest priority (+8).
        const uint32_t sentinel = read32(controller + 100u);
        const uint32_t size = read32(controller + 104u);
        if (!plausible(sentinel) || size == 0u || size > 64u) {
            return 0u;
        }
        uint32_t winner = 0u;
        int32_t best = -1;
        uint32_t node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (!plausible(record) || read32(record + 28u) == 2u) {
                continue;
            }
            const int32_t priority = static_cast<int32_t>(read32(record + 8u));
            if (priority >= best) {
                best = priority;
                winner = record;
            }
        }
        leaf = winner != 0u ? dominant(read32(winner + 36u)) : 0u;
        if (leaf == 0u) {
            return 0u;
        }
        time = readf(leaf + 32u);
        previous = readf(leaf + 28u);
    }

    if (!std::isfinite(time) || !std::isfinite(previous)) {
        return 0u;
    }

    // comp191's map (`std::map` head at +28): lower_bound on node+12, then an exact match.
    const uint32_t source_key = read32(leaf + 24u);
    const uint32_t head = read32(linked + 28u);
    if (!plausible(head)) {
        return 0u;
    }
    uint32_t found = head;
    uint32_t at = read32(head + 4u);
    for (int steps = 0; steps < 64 && plausible(at) && GuestByteAt(base, at + 21u) == 0u; ++steps) {
        if (read32(at + 12u) >= source_key) {
            found = at;
            at = read32(at);
        } else {
            at = read32(at + 8u);
        }
    }
    if (found == head || source_key < read32(found + 12u)) {
        return 0u;
    }
    const uint32_t key = read32(found + 16u);
    if (key == 0u) {
        return 0u;
    }

    const float one = 1.0f;
    uint32_t one_bits;
    std::memcpy(&one_bits, &one, sizeof(one_bits));
    std::memcpy(&out[1], &time, sizeof(out[1]));
    std::memcpy(&out[2], &previous, sizeof(out[2]));
    out[0] = key;
    out[3] = one_bits;
    // Looping (`stb 1` into the stack node's +36), and flagged as a network extrapolation when the
    // time ran on from a receipt, so the host does not read a stalled pair as a cut.
    out[4] = 1u | (from_tree ? 8u : 0u);
    // The leaf's identity: the linked component, never a record uid of the rig.
    out[5] = linked;
    out[6] = 0u;
    out[7] = 0u;
    out[8] = 0u;
    out[9] = 0xFFFFFFFFu;
    out[10] = one_bits;
    out[11] = one_bits;
    return 1u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityAnimState(uint32_t entity, uint32_t* out_words, uint32_t word_capacity,
                                                            uint32_t* out_flags) {
    if (out_flags != nullptr) {
        *out_flags = 0u;
    }
    if (out_words == nullptr || word_capacity < REFII_ANIM_LEAF_WORDS) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    // The same bound `EntityComponent` enforces (below 0xC0000000), so the walk-stats split
    // agrees with the fetch.
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto readf = [read32](uint32_t at) -> float {
        const uint32_t raw = read32(at);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };
    if (!plausible(entity)) {
        return 0u;
    }
    const auto component = [&](uint32_t id) -> uint32_t {
        const uint32_t word = read32(entity + 0x24u + ((id / 32u) * 4u));
        if (((word >> (id % 32u)) & 1u) == 0u) {
            return 0u;
        }
        return EntityComponent(base, entity, id);
    };

    // Why a rig has no clips, counted by cause: no component-19 bit, component unresolved, or
    // controller found (with `RefiiAnimEmptyList` for an empty leaf list).
    const uint32_t present_word = read32(entity + 0x24u);
    if (((present_word >> 19u) & 1u) == 0u) {
        // A LINKED ANIMATION. With no component 19, `sub_8220D1B8` takes `beq loc_8220D98C`
        // (0x8220d904) and poses the rig from component 191 instead (entity+0x38 bit 31, id 0xBF
        // at 0x8220d9f4; `sub_8253B608` called at 0x8220da9c, its return is the pose).
        // `sub_8253B608`: the source entity is the weak ref at comp191+12/+16 (`sub_82173E38`:
        // `**(comp191+16)`), live only with entity+144 bit 0x40; its component 19's dominant leaf
        // is `sub_82231FD0(comp19+156)` when a tree is installed — time `leaf+32 +
        // (LHTiming_GetTime() - comp19+164)` — else `sub_82201518`: the winning record
        // (`sub_822013A8`) reduced by `sub_82232070` — time `leaf+32`. The leaf's key is looked up
        // in comp191's map (head at +28; node +12 key, +16 value, +21 nil) and the VALUE is the
        // clip played, one type-0 node at weight 1, looping, `tPrev = tCur = t`, into comp191+36.
        // Stated here as one leaf; the previous time is the source's own, so the host has the
        // rate.
        const uint32_t linked = EntityLinkedAnimLeaf(base, entity, out_words);
        if (linked != 0u) {
            if (out_flags != nullptr && EntityPhysicsOwnsPose(base, entity)) {
                *out_flags = 8u;
            }
            RefiiAnimLinked.fetch_add(1u, std::memory_order_relaxed);
            return linked;
        }
        RefiiAnimNoComponentBit.fetch_add(1u, std::memory_order_relaxed);
        return 0u;
    }

    const uint32_t controller = component(19u);
    if (!plausible(controller)) {
        RefiiAnimComponentUnresolved.fetch_add(1u, std::memory_order_relaxed);
        return 0u;
    }

    RefiiAnimControllerFound.fetch_add(1u, std::memory_order_relaxed);
    // FLAG 1 IS THE GUEST'S BONE-COUNT TIER, NOT AN ACTIVE FLAG. `comp19+148` is 1 or 3:
    // `sub_821F2688` sets 3 when the rig is within its LOD radius (`comp6+220`, 10 m by
    // default) and recently visible, else 1, and `sub_8220D1B8` (0x8220D684) then SKIPS the
    // guest's own pose update for a tier-1 non-hero rig — its list at +96 stays live and is
    // read here regardless. The 360 froze far rigs; we hold the clips and need not.
    uint32_t flags = 0u;
    if (read32(controller + 148u) == 3u) {
        flags |= 1u;
    }
    // Flag 4 carries no information and is kept only so the bit numbering does not move: the
    // constructor `sub_82562CC8` writes 1 to `comp19+576` on every controller
    // (`stb r25, 0x240(r31)`) and its only other accessors are a plain getter and setter.
    // Do not gate anything on it.
    if (read8(controller + 576u) != 0u) {
        flags |= 4u;
    }
    // FLAG 8: THE POSE IS PHYSICS'S, NOT THE LIST'S. Havok writes the finished pose into
    // comp6+44 and a host that keeps folding clips over it blows the body apart — an
    // electrocuted bandit coming to pieces.
    //
    // THIS DID NOT USE TO READ THE RAGDOLL AT ALL. It tested bit 2 of the spatial component's
    // word at comp3+144, and that was wrong twice over. First the width: `sub_82204FF0` loads
    // it with `lwz r11, 0x90(r10)` at 0x8220525C, a 32-BIT WORD, so on the big-endian guest
    // value-bit 2 lives at byte +147 and a raw byte read of +144 takes the MSB and can never
    // see it — measured, the fallback counter sat at exactly 0 over 444,813 clip-driven
    // frames. Second, and worse, that bit is not a physics flag: the branch at 0x82205268
    // skips the pose update when it is CLEAR, so it means "pose this skeleton this frame", an
    // ENABLE that is true for nearly every animated rig. Correcting only the width would have
    // handed every character to the pose feed and stopped all authored animation.
    //
    // The real state is the ragdoll object's mode, and only 3 and 4 hand the bones to physics
    // — see `EntityPhysicsOwnsPose` for the chain and the readback's own test.
    if (EntityPhysicsOwnsPose(base, entity)) {
        flags |= 8u;
    }
    if (out_flags != nullptr) {
        *out_flags = flags;
    }

    const uint32_t max_leaves = word_capacity / REFII_ANIM_LEAF_WORDS;
    uint32_t leaves = 0u;
    uint32_t ramp_in_bits = 0u;
    uint32_t ramp_out_bits = 0u;
    // The record's stated weight pair (+0x18 previous, +0x14 current), carried unscaled onto
    // every leaf of that record: the guest's crossfade factor uses the record's own values,
    // while the tree factors below scale the leaf's share.
    uint32_t record_prev_bits = 0u;
    uint32_t record_cur_bits = 0u;
    const auto ramp_seconds = [&](uint32_t ramp) -> uint32_t {
        if (!plausible(ramp)) {
            return 0u;
        }
        const float step = readf(ramp + 4u);
        float seconds = step > 1e-6f && std::isfinite(step) ? 1.0f / (step * 15.0f) : 0.0f;
        if (!(seconds > 0.0f) || seconds > 5.0f) {
            seconds = 0.0f;
        }
        uint32_t bits;
        std::memcpy(&bits, &seconds, sizeof(bits));
        return bits;
    };

    // Flatten one tree, depth-first, bounded against a torn read.
    struct Frame {
        uint32_t node;
        float weight;
        bool additive;
        uint32_t mask;  // the nearest type-4 mask node above this one, 0 when none
    };
    Frame stack[64];
    bool overlay_list = false;
    uint32_t priority_mask = 0u;
    // A REPLICA'S CLIP TIME IS A WALL-CLOCK EXTRAPOLATION, NOT THE NODE'S PAIR. The tree at
    // +156 arrives from the host (`sub_825657C8`, net messages 174/175) with each leaf's
    // `+28`/`+32` as the host's last graph step, and those words change only when the next
    // packet lands — every 100 ms for a nearby rig whose clips changed, 500 ms when they did
    // not. The guest's own sampler does not read them as a per-tick pair: `sub_82563B78`
    // (0x82563c04..0x82563c48) computes
    //     alpha = (((double)mftb - dbl_834970A0) / dbl_834970A8 - comp19+164) * 15.0
    // and samples `lerp(+28, +32, alpha)`, so the clip runs on in 1/15 s steps of the guest's
    // clock between packets (`+164` is the receipt time, `LHTiming_GetTime` at receipt). Read
    // as a pair, the leaf stood still for 100 ms and jumped — every rig but the local hero on
    // a client at ten frames a second. Stated here as the guest samples it: the current time
    // at alpha and the previous one a whole alpha step back. The animator folds a pair once
    // per GAME tick (`Engine_TickClock.GuestGameTick`, 15 Hz, `dword_83319650`) and alpha
    // advances by exactly 1.0 per game tick, so the previous value must be one graph step
    // back for consecutive folds to join; half a step read as a re-time every tick and cut
    // the timeline on each (run 2026-10-07 20:31: the joiner's town slid without animating).
    // A looping pair that wrapped (`+32 < +28`) keeps the guest's own step, 1/15 s.
    bool net_extrapolate = false;
    float net_alpha = 0.0f;
    const auto flatten = [&](uint32_t root, float weight, uint32_t priority, uint32_t track, uint32_t uid) {
        int depth = 0;
        stack[depth++] = Frame{root, weight, false, 0u};
        int steps = 0;
        while (depth > 0 && leaves < max_leaves && ++steps < 512) {
            const Frame frame = stack[--depth];
            if (!plausible(frame.node)) {
                continue;
            }
            const uint32_t type = read32(frame.node + 20u);
            const auto push = [&](uint32_t child, float w, bool add, uint32_t mask) {
                if (depth < 64 && plausible(child)) {
                    stack[depth++] = Frame{child, w, add, mask};
                }
            };
            switch (type) {
                case 0u: {
                    uint32_t* out = out_words + (leaves * REFII_ANIM_LEAF_WORDS);
                    out[0] = read32(frame.node + 24u);
                    out[1] = read32(frame.node + 32u);
                    out[2] = read32(frame.node + 28u);
                    if (net_extrapolate) {
                        const float prev = readf(frame.node + 28u);
                        const float cur = readf(frame.node + 32u);
                        float step = cur - prev;
                        if (!(step > 0.0f) || !std::isfinite(step)) {
                            step = 1.0f / 15.0f;
                        }
                        const float sampled = cur + step * (net_alpha - 1.0f);
                        const float before = sampled - step;
                        if (std::isfinite(sampled) && std::isfinite(before)) {
                            std::memcpy(&out[1], &sampled, sizeof(out[1]));
                            std::memcpy(&out[2], &before, sizeof(out[2]));
                        }
                    }
                    uint32_t weight_bits;
                    std::memcpy(&weight_bits, &frame.weight, sizeof(weight_bits));
                    out[3] = weight_bits;
                    // Bit 3: the times are the packet-alpha extrapolation above, whose
                    // continuity tick to tick is NOT stated (the receipt is an arrival time, and
                    // this read lands anywhere in the frame); the animator keeps its own timeline
                    // for such a clip and converges on these values.
                    out[4] = (read8(frame.node + 36u) != 0u ? 1u : 0u) | (frame.additive ? 2u : 0u) |
                             (overlay_list ? 4u : 0u) | (net_extrapolate ? 8u : 0u) |
                             ((priority & 0xFFu) << 8) | ((track & 0xFFu) << 16);
                    out[5] = uid;
                    out[6] = ramp_in_bits;
                    out[7] = ramp_out_bits;
                    out[8] = frame.mask;
                    out[9] = priority_mask;
                    out[10] = record_prev_bits;
                    out[11] = record_cur_bits;
                    ++leaves;
                    break;
                }
                case 1u:
                case 6u: {
                    const float t = readf(frame.node + 32u);
                    push(read32(frame.node + 24u), frame.weight * (1.0f - t), frame.additive, frame.mask);
                    push(read32(frame.node + 28u), frame.weight * t, frame.additive, frame.mask);
                    break;
                }
                case 2u: {
                    const uint32_t begin = read32(frame.node + 28u);
                    const uint32_t end = read32(frame.node + 32u);
                    if (!plausible(begin) || end < begin || end - begin > 4u * 64u) {
                        break;
                    }
                    float total = 0.0f;
                    for (uint32_t at = begin; at + 4u <= end; at += 4u) {
                        const uint32_t child = read32(at);
                        if (plausible(child)) {
                            total += readf(child + 16u);
                        }
                    }
                    for (uint32_t at = begin; at + 4u <= end; at += 4u) {
                        const uint32_t child = read32(at);
                        if (plausible(child)) {
                            const float w = readf(child + 16u);
                            push(child, total > 1e-6f ? frame.weight * (w / total) : 0.0f, frame.additive, frame.mask);
                        }
                    }
                    break;
                }
                case 3u: {
                    push(read32(frame.node + 28u), frame.weight, frame.additive, frame.mask);
                    const uint32_t layer = read32(frame.node + 24u);
                    if (plausible(layer)) {
                        push(layer, frame.weight * readf(layer + 16u), true, frame.mask);
                    }
                    break;
                }
                case 4u:
                    // THE MASK NODE (`sub_82761CE8`): +24 child, +28 skeleton id, +36..+40 one
                    // byte per live bone. Its child's leaves carry this node so the host can
                    // fetch the bytes (RefiiHostGetAnimMask) and composite per bone.
                    push(read32(frame.node + 24u), frame.weight, frame.additive, frame.node);
                    break;
                case 5u:
                    push(read32(frame.node + 28u), frame.weight, frame.additive, frame.mask);
                    break;
                default:
                    break;
            }
        }
    };

    const auto walk_list = [&](uint32_t list, bool overlay) {
        const uint32_t sentinel = read32(list + 4u);
        const uint32_t size = read32(list + 8u);
        if (!plausible(sentinel) || size == 0u || size > 64u) {
            return;
        }
        // One primary wins: the guest's own pick.
        //
        // `sub_822013A8` walks this list, skips records whose state (+28) is 2, and keeps the
        // highest priority (+8) with a strict `>` (0x82201464), returning that single record.
        // It walks backward (`lwz r8, 4(r10)` at 0x82201434 takes _Prev), so the record nearest
        // the tail wins a tie; `>=` on a forward walk is the same pick. `sub_82211660` folds only
        // that record (`sub_82229960(a2, &out, v7, alpha)`), or installs its tree outright when
        // its priority is 128.
        //
        // The runner-up is also exported: the guest crossfades the winner against it by the
        // winner's weight.
        uint32_t winner = 0u;
        int32_t winner_priority = -1;
        uint32_t runner_up = 0u;
        float runner_up_weight = 0.0f;
        uint32_t node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (!plausible(record) || read32(record + 28u) == 2u) {
                continue;
            }

            const int32_t priority = static_cast<int32_t>(read32(record + 8u));
            if (priority >= winner_priority) {
                winner_priority = priority;
                winner = record;
            }
        }

        // The runner-up must clear 1e-4 to exist at all (0x82238070..0x82238074); below that
        // `sub_82237F90` emits nothing and the winner is used alone.
        // Optimization: if size <= 1, no second record exists in the list so skip the walk.
        if (!overlay && winner != 0u && size > 1u) {
            node = read32(sentinel);
            for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
                const uint32_t record = read32(node + 8u);
                node = read32(node);
                // No state filter here, matching the guest: the runner-up scan `sub_82237F90`
                // (0x82237FD8..0x82238048) excludes only the winner's node and never tests +0x1C.
                // It ranks by +0x14 alone, forward with a strict `>`, so the first record wins
                // equal weights.
                if (!plausible(record) || record == winner) {
                    continue;
                }

                const float other = readf(record + 20u);
                if (other > runner_up_weight) {
                    runner_up_weight = other;
                    runner_up = record;
                }
            }

            if (runner_up_weight <= 1e-4f) {
                runner_up = 0u;
            }
        }

        node = read32(sentinel);
        for (uint32_t i = 0u; i < size && plausible(node) && node != sentinel; ++i) {
            const uint32_t record = read32(node + 8u);
            node = read32(node);
            if (!plausible(record)) {
                continue;
            }
            if (!overlay && record != winner && record != runner_up) {
                continue;
            }
            const uint32_t state = read32(record + 28u);
            if (state == 2u && (!overlay ? record != runner_up : false)) {
                continue;
            }
            const float weight = readf(record + 20u);
            if (weight <= 1e-4f) {
                if (!overlay && record == winner) {
                    // Retain winner even if cur weight is 0.0f on its opening tick,
                    // so the host registers the channel and folds runner_up -> winner.
                } else {
                    continue;
                }
            }
            record_prev_bits = read32(record + 24u);
            record_cur_bits = read32(record + 20u);
            ramp_in_bits = ramp_seconds(read32(record + 40u));
            ramp_out_bits = ramp_seconds(read32(record + 48u));
            overlay_list = overlay;
            priority_mask = read32(record + 16u);

            uint32_t exported_prio = read32(record + 8u);
            if (!overlay && record == runner_up && winner != 0u) {
                // Ensure runner_up priority never exceeds winner's priority, so the host Fold()
                // always elects winner as topUid and runner_up as runnerUid.
                if (exported_prio >= static_cast<uint32_t>(winner_priority) && winner_priority > 0) {
                    exported_prio = static_cast<uint32_t>(winner_priority - 1);
                }
            }
            flatten(read32(record + 36u), weight <= 1e-4f ? 1e-4f : weight, exported_prio, read32(record + 12u), read32(record + 32u));
        }
    };

    // THE GUEST'S OWN CHOICE OF ROOT (`sub_82208D70` 0x82208DB4): the tree at +156 plays when
    // it exists and either the layer list is empty or its highest priority is below 128;
    // otherwise the layer lists play. Same node types either way.
    const uint32_t root = read32(controller + 156u);
    const bool list_empty = read32(controller + 104u) == 0u;
    const bool root_wins = plausible(root) &&
                           (list_empty || static_cast<int32_t>(read32(controller + 604u)) < 128);
    if (root_wins) {
        ramp_in_bits = 0u;
        ramp_out_bits = 0u;
        overlay_list = false;
        priority_mask = 0xFFFFFFFFu;
        // The +156 tree is not a record and has no weight pair; it plays whole.
        record_prev_bits = 0x3F800000u;
        record_cur_bits = 0x3F800000u;
        // The guest's own alpha for this tree (see the note above `flatten`). `mftb` in
        // recompiled code is the payload's tick count, refii_bridge_rex_query_guest_tick_count —
        // the VIRTUAL tick under the driven time base, never the SDK's real
        // `Clock::QueryGuestTickCount`, which runs ahead by however far the world lags real
        // time (17 s on a joiner, 2026-10-20: every replica rig stated clip times far ahead and
        // jumping, so bodies slid under poses held on random frames). The two doubles are
        // LHTiming's base and frequency, stored high word first. A packet landing mid-walk re-bases +164 and
        // swaps the tree, so a walk whose receipt moved is done once more with the new one.
        const auto read_double = [&](uint32_t at) {
            const uint64_t bits = (static_cast<uint64_t>(read32(at)) << 32) | read32(at + 4u);
            double out = 0.0;
            std::memcpy(&out, &bits, sizeof(out));
            return out;
        };
        const double time_base = read_double(refii::guest::Address(0x834970A0u));
        const double frequency = read_double(refii::guest::Address(0x834970A8u));
        for (int attempt = 0; attempt < 2; ++attempt) {
            leaves = 0u;
            net_extrapolate = false;
            const float receipt = readf(controller + 164u);
            if (std::isfinite(frequency) && frequency > 0.0 && std::isfinite(time_base) &&
                std::isfinite(receipt)) {
                const double now = static_cast<double>(refii_bridge_rex_query_guest_tick_count());
                const double alpha = ((now - time_base) / frequency - receipt) * 15.0;
                // Bounded against a stale receipt: a minute of extrapolation is a tree the
                // host stopped sending, not a clip to run on.
                if (std::isfinite(alpha) && alpha > -1.0 && alpha < 15.0 * 60.0) {
                    net_extrapolate = true;
                    net_alpha = static_cast<float>(alpha);
                }
            }
            flatten(read32(controller + 156u), 1.0f, 0u, 0u, 0u);
            if (readf(controller + 164u) == receipt) {
                break;
            }
        }
        // The overlay list's records are LOCAL per-tick pairs; only the +156 tree is sampled
        // by the packet alpha.
        net_extrapolate = false;
        walk_list(controller + 108u, true);
        return leaves;
    }

    walk_list(controller + 96u, false);
    walk_list(controller + 108u, true);
    if (leaves == 0u) {
        RefiiAnimEmptyList.fetch_add(1u, std::memory_order_relaxed);
    }

    return leaves;
}

/// Why rigs are not clip-driven, split by cause. Four counters: the entity has no component-19
/// bit (and no linked animation), the bit is set but the component does not resolve, the
/// controller was found, and the controller was found but its lists flattened to nothing; a
/// fifth, when the capacity allows, counts rigs posed by a linked animation (component 191).
///
/// Separates "the guest has nothing to play" from "this walk misses the controller".
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimWalkStats(uint32_t* out_words, uint32_t capacity) {
    if (out_words == nullptr || capacity < 4u) {
        return 0u;
    }

    out_words[0] = static_cast<uint32_t>(RefiiAnimNoComponentBit.load(std::memory_order_relaxed));
    out_words[1] = static_cast<uint32_t>(RefiiAnimComponentUnresolved.load(std::memory_order_relaxed));
    out_words[2] = static_cast<uint32_t>(RefiiAnimControllerFound.load(std::memory_order_relaxed));
    out_words[3] = static_cast<uint32_t>(RefiiAnimEmptyList.load(std::memory_order_relaxed));
    if (capacity < 5u) {
        return 4u;
    }
    // A fifth word for a caller that asks: rigs a linked animation (component 191) posed.
    out_words[4] = static_cast<uint32_t>(RefiiAnimLinked.load(std::memory_order_relaxed));
    return 5u;
}

/// How many words RefiiHostGetAttachments writes per attachment record.
#define REFII_ATTACHMENT_WORDS 4u

/// How many bytes of dummy name it writes per record.
#define REFII_ATTACHMENT_NAME 64u

/// EVERY LIVE ATTACHMENT THE GUEST HAS STATED — which entity hangs on which entity's dummy.
///
/// A weapon, a hat carried in the hand, a prop: each is an ENTITY of its own, and the guest
/// re-derives its world placement every frame the parent moves (the attachment pass
/// `sub_822025E0`, after the animation pass in `sub_82278AE0`; composed by `sub_8250F5F0`,
/// written into the child's placement through vt+0x84 or vt+0x70/+0x74). That placement is
/// computed from the GUEST's bone pose. Godot now
/// plays the animation itself, so a child placed from the guest's pose swims against the body
/// it is supposed to be attached to — the weapons "bounce around and do not match the
/// character". The host has to hang the child off ITS OWN bone instead, and for that it needs
/// the guest's STATEMENT of the attachment rather than its result.
///
/// The statement lives on the PARENT, component 22 `CECObjectAttachment` (registered by
/// `sub_8265B680`, made by `sub_82649AA8`; records built by `sub_8250DF18` on the attach path
/// `sub_8250E9F8`):
///     att+4          the owner entity
///     att+16         the list sentinel; att+20 the count. Nodes are {next +0, record +8}.
///     record+4       handle slot; `*slot` is the CHILD entity
///     record+8       LHString dummy name on the PARENT, e.g. `Character.Carry.SheathWeaponBack`
///     record+12      dummy index — the ordinal among equally-named dummies
///     record+144     ticket, the id `CECDependantEntity` on the child joins back with
///
/// The dummy is a record in the parent model's own `.mdl.gmd` attachment table, which Godot
/// already parses; the child's own offset (record+0x50) comes from RefiiHostGetAttachmentGrips.
///
/// Words per record: [0] child entity, [1] parent entity, [2] dummy index, [3] ticket.
/// `out_names` takes REFII_ATTACHMENT_NAME bytes per record, NUL-padded. Returns the record
/// count, capped at `max_records`.
/// The attachment walk the attachment exports share: every live entity's component 22 records,
/// gated as the guest gates its own update, visited as (parent, record, child, slot) where slot
/// counts the visits so far. Stops at `max_records`. Returns the count visited.
extern "C++" {
template <typename Visit>
static uint32_t ForEachStatedAttachment(uint8_t* base, uint32_t max_records, Visit&& visit) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t manager = GuestWordAt(base, refii::guest::Address(0x83496B28u));
    if (!plausible(manager)) {
        return 0u;
    }

    /// The entity's component by id — the accelerator byte table at +0x8C when it is there,
    /// the sorted {id, pointer} pairs at +0x48..+0x4C otherwise. Same walk the entity feed uses.
    const auto component = [&](uint32_t entity, uint32_t id) -> uint32_t {
        const uint32_t word = read32(entity + 0x24u + ((id / 32u) * 4u));
        if (((word >> (id % 32u)) & 1u) == 0u) {
            return 0u;
        }
        return EntityComponent(base, entity, id);
    };

    uint32_t written = 0u;
    uint32_t node = read32(manager + 8u);
    const uint32_t counted = read32(manager + 0x10u);
    const uint32_t guard = counted > 0u && counted < 65536u ? counted + 16u : 65536u;

    for (uint32_t steps = 0u; plausible(node) && written < max_records && steps < guard;
         ++steps) {
        const uint32_t next = read32(node + 0u);
        const uint32_t entity = read32(node + 8u);
        node = next;

        if (!plausible(entity)) {
            break;
        }

        if ((read8(entity + 0x90u) & 0x40u) == 0u) {
            continue;  // not LIVE
        }

        const uint32_t attachment = component(entity, 22u);
        if (!plausible(attachment)) {
            continue;
        }

        const uint32_t sentinel = read32(attachment + 16u);
        if (!plausible(sentinel)) {
            continue;
        }

        uint32_t link = read32(sentinel);
        for (uint32_t depth = 0u; depth < 64u && written < max_records; ++depth) {
            if (link == sentinel || !plausible(link)) {
                break;
            }

            const uint32_t record = read32(link + 8u);
            link = read32(link);
            if (!plausible(record)) {
                continue;
            }

            const uint32_t slot = read32(record + 4u);
            const uint32_t child = plausible(slot) ? read32(slot) : 0u;
            if (!plausible(child)) {
                continue;  // the handle has been released — the record is on its way out
            }

            // The guest's own gate: its attachment update places a record only for a child
            // whose live bit is set, so a child that fails it is not being placed by it.
            if ((read8(child + 144u) & 0x40u) == 0u) {
                continue;
            }

            visit(entity, record, child, written);
            ++written;
        }
    }

    return written;
}
}  // extern "C++"

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachments(uint32_t* out_words, uint32_t max_records,
                                                       char* out_names) {
    if (out_words == nullptr || max_records == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    return ForEachStatedAttachment(base, max_records,
        [&](uint32_t entity, uint32_t record, uint32_t child, uint32_t written) {
            uint32_t* words = out_words + (written * REFII_ATTACHMENT_WORDS);
            words[0] = child;
            words[1] = entity;
            words[2] = GuestWordAt(base, record + 12u);
            words[3] = GuestWordAt(base, record + 144u);

            if (out_names != nullptr) {
                char* slotName = out_names + (written * REFII_ATTACHMENT_NAME);
                std::memset(slotName, 0, REFII_ATTACHMENT_NAME);
                const std::string name = ReadGuestString(base, record + 8u);
                const std::size_t copied =
                    name.size() < (REFII_ATTACHMENT_NAME - 1u) ? name.size()
                                                               : (REFII_ATTACHMENT_NAME - 1u);
                std::memcpy(slotName, name.data(), copied);
            }
        });
}

/// How many words RefiiHostGetAttachmentGrips writes per record.
#define REFII_ATTACHMENT_GRIP_WORDS 18u

/// THE OFFSET EACH ATTACHED CHILD HANGS AT, AS THE GUEST STATES IT — so the host composes the
/// child's placement instead of solving for it from reads taken at different instants (a solve
/// that re-ran about once a second across a town and snapped a weapon each time).
///
/// The guest places a child in `sub_8250F5F0(att, out, record, W)`:
///     8250f60c  bl   sub_822374D0          ; D = the parent dummy, model space, live pose
///     8250f614  addi r4, r31, 0x50         ; A = record+0x50
///     8250f61c  bl   sub_8221ACC0          ; T = A . D   (row vectors: A applies first)
///     8250f62c  bl   sub_8221ACC0          ; out = T . W  (W = the parent's world placement)
/// record+0x50 is the rigid inverse (`sub_82260FA0`, transpose and negated row 3) of the CHILD's
/// own grip dummy at record+0x10, copied in by the attach path (`sub_8250DF18`). It is constant
/// for the attachment's life. The dummy D is the parent gmd entry's local, times the gmd node
/// locals up to the first live bone (`sub_8223D148`), times that bone's model-space pose — all of
/// which the host has from its own `.mdl.gmd` and its own pose.
///
/// Words per record, in the same order as RefiiHostGetAttachments: [0] child, [1] ticket,
/// [2..17] record+0x50 as 16 floats, row-major, row vectors, translation in row 3. Additive: a
/// host that does not find this export solves as before.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachmentGrips(uint32_t* out_words, uint32_t max_records) {
    if (out_words == nullptr || max_records == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    return ForEachStatedAttachment(base, max_records,
        [&](uint32_t, uint32_t record, uint32_t child, uint32_t written) {
            uint32_t* words = out_words + (written * REFII_ATTACHMENT_GRIP_WORDS);
            words[0] = child;
            words[1] = GuestWordAt(base, record + 144u);
            for (uint32_t i = 0u; i < 16u; ++i) {
                words[2u + i] = GuestWordAt(base, record + 0x50u + (i * 4u));
            }
        });
}

/// The locomotion state a character is IN, by name, plus the gates that state was chosen under.
///
/// WHY BY NAME AND NOT BY INDEX. The guest's machine keeps its current state as an object with
/// an LHString name, and the name is the only stable identity it has: the 240-byte definition
/// records are heap-allocated per run and the registry is a std::map keyed by that same string.
/// Godot already reads the 41 state names out of the shipped script, so a name joins the two
/// sides exactly and a wrong read cannot masquerade as a different valid state — it comes back
/// as a string that matches nothing.
///
/// THE MACHINE IS AN ENTITY MODE. There is no locomotion component. Modes hang off component
/// 21 in a circular list and the locomotion machine is simply the one whose `GetModeType()`
/// returns 2, 3 or 4 (walk / strafe / aiming). Those three cannot be told apart by layout, so
/// they are identified by VTABLE — the only RTTI a stripped binary offers — and each is
/// verified to be a movement-interface mode: all three share `GetModeInterface()`
/// (`0x82EA63A8`, returns 1) at vtable slot +8, while slot +4 returns 2, 3 and 4 respectively.
///
/// WHAT IS NOT HERE. The velocity and control magnitudes the state machine actually tests are
/// built into a ~316-byte STACK temporary each tick by `sub_8228D058` and are gone by the time
/// any host can look, so there is nothing resident to read. An earlier report of a persistent
/// velocity ring on the machine did not survive reading that function and is not implemented
/// rather than being implemented on faith. `is child` is likewise absent: it needs a guest CALL
/// (`sub_821C2A18`), and this bridge only ever reads memory.
///
/// Every offset below was decompiled for this change, not taken on report:
///   entity+0x24 bit 21, entity+0x8C accelerator, entity+0x48..0x4C  (sub_821E5B28)
///   mgr+0x18 head, node{next+0, mode+8}, vtable+4 = GetModeType     (sub_821FF5C8)
///   machine+0x04 entity, machine+0x14 current state                 (sub_822CCE10)
///   state+0x24 name, state+0x2C dwell seconds                       (sub_822CCE10)
///   name+0x00 chars, name+0x04 length                               (LHString CBasicString ==)
///   comp99+16 stance, comp99+40 combat, comp132 confinement list    (sub_8228D058)
///
/// Writes REFII_LOCOMOTION_WORDS words: machine, state, modeType, dwell (float bits), stance,
/// combatState, confinementMask. Returns the words written, or 0 when the entity carries no
/// locomotion mode — which most entities do not, so 0 is the ordinary answer, not a failure.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLocomotion(uint32_t entity, uint32_t* out_words, uint32_t word_count,
                                      char* out_name, uint32_t name_capacity) {
    if (out_words == nullptr || word_count < REFII_LOCOMOTION_WORDS) {
        return 0u;
    }

    for (uint32_t i = 0u; i < REFII_LOCOMOTION_WORDS; ++i) {
        out_words[i] = 0u;
    }
    if (out_name != nullptr && name_capacity != 0u) {
        out_name[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };

    if (!plausible(entity)) {
        return 0u;
    }

    // entity -> component, by the id->slot accelerator with the sorted vector as the fallback.
    const auto component = [&](uint32_t id) -> uint32_t {
        return EntityComponent(base, entity, id);
    };

    const uint32_t manager = component(21u);
    if (!plausible(manager)) {
        return 0u;
    }

    // The mode list is circular and `mgr+0x18` holds the SENTINEL, so the walk starts at its
    // next and stops when it comes back round. The step cap is against a torn read, not a
    // real bound: no entity carries anywhere near this many modes.
    const uint32_t sentinel = read32(manager + 0x18u);
    if (!plausible(sentinel)) {
        return 0u;
    }

    uint32_t machine = 0u;
    uint32_t mode_type = 0u;

    uint32_t node = read32(sentinel);
    for (uint32_t step = 0u; step < 64u && plausible(node) && node != sentinel; ++step) {
        const uint32_t mode = read32(node + 8u);
        if (plausible(mode)) {
            switch (refii::guest::ToTu1(read32(mode))) {
                case 0x82006070u: machine = mode; mode_type = 2u; break;
                case 0x820DA468u: machine = mode; mode_type = 3u; break;
                case 0x820DA4C8u: machine = mode; mode_type = 4u; break;
                default: break;
            }
        }

        if (machine != 0u) {
            break;
        }

        node = read32(node);
    }

    if (machine == 0u) {
        return 0u;
    }

    const uint32_t state = read32(machine + 0x14u);

    out_words[0] = machine;
    out_words[1] = state;
    out_words[2] = mode_type;

    if (plausible(state)) {
        out_words[3] = read32(state + 0x2Cu);  // dwell seconds, float bits

        const uint32_t name = read32(state + 0x24u);
        if (plausible(name) && out_name != nullptr && name_capacity != 0u) {
            const uint32_t chars = read32(name);
            const uint32_t length = read32(name + 4u);
            if (plausible(chars) && length < 256u) {
                uint32_t copy = length;
                if (copy > name_capacity - 1u) {
                    copy = name_capacity - 1u;
                }

                for (uint32_t i = 0u; i < copy; ++i) {
                    out_name[i] = (char)read8(chars + i);
                }

                out_name[copy] = '\0';
            }
        }
    }

    // Stance and combat default to 1 — the wildcard on both — when the entity has no stance
    // component, which is what sub_8228D058 leaves in the context before its own branch.
    uint32_t stance = 1u;
    uint32_t combat = 1u;

    const uint32_t stance_component = component(99u);
    if (plausible(stance_component)) {
        stance = read32(stance_component + 16u);
        combat = (read32(stance_component + 40u) != 0u) ? 2u : 1u;
    }

    // The guest forces a neutral stance to REGULAR before testing, so a state declaring
    // Stance=1 matches everything and no runtime stance is ever 1.
    if (stance == 1u) {
        stance = 2u;
    }

    out_words[4] = stance;
    out_words[5] = combat;

    // Confinement is the OR of every ACTIVE contact's mask, not a single field.
    uint32_t confinement = 0u;
    const uint32_t confinement_component = component(132u);
    if (plausible(confinement_component)) {
        const uint32_t begin = read32(confinement_component + 16u);
        const uint32_t end = read32(confinement_component + 20u);
        if (plausible(begin) && end > begin && (end - begin) <= 0x1000u) {
            for (uint32_t at = begin; at + 8u <= end; at += 8u) {
                const uint32_t contact = read32(at);
                if (plausible(contact) && read8(contact + 112u) != 0u) {
                    confinement |= read32(contact + 128u);
                }
            }
        }
    }

    out_words[6] = confinement;
    return REFII_LOCOMOTION_WORDS;
}

/// The model hashes a COMPOSITE character is assembled from.
///
/// WHY THIS EXISTS. 29 of BWSSlums's 138 characters have a live 160-bone skeleton and a model
/// hash of exactly zero at comp6+0x20, so nothing draws them — they are people missing from
/// the street. `sub_82229BE0` proves zero is terminal on component 6:
///
///   82229ee4  lwz    r11, 0x14(r28)     ; r28 = comp6+12, so this is comp6+0x20
///   82229ef0  ble    cr6, loc_8222A3B8  ; hash == 0 -> branch past the ENTIRE mesh block
///
/// Their geometry is on `CECGraphicAppearanceMorph`, component id 17 — confirmed, not assumed:
/// `sub_8263C078` sets `v13[1] = 17` beside the class-name string in the 264-class registry,
/// and all 29 were measured to carry id 17.
///
/// TWO LISTS, AND WE READ THE ONE THAT IS ACTUALLY FILLED. Verified in `sub_8217FA50`:
///
///   comp17+0x28..0x2C  stride 4   the SOURCE hashes, written by the property loader
///   comp17+0x38..0x3C  stride 24  the BUILT part records, +0 = the same hash
///
/// `sub_8217FA50` maps the first to the second — `(a1[11] - a1[10]) >> 2` walks the source and
/// `sub_824C6388` pushes 24-byte records — and it is reached from `sub_8220DE88`, the per-frame
/// DRAW virtual. Our guest never draws, so the built list may never be populated at all, while
/// the source list is filled at construction from HeadModel / TorsoModel / LegsModel /
/// *Accessories / AdditionalModelFiles on the CharacterCreationRecord.
///
/// So the source list is what is returned, and the built count comes back separately rather
/// than being assumed either way. If `out_built` is non-zero the draw path does run here and
/// the 24-byte records are available too; if it is zero, this export is the only way in.
///
/// All offsets are measured from the COMPONENT POINTER. Writes up to `max_hashes` model
/// resource-name hashes and returns how many were written.
/// The pause-menu gate, so the owned view knows when it may stand in and when it must yield.
///
/// `out_screen_id` = `*(*(GUI+56)+4)` — the ACTIVE screen's id; **0 is the pause root**
/// (`sub_826A2C88` pushes id 0 at 0x826a2cd0). `out_modal_id` = `*(*(GUI+28)+44)` — the
/// pending query-box id, written only by the opener `sub_8236FA68` and reset to 0 by
/// `sub_8236FF58`. `out_modal_mode` = `*(*(GUI+28)+40)` — the modal LATCH: 1 idle,
/// 2 MessageBox (screen 3), 3 QueryBox (screen 19), 5 the numeric adjuster (screen 4); the
/// openers save/restore the current screen id, so `out_screen_id` is always the UNDERLYING
/// screen whatever box is up. `out_current_tab` = tabObj+4.
/// Returns 0 when the GUI object is not up, or is not the IN-GAME one: `dword_83496B08` has
/// two writers, and the front-end object's +56 is not a screen manager.
REFII_HOST_ABI_EXPORT int RefiiHostGetPauseState(uint32_t* out_screen_id, uint32_t* out_modal_id,
                                                 uint32_t* out_modal_mode,
                                                 uint32_t* out_current_tab) {
    if (out_screen_id != nullptr) *out_screen_id = 0xFFFFFFFFu;
    if (out_modal_id != nullptr) *out_modal_id = 0u;
    if (out_modal_mode != nullptr) *out_modal_mode = 1u;
    if (out_current_tab != nullptr) *out_current_tab = 0u;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;

    const uint32_t screen = read32(gui + 56u);
    if (out_screen_id != nullptr && plausible(screen)) *out_screen_id = refii::guest::ScreenIdToTu1(read32(screen + 4u));

    const uint32_t mgr = read32(gui + 28u);
    if (plausible(mgr)) {
        if (out_modal_id != nullptr) *out_modal_id = read32(mgr + 44u);
        if (out_modal_mode != nullptr) *out_modal_mode = read32(mgr + 40u);
    }

    const uint32_t tab = read32(gui + 76u);
    if (out_current_tab != nullptr && plausible(tab)) *out_current_tab = read32(tab + 4u);

    return 1;
}

/// Selects a pause tab THE GUEST'S OWN WAY — the lever `sub_8229ABD8` polls every GUI tick:
///
///   8229ace8  lwz r31, 0x4C(r11)     ; tabObj = *(GUI+76)
///   8229acf4  bl  sub_828C3748       ; fires when BYTE tabObj+13 set and FLOAT tabObj+20 <= now
///   8229ad0c  lwz r3, 8(r31)         ; requested (+8)
///   8229ad10  lwz r11, 4(r31)        ; current   (+4)
///   8229ad1c  bl  sub_826B89E0       ; the GUEST pushes the screen itself
///
/// So: `+8 = tab, +13 = 1, +20 = 0.0f` and nothing else — no deferred call, no thread hop.
/// Tabs: 1 root list, 2 map, 3 abilities, 4 items, 5 weapons, 6 clothing, 7 logbook,
/// 8 options (`sub_826B89E0`'s switch).
///
/// Refused (returns 0) unless the pause ROOT is the active screen (id 0) with no modal
/// pending — the same window in which the guest's own LB/RB nav runs. Availability
/// (`sub_826B83D0`: unlock flags and childhood) is the CALLER's job: `sub_826B89E0` does not
/// re-check it, so the host must not offer a tab the guest would ghost.
REFII_HOST_ABI_EXPORT int RefiiHostPauseTabSelect(uint32_t tab) {
    if (tab < 1u || tab > 8u) return 0;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;     // the IN-GAME object only

    const uint32_t screen = read32(gui + 56u);
    if (!plausible(screen) || read32(screen + 4u) != 0u) return 0;   // pause root only

    const uint32_t mgr = read32(gui + 28u);
    if (!plausible(mgr) || read32(mgr + 44u) != 0u) return 0;        // no modal pending

    const uint32_t tab_object = read32(gui + 76u);
    if (!plausible(tab_object)) return 0;

    const uint32_t requested = __builtin_bswap32(tab);
    StoreGuest(base, tab_object + 8u, &requested, sizeof(requested));
    const uint32_t zero_float = 0u;                                  // 0.0f, endian-free
    StoreGuest(base, tab_object + 20u, &zero_float, sizeof(zero_float));
    const uint8_t armed = 1u;
    StoreGuest(base, tab_object + 13u, &armed, sizeof(armed));       // the flag LAST

    return 1;
}

/// The display box that is up — `GUI.Display*Question`, the numeric adjuster and their
/// siblings — or 0 when none is.
///
/// The same walk `DispatchDisplayBoxAction` answers through: `guiOwner = *(GUI+28)`, the box
/// owner at `guiOwner+52` (as `IsDisplayBoxActive` 0x82305F10 passes it to `sub_822A5C50`), then
/// the router's own choice of showing box — see gui/gui_display_box.h. Open is the byte every
/// handler gates on (`sub_823CF3B0` `lbz r11, 0xC(r3)`). The vtable names the class —
/// 0x820AE688 the QueryBox, 0x820AE6A8 the NumericAdjuster.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDisplayBox(uint32_t* out_vtable) {
    if (out_vtable != nullptr) *out_vtable = 0u;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;

    const uint32_t gui_owner = read32(gui + 28u);
    if (!plausible(gui_owner)) return 0;

    const uint32_t owner = read32(gui_owner + 52u);
    if (!plausible(owner)) return 0;

    const uint32_t box = refii::vfs::ShowingDisplayBox(owner, read32);
    if (!plausible(box) || GuestByteAt(base, box + 12u) == 0u) return 0;

    if (out_vtable != nullptr) *out_vtable = read32(box);
    return box;
}

/// The MenuBox's state, for the owned list: is one open, how many options, which is
/// selected, whether it can be cancelled, and which options the guest marked enabled.
///
/// Not element state: the controller at `*(guiOwner+56)` is a plain struct (no vtable) holding
/// the model — `+0` selected index, `+12` open flag, `+13` cancelled, `+14` cancelable,
/// `+20/+24` the option vector (stride 8, `{item*, refcell*}`), each item's `+4` byte its
/// ENABLED flag, which `sub_823DC720` re-checks on accept. Option LABELS are live element text.
///
/// Returns the option count (0 when no box is open), writes the enabled bits as a bitmask.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuBox(uint32_t* out_selected, uint32_t* out_flags,
                                                   uint32_t* out_enabled_mask) {
    if (out_selected != nullptr) *out_selected = 0u;
    if (out_flags != nullptr) *out_flags = 0u;
    if (out_enabled_mask != nullptr) *out_enabled_mask = 0u;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;

    const uint32_t owner = read32(gui + 28u);
    if (!plausible(owner)) return 0;

    const uint32_t control = read32(owner + 56u);
    if (!plausible(control) || base[control + 12u] == 0u) return 0;  // no box open

    const uint32_t begin = read32(control + 20u);
    const uint32_t end = read32(control + 24u);
    if (begin == 0u || !plausible(begin) || end < begin) return 0;

    // The box caps at six; anything else is a torn read of a vector mid-grow.
    const uint32_t count = (end - begin) / 8u;
    if (count > 6u) return 0;

    if (out_selected != nullptr) *out_selected = read32(control + 0u);
    if (out_flags != nullptr) {
        // bit 0 cancelable, bit 1 already-cancelled.
        *out_flags = (base[control + 14u] != 0u ? 1u : 0u) |
                     (base[control + 13u] != 0u ? 2u : 0u);
    }

    if (out_enabled_mask != nullptr) {
        uint32_t mask = 0u;
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t item = read32(begin + (i * 8u));
            if (plausible(item) && base[item + 4u] != 0u) {
                mask |= 1u << i;
            }
        }

        *out_enabled_mask = mask;
    }

    return count;
}

/// Which pause tabs the guest itself would offer, as its own predicate `sub_826B83D0` answered
/// when the pump called it on the GUI tick. There is no availability mask in the image, and the
/// tab push (`sub_826B89E0`) never re-checks the predicate.
///
/// Returns a bitmask with bit N = tab N (2..8); bits 0 and 1 are unused. Fails open to
/// all-available only when the pump has not sampled yet (guest not up, or the pause book not
/// yet opened).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPauseTabsAvailable(void) {
    constexpr uint32_t kAll = 0x1FCu;  // bits 2..8

    const uint32_t sampled = refii::vfs::PauseTabsMask();
    return sampled != 0u ? sampled : kAll;
}

/// The subtitles setting: 1 on, 0 off, -1 not sampled yet (front end not up, or too early).
REFII_HOST_ABI_EXPORT int RefiiHostGetSubtitles(void) {
    return refii::vfs::FrontEndSubtitles();
}

/// One captured menu row's IDENTITY — the half of a list that never touches an element.
///
/// Captured, not read: `g_Menu:CreateMenuItem` builds a pure Lua table the host cannot reach,
/// but `sub_826CE8D0` is handed the name, event type and 32-bit id in registers.
/// `sub_826CE110` (the `DeleteAllSpawnedMenuItems` wrapper) clears the capture — the one list
/// boundary the in-game and front-end populations share. The label arrives separately, through
/// the element tree, on the pool button's `Icon.Title`.
///
/// A row is committed through `RefiiHostPostFrontEndAction("MenuEvent:<name>:<type>:<id>")` —
/// the same `GUI:PushButtonEvent` the shipped `SelectMenuItem` would have made.
///
/// Returns the TOTAL row count regardless of `index`, so a caller sizes its loop from the first
/// call. `out_generation` is bumped on every spawn: equal generations mean the same list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRow(uint32_t index, char* out_name,
                                                   uint32_t name_capacity, char* out_parent,
                                                   uint32_t parent_capacity, uint32_t* out_id,
                                                   int32_t* out_event_type,
                                                   uint64_t* out_generation,
                                                   int32_t* out_rating) {
    return refii::vfs::MenuRows(index, out_name, name_capacity, out_parent, parent_capacity,
                                out_id, out_event_type, out_generation, out_rating);
}

/// The SCREEN the captured rows belong to, or 0xFFFFFFFF when no in-game screen states one.
///
/// The capture is one list, so a template sourced `capture:Root` used to take whatever was
/// spawned last, whoever spawned it — the front end's `New Game` and `Continue` came up on the
/// real-estate screen above its own rows. This is the key that was missing: `manager+4` off the
/// in-game GUI, read by the payload at the boundary that clears the list. Compare it with the
/// screen a template is bound to and reject a population that belongs to another.
/// Raw guest words for the debug channel — the microscope of last resort.
///
/// Every deep UI bug this week ended at the same wall: a pointer chain whose next hop nobody
/// could see without another native rebuild. The world-map provider's output sat at a live
/// address holding live pointers, readable only by adding one more log and paying one more
/// fifteen-minute cycle. This reads N words at an address so the question costs one call.
/// Read-only, loopback-gated by the debug server; returns how many words were readable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPeek(uint32_t addr, uint32_t* out_words,
                                             uint32_t count) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_words == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    if (count > 64u) count = 64u;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t at = addr + i * 4u;
        if (at < 0x10000u || at >= 0xC0000000u) return i;
        out_words[i] = GuestWordAt(base, at);
    }

    return count;
}

// ========================= ReXGlue runtime introspection (debug channel) =========================
//
// The runtime the guest runs IN, exposed the same way RefiiHostPeek exposes its memory: read-only
// answers for the debug channel, so a frozen run can name its own stuck thread instead of needing
// gdb as a parent. Everything here reaches public rex:: API through Runtime::instance() — no
// shadow edits, no kernel edits. RefiiHostSetCvar is the one write, and the cvar registry's own
// lifecycle validation decides whether it lands.

/// Fixed-stride string table: slot `index` of `out`, truncating. Stride includes the NUL.
static void WriteSlot(char* out, uint32_t stride, uint32_t index, const std::string& text) {
    if (out == nullptr || stride == 0u) return;
    char* slot = out + static_cast<size_t>(index) * stride;
    const size_t copied = text.size() < stride - 1u ? text.size() : stride - 1u;
    std::memcpy(slot, text.data(), copied);
    slot[copied] = '\0';
}

/// Every kernel-registered thread. Flags: bit0 guest-created, bit1 main thread, bit2 running.
/// r1 and lr are read from the LIVE context without stopping the thread — torn values are
/// possible on a running thread and exact on a parked one, which is the case that matters.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGuestThreads(
    uint32_t* out_tids, uint32_t* out_flags, int32_t* out_priority, uint32_t* out_cpu,
    uint32_t* out_r1, uint32_t* out_lr, char* out_names, uint32_t name_stride,
    uint32_t capacity) {
    rex::system::KernelState* state = rex::system::kernel_state();
    if (state == nullptr) return 0;

    auto threads =
        state->object_table()->GetObjectsByType<rex::system::XThread>();
    uint32_t written = 0;
    for (const auto& thread : threads) {
        if (written >= capacity) break;
        if (out_tids != nullptr) out_tids[written] = thread->thread_id();
        if (out_flags != nullptr) {
            out_flags[written] = (thread->is_guest_thread() ? 1u : 0u) |
                                 (thread->main_thread() ? 2u : 0u) |
                                 (thread->is_running() ? 4u : 0u);
        }
        if (out_priority != nullptr) out_priority[written] = thread->priority();
        if (out_cpu != nullptr) out_cpu[written] = thread->active_cpu();

        PPCContext* context =
            thread->thread_state() != nullptr ? thread->thread_state()->context() : nullptr;
        if (out_r1 != nullptr) out_r1[written] = context != nullptr ? context->r1.u32 : 0u;
        if (out_lr != nullptr)
            out_lr[written] = context != nullptr ? static_cast<uint32_t>(context->lr) : 0u;

        WriteSlot(out_names, name_stride, written,
                  thread->thread() != nullptr ? thread->thread()->name() : std::string());
        ++written;
    }

    return written;
}

/// Guest backtrace for one thread id, innermost first. Frame 0 is the context's own LR (a leaf's
/// return address exists nowhere else); the rest walk the back chain.
///
/// The walk is the title's own ABI, verified in the binary: every one of 45313 frames is a
/// 32-bit `stwu r1, -X(r1)` (zero stdu), so the back chain is a 4-byte word at [r1]; and both
/// prologue conventions save LR at back-chain minus 8 — `stw r12, -8(r1)` at 0x82170CCC (plain)
/// and 0x82CA9418 (__savegprlr_*), 24679 of 24715 `mflr r12` functions. So:
///
///     back = load32(r1);  lr = load32(back - 8);  r1 = back;  repeat
///
/// terminating on 0, a non-ascending chain, or an unreadable word.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestBacktrace(uint32_t tid, uint32_t* out_frames,
                                                       uint32_t capacity) {
    rex::system::KernelState* state = rex::system::kernel_state();
    auto* memory = SafeKernelMemory();
    if (state == nullptr || memory == nullptr || out_frames == nullptr || capacity == 0u) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    auto thread = state->GetThreadByID(tid);
    if (!thread || thread->thread_state() == nullptr) return 0;
    PPCContext* context = thread->thread_state()->context();
    if (context == nullptr) return 0;

    uint32_t written = 0;
    out_frames[written++] = static_cast<uint32_t>(context->lr);

    uint32_t sp = context->r1.u32;
    while (written < capacity && sp >= 0x10000u && sp < 0xC0000000u) {
        const uint32_t back = GuestWordAt(base, sp);
        if (back == 0u || back <= sp) break;
        const uint32_t lr = GuestWordAt(base, back - 8u);
        if (lr == 0u) break;
        out_frames[written++] = lr;
        sp = back;
    }

    return written;
}

#ifdef REXGLUE_ENABLE_PROFILING
/// GUEST STACK SAMPLING into the in-game Tracy capture: the recompiled game's own call stacks,
/// which are otherwise invisible to Tracy (instrumenting every guest function is far too heavy).
///
/// A sampler thread reads every guest thread's live context and walks its back chain, exactly as
/// RefiiHostGuestBacktrace does, a few hundred times a second. Each guest thread is a Tracy fiber
/// ("guest: <name> (<tid>)"); between samples, the frames that changed close and open as zones,
/// so the timeline shows each guest thread's sampled call stack over time. Times are sample
/// times: a zone is as long as the frame was seen, to the sampling interval.
///
/// The thread list is refreshed once a second; the kernel's global critical region is taken
/// only for that, never per sample — the walk reads the context and guest memory with the same
/// fault-guarded reads as the backtrace, without stopping the thread. A frame is the function
/// containing a return address (the nearest recompiled function entry at or below it, cached
/// per address), named sub_XXXXXXXX. Frame 0 is the context's LR — the most recent call's return
/// address, so it names the function that made the last call.
namespace guest_sampler {

using ThreadRefs = decltype(rex::system::kernel_state()->object_table()->GetObjectsByType<rex::system::XThread>());

struct Track {
    rex::system::XThread* thread = nullptr;
    const char* fiber = nullptr;
    std::vector<uint32_t> stack;       // open frames, outermost first
    std::vector<TracyCZoneCtx> zones;  // their zones, same order
    bool seen = false;
};

std::mutex g_control;
std::thread g_thread;
std::atomic<bool> g_run{false};

/// Fiber names live for the process: Tracy keeps the pointer.
const char* InternFiber(const std::string& name) {
    static std::mutex lock;
    static std::set<std::string>* names = new std::set<std::string>();
    std::lock_guard guard(lock);
    return names->insert(name).first->c_str();
}

/// The recompiled function containing `address`: the nearest entry at or below it.
uint32_t FunctionOf(rex::runtime::FunctionDispatcher* dispatcher, uint32_t address,
                    std::unordered_map<uint32_t, uint32_t>& cache) {
    if (auto it = cache.find(address); it != cache.end()) return it->second;
    constexpr uint32_t kSearch = 64u * 1024u;  // no Fable 2 function is longer
    uint32_t entry = 0;
    for (uint32_t at = address & ~3u, steps = 0; at != 0 && steps < kSearch / 4u; at -= 4u, ++steps) {
        if (dispatcher->GetFunction(at) != nullptr) {
            entry = at;
            break;
        }
    }
    cache.emplace(address, entry);
    return entry;
}

void Close(Track& track, size_t keep) {
    while (track.zones.size() > keep) {
        ___tracy_emit_zone_end(track.zones.back());
        track.zones.pop_back();
        track.stack.pop_back();
    }
}

void Run(uint32_t interval_us, uint32_t depth) {
    rex::runtime::FunctionDispatcher* dispatcher =
        rex::Runtime::instance() != nullptr ? rex::Runtime::instance()->function_dispatcher() : nullptr;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (dispatcher == nullptr || base == nullptr) return;

    ThreadRefs refs;
    std::unordered_map<uint32_t, Track> tracks;
    std::unordered_map<uint32_t, uint32_t> functions;
    std::vector<uint32_t> walk;
    std::vector<uint32_t> now;
    auto refreshed = std::chrono::steady_clock::time_point{};

    while (g_run.load(std::memory_order_relaxed)) {
        const auto tick = std::chrono::steady_clock::now();
        if (tick - refreshed > std::chrono::seconds(1)) {
            refreshed = tick;
            if (rex::system::KernelState* state = rex::system::kernel_state()) {
                refs = state->object_table()->GetObjectsByType<rex::system::XThread>();
                for (auto& [tid, track] : tracks) track.seen = false;
                for (const auto& thread : refs) {
                    if (!thread->is_guest_thread() || thread->thread_state() == nullptr) continue;
                    Track& track = tracks[thread->thread_id()];
                    track.seen = true;
                    track.thread = thread.get();
                    if (track.fiber == nullptr) {
                        const std::string name = thread->thread() != nullptr ? thread->thread()->name() : std::string();
                        track.fiber = InternFiber("guest: " + (name.empty() ? std::string("thread") : name) + " (" +
                                                  std::to_string(thread->thread_id()) + ")");
                    }
                }
                for (auto it = tracks.begin(); it != tracks.end();) {
                    if (it->second.seen) {
                        ++it;
                        continue;
                    }
                    ___tracy_fiber_enter(it->second.fiber);
                    Close(it->second, 0);
                    ___tracy_fiber_leave();
                    it = tracks.erase(it);
                }
            }
        }

        for (auto& [tid, track] : tracks) {
            PPCContext* context = track.thread->thread_state() != nullptr ? track.thread->thread_state()->context() : nullptr;
            if (context == nullptr) continue;

            walk.clear();
            walk.push_back(static_cast<uint32_t>(context->lr));
            uint32_t sp = context->r1.u32;
            while (walk.size() < depth && sp >= 0x10000u && sp < 0xC0000000u) {
                const uint32_t back = GuestWordAt(base, sp);
                if (back == 0u || back <= sp) break;
                const uint32_t lr = GuestWordAt(base, back - 8u);
                if (lr == 0u) break;
                walk.push_back(lr);
                sp = back;
            }

            now.clear();
            for (auto it = walk.rbegin(); it != walk.rend(); ++it) {
                if (const uint32_t entry = FunctionOf(dispatcher, *it, functions)) now.push_back(entry);
            }

            size_t common = 0;
            while (common < now.size() && common < track.stack.size() && now[common] == track.stack[common]) ++common;
            if (common == now.size() && common == track.stack.size()) continue;

            ___tracy_fiber_enter(track.fiber);
            Close(track, common);
            for (size_t i = common; i < now.size(); ++i) {
                char name[16];
                const int length = std::snprintf(name, sizeof(name), "sub_%08X", now[i]);
                const uint64_t srcloc = ___tracy_alloc_srcloc_name(0, "guest", 5, name, length, name, length, 0);
                track.zones.push_back(___tracy_emit_zone_begin_alloc(srcloc, 1));
                track.stack.push_back(now[i]);
            }
            ___tracy_fiber_leave();
        }

        std::this_thread::sleep_until(tick + std::chrono::microseconds(interval_us));
    }

    for (auto& [tid, track] : tracks) {
        ___tracy_fiber_enter(track.fiber);
        Close(track, 0);
        ___tracy_fiber_leave();
    }
}

}  // namespace guest_sampler

/// Starts sampling the guest threads' stacks into the Tracy capture: `hz` samples a second,
/// at most `depth` frames each. 1 = running.
REFII_HOST_ABI_EXPORT int RefiiHostGuestSamplerStart(uint32_t hz, uint32_t depth) {
    std::lock_guard guard(guest_sampler::g_control);
    if (guest_sampler::g_thread.joinable() || !TracyIsStarted) return guest_sampler::g_thread.joinable() ? 1 : 0;
    const uint32_t interval_us = 1000000u / std::clamp<uint32_t>(hz, 1u, 2000u);
    guest_sampler::g_run.store(true);
    guest_sampler::g_thread = std::thread([interval_us, depth] {
        tracy::SetThreadName("Guest stack sampler");
        guest_sampler::Run(interval_us, std::clamp<uint32_t>(depth, 1u, 256u));
    });
    return 1;
}

REFII_HOST_ABI_EXPORT void RefiiHostGuestSamplerStop(void) {
    std::lock_guard guard(guest_sampler::g_control);
    guest_sampler::g_run.store(false);
    if (guest_sampler::g_thread.joinable()) guest_sampler::g_thread.join();
}
#else
REFII_HOST_ABI_EXPORT int RefiiHostGuestSamplerStart(uint32_t, uint32_t) { return 0; }
REFII_HOST_ABI_EXPORT void RefiiHostGuestSamplerStop(void) {}
#endif

/// Every object in the kernel handle table: handle, type (rex::system::XObject::Type), name.
/// `out_total` is the table's full population so a clamped read says it was clamped.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetKernelObjects(uint32_t* out_handles,
                                                         uint32_t* out_types, char* out_names,
                                                         uint32_t name_stride, uint32_t capacity,
                                                         uint32_t* out_total) {
    if (out_total != nullptr) *out_total = 0;
    rex::system::KernelState* state = rex::system::kernel_state();
    if (state == nullptr) return 0;

    auto objects = state->object_table()->GetAllObjects();
    if (out_total != nullptr) *out_total = static_cast<uint32_t>(objects.size());

    uint32_t written = 0;
    for (const auto& object : objects) {
        if (written >= capacity) break;
        if (out_handles != nullptr) out_handles[written] = object->handle();
        if (out_types != nullptr) out_types[written] = static_cast<uint32_t>(object->type());
        WriteSlot(out_names, name_stride, written, object->name());
        ++written;
    }

    return written;
}

/// Every rex::perf counter, LIVE values. Snapshot counters are deliberately not used: they
/// rotate in Profiler::Flip(), which lives in the command processor — compiled out with the
/// renderer — so in this build they would read as an eternal zero. Live counters with no reset
/// site are run totals; rates come from deltas between two reads, taken by the caller.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPerfCounters(int64_t* out_values, char* out_names,
                                                        uint32_t name_stride, uint32_t capacity) {
    const uint32_t count = static_cast<uint32_t>(rex::perf::CounterId::kCount);
    uint32_t written = 0;
    for (uint32_t i = 0; i < count && written < capacity; ++i) {
        const auto id = static_cast<rex::perf::CounterId>(i);
        if (out_values != nullptr) out_values[written] = rex::perf::GetCounter(id);
        WriteSlot(out_names, name_stride, written, rex::perf::CounterName(id));
        ++written;
    }

    return written;
}

/// A page of the kernel export tables, as "module!Name" plus ordinal and flags (bit0
/// implemented, bit1 variable). `only_unimplemented` filters to the stubs the title could hit.
/// `out_total` is the filtered population. Per-call counts are NOT available: Export carries a
/// call_count field but nothing increments it in recompiled builds — do not report these as
/// call frequencies.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetKernelExports(uint32_t first,
                                                         uint32_t only_unimplemented,
                                                         uint32_t* out_ordinals,
                                                         uint32_t* out_flags, char* out_names,
                                                         uint32_t name_stride, uint32_t capacity,
                                                         uint32_t* out_total) {
    if (out_total != nullptr) *out_total = 0;
    rex::Runtime* runtime = rex::Runtime::instance();
    if (runtime == nullptr || runtime->export_resolver() == nullptr) return 0;

    uint32_t matched = 0;
    uint32_t written = 0;
    for (const auto& table : runtime->export_resolver()->tables()) {
        for (const rex::runtime::Export* entry : table.exports_by_name()) {
            if (entry == nullptr) continue;
            if (only_unimplemented != 0u && entry->is_implemented()) continue;
            const uint32_t index = matched++;
            if (index < first || written >= capacity) continue;
            if (out_ordinals != nullptr) out_ordinals[written] = entry->ordinal;
            if (out_flags != nullptr) {
                out_flags[written] =
                    (entry->is_implemented() ? 1u : 0u) |
                    (entry->type == rex::runtime::Export::Type::kVariable ? 2u : 0u);
            }
            WriteSlot(out_names, name_stride, written, table.module_name() + "!" + entry->name);
            ++written;
        }
    }

    if (out_total != nullptr) *out_total = matched;
    return written;
}

/// One cvar by registry index: name, live value, default, category, type
/// (rex::cvar::FlagType), lifecycle, flags (bit0 modified from default, bit1 debug-only).
/// Returns 0 past the end. Commands have no value; their slots come back empty.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCvarInfo(uint32_t index, char* out_name,
                                                    uint32_t name_capacity, char* out_value,
                                                    uint32_t value_capacity, char* out_default,
                                                    uint32_t default_capacity, char* out_category,
                                                    uint32_t category_capacity,
                                                    uint32_t* out_type, uint32_t* out_lifecycle,
                                                    uint32_t* out_flags) {
    const auto& registry = rex::cvar::GetRegistry();
    if (index >= registry.size()) return 0;
    const rex::cvar::FlagEntry& entry = registry[index];

    WriteSlot(out_name, name_capacity, 0, entry.name);
    WriteSlot(out_value, value_capacity, 0,
              entry.getter ? entry.getter() : std::string());
    WriteSlot(out_default, default_capacity, 0, entry.default_value);
    WriteSlot(out_category, category_capacity, 0, entry.category);
    if (out_type != nullptr) *out_type = static_cast<uint32_t>(entry.type);
    if (out_lifecycle != nullptr) *out_lifecycle = static_cast<uint32_t>(entry.lifecycle);
    if (out_flags != nullptr) {
        *out_flags = (rex::cvar::HasNonDefaultValue(entry.name) ? 1u : 0u) |
                     (entry.is_debug_only ? 2u : 0u);
    }

    return 1;
}

REFII_HOST_ABI_EXPORT int32_t RefiiHostGetCvarCount(void) {
    return static_cast<int32_t>(rex::cvar::GetRegistry().size());
}

/// Sets one cvar through the registry's own validator — lifecycle, range and allowed-value
/// checks are its, not ours. Returns 1 when the registry accepted the value.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostSetCvar(const char* name, const char* value) {
    if (name == nullptr || value == nullptr) return 0;
    return rex::cvar::SetFlagByName(name, value) ? 1u : 0u;
}

/// Resolves one guest path against the live VFS. Returns bit0 = an entry resolved (absolute
/// path, size and attributes filled), bit1 = the path's root is a symbolic link (target
/// filled) — so "the link exists but resolves to nothing" is distinguishable from "no such
/// mount", which is the wrong-naming failure this project has already had.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostVfsResolve(const char* guest_path, char* out_absolute,
                                                   uint32_t absolute_capacity, char* out_link,
                                                   uint32_t link_capacity, uint64_t* out_size,
                                                   uint32_t* out_attributes) {
    if (out_size != nullptr) *out_size = 0;
    if (out_attributes != nullptr) *out_attributes = 0;
    if (guest_path == nullptr) return 0;
    rex::Runtime* runtime = rex::Runtime::instance();
    if (runtime == nullptr || runtime->file_system() == nullptr) return 0;

    uint32_t result = 0;

    std::string path(guest_path);
    std::string link_target;
    const size_t separator = path.find_first_of("\\/");
    const std::string root = separator == std::string::npos ? path : path.substr(0, separator);
    if (runtime->file_system()->FindSymbolicLink(root, link_target)) {
        result |= 2u;
        WriteSlot(out_link, link_capacity, 0, link_target);
    }

    rex::filesystem::Entry* entry = runtime->file_system()->ResolvePath(path);
    if (entry != nullptr) {
        result |= 1u;
        WriteSlot(out_absolute, absolute_capacity, 0, entry->absolute_path());
        if (out_size != nullptr) *out_size = entry->size();
        if (out_attributes != nullptr) *out_attributes = entry->attributes();
    }

    return result;
}

/// The guest arena's identity, for OUT-OF-PROCESS memory readers — the gdb-equivalent path.
///
/// The whole guest address space is one named shared-memory object (`Memory::Initialize`
/// backs 4GB+512MB with `xenia_memory_<tick>`), so any same-user process can read it without
/// ptrace: on Linux it is a file in /dev/shm (and a crashed run's leak is a full post-mortem
/// image); on Windows the mapping is named `Local\xenia_memory_<tick>` and
/// ReadProcessMemory(membase + guest_address) reads through this process's own page tables.
/// The name here is the BARE name — the reader adds the platform prefix. `out_membase` is the
/// host virtual base of the guest's views, which is what ReadProcessMemory needs.
///
/// This must be readable while the game is FROZEN, which is exactly when the debug channel —
/// served from the host's main thread — cannot answer. So the identity is published into the
/// handshake file the moment the guest is up, and learned by readers BEFORE they need it.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGuestArenaInfo(char* out_name, uint32_t name_capacity,
                                                          uint64_t* out_membase) {
    if (out_membase != nullptr) *out_membase = 0;
    rex::memory::Memory* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;

    WriteSlot(out_name, name_capacity, 0, memory->file_name().string());
    if (out_membase != nullptr) {
        *out_membase = reinterpret_cast<uint64_t>(memory->virtual_membase());
    }

    return 1;
}

// ============================ Debug instrumentation batch (ABI 113) ============================

/// One FrameMark per presented host frame. Profiler::Flip() is otherwise called only by the
/// command processor, which this build compiles out — so without this the Tracy timeline has no
/// frame boundaries and the perf snapshot counters never rotate. Calling it here makes
/// GetSnapshotCounter mean "last frame" and arms the perf_log_csv cvar's per-frame writer.
REFII_HOST_ABI_EXPORT void RefiiHostFrameMark(void) { rex::perf::Profiler::Flip(); }

/// Per-frame counter snapshots — meaningful once RefiiHostFrameMark runs each frame.
/// Accumulators are the LAST FRAME's count; gauges are their value at the last flip.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPerfSnapshots(int64_t* out_values, char* out_names,
                                                         uint32_t name_stride, uint32_t capacity) {
    const uint32_t count = static_cast<uint32_t>(rex::perf::CounterId::kCount);
    uint32_t written = 0;
    for (uint32_t i = 0; i < count && written < capacity; ++i) {
        const auto id = static_cast<rex::perf::CounterId>(i);
        if (out_values != nullptr) out_values[written] = rex::perf::GetSnapshotCounter(id);
        WriteSlot(out_names, name_stride, written, rex::perf::CounterName(id));
        ++written;
    }

    return written;
}

/// Tracy from the HOST side, so Godot's phases share the guest threads' timeline. All four are
/// no-ops in a build without profiling. Zone contexts cross the ABI packed as
/// id | (active << 32); plot names are interned because Tracy keeps the pointer.
///
/// AND NO-OPS WHILE THE PROFILER IS NOT STARTED. The SDK builds Tracy with
/// TRACY_MANUAL_LIFETIME: rex::perf::Profiler::Startup() starts it on demand, and until then
/// the C emit functions assert on the missing profiler data (b171: every payload zone came
/// through here and the first one aborted the process). The host-side macros carry
/// TracyIsStarted as their active flag; this is the same gate for the C path. An inactive
/// zone context (active = 0) is what ___tracy_emit_zone_end already ignores.
#ifdef REXGLUE_ENABLE_PROFILING
/// Brings the profiler up, once. Answers 1 when it is running, 0 when it is not — on Windows that
/// is a quarantined Tracy library: every module delay-loads it and a missing one starts nothing
/// (src/tracy/tracy_absent_win.cpp).
///
/// THE ZONES ARE INERT WITHOUT THIS. The runtime is built with `TRACY_MANUAL_LIFETIME`, which
/// means the client does nothing until it is started explicitly, and nothing in the tree ever
/// called it. Every zone the engine emitted therefore went nowhere, which reads exactly like
/// "the profiler is compiled out" and is not.
///
/// Cheap to leave running: `TRACY_ON_DEMAND` is also set, so the client collects nothing until
/// the game's own capture (refii_tracy_capture.h, in-process, no socket) connects to it.
REFII_HOST_ABI_EXPORT int RefiiHostTraceStartup(void) {
    static bool started = false;
    if (!started) {
        ___tracy_startup_profiler();
        started = true;
        if (TracyIsStarted) {
            REXLOG_INFO("Refii host bridge: Tracy profiler started (in-process; capture from the dev menu)");
        } else {
            REXLOG_WARN("Refii host bridge: Tracy library missing; profiler and crash captures unavailable");
        }
    }

    return TracyIsStarted ? 1 : 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostTraceMessage(const char* text) {
    if (text != nullptr && TracyIsStarted) {
        ___tracy_emit_message(text, std::strlen(text), 0);
    }
}

REFII_HOST_ABI_EXPORT uint64_t RefiiHostTraceZoneBegin(const char* name) {
    if (name == nullptr || !TracyIsStarted) return 0;
    const uint64_t srcloc = ___tracy_alloc_srcloc_name(
        0, "host", 4, "godot", 5, name, std::strlen(name), 0);
    const TracyCZoneCtx zone = ___tracy_emit_zone_begin_alloc(srcloc, 1);
    return static_cast<uint64_t>(zone.id) | (static_cast<uint64_t>(zone.active) << 32);
}

REFII_HOST_ABI_EXPORT void RefiiHostTraceZoneEnd(uint64_t packed) {
    if (packed == 0) return;
    TracyCZoneCtx zone;
    zone.id = static_cast<uint32_t>(packed);
    zone.active = static_cast<int>(packed >> 32);
    ___tracy_emit_zone_end(zone);
}

REFII_HOST_ABI_EXPORT void RefiiHostTracePlot(const char* name, double value) {
    if (name == nullptr || !TracyIsStarted) return;
    static std::mutex names_mutex;
    static std::set<std::string>* interned = new std::set<std::string>();
    const char* stable;
    {
        std::lock_guard<std::mutex> guard(names_mutex);
        stable = interned->insert(name).first->c_str();
    }
    ___tracy_emit_plot(stable, value);
}

/// GODOT'S GPU PASSES ON THE TRACY TIMELINE. The engine puts a GPU timestamp at every render pass
/// boundary (`RENDER_TIMESTAMP`: shadows, opaque, sky, transparent, SDFGI, fog, tonemap) while
/// frame profiling is on (`--gpu-profile`), and the host reads the finished frame's table off
/// the RenderingDevice. Each consecutive pair of timestamps is one pass; here it becomes a GPU
/// zone on a context of its own and a plot of its milliseconds, so the split of the GPU frame
/// sits beside the CPU zones instead of being one number per viewport.
///
/// PLACEMENT IS APPROXIMATE, LENGTHS ARE EXACT. Tracy pairs a GPU context's reference timestamp
/// with the CPU instant the context is created, and the table arrives a frame after the GPU ran
/// it, so the zones sit about a frame later on the timeline than the work they name; the pass
/// durations are the GPU's own. `names` is a packed table of `name_stride` bytes per entry; the
/// GPU times are NANOSECONDS despite the `gpu_us` name. Serial emits: this runs on Godot's main
/// thread every frame and the order of the events is the contract.
REFII_HOST_ABI_EXPORT void RefiiHostTraceGpuPasses(const char* names, uint32_t name_stride,
                                                    const int64_t* cpu_times, const int64_t* gpu_us,
                                                    uint32_t count) {
    (void)cpu_times;
    // Connected, not just started: under TRACY_ON_DEMAND each pass would still allocate a source
    // location per frame only to be dropped. The GPU context is created on the first connected
    // frame.
    if (names == nullptr || gpu_us == nullptr || name_stride == 0 || count < 2 || !TracyIsStarted ||
        ___tracy_connected() == 0) {
        return;
    }

    constexpr uint8_t kContext = 200;  // clear of anything the runtime numbers itself
    constexpr uint8_t kCustomContext = 7;  // tracy::GpuContextType::Custom
    static bool context_made = false;
    static uint16_t query = 0;
    if (!context_made) {
        ___tracy_gpu_new_context_data context{};
        context.gpuTime = gpu_us[0];
        context.period = 1.0f;  // nanoseconds per tick: the timestamps are nanoseconds
        context.context = kContext;
        context.flags = 0;
        context.type = kCustomContext;
        ___tracy_emit_gpu_new_context_serial(context);

        static const char kName[] = "Godot render passes";
        ___tracy_gpu_context_name_data name{};
        name.context = kContext;
        name.name = kName;
        name.len = static_cast<uint16_t>(sizeof(kName) - 1);
        ___tracy_emit_gpu_context_name_serial(name);
        context_made = true;
    }

    static std::mutex names_mutex;
    static std::set<std::string>* interned = new std::set<std::string>();
    for (uint32_t i = 0; i + 1 < count; ++i) {
        const char* name = names + static_cast<size_t>(i) * name_stride;
        const size_t len = strnlen(name, name_stride);
        if (len == 0) {
            continue;
        }

        const uint64_t srcloc = ___tracy_alloc_srcloc_name(0, "gpu", 3, "godot", 5, name, len, 0);
        ___tracy_gpu_zone_begin_data begin{};
        begin.srcloc = srcloc;
        begin.queryId = query;
        begin.context = kContext;
        ___tracy_emit_gpu_zone_begin_alloc_serial(begin);
        ___tracy_gpu_time_data start{};
        start.gpuTime = gpu_us[i];
        start.queryId = query;
        start.context = kContext;
        ___tracy_emit_gpu_time_serial(start);
        ++query;

        ___tracy_gpu_zone_end_data end{};
        end.queryId = query;
        end.context = kContext;
        ___tracy_emit_gpu_zone_end_serial(end);
        ___tracy_gpu_time_data finish{};
        finish.gpuTime = gpu_us[i + 1];
        finish.queryId = query;
        finish.context = kContext;
        ___tracy_emit_gpu_time_serial(finish);
        ++query;

        const char* stable;
        {
            std::lock_guard<std::mutex> guard(names_mutex);
            stable = interned->insert("gpu " + std::string(name, len) + " ms").first->c_str();
        }
        ___tracy_emit_plot(stable, static_cast<double>(gpu_us[i + 1] - gpu_us[i]) / 1.0e6);
    }
}
#else
REFII_HOST_ABI_EXPORT int RefiiHostTraceStartup(void) { return 0; }

REFII_HOST_ABI_EXPORT void RefiiHostTraceMessage(const char*) {}
REFII_HOST_ABI_EXPORT uint64_t RefiiHostTraceZoneBegin(const char*) { return 0; }
REFII_HOST_ABI_EXPORT void RefiiHostTraceZoneEnd(uint64_t) {}
REFII_HOST_ABI_EXPORT void RefiiHostTracePlot(const char*, double) {}
REFII_HOST_ABI_EXPORT void RefiiHostTraceGpuPasses(const char*, uint32_t, const int64_t*,
                                                    const int64_t*, uint32_t) {}
#endif

/// Which heap owns an address, and its protection — the validity check for a pointer chain,
/// and the write-permission gate RefiiHostPoke enforces. Returns 0 when no heap covers it.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostHeapLookup(uint32_t addr, uint32_t* out_heap_base,
                                                   uint32_t* out_heap_size, uint32_t* out_page,
                                                   uint32_t* out_protect) {
    if (out_heap_base != nullptr) *out_heap_base = 0;
    if (out_heap_size != nullptr) *out_heap_size = 0;
    if (out_page != nullptr) *out_page = 0;
    if (out_protect != nullptr) *out_protect = 0;
    rex::memory::Memory* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    const rex::memory::BaseHeap* heap = memory->LookupHeap(addr);
    if (heap == nullptr) return 0;
    if (out_heap_base != nullptr) *out_heap_base = heap->heap_base();
    if (out_heap_size != nullptr) *out_heap_size = heap->heap_size();
    if (out_page != nullptr) *out_page = heap->page_size();
    uint32_t protect = 0;
    if (const_cast<rex::memory::BaseHeap*>(heap)->QueryProtect(addr, &protect) &&
        out_protect != nullptr) {
        *out_protect = protect;
    }

    return 1;
}

/// One deliberate guest word write — the flip-a-byte-and-look experiment. Refused unless the
/// page is heap-tracked and WRITABLE (a write to protected text would fault on the spot), and
/// only reachable through the loopback debug channel. Returns 1 on the write.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPoke(uint32_t addr, uint32_t value) {
    rex::memory::Memory* memory = SafeKernelMemory();
    if (memory == nullptr || addr < 0x10000u || addr >= 0xC0000000u) return 0;
    rex::memory::BaseHeap* heap =
        const_cast<rex::memory::BaseHeap*>(memory->LookupHeap(addr));
    uint32_t protect = 0;
    if (heap == nullptr || !heap->QueryProtect(addr, &protect) ||
        (protect & rex::memory::kMemoryProtectWrite) == 0) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const uint32_t swapped = __builtin_bswap32(value);
    std::memcpy(base + addr, &swapped, sizeof(swapped));
    return 1;
}

/// Dynamic probes: swap a guest function's dispatch slot for a recording thunk that counts the
/// call, keeps the last arguments and LR, and runs the original. The pointer swap is one
/// aligned store, so a racing call takes old or new whole — and `original` is written before
/// the swap and never cleared, so an in-flight thunk always has a target. What this is NOT:
/// a breakpoint. Nothing stops; the record is best-effort racy and that is the deal.
extern "C++" {
namespace probe {
constexpr int kSlots = 16;


// The device's constant shadow (`sub_8221A880`: VS cN at dev + 16*(N+120); `sub_82214E30`: PS
// cN at dev + 16*(N+376)), dev = *(0x83360364). 256 float4 registers per stage.
constexpr uint32_t kShadowBytes = 256u * 16u;

struct Slot {
    std::atomic<uint32_t> address{0};
    PPCFunc* original = nullptr;
    std::atomic<uint64_t> hits{0};
    std::atomic<uint32_t> last_lr{0};
    std::atomic<uint64_t> last_args[6] = {};
    // One-shot: the next call's return copies both shadows (raw, guest byte order).
    std::atomic<uint32_t> snapshot_armed{0};
    std::atomic<uint32_t> snapshot_ready{0};
    uint8_t snapshot[2 * kShadowBytes] = {};
};

Slot g_slots[kSlots];

template <int N>
void Thunk(PPCContext& ctx, uint8_t* base) {
    Slot& slot = g_slots[N];
    slot.hits.fetch_add(1, std::memory_order_relaxed);
    slot.last_lr.store(static_cast<uint32_t>(ctx.lr), std::memory_order_relaxed);
    slot.last_args[0].store(ctx.r3.u64, std::memory_order_relaxed);
    slot.last_args[1].store(ctx.r4.u64, std::memory_order_relaxed);
    slot.last_args[2].store(ctx.r5.u64, std::memory_order_relaxed);
    slot.last_args[3].store(ctx.r6.u64, std::memory_order_relaxed);
    slot.last_args[4].store(ctx.r7.u64, std::memory_order_relaxed);
    slot.last_args[5].store(ctx.r8.u64, std::memory_order_relaxed);
    if (slot.original != nullptr) {
        slot.original(ctx, base);
    }
    if (slot.snapshot_armed.exchange(0, std::memory_order_acq_rel) != 0) {
        const uint32_t device = GuestWordAt(base, refii::guest::Address(0x83360364u));
        if (device >= 0x10000u && device < 0xC0000000u) {
            std::memcpy(slot.snapshot, base + device + 0x780u, kShadowBytes);
            std::memcpy(slot.snapshot + kShadowBytes, base + device + 0x1780u, kShadowBytes);
            slot.snapshot_ready.store(1, std::memory_order_release);
        }
    }
}

template <int... Is>
constexpr std::array<PPCFunc*, kSlots> MakeThunks(std::integer_sequence<int, Is...>) {
    return {&Thunk<Is>...};
}

const std::array<PPCFunc*, kSlots> kThunks = MakeThunks(std::make_integer_sequence<int, kSlots>{});
}  // namespace probe
}  // extern "C++"


/// Arms a probe on a guest function START address. Returns the slot, -1 when all 16 are used,
/// -2 when the address has no dispatch entry (not a recompiled function start), -3 with no
/// runtime. Probing the same address twice returns its existing slot.
REFII_HOST_ABI_EXPORT int32_t RefiiHostProbeSet(uint32_t addr) {
    rex::Runtime* runtime = rex::Runtime::instance();
    if (runtime == nullptr || runtime->function_dispatcher() == nullptr) return -3;
    for (int i = 0; i < probe::kSlots; ++i) {
        if (probe::g_slots[i].address.load(std::memory_order_relaxed) == addr) return i;
    }

    PPCFunc* original = runtime->function_dispatcher()->GetFunction(addr);
    if (original == nullptr) return -2;
    for (int i = 0; i < probe::kSlots; ++i) {
        uint32_t expected = 0;
        if (probe::g_slots[i].address.compare_exchange_strong(expected, addr)) {
            probe::g_slots[i].original = original;
            probe::g_slots[i].hits.store(0, std::memory_order_relaxed);
            if (!runtime->function_dispatcher()->SetFunction(addr, probe::kThunks[i])) {
                probe::g_slots[i].address.store(0, std::memory_order_relaxed);
                return -2;
            }

            return i;
        }
    }

    return -1;
}

/// Restores the original dispatch entry. `original` stays set for any in-flight thunk.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostProbeClear(int32_t slot) {
    if (slot < 0 || slot >= probe::kSlots) return 0;
    const uint32_t addr = probe::g_slots[slot].address.load(std::memory_order_relaxed);
    if (addr == 0) return 0;
    rex::Runtime* runtime = rex::Runtime::instance();
    if (runtime != nullptr && runtime->function_dispatcher() != nullptr) {
        runtime->function_dispatcher()->SetFunction(addr, probe::g_slots[slot].original);
    }

    probe::g_slots[slot].address.store(0, std::memory_order_relaxed);
    return 1;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostProbeRead(int32_t slot, uint32_t* out_addr,
                                                  uint64_t* out_hits, uint32_t* out_last_lr,
                                                  uint64_t* out_args, uint32_t args_capacity) {
    if (slot < 0 || slot >= probe::kSlots) return 0;
    const probe::Slot& held = probe::g_slots[slot];
    if (out_addr != nullptr) *out_addr = held.address.load(std::memory_order_relaxed);
    if (out_hits != nullptr) *out_hits = held.hits.load(std::memory_order_relaxed);
    if (out_last_lr != nullptr) *out_last_lr = held.last_lr.load(std::memory_order_relaxed);
    for (uint32_t i = 0; i < args_capacity && i < 6u; ++i) {
        out_args[i] = held.last_args[i].load(std::memory_order_relaxed);
    }

    return 1;
}

/// Arms a one-shot constant snapshot on an armed probe: the next return of the probed function
/// copies the device's VS and PS constant shadows. Returns 1 when armed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostProbeSnapshotArm(int32_t slot) {
    if (slot < 0 || slot >= probe::kSlots || probe::g_slots[slot].address.load() == 0) return 0;
    probe::g_slots[slot].snapshot_ready.store(0, std::memory_order_relaxed);
    probe::g_slots[slot].snapshot_armed.store(1, std::memory_order_release);
    return 1;
}

/// The snapshot, as floats: VS c0..c255 then PS c0..c255, four lanes each (2048 floats).
/// Returns 1 when one has been taken since the last arm.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostProbeSnapshotRead(int32_t slot, float* out, uint32_t capacity) {
    if (slot < 0 || slot >= probe::kSlots || out == nullptr || capacity < 2048u) return 0;
    const probe::Slot& held = probe::g_slots[slot];
    if (held.snapshot_ready.load(std::memory_order_acquire) == 0) return 0;
    for (uint32_t i = 0; i < 2048u; ++i) {
        uint32_t raw;
        std::memcpy(&raw, held.snapshot + i * 4u, sizeof(raw));
        raw = __builtin_bswap32(raw);
        std::memcpy(out + i, &raw, sizeof(raw));
    }
    return 1;
}

/// The last debug-call / debug-lua result the tick pump recorded — see guest_tick_pump.cpp's
/// drain. The sequence number says whether a posted call has finished yet.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDebugCallResult(uint64_t* out_seq, uint32_t* out_addr,
                                                           uint64_t* out_r3, char* out_note,
                                                           uint32_t note_capacity) {
    return refii::vfs::DebugCallResult(out_seq, out_addr, out_r3, out_note, note_capacity);
}

/// Every socket in the kernel handle table: handle, bound guest port, host native handle.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGuestSockets(uint32_t* out_handles,
                                                        uint32_t* out_ports,
                                                        uint64_t* out_native,
                                                        uint32_t capacity) {
    rex::system::KernelState* state = rex::system::kernel_state();
    if (state == nullptr) return 0;

    auto sockets = state->object_table()->GetObjectsByType<rex::system::XSocket>();
    uint32_t written = 0;
    for (const auto& socket : sockets) {
        if (written >= capacity) break;
        if (out_handles != nullptr) out_handles[written] = socket->handle();
        if (out_ports != nullptr) out_ports[written] = socket->bound_port();
        if (out_native != nullptr) out_native[written] = socket->native_handle();
        ++written;
    }

    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuScreenId(void) {
    return refii::vfs::MenuScreenId();
}

/// One captured row's DISPLAY LABEL — String2, the localised text with its decorations,
/// decoded from the wide staging string at capture. Returns the same total count as
/// RefiiHostGetMenuRow, so the two calls describe one list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRowLabel(uint32_t index, char* out_label,
                                                        uint32_t label_capacity) {
    return refii::vfs::MenuRowLabel(index, out_label, label_capacity);
}

/// The UIDs the guest considers DEAD — destroyed placements it will not respawn.
///
/// WHY THIS EXISTS. We draw the region's build-time copy of a placement and hide it while the
/// guest has a live entity for that record. Smash a barrel and the guest drops its entity, so
/// the copy comes back and stands where the smashed one was — a ghost. "No live entity" is not
/// "should be visible"; the missing term is the guest's own statement that the placement is
/// gone, and it keeps one.
///
/// `sub_82366088` inserts on every destroy. Verified in the disassembly, offsets measured from
/// the ENTITY and the ENTITY MANAGER respectively:
///
///   8236627c  ld   r11, 0x14(r31)   ; r31 = entity  -> the 64-bit UID, the map KEY
///   8236628c  lwz  r11, 0x84(r31)   ; LayerHash, the map VALUE
///   82366298  addi r4, r30, 0x94    ; r30 = EntityManager -> the map at +148
///
/// And the node layout from the save writer `sub_82367398`, which walks the same tree:
/// `_Myhead` at map+4 (mgr+152), iteration ends when the node equals it, key `__int64` at
/// node+12, LayerHash at node+20. MSVC `_Tree`: node+0 `_Left`, +4 `_Parent`, +8 `_Right`,
/// +24 `_Color`, +25 `_Isnil`.
///
/// It PERSISTS — written to the save as `DeadEntities`/`DeadEntity` and read back on load — and
/// deactivating a layer purges that layer's entries (`sub_82366A30`), so entries disappearing
/// is meaningful rather than stale: a layer cycling off and on legitimately restores its broken
/// props, exactly as the guest does for itself.
///
/// Writes up to `max_uids`; returns how many were written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeadEntities(uint64_t* out_uids, uint32_t max_uids) {
    if (out_uids == nullptr || max_uids == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t kEntityManagerGlobal = refii::guest::Address(0x83496B28u);
    const uint32_t manager = read32(kEntityManagerGlobal);
    if (!plausible(manager)) {
        return 0u;
    }

    const uint32_t head = read32(manager + 152u);   // map+4, the end sentinel
    if (!plausible(head)) {
        return 0u;
    }

    const uint32_t stated = read32(manager + 156u); // _Mysize

    // WALKED WITH AN EXPLICIT STACK AND A HARD CAP. This is a live guest tree read without a
    // lock, so a rotation in flight can hand back a cycle; a recursive or unbounded walk would
    // hang the caller rather than return something merely wrong. The cap is the size the
    // container states, bounded again so a torn `_Mysize` cannot widen it.
    constexpr uint32_t kMaxNodes = 8192u;
    uint32_t budget = stated < kMaxNodes ? stated + 1u : kMaxNodes;

    uint32_t stack[64];
    uint32_t depth = 0u;
    uint32_t written = 0u;

    const uint32_t root = read32(head + 4u);        // _Myhead->_Parent
    if (plausible(root) && root != head) {
        stack[depth++] = root;
    }

    while (depth != 0u && written < max_uids && budget-- != 0u) {
        const uint32_t node = stack[--depth];
        if (!plausible(node) || node == head) {
            continue;
        }

        // The sentinel is the only node flagged nil; anything else carries a live pair.
        if (GuestByteAt(base, node + 25u) == 0u) {
            const uint64_t hi = read32(node + 12u);
            const uint64_t lo = read32(node + 16u);
            out_uids[written++] = (hi << 32) | lo;
        }

        const uint32_t left = read32(node + 0u);
        const uint32_t right = read32(node + 8u);
        if (depth + 2u <= 64u) {
            if (plausible(left) && left != head) {
                stack[depth++] = left;
            }
            if (plausible(right) && right != head) {
                stack[depth++] = right;
            }
        }
    }

    return written;
}

/// The meshes a `CECGraphicAppearanceStaticMultipleMesh` (component 5) draws.
///
/// THE MISSING GEOMETRY ROUTE. `RefiiHostGetEntities` reports one model hash per entity, taken
/// from the single-slot appearance components (4 `+0x18`, 6 `+0x20`, 10/11 `+0x14`). Component 5
/// has no such slot — it holds a VECTOR of mesh records — so every entity using one reported
/// zero and read as "not drawable". That is what buildings use: the standing static copies in a
/// region are named `… interior` / `… exterior`, which are two records of one MeshRecords list.
///
/// Verified in `sub_828F9BA0` (component 5's InitialiseFromDefinition), offsets measured from
/// the COMPONENT pointer and from the RECORD pointer respectively:
///
///   comp5+0x10  vector object      comp5+0x14 begin, +0x18 end, +0x1C capacity
///   stride 64                      `(end - begin) >> 6` in both the init and the draw walk
///   record+0x00 render handle, initialised to -1
///   record+0x04 `Mesh`             <- what this returns
///   record+0x08 `ShadowMesh`
///   record+0x3C `Interior` flag    (`*(_BYTE *)(rec + 60)` in the draw walk `sub_828F9F90`)
///
/// Each record's hash comes from a GDB entry resolved per `MeshRecords` element, so it is a
/// lower-cased resource-name hash exactly like `ModelFile` — the same index resolves it.
///
/// Writes up to `max_hashes`; returns how many were written. `out_total` receives the record
/// count the component states, which may exceed what was written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMeshParts(uint32_t entity, uint32_t* out_hashes,
                                      uint32_t max_hashes, uint32_t* out_total) {
    if (out_total != nullptr) {
        *out_total = 0u;
    }

    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    constexpr uint8_t kStaticMultipleMeshId = 5u;

    if (((read32(entity + 0x24u + (kStaticMultipleMeshId / 32u) * 4u) >>
          (kStaticMultipleMeshId % 32u)) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kStaticMultipleMeshId);

    if (!plausible(component)) {
        return 0u;
    }

    // CHECKED AS A TRIPLE, for the reason the morph list is: a vector that grows publishes its
    // pointers one at a time, so `begin <= end <= capacity` is the only statement that a pair
    // cannot satisfy by accident mid-write.
    const uint32_t begin = read32(component + 0x14u);
    const uint32_t end = read32(component + 0x18u);
    const uint32_t capacity = read32(component + 0x1Cu);

    if (!plausible(begin) || end <= begin || capacity < end ||
        ((end - begin) % 64u) != 0u || ((capacity - begin) % 64u) != 0u ||
        (end - begin) > 0x10000u) {
        return 0u;
    }

    if (out_total != nullptr) {
        *out_total = (end - begin) / 64u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + 64u <= end && written < max_hashes; at += 64u) {
        const uint32_t hash = read32(at + 4u);
        if (hash != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return written;
}

/// The INTERIOR records of a multi-mesh appearance (component 5): the `Mesh` hashes of the
/// records whose flag word +0x38 carries 0x2000 (`sub_828F9BA0` 0x828F9E10: set when the record's
/// GDB `Interior` is non-zero). The consumer `sub_828F9F90` ORs it into the descriptor (0x828FA55C)
/// and the twin init shifts it to twin+8 bit 14, which is what the weather's roof walk keeps
/// (`sub_82A78528` 0x82A78694): these meshes are the rain's and snow's roofs.
/// Same vector and checks as RefiiHostGetEntityMeshParts; returns the hashes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityInteriorMeshes(uint32_t entity, uint32_t* out_hashes,
                                                              uint32_t max_hashes) {
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    constexpr uint8_t kStaticMultipleMeshId = 5u;
    if (((GuestWordAt(base, entity + 0x24u + (kStaticMultipleMeshId / 32u) * 4u) >>
          (kStaticMultipleMeshId % 32u)) & 1u) == 0u) {
        return 0u;
    }
    const uint32_t component = EntityComponent(base, entity, kStaticMultipleMeshId);
    if (component < 0x10000u || component >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, component + 0x14u);
    const uint32_t end = GuestWordAt(base, component + 0x18u);
    const uint32_t capacity = GuestWordAt(base, component + 0x1Cu);
    if (begin < 0x10000u || end <= begin || capacity < end || ((end - begin) % 64u) != 0u ||
        ((capacity - begin) % 64u) != 0u || (end - begin) > 0x10000u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at + 64u <= end && written < max_hashes; at += 64u) {
        const uint32_t hash = GuestWordAt(base, at + 4u);
        if (hash != 0u && (GuestWordAt(base, at + 0x38u) & 0x2000u) != 0u) {
            out_hashes[written++] = hash;
        }
    }
    return written;
}

/// The DISPLACEMENT parts of a composite character: the model hashes of component 17's built
/// records (+0x38 begin, +0x3C end, +0x40 capacity, stride 24) whose +0x15 byte is set — the
/// definition's GDB `DisplacementMeshes` (`sub_8217FA50`). The model create for such a record
/// carries flag 0x800 (`sub_8220DE88` 0x8220E09C), which moves its twin to the displacement
/// renderer's layer alone (`sub_82AA3C78`): drawn only as distortion. Returns the hashes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityDisplacementParts(uint32_t entity, uint32_t* out_hashes,
                                                                 uint32_t max_hashes) {
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t morph = EntityComponent(base, entity, 17u);
    if (morph < 0x10000u || morph >= 0xC0000000u) {
        return 0u;
    }
    const uint32_t begin = GuestWordAt(base, morph + 0x38u);
    const uint32_t end = GuestWordAt(base, morph + 0x3Cu);
    const uint32_t capacity = GuestWordAt(base, morph + 0x40u);
    if (begin < 0x10000u || end <= begin || capacity < end || ((end - begin) % 24u) != 0u ||
        ((capacity - begin) % 24u) != 0u || (end - begin) > 0x18000u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at + 24u <= end && written < max_hashes; at += 24u) {
        const uint32_t hash = GuestWordAt(base, at);
        if (hash != 0u && GuestByteAt(base, at + 0x15u) != 0u) {
            out_hashes[written++] = hash;
        }
    }
    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMorphParts(uint32_t entity, uint32_t* out_hashes,
                                      uint32_t max_hashes, uint32_t* out_built) {
    if (out_built != nullptr) {
        *out_built = 0u;
    }

    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    // Components, through the entity's own lookup contract: presence bitmask of 8 dwords at
    // entity+0x24, then either the byte index table at entity+0x8C or a walk of the {id, ptr}
    // pairs. Same shape RefiiHostGetEntities uses for the graphic-appearance slots.
    const auto component = [&](uint8_t id) -> uint32_t {
        return EntityComponent(base, entity, id);
    };

    // THE EYES ARE NOT MORPH PARTS. They are component 7 `CECGraphicAppearanceEyes`' own two
    // render instances: the right eye model hash at comp7+0x18 (`sub_8259D380`, `result[6]`)
    // and the left at comp7+0x28 (`sub_8259D3F8`, `result[10]`, which already holds the RIGHT
    // model when `BodgeEyes` at comp7+81 is set). The eye-case fields the GDB also names are
    // read and discarded by the guest (`sub_825B5160` 0x825B56E0/0x825B56F4) and are not
    // here. Both are appended after the parts so the host can hang them on
    // `Shadow_R_Eye`/`Shadow_L_Eye` the way the guest does (`sub_82209480`). Nothing is
    // appended when the component is absent or its `Enabled` byte at +0x60 is clear.
    //
    // READ BEFORE THE MORPH COMPONENT, NOT INSIDE IT. `CreatureHeroChild` states its whole
    // body as ONE ModelFile on component 6 and its eyes on component 7; its morph component
    // has an empty source list. The child therefore never had a morph read at all, and its
    // eyes — hung by the guest on the same rig, `sub_8259CF80` stores them at comp7+0x18/+0x28
    // for any entity with the component — were never crossed. An entity with eyes and no
    // morph parts answers with the two eyes alone.
    constexpr uint8_t kEyesComponentId = 7u;
    const uint32_t eyes = component(kEyesComponentId);

    const auto append_eyes = [&](uint32_t written) -> uint32_t {
        if (!plausible(eyes) || read8(eyes + 0x60u) == 0u) {
            return written;
        }
        const uint32_t right = read32(eyes + 0x18u);
        const uint32_t left = read32(eyes + 0x28u);
        if (right != 0u && written < max_hashes) {
            out_hashes[written++] = right;
        }
        if (left != 0u && written < max_hashes) {
            out_hashes[written++] = left;
        }
        return written;
    };

    constexpr uint8_t kMorphComponentId = 17u;
    const uint32_t morph = component(kMorphComponentId);
    if (!plausible(morph)) {
        return append_eyes(0u);
    }

    // THE BUILT LIST IS THE DRESSED CHARACTER, AND IT IS THE ONE TO READ.
    //
    // The source list at +0x28 holds ONLY what the CharacterCreationRecord named — head, torso,
    // legs, eyes. Worn clothing is never in it and never can be: `sub_8217EDE0` appends garments
    // straight into the BUILT vector, reading them from a different component entirely —
    // id 15, `CECAppearanceModifierManager`, whose applied-modifier list at +0x10..+0x14 is the
    // outfit. Equipping does not touch the source list; `sub_824BECF8` only marks
    // CECGraphicAppearance dirty and the next rebuild picks it up.
    //
    // This used to return the source list on the reasoning that our guest never draws, so the
    // built list might never be filled. THAT IS MEASURED FALSE: we see 147-245 built records
    // against 84 source parts. The difference is the clothes, and the hero was rendering naked.
    // The rebuild is BUILD-ON-DEMAND, not per-frame: `sub_8220DE88` calls `sub_8217FA50` only
    // when the render-instance vector at comp+0x48..+0x4C is empty (what the clear
    // `sub_825B8C70` leaves behind, 0x825b8cd0) or when its caller sets bit 0x40 of ctx+0x30
    // — every other trigger is a discrete appearance change, so gear changes are when this
    // list moves, not movement or streaming.
    //
    // ORDER IS FREE. The rebuild ends in `sub_825BC9C8(...)`, a sort by record+0x10
    // (ClusterSortLayer) — body parts author -100 so they sort first — so reading the vector in
    // array order already gives the guest's own draw order.
    //
    // Record layout, 24 bytes: +0x00 model hash, +0x10 i32 sort layer, +0x14 u8 isBodyPart
    // (0 = GARMENT), +0x15 u8 has displacement mesh, +0x16 u8 authored-source flag.
    // +0x04..+0x0F IS NOT INLINE DATA — it is an MSVC `_Tree` (std::set<string>) of the body
    // clusters the garment hides: +0x08 is `_Myhead`, a pointer to a heap sentinel node.
    // DO NOT DEREFERENCE IT from here; reading those names means walking a red-black tree of
    // guest heap nodes that the rebuild frees.
    const uint32_t built_begin = read32(morph + 0x38u);
    const uint32_t built_end = read32(morph + 0x3Cu);
    const uint32_t built_capacity = read32(morph + 0x40u);

    // THE TRIPLE IS CHECKED AS A TRIPLE. `sub_824C6CC0` grows the vector by freeing the old
    // block, publishing `begin` (0x824c6e2c), then `capacity` (0x824c6e4c), then `end`
    // (0x824c6e54) — eleven instructions in which `begin` addresses the new block while `end`
    // still addresses the freed one, so `end - begin` is the difference of two unrelated heap
    // pointers and can be any size or sign. `begin <= end <= capacity` is the container's own
    // invariant at rest, so a triple that breaks it is torn by definition.
    const bool built_usable = plausible(built_begin) && built_end > built_begin &&
                              built_capacity >= built_end &&
                              ((built_end - built_begin) % 24u) == 0u &&
                              ((built_capacity - built_begin) % 24u) == 0u &&
                              (built_end - built_begin) <= 0x18000u;

    if (built_usable) {
        const uint32_t count = (built_end - built_begin) / 24u;
        if (out_built != nullptr) {
            *out_built = count;
        }

        uint32_t written = 0u;
        for (uint32_t at = built_begin; at + 24u <= built_end && written < max_hashes;
             at += 24u) {
            const uint32_t hash = read32(at);
            if (hash != 0u) {
                out_hashes[written++] = hash;
            }
        }

        if (written != 0u) {
            return append_eyes(written);
        }
    }

    // FALLBACK: the source list, stride 4. Reached when the draw path has not run for this
    // entity yet, which is a real state on the frame a character is spawned — the naked body is
    // a better answer than nothing, and the next frame carries the clothes.
    const uint32_t begin = read32(morph + 0x28u);
    const uint32_t end = read32(morph + 0x2Cu);
    if (!plausible(begin) || end <= begin || ((end - begin) % 4u) != 0u ||
        (end - begin) > 0x4000u) {
        return append_eyes(0u);
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + 4u <= end && written < max_hashes; at += 4u) {
        const uint32_t hash = read32(at);
        if (hash != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return append_eyes(written);
}

/// One live character's SKELETON POSE, so Godot can drive a Skeleton3D from the guest.
///
/// The placement feed puts a prop where the guest says it is; this is the rest of what a
/// CHARACTER needs. Like the placement read it calls no guest code — the pose the animation
/// system evaluated is left in a stable, component-owned heap array, and we copy it out.
///
/// WHERE IT IS. The animated-mesh component is id 6 (`CECGraphicAppearanceAnimatedMesh`),
/// and it owns a vector of 48-byte bone transforms at comp+0x2C: begin +0x30, end +0x34, so
/// the bone count is `(end - begin) / 48`.
///
/// WHY THAT ARRAY AND NOT THE POSE OBJECT AT comp+0x24. Both exist and only one is the live
/// finished pose. `sub_821F2C78` XMemCpys the freshly evaluated pose INTO comp+0x30 —
/// `XMemCpy(*(a1+48), *(a2+4), 48 * ((*(a1+52) - *(a1+48)) / 48))` — and its caller
/// `sub_8257D8B8` then runs `sub_821F57D0(*(a1+48), *(a1+148), a1+112, 0)` to turn it from
/// local into MODEL space in place, stamps `*(a1+20) = 2` and sets the dirty byte at
/// `*(a1+228)`. So this array is per-frame, and by the time the frame is over it is already
/// object-space: no parent walk on our side.
///
/// `comp+0x14` STATES the space — 1 local, 2 model — and it is returned rather than assumed.
/// A pose read mid-update is local, and silently treating those bones as model-space is the
/// kind of plausible-and-wrong result that reads as a rigging bug.
///
/// ELEMENT LAYOUT, 48 bytes: quaternion at +0 (w in lane 3), translation at +0x10, scale at
/// +0x20. NOTE this is NOT Havok's order — `hkQsTransform` is translation first, and the two
/// are the same size, so one reader cannot serve both.
///
/// The PARENT table comes from the skeleton at comp+0x94: entries of 8 bytes between
/// skel+0x2C and skel+0x30, parent index (-1 for a root) in the first dword. Bones are
/// ordered parent-before-child.
///
/// Fills 12 floats per bone and one parent index per bone. Returns the bone count, or 0 when
/// the entity carries no animated mesh — which is the common case and not an error.
static uint32_t RefiiHostReadEntityRig(uint32_t entity, float* out_transforms,
                                       int32_t* out_parents, uint32_t max_bones,
                                       uint32_t* out_space, uint32_t* out_torn, bool rest);

/// `out_torn` is set when the read could not be made coherent — see the snapshot comment in
/// `RefiiHostReadEntityRig`. The bones are still filled in on a torn read, because the
/// caller is better placed to decide between last frame's pose and this one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPose(uint32_t entity, float* out_transforms, int32_t* out_parents,
                                uint32_t max_bones, uint32_t* out_space, uint32_t* out_torn) {
    return RefiiHostReadEntityRig(entity, out_transforms, out_parents, max_bones, out_space,
                                  out_torn, false);
}

/// The REST pose of a character's rig — the bind pose Godot needs to build its skin.
///
/// Separate from the live pose because it is read ONCE per model and never changes, while
/// the live pose is read every frame. Same bone ordering and same parent table, so the two
/// index the same rig.
///
/// It is stored LOCAL and has to be composed through the parent chain to be useful, which is
/// the opposite of the live pose. Verified rather than assumed: `sub_82B73170` computes the
/// count as `(*(skel+148) - *(skel+144)) / 48`, copies those elements to a scratch buffer,
/// and runs the local-to-model converter `sub_82B734B0` ON THE COPY — you do not convert
/// something that is already converted.
///
/// Bones come back parent-before-child, so one forward pass composes them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityRestPose(uint32_t entity, float* out_transforms,
                                    int32_t* out_parents, uint32_t max_bones) {
    return RefiiHostReadEntityRig(entity, out_transforms, out_parents, max_bones, nullptr,
                                  nullptr, true);
}

/// The shared body of the two rig reads above.
///
/// One implementation, because the only difference between them is WHICH 48-byte array on
/// the same component they take — the entity walk, the component lookup, the bounds checks
/// and the parent table are identical, and two copies of that would be two things to keep
/// in agreement.
static uint32_t RefiiHostReadEntityRig(uint32_t entity, float* out_transforms,
                                       int32_t* out_parents, uint32_t max_bones,
                                       uint32_t* out_space, uint32_t* out_torn, bool rest) {
    constexpr uint8_t kAnimatedMeshId = 6u;

    if (out_space != nullptr) {
        *out_space = 0u;
    }

    // Clean by default: every path that returns without reaching the snapshot returns no
    // bones at all, and "torn" must not read as true for a rig that was never read.
    if (out_torn != nullptr) {
        *out_torn = 0u;
    }

    if (out_transforms == nullptr || out_parents == nullptr || max_bones == 0u) {
        return 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    if (!plausible(entity)) {
        return 0u;
    }

    // Same component lookup the model-hash read uses: the id bitmask gates it, the
    // accelerator resolves it in one step, and the linear scan is the fallback for an
    // entity that has not built one.
    if (((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kAnimatedMeshId);

    if (!plausible(component)) {
        return 0u;
    }

    // The parent table is the skeleton's, and it has to agree with whichever array we read
    // or the two are describing different rigs.
    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    // The live pose hangs off the COMPONENT; the rest pose off the SKELETON. Same element
    // size, same bone order, different owner — and quoting one against the other's base is
    // exactly the mistake that has cost this project a shipped-wrong offset before.
    // The live pose is the published block, not the component's working array.
    //
    // `sub_821FF010` is the guest's publish: it allocates a fresh block (`sub_821F5C18`),
    // XMemCpy's the finished pose into it, applies the attachment override, and swaps it into
    // the shared_ptr at `comp+0x24`. The attachment override at `comp+0x3C`/`+0x40` is written
    // into the published copy only (`821ff0e0`..`821ff11c`), and the published block is never
    // rewritten in place, unlike `comp+0x30`.
    //
    // Layout: holder = `*(comp+0x24)` (a shared_ptr's px), block = `*holder`, bone buffer =
    // `*(block+0x10)`, and `block+8` is the skeleton identity tag every render consumer checks
    // against `skel+0x14`. The count is the publisher's own — `(skel+0x30 - skel+0x2C) >> 3` —
    // not `block+0xC`, which nothing in the image reads.
    //
    // Falls back to the working array: a component publishes only through `sub_821F56F8`, and
    // `sub_8257D8B8` poses without ever calling it, so a block is not guaranteed to exist.
    uint32_t pose = 0u;
    uint32_t pose_end = 0u;
    uint32_t holder = 0u;
    uint32_t published = 0u;
    bool from_block = false;

    // A ragdoll's pose exists only at the end of the tick, and only in the working array.
    //
    // The publish runs twice per tick for an active ragdoll. `sub_8220D1B8` publishes the clip
    // pose unconditionally (`8220dbe8 .. bl sub_821F56F8` @0x8220dbf0); Havok steps; then
    // `sub_8220A700` copies the bodies over the array (`sub_8257DBC0` @0x8220abd8), stamps
    // `comp+0x10 := 2` (`8257dc94 stw r11,0x10(r31)`) and publishes again (@0x8220ad48). The
    // guest draws after the second; an asynchronous reader can see the first.
    //
    // So a physics-owned rig reads `comp+0x30` and only while `comp+0x10 == 2`. `sub_821F2C78`
    // stamps 1 when it copies the next clip pose in, so 2 brackets the physics result; the one
    // window it lags (the XMemCpy before that stamp) leaves a local-space array, which the
    // double read or the host's space vote refuses.
    const bool physics = !rest && EntityPhysicsOwnsPose(base, entity);

    if (!rest && !physics) {
        holder = read32(component + 0x24u);
        if (plausible(holder)) {
            published = read32(holder);
            if (plausible(published) && read32(published + 8u) == read32(skeleton + 0x14u)) {
                const uint32_t buffer = read32(published + 0x10u);
                const uint32_t table_begin = read32(skeleton + 0x2Cu);
                const uint32_t table_finish = read32(skeleton + 0x30u);
                if (plausible(buffer) && table_finish > table_begin) {
                    pose = buffer;
                    pose_end = buffer + (((table_finish - table_begin) >> 3) * 48u);
                    from_block = true;
                }
            }
        }
    }

    if (!from_block) {
        pose = rest ? read32(skeleton + 0x90u) : read32(component + 0x30u);
        pose_end = rest ? read32(skeleton + 0x94u) : read32(component + 0x34u);
    }

    if (!plausible(pose) || pose_end <= pose) {
        return 0u;  // sized but never filled — the character has not been posed yet
    }

    uint32_t bones = (pose_end - pose) / 48u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    const uint32_t bone_table = read32(skeleton + 0x2Cu);
    const uint32_t bone_table_end = read32(skeleton + 0x30u);
    if (!plausible(bone_table) || bone_table_end <= bone_table ||
        (bone_table_end - bone_table) / 8u < bones) {
        return 0u;
    }

    // A COHERENT SNAPSHOT, because the guest rewrites this array underneath us.
    //
    // The guest publishes each finished pose into a fresh block and swaps it into the
    // shared_ptr at `comp+0x24` (`sub_821FF010` -> `sub_821F5C18`); its own renderer keeps the
    // last two and interpolates between them (`sub_8222E2B8`, blocks at
    // `renderObj+0x200`/`+0x204`). `comp+0x30` itself is rewritten in place — `sub_821F2C78`
    // XMemCpy's the pose in, modifiers mutate it, and `sub_821F57D0` converts it to model space
    // bone by bone over the same memory — so the fallback path below reads it twice.
    //
    // AND A TORN RIG IS NOT A SMALL ERROR. During `sub_821F57D0` the array holds two
    // different SPACES at once — bones behind its cursor are model-space, bones ahead of it
    // are still parent-relative. Read then, part of the skeleton is interpreted in the wrong
    // space entirely, which is a character exploding for one frame rather than a bone being
    // slightly stale.
    //
    // NEITHER MARKER THE COMPONENT CARRIES CAN GATE THIS, and both fail the same way. The
    // flag at `comp+0x14` is stamped to 1 at the TAIL of `sub_821F2C78` (0x821F2F7C), so it
    // still reads 2 from the previous frame for the whole duration of the memcpy — a flag
    // check would accept exactly the tear it exists to catch. `dword_83319650` trails the
    // entire world update, so it does not move during one either. A marker that lags its own
    // payload is worse than no marker, because it reports confidence it has not earned.
    //
    // So: read the payload twice and require the two to agree, which assumes nothing about
    // either marker. This is what `RefiiHostGetCamera` already does for the same reason. An
    // idle character memcpy'd back to identical values compares equal, and that is correct —
    // identical bytes are not torn in any sense that matters.
    constexpr uint32_t kMaxSnapshotBones = 256u;
    if (bones > kMaxSnapshotBones) {
        bones = kMaxSnapshotBones;
    }

    static thread_local uint8_t first[kMaxSnapshotBones * 48u];
    static thread_local uint8_t second[kMaxSnapshotBones * 48u];
    const size_t span = (size_t)bones * 48u;

    const uint32_t latch_before = physics ? read32(component + 0x10u) : 2u;
    bool coherent = false;
    if (from_block) {
        // The holder's pointer is the generation counter. The publisher never writes a block in
        // place, so the copy can only be spoiled by a swap; an unchanged holder after the copy
        // proves the block was current for the whole read.
        for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
            CopyGuest(base, pose, first, span);
            coherent = read32(holder) == published;
            if (!coherent) {
                published = read32(holder);
                if (!plausible(published) ||
                    read32(published + 8u) != read32(skeleton + 0x14u)) {
                    return 0u;  // the rig changed skeletons mid-read; nothing to draw
                }

                pose = read32(published + 0x10u);
                if (!plausible(pose)) {
                    return 0u;
                }
            }
        }
    } else {
        // The working array at `comp+0x30` is rewritten in place, and during `sub_821F57D0` it
        // holds two different spaces at once. No generation to lean on: read it twice and
        // require agreement. Reached for the rest pose, physics rigs and components that never publish.
        for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
            CopyGuest(base, pose, first, span);
            CopyGuest(base, pose, second, span);
            coherent = std::memcmp(first, second, span) == 0;
        }
    }

    if (physics && (latch_before != 2u || read32(component + 0x10u) != 2u)) {
        coherent = false;  // mid-tick: the array holds this tick's clip pose, not the bodies
    }

    // REPORTED, NOT HIDDEN. A caller that knows the read was torn can hold last frame's pose
    // for that entity, which is always better than drawing a rig assembled from two of them.
    // Swallowing it here would leave the host unable to tell a stale pose from a broken one.
    if (out_torn != nullptr) {
        *out_torn = coherent ? 0u : 1u;
    }

    // 12 big-endian floats per bone; the parent index is the first word of each 8-byte
    // bone-table entry. The table is read in one guarded copy, not one per bone.
    rex::memory::copy_and_swap_32_unaligned(out_transforms, first, (size_t)bones * 12u);

    static thread_local uint8_t table[kMaxSnapshotBones * 8u];
    CopyGuest(base, bone_table, table, (size_t)bones * 8u);
    for (uint32_t i = 0u; i < bones; ++i) {
        uint32_t parent = 0u;
        std::memcpy(&parent, table + (i * 8u), sizeof(parent));
        out_parents[i] = (int32_t)__builtin_bswap32(parent);
    }

    if (out_space != nullptr) {
        *out_space = read32(component + 0x14u);
    }

    return bones;
}

/// The guest's own NAME for each of a character's bones.
///
/// THIS IS WHAT MAKES A CHARACTER DRAWABLE. The `.mdl` numbers its bones one way and the
/// guest's runtime skeleton numbers them another — 143 against 161 for the child hero, and
/// 39 against 39 with only 11 of the parent links agreeing for a chicken. A mesh's
/// BLENDINDICES address the model's table, so binding them to a skeleton built in the
/// guest's order attaches every vertex to the wrong bone and the character renders as a fan
/// of spikes. The name is the only key both sides share.
///
/// WHERE THE CHARACTERS ARE. `sub_821B5508` is the guest's own find-bone-by-name and is the
/// ground truth for all of this:
///
///     v3 = *(skel + 44);                    // skel+0x2C, bone table begin
///     v4 = (*(skel + 48) - v3) >> 3;        // 8 bytes per entry
///     for ( i = (int *)(v3 + 4); ; i += 2 ) // &bone[0].name, stride 8
///         if ( *i == v5 ) ...               // *a2, compared WITHOUT a dereference
///
/// so the dword at `bone + 4` IS the string node, not a handle to be resolved. The node's
/// own layout comes from the comparator it falls back on when the pointers differ,
/// `LHString_CBasicString_char_operator` @ 0x8226D790, which is called with those same two
/// node pointers:
///
///     return *(_DWORD *)(a1 + 4) == *(_DWORD *)(a2 + 4)
///         && sub_8226D7F8(*(_BYTE **)a1, *(_BYTE **)a2) == 0;
///
/// — `node + 4` is the byte length and `*(char**)node` is NUL-terminated ASCII. Plain
/// characters, in memory, in the shipped build: a strcmp cannot work otherwise.
///
/// A null node is legitimate and means an unnamed bone, not a failure — the guest degrades
/// it to the shared empty string at 0x8208FFDF, which is a single 0x00 byte. Names are also
/// INTERNED through a global set, so two bones with the same name share one node; that is
/// why the scan above has a pointer fast path, and it is why a caller must never key a map
/// on the pointer.
///
/// Fills `max_bones * stride` bytes, one NUL-padded name per bone. Returns the bone count,
/// or 0 when the entity carries no animated mesh.
///
/// The STRIDE is the caller's, not a constant here, so there is exactly one owner of it. A
/// matching pair of constants either side of a P/Invoke is a thing that drifts, and it
/// drifts silently: the reader simply starts finding the tail of the previous name.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityBoneNames(uint32_t entity, char* out_names, uint32_t stride,
                                     uint32_t max_bones) {
    constexpr uint8_t kAnimatedMeshId = 6u;

    if (out_names == nullptr || max_bones == 0u || stride < 2u) {
        return 0u;
    }

    std::memset(out_names, 0, (size_t)max_bones * stride);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity) || ((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t component = EntityComponent(base, entity, kAnimatedMeshId);

    if (!plausible(component)) {
        return 0u;
    }

    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    const uint32_t table = read32(skeleton + 0x2Cu);
    const uint32_t table_end = read32(skeleton + 0x30u);
    if (!plausible(table) || table_end <= table) {
        return 0u;
    }

    uint32_t bones = (table_end - table) / 8u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    for (uint32_t i = 0u; i < bones; ++i) {
        char* slot = out_names + ((size_t)i * stride);
        const uint32_t node = read32(table + (i * 8u) + 4u);
        if (!plausible(node)) {
            continue;  // unnamed — the guest's own empty-string case, not an error
        }

        const uint32_t chars = read32(node);
        uint32_t length = read32(node + 4u);
        if (!plausible(chars)) {
            continue;
        }

        // The length is the guest's, so it is clamped rather than trusted: a torn read of a
        // string being rebuilt would otherwise walk off into whatever follows it.
        if (length > stride - 1u) {
            length = stride - 1u;
        }

        for (uint32_t c = 0u; c < length; ++c) {
            const uint8_t byte = read8(chars + c);
            if (byte == 0u) {
                break;
            }

            slot[c] = (char)byte;
        }
    }

    return bones;
}

/// The command-stream rings, read-only. `sub_8222ABE0` blocks when the reader traps the
/// writer, and these four numbers per slot are the whole condition.
///
/// The frame-constant sources the title's render passes read, as host floats:
///   [0..3]    ambient light colour, `*(0x834A4D60)+208` (the second directional light, whose
///             colour is the theme's AMBIENT_COLOUR) -> PS c20 / VS c54 (`sub_821B1A88`)
///   [4..7]    its direction, `*(0x834A4D60)+304` (the sun's, as the light travels)
///             -> PS c23 = (-dir, 4), VS c19 = -dir (`sub_821B16F8`)
///   [8..11]   lightmap factor, `0x834A4D40`       -> PS c21 = (xyz, 1.3), VS c18 = (xyz, 2.86)
///   [12..15]  normal-map darkening, `0x834A4D50`  -> PS c22
///   [16..35]  atmosphere block, `0x834A0EC0`, 5 float4 -> PS c64..68 / VS c108..112
///             (filled by `sub_82263570` while the sky is on)
///   [36..131] the scene's THEME BLOCK, 24 float4 at scene+16: MAIN_LIGHT_COLOUR,
///             MAIN_LIGHT_SHADOWED, AMBIENT_COLOUR, LIGHTMAP_COLOUR, AMBIENT_NORMAL_MAP_
///             DARKENING_COLOUR, DISTANT_SHADOW_FACTOR, ENABLE_LIGHT_ZONE_SHADOW_CULLING,
///             SUN_INTENSITY, SKY_BETA_RAYLEIGH_MULTIPLIER, SKY_BETA_MIE_MULTIPLIER, SKY_COLOUR,
///             SKY_COMPLEMENTARY_COLOUR, SKY_COMPLEMENTARY_COLOUR_BIAS, SKY_SUNSET_COLOUR,
///             SKY_FOGGING_START, NEAR_DISTANCE, NEAR_DENSITY, FAR_DISTANCE, FAR_DENSITY,
///             CLOSE_FOG_COLOUR, CLOSE_FOG_MAX_DISTANCE, then tone mapping (`sub_83288CA0`'s
///             table; the render-side copy `sub_822AF480` writes at scene+16)
///   [132..135] the sun light's colour (twin+208), [136..139] its direction (twin+304),
///   [140] its shadow enable (byte twin+336), [141] intensity (twin+448), [142] twin+452,
///   [143] the scene's global mist strength (O+3040.x), [144..146] the wind direction and
///   [147] its speed (O+3384..3399).
/// The scene is `*(*(0x8349F79C) + 4*idx)` and the sun twin `*(*(0x8349F7AC) + 4*slot)`, with
/// idx = L+4 and slot = L+8 off the sky owner L = `*(wg+92)`,
/// wg = `*(*(*(*(0x83496AB8)+12)+88)+4)` (`sub_8234EF58`, `sub_825A14C0`).
///
/// THE SOURCES, NOT THE DEVICE: the passes copy these into the D3D constant shadow as they
/// run, and in this host they never run (the scene stream is drained, the twins are kept, the
/// pass bodies are not entered), so the shadow is zero while these are live. Returns the floats
/// written (148), or 0 when the lighting objects are not up yet; a missing scene or sun leaves
/// its part zero.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicFrame(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = 148u;
    if (out == nullptr || capacity < kFloats) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    for (uint32_t i = 0; i < kFloats; ++i) {
        out[i] = 0.0f;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };

    const uint32_t lighting = read32(refii::guest::Address(0x834A4D60u));
    if (!plausible(lighting)) {
        return 0;
    }
    read_floats(lighting + 208u, out, 4u);
    read_floats(lighting + 304u, out + 4, 4u);
    read_floats(refii::guest::Address(0x834A4D40u), out + 8, 4u);
    read_floats(refii::guest::Address(0x834A4D50u), out + 12, 4u);
    read_floats(refii::guest::Address(0x834A0EC0u), out + 16, 20u);

    // The sky owner, through the game mode's world graphics.
    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return kFloats;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    if (!plausible(owner)) {
        return kFloats;
    }

    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (plausible(scenes) && scene_index < 4096u) {
        const uint32_t scene = read32(scenes + scene_index * 4u);
        if (plausible(scene)) {
            read_floats(scene + 16u, out + 36, 96u);
            // [143] the scene's global mist strength (O+3040.x), the mist binder's factor.
            read_floats(scene + 3040u, out + 143, 1u);
            // [144..146] the wind's unit direction and [147] its speed (O+3384..3399, the wind
            // object's command `sub_8229B798`): the tree and grass sway terms.
            read_floats(scene + 3384u, out + 144, 4u);
        }
    }

    const uint32_t sun_slot = read32(owner + 8u);
    const uint32_t twins = read32(refii::guest::Address(0x8349F7ACu));
    if (plausible(twins) && sun_slot < 65536u) {
        const uint32_t sun = read32(twins + sun_slot * 4u);
        if (plausible(sun)) {
            read_floats(sun + 208u, out + 132, 4u);
            read_floats(sun + 304u, out + 136, 4u);
            out[140] = static_cast<float>(GuestWordAt(base, sun + 336u) >> 24);
            read_floats(sun + 448u, out + 141, 2u);
        }
    }
    return kFloats;
}

/// The render scene the classic theme lives in: `*(0x8349F79C)[owner+4]`, with owner reached from
/// the game mode (`*(0x83496AB8)+12 -> +88 -> +4 -> +92`). 0 when the world is not up.
static uint32_t ClassicThemeScene(uint8_t* base) {
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t a = plausible(mode) ? read32(mode + 12u) : 0u;
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0u;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    return plausible(scene) ? scene : 0u;
}

/// THE THEME'S TEXTURE SLOTS, raw, for the classic sky and water. The theme (T = scene+16) holds,
/// per slot s, a vector of {f32 weight, u32 texture hash} at T + 16 x (194 + s): +4 begin, +8 end
/// (`sub_821D0C00`, 0x821D0C14..0x821D0C30). Slots: 0 TONE_MAPPING_OVERLAY, 1 SKY_OVERLAY,
/// 2 SKY_SUNBEAMS, 3 SKY_SUNDISC, 4 SKY_STREAKSTEXTURE, 5 SKY_GLARETEXTURE, 6 SKY_MOONTEXTURE,
/// 7 SKY_MOONGLARETEXTURE, 8..11 CLOUDS_LAYERn_DENSITY_MAP, 12 WATER_NORMAL_MAP,
/// 13 WATER_DETAIL_MAP, 14 WATER_HEIGHT_MAP. Out, u32 words, per slot 17: [0] the element count
/// (all of them, even past the 8 copied), then up to 8 pairs (weight bits, hash) in the vector's
/// own order — the caller picks and normalises as `sub_821D0C00` does. Returns the words written
/// (15 x 17), or 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicThemeTextures(uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kSlots = 15u;
    constexpr uint32_t kPairs = 8u;
    constexpr uint32_t kStride = 1u + kPairs * 2u;
    if (out == nullptr || capacity < kSlots * kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0;
    }
    const uint32_t theme = scene + 16u;
    for (uint32_t slot = 0; slot < kSlots; ++slot) {
        uint32_t* row = out + slot * kStride;
        for (uint32_t i = 0; i < kStride; ++i) {
            row[i] = 0u;
        }
        const uint32_t header = theme + 16u * (194u + slot);
        const uint32_t begin = GuestWordAt(base, header + 4u);
        const uint32_t end = GuestWordAt(base, header + 8u);
        if (begin < 0x10000u || end < begin || end - begin > 0x10000u) {
            continue;
        }
        const uint32_t count = (end - begin) >> 3;
        row[0] = count;
        const uint32_t copied = count < kPairs ? count : kPairs;
        for (uint32_t i = 0; i < copied; ++i) {
            row[1u + i * 2u] = GuestWordAt(base, begin + i * 8u);
            row[2u + i * 2u] = GuestWordAt(base, begin + i * 8u + 4u);
        }
    }
    return kSlots * kStride;
}

/// THE WHOLE THEME BLOCK and the sky object's sun and moon, for the classic sky. Out, floats:
///   [0 .. 775]  the scene's theme block, 194 parameters x 4 lanes (scene+16 + 16 x index, the
///               table `sub_83288CA0` fills; Engine_ClassicThemeParams names them)
///   [776..779]  sun colour (sky S+0), [780..783] sun direction (S+16)
///   [784..787]  moon direction (M+0), [788] moon phase (M+20, an integer 0..7)
/// S = sky+144, M = sky+208, the sky being the render twin named by owner+16 (vtable 0x820052C8,
/// slot 17 `sub_8228F038` draws dome, moon, stars and clouds). Returns the floats written, 0
/// when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTheme(float* out, uint32_t capacity) {
    constexpr uint32_t kTheme = 194u * 4u;
    constexpr uint32_t kFloats = kTheme + 13u;
    if (out == nullptr || capacity < kFloats) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t a = plausible(mode) ? read32(mode + 12u) : 0u;
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scene = ClassicThemeScene(base);
    if (!plausible(owner) || scene == 0u) {
        return 0;
    }
    for (uint32_t i = 0; i < kFloats; ++i) {
        out[i] = 0.0f;
    }
    read_floats(scene + 16u, out, kTheme);
    const uint32_t twins = read32(refii::guest::Address(0x8349F7ACu));
    const uint32_t sky_slot = read32(owner + 16u);
    const uint32_t sky = plausible(twins) && sky_slot < 65536u ? read32(twins + sky_slot * 4u) : 0u;
    if (plausible(sky) && read32(sky) == refii::guest::Address(0x820052C8u)) {
        read_floats(sky + 144u, out + kTheme, 4u);
        read_floats(sky + 160u, out + kTheme + 4u, 4u);
        read_floats(sky + 208u, out + kTheme + 8u, 4u);
        out[kTheme + 12u] = static_cast<float>(read32(sky + 228u));
    }
    return kFloats;
}

/// THE LIGHT HALOS the guest draws this frame: the light manager's halo list, a global vector of
/// light pointers at 0x834A4DA0 (begin) / 0x834A4DA4 (end) that `sub_821D5628` refills each frame
/// from families 8 and 9, and that `sub_82274370` walks to draw LIGHTHALO (VS 0x9B / PS 0x9C,
/// ONE/ONE, depth-tested, no depth write) into the half-res bloom layer. Per light L the draw
/// (`sub_82273E60`) needs L+240 (intensity) and L+236 (size) both >= 0.01 and a texture at L+460;
/// it places a camera-facing quad of half-extent L+236 (aspect 1, 1 in the main pass) at L+288
/// and sets PS c123 = L+208.rgb x L+448 x L+240. Out, per halo 12 floats:
///   [0..2] centre L+288, [3] half-extent L+236, [4..6] colour (the c123 product), [7] the
///   water pass's extra factor L+244, [8..9] its aspect L+248 / L+252, [10] the texture's
///   resource hash (*(L+460)+28) as raw bits, [11] the light pointer as raw bits.
/// Returns the halos written (at most capacity / 12), 0 when the list is empty.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLightHalos(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = 12u;
    if (out == nullptr || capacity < kFloats) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(float));
        return value;
    };
    const auto put_bits = [](float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(float)); };

    const uint32_t begin = read32(refii::guest::Address(0x834A4DA0u));
    const uint32_t end = read32(refii::guest::Address(0x834A4DA4u));
    if (!plausible(begin) || end < begin || end - begin > 4u * 4096u) {
        return 0;
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && (written + 1u) * kFloats <= capacity; at += 4u) {
        const uint32_t light = read32(at);
        if (!plausible(light)) {
            continue;
        }
        const float intensity = read_float(light + 240u);
        const float size = read_float(light + 236u);
        const uint32_t texture = read32(light + 460u);
        if (!(intensity >= 0.01f) || !(size >= 0.01f) || !plausible(texture)) {
            continue;
        }
        const float scale = read_float(light + 448u) * intensity;
        float* o = out + written * kFloats;
        o[0] = read_float(light + 288u);
        o[1] = read_float(light + 292u);
        o[2] = read_float(light + 296u);
        o[3] = size;
        o[4] = read_float(light + 208u) * scale;
        o[5] = read_float(light + 212u) * scale;
        o[6] = read_float(light + 216u) * scale;
        o[7] = read_float(light + 244u);
        o[8] = read_float(light + 248u);
        o[9] = read_float(light + 252u);
        put_bits(o + 10, read32(texture + 28u));
        put_bits(o + 11, light);
        ++written;
    }
    return written;
}

/// The render scene's ACTIVE LIGHTS, as the light manager holds them: the set the title's
/// per-draw light query walks (`sub_82AB6360`), so the classic renderer can assign lights to
/// a draw the way the guest does. LM = scene+3684; its entries are 32 bytes at `*(LM+8)`
/// (vec4 position.xyz / radius.w, +16 the light object L; the sun's radius is 1e10) and
/// `*(LM+24)` is the 64-bit mask of live entries, bit i for entry i. Per light, 20 floats:
///   [0..2] position, [3] radius (the entry's), [4..6] colour (L+208), [7] intensity (L+448),
///   [8..10] direction (L+304), [11] type (L+4: 8 point, 9 spot, 10 directional),
///   [12] range (L+320), [13] cone angle (L+324), [14] range scale (L+452),
///   [15] flags: bit0 L+336, bit1 L+337, bit2 L+338, bit3 L+484 (the shadow-casting bytes),
///   [16] lighting zone id (L+416) as raw bits, [17] the entry index, [18..19] reserved.
/// Returns the number of lights written (at most `capacity / 20`), or 0 when the scene is not
/// up. The fields are the ones `sub_82A6CDD0` packs into the light block and the query reads.
/// The SPOT LIGHTS' RECEIVE FRUSTUMS: a spot takes a draw only when the draw's box is inside its
/// frustum (light vtable +96 = `sub_82A6CC98` -> +92 `sub_82A6CC80` -> `sub_822362F8(light+560,
/// box)`: planes vec4 at +0, count at +0x100; a box is out when, for some plane, the box corner
/// along the normal gives dot(n, p) + w < 0). Per live spot of the manager RefiiHostGetClassicLights
/// walks: [0] entry index, [1] plane count (<= 16), [2..65] the planes. out[0] = spots; returns the
/// floats written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSpotFrustums(float* out, uint32_t capacity) {
    constexpr uint32_t kRecord = 66u;
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const uint32_t scene = ClassicThemeScene(base);
    const uint32_t manager = scene != 0u ? GuestWordAt(base, scene + 3684u) : 0u;
    if (!plausible(manager)) {
        return 1u;
    }
    const uint32_t entries = GuestWordAt(base, manager + 8u);
    const uint64_t mask = (static_cast<uint64_t>(GuestWordAt(base, manager + 24u)) << 32) | GuestWordAt(base, manager + 28u);
    if (!plausible(entries)) {
        return 1u;
    }
    uint32_t used = 1u;
    uint32_t spots = 0u;
    for (uint32_t index = 0; index < 64u && used + kRecord <= capacity; ++index) {
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t light = GuestWordAt(base, entries + index * 32u + 16u);
        if (!plausible(light) || GuestWordAt(base, light + 4u) != 9u) {
            continue;
        }
        const uint32_t frustum = light + 560u;
        uint32_t count = GuestWordAt(base, frustum + 0x100u);
        if (count > 16u) {
            count = 16u;
        }
        float* f = out + used;
        f[0] = static_cast<float>(index);
        f[1] = static_cast<float>(count);
        for (uint32_t k = 0; k < 64u; ++k) {
            const uint32_t bits = k < count * 4u ? GuestWordAt(base, frustum + k * 4u) : 0u;
            std::memcpy(f + 2 + k, &bits, sizeof(float));
        }
        used += kRecord;
        ++spots;
    }
    out[0] = static_cast<float>(spots);
    return used;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 20u;
    if (out == nullptr || capacity < kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t manager = read32(scene + 3684u);
    if (!plausible(manager)) {
        return 0;
    }
    const uint32_t entries = read32(manager + 8u);
    const uint64_t mask = (static_cast<uint64_t>(read32(manager + 24u)) << 32) | read32(manager + 28u);
    if (!plausible(entries) || mask == 0u) {
        return 0;
    }

    const uint32_t max_lights = capacity / kStride;
    uint32_t written = 0;
    for (uint32_t index = 0; index < 64u && written < max_lights; ++index) {
        // Bit i is entry i: live, the mask read 1 with the sun in entry 0 and entry 63 empty.
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t entry = entries + index * 32u;
        const uint32_t light = read32(entry + 16u);
        if (!plausible(light)) {
            continue;
        }
        float* o = out + written * kStride;
        o[0] = read_float(entry);
        o[1] = read_float(entry + 4u);
        o[2] = read_float(entry + 8u);
        o[3] = read_float(entry + 12u);
        o[4] = read_float(light + 208u);
        o[5] = read_float(light + 212u);
        o[6] = read_float(light + 216u);
        o[7] = read_float(light + 448u);
        o[8] = read_float(light + 304u);
        o[9] = read_float(light + 308u);
        o[10] = read_float(light + 312u);
        o[11] = static_cast<float>(read32(light + 4u));
        o[12] = read_float(light + 320u);
        o[13] = read_float(light + 324u);
        o[14] = read_float(light + 452u);
        const uint32_t flag_word = read32(light + 336u);  // bytes +336..+339
        uint32_t flags = 0;
        flags |= ((flag_word >> 24) & 0xFFu) != 0u ? 1u : 0u;
        flags |= ((flag_word >> 16) & 0xFFu) != 0u ? 2u : 0u;
        flags |= ((flag_word >> 8) & 0xFFu) != 0u ? 4u : 0u;
        flags |= (read32(light + 484u) >> 24) != 0u ? 8u : 0u;
        o[15] = static_cast<float>(flags);
        // The lighting zone id, bit for bit: a zone is a hash and a float cast would round it.
        const uint32_t zone = read32(light + 416u);
        std::memcpy(o + 16, &zone, sizeof(zone));
        o[17] = static_cast<float>(index);
        // The light's address, bit for bit: matches it to RefiiHostGetClassicSceneLights.
        std::memcpy(o + 18, &light, sizeof(light));
        o[19] = 0.0f;
        ++written;
    }
    return written;
}

/// EVERY LIGHT OF THE RENDER SCENE, not only the manager's active list: the scene registry's
/// lights (scene+3412, nodes of 24 bytes at +24..+28 with flag 1 at +4, objects at +12..+16, as
/// `sub_821FF800` walks them; a light is an object whose vtable+8 is `sub_8221FAE0`), less type 11
/// and those with L+338 set — what the manager's gather tests further against ONE eye, the view the
/// guest draws (player one's). For split screen's second view the classic renderer gathers from
/// these by the same rules against player two's eye (Engine_ClassicLights).
/// 36 floats per light: [0..19] as RefiiHostGetClassicLights (sphere L+112, colour L+208,
/// intensity L+448 as player one's eye left it, direction, type, range, cone, range scale, flags,
/// zone, [17] 0, [18] the light's address), then [20..22] position L+288, [23] fade distance
/// L+328, [24] fade range L+332, [25] flicker brightness L+224, [26] the flicker walk L+444,
/// [27..29] AABB min L+80, [30..32] AABB max L+96. Returns the lights written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicSceneLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 36u;
    if (out == nullptr || capacity < kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xE0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_float = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float value;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t a = plausible(mode) ? read32(mode + 12u) : 0u;
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t registry = scene + 3412u;
    const uint32_t nodes_begin = read32(registry + 24u);
    const uint32_t nodes_end = read32(registry + 28u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || (nodes_end - nodes_begin) % 24u != 0u ||
        (nodes_end - nodes_begin) / 24u > 64u) {
        return 0;
    }
    const uint32_t stamp = refii::guest::Address(0x8221FAE0u);
    const uint32_t max_lights = capacity / kStride;
    uint32_t written = 0;
    for (uint32_t node = nodes_begin; node < nodes_end && written < max_lights; node += 24u) {
        if ((read32(node + 4u) & 1u) == 0u) {
            continue;
        }
        const uint32_t objects = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects) || objects_end < objects || objects_end - objects > 4u * 4096u) {
            continue;
        }
        for (uint32_t at = objects; at < objects_end && written < max_lights; at += 4u) {
            const uint32_t light = read32(at);
            const uint32_t vtable = plausible(light) ? read32(light) : 0u;
            if (!plausible(vtable) || read32(vtable + 8u) != stamp || read32(light + 4u) == 11u ||
                GuestByteAt(base, light + 338u) != 0u) {
                continue;
            }
            float* o = out + written * kStride;
            o[0] = read_float(light + 112u);
            o[1] = read_float(light + 116u);
            o[2] = read_float(light + 120u);
            o[3] = read_float(light + 124u);
            o[4] = read_float(light + 208u);
            o[5] = read_float(light + 212u);
            o[6] = read_float(light + 216u);
            o[7] = read_float(light + 448u);
            o[8] = read_float(light + 304u);
            o[9] = read_float(light + 308u);
            o[10] = read_float(light + 312u);
            o[11] = static_cast<float>(read32(light + 4u));
            o[12] = read_float(light + 320u);
            o[13] = read_float(light + 324u);
            o[14] = read_float(light + 452u);
            const uint32_t flag_word = read32(light + 336u);
            uint32_t flags = 0;
            flags |= ((flag_word >> 24) & 0xFFu) != 0u ? 1u : 0u;
            flags |= ((flag_word >> 16) & 0xFFu) != 0u ? 2u : 0u;
            flags |= ((flag_word >> 8) & 0xFFu) != 0u ? 4u : 0u;
            flags |= (read32(light + 484u) >> 24) != 0u ? 8u : 0u;
            o[15] = static_cast<float>(flags);
            const uint32_t zone = read32(light + 416u);
            std::memcpy(o + 16, &zone, sizeof(zone));
            o[17] = 0.0f;
            std::memcpy(o + 18, &light, sizeof(light));
            o[19] = 0.0f;
            o[20] = read_float(light + 288u);
            o[21] = read_float(light + 292u);
            o[22] = read_float(light + 296u);
            o[23] = read_float(light + 328u);
            o[24] = read_float(light + 332u);
            o[25] = read_float(light + 224u);
            o[26] = read_float(light + 444u);
            for (uint32_t k = 0; k < 3u; ++k) {
                o[27 + k] = read_float(light + 80u + 4u * k);
                o[30 + k] = read_float(light + 96u + 4u * k);
            }
            for (uint32_t k = 33u; k < kStride; ++k) {
                o[k] = 0.0f;
            }
            ++written;
        }
    }
    return written;
}

/// The render scene's MIST VOLUMES: the type-27 objects of the scene's typed list (O+3412:
/// nodes of 24 bytes between *(L+4) and *(L+8), keyed by type at +0, objects at *(node+12)..
/// *(node+16); `sub_82A8B998`, `sub_821F4480`), as the per-draw test and the binder read them
/// (`sub_82B1DE38`, `sub_82A6DD30`, `Render_BindHighSamplersAndPixelBool` 0x82A6EAA8). Per
/// mist, 36 floats:
///   [0..2] bounds min (V+80), [3..5] bounds max (V+96), [6..9] colour (V+192) -> c72,
///   [10] density (V+184), [11] noise offset (V+216) -> the evolve pass's c46,
///   [12] time speed (V+220), [13..14] noise tiling (V+264/268) -> the evolve pass's c31.xy,
///   [15] V+316 (c76.w = 1/it), [16..19] V+320 -> c75, [20] global-strength weight (V+344),
///   [21..24] V+352 -> c73, [25..28] V+368 -> c74, [29..31] extents (V+224), [32..34] origin
///   (V+240), [35] 1 when the height texture (V+276) is set.
/// Returns the mists written (at most capacity / 36), or 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicMists(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 36u;
    if (out == nullptr || capacity < kStride) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = read32(list + 4u);
    const uint32_t nodes_end = read32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0;
    }

    const uint32_t max_mists = capacity / kStride;
    uint32_t written = 0;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        if (read32(node) != 27u) {
            continue;
        }
        const uint32_t objects_begin = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            break;
        }
        const uint32_t count = (objects_end - objects_begin) / 4u;
        for (uint32_t i = 0; i < count && written < max_mists; ++i) {
            const uint32_t mist = read32(objects_begin + i * 4u);
            if (!plausible(mist)) {
                continue;
            }
            float* o = out + written * kStride;
            read_floats(mist + 80u, o, 3u);
            read_floats(mist + 96u, o + 3, 3u);
            read_floats(mist + 192u, o + 6, 4u);
            read_floats(mist + 184u, o + 10, 1u);
            read_floats(mist + 216u, o + 11, 1u);
            read_floats(mist + 220u, o + 12, 1u);
            read_floats(mist + 264u, o + 13, 2u);
            read_floats(mist + 316u, o + 15, 1u);
            read_floats(mist + 320u, o + 16, 4u);
            read_floats(mist + 344u, o + 20, 1u);
            read_floats(mist + 352u, o + 21, 4u);
            read_floats(mist + 368u, o + 25, 4u);
            read_floats(mist + 224u, o + 29, 3u);
            read_floats(mist + 240u, o + 32, 3u);
            o[35] = plausible(read32(mist + 276u)) ? 1.0f : 0.0f;
            ++written;
        }
        break;
    }
    return written;
}

/// THE PARTICLE-SYSTEM INSTANCE BEHIND EACH QUAD AND TRAIL TWIN, so the host can tell which
/// effects the guest is drawing itself. The twin keeps no pointer back to the sim, so the link is
/// walked from the sim side: every live instance (the vector at dword_8349E48C, {instance,
/// refnode} stride 8) whose class carries the render component at +0x150 (`sub_8322F600`
/// 0x8322F6C8 `addi r3,r31,0x150` -> `sub_83228588`) holds the renderer holder at component +28
/// (`*(a1+28) = v39`), i.e. instance +0x16C; the holder (vtable off_82001AC0) keeps the quad/trail
/// proxy at +24 and the proxy its twin handle at +4, -1 when none (`sub_82456E60`); the handle
/// indexes the twin table dword_8349F7AC and the twin repeats it at +16 (`sub_821E1F40`
/// `v24[4] = v21`). A class without the component reads past its end here, so a link counts
/// only when the holder's vtable and the table's twin both agree. Child components (the 496-byte
/// class's map at +460) are not walked: their twins have no owner.
static void ParticleTwinOwners(uint8_t* base, std::unordered_map<uint32_t, uint32_t>& owners) {
    owners.clear();
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const uint32_t begin = read32(refii::guest::Address(0x8349E490u));
    const uint32_t end = read32(refii::guest::Address(0x8349E494u));
    const uint32_t table = read32(refii::guest::Address(0x8349F7ACu));
    const uint32_t table_end = read32(refii::guest::Address(0x8349F7B0u));
    const uint32_t holder_vtable = refii::guest::Address(0x82001AC0u);
    if (!plausible(begin) || end < begin || end - begin > 8u * 8192u || !plausible(table) || table_end < table) {
        return;
    }
    const uint32_t handles = (table_end - table) / 4u;
    for (uint32_t at = begin; at + 8u <= end; at += 8u) {
        const uint32_t instance = read32(at);
        if (!plausible(instance)) {
            continue;
        }
        const uint32_t holder = read32(instance + 0x16Cu);
        if (!plausible(holder) || read32(holder) != holder_vtable) {
            continue;
        }
        const uint32_t proxy = read32(holder + 24u);
        if (!plausible(proxy)) {
            continue;
        }
        const uint32_t handle = read32(proxy + 4u);
        if (handle >= handles) {
            continue;
        }
        const uint32_t twin = read32(table + handle * 4u);
        if (plausible(twin) && read32(twin + 16u) == handle) {
            owners[twin] = instance;
        }
    }
}

/// The render scene's PARTICLE GROUPS as their render-side twins hold them: the data the sim
/// states to the renderer through the twin's receivers (`sub_821D8EA8` the records, material
/// and bounds; `sub_821D9D40` the transform), which the title's point-sprite draw
/// (`sub_821CA790`) turns into FACE_ME quads. Walks the scene's typed object list (O+3412) for
/// the two particle classes (types 12 and 30, `sub_82A43EA0` / `sub_82A449A8`) and writes, per
/// group whose +240 kind is 1 (quads; trails are 2 and 3):
///   a 320-float header, then count x 12 floats of 48-byte records as the stream holds them.
/// Header (R = the render object, D = *(*(R+180)+4) its render data, M = *(D+48) its material):
///   [0] R (bits) [1] R+240 kind [2] count R+176 [3] R+244
///   [4..6] bounds min R+80 [7] the owning particle-system instance (bits; 0 unknown,
///   ParticleTwinOwners) [8..10] bounds max R+96
///   [12..16] fade distances R+384..400 (in near, in far, out near, out far, alpha)
///   [17] scale R+404 [18] R+408 [19] R+409 [20] R+412
///   [21..36] the current transform R+320, row-major as the title lerps it (`sub_8288F408`)
///   [37] M (bits) [38] texture count M+180 [39..41] blend src/dst/op M+36..44 (engine enums:
///       0 ZERO 1 ONE 2 SRCCOLOR 3 INVSRCCOLOR 4 SRCALPHA 5 INVSRCALPHA 6 DESTCOLOR
///       7 INVDESTCOLOR 8 DESTALPHA 9 INVDESTALPHA; ops 0 ADD 1 SUB 2 REVSUB 3 MIN 4 MAX)
///   [42] soft fade distance M+48 [43] M+52 displacement [44] M+53 alpha ref 16 [45] M+56 fixed axes
///   [46..49] D+32 the fixed-axis quaternion [50] keyframe count (at most 19)
///   [51..53] the lighting object's +496 (the ambient the lit particle adds) [54] probe count
///   [64..253] 19 keyframes x 10: colour(4) size x, size y, global scale x, y, self-illum, start
///       (M+8 vector, stride 40: +0 colour, +16/+20 sizes, +24/+28 global scales, +32, +36)
///   [254..333] 4 textures x 20: key word, start time, x divisions, y divisions, 16 words of the
///       key's text when the key is a pointer to one (M+80 + 24 i: +4 key, +8 start, +20/+21);
///       the draw takes up to four (`sub_821CA790`, blend index clamp(M+180 - 1, 0, 3))
/// out[0] = groups written, out[1] = floats used; returns the floats used, 0 when the scene is
/// not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicParticles(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 340u;
    constexpr uint32_t kRecord = 12u;
    constexpr uint32_t kMaxRecords = 8192u;
    if (out == nullptr || capacity < 2u + kHeader) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&read32](uint32_t at) {
        return (read32(at & ~3u) >> (8u * (3u - (at & 3u)))) & 0xFFu;
    };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const auto put_bits = [](float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(float)); };

    const uint32_t mode = read32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t a = read32(mode + 12u);
    const uint32_t b = plausible(a) ? read32(a + 88u) : 0u;
    const uint32_t wg = plausible(b) ? read32(b + 4u) : 0u;
    const uint32_t owner = plausible(wg) ? read32(wg + 92u) : 0u;
    const uint32_t scenes = read32(refii::guest::Address(0x8349F79Cu));
    if (!plausible(owner) || !plausible(scenes)) {
        return 0;
    }
    const uint32_t scene_index = read32(owner + 4u);
    const uint32_t scene = scene_index < 4096u ? read32(scenes + scene_index * 4u) : 0u;
    if (!plausible(scene)) {
        return 0;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = read32(list + 4u);
    const uint32_t nodes_end = read32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0;
    }
    const uint32_t lighting = read32(refii::guest::Address(0x834A4D60u));
    // The probe container the lit particle's ambient asks (`sub_821907F8`): *(scene+3408), read
    // at 0x821CA98C..A0 as *(*(view+4)+20)+0xD50. Its +72/+76 vector holds 12-byte entries.
    const uint32_t container = read32(scene + 3408u);
    const uint32_t probes = plausible(container)
        ? (read32(container + 76u) - read32(container + 72u)) / 12u
        : 0u;

    uint32_t used = 2u;
    static thread_local std::unordered_map<uint32_t, uint32_t> owners;
    ParticleTwinOwners(base, owners);
    uint32_t groups = 0u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        const uint32_t type = read32(node);
        if (type != 12u && type != 30u) {
            continue;
        }
        const uint32_t objects_begin = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            continue;
        }
        const uint32_t count_objects = (objects_end - objects_begin) / 4u;
        for (uint32_t i = 0; i < count_objects; ++i) {
            const uint32_t r = read32(objects_begin + i * 4u);
            if (!plausible(r) || read32(r + 240u) != 1u) {
                continue;
            }
            uint32_t count = read32(r + 176u);
            if (count == 0u || count > kMaxRecords) {
                continue;
            }
            const uint32_t shared = read32(r + 180u);
            const uint32_t data = plausible(shared) ? read32(shared + 4u) : 0u;
            if (!plausible(data)) {
                continue;
            }
            const uint32_t records = read32(data + 8u);
            const uint32_t material = read32(data + 48u);
            if (!plausible(records) || !plausible(material)) {
                continue;
            }
            if (used + kHeader + count * kRecord > capacity) {
                break;
            }
            float* h = out + used;
            for (uint32_t k = 0; k < kHeader; ++k) {
                h[k] = 0.0f;
            }
            put_bits(h + 0, r);
            { const auto owner = owners.find(r); put_bits(h + 7, owner != owners.end() ? owner->second : 0u); }
            h[1] = 1.0f;
            h[2] = static_cast<float>(count);
            h[3] = static_cast<float>(static_cast<int32_t>(read32(r + 244u)));
            read_floats(r + 80u, h + 4, 3u);
            read_floats(r + 96u, h + 8, 3u);
            read_floats(r + 384u, h + 12, 5u);
            read_floats(r + 404u, h + 17, 1u);
            h[18] = static_cast<float>(read8(r + 408u));
            h[19] = static_cast<float>(read8(r + 409u));
            read_floats(r + 412u, h + 20, 1u);
            read_floats(r + 320u, h + 21, 16u);
            put_bits(h + 37, material);
            const uint32_t textures = read32(material + 180u);
            h[38] = static_cast<float>(textures > 4u ? 4u : textures);
            h[39] = static_cast<float>(read32(material + 36u));
            h[40] = static_cast<float>(read32(material + 40u));
            h[41] = static_cast<float>(read32(material + 44u));
            read_floats(material + 48u, h + 42, 1u);
            h[43] = static_cast<float>(read8(material + 52u));
            h[44] = static_cast<float>(read8(material + 53u));
            h[45] = static_cast<float>(read8(material + 56u));
            read_floats(data + 32u, h + 46, 4u);
            const uint32_t keys_begin = read32(material + 8u);
            const uint32_t keys_end = read32(material + 12u);
            uint32_t keys = plausible(keys_begin) && keys_end >= keys_begin ? (keys_end - keys_begin) / 40u : 0u;
            if (keys > 19u) {
                keys = 19u;
            }
            h[50] = static_cast<float>(keys);
            if (plausible(lighting)) {
                read_floats(lighting + 496u, h + 51, 3u);
            }
            h[54] = static_cast<float>(probes);
            for (uint32_t k = 0; k < keys; ++k) {
                const uint32_t key = keys_begin + k * 40u;
                float* o = h + 64 + k * 10;
                read_floats(key, o, 4u);
                read_floats(key + 16u, o + 4, 2u);
                read_floats(key + 24u, o + 6, 2u);
                read_floats(key + 32u, o + 8, 2u);
            }
            for (uint32_t t = 0; t < textures && t < 4u; ++t) {
                const uint32_t entry = material + 80u + t * 24u;
                float* o = h + 254 + t * 20;
                const uint32_t key = read32(entry + 4u);
                put_bits(o, key);
                read_floats(entry + 8u, o + 1, 1u);
                o[2] = static_cast<float>(static_cast<int8_t>(read8(entry + 20u)));
                o[3] = static_cast<float>(static_cast<int8_t>(read8(entry + 21u)));
                // A key that points at printable text is the texture's path; carry it.
                if (plausible(key)) {
                    bool text = true;
                    for (uint32_t c = 0; c < 4u && text; ++c) {
                        const uint32_t ch = read8(key + c);
                        text = ch >= 0x20u && ch < 0x7Fu;
                    }
                    if (text) {
                        for (uint32_t w = 0; w < 16u; ++w) {
                            put_bits(o + 4 + w, read32(key + w * 4u));
                        }
                    }
                }
            }
            read_floats(records, h + kHeader, count * kRecord);
            used += kHeader + count * kRecord;
            ++groups;
        }
    }
    out[0] = static_cast<float>(groups);
    out[1] = static_cast<float>(used);
    return groups == 0u ? 0u : used;
}

/// The render scene's TRAILS (R+240 == 2) and DOUBLE TRAILS (3), as their render twins hold them,
/// for the title's TRAILS / DOUBLE_TRAILS vertex shaders. The draw (`sub_821CA5E8` 0x821CA6A4)
/// takes D = *(*(R+0xB4)+4) and the material TM = *(D+0x40) (trails) or DM = *(D+0x2C) (double),
/// drawing into the displacement target instead when TM+0x34 / DM+0x28 (IsDisplacementTexture) is
/// set (`Render_EffectStripDraw` 0x82A597B0 / `Render_EffectStripDraw2` 0x82A5A818: VS 0041 / 0043,
/// PS 0037, the renderer's ONE / ONE; header [55]).
/// The stream it draws is the ring at D (+8 base, +20 head, +32 mask, +36 count c) unrolled with
/// a point either side, as `sub_82179C00` / `sub_82A592F0` fill it: c + 3 points of 8 floats.
/// Per group a 288-float header, then the points:
///   [0] R (bits) [1] kind [2] n = R+176 [3] c [4] s (TM+0x20 / DM+0x14, int) [5] vertices
///   2((n-1)s+1) [6] closed loop (kind 2) [7] TM or DM (bits) [8..11] c366 (s, 1/((n-1)s), 1/s, 0)
///   [12..16] fades R+384..400 [17..32] transform R+320 [33] fixed axis TM+0x38 [34..37] D+48
///   quaternion [38] the owning particle-system instance (bits; 0 unknown, ParticleTwinOwners)
///   [42..44] blend src/dst/op (B+0/4/8; B = TM+0x24 / DM+0x18) [45] soft distance B+12
///   [46] B+16 [47] B+17 (alpha ref 16) [48] texture count [50] keyframes (<= 19)
///   [51..53] the lighting object's +496 [54] probe count
///   [56..207] 19 keyframes x 8: K+0..12 colour, K+16, K+20, K+24, 0 (TM/DM+8, stride 28)
///   [208..287] 4 textures x 20 as the quad export's [254..333] (block TM+0x50 / DM+0x2C)
/// out[0] = groups, out[1] = floats used; returns the floats used, 0 when none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTrails(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 288u;
    constexpr uint32_t kPoint = 8u;
    constexpr uint32_t kMaxPoints = 4096u;
    if (out == nullptr || capacity < 2u + kHeader) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto read8 = [&read32](uint32_t at) {
        return (read32(at & ~3u) >> (8u * (3u - (at & 3u)))) & 0xFFu;
    };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const auto put_bits = [](float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(float)); };

    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0;
    }
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = read32(list + 4u);
    const uint32_t nodes_end = read32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 0;
    }
    const uint32_t lighting = read32(refii::guest::Address(0x834A4D60u));
    const uint32_t container = read32(scene + 3408u);
    const uint32_t probes = plausible(container)
        ? (read32(container + 76u) - read32(container + 72u)) / 12u
        : 0u;

    uint32_t used = 2u;
    static thread_local std::unordered_map<uint32_t, uint32_t> owners;
    ParticleTwinOwners(base, owners);
    uint32_t groups = 0u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        const uint32_t type = read32(node);
        if (type != 12u && type != 30u) {
            continue;
        }
        const uint32_t objects_begin = read32(node + 12u);
        const uint32_t objects_end = read32(node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin) {
            continue;
        }
        for (uint32_t at = objects_begin; at + 4u <= objects_end; at += 4u) {
            const uint32_t r = read32(at);
            if (!plausible(r)) {
                continue;
            }
            const uint32_t kind = read32(r + 240u);
            if (kind != 2u && kind != 3u) {
                continue;
            }
            const uint32_t shared = read32(r + 180u);
            const uint32_t d = plausible(shared) ? read32(shared + 4u) : 0u;
            if (!plausible(d)) {
                continue;
            }
            const uint32_t m = read32(d + (kind == 2u ? 0x40u : 0x2Cu));
            if (!plausible(m)) {
                continue;
            }
            // IsDisplacementTexture (the key's +0x34 / +0x28): drawn into the displacement target.
            const bool displaces = read8(m + (kind == 2u ? 0x34u : 0x28u)) != 0u;
            const uint32_t n = read32(r + 176u);
            const uint32_t c = read32(d + 36u);
            if (n < 2u || c < n || c > kMaxPoints) {
                continue;  // `sub_82179C00`'s own exit
            }
            const uint32_t points = c + 3u;
            if (used + kHeader + points * kPoint > capacity) {
                break;
            }
            const int32_t sub = static_cast<int32_t>(read32(m + (kind == 2u ? 0x20u : 0x14u)));
            if (sub <= 0) {
                continue;
            }

            float* h = out + used;
            for (uint32_t k = 0; k < kHeader; ++k) {
                h[k] = 0.0f;
            }
            put_bits(h + 0, r);
            { const auto owner = owners.find(r); put_bits(h + 38, owner != owners.end() ? owner->second : 0u); }
            h[1] = static_cast<float>(kind);
            h[2] = static_cast<float>(n);
            h[3] = static_cast<float>(c);
            h[4] = static_cast<float>(sub);
            h[5] = static_cast<float>(2u * ((n - 1u) * static_cast<uint32_t>(sub) + 1u));
            put_bits(h + 7, m);
            h[55] = displaces ? 1.0f : 0.0f;
            const float span = static_cast<float>(n - 1u) * static_cast<float>(sub);
            h[8] = static_cast<float>(sub);
            h[9] = span > 0.0f ? 1.0f / span : 0.0f;
            h[10] = 1.0f / static_cast<float>(sub);
            read_floats(r + 384u, h + 12, 5u);
            read_floats(r + 320u, h + 17, 16u);
            if (kind == 2u) {
                h[33] = static_cast<float>(read8(m + 0x38u));
            }
            read_floats(d + 48u, h + 34, 4u);
            const uint32_t blend = m + (kind == 2u ? 0x24u : 0x18u);
            h[42] = static_cast<float>(read32(blend));
            h[43] = static_cast<float>(read32(blend + 4u));
            h[44] = static_cast<float>(read32(blend + 8u));
            read_floats(blend + 12u, h + 45, 1u);
            h[46] = static_cast<float>(read8(blend + 16u));
            h[47] = static_cast<float>(read8(blend + 17u));
            const uint32_t block = m + (kind == 2u ? 0x50u : 0x2Cu);
            const uint32_t textures = read32(block + 100u);
            h[48] = static_cast<float>(textures > 4u ? 4u : textures);
            const uint32_t keys_begin = read32(m + 8u);
            const uint32_t keys_end = read32(m + 12u);
            uint32_t keys = plausible(keys_begin) && keys_end >= keys_begin ? (keys_end - keys_begin) / 28u : 0u;
            if (keys > 19u) {
                keys = 19u;
            }
            h[50] = static_cast<float>(keys);
            if (plausible(lighting)) {
                read_floats(lighting + 496u, h + 51, 3u);
            }
            h[54] = static_cast<float>(probes);
            for (uint32_t k = 0; k < keys; ++k) {
                read_floats(keys_begin + k * 28u, h + 56 + k * 8, 7u);
            }
            for (uint32_t t = 0; t < textures && t < 4u; ++t) {
                const uint32_t entry = block + t * 24u;
                float* o = h + 208 + t * 20;
                const uint32_t key = read32(entry + 4u);
                put_bits(o, key);
                read_floats(entry + 8u, o + 1, 1u);
                o[2] = static_cast<float>(static_cast<int8_t>(read8(entry + 20u)));
                o[3] = static_cast<float>(static_cast<int8_t>(read8(entry + 21u)));
                if (plausible(key)) {
                    bool text = true;
                    for (uint32_t ch = 0; ch < 4u && text; ++ch) {
                        const uint32_t value = read8(key + ch);
                        text = value >= 0x20u && value < 0x7Fu;
                    }
                    if (text) {
                        for (uint32_t w = 0; w < 16u; ++w) {
                            put_bits(o + 4 + w, read32(key + w * 4u));
                        }
                    }
                }
            }

            // The stream: the ring unrolled from its head, with the points either side.
            const uint32_t ring = read32(d + 8u);
            const uint32_t head = read32(d + 20u);
            const uint32_t mask = read32(d + 32u);
            float* p = h + kHeader;
            const auto point = [&](uint32_t i, float* to) {
                read_floats(ring + ((head + i) & mask) * 32u, to, kPoint);
            };
            for (uint32_t i = 0; i < c; ++i) {
                point(i, p + (1u + i) * kPoint);
            }
            const float* first = p + kPoint;
            const float* second = p + 2u * kPoint;
            const float* last = p + c * kPoint;
            const float* before_last = p + (c - 1u) * kPoint;
            float* pre = p;
            float* post1 = p + (c + 1u) * kPoint;
            float* post2 = p + (c + 2u) * kPoint;
            bool closed = false;
            if (kind == 2u && c > 2u) {
                closed = true;
                for (uint32_t i = 0; i < 3u && closed; ++i) {
                    closed = std::fabs(first[i] - last[i]) < 0.003f;
                }
            }
            if (closed) {
                // A closed loop wraps: the neighbours are the ring's own (0x82179CB0..D38).
                std::memcpy(pre, p + (c - 1u) * kPoint, kPoint * sizeof(float));
                std::memcpy(post1, second, kPoint * sizeof(float));
                std::memcpy(post2, p + 3u * kPoint, kPoint * sizeof(float));
            } else {
                // Extrapolated: a trail its first float4 (its second the end point's), a double
                // trail its first seven floats (its eighth the end point's).
                const uint32_t lanes = kind == 2u ? 4u : 7u;
                for (uint32_t i = 0; i < kPoint; ++i) {
                    if (i < lanes) {
                        pre[i] = 2.0f * first[i] - second[i];
                        post1[i] = 2.0f * last[i] - before_last[i];
                        post2[i] = 3.0f * last[i] - 2.0f * before_last[i];
                    } else {
                        pre[i] = first[i];
                        post1[i] = last[i];
                        post2[i] = last[i];
                    }
                }
            }
            h[6] = closed ? 1.0f : 0.0f;
            used += kHeader + points * kPoint;
            ++groups;
        }
    }
    out[0] = static_cast<float>(groups);
    out[1] = static_cast<float>(used);
    return groups == 0u ? 0u : used;
}

/// THE DECALS the guest stated (`sub_82A40610`, taken by src/hooks/decals.cpp), oldest first, 27
/// words each: [0..14] as core/payload_bridge.h states them (serial, owner twin, texture name
/// hash, position, direction, half sizes, life, flag), [15..26] the owner twin's world matrix as
/// `sub_8220B490` loads it from twin+336: three rows (axis.xyz, translation) of floats, zeros when
/// the decal has no owner. `out_serial` moves whenever the set changed. Returns the count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDecals(uint32_t* out, uint32_t capacity, uint32_t* out_serial) {
    constexpr uint32_t kOut = kDecalWords + 12u;
    uint32_t stated[50u * kDecalWords];
    const uint32_t count = refii::vfs::StatedDecals(stated, 50u * kDecalWords, out_serial);
    if (out == nullptr) {
        return 0u;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    uint32_t written = 0u;
    for (uint32_t i = 0u; i < count && (written + 1u) * kOut <= capacity; ++i) {
        uint32_t* row = out + written * kOut;
        std::memcpy(row, stated + i * kDecalWords, kDecalWords * sizeof(uint32_t));
        const uint32_t owner = row[1];
        for (uint32_t k = 0u; k < 12u; ++k) {
            row[kDecalWords + k] =
                (base != nullptr && owner >= 0x10000u && owner < 0xC0000000u) ? GuestWordAt(base, owner + 336u + k * 4u) : 0u;
        }
        ++written;
    }
    return written;
}

/// What the title's RAIN (`0x822803D0`) and SNOW (`0x821BDE68`) draws read beyond the theme block
/// and the wind, which the frame export already carries. Out, floats:
///   [0] 1 when the light-zone stack (`0x834A4D68` begin, `0x834A4D6C` end) has a top zone Z
///       [1..3] its colour Z+0xD0 — the rain's diffuse and specular are 0.25 x it, the snow's
///       colour 0.75 + 0.25 x it (0x82281474, 0x821BEE20); without one, 0.1 and 1.
///   [4] the clock the snow animates by, `(float)dbl_83496E18` (0x821BEC30, binding 397)
///   [5] n, the scene's lights the rain may take: `*(scene+0xE64)`, a vector at +8 / +0xC of
///       32-byte entries whose light is at +0x10, kept when L+0xC0 == 1 (0x8228074C..0x822807B4).
///       The draw keeps those within 20 of the camera and drops the farthest down to three;
///       which camera is the host's, so the cut is the caller's.
///   [8..71] the snow's spell pushes, g_PushVector (binding 0x17A, 16 registers, 0x821BEC78): the
///       weather primitive's (scene list type 25, vtable 0x82004BC8) 8 slots at +0x50, each
///       (start xyz, clock + t1) and (end xyz, clock + t2), as message 24 states them
///       (`sub_82A77F60`, from the game's `sub_8260E100`: strikes, shots, spells)
///   [72..79] g_PushVectorOverDist (0x17B, 0x821BEC8C): 1 / d per slot at +0x150
///   [80 + 8i ..] position L+0x120 xyz, radius L+0x140, colour L+0xD0 rgb, 0.
/// Returns the floats written, 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicWeather(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 80u;
    constexpr uint32_t kLight = 8u;
    if (out == nullptr || capacity < kHeader) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    for (uint32_t i = 0; i < kHeader; ++i) {
        out[i] = 0.0f;
    }

    const uint32_t zones_begin = GuestWordAt(base, refii::guest::Address(0x834A4D68u));
    const uint32_t zones_end = GuestWordAt(base, refii::guest::Address(0x834A4D6Cu));
    if (plausible(zones_end) && zones_end > zones_begin && zones_end - zones_begin < 0x10000u) {
        const uint32_t zone = GuestWordAt(base, zones_end - 4u);
        if (plausible(zone)) {
            out[0] = 1.0f;
            read_floats(zone + 0xD0u, out + 1, 3u);
        }
    }

    const uint32_t clock_at = refii::guest::Address(0x83496E18u);
    const uint64_t clock_bits = (static_cast<uint64_t>(GuestWordAt(base, clock_at)) << 32) |
        GuestWordAt(base, clock_at + 4u);
    double clock = 0.0;
    std::memcpy(&clock, &clock_bits, sizeof(clock));
    out[4] = std::isfinite(clock) ? static_cast<float>(clock) : 0.0f;

    // The snow's push slots: the weather primitive in the scene's typed list (type 25).
    for (uint32_t k = 0; k < 8u; ++k) {
        out[72u + k] = 0.0f;
    }
    {
        const uint32_t list = scene + 3412u;
        const uint32_t nodes_begin = GuestWordAt(base, list + 4u);
        const uint32_t nodes_end = GuestWordAt(base, list + 8u);
        if (plausible(nodes_begin) && nodes_end >= nodes_begin && nodes_end - nodes_begin <= 48u * 24u) {
            for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
                if (GuestWordAt(base, node) != 25u) {
                    continue;
                }
                const uint32_t objects_begin = GuestWordAt(base, node + 12u);
                const uint32_t objects_end = GuestWordAt(base, node + 16u);
                const uint32_t weather = plausible(objects_begin) && objects_end > objects_begin
                    ? GuestWordAt(base, objects_begin) : 0u;
                if (plausible(weather)) {
                    read_floats(weather + 0x50u, out + 8, 64u);
                    read_floats(weather + 0x150u, out + 72, 8u);
                }
                break;
            }
        }
    }

    uint32_t lights = 0u;
    const uint32_t list = GuestWordAt(base, scene + 0xE64u);
    if (plausible(list)) {
        const uint32_t begin = GuestWordAt(base, list + 8u);
        const uint32_t end = GuestWordAt(base, list + 0xCu);
        if (plausible(begin) && end >= begin && end - begin < 0x40000u) {
            for (uint32_t at = begin; at + 32u <= end; at += 32u) {
                if (kHeader + (lights + 1u) * kLight > capacity) {
                    break;
                }
                const uint32_t light = GuestWordAt(base, at + 0x10u);
                if (!plausible(light) || GuestWordAt(base, light + 0xC0u) != 1u) {
                    continue;
                }
                float* row = out + kHeader + lights * kLight;
                read_floats(light + 0x120u, row, 3u);
                read_floats(light + 0x140u, row + 3, 1u);
                read_floats(light + 0xD0u, row + 4, 3u);
                row[7] = 0.0f;
                ++lights;
            }
        }
    }
    out[5] = static_cast<float>(lights);
    return kHeader + lights * kLight;
}

/// The FX MESH PARTICLES the guest's effects state (src/hooks/fx_meshes.cpp): per descriptor its
/// render twin (descriptor +4 -> `dword_8349F7AC[handle]`) as the effect's mesh renderer
/// (`sub_821CEA80` kind 4) keeps it with its messages. Per mesh 24 floats:
///   [0] descriptor (bits) [1] model hash (descriptor +128) [2] flags (descriptor +20: 0x800
///   displacement) [3..14] the world rows twin+336 (3 x vec4, translation in w, `sub_8220B6A8`)
///   [15..18] the tint twin+240 (message 17 -> PS c49, `sub_82220CA8`) [19] alpha, the state
///   block's (*(twin+176)) +104 -> +108 at its +16 (message 16; `sub_8221E5F0`) [20..22] the
///   distance fade twin+284 (full within), +288 (slope), +292 (gone beyond) [23] twin type (+4).
/// out[0] = meshes; returns the floats written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFxMeshes(float* out, uint32_t capacity) {
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const uint32_t table = GuestWordAt(base, refii::guest::Address(0x8349F7ACu));
    if (!plausible(table)) {
        return 1u;
    }
    uint32_t descriptors[1024];
    const uint32_t stated = refii::vfs::StatedFxMeshes(descriptors, 1024u);
    uint32_t used = 1u;
    uint32_t meshes = 0u;
    for (uint32_t i = 0; i < stated && used + 24u <= capacity; ++i) {
        const uint32_t descriptor = descriptors[i];
        const uint32_t handle = GuestWordAt(base, descriptor + 4u);
        if (handle == 0u || handle == 0xFFFFFFFFu || handle > 0x100000u) {
            continue;
        }
        const uint32_t twin = GuestWordAt(base, table + handle * 4u);
        if (!plausible(twin)) {
            continue;
        }
        float* m = out + used;
        std::memcpy(m + 0, &descriptor, sizeof(float));
        const uint32_t hash = GuestWordAt(base, descriptor + 128u);
        std::memcpy(m + 1, &hash, sizeof(float));
        m[2] = static_cast<float>(GuestWordAt(base, descriptor + 20u));
        read_floats(twin + 336u, m + 3, 12u);
        read_floats(twin + 240u, m + 15, 4u);
        const uint32_t state = GuestWordAt(base, twin + 176u);
        float alpha = 1.0f;
        if (plausible(state)) {
            float lanes[3];
            read_floats(state + 104u, lanes, 2u);
            read_floats(state + 16u, lanes + 2, 1u);
            alpha = lanes[0] + (lanes[1] - lanes[0]) * lanes[2];
        }
        m[19] = alpha;
        read_floats(twin + 284u, m + 20, 3u);
        m[23] = static_cast<float>(GuestWordAt(base, twin + 4u));
        used += 24u;
        ++meshes;
    }
    out[0] = static_cast<float>(meshes);
    return used;
}

/// Which heroes are drunk enough for the drunk warp, the warp builder's own test (`sub_822B62F0`:
/// component 157 +12 at or past +36): bit 0 hero one, bit 1 hero two. On split screen the warp
/// draws in each drunk player's own view (the guest builds player one's effect, the pump player
/// two's, from the same record).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDrunkPlayers(void) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    uint32_t bits = 0u;
    for (uint32_t player = 1u; player <= 2u; ++player) {
        const uint32_t hero = PlayerEntityForKey(base, player);
        const uint32_t drink = hero >= 0x10000u && hero < 0xC0000000u ? EntityComponent(base, hero, 157u) : 0u;
        if (drink < 0x10000u || drink >= 0xC0000000u) {
            continue;
        }
        const uint32_t level = GuestWordAt(base, drink + 12u);
        const uint32_t limit = GuestWordAt(base, drink + 36u);
        float a = 0.0f, b = 0.0f;
        std::memcpy(&a, &level, sizeof(a));
        std::memcpy(&b, &limit, sizeof(b));
        if (a >= b) {
            bits |= 1u << (player - 1u);
        }
    }
    return bits;
}

/// The render scene's DISPLACEMENT QUADS (typed list type 29, ctor `sub_82A4B9A8`, vtable
/// 0x820F72F0): screen-space quads the displacement renderer draws with VS 176 / PS 177 (a textured
/// quad) — as shipped only the drunk warp (`sub_822B62F0`: "Art\GUI\HUD\FXT_Fullscreen_Drunk",
/// full screen, while the drunkenness stat is past its threshold). Per quad 16 floats, from the
/// fields message 35 sets (`sub_82A81FB0`): [0..3] x0 y0 x1 y1 pixels (+76..+88), [4] z (+92),
/// [5] rotation (+96), [6..13] the corners' uv (+100..+128, in the draw's v0..v3 order), [14..15]
/// the guest's screen size (`0x834A5FE8` / +4, what the draw's g_ScreenToViewportTransform divides
/// by). out[0] = quads; returns the floats written, 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDisplacementQuads(float* out, uint32_t capacity) {
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicThemeScene(base);
    if (scene == 0u) {
        return 0u;
    }
    const auto plausible = [](uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; };
    const uint32_t screen = refii::guest::Address(0x834A5FE8u);
    const float width = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, screen)));
    const float height = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, screen + 4u)));
    const uint32_t list = scene + 3412u;
    const uint32_t nodes_begin = GuestWordAt(base, list + 4u);
    const uint32_t nodes_end = GuestWordAt(base, list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > 48u * 24u) {
        return 1u;
    }
    uint32_t quads = 0u;
    uint32_t used = 1u;
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        if (GuestWordAt(base, node) != 29u) {
            continue;
        }
        const uint32_t objects_begin = GuestWordAt(base, node + 12u);
        const uint32_t objects_end = GuestWordAt(base, node + 16u);
        if (!plausible(objects_begin) || objects_end < objects_begin || objects_end - objects_begin > 4096u) {
            continue;
        }
        for (uint32_t at = objects_begin; at + 4u <= objects_end && used + 16u <= capacity; at += 4u) {
            const uint32_t quad = GuestWordAt(base, at);
            if (!plausible(quad)) {
                continue;
            }
            float* q = out + used;
            for (uint32_t k = 0; k < 6u; ++k) {
                const uint32_t bits = GuestWordAt(base, quad + 76u + k * 4u);
                std::memcpy(q + k, &bits, sizeof(float));
            }
            for (uint32_t k = 0; k < 8u; ++k) {
                const uint32_t bits = GuestWordAt(base, quad + 100u + k * 4u);
                std::memcpy(q + 6u + k, &bits, sizeof(float));
            }
            q[14] = width;
            q[15] = height;
            used += 16u;
            ++quads;
        }
    }
    out[0] = static_cast<float>(quads);
    return used;
}

/// Fills 4 slots x 6 words: object, capacity, write cursor (+32), write consumed (+36),
/// reader consumed (+64), last read (+24). A zero object means the slot is empty.
REFII_HOST_ABI_EXPORT void RefiiHostGetSceneStreams(uint32_t* out) {
    if (out == nullptr) {
        return;
    }

    for (int i = 0; i < 24; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t object = GuestWordAt(base, refii::guest::Address(0x8349F9B0u) + slot * 4u);
        if (object < 0x10000u || object >= 0xC0000000u) {
            continue;
        }

        uint32_t* row = out + slot * 6u;
        row[0] = object;
        row[1] = read32(object + 8u);
        row[2] = read32(object + 32u);
        row[3] = read32(object + 36u);
        row[4] = read32(object + 64u);
        row[5] = read32(object + 24u);
    }
}

/// The 15 Hz pacer's own arithmetic — whether `rex_lhGameLoop_Tick` is ticking, and why. From
/// outside, a tick thread parked in a deadlock and one healthily yielding are the same silence.
/// From `sub_82276D10` (loop head 0x82276E78):
///
///     sub_8221EA28(&now);                                  ; 0x8221EA28 is `mftb`
///     seconds = ((double)now - dbl_834970A0) / dbl_834970A8
///     target  = (int)((seconds - resyncTime) * 15.0 + resyncTick)     ; 0x82276EF0
///     *(double*)(game + 0x110) = (double)(target - resyncTick) * (1/15.0) + resyncTime
///     if (target - lastTick <= 0) SwitchToThread();         ; 0x82276F1C -> 0x82277344
///     else <the whole tick body>
///
/// The spin at LR 0x82277348 is the ORDINARY fixed-step yield ("no tick is due yet"). What
/// separates a paced thread from a stuck one is whether `target` is still climbing, and
/// `game + 0x110` is that number in seconds, rewritten EVERY iteration, spin arm included.
///
/// `resyncTime` and `resyncTick` live in registers and cannot be read from here, so the two
/// globals the seconds derive from are reported instead. A zero divisor at `dbl_834970A8`
/// makes `seconds` infinite and `fctiwz` saturate; a NaN parks the comparison on the spin arm.
///
/// `0x83497124` is a SLOT INDEX, not a count: `Render_FrameCompletionCallback` (0x82B90160)
/// publishes it with `extrwi r3, r4, 1, 26` / `xori r10, r3, 1`, one bit, so it is 0 or 1. The
/// completed count is a field of the record it names,
/// `*(u32*)(0x83491D98 + 24*index + 0x10)`.
///
/// Read-only, and every value is one aligned load. <paramref name="words"/> takes 8:
///
///     [0] the game-loop object, or 0 when there is not one
///     [1] game+0x104 — the in-game simulation gate
///     [2] game+0x105 — the world-ready gate
///     [3] presented   (0x83497120)
///     [4] frame-sync slot INDEX (0x83497124), 0 or 1
///     [5] completed   (records + 24*index + 0x10)
///     [6] that slot's reader lock (+0x14)
///     [7] the app-object quit byte the loop's own continuation predicate reads
///
/// <paramref name="values"/> takes 3: sim time (game+0x110), then the timebase epoch and
/// ticks-per-second the seconds are derived from.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGameLoopPacer(uint32_t* words, uint32_t word_capacity,
                                                        double* values, uint32_t value_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto word = [&](uint32_t index, uint32_t value) {
        if (words != nullptr && index < word_capacity) {
            words[index] = value;
        }
    };
    const auto real = [&](uint32_t index, double value) {
        if (values != nullptr && index < value_capacity) {
            values[index] = value;
        }
    };
    // The guest stores a double high word first, the same way its `stfd` leaves it.
    const auto read_double = [base](uint32_t at) {
        const uint64_t bits = (static_cast<uint64_t>(GuestWordAt(base, at)) << 32) |
                              GuestWordAt(base, at + 4u);
        double out = 0.0;
        std::memcpy(&out, &bits, sizeof(out));
        return out;
    };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    for (uint32_t i = 0; i < 11u; ++i) word(i, 0u);
    for (uint32_t i = 0; i < 5u; ++i) real(i, 0.0);

    real(1, read_double(refii::guest::Address(0x834970A0u)));
    real(2, read_double(refii::guest::Address(0x834970A8u)));

    // The GUI manager's update gate and the presentation clock. `sub_821A1140` runs the
    // gameplan update — advancing every timeline and draining every presentation's event queue
    // — only while `byte_83319409 && *(u8*)(GUI+0x65)` (registered players) and the game state
    // is not 7; it then sets `GUI+0x64` (the DoString window). A presentation's root time block
    // is at `pres+852` (`{+0 time ms, +4 duration, +8 offset, +12 pauseTime, +16 flags}`); `+0`
    // is absolute milliseconds and advances ~1000 per second while the manager updates.
    {
        // The enable byte is a global: read it whether or not the in-game manager exists, or a
        // front-end sample reports "update off" for a byte that is 1 from image load.
        word(9, GuestByteAt(base, refii::guest::Address(0x83319409u)));
        const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
        if (plausible(gui)) {
            word(8, GuestByteAt(base, gui + 0x65u));
            word(10, GuestByteAt(base, gui + 0x64u));

            const uint32_t hud = GuestWordAt(base, gui + 32u);
            const uint32_t hud_pres = plausible(hud) ? GuestWordAt(base, hud + 4u) : 0u;
            if (plausible(hud_pres)) {
                real(3, static_cast<double>(GuestWordAt(base, hud_pres + 852u)));
            }

            const uint32_t manager = GuestWordAt(base, gui + 56u);
            const uint32_t screen_pres = plausible(manager) ? GuestWordAt(base, manager + refii::guest::ScreenManagerField(156u)) : 0u;
            if (plausible(screen_pres)) {
                real(4, static_cast<double>(GuestWordAt(base, screen_pres + 852u)));
            }
        }
    }

    word(3, GuestWordAt(base, refii::guest::Address(0x83497120u)));

    // The index first, then the record it names — never a fixed address: the callback
    // ping-pongs the slot on every completion.
    const uint32_t index = GuestWordAt(base, refii::guest::Address(0x83497124u));
    word(4, index);
    if (index < 2u) {
        const uint32_t record = refii::guest::Address(0x83491D98u) + index * 24u;
        word(5, GuestWordAt(base, record + 0x10u));
        word(6, GuestWordAt(base, record + 0x14u));
    }

    // The loop's continuation predicate is `app->vtbl[0x10]()` == `*(u8*)(app + 44) == 0`
    // (`sub_82185868`). Non-zero means the loop was asked to end, not that it is stuck.
    const uint32_t app = GuestWordAt(base, refii::guest::Address(0x8349E9ECu));
    if (plausible(app)) {
        word(7, GuestByteAt(base, app + 44u));
    }

    const uint32_t game_root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    if (!plausible(game_root)) {
        return 0;
    }
    const uint32_t game = GuestWordAt(base, game_root + 12u);
    if (!plausible(game)) {
        return 0;
    }

    word(0, game);
    word(1, GuestByteAt(base, game + refii::guest::GameField(0x104u)));
    word(2, GuestByteAt(base, game + refii::guest::GameField(0x105u)));
    real(0, read_double(game + 0x110u));

    return game;
}

/// One guest LHString global to a NUL-terminated ASCII buffer.
///
/// The global holds a REP POINTER, not characters — `sub_821F0240` copies `*a2` and then
/// bumps a refcount at `Rep+12` (0x821F02A4 / 0x821F02B4). Reading the Rep as if it were the
/// text is what made this return empty and skip the world build silently.
///
/// THE REP LAYOUT, decompiled rather than guessed. `sub_82208778` is the whole definition —
/// it is the only thing that ever fills one:
///
///   v7 = alloc((len + 4) & ~3);   *a1 = v7;      // +0x00 = the CHARACTER BUFFER
///   a1[1] = len;                                 // +0x04 = length
///   a1[2] = (2 * ((len + 4) & ~3)) | (a1[2] & 1);// +0x08 = capacity<<1 | flag
///   *(BYTE *)(v7 + len) = 0;                     // NUL, so it is narrow, not UTF-16
///   XMemCpy(*a1, a2, len);                       // one byte per character
///
/// Read the layout, do not probe for it. A heuristic that tries candidate pointers against
/// two encodings and takes whichever comes out printable does not fail loudly when it is
/// wrong — it returns plausible rubbish, and one global read as "TravelRS_Coach", "P ",
/// "Albion" and "Mh" across four consecutive runs.
static bool read_guest_name(const uint8_t* base, uint32_t global, char* out,
                            uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return false;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t rep = read32(global);
    if (rep < 0x10000u || rep >= 0xC0000000u) {
        return false;
    }

    const uint32_t chars = read32(rep);
    const uint32_t length = read32(rep + 4u);
    if (chars < 0x10000u || chars >= 0xC0000000u || length == 0u || length >= capacity) {
        out[0] = '\0';
        return false;
    }

    for (uint32_t i = 0; i < length; ++i) {
        const char c = static_cast<char>(GuestByteAt(base, chars + i));

        // A name here is short and printable. Anything else means the Rep was freed or torn
        // under us, and half a name is worse than none.
        if (c < 0x20 || c > 0x7E) {
            out[0] = '\0';
            return false;
        }

        out[i] = c;
    }

    out[length] = '\0';
    return true;
}

/// Where the guest is going. `sub_82379628` — the sole new-game entry — writes these before
/// it latches the mode change, into four CONSECUTIVE globals:
///
///     g_StartWorldName    = "Albion"                 0x83497584
///     g_StartLevelName    = "BWSSlums"               0x83497588
///     g_StartScenarioName = "DefaultScenario"        0x8349758C   (0x8237972C)
///     g_StartEntranceName = "QC010_ChildhoodStart"   0x83497590
///
/// Fills three NUL-terminated buffers. Returns 1 when the level name came out non-empty —
/// that is the one a caller cannot proceed without. The scenario has its own entry point
/// because adding a parameter here would break the ABI for a value most callers do not want.
REFII_HOST_ABI_EXPORT int RefiiHostGetStartLevel(char* world, char* level, char* entrance, uint32_t capacity) {
    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    read_guest_name(base, refii::guest::Address(0x83497584u), world, capacity);
    read_guest_name(base, refii::guest::Address(0x83497590u), entrance, capacity);
    return read_guest_name(base, refii::guest::Address(0x83497588u), level, capacity) ? 1 : 0;
}

/// The world manager, from the one fixed global that reaches it.
///
/// Six hops, and every one of them was quoted out of `sub_823BBA70` (0x823BBA8C onward), which
/// emits the whole chain in seven consecutive instructions. Two other call sites — the savegame
/// loader at 0x82447B44 and the Lua registrar at 0x82458FFC — emit the identical form, which is
/// what a canonical accessor looks like when it has been inlined everywhere.
///
/// NOT CACHED across a level load: the chain is rebuilt, so a stale pointer here is a pointer
/// into a freed object.
static uint32_t world_manager(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        if (at < 0x10000u || at >= 0xC0000000u) {
            return 0;
        }
        return u32(at + offset);
    };

    uint32_t at = u32(refii::guest::Address(0x83496AB8u));
    at = step(at, 0x0Cu);
    at = step(at, 0x58u);
    at = step(at, 0x04u);
    at = step(at, 0x08u);
    at = step(at, 0x04u);
    return (at >= 0x10000u && at < 0xC0000000u) ? at : 0;
}

/// WHERE THE GUEST ACTUALLY IS — as opposed to where a New Game asked it to go.
///
/// `RefiiHostGetStartLevel` reads `g_StartWorldName`/`g_StartLevelName` (0x83497584/88), and a
/// host that polls those to notice a level change is reading the wrong thing. Every writer of
/// those globals is a GAME-START path: the CRT static initialisers ("TestWorld"/"TW_Level1"),
/// the `SetInitialWorldName`/`SetInitialLevelName` Lua natives, the new-game entry
/// `sub_82379628`, `Frontend_SetStartGameTarget`, the dev override, the network-join handler
/// and the debug level-select menu.
///
/// NOT ONE TRANSITION PATH TOUCHES THEM. Doors and teleporters (`sub_8283C7F8`), fast travel
/// (`sub_82519B08`), the `World.LoadLevel` Lua native (`sub_82350D20`) and a level reload
/// (`sub_8231C988`) all reach `World_RequestLevelChange` (0x82350B20), which puts the
/// destination on the COMMAND OBJECT at cmd+0x14/+0x18/+0x1C and never in a global. So polling
/// the start globals notices a new game and nothing else — and it appears to work in a dev
/// build only because the debug menu happens to rewrite them before it calls `Debug.LoadLevel`.
///
/// This reads the standing World and Level instead, which is mechanism-independent: new game,
/// load-save, door, teleporter, fast travel and network join all land here through the single
/// `World_LoadLevel_Execute` (0x8231C608) -> Level ctor (0x82354228) path.
///
/// OFFSETS, EACH AGAINST ITS OWN BASE, verified in the decompile rather than taken on report:
///
///     world = world_manager(base)   // despite the name this IS the World: the helper's last
///                                   // two hops are *(mgr+8) then +4, which is exactly the
///                                   // `*(*(a1+8)+4)` that 0x82350B64 dereferences
///       world + 0x14  -> Rep, world name    (written by `sub_822651A0(a1+20, a2)` @0x8231C664)
///       world + 0x24  -> holder; *(holder+4) = Level
///     level + 0xAC -> Rep, level name       (`sub_821F0240(a1+172, a3)` @0x823542E0)
///     level + 0xB0 -> Rep, entrance name    (ctor seeds it "PlayerStart" @0x823542F4; state 5
///                                            of CmdLevelChange_Update overwrites it)
///     level + 0xB4 =  u32 key               (`*(a1+180) = a2`, and the caller passes
///                                            `World_ComputeWorldLevelFnv1Key(a1,a2,a3)`)
///
/// THE KEY IS THE THING TO POLL. It is one aligned dword that changes on exactly the
/// transitions that reload a level, so a host can compare it frame to frame without parsing a
/// string or caring which mechanism moved the player. The names are for humans and for
/// resolving the region; the key is the edge.
///
/// `out_key` receives it. Returns 1 when a level is standing, 0 when none is — which is the
/// ordinary state before the first load and during the window when the old Level has been
/// released and the new one not yet constructed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentLevel(char* world, char* level, char* entrance, uint32_t capacity,
                                  uint32_t* out_key) {
    if (out_key != nullptr) {
        *out_key = 0u;
    }

    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t world_object = world_manager(base);
    if (world_object == 0) {
        return 0;
    }

    const uint32_t holder = u32(world_object + 0x24u);
    if (holder < 0x10000u || holder >= 0xC0000000u) {
        return 0;  // no level standing
    }

    const uint32_t level_object = u32(holder + 4u);
    if (level_object < 0x10000u || level_object >= 0xC0000000u) {
        return 0;
    }

    if (out_key != nullptr) {
        *out_key = u32(level_object + 0xB4u);
    }

    read_guest_name(base, world_object + 0x14u, world, capacity);
    read_guest_name(base, level_object + 0xB0u, entrance, capacity);

    // The LEVEL name is the one a caller cannot proceed without, so it decides the result.
    return read_guest_name(base, level_object + 0xACu, level, capacity) ? 1 : 0;
}

/// The scenario the guest is ACTUALLY in, for the level it currently has loaded.
///
/// This is the live answer, where RefiiHostGetStartScenario is only the value a new game began
/// with. `Layers.ActivateScenario` rewrites it and reloads the level, so a quest advancing the
/// story changes which map and which placements the level has.
///
/// The lookup is the guest's own: the level caches its FNV-1 key at level+180, and the scenario
/// map is a vector of {u32 key, LHString name} at worldMgr+88..+92. THE SORT IS LAZY — worldMgr
/// +101 is a dirty flag and `sub_82321228` only sorts when it is set, so a host polling between
/// an insert and the flush must scan linearly or it will silently miss the entry it wants.
REFII_HOST_ABI_EXPORT int RefiiHostGetActiveScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0) {
        return 0;
    }

    scenario[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t holder = u32(wm + 36u);
    if (holder < 0x10000u || holder >= 0xC0000000u) {
        return 0;  // no level loaded
    }

    const uint32_t level = u32(holder + 4u);
    if (level < 0x10000u || level >= 0xC0000000u) {
        return 0;
    }

    const uint32_t key = u32(level + 180u);
    const uint32_t first = u32(wm + 88u);
    const uint32_t last = u32(wm + 92u);
    if (first < 0x10000u || last < first || ((last - first) % 8u) != 0u) {
        return 0;
    }

    // Linear either way. The vector is at most a few dozen entries, and a binary search that is
    // wrong when the dirty flag is set is worse than a scan that is always right.
    for (uint32_t at = first; at + 8u <= last; at += 8u) {
        if (u32(at) != key) {
            continue;
        }

        return read_guest_name(base, at + 4u, scenario, capacity) ? 1 : 0;
    }

    return 0;
}

/// The FNV-1 hashes of the layers the guest currently has active.
///
/// Layers gate ENTITY INSTANTIATION — the `.engine_level` marks elements AlwaysOn or
/// InitiallyOn and `Layers.ActivateLayer` turns the rest on — so a host that places everything
/// it finds will place things the guest has not.
///
/// The container is an MSVC `std::list<uint32_t>`, which the overflow guard gives away by
/// raising "list<T> too long" (`sub_82322878`). worldMgr+60 is the _Myproxy and reads as a
/// perfectly plausible pointer; the head is +64 and the size is +68. Nodes are 12 bytes,
/// {_Next, _Prev, u32 hash}.
///
/// There is a SECOND identically shaped list at worldMgr+72; it is not this one and its meaning
/// is unproven.
///
/// Returns how many hashes were written. Hashes are FNV-1 over the layer name's EXACT bytes —
/// no lowercasing and no separator normalisation, unlike the world+level key.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveLayers(uint32_t* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t head = u32(wm + 64u);
    if (head < 0x10000u || head >= 0xC0000000u) {
        return 0;
    }

    const uint32_t size = u32(wm + 68u);
    if (size > 4096u) {
        return 0;  // not a list we recognise
    }

    uint32_t written = 0;
    uint32_t node = u32(head);
    for (uint32_t guard = 0; guard <= size + 8u; ++guard) {
        if (node == head || node < 0x10000u || node >= 0xC0000000u) {
            break;
        }

        if (out != nullptr && written < capacity) {
            out[written] = u32(node + 8u);
        }

        ++written;
        node = u32(node);
    }

    return written;
}

/// THE SCENARIO, which is what actually picks the map.
///
/// `Level_BuildDataWorldsFilePath` (0x8250EED0) builds
/// `Data\Worlds\<World>\<Level>\<Scenario>\<Scenario>.list` and the loader takes every
/// .ghf that manifest names — `Level_LoadHeightfieldGhfEntry` (0x8250ECE0) does no filtering
/// of its own. So a level with five heightfields has five scenarios, and without this value a
/// host has to guess which one; picking by name order put a childhood entrance in chapter 2's
/// Bowerstone, which reads as the wrong season because the seasons ARE the scenarios.
///
/// This returns the START scenario. The LIVE one is a world-manager map keyed by
/// FNV-1(world + level) that Lua's `Layers.ActivateScenario` writes, and it is not exposed
/// yet — so this is right for a new game and stale once a quest moves the story on.
REFII_HOST_ABI_EXPORT int RefiiHostGetStartScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0) {
        return 0;
    }

    scenario[0] = '\0';

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    return read_guest_name(base, refii::guest::Address(0x8349758Cu), scenario, capacity) ? 1 : 0;
}

/// Non-zero while the guest is IN its front end (title screen / main menu).
///
/// IDA-proven, and deliberately NOT the mode singleton `dword_83496AC0`. That global is a
/// pointer stored when `sub_8231A908` CONSTRUCTS the front-end mode (0x8231A988) and it
/// stays set forever after — so a host reading it would keep the menu on screen right
/// through gameplay. `byte_83496AC4` is the flag the front end actually maintains, with
/// exactly two writers: `= 1` in its OnEnter (0x82319360) and `= 0` in its OnExit
/// (0x82319B24).
///
/// The guest only enters that mode once every startup logo has played AND the "Permanent
/// Bank Mounter" handshake has completed — the boot-movie loop blocks at 0x82377D74 on
/// `sub_822F3B20` until the mounter sets its +5 byte. So this going true is the title
/// SAYING the boot sequence is over, rather than the host timing a movie and guessing.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndState(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // byte_83496AC4 — one byte, so endianness does not enter into it.
    return GuestByteAt(base, refii::guest::Address(0x83496AC4u));
}

/// Every element of the active screen: its identity, its slide, and its live TEXT.
///
/// The text matters as much as the slide. A shipped `.bgf` carries placeholders — the
/// dialog panel authors "DEFAULT_TEXT_ID" — and the real string is written at runtime by
/// SetText (`sub_82425DD0`, which stores to the record at `**(element+4) + 140` and marks
/// `record+144` "set externally"). Other labels are tags the guest resolves through a
/// table that is NOT the Babel book — `GUI_CONTINUE_WITHOUT_SAVING` is absent from
/// `book.babel` under either FNV-1 variant. Reading what the element actually holds makes
/// both cases moot: it is the string the game is displaying, whatever produced it.
///
/// The string is a refcounted Rep: `+0` UTF-16 data, `+4` length in code units, `+8`
/// capacity, `+12` refcount. The characters are BIG-endian, like everything else the
/// guest wrote (`sub_822D6260` compares them with `lhz`, stride 2).
///
/// Two buffers so one walk fills both:
///   `words`  — 3 per element: {name hash, current slide, text offset}, where the offset
///              is an index into `text` in UTF-16 units, or 0xFFFFFFFF for no text.
///   `text`   — the strings, host-endian UTF-16, each NUL-terminated.
///
/// Returns the number of ELEMENTS available. Pass nulls to size the buffers.
/// The front end as a MODEL, not as widgets — what a natively rebuilt menu needs.
///
/// The element tree is not where the front end's meaning lives, so mirroring it is not enough.
/// The main menu has no C++ item list and no C++ highlight: items, order,
/// nesting and the highlight are all inside the shipped Lua object `g_Menu`. `sub_826E2220`
/// SPAWNS seven items into it one call at a time and keeps none of them, and root-menu
/// Up/Down never touch C++ state at all — they forward strings to Lua.
///
/// So there is nothing to read that says "item 3 is highlighted". A native menu owns its own
/// highlight; what it needs from the guest is which entries EXIST, because the guest's own
/// spawn conditions decide that and a host list that disagrees would offer a menu the guest
/// will not honour.
///
/// The two spawn conditions, at the instruction level:
///   signed in  — `lwz r4, dword_83496AF8` / `lwz r10, dword_83496A70` / `slw r7, r8, r4` /
///                `and r5, r7, r10`, and a zero result skips the Load Game spawn entirely.
///   languages  — `lwz r11, dword_83319934` / `cmplwi cr6, r11, 1` / `ble` past the spawn,
///                so the Language entry exists only for MORE THAN ONE installed language.
///
/// LOAD GAME IS NOT HERE, and that is deliberate. Its condition is signed-in AND at least one
/// save, and the save count comes from `sub_82443308`, which is a live
/// `XamContentCreateEnumerator` — a guest CALL, not a memory read. Everything in this bridge
/// is call-free by design, so the count is left to the caller, which in this port owns
/// storage anyway. Reporting a guessed count would be worse than reporting none.
///
/// Fills 11 words. Returns 1 when the front-end GUI is up, 0 otherwise.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndModel(uint32_t* out) {
    if (out == nullptr) {
        return 0u;
    }

    for (int i = 0; i < 11; ++i) {
        out[i] = 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x820A924Cu)) {
        return 0u;  // the base-only instance the game republishes — no front end here
    }

    const uint32_t title = read32(gui + 180u);
    const uint32_t menu = read32(gui + 184u);
    const uint32_t active = read32(gui + 188u);

    const uint32_t user = GuestWordAt(base, refii::guest::Address(0x83496AF8u));
    const uint32_t signed_mask = GuestWordAt(base, refii::guest::Address(0x83496A70u));
    const bool signed_in = user < 32u && ((1u << user) & signed_mask) != 0u;

    out[0] = signed_in ? 1u : 0u;
    out[1] = user;
    out[2] = GuestWordAt(base, refii::guest::Address(0x83319934u));  // installed language count; > 1 spawns "Language"
    out[3] = GuestWordAt(base, refii::guest::Address(0x83319938u));  // current language index

    // The "new downloadable content" glyph: shown when the flag is set and the available
    // count differs from the count already seen.
    const uint32_t available = GuestWordAt(base, refii::guest::Address(0x83496B20u));
    const uint32_t seen = GuestWordAt(base, refii::guest::Address(0x83496B24u));
    out[4] = (GuestByteAt(base, refii::guest::Address(0x8331963Eu)) != 0u && (int32_t)available > 0 && available != seen) ? 1u : 0u;

    // WHICH SCREEN, by pointer identity. There is no title state that means "the menu is up" —
    // state 11 is consumed and reset to 0 in the same update, so reading it that way reports
    // the handover as never having happened.
    out[5] = (plausible(menu) && active == menu) ? 1u : 0u;
    out[6] = plausible(menu) ? read32(menu + 24u) : 0u;
    out[7] = plausible(title) ? read32(title + 24u) : 0u;
    out[8] = plausible(title) ? GuestByteAt(base, title + refii::guest::FrontEndTitleField(80u)) : 0u;  // the CAN_PRESS_A gate

    // The Load Game card cursor. NOT the menu highlight — it moves only on Left/Right and only
    // in menu state 5, and it indexes the save-card vector at menu+84.
    out[9] = plausible(menu) ? read32(menu + refii::guest::FrontEndMenuField(72u)) : 0u;

    // HOW MANY SAVES THE GUEST HAS ENUMERATED, from the same vector its own "Load Game"
    // branch tests before deciding whether to offer one:
    //
    //     826e03d0  lwz    r11, 8(r28)     ; end        (screen+88)
    //     826e03d8  lwz    r10, 4(r28)     ; begin      (screen+84)
    //     826e03dc  subf   r9, r10, r11
    //     826e03e0  srawi. r8, r9, 2       ; / 4 = count
    //     826e03e4  beq    -> "no save games"
    //
    // A std::vector of 4-byte entries, so the count is the byte span over 4. This is a stated
    // value read with no call, which a host-side guess at storage cannot be.
    //
    // CAVEAT, and it is why this is named "enumerated" rather than "saves": the guest fills
    // this vector when it enumerates, which it does on its own schedule. A zero here means
    // "the guest has not told us about any", NOT "there are none on the device" — the guest
    // decides that with a live XamContentCreateEnumerator, which is a call and not a read.
    uint32_t enumerated = 0;
    if (plausible(menu)) {
        const uint32_t begin = read32(menu + refii::guest::FrontEndMenuField(84u));
        const uint32_t end = read32(menu + refii::guest::FrontEndMenuField(88u));
        if (end >= begin && (end - begin) < 0x10000u) {
            enumerated = (end - begin) / 4u;
        }
    }
    out[10] = enumerated;

    return 1u;
}

/// The save cards the guest has ENUMERATED, with the text it built for each one.
///
/// WHY THIS IS A READ AND THE COUNT IS NOT. `RefiiHostGetSaveState` has to go through the tick
/// pump because "how many saves exist" is `sub_82443308`, a live `XamContentCreateEnumerator`
/// — a guest CALL. This is the other side of that: once the player has chosen "Load Game", the
/// guest has ALREADY made the call, and everything the card fan needs is sitting in three
/// containers on the front-end screen object. So a host that draws the fan reads; it does not
/// ask.
///
/// WHERE, all offsets from the front-end main-menu screen at `GUI+184`, and every one of them
/// verified in the decompile rather than taken on report:
///
///   +84 / +88   `vector<CCharString>` of `szFileName`, stride 4. This is the COUNT: the
///               dispatcher's own test at 0x826E03E0 is `srawi. r8, r9, 2` over `+88 - +84`.
///   +132        begin of the container of finished card LHStrings, stride 4.
///   +72         the selected card index.
///   +76         the base card slot, which the guest uses to centre a short row.
///
/// THE TWO CONTAINERS ARE 1:1 AND THAT IS LOAD-BEARING, because this indexes one by the other's
/// count. `sub_826E14F8` is the loop that fills the text, and it is bounded by the FILENAME
/// vector — `while ( v5 < (a1[22] - a1[21]) >> 2 )` at 0x826E1AB4 — pushing exactly one string
/// per file through `sub_8243BE88(a1 + 32, &v52)` at 0x826E19D8. It is still bounds-checked
/// below: a torn read mid-enumeration is worth surviving, and half a fan is better than a walk
/// off the end of a container.
///
/// WHAT THE TEXT IS. Not a tag — a finished, localised string the guest assembles per card:
/// seeded from the container's wide `szDisplayName` (`sub_82287C28(&v52, v7 + a1[25])`), then
/// the level name resolved through the `LevelNameTextTags` GDB table, then a save-context line.
/// When the header will not parse it is a version message instead, or
/// `GUI_CORRUPT_SAVE_GAME_WARNING` when the version reads 0. All of which is why a host must
/// take this string rather than build its own: the failure text is the guest's answer too.
///
/// Names are written to `text` as NUL-separated UTF-8 in card order. Returns the number of
/// cards, or 0 when the front end is not up. Pass nulls to ask for the count alone.
/// Defined below, beside the HUD readers that share it — see `write_guest_wide_string`.
static uint32_t write_guest_wide_string(const uint8_t* base, uint32_t at, char* out,
                                        uint32_t capacity);

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveCards(uint32_t* out_cursor, uint32_t* out_base_slot,
                               char* text, uint32_t capacity) {
    if (out_cursor != nullptr) {
        *out_cursor = 0u;
    }
    if (out_base_slot != nullptr) {
        *out_base_slot = 0u;
    }
    if (text != nullptr && capacity > 0) {
        text[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x820A924Cu)) {
        return 0u;  // the base-only instance the game republishes — no front end here
    }

    const uint32_t screen = read32(gui + 184u);
    if (!plausible(screen)) {
        return 0u;
    }

    const uint32_t files_begin = read32(screen + refii::guest::FrontEndMenuField(84u));
    const uint32_t files_end = read32(screen + refii::guest::FrontEndMenuField(88u));
    if (!plausible(files_begin) || files_end < files_begin ||
        (files_end - files_begin) >= 0x10000u) {
        return 0u;
    }

    const uint32_t count = (files_end - files_begin) / 4u;
    if (count == 0u) {
        return 0u;
    }

    if (out_cursor != nullptr) {
        *out_cursor = read32(screen + refii::guest::FrontEndMenuField(72u));
    }
    if (out_base_slot != nullptr) {
        *out_base_slot = read32(screen + refii::guest::FrontEndMenuField(76u));
    }

    if (text == nullptr || capacity == 0u) {
        return count;
    }

    // The description container's OWN bound, not the file count's. They agree by construction
    // — see the remarks — and disagreeing means the enumeration is half-done, which is a state
    // a host polling every frame will genuinely observe.
    const uint32_t text_begin = read32(screen + refii::guest::FrontEndMenuField(132u));
    const uint32_t text_end = read32(screen + refii::guest::FrontEndMenuField(136u));
    const uint32_t described =
        (plausible(text_begin) && text_end >= text_begin && (text_end - text_begin) < 0x10000u)
            ? (text_end - text_begin) / 4u
            : 0u;

    // ONE ENCODER, shared with the HUD text reader — see `write_guest_wide_string`. A card whose
    // text is not there yet still gets an entry, so the index a caller hands back to `LoadSave`
    // stays the guest's own rather than shifting under a missing description.
    uint32_t written = 0;
    for (uint32_t i = 0; i < count; ++i) {
        written += write_guest_wide_string(
            base, i < described ? text_begin + (i * 4u) : 0u, text + written,
            capacity > written ? capacity - written : 0u);
    }

    return count;
}

/// The TEXTURE each element of a presentation is currently showing, by element ordinal.
///
/// WHY THIS EXISTS: the load-game cards. Their portraits are not in the file — the artist
/// authored every one as the placeholder `art\gui\questscreenshots\loadingcardback.tex`, and
/// the guest stamps the real one per card at load time. `sub_826E1408` is where, and it is
/// worth quoting because the indirection is easy to get wrong:
///
/// <code>
/// sub_824714E8(&amp;v15, a1[37] + 40 * i + 16);   // record+16 = QuestName -> a texture HASH
/// v11 = sub_82426798(name, a1[5]);            // "loadgame.Group.CardN.Group.screenshot"
/// v12 = *(int **)(v11 + 4);
/// v13 = *v12;                                 // record = **(element + 4)
/// if ( v8 != *(_DWORD *)(v13 + 512) ) {
///     *(_BYTE *)(v13 + 548) = 1;              // dirty
///     *(_DWORD *)(v13 + 512) = v8;            // the texture hash
/// }
/// </code>
///
/// So the value is at **`**(element + 4) + 512`** — the SAME double dereference the element's
/// live text uses at `+140`, against the same record. An offset quoted against `element` rather
/// than against that record looks perfectly reasonable and reads garbage.
///
/// TAKE THE HASH, DO NOT RE-DERIVE IT. `sub_82471638` maps a quest prefix onto a screenshot
/// name (`QC`/`QO`/`QJ` take the token before `_`, `QR` becomes `QR_<tail>`, `QD010_*` maps to
/// QO700/710/720, everything else is `albion`) and appends `_Epilogue_Screen_Image`. Host code
/// could reimplement that from the save record's QuestName, and it would be reimplementing a
/// decision the guest has already made and published — which is the thing this port does not
/// do. `Engine_TextureNameTable` inverts the hash back to a path on the Godot side.
///
/// BY ORDINAL, in the same depth-first order as the element walk, so a caller that already
/// knows an element's ordinal — every caller does, that is how slides and text are keyed —
/// can index straight in. Zero where an element has no texture record, which is most of them.
///
/// Returns the number of elements walked. Pass null to count them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiTextureHashes(uint32_t presentation, uint32_t* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    // ZERO MEANS THE FRONT END'S ACTIVE SCREEN, the same convention `RefiiHostGetUiElements`
    // uses — and it has to, because a caller joining the two arrays by ordinal must be able to
    // ask both about the same presentation the same way. The `gui+188` chain is the front end's
    // only and is dead in game; see that function for why.
    if (presentation == 0u) {
        const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
        if (!plausible(gui)) {
            return 0;
        }

        const uint32_t active = read32(gui + 188u);
        if (!plausible(active)) {
            return 0;
        }

        presentation = read32(active + 20u);
    }

    if (!plausible(presentation)) {
        return 0;
    }

    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    constexpr uint32_t kMaxElements = 16384u;
    uint32_t found = 0;
    uint32_t element = root;

    while (element != 0 && found < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        if (out != nullptr && found < capacity) {
            uint32_t hash = 0;

            // The record is two dereferences down and either can be absent — most elements
            // have no texture record at all, and reading +512 off whatever `+4` happened to
            // hold for those would be inventing a texture per widget.
            const uint32_t holder = read32(element + 4u);
            if (plausible(holder)) {
                const uint32_t record = read32(holder);
                if (plausible(record)) {
                    hash = read32(record + 512u);
                }
            }

            out[found] = hash;
        }

        ++found;

        // Down first, then along, then climb — the identical traversal the element reader
        // uses, because the ORDINALS have to agree between the two or a caller joining them
        // would read one element's texture against another's slide.
        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        // Bounded: a torn tree whose parent links form a cycle would otherwise hold the
        // caller here for ever.
        int climbed = 0;
        while (element != 0) {
            if (element == root || ++climbed > 16384) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0;
        }
    }

    return found;
}

/// Every live value the in-game HUD's METERS are holding, keyed by the name each is published
/// under.
///
/// THIS IS THE HUD'S WHOLE VALUE CONTRACT, and it is one read. Unlike the front end, the HUD
/// has no script to mirror: `hud.lua` carries no layout tables, no timing constants and — over
/// all 587 shipped chunks — no reader for a single hero stat. Its `g_HUD.State` health and mana
/// numbers are Anark authoring placeholders, each written in one chunk and read in none. What
/// the HUD has instead is a binding authored in `hud.bgf` — an element carries
/// `MeterName = "PlayerHealth"` — and C++ publishes a percentage against FNV-1 of that string
/// every frame. So a host reads the published values and draws whichever elements the FILE says
/// they belong to; nothing here has to know what a name MEANS.
///
/// THE CHAIN, every hop verified in the decompile rather than taken on report:
///
///   G        = *(0x83496B08)
///   GUIMgr   = *(G + 0x20)
///   vector   = *(GUIMgr + 0x20)         ; begin at +28, end at +32, stride 4
///   registry = *(begin + 4)             ; element [1] — the METER registry
///
/// The index is the guest's own: `sub_825E8DE0` bounds-checks the vector for at least two
/// entries and then passes `*(v31 + 4)` to `sub_823FE210` when it writes PlayerHealth
/// (0x825E9074..0x825E90D0). Text and pulsers live at other indices in the same vector and are
/// deliberately NOT read here — a second index taken on inference would be an offset reaching
/// running code without a decompile behind it.
///
/// THE MAP is a plain MSVC `std::_Tree`, and `sub_82375480` (its lower_bound) states the whole
/// layout without ambiguity:
///
///     v3 = *(map + 4);              // _Myhead, the sentinel
///     v4 = v3[1];                   // head+4 = _Parent = the ROOT
///     while ( !*((_BYTE *)v4 + 21) )        // node+21 = _Isnil
///       if ( (unsigned)v4[3] >= *a3 )       // node+12 = the KEY, unsigned
///         { v3 = v4; v4 = (int *)*v4; }     // node+0  = _Left
///       else v4 = (int *)v4[2];             // node+8  = _Right
///
/// and `sub_822A9C60` returns `*(node + 16)` — node+16 is the mapped RECORD. The map object
/// itself sits at `registry+4`, so `*(registry+8)` is `_Myhead`.
///
/// A RECORD IS A VECTOR OF ELEMENTS, not one element: `record+4`/`record+8` are begin/end,
/// stride 4, and the setters walk the whole span (`sub_823FE2D0` at 0x823FE3A8 does
/// `r10 += 4; cmplw r10, *(r3+8); bne`). One name genuinely drives several — "TopBoxY" is on
/// five elements and "CornerY" on eight — so the value is per NAME and the host applies it to
/// every element the file bound to that name.
///
/// THE VALUE IS `element + 0x14`, the CURRENT percent, 0..100. Its two neighbours are what
/// prove the roles: `sub_823FE2D0` writes the clamped target to `+0x1C`, zero to `+0x0C`, and
/// then either `rate = 0` into `+0x18` for an immediate set, or
/// `(target - *(float *)(e + 0x14)) / seconds` into `+0x18` for a tween — reading `+0x14` as
/// the value it is interpolating FROM. Taking `+0x14` rather than `+0x1C` is therefore taking
/// what is on screen now instead of where it is heading, which is what a bar should draw.
///
/// Fills two parallel arrays. Returns the number of meters FOUND, which may exceed
/// `capacity` — a caller that gets a bigger number than it asked for should resize and ask
/// again rather than assume it saw everything. Pass nulls to size the buffers.
/// The GUI's typed element registries, by index into the vector at `*(GUIMgr + 0x20)`.
///
/// NOT INFERRED. `sub_827F0F00` writes each sub-registry at a fixed offset paired with its own
/// vtable constant, and every one of those vtables' slot [3] decompiles to a one-line
/// `sub_8222CED0(a1, "<name>")` returning the `.bgf` attribute it binds on. The full set is
/// [0] ParticleEffect, [1] MeterName, [2] SlideFaderName, [3] SlideFader2Name, [4] PulserName,
/// [5] BoxName, [6] TextName, [7] an inert base instance, [8] TinterName, [9] SlideToggleName,
/// [10] SlideSetName, [11] PrimDescName, [12] ImageName.
///
/// Only the two this file reads are named here — an unused constant for a registry nobody
/// walks would be a claim with no code behind it to keep it honest.
constexpr uint32_t kGuiRegistryMeter = 1u;
constexpr uint32_t kGuiRegistryPulser = 4u;
constexpr uint32_t kGuiRegistryText = 6u;

/// Encodes the guest LHString whose HANDLE sits at `at` as NUL-terminated UTF-8 into `out`.
///
/// Returns the bytes written INCLUDING the terminator, so a caller packing several strings back
/// to back advances by the return value. Writes just the terminator for an absent or unreadable
/// string, which keeps one entry per name even when a name has no text yet — a caller joining
/// parallel arrays must not have them slip.
///
/// The Rep layout is the guest's usual one, verified in `sub_82425DD0`'s neighbours and used
/// identically by the element-text walker: `+0` UTF-16 data, `+4` length in code units, and the
/// characters are BIG-endian like everything else the guest wrote. Only the basic plane is
/// handled — a surrogate pair would be a string outside it, and emitting a lone surrogate is
/// worse than stopping at it.
static uint32_t write_guest_wide_string(const uint8_t* base, uint32_t at, char* out,
                                        uint32_t capacity) {
    if (out == nullptr || capacity == 0u) {
        return 0u;
    }

    // `at == 0` is the caller's way of saying "no string here" — the save-card reader uses it
    // for a card whose description has not been built yet. Guarded rather than left to the
    // plausibility test below, because address 0 is a real, readable place in the guest's
    // address space and reading it would be a genuine read of the wrong thing.
    if (at == 0u) {
        out[0] = '\0';
        return 1u;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    uint32_t written = 0;
    const uint32_t rep = read32(at);
    if (plausible(rep)) {
        const uint32_t chars = read32(rep);
        const uint32_t length = read32(rep + 4u);
        if (plausible(chars) && length > 0u && length < 512u) {
            for (uint32_t c = 0; c < length; ++c) {
                const uint16_t unit = GuestHalfAt(base, chars + (c * 2u));
                if (unit == 0) {
                    break;
                }

                char encoded[3];
                uint32_t bytes = 0;
                if (unit < 0x80u) {
                    encoded[0] = static_cast<char>(unit);
                    bytes = 1;
                } else if (unit < 0x800u) {
                    encoded[0] = static_cast<char>(0xC0u | (unit >> 6));
                    encoded[1] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    bytes = 2;
                } else {
                    encoded[0] = static_cast<char>(0xE0u | (unit >> 12));
                    encoded[1] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                    encoded[2] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    bytes = 3;
                }

                if (written + bytes + 1u >= capacity) {
                    break;
                }

                for (uint32_t b = 0; b < bytes; ++b) {
                    out[written++] = encoded[b];
                }
            }
        }
    }

    out[written++] = '\0';
    return written;
}

/// Walks a guest `std::map<uint32, uint32>` into parallel key/value arrays.
///
/// Node layout from `sub_823C5E00` (the HUD script store's `operator[]`): `+0` left, `+4`
/// parent, `+8` right, `+12` KEY, `+16` VALUE, `+21` the `_Isnil` sentinel byte; `map+4` is
/// `_Myhead`, whose `_Parent` is the root. Bounded, explicit stack: this runs on Godot's thread
/// against memory the guest is mutating, so a torn pointer must cost a truncated answer.
///
/// `map_object` is the address of the map ITSELF, not of the container holding it.
static uint32_t walk_guest_u32_map(const uint8_t* base, uint32_t map_object, uint32_t* out_keys,
                                   uint32_t* out_values, uint32_t capacity) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(map_object)) {
        return 0u;
    }

    const uint32_t head = read32(map_object + 4u);
    if (!plausible(head)) {
        return 0u;
    }

    const uint32_t root = read32(head + 4u);
    if (!plausible(root)) {
        return 0u;
    }

    constexpr uint32_t kMaxDepth = 64u;
    constexpr uint32_t kMaxNames = 512u;

    uint32_t stack[kMaxDepth];
    uint32_t depth = 0;
    uint32_t found = 0;

    stack[depth++] = root;

    while (depth > 0 && found < kMaxNames) {
        const uint32_t node = stack[--depth];
        if (!plausible(node) || GuestByteAt(base, node + 21u) != 0u) {
            continue;
        }

        if (out_keys != nullptr && out_values != nullptr && found < capacity) {
            out_keys[found] = read32(node + 12u);
            out_values[found] = read32(node + 16u);
        }

        ++found;

        if (depth + 2u <= kMaxDepth) {
            stack[depth++] = read32(node + 0u);
            stack[depth++] = read32(node + 8u);
        }
    }

    return found;
}

/// The HUD SCRIPT STORE — `*(*(0x83496B08 + 0x20) + 108)`, or 0 when the GUI is not up.
///
/// The element registries hold only the C++ publishers' ~20 named meters, nearly all scoped to
/// one screen; only `PlayerHealth`/`PlayerHealthMax` persist through gameplay, edge-triggered
/// (`sub_825E92C8` republishes only `if (*(_DWORD*)(v5+8) != a2)`).
///
/// The rest of `hud.bgf` is driven from the shipped Lua, by name, through four GUI natives
/// (`SetMeterRatioOverTime`, `SetMeterPercentOverTime`, `SetGUIText`, `ToggleElement`) which hash
/// the name at call time. Each caches its value in this store before forwarding it:
///
///     sub_82306928  sub_823C59E0(*(*(0x83496B08 + 32) + 108), Fnv1(name), ratio, time)
///     sub_823C59E0  *(float *)sub_823C5E00(store + 16, &hash) = ratio;     // 0x823c5a90
///     sub_823C5B00  *(_DWORD *)sub_8236A9B0(store + 28, &hash) = textId;   // 0x823c5b94
///
/// The store survives a presentation reload — `sub_823C4A88` serialises these two maps into
/// the save under `"HUDScriptElements"`. Units differ between the routes: the store holds the
/// RATIO (0..1) while `sub_823C59E0` publishes `ratio * 100` into the registry.
static uint32_t hud_script_store(const uint8_t* base) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui)) {
        return 0u;
    }

    const uint32_t manager = read32(gui + 0x20u);
    if (!plausible(manager)) {
        return 0u;
    }

    const uint32_t store = read32(manager + 108u);
    return plausible(store) ? store : 0u;
}

/// Walks one of the GUI's typed element registries, calling `visit(nameHash, element)` for the
/// FIRST element registered under each name.
///
/// ONE WALK FOR EVERY REGISTRY, because they are the same structure with different element
/// types behind them — `sub_827F0F00` builds all thirteen from one base whose register/lookup
/// code is shared. Writing the traversal twice is how the meter reader and the text reader
/// would drift apart on the day one of them learned something.
///
/// `index` selects the registry, and the indices are the guest's own, proven by each
/// sub-registry's vtable slot [3] returning its `.bgf` attribute name:
/// **[1] MeterName, [4] PulserName, [6] TextName, [9] SlideToggleName.**
///
/// THE FIRST ELEMENT PER NAME STANDS FOR THE NAME. One hash genuinely drives several elements
/// and every setter writes them all the same value in one loop, so reporting one per name
/// reports what the guest stated; asking per element would be asking which of several identical
/// answers was preferred.
///
/// Fills parallel `out_hashes` / `out_elements` arrays and returns the number of names found,
/// which may exceed `capacity` — a caller that gets a bigger number should resize and ask again
/// rather than assume it saw everything.
///
/// Deliberately NOT a template taking a visitor: this lives inside the file's `extern "C"`
/// block, and a template cannot have C language linkage. Handing back the pairs and letting
/// each reader pull the field it wants costs one small array and keeps the traversal in one
/// place, which is the point.
static uint32_t walk_gui_registry(const uint8_t* base, uint32_t index, uint32_t* out_hashes,
                                  uint32_t* out_elements, uint32_t capacity) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui)) {
        return 0u;
    }

    const uint32_t manager = read32(gui + 0x20u);
    if (!plausible(manager)) {
        return 0u;
    }

    const uint32_t vector = read32(manager + 0x20u);
    if (!plausible(vector)) {
        return 0u;
    }

    const uint32_t begin = read32(vector + 28u);
    const uint32_t end = read32(vector + 32u);
    if (!plausible(begin) || end <= begin || (end - begin) > 0x400u) {
        return 0u;
    }

    // The same bound the guest's own writers check before indexing — they `__trap()` on a
    // vector too short for the slot they want, and a host must simply decline instead.
    if (((end - begin) / 4u) <= index) {
        return 0u;
    }

    const uint32_t registry = read32(begin + (index * 4u));
    if (!plausible(registry)) {
        return 0u;
    }

    // The map object is at `registry+4`, so its `_Myhead` is at `registry+8` — which is exactly
    // what `sub_822A9C60` compares a found node against to detect `end()`.
    const uint32_t head = read32(registry + 8u);
    if (!plausible(head)) {
        return 0u;
    }

    const uint32_t root = read32(head + 4u);  // head->_Parent
    if (!plausible(root)) {
        return 0u;
    }

    // AN EXPLICIT STACK, NOT RECURSION. This runs on Godot's thread against memory another
    // thread is mutating, so a torn pointer must cost a truncated answer rather than the
    // process. The depth bound is the same reasoning: a red-black tree of N nodes is at most
    // 2*log2(N+1) deep, so 64 is unreachable for any real registry and a tree that asks for
    // more has been corrupted under us.
    constexpr uint32_t kMaxDepth = 64u;
    constexpr uint32_t kMaxNames = 512u;

    uint32_t stack[kMaxDepth];
    uint32_t depth = 0;
    uint32_t found = 0;

    stack[depth++] = root;

    while (depth > 0 && found < kMaxNames) {
        const uint32_t node = stack[--depth];
        if (!plausible(node) || GuestByteAt(base, node + 21u) != 0u) {
            continue;  // node+21 is _Isnil: the sentinel, or a pointer that cannot be a node
        }

        const uint32_t key = read32(node + 12u);
        const uint32_t record = read32(node + 16u);

        if (plausible(record)) {
            const uint32_t elements = read32(record + 4u);
            const uint32_t elements_end = read32(record + 8u);
            if (plausible(elements) && elements_end > elements) {
                const uint32_t element = read32(elements);
                if (plausible(element)) {
                    if (out_hashes != nullptr && out_elements != nullptr && found < capacity) {
                        out_hashes[found] = key;
                        out_elements[found] = element;
                    }

                    ++found;
                }
            }
        }

        if (depth + 2u <= kMaxDepth) {
            stack[depth++] = read32(node + 0u);  // _Left
            stack[depth++] = read32(node + 8u);  // _Right
        }
    }

    return found;
}

/// The FNV-1 hashes of the layers the guest has explicitly DEACTIVATED — worldMgr+72.
///
/// THIS IS THE LIST A HOST NEEDS, and it is not the one the name "active layers" suggests.
/// `sub_823662C8` reads a different container per the layer's `load` attribute:
///
///   823664d0  bne cr6, loc_82366548   AlwaysOn    -> spawn, consulting NEITHER list
///   823664e8  bl  sub_82320A68        InitiallyOn -> spawn iff ABSENT from +72
///   82366520  bl  sub_8244C428        else        -> spawn iff PRESENT in +60
///
/// `sub_82320A68` walks `W+72` and returns PRESENT — `subf/cntlzw/extrwi` then `xori r3, r8, 1`
/// at 0x82320AE4 — and the caller spawns on zero (`beq cr6, loc_82366548`, 0x823664F4). The
/// else-branch test is inline at `loc_823664F8` against r23, which `World_LoadLevel_Execute`
/// supplies as `a1 + 60` (0x8231C788).
///
/// The two are pinned by their writers touching BOTH off one base, which is what makes the
/// assignment airtight rather than a guess:
///
///   Layers.ActivateLayer   sub_8231D8F0  insert +60, erase  +72
///   Layers.DeactivateLayer sub_8231DBE0  erase  +60, insert +72
///
/// and `IsLayerActive` (`sub_8231DEA8`) tests +60 for presence.
///
/// Both lists are EMPTY on a new game: `sub_8231BF20` default-constructs each, and the only bulk
/// writer is `World_LoadLevelFromSaveHeader` (0x8231C7E4/0x8231C7F0), which the new-game path
/// through `CmdLevelChange_Update` never reaches. That is why a host must gate on THIS list —
/// gating on the activated one would hide every `InitiallyOn` layer in a fresh world.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeactivatedLayers(uint32_t* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // Mirrors RefiiHostGetActiveLayers: MSVC `std::list`, _Myproxy then _Myhead at +4 and
    // _Mysize at +8. The only difference is the container offset — +60 activated, +72 here.
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t head = u32(wm + 72u + 4u);
    if (head < 0x10000u || head >= 0xC0000000u) {
        return 0;
    }

    const uint32_t size = u32(wm + 72u + 8u);
    if (size > 4096u) {
        return 0;  // not a list we recognise
    }

    uint32_t written = 0;
    uint32_t node = u32(head);
    for (uint32_t guard = 0; guard <= size + 8u; ++guard) {
        if (node == head || node < 0x10000u || node >= 0xC0000000u) {
            break;
        }

        if (out != nullptr && written < capacity) {
            out[written] = u32(node + 8u);
        }

        ++written;
        node = u32(node);
    }

    return written;
}

/// The element currently hosting a Bink MOVIE, and the path it is playing.
///
/// There is no "embedded video" flag: a sprite whose `Movie` string is non-empty draws a movie
/// into ITS OWN rect instead of a texture, and setting a texture clears it (`sub_8286DDC0`).
///
/// The string is at sprite +0x234, the same base whose +0x200 texture hash
/// `RefiiHostGetUiTextureHashes` reads (`sub_8286E360`, 0x8286E704). It is an LHString: two
/// pointer reads then the bytes, and a NULL first pointer is the empty case — per
/// `sub_8229B1B8`, which takes `_BYTE***`, branches on `*a1` and compares `**a1`.
///
/// Returns 1 when a slot is playing, with `out_ordinal` in the same traversal
/// `RefiiHostGetUiElements` uses. Only the FIRST is reported; shipped screens carry one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiMovie(uint32_t presentation, uint32_t* out_ordinal,
                                                   char* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (presentation == 0u) {
        const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
        if (!plausible(gui)) {
            return 0;
        }

        const uint32_t active = read32(gui + 188u);
        if (!plausible(active)) {
            return 0;
        }

        presentation = read32(active + 20u);
    }

    if (!plausible(presentation)) {
        return 0;
    }

    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    constexpr uint32_t kMaxElements = 16384u;
    uint32_t ordinal = 0;
    uint32_t element = root;

    while (element != 0 && ordinal < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        // Same two dereferences as the texture read: most elements have no render item.
        const uint32_t holder = read32(element + 4u);

        // THE TYPE BYTE IS NOT OPTIONAL — `node+10 == 2` is the guest's own guard before it
        // treats `node+0` as a sprite (`sub_829A5A18` at 0x829A5B64). Without it, +564 is read
        // off every kind of render item and returns whatever pointer-shaped bytes are there.
        if (plausible(holder) && (base[holder + 10u] == 2u)) {
            const uint32_t record = read32(holder);
            if (plausible(record)) {
                const uint32_t indirect = read32(record + 564u);
                if (plausible(indirect)) {
                    const uint32_t text = read32(indirect);
                    if (plausible(text) && base[text] != 0) {
                        if (out_ordinal != nullptr) {
                            *out_ordinal = ordinal;
                        }

                        if (out != nullptr && capacity > 0) {
                            uint32_t i = 0;
                            for (; i + 1u < capacity; ++i) {
                                const char c = (char)base[text + i];
                                if (c == 0) {
                                    break;
                                }
                                out[i] = c;
                            }
                            out[i] = 0;
                        }

                        return 1;
                    }
                }
            }
        }

        ++ordinal;

        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        // Bounded: a torn tree whose parent links form a cycle would otherwise hold the
        // caller here for ever.
        int climbed = 0;
        while (element != 0) {
            if (element == root || ++climbed > 16384) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0;
        }
    }

    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudMeters(uint32_t* out_hashes, float* out_values, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    constexpr uint32_t kMaxNames = 512u;
    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;

    const uint32_t found =
        walk_gui_registry(base, kGuiRegistryMeter, out_hashes, elements, want);

    const uint32_t stored = found < want ? found : want;
    for (uint32_t i = 0; i < stored; ++i) {
        if (out_values == nullptr) {
            break;
        }

        const uint32_t raw = GuestWordAt(base, elements[i] + 0x14u);
        std::memcpy(&out_values[i], &raw, sizeof(out_values[i]));
    }

    return found;
}

/// The guest's expression/job timing meter object, `M = *(*(GUI+32)+44)`, as the fields the
/// HUD needs to draw it. ABI 114. The guest RUNS the meter (its natives are observed, not
/// suppressed: `StartExpressionMeterPip` → `sub_823D2120`, the pip tick `sub_822ACBF8`, the
/// judge `sub_823D21C0` on the guest's own A press) and answers its own predicates from this
/// object; the host only reads it. Layout from the decompile: +0 on, +4 mode (1 pip hero A,
/// 2 pip hero B, 3 bar-job fill), +12/+13 pip armed A/B, +14 latched, +16 position 0..100,
/// +20 speed %/s, +36/+37 hit A/B, +38 the meter's active latch, +40 colour band (0 low,
/// 1 med, 2 high).
///
/// +38 is +0x26, the active latch. `sub_823D1F98` raises it at 0x823D1FE0, `sub_823D1458`
/// clears it at 0x823D147C, and
/// `sub_823D1A48` reads it at 0x823D1BBC to skip its own reset block.
///
/// values: [0] on, [1] mode, [2] armedA, [3] armedB, [4] latched, [5] position, [6] speed,
/// [7] hitA, [8] hitB, [9] live, [10] band. Returns the count written, 0 with no meter.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetExpressionMeter(float* out_values, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_values == nullptr || capacity < 11u) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    const uint32_t hud = plausible(gui) ? GuestWordAt(base, gui + 32u) : 0u;
    const uint32_t meter = plausible(hud) ? GuestWordAt(base, hud + 44u) : 0u;
    if (!plausible(meter)) {
        return 0u;
    }

    const auto byte_at = [base](uint32_t addr) -> float {
        const uint32_t word = GuestWordAt(base, addr & ~3u);
        return static_cast<float>((word >> (8u * (3u - (addr & 3u)))) & 0xFFu);
    };
    const auto float_at = [base](uint32_t addr) -> float {
        const uint32_t raw = GuestWordAt(base, addr);
        float value;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    out_values[0] = byte_at(meter + 0u);
    out_values[1] = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, meter + 4u)));
    out_values[2] = byte_at(meter + 12u);
    out_values[3] = byte_at(meter + 13u);
    out_values[4] = byte_at(meter + 14u);
    out_values[5] = float_at(meter + 16u);
    out_values[6] = float_at(meter + 20u);
    out_values[7] = byte_at(meter + 36u);
    out_values[8] = byte_at(meter + 37u);
    out_values[9] = byte_at(meter + 38u);
    out_values[10] = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, meter + 40u)));
    return 11u;
}

/// The TopBox manager's FRONT record — the box the HUD's `TopBox` group is showing. ABI 115.
///
/// `mgr = *(*(GUI+32)+88)`; `sub_8230D4F0`: channels are a vector at `mgr+8..+12` of Channel*,
/// each channel's records at `+4..+8` of Record*, and the front is the LAST non-null record of
/// the FIRST non-empty channel. Record (32 bytes, filled by `sub_823033D0` at 0x82303640–a4):
/// +0 FNV1(name), +4 title tag hash, +12 lifetime ms (−1 forever), +16 ShowA, +17 IsHoldA,
/// +24 ShowY, +25 IsHoldY. Records enter through Lua natives AND natively (the quest objective,
/// `sub_8251D818`), which is why the host reads the manager rather than its own copy of the calls.
///
/// words: [0] present, [1] title hash, [2] showA, [3] holdA, [4] showY, [5] holdY, [6] channel,
/// [7] name hash, [8] lifetime ms as int. Returns 9, or 0 with no manager.
/// The TopBox manager's FRONT record (`sub_8230D4F0`): the last record of the lowest-index
/// non-empty channel. Returns 0 with `channel` untouched when the manager is not up; sets
/// `manager_found` so a caller can tell "no manager" from "no record".
static uint32_t TopBoxFrontRecord(const uint8_t* base, uint32_t& channel, bool& manager_found) {
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    manager_found = false;
    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    const uint32_t hud = plausible(gui) ? GuestWordAt(base, gui + 32u) : 0u;
    const uint32_t manager = plausible(hud) ? GuestWordAt(base, hud + 88u) : 0u;
    if (!plausible(manager)) {
        return 0u;
    }
    manager_found = true;

    // HIDDEN BY A SCRIPT: `GUI.SetTopBoxHidden` (`sub_82304C30` → `sub_8230C7A8`) writes
    // `mgr+0`, and `mgr+1` hides it the same way — the guest ORs the two. Hidden, it fades the
    // `TopBox` group out and KEEPS the records, so a front record alone does not mean shown.
    if (GuestByteAt(base, manager) != 0u || GuestByteAt(base, manager + 1u) != 0u) {
        return 0u;
    }

    const uint32_t channels_begin = GuestWordAt(base, manager + 8u);
    const uint32_t channels_end = GuestWordAt(base, manager + 12u);
    if (!plausible(channels_begin) || channels_end < channels_begin ||
        (channels_end - channels_begin) > 16u * 4u) {
        return 0u;
    }

    channel = 0u;
    for (uint32_t at = channels_begin; at < channels_end; at += 4u, ++channel) {
        const uint32_t list = GuestWordAt(base, at);
        if (!plausible(list)) {
            continue;
        }

        const uint32_t begin = GuestWordAt(base, list + 4u);
        const uint32_t end = GuestWordAt(base, list + 8u);
        if (!plausible(begin) || end <= begin || ((end - begin) & ~3u) == 0u) {
            continue;
        }

        const uint32_t last = GuestWordAt(base, end - 4u);
        if (plausible(last)) {
            return last;
        }
    }

    return 0u;
}

/// THE FRONT RECORD'S BODY, as its provider states it — read, not called.
///
/// `sub_821787D0` composes the TopBox from the front record: title `TextManager_HasKey(rec+4)`,
/// body from `(**(rec+8)+4)(&out)`. The object at rec+8 is one of four clones `sub_8230B648`
/// makes by the provider's slot-0 type code, and every one of them is data the host can read:
///   0  `off_820A4268`, 8 B:  body = Babel(obj+4)
///   1  `off_820A4270`, 20 B: Babel(obj+4) with %1/%2/%3 <- decimal obj+8/+12/+16 (`sub_8230B298`)
///   2  `off_820A4278`, 16 B: Babel(obj+4) with %s,%1 <- Babel(obj+8), %2 <- Babel(obj+12)
///   3  `off_820A16F0`, 12 B: the literal LHString at obj+8 (`0x822EA510` copies the handle)
/// Reading registry 6 instead is what showed the previous box's body under a new title: a
/// record with no title and no body only sets ButtonOnly and never rewrites TopMain
/// (0x82178A60), so the registry string outlives the record it belonged to.
///
/// Words: [0] present, [1] class 0..3 (0xFF = unrecognised vtable, treat as empty),
/// [2] obj+4, [3] obj+8, [4] obj+12, [5] obj+16, [6] title key (rec+4), [7] record hash.
/// `out_text` receives the class-3 literal as UTF-8, empty otherwise.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTopBoxBody(uint32_t* out_words, uint32_t capacity,
                                                      char* out_text, uint32_t text_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_words == nullptr || capacity < 8u) {
        return 0u;
    }
    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    for (uint32_t i = 0; i < 8u; ++i) {
        out_words[i] = 0u;
    }
    if (out_text != nullptr && text_capacity != 0u) {
        out_text[0] = '\0';
    }

    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    uint32_t channel = 0u;
    bool manager_found = false;
    const uint32_t record = TopBoxFrontRecord(base, channel, manager_found);
    if (!manager_found) {
        return 0u;
    }
    if (record == 0u) {
        return 8u;
    }

    out_words[0] = 1u;
    out_words[6] = GuestWordAt(base, record + 4u);
    out_words[7] = GuestWordAt(base, record + 0u);
    const uint32_t provider = GuestWordAt(base, record + 8u);
    if (!plausible(provider)) {
        out_words[1] = 0xFFu;
        return 8u;
    }
    const uint32_t vtable = GuestWordAt(base, provider);
    uint32_t cls = 0xFFu;
    switch (refii::guest::ToTu1(vtable)) {
        case 0x820A4268u: cls = 0u; break;
        case 0x820A4270u: cls = 1u; break;
        case 0x820A4278u: cls = 2u; break;
        case 0x820A16F0u: cls = 3u; break;
        default: break;
    }
    out_words[1] = cls;
    if (cls == 0xFFu) {
        return 8u;
    }
    out_words[2] = GuestWordAt(base, provider + 4u);
    if (cls == 1u || cls == 2u) {
        out_words[3] = GuestWordAt(base, provider + 8u);
        out_words[4] = GuestWordAt(base, provider + 12u);
    }
    if (cls == 1u) {
        out_words[5] = GuestWordAt(base, provider + 16u);
    }
    if (cls == 3u && out_text != nullptr) {
        write_guest_wide_string(base, provider + 8u, out_text, text_capacity);
    }
    return 8u;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTopBoxFront(uint32_t* out_words, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_words == nullptr || capacity < 9u) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    for (uint32_t i = 0; i < 9u; ++i) {
        out_words[i] = 0u;
    }

    uint32_t channel = 0u;
    bool manager_found = false;
    const uint32_t record = TopBoxFrontRecord(base, channel, manager_found);
    if (!manager_found) {
        return 0u;
    }
    if (record == 0u) {
        return 9u;
    }

    const auto byte_at = [base](uint32_t addr) -> uint32_t {
        const uint32_t word = GuestWordAt(base, addr & ~3u);
        return (word >> (8u * (3u - (addr & 3u)))) & 0xFFu;
    };

    out_words[0] = 1u;
    out_words[1] = GuestWordAt(base, record + 4u);
    out_words[2] = byte_at(record + 16u);
    out_words[3] = byte_at(record + 17u);
    out_words[4] = byte_at(record + 24u);
    out_words[5] = byte_at(record + 25u);
    out_words[6] = channel;
    out_words[7] = GuestWordAt(base, record + 0u);
    out_words[8] = GuestWordAt(base, record + 12u);
    return 9u;
}

/// THE TARGETED-CITIZEN READOUT, as the guest's own per-frame record states it.
///
/// `sub_82176808` rebuilds the record at `*(*(0x83496B08 + 32) + 112)` — `*(GUI+32)` is the HUD,
/// and the block is a POINTER field (`lwz r3, 0x70(r11)` at 0x82178988, not `addi`) — then hands
/// **block + 8** to `sub_823FD410`, which publishes it by FNV-1 name hash. Every offset below is
/// from the BLOCK, so subtract 8 to match that function's own `a3` arithmetic:
///   +0  BYTE   prompt raised (`sub_823FD8E8` stores 1 at 0x823fd910; `sub_823FD9B0` stores 0)
///   +4  DWORD  the targeted entity; cleared at 0x82176fa4 when it fails the liveness gate
///   +8  DWORD  the entity as last published — `sub_823FD160` compares from here
///   +20 BYTE   gift on     -> reg 9  `TargetedGift`              0x823fd6d8
///   +24 float  gift value  -> reg 1  `TargetedGift`        x100  0x823fd698
///   +28 BYTE   button      -> reg 9  `TargetedQueryButton`       0x823fd71c
///   +32 DWORD  axis + 1    -> reg 10 `TargetedQuery`             0x823fd760
///   +36 BYTE   divorce     -> reg 9  `TargetedDivorce`           0x823fd884
///   +40 float  divorce val -> reg 1  `TargetedDivorce`     x100  0x823fd7ec
///   +44 BYTE   marry       -> reg 9  `TargetedMarry`             0x823fd8c4
///   +48 float  marry val   -> reg 1  `TargetedMarry`       x100  0x823fd834
///   +52 float  opinion — read by the CALLER and passed as `sub_823FD410`'s first argument,
///              which is why that function publishes it from `a1` and not from the record
///              (0x823fd7a4).
///
/// THE MARRY AND DIVORCE FIELDS ARE THE OTHER WAY ROUND from the obvious reading, and a working
/// note in this project had them swapped: +36/+40 publish under `dword_83497DAC`
/// (`TargetedDivorce`) and +44/+48 under `dword_83497DB0` (`TargetedMarry`). The predicates
/// agree — `sub_822684A8` asks the hero's family component whether this person is already in it,
/// `sub_8226B0D8` runs the marriage-eligibility test. Note also that both TOGGLES are always
/// published while only ONE of the two VALUES is (the if / else-if at 0x823fd7a8..0x823fd7f8).
///
/// The axis is component 131's first pending deed (`*(rec + 4)`, 0x82176cb8), published as
/// `axis + 1` (0x82176f10) and stated VERBATIM: `sub_828421F8` hands it straight to the slide
/// player `sub_82426BA8`, and registry 9's `sub_823FE848` reaches that SAME call through
/// `sub_82426CA8` with 1 or 2 — so the toggle's shift belongs to the toggle, not the channel.
/// Order from `sub_82428AB0` is Love, Fear, Attractiveness, Morality, Respect, giving 1 love,
/// 2 fear, 3 ugly — `LoveSlider`'s three slides.
///
/// Registry 10 is index 10 by the bound check `<= 0xA` and the slot `*(v35 + 40)` at 0x823fd748
/// / 0x823fd760, matching the table above the text reader. Registries 9 and 10 are write-through
/// — nothing stores the index on the element — so the host RE-DERIVES both from this record, the
/// same way it already does for 3 and 9.
///
/// Words: [0] prompt raised, [1] target entity, [2] published entity, [3] gift on,
/// [4] gift value bits, [5] button on, [6] slide-set index, [7] divorce on,
/// [8] divorce value bits, [9] marry on, [10] marry value bits, [11] opinion bits.
/// Returns 12, or 0 when the HUD or the record is not reachable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTargetedQuery(uint32_t* out_words, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_words == nullptr || capacity < 12u) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    for (uint32_t i = 0; i < 12u; ++i) {
        out_words[i] = 0u;
    }

    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    const uint32_t hud = plausible(gui) ? GuestWordAt(base, gui + 32u) : 0u;
    const uint32_t block = plausible(hud) ? GuestWordAt(base, hud + 112u) : 0u;
    if (!plausible(block)) {
        return 0u;
    }

    out_words[0] = GuestByteAt(base, block + 0u);
    out_words[1] = GuestWordAt(base, block + 4u);
    out_words[2] = GuestWordAt(base, block + 8u);
    out_words[3] = GuestByteAt(base, block + 20u);
    out_words[4] = GuestWordAt(base, block + 24u);
    out_words[5] = GuestByteAt(base, block + 28u);
    out_words[6] = GuestWordAt(base, block + 32u);
    out_words[7] = GuestByteAt(base, block + 36u);
    out_words[8] = GuestWordAt(base, block + 40u);
    out_words[9] = GuestByteAt(base, block + 44u);
    out_words[10] = GuestWordAt(base, block + 48u);
    out_words[11] = GuestWordAt(base, block + 52u);
    return 12u;
}

/// PLAYER TWO'S TARGETED READOUT on split screen. The guest keeps ONE readout, for the local hero
/// (`sub_82176808` on the block at `*(*(GUI+32)+112)`, its target set from hero one's targeting
/// only, `sub_8218FC60`), so player two's is built here from the SAME reads the guest's driver
/// makes, for hero two's own focus target (component 77, `GetFocusTarget` `sub_82843C40`:
/// `*(*(comp77 + 0xC8))`):
///   raised       — the target alive (+144 & 0x40) and a citizen (+36 bit 26 or +48 bit 1);
///   gift / axis  — component 131's first pending record (0x82176c4c..0x82176cd8): `axis = rec+4`,
///                  value `(rec+8 ± 1) × ±0.5`, behind the driver's one gate, the bytes +73/+75 of
///                  `*(*(*(game+88)+4)+104)` (0x82176c10) — read as the driver reads it; the button
///                  (`sub_825DD3C8`, a call) is not stated here;
///   opinion      — the opinion component's current axis (+44 + 4·axis), from the VILLAGER'S
///                  OPINION OF PLAYER TWO (core/coop_opinion.cpp) — their base attitudes until
///                  player two has done something there;
///   marry/divorce— the guest asks hero one's family and eligibility only; not stated.
/// The title is the guest's own composition, called for this target in the pump
/// (RefiiHostGetPlayerTwoHud). Words as RefiiHostGetTargetedQuery's; returns 12.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPlayerTwoTargetedQuery(uint32_t* out_words, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr || out_words == nullptr || capacity < 12u) {
        return 0u;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }
    for (uint32_t i = 0; i < 12u; ++i) {
        out_words[i] = 0u;
    }
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const auto word = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto f32 = [&](uint32_t at) {
        const uint32_t bits = word(at);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };
    const auto bits = [](float value) {
        uint32_t out = 0u;
        std::memcpy(&out, &value, sizeof(out));
        return out;
    };

    const uint32_t hero = PlayerEntityForKey(base, 2u);
    const uint32_t targeting = plausible(hero) ? EntityComponent(base, hero, 77u) : 0u;
    const uint32_t holder = plausible(targeting) ? word(targeting + 0xC8u) : 0u;
    const uint32_t target = plausible(holder) ? word(holder) : 0u;
    const bool alive = plausible(target) && (GuestByteAt(base, target + 144u) & 0x40u) != 0u;
    const bool citizen = alive && (((word(target + 36u) >> 26) & 1u) != 0u || ((word(target + 48u) >> 1) & 1u) != 0u);
    if (!citizen) {
        return 12u;
    }
    out_words[0] = 1u;
    out_words[1] = target;
    out_words[2] = target;

    // Gift and axis, behind the driver's gate (0x82176c10..0x82176c28).
    uint32_t axis = 0u;
    const uint32_t game = word(word(refii::guest::Address(0x83496AB8u)) + 12u);
    const uint32_t players = plausible(game) ? word(word(game + 88u) + 4u) : 0u;
    const uint32_t gate_object = plausible(players) ? word(players + 104u) : 0u;
    const bool gate = plausible(gate_object) && GuestByteAt(base, gate_object + 75u) == 0u &&
                      GuestByteAt(base, gate_object + 73u) != 0u;
    const uint32_t wants = EntityComponent(base, target, 131u);
    if (gate && plausible(wants) && GuestByteAt(base, wants + 32u) != 0u && word(wants + 20u) != 0u) {
        const uint32_t list = word(wants + 16u);
        const uint32_t node = plausible(list) ? word(list) : 0u;
        const uint32_t record = plausible(node) && node != list ? word(node + 8u) : 0u;
        if (plausible(record)) {
            axis = word(record + 4u);
            const float raw = f32(record + 8u);
            out_words[3] = 1u;
            out_words[4] = bits(axis == 1u ? (raw - 1.0f) * -0.5f : (raw + 1.0f) * 0.5f);
        }
    }
    out_words[6] = axis + 1u;

    // Opinion: the villager's opinion of player two.
    const uint32_t opinion = EntityComponent(base, target, 47u);
    if (plausible(opinion) && axis < 5u) {
        uint8_t two[REFII_OPINION_BLOCK_BYTES];
        float value = 0.0f;
        if (refii_bridge_opinion_two_load(opinion, two) != 0) {
            uint32_t raw = 0u;
            std::memcpy(&raw, two + 20u + (4u * axis), sizeof(raw));
            raw = __builtin_bswap32(raw);
            std::memcpy(&value, &raw, sizeof(value));
        } else {
            value = f32(opinion + 24u + (4u * axis));
        }
        out_words[11] = bits(axis == 1u ? (value - 1.0f) * -0.5f : (value + 1.0f) * 0.5f);
    }
    return 12u;
}

/// An entity's network id, the 64-bit UID the guest keys its entity map on (`entity+20` high,
/// `+24` low; `sub_82264488`). 0 for none.
REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetEntityUid(uint32_t entity) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || entity < 0x10000u || entity >= 0xC0000000u) {
        return 0u;
    }
    return (static_cast<uint64_t>(GuestWordAt(base, entity + 20u)) << 32) | GuestWordAt(base, entity + 24u);
}

/// A villager's opinion of player `player` (1 or 2) by its network id: the five axes (love, fear,
/// attractiveness, morality, respect) into `out`. Player one's are the opinion component's current
/// axes (+44); player two's the set kept for them (current at block+20), or the villager's base
/// attitudes (+24) before player two has done anything there. The entity is found the guest's way,
/// in the UID map at players+136 (`sub_82264488`: {left +0, parent +4, right +8, key high +12, low
/// +16, value +20, nil byte +25}). Returns 1 when found.
REFII_HOST_ABI_EXPORT int RefiiHostGetOpinionOfUid(uint64_t uid, int player, float* out) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || out == nullptr || uid == 0u || (player != 1 && player != 2)) {
        return 0;
    }
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const auto word = [base](uint32_t at) { return GuestWordAt(base, at); };
    const uint32_t root = word(refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? word(root + 12u) : 0u;
    const uint32_t manager = plausible(game) ? word(game + 88u) : 0u;
    const uint32_t cell = plausible(manager) ? word(manager + 4u) : 0u;
    const uint32_t players = plausible(cell) ? word(cell) : 0u;
    const uint32_t head = plausible(players) ? word(players + 140u) : 0u;
    if (!plausible(head)) {
        return 0;
    }
    const auto key = [&](uint32_t node) { return (static_cast<uint64_t>(word(node + 12u)) << 32) | word(node + 16u); };
    uint32_t found = head;
    uint32_t node = word(head + 4u);
    for (uint32_t steps = 0; steps < 96u && plausible(node) && GuestByteAt(base, node + 25u) == 0u; ++steps) {
        if (key(node) >= uid) {
            found = node;
            node = word(node);
        } else {
            node = word(node + 8u);
        }
    }
    if (found == head || key(found) != uid) {
        return 0;
    }
    const uint32_t entity = word(found + 20u);
    const uint32_t opinion = plausible(entity) ? EntityComponent(base, entity, 47u) : 0u;
    if (!plausible(opinion)) {
        return 0;
    }
    uint8_t two[REFII_OPINION_BLOCK_BYTES];
    const bool kept = player == 2 && refii_bridge_opinion_two_load(opinion, two) != 0;
    for (uint32_t axis = 0; axis < 5u; ++axis) {
        uint32_t raw = 0u;
        if (kept) {
            std::memcpy(&raw, two + 20u + (4u * axis), sizeof(raw));
            raw = __builtin_bswap32(raw);
        } else {
            raw = word(opinion + (player == 1 ? 44u : 24u) + (4u * axis));
        }
        std::memcpy(&out[axis], &raw, sizeof(raw));
    }
    return 1;
}

/// A PLAYER'S PROPERTIES: the records of their hero's CECPlayerProperties (comp69: *(props+28) the
/// list's sentinel, the record at node+8, "PlayerBuildingData": +0/+4 the building's UID, +8 its
/// BuildingName tag, +12 marital home, +13 rented out, +14 dungeon, +15 home, +18 tenants,
/// +19 workers, +28 income per drop, +44 value, +48 bought for), with each building's condition and
/// a full repair's bill (core/property_condition.h). `player` 1 or 2, or 0 for the hero whose menus
/// are open (ResolvePlayerHero). One record per line: `uid(hex) \t name tag \t flags \t income
/// \t value \t condition \t repair bill \t bought for`, flags bit 0 marital, 1 rented, 2 dungeon, 3 home,
/// 4 tenants, 5 workers, 6 shared with the holder's partner (core/hero_partners.cpp). Returns the
/// byte count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetProperties(int32_t player, char* out, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr || out == nullptr || capacity == 0u) {
        return 0u;
    }
    const auto heap = [](uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; };
    const uint32_t hero = player == 1 || player == 2 ? PlayerEntityForKey(base, static_cast<uint32_t>(player))
                                                     : ResolvePlayerHero(base);
    const uint32_t props = heap(hero) ? EntityComponent(base, hero, 69u) : 0u;
    if (!heap(props)) {
        return 0u;
    }
    const int32_t holder = hero == PlayerEntityForKey(base, 2u) ? 2 : 1;
    std::string text;
    const uint32_t head = GuestWordAt(base, props + 28u);
    uint32_t node = heap(head) ? GuestWordAt(base, head) : 0u;
    for (uint32_t n = 0; heap(node) && node != head && n < 256u; ++n, node = GuestWordAt(base, node)) {
        const uint32_t record = GuestWordAt(base, node + 8u);
        if (!heap(record)) {
            continue;
        }
        const uint64_t uid = (static_cast<uint64_t>(GuestWordAt(base, record)) << 32) | GuestWordAt(base, record + 4u);
        const uint32_t rep = GuestWordAt(base, record + 8u);
        const uint32_t chars = heap(rep) ? GuestWordAt(base, rep) : 0u;
        std::string name;
        for (uint32_t i = 0; heap(chars) && i < 128u; ++i) {
            const char c = static_cast<char>(GuestByteAt(base, chars + i));
            if (c == '\0') {
                break;
            }
            name.push_back(c == '\t' || c == '\n' ? ' ' : c);
        }
        uint32_t flags = 0u;
        flags |= GuestByteAt(base, record + 12u) != 0u ? 1u : 0u;
        flags |= GuestByteAt(base, record + 13u) != 0u ? 2u : 0u;
        flags |= GuestByteAt(base, record + 14u) != 0u ? 4u : 0u;
        flags |= GuestByteAt(base, record + 15u) != 0u ? 8u : 0u;
        flags |= GuestByteAt(base, record + 18u) != 0u ? 16u : 0u;
        flags |= GuestByteAt(base, record + 19u) != 0u ? 32u : 0u;
        const uint64_t co_owner = refii_bridge_coowner(uid);
        flags |= co_owner != 0u && refii_bridge_partner_kind(holder, co_owner) > 0 ? 64u : 0u;
        const int32_t income = static_cast<int32_t>(GuestWordAt(base, record + 28u));
        const int32_t value = static_cast<int32_t>(GuestWordAt(base, record + 44u));
        float condition = 1.0f;
        const std::string key = refii::property::ConditionKey(uid);
        refii_bridge_player_get(1, key.c_str(), key.size(), &condition);
        condition = std::clamp(condition, 0.0f, 1.0f);
        const int32_t bought = static_cast<int32_t>(GuestWordAt(base, record + 48u));
        char line[512];
        std::snprintf(line, sizeof(line), "%016llX\t%s\t%u\t%d\t%d\t%.4f\t%d\t%d\n",
                      static_cast<unsigned long long>(uid), name.c_str(), flags, income, value, condition,
                      refii::property::RepairCost(value, condition), bought);
        text += line;
    }
    if (text.size() + 1u > capacity) {
        return 0u;
    }
    std::memcpy(out, text.c_str(), text.size() + 1u);
    return static_cast<uint32_t>(text.size());
}

/// The oldest property notice (core/payload_bridge.h): 1 with its kind (0 fell into disrepair,
/// 1 repaired, 2 not enough gold, 3 nothing to repair, 6 a purchase shared with the partner: the
/// building's UID; 4 owed gold paid on return, 5 a sale's half paid to the partner: the gold in
/// `uid`), 0 when none.
REFII_HOST_ABI_EXPORT int32_t RefiiHostTakePropertyNotice(int32_t* kind, uint64_t* uid) {
    return refii_bridge_property_notice_take(kind, uid);
}

/// THE PARTNERSHIP between the two heroes in the game, Fable 3's (core/hero_partners.cpp): 1 with
/// its kind (0 none, 1 business partners, 2 married) when both heroes are here with their
/// identities known, 0 otherwise.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetPartnership(int32_t* kind) {
    if (kind != nullptr) *kind = 0;
    if (refii_bridge_hero_id(1) == 0u || refii_bridge_hero_id(2) == 0u) {
        return 0;
    }
    if (kind != nullptr) *kind = refii_bridge_partnership();
    return 1;
}

/// The oldest household action for the co-op peer (mod_events.cpp): a joiner's estate action,
/// gift, furniture level or bed for the host to run for its hero two, or the host's kept gift for
/// the joiner. `<action>:<uid hex>:<value>`. Returns its length, 0 when none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeEstateIntent(char* out, uint32_t capacity) {
    return refii_bridge_estate_intent_take(out, capacity);
}

/// How many of the item definition `definition` (its GDB record hash, the game's FNV-1 of the
/// record's name) player `player`'s hero carries: the inventory component (13) list from its +16
/// sentinel, each node's item at +8 with the definition at +4 and the count at +12. -1 without the
/// hero.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetItemCount(int32_t player, uint32_t definition) {
    auto* memory = SafeKernelMemory();
    const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };
    const uint32_t hero = base != nullptr && (player == 1 || player == 2)
                              ? PlayerEntityForKey(base, static_cast<uint32_t>(player))
                              : 0u;
    const uint32_t inventory = plausible(hero) ? EntityComponent(base, hero, 13u) : 0u;
    if (!plausible(inventory)) {
        return -1;
    }
    const uint32_t sentinel = GuestWordAt(base, inventory + 16u);
    int32_t count = 0;
    uint32_t node = plausible(sentinel) ? GuestWordAt(base, sentinel) : 0u;
    for (uint32_t walked = 0; plausible(node) && node != sentinel && walked < 4096u; ++walked) {
        const uint32_t item = GuestWordAt(base, node + 8u);
        if (plausible(item) && GuestWordAt(base, item + 4u) == definition) {
            count += static_cast<int32_t>(GuestWordAt(base, item + 12u));
        }
        node = GuestWordAt(base, node);
    }
    return count;
}

/// A player's store value (core/player_state.h): 1 with it in `out`, 0 when the key is unset.
/// Writes a SAVED player value (core/player_state.h) for `player` (1 or 2), as a mod's would: kept
/// with that player's save, the same on both machines. Returns 1 when written.
REFII_HOST_ABI_EXPORT int32_t RefiiHostSetPlayerValue(int32_t player, const char* key, float value) {
    if (key == nullptr || (player != 1 && player != 2)) {
        return 0;
    }
    refii_bridge_player_write(player, key, std::strlen(key), value, 0, 1);
    return 1;
}

/// The world map's property row: names the UID the pump samples screen 28's numbers for (0 for
/// none) — see SamplePropertyEstate.
REFII_HOST_ABI_EXPORT void RefiiHostRequestPropertyEstate(uint64_t uid) {
    refii::vfs::RequestPropertyEstate(uid);
}

/// The answer for `uid`, in RefiiHostGetRealEstate's fifteen words. Returns 1 when the building
/// was sampled (its region is loaded), 0 when it was not or the answer is another row's.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPropertyEstate(uint64_t uid, uint32_t* out_words, uint32_t word_capacity,
                                                         char* occupant, uint32_t occupant_capacity) {
    uint64_t answered = 0u;
    const refii::vfs::RealEstate estate = refii::vfs::PropertyEstateSnapshot(&answered);
    if (answered != uid || !estate.known) {
        return 0u;
    }
    WriteOut(estate.occupant, occupant, occupant_capacity);
    const uint32_t words[] = {
        estate.record,
        estate.type_mask,
        estate.owned ? 1u : 0u,
        estate.has_tenant ? 1u : 0u,
        static_cast<uint32_t>(estate.value),
        static_cast<uint32_t>(estate.economy),
        static_cast<uint32_t>(estate.decoration),
        static_cast<uint32_t>(estate.income),
        static_cast<uint32_t>(estate.sale_price),
        static_cast<uint32_t>(estate.selling_fees),
        static_cast<uint32_t>(estate.not_vacant),
        static_cast<uint32_t>(estate.owner_killed),
        static_cast<uint32_t>(estate.hero_opinion),
        estate.bonuses_record,
        estate.title_record,
    };
    if (out_words != nullptr) {
        const uint32_t count = std::min<uint32_t>(word_capacity, sizeof(words) / sizeof(words[0]));
        for (uint32_t i = 0; i < count; ++i) {
            out_words[i] = words[i];
        }
    }
    return 1u;
}

REFII_HOST_ABI_EXPORT int32_t RefiiHostGetPlayerValue(int32_t player, const char* key, float* out) {
    return key != nullptr ? refii_bridge_player_get(player, key, std::strlen(key), out) : 0;
}

/// Sets the partnership in both heroes' saved values (0 ends it, 1 business, 2 married). Returns
/// 1 when both identities are known.
REFII_HOST_ABI_EXPORT int32_t RefiiHostSetPartnership(int32_t kind) { return refii_bridge_partnership_set(kind); }

/// PLAYER TWO'S D-PAD AND TARGETED TITLE on split screen, from the guest's own builders called
/// for hero two each tick (guest_tick_pump.cpp, SamplePlayerTwoHud). `out` takes the title, then
/// the four d-pad icon paths (up, left, down, right), newline-separated; `out_target` the entity
/// the title is for (0 when none). Returns 1 when stated, 0 off split screen.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPlayerTwoHud(uint32_t* out_target, char* out, uint32_t capacity) {
    return refii_bridge_player_two_hud(out_target, out, capacity);
}

/// Every live PULSER the in-game HUD is holding — registry 4, `PulserName` — as its PHASE.
///

/// A pulser LOOPS the authored timeline: `sub_822C21E0` advances `rec+20` by `rec+24 · dt`
/// (mode `rec+12`: 0 loop with wrap, 1 one-shot then 2 stopped) and applies
/// `time = phase × 1000` to the element. The guest advances it; the host reads the phase at
/// +0x14 (same slot as the meter's current value) and scrubs its clone at the same tick.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulsers(uint32_t* out_hashes, float* out_phases, uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    constexpr uint32_t kMaxNames = 512u;
    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;

    const uint32_t found =
        walk_gui_registry(base, kGuiRegistryPulser, out_hashes, elements, want);

    const uint32_t stored = found < want ? found : want;
    for (uint32_t i = 0; i < stored; ++i) {
        if (out_phases == nullptr) {
            break;
        }

        const uint32_t raw = GuestWordAt(base, elements[i] + 0x14u);
        std::memcpy(&out_phases[i], &raw, sizeof(out_phases[i]));
    }

    return found;
}

/// WHY THERE IS NO SLIDE OR IMAGE READER HERE, though the registries exist.
///
/// I wrote both and removed them: neither can work, and both returned a plausible number.
///
/// The SLIDE writers are WRITE-THROUGH. `sub_823FE848` (toggle) and `sub_828421F8` (fader and
/// set) look the name up, walk the record's element vector and call `sub_82426CA8` /
/// `sub_82426BA8` — storing nothing on the record. And `sub_82426BA8` does not store the index
/// on the element either; it is
///
///     controller = presentation->vt[0x24]();
///     controller->vt[0x08](controller, element, index, 1);
///
/// a virtual dispatch into the slide controller, whose concrete class is built through the GUI
/// manager array and never named in the image. `AKElement_getCurrentSlide` reads it back the
/// same way, through `vt[0x20]`. There is no address a host can read.
///
/// The IMAGE hash is real but is NOT on the registry record. `sub_8286DDC0` stores it at
/// `+512` (with `+516` normal, `+520` parallax and `+548` dirty) on the LIVE NODE — the object
/// that also carries the `Movie` string at `+564` — whereas this walker hands back the BINDING
/// record, whose own fields are the string at `+16` and the pulser phase at `+20`. Reading
/// `+512` off the binding record reads 512 bytes past a small struct.
///
/// Neither is needed. Slides and textures already cross PER ELEMENT through the ordinary
/// element walk that feeds every owned book (`Slides`, `Textures`), which is the same statement
/// arriving by a route that is actually addressable.

/// Every live TEXT the in-game HUD is holding, keyed by the name each is published under.
///
/// THE COMPANION TO THE METERS, and needed because the two halves of the HUD arrive by
/// different routes even though they share a registry structure. `hud.bgf` binds 18 text
/// elements — `TopTitle`, `TopMain`, `Subtitles`, `Counter1..4`, the four experience readouts
/// — and `sub_823FEB28(registry, hash, string)` writes each one:
///
/// <code>
/// v7 = *(_DWORD *)v6;                                   // the text element
/// sub_822B88A8((int *)(*(_DWORD *)v6 + 16), a3);        // element+0x10 = the LHString
/// *(_BYTE *)(v7 + 12) = 1;                              // element+0x0C = dirty
/// </code>
///
/// so the value is at **`element + 0x10`** and it is a REFCOUNTED LHString handle, read here
/// the same way the save-card descriptions are: `+0` UTF-16 data, `+4` length in code units,
/// characters BIG-endian.
///
/// IT IS THE RESOLVED STRING, NOT A TAG. Both writers localise first — `SetGUIText`
/// (`sub_823C5B00`) goes through `TextManager_GetString` before calling, and the mini-game
/// screen passes text it has already put through `sub_8230E0E8`. So a host must NOT put this
/// through Babel again; doing so would look up a sentence.
///
/// Strings are written to `text` as NUL-separated UTF-8, one per hash, in the same order as
/// `out_hashes`. The two buffers are sized SEPARATELY — a hash is four bytes and a string is a
/// sentence, so one capacity for both would either waste the array or truncate the text.
/// Returns the number of names found. Pass nulls to size the buffers.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudTexts(uint32_t* out_hashes, uint32_t capacity, char* text,
                              uint32_t text_capacity) {
    if (text != nullptr && text_capacity > 0) {
        text[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    constexpr uint32_t kMaxNames = 512u;
    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;

    const uint32_t found =
        walk_gui_registry(base, kGuiRegistryText, out_hashes, elements, want);

    const uint32_t stored = found < want ? found : want;
    uint32_t written = 0;
    for (uint32_t i = 0; i < stored && text != nullptr; ++i) {
        written += write_guest_wide_string(base, elements[i] + 0x10u, text + written,
                                           text_capacity > written ? text_capacity - written : 0u);
    }

    return found;
}

/// The WILL ASSIGNMENT state, for a Godot-owned spell widget.
///
/// `out` receives 14 words:
///   [0]  the CECSpellManager, 0 when the hero has none yet
///   [1..5]   the will on d-pad slot 0..4, or 0xFFFFFFFF for an empty slot
///   [6..10]  per slot, a bitmask of the wills that slot may be given
///   [11] the abilities screen's assignment-mode byte, or 0xFFFFFFFF when that screen is down
///   [12] player 1's quick-wheel assigner flag
///   [13] player 2's
/// Returns 1 when the manager resolved, 0 otherwise. The tail is zeroed either way.
///
/// The offering matrix is the only validation — `sub_82541F30` stores whatever byte it is
/// handed. `sub_826B4848` builds each row's list from two parallel byte planes, admitting a
/// will only when BOTH are set:
///
///     0x826b4ae4  v27 = mgr + 128 + row + 68;          // mgr + 196 + row
///     0x826b4af4  if ( *(v27 - 40) && *v27 )           // mgr + 156 + 5*s + row
///     0x826b4b14      spells[row][count++] = s;
///
/// with the loop bounds `while (v23 < 40)` stepping 8 (five rows) and `while (v26 - 2 < 8)`
/// (eight wills). The same `5*spell + row + 156/196` expression appears in the standalone
/// accessors `sub_825410C8` / `sub_82540EE8` / `sub_82541118`, from the same base.
///
/// The hero is NOT the player entity: the manager hangs off a sub-object two dereferences past
/// it. Chain from `sub_823B96E0` (0x823B970C..0x823B9730): the global at 0x83496AB8, `+12` the
/// game, `+88` then `+4`, one more load, `+108`, one more load. Read, never called.
REFII_HOST_ABI_EXPORT int RefiiHostGetSpellAssign(uint32_t* out) {
    constexpr uint32_t kWords = 14u;
    constexpr uint32_t kSlots = 5u;
    constexpr uint32_t kSpells = 8u;
    constexpr uint32_t kNone = 0xFFFFFFFFu;

    if (out != nullptr) {
        for (uint32_t i = 0; i < kWords; ++i) {
            out[i] = (i >= 1u && i <= kSlots) || i == 11u ? kNone : 0u;
        }
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr || out == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    // The quick-wheel assigner flags and the abilities screen's mode, read even when the
    // manager below does not resolve, so "open but no hero" is distinguishable from closed.
    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (plausible(gui)) {
        const uint32_t owner = read32(gui + 28u);
        if (plausible(owner)) {
            out[12] = read8(owner + refii::guest::GuiOwnerField(36u));
            out[13] = read8(owner + refii::guest::GuiOwnerField(37u));
        }
    }

    out[11] = static_cast<uint32_t>(RefiiHostGetAbilitiesMode());

    const uint32_t hero = ResolvePlayerHero(base);
    // The callee's own two guards, so a half-built hero reports "no manager".
    if (!plausible(hero) || (read8(hero + 144u) & 0x40u) == 0u ||
        (read8(hero + 40u) & 0x01u) == 0u) {
        return 0;
    }

    // Component 56, by the guest's own two routes: the cached slot at `hero+140` first, then
    // the binary search over the sorted {id, pointer} pairs at `hero+72 .. hero+76`.
    constexpr uint32_t kSpellManagerComponent = 56u;
    uint32_t manager = 0u;

    const uint32_t cache = read32(hero + 140u);
    const uint32_t table = read32(hero + 72u);
    if (plausible(cache) && plausible(table)) {
        manager = read32(table + (static_cast<uint32_t>(read8(cache + 56u)) << 3) + 4u);
    } else if (plausible(table)) {
        const uint32_t end = read32(hero + 76u);
        if (end > table && (end - table) <= 0x2000u) {
            for (uint32_t at = table; at < end; at += 8u) {
                if (read32(at) == kSpellManagerComponent) {
                    manager = read32(at + 4u);
                    break;
                }
            }
        }
    }

    if (!plausible(manager)) {
        return 0;
    }

    out[0] = manager;

    for (uint32_t row = 0; row < kSlots; ++row) {
        const uint8_t assigned = read8(manager + 128u + row);

        // 0xFF is the guest's empty slot; anything past the will table has no icon.
        out[1u + row] = assigned < kSpells ? assigned : kNone;

        uint32_t offered = 0u;
        for (uint32_t spell = 0; spell < kSpells; ++spell) {
            const uint32_t index = (5u * spell) + row;
            if (read8(manager + 156u + index) != 0u && read8(manager + 196u + index) != 0u) {
                offered |= 1u << spell;
            }
        }

        out[6u + row] = offered;
    }

    return 1;
}

/// THE QUICK SPELL WHEEL'S CURSOR AND LISTS, as the guest's own widget holds them.
///
/// The wheel over the running game is the `SpellAssigning` controller at `*(*(GUI+32)+92)`
/// (player 1) / `+96` (player 2) — the same 420-byte class the abilities blade embeds at
/// `screen14+200` (`sub_826B4358`, binder `sub_826B5860`). The GUEST moves its cursor:
/// `sub_826B5430` case 28 `if (row < *(this+412)) ++row` (0x826b54a8), 29 `--row`
/// (0x826b5538), 30/31 move the row's cell and COMMIT through `sub_82541F30`; the pump's
/// `SpellBlade:up|down|left|right` lever hands it the held direction every frame. So the row and cell are
/// data points to read, not a cursor to keep — the host kept one of its own and the gold box
/// never left row 0.
///
/// Layout from the widget (decompiled 2026-08-21, re-read live 2026-10-03): `+172` the
/// CECSpellManager, `+176` row, `+180+4r` cells populated in row r, `+200+4r` the row's current
/// cell, `+220+32r+4c` the spell index (0..7) at row r cell c — the row's offered wills,
/// compacted in index order by `sub_826B4848` — and `+412` the highest populated row (−1 =
/// nothing to assign). Entries past a row's count are stale heap and are not read.
///
/// Writes `out[0]` widget, `[1]` row, `[2]` max row (as written, signed), `[3+r]` count,
/// `[8+r]` cell, `[13 + 8r + c]` list — 53 words. Returns 1 when the widget resolved and the
/// wheel's open flag (`QM+36`/`+37`) is set, else 0 with the tail zeroed: a closed wheel's
/// tables are uninitialised heap (`sub_826B4848` runs only on open).
namespace {

/// The spell-assignment widget's cursor and lists, as the guest keeps them: row +176, max row
/// +412, per-row count +180+4r, per-row cell +200+4r, per-row list +220+32r+4c. The same
/// 420-byte controller serves the quick wheel and the abilities blade (`sub_826B4358`).
/// 53 words out: widget, row, max row, 5 counts, 5 cells, 5x8 lists. 0 when the widget is not
/// plausible.
int ReadSpellWidget(const uint8_t* base, uint32_t widget, uint32_t* out) {
    constexpr uint32_t kRows = 5u;
    constexpr uint32_t kCells = 8u;
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    if (!plausible(widget)) {
        return 0;
    }

    const uint32_t row = read32(widget + 176u);
    const int32_t max_row = static_cast<int32_t>(read32(widget + 412u));
    if (row >= kRows || max_row < -1 || max_row >= static_cast<int32_t>(kRows)) {
        return 0;
    }

    out[0] = widget;
    out[1] = row;
    out[2] = static_cast<uint32_t>(max_row);
    for (uint32_t r = 0; r < kRows; ++r) {
        const uint32_t count = read32(widget + 180u + (4u * r));
        const uint32_t cell = read32(widget + 200u + (4u * r));
        out[3u + r] = count <= kCells ? count : 0u;
        out[8u + r] = cell < kCells ? cell : 0u;
        for (uint32_t c = 0; c < out[3u + r]; ++c) {
            out[13u + (kCells * r) + c] = read32(widget + 220u + (32u * r) + (4u * c));
        }
    }

    return 1;
}

}  // namespace

REFII_HOST_ABI_EXPORT int RefiiHostGetSpellWheel(uint32_t player, uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kWords = 53u;
    if (out == nullptr || capacity < kWords) {
        return 0;
    }

    for (uint32_t i = 0; i < kWords; ++i) {
        out[i] = 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const bool two = player == 2u;
    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    const uint32_t quick = plausible(gui) ? read32(gui + 28u) : 0u;
    const uint32_t hud = plausible(gui) ? read32(gui + 32u) : 0u;
    if (!plausible(quick) || !plausible(hud) || read8(quick + refii::guest::GuiOwnerField(two ? 37u : 36u)) == 0u) {
        return 0;
    }

    return ReadSpellWidget(base, read32(hud + (two ? 96u : 92u)), out);
}

/// The abilities book's spell blade: screen 14's embedded widget at `screen + 200`
/// (`sub_826B4358` from the screen-14 ctor `sub_826C1E80`), read only while the abilities screen
/// is the active one and its assignment mode byte (`screen + 620`, the blade's open state) is set.
/// The screen object comes off the owner's screen map, keyed by TU1 screen id, as the co-op
/// mannequin's does. Same 53 words as RefiiHostGetSpellWheel.
REFII_HOST_ABI_EXPORT int RefiiHostGetSpellBlade(uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kWords = 53u;
    constexpr uint32_t kAbilitiesScreen = 14u;
    if (out == nullptr || capacity < kWords) {
        return 0;
    }

    for (uint32_t i = 0; i < kWords; ++i) {
        out[i] = 0u;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto read8 = [base](uint32_t at) -> uint8_t { return GuestByteAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) {
        return 0;
    }

    const uint32_t manager = read32(gui + 56u);
    const uint32_t owner = read32(gui + 28u);
    if (!plausible(manager) || !plausible(owner) ||
        refii::guest::ScreenIdToTu1(read32(manager + 4u)) != kAbilitiesScreen) {
        return 0;
    }

    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) {
        return 0;
    }

    uint32_t node = read32(end_node + 4u);
    uint32_t screen = 0;
    for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
        const uint32_t key = refii::guest::ScreenIdToTu1(read32(node + 12u));
        if (key == kAbilitiesScreen) {
            screen = read32(node + 16u);
            break;
        }
        node = read32(node + (kAbilitiesScreen < key ? 0u : 8u));
    }

    if (!plausible(screen) || read8(screen + 620u) == 0u) {
        return 0;
    }

    return ReadSpellWidget(base, screen + 200u, out);
}

/// Every HUD value the shipped LUA has set, keyed by `FNV-1(name)` — the other fifty meters.
///
/// See `hud_script_store`. Values here are the RATIO, 0..1, as `SetMeterRatioOverTime` stated
/// it; merging with `RefiiHostGetHudMeters` needs a scale by 100.
///
/// Returns the number of names in the store, which may exceed `capacity`. Pass nulls to size
/// the buffers.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudScriptMeters(uint32_t* out_hashes, float* out_ratios,
                                                           uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t store = hud_script_store(base);
    if (store == 0u) {
        return 0u;
    }

    constexpr uint32_t kMaxNames = 512u;
    uint32_t raw[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;

    const uint32_t found = walk_guest_u32_map(base, store + 16u, out_hashes, raw, want);

    const uint32_t stored = found < want ? found : want;
    for (uint32_t i = 0; i < stored && out_ratios != nullptr; ++i) {
        std::memcpy(&out_ratios[i], &raw[i], sizeof(out_ratios[i]));
    }

    return found;
}

/// Every HUD text the shipped Lua has set, as `FNV-1(name)` to the BABEL TEXT ID it was given.
///
/// A tag id, not a string — the difference from `RefiiHostGetHudTexts`. `SetGUIText` caches the
/// id it was handed (`*(_DWORD *)sub_8236A9B0(store + 28, &hash) = a3`, 0x823c5b94) before
/// putting it through `TextManager_GetString`, so the host resolves it through its own
/// `book.babel`, keyed by this hash. Read here, the value survives a presentation reload.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudScriptTexts(uint32_t* out_hashes, uint32_t* out_text_ids,
                                                          uint32_t capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0u;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t store = hud_script_store(base);
    if (store == 0u) {
        return 0u;
    }

    return walk_guest_u32_map(base, store + 28u, out_hashes, out_text_ids, capacity);
}

/// Every Gameface presentation the guest currently has live — HUD, menus and front end.
///
/// ONE REGISTRY, NO SPECIAL CASES. The three owners keep their `CPresentation*` in three
/// different places (`wrapper+20` for the front end, `GUI+32 -> +4` for the HUD,
/// `mgr+12+4*id` for the menus), but every one of them is also appended to a single array on
/// the AKPlayer at `GUI+16`. Enumerating that array means the host never has to know which
/// kind of screen it is looking at — and `GUI+16` is a BASE-class field, zeroed in
/// `sub_823710A8` and populated by base-class code, so unlike `GUI+188` it survives the
/// object swap when the game takes the GUI over.
///
/// The array is DENSE: removal (`sub_82BEDC90`) memmoves the tail down and decrements the
/// count, so there are no holes to skip and indices are not stable across removals.
///
/// WHETHER IT IS ACTUALLY SHOWING is a separate question from whether it is live, and this
/// answers both. In game the guest keeps `pausemainmenu.bgf` and `menucycler.bgf` in the
/// registry permanently with valid roots while neither is on screen, so a host that draws
/// everything live puts a closed pause menu over the running game.
///
/// The gate is the ROOT ELEMENT's own `active` bit, read straight out of
/// `CPresentation::Update` (`sub_822AC880`):
///
///     822ac9c4  lwz    r4, 0x1F4(r30)     ; root = *(pres + 500)
///     822ac9d4  lhz    r11, 0x1C(r4)      ; the flags halfword
///     822ac9d8  rlwinm r10, r11, 0,18,18  ; bit 18 MSB-first == 0x2000
///     822ac9e0  beq    cr6, loc_822AC9EC  ; clear -> skip the entire tree walk
///
/// It is bit `0x2000` (`active`) and NOT `0x1000` (`globalactive`). That distinction is the
/// whole trap: the flag writer is a plain mask set/clear with no cross-linking, and the only
/// code that clears `0x1000` lives inside the tree walk that this gate skips — so a hidden
/// presentation keeps `globalactive` set on its root forever. Following `0x1000` here would
/// report every closed menu as visible.
///
/// `pres+873` is checked too, but it means FROZEN rather than hidden: non-zero returns before
/// the per-frame lists are wiped, so the presentation holds last frame's content instead of
/// becoming empty.
///
/// Five words per presentation — {address, root, name offset, state, active count} — where
/// state bit 0 is "root active", bit 1 is "frozen". The active count is the guest's own
/// answer: `pres+84` is a 48-byte array rebuilt every frame with exactly the globally-active
/// elements, so its count (at `pres+124`) is zero for a screen that is contributing nothing.
///
/// The names are written
/// into <paramref name="names"/> as NUL-separated ASCII. The NAME is the only discriminator
/// there is: every presentation shares one vtable (`off_82006D48`), so the class cannot tell
/// the HUD from a menu; the string at `presentation+12` can. It is an AKString — `char*` at
/// +12, u16 length at +16 — and it holds the path the factory was handed, e.g. the one ending
/// `HUD.bgf`. Match on a SUFFIX rather than the whole string: the path is built by
/// `LHFile_MakePathOSConformant` and its prefix and slash direction are not established.
///
/// Returns the number of live presentations, which may exceed what fitted.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPresentations(uint32_t* words, uint32_t word_capacity,
                                   char* names, uint32_t name_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    if (!plausible(gui)) {
        return 0;
    }

    const uint32_t player = read32(gui + 16u);
    if (!plausible(player)) {
        return 0;
    }

    const uint32_t array = read32(player + 72u);
    const uint32_t count = read32(player + 80u);
    if (!plausible(array)) {
        return 0;
    }

    // A ceiling on a count read out of guest memory, so a corrupted word cannot walk off.
    constexpr uint32_t kMaxPresentations = 256u;
    constexpr uint32_t kMaxNameChars = 260u;

    uint32_t found = 0;
    uint32_t chars = 0;

    for (uint32_t i = 0; i < count && i < kMaxPresentations; ++i) {
        const uint32_t presentation = read32(array + i * 4u);
        if (!plausible(presentation)) {
            continue;
        }

        // Null-checked: the pool is installed through a pointer to the +492 subobject, so
        // during load a live presentation can have no root.
        const uint32_t root = read32(presentation + 492u + 8u);

        uint32_t offset = 0xFFFFFFFFu;
        const uint32_t text = read32(presentation + 12u);
        const uint16_t length = GuestHalfAt(base, presentation + 16u);

        if (plausible(text) && length > 0 && length < kMaxNameChars) {
            offset = chars;
            for (uint16_t c = 0; c < length; ++c) {
                if (names != nullptr && chars < name_capacity) {
                    names[chars] = static_cast<char>(GuestByteAt(base, text + c));
                }
                ++chars;
            }
            if (names != nullptr && chars < name_capacity) {
                names[chars] = '\0';
            }
            ++chars;
        }

        // Is it SHOWING? The root's own `active` bit, and the freeze byte.
        uint32_t state = 0u;
        if (plausible(root)) {
            const uint16_t flags = GuestHalfAt(base, root + 0x1Cu);
            if ((flags & 0x2000u) != 0u) {
                state |= 1u;
            }
        }
        // The two presentation-level suppression bytes, INVERTED — 0 is live, 1 is parked.
        // The constructor zeroes both, so a presentation is born visible.
        //
        //   +0x369 (873) gates the per-frame UPDATE. `CPresentation::Update` reads it first
        //                (`lbz r11, 0x369(r30)` at 0x822AC890) and returns immediately.
        //   +0x368 (872) gates the SCENE DRAW. `CScene`'s slot-5 fetches the owning
        //                presentation from `scene+4` and calls its vtable +0x60
        //                (`sub_822CD108`, `lbz r3, 0x368(r3); blr`), branching past the draw
        //                when it is non-zero.
        //
        // The pause menu and the menu cycler are loaded and then written (1,1); the HUD's
        // loader never touches either byte.
        if (GuestByteAt(base, presentation + 873u) != 0u) {
            state |= 2u;
        }
        if (GuestByteAt(base, presentation + 872u) != 0u) {
            state |= 4u;
        }

        // The guest's own count of globally-active elements this frame — zero for a screen
        // that is drawing nothing. Reported alongside the flag, not instead of it.
        const uint32_t active_count = read32(presentation + 124u);

        if (words != nullptr && (found * 5u + 4u) < word_capacity) {
            words[found * 5u] = presentation;
            words[found * 5u + 1u] = plausible(root) ? root : 0u;
            words[found * 5u + 2u] = offset;
            words[found * 5u + 3u] = state;
            words[found * 5u + 4u] = active_count;
        }
        ++found;
    }

    return found;
}

// ---------------------------------------------------------------------------
// The Gameface Lua state, read directly.
//
// Every in-game list is `g_Menu`, a plain Lua table in the presentation's own VM, stating the
// visible order, the fold, the depth, the cursor and each row's commit identity. What follows
// is a READ-ONLY walk: no guest code runs, nothing is pushed on a Lua stack, and there is no
// thread constraint.
//
// There are two Lua VMs in this image and they DO NOT share a layout. Every offset below is
// from the GAMEFACE one, taken out of its own copy of the interpreter:
//
//   index2adr_gameface (0x82BCBB98)  LUA_GLOBALSINDEX -> `a1 + 17` words, i.e. L + 68.
//                                    The game VM's copy (0x82A240E0) returns `a1 + 18` — 72.
//                                    Reading the game VM's offset here lands on `env`.
//   luaH_getnum        (0x821E18D8)  array at t+12, sizearray at t+28, node at t+16,
//                                    lsizenode a BYTE at t+7, Node stride 20.
//                                    The game VM's luaH_getstr (0x82228470) reads a full
//                                    `int sizenode` at t+8 and node at t+20 — LuaPlus.
//   luaS_newlstr       (0x8227BBB8)  TString len at +12, chars at +16.
//   lua_getfield       (0x821E1848)  `L->top += 8` — TValue is EIGHT bytes, so lua_Number is
//                                    a float, not a double.
//
// The state itself hangs off the presentation: `sub_82426470` fetches it through vtable+20
// (`sub_82915D88`, `return *(pres + 28)`) and `sub_82372500` then reads `*(that + 4)` as the
// `lua_State*`. One state per CPresentation, so the caller names the presentation.
// ---------------------------------------------------------------------------
//
// `extern "C++"` because this file's exports live inside one big `extern "C"` block and these
// helpers return `LuaValue` and `std::string`, which warn under C linkage.
extern "C++" {
namespace {
/// Vanilla Lua 5.1 type tags — the one thing both VMs agree on.
constexpr uint32_t kLuaTNil = 0u;

constexpr uint32_t kLuaTBoolean = 1u;
constexpr uint32_t kLuaTNumber = 3u;
constexpr uint32_t kLuaTString = 4u;
constexpr uint32_t kLuaTTable = 5u;

/// `L + 68` — see index2adr_gameface above. NOT 72.
constexpr uint32_t kLuaGlobalsOffset = 68u;

/// A TValue, split. `value` is the GC pointer, the boolean, or the float's bit pattern.
struct LuaValue {
    uint32_t tt = kLuaTNil;
    uint32_t value = 0u;
};

bool LuaPlausible(uint32_t pointer) { return pointer >= 0x10000u && pointer < 0xC0000000u; }

LuaValue LuaRead(const uint8_t* base, uint32_t at) {
    LuaValue out;
    out.value = GuestWordAt(base, at);
    out.tt = GuestWordAt(base, at + 4u);
    return out;
}

float LuaNumber(const LuaValue& value) {
    float number = 0.0f;
    std::memcpy(&number, &value.value, sizeof(number));
    return number;
}

/// Lua's own truth: only nil and false are false. A zero NUMBER is true.
bool LuaTruthy(const LuaValue& value) {
    return !(value.tt == kLuaTNil || (value.tt == kLuaTBoolean && value.value == 0u));
}

int32_t LuaInt(const LuaValue& value) {
    return value.tt == kLuaTNumber ? static_cast<int32_t>(LuaNumber(value)) : 0;
}

std::string LuaText(const uint8_t* base, const LuaValue& value) {
    if (value.tt != kLuaTString || !LuaPlausible(value.value)) {
        return std::string();
    }
    const uint32_t length = GuestWordAt(base, value.value + 12u);
    if (length > 1024u) {
        return std::string();
    }
    std::string out;
    out.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
        out.push_back(static_cast<char>(GuestByteAt(base, value.value + 16u + i)));
    }
    return out;
}

/// How many Node slots the hash part has. A table with no hash part points at the shared dummy
/// node with `lsizenode` 0, which reads back as one nil-keyed slot.
uint32_t LuaNodeCount(const uint8_t* base, uint32_t table) {
    const uint8_t lsizenode = GuestByteAt(base, table + 7u);
    return lsizenode > 16u ? 0u : (1u << lsizenode);
}

/// Reads several string-keyed fields in ONE pass over the hash part.
///
/// By scan, not by hash: finding a key by its main position needs the INTERNED TString for
/// that key, so correctness would depend on matching the guest's hash function. These tables
/// are menu items — thirty-two slots — and one pass fills every field.
void LuaFields(const uint8_t* base, uint32_t table, const char* const* keys, LuaValue* out,
               size_t count) {
    for (size_t k = 0; k < count; ++k) {
        out[k] = LuaValue{};
    }
    if (!LuaPlausible(table)) {
        return;
    }

    const uint32_t node = GuestWordAt(base, table + 16u);
    const uint32_t nodes = LuaNodeCount(base, table);
    if (!LuaPlausible(node)) {
        return;
    }

    for (uint32_t i = 0; i < nodes; ++i) {
        const uint32_t slot = node + i * 20u;
        if (GuestWordAt(base, slot + 12u) != kLuaTString) {
            continue;
        }
        const uint32_t key = GuestWordAt(base, slot + 8u);
        if (!LuaPlausible(key)) {
            continue;
        }
        const uint32_t length = GuestWordAt(base, key + 12u);
        if (length == 0u || length > 64u) {
            continue;
        }

        for (size_t k = 0; k < count; ++k) {
            if (out[k].tt != kLuaTNil || std::strlen(keys[k]) != length) {
                continue;
            }
            bool same = true;
            for (uint32_t c = 0; c < length && same; ++c) {
                same = GuestByteAt(base, key + 16u + c) == static_cast<uint8_t>(keys[k][c]);
            }
            if (same) {
                out[k] = LuaRead(base, slot);
                break;
            }
        }
    }
}

LuaValue LuaField(const uint8_t* base, uint32_t table, const char* key) {
    LuaValue out;
    const char* keys[1] = {key};
    LuaFields(base, table, keys, &out, 1);
    return out;
}

/// `t[i]` for a positive integer key — the array part first, then the hash part.
///
/// Both parts: which one holds an index is a rehash decision, and a table built one element at
/// a time can leave its tail in the hash part.
LuaValue LuaIndex(const uint8_t* base, uint32_t table, int32_t index) {
    if (!LuaPlausible(table) || index < 1) {
        return LuaValue{};
    }

    const uint32_t sizearray = GuestWordAt(base, table + 28u);
    if (static_cast<uint32_t>(index) <= sizearray) {
        const uint32_t array = GuestWordAt(base, table + 12u);
        if (LuaPlausible(array)) {
            return LuaRead(base, array + (static_cast<uint32_t>(index) - 1u) * 8u);
        }
    }

    const uint32_t node = GuestWordAt(base, table + 16u);
    const uint32_t nodes = LuaNodeCount(base, table);
    if (!LuaPlausible(node)) {
        return LuaValue{};
    }

    for (uint32_t i = 0; i < nodes; ++i) {
        const uint32_t slot = node + i * 20u;
        if (GuestWordAt(base, slot + 12u) != kLuaTNumber) {
            continue;
        }
        LuaValue key;
        key.tt = kLuaTNumber;
        key.value = GuestWordAt(base, slot + 8u);
        if (LuaNumber(key) == static_cast<float>(index)) {
            return LuaRead(base, slot);
        }
    }

    return LuaValue{};
}

/// The presentation's own globals table, or 0.
uint32_t LuaGlobals(const uint8_t* base, uint32_t presentation) {
    if (!LuaPlausible(presentation)) {
        return 0u;
    }
    const uint32_t engine = GuestWordAt(base, presentation + 28u);
    if (!LuaPlausible(engine)) {
        return 0u;
    }
    const uint32_t state = GuestWordAt(base, engine + 4u);
    if (!LuaPlausible(state)) {
        return 0u;
    }
    const LuaValue globals = LuaRead(base, state + kLuaGlobalsOffset);
    return globals.tt == kLuaTTable && LuaPlausible(globals.value) ? globals.value : 0u;
}

/// `g_Menu:WaitForInput()`, evaluated host-side from the same two globals it reads.
///
/// It is the FIRST line of `SelectMenuItem` (`expandablemenuinput.dis main.5 pc0..4`) and
/// returns before the button event and the fold, so while it is true every A press the host
/// posts is swallowed. The rule, from `expandablemenuinput.dis main.4`:
///
///     for _, a in ipairs(g_AnimationList) do
///       if not (0 < a.time_delay and not (a.start_time < 0)) then
///         if 0 < a.start_time and not (a.time_delay < the_time - a.start_time) then
///           return true          -- this one is still inside its window
///         end
///       end
///     end
///     return false
///
/// `the_time` has no writer — a shipped bug: the sequences `the_time`, `g_AnimationList` and
/// `start_time` do not occur in `default.xex`, so `the_time - start_time` is a nil arithmetic
/// error the enclosing `luaD_pcall` catches, aborting `SelectMenuItem`. It goes unnoticed
/// because `ProcessAnimationList` sweeps the list empty every frame.
///
/// `now` is therefore `getElapsedTime()` = `sub_8219F200`: `*(pres + 852) * 0.001`, the
/// presentation clock `ProcessAnimationList` writes `start_time` from. The blocking entry's
/// `start_time`, `time_delay` and the clock are written out, so a frozen clock is visible.
bool LuaMenuWaitingForInput(const uint8_t* base, uint32_t presentation, uint32_t globals,
                            uint32_t* out_count, float* out_now, float* out_start,
                            float* out_delay) {
    if (out_count != nullptr) *out_count = 0;
    if (out_now != nullptr) *out_now = 0.0f;
    if (out_start != nullptr) *out_start = 0.0f;
    if (out_delay != nullptr) *out_delay = 0.0f;

    const LuaValue list = LuaField(base, globals, "g_AnimationList");
    if (list.tt != kLuaTTable || !LuaPlausible(list.value)) {
        return false;
    }

    // `getElapsedTime()` — NOT the dead `the_time` global. See the note above.
    const float the_time =
        static_cast<float>(GuestWordAt(base, presentation + 852u)) * 0.001f;
    if (out_now != nullptr) *out_now = the_time;

    constexpr uint32_t kMaxAnimations = 512u;
    uint32_t count = 0;
    bool waiting = false;

    for (uint32_t i = 1; i <= kMaxAnimations; ++i) {
        const LuaValue entry = LuaIndex(base, list.value, static_cast<int32_t>(i));
        if (entry.tt != kLuaTTable || !LuaPlausible(entry.value)) {
            break;  // ipairs stops at the first hole
        }
        ++count;

        if (waiting) {
            continue;  // keep counting, but the answer is settled
        }

        const char* const kKeys[] = {"time_delay", "start_time"};
        LuaValue fields[2];
        LuaFields(base, entry.value, kKeys, fields, 2);

        const float delay = fields[0].tt == kLuaTNumber ? LuaNumber(fields[0]) : 0.0f;
        const float start = fields[1].tt == kLuaTNumber ? LuaNumber(fields[1]) : 0.0f;

        if (delay > 0.0f && start >= 0.0f) {
            continue;
        }
        if (!(start > 0.0f)) {
            continue;
        }
        if (delay < the_time - start) {
            continue;
        }

        waiting = true;
        if (out_start != nullptr) *out_start = start;
        if (out_delay != nullptr) *out_delay = delay;
    }

    if (out_count != nullptr) *out_count = count;
    return waiting;
}
}  // namespace
}  // extern "C++"


REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGoldenCockInteraction(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = LuaPlausible(root) ? GuestWordAt(base, root + 0xCu) : 0;
    const uint32_t state = LuaPlausible(game) ? GuestWordAt(base, game + 0xACu) : 0;
    if (!LuaPlausible(state)) return 0;
    const LuaValue globals = LuaRead(base, state + 72u);
    if (globals.tt != kLuaTTable || !LuaPlausible(globals.value)) return 0;
    const uint32_t count = GuestWordAt(base, globals.value + 8u);
    const uint32_t nodes = GuestWordAt(base, globals.value + 20u);
    if (!LuaPlausible(nodes) || count > 65536u) return 0;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t node = nodes + i * 20u;
        if (LuaText(base, LuaRead(base, node + 8u)) != "RefiiGoldenCockInteraction") continue;
        const LuaValue value = LuaRead(base, node);
        const float sequence = value.tt == kLuaTNumber ? LuaNumber(value) : 0.0f;
        return std::isfinite(sequence) && sequence >= 0.0f && sequence < 1000000.0f
            ? static_cast<uint32_t>(sequence) : 0u;
    }
    return 0;
}

/// The live expandable-menu model, straight out of the presentation's Lua. What is read, and
/// where it is stated:
///
///   g_Menu.MenuRoot.ChildItems           the whole tree, folded and unfolded alike
///   g_Menu.VisibleInfo.VisibleElements   which of those rows a fold is currently showing
///   g_Menu.CurrentHighlightIdx           the cursor, 1-based into the visible rows
///   item.Name / TextTag                  the identity every dispatcher strcmps on
///   item.EventType / IDLow / IDHigh      what `SelectMenuItem` commits with
///   item.IsOpen / Ghosted / Emphasis     the fold and the row's face
///   item.ChildItems                      whether it folds at all
///   item.ComponentIdx                    the 1-based pool button it is drawn in
///
/// `VisibleElements` is a NEW table every frame (`g_MenuFormatter:onUpdate` calls
/// `DirtyLayout` unconditionally), so nothing may be cached across a call. It holds only the
/// rows a fold currently exposes, so the walk starts at `MenuRoot.ChildItems` and returns the
/// WHOLE tree, each row saying whether it is showing and at which index.
///
/// `spellassigning.bgf`'s wheel also calls itself `g_Menu` but has a `State` field and no
/// `VisibleInfo`, so the read is gated on `VisibleInfo.VisibleElements` being a table.
///
/// Twelve words per row — {name, tag, depth, flags, eventType, idLow, idHigh, componentIdx,
/// menuItemIdx, emphasis, ratingSlide, visibleIndex} — where `name` and `tag` are offsets into
/// <paramref name="names"/> (0xFFFFFFFF for none), `visibleIndex` is 1-based or 0 for a row
/// the fold is hiding, and `flags` is bit 0 IsOpen, bit 1 Ghosted, bit 2 has children, bit 3
/// the name is usable as an identity, bit 4 currently showing.
///
/// <paramref name="header"/> takes EIGHT words: the cursor (1-based into the VISIBLE rows), how
/// many rows are visible, the `lua_State`, the `g_Menu` table, then the input gate — the live
/// `g_AnimationList` length with bit 31 set when `WaitForInput()` is true, and the three floats
/// that decide it (`the_time`, and the blocking entry's `start_time` and `time_delay`).
///
/// Called with no buffers it answers the visible COUNT and fills the header without touching
/// an item, which is the per-frame cursor read.
///
/// Returns the number of rows in the tree, which may exceed what fitted.
///
/// Nothing in the app calls this: the menu model is fed by `RefiiHostTakeMenuChunks`. Kept as
/// an instrument — reading the Gameface VM's own `g_Menu` back checks that the captured model
/// and the VM agree.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLuaMenu(uint32_t presentation, uint32_t* header,
                              uint32_t* words, uint32_t word_capacity,
                              char* names, uint32_t name_capacity) {
    if (header != nullptr) {
        for (uint32_t i = 0; i < 8u; ++i) {
            header[i] = 0u;
        }
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    if (header != nullptr && LuaPlausible(presentation)) {
        const uint32_t engine = GuestWordAt(base, presentation + 28u);
        header[2] = LuaPlausible(engine) ? GuestWordAt(base, engine + 4u) : 0u;
    }

    const uint32_t globals = LuaGlobals(base, presentation);
    if (globals == 0u) {
        return 0;
    }

    const LuaValue menu = LuaField(base, globals, "g_Menu");
    if (menu.tt != kLuaTTable || !LuaPlausible(menu.value)) {
        return 0;
    }

    const char* const kMenuKeys[] = {"VisibleInfo", "CurrentHighlightIdx", "MenuRoot"};
    LuaValue menu_fields[3];
    LuaFields(base, menu.value, kMenuKeys, menu_fields, 3);

    if (menu_fields[0].tt != kLuaTTable) {
        return 0;  // the wheel's g_Menu, or a menu that has not initialised
    }

    const LuaValue elements = LuaField(base, menu_fields[0].value, "VisibleElements");
    if (elements.tt != kLuaTTable || !LuaPlausible(elements.value)) {
        return 0;
    }

    const uint32_t root = menu_fields[2].tt == kLuaTTable ? menu_fields[2].value : 0u;

    if (header != nullptr) {
        header[0] = static_cast<uint32_t>(LuaInt(menu_fields[1]));
        header[3] = menu.value;
    }

    // A ceiling on values read out of guest memory, so a corrupted word cannot walk off.
    constexpr uint32_t kMaxRows = 512u;
    constexpr uint32_t kMaxVisible = 512u;
    constexpr uint32_t kWordsPerRow = 12u;

    // Which rows are showing, by table identity: `DirtyLayout` inserts the item tables
    // themselves, not copies, so a pointer compare is exact.
    uint32_t visible[kMaxVisible];
    uint32_t visible_count = 0;
    for (uint32_t i = 1; i <= kMaxVisible; ++i) {
        const LuaValue entry = LuaIndex(base, elements.value, static_cast<int32_t>(i));
        if (entry.tt != kLuaTTable || !LuaPlausible(entry.value)) {
            break;  // dense list: the first hole is the end
        }
        visible[visible_count++] = entry.value;
    }

    if (header != nullptr) {
        header[1] = visible_count;

        // The input gate — see LuaMenuWaitingForInput; it swallows the whole action.
        uint32_t animations = 0;
        float now = 0.0f;
        float start = 0.0f;
        float delay = 0.0f;
        const bool waiting = LuaMenuWaitingForInput(
            base, presentation, globals, &animations, &now, &start, &delay);
        header[4] = animations | (waiting ? 0x80000000u : 0u);
        std::memcpy(&header[5], &now, sizeof(now));
        std::memcpy(&header[6], &start, sizeof(start));
        std::memcpy(&header[7], &delay, sizeof(delay));
    }

    // Counting is not reading: with no buffers the answer is already in the header, so the
    // per-item hash walk is skipped.
    if (words == nullptr && names == nullptr) {
        return visible_count;
    }

    if (root == 0u) {
        return 0;  // no tree to walk; the cursor in the header is still good
    }

    uint32_t found = 0;
    uint32_t chars = 0;

    auto emit = [&](const std::string& text) -> uint32_t {
        if (text.empty()) {
            return 0xFFFFFFFFu;
        }
        const uint32_t offset = chars;
        for (char c : text) {
            if (names != nullptr && chars < name_capacity) {
                names[chars] = c;
            }
            ++chars;
        }
        if (names != nullptr && chars < name_capacity) {
            names[chars] = '\0';
        }
        ++chars;
        return offset;
    };

    // The whole tree, not just what is showing: a closed folder's children are absent from
    // `VisibleElements` but present in `ChildItems`, so the walk starts at `MenuRoot`.
    //
    // Depth-first, explicit stack, bounded — a cycle in a guest-owned table must not spin here.
    struct Frame {
        uint32_t table;
        int32_t index;
        uint32_t depth;
    };
    Frame stack[24];
    uint32_t top = 0;

    const LuaValue root_children = LuaField(base, root, "ChildItems");
    if (root_children.tt != kLuaTTable || !LuaPlausible(root_children.value)) {
        return 0;
    }
    stack[top++] = Frame{root_children.value, 1, 1u};

    while (top > 0 && found < kMaxRows) {
        Frame& frame = stack[top - 1];
        const LuaValue entry = LuaIndex(base, frame.table, frame.index);
        ++frame.index;

        if (entry.tt != kLuaTTable || !LuaPlausible(entry.value)) {
            --top;
            continue;
        }

        const uint32_t depth = frame.depth;

        const char* const kItemKeys[] = {"Name",       "TextTag",      "EventType",
                                         "IDLow",      "IDHigh",       "IsOpen",
                                         "Ghosted",    "Emphasis",     "ChildItems",
                                         "ComponentIdx", "MenuItemIdx", "RatingSlide"};
        LuaValue item[12];
        LuaFields(base, entry.value, kItemKeys, item, 12);

        uint32_t showing = 0;
        for (uint32_t v = 0; v < visible_count; ++v) {
            if (visible[v] == entry.value) {
                showing = v + 1u;
                break;
            }
        }

        const std::string name = LuaText(base, item[0]);
        const uint32_t name_offset = emit(name);
        const uint32_t tag_offset = emit(LuaText(base, item[1]));

        const bool has_children =
            item[8].tt == kLuaTTable && LuaPlausible(item[8].value) &&
            LuaIndex(base, item[8].value, 1).tt == kLuaTTable;

        uint32_t flags = 0u;
        if (LuaTruthy(item[5])) flags |= 1u;
        if (LuaTruthy(item[6])) flags |= 2u;
        if (has_children) flags |= 4u;
        if (!name.empty()) flags |= 8u;
        if (showing != 0u) flags |= 16u;

        if (words != nullptr && (found * kWordsPerRow + kWordsPerRow - 1u) < word_capacity) {
            uint32_t* row = words + found * kWordsPerRow;
            row[0] = name_offset;
            row[1] = tag_offset;
            row[2] = depth;
            row[3] = flags;
            row[4] = static_cast<uint32_t>(LuaInt(item[2]));
            row[5] = static_cast<uint32_t>(LuaInt(item[3]));
            row[6] = static_cast<uint32_t>(LuaInt(item[4]));
            row[7] = static_cast<uint32_t>(LuaInt(item[9]));
            row[8] = static_cast<uint32_t>(LuaInt(item[10]));
            row[9] = static_cast<uint32_t>(LuaInt(item[7]));
            row[10] = static_cast<uint32_t>(LuaInt(item[11]));
            row[11] = showing;
        }
        ++found;

        if (has_children && top < (sizeof(stack) / sizeof(stack[0]))) {
            stack[top++] = Frame{item[8].value, 1, depth + 1u};
        }
    }

    return found;
}

/// Walks one presentation's element tree.
///
/// <paramref name="requested_presentation"/> is a `CPresentation*` from
/// <see cref="RefiiHostGetPresentations"/>, or 0 for the front end's active screen — which is
/// what every caller meant before the HUD and the in-game menus needed reaching.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiElements(uint32_t requested_presentation,
                                uint32_t* words, uint32_t word_capacity,
                                uint16_t* text, uint32_t text_capacity) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    // WHICH presentation. Zero keeps the old meaning — the front end's active screen — and
    // any other value is one the caller enumerated.
    //
    // The front-end chain below is NOT general and it is DEAD in game: `gui+188` is written
    // only by the derived GUI_FRONT_END constructor (`sub_82378230`), and once the game takes
    // over, the object republished at 0x83496B08 is the BASE class, allocated at 176 bytes
    // (`li r3, 0xB0` at 0x82310FE0) — so reading +188 there runs off the end of the
    // allocation. That is why the HUD and the thirty in-game menus were invisible here: not
    // because they are a different kind of object, but because this could only ever find one
    // screen, and only while the front end owned the GUI.
    uint32_t presentation = requested_presentation;
    if (presentation == 0u) {
        const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
        if (!plausible(gui)) {
            return 0;
        }

        const uint32_t active = read32(gui + 188u);
        if (!plausible(active)) {
            return 0;
        }

        // Front-end wrappers hold the CPresentation at +20. The HUD (`GUI+32` then `+4`) and
        // the menus (`mgr+12+4*id`) store it DIRECTLY — that indirection difference is what
        // RefiiHostGetPresentations exists to hide.
        presentation = read32(active + 20u);
    }

    if (!plausible(presentation)) {
        return 0;
    }

    // +492 is an embedded subobject and its `+8` is the root node — verified from two
    // one-instruction thunks that compose to it: `sub_82284360` is `addi r3, r3, 0x1EC; blr`
    // and `sub_830F3910` is `lwz r3, 8(r3); blr`. 0x1EC is 492, measured from the
    // CPresentation base. Identical for the HUD, the menus and the front end, because all
    // three come out of one factory (`sub_823DCB10`).
    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    // The live text of one element, or "no text". Only TEXT elements (type 4) carry a
    // record; asking any other kind for one would be dereferencing whatever `+4` happens
    // to point at for that type.
    auto text_rep = [&](uint32_t element) -> uint32_t {
        if (GuestByteAt(base, element + 30u) != 4u) {
            return 0;
        }

        const uint32_t holder = read32(element + 4u);
        if (!plausible(holder)) {
            return 0;
        }

        const uint32_t record = read32(holder);
        if (!plausible(record)) {
            return 0;
        }

        const uint32_t rep = read32(record + 140u);
        return plausible(rep) ? rep : 0;
    };

    // Link-following, with NO auxiliary stack.
    //
    // The first version pushed siblings onto a fixed 64-entry stack and dropped subtrees
    // when it filled: it reported 289 of this screen's 3812 elements, so 92% of the UI
    // fell back to a guess while looking like live data. Walking child -> sibling and
    // climbing through the parent link at +12 has no such ceiling; the only bound is the
    // element count itself, which also breaks a corrupted cycle.
    constexpr uint32_t kMaxElements = 16384u;
    constexpr uint32_t kMaxChars = 4096u;

    uint32_t found = 0;
    uint32_t chars = 0;
    uint32_t element = root;

    while (element != 0 && found < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        uint32_t offset = 0xFFFFFFFFu;
        const uint32_t rep = text_rep(element);
        if (rep != 0) {
            const uint32_t data = read32(rep);
            const uint32_t length = read32(rep + 4u);
            if (plausible(data) && length > 0) {
                // TRUNCATED, never dropped: an item description runs past 512 units, and a
                // dropped string reads on the host as "the guest wrote nothing".
                const uint32_t units = std::min(length, kMaxChars);
                offset = chars;
                for (uint32_t i = 0; i < units; ++i) {
                    const uint16_t unit = GuestHalfAt(base, data + i * 2u);
                    if (text != nullptr && chars < text_capacity) {
                        text[chars] = unit;
                    }
                    ++chars;
                }

                if (text != nullptr && chars < text_capacity) {
                    text[chars] = 0;
                }
                ++chars;
            }
        }

        // Stride 6: NAME HASH, slide, text offset, type, FLAGS, address.
        //
        // `element+0` is the NAME HASH, not a vtable: elements carry no vtable at all. Both
        // constructors (`sub_82BB2E18` 32-byte, `sub_82BB2F10` 68-byte) bump-allocate out of
        // one flat POD arena and store the file's own dword at +0 (`82bb2f7c stw r5, 0(r31)`),
        // zeroing every other field. The manager's lookup `sub_82BB5658` compares that word
        // against the sdbm hash `sub_82189140` computes from the element's name.
        //
        // That arena is why the sixth word is not an identity: the element address is
        // bump-allocated and reused, so it joins the change feed to this walk WITHIN one seed
        // only, and the name hash is what says the seed still describes the same tree.
        //
        // The flags word is the one that decides whether an element is drawn, and reading it
        // replaces a host-side reimplementation of the rule with the guest's own answer.
        //
        // `element+0x1C` is a big-endian u16. Bit 0x2000 is `active`, the authored state the
        // .bgf carries and Lua's setAttribute writes; bit 0x1000 is `globalactive`, which the
        // runtime DERIVES every frame in sub_822849A0 as
        //     globalactive = active AND in-time-range AND parent's globalactive
        // and enforces structurally — that walk never enters a subtree whose parent failed.
        //
        // 0x1000 is the one to follow. sub_82188B70 copies exactly it onto the render node:
        //     lwz  r11, 4(r31)    ; node+4 = element
        //     lhz  r10, 0x1C(r11) ; the flags halfword
        //     extrwi r9, r10, 1,19; bit 0x1000
        //     stb  r9, 8(r31)     ; node+8, the draw gate
        // and sub_8229E400 tests node+8 before descending, so a zero prunes the whole subtree.
        //
        // Opacity is NOT a second gate. sub_8229E358 uses it only to choose the transparent
        // bucket — a fully transparent element is still traversed and still submitted, so an
        // element carrying no opacity attribute is not thereby hidden.
        //
        // The TYPE byte at +0x1E carries no live state; it is here so the ORDINAL JOIN between
        // the guest walk and the .bgf can be checked rather than assumed.
        //
        // Every piece of live state we apply — slide, text, opacity — is keyed by position:
        // the guest's depth-first walk order is taken to equal the .bgf's element order. That
        // was verified 77/77 on the title screen and never on the main menu, which has 3812
        // elements. A divergence anywhere would hand every element after it someone else's
        // state, which does not look like a join bug; it looks like a mostly-correct screen
        // with stray widgets on it, and that is exactly what we are looking at.
        //
        // Comparing the guest's type sequence against the file's localises any divergence to
        // an exact index, or proves there is none.
        //
        // Stride 6 since ABI 84 — the sixth word is the ELEMENT ADDRESS, which joins this
        // ordinal-indexed walk to the element-change feed, that feed stating its changes by
        // element pointer.
        if (words != nullptr && (found * 6u + 5u) < word_capacity) {
            const uint16_t flags = GuestHalfAt(base, element + 0x1Cu);

            words[found * 6u] = read32(element);
            words[found * 6u + 1u] = GuestByteAt(base, element + 43u);
            words[found * 6u + 2u] = offset;
            words[found * 6u + 3u] = GuestByteAt(base, element + 30u);
            words[found * 6u + 4u] = flags;
            words[found * 6u + 5u] = element;
        }
        ++found;

        // Down first, then along, then climb until there is something along — the root
        // itself is never followed sideways, or the walk would escape into its siblings.
        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        // Bounded: a torn tree whose parent links form a cycle would otherwise hold the
        // caller here for ever.
        int climbed = 0;
        while (element != 0) {
            if (element == root || ++climbed > 16384) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0;
        }
    }

    return found;
}

/// The live state of the guest's front end, for Godot to mirror.
///
/// Walks the one global the whole front end hangs off — `0x83496B08`, published by the
/// GUI base ctor at 0x8237132C — down to the screen the guest currently has active:
///
///     GUI+180 startup/title screen   (96 B,  vtable 0x820D09C4)
///     GUI+184 main menu              (232 B, vtable 0x820D0934)
///     GUI+188 THE ACTIVE ONE         (set at 0x8237834C, switched by sub_82379488)
///
/// `screen+24` is the screen's own state int. For the title screen the proven values are
/// 9 legal sequence, 0 idle (this is where "press A" lives), 8 attract video, 10 start
/// sequence playing, 11 hand over to the main menu, 12 dialog. `startup+80` is the flag
/// the "CAN_PRESS_A" presentation event sets at 0x826E2E38 — A is IGNORED until it is 1
/// (`if (a2 == 36 && *(BYTE*)(a1+80))` at 0x826E3078). `menu+72` is the selected index.
///
/// Reading rather than driving, deliberately: the guest owns this state machine. Godot
/// needs to know which screen is up and which item is highlighted so it can draw them.
///
/// Also reports the active screen's SCENE SLIDE, which is what actually decides what the
/// front end looks like. Its Lua navigates with `goToSlide(SCENE, n)` over
/// `{PressStart=1, Title=2, NewGame=3, LoadGame=4, Option=5, Multiplayer=6, Debug=7}`,
/// and `frontendmainmenu.bgf` carries exactly seven slides on its root — one page each.
/// Without it a host draws every page at once.
///
/// The slide lives in the element itself: gameplan GoToSlide (`sub_82BB5760`) stores the
/// index to `element+43` at 0x82BB5860, and the getter (`sub_82BB5990`) reads that same
/// byte back at 0x82BB59AC. The chain to the root element is
/// `screen+20` (CPresentation, vtable 0x82006D48) -> `+492` (element manager, its vtable
/// slot +68 is literally `return *(u32*)(mgr+8)`) -> the root.
///
/// Fills TEN words: gui, active screen, which (0 none / 1 title / 2 menu), state,
/// press-A-armed, cursor, scene slide, slide count, the ACTIVE SCREEN'S VTABLE, and THE
/// GUI OBJECT'S OWN VTABLE.
///
/// The GUI's vtable is not belt and braces — it is the only thing that says whether these
/// fields mean anything. `dword_83496B08` has TWO writers, both through the same base ctor
/// `sub_823710A8` (the store is at 0x8237132C):
///
///   - `sub_82378230`, the front-end GUI, whose derived part holds +180 title,
///     +184 menu, +188 active;
///   - `Game_InitDatabases` (0x82310FF0), which builds a BASE-ONLY instance. The base ctor
///     initialises to +172 and no further, so +180/+184/+188 on that object are not screen
///     pointers at all.
///
/// Reading the second as though it were the first reports "the front end went nowhere" for a
/// game that has started, so the caller must check the vtable before believing `which`.
///
/// Returns 1 when the GUI exists.
REFII_HOST_ABI_EXPORT int RefiiHostGetFrontEnd(uint32_t* out) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 10; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // The guest is big-endian; every pointer and int below needs the swap.
    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    // A guest pointer, not an offset into anything of ours — sanity-check it before
    // chasing it, or a half-built GUI walks us into unmapped memory.
    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    if (!plausible(gui)) {
        return 0;
    }

    out[0] = gui;
    out[9] = read32(gui);  // the GUI object's vtable — which of the two kinds this is

    const uint32_t startup = read32(gui + 180u);
    const uint32_t menu = read32(gui + 184u);
    const uint32_t active = read32(gui + 188u);
    if (!plausible(active)) {
        return 1;
    }

    out[1] = active;
    out[2] = active == startup ? 1u : (active == menu ? 2u : 0u);
    out[3] = read32(active + 24u);
    out[8] = read32(active);  // the screen's vtable — its identity

    if (plausible(startup)) {
        out[4] = GuestByteAt(base, startup + refii::guest::FrontEndTitleField(80u));
    }
    if (plausible(menu)) {
        out[5] = read32(menu + refii::guest::FrontEndMenuField(72u));
    }

    // The scene slide, down the presentation's own element tree.
    const uint32_t presentation = read32(active + 20u);
    if (plausible(presentation)) {
        const uint32_t root = read32(presentation + 492u + 8u);
        if (plausible(root)) {
            out[6] = GuestByteAt(base, root + 43u);  // current slide
            out[7] = GuestByteAt(base, root + 42u);  // how many it has, for a sanity check
        }
    }

    return 1;
}

/// Where the front end got to — the fields that separate a title that never activated, one
/// that activated into a tick that is gated off, and one ticking with nothing to show.

///
/// Fills SIX words:
///
///   0,1  `gui+0x104` and `gui+0x10C` — the two refcounted cells the front-end Init builds from
///        the precomputed name hashes at 0x823787B8 and 0x82378868. Both NULL means Init never
///        ran: the constructor zeroes them (0x82378384 / 0x82378394) and only Init writes them.
///   2,3  each cell's `+8`, the gate at 0x8237919C/0x823791AC — a BOOLEAN mip-residency
///        predicate published by the streaming job `sub_82A67188`, not a resource pointer.
///        Either one zero and `sub_82379170` returns at 0x8237947C, discarding the whole
///        front-end frame. A missing file still passes (the 16x16 placeholder stores mip 0),
///        so zero means the streaming job is not being serviced, not that a texture is absent.
///   4    the title's presentation pointer (`title+20`), 0 before the `.bgf` is built.
///   5    1 when the title's last-input stamp at `title+72` has ever been written; the ctor
///        leaves it 0.0, so 0 means the screen is at its CONSTRUCTED state, not idle.
///
/// Returns 1 when the front-end GUI exists.
REFII_HOST_ABI_EXPORT int RefiiHostGetFrontEndInit(uint32_t* out) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 6; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || GuestWordAt(base, gui) != refii::guest::Address(0x820A924Cu)) {
        return 0;
    }

    const uint32_t cell_logo = GuestWordAt(base, gui + 0x104u);
    const uint32_t cell_button = GuestWordAt(base, gui + 0x10Cu);
    out[0] = cell_logo;
    out[1] = cell_button;
    if (plausible(cell_logo)) {
        out[2] = GuestWordAt(base, cell_logo + 8u);
    }
    if (plausible(cell_button)) {
        out[3] = GuestWordAt(base, cell_button + 8u);
    }

    const uint32_t title = GuestWordAt(base, gui + 180u);
    if (plausible(title) && GuestWordAt(base, title) == refii::guest::Address(0x820D09C4u)) {
        out[4] = GuestWordAt(base, title + 20u);
        out[5] = GuestWord64At(base, title + refii::guest::FrontEndTitleField(72u)) != 0u ? 1u : 0u;
    }

    return 1;
}

/// One NARROW LHString out of the guest, as NUL-terminated UTF-8.
///
/// The member at the offset is a `Rep*`, not the Rep — four bytes, not eight — so the two
/// consecutive strings at `+268` and `+272` do NOT overlap, which is worth stating because
/// assuming an inline 8-byte Rep makes them look as though they must. `Rep+0` is the character
/// data and `Rep+4` its length; `sub_821F0240` keeps the refcount at `Rep+12`.
///
/// Returns the number of bytes written INCLUDING the terminator, so a caller can pack several
/// strings back to back.
static uint32_t write_guest_narrow_string(const uint8_t* base, uint32_t at, char* out,
                                          uint32_t capacity) {
    if (out == nullptr || capacity == 0u) {
        return 0u;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    out[0] = '\0';
    if (at == 0u) {
        return 1u;
    }

    const uint32_t rep = read32(at);
    if (!plausible(rep)) {
        return 1u;
    }

    const uint32_t chars = read32(rep);
    const uint32_t length = read32(rep + 4u);
    if (!plausible(chars) || length == 0u || length >= 512u) {
        return 1u;
    }

    uint32_t written = 0;
    for (uint32_t i = 0; i < length && written + 1u < capacity; ++i) {
        const char c = static_cast<char>(GuestByteAt(base, chars + i));
        if (c == '\0') {
            break;
        }
        out[written++] = c;
    }

    out[written] = '\0';
    return written + 1u;
}

/// THE LOADING SCREEN OBJECT — and the one global that has to be right to reach it.
///
/// THE ROOT IS NOT `0x83496B08`. That global is the FRONT END's, and reading the loading screen
/// through it is how three of this file's four loading-screen readers came to return other
/// people's memory. The guest's own frame driver `sub_82388380` picks its root from a
/// three-deep fallback chain, verified instruction for instruction at 0x823885FC-0x82388644:
///
///     823885fc  lis    r11, dword_83496AB8@ha
///     8238860c  lwz    r11, dword_83496AB8@l(r11)   ; first choice
///     82388614  bne    cr6, loc_82388638            ; non-null wins
///     8238861c  lwz    r11, dword_83496AC0@l(r11)   ; else this
///     82388624  bne    cr6, loc_82388638
///     8238862c  lwz    r11, dword_8335C4C4@l(r11)   ; else this
///     82388634  beq    cr6, loc_82388648            ; all null: no loading screen at all
///     82388638  lwz    r11, 8(r11)                  ; owner   = *(app + 8)
///     82388640  lwz    r3,  0x24(r11)               ; manager = *(owner + 36)
///
/// and then `sub_82361AF8` takes `screen = *(manager + 4)`.
///
/// THE SYMPTOM THIS EXPLAINS, and why it was so easy to misread as bad offsets. The art reader
/// went through `RefiiHostGetLoadingScreen`, which already used the right roots; the tip and
/// layout readers used the front end's. Both landed on a 400-byte block, so both looked like
/// they were working — the art path, the type and the fade all read back correctly from the
/// real object, while the tip index and the list count came off a DIFFERENT, freed one and read
/// as rising floats around 3.3. Every offset involved was correct. Only the base was wrong.
///
/// One resolver now, so the four readers cannot drift apart again. Returns 0 when there is no
/// screen up, which is an ordinary state rather than a failure.
static uint32_t loading_screen_object(const uint8_t* base) {
    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    for (uint32_t root : {refii::guest::Address(0x83496AB8u), refii::guest::Address(0x83496AC0u), refii::guest::Address(0x8335C4C4u)}) {
        const uint32_t app = read32(root);
        if (!plausible(app)) {
            continue;
        }

        const uint32_t owner = read32(app + 8u);
        if (!plausible(owner)) {
            continue;
        }

        const uint32_t manager = read32(owner + 36u);
        if (!plausible(manager)) {
            continue;
        }

        // 0 here is "no loading screen", not "wrong pointer" — `sub_82361458` clears it on
        // teardown. Keep looking rather than reporting the first root's silence as the answer.
        const uint32_t screen = read32(manager + 4u);
        if (plausible(screen)) {
            return screen;
        }
    }

    return 0;
}

/// EVERYTHING THE LOADING SCREEN DRAWS BESIDES THE PAINTING — the tip, the banner, the spinner.
///
/// The screen is more than its art: the original also puts up a rotating TIP over a panel, a
/// "Loading <level>" banner, an animated spinner and a vignette. None of that is a Gameface
/// presentation, so none of it arrives through the element walk — it has to be read off the
/// screen object directly. Offsets measured from the SCREEN object (`manager+4`).
///
/// THE TIP IS A BABEL TAG, NOT A STRING. `sub_8235E420` resolves the tag GROUP named by
/// `dword_834975C0` (default `"GUI_SCREEN_LOADING_TIPS"`) and pushes every member tag onto the
/// std::list at `+380`; `sub_82360418` then picks entry `+300`, wrapping on the count at `+388`,
/// and advances it every ten seconds. So this returns the tag KEY and the host localises it
/// with its own Babel database — the alternative would be calling the guest's TextManager, and
/// this bridge does not call into the guest.
///
/// List walk: `+380` is the list object, so `+384` is `_Myhead` and `+388` is `_Mysize`. MSVC's
/// node is `{ _Next, _Prev, _Myval }`, hence the first element is `*(*(screen+384))` and the
/// value sits at node+8 — matching `sub_82360418`'s own `v9 = (_DWORD *)*v9` / `v9[2]`.
///
/// Fills SIX words — tip index, tip count, the current tip's tag key, the spinner's frame
/// counter, and the tips/banner enable bytes — and writes the level name then the host-set
/// overlay text as two NUL-separated UTF-8 strings. Returns 1 when a screen object was read.
REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreenText(uint32_t* out, char* text, uint32_t capacity) {
    if (out != nullptr) {
        for (int i = 0; i < 6; ++i) {
            out[i] = 0;
        }
    }
    if (text != nullptr && capacity > 0u) {
        text[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t screen = loading_screen_object(base);
    if (!plausible(screen)) {
        return 0;
    }

    if (out != nullptr) {
        const uint32_t index = read32(screen + 300u);
        const uint32_t count = read32(screen + 388u);

        out[0] = index;
        out[1] = count;
        out[3] = read32(screen + 168u);
        out[4] = GuestByteAt(base, screen + 313u);
        out[5] = GuestByteAt(base, screen + 314u);

        // Walk to the current tip. Bounded by the list's own count as well as by the index, so
        // a torn read cannot send this off the end of the chain.
        const uint32_t head = read32(screen + 384u);
        if (plausible(head) && count > 0u && index < count) {
            uint32_t node = read32(head);
            for (uint32_t step = 0; step < index && plausible(node); ++step) {
                node = read32(node);
            }

            if (plausible(node)) {
                out[2] = read32(node + 8u);
            }
        }
    }

    uint32_t written = 0;
    if (text != nullptr) {
        // THE TWO STRINGS ARE DIFFERENT WIDTHS, and reading either with the other's decoder
        // produces something that looks like a decoding bug rather than an encoding one.
        //
        // `+268` is the level NAME KEY, and it is NARROW — the same string instantiation as the
        // art path at `+272`, which this file already reads a byte at a time and gets right.
        // Reading it as UTF-16 halves its length and returns mojibake or nothing.
        //
        // `+280` is the host-set overlay, and it is WIDE (UTF-16, big-endian). It goes through
        // the other instantiation, `sub_82287C28` — the one Babel and the text manager return —
        // and `sub_82360968` searches it with the wide helpers. Reading it as bytes returns one
        // stray character, which is exactly what it was returning.
        written += write_guest_narrow_string(base, screen + 268u, text, capacity);
        written += write_guest_wide_string(base, screen + 280u, text + written,
                                           capacity > written ? capacity - written : 0u);
    }

    return 1;
}

/// HOW THE LOADING SCREEN LAYS ITS ART OUT — which of the guest's three paths is in force.
///
/// `sub_8235FDC0` does NOT draw the painting one way. It picks between three, and getting this
/// wrong mangles the picture rather than merely offsetting it:
///
///   +310 set  — the "quit without save" black fade: ONE quad (0,0,1280,720), plain 0..1 UVs.
///   +315 set  — a fixed art (`dword_8349AA04`): ONE quad, plain 0..1 UVs on a wide display,
///               or inset to (160,0,1120,720) — a centre crop — on 4:3.
///   otherwise — a normal level load: TWO quads. The 1280x720 painting is packed into a square
///               texture in two pieces and the right-hand 280 columns are stored ROTATED 90
///               degrees, so it must be reassembled:
///                 dest (0,0,1000,720)     u 0..0.9766  v 0..0.7031    straight
///                 dest (1000,0,1280,720)  u 0..0.7031  v 0.7266..1.0  u and v TRANSPOSED
///
/// Reassembling a fixed-art screen as though it were two pieces is exactly as wrong as
/// stretching a two-piece one flat, which is why the host cannot pick a single path and has to
/// be told. Offsets below are measured from the SCREEN object — the pointer at manager+4, the
/// same one `RefiiHostGetLoadingScreen` already returns.
///
/// Fills THREE words: the +315 fixed-art flag, the +310 quit-fade flag, and the +320 4:3 inset
/// flag. Returns 1 when a screen object was there to read.
REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreenLayout(uint32_t* out) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 3; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t screen = loading_screen_object(base);
    if (!plausible(screen)) {
        return 0;
    }

    out[0] = GuestByteAt(base, screen + 315u);
    out[1] = GuestByteAt(base, screen + 310u);
    out[2] = GuestByteAt(base, screen + 320u);
    return 1;
}

/// WHY A PRESENTATION'S TEXTURE HASHES ARE ALL ZERO — the breakdown, not the total.
///
/// `RefiiHostGetUiTextureHashes` returns zero for an element in four different situations, and
/// they need completely different work. The chain it walks is the guest's own, verified
/// instruction for instruction against `sub_82425C10`, the engine's "set this element's
/// texture" helper:
///
/// <code>
///   82425c10  lwz r11, 4(r4)      ; element+4 = scene node   -- NULL check, return 0
///   82425c1c  lwz r11, 0(r11)     ; node+0    = render item  -- NULL check, return 0
///   82425c2c  lwz r9, 0x200(r11)  ; quad+512  = texture hash
/// </code>
///
/// So a zero can mean: the element has no scene node; it has a node with no render item; it has
/// a render item carrying no image (0 is the guest's own "no texture" encoding — an empty name
/// hashes to 0 and the hash field doubles as a not-yet-computed sentinel); or the node is not an
/// IMAGE node at all (`node+10` selects the applier — only type 2 carries a texture).
///
/// Counting those apart is the difference between "this game does not swap textures", "the
/// guest builds no render items in this configuration", and "our walk is broken". A single
/// total of zero says none of them.
///
/// Fills SIX words: elements walked, with a scene node, with a render item, image nodes
/// (`node+10 == 2`), non-zero hashes, and nodes whose back-pointer disagrees.
///
/// THE BACK-POINTER IS THE VALIDITY CHECK. `node+4` points back at the element
/// (`sub_82188D10` reads it as its first act), so `*(node+4) == element` is the cheap proof
/// that what we followed really is a scene node rather than whatever else happened to be at
/// `element+4`. A non-zero count in that word means the walk is wrong and every other number
/// here is meaningless.
///
/// Returns 1 when the presentation was walked.
REFII_HOST_ABI_EXPORT int RefiiHostGetUiTextureStats(uint32_t presentation, uint32_t* out) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 6; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(presentation)) {
        return 0;
    }

    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    constexpr uint32_t kMaxElements = 16384u;
    uint32_t element = root;

    while (element != 0 && out[0] < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        ++out[0];

        const uint32_t node = read32(element + 4u);
        if (plausible(node)) {
            ++out[1];

            if (read32(node + 4u) != element) {
                ++out[5];
            }

            if (GuestByteAt(base, node + 10u) == 2u) {
                ++out[3];
            }

            const uint32_t quad = read32(node);
            if (plausible(quad)) {
                ++out[2];
                if (read32(quad + 512u) != 0u) {
                    ++out[4];
                }
            }
        }

        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        while (element != 0) {
            if (element == root) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0u;
        }
    }

    return 1;
}

/// WHICH IN-GAME SCREEN THE GUEST HAS UP — stated by the guest, not inferred.
///
/// The front end names its two screens in dedicated GUI members; the thirty IN-GAME screens
/// do not work that way at all. They live in a screen manager hung off `GUI+56`, as an array
/// of `CPresentation*` indexed by a screen ID, with a `std::map<int, std::string>` beside it
/// mapping that ID to the `.bgf` file name.
///
/// Layout, verified in the binary rather than taken second-hand — every offset below is
/// measured from the SCREEN MANAGER, i.e. from `*(GUI + 56)`, except where it says GUI:
///
///   mgr        = *(0x83496B08 + 56)
///   mgr + 0x04 = the CURRENT screen ID; 31 means "none up"
///   mgr + 0x0C = the first of 30 `CPresentation*`, indexed by ID
///   mgr + 0xA0 = the std::map<int, std::string> of ID -> ".bgf" name
///
/// The proof for the two that matter:
///
///   sub_8236E630, the generic opener, at 0x8236eb20:
///       *(_DWORD *)(*(_DWORD *)(dword_83496B08 + 56) + 4) = a2;   // a2 IS the screen ID
///
///   sub_823DCBE0, which reads it back:
///       v3 = a1[1];                       // mgr + 4
///       if ( v3 < 30 ) return a1[v3 + 3]; // mgr + 0x0C + 4*id
///
///   sub_8281F620, the ID -> name table, at 0x8281f640..0x8281f684:
///       addi r4, r11, aPausemainmenuB   # "PauseMainMenu.bgf"
///       li   r10, 0                     # <-- the key
///       stw  r10, 0x90+var_38(r1)
///       bl   sub_82321D80               # map.insert(0, "PauseMainMenu.bgf")
///   so SCREEN ID 0 IS THE PAUSE MENU, and 1 is PhotomodeScreen.bgf.
///
/// WHY THIS IS WORTH A CALL OF ITS OWN. We already walk the presentation registry and draw
/// whatever has its root element active, which is right for the HUD and for anything layered
/// over it — but "active" cannot say which screen the player is IN. The pause menu's
/// presentation is created ONCE at game entry (`sub_823DC888` from `sub_82371788`, gated on
/// the once-only byte at GUI+102) and is never freed, so it is in the registry from the first
/// frame of play whether or not the player has ever pressed pause. This field is the guest
/// stating the answer, which beats inferring it.
///
/// Fills 4 + 30 + 2 words: the GUI object, its vtable, the screen manager, the current screen
/// ID; then the THIRTY presentation pointers indexed by screen ID; then the shared slot's
/// owning ID and its presentation.
///
/// THE THIRTY POINTERS ARE THE POINT, not padding. Knowing that no screen is up is useless on
/// its own — the host still has to know WHICH of the presentations it can see belong to this
/// array, because the HUD does not (it hangs off the HUD manager at GUI+32, not off the screen
/// manager) and must keep drawing regardless. Comparing a presentation's address against these
/// thirty is the guest's own statement of membership, and it needs no name list.
///
/// The vtable is there for the same reason RefiiHostGetFrontEnd carries it — `0x83496B08` has
/// two writers and only the in-game one (`0x82001610`) has a screen manager at +56.
///
/// Returns 1 when the screen manager was reached.
// ---------------------------------------------------------------------------
// PUB GAMES
//
// THE GUEST OWNS THE WHOLE LIFECYCLE; Godot only plays the game. Read from the binary rather
// than inferred, and every offset below was verified by decompiling the function that writes it:
//
//   sub_82180418  returns &dword_834C20B8            -- the manager singleton
//   sub_824679F8  StartPubGame(name, variation):     -- mgr+8 = name, mgr+12 = variation,
//                 refuses while mgr+52 is non-zero      mgr+136 = 0, then state 1
//   sub_82467E40  *(mgr + 52) = state, under the critical section at mgr+68
//   sub_82467EF8  mgr+124 = statsComponent + 0x500   -- `addi r3, r11, 0x500 ; stw r3, 0x7C(r29)`
//                 and seeds the profile's bank from the hero's real gold
//   sub_82EE8640  *(profile + 264) = gold  (0x108)   -- and sets the dirty byte at +868 (0x364)
//   sub_82EE8810  *(profile + 304) = debt  (0x130)
//
// THE MOVIE IS THE ONLY PART GODOT REPLACES. The guest's tick (sub_821987B8) case 1 picks the
// loading screen, snapshots the language, the pad, the camera and the hero's gold, seeds the
// profile's bank from it, then moves to state 2. The render-thread pump (sub_82467B08) would then
// create the GFx module and run game.swf through states 2-8; once the host claims the games,
// src/vfs/src/hooks/pubgame_pump.cpp keeps that pump's transitions and its sound emitter and
// leaves the movie out. Godot's game plays while the guest sits in 3, and each fscommand it
// issues is handed to the branch of the guest's handler (sub_82EF2AC0) that would have taken it
// -- holdings, points, rank, sounds, volumes, and GameOver, which moves to 6 and then the settle.
//
// AND THE SETTLEMENT STAYS THE GUEST'S. sub_824684F8 credits the real gold by
// (profile gold now - mgr+96), nets the debt against mgr+100, updates the per-game statistics,
// applies PurityBalancing, gossips MiniGamePlayedGood/Bad, awards the achievement and raises
// "FinishedPubGame". Reimplementing any of that host-side would be a second, disagreeing copy of
// rules the binary already states.
//
// THE NAME IS TWO DEREFERENCES FROM mgr+8, not one. sub_82275438 allocates a 16-byte string
// object and stores the POINTER at the destination, so mgr+8 holds `object*` and `object+0` holds
// the `char*`. Reading one level in gets a plausible-looking pointer that is not text.

namespace {
constexpr refii::guest::Addr kPubGameManager{0x834C20B8u};


constexpr uint32_t kPubGameNameOffset = 8u;
constexpr uint32_t kPubGameVariationOffset = 12u;
constexpr uint32_t kPubGameStateOffset = 52u;
constexpr uint32_t kPubGameProfileOffset = 124u;

constexpr uint32_t kPubProfileGoldOffset = 0x108u;
constexpr uint32_t kPubProfileDebtOffset = 0x130u;
}  // namespace


REFII_HOST_ABI_EXPORT int RefiiHostGetPubGame(uint32_t* out,
                                              char* name,
                                              uint32_t name_capacity,
                                              char* variation,
                                              uint32_t variation_capacity) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 6; ++i) {
        out[i] = 0;
    }

    if (name != nullptr && name_capacity > 0) {
        name[0] = '\0';
    }

    if (variation != nullptr && variation_capacity > 0) {
        variation[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t state = read32(kPubGameManager + kPubGameStateOffset);
    const uint32_t profile = read32(kPubGameManager + kPubGameProfileOffset);

    out[0] = state;
    out[1] = plausible(profile) ? profile : 0u;

    if (out[1] != 0u) {
        // BIG ENDIAN, so the HIGH word is at the lower address. Reading these the other way round
        // turns 500 gold into 2,147,483,648 and the settlement would credit the difference.
        out[2] = read32(profile + kPubProfileGoldOffset);
        out[3] = read32(profile + kPubProfileGoldOffset + 4u);
        out[4] = read32(profile + kPubProfileDebtOffset);
        out[5] = read32(profile + kPubProfileDebtOffset + 4u);
    }

    const auto read_string = [&](uint32_t at, char* into, uint32_t capacity) {
        if (into == nullptr || capacity == 0) {
            return;
        }

        const uint32_t object = read32(at);
        if (!plausible(object)) {
            return;
        }

        const uint32_t text = read32(object);
        if (!plausible(text)) {
            return;
        }

        uint32_t i = 0;
        for (; i + 1 < capacity; ++i) {
            const char c = static_cast<char>(GuestByteAt(base, text + i));
            if (c == '\0') {
                break;
            }

            into[i] = c;
        }

        into[i] = '\0';
    };

    read_string(kPubGameManager + kPubGameNameOffset, name, name_capacity);
    read_string(kPubGameManager + kPubGameVariationOffset, variation, variation_capacity);
    return 1;
}

/// Godot plays the pub games: the guest's pump stops creating the Scaleform module and hands
/// the session to the host instead. See src/vfs/src/hooks/pubgame_pump.cpp.
REFII_HOST_ABI_EXPORT int RefiiHostClaimPubGames(int claimed) {
    refii::vfs::ClaimPubGames(claimed != 0);
    return 1;
}

/// One fscommand from Godot's game. Kinds and argument slots are HostBridge.PubGameCommand;
/// the pump hands each to the branch of the guest's fscommand handler that would have taken it.
REFII_HOST_ABI_EXPORT int RefiiHostPostPubGameCommand(uint32_t kind,
                                                      const char* text,
                                                      double n0,
                                                      double n1,
                                                      double n2,
                                                      int64_t i0,
                                                      int64_t i1) {
    const double numbers[3] = {n0, n1, n2};
    const int64_t integers[2] = {i0, i1};
    refii::vfs::PostPubGameCommand(kind, text, numbers, integers);
    return 1;
}

/// What the pump told the movie since the last call; bits are HostBridge.PubGameEvent.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakePubGameEvents(void) {
    return refii::vfs::TakePubGameEvents();
}

/// What `sub_82EF18A0` hands the movie before `pubGame.Run`, read from the same places.
///
/// Fills 14 words: rank (int64 +0x110, high word first), the three per-game point slots
/// (int64 +0x118 + 8*i, `sub_82EE8770`), the tournament item bits (+0x35C, `sub_82EE88F0`), the
/// pad (mgr+116), the sound and music volumes as float bits (mgr+128/+132, 0..1), and the gold
/// and debt the session started from (mgr+96/+100). The name is the profile's UTF-16 string at
/// +8 (`sub_82EE8538`, handed over as a GFx StringW), written out as UTF-8; the language is the
/// two letters at mgr+104 after `sub_82467EF8`'s "sk" and missing-file fallbacks.
REFII_HOST_ABI_EXPORT int RefiiHostGetPubGameProfile(uint32_t* out,
                                                     char* name,
                                                     uint32_t name_capacity,
                                                     char* language,
                                                     uint32_t language_capacity) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 14; ++i) {
        out[i] = 0;
    }

    if (name != nullptr && name_capacity > 0) {
        name[0] = '\0';
    }

    if (language != nullptr && language_capacity > 0) {
        language[0] = '\0';
    }

    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };
    const uint32_t profile = read32(kPubGameManager + kPubGameProfileOffset);
    if (profile < 0x10000u || profile >= 0xC0000000u) {
        return 0;
    }

    out[0] = read32(profile + 0x110u);
    out[1] = read32(profile + 0x114u);
    for (uint32_t i = 0; i < 3; ++i) {
        out[2 + (i * 2)] = read32(profile + 0x118u + (i * 8u));
        out[3 + (i * 2)] = read32(profile + 0x11Cu + (i * 8u));
    }

    out[8] = read32(profile + 0x35Cu);
    out[9] = read32(kPubGameManager + 116u);
    out[10] = read32(kPubGameManager + 128u);
    out[11] = read32(kPubGameManager + 132u);
    out[12] = read32(kPubGameManager + 96u);
    out[13] = read32(kPubGameManager + 100u);

    if (name != nullptr && name_capacity > 1) {
        uint32_t written = 0;
        for (uint32_t i = 0; i < 64; ++i) {
            const uint32_t unit = (static_cast<uint32_t>(GuestByteAt(base, profile + 8u + i * 2u)) << 8) |
                                  GuestByteAt(base, profile + 9u + i * 2u);
            if (unit == 0u) {
                break;
            }

            char encoded[3];
            uint32_t length = 0;
            if (unit < 0x80u) {
                encoded[length++] = static_cast<char>(unit);
            } else if (unit < 0x800u) {
                encoded[length++] = static_cast<char>(0xC0u | (unit >> 6));
                encoded[length++] = static_cast<char>(0x80u | (unit & 0x3Fu));
            } else {
                encoded[length++] = static_cast<char>(0xE0u | (unit >> 12));
                encoded[length++] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                encoded[length++] = static_cast<char>(0x80u | (unit & 0x3Fu));
            }

            if (written + length + 1 > name_capacity) {
                break;
            }

            for (uint32_t k = 0; k < length; ++k) {
                name[written++] = encoded[k];
            }
        }

        name[written] = '\0';
    }

    if (language != nullptr && language_capacity > 2) {
        language[0] = static_cast<char>(GuestByteAt(base, kPubGameManager + 104u));
        language[1] = static_cast<char>(GuestByteAt(base, kPubGameManager + 105u));
        language[2] = '\0';
    }

    return 1;
}

/// THE INVENTORY SCREEN'S MODE — 1 Items, 3 Weapons, 4 Clothing, 0 when unknown.
///
/// Screen 15 is not three screens, it is one with a mode word, and the whole list changes with
/// it. `sub_829B0A58` reads it and branches: mode 1 spawns the item headers (`sub_829B1578`),
/// mode 3 the `Melee`/`Ranged` pair (`sub_829B1890`), and the shared row builder
/// `sub_829B1968` then buckets each item by category — `Category == 1 -> 3`, `<= 16 || > 24 ->
/// 1`, else `4` — and keeps only the bucket matching the mode.
///
/// The guest reads it as `*(*sub_8243BD60(guiMgr + 68, &15) + 372)`. `sub_8243BD60` is
/// `std::map<int, Screen*>::operator[]` — it INSERTS a default when the key is absent
/// (`sub_8243C198` at 0x8243BE3C), so it must not be called from here. The descent is
/// reproduced read-only instead: node `+0` left, `+4` parent, `+8` right, `+12` key, `+16`
/// value, `+25` the nil byte, with the head at `map + 4` and the root at `head[1]`.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetInventoryScreenMode(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    const uint32_t manager = plausible(gui) ? read32(gui + 28u) : 0u;
    const uint32_t map = plausible(manager) ? manager + 68u : 0u;
    const uint32_t head = map != 0u ? read32(map + 4u) : 0u;
    if (!plausible(head)) {
        return 0;
    }

    constexpr int32_t kInventoryScreenKey = 15;

    // lower_bound, exactly the loop at 0x8243BD7C — descend left while the node's key is at or
    // above the one wanted, remembering the last such node.
    uint32_t candidate = head;
    uint32_t node = read32(head + 4u);
    for (int guard = 0; guard < 64 && plausible(node); ++guard) {
        if (GuestByteAt(base, node + 25u) != 0u) {
            break;
        }

        const int32_t key =
            static_cast<int32_t>(refii::guest::ScreenIdToTu1(read32(node + 12u)));
        if (key >= kInventoryScreenKey) {
            candidate = node;
            node = read32(node);
        } else {
            node = read32(node + 8u);
        }
    }

    // The guest would insert here; we simply have no answer.
    if (candidate == head ||
        static_cast<int32_t>(refii::guest::ScreenIdToTu1(read32(candidate + 12u))) !=
            kInventoryScreenKey) {
        return 0;
    }

    const uint32_t screen = read32(candidate + 16u);
    if (!plausible(screen)) {
        return 0;
    }

    return read32(screen + 372u);
}

REFII_HOST_ABI_EXPORT int RefiiHostGetInGameScreen(uint32_t* out) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 36; ++i) {
        out[i] = 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui)) {
        return 0;
    }

    out[0] = gui;
    out[1] = read32(gui);

    const uint32_t manager = read32(gui + 56u);
    if (!plausible(manager)) {
        return 0;
    }

    out[2] = manager;

    // NOT RANGE-CHECKED TO 0..29 HERE. 31 is the guest's own "no screen up" and the caller
    // needs to see it; clamping it into the array's range would report the pause menu as open
    // for a player who is simply walking around.
    out[3] = refii::guest::ScreenIdToTu1(read32(manager + 4u));

    // The thirty per-screen slots, ID order. `sub_823DCBE0` indexes them as `a1[id + 3]`, i.e.
    // manager + 0x0C + 4*id, for id < 30.
    //
    // REPORTED BY TU1 ID in both builds. Without the title update there are twenty-nine, keyed
    // by ids one lower from 13 up, and TU1's id 13 has no slot at all -- so each TU1 id is
    // looked up at the running build's own slot and 13 reads as empty there.
    for (uint32_t id = 0; id < 30u; ++id) {
        const uint32_t slot = refii::guest::ScreenManagerSlot(id);
        out[4 + id] = slot != 0u ? read32(manager + slot) : 0u;
    }

    // The shared slot. Screens not in `sub_823DCDA0`'s dedicated list take turns in this one,
    // so a presentation found here belongs to whichever ID currently owns it.
    out[34] = refii::guest::ScreenIdToTu1(read32(manager + refii::guest::ScreenManagerField(0x98u)));
    out[35] = read32(manager + refii::guest::ScreenManagerField(0x9Cu));
    return 1;
}

/// The LIVE attribute values of every element on the active screen.
///
/// This is what a .bgf alone cannot tell you. The shipped file holds the authored
/// attributes and the slides that override them, but the screen's Lua then moves and
/// hides things at runtime: Page.Initialize does `setAttribute(DimensionsImage,
/// "opacity", 0)`, Page.ArrangeItemsVertically writes "position.y" per item, DisplayBox
/// writes "textstring". A host that reads only the file draws all of that where the file
/// put it — which is a dialog panel sitting over the main menu that the game has hidden.
///
/// Layout verified directly, not taken second-hand (sub_822848E0, and sub_82284948 for
/// the value offset):
///
///   base  = (*(u16*)(elem + 28) & 1) ? elem + 68 : elem + 32
///   count = *(u8*)(elem + 31)
///   record = 8 bytes; id = *(u32*)(rec + 0) >> 5; VALUE = *(u32*)(rec + 4)
///
/// `ids` are sdbm hashes of the attribute names (sub_82189140), which is exactly what
/// Engine_GameHash.Sdbm computes — the caller passes them so the hash lives in one place.
///
/// Emits TWO words per (element, id) in walk order: the raw value and 1/0 for present.
/// The value is raw bits; a float attribute is a big-endian float, already swapped here.
/// Returns the number of ELEMENTS walked.
/// Reads named attributes off every element of one presentation.
///
/// <paramref name="requested_presentation"/> is a `CPresentation*` from
/// <see cref="RefiiHostGetPresentations"/>, or 0 for the front end's active screen. Same
/// parameterisation as RefiiHostGetUiElements and for the same reason: the front-end chain
/// this used to hardcode cannot reach the HUD, and `opacity` is exactly the attribute the
/// in-game UI needs — the game hides the whole HUD by writing it to 0 on one group.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiAttributes(uint32_t requested_presentation,
                                  const uint32_t* ids, uint32_t id_count,
                                  uint32_t* out, uint32_t out_capacity) {
    if (ids == nullptr || id_count == 0) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t { return GuestWordAt(base, address); };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    uint32_t presentation = requested_presentation;
    if (presentation == 0u) {
        const uint32_t gui = GuestWordAt(base, refii::guest::Address(0x83496B08u));
        if (!plausible(gui)) {
            return 0;
        }

        const uint32_t active = read32(gui + 188u);
        if (!plausible(active)) {
            return 0;
        }

        presentation = read32(active + 20u);
    }

    if (!plausible(presentation)) {
        return 0;
    }

    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    // One attribute of one element, or "absent".
    auto find = [&](uint32_t element, uint32_t id, uint32_t* value) -> bool {
        const uint16_t flags = static_cast<uint16_t>(read32(element + 28u) >> 16);
        const uint32_t records = (flags & 1u) != 0 ? element + 68u : element + 32u;
        const uint32_t count = GuestByteAt(base, element + 31u);

        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t record = records + i * 8u;
            if ((read32(record) >> 5) == id) {
                *value = read32(record + 4u);
                return true;
            }
        }

        return false;
    };

    constexpr uint32_t kMaxElements = 16384u;

    uint32_t found = 0;
    uint32_t element = root;

    while (element != 0 && found < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        // Each row leads with the element address and its name hash, so the caller can check
        // its ordinal join against the element walk (a separate call the tree may change
        // between).
        const uint32_t stride = 2u + id_count * 2u;
        const uint32_t row = found * stride;
        if (out != nullptr && (row + 1u) < out_capacity) {
            out[row] = element;
            out[row + 1u] = read32(element);  // the element's name hash
        }

        for (uint32_t i = 0; i < id_count; ++i) {
            const uint32_t slot = row + 2u + i * 2u;
            uint32_t value = 0;
            const bool present = find(element, ids[i], &value);
            if (out != nullptr && (slot + 1u) < out_capacity) {
                out[slot] = value;
                out[slot + 1u] = present ? 1u : 0u;
            }
        }

        ++found;

        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        // Bounded: a torn tree whose parent links form a cycle would otherwise hold the
        // caller here for ever.
        int climbed = 0;
        while (element != 0) {
            if (element == root || ++climbed > 16384) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0;
        }
    }

    return found;
}

/// The camera manager, whose block at manager+0x90 is the live camera the guest renders with.
///
/// The guest's chain is three hops past the entry:
///
///   `sub_82264230(*(game+0x9C), key)`  returns *(entry+4)   -- the PLAYER
///   `sub_821F87C8`                     returns *(player+4)  -- the view
///   `sub_821DD3B0` (`lwz r3, 4(r4)`)   takes  *(view+4)     -- the MANAGER
///
/// mode = *(0x83496AB8) -> game = *(mode+0x0C) -> the container *(game+0x9C), whose 12-byte
/// {key, value, _} entries run from *(container+8) to *(container+0x0C); take the one keyed 1
/// (2 on an online joiner, when *(game+0x8C)+0x34 and +0x36 — in a session, the joiner — are both
/// set: the joiner's own player, whose camera manager is the one that machine runs). 0 when the chain is not
/// up yet — the ordinary case out of game, and why the caller keeps the pull cache as a
/// fallback. Every hop is a guarded read, so a half-built chain reads back 0 rather than faults.
static uint32_t resolve_camera_manager(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t mode = u32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return 0;
    }
    const uint32_t game = u32(mode + 0x0Cu);
    if (!plausible(game)) {
        return 0;
    }

    uint32_t key = 1u;
    const uint32_t split = u32(game + 0x8Cu);
    if (plausible(split) && GuestByteAt(base, split + 0x34u) != 0 &&
        GuestByteAt(base, split + 0x36u) != 0) {
        key = 2u;
    }

    const uint32_t container = u32(game + 0x9Cu);
    if (!plausible(container)) {
        return 0;
    }
    const uint32_t begin = u32(container + 0x08u);
    const uint32_t end = u32(container + 0x0Cu);
    // Bounded so a torn read of either pointer cannot walk a runaway range.
    if (!plausible(begin) || end <= begin || (end - begin) > 0x1000u) {
        return 0;
    }

    for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
        if (u32(entry) != key) {
            continue;
        }
        const uint32_t player = u32(entry + 4u);
        if (!plausible(player)) {
            return 0;
        }
        const uint32_t view = u32(player + 4u);
        if (!plausible(view)) {
            return 0;
        }
        const uint32_t manager = u32(view + 4u);
        return plausible(manager) ? manager : 0u;
    }
    return 0;
}

/// Where the guest keeps the two cameras it blends between, and the offset of the live one.
///
/// The guest blends a camera pair as it does bodies: `sub_82278DD0` calls
/// `sub_8228FEF0(dest, game+0x10, live, alpha)` at `0x82278e2c`-`0x82278e40`, and the alpha in
/// `f1` is the same `f28` (the `fdivs` at `0x822771e4`) that `sub_822112D8` gets for every
/// renderable.
///
/// Prev is latched into `game+0x10` by five inlined stores at `0x82277234`-`0x8227725c`, only
/// when a new game tick starts (`beq cr6, loc_82277298` at `0x82277200`) — position to
/// `game+0x10`, the two basis vectors to `+0x20`/`+0x30`, the fov to `+0x40`, the snap byte to
/// `+0x44`.
///
/// Live is what `sub_82265F90` returns: `manager+0x90`. Shipped, an online joiner
/// (`*(game+0x11C) == 1` with `*(game+0x8C)+0x34` and `+0x36` set) got `manager+0x130`, the host's
/// streamed camera; each machine now runs its own and the getter always returns `+0x90`. It is memoised
/// against KeTickCount at `manager+0xD0` (at most once a millisecond), unlike the pull cache at
/// 0x8331A230, which refreshes only when a Lua frustum native runs.
///
/// Both blocks are the same 0x38 layout: pos +0x00, up +0x10, forward +0x20, horizontal fov
/// +0x30, snap byte +0x34. Returns false when the chain is not up.
static bool resolve_camera_pair(const uint8_t* base, uint32_t* out_prev, uint32_t* out_live) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t mode = u32(refii::guest::Address(0x83496AB8u));
    if (!plausible(mode)) {
        return false;
    }
    const uint32_t game = u32(mode + 0x0Cu);
    if (!plausible(game)) {
        return false;
    }

    const uint32_t manager = resolve_camera_manager(base);
    if (!plausible(manager)) {
        return false;
    }

    // The live block is +0x90 on every machine. The guest's getter returned +0x130 — the host's
    // camera, streamed as net message 0xBD — on an online joiner; each machine now runs its own
    // camera and the getter always takes its +0x90 path (coop_hero_hooks.cpp, 0x82265FB0).
    *out_prev = game + 0x10u;
    *out_live = manager + 0x90u;
    return true;
}

/// The guest's camera, for Godot to render through.
///
/// Reads the snapshot at 0x8331A230. Layout is IDA-proven, and two details are not what a
/// reasonable guess would give:
///
///   +0x00  position   16-byte lanes, NOT 12 — three separate stvx128 at 0x8221161C,
///   +0x10  up         0x82211628 and 0x82211630. A packed-12 reader is wrong from the
///   +0x20  forward    second vector on. Only xyz is ever read by the guest (vmsum3fp128).
///   +0x30  HORIZONTAL field of view, radians (the "vertical" reading is wrong —
///          sub_82293820 derives vfov from it at 0x822938A8)
///   +0x34  a boolean whose meaning is UNPROVEN
///
/// World basis is right-handed with X right, Y FORWARD, Z up — proven from sub_821B0068
/// building [cross(fwd,up), fwd, up] as a proper rotation, and from the defaults
/// (fwd = +Y, up = +Z).
///
/// It is a PULL CACHE: sub_82211598 rewrites it only when the sim generation counter
/// dword_83319650 has moved. Whether any of its callers still run headless was the open
/// question; it is now SETTLED and the answer is yes. All ten are Lua natives
/// ("IsPositionInCameraFrustrum", "IsSphereInCameraFrustrum",
/// "IsEntityInCameraFrustrumWithMultiplier"), Kynapse path-object distance gates, effect
/// ticks and animation-LOD classifiers. None contains a device call, so none of them needs
/// the render path. dword_83319650 is a SIMULATION tick counter — incremented at the tail
/// of sub_821D4628 and sub_823D9898, both world updates — not a present counter.
///
/// It is still a copy of a copy. sub_82211598 reads through sub_821DD3B0 -> sub_82265F90,
/// and that one's own memo at cameraManager+0xD0 is stamped with KeTickCount, so the whole
/// chain refreshes at most once a millisecond. The camera manager's PUSHED block is the
/// better source and is laid out the same way, measured from the CAMERA MANAGER base:
/// +0xE0 position, +0xF0 up, +0x100 forward, +0x110 fov, +0x114 bool. sub_821B8FD8 writes
/// it — stvx128 at 0x821B917C for the position, sub_822BDD10 for up and forward, stfs/stb
/// at 0x821B91DC/0x821B91C4 — and sub_8233B6B0 drives it with force=1 once per sim tick.
/// Resolving the manager needs no guest call: Game = *(0x83496AB8+12), binary-search the
/// 12-byte entries between *(Game+0x9C)+8 and +12 for key 1 (key 2 when *(Game+0x8C)+0x34
/// and +0x36 are both set), take *(entry+4), then *(view+4).
///
/// Fills 8 floats: pos xyz, up xyz... actually 11: pos[3], up[3], fwd[3], hfov, flag.
/// Returns 1 on a coherent read, 0 if the two reads disagreed or the guest is absent.
REFII_HOST_ABI_EXPORT int RefiiHostGetCamera(float* out) {
    if (out == nullptr) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // PREFER THE CAMERA MANAGER'S PUSHED BLOCK. `sub_8233B6B0` writes it force=1 once per SIM
    // tick (manager+0xE0 pos, +0xF0 up, +0x100 fwd, +0x110 hfov, +0x114 flag — same 0x38-byte
    // layout as the cache), so it is live the moment the world is up. The pull cache below only
    // refreshes when a Lua frustum native runs, and in game none has yet, so it reads the default
    // pose and the world renders through nothing. Coherent double-read, same unsynchronised
    // stores as the cache; used only when it resolves to a real forward vector, else fall through.
    // On by default; the live block is the one `sub_82265F90` returns (`manager+0x90`,
    // `manager+0x130` split screen). `REFII_CAMERA_PUSHED_BLOCK=0` falls back to the pull cache,
    // and `REFII_CAMERA_DEBUG=1` dumps the walk and a float window.
    static const bool use_pushed_camera = [] {
        const char* value = std::getenv("REFII_CAMERA_PUSHED_BLOCK");
        return value == nullptr || value[0] != '0';
    }();
    uint32_t prev_block = 0;
    uint32_t live_block = 0;
    const bool pair_up = use_pushed_camera && resolve_camera_pair(base, &prev_block, &live_block);
    const uint32_t manager = pair_up ? live_block : 0u;
    if (manager != 0u) {
        static const bool debug_camera = [] {
            const char* value = std::getenv("REFII_CAMERA_DEBUG");
            return value != nullptr && value[0] == '1';
        }();
        if (debug_camera) {
            static std::atomic<uint32_t> dumped{0};
            if (dumped.fetch_add(1, std::memory_order_relaxed) < 3u) {
                const auto f = [base](uint32_t at) {
                    uint32_t bits = __builtin_bswap32(GuestWordAt(base, at));
                    float v = 0.0f;
                    std::memcpy(&v, &bits, sizeof(v));
                    return v;
                };
                std::string floats;
                for (uint32_t off = 0u; off <= 0x38u; off += 4u) {
                    char buf[32];
                    std::snprintf(buf, sizeof(buf), "%02X:%.3f ", off, f(manager + off));
                    floats += buf;
                }
                REXLOG_INFO("Refii camera block 0x{:08X} (prev 0x{:08X}) floats {}", manager,
                            prev_block, floats);
            }
        }

        uint8_t a[0x38];
        uint8_t b[0x38];
        bool coherent = false;
        for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
            CopyGuest(base, manager, a, sizeof(a));
            CopyGuest(base, manager, b, sizeof(b));
            coherent = std::memcmp(a, b, sizeof(a)) == 0;
        }

        if (coherent) {
            const auto rf = [&a](uint32_t offset) {
                uint32_t bits = 0;
                std::memcpy(&bits, a + offset, sizeof(bits));
                bits = __builtin_bswap32(bits);
                float value = 0.0f;
                std::memcpy(&value, &bits, sizeof(value));
                return value;
            };

            float cam[11];
            for (int lane = 0; lane < 3; ++lane) {
                cam[lane] = rf(0x00u + lane * 4u);
                cam[3 + lane] = rf(0x10u + lane * 4u);
                cam[6 + lane] = rf(0x20u + lane * 4u);
            }
            cam[9] = rf(0x30u);
            cam[10] = a[0x34u] != 0 ? 1.0f : 0.0f;

            bool finite = true;
            for (int i = 0; i < 10; ++i) {
                finite = finite && cam[i] == cam[i] && cam[i] > -1e18f && cam[i] < 1e18f;
            }
            const float fwd2 = cam[6] * cam[6] + cam[7] * cam[7] + cam[8] * cam[8];
            if (finite && fwd2 > 1e-4f) {
                for (int i = 0; i < 11; ++i) {
                    out[i] = cam[i];
                }
                static std::atomic<uint32_t> logged{0};
                const uint32_t n = logged.fetch_add(1, std::memory_order_relaxed);
                if (n < 8u || (n % 256u) == 0u) {
                    REXLOG_INFO("Refii host camera [pushed] mgr=0x{:08X} pos=({:.2f},{:.2f},"
                                "{:.2f}) fwd=({:.2f},{:.2f},{:.2f}) hfov={:.3f}",
                                manager, cam[0], cam[1], cam[2], cam[6], cam[7], cam[8], cam[9]);
                }
                return 1;
            }
        }
    }

    const uint32_t kCamera = refii::guest::Address(0x8331A230u);

    // Five unsynchronised stores with no barrier and no sequence counter, and the
    // "cached" memo is published BEFORE the payload (0x82211600, ahead of the vectors at
    // 0x8221161C onward) — so watching that memo for freshness is the worst available
    // signal. Reading twice and requiring agreement is the cheapest correct thing: the
    // window is nine instructions wide, so a retry almost never happens.
    auto snapshot = [&](uint8_t* into) {
        CopyGuest(base, kCamera, into, 0x38u);
    };

    uint8_t first[0x38];
    uint8_t second[0x38];
    bool coherent = false;
    for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
        snapshot(first);
        snapshot(second);
        coherent = std::memcmp(first, second, sizeof(first)) == 0;
    }

    if (!coherent) {
        return 0;
    }

    auto read_float = [&](uint32_t offset) {
        uint32_t bits = 0;
        std::memcpy(&bits, first + offset, sizeof(bits));
        bits = __builtin_bswap32(bits);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    for (int lane = 0; lane < 3; ++lane) {
        out[lane] = read_float(0x00u + lane * 4u);       // position
        out[3 + lane] = read_float(0x10u + lane * 4u);   // up
        out[6 + lane] = read_float(0x20u + lane * 4u);   // forward
    }

    out[9] = read_float(0x30u);          // horizontal FOV, radians
    out[10] = first[0x34u] != 0 ? 1.0f : 0.0f;
    return 1;
}

/// The two cameras the guest blends between, so the host blends them by the same alpha it
/// uses for bodies (`sub_8228FEF0` and `sub_822112D8` take the same alpha register).
///
/// Fills 22 floats: live pos[3], up[3], forward[3], horizontal fov, snap flag, then the same
/// eleven for the previous camera. The snap flag is the guest's override: `lbz r11, 0x34(r30)`
/// on the live block at `0x8228ff40` forces alpha to 1.0 (a cut). Returns 1 when both blocks
/// read coherently, 0 otherwise, and the caller falls back to `RefiiHostGetCamera`.
/// The camera manager of the player keyed `key` in the guest's player map (1 or 2), or 0 — the
/// same hops as resolve_camera_manager with the key stated rather than chosen.
static uint32_t resolve_player_camera_manager(const uint8_t* base, uint32_t key) {
    const auto u32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };
    const uint32_t mode = u32(refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(mode) ? u32(mode + 0x0Cu) : 0u;
    const uint32_t container = plausible(game) ? u32(game + 0x9Cu) : 0u;
    if (!plausible(container)) {
        return 0;
    }
    const uint32_t begin = u32(container + 0x08u);
    const uint32_t end = u32(container + 0x0Cu);
    if (!plausible(begin) || end <= begin || (end - begin) > 0x1000u) {
        return 0;
    }
    for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
        if (u32(entry) != key) {
            continue;
        }
        const uint32_t player = u32(entry + 4u);
        const uint32_t view = plausible(player) ? u32(player + 4u) : 0u;
        const uint32_t manager = plausible(view) ? u32(view + 4u) : 0u;
        return plausible(manager) ? manager : 0u;
    }
    return 0;
}

/// One player's own live camera — `manager+0x90` of the camera manager their Player owns — as
/// 11 floats (position, up, forward, horizontal fov, snap), or 0 when that player has no camera
/// manager or the block is not a camera yet. Split screen draws player two's view from this.
REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerCamera(int key, float* out) {
    if (out == nullptr || (key != 1 && key != 2)) {
        return 0;
    }
    auto* memory = SafeKernelMemory();
    uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
    if (base == nullptr) {
        return 0;
    }
    const uint32_t manager = resolve_player_camera_manager(base, static_cast<uint32_t>(key));
    if (manager == 0u) {
        return 0;
    }
    const uint32_t live_at = manager + 0x90u;
    uint8_t first[0x38];
    uint8_t second[0x38];
    bool coherent = false;
    for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
        CopyGuest(base, live_at, first, sizeof(first));
        CopyGuest(base, live_at, second, sizeof(second));
        coherent = std::memcmp(first, second, sizeof(first)) == 0;
    }
    if (!coherent) {
        return 0;
    }
    const auto read_float = [&first](uint32_t offset) {
        uint32_t bits = 0;
        std::memcpy(&bits, first + offset, sizeof(bits));
        bits = __builtin_bswap32(bits);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };
    for (uint32_t lane = 0; lane < 3u; ++lane) {
        out[lane] = read_float(0x00u + lane * 4u);
        out[3u + lane] = read_float(0x10u + lane * 4u);
        out[6u + lane] = read_float(0x20u + lane * 4u);
    }
    out[9] = read_float(0x30u);
    out[10] = first[0x34u] != 0 ? 1.0f : 0.0f;
    const float forward2 = out[6] * out[6] + out[7] * out[7] + out[8] * out[8];
    return forward2 > 1e-4f ? 1 : 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetCameraPair(float* out) {
    if (out == nullptr) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t prev_at = 0;
    uint32_t live_at = 0;
    if (!resolve_camera_pair(base, &prev_at, &live_at)) {
        return 0;
    }

    // Unsynchronised stores with no sequence counter, same as the pull cache: read twice and
    // require agreement. Both blocks in one pass, so a tick boundary between them cannot hand
    // back a prev from one tick and a live from another.
    uint8_t first[0x70];
    uint8_t second[0x70];
    bool coherent = false;
    for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
        CopyGuest(base, live_at, first, 0x38u);
        CopyGuest(base, prev_at, first + 0x38u, 0x38u);
        CopyGuest(base, live_at, second, 0x38u);
        CopyGuest(base, prev_at, second + 0x38u, 0x38u);
        coherent = std::memcmp(first, second, sizeof(first)) == 0;
    }

    if (!coherent) {
        return 0;
    }

    const auto read_float = [&first](uint32_t offset) {
        uint32_t bits = 0;
        std::memcpy(&bits, first + offset, sizeof(bits));
        bits = __builtin_bswap32(bits);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    for (uint32_t half = 0; half < 2u; ++half) {
        const uint32_t at = half * 0x38u;
        const uint32_t into = half * 11u;
        for (uint32_t lane = 0; lane < 3u; ++lane) {
            out[into + lane] = read_float(at + 0x00u + lane * 4u);       // position
            out[into + 3u + lane] = read_float(at + 0x10u + lane * 4u);  // up
            out[into + 6u + lane] = read_float(at + 0x20u + lane * 4u);  // forward
        }
        out[into + 9u] = read_float(at + 0x30u);                          // horizontal FOV
        out[into + 10u] = first[at + 0x34u] != 0 ? 1.0f : 0.0f;           // snap
    }

    // A forward vector is the cheapest thing that tells a real camera from a zeroed block, and
    // the previous one is zero for the whole first tick of a level.
    const float live2 = out[6] * out[6] + out[7] * out[7] + out[8] * out[8];
    const float prev2 = out[17] * out[17] + out[18] * out[18] + out[19] * out[19];
    if (!(live2 > 1e-4f)) {
        return 0;
    }
    if (!(prev2 > 1e-4f)) {
        // Nothing latched yet: hand the live camera back as both halves so the caller blends
        // between identical endpoints rather than through the origin.
        for (uint32_t i = 0; i < 11u; ++i) {
            out[11u + i] = out[i];
        }
    }

    for (uint32_t i = 0; i < 22u; ++i) {
        if (!(out[i] == out[i]) || out[i] < -1e18f || out[i] > 1e18f) {
            return 0;
        }
    }

    return 1;
}

/// The sound of the movie the guest last opened, as the guest stated it.
///
/// `out_sound` is the opener's `sound` argument: 0 is a movie the guest plays silent (the
/// ability previews, `PlaySilentMovie`). `out_ids` are Bink track IDS, not file indices — an
/// id the file does not carry is dropped, which is what Bink does with it. `out_volumes` are
/// linear 0..1 and can change while the movie plays. Returns the track count written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMovieSound(uint32_t* out_sound, uint32_t* out_ids,
                                                      float* out_volumes, uint32_t capacity) {
    return refii::vfs::RequestedBinkSound(out_sound, out_ids, out_volumes, capacity);
}

/// Whether the guest still wants the movie the host is showing.
///
/// 0 none, 1 still wanted, 2 the guest has ended it — by finishing its own decode or by
/// skipping, which is what a button press at the title does. The host stops on 2 rather
/// than playing to its own end, or it keeps a finished movie over whatever the guest has
/// moved on to.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostMovieState(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::vfs::BinkMovieState(memory->virtual_membase());
}

/// The chosen co-op hero save's name, or 0 bytes when none is chosen.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopHeroSaveName(char* out, uint32_t capacity) {
    if (out != nullptr && capacity != 0) out[0] = '\0';
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
    const uint32_t saves = plausible(game) ? GuestWordAt(base, game + 148u) : 0u;
    if (!plausible(saves)) return 0;
    const std::string name = ReadGuestString(base, saves + 4u);
    WriteOut(name, out, capacity);
    return static_cast<uint32_t>(name.size());
}

/// The chosen co-op hero save, as the guest holds it. `SaveMgr = *(*(0x83496AB8)+12)+148`.
/// `out_values` takes 28: money, gender, morality, purity, renown, 15 ranks, 4 experience
/// pools, 4 totals — in `sub_82440F78`'s order. Returns 1 when a save is chosen; the block is
/// only meaningful then.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopHeroSave(float* out_values, uint32_t value_count) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };
    const auto put = [&](uint32_t slot, float value) {
        if (out_values != nullptr && slot < value_count) out_values[slot] = value;
    };

    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
    const uint32_t saves = plausible(game) ? GuestWordAt(base, game + 148u) : 0u;
    if (!plausible(saves)) return 0;

    // The chosen name: an LHString whose data word sits at +4 and whose length is at +4 of
    // that — `sub_8243AE90` at 0x8243AEB8..0x8243AEE4.
    const uint32_t name = GuestWordAt(base, saves + 4u);
    if (!plausible(name) || GuestWordAt(base, name + 4u) == 0u) return 0;

    const uint32_t block = saves + 1016u;
    put(0, static_cast<float>(static_cast<int32_t>(GuestWordAt(base, block + 0u))));
    put(1, static_cast<float>(GuestWordAt(base, block + 4u)));
    put(2, read_float(block + 8u));
    put(3, read_float(block + 12u));
    put(4, static_cast<float>(static_cast<int32_t>(GuestWordAt(base, block + 16u))));
    for (uint32_t i = 0; i < 15u; ++i) {
        put(5u + i, static_cast<float>(static_cast<int32_t>(GuestWordAt(base, block + 20u + 4u * i))));
    }
    const uint32_t pools = GuestWordAt(base, block + 84u);
    const uint32_t pools_end = GuestWordAt(base, block + 88u);
    const uint32_t totals = GuestWordAt(base, block + 100u);
    for (uint32_t i = 0; i < 4u; ++i) {
        const bool have = plausible(pools) && pools + 4u * (i + 1u) <= pools_end;
        put(20u + i, have ? read_float(pools + 4u * i) : 0.0f);
        put(24u + i, have && plausible(totals) ? read_float(totals + 4u * i) : 0.0f);
    }
    return 1;
}

/// The hero's looks that no other export carries. Returns 1 when the hero has component 57.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroLooks(uint32_t* out_face, float* out_fat,
                                                     float* out_height, float* out_tan) {
    if (out_face != nullptr) *out_face = 0;
    if (out_fat != nullptr) *out_fat = 0.0f;
    if (out_height != nullptr) *out_height = 0.0f;
    if (out_tan != nullptr) *out_tan = 0.0f;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };
    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t stats = hero != 0u ? EntityComponent(base, hero, 57u) : 0u;
    const uint32_t morph = hero != 0u ? EntityComponent(base, hero, 17u) : 0u;
    if (stats == 0u) return 0;
    if (out_face != nullptr && morph != 0u) *out_face = GuestWordAt(base, morph + 180u);
    if (out_fat != nullptr) *out_fat = read_float(stats + 56u);
    if (out_height != nullptr) *out_height = read_float(stats + 60u);
    if (out_tan != nullptr) *out_tan = read_float(stats + 52u);
    return 1;
}

/// The hero's equipped weapons as record ids: `sub_824AD108` stores the weapon entity's `+28`
/// into `comp28+84` (melee, 0x824AD264) or `+88` (ranged, 0x824AD400), and a live read
/// confirmed the slot holds a bare id, not a pointer. 0 for an empty slot. Returns 1 when the
/// hero has a weapon set at all.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroWeapons(uint32_t* out_melee, uint32_t* out_ranged) {
    if (out_melee != nullptr) *out_melee = 0;
    if (out_ranged != nullptr) *out_ranged = 0;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const uint32_t hero = ResolvePlayerHero(base);
    const uint32_t weapons = hero != 0u ? EntityComponent(base, hero, 28u) : 0u;
    if (weapons == 0u) return 0;

    if (out_melee != nullptr) *out_melee = GuestWordAt(base, weapons + 84u);
    if (out_ranged != nullptr) *out_ranged = GuestWordAt(base, weapons + 88u);
    return 1;
}

/// The item entities a player holds, as net ids (ABI 135, 64-bit since 170). Component 24
/// entries of 12 bytes at `+16..+20`, holder at entry+8, item = `*holder` (`sub_824FB4E8`).
/// An item is melee or ranged by the guest's own test (`sub_824AD108` at 0x824AD254): component
/// 179's `+12` through `sub_82561420` — 2..4 melee, 5..8 ranged. Component 28's `+84`/`+88` are
/// NOT the key: on a body the client controls, the client's weapon-set message (`sub_824AB0A0`,
/// 0x824AB0BC..) zeroes both and refills them with entity id words, so on the host player two's
/// read 0/0 while both weapons hung on the body (2026-10-08) and the joiner was never told which
/// replicas to attach. The first held item of each class answers. The net
/// id is the entity's 64-bit uid, high word `entity+20`, low word `entity+24` — `sub_821B2390`
/// returns `(entity+0x14 << 32) | entity+0x18` on the host, and `sub_82334A60(manager, id)`
/// takes that whole value in r4. An entity made on the host carries its creation counter in the
/// HIGH word (`sub_8238B9C8`: `{counter, 0}`), so the low word alone reads as zero and names
/// nothing. Returns how many of the two were found.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopHeldUids(uint32_t player, uint64_t* out_melee,
                                                        uint64_t* out_ranged) {
    if (out_melee != nullptr) *out_melee = 0u;
    if (out_ranged != nullptr) *out_ranged = 0u;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0u;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0u;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const uint32_t entity = PlayerEntityForKey(base, player);
    if (entity == 0u) return 0u;
    const uint32_t held = EntityComponent(base, entity, 24u);
    if (held == 0u) return 0u;
    const uint32_t begin = GuestWordAt(base, held + 16u);
    const uint32_t end = GuestWordAt(base, held + 20u);
    if (!plausible(begin) || end < begin || (end - begin) > 12u * 64u) return 0u;
    uint32_t found = 0u;
    for (uint32_t at = begin; at + 12u <= end; at += 12u) {
        const uint32_t holder = GuestWordAt(base, at + 8u);
        if (!plausible(holder)) continue;
        const uint32_t item = GuestWordAt(base, holder);
        if (!plausible(item) || (GuestByteAt(base, item + 144u) & 0x40u) == 0u) continue;
        const uint32_t kind = EntityComponent(base, item, 179u);
        if (kind == 0u) continue;
        const uint32_t type = GuestWordAt(base, kind + 12u);
        const bool melee = type >= 2u && type <= 4u;
        const bool ranged = type >= 5u && type <= 8u;
        const uint64_t net = (static_cast<uint64_t>(GuestWordAt(base, item + 20u)) << 32) |
                             GuestWordAt(base, item + 24u);
        if (melee && out_melee != nullptr && *out_melee == 0u) {
            *out_melee = net;
            ++found;
        } else if (ranged && out_ranged != nullptr && *out_ranged == 0u) {
            *out_ranged = net;
            ++found;
        }
    }
    return found;
}

namespace {
/// The pad bound to player-map key 1 or 2, or 0xFFFFFFFF — `Player+0x28`, stored by the
/// Player ctor `sub_82389938` (`stw r5, 0x28(r31)` at 0x8238998c) from the joining pad. The
/// map is the only source of the pad; the ENTITY for a key is `PlayerEntityForKey`'s.
uint32_t PlayerPadForKey(const uint8_t* base, uint32_t wanted) {
    const uint32_t record = PlayerMapRecordForKey(base, wanted);
    return record == 0u ? 0xFFFFFFFFu : GuestWordAt(base, record + 0x28u);
}
}  // namespace


REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopSession(uint32_t* out_words, uint32_t capacity) {
    // [0] manager present, [1] session, [2] host, [3] client, [4] lost, [5] client joined,
    // [6] peer count, [7] player one entity, [8] player two entity, [9] game+140 copy present,
    // [10] game+284 — 0 front end, 1 game, 2 LOCAL co-op (the guest's own `IsLocalCoopGame`,
    // `sub_82340300`; set by `Coop_SetSharedScreenMode` 0x8233c34c), [11] player two's pad or
    // 0xFFFFFFFF. Words 10-11 are written only for a caller that asks for them.
    constexpr uint32_t kWords = 10u;
    constexpr uint32_t kLocalWords = 12u;
    if (out_words == nullptr || capacity < kWords) return 0;
    const uint32_t written = capacity >= kLocalWords ? kLocalWords : kWords;
    for (uint32_t i = 0; i < written; ++i) out_words[i] = 0u;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t manager = GuestWordAt(base, refii::guest::Address(0x83497490u));
    if (plausible(manager)) {
        out_words[0] = 1u;
        out_words[1] = GuestByteAt(base, manager + 52u);
        out_words[2] = GuestByteAt(base, manager + 53u);
        out_words[3] = GuestByteAt(base, manager + 54u);
        out_words[4] = GuestByteAt(base, manager + 238u);
        out_words[5] = GuestByteAt(base, manager + 221u);
        out_words[6] = GuestWordAt(base, manager + 100u);
    }
    out_words[7] = PlayerEntityForKey(base, 1u);
    out_words[8] = PlayerEntityForKey(base, 2u);
    const uint32_t root = GuestWordAt(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? GuestWordAt(base, root + 12u) : 0u;
    out_words[9] = plausible(game) && plausible(GuestWordAt(base, game + 140u)) ? 1u : 0u;
    if (written == kLocalWords) {
        out_words[10] = plausible(game) ? GuestWordAt(base, game + 284u) : 0u;
        out_words[11] = PlayerPadForKey(base, 2u);
    }
    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopPlayerEntities(uint32_t* out_one, uint32_t* out_two) {
    if (out_one != nullptr) *out_one = 0;
    if (out_two != nullptr) *out_two = 0;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    if (out_one != nullptr) *out_one = PlayerEntityForKey(base, 1u);
    if (out_two != nullptr) *out_two = PlayerEntityForKey(base, 2u);
    return 1;
}

/// The clothing screen's mannequin — the `CreatureHeroMannequin*` screen 16 spawns at open and
/// dresses on a press (and, in the remaster, on `Inventory:tryon`). 0 when the screen is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClothingMannequin(void) {
    return refii::vfs::ClothingMannequinSnapshot();
}

/// The co-op screen's handler, off the same screen map the preview export walks, or 0 when screen
/// 24 is not the current screen. `step` is the walk step that refused, for a once-per-cause log.
static uint32_t CoopScreenHandler(uint8_t* base, int* step, uint32_t* detail) {
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto refuse = [&](int at, uint32_t why) {
        *step = at;
        *detail = why;
        return 0u;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return refuse(1, gui);
    const uint32_t manager = read32(gui + 56u);
    const uint32_t owner = read32(gui + 28u);
    if (!plausible(manager) || !plausible(owner)) return refuse(2, manager);
    if (refii::guest::ScreenIdToTu1(read32(manager + 4u)) != 24u) return refuse(3, read32(manager + 4u));

    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) return refuse(4, end_node);
    uint32_t node = read32(end_node + 4u);
    uint32_t handler = 0;
    for (uint32_t walk = 0; walk < 64u && plausible(node) && node != end_node; ++walk) {
        const uint32_t key = refii::guest::ScreenIdToTu1(read32(node + 12u));
        if (key == 24u) { handler = read32(node + 16u); break; }
        node = read32(node + (24u < key ? 0u : 8u));
    }
    if (!plausible(handler)) return refuse(5, handler);
    *step = 0;
    *detail = handler;
    return handler;
}

/// Both co-op screen figures, as words: [0] player two's henchman entity (`screen+276`), [1] its
/// preview ticket (`screen+284`), [2] player one's mannequin entity, [3] its ticket
/// (`screen+280`). The original spawns player one's figure itself — `sub_82436548` calls
/// `sub_826D7650(previewManager, sub_8222C1B8(players[0]), "LeftPage.model_proxy.model")`
/// (0x82436A2C), a `CreatureHeroMannequin*` dressed from the hero by component 192 — and keeps
/// only the ticket, so the entity is found in the preview manager `*(GUI+8)`: records
/// `*(pm+56)..*(pm+60)`, stride 24, `+4` entity holder, `+16` ticket. A word is 0 for a figure not
/// standing. Returns the words written, 0 when the screen is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopFigures(uint32_t* out_words, uint32_t capacity) {
    constexpr uint32_t kWords = 4u;
    if (out_words == nullptr || capacity < kWords) return 0;
    for (uint32_t i = 0; i < kWords; ++i) out_words[i] = 0u;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    int step = 0;
    uint32_t detail = 0;
    const uint32_t handler = CoopScreenHandler(base, &step, &detail);
    if (handler == 0u) return 0;

    const uint32_t henchman = read32(handler + 276u);
    if (plausible(henchman)) {
        out_words[0] = henchman;
        out_words[1] = read32(handler + 284u);
    }

    const uint32_t ticket = read32(handler + 280u);
    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    const uint32_t previews = plausible(gui) ? read32(gui + 8u) : 0u;
    if (ticket != 0u && plausible(previews)) {
        const uint32_t begin = read32(previews + 56u);
        const uint32_t end = read32(previews + 60u);
        if (plausible(begin) && end >= begin && end - begin <= 24u * 64u) {
            for (uint32_t record = begin; record + 24u <= end; record += 24u) {
                if (read32(record + 16u) != ticket) continue;
                const uint32_t holder = read32(record + 4u);
                const uint32_t entity = plausible(holder) ? read32(holder) : 0u;
                if (plausible(entity)) {
                    out_words[2] = entity;
                    out_words[3] = ticket;
                }
                break;
            }
        }
    }
    return kWords;
}

/// The co-op screen's state, as words, for a host that draws the screen: [0] the left page's
/// focused row (`+44`), [1] the left page accepted (`+48`), [2] the right page's focused row
/// (`+156`), [3] the right page accepted (`+160`), [4] the save picker open (`+304`), [5] its
/// cursor (`+392`), [6] its base card slot (`+396`), [7] the saves listed (`+404..+408`), [8] a
/// bit per save whose header parsed (`*(+452) + 40i + 24`; A is refused on the others at
/// 0x82438DB4), [9] closing (`+268`). Verified against `sub_82434DD8` (the two row maps and
/// their focus/accept words), `sub_8243B070` (base slot by count, the header walk) and
/// `sub_82439738` (the cursor and its `min(count, 6)` bound).
///
/// The picker's card text is written to `text` as NUL-separated UTF-8 in save order — the
/// finished strings `sub_8243B678` builds into `+436` (display name, level, context line, or the
/// corrupt-save warning), the same LHStrings `sub_8243B618` puts on `loadgame.SaveGameDescription`.
/// Returns the words written, 0 when screen 24 is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopScreen(uint32_t* out_words, uint32_t capacity, char* text,
                                                      uint32_t text_capacity) {
    constexpr uint32_t kWords = 10u;
    if (text != nullptr && text_capacity > 0u) text[0] = '\0';
    if (out_words == nullptr || capacity < kWords) return 0;
    for (uint32_t i = 0; i < kWords; ++i) out_words[i] = 0u;
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;
    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    int step = 0;
    uint32_t detail = 0;
    const uint32_t handler = CoopScreenHandler(base, &step, &detail);
    if (handler == 0u) return 0;

    out_words[0] = read32(handler + 44u);
    out_words[1] = GuestByteAt(base, handler + 48u);
    out_words[2] = read32(handler + 156u);
    out_words[3] = GuestByteAt(base, handler + 160u);
    out_words[4] = GuestByteAt(base, handler + 304u);
    out_words[5] = read32(handler + 392u);
    out_words[6] = read32(handler + 396u);
    out_words[9] = GuestByteAt(base, handler + 268u);

    const uint32_t files_begin = read32(handler + 404u);
    const uint32_t files_end = read32(handler + 408u);
    const uint32_t count = plausible(files_begin) && files_end >= files_begin &&
                                   files_end - files_begin < 0x1000u
                               ? (files_end - files_begin) / 4u
                               : 0u;
    out_words[7] = count;

    const uint32_t headers = read32(handler + 452u);
    if (plausible(headers)) {
        for (uint32_t i = 0; i < count && i < 32u; ++i) {
            if (GuestByteAt(base, headers + (40u * i) + 24u) != 0u) out_words[8] |= 1u << i;
        }
    }

    if (text != nullptr && text_capacity > 0u) {
        const uint32_t text_begin = read32(handler + 436u);
        const uint32_t text_end = read32(handler + 440u);
        const uint32_t described = plausible(text_begin) && text_end >= text_begin &&
                                           text_end - text_begin < 0x1000u
                                       ? (text_end - text_begin) / 4u
                                       : 0u;
        uint32_t written = 0;
        for (uint32_t i = 0; i < count; ++i) {
            written += write_guest_wide_string(
                base, i < described ? text_begin + (i * 4u) : 0u, text + written,
                text_capacity > written ? text_capacity - written : 0u);
        }
    }
    return kWords;
}

/// The 3D PREVIEW OBJECT the current in-game screen shows — the spinning weapon or item.
///
/// There is no mesh element: the ring's interior is a plain transform anchor and the guest
/// spawns a real scene entity at it. Everything needed to draw it is in a 128-byte plain
/// struct (no vptr) built by `sub_82536B68`, at a fixed offset in the screen handler. Six
/// screens have one:
///
///   id  8 receive-items   handler+96    anchor "Foreground.SpinningCube.Cube"
///   id 15 items/weapons   handler+128   anchor "Foreground.Book.Pages.Page1.Contents.
///   id 22 shop            handler+160          ModelHider.model_proxy.model"
///   id 25 gift giving     handler+128
///   id 26 item selection  handler+128
///   id 27 decoration      handler+128
///
/// Fields from previewObj: +0 the previewed entity, **+64 the SPAWNED byte**, +68 render id,
/// +80 GUIScreenTranslationOffset, +96 GUIScreenRotationOffset, +112 GUIScreenScaleMultiplier,
/// +120 GUIScreenRotationAxis, **+124 the model resource-name hash**.
///
/// "Is something shown" is +64, not +124: the hash is sticky, since `sub_828C3FD8` clears only
/// +64 and sets +68 to -1. The hash is `rex_lhResourceName_ComputeHash(lowercase(path))` — the
/// currency the model index keys on, not an index.
///
/// The handler comes out of the guest's own screen map at `guiOwner+68` (a std::map keyed by
/// screen id: node +0 left, +4 parent, +8 right, +12 key, +16 value; the end node is at
/// `guiOwner+72`). Walked READ-ONLY and step-bounded rather than calling `sub_8236FFC0`,
/// because this runs on the host thread.
///
/// Returns the screen id whose preview is live, or 0.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiPreviewObject(uint32_t* out_model_hash,
                                                           uint32_t* out_entity,
                                                           float* out_values,
                                                           uint32_t value_count) {
    if (out_model_hash != nullptr) *out_model_hash = 0;
    if (out_entity != nullptr) *out_entity = 0;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
    const auto read_float = [base](uint32_t at) {
        const uint32_t word = GuestWordAt(base, at);
        float value = 0.0f;
        std::memcpy(&value, &word, sizeof(value));
        return value;
    };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;

    const uint32_t manager = read32(gui + 56u);
    const uint32_t owner = read32(gui + 28u);
    if (!plausible(manager) || !plausible(owner)) return 0;

    const uint32_t screen_id = refii::guest::ScreenIdToTu1(read32(manager + 4u));
    uint32_t preview_offset = 0;
    switch (screen_id) {
        case 8u: preview_offset = 96u; break;
        case 22u: preview_offset = 160u; break;
        // Clothing (16) keeps its preview at the same +128 as the gift family: its back
        // handler `sub_826D2560` clears it with `sub_828C3FD8(screen + 128)` at 0x826d280c.
        case 15u: case 16u: case 25u: case 26u: case 27u: preview_offset = 128u; break;
        default: return 0;  // this screen has no preview object
    }

    // The screen -> handler map, descended by key. Bounded: a mid-edit tree must not hang.
    const uint32_t end_node = read32(owner + 72u);
    if (!plausible(end_node)) return 0;
    uint32_t node = read32(end_node + 4u);  // the header's parent is the root
    uint32_t handler = 0;
    for (uint32_t step = 0; step < 64u && plausible(node) && node != end_node; ++step) {
        const uint32_t key = refii::guest::ScreenIdToTu1(read32(node + 12u));
        if (key == screen_id) {
            handler = read32(node + 16u);
            break;
        }

        node = read32(node + (screen_id < key ? 0u : 8u));
    }

    if (!plausible(handler)) return 0;

    const uint32_t preview = handler + preview_offset;
    if (base[preview + 64u] == 0u) return 0;  // nothing spawned — the panel is empty

    if (out_model_hash != nullptr) *out_model_hash = read32(preview + 124u);
    if (out_entity != nullptr) *out_entity = read32(preview);

    // translation xyz, rotation xyz, scale, rotation axis — in the struct's own order.
    if (out_values != nullptr) {
        const uint32_t offsets[] = { 80u, 84u, 88u, 96u, 100u, 104u, 112u, 120u };
        for (uint32_t i = 0; i < value_count && i < 8u; ++i) {
            out_values[i] = i == 7u
                ? static_cast<float>(static_cast<int32_t>(read32(preview + offsets[i])))
                : read_float(preview + offsets[i]);
        }
    }

    return screen_id;
}

/// The WORLD ICON queue — the channel property income and every scripted `GUI.AddWorldIcon`
/// travel on.
///
/// `wim = *(*(GUI+32)+28)` is the WorldIconManager; `sub_823062F0`, the Lua `AddWorldIcon`
/// native, calls into its request entry `sub_823DD6B0(wim, name, entity, text, iconType)`.
///
/// Two gates decide whether anything is ever queued, and both come back here:
///
///  * `wim+48` — the master enable. The ctor `sub_823DD248` clears it; only the HUD load path
///    arms it (`sub_823CA428`), and only if `sub_828254D8` binds EXACTLY 16 elements through
///    binding registry 7. Zero means `sub_823DD860` rejects every request.
///  * `CECMoney+20` (`ShowIncomeIcon`, component id 81 on the hero) — the income payout
///    `sub_8242E578` only labels the icon type 37 (`GUI_SCREEN_INCOME`) when this is set.
///
/// The queue itself is a 48-byte record vector at `wim+32` (begin +36, end +40): `+8` the
/// anchor entity, `+12` the amount LHString, `+16` the icon type (37 property income,
/// 36 gold change), `+32` a 16-byte position offset.
///
/// Returns the number of records written; `out_flags` bit 0 is the master enable and bit 1
/// is ShowIncomeIcon.
/// The world-space icon class — property signs, income and gift gold, opinion splashes, quest
/// markers, dog points, NPC status. `sub_823DDF90` files every request whose type is not one of
/// 31-35/52-54 (the HUD-element kinds) as a 368-byte object of vtable 0x820028C4 and pushes
/// `{obj, refblock}` (8 bytes) onto the manager's vector at `wim+4..wim+8` (0x823de288). The
/// guest drew them through `sub_82261B70`, which is off; Godot draws the same objects.
///
/// Per object (base = obj; from `sub_82241AD8`/`sub_821DEDA0`/`sub_821AA758`/`sub_82261B70`):
///   +20 type   +24 entity handle   +48 seconds left (frozen while +52 persistent)
///   +56 size (per type)   +60..62 text colour as B,G,R bytes   +64 opacity ceiling
///   +68 age   +100 fading   +112 resolved world point (`stvx128 v127, r28, r26` with r26=112,
///   the +32 offset plus head height and stacking)   +128 texture hash   +132 hero distance²
///   +140/+148 title/amount strings   +160 opacity, tweened between +156 and +164.
///
/// The drawn size is `+56 * curve(+132)` (`sub_821AA758`: >=300 -> 0.6; <100 -> 1 - d²/400;
/// else 0.75 - 0.00075*(d²-100)), a `50*size` square; the quad's colour is (0.7,0.7,0.7,+160)
/// and the text's is (+62,+61,+60)/255 with alpha +160. Published: rgba = R,G,B,A*255; the
/// scale value is the composed size.
///
/// THE ANCHOR ENTITY IS BEHIND A HANDLE. `obj+24` is an embedded handle object — its first word
/// is that class's vtable (0x82000B7C on every sprite, measured over 53 live ones) — and
/// `obj+28` its block, whose first word is the entity (vtable 0x820A9B38, and `entity+4` points
/// back at the block). Keying icons on `obj+24` folded every heart into one and every property
/// sign into another.
///
/// Words per icon: type, texture, rgba, entity, persistent, flags, pool slot, OBJECT (the
/// icon's identity for its lifetime). Values: anchor x y z, scale, seconds left, age. Text:
/// title NUL opinion NUL, per icon in order.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldSprites(uint32_t* words, uint32_t word_capacity,
                                                        float* values, uint32_t value_capacity,
                                                        char* text, uint32_t text_capacity,
                                                        uint32_t max_icons) {
    constexpr uint32_t kWords = 8u;
    constexpr uint32_t kValues = 6u;
    const uint32_t kSpriteVtable = refii::guest::Address(0x820028C4u);
    // The POOLED class (types 31-35/52-54, drawn on the 16 `WorldIconN` HUD elements): its
    // update projects `+112` (cached world anchor) plus `+144` (the lift putting the element's
    // bottom on the anchor) and writes the element's position. Published as a world point so
    // Godot projects it through ITS camera. Valid once the first update has run (`+160`
    // cleared); `+128` = pool slot record.
    const uint32_t kPooledVtable = refii::guest::Address(0x820DD0E0u);
    const uint32_t kPoolBase = refii::guest::Address(0x8349BC80u);

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto readf = [base](uint32_t at) {
        const uint32_t bits = GuestWordAt(base, at);
        float out = 0.0f;
        std::memcpy(&out, &bits, sizeof(out));
        return out;
    };
    // D3D objects (the sprite class is `Render_AllocD3DObject(368)`) live in the guest's
    // PHYSICAL heaps, above 0xC0000000, so the usual pointer ceiling does not apply.
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xE0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;
    const uint32_t hud = read32(gui + 32u);
    if (!plausible(hud)) return 0;
    const uint32_t wim = read32(hud + 28u);
    if (!plausible(wim)) return 0;

    const uint32_t begin = read32(wim + 4u);
    const uint32_t end = read32(wim + 8u);
    if (!plausible(begin) || end < begin || (end - begin) > 8u * 512u) return 0;

    uint32_t written = 0;
    uint32_t text_at = 0;
    const auto put_text = [&](const std::string& value) {
        if (text == nullptr || text_capacity == 0) return;
        for (char c : value) {
            if (text_at + 1 >= text_capacity) break;
            text[text_at++] = c;
        }
        if (text_at < text_capacity) text[text_at++] = '\0';
    };

    uint32_t entries = 0;
    uint32_t other = 0;
    uint32_t first_other_vtable = 0;
    for (uint32_t at = begin; at < end && written < max_icons; at += 8u) {
        const uint32_t obj = read32(at);
        ++entries;
        const uint32_t vtable = plausible(obj) ? read32(obj) : 0u;
        if (vtable == kPooledVtable) {
            if (base[obj + 160u] != 0u) continue;  // not yet updated: no anchor to publish
            if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity) break;
            const uint32_t slot_record = read32(obj + 128u);
            uint32_t* w = words + written * kWords;
            w[0] = read32(obj + 20u);
            w[1] = 0u;
            w[2] = 0xFFFFFFFFu;
            // The prompted thing: the icon's weak reference at +24 (sub_828203B8, from the
            // prompt's descriptor, sub_823DD1D0), whose block at +28 holds the entity first.
            const uint32_t block = read32(obj + 28u);
            w[3] = plausible(block) ? read32(block) : 0u;
            w[4] = 0u;
            w[5] = 2u;  // pooled
            w[6] = slot_record >= kPoolBase ? (slot_record - kPoolBase) / 64u : 0xFFFFFFFFu;
            w[7] = obj;
            float* v = values + written * kValues;
            v[0] = readf(obj + 112u) + readf(obj + 144u);
            v[1] = readf(obj + 116u) + readf(obj + 148u);
            v[2] = readf(obj + 120u) + readf(obj + 152u);
            v[3] = 1.0f;
            v[4] = 0.0f;
            v[5] = 0.0f;
            put_text(std::string());
            put_text(std::string());
            ++written;
            continue;
        }
        if (vtable != kSpriteVtable) {
            ++other;
            if (first_other_vtable == 0) first_other_vtable = plausible(obj) ? read32(obj) : obj;
            continue;
        }
        if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity) break;

        uint32_t* w = words + written * kWords;
        w[0] = read32(obj + 20u);
        w[1] = read32(obj + 128u);
        const float opacity = readf(obj + 160u);
        const uint32_t alpha = opacity <= 0.0f ? 0u : opacity >= 1.0f ? 255u
                                                  : static_cast<uint32_t>(opacity * 255.0f);
        w[2] = (static_cast<uint32_t>(base[obj + 62u]) << 24) |
               (static_cast<uint32_t>(base[obj + 61u]) << 16) |
               (static_cast<uint32_t>(base[obj + 60u]) << 8) | alpha;
        const uint32_t handle = read32(obj + 28u);
        w[3] = plausible(handle) ? read32(handle) : 0u;
        w[4] = base[obj + 52u];
        // bit0 fading, bit1 pooled, bit2 player two's (its key, +16, ends "_P2": mod_events.cpp,
        // the opinion icons asked in player two's scope).
        const uint32_t key_rep = read32(obj + 16u);
        const uint32_t key_chars = plausible(key_rep) ? read32(key_rep) : 0u;
        const uint32_t key_length = plausible(key_rep) ? read32(key_rep + 4u) : 0u;
        const bool player_two = plausible(key_chars) && key_length >= 3u && key_length <= 512u &&
                                std::memcmp(base + key_chars + key_length - 3u, "_P2", 3u) == 0;
        w[5] = (base[obj + 100u] != 0u ? 1u : 0u) | (player_two ? 4u : 0u);
        w[6] = 0xFFFFFFFFu;
        w[7] = obj;

        float* v = values + written * kValues;
        v[0] = readf(obj + 112u);
        v[1] = readf(obj + 116u);
        v[2] = readf(obj + 120u);
        const float d2 = readf(obj + 132u);
        const float curve = d2 >= 300.0f ? 0.6f
                          : d2 < 100.0f ? 1.0f - (d2 * 0.0025f)
                                        : 0.75f - ((d2 - 100.0f) * 0.00075f);
        v[3] = readf(obj + 56u) * curve;
        v[4] = readf(obj + 48u);
        v[5] = readf(obj + 68u);

        // BOTH ARE WIDE. `sub_82824968`, the sprite's vtable slot, is exactly
        // `sub_822B88A8(this + 140, title); sub_822B88A8(this + 148, amount);` — two LHString
        // handles. The amount is the whole readout for a good or evil deed: `sub_828203B8`
        // gives an amount-bearing type (1..21, 36..49, 54) the request's text as the AMOUNT and
        // synthesises the title, and types 38/39 are given no title at all.
        put_text(ReadGuestWideString(base, obj + 140u));
        put_text(ReadGuestWideString(base, obj + 148u));
        ++written;
    }

    // What the walk met, once per change: the split between the two classes and the vtable of
    // whatever it declined.
    static uint32_t reported_entries = 0xFFFFFFFFu;
    static uint32_t reported_other = 0xFFFFFFFFu;
    if (entries != reported_entries || other != reported_other) {
        reported_entries = entries;
        reported_other = other;
        REXLOG_INFO("Refii world sprites: {} vector entr{} ({} sprite-class, {} other; first other "
                    "vtable/ptr 0x{:08X}) at wim=0x{:08X} [0x{:08X}..0x{:08X})",
                    entries, entries == 1 ? "y" : "ies", written, other, first_other_vtable, wim,
                    begin, end);
    }

    return written;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldIcons(uint32_t* out_flags, uint32_t* out_types,
                                                      uint32_t* out_entities, uint32_t max_icons) {
    if (out_flags != nullptr) *out_flags = 0;

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) return 0;
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) return 0;

    const auto read32 = [base](uint32_t at) { return GuestWordAt(base, at); };
    const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };

    const uint32_t gui = read32(refii::guest::Address(0x83496B08u));
    if (!plausible(gui) || read32(gui) != refii::guest::Address(0x82001610u)) return 0;

    const uint32_t hud = read32(gui + 32u);
    if (!plausible(hud)) return 0;

    const uint32_t wim = read32(hud + 28u);
    if (!plausible(wim)) return 0;

    uint32_t flags = base[wim + 48u] != 0u ? 1u : 0u;

    // ShowIncomeIcon, off the hero's money component: the pause-tab predicate's hero chain,
    // then component id 81 (`CECMoney`) via the sorted {id, ptr} pairs at entity+0x48..+0x4C.
    const uint32_t game_root = read32(refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(game_root) ? read32(game_root + 12u) : 0u;
    const uint32_t session = plausible(game) ? read32(game + 0x8Cu) : 0u;
    const uint32_t player_map = plausible(game) ? read32(game + 0x9Cu) : 0u;
    if (plausible(session) && plausible(player_map)) {
        const uint32_t key =
            (base[session + 0x34u] != 0u && base[session + 0x35u] != 0u) ? 1u : 2u;
        uint32_t entry = read32(player_map + 8u);
        const uint32_t entries_end = read32(player_map + 12u);
        uint32_t hero = 0;
        if (plausible(entry) && entries_end >= entry && (entries_end - entry) <= 12u * 64u) {
            for (; entry < entries_end; entry += 12u) {
                if (read32(entry) == key) {
                    const uint32_t record = read32(entry + 4u);
                    const uint32_t control = plausible(record) ? read32(record + 16u) : 0u;
                    hero = plausible(control) ? read32(control) : 0u;
                    break;
                }
            }
        }

        if (plausible(hero)) {
            const uint32_t pairs = read32(hero + 0x48u);
            const uint32_t pairs_end = read32(hero + 0x4Cu);
            if (plausible(pairs) && pairs_end >= pairs && (pairs_end - pairs) <= 8u * 512u) {
                for (uint32_t at = pairs; at < pairs_end; at += 8u) {
                    if (read32(at) == 81u) {
                        const uint32_t money = read32(at + 4u);
                        if (plausible(money) && base[money + 20u] != 0u) flags |= 2u;
                        break;
                    }
                }
            }
        }
    }

    // How many of the guest's own icon elements are lit, in bits 8..15. The guest draws these
    // itself: `sub_82825618` claims a free slot out of the 16-element pool `sub_828254D8`
    // bound from binding registry 7 and marks it busy at +48; the per-frame update
    // `sub_82824D60` projects the anchor and writes the element's position via
    // `sub_824251E0(*(slot+0), presentation, x, y, z)`. Types 35/52/53 have dedicated elements
    // rather than a pool slot and are counted in bits 16..18.
    //
    // Packed into the existing flags word: a new out parameter would be an ABI break.
    const uint32_t kIconPool = refii::guest::Address(0x8349BC80u);
    const uint32_t kIconPoolEnd = refii::guest::Address(0x8349C080u);
    uint32_t lit = 0;
    for (uint32_t slot = kIconPool; slot < kIconPoolEnd; slot += 64u) {
        if (base[slot + 48u] != 0u) ++lit;
    }
    flags |= (lit & 0xFFu) << 8;

    const uint32_t kIconSingles[3] = {refii::guest::Address(0x8349C080u), refii::guest::Address(0x8349C0C0u), refii::guest::Address(0x8349C100u)};
    for (uint32_t i = 0; i < 3u; ++i) {
        if (base[kIconSingles[i] + 48u] != 0u) flags |= 1u << (16u + i);
    }

    if (out_flags != nullptr) *out_flags = flags;

    const uint32_t begin = read32(wim + 36u);
    const uint32_t end = read32(wim + 40u);
    if (!plausible(begin) || end < begin) return 0;

    const uint32_t count = (end - begin) / 48u;
    const uint32_t wanted = count < max_icons ? count : max_icons;
    for (uint32_t i = 0; i < wanted; ++i) {
        const uint32_t record = begin + (i * 48u);
        if (out_types != nullptr) out_types[i] = read32(record + 16u);
        if (out_entities != nullptr) out_entities[i] = read32(record + 8u);
    }

    return count;
}

//----------------------------------------------------------------------------------
// Achievements
//
// Godot owns achievements (Engine_Achievements): the definitions parsed from the title's XDBF,
// the unlock store and its file, the toast and the page. `refii_achievement` is the guest-facing
// seam only, and these four exports are its two directions: the guest's writes and its UI
// requests come up; the XDBF comes up once; the unlocked set the host states goes down for the
// guest's own enumerator to read back.
//----------------------------------------------------------------------------------

/// Writes NEWER than `after_sequence`, oldest first. Returns how many were written.
///
/// A sequence, not a flushing queue: the seam keeps every write of the session stamped with a
/// monotonic sequence, so the read is idempotent and two readers cannot lose one between them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAchievementUnlocks(uint64_t after_sequence,
                                                             uint32_t* out_ids,
                                                             uint32_t* out_users,
                                                             uint64_t* out_sequences,
                                                             uint32_t max_unlocks) {
    const std::vector<refii::achievement::AchievementUnlock> writes =
        refii::achievement::GetAchievementWritesSnapshot();

    uint32_t written = 0;
    for (const auto& write : writes) {
        if (write.sequence <= after_sequence) continue;
        if (written >= max_unlocks) break;

        if (out_ids != nullptr) out_ids[written] = write.achievement_id;
        if (out_users != nullptr) out_users[written] = write.user_index;
        if (out_sequences != nullptr) out_sequences[written] = write.sequence;
        ++written;
    }

    return written;
}

/// The title's XDBF resource. Returns the byte count — call with a null buffer to size it
/// first. Zero until the executable module has been launched.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTitleXdbf(uint8_t* out_bytes, uint32_t capacity) {
    const std::vector<uint8_t> xdbf = refii::achievement::GetTitleXdbfSnapshot();
    if (out_bytes != nullptr && capacity != 0) {
        const size_t take = xdbf.size() < capacity ? xdbf.size() : capacity;
        std::memcpy(out_bytes, xdbf.data(), take);
    }

    return static_cast<uint32_t>(xdbf.size());
}

/// The unlocked set as the host's store has it — ids with unlock FILETIMEs — for the guest's
/// `XamUserCreateAchievementEnumerator` to answer from. Replaces the previous statement.
REFII_HOST_ABI_EXPORT void RefiiHostSetAchievementUnlocks(const uint32_t* ids,
                                                          const uint64_t* unlock_filetimes,
                                                          uint32_t count) {
    if (ids == nullptr || count == 0) {
        refii::achievement::SetUnlockedAchievements({}, {});
        return;
    }

    refii::achievement::SetUnlockedAchievements(
        std::span<const uint32_t>(ids, count),
        unlock_filetimes == nullptr ? std::span<const uint64_t>()
                                    : std::span<const uint64_t>(unlock_filetimes, count));
}

/// How many times the guest has called `XamShowAchievementsUI` this session; the host opens
/// its page on every increment.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAchievementsUiRequests(void) {
    return refii::achievement::GetAchievementsUiRequestCount();
}

/// Where the guest wants the live movie drawn — the rect it states for itself, so "fullscreen
/// or embedded" needs no inference. Every caller of the open sets it immediately afterwards
/// (`sub_82A41E30`) and the guest's own draw builds its quad from these four floats: the
/// fullscreen owners state the screen rect (letterboxed to 1/8..7/8 of the height in 4:3), the
/// UI sprite owner the hosting element's rect. Returns 0 when no movie is live.
REFII_HOST_ABI_EXPORT int RefiiHostGetMovieRect(float* out_x0, float* out_y0,
                                                float* out_x1, float* out_y1) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::vfs::BinkMovieDestRect(memory->virtual_membase(),
                                         out_x0, out_y0, out_x1, out_y1) != 0 ? 1 : 0;
}

/// Tells the guest the movie the host was showing has finished.
///
/// The guest's startup sequence blocks until every movie has played, and decides that
/// from its OWN Bink decode — work whose output nobody sees now that Godot draws the
/// picture, running on a clock that drifts from the one the player is watching. This
/// writes the same done byte the guest writes itself, so the sequence advances when the
/// movie visibly ends rather than tens of seconds later.
///
/// Returns the player address retired, or 0 when no movie was live.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostFinishMovie(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::vfs::FinishCurrentBinkMovie(memory->virtual_membase());
}

/// The rumble the guest last asked for, 0..65535 per motor. Returns 1 on success.
REFII_HOST_ABI_EXPORT int RefiiHostGetVibration(uint32_t user_index, uint32_t* out_left, uint32_t* out_right) {
    uint16_t left = 0;
    uint16_t right = 0;
    if (!refii::host::TakeVibration(user_index, &left, &right)) {
        return 0;
    }

    if (out_left != nullptr) {
        *out_left = left;
    }
    if (out_right != nullptr) {
        *out_right = right;
    }
    return 1;
}

/// Reserves the guest address space in THIS process and reports where it landed.
///
/// Returns 1 on success. On failure returns 0 and `out` carries the reason.
///
/// This is the gate for hosting the guest inside Godot. `rex::memory::Memory::Initialize` maps a
/// 4.5 GB file-backed region (the full 4 GB guest space plus 512 MB physical) and then
/// tries to place its views at 1<<32 .. 1<<63, taking the first base where every view
/// lands. It is NOT a single fixed address — that is the assumption worth killing early,
/// because it makes "Godot already owns that range" a 32-way retry rather than a hard
/// failure.
///
/// The reservation is released again before returning: this answers whether the space
/// CAN be taken, and holding 4.5 GB of the editor's address space to prove it would be
/// its own problem.
REFII_HOST_ABI_EXPORT int RefiiHostProbeGuestMemory(char* out, std::size_t capacity) {
    try {
        auto memory = std::make_unique<rex::memory::Memory>();
        if (!memory->Initialize()) {
            WriteOut("rex::memory::Memory::Initialize failed: no continuous 4.5 GB block was "
                     "available at any of the 32 candidate bases",
                     out, capacity);
            return 0;
        }

        const auto* virtual_base = memory->virtual_membase();
        const auto* physical_base = memory->physical_membase();

        char buffer[256];
        std::snprintf(buffer, sizeof(buffer),
                      "virtual_membase=%p physical_membase=%p (offset 0x%llx)",
                      static_cast<const void*>(virtual_base),
                      static_cast<const void*>(physical_base),
                      static_cast<unsigned long long>(physical_base - virtual_base));

        WriteOut(buffer, out, capacity);
        return 1;
    } catch (const std::exception& error) {
        WriteOut(std::string("exception: ") + error.what(), out, capacity);
        return 0;
    } catch (...) {
        WriteOut("unknown exception while reserving guest memory", out, capacity);
        return 0;
    }
}

/// Boots the guest on a dedicated thread and returns immediately.
///
/// Returns 1 when the thread was started — NOT when the guest is up. Booting loads the
/// XEX, maps the guest space and runs its init; it takes seconds and can fail long after
/// this returns, so the caller polls <see cref="RefiiHostIsRunning"/> and
/// <see cref="RefiiHostStatus"/> rather than treating a 1 here as success.
///
/// <param name="arguments">Newline-separated. Not space-separated: an argument can be a
/// path with spaces, and this tree has several.</param>
///
/// ONE PER PROCESS. The runtime owns global state — the guest memory map, the kernel
/// object table, the MMIO signal handler — so a second boot cannot work and is refused
/// rather than left to corrupt the first.
REFII_HOST_ABI_EXPORT int RefiiHostStart(const char* game_directory, const char* arguments,
                   char* out, std::size_t capacity) {
    GuestSession& session = Session();

    bool expected = false;
    if (!session.started.compare_exchange_strong(expected, true)) {
        WriteOut("guest already started in this process; the runtime's global state "
                 "cannot be booted twice",
                 out, capacity);
        return 0;
    }

    if (game_directory == nullptr || game_directory[0] == '\0') {
        session.started.store(false);
        WriteOut("no game directory given", out, capacity);
        return 0;
    }

    std::filesystem::path directory{game_directory};
    std::error_code exists_error;
    if (!std::filesystem::is_directory(directory, exists_error)) {
        session.started.store(false);
        WriteOut("game directory does not exist: " + directory.string(), out, capacity);
        return 0;
    }

    std::vector<std::string> parsed = SplitArguments(arguments);

    ApplyEmbeddedEnvironment();

    // The host owns presentation. Two switches, and they are not the same thing:
    //
    //   REX_EMBED_HEADLESS=1  no window, no ImGui drawer, no presentation setup.
    //
    // NOT REX_HEADLESS: that name maps to the existing `headless` KERNEL cvar, which
    // suppresses XAM UI dialogs. Setting it changed guest dialog behaviour as a silent
    // side effect of asking for no window.
    //
    // REX_EMBED_HEADLESS is also what tells RefiiApp::OnPreSetup there is no guest GPU: without
    // it the SDK defaults gpu_plugin to "xenos" and dlopens librexgpu-xenosd.so from beside
    // the executable, which embedded is Godot's directory, and the boot fails.
    SetEnvironmentValue("REX_EMBED_HEADLESS", "1");

    // Pass the game directory as an explicit flag, not just positionally.
    //
    // Embedded, GetExecutableFolder() is the HOST's directory — Godot's — so the guest
    // looks for refii.toml beside the Godot binary, finds none, and falls back to a
    // relative default ("assets/game") resolved against a working directory that has no
    // game in it. Nothing about the guest's own layout applies once it is a library
    // inside someone else's process, so the host has to say where things are.
    bool has_game_root = false;
    for (const std::string& argument : parsed) {
        if (argument.rfind("--game_data_root", 0) == 0) {
            has_game_root = true;
            break;
        }
    }
    if (!has_game_root) {
        parsed.emplace_back("--game_data_root=" + directory.string());
    }

    session.running.store(true);
    session.SetStatus("booting");

    session.thread = std::thread([&session, directory, parsed]() {
        int result = -1;
        try {
            result = RunRefiiEmbedded(directory, parsed);
            session.SetStatus("exited with code " + std::to_string(result));
        } catch (const std::exception& error) {
            session.SetStatus(std::string("threw: ") + error.what());
        } catch (...) {
            session.SetStatus("threw an unknown exception");
        }

        session.exit_code.store(result);
        session.running.store(false);
    });

    WriteOut("started; poll RefiiHostIsRunning / RefiiHostStatus", out, capacity);
    return 1;
}

/// 1 while the guest thread is alive.
REFII_HOST_ABI_EXPORT int RefiiHostIsRunning(void) { return Session().running.load() ? 1 : 0; }

/// 1 while a guest is up and its memory is safe to read, BY EITHER BOOT PATH.
///
/// WHY THIS EXISTS, and it is worth stating plainly because the absence of it cost a whole
/// character pipeline. There are two ways to bring the guest up and they set different flags:
///
///   `RefiiHostStart`      — runs the guest on its OWN thread; sets `Session().running`.
///   `RefiiHostInitialize` — the EMBEDDED path the game uses, driven a slice per frame by
///                           `RefiiHostStep`; sets `Hosted().initialized` and never touches
///                           `Session().running`.
///
/// `RefiiHostIsRunning` only ever answers for the first. Every host accessor that guarded
/// itself with it — entity poses, rest poses, bone names, locomotion, the language table —
/// therefore returned "no guest" on every frame of the RUNNING GAME while the guest was
/// perfectly alive, and did so silently. A run showed the shape of it exactly: 3205 drawn
/// entities with no skeleton, `characters=0 bones=0`, and the report itself concluding "a node
/// whose rest pose the bridge will not return". Nothing was wrong with any of those readers.
///
/// So this is the predicate an accessor wants: not "did anyone call Start", but "is there a
/// guest whose address space I may read". `RefiiHostIsRunning` keeps its narrower meaning for
/// the callers that genuinely ask about the session THREAD — chiefly waiting for it to exit.
REFII_HOST_ABI_EXPORT int RefiiHostIsGuestUp(void) {
    return (Session().running.load() || Hosted().initialized) ? 1 : 0;
}

/// The guest's exit code, or -1 while it is still running or was never started.
REFII_HOST_ABI_EXPORT int RefiiHostExitCode(void) { return Session().exit_code.load(); }

/// A human-readable status line. Same buffer convention as the probe.
REFII_HOST_ABI_EXPORT int RefiiHostStatus(char* out, std::size_t capacity) {
    return WriteOut(Session().GetStatus(), out, capacity);
}

/// Joins the guest thread, if it has finished.
///
/// Does NOT ask the guest to stop — there is no clean external shutdown wired through
/// this ABI yet, and pretending otherwise would leave a caller believing it had asked.
/// Call it after <see cref="RefiiHostIsRunning"/> reports 0.
/// Initialises the guest ON THE CALLING THREAD and returns once it is up.
///
/// This is the "owned by Godot" entry point: the host keeps its own thread and drives
/// the guest with <see cref="RefiiHostStep"/>, instead of handing a thread to the
/// guest's own blocking loop.
///
/// SYNCHRONOUS, and it takes seconds — it loads the XEX and runs the guest's init. Call
/// it from the host's main thread and accept the stall: SDL wants its main thread, and
/// the previous arrangement (guest on a worker) left that unresolved.
///
/// Returns 1 when the guest is initialised and ready to be stepped.
#ifdef __linux__
/// Unlinks guest memory arenas whose process is gone.
///
/// `rex::system::Memory::Initialize` backs the guest's 4 GB + 512 MB address space with a POSIX
/// shared-memory object named `xenia_memory_<host tick count>` (system/xmemory.cpp). /dev/shm is
/// RAM, and an shm object outlives its creator: a run that aborts or is killed never unlinks its
/// arena, so every crashed session permanently costs whatever that arena had touched. Measured
/// 2026-08-19 — 16 abandoned arenas, 5.9 GB resident, on a machine that had pushed 26 GB into
/// swap by then. The symptom is not "a leak", it is the next run stuttering and dying.
///
/// ONLY THE UNMAPPED ONES. Running a second instance is legitimate, so an arena goes only when
/// no live process maps it — /proc/<pid>/maps names the object. If /proc cannot be read, nothing
/// is removed: this must never guess a live arena is dead.
void ReclaimAbandonedGuestArenas() {
    static constexpr std::string_view kArenaPrefix = "xenia_memory_";

    std::error_code error;
    std::filesystem::directory_iterator shm{"/dev/shm", error};
    if (error) {
        return;
    }

    std::vector<std::filesystem::path> candidates;
    for (const std::filesystem::directory_entry& entry : shm) {
        if (entry.path().filename().string().rfind(kArenaPrefix, 0) == 0) {
            candidates.push_back(entry.path());
        }
    }
    if (candidates.empty()) {
        return;
    }

    std::error_code proc_error;
    std::filesystem::directory_iterator processes{"/proc", proc_error};
    if (proc_error) {
        return;
    }

    std::set<std::string> live;
    for (const std::filesystem::directory_entry& process : processes) {
        const std::string pid = process.path().filename().string();
        if (pid.empty() || !std::all_of(pid.begin(), pid.end(),
                                        [](unsigned char c) { return std::isdigit(c) != 0; })) {
            continue;
        }
        std::ifstream maps{process.path() / "maps"};
        if (!maps) {
            continue;  // exited between the listing and the open, or not ours to read
        }
        std::string line;
        while (std::getline(maps, line)) {
            const std::size_t at = line.find(kArenaPrefix);
            if (at == std::string::npos) {
                continue;
            }
            std::size_t end = line.find_first_of(" \t", at);
            if (end == std::string::npos) {
                end = line.size();
            }
            live.insert(line.substr(at, end - at));
        }
    }

    std::size_t reclaimed = 0;
    for (const std::filesystem::path& candidate : candidates) {
        if (live.count(candidate.filename().string()) != 0) {
            continue;
        }
        std::error_code remove_error;
        if (std::filesystem::remove(candidate, remove_error)) {
            ++reclaimed;
        }
    }

    if (reclaimed != 0) {
        std::fprintf(stderr, "[refii] reclaimed %zu abandoned guest memory arena(s) in /dev/shm\n",
                     reclaimed);
    }
}
#endif  // __linux__

REFII_HOST_ABI_EXPORT int RefiiHostInitialize(const char* game_directory, const char* arguments,
                        char* out, std::size_t capacity) {
    HostedGuest& hosted = Hosted();

    if (hosted.initialized) {
        WriteOut("already initialized", out, capacity);
        return 0;
    }

    if (game_directory == nullptr || game_directory[0] == '\0') {
        WriteOut("no game directory given", out, capacity);
        return 0;
    }

    std::filesystem::path directory{game_directory};
    std::error_code exists_error;
    if (!std::filesystem::is_directory(directory, exists_error)) {
        WriteOut("game directory does not exist: " + directory.string(), out, capacity);
        return 0;
    }

    try {
#ifdef __linux__
        ReclaimAbandonedGuestArenas();
#endif
        ApplyEmbeddedEnvironment();

        // argv[0] then the flags. The game directory goes as an explicit flag, not
        // positionally: embedded, GetExecutableFolder() is the HOST's directory, so the
        // guest finds no refii.toml of its own and would fall back to a relative default.
        std::vector<std::string> owned;
        owned.emplace_back("refii");
        for (const std::string& argument : SplitArguments(arguments)) {
            owned.push_back(argument);
        }
        owned.push_back("--game_data_root=" + directory.string());

        std::vector<char*> argv;
        argv.reserve(owned.size());
        for (std::string& argument : owned) {
            argv.push_back(argument.data());
        }

        rex::cvar::Init(static_cast<int>(argv.size()), argv.data());
        rex::cvar::ApplyEnvironment();

        hosted.context = std::make_unique<rex::ui::SDLWindowedAppContext>();
        if (!hosted.context->Initialize()) {
            hosted.status = "SDL context init failed";
            WriteOut(hosted.status, out, capacity);
            return 0;
        }

        hosted.app = CreateRefiiApp(*hosted.context);
        if (!hosted.app) {
            hosted.status = "CreateRefiiApp returned null";
            WriteOut(hosted.status, out, capacity);
            return 0;
        }

        if (!hosted.app->OnInitialize()) {
            hosted.status = "guest OnInitialize failed";
            WriteOut(hosted.status, out, capacity);
            return 0;
        }

        hosted.initialized = true;
        InstallLastResortFaultHandler();

        // Windows: the process timer period, raised to 1 ms BEFORE any pacing thread starts.
        // The runtime's own Sleep(1) and every guest timed wait with a millisecond timeout are
        // quantised to it, and nothing else in the process is guaranteed to have raised it; at
        // the 15.6 ms default the 1 ms polls below run at 64 Hz. A no-op elsewhere.
        refii::RaiseTimerResolution();

        // THE FAST-ARM WATCHER. The guest's D3D device creation reads the no-GPU latch ONCE,
        // tens of milliseconds after publishing the device pointer; armed first, boot proceeds —
        // read first, the guest takes the "a GPU exists" path and parks in a ring wait forever.
        // Polled only from the 30 Hz step, the arm lands up to a frame late and loses that race
        // roughly half the time (observed 5 losses / 8 launches in one day). A 1 ms poll on its
        // own thread wins it by construction; the thread exits the moment the latch is armed.
        g_arm_watcher = std::thread([]() {
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::minutes(3);
            while (!g_arm_watcher_stop.load(std::memory_order_relaxed) &&
                   !g_no_gpu_armed.load(std::memory_order_relaxed) &&
                   std::chrono::steady_clock::now() < deadline) {
                ArmGuestNoGpuMode();
                refii::PreciseSleepFor(std::chrono::milliseconds(1));
            }
        });
        const char* completion_env = std::getenv("REFII_COMPLETION_THREAD");
        if (completion_env == nullptr || std::string(completion_env) != "0") {
            g_completion_thread = std::thread([]() {
                while (!g_completion_stop.load(std::memory_order_relaxed)) {
                    SignalGuestFrameCompletions();
                    refii::PreciseSleepFor(std::chrono::milliseconds(1));
                }
            });
        }

        hosted.status = "running";
        WriteOut("initialized; drive it with RefiiHostStep", out, capacity);
        return 1;
    } catch (const std::exception& error) {
        hosted.status = std::string("threw: ") + error.what();
        WriteOut(hosted.status, out, capacity);
        return 0;
    } catch (...) {
        hosted.status = "threw an unknown exception";
        WriteOut(hosted.status, out, capacity);
        return 0;
    }
}

/// Advances the guest by one host frame. Returns 1 while it is still running.
///
/// This is the loop body the guest would otherwise run for itself, minus the blocking
/// wait. Headless there are no window or input events to dispatch — the only event that
/// matters is the wakeup that carries pending UI-thread work, and
/// ExecutePendingFunctionsFromUIThread services that directly. The queue is still
/// drained so SDL does not accumulate events nobody reads.
REFII_HOST_ABI_EXPORT int RefiiHostStep(void) {
    HostedGuest& hosted = Hosted();
    if (!hosted.initialized || hosted.finished) {
        return 0;
    }

    try {
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            // Deliberately dropped: with no window there is nothing to route them to.
        }

        // RE-ASSERT THE RENDER-FENCE HOLD. The world's own Update stamps `engine+176` with the
        // real hand-off time whenever it drains the pending `.engine_level`, so a hold written
        // once at the start of a build is wiped the first time that runs and the guest walks
        // straight into a world Godot has not finished. Costs one pointer walk per step, and
        // only while a build is actually outstanding.
        if (g_world_build_pending.load(std::memory_order_relaxed)) {
            if (auto* memory = SafeKernelMemory()) {
                if (uint8_t* base = memory->virtual_membase()) {
                    apply_world_build_hold(base);
                }
            }
        }

        hosted.context->ExecutePendingFunctionsFromUIThread();

        // Cheap until it succeeds, then a single predictable branch.
        ArmGuestNoGpuMode();
        KeepGuestIdleCounterClear();
        SignalGuestFrameCompletions();
        // Runs on Godot's thread, which is the point: the guest thread that needs this is
        // the one spinning, so it cannot pump its own ring.
        RetireGuestSceneStreams();

        if (auto* step_memory = SafeKernelMemory()) {
            if (uint8_t* step_base = step_memory->virtual_membase()) {
                refii::vfs::FinishStuckInGameLoadingScreenFromHost(step_base);
            }
        }

        if (hosted.context->HasQuitFromUIThread()) {
            hosted.finished = true;
            hosted.status = "guest requested quit";
            return 0;
        }

        return 1;
    } catch (const std::exception& error) {
        hosted.finished = true;
        hosted.status = std::string("step threw: ") + error.what();
        return 0;
    } catch (...) {
        hosted.finished = true;
        hosted.status = "step threw an unknown exception";
        return 0;
    }
}

/// Status of the hosted guest, for diagnostics.
REFII_HOST_ABI_EXPORT int RefiiHostHostedStatus(char* out, std::size_t capacity) {
    return WriteOut(Hosted().status, out, capacity);
}

/// Tears the hosted guest down. Safe to call more than once.
///
/// Without this the guest's threads are still live when the host process exits, which
/// is what produced the abort on teardown.
REFII_HOST_ABI_EXPORT int RefiiHostShutdown(char* out, std::size_t capacity) {
    HostedGuest& hosted = Hosted();

    g_completion_stop = true;
    if (g_completion_thread.joinable()) {
        g_completion_thread.join();
    }

    g_arm_watcher_stop = true;
    if (g_arm_watcher.joinable()) {
        g_arm_watcher.join();
    }

    if (!hosted.initialized) {
        WriteOut("not initialized", out, capacity);
        return 1;
    }

    try {
        // Ask the runtime to stop, then PUMP while it does.
        //
        // TerminateTitle runs on its own thread, but it queues work back to the UI thread
        // — and this call is made from that thread. Blocking here instead of pumping is
        // therefore self-defeating: the work that would finish the shutdown is sitting in
        // a queue that only this thread drains, so the grace period always expires and
        // the runtime is abandoned with its threads live.
        auto* app = dynamic_cast<rex::ReXApp*>(hosted.app.get());
        if (app != nullptr) {
            app->RequestShutdown();
        } else if (hosted.context && !hosted.context->HasQuitFromUIThread()) {
            hosted.context->QuitFromUIThread();
        }

        bool stopped = app == nullptr;
        if (app != nullptr && hosted.context) {
            const auto deadline =
                std::chrono::steady_clock::now() + kShutdownPumpBudget;
            while (std::chrono::steady_clock::now() < deadline) {
                SDL_Event event;
                while (SDL_PollEvent(&event)) {
                }

                hosted.context->ExecutePendingFunctionsFromUIThread();

                if (app->ShutdownThreadsFinished()) {
                    stopped = true;
                    break;
                }

                refii::PreciseSleepFor(std::chrono::milliseconds(1));
            }
        }

        // Only once the guest has stopped: OnDestroy ends in runtime_.reset(), which frees
        // every XThread, and a straggler (one spinning on a D3D fence never reaches a kernel
        // wait, so TerminateTitle cannot stop it) would read the freed object on its next
        // kernel call. The leak path below must leave the runtime intact.
        if (stopped) {
            if (hosted.app) {
                hosted.app->InvokeOnDestroy();
            }
#ifndef _WIN32
            fault::Reinstall();
#endif

            // The guest really stopped, so its memory can go back and a later session is
            // possible within this process.
            hosted.app.reset();
            hosted.context.reset();
            rex::ShutdownLogging();

            hosted.initialized = false;
            hosted.finished = true;
            hosted.status = "shut down";
            WriteOut(hosted.status, out, capacity);
            return 1;
        }

        // DELIBERATELY LEAKED, not reset.
        //
        // Standalone, refii tears the runtime down and then the process ends, so nothing
        // outlives the wreckage. Hosted, the process carries on — and the guest's title
        // threads routinely miss the shutdown grace period, so destroying the app and
        // context here runs their destructors underneath threads still using them. That
        // is a SIGSEGV in the host: the launcher crashed on closing the game window,
        // having previously been killed outright by the runtime's own std::_Exit.
        //
        // Logging is left up for the same reason: ShutdownLogging frees loggers a
        // straggler may still write through.
        //
        // The cost is that the runtime's memory is held until the process exits. That is
        // affordable because a session is single-shot regardless — the runtime keeps
        // process-global state (memory map, kernel, MMIO handler) and cannot be started
        // twice, which GameWindow already enforces.
        (void)hosted.app.release();
        (void)hosted.context.release();

        hosted.initialized = false;
        hosted.finished = true;
        // Name what is stuck. "Play twice in one session" is blocked on exactly this,
        // and a bare "left resident" gives the next person nothing to start from.
        const std::string pending =
            app != nullptr ? app->ShutdownThreadsPending() : std::string("unknown");
        hosted.status =
            "shut down (runtime left resident; still running: " +
            (pending.empty() ? std::string("none") : pending) + ")";
        WriteOut(hosted.status, out, capacity);
        return 1;
    } catch (const std::exception& error) {
        WriteOut(std::string("shutdown threw: ") + error.what(), out, capacity);
        return 0;
    } catch (...) {
        WriteOut("shutdown threw an unknown exception", out, capacity);
        return 0;
    }
}

REFII_HOST_ABI_EXPORT int RefiiHostJoin(char* out, std::size_t capacity) {
    GuestSession& session = Session();

    if (session.running.load()) {
        WriteOut("still running", out, capacity);
        return 0;
    }

    if (session.thread.joinable()) {
        session.thread.join();
    }

    WriteOut(session.GetStatus(), out, capacity);
    return 1;
}

// ---------------------------------------------------------------------------
// RexNet — friends, invites and sessions, for the shell's own network screen.
//
// OVER `RexNet::shared()`, NEVER `rexnet_init`. The FFI in <rex/net/rexnet_ffi.h> is the
// layer BELOW this one: `RexNet::InitializeShared` calls `rexnet_init` and owns the handle,
// the pump thread, the friend list and the presence cache. A second `rexnet_init` from the
// shell would stand up a second libp2p node with its own identity — a different player.
//
// TEXT, NOT STRUCTS. Every reader returns LF-separated records of TAB-separated fields,
// UTF-8, and the return value is the byte count (0 when RexNet is absent or down). Display
// names are peer-supplied, so `Field` strips every control character on the way out: a name
// carrying a tab or a newline would otherwise forge a record boundary. The field order is
// stated at each export and mirrored by Refii.Host.NetBridge.

/// Is RexNet up? Writes one record: `displayName \t peerId \t friendCode`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetIdentity(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    const RexNetPeerId self = net->local_peer_id();
    if (!NetStillUp(net)) {
        return 0;
    }

    const std::string name = net->display_name();
    char code[192] = {0};
    rexnet_friend_code(&self, name.empty() ? nullptr : name.c_str(), code, sizeof(code));

    std::string row;
    Field(row, name, '\t');
    Field(row, PeerText(self), '\t');
    Field(row, code, '\n');
    return Emit(row, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif

}

/// One record per friend: `peerId \t name \t online \t titleId \t titleName \t hasSession
/// \t sessionId \t presenceState`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetFriends(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
        Field(text, PeerText(friend_info.peer), '\t');
        Field(text, friend_info.display_name, '\t');
        FieldNum(text, friend_info.online ? 1u : 0u, '\t');
        FieldNum(text, friend_info.title_id, '\t');
        Field(text, friend_info.title_name, '\t');
        FieldNum(text, friend_info.has_session ? 1u : 0u, '\t');
        Field(text, friend_info.has_session ? HexOf(friend_info.session_id) : std::string(), '\t');
        FieldNum(text, friend_info.presence_state, '\t');
        Field(text, friend_info.status, '\t');
        FieldNum(text, friend_info.signal, '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per inbound friend request: `peerId \t name \t note`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetRequests(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    for (const rex::net::RexNet::FriendRequestInfo& request : net->PendingFriendRequests()) {
        Field(text, PeerText(request.peer), '\t');
        Field(text, request.display_name, '\t');
        Field(text, request.note, '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per notice the node raised since the last call: `kind\tpeerId\tmessage`, kind
/// 0 error, 1 invite failed, 2 join timed out (`RexNet::Notice::Kind`). Drained on read.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetNotices(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const rex::net::RexNet::Notice& notice : net->TakeNotices()) {
        Field(text, std::to_string(static_cast<uint32_t>(notice.kind)), '\t');
        Field(text, PeerText(notice.peer), '\t');
        Field(text, notice.message, '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per peer that dismissed a request we sent since the last call: `peerId`.
/// Drained on read; the node keeps nothing once it is reported.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetDeclined(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const RexNetPeerId& peer : net->TakeDeclinedFriendRequests()) {
        Field(text, PeerText(peer), '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per peer a request we sent REACHED since the last call: `peerId`. Drained on
/// read. "Sent" is the bridge handing the request to the node; this is the other side's ack.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetDelivered(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const RexNetPeerId& peer : net->TakeDeliveredFriendRequests()) {
        Field(text, PeerText(peer), '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per pending invite: `peerId \t titleId \t sessionId`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetInvites(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    for (const rex::net::RexNet::InviteInfo& invite : net->PendingInvites()) {
        Field(text, PeerText(invite.peer), '\t');
        FieldNum(text, invite.title_id, '\t');
        Field(text, HexOf(invite.session_id), '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Drains the accumulated session search: `hostPeerId \t sessionId \t total \t open \t invite`.
///
/// DRAINING, so the shell must keep what it reads — `TakeSessionResults` empties the queue and a
/// second call returns nothing. `RefiiNetSessionSearch` starts a fresh sweep. Records the
/// caller's buffer could not hold are carried to the next call, never lost.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetSessionResults(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const rex::net::RexNet::SessionResult& result : net->TakeSessionResults()) {
        Field(text, PeerText(result.host), '\t');
        Field(text, HexOf(result.session_id), '\t');
        FieldNum(text, result.slots_total, '\t');
        FieldNum(text, result.slots_open, '\t');
        FieldNum(text, result.requires_invite ? 1u : 0u, '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

REFII_HOST_ABI_EXPORT int RefiiNetSessionSearch(void) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    net->SessionSearch();
    return 1;
#else
    return 0;
#endif
}

/// Are we hosting, and will we admit a joiner? `hosting \t joinable`.
///
/// BOTH BYTES LIVE ON `g_XrnmNetContext` (`*(0x83497490)`), and both are read there by the
/// host's own admit test in `sub_822EDCF0`:
///
///     822edecc  lwz    r11, g_XrnmNetContext@l(r25)
///     822eded0  lbz    r10, 0xF3(r11)      ; hosting
///     822eded8  beq    cr6, loc_822EE5B0   ; not hosting -> refuse
///     822ededc  lbz    r11, 0xF5(r11)      ; joinable
///
/// +0xF3 is the session machinery's to set; +0xF5 is the one the title's own
/// `XboxLive.SetSessionJoinable` writes (`sub_8232CD28` is `*(ctx + 245) = a2`, and 245 = 0xF5).
REFII_HOST_ABI_EXPORT uint32_t RefiiNetHostState(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::memory::Memory* memory = SafeKernelMemory();
    uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t context = NetContext(base);
    if (context == 0u) {
        return 0;
    }

    std::string row;
    Field(row, GuestByteAt(base, context + kNetHostingByte) != 0u ? "1" : "0", '\t');
    Field(row, GuestByteAt(base, context + kNetJoinableByte) != 0u ? "1" : "0", '\n');
    return Emit(row, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Opens or closes our session to joiners — the byte `XboxLive.SetSessionJoinable` writes.
///
/// WRITTEN WHERE THE GAME READS IT rather than through the native: `sub_8232CD28` takes the
/// context bound at native registration, and that binding is not proved to be
/// `g_XrnmNetContext`. The admit test's own base is, so the byte is set there.
/// States this player's status to everyone who sees their presence (empty clears it).
REFII_HOST_ABI_EXPORT int RefiiNetSetPlayerStatus(const char* status) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }
    std::string text = status != nullptr ? status : "";
    for (char& c : text) {
        if (c == '\t' || c == '\n' || c == '\r') {
            c = ' ';
        }
    }
    net->SetPlayerStatus(text);
    return 1;
#else
    (void)status;
    return 0;
#endif
}

/// Sends a signal (a greeting, by id 1..255) to everyone who sees this player's presence.
REFII_HOST_ABI_EXPORT int RefiiNetSendPlayerSignal(int id) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr || id <= 0 || id > 255) {
        return 0;
    }
    net->SendPlayerSignal(static_cast<uint8_t>(id));
    return 1;
#else
    (void)id;
    return 0;
#endif
}

REFII_HOST_ABI_EXPORT int RefiiNetSetJoinable(int on) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::memory::Memory* memory = SafeKernelMemory();
    uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t context = NetContext(base);
    if (context == 0u) {
        return 0;
    }

    const uint8_t value = on != 0 ? 1u : 0u;
    return StoreGuest(base, context + kNetJoinableByte, &value, sizeof(value)) ? 1 : 0;
#else
    (void)on;
    return 0;
#endif
}

/// Reads a `REXN-…` code without acting on it: `name \t peerId`. 0 when it is not one.
///
/// A PURE PARSE, so it answers with the node down: the code carries the peer id and the name
/// inside it, which is what lets the shell say who it is about to add before it sends anything.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPreviewCode(const char* code, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    if (code == nullptr || code[0] == '\0') {
        return 0;
    }

    RexNetPeerId peer{};
    char name[128] = {0};
    if (!rexnet_friend_code_parse(code, &peer, name, sizeof(name))) {
        return 0;
    }

    std::string row;
    Field(row, name, '\t');
    Field(row, PeerText(peer), '\n');
    return Emit(row, out, capacity);
#else
    (void)code;
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// THE NAME TO SHOW FOR A PEER (RexNet §17.3.7–§17.3.8): a friend's own display name; anyone else
/// is shown the pseudonym every observer derives from their peer id ("Curious Cat"), never a name
/// they asserted. `name` in one record, 0 when the id is not one.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPeerName(const char* peer_text, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    RexNetPeerId peer{};
    if (!ParsePeer(peer_text, peer)) {
        return 0;
    }

    std::string name;
    rex::net::RexNet* net = Net();
    if (net != nullptr && net->IsFriend(peer)) {
        for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
            if (SamePeerId(friend_info.peer, peer)) {
                name = friend_info.display_name;
                break;
            }
        }
    }
    if (name.empty()) {
        name = rex::net::Pseudonym(peer);
    }

    std::string row;
    Field(row, name, '\n');
    return Emit(row, out, capacity);
#else
    (void)peer_text;
    (void)out;
    (void)capacity;
    return 0;
#endif
}

extern "C" uint32_t RefiiCoopChannelPeer(void);

/// The online co-op partner's name to show, by the same rule: the connection the co-op channel
/// talks to (`RefiiCoopChannelPeer`), named by RexNet's own snapshot (a friend's display name, else
/// the peer's pseudonym; §17.2). `name` in one record, 0 when no partner is known.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPartnerName(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    const uint32_t partner = RefiiCoopChannelPeer();
    if (net == nullptr || partner == 0u) {
        return 0;
    }

    for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
        if (connection.virtual_ip == partner && !connection.is_relay) {
            std::string row;
            Field(row, connection.display_name.empty() ? rex::net::Pseudonym(connection.peer)
                                                       : connection.display_name,
                  '\n');
            return Emit(row, out, capacity);
        }
    }
    return 0;
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Sends a friend request to a `REXN-…` code. The code carries the peer's own display name, so
/// the shell can show who it is about to add before committing.
REFII_HOST_ABI_EXPORT int RefiiNetAddFriendByCode(const char* code, const char* note) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr || code == nullptr) {
        return 0;
    }

    RexNetPeerId peer{};
    if (!rexnet_friend_code_parse(code, &peer, nullptr, 0)) {
        return 0;
    }

    // 2 = that is OUR OWN code. Two installs made by copying one share identity.key, so
    // the other machine's code is ours and nothing could ever connect; said on the page
    // rather than left as a request that waits forever.
    if (std::memcmp(&peer, &net->local_peer_id(), sizeof(peer)) == 0) {
        return 2;
    }
    if (!NetStillUp(net)) {
        return 0;
    }

    net->FriendRequest(peer, note == nullptr ? std::string() : std::string(note));
    return 1;
#else
    (void)code;
    (void)note;
    return 0;
#endif
}

/// `action`: 0 accept request, 1 dismiss request, 2 remove friend, 3 accept invite,
/// 4 decline invite, 5 invite this peer to our session, 6 join this peer's session.
///
/// 3 AND 6 GO THROUGH `AcceptInvite`, NOT `InviteReply`. Only `AcceptInvite` arms
/// `pending_accept_`; `InviteReply` just answers the sender. Without the arm, the session
/// descriptor that arrives next never fills `accepted_invite_`, no `kXNotifyLiveInviteAccepted`
/// is raised, and the guest's `XInviteGetAcceptedInfo` fails — the reply is sent and nothing
/// joins.
REFII_HOST_ABI_EXPORT int RefiiNetPeerAction(const char* peer_text, int action) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    RexNetPeerId peer{};
    if (net == nullptr || !ParsePeer(peer_text, peer)) {
        return 0;
    }

    switch (action) {
        case 0: net->AcceptFriendRequest(peer); return 1;
        case 1: net->DismissFriendRequest(peer); return 1;
        case 2: net->FriendRemove(peer); return 1;
        case 4: net->DeclineInvite(peer); return 1;
        case 5: net->SendInvite(peer); return 1;

        case 3: {
            // The pending invite carries the session id the arm needs, so it is looked up
            // rather than rebuilt: an accept for an invite that is no longer offered must fail.
            for (const rex::net::RexNet::InviteInfo& invite : net->PendingInvites()) {
                if (SamePeerId(invite.peer, peer)) {
                    if (!NetStillUp(net)) {
                        return 0;
                    }
                    net->AcceptInvite(invite);
                    return 1;
                }
            }

            return 0;
        }

        case 6: {
            // AN UNINVITED JOIN IS AN INVITE WE RAISE FOR OURSELVES. The material is the same —
            // the host peer and the 16-byte session id — and presence already carries both for
            // a friend who is hosting, so nothing new has to be resolved.
            std::optional<rex::net::RexNet::PresenceInfo> presence = net->GetPresence(peer);
            if (!presence.has_value() || !presence->has_session || !NetStillUp(net)) {
                return 0;
            }

            // ALREADY IN THAT GAME: the press is answered as the join it already is.
            if (GuestAlreadyJoined(net, peer, presence->session_id)) {
                REXLOG_INFO(
                    "RefiiNetPeerAction: join to the session the guest is already in; not re-armed");
                return 1;
            }
            if (!NetStillUp(net)) {
                return 0;
            }

            rex::net::RexNet::InviteInfo self_invite;
            self_invite.peer = peer;
            self_invite.title_id = presence->title_id;
            self_invite.session_id = presence->session_id;
            net->AcceptInvite(self_invite);

            // The arm resolves on the HOST'S DESCRIPTOR, which arrives as a search result — so
            // the search is part of the join, not a separate step the player has to take.
            if (!NetStillUp(net)) {
                return 0;
            }
            net->SessionSearch();
            return 1;
        }

        default: return 0;
    }
#else
    (void)peer_text;
    (void)action;
    return 0;
#endif
}

/// Our session's 16-byte id as 32 hex digits, or 0 when we are not hosting one. With the peer
/// id, it is the whole of what a joiner needs — the material an invite carries.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetOwnSession(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::optional<std::array<uint8_t, 16>> session = net->current_session_id();
    if (!session.has_value()) {
        return 0;
    }

    std::string hex;
    for (uint8_t byte : *session) {
        char pair[3];
        std::snprintf(pair, sizeof(pair), "%02x", byte);
        hex += pair;
    }
    return Emit(hex, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Joins a session from the material itself — host peer, title id and the session id as
/// `RefiiNetOwnSession` writes it — rather than from a friend's presence. The uninvited join
/// above (action 6) with the lookup removed: a Discord invite carries all three, and the host
/// need not be a friend yet. 0 when the node is down or the material does not parse.
///
/// A JOIN THROUGH DISCORD MAKES A FRIEND. Unless they already are one, the host is sent a
/// friend request whose note is `discord:<session>`: the session id is the proof that the
/// request came with the secret, and the host's shell accepts a request so marked while that
/// session is its own. Mutual consent then completes as for any request.
REFII_HOST_ABI_EXPORT int RefiiNetJoinSession(const char* peer_text, uint32_t title_id,
                                              const char* session_hex) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    RexNetPeerId peer{};
    std::array<uint8_t, 16> session_id{};
    if (net == nullptr || !ParsePeer(peer_text, peer) || !ParseHex16(session_hex, session_id)) {
        return 0;
    }

    // ALREADY IN THAT GAME: the same refusal as action 6, so neither route re-arms a join the
    // guest is in.
    if (GuestAlreadyJoined(net, peer, session_id)) {
        REXLOG_INFO("RefiiNetJoinSession: join to the session the guest is already in; not re-armed");
        return 1;
    }
    if (!NetStillUp(net)) {
        return 0;
    }

    // ONE JOIN, THE NODE'S. The three fields are the first cut's secret; the node reads that
    // form as well as its own, and does everything a join needs (dial, arm, search, befriend,
    // shard) in one place for every host, Discord's included.
    char material[256];
    std::snprintf(material, sizeof(material), "%s|%08X|%s", peer_text, title_id, session_hex);
    return net->JoinFromSecret(material) ? 1 : 0;
#else
    (void)peer_text;
    (void)title_id;
    (void)session_hex;
    return 0;
#endif
}

/// What Discord shows for us: the two lines, and whether a joiner is admitted right now
/// (-1 = whenever a local session exists). The node owns the Discord connection, the party and
/// the join secret; this only states the text.
REFII_HOST_ABI_EXPORT void RefiiNetDiscordActivity(const char* details, const char* state,
                                                   int joinable) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return;
    }
    net->SetDiscordActivity(details == nullptr ? std::string() : std::string(details),
                            state == nullptr ? std::string() : std::string(state),
                            joinable < 0 ? std::nullopt : std::optional<bool>(joinable != 0));
#else
    (void)details;
    (void)state;
    (void)joinable;
#endif
}

/// Rich Presence on or off, the player's setting. Off clears it at Discord.
REFII_HOST_ABI_EXPORT void RefiiNetDiscordEnabled(int enabled) {
#if defined(REXGLUE_ENABLE_REXNET)
    if (rex::net::RexNet* net = Net()) {
        net->SetDiscordEnabled(enabled != 0);
    }
#else
    (void)enabled;
#endif
}

/// One record per Discord "ask to join" the node is holding for an answer: `userId \t userName`.
/// Drained on read; a record the buffer could not hold is carried to the next call.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetJoinRequests(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const rex::net::RexNet::JoinRequest& request : net->TakeJoinRequests()) {
        Field(text, request.user_id, '\t');
        Field(text, request.user_name, '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Answers one join request by the Discord user id it named. 0 when the node is down.
REFII_HOST_ABI_EXPORT int RefiiNetJoinRequestReply(const char* user_id, int accept) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr || user_id == nullptr || user_id[0] == '\0') {
        return 0;
    }

    net->ReplyJoinRequest(std::string(user_id), accept != 0);
    return 1;
#else
    (void)user_id;
    (void)accept;
    return 0;
#endif
}

// --- Refii's own co-op channel ------------------------------------------
//
// Two Refii instances, one co-op session, and state that is OURS: which body player two is in,
// and whatever the native-hero payload grows into. It rides port 0xEF11 rather than any packet
// the title would recognise, so the guest wire is untouched.
//
// Delivery is deliberately unreliable and the messages are deliberately IDEMPOTENT STATE, not
// events — "player two's body is X", restated while the screen is up. A lost datagram costs one
// restatement, which is why this needs no acking, no sequencing and no reliability flag.
//
// The receive half lives in the shadowed XAM net layer, where the datagram sink already sees
// every inbound packet; see `rexglue-net/shadow/rexglue-sdk/src/kernel/xam/xam_net.cpp`.
extern "C" uint16_t RefiiCoopChannelPort(void);
extern "C" uint32_t RefiiCoopChannelPeer(void);
extern "C" uint32_t RefiiCoopChannelTake(char* out, uint32_t capacity);

/// The HeroState version the pump last applied, in full, to player 1 or 2's current body — 0
/// when none has, after a reset, after that body was dropped, or when the guest memory is not
/// up. Godot acks the joiner's HeroState with it; the joiner stops resending at the ack.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroStateApplied(uint32_t player) {
    if (SafeKernelMemory() == nullptr) {
        return 0u;
    }
    return refii::vfs::GuestTickPumpHeroStateApplied(player);
}

/// Sends one message to the co-op peer. Returns 0 when RexNet is down or no peer has been seen.
REFII_HOST_ABI_EXPORT int RefiiNetCoopSend(const char* text) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    const uint32_t peer_vip = RefiiCoopChannelPeer();
    static std::atomic<int> last_outcome{-1};  // -1 never, 0 refused, 1 sent; logged on change
    if (net == nullptr || peer_vip == 0 || text == nullptr || text[0] == '\0') {
        if (last_outcome.exchange(0) != 0) {
            REXLOG_INFO("Refii co-op channel: send refused — net {}, peer vip {:08X}",
                        net == nullptr ? "MISSING" : "up", peer_vip);
        }
        return 0;
    }

    const uint16_t port = RefiiCoopChannelPort();
    net->SendDatagram(peer_vip, port, port, text, static_cast<uint32_t>(std::strlen(text)));
    if (last_outcome.exchange(1) != 1) {
        REXLOG_INFO("Refii co-op channel: sending to 10.77.{}.{} port {}", (peer_vip >> 8) & 0xFF,
                    peer_vip & 0xFF, port);
    }
    return 1;
#else
    (void)text;
    return 0;
#endif
}

/// Takes the oldest queued message. Returns the byte count, 0 when nothing is waiting.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetCoopReceive(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    return RefiiCoopChannelTake(out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}
}  // extern "C"
