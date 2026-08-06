#pragma once

#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

#include <cstdint>
#include <string>
#include <vector>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
inline constexpr uint32_t kDefaultVertexInputSlot = 15;
inline constexpr uint32_t kDefaultVertexInputStride = 4;

enum FableRenderStateSlot : uint32_t {
  kFableRenderStateZFunc = 0,
  kFableRenderStateColorWriteEnable = 1,
  kFableRenderStateAlphaBlendEnable = 2,
  kFableRenderStateSrcBlend = 3,
  kFableRenderStateZWriteEnable = 4,
  kFableRenderStateDestBlend = 5,
  kFableRenderStateAlphaTestEnable = 6,
  kFableRenderStateZEnable = 7,
  kFableRenderStateHiZWriteEnable = 8,
  kFableRenderStateBlendOp = 9,
  kFableRenderStateBlendOpAlpha = 10,
  kFableRenderStateSrcBlendAlpha = 11,
  kFableRenderStateDestBlendAlpha = 12,
  kFableRenderStateSeparateAlphaBlendEnable = 13,
  kFableRenderStateClipPlaneEnable = 14,
  kFableRenderStateHighPrecisionBlendEnable = 15,
  kFableRenderStateStencilFail = 16,
  kFableRenderStateStencilZFail = 17,
  kFableRenderStateStencilEnable = 18,
  kFableRenderStateCullMode = 19,
  kFableRenderStateStencilPass = 20,
  kFableRenderStateStencilFunc = 21,
  kFableRenderStateStencilReadMask = 22,
  kFableRenderStateStencilRef = 23,
  kFableRenderStateAlphaFunc = 24,
  kFableRenderStateAlphaRef = 25,
  kFableRenderStatePixelCenterOffset = 26,
  kFableRenderStateStencilWriteMask = 27,
};

uint32_t PackF32(float value);
float UnpackF32(uint32_t bits);
uint32_t FableAlphaFunc(const RendererDrawEventSnapshot& draw_event);
bool FableAlphaTestEnabled(const RendererDrawEventSnapshot& draw_event);
bool FableViewportUsesReverseZ(const RendererDrawEventSnapshot& draw_event);
uint32_t FableAlphaThresholdBits(
    const RendererDrawEventSnapshot& draw_event);
// Scalar cores of the alpha-test helpers; the snapshot overloads above
// forward here. The per-draw submit path (FillDirectSharedConstants) used to
// default-construct a full RendererDrawEventSnapshot — ~13 KB of zero-init,
// two float banks included — per recorded draw just to reach the four alpha
// fields these take directly.
uint32_t FableAlphaFunc(bool pm4_registers_valid, uint32_t rb_colorcontrol,
                        uint32_t rs_alpha_func);
bool FableAlphaTestEnabled(bool pm4_registers_valid, uint32_t rb_colorcontrol,
                           uint32_t rs_alpha_test_enable,
                           uint32_t rs_alpha_func);
uint32_t FableAlphaThresholdBits(bool pm4_registers_valid,
                                 uint32_t rb_colorcontrol,
                                 uint32_t rb_alpha_ref,
                                 uint32_t rs_alpha_test_enable,
                                 uint32_t rs_alpha_func,
                                 uint32_t rs_alpha_ref);
plume::RenderPrimitiveTopology ConvertPrimitiveTopology(
    uint32_t primitive_type);
uint32_t DrawVertexOrIndexCount(uint32_t primitive_type,
                                uint32_t captured_count);
bool HasInputLocation(
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t location);
bool HasInputSlot(const std::vector<plume::RenderInputSlot>& input_slots,
                  uint32_t slot);
plume::RenderFormat DefaultInputFormatForUsage(uint8_t usage);
uint32_t InputFormatByteSize(plume::RenderFormat format);
uint32_t InputLayoutSlotMaxElementEnd(
    const std::vector<plume::RenderInputElement>& elements, uint32_t slot);
std::vector<plume::RenderInputElement> ParseShaderInputSignature(
    const std::string& hlsl_path);
void FilterInputElementsToShaderInputs(
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>& shader_inputs);
void RemapInputLocationsFromShader(
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>& shader_inputs);
void NormalizeInputElementFormatsToBoundStreams(
    const RendererStreamBindingSnapshot* streams, size_t stream_count,
    std::vector<plume::RenderInputElement>& input_elements);
void NormalizeInputElementFormatsToBoundStreams(
    const RendererDrawEventSnapshot& draw_event,
    std::vector<plume::RenderInputElement>& input_elements);
void PadMissingInputLocations(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>* shader_inputs = nullptr);
void TrimInputSlotsToElements(
    std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements);
plume::RenderBlend ConvertFableBlend(uint32_t blend,
                                     plume::RenderBlend default_value);
plume::RenderBlend ConvertFableAlphaBlend(uint32_t blend,
                                          plume::RenderBlend default_value);
plume::RenderBlendOperation ConvertFableBlendOp(
    uint32_t blend_op, plume::RenderBlendOperation default_value);
plume::RenderComparisonFunction ConvertFableCompare(
    uint32_t compare, plume::RenderComparisonFunction default_value);
plume::RenderCullMode ConvertFableCullMode(uint32_t cull_mode);
plume::RenderFrontFace ConvertFableFrontFace(uint32_t cull_mode);
plume::RenderStencilOp ConvertFableStencilOp(uint32_t op);
plume::RenderStencilFaceDesc FableStencilFaceFromDepthControl(
    uint32_t depth_control, bool back_face);
#endif

}  // namespace refii::renderer
