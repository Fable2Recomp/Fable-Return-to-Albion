// refii — the native text bus (core/refii_mod_bus.h): hard mods' handlers by event name, run by each
// title's bus over the table text Lua and Godot get. Compiled into every title's payload.

#include "core/refii_mod_bus.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include <rex/logging.h>

namespace {

struct Subscription {
  std::string event;
  refii_mod_bus_fn fn;
  void* user;
  int32_t priority;
  int32_t kind;  // -1 until the title names it
};

struct Bus {
  std::mutex mutex;
  std::vector<Subscription> subscriptions;
  std::vector<std::pair<std::string, int32_t>> names;
};

/// Constructed on first use: subscriptions arrive from static initialisers in any order.
Bus& TheBus() {
  static Bus bus;
  return bus;
}

void Resolve(Bus& bus, Subscription& s) {
  for (const auto& name : bus.names) {
    if (name.first == s.event) {
      s.kind = name.second;
      return;
    }
  }
}

}  // namespace

extern "C" int32_t refii_mod_bus_subscribe(const char* event, refii_mod_bus_fn fn, void* user, int32_t priority) {
  if (event == nullptr || *event == 0 || fn == nullptr) {
    return 0;
  }
  Bus& bus = TheBus();
  std::lock_guard<std::mutex> lock(bus.mutex);
  Subscription s{event, fn, user, priority, -1};
  Resolve(bus, s);
  bus.subscriptions.push_back(std::move(s));
  std::stable_sort(bus.subscriptions.begin(), bus.subscriptions.end(),
                   [](const Subscription& a, const Subscription& b) { return a.priority > b.priority; });
  return 1;
}

extern "C" void refii_mod_bus_name(const char* event, int32_t kind) {
  if (event == nullptr || *event == 0) {
    return;
  }
  Bus& bus = TheBus();
  std::lock_guard<std::mutex> lock(bus.mutex);
  bus.names.emplace_back(event, kind);
  for (Subscription& s : bus.subscriptions) {
    if (s.kind < 0 && s.event == event) {
      s.kind = kind;
    }
  }
}

extern "C" int32_t refii_mod_bus_wants(int32_t kind) {
  Bus& bus = TheBus();
  std::lock_guard<std::mutex> lock(bus.mutex);
  for (const Subscription& s : bus.subscriptions) {
    if (s.kind == kind) {
      return 1;
    }
  }
  return 0;
}

extern "C" double refii_mod_bus_number(const char* fields, size_t length, const char* name) {
  if (fields == nullptr || name == nullptr) {
    return 0.0;
  }
  const std::string text(fields, length);
  const std::string key = std::string(name) + "=";
  size_t at = 0;
  while ((at = text.find(key, at)) != std::string::npos) {
    if (at == 0 || text[at - 1] == ',') {
      return std::strtod(text.c_str() + at + key.size(), nullptr);
    }
    at += key.size();
  }
  return 0.0;
}

extern "C" int32_t refii_mod_bus_run(int32_t kind, const char* fields, size_t length, char* out, size_t capacity) {
  if (out == nullptr || capacity == 0u) {
    return 0;
  }
  out[0] = 0;
  std::vector<Subscription> handlers;
  {
    Bus& bus = TheBus();
    std::lock_guard<std::mutex> lock(bus.mutex);
    for (const Subscription& s : bus.subscriptions) {
      if (s.kind == kind) {
        handlers.push_back(s);
      }
    }
  }
  std::string merged;
  for (const Subscription& s : handlers) {
    char answer[512];
    const int32_t n = s.fn(kind, fields, length, answer, sizeof(answer) - 1u, s.user);
    if (n <= 0) {
      continue;
    }
    answer[std::min<int32_t>(n, static_cast<int32_t>(sizeof(answer)) - 1)] = 0;
    if (!merged.empty()) {
      merged += ',';
    }
    merged += answer;
  }
  const size_t n = std::min(merged.size(), capacity - 1u);
  std::memcpy(out, merged.data(), n);
  out[n] = 0;
  return static_cast<int32_t>(n);
}
