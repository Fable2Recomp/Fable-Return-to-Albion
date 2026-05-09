#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

bool RefiiPostGameFacePrimaryOwnerReady(uint8_t* base);
bool TryGetRefiiPostGameFacePrimaryRenderRequest(uint8_t* base,
                                                 uint32_t* request_guest,
                                                 uint32_t* owner_guest,
                                                 uint32_t* handoff_guest);
bool TryPublishRefiiPostGameFacePrimaryOwner(const PPCContext& source_ctx,
                                             uint8_t* base,
                                             std::string_view asset_path,
                                             std::string_view source_api,
                                             uint32_t package_mask);
bool TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(PPCContext& source_ctx,
                                                      uint8_t* base,
                                                      std::string_view api);
bool TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);
bool TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);
bool TryFinalizeRefiiPostLoadingFrontendGeneratedState(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api,
    bool clear_render_handoff,
    bool preserve_post_gameface_primary_allowed);
bool RefiiPostLoadingFrontendFinalHandoffWakeEmitted();
bool TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);
inline bool TryFinalizeRefiiPostLoadingFrontendGeneratedState(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api) {
  return TryFinalizeRefiiPostLoadingFrontendGeneratedState(
      source_ctx, base, api, true, false);
}
inline bool TryFinalizeRefiiPostLoadingFrontendGeneratedState(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api,
    bool clear_render_handoff) {
  return TryFinalizeRefiiPostLoadingFrontendGeneratedState(
      source_ctx, base, api, clear_render_handoff, false);
}
bool TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);

void MarkRefiiPostFrontendConsumerAssetRequest(std::string_view api,
                                               std::string_view asset_path,
                                               uint32_t resource_hash,
                                               uint32_t lr);
bool MaybeSignalRefiiPostGameFacePackageHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view asset_path,
    uint32_t stream_guest,
    bool allow_generated_frame_completion);
bool MaybeRecordRefiiPostGameFacePackageOptionalMiss(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view asset_path,
    std::string_view api);
inline bool MaybeSignalRefiiPostGameFacePackageHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view asset_path,
    uint32_t stream_guest) {
  return MaybeSignalRefiiPostGameFacePackageHandoff(
      source_ctx, base, asset_path, stream_guest, true);
}
bool TryPrimeRefiiPostFrontendGameFacePackage(
    const PPCContext& source_ctx,
    uint8_t* base,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    std::string_view api);
bool MaybeSignalRefiiFrontendDeferredHashWakeHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);
bool TryRunPostVideoFrontendSyntheticHandoff(PPCContext& source_ctx,
                                             uint8_t* base,
                                             uint32_t root_guest,
                                             std::string_view api);
std::string FormatFrontendTextureConsumerTraceForLog(
    const ActiveFrontendTextureConsumerTraceContext& trace);
std::string FrontendTextureConsumerNextBoundary(
    const ActiveFrontendTextureConsumerTraceContext& trace);
bool RecordRefiiFrontendTextureConsumerVerdict(
    uint32_t lr,
    uint32_t result,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    bool texture_owned_but_unopened);
bool MaybeSignalRefiiPostFrontendConsumerHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    std::string_view api);
bool MaybeRefreshRefiiPostGameFaceFollowupBoundary(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view normalized_asset,
    uint32_t stream_guest,
    std::string_view skip_reason,
    uint32_t package_mask_after);

}  // namespace detail
}  // namespace refii::vfs
