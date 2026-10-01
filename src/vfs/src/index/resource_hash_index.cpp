#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {
std::mutex& RuntimeResourceHashNameMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, std::vector<std::string>>&
RuntimeResourceHashNames() {
  static std::unordered_map<uint32_t, std::vector<std::string>> names;
  return names;
}

std::vector<std::string> GetRuntimeResourceHashNames(uint32_t resource_hash) {
  std::lock_guard<std::mutex> lock(RuntimeResourceHashNameMutex());
  const auto match = RuntimeResourceHashNames().find(resource_hash);
  if (match == RuntimeResourceHashNames().end()) {
    return {};
  }
  return match->second;
}
}  // namespace detail
}  // namespace refii::vfs
