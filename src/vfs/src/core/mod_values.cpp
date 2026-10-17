// refii — hard mods' named values: what a mod's hooks state for Godot, and the settings Godot
// hands them. The C entries are declared in core/payload_bridge.h; the C++ wrappers in
// core/mod_values.h.
//
// HERE, NOT BESIDE THE HOOKS, by the rule the rest of src/vfs/src/core follows: the payload
// writes and the host bridge reads, so the state lives in the one library both link.

#include "core/mod_values.h"

#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>

namespace {

// A key is "<mod>.<name>". Bounded so a hook passing a garbage length cannot make the host copy
// megabytes on the guest thread.
constexpr size_t kMaxKeyLength = 128;

struct ModValueTables {
  std::mutex mutex;
  std::unordered_map<std::string, float> published;
  std::unordered_map<std::string, float> settings;
};

ModValueTables& Tables() {
  static ModValueTables tables;
  return tables;
}

bool ValidKey(const char* key, size_t length) {
  return key != nullptr && length != 0 && length <= kMaxKeyLength;
}

}  // namespace

extern "C" void refii_bridge_mod_publish_f32(const char* key, size_t key_length, float value) {
  if (!ValidKey(key, key_length)) {
    return;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  tables.published[std::string(key, key_length)] = value;
}

extern "C" int32_t refii_bridge_mod_value_f32(const char* key, size_t key_length, float* out) {
  if (!ValidKey(key, key_length)) {
    return 0;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  auto found = tables.published.find(std::string(key, key_length));
  if (found == tables.published.end()) {
    return 0;
  }
  if (out != nullptr) {
    *out = found->second;
  }
  return 1;
}

extern "C" void refii_bridge_mod_set_setting_f32(const char* key, size_t key_length, float value) {
  if (!ValidKey(key, key_length)) {
    return;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  tables.settings[std::string(key, key_length)] = value;
}

extern "C" float refii_bridge_mod_setting_f32(const char* key, size_t key_length, float fallback) {
  if (!ValidKey(key, key_length)) {
    return fallback;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  auto found = tables.settings.find(std::string(key, key_length));
  return found == tables.settings.end() ? fallback : found->second;
}
