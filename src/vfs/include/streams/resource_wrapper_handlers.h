#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <memory>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

bool EnsureRefiiReadRequestCompleted(uint8_t* base,
                                     const std::shared_ptr<RefiiReadRequestRecord>& record,
                                     uint32_t record_guest,
                                     std::string_view api);
bool TryHandleRefiiResourceWrapperWait(PPCContext& ctx,
                                       uint8_t* base,
                                       uint32_t wrapper_guest,
                                       std::string_view api);
bool TryHandleRefiiResourceWrapperGetBytes(PPCContext& ctx,
                                           uint8_t* base,
                                           uint32_t out_guest,
                                           uint32_t wrapper_guest);
bool TryHandleRefiiResourceWrapperGetSize(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t wrapper_guest);
bool TryHandleRefiiHostDiskStreamRead(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t out_guest,
                                      uint32_t stream_guest,
                                      uint32_t buffer_guest,
                                      uint64_t offset,
                                      uint32_t requested_bytes);
bool TryHandleRefiiHostDiskStreamDirectRead(PPCContext& ctx,
                                            uint8_t* base,
                                            uint32_t stream_guest,
                                            uint32_t buffer_guest,
                                            uint64_t offset,
                                            uint32_t requested_bytes);
bool TryHandleRefiiHostDiskStreamNoopSuccess(PPCContext& ctx,
                                             uint8_t* base,
                                             uint32_t stream_guest,
                                             std::string_view api);
bool TryHandleRefiiStreamReaderFlush(PPCContext& ctx,
                                     uint8_t* base,
                                     uint32_t reader_guest,
                                     std::string_view api);
bool TryHandleRefiiStreamReaderRefill(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t reader_guest,
                                      uint32_t page_index,
                                      uint64_t target_offset);
bool TryHandleRefiiReadRequestWait(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t request_guest,
                                   std::string_view api);
bool TryHandleRefiiReadRequestAddRef(PPCContext& ctx,
                                     uint8_t* base,
                                     uint32_t request_guest);
bool TryHandleRefiiReadRequestRelease(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t request_guest);
bool RetireRefiiReadRequestAfterNativeRelease(PPCContext& ctx,
                                              uint8_t* base,
                                              uint32_t request_guest,
                                              std::string_view api);
bool TryHandleRefiiResourceWrapperSlot36(PPCContext& ctx,
                                         uint8_t* base,
                                         uint32_t first_guest,
                                         uint32_t second_guest);
bool TryHandleRefiiResourceWrapperAddRef(PPCContext& ctx,
                                         uint8_t* base,
                                         uint32_t wrapper_guest,
                                         std::string_view api);
bool TryHandleRefiiResourceWrapperRelease(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t wrapper_guest,
                                          uint32_t flags,
                                          std::string_view api);
bool TryHandleRefiiResourceWrapperDtor(PPCContext& ctx,
                                       uint8_t* base,
                                       uint32_t wrapper_guest,
                                       uint32_t flags);
bool TryHandleRefiiReadRequestGetStatus(PPCContext& ctx,
                                        uint8_t* base,
                                        uint32_t request_guest,
                                        std::string_view api);
bool TryHandleRefiiReadRequestGetBytes(PPCContext& ctx,
                                       uint8_t* base,
                                       uint32_t out_guest,
                                       uint32_t request_guest);
bool TryHandleRefiiReadRequestGetOffset(PPCContext& ctx,
                                        uint32_t request_guest);
bool TryHandleRefiiReadRequestReturnSuccess(PPCContext& ctx,
                                            uint8_t* base,
                                            uint32_t request_guest,
                                            std::string_view api);
bool TryHandleRefiiReadRequestSmartPtrAssign(PPCContext& ctx,
                                             uint8_t* base,
                                             uint32_t target_guest,
                                             uint32_t source_guest);
bool TryHandleRefiiHostCompositeStreamRead(PPCContext& ctx,
                                           uint8_t* base,
                                           uint32_t out_guest,
                                           uint32_t stream_guest,
                                           uint32_t buffer_guest,
                                           uint64_t offset,
                                           uint32_t requested_bytes);
bool TryHandleRefiiHostCompositeStreamDirectRead(PPCContext& ctx,
                                                 uint8_t* base,
                                                 uint32_t stream_guest,
                                                 uint32_t buffer_guest,
                                                 uint64_t offset,
                                                 uint32_t requested_bytes);

}  // namespace detail
}  // namespace refii::vfs
