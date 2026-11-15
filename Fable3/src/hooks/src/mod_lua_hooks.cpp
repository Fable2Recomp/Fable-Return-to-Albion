// refii - Fable 3's mod Lua: the mods' composed chunks run in the GAME VM after the shipped startup
// scripts, the Refii.* natives registered there, and the host's mod lanes (commands, input, the
// tick, the Lua subscription masks) served on that VM's own thread.
//
// Every address is the TU3 IDB's; the disc's is bound by the same name (Fable3/versions/Fable3).
//
// THE VM. The game VM ("Fable 3") is built by sub_822D9808: the old script system and state are
// deleted (lua_close), a new state made (sub_822E54D0), a 576-byte script system constructed on it
// (sub_8231B568: ss+0 = L, ss+477 = 0) and its namespaces registered. The UI's state ("Fable 3
// Anark", sub_823319E0) shares the same Kore runtime code, so nothing here hooks the runtime: the
// seams are the script system's own functions, which only the game VM's script system calls.
//
// THE SEAM. Game_InitDatabases (sub_8234BFC0) calls the startup scripts (sub_8231CAA8) on every
// component init; the body runs the save-load, general, AI, quest and camera scripts only while
// byte ss+477 is clear, then sets it:
//     8231cabc  if ( !*((_BYTE *)a1 + 477) ) { ... sub_822E5610(*a1, ...) x5 ...; *(a1+477) = 1; }
// So the mods' chunks run on that 0 -> 1 transition, once per VM, after every shipped script and
// native: Fable 2's position (Fable2/src/vfs/src/hooks/mod_lua_hooks.cpp). A VM kept across a
// component switch keeps the mods' state, as it keeps the game's.
//
// RUNNING A CHUNK. Not through the game's lhLua_RunScriptBuffer (sub_822E56E0), which copies the
// load error into a string it destroys and leaves a runtime error unread: luaL_loadbuffer
// (sub_822CFD30: L, G+0x288 load settings, buffer, size, name; nonzero with the message on top)
// then lua_pcall (sub_82236DD8: L, 0, 0, 0; nonzero with the message on top), the top restored
// after. The loader compiles SOURCE: sub_822A94F0 undumps only a first byte of 27 and hands
// anything else to the Kore lexer and parser (0x822a95a8), so the host's text runs as composed.
//
// A NATIVE. A host PPCFunc given a guest address (refii_bridge_rex_allocate_thunk), pushed with
// lua_pushcclosure (sub_822384F8: L, fn, nup, flags), bound with lua_setfield. Called with r3 = L,
// answering r3 = the result count (luaD_call's `mr r3,r31; mtctr; bctrl`). Between the push and the
// set, lhLuaNamespace_RegisterFunctionHash(holder, L, name) (0x82361988) keys the closure on top as
// FNV-1a("<namespace>_<name>") in PlutoPermanentsSaveTable / LoadTable, as it does for each of the
// game's natives — the namespace is `*holder`, its name the CCharString at +8, its enable byte +12 —
// so a Refii native a mod keeps in its saved state is written by name, never as a C function.
//
// THE STACK. L+0x1C top, L+0x20 base, L+0x24 stack_last; a TValue is {value, tag} (tags 0 nil,
// 1 boolean, 3 number, 4 string, 5 table). lua_pushnumber / pushboolean do not exist on TU3, so
// those are written in place as the game's own glue does, after the room check lua_setfield makes
// (`top + 16 > stack_last` -> luaD_growstack(L+4, L, 2), 0x822a6278). Numbers are float32.

#include "mod_lua.h"

#include <algorithm>
#include <atomic>
#include <mutex>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <string>
#include <thread>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/host_asset_reader.h"
#include "core/mod_values.h"
#include "core/payload_bridge.h"
#include "core/refii_mod_bus.h"
#include "core/refii_mod_requests.h"
#include "core/rex_payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_lhLuaScriptSystem_LoadStartupScripts);
REX_EXTERN(__imp__rex_lhLuaScriptSystem_LoadScriptSave);
REX_EXTERN(__imp__rex_lhLuaScriptSystem_RunCameraScripts);
REX_EXTERN(__imp__rex_lua_close);
REX_EXTERN(rex_luaL_loadbuffer);
REX_EXTERN(rex_lua_pcall);
REX_EXTERN(rex_lua_settop);
REX_EXTERN(rex_lua_type);
REX_EXTERN(rex_lua_tonumber);
REX_EXTERN(rex_lua_toboolean);
REX_EXTERN(rex_lua_tolstring);
REX_EXTERN(rex_lua_pushlstring);
REX_EXTERN(rex_lua_pushcclosure);
REX_EXTERN(rex_lua_rawgeti);
REX_EXTERN(rex_luaD_growstack);
REX_EXTERN(rex_lua_getfield);
REX_EXTERN(rex_lua_setfield);
REX_EXTERN(rex_lhLuaNamespace_RegisterFunctionHash);
REX_EXTERN(rex_lhCharString_Ctor);
REX_EXTERN(rex_lhNew);
REX_EXTERN(rex_lhDelete);
REX_EXTERN(rex_lhLua_PushEntity);
REX_EXTERN(rex_lhScriptEntityRef_Resolve);
REX_EXTERN(rex_lua_getmetatable);
REX_EXTERN(rex_lua_touserdata);
REX_EXTERN(__imp__rex_lhEntity_Destroy);
REX_EXTERN(__imp__rex_lhEntityManager_Add);

namespace {

constexpr uint32_t kStateTop = 0x1Cu;
constexpr uint32_t kStateBase = 0x20u;
constexpr uint32_t kStateStackLast = 0x24u;
/// G(L) + 0x288: the load settings every luaL_loadbuffer caller passes (`*a1 + 648`, 0x822e5780).
constexpr uint32_t kGlobalLoadSettings = 0x288u;
/// The script system's startup-scripts-ran byte (0x8231cabc).
constexpr uint32_t kScriptSystemRan = 477u;

constexpr int32_t kGlobalsIndex = -10002;
constexpr uint32_t kTagNil = 0u;
constexpr uint32_t kTagBoolean = 1u;
constexpr uint32_t kTagNumber = 3u;
constexpr int32_t kTypeNumber = 3;
constexpr int32_t kTypeString = 4;
constexpr int32_t kTypeTable = 5;
constexpr int32_t kTypeUserdata = 7;
constexpr int32_t kRegistryIndex = -10000;
/// The script system's registry ref to the Entity userdata's metatable (`lwz r4,0x230(r30)`,
/// sub_8293CBA0 reads `*(ss + 560)`; -2 until the first push makes it).
constexpr uint32_t kScriptSystemEntityMeta = 560u;

/// The host's composed chunks (Godot/src/Engine/mods/Engine_ModLua.cs), read through the mount.
constexpr const char* kInitKey = "refii/mod_init.lua";
/// Fable 3's helper calls (Godot/src/Titles/Fable3/Fable3CoreLua.cs), run before any mod's chunk.
constexpr const char* kCoreKey = "refii/core_init.lua";
constexpr const char* kCameraKey = "refii/mod_camera.lua";

/// The event kinds this runtime raises (Fable2/src/vfs/include/core/refii_mod_events.h).
constexpr int32_t kEventTick = 0;
constexpr int32_t kEventHero = 1;
constexpr int32_t kEventHeroGone = 2;
constexpr int32_t kEventLua = 3;
constexpr int32_t kEventSaveLoaded = 4;
constexpr int32_t kEventInputAction = 34;
constexpr int32_t kEventEntitySpawned = 39;
constexpr int32_t kEventEntityRemoved = 40;
/// The kind the host's query replies arrive under (REFII_MOD_HOST_REPLY_KIND).
constexpr int32_t kHostReplyKind = 1000;

/// Refii._masks: groups of 20 bits (a float32 is exact to 24), kinds 0..199 (ModApi.LuaMaskBits /
/// LuaMaskGroups).
constexpr int32_t kLuaMaskBits = 20;
constexpr int32_t kLuaMaskGroups = 10;
constexpr float kLuaDirtyPollSeconds = 1.0f;

/// One HF step of the loop App_TickInput rides (game_tick_pump.cpp: HF rate 30).
constexpr float kStepSeconds = 1.0f / 30.0f;

/// The game (`dword_83597034`). Its world, as Lua_Global_IsPlayer hands it to IsPlayer
/// (0x8219ABC8): `*(*(*(*(game + 0xC) + 0x1C) + 4) + 4)`. The world's hero holders, keyed by the
/// player's PlayerID (the save loader binds ID 1 to +0x7C, ID 2 to +0x84 at 0x8242b0f8 / 0x8242b10c):
/// smart pointers whose +4 is the block holding the hero (getters sub_821FB318 / sub_823BB4C0).
const refii::guest::Addr kGame{0x83597034u};
constexpr uint32_t kWorldHeroOne = 0x7Cu;
constexpr uint32_t kWorldHeroTwo = 0x84u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t LoadU32(const uint8_t* base, uint32_t at) {
  uint32_t value;
  std::memcpy(&value, base + at, sizeof(value));
  return __builtin_bswap32(value);
}

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + at, &be, sizeof(be));
}

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadU32(base, at) : 0u; }

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
PPCContext Frame(const PPCContext& ctx, uint32_t bytes) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// The game's operator new / delete (sub_8221E3B0 / sub_8221E388).
uint32_t GuestNew(const PPCContext& ctx, uint8_t* base, uint32_t size) {
  PPCContext work = Frame(ctx, 0x100u);
  work.r3.u32 = size;
  rex_lhNew(work, base);
  return work.r3.u32;
}

void GuestDelete(const PPCContext& ctx, uint8_t* base, uint32_t block) {
  if (block == 0u) {
    return;
  }
  PPCContext work = Frame(ctx, 0x100u);
  work.r3.u32 = block;
  rex_lhDelete(work, base);
}

// ---------------------------------------------------------------------------------------------
// The game VM this runtime serves.

std::atomic<uint32_t> g_state{0u};
/// The game VM's script system (r3 of the startup scripts): the Entity metatable's ref is its +560.
std::atomic<uint32_t> g_system{0u};
std::atomic<std::thread::id> g_vm_thread{};
/// Bumped for each new VM and each save read into one: the masks are read again.
std::atomic<uint32_t> g_generation{0u};

bool OnVmThread() { return std::this_thread::get_id() == g_vm_thread.load(std::memory_order_relaxed); }

// ---------------------------------------------------------------------------------------------
// The Lua C API over a guest frame.

/// Strings are staged in the frame's scratch, from +0x40 for kScratchBytes; a longer one goes to
/// the guest heap for the call.
constexpr uint32_t kScratchBytes = 0x300u;
constexpr uint32_t kNativeFrame = 0x400u;

struct GuestLua {
  PPCContext& ctx;
  uint8_t* base;
  uint32_t L;
  uint32_t scratch;

  GuestLua(PPCContext& c, uint8_t* b, uint32_t state) : ctx(c), base(b), L(state), scratch(c.r1.u32 + 0x40u) {}

  PPCContext Call() const {
    PPCContext work = ctx;
    work.r3.u32 = L;
    return work;
  }

  int32_t Top() const {
    return static_cast<int32_t>((LoadU32(base, L + kStateTop) - LoadU32(base, L + kStateBase)) >> 3);
  }

  void SetTop(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_settop(w, base);
  }

  /// Room for `slots` more values, as lua_setfield makes it.
  void Room(uint32_t slots) {
    if (LoadU32(base, L + kStateTop) + 8u * slots > LoadU32(base, L + kStateStackLast)) {
      PPCContext w = ctx;
      w.r3.u32 = L + 4u;
      w.r4.u32 = L;
      w.r5.u32 = slots;
      rex_luaD_growstack(w, base);
    }
  }

  int32_t Type(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_type(w, base);
    return static_cast<int32_t>(w.r3.u32);
  }

  double Number(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_tonumber(w, base);
    return w.f1.f64;
  }

  bool Boolean(int32_t index) {
    PPCContext w = Call();
    w.r4.s64 = index;
    rex_lua_toboolean(w, base);
    return w.r3.u32 != 0u;
  }

  /// The string (or number, as Lua converts it) at `index`, up to `limit` bytes; empty otherwise.
  std::string String(int32_t index, size_t limit = 4096u) {
    const int32_t type = Type(index);
    if (type != kTypeString && type != kTypeNumber) {
      return {};
    }
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.u32 = 0u;
    rex_lua_tolstring(w, base);
    const uint32_t text = w.r3.u32;
    if (!Plausible(text)) {
      return {};
    }
    size_t n = 0;
    while (n < limit && text + n < 0xC0000000u && base[text + n] != 0) {
      ++n;
    }
    return std::string(reinterpret_cast<const char*>(base + text), n);
  }

  void PushRaw(uint32_t value, uint32_t tag) {
    Room(1u);
    const uint32_t top = LoadU32(base, L + kStateTop);
    StoreU32(base, top, value);
    StoreU32(base, top + 4u, tag);
    StoreU32(base, L + kStateTop, top + 8u);
  }

  void PushNil() { PushRaw(0u, kTagNil); }
  void PushBoolean(bool value) { PushRaw(value ? 1u : 0u, kTagBoolean); }
  void PushNumber(double value) {
    const float f = std::isfinite(value) ? static_cast<float>(value) : 0.0f;
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof(bits));
    PushRaw(bits, kTagNumber);
  }

  void PushString(std::string_view text) {
    Room(1u);
    uint32_t staged = scratch;
    uint32_t heap = 0u;
    if (text.size() >= kScratchBytes) {
      heap = GuestNew(ctx, base, static_cast<uint32_t>(text.size()) + 1u);
      if (!Plausible(heap)) {
        PushNil();
        return;
      }
      staged = heap;
    }
    std::memcpy(base + staged, text.data(), text.size());
    base[staged + text.size()] = 0;
    PPCContext w = Call();
    w.r4.u32 = staged;
    w.r5.u32 = static_cast<uint32_t>(text.size());
    rex_lua_pushlstring(w, base);
    GuestDelete(ctx, base, heap);
  }

  uint32_t Stage(const char* text) {
    size_t n = std::strlen(text);
    if (n >= kScratchBytes) {
      n = kScratchBytes - 1u;
    }
    std::memcpy(base + scratch, text, n);
    base[scratch + n] = 0;
    return scratch;
  }

  void GetField(int32_t index, const char* name) {
    Room(2u);
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_getfield(w, base);
  }

  void SetField(int32_t index, const char* name) {
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_setfield(w, base);
  }

  void RawGetI(int32_t index, int32_t n) {
    Room(1u);
    PPCContext w = Call();
    w.r4.s64 = index;
    w.r5.s64 = n;
    rex_lua_rawgeti(w, base);
  }

  void PushClosure(uint32_t function) {
    Room(1u);
    PPCContext w = Call();
    w.r4.u32 = function;
    w.r5.u32 = 0u;
    w.r6.u32 = 0u;
    rex_lua_pushcclosure(w, base);
  }

  /// luaL_loadbuffer + lua_pcall of `chunk`; the top restored. False with the message in `error`.
  bool Run(std::string_view chunk, const char* name, std::string* error) {
    const int32_t top = Top();
    const uint32_t size = static_cast<uint32_t>(chunk.size());
    const uint32_t buffer = GuestNew(ctx, base, size + 1u);
    if (!Plausible(buffer)) {
      if (error != nullptr) {
        *error = "no guest memory for the chunk";
      }
      return false;
    }
    std::memcpy(base + buffer, chunk.data(), size);
    base[buffer + size] = 0;

    PPCContext w = Call();
    w.r4.u32 = LoadU32(base, L) + kGlobalLoadSettings;
    w.r5.u32 = buffer;
    w.r6.u32 = size;
    w.r7.u32 = Stage(name);
    rex_luaL_loadbuffer(w, base);
    uint32_t status = w.r3.u32;
    if (status == 0u) {
      w = Call();
      w.r4.u32 = 0u;
      w.r5.u32 = 0u;
      w.r6.u32 = 0u;
      rex_lua_pcall(w, base);
      status = w.r3.u32;
    }
    if (status != 0u && error != nullptr) {
      *error = Top() > top ? String(-1, 1024u) : std::string("(no message)");
      if (error->empty()) {
        *error = "(a non-string error)";
      }
    }
    SetTop(top);
    GuestDelete(ctx, base, buffer);
    return status == 0u;
  }
};

// ---------------------------------------------------------------------------------------------
// The natives.

int32_t g_results = 0;
bool g_lua_cancelled = false;

#define REFII_F3_NATIVE(name)                         \
  void name##Body(GuestLua& lua);                     \
  void name(PPCContext& ctx, uint8_t* base) {         \
    const uint32_t state = ctx.r3.u32;                \
    PPCContext frame = Frame(ctx, kNativeFrame);      \
    GuestLua lua(frame, base, state);                 \
    g_results = 0;                                    \
    name##Body(lua);                                  \
    ctx.r3.u64 = static_cast<uint32_t>(g_results);    \
  }                                                   \
  void name##Body(GuestLua& lua)

uint32_t World(const uint8_t* base) {
  return Word(base, Word(base, Word(base, Word(base, Word(base, kGame) + 0xCu) + 0x1Cu) + 4u) + 4u);
}

/// Player 1's or 2's hero, read as the getters read it (without their release of a dead block). 0
/// when none.
uint32_t PlayerHero(const uint8_t* base, int32_t player) {
  if (player != 1 && player != 2) {
    return 0u;
  }
  const uint32_t world = World(base);
  if (!Plausible(world)) {
    return 0u;
  }
  const uint32_t block = Word(base, world + (player == 1 ? kWorldHeroOne : kWorldHeroTwo) + 4u);
  return Plausible(block) ? Word(base, block) : 0u;
}

uint32_t Fnv1(std::string_view text) {
  uint32_t hash = 0x811C9DC5u;
  for (char c : text) {
    hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(c);
  }
  return hash;
}

REFII_F3_NATIVE(NativeLog) {
  REXLOG_INFO("[mod lua] {}", lua.String(1, 1024u));
}

REFII_F3_NATIVE(NativeError) {
  const std::string text = lua.String(1, 1024u);
  if (!text.empty()) {
    refii_bridge_mod_error_post(text.data(), text.size());
    REXLOG_WARN("[mod lua] {}", text);
  }
}

REFII_F3_NATIVE(NativePublish) {
  const std::string key = lua.String(1, 128u);
  if (!key.empty()) {
    refii::mods::Publish(key, static_cast<float>(lua.Number(2)));
  }
}

REFII_F3_NATIVE(NativeValue) {
  const std::string key = lua.String(1, 128u);
  float value = 0.0f;
  if (!key.empty() && refii::mods::Published(key, value)) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativeHash) {
  char text[16];
  std::snprintf(text, sizeof(text), "0x%08x", Fnv1(lua.String(1, 256u)));
  lua.PushString(text);
  g_results = 1;
}

REFII_F3_NATIVE(NativePlayerHero) {
  const double player = lua.Type(1) == kTypeNumber ? lua.Number(1) : 1.0;
  const uint32_t hero = PlayerHero(lua.base, std::isfinite(player) ? static_cast<int32_t>(player) : 1);
  if (hero != 0u) {
    lua.PushNumber(refii_bridge_mod_handle(hero));
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativeCancelled) {
  (void)lua;
  g_lua_cancelled = true;
}

/// The event a seam is deciding now (refii::fable3::modlua::Decide), -1 outside one: Refii._write
/// lands in its writes.
int32_t g_deciding_kind = -1;
refii::fable3::modlua::Verdict* g_deciding = nullptr;

/// Refii._write(kind, field, value): a handler's change to a writable number of the event being
/// decided, as the preamble's _fire states it after the handlers ran. False outside that moment.
REFII_F3_NATIVE(NativeWrite) {
  const bool now = g_deciding != nullptr && lua.Type(1) == kTypeNumber &&
                   static_cast<int32_t>(lua.Number(1)) == g_deciding_kind && lua.Type(3) == kTypeNumber;
  if (now) {
    g_deciding->Write(lua.String(2, 64u), lua.Number(3));
  }
  lua.PushBoolean(now);
  g_results = 1;
}

/// The entity a script value names: a handle number, or a game Entity userdata — checked to carry
/// the Entity metatable (the game's glue dereferences any userdata unchecked) and resolved through
/// its script ref. 0 for anything else or a dead entity.
uint32_t EntityArgument(GuestLua& lua, int32_t index) {
  const int32_t type = lua.Type(index);
  if (type == kTypeNumber) {
    const double handle = lua.Number(index);
    return handle >= 1.0 && handle < 4294967296.0 ? refii_bridge_mod_handle_address(static_cast<uint32_t>(handle)) : 0u;
  }
  if (type != kTypeUserdata) {
    return 0u;
  }
  const uint32_t system = g_system.load(std::memory_order_relaxed);
  const uint32_t meta_ref = Plausible(system) ? LoadU32(lua.base, system + kScriptSystemEntityMeta) : 0xFFFFFFFEu;
  if (static_cast<int32_t>(meta_ref) < 0) {
    return 0u;
  }
  const int32_t top = lua.Top();
  lua.Room(2u);
  PPCContext w = lua.Call();
  w.r4.s64 = index;
  rex_lua_getmetatable(w, lua.base);
  bool entity_meta = false;
  if (w.r3.u32 != 0u) {
    lua.RawGetI(kRegistryIndex, static_cast<int32_t>(meta_ref));
    const uint32_t at = LoadU32(lua.base, lua.L + kStateTop);
    entity_meta = LoadU32(lua.base, at - 16u) == LoadU32(lua.base, at - 8u) &&
                  LoadU32(lua.base, at - 12u) == LoadU32(lua.base, at - 4u);
  }
  lua.SetTop(top);
  if (!entity_meta) {
    return 0u;
  }
  w = lua.Call();
  w.r4.s64 = index;
  rex_lua_touserdata(w, lua.base);
  const uint32_t block = w.r3.u32;
  const uint32_t ref = Plausible(block) ? LoadU32(lua.base, block) : 0u;
  if (!Plausible(ref)) {
    return 0u;
  }
  w = lua.ctx;
  w.r3.u32 = ref;
  rex_lhScriptEntityRef_Resolve(w, lua.base);
  return Plausible(w.r3.u32) ? w.r3.u32 : 0u;
}

/// Refii.entity(h): the game's own Entity userdata for a handle (or an Entity passed through), what
/// the shipped scripts and the game's natives take; nil for a dead or unknown one.
REFII_F3_NATIVE(NativeEntity) {
  const uint32_t entity = EntityArgument(lua, 1);
  if (entity == 0u) {
    lua.PushNil();
  } else {
    lua.Room(2u);
    PPCContext w = lua.Call();
    w.r4.u32 = entity;
    rex_lhLua_PushEntity(w, lua.base);
  }
  g_results = 1;
}

/// Refii.handle(e): the handle for a game Entity (or a handle passed through); nil for anything else.
REFII_F3_NATIVE(NativeHandle) {
  const uint32_t entity = EntityArgument(lua, 1);
  if (entity == 0u) {
    lua.PushNil();
  } else {
    lua.PushNumber(refii_bridge_mod_handle(entity));
  }
  g_results = 1;
}

/// Refii.alive(e): whether a handle or Entity still names a live entity.
REFII_F3_NATIVE(NativeAlive) {
  lua.PushBoolean(EntityArgument(lua, 1) != 0u);
  g_results = 1;
}

// ---------------------------------------------------------------------------------------------
// A mod's requests for Godot (core/refii_mod_requests.h, shared with Fable 2): the preamble's
// play_fx, light, message_box, menu_box, mark, world_meter... unpack their options into these, in
// Fable 2's argument order, so the one preamble serves both titles.

uint32_t HandleArgument(GuestLua& lua, int32_t index) {
  const double value = lua.Number(index);
  return value >= 1.0 && value < 4294967296.0 ? static_cast<uint32_t>(value) : 0u;
}

int32_t IntArgument(GuestLua& lua, int32_t index) {
  const double value = lua.Number(index);
  return value != value ? 0 : value >= 2147483647.0 ? 2147483647 : value <= -2147483648.0 ? (-2147483647 - 1)
                                                                                         : static_cast<int32_t>(value);
}

float FloatArgument(GuestLua& lua, int32_t index) { return static_cast<float>(lua.Number(index)); }

void PushId(GuestLua& lua, uint32_t id) {
  if (id != 0u) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativePlayFx) {
  const std::string name = lua.String(1, 127u);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const uint32_t handle = HandleArgument(lua, 5);
  refii_mod_fx_options options = refii_mod_fx_default_options();
  options.duration = FloatArgument(lua, 6);
  options.scale = FloatArgument(lua, 7);
  options.tint[0] = FloatArgument(lua, 8);
  options.tint[1] = FloatArgument(lua, 9);
  options.tint[2] = FloatArgument(lua, 10);
  options.intensity = FloatArgument(lua, 11);
  options.loop = IntArgument(lua, 12) != 0 ? 1 : 0;
  options.offset[0] = FloatArgument(lua, 13);
  options.offset[1] = FloatArgument(lua, 14);
  options.offset[2] = FloatArgument(lua, 15);
  const std::string at = lua.String(16, 95u);
  options.at = at.empty() ? nullptr : at.c_str();
  PushId(lua, refii_mod_play_fx_handle(name.c_str(), handle != 0u ? nullptr : xyz, handle, &options));
}

REFII_F3_NATIVE(NativeStopFx) { refii_mod_stop_fx(HandleArgument(lua, 1)); }

REFII_F3_NATIVE(NativeLight) {
  const uint32_t handle = HandleArgument(lua, 1);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const float rgb[3] = {FloatArgument(lua, 5), FloatArgument(lua, 6), FloatArgument(lua, 7)};
  refii_mod_light_options options{};
  options.offset[0] = FloatArgument(lua, 10);
  options.offset[1] = FloatArgument(lua, 11);
  options.offset[2] = FloatArgument(lua, 12);
  const std::string at = lua.String(13, 95u);
  options.at = at.empty() ? nullptr : at.c_str();
  options.flicker = FloatArgument(lua, 14);
  options.direction[0] = FloatArgument(lua, 15);
  options.direction[1] = FloatArgument(lua, 16);
  options.direction[2] = FloatArgument(lua, 17);
  options.cone = FloatArgument(lua, 18);
  options.attenuation = FloatArgument(lua, 19);
  PushId(lua, refii_mod_light_handle(handle, xyz, rgb, FloatArgument(lua, 8), FloatArgument(lua, 9), &options));
}

REFII_F3_NATIVE(NativeLightRemove) { refii_mod_light_remove(HandleArgument(lua, 1)); }

REFII_F3_NATIVE(NativeOpenMenu) {
  lua.PushBoolean(refii_mod_open_menu(IntArgument(lua, 2), lua.String(1, 95u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMessageBox) {
  const std::string title = lua.String(1, 127u);
  const std::string text = lua.String(2, 479u);
  const std::string accept = lua.String(3, 63u);
  const std::string cancel = lua.String(4, 63u);
  PushId(lua, refii_mod_message_box(IntArgument(lua, 6), title.c_str(), text.c_str(), accept.c_str(),
                                    IntArgument(lua, 5) != 0 ? cancel.c_str() : nullptr));
}

REFII_F3_NATIVE(NativeNotice) {
  const std::string title = lua.String(1, 127u);
  const std::string text = lua.String(2, 479u);
  PushId(lua, refii_mod_notice(IntArgument(lua, 4), title.c_str(), text.c_str(), FloatArgument(lua, 3)));
}

REFII_F3_NATIVE(NativeMenuBox) {
  constexpr int32_t kMax = 12;
  const std::string title = lua.String(1, 127u);
  const std::string text = lua.String(2, 479u);
  const int32_t count = std::max<int32_t>(0, std::min<int32_t>(kMax, IntArgument(lua, 5)));
  std::string options[kMax];
  const char* list[kMax];
  for (int32_t i = 0; i < count; ++i) {
    options[i] = lua.String(6 + i, 95u);
    list[i] = options[i].c_str();
  }
  PushId(lua, refii_mod_menu_box(IntArgument(lua, 3), title.c_str(), text.c_str(), list, count,
                                 IntArgument(lua, 4) != 0 ? 1 : 0));
}

REFII_F3_NATIVE(NativeText) {
  const std::string text = lua.String(1, 479u);
  char out[1024];
  if (!text.empty() && refii_mod_text(text.c_str(), out, sizeof(out)) >= 0) {
    lua.PushString(out);
  } else {
    lua.PushNil();
  }
  g_results = 1;
}

REFII_F3_NATIVE(NativeMark) {
  const uint32_t handle = HandleArgument(lua, 1);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const std::string icon = lua.String(5, 159u);
  const std::string text = lua.String(6, 159u);
  const uint32_t entity = handle != 0u ? refii_bridge_mod_handle_address(handle) : 0u;
  PushId(lua, refii_mod_mark(IntArgument(lua, 7), entity, handle != 0u ? nullptr : xyz, icon.c_str(), text.c_str()));
}

REFII_F3_NATIVE(NativeUnmark) { refii_mod_unmark(HandleArgument(lua, 1)); }

REFII_F3_NATIVE(NativeWorldMeter) {
  const uint32_t handle = HandleArgument(lua, 1);
  const float xyz[3] = {FloatArgument(lua, 2), FloatArgument(lua, 3), FloatArgument(lua, 4)};
  const std::string style = lua.String(7, 31u);
  const uint32_t entity = handle != 0u ? refii_bridge_mod_handle_address(handle) : 0u;
  PushId(lua, refii_mod_world_meter(IntArgument(lua, 8), entity, handle != 0u ? nullptr : xyz, FloatArgument(lua, 5),
                                    FloatArgument(lua, 6), style.c_str()));
}

REFII_F3_NATIVE(NativeMarkMeter) {
  lua.PushBoolean(refii_mod_mark_meter(HandleArgument(lua, 1), FloatArgument(lua, 2), FloatArgument(lua, 3),
                                       lua.String(4, 31u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeFade) {
  const std::string dir = lua.String(2, 31u);
  const bool to_black = dir == "to_black" || dir == "out" || dir == "black";
  lua.PushBoolean(refii_mod_fade(IntArgument(lua, 1), to_black ? 1 : 0, FloatArgument(lua, 3)) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMeter) {
  lua.PushBoolean(refii_mod_meter(IntArgument(lua, 5), lua.String(1, 95u).c_str(), lua.String(2, 127u).c_str(),
                                  FloatArgument(lua, 3), FloatArgument(lua, 4), lua.String(6, 31u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMeterRemove) {
  lua.PushBoolean(refii_mod_meter_remove(lua.String(1, 95u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeInputClaim) {
  lua.PushBoolean(refii_mod_input_claim(IntArgument(lua, 1), lua.String(2, 95u).c_str(), lua.Boolean(3) ? 1 : 0) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeHudIcon) {
  lua.PushBoolean(refii_mod_hud_icon(IntArgument(lua, 1), lua.String(2, 95u).c_str(), lua.String(3, 159u).c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeHudSlide) {
  lua.PushBoolean(refii_mod_hud_slide(IntArgument(lua, 1), lua.String(2, 95u).c_str(), IntArgument(lua, 3)) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeHudHold) {
  lua.PushBoolean(refii_mod_hud_hold(IntArgument(lua, 1), lua.String(2, 95u).c_str(), lua.Boolean(3) ? 1 : 0) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativePress) {
  const int32_t held = lua.Type(3) == kTypeNumber ? IntArgument(lua, 3) : (lua.Boolean(3) ? 1 : 0);
  lua.PushBoolean(refii_mod_press(IntArgument(lua, 1), lua.String(2, 31u).c_str(), held) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeTap) {
  const int32_t ticks = lua.Type(3) == kTypeNumber ? IntArgument(lua, 3) : 1;
  lua.PushBoolean(refii_mod_tap(IntArgument(lua, 1), lua.String(2, 31u).c_str(), ticks) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeMenuRows) {
  const std::string rows = lua.String(2, REFII_MOD_MENU_ROWS_MAX + 1u);
  lua.PushBoolean(refii_mod_set_menu_rows(IntArgument(lua, 3), lua.String(1, 95u).c_str(), rows.c_str()) != 0);
  g_results = 1;
}

REFII_F3_NATIVE(NativeOpenScreen) {
  const double id = lua.Number(1);
  lua.PushBoolean(id >= 1.0 && id <= 4294967295.0 &&
                  refii_mod_open_screen(IntArgument(lua, 2), static_cast<uint32_t>(id)) != 0);
  g_results = 1;
}

struct Native {
  const char* name;
  void (*function)(PPCContext&, uint8_t*);
  uint32_t thunk;
};

Native g_natives[] = {
    {"log", &NativeLog, 0u},
    {"_error", &NativeError, 0u},
    {"publish", &NativePublish, 0u},
    {"value", &NativeValue, 0u},
    {"hash", &NativeHash, 0u},
    {"player_hero", &NativePlayerHero, 0u},
    {"entity", &NativeEntity, 0u},
    {"handle", &NativeHandle, 0u},
    {"alive", &NativeAlive, 0u},
    {"_play_fx", &NativePlayFx, 0u},
    {"stop_fx", &NativeStopFx, 0u},
    {"_light", &NativeLight, 0u},
    {"light_remove", &NativeLightRemove, 0u},
    {"open_menu", &NativeOpenMenu, 0u},
    {"_message_box", &NativeMessageBox, 0u},
    {"notice", &NativeNotice, 0u},
    {"_menu_box", &NativeMenuBox, 0u},
    {"_text", &NativeText, 0u},
    {"_mark", &NativeMark, 0u},
    {"unmark", &NativeUnmark, 0u},
    {"_world_meter", &NativeWorldMeter, 0u},
    {"mark_meter", &NativeMarkMeter, 0u},
    {"fade", &NativeFade, 0u},
    {"meter", &NativeMeter, 0u},
    {"meter_remove", &NativeMeterRemove, 0u},
    {"input_claim", &NativeInputClaim, 0u},
    {"hud_icon", &NativeHudIcon, 0u},
    {"hud_slide", &NativeHudSlide, 0u},
    {"hud_hold", &NativeHudHold, 0u},
    {"press", &NativePress, 0u},
    {"tap", &NativeTap, 0u},
    {"_menu_rows", &NativeMenuRows, 0u},
    {"open_screen", &NativeOpenScreen, 0u},
    {"_cancelled", &NativeCancelled, 0u},
    {"_write", &NativeWrite, 0u},
};

/// A guest function inside the game module, for the thunk pool: lhLua_RunScriptBuffer.
const refii::guest::Addr kModuleAnchor{0x822E56E0u};

bool AllocateThunks() {
  static bool done = false;
  static bool ok = false;
  if (done) {
    return ok;
  }
  done = true;
  ok = true;
  for (Native& native : g_natives) {
    native.thunk = refii_bridge_rex_allocate_thunk(reinterpret_cast<void*>(native.function), kModuleAnchor);
    if (native.thunk == 0u) {
      REXLOG_WARN("Refii mod Lua: no guest address for Refii.{}; the natives are not registered", native.name);
      ok = false;
    }
  }
  return ok;
}

/// The namespace RegisterFunctionHash reads for the Refii natives, built once in the guest heap:
/// block+0 the namespace (+8 its CCharString "Refii", +12 its enable byte), block+0x18 the holder.
uint32_t RefiiNamespaceHolder(PPCContext& ctx, uint8_t* base) {
  static uint32_t holder = 0u;
  if (holder != 0u) {
    return holder;
  }
  const uint32_t block = GuestNew(ctx, base, 0x40u);
  if (!Plausible(block)) {
    return 0u;
  }
  std::memset(base + block, 0, 0x40u);
  const uint32_t chars = block + 0x20u;
  std::memcpy(base + chars, "Refii", 6u);
  PPCContext w = Frame(ctx, 0x100u);
  w.r3.u32 = block + 8u;
  w.r4.u32 = chars;
  w.r5.s64 = -1;
  rex_lhCharString_Ctor(w, base);
  base[block + 12u] = 1u;
  StoreU32(base, block + 0x18u, block);
  holder = block + 0x18u;
  return holder;
}

/// The Refii table and every native in it, in the VM `state`.
void RegisterNatives(PPCContext& ctx, uint8_t* base, uint32_t state) {
  if (!AllocateThunks()) {
    return;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  std::string error;
  if (!lua.Run("Refii = Refii or {}", "=refii/natives", &error)) {
    REXLOG_WARN("Refii mod Lua: the Refii table could not be made: {}", error);
    return;
  }
  const uint32_t holder = RefiiNamespaceHolder(frame, base);
  const int32_t top = lua.Top();
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) != kTypeTable) {
    lua.SetTop(top);
    return;
  }
  for (const Native& native : g_natives) {
    lua.PushClosure(native.thunk);
    if (holder != 0u) {
      PPCContext w = frame;
      w.r3.u32 = holder;
      w.r4.u32 = state;
      w.r5.u32 = lua.Stage(native.name);
      rex_lhLuaNamespace_RegisterFunctionHash(w, base);
    }
    lua.SetField(-2, native.name);
  }
  lua.SetTop(top);
  REXLOG_INFO("Refii mod Lua: {} Refii.* natives registered{}", sizeof(g_natives) / sizeof(g_natives[0]),
              holder != 0u ? " as permanents" : "");
}

/// The host's composed chunk at `key`, run in `state`. False when there is none or it failed.
bool RunComposed(PPCContext& ctx, uint8_t* base, uint32_t state, const char* key) {
  std::vector<uint8_t> text;
  if (!refii::vfs::ReadHostAsset(key, text) || text.empty()) {
    return false;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  std::string error;
  const std::string name = std::string("=") + key;
  const bool ran = lua.Run(std::string_view(reinterpret_cast<const char*>(text.data()), text.size()),
                           name.c_str(), &error);
  if (ran) {
    REXLOG_INFO("Refii mod Lua: {} ({} bytes) ran", key, text.size());
  } else {
    REXLOG_WARN("Refii mod Lua: {} ({} bytes) FAILED: {}", key, text.size(), error);
    const std::string report = std::string(key) + ": " + error;
    refii_bridge_mod_error_post(report.data(), report.size());
  }
  return ran;
}

// ---------------------------------------------------------------------------------------------
// Entities coming and going, raised from the next tick: the seams run inside the world's own update
// (and a destroy inside a script's native), where no Lua is run.

struct QueuedEvent {
  int32_t kind;
  std::string fields;
};

std::mutex g_queue_mutex;
std::vector<QueuedEvent> g_queue;
constexpr size_t kMaxQueued = 4096u;
constexpr int kRaisedPerTick = 64;

bool HostOrLuaWants(int32_t kind);

void QueueEdge(int32_t kind, uint32_t entity) {
  if (!HostOrLuaWants(kind)) {
    return;
  }
  char fields[48];
  std::snprintf(fields, sizeof(fields), "entity=%u", refii_bridge_mod_handle(entity));
  refii::fable3::modlua::QueueEvent(kind, fields);
}

// ---------------------------------------------------------------------------------------------
// The bus's Lua side and the host lanes.

uint32_t g_lua_masks[kLuaMaskGroups] = {};
uint32_t g_mask_generation = 0xFFFFFFFFu;
float g_dirty_poll = 0.0f;
uint32_t g_hero_seen[2] = {0u, 0u};
int32_t g_lua_depth = 0;

bool LuaWants(int32_t kind) {
  if (kind < 0 || kind >= kLuaMaskBits * kLuaMaskGroups) {
    return false;
  }
  return ((g_lua_masks[kind / kLuaMaskBits] >> (kind % kLuaMaskBits)) & 1u) != 0u;
}

/// Whether anyone hears `kind`: Lua's handlers, Godot's mask, a Godot verdict or a native handler.
bool HostOrLuaWants(int32_t kind) {
  return LuaWants(kind) || refii::mods::HostWantsEvent(kind) || refii_mod_bus_wants(kind) != 0 ||
         (kind >= 0 && kind < refii::mods::kHostMaskCapacity &&
          ((refii_bridge_mod_verdict_mask_word(kind / 64) >> (kind % 64)) & 1u) != 0u);
}

/// Refii._masks[1..groups] read straight off the table, and Refii._dirty cleared.
void ReadLuaMasks(GuestLua& lua) {
  const int32_t top = lua.Top();
  for (uint32_t& mask : g_lua_masks) {
    mask = 0u;
  }
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) == kTypeTable) {
    lua.GetField(-1, "_masks");
    if (lua.Type(-1) == kTypeTable) {
      for (int32_t g = 0; g < kLuaMaskGroups; ++g) {
        lua.RawGetI(-1, g + 1);
        if (lua.Type(-1) == kTypeNumber) {
          const double value = lua.Number(-1);
          if (value >= 0.0 && value < static_cast<double>(1u << kLuaMaskBits)) {
            g_lua_masks[g] = static_cast<uint32_t>(value);
          }
        }
        lua.SetTop(-2);
      }
    }
    lua.SetTop(-2);
    lua.PushBoolean(false);
    lua.SetField(-2, "_dirty");
  }
  lua.SetTop(top);
}

bool LuaDirty(GuestLua& lua) {
  const int32_t top = lua.Top();
  bool dirty = false;
  lua.GetField(kGlobalsIndex, "Refii");
  if (lua.Type(-1) == kTypeTable) {
    lua.GetField(-1, "_dirty");
    dirty = lua.Boolean(-1);
  }
  lua.SetTop(top);
  return dirty;
}

/// A text as a single-quoted Lua literal's body: quotes, backslashes and line breaks become
/// underscores, as Fable 2's LuaString.
std::string LuaQuoted(std::string_view text) {
  std::string out;
  out.reserve(text.size());
  for (char c : text) {
    out.push_back((c == '\'' || c == '\\' || c == '\n' || c == '\r') ? '_' : c);
  }
  return out;
}

/// The kinds Fable 3 raises, by the catalog's Lua names (ModApi.Fable3Raised), for the native bus.
constexpr std::pair<const char*, int32_t> kRaisedNames[] = {
    {"tick", 0},           {"hero_ready", 1},      {"hero_gone", 2},        {"lua_ready", 3},
    {"save_loaded", 4},    {"entity_died", 21},    {"hero_downed", 22},     {"level_loaded", 24},
    {"level_unloading", 25}, {"ability_level", 31}, {"gold", 32},           {"input_action", 34},
    {"quest_started", 35}, {"quest_ended", 36},    {"quest_objective", 37}, {"entity_spawned", 39},
    {"entity_removed", 40}, {"renown", 42},        {"fast_travel", 56},     {"health_changed", 101},
    {"guild_seals", 116},  {"treasury", 117},      {"kingdom_decision", 118},
};

void NameRaisedKinds() {
  static const bool named = [] {
    for (const auto& name : kRaisedNames) {
      refii_mod_bus_name(name.first, name.second);
    }
    return true;
  }();
  (void)named;
}

/// A verdict's answer (`cancel=1,field=number,...`, a Godot verdict's or the native bus's) into `verdict`.
void ApplyAnswer(refii::fable3::modlua::Verdict& verdict, char* answer) {
  for (char* cursor = answer; cursor != nullptr && *cursor != 0;) {
    char* comma = std::strchr(cursor, ',');
    if (comma != nullptr) {
      *comma = 0;
    }
    if (char* equals = std::strchr(cursor, '='); equals != nullptr) {
      *equals = 0;
      const double value = std::strtod(equals + 1, nullptr);
      if (std::strcmp(cursor, "cancel") == 0) {
        verdict.cancelled = verdict.cancelled || value != 0.0;
      } else {
        verdict.Write(cursor, value);
      }
    }
    cursor = comma != nullptr ? comma + 1 : nullptr;
  }
}

/// Raises `kind` with the table text `fields` (Lua's and the host's, as Fable 2's HostFields):
/// to Lua's handlers when its mask wants it, then to Godot when its mask does. True when a Lua
/// handler cancelled.
bool Raise(GuestLua& lua, int32_t kind, const std::string& fields) {
  bool cancelled = false;
  if (refii_mod_bus_wants(kind) != 0) {
    char answer[512];
    refii_mod_bus_run(kind, fields.data(), fields.size(), answer, sizeof(answer));
  }
  if (LuaWants(kind) && g_lua_depth < 2) {
    char head[160];
    std::snprintf(head, sizeof(head), "if Refii and Refii._fire and not Refii._fire(%d,{", kind);
    const std::string chunk =
        std::string(head) + fields + "}) and Refii._cancelled then Refii._cancelled() end";
    const bool outer = g_lua_cancelled;
    g_lua_cancelled = false;
    ++g_lua_depth;
    std::string error;
    if (!lua.Run(chunk, "=refii/event", &error)) {
      REXLOG_WARN("Refii mod Lua: event {} did not run: {}", kind, error);
    }
    --g_lua_depth;
    cancelled = g_lua_cancelled;
    g_lua_cancelled = outer;
  }
  if (refii::mods::HostWantsEvent(kind)) {
    refii::mods::PostEvent(kind, fields);
  }
  return cancelled;
}

void Reply(const std::string& id, bool ok, const std::string& fields) {
  std::string text = "id=" + id + ",ok=" + (ok ? "true" : "false");
  if (!fields.empty()) {
    text += "," + fields;
  }
  refii::mods::PostEvent(kHostReplyKind, text);
}

/// One line of the host's mod lane (HostBridge.PostModCommand). The verbs Fable 3 serves; any
/// other is named once and dropped, and a query is answered unsupported so its caller never waits.
void RunCommand(GuestLua& lua, const std::string& line) {
  const size_t space = line.find(' ');
  const std::string op = line.substr(0, space);
  const std::string rest = space == std::string::npos ? std::string() : line.substr(space + 1u);
  if (op == "run_lua") {
    std::string error;
    if (!lua.Run(rest, "=refii/run_lua", &error)) {
      REXLOG_WARN("Refii mod Lua: run_lua failed: {}", error);
      refii_bridge_mod_error_post(error.data(), error.size());
    }
    return;
  }
  if (op == "publish") {
    const size_t at = rest.find(' ');
    if (at != std::string::npos) {
      refii::mods::Publish(rest.substr(0, at), std::strtof(rest.c_str() + at + 1u, nullptr));
    }
    return;
  }
  if (op == "mask_word") {
    char which[16] = {0};
    int32_t word = -1;
    unsigned long long bits = 0;
    if (std::sscanf(rest.c_str(), "%15s %d %llx", which, &word, &bits) == 3) {
      if (std::strcmp(which, "event") == 0) {
        refii_bridge_mod_set_host_event_mask_word(word, bits);
      } else if (std::strcmp(which, "verdict") == 0) {
        refii_bridge_mod_set_verdict_mask_word(word, bits);
      }
    }
    return;
  }
  if (op == "query") {
    const size_t at = rest.find(' ');
    const std::string id = rest.substr(0, at);
    const std::string verb = at == std::string::npos ? std::string() : rest.substr(at + 1u, rest.find(' ', at + 1u) - at - 1u);
    Reply(id.empty() ? std::string("0") : id, false, "error='" + LuaQuoted(verb) + " is not available in Fable 3 yet'");
    return;
  }
  static uint32_t named = 0u;
  if (named < 32u) {
    ++named;
    REXLOG_WARN("Refii mod Lua: command '{}' has no Fable 3 implementation; dropped", op);
  }
}

/// The heroes, stated to Lua (Refii._hero) and raised as hero_ready / hero_gone on a change.
void PumpHeroes(GuestLua& lua) {
  for (int32_t p = 1; p <= 2; ++p) {
    const uint32_t hero = PlayerHero(lua.base, p);
    uint32_t& seen = g_hero_seen[p - 1];
    if (hero == seen) {
      continue;
    }
    char fields[64];
    if (seen != 0u) {
      std::snprintf(fields, sizeof(fields), "player=%d,hero=%u", p, refii_bridge_mod_handle(seen));
      Raise(lua, kEventHeroGone, fields);
    }
    seen = hero;
    char chunk[96];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._hero then Refii._hero[%d] = %u end", p,
                  hero != 0u ? refii_bridge_mod_handle(hero) : 0u);
    lua.Run(chunk, "=refii/hero", nullptr);
    if (hero != 0u) {
      std::snprintf(fields, sizeof(fields), "player=%d,hero=%u", p, refii_bridge_mod_handle(hero));
      Raise(lua, kEventHero, fields);
    }
  }
}

/// A new VM, or a save read into one: the natives, the chunks, the masks, the ready event.
void OnLuaReady(PPCContext& ctx, uint8_t* base, uint32_t state, bool save_loaded) {
  const uint32_t generation = g_generation.fetch_add(1u, std::memory_order_relaxed) + 1u;
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  g_hero_seen[0] = g_hero_seen[1] = 0u;
  ReadLuaMasks(lua);
  g_mask_generation = generation;
  char fields[32];
  std::snprintf(fields, sizeof(fields), "generation=%u", generation);
  Raise(lua, save_loaded ? kEventSaveLoaded : kEventLua, fields);
}

}  // namespace

namespace refii::fable3::modlua {

bool Wants(int32_t kind) { return HostOrLuaWants(kind); }

void Verdict::Write(const std::string& field, double value) {
  for (auto& write : writes) {
    if (write.first == field) {
      write.second = value;
      return;
    }
  }
  writes.emplace_back(field, value);
}

bool Verdict::Read(const char* field, double& value) const {
  for (const auto& write : writes) {
    if (write.first == field) {
      value = write.second;
      return true;
    }
  }
  return false;
}

Verdict Decide(PPCContext& ctx, uint8_t* base, int32_t kind, const std::string& fields) {
  Verdict verdict;
  const bool godot = kind >= 0 && kind < refii::mods::kHostMaskCapacity &&
                     ((refii_bridge_mod_verdict_mask_word(kind / 64) >> (kind % 64)) & 1u) != 0u &&
                     refii_bridge_mod_verdict_handler() != nullptr;
  const bool native = refii_mod_bus_wants(kind) != 0;
  if (!LuaWants(kind) && !godot && !native) {
    if (refii::mods::HostWantsEvent(kind)) {
      refii::mods::PostEvent(kind, fields);
    }
    return verdict;
  }
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state == 0u || !OnVmThread() || g_deciding != nullptr) {
    // Not on the VM's thread, or inside another decision: reported, never decided.
    QueueEvent(kind, fields);
    return verdict;
  }

  g_deciding = &verdict;
  g_deciding_kind = kind;
  // Native handlers first (core/refii_mod_bus.h), as Fable 2's typed subscribers run before Lua.
  if (native) {
    char answer[512];
    if (refii_mod_bus_run(kind, fields.data(), fields.size(), answer, sizeof(answer)) > 0) {
      ApplyAnswer(verdict, answer);
    }
  }
  if (LuaWants(kind) && !verdict.cancelled) {
    PPCContext frame = Frame(ctx, kNativeFrame);
    GuestLua lua(frame, base, state);
    char head[160];
    std::snprintf(head, sizeof(head), "if Refii and Refii._fire and not Refii._fire(%d,{", kind);
    const std::string chunk = std::string(head) + fields + "}) and Refii._cancelled then Refii._cancelled() end";
    const bool outer = g_lua_cancelled;
    g_lua_cancelled = false;
    std::string error;
    if (!lua.Run(chunk, "=refii/event", &error)) {
      REXLOG_WARN("Refii mod Lua: event {} did not run: {}", kind, error);
    }
    verdict.cancelled = g_lua_cancelled;
    g_lua_cancelled = outer;
  }

  // The fields as they stand after Lua's writes: what Godot's verdict and the host's event read.
  std::string stated = fields;
  for (const auto& write : verdict.writes) {
    const std::string key = write.first + "=";
    const size_t at = stated.find(key) == 0 ? 0 : stated.find("," + key);
    if (at == std::string::npos) {
      continue;
    }
    const size_t value_at = at + (at == 0 ? 0u : 1u) + key.size();
    const size_t end = stated.find(',', value_at);
    char number[32];
    std::snprintf(number, sizeof(number), "%.9g", write.second);
    stated.replace(value_at, (end == std::string::npos ? stated.size() : end) - value_at, number);
  }

  if (godot && !verdict.cancelled) {
    char answer[512];
    const int32_t length =
        refii_bridge_mod_verdict_handler()(kind, stated.c_str(), stated.size(), answer, sizeof(answer) - 1u);
    if (length > 0) {
      answer[length < static_cast<int32_t>(sizeof(answer)) ? length : static_cast<int32_t>(sizeof(answer)) - 1] = 0;
      ApplyAnswer(verdict, answer);
    }
  }
  g_deciding = nullptr;
  g_deciding_kind = -1;

  if (refii::mods::HostWantsEvent(kind)) {
    refii::mods::PostEvent(kind, verdict.cancelled ? stated + ",cancelled=true" : stated);
  }
  return verdict;
}

void QueueEvent(int32_t kind, std::string fields) {
  std::lock_guard<std::mutex> lock(g_queue_mutex);
  if (g_queue.size() < kMaxQueued) {
    g_queue.push_back({kind, std::move(fields)});
  }
}

uint32_t Hero(const uint8_t* base, int32_t player) { return PlayerHero(base, player); }

int32_t PlayerOf(const uint8_t* base, uint32_t entity) {
  if (!Plausible(entity)) {
    return 0;
  }
  return entity == PlayerHero(base, 1) ? 1 : entity == PlayerHero(base, 2) ? 2 : 0;
}

std::string Quoted(std::string_view text) { return LuaQuoted(text); }

bool Run(PPCContext& ctx, uint8_t* base, std::string_view chunk, const char* name, std::string* error) {
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state == 0u) {
    if (error != nullptr) {
      *error = "no game VM";
    }
    return false;
  }
  if (!OnVmThread()) {
    if (error != nullptr) {
      *error = "not the game VM's thread";
    }
    return false;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);
  return lua.Run(chunk, name, error);
}

void Tick(PPCContext& ctx, uint8_t* base) {
  const uint32_t state = g_state.load(std::memory_order_relaxed);
  if (state == 0u || !OnVmThread()) {
    // No VM to serve: input edges are stale by the time one exists.
    char name[160];
    while (refii_bridge_mod_input_poll(name, sizeof(name), nullptr, nullptr) != 0) {
    }
    return;
  }
  PPCContext frame = Frame(ctx, kNativeFrame);
  GuestLua lua(frame, base, state);

  const uint32_t generation = g_generation.load(std::memory_order_relaxed);
  if (generation != g_mask_generation) {
    g_mask_generation = generation;
    ReadLuaMasks(lua);
    g_dirty_poll = 0.0f;
  } else {
    g_dirty_poll += kStepSeconds;
    if (g_dirty_poll >= kLuaDirtyPollSeconds) {
      g_dirty_poll = 0.0f;
      if (LuaDirty(lua)) {
        ReadLuaMasks(lua);
      }
    }
  }

  PumpHeroes(lua);

  char name[160];
  int32_t player = 0;
  int32_t pressed = 0;
  while (refii_bridge_mod_input_poll(name, sizeof(name), &player, &pressed) != 0) {
    char fields[256];
    std::snprintf(fields, sizeof(fields), "name='%s',player=%d,pressed=%s", LuaQuoted(name).c_str(), player,
                  pressed != 0 ? "true" : "false");
    Raise(lua, kEventInputAction, fields);
  }

  std::vector<QueuedEvent> queued;
  {
    std::lock_guard<std::mutex> lock(g_queue_mutex);
    const size_t n = g_queue.size() < static_cast<size_t>(kRaisedPerTick) ? g_queue.size() : kRaisedPerTick;
    queued.assign(std::make_move_iterator(g_queue.begin()),
                  std::make_move_iterator(g_queue.begin() + static_cast<std::ptrdiff_t>(n)));
    g_queue.erase(g_queue.begin(), g_queue.begin() + static_cast<std::ptrdiff_t>(n));
  }
  for (const QueuedEvent& event : queued) {
    Raise(lua, event.kind, event.fields);
  }

  // Bounded per step, as Fable 2's, so a flooding script cannot stall the simulation.
  char line[4096];
  for (int n = 0; n < 64 && refii_bridge_mod_command_poll(line, sizeof(line)) != 0; ++n) {
    line[sizeof(line) - 1] = '\0';
    RunCommand(lua, line);
  }

  char fields[48];
  std::snprintf(fields, sizeof(fields), "seconds=%.9g", static_cast<double>(kStepSeconds));
  Raise(lua, kEventTick, fields);
}

}  // namespace refii::fable3::modlua

REX_EXTERN(rex_lhLuaScriptSystem_LoadStartupScripts) {
  const PPCContext entry = ctx;
  const uint32_t system = ctx.r3.u32;
  const bool ran_before = Plausible(system) && base[system + kScriptSystemRan] != 0u;
  __imp__rex_lhLuaScriptSystem_LoadStartupScripts(ctx, base);
  if (!Plausible(system) || ran_before || base[system + kScriptSystemRan] == 0u) {
    return;
  }

  const uint32_t state = LoadU32(base, system);
  if (!Plausible(state)) {
    return;
  }
  g_vm_thread.store(std::this_thread::get_id(), std::memory_order_relaxed);
  g_system.store(system, std::memory_order_relaxed);
  g_state.store(state, std::memory_order_relaxed);
  REXLOG_INFO("Refii mod Lua: the game VM 0x{:08X} ran its startup scripts", state);

  PPCContext work = entry;
  NameRaisedKinds();
  RegisterNatives(work, base, state);
  RunComposed(work, base, state, kCoreKey);
  RunComposed(work, base, state, kInitKey);
  RunComposed(work, base, state, kCameraKey);
  OnLuaReady(work, base, state, false);
}

REX_EXTERN(rex_lhLuaScriptSystem_LoadScriptSave) {
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_LoadScriptSave(ctx, base);
  const uint32_t system = entry.r3.u32;
  const uint32_t state = Plausible(system) ? LoadU32(base, system) : 0u;
  if (state != 0u && state == g_state.load(std::memory_order_relaxed)) {
    PPCContext work = entry;
    OnLuaReady(work, base, state, true);
  }
}

REX_EXTERN(rex_lhLuaScriptSystem_RunCameraScripts) {
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_RunCameraScripts(ctx, base);
  const uint32_t system = entry.r3.u32;
  const uint32_t state = Plausible(system) ? LoadU32(base, system) : 0u;
  if (state != 0u && state == g_state.load(std::memory_order_relaxed)) {
    PPCContext work = entry;
    RunComposed(work, base, state, kCameraKey);
  }
}

REX_EXTERN(rex_lua_close) {
  const uint32_t state = ctx.r3.u32;
  uint32_t expected = state;
  if (state != 0u && g_state.compare_exchange_strong(expected, 0u, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii mod Lua: the game VM 0x{:08X} is closing", state);
  }
  __imp__rex_lua_close(ctx, base);
}

/// An entity linked into the manager: raised as entity_spawned from the next tick.
REX_EXTERN(rex_lhEntityManager_Add) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t entity = ctx.r4.u32;
  const uint32_t before = Plausible(manager) ? LoadU32(base, manager + 0xCu) : 0u;
  __imp__rex_lhEntityManager_Add(ctx, base);
  if (Plausible(manager) && Plausible(entity) && LoadU32(base, manager + 0xCu) != before) {
    QueueEdge(kEventEntitySpawned, entity);
  }
}

/// An entity's destroy: its handle is forgotten (a handle never outlives its entity) and
/// entity_removed raised from the next tick with the number scripts held.
REX_EXTERN(rex_lhEntity_Destroy) {
  const uint32_t entity = ctx.r3.u32;
  if (Plausible(entity)) {
    QueueEdge(kEventEntityRemoved, entity);
  }
  __imp__rex_lhEntity_Destroy(ctx, base);
  if (Plausible(entity)) {
    refii_bridge_mod_handle_forget(entity);
  }
}
