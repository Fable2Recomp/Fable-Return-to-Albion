#pragma once

#include <cstdint>
#include <string>

namespace refii::renderer {

enum class RendererBackend {
  kNative,
  kPlume,
  kDisabled,
};

enum class RendererGraphicsApi {
  kVulkan,
  kD3D12,
};

bool RendererHooksEnabled();
bool RendererHookTracingEnabled();
bool RendererResourceTracingEnabled();
bool RendererDrawTracingEnabled();
bool RendererVerboseTracingEnabled();
bool RendererNativeFallbackAllowed();
bool RendererImGuiEnabled();
bool RendererImGuiStatsVisible();
bool RendererImGuiDimFableEnabled();
float RendererImGuiDimFableAlpha();
bool RendererDebugTestTriangleEnabled();
bool RendererQuadListStrip0132Order();
bool RendererPlumeVsyncEnabled();
uint32_t RendererPlumeSwapchainImageCount();
bool RendererPlumePresentPumpEnabled();
uint32_t RendererPlumePresentTargetFps();
bool RenderDocDebuggerEnabled();
bool RenderDocOverlayEnabled();
bool RenderDocReplayLaunchEnabled();
bool RequestRenderDocFrameCapture();
bool ConsumeRenderDocCaptureRequest();

RendererBackend GetRendererBackend();
RendererGraphicsApi GetRendererGraphicsApi();
bool PlumeRendererBackendEnabled();
bool PlumeVulkanBackendEnabled();
bool PlumeD3D12BackendEnabled();
bool RendererVulkanValidationEnabled();
bool RendererD3D12DebugLayerEnabled();
int RendererGuestVideoWidth();
int RendererGuestVideoHeight();
int RendererOutputWidth();
int RendererOutputHeight();
int RendererInternalScale();

inline namespace detail {

std::string ConfiguredRendererBackend();
std::string ConfiguredRendererGraphicsApi();
std::string ConfiguredRendererVulkanShaderRoot();
std::string ConfiguredRendererD3D12ShaderRoot();
std::string ConfiguredRendererRenderDocRuntimePath();

}  // namespace detail
}  // namespace refii::renderer
