// refii — the Refii.* natives in the GAME VM, for mods' Lua. Registered into the global `Refii`
// table before any mod's init.lua runs (mod_lua_hooks.cpp), once per Lua state.
//
// HOW A HOST FUNCTION BECOMES A LUA NATIVE. The runtime hands a host PPCFunc a guest address
// (refii_bridge_rex_allocate_thunk) that the guest's function table resolves back to it, so the
// VM's own lua_pushcclosure takes it like any of the game's 3,602 natives: called with r3 = L,
// answering r3 = the result count. The Lua C API it uses is the GAME runtime's (two Lua runtimes
// are linked; the UI's is never touched): versions/*/refii_config.toml names each function.
//
// NUMBERS. This Lua's number is a 32-bit float, so an entity is never passed as its address:
// every entity or object is a HANDLE (refii_mod_handle), the same one the events carry, and a
// definition or text hash is a "0x…" string (a number is accepted too). Results follow the same
// rule.
//
// SURFACE (all under Refii): publish(key, n) value(key) log(text) hash(name)
//   health(e) -> cur, max · set_health(e, v) · modify_health(e, delta, attacker)
//   position(e) -> x, y, z · set_position(e, x, y, z)
//   entity_tint(e, r, g, b [, a]) · entity_untint(e)
//   entity_alpha(e, a) · entity_scale(e, s) · entity_saturation_mask(e, on) · entity_drawable(e, on)
//   lock_target(p [, e]) · release_lock(p) · switch_target(p, left) -> target · locked_target(p) -> target
//   apply_status(e, kind, seconds [, level [, source]]) · clear_status(e, kind) · has_status(e, kind)
//   set_speed_multiplier(p, factor) · clear_speed_multiplier(p) · speed_multiplier(p) -> overridden, factor
//   current_action(e) -> action · action_elapsed(a) -> seconds · action_interruptible(a) · allow_action_interrupt(a)
//   action_type(a) -> EScriptableAction · action_name(a) -> its name
//   living(e) · spell_in_slot(p, slot) -> type · spell_parts(serial) -> table
//   knockback(e, x, y, z [, force]) · stagger(e)
//   play_sound(cue, e [, tag]) · play_sound_at(cue, x, y, z [, tag]) · stop_sound(e, tag [, fade_ms])
//   play_music(name) · stop_music(name)
//   play_animation(e, name [, loops [, blend_seconds [, speed]]]) · stop_animation(e)
//   modify_opinion(villager, hero, axis, delta) · add_deed(villager, hero, deed)
//   npc_follow(e, target [, radius]) · npc_go_to(e, x, y, z) · npc_flee(e, from) · npc_attack(e, target)
//   npc_set_hostile(e, other, on) · npc_stop(e)
//   set_time_of_day(hours) · time_of_day() -> hours · set_weather(theme [, weight [, seconds]])
//   gold(e) · give_gold(e, delta) · item_count(e, def) · add_item(e, def)
//   experience(e, pool) · give_experience(e, pool, amount) · spend_experience(e, pool, amount)
//   ability_level(e, type) · spell_serial(spell) -> serial
//   _cast_spell(caster, type, targeted, target, level, has_pos, px, py, pz, has_dir, dx, dy, dz) -> spell, serial
//   (cast_spell(caster, type, targeted, target, level [, pos [, dir]]) is the preamble's: it unpacks the tables)
//   relation(a, b) · alive(e) · definition(e) · name(e) · player_of(e) · level_name()
//   player_hero(p) · local_hero() · menu_player() · player_value(p, key [, fallback])
//   player_set(p, key, v) · player_add(p, key, d [, saved]) · player_save(p, key, v) · player_here(p)
//   event_replica() · wheel_switches_target(e)
//   renown(e) · morality(e) · purity(e) · physique(e) -> fat, height
//   age(e) · inventory(e) · equipped(e) -> melee, ranged · near(x, y, z, r [, creatures])
//   line_of_sight(x1, y1, z1, x2, y2, z2) · spawn(def, x, y, z) · entity(handle) · handle(entity)
//   forgive_crimes(villager, p) · discovered_crime(villager, p) · perceived_renown(villager, p)
//   stop_fx(id) · _play_fx(name, x, y, z, e, duration, scale, r, g, b, intensity, loop, ox, oy, oz)
//   (play_fx / play_fx_on are the preamble's, Engine_ModLua: they unpack the options table)
//   open_menu(name [, p]) · _message_box(title, text, accept, cancel, question, p)
//   (message_box is the preamble's: it fills the defaults)
//   notice(title, text [, seconds [, p]]) · _menu_box(title, text, p, cancelable, n, o1 .. o12)
//   _mark(e, x, y, z, icon, text, p) (mark is the preamble's) · unmark(id) · fade(p, dir [, seconds])
//   meter(id, label, value, max [, p [, style]]) · meter_remove(id)
//   _light(e, x, y, z, r, g, b, energy, range, ox, oy, oz, at, flicker, dx, dy, dz, cone, attenuation) (light is the preamble's)
//   light_remove(id) · send_to_partner(mod, name, payload)
//   (menu_box is the preamble's: it unpacks the options table)
//   _menu_rows(menu, rows, p) · open_screen(id [, p])
//   (set_menu_rows is the preamble's: it writes the rows table as the bus text)
//   _write(kind, field, value) · _error(text)

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/rex_payload_bridge.h"
#include "hooks/ppc_externs.h"

/// The local co-op screen mode (guest_tick_pump.cpp).
extern "C" int RefiiCoopSplitScreen(void);
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);
extern "C" void RefiiCoopSetPartnerDog(uint32_t dog);
extern "C" void RefiiCoopSetJobWorker(int player);
extern "C" void RefiiCoopPartnerScreen(int on);
extern "C" void RefiiCoopSetEstateBuyer(int player);
extern "C" void refii_bridge_estate_intent_post(const char* text);
extern "C" uint32_t refii_household_resolve_uid(PPCContext* ctx, uint8_t* base, uint64_t uid);
extern "C" void refii_household_context(int32_t on);
extern "C" void refii_household_forward_speech(uint8_t* base, uint32_t speaker, float seconds);
extern "C" int32_t refii_household_children_two(uint8_t* base);
extern "C" int32_t refii_household_follower_two(PPCContext* ctx, uint8_t* base, int32_t index, uint32_t* entity,
                                                uint32_t* flags);

PPC_EXTERN_FUNC(rex_lua_pushcclosure);
PPC_EXTERN_FUNC(rex_lua_getfield);
PPC_EXTERN_FUNC(rex_lua_setfield);
PPC_EXTERN_FUNC(rex_lua_createtable);
PPC_EXTERN_FUNC(rex_lua_gettop);
PPC_EXTERN_FUNC(rex_lua_settop);
PPC_EXTERN_FUNC(rex_lua_type);
PPC_EXTERN_FUNC(rex_lua_tonumber);
PPC_EXTERN_FUNC(rex_lua_toboolean);
PPC_EXTERN_FUNC(rex_lua_tolstring);
PPC_EXTERN_FUNC(rex_lua_pushnil);
PPC_EXTERN_FUNC(rex_lua_pushnumber);
PPC_EXTERN_FUNC(rex_lua_pushboolean);
PPC_EXTERN_FUNC(rex_lua_pushstring);
PPC_EXTERN_FUNC(rex_lua_touserdata);
PPC_EXTERN_FUNC(rex_lua_rawseti);
PPC_EXTERN_FUNC(rex_lhLua_PushEntity);
PPC_EXTERN_FUNC(rex_lhLua_IsFableType);
PPC_EXTERN_FUNC(rex_lhScriptEntityRef_Resolve);

namespace refii::vfs {
bool WriteModEventField(int32_t kind, const char* field, double value);
void NoteLuaCancelled();
void BoxSeatEnter(uint8_t* base, int32_t player);
void BoxSeatLeave(uint8_t* base);
uint32_t PlayModFxOnHandle(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options);
uint32_t PlaceModLightOnHandle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                               const refii_mod_light_options* options);
}

namespace {

constexpr int32_t kGlobalsIndex = -10002;
constexpr int32_t kTypeString = 4;
constexpr int32_t kTypeTable = 5;
/// A guest function inside the game module, for the thunk pool (RunScriptBuffer, TU1).
constexpr uint32_t kModuleAnchor = 0x82451F18u;
constexpr uint32_t kScratchBytes = 0x100u;
/// The frame's scratch as a whole (the native's frame is 0x400 bytes, scratch from +0x40).
constexpr uint32_t kLongScratchBytes = 0x300u;
/// The LHString holding "FableType_CEntity", the Entity userdata's metatable name in the registry
/// (set by sub_8324BD18; rex_lhLua_PushEntity reads it, TU1 0x823A2FEC).
constexpr uint32_t kEntityTypeName = 0x834979B0u;
constexpr int32_t kTypeNumber = 3;

/// The Lua C API over a guest frame: every call below the native's own stack pointer, strings
/// staged in the frame's scratch.
struct Lua {
  PPCContext& ctx;
  uint8_t* base;
  uint32_t L;
  uint32_t scratch;

  Lua(PPCContext& c, uint8_t* b, uint32_t state) : ctx(c), base(b), L(state), scratch(c.r1.u32 + 0x40u) {}

  PPCContext Work() const {
    PPCContext work = ctx;
    work.r3.u32 = L;
    return work;
  }

  /// `text` copied into the frame's scratch; the guest address of the copy.
  uint32_t Stage(const char* text) {
    size_t n = std::strlen(text);
    if (n >= kScratchBytes - 1) {
      n = kScratchBytes - 1;
    }
    std::memcpy(base + scratch, text, n);
    base[scratch + n] = 0;
    return scratch;
  }

  int32_t Top() {
    PPCContext w = Work();
    rex_lua_gettop(w, base);
    return static_cast<int32_t>(w.r3.u32);
  }
  void SetTop(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_settop(w, base);
  }
  int32_t Type(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_type(w, base);
    return static_cast<int32_t>(w.r3.u32);
  }
  double Number(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_tonumber(w, base);
    return w.f1.f64;
  }
  bool Boolean(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_toboolean(w, base);
    return w.r3.u32 != 0;
  }
  /// The string at `index` copied into `out` (empty for a non-string).
  void String(int32_t index, char* out, size_t size) {
    out[0] = 0;
    if (Type(index) != kTypeString) {
      return;
    }
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = 0;
    rex_lua_tolstring(w, base);
    const uint32_t text = w.r3.u32;
    if (text < 0x10000u || text >= 0xC0000000u) {
      return;
    }
    size_t n = 0;
    for (; n + 1 < size; ++n) {
      const char c = static_cast<char>(base[text + n]);
      if (c == 0) {
        break;
      }
      out[n] = c;
    }
    out[n] = 0;
  }
  void PushNil() {
    PPCContext w = Work();
    rex_lua_pushnil(w, base);
  }
  void PushNumber(double value) {
    PPCContext w = Work();
    w.f1.f64 = value;
    rex_lua_pushnumber(w, base);
  }
  void PushBoolean(bool value) {
    PPCContext w = Work();
    w.r4.u32 = value ? 1u : 0u;
    rex_lua_pushboolean(w, base);
  }
  void PushString(const char* text) {
    PPCContext w = Work();
    w.r4.u32 = Stage(text);
    rex_lua_pushstring(w, base);
  }
  /// A string of up to kLongScratchBytes - 1 bytes (the route's lines): staged across the frame's
  /// whole scratch, which nothing else holds while it is pushed.
  void PushLongString(const char* text) {
    size_t n = std::strlen(text);
    if (n >= kLongScratchBytes - 1) {
      n = kLongScratchBytes - 1;
    }
    std::memcpy(base + scratch, text, n);
    base[scratch + n] = 0;
    PPCContext w = Work();
    w.r4.u32 = scratch;
    rex_lua_pushstring(w, base);
  }
  void PushClosure(uint32_t function) {
    PPCContext w = Work();
    w.r4.u32 = function;
    w.r5.u32 = 0;
    rex_lua_pushcclosure(w, base);
  }
  void GetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_getfield(w, base);
  }
  void SetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_setfield(w, base);
  }
  /// t[n] = the value on top (popped), t at `index`.
  void RawSetI(int32_t index, int32_t n) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.s64 = n;
    rex_lua_rawseti(w, base);
  }
  void NewTable() {
    PPCContext w = Work();
    w.r4.u32 = 0;
    w.r5.u32 = 32;
    rex_lua_createtable(w, base);
  }

  // Argument readers in the API's terms.
  /// A whole number read safely: NaN and anything outside [lo, hi] clamp (a raw float-to-int cast
  /// is undefined there).
  static double Clamp(double value, double lo, double hi) {
    if (std::isnan(value)) {
      return 0.0;
    }
    return value < lo ? lo : (value > hi ? hi : value);
  }
  uint32_t Entity(int32_t index) {
    return refii_mod_handle_address(static_cast<uint32_t>(Clamp(Number(index), 0.0, 4294967295.0)));
  }
  int32_t Int(int32_t index) { return static_cast<int32_t>(Clamp(Number(index), -2147483648.0, 2147483647.0)); }
  /// A hash: a "0x…" (or decimal) string, or a number.
  uint32_t Hash(int32_t index) {
    if (Type(index) == kTypeString) {
      char text[64];
      String(index, text, sizeof(text));
      return static_cast<uint32_t>(std::strtoul(text, nullptr, 0));
    }
    return static_cast<uint32_t>(Clamp(Number(index), 0.0, 4294967295.0));
  }
  void PushHash(uint32_t hash) {
    char text[16];
    std::snprintf(text, sizeof(text), "0x%08x", hash);
    PushString(text);
  }
  void PushHandle(uint32_t address) {
    if (address == 0) {
      PushNil();
    } else {
      PushNumber(refii_mod_handle(address));
    }
  }
};

}  // namespace

namespace {
/// How many values the running native pushed.
int32_t g_results = 0;
}  // namespace
#define RESULTS(n) (g_results = (n))

namespace {

/// Each native runs with the guest's own stack pointer: its frame is where the VM called it, so
/// the API calls are made from a frame below it. r3 = L in, r3 = the result count out.
#define REFII_LUA_NATIVE(name)                                   \
  void name##Body(Lua& lua);                                     \
  void name(PPCContext& ctx, uint8_t* base) {                    \
    const uint32_t state = ctx.r3.u32;                           \
    PPCContext frame = refii::mods::Frame(ctx, 0x400u);          \
    Lua lua(frame, base, state);                                 \
    g_results = 0;                                               \
    name##Body(lua);                                             \
    ctx.r3.u64 = static_cast<uint32_t>(g_results);               \
  }                                                              \
  void name##Body(Lua& lua)

REFII_LUA_NATIVE(NativePublish) {
  char key[128];
  lua.String(1, key, sizeof(key));
  if (key[0] != 0) {
    refii::mods::Publish(key, static_cast<float>(lua.Number(2)));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeValue) {
  char key[128];
  lua.String(1, key, sizeof(key));
  float value = 0.0f;
  if (key[0] != 0 && refii::mods::Published(key, value)) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLog) {
  char text[256];
  lua.String(1, text, sizeof(text));
  REXLOG_INFO("[mod lua] {}", text);
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeHash) {
  char text[256];
  lua.String(1, text, sizeof(text));
  lua.PushHash(refii_mod_fnv1(text));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeHealth) {
  float current = 0.0f, max = 0.0f;
  if (refii_mod_health(lua.base, lua.Entity(1), &current, &max) != 0) {
    lua.PushNumber(current);
    lua.PushNumber(max);
    RESULTS(2);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

REFII_LUA_NATIVE(NativeSetHealth) {
  lua.PushBoolean(refii_mod_set_health(lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeModifyHealth) {
  lua.PushBoolean(refii_mod_modify_health(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2)),
                                          lua.Entity(3)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePosition) {
  float xyz[3] = {0, 0, 0};
  if (refii_mod_entity_position(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0) {
    lua.PushNumber(xyz[0]);
    lua.PushNumber(xyz[1]);
    lua.PushNumber(xyz[2]);
    RESULTS(3);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

REFII_LUA_NATIVE(NativeSetPosition) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_set_position(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0);
  RESULTS(1);
}

// Refii.entity_tint(e, r, g, b [, a]): a defaults to 1.
REFII_LUA_NATIVE(NativeEntityTint) {
  const float a = lua.Type(5) == kTypeNumber ? static_cast<float>(lua.Number(5)) : 1.0f;
  lua.PushBoolean(refii_mod_entity_tint(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2)),
                                        static_cast<float>(lua.Number(3)), static_cast<float>(lua.Number(4)), a) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntityUntint) {
  lua.PushBoolean(refii_mod_entity_untint(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// The other component-3 setters: Refii.entity_alpha(e, a) · entity_scale(e, s) ·
// entity_saturation_mask(e, on) · entity_drawable(e, on). `on` is a Lua boolean or number.
REFII_LUA_NATIVE(NativeEntityAlpha) {
  lua.PushBoolean(refii_mod_entity_alpha(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntityScale) {
  lua.PushBoolean(refii_mod_entity_scale(&lua.ctx, lua.base, lua.Entity(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntitySaturationMask) {
  const bool on = lua.Type(2) == kTypeNumber ? lua.Number(2) != 0.0 : lua.Boolean(2);
  lua.PushBoolean(refii_mod_entity_saturation_mask(&lua.ctx, lua.base, lua.Entity(1), on ? 1 : 0) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEntityDrawable) {
  const bool on = lua.Type(2) == kTypeNumber ? lua.Number(2) != 0.0 : lua.Boolean(2);
  lua.PushBoolean(refii_mod_entity_drawable(&lua.ctx, lua.base, lua.Entity(1), on ? 1 : 0) != 0);
  RESULTS(1);
}

// The target lock: Refii.lock_target(p [, e]) (e nil or 0: the game's own pick) · release_lock(p)
// · switch_target(p, left) -> the target held afterwards · locked_target(p) -> the target or nil.
REFII_LUA_NATIVE(NativeLockTarget) {
  const uint32_t entity = lua.Type(2) == kTypeNumber ? lua.Entity(2) : 0u;
  lua.PushBoolean(refii_mod_lock_target(&lua.ctx, lua.base, lua.Int(1), entity) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeReleaseLock) {
  lua.PushBoolean(refii_mod_release_lock(&lua.ctx, lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSwitchTarget) {
  const bool left = lua.Type(2) == kTypeNumber ? lua.Number(2) != 0.0 : lua.Boolean(2);
  lua.PushHandle(refii_mod_switch_target(&lua.ctx, lua.base, lua.Int(1), left ? 1 : 0));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLockedTarget) {
  lua.PushHandle(refii_mod_locked_target(&lua.ctx, lua.base, lua.Int(1)));
  RESULTS(1);
}

// Movement speed: Refii.set_speed_multiplier(p, factor) · clear_speed_multiplier(p) ·
// speed_multiplier(p) -> overridden, factor (nil without a player component).
REFII_LUA_NATIVE(NativeSetSpeedMultiplier) {
  lua.PushBoolean(refii_mod_set_speed_multiplier(lua.base, lua.Int(1), static_cast<float>(lua.Number(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeClearSpeedMultiplier) {
  lua.PushBoolean(refii_mod_clear_speed_multiplier(lua.base, lua.Int(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSpeedMultiplier) {
  float factor = 1.0f;
  const uint32_t hero = refii_mod_player_hero(lua.base, lua.Int(1));
  if (hero == 0 || refii_mod_component(lua.base, hero, 33u) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  const bool overridden = refii_mod_speed_multiplier(lua.base, lua.Int(1), &factor) != 0;
  lua.PushBoolean(overridden);
  lua.PushNumber(factor);
  RESULTS(2);
}

// Actions: Refii.current_action(e) -> action or nil · action_elapsed(a) -> seconds or nil ·
// action_interruptible(a) · allow_action_interrupt(a). An action is a handle like any object.
REFII_LUA_NATIVE(NativeCurrentAction) {
  lua.PushHandle(refii_mod_current_action(lua.base, lua.Entity(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeActionElapsed) {
  float seconds = 0.0f;
  if (refii_mod_action_elapsed(&lua.ctx, lua.base, lua.Entity(1), &seconds) != 0) {
    lua.PushNumber(seconds);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeActionInterruptible) {
  lua.PushBoolean(refii_mod_action_interruptible(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAllowActionInterrupt) {
  lua.PushBoolean(refii_mod_allow_action_interrupt(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Refii.action_type(a) -> EScriptableAction (0..88) or nil · Refii.action_name(a) -> its name or
// nil: what the action was made from (refii_mod_action_type, the factory seam's record).
REFII_LUA_NATIVE(NativeActionType) {
  const int32_t type = refii_mod_action_type(lua.base, lua.Entity(1));
  if (type >= 0) {
    lua.PushNumber(type);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeActionName) {
  char name[64];
  if (refii_mod_action_name(lua.base, lua.Entity(1), name, sizeof(name)) != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.living(e): in the world and, for a creature, not yet died (refii_mod_entity_living).
REFII_LUA_NATIVE(NativeLiving) {
  lua.PushBoolean(refii_mod_entity_living(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Refii.spell_in_slot(p, slot) -> the spell type held, -1 for none.
REFII_LUA_NATIVE(NativeSpellInSlot) {
  lua.PushNumber(refii_mod_spell_in_slot(lua.base, lua.Int(1), lua.Int(2)));
  RESULTS(1);
}

// Refii.spell_parts(serial) -> { spell, type, targeted, level, caster, caster_player, vortex,
// anchor, swords = { e1, .. }, origin = { x, y, z } or nil, exploded } (handles; absent parts
// nil), or nil for a cast the factory never saw (refii_mod_spell_parts).
REFII_LUA_NATIVE(NativeSpellParts) {
  refii_mod_cast_parts parts{};
  if (refii_mod_spell_parts(&lua.ctx, lua.base, static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)), &parts) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  const auto handle_field = [&lua](const char* name, uint32_t address) {
    if (address != 0) {
      lua.PushNumber(refii_mod_handle(address));
      lua.SetField(-2, name);
    }
  };
  handle_field("spell", parts.spell);
  lua.PushNumber(parts.type);
  lua.SetField(-2, "type");
  lua.PushBoolean(parts.targeted != 0);
  lua.SetField(-2, "targeted");
  lua.PushNumber(parts.level);
  lua.SetField(-2, "level");
  handle_field("caster", parts.caster);
  lua.PushNumber(parts.caster_player);
  lua.SetField(-2, "caster_player");
  handle_field("vortex", parts.vortex);
  handle_field("anchor", parts.anchor);
  lua.NewTable();
  for (int32_t i = 0; i < parts.sword_count; ++i) {
    lua.PushNumber(refii_mod_handle(parts.swords[i]));
    lua.RawSetI(-2, i + 1);
  }
  lua.SetField(-2, "swords");
  if (parts.has_origin != 0) {
    lua.NewTable();
    for (int32_t i = 0; i < 3; ++i) {
      lua.PushNumber(parts.origin[i]);
      lua.RawSetI(-2, i + 1);
    }
    lua.SetField(-2, "origin");
  }
  lua.PushBoolean(parts.exploded != 0);
  lua.SetField(-2, "exploded");
  RESULTS(1);
}

// Statuses: Refii.apply_status(e, kind, seconds [, level [, source]]) · clear_status(e, kind) ·
// has_status(e, kind); kinds 1 electrified 2 chaos 3 burning 4 vortex (the status_effect event's).
REFII_LUA_NATIVE(NativeApplyStatus) {
  const int32_t level = lua.Type(4) == kTypeNumber ? lua.Int(4) : 0;
  const uint32_t source = lua.Type(5) == kTypeNumber ? lua.Entity(5) : 0u;
  lua.PushBoolean(refii_mod_apply_status(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2), static_cast<float>(lua.Number(3)),
                                         level, source) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeClearStatus) {
  lua.PushBoolean(refii_mod_clear_status(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeHasStatus) {
  lua.PushBoolean(refii_mod_has_status(lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

// Knockback and stagger: Refii.knockback(e, x, y, z [, force]) (force defaults to 1) · stagger(e).
REFII_LUA_NATIVE(NativeKnockback) {
  const float dir[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  const float force = lua.Type(5) == kTypeNumber ? static_cast<float>(lua.Number(5)) : 1.0f;
  lua.PushBoolean(refii_mod_knockback(&lua.ctx, lua.base, lua.Entity(1), dir, force) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStagger) {
  lua.PushBoolean(refii_mod_stagger(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Sound: Refii.play_sound(cue, e [, tag]) · play_sound_at(cue, x, y, z [, tag]) ·
// stop_sound(e, tag [, fade_ms]) (e 0: the point emitters) · play_music(name) · stop_music(name).
REFII_LUA_NATIVE(NativePlaySound) {
  char cue[128];
  char tag[128];
  lua.String(1, cue, sizeof(cue));
  lua.String(3, tag, sizeof(tag));
  lua.PushBoolean(refii_mod_play_sound_on(&lua.ctx, lua.base, lua.Entity(2), cue, tag) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlaySoundAt) {
  char cue[128];
  char tag[128];
  lua.String(1, cue, sizeof(cue));
  lua.String(5, tag, sizeof(tag));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_play_sound_at(&lua.ctx, lua.base, xyz, cue, tag) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStopSound) {
  char tag[128];
  lua.String(2, tag, sizeof(tag));
  const int32_t fade = lua.Type(3) == kTypeNumber ? lua.Int(3) : 0;
  const uint32_t entity = lua.Type(1) == kTypeNumber && lua.Number(1) != 0.0 ? lua.Entity(1) : 0u;
  lua.PushBoolean(refii_mod_stop_sound(&lua.ctx, lua.base, entity, tag, fade) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayMusic) {
  char name[128];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(refii_mod_play_music(&lua.ctx, lua.base, name) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStopMusic) {
  char name[128];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(refii_mod_stop_music(&lua.ctx, lua.base, name) != 0);
  RESULTS(1);
}

// Animation: Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]]) ·
// stop_animation(e). loops defaults to 1, blend to the action's 0.1 s, speed to 1.
REFII_LUA_NATIVE(NativePlayAnimation) {
  char name[64];
  lua.String(2, name, sizeof(name));
  const int32_t loops = lua.Type(3) == kTypeNumber ? lua.Int(3) : 1;
  const float blend = lua.Type(4) == kTypeNumber ? static_cast<float>(lua.Number(4)) : 0.1f;
  const float speed = lua.Type(5) == kTypeNumber ? static_cast<float>(lua.Number(5)) : 1.0f;
  lua.PushBoolean(refii_mod_play_animation(&lua.ctx, lua.base, lua.Entity(1), name, loops, blend, speed) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeStopAnimation) {
  lua.PushBoolean(refii_mod_stop_animation(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Opinion and deeds: Refii.modify_opinion(villager, hero, axis, delta) · add_deed(villager, hero,
// deed) — deed a "0x…" string (Refii.hash of the Deed record's name).
REFII_LUA_NATIVE(NativeModifyOpinion) {
  lua.PushBoolean(refii_mod_modify_opinion(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), lua.Int(3),
                                           static_cast<float>(lua.Number(4))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAddDeed) {
  lua.PushBoolean(refii_mod_add_deed(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), lua.Hash(3)) != 0);
  RESULTS(1);
}

// NPC behaviour: Refii.npc_follow(e, target [, radius]) · npc_go_to(e, x, y, z) · npc_flee(e, from)
// · npc_attack(e, target) · npc_set_hostile(e, other, on) · npc_stop(e) — the quests' own orders
// (refii_mod_npc_*), run in the game VM. radius defaults to the quests' 2.
REFII_LUA_NATIVE(NativeNpcFollow) {
  const float radius = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : 2.0f;
  lua.PushBoolean(refii_mod_npc_follow(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), radius) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcGoTo) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)), static_cast<float>(lua.Number(4))};
  lua.PushBoolean(refii_mod_npc_go_to(&lua.ctx, lua.base, lua.Entity(1), xyz) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcFlee) {
  lua.PushBoolean(refii_mod_npc_flee(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcAttack) {
  lua.PushBoolean(refii_mod_npc_attack(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcSetHostile) {
  const bool on = lua.Type(3) == kTypeNumber ? lua.Number(3) != 0.0 : lua.Boolean(3);
  lua.PushBoolean(refii_mod_npc_set_hostile(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2), on ? 1 : 0) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeNpcStop) {
  lua.PushBoolean(refii_mod_npc_stop(&lua.ctx, lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

// Time and weather: Refii.set_time_of_day(hours) · time_of_day() -> hours ·
// set_weather(theme [, weight [, seconds]]) — the game clock's own setter (refii_mod_set_time_of_day)
// and the quests' environment-theme blend (refii_mod_set_weather); weight defaults to 1, seconds to 1.
REFII_LUA_NATIVE(NativeSetTimeOfDay) {
  lua.PushBoolean(refii_mod_set_time_of_day(&lua.ctx, lua.base, static_cast<float>(lua.Number(1))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeTimeOfDay) {
  float hours = 0.0f;
  if (refii_mod_time_of_day(lua.base, &hours) != 0) {
    lua.PushNumber(hours);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSetWeather) {
  char theme[64];
  lua.String(1, theme, sizeof(theme));
  const float weight = lua.Type(2) == kTypeNumber ? static_cast<float>(lua.Number(2)) : 1.0f;
  const float seconds = lua.Type(3) == kTypeNumber ? static_cast<float>(lua.Number(3)) : 1.0f;
  lua.PushBoolean(refii_mod_set_weather(&lua.ctx, lua.base, theme, weight, seconds) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeGold) {
  int32_t gold = 0;
  if (refii_mod_gold(lua.base, lua.Entity(1), &gold) != 0) {
    lua.PushNumber(gold);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeGiveGold) {
  lua.PushBoolean(refii_mod_give_gold(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeItemCount) {
  lua.PushNumber(refii_mod_item_count(lua.base, lua.Entity(1), lua.Hash(2)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAddItem) {
  lua.PushHandle(refii_mod_add_item(&lua.ctx, lua.base, lua.Entity(1), lua.Hash(2)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeExperience) {
  float amount = 0.0f;
  if (refii_mod_experience(lua.base, lua.Entity(1), lua.Int(2), &amount) != 0) {
    lua.PushNumber(amount);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeGiveExperience) {
  lua.PushBoolean(refii_mod_give_experience(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2),
                                            static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSpendExperience) {
  lua.PushBoolean(refii_mod_spend_experience(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2),
                                             static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAbilityLevel) {
  const int32_t level = refii_mod_ability_level(lua.base, lua.Entity(1), lua.Int(2));
  if (level >= 0) {
    lua.PushNumber(level);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._cast_spell(caster, type, targeted, target, level, has_pos, px, py, pz, has_dir, dx, dy,
// dz) -> spell, serial: refii_mod_cast_spell with the start position and facing when stated (the
// preamble's Refii.cast_spell unpacks its pos / dir tables, as play_fx does its options). The
// serial is read from the object the factory returned, so a Lua mod knows its own cast by the
// number its hits carry — the object's spell_created is raised inside this call and reaches Lua
// deferred, after the chunk. Nil, nil for no cast.
REFII_LUA_NATIVE(NativeCastSpell) {
  const bool targeted = lua.Type(3) == 1 ? lua.Boolean(3) : lua.Int(3) != 0;
  const float pos[3] = {static_cast<float>(lua.Number(7)), static_cast<float>(lua.Number(8)),
                        static_cast<float>(lua.Number(9))};
  const float dir[3] = {static_cast<float>(lua.Number(11)), static_cast<float>(lua.Number(12)),
                        static_cast<float>(lua.Number(13))};
  const uint32_t spell = refii_mod_cast_spell(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2), targeted ? 1 : 0, lua.Entity(4),
                                              lua.Int(5), lua.Int(6) != 0 ? pos : nullptr, lua.Int(10) != 0 ? dir : nullptr);
  lua.PushHandle(spell);
  const uint32_t serial = refii_mod_spell_serial(lua.base, spell);
  if (serial != 0) {
    lua.PushNumber(serial);
  } else {
    lua.PushNil();
  }
  RESULTS(2);
}

// Refii.spell_serial(spell) -> serial: the cast number a spell object's hits carry
// (refii_mod_spell_serial), or nil for no object. A spell handle is the object's address for the
// object's life and is never forgotten: the serial, not the handle, is a cast's identity.
REFII_LUA_NATIVE(NativeSpellSerial) {
  const uint32_t serial = refii_mod_spell_serial(lua.base, lua.Entity(1));
  if (serial != 0) {
    lua.PushNumber(serial);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeRelation) {
  lua.PushNumber(refii_mod_relation(&lua.ctx, lua.base, lua.Entity(1), lua.Entity(2)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeAlive) {
  lua.PushBoolean(refii_mod_entity_alive(lua.base, lua.Entity(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeDefinition) {
  const uint32_t definition = refii_mod_entity_definition(lua.base, lua.Entity(1));
  if (definition != 0) {
    lua.PushHash(definition);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeName) {
  char name[128];
  if (refii_mod_entity_name(lua.base, lua.Entity(1), name, sizeof(name)) != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerOf) {
  lua.PushNumber(refii_mod_player_of(lua.base, lua.Entity(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLevelName) {
  char name[128];
  if (refii_mod_level_name(lua.base, name, sizeof(name)) != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerHero) {
  lua.PushHandle(refii_mod_player_hero(lua.base, lua.Int(1)));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSplitScreen) {
  lua.PushBoolean(RefiiCoopSplitScreen() != 0);
  RESULTS(1);
}

// Refii.player_dog(p): player p's dog — hero one's is the game's own GetDog, player two's the
// co-op dog bound to them.
REFII_LUA_NATIVE(NativePlayerDog) {
  lua.PushHandle(RefiiCoopPlayerDog(lua.base, lua.Int(1)));
  RESULTS(1);
}

// Refii._set_partner_dog([dog]): records player two's dog; with none, clears the record (the
// core Lua's breed change, so the replacement is recorded by its first bind).
REFII_LUA_NATIVE(NativeSetPartnerDog) {
  RefiiCoopSetPartnerDog(lua.Type(1) == kTypeNumber ? lua.Entity(1) : 0u);
  RESULTS(0);
}

// Refii._job_worker(p) / Refii._estate_buyer(p): the core Lua's statements of who works the
// running job and who the real-estate screen about to open buys for (0 none, 1, 2).
REFII_LUA_NATIVE(NativeJobWorker) {
  RefiiCoopSetJobWorker(lua.Int(1));
  RESULTS(0);
}

// Refii._partner_screen(on): "on screen" answers player two's view until turned off (nests).
REFII_LUA_NATIVE(NativePartnerScreen) {
  RefiiCoopPartnerScreen(lua.Boolean(1) ? 1 : 0);
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeEstateBuyer) {
  RefiiCoopSetEstateBuyer(lua.Int(1));
  RESULTS(0);
}

// Core co-op only: Refii._household(action, uid, value) sends player two's household action to the
// co-op peer (`<action>:<uid hex>:<value>`, the pump's Estate: action there; mod_events.cpp). `uid`
// is Refii.uid's text ("0x…").
REFII_LUA_NATIVE(NativeHousehold) {
  char action[24];
  char uid[32];
  lua.String(1, action, sizeof(action));
  lua.String(2, uid, sizeof(uid));
  const unsigned long long value = std::strtoull(uid, nullptr, 16);
  char text[96];
  std::snprintf(text, sizeof(text), "%s:%016llX:%d", action, value, lua.Int(3));
  if (action[0] != 0) {
    refii_bridge_estate_intent_post(text);
  }
  RESULTS(0);
}

// Core co-op only: Refii._by_uid(uid) — the handle of the living entity with that UniqueID
// (Refii.uid's "0x…" text), or nil.
REFII_LUA_NATIVE(NativeByUid) {
  char uid[32];
  lua.String(1, uid, sizeof(uid));
  lua.PushHandle(refii_household_resolve_uid(&lua.ctx, lua.base, std::strtoull(uid, nullptr, 16)));
  RESULTS(1);
}

// Core co-op only: Refii._followers_two(i) — player two's i-th follower as the host states it
// (household_persist.cpp publishes them; online the joiner's own copy has none): its handle and
// flags (1 engaged to player two, 2 following for sex, 4 married to player two), or nil.
REFII_LUA_NATIVE(NativeFollowersTwo) {
  uint32_t entity = 0u;
  uint32_t flags = 0u;
  if (refii_household_follower_two(&lua.ctx, lua.base, lua.Int(1), &entity, &flags) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.PushHandle(entity);
  lua.PushNumber(static_cast<double>(flags));
  RESULTS(2);
}

// Core co-op only: Refii._household_context(on) — the core Lua's silent bed thread for player two
// runs (on 1, off 0, nested): player two's stats it counts go to the joiner (mod_events.cpp).
REFII_LUA_NATIVE(NativeHouseholdContext) {
  refii_household_context(lua.Boolean(1) ? 1 : 0);
  RESULTS(0);
}

// Core co-op only: Refii._forward_speech(e, seconds) — the lines `e` speaks on the host in the next
// seconds go to the online joiner too (the bed scene run for its hero two; mod_events.cpp).
REFII_LUA_NATIVE(NativeForwardSpeech) {
  refii_household_forward_speech(lua.base, lua.Entity(1), static_cast<float>(lua.Number(2)));
  RESULTS(0);
}

// Core co-op only: Refii._children_two() — how many children hero two's family records hold.
REFII_LUA_NATIVE(NativeChildrenTwo) {
  lua.PushNumber(refii_household_children_two(lua.base));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLocalHero) {
  lua.PushHandle(refii_mod_local_hero(lua.base));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeMenuPlayer) {
  lua.PushNumber(refii_mod_menu_player(lua.base));
  RESULTS(1);
}

// PLAYER STATE (core/refii_mod.h refii_mod_player_*): per-player values, the same on both co-op
// machines, the saved ones kept with the hero's own save.
REFII_LUA_NATIVE(NativePlayerValue) {
  char key[128];
  lua.String(2, key, sizeof(key));
  float value = 0.0f;
  if (key[0] != 0 && refii_mod_player_get(lua.Int(1), key, &value) != 0) {
    lua.PushNumber(value);
  } else if (lua.Type(3) == kTypeNumber) {
    lua.PushNumber(lua.Number(3));
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePlayerSet) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_set(lua.Int(1), key, static_cast<float>(lua.Number(3)));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerAdd) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_add(lua.Int(1), key, static_cast<float>(lua.Number(3)), lua.Boolean(4) ? 1 : 0);
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerSave) {
  char key[128];
  lua.String(2, key, sizeof(key));
  if (key[0] != 0) {
    refii_mod_player_save(lua.Int(1), key, static_cast<float>(lua.Number(3)));
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativePlayerHere) {
  lua.PushBoolean(refii_mod_player_here(lua.Int(1)) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEventReplica) {
  lua.PushBoolean(refii_mod_event_replica() != 0);
  RESULTS(1);
}

// Core co-op only: a script asks the partner (kind, asker 1|2, detail) and polls the answer.
extern "C" int32_t RefiiCoopKeyTaken(uint32_t entity, int32_t player);

// Core co-op only: whether player p has taken the per-player collectable e already (1) or not (0).
REFII_LUA_NATIVE(NativeKeyTaken) {
  lua.PushNumber(RefiiCoopKeyTaken(lua.Entity(1), lua.Int(2)));
  RESULTS(1);
}

extern "C" int32_t refii_mod_raise_partner_consent(PPCContext* ctx, uint8_t* base, uint32_t ticket, int32_t kind,
                                                   int32_t asker, int32_t detail);

/// A new question: the PartnerConsent event may answer it for the partner, in which case it is
/// answered before anyone is asked.
void ConsentAsked(PPCContext& ctx, uint8_t* base, uint32_t ticket, int32_t kind, int32_t asker, int32_t detail) {
  const int32_t answer = refii_mod_raise_partner_consent(&ctx, base, ticket, kind, asker, detail);
  if (answer >= 0) {
    refii_bridge_consent_give(ticket, answer);
  }
}

REFII_LUA_NATIVE(NativeConsentAsk) {
  const int32_t kind = lua.Int(1);
  const int32_t asker = lua.Int(2);
  const int32_t detail = lua.Int(3);
  const uint32_t ticket = refii_bridge_consent_ask(kind, asker, detail);
  ConsentAsked(lua.ctx, lua.base, ticket, kind, asker, detail);
  lua.PushNumber(static_cast<double>(ticket));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeConsentAnswer) {
  lua.PushNumber(refii_bridge_consent_answer(static_cast<uint32_t>(lua.Int(1))));
  RESULTS(1);
}

// Refii.ask_partner(asker, question): a mod's own question for the other player (a box on their
// screen, or the peer's online); a ticket for Refii.partner_answer. Unanswered in 30 s is a no.
REFII_LUA_NATIVE(NativeAskPartner) {
  char text[256];
  lua.String(2, text, sizeof(text));
  const int32_t asker = lua.Int(1);
  if (text[0] == 0 || (asker != 1 && asker != 2)) {
    lua.PushNil();
  } else {
    const uint32_t ticket = refii_bridge_consent_ask_text(asker, text);
    ConsentAsked(lua.ctx, lua.base, ticket, 0, asker, 0);
    lua.PushNumber(static_cast<double>(ticket));
  }
  RESULTS(1);
}

// Refii.partner_answer(ticket): nil while pending, then true or false.
REFII_LUA_NATIVE(NativePartnerAnswer) {
  const int32_t answer = refii_bridge_consent_answer(static_cast<uint32_t>(lua.Int(1)));
  if (answer < 0) {
    lua.PushNil();
  } else {
    lua.PushBoolean(answer == 1);
  }
  RESULTS(1);
}

// Refii.key_taken(e, p): whether player p has taken the per-player collectable e (a silver key).
REFII_LUA_NATIVE(NativeKeyTakenPublic) {
  lua.PushBoolean(RefiiCoopKeyTaken(lua.Entity(1), lua.Int(2)) != 0);
  RESULTS(1);
}

// Refii.opinion(villager, p) -> love, fear, attractiveness, morality, respect: how the villager
// feels about player p's hero; nil when it holds no opinions.
REFII_LUA_NATIVE(NativeOpinion) {
  float axes[5] = {};
  if (refii_mod_opinion(lua.base, lua.Entity(1), lua.Int(2), axes) == 0) {
    lua.PushNil();
    RESULTS(1);
  } else {
    for (const float axis : axes) {
      lua.PushNumber(axis);
    }
    RESULTS(5);
  }
}

extern "C" int32_t refii_mod_opinion_enter(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" void refii_mod_opinion_leave(void);

// Core co-op only: Refii._opinion_enter(villager [, hero]) runs what follows as the villager's
// opinion of hero two when it concerns them (the named hero, else the nearer); 1 when entered,
// each 1 matched by Refii._opinion_leave().
REFII_LUA_NATIVE(NativeOpinionEnter) {
  const uint32_t hero = lua.Type(2) == kTypeNumber ? lua.Entity(2) : 0u;
  lua.PushNumber(refii_mod_opinion_enter(&lua.ctx, lua.base, lua.Entity(1), hero));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeOpinionLeave) {
  refii_mod_opinion_leave();
  RESULTS(0);
}

// Core co-op only: Refii._route_post(line) / Refii._route_take() -> line or nil — the route, the
// ordered and acknowledged co-op channel the core Lua's calls for the peer's player and their
// answers travel on (core/event_relay.h ps.r; the line is the Lua's own text).
REFII_LUA_NATIVE(NativeRoutePost) {
  char text[768];
  lua.String(1, text, sizeof(text));
  if (text[0] != 0) {
    refii_bridge_route_post(text);
  }
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeRouteTake) {
  char text[768];
  if (refii_bridge_route_take(text, sizeof(text)) != 0) {
    lua.PushLongString(text);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Core co-op only: Refii._ui_seat(p) before a guest call that builds a box for player p (1 or 2),
// Refii._ui_seat(0) after it: every box that call queued is stated as p's (display_box_hooks.cpp),
// so on split screen it opens in p's half on p's pad. The call between must not yield.
REFII_LUA_NATIVE(NativeUiSeat) {
  const int32_t player = lua.Int(1);
  if (player == 1 || player == 2) {
    refii::vfs::BoxSeatEnter(lua.base, player);
  } else {
    refii::vfs::BoxSeatLeave(lua.base);
  }
  RESULTS(0);
}

// Refii.forgive_crimes(villager, p) -> n: the crimes of player p's hero the villager knows of are
// forgiven (the other hero's stand); how many. Refii.discovered_crime(villager, p): whether the
// villager has discovered a crime of player p's. Refii.perceived_renown(villager, p): the renown the
// villager credits player p's hero with, or nil (refii_mod_forgive_crimes / _discovered_crime /
// _perceived_renown: the shipped Villager natives name no hero).
REFII_LUA_NATIVE(NativeForgiveCrimes) {
  lua.PushNumber(refii_mod_forgive_crimes(&lua.ctx, lua.base, lua.Entity(1), refii_mod_player_hero(lua.base, lua.Int(2))));
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeDiscoveredCrime) {
  lua.PushBoolean(
      refii_mod_discovered_crime(&lua.ctx, lua.base, lua.Entity(1), refii_mod_player_hero(lua.base, lua.Int(2))) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePerceivedRenown) {
  int32_t renown = 0;
  if (refii_mod_perceived_renown(&lua.ctx, lua.base, lua.Entity(1), refii_mod_player_hero(lua.base, lua.Int(2)),
                                 &renown) != 0) {
    lua.PushNumber(renown);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.uid(e): the entity's own 64-bit UniqueID (+0x14 high, +0x18 low) as "0x…": the same on
// both co-op machines and across saves, unlike a handle; nil for no entity.
REFII_LUA_NATIVE(NativeUid) {
  const uint32_t entity = lua.Entity(1);
  if (entity == 0u) {
    lua.PushNil();
  } else {
    char text[24];
    std::snprintf(text, sizeof(text), "0x%08x%08x", refii::mods::LoadU32(lua.base, entity + 0x14u),
                  refii::mods::LoadU32(lua.base, entity + 0x18u));
    lua.PushString(text);
  }
  RESULTS(1);
}

// Core co-op only: Refii._chest_counted(chest) — after Chest.SnapToClosed re-closes a chest for the
// partner, its "has been opened" byte (component 40 +0x0F, which SnapToClosed clears) is put back,
// so the world map's chest count (`sub_825949E0`, gated on it) counts the chest once.
REFII_LUA_NATIVE(NativeChestCounted) {
  const uint32_t chest = refii_mod_component(lua.base, lua.Entity(1), 40u);
  if (chest != 0u) {
    refii::mods::StoreU8(lua.base, chest + 0x0Fu, 1u);
  }
  RESULTS(0);
}

// Core co-op only: Refii._chest_money(chest) — puts a chest's gold back as its definition states
// it: the money component (81) re-reads "Money" from its own definition record, the read it made
// when the chest was created (`sub_8242E2A8(comp, entry)` → `sub_823AD468(entry, "Money", comp+12)`;
// the entry is the component's record, `sub_821DA3B0(&h, comp)` then `sub_821B24F8(&h)`).
REFII_LUA_NATIVE(NativeChestMoney) {
  const uint32_t money = refii_mod_component(lua.base, lua.Entity(1), 81u);
  if (money != 0u) {
    PPCContext work = refii::mods::Frame(lua.ctx);
    const uint32_t handle = (work.r1.u32 + 0x80u) & ~0xFu;
    refii::mods::StoreU32(lua.base, handle, 0u);
    refii::mods::StoreU32(lua.base, handle + 4u, 0u);
    work.r3.u64 = handle;
    work.r4.u64 = money;
    if (refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x821DA3B0u))) {
      work = refii::mods::Frame(lua.ctx);
      work.r3.u64 = handle;
      if (refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x821B24F8u)) && work.r3.u32 >= 0x10000u) {
        const uint32_t entry = work.r3.u32;
        work = refii::mods::Frame(lua.ctx);
        work.r3.u64 = money;
        work.r4.u64 = entry;
        refii::mods::CallGuest(work, lua.base, refii::guest::Address(0x8242E2A8u));
      }
    }
  }
  RESULTS(0);
}

// Refii.coop_mode(): 0 alone, 1 local co-op, 2 online hosting, 3 online joined.
REFII_LUA_NATIVE(NativeCoopMode) {
  lua.PushNumber(refii_mod_coop_mode(lua.base));
  RESULTS(1);
}

// Refii.world_here(): whether this machine runs the shared world (alone, local co-op, online host).
REFII_LUA_NATIVE(NativeWorldHere) {
  lua.PushBoolean(refii_mod_world_here(lua.base) != 0);
  RESULTS(1);
}

// Refii.focus(p): the living entity player p's hero is focused on, or nil.
REFII_LUA_NATIVE(NativeFocus) {
  lua.PushHandle(refii_mod_focus(lua.base, lua.Int(1)));
  RESULTS(1);
}

// Refii.wheel_switches_target(e): e's target lock switches by the mouse wheel this tick (call it
// each tick the lock holds; refii_mod_wheel_switches_target).
REFII_LUA_NATIVE(NativeWheelSwitchesTarget) {
  refii_mod_wheel_switches_target(lua.base, lua.Entity(1));
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeError) {
  char text[512];
  lua.String(1, text, sizeof(text));
  if (text[0] != 0) {
    refii_bridge_mod_error_post(text, std::strlen(text));
    REXLOG_WARN("[mod lua] {}", text);
  }
  RESULTS(0);
}

// Refii.entity(handle): the game's own Entity userdata for a handle — what the shipped scripts
// and the game's natives take (Entity:GetName(), CreatureGenerator...) — pushed by the game's push
// (an interned, ref-counted script reference holding a weak pointer and the entity's id), or nil.
REFII_LUA_NATIVE(NativeEntity) {
  const uint32_t entity = lua.Entity(1);
  if (entity == 0 || refii_mod_entity_alive(lua.base, entity) == 0) {
    lua.PushNil();
  } else {
    PPCContext w = lua.Work();
    w.r4.u32 = entity;
    rex_lhLua_PushEntity(w, lua.base);
  }
  RESULTS(1);
}

/// The entity at argument `index`: a handle number, or a game Entity userdata (any the shipped
/// scripts or natives gave) resolved through its script reference; 0 for a dead entity or anything
/// else.
uint32_t EntityArgument(Lua& lua, int32_t index) {
  if (lua.Type(index) == kTypeNumber) {
    return lua.Entity(index);
  }
  PPCContext check = lua.Work();
  check.r4.s64 = index;
  check.r5.u32 = refii::guest::Address(kEntityTypeName);
  rex_lhLua_IsFableType(check, lua.base);
  if (check.r3.u32 == 0) {
    return 0u;
  }
  PPCContext data = lua.Work();
  data.r4.s64 = index;
  rex_lua_touserdata(data, lua.base);
  const uint32_t body = data.r3.u32;
  const uint32_t ref = body >= 0x10000u && body < 0xC0000000u ? refii::mods::LoadU32(lua.base, body) : 0u;
  if (ref < 0x10000u || ref >= 0xC0000000u) {
    return 0u;
  }
  PPCContext resolve = lua.ctx;
  resolve.r3.u32 = ref;
  rex_lhScriptEntityRef_Resolve(resolve, lua.base);
  return resolve.r3.u32;
}

// Refii.handle(x): the handle for a game Entity userdata (any the shipped scripts or natives gave),
// a number handed back as it is, or nil for a dead entity or anything else.
REFII_LUA_NATIVE(NativeHandle) {
  if (lua.Type(1) == kTypeNumber) {
    lua.PushNumber(lua.Number(1));
    RESULTS(1);
    return;
  }
  lua.PushHandle(EntityArgument(lua, 1));
  RESULTS(1);
}

// Refii.player_for(e): 1 or 2, the player entity `e` deals with (refii_mod_player_for: a hero
// itself, a player's dog its master, anything else the nearer hero); nil with no hero. `e` is a
// handle or a game Entity, so the core co-op Lua and mods ask the one rule.
REFII_LUA_NATIVE(NativePlayerFor) {
  const int32_t player = refii_mod_player_for(&lua.ctx, lua.base, EntityArgument(lua, 1));
  if (player == 1 || player == 2) {
    lua.PushNumber(player);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeRenown) {
  int32_t renown = 0;
  if (refii_mod_renown(&lua.ctx, lua.base, lua.Entity(1), &renown) != 0) {
    lua.PushNumber(renown);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeMorality) {
  float value = 0.0f;
  if (refii_mod_morality(&lua.ctx, lua.base, lua.Entity(1), &value) != 0) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePurity) {
  float value = 0.0f;
  if (refii_mod_purity(&lua.ctx, lua.base, lua.Entity(1), &value) != 0) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativePhysique) {
  float fat = 0.0f, height = 0.0f;
  if (refii_mod_physique(lua.base, lua.Entity(1), &fat, &height) != 0) {
    lua.PushNumber(fat);
    lua.PushNumber(height);
    RESULTS(2);
  } else {
    lua.PushNil();
    RESULTS(1);
  }
}

REFII_LUA_NATIVE(NativeAge) {
  float age = 0.0f;
  if (refii_mod_age(lua.base, lua.Entity(1), &age) != 0) {
    lua.PushNumber(age);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.inventory(e): { {definition = "0x…", count = n}, ... } in the game's order, or nil.
REFII_LUA_NATIVE(NativeInventory) {
  const uint32_t entity = lua.Entity(1);
  if (refii_mod_component(lua.base, entity, 13) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  lua.NewTable();
  uint32_t definition = 0;
  int32_t count = 0;
  for (int32_t i = 0; i < 4096 && refii_mod_inventory_stack(lua.base, entity, i, &definition, &count) != 0; ++i) {
    lua.NewTable();
    lua.PushHash(definition);
    lua.SetField(-2, "definition");
    lua.PushNumber(count);
    lua.SetField(-2, "count");
    lua.RawSetI(-2, i + 1);
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeEquipped) {
  uint32_t melee = 0, ranged = 0;
  if (refii_mod_equipped(lua.base, lua.Entity(1), &melee, &ranged) == 0) {
    lua.PushNil();
    RESULTS(1);
    return;
  }
  if (melee != 0) {
    lua.PushHash(melee);
  } else {
    lua.PushNil();
  }
  if (ranged != 0) {
    lua.PushHash(ranged);
  } else {
    lua.PushNil();
  }
  RESULTS(2);
}

// Refii.near(x, y, z, radius [, creatures_only]): handles, nearest first (at most 2048).
REFII_LUA_NATIVE(NativeNear) {
  const float xyz[3] = {static_cast<float>(lua.Number(1)), static_cast<float>(lua.Number(2)),
                        static_cast<float>(lua.Number(3))};
  const bool creatures = lua.Type(5) == 1 ? lua.Boolean(5) : lua.Int(5) != 0;
  static uint32_t found[2048];
  const int32_t n = refii_mod_entities_near(&lua.ctx, lua.base, xyz, static_cast<float>(lua.Number(4)),
                                            creatures ? 1 : 0, found, 2048);
  lua.NewTable();
  for (int32_t i = 0; i < n; ++i) {
    lua.PushNumber(refii_mod_handle(found[i]));
    lua.RawSetI(-2, i + 1);
  }
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeLineOfSight) {
  const float from[3] = {static_cast<float>(lua.Number(1)), static_cast<float>(lua.Number(2)),
                         static_cast<float>(lua.Number(3))};
  const float to[3] = {static_cast<float>(lua.Number(4)), static_cast<float>(lua.Number(5)),
                       static_cast<float>(lua.Number(6))};
  lua.PushBoolean(refii_mod_line_of_sight(&lua.ctx, lua.base, from, to) != 0);
  RESULTS(1);
}

REFII_LUA_NATIVE(NativeSpawn) {
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  lua.PushHandle(refii_mod_spawn(&lua.ctx, lua.base, lua.Hash(1), xyz));
  RESULTS(1);
}

// Refii._play_fx(name, x, y, z, e, duration, scale, r, g, b, intensity, loop, ox, oy, oz, at): the
// request Godot draws (refii_mod_play_fx); e is a handle, 0 for a world position; at a model
// helper name or "" (refii_mod_fx_options.at). The id, or nil.
REFII_LUA_NATIVE(NativePlayFx) {
  char name[128];
  lua.String(1, name, sizeof(name));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(5), 0.0, 4294967295.0));
  refii_mod_fx_options options = refii_mod_fx_default_options();
  options.duration = static_cast<float>(lua.Number(6));
  options.scale = static_cast<float>(lua.Number(7));
  options.tint[0] = static_cast<float>(lua.Number(8));
  options.tint[1] = static_cast<float>(lua.Number(9));
  options.tint[2] = static_cast<float>(lua.Number(10));
  options.intensity = static_cast<float>(lua.Number(11));
  options.loop = lua.Int(12) != 0 ? 1 : 0;
  options.offset[0] = static_cast<float>(lua.Number(13));
  options.offset[1] = static_cast<float>(lua.Number(14));
  options.offset[2] = static_cast<float>(lua.Number(15));
  char at[96];
  lua.String(16, at, sizeof(at));
  options.at = at[0] != 0 ? at : nullptr;
  const uint32_t id = refii::vfs::PlayModFxOnHandle(name, handle != 0 ? nullptr : xyz, handle, &options);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._mark(e, x, y, z, icon, text, p): refii_mod_mark — a HUD world marker over handle e (0: at
// x, y, z) for player p (0 or nil: every player played here); the preamble's Refii.mark(target,
// icon, text [, p]) takes a handle or {x, y, z}. The id Refii.unmark takes, or nil.
REFII_LUA_NATIVE(NativeMark) {
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  char icon[160];
  char text[160];
  lua.String(5, icon, sizeof(icon));
  lua.String(6, text, sizeof(text));
  const uint32_t entity = handle != 0u ? refii_mod_handle_address(handle) : 0u;
  const uint32_t id = refii_mod_mark(lua.Int(7), entity, handle != 0u ? nullptr : xyz, icon, text);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.unmark(id): removes a marker by any surface's id.
REFII_LUA_NATIVE(NativeUnmark) {
  refii_mod_unmark(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  RESULTS(0);
}

// Refii.fade(p, dir [, seconds]): refii_mod_fade — player p's screen (0 or nil: every player
// played here) "to_black" / "out" or "from_black" / "in" over seconds (0 or nil: at once). True
// when posted.
REFII_LUA_NATIVE(NativeFade) {
  char dir[32];
  lua.String(2, dir, sizeof(dir));
  const bool to_black = std::strcmp(dir, "to_black") == 0 || std::strcmp(dir, "out") == 0 || std::strcmp(dir, "black") == 0;
  lua.PushBoolean(refii_mod_fade(lua.Int(1), to_black ? 1 : 0, static_cast<float>(lua.Number(3))) != 0);
  RESULTS(1);
}

// Refii.meter(id, label, value, max [, p [, style]]): refii_mod_meter — a HUD meter for player p
// (0 or nil: every player played here) in style "bar" (nil), "boss" or "timer" (the HUD's own
// primitives), or "generic", "crucible" or "price" (the game's own meter art, a lit copy). True when posted.
REFII_LUA_NATIVE(NativeMeter) {
  char id[96];
  char label[128];
  char style[32];
  lua.String(1, id, sizeof(id));
  lua.String(2, label, sizeof(label));
  lua.String(6, style, sizeof(style));
  lua.PushBoolean(refii_mod_meter(lua.Int(5), id, label, static_cast<float>(lua.Number(3)),
                                  static_cast<float>(lua.Number(4)), style) != 0);
  RESULTS(1);
}

// Refii.meter_remove(id): refii_mod_meter_remove. True when posted.
REFII_LUA_NATIVE(NativeMeterRemove) {
  char id[96];
  lua.String(1, id, sizeof(id));
  lua.PushBoolean(refii_mod_meter_remove(id) != 0);
  RESULTS(1);
}

// Refii.stop_fx(id): any surface's id.
REFII_LUA_NATIVE(NativeStopFx) {
  refii_mod_stop_fx(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  RESULTS(0);
}

// Refii._light(e, x, y, z, r, g, b, energy, range, ox, oy, oz, at, flicker, dx, dy, dz, cone,
// attenuation): the light Godot places (refii_mod_light); e is a handle, 0 for the world point
// x, y, z; at a model helper name or ""; a cone above 0 makes it a spot along dx, dy, dz
// (refii_mod_light_options). The preamble's Refii.light(target, color, energy, range [, o]) builds
// it. The id, or nil.
REFII_LUA_NATIVE(NativeLight) {
  const uint32_t handle = static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0));
  const float xyz[3] = {static_cast<float>(lua.Number(2)), static_cast<float>(lua.Number(3)),
                        static_cast<float>(lua.Number(4))};
  const float rgb[3] = {static_cast<float>(lua.Number(5)), static_cast<float>(lua.Number(6)),
                        static_cast<float>(lua.Number(7))};
  refii_mod_light_options options{};
  options.offset[0] = static_cast<float>(lua.Number(10));
  options.offset[1] = static_cast<float>(lua.Number(11));
  options.offset[2] = static_cast<float>(lua.Number(12));
  char at[96];
  lua.String(13, at, sizeof(at));
  options.at = at[0] != 0 ? at : nullptr;
  options.flicker = static_cast<float>(lua.Number(14));
  options.direction[0] = static_cast<float>(lua.Number(15));
  options.direction[1] = static_cast<float>(lua.Number(16));
  options.direction[2] = static_cast<float>(lua.Number(17));
  options.cone = static_cast<float>(lua.Number(18));
  options.attenuation = static_cast<float>(lua.Number(19));
  const uint32_t id = refii::vfs::PlaceModLightOnHandle(handle, xyz, rgb, static_cast<float>(lua.Number(8)),
                                                        static_cast<float>(lua.Number(9)), &options);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.light_remove(id): removes a mod light by any surface's id.
REFII_LUA_NATIVE(NativeLightRemove) {
  refii_mod_light_remove(static_cast<uint32_t>(Lua::Clamp(lua.Number(1), 0.0, 4294967295.0)));
  RESULTS(0);
}

// Refii.send_to_partner(mod, name, payload): refii_mod_send_to_partner — a mod's own message to
// the other machine of an online session, raised there as partner_message. True when posted.
REFII_LUA_NATIVE(NativeSendToPartner) {
  char mod[64];
  char name[64];
  char payload[REFII_MOD_PARTNER_PAYLOAD_MAX + 2];
  lua.String(1, mod, sizeof(mod));
  lua.String(2, name, sizeof(name));
  lua.String(3, payload, sizeof(payload));
  lua.PushBoolean(refii_mod_send_to_partner(mod, name, payload) != 0);
  RESULTS(1);
}

// Refii.open_menu(name [, p]): a mod's menu for player p (0 or nil: whoever owns the menus
// here), over the list that has the input or on its own (refii_mod_open_menu). True when posted.
REFII_LUA_NATIVE(NativeOpenMenu) {
  char name[96];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(refii_mod_open_menu(lua.Int(2), name) != 0);
  RESULTS(1);
}

// Refii._menu_rows(menu, rows, p): refii_mod_set_menu_rows with the preamble's text for the rows
// table (Refii.set_menu_rows). True when posted.
REFII_LUA_NATIVE(NativeMenuRows) {
  char menu[96];
  char rows[1800];
  lua.String(1, menu, sizeof(menu));
  lua.String(2, rows, sizeof(rows));
  lua.PushBoolean(refii_mod_set_menu_rows(lua.Int(3), menu, rows) != 0);
  RESULTS(1);
}

// Refii.open_screen(id [, p]): a host-owned screen for player p (refii_mod_open_screen). True when posted.
REFII_LUA_NATIVE(NativeOpenScreen) {
  const double id = lua.Number(1);
  lua.PushBoolean(id >= 1.0 && id <= 4294967295.0 &&
                  refii_mod_open_screen(lua.Int(2), static_cast<uint32_t>(id)) != 0);
  RESULTS(1);
}

// Refii._message_box(title, text, accept, cancel, question, p): refii_mod_message_box; the
// preamble's Refii.message_box fills the defaults. The id the mod_message_box event carries, or nil.
REFII_LUA_NATIVE(NativeMessageBox) {
  char title[128];
  char text[480];
  char accept[64];
  char cancel[64];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  lua.String(3, accept, sizeof(accept));
  lua.String(4, cancel, sizeof(cancel));
  const uint32_t id = refii_mod_message_box(lua.Int(6), title, text, accept, lua.Int(5) != 0 ? cancel : nullptr);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii.notice(title, text [, seconds [, p]]): refii_mod_notice — the notice channel, for player p
// (0 or nil: whoever owns the menus here). The id the mod_notice event carries, or nil.
REFII_LUA_NATIVE(NativeNotice) {
  char title[128];
  char text[480];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  const uint32_t id = refii_mod_notice(lua.Int(4), title, text, static_cast<float>(lua.Number(3)));
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._menu_box(title, text, p, cancelable, n, o1 .. o12): refii_mod_menu_box; the preamble's
// Refii.menu_box(title, text, options [, p [, cancelable]]) unpacks the options table (this Lua's
// C API here has no array reads). The id the mod_menu_box event carries, or nil.
REFII_LUA_NATIVE(NativeMenuBox) {
  constexpr int32_t kMax = 12;
  char title[128];
  char text[480];
  char options[kMax][96];
  const char* list[kMax];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  const int32_t count = static_cast<int32_t>(Lua::Clamp(lua.Number(5), 0.0, kMax));
  for (int32_t i = 0; i < count; ++i) {
    lua.String(6 + i, options[i], sizeof(options[i]));
    list[i] = options[i];
  }
  const uint32_t id = refii_mod_menu_box(lua.Int(3), title, text, list, count, lua.Int(4) != 0 ? 1 : 0);
  if (id != 0) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  RESULTS(1);
}

// Refii._cancelled(): the preamble's _fire states that a handler returned true.
REFII_LUA_NATIVE(NativeCancelled) {
  refii::vfs::NoteLuaCancelled();
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeWrite) {
  char field[32];
  lua.String(2, field, sizeof(field));
  lua.PushBoolean(refii::vfs::WriteModEventField(lua.Int(1), field, lua.Number(3)));
  RESULTS(1);
}

struct Native {
  const char* name;
  PPCFunc* function;
  uint32_t thunk;
};

Native g_natives[] = {
    {"publish", &NativePublish, 0},
    {"value", &NativeValue, 0},
    {"log", &NativeLog, 0},
    {"hash", &NativeHash, 0},
    {"health", &NativeHealth, 0},
    {"set_health", &NativeSetHealth, 0},
    {"modify_health", &NativeModifyHealth, 0},
    {"position", &NativePosition, 0},
    {"set_position", &NativeSetPosition, 0},
    {"entity_tint", &NativeEntityTint, 0},
    {"entity_untint", &NativeEntityUntint, 0},
    {"entity_alpha", &NativeEntityAlpha, 0},
    {"entity_scale", &NativeEntityScale, 0},
    {"entity_saturation_mask", &NativeEntitySaturationMask, 0},
    {"entity_drawable", &NativeEntityDrawable, 0},
    {"lock_target", &NativeLockTarget, 0},
    {"release_lock", &NativeReleaseLock, 0},
    {"switch_target", &NativeSwitchTarget, 0},
    {"locked_target", &NativeLockedTarget, 0},
    {"set_speed_multiplier", &NativeSetSpeedMultiplier, 0},
    {"clear_speed_multiplier", &NativeClearSpeedMultiplier, 0},
    {"speed_multiplier", &NativeSpeedMultiplier, 0},
    {"current_action", &NativeCurrentAction, 0},
    {"action_elapsed", &NativeActionElapsed, 0},
    {"action_interruptible", &NativeActionInterruptible, 0},
    {"allow_action_interrupt", &NativeAllowActionInterrupt, 0},
    {"action_type", &NativeActionType, 0},
    {"action_name", &NativeActionName, 0},
    {"living", &NativeLiving, 0},
    {"spell_in_slot", &NativeSpellInSlot, 0},
    {"spell_parts", &NativeSpellParts, 0},
    {"apply_status", &NativeApplyStatus, 0},
    {"clear_status", &NativeClearStatus, 0},
    {"has_status", &NativeHasStatus, 0},
    {"knockback", &NativeKnockback, 0},
    {"stagger", &NativeStagger, 0},
    {"play_sound", &NativePlaySound, 0},
    {"play_sound_at", &NativePlaySoundAt, 0},
    {"stop_sound", &NativeStopSound, 0},
    {"play_music", &NativePlayMusic, 0},
    {"stop_music", &NativeStopMusic, 0},
    {"play_animation", &NativePlayAnimation, 0},
    {"stop_animation", &NativeStopAnimation, 0},
    {"modify_opinion", &NativeModifyOpinion, 0},
    {"add_deed", &NativeAddDeed, 0},
    {"npc_follow", &NativeNpcFollow, 0},
    {"npc_go_to", &NativeNpcGoTo, 0},
    {"npc_flee", &NativeNpcFlee, 0},
    {"npc_attack", &NativeNpcAttack, 0},
    {"npc_set_hostile", &NativeNpcSetHostile, 0},
    {"npc_stop", &NativeNpcStop, 0},
    {"set_time_of_day", &NativeSetTimeOfDay, 0},
    {"time_of_day", &NativeTimeOfDay, 0},
    {"set_weather", &NativeSetWeather, 0},
    {"gold", &NativeGold, 0},
    {"give_gold", &NativeGiveGold, 0},
    {"item_count", &NativeItemCount, 0},
    {"add_item", &NativeAddItem, 0},
    {"experience", &NativeExperience, 0},
    {"give_experience", &NativeGiveExperience, 0},
    {"spend_experience", &NativeSpendExperience, 0},
    {"ability_level", &NativeAbilityLevel, 0},
    {"_cast_spell", &NativeCastSpell, 0},
    {"spell_serial", &NativeSpellSerial, 0},
    {"relation", &NativeRelation, 0},
    {"alive", &NativeAlive, 0},
    {"definition", &NativeDefinition, 0},
    {"name", &NativeName, 0},
    {"player_of", &NativePlayerOf, 0},
    {"level_name", &NativeLevelName, 0},
    {"player_hero", &NativePlayerHero, 0},
    {"local_hero", &NativeLocalHero, 0},
    {"menu_player", &NativeMenuPlayer, 0},
    {"player_value", &NativePlayerValue, 0},
    {"player_set", &NativePlayerSet, 0},
    {"player_add", &NativePlayerAdd, 0},
    {"player_save", &NativePlayerSave, 0},
    {"player_here", &NativePlayerHere, 0},
    {"event_replica", &NativeEventReplica, 0},
    {"split_screen", &NativeSplitScreen, 0},
    {"player_dog", &NativePlayerDog, 0},
    {"player_for", &NativePlayerFor, 0},
    {"_set_partner_dog", &NativeSetPartnerDog, 0},
    {"_job_worker", &NativeJobWorker, 0},
    {"_estate_buyer", &NativeEstateBuyer, 0},
    {"_household", &NativeHousehold, 0},
    {"_by_uid", &NativeByUid, 0},
    {"_followers_two", &NativeFollowersTwo, 0},
    {"_household_context", &NativeHouseholdContext, 0},
    {"_children_two", &NativeChildrenTwo, 0},
    {"_forward_speech", &NativeForwardSpeech, 0},
    {"_partner_screen", &NativePartnerScreen, 0},
    {"_consent_ask", &NativeConsentAsk, 0},
    {"_key_taken", &NativeKeyTaken, 0},
    {"_consent_answer", &NativeConsentAnswer, 0},
    {"ask_partner", &NativeAskPartner, 0},
    {"partner_answer", &NativePartnerAnswer, 0},
    {"key_taken", &NativeKeyTakenPublic, 0},
    {"opinion", &NativeOpinion, 0},
    {"focus", &NativeFocus, 0},
    {"wheel_switches_target", &NativeWheelSwitchesTarget, 0},
    {"coop_mode", &NativeCoopMode, 0},
    {"world_here", &NativeWorldHere, 0},
    {"uid", &NativeUid, 0},
    {"_chest_counted", &NativeChestCounted, 0},
    {"_chest_money", &NativeChestMoney, 0},
    {"_opinion_enter", &NativeOpinionEnter, 0},
    {"_opinion_leave", &NativeOpinionLeave, 0},
    {"_route_post", &NativeRoutePost, 0},
    {"_route_take", &NativeRouteTake, 0},
    {"_ui_seat", &NativeUiSeat, 0},
    {"forgive_crimes", &NativeForgiveCrimes, 0},
    {"discovered_crime", &NativeDiscoveredCrime, 0},
    {"perceived_renown", &NativePerceivedRenown, 0},
    {"renown", &NativeRenown, 0},
    {"morality", &NativeMorality, 0},
    {"purity", &NativePurity, 0},
    {"physique", &NativePhysique, 0},
    {"age", &NativeAge, 0},
    {"inventory", &NativeInventory, 0},
    {"equipped", &NativeEquipped, 0},
    {"near", &NativeNear, 0},
    {"line_of_sight", &NativeLineOfSight, 0},
    {"spawn", &NativeSpawn, 0},
    {"entity", &NativeEntity, 0},
    {"handle", &NativeHandle, 0},
    {"_play_fx", &NativePlayFx, 0},
    {"stop_fx", &NativeStopFx, 0},
    {"open_menu", &NativeOpenMenu, 0},
    {"_menu_rows", &NativeMenuRows, 0},
    {"open_screen", &NativeOpenScreen, 0},
    {"_message_box", &NativeMessageBox, 0},
    {"notice", &NativeNotice, 0},
    {"_mark", &NativeMark, 0},
    {"unmark", &NativeUnmark, 0},
    {"fade", &NativeFade, 0},
    {"meter", &NativeMeter, 0},
    {"meter_remove", &NativeMeterRemove, 0},
    {"_light", &NativeLight, 0},
    {"light_remove", &NativeLightRemove, 0},
    {"send_to_partner", &NativeSendToPartner, 0},
    {"_menu_box", &NativeMenuBox, 0},
    {"_write", &NativeWrite, 0},
    {"_error", &NativeError, 0},
    {"_cancelled", &NativeCancelled, 0},
};

bool AllocateThunks() {
  static bool done = false;
  static bool ok = false;
  if (done) {
    return ok;
  }
  done = true;
  ok = true;
  const uint32_t anchor = refii::guest::Address(kModuleAnchor);
  for (Native& native : g_natives) {
    native.thunk = refii_bridge_rex_allocate_thunk(reinterpret_cast<void*>(native.function), anchor);
    if (native.thunk == 0) {
      REXLOG_WARN("Refii mod Lua: no guest address for Refii.{} -- the natives are not registered", native.name);
      ok = false;
    }
  }
  return ok;
}

}  // namespace

namespace refii::vfs {

void RegisterModLuaNatives(PPCContext& ctx, uint8_t* base, uint32_t state) {
  if (state < 0x10000u || state >= 0xC0000000u || !AllocateThunks()) {
    return;
  }
  PPCContext frame = refii::mods::Frame(ctx, 0x400u);
  Lua lua(frame, base, state);
  const int32_t top = lua.Top();
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) != kTypeTable) {
    lua.SetTop(-2);
    lua.NewTable();
    lua.SetField(kGlobalsIndex, "Refii");
    lua.GetField(kGlobalsIndex, "Refii");
  }
  for (const Native& native : g_natives) {
    lua.PushClosure(native.thunk);
    lua.SetField(-2, native.name);
  }
  lua.SetTop(top);
  REXLOG_INFO("Refii mod Lua: {} Refii.* natives registered", sizeof(g_natives) / sizeof(g_natives[0]));
}

}  // namespace refii::vfs
