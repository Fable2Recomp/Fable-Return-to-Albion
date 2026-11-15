// refii — the native text bus: a hard mod's handler for any catalog event, by name. The
// handler gets the event's kind and the table text Lua and Godot get (`name=value,...`, the catalog's
// fields: Godot/src/Engine/mods/Engine_ModApi.cs), and answers as a Godot verdict does: nothing, or
// `cancel=1` and `field=number` pairs (comma separated) for the event's writable fields, which the
// title applies where the event is decided. Handlers run on the game's thread inside the seam,
// highest priority first, before the event's Lua handlers. Implemented in
// src/payload/src/core/mod_bus.cpp, compiled into every title's payload, and run by both titles'
// buses: Fable 3's (Fable3/src/hooks/src/mod_lua_hooks.cpp) and Fable 2's (mod_events.cpp, after its
// typed REFII_MOD_ON subscribers), so one native mod source hears either game.
//
//   static int32_t OnGold(int32_t kind, const char* fields, size_t length, char* out, size_t capacity, void* user) {
//     return std::snprintf(out, capacity, "delta=%d", 2 * refii_mod_bus_int(fields, length, "delta"));
//   }
//   REFII_MOD_BUS_ON("gold", OnGold, nullptr);

#pragma once

#include <cstddef>
#include <cstdint>

/// A handler: writes its answer (cancel=1, field=number) into `out` and returns its length, or 0.
typedef int32_t (*refii_mod_bus_fn)(int32_t kind, const char* fields, size_t length, char* out, size_t capacity,
                                    void* user);

/// Subscribes `fn` to the catalog event named `event` (its Lua name: "gold", "hit"...) at `priority`
/// (higher first). Matched by name when the title's bus names its kinds, so a mod's subscription made
/// while the module loads (REFII_MOD_BUS_ON) stands whatever the load order; a name the title never
/// raises is reported once, in the log, when the title names its kinds. Returns 1.
extern "C" int32_t refii_mod_bus_subscribe(const char* event, refii_mod_bus_fn fn, void* user, int32_t priority);

/// A number field of `fields` by name (0 when absent): the handler's reading helper.
extern "C" double refii_mod_bus_number(const char* fields, size_t length, const char* name);

/// THE TITLES' SIDE. Whether a native handler wants `kind`.
extern "C" int32_t refii_mod_bus_wants(int32_t kind);
/// Runs every handler of `kind` over `fields`, merging their answers into `out` (later pairs win;
/// a cancel stands once stated). Returns the answer's length.
extern "C" int32_t refii_mod_bus_run(int32_t kind, const char* fields, size_t length, char* out, size_t capacity);
/// Names a kind the title raises (its Lua name), from the title's bus before it runs any.
extern "C" void refii_mod_bus_name(const char* event, int32_t kind);

inline int32_t refii_mod_bus_int(const char* fields, size_t length, const char* name) {
  return static_cast<int32_t>(refii_mod_bus_number(fields, length, name));
}

#define REFII_MOD_BUS_JOIN2(a, b) a##b
#define REFII_MOD_BUS_JOIN(a, b) REFII_MOD_BUS_JOIN2(a, b)
/// Subscribes `fn` to `event` when the module loads, at priority 0.
#define REFII_MOD_BUS_ON(event, fn, user)                                                         \
  static const int32_t REFII_MOD_BUS_JOIN(refii_mod_bus_on_, __LINE__) =                        \
      refii_mod_bus_subscribe((event), (fn), (user), 0)
