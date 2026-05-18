#pragma once

#include <cstdint>

struct PPCContext;

namespace refii::renderer {

enum class RendererHookKind {
  kGeneral,
  kResource,
  kDraw,
};

struct RendererHookArgs {
  uint32_t r3 = 0;
  uint32_t r4 = 0;
  uint32_t r5 = 0;
  uint32_t r6 = 0;
  uint32_t r7 = 0;
  uint32_t r8 = 0;
  uint32_t r9 = 0;
  uint32_t r10 = 0;
  uint32_t r1 = 0;
  uint32_t lr = 0;
  uint64_t r3_u64 = 0;
  uint64_t r4_u64 = 0;
  uint64_t r5_u64 = 0;
  uint64_t r6_u64 = 0;
  double f1 = 0.0;
  double f2 = 0.0;
  double f3 = 0.0;
  double f4 = 0.0;
  double f5 = 0.0;
  double f6 = 0.0;
};

struct RendererHookTimingSnapshot {
  uint64_t hook_enters = 0;
  uint64_t draw_hook_enters = 0;
  uint64_t display_present_hooks = 0;
  uint64_t plume_present_requests = 0;
  uint64_t plume_present_pump_requests = 0;
  uint64_t plume_present_pump_fable_advances = 0;
  uint64_t native_skipped_hooks = 0;
  int64_t last_hook_gap_us = 0;
  int64_t last_draw_hook_gap_us = 0;
  int64_t last_display_present_gap_us = 0;
  int64_t last_plume_present_request_gap_us = 0;
  int64_t last_plume_present_pump_gap_us = 0;
  uint32_t fable_device = 0;
  uint32_t fable_present_submitted = 0;
  uint32_t fable_present_completed = 0;
  uint32_t fable_swap_callbacks = 0;
  uint32_t fable_present_pending = 0;
  uint32_t plume_present_pump_target_fps = 0;
  bool seen_display_swap_hook = false;
  bool plume_present_pump_enabled = false;
  char last_hook_symbol[64] = {};
  char last_display_present_symbol[64] = {};
  char last_plume_present_symbol[64] = {};
  char last_plume_present_reason[32] = {};
  char last_native_skipped_symbol[64] = {};
};

bool PlumeAvailable();
void ShutdownRendererPresentPump();
RendererHookTimingSnapshot GetRendererHookTimingSnapshot();
void OnRendererGuestHook(const char* symbol, RendererHookKind kind);
RendererHookArgs CaptureRendererHookArgs(const PPCContext& ctx);
void OnRendererGuestHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args,
                              uint8_t* base = nullptr);
void OnRendererStateDispatchHook(const RendererHookArgs& args, uint8_t* base);
void OnRendererPassQueueHook(const RendererHookArgs& args, uint8_t* base);
void OnRendererPassQueueSnapshot(const RendererHookArgs& args, uint8_t* base);
void OnRendererGuestHookExit(const char* symbol, RendererHookKind kind,
                             const RendererHookArgs& args,
                             const PPCContext& ctx, uint8_t* base);
bool ShouldRunNativeRendererHook(const char* symbol, RendererHookKind kind);
void OnRendererGuestHookNativeSkipped(const char* symbol, RendererHookKind kind,
                                      const RendererHookArgs& args,
                                      PPCContext& ctx, uint8_t* base);

}  // namespace refii::renderer
