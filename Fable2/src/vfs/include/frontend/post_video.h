#pragma once

#include "core/guest_state.h"
#include "vfs_hook_types.h"

#include <cstdint>
#include <chrono>
#include <mutex>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

struct RefiiMainMenuBlockerTrace;

void RecordRefiiMainMenuBlockerEvent(std::string_view kind,
                                     std::string message);
std::mutex& RefiiMainMenuBlockerTraceMutex();
RefiiMainMenuBlockerTrace& RefiiMainMenuBlockerTraceState();
std::string FormatRefiiMainMenuBlockerTraceForLog();

void RecordRefiiPostVideoUiEvent(std::string_view event,
                                 uint32_t lr,
                                 uint32_t result,
                                 uint32_t r3,
                                 uint32_t r4,
                                 uint32_t r5);
void RecordRefiiPostVideoStageTiming(std::string_view stage,
                                     uint32_t lr,
                                     uint32_t result,
                                     int64_t elapsed_ms);
void RecordRefiiStartupUpdateSnapshot(uint8_t* base,
                                      uint32_t update_guest,
                                      uint32_t lr,
                                      uint32_t result);
void RecordRefiiGetCachedHandoffEvent(std::string_view name,
                                      uint32_t lr,
                                      uint32_t stream_guest);
void RecordRefiiRenderProducerScheduleSnapshot(uint8_t* base,
                                               uint32_t owner_guest,
                                               uint32_t lr,
                                               uint32_t source124,
                                               uint32_t source128,
                                               double frame_time);
std::string FormatRefiiPostVideoStageTimingForLog();
std::string FormatRefiiStartupVideoSlotTrace(
    const RefiiStartupVideoItemTrace& slot,
    std::chrono::steady_clock::time_point now);
inline std::string FormatRefiiStartupVideoSlotTrace(
    const RefiiStartupVideoItemTrace& slot) {
  return FormatRefiiStartupVideoSlotTrace(slot, std::chrono::steady_clock::now());
}

std::string FormatRefiiPostVideoHandoffTraceForLog();
bool ShouldTracePostVideoBoundaryProbe(std::string_view api);
bool ShouldTracePostVideoTextureOwnedHandoffProbe(std::string_view api);
bool RefiiPostVideoVfsHandoffTraceActive();
std::string FormatPostVideoBoundaryArgs(uint8_t* base,
                                        const PPCContext& ctx);
std::string FormatStartupVideoRenderPollDispatchState(uint8_t* base);
std::string FormatStartupVideoUpdateTimingState(uint8_t* base,
                                                uint32_t update_guest);
std::string FormatPostVideoFrameWaitState(uint8_t* base,
                                          uint32_t wait_object_guest);
std::string FormatNativeVfsAsyncRequestState(uint8_t* base,
                                             uint32_t request_guest);

bool RefiiStartupVideosPlaybackCriticalActive();
}  // namespace detail
}  // namespace refii::vfs
