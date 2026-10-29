#pragma once

#include "vfs_hook_types.h"

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {
uint32_t NormalizeRefiiResourceStreamWrapper(uint8_t* base,
                                             uint32_t wrapper_guest);
bool EnsureRefiiResourceWrapperMaterialized(PPCContext& ctx,
                                            uint8_t* base,
                                            uint32_t wrapper_guest);
int32_t GetRefiiResourceWrapperStatusNonBlocking(uint8_t* base,
                                                 uint32_t wrapper_guest,
                                                 uint32_t* inner_guest_out,
                                                 uint32_t* source_stream_out,
                                                 uint32_t* requested_out,
                                                 uint32_t* bytes_out,
                                                 uint32_t* status_word_out,
                                                 uint32_t* buffer_out);
uint32_t GetAnimationTocPayloadOffset(uint8_t* base,
                                      uint32_t buffer_guest,
                                      uint32_t size,
                                      std::string_view provider_key);
bool ShouldWarnCompletedMenuTextureStatus(uint32_t wrapper_guest);
RefiiResourceWrapperView GetRefiiResourceWrapperView(PPCContext& ctx,
                                                     uint8_t* base,
                                                     uint32_t wrapper_guest,
                                                     std::string_view api);
std::string GetRefiiResourceWrapperProviderKey(uint8_t* base,
                                               uint32_t wrapper_guest,
                                               uint32_t source_stream_guest);
void ForgetRefiiTextureWrapperOwner(uint32_t wrapper_guest);
uint32_t FindRefiiTextureWrapperOwner(uint32_t wrapper_guest);
bool MaybeAugmentRefiiSplitTextureWrapperPayloadView(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t wrapper_guest,
    std::string_view api,
    RefiiResourceWrapperView* view);
std::string FormatTextureOwnerWords(uint8_t* base, uint32_t texture_guest);
std::string FormatGuestWordsIfPointer(uint8_t* base,
                                      uint32_t guest,
                                      uint32_t word_count);
std::string FormatGuestWordsAtOffsetIfPointer(uint8_t* base,
                                              uint32_t guest,
                                              uint32_t offset,
                                              uint32_t word_count);
}  // namespace detail
}  // namespace refii::vfs
