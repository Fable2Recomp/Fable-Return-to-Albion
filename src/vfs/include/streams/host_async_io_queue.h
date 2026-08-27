#pragma once

#include "streams/types.h"

#include <cstdint>
#include <future>
#include <memory>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

uint32_t CreateCompletedReadRequest(PPCContext& ctx,
                                    uint8_t* base,
                                    uint32_t stream_guest,
                                    uint32_t buffer_guest,
                                    uint64_t offset,
                                    uint32_t requested_bytes,
                                    uint32_t bytes_read);
uint32_t CreateDeferredReadRequest(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t stream_guest,
                                   uint32_t buffer_guest,
                                   uint64_t offset,
                                   uint32_t requested_bytes,
                                   bool uses_stream_destination);
void CompleteRefiiReadRequest(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record,
    uint32_t record_guest,
    uint32_t bytes_read);
void CloseRefiiReadRequestEvent(PPCContext& ctx,
                                uint8_t* base,
                                uint32_t request_guest);
void StartRefiiReadRequestAsync(
    PPCContext ctx,
    uint8_t* base,
    uint32_t record_guest,
    const std::shared_ptr<RefiiReadRequestRecord>& record);
bool WaitForRefiiReadRequest(
    const std::shared_ptr<RefiiReadRequestRecord>& record);
// Reads a pending request and completes it, on the CALLING thread — which must be the guest's.
// Idempotent: a request already completed is left alone.
// Serves any read on this stream postponed for an unresolved destination (call from Dtor).
void CompleteRefiiDeferredReadsForStream(PPCContext& ctx, uint8_t* base,
                                         uint32_t stream_guest);

void PerformRefiiReadRequestNow(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record,
    uint32_t record_guest);

bool EnqueueRefiiIoRead(
    PPCContext ctx,
    uint8_t* base,
    uint32_t request_guest,
    const std::shared_ptr<RefiiReadRequestRecord>& record,
    const std::shared_ptr<std::promise<void>>& completion);

}  // namespace detail
}  // namespace refii::vfs
