#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <cstdint>

namespace refii::renderer {

inline constexpr uint32_t kFableSyntheticPrivateRectVertexShader =
    0x0E000152u;
inline constexpr uint32_t kFableSyntheticPrivateRectPixelShader =
    0x0E010171u;
inline constexpr uint32_t kFableSyntheticPrivateRectDepthPixelShader =
    0x0E010136u;
inline constexpr uint32_t kFableSyntheticPrivateRectCopyPixelShader =
    0x0E010137u;
inline constexpr uint32_t kFablePrivateRectCopyTextureSlot = 13u;
inline constexpr uint64_t kFableGuiQuadVertexShaderHash =
    0x4724831317AA37A8ull;
inline constexpr uint64_t kFablePrivateRectDepthPixelShaderHash =
    0xE725F7105B73C0DBull;
inline constexpr uint64_t kFablePrivateRectCopyPixelShaderHash =
    0x9CEFC0421AC2A68Cull;

bool IsFableSyntheticPrivateRectCopyShader(uint32_t shader);

bool DirectSyntheticPrivateRectCommandUsesDepthTarget(
    const FableDirectRenderCommand& command);

FablePrivateTextureCopyKind DirectSyntheticPrivateRectCopyKindForTarget(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target);

bool DirectPrivateTextureCopyKindUsesColorBacking(
    FablePrivateTextureCopyKind kind);

bool DirectPrivateTextureCopyKindCanBlitToColorTarget(
    FablePrivateTextureCopyKind kind);

uint32_t DirectPrivateRectExactPixelShaderKey(
    uint32_t pixel_shader, FablePrivateTextureCopyKind kind);

bool DirectPrivateRectSyntheticPixelShadersCompatible(
    uint32_t command_pixel_shader, uint32_t action_pixel_shader,
    FablePrivateTextureCopyKind action_kind);

}  // namespace refii::renderer
