#pragma once

// refii — the host bridge's shared core, as a title's bridge sees it.
//
// refii_hostcore carries every export of the C ABI Godot binds that is the same for any title:
// the ABI version, the guest's lifecycle, the debug channel, audio, input, time, mods, player
// state, achievements and RexNet. A title's librefii_hostbridge is this library linked whole
// plus the title's own feeds, which reach the guest through the helpers declared here.
//
// THE TITLE PLUGS INTO THE LIFECYCLE through TitleCallbacks, registered once at load (a
// namespace-scope initialiser in the title's bridge). Every member may be null.
//
// INTERNAL TO THE BRIDGE LIBRARY. Hidden on ELF, so a title's bridge exports exactly the C ABI
// and nothing here appears in its dynamic symbol table.

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/ui/windowed_app.h>

#include "hosted_app_context.h"

#ifndef _WIN32
#include <setjmp.h>
#endif

#if defined(REXGLUE_ENABLE_REXNET)
#include <array>
#include <optional>

#include <rex/net/rexnet.h>
#include <rex/net/rexnet_ffi.h>
#endif

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility push(hidden)
#endif

namespace refii::hostcore {

/// What the title adds to the guest's lifecycle and the few core exports whose answer is the
/// title's. Registered once with RegisterTitle; a null member is a title that has nothing there.
struct TitleCallbacks {
    /// RefiiHostInitialize, once the guest's OnInitialize succeeded, the last-resort fault
    /// reporter is installed and the timer resolution raised; before it returns.
    void (*on_initialized)();
    /// Each RefiiHostStep, before the UI thread's pending work runs.
    void (*before_step)();
    /// Each RefiiHostStep, after the UI thread's pending work and before the quit check.
    void (*after_step)();
    /// First thing in RefiiHostShutdown, whether or not the guest was initialised: the
    /// title's own threads stop here.
    void (*on_shutdown)();
    /// The guest addresses of the device's vertex and pixel shader constant shadows (256
    /// float4 each), for the debug channel's probe snapshot. False when there is no device.
    bool (*constant_shadows)(const uint8_t* base, uint32_t* out_vs, uint32_t* out_ps);
    /// The guest's own statement that it is in a session as a joiner, for the RexNet join
    /// routes' "already in that game" refusal. Asked only when the accepted invite names the
    /// host and session being joined.
    bool (*guest_joined_session)(const uint8_t* base);
};

void RegisterTitle(const TitleCallbacks& callbacks);
const TitleCallbacks& Title();

/// Guest memory, or nullptr when there is no guest — the check `rex::system::kernel_memory`
/// does not make. Its accessor is `kernel_state()->memory()` with no null test, so with no
/// kernel state the arrow faults inside the accessor, before any `memory == nullptr` guard in
/// the bridge can run. Every call in the bridge goes through here.
inline rex::memory::Memory* SafeKernelMemory() {
    rex::system::KernelState* state = rex::system::kernel_state();
    return state != nullptr ? state->memory() : nullptr;
}

/// Copies into the caller's buffer and returns the length that WOULD have been written, so a
/// caller can size a buffer with a null probe. Mirrors the convention the launcher ops ABI
/// already uses.
int WriteOut(const std::string& text, char* out, std::size_t capacity);

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

GuestSession& Session();

/// The guest when GODOT drives it, rather than the guest driving itself.
///
/// The difference matters. RunRefiiEmbedded owns a blocking SDL loop — on netplay,
/// SDL_WaitEvent — so hosting it means handing a thread over and getting it back when
/// the guest decides. Here the host keeps its thread: it initialises the guest once,
/// then pumps it a slice at a time from its own frame loop, and the guest's context is
/// that queue alone (HostedAppContext), with no SDL under it.

/// How long shutdown pumps the UI thread waiting for the guest to stop.
///
/// Generous on purpose: this runs once, when the player closes the game, and the
/// alternative to waiting is abandoning the runtime for the rest of the process.
constexpr std::chrono::seconds kShutdownPumpBudget{15};

struct HostedGuest {
    std::unique_ptr<HostedAppContext> context;
    std::unique_ptr<rex::ui::WindowedApp> app;
    bool initialized = false;
    bool finished = false;

    std::string status{"not initialized"};
};

HostedGuest& Hosted();

/// Copies `size` guest bytes at `address` into `out`, or zero-fills and returns false when
/// they are not readable. The one read primitive everything else is built on: guest pages
/// may be reserved, decommitted or freed, and a host read of one must not take the process
/// down (see guest_access.cpp).
bool CopyGuest(const uint8_t* base, uint32_t address, void* out, std::size_t size);

/// One guest byte, or 0 when it is not readable.
uint8_t GuestByteAt(const uint8_t* base, uint32_t address);

/// One guest word, big-endian, or 0 when it is not readable.
uint32_t GuestWordAt(const uint8_t* base, uint32_t address);

/// One guest halfword, big-endian, or 0 when it is not readable.
uint16_t GuestHalfAt(const uint8_t* base, uint32_t address);

/// One guest doubleword, big-endian, or 0 when it is not readable.
uint64_t GuestWord64At(const uint8_t* base, uint32_t address);

/// CopyGuest a 4 KiB page at a time (guest_span.h): an unreadable page zeroes only its own bytes.
/// False when any page was not readable.
bool CopyGuestPages(const uint8_t* base, uint32_t address, void* out, std::size_t size);

/// Guest memory as the spans of guest_span.h read it.
struct GuestReader {
    const uint8_t* base;

    void Copy(uint32_t address, void* out, std::size_t size) const { CopyGuestPages(base, address, out, size); }
    uint32_t Word(uint32_t address) const { return GuestWordAt(base, address); }
    uint8_t Byte(uint32_t address) const { return GuestByteAt(base, address); }
};

/// Stores `size` bytes into guest memory, or returns false when the page is not there. The
/// few host-side writes are latches on objects the guest handed over, so a miss is a stale
/// pointer, not a reason to fault.
bool StoreGuest(uint8_t* base, uint32_t address, const void* value, std::size_t size);

void SetEnvironmentValue(const char* name, const char* value);

#ifdef _WIN32
/// Non-zero while a guarded read is in flight, so the trace handler does not report the fault
/// that path exists to absorb.
extern thread_local int g_guarded_guest_reads;
#else
/// The guard the faulting thread published, or null. Called from the signal handler.
sigjmp_buf* GuardForFaultingThread();

namespace fault {
/// After the runtime is destroyed: puts the last-resort reporter back over whatever the
/// runtime's teardown restored.
void Reinstall();
}  // namespace fault
#endif

/// Installs the fault reporter once the guest is up. No-op on Windows, where the vectored
/// handler already reports faults.
void InstallLastResortFaultHandler();

/// Names a fail-fast before the process dies: a fixed buffer and raw writes, safe inside the
/// SIGABRT handler.
void ReportEmbeddedFailure(const char* kind, const std::string& detail);

void InstallEmbeddedFailureHandlers();

/// Applies the switches that make the guest a simulation server rather than an app.
/// Shared by both entry points so they cannot drift.
void ApplyEmbeddedEnvironment();

/// Newline-separated arguments: an argument can contain spaces (a path).
std::vector<std::string> SplitArguments(const char* packed);

#if defined(REXGLUE_ENABLE_REXNET)
/// Appends one field, stripped of anything that could forge a delimiter.
void Field(std::string& row, const std::string& value, char terminator);
void FieldNum(std::string& row, uint64_t value, char terminator);
std::string PeerText(const RexNetPeerId& peer);
std::string HexOf(const std::array<uint8_t, 16>& id);
/// Are these the same peer? `RexNetPeerId` is a length-prefixed multihash.
bool SamePeerId(const RexNetPeerId& a, const RexNetPeerId& b);
/// The live node, or null when RexNet is off, not built in, or not up yet (see rexnet.cpp).
rex::net::RexNet* Net();
/// The node taken at entry is still the shared instance.
bool NetStillUp(const rex::net::RexNet* net);
/// The standard guest-address bound for the net exports.
bool NetPlausible(uint32_t a);
/// 32 hex digits to a 16-byte id; false on any other input.
bool ParseHex16(const char* text, std::array<uint8_t, 16>& out);
bool ParsePeer(const char* text, RexNetPeerId& out);
/// The whole text NUL-terminated in `out`, or 0 when it does not fit.
uint32_t Emit(const std::string& text, char* out, uint32_t capacity);
#endif

}  // namespace refii::hostcore

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif
