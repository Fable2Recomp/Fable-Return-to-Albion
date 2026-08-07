#include "runtime/plume/general/plume_diagnostics.h"

#include "runtime/cvars.h"

#include <cstddef>
#include <cstring>
#include <memory>

#include <rex/logging.h>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>

#if defined(_WIN64)
#include "plume_debug_triangle_ps.hlsl.dxil.h"
#include "plume_debug_triangle_vs.hlsl.dxil.h"
#endif
#include "plume_debug_triangle_ps.hlsl.spirv.h"
#include "plume_debug_triangle_vs.hlsl.spirv.h"
#endif

namespace refii::renderer {
namespace {

#if REFII_RENDERER_HAS_PLUME
struct DiagnosticVertex {
  float position[3];
  float color[4];
};

struct PlumeDiagnosticState {
  plume::RenderShaderFormat shader_format = plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderPipelineLayout> pipeline_layout;
  std::unique_ptr<plume::RenderPipeline> pipeline;
  std::unique_ptr<plume::RenderBuffer> vertex_buffer;
  bool logged_ready = false;
  bool logged_submitted = false;
};

PlumeDiagnosticState g_diagnostics;

bool CreateShaderForFormat(plume::RenderDevice* device,
                           plume::RenderShaderFormat shader_format,
                           bool vertex_shader,
                           std::unique_ptr<plume::RenderShader>& shader) {
  switch (shader_format) {
    case plume::RenderShaderFormat::SPIRV:
      shader = device->createShader(
          vertex_shader ? plume_debug_triangle_vsBlobSPIRV
                        : plume_debug_triangle_psBlobSPIRV,
          vertex_shader ? plume_debug_triangle_vsBlobSPIRV_size
                        : plume_debug_triangle_psBlobSPIRV_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return shader != nullptr;
#if defined(_WIN64)
    case plume::RenderShaderFormat::DXIL:
      shader = device->createShader(
          vertex_shader ? plume_debug_triangle_vsBlobDXIL
                        : plume_debug_triangle_psBlobDXIL,
          vertex_shader ? plume_debug_triangle_vsBlobDXIL_size
                        : plume_debug_triangle_psBlobDXIL_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return shader != nullptr;
#endif
    default:
      return false;
  }
}

bool EnsureDiagnostics(plume::RenderDevice* device,
                       plume::RenderShaderFormat shader_format) {
  if (!device) {
    return false;
  }
  if (g_diagnostics.pipeline && g_diagnostics.vertex_buffer &&
      g_diagnostics.shader_format == shader_format) {
    return true;
  }

  g_diagnostics = PlumeDiagnosticState{};
  g_diagnostics.shader_format = shader_format;

  plume::RenderPipelineLayoutDesc layout_desc;
  layout_desc.allowInputLayout = true;
  g_diagnostics.pipeline_layout = device->createPipelineLayout(layout_desc);
  if (!g_diagnostics.pipeline_layout) {
    REXLOG_WARN("Refii plume diagnostic triangle could not create layout");
    return false;
  }

  std::unique_ptr<plume::RenderShader> vertex_shader;
  std::unique_ptr<plume::RenderShader> pixel_shader;
  if (!CreateShaderForFormat(device, shader_format, true, vertex_shader) ||
      !CreateShaderForFormat(device, shader_format, false, pixel_shader)) {
    REXLOG_WARN("Refii plume diagnostic triangle could not create shaders");
    return false;
  }

  plume::RenderInputSlot input_slot(0, sizeof(DiagnosticVertex));
  plume::RenderInputElement input_elements[2] = {
      plume::RenderInputElement("POSITION", 0, 0,
                                plume::RenderFormat::R32G32B32_FLOAT, 0,
                                offsetof(DiagnosticVertex, position)),
      plume::RenderInputElement("COLOR", 0, 1,
                                plume::RenderFormat::R32G32B32A32_FLOAT, 0,
                                offsetof(DiagnosticVertex, color)),
  };

  plume::RenderGraphicsPipelineDesc pipeline_desc;
  pipeline_desc.pipelineLayout = g_diagnostics.pipeline_layout.get();
  pipeline_desc.vertexShader = vertex_shader.get();
  pipeline_desc.pixelShader = pixel_shader.get();
  pipeline_desc.renderTargetFormat[0] = plume::RenderFormat::B8G8R8A8_UNORM;
  pipeline_desc.renderTargetBlend[0] = plume::RenderBlendDesc::Copy();
  pipeline_desc.renderTargetCount = 1;
  pipeline_desc.inputSlots = &input_slot;
  pipeline_desc.inputSlotsCount = 1;
  pipeline_desc.inputElements = input_elements;
  pipeline_desc.inputElementsCount = 2;
  pipeline_desc.primitiveTopology = plume::RenderPrimitiveTopology::TRIANGLE_LIST;
  pipeline_desc.cullMode = plume::RenderCullMode::NONE;
  g_diagnostics.pipeline = device->createGraphicsPipeline(pipeline_desc);
  if (!g_diagnostics.pipeline) {
    REXLOG_WARN("Refii plume diagnostic triangle could not create pipeline");
    return false;
  }
  g_diagnostics.pipeline->setName("Refii Plume Diagnostic Triangle");

  constexpr DiagnosticVertex vertices[] = {
      {{-0.85f, -0.70f, 0.0f}, {1.0f, 0.1f, 0.1f, 1.0f}},
      {{-0.35f, -0.70f, 0.0f}, {0.1f, 1.0f, 0.1f, 1.0f}},
      {{-0.60f, -0.25f, 0.0f}, {0.1f, 0.4f, 1.0f, 1.0f}},
  };
  g_diagnostics.vertex_buffer = device->createBuffer(
      plume::RenderBufferDesc::VertexBuffer(sizeof(vertices),
                                            plume::RenderHeapType::UPLOAD));
  if (!g_diagnostics.vertex_buffer) {
    REXLOG_WARN("Refii plume diagnostic triangle could not create vertex buffer");
    return false;
  }

  void* dst = g_diagnostics.vertex_buffer->map();
  if (!dst) {
    REXLOG_WARN("Refii plume diagnostic triangle could not map vertex buffer");
    return false;
  }
  std::memcpy(dst, vertices, sizeof(vertices));
  plume::RenderRange written_range(0, sizeof(vertices));
  g_diagnostics.vertex_buffer->unmap(0, &written_range);

  REXLOG_INFO("Refii plume diagnostic triangle renderer initialized");
  g_diagnostics.logged_ready = true;
  return true;
}
#endif

}  // namespace

#if REFII_RENDERER_HAS_PLUME
void RenderPlumeDiagnostics(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            plume::RenderShaderFormat shader_format,
                            uint32_t frame_index, uint32_t width,
                            uint32_t height) {
  (void)frame_index;
  if (!RendererDebugTestTriangleEnabled() || !command_list ||
      !EnsureDiagnostics(device, shader_format)) {
    return;
  }

  command_list->setGraphicsPipelineLayout(g_diagnostics.pipeline_layout.get());
  command_list->setPipeline(g_diagnostics.pipeline.get());
  command_list->setViewports(
      plume::RenderViewport(0.0f, 0.0f, float(width), float(height)));
  command_list->setScissors(plume::RenderRect(0, 0, width, height));

  const plume::RenderVertexBufferView vertex_buffer_view(
      g_diagnostics.vertex_buffer->at(0), sizeof(DiagnosticVertex) * 3);
  const plume::RenderInputSlot input_slot(0, sizeof(DiagnosticVertex));
  command_list->setVertexBuffers(0, &vertex_buffer_view, 1, &input_slot);
  command_list->drawInstanced(3, 1, 0, 0);

  if (!g_diagnostics.logged_submitted) {
    REXLOG_INFO("Refii plume diagnostic triangle submitted");
    g_diagnostics.logged_submitted = true;
  }
}

void ShutdownPlumeDiagnostics() {
  g_diagnostics = PlumeDiagnosticState{};
}
#endif

}  // namespace refii::renderer
