#include "loose_asset_hooks.h"
#include "runtime/cvars.h"

#include <algorithm>
#include <cctype>
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

}  // namespace

bool LooseAssetOverridesEnabled() {
  return GetLooseAssetBackend() == LooseAssetBackend::kRefii;
}

bool LooseAssetOverrideTracingEnabled() {
  return RefiiLooseAssetBackendEnabled() && REXCVAR_GET(vfs_trace_loose_assets);
}

bool VfsTracingEnabled() {
  return RefiiLooseAssetBackendEnabled() && REXCVAR_GET(vfs_trace_vfs);
}

bool VfsVerboseIoTracingEnabled() {
  return RefiiLooseAssetBackendEnabled() &&
         REXCVAR_GET(vfs_trace_vfs_verbose_io);
}

bool LooseGameFaceBinaryOverridesEnabled() {
  return RefiiLooseAssetBackendEnabled();
}

bool LooseBankEntryOverridesEnabled() {
  return RefiiLooseAssetBackendEnabled();
}

bool SkipStartupVideosEnabled() {
  return LooseAssetOverridesEnabled() &&
         GetLooseAssetBackend() == LooseAssetBackend::kRefii &&
         REXCVAR_GET(vfs_skip_startup_videos) &&
         !REXCVAR_GET(vfs_force_startup_video_sequence);
}

int StartupVideoMinimumPlaybackMs() {
  return std::max(0, REXCVAR_GET(vfs_startup_video_minimum_playback_ms));
}

LooseAssetBackend GetLooseAssetBackend() {
  std::string backend = REXCVAR_GET(vfs_loose_asset_backend);
  if (EqualsAsciiCaseInsensitive(backend, "refii")) {
    return LooseAssetBackend::kRefii;
  }

  return LooseAssetBackend::kNative;
}

bool RefiiLooseAssetBackendEnabled() {
  return LooseAssetOverridesEnabled();
}

inline namespace detail {

std::string ConfiguredLooseAssetRoot() {
  return REXCVAR_GET(vfs_loose_asset_root);
}

std::string ConfiguredModAssetRoots() {
  return REXCVAR_GET(vfs_mod_roots);
}

}  // namespace detail
}  // namespace refii::vfs
