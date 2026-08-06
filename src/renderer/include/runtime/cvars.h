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
bool RendererGtlExecutorShadowEnabled();
bool RendererGtlSubmissionEnabled();
bool RendererGtlStatedPresentSourceEnabled();
bool RendererGtlAuthoritativeSubmissionEnabled();
bool RendererPlumeGuestDrivenPresentEnabled();
bool RendererGtlPrivateRectComposeEnabled();
bool RendererGtlVideoDrawsEnabled();
bool RendererBacklogSupersessionEnabled();
bool RendererRecordBacklogCapEnabled();
bool RendererGtlResolveClearsEnabled();
bool RendererGtlClearsAuthoritativeEnabled();
bool RendererGtlDepthResolvesEnabled();
bool RendererGtlLiveCullAuthorityEnabled();
bool RendererBinkRealtimeAudioClockEnabled();
bool RendererBinkChannelCaptureGateEnabled();
bool RendererUnlockDirtyRangesEnabled();
bool RendererLockReturnsDecodedViewEnabled();
bool RendererPhysicalViewReadsEnabled();
bool RendererGtlContentAuthorityEnabled();
bool RendererGtlConstantSelfReadEnabled();
bool RendererGtlExecutorEnabled();
bool RendererVerboseTracingEnabled();
bool RendererHookTelemetryEnabled();
bool RendererObserverDrawExitEnabled();
bool RendererObserverResourceExitEnabled();
bool RendererRootOnlyHooksEnabled();
bool RendererDeepObserverHooksEnabled();
bool RendererSceneStreamRepairEnabled();
bool RendererReplaceResourcesEnabled();
bool RendererReplaceStateEnabled();
bool RendererReplaceDrawsEnabled();
bool RendererReplacePresentEnabled();
bool RendererPlumeOwnsCommandSubmissionEnabled();
bool RendererPlumePacketQueueReplayEnabled();
bool RendererShaderRequirePrecompiled();
bool RendererImGuiEnabled();
bool RendererImGuiStatsVisible();
bool RendererImGuiDimFableEnabled();
float RendererImGuiDimFableAlpha();
bool RendererDebugTestTriangleEnabled();
bool RendererPlumeVsyncEnabled();
uint32_t RendererPlumeSwapchainImageCount();
// Guest render extent. These FALL BACK to the launcher/toml resolution
// (RendererOutputWidth/Height -> video_mode_width/height), so the guest renders
// at whatever the launcher is set to; the renderer_native_* cvars are an
// explicit override for decoupling render size from window size.
uint32_t RendererNativeWidthOverride();
uint32_t RendererNativeHeightOverride();
// True when the renderer_native_* cvars supplied the value rather than the
// launcher resolution. Diagnostic only (the boot log says which).
bool RendererNativeWidthOverrideIsExplicit();
// Pass the REQUESTED extent through verbatim -- ask for 1920x1080 and the guest
// renders exactly that. The boot path (sub_82B6F6C0) takes our values untouched
// into both the presentation parameters and qword_834A5FE8; only the runtime
// resize path rounds, and it does not run at boot. Kept as a single shared
// helper so every host path that states an extent agrees (run 544: two paths
// disagreed and the guest allocated its scene RT set twice).
uint32_t RendererGuestAlignedRenderWidth(uint32_t width);
uint32_t RendererGuestAlignedRenderHeight(uint32_t height);
bool RendererPlumePresentPumpEnabled();
bool RendererPlumePresentPumpFableBookkeepingEnabled();
uint32_t RendererPlumePresentTargetFps();
bool RendererPresentFastWakeEnabled();
bool RendererPresentUIThreadPipelineEnabled();
bool RenderDocDebuggerEnabled();
bool RenderDocNativeArmEnabled();
void FreezeRendererBackendCache();
bool RenderDocOverlayEnabled();
bool RenderDocReplayLaunchEnabled();
bool RenderDocCaptureOnErrorEnabled();
bool RequestRenderDocFrameCapture();
bool RequestRenderDocFrameCaptureWithReason(const char* reason);
bool RenderDocTriggerCaptureRequestPending();
void ClearRenderDocTriggerCaptureRequest();
bool ConsumeRenderDocTriggerCaptureRequest();
bool RenderDocCaptureRequestPending();
void ClearRenderDocCaptureRequest();
uint32_t RenderDocCaptureFrameIndex();
uint32_t RenderDocCaptureDrawIndex();
std::string RenderDocCaptureDrawTarget();
std::string RenderDocCapturePassName();
std::string RenderDocCaptureReason();
void ClearRenderDocCaptureReason();

RendererBackend GetRendererBackend();
RendererGraphicsApi GetRendererGraphicsApi();
bool PlumeRendererBackendEnabled();
int RendererGuestVideoWidth();
int RendererGuestVideoHeight();
int RendererOutputWidth();
int RendererOutputHeight();
int RendererInternalScale();

inline namespace detail {

std::string ConfiguredRendererVulkanShaderRoot();
std::string ConfiguredRendererD3D12ShaderRoot();
std::string ConfiguredRendererMetalShaderRoot();
std::string ConfiguredRendererRenderDocRuntimePath();

} // namespace detail
} // namespace refii::renderer
