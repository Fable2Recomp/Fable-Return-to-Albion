// THE GAME'S TRACY CAPTURE, for the dev menu: thin exports over refii_tracy_capture.h, which lives
// in the Tracy library beside the in-process client and analysis Worker (see that header for every
// call's contract). Godot reaches Tracy only through these; a build without the profiler answers
// every one with "nothing".

#include <cstdint>
#include <cstring>
#include <mutex>
#include <set>
#include <string>

#include "host_abi.h"
#include "host_tracy.h"

#ifdef REXGLUE_ENABLE_PROFILING
#include <refii_tracy_capture.h>
#include <tracy/TracyC.h>

namespace {

/// Pool and lock names live for the process: Tracy keeps the pointers it is handed.
const char* Intern(const char* name) {
    static std::mutex lock;
    static std::set<std::string>* names = new std::set<std::string>();
    std::lock_guard guard(lock);
    return names->insert(name).first->c_str();
}

}  // namespace
#endif

extern "C" {

#ifdef REXGLUE_ENABLE_PROFILING

REFII_HOST_ABI_EXPORT int RefiiHostTracyStart(int64_t memory_limit_bytes, int system_tracing) {
    return RefiiTracyCaptureStart(memory_limit_bytes, system_tracing);
}

REFII_HOST_ABI_EXPORT void RefiiHostTracyStop(void) { RefiiTracyCaptureStop(); }

REFII_HOST_ABI_EXPORT void RefiiHostTracyDiscard(void) { RefiiTracyCaptureDiscard(); }

/// `size` is the caller's struct size: a layout mismatch reads as an empty status, not a
/// corrupted one.
REFII_HOST_ABI_EXPORT void RefiiHostTracyGetStatus(void* out, uint32_t size) {
    if (out == nullptr) return;
    std::memset(out, 0, size);
    if (size != sizeof(RefiiTracyCaptureStatus)) return;
    RefiiTracyCaptureGetStatus(static_cast<RefiiTracyCaptureStatus*>(out));
}

REFII_HOST_ABI_EXPORT int RefiiHostTracySave(const char* path) { return RefiiTracyCaptureSave(path); }

REFII_HOST_ABI_EXPORT int RefiiHostTracyLive(void) { return RefiiTracyCaptureIsLive(); }

/// Named memory for Godot-side pools (textures, render targets, movies): an allocation of `size`
/// bytes known by `id` in `pool`, and its release. Recorded only while a capture streams; a
/// capture that starts later does not see what was allocated before it, which Tracy's on-demand
/// mode accepts.
REFII_HOST_ABI_EXPORT void RefiiHostTraceAlloc(const char* pool, uint64_t id, uint64_t size) {
    if (pool == nullptr || id == 0 || !TracyCIsStarted || !RefiiTracyCaptureIsLive()) return;
    ___tracy_emit_memory_alloc_named(reinterpret_cast<const void*>(id), static_cast<size_t>(size), 0,
                                     Intern(pool));
}

REFII_HOST_ABI_EXPORT void RefiiHostTraceFree(const char* pool, uint64_t id) {
    if (pool == nullptr || id == 0 || !TracyCIsStarted || !RefiiTracyCaptureIsLive()) return;
    ___tracy_emit_memory_free_named(reinterpret_cast<const void*>(id), 0, Intern(pool));
}

/// A Godot-side lock for Tracy's lock view: a handle, 0 when the profiler is not running. The
/// caller pairs every acquisition it reported with RefiiHostTraceLockAfterUnlock, as
/// refii::TracedMutex does (Tracy's on-demand bookkeeping counts them).
REFII_HOST_ABI_EXPORT uint64_t RefiiHostTraceLockAnnounce(const char* name) {
    if (name == nullptr || !TracyCIsStarted) return 0;
    const char* interned = Intern(name);
    auto* where = new ___tracy_source_location_data{interned, interned, "", 0, 0};  // process lifetime
    return reinterpret_cast<uint64_t>(___tracy_announce_lockable_ctx(where));
}

REFII_HOST_ABI_EXPORT int RefiiHostTraceLockBefore(uint64_t lock) {
    return lock != 0 ? ___tracy_before_lock_lockable_ctx(reinterpret_cast<TracyCLockCtx>(lock)) : 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostTraceLockAfter(uint64_t lock) {
    if (lock != 0) ___tracy_after_lock_lockable_ctx(reinterpret_cast<TracyCLockCtx>(lock));
}

REFII_HOST_ABI_EXPORT void RefiiHostTraceLockAfterUnlock(uint64_t lock) {
    if (lock != 0) ___tracy_after_unlock_lockable_ctx(reinterpret_cast<TracyCLockCtx>(lock));
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetLocks(int64_t t0, int64_t t1, uint32_t capacity,
                                                      char* out_names, uint32_t name_stride,
                                                      uint32_t* out_threads, uint64_t* out_acquired,
                                                      uint64_t* out_contended,
                                                      int64_t* out_wait_total,
                                                      int64_t* out_wait_max,
                                                      int64_t* out_hold_total,
                                                      int64_t* out_hold_max,
                                                      int64_t* out_worst_wait_at) {
    return RefiiTracyGetLocks(t0, t1, capacity, out_names, name_stride, out_threads, out_acquired,
                              out_contended, out_wait_total, out_wait_max, out_hold_total,
                              out_hold_max, out_worst_wait_at);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetMemoryPools(uint32_t capacity, char* out_names,
                                                            uint32_t name_stride,
                                                            uint64_t* out_allocations,
                                                            uint64_t* out_live, uint64_t* out_bytes,
                                                            uint64_t* out_peak) {
    return RefiiTracyGetMemoryPools(capacity, out_names, name_stride, out_allocations, out_live,
                                    out_bytes, out_peak);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetMemoryLargest(const char* pool, uint32_t capacity,
                                                              uint64_t* out_id, uint64_t* out_size,
                                                              int64_t* out_time) {
    return RefiiTracyGetMemoryLargest(pool, capacity, out_id, out_size, out_time);
}

REFII_HOST_ABI_EXPORT void RefiiHostTracySetCrashDirectory(const char* directory) {
    RefiiTracyCaptureSetCrashDirectory(directory);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetFrames(uint64_t first, uint32_t capacity,
                                                       int64_t* out_start, int64_t* out_end,
                                                       uint64_t* out_total) {
    return RefiiTracyGetFrames(first, capacity, out_start, out_end, out_total);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetThreads(uint32_t capacity, uint64_t* out_id,
                                                        uint32_t* out_flags, char* out_names,
                                                        uint32_t name_stride) {
    return RefiiTracyGetThreads(capacity, out_id, out_flags, out_names, name_stride);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetZones(uint64_t thread, int64_t t0, int64_t t1,
                                                      int64_t min_ns, uint32_t max_depth,
                                                      uint32_t capacity, int64_t* out_start,
                                                      int64_t* out_end, int16_t* out_srcloc,
                                                      uint8_t* out_depth) {
    return RefiiTracyGetZones(thread, t0, t1, min_ns, max_depth, capacity, out_start, out_end,
                              out_srcloc, out_depth);
}

REFII_HOST_ABI_EXPORT int RefiiHostTracyGetSourceLocation(int16_t srcloc, char* out_name,
                                                          uint32_t name_size, char* out_function,
                                                          uint32_t function_size, char* out_file,
                                                          uint32_t file_size, uint32_t* out_line,
                                                          uint32_t* out_color) {
    return RefiiTracyGetSourceLocation(srcloc, out_name, name_size, out_function, function_size,
                                       out_file, file_size, out_line, out_color);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetZoneStats(uint32_t capacity, int16_t* out_srcloc,
                                                          uint64_t* out_count, int64_t* out_total,
                                                          int64_t* out_min, int64_t* out_max,
                                                          int64_t* out_median, int64_t* out_p95,
                                                          int64_t* out_self_total) {
    return RefiiTracyGetZoneStats(capacity, out_srcloc, out_count, out_total, out_min, out_max,
                                  out_median, out_p95, out_self_total);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetZoneStatsRange(
    uint64_t thread, int64_t t0, int64_t t1, uint32_t capacity, int16_t* out_srcloc,
    uint64_t* out_count, int64_t* out_total, int64_t* out_min, int64_t* out_max, int64_t* out_median,
    int64_t* out_p95, int64_t* out_self_total) {
    return RefiiTracyGetZoneStatsRange(thread, t0, t1, capacity, out_srcloc, out_count, out_total,
                                       out_min, out_max, out_median, out_p95, out_self_total);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyFindZone(
    int16_t srcloc, uint64_t thread, int64_t t0, int64_t t1, uint32_t buckets, int log_scale,
    uint64_t* out_histogram, int64_t* out_min, int64_t* out_max, uint64_t* out_count,
    int64_t* out_total, int64_t* out_median, int64_t* out_p95, uint32_t slowest_capacity,
    int64_t* out_slow_start, int64_t* out_slow_end, uint64_t* out_slow_thread) {
    return RefiiTracyFindZone(srcloc, thread, t0, t1, buckets, log_scale, out_histogram, out_min,
                              out_max, out_count, out_total, out_median, out_p95, slowest_capacity,
                              out_slow_start, out_slow_end, out_slow_thread);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetSampleTree(
    uint64_t thread, int64_t t0, int64_t t1, int inverted, uint32_t min_samples, uint32_t capacity,
    int32_t* out_parent, uint8_t* out_depth, uint64_t* out_addr, uint32_t* out_total,
    uint32_t* out_self, char* out_names, uint32_t name_stride, uint64_t* out_samples) {
    return RefiiTracyGetSampleTree(thread, t0, t1, inverted, min_samples, capacity, out_parent,
                                   out_depth, out_addr, out_total, out_self, out_names, name_stride,
                                   out_samples);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetPlots(uint32_t capacity, char* out_names,
                                                      uint32_t name_stride, uint8_t* out_type,
                                                      uint8_t* out_format, uint64_t* out_count,
                                                      double* out_min, double* out_max) {
    return RefiiTracyGetPlots(capacity, out_names, name_stride, out_type, out_format, out_count,
                              out_min, out_max);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetPlotData(uint32_t plot, int64_t t0, int64_t t1,
                                                         uint32_t capacity, int64_t* out_time,
                                                         double* out_value) {
    return RefiiTracyGetPlotData(plot, t0, t1, capacity, out_time, out_value);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetMessages(uint64_t first, uint32_t capacity,
                                                         int64_t* out_time, uint64_t* out_thread,
                                                         uint32_t* out_color, char* out_text,
                                                         uint32_t text_stride, uint64_t* out_total) {
    return RefiiTracyGetMessages(first, capacity, out_time, out_thread, out_color, out_text,
                                 text_stride, out_total);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetGpuContexts(uint32_t capacity, char* out_names,
                                                            uint32_t name_stride,
                                                            uint64_t* out_zones) {
    return RefiiTracyGetGpuContexts(capacity, out_names, name_stride, out_zones);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetGpuZones(uint32_t context, int64_t t0, int64_t t1,
                                                         int64_t min_ns, uint32_t max_depth,
                                                         uint32_t capacity, int64_t* out_start,
                                                         int64_t* out_end, int16_t* out_srcloc,
                                                         uint8_t* out_depth) {
    return RefiiTracyGetGpuZones(context, t0, t1, min_ns, max_depth, capacity, out_start, out_end,
                                 out_srcloc, out_depth);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetRunning(uint64_t thread, int64_t t0, int64_t t1,
                                                        uint32_t capacity, int64_t* out_start,
                                                        int64_t* out_end, uint8_t* out_cpu) {
    return RefiiTracyGetRunning(thread, t0, t1, capacity, out_start, out_end, out_cpu);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetCpuCount(void) { return RefiiTracyGetCpuCount(); }

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetCpuSlices(uint32_t cpu, int64_t t0, int64_t t1,
                                                          uint32_t capacity, int64_t* out_start,
                                                          int64_t* out_end, uint64_t* out_thread) {
    return RefiiTracyGetCpuSlices(cpu, t0, t1, capacity, out_start, out_end, out_thread);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetSampledSymbols(uint32_t capacity, uint64_t* out_addr,
                                                               uint32_t* out_incl,
                                                               uint32_t* out_excl, char* out_names,
                                                               uint32_t name_stride,
                                                               char* out_files,
                                                               uint32_t file_stride,
                                                               uint32_t* out_line) {
    return RefiiTracyGetSampledSymbols(capacity, out_addr, out_incl, out_excl, out_names,
                                       name_stride, out_files, file_stride, out_line);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyDisassemble(uint64_t symbol, uint32_t capacity,
                                                         uint64_t* out_addr, uint32_t* out_samples,
                                                         char* out_text, uint32_t text_stride) {
    return RefiiTracyDisassemble(symbol, capacity, out_addr, out_samples, out_text, text_stride);
}

#else

REFII_HOST_ABI_EXPORT int RefiiHostTracyStart(int64_t, int) { return 0; }
REFII_HOST_ABI_EXPORT void RefiiHostTracyStop(void) {}
REFII_HOST_ABI_EXPORT void RefiiHostTracyDiscard(void) {}
REFII_HOST_ABI_EXPORT void RefiiHostTracyGetStatus(void* out, uint32_t size) {
    if (out != nullptr) std::memset(out, 0, size);
}
REFII_HOST_ABI_EXPORT int RefiiHostTracySave(const char*) { return 0; }
REFII_HOST_ABI_EXPORT int RefiiHostTracyLive(void) { return 0; }
REFII_HOST_ABI_EXPORT void RefiiHostTraceAlloc(const char*, uint64_t, uint64_t) {}
REFII_HOST_ABI_EXPORT void RefiiHostTraceFree(const char*, uint64_t) {}
REFII_HOST_ABI_EXPORT uint64_t RefiiHostTraceLockAnnounce(const char*) { return 0; }
REFII_HOST_ABI_EXPORT int RefiiHostTraceLockBefore(uint64_t) { return 0; }
REFII_HOST_ABI_EXPORT void RefiiHostTraceLockAfter(uint64_t) {}
REFII_HOST_ABI_EXPORT void RefiiHostTraceLockAfterUnlock(uint64_t) {}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetLocks(int64_t, int64_t, uint32_t, char*, uint32_t,
                                                      uint32_t*, uint64_t*, uint64_t*, int64_t*,
                                                      int64_t*, int64_t*, int64_t*, int64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetMemoryPools(uint32_t, char*, uint32_t, uint64_t*,
                                                            uint64_t*, uint64_t*, uint64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetMemoryLargest(const char*, uint32_t, uint64_t*,
                                                              uint64_t*, int64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT void RefiiHostTracySetCrashDirectory(const char*) {}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetFrames(uint64_t, uint32_t, int64_t*, int64_t*,
                                                       uint64_t* out_total) {
    if (out_total != nullptr) *out_total = 0;
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetThreads(uint32_t, uint64_t*, uint32_t*, char*,
                                                        uint32_t) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetZones(uint64_t, int64_t, int64_t, int64_t,
                                                      uint32_t, uint32_t, int64_t*, int64_t*,
                                                      int16_t*, uint8_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT int RefiiHostTracyGetSourceLocation(int16_t, char*, uint32_t, char*,
                                                          uint32_t, char*, uint32_t, uint32_t*,
                                                          uint32_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetZoneStats(uint32_t, int16_t*, uint64_t*, int64_t*,
                                                          int64_t*, int64_t*, int64_t*, int64_t*,
                                                          int64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetZoneStatsRange(uint64_t, int64_t, int64_t, uint32_t,
                                                               int16_t*, uint64_t*, int64_t*,
                                                               int64_t*, int64_t*, int64_t*,
                                                               int64_t*, int64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyFindZone(int16_t, uint64_t, int64_t, int64_t, uint32_t,
                                                      int, uint64_t*, int64_t* out_min,
                                                      int64_t* out_max, uint64_t* out_count,
                                                      int64_t* out_total, int64_t* out_median,
                                                      int64_t* out_p95, uint32_t, int64_t*,
                                                      int64_t*, uint64_t*) {
    *out_min = *out_max = *out_total = *out_median = *out_p95 = 0;
    *out_count = 0;
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetSampleTree(uint64_t, int64_t, int64_t, int, uint32_t,
                                                           uint32_t, int32_t*, uint8_t*, uint64_t*,
                                                           uint32_t*, uint32_t*, char*, uint32_t,
                                                           uint64_t* out_samples) {
    if (out_samples != nullptr) *out_samples = 0;
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetPlots(uint32_t, char*, uint32_t, uint8_t*,
                                                      uint8_t*, uint64_t*, double*, double*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetPlotData(uint32_t, int64_t, int64_t, uint32_t,
                                                         int64_t*, double*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetMessages(uint64_t, uint32_t, int64_t*, uint64_t*,
                                                         uint32_t*, char*, uint32_t,
                                                         uint64_t* out_total) {
    if (out_total != nullptr) *out_total = 0;
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetGpuContexts(uint32_t, char*, uint32_t, uint64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetGpuZones(uint32_t, int64_t, int64_t, int64_t,
                                                         uint32_t, uint32_t, int64_t*, int64_t*,
                                                         int16_t*, uint8_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetRunning(uint64_t, int64_t, int64_t, uint32_t,
                                                        int64_t*, int64_t*, uint8_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetCpuCount(void) { return 0; }
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetCpuSlices(uint32_t, int64_t, int64_t, uint32_t,
                                                          int64_t*, int64_t*, uint64_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyGetSampledSymbols(uint32_t, uint64_t*, uint32_t*,
                                                               uint32_t*, char*, uint32_t, char*,
                                                               uint32_t, uint32_t*) {
    return 0;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTracyDisassemble(uint64_t, uint32_t, uint64_t*, uint32_t*,
                                                         char*, uint32_t) {
    return 0;
}

#endif

}  // extern "C"

namespace refii_host_tracy {

int SaveOnCrash(uint32_t timeout_ms) {
#ifdef REXGLUE_ENABLE_PROFILING
    return RefiiTracyCaptureSaveOnCrash(timeout_ms);
#else
    (void)timeout_ms;
    return 0;
#endif
}

}  // namespace refii_host_tracy
