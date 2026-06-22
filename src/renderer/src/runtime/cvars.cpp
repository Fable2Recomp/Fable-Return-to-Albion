#include "runtime/cvars.h"

#include "renderer_runtime.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <string>

#include <rex/cvar.h>
#include <rex/graphics/flags.h>
#include <rex/graphics/video_mode_util.h>

namespace refii::renderer {
namespace {

REXCVAR_DEFINE_STRING(renderer_backend, "xenia", "Renderer",
                      "Renderer backend: plume for the Refii RHI path, xenia "
                      "for the original Fable 2 renderer.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_plume_api, "vulkan", "Renderer",
                      "Plume graphics API: vulkan, d3d12, or metal.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_scaling, "1x", "Renderer",
                      "Fable 2 internal viewport supersampling scale: "
                      "1x, 2x, 3x, or 4x.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_trace_hooks, false, "Renderer",
                    "Log renderer hook dispatch and suppression decisions.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_trace_resources, false, "Renderer",
                    "Log renderer resource create/lock/unlock/destroy flow.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_trace_draws, false, "Renderer",
                    "Log draw, state, and presentation calls.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_trace_verbose, false, "Renderer",
    "Log high-volume renderer details while tracing is enabled.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_replace_resources, true, "Renderer/Replacement",
    "Enable Plume-backed resource creation, upload, and lifetime "
    "replacement while the Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_replace_state, true, "Renderer/Replacement",
    "Enable Plume-owned Fable render-state replacement while the "
    "Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_replace_draws, true, "Renderer/Replacement",
                    "Enable Plume-backed Fable draw command execution while "
                    "the Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_replace_present, true, "Renderer/Replacement",
                    "Enable Plume-backed Fable present/backbuffer replacement "
                    "while the Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_suppress_native_command_submission, true,
                    "Renderer/Replacement",
                    "Suppress Fable's native indirect command-buffer "
                    "submission hooks after Plume advances their guest-visible "
                    "bookkeeping.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_shader_require_precompiled, true, "Renderer/Replacement",
    "Require a precompiled shader artifact before a Fable shader "
    "can participate in Plume draw replacement.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_imgui_enable, false, "Renderer/ImGui",
                    "Enable the ReXGlue ImGui interface on the Plume renderer path.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_imgui_show_stats, false, "Renderer/ImGui",
    "Show Refii renderer stats windows when ImGui is enabled.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_imgui_dim_fable, false, "Renderer/ImGui",
    "Draw a dimmer between Fable 2 and the Plume ImGui overlay.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_DOUBLE(renderer_imgui_dim_alpha, 0.55, "Renderer/ImGui",
                      "Opacity for the Plume ImGui dimmer over Fable 2.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload)
    .range(0.0, 0.95);

REXCVAR_DEFINE_BOOL(renderer_debug_test_triangle, false, "Renderer/Debug",
                    "Draw a Plume-native diagnostic triangle over the game "
                    "output to validate swapchain and pipeline rendering.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_plume_vsync, false, "Renderer/Plume",
                    "Enable Plume swapchain vsync. Disable to let the "
                    "replacement renderer present as fast as Fable submits "
                    "frames.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_UINT32(renderer_plume_swapchain_images, 3, "Renderer/Plume",
                      "Requested Plume swapchain image count. Higher values "
                      "can reduce acquire stalls while replacing the original "
                      "renderer.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_plume_present_pump, true, "Renderer/Plume",
                    "Drive Plume presents from a renderer-owned FPS pump "
                    "instead of waiting for Fable's suppressed display-swap "
                    "hook cadence.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_plume_present_pump_fable_bookkeeping, true, "Renderer/Plume",
    "Let the Plume present pump advance Fable's guest-visible display "
    "submitted/completed counters when suppressed display-swap hooks stop "
    "arriving.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_UINT32(renderer_plume_present_fps, 60, "Renderer/Plume",
                      "Target frame rate for the Plume present pump.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_vulkan_shader_root,
                      "assets/unpacked/shaders/vulkan", "Renderer/Vulkan",
                      "Directory containing compiled Vulkan shader artifacts.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_vulkan_validation, false, "Renderer/Vulkan",
                    "Enable Vulkan validation for the Plume renderer backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_d3d12_shader_root, "assets/unpacked/shaders/D3D12",
    "Renderer/D3D12",
    "Directory containing compiled DirectX 12 shader artifacts.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_d3d12_debug_layer, false, "Renderer/D3D12",
    "Enable the DirectX 12 debug layer for the Plume renderer backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_metal_shader_root,
                      "assets/unpacked/shaders/metal", "Renderer/Metal",
                      "Directory containing compiled Metal shader artifacts.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_enable, false, "Renderer/RenderDoc",
    "Enable RenderDoc in-application capture integration for the "
    "Refii renderer.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_renderdoc_capture_next_frame, false,
                    "Renderer/RenderDoc",
                    "Capture the next Plume-rendered frame with RenderDoc.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_UINT32(
    renderer_renderdoc_capture_frame, 0, "Renderer/RenderDoc",
    "Capture a specific Plume frame with RenderDoc. 0 disables frame-index "
    "capture.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_renderdoc_capture_draw_target, "", "Renderer/RenderDoc",
    "Capture the first Plume frame whose queued direct draw matches this "
    "target. Supported values: treebillboard, particlematerial, "
    "overdrawvisualise, postprocessingdownscale4, postprocessinggaussblur, "
    "screenfacingsprite, decal, texturedquadpreservestencil.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_overlay, true, "Renderer/RenderDoc",
    "Show RenderDoc's capture overlay when the RenderDoc API is available.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_launch_replay, false, "Renderer/RenderDoc",
    "Ask RenderDoc to launch or focus its replay UI when a capture "
    "is triggered from Refii.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_renderdoc_runtime_path, "", "Renderer/RenderDoc",
                      "Optional RenderDoc runtime library path. Empty searches "
                      "common system and local librenderdoc locations.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_renderdoc_capture_path, "renderdoc/captures",
    "Renderer/RenderDoc",
    "Directory for RenderDoc captures. Relative paths resolve beside refii.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

std::string NormalizedRendererBackend() {
  std::string backend = REXCVAR_GET(renderer_backend);
  std::transform(
      backend.begin(), backend.end(), backend.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return backend;
}

std::string NormalizedRendererGraphicsApi() {
  std::string api = REXCVAR_GET(renderer_plume_api);
  std::transform(api.begin(), api.end(), api.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });
  return api;
}

std::string NormalizedRendererScaling() {
  std::string scaling = REXCVAR_GET(renderer_scaling);
  scaling.erase(
      std::remove_if(scaling.begin(), scaling.end(),
                     [](unsigned char ch) { return std::isspace(ch) != 0; }),
      scaling.end());
  std::transform(
      scaling.begin(), scaling.end(), scaling.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return scaling;
}

} // namespace

bool RendererHooksEnabled() {
  return GetRendererBackend() == RendererBackend::kPlume;
}

bool RendererHookTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_hooks);
}

bool RendererResourceTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_resources);
}

bool RendererDrawTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_draws);
}

bool RendererVerboseTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_verbose);
}

bool RendererReplaceResourcesEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_replace_resources);
}

bool RendererReplaceStateEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_replace_state);
}

bool RendererReplaceDrawsEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_replace_draws);
}

bool RendererReplacePresentEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_replace_present);
}

bool RendererSuppressNativeCommandSubmissionEnabled() {
  return RendererReplaceDrawsEnabled() &&
         REXCVAR_GET(renderer_suppress_native_command_submission);
}

bool RendererShaderRequirePrecompiled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_shader_require_precompiled);
}

bool RendererImGuiEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_imgui_enable);
}

bool RendererImGuiStatsVisible() {
  return RendererImGuiEnabled() && REXCVAR_GET(renderer_imgui_show_stats);
}

bool RendererImGuiDimFableEnabled() {
  return RendererImGuiEnabled() && REXCVAR_GET(renderer_imgui_dim_fable);
}

float RendererImGuiDimFableAlpha() {
  return static_cast<float>(
      std::clamp(REXCVAR_GET(renderer_imgui_dim_alpha), 0.0, 0.95));
}

bool RendererDebugTestTriangleEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_debug_test_triangle);
}

bool RendererPlumeVsyncEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_plume_vsync);
}

uint32_t RendererPlumeSwapchainImageCount() {
  return std::clamp<uint32_t>(REXCVAR_GET(renderer_plume_swapchain_images), 2,
                              4);
}

bool RendererPlumePresentPumpEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_plume_present_pump);
}

bool RendererPlumePresentPumpFableBookkeepingEnabled() {
  return RendererPlumePresentPumpEnabled() &&
         REXCVAR_GET(renderer_plume_present_pump_fable_bookkeeping);
}

uint32_t RendererPlumePresentTargetFps() {
  return std::clamp<uint32_t>(REXCVAR_GET(renderer_plume_present_fps), 1, 240);
}

bool RenderDocDebuggerEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_renderdoc_enable);
}

bool RenderDocOverlayEnabled() {
  return RenderDocDebuggerEnabled() && REXCVAR_GET(renderer_renderdoc_overlay);
}

bool RenderDocReplayLaunchEnabled() {
  return RenderDocDebuggerEnabled() &&
         REXCVAR_GET(renderer_renderdoc_launch_replay);
}

bool RequestRenderDocFrameCapture() {
  if (!RenderDocDebuggerEnabled()) {
    return false;
  }

  REXCVAR_SET(renderer_renderdoc_capture_next_frame, true);
  return true;
}

bool ConsumeRenderDocCaptureRequest() {
  if (!RenderDocDebuggerEnabled() ||
      !REXCVAR_GET(renderer_renderdoc_capture_next_frame)) {
    return false;
  }

  REXCVAR_SET(renderer_renderdoc_capture_next_frame, false);
  return true;
}

uint32_t RenderDocCaptureFrameIndex() {
  return RenderDocDebuggerEnabled()
             ? REXCVAR_GET(renderer_renderdoc_capture_frame)
             : 0u;
}

std::string RenderDocCaptureDrawTarget() {
  if (!RenderDocDebuggerEnabled()) {
    return {};
  }

  std::string target = REXCVAR_GET(renderer_renderdoc_capture_draw_target);
  target.erase(
      std::remove_if(target.begin(), target.end(),
                     [](unsigned char ch) { return std::isspace(ch) != 0; }),
      target.end());
  std::transform(
      target.begin(), target.end(), target.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return target;
}

RendererBackend GetRendererBackend() {
  const std::string backend = NormalizedRendererBackend();
  if (backend == "plume") {
    return RendererBackend::kPlume;
  }
  if (backend == "xenia" || backend == "native") {
    return RendererBackend::kNative;
  }
  if (backend == "disabled") {
    return RendererBackend::kDisabled;
  }
  return RendererBackend::kNative;
}

RendererGraphicsApi GetRendererGraphicsApi() {
  const std::string api = NormalizedRendererGraphicsApi();
  if (api == "d3d12" || api == "directx12" || api == "directx 12") {
    return RendererGraphicsApi::kD3D12;
  }
  if (api == "metal" || api == "mtl") {
    return RendererGraphicsApi::kMetal;
  }
  return RendererGraphicsApi::kVulkan;
}

bool PlumeRendererBackendEnabled() {
  return RendererHooksEnabled() &&
         GetRendererBackend() == RendererBackend::kPlume && PlumeAvailable();
}

bool PlumeVulkanBackendEnabled() {
  return PlumeRendererBackendEnabled() &&
         GetRendererGraphicsApi() == RendererGraphicsApi::kVulkan;
}

bool PlumeD3D12BackendEnabled() {
  return PlumeRendererBackendEnabled() &&
         GetRendererGraphicsApi() == RendererGraphicsApi::kD3D12;
}

bool PlumeMetalBackendEnabled() {
  return PlumeRendererBackendEnabled() &&
         GetRendererGraphicsApi() == RendererGraphicsApi::kMetal;
}

bool RendererVulkanValidationEnabled() {
  return PlumeVulkanBackendEnabled() && REXCVAR_GET(renderer_vulkan_validation);
}

bool RendererD3D12DebugLayerEnabled() {
  return PlumeD3D12BackendEnabled() && REXCVAR_GET(renderer_d3d12_debug_layer);
}

int RendererGuestVideoWidth() {
  int32_t preset_width = 0;
  int32_t preset_height = 0;
  if (rex::graphics::video_mode_util::TryGetResolutionPresetFromCVar(
          preset_width, preset_height)) {
    return std::max(1, preset_width);
  }
  return std::max(1, REXCVAR_GET(video_mode_width));
}

int RendererGuestVideoHeight() {
  int32_t preset_width = 0;
  int32_t preset_height = 0;
  if (rex::graphics::video_mode_util::TryGetResolutionPresetFromCVar(
          preset_width, preset_height)) {
    return std::max(1, preset_height);
  }
  return std::max(1, REXCVAR_GET(video_mode_height));
}

int RendererOutputWidth() { return RendererGuestVideoWidth(); }

int RendererOutputHeight() { return RendererGuestVideoHeight(); }

int RendererInternalScale() {
  const std::string scaling = NormalizedRendererScaling();
  if (scaling == "4" || scaling == "4x") {
    return 4;
  }
  if (scaling == "3" || scaling == "3x") {
    return 3;
  }
  if (scaling == "2" || scaling == "2x") {
    return 2;
  }
  return 1;
}

inline namespace detail {

std::string ConfiguredRendererBackend() {
  return REXCVAR_GET(renderer_backend);
}

std::string ConfiguredRendererGraphicsApi() {
  return REXCVAR_GET(renderer_plume_api);
}

std::string ConfiguredRendererVulkanShaderRoot() {
  return REXCVAR_GET(renderer_vulkan_shader_root);
}

std::string ConfiguredRendererD3D12ShaderRoot() {
  return REXCVAR_GET(renderer_d3d12_shader_root);
}

std::string ConfiguredRendererMetalShaderRoot() {
  return REXCVAR_GET(renderer_metal_shader_root);
}

std::string ConfiguredRendererRenderDocRuntimePath() {
  return REXCVAR_GET(renderer_renderdoc_runtime_path);
}

std::string ConfiguredRendererRenderDocCapturePath() {
  return REXCVAR_GET(renderer_renderdoc_capture_path);
}

} // namespace detail
} // namespace refii::renderer
