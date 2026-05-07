#pragma once

#include "core/guest_state.h"
#include "loose_asset_hooks.h"
#include "vfs_hook_types.h"

#include <cstdint>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

inline constexpr uint32_t kFrontendTextureConsumerObjectVTable = 0x820A924C;
inline constexpr uint32_t kFrontendTextureConsumerBundleCallLr = 0x82378634;
inline constexpr uint32_t kRefiiLoadingAnimationHash = 0x27009C86u;
inline constexpr bool kRunCollapsedFrontendGeneratedHandoff = true;

uint32_t TryAllocateRefiiFrontendTextureObject(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t resource_hash,
    const LooseAssetResolution& resolution,
    std::string_view candidate,
    std::string_view api);
bool TryHandleRefiiTextureParserC340(PPCContext& ctx,
                                     uint8_t* base,
                                     std::string_view api);
bool TryHandleRefiiTextureParserC340NativeHandoff(PPCContext& ctx,
                                                  uint8_t* base,
                                                  std::string_view api);
bool TryCommitRefiiFrontendTextureWrapper(PPCContext& source_ctx,
                                          uint8_t* base,
                                          uint32_t texture_guest,
                                          uint32_t resource_hash,
                                          const LooseAssetResolution& resolution,
                                          std::string_view name,
                                          std::string_view candidate,
                                          std::string_view source,
                                          uint32_t caller_lr,
                                          std::string_view api);
bool TryPrimeRefiiFrontendTextureResource(PPCContext& source_ctx,
                                          uint8_t* base,
                                          uint32_t resource_hash,
                                          std::string_view api);
bool TrySynthesizeFrontendTextureDescriptorSlot(PPCContext& source_ctx,
                                                uint8_t* base,
                                                uint32_t owner_guest,
                                                uint32_t resource_hash,
                                                uint32_t ctor_lr,
                                                uint32_t resolver_lr,
                                                uint32_t pair_store_lr,
                                                uint32_t slot_store_lr,
                                                uint32_t slot_offset,
                                                uint32_t ref_object_guest,
                                                uint32_t* out_ref_object_guest,
                                                uint32_t* out_resolver_result,
                                                uint32_t* out_pair_guest);
bool TrySynthesizeFrontendTextureStaticTablePhase(PPCContext& source_ctx,
                                                  uint8_t* base,
                                                  uint32_t owner_guest);
bool TrySynthesizeFrontendTextureFinalDirectPhase(PPCContext& source_ctx,
                                                  uint8_t* base,
                                                  uint32_t owner_guest,
                                                  bool* out_wrapper_committed);
bool TrySynthesizeFrontendTextureFollowonFramePhase(PPCContext& source_ctx,
                                                    uint8_t* base,
                                                    uint32_t owner_guest);

bool FrontendTextureConsumerTraceActive();
bool FrontendTextureConsumerVerboseTraceActive();
bool LooksLikeFrontendTextureConsumerObject(uint8_t* base,
                                            uint32_t object_guest);
uint32_t FindPostVideoFrontendTextureConsumerOwner(uint8_t* base,
                                                   uint32_t root_guest,
                                                   std::string* source_out);
inline uint32_t FindPostVideoFrontendTextureConsumerOwner(uint8_t* base) {
  return FindPostVideoFrontendTextureConsumerOwner(base, 0, nullptr);
}
uint32_t TryGetCollapsibleRefiiFrontendTextureBundle(uint8_t* base,
                                                     const PPCContext& ctx);
inline uint32_t TryGetCollapsibleRefiiFrontendTextureBundle(uint8_t* base) {
  PPCContext ctx{};
  return TryGetCollapsibleRefiiFrontendTextureBundle(base, ctx);
}
std::string FormatFrontendTextureConsumerOwnerSlots(uint8_t* base,
                                                    uint32_t owner_guest);
std::string_view LabelFrontendTextureConsumerGeneratedCallSite(uint32_t lr);
std::string_view LabelFrontendConstantTableResolverShape(
    uint32_t descriptor_vtable,
    uint32_t descriptor_slot12,
    uint32_t descriptor_slot16);
void BeginFrontendTextureConsumerTrace(uint32_t owner_guest,
                                       uint32_t lr,
                                       bool verbose);
ActiveFrontendTextureConsumerTraceContext EndFrontendTextureConsumerTrace();
void RecordFrontendTextureConsumerDescriptor(uint32_t descriptor_guest,
                                             uint32_t descriptor_lr,
                                             uint32_t resource_hash,
                                             uint32_t descriptor_vtable,
                                             uint32_t descriptor_slot12,
                                             uint32_t descriptor_slot16,
                                             std::string_view name,
                                             std::string_view candidate,
                                             std::string_view source,
                                             bool refii_owned);
void RecordFrontendTextureConsumerResolver(uint8_t* base,
                                           uint32_t lr,
                                           uint32_t descriptor_guest,
                                           uint32_t resource_hash,
                                           uint32_t descriptor_vtable,
                                           uint32_t descriptor_slot12,
                                           uint32_t descriptor_slot16,
                                           uint32_t table_index,
                                           uint32_t table_object,
                                           std::string_view table_before,
                                           std::string_view table_after,
                                           uint32_t result_guest,
                                           bool refii_owned);
void RecordFrontendTextureConsumerStore(uint8_t* base,
                                        std::string_view api,
                                        uint32_t lr,
                                        uint32_t destination_guest,
                                        uint32_t source_guest,
                                        uint32_t value_guest);
void RecordFrontendTextureConsumerOwnerSlotSnapshot(uint8_t* base,
                                                    uint32_t owner_guest);
void RecordFrontendTextureConsumerStaticTableListCommit();
void RecordFrontendTextureConsumerFollowonFrameListCommit();
void RecordFrontendTextureConsumerResourceBoundary(std::string_view api,
                                                   uint32_t resource_hash,
                                                   uint32_t result_guest);

bool TrySynthesizeFrontendTextureDescriptorPhase(PPCContext& source_ctx,
                                                 uint8_t* base,
                                                 uint32_t owner_guest,
                                                 bool allow_followon_frame,
                                                bool allow_native_list200);
bool TryRunFrontendTextureConsumerNativeUpdateForPendingVfsDirect(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    std::string_view source_api);
bool TryCollapseFrontendTextureGeneratedHandoff(PPCContext& ctx,
                                                uint8_t* base,
                                                bool synthesize_missing_followon,
                                                std::string_view source_api);
bool TryRunActiveFrontendTextureConsumerSyntheticPhase(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t* out_owner_guest,
    bool* out_opened_any);

}  // namespace detail
}  // namespace refii::vfs
