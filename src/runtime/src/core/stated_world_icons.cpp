// refii — the world icons and choice records a title states (core/payload_bridge.h): each icon as
// its own Init, Update and ring tick leave it, until the registry erases it or it is destroyed;
// each choice record from its creation to its deleter.
//
// It crosses from a title's hooks (the payload, on the guest's game thread) to its host bridge
// (on Godot's), which live in different modules; this is the one table both link. A statement
// replaces the entry whole under the lock, so a read never sees half of one. Reads come back in
// the order the entries were first stated.

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/payload_bridge.h"

namespace {

struct Icon {
  uint64_t sequence = 0u;
  bool visible = false;
  uint32_t words[REFII_WORLD_ICON_WORDS]{};
  float values[REFII_WORLD_ICON_VALUES]{};
  std::string text;
};

struct Choice {
  uint64_t sequence = 0u;
  uint32_t words[REFII_EMOTION_CHOICE_WORDS]{};
  float values[REFII_EMOTION_CHOICE_VALUES]{};
  std::string text;
};

/// Far above the live population (a town of owned houses holds ~60 icons; the guest's choice list
/// holds a handful). A statement past the cap is dropped, never a live entry.
constexpr size_t kMaxIcons = 1024u;
constexpr size_t kMaxChoices = 64u;
constexpr uint32_t kMaxText = 4096u;

std::mutex g_lock;
std::unordered_map<uint32_t, Icon>* g_icons = nullptr;
std::unordered_map<uint32_t, Choice>* g_choices = nullptr;
uint64_t g_sequence = 0u;

std::unordered_map<uint32_t, Icon>& Icons() {
  if (g_icons == nullptr) {
    g_icons = new std::unordered_map<uint32_t, Icon>();
  }
  return *g_icons;
}

std::unordered_map<uint32_t, Choice>& Choices() {
  if (g_choices == nullptr) {
    g_choices = new std::unordered_map<uint32_t, Choice>();
  }
  return *g_choices;
}

std::string Text(const char* text, uint32_t length) {
  if (text == nullptr) {
    return std::string();
  }
  return std::string(text, std::min(length, kMaxText));
}

/// Appends `text` (NUL-separated strings, `strings` of them) to `out` at `*at`, NUL-terminating
/// each; false, writing nothing, when they do not all fit.
bool PutText(const std::string& text, uint32_t strings, char* out, uint32_t capacity, uint32_t* at) {
  std::vector<std::pair<size_t, size_t>> parts;
  size_t from = 0u;
  for (uint32_t i = 0; i < strings; ++i) {
    const size_t end = from <= text.size() ? text.find('\0', from) : std::string::npos;
    const size_t stop = end == std::string::npos ? text.size() : end;
    parts.emplace_back(std::min(from, text.size()), stop >= from ? stop - from : 0u);
    from = stop + 1u;
  }
  size_t need = 0u;
  for (const auto& [start, length] : parts) {
    need += length + 1u;
  }
  if (static_cast<size_t>(*at) + need > capacity) {
    return false;
  }
  for (const auto& [start, length] : parts) {
    std::memcpy(out + *at, text.data() + start, length);
    *at += static_cast<uint32_t>(length);
    out[(*at)++] = '\0';
  }
  return true;
}

template <typename Entry>
std::vector<std::pair<uint32_t, const Entry*>> Ordered(const std::unordered_map<uint32_t, Entry>& table) {
  std::vector<std::pair<uint32_t, const Entry*>> order;
  order.reserve(table.size());
  for (const auto& [key, entry] : table) {
    order.emplace_back(key, &entry);
  }
  std::sort(order.begin(), order.end(),
            [](const auto& a, const auto& b) { return a.second->sequence < b.second->sequence; });
  return order;
}

}  // namespace

extern "C" {

void refii_bridge_world_icon_stated(uint32_t icon, int32_t visible, const uint32_t* words, const float* values,
                                    const char* text, uint32_t text_length) {
  if (icon == 0u || words == nullptr || values == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto& icons = Icons();
  auto it = icons.find(icon);
  if (it == icons.end()) {
    if (icons.size() >= kMaxIcons) {
      return;
    }
    it = icons.emplace(icon, Icon{}).first;
    it->second.sequence = ++g_sequence;
  }
  Icon& entry = it->second;
  if (visible >= 0) {
    entry.visible = visible != 0;
  }
  std::memcpy(entry.words, words, sizeof(entry.words));
  std::memcpy(entry.values, values, sizeof(entry.values));
  entry.text = Text(text, text_length);
}

void refii_bridge_world_icon_removed(uint32_t icon) {
  std::lock_guard<std::mutex> guard(g_lock);
  Icons().erase(icon);
}

uint32_t refii_bridge_world_icons_read(uint32_t* out_icons, uint32_t* out_words, float* out_values, char* out_text,
                                       uint32_t text_capacity, uint32_t max_icons) {
  std::lock_guard<std::mutex> guard(g_lock);
  uint32_t written = 0u;
  uint32_t text_at = 0u;
  for (const auto& [icon, entry] : Ordered(Icons())) {
    if (written >= max_icons) {
      break;
    }
    if (out_text != nullptr && !PutText(entry->text, 3u, out_text, text_capacity, &text_at)) {
      break;
    }
    if (out_icons != nullptr) {
      out_icons[written] = icon;
    }
    if (out_words != nullptr) {
      uint32_t* w = out_words + (written * REFII_WORLD_ICON_WORDS);
      std::memcpy(w, entry->words, sizeof(entry->words));
      w[REFII_WORLD_ICON_FLAGS_WORD] = (w[REFII_WORLD_ICON_FLAGS_WORD] & ~REFII_WORLD_ICON_VISIBLE) |
                                       (entry->visible ? REFII_WORLD_ICON_VISIBLE : 0u);
    }
    if (out_values != nullptr) {
      std::memcpy(out_values + (written * REFII_WORLD_ICON_VALUES), entry->values, sizeof(entry->values));
    }
    ++written;
  }
  return written;
}

void refii_bridge_emotion_choice_stated(uint32_t record, const uint32_t* words, const float* values, const char* text,
                                        uint32_t text_length) {
  if (record == 0u || words == nullptr || values == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto& choices = Choices();
  auto it = choices.find(record);
  if (it == choices.end()) {
    if (choices.size() >= kMaxChoices) {
      return;
    }
    it = choices.emplace(record, Choice{}).first;
    it->second.sequence = ++g_sequence;
  }
  Choice& entry = it->second;
  std::memcpy(entry.words, words, sizeof(entry.words));
  std::memcpy(entry.values, values, sizeof(entry.values));
  entry.text = Text(text, text_length);
}

void refii_bridge_emotion_choice_removed(uint32_t record) {
  std::lock_guard<std::mutex> guard(g_lock);
  Choices().erase(record);
}

uint32_t refii_bridge_emotion_choices_read(uint32_t* out_records, uint32_t* out_words, float* out_values,
                                           char* out_text, uint32_t text_capacity, uint32_t max_choices) {
  std::lock_guard<std::mutex> guard(g_lock);
  uint32_t written = 0u;
  uint32_t text_at = 0u;
  for (const auto& [record, entry] : Ordered(Choices())) {
    if (written >= max_choices) {
      break;
    }
    if (out_text != nullptr && !PutText(entry->text, 3u, out_text, text_capacity, &text_at)) {
      break;
    }
    if (out_records != nullptr) {
      out_records[written] = record;
    }
    if (out_words != nullptr) {
      std::memcpy(out_words + (written * REFII_EMOTION_CHOICE_WORDS), entry->words, sizeof(entry->words));
    }
    if (out_values != nullptr) {
      std::memcpy(out_values + (written * REFII_EMOTION_CHOICE_VALUES), entry->values, sizeof(entry->values));
    }
    ++written;
  }
  return written;
}

}  // extern "C"
