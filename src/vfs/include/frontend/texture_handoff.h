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

bool FrontendTextureConsumerTraceActive();
bool FrontendTextureConsumerVerboseTraceActive();
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
void RecordFrontendTextureConsumerResourceBoundary(std::string_view api,
                                                   uint32_t resource_hash,
                                                   uint32_t result_guest);

}  // namespace detail
}  // namespace refii::vfs
