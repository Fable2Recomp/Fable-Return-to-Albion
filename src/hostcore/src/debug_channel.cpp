// refii — the debug channel's shared half: guest memory, threads, backtraces, the sampler,
// kernel objects and exports, perf counters, cvars, the VFS, the arena, Tracy marks, the heap,
// pokes, probes and sockets. Read-only except RefiiHostSetCvar, RefiiHostPoke and the probes;
// the debug server gates every one of these to loopback.

#include <algorithm>
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
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>

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

#include "host_input.h"
#include "refii_host.h"

#include "host_abi.h"
#include "host_core.h"
#include "host_tracy.h"
#include "hang_watchdog.h"
#include "core/traced_mutex.h"
#include "core/audio_state.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"

using namespace refii::hostcore;

extern "C" {
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


// The device's constant shadows, 256 float4 registers per stage, where the title states them
// (TitleCallbacks::constant_shadows).
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
        uint32_t vs = 0u;
        uint32_t ps = 0u;
        const auto shadows = refii::hostcore::Title().constant_shadows;
        if (shadows != nullptr && shadows(base, &vs, &ps)) {
            std::memcpy(slot.snapshot, base + vs, kShadowBytes);
            std::memcpy(slot.snapshot + kShadowBytes, base + ps, kShadowBytes);
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
}  // extern "C"
