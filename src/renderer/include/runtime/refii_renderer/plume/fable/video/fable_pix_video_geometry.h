#pragma once

#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FablePixVideoUvScale {
  bool valid = false;
  float output_width = 0.0f;
  float output_height = 0.0f;
  float decoded_width = 0.0f;
  float decoded_height = 0.0f;
  float u1 = 1.0f;
  float v1 = 1.0f;
};

bool FablePixVideoBuildFrameVertices(float width,
                                     float height,
                                     float left,
                                     float top,
                                     std::array<float, 6>& vertices);

bool FablePixVideoReadFrameVerticesFromArgs(uint8_t* base,
                                            uint32_t extent_pointer,
                                            uint32_t position_pointer,
                                            std::array<float, 6>& vertices);

uint32_t FablePixVideoSyntheticVertexBufferAddress(uint32_t packet,
                                                   uint32_t salt);

FablePixVideoUvScale FablePixVideoComputeUvScale(
    float decoded_width,
    float decoded_height,
    const std::array<float, 6>& vertices);

FablePixVideoUvScale FablePixVideoReadUvScaleFromDecodedExtent(
    uint8_t* base,
    uint32_t decoded_extent_pointer,
    const std::array<float, 6>& vertices);

FablePixVideoUvScale FablePixVideoReadUvScaleFromStack(
    uint8_t* base,
    uint32_t stack_pointer,
    const std::array<float, 6>& vertices);

uint32_t FablePixVideoOutputWidthFromVertices(
    const std::array<float, 6>& vertices);

uint32_t FablePixVideoOutputHeightFromVertices(
    const std::array<float, 6>& vertices);

void FablePixVideoConfigureOutputState(RendererDrawEventSnapshot& event,
                                       uint32_t width,
                                       uint32_t height);

void FablePixVideoConfigurePixelConstants(RendererDrawEventSnapshot& event);

}  // namespace refii::renderer
