#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

std::string FormatRefiiLoadingScreenSequenceGlobals(uint8_t* base);
uint32_t CountGuestLinearVector(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t vector_offset,
                                uint32_t element_size);
std::string FormatGuestLinearVector(uint8_t* base,
                                    uint32_t object_guest,
                                    uint32_t vector_offset,
                                    uint32_t element_size,
                                    uint32_t preview_words);
uint32_t FindRefiiLoadingScreenActiveResourceTable(uint8_t* base);
bool LooksLikeNativeResourceHandle(uint32_t handle);
std::atomic<uint32_t>& RefiiRememberedLoadingScreenOwner();
std::atomic<uint32_t>& RefiiBridgedLoadingSequenceHandle();
std::atomic<uint32_t>& RefiiLoadingAnimationResolverHandle();
std::atomic<uint32_t>& RefiiCompletedLoadingScreenOwner();
std::atomic<uint32_t>& RefiiForcedLoadingScreenOwnerFinish();
void RecordRefiiLoadingSequenceHandle(uint32_t handle);
bool IsRefiiLoadingSequenceHandle(uint32_t handle);
void RecordRefiiLoadingSequenceResolverHandle(uint32_t resource_hash,
                                              uint32_t handle,
                                              uint32_t lr,
                                              uint32_t table_index);
void RecordRefiiLoadingScreenOwner(uint8_t* base,
                                   uint32_t owner_guest,
                                   std::string_view api);
bool RefiiPostGameFacePackageHandoffSignalEmitted();
bool RefiiPostGameFacePackageComplete();
bool RefiiPostFrontendDeferredHashWakeSignalEmitted();
bool RefiiPostVfsChainReadyForLoadingOwnerRelease();
bool RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder();

bool TryBridgeLoadingScreenSequenceListFromRefiiHandle(PPCContext& source_ctx,
                                                       uint8_t* base,
                                                       uint32_t owner_guest,
                                                       uint32_t* out_handle);
std::string FormatRefiiLoadingScreenOwnerState(uint8_t* base,
                                               uint32_t owner_guest);
bool TryRefreshRememberedLoadingScreenSequenceBridge(PPCContext& source_ctx,
                                                     uint8_t* base,
                                                     std::string_view api,
                                                     uint32_t* out_handle);
inline bool TryRefreshRememberedLoadingScreenSequenceBridge(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view api) {
  return TryRefreshRememberedLoadingScreenSequenceBridge(
      source_ctx, base, api, nullptr);
}
bool RefiiLoadingScreenSequenceReady(uint8_t* base,
                                     uint32_t owner_guest,
                                     uint32_t* out_handle);
inline bool RefiiLoadingScreenSequenceReady(uint8_t* base) {
  return RefiiLoadingScreenSequenceReady(base, 0, nullptr);
}
bool TryResolveRefiiBridgedLoadingSequenceHandleLookup(PPCContext& source_ctx,
                                                       uint8_t* base,
                                                       uint32_t out_guest,
                                                       uint32_t table_guest,
                                                       uint32_t handle_guest);
bool TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api);
bool RefiiMainMenuTraceIsHashOnly();
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();
bool RefiiMainMenuTraceNeedsTexturePrime();
bool RefiiPostVideoFrontendChainConsumed();
bool RefiiPostFrontendConsumerHandoffSignalEmitted();
bool RefiiMainMenuTraceNeedsFrontendHandoff();
bool RefiiMainMenuTraceAllowsPostVideoReadyAdvance();
bool RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
bool RefiiCollapsedLoadingSequenceReadyForOwnerRelease();

}  // namespace detail
}  // namespace refii::vfs
