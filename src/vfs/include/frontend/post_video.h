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
void MaybeRecordFirstRefiiPostFrontendConsumerAssetRequest(
    std::string_view api,
    std::string_view asset_path,
    uint32_t resource_hash,
    uint32_t lr);
void BeginRefiiPostVideoFrameStageTiming();
void RecordRefiiPostVideoStageTiming(std::string_view stage,
                                     uint32_t lr,
                                     uint32_t result,
                                     int64_t elapsed_ms);
void RecordRefiiStartupUpdateSnapshot(uint8_t* base,
                                      uint32_t update_guest,
                                      uint32_t lr,
                                      uint32_t result);
uint32_t LastRefiiStartupUpdateGuest();
void RecordRefiiStartupReadyPoll(uint8_t* base,
                                 uint32_t object_guest,
                                 uint32_t lr,
                                 uint32_t result);
void RecordRefiiStartupCompletionGateSnapshot(uint8_t* base,
                                              uint32_t callback_guest,
                                              uint32_t lr,
                                              uint32_t result);
bool ApplyRefiiSkipStartupVideoReadyPatch(uint8_t* base,
                                          uint32_t update_guest,
                                          std::string_view reason);
inline bool ApplyRefiiSkipStartupVideoReadyPatch(uint8_t* base) {
  return ApplyRefiiSkipStartupVideoReadyPatch(base, 0, {});
}
bool ApplyRefiiVfsStartupVideoReadyPatch(uint8_t* base,
                                         uint32_t update_guest,
                                         std::string_view reason);
inline bool ApplyRefiiVfsStartupVideoReadyPatch(uint8_t* base) {
  return ApplyRefiiVfsStartupVideoReadyPatch(base, 0, {});
}
bool ApplyRefiiStartupReadyPollObjectPatch(uint8_t* base,
                                           uint32_t object_guest,
                                           std::string_view reason);
void RecordRefiiGetCachedHandoffEvent(std::string_view name,
                                      uint32_t lr,
                                      uint32_t stream_guest);
void RecordRefiiPostVideoWaitSnapshot(uint8_t* base,
                                      uint32_t owner_guest,
                                      uint32_t lr,
                                      uint32_t result,
                                      int64_t wait_ms);
inline void RecordRefiiPostVideoWaitSnapshot(uint8_t* base,
                                             uint32_t owner_guest,
                                             uint32_t lr) {
  RecordRefiiPostVideoWaitSnapshot(base, owner_guest, lr, 0, -1);
}
void RecordRefiiPostVideoWaitResult(uint32_t lr,
                                    uint32_t result,
                                    int64_t wait_ms,
                                    uint32_t wait_object_guest,
                                    uint32_t wait_handle_guest);
inline void RecordRefiiPostVideoWaitResult(uint32_t lr,
                                           uint32_t result,
                                           int64_t wait_ms) {
  RecordRefiiPostVideoWaitResult(lr, result, wait_ms, 0, 0);
}
void RecordRefiiPostVideoFrameHandoffSnapshot(uint8_t* base,
                                              uint32_t owner_guest,
                                              uint32_t lr);
void RecordRefiiRenderProducerScheduleSnapshot(uint8_t* base,
                                               uint32_t owner_guest,
                                               uint32_t lr,
                                               uint32_t source124,
                                               uint32_t source128,
                                               double frame_time);
bool RefiiPostVideoEventMatchesHandoffWait(uint32_t guest);
bool RefiiStartupVideoSlotReady(const RefiiStartupVideoItemTrace& slot);
bool RefiiStartupVideosReadyForMenuHandoff();
bool RefiiStartupVideosFinalizedForMenuHandoff();
bool RefiiStartupVideosSettledForMenuHandoff();
bool RefiiStartupVideosSettledForPostVideoHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace,
    int64_t* settled_age_ms_out);
bool RefiiStartupVideosNativeObjectsRetiredLocked(
    const RefiiPostVideoHandoffTrace& trace);
bool RefiiStartupReadyCompletionSeenForPostVideoHandoffLocked(
    const RefiiPostVideoHandoffTrace& trace);
std::string FormatRefiiPostVideoStageTimingForLog();
std::string RefiiPostVideoSlowFrameNextBoundary();
void MaybeLogRefiiActiveStreamCompletionAudit(uint32_t lr,
                                              uint8_t* base);
bool MaybeReleaseCompletedStartupBikSource(PPCContext& source_ctx,
                                           uint8_t* base,
                                           uint32_t object_guest,
                                           std::string_view api);
void MaybeLogRefiiVideosReadyTextureOpenGap(uint32_t lr);
void MaybeLogRefiiVideosReadyTextureOpenGap(uint32_t lr,
                                            std::string_view context,
                                            uint8_t* base);
void MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(uint32_t lr);
void MaybeLogRefiiStartupVideoReadyNoFinalizerBlocker(uint32_t lr,
                                                      std::string_view context,
                                                      uint8_t* base);
std::string FormatRefiiStartupVideoSlotTrace(
    const RefiiStartupVideoItemTrace& slot,
    std::chrono::steady_clock::time_point now);
inline std::string FormatRefiiStartupVideoSlotTrace(
    const RefiiStartupVideoItemTrace& slot) {
  return FormatRefiiStartupVideoSlotTrace(slot, std::chrono::steady_clock::now());
}

std::string FormatRefiiPostVideoHandoffTraceForLog();
bool RefiiIsTrackedStartupVideoObject(uint32_t object_guest);
bool ShouldTracePostVideoBoundaryProbe(std::string_view api);
bool ShouldTracePostVideoTextureOwnedHandoffProbe(std::string_view api);
bool RefiiPostVideoVfsHandoffTraceActive();
bool IsPostVideoGeneratedWaitLr(uint32_t lr);
bool IsGeneratedPostVideoRenderReadyPollLr(uint32_t lr);
void RememberRefiiNativeVfsWorkerWait(uint32_t lr,
                                      uint32_t object_guest,
                                      uint32_t handle_guest);
bool TrySignalRefiiCompletedNativeVfsWorkerWait(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api);
bool TryReleaseRefiiCompletedNativeVfsFinalizerExit(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api,
    bool trace);
bool MaybeSignalRefiiPostVideoGeneratedWaitBeforeNative(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest);
bool TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    std::string_view api,
    uint32_t source_result,
    bool sequence_ready,
    uint32_t sequence_handle);
inline bool TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    std::string_view api) {
  return TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
      source_ctx, base, owner_guest, api, 1, false, 0);
}
bool TrySignalRefiiCompletedPostGameFacePackageHandoffFromTrace(
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest,
    bool trace);
bool TryCollapseRefiiCompletedLoadingOwnerWorkerWait(PPCContext& ctx,
                                                     const PPCContext& original_ctx,
                                                     uint8_t* base,
                                                     uint32_t wait_object_guest,
                                                     uint32_t wait_handle_guest,
                                                     bool trace);
void RecordRefiiFrontEndAudioBankLateBind(std::string_view bank_name,
                                          uint32_t lr);
bool TryCollapseRefiiCompletedFrontEndAudioSyncWait(
    PPCContext& ctx,
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t wait_object_guest,
    uint32_t wait_handle_guest,
    bool trace);
bool TryCollapseRefiiPostVideoRendererSyncWait(PPCContext& ctx,
                                               const PPCContext& original_ctx,
                                               uint8_t* base,
                                               uint32_t wait_object_guest,
                                               uint32_t wait_handle_guest,
                                               bool trace);
bool TryCollapseRefiiPostVideoGeneratedRenderWait(PPCContext& ctx,
                                                  const PPCContext& original_ctx,
                                                  uint8_t* base,
                                                  uint32_t wait_object_guest,
                                                  uint32_t wait_handle_guest,
                                                  bool trace);
bool TryCollapseRefiiCompletedNativeVfsWorkerWait(PPCContext& ctx,
                                                  const PPCContext& original_ctx,
                                                  uint8_t* base,
                                                  uint32_t wait_object_guest,
                                                  uint32_t wait_handle_guest,
                                                  bool trace);
bool ShouldTracePostVideoGeneratedEventWait(uint32_t lr,
                                            uint32_t wait_object_guest,
                                            uint32_t wait_handle_guest);
bool ShouldRecordPostVideoWaitSnapshot(uint32_t lr);
std::string FormatPostVideoBoundaryArgs(uint8_t* base,
                                        const PPCContext& ctx);
std::string FormatPostVideoFrameChainState(uint8_t* base,
                                           uint32_t owner_guest);
std::string FormatCurrentPostVideoFrameChainState(uint8_t* base);
std::string FormatStartupVideoRenderPollDispatchState(uint8_t* base);
std::string FormatPostVideoFrameTimingState(uint8_t* base,
                                            uint32_t owner_guest,
                                            double frame_time);
std::string FormatStartupVideoUpdateTimingState(uint8_t* base,
                                                uint32_t update_guest);
bool ShouldTraceStartupVideoLoopProbe(std::string_view operation,
                                      uint32_t lr,
                                      int interval);
inline bool ShouldTraceStartupVideoLoopProbe(std::string_view operation,
                                             uint32_t lr) {
  return ShouldTraceStartupVideoLoopProbe(operation, lr, 24);
}
std::string FormatPostVideoCompletionGateState(uint8_t* base,
                                               uint32_t callback_guest);
std::string FormatPostVideoDynamicHandoffGetterState(uint8_t* base,
                                                     uint32_t object_guest);
std::string FormatPostVideoFrameWaitState(uint8_t* base,
                                          uint32_t wait_object_guest);
std::string FormatPostVideoRenderProducerState(uint8_t* base,
                                               uint32_t producer_guest);
std::string FormatNativeVfsAsyncRequestState(uint8_t* base,
                                             uint32_t request_guest);

bool MaybeSeedRefiiGeneratedRenderReadyPayload(const PPCContext& source_ctx,
                                               uint8_t* base,
                                               uint32_t object_guest);
uint32_t FindPostVideoOwnerFromGlobalRoot(uint8_t* base);
bool RefiiMainMenuTextureKnownOwnedByVfsIndex();
void MaybeLogRefiiPostVideoHashOnlyBlocker(uint32_t lr);
void MaybeLogRefiiPostVideoHashOnlyBlocker(uint32_t lr,
                                           std::string_view context,
                                           uint8_t* base);
void MaybeLogRefiiStartupReadyGateTextureOpenVerdict(uint32_t lr);
void MaybeLogRefiiStartupReadyGateTextureOpenVerdict(uint32_t lr,
                                                     std::string_view context,
                                                     uint8_t* base);
bool RefiiStartupVideosPlaybackCriticalActive();
bool RefiiStartupReadyGateFalseLongEnoughForDebugAccelerator(
    int64_t* false_age_ms_out);
inline bool RefiiStartupReadyGateFalseLongEnoughForDebugAccelerator(uint32_t) {
  return RefiiStartupReadyGateFalseLongEnoughForDebugAccelerator(nullptr);
}
void MarkRefiiStartupReadyGateDebugAcceleratorEmitted();
void RecordRefiiStartupReadyGateObjectPatch(uint32_t object_guest,
                                            std::string_view reason);
bool MaybeApplyRefiiVfsStartupHandoffReadyPatch(uint8_t* base,
                                                uint32_t lr,
                                                std::string_view context);
inline bool MaybeApplyRefiiVfsStartupHandoffReadyPatch(
    uint8_t* base,
    uint32_t object_guest,
    uint32_t lr) {
  (void)object_guest;
  return MaybeApplyRefiiVfsStartupHandoffReadyPatch(base, lr, {});
}
bool MaybeApplyRefiiVfsPostVideoHandoffTimerPatch(uint8_t* base,
                                                  uint32_t lr,
                                                  uint32_t object_guest,
                                                  std::string_view context);

}  // namespace detail
}  // namespace refii::vfs
