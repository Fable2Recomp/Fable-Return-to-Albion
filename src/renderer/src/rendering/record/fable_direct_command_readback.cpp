#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_readback.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace refii::renderer {

uint64_t HashDirectInlineBufferPrefix(const FableDirectInlineBuffer& buffer) {
  if (!buffer.ready()) {
    return 0;
  }
  constexpr uint64_t kFnvOffset = 1469598103934665603ull;
  constexpr uint64_t kFnvPrime = 1099511628211ull;
  uint64_t hash = kFnvOffset;
  const uint32_t byte_count =
      std::min<uint32_t>(buffer.byte_size, static_cast<uint32_t>(256u));
  for (uint32_t i = 0; i < byte_count; ++i) {
    hash ^= buffer.data[i];
    hash *= kFnvPrime;
  }
  return hash;
}

uint32_t ReadDirectInlineDword(const FableDirectInlineBuffer& buffer,
                               uint32_t dword_index) {
  const size_t byte_offset = size_t(dword_index) * sizeof(uint32_t);
  if (!buffer.ready() || byte_offset + sizeof(uint32_t) > buffer.data.size()) {
    return 0;
  }
  uint32_t value = 0;
  std::memcpy(&value, buffer.data.data() + byte_offset, sizeof(value));
  return value;
}

float DwordAsFloat(uint32_t value) {
  float result = 0.0f;
  std::memcpy(&result, &value, sizeof(result));
  return result;
}

float ReadDirectInlineFloat(const FableDirectInlineBuffer& buffer,
                            uint32_t byte_offset) {
  if (byte_offset + sizeof(float) > buffer.data.size()) {
    return 0.0f;
  }
  float value = 0.0f;
  std::memcpy(&value, buffer.data.data() + byte_offset, sizeof(value));
  return value;
}

uint32_t ReadDirectInlineIndex(const FableDirectInlineBuffer& buffer,
                               uint32_t index) {
  if (!buffer.ready()) {
    return 0;
  }
  const uint32_t stride = buffer.stride_or_format == 4u ? 4u : 2u;
  const size_t byte_offset = size_t(index) * stride;
  if (byte_offset + stride > buffer.data.size()) {
    return 0;
  }
  if (stride == 4u) {
    uint32_t value = 0;
    std::memcpy(&value, buffer.data.data() + byte_offset, sizeof(value));
    return value;
  }
  uint16_t value = 0;
  std::memcpy(&value, buffer.data.data() + byte_offset, sizeof(value));
  return value;
}

uint32_t DirectPixelConstantDword(const FableDirectRenderCommand& command,
                                  uint32_t constant_index,
                                  uint32_t component) {
  const uint32_t dword_index = constant_index * 4u + component;
  if (component >= 4u || dword_index >= command.constants.pixel_float.size()) {
    return 0;
  }
  return command.constants.pixel_float[dword_index];
}

uint32_t DirectVertexConstantDword(const FableDirectRenderCommand& command,
                                   uint32_t constant_index,
                                   uint32_t component) {
  const uint32_t dword_index = constant_index * 4u + component;
  if (component >= 4u || dword_index >= command.constants.vertex_float.size()) {
    return 0;
  }
  return command.constants.vertex_float[dword_index];
}

bool DirectPixelConstantQuadNonZero(const FableDirectRenderCommand& command,
                                    uint32_t constant_index) {
  for (uint32_t component = 0; component < 4u; ++component) {
    if (DirectPixelConstantDword(command, constant_index, component) != 0) {
      return true;
    }
  }
  return false;
}

bool DirectVertexConstantQuadNonZero(const FableDirectRenderCommand& command,
                                     uint32_t constant_index) {
  for (uint32_t component = 0; component < 4u; ++component) {
    if (DirectVertexConstantDword(command, constant_index, component) != 0) {
      return true;
    }
  }
  return false;
}

RendererDrawEventSnapshot BuildDrawEventFromCommandForShaderState(
    const FableDirectRenderCommand& command) {
  RendererDrawEventSnapshot event;
  event.draw = command.draw.draw;
  event.pm4_registers_valid = command.pipeline.pm4.valid;
  event.rb_colorcontrol = command.pipeline.pm4.rb_colorcontrol;
  event.rb_alpha_ref = command.pipeline.pm4.rb_alpha_ref;
  event.viewport = command.viewport;
  std::copy(command.pipeline.render_states.begin(),
            command.pipeline.render_states.end(), event.render_states.begin());
  return event;
}


}  // namespace refii::renderer
