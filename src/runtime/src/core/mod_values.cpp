// refii — hard mods' named values: what a mod's hooks state for Godot, and the settings Godot
// hands them. The C entries are declared in core/payload_bridge.h; the C++ wrappers in
// core/mod_values.h.
//
// HERE, NOT BESIDE THE HOOKS, by the rule the rest of refii_runtime follows: the payload writes
// and the host bridge reads, so the state lives in the one library both link. The event KINDS
// are the title's catalog; the title holds its count under kHostMaskCapacity where it defines
// them (Fable 2: Fable2/src/vfs/src/hooks/mod_events.cpp).

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
/// The host's event mask, word w holding kinds 64w..64w+63 (core/mod_values.h kHostMaskWords).
std::atomic<uint64_t> g_host_event_mask[refii::mods::kHostMaskWords] = {};

ModValueTables& Tables() {
  static ModValueTables tables;
  return tables;
}

bool ValidKey(const char* key, size_t length) {
  return key != nullptr && length != 0 && length <= kMaxKeyLength;
}

}  // namespace

/// Whether a host listens at all: some word of its event mask is set (a Godot script is loaded).
static bool HostListening() {
  for (const std::atomic<uint64_t>& word : g_host_event_mask) {
    if (word.load(std::memory_order_relaxed) != 0u) {
      return true;
    }
  }
  return false;
}

extern "C" void refii_bridge_mod_publish_f32(const char* key, size_t key_length, float value) {
  if (!ValidKey(key, key_length)) {
    return;
  }
  bool changed = false;
  {
    ModValueTables& tables = Tables();
    std::lock_guard lock(tables.mutex);
    auto [at, added] = tables.published.try_emplace(std::string(key, key_length), value);
    // Bitwise, so a NaN restated is no change and +0 / -0 are.
    changed = added || std::memcmp(&at->second, &value, sizeof(value)) != 0;
    at->second = value;
  }
  // THE CHANGE, for the host's value_changed: posted after the lock (the post takes it).
  if (changed && HostListening()) {
    std::string fields = "key='";
    for (size_t i = 0; i < key_length; ++i) {
      fields += key[i] == '\'' || static_cast<unsigned char>(key[i]) < 0x20u ? '_' : key[i];
    }
    char number[48];
    std::snprintf(number, sizeof(number), "',value=%.9g", static_cast<double>(value));
    fields += number;
    refii_bridge_mod_event_post(REFII_MOD_HOST_VALUE_KIND, fields.data(), fields.size());
  }
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

namespace {

/// The host's text lookup (refii::mods::SetHostModTextLookup); null until Godot installs one.
std::atomic<refii::mods::HostModTextFn> g_mod_text_lookup{nullptr};

/// A text to look up is a label or a line: bounded so a garbage length cannot cross megabytes.
constexpr size_t kMaxModTextLength = 4096;

}  // namespace

void refii::mods::SetHostModTextLookup(HostModTextFn lookup) {
  g_mod_text_lookup.store(lookup, std::memory_order_release);
}

extern "C" int32_t refii_bridge_mod_text(const char* text, size_t length, char* out, uint32_t capacity) {
  const refii::mods::HostModTextFn lookup = g_mod_text_lookup.load(std::memory_order_acquire);
  if (lookup == nullptr || text == nullptr || out == nullptr || capacity == 0u || length > kMaxModTextLength) {
    return -1;
  }
  out[0] = 0;
  const std::string terminated(text, length);
  const int32_t written = lookup(terminated.c_str(), static_cast<uint32_t>(terminated.size()), out, capacity);
  if (written < 0) {
    out[0] = 0;
    return -1;
  }
  out[capacity - 1u] = 0;
  return written < static_cast<int32_t>(capacity) ? written : static_cast<int32_t>(capacity - 1u);
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
    // Drop the oldest ordinary event; a host-only record (a query reply, an effect request) and the
    // hero events are what a script's state hangs on, so they are kept while anything else can go.
    auto victim = tables.events.begin();
    while (victim != tables.events.end() &&
           (victim->first >= kHostOnlyKind || victim->first == 1 || victim->first == 2)) {
      ++victim;
    }
    tables.events.erase(victim != tables.events.end() ? victim : tables.events.begin());
  }
  tables.events.emplace_back(kind, std::string(fields, length));
}

extern "C" uint64_t refii_bridge_mod_host_event_mask_word(int32_t word) {
  return word >= 0 && word < refii::mods::kHostMaskWords ? g_host_event_mask[word].load(std::memory_order_relaxed) : 0u;
}

extern "C" void refii_bridge_mod_set_host_event_mask_word(int32_t word, uint64_t mask) {
  refii::mods::SetHostEventMaskWord(word, mask);
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

void SetHostEventMaskWord(int32_t word, uint64_t mask) {
  if (word < 0 || word >= kHostMaskWords) {
    return;
  }
  g_host_event_mask[word].store(mask, std::memory_order_relaxed);
  for (int32_t w = 0; w < kHostMaskWords; ++w) {
    if (g_host_event_mask[w].load(std::memory_order_relaxed) != 0u) {
      return;
    }
  }
  // Nobody listens: what is queued is dropped (the host-only records with it, as before).
  ModValueTables& tables = Tables();
  std::lock_guard lock(tables.mutex);
  tables.events.clear();
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
std::atomic<refii_mod_verdict_read_fn> g_verdict_reader{nullptr};
/// The verdict mask, the event mask's shape.
std::atomic<uint64_t> g_verdict_mask[refii::mods::kHostMaskWords] = {};
}  // namespace

extern "C" void refii_bridge_mod_set_verdict_handler(refii_mod_verdict_fn handler) {
  g_verdict_handler.store(handler, std::memory_order_release);
}

extern "C" refii_mod_verdict_fn refii_bridge_mod_verdict_handler(void) {
  return g_verdict_handler.load(std::memory_order_acquire);
}

extern "C" void refii_bridge_mod_set_verdict_reader(refii_mod_verdict_read_fn reader) {
  g_verdict_reader.store(reader, std::memory_order_release);
}

/// The payload's reader decides whether this thread is inside a verdict: it answers
/// REFII_MOD_READ_OUTSIDE anywhere else.
extern "C" int32_t refii_bridge_mod_verdict_read(const char* line, size_t length, char* out, size_t capacity) {
  const refii_mod_verdict_read_fn reader = g_verdict_reader.load(std::memory_order_acquire);
  if (reader == nullptr || line == nullptr || out == nullptr || capacity == 0) {
    return REFII_MOD_READ_OUTSIDE;
  }
  return reader(line, length, out, capacity);
}

extern "C" void refii_bridge_mod_set_verdict_mask_word(int32_t word, uint64_t mask) {
  if (word >= 0 && word < refii::mods::kHostMaskWords) {
    g_verdict_mask[word].store(mask, std::memory_order_release);
  }
}

extern "C" uint64_t refii_bridge_mod_verdict_mask_word(int32_t word) {
  return word >= 0 && word < refii::mods::kHostMaskWords ? g_verdict_mask[word].load(std::memory_order_acquire) : 0u;
}
