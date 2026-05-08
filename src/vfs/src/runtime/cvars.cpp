#include "loose_asset_hooks.h"
#include "runtime/cvars.h"

#include <algorithm>
#include <string>

#include <rex/cvar.h>

namespace refii::vfs {
namespace {

REXCVAR_DEFINE_BOOL(vfs_use_loose_assets, false, "VFS",
                    "Prefer loose files from the unpacked mod workspace before banked assets.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_trace_loose_assets, false, "VFS",
                    "Log loose asset override lookups and matches.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_trace_vfs, false, "VFS",
                    "Log Fable 2 VFS/resource manager/bank stream flow.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_trace_vfs_verbose_io, false, "VFS",
                    "Log high-volume Refii VFS stream read/page operations.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_use_loose_gameface_binaries, false, "VFS",
                    "Allow loose Anark/GameFace binary assets (.bgf/.bsg/.fac).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_use_loose_bank_entries, false, "VFS",
                    "Prefer loose files for entries opened from Fable 2 BNK banks.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_skip_startup_videos, false, "VFS",
                    "Skip the non-interactive startup videos while using the Refii VFS.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(vfs_force_startup_video_sequence, false, "VFS",
                    "Require the native startup videos to play in sequence before Refii VFS "
                    "post-video handoff patches may run.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_INT32(vfs_startup_video_minimum_playback_ms, 0, "VFS",
                     "Optional wall-clock startup-video fallback before Refii VFS "
                     "post-video handoff may continue if native finalizers are not "
                     "observed. 0 disables the fallback.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_loose_asset_backend, "disabled", "VFS",
                      "Loose asset stream backend: refii or disabled.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(vfs_loose_asset_root, "", "VFS",
                      "Optional host path to loose asset overrides. Defaults to "
                      "<assets_root>/unpacked.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

}  // namespace

bool LooseAssetOverridesEnabled() { return REXCVAR_GET(vfs_use_loose_assets); }

bool LooseAssetOverrideTracingEnabled() {
  return REXCVAR_GET(vfs_trace_loose_assets);
}

bool VfsTracingEnabled() { return REXCVAR_GET(vfs_trace_vfs); }

bool VfsVerboseIoTracingEnabled() {
  return REXCVAR_GET(vfs_trace_vfs_verbose_io);
}

bool LooseGameFaceBinaryOverridesEnabled() {
  return REXCVAR_GET(vfs_use_loose_gameface_binaries);
}

bool LooseBankEntryOverridesEnabled() {
  return REXCVAR_GET(vfs_use_loose_bank_entries);
}

bool SkipStartupVideosEnabled() {
  return LooseAssetOverridesEnabled() && REXCVAR_GET(vfs_skip_startup_videos) &&
         !REXCVAR_GET(vfs_force_startup_video_sequence);
}

int StartupVideoMinimumPlaybackMs() {
  return std::max(0, REXCVAR_GET(vfs_startup_video_minimum_playback_ms));
}

LooseAssetBackend GetLooseAssetBackend() {
  std::string backend = REXCVAR_GET(vfs_loose_asset_backend);
  std::ranges::transform(backend, backend.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });

  if (backend == "refii" || backend.empty()) {
    return LooseAssetBackend::kRefii;
  }

  return LooseAssetBackend::kDisabled;
}

bool RefiiLooseAssetBackendEnabled() {
  return LooseAssetOverridesEnabled() &&
         GetLooseAssetBackend() == LooseAssetBackend::kRefii;
}

inline namespace detail {

std::string ConfiguredLooseAssetRoot() {
  return REXCVAR_GET(vfs_loose_asset_root);
}

}  // namespace detail
}  // namespace refii::vfs
