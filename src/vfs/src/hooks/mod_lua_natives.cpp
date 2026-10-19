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
//   gold(e) · give_gold(e, delta) · item_count(e, def) · add_item(e, def)
//   experience(e, pool) · give_experience(e, pool, amount) · spend_experience(e, pool, amount)
//   ability_level(e, type) · cast_spell(caster, type, targeted, target, level)
//   relation(a, b) · alive(e) · definition(e) · name(e) · player_of(e) · level_name()
//   player_hero(p) · local_hero() · renown(e) · morality(e) · purity(e) · physique(e) -> fat, height
//   age(e) · inventory(e) · equipped(e) -> melee, ranged · near(x, y, z, r [, creatures])
//   line_of_sight(x1, y1, z1, x2, y2, z2) · spawn(def, x, y, z) · entity(handle) · handle(entity)
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
extern "C" void RefiiCoopSetEstateBuyer(int player);

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
}

namespace {

constexpr int32_t kGlobalsIndex = -10002;
constexpr int32_t kTypeString = 4;
constexpr int32_t kTypeTable = 5;
/// A guest function inside the game module, for the thunk pool (RunScriptBuffer, TU1).
constexpr uint32_t kModuleAnchor = 0x82451F18u;
constexpr uint32_t kScratchBytes = 0x100u;
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

REFII_LUA_NATIVE(NativeCastSpell) {
  const bool targeted = lua.Type(3) == 1 ? lua.Boolean(3) : lua.Int(3) != 0;
  lua.PushHandle(refii_mod_cast_spell(&lua.ctx, lua.base, lua.Entity(1), lua.Int(2), targeted ? 1 : 0, lua.Entity(4),
                                      lua.Int(5), nullptr, nullptr));
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

REFII_LUA_NATIVE(NativeEstateBuyer) {
  RefiiCoopSetEstateBuyer(lua.Int(1));
  RESULTS(0);
}

REFII_LUA_NATIVE(NativeLocalHero) {
  lua.PushHandle(refii_mod_local_hero(lua.base));
  RESULTS(1);
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

// Refii.handle(x): the handle for a game Entity userdata (any the shipped scripts or natives gave),
// a number handed back as it is, or nil for a dead entity or anything else.
REFII_LUA_NATIVE(NativeHandle) {
  if (lua.Type(1) == kTypeNumber) {
    lua.PushNumber(lua.Number(1));
    RESULTS(1);
    return;
  }
  PPCContext check = lua.Work();
  check.r4.s64 = 1;
  check.r5.u32 = refii::guest::Address(kEntityTypeName);
  rex_lhLua_IsFableType(check, lua.base);
  uint32_t entity = 0;
  if (check.r3.u32 != 0) {
    PPCContext data = lua.Work();
    data.r4.s64 = 1;
    rex_lua_touserdata(data, lua.base);
    const uint32_t body = data.r3.u32;
    const uint32_t ref = body >= 0x10000u && body < 0xC0000000u ? refii::mods::LoadU32(lua.base, body) : 0u;
    if (ref >= 0x10000u && ref < 0xC0000000u) {
      PPCContext resolve = lua.ctx;
      resolve.r3.u32 = ref;
      rex_lhScriptEntityRef_Resolve(resolve, lua.base);
      entity = resolve.r3.u32;
    }
  }
  lua.PushHandle(entity);
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
    {"gold", &NativeGold, 0},
    {"give_gold", &NativeGiveGold, 0},
    {"item_count", &NativeItemCount, 0},
    {"add_item", &NativeAddItem, 0},
    {"experience", &NativeExperience, 0},
    {"give_experience", &NativeGiveExperience, 0},
    {"spend_experience", &NativeSpendExperience, 0},
    {"ability_level", &NativeAbilityLevel, 0},
    {"cast_spell", &NativeCastSpell, 0},
    {"relation", &NativeRelation, 0},
    {"alive", &NativeAlive, 0},
    {"definition", &NativeDefinition, 0},
    {"name", &NativeName, 0},
    {"player_of", &NativePlayerOf, 0},
    {"level_name", &NativeLevelName, 0},
    {"player_hero", &NativePlayerHero, 0},
    {"local_hero", &NativeLocalHero, 0},
    {"split_screen", &NativeSplitScreen, 0},
    {"player_dog", &NativePlayerDog, 0},
    {"_set_partner_dog", &NativeSetPartnerDog, 0},
    {"_job_worker", &NativeJobWorker, 0},
    {"_estate_buyer", &NativeEstateBuyer, 0},
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
