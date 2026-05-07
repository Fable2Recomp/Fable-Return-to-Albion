#pragma once

#include "hooks/file_api_types.h"

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_set>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

bool TryRunRefiiFrontendRenderRequestInline(PPCContext& source_ctx,
                                            uint8_t* base,
                                            uint32_t request_guest,
                                            std::string_view api,
                                            bool allow_ready_primary,
                                            bool skip_ready_probe);
inline bool TryRunRefiiFrontendRenderRequestInline(PPCContext& source_ctx,
                                                   uint8_t* base,
                                                   uint32_t request_guest,
                                                   std::string_view api) {
  return TryRunRefiiFrontendRenderRequestInline(
      source_ctx, base, request_guest, api, true, false);
}
bool TryRunRefiiDeferredPostLoadingFrontendRequestOnRenderThread(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);
bool RefiiFrontendRenderRequestInlineAlreadyCompleted(uint32_t request_guest);
std::mutex& RefiiInlineFrontendRenderRequestMutex();
std::unordered_set<uint32_t>& RefiiPostLoadingNativeFrontendRenderRuns();
std::unordered_set<uint32_t>& RefiiPostLoadingFrontendRenderCommitRuns();
std::unordered_set<uint32_t>&
RefiiPostLoadingFrontendRenderProducerCompletions();
std::unordered_set<uint32_t>& RefiiPostLoadingFrontendCompletionPublishes();
std::unordered_set<uint32_t>& RefiiPostLoadingFrontendGeneratedRuns();
std::unordered_set<uint32_t>& RefiiPostLoadingFrontendStateFinalizes();
std::string FormatRefiiPostLoadingAfterHolderState(uint8_t* base,
                                                   uint32_t holder_guest);
inline std::string FormatRefiiPostLoadingAfterHolderState(uint8_t* base) {
  return FormatRefiiPostLoadingAfterHolderState(base, 0);
}
bool IsRefiiPostLoadingMiddlewareVideoPath(std::string_view path);
bool RefiiPostLoadingMiddlewareVideoReadComplete();
bool RefiiOwnerlessPostVideoAfterHolderCleaned();
void RecordRefiiPostLoadingMiddlewareVideoReadComplete(
    std::string_view path,
    uint32_t bytes_read,
    uint32_t requested_bytes);
void RecordRefiiPostLoadingMiddlewareVideoReadComplete(
    const RefiiPrivateFileHandleRecord& record,
    uint32_t handle,
    uint64_t read_end);
bool TryAdvanceRefiiPostLoadingAfterHolderMode4(const PPCContext& source_ctx,
                                                uint8_t* base,
                                                uint32_t holder_guest,
                                                std::string_view api);
bool TryMarkRefiiPostLoadingAfterHolderReady(const PPCContext& source_ctx,
                                             uint8_t* base,
                                             uint32_t holder_guest,
                                             std::string_view api);
bool TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view api);
inline bool TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api) {
  return TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
      source_ctx, base, 0, api);
}
bool TryReturnRefiiPostLoadingAfterHolderSingletonForCompletedVfsChain(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t* out_holder_guest,
    std::string_view api);
bool TryCompleteRefiiPostLoadingAfterHolderForCompletedOwner(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view api);
bool TryCollapseRefiiPostLoadingAfterHolderMode2(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view api);
bool TryFinalizeRefiiOwnerlessPostLoadingAfterHolderState(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api);
std::atomic<uint32_t>& RefiiPostLoadingAfterHolderMode5CleanedHolder();
uint32_t& RefiiPostLoadingAfterHolderRenderOwnerScratch();

}  // namespace detail
}  // namespace refii::vfs
