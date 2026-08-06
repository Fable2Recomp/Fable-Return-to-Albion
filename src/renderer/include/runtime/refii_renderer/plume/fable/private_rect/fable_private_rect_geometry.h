#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FablePrivateRectDirectVertex {
  float position[4] = {};
  float texcoord0[4] = {};
  float texcoord1[4] = {};
};

struct FablePrivateRectUvBounds {
  bool valid = false;
  float u0 = 0.0f;
  float v0 = 0.0f;
  float u1 = 1.0f;
  float v1 = 1.0f;
};

struct FablePrivateRectTargetScaleInput {
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t target_width = 0;
  uint32_t target_height = 0;
};

struct FablePrivateRectTargetScaleResult {
  bool scaled = false;
  float scale_x = 1.0f;
  float scale_y = 1.0f;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t target_width = 0;
  uint32_t target_height = 0;
};

struct FablePrivateTextureRectangleObserverGeometryCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
  bool (*is_exact_stack_rect_return_address)(uint32_t lr) = nullptr;
  uint32_t (*current_pixel_center_offset_render_state)() = nullptr;
  std::array<uint32_t, 6> (*load_texture_fetch_dwords)(
      uint8_t* base,
      uint32_t source_address) = nullptr;
};

uint32_t MixSyntheticDirectAddress(uint32_t value);

uint32_t SyntheticDirectScratchBufferAddress(uint32_t namespace_base,
                                             uint32_t source,
                                             uint32_t salt);
uint32_t SyntheticPrivateTextureRectangleVertexBufferAddress(uint32_t source,
                                                            uint32_t salt);

std::array<FablePrivateRectDirectVertex, 6>
BuildPrivateTextureRectangleDirectVertices(
    const std::array<float, 6>& corners,
    float u0 = 0.0f,
    float v0 = 0.0f,
    float u1 = 1.0f,
    float v1 = 1.0f);

std::array<FablePrivateRectDirectVertex, 3>
BuildPrivateTextureRectangleSingleTriangleDirectVertices(
    const std::array<float, 6>& corners,
    float u0 = 0.0f,
    float v0 = 0.0f,
    float u1 = 1.0f,
    float v1 = 1.0f);

FablePrivateRectUvBounds ComputePrivateTextureRectangleUvBounds(
    uint32_t source_width,
    uint32_t source_height,
    const std::array<float, 6>& corners,
    float source_x,
    float source_y);

bool FablePrivateTextureRectangleScaleVerticesToTarget(
    FablePrivateRectTargetScaleInput input,
    std::array<float, 6>& vertices,
    FablePrivateRectTargetScaleResult* result = nullptr);

bool FablePrivateTextureRectangleExtentFromFetchDwords(
    const std::array<uint32_t, 6>& dwords,
    uint32_t& right,
    uint32_t& bottom);

bool FablePrivateTextureRectangleFetchLooksLikeTextureSource(
    const std::array<uint32_t, 6>& dwords);

bool FablePrivateTextureRectangleCornersFromRect(
    uint32_t left,
    uint32_t top,
    uint32_t right,
    uint32_t bottom,
    bool pixel_center_offset_enabled,
    std::array<float, 6>& corners);

float FablePrivateTextureSourceCoordinateFromRaw(uint32_t raw);

RendererScissorSnapshot FablePrivateTextureRectangleClearRectFromCorners(
    const RendererDrawEventSnapshot& event);

bool FablePrivateTextureRectangleTargetExtent(
    const RendererDrawEventSnapshot& event,
    uint32_t& width,
    uint32_t& height);

uint32_t PackPrivateTextureRectangleSurfaceExtent(uint32_t width,
                                                  uint32_t height);

uint32_t FablePrivateTextureRectangleSourceFetchAddress(
    const RendererHookArgs& args,
    uint8_t* base,
    uint32_t source,
    const FablePrivateTextureRectangleObserverGeometryCallbacks& callbacks);

bool FablePrivateTextureRectangleReasonableVertexBuffer(
    uint8_t* base,
    uint32_t vertex_buffer);

uint32_t FablePrivateTextureRectangleFindVertexBuffer(
    uint8_t* base,
    uint32_t device_pointer);

bool FablePrivateTextureRectangleBuildVerticesFromArgs(
    const RendererHookArgs& args,
    uint8_t* base,
    const FablePrivateTextureRectangleObserverGeometryCallbacks& callbacks,
    std::array<float, 6>& vertices);

float FablePrivateTextureRectangleSourceCoordinate(
    uint8_t* base,
    uint32_t address,
    const FablePrivateTextureRectangleObserverGeometryCallbacks& callbacks);

}  // namespace refii::renderer
