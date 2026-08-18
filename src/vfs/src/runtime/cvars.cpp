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

REXCVAR_DEFINE_BOOL(vfs_skip_startup_videos, false, "VFS",
                    "Skip the non-interactive startup videos while using the Refii VFS.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_force_startup_video_sequence, false, "VFS",
                    "Require the native startup videos to play in sequence before Refii VFS "
                    "post-video handoff patches may run.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_INT32(vfs_startup_video_minimum_playback_ms, 0, "VFS",
                     "Optional extra wall-clock startup-video playback floor "
                     "before Refii VFS post-video handoff may continue when "
                     "native finalizers are not observed. 0 uses the built-in "
                     "observed playback gate.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_loose_asset_backend, "native", "VFS",
                      "VFS backend: refii for loose asset overrides, native "
                      "for the original Fable 2 VFS.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_loose_asset_root, "", "VFS",
                      "Optional host path to loose asset overrides. Defaults to "
                      "<assets_root>/unpacked.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_mod_roots, "", "VFS",
                      "Optional semicolon-separated mod asset roots. Each root "
                      "uses the same game-relative layout as unpacked.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_auto_press_start, false, "VFS",
                    "Testing aid: emulate the player pressing Start once at "
                    "the interactive title screen (drives the exact native "
                    "press handler). Lets unattended runs advance into the "
                    "main menu.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_frontend_synthetic_handoff, false, "VFS",
                    "Enable the legacy synthetic post-video/post-loading "
                    "frontend handoff machinery (texture descriptor "
                    "synthesis, holder arming, wait collapsing). Off by "
                    "default: the fabricated guest writes corrupted native "
                    "GUI state (boot fault-loops); the native flow reaches "
                    "the menu without them. Kept for diagnosis only.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_force_retail_update_mode, true, "VFS",
                    "Report the retail (non-dev-kit) update-path mode to "
                    "GameInit so bootstrap follows the packed-container "
                    "resource path instead of the dev-roots path (the host "
                    "filesystem otherwise looks like a dev kit and takes a "
                    "boot path retail Fable 2 never used).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_native_bank_parse_probe, false, "VFS",
                    "Diagnostic: run the native Bank_ParseHeader on the "
                    "incoming stream for extracted banks and log the resulting "
                    "entry-table shape, instead of building a synthetic bank.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_gdb_csv_overrides, true, "VFS",
                    "Serve <name>.gdb.csv files found in loose asset roots in "
                    "place of the binary .gdb game database: the CSV is "
                    "compiled to .cache/gdb_compiled/ on demand and handed to "
                    "the guest. Enabled by default for the Refii backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_babel_csv_overrides, true, "VFS",
                    "Serve <name>.babel.csv files found in loose asset roots "
                    "in place of the binary .babel localized-text database: "
                    "the CSV is compiled to .cache/babel_compiled/ on demand "
                    "and handed to the guest. Enabled by default for the "
                    "Refii backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_mods_root, "", "VFS",
                      "Folder that holds installed mod packages. Defaults to "
                      "<assets_root>/mods.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_enabled_mods, "", "VFS",
                      "Optional semicolon-separated enabled mod folder names "
                      "under vfs_mods_root. Merged with "
                      "<vfs_mods_root>/enabled.txt; list order is search "
                      "priority.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

bool EqualsAsciiCaseInsensitive(std::string_view left, std::string_view right) {
  if (left.size() != right.size()) {
    return false;
  }
  for (size_t i = 0; i < left.size(); ++i) {
    const auto lhs = static_cast<unsigned char>(left[i]);
    const auto rhs = static_cast<unsigned char>(right[i]);
    if (std::tolower(lhs) != std::tolower(rhs)) {
      return false;
    }
  }
  return true;
}


// The backend/trace checks below run on every hooked guest call (hundreds of
// thousands of times per second on the render thread). REXCVAR_GET on a
// string cvar heap-allocates a copy per read, which perf showed costing ~20%
// of total process cycles. Cache the decisions in atomics and refresh them
// through the cvar change callbacks so hot-reload semantics are preserved at
// zero steady-state cost.
std::atomic<bool> g_cached_refii_backend{false};
std::atomic<bool> g_cached_trace_vfs{false};
std::atomic<bool> g_cached_trace_loose_assets{false};
std::atomic<bool> g_cached_trace_verbose_io{false};
std::once_flag g_cvar_cache_once;
std::atomic<bool> g_cvar_cache_ready{false};

void RefreshVfsCvarCache() {
  const std::string backend = REXCVAR_GET(vfs_loose_asset_backend);
  g_cached_refii_backend.store(EqualsAsciiCaseInsensitive(backend, "refii"),
                               std::memory_order_release);
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
    rex::cvar::RegisterChangeCallback("vfs_loose_asset_backend", refresh);
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

bool LooseAssetOverridesEnabled() {
  EnsureVfsCvarCache();
  return g_cached_refii_backend.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_loose_asset_override_tracing_enabled() {
  EnsureVfsCvarCache();
  return g_cached_refii_backend.load(std::memory_order_acquire) &&
         g_cached_trace_loose_assets.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_tracing_enabled() {
  EnsureVfsCvarCache();
  return g_cached_refii_backend.load(std::memory_order_acquire) &&
         g_cached_trace_vfs.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_verbose_io_tracing_enabled() {
  EnsureVfsCvarCache();
  return g_cached_refii_backend.load(std::memory_order_acquire) &&
         g_cached_trace_verbose_io.load(std::memory_order_acquire);
}

extern "C" int32_t refii_bridge_vfs_loose_gameface_binary_overrides_enabled() {
  return RefiiLooseAssetBackendEnabled();
}

extern "C" int32_t refii_bridge_vfs_loose_bank_entry_overrides_enabled() {
  return RefiiLooseAssetBackendEnabled();
}

extern "C" int32_t refii_bridge_vfs_native_bank_parse_probe_enabled() {
  return RefiiLooseAssetBackendEnabled() &&
         REXCVAR_GET(vfs_native_bank_parse_probe);
}

bool GdbCsvOverridesEnabled() {
  return RefiiLooseAssetBackendEnabled() && REXCVAR_GET(vfs_gdb_csv_overrides);
}

bool BabelCsvOverridesEnabled() {
  return RefiiLooseAssetBackendEnabled() && REXCVAR_GET(vfs_babel_csv_overrides);
}

extern "C" int32_t refii_bridge_vfs_auto_press_start_enabled() {
  return RefiiLooseAssetBackendEnabled() && REXCVAR_GET(vfs_auto_press_start);
}

extern "C" int32_t refii_bridge_vfs_frontend_synthetic_handoff_enabled() {
  return RefiiLooseAssetBackendEnabled() &&
         REXCVAR_GET(vfs_frontend_synthetic_handoff);
}

extern "C" int32_t refii_bridge_vfs_force_retail_update_mode_enabled() {
  return RefiiLooseAssetBackendEnabled() &&
         REXCVAR_GET(vfs_force_retail_update_mode);
}

extern "C" int32_t refii_bridge_vfs_skip_startup_videos_enabled() {
  return LooseAssetOverridesEnabled() &&
         GetLooseAssetBackend() == LooseAssetBackend::kRefii &&
         REXCVAR_GET(vfs_skip_startup_videos) &&
         !REXCVAR_GET(vfs_force_startup_video_sequence);
}

extern "C" int32_t refii_bridge_vfs_startup_video_minimum_playback_ms() {
  return std::max(0, REXCVAR_GET(vfs_startup_video_minimum_playback_ms));
}

LooseAssetBackend GetLooseAssetBackend() {
  EnsureVfsCvarCache();
  return g_cached_refii_backend.load(std::memory_order_acquire)
             ? LooseAssetBackend::kRefii
             : LooseAssetBackend::kNative;
}

extern "C" int32_t refii_bridge_vfs_refii_loose_asset_backend_enabled() {
  return LooseAssetOverridesEnabled();
}

inline namespace detail {

std::string ConfiguredLooseAssetRoot() {
  return REXCVAR_GET(vfs_loose_asset_root);
}

std::string ConfiguredModAssetRoots() {
  return REXCVAR_GET(vfs_mod_roots);
}

std::string ConfiguredModsRootSetting() {
  return REXCVAR_GET(vfs_mods_root);
}

std::string ConfiguredEnabledModsSetting() {
  return REXCVAR_GET(vfs_enabled_mods);
}

}  // namespace detail
}  // namespace refii::vfs
