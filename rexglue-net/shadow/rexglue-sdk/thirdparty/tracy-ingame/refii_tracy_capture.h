#ifndef REFII_TRACY_CAPTURE_H
#define REFII_TRACY_CAPTURE_H

// THE GAME'S OWN TRACY CAPTURE. The profiler client and Tracy's analysis Worker run in this
// library and talk over the in-process transport (TracySocket.hpp); a capture is a Worker
// connected to the client. Nothing leaves the process: the dev menu reads the Worker through
// these calls, and Save writes a .tracy file only where it is told to.
//
// One capture at a time. Start discards a stopped capture and begins a new one; Stop ends the
// stream but keeps the data for browsing and saving; Discard frees it.
//
// QUERIES read the held capture, live or stopped, under the Worker's own main-thread lock. Times
// are Tracy's clock in nanoseconds. Strings come back in fixed-stride slots (stride includes the
// NUL; longer text is truncated). Each returns how many items it wrote.

#include <stdint.h>

#include "common/TracyApi.h"

#ifdef __cplusplus
extern "C" {
#endif

enum RefiiTracyCaptureState
{
    REFII_TRACY_CAPTURE_NONE = 0,       // no capture held
    REFII_TRACY_CAPTURE_CONNECTING = 1, // Worker up, client not yet streaming
    REFII_TRACY_CAPTURE_LIVE = 2,       // streaming
    REFII_TRACY_CAPTURE_STOPPED = 3,    // stream ended (Stop, memory limit or failure); data held
};

typedef struct RefiiTracyCaptureStatus
{
    int32_t state;          // RefiiTracyCaptureState
    int32_t failure;        // tracy::Worker::Failure; 0 = none
    uint64_t frames;        // frames of the default frame set
    uint64_t zones;         // CPU zones recorded
    int64_t first_ns;       // capture span, Tracy's clock
    int64_t last_ns;
    int64_t memory_bytes;   // the Worker's own allocation
    int64_t memory_limit;   // the limit it was started with; <= 0 = none
    uint64_t samples;       // CPU samples with call stacks
    int32_t context_switches; // 1 when the capture holds context switches
    int32_t system_tracing; // 1 when the capture asked for sampling and context switches
} RefiiTracyCaptureStatus;

// ---- capture -------------------------------------------------------------------------------

// Starts a capture. The profiler client must already be up — the game starts it at launch, and
// only when Tracy is enabled — so this never brings Tracy into a process that did not ask for it.
// `system_tracing` adds CPU sampling with callstacks and context switches for the game's own
// threads (Windows needs the process elevated; without it that data is simply empty).
// 1 = started; 0 = the client is not running, or a capture is already live.
TRACY_API int RefiiTracyCaptureStart( int64_t memory_limit_bytes, int system_tracing );

// Ends the live capture's stream; its data stays readable.
TRACY_API void RefiiTracyCaptureStop( void );

// Frees the held capture.
TRACY_API void RefiiTracyCaptureDiscard( void );

TRACY_API void RefiiTracyCaptureGetStatus( RefiiTracyCaptureStatus* out );

// 1 while a capture is streaming. One atomic load: what per-frame feeds check before doing work.
TRACY_API int RefiiTracyCaptureIsLive( void );

// Writes the stopped capture as a .tracy file (zstd), once its stream has fully ended. 1 =
// written; 0 = nothing stopped to write, or the file could not be opened.
TRACY_API int RefiiTracyCaptureSave( const char* path );

// ---- crash ---------------------------------------------------------------------------------

// Where a crash writes the live capture (crash-<date>-<time>.tracy). Also starts the saver
// thread that does the work, so the crash path itself only signals and waits.
TRACY_API void RefiiTracyCaptureSetCrashDirectory( const char* directory );

// Called from a crash handler: if a capture is live, has the saver thread stop and save it, and
// waits up to `timeout_ms` for that. Async-signal-safe in itself (an atomic, sem_post/SetEvent
// and sleeps); the save runs on the saver thread, and if the crashed thread holds something the
// save needs, the wait simply runs out. 1 = saved.
TRACY_API int RefiiTracyCaptureSaveOnCrash( uint32_t timeout_ms );

// ---- queries -------------------------------------------------------------------------------

// Frames of the default frame set from `first` (an on-demand capture's first two frames predate
// the connection and are skipped). *out_total = every frame.
TRACY_API uint32_t RefiiTracyGetFrames( uint64_t first, uint32_t capacity, int64_t* out_start,
                                        int64_t* out_end, uint64_t* out_total );

// Threads, in the Worker's order. Flags: bit0 zones, bit1 samples, bit2 context switches,
// bit3 fiber.
TRACY_API uint32_t RefiiTracyGetThreads( uint32_t capacity, uint64_t* out_id, uint32_t* out_flags,
                                         char* out_names, uint32_t name_stride );

// A thread's zones overlapping [t0, t1], depth first (parents before children), at most
// `max_depth` levels. Zones shorter than `min_ns` are left out with everything under them.
TRACY_API uint32_t RefiiTracyGetZones( uint64_t thread, int64_t t0, int64_t t1, int64_t min_ns,
                                       uint32_t max_depth, uint32_t capacity, int64_t* out_start,
                                       int64_t* out_end, int16_t* out_srcloc, uint8_t* out_depth );

// A zone's source location: display name, function, file, line, colour (0 = none). 1 = found.
TRACY_API int RefiiTracyGetSourceLocation( int16_t srcloc, char* out_name, uint32_t name_size,
                                           char* out_function, uint32_t function_size,
                                           char* out_file, uint32_t file_size,
                                           uint32_t* out_line, uint32_t* out_color );

// One row per source location with zones: count, total, min, max, median, 95th percentile and
// self (exclusive) total. Percentiles walk every zone of the location.
TRACY_API uint32_t RefiiTracyGetZoneStats( uint32_t capacity, int16_t* out_srcloc,
                                           uint64_t* out_count, int64_t* out_total,
                                           int64_t* out_min, int64_t* out_max,
                                           int64_t* out_median, int64_t* out_p95,
                                           int64_t* out_self_total );

// As RefiiTracyGetZoneStats, limited to zones starting in [t0, t1] and, when `thread` is not 0, to
// that thread. Self time is each instance minus its direct children.
TRACY_API uint32_t RefiiTracyGetZoneStatsRange( uint64_t thread, int64_t t0, int64_t t1,
                                                uint32_t capacity, int16_t* out_srcloc,
                                                uint64_t* out_count, int64_t* out_total,
                                                int64_t* out_min, int64_t* out_max,
                                                int64_t* out_median, int64_t* out_p95,
                                                int64_t* out_self_total );

// FIND ZONE: one source location's instances starting in [t0, t1] (one thread, or all when
// `thread` is 0). Fills a histogram of `buckets` bins between the shortest and longest instance —
// log-spaced when `log_scale` — and the count, total, median and 95th percentile; writes the
// `slowest_capacity` longest instances, longest first (start, end, thread) and returns how many.
TRACY_API uint32_t RefiiTracyFindZone( int16_t srcloc, uint64_t thread, int64_t t0, int64_t t1,
                                       uint32_t buckets, int log_scale, uint64_t* out_histogram,
                                       int64_t* out_min, int64_t* out_max, uint64_t* out_count,
                                       int64_t* out_total, int64_t* out_median, int64_t* out_p95,
                                       uint32_t slowest_capacity, int64_t* out_slow_start,
                                       int64_t* out_slow_end, uint64_t* out_slow_thread );

// SAMPLE CALL TREE over the samples taken in [t0, t1] (one thread, or all when `thread` is 0):
// one node per distinct chain of functions, inline frames included. Top-down (`inverted` 0) roots
// are the outermost callers; bottom-up (1) roots are the functions the samples landed in, with
// their callers beneath. Nodes come depth first, each node's children by most samples first;
// nodes under `min_samples` are left out with everything beneath. Per node: parent index (-1 for
// a root), depth, function address, samples through it, samples that ended in it, name.
// *out_samples = samples in the range.
TRACY_API uint32_t RefiiTracyGetSampleTree( uint64_t thread, int64_t t0, int64_t t1, int inverted,
                                            uint32_t min_samples, uint32_t capacity,
                                            int32_t* out_parent, uint8_t* out_depth,
                                            uint64_t* out_addr, uint32_t* out_total,
                                            uint32_t* out_self, char* out_names,
                                            uint32_t name_stride, uint64_t* out_samples );

// LOCKS: one row per lock acquired in [t0, t1] — name, threads that used it, acquisitions, how
// many waited on another holder (contended), total and longest wait, total and longest hold, and
// when the longest wait began (to open it in the timeline).
TRACY_API uint32_t RefiiTracyGetLocks( int64_t t0, int64_t t1, uint32_t capacity, char* out_names,
                                       uint32_t name_stride, uint32_t* out_threads,
                                       uint64_t* out_acquired, uint64_t* out_contended,
                                       int64_t* out_wait_total, int64_t* out_wait_max,
                                       int64_t* out_hold_total, int64_t* out_hold_max,
                                       int64_t* out_worst_wait_at );

// MEMORY POOLS (Tracy's named memory): name, allocations made, allocations live, bytes live, the
// peak, and the capture's largest live allocations come from RefiiTracyGetMemoryLargest.
TRACY_API uint32_t RefiiTracyGetMemoryPools( uint32_t capacity, char* out_names, uint32_t name_stride,
                                             uint64_t* out_allocations, uint64_t* out_live,
                                             uint64_t* out_bytes, uint64_t* out_peak );

// The named pool's largest live allocations, largest first: id (the pointer or handle it was
// reported with), size, when it was made.
TRACY_API uint32_t RefiiTracyGetMemoryLargest( const char* pool, uint32_t capacity, uint64_t* out_id,
                                               uint64_t* out_size, int64_t* out_time );

// Plots: name, tracy::PlotType, tracy::PlotValueFormatting, point count, min, max.
TRACY_API uint32_t RefiiTracyGetPlots( uint32_t capacity, char* out_names, uint32_t name_stride,
                                       uint8_t* out_type, uint8_t* out_format,
                                       uint64_t* out_count, double* out_min, double* out_max );

// A plot's points in [t0, t1]. More than `capacity` are reduced to each bucket's minimum and
// maximum, in time order, so a spike survives.
TRACY_API uint32_t RefiiTracyGetPlotData( uint32_t plot, int64_t t0, int64_t t1, uint32_t capacity,
                                          int64_t* out_time, double* out_value );

// Messages from `first`: time, thread, colour (0 = none), text. *out_total = every message.
TRACY_API uint32_t RefiiTracyGetMessages( uint64_t first, uint32_t capacity, int64_t* out_time,
                                          uint64_t* out_thread, uint32_t* out_color,
                                          char* out_text, uint32_t text_stride,
                                          uint64_t* out_total );

// GPU contexts: name and zone count.
TRACY_API uint32_t RefiiTracyGetGpuContexts( uint32_t capacity, char* out_names,
                                             uint32_t name_stride, uint64_t* out_zones );

// A GPU context's zones overlapping [t0, t1], as RefiiTracyGetZones (GPU time, on the same clock).
TRACY_API uint32_t RefiiTracyGetGpuZones( uint32_t context, int64_t t0, int64_t t1, int64_t min_ns,
                                          uint32_t max_depth, uint32_t capacity,
                                          int64_t* out_start, int64_t* out_end,
                                          int16_t* out_srcloc, uint8_t* out_depth );

// When a thread was running in [t0, t1], from its context switches, and on which core.
TRACY_API uint32_t RefiiTracyGetRunning( uint64_t thread, int64_t t0, int64_t t1, uint32_t capacity,
                                         int64_t* out_start, int64_t* out_end, uint8_t* out_cpu );

// Cores with context-switch data.
TRACY_API uint32_t RefiiTracyGetCpuCount( void );

// What a core ran in [t0, t1]: a game thread's id, 0xFFFFFFFE for every other process.
TRACY_API uint32_t RefiiTracyGetCpuSlices( uint32_t cpu, int64_t t0, int64_t t1, uint32_t capacity,
                                           int64_t* out_start, int64_t* out_end,
                                           uint64_t* out_thread );

// Sampled functions, most exclusive samples first: address, inclusive samples (in the function
// or anything it called), exclusive samples (in the function itself), name, file, line.
TRACY_API uint32_t RefiiTracyGetSampledSymbols( uint32_t capacity, uint64_t* out_addr,
                                                uint32_t* out_incl, uint32_t* out_excl,
                                                char* out_names, uint32_t name_stride,
                                                char* out_files, uint32_t file_stride,
                                                uint32_t* out_line );

// A sampled function's machine code, disassembled with capstone, one instruction per line:
// address, samples that landed on it, "mnemonic operands". 0 = no code transferred for it.
TRACY_API uint32_t RefiiTracyDisassemble( uint64_t symbol, uint32_t capacity, uint64_t* out_addr,
                                          uint32_t* out_samples, char* out_text,
                                          uint32_t text_stride );

#ifdef __cplusplus
}
#endif

#endif
