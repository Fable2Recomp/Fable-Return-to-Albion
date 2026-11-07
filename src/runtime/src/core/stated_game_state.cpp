// refii — the game state a title states for the shell (core/payload_bridge.h): its loading screen,
// its game mode stacks and GUI screen (the pause gate), and its installed cultures.
//
// THE LOADING SCREEN is one object at a time: made by its manager, updated by its own per-frame
// update, gone when destroyed. The table holds what each of those stated last.
//
// THE MODE STACKS are keyed by the stack object, each holding the top its last push, pop or run
// left. The GUI screen is one id, as its writers last stated it.
//
// THE CULTURES are the table as its builder last left it and the row selected since.
//
// It crosses from a title's hooks (the payload, on the guest's threads) to its host bridge (on
// Godot's), which live in different modules; this is the one table both link.

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "core/payload_bridge.h"

namespace {

constexpr uint32_t kMaxArt = 256u;
/// Far above the live population (one stack per game, a handful of games). A stack past the cap
/// replaces the oldest.
constexpr size_t kMaxStacks = 64u;
constexpr uint32_t kMaxCultures = 64u;

std::mutex g_lock;

struct LoadingScreen {
  uint32_t manager = 0u;
  uint32_t screen = 0u;
  uint32_t kind = 0u;
  uint32_t body = 0u;
  float fade = 0.0f;
  bool requested = false;
  bool done = false;
  std::string art;
};

LoadingScreen g_loading;

struct ModeStack {
  uint64_t sequence = 0u;
  uint32_t top = 0u;
  uint32_t vtable = 0u;
  uint32_t depth = 0u;
};

std::unordered_map<uint32_t, ModeStack>* g_stacks = nullptr;
uint64_t g_stack_sequence = 0u;
bool g_gui_screen_stated = false;
uint32_t g_gui_screen = 0u;

std::unordered_map<uint32_t, ModeStack>& Stacks() {
  if (g_stacks == nullptr) {
    g_stacks = new std::unordered_map<uint32_t, ModeStack>();
  }
  return *g_stacks;
}

std::vector<std::string>* g_codes = nullptr;
std::vector<std::string>* g_names = nullptr;
int32_t g_current = -1;

std::vector<std::string>& Codes() {
  if (g_codes == nullptr) {
    g_codes = new std::vector<std::string>();
  }
  return *g_codes;
}

std::vector<std::string>& Names() {
  if (g_names == nullptr) {
    g_names = new std::vector<std::string>();
  }
  return *g_names;
}

/// `count` NUL-terminated strings back to back in `length` bytes; a short buffer yields "".
std::vector<std::string> Split(const char* text, uint32_t length, uint32_t count) {
  std::vector<std::string> out;
  out.reserve(count);
  uint32_t at = 0u;
  for (uint32_t i = 0; i < count; ++i) {
    if (text == nullptr || at >= length) {
      out.emplace_back();
      continue;
    }
    const char* start = text + at;
    const void* end = std::memchr(start, '\0', length - at);
    const uint32_t size = end == nullptr ? length - at : static_cast<uint32_t>(static_cast<const char*>(end) - start);
    out.emplace_back(start, size);
    at += size + 1u;
  }
  return out;
}

}  // namespace

extern "C" {

void refii_bridge_loading_screen_created(uint32_t manager, uint32_t screen, uint32_t kind) {
  if (screen == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  g_loading = LoadingScreen{};
  g_loading.manager = manager;
  g_loading.screen = screen;
  g_loading.kind = kind;
}

void refii_bridge_loading_screen_destroyed(uint32_t screen) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_loading.screen == screen) {
    g_loading = LoadingScreen{};
  }
}

void refii_bridge_loading_screen_art(uint32_t screen, uint32_t kind, uint32_t body, const char* name,
                                     uint32_t length) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_loading.screen != screen) {
    return;
  }
  g_loading.kind = kind;
  g_loading.body = body;
  g_loading.art = name == nullptr ? std::string() : std::string(name, std::min(length, kMaxArt));
}

void refii_bridge_loading_screen_updated(uint32_t screen, float fade, int32_t requested, int32_t done) {
  if (screen == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_loading.screen != screen) {
    return;
  }
  g_loading.fade = fade;
  g_loading.requested = requested != 0;
  g_loading.done = done != 0;
}

int32_t refii_bridge_loading_screen_read(uint32_t* out_manager, uint32_t* out_screen, float* out_fade,
                                         uint32_t* out_requested, uint32_t* out_done, uint32_t* out_kind,
                                         uint32_t* out_body, char* out_name, uint32_t name_capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  const LoadingScreen& ls = g_loading;
  if (out_manager != nullptr) *out_manager = ls.manager;
  if (out_screen != nullptr) *out_screen = ls.screen;
  if (out_fade != nullptr) *out_fade = ls.fade;
  if (out_requested != nullptr) *out_requested = ls.requested ? 1u : 0u;
  if (out_done != nullptr) *out_done = ls.done ? 1u : 0u;
  if (out_kind != nullptr) *out_kind = ls.kind;
  if (out_body != nullptr) *out_body = ls.body;
  if (out_name != nullptr && name_capacity > 0u) {
    const size_t length = std::min<size_t>(ls.art.size(), name_capacity - 1u);
    std::memcpy(out_name, ls.art.data(), length);
    out_name[length] = '\0';
  }
  return ls.screen != 0u ? 1 : 0;
}

void refii_bridge_mode_stack_stated(uint32_t stack, uint32_t top, uint32_t top_vtable, uint32_t depth) {
  if (stack == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto& stacks = Stacks();
  auto it = stacks.find(stack);
  if (it == stacks.end() && stacks.size() >= kMaxStacks) {
    auto oldest = std::min_element(stacks.begin(), stacks.end(), [](const auto& a, const auto& b) {
      return a.second.sequence < b.second.sequence;
    });
    stacks.erase(oldest);
  }
  ModeStack& entry = stacks[stack];
  entry.sequence = ++g_stack_sequence;
  entry.top = depth != 0u ? top : 0u;
  entry.vtable = depth != 0u ? top_vtable : 0u;
  entry.depth = depth;
}

int32_t refii_bridge_mode_stack_read(uint32_t stack, uint32_t* out_top, uint32_t* out_vtable, uint32_t* out_depth) {
  std::lock_guard<std::mutex> guard(g_lock);
  auto it = Stacks().find(stack);
  if (stack == 0u || it == Stacks().end()) {
    return 0;
  }
  if (out_top != nullptr) *out_top = it->second.top;
  if (out_vtable != nullptr) *out_vtable = it->second.vtable;
  if (out_depth != nullptr) *out_depth = it->second.depth;
  return 1;
}

void refii_bridge_gui_screen_stated(uint32_t screen_id) {
  std::lock_guard<std::mutex> guard(g_lock);
  g_gui_screen = screen_id;
  g_gui_screen_stated = true;
}

int32_t refii_bridge_gui_screen_read(uint32_t* out_screen_id) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_screen_id != nullptr) *out_screen_id = g_gui_screen;
  return g_gui_screen_stated ? 1 : 0;
}

void refii_bridge_cultures_stated(uint32_t count, int32_t current, const char* codes, uint32_t codes_length,
                                  const char* names, uint32_t names_length) {
  count = std::min(count, kMaxCultures);
  std::lock_guard<std::mutex> guard(g_lock);
  Codes() = Split(codes, codes_length, count);
  Names() = Split(names, names_length, count);
  g_current = current >= 0 && static_cast<uint32_t>(current) < count ? current : -1;
}

void refii_bridge_culture_selected(uint32_t index) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (index < Codes().size()) {
    g_current = static_cast<int32_t>(index);
  }
}

uint32_t refii_bridge_cultures_read(int32_t* out_current, char* out_names, uint32_t names_capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_current != nullptr) *out_current = g_current;
  uint32_t at = 0u;
  if (out_names != nullptr && names_capacity > 0u) {
    out_names[0] = '\0';
    for (const std::string& name : Names()) {
      if (at + name.size() + 1u > names_capacity) {
        break;
      }
      std::memcpy(out_names + at, name.data(), name.size());
      at += static_cast<uint32_t>(name.size());
      out_names[at++] = '\0';
    }
  }
  return static_cast<uint32_t>(Codes().size());
}

uint32_t refii_bridge_culture_code_read(char* out, uint32_t capacity) {
  if (out == nullptr || capacity == 0u) {
    return 0u;
  }
  out[0] = '\0';
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_current < 0 || static_cast<size_t>(g_current) >= Codes().size()) {
    return 0u;
  }
  const std::string& code = Codes()[static_cast<size_t>(g_current)];
  if (code.empty() || code.size() + 1u > capacity) {
    return 0u;
  }
  std::memcpy(out, code.data(), code.size());
  out[code.size()] = '\0';
  return static_cast<uint32_t>(code.size());
}

}  // extern "C"
