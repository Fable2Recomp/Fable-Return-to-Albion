#include "runtime/plume/general/plume_backend.h"

#include "renderer_runtime.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/plume/general/plume_api_bridge.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"
#include "runtime/plume/general/plume_diagnostics.h"
#include "runtime/plume/general/plume_imgui_overlay.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
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
#include <rex/ui/window.h>
#include <tracy/Tracy.hpp>

#if !defined(REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE)
#define REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE 0
#endif
#if !defined(REFII_SDL3_WINDOW_ENABLED)
#define REFII_SDL3_WINDOW_ENABLED 0
#endif
#if REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE
#include <X11/Xlib.h>
#endif
#if REFII_SDL3_WINDOW_ENABLED && defined(__linux__)
#include <rex/ui/window_sdl.h>
#endif

#if defined(_WIN64)
#endif

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {
namespace {

using PlumeClock = std::chrono::steady_clock;

double PlumeElapsedMs(PlumeClock::time_point begin,
                      PlumeClock::time_point end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}

int64_t PlumeElapsedUs(PlumeClock::time_point begin,
                       PlumeClock::time_point end) {
  return std::chrono::duration_cast<std::chrono::microseconds>(end - begin)
      .count();
}

bool HasFableDirectFrameContent(const FableDirectStateSnapshot& snapshot) {
  const bool staged_draw_work =
      std::any_of(snapshot.staged_draw_source_counts.begin(),
                  snapshot.staged_draw_source_counts.end(),
                  [](uint32_t count) { return count != 0; });
  return snapshot.submitted_render_command_count != 0 ||
         snapshot.draw_event_count != 0 ||
         snapshot.direct_event_count != 0 ||
         snapshot.draw_count != 0 ||
         snapshot.indexed_draw_count != 0 ||
         snapshot.staged_clear_command_count != 0 ||
         snapshot.staged_resolve_command_count != 0 ||
         staged_draw_work;
}

bool QueuedCommandsContainRenderDocDrawTarget(const std::string& target) {
  if (target.empty()) {
    return false;
  }

  constexpr size_t kMaxRenderDocTargetCommandProbe = 4096;
  return FableDirectQueuedRenderDocTargetPresent(
      target, kMaxRenderDocTargetCommandProbe);
}

bool IsRenderDocRootCoverageCaptureTarget(std::string_view target) {
  return target == "allroots" || target == "rootcoverage" ||
         target == "hookroots";
}

std::string NormalizeRenderDocTargetToken(std::string_view target) {
  std::string normalized;
  normalized.reserve(target.size());
  for (const unsigned char ch : target) {
    if (std::isalnum(ch) != 0) {
      normalized.push_back(static_cast<char>(std::tolower(ch)));
    }
  }
  return normalized;
}

std::string SanitizeRenderDocTagComponent(std::string_view value) {
  std::string sanitized;
  sanitized.reserve(value.size());
  bool pending_separator = false;
  for (const unsigned char ch : value) {
    if (std::isalnum(ch) != 0) {
      if (pending_separator && !sanitized.empty()) {
        sanitized.push_back('_');
      }
      sanitized.push_back(static_cast<char>(std::tolower(ch)));
      pending_separator = false;
    } else {
      pending_separator = true;
    }
  }
  return sanitized;
}

bool ParseRenderDocHookTargetAddress(std::string_view token,
                                     uint32_t& address) {
  if (token.empty()) {
    return false;
  }
  if (token.size() > 2u && token[0] == '0' &&
      (token[1] == 'x' || token[1] == 'X')) {
    token.remove_prefix(2u);
  }
  if (token.empty() || token.size() > 8u) {
    return false;
  }

  uint32_t parsed = 0;
  for (const unsigned char ch : token) {
    const int value =
        ch >= '0' && ch <= '9'
            ? ch - '0'
            : (ch >= 'a' && ch <= 'f'
                   ? ch - 'a' + 10
                   : (ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1));
    if (value < 0) {
      return false;
    }
    parsed = (parsed << 4u) | static_cast<uint32_t>(value);
  }
  address = parsed;
  return true;
}

bool TryGetRenderDocHookRootCaptureToken(std::string_view target,
                                         std::string_view& token) {
  constexpr std::string_view root_prefix = "root:";
  constexpr std::string_view hook_prefix = "hook:";
  if (target.substr(0, root_prefix.size()) == root_prefix) {
    token = target.substr(root_prefix.size());
    return !token.empty();
  }
  if (target.substr(0, hook_prefix.size()) == hook_prefix) {
    token = target.substr(hook_prefix.size());
    return !token.empty();
  }
  return false;
}

bool RenderDocHookRootTargetActive(std::string_view token) {
  const std::string normalized_token = NormalizeRenderDocTargetToken(token);
  if (normalized_token.empty()) {
    return false;
  }

  uint32_t requested_address = 0;
  const bool has_requested_address =
      ParseRenderDocHookTargetAddress(token, requested_address);
  const RendererHookStatsSnapshots hook_stats = GetRendererHookStatsSnapshots();
  for (const RendererHookStatsSnapshot& stats : hook_stats) {
    if (stats.hit_count == 0 && stats.guest_passthrough_count == 0) {
      continue;
    }
    if (has_requested_address && stats.fable_address == requested_address) {
      return true;
    }
    if (NormalizeRenderDocTargetToken(stats.name) == normalized_token) {
      return true;
    }
  }
  return false;
}

void ConfigurePlumeBackendTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii Plume Frame Total Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii Plume Acquire Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii Plume Resource Sync Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii Plume Readiness Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xEAB308);
    TracyPlotConfig("Refii Plume Command Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii Plume ImGui Build Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii Plume ImGui Render Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x3B82F6);
    TracyPlotConfig("Refii Plume Command Record Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x8B5CF6);
    TracyPlotConfig("Refii Plume Submit Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xA855F7);
    TracyPlotConfig("Refii Plume Present Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii Plume Fence Wait Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii Plume Draw Events",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii Plume Recorded Draws",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x10B981);
    TracyPlotConfig("Refii Plume Missing Resources",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xEF4444);
    TracyPlotConfig("Refii Plume Missing Pipelines",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF43F5E);
    TracyPlotConfig("Refii Plume Filtered Draws",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
    TracyPlotConfig("Refii Plume Texture Uploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii Plume Pending Texture Uploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB923C);
    TracyPlotConfig("Refii Plume Pix Video Events",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.DrawPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x10B981);
    TracyPlotConfig("Refii.CommandPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.ClearPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.ResolvePackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii.PresentBlitPackets",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii.ResourceEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x0EA5E9);
    TracyPlotConfig("Refii.ClearResourceEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x7DD3FC);
    TracyPlotConfig("Refii.ResolveResourceEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFDBA74);
    TracyPlotConfig("Refii.SurfaceDestinationEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x34D399);
    TracyPlotConfig("Refii.PresentBlitEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA3E635);
    TracyPlotConfig("Refii.PassResourceUsages",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x4ADE80);
    TracyPlotConfig("Refii.PassClassified",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.PassDependencies",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
    TracyPlotConfig("Refii.ResourceTransitions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB923C);
    TracyPlotConfig("Refii.FrameResourceLifetimes",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.MaterialRecords",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF472B6);
    TracyPlotConfig("Refii.MeshRecords",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xC084FC);
    TracyPlotConfig("Refii.AssetUsages",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii.RenderGraphNodes",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.RenderGraphEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
    TracyPlotConfig("Refii.RenderGraphExecutionSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii.RenderGraphBarriers",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB923C);
    TracyPlotConfig("Refii.RenderGraphPhases",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii.RenderGraphRuntimeExecutions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.RenderGraphBridgeExecutions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.RenderGraphSchedulerExecutions",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA3E635);
    TracyPlotConfig("Refii.RenderGraphSchedulerSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii.RenderGraphSchedulerBridgeSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedPlanSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedRenderSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x818CF8);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedClearSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x67E8F9);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedClearCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22D3EE);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedDrawSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedDrawCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xC084FC);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedResolveSteps",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF472B6);
    TracyPlotConfig("Refii.RenderGraphSchedulerGraphOwnedResolveCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB7185);
    TracyPlotConfig("Refii.RenderGraphSchedulerBridgeCommands",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFDBA74);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanPasses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x4ADE80);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanResources",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanEdgeOnlyPasses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x34D399);
    TracyPlotConfig("Refii.RenderGraphRuntimePlanSurfaceDestinationEdges",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii.FrameHistoryEntries",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.PacketQueueDepth",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii.PacketReplayPlanned",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.PacketReplayUnsupported",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xEF4444);
    TracyPlotConfig("Refii.PipelineCreates",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB7185);
    TracyPlotConfig("Refii.PipelineCacheMisses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF43F5E);
    TracyPlotConfig("Refii.ShaderMisses",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.TextureUploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii.BufferUploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii.MissingResources",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xEF4444);
    TracyPlotConfig("Refii.MissingTextures",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF87171);
    TracyPlotConfig("Refii.MissingShaders",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFBBF24);
    TracyPlotConfig("Refii.RenderTargetSwitches",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.DepthTargetSwitches",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.FrameDatabaseBytes",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.RootCoverage.Serialized",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x94A3B8);
    TracyPlotConfig("Refii.RootCoverage.Draw",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii.RootCoverage.VertexShader",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x818CF8);
    TracyPlotConfig("Refii.RootCoverage.PixelShader",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.RootCoverage.FullShader",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.RootCoverage.PrivateWrapper",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.RootCoverage.Any",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii.Pass.MainScene",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii.Pass.Video",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
    TracyPlotConfig("Refii.Pass.PrivateRect",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii.Pass.DepthOnly",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii.Pass.PostProcessOrUI",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii.Pass.WrapperDraw",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
  });
}

bool ShouldLogPlumeFrameTiming(uint32_t frame_index) {
  // Every 120 frames is far too sparse to characterise the title phase: at
  // ~4fps a 34-second title window yielded exactly TWO samples (1.2ms with 0
  // draws, 88.6ms with 164 draws). That is not a distribution, and reading the
  // larger one as "the compose cost" is exactly the kind of single-sample
  // conclusion this project keeps getting burned by.
  //
  // 8 is dense enough to characterise a 4fps phase (~30 samples per 60s) and
  // still ~15x cheaper than logging every frame. Restore 120 once the compose
  // distribution is established.
  return frame_index <= 8 || frame_index % 8 == 0;
}

#if REFII_RENDERER_HAS_PLUME && REFII_SDL3_WINDOW_ENABLED
bool ShouldMarshalPlumeToUIThread() {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->app_context()) {
    return false;
  }
  return !runtime->app_context()->IsInUIThread();
}

bool CallPlumeOnUIThreadSynchronous(std::function<void()> function) {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->app_context()) {
    return false;
  }
  return runtime->app_context()->CallInUIThreadSynchronous(std::move(function));
}

bool CallPlumeOnUIThreadDeferred(std::function<void()> function) {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->app_context()) {
    return false;
  }
  return runtime->app_context()->CallInUIThread(std::move(function));
}
#endif

std::string BuildFrameCorrelationLabel(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot) {
  char label[256] = {};
  std::snprintf(
      label, sizeof(label),
      "Frame %u / DrawPackets %u / DrawEvents %u / VS_%08X / PS_%08X / "
      "RT_%08X / Pix_%08X / Output_%ux%u",
      frame_index, direct_stats.draw_count, direct_stats.last_draw_events,
      hook_snapshot.vertex_shader, hook_snapshot.pixel_shader,
      direct_stats.last_present_target, hook_snapshot.last_pix_texture, width,
      height);
  return label;
}

std::string BuildRenderDocCaptureTitle(std::string_view correlation_label) {
  return "Refii Plume / " + std::string(correlation_label);
}

std::string BuildRenderDocCaptureFileTag(uint32_t frame_index,
                                         uint32_t draw_index,
                                         std::string_view draw_target,
                                         std::string_view pass_name,
                                         std::string_view capture_reason) {
  char prefix[160] = {};
  if (!capture_reason.empty()) {
    const bool error_reason =
        capture_reason.find("error") != std::string_view::npos ||
        capture_reason.find("failed") != std::string_view::npos ||
        capture_reason.find("failure") != std::string_view::npos;
    std::snprintf(prefix, sizeof(prefix),
                  error_reason ? "refii_error_%.*s_frame_%06u"
                               : "refii_%.*s_frame_%06u",
                  static_cast<int>(capture_reason.size()),
                  capture_reason.data(), frame_index);
  } else {
    std::snprintf(prefix, sizeof(prefix), "refii_frame_%06u", frame_index);
  }

  std::string tag(prefix);
  if (draw_index != 0) {
    char draw_label[32] = {};
    std::snprintf(draw_label, sizeof(draw_label), "_draw_%06u", draw_index);
    tag += draw_label;
  }
  if (!draw_target.empty()) {
    tag += "_target_";
    tag.append(SanitizeRenderDocTagComponent(draw_target));
  }
  if (!pass_name.empty()) {
    tag += "_pass_";
    tag.append(SanitizeRenderDocTagComponent(pass_name));
  }
  return tag;
}

std::string BuildFrameDebugScopeLabel(uint32_t frame_index, uint32_t width,
                                      uint32_t height) {
  char label[96] = {};
  std::snprintf(label, sizeof(label), "RefiiPlume Frame %u Output_%ux%u",
                frame_index, width, height);
  return label;
}

std::string BuildRenderGraphExecuteLabel(uint32_t frame_index,
                                         uint32_t width,
                                         uint32_t height) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Execute Frame %u Output_%ux%u",
                frame_index, width, height);
  return label;
}

std::string BuildRenderGraphBridgeLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Phase::FableDirectBridge Frame %u",
                frame_index);
  return label;
}

std::string BuildRenderGraphSchedulerLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Scheduler Frame %u", frame_index);
  return label;
}

std::string BuildRenderGraphSchedulerBridgeStepLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Scheduler::Step::FableDirectBridge Frame %u",
                frame_index);
  return label;
}

std::string BuildRenderGraphSchedulerPlanStepLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(
      label, sizeof(label),
      "RefiiRenderGraph::Scheduler::Step::FrameGraphPlanPublish Frame %u",
      frame_index);
  return label;
}

class PlumeDebugLabelScope {
 public:
  PlumeDebugLabelScope(plume::RenderCommandList* command_list,
                       const std::string& label)
      : command_list_(command_list), active_(command_list != nullptr) {
    if (active_) {
      command_list_->beginDebugLabel(label);
    }
  }

  ~PlumeDebugLabelScope() {
    End();
  }

  void End() {
    if (!active_) {
      return;
    }
    command_list_->endDebugLabel();
    active_ = false;
  }

 private:
  plume::RenderCommandList* command_list_ = nullptr;
  bool active_ = false;
};

struct RefiiRenderGraphRuntimePlanSnapshot {
  uint32_t frame_index = 0;
  uint32_t passes = 0;
  uint32_t packet_passes = 0;
  uint32_t edge_only_passes = 0;
  uint32_t omitted_passes = 0;
  uint32_t draw_packets = 0;
  uint32_t omitted_draw_packets = 0;
  uint32_t command_packets = 0;
  uint32_t omitted_command_packets = 0;
  uint32_t resource_references = 0;
  uint32_t resource_edges = 0;
  uint32_t surface_destination_edges = 0;
  uint32_t present_blit_edges = 0;
  uint32_t execution_steps = 0;
  uint32_t barriers = 0;
  uint32_t phases = 0;
};

struct RefiiRenderGraphRuntimeSchedulerSnapshot {
  uint32_t frame_index = 0;
  uint32_t executions = 0;
  uint32_t steps = 0;
  uint32_t bridge_steps = 0;
  uint32_t graph_owned_steps = 0;
  uint32_t graph_owned_plan_steps = 0;
  uint32_t graph_owned_render_steps = 0;
  uint32_t graph_owned_clear_steps = 0;
  uint32_t graph_owned_clear_commands = 0;
  uint32_t graph_owned_draw_steps = 0;
  uint32_t graph_owned_draw_commands = 0;
  uint32_t graph_owned_resolve_steps = 0;
  uint32_t graph_owned_resolve_commands = 0;
  uint32_t bridge_commands = 0;
};

std::atomic<uint32_t> g_refii_render_graph_runtime_plan_frame{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_packet_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_edge_only_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_omitted_passes{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_draw_packets{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_omitted_draw_packets{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_command_packets{0};
std::atomic<uint32_t>
    g_refii_render_graph_runtime_plan_omitted_command_packets{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_resource_references{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_resource_edges{0};
std::atomic<uint32_t>
    g_refii_render_graph_runtime_plan_surface_destination_edges{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_execution_steps{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_barriers{0};
std::atomic<uint32_t> g_refii_render_graph_runtime_plan_phases{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_frame{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_executions{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_bridge_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_plan_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_render_steps{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_clear_steps{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_clear_commands{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_graph_owned_draw_steps{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_draw_commands{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_resolve_steps{0};
std::atomic<uint32_t>
    g_refii_render_graph_scheduler_graph_owned_resolve_commands{0};
std::atomic<uint32_t> g_refii_render_graph_scheduler_bridge_commands{0};

void StoreRefiiRenderGraphRuntimeSchedulerSnapshot(
    const RefiiRenderGraphRuntimeSchedulerSnapshot& snapshot) {
  g_refii_render_graph_scheduler_frame.store(snapshot.frame_index,
                                            std::memory_order_release);
  g_refii_render_graph_scheduler_executions.store(snapshot.executions,
                                                 std::memory_order_release);
  g_refii_render_graph_scheduler_steps.store(snapshot.steps,
                                            std::memory_order_release);
  g_refii_render_graph_scheduler_bridge_steps.store(
      snapshot.bridge_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_steps.store(
      snapshot.graph_owned_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_plan_steps.store(
      snapshot.graph_owned_plan_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_render_steps.store(
      snapshot.graph_owned_render_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_clear_steps.store(
      snapshot.graph_owned_clear_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_clear_commands.store(
      snapshot.graph_owned_clear_commands, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_draw_steps.store(
      snapshot.graph_owned_draw_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_draw_commands.store(
      snapshot.graph_owned_draw_commands, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_resolve_steps.store(
      snapshot.graph_owned_resolve_steps, std::memory_order_release);
  g_refii_render_graph_scheduler_graph_owned_resolve_commands.store(
      snapshot.graph_owned_resolve_commands, std::memory_order_release);
  g_refii_render_graph_scheduler_bridge_commands.store(
      snapshot.bridge_commands, std::memory_order_release);
}

RefiiRenderGraphRuntimeSchedulerSnapshot
GetRefiiRenderGraphRuntimeSchedulerSnapshot() {
  RefiiRenderGraphRuntimeSchedulerSnapshot snapshot;
  snapshot.frame_index =
      g_refii_render_graph_scheduler_frame.load(std::memory_order_acquire);
  snapshot.executions = g_refii_render_graph_scheduler_executions.load(
      std::memory_order_acquire);
  snapshot.steps =
      g_refii_render_graph_scheduler_steps.load(std::memory_order_acquire);
  snapshot.bridge_steps = g_refii_render_graph_scheduler_bridge_steps.load(
      std::memory_order_acquire);
  snapshot.graph_owned_steps =
      g_refii_render_graph_scheduler_graph_owned_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_plan_steps =
      g_refii_render_graph_scheduler_graph_owned_plan_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_render_steps =
      g_refii_render_graph_scheduler_graph_owned_render_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_clear_steps =
      g_refii_render_graph_scheduler_graph_owned_clear_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_clear_commands =
      g_refii_render_graph_scheduler_graph_owned_clear_commands.load(
          std::memory_order_acquire);
  snapshot.graph_owned_draw_steps =
      g_refii_render_graph_scheduler_graph_owned_draw_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_draw_commands =
      g_refii_render_graph_scheduler_graph_owned_draw_commands.load(
          std::memory_order_acquire);
  snapshot.graph_owned_resolve_steps =
      g_refii_render_graph_scheduler_graph_owned_resolve_steps.load(
          std::memory_order_acquire);
  snapshot.graph_owned_resolve_commands =
      g_refii_render_graph_scheduler_graph_owned_resolve_commands.load(
          std::memory_order_acquire);
  snapshot.bridge_commands = g_refii_render_graph_scheduler_bridge_commands.load(
      std::memory_order_acquire);
  return snapshot;
}

void StoreRefiiRenderGraphRuntimePlanSnapshot(
    const RefiiRenderGraphRuntimePlanSnapshot& snapshot) {
  g_refii_render_graph_runtime_plan_frame.store(snapshot.frame_index,
                                               std::memory_order_release);
  g_refii_render_graph_runtime_plan_passes.store(snapshot.passes,
                                                std::memory_order_release);
  g_refii_render_graph_runtime_plan_packet_passes.store(
      snapshot.packet_passes, std::memory_order_release);
  g_refii_render_graph_runtime_plan_edge_only_passes.store(
      snapshot.edge_only_passes, std::memory_order_release);
  g_refii_render_graph_runtime_plan_omitted_passes.store(
      snapshot.omitted_passes, std::memory_order_release);
  g_refii_render_graph_runtime_plan_draw_packets.store(
      snapshot.draw_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_omitted_draw_packets.store(
      snapshot.omitted_draw_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_command_packets.store(
      snapshot.command_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_omitted_command_packets.store(
      snapshot.omitted_command_packets, std::memory_order_release);
  g_refii_render_graph_runtime_plan_resource_references.store(
      snapshot.resource_references, std::memory_order_release);
  g_refii_render_graph_runtime_plan_resource_edges.store(
      snapshot.resource_edges, std::memory_order_release);
  g_refii_render_graph_runtime_plan_surface_destination_edges.store(
      snapshot.surface_destination_edges, std::memory_order_release);
  g_refii_render_graph_runtime_plan_execution_steps.store(
      snapshot.execution_steps, std::memory_order_release);
  g_refii_render_graph_runtime_plan_barriers.store(snapshot.barriers,
                                                  std::memory_order_release);
  g_refii_render_graph_runtime_plan_phases.store(snapshot.phases,
                                                std::memory_order_release);
}

void StoreRefiiRenderGraphRuntimePlanSnapshot(
    const FableDirectRenderGraphSnapshot& snapshot) {
  RefiiRenderGraphRuntimePlanSnapshot runtime_plan;
  runtime_plan.frame_index = snapshot.frame_index;
  runtime_plan.passes = snapshot.pass_count;
  runtime_plan.packet_passes = snapshot.pass_count;
  runtime_plan.omitted_passes = snapshot.omitted_passes;
  runtime_plan.draw_packets = snapshot.draw_packet_count;
  runtime_plan.omitted_draw_packets = snapshot.omitted_draw_packets;
  runtime_plan.command_packets = snapshot.command_packet_count;
  runtime_plan.omitted_command_packets = snapshot.omitted_command_packets;
  runtime_plan.resource_references = snapshot.resource_reference_count;
  runtime_plan.execution_steps = snapshot.pass_count;
  StoreRefiiRenderGraphRuntimePlanSnapshot(runtime_plan);
}

FableDirectRendererStats ExecuteRefiiRenderGraphFrame(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    plume::RenderFormat output_depth_format,
    uint32_t frame_index,
    uint32_t width,
    uint32_t height) {
  ZoneScopedN("RenderGraph::Execute");
  PlumeDebugLabelScope execute_scope(
      command_list, BuildRenderGraphExecuteLabel(frame_index, width, height));
  {
    ZoneScopedN("RenderGraph::Scheduler");
    PlumeDebugLabelScope scheduler_scope(
        command_list, BuildRenderGraphSchedulerLabel(frame_index));
    RefiiRenderGraphRuntimeSchedulerSnapshot scheduler_snapshot;
    scheduler_snapshot.frame_index = frame_index;
    scheduler_snapshot.executions = 1;
    scheduler_snapshot.steps = 2;
    scheduler_snapshot.bridge_steps = 1;
    scheduler_snapshot.graph_owned_steps = 1;
    scheduler_snapshot.graph_owned_plan_steps = 1;
    scheduler_snapshot.graph_owned_render_steps = 0;
    scheduler_snapshot.graph_owned_clear_steps = 0;
    scheduler_snapshot.graph_owned_clear_commands = 0;
    scheduler_snapshot.graph_owned_draw_steps = 0;
    scheduler_snapshot.graph_owned_draw_commands = 0;
    scheduler_snapshot.graph_owned_resolve_steps = 0;
    scheduler_snapshot.graph_owned_resolve_commands = 0;
    scheduler_snapshot.bridge_commands = 0;
    StoreRefiiRenderGraphRuntimeSchedulerSnapshot(scheduler_snapshot);
    {
      ZoneScopedN("RenderGraph::Scheduler/Step::FableDirectBridge");
      PlumeDebugLabelScope scheduler_step_scope(
          command_list, BuildRenderGraphSchedulerBridgeStepLabel(frame_index));
      {
        ZoneScopedN("RenderGraph::Execute/FableDirectBridge");
        PlumeDebugLabelScope bridge_scope(
            command_list, BuildRenderGraphBridgeLabel(frame_index));
        // Deferred texel captures land here so the recorded frame sees
        // fresh content without the guest thread paying for the copies.
        //
        // STEP 6 capture-tied ack: sample the guest present boundary BEFORE
        // the drain (conservative — a boundary that lands mid-record is NOT
        // claimed), and publish it back once RecordFableDirectFrame returns:
        // by then every CPU-side guest-memory read for this frame (deferred
        // texel captures + record) has completed, which is what releases the
        // guest thread's capture-tied present ack in renderer_runtime.cpp.
        const uint64_t capture_ack_boundary_seq =
            FableGuestPresentBoundarySeqForCapture();
        DrainDeferredTextureCaptures();
        RecordFableDirectFrame(device, command_list, output_framebuffer,
                               output_texture, present_framebuffer,
                               shader_format, output_depth_format,
                               frame_index);
        PublishFableCapturesCompletedThroughBoundary(capture_ack_boundary_seq);
      }
    }
    const FableDirectRenderGraphExecutionSnapshot graph_execution =
        GetFableDirectRenderGraphExecutionSnapshot();
    {
      ZoneScopedN("RenderGraph::Scheduler/Step::FrameGraphPlanPublish");
      PlumeDebugLabelScope scheduler_plan_step_scope(
          command_list, BuildRenderGraphSchedulerPlanStepLabel(frame_index));
      StoreRefiiRenderGraphRuntimePlanSnapshot(
          GetFableDirectRenderGraphSnapshot());
    }
    scheduler_snapshot.bridge_steps =
        graph_execution.bridge_steps != 0 ? graph_execution.bridge_steps : 1u;
    scheduler_snapshot.bridge_commands = graph_execution.bridge_commands;
    scheduler_snapshot.graph_owned_render_steps =
        graph_execution.graph_owned_render_steps;
    scheduler_snapshot.graph_owned_clear_steps =
        graph_execution.graph_owned_clear_steps;
    scheduler_snapshot.graph_owned_clear_commands =
        graph_execution.graph_owned_clear_commands;
    scheduler_snapshot.graph_owned_draw_steps =
        graph_execution.graph_owned_draw_steps;
    scheduler_snapshot.graph_owned_draw_commands =
        graph_execution.graph_owned_draw_commands;
    scheduler_snapshot.graph_owned_resolve_steps =
        graph_execution.graph_owned_resolve_steps;
    scheduler_snapshot.graph_owned_resolve_commands =
        graph_execution.graph_owned_resolve_commands;
    scheduler_snapshot.graph_owned_steps =
        scheduler_snapshot.graph_owned_plan_steps +
        scheduler_snapshot.graph_owned_render_steps;
    scheduler_snapshot.steps =
        scheduler_snapshot.bridge_steps + scheduler_snapshot.graph_owned_steps;
    StoreRefiiRenderGraphRuntimeSchedulerSnapshot(scheduler_snapshot);
  }
  return GetFableDirectRendererStats();
}

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

struct FrameDatabaseResourceNameIndex {
  std::unordered_map<uint32_t, std::string> names;
  std::unordered_map<uint32_t, std::string> kinds;
  std::unordered_map<uint32_t, uint64_t> content_hashes;

  void Add(uint32_t id, std::string_view kind, std::string_view name,
           uint64_t content_hash) {
    if (id == 0) {
      return;
    }
    kinds.try_emplace(id, std::string(kind));
    if (content_hash != 0) {
      content_hashes.try_emplace(id, content_hash);
    }
    if (!name.empty()) {
      names.try_emplace(id, std::string(name));
    }
  }

  std::string NameFor(uint32_t id, std::string_view default_role) const {
    if (id == 0) {
      return "none";
    }
    const auto found = names.find(id);
    if (found != names.end()) {
      return found->second;
    }
    const auto kind = kinds.find(id);
    if (kind != kinds.end()) {
      return FrameDatabaseSyntheticName(kind->second, id);
    }
    return FrameDatabaseSyntheticName(default_role, id);
  }

  std::string KindFor(uint32_t id, std::string_view default_kind) const {
    if (id == 0) {
      return "none";
    }
    const auto found = kinds.find(id);
    if (found != kinds.end()) {
      return found->second;
    }
    return std::string(default_kind);
  }

  uint64_t ContentHashFor(uint32_t id) const {
    const auto found = content_hashes.find(id);
    return found != content_hashes.end() ? found->second : 0ull;
  }
};

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

enum class FrameDatabaseResourceRole : uint8_t {
  kVertexShader,
  kPixelShader,
  kVertexDeclaration,
  kRenderTarget,
  kDepthStencil,
  kTexture,
  kVertexBuffer,
  kIndexBuffer,
};

struct FrameDatabaseResourceEvent {
  uint32_t resource_id = 0;
  uint64_t first_packet_id = 0;
  uint32_t reference_count = 0;
  bool vertex_shader = false;
  bool pixel_shader = false;
  bool vertex_declaration = false;
  bool render_target = false;
  bool depth_stencil = false;
  bool texture = false;
  bool vertex_buffer = false;
  bool index_buffer = false;
};

struct FrameDatabaseResourceEdge {
  std::string kind;
  std::string pass_name;
  uint64_t packet_id = 0;
  uint64_t command_serial = 0;
  FableDirectFrameDatabaseRoot root;
  std::array<uint32_t, 4> source_render_targets = {};
  uint32_t source_depth_stencil = 0;
  uint32_t destination_base = 0;
  uint32_t destination_pitch = 0;
  uint32_t destination_info = 0;
  uint32_t destination_slice = 0;
  uint64_t relation_serial = 0;
  uint32_t source_surface = 0;
  uint32_t destination_texture = 0;
  uint32_t source_slot = 0xFFFFFFFFu;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t destination_width = 0;
  uint32_t destination_height = 0;
  uint32_t texture_base_address = 0;
  uint32_t backing_texture = 0;
  uint32_t backing_base_address = 0;
  uint64_t backing_content_serial = 0;
  uint64_t target_content_serial = 0;
  bool scaled_copy = false;
  bool trusted_destination_edge = false;
  bool wrapper_source_exact = false;
};

struct FrameDatabasePassSummary {
  std::string pass_name;
  std::string pass_class;
  uint64_t first_packet_id = 0;
  uint64_t first_command_packet_id = 0;
  uint32_t command_packets = 0;
  uint32_t clear_packets = 0;
  uint32_t resolve_packets = 0;
  uint32_t resource_edges = 0;
  uint32_t clear_resource_edges = 0;
  uint32_t resolve_resource_edges = 0;
  uint32_t surface_destination_edges = 0;
  uint32_t present_blit_edges = 0;
  uint32_t draw_packets = 0;
  uint32_t indexed_draw_packets = 0;
  uint32_t draw_rooted_packets = 0;
  uint32_t vertex_shader_rooted_packets = 0;
  uint32_t pixel_shader_rooted_packets = 0;
  uint32_t fully_shader_rooted_packets = 0;
  uint32_t private_texture_wrapper_rooted_packets = 0;
  uint32_t any_rooted_packets = 0;
};

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

enum class FrameDatabasePassResourceRole : uint8_t {
  kVertexShader,
  kPixelShader,
  kVertexDeclaration,
  kRenderTarget,
  kDepthStencil,
  kTexture,
  kVertexBuffer,
  kIndexBuffer,
  kClearTarget,
  kResolveSource,
  kResolveDestination,
  kSurfaceSource,
  kSurfaceDestination,
  kPresentSource,
  kPresentOutput,
};

struct FrameDatabasePassResourceUsage {
  std::string pass_name;
  uint32_t resource_id = 0;
  uint32_t read_count = 0;
  uint32_t write_count = 0;
  bool vertex_shader = false;
  bool pixel_shader = false;
  bool vertex_declaration = false;
  bool render_target = false;
  bool depth_stencil = false;
  bool texture = false;
  bool vertex_buffer = false;
  bool index_buffer = false;
  bool clear_target = false;
  bool resolve_source = false;
  bool resolve_destination = false;
  bool surface_source = false;
  bool surface_destination = false;
  bool present_source = false;
  bool present_output = false;
};

struct FrameDatabasePassDependency {
  std::string producer_pass;
  std::string consumer_pass;
  uint32_t resource_id = 0;
  std::string hazard;
  uint32_t producer_write_count = 0;
  uint32_t consumer_read_count = 0;
  uint32_t consumer_write_count = 0;
};

struct FrameDatabasePassResourceTransition {
  std::string before_pass;
  std::string after_pass;
  uint32_t resource_id = 0;
  std::string before_access;
  std::string after_access;
  std::string hazard;
};

struct FrameDatabaseFrameResourceLifetime {
  uint32_t resource_id = 0;
  uint32_t first_pass_index = 0;
  uint32_t last_pass_index = 0;
  std::string first_pass;
  std::string last_pass;
  uint32_t read_count = 0;
  uint32_t write_count = 0;
  bool vertex_shader = false;
  bool pixel_shader = false;
  bool vertex_declaration = false;
  bool render_target = false;
  bool depth_stencil = false;
  bool texture = false;
  bool vertex_buffer = false;
  bool index_buffer = false;
  bool clear_target = false;
  bool resolve_source = false;
  bool resolve_destination = false;
  bool surface_source = false;
  bool surface_destination = false;
  bool present_source = false;
  bool present_output = false;
};

struct FrameDatabaseMaterialRecord {
  uint64_t material_id = 0;
  uint64_t first_packet_id = 0;
  uint32_t draw_count = 0;
  std::string first_pass;
  std::string last_pass;
  std::string semantic_class;
  uint32_t pixel_shader = 0;
  std::array<uint32_t, 16> textures = {};
  std::array<uint32_t, 16> sampler_states = {};
  std::array<uint32_t, 4> render_targets = {};
  uint32_t depth_stencil = 0;
};

struct FrameDatabaseMeshRecord {
  uint64_t mesh_id = 0;
  uint64_t first_packet_id = 0;
  uint32_t draw_count = 0;
  std::string first_pass;
  std::string last_pass;
  std::string semantic_class;
  uint32_t vertex_declaration = 0;
  std::array<uint32_t, 16> vertex_buffers = {};
  std::array<uint32_t, 16> vertex_strides = {};
  std::array<uint32_t, 16> vertex_offsets = {};
  uint32_t index_buffer = 0;
  uint32_t index_format = 0;
  bool indexed = false;
  uint32_t primitive_type = 0;
  uint32_t start = 0;
  uint32_t vertex_or_index_count = 0;
  int32_t base_vertex = 0;
};

struct FrameDatabaseAssetUsageRecord {
  std::string kind;
  uint32_t resource_id = 0;
  uint32_t slot = 0xFFFFFFFFu;
  uint64_t packet_id = 0;
  std::string pass_name;
  std::string semantic_class;
  uint64_t material_id = 0;
  uint64_t mesh_id = 0;
};

struct FrameDatabaseRenderGraphNode {
  uint32_t pass_index = 0;
  std::string pass_name;
  std::string pass_class;
  std::string phase_name;
  uint32_t draw_packets = 0;
  uint32_t command_packets = 0;
  uint32_t resource_usages = 0;
  uint32_t read_resources = 0;
  uint32_t write_resources = 0;
  uint32_t material_usages = 0;
  uint32_t mesh_usages = 0;
  uint32_t asset_usages = 0;
};

struct FrameDatabaseRenderGraphEdge {
  uint32_t edge_index = 0;
  std::string producer_pass;
  std::string consumer_pass;
  uint32_t resource_id = 0;
  std::string hazard;
};

struct FrameDatabaseRenderGraphExecutionStep {
  uint32_t step_index = 0;
  uint32_t pass_index = 0;
  std::string pass_name;
  std::string pass_class;
  uint32_t wait_edges = 0;
  uint32_t signal_edges = 0;
  uint32_t barriers_before = 0;
  uint32_t barriers_after = 0;
  bool execute_draws = false;
  bool execute_commands = false;
  bool reads_resources = false;
  bool writes_resources = false;
};

struct FrameDatabaseRenderGraphPhase {
  uint32_t phase_index = 0;
  std::string phase_name;
  uint32_t first_pass_index = 0;
  uint32_t last_pass_index = 0;
  uint32_t pass_count = 0;
  uint32_t draw_packets = 0;
  uint32_t command_packets = 0;
  uint32_t resource_usages = 0;
  uint32_t read_resources = 0;
  uint32_t write_resources = 0;
  uint32_t material_usages = 0;
  uint32_t mesh_usages = 0;
  uint32_t asset_usages = 0;
  uint32_t wait_edges = 0;
  uint32_t signal_edges = 0;
  uint32_t barriers_before = 0;
  uint32_t barriers_after = 0;
};

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

struct FrameDatabaseResourceEdgeCounts {
  uint32_t total_edges = 0;
  uint32_t clear_edges = 0;
  uint32_t resolve_edges = 0;
  uint32_t surface_destination_edges = 0;
  uint32_t present_blit_edges = 0;
};

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

#if REFII_RENDERER_HAS_PLUME
struct PlumeFrameContext {
  bool submitted = false;
  std::unique_ptr<plume::RenderCommandList> command_list;
  std::unique_ptr<plume::RenderCommandSemaphore> acquire_semaphore;
  std::unique_ptr<plume::RenderCommandFence> fence;
};

// Present-decoupling step 2: persistent offscreen compose target. The fable
// frame records into one of these instead of the acquired swapchain image;
// the finished compose is then blitted into the swapchain image, and the
// diagnostics/ImGui overlay draw into the swapchain image on top exactly as
// they previously drew after the scene. Two targets alternate so a re-present
// blit of one can overlap a record into the other (step 3 consumes this);
// last_use_frame_context names the frame context whose fence covers this
// target's most recent GPU use. Same size/format as the swapchain images,
// recreated with the framebuffers on any resize.
struct PlumeComposeTarget {
  std::unique_ptr<plume::RenderTexture> color_texture;
  // Color-only framebuffer (mirrors framebuffers[i] for the swapchain image).
  std::unique_ptr<plume::RenderFramebuffer> framebuffer;
  // Color + shared backend depth (mirrors depth_framebuffers[i]).
  std::unique_ptr<plume::RenderFramebuffer> depth_framebuffer;
  uint32_t last_use_frame_context = UINT32_MAX;
  bool recorded = false;
};
#endif

struct PlumeBackendState {
  std::mutex mutex;
  PlumeBackendStatus status = PlumeBackendStatus::kUnavailable;
  RendererGraphicsApi api = RendererGraphicsApi::kVulkan;
  bool attempted = false;
  bool logged_hook_observer = false;
#if REFII_RENDERER_HAS_PLUME
  uint32_t present_count = 0;
  bool logged_frame_after_uploads = false;
  bool logged_frame_after_color_target = false;
  bool logged_frame_after_imgui_build = false;
  bool logged_frame_after_imgui_render = false;
  bool logged_frame_before_submit = false;
  bool logged_frame_after_present = false;
  bool logged_frame_after_wait = false;
  bool logged_first_frame_summary = false;
  bool logged_direct_command_stream = false;
  bool logged_submitted_command_stream = false;
  bool logged_present_pump_pending_command_stream = false;
  bool logged_reused_identical_fable_frame = false;
  bool logged_preserved_fable_direct_output = false;
  bool logged_renderdoc_draw_target_watch = false;
  bool logged_renderdoc_pass_watch = false;
  bool logged_renderdoc_draw_watch = false;
  uint32_t last_renderdoc_draw_capture = 0;
  std::string last_renderdoc_draw_target_capture;
  std::string last_renderdoc_pass_capture;
  // Remaining consecutive captures after a pass match: the queued-command
  // probe sees commands one frame before/after they execute, so a single
  // capture often brackets only the held-present blit.
  uint32_t renderdoc_pass_capture_repeats = 0;
  bool has_fable_direct_output_to_preserve = false;
  std::unique_ptr<plume::RenderInterface> render_interface;
  std::unique_ptr<plume::RenderDevice> device;
  std::unique_ptr<plume::RenderCommandQueue> direct_queue;
  std::unique_ptr<plume::RenderCommandQueue> copy_queue;
  std::unique_ptr<plume::RenderSwapChain> swap_chain;
  bool swap_chain_present_wait_enabled = false;
  uint32_t next_frame_context = 0;
  std::vector<PlumeFrameContext> frame_contexts;
  std::vector<std::unique_ptr<plume::RenderCommandSemaphore>> release_semaphores;
  std::unique_ptr<plume::RenderTexture> depth_texture;
  std::vector<std::unique_ptr<plume::RenderFramebuffer>> framebuffers;
  std::vector<std::unique_ptr<plume::RenderFramebuffer>> depth_framebuffers;
  // Present-decoupling steps 2/3 (see PlumeComposeTarget). next_compose_target
  // is the target the next full record uses; last_recorded_compose_target is
  // the most recent completed record (UINT32_MAX = none yet, forces a record).
  // last_record_present_target / last_record_bink_upload_count snapshot what
  // that record saw, for the fast path's PresentFableDirectFrame call and the
  // "fresh Bink texels force a record" check.
  std::array<PlumeComposeTarget, 2> compose_targets;
  uint32_t next_compose_target = 0;
  uint32_t last_recorded_compose_target = UINT32_MAX;
  uint32_t last_record_present_target = 0;
  uint64_t last_record_bink_upload_count = 0;
  uint64_t last_record_deferred_capture_generation = 0;
  // When the last executed record finished (submit-side bookkeeping). Gates
  // ONLY the captures record reason — see kPlumeCapturesRerecordMinInterval.
  PlumeClock::time_point last_record_completed_at{};
#endif
};

PlumeBackendState g_plume_backend;
std::atomic<int64_t> g_last_plume_marshal_us{0};
std::atomic<int64_t> g_last_plume_frame_total_us{0};
std::atomic<int64_t> g_last_plume_backend_us{0};
std::atomic<int64_t> g_last_plume_output_us{0};
std::atomic<int64_t> g_last_plume_consume_us{0};
std::atomic<int64_t> g_last_plume_sync_us{0};
std::atomic<int64_t> g_last_plume_readiness_us{0};
std::atomic<int64_t> g_last_plume_acquire_us{0};
std::atomic<int64_t> g_last_plume_acquire_texture_us{0};
std::atomic<int64_t> g_last_plume_setup_us{0};
std::atomic<int64_t> g_last_plume_imgui_build_us{0};
std::atomic<int64_t> g_last_plume_direct_us{0};
std::atomic<int64_t> g_last_plume_imgui_render_us{0};
std::atomic<int64_t> g_last_plume_command_record_us{0};
std::atomic<int64_t> g_last_plume_submit_us{0};
std::atomic<int64_t> g_last_plume_present_us{0};
std::atomic<int64_t> g_last_plume_fence_wait_us{0};
std::atomic_bool g_plume_output_ready{false};
std::atomic_bool g_plume_output_vsync_enabled{false};
std::atomic<uint32_t> g_plume_output_width{0};
std::atomic<uint32_t> g_plume_output_height{0};
std::atomic<uint32_t> g_plume_output_texture_count{0};
std::atomic<uint32_t> g_plume_output_present_count{0};
std::atomic<uint32_t> g_plume_backend_debug_status{
    static_cast<uint32_t>(PlumeBackendStatus::kUnavailable)};
std::atomic<uint32_t> g_plume_backend_debug_api{
    static_cast<uint32_t>(RendererGraphicsApi::kVulkan)};
std::atomic_bool g_plume_backend_debug_attempted{false};
std::atomic_bool g_plume_backend_debug_direct_queue_ready{false};
std::atomic_bool g_plume_backend_debug_copy_queue_ready{false};
std::atomic_bool g_plume_backend_debug_swapchain_ready{false};
std::atomic_bool g_plume_backend_debug_swapchain_present_wait_enabled{false};
std::atomic<uint32_t> g_plume_backend_debug_next_frame_context{0};
std::atomic<uint32_t> g_plume_backend_debug_frame_context_count{0};
std::atomic<uint32_t> g_plume_backend_debug_release_semaphore_count{0};
std::atomic<uint32_t> g_plume_backend_debug_framebuffer_count{0};
std::atomic<uint32_t> g_plume_backend_debug_depth_framebuffer_count{0};
std::atomic<uint64_t> g_plume_backend_debug_dedicated_video_memory{0};
std::mutex g_plume_backend_debug_device_name_mutex;
std::array<char, kPlumeBackendDebugDeviceNameCapacity>
    g_plume_backend_debug_device_name{};
std::atomic<uint32_t> g_last_plume_fable_draw_events{0};
std::atomic_bool g_last_plume_fable_draw_events_fresh{false};
std::atomic<uint32_t> g_last_plume_fable_render_commands{0};
std::atomic<uint32_t> g_last_plume_fable_command_source{
    static_cast<uint32_t>(PlumeFableCommandSource::kNone)};
std::atomic<uint32_t> g_last_plume_fable_direct_source{
    static_cast<uint32_t>(PlumeFableCommandSource::kNone)};
std::atomic_bool g_plume_backend_shutdown_requested{false};
std::atomic_bool g_plume_async_present_pending{false};
std::atomic_bool g_logged_plume_async_present{false};
std::atomic_bool g_logged_plume_async_present_coalesced{false};
// Present census (present-decoupling step 1): cumulative counters logged
// every ~2s from the present path. requests = pump presents that reached the
// UI-thread coalescer; coalesced_dropped = requests discarded because one was
// already queued (g_plume_async_present_pending held — with the step-5
// pipeline cvar ON the slot is released when execution starts, so this counts
// only requests faster than one-queued-plus-one-executing); presented =
// swapchain presents that returned true; rerecords = frames that ran the full
// fable record; represents = step-3 fast-path frames that re-blitted the held
// offscreen compose target instead.
std::atomic<uint64_t> g_plume_present_census_requests{0};
std::atomic<uint64_t> g_plume_present_census_coalesced_dropped{0};
std::atomic<uint64_t> g_plume_present_census_presented{0};
std::atomic<uint64_t> g_plume_present_census_rerecords{0};
std::atomic<uint64_t> g_plume_present_census_represents{0};
// Per-reason attribution for every executed record (first matching reason in
// evaluation order wins; a fast-path frame increments nothing). Run 460:
// rerecords outnumbered guest frames ~10x with no attribution — this names
// the residual false-positive source directly on the census line.
enum PlumePresentRecordWhy : size_t {
  kPlumeRecordWhyNoCache = 0,   // no completed compose target (boot/resize)
  kPlumeRecordWhyRenderDoc,     // a capture wants this present
  kPlumeRecordWhyBink,          // Bink upload generation advanced
  kPlumeRecordWhyCaptures,      // deferred-capture enqueue generation advanced
  kPlumeRecordWhyMode,          // GTL-authoritative submission off
  kPlumeRecordWhyVideo,         // startup-video pump active
  kPlumeRecordWhyBoundary,      // guest frame boundary missing or advanced
  kPlumeRecordWhyCarried,       // carried draw packets await their compose
  kPlumeRecordWhyPackets,       // late GTL packets below the held boundary
  kPlumeRecordWhyCount,
};
std::array<std::atomic<uint64_t>, kPlumeRecordWhyCount>
    g_plume_present_record_why{};
// Wakes where the captures reason WANTED a record but was suppressed by the
// minimum re-record interval (run 461: the guest enqueues ~2 captures spread
// across each ~190ms frame, so a ~100ms record always overlapped a fresh
// enqueue and captures-caused records ran back-to-back, freezing represents).
std::atomic<uint64_t> g_plume_present_census_captures_deferred{0};

struct AtomicPlumeFrameHistoryEntry {
  std::atomic_bool valid{false};
  std::atomic_bool presented{false};
  std::atomic_bool renderdoc_capture_active{false};
  std::atomic<uint32_t> frame_index{0};
  std::atomic<uint32_t> width{0};
  std::atomic<uint32_t> height{0};
  std::atomic<uint32_t> draw_count{0};
  std::atomic<uint32_t> indexed_draw_count{0};
  std::atomic<uint32_t> render_command_count{0};
  std::atomic<uint32_t> resource_upload_count{0};
  std::atomic<uint32_t> pipeline_cache_miss_count{0};
  std::atomic<uint32_t> failed_resource_sync_count{0};
  std::atomic<uint32_t> present_target{0};
  std::atomic<int64_t> frame_total_us{0};
  std::atomic<int64_t> direct_us{0};
  std::atomic<int64_t> submit_us{0};
  std::atomic<int64_t> present_us{0};
};

std::array<AtomicPlumeFrameHistoryEntry, kPlumeFrameHistoryCapacity>
    g_plume_frame_history;
std::atomic<uint64_t> g_plume_frame_history_write_count{0};

void StorePlumeFrameHistoryEntry(
    uint32_t frame_index, uint32_t width, uint32_t height, bool presented,
    bool renderdoc_capture_active, const FableDirectRendererStats& direct_stats,
    int64_t frame_total_us, int64_t direct_us, int64_t submit_us,
    int64_t present_us) {
  const uint64_t write_index =
      g_plume_frame_history_write_count.fetch_add(1, std::memory_order_acq_rel);
  AtomicPlumeFrameHistoryEntry& entry =
      g_plume_frame_history[write_index % kPlumeFrameHistoryCapacity];
  entry.valid.store(false, std::memory_order_release);
  entry.presented.store(presented, std::memory_order_relaxed);
  entry.renderdoc_capture_active.store(renderdoc_capture_active,
                                       std::memory_order_relaxed);
  entry.frame_index.store(frame_index, std::memory_order_relaxed);
  entry.width.store(width, std::memory_order_relaxed);
  entry.height.store(height, std::memory_order_relaxed);
  entry.draw_count.store(direct_stats.draw_count, std::memory_order_relaxed);
  entry.indexed_draw_count.store(direct_stats.indexed_draw_count,
                                 std::memory_order_relaxed);
  entry.render_command_count.store(direct_stats.last_render_commands,
                                   std::memory_order_relaxed);
  entry.resource_upload_count.store(direct_stats.resource_uploads,
                                    std::memory_order_relaxed);
  entry.pipeline_cache_miss_count.store(direct_stats.pipeline_cache_misses,
                                        std::memory_order_relaxed);
  entry.failed_resource_sync_count.store(direct_stats.failed_resource_syncs,
                                         std::memory_order_relaxed);
  entry.present_target.store(direct_stats.last_present_target,
                             std::memory_order_relaxed);
  entry.frame_total_us.store(frame_total_us, std::memory_order_relaxed);
  entry.direct_us.store(direct_us, std::memory_order_relaxed);
  entry.submit_us.store(submit_us, std::memory_order_relaxed);
  entry.present_us.store(present_us, std::memory_order_relaxed);
  entry.valid.store(true, std::memory_order_release);
}

PlumeFrameHistoryEntry LoadPlumeFrameHistoryEntry(
    const AtomicPlumeFrameHistoryEntry& entry) {
  PlumeFrameHistoryEntry snapshot;
  snapshot.valid = entry.valid.load(std::memory_order_acquire);
  snapshot.presented = entry.presented.load(std::memory_order_relaxed);
  snapshot.renderdoc_capture_active =
      entry.renderdoc_capture_active.load(std::memory_order_relaxed);
  snapshot.frame_index = entry.frame_index.load(std::memory_order_relaxed);
  snapshot.width = entry.width.load(std::memory_order_relaxed);
  snapshot.height = entry.height.load(std::memory_order_relaxed);
  snapshot.draw_count = entry.draw_count.load(std::memory_order_relaxed);
  snapshot.indexed_draw_count =
      entry.indexed_draw_count.load(std::memory_order_relaxed);
  snapshot.render_command_count =
      entry.render_command_count.load(std::memory_order_relaxed);
  snapshot.resource_upload_count =
      entry.resource_upload_count.load(std::memory_order_relaxed);
  snapshot.pipeline_cache_miss_count =
      entry.pipeline_cache_miss_count.load(std::memory_order_relaxed);
  snapshot.failed_resource_sync_count =
      entry.failed_resource_sync_count.load(std::memory_order_relaxed);
  snapshot.present_target = entry.present_target.load(std::memory_order_relaxed);
  snapshot.frame_total_us = entry.frame_total_us.load(std::memory_order_relaxed);
  snapshot.direct_us = entry.direct_us.load(std::memory_order_relaxed);
  snapshot.submit_us = entry.submit_us.load(std::memory_order_relaxed);
  snapshot.present_us = entry.present_us.load(std::memory_order_relaxed);
  return snapshot;
}

void StorePlumeBackendDebugSnapshotLocked() {
  g_plume_backend_debug_status.store(
      static_cast<uint32_t>(g_plume_backend.status), std::memory_order_relaxed);
  g_plume_backend_debug_api.store(static_cast<uint32_t>(g_plume_backend.api),
                                  std::memory_order_relaxed);
  g_plume_backend_debug_attempted.store(g_plume_backend.attempted,
                                        std::memory_order_relaxed);

#if REFII_RENDERER_HAS_PLUME
  g_plume_backend_debug_direct_queue_ready.store(
      g_plume_backend.direct_queue != nullptr, std::memory_order_relaxed);
  g_plume_backend_debug_copy_queue_ready.store(
      g_plume_backend.copy_queue != nullptr, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_ready.store(
      g_plume_backend.swap_chain != nullptr, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_present_wait_enabled.store(
      g_plume_backend.swap_chain_present_wait_enabled,
      std::memory_order_relaxed);
  g_plume_backend_debug_next_frame_context.store(
      g_plume_backend.next_frame_context, std::memory_order_relaxed);
  g_plume_backend_debug_frame_context_count.store(
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size()),
      std::memory_order_relaxed);
  g_plume_backend_debug_release_semaphore_count.store(
      static_cast<uint32_t>(g_plume_backend.release_semaphores.size()),
      std::memory_order_relaxed);
  g_plume_backend_debug_framebuffer_count.store(
      static_cast<uint32_t>(g_plume_backend.framebuffers.size()),
      std::memory_order_relaxed);
  g_plume_backend_debug_depth_framebuffer_count.store(
      static_cast<uint32_t>(g_plume_backend.depth_framebuffers.size()),
      std::memory_order_relaxed);

  std::string device_name;
  uint64_t dedicated_video_memory = 0;
  if (g_plume_backend.device) {
    const plume::RenderDeviceDescription& description =
        g_plume_backend.device->getDescription();
    device_name = description.name;
    dedicated_video_memory = description.dedicatedVideoMemory;
  }
  g_plume_backend_debug_dedicated_video_memory.store(
      dedicated_video_memory, std::memory_order_relaxed);
  {
    std::lock_guard<std::mutex> name_lock(
        g_plume_backend_debug_device_name_mutex);
    g_plume_backend_debug_device_name.fill('\0');
    const size_t copy_size =
        std::min<size_t>(device_name.size(),
                         kPlumeBackendDebugDeviceNameCapacity - 1);
    std::memcpy(g_plume_backend_debug_device_name.data(),
                device_name.data(), copy_size);
  }
#else
  g_plume_backend_debug_direct_queue_ready.store(false,
                                                 std::memory_order_relaxed);
  g_plume_backend_debug_copy_queue_ready.store(false, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_ready.store(false, std::memory_order_relaxed);
  g_plume_backend_debug_swapchain_present_wait_enabled.store(
      false, std::memory_order_relaxed);
  g_plume_backend_debug_next_frame_context.store(0, std::memory_order_relaxed);
  g_plume_backend_debug_frame_context_count.store(0, std::memory_order_relaxed);
  g_plume_backend_debug_release_semaphore_count.store(
      0, std::memory_order_relaxed);
  g_plume_backend_debug_framebuffer_count.store(0, std::memory_order_relaxed);
  g_plume_backend_debug_depth_framebuffer_count.store(
      0, std::memory_order_relaxed);
  g_plume_backend_debug_dedicated_video_memory.store(
      0, std::memory_order_relaxed);
  {
    std::lock_guard<std::mutex> name_lock(
        g_plume_backend_debug_device_name_mutex);
    g_plume_backend_debug_device_name.fill('\0');
  }
#endif
}

#if REFII_RENDERER_HAS_PLUME
bool DrainPlumeBackendGpuLocked();

void StorePlumeOutputSnapshotLocked() {
  StorePlumeBackendDebugSnapshotLocked();
  if (!g_plume_backend.swap_chain) {
    g_plume_output_ready.store(false, std::memory_order_relaxed);
    g_plume_output_vsync_enabled.store(false, std::memory_order_relaxed);
    g_plume_output_width.store(0, std::memory_order_relaxed);
    g_plume_output_height.store(0, std::memory_order_relaxed);
    g_plume_output_texture_count.store(0, std::memory_order_relaxed);
    return;
  }

  g_plume_output_ready.store(true, std::memory_order_relaxed);
  g_plume_output_vsync_enabled.store(
      g_plume_backend.swap_chain->isVsyncEnabled(), std::memory_order_relaxed);
  g_plume_output_width.store(g_plume_backend.swap_chain->getWidth(),
                             std::memory_order_relaxed);
  g_plume_output_height.store(g_plume_backend.swap_chain->getHeight(),
                              std::memory_order_relaxed);
  g_plume_output_texture_count.store(
      g_plume_backend.swap_chain->getTextureCount(), std::memory_order_relaxed);
}

void ResetPlumeComposeTargetsLocked() {
  for (PlumeComposeTarget& target : g_plume_backend.compose_targets) {
    target.color_texture.reset();
    target.framebuffer.reset();
    target.depth_framebuffer.reset();
    target.last_use_frame_context = UINT32_MAX;
    target.recorded = false;
  }
  g_plume_backend.next_compose_target = 0;
  g_plume_backend.last_recorded_compose_target = UINT32_MAX;
  g_plume_backend.last_record_present_target = 0;
}

void ResetPlumeOutputLocked() {
  DrainPlumeBackendGpuLocked();
  StorePlumeOutputSnapshotLocked();
  g_plume_backend.framebuffers.clear();
  g_plume_backend.depth_framebuffers.clear();
  g_plume_backend.depth_texture.reset();
  ResetPlumeComposeTargetsLocked();
  g_plume_backend.release_semaphores.clear();
  g_plume_backend.logged_direct_command_stream = false;
  g_plume_backend.logged_submitted_command_stream = false;
  g_plume_backend.logged_present_pump_pending_command_stream = false;
  g_plume_backend.logged_reused_identical_fable_frame = false;
  g_plume_backend.logged_preserved_fable_direct_output = false;
  g_plume_backend.has_fable_direct_output_to_preserve = false;
  g_plume_backend.frame_contexts.clear();
  g_plume_backend.next_frame_context = 0;
  g_plume_backend.swap_chain.reset();
  g_plume_backend.swap_chain_present_wait_enabled = false;
  StorePlumeOutputSnapshotLocked();
}

bool WaitPlumeFrameContextLocked(PlumeFrameContext& frame_context) {
  if (g_plume_backend.direct_queue && frame_context.fence &&
      frame_context.submitted) {
    const bool completed = g_plume_backend.direct_queue->waitForCommandFence(
        frame_context.fence.get());
    frame_context.submitted = false;
    return completed;
  }
  return true;
}

bool WaitPlumeFrameFencesLocked() {
  bool completed = true;
  for (PlumeFrameContext& frame_context : g_plume_backend.frame_contexts) {
    completed = WaitPlumeFrameContextLocked(frame_context) && completed;
  }
  return completed;
}

bool DrainPlumeBackendGpuLocked() {
  bool completed = WaitPlumeFrameFencesLocked();
  if (g_plume_backend.direct_queue) {
    completed = g_plume_backend.direct_queue->waitIdle() && completed;
  }
  if (g_plume_backend.copy_queue) {
    completed = g_plume_backend.copy_queue->waitIdle() && completed;
  }
  if (g_plume_backend.swap_chain &&
      g_plume_backend.swap_chain_present_wait_enabled) {
    g_plume_backend.swap_chain->wait();
  }
  return completed;
}

bool CreatePlumeFrameContextsLocked() {
  if (!g_plume_backend.device || !g_plume_backend.direct_queue ||
      !g_plume_backend.swap_chain) {
    return false;
  }

  WaitPlumeFrameFencesLocked();
  g_plume_backend.frame_contexts.clear();
  g_plume_backend.next_frame_context = 0;
  const uint32_t context_count =
      std::max<uint32_t>(2, g_plume_backend.swap_chain->getTextureCount());
  g_plume_backend.frame_contexts.reserve(context_count);
  for (uint32_t i = 0; i < context_count; ++i) {
    PlumeFrameContext& frame_context =
        g_plume_backend.frame_contexts.emplace_back();
    frame_context.command_list =
        g_plume_backend.direct_queue->createCommandList();
    frame_context.acquire_semaphore =
        g_plume_backend.device->createCommandSemaphore();
    frame_context.fence = g_plume_backend.device->createCommandFence();
    if (!frame_context.command_list || !frame_context.acquire_semaphore ||
        !frame_context.fence) {
      g_plume_backend.frame_contexts.clear();
      return false;
    }
  }
  return true;
}

bool GetCurrentGameWindow(plume::RenderWindow& render_window) {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->display_window()) {
    REXLOG_WARN(
        "Refii plume output could not find the current game window yet");
    return false;
  }

  rex::ui::Window* window = runtime->display_window();
#if defined(_WIN64)
  void* hwnd = window->GetNativeWindowHandle();
  if (!hwnd) {
    REXLOG_WARN("Refii plume output window does not have an HWND yet");
    return false;
  }
  render_window = static_cast<HWND>(hwnd);
  return true;
#elif defined(PLUME_SDL_VULKAN_ENABLED) && defined(__linux__)
  // The stable-tree shadow exposes SDLWindow::window(); the netplay SDK's
  // WindowSDL exposes sdl_window() (shadow-added accessor).
#if defined(REFII_REXGLUE_NETPLAY) && REFII_REXGLUE_NETPLAY
  auto* sdl_window = dynamic_cast<rex::ui::WindowSDL*>(window);
  SDL_Window* sdl_window_handle = sdl_window ? sdl_window->sdl_window() : nullptr;
#else
  auto* sdl_window = dynamic_cast<rex::ui::SDLWindow*>(window);
  SDL_Window* sdl_window_handle = sdl_window ? sdl_window->window() : nullptr;
#endif
  if (!sdl_window_handle) {
    REXLOG_WARN(
        "Refii plume/vulkan output could not access the ReXGlue SDL window");
    return false;
  }
  render_window = sdl_window_handle;
  REXLOG_INFO("Refii plume/vulkan output selected ReXGlue SDL window");
  return true;
#elif REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE
  void* native_display = window->GetNativeDisplayHandle();
  void* native_window = window->GetNativeWindowHandle();
  if (!native_display || !native_window) {
    REXLOG_WARN(
        "Refii plume/vulkan output could not access the ReXGlue X11 window "
        "handoff handles");
    return false;
  }

  switch (window->GetNativeWindowSystem()) {
    case rex::ui::Window::NativeWindowSystem::kX11:
      render_window = plume::RenderWindow::XlibWindow(
          static_cast<Display*>(native_display),
          static_cast<::Window>(reinterpret_cast<uintptr_t>(native_window)));
      REXLOG_INFO(
          "Refii plume/vulkan output selected ReXGlue window XID=0x{:X}",
          static_cast<uint64_t>(render_window.window));
      return true;
    case rex::ui::Window::NativeWindowSystem::kWayland:
      render_window = plume::RenderWindow::WaylandWindow(
          native_display, native_window, window->GetActualPhysicalWidth(),
          window->GetActualPhysicalHeight());
      REXLOG_INFO(
          "Refii plume/vulkan output selected ReXGlue Wayland surface");
      return true;
    default:
      REXLOG_WARN(
          "Refii plume/vulkan output found an unsupported ReXGlue native "
          "window system");
      return false;
  }
#else
  (void)window;
  REXLOG_WARN(
      "Refii plume output is not wired for this platform/window system yet");
  return false;
#endif
}

bool RecreatePlumeFramebuffersLocked() {
  DrainPlumeBackendGpuLocked();
  g_plume_backend.framebuffers.clear();
  g_plume_backend.depth_framebuffers.clear();
  g_plume_backend.depth_texture.reset();
  ResetPlumeComposeTargetsLocked();
  if (!g_plume_backend.swap_chain || !g_plume_backend.device) {
    return false;
  }

  const uint32_t width = g_plume_backend.swap_chain->getWidth();
  const uint32_t height = g_plume_backend.swap_chain->getHeight();
  if (width == 0 || height == 0) {
    return false;
  }

  g_plume_backend.depth_texture =
      g_plume_backend.device->createTexture(plume::RenderTextureDesc::DepthTarget(
          width, height, plume::RenderFormat::D32_FLOAT_S8_UINT));
  if (!g_plume_backend.depth_texture) {
    REXLOG_WARN(
        "Refii plume output could not create swapchain depth target {}x{}",
        width, height);
    return false;
  }
  g_plume_backend.depth_texture->setName("Refii Plume Output Depth");

  const uint32_t texture_count = g_plume_backend.swap_chain->getTextureCount();
  for (uint32_t i = 0; i < texture_count; ++i) {
    plume::RenderTexture* color_attachment =
        g_plume_backend.swap_chain->getTexture(i);
    if (color_attachment) {
      color_attachment->setName("Refii Plume Output Swapchain " +
                                std::to_string(i));
    }
    const plume::RenderTexture* color_attachment_const = color_attachment;
    plume::RenderFramebufferDesc framebuffer_desc;
    framebuffer_desc.colorAttachments = &color_attachment_const;
    framebuffer_desc.colorAttachmentsCount = 1;
    g_plume_backend.framebuffers.emplace_back(
        g_plume_backend.device->createFramebuffer(framebuffer_desc));
    if (!g_plume_backend.framebuffers.back()) {
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      return false;
    }

    plume::RenderFramebufferDesc depth_framebuffer_desc;
    depth_framebuffer_desc.colorAttachments = &color_attachment_const;
    depth_framebuffer_desc.colorAttachmentsCount = 1;
    depth_framebuffer_desc.depthAttachment = g_plume_backend.depth_texture.get();
    g_plume_backend.depth_framebuffers.emplace_back(
        g_plume_backend.device->createFramebuffer(depth_framebuffer_desc));
    if (!g_plume_backend.depth_framebuffers.back()) {
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      return false;
    }
  }

  // Present-decoupling step 2: the offscreen compose targets, same size and
  // format as the swapchain images (created as B8G8R8A8_UNORM in
  // EnsurePlumeOutputLocked). Recreated with the framebuffers on any resize,
  // which also clears the recorded flags so the first post-resize present
  // takes the record path.
  for (size_t i = 0; i < g_plume_backend.compose_targets.size(); ++i) {
    PlumeComposeTarget& target = g_plume_backend.compose_targets[i];
    target.color_texture =
        g_plume_backend.device->createTexture(plume::RenderTextureDesc::ColorTarget(
            width, height, plume::RenderFormat::B8G8R8A8_UNORM));
    if (!target.color_texture) {
      REXLOG_WARN(
          "Refii plume output could not create compose target {} {}x{}", i,
          width, height);
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      ResetPlumeComposeTargetsLocked();
      return false;
    }
    target.color_texture->setName("Refii Plume Compose Target " +
                                  std::to_string(i));
    const plume::RenderTexture* compose_attachment = target.color_texture.get();
    plume::RenderFramebufferDesc compose_framebuffer_desc;
    compose_framebuffer_desc.colorAttachments = &compose_attachment;
    compose_framebuffer_desc.colorAttachmentsCount = 1;
    target.framebuffer =
        g_plume_backend.device->createFramebuffer(compose_framebuffer_desc);

    plume::RenderFramebufferDesc compose_depth_framebuffer_desc;
    compose_depth_framebuffer_desc.colorAttachments = &compose_attachment;
    compose_depth_framebuffer_desc.colorAttachmentsCount = 1;
    compose_depth_framebuffer_desc.depthAttachment =
        g_plume_backend.depth_texture.get();
    target.depth_framebuffer = g_plume_backend.device->createFramebuffer(
        compose_depth_framebuffer_desc);
    if (!target.framebuffer || !target.depth_framebuffer) {
      REXLOG_WARN(
          "Refii plume output could not create compose framebuffers {} {}x{}",
          i, width, height);
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      ResetPlumeComposeTargetsLocked();
      return false;
    }
  }

  return true;
}

bool EnsurePlumeOutputLocked() {
  if (!g_plume_backend.device || !g_plume_backend.direct_queue) {
    return false;
  }

  if (!g_plume_backend.swap_chain) {
    plume::RenderWindow render_window = {};
    if (!GetCurrentGameWindow(render_window)) {
      return false;
    }

    const uint32_t texture_count = RendererPlumeSwapchainImageCount();
    const bool enable_present_wait =
        g_plume_backend.device->getCapabilities().presentWait;
    g_plume_backend.swap_chain = g_plume_backend.direct_queue->createSwapChain(
        plume::RenderSwapChainDesc(render_window,
                                   plume::RenderFormat::B8G8R8A8_UNORM,
                                   texture_count, enable_present_wait,
                                   texture_count));
    g_plume_backend.swap_chain_present_wait_enabled =
        g_plume_backend.swap_chain && enable_present_wait;
    if (g_plume_backend.swap_chain) {
      g_plume_backend.swap_chain->setVsyncEnabled(RendererPlumeVsyncEnabled());
    }
    if (!g_plume_backend.swap_chain || !g_plume_backend.swap_chain->resize()) {
      REXLOG_WARN(
          "Refii plume/{} output could not create a swapchain for the selected "
          "output window",
          PlumeApiBridgeName(g_plume_backend.api));
      ResetPlumeOutputLocked();
      return false;
    }

    if (!CreatePlumeFrameContextsLocked() ||
        !RecreatePlumeFramebuffersLocked()) {
      REXLOG_WARN("Refii plume output could not create frame resources");
      ResetPlumeOutputLocked();
      return false;
    }

    StorePlumeOutputSnapshotLocked();
    REXLOG_INFO(
        "Refii plume/{} output attached to selected output window "
        "vsync={} present_wait={} requested_images={} actual_images={}",
        PlumeApiBridgeName(g_plume_backend.api),
        g_plume_backend.swap_chain->isVsyncEnabled(),
        g_plume_backend.swap_chain_present_wait_enabled, texture_count,
        g_plume_backend.swap_chain->getTextureCount());
    return true;
  }

  g_plume_backend.swap_chain->setVsyncEnabled(RendererPlumeVsyncEnabled());
  if (g_plume_backend.swap_chain->getTextureCount() !=
      RendererPlumeSwapchainImageCount()) {
    DrainPlumeBackendGpuLocked();
    ResetPlumeOutputLocked();
    return EnsurePlumeOutputLocked();
  }

  if (g_plume_backend.swap_chain->needsResize()) {
    DrainPlumeBackendGpuLocked();
    g_plume_backend.framebuffers.clear();
    g_plume_backend.depth_framebuffers.clear();
    g_plume_backend.depth_texture.reset();
    if (!g_plume_backend.swap_chain->resize() ||
        !CreatePlumeFrameContextsLocked() ||
        !RecreatePlumeFramebuffersLocked()) {
      REXLOG_WARN("Refii plume output swapchain resize failed");
      ResetPlumeOutputLocked();
      return false;
    }
    StorePlumeOutputSnapshotLocked();
  }

  StorePlumeOutputSnapshotLocked();
  return true;
}
#endif

}  // namespace

const char* PlumeBackendStatusName(PlumeBackendStatus status) {
  switch (status) {
    case PlumeBackendStatus::kUnavailable:
      return "unavailable";
    case PlumeBackendStatus::kUnsupportedApi:
      return "unsupported_api";
    case PlumeBackendStatus::kInterfaceUnavailable:
      return "interface_unavailable";
    case PlumeBackendStatus::kDeviceUnavailable:
      return "device_unavailable";
    case PlumeBackendStatus::kQueueUnavailable:
      return "queue_unavailable";
    case PlumeBackendStatus::kReady:
      return "ready";
  }

  return "unknown";
}

const char* PlumeFableCommandSourceName(PlumeFableCommandSource source) {
  switch (source) {
    case PlumeFableCommandSource::kNone:
      return "none";
    case PlumeFableCommandSource::kStaged:
      return "staged";
    case PlumeFableCommandSource::kPresented:
      return "presented";
    case PlumeFableCommandSource::kSubmitted:
      return "submitted";
  }

  return "unknown";
}

void RequestPlumeBackendShutdown() {
  g_plume_backend_shutdown_requested.store(true, std::memory_order_release);
  g_plume_async_present_pending.store(false, std::memory_order_release);
}

void ShutdownPlumeBackend() {
  RequestPlumeBackendShutdown();
#if REFII_RENDERER_HAS_PLUME && REFII_SDL3_WINDOW_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    CallPlumeOnUIThreadSynchronous([]() { ShutdownPlumeBackend(); });
    return;
  }
#endif

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
#if REFII_RENDERER_HAS_PLUME
  DrainPlumeBackendGpuLocked();
  ShutdownPlumeImGuiOverlay();
  ShutdownPlumeDiagnostics();
  ShutdownFableDirectRenderer();
  ResetPlumeOutputLocked();
  g_plume_backend.copy_queue.reset();
  g_plume_backend.direct_queue.reset();
  g_plume_backend.device.reset();
  g_plume_backend.render_interface.reset();
#endif
  g_plume_backend.attempted = true;
  g_plume_backend.api = GetRendererGraphicsApi();
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  g_plume_backend.logged_hook_observer = false;
  StorePlumeBackendDebugSnapshotLocked();
}

PlumeBackendStatus EnsurePlumeBackendLocal() {
  if (g_plume_backend_shutdown_requested.load(std::memory_order_acquire)) {
    return PlumeBackendStatus::kUnavailable;
  }
  if (!PlumeRendererBackendEnabled()) {
    return PlumeBackendStatus::kUnavailable;
  }

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  const RendererGraphicsApi api = GetRendererGraphicsApi();
  if (g_plume_backend.attempted && g_plume_backend.api == api) {
    return g_plume_backend.status;
  }

  g_plume_backend.attempted = true;
  g_plume_backend.api = api;
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  g_plume_backend.logged_hook_observer = false;
  StorePlumeBackendDebugSnapshotLocked();

#if REFII_RENDERER_HAS_PLUME
  DrainPlumeBackendGpuLocked();
  ShutdownPlumeImGuiOverlay();
  ShutdownPlumeDiagnostics();
  ShutdownFableDirectRenderer();
  ResetPlumeOutputLocked();
  g_plume_backend.copy_queue.reset();
  g_plume_backend.direct_queue.reset();
  g_plume_backend.device.reset();
  g_plume_backend.render_interface.reset();

  if (!PlumeApiBridgeSupported(api)) {
    g_plume_backend.status = PlumeBackendStatus::kUnsupportedApi;
    StorePlumeBackendDebugSnapshotLocked();
    REXLOG_WARN(
        "Refii plume/{} backend was requested, but this build target does "
        "not support that API. Select renderer_backend=xenia to use the "
        "original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  InitializeRenderDocDebugger();
  RefreshRendererShaderLibrary();
  PreparePlumeApiBridge(api);
  g_plume_backend.render_interface = CreatePlumeApiInterface(api);
  if (!g_plume_backend.render_interface) {
    g_plume_backend.status = PlumeBackendStatus::kInterfaceUnavailable;
    StorePlumeBackendDebugSnapshotLocked();
    REXLOG_WARN(
        "Refii plume/{} backend could not create a Plume interface. Select "
        "renderer_backend=xenia to use the original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.device =
      g_plume_backend.render_interface->createDevice(std::string());
  if (!g_plume_backend.device) {
    g_plume_backend.status = PlumeBackendStatus::kDeviceUnavailable;
    StorePlumeBackendDebugSnapshotLocked();
    REXLOG_WARN(
        "Refii plume/{} backend could not create a Plume device. Select "
        "renderer_backend=xenia to use the original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.direct_queue = g_plume_backend.device->createCommandQueue(
      plume::RenderCommandListType::DIRECT);
  if (!g_plume_backend.direct_queue) {
    g_plume_backend.status = PlumeBackendStatus::kQueueUnavailable;
    StorePlumeBackendDebugSnapshotLocked();
    REXLOG_WARN(
        "Refii plume/{} backend could not create a direct command queue. "
        "Select renderer_backend=xenia to use the original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.copy_queue =
      g_plume_backend.device->createCommandQueue(plume::RenderCommandListType::COPY);
  if (!g_plume_backend.copy_queue) {
    REXLOG_WARN(
        "Refii plume/{} backend could not create a copy command queue. "
        "Uploads will use the direct queue until the backend is expanded.",
        PlumeApiBridgeName(api));
  }

  const plume::RenderDeviceDescription& description =
      g_plume_backend.device->getDescription();
  InitializeFableDirectRenderer(
      g_plume_backend.device.get(),
      g_plume_backend.render_interface->getCapabilities().shaderFormat);
  g_plume_backend.status = PlumeBackendStatus::kReady;
  StorePlumeBackendDebugSnapshotLocked();
  REXLOG_INFO(
      "Refii plume/{} backend initialized device=\"{}\" vram={} MiB "
      "direct_queue=ready copy_queue={}",
      PlumeApiBridgeName(api), description.name,
      description.dedicatedVideoMemory / (1024 * 1024),
      g_plume_backend.copy_queue ? "ready" : "direct_queue_uploads");
  return g_plume_backend.status;
#else
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  StorePlumeBackendDebugSnapshotLocked();
  return g_plume_backend.status;
#endif
}


bool PlumeOutputReady() {
#if REFII_RENDERER_HAS_PLUME
#if REFII_SDL3_WINDOW_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    bool ready = false;
    if (!CallPlumeOnUIThreadSynchronous(
            [&ready]() { ready = PlumeOutputReady(); })) {
      return false;
    }
    return ready;
  }
#endif

  if (EnsurePlumeBackendLocal() != PlumeBackendStatus::kReady) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  return EnsurePlumeOutputLocked();
#else
  return false;
#endif
}

uint32_t CurrentPlumeOutputFrameIndex() {
  return g_plume_output_present_count.load(std::memory_order_relaxed);
}

// Live swapchain extent, lock-free. This is the resolution we actually present
// at, which is what "render at the requested resolution" has to mean -- the
// video_mode_width/height cvars DEFAULT to 1280x720 (rexglue window.cpp), so
// reading them alone makes the render-surface promotion a no-op on any machine
// that never set them, which is why the title stayed 720p. Cheap enough for the
// per-resource decode path: two relaxed atomic loads, no lock, no snapshot copy.
bool CurrentPlumeOutputExtent(uint32_t &width, uint32_t &height) {
  const uint32_t w = g_plume_output_width.load(std::memory_order_relaxed);
  const uint32_t h = g_plume_output_height.load(std::memory_order_relaxed);
  if (w == 0 || h == 0) {
    return false;
  }
  width = w;
  height = h;
  return true;
}

PlumeBackendDebugSnapshot GetPlumeBackendDebugSnapshot() {
  PlumeBackendDebugSnapshot snapshot;
  snapshot.status = static_cast<PlumeBackendStatus>(
      g_plume_backend_debug_status.load(std::memory_order_relaxed));
  snapshot.api = static_cast<RendererGraphicsApi>(
      g_plume_backend_debug_api.load(std::memory_order_relaxed));
  snapshot.attempted =
      g_plume_backend_debug_attempted.load(std::memory_order_relaxed);
  snapshot.direct_queue_ready =
      g_plume_backend_debug_direct_queue_ready.load(std::memory_order_relaxed);
  snapshot.copy_queue_ready =
      g_plume_backend_debug_copy_queue_ready.load(std::memory_order_relaxed);
  snapshot.swapchain_ready =
      g_plume_backend_debug_swapchain_ready.load(std::memory_order_relaxed);
  snapshot.swapchain_present_wait_enabled =
      g_plume_backend_debug_swapchain_present_wait_enabled.load(
          std::memory_order_relaxed);
  snapshot.next_frame_context =
      g_plume_backend_debug_next_frame_context.load(std::memory_order_relaxed);
  snapshot.frame_context_count =
      g_plume_backend_debug_frame_context_count.load(std::memory_order_relaxed);
  snapshot.release_semaphore_count =
      g_plume_backend_debug_release_semaphore_count.load(
          std::memory_order_relaxed);
  snapshot.framebuffer_count =
      g_plume_backend_debug_framebuffer_count.load(std::memory_order_relaxed);
  snapshot.depth_framebuffer_count =
      g_plume_backend_debug_depth_framebuffer_count.load(
          std::memory_order_relaxed);
  snapshot.dedicated_video_memory =
      g_plume_backend_debug_dedicated_video_memory.load(
          std::memory_order_relaxed);
  {
    std::lock_guard<std::mutex> name_lock(
        g_plume_backend_debug_device_name_mutex);
    snapshot.device_name = g_plume_backend_debug_device_name;
  }
  return snapshot;
}

PlumeOutputSnapshot GetPlumeOutputSnapshot() {
  PlumeOutputSnapshot snapshot;
  snapshot.configured_vsync = RendererPlumeVsyncEnabled();
  snapshot.configured_texture_count = RendererPlumeSwapchainImageCount();
  snapshot.last_marshal_us =
      g_last_plume_marshal_us.load(std::memory_order_relaxed);
  snapshot.last_frame_total_us =
      g_last_plume_frame_total_us.load(std::memory_order_relaxed);
  snapshot.last_backend_us =
      g_last_plume_backend_us.load(std::memory_order_relaxed);
  snapshot.last_output_us =
      g_last_plume_output_us.load(std::memory_order_relaxed);
  snapshot.last_consume_us =
      g_last_plume_consume_us.load(std::memory_order_relaxed);
  snapshot.last_sync_us =
      g_last_plume_sync_us.load(std::memory_order_relaxed);
  snapshot.last_readiness_us =
      g_last_plume_readiness_us.load(std::memory_order_relaxed);
  snapshot.last_acquire_us =
      g_last_plume_acquire_us.load(std::memory_order_relaxed);
  snapshot.last_acquire_texture_us =
      g_last_plume_acquire_texture_us.load(std::memory_order_relaxed);
  snapshot.last_setup_us =
      g_last_plume_setup_us.load(std::memory_order_relaxed);
  snapshot.last_imgui_build_us =
      g_last_plume_imgui_build_us.load(std::memory_order_relaxed);
  snapshot.last_direct_us =
      g_last_plume_direct_us.load(std::memory_order_relaxed);
  snapshot.last_imgui_render_us =
      g_last_plume_imgui_render_us.load(std::memory_order_relaxed);
  snapshot.last_command_record_us =
      g_last_plume_command_record_us.load(std::memory_order_relaxed);
  snapshot.last_submit_us =
      g_last_plume_submit_us.load(std::memory_order_relaxed);
  snapshot.last_present_us =
      g_last_plume_present_us.load(std::memory_order_relaxed);
  snapshot.last_fence_wait_us =
      g_last_plume_fence_wait_us.load(std::memory_order_relaxed);
  snapshot.ready = g_plume_output_ready.load(std::memory_order_relaxed);
  snapshot.vsync_enabled =
      g_plume_output_vsync_enabled.load(std::memory_order_relaxed);
  snapshot.width = g_plume_output_width.load(std::memory_order_relaxed);
  snapshot.height = g_plume_output_height.load(std::memory_order_relaxed);
  snapshot.texture_count =
      g_plume_output_texture_count.load(std::memory_order_relaxed);
  snapshot.present_count =
      g_plume_output_present_count.load(std::memory_order_relaxed);
  snapshot.last_fable_render_commands =
      g_last_plume_fable_render_commands.load(std::memory_order_relaxed);
  snapshot.last_fable_draw_events =
      g_last_plume_fable_draw_events.load(std::memory_order_relaxed);
  snapshot.last_fable_command_source = static_cast<PlumeFableCommandSource>(
      g_last_plume_fable_command_source.load(std::memory_order_relaxed));
  snapshot.last_fable_direct_source = static_cast<PlumeFableCommandSource>(
      g_last_plume_fable_direct_source.load(std::memory_order_relaxed));
  snapshot.last_fable_draw_events_fresh =
      g_last_plume_fable_draw_events_fresh.load(std::memory_order_relaxed);
  return snapshot;
}

PlumeFrameHistorySnapshot GetPlumeFrameHistorySnapshot() {
  PlumeFrameHistorySnapshot snapshot;
  const uint64_t written =
      g_plume_frame_history_write_count.load(std::memory_order_acquire);
  const uint32_t available = static_cast<uint32_t>(
      std::min<uint64_t>(written, kPlumeFrameHistoryCapacity));
  for (uint32_t index = 0; index < available; ++index) {
    const uint64_t history_index = written - 1u - index;
    const AtomicPlumeFrameHistoryEntry& source =
        g_plume_frame_history[history_index % kPlumeFrameHistoryCapacity];
    PlumeFrameHistoryEntry entry = LoadPlumeFrameHistoryEntry(source);
    if (!entry.valid) {
      continue;
    }
    snapshot.entries[snapshot.count++] = entry;
  }
  return snapshot;
}

bool PresentPlumeFrame(bool drive_fable_frame) {
  ZoneNamedN(tracy_frame_zone, "RefiiPlume/PresentFrame", true);
  if (g_plume_backend_shutdown_requested.load(std::memory_order_acquire)) {
    return false;
  }
  if (!RendererReplacePresentEnabled()) {
    return false;
  }
#if REFII_RENDERER_HAS_PLUME
  ConfigurePlumeBackendTracyPlots();
#if REFII_SDL3_WINDOW_ENABLED
  // Present-decoupling step 5 (2026-07-30): the real present body stays on
  // the UI thread DELIBERATELY. The Vulkan side would be safe from the pump
  // thread — swapchain, surface, queue and every frame resource are touched
  // only under g_plume_backend.mutex, resize is handled inside the present
  // body via swap_chain->needsResize(), and vkQueuePresentKHR has no
  // thread-affinity requirement — but the ImGui overlay is not: it renders on
  // the SHARED rexglue ImGui context (EnsureImGuiContext ->
  // g_rexglue_imgui_drawer->context()), and that context's io state is
  // written lock-free by the UI thread's input listeners (ImGuiDrawer
  // OnMouseDown/OnMouseMove/OnKey*, imgui_drawer.cpp). NewFrame/Render racing
  // those writes is a data race today only prevented by both running on the
  // UI thread. So instead of moving the present, the single-slot coalescer is
  // re-armed when the queued present STARTS executing (cvar-gated below):
  // back-to-back presents pipeline (at most one queued behind the one
  // executing) instead of dropping every request that arrives mid-execution.
  // The UI-loop wake per request already exists — CallInUIThreadDeferred
  // pushes an SDL user event (SDLWindowedAppContext::
  // NotifyUILoopOfPendingFunctions -> SDL_PushEvent).
  if (ShouldMarshalPlumeToUIThread()) {
    ZoneScopedN("RefiiPlume/PresentFrameMarshalToUI");
    if (drive_fable_frame) {
      g_plume_present_census_requests.fetch_add(1, std::memory_order_relaxed);
      bool expected = false;
      if (!g_plume_async_present_pending.compare_exchange_strong(
              expected, true, std::memory_order_acq_rel)) {
        g_plume_present_census_coalesced_dropped.fetch_add(
            1, std::memory_order_relaxed);
        if (!g_logged_plume_async_present_coalesced.exchange(
                true, std::memory_order_relaxed)) {
          REXLOG_INFO(
              "Refii plume present pump is coalescing pending UI-thread "
              "presents");
        }
        return false;
      }

      const bool pipeline_presents = RendererPresentUIThreadPipelineEnabled();
      if (!CallPlumeOnUIThreadDeferred([drive_fable_frame,
                                        pipeline_presents]() {
            if (pipeline_presents) {
              // Release the slot at execution START so the next pump wake can
              // queue one present behind this one; anything faster than that
              // still coalesces into the single queued slot.
              g_plume_async_present_pending.store(false,
                                                  std::memory_order_release);
            }
            if (!g_plume_backend_shutdown_requested.load(
                    std::memory_order_acquire)) {
              PresentPlumeFrame(drive_fable_frame);
            }
            if (!pipeline_presents) {
              g_plume_async_present_pending.store(false,
                                                  std::memory_order_release);
            }
          })) {
        g_plume_async_present_pending.store(false, std::memory_order_release);
        return false;
      }

      if (!g_logged_plume_async_present.exchange(true,
                                                 std::memory_order_relaxed)) {
        REXLOG_INFO(
            "Refii plume present pump is queuing coalesced async UI-thread "
            "presents");
      }
      return true;
    }

    const PlumeClock::time_point marshal_t0 = PlumeClock::now();
    bool presented = false;
    if (!CallPlumeOnUIThreadSynchronous(
            [&presented, drive_fable_frame]() {
              presented = PresentPlumeFrame(drive_fable_frame);
            })) {
      return false;
    }
    const double marshal_ms = PlumeElapsedMs(marshal_t0, PlumeClock::now());
    g_last_plume_marshal_us.store(static_cast<int64_t>(marshal_ms * 1000.0),
                                  std::memory_order_relaxed);
    if (marshal_ms > 25.0) {
      REXLOG_INFO("Refii plume UI-thread present marshal took {:.3f} ms",
                  marshal_ms);
    }
    return presented;
  }
#endif

  const PlumeClock::time_point frame_t0 = PlumeClock::now();
  {
    ZoneScopedN("RefiiPlume/EnsureBackend");
    if (EnsurePlumeBackendLocal() != PlumeBackendStatus::kReady) {
      return false;
    }
  }
  const PlumeClock::time_point backend_t1 = PlumeClock::now();

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  const plume::RenderShaderFormat shader_format =
      g_plume_backend.render_interface->getCapabilities().shaderFormat;
  {
    ZoneScopedN("RefiiPlume/EnsureOutput");
    if (!EnsurePlumeOutputLocked()) {
      return false;
    }
  }
  const PlumeClock::time_point output_t1 = PlumeClock::now();
  PlumeFableCommandSource command_source = PlumeFableCommandSource::kNone;
  FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  {
    ZoneScopedN("RefiiPlume/ReadDirectState");
    const bool staged_has_render_work =
        direct_snapshot.staged_clear_command_count != 0 ||
        direct_snapshot.staged_resolve_command_count != 0 ||
        std::any_of(direct_snapshot.staged_draw_source_counts.begin(),
                    direct_snapshot.staged_draw_source_counts.end(),
                    [](uint32_t count) { return count != 0; });
    if (direct_snapshot.submitted_render_command_count != 0) {
      command_source = PlumeFableCommandSource::kSubmitted;
    } else if (staged_has_render_work) {
      command_source = PlumeFableCommandSource::kStaged;
    }
  }
  g_last_plume_fable_render_commands.store(
      static_cast<uint32_t>(std::min<uint64_t>(
          direct_snapshot.render_command_count, UINT32_MAX)),
      std::memory_order_relaxed);
  const uint32_t executed_draw_command_count =
      static_cast<uint32_t>(std::min<uint64_t>(
          direct_snapshot.draw_count, UINT32_MAX));
  g_last_plume_fable_draw_events.store(
      executed_draw_command_count, std::memory_order_relaxed);
  g_last_plume_fable_draw_events_fresh.store(true,
                                             std::memory_order_relaxed);
  g_last_plume_fable_command_source.store(
      static_cast<uint32_t>(command_source), std::memory_order_relaxed);
  g_last_plume_fable_direct_source.store(static_cast<uint32_t>(command_source),
                                         std::memory_order_relaxed);
  const PlumeClock::time_point consume_t1 = PlumeClock::now();
  const PlumeClock::time_point sync_t1 = PlumeClock::now();
  const PlumeClock::time_point readiness_t1 = PlumeClock::now();

  const bool renderdoc_ready_to_capture =
      direct_snapshot.draw_count != 0 ||
      direct_snapshot.indexed_draw_count != 0 ||
      direct_snapshot.last_submitted_draw_event_count != 0 ||
      direct_snapshot.last_command_boundary_staged_draws != 0;
  const uint32_t renderdoc_capture_frame_index =
      g_plume_backend.present_count + 1u;
  const uint32_t renderdoc_capture_frame = RenderDocCaptureFrameIndex();
  if (renderdoc_capture_frame != 0 &&
      renderdoc_capture_frame_index == renderdoc_capture_frame) {
    RequestRenderDocFrameCapture();
  }
  const uint32_t renderdoc_capture_draw_index = RenderDocCaptureDrawIndex();
  if (renderdoc_capture_draw_index == 0) {
    g_plume_backend.last_renderdoc_draw_capture = 0;
    g_plume_backend.logged_renderdoc_draw_watch = false;
  } else if (g_plume_backend.last_renderdoc_draw_capture !=
             renderdoc_capture_draw_index) {
    if (!g_plume_backend.logged_renderdoc_draw_watch) {
      REXLOG_INFO("Refii plume RenderDoc draw watch active draw={}",
                  renderdoc_capture_draw_index);
      g_plume_backend.logged_renderdoc_draw_watch = true;
    }
    constexpr size_t kMaxRenderDocDrawCommandProbe = 4096;
    if (FableDirectQueuedDrawPresent(renderdoc_capture_draw_index,
                                     kMaxRenderDocDrawCommandProbe)) {
      if (RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_capture =
            renderdoc_capture_draw_index;
        REXLOG_INFO(
            "Refii plume RenderDoc draw capture armed draw={} present={}",
            renderdoc_capture_draw_index, renderdoc_capture_frame_index);
      }
    }
  }
  const std::string renderdoc_capture_draw_target =
      RenderDocDeepObserverCaptureDrawTarget();
  if (renderdoc_capture_draw_target.empty()) {
    g_plume_backend.last_renderdoc_draw_target_capture.clear();
    g_plume_backend.logged_renderdoc_draw_target_watch = false;
  } else if (g_plume_backend.last_renderdoc_draw_target_capture !=
                 renderdoc_capture_draw_target) {
    std::string_view renderdoc_hook_root_token;
    if (!g_plume_backend.logged_renderdoc_draw_target_watch) {
      REXLOG_INFO("Refii plume RenderDoc draw-target watch active target={}",
                  renderdoc_capture_draw_target);
      g_plume_backend.logged_renderdoc_draw_target_watch = true;
    }
    if (IsRenderDocResolveCaptureTarget(renderdoc_capture_draw_target)) {
      REXLOG_INFO(
          "Refii plume RenderDoc resolve-target watch is record-time only "
          "target={}",
          renderdoc_capture_draw_target);
      g_plume_backend.last_renderdoc_draw_target_capture =
          renderdoc_capture_draw_target;
    } else if (IsRenderDocSurfaceDestinationCaptureTarget(
                   renderdoc_capture_draw_target)) {
      REXLOG_INFO(
          "Refii plume RenderDoc surface-destination target watch is "
          "record-time only target={}",
          renderdoc_capture_draw_target);
      g_plume_backend.last_renderdoc_draw_target_capture =
          renderdoc_capture_draw_target;
    } else if (IsRenderDocRootCoverageCaptureTarget(
                   renderdoc_capture_draw_target)) {
      if (renderdoc_ready_to_capture && RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc root-coverage capture armed target={} "
            "present={}",
            renderdoc_capture_draw_target, renderdoc_capture_frame_index);
      }
    } else if (TryGetRenderDocHookRootCaptureToken(
                   renderdoc_capture_draw_target,
                   renderdoc_hook_root_token)) {
      if (RenderDocHookRootTargetActive(renderdoc_hook_root_token) &&
          RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc hook-root capture armed target={} "
            "root={} present={}",
            renderdoc_capture_draw_target, renderdoc_hook_root_token,
            renderdoc_capture_frame_index);
      }
    } else if (QueuedCommandsContainRenderDocDrawTarget(
                   renderdoc_capture_draw_target)) {
      if (RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc draw-target capture armed target={} "
            "present={}",
            renderdoc_capture_draw_target, renderdoc_capture_frame_index);
      }
    }
  }
  const std::string renderdoc_capture_pass = RenderDocCapturePassName();
  if (renderdoc_capture_pass.empty()) {
    g_plume_backend.last_renderdoc_pass_capture.clear();
    g_plume_backend.logged_renderdoc_pass_watch = false;
  } else if (g_plume_backend.last_renderdoc_pass_capture !=
             renderdoc_capture_pass) {
    if (!g_plume_backend.logged_renderdoc_pass_watch) {
      REXLOG_INFO("Refii plume RenderDoc pass watch active pass={}",
                  renderdoc_capture_pass);
      g_plume_backend.logged_renderdoc_pass_watch = true;
    }
    constexpr size_t kMaxRenderDocPassCommandProbe = 4096;
    if (g_plume_backend.renderdoc_pass_capture_repeats != 0) {
      if (RequestRenderDocFrameCapture()) {
        --g_plume_backend.renderdoc_pass_capture_repeats;
        if (g_plume_backend.renderdoc_pass_capture_repeats == 0) {
          g_plume_backend.last_renderdoc_pass_capture = renderdoc_capture_pass;
        }
        REXLOG_INFO(
            "Refii plume RenderDoc pass capture repeat pass={} present={} "
            "remaining={}",
            renderdoc_capture_pass, renderdoc_capture_frame_index,
            g_plume_backend.renderdoc_pass_capture_repeats);
      }
    } else if (FableDirectQueuedRenderPassPresent(
                   renderdoc_capture_pass, kMaxRenderDocPassCommandProbe)) {
      if (RequestRenderDocFrameCapture()) {
        // Capture 3 consecutive frames; the queued-command probe runs one
        // frame out of phase with execution.
        g_plume_backend.renderdoc_pass_capture_repeats = 2;
        REXLOG_INFO(
            "Refii plume RenderDoc pass capture armed pass={} present={}",
            renderdoc_capture_pass, renderdoc_capture_frame_index);
      }
    }
  }
  const PlumeRenderDocAutoCaptureArm renderdoc_auto_capture =
      MaybeArmPlumeRenderDocAutoContentCapture(
          renderdoc_capture_frame_index, renderdoc_ready_to_capture,
          renderdoc_capture_frame, renderdoc_capture_draw_index,
          renderdoc_capture_draw_target, renderdoc_capture_pass);
  const std::string_view renderdoc_effective_capture_draw_target =
      renderdoc_capture_draw_target.empty()
          ? std::string_view(renderdoc_auto_capture.draw_target)
          : std::string_view(renderdoc_capture_draw_target);
  const std::string renderdoc_capture_reason = RenderDocCaptureReason();
  const std::string renderdoc_capture_file_tag =
      BuildRenderDocCaptureFileTag(renderdoc_capture_frame_index,
                                   renderdoc_capture_draw_index,
                                   renderdoc_effective_capture_draw_target,
                                   renderdoc_capture_pass,
                                   renderdoc_capture_reason);
  const bool renderdoc_capture_file_tag_pending =
      RenderDocCaptureRequestPending() || RenderDocTriggerCaptureRequestPending();
  if (renderdoc_capture_file_tag_pending) {
    SetRenderDocFrameCaptureFileTag(renderdoc_capture_file_tag.c_str());
  }
  // Present-decoupling step 3: decide record vs re-present BEFORE the capture
  // begins or any fable frame state is touched. Record whenever anything could
  // change the composed output — a new guest boundary, drainable/carried GTL
  // packets (FableDirectFrameRecordReason), fresh guest-side deferred texel
  // captures or Bink uploads (generation compares against the last record) —
  // or when RenderDoc wants this present (a capture of a blit-only frame is
  // useless). The present itself is NEVER skipped (run-328 post-mortem): the
  // fast path replaces only the record with a blit of the last completed
  // compose target; guest-servicing work stays on every wake.
  const uint64_t present_bink_upload_count = PlumeBinkVideoUploadReadyCount();
  const uint64_t present_deferred_capture_generation =
      DeferredTextureCaptureEnqueueGeneration();
  PlumeComposeTarget* held_compose_target =
      g_plume_backend.last_recorded_compose_target <
              g_plume_backend.compose_targets.size()
          ? &g_plume_backend
                 .compose_targets[g_plume_backend.last_recorded_compose_target]
          : nullptr;
  const bool renderdoc_wants_present =
      renderdoc_capture_file_tag_pending || RenderDocFrameCaptureActive();
  // First matching reason wins; kPlumeRecordWhyCount means none — fast path.
  size_t record_why = kPlumeRecordWhyCount;
  if (held_compose_target == nullptr || !held_compose_target->recorded ||
      held_compose_target->color_texture == nullptr) {
    record_why = kPlumeRecordWhyNoCache;
  } else if (renderdoc_wants_present) {
    record_why = kPlumeRecordWhyRenderDoc;
  } else if (present_bink_upload_count !=
             g_plume_backend.last_record_bink_upload_count) {
    record_why = kPlumeRecordWhyBink;
  } else {
    if (present_deferred_capture_generation !=
        g_plume_backend.last_record_deferred_capture_generation) {
      // Captures ONLY re-record after a minimum interval (run 461: the guest
      // enqueues captures spread across its whole frame, so a ~100ms record
      // always overlapped a fresh enqueue and captures-caused records ran
      // back-to-back — zero blit windows, presents capped at 1/record-time).
      // This bounds capture staleness at ~one guest frame, the already-
      // accepted risk. Boundary/bink/video reasons stay immediate below —
      // real new frames never wait.
      constexpr std::chrono::milliseconds kPlumeCapturesRerecordMinInterval{
          100};
      if (g_plume_backend.last_record_completed_at ==
              PlumeClock::time_point{} ||
          PlumeClock::now() - g_plume_backend.last_record_completed_at >
              kPlumeCapturesRerecordMinInterval) {
        record_why = kPlumeRecordWhyCaptures;
      } else {
        g_plume_present_census_captures_deferred.fetch_add(
            1, std::memory_order_relaxed);
      }
    }
    if (record_why == kPlumeRecordWhyCount) {
      switch (FableDirectFrameRecordReason()) {
        case FableDirectRecordReason::kNone:
          break;
        case FableDirectRecordReason::kNotAuthoritative:
          record_why = kPlumeRecordWhyMode;
          break;
        case FableDirectRecordReason::kStartupVideoPump:
          record_why = kPlumeRecordWhyVideo;
          break;
        case FableDirectRecordReason::kGuestBoundary:
          record_why = kPlumeRecordWhyBoundary;
          break;
        case FableDirectRecordReason::kCarriedPackets:
          record_why = kPlumeRecordWhyCarried;
          break;
        case FableDirectRecordReason::kPendingPackets:
          record_why = kPlumeRecordWhyPackets;
          break;
      }
    }
  }
  const bool skip_record = record_why == kPlumeRecordWhyCount;
  if (renderdoc_ready_to_capture && ConsumeRenderDocTriggerCaptureRequest()) {
    TriggerRenderDocCapture();
    REXLOG_INFO(
        "Refii plume RenderDoc-owned capture trigger consumed present={} "
        "tag={}",
        renderdoc_capture_frame_index, renderdoc_capture_file_tag);
  }
  const bool renderdoc_capture_started =
      BeginRenderDocFrameCapture("Refii Plume Frame",
                                 renderdoc_ready_to_capture);
  auto end_renderdoc_started_capture = [&]() {
    EndRenderDocFrameCapture(renderdoc_capture_started);
    if (renderdoc_capture_started) {
      ClearRenderDocCaptureReason();
    }
  };

  if (g_plume_backend.frame_contexts.empty()) {
    end_renderdoc_started_capture();
    return false;
  }
  const uint32_t frame_context_index =
      g_plume_backend.next_frame_context %
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size());
  PlumeFrameContext& frame_context =
      g_plume_backend.frame_contexts[frame_context_index];
  g_plume_backend.next_frame_context =
      (frame_context_index + 1) %
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size());
  const PlumeClock::time_point frame_context_wait_t0 = PlumeClock::now();
  {
    ZoneScopedN("RefiiPlume/WaitReusableFrameContext");
    WaitPlumeFrameContextLocked(frame_context);
  }
  // Step 2 compose-target rotation. On the record path, take the next
  // alternating offscreen target and wait out the frame context that last
  // used it: the direct queue is in-order so a later fence covers earlier
  // submissions, and this is what lets a re-present blit of target A overlap
  // a later record into target B without ever racing A's read.
  PlumeComposeTarget* compose_record_target = nullptr;
  if (!skip_record) {
    const uint32_t compose_index =
        g_plume_backend.next_compose_target %
        static_cast<uint32_t>(g_plume_backend.compose_targets.size());
    compose_record_target = &g_plume_backend.compose_targets[compose_index];
    if (compose_record_target->last_use_frame_context <
        g_plume_backend.frame_contexts.size()) {
      WaitPlumeFrameContextLocked(
          g_plume_backend
              .frame_contexts[compose_record_target->last_use_frame_context]);
    }
    if (!compose_record_target->color_texture ||
        !compose_record_target->framebuffer ||
        !compose_record_target->depth_framebuffer) {
      end_renderdoc_started_capture();
      return false;
    }
  }
  const PlumeClock::time_point frame_context_wait_t1 = PlumeClock::now();

  uint32_t image_index = 0;
  {
    ZoneScopedN("RefiiPlume/AcquireTexture");
    if (!g_plume_backend.swap_chain->acquireTexture(
            frame_context.acquire_semaphore.get(), &image_index)) {
      end_renderdoc_started_capture();
      return false;
    }
  }
  const PlumeClock::time_point acquire_t1 = PlumeClock::now();

  if (image_index >= g_plume_backend.framebuffers.size()) {
    end_renderdoc_started_capture();
    return false;
  }
  if (g_plume_backend.depth_texture &&
      image_index >= g_plume_backend.depth_framebuffers.size()) {
    end_renderdoc_started_capture();
    return false;
  }
  // Step 2: the fable frame records into the offscreen compose target, never
  // the acquired swapchain image; the swapchain image receives the compose by
  // blit below, then the diagnostics/ImGui overlay exactly as before. Null on
  // the step-3 fast path, which records no fable frame at all.
  plume::RenderFramebuffer* fable_output_framebuffer = nullptr;
  if (compose_record_target) {
    fable_output_framebuffer =
        g_plume_backend.depth_texture
            ? compose_record_target->depth_framebuffer.get()
            : compose_record_target->framebuffer.get();
  }

  plume::RenderTexture* swap_chain_texture =
      g_plume_backend.swap_chain->getTexture(image_index);
  plume::RenderCommandList* command_list = frame_context.command_list.get();
  if (!command_list) {
    end_renderdoc_started_capture();
    return false;
  }
  const bool render_imgui_overlay = RendererImGuiEnabled();
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/EnsureImGuiRenderer");
    EnsurePlumeImGuiRenderer(g_plume_backend.device.get(), shader_format);
  }
  command_list->begin();
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/RecordImGuiUploads");
    PlumeDebugLabelScope imgui_upload_scope(command_list,
                                            "RefiiPlume::ImGuiUploads");
    RecordPlumeImGuiUploads(command_list);
  }
  if (!g_plume_backend.logged_frame_after_uploads) {
    REXLOG_INFO("Refii plume frame recorded pending uploads");
    g_plume_backend.logged_frame_after_uploads = true;
  }
  if (compose_record_target) {
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS,
        plume::RenderTextureBarrier(compose_record_target->color_texture.get(),
                                    plume::RenderTextureLayout::COLOR_WRITE));
    if (g_plume_backend.depth_texture) {
      command_list->barriers(
          plume::RenderBarrierStage::GRAPHICS,
          plume::RenderTextureBarrier(g_plume_backend.depth_texture.get(),
                                      plume::RenderTextureLayout::DEPTH_WRITE));
    }
    command_list->setFramebuffer(fable_output_framebuffer);
    if (!g_plume_backend.logged_frame_after_color_target) {
      REXLOG_INFO("Refii plume frame attached offscreen compose color target");
      g_plume_backend.logged_frame_after_color_target = true;
    }
  }

  const uint32_t width = g_plume_backend.swap_chain->getWidth();
  const uint32_t height = g_plume_backend.swap_chain->getHeight();
  const uint32_t frame_index = ++g_plume_backend.present_count;
  PlumeDebugLabelScope frame_debug_label(
      command_list, BuildFrameDebugScopeLabel(frame_index, width, height));
  g_plume_output_present_count.store(frame_index, std::memory_order_relaxed);
  const PlumeClock::time_point setup_t1 = PlumeClock::now();
  if (compose_record_target) {
    BeginFableDirectFrame(frame_index);
    direct_snapshot = GetFableDirectStateSnapshot();
  }
  const bool log_frame_summary =
      compose_record_target != nullptr &&
      (!g_plume_backend.logged_first_frame_summary ||
       (RendererVerboseTracingEnabled() && frame_index % 120 == 0));
  if (log_frame_summary) {
    const RendererHookObserverSnapshot hook_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererHookTimingSnapshot timing_snapshot =
        GetRendererHookTimingSnapshot();
    const FableDirectStateSnapshot direct_snapshot =
        GetFableDirectStateSnapshot();
    REXLOG_INFO(
        "Refii plume frame summary frame={} output={}x{} live={} vb={} ib={} "
        "shaders={} passes={} flushes={} dispatches={} dispatch_targets={} "
        "queue_callbacks={} queue_targets={} command_writes={} "
        "command_submits={} command_words={} pm4_type0={} pm4_type3={} "
        "pm4_draw={} pm4_indirect={} pm4_constants={} pm4_swap={} "
        "pm4_errors={} shader_uploads={} fetch_uploads={} shader_nz={}/{} "
        "shader_upload_nz={} shader_zero={} shader_skip={} "
        "last_shader={}:r{}+{} dwords={} nz={} src=0x{:08X} lr=0x{:08X} "
        "data={:08X}/{:08X}/{:08X}/{:08X} fetch_nz={} "
        "display_source={} display_base=0x{:08X} display_object=0x{:08X} "
        "display_size={}x{} display_fmt={} display_reg=0x{:04X}/0x{:08X} "
        "draws={} indexed={} executed_draws={} dropped_draws={} "
        "scene_walkers={}/{}/{} scene_slots={} last_scene=0x{:08X}/{} "
        "first=0x{:08X} result=0x{:08X} scene_batches={}/{}/{} "
        "scene_bridge={}/{}/{} scene_inline={} "
        "title_lists={}/{}/{} nodes={} last_title=0x{:08X} "
        "object=0x{:08X}/vt=0x{:08X} node=0x{:08X}/vt=0x{:08X} "
        "title_world={}/0x{:08X} registry=0x{:08X} count={} matched=0x{:08X}/{} "
        "cmd_source={} exec_source={} cmd_counts={}/{} "
        "submit_serial={} boundary={}/{}/{} submit_pending={}/{} "
        "submit_draws={} "
        "ready_vb={} ready_ib={} ready_decl={} ready_vs={} ready_ps={} "
        "fable_present_hooks={} plume_pump_requests={} "
        "plume_pump_fable_advances={} fable_submitted={} fable_completed={} "
        "fable_callbacks={} locks={}/{} "
        "flush_lr=0x{:08X} last_pm4=0x{:08X}/op=0x{:02X} "
        "last_draw={}/prim:{} base:{} start:{} count:{} vb0=0x{:08X}/{} "
        "ib=0x{:08X} decl=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        frame_index, width, height, hook_snapshot.live_resources,
        hook_snapshot.live_vertex_buffers, hook_snapshot.live_index_buffers,
        hook_snapshot.live_shaders, hook_snapshot.render_passes,
        hook_snapshot.state_flushes, hook_snapshot.state_dispatches,
        hook_snapshot.state_dispatch_targets,
        hook_snapshot.render_queue_callbacks,
        hook_snapshot.render_queue_callback_targets,
        hook_snapshot.command_writes, hook_snapshot.command_submits,
        hook_snapshot.command_words, hook_snapshot.pm4_type0_packets,
        hook_snapshot.pm4_type3_packets, hook_snapshot.pm4_draw_packets,
        hook_snapshot.pm4_indirect_packets, hook_snapshot.pm4_constant_packets,
        hook_snapshot.pm4_swap_packets, hook_snapshot.pm4_parse_errors,
        hook_snapshot.shader_constant_uploads,
        hook_snapshot.fetch_constant_uploads,
        hook_snapshot.vertex_shader_constant_nonzero_dwords,
        hook_snapshot.pixel_shader_constant_nonzero_dwords,
        hook_snapshot.shader_constant_upload_nonzero_dwords,
        hook_snapshot.shader_constant_zero_uploads,
        hook_snapshot.shader_constant_rejected_uploads,
        hook_snapshot.last_shader_constant_bank != 0u ? "ps" : "vs",
        hook_snapshot.last_shader_constant_first_register,
        hook_snapshot.last_shader_constant_first_component,
        hook_snapshot.last_shader_constant_dwords,
        hook_snapshot.last_shader_constant_nonzero_dwords,
        hook_snapshot.last_shader_constant_source,
        hook_snapshot.last_shader_constant_lr,
        hook_snapshot.last_shader_constant_preview[0],
        hook_snapshot.last_shader_constant_preview[1],
        hook_snapshot.last_shader_constant_preview[2],
        hook_snapshot.last_shader_constant_preview[3],
        hook_snapshot.fetch_constant_nonzero_dwords,
        hook_snapshot.display_present_source,
        hook_snapshot.display_present_texture_base,
        hook_snapshot.display_present_surface_object,
        hook_snapshot.display_present_width,
        hook_snapshot.display_present_height,
        hook_snapshot.display_present_format,
        hook_snapshot.display_present_register,
        hook_snapshot.display_present_register_value,
        hook_snapshot.draws, hook_snapshot.indexed_draws,
        executed_draw_command_count, hook_snapshot.dropped_draw_events,
        hook_snapshot.scene_walker_calls,
        hook_snapshot.scene_walker_flush_calls,
        hook_snapshot.scene_walker_nonempty_calls,
        hook_snapshot.scene_walker_object_slots_total,
        hook_snapshot.last_scene_walker_list,
        hook_snapshot.last_scene_walker_count,
        hook_snapshot.last_scene_walker_first,
        hook_snapshot.last_scene_walker_result,
        hook_snapshot.scene_draw_filter_calls,
        hook_snapshot.scene_batch_traversal_calls,
        hook_snapshot.scene_batch_bridge_calls,
        hook_snapshot.scene_batch_bridge_calls,
        hook_snapshot.scene_batch_bridge_rejected_calls,
        hook_snapshot.scene_batch_bridge_queued_draws,
        hook_snapshot.scene_inline_vertex_draws,
        hook_snapshot.title_render_list_dispatches,
        hook_snapshot.title_render_list_insert_callbacks,
        hook_snapshot.title_render_list_sorted_inserts,
        hook_snapshot.title_render_list_inserted_nodes,
        hook_snapshot.last_title_render_list,
        hook_snapshot.last_title_render_object,
        hook_snapshot.last_title_render_object_vtable,
        hook_snapshot.last_title_render_node,
        hook_snapshot.last_title_render_node_vtable,
        hook_snapshot.title_world_registry_observations,
        hook_snapshot.last_title_world,
        hook_snapshot.last_title_world_registry,
        hook_snapshot.last_title_world_registry_count,
        hook_snapshot.last_title_world_registry_matched_entry,
        hook_snapshot.last_title_world_registry_matched_object_count,
        PlumeFableCommandSourceName(command_source),
        PlumeFableCommandSourceName(command_source),
        direct_snapshot.render_command_count,
        direct_snapshot.submitted_render_command_count,
        direct_snapshot.submitted_render_command_serial,
        direct_snapshot.command_boundary_capture_count,
        direct_snapshot.empty_command_boundary_capture_count,
        direct_snapshot.last_command_boundary_submit,
        direct_snapshot.last_command_boundary_staged_commands,
        direct_snapshot.last_command_boundary_staged_draws,
        direct_snapshot.last_submitted_draw_event_count,
        direct_snapshot.input.streams[0].buffer != 0 ? 1u : 0u,
        direct_snapshot.input.index_buffer != 0 ? 1u : 0u,
        direct_snapshot.shaders.vertex_declaration != 0 ? 1u : 0u,
        direct_snapshot.shaders.vertex_shader != 0 ? 1u : 0u,
        direct_snapshot.shaders.pixel_shader != 0 ? 1u : 0u,
        hook_snapshot.presents, timing_snapshot.plume_present_pump_requests,
        timing_snapshot.plume_present_pump_fable_advances,
        timing_snapshot.fable_present_submitted,
        timing_snapshot.fable_present_completed,
        timing_snapshot.fable_swap_callbacks, hook_snapshot.resource_locks,
        hook_snapshot.resource_unlocks, hook_snapshot.last_state_flush_lr,
        hook_snapshot.last_command_first_word,
        hook_snapshot.last_command_type3_opcode,
	        hook_snapshot.last_draw.indexed
	            ? "indexed"
	            : "direct",
	        hook_snapshot.last_draw.primitive_type,
	        hook_snapshot.last_draw.base_vertex,
	        hook_snapshot.last_draw.start,
	        hook_snapshot.last_draw.primitive_count,
	        hook_snapshot.streams[0].buffer,
	        hook_snapshot.streams[0].stride,
	        hook_snapshot.index_buffer,
	        hook_snapshot.vertex_declaration,
	        hook_snapshot.vertex_shader,
	        hook_snapshot.pixel_shader);
    g_plume_backend.logged_first_frame_summary = true;
  }
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/BuildImGuiOverlay");
    DrawPlumeImGuiOverlay(width, height, frame_index);
  }
  const PlumeClock::time_point imgui_build_t1 = PlumeClock::now();
  if (render_imgui_overlay && !g_plume_backend.logged_frame_after_imgui_build) {
    REXLOG_INFO("Refii plume frame built ImGui draw data");
    g_plume_backend.logged_frame_after_imgui_build = true;
  }
  command_list->setViewports(
      plume::RenderViewport(0.0f, 0.0f, float(width), float(height)));
  command_list->setScissors(plume::RenderRect(0, 0, width, height));
  const bool preserve_direct_output =
      HasFableDirectFrameContent(direct_snapshot) ||
      g_plume_backend.has_fable_direct_output_to_preserve;
  if (compose_record_target && !preserve_direct_output) {
    PlumeDebugLabelScope clear_scope(command_list,
                                     "RefiiPlume::ClearOutput");
    command_list->clearColor(0, plume::RenderColor(0.0f, 0.0f, 0.0f, 1.0f));
    if (g_plume_backend.depth_texture) {
      command_list->clearDepthStencil(true, false, 1.0f, 0);
    }
  } else if (compose_record_target &&
             !g_plume_backend.logged_preserved_fable_direct_output) {
    REXLOG_INFO(
        "Refii plume preserving Fable direct output instead of pre-clearing "
        "the compose target draw_count={} commands={} submitted={} serial={}",
        direct_snapshot.draw_count, direct_snapshot.render_command_count,
        direct_snapshot.submitted_render_command_count,
        direct_snapshot.submitted_render_command_serial);
    g_plume_backend.logged_preserved_fable_direct_output = true;
  }
  FableDirectRendererStats direct_stats = {};
  if (compose_record_target) {
    direct_stats = ExecuteRefiiRenderGraphFrame(
        g_plume_backend.device.get(), command_list, fable_output_framebuffer,
        compose_record_target->color_texture.get(),
        compose_record_target->framebuffer.get(), shader_format,
        g_plume_backend.depth_texture ? plume::RenderFormat::D32_FLOAT_S8_UINT
                                      : plume::RenderFormat::UNKNOWN,
        frame_index, width, height);
    if (direct_stats.draw_count != 0 ||
        direct_stats.last_present_target != 0) {
      g_plume_backend.has_fable_direct_output_to_preserve = true;
    }
  }
  const bool renderdoc_owned_capture_active =
      renderdoc_capture_started || direct_stats.renderdoc_capture_started;
  const bool renderdoc_observed_capture_active = RenderDocFrameCaptureActive();
  const bool renderdoc_capture_active =
      renderdoc_owned_capture_active || renderdoc_observed_capture_active;
  if (renderdoc_observed_capture_active && !renderdoc_owned_capture_active) {
    REXLOG_INFO(
        "Refii plume observed external RenderDoc frame capture; writing frame "
        "database and hook trace sidecars frame={}",
        frame_index);
    if (direct_stats.draw_count == 0 && direct_stats.last_draw_events == 0 &&
        direct_stats.last_render_commands == 0 &&
        direct_stats.last_present_target != 0) {
      REXLOG_WARN(
          "Refii plume external RenderDoc capture is present-only; use a "
          "Refii-owned target/pass capture for title/Bink analysis frame={} "
          "present=0x{:08X} size={}x{}",
          frame_index, direct_stats.last_present_target,
          direct_stats.last_present_width, direct_stats.last_present_height);
    }
  }
  const bool renderdoc_record_time_target =
      IsRenderDocResolveCaptureTarget(renderdoc_effective_capture_draw_target) ||
      IsRenderDocSurfaceDestinationCaptureTarget(
          renderdoc_effective_capture_draw_target);
  const std::string_view renderdoc_sidecar_draw_target =
      renderdoc_record_time_target && !direct_stats.renderdoc_capture_started
          ? std::string_view()
          : renderdoc_effective_capture_draw_target;
  const PlumeClock::time_point direct_t1 = PlumeClock::now();
  const RendererHookObserverSnapshot frame_hook_snapshot =
      GetRendererHookObserverSnapshot();
  const std::string frame_correlation_label = BuildFrameCorrelationLabel(
      frame_index, width, height, direct_stats, frame_hook_snapshot);
  command_list->insertDebugLabel(frame_correlation_label);
  ZoneTextV(tracy_frame_zone, frame_correlation_label.c_str(),
            frame_correlation_label.size());
  if (renderdoc_capture_active) {
    const RendererHookTimingSnapshot renderdoc_timing_snapshot =
        GetRendererHookTimingSnapshot();
    const std::string frame_database_path = WriteRenderDocFrameDatabaseDump(
        frame_index, width, height, direct_stats, frame_hook_snapshot,
        direct_snapshot, frame_correlation_label, renderdoc_capture_draw_index,
        renderdoc_sidecar_draw_target, renderdoc_capture_pass,
        renderdoc_capture_reason, renderdoc_owned_capture_active,
        direct_stats.renderdoc_capture_started);
    const std::string hook_trace_path = WriteRenderDocHookTraceDump(
        frame_index, width, height, frame_correlation_label,
        renderdoc_capture_draw_index, renderdoc_sidecar_draw_target,
        renderdoc_capture_pass, renderdoc_capture_reason,
        renderdoc_owned_capture_active, direct_stats.renderdoc_capture_started);
    SetRenderDocFrameCaptureTitle(BuildRenderDocCaptureTitle(
                                      frame_correlation_label)
                                      .c_str());
    SetRenderDocFrameCaptureComments(
        BuildRenderDocCaptureComments(frame_index, width, height, direct_stats,
                                      frame_hook_snapshot,
                                      renderdoc_timing_snapshot,
                                      direct_snapshot, frame_database_path,
                                      hook_trace_path, frame_correlation_label,
                                      renderdoc_capture_draw_index,
                                      renderdoc_sidecar_draw_target,
                                      renderdoc_capture_pass,
                                      renderdoc_capture_reason)
            .c_str());
  }
  // Step 2 blit: offscreen compose -> acquired swapchain image. On the step-3
  // fast path the source is the last completed record; otherwise the target
  // recorded above. The ImGui dimmer + overlay and diagnostics then draw into
  // the swapchain image on top, exactly as they previously drew after the
  // scene.
  PlumeComposeTarget* compose_blit_source =
      compose_record_target ? compose_record_target : held_compose_target;
  {
    ZoneScopedN("RefiiPlume/ComposeBlit");
    PlumeDebugLabelScope blit_scope(command_list, "RefiiPlume::ComposeBlit");
    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(compose_blit_source->color_texture.get(),
                                    plume::RenderTextureLayout::COPY_SOURCE));
    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(swap_chain_texture,
                                    plume::RenderTextureLayout::COPY_DEST));
    command_list->copyTexture(swap_chain_texture,
                              compose_blit_source->color_texture.get());
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS,
        plume::RenderTextureBarrier(swap_chain_texture,
                                    plume::RenderTextureLayout::COLOR_WRITE));
  }
  command_list->setFramebuffer(g_plume_backend.framebuffers[image_index].get());
  {
    ZoneScopedN("RefiiPlume/RenderDiagnostics");
    PlumeDebugLabelScope diagnostics_scope(command_list,
                                           "RefiiPlume::Diagnostics");
    RenderPlumeDiagnostics(g_plume_backend.device.get(), command_list,
                           shader_format, frame_index, width, height);
  }
  if (direct_stats.draw_count != 0 && frame_index % 120 == 0) {
    REXLOG_INFO(
        "Refii plume direct renderer frame={} draws={} indexed={} events={} "
        "commands={} resources={}/{} pipeline={}/{} failed_hooks={} "
        "failed_resources={} "
        "target=0x{:08X} size={}x{} source={}",
        frame_index, direct_stats.draw_count, direct_stats.indexed_draw_count,
        direct_stats.last_draw_events, direct_stats.last_render_commands,
        direct_stats.resource_creations, direct_stats.resource_uploads,
        direct_stats.pipeline_cache_hits, direct_stats.pipeline_cache_misses,
        direct_stats.failed_hooks, direct_stats.failed_resource_syncs,
        direct_stats.last_present_target, direct_stats.last_present_width,
        direct_stats.last_present_height, PlumeFableCommandSourceName(command_source));
  }
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/RenderImGuiOverlay");
    PlumeDebugLabelScope imgui_scope(command_list,
                                     "RefiiPlume::ImGuiOverlay");
    RenderPlumeImGuiOverlay(g_plume_backend.device.get(), command_list);
  }
  const PlumeClock::time_point imgui_render_t1 = PlumeClock::now();
  if (render_imgui_overlay && !g_plume_backend.logged_frame_after_imgui_render) {
    REXLOG_INFO("Refii plume frame recorded ImGui overlay draws");
    g_plume_backend.logged_frame_after_imgui_render = true;
  }
  command_list->barriers(
      plume::RenderBarrierStage::NONE,
      plume::RenderTextureBarrier(swap_chain_texture,
                                  plume::RenderTextureLayout::PRESENT));
  frame_debug_label.End();
  command_list->end();
  const PlumeClock::time_point command_end_t1 = PlumeClock::now();

  while (g_plume_backend.release_semaphores.size() <= image_index) {
    g_plume_backend.release_semaphores.emplace_back(
        g_plume_backend.device->createCommandSemaphore());
  }

  const plume::RenderCommandList* submitted_list = command_list;
  plume::RenderCommandSemaphore* wait_semaphore =
      frame_context.acquire_semaphore.get();
  plume::RenderCommandSemaphore* signal_semaphore =
      g_plume_backend.release_semaphores[image_index].get();
  if (!g_plume_backend.logged_frame_before_submit) {
    REXLOG_INFO("Refii plume frame submitting command list");
    g_plume_backend.logged_frame_before_submit = true;
  }
  {
    ZoneScopedN("RefiiPlume/SubmitCommandList");
    frame_context.submitted = g_plume_backend.direct_queue->executeCommandLists(
        &submitted_list, 1, &wait_semaphore, 1, &signal_semaphore, 1,
        frame_context.fence.get());
  }
  const PlumeClock::time_point submit_t1 = PlumeClock::now();
  if (!frame_context.submitted) {
    EndRenderDocFrameCapture(renderdoc_owned_capture_active);
    if (renderdoc_capture_started) {
      ClearRenderDocCaptureReason();
    }
    return false;
  }
  // Step 2/3 bookkeeping: whichever compose target this frame used (recorded
  // into or blitted from) is now covered by this frame context's fence.
  if (compose_record_target) {
    compose_record_target->recorded = true;
    compose_record_target->last_use_frame_context = frame_context_index;
    g_plume_backend.last_recorded_compose_target = static_cast<uint32_t>(
        compose_record_target - g_plume_backend.compose_targets.data());
    g_plume_backend.next_compose_target =
        (g_plume_backend.last_recorded_compose_target + 1u) %
        static_cast<uint32_t>(g_plume_backend.compose_targets.size());
    g_plume_backend.last_record_present_target =
        direct_stats.last_present_target;
    g_plume_backend.last_record_bink_upload_count = present_bink_upload_count;
    // Stamp the capture generation POST-record (not the pre-record snapshot,
    // run 461): the record's own drain consumed everything queued before it,
    // and an enqueue that landed mid-record is absorbed here — it is at most
    // one captures-interval stale, not a reason for a back-to-back record.
    g_plume_backend.last_record_deferred_capture_generation =
        DeferredTextureCaptureEnqueueGeneration();
    g_plume_backend.last_record_completed_at = PlumeClock::now();
    g_plume_present_census_rerecords.fetch_add(1, std::memory_order_relaxed);
    if (record_why < kPlumeRecordWhyCount) {
      g_plume_present_record_why[record_why].fetch_add(
          1, std::memory_order_relaxed);
    }
  } else {
    compose_blit_source->last_use_frame_context = frame_context_index;
    g_plume_present_census_represents.fetch_add(1, std::memory_order_relaxed);
  }
  bool presented = false;
  {
    ZoneScopedN("RefiiPlume/SwapChainPresent");
    presented = g_plume_backend.swap_chain->present(
        image_index, &signal_semaphore, 1);
    if (presented) {
      g_plume_present_census_presented.fetch_add(1,
                                                 std::memory_order_relaxed);
    }
    if (presented && g_plume_backend.swap_chain_present_wait_enabled) {
      g_plume_backend.swap_chain->wait();
    }
  }
  const PlumeClock::time_point present_t1 = PlumeClock::now();
  // Fable display bookkeeping runs on EVERY present (run-328 post-mortem: the
  // guest's swap acknowledgement must never be gated on the record path); the
  // fast path re-states the last record's present target.
  PresentFableDirectFrame(compose_record_target
                              ? direct_stats.last_present_target
                              : g_plume_backend.last_record_present_target,
                          width, height);
  if (compose_record_target) {
    EndFableDirectFrame();
  }
  if (!g_plume_backend.logged_frame_after_present) {
    REXLOG_INFO("Refii plume frame present returned {}", presented);
    g_plume_backend.logged_frame_after_present = true;
  }
  if (!g_plume_backend.logged_frame_after_wait) {
    REXLOG_INFO(
        "Refii plume is using {} frame contexts before reusing command "
        "resources",
        g_plume_backend.frame_contexts.size());
    g_plume_backend.logged_frame_after_wait = true;
  }
  if (ShouldLogPlumeFrameTiming(frame_index)) {
    REXLOG_INFO(
        "Refii plume frame timing frame={} total_ms={:.3f} backend_ms={:.3f} "
        "output_ms={:.3f} consume_ms={:.3f} sync_ms={:.3f} "
        "readiness_ms={:.3f} acquire_ms={:.3f} setup_ms={:.3f} "
        "imgui_build_ms={:.3f} direct_ms={:.3f} imgui_render_ms={:.3f} "
        "cmd_end_ms={:.3f} submit_ms={:.3f} present_ms={:.3f} "
        "context_wait_ms={:.3f} direct_draws={} cmd_source={}",
        frame_index, PlumeElapsedMs(frame_t0, present_t1),
        PlumeElapsedMs(frame_t0, backend_t1),
        PlumeElapsedMs(backend_t1, output_t1),
        PlumeElapsedMs(output_t1, consume_t1),
        PlumeElapsedMs(consume_t1, sync_t1),
        PlumeElapsedMs(sync_t1, readiness_t1),
        PlumeElapsedMs(readiness_t1, acquire_t1),
        PlumeElapsedMs(acquire_t1, setup_t1),
        PlumeElapsedMs(setup_t1, imgui_build_t1),
        PlumeElapsedMs(imgui_build_t1, direct_t1),
        PlumeElapsedMs(direct_t1, imgui_render_t1),
        PlumeElapsedMs(imgui_render_t1, command_end_t1),
        PlumeElapsedMs(command_end_t1, submit_t1),
        PlumeElapsedMs(submit_t1, present_t1),
        PlumeElapsedMs(frame_context_wait_t0, frame_context_wait_t1),
        direct_stats.draw_count, PlumeFableCommandSourceName(command_source));
  }
  {
    // Step 1 census: time-limited to ~2s so every phase (menu 1-2ms composes
    // through gameplay ~100ms composes) gets a steady cadence. Cumulative
    // counters — read deltas between lines, not absolutes.
    static PlumeClock::time_point s_last_present_census_log{};
    const PlumeClock::time_point census_now = PlumeClock::now();
    if (s_last_present_census_log == PlumeClock::time_point{} ||
        census_now - s_last_present_census_log >= std::chrono::seconds(2)) {
      s_last_present_census_log = census_now;
      // STEP 6 gate census (cumulative, like every counter on this line):
      // checks = all gate evaluations, disarmed = ones skipped by the
      // publish-bootstrap/startup-video guards (armed = checks - disarmed;
      // run-513: counting checks after the guards made a video-phase segment
      // read as "gate never executed"), waits = ones that blocked, wait_ms =
      // total blocked guest-thread time, timeouts = bounded-wait expiries
      // (gate degraded to advisory), outstanding = present-captured boundary
      // lag at gate entry (max/last; 0-1 = depth held for free), boundary =
      // present_seq/captured_seq, publishes = record completions.
      const FableCaptureAckGateCensus ack_census =
          GetFableCaptureAckGateCensus();
      REXLOG_INFO(
          "Refii present census requests={} coalesced_dropped={} presented={} "
          "rerecords={} represents={} record_why[nocache={} renderdoc={} "
          "bink={} captures={} mode={} video={} boundary={} carried={} "
          "packets={}] captures_deferred={} capture_ack[checks={} disarmed={} "
          "waits={} wait_ms={} timeouts={} outstanding_max={} "
          "outstanding_last={} boundary={}/{} publishes={}]",
          g_plume_present_census_requests.load(std::memory_order_relaxed),
          g_plume_present_census_coalesced_dropped.load(
              std::memory_order_relaxed),
          g_plume_present_census_presented.load(std::memory_order_relaxed),
          g_plume_present_census_rerecords.load(std::memory_order_relaxed),
          g_plume_present_census_represents.load(std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyNoCache].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyRenderDoc].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyBink].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyCaptures].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyMode].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyVideo].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyBoundary].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyCarried].load(
              std::memory_order_relaxed),
          g_plume_present_record_why[kPlumeRecordWhyPackets].load(
              std::memory_order_relaxed),
          g_plume_present_census_captures_deferred.load(
              std::memory_order_relaxed),
          ack_census.checks, ack_census.disarms, ack_census.waits,
          ack_census.wait_us / 1000u, ack_census.timeouts,
          ack_census.max_outstanding, ack_census.last_outstanding,
          ack_census.present_boundary, ack_census.captured_boundary,
          ack_census.publishes);

      // GUEST RENDER STALL WATCHDOG (2026-08-02, save-load black screen).
      // Run 516/517 signature: the guest render thread executes a burst of
      // `twi 31,r0,22` (__debugbreak) and never issues another hooked D3D
      // call, so `rerecords` freezes while `represents` keeps climbing — the
      // pump re-presents one stale frame forever and the screen reads black.
      // `presented` keeps advancing in that state, so it is NOT a liveness
      // signal; rerecords (new guest content recorded) is the only one.
      //
      // On the transition into that state, emit exactly ONE line naming every
      // guest thread's last hooked symbol and how long ago it ran. The thread
      // whose age jumps to seconds while the others keep ticking is the one
      // that died, and its symbol is the last guest function we saw it in —
      // which is what pins the trap site. One-shot per stall episode; re-arms
      // if content publication resumes.
      static uint64_t s_watchdog_last_rerecords = 0u;
      static uint32_t s_watchdog_stalled_censuses = 0u;
      static bool s_watchdog_reported = false;
      const uint64_t watchdog_rerecords =
          g_plume_present_census_rerecords.load(std::memory_order_relaxed);
      if (watchdog_rerecords != s_watchdog_last_rerecords) {
        s_watchdog_last_rerecords = watchdog_rerecords;
        s_watchdog_stalled_censuses = 0u;
        s_watchdog_reported = false;
      } else if (watchdog_rerecords != 0u) {
        // Census cadence is ~2s, so 3 consecutive stalls is ~6s of zero new
        // guest content — far outside any legitimate compose or load hitch.
        ++s_watchdog_stalled_censuses;
        if (s_watchdog_stalled_censuses >= 3u && !s_watchdog_reported) {
          s_watchdog_reported = true;
          refii::renderer::RendererHookThreadTelemetryEntry threads[64];
          const size_t thread_count =
              refii::renderer::SnapshotRendererHookThreadTelemetry(
                  threads, std::size(threads));
          std::string autopsy;
          for (size_t i = 0; i < thread_count; ++i) {
            if (!autopsy.empty()) {
              autopsy += " | ";
            }
            autopsy += fmt::format("tid=0x{:08X} last_hook={} age_us={}",
                                   threads[i].guest_tid,
                                   threads[i].last_symbol != nullptr
                                       ? threads[i].last_symbol
                                       : "none",
                                   threads[i].us_since_enter);
          }
          REXLOG_WARN(
              "Refii GUEST RENDER STALLED: no new guest content for ~{}s "
              "(rerecords frozen at {}, represents={} presented={} still "
              "climbing = pump re-presenting a stale frame). Guest thread "
              "autopsy [{} threads]: {} -- the thread whose age_us is large "
              "while others tick is the stalled one; its last_hook is the "
              "last guest function it entered.",
              s_watchdog_stalled_censuses * 2u, watchdog_rerecords,
              g_plume_present_census_represents.load(std::memory_order_relaxed),
              g_plume_present_census_presented.load(std::memory_order_relaxed),
              thread_count, autopsy.empty() ? "(registry empty)" : autopsy);
        }
      }
    }
  }
  const int64_t frame_total_us = PlumeElapsedUs(frame_t0, present_t1);
  const int64_t backend_us = PlumeElapsedUs(frame_t0, backend_t1);
  const int64_t output_us = PlumeElapsedUs(backend_t1, output_t1);
  const int64_t consume_us = PlumeElapsedUs(output_t1, consume_t1);
  const int64_t sync_us = PlumeElapsedUs(consume_t1, sync_t1);
  const int64_t readiness_us = PlumeElapsedUs(sync_t1, readiness_t1);
  const int64_t acquire_us = PlumeElapsedUs(readiness_t1, acquire_t1);
  const int64_t acquire_texture_us =
      PlumeElapsedUs(frame_context_wait_t1, acquire_t1);
  const int64_t setup_us = PlumeElapsedUs(acquire_t1, setup_t1);
  const int64_t imgui_build_us = PlumeElapsedUs(setup_t1, imgui_build_t1);
  const int64_t direct_us = PlumeElapsedUs(imgui_build_t1, direct_t1);
  const int64_t imgui_render_us =
      PlumeElapsedUs(direct_t1, imgui_render_t1);
  const int64_t command_record_us =
      PlumeElapsedUs(imgui_render_t1, command_end_t1);
  const int64_t submit_us = PlumeElapsedUs(command_end_t1, submit_t1);
  const int64_t present_us = PlumeElapsedUs(submit_t1, present_t1);
  const int64_t fence_wait_us =
      PlumeElapsedUs(frame_context_wait_t0, frame_context_wait_t1);
  const RendererHookObserverSnapshot tracy_hook_snapshot =
      GetRendererHookObserverSnapshot();
  g_last_plume_frame_total_us.store(frame_total_us, std::memory_order_relaxed);
  g_last_plume_backend_us.store(backend_us, std::memory_order_relaxed);
  g_last_plume_output_us.store(output_us, std::memory_order_relaxed);
  g_last_plume_consume_us.store(consume_us, std::memory_order_relaxed);
  g_last_plume_sync_us.store(sync_us, std::memory_order_relaxed);
  g_last_plume_readiness_us.store(readiness_us, std::memory_order_relaxed);
  g_last_plume_acquire_us.store(acquire_us, std::memory_order_relaxed);
  g_last_plume_acquire_texture_us.store(acquire_texture_us,
                                        std::memory_order_relaxed);
  g_last_plume_setup_us.store(setup_us, std::memory_order_relaxed);
  g_last_plume_imgui_build_us.store(imgui_build_us, std::memory_order_relaxed);
  g_last_plume_direct_us.store(direct_us, std::memory_order_relaxed);
  g_last_plume_imgui_render_us.store(imgui_render_us,
                                     std::memory_order_relaxed);
  g_last_plume_command_record_us.store(command_record_us,
                                       std::memory_order_relaxed);
  g_last_plume_submit_us.store(submit_us, std::memory_order_relaxed);
  g_last_plume_present_us.store(present_us, std::memory_order_relaxed);
  g_last_plume_fence_wait_us.store(fence_wait_us, std::memory_order_relaxed);
  StorePlumeFrameHistoryEntry(frame_index, width, height, presented,
                              renderdoc_capture_active, direct_stats,
                              frame_total_us, direct_us, submit_us,
                              present_us);
  if (renderdoc_capture_active) {
    FableDirectRendererStats post_present_direct_stats = direct_stats;
    post_present_direct_stats.presented = presented;
    post_present_direct_stats.last_present_width = width;
    post_present_direct_stats.last_present_height = height;
    WriteRenderDocFrameDatabaseDump(
        frame_index, width, height, post_present_direct_stats,
        frame_hook_snapshot, direct_snapshot, frame_correlation_label,
        renderdoc_capture_draw_index, renderdoc_sidecar_draw_target,
        renderdoc_capture_pass, renderdoc_capture_reason,
        renderdoc_owned_capture_active, direct_stats.renderdoc_capture_started);
    WriteRenderDocHookTraceDump(
        frame_index, width, height, frame_correlation_label,
        renderdoc_capture_draw_index, renderdoc_sidecar_draw_target,
        renderdoc_capture_pass, renderdoc_capture_reason,
        renderdoc_owned_capture_active, direct_stats.renderdoc_capture_started);
  }
  ZoneValueV(tracy_frame_zone, frame_total_us);
  TracyPlot("Refii Plume Frame Total Us", frame_total_us);
  TracyPlot("Refii Plume Acquire Us", acquire_us);
  TracyPlot("Refii Plume Resource Sync Us", sync_us);
  TracyPlot("Refii Plume Readiness Us", readiness_us);
  TracyPlot("Refii Plume Command Us", direct_us);
  TracyPlot("Refii Plume ImGui Build Us", imgui_build_us);
  TracyPlot("Refii Plume ImGui Render Us", imgui_render_us);
  TracyPlot("Refii Plume Command Record Us", command_record_us);
  TracyPlot("Refii Plume Submit Us", submit_us);
  TracyPlot("Refii Plume Present Us", present_us);
  TracyPlot("Refii Plume Fence Wait Us", fence_wait_us);
  TracyPlot("Refii Plume Executed Draw Commands",
            static_cast<int64_t>(executed_draw_command_count));
  TracyPlot("Refii Plume Direct Draws",
            static_cast<int64_t>(direct_stats.draw_count));
  TracyPlot("Refii Plume Missing Resources", int64_t{0});
  TracyPlot("Refii Plume Missing Pipelines",
            static_cast<int64_t>(direct_stats.pipeline_cache_misses));
  TracyPlot("Refii Plume Texture Uploads",
            static_cast<int64_t>(direct_stats.resource_uploads));
  TracyPlot("Refii Plume Pending Texture Uploads", int64_t{0});
  TracyPlot("Refii Plume Pix Video Events",
            static_cast<int64_t>(tracy_hook_snapshot.pix_render_events));
  const RendererShaderLibrarySnapshot tracy_shader_snapshot =
      GetRendererShaderLibrarySnapshot();
  const int64_t tracy_pipeline_creates =
      static_cast<int64_t>(direct_stats.pipeline_cache_misses);
  const int64_t tracy_shader_misses =
      static_cast<int64_t>(tracy_shader_snapshot.missing_binary_count);
  const int64_t tracy_missing_textures =
      static_cast<int64_t>(direct_stats.failed_resource_syncs);
  const RefiiFramePacketQueueSnapshot tracy_packet_queue =
      GetRefiiFramePacketQueueSnapshot();
  TracyPlot("Refii.DrawPackets", static_cast<int64_t>(direct_stats.draw_count));
  TracyPlot("Refii.PacketQueueDepth",
            static_cast<int64_t>(tracy_packet_queue.packets.size()));
  TracyPlot("Refii.PacketActionQueueDepth",
            static_cast<int64_t>(tracy_packet_queue.actions.size()));
  TracyPlot("Refii.PacketReplayPlannedActions",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_action_packets));
  TracyPlot("Refii.PacketReplayPlannedCopies",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_copy_packets));
  TracyPlot("Refii.PacketReplayPlannedPresents",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_present_packets));
  TracyPlot("Refii.PacketReplayPlanned",
            static_cast<int64_t>(
                tracy_packet_queue.replay.planned_draw_packets));
  TracyPlot("Refii.PacketReplayUnsupported",
            static_cast<int64_t>(
                tracy_packet_queue.replay.unsupported_packets));
  TracyPlot("Refii.PipelineCreates", tracy_pipeline_creates);
  TracyPlot("Refii.PipelineCacheMisses",
            static_cast<int64_t>(direct_stats.pipeline_cache_misses));
  TracyPlot("Refii.ShaderMisses", tracy_shader_misses);
  TracyPlot("Refii.TextureUploads",
            static_cast<int64_t>(direct_stats.resource_uploads));
  TracyPlot("Refii.BufferUploads", int64_t{0});
  TracyPlot("Refii.MissingResources",
            static_cast<int64_t>(direct_stats.failed_resource_syncs));
  TracyPlot("Refii.MissingTextures", tracy_missing_textures);
  TracyPlot("Refii.MissingShaders", tracy_shader_misses);
  TracyPlot("Refii.RenderTargetSwitches",
            static_cast<int64_t>(direct_stats.render_target_switches));
  TracyPlot("Refii.DepthTargetSwitches",
            static_cast<int64_t>(direct_stats.depth_target_switches));
  TracyPlot("Refii.FrameDatabaseBytes",
            static_cast<int64_t>(FrameDatabasePacketBytesEstimate(direct_stats)));
  const FableDirectFrameDatabaseDrawPackets tracy_draw_packets =
      GetFableDirectFrameDatabaseDrawPackets();
  const FableDirectFrameDatabaseCommandPackets tracy_command_packets =
      GetFableDirectFrameDatabaseCommandPackets();
  const FrameDatabaseResourceNameIndex tracy_resource_name_index =
      BuildFrameDatabaseResourceNameIndex(GetFableDirectResourceDebugSnapshot());
  const std::vector<FrameDatabaseResourceEdge> tracy_resource_edges =
      BuildFrameDatabaseResourceEdges(tracy_command_packets);
  const FrameDatabaseResourceEdgeCounts tracy_resource_edge_counts =
      CountFrameDatabaseResourceEdges(tracy_resource_edges);
  TracyPlot("Refii.CommandPackets",
            static_cast<int64_t>(
                CountFrameDatabaseExecutableCommandPackets(
                    tracy_command_packets)));
  TracyPlot("Refii.ClearPackets",
            static_cast<int64_t>(
                CountFrameDatabaseCommandKind(tracy_command_packets, "clear")));
  TracyPlot(
      "Refii.ResolvePackets",
      static_cast<int64_t>(
          CountFrameDatabaseCommandKind(tracy_command_packets, "resolve")));
  TracyPlot("Refii.PresentBlitPackets",
            static_cast<int64_t>(CountFrameDatabaseCommandKind(
                tracy_command_packets, "present_blit")));
  TracyPlot("Refii.ResourceEdges",
            static_cast<int64_t>(tracy_resource_edge_counts.total_edges));
  TracyPlot("Refii.ClearResourceEdges",
            static_cast<int64_t>(tracy_resource_edge_counts.clear_edges));
  TracyPlot("Refii.ResolveResourceEdges",
            static_cast<int64_t>(tracy_resource_edge_counts.resolve_edges));
  TracyPlot(
      "Refii.SurfaceDestinationEdges",
      static_cast<int64_t>(
          tracy_resource_edge_counts.surface_destination_edges));
  TracyPlot("Refii.PresentBlitEdges",
            static_cast<int64_t>(
                tracy_resource_edge_counts.present_blit_edges));
  const std::vector<FrameDatabasePassResourceUsage> tracy_pass_resource_usages =
      BuildFrameDatabasePassResourceUsages(tracy_draw_packets,
                                           tracy_resource_edges);
  TracyPlot("Refii.PassResourceUsages",
            static_cast<int64_t>(tracy_pass_resource_usages.size()));
  const std::vector<FrameDatabasePassSummary> tracy_pass_summaries =
      BuildFrameDatabasePassSummaries(tracy_draw_packets,
                                      tracy_command_packets,
                                      tracy_resource_edges);
  TracyPlot("Refii.PassClassified",
            static_cast<int64_t>(
                CountFrameDatabaseClassifiedPassSummaries(
                    tracy_pass_summaries)));
  const std::vector<FrameDatabaseFrameResourceLifetime>
      tracy_frame_resource_lifetimes =
          BuildFrameDatabaseFrameResourceLifetimes(tracy_pass_summaries,
                                                  tracy_pass_resource_usages);
  TracyPlot("Refii.FrameResourceLifetimes",
            static_cast<int64_t>(tracy_frame_resource_lifetimes.size()));
  const std::vector<FrameDatabaseMaterialRecord> tracy_material_records =
      BuildFrameDatabaseMaterialRecords(tracy_draw_packets,
                                        tracy_resource_name_index);
  TracyPlot("Refii.MaterialRecords",
            static_cast<int64_t>(tracy_material_records.size()));
  const std::vector<FrameDatabaseMeshRecord> tracy_mesh_records =
      BuildFrameDatabaseMeshRecords(tracy_draw_packets,
                                    tracy_resource_name_index);
  TracyPlot("Refii.MeshRecords",
            static_cast<int64_t>(tracy_mesh_records.size()));
  const std::vector<FrameDatabaseAssetUsageRecord> tracy_asset_usage_records =
      BuildFrameDatabaseAssetUsageRecords(tracy_draw_packets,
                                          tracy_resource_name_index);
  TracyPlot("Refii.AssetUsages",
            static_cast<int64_t>(tracy_asset_usage_records.size()));
  const std::vector<FrameDatabasePassDependency> tracy_pass_dependencies =
      BuildFrameDatabasePassDependencies(tracy_pass_summaries,
                                         tracy_pass_resource_usages);
  TracyPlot("Refii.PassDependencies",
            static_cast<int64_t>(tracy_pass_dependencies.size()));
  const std::vector<FrameDatabasePassResourceTransition>
      tracy_pass_resource_transitions =
          BuildFrameDatabasePassResourceTransitions(tracy_pass_dependencies);
  TracyPlot("Refii.ResourceTransitions",
            static_cast<int64_t>(tracy_pass_resource_transitions.size()));
  const std::vector<FrameDatabaseRenderGraphNode> tracy_render_graph_nodes =
      BuildFrameDatabaseRenderGraphNodes(tracy_pass_summaries,
                                        tracy_pass_resource_usages,
                                        tracy_asset_usage_records);
  TracyPlot("Refii.RenderGraphNodes",
            static_cast<int64_t>(tracy_render_graph_nodes.size()));
  const std::vector<FrameDatabaseRenderGraphEdge> tracy_render_graph_edges =
      BuildFrameDatabaseRenderGraphEdges(tracy_pass_dependencies);
  TracyPlot("Refii.RenderGraphEdges",
            static_cast<int64_t>(tracy_render_graph_edges.size()));
  const std::vector<FrameDatabaseRenderGraphExecutionStep>
      tracy_render_graph_execution_steps =
          BuildFrameDatabaseRenderGraphExecutionPlan(
              tracy_render_graph_nodes, tracy_render_graph_edges,
              tracy_pass_resource_transitions);
  TracyPlot("Refii.RenderGraphExecutionSteps",
            static_cast<int64_t>(tracy_render_graph_execution_steps.size()));
  TracyPlot("Refii.RenderGraphBarriers",
            static_cast<int64_t>(tracy_pass_resource_transitions.size()));
  const std::vector<FrameDatabaseRenderGraphPhase> tracy_render_graph_phases =
      BuildFrameDatabaseRenderGraphPhases(tracy_render_graph_nodes,
                                          tracy_render_graph_execution_steps);
  TracyPlot("Refii.RenderGraphPhases",
            static_cast<int64_t>(tracy_render_graph_phases.size()));
  TracyPlot("Refii.RenderGraphRuntimeExecutions", int64_t{1});
  TracyPlot("Refii.RenderGraphBridgeExecutions", int64_t{1});
  const RefiiRenderGraphRuntimeSchedulerSnapshot tracy_scheduler =
      GetRefiiRenderGraphRuntimeSchedulerSnapshot();
  TracyPlot("Refii.RenderGraphSchedulerExecutions",
            static_cast<int64_t>(tracy_scheduler.executions));
  TracyPlot("Refii.RenderGraphSchedulerSteps",
            static_cast<int64_t>(tracy_scheduler.steps));
  TracyPlot("Refii.RenderGraphSchedulerBridgeSteps",
            static_cast<int64_t>(tracy_scheduler.bridge_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedPlanSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_plan_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedRenderSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_render_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedClearSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_clear_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedClearCommands",
            static_cast<int64_t>(tracy_scheduler.graph_owned_clear_commands));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedDrawSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_draw_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedDrawCommands",
            static_cast<int64_t>(tracy_scheduler.graph_owned_draw_commands));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedResolveSteps",
            static_cast<int64_t>(tracy_scheduler.graph_owned_resolve_steps));
  TracyPlot("Refii.RenderGraphSchedulerGraphOwnedResolveCommands",
            static_cast<int64_t>(tracy_scheduler.graph_owned_resolve_commands));
  TracyPlot("Refii.RenderGraphSchedulerBridgeCommands",
            static_cast<int64_t>(tracy_scheduler.bridge_commands));
  const RefiiRenderGraphRuntimePlanSnapshot tracy_render_graph_runtime_plan =
      BuildRefiiRenderGraphRuntimePlanSnapshot(
          renderdoc_capture_frame_index, tracy_draw_packets,
          tracy_command_packets, tracy_pass_summaries, tracy_resource_edges,
          tracy_pass_resource_usages, tracy_render_graph_nodes,
          tracy_render_graph_execution_steps, tracy_render_graph_phases,
          tracy_pass_resource_transitions);
  StoreRefiiRenderGraphRuntimePlanSnapshot(tracy_render_graph_runtime_plan);
  TracyPlot("Refii.RenderGraphRuntimePlanPasses",
            static_cast<int64_t>(tracy_render_graph_runtime_plan.passes));
  TracyPlot(
      "Refii.RenderGraphRuntimePlanResources",
      static_cast<int64_t>(tracy_render_graph_runtime_plan.resource_references));
  TracyPlot("Refii.RenderGraphRuntimePlanEdgeOnlyPasses",
            static_cast<int64_t>(
                tracy_render_graph_runtime_plan.edge_only_passes));
  TracyPlot("Refii.RenderGraphRuntimePlanSurfaceDestinationEdges",
            static_cast<int64_t>(
                tracy_render_graph_runtime_plan.surface_destination_edges));
  TracyPlot("Refii.FrameHistoryEntries",
            static_cast<int64_t>(GetPlumeFrameHistorySnapshot().count));
  PlotFrameDatabasePassSummaries(tracy_pass_summaries);
  PlotFrameDatabaseRootCoverage(tracy_draw_packets);
  EndRenderDocFrameCapture(renderdoc_owned_capture_active);
  if (renderdoc_capture_started) {
    ClearRenderDocCaptureReason();
  }
  return presented;
#else
  return false;
#endif
}

void NotifyPlumeBackendHook(const char* symbol, RendererHookKind kind) {
  if (!PlumeRendererBackendEnabled()) {
    return;
  }
  // Once the one-shot log below has latched, this function has nothing left
  // to do — and it runs on EVERY hooked call (5,790/frame at title, run 415).
  // The unconditional try_lock was a per-call atomic RMW on the shared
  // backend mutex cacheline, ping-ponging against the render thread. Read
  // the latch first; the flag only ever goes false->true, so a stale read
  // costs one harmless extra try_lock.
  if (g_plume_backend.logged_hook_observer) {
    return;
  }

  std::unique_lock<std::mutex> lock(g_plume_backend.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    return;
  }

  if (!g_plume_backend.logged_hook_observer) {
    const char* hook_class = kind == RendererHookKind::kDraw ? "draw/state" :
                             kind == RendererHookKind::kResource ? "resource" :
                             "general";
    const RendererGraphicsApi api = GetRendererGraphicsApi();
    REXLOG_INFO(
        "Refii plume/{} hook observer is active; {} ({}) is feeding the "
        "static Plume replacement path while Plume owns presentation and "
        "routes captured Fable command submission through the replacement "
        "backend",
        PlumeApiBridgeName(api), symbol, hook_class);
    g_plume_backend.logged_hook_observer = true;
  }
}

}  // namespace refii::renderer
