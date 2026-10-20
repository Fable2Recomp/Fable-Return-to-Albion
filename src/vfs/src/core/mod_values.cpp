// refii — hard mods' named values: what a mod's hooks state for Godot, and the settings Godot
// hands them. The C entries are declared in core/payload_bridge.h; the C++ wrappers in
// core/mod_values.h.
//
// HERE, NOT BESIDE THE HOOKS, by the rule the rest of src/vfs/src/core follows: the payload
// writes and the host bridge reads, so the state lives in the one library both link.

#include "core/mod_values.h"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace {

// A key is "<mod>.<name>". Bounded so a hook passing a garbage length cannot make the host copy
// megabytes on the guest thread.
constexpr size_t kMaxKeyLength = 128;

struct InputEdge {
  std::string action;
  int32_t player;
  bool pressed;
};

struct ModValueTables {
  std::mutex mutex;
  std::unordered_map<std::string, float> published;
  std::unordered_map<std::string, float> settings;
  // Events for the host, oldest first; bounded so a host that stops draining (a paused editor)
  // costs a fixed amount of memory and sees the newest events when it resumes.
  std::deque<std::pair<int32_t, std::string>> events;
  std::deque<InputEdge> inputs;
  std::deque<std::string> commands;
  std::deque<std::string> errors;
};
constexpr size_t kMaxQueuedErrors = 256;

struct HandleTable {
  std::mutex mutex;
  std::vector<uint32_t> addresses{0u};
  std::unordered_map<uint32_t, uint32_t> handles;
};

HandleTable& Handles() {
  static HandleTable table;
  return table;
}
constexpr size_t kMaxQueuedCommands = 1024;
/// The tick polls a command into a 4096-byte buffer with its terminator.
constexpr size_t kMaxCommandLength = 4095;
/// Replies and other host-only records: posted whatever the mask says.
constexpr int32_t kHostOnlyKind = 1000;

constexpr size_t kMaxQueuedEvents = 4096;
constexpr size_t kMaxQueuedInputs = 256;
constexpr size_t kMaxFieldsLength = 2048;
std::atomic<uint64_t> g_host_event_mask{0};
/// Kinds 64..127.
std::atomic<uint64_t> g_host_event_mask_high{0};

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

extern "C" void refii_bridge_mod_event_post(int32_t kind, const char* fields, size_t length) {
  if (kind < 0 || fields == nullptr || length > kMaxFieldsLength) {
    return;
  }
  if (kind < kHostOnlyKind && !refii::mods::HostWantsEvent(kind)) {
    return;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  if (tables.events.size() >= kMaxQueuedEvents) {
    // Drop the oldest ordinary event; a query reply and the hero events are what a script's state
    // hangs on, so they are kept while anything else can go.
    auto victim = tables.events.begin();
    while (victim != tables.events.end() &&
           (victim->first == kHostOnlyKind || victim->first == 1 || victim->first == 2)) {
      ++victim;
    }
    tables.events.erase(victim != tables.events.end() ? victim : tables.events.begin());
  }
  tables.events.emplace_back(kind, std::string(fields, length));
}

extern "C" uint64_t refii_bridge_mod_host_event_mask(void) {
  return g_host_event_mask.load(std::memory_order_relaxed);
}

extern "C" uint64_t refii_bridge_mod_host_event_mask_high(void) {
  return g_host_event_mask_high.load(std::memory_order_relaxed);
}

extern "C" int32_t refii_bridge_mod_input_poll(char* out_name, size_t capacity, int32_t* out_player,
                                               int32_t* out_pressed) {
  if (out_name == nullptr || capacity == 0) {
    return 0;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  if (tables.inputs.empty()) {
    return 0;
  }
  const InputEdge& edge = tables.inputs.front();
  const size_t n = edge.action.size() < capacity - 1 ? edge.action.size() : capacity - 1;
  std::memcpy(out_name, edge.action.data(), n);
  out_name[n] = 0;
  if (out_player != nullptr) {
    *out_player = edge.player;
  }
  if (out_pressed != nullptr) {
    *out_pressed = edge.pressed ? 1 : 0;
  }
  tables.inputs.pop_front();
  return 1;
}

extern "C" int32_t refii_bridge_mod_command_poll(char* out_line, size_t capacity) {
  if (out_line == nullptr || capacity == 0) {
    return 0;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  if (tables.commands.empty()) {
    return 0;
  }
  const std::string& line = tables.commands.front();
  const size_t n = line.size() < capacity - 1 ? line.size() : capacity - 1;
  std::memcpy(out_line, line.data(), n);
  out_line[n] = 0;
  tables.commands.pop_front();
  return 1;
}

extern "C" uint32_t refii_bridge_mod_handle(uint32_t address) {
  if (address == 0) {
    return 0;
  }
  HandleTable& table = Handles();
  std::lock_guard lock(table.mutex);
  auto found = table.handles.find(address);
  if (found != table.handles.end()) {
    return found->second;
  }
  const uint32_t handle = static_cast<uint32_t>(table.addresses.size());
  table.addresses.push_back(address);
  table.handles.emplace(address, handle);
  return handle;
}

extern "C" void refii_bridge_mod_handle_forget(uint32_t address) {
  if (address == 0) {
    return;
  }
  HandleTable& table = Handles();
  std::lock_guard lock(table.mutex);
  auto found = table.handles.find(address);
  if (found == table.handles.end()) {
    return;
  }
  table.addresses[found->second] = 0u;
  table.handles.erase(found);
}

extern "C" uint32_t refii_bridge_mod_handle_address(uint32_t handle) {
  HandleTable& table = Handles();
  std::lock_guard lock(table.mutex);
  return handle < table.addresses.size() ? table.addresses[handle] : 0u;
}

extern "C" void refii_bridge_mod_error_post(const char* text, size_t length) {
  if (text == nullptr || length == 0) {
    return;
  }
  std::string line(text, length < kMaxFieldsLength ? length : kMaxFieldsLength);
  for (char& c : line) {
    if (c == '\n' || c == '\r') {
      c = ' ';
    }
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  if (tables.errors.size() >= kMaxQueuedErrors) {
    tables.errors.pop_front();
  }
  tables.errors.push_back(std::move(line));
}

namespace refii::mods {

size_t DrainErrorsForHost(char* out, size_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  size_t written = 0;
  while (!tables.errors.empty()) {
    const std::string& line = tables.errors.front();
    if (written + line.size() + 1 > capacity) {
      break;
    }
    std::memcpy(out + written, line.data(), line.size());
    written += line.size();
    out[written++] = '\n';
    tables.errors.pop_front();
  }
  return written;
}

void PostCommandFromHost(std::string_view line) {
  if (line.empty() || line.size() > kMaxCommandLength) {
    return;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  if (tables.commands.size() >= kMaxQueuedCommands) {
    tables.commands.pop_front();
  }
  tables.commands.emplace_back(line);
}

size_t DrainEventsForHost(char* out, size_t capacity) {
  if (out == nullptr || capacity == 0) {
    return 0;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  size_t written = 0;
  while (!tables.events.empty()) {
    const auto& [kind, fields] = tables.events.front();
    char head[16];
    const int head_length = std::snprintf(head, sizeof(head), "%d\t", kind);
    const size_t record = static_cast<size_t>(head_length) + fields.size() + 1;
    if (written + record > capacity) {
      break;
    }
    std::memcpy(out + written, head, static_cast<size_t>(head_length));
    written += static_cast<size_t>(head_length);
    std::memcpy(out + written, fields.data(), fields.size());
    written += fields.size();
    out[written++] = '\n';
    tables.events.pop_front();
  }
  return written;
}

void SetHostEventMask(uint64_t mask) {
  g_host_event_mask.store(mask, std::memory_order_relaxed);
  if (mask == 0 && g_host_event_mask_high.load(std::memory_order_relaxed) == 0) {
    ModValueTables& tables = Tables();
    std::lock_guard lock(tables.mutex);
    tables.events.clear();
  }
}

void SetHostEventMaskHigh(uint64_t mask) {
  g_host_event_mask_high.store(mask, std::memory_order_relaxed);
}

void PostInputFromHost(std::string_view action, int32_t player, bool pressed) {
  if (!ValidKey(action.data(), action.size()) || (player != 1 && player != 2)) {
    return;
  }
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  if (tables.inputs.size() >= kMaxQueuedInputs) {
    tables.inputs.pop_front();
  }
  tables.inputs.push_back(InputEdge{std::string(action), player, pressed});
}

}  // namespace refii::mods

namespace {
std::atomic<refii_mod_verdict_fn> g_verdict_handler{nullptr};
std::atomic<uint64_t> g_verdict_mask{0};
std::atomic<uint64_t> g_verdict_mask_high{0};
}  // namespace

extern "C" void refii_bridge_mod_set_verdict_handler(refii_mod_verdict_fn handler) {
  g_verdict_handler.store(handler, std::memory_order_release);
}

extern "C" refii_mod_verdict_fn refii_bridge_mod_verdict_handler(void) {
  return g_verdict_handler.load(std::memory_order_acquire);
}

extern "C" void refii_bridge_mod_set_verdict_mask(uint64_t mask) {
  g_verdict_mask.store(mask, std::memory_order_release);
}

extern "C" uint64_t refii_bridge_mod_verdict_mask(void) {
  return g_verdict_mask.load(std::memory_order_acquire);
}

extern "C" void refii_bridge_mod_set_verdict_mask_high(uint64_t mask) {
  g_verdict_mask_high.store(mask, std::memory_order_release);
}

extern "C" uint64_t refii_bridge_mod_verdict_mask_high(void) {
  return g_verdict_mask_high.load(std::memory_order_acquire);
}
