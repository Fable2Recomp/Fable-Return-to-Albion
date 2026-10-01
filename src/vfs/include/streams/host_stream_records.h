#pragma once

#include "streams/types.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {
std::shared_ptr<RefiiHostStreamRecord> FindRefiiHostStreamRecord(
    uint32_t stream_guest);
std::unordered_map<uint32_t, std::shared_ptr<RefiiHostStreamRecord>>&
RefiiHostStreamRecords();
std::mutex& RefiiHostStreamRecordsMutex();
void StoreRefiiHostStreamRecord(
    uint32_t stream_guest,
    std::shared_ptr<RefiiHostStreamRecord> record);
size_t RefiiHostStreamRecordCount();

std::shared_ptr<RefiiReadRequestRecord> FindRefiiReadRequestRecord(
    uint32_t request_guest,
    uint32_t* out_record_guest);
inline std::shared_ptr<RefiiReadRequestRecord> FindRefiiReadRequestRecord(
    uint32_t request_guest) {
  return FindRefiiReadRequestRecord(request_guest, nullptr);
}
void StoreRefiiReadRequestRecord(
    uint32_t request_guest,
    std::shared_ptr<RefiiReadRequestRecord> record);
void EraseRefiiReadRequestRecord(uint32_t request_guest);
size_t RefiiReadRequestRecordCount();

void EraseRefiiHostStreamRecord(uint32_t stream_guest);
void EraseLooseStreamRecord(uint32_t stream_guest);
void RecordLooseStream(uint8_t* base,
                       uint32_t stream_guest,
                       const std::filesystem::path& loose_path,
                       std::string provider_root,
                       std::string provider_name);
bool FindLooseStreamRecord(uint8_t* base,
                           uint32_t stream_guest,
                           LooseStreamRecord* out_record,
                           std::string* out_match);
void RecordLooseStreamAlias(uint8_t* base,
                            std::string_view api,
                            uint32_t alias_stream_guest,
                            uint32_t source_stream_guest);
uint32_t TryReadOutputPointer(uint8_t* base, uint32_t out_guest);
void RecordLooseStreamAliasFromOutput(uint8_t* base,
                                      std::string_view api,
                                      uint32_t out_guest,
                                      uint32_t source_stream_guest);

bool ShouldLogLooseStreamOperation(std::string_view api,
                                   uint32_t stream_guest,
                                   uint32_t limit);
void LogLooseStreamSizeResult(std::string_view api,
                              uint8_t* base,
                              uint32_t stream_guest,
                              const PPCContext& ctx);
void LogLooseStreamRead(std::string_view api,
                        uint8_t* base,
                        uint32_t stream_guest,
                        uint32_t arg4,
                        uint32_t arg5,
                        uint32_t arg6,
                        const PPCContext& ctx);
void LogLooseStreamWrapperCall(std::string_view api,
                               uint8_t* base,
                               const PPCContext& ctx,
                               uint32_t out_guest,
                               uint32_t source_stream_guest);
void LogLooseStreamWrapperResult(std::string_view api,
                                 uint8_t* base,
                                 const PPCContext& before_ctx,
                                 const PPCContext& after_ctx,
                                 uint32_t out_guest,
                                 uint32_t source_stream_guest);
bool IsNativeGetCachedShapeRepairCandidate(std::string_view asset_path);
}  // namespace detail
}  // namespace refii::vfs
