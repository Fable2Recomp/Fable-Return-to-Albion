#pragma once

#include "runtime/plume/direct/fable_render_types.h"

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
plume::RenderPrimitiveTopology ConvertPrimitiveTopology(
    uint32_t primitive_type);
uint32_t DrawVertexOrIndexCount(uint32_t primitive_type,
                                uint32_t captured_count);
bool IsFablePrimitivePolygonal(uint32_t primitive_type);
bool IsFableRasterizationPotentiallyDone(
    const RendererDrawEventSnapshot& draw_event);
uint32_t CountBoundRenderTargets(const RendererDrawEventSnapshot& draw_event);
bool DrawHasSparseRenderTargetSlots(
    const RendererDrawEventSnapshot& draw_event);
bool DrawHasAnyRenderTarget(const RendererDrawEventSnapshot& draw_event);
bool DrawTargetsContainRenderTarget(
    const RendererDrawEventSnapshot& draw_event, uint32_t render_target);
bool ShouldDirectDrawToCurrentOutput(
    const RendererDrawEventSnapshot& draw_event,
    uint32_t present_render_target);
bool DrawTargetsSelectedPresentTarget(
    const RendererDrawEventSnapshot& draw_event,
    uint32_t present_render_target);
bool HasInputLocation(
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t location);
bool HasInputSlot(const std::vector<plume::RenderInputSlot>& input_slots,
                  uint32_t slot);
plume::RenderFormat DefaultInputFormatForUsage(uint8_t usage);
uint32_t InputFormatByteSize(plume::RenderFormat format);
uint32_t InputLayoutSlotMaxElementEnd(
    const std::vector<plume::RenderInputElement>& elements, uint32_t slot);
std::string FormatInputLayoutSummary(
    const std::vector<plume::RenderInputSlot>& slots,
    const std::vector<plume::RenderInputElement>& elements);
std::vector<plume::RenderInputElement> ParseShaderInputSignature(
    const std::string& hlsl_path);
plume::RenderFormat HeuristicShaderInputFormat(
    const plume::RenderInputElement& shader_input);
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
                                     plume::RenderBlend fallback);
plume::RenderBlend ConvertFableAlphaBlend(uint32_t blend,
                                          plume::RenderBlend fallback);
plume::RenderBlendOperation ConvertFableBlendOp(
    uint32_t blend_op, plume::RenderBlendOperation fallback);
plume::RenderComparisonFunction ConvertFableCompare(
    uint32_t compare, plume::RenderComparisonFunction fallback);
plume::RenderCullMode ConvertFableCullMode(uint32_t cull_mode);
plume::RenderFrontFace ConvertFableFrontFace(uint32_t cull_mode);
plume::RenderStencilOp ConvertFableStencilOp(uint32_t op);
plume::RenderStencilFaceDesc FableStencilFaceFromDepthControl(
    uint32_t depth_control, bool back_face);
#endif

}  // namespace refii::renderer
