#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {
bool RefiiMainMenuTraceIsHashOnly() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  return !trace.last_texture_hash.empty() &&
         trace.last_texture_selection.empty() &&
         trace.last_wrapper_view.empty() &&
         trace.last_wrapper_slot36.empty() &&
         trace.last_texture_stream_build.empty();
}

bool RefiiMainMenuTraceIsTextureOwnedButNotOpened() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  return !trace.last_texture_selection.empty() &&
         trace.last_wrapper_view.empty() &&
         trace.last_wrapper_slot36.empty() &&
         trace.last_texture_stream_build.empty();
}
}  // namespace detail
}  // namespace refii::vfs
