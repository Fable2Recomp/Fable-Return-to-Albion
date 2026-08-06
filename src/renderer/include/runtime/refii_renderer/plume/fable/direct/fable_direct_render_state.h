#pragma once

#include "runtime/plume/draw/plume_draw_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

inline constexpr uint32_t kDirectPackedSamplerStateValid = 0x80000000u;
inline constexpr uint32_t kDirectPackedSamplerStateMagLinear = 0x00000001u;
inline constexpr uint32_t kDirectPackedSamplerStateMinLinear = 0x00000002u;
inline constexpr uint32_t kDirectPackedSamplerStateMipLinear = 0x00000008u;
inline constexpr uint32_t kDirectPackedSamplerStateAddressUShift = 8u;
inline constexpr uint32_t kDirectPackedSamplerStateAddressVShift = 11u;
inline constexpr uint32_t kDirectPackedSamplerStateAddressWShift = 14u;
inline constexpr uint32_t kDirectPackedSamplerStateAddressMask = 0x7u;
// Mip LOD bias, in the guest's own encoding: a SIGNED 10-bit fixed-point value
// in 1/32 steps, i.e. `bias * 32` truncated. IDA, D3DDevice_SetSamplerState_
// MipMapLodBias 0x821EC798: the DWORD argument is loaded as a float (`lfs f13`),
// scaled by 32.0, and inserted as 10 bits into fetch dword 4 [21:12]
// (`insrwi r8, r9, 10, 10`). We store the same number so the sampler key and
// the decoded bias are both exactly what the console programmed.
inline constexpr uint32_t kDirectPackedSamplerStateLodBiasShift = 17u;
inline constexpr uint32_t kDirectPackedSamplerStateLodBiasMask = 0x3FFu;
inline constexpr float kDirectSamplerLodBiasScale = 32.0f;

// Sign-extend and rescale the packed LOD bias back to a float.
inline float DirectSamplerLodBiasFromPackedState(uint32_t packed_state) {
  const uint32_t field = (packed_state >> kDirectPackedSamplerStateLodBiasShift) &
                         kDirectPackedSamplerStateLodBiasMask;
  // 10-bit two's complement.
  const int32_t signed_field =
      field >= 0x200u ? static_cast<int32_t>(field) - 0x400
                      : static_cast<int32_t>(field);
  return static_cast<float>(signed_field) / kDirectSamplerLodBiasScale;
}

inline constexpr uint32_t kDirectDefaultBlendControl = 0x00010001u;

uint32_t DirectPackedSamplerStateWithClampAddress(uint32_t packed_state);
// Derives the sampler address modes from a texture fetch constant's
// clamp_x/y/z fields — the console's authoritative per-binding addressing.
// Returns packed_state unchanged when the dwords are not a texture fetch.
uint32_t DirectPackedSamplerStateWithFetchClampAddress(
    uint32_t packed_state, const std::array<uint32_t, 6>& fetch_dwords);

uint32_t DirectPackedBlendControlFromRenderStates(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states);

uint32_t DirectBlendControlForTarget(const FableDirectRenderCommand& command,
                                     uint32_t slot);

bool DirectBlendEnabledForTarget(const FableDirectRenderCommand& command,
                                 uint32_t slot);

uint8_t DirectColorWriteMaskForTarget(
    const FableDirectRenderCommand& command, uint32_t slot);

uint8_t DirectColorWriteMaskForFramebufferTarget(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer, uint32_t framebuffer_slot);


plume::RenderBlendDesc DirectBlendDescFromEffectiveState(
    bool blend_enabled, uint32_t blend_control, uint8_t color_write_mask);

uint32_t DirectDepthControlFromRenderStates(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states);

uint32_t DirectDepthControl(const FableDirectRenderCommand& command);

uint32_t DirectStencilRefMaskFromRenderStates(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states);

uint32_t DirectStencilRefMask(const FableDirectRenderCommand& command,
                              bool back_face);

}  // namespace refii::renderer
