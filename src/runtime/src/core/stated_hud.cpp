// refii — the HUD element registries, the screen fade and the aim reticules a title states (core/payload_bridge.h).
//
// THE REGISTRIES are the guest's own map shape: per registry, a name (the element name's FNV-1)
// holds every element filed under it, in filing order, and a writer addresses the name. A name
// leaves whole — the guest erases the map node, not one element — when an element filed under it
// is unfiled, and every name of a registry leaves with the registry. Each element carries what
// its writers last stated: the text, or the value and mode. A read answers one row per name, the
// first element's state: what the guest's own readers take (`*(*(record+4))`).
//
// THE FADE is stated at its start and at the edges of its advance, and run between them on the
// host's steady clock: progress = base + rate * (now - stamp) while running, held otherwise. A
// start holds until the guest's advance first moves it.
//
// THE RETICULES are each record as its setter last left it, read back as stated.
//
// It crosses from a title's hooks (the payload, on the guest's game thread) to its host bridge
// (on Godot's), which live in different modules; this is the one table both link.

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/payload_bridge.h"

namespace {

struct Element {
  uint32_t element = 0u;
  uint32_t source = 0u;
  float value = 0.0f;
  uint32_t mode = 0u;
  std::string text;
  /// A text the guest wrote (refii_bridge_hud_text_stated). A name filed and never written states
  /// no text: its element shows what the element's own writers committed.
  bool text_stated = false;
};

struct Name {
  uint64_t sequence = 0u;
  uint32_t kind = 0u;
  std::vector<Element> elements;
};

/// (registry, name hash).
using NameKey = std::pair<uint32_t, uint32_t>;

struct NameKeyHash {
  size_t operator()(const NameKey& key) const {
    return std::hash<uint64_t>()((static_cast<uint64_t>(key.first) << 32) | key.second);
  }
};

/// Far above the live population (hud.bgf files a few hundred names across its registries). A
/// filing past the cap is dropped, never a live name.
constexpr size_t kMaxNames = 4096u;
constexpr uint32_t kMaxText = 4096u;

std::mutex g_lock;
std::unordered_map<NameKey, Name, NameKeyHash>* g_names = nullptr;
/// element -> the name it is filed under, for the writers that address an element.
std::unordered_map<uint32_t, NameKey>* g_owner = nullptr;
/// (registry, source element) -> the name an element filed from it is under, for the unfiling.
std::unordered_map<uint64_t, NameKey>* g_sources = nullptr;
uint64_t g_sequence = 0u;

uint64_t SourceKey(uint32_t registry, uint32_t source) { return (static_cast<uint64_t>(registry) << 32) | source; }

std::unordered_map<NameKey, Name, NameKeyHash>& Names() {
  if (g_names == nullptr) {
    g_names = new std::unordered_map<NameKey, Name, NameKeyHash>();
  }
  return *g_names;
}

std::unordered_map<uint32_t, NameKey>& Owner() {
  if (g_owner == nullptr) {
    g_owner = new std::unordered_map<uint32_t, NameKey>();
  }
  return *g_owner;
}

std::unordered_map<uint64_t, NameKey>& Sources() {
  if (g_sources == nullptr) {
    g_sources = new std::unordered_map<uint64_t, NameKey>();
  }
  return *g_sources;
}

/// Erases the name and its elements' back references. Caller holds g_lock.
void EraseName(std::unordered_map<NameKey, Name, NameKeyHash>::iterator it) {
  auto& owner = Owner();
  auto& sources = Sources();
  for (const Element& element : it->second.elements) {
    auto back = owner.find(element.element);
    if (back != owner.end() && back->second == it->first) {
      owner.erase(back);
    }
    auto from = sources.find(SourceKey(it->first.first, element.source));
    if (from != sources.end() && from->second == it->first) {
      sources.erase(from);
    }
  }
  Names().erase(it);
}

/// The longest prefix of `text` that fits `room` bytes without splitting a UTF-8 sequence.
size_t Fit(const std::string& text, size_t room) {
  if (text.size() <= room) {
    return text.size();
  }
  size_t cut = room;
  while (cut > 0u && (static_cast<uint8_t>(text[cut]) & 0xC0u) == 0x80u) {
    --cut;
  }
  return cut;
}

using Clock = std::chrono::steady_clock;

struct Fade {
  uint32_t fader = 0u;
  uint8_t from[4]{};
  uint8_t to[4]{};
  float base = 0.0f;
  float rate = 0.0f;
  bool running = false;
  bool locked = false;
  Clock::time_point stamp{};
};

Fade g_fade;

struct Reticle {
  bool active = false;
  float values[REFII_RETICLE_VALUES]{};
  uint32_t words[REFII_RETICLE_WORDS]{};
};

/// [player][slot].
Reticle g_reticles[2][2];

float FadeProgress(const Fade& fade, Clock::time_point now) {
  if (!fade.running || fade.locked) {
    return fade.base;
  }
  const double seconds = std::chrono::duration<double>(now - fade.stamp).count();
  const double progress = static_cast<double>(fade.base) + (static_cast<double>(fade.rate) * seconds);
  return progress >= 1.0 ? 1.0f : static_cast<float>(progress);
}

}  // namespace

extern "C" {

void refii_bridge_hud_element_filed(uint32_t kind, uint32_t registry, uint32_t hash, uint32_t element,
                                    uint32_t source, float value, uint32_t mode) {
  if (registry == 0u || element == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto& names = Names();
  const NameKey key{registry, hash};
  auto it = names.find(key);
  if (it == names.end()) {
    if (names.size() >= kMaxNames) {
      return;
    }
    it = names.emplace(key, Name{}).first;
    it->second.sequence = ++g_sequence;
    it->second.kind = kind;
  }
  Element filed;
  filed.element = element;
  filed.source = source;
  filed.value = value;
  filed.mode = mode;
  it->second.elements.push_back(std::move(filed));
  Owner()[element] = key;
  if (source != 0u) {
    Sources()[SourceKey(registry, source)] = key;
  }
}

void refii_bridge_hud_source_unfiled(uint32_t registry, uint32_t source) {
  if (source == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  auto from = Sources().find(SourceKey(registry, source));
  if (from == Sources().end()) {
    return;
  }
  auto it = Names().find(from->second);
  if (it == Names().end()) {
    Sources().erase(from);
    return;
  }
  EraseName(it);
}

void refii_bridge_hud_registry_destroyed(uint32_t registry) {
  std::lock_guard<std::mutex> guard(g_lock);
  auto& names = Names();
  for (auto it = names.begin(); it != names.end();) {
    auto next = std::next(it);
    if (it->first.first == registry) {
      EraseName(it);
    }
    it = next;
  }
}

void refii_bridge_hud_text_stated(uint32_t registry, uint32_t hash, const char* text, uint32_t length) {
  std::lock_guard<std::mutex> guard(g_lock);
  auto& names = Names();
  auto it = names.find(NameKey{registry, hash});
  if (it == names.end()) {
    return;
  }
  const std::string value = text == nullptr ? std::string() : std::string(text, std::min(length, kMaxText));
  for (Element& element : it->second.elements) {
    element.text = value;
    element.text_stated = true;
  }
}

void refii_bridge_hud_value_stated(uint32_t element, float value) {
  std::lock_guard<std::mutex> guard(g_lock);
  auto owner = Owner().find(element);
  if (owner == Owner().end()) {
    return;
  }
  auto it = Names().find(owner->second);
  if (it == Names().end()) {
    return;
  }
  for (Element& filed : it->second.elements) {
    if (filed.element == element) {
      filed.value = value;
    }
  }
}

uint32_t refii_bridge_hud_read(uint32_t kind, uint32_t* out_hashes, float* out_values, uint32_t* out_modes,
                               char* out_text, uint32_t text_capacity, uint32_t max_rows) {
  std::lock_guard<std::mutex> guard(g_lock);
  std::vector<std::pair<uint64_t, const std::pair<const NameKey, Name>*>> order;
  for (const auto& entry : Names()) {
    if (entry.second.kind == kind && !entry.second.elements.empty() &&
        (kind != REFII_HUD_KIND_TEXT || entry.second.elements.front().text_stated)) {
      order.emplace_back(entry.second.sequence, &entry);
    }
  }
  std::sort(order.begin(), order.end(), [](const auto& a, const auto& b) { return a.first < b.first; });

  uint32_t text_at = 0u;
  bool text_full = out_text == nullptr || text_capacity == 0u;
  const uint32_t rows = static_cast<uint32_t>(order.size());
  for (uint32_t i = 0; i < rows && i < max_rows; ++i) {
    const auto& [key, name] = *order[i].second;
    const Element& first = name.elements.front();
    if (out_hashes != nullptr) {
      out_hashes[i] = key.second;
    }
    if (out_values != nullptr) {
      out_values[i] = first.value;
    }
    if (out_modes != nullptr) {
      out_modes[i] = first.mode;
    }
    if (!text_full) {
      const size_t room = text_capacity - text_at - 1u;
      const size_t length = Fit(first.text, room);
      std::memcpy(out_text + text_at, first.text.data(), length);
      text_at += static_cast<uint32_t>(length);
      out_text[text_at++] = '\0';
      text_full = text_at >= text_capacity;
    }
  }
  return rows;
}

void refii_bridge_screen_fade_started(uint32_t fader, const uint8_t* from, const uint8_t* to, float progress,
                                      float rate) {
  if (fader == 0u || from == nullptr || to == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  const bool locked = g_fade.fader == fader && g_fade.locked;
  g_fade.fader = fader;
  std::memcpy(g_fade.from, from, sizeof(g_fade.from));
  std::memcpy(g_fade.to, to, sizeof(g_fade.to));
  g_fade.base = progress;
  g_fade.rate = rate;
  // Held until the guest's own advance first moves it: a start inside a hold (the lock, a loading
  // mode) never runs ahead of the guest and back.
  g_fade.running = false;
  g_fade.locked = locked;
  g_fade.stamp = Clock::now();
}

void refii_bridge_screen_fade_stepped(uint32_t fader, float progress, int32_t advanced, int32_t locked) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_fade.fader != fader) {
    return;
  }
  const Clock::time_point now = Clock::now();
  const bool lock = locked != 0;
  const bool moving = advanced != 0;
  if (progress >= 1.0f) {
    if (g_fade.base < 1.0f || g_fade.running) {
      g_fade.base = 1.0f;
      g_fade.running = false;
      g_fade.stamp = now;
    }
  } else if (moving != g_fade.running) {
    g_fade.base = progress;
    g_fade.running = moving;
    g_fade.stamp = now;
  }
  if (lock != g_fade.locked) {
    g_fade.base = progress;
    g_fade.stamp = now;
    g_fade.locked = lock;
  }
}

uint32_t refii_bridge_screen_fade_read(uint8_t* out_from, uint8_t* out_to, float* out_progress,
                                       uint32_t* out_locked) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_from != nullptr) {
    std::memcpy(out_from, g_fade.from, sizeof(g_fade.from));
  }
  if (out_to != nullptr) {
    std::memcpy(out_to, g_fade.to, sizeof(g_fade.to));
  }
  if (out_progress != nullptr) {
    *out_progress = FadeProgress(g_fade, Clock::now());
  }
  if (out_locked != nullptr) {
    *out_locked = g_fade.locked ? 1u : 0u;
  }
  return g_fade.fader;
}

void refii_bridge_reticle_stated(uint32_t player, uint32_t slot, int32_t active, const float* values,
                                 const uint32_t* words) {
  if (player > 1u || slot > 1u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  Reticle& reticle = g_reticles[player][slot];
  reticle.active = active != 0;
  if (!reticle.active) {
    return;
  }
  if (values != nullptr) {
    std::memcpy(reticle.values, values, sizeof(reticle.values));
  }
  if (words != nullptr) {
    std::memcpy(reticle.words, words, sizeof(reticle.words));
  }
}

int32_t refii_bridge_reticle_read(uint32_t player, uint32_t slot, float* out_values, uint32_t* out_words) {
  if (player > 1u || slot > 1u) {
    return 0;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  const Reticle& reticle = g_reticles[player][slot];
  if (!reticle.active) {
    return 0;
  }
  if (out_values != nullptr) {
    std::memcpy(out_values, reticle.values, sizeof(reticle.values));
  }
  if (out_words != nullptr) {
    std::memcpy(out_words, reticle.words, sizeof(reticle.words));
  }
  return 1;
}

}  // extern "C"
