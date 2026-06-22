#include "runtime/plume/draw/plume_draw_state.h"

#include "runtime/plume/resource/plume_vertex_declaration.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iterator>
#include <string>
#include <string_view>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {

constexpr float kFallbackAlphaTestThreshold = 1.0f / 255.0f;

const char* StaticSemanticName(std::string_view semantic) {
  if (semantic == "POSITION") return "POSITION";
  if (semantic == "NORMAL") return "NORMAL";
  if (semantic == "TANGENT") return "TANGENT";
  if (semantic == "BINORMAL") return "BINORMAL";
  if (semantic == "TEXCOORD") return "TEXCOORD";
  if (semantic == "COLOR") return "COLOR";
  if (semantic == "BLENDWEIGHT") return "BLENDWEIGHT";
  if (semantic == "BLENDINDICES") return "BLENDINDICES";
  return nullptr;
}

uint8_t GuestDeclUsageFromSemantic(std::string_view semantic) {
  if (semantic == "POSITION") return 0;
  if (semantic == "BLENDWEIGHT") return 1;
  if (semantic == "BLENDINDICES") return 2;
  if (semantic == "NORMAL") return 3;
  if (semantic == "TEXCOORD") return 5;
  if (semantic == "TANGENT") return 6;
  if (semantic == "BINORMAL") return 7;
  if (semantic == "COLOR") return 10;
  return UINT8_MAX;
}

plume::RenderFormat ShaderInputTypeFormat(std::string_view type) {
  if (type == "float") return plume::RenderFormat::R32_FLOAT;
  if (type == "float2") return plume::RenderFormat::R32G32_FLOAT;
  if (type == "float3") return plume::RenderFormat::R32G32B32_FLOAT;
  if (type == "float4") return plume::RenderFormat::R32G32B32A32_FLOAT;
  if (type == "uint") return plume::RenderFormat::R32_UINT;
  if (type == "uint2") return plume::RenderFormat::R32G32_UINT;
  if (type == "uint3") return plume::RenderFormat::R32G32B32_UINT;
  if (type == "uint4") return plume::RenderFormat::R32G32B32A32_UINT;
  return plume::RenderFormat::UNKNOWN;
}

bool ParseShaderSemanticToken(std::string_view token, std::string& semantic,
                              uint32_t& semantic_index) {
  semantic.clear();
  semantic_index = 0;
  while (!token.empty() &&
         std::isspace(static_cast<unsigned char>(token.front()))) {
    token.remove_prefix(1);
  }
  size_t end = token.find_first_of(", \t\r\n)");
  if (end == std::string_view::npos) {
    end = token.size();
  }
  token = token.substr(0, end);
  while (!token.empty() &&
         (token.back() == ',' || token.back() == '\r' || token.back() == '\n')) {
    token.remove_suffix(1);
  }
  size_t digit_start = token.size();
  while (digit_start > 0 &&
         std::isdigit(static_cast<unsigned char>(token[digit_start - 1]))) {
    --digit_start;
  }
  if (digit_start == 0 || digit_start == token.size()) {
    return false;
  }
  semantic.assign(token.substr(0, digit_start));
  for (size_t i = digit_start; i < token.size(); ++i) {
    semantic_index =
        semantic_index * 10u + static_cast<uint32_t>(token[i] - '0');
  }
  return true;
}

bool ParseShaderInputLine(std::string_view line, uint32_t& location,
                          plume::RenderFormat& declared_format,
                          std::string& semantic,
                          uint32_t& semantic_index,
                          uint32_t& next_auto_location) {
  while (!line.empty() &&
         std::isspace(static_cast<unsigned char>(line.front()))) {
    line.remove_prefix(1);
  }

  bool has_explicit_location = false;
  if (line.starts_with("[[vk::location(")) {
    const size_t location_begin = std::string_view("[[vk::location(").size();
    const size_t location_end = line.find(")", location_begin);
    if (location_end == std::string_view::npos ||
        location_end <= location_begin) {
      return false;
    }

    location = 0;
    for (size_t i = location_begin; i < location_end; ++i) {
      if (!std::isdigit(static_cast<unsigned char>(line[i]))) {
        return false;
      }
      location = location * 10u + static_cast<uint32_t>(line[i] - '0');
    }
    has_explicit_location = true;

    const size_t close = line.find("]]", location_end);
    if (close == std::string_view::npos) {
      return false;
    }
    line.remove_prefix(close + 2);
    while (!line.empty() &&
           std::isspace(static_cast<unsigned char>(line.front()))) {
      line.remove_prefix(1);
    }
  }

  if (!line.starts_with("in ")) {
    return false;
  }
  line.remove_prefix(3);
  while (!line.empty() &&
         std::isspace(static_cast<unsigned char>(line.front()))) {
    line.remove_prefix(1);
  }

  const size_t colon = line.find(':');
  if (colon == std::string_view::npos) {
    return false;
  }

  const size_t type_end = line.find_first_of(" \t\r\n", 0);
  declared_format =
      type_end != std::string_view::npos
          ? ShaderInputTypeFormat(line.substr(0, type_end))
          : plume::RenderFormat::UNKNOWN;

  if (!ParseShaderSemanticToken(line.substr(colon + 1), semantic,
                                semantic_index)) {
    return false;
  }

  if (!has_explicit_location) {
    location = next_auto_location;
  }
  next_auto_location = std::max(next_auto_location, location + 1u);
  return true;
}

uint32_t FableDepthControlStencilFail(uint32_t depth_control,
                                      bool back_face) {
  return back_face ? ((depth_control >> 23u) & 0x7u)
                   : ((depth_control >> 11u) & 0x7u);
}

uint32_t FableDepthControlStencilZPass(uint32_t depth_control,
                                       bool back_face) {
  return back_face ? ((depth_control >> 26u) & 0x7u)
                   : ((depth_control >> 14u) & 0x7u);
}

uint32_t FableDepthControlStencilZFail(uint32_t depth_control,
                                       bool back_face) {
  return back_face ? ((depth_control >> 29u) & 0x7u)
                   : ((depth_control >> 17u) & 0x7u);
}

uint32_t FableDepthControlStencilFunc(uint32_t depth_control,
                                      bool back_face) {
  return back_face ? ((depth_control >> 20u) & 0x7u)
                   : ((depth_control >> 8u) & 0x7u);
}

}  // namespace

uint32_t PackF32(float value) {
  uint32_t bits = 0;
  static_assert(sizeof(bits) == sizeof(value));
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

float UnpackF32(uint32_t bits) {
  float value = 0.0f;
  static_assert(sizeof(bits) == sizeof(value));
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

uint32_t FableAlphaFunc(const RendererDrawEventSnapshot& draw_event) {
  return draw_event.pm4_registers_valid
             ? (draw_event.rb_colorcontrol & 0x7u)
             : draw_event.render_states[kFableRenderStateAlphaFunc];
}

bool FableAlphaFuncMatchesGeneratedClip(uint32_t alpha_func) {
  // The translated XenosRecomp shaders currently expose one alpha-test shape:
  // clip(oC0.a - g_AlphaThreshold), which represents GREATER/GREATER_EQUAL.
  return alpha_func == 4u || alpha_func == 6u;
}

bool FableAlphaTestEnabled(const RendererDrawEventSnapshot& draw_event) {
  if (draw_event.pm4_registers_valid) {
    const uint32_t alpha_func = FableAlphaFunc(draw_event);
    const bool alpha_test_enable = (draw_event.rb_colorcontrol & 0x8u) != 0;
    return alpha_test_enable && FableAlphaFuncMatchesGeneratedClip(alpha_func);
  }
  return draw_event.render_states[kFableRenderStateAlphaTestEnable] != 0 &&
         FableAlphaFuncMatchesGeneratedClip(FableAlphaFunc(draw_event));
}

bool FableViewportUsesReverseZ(const RendererDrawEventSnapshot& draw_event) {
  return draw_event.viewport.valid &&
         std::isfinite(draw_event.viewport.min_depth) &&
         std::isfinite(draw_event.viewport.max_depth) &&
         draw_event.viewport.min_depth > draw_event.viewport.max_depth;
}

uint32_t FableAlphaThresholdBits(const RendererDrawEventSnapshot& draw_event) {
  if (!FableAlphaTestEnabled(draw_event)) {
    return 0;
  }
  if (draw_event.pm4_registers_valid) {
    float alpha_ref = UnpackF32(draw_event.rb_alpha_ref);
    if (!std::isfinite(alpha_ref) || alpha_ref < 0.0f || alpha_ref > 1.0f ||
        (draw_event.rb_alpha_ref != 0 && alpha_ref < (1.0f / 65535.0f) &&
         draw_event.rb_alpha_ref <= 0xFFu)) {
      alpha_ref = static_cast<float>(draw_event.rb_alpha_ref & 0xFFu) /
                  255.0f;
    }
    if (FableAlphaFunc(draw_event) == 4u) {
      alpha_ref = std::min(1.0f, alpha_ref + (0.5f / 255.0f));
    }
    return PackF32(alpha_ref);
  }
  float alpha_ref =
      static_cast<float>(draw_event.render_states[kFableRenderStateAlphaRef] &
                         0xFFu) /
      255.0f;
  if (draw_event.render_states[kFableRenderStateAlphaRef] == 0) {
    alpha_ref = kFallbackAlphaTestThreshold;
  }
  return PackF32(alpha_ref);
}

plume::RenderPrimitiveTopology ConvertPrimitiveTopology(
    uint32_t primitive_type) {
  switch (primitive_type) {
    case 1:
      return plume::RenderPrimitiveTopology::POINT_LIST;
    case 2:
      return plume::RenderPrimitiveTopology::LINE_LIST;
    case 3:
      return plume::RenderPrimitiveTopology::LINE_STRIP;
    case 4:
    case 8:
    case 13:
      return plume::RenderPrimitiveTopology::TRIANGLE_LIST;
    case 5:
      return plume::RenderPrimitiveTopology::TRIANGLE_FAN;
    case 6:
      return plume::RenderPrimitiveTopology::TRIANGLE_STRIP;
    default:
      return plume::RenderPrimitiveTopology::UNKNOWN;
  }
}

uint32_t DrawVertexOrIndexCount(uint32_t primitive_type,
                                uint32_t captured_count) {
  switch (primitive_type) {
    case 1:
    case 2:
    case 3:
    case 4:
    case 5:
    case 6:
    case 8:
    case 13:
      return captured_count;
    default:
      return 0;
  }
}

bool IsFablePrimitivePolygonal(uint32_t primitive_type) {
  switch (primitive_type) {
    case 4:   // Triangle list.
    case 5:   // Triangle fan.
    case 6:   // Triangle strip.
    case 7:   // Triangle with edge/w flags.
    case 13:  // Quad list.
    case 14:  // Quad strip.
    case 15:  // Polygon.
      return true;
    default:
      return false;
  }
}

bool IsFableRasterizationPotentiallyDone(
    const RendererDrawEventSnapshot& draw_event) {
  if (!draw_event.pm4_registers_valid) {
    return true;
  }

  const uint32_t edram_mode = draw_event.rb_modecontrol & 0x7u;
  if (edram_mode != 4u && edram_mode != 5u) {
    return false;
  }
  if ((draw_event.rb_surface_info & 0x3FFFu) == 0) {
    return false;
  }
  if (IsFablePrimitivePolygonal(draw_event.draw.primitive_type) &&
      (draw_event.pa_su_sc_mode_cntl & 0x3u) == 0x3u) {
    return false;
  }
  return true;
}

uint32_t CountBoundRenderTargets(const RendererDrawEventSnapshot& draw_event) {
  std::array<uint32_t, std::size(draw_event.render_targets)> seen_targets = {};
  uint32_t seen_count = 0;
  for (uint32_t render_target : draw_event.render_targets) {
    if (render_target == 0) {
      continue;
    }
    bool seen = false;
    for (uint32_t i = 0; i < seen_count; ++i) {
      if (seen_targets[i] == render_target) {
        seen = true;
        break;
      }
    }
    if (seen) {
      continue;
    }
    if (seen_count < seen_targets.size()) {
      seen_targets[seen_count] = render_target;
    }
    ++seen_count;
  }
  return seen_count;
}

bool DrawHasSparseRenderTargetSlots(
    const RendererDrawEventSnapshot& draw_event) {
  bool saw_empty_slot = false;
  for (uint32_t render_target : draw_event.render_targets) {
    if (render_target == 0) {
      saw_empty_slot = true;
      continue;
    }
    if (saw_empty_slot) {
      return true;
    }
  }
  return false;
}

bool DrawHasAnyRenderTarget(const RendererDrawEventSnapshot& draw_event) {
  return CountBoundRenderTargets(draw_event) != 0;
}

bool DrawTargetsContainRenderTarget(
    const RendererDrawEventSnapshot& draw_event, uint32_t render_target) {
  if (render_target == 0) {
    return false;
  }
  for (uint32_t bound_render_target : draw_event.render_targets) {
    if (bound_render_target == render_target) {
      return true;
    }
  }
  return false;
}

bool ShouldDirectDrawToCurrentOutput(
    const RendererDrawEventSnapshot& draw_event,
    uint32_t present_render_target) {
  if (present_render_target == 0) {
    return true;
  }
  return DrawTargetsContainRenderTarget(draw_event, present_render_target);
}

bool DrawTargetsSelectedPresentTarget(
    const RendererDrawEventSnapshot& draw_event,
    uint32_t present_render_target) {
  return DrawTargetsContainRenderTarget(draw_event, present_render_target);
}

bool HasInputLocation(
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t location) {
  return std::any_of(input_elements.begin(), input_elements.end(),
                     [location](const plume::RenderInputElement& element) {
                       return element.location == location;
                     });
}

bool HasInputSlot(const std::vector<plume::RenderInputSlot>& input_slots,
                  uint32_t slot) {
  return std::any_of(input_slots.begin(), input_slots.end(),
                     [slot](const plume::RenderInputSlot& input_slot) {
                       return input_slot.index == slot;
                     });
}

plume::RenderFormat DefaultInputFormatForUsage(uint8_t usage) {
  switch (usage) {
    case 2:
    case 3:
    case 6:
    case 7:
      return plume::RenderFormat::R32_UINT;
    default:
      return plume::RenderFormat::R32_FLOAT;
  }
}

uint32_t InputFormatByteSize(plume::RenderFormat format) {
  switch (format) {
    case plume::RenderFormat::R32_FLOAT:
    case plume::RenderFormat::R32_UINT:
    case plume::RenderFormat::R32_SINT:
    case plume::RenderFormat::B8G8R8A8_UNORM:
    case plume::RenderFormat::R8G8B8A8_UNORM:
    case plume::RenderFormat::R8G8B8A8_UINT:
    case plume::RenderFormat::R8G8B8A8_SNORM:
    case plume::RenderFormat::R8G8B8A8_SINT:
    case plume::RenderFormat::R16G16_FLOAT:
    case plume::RenderFormat::R16G16_UNORM:
    case plume::RenderFormat::R16G16_UINT:
    case plume::RenderFormat::R16G16_SNORM:
    case plume::RenderFormat::R16G16_SINT:
      return 4;
    case plume::RenderFormat::R16_FLOAT:
    case plume::RenderFormat::R16_UNORM:
    case plume::RenderFormat::R16_UINT:
    case plume::RenderFormat::R16_SNORM:
    case plume::RenderFormat::R16_SINT:
    case plume::RenderFormat::R8G8_UNORM:
    case plume::RenderFormat::R8G8_UINT:
    case plume::RenderFormat::R8G8_SNORM:
    case plume::RenderFormat::R8G8_SINT:
      return 2;
    case plume::RenderFormat::R8_UNORM:
    case plume::RenderFormat::R8_UINT:
    case plume::RenderFormat::R8_SNORM:
    case plume::RenderFormat::R8_SINT:
      return 1;
    case plume::RenderFormat::R32G32_FLOAT:
    case plume::RenderFormat::R32G32_UINT:
    case plume::RenderFormat::R32G32_SINT:
    case plume::RenderFormat::R16G16B16A16_FLOAT:
    case plume::RenderFormat::R16G16B16A16_UNORM:
    case plume::RenderFormat::R16G16B16A16_UINT:
    case plume::RenderFormat::R16G16B16A16_SNORM:
    case plume::RenderFormat::R16G16B16A16_SINT:
      return 8;
    case plume::RenderFormat::R32G32B32_FLOAT:
    case plume::RenderFormat::R32G32B32_UINT:
    case plume::RenderFormat::R32G32B32_SINT:
      return 12;
    case plume::RenderFormat::R32G32B32A32_FLOAT:
    case plume::RenderFormat::R32G32B32A32_UINT:
    case plume::RenderFormat::R32G32B32A32_SINT:
      return 16;
    default:
      return 0;
  }
}

uint32_t InputLayoutSlotMaxElementEnd(
    const std::vector<plume::RenderInputElement>& elements, uint32_t slot) {
  uint32_t max_end = 0;
  for (const plume::RenderInputElement& element : elements) {
    if (element.slotIndex != slot) {
      continue;
    }
    const uint32_t element_size = InputFormatByteSize(element.format);
    if (element_size == 0) {
      continue;
    }
    max_end =
        std::max(max_end, element.alignedByteOffset + element_size);
  }
  return max_end;
}

std::string FormatInputLayoutSummary(
    const std::vector<plume::RenderInputSlot>& slots,
    const std::vector<plume::RenderInputElement>& elements) {
  std::string summary = "slots[";
  for (size_t i = 0; i < slots.size(); ++i) {
    if (i != 0) {
      summary += ",";
    }
    summary += std::to_string(slots[i].index) + ":" +
               std::to_string(slots[i].stride);
  }
  summary += "] elems[";
  const size_t element_limit = std::min<size_t>(elements.size(), 6);
  for (size_t i = 0; i < element_limit; ++i) {
    if (i != 0) {
      summary += ",";
    }
    const plume::RenderInputElement& element = elements[i];
    summary +=
        std::string(element.semanticName ? element.semanticName : "?") +
        std::to_string(element.semanticIndex) + "@l" +
        std::to_string(element.location) + "/s" +
        std::to_string(element.slotIndex) + "+" +
        std::to_string(element.alignedByteOffset) + "/f" +
        std::to_string(static_cast<uint32_t>(element.format));
  }
  if (elements.size() > element_limit) {
    summary += ",...";
  }
  summary += "]";
  return summary;
}

std::vector<plume::RenderInputElement> ParseShaderInputSignature(
    const std::string& hlsl_path) {
  std::vector<plume::RenderInputElement> inputs;
  if (hlsl_path.empty()) {
    return inputs;
  }

  std::ifstream file(hlsl_path);
  if (!file) {
    return inputs;
  }

  std::string line;
  uint32_t next_auto_location = 0;
  while (std::getline(file, line)) {
    uint32_t location = 0;
    plume::RenderFormat declared_format = plume::RenderFormat::UNKNOWN;
    std::string semantic;
    uint32_t semantic_index = 0;
    if (!ParseShaderInputLine(line, location, declared_format, semantic,
                              semantic_index, next_auto_location)) {
      continue;
    }

    const char* semantic_name = StaticSemanticName(semantic);
    if (!semantic_name) {
      continue;
    }

    const uint8_t usage = GuestDeclUsageFromSemantic(semantic);
    plume::RenderFormat format =
        declared_format != plume::RenderFormat::UNKNOWN
            ? declared_format
            : (usage != UINT8_MAX ? DefaultInputFormatForUsage(usage)
                                  : plume::RenderFormat::R32_FLOAT);
    if (declared_format == plume::RenderFormat::UNKNOWN) {
      if (semantic == "POSITION") {
        format = plume::RenderFormat::R32G32B32A32_FLOAT;
      } else if (semantic == "TEXCOORD") {
        format = plume::RenderFormat::R32G32_FLOAT;
      } else if (semantic == "COLOR") {
        format = plume::RenderFormat::B8G8R8A8_UNORM;
      } else if (semantic == "BLENDWEIGHT") {
        format = plume::RenderFormat::R32G32B32A32_FLOAT;
      }
    }

    inputs.emplace_back(semantic_name, semantic_index, location, format, 0, 0);
  }

  std::sort(inputs.begin(), inputs.end(),
            [](const plume::RenderInputElement& lhs,
               const plume::RenderInputElement& rhs) {
              if (lhs.location != rhs.location) {
                return lhs.location < rhs.location;
              }
              return lhs.semanticIndex < rhs.semanticIndex;
            });
  inputs.erase(std::unique(inputs.begin(), inputs.end(),
                           [](const plume::RenderInputElement& lhs,
                              const plume::RenderInputElement& rhs) {
                             return lhs.location == rhs.location;
                           }),
               inputs.end());
  return inputs;
}

plume::RenderFormat HeuristicShaderInputFormat(
    const plume::RenderInputElement& shader_input) {
  const std::string_view semantic =
      shader_input.semanticName != nullptr ? shader_input.semanticName : "";
  if (semantic == "POSITION") {
    return plume::RenderFormat::R32G32B32_FLOAT;
  }
  if (semantic == "NORMAL" || semantic == "TANGENT" ||
      semantic == "BINORMAL" || semantic == "BLENDINDICES") {
    return plume::RenderFormat::R32_UINT;
  }
  if (semantic == "COLOR") {
    return plume::RenderFormat::B8G8R8A8_UNORM;
  }
  if (semantic == "TEXCOORD") {
    return plume::RenderFormat::R32G32_FLOAT;
  }
  if (semantic == "BLENDWEIGHT") {
    return plume::RenderFormat::R32G32B32A32_FLOAT;
  }
  return shader_input.format != plume::RenderFormat::UNKNOWN
             ? shader_input.format
             : plume::RenderFormat::R32_FLOAT;
}

bool InputSemanticMatches(const plume::RenderInputElement& lhs,
                          const plume::RenderInputElement& rhs) {
  const std::string_view lhs_semantic =
      lhs.semanticName != nullptr ? lhs.semanticName : "";
  const std::string_view rhs_semantic =
      rhs.semanticName != nullptr ? rhs.semanticName : "";
  return lhs_semantic == rhs_semantic &&
         lhs.semanticIndex == rhs.semanticIndex;
}

bool HasInputSemantic(
    const std::vector<plume::RenderInputElement>& input_elements,
    const plume::RenderInputElement& candidate) {
  return std::any_of(input_elements.begin(), input_elements.end(),
                     [&candidate](const plume::RenderInputElement& element) {
                       return InputSemanticMatches(element, candidate);
                     });
}

void FilterInputElementsToShaderInputs(
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>& shader_inputs) {
  if (shader_inputs.empty()) {
    return;
  }

  input_elements.erase(
      std::remove_if(input_elements.begin(), input_elements.end(),
                     [&shader_inputs](const plume::RenderInputElement& element) {
                       return !HasInputSemantic(shader_inputs, element);
                     }),
      input_elements.end());

  std::sort(input_elements.begin(), input_elements.end(),
            [](const plume::RenderInputElement& lhs,
               const plume::RenderInputElement& rhs) {
              if (lhs.location != rhs.location) {
                return lhs.location < rhs.location;
              }
              if (lhs.slotIndex != rhs.slotIndex) {
                return lhs.slotIndex < rhs.slotIndex;
              }
              return lhs.alignedByteOffset < rhs.alignedByteOffset;
            });
  input_elements.erase(
      std::unique(input_elements.begin(), input_elements.end(),
                  [](const plume::RenderInputElement& lhs,
                     const plume::RenderInputElement& rhs) {
                    return lhs.location == rhs.location;
                  }),
      input_elements.end());
}

void RemapInputLocationsFromShader(
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>& shader_inputs) {
  for (plume::RenderInputElement& element : input_elements) {
    for (const plume::RenderInputElement& shader_input : shader_inputs) {
      if (InputSemanticMatches(element, shader_input)) {
        element.location = shader_input.location;
        break;
      }
    }
  }
}

bool IsSemantic(const plume::RenderInputElement& element,
                std::string_view semantic, uint32_t semantic_index) {
  const std::string_view element_semantic =
      element.semanticName != nullptr ? element.semanticName : "";
  return element_semantic == semantic && element.semanticIndex == semantic_index;
}

void NormalizeInputElementFormatsToBoundStreams(
    const RendererStreamBindingSnapshot* streams, size_t stream_count,
    std::vector<plume::RenderInputElement>& input_elements) {
  if (!streams) {
    return;
  }

  for (plume::RenderInputElement& element : input_elements) {
    if (element.slotIndex >= stream_count) {
      continue;
    }

    const RendererStreamBindingSnapshot& stream =
        streams[element.slotIndex];
    if (stream.stride == 0) {
      continue;
    }

    const uint32_t element_size = InputFormatByteSize(element.format);
    if (element_size != 0 &&
        element.alignedByteOffset + element_size <= stream.stride) {
      continue;
    }

    if (IsSemantic(element, "POSITION", 0) &&
        element.format == plume::RenderFormat::R32G32B32_FLOAT &&
        element.alignedByteOffset + 8u <= stream.stride) {
      element.format = plume::RenderFormat::R32G32_FLOAT;
    }
  }
}

void NormalizeInputElementFormatsToBoundStreams(
    const RendererDrawEventSnapshot& draw_event,
    std::vector<plume::RenderInputElement>& input_elements) {
  NormalizeInputElementFormatsToBoundStreams(draw_event.streams,
                                             std::size(draw_event.streams),
                                             input_elements);
}

void PadMissingInputLocations(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>* shader_inputs) {
  if (shader_inputs != nullptr && !shader_inputs->empty()) {
    bool needs_default_slot = false;
    for (const plume::RenderInputElement& shader_input : *shader_inputs) {
      if (HasInputSemantic(input_elements, shader_input) ||
          HasInputLocation(input_elements, shader_input.location)) {
        continue;
      }

      plume::RenderInputElement element = shader_input;
      element.format = HeuristicShaderInputFormat(shader_input);
      element.slotIndex = kDefaultVertexInputSlot;
      element.alignedByteOffset = 0;
      input_elements.push_back(element);
      needs_default_slot = true;
    }

    if (needs_default_slot &&
        !HasInputSlot(input_slots, kDefaultVertexInputSlot)) {
      input_slots.emplace_back(kDefaultVertexInputSlot,
                               kDefaultVertexInputStride);
    }
    return;
  }

  struct DefaultInputElement {
    uint8_t usage;
    uint8_t usage_index;
  };

  static constexpr std::array<DefaultInputElement, 11> kDefaultInputs = {{
      {0, 0},
      {3, 0},
      {6, 0},
      {7, 0},
      {5, 0},
      {5, 1},
      {5, 2},
      {5, 3},
      {10, 0},
      {1, 0},
      {2, 0},
  }};

  bool needs_default_slot = false;
  for (const DefaultInputElement& default_input : kDefaultInputs) {
    const uint32_t location =
        ConvertGuestDeclLocation(default_input.usage,
                                 default_input.usage_index);
    if (location == UINT32_MAX || HasInputLocation(input_elements, location)) {
      continue;
    }

    input_elements.emplace_back(
        ConvertGuestDeclUsageName(default_input.usage),
        default_input.usage_index, location,
        DefaultInputFormatForUsage(default_input.usage),
        kDefaultVertexInputSlot, 0);
    needs_default_slot = true;
  }

  if (needs_default_slot &&
      !HasInputSlot(input_slots, kDefaultVertexInputSlot)) {
    input_slots.emplace_back(kDefaultVertexInputSlot,
                             kDefaultVertexInputStride);
  }
}

void TrimInputSlotsToElements(
    std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements) {
  input_slots.erase(
      std::remove_if(input_slots.begin(), input_slots.end(),
                     [&input_elements](const plume::RenderInputSlot& slot) {
                       return std::none_of(
                           input_elements.begin(), input_elements.end(),
                           [&slot](const plume::RenderInputElement& element) {
                             return element.slotIndex == slot.index;
                           });
                     }),
      input_slots.end());
}

plume::RenderBlend ConvertFableBlend(uint32_t blend,
                                     plume::RenderBlend fallback) {
  // Fable writes raw Xenos RB_BLENDCONTROL blend factors, not D3D9 one-based
  // D3DBLEND values.
  switch (blend) {
    case 0:
      return plume::RenderBlend::ZERO;
    case 1:
      return plume::RenderBlend::ONE;
    case 4:
      return plume::RenderBlend::SRC_COLOR;
    case 5:
      return plume::RenderBlend::INV_SRC_COLOR;
    case 6:
      return plume::RenderBlend::SRC_ALPHA;
    case 7:
      return plume::RenderBlend::INV_SRC_ALPHA;
    case 8:
      return plume::RenderBlend::DEST_COLOR;
    case 9:
      return plume::RenderBlend::INV_DEST_COLOR;
    case 10:
      return plume::RenderBlend::DEST_ALPHA;
    case 11:
      return plume::RenderBlend::INV_DEST_ALPHA;
    case 12:
    case 14:
      return plume::RenderBlend::BLEND_FACTOR;
    case 13:
    case 15:
      return plume::RenderBlend::INV_BLEND_FACTOR;
    case 16:
      return plume::RenderBlend::SRC_ALPHA_SAT;
    default:
      return fallback;
  }
}

plume::RenderBlend ConvertFableAlphaBlend(uint32_t blend,
                                          plume::RenderBlend fallback) {
  // Xenos uses the same raw blend-factor enum for color and alpha equations,
  // but color factors collapse to their alpha equivalents in the alpha path.
  switch (blend) {
    case 4:
    case 6:
      return plume::RenderBlend::SRC_ALPHA;
    case 5:
    case 7:
      return plume::RenderBlend::INV_SRC_ALPHA;
    case 8:
    case 10:
      return plume::RenderBlend::DEST_ALPHA;
    case 9:
    case 11:
      return plume::RenderBlend::INV_DEST_ALPHA;
    default:
      return ConvertFableBlend(blend, fallback);
  }
}

plume::RenderBlendOperation ConvertFableBlendOp(
    uint32_t blend_op, plume::RenderBlendOperation fallback) {
  // Raw Xenos RB_BLENDCONTROL blend operation values.
  switch (blend_op) {
    case 0:
      return plume::RenderBlendOperation::ADD;
    case 1:
      return plume::RenderBlendOperation::SUBTRACT;
    case 2:
      return plume::RenderBlendOperation::MIN;
    case 3:
      return plume::RenderBlendOperation::MAX;
    case 4:
      return plume::RenderBlendOperation::REV_SUBTRACT;
    default:
      return fallback;
  }
}

plume::RenderComparisonFunction ConvertFableCompare(
    uint32_t compare, plume::RenderComparisonFunction fallback) {
  // Fable writes raw Xenos RB_DEPTHCONTROL enum values, not D3D9 one-based
  // D3DCMPFUNC values.
  switch (compare) {
    case 0:
      return plume::RenderComparisonFunction::NEVER;
    case 1:
      return plume::RenderComparisonFunction::LESS;
    case 2:
      return plume::RenderComparisonFunction::EQUAL;
    case 3:
      return plume::RenderComparisonFunction::LESS_EQUAL;
    case 4:
      return plume::RenderComparisonFunction::GREATER;
    case 5:
      return plume::RenderComparisonFunction::NOT_EQUAL;
    case 6:
      return plume::RenderComparisonFunction::GREATER_EQUAL;
    case 7:
      return plume::RenderComparisonFunction::ALWAYS;
    default:
      return fallback;
  }
}

plume::RenderCullMode ConvertFableCullMode(uint32_t cull_mode) {
  // Raw Xenos PA_SU_SC_MODE_CNTL low bits: bit 0 culls front faces, bit 1
  // culls back faces, bit 2 chooses front-face winding.
  const bool cull_front = (cull_mode & 0x1u) != 0;
  const bool cull_back = (cull_mode & 0x2u) != 0;
  if (cull_front && !cull_back) {
    return plume::RenderCullMode::FRONT;
  }
  if (cull_back && !cull_front) {
    return plume::RenderCullMode::BACK;
  }
  if (cull_front && cull_back) {
    return plume::RenderCullMode::BACK;
  }
  return plume::RenderCullMode::NONE;
}

plume::RenderFrontFace ConvertFableFrontFace(uint32_t cull_mode) {
  return (cull_mode & 0x4u) != 0 ? plume::RenderFrontFace::CLOCKWISE
                                 : plume::RenderFrontFace::COUNTER_CLOCKWISE;
}

plume::RenderStencilOp ConvertFableStencilOp(uint32_t op) {
  // Raw Xenos RB_DEPTHCONTROL stencil op values.
  switch (op) {
    case 0:
      return plume::RenderStencilOp::KEEP;
    case 1:
      return plume::RenderStencilOp::ZERO;
    case 2:
      return plume::RenderStencilOp::REPLACE;
    case 3:
      return plume::RenderStencilOp::INCREMENT_AND_CLAMP;
    case 4:
      return plume::RenderStencilOp::DECREMENT_AND_CLAMP;
    case 5:
      return plume::RenderStencilOp::INVERT;
    case 6:
      return plume::RenderStencilOp::INCREMENT_AND_WRAP;
    case 7:
      return plume::RenderStencilOp::DECREMENT_AND_WRAP;
    default:
      return plume::RenderStencilOp::KEEP;
  }
}

plume::RenderStencilFaceDesc FableStencilFaceFromDepthControl(
    uint32_t depth_control, bool back_face) {
  plume::RenderStencilFaceDesc face;
  face.failOp =
      ConvertFableStencilOp(FableDepthControlStencilFail(depth_control,
                                                        back_face));
  face.depthFailOp =
      ConvertFableStencilOp(FableDepthControlStencilZFail(depth_control,
                                                         back_face));
  face.passOp =
      ConvertFableStencilOp(FableDepthControlStencilZPass(depth_control,
                                                         back_face));
  face.compareFunction =
      ConvertFableCompare(FableDepthControlStencilFunc(depth_control,
                                                       back_face),
                          plume::RenderComparisonFunction::ALWAYS);
  return face;
}
#endif

}  // namespace refii::renderer
