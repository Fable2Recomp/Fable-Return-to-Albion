#include "runtime/plume/general/plume_imgui_overlay.h"

#include "renderdoc_debugger.h"
#include "renderer_runtime.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/plume/direct/fable_direct_renderer.h"
#include "runtime/plume/direct/fable_direct_state.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/replacement_health.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <unordered_map>
#include <vector>
#include <rex/logging.h>

#if REFII_RENDERER_HAS_IMGUI
#include <imgui.h>
#include <rex/chrono/clock.h>
#include <rex/ui/imgui_drawer.h>
#include <rex/ui/window.h>
#endif

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#if defined(_WIN64)
#include "plume_imgui_ps.hlsl.dxil.h"
#include "plume_imgui_vs.hlsl.dxil.h"
#endif
#include "plume_imgui_ps.hlsl.spirv.h"
#include "plume_imgui_vs.hlsl.spirv.h"
#endif

namespace refii::renderer {
namespace {

#if REFII_RENDERER_HAS_IMGUI
bool g_logged_imgui_ready = false;
bool g_logged_imgui_waiting_for_gpu_renderer = false;
double g_frame_time_tick_frequency = 0.0;
uint64_t g_last_frame_time_ticks = 0;
rex::ui::Window* g_imgui_window = nullptr;
rex::ui::ImGuiDrawer* g_rexglue_imgui_drawer = nullptr;
bool g_plume_imgui_draw_data_ready = false;
bool g_plume_imgui_building_frame = false;
uint64_t g_rejected_imgui_draw_data = 0;

bool EnsureImGuiContext();

float GetWindowPhysicalToLogicalScale() {
  if (g_imgui_window == nullptr) {
    return 1.0f;
  }
  const uint32_t dpi = g_imgui_window->GetDpi();
  if (dpi == 0) {
    return 1.0f;
  }
  return static_cast<float>(g_imgui_window->GetMediumDpi()) /
         static_cast<float>(dpi);
}

float NextReXGlueImGuiDeltaTime() {
  const uint64_t current_ticks = rex::chrono::Clock::QueryHostTickCount();
  float delta_time = 1.0f / 60.0f;
  if (g_frame_time_tick_frequency > 0.0 && current_ticks >= g_last_frame_time_ticks) {
    delta_time = static_cast<float>(
        static_cast<double>(current_ticks - g_last_frame_time_ticks) /
        g_frame_time_tick_frequency);
  }
  if (!(delta_time > 0.0f)) {
    delta_time = 1.0f / 60.0f;
  }
  g_last_frame_time_ticks = current_ticks;
  return delta_time;
}

const char* TextOrDash(const char* value) {
  return value != nullptr && value[0] != '\0' ? value : "-";
}

void DrawFableDimmerBehindImGui(float display_width, float display_height) {
  if (!RendererImGuiDimFableEnabled()) {
    return;
  }

  const float alpha = RendererImGuiDimFableAlpha();
  if (!(alpha > 0.0f)) {
    return;
  }

  ImDrawList* background_draw_list = ImGui::GetBackgroundDrawList();
  if (background_draw_list == nullptr) {
    return;
  }

  background_draw_list->AddRectFilled(
      ImVec2(0.0f, 0.0f), ImVec2(display_width, display_height),
      ImGui::ColorConvertFloat4ToU32(ImVec4(0.0f, 0.0f, 0.0f, alpha)));
}

#if REFII_RENDERER_HAS_PLUME
constexpr uint32_t kPitchAlignment = 0x100;
constexpr uint32_t kPlacementAlignment = 0x200;

struct ImGuiPushConstants {
  float display_pos[2] = {};
  float display_size[2] = {};
};

struct PlumeImGuiTexture {
  std::unique_ptr<plume::RenderTexture> texture;
  std::unique_ptr<plume::RenderTextureView> texture_view;
  std::unique_ptr<plume::RenderDescriptorSet> descriptor_set;
  std::unique_ptr<plume::RenderBuffer> upload_buffer;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t row_pitch = 0;
  bool upload_pending = false;
};

struct PlumeImGuiRendererState {
  plume::RenderDevice* device = nullptr;
  plume::RenderShaderFormat shader_format = plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderTexture> font_texture;
  std::unique_ptr<plume::RenderTextureView> font_texture_view;
  std::unique_ptr<plume::RenderSampler> font_sampler;
  std::unique_ptr<plume::RenderDescriptorSet> texture_descriptor_set;
  std::unique_ptr<plume::RenderDescriptorSet> sampler_descriptor_set;
  std::unique_ptr<plume::RenderPipelineLayout> pipeline_layout;
  std::unique_ptr<plume::RenderPipeline> pipeline;
  std::unique_ptr<plume::RenderBuffer> font_upload_buffer;
  std::unique_ptr<plume::RenderBuffer> vertex_buffer;
  std::unique_ptr<plume::RenderBuffer> index_buffer;
  std::vector<std::unique_ptr<plume::RenderBuffer>> retired_buffers;
  std::unordered_map<uint64_t, PlumeImGuiTexture> external_textures;
  uint32_t vertex_buffer_size = 0;
  uint32_t index_buffer_size = 0;
  uint32_t font_width = 0;
  uint32_t font_height = 0;
  uint32_t font_row_pitch = 0;
  bool font_upload_pending = false;
  bool logged_font_upload_recorded = false;
  bool logged_visible_renderer = false;
  bool logged_draw_data_shape = false;
};

PlumeImGuiRendererState g_plume_imgui;
std::vector<PlumeImGuiRendererState> g_retired_plume_imgui_states;

uint32_t AlignUp(uint32_t value, uint32_t alignment) {
  return (value + alignment - 1) & ~(alignment - 1);
}

bool CreatePlumeImGuiExternalTexture(plume::RenderDevice* device,
                                     const uint64_t texture_id,
                                     const uint32_t width,
                                     const uint32_t height,
                                     const uint8_t* rgba_pixels,
                                     PlumeImGuiTexture& texture) {
  if (!device || texture_id == 0 || width == 0 || height == 0 ||
      rgba_pixels == nullptr) {
    return false;
  }

  plume::RenderTextureDesc texture_desc = plume::RenderTextureDesc::Texture2D(
      width, height, 1, plume::RenderFormat::R8G8B8A8_UNORM);
  std::unique_ptr<plume::RenderTexture> new_texture =
      device->createTexture(texture_desc);
  if (!new_texture) {
    return false;
  }
  new_texture->setName("Refii Plume ImGui External Texture");

  const uint32_t row_pitch = AlignUp(width * 4, kPitchAlignment);
  const uint32_t slice_pitch = AlignUp(row_pitch * height, kPlacementAlignment);
  std::unique_ptr<plume::RenderBuffer> upload_buffer =
      device->createBuffer(plume::RenderBufferDesc::UploadBuffer(slice_pitch));
  if (!upload_buffer) {
    return false;
  }

  auto* dst = static_cast<uint8_t*>(upload_buffer->map());
  if (!dst) {
    return false;
  }
  for (uint32_t y = 0; y < height; ++y) {
    std::memcpy(dst + row_pitch * y,
                rgba_pixels + static_cast<size_t>(width) * 4U * y,
                width * 4U);
  }
  upload_buffer->unmap();

  std::unique_ptr<plume::RenderTextureView> texture_view =
      new_texture->createTextureView(
          plume::RenderTextureViewDesc::Texture2D(
              plume::RenderFormat::R8G8B8A8_UNORM));
  if (!texture_view) {
    return false;
  }

  plume::RenderDescriptorRange texture_range(
      plume::RenderDescriptorRangeType::TEXTURE, 0, 1);
  std::unique_ptr<plume::RenderDescriptorSet> descriptor_set =
      device->createDescriptorSet(
          plume::RenderDescriptorSetDesc(&texture_range, 1));
  if (!descriptor_set) {
    return false;
  }
  descriptor_set->setTexture(0, new_texture.get(),
                             plume::RenderTextureLayout::SHADER_READ,
                             texture_view.get());

  texture.texture = std::move(new_texture);
  texture.texture_view = std::move(texture_view);
  texture.descriptor_set = std::move(descriptor_set);
  texture.upload_buffer = std::move(upload_buffer);
  texture.width = width;
  texture.height = height;
  texture.row_pitch = row_pitch;
  texture.upload_pending = true;
  return true;
}

plume::RenderDescriptorSet* PlumeImGuiDescriptorSetForTexture(
    const ImTextureID texture_id) {
  if (texture_id == 0 ||
      texture_id == reinterpret_cast<ImTextureID>(
                        g_plume_imgui.font_texture_view.get())) {
    return g_plume_imgui.texture_descriptor_set.get();
  }

  const auto texture_it =
      g_plume_imgui.external_textures.find(static_cast<uint64_t>(texture_id));
  if (texture_it == g_plume_imgui.external_textures.end() ||
      texture_it->second.upload_pending ||
      texture_it->second.descriptor_set == nullptr) {
    return g_plume_imgui.texture_descriptor_set.get();
  }
  return texture_it->second.descriptor_set.get();
}

bool CreateShaderForFormat(plume::RenderDevice* device,
                           plume::RenderShaderFormat shader_format,
                           bool vertex_shader,
                           std::unique_ptr<plume::RenderShader>& shader) {
  switch (shader_format) {
    case plume::RenderShaderFormat::SPIRV:
      shader = device->createShader(
          vertex_shader ? plume_imgui_vsBlobSPIRV : plume_imgui_psBlobSPIRV,
          vertex_shader ? plume_imgui_vsBlobSPIRV_size
                        : plume_imgui_psBlobSPIRV_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return shader != nullptr;
#if defined(_WIN64)
    case plume::RenderShaderFormat::DXIL:
      shader = device->createShader(
          vertex_shader ? plume_imgui_vsBlobDXIL : plume_imgui_psBlobDXIL,
          vertex_shader ? plume_imgui_vsBlobDXIL_size
                        : plume_imgui_psBlobDXIL_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return shader != nullptr;
#endif
    default:
      return false;
  }
}

bool CreateFontTextureUpload(plume::RenderDevice* device) {
  ImGuiIO& io = ImGui::GetIO();
  REXLOG_INFO("Refii Plume ImGui font upload setup starting");
  unsigned char* pixels = nullptr;
  int width = 0;
  int height = 0;
  io.Fonts->GetTexDataAsRGBA32(&pixels, &width, &height);
  if (pixels == nullptr || width <= 0 || height <= 0) {
    REXLOG_WARN("Refii Plume ImGui font atlas did not produce RGBA pixels");
    return false;
  }
  REXLOG_INFO("Refii Plume ImGui font atlas built {}x{}", width, height);

  plume::RenderTextureDesc texture_desc = plume::RenderTextureDesc::Texture2D(
      static_cast<uint32_t>(width), static_cast<uint32_t>(height), 1,
      plume::RenderFormat::R8G8B8A8_UNORM);
  g_plume_imgui.font_texture = device->createTexture(texture_desc);
  if (!g_plume_imgui.font_texture) {
    REXLOG_WARN("Refii Plume ImGui font texture creation failed");
    return false;
  }
  g_plume_imgui.font_texture->setName("Refii Plume ImGui Font");
  REXLOG_INFO("Refii Plume ImGui font texture created");

  const uint32_t row_pitch =
      AlignUp(static_cast<uint32_t>(width) * 4, kPitchAlignment);
  const uint32_t slice_pitch =
      AlignUp(row_pitch * static_cast<uint32_t>(height), kPlacementAlignment);
  g_plume_imgui.font_upload_buffer =
      device->createBuffer(plume::RenderBufferDesc::UploadBuffer(slice_pitch));
  if (!g_plume_imgui.font_upload_buffer) {
    REXLOG_WARN("Refii Plume ImGui font upload buffer creation failed");
    return false;
  }
  REXLOG_INFO("Refii Plume ImGui font upload buffer created size={}", slice_pitch);

  auto* dst = static_cast<uint8_t*>(g_plume_imgui.font_upload_buffer->map());
  if (!dst) {
    REXLOG_WARN("Refii Plume ImGui font upload buffer map failed");
    return false;
  }
  const auto* src = static_cast<const uint8_t*>(pixels);
  for (int y = 0; y < height; ++y) {
    std::memcpy(dst + row_pitch * static_cast<uint32_t>(y),
                src + static_cast<uint32_t>(width) * 4U * y,
                static_cast<uint32_t>(width) * 4U);
  }
  g_plume_imgui.font_upload_buffer->unmap();
  REXLOG_INFO("Refii Plume ImGui font upload buffer populated");

  g_plume_imgui.font_texture_view =
      g_plume_imgui.font_texture->createTextureView(
          plume::RenderTextureViewDesc::Texture2D(
              plume::RenderFormat::R8G8B8A8_UNORM));
  if (!g_plume_imgui.font_texture_view) {
    REXLOG_WARN("Refii Plume ImGui font texture view creation failed");
    return false;
  }
  REXLOG_INFO("Refii Plume ImGui font texture view created");

  if (ImTextureData* font_texture_data = io.Fonts->TexData) {
    font_texture_data->SetTexID(
        reinterpret_cast<ImTextureID>(g_plume_imgui.font_texture_view.get()));
    font_texture_data->SetStatus(ImTextureStatus_OK);
  }

  g_plume_imgui.font_width = static_cast<uint32_t>(width);
  g_plume_imgui.font_height = static_cast<uint32_t>(height);
  g_plume_imgui.font_row_pitch = row_pitch;
  g_plume_imgui.font_upload_pending = true;
  return true;
}

bool CreatePipeline(plume::RenderDevice* device,
                    plume::RenderShaderFormat shader_format) {
  REXLOG_INFO("Refii Plume ImGui pipeline creation starting");
  plume::RenderDescriptorRange texture_range(
      plume::RenderDescriptorRangeType::TEXTURE, 0, 1);
  plume::RenderDescriptorRange sampler_range(
      plume::RenderDescriptorRangeType::SAMPLER, 0, 1);
  plume::RenderDescriptorSetDesc descriptor_sets[2] = {
      plume::RenderDescriptorSetDesc(&texture_range, 1),
      plume::RenderDescriptorSetDesc(&sampler_range, 1),
  };
  plume::RenderPushConstantRange push_constant_range(
      0, 2, 0, sizeof(ImGuiPushConstants),
      plume::RenderShaderStageFlag::VERTEX | plume::RenderShaderStageFlag::PIXEL);
  plume::RenderPipelineLayoutDesc pipeline_layout_desc;
  pipeline_layout_desc.pushConstantRanges = &push_constant_range;
  pipeline_layout_desc.pushConstantRangesCount = 1;
  pipeline_layout_desc.descriptorSetDescs = descriptor_sets;
  pipeline_layout_desc.descriptorSetDescsCount = 2;
  pipeline_layout_desc.allowInputLayout = true;
  g_plume_imgui.pipeline_layout =
      device->createPipelineLayout(pipeline_layout_desc);
  if (!g_plume_imgui.pipeline_layout) {
    REXLOG_WARN("Refii Plume ImGui pipeline layout creation failed");
    return false;
  }
  REXLOG_INFO("Refii Plume ImGui pipeline layout created");

  std::unique_ptr<plume::RenderShader> vertex_shader;
  std::unique_ptr<plume::RenderShader> pixel_shader;
  if (!CreateShaderForFormat(device, shader_format, true, vertex_shader) ||
      !CreateShaderForFormat(device, shader_format, false, pixel_shader)) {
    REXLOG_WARN("Refii Plume ImGui shader creation failed");
    return false;
  }
  REXLOG_INFO("Refii Plume ImGui shaders created");

  plume::RenderInputSlot input_slot(0, sizeof(ImDrawVert));
  plume::RenderInputElement input_elements[3] = {
      plume::RenderInputElement("POSITION", 0, 0, plume::RenderFormat::R32G32_FLOAT,
                                0, offsetof(ImDrawVert, pos)),
      plume::RenderInputElement("TEXCOORD", 0, 1, plume::RenderFormat::R32G32_FLOAT,
                                0, offsetof(ImDrawVert, uv)),
      plume::RenderInputElement("COLOR", 0, 2, plume::RenderFormat::R8G8B8A8_UNORM,
                                0, offsetof(ImDrawVert, col)),
  };

  plume::RenderGraphicsPipelineDesc pipeline_desc;
  pipeline_desc.pipelineLayout = g_plume_imgui.pipeline_layout.get();
  pipeline_desc.vertexShader = vertex_shader.get();
  pipeline_desc.pixelShader = pixel_shader.get();
  pipeline_desc.renderTargetFormat[0] = plume::RenderFormat::B8G8R8A8_UNORM;
  pipeline_desc.renderTargetBlend[0] = plume::RenderBlendDesc::AlphaBlend();
  pipeline_desc.renderTargetCount = 1;
  pipeline_desc.inputSlots = &input_slot;
  pipeline_desc.inputSlotsCount = 1;
  pipeline_desc.inputElements = input_elements;
  pipeline_desc.inputElementsCount = 3;
  pipeline_desc.primitiveTopology = plume::RenderPrimitiveTopology::TRIANGLE_LIST;
  pipeline_desc.cullMode = plume::RenderCullMode::NONE;
  g_plume_imgui.pipeline = device->createGraphicsPipeline(pipeline_desc);
  if (!g_plume_imgui.pipeline) {
    REXLOG_WARN("Refii Plume ImGui graphics pipeline creation failed");
    return false;
  }
  g_plume_imgui.pipeline->setName("Refii Plume ImGui");
  return true;
}

bool HasPlumeImGuiGpuResources(const PlumeImGuiRendererState& state) {
  return state.font_texture || state.font_texture_view || state.font_sampler ||
         state.texture_descriptor_set || state.sampler_descriptor_set ||
         state.pipeline_layout || state.pipeline || state.font_upload_buffer ||
         state.vertex_buffer || state.index_buffer ||
         !state.retired_buffers.empty() || !state.external_textures.empty();
}

void ClearPlumeImGuiGpuState(PlumeImGuiRendererState& state) {
  state.external_textures.clear();
  state.retired_buffers.clear();
  state.index_buffer.reset();
  state.vertex_buffer.reset();
  state.font_upload_buffer.reset();
  state.pipeline.reset();
  state.pipeline_layout.reset();
  state.sampler_descriptor_set.reset();
  state.texture_descriptor_set.reset();
  state.font_sampler.reset();
  state.font_texture_view.reset();
  state.font_texture.reset();

  state.device = nullptr;
  state.shader_format = plume::RenderShaderFormat::UNKNOWN;
  state.vertex_buffer_size = 0;
  state.index_buffer_size = 0;
  state.font_width = 0;
  state.font_height = 0;
  state.font_row_pitch = 0;
  state.font_upload_pending = false;
  state.logged_font_upload_recorded = false;
  state.logged_visible_renderer = false;
  state.logged_draw_data_shape = false;
}

void RetirePlumeImGuiGpuState() {
  if (!HasPlumeImGuiGpuResources(g_plume_imgui)) {
    ClearPlumeImGuiGpuState(g_plume_imgui);
    return;
  }

  g_retired_plume_imgui_states.emplace_back(std::move(g_plume_imgui));
  ClearPlumeImGuiGpuState(g_plume_imgui);
}

void ReplacePlumeImGuiBuffer(
    std::unique_ptr<plume::RenderBuffer>& destination,
    std::unique_ptr<plume::RenderBuffer> replacement) {
  if (destination) {
    g_plume_imgui.retired_buffers.emplace_back(std::move(destination));
  }
  destination = std::move(replacement);
}

bool ValidatePlumeImGuiDrawData(const ImDrawData* draw_data,
                                uint32_t& vertex_bytes,
                                uint32_t& index_bytes) {
  vertex_bytes = 0;
  index_bytes = 0;
  if (draw_data == nullptr || draw_data->CmdListsCount <= 0 ||
      draw_data->TotalVtxCount <= 0 || draw_data->TotalIdxCount <= 0) {
    return false;
  }

  const uint64_t total_vertices =
      static_cast<uint64_t>(draw_data->TotalVtxCount);
  const uint64_t total_indices =
      static_cast<uint64_t>(draw_data->TotalIdxCount);
  if (total_vertices >
          std::numeric_limits<uint32_t>::max() / sizeof(ImDrawVert) ||
      total_indices >
          std::numeric_limits<uint32_t>::max() / sizeof(ImDrawIdx)) {
    return false;
  }

  uint64_t summed_vertices = 0;
  uint64_t summed_indices = 0;
  for (int list_index = 0; list_index < draw_data->CmdListsCount;
       ++list_index) {
    const ImDrawList* draw_list = draw_data->CmdLists[list_index];
    if (draw_list == nullptr || draw_list->VtxBuffer.Size < 0 ||
        draw_list->IdxBuffer.Size < 0 || draw_list->CmdBuffer.Size < 0) {
      return false;
    }

    summed_vertices += static_cast<uint64_t>(draw_list->VtxBuffer.Size);
    summed_indices += static_cast<uint64_t>(draw_list->IdxBuffer.Size);
    if (summed_vertices > total_vertices || summed_indices > total_indices) {
      return false;
    }

    const auto index_count = static_cast<uint32_t>(draw_list->IdxBuffer.Size);
    const auto vertex_count = static_cast<uint32_t>(draw_list->VtxBuffer.Size);
    for (int cmd_index = 0; cmd_index < draw_list->CmdBuffer.Size;
         ++cmd_index) {
      const ImDrawCmd& draw_cmd = draw_list->CmdBuffer[cmd_index];
      if (draw_cmd.UserCallback != nullptr) {
        continue;
      }
      if (draw_cmd.IdxOffset > index_count ||
          draw_cmd.ElemCount > index_count - draw_cmd.IdxOffset ||
          draw_cmd.VtxOffset > vertex_count) {
        return false;
      }
      if (!std::isfinite(draw_cmd.ClipRect.x) ||
          !std::isfinite(draw_cmd.ClipRect.y) ||
          !std::isfinite(draw_cmd.ClipRect.z) ||
          !std::isfinite(draw_cmd.ClipRect.w)) {
        return false;
      }
    }
  }

  if (summed_vertices != total_vertices || summed_indices != total_indices) {
    return false;
  }

  vertex_bytes = static_cast<uint32_t>(total_vertices * sizeof(ImDrawVert));
  index_bytes = static_cast<uint32_t>(total_indices * sizeof(ImDrawIdx));
  return true;
}

void LogRejectedPlumeImGuiDrawData(const char* stage,
                                   const ImDrawData* draw_data) {
  ++g_rejected_imgui_draw_data;
  if (g_rejected_imgui_draw_data <= 8 ||
      (g_rejected_imgui_draw_data % 120) == 0) {
    REXLOG_WARN(
        "Refii Plume ImGui rejected invalid draw data at {} "
        "count={} lists={} vtx={} idx={}",
        stage, g_rejected_imgui_draw_data,
        draw_data ? draw_data->CmdListsCount : 0,
        draw_data ? draw_data->TotalVtxCount : 0,
        draw_data ? draw_data->TotalIdxCount : 0);
  }
}
#endif

bool EnsureImGuiContext() {
  if (g_rexglue_imgui_drawer == nullptr ||
      g_rexglue_imgui_drawer->context() == nullptr) {
    return false;
  }

  ImGui::SetCurrentContext(g_rexglue_imgui_drawer->context());
  ImGuiIO& io = ImGui::GetIO();
  io.BackendFlags |= ImGuiBackendFlags_RendererHasVtxOffset;
  io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
  if (g_frame_time_tick_frequency <= 0.0) {
    g_frame_time_tick_frequency =
        static_cast<double>(rex::chrono::Clock::QueryHostTickFrequency());
    g_last_frame_time_ticks = rex::chrono::Clock::QueryHostTickCount();
  }

  if (!g_logged_imgui_ready) {
    REXLOG_INFO(
        "Refii Plume ImGui renderer is using the existing ReXGlue ImGui "
        "drawer");
    g_logged_imgui_ready = true;
  }
  return true;
}

void ReleasePlumeImGuiInputCapture() {
  if (g_rexglue_imgui_drawer == nullptr ||
      g_rexglue_imgui_drawer->context() == nullptr) {
    return;
  }

  ImGui::SetCurrentContext(g_rexglue_imgui_drawer->context());
  ImGuiIO& io = ImGui::GetIO();
  const bool had_mouse_down = ImGui::IsAnyMouseDown();
  std::memset(io.MouseDown, 0, sizeof(io.MouseDown));
  io.MousePos = ImVec2(-std::numeric_limits<float>::max(),
                       -std::numeric_limits<float>::max());
  io.ClearInputKeys();
  // ReXGlue only captures the mouse for active ImGui button drags. Avoid
  // releasing for WantCapture* alone since that can be true with no matching
  // Window::CaptureMouse request.
  if (had_mouse_down && g_imgui_window != nullptr &&
      g_imgui_window->IsMouseCaptureRequested()) {
    g_imgui_window->ReleaseMouse();
  }
}
#endif

}  // namespace

void AttachPlumeImGuiToReXGlue(rex::ui::Window* window,
                               rex::ui::ImGuiDrawer* drawer) {
#if REFII_RENDERER_HAS_IMGUI
  DetachPlumeImGuiFromReXGlue();
  g_imgui_window = window;
  g_rexglue_imgui_drawer = drawer;
  if (drawer != nullptr) {
    drawer->AddExternalRenderer();
    REXLOG_INFO(
        "Refii Plume ImGui renderer attached to the existing ReXGlue ImGui "
        "drawer");
  }
#else
  (void)window;
  (void)drawer;
#endif
}

void DetachPlumeImGuiFromReXGlue() {
#if REFII_RENDERER_HAS_IMGUI
  ReleasePlumeImGuiInputCapture();
  if (g_rexglue_imgui_drawer != nullptr) {
    g_rexglue_imgui_drawer->RemoveExternalRenderer();
  }
  g_imgui_window = nullptr;
  g_rexglue_imgui_drawer = nullptr;
#endif
}

void DrawPlumeImGuiOverlay(uint32_t width, uint32_t height,
                           uint32_t present_count) {
#if REFII_RENDERER_HAS_IMGUI
  g_plume_imgui_draw_data_ready = false;
#endif
  if (!RendererImGuiEnabled()) {
#if REFII_RENDERER_HAS_IMGUI
    ReleasePlumeImGuiInputCapture();
#endif
    return;
  }

#if REFII_RENDERER_HAS_IMGUI
  if (!EnsureImGuiContext()) {
    return;
  }

  ImGuiIO& io = ImGui::GetIO();
  float display_width = static_cast<float>(width);
  float display_height = static_cast<float>(height);
  if (g_imgui_window != nullptr) {
    const float physical_to_logical = GetWindowPhysicalToLogicalScale();
    const uint32_t physical_width = g_imgui_window->GetActualPhysicalWidth();
    const uint32_t physical_height = g_imgui_window->GetActualPhysicalHeight();
    if (physical_width != 0 && physical_height != 0) {
      display_width = static_cast<float>(physical_width) * physical_to_logical;
      display_height = static_cast<float>(physical_height) * physical_to_logical;
    }
  }
  display_width = std::max(display_width, 1.0f);
  display_height = std::max(display_height, 1.0f);
  io.DisplaySize = ImVec2(display_width, display_height);
  io.DeltaTime = NextReXGlueImGuiDeltaTime();

  auto draw_stats_windows = [&]() {
    DrawFableDimmerBehindImGui(display_width, display_height);

    if (!RendererImGuiStatsVisible()) {
      return;
    }

    const RendererHookObserverSnapshot hook_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererHookTimingSnapshot timing_snapshot =
        GetRendererHookTimingSnapshot();
    const RendererShaderLibrarySnapshot shader_snapshot =
        GetRendererShaderLibrarySnapshot();
    const FableDirectRendererStats direct_stats =
        GetFableDirectRendererStats();
    const RendererReplacementHealthSnapshot replacement_health =
        GetRendererReplacementHealthSnapshot();
    const PlumeOutputSnapshot plume_output = GetPlumeOutputSnapshot();
    const FableDirectStateSnapshot direct_snapshot =
        GetFableDirectStateSnapshot();
    const bool renderdoc_enabled = RenderDocDebuggerEnabled();
    const bool renderdoc_available = RenderDocDebuggerAvailable();

    ImGui::SetNextWindowPos(ImVec2(12.0f, 12.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(360.0f, 164.0f), ImGuiCond_FirstUseEver);
    if (ImGui::Begin("Refii Plume Renderer")) {
      ImGui::Text("Backend: Plume");
      ImGui::Text("Plume frame: %u", plume_output.present_count);
      ImGui::Text("Output: %ux%u", width, height);
      ImGui::Text("Swapchain: %s vsync %s images %u/%u",
                  plume_output.ready ? "ready" : "waiting",
                  plume_output.vsync_enabled ? "on" : "off",
                  plume_output.texture_count,
                  plume_output.configured_texture_count);
      ImGui::Text("RenderDoc: %s / %s",
                  renderdoc_enabled ? "enabled" : "disabled",
                  renderdoc_available ? "available" : "not attached");
      if (ImGui::Button("Capture next Plume frame")) {
        if (!RequestRenderDocFrameCapture()) {
          TriggerRenderDocCapture();
        }
      }
      ImGui::Text("Shaders: %u ids, %u bin / %u hlsl",
                  shader_snapshot.unique_shader_count,
                  shader_snapshot.compiled_binary_count,
                  shader_snapshot.hlsl_source_count);
      ImGui::Text("Replacement: res %s state %s commands %s present %s",
                  replacement_health.replace_resources ? "on" : "off",
                  replacement_health.replace_state ? "on" : "off",
                  replacement_health.replace_draws ? "on" : "off",
                  replacement_health.replace_present ? "on" : "off");
      ImGui::Text("Strict shaders: %s",
                  replacement_health.shader_require_precompiled ? "on" : "off");
      ImGui::Text("Last blocker: %s",
                  RendererReplacementHealthFailureName(replacement_health));
      ImGui::Text("Direct draws: %u indexed %u events %u",
                  direct_stats.draw_count,
                  direct_stats.indexed_draw_count,
                  direct_stats.last_draw_events);
      ImGui::Text("Direct resources: create %u destroy %u upload %u",
                  direct_stats.resource_creations,
                  direct_stats.resource_destroys,
                  direct_stats.resource_uploads);
      ImGui::Text("Direct live: res %u tex %u surf %u vb %u ib %u sh %u",
                  direct_stats.live_resources,
                  direct_stats.live_textures,
                  direct_stats.live_surfaces,
                  direct_stats.live_vertex_buffers,
                  direct_stats.live_index_buffers,
                  direct_stats.live_shaders);
      ImGui::Text("Direct present: 0x%08X %ux%u",
                  direct_stats.last_present_target,
                  direct_stats.last_present_width,
                  direct_stats.last_present_height);
    }
    ImGui::End();

    ImGui::SetNextWindowPos(ImVec2(12.0f, 166.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(360.0f, 112.0f), ImGuiCond_FirstUseEver);
    if (ImGui::Begin("Plume Overlay Status", nullptr,
                     ImGuiWindowFlags_NoCollapse |
                         ImGuiWindowFlags_NoScrollbar |
                         ImGuiWindowFlags_NoSavedSettings)) {
      ImGui::Text("Plume overlay path is live");
      ImGui::Text("Xenia renderer path is suppressed");
      ImGui::Text("Shader root: %s",
                  shader_snapshot.root_exists ? "ready" : "missing");
      ImGui::Text("VS: %u  PS: %u  Combined: %u",
                  shader_snapshot.vertex_shader_count,
                  shader_snapshot.pixel_shader_count,
                  shader_snapshot.combined_shader_count);
      ImGui::Text("Guest blobs: %u  Missing bins: %u",
                  shader_snapshot.guest_blob_count,
                  shader_snapshot.missing_binary_count);
    }
    ImGui::End();

    const auto us_to_ms = [](int64_t us) {
      return static_cast<float>(static_cast<double>(us) / 1000.0);
    };
    struct ProfilerRow {
      const char* name;
      int64_t us;
    };
    const ProfilerRow profiler_rows[] = {
        {"backend", plume_output.last_backend_us},
        {"output", plume_output.last_output_us},
        {"consume", plume_output.last_consume_us},
        {"sync", plume_output.last_sync_us},
        {"readiness", plume_output.last_readiness_us},
        {"wait", plume_output.last_fence_wait_us},
        {"acquire", plume_output.last_acquire_texture_us},
        {"setup", plume_output.last_setup_us},
        {"imgui build", plume_output.last_imgui_build_us},
        {"fable direct", plume_output.last_direct_us},
        {"imgui render", plume_output.last_imgui_render_us},
        {"cmd record", plume_output.last_command_record_us},
        {"submit", plume_output.last_submit_us},
        {"present", plume_output.last_present_us},
    };
    const ProfilerRow* slowest_row = nullptr;
    int64_t positive_total_us = 0;
    for (const ProfilerRow& row : profiler_rows) {
      if (row.us > 0) {
        positive_total_us += row.us;
        if (slowest_row == nullptr || row.us > slowest_row->us) {
          slowest_row = &row;
        }
      }
    }
    const int64_t denominator_us =
        std::max<int64_t>(plume_output.last_frame_total_us, positive_total_us);

    ImGui::SetNextWindowPos(ImVec2(384.0f, 12.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(460.0f, 274.0f), ImGuiCond_FirstUseEver);
    if (ImGui::Begin("Renderer Profiler", nullptr,
                     ImGuiWindowFlags_NoCollapse |
                         ImGuiWindowFlags_NoSavedSettings)) {
      const float total_ms = us_to_ms(plume_output.last_frame_total_us);
      const float fps = total_ms > 0.0f ? 1000.0f / total_ms : 0.0f;
      ImGui::Text("Frame: %.3f ms  %.1f FPS", total_ms, fps);
      ImGui::Text("Pump: %s %u FPS  hook gap %.3f ms",
                  timing_snapshot.plume_present_pump_enabled ? "on" : "off",
                  timing_snapshot.plume_present_pump_target_fps,
                  static_cast<double>(
                      timing_snapshot.last_plume_present_pump_gap_us) /
                      1000.0);
      ImGui::Text("Slowest: %s %.3f ms",
                  slowest_row ? slowest_row->name : "-",
                  slowest_row ? us_to_ms(slowest_row->us) : 0.0f);
      ImGui::Separator();
      for (const ProfilerRow& row : profiler_rows) {
        const float ratio =
            denominator_us > 0
                ? std::clamp(static_cast<float>(
                                 static_cast<double>(row.us) /
                                 static_cast<double>(denominator_us)),
                             0.0f, 1.0f)
                : 0.0f;
        ImGui::Text("%-12s %7.3f ms", row.name, us_to_ms(row.us));
        ImGui::SameLine(190.0f);
        ImGui::ProgressBar(ratio, ImVec2(0.0f, 0.0f), "");
      }
      ImGui::Separator();
      ImGui::Text("Direct draws/events/commands: %u / %u / %u",
                  direct_stats.draw_count,
                  direct_stats.last_draw_events,
                  direct_stats.last_render_commands);
      ImGui::Text("Indexed/resources/uploads: %u / %u / %u",
                  direct_stats.indexed_draw_count,
                  direct_stats.resource_creations,
                  direct_stats.resource_uploads);
      ImGui::Text("Live resources tex/surf/vb/ib/sh: %u / %u / %u / %u / %u",
                  direct_stats.live_textures,
                  direct_stats.live_surfaces,
                  direct_stats.live_vertex_buffers,
                  direct_stats.live_index_buffers,
                  direct_stats.live_shaders);
      ImGui::Text("Pipeline hits/misses: %u / %u",
                  direct_stats.pipeline_cache_hits,
                  direct_stats.pipeline_cache_misses);
    }
    ImGui::End();

    ImGui::SetNextWindowPos(ImVec2(12.0f, 292.0f), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(560.0f, 260.0f), ImGuiCond_FirstUseEver);
    if (ImGui::Begin("Renderer Hook State", nullptr,
                     ImGuiWindowFlags_NoCollapse |
                         ImGuiWindowFlags_NoSavedSettings)) {
      ImGui::Text("Hooks present/draw/indexed: %llu / %llu / %llu",
                  static_cast<unsigned long long>(hook_snapshot.presents),
                  static_cast<unsigned long long>(hook_snapshot.draws),
                  static_cast<unsigned long long>(
                      hook_snapshot.indexed_draws));
      ImGui::Text("Direct draws/indexed/events: %u / %u / %u  source: %s",
                  direct_stats.draw_count,
                  direct_stats.indexed_draw_count,
                  direct_stats.last_draw_events,
                  PlumeFableCommandSourceName(
                      plume_output.last_fable_direct_source));
      ImGui::Text("Direct cmds pending/submitted: %llu / %llu",
                  static_cast<unsigned long long>(
                      direct_snapshot.render_command_count),
                  static_cast<unsigned long long>(
                      direct_snapshot.submitted_render_command_count));
      ImGui::Text("Submit batch cmds/draws: %llu / %llu  submitted draws: %llu",
                  static_cast<unsigned long long>(
                      direct_snapshot.last_command_boundary_pending_commands),
                  static_cast<unsigned long long>(
                      direct_snapshot.last_command_boundary_pending_draws),
                  static_cast<unsigned long long>(
                      direct_snapshot.last_submitted_draw_event_count));
      ImGui::Text("Pipeline hits/misses failed hooks: %u / %u / %u",
                  direct_stats.pipeline_cache_hits,
                  direct_stats.pipeline_cache_misses,
                  direct_stats.failed_hooks);
      ImGui::Text("Resource sync failures: %u  last 0x%08X bytes %u",
                  direct_stats.failed_resource_syncs,
                  direct_stats.last_resource_address,
                  direct_stats.last_resource_size);
      ImGui::Text("Direct target: 0x%08X %ux%u",
                  direct_stats.last_present_target,
                  direct_stats.last_present_width,
                  direct_stats.last_present_height);
      ImGui::Text("Last hook: %s  skipped: %s",
                  TextOrDash(timing_snapshot.last_hook_symbol),
                  TextOrDash(timing_snapshot.last_native_skipped_symbol));
      ImGui::Separator();
      if (ImGui::TreeNode("Advanced Hook Details")) {
        ImGui::Text("Fable present hooks: %llu  Draws: %llu  Indexed: %llu",
                    static_cast<unsigned long long>(hook_snapshot.presents),
                    static_cast<unsigned long long>(hook_snapshot.draws),
                    static_cast<unsigned long long>(
                        hook_snapshot.indexed_draws));
        ImGui::Text("Direct frames: begun %llu ended %llu presented %llu",
                    static_cast<unsigned long long>(
                        direct_stats.frames_begun),
                    static_cast<unsigned long long>(
                        direct_stats.frames_ended),
                    static_cast<unsigned long long>(direct_stats.presents));
        ImGui::Text("Direct state events/device: %llu / 0x%08X",
                    static_cast<unsigned long long>(
                        direct_snapshot.draw_event_count),
                    direct_snapshot.last_device);
        ImGui::Text("Direct state draws/indexed/flushes: %llu / %llu / %llu",
                    static_cast<unsigned long long>(direct_snapshot.draw_count),
                    static_cast<unsigned long long>(
                        direct_snapshot.indexed_draw_count),
                    static_cast<unsigned long long>(
                        direct_snapshot.state_flush_count));
        ImGui::Text("Direct commands pending/submitted/dropped: %llu / %llu / %llu",
                    static_cast<unsigned long long>(
                        direct_snapshot.render_command_count),
                    static_cast<unsigned long long>(
                        direct_snapshot.submitted_render_command_count),
                    static_cast<unsigned long long>(
                        direct_snapshot.dropped_render_commands));
        ImGui::Text("Hook gaps ms all/draw/display/present: %.3f / %.3f / %.3f / %.3f",
                    static_cast<double>(timing_snapshot.last_hook_gap_us) /
                        1000.0,
                    static_cast<double>(timing_snapshot.last_draw_hook_gap_us) /
                        1000.0,
                    static_cast<double>(
                        timing_snapshot.last_display_present_gap_us) /
                        1000.0,
                    static_cast<double>(
                        timing_snapshot.last_plume_present_request_gap_us) /
                        1000.0);
        ImGui::Text("Plume pump: %s %u FPS  frames: %u  requests: %llu  gap: %.3f ms",
                    timing_snapshot.plume_present_pump_enabled ? "on" : "off",
                    timing_snapshot.plume_present_pump_target_fps,
                    plume_output.present_count,
                    static_cast<unsigned long long>(
                        timing_snapshot.plume_present_pump_requests),
                    static_cast<double>(
                        timing_snapshot.last_plume_present_pump_gap_us) /
                        1000.0);
        ImGui::Text("Plume ms total/marshal/acquire/present/context: %.3f / %.3f / %.3f / %.3f / %.3f",
                    static_cast<double>(plume_output.last_frame_total_us) /
                        1000.0,
                    static_cast<double>(plume_output.last_marshal_us) / 1000.0,
                    static_cast<double>(plume_output.last_acquire_us) / 1000.0,
                    static_cast<double>(plume_output.last_present_us) / 1000.0,
                    static_cast<double>(plume_output.last_fence_wait_us) /
                        1000.0);
        ImGui::Text("Fable direct commands: %u  source: %s",
                    plume_output.last_fable_render_commands,
                    PlumeFableCommandSourceName(
                        plume_output.last_fable_direct_source));
        ImGui::Text("Hook counts all/draw/display/present: %llu / %llu / %llu / %llu",
                    static_cast<unsigned long long>(timing_snapshot.hook_enters),
                    static_cast<unsigned long long>(
                        timing_snapshot.draw_hook_enters),
                    static_cast<unsigned long long>(
                        timing_snapshot.display_present_hooks),
                    static_cast<unsigned long long>(
                        timing_snapshot.plume_present_requests));
        ImGui::Text("Renderer setup: %llu  renderer/owner/game: 0x%08X / 0x%08X / 0x%08X",
                    static_cast<unsigned long long>(
                        timing_snapshot.renderer_setup_hooks),
                    timing_snapshot.fable_renderer,
                    timing_snapshot.fable_renderer_owner,
                    timing_snapshot.fable_game);
        ImGui::Text("Display swap: %s  Native skips: %llu",
                    timing_snapshot.seen_display_swap_hook ? "seen" : "waiting",
                    static_cast<unsigned long long>(
                        timing_snapshot.native_skipped_hooks));
        ImGui::Text("Last hook: %s", TextOrDash(timing_snapshot.last_hook_symbol));
        ImGui::Text("Last display: %s  Plume: %s/%s",
                    TextOrDash(timing_snapshot.last_display_present_symbol),
                    TextOrDash(timing_snapshot.last_plume_present_symbol),
                    TextOrDash(timing_snapshot.last_plume_present_reason));
        ImGui::Text("Last skipped native: %s",
                    TextOrDash(timing_snapshot.last_native_skipped_symbol));
        ImGui::Separator();
      ImGui::Text("Passes: %llu  State flushes: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.render_passes),
                  static_cast<unsigned long long>(
                      hook_snapshot.state_flushes));
      ImGui::Text("Dispatches: %llu  Targets: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.state_dispatches),
                  static_cast<unsigned long long>(
                      hook_snapshot.state_dispatch_targets));
      ImGui::Text("Queue callbacks: %llu  Targets: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.render_queue_callbacks),
                  static_cast<unsigned long long>(
                      hook_snapshot.render_queue_callback_targets));
      ImGui::Text("Command writes: %llu  Submits: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.command_writes),
                  static_cast<unsigned long long>(
                      hook_snapshot.command_submits));
      ImGui::Text("PM4 words: %llu  Type0: %llu  Type3: %llu",
                  static_cast<unsigned long long>(hook_snapshot.command_words),
                  static_cast<unsigned long long>(
                      hook_snapshot.pm4_type0_packets),
                  static_cast<unsigned long long>(
                      hook_snapshot.pm4_type3_packets));
      ImGui::Text("PM4 draw: %llu  IB: %llu  Const: %llu  Swap: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.pm4_draw_packets),
                  static_cast<unsigned long long>(
                      hook_snapshot.pm4_indirect_packets),
                  static_cast<unsigned long long>(
                      hook_snapshot.pm4_constant_packets),
                  static_cast<unsigned long long>(
                      hook_snapshot.pm4_swap_packets));
      ImGui::Text("Shader uploads: %llu  VS/PS nz: %u / %u",
                  static_cast<unsigned long long>(
                      hook_snapshot.shader_constant_uploads),
                  hook_snapshot.vertex_shader_constant_nonzero_dwords,
                  hook_snapshot.pixel_shader_constant_nonzero_dwords);
      ImGui::Text("Fetch uploads: %llu  nz: %u",
                  static_cast<unsigned long long>(
                      hook_snapshot.fetch_constant_uploads),
                  hook_snapshot.fetch_constant_nonzero_dwords);
      ImGui::Text("Last PM4: 0x%08X  type: %u  op: 0x%02X",
                  hook_snapshot.last_command_first_word,
                  hook_snapshot.last_command_packet_type,
                  hook_snapshot.last_command_type3_opcode);
      ImGui::Text("Display source: %u  base 0x%08X  obj 0x%08X  %ux%u fmt %u",
                  hook_snapshot.display_present_source,
                  hook_snapshot.display_present_texture_base,
                  hook_snapshot.display_present_surface_object,
                  hook_snapshot.display_present_width,
                  hook_snapshot.display_present_height,
                  hook_snapshot.display_present_format);
      ImGui::Text("Display reg: 0x%04X = 0x%08X",
                  hook_snapshot.display_present_register,
                  hook_snapshot.display_present_register_value);
      if (hook_snapshot.scaler.valid) {
        ImGui::Text("Scaler: 0x%08X src %u,%u-%u,%u out %ux%u",
                    hook_snapshot.scaler.parameter_pointer,
                    hook_snapshot.scaler.source_left,
                    hook_snapshot.scaler.source_top,
                    hook_snapshot.scaler.source_right,
                    hook_snapshot.scaler.source_bottom,
                    hook_snapshot.scaler.output_width,
                    hook_snapshot.scaler.output_height);
      }
      ImGui::Text("Last flush LR: 0x%08X",
                  hook_snapshot.last_state_flush_lr);
      ImGui::Text("Clears: %llu  State changes: %llu",
                  static_cast<unsigned long long>(hook_snapshot.clears),
                  static_cast<unsigned long long>(
                      hook_snapshot.state_changes));
      ImGui::Text("Resources create/destroy: %llu / %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.resource_creates),
                  static_cast<unsigned long long>(
                      hook_snapshot.resource_destroys));
      ImGui::Text("Live: %llu  Tex: %llu  Surf: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.live_resources),
                  static_cast<unsigned long long>(hook_snapshot.live_textures),
                  static_cast<unsigned long long>(hook_snapshot.live_surfaces));
      ImGui::Text("VB: %llu  IB: %llu  Shaders: %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.live_vertex_buffers),
                  static_cast<unsigned long long>(
                      hook_snapshot.live_index_buffers),
                  static_cast<unsigned long long>(hook_snapshot.live_shaders));
      ImGui::Text("Locks/unlocks: %llu / %llu",
                  static_cast<unsigned long long>(
                      hook_snapshot.resource_locks),
                  static_cast<unsigned long long>(
                      hook_snapshot.resource_unlocks));
      ImGui::Separator();
      ImGui::Text("RT0: 0x%08X  RT1: 0x%08X",
                  hook_snapshot.render_targets[0],
                  hook_snapshot.render_targets[1]);
      ImGui::Text("RT2: 0x%08X  RT3: 0x%08X",
                  hook_snapshot.render_targets[2],
                  hook_snapshot.render_targets[3]);
      ImGui::Text("Depth: 0x%08X  Clear flags: 0x%08X",
                  hook_snapshot.depth_stencil, hook_snapshot.clear_flags);
      ImGui::Text("Viewport: 0x%08X  Scissor: 0x%08X",
                  hook_snapshot.viewport_pointer,
                  hook_snapshot.scissor_pointer);
      if (hook_snapshot.viewport.valid) {
        ImGui::Text("VP: %.0f %.0f %.0fx%.0f  Z %.2f..%.2f",
                    hook_snapshot.viewport.x, hook_snapshot.viewport.y,
                    hook_snapshot.viewport.width,
                    hook_snapshot.viewport.height,
                    hook_snapshot.viewport.min_depth,
                    hook_snapshot.viewport.max_depth);
      }
      if (hook_snapshot.scissor.valid) {
        ImGui::Text("SC: %d %d %d %d", hook_snapshot.scissor.left,
                    hook_snapshot.scissor.top, hook_snapshot.scissor.right,
                    hook_snapshot.scissor.bottom);
      }
      ImGui::Separator();
      ImGui::Text("VD: 0x%08X  VS: 0x%08X  PS: 0x%08X",
                  hook_snapshot.vertex_declaration,
                  hook_snapshot.vertex_shader, hook_snapshot.pixel_shader);
      ImGui::Text("IB: 0x%08X  VB0: 0x%08X  stride %u",
                  hook_snapshot.index_buffer,
                  hook_snapshot.streams[0].buffer,
                  hook_snapshot.streams[0].stride);
      ImGui::Text("Tex0: 0x%08X  Tex1: 0x%08X",
                  hook_snapshot.textures[0], hook_snapshot.textures[1]);
      ImGui::Text("Last resource: 0x%08X size %u meta %u",
                  hook_snapshot.last_resource_address,
                  hook_snapshot.last_resource_size,
                  hook_snapshot.last_resource_stride_or_format);
      if (hook_snapshot.last_shader_guest_hash != 0) {
        ImGui::Text("Last shader hash: 0x%016llX",
                    static_cast<unsigned long long>(
                        hook_snapshot.last_shader_guest_hash));
      }
      ImGui::Separator();
      ImGui::Text("Last: %s prim %u base %u start %u count %u",
                  hook_snapshot.last_draw.indexed ? "indexed" : "direct",
                  hook_snapshot.last_draw.primitive_type,
                  hook_snapshot.last_draw.base_vertex,
                  hook_snapshot.last_draw.start,
                  hook_snapshot.last_draw.primitive_count);
      ImGui::TreePop();
      }
    }
    ImGui::End();
  };

  g_plume_imgui_building_frame = true;
  const ImDrawData* draw_data =
      g_rexglue_imgui_drawer->DrawForExternalRenderer(
          display_width, display_height, io.DeltaTime, draw_stats_windows);
  g_plume_imgui_building_frame = false;
#if REFII_RENDERER_HAS_PLUME
  uint32_t vertex_bytes = 0;
  uint32_t index_bytes = 0;
  if (!ValidatePlumeImGuiDrawData(draw_data, vertex_bytes, index_bytes)) {
    if (draw_data != nullptr && (draw_data->CmdListsCount > 0 ||
                                 draw_data->TotalVtxCount > 0 ||
                                 draw_data->TotalIdxCount > 0)) {
      LogRejectedPlumeImGuiDrawData("build", draw_data);
    }
    ReleasePlumeImGuiInputCapture();
    return;
  }
  g_plume_imgui_draw_data_ready = true;
#else
  g_plume_imgui_draw_data_ready =
      draw_data != nullptr && draw_data->CmdListsCount > 0 &&
      draw_data->TotalVtxCount > 0 && draw_data->TotalIdxCount > 0;
#endif
  if (draw_data != nullptr && draw_data->CmdListsCount > 0 &&
      !g_logged_imgui_waiting_for_gpu_renderer) {
    REXLOG_INFO(
        "Refii Plume ImGui produced ReXGlue draw data for the Plume overlay "
        "renderer");
    g_logged_imgui_waiting_for_gpu_renderer = true;
  }
#else
  (void)width;
  (void)height;
  (void)present_count;
#endif
}

#if REFII_RENDERER_HAS_PLUME
bool EnsurePlumeImGuiRenderer(plume::RenderDevice* device,
                              plume::RenderShaderFormat shader_format) {
#if REFII_RENDERER_HAS_IMGUI
  if (!RendererImGuiEnabled()) {
    return false;
  }
  if (!device || !EnsureImGuiContext()) {
    return false;
  }

  if (g_plume_imgui.pipeline &&
      g_plume_imgui.shader_format == shader_format) {
    g_plume_imgui.device = device;
    return true;
  }

  RetirePlumeImGuiGpuState();
  g_plume_imgui.shader_format = shader_format;
  g_plume_imgui.device = device;
  if (!CreateFontTextureUpload(device) ||
      !CreatePipeline(device, shader_format)) {
    REXLOG_WARN("Refii Plume ImGui renderer could not initialize GPU resources");
    ClearPlumeImGuiGpuState(g_plume_imgui);
    return false;
  }

  plume::RenderSamplerDesc sampler_desc;
  sampler_desc.addressU = plume::RenderTextureAddressMode::CLAMP;
  sampler_desc.addressV = plume::RenderTextureAddressMode::CLAMP;
  sampler_desc.addressW = plume::RenderTextureAddressMode::CLAMP;
  sampler_desc.anisotropyEnabled = false;
  g_plume_imgui.font_sampler = device->createSampler(sampler_desc);
  if (!g_plume_imgui.font_sampler) {
    return false;
  }

  plume::RenderDescriptorRange texture_range(
      plume::RenderDescriptorRangeType::TEXTURE, 0, 1);
  plume::RenderDescriptorRange sampler_range(
      plume::RenderDescriptorRangeType::SAMPLER, 0, 1);
  g_plume_imgui.texture_descriptor_set = device->createDescriptorSet(
      plume::RenderDescriptorSetDesc(&texture_range, 1));
  g_plume_imgui.sampler_descriptor_set = device->createDescriptorSet(
      plume::RenderDescriptorSetDesc(&sampler_range, 1));
  if (!g_plume_imgui.texture_descriptor_set ||
      !g_plume_imgui.sampler_descriptor_set) {
    return false;
  }

  g_plume_imgui.texture_descriptor_set->setTexture(
      0, g_plume_imgui.font_texture.get(),
      plume::RenderTextureLayout::SHADER_READ,
      g_plume_imgui.font_texture_view.get());
  g_plume_imgui.sampler_descriptor_set->setSampler(
      0, g_plume_imgui.font_sampler.get());
  REXLOG_INFO("Refii Plume ImGui GPU renderer initialized");
  return true;
#else
  (void)device;
  (void)shader_format;
  return false;
#endif
}

uint64_t RegisterPlumeImGuiRgbaTexture(const void* key, const uint32_t width,
                                       const uint32_t height,
                                       const uint8_t* rgba_pixels) {
#if REFII_RENDERER_HAS_IMGUI
  if (key == nullptr || width == 0 || height == 0 || rgba_pixels == nullptr ||
      !g_plume_imgui_building_frame ||
      g_plume_imgui.device == nullptr || g_plume_imgui.pipeline == nullptr ||
      g_plume_imgui.font_sampler == nullptr) {
    return 0;
  }

  const uint64_t texture_id =
      static_cast<uint64_t>(reinterpret_cast<std::uintptr_t>(key));
  PlumeImGuiTexture& texture = g_plume_imgui.external_textures[texture_id];
  if (texture.texture != nullptr && texture.texture_view != nullptr &&
      texture.descriptor_set != nullptr && texture.width == width &&
      texture.height == height) {
    return texture.upload_pending ? 0 : texture_id;
  }

  if (!CreatePlumeImGuiExternalTexture(g_plume_imgui.device, texture_id, width,
                                       height, rgba_pixels, texture)) {
    g_plume_imgui.external_textures.erase(texture_id);
    return 0;
  }
  return 0;
#else
  (void)key;
  (void)width;
  (void)height;
  (void)rgba_pixels;
  return 0;
#endif
}

void RecordPlumeImGuiUploads(plume::RenderCommandList* command_list) {
#if REFII_RENDERER_HAS_IMGUI
  if (!command_list) {
    return;
  }

  bool recorded_upload = false;

  if (g_plume_imgui.font_upload_pending && g_plume_imgui.font_texture &&
      g_plume_imgui.font_upload_buffer) {
    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(g_plume_imgui.font_texture.get(),
                                    plume::RenderTextureLayout::COPY_DEST));
    command_list->copyTextureRegion(
        plume::RenderTextureCopyLocation::Subresource(
            g_plume_imgui.font_texture.get(), 0),
        plume::RenderTextureCopyLocation::PlacedFootprint(
            g_plume_imgui.font_upload_buffer.get(),
            plume::RenderFormat::R8G8B8A8_UNORM, g_plume_imgui.font_width,
            g_plume_imgui.font_height, 1, g_plume_imgui.font_row_pitch / 4));
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(g_plume_imgui.font_texture.get(),
                                    plume::RenderTextureLayout::SHADER_READ));
    g_plume_imgui.font_upload_pending = false;
    recorded_upload = true;
  }

  for (auto& texture_pair : g_plume_imgui.external_textures) {
    PlumeImGuiTexture& texture = texture_pair.second;
    if (!texture.upload_pending || !texture.texture || !texture.upload_buffer) {
      continue;
    }

    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(texture.texture.get(),
                                    plume::RenderTextureLayout::COPY_DEST));
    command_list->copyTextureRegion(
        plume::RenderTextureCopyLocation::Subresource(texture.texture.get(), 0),
        plume::RenderTextureCopyLocation::PlacedFootprint(
            texture.upload_buffer.get(), plume::RenderFormat::R8G8B8A8_UNORM,
            texture.width, texture.height, 1, texture.row_pitch / 4));
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(texture.texture.get(),
                                    plume::RenderTextureLayout::SHADER_READ));
    texture.upload_pending = false;
    recorded_upload = true;
  }

  if (!recorded_upload) {
    return;
  }

  if (!g_plume_imgui.logged_font_upload_recorded) {
    REXLOG_INFO("Refii Plume ImGui texture upload recorded into frame command list");
    g_plume_imgui.logged_font_upload_recorded = true;
  }
#else
  (void)command_list;
#endif
}

void RenderPlumeImGuiOverlay(plume::RenderDevice* device,
                             plume::RenderCommandList* command_list) {
#if REFII_RENDERER_HAS_IMGUI
  if (!RendererImGuiEnabled() || !g_plume_imgui_draw_data_ready || !device ||
      !command_list ||
      !g_plume_imgui.pipeline) {
    ReleasePlumeImGuiInputCapture();
    g_plume_imgui_draw_data_ready = false;
    return;
  }

  ImDrawData* draw_data = ImGui::GetDrawData();
  uint32_t vertex_bytes = 0;
  uint32_t index_bytes = 0;
  if (!ValidatePlumeImGuiDrawData(draw_data, vertex_bytes, index_bytes)) {
    LogRejectedPlumeImGuiDrawData("render", draw_data);
    ReleasePlumeImGuiInputCapture();
    g_plume_imgui_draw_data_ready = false;
    return;
  }

  if (!g_plume_imgui.logged_draw_data_shape) {
    REXLOG_INFO(
        "Refii Plume ImGui draw data display={}x{} pos={}x{} scale={}x{} "
        "lists={} vertices={} indices={} vertex_bytes={} index_bytes={}",
        draw_data->DisplaySize.x, draw_data->DisplaySize.y,
        draw_data->DisplayPos.x, draw_data->DisplayPos.y,
        draw_data->FramebufferScale.x, draw_data->FramebufferScale.y,
        draw_data->CmdListsCount, draw_data->TotalVtxCount,
        draw_data->TotalIdxCount, vertex_bytes, index_bytes);
    g_plume_imgui.logged_draw_data_shape = true;
  }

  if (!g_plume_imgui.vertex_buffer ||
      g_plume_imgui.vertex_buffer_size < vertex_bytes) {
    const uint32_t new_vertex_buffer_size = AlignUp(vertex_bytes, 65536);
    std::unique_ptr<plume::RenderBuffer> new_vertex_buffer = device->createBuffer(
        plume::RenderBufferDesc::VertexBuffer(new_vertex_buffer_size,
                                              plume::RenderHeapType::UPLOAD));
    if (new_vertex_buffer) {
      g_plume_imgui.vertex_buffer_size = new_vertex_buffer_size;
      ReplacePlumeImGuiBuffer(g_plume_imgui.vertex_buffer,
                              std::move(new_vertex_buffer));
    }
  }
  if (!g_plume_imgui.index_buffer ||
      g_plume_imgui.index_buffer_size < index_bytes) {
    const uint32_t new_index_buffer_size = AlignUp(index_bytes, 65536);
    std::unique_ptr<plume::RenderBuffer> new_index_buffer = device->createBuffer(
        plume::RenderBufferDesc::IndexBuffer(new_index_buffer_size,
                                             plume::RenderHeapType::UPLOAD));
    if (new_index_buffer) {
      g_plume_imgui.index_buffer_size = new_index_buffer_size;
      ReplacePlumeImGuiBuffer(g_plume_imgui.index_buffer,
                              std::move(new_index_buffer));
    }
  }
  if (!g_plume_imgui.vertex_buffer || !g_plume_imgui.index_buffer) {
    ReleasePlumeImGuiInputCapture();
    g_plume_imgui_draw_data_ready = false;
    return;
  }

  auto* vertex_dst = static_cast<uint8_t*>(g_plume_imgui.vertex_buffer->map());
  auto* index_dst = static_cast<uint8_t*>(g_plume_imgui.index_buffer->map());
  if (!vertex_dst || !index_dst) {
    if (vertex_dst) {
      g_plume_imgui.vertex_buffer->unmap();
    }
    if (index_dst) {
      g_plume_imgui.index_buffer->unmap();
    }
    ReleasePlumeImGuiInputCapture();
    g_plume_imgui_draw_data_ready = false;
    return;
  }

  for (int list_index = 0; list_index < draw_data->CmdListsCount;
       ++list_index) {
    const ImDrawList* draw_list = draw_data->CmdLists[list_index];
    const size_t vertex_list_bytes =
        static_cast<size_t>(draw_list->VtxBuffer.Size) * sizeof(ImDrawVert);
    const size_t index_list_bytes =
        static_cast<size_t>(draw_list->IdxBuffer.Size) * sizeof(ImDrawIdx);
    std::memcpy(vertex_dst, draw_list->VtxBuffer.Data, vertex_list_bytes);
    std::memcpy(index_dst, draw_list->IdxBuffer.Data, index_list_bytes);
    vertex_dst += vertex_list_bytes;
    index_dst += index_list_bytes;
  }
  g_plume_imgui.vertex_buffer->unmap();
  g_plume_imgui.index_buffer->unmap();

  command_list->setGraphicsPipelineLayout(g_plume_imgui.pipeline_layout.get());
  command_list->setPipeline(g_plume_imgui.pipeline.get());
  plume::RenderDescriptorSet* current_texture_descriptor_set =
      g_plume_imgui.texture_descriptor_set.get();
  command_list->setGraphicsDescriptorSet(current_texture_descriptor_set, 0);
  command_list->setGraphicsDescriptorSet(
      g_plume_imgui.sampler_descriptor_set.get(), 1);

  const plume::RenderViewport viewport(
      draw_data->DisplayPos.x, draw_data->DisplayPos.y,
      draw_data->DisplaySize.x * draw_data->FramebufferScale.x,
      draw_data->DisplaySize.y * draw_data->FramebufferScale.y);
  command_list->setViewports(viewport);

  ImGuiPushConstants push_constants;
  push_constants.display_pos[0] = draw_data->DisplayPos.x;
  push_constants.display_pos[1] = draw_data->DisplayPos.y;
  push_constants.display_size[0] = draw_data->DisplaySize.x;
  push_constants.display_size[1] = draw_data->DisplaySize.y;
  command_list->setGraphicsPushConstants(0, &push_constants);

  const plume::RenderVertexBufferView vertex_buffer_view(
      g_plume_imgui.vertex_buffer->at(0), vertex_bytes);
  const plume::RenderInputSlot input_slot(0, sizeof(ImDrawVert));
  command_list->setVertexBuffers(0, &vertex_buffer_view, 1, &input_slot);

  const plume::RenderIndexBufferView index_buffer_view(
      g_plume_imgui.index_buffer->at(0), index_bytes,
      sizeof(ImDrawIdx) == 2 ? plume::RenderFormat::R16_UINT
                             : plume::RenderFormat::R32_UINT);
  command_list->setIndexBuffer(&index_buffer_view);

  int global_vertex_offset = 0;
  int global_index_offset = 0;
  const ImVec2 clip_offset = draw_data->DisplayPos;
  const ImVec2 clip_scale = draw_data->FramebufferScale;
  const int fb_width =
      static_cast<int>(draw_data->DisplaySize.x * draw_data->FramebufferScale.x);
  const int fb_height =
      static_cast<int>(draw_data->DisplaySize.y * draw_data->FramebufferScale.y);

  for (int list_index = 0; list_index < draw_data->CmdListsCount;
       ++list_index) {
    const ImDrawList* draw_list = draw_data->CmdLists[list_index];
    for (int cmd_index = 0; cmd_index < draw_list->CmdBuffer.Size;
         ++cmd_index) {
      const ImDrawCmd& draw_cmd = draw_list->CmdBuffer[cmd_index];
      if (draw_cmd.UserCallback != nullptr) {
        if (draw_cmd.UserCallback == ImDrawCallback_ResetRenderState) {
          command_list->setPipeline(g_plume_imgui.pipeline.get());
        }
        continue;
      }

      const ImVec2 clip_min((draw_cmd.ClipRect.x - clip_offset.x) * clip_scale.x,
                            (draw_cmd.ClipRect.y - clip_offset.y) * clip_scale.y);
      const ImVec2 clip_max((draw_cmd.ClipRect.z - clip_offset.x) * clip_scale.x,
                            (draw_cmd.ClipRect.w - clip_offset.y) * clip_scale.y);
      if (clip_max.x <= clip_min.x || clip_max.y <= clip_min.y) {
        continue;
      }

      plume::RenderRect scissor(
          std::max(0, static_cast<int>(clip_min.x)),
          std::max(0, static_cast<int>(clip_min.y)),
          std::min(fb_width, static_cast<int>(clip_max.x)),
          std::min(fb_height, static_cast<int>(clip_max.y)));
      if (scissor.right <= scissor.left || scissor.bottom <= scissor.top) {
        continue;
      }
      plume::RenderDescriptorSet* texture_descriptor_set =
          PlumeImGuiDescriptorSetForTexture(draw_cmd.GetTexID());
      if (texture_descriptor_set != current_texture_descriptor_set) {
        command_list->setGraphicsDescriptorSet(texture_descriptor_set, 0);
        current_texture_descriptor_set = texture_descriptor_set;
      }
      command_list->setScissors(scissor);
      command_list->drawIndexedInstanced(
          draw_cmd.ElemCount, 1, draw_cmd.IdxOffset + global_index_offset,
          static_cast<int32_t>(draw_cmd.VtxOffset + global_vertex_offset), 0);
    }
    global_index_offset += draw_list->IdxBuffer.Size;
    global_vertex_offset += draw_list->VtxBuffer.Size;
  }

  if (!g_plume_imgui.logged_visible_renderer) {
    REXLOG_INFO("Refii Plume ImGui overlay submitted through Plume");
    g_plume_imgui.logged_visible_renderer = true;
  }
  g_plume_imgui_draw_data_ready = false;
#else
  (void)device;
  (void)command_list;
#endif
}
#endif

void ShutdownPlumeImGuiOverlay() {
#if REFII_RENDERER_HAS_IMGUI
  ReleasePlumeImGuiInputCapture();
#if REFII_RENDERER_HAS_PLUME
  g_retired_plume_imgui_states.clear();
  ClearPlumeImGuiGpuState(g_plume_imgui);
#endif
#endif
}

}  // namespace refii::renderer
