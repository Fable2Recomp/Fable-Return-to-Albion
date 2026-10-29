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
bool IsNativeVfsScheduledRequest(uint8_t* base, uint32_t request_guest);
bool IsStartupLuaAssetName(std::string_view name);
std::mutex& RefiiPostVideoHandoffTraceMutex();
RefiiPostVideoHandoffTrace& RefiiPostVideoHandoffTraceState();
int64_t RefiiBootTraceAgeMs();
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
                                       uint32_t requested_bytes);
void RecordRefiiStartupVideoReadComplete(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t requested_bytes,
                                         uint32_t bytes_read);
void RecordRefiiStartupVideoCursor(uint8_t* base,
                                   uint32_t object_guest,
                                   uint32_t stream_guest);
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
