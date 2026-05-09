#pragma once

#include "frontend/types.h"
#include "streams/types.h"

#include <cstddef>
#include <cstdint>
#include <chrono>
#include <mutex>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

std::string FormatGuestObjectVTableSlots(uint8_t* base,
                                         uint32_t guest,
                                         uint32_t slot_count);
bool LooksLikeGuestPointer(uint32_t value);
bool LooksLikeGuestDataPointer(uint32_t value);
bool IsNativeVfsScheduledRequest(uint8_t* base, uint32_t request_guest);
bool IsNativeFrontendRenderRequest(uint8_t* base, uint32_t request_guest);
bool LooksLikePointerPosition(uint32_t position, uint64_t expected_size);
bool IsStartupLuaAssetName(std::string_view name);
std::mutex& RefiiPostVideoHandoffTraceMutex();
RefiiPostVideoHandoffTrace& RefiiPostVideoHandoffTraceState();
int64_t RefiiBootTraceAgeMs();
uint32_t RefiiCompletedPostGameFacePackageMaskLocked(
    const RefiiPostVideoHandoffTrace& trace);
RefiiStartupVideoItemTrace* SelectStartupVideoTraceSlotByStreamLocked(
    RefiiPostVideoHandoffTrace& trace,
    uint8_t* base,
    uint32_t stream_guest);
bool IsBikLooseStreamTraceAsset(uint8_t* base, uint32_t stream_guest);
bool ShouldTraceBikPpcShape(uint8_t* base,
                            uint32_t stream_guest,
                            std::string_view api,
                            uint32_t loose_limit = 12,
                            uint32_t native_limit = 8);
bool ShouldTraceStartupBikStreamShape(uint8_t* base,
                                      uint32_t stream_guest,
                                      std::string_view api,
                                      uint32_t loose_limit);
std::string FormatBikPpcStreamSummary(uint8_t* base, uint32_t stream_guest);
std::string FormatBikBufferedNativeShape(uint8_t* base,
                                         uint32_t stream_guest);
std::string FormatRefiiReadRequestShape(uint8_t* base,
                                        uint32_t request_guest);
bool IsStartupBikAssetName(std::string_view name);
bool GetBikLooseStreamRecord(uint8_t* base,
                             uint32_t stream_guest,
                             LooseStreamRecord* out_record);
bool GetBikLooseStreamRecord(uint8_t* base,
                             uint32_t stream_guest,
                             LooseStreamRecord* out_record,
                             std::string* out_match);
void RecordRefiiStartupVideoBufferedWrapper(uint8_t* base,
                                            std::string_view provider,
                                            uint64_t expected_size,
                                            uint32_t source_stream_guest,
                                            uint32_t wrapped_stream_guest,
                                            uint32_t arg5,
                                            uint32_t arg6,
                                            bool repaired);
void RecordRefiiStartupVideoReadQueued(uint8_t* base,
                                       uint32_t stream_guest,
                                       uint32_t request_guest,
                                       uint32_t requested_bytes);
void RecordRefiiStartupVideoReadComplete(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t requested_bytes,
                                         uint32_t bytes_read,
                                         int64_t queue_wait_ms,
                                         int64_t read_ms);
inline void RecordRefiiStartupVideoReadComplete(uint8_t* base,
                                                uint32_t stream_guest,
                                                uint32_t requested_bytes,
                                                uint32_t bytes_read) {
  RecordRefiiStartupVideoReadComplete(
      base, stream_guest, requested_bytes, bytes_read, -1, -1);
}
void RecordRefiiStartupVideoCursor(uint8_t* base,
                                   uint32_t object_guest,
                                   uint32_t stream_guest,
                                   uint32_t result);
void RecordRefiiStartupVideoSize(uint8_t* base,
                                 uint32_t object_guest,
                                 uint32_t stream_guest,
                                 uint32_t result);
void RecordRefiiStartupVideoStatus(uint8_t* base,
                                   uint32_t object_guest,
                                   uint32_t stream_guest,
                                   uint32_t result);
void MaybeLogRefiiStartupVideoShapeVerdict(uint8_t* base,
                                           uint32_t stream_guest,
                                           std::string_view reason);
void RecordRefiiStartupVideoPosition(uint8_t* base,
                                     uint32_t stream_guest,
                                     uint32_t out_guest,
                                     uint32_t low,
                                     uint32_t high);
bool TryRepairRefiiStartupBikBufferedPosition(uint8_t* base,
                                              uint32_t stream_guest,
                                              uint32_t out_guest,
                                              uint32_t native_low,
                                              uint32_t native_high,
                                              uint32_t lr,
                                              uint32_t* repaired_low,
                                              uint32_t* repaired_high);
bool TryGetRefiiStartupBikCompletedReadBuffer(uint8_t* base,
                                              uint32_t stream_guest,
                                              uint32_t* buffer_guest,
                                              uint32_t* bytes_read);
void RecordRefiiStartupVideoBufferedSize(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t result);
void RecordRefiiStartupVideoFinalizer(uint8_t* base,
                                      uint32_t stream_guest,
                                      uint32_t result);
std::string FormatBikObjectVirtualProbe(uint8_t* base,
                                        uint32_t object_guest);
void RecordRefiiStartupVideoObjectHandoff(uint8_t* base,
                                          std::string_view method,
                                          uint32_t lr,
                                          uint32_t object_guest,
                                          uint32_t stream_before,
                                          uint32_t stream_after,
                                          uint32_t result,
                                          std::string_view requested_name);
inline void RecordRefiiStartupVideoObjectHandoff(uint8_t* base,
                                                 std::string_view method,
                                                 uint32_t lr,
                                                 uint32_t object_guest,
                                                 uint32_t stream_before,
                                                 uint32_t stream_after,
                                                 uint32_t result) {
  RecordRefiiStartupVideoObjectHandoff(
      base, method, lr, object_guest, stream_before, stream_after, result, {});
}
void MaybeLogStartupLuaGetCachedShapeVerdict(uint8_t* base,
                                             std::string_view name,
                                             uint32_t lr,
                                             uint32_t stream_guest,
                                             bool native_shape_repaired,
                                             std::string_view reason);
void MaybeLogStartupLuaStreamCloseVerdict(uint32_t stream_guest,
                                          const RefiiHostStreamRecord* record,
                                          size_t active_after);

}  // namespace detail
}  // namespace refii::vfs
