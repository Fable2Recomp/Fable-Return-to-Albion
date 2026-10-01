#include "loose_asset_hooks.h"
#include <cstdint>

#include "runtime/cvars.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <mutex>
#include <string>
#include <string_view>

#include <rex/cvar.h>

namespace refii::vfs {
namespace {

REXCVAR_DEFINE_BOOL(vfs_trace_loose_assets, false, "VFS",
                    "Log loose asset override lookups and matches.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_trace_vfs, false, "VFS",
                    "Log Fable 2 VFS/resource manager/bank stream flow.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_trace_vfs_verbose_io, false, "VFS",
                    "Log high-volume Refii VFS stream read/page operations.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_loose_asset_root, "", "VFS",
                      "Optional host path to loose asset overrides. Defaults to "
                      "<assets_root>/unpacked.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// The backend/trace checks below run on every hooked guest call (hundreds of
// thousands of times per second on the render thread). REXCVAR_GET on a
// string cvar heap-allocates a copy per read, which perf showed costing ~20%
// of total process cycles. Cache the decisions in atomics and refresh them
// through the cvar change callbacks so hot-reload semantics are preserved at
// zero steady-state cost.
std::atomic<bool> g_cached_trace_vfs{false};
std::atomic<bool> g_cached_trace_loose_assets{false};
std::atomic<bool> g_cached_trace_verbose_io{false};
std::once_flag g_cvar_cache_once;
std::atomic<bool> g_cvar_cache_ready{false};

void RefreshVfsCvarCache() {
  // REFII'S VFS IS THE ONLY ONE — there is nothing to select, so nothing is cached about it.
  g_cached_trace_vfs.store(REXCVAR_GET(vfs_trace_vfs),
                           std::memory_order_release);
  g_cached_trace_loose_assets.store(REXCVAR_GET(vfs_trace_loose_assets),
                                    std::memory_order_release);
  g_cached_trace_verbose_io.store(REXCVAR_GET(vfs_trace_vfs_verbose_io),
                                  std::memory_order_release);
}

void InitializeVfsCvarCacheSlow() {
  std::call_once(g_cvar_cache_once, [] {
    RefreshVfsCvarCache();
    const auto refresh = [](std::string_view, std::string_view) {
      RefreshVfsCvarCache();
    };
    rex::cvar::RegisterChangeCallback("vfs_trace_vfs", refresh);
    rex::cvar::RegisterChangeCallback("vfs_trace_loose_assets", refresh);
    rex::cvar::RegisterChangeCallback("vfs_trace_vfs_verbose_io", refresh);
    g_cvar_cache_ready.store(true, std::memory_order_release);
  });
}

inline void EnsureVfsCvarCache() {
  // Double-checked fast path: one relaxed-ish atomic load per call.
  // std::call_once's fast path alone showed up at ~2% of process cycles.
  if (!g_cvar_cache_ready.load(std::memory_order_acquire)) [[unlikely]] {
    InitializeVfsCvarCacheSlow();
  }
}
}  // namespace

extern "C" int32_t refii_bridge_vfs_loose_asset_override_tracing_enabled() {
  EnsureVfsCvarCache();
  return g_cached_trace_loose_assets.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_tracing_enabled() {
  EnsureVfsCvarCache();
  return g_cached_trace_vfs.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_verbose_io_tracing_enabled() {
  EnsureVfsCvarCache();
  return g_cached_trace_verbose_io.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_loose_gameface_binary_overrides_enabled() {
  return true;
}

inline namespace detail {
std::string ConfiguredLooseAssetRoot() {
  return REXCVAR_GET(vfs_loose_asset_root);
}
}  // namespace detail
}  // namespace refii::vfs
