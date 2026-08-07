#include "runtime/refii_renderer/plume/general/plume_frame_database.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>
#include <rex/runtime.h>
#include <tracy/Tracy.hpp>

namespace refii::renderer {

std::string Hex32(uint32_t value) {
  char text[16] = {};
  std::snprintf(text, sizeof(text), "0x%08X", value);
  return text;
}

std::string Hex64(uint64_t value) {
  char text[24] = {};
  std::snprintf(text, sizeof(text), "0x%016llX",
                static_cast<unsigned long long>(value));
  return text;
}

std::string JsonEscape(std::string_view value) {
  std::string escaped;
  escaped.reserve(value.size() + 8u);
  for (char ch : value) {
    switch (ch) {
      case '\\':
        escaped += "\\\\";
        break;
      case '"':
        escaped += "\\\"";
        break;
      case '\n':
        escaped += "\\n";
        break;
      case '\r':
        escaped += "\\r";
        break;
      case '\t':
        escaped += "\\t";
        break;
      default:
        escaped.push_back(ch);
        break;
    }
  }
  return escaped;
}

void AppendRenderDocDisplayProvenanceComment(std::string& comments) {
  const FableDirectFrameDatabaseCommandPackets command_packets =
      GetFableDirectFrameDatabaseCommandPackets();
  uint32_t present_blit_packets = 0;
  const FableDirectFrameDatabasePresentBlitPacket* latest_present_blit =
      nullptr;
  for (const FableDirectFrameDatabaseCommandPacket& packet :
       command_packets.packets) {
    if (packet.kind != "present_blit") {
      continue;
    }
    ++present_blit_packets;
    latest_present_blit = &packet.present_blit;
  }

  if (latest_present_blit == nullptr) {
    comments += "Display provenance: PresentBlit=<none>\n";
    return;
  }

  comments += "Display provenance: PresentBlit source=" +
              Hex32(latest_present_blit->source_target) + " output=" +
              Hex32(latest_present_blit->output_resource) + " src=" +
              std::to_string(latest_present_blit->source_width) + "x" +
              std::to_string(latest_present_blit->source_height) + " tex=" +
              std::to_string(latest_present_blit->texture_width) + "x" +
              std::to_string(latest_present_blit->texture_height) + " out=" +
              std::to_string(latest_present_blit->output_width) + "x" +
              std::to_string(latest_present_blit->output_height) +
              " write_serial=" +
              std::to_string(latest_present_blit->source_write_serial) +
              " packets=" + std::to_string(present_blit_packets);
  if (!latest_present_blit->reason.empty()) {
    comments += " reason=";
    comments += latest_present_blit->reason;
  }
  comments += "\n";
}

const char* FableGuestResourceKindName(FableGuestResourceKind kind) {
  switch (kind) {
    case FableGuestResourceKind::kUnknown:
      return "unknown";
    case FableGuestResourceKind::kTexture:
      return "texture";
    case FableGuestResourceKind::kSurface:
      return "surface";
    case FableGuestResourceKind::kDepthSurface:
      return "depth_surface";
    case FableGuestResourceKind::kVertexBuffer:
      return "vertex_buffer";
    case FableGuestResourceKind::kIndexBuffer:
      return "index_buffer";
    case FableGuestResourceKind::kVertexDeclaration:
      return "vertex_declaration";
    case FableGuestResourceKind::kVertexShader:
      return "vertex_shader";
    case FableGuestResourceKind::kPixelShader:
      return "pixel_shader";
  }
  return "unknown";
}

template <size_t Count>
std::string_view FixedCStringView(const std::array<char, Count>& text) {
  size_t size = 0;
  while (size < text.size() && text[size] != '\0') {
    ++size;
  }
  return {text.data(), size};
}

std::string FrameDatabaseSyntheticName(std::string_view role,
                                       uint32_t value) {
  if (value == 0) {
    return "none";
  }
  std::string name(role);
  name += "_";
  name += Hex32(value);
  return name;
}

std::string FrameDatabaseSyntheticName64(std::string_view role,
                                         uint64_t value) {
  if (value == 0) {
    return "none";
  }
  std::string name(role);
  name += "_";
  name += Hex64(value);
  return name;
}


FrameDatabaseResourceNameIndex BuildFrameDatabaseResourceNameIndex(
    const FableDirectResourceDebugSnapshot& snapshot) {
  FrameDatabaseResourceNameIndex index;
  const uint32_t count =
      std::min<uint32_t>(snapshot.count,
                         static_cast<uint32_t>(snapshot.entries.size()));
  for (uint32_t entry_index = 0; entry_index < count; ++entry_index) {
    const FableDirectResourceDebugEntry& entry = snapshot.entries[entry_index];
    if (!entry.valid) {
      continue;
    }
    const std::string_view kind = FableGuestResourceKindName(entry.kind);
    const std::string_view annotation = FixedCStringView(entry.annotation);
    const std::string synthetic_name =
        FrameDatabaseSyntheticName(kind, entry.guest_address);
    const std::string_view name = annotation.empty()
                                      ? std::string_view(synthetic_name)
                                      : annotation;
    index.Add(entry.guest_address, kind, name, entry.content_hash);
    index.Add(entry.parent_address, kind, name, entry.content_hash);
    index.Add(entry.base_or_gpu_address, kind, name, entry.content_hash);
  }
  return index;
}

std::filesystem::path ResolveRenderDocFrameDatabaseDir() {
  std::filesystem::path capture_dir = RenderDocCaptureDirectory();

  std::error_code ec;
  std::filesystem::create_directories(capture_dir, ec);
  if (ec) {
    REXLOG_WARN("Refii frame database directory could not be created: {}",
                capture_dir.string());
    return {};
  }
  return capture_dir;
}

template <size_t Count>
void WriteJsonU32Array(std::ofstream& file, const char* name,
                       const std::array<uint32_t, Count>& values,
                       const char* indent = "    ") {
  file << indent << "\"" << name << "\": [";
  for (size_t index = 0; index < values.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << values[index];
  }
  file << "]";
}

template <size_t Count>
void WriteJsonU32HexArray(std::ofstream& file, const char* name,
                          const std::array<uint32_t, Count>& values,
                          const char* indent = "    ") {
  file << indent << "\"" << name << "\": [";
  for (size_t index = 0; index < values.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << "\"" << Hex32(values[index]) << "\"";
  }
  file << "]";
}

template <size_t Count>
void WriteJsonU32NameArray(std::ofstream& file, const char* name,
                           const std::array<uint32_t, Count>& values,
                           const FrameDatabaseResourceNameIndex& name_index,
                           std::string_view default_role,
                           const char* indent = "    ") {
  file << indent << "\"" << name << "\": [";
  for (size_t index = 0; index < values.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << "\""
         << JsonEscape(name_index.NameFor(values[index], default_role))
         << "\"";
  }
  file << "]";
}

const char* FrameDatabaseShaderStageName(RendererShaderStage stage) {
  switch (stage) {
    case RendererShaderStage::kUnknown:
      return "unknown";
    case RendererShaderStage::kVertex:
      return "vertex";
    case RendererShaderStage::kPixel:
      return "pixel";
    case RendererShaderStage::kCombined:
      return "combined";
  }
  return "unknown";
}

bool DecodeSyntheticGeneratedShaderId(uint32_t shader_id,
                                      uint32_t& generated_id) {
  if ((shader_id & 0xFF000000u) != 0x0E000000u) {
    return false;
  }
  const uint32_t encoded = shader_id & 0xFFFFu;
  uint32_t decoded = 0;
  for (int shift = 12; shift >= 0; shift -= 4) {
    const uint32_t digit = (encoded >> shift) & 0xFu;
    if (digit > 9u) {
      return false;
    }
    decoded = decoded * 10u + digit;
  }
  if (decoded == 0) {
    return false;
  }
  generated_id = decoded;
  return true;
}

void WriteFrameDatabaseShaderArtifact(
    std::ofstream& file, const char* field_name, uint32_t shader_id,
    const FrameDatabaseResourceNameIndex& name_index,
    const char* indent = "      ") {
  uint64_t guest_hash = name_index.ContentHashFor(shader_id);
  RendererShaderLibraryEntry entry;
  bool found =
      guest_hash != 0 && FindRendererShaderLibraryEntryByGuestHash(guest_hash,
                                                                   entry);
  uint32_t synthetic_generated_shader_id = 0;
  if (!found && DecodeSyntheticGeneratedShaderId(
                    shader_id, synthetic_generated_shader_id)) {
    found = FindRendererShaderLibraryEntryById(synthetic_generated_shader_id,
                                               entry);
    if (found) {
      guest_hash = entry.guest_bytecode_hash != 0 ? entry.guest_bytecode_hash
                                                  : entry.guest_payload_hash;
    }
  }

  file << indent << "\"" << field_name << "\": {";
  file << "\"guest_hash\": " << guest_hash
       << ", \"guest_hash_hex\": \"" << Hex64(guest_hash) << "\""
       << ", \"found\": " << (found ? "true" : "false")
       << ", \"synthetic_generated_shader_id\": "
       << synthetic_generated_shader_id;
  if (found) {
    file << ", \"name\": \"" << JsonEscape(entry.name) << "\""
         << ", \"stage\": \"" << FrameDatabaseShaderStageName(entry.stage)
         << "\""
         << ", \"active_binary_path\": \""
         << JsonEscape(entry.binary_path) << "\""
         << ", \"hlsl_path\": \"" << JsonEscape(entry.hlsl_path) << "\""
         << ", \"guest_blob_path\": \""
         << JsonEscape(entry.guest_blob_path) << "\""
         << ", \"vulkan_binary_path\": \""
         << JsonEscape(entry.vulkan_binary_path) << "\""
         << ", \"vulkan_hlsl_path\": \""
         << JsonEscape(entry.vulkan_hlsl_path) << "\""
         << ", \"d3d12_binary_path\": \""
         << JsonEscape(entry.d3d12_binary_path) << "\""
         << ", \"d3d12_hlsl_path\": \""
         << JsonEscape(entry.d3d12_hlsl_path) << "\""
         << ", \"metal_binary_path\": \""
         << JsonEscape(entry.metal_binary_path) << "\""
         << ", \"metal_source_path\": \""
         << JsonEscape(entry.metal_source_path) << "\""
         << ", \"texture_slot_mask_valid\": "
         << (entry.texture_slot_mask_valid ? "true" : "false")
         << ", \"texture_slot_mask\": " << entry.texture_slot_mask
         << ", \"texture_slot_mask_hex\": \""
         << Hex32(entry.texture_slot_mask) << "\"";
  }
  file << "}";
}

std::string FrameDatabaseShaderDisplayNameFor(
    uint32_t shader_id, const FrameDatabaseResourceNameIndex& name_index,
    std::string_view default_role) {
  if (shader_id == 0) {
    return "none";
  }

  uint64_t guest_hash = name_index.ContentHashFor(shader_id);
  RendererShaderLibraryEntry entry;
  if (guest_hash != 0 &&
      FindRendererShaderLibraryEntryByGuestHash(guest_hash, entry)) {
    return std::string(entry.name);
  }

  uint32_t synthetic_generated_shader_id = 0;
  if (DecodeSyntheticGeneratedShaderId(shader_id,
                                       synthetic_generated_shader_id) &&
      FindRendererShaderLibraryEntryById(synthetic_generated_shader_id,
                                         entry)) {
    return std::string(entry.name);
  }

  return name_index.NameFor(shader_id, default_role);
}

bool FrameDatabaseShaderNameContains(std::string_view shader_name,
                                     std::string_view needle) {
  return shader_name.find(needle) != std::string_view::npos;
}

std::string FrameDatabaseDrawSemanticClassFor(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FrameDatabaseResourceNameIndex& name_index) {
  const std::string vertex_shader_name = SanitizeRenderDocTagComponent(
      FrameDatabaseShaderDisplayNameFor(packet.vertex_shader, name_index,
                                        "vertex_shader"));
  const std::string pixel_shader_name = SanitizeRenderDocTagComponent(
      FrameDatabaseShaderDisplayNameFor(packet.pixel_shader, name_index,
                                        "pixel_shader"));
  const auto shader_name_contains = [&](std::string_view needle) {
    return FrameDatabaseShaderNameContains(vertex_shader_name, needle) ||
           FrameDatabaseShaderNameContains(pixel_shader_name, needle);
  };

  if (shader_name_contains("mem_export_skin")) {
    return "character_skin_mem_export";
  }
  if (shader_name_contains("skinned_mesh") ||
      shader_name_contains("preskinned_mesh")) {
    return "character_skinned_mesh";
  }
  if (shader_name_contains("one_bone_mesh")) {
    return "character_one_bone_mesh";
  }
  if (shader_name_contains("shell_fur") &&
      shader_name_contains("preskinned")) {
    return "creature_preskinned_fur";
  }
  return "unknown";
}

std::string MergeFrameDatabaseSemanticClass(std::string_view existing,
                                            std::string_view incoming) {
  if (incoming.empty() || incoming == "unknown") {
    return existing.empty() ? std::string("unknown") : std::string(existing);
  }
  if (existing.empty() || existing == "unknown" || existing == incoming) {
    return std::string(incoming);
  }
  if (FrameDatabaseShaderNameContains(existing, "character") ||
      FrameDatabaseShaderNameContains(existing, "creature") ||
      FrameDatabaseShaderNameContains(incoming, "character") ||
      FrameDatabaseShaderNameContains(incoming, "creature")) {
    return "character_mixed";
  }
  return "mixed";
}

void WriteFrameDatabasePipelineState(
    std::ofstream& file,
    const FableDirectFrameDatabaseDrawPacket& packet,
    const char* indent = "      ") {
  file << indent << "\"pipeline_state\": {";
  file << "\"pm4_valid\": " << (packet.pipeline_pm4_valid ? "true" : "false")
       << ", \"depth_control\": " << packet.depth_control
       << ", \"depth_control_hex\": \"" << Hex32(packet.depth_control)
       << "\""
       << ", \"stencil_ref_mask\": " << packet.stencil_ref_mask
       << ", \"stencil_ref_mask_hex\": \""
       << Hex32(packet.stencil_ref_mask) << "\""
       << ", \"stencil_ref_mask_bf\": " << packet.stencil_ref_mask_bf
       << ", \"stencil_ref_mask_bf_hex\": \""
       << Hex32(packet.stencil_ref_mask_bf) << "\""
       << ", \"cull_control\": " << packet.cull_control
       << ", \"cull_control_hex\": \"" << Hex32(packet.cull_control)
       << "\""
       << ", \"pa_su_sc_mode_cntl\": " << packet.pa_su_sc_mode_cntl
       << ", \"pa_su_sc_mode_cntl_hex\": \""
       << Hex32(packet.pa_su_sc_mode_cntl) << "\"";

  file << ", \"blend_controls\": [";
  for (size_t index = 0; index < packet.blend_controls.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << packet.blend_controls[index];
  }
  file << "], \"blend_controls_hex\": [";
  for (size_t index = 0; index < packet.blend_controls.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << "\"" << Hex32(packet.blend_controls[index]) << "\"";
  }
  file << "]";

  file << ", \"color_write_masks\": [";
  for (size_t index = 0; index < packet.color_write_masks.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << packet.color_write_masks[index];
  }
  file << "], \"color_write_masks_hex\": [";
  for (size_t index = 0; index < packet.color_write_masks.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << "\"" << Hex32(packet.color_write_masks[index]) << "\"";
  }
  file << "]";

  file << ", \"color_control\": " << packet.color_control
       << ", \"color_control_hex\": \"" << Hex32(packet.color_control)
       << "\""
       << ", \"alpha_ref\": " << packet.alpha_ref
       << ", \"alpha_ref_hex\": \"" << Hex32(packet.alpha_ref) << "\""
       << ", \"alpha_test_enable\": " << packet.alpha_test_enable
       << ", \"alpha_test_enable_hex\": \""
       << Hex32(packet.alpha_test_enable) << "\""
       << ", \"alpha_func\": " << packet.alpha_func
       << ", \"alpha_func_hex\": \"" << Hex32(packet.alpha_func) << "\"";
  file << "}";
}

template <size_t Count>
uint32_t CountNonZeroDwords(const std::array<uint32_t, Count>& values) {
  return static_cast<uint32_t>(
      std::count_if(values.begin(), values.end(),
                    [](uint32_t value) { return value != 0; }));
}

void WriteFrameDatabaseConstantSummary(
    std::ofstream& file,
    const RefiiConstantStore& constants,
    const char* indent = "      ") {
  file << indent << "\"constants\": {";
  file << "\"fetch_dwords\": " << constants.fetch.size();
  file << ", \"fetch_nonzero_dwords\": "
       << CountNonZeroDwords(constants.fetch);
  file << ", \"vertex_float_dwords\": " << constants.vertex_float.size();
  file << ", \"vertex_float_nonzero_dwords\": "
       << CountNonZeroDwords(constants.vertex_float);
  file << ", \"pixel_float_dwords\": " << constants.pixel_float.size();
  file << ", \"pixel_float_nonzero_dwords\": "
       << CountNonZeroDwords(constants.pixel_float);
  file << ", \"vertex_int_dwords\": " << constants.vertex_int.size();
  file << ", \"vertex_int_nonzero_dwords\": "
       << CountNonZeroDwords(constants.vertex_int);
  file << ", \"pixel_int_dwords\": " << constants.pixel_int.size();
  file << ", \"pixel_int_nonzero_dwords\": "
       << CountNonZeroDwords(constants.pixel_int);
  file << ", \"vertex_int_valid_mask\": "
       << constants.vertex_int_valid_mask;
  file << ", \"pixel_int_valid_mask\": " << constants.pixel_int_valid_mask;
  file << ", \"vertex_bool_dwords\": " << constants.vertex_bool.size();
  file << ", \"vertex_bool_nonzero_dwords\": "
       << CountNonZeroDwords(constants.vertex_bool);
  file << ", \"pixel_bool_dwords\": " << constants.pixel_bool.size();
  file << ", \"pixel_bool_nonzero_dwords\": "
       << CountNonZeroDwords(constants.pixel_bool);
  file << "}";
}

void WriteFrameDatabaseInlineBufferMetadata(
    std::ofstream& file,
    const RefiiInlineBufferPayload& payload) {
  file << "{\"valid\": " << (payload.valid() ? "true" : "false")
       << ", \"address\": " << payload.address
       << ", \"address_hex\": \"" << Hex32(payload.address) << "\""
       << ", \"byte_size\": " << payload.byte_size
       << ", \"stride_or_format\": " << payload.stride_or_format
       << ", \"data_bytes\": " << payload.data.size() << "}";
}

void WriteFrameDatabaseInlineTextureMetadata(
    std::ofstream& file,
    const RefiiInlineTexturePayload& payload) {
  file << "{\"valid\": " << (payload.valid() ? "true" : "false")
       << ", \"address\": " << payload.address
       << ", \"address_hex\": \"" << Hex32(payload.address) << "\""
       << ", \"parent_address\": " << payload.parent_address
       << ", \"parent_address_hex\": \"" << Hex32(payload.parent_address)
       << "\""
       << ", \"base_address\": " << payload.base_address
       << ", \"base_address_hex\": \"" << Hex32(payload.base_address) << "\""
       << ", \"byte_size\": " << payload.byte_size
       << ", \"upload_generation\": " << payload.upload_generation
       << ", \"upload_hash\": " << payload.upload_hash
       << ", \"serial\": " << payload.serial
       << ", \"data_bytes\": " << payload.data.size() << "}";
}

template <size_t Count>
void WriteFrameDatabaseInlineBufferArray(
    std::ofstream& file,
    const std::array<RefiiInlineBufferPayload, Count>& payloads,
    const char* name,
    const char* indent = "      ") {
  file << indent << "\"" << name << "\": [";
  for (size_t index = 0; index < payloads.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    WriteFrameDatabaseInlineBufferMetadata(file, payloads[index]);
  }
  file << "]";
}

template <size_t Count>
void WriteFrameDatabaseInlineTextureArray(
    std::ofstream& file,
    const std::array<RefiiInlineTexturePayload, Count>& payloads,
    const char* name,
    const char* indent = "      ") {
  file << indent << "\"" << name << "\": [";
  for (size_t index = 0; index < payloads.size(); ++index) {
    if (index != 0) {
      file << ", ";
    }
    WriteFrameDatabaseInlineTextureMetadata(file, payloads[index]);
  }
  file << "]";
}

std::string BuildFrameDatabaseDrawLabel(
    uint32_t frame_index,
    const FableDirectFrameDatabaseDrawPacket& packet) {
  char label[256] = {};
  std::snprintf(
      label, sizeof(label),
      "RefiiRenderer::SubmitDrawPacket Frame %u Draw %llu Cmd %llu "
      "Pass_%s Source %u VS_%08X PS_%08X RT_%08X DS_%08X Prim_%u "
      "Count_%u Indexed_%u",
      frame_index, static_cast<unsigned long long>(packet.packet_id),
      static_cast<unsigned long long>(packet.command_serial),
      packet.pass_name.c_str(), packet.source, packet.vertex_shader,
      packet.pixel_shader, packet.render_targets[0],
      packet.depth_stencil, packet.primitive_type,
      packet.vertex_or_index_count, packet.indexed ? 1u : 0u);
  return label;
}





std::string ClassifyFrameDatabasePassSummary(
    const FrameDatabasePassSummary& summary) {
  if (summary.pass_name == "Clear") {
    return "clear";
  }
  if (summary.pass_name == "Resolve") {
    return "resolve";
  }
  if (summary.pass_name == "SurfaceDestination") {
    return "surface_destination";
  }
  if (summary.pass_name == "PresentBlit") {
    return "present_blit";
  }
  if (summary.pass_name == "MainScene") {
    return "main_scene";
  }
  if (summary.pass_name == "Video") {
    return "video";
  }
  if (summary.pass_name == "PrivateRect") {
    return "private_rect";
  }
  if (summary.pass_name == "PrivateRectCopy") {
    return "private_rect_copy";
  }
  if (summary.pass_name == "PrivateRectDepth") {
    return "private_rect_depth";
  }
  if (summary.pass_name == "DepthOnly") {
    return "depth_only";
  }
  if (summary.pass_name == "PostProcessOrUI") {
    return "post_process_or_ui";
  }
  if (summary.pass_name == "WrapperDraw") {
    return "wrapper_draw";
  }
  if (summary.clear_packets != 0 || summary.clear_resource_edges != 0) {
    return "clear";
  }
  if (summary.resolve_packets != 0 || summary.resolve_resource_edges != 0) {
    return "resolve";
  }
  if (summary.surface_destination_edges != 0) {
    return "surface_destination";
  }
  if (summary.present_blit_edges != 0) {
    return "present_blit";
  }
  if (summary.draw_packets != 0) {
    return "draw";
  }
  if (summary.command_packets != 0 || summary.resource_edges != 0) {
    return "command";
  }
  return "unknown";
}













bool FrameDatabaseRootPresent(const FableDirectFrameDatabaseRoot& root) {
  return root.call_serial != 0 || root.context_key != 0 ||
         root.symbol_address != 0 || root.lr != 0;
}

uint64_t MixFrameDatabaseIdentityHash(uint64_t hash, uint64_t value) {
  hash ^= value;
  hash *= 0x100000001B3ull;
  return hash;
}

template <size_t Count>
uint64_t MixFrameDatabaseIdentityArray(
    uint64_t hash,
    const std::array<uint32_t, Count>& values) {
  for (uint32_t value : values) {
    hash = MixFrameDatabaseIdentityHash(hash, value);
  }
  return hash;
}

uint64_t HashFrameDatabaseMaterialKey(
    const FableDirectFrameDatabaseDrawPacket& packet) {
  uint64_t hash = 0xCBF29CE484222325ull;
  hash = MixFrameDatabaseIdentityHash(hash, packet.pixel_shader);
  hash = MixFrameDatabaseIdentityArray(hash, packet.textures);
  hash = MixFrameDatabaseIdentityArray(hash, packet.sampler_states);
  hash = MixFrameDatabaseIdentityArray(hash, packet.render_targets);
  hash = MixFrameDatabaseIdentityHash(hash, packet.depth_stencil);
  return hash;
}

uint64_t HashFrameDatabaseMeshKey(
    const FableDirectFrameDatabaseDrawPacket& packet) {
  uint64_t hash = 0xCBF29CE484222325ull;
  hash = MixFrameDatabaseIdentityHash(hash, packet.vertex_declaration);
  hash = MixFrameDatabaseIdentityArray(hash, packet.vertex_buffers);
  hash = MixFrameDatabaseIdentityArray(hash, packet.vertex_strides);
  hash = MixFrameDatabaseIdentityArray(hash, packet.vertex_offsets);
  hash = MixFrameDatabaseIdentityHash(hash, packet.index_buffer);
  hash = MixFrameDatabaseIdentityHash(hash, packet.index_format);
  hash = MixFrameDatabaseIdentityHash(hash, packet.indexed ? 1u : 0u);
  hash = MixFrameDatabaseIdentityHash(hash, packet.primitive_type);
  hash = MixFrameDatabaseIdentityHash(hash, packet.start);
  hash = MixFrameDatabaseIdentityHash(hash, packet.vertex_or_index_count);
  hash = MixFrameDatabaseIdentityHash(
      hash, static_cast<uint32_t>(packet.base_vertex));
  return hash;
}

FrameDatabasePassSummary& FindOrAddFrameDatabasePassSummary(
    std::vector<FrameDatabasePassSummary>& summaries,
    std::string_view pass_name) {
  const std::string normalized_pass =
      pass_name.empty() ? std::string("Unknown") : std::string(pass_name);
  auto it = std::find_if(
      summaries.begin(), summaries.end(),
      [&](const FrameDatabasePassSummary& summary) {
        return summary.pass_name == normalized_pass;
      });
  if (it == summaries.end()) {
    FrameDatabasePassSummary summary;
    summary.pass_name = normalized_pass;
    summaries.push_back(std::move(summary));
    it = std::prev(summaries.end());
  }
  return *it;
}

bool IsFrameDatabaseExecutableCommandKind(std::string_view kind) {
  return kind == "draw" || kind == "clear" || kind == "resolve" ||
         kind == "surface_destination" || kind == "present_blit";
}

uint32_t CountFrameDatabaseExecutableCommandPackets(
    const FableDirectFrameDatabaseCommandPackets& command_packets) {
  return static_cast<uint32_t>(
      std::count_if(command_packets.packets.begin(),
                    command_packets.packets.end(),
                    [](const FableDirectFrameDatabaseCommandPacket& packet) {
                      return IsFrameDatabaseExecutableCommandKind(packet.kind);
                    }));
}

std::vector<FrameDatabasePassSummary> BuildFrameDatabasePassSummaries(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges) {
  std::vector<FrameDatabasePassSummary> summaries;
  for (const FableDirectFrameDatabaseCommandPacket& packet :
       command_packets.packets) {
    FrameDatabasePassSummary& summary =
        FindOrAddFrameDatabasePassSummary(summaries, packet.pass_name);
    ++summary.command_packets;
    if (summary.first_command_packet_id == 0) {
      summary.first_command_packet_id = packet.packet_id;
    }
    if (packet.kind == "clear") {
      ++summary.clear_packets;
    } else if (packet.kind == "resolve") {
      ++summary.resolve_packets;
    }
  }

  for (const FrameDatabaseResourceEdge& edge : resource_edges) {
    FrameDatabasePassSummary& summary =
        FindOrAddFrameDatabasePassSummary(summaries, edge.pass_name);
    ++summary.resource_edges;
    if (edge.kind == "clear") {
      ++summary.clear_resource_edges;
    } else if (edge.kind == "resolve") {
      ++summary.resolve_resource_edges;
    } else if (edge.kind == "surface_destination") {
      ++summary.surface_destination_edges;
    } else if (edge.kind == "present_blit") {
      ++summary.present_blit_edges;
    }
  }

  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    FrameDatabasePassSummary& summary =
        FindOrAddFrameDatabasePassSummary(summaries, packet.pass_name);
    if (summary.first_packet_id == 0) {
      summary.first_packet_id = packet.packet_id;
    }
    ++summary.draw_packets;
    if (packet.indexed) {
      ++summary.indexed_draw_packets;
    }
    const bool has_draw_root = FrameDatabaseRootPresent(packet.draw_root);
    const bool has_vertex_shader_root =
        FrameDatabaseRootPresent(packet.vertex_shader_root);
    const bool has_pixel_shader_root =
        FrameDatabaseRootPresent(packet.pixel_shader_root);
    const bool has_private_wrapper_root =
        FrameDatabaseRootPresent(packet.private_texture_wrapper_root);
    summary.draw_rooted_packets += has_draw_root ? 1u : 0u;
    summary.vertex_shader_rooted_packets += has_vertex_shader_root ? 1u : 0u;
    summary.pixel_shader_rooted_packets += has_pixel_shader_root ? 1u : 0u;
    summary.fully_shader_rooted_packets +=
        has_vertex_shader_root && has_pixel_shader_root ? 1u : 0u;
    summary.private_texture_wrapper_rooted_packets +=
        has_private_wrapper_root ? 1u : 0u;
    summary.any_rooted_packets += has_draw_root || has_vertex_shader_root ||
                                           has_pixel_shader_root ||
                                           has_private_wrapper_root
                                       ? 1u
                                       : 0u;
  }
  for (FrameDatabasePassSummary& summary : summaries) {
    summary.pass_class = ClassifyFrameDatabasePassSummary(summary);
  }
  return summaries;
}

void MarkFrameDatabasePassResourceRole(
    FrameDatabasePassResourceUsage& usage,
    FrameDatabasePassResourceRole role) {
  switch (role) {
    case FrameDatabasePassResourceRole::kVertexShader:
      usage.vertex_shader = true;
      break;
    case FrameDatabasePassResourceRole::kPixelShader:
      usage.pixel_shader = true;
      break;
    case FrameDatabasePassResourceRole::kVertexDeclaration:
      usage.vertex_declaration = true;
      break;
    case FrameDatabasePassResourceRole::kRenderTarget:
      usage.render_target = true;
      break;
    case FrameDatabasePassResourceRole::kDepthStencil:
      usage.depth_stencil = true;
      break;
    case FrameDatabasePassResourceRole::kTexture:
      usage.texture = true;
      break;
    case FrameDatabasePassResourceRole::kVertexBuffer:
      usage.vertex_buffer = true;
      break;
    case FrameDatabasePassResourceRole::kIndexBuffer:
      usage.index_buffer = true;
      break;
    case FrameDatabasePassResourceRole::kClearTarget:
      usage.clear_target = true;
      break;
    case FrameDatabasePassResourceRole::kResolveSource:
      usage.resolve_source = true;
      break;
    case FrameDatabasePassResourceRole::kResolveDestination:
      usage.resolve_destination = true;
      break;
    case FrameDatabasePassResourceRole::kSurfaceSource:
      usage.surface_source = true;
      break;
    case FrameDatabasePassResourceRole::kSurfaceDestination:
      usage.surface_destination = true;
      break;
    case FrameDatabasePassResourceRole::kPresentSource:
      usage.present_source = true;
      break;
    case FrameDatabasePassResourceRole::kPresentOutput:
      usage.present_output = true;
      break;
  }
}

void AddFrameDatabasePassResourceUsage(
    std::vector<FrameDatabasePassResourceUsage>& usages,
    std::string_view pass_name, uint32_t resource_id,
    FrameDatabasePassResourceRole role, bool read, bool write) {
  if (resource_id == 0) {
    return;
  }
  const std::string normalized_pass =
      pass_name.empty() ? std::string("Unknown") : std::string(pass_name);
  auto it = std::find_if(
      usages.begin(), usages.end(),
      [&](const FrameDatabasePassResourceUsage& usage) {
        return usage.pass_name == normalized_pass &&
               usage.resource_id == resource_id;
      });
  if (it == usages.end()) {
    FrameDatabasePassResourceUsage usage;
    usage.pass_name = normalized_pass;
    usage.resource_id = resource_id;
    usages.push_back(std::move(usage));
    it = std::prev(usages.end());
  }

  it->read_count += read ? 1u : 0u;
  it->write_count += write ? 1u : 0u;
  MarkFrameDatabasePassResourceRole(*it, role);
}

std::vector<FrameDatabasePassResourceUsage> BuildFrameDatabasePassResourceUsages(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges) {
  std::vector<FrameDatabasePassResourceUsage> usages;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    AddFrameDatabasePassResourceUsage(
        usages, packet.pass_name, packet.vertex_shader,
        FrameDatabasePassResourceRole::kVertexShader, true, false);
    AddFrameDatabasePassResourceUsage(
        usages, packet.pass_name, packet.pixel_shader,
        FrameDatabasePassResourceRole::kPixelShader, true, false);
    AddFrameDatabasePassResourceUsage(
        usages, packet.pass_name, packet.vertex_declaration,
        FrameDatabasePassResourceRole::kVertexDeclaration, true, false);
    for (uint32_t render_target : packet.render_targets) {
      AddFrameDatabasePassResourceUsage(
          usages, packet.pass_name, render_target,
          FrameDatabasePassResourceRole::kRenderTarget, false, true);
    }
    AddFrameDatabasePassResourceUsage(
        usages, packet.pass_name, packet.depth_stencil,
        FrameDatabasePassResourceRole::kDepthStencil, false, true);
    for (uint32_t texture : packet.textures) {
      AddFrameDatabasePassResourceUsage(
          usages, packet.pass_name, texture,
          FrameDatabasePassResourceRole::kTexture, true, false);
    }
    for (uint32_t vertex_buffer : packet.vertex_buffers) {
      AddFrameDatabasePassResourceUsage(
          usages, packet.pass_name, vertex_buffer,
          FrameDatabasePassResourceRole::kVertexBuffer, true, false);
    }
    AddFrameDatabasePassResourceUsage(
        usages, packet.pass_name, packet.index_buffer,
        FrameDatabasePassResourceRole::kIndexBuffer, true, false);
  }

  for (const FrameDatabaseResourceEdge& edge : resource_edges) {
    if (edge.kind == "clear") {
      for (uint32_t render_target : edge.source_render_targets) {
        AddFrameDatabasePassResourceUsage(
            usages, edge.pass_name, render_target,
            FrameDatabasePassResourceRole::kClearTarget, false, true);
      }
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.source_depth_stencil,
          FrameDatabasePassResourceRole::kClearTarget, false, true);
    } else if (edge.kind == "resolve") {
      for (uint32_t render_target : edge.source_render_targets) {
        AddFrameDatabasePassResourceUsage(
            usages, edge.pass_name, render_target,
            FrameDatabasePassResourceRole::kResolveSource, true, false);
      }
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.source_depth_stencil,
          FrameDatabasePassResourceRole::kResolveSource, true, false);
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.destination_base,
          FrameDatabasePassResourceRole::kResolveDestination, false, true);
    } else if (edge.kind == "surface_destination") {
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.source_surface,
          FrameDatabasePassResourceRole::kSurfaceSource, true, false);
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.destination_texture,
          FrameDatabasePassResourceRole::kSurfaceDestination, false, true);
    } else if (edge.kind == "present_blit") {
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.source_surface,
          FrameDatabasePassResourceRole::kPresentSource, true, false);
      AddFrameDatabasePassResourceUsage(
          usages, edge.pass_name, edge.destination_texture,
          FrameDatabasePassResourceRole::kPresentOutput, false, true);
    }
  }
  return usages;
}

void MergeFrameDatabaseResourceLifetimeRoles(
    FrameDatabaseFrameResourceLifetime& lifetime,
    const FrameDatabasePassResourceUsage& usage) {
  lifetime.vertex_shader |= usage.vertex_shader;
  lifetime.pixel_shader |= usage.pixel_shader;
  lifetime.vertex_declaration |= usage.vertex_declaration;
  lifetime.render_target |= usage.render_target;
  lifetime.depth_stencil |= usage.depth_stencil;
  lifetime.texture |= usage.texture;
  lifetime.vertex_buffer |= usage.vertex_buffer;
  lifetime.index_buffer |= usage.index_buffer;
  lifetime.clear_target |= usage.clear_target;
  lifetime.resolve_source |= usage.resolve_source;
  lifetime.resolve_destination |= usage.resolve_destination;
  lifetime.surface_source |= usage.surface_source;
  lifetime.surface_destination |= usage.surface_destination;
  lifetime.present_source |= usage.present_source;
  lifetime.present_output |= usage.present_output;
}

std::vector<FrameDatabaseFrameResourceLifetime>
BuildFrameDatabaseFrameResourceLifetimes(
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages) {
  std::vector<FrameDatabaseFrameResourceLifetime> lifetimes;
  for (size_t pass_index = 0; pass_index < pass_summaries.size();
       ++pass_index) {
    const FrameDatabasePassSummary& pass_summary = pass_summaries[pass_index];
    for (const FrameDatabasePassResourceUsage& usage : pass_resource_usages) {
      if (usage.pass_name != pass_summary.pass_name) {
        continue;
      }
      auto it = std::find_if(
          lifetimes.begin(), lifetimes.end(),
          [&](const FrameDatabaseFrameResourceLifetime& lifetime) {
            return lifetime.resource_id == usage.resource_id;
          });
      if (it == lifetimes.end()) {
        FrameDatabaseFrameResourceLifetime lifetime;
        lifetime.resource_id = usage.resource_id;
        lifetime.first_pass_index = static_cast<uint32_t>(pass_index);
        lifetime.last_pass_index = static_cast<uint32_t>(pass_index);
        lifetime.first_pass = usage.pass_name;
        lifetime.last_pass = usage.pass_name;
        lifetimes.push_back(std::move(lifetime));
        it = std::prev(lifetimes.end());
      } else {
        it->last_pass_index = static_cast<uint32_t>(pass_index);
        it->last_pass = usage.pass_name;
      }

      it->read_count += usage.read_count;
      it->write_count += usage.write_count;
      MergeFrameDatabaseResourceLifetimeRoles(*it, usage);
    }
  }
  return lifetimes;
}

bool FrameDatabaseMaterialKeysEqual(
    const FrameDatabaseMaterialRecord& record,
    const FableDirectFrameDatabaseDrawPacket& packet) {
  return record.pixel_shader == packet.pixel_shader &&
         record.textures == packet.textures &&
         record.sampler_states == packet.sampler_states &&
         record.render_targets == packet.render_targets &&
         record.depth_stencil == packet.depth_stencil;
}

std::vector<FrameDatabaseMaterialRecord> BuildFrameDatabaseMaterialRecords(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index) {
  std::vector<FrameDatabaseMaterialRecord> records;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    const uint64_t material_id = HashFrameDatabaseMaterialKey(packet);
    const std::string semantic_class =
        FrameDatabaseDrawSemanticClassFor(packet, name_index);
    auto it = std::find_if(
        records.begin(), records.end(),
        [&](const FrameDatabaseMaterialRecord& record) {
          return record.material_id == material_id &&
                 FrameDatabaseMaterialKeysEqual(record, packet);
        });
    if (it == records.end()) {
      FrameDatabaseMaterialRecord record;
      record.material_id = material_id;
      record.first_packet_id = packet.packet_id;
      record.draw_count = 1;
      record.first_pass =
          packet.pass_name.empty() ? std::string("Unknown") : packet.pass_name;
      record.last_pass = record.first_pass;
      record.semantic_class = semantic_class;
      record.pixel_shader = packet.pixel_shader;
      record.textures = packet.textures;
      record.sampler_states = packet.sampler_states;
      record.render_targets = packet.render_targets;
      record.depth_stencil = packet.depth_stencil;
      records.push_back(std::move(record));
    } else {
      ++it->draw_count;
      it->last_pass =
          packet.pass_name.empty() ? std::string("Unknown") : packet.pass_name;
      it->semantic_class =
          MergeFrameDatabaseSemanticClass(it->semantic_class, semantic_class);
    }
  }
  return records;
}

bool FrameDatabaseMeshKeysEqual(
    const FrameDatabaseMeshRecord& record,
    const FableDirectFrameDatabaseDrawPacket& packet) {
  return record.vertex_declaration == packet.vertex_declaration &&
         record.vertex_buffers == packet.vertex_buffers &&
         record.vertex_strides == packet.vertex_strides &&
         record.vertex_offsets == packet.vertex_offsets &&
         record.index_buffer == packet.index_buffer &&
         record.index_format == packet.index_format &&
         record.indexed == packet.indexed &&
         record.primitive_type == packet.primitive_type &&
         record.start == packet.start &&
         record.vertex_or_index_count == packet.vertex_or_index_count &&
         record.base_vertex == packet.base_vertex;
}

std::vector<FrameDatabaseMeshRecord> BuildFrameDatabaseMeshRecords(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index) {
  std::vector<FrameDatabaseMeshRecord> records;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    const uint64_t mesh_id = HashFrameDatabaseMeshKey(packet);
    const std::string semantic_class =
        FrameDatabaseDrawSemanticClassFor(packet, name_index);
    auto it = std::find_if(records.begin(), records.end(),
                           [&](const FrameDatabaseMeshRecord& record) {
                             return record.mesh_id == mesh_id &&
                                    FrameDatabaseMeshKeysEqual(record, packet);
                           });
    if (it == records.end()) {
      FrameDatabaseMeshRecord record;
      record.mesh_id = mesh_id;
      record.first_packet_id = packet.packet_id;
      record.draw_count = 1;
      record.first_pass =
          packet.pass_name.empty() ? std::string("Unknown") : packet.pass_name;
      record.last_pass = record.first_pass;
      record.semantic_class = semantic_class;
      record.vertex_declaration = packet.vertex_declaration;
      record.vertex_buffers = packet.vertex_buffers;
      record.vertex_strides = packet.vertex_strides;
      record.vertex_offsets = packet.vertex_offsets;
      record.index_buffer = packet.index_buffer;
      record.index_format = packet.index_format;
      record.indexed = packet.indexed;
      record.primitive_type = packet.primitive_type;
      record.start = packet.start;
      record.vertex_or_index_count = packet.vertex_or_index_count;
      record.base_vertex = packet.base_vertex;
      records.push_back(std::move(record));
    } else {
      ++it->draw_count;
      it->last_pass =
          packet.pass_name.empty() ? std::string("Unknown") : packet.pass_name;
      it->semantic_class =
          MergeFrameDatabaseSemanticClass(it->semantic_class, semantic_class);
    }
  }
  return records;
}

void AddFrameDatabaseAssetUsageRecord(
    std::vector<FrameDatabaseAssetUsageRecord>& records,
    std::string_view kind, uint32_t resource_id, uint32_t slot,
    const FableDirectFrameDatabaseDrawPacket& packet,
    std::string_view semantic_class, uint64_t material_id, uint64_t mesh_id) {
  if (resource_id == 0 || kind.empty()) {
    return;
  }
  FrameDatabaseAssetUsageRecord record;
  record.kind = std::string(kind);
  record.resource_id = resource_id;
  record.slot = slot;
  record.packet_id = packet.packet_id;
  record.pass_name =
      packet.pass_name.empty() ? std::string("Unknown") : packet.pass_name;
  record.semantic_class = semantic_class.empty() ? std::string("unknown")
                                                : std::string(semantic_class);
  record.material_id = material_id;
  record.mesh_id = mesh_id;
  records.push_back(std::move(record));
}

std::vector<FrameDatabaseAssetUsageRecord> BuildFrameDatabaseAssetUsageRecords(
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index) {
  std::vector<FrameDatabaseAssetUsageRecord> records;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    const uint64_t material_id = HashFrameDatabaseMaterialKey(packet);
    const uint64_t mesh_id = HashFrameDatabaseMeshKey(packet);
    const std::string semantic_class =
        FrameDatabaseDrawSemanticClassFor(packet, name_index);
    constexpr uint32_t kNoSlot = 0xFFFFFFFFu;
    AddFrameDatabaseAssetUsageRecord(records, "vertex_shader",
                                     packet.vertex_shader, kNoSlot, packet,
                                     semantic_class, material_id, mesh_id);
    AddFrameDatabaseAssetUsageRecord(records, "pixel_shader",
                                     packet.pixel_shader, kNoSlot, packet,
                                     semantic_class, material_id, mesh_id);
    AddFrameDatabaseAssetUsageRecord(records, "vertex_declaration",
                                     packet.vertex_declaration, kNoSlot, packet,
                                     semantic_class, material_id, mesh_id);
    for (size_t slot = 0; slot < packet.textures.size(); ++slot) {
      AddFrameDatabaseAssetUsageRecord(
          records, "texture", packet.textures[slot], static_cast<uint32_t>(slot),
          packet, semantic_class, material_id, mesh_id);
    }
    for (size_t slot = 0; slot < packet.vertex_buffers.size(); ++slot) {
      AddFrameDatabaseAssetUsageRecord(
          records, "vertex_buffer", packet.vertex_buffers[slot],
          static_cast<uint32_t>(slot), packet, semantic_class, material_id,
          mesh_id);
    }
    AddFrameDatabaseAssetUsageRecord(records, "index_buffer",
                                     packet.index_buffer, kNoSlot, packet,
                                     semantic_class, material_id, mesh_id);
  }
  return records;
}

template <typename T>
void AddUniqueFrameDatabaseValue(std::vector<T>& values, T value) {
  if (std::find(values.begin(), values.end(), value) == values.end()) {
    values.push_back(value);
  }
}

std::string_view FrameDatabaseRenderGraphPhaseForClass(
    std::string_view pass_class) {
  if (pass_class == "clear") {
    return "setup";
  }
  if (pass_class == "main_scene" || pass_class == "depth_only" ||
      pass_class == "draw") {
    return "scene";
  }
  if (pass_class == "video") {
    return "video";
  }
  if (pass_class == "post_process_or_ui" || pass_class == "wrapper_draw") {
    return "post_process_or_ui";
  }
  if (pass_class == "resolve" || pass_class == "surface_destination" ||
      pass_class == "private_rect" || pass_class == "private_rect_copy" ||
      pass_class == "private_rect_depth") {
    return "copy_resolve";
  }
  if (pass_class == "present_blit") {
    return "present";
  }
  if (pass_class == "command") {
    return "command";
  }
  return "unknown";
}

std::vector<FrameDatabaseRenderGraphNode> BuildFrameDatabaseRenderGraphNodes(
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages,
    const std::vector<FrameDatabaseAssetUsageRecord>& asset_usage_records) {
  std::vector<FrameDatabaseRenderGraphNode> nodes;
  for (size_t pass_index = 0; pass_index < pass_summaries.size();
       ++pass_index) {
    const FrameDatabasePassSummary& pass_summary = pass_summaries[pass_index];
    FrameDatabaseRenderGraphNode node;
    node.pass_index = static_cast<uint32_t>(pass_index);
    node.pass_name = pass_summary.pass_name;
    node.pass_class = pass_summary.pass_class;
    node.phase_name =
        std::string(FrameDatabaseRenderGraphPhaseForClass(node.pass_class));
    node.draw_packets = pass_summary.draw_packets;
    node.command_packets = pass_summary.command_packets;

    for (const FrameDatabasePassResourceUsage& usage : pass_resource_usages) {
      if (usage.pass_name != pass_summary.pass_name) {
        continue;
      }
      ++node.resource_usages;
      node.read_resources += usage.read_count != 0 ? 1u : 0u;
      node.write_resources += usage.write_count != 0 ? 1u : 0u;
    }

    std::vector<uint64_t> material_ids;
    std::vector<uint64_t> mesh_ids;
    for (const FrameDatabaseAssetUsageRecord& usage : asset_usage_records) {
      if (usage.pass_name != pass_summary.pass_name) {
        continue;
      }
      ++node.asset_usages;
      AddUniqueFrameDatabaseValue(material_ids, usage.material_id);
      AddUniqueFrameDatabaseValue(mesh_ids, usage.mesh_id);
    }
    node.material_usages = static_cast<uint32_t>(material_ids.size());
    node.mesh_usages = static_cast<uint32_t>(mesh_ids.size());
    nodes.push_back(std::move(node));
  }
  return nodes;
}

std::vector<FrameDatabaseRenderGraphEdge> BuildFrameDatabaseRenderGraphEdges(
    const std::vector<FrameDatabasePassDependency>& pass_dependencies) {
  std::vector<FrameDatabaseRenderGraphEdge> edges;
  for (size_t edge_index = 0; edge_index < pass_dependencies.size();
       ++edge_index) {
    const FrameDatabasePassDependency& dependency =
        pass_dependencies[edge_index];
    FrameDatabaseRenderGraphEdge edge;
    edge.edge_index = static_cast<uint32_t>(edge_index);
    edge.producer_pass = dependency.producer_pass;
    edge.consumer_pass = dependency.consumer_pass;
    edge.resource_id = dependency.resource_id;
    edge.hazard = dependency.hazard;
    edges.push_back(std::move(edge));
  }
  return edges;
}

std::vector<FrameDatabaseRenderGraphExecutionStep>
BuildFrameDatabaseRenderGraphExecutionPlan(
    const std::vector<FrameDatabaseRenderGraphNode>& nodes,
    const std::vector<FrameDatabaseRenderGraphEdge>& edges,
    const std::vector<FrameDatabasePassResourceTransition>& transitions) {
  std::vector<FrameDatabaseRenderGraphExecutionStep> steps;
  for (size_t step_index = 0; step_index < nodes.size(); ++step_index) {
    const FrameDatabaseRenderGraphNode& node = nodes[step_index];
    FrameDatabaseRenderGraphExecutionStep step;
    step.step_index = static_cast<uint32_t>(step_index);
    step.pass_index = node.pass_index;
    step.pass_name = node.pass_name;
    step.pass_class = node.pass_class;
    step.execute_draws = node.draw_packets != 0;
    step.execute_commands = node.command_packets != 0;
    step.reads_resources = node.read_resources != 0;
    step.writes_resources = node.write_resources != 0;

    for (const FrameDatabaseRenderGraphEdge& edge : edges) {
      step.wait_edges += edge.consumer_pass == node.pass_name ? 1u : 0u;
      step.signal_edges += edge.producer_pass == node.pass_name ? 1u : 0u;
    }
    for (const FrameDatabasePassResourceTransition& transition :
         transitions) {
      step.barriers_before += transition.after_pass == node.pass_name ? 1u : 0u;
      step.barriers_after += transition.before_pass == node.pass_name ? 1u : 0u;
    }
    steps.push_back(std::move(step));
  }
  return steps;
}

std::vector<FrameDatabaseRenderGraphPhase> BuildFrameDatabaseRenderGraphPhases(
    const std::vector<FrameDatabaseRenderGraphNode>& nodes,
    const std::vector<FrameDatabaseRenderGraphExecutionStep>& execution_plan) {
  std::vector<FrameDatabaseRenderGraphPhase> phases;
  for (const FrameDatabaseRenderGraphNode& node : nodes) {
    auto it = std::find_if(
        phases.begin(), phases.end(),
        [&](const FrameDatabaseRenderGraphPhase& phase) {
          return phase.phase_name == node.phase_name;
        });
    if (it == phases.end()) {
      FrameDatabaseRenderGraphPhase phase;
      phase.phase_index = static_cast<uint32_t>(phases.size());
      phase.phase_name = node.phase_name;
      phase.first_pass_index = node.pass_index;
      phase.last_pass_index = node.pass_index;
      phases.push_back(std::move(phase));
      it = std::prev(phases.end());
    } else {
      it->last_pass_index = node.pass_index;
    }

    ++it->pass_count;
    it->draw_packets += node.draw_packets;
    it->command_packets += node.command_packets;
    it->resource_usages += node.resource_usages;
    it->read_resources += node.read_resources;
    it->write_resources += node.write_resources;
    it->material_usages += node.material_usages;
    it->mesh_usages += node.mesh_usages;
    it->asset_usages += node.asset_usages;
  }

  for (const FrameDatabaseRenderGraphExecutionStep& step : execution_plan) {
    const std::string_view step_phase =
        FrameDatabaseRenderGraphPhaseForClass(step.pass_class);
    auto it = std::find_if(
        phases.begin(), phases.end(),
        [&](const FrameDatabaseRenderGraphPhase& phase) {
          return std::string_view(phase.phase_name) == step_phase;
        });
    if (it == phases.end()) {
      continue;
    }
    it->wait_edges += step.wait_edges;
    it->signal_edges += step.signal_edges;
    it->barriers_before += step.barriers_before;
    it->barriers_after += step.barriers_after;
  }
  return phases;
}

void AddFrameDatabasePassDependency(
    std::vector<FrameDatabasePassDependency>& dependencies,
    std::string_view producer_pass, std::string_view consumer_pass,
    uint32_t resource_id, std::string_view hazard,
    uint32_t producer_write_count, uint32_t consumer_read_count,
    uint32_t consumer_write_count) {
  if (resource_id == 0 || producer_pass.empty() || consumer_pass.empty() ||
      producer_pass == consumer_pass || hazard.empty()) {
    return;
  }
  const auto duplicate = std::find_if(
      dependencies.begin(), dependencies.end(),
      [&](const FrameDatabasePassDependency& dependency) {
        return dependency.producer_pass == producer_pass &&
               dependency.consumer_pass == consumer_pass &&
               dependency.resource_id == resource_id &&
               dependency.hazard == hazard;
      });
  if (duplicate != dependencies.end()) {
    return;
  }

  FrameDatabasePassDependency dependency;
  dependency.producer_pass = std::string(producer_pass);
  dependency.consumer_pass = std::string(consumer_pass);
  dependency.resource_id = resource_id;
  dependency.hazard = std::string(hazard);
  dependency.producer_write_count = producer_write_count;
  dependency.consumer_read_count = consumer_read_count;
  dependency.consumer_write_count = consumer_write_count;
  dependencies.push_back(std::move(dependency));
}

std::vector<FrameDatabasePassDependency> BuildFrameDatabasePassDependencies(
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages) {
  struct LastWriter {
    uint32_t resource_id = 0;
    std::string pass_name;
    uint32_t write_count = 0;
  };

  std::vector<LastWriter> last_writers;
  std::vector<FrameDatabasePassDependency> dependencies;
  for (const FrameDatabasePassSummary& pass_summary : pass_summaries) {
    for (const FrameDatabasePassResourceUsage& usage : pass_resource_usages) {
      if (usage.pass_name != pass_summary.pass_name) {
        continue;
      }

      auto writer = std::find_if(
          last_writers.begin(), last_writers.end(),
          [&](const LastWriter& item) {
            return item.resource_id == usage.resource_id;
          });
      if (writer != last_writers.end() &&
          writer->pass_name != usage.pass_name) {
        if (usage.read_count != 0) {
          AddFrameDatabasePassDependency(
              dependencies, writer->pass_name, usage.pass_name,
              usage.resource_id, "read_after_write", writer->write_count,
              usage.read_count, usage.write_count);
        } else if (usage.write_count != 0) {
          AddFrameDatabasePassDependency(
              dependencies, writer->pass_name, usage.pass_name,
              usage.resource_id, "write_after_write", writer->write_count,
              usage.read_count, usage.write_count);
        }
      }

      if (usage.write_count == 0) {
        continue;
      }
      if (writer == last_writers.end()) {
        LastWriter item;
        item.resource_id = usage.resource_id;
        item.pass_name = usage.pass_name;
        item.write_count = usage.write_count;
        last_writers.push_back(std::move(item));
      } else {
        writer->pass_name = usage.pass_name;
        writer->write_count = usage.write_count;
      }
    }
  }
  return dependencies;
}

std::vector<FrameDatabasePassResourceTransition>
BuildFrameDatabasePassResourceTransitions(
    const std::vector<FrameDatabasePassDependency>& dependencies) {
  std::vector<FrameDatabasePassResourceTransition> transitions;
  transitions.reserve(dependencies.size());
  for (const FrameDatabasePassDependency& dependency : dependencies) {
    FrameDatabasePassResourceTransition transition;
    transition.before_pass = dependency.producer_pass;
    transition.after_pass = dependency.consumer_pass;
    transition.resource_id = dependency.resource_id;
    transition.before_access = "write";
    transition.after_access =
        dependency.hazard == "read_after_write" ? "read" : "write";
    transition.hazard = dependency.hazard;
    transitions.push_back(std::move(transition));
  }
  return transitions;
}

uint32_t FrameDatabasePassDrawCount(
    const std::vector<FrameDatabasePassSummary>& summaries,
    std::string_view pass_name) {
  for (const FrameDatabasePassSummary& summary : summaries) {
    if (summary.pass_name == pass_name) {
      return summary.draw_packets;
    }
  }
  return 0;
}

uint32_t FrameDatabasePrivateRectPassDrawCount(
    const std::vector<FrameDatabasePassSummary>& summaries) {
  return FrameDatabasePassDrawCount(summaries, "PrivateRect") +
         FrameDatabasePassDrawCount(summaries, "PrivateRectCopy") +
         FrameDatabasePassDrawCount(summaries, "PrivateRectDepth");
}

uint32_t CountFrameDatabaseClassifiedPassSummaries(
    const std::vector<FrameDatabasePassSummary>& summaries) {
  return static_cast<uint32_t>(
      std::count_if(summaries.begin(), summaries.end(),
                    [](const FrameDatabasePassSummary& summary) {
                      return !summary.pass_class.empty() &&
                             summary.pass_class != "unknown";
                    }));
}

void PlotFrameDatabasePassSummaries(
    const std::vector<FrameDatabasePassSummary>& summaries) {
  TracyPlot("Refii.PassClassified",
            static_cast<int64_t>(
                CountFrameDatabaseClassifiedPassSummaries(summaries)));
  TracyPlot("Refii.Pass.MainScene",
            static_cast<int64_t>(
                FrameDatabasePassDrawCount(summaries, "MainScene")));
  TracyPlot("Refii.Pass.Video",
            static_cast<int64_t>(
                FrameDatabasePassDrawCount(summaries, "Video")));
  TracyPlot("Refii.Pass.PrivateRect",
            static_cast<int64_t>(
                FrameDatabasePrivateRectPassDrawCount(summaries)));
  TracyPlot("Refii.Pass.DepthOnly",
            static_cast<int64_t>(
                FrameDatabasePassDrawCount(summaries, "DepthOnly")));
  TracyPlot("Refii.Pass.PostProcessOrUI",
            static_cast<int64_t>(
                FrameDatabasePassDrawCount(summaries, "PostProcessOrUI")));
  TracyPlot("Refii.Pass.WrapperDraw",
            static_cast<int64_t>(
                FrameDatabasePassDrawCount(summaries, "WrapperDraw")));
}

void MarkFrameDatabaseResourceRole(FrameDatabaseResourceEvent& event,
                                   FrameDatabaseResourceRole role) {
  switch (role) {
    case FrameDatabaseResourceRole::kVertexShader:
      event.vertex_shader = true;
      break;
    case FrameDatabaseResourceRole::kPixelShader:
      event.pixel_shader = true;
      break;
    case FrameDatabaseResourceRole::kVertexDeclaration:
      event.vertex_declaration = true;
      break;
    case FrameDatabaseResourceRole::kRenderTarget:
      event.render_target = true;
      break;
    case FrameDatabaseResourceRole::kDepthStencil:
      event.depth_stencil = true;
      break;
    case FrameDatabaseResourceRole::kTexture:
      event.texture = true;
      break;
    case FrameDatabaseResourceRole::kVertexBuffer:
      event.vertex_buffer = true;
      break;
    case FrameDatabaseResourceRole::kIndexBuffer:
      event.index_buffer = true;
      break;
  }
}

void AddFrameDatabaseResourceEvent(
    std::vector<FrameDatabaseResourceEvent>& events,
    uint32_t resource_id,
    FrameDatabaseResourceRole role,
    uint64_t packet_id) {
  if (resource_id == 0) {
    return;
  }
  for (FrameDatabaseResourceEvent& event : events) {
    if (event.resource_id != resource_id) {
      continue;
    }
    ++event.reference_count;
    MarkFrameDatabaseResourceRole(event, role);
    return;
  }

  FrameDatabaseResourceEvent event;
  event.resource_id = resource_id;
  event.first_packet_id = packet_id;
  event.reference_count = 1;
  MarkFrameDatabaseResourceRole(event, role);
  events.push_back(event);
}

std::vector<FrameDatabaseResourceEvent> BuildFrameDatabaseResourceEvents(
    const FableDirectFrameDatabaseDrawPackets& draw_packets) {
  std::vector<FrameDatabaseResourceEvent> events;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    AddFrameDatabaseResourceEvent(events, packet.vertex_shader,
                                  FrameDatabaseResourceRole::kVertexShader,
                                  packet.packet_id);
    AddFrameDatabaseResourceEvent(events, packet.pixel_shader,
                                  FrameDatabaseResourceRole::kPixelShader,
                                  packet.packet_id);
    AddFrameDatabaseResourceEvent(
        events, packet.vertex_declaration,
        FrameDatabaseResourceRole::kVertexDeclaration, packet.packet_id);
    for (uint32_t render_target : packet.render_targets) {
      AddFrameDatabaseResourceEvent(events, render_target,
                                    FrameDatabaseResourceRole::kRenderTarget,
                                    packet.packet_id);
    }
    AddFrameDatabaseResourceEvent(events, packet.depth_stencil,
                                  FrameDatabaseResourceRole::kDepthStencil,
                                  packet.packet_id);
    for (uint32_t texture : packet.textures) {
      AddFrameDatabaseResourceEvent(events, texture,
                                    FrameDatabaseResourceRole::kTexture,
                                    packet.packet_id);
    }
    for (uint32_t vertex_buffer : packet.vertex_buffers) {
      AddFrameDatabaseResourceEvent(events, vertex_buffer,
                                    FrameDatabaseResourceRole::kVertexBuffer,
                                    packet.packet_id);
    }
    AddFrameDatabaseResourceEvent(events, packet.index_buffer,
                                  FrameDatabaseResourceRole::kIndexBuffer,
                                  packet.packet_id);
  }
  return events;
}

std::vector<FrameDatabaseResourceEdge> BuildFrameDatabaseResourceEdges(
    const FableDirectFrameDatabaseCommandPackets& command_packets) {
  std::vector<FrameDatabaseResourceEdge> edges;
  for (const FableDirectFrameDatabaseCommandPacket& packet :
       command_packets.packets) {
    if (packet.kind != "clear" && packet.kind != "resolve" &&
        packet.kind != "surface_destination" && packet.kind != "present_blit") {
      continue;
    }
    FrameDatabaseResourceEdge edge;
    edge.kind = packet.kind;
    edge.pass_name = packet.pass_name.empty() ? "Unknown" : packet.pass_name;
    edge.packet_id = packet.packet_id;
    edge.command_serial = packet.command_serial;
    edge.root = packet.root;
    edge.source_render_targets = packet.render_targets;
    edge.source_depth_stencil = packet.depth_stencil;
    if (packet.kind == "resolve") {
      edge.source_render_targets = {};
      edge.source_depth_stencil = 0;
      edge.source_slot = packet.resolve_source_slot;
      if (packet.resolve_source_surface != 0) {
        edge.source_surface = packet.resolve_source_surface;
        if (packet.resolve_source_depth) {
          edge.source_depth_stencil = packet.resolve_source_surface;
        } else if (edge.source_slot < edge.source_render_targets.size()) {
          edge.source_render_targets[edge.source_slot] =
              packet.resolve_source_surface;
        }
      } else {
        edge.source_slot = packet.resolve_copy_control & 0x7u;
        if (edge.source_slot < packet.render_targets.size()) {
          edge.source_render_targets[edge.source_slot] =
              packet.render_targets[edge.source_slot];
          edge.source_surface = packet.render_targets[edge.source_slot];
        } else {
          edge.source_depth_stencil = packet.depth_stencil;
          edge.source_surface = packet.depth_stencil;
        }
      }
      edge.destination_base = packet.resolve_dest_base;
      edge.destination_pitch = packet.resolve_dest_pitch;
      edge.destination_info = packet.resolve_dest_info;
      edge.destination_slice = packet.resolve_surface_slice;
    } else if (packet.kind == "surface_destination") {
      edge.destination_base = packet.surface_destination.texture_base;
      edge.relation_serial = packet.surface_destination.relation_serial;
      edge.source_surface = packet.surface_destination.source_surface;
      edge.destination_texture =
          packet.surface_destination.destination_texture;
      edge.source_slot = packet.surface_destination.source_slot;
      edge.source_width = packet.surface_destination.source_width;
      edge.source_height = packet.surface_destination.source_height;
      edge.destination_width = packet.surface_destination.destination_width;
      edge.destination_height = packet.surface_destination.destination_height;
      edge.texture_base_address = packet.surface_destination.texture_base;
      edge.backing_texture = packet.surface_destination.backing_texture;
      edge.backing_base_address = packet.surface_destination.backing_base;
      edge.backing_content_serial =
          packet.surface_destination.backing_content_serial;
      edge.target_content_serial =
          packet.surface_destination.target_content_serial;
      edge.scaled_copy = packet.surface_destination.scaled_copy;
      edge.trusted_destination_edge =
          packet.surface_destination.trusted_destination_edge;
      edge.wrapper_source_exact =
          packet.surface_destination.wrapper_source_exact;
    } else if (packet.kind == "present_blit") {
      edge.source_surface = packet.present_blit.source_target;
      edge.destination_texture = packet.present_blit.output_resource;
      edge.source_width = packet.present_blit.source_width;
      edge.source_height = packet.present_blit.source_height;
      edge.destination_width = packet.present_blit.output_width;
      edge.destination_height = packet.present_blit.output_height;
      edge.texture_base_address = packet.present_blit.source_target;
      edge.target_content_serial = packet.present_blit.source_write_serial;
    }
    edges.push_back(std::move(edge));
  }
  return edges;
}


FrameDatabaseResourceEdgeCounts CountFrameDatabaseResourceEdges(
    const std::vector<FrameDatabaseResourceEdge>& edges) {
  FrameDatabaseResourceEdgeCounts counts;
  counts.total_edges = static_cast<uint32_t>(edges.size());
  for (const FrameDatabaseResourceEdge& edge : edges) {
    if (edge.kind == "clear") {
      ++counts.clear_edges;
    } else if (edge.kind == "resolve") {
      ++counts.resolve_edges;
    } else if (edge.kind == "surface_destination") {
      ++counts.surface_destination_edges;
    } else if (edge.kind == "present_blit") {
      ++counts.present_blit_edges;
    }
  }
  return counts;
}

uint32_t SaturatingU32Size(size_t value) {
  return static_cast<uint32_t>(std::min<size_t>(value, UINT32_MAX));
}

RefiiRenderGraphRuntimePlanSnapshot BuildRefiiRenderGraphRuntimePlanSnapshot(
    uint32_t frame_index,
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages,
    const std::vector<FrameDatabaseRenderGraphNode>& render_graph_nodes,
    const std::vector<FrameDatabaseRenderGraphExecutionStep>&
        render_graph_execution_steps,
    const std::vector<FrameDatabaseRenderGraphPhase>& render_graph_phases,
    const std::vector<FrameDatabasePassResourceTransition>&
        pass_resource_transitions) {
  RefiiRenderGraphRuntimePlanSnapshot snapshot;
  snapshot.frame_index = frame_index;
  snapshot.passes = SaturatingU32Size(render_graph_nodes.size());
  snapshot.draw_packets = SaturatingU32Size(draw_packets.packets.size());
  snapshot.omitted_draw_packets = draw_packets.omitted_packets;
  snapshot.command_packets =
      CountFrameDatabaseExecutableCommandPackets(command_packets);
  snapshot.omitted_command_packets = command_packets.omitted_packets;
  snapshot.resource_edges = SaturatingU32Size(resource_edges.size());
  snapshot.execution_steps =
      SaturatingU32Size(render_graph_execution_steps.size());
  snapshot.barriers = SaturatingU32Size(pass_resource_transitions.size());
  snapshot.phases = SaturatingU32Size(render_graph_phases.size());

  for (const FrameDatabasePassSummary& summary : pass_summaries) {
    if (summary.draw_packets != 0 || summary.command_packets != 0) {
      ++snapshot.packet_passes;
    } else if (summary.resource_edges != 0) {
      ++snapshot.edge_only_passes;
    }
    snapshot.surface_destination_edges += summary.surface_destination_edges;
  }
  for (const FrameDatabasePassResourceUsage& usage : pass_resource_usages) {
    snapshot.resource_references += usage.read_count != 0 ? 1u : 0u;
    snapshot.resource_references += usage.write_count != 0 ? 1u : 0u;
  }
  return snapshot;
}

void WriteFrameDatabaseResourceRoles(std::ofstream& file,
                                     const FrameDatabaseResourceEvent& event) {
  bool wrote_role = false;
  auto write_role = [&](bool enabled, const char* role) {
    if (!enabled) {
      return;
    }
    if (wrote_role) {
      file << ", ";
    }
    file << "\"" << role << "\"";
    wrote_role = true;
  };

  file << "[";
  write_role(event.vertex_shader, "vertex_shader");
  write_role(event.pixel_shader, "pixel_shader");
  write_role(event.vertex_declaration, "vertex_declaration");
  write_role(event.render_target, "render_target");
  write_role(event.depth_stencil, "depth_stencil");
  write_role(event.texture, "texture");
  write_role(event.vertex_buffer, "vertex_buffer");
  write_role(event.index_buffer, "index_buffer");
  file << "]";
}

void WriteFrameDatabaseResourceEvents(
    std::ofstream& file,
    const std::vector<FrameDatabaseResourceEvent>& events,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"resource_events\": [\n";
  for (size_t index = 0; index < events.size(); ++index) {
    const FrameDatabaseResourceEvent& event = events[index];
    file << "    {\"resource_id\": " << event.resource_id
         << ", \"resource_id_hex\": \"" << Hex32(event.resource_id)
         << "\""
         << ", \"resource_name\": \""
         << JsonEscape(name_index.NameFor(event.resource_id, "resource"))
         << "\""
         << ", \"first_packet_id\": " << event.first_packet_id
         << ", \"reference_count\": " << event.reference_count
         << ", \"roles\": ";
    WriteFrameDatabaseResourceRoles(file, event);
    file << "}" << (index + 1u == events.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseResourceNames(
    std::ofstream& file,
    const FableDirectResourceDebugSnapshot& resource_snapshot,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"resource_names\": {\n";
  file << "    \"summary\": {\"entries\": " << resource_snapshot.count
       << ", \"omitted\": " << resource_snapshot.omitted << "},\n";
  file << "    \"entries\": [\n";
  const uint32_t count =
      std::min<uint32_t>(resource_snapshot.count,
                         static_cast<uint32_t>(
                             resource_snapshot.entries.size()));
  for (uint32_t index = 0; index < count; ++index) {
    const FableDirectResourceDebugEntry& entry =
        resource_snapshot.entries[index];
    const std::string_view kind = FableGuestResourceKindName(entry.kind);
    const std::string name =
        name_index.NameFor(entry.guest_address, kind);
    file << "      {\"resource_id\": " << entry.guest_address
         << ", \"resource_id_hex\": \"" << Hex32(entry.guest_address)
         << "\", \"resource_name\": \"" << JsonEscape(name)
         << "\", \"kind\": \"" << JsonEscape(kind)
         << "\", \"ready\": " << (entry.ready ? "true" : "false")
         << ", \"renderdoc_annotated\": "
         << (entry.renderdoc_annotated ? "true" : "false")
         << ", \"parent_address\": " << entry.parent_address
         << ", \"parent_address_hex\": \"" << Hex32(entry.parent_address)
         << "\", \"base_or_gpu_address\": " << entry.base_or_gpu_address
         << ", \"base_or_gpu_address_hex\": \""
         << Hex32(entry.base_or_gpu_address)
         << "\", \"byte_size\": " << entry.byte_size
         << ", \"width\": " << entry.width
         << ", \"height\": " << entry.height
         << ", \"depth\": " << entry.depth
         << ", \"format_or_stride\": " << entry.format_or_stride
         << ", \"serial\": " << entry.serial
         << ", \"serial_hex\": \"" << Hex64(entry.serial)
         << "\", \"content_serial\": " << entry.content_serial
         << ", \"content_serial_hex\": \"" << Hex64(entry.content_serial)
         << "\", \"upload_generation\": " << entry.upload_generation
         << ", \"upload_generation_hex\": \""
         << Hex64(entry.upload_generation)
         << "\", \"content_hash\": " << entry.content_hash
         << ", \"content_hash_hex\": \"" << Hex64(entry.content_hash)
         << "\"}"
         << (index + 1u == count ? "" : ",") << "\n";
  }
  file << "    ]\n";
  file << "  }\n";
}

void WriteFrameDatabaseRoot(std::ofstream& file,
                            const FableDirectFrameDatabaseRoot& root);

void WriteFrameDatabaseResourceEdges(
    std::ofstream& file,
    const std::vector<FrameDatabaseResourceEdge>& edges) {
  file << "  \"resource_edges\": [\n";
  for (size_t index = 0; index < edges.size(); ++index) {
    const FrameDatabaseResourceEdge& edge = edges[index];
    file << "    {\n";
    file << "      \"kind\": \"" << JsonEscape(edge.kind) << "\",\n";
    file << "      \"pass\": \"" << JsonEscape(edge.pass_name) << "\",\n";
    file << "      \"packet_id\": " << edge.packet_id << ",\n";
    file << "      \"command_serial\": " << edge.command_serial << ",\n";
    file << "      \"root\": ";
    WriteFrameDatabaseRoot(file, edge.root);
    file << ",\n";
    WriteJsonU32Array(file, "source_render_targets",
                      edge.source_render_targets, "      ");
    file << ",\n";
    file << "      \"source_depth_stencil\": " << edge.source_depth_stencil
         << ",\n";
    file << "      \"destination\": {\"base\": " << edge.destination_base
         << ", \"pitch\": " << edge.destination_pitch
         << ", \"info\": " << edge.destination_info
         << ", \"slice\": " << edge.destination_slice << "},\n";
    file << "      \"surface_destination\": {\"relation_serial\": "
         << edge.relation_serial
         << ", \"source_surface\": " << edge.source_surface
         << ", \"destination_texture\": " << edge.destination_texture
         << ", \"source_slot\": " << edge.source_slot
         << ", \"source_width\": " << edge.source_width
         << ", \"source_height\": " << edge.source_height
         << ", \"destination_width\": " << edge.destination_width
         << ", \"destination_height\": " << edge.destination_height
         << ", \"texture_base\": " << edge.texture_base_address
         << ", \"backing_texture\": " << edge.backing_texture
         << ", \"backing_base\": " << edge.backing_base_address
         << ", \"backing_content_serial\": "
         << edge.backing_content_serial
         << ", \"target_content_serial\": " << edge.target_content_serial
         << ", \"scaled_copy\": " << (edge.scaled_copy ? "true" : "false")
         << ", \"trusted_destination_edge\": "
         << (edge.trusted_destination_edge ? "true" : "false")
         << ", \"wrapper_source_exact\": "
         << (edge.wrapper_source_exact ? "true" : "false") << "},\n";
    file << "      \"present_blit\": {\"source_target\": "
         << edge.source_surface
         << ", \"output_resource\": " << edge.destination_texture
         << ", \"source_width\": " << edge.source_width
         << ", \"source_height\": " << edge.source_height
         << ", \"output_width\": " << edge.destination_width
         << ", \"output_height\": " << edge.destination_height
         << ", \"source_write_serial\": " << edge.target_content_serial
         << "}\n";
    file << "    }" << (index + 1u == edges.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabasePassSummaries(
    std::ofstream& file,
    const std::vector<FrameDatabasePassSummary>& summaries) {
  file << "  \"pass_summaries\": [\n";
  for (size_t index = 0; index < summaries.size(); ++index) {
    const FrameDatabasePassSummary& summary = summaries[index];
    file << "    {\"pass\": \"" << JsonEscape(summary.pass_name)
         << "\", \"class\": \"" << JsonEscape(summary.pass_class)
         << "\", \"first_packet_id\": " << summary.first_packet_id
         << ", \"first_command_packet_id\": "
         << summary.first_command_packet_id
         << ", \"command_packets\": " << summary.command_packets
         << ", \"clear_packets\": " << summary.clear_packets
         << ", \"resolve_packets\": " << summary.resolve_packets
         << ", \"resource_edges\": " << summary.resource_edges
         << ", \"clear_resource_edges\": " << summary.clear_resource_edges
         << ", \"resolve_resource_edges\": " << summary.resolve_resource_edges
         << ", \"surface_destination_edges\": "
         << summary.surface_destination_edges
         << ", \"present_blit_edges\": " << summary.present_blit_edges
         << ", \"draw_packets\": " << summary.draw_packets
         << ", \"indexed_draw_packets\": "
         << summary.indexed_draw_packets
         << ", \"draw_rooted_packets\": " << summary.draw_rooted_packets
         << ", \"vertex_shader_rooted_packets\": "
         << summary.vertex_shader_rooted_packets
         << ", \"pixel_shader_rooted_packets\": "
         << summary.pixel_shader_rooted_packets
         << ", \"fully_shader_rooted_packets\": "
         << summary.fully_shader_rooted_packets
         << ", \"private_texture_wrapper_rooted_packets\": "
         << summary.private_texture_wrapper_rooted_packets
         << ", \"any_rooted_packets\": " << summary.any_rooted_packets << "}"
         << (index + 1u == summaries.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabasePassResourceUsageRoles(
    std::ofstream& file,
    const FrameDatabasePassResourceUsage& usage) {
  bool wrote_role = false;
  auto write_role = [&](bool enabled, const char* role) {
    if (!enabled) {
      return;
    }
    if (wrote_role) {
      file << ", ";
    }
    file << "\"" << role << "\"";
    wrote_role = true;
  };

  file << "[";
  write_role(usage.vertex_shader, "vertex_shader");
  write_role(usage.pixel_shader, "pixel_shader");
  write_role(usage.vertex_declaration, "vertex_declaration");
  write_role(usage.render_target, "render_target");
  write_role(usage.depth_stencil, "depth_stencil");
  write_role(usage.texture, "texture");
  write_role(usage.vertex_buffer, "vertex_buffer");
  write_role(usage.index_buffer, "index_buffer");
  write_role(usage.clear_target, "clear_target");
  write_role(usage.resolve_source, "resolve_source");
  write_role(usage.resolve_destination, "resolve_destination");
  write_role(usage.surface_source, "surface_source");
  write_role(usage.surface_destination, "surface_destination");
  write_role(usage.present_source, "present_source");
  write_role(usage.present_output, "present_output");
  file << "]";
}

void WriteFrameDatabaseFrameResourceLifetimeRoles(
    std::ofstream& file,
    const FrameDatabaseFrameResourceLifetime& lifetime) {
  bool wrote_role = false;
  auto write_role = [&](bool enabled, const char* role) {
    if (!enabled) {
      return;
    }
    if (wrote_role) {
      file << ", ";
    }
    file << "\"" << role << "\"";
    wrote_role = true;
  };

  file << "[";
  write_role(lifetime.vertex_shader, "vertex_shader");
  write_role(lifetime.pixel_shader, "pixel_shader");
  write_role(lifetime.vertex_declaration, "vertex_declaration");
  write_role(lifetime.render_target, "render_target");
  write_role(lifetime.depth_stencil, "depth_stencil");
  write_role(lifetime.texture, "texture");
  write_role(lifetime.vertex_buffer, "vertex_buffer");
  write_role(lifetime.index_buffer, "index_buffer");
  write_role(lifetime.clear_target, "clear_target");
  write_role(lifetime.resolve_source, "resolve_source");
  write_role(lifetime.resolve_destination, "resolve_destination");
  write_role(lifetime.surface_source, "surface_source");
  write_role(lifetime.surface_destination, "surface_destination");
  write_role(lifetime.present_source, "present_source");
  write_role(lifetime.present_output, "present_output");
  file << "]";
}

void WriteFrameDatabasePassResourceUsages(
    std::ofstream& file,
    const std::vector<FrameDatabasePassResourceUsage>& usages) {
  file << "  \"pass_resource_usages\": [\n";
  for (size_t index = 0; index < usages.size(); ++index) {
    const FrameDatabasePassResourceUsage& usage = usages[index];
    file << "    {\"pass\": \"" << JsonEscape(usage.pass_name)
         << "\", \"resource_id\": " << usage.resource_id
         << ", \"read_count\": " << usage.read_count
         << ", \"write_count\": " << usage.write_count << ", \"roles\": ";
    WriteFrameDatabasePassResourceUsageRoles(file, usage);
    file << "}" << (index + 1u == usages.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseFrameResourceLifetimes(
    std::ofstream& file,
    const std::vector<FrameDatabaseFrameResourceLifetime>& lifetimes) {
  file << "  \"frame_resource_lifetimes\": [\n";
  for (size_t index = 0; index < lifetimes.size(); ++index) {
    const FrameDatabaseFrameResourceLifetime& lifetime = lifetimes[index];
    file << "    {\"resource_id\": " << lifetime.resource_id
         << ", \"first_pass_index\": " << lifetime.first_pass_index
         << ", \"last_pass_index\": " << lifetime.last_pass_index
         << ", \"first_pass\": \"" << JsonEscape(lifetime.first_pass)
         << "\", \"last_pass\": \"" << JsonEscape(lifetime.last_pass)
         << "\", \"read_count\": " << lifetime.read_count
         << ", \"write_count\": " << lifetime.write_count << ", \"roles\": ";
    WriteFrameDatabaseFrameResourceLifetimeRoles(file, lifetime);
    file << "}" << (index + 1u == lifetimes.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabasePassDependencies(
    std::ofstream& file,
    const std::vector<FrameDatabasePassDependency>& dependencies) {
  file << "  \"pass_dependencies\": [\n";
  for (size_t index = 0; index < dependencies.size(); ++index) {
    const FrameDatabasePassDependency& dependency = dependencies[index];
    file << "    {\"producer_pass\": \""
         << JsonEscape(dependency.producer_pass)
         << "\", \"consumer_pass\": \""
         << JsonEscape(dependency.consumer_pass)
         << "\", \"resource_id\": " << dependency.resource_id
         << ", \"hazard\": \"" << JsonEscape(dependency.hazard)
         << "\", \"producer_write_count\": "
         << dependency.producer_write_count
         << ", \"consumer_read_count\": " << dependency.consumer_read_count
         << ", \"consumer_write_count\": " << dependency.consumer_write_count
         << "}" << (index + 1u == dependencies.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabasePassResourceTransitions(
    std::ofstream& file,
    const std::vector<FrameDatabasePassResourceTransition>& transitions) {
  file << "  \"pass_resource_transitions\": [\n";
  for (size_t index = 0; index < transitions.size(); ++index) {
    const FrameDatabasePassResourceTransition& transition = transitions[index];
    file << "    {\"before_pass\": \"" << JsonEscape(transition.before_pass)
         << "\", \"after_pass\": \"" << JsonEscape(transition.after_pass)
         << "\", \"resource_id\": " << transition.resource_id
         << ", \"before_access\": \""
         << JsonEscape(transition.before_access)
         << "\", \"after_access\": \"" << JsonEscape(transition.after_access)
         << "\", \"hazard\": \"" << JsonEscape(transition.hazard) << "\"}"
         << (index + 1u == transitions.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseRoot(std::ofstream& file,
                            const FableDirectFrameDatabaseRoot& root) {
  file << "{\"call_serial\": " << root.call_serial
       << ", \"call_serial_hex\": \"" << Hex64(root.call_serial) << "\""
       << ", \"context_key\": " << root.context_key
       << ", \"context_key_hex\": \"" << Hex64(root.context_key) << "\""
       << ", \"symbol_address\": " << root.symbol_address
       << ", \"symbol_address_hex\": \"" << Hex32(root.symbol_address)
       << "\""
       << ", \"lr\": " << root.lr
       << ", \"lr_hex\": \"" << Hex32(root.lr) << "\"}";
}

struct FrameDatabaseRootCoverage {
  uint32_t draw_rooted_packets = 0;
  uint32_t vertex_shader_rooted_packets = 0;
  uint32_t pixel_shader_rooted_packets = 0;
  uint32_t fully_shader_rooted_packets = 0;
  uint32_t private_texture_wrapper_rooted_packets = 0;
  uint32_t any_rooted_packets = 0;
};

FrameDatabaseRootCoverage BuildFrameDatabaseRootCoverage(
    const FableDirectFrameDatabaseDrawPackets& draw_packets) {
  FrameDatabaseRootCoverage coverage;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       draw_packets.packets) {
    const bool has_draw_root = FrameDatabaseRootPresent(packet.draw_root);
    const bool has_vertex_shader_root =
        FrameDatabaseRootPresent(packet.vertex_shader_root);
    const bool has_pixel_shader_root =
        FrameDatabaseRootPresent(packet.pixel_shader_root);
    const bool has_private_wrapper_root =
        FrameDatabaseRootPresent(packet.private_texture_wrapper_root);

    coverage.draw_rooted_packets += has_draw_root ? 1u : 0u;
    coverage.vertex_shader_rooted_packets += has_vertex_shader_root ? 1u : 0u;
    coverage.pixel_shader_rooted_packets += has_pixel_shader_root ? 1u : 0u;
    coverage.fully_shader_rooted_packets +=
        has_vertex_shader_root && has_pixel_shader_root ? 1u : 0u;
    coverage.private_texture_wrapper_rooted_packets +=
        has_private_wrapper_root ? 1u : 0u;
    coverage.any_rooted_packets += has_draw_root || has_vertex_shader_root ||
                                           has_pixel_shader_root ||
                                           has_private_wrapper_root
                                       ? 1u
                                       : 0u;
  }
  return coverage;
}

void WriteFrameDatabaseRootCoverage(
    std::ofstream& file,
    const FableDirectFrameDatabaseDrawPackets& draw_packets) {
  const FrameDatabaseRootCoverage coverage =
      BuildFrameDatabaseRootCoverage(draw_packets);
  file << "  \"root_coverage\": {\n";
  file << "    \"serialized_draw_packets\": " << draw_packets.packets.size()
       << ",\n";
  file << "    \"draw_rooted_packets\": " << coverage.draw_rooted_packets
       << ",\n";
  file << "    \"vertex_shader_rooted_packets\": "
       << coverage.vertex_shader_rooted_packets << ",\n";
  file << "    \"pixel_shader_rooted_packets\": "
       << coverage.pixel_shader_rooted_packets << ",\n";
  file << "    \"fully_shader_rooted_packets\": "
       << coverage.fully_shader_rooted_packets << ",\n";
  file << "    \"private_texture_wrapper_rooted_packets\": "
       << coverage.private_texture_wrapper_rooted_packets << ",\n";
  file << "    \"any_rooted_packets\": " << coverage.any_rooted_packets << "\n";
  file << "  }\n";
}

void PlotFrameDatabaseRootCoverage(
    const FableDirectFrameDatabaseDrawPackets& draw_packets) {
  const FrameDatabaseRootCoverage coverage =
      BuildFrameDatabaseRootCoverage(draw_packets);
  TracyPlot("Refii.RootCoverage.Serialized",
            static_cast<int64_t>(draw_packets.packets.size()));
  TracyPlot("Refii.RootCoverage.Draw",
            static_cast<int64_t>(coverage.draw_rooted_packets));
  TracyPlot("Refii.RootCoverage.VertexShader",
            static_cast<int64_t>(coverage.vertex_shader_rooted_packets));
  TracyPlot("Refii.RootCoverage.PixelShader",
            static_cast<int64_t>(coverage.pixel_shader_rooted_packets));
  TracyPlot("Refii.RootCoverage.FullShader",
            static_cast<int64_t>(coverage.fully_shader_rooted_packets));
  TracyPlot(
      "Refii.RootCoverage.PrivateWrapper",
      static_cast<int64_t>(coverage.private_texture_wrapper_rooted_packets));
  TracyPlot("Refii.RootCoverage.Any",
            static_cast<int64_t>(coverage.any_rooted_packets));
}

std::string BuildFrameDatabaseRootCoverageComment() {
  const FableDirectFrameDatabaseDrawPackets draw_packets =
      GetFableDirectFrameDatabaseDrawPackets();
  const FrameDatabaseRootCoverage coverage =
      BuildFrameDatabaseRootCoverage(draw_packets);
  return "Root coverage serialized/draw/vs/ps/full/wrapper/any: " +
         std::to_string(draw_packets.packets.size()) + " / " +
         std::to_string(coverage.draw_rooted_packets) + " / " +
         std::to_string(coverage.vertex_shader_rooted_packets) + " / " +
         std::to_string(coverage.pixel_shader_rooted_packets) + " / " +
         std::to_string(coverage.fully_shader_rooted_packets) + " / " +
         std::to_string(coverage.private_texture_wrapper_rooted_packets) +
         " / " + std::to_string(coverage.any_rooted_packets);
}

const char* RendererHookConfidenceName(RendererHookConfidence confidence) {
  switch (confidence) {
  case RendererHookConfidence::High:
    return "High";
  case RendererHookConfidence::Tentative:
    return "Tentative";
  case RendererHookConfidence::Unresolved:
    return "Unresolved";
  }
  return "Unknown";
}

const char* RendererHookReplacementName(RendererHookReplacement replacement) {
  switch (replacement) {
  case RendererHookReplacement::FullReplacement:
    return "FullReplacement";
  case RendererHookReplacement::FableAdapter:
    return "FableAdapter";
  case RendererHookReplacement::SideEffectOnly:
    return "SideEffectOnly";
  case RendererHookReplacement::Unresolved:
    return "Unresolved";
  }
  return "Unknown";
}

const char* RendererHookSurfaceName(RendererHookSurface surface) {
  switch (surface) {
  case RendererHookSurface::RequiredRoot:
    return "RequiredRoot";
  case RendererHookSurface::MigrationSupport:
    return "MigrationSupport";
  case RendererHookSurface::DebugSupport:
    return "DebugSupport";
  case RendererHookSurface::Unresolved:
    return "Unresolved";
  }
  return "Unknown";
}

uint64_t FrameDatabasePacketBytesEstimate(
    const FableDirectRendererStats& direct_stats) {
  return uint64_t(direct_stats.last_render_commands) *
         sizeof(FableDirectRenderCommand);
}

uint32_t CountFrameDatabaseCommandKind(
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    std::string_view kind) {
  return static_cast<uint32_t>(
      std::count_if(command_packets.packets.begin(),
                    command_packets.packets.end(),
                    [&](const FableDirectFrameDatabaseCommandPacket& packet) {
                      return packet.kind == kind;
                    }));
}

void WriteFrameDatabaseTracyCounters(
    std::ofstream& file, const FableDirectRendererStats& direct_stats,
    const RendererShaderLibrarySnapshot& shader_snapshot,
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FableDirectFrameDatabaseCommandPackets& command_packets,
    const std::vector<FrameDatabasePassSummary>& pass_summaries,
    const std::vector<FrameDatabaseResourceEdge>& resource_edges,
    const std::vector<FrameDatabasePassResourceUsage>& pass_resource_usages,
    const std::vector<FrameDatabaseFrameResourceLifetime>&
        frame_resource_lifetimes,
    const std::vector<FrameDatabaseMaterialRecord>& material_records,
    const std::vector<FrameDatabaseMeshRecord>& mesh_records,
    const std::vector<FrameDatabaseAssetUsageRecord>& asset_usage_records,
    const std::vector<FrameDatabaseRenderGraphNode>& render_graph_nodes,
    const std::vector<FrameDatabaseRenderGraphEdge>& render_graph_edges,
    const std::vector<FrameDatabaseRenderGraphExecutionStep>&
        render_graph_execution_steps,
    const std::vector<FrameDatabaseRenderGraphPhase>& render_graph_phases,
    const RefiiRenderGraphRuntimePlanSnapshot& render_graph_runtime_plan,
    const RefiiRenderGraphRuntimeSchedulerSnapshot&
        render_graph_runtime_scheduler,
    const PlumeFrameHistorySnapshot& frame_history,
    const std::vector<FrameDatabasePassDependency>& pass_dependencies,
    const std::vector<FrameDatabasePassResourceTransition>&
        pass_resource_transitions) {
  const uint64_t frame_database_bytes =
      FrameDatabasePacketBytesEstimate(direct_stats);
  const FrameDatabaseRootCoverage root_coverage =
      BuildFrameDatabaseRootCoverage(draw_packets);
  const FrameDatabaseResourceEdgeCounts resource_edge_counts =
      CountFrameDatabaseResourceEdges(resource_edges);
  const uint32_t pipeline_creates = direct_stats.pipeline_cache_misses;
  const uint32_t shader_misses = shader_snapshot.missing_binary_count;
  const uint32_t missing_textures = direct_stats.failed_resource_syncs;
  const uint32_t clear_packets =
      CountFrameDatabaseCommandKind(command_packets, "clear");
  const uint32_t resolve_packets =
      CountFrameDatabaseCommandKind(command_packets, "resolve");
  const uint32_t present_blit_packets =
      CountFrameDatabaseCommandKind(command_packets, "present_blit");
  constexpr uint32_t kBufferUploadsNotYetSeparated = 0u;

  file << "  \"tracy_counters\": {\n";
  file << "    \"Refii.DrawPackets\": " << direct_stats.draw_count << ",\n";
  file << "    \"Refii.DirectAttemptedDrawCommands\": "
       << direct_stats.attempted_draw_commands << ",\n";
  file << "    \"Refii.DirectRecordedDrawCommands\": "
       << direct_stats.recorded_draw_commands << ",\n";
  file << "    \"Refii.DirectFailedDrawCommands\": "
       << direct_stats.failed_draw_commands << ",\n";
  file << "    \"Refii.DirectRecordedClearCommands\": "
       << direct_stats.recorded_clear_commands << ",\n";
  file << "    \"Refii.DirectRecordedResolveCommands\": "
       << direct_stats.recorded_resolve_commands << ",\n";
  file << "    \"Refii.CommandPackets\": "
       << CountFrameDatabaseExecutableCommandPackets(command_packets) << ",\n";
  file << "    \"Refii.ClearPackets\": " << clear_packets << ",\n";
  file << "    \"Refii.ResolvePackets\": " << resolve_packets << ",\n";
  file << "    \"Refii.PresentBlitPackets\": " << present_blit_packets
       << ",\n";
  file << "    \"Refii.ResourceEdges\": "
       << resource_edge_counts.total_edges << ",\n";
  file << "    \"Refii.ClearResourceEdges\": "
       << resource_edge_counts.clear_edges << ",\n";
  file << "    \"Refii.ResolveResourceEdges\": "
       << resource_edge_counts.resolve_edges << ",\n";
  file << "    \"Refii.SurfaceDestinationEdges\": "
       << resource_edge_counts.surface_destination_edges << ",\n";
  file << "    \"Refii.PresentBlitEdges\": "
       << resource_edge_counts.present_blit_edges << ",\n";
  file << "    \"Refii.PassResourceUsages\": "
       << pass_resource_usages.size() << ",\n";
  file << "    \"Refii.PassClassified\": "
       << CountFrameDatabaseClassifiedPassSummaries(pass_summaries) << ",\n";
  file << "    \"Refii.FrameResourceLifetimes\": "
       << frame_resource_lifetimes.size() << ",\n";
  file << "    \"Refii.MaterialRecords\": "
       << material_records.size() << ",\n";
  file << "    \"Refii.MeshRecords\": " << mesh_records.size() << ",\n";
  file << "    \"Refii.AssetUsages\": "
       << asset_usage_records.size() << ",\n";
  file << "    \"Refii.RenderGraphNodes\": "
       << render_graph_nodes.size() << ",\n";
  file << "    \"Refii.RenderGraphEdges\": "
       << render_graph_edges.size() << ",\n";
  file << "    \"Refii.RenderGraphExecutionSteps\": "
       << render_graph_execution_steps.size() << ",\n";
  file << "    \"Refii.RenderGraphBarriers\": "
       << pass_resource_transitions.size() << ",\n";
  file << "    \"Refii.RenderGraphPhases\": "
       << render_graph_phases.size() << ",\n";
  file << "    \"Refii.RenderGraphRuntimeExecutions\": 1,\n";
  file << "    \"Refii.RenderGraphBridgeExecutions\": 1,\n";
  file << "    \"Refii.RenderGraphSchedulerExecutions\": "
       << render_graph_runtime_scheduler.executions << ",\n";
  file << "    \"Refii.RenderGraphSchedulerSteps\": "
       << render_graph_runtime_scheduler.steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerBridgeSteps\": "
       << render_graph_runtime_scheduler.bridge_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedSteps\": "
       << render_graph_runtime_scheduler.graph_owned_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedPlanSteps\": "
       << render_graph_runtime_scheduler.graph_owned_plan_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedRenderSteps\": "
       << render_graph_runtime_scheduler.graph_owned_render_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedClearSteps\": "
       << render_graph_runtime_scheduler.graph_owned_clear_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedClearCommands\": "
       << render_graph_runtime_scheduler.graph_owned_clear_commands << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedDrawSteps\": "
       << render_graph_runtime_scheduler.graph_owned_draw_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedDrawCommands\": "
       << render_graph_runtime_scheduler.graph_owned_draw_commands << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedResolveSteps\": "
       << render_graph_runtime_scheduler.graph_owned_resolve_steps << ",\n";
  file << "    \"Refii.RenderGraphSchedulerGraphOwnedResolveCommands\": "
       << render_graph_runtime_scheduler.graph_owned_resolve_commands << ",\n";
  file << "    \"Refii.RenderGraphSchedulerBridgeCommands\": "
       << render_graph_runtime_scheduler.bridge_commands << ",\n";
  file << "    \"Refii.RenderGraphRuntimePlanPasses\": "
       << render_graph_runtime_plan.passes << ",\n";
  file << "    \"Refii.RenderGraphRuntimePlanResources\": "
       << render_graph_runtime_plan.resource_references << ",\n";
  file << "    \"Refii.RenderGraphRuntimePlanEdgeOnlyPasses\": "
       << render_graph_runtime_plan.edge_only_passes << ",\n";
  file << "    \"Refii.RenderGraphRuntimePlanSurfaceDestinationEdges\": "
       << render_graph_runtime_plan.surface_destination_edges << ",\n";
  file << "    \"Refii.FrameHistoryEntries\": " << frame_history.count
       << ",\n";
  file << "    \"Refii.PassDependencies\": "
       << pass_dependencies.size() << ",\n";
  file << "    \"Refii.ResourceTransitions\": "
       << pass_resource_transitions.size() << ",\n";
  file << "    \"Refii.PipelineCreates\": " << pipeline_creates << ",\n";
  file << "    \"Refii.PipelineCacheMisses\": "
       << direct_stats.pipeline_cache_misses << ",\n";
  file << "    \"Refii.ShaderMisses\": " << shader_misses << ",\n";
  file << "    \"Refii.TextureUploads\": " << direct_stats.resource_uploads
       << ",\n";
  file << "    \"Refii.BufferUploads\": "
       << kBufferUploadsNotYetSeparated << ",\n";
  file << "    \"Refii.RenderTargetSwitches\": "
       << direct_stats.render_target_switches << ",\n";
  file << "    \"Refii.DepthTargetSwitches\": "
       << direct_stats.depth_target_switches << ",\n";
  file << "    \"Refii.MissingTextures\": " << missing_textures << ",\n";
  file << "    \"Refii.MissingShaders\": " << shader_misses << ",\n";
  file << "    \"Refii.FrameDatabaseBytes\": " << frame_database_bytes
       << ",\n";
  file << "    \"Refii.RootCoverage.Serialized\": "
       << draw_packets.packets.size() << ",\n";
  file << "    \"Refii.RootCoverage.Draw\": "
       << root_coverage.draw_rooted_packets << ",\n";
  file << "    \"Refii.RootCoverage.VertexShader\": "
       << root_coverage.vertex_shader_rooted_packets << ",\n";
  file << "    \"Refii.RootCoverage.PixelShader\": "
       << root_coverage.pixel_shader_rooted_packets << ",\n";
  file << "    \"Refii.RootCoverage.FullShader\": "
       << root_coverage.fully_shader_rooted_packets << ",\n";
  file << "    \"Refii.RootCoverage.PrivateWrapper\": "
       << root_coverage.private_texture_wrapper_rooted_packets << ",\n";
  file << "    \"Refii.RootCoverage.Any\": "
       << root_coverage.any_rooted_packets << "\n";
  file << "  }\n";
}

void WriteFrameDatabaseDrawPackets(
    std::ofstream& file,
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    uint32_t frame_index,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"draw_packets\": [\n";
  for (size_t index = 0; index < draw_packets.packets.size(); ++index) {
    const FableDirectFrameDatabaseDrawPacket& packet =
        draw_packets.packets[index];
    file << "    {\n";
    file << "      \"packet_id\": " << packet.packet_id << ",\n";
    file << "      \"command_serial\": " << packet.command_serial << ",\n";
    const std::string packet_label =
        packet.debug_label.empty()
            ? BuildFrameDatabaseDrawLabel(frame_index, packet)
            : packet.debug_label;
    file << "      \"label\": \""
         << JsonEscape(packet_label) << "\",\n";
    file << "      \"tracy_zone\": \"RefiiRenderer::SubmitDrawPacket\",\n";
    file << "      \"tracy_text\": \""
         << JsonEscape(packet_label) << "\",\n";
    file << "      \"renderdoc_marker\": \""
         << JsonEscape(packet_label) << "\",\n";
    file << "      \"command_type\": " << packet.command_type << ",\n";
    file << "      \"source\": " << packet.source << ",\n";
    file << "      \"pass\": \"" << JsonEscape(packet.pass_name) << "\",\n";
    file << "      \"roots\": {\n";
    file << "        \"draw\": ";
    WriteFrameDatabaseRoot(file, packet.draw_root);
    file << ",\n";
    file << "        \"vertex_shader\": ";
    WriteFrameDatabaseRoot(file, packet.vertex_shader_root);
    file << ",\n";
    file << "        \"pixel_shader\": ";
    WriteFrameDatabaseRoot(file, packet.pixel_shader_root);
    file << ",\n";
    file << "        \"private_texture_wrapper\": ";
    WriteFrameDatabaseRoot(file, packet.private_texture_wrapper_root);
    file << "\n";
    file << "      },\n";
    file << "      \"indexed\": " << (packet.indexed ? "true" : "false")
         << ",\n";
    file << "      \"primitive_type\": " << packet.primitive_type << ",\n";
    file << "      \"start\": " << packet.start << ",\n";
    file << "      \"primitive_count\": " << packet.primitive_count << ",\n";
    file << "      \"vertex_or_index_count\": "
         << packet.vertex_or_index_count << ",\n";
    file << "      \"base_vertex\": " << packet.base_vertex << ",\n";
    WriteFrameDatabasePipelineState(file, packet, "      ");
    file << ",\n";
    file << "      \"vertex_shader\": " << packet.vertex_shader << ",\n";
    file << "      \"vertex_shader_hex\": \"" << Hex32(packet.vertex_shader)
         << "\",\n";
    file << "      \"vertex_shader_name\": \""
         << JsonEscape(FrameDatabaseShaderDisplayNameFor(
                packet.vertex_shader, name_index, "vertex_shader"))
         << "\",\n";
    WriteFrameDatabaseShaderArtifact(file, "vertex_shader_artifact",
                                     packet.vertex_shader, name_index,
                                     "      ");
    file << ",\n";
    file << "      \"pixel_shader\": " << packet.pixel_shader << ",\n";
    file << "      \"pixel_shader_hex\": \"" << Hex32(packet.pixel_shader)
         << "\",\n";
    file << "      \"pixel_shader_name\": \""
         << JsonEscape(FrameDatabaseShaderDisplayNameFor(
                packet.pixel_shader, name_index, "pixel_shader"))
         << "\",\n";
    WriteFrameDatabaseShaderArtifact(file, "pixel_shader_artifact",
                                     packet.pixel_shader, name_index,
                                     "      ");
    file << ",\n";
    file << "      \"vertex_declaration\": " << packet.vertex_declaration
         << ",\n";
    file << "      \"vertex_declaration_hex\": \""
         << Hex32(packet.vertex_declaration) << "\",\n";
    file << "      \"vertex_declaration_name\": \""
         << JsonEscape(name_index.NameFor(packet.vertex_declaration,
                                          "vertex_declaration"))
         << "\",\n";
    WriteJsonU32Array(file, "render_targets", packet.render_targets, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "render_targets_hex", packet.render_targets,
                         "      ");
    file << ",\n";
    WriteJsonU32NameArray(file, "render_targets_names", packet.render_targets,
                          name_index, "render_target", "      ");
    file << ",\n";
    file << "      \"depth_stencil\": " << packet.depth_stencil << ",\n";
    file << "      \"depth_stencil_hex\": \"" << Hex32(packet.depth_stencil)
         << "\",\n";
    file << "      \"depth_stencil_name\": \""
         << JsonEscape(name_index.NameFor(packet.depth_stencil,
                                          "depth_stencil"))
         << "\",\n";
    WriteJsonU32Array(file, "textures", packet.textures, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "textures_hex", packet.textures, "      ");
    file << ",\n";
    WriteJsonU32NameArray(file, "textures_names", packet.textures,
                          name_index, "texture", "      ");
    file << ",\n";
    file << "      \"resolved_bindings_valid\": "
         << (packet.resolved_bindings_valid ? "true" : "false") << ",\n";
    WriteJsonU32HexArray(file, "resolved_textures_hex",
                         packet.resolved_textures, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "resolved_texture_bases_hex",
                         packet.resolved_texture_bases, "      ");
    file << ",\n";
    WriteJsonU32Array(file, "sampler_states", packet.sampler_states, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "sampler_states_hex", packet.sampler_states,
                         "      ");
    file << ",\n";
    WriteJsonU32Array(file, "vertex_buffers", packet.vertex_buffers, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "vertex_buffers_hex", packet.vertex_buffers,
                         "      ");
    file << ",\n";
    WriteJsonU32NameArray(file, "vertex_buffers_names",
                          packet.vertex_buffers, name_index, "vertex_buffer",
                          "      ");
    file << ",\n";
    WriteJsonU32Array(file, "vertex_strides", packet.vertex_strides, "      ");
    file << ",\n";
    WriteJsonU32Array(file, "vertex_offsets", packet.vertex_offsets, "      ");
    file << ",\n";
    file << "      \"index_buffer\": " << packet.index_buffer << ",\n";
    file << "      \"index_buffer_hex\": \"" << Hex32(packet.index_buffer)
         << "\",\n";
    file << "      \"index_buffer_name\": \""
         << JsonEscape(name_index.NameFor(packet.index_buffer,
                                          "index_buffer"))
         << "\",\n";
    file << "      \"index_format\": " << packet.index_format << ",\n";
    WriteFrameDatabaseInlineBufferArray(file, packet.inline_vertex_buffers,
                                        "inline_vertex_buffers", "      ");
    file << ",\n";
    file << "      \"inline_index_buffer\": ";
    WriteFrameDatabaseInlineBufferMetadata(file, packet.inline_index_buffer);
    file << ",\n";
    WriteFrameDatabaseInlineTextureArray(file, packet.pix_video_planes,
                                         "pix_video_planes", "      ");
    file << ",\n";
    file << "      \"viewport\": {\"valid\": "
         << (packet.viewport_valid ? "true" : "false")
         << ", \"x\": " << packet.viewport_x
         << ", \"y\": " << packet.viewport_y
         << ", \"width\": " << packet.viewport_width
         << ", \"height\": " << packet.viewport_height
         << ", \"min_depth\": " << packet.viewport_min_depth
         << ", \"max_depth\": " << packet.viewport_max_depth << "},\n";
    file << "      \"scissor\": {\"valid\": "
         << (packet.scissor_valid ? "true" : "false")
         << ", \"left\": " << packet.scissor_left
         << ", \"top\": " << packet.scissor_top
         << ", \"right\": " << packet.scissor_right
         << ", \"bottom\": " << packet.scissor_bottom << "},\n";
    file << "      \"private_texture\": {\"enabled\": "
         << (packet.private_texture.enabled ? "true" : "false")
         << ", \"texture_slot\": " << packet.private_texture.texture_slot
         << ", \"source_x\": " << packet.private_texture.source_x
         << ", \"source_y\": " << packet.private_texture.source_y
         << ", \"corners_valid\": "
         << (packet.private_texture.corners_valid ? "true" : "false")
         << ", \"corners\": [";
    for (size_t corner_index = 0;
         corner_index < packet.private_texture.corners.size();
         ++corner_index) {
      if (corner_index != 0) {
        file << ", ";
      }
      file << packet.private_texture.corners[corner_index];
    }
    file << "], \"uv_override\": "
         << (packet.private_texture.uv_override ? "true" : "false")
         << ", \"u0\": " << packet.private_texture.u0
         << ", \"v0\": " << packet.private_texture.v0
         << ", \"u1\": " << packet.private_texture.u1
         << ", \"v1\": " << packet.private_texture.v1
         << ", \"wrapper_source_texture\": "
         << packet.private_texture.wrapper_source_texture
         << ", \"wrapper_source_texture_hex\": \""
         << Hex32(packet.private_texture.wrapper_source_texture)
         << "\", \"wrapper_source_texture_name\": \""
         << JsonEscape(name_index.NameFor(
                packet.private_texture.wrapper_source_texture, "texture"))
         << "\""
         << ", \"wrapper_source_exact\": "
         << (packet.private_texture.wrapper_source_exact ? "true" : "false")
         << "},\n";
    WriteFrameDatabaseConstantSummary(file, packet.constants);
    file << "\n";
    file << "    }" << (index + 1u == draw_packets.packets.size() ? "" : ",")
         << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabasePipelineEvents(
    std::ofstream& file,
    const FableDirectFrameDatabaseDrawPackets& draw_packets,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"pipeline_events\": [\n";
  for (size_t index = 0; index < draw_packets.packets.size(); ++index) {
    const FableDirectFrameDatabaseDrawPacket& packet =
        draw_packets.packets[index];
    const uint64_t vertex_hash =
        name_index.ContentHashFor(packet.vertex_shader);
    const uint64_t pixel_hash = name_index.ContentHashFor(packet.pixel_shader);
    file << "    {\n";
    file << "      \"event_index\": " << index << ",\n";
    file << "      \"event\": \"draw_pipeline_bind\",\n";
    file << "      \"packet_id\": " << packet.packet_id << ",\n";
    file << "      \"command_serial\": " << packet.command_serial << ",\n";
    file << "      \"pass\": \"" << JsonEscape(packet.pass_name) << "\",\n";
    file << "      \"label\": \""
         << JsonEscape(packet.debug_label) << "\",\n";
    file << "      \"vertex_shader\": " << packet.vertex_shader << ",\n";
    file << "      \"vertex_shader_hex\": \"" << Hex32(packet.vertex_shader)
         << "\",\n";
    file << "      \"vertex_shader_hash_hex\": \"" << Hex64(vertex_hash)
         << "\",\n";
    file << "      \"pixel_shader\": " << packet.pixel_shader << ",\n";
    file << "      \"pixel_shader_hex\": \"" << Hex32(packet.pixel_shader)
         << "\",\n";
    file << "      \"pixel_shader_hash_hex\": \"" << Hex64(pixel_hash)
         << "\",\n";
    file << "      \"vertex_declaration\": " << packet.vertex_declaration
         << ",\n";
    file << "      \"vertex_declaration_hex\": \""
         << Hex32(packet.vertex_declaration) << "\",\n";
    file << "      \"render_target0_hex\": \""
         << Hex32(packet.render_targets[0]) << "\",\n";
    file << "      \"depth_stencil_hex\": \"" << Hex32(packet.depth_stencil)
         << "\",\n";
    file << "      \"primitive_type\": " << packet.primitive_type << ",\n";
    WriteFrameDatabasePipelineState(file, packet, "      ");
    file << "\n";
    file << "    }"
         << (index + 1u == draw_packets.packets.size() ? "" : ",")
         << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseMaterialRecords(
    std::ofstream& file,
    const std::vector<FrameDatabaseMaterialRecord>& records,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"material_records\": [\n";
  for (size_t index = 0; index < records.size(); ++index) {
    const FrameDatabaseMaterialRecord& record = records[index];
    file << "    {\n";
    file << "      \"material_id\": " << record.material_id << ",\n";
    file << "      \"material_id_hex\": \"" << Hex64(record.material_id)
         << "\",\n";
    file << "      \"material_name\": \""
         << JsonEscape(FrameDatabaseSyntheticName64("material",
                                                    record.material_id))
         << "\",\n";
    file << "      \"first_packet_id\": " << record.first_packet_id << ",\n";
    file << "      \"draw_count\": " << record.draw_count << ",\n";
    file << "      \"first_pass\": \"" << JsonEscape(record.first_pass)
         << "\",\n";
    file << "      \"last_pass\": \"" << JsonEscape(record.last_pass)
         << "\",\n";
    file << "      \"semantic_class\": \""
         << JsonEscape(record.semantic_class.empty() ? "unknown"
                                                     : record.semantic_class)
         << "\",\n";
    file << "      \"pixel_shader\": " << record.pixel_shader << ",\n";
    file << "      \"pixel_shader_hex\": \"" << Hex32(record.pixel_shader)
         << "\",\n";
    file << "      \"pixel_shader_name\": \""
         << JsonEscape(name_index.NameFor(record.pixel_shader,
                                          "pixel_shader"))
         << "\",\n";
    WriteFrameDatabaseShaderArtifact(file, "pixel_shader_artifact",
                                     record.pixel_shader, name_index,
                                     "      ");
    file << ",\n";
    WriteJsonU32Array(file, "textures", record.textures, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "textures_hex", record.textures, "      ");
    file << ",\n";
    WriteJsonU32NameArray(file, "textures_names", record.textures,
                          name_index, "texture", "      ");
    file << ",\n";
    WriteJsonU32Array(file, "sampler_states", record.sampler_states, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "sampler_states_hex", record.sampler_states,
                         "      ");
    file << ",\n";
    WriteJsonU32Array(file, "render_targets", record.render_targets, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "render_targets_hex", record.render_targets,
                         "      ");
    file << ",\n";
    WriteJsonU32NameArray(file, "render_targets_names",
                          record.render_targets, name_index, "render_target",
                          "      ");
    file << ",\n";
    file << "      \"depth_stencil\": " << record.depth_stencil << ",\n";
    file << "      \"depth_stencil_hex\": \"" << Hex32(record.depth_stencil)
         << "\",\n";
    file << "      \"depth_stencil_name\": \""
         << JsonEscape(name_index.NameFor(record.depth_stencil,
                                          "depth_stencil"))
         << "\"\n";
    file << "    }" << (index + 1u == records.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseMeshRecords(
    std::ofstream& file,
    const std::vector<FrameDatabaseMeshRecord>& records,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"mesh_records\": [\n";
  for (size_t index = 0; index < records.size(); ++index) {
    const FrameDatabaseMeshRecord& record = records[index];
    file << "    {\n";
    file << "      \"mesh_id\": " << record.mesh_id << ",\n";
    file << "      \"mesh_id_hex\": \"" << Hex64(record.mesh_id) << "\",\n";
    file << "      \"mesh_name\": \""
         << JsonEscape(FrameDatabaseSyntheticName64("mesh", record.mesh_id))
         << "\",\n";
    file << "      \"first_packet_id\": " << record.first_packet_id << ",\n";
    file << "      \"draw_count\": " << record.draw_count << ",\n";
    file << "      \"first_pass\": \"" << JsonEscape(record.first_pass)
         << "\",\n";
    file << "      \"last_pass\": \"" << JsonEscape(record.last_pass)
         << "\",\n";
    file << "      \"semantic_class\": \""
         << JsonEscape(record.semantic_class.empty() ? "unknown"
                                                     : record.semantic_class)
         << "\",\n";
    file << "      \"vertex_declaration\": "
         << record.vertex_declaration << ",\n";
    file << "      \"vertex_declaration_hex\": \""
         << Hex32(record.vertex_declaration) << "\",\n";
    file << "      \"vertex_declaration_name\": \""
         << JsonEscape(name_index.NameFor(record.vertex_declaration,
                                          "vertex_declaration"))
         << "\",\n";
    WriteJsonU32Array(file, "vertex_buffers", record.vertex_buffers, "      ");
    file << ",\n";
    WriteJsonU32HexArray(file, "vertex_buffers_hex", record.vertex_buffers,
                         "      ");
    file << ",\n";
    WriteJsonU32NameArray(file, "vertex_buffers_names",
                          record.vertex_buffers, name_index, "vertex_buffer",
                          "      ");
    file << ",\n";
    WriteJsonU32Array(file, "vertex_strides", record.vertex_strides, "      ");
    file << ",\n";
    WriteJsonU32Array(file, "vertex_offsets", record.vertex_offsets, "      ");
    file << ",\n";
    file << "      \"index_buffer\": " << record.index_buffer << ",\n";
    file << "      \"index_buffer_hex\": \"" << Hex32(record.index_buffer)
         << "\",\n";
    file << "      \"index_buffer_name\": \""
         << JsonEscape(name_index.NameFor(record.index_buffer,
                                          "index_buffer"))
         << "\",\n";
    file << "      \"index_format\": " << record.index_format << ",\n";
    file << "      \"indexed\": " << (record.indexed ? "true" : "false")
         << ",\n";
    file << "      \"primitive_type\": " << record.primitive_type << ",\n";
    file << "      \"start\": " << record.start << ",\n";
    file << "      \"vertex_or_index_count\": "
         << record.vertex_or_index_count << ",\n";
    file << "      \"base_vertex\": " << record.base_vertex << "\n";
    file << "    }" << (index + 1u == records.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseAssetUsageRecords(
    std::ofstream& file,
    const std::vector<FrameDatabaseAssetUsageRecord>& records,
    const FrameDatabaseResourceNameIndex& name_index) {
  file << "  \"asset_usage_records\": [\n";
  for (size_t index = 0; index < records.size(); ++index) {
    const FrameDatabaseAssetUsageRecord& record = records[index];
    file << "    {\"kind\": \"" << JsonEscape(record.kind)
         << "\", \"resource_id\": " << record.resource_id
         << ", \"resource_id_hex\": \"" << Hex32(record.resource_id)
         << "\""
         << ", \"resource_name\": \""
         << JsonEscape(name_index.NameFor(record.resource_id, record.kind))
         << "\""
         << ", \"slot\": " << record.slot
         << ", \"slot_hex\": \"" << Hex32(record.slot) << "\""
         << ", \"packet_id\": " << record.packet_id
         << ", \"pass\": \"" << JsonEscape(record.pass_name)
         << "\", \"semantic_class\": \""
         << JsonEscape(record.semantic_class.empty() ? "unknown"
                                                     : record.semantic_class)
         << "\", \"material_id\": " << record.material_id
         << ", \"material_id_hex\": \"" << Hex64(record.material_id)
         << "\""
         << ", \"material_name\": \""
         << JsonEscape(FrameDatabaseSyntheticName64("material",
                                                    record.material_id))
         << "\""
         << ", \"mesh_id\": " << record.mesh_id
         << ", \"mesh_id_hex\": \"" << Hex64(record.mesh_id) << "\""
         << ", \"mesh_name\": \""
         << JsonEscape(FrameDatabaseSyntheticName64("mesh", record.mesh_id))
         << "\"}"
         << (index + 1u == records.size() ? "" : ",") << "\n";
  }
  file << "  ]\n";
}

void WriteFrameDatabaseRenderGraph(
    std::ofstream& file,
    const std::vector<FrameDatabaseRenderGraphNode>& nodes,
    const std::vector<FrameDatabaseRenderGraphEdge>& edges,
    const std::vector<FrameDatabaseRenderGraphExecutionStep>& execution_plan,
    const std::vector<FrameDatabaseRenderGraphPhase>& phases,
    const std::vector<FrameDatabasePassResourceTransition>& transitions,
    const std::vector<FrameDatabaseFrameResourceLifetime>& lifetimes) {
  file << "  \"render_graph\": {\n";
  file << "    \"summary\": {\"nodes\": " << nodes.size()
       << ", \"edges\": " << edges.size()
       << ", \"execution_steps\": " << execution_plan.size()
       << ", \"barriers\": " << transitions.size()
       << ", \"phases\": " << phases.size()
       << ", \"resource_transitions\": " << transitions.size()
       << ", \"frame_resource_lifetimes\": " << lifetimes.size() << "},\n";
  file << "    \"nodes\": [\n";
  for (size_t index = 0; index < nodes.size(); ++index) {
    const FrameDatabaseRenderGraphNode& node = nodes[index];
    file << "      {\"pass_index\": " << node.pass_index
         << ", \"pass\": \"" << JsonEscape(node.pass_name)
         << "\", \"class\": \"" << JsonEscape(node.pass_class)
         << "\", \"phase\": \"" << JsonEscape(node.phase_name)
         << "\", \"draw_packets\": " << node.draw_packets
         << ", \"command_packets\": " << node.command_packets
         << ", \"resource_usages\": " << node.resource_usages
         << ", \"read_resources\": " << node.read_resources
         << ", \"write_resources\": " << node.write_resources
         << ", \"material_usages\": " << node.material_usages
         << ", \"mesh_usages\": " << node.mesh_usages
         << ", \"asset_usages\": " << node.asset_usages << "}"
         << (index + 1u == nodes.size() ? "" : ",") << "\n";
  }
  file << "    ],\n";
  file << "    \"phases\": [\n";
  for (size_t index = 0; index < phases.size(); ++index) {
    const FrameDatabaseRenderGraphPhase& phase = phases[index];
    file << "      {\"phase_index\": " << phase.phase_index
         << ", \"phase\": \"" << JsonEscape(phase.phase_name)
         << "\", \"first_pass_index\": " << phase.first_pass_index
         << ", \"last_pass_index\": " << phase.last_pass_index
         << ", \"pass_count\": " << phase.pass_count
         << ", \"draw_packets\": " << phase.draw_packets
         << ", \"command_packets\": " << phase.command_packets
         << ", \"resource_usages\": " << phase.resource_usages
         << ", \"read_resources\": " << phase.read_resources
         << ", \"write_resources\": " << phase.write_resources
         << ", \"material_usages\": " << phase.material_usages
         << ", \"mesh_usages\": " << phase.mesh_usages
         << ", \"asset_usages\": " << phase.asset_usages
         << ", \"wait_edges\": " << phase.wait_edges
         << ", \"signal_edges\": " << phase.signal_edges
         << ", \"barriers_before\": " << phase.barriers_before
         << ", \"barriers_after\": " << phase.barriers_after << "}"
         << (index + 1u == phases.size() ? "" : ",") << "\n";
  }
  file << "    ],\n";
  file << "    \"edges\": [\n";
  for (size_t index = 0; index < edges.size(); ++index) {
    const FrameDatabaseRenderGraphEdge& edge = edges[index];
    file << "      {\"edge_index\": " << edge.edge_index
         << ", \"producer_pass\": \"" << JsonEscape(edge.producer_pass)
         << "\", \"consumer_pass\": \"" << JsonEscape(edge.consumer_pass)
         << "\", \"resource_id\": " << edge.resource_id
         << ", \"hazard\": \"" << JsonEscape(edge.hazard) << "\"}"
         << (index + 1u == edges.size() ? "" : ",") << "\n";
  }
  file << "    ],\n";
  file << "    \"execution_plan\": [\n";
  for (size_t index = 0; index < execution_plan.size(); ++index) {
    const FrameDatabaseRenderGraphExecutionStep& step =
        execution_plan[index];
    file << "      {\"step_index\": " << step.step_index
         << ", \"pass_index\": " << step.pass_index
         << ", \"pass\": \"" << JsonEscape(step.pass_name)
         << "\", \"class\": \"" << JsonEscape(step.pass_class)
         << "\", \"wait_edges\": " << step.wait_edges
         << ", \"signal_edges\": " << step.signal_edges
         << ", \"barriers_before\": " << step.barriers_before
         << ", \"barriers_after\": " << step.barriers_after
         << ", \"execute_draws\": "
         << (step.execute_draws ? "true" : "false")
         << ", \"execute_commands\": "
         << (step.execute_commands ? "true" : "false")
         << ", \"reads_resources\": "
         << (step.reads_resources ? "true" : "false")
         << ", \"writes_resources\": "
         << (step.writes_resources ? "true" : "false") << "}"
         << (index + 1u == execution_plan.size() ? "" : ",") << "\n";
  }
  file << "    ]\n";
  file << "  }\n";
}

void WriteFrameDatabaseFrameHistory(std::ofstream& file,
                                    const PlumeFrameHistorySnapshot& history,
                                    uint32_t frame_index) {
  const bool has_latest = history.count != 0;
  const uint32_t latest_frame =
      has_latest ? history.entries[0].frame_index : 0u;
  bool includes_capture_frame = false;
  for (uint32_t index = 0; index < history.count; ++index) {
    if (history.entries[index].frame_index == frame_index) {
      includes_capture_frame = true;
      break;
    }
  }

  file << "  \"frame_history\": {\n";
  file << "    \"summary\": {\"capacity\": " << kPlumeFrameHistoryCapacity
       << ", \"count\": " << history.count
       << ", \"latest_frame\": " << latest_frame
       << ", \"includes_capture_frame\": "
       << (includes_capture_frame ? "true" : "false") << "},\n";
  file << "    \"entries\": [\n";
  for (uint32_t index = 0; index < history.count; ++index) {
    const PlumeFrameHistoryEntry& entry = history.entries[index];
    file << "      {\"history_index\": " << index
         << ", \"frame\": " << entry.frame_index
         << ", \"presented\": " << (entry.presented ? "true" : "false")
         << ", \"renderdoc_capture_active\": "
         << (entry.renderdoc_capture_active ? "true" : "false")
         << ", \"width\": " << entry.width
         << ", \"height\": " << entry.height
         << ", \"draw_packets\": " << entry.draw_count
         << ", \"indexed_draw_packets\": " << entry.indexed_draw_count
         << ", \"render_commands\": " << entry.render_command_count
         << ", \"texture_uploads\": " << entry.resource_upload_count
         << ", \"pipeline_cache_misses\": "
         << entry.pipeline_cache_miss_count
         << ", \"missing_resources\": "
         << entry.failed_resource_sync_count
         << ", \"present_target\": " << entry.present_target
         << ", \"frame_total_us\": " << entry.frame_total_us
         << ", \"direct_us\": " << entry.direct_us
         << ", \"submit_us\": " << entry.submit_us
         << ", \"present_us\": " << entry.present_us << "}"
         << (index + 1u == history.count ? "" : ",") << "\n";
  }
  file << "    ]\n";
  file << "  }\n";
}

void WriteFrameDatabaseCommandPackets(
    std::ofstream& file,
    const FableDirectFrameDatabaseCommandPackets& command_packets) {
  file << "  \"command_packets\": [\n";
  for (size_t index = 0; index < command_packets.packets.size(); ++index) {
    const FableDirectFrameDatabaseCommandPacket& packet =
        command_packets.packets[index];
    file << "    {\n";
    file << "      \"packet_id\": " << packet.packet_id << ",\n";
    file << "      \"command_serial\": " << packet.command_serial << ",\n";
    file << "      \"command_type\": " << packet.command_type << ",\n";
    file << "      \"kind\": \"" << JsonEscape(packet.kind) << "\",\n";
    file << "      \"pass\": \"" << JsonEscape(packet.pass_name) << "\",\n";
    file << "      \"draw_packet_id\": " << packet.draw_packet_id << ",\n";
    file << "      \"root\": ";
    WriteFrameDatabaseRoot(file, packet.root);
    file << ",\n";
    WriteJsonU32Array(file, "render_targets", packet.render_targets, "      ");
    file << ",\n";
    file << "      \"depth_stencil\": " << packet.depth_stencil << ",\n";
    file << "      \"clear\": {\"flags\": " << packet.clear_flags
         << ", \"color\": [";
    for (size_t color_index = 0; color_index < packet.clear_color.size();
         ++color_index) {
      if (color_index != 0) {
        file << ", ";
      }
      file << packet.clear_color[color_index];
    }
    file << "], \"depth\": " << packet.clear_depth
         << ", \"stencil\": " << packet.clear_stencil
         << ", \"rect\": {\"valid\": "
         << (packet.rect_valid ? "true" : "false")
         << ", \"left\": " << packet.rect_left
         << ", \"top\": " << packet.rect_top
         << ", \"right\": " << packet.rect_right
         << ", \"bottom\": " << packet.rect_bottom << "}},\n";
    file << "      \"resolve\": {\"surface_info\": "
         << packet.resolve_surface_info << ", ";
    WriteJsonU32Array(file, "color_info", packet.resolve_color_info, "");
    file << ", \"depth_info\": " << packet.resolve_depth_info
         << ", \"copy_control\": " << packet.resolve_copy_control
         << ", \"source_surface\": " << packet.resolve_source_surface
         << ", \"source_slot\": " << packet.resolve_source_slot
         << ", \"source_depth\": "
         << (packet.resolve_source_depth ? "true" : "false")
         << ", \"dest_base\": " << packet.resolve_dest_base
         << ", \"dest_pitch\": " << packet.resolve_dest_pitch
         << ", \"dest_info\": " << packet.resolve_dest_info
         << ", \"copy_func\": " << packet.resolve_copy_func
         << ", \"copy_ref\": " << packet.resolve_copy_ref
         << ", \"copy_mask\": " << packet.resolve_copy_mask
         << ", \"surface_slice\": " << packet.resolve_surface_slice << "},\n";
    file << "      \"surface_destination\": {\"relation_serial\": "
         << packet.surface_destination.relation_serial
         << ", \"source_surface\": "
         << packet.surface_destination.source_surface
         << ", \"destination_texture\": "
         << packet.surface_destination.destination_texture
         << ", \"source_slot\": "
         << packet.surface_destination.source_slot
         << ", \"source_width\": "
         << packet.surface_destination.source_width
         << ", \"source_height\": "
         << packet.surface_destination.source_height
         << ", \"destination_width\": "
         << packet.surface_destination.destination_width
         << ", \"destination_height\": "
         << packet.surface_destination.destination_height
         << ", \"texture_base\": "
         << packet.surface_destination.texture_base
         << ", \"backing_texture\": "
         << packet.surface_destination.backing_texture
         << ", \"backing_base\": "
         << packet.surface_destination.backing_base
         << ", \"backing_content_serial\": "
         << packet.surface_destination.backing_content_serial
         << ", \"target_content_serial\": "
         << packet.surface_destination.target_content_serial
         << ", \"scaled_copy\": "
         << (packet.surface_destination.scaled_copy ? "true" : "false")
         << ", \"trusted_destination_edge\": "
         << (packet.surface_destination.trusted_destination_edge ? "true"
                                                                 : "false")
         << ", \"wrapper_source_exact\": "
         << (packet.surface_destination.wrapper_source_exact ? "true"
                                                            : "false")
         << "},\n";
    file << "      \"present_blit\": {\"source_target\": "
         << packet.present_blit.source_target
         << ", \"output_resource\": "
         << packet.present_blit.output_resource
         << ", \"source_width\": " << packet.present_blit.source_width
         << ", \"source_height\": " << packet.present_blit.source_height
         << ", \"texture_width\": " << packet.present_blit.texture_width
         << ", \"texture_height\": " << packet.present_blit.texture_height
         << ", \"output_width\": " << packet.present_blit.output_width
         << ", \"output_height\": " << packet.present_blit.output_height
         << ", \"source_write_serial\": "
         << packet.present_blit.source_write_serial
         << ", \"source_frame\": " << packet.present_blit.source_frame
         << ", \"reason\": \"" << JsonEscape(packet.present_blit.reason)
         << "\"}\n";
    file << "    }"
         << (index + 1u == command_packets.packets.size() ? "" : ",")
         << "\n";
  }
  file << "  ]\n";
}

std::string WriteRenderDocFrameDatabaseDump(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot,
    const FableDirectStateSnapshot& direct_snapshot,
    std::string_view correlation_label,
    uint32_t capture_draw_index,
    std::string_view capture_draw_target,
    std::string_view capture_pass,
    std::string_view capture_reason,
    bool renderdoc_refii_owned_capture,
    bool renderdoc_record_time_capture) {
  const std::filesystem::path capture_dir = ResolveRenderDocFrameDatabaseDir();
  if (capture_dir.empty()) {
    return {};
  }

  const std::filesystem::path path =
      capture_dir /
      ("refii_plume_frame" + std::to_string(frame_index) +
       ".frame_database.json");
  std::ofstream file(path, std::ios::binary | std::ios::trunc);
  if (!file) {
    REXLOG_WARN("Refii frame database dump could not be opened: {}",
                path.string());
    return {};
  }
  const FableDirectFrameDatabaseDrawPackets draw_packets =
      GetFableDirectFrameDatabaseDrawPackets();
  const FableDirectFrameDatabaseCommandPackets command_packets =
      GetFableDirectFrameDatabaseCommandPackets();
  const RefiiFramePacketQueueSnapshot packet_queue =
      GetRefiiFramePacketQueueSnapshot();
  const FableDirectResourceDebugSnapshot resource_snapshot =
      GetFableDirectResourceDebugSnapshot();
  const FrameDatabaseResourceNameIndex resource_name_index =
      BuildFrameDatabaseResourceNameIndex(resource_snapshot);
  const std::vector<FrameDatabaseResourceEdge> resource_edges =
      BuildFrameDatabaseResourceEdges(command_packets);
  const std::vector<FrameDatabasePassSummary> pass_summaries =
      BuildFrameDatabasePassSummaries(draw_packets, command_packets,
                                      resource_edges);
  const std::vector<FrameDatabasePassResourceUsage> pass_resource_usages =
      BuildFrameDatabasePassResourceUsages(draw_packets, resource_edges);
  const std::vector<FrameDatabaseFrameResourceLifetime>
      frame_resource_lifetimes =
          BuildFrameDatabaseFrameResourceLifetimes(pass_summaries,
                                                  pass_resource_usages);
  const std::vector<FrameDatabaseMaterialRecord> material_records =
      BuildFrameDatabaseMaterialRecords(draw_packets, resource_name_index);
  const std::vector<FrameDatabaseMeshRecord> mesh_records =
      BuildFrameDatabaseMeshRecords(draw_packets, resource_name_index);
  const std::vector<FrameDatabaseAssetUsageRecord> asset_usage_records =
      BuildFrameDatabaseAssetUsageRecords(draw_packets, resource_name_index);
  const std::vector<FrameDatabasePassDependency> pass_dependencies =
      BuildFrameDatabasePassDependencies(pass_summaries, pass_resource_usages);
  const std::vector<FrameDatabasePassResourceTransition>
      pass_resource_transitions =
          BuildFrameDatabasePassResourceTransitions(pass_dependencies);
  const std::vector<FrameDatabaseRenderGraphNode> render_graph_nodes =
      BuildFrameDatabaseRenderGraphNodes(pass_summaries, pass_resource_usages,
                                        asset_usage_records);
  const std::vector<FrameDatabaseRenderGraphEdge> render_graph_edges =
      BuildFrameDatabaseRenderGraphEdges(pass_dependencies);
  const std::vector<FrameDatabaseRenderGraphExecutionStep>
      render_graph_execution_steps =
          BuildFrameDatabaseRenderGraphExecutionPlan(
              render_graph_nodes, render_graph_edges,
              pass_resource_transitions);
  const std::vector<FrameDatabaseRenderGraphPhase> render_graph_phases =
      BuildFrameDatabaseRenderGraphPhases(render_graph_nodes,
                                          render_graph_execution_steps);
  const std::vector<FrameDatabaseResourceEvent> resource_events =
      BuildFrameDatabaseResourceEvents(draw_packets);
  const FrameDatabaseResourceEdgeCounts resource_edge_counts =
      CountFrameDatabaseResourceEdges(resource_edges);
  const RendererShaderLibrarySnapshot shader_snapshot =
      GetRendererShaderLibrarySnapshot();
  const PlumeFrameHistorySnapshot frame_history =
      GetPlumeFrameHistorySnapshot();
  const RefiiRenderGraphRuntimePlanSnapshot render_graph_runtime_plan =
      BuildRefiiRenderGraphRuntimePlanSnapshot(
          frame_index, draw_packets, command_packets, pass_summaries,
          resource_edges, pass_resource_usages, render_graph_nodes,
          render_graph_execution_steps, render_graph_phases,
          pass_resource_transitions);
  StoreRefiiRenderGraphRuntimePlanSnapshot(render_graph_runtime_plan);
  const RefiiRenderGraphRuntimeSchedulerSnapshot render_graph_scheduler =
      GetRefiiRenderGraphRuntimeSchedulerSnapshot();

  file << "{\n";
  file << "  \"schema\": \"refii.frame_database.v0\",\n";
  file << "  \"source\": \"RenderDocCapture\",\n";
  file << "  \"capture_label\": \"" << JsonEscape(correlation_label)
       << "\",\n";
  file << "  \"capture_reason\": \"" << JsonEscape(capture_reason)
       << "\",\n";
  file << "  \"capture_trigger\": {\"draw\": " << capture_draw_index
       << ", \"draw_target\": \"" << JsonEscape(capture_draw_target)
       << "\", \"pass\": \"" << JsonEscape(capture_pass)
       << "\", \"reason\": \"" << JsonEscape(capture_reason)
       << "\", \"refii_owned\": "
       << (renderdoc_refii_owned_capture ? "true" : "false")
       << ", \"record_time_started\": "
       << (renderdoc_record_time_capture ? "true" : "false") << "},\n";
  file << "  \"frame\": " << frame_index << ",\n";
  file << "  \"output\": {\"width\": " << width << ", \"height\": "
       << height << "},\n";
  file << "  \"direct\": {\n";
  file << "    \"draw_packets\": " << direct_stats.draw_count << ",\n";
  file << "    \"indexed_draw_packets\": " << direct_stats.indexed_draw_count
       << ",\n";
  file << "    \"draw_events\": " << direct_stats.last_draw_events << ",\n";
  file << "    \"render_commands\": " << direct_stats.last_render_commands
       << ",\n";
  file << "    \"presented\": "
       << (direct_stats.presented ? "true" : "false") << ",\n";
  file << "    \"present_target\": " << direct_stats.last_present_target
       << ",\n";
  file << "    \"present_target_hex\": \""
       << Hex32(direct_stats.last_present_target) << "\",\n";
  file << "    \"present_width\": " << direct_stats.last_present_width
       << ",\n";
  file << "    \"present_height\": " << direct_stats.last_present_height
       << ",\n";
  file << "    \"serialized_draw_packets\": "
       << draw_packets.packets.size() << ",\n";
  file << "    \"omitted_draw_packets\": "
       << draw_packets.omitted_packets << ",\n";
  file << "    \"serialized_command_packets\": "
       << CountFrameDatabaseExecutableCommandPackets(command_packets) << ",\n";
  file << "    \"serialized_clear_packets\": "
       << CountFrameDatabaseCommandKind(command_packets, "clear") << ",\n";
  file << "    \"serialized_resolve_packets\": "
       << CountFrameDatabaseCommandKind(command_packets, "resolve") << ",\n";
  file << "    \"serialized_surface_destination_packets\": "
       << CountFrameDatabaseCommandKind(command_packets, "surface_destination")
       << ",\n";
  file << "    \"serialized_present_blit_packets\": "
       << CountFrameDatabaseCommandKind(command_packets, "present_blit")
       << ",\n";
  file << "    \"omitted_command_packets\": "
       << command_packets.omitted_packets << ",\n";
  file << "    \"packet_queue_generation\": " << packet_queue.generation
       << ",\n";
  file << "    \"packet_queue_depth\": " << packet_queue.packets.size()
       << ",\n";
  file << "    \"packet_action_queue_depth\": " << packet_queue.actions.size()
       << ",\n";
  file << "    \"omitted_packet_actions\": " << packet_queue.omitted_actions
       << ",\n";
  file << "    \"packet_queue_replay_ready\": "
       << (packet_queue.replay_ready ? 1 : 0) << ",\n";
  file << "    \"packet_queue_replay_active\": "
       << (direct_stats.packet_queue_replay_enabled ? 1 : 0) << ",\n";
  file << "    \"packet_replay_planned_actions\": "
       << packet_queue.replay.planned_action_packets << ",\n";
  file << "    \"packet_replay_planned\": "
       << packet_queue.replay.planned_draw_packets << ",\n";
  file << "    \"packet_replay_planned_clears\": "
       << packet_queue.replay.planned_clear_packets << ",\n";
  file << "    \"packet_replay_planned_resolves\": "
       << packet_queue.replay.planned_resolve_packets << ",\n";
  file << "    \"packet_replay_planned_copies\": "
       << packet_queue.replay.planned_copy_packets << ",\n";
  file << "    \"packet_replay_planned_presents\": "
       << packet_queue.replay.planned_present_packets << ",\n";
  file << "    \"packet_replay_unsupported\": "
       << packet_queue.replay.unsupported_packets << ",\n";
  file << "    \"packet_replay_indexed\": "
       << packet_queue.replay.indexed_packets << ",\n";
  file << "    \"packet_replay_missing_shaders\": "
       << packet_queue.replay.missing_shader_packets << ",\n";
  file << "    \"packet_replay_missing_targets\": "
       << packet_queue.replay.missing_target_packets << ",\n";
  file << "    \"packet_replay_missing_vertex_input\": "
       << packet_queue.replay.missing_vertex_input_packets << ",\n";
  file << "    \"packet_replay_missing_index_input\": "
       << packet_queue.replay.missing_index_input_packets << ",\n";
  file << "    \"packet_replay_empty_draws\": "
       << packet_queue.replay.empty_draw_packets << ",\n";
  file << "    \"packet_replay_missing_private_texture_payload\": "
       << packet_queue.replay.missing_private_texture_payload_packets << ",\n";
  file << "    \"packet_replay_unsupported_sources\": "
       << packet_queue.replay.unsupported_source_packets << ",\n";
  file << "    \"direct_attempted_draw_commands\": "
       << direct_stats.attempted_draw_commands << ",\n";
  file << "    \"direct_recorded_draw_commands\": "
       << direct_stats.recorded_draw_commands << ",\n";
  file << "    \"direct_failed_draw_commands\": "
       << direct_stats.failed_draw_commands << ",\n";
  file << "    \"direct_recorded_clear_commands\": "
       << direct_stats.recorded_clear_commands << ",\n";
  file << "    \"direct_recorded_resolve_commands\": "
       << direct_stats.recorded_resolve_commands << ",\n";
  file << "    \"resource_events\": " << resource_events.size() << ",\n";
  file << "    \"resource_edges\": " << resource_edges.size() << ",\n";
  file << "    \"clear_resource_edges\": "
       << resource_edge_counts.clear_edges << ",\n";
  file << "    \"resolve_resource_edges\": "
       << resource_edge_counts.resolve_edges << ",\n";
  file << "    \"surface_destination_edges\": "
       << resource_edge_counts.surface_destination_edges << ",\n";
  file << "    \"present_blit_edges\": "
       << resource_edge_counts.present_blit_edges << ",\n";
  file << "    \"pass_summaries\": " << pass_summaries.size() << ",\n";
  file << "    \"pass_classified\": "
       << CountFrameDatabaseClassifiedPassSummaries(pass_summaries) << ",\n";
  file << "    \"pass_resource_usages\": "
       << pass_resource_usages.size() << ",\n";
  file << "    \"frame_resource_lifetimes\": "
       << frame_resource_lifetimes.size() << ",\n";
  file << "    \"material_records\": " << material_records.size() << ",\n";
  file << "    \"mesh_records\": " << mesh_records.size() << ",\n";
  file << "    \"asset_usage_records\": "
       << asset_usage_records.size() << ",\n";
  file << "    \"render_graph_nodes\": " << render_graph_nodes.size() << ",\n";
  file << "    \"render_graph_edges\": " << render_graph_edges.size() << ",\n";
  file << "    \"render_graph_execution_steps\": "
       << render_graph_execution_steps.size() << ",\n";
  file << "    \"render_graph_barriers\": "
       << pass_resource_transitions.size() << ",\n";
  file << "    \"render_graph_phases\": " << render_graph_phases.size()
       << ",\n";
  file << "    \"render_graph_runtime_executions\": 1,\n";
  file << "    \"render_graph_bridge_executions\": 1,\n";
  file << "    \"render_graph_scheduler_executions\": "
       << render_graph_scheduler.executions << ",\n";
  file << "    \"render_graph_scheduler_steps\": "
       << render_graph_scheduler.steps << ",\n";
  file << "    \"render_graph_scheduler_bridge_steps\": "
       << render_graph_scheduler.bridge_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_steps\": "
       << render_graph_scheduler.graph_owned_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_plan_steps\": "
       << render_graph_scheduler.graph_owned_plan_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_render_steps\": "
       << render_graph_scheduler.graph_owned_render_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_clear_steps\": "
       << render_graph_scheduler.graph_owned_clear_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_clear_commands\": "
       << render_graph_scheduler.graph_owned_clear_commands << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_draw_steps\": "
       << render_graph_scheduler.graph_owned_draw_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_draw_commands\": "
       << render_graph_scheduler.graph_owned_draw_commands << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_resolve_steps\": "
       << render_graph_scheduler.graph_owned_resolve_steps << ",\n";
  file << "    \"render_graph_scheduler_graph_owned_resolve_commands\": "
       << render_graph_scheduler.graph_owned_resolve_commands << ",\n";
  file << "    \"surface_destination_relations\": "
       << direct_stats.surface_destination_relations << ",\n";
  file << "    \"surface_destination_omitted_relations\": "
       << direct_stats.surface_destination_omitted_relations << ",\n";
  file << "    \"surface_destination_copied_exact\": "
       << direct_stats.surface_destination_copied_exact << ",\n";
  file << "    \"surface_destination_copied_scaled\": "
       << direct_stats.surface_destination_copied_scaled << ",\n";
  file << "    \"surface_destination_skipped_invalid\": "
       << direct_stats.surface_destination_skipped_invalid << ",\n";
  file << "    \"surface_destination_skipped_zero_endpoint\": "
       << direct_stats.surface_destination_skipped_zero_endpoint << ",\n";
  file << "    \"surface_destination_skipped_missing_copy_root\": "
       << direct_stats.surface_destination_skipped_missing_copy_root << ",\n";
  file << "    \"surface_destination_skipped_wrong_frame\": "
       << direct_stats.surface_destination_skipped_wrong_frame << ",\n";
  file << "    \"surface_destination_skipped_missing_resource\": "
       << direct_stats.surface_destination_skipped_missing_resource << ",\n";
  file << "    \"surface_destination_skipped_not_image\": "
       << direct_stats.surface_destination_skipped_not_image << ",\n";
  file << "    \"surface_destination_skipped_not_ready\": "
       << direct_stats.surface_destination_skipped_not_ready << ",\n";
  file << "    \"surface_destination_skipped_same_texture\": "
       << direct_stats.surface_destination_skipped_same_texture << ",\n";
  file << "    \"surface_destination_skipped_inexact_wrapper\": "
       << direct_stats.surface_destination_skipped_inexact_wrapper << ",\n";
  file << "    \"surface_destination_skipped_stale_content\": "
       << direct_stats.surface_destination_skipped_stale_content << ",\n";
  file << "    \"surface_destination_skipped_copy_failed\": "
       << direct_stats.surface_destination_skipped_copy_failed << ",\n";
  file << "    \"render_graph_scheduler_bridge_commands\": "
       << render_graph_scheduler.bridge_commands << ",\n";
  file << "    \"render_graph_runtime_plan_passes\": "
       << render_graph_runtime_plan.passes << ",\n";
  file << "    \"render_graph_runtime_plan_packet_passes\": "
       << render_graph_runtime_plan.packet_passes << ",\n";
  file << "    \"render_graph_runtime_plan_edge_only_passes\": "
       << render_graph_runtime_plan.edge_only_passes << ",\n";
  file << "    \"render_graph_runtime_plan_omitted_passes\": "
       << render_graph_runtime_plan.omitted_passes << ",\n";
  file << "    \"render_graph_runtime_plan_draw_packets\": "
       << render_graph_runtime_plan.draw_packets << ",\n";
  file << "    \"render_graph_runtime_plan_omitted_draw_packets\": "
       << render_graph_runtime_plan.omitted_draw_packets << ",\n";
  file << "    \"render_graph_runtime_plan_command_packets\": "
       << render_graph_runtime_plan.command_packets << ",\n";
  file << "    \"render_graph_runtime_plan_omitted_command_packets\": "
       << render_graph_runtime_plan.omitted_command_packets << ",\n";
  file << "    \"render_graph_runtime_plan_resource_references\": "
       << render_graph_runtime_plan.resource_references << ",\n";
  file << "    \"render_graph_runtime_plan_resource_edges\": "
       << render_graph_runtime_plan.resource_edges << ",\n";
  file << "    \"render_graph_runtime_plan_surface_destination_edges\": "
       << render_graph_runtime_plan.surface_destination_edges << ",\n";
  file << "    \"render_graph_runtime_plan_execution_steps\": "
       << render_graph_runtime_plan.execution_steps << ",\n";
  file << "    \"render_graph_runtime_plan_barriers\": "
       << render_graph_runtime_plan.barriers << ",\n";
  file << "    \"render_graph_runtime_plan_phases\": "
       << render_graph_runtime_plan.phases << ",\n";
  file << "    \"frame_history_entries\": " << frame_history.count << ",\n";
  file << "    \"pass_dependencies\": " << pass_dependencies.size() << ",\n";
  file << "    \"pass_resource_transitions\": "
       << pass_resource_transitions.size() << ",\n";
  file << "    \"frame_packet_bytes_estimate\": "
       << FrameDatabasePacketBytesEstimate(direct_stats) << ",\n";
  file << "    \"pipeline_cache_hits\": "
       << direct_stats.pipeline_cache_hits << ",\n";
  file << "    \"pipeline_cache_misses\": "
       << direct_stats.pipeline_cache_misses << ",\n";
  file << "    \"texture_uploads\": " << direct_stats.resource_uploads
       << ",\n";
  file << "    \"missing_resources\": " << direct_stats.failed_resource_syncs
       << ",\n";
  file << "    \"render_target_switches\": "
       << direct_stats.render_target_switches << ",\n";
  file << "    \"depth_target_switches\": "
       << direct_stats.depth_target_switches << ",\n";
  file << "    \"cumulative_render_target_switches\": "
       << direct_snapshot.render_target_switch_count << ",\n";
  file << "    \"cumulative_depth_target_switches\": "
       << direct_snapshot.depth_target_switch_count << "\n";
  file << "  },\n";
  file << "  \"fable_state\": {\n";
  WriteJsonU32Array(file, "render_targets", direct_snapshot.targets.color);
  file << ",\n";
  WriteJsonU32HexArray(file, "render_targets_hex",
                       direct_snapshot.targets.color);
  file << ",\n";
  WriteJsonU32NameArray(file, "render_targets_names",
                        direct_snapshot.targets.color, resource_name_index,
                        "render_target");
  file << ",\n";
  file << "    \"depth_stencil\": " << direct_snapshot.targets.depth << ",\n";
  file << "    \"depth_stencil_hex\": \""
       << Hex32(direct_snapshot.targets.depth) << "\",\n";
  file << "    \"depth_stencil_name\": \""
       << JsonEscape(resource_name_index.NameFor(direct_snapshot.targets.depth,
                                                 "depth_stencil"))
       << "\",\n";
  file << "    \"vertex_shader\": " << direct_snapshot.shaders.vertex_shader
       << ",\n";
  file << "    \"vertex_shader_hex\": \""
       << Hex32(direct_snapshot.shaders.vertex_shader) << "\",\n";
  file << "    \"vertex_shader_name\": \""
       << JsonEscape(resource_name_index.NameFor(
              direct_snapshot.shaders.vertex_shader, "vertex_shader"))
       << "\",\n";
  WriteFrameDatabaseShaderArtifact(file, "vertex_shader_artifact",
                                   direct_snapshot.shaders.vertex_shader,
                                   resource_name_index, "    ");
  file << ",\n";
  file << "    \"pixel_shader\": " << direct_snapshot.shaders.pixel_shader
       << ",\n";
  file << "    \"pixel_shader_hex\": \""
       << Hex32(direct_snapshot.shaders.pixel_shader) << "\",\n";
  file << "    \"pixel_shader_name\": \""
       << JsonEscape(resource_name_index.NameFor(
              direct_snapshot.shaders.pixel_shader, "pixel_shader"))
       << "\",\n";
  WriteFrameDatabaseShaderArtifact(file, "pixel_shader_artifact",
                                   direct_snapshot.shaders.pixel_shader,
                                   resource_name_index, "    ");
  file << "\n";
  file << "  },\n";
  file << "  \"hook_observer\": {\n";
  file << "    \"vertex_shader\": " << hook_snapshot.vertex_shader << ",\n";
  file << "    \"vertex_shader_hex\": \"" << Hex32(hook_snapshot.vertex_shader)
       << "\",\n";
  file << "    \"vertex_shader_name\": \""
       << JsonEscape(FrameDatabaseShaderDisplayNameFor(
              hook_snapshot.vertex_shader, resource_name_index,
              "vertex_shader"))
       << "\",\n";
  WriteFrameDatabaseShaderArtifact(file, "vertex_shader_artifact",
                                   hook_snapshot.vertex_shader,
                                   resource_name_index, "    ");
  file << ",\n";
  file << "    \"pixel_shader\": " << hook_snapshot.pixel_shader << ",\n";
  file << "    \"pixel_shader_hex\": \"" << Hex32(hook_snapshot.pixel_shader)
       << "\",\n";
  file << "    \"pixel_shader_name\": \""
       << JsonEscape(FrameDatabaseShaderDisplayNameFor(
              hook_snapshot.pixel_shader, resource_name_index, "pixel_shader"))
       << "\",\n";
  WriteFrameDatabaseShaderArtifact(file, "pixel_shader_artifact",
                                   hook_snapshot.pixel_shader,
                                   resource_name_index, "    ");
  file << ",\n";
  file << "    \"last_pix_texture\": " << hook_snapshot.last_pix_texture
       << ",\n";
  file << "    \"last_pix_texture_hex\": \""
       << Hex32(hook_snapshot.last_pix_texture) << "\",\n";
  file << "    \"last_pix_texture_name\": \""
       << JsonEscape(resource_name_index.NameFor(hook_snapshot.last_pix_texture,
                                                 "texture"))
       << "\",\n";
  file << "    \"pix_decode_frames\": " << hook_snapshot.pix_decode_frames
       << ",\n";
  file << "    \"pix_decode_pending_frames\": "
       << hook_snapshot.pix_decode_pending_frames << ",\n";
  file << "    \"pix_render_hooks\": " << hook_snapshot.pix_render_hooks
       << ",\n";
  file << "    \"pix_render_events\": " << hook_snapshot.pix_render_events
       << ",\n";
  file << "    \"pix_render_uv_overrides\": "
       << hook_snapshot.pix_render_uv_overrides << ",\n";
  file << "    \"pix_capture_hooks\": " << hook_snapshot.pix_capture_hooks
       << ",\n";
  file << "    \"pix_capture_packet_builder_paths\": "
       << hook_snapshot.pix_capture_packet_builder_paths << ",\n";
  file << "    \"pix_capture_suppressed_paths\": "
       << hook_snapshot.pix_capture_suppressed_paths << ",\n";
  file << "    \"pix_capture_suppressed_missing_device_memory\": "
       << hook_snapshot.pix_capture_suppressed_missing_device_memory << ",\n";
  file << "    \"pix_capture_suppressed_control_disabled\": "
       << hook_snapshot.pix_capture_suppressed_control_disabled << ",\n";
  file << "    \"pix_capture_suppressed_nonzero_r5\": "
       << hook_snapshot.pix_capture_suppressed_nonzero_r5 << ",\n";
  file << "    \"pix_capture_suppressed_alternate_display_path\": "
       << hook_snapshot.pix_capture_suppressed_alternate_display_path << ",\n";
  file << "    \"last_pix_capture_frame\": "
       << hook_snapshot.last_pix_capture_frame << ",\n";
  file << "    \"last_pix_capture_frame_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_frame) << "\",\n";
  file << "    \"last_pix_capture_format\": "
       << hook_snapshot.last_pix_capture_format << ",\n";
  file << "    \"last_pix_capture_format_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_format) << "\",\n";
  file << "    \"last_pix_capture_size\": \""
       << hook_snapshot.last_pix_capture_width << "x"
       << hook_snapshot.last_pix_capture_height << "\",\n";
  file << "    \"last_pix_capture_scaler\": "
       << hook_snapshot.last_pix_capture_scaler << ",\n";
  file << "    \"last_pix_capture_scaler_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_scaler) << "\",\n";
  file << "    \"last_pix_capture_gamma\": "
       << hook_snapshot.last_pix_capture_gamma << ",\n";
  file << "    \"last_pix_capture_gamma_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_gamma) << "\",\n";
  file << "    \"last_pix_capture_control\": "
       << hook_snapshot.last_pix_capture_control << ",\n";
  file << "    \"last_pix_capture_control_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_control) << "\",\n";
  file << "    \"last_pix_capture_display_flags\": "
       << hook_snapshot.last_pix_capture_display_flags << ",\n";
  file << "    \"last_pix_capture_display_flags_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_display_flags) << "\",\n";
  file << "    \"last_pix_capture_r5\": "
       << hook_snapshot.last_pix_capture_r5 << ",\n";
  file << "    \"last_pix_capture_r5_hex\": \""
       << Hex32(hook_snapshot.last_pix_capture_r5) << "\",\n";
  file << "    \"last_pix_capture_packet_branch_candidate\": "
       << hook_snapshot.last_pix_capture_packet_branch_candidate << ",\n";
  file << "    \"pix_render_bad_packets\": "
       << hook_snapshot.pix_render_bad_packets << ",\n";
  file << "    \"pix_render_bad_vertices\": "
       << hook_snapshot.pix_render_bad_vertices << ",\n";
  file << "    \"last_pix_packet\": " << hook_snapshot.last_pix_packet
       << ",\n";
  file << "    \"last_pix_packet_hex\": \""
       << Hex32(hook_snapshot.last_pix_packet) << "\",\n";
  file << "    \"last_pix_decode_base\": "
       << hook_snapshot.last_pix_decode_base << ",\n";
  file << "    \"last_pix_decode_base_hex\": \""
       << Hex32(hook_snapshot.last_pix_decode_base) << "\",\n";
  file << "    \"last_pix_decode_size\": \""
       << hook_snapshot.last_pix_decode_width << "x"
       << hook_snapshot.last_pix_decode_height << "\",\n";
  file << "    \"last_pix_fetch_base\": "
       << hook_snapshot.last_pix_fetch_base << ",\n";
  file << "    \"last_pix_fetch_base_hex\": \""
       << Hex32(hook_snapshot.last_pix_fetch_base) << "\",\n";
  file << "    \"last_pix_fetch_size\": "
       << hook_snapshot.last_pix_fetch_size << ",\n";
  file << "    \"last_pix_fetch_format\": "
       << hook_snapshot.last_pix_fetch_format << ",\n";
  file << "    \"last_pix_output_size\": \""
       << hook_snapshot.last_pix_output_width << "x"
       << hook_snapshot.last_pix_output_height << "\",\n";
  file << "    \"last_pix_decoded_size\": \""
       << hook_snapshot.last_pix_decoded_width << "x"
       << hook_snapshot.last_pix_decoded_height << "\",\n";
  file << "    \"last_pix_uv\": \"" << hook_snapshot.last_pix_u1 << "x"
       << hook_snapshot.last_pix_v1 << "\",\n";
  file << "    \"title_render_list_dispatches\": "
       << hook_snapshot.title_render_list_dispatches << ",\n";
  file << "    \"title_render_list_insert_callbacks\": "
       << hook_snapshot.title_render_list_insert_callbacks << ",\n";
  file << "    \"title_render_list_sorted_inserts\": "
       << hook_snapshot.title_render_list_sorted_inserts << ",\n";
  file << "    \"title_render_list_inserted_nodes\": "
       << hook_snapshot.title_render_list_inserted_nodes << ",\n";
  file << "    \"title_world_registry_observations\": "
       << hook_snapshot.title_world_registry_observations << ",\n";
  file << "    \"last_title_render_list\": "
       << hook_snapshot.last_title_render_list << ",\n";
  file << "    \"last_title_render_list_hex\": \""
       << Hex32(hook_snapshot.last_title_render_list) << "\",\n";
  file << "    \"last_title_render_list_owner\": "
       << hook_snapshot.last_title_render_list_owner << ",\n";
  file << "    \"last_title_render_list_owner_hex\": \""
       << Hex32(hook_snapshot.last_title_render_list_owner) << "\",\n";
  file << "    \"last_title_render_object\": "
       << hook_snapshot.last_title_render_object << ",\n";
  file << "    \"last_title_render_object_hex\": \""
       << Hex32(hook_snapshot.last_title_render_object) << "\",\n";
  file << "    \"last_title_render_object_vtable\": "
       << hook_snapshot.last_title_render_object_vtable << ",\n";
  file << "    \"last_title_render_object_vtable_hex\": \""
       << Hex32(hook_snapshot.last_title_render_object_vtable) << "\",\n";
  file << "    \"last_title_render_node\": "
       << hook_snapshot.last_title_render_node << ",\n";
  file << "    \"last_title_render_node_hex\": \""
       << Hex32(hook_snapshot.last_title_render_node) << "\",\n";
  file << "    \"last_title_render_node_vtable\": "
       << hook_snapshot.last_title_render_node_vtable << ",\n";
  file << "    \"last_title_render_node_vtable_hex\": \""
       << Hex32(hook_snapshot.last_title_render_node_vtable) << "\",\n";
  file << "    \"last_title_render_sort_key\": "
       << hook_snapshot.last_title_render_sort_key << ",\n";
  file << "    \"last_title_render_sort_key_hex\": \""
       << Hex32(hook_snapshot.last_title_render_sort_key) << "\",\n";
  file << "    \"last_title_render_flags\": "
       << hook_snapshot.last_title_render_flags << ",\n";
  file << "    \"last_title_render_flags_hex\": \""
       << Hex32(hook_snapshot.last_title_render_flags) << "\",\n";
  file << "    \"last_title_world\": "
       << hook_snapshot.last_title_world << ",\n";
  file << "    \"last_title_world_hex\": \""
       << Hex32(hook_snapshot.last_title_world) << "\",\n";
  file << "    \"last_title_world_registry\": "
       << hook_snapshot.last_title_world_registry << ",\n";
  file << "    \"last_title_world_registry_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry) << "\",\n";
  file << "    \"last_title_world_registry_begin\": "
       << hook_snapshot.last_title_world_registry_begin << ",\n";
  file << "    \"last_title_world_registry_begin_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry_begin) << "\",\n";
  file << "    \"last_title_world_registry_end\": "
       << hook_snapshot.last_title_world_registry_end << ",\n";
  file << "    \"last_title_world_registry_end_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry_end) << "\",\n";
  file << "    \"last_title_world_registry_count\": "
       << hook_snapshot.last_title_world_registry_count << ",\n";
  file << "    \"last_title_world_registry_search_key\": "
       << hook_snapshot.last_title_world_registry_search_key << ",\n";
  file << "    \"last_title_world_registry_search_key_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry_search_key)
       << "\",\n";
  file << "    \"last_title_world_registry_matched_entry\": "
       << hook_snapshot.last_title_world_registry_matched_entry << ",\n";
  file << "    \"last_title_world_registry_matched_entry_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry_matched_entry)
       << "\",\n";
  file << "    \"last_title_world_registry_matched_object_count\": "
       << hook_snapshot.last_title_world_registry_matched_object_count
       << ",\n";
  file << "    \"last_title_world_registry_first_object\": "
       << hook_snapshot.last_title_world_registry_first_object << ",\n";
  file << "    \"last_title_world_registry_first_object_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry_first_object)
       << "\",\n";
  file << "    \"last_title_world_registry_first_object_vtable\": "
       << hook_snapshot.last_title_world_registry_first_object_vtable
       << ",\n";
  file << "    \"last_title_world_registry_first_object_vtable_hex\": \""
       << Hex32(hook_snapshot.last_title_world_registry_first_object_vtable)
       << "\",\n";
  file << "    \"pm4_command_writes\": " << hook_snapshot.command_writes
       << ",\n";
  file << "    \"pm4_command_submits\": " << hook_snapshot.command_submits
       << ",\n";
  file << "    \"pm4_command_words\": " << hook_snapshot.command_words
       << ",\n";
  file << "    \"pm4_type0_packets\": " << hook_snapshot.pm4_type0_packets
       << ",\n";
  file << "    \"pm4_type3_packets\": " << hook_snapshot.pm4_type3_packets
       << ",\n";
  file << "    \"pm4_indirect_packets\": "
       << hook_snapshot.pm4_indirect_packets << ",\n";
  file << "    \"pm4_draw_packets\": " << hook_snapshot.pm4_draw_packets
       << ",\n";
  file << "    \"pm4_constant_packets\": "
       << hook_snapshot.pm4_constant_packets << ",\n";
  file << "    \"pm4_swap_packets\": " << hook_snapshot.pm4_swap_packets
       << ",\n";
  file << "    \"pm4_parse_errors\": " << hook_snapshot.pm4_parse_errors
       << ",\n";
  file << "    \"last_pm4_command_source\": "
       << hook_snapshot.last_command_source << ",\n";
  file << "    \"last_pm4_command_source_hex\": \""
       << Hex32(hook_snapshot.last_command_source) << "\",\n";
  file << "    \"last_pm4_command_pointer\": "
       << hook_snapshot.last_command_pointer << ",\n";
  file << "    \"last_pm4_command_pointer_hex\": \""
       << Hex32(hook_snapshot.last_command_pointer) << "\",\n";
  file << "    \"last_pm4_command_word_count\": "
       << hook_snapshot.last_command_word_count << ",\n";
  file << "    \"last_pm4_command_first_word\": "
       << hook_snapshot.last_command_first_word << ",\n";
  file << "    \"last_pm4_command_first_word_hex\": \""
       << Hex32(hook_snapshot.last_command_first_word) << "\",\n";
  file << "    \"last_pm4_command_packet_type\": "
       << hook_snapshot.last_command_packet_type << ",\n";
  file << "    \"last_pm4_command_type3_opcode\": "
       << hook_snapshot.last_command_type3_opcode << ",\n";
  file << "    \"last_pm4_command_type3_opcode_hex\": \""
       << Hex32(hook_snapshot.last_command_type3_opcode) << "\",\n";
  file << "    \"last_pm4_command_preview_hex\": [";
  for (size_t index = 0; index < std::size(hook_snapshot.last_command_preview);
       ++index) {
    if (index != 0) {
      file << ", ";
    }
    file << "\"" << Hex32(hook_snapshot.last_command_preview[index]) << "\"";
  }
  file << "]\n";
  file << "  },\n";
  file << "  \"render_graph_runtime\": {\n";
  file << "    \"mode\": \"fable_direct_bridge\",\n";
  file << "    \"execute_recorded\": true,\n";
  file << "    \"runtime_executions\": 1,\n";
  file << "    \"bridge_executions\": 1,\n";
  file << "    \"scheduler\": {\"frame\": "
       << render_graph_scheduler.frame_index
       << ", \"mode\": \"bridge_seed\", \"executions\": "
       << render_graph_scheduler.executions << ", \"steps\": "
       << render_graph_scheduler.steps << ", \"bridge_steps\": "
       << render_graph_scheduler.bridge_steps << ", \"graph_owned_steps\": "
       << render_graph_scheduler.graph_owned_steps
       << ", \"graph_owned_plan_steps\": "
       << render_graph_scheduler.graph_owned_plan_steps
       << ", \"graph_owned_render_steps\": "
       << render_graph_scheduler.graph_owned_render_steps
       << ", \"graph_owned_clear_steps\": "
       << render_graph_scheduler.graph_owned_clear_steps
       << ", \"graph_owned_clear_commands\": "
       << render_graph_scheduler.graph_owned_clear_commands
       << ", \"graph_owned_draw_steps\": "
       << render_graph_scheduler.graph_owned_draw_steps
       << ", \"graph_owned_draw_commands\": "
       << render_graph_scheduler.graph_owned_draw_commands
       << ", \"graph_owned_resolve_steps\": "
       << render_graph_scheduler.graph_owned_resolve_steps
       << ", \"graph_owned_resolve_commands\": "
       << render_graph_scheduler.graph_owned_resolve_commands
       << ", \"bridge_commands\": "
       << render_graph_scheduler.bridge_commands << "},\n";
  file << "    \"plan\": {\"frame\": "
       << render_graph_runtime_plan.frame_index << ", \"passes\": "
       << render_graph_runtime_plan.passes << ", \"packet_passes\": "
       << render_graph_runtime_plan.packet_passes
       << ", \"edge_only_passes\": "
       << render_graph_runtime_plan.edge_only_passes
       << ", \"omitted_passes\": "
       << render_graph_runtime_plan.omitted_passes << ", \"draw_packets\": "
       << render_graph_runtime_plan.draw_packets
       << ", \"omitted_draw_packets\": "
       << render_graph_runtime_plan.omitted_draw_packets
       << ", \"command_packets\": "
       << render_graph_runtime_plan.command_packets
       << ", \"omitted_command_packets\": "
       << render_graph_runtime_plan.omitted_command_packets
       << ", \"resource_references\": "
       << render_graph_runtime_plan.resource_references
       << ", \"resource_edges\": " << render_graph_runtime_plan.resource_edges
       << ", \"surface_destination_edges\": "
       << render_graph_runtime_plan.surface_destination_edges
       << ", \"execution_steps\": "
       << render_graph_runtime_plan.execution_steps << ", \"barriers\": "
       << render_graph_runtime_plan.barriers << ", \"phases\": "
       << render_graph_runtime_plan.phases << "}\n";
  file << "  },\n";
  WriteFrameDatabaseTracyCounters(file, direct_stats, shader_snapshot,
                                  draw_packets, command_packets,
                                  pass_summaries,
                                  resource_edges, pass_resource_usages,
                                  frame_resource_lifetimes,
                                  material_records, mesh_records,
                                  asset_usage_records,
                                  render_graph_nodes, render_graph_edges,
                                  render_graph_execution_steps,
                                  render_graph_phases,
                                  render_graph_runtime_plan,
                                  render_graph_scheduler,
                                  frame_history,
                                  pass_dependencies,
                                  pass_resource_transitions);
  file << ",\n";
  WriteFrameDatabaseRootCoverage(file, draw_packets);
  file << ",\n";
  WriteFrameDatabaseResourceNames(file, resource_snapshot,
                                  resource_name_index);
  file << ",\n";
  WriteFrameDatabaseDrawPackets(file, draw_packets, frame_index,
                                resource_name_index);
  file << ",\n";
  WriteFrameDatabasePipelineEvents(file, draw_packets, resource_name_index);
  file << ",\n";
  WriteFrameDatabaseMaterialRecords(file, material_records,
                                    resource_name_index);
  file << ",\n";
  WriteFrameDatabaseMeshRecords(file, mesh_records, resource_name_index);
  file << ",\n";
  WriteFrameDatabaseAssetUsageRecords(file, asset_usage_records,
                                      resource_name_index);
  file << ",\n";
  WriteFrameDatabaseRenderGraph(file, render_graph_nodes, render_graph_edges,
                                render_graph_execution_steps,
                                render_graph_phases,
                                pass_resource_transitions,
                                frame_resource_lifetimes);
  file << ",\n";
  WriteFrameDatabaseFrameHistory(file, frame_history, frame_index);
  file << ",\n";
  WriteFrameDatabaseCommandPackets(file, command_packets);
  file << ",\n";
  WriteFrameDatabasePassSummaries(file, pass_summaries);
  file << ",\n";
  WriteFrameDatabasePassResourceUsages(file, pass_resource_usages);
  file << ",\n";
  WriteFrameDatabaseFrameResourceLifetimes(file, frame_resource_lifetimes);
  file << ",\n";
  WriteFrameDatabasePassDependencies(file, pass_dependencies);
  file << ",\n";
  WriteFrameDatabasePassResourceTransitions(file, pass_resource_transitions);
  file << ",\n";
  WriteFrameDatabaseResourceEvents(file, resource_events,
                                   resource_name_index);
  file << ",\n";
  WriteFrameDatabaseResourceEdges(file, resource_edges);
  file << "}\n";
  if (!file) {
    REXLOG_WARN("Refii frame database dump write failed: {}", path.string());
    return {};
  }

  REXLOG_INFO("Refii frame database dump saved: {}", path.string());
  return path.string();
}

std::string WriteRenderDocHookTraceDump(
    uint32_t frame_index, uint32_t width, uint32_t height,
    std::string_view correlation_label,
    uint32_t capture_draw_index,
    std::string_view capture_draw_target,
    std::string_view capture_pass,
    std::string_view capture_reason,
    bool renderdoc_refii_owned_capture,
    bool renderdoc_record_time_capture) {
  const std::filesystem::path capture_dir = ResolveRenderDocFrameDatabaseDir();
  if (capture_dir.empty()) {
    return {};
  }

  const std::filesystem::path path =
      capture_dir /
      ("refii_plume_frame" + std::to_string(frame_index) +
       ".hook_trace.json");
  std::ofstream file(path, std::ios::binary | std::ios::trunc);
  if (!file) {
    REXLOG_WARN("Refii hook trace dump could not be opened: {}",
                path.string());
    return {};
  }

  const RendererHookStatsSnapshots hook_stats = GetRendererHookStatsSnapshots();
  uint64_t total_hits = 0;
  uint64_t total_guest_passthroughs = 0;
  uint32_t active_targets = 0;
  for (const RendererHookStatsSnapshot& stats : hook_stats) {
    total_hits += stats.hit_count;
    total_guest_passthroughs += stats.guest_passthrough_count;
    if (stats.hit_count != 0 || stats.guest_passthrough_count != 0) {
      ++active_targets;
    }
  }

  file << "{\n";
  file << "  \"schema\": \"refii.hook_trace.v0\",\n";
  file << "  \"source\": \"RenderDocCapture\",\n";
  file << "  \"capture_label\": \"" << JsonEscape(correlation_label)
       << "\",\n";
  file << "  \"capture_reason\": \"" << JsonEscape(capture_reason)
       << "\",\n";
  file << "  \"capture_trigger\": {\"draw\": " << capture_draw_index
       << ", \"draw_target\": \"" << JsonEscape(capture_draw_target)
       << "\", \"pass\": \"" << JsonEscape(capture_pass)
       << "\", \"reason\": \"" << JsonEscape(capture_reason)
       << "\", \"refii_owned\": "
       << (renderdoc_refii_owned_capture ? "true" : "false")
       << ", \"record_time_started\": "
       << (renderdoc_record_time_capture ? "true" : "false") << "},\n";
  file << "  \"frame\": " << frame_index << ",\n";
  file << "  \"output\": {\"width\": " << width << ", \"height\": "
       << height << "},\n";
  file << "  \"summary\": {\"targets\": " << hook_stats.size()
       << ", \"active_targets\": " << active_targets
       << ", \"hits\": " << total_hits
       << ", \"guest_passthroughs\": " << total_guest_passthroughs << "},\n";
  file << "  \"hooks\": [\n";
  for (size_t index = 0; index < hook_stats.size(); ++index) {
    const RendererHookStatsSnapshot& stats = hook_stats[index];
    const RendererHookTarget* target = RendererHookTargetById(stats.id);
    file << "    {";
    file << "\"id\": " << static_cast<uint32_t>(stats.id);
    file << ", \"name\": \"" << JsonEscape(stats.name) << "\"";
    file << ", \"fable_address\": " << stats.fable_address;
    file << ", \"fable_address_hex\": \"" << Hex32(stats.fable_address)
         << "\"";
    file << ", \"sonic_address\": " << stats.sonic_address;
    file << ", \"sonic_address_hex\": \"" << Hex32(stats.sonic_address)
         << "\"";
    file << ", \"confidence\": \""
         << (target != nullptr ? RendererHookConfidenceName(target->confidence)
                               : "Unknown")
         << "\"";
    file << ", \"replacement\": \""
         << (target != nullptr ? RendererHookReplacementName(target->replacement)
                               : "Unknown")
         << "\"";
    file << ", \"surface\": \""
         << (target != nullptr ? RendererHookSurfaceName(target->surface)
                               : "Unknown")
         << "\"";
    file << ", \"hits\": " << stats.hit_count;
    file << ", \"guest_passthroughs\": " << stats.guest_passthrough_count;
    file << ", \"first_frame_seen\": " << stats.first_frame_seen;
    file << ", \"last_frame_seen\": " << stats.last_frame_seen;
    file << "}";
    if (index + 1u != hook_stats.size()) {
      file << ",";
    }
    file << "\n";
  }
  file << "  ]\n";
  file << "}\n";

  if (!file) {
    REXLOG_WARN("Refii hook trace dump write failed: {}", path.string());
    return {};
  }

  REXLOG_INFO("Refii hook trace dump saved: {}", path.string());
  return path.string();
}

std::string BuildRenderDocCaptureComments(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot,
    const RendererHookTimingSnapshot& timing_snapshot,
    const FableDirectStateSnapshot& direct_snapshot,
    std::string_view frame_database_path,
    std::string_view hook_trace_path,
    std::string_view correlation_label,
    uint32_t capture_draw_index,
    std::string_view capture_draw_target,
    std::string_view capture_pass,
    std::string_view capture_reason) {
  std::string comments;
  comments.reserve(2048);
  comments += "Refii Plume RenderDoc capture\n";
  comments += "Correlation label: ";
  comments += correlation_label;
  comments += "\n";
  if (!capture_reason.empty()) {
    comments += "Capture reason: ";
    comments += capture_reason;
    comments += "\n";
  }
  if (capture_draw_index != 0 || !capture_draw_target.empty() ||
      !capture_pass.empty()) {
    comments += "Capture trigger: draw=" +
                std::to_string(capture_draw_index) + " target=";
    comments += capture_draw_target.empty() ? "<none>" : capture_draw_target;
    comments += " pass=";
    comments += capture_pass.empty() ? "<none>" : capture_pass;
    comments += "\n";
  }
  comments += "Frame: " + std::to_string(frame_index) + "\n";
  comments += "Output: " + std::to_string(width) + "x" +
              std::to_string(height) + "\n";
  comments += "Direct draws/indexed/events: " +
              std::to_string(direct_stats.draw_count) + " / " +
              std::to_string(direct_stats.indexed_draw_count) + " / " +
              std::to_string(direct_stats.last_draw_events) + "\n";
  comments += "Direct commands/resources/uploads: " +
              std::to_string(direct_stats.last_render_commands) + " / " +
              std::to_string(direct_stats.resource_creations) + " / " +
              std::to_string(direct_stats.resource_uploads) + "\n";
  comments += "Direct live resources tex/surf/vb/ib/sh: " +
              std::to_string(direct_stats.live_textures) + " / " +
              std::to_string(direct_stats.live_surfaces) + " / " +
              std::to_string(direct_stats.live_vertex_buffers) + " / " +
              std::to_string(direct_stats.live_index_buffers) + " / " +
              std::to_string(direct_stats.live_shaders) + "\n";
  comments += "Pipeline hits/misses failed hooks/resources/draws: " +
              std::to_string(direct_stats.pipeline_cache_hits) + " / " +
              std::to_string(direct_stats.pipeline_cache_misses) + " / " +
              std::to_string(direct_stats.failed_hooks) + " / " +
              std::to_string(direct_stats.failed_resource_syncs) + " / " +
              std::to_string(direct_stats.failed_draw_commands) + "\n";
  comments += "Direct attempted/recorded draw commands: " +
              std::to_string(direct_stats.attempted_draw_commands) + " / " +
              std::to_string(direct_stats.recorded_draw_commands) + "\n";
  comments += "Frame packet bytes estimate: " +
              std::to_string(uint64_t(direct_stats.last_render_commands) *
                             sizeof(FableDirectRenderCommand)) +
              "\n";
  comments += "RT/DT switches: " +
              std::to_string(direct_stats.render_target_switches) + " / " +
              std::to_string(direct_stats.depth_target_switches) + "\n";
  if (!frame_database_path.empty()) {
    comments += "Frame database: ";
    comments += frame_database_path;
    comments += "\n";
  }
  if (!hook_trace_path.empty()) {
    comments += "Hook trace: ";
    comments += hook_trace_path;
    comments += "\n";
  }
  comments += BuildFrameDatabaseRootCoverageComment();
  comments += "\n";
  comments += "Present target: " + Hex32(direct_stats.last_present_target) +
              " " + std::to_string(direct_stats.last_present_width) + "x" +
              std::to_string(direct_stats.last_present_height) + "\n";
  AppendRenderDocDisplayProvenanceComment(comments);
  comments += "GetRenderTarget calls/last slot/surface: " +
              std::to_string(hook_snapshot.get_render_target_calls) + " / " +
              std::to_string(hook_snapshot.last_get_render_target_slot) +
              " / " + Hex32(hook_snapshot.last_get_render_target_surface) +
              "\n";
  comments += "Pix decode/pending/render/events/capture: " +
              std::to_string(hook_snapshot.pix_decode_frames) + " / " +
              std::to_string(hook_snapshot.pix_decode_pending_frames) + " / " +
              std::to_string(hook_snapshot.pix_render_hooks) + " / " +
              std::to_string(hook_snapshot.pix_render_events) + " / " +
              std::to_string(hook_snapshot.pix_capture_hooks) + "\n";
  comments += "Pix texture packet/decode/fetch: tex=" +
              Hex32(hook_snapshot.last_pix_texture) + " pkt=" +
              Hex32(hook_snapshot.last_pix_packet) + " dec=" +
              Hex32(hook_snapshot.last_pix_decode_base) + " fetch=" +
              Hex32(hook_snapshot.last_pix_fetch_base) + "\n";
  comments += "Active shader handles VS/PS/Decl/IB: " +
              Hex32(hook_snapshot.vertex_shader) + " / " +
              Hex32(hook_snapshot.pixel_shader) + " / " +
              Hex32(hook_snapshot.vertex_declaration) + " / " +
              Hex32(hook_snapshot.index_buffer) + "\n";
  comments += "Direct state RT0/Depth/VS/PS/Tex0: " +
              Hex32(direct_snapshot.targets.color[0]) + " / " +
              Hex32(direct_snapshot.targets.depth) + " / " +
              Hex32(direct_snapshot.shaders.vertex_shader) + " / " +
              Hex32(direct_snapshot.shaders.pixel_shader) + " / " +
              Hex32(direct_snapshot.textures.textures[0]) + "\n";
  comments += "Present pump requests/advances/submitted/completed/callbacks: " +
              std::to_string(timing_snapshot.plume_present_pump_requests) +
              " / " +
              std::to_string(
                  timing_snapshot.plume_present_pump_fable_advances) +
              " / " +
              std::to_string(timing_snapshot.fable_present_submitted) + " / " +
              std::to_string(timing_snapshot.fable_present_completed) + " / " +
              std::to_string(timing_snapshot.fable_swap_callbacks) + "\n";
  return comments;
}

}  // namespace refii::renderer
