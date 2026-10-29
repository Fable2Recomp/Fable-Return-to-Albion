#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {
bool RefiiStartupVideosPlaybackCriticalActive() {
  std::unique_lock<std::mutex> lock(RefiiPostVideoHandoffTraceMutex(),
                                   std::try_to_lock);
  if (!lock.owns_lock()) {
    // This predicate is used by trace/log throttles on hot video paths. If the
    // trace mutex is already busy, assume playback is still critical so logging
    // fails closed instead of formatting large state snapshots inside the BIK
    // frame loop.
    return true;
  }

  const auto& trace = RefiiPostVideoHandoffTraceState();
  return !trace.middleware.provider.empty() ||
         !trace.microsoft.provider.empty() ||
         !trace.lionhead.provider.empty() ||
         trace.last_bik_at.time_since_epoch().count() != 0 ||
         trace.last_startup_update_guest != 0;
}
}  // namespace detail
}  // namespace refii::vfs
