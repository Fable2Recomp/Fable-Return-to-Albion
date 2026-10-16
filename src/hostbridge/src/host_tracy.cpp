// THE GAME'S TRACY CAPTURE, for the dev menu: thin exports over refii_tracy_capture.h, which lives
// in the Tracy library beside the in-process client and analysis Worker (see that header for every
// call's contract). Godot reaches Tracy only through these; a build without the profiler answers
// every one with "nothing".

#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_tracy.h"

#ifdef REXGLUE_ENABLE_PROFILING
#include <refii_tracy_capture.h>
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
