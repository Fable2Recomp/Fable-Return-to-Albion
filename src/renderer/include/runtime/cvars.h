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
  kMetal,
};

bool RendererHooksEnabled();
bool RendererHookTracingEnabled();
bool RendererResourceTracingEnabled();
bool RendererDrawTracingEnabled();
bool RendererVerboseTracingEnabled();
bool RendererReplaceResourcesEnabled();
bool RendererReplaceStateEnabled();
bool RendererReplaceDrawsEnabled();
bool RendererReplacePresentEnabled();
bool RendererSuppressNativeCommandSubmissionEnabled();
bool RendererShaderRequirePrecompiled();
bool RendererImGuiEnabled();
bool RendererImGuiStatsVisible();
bool RendererImGuiDimFableEnabled();
float RendererImGuiDimFableAlpha();
bool RendererDebugTestTriangleEnabled();
bool RendererPlumeVsyncEnabled();
uint32_t RendererPlumeSwapchainImageCount();
bool RendererPlumePresentPumpEnabled();
bool RendererPlumePresentPumpFableBookkeepingEnabled();
uint32_t RendererPlumePresentTargetFps();
bool RenderDocDebuggerEnabled();
bool RenderDocOverlayEnabled();
bool RenderDocReplayLaunchEnabled();
bool RequestRenderDocFrameCapture();
bool ConsumeRenderDocCaptureRequest();
uint32_t RenderDocCaptureFrameIndex();
std::string RenderDocCaptureDrawTarget();

RendererBackend GetRendererBackend();
RendererGraphicsApi GetRendererGraphicsApi();
bool PlumeRendererBackendEnabled();
bool PlumeVulkanBackendEnabled();
bool PlumeD3D12BackendEnabled();
bool PlumeMetalBackendEnabled();
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
std::string ConfiguredRendererMetalShaderRoot();
std::string ConfiguredRendererRenderDocRuntimePath();
std::string ConfiguredRendererRenderDocCapturePath();

} // namespace detail
} // namespace refii::renderer
