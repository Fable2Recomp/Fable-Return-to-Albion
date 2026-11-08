#include "vfs_hooks_private.h"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/host_asset_reader.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/rex_payload_bridge.h"
#include "hooks/ppc_externs.h"

// MODS' LUA ON THE TITLE AND THE FRONT END.
//
// There is no game VM there: the game's Lua is built per session by Game_InitDatabases
// (`stw r3, 0xAC(r27)` at 0x82310e30) and closed when it ends. The front end's Lua is Gameface's,
// a second Lua 5.1 build linked beside the game's, with one state PER PRESENTATION (TU1):
//   `sub_82BBA588` loads a presentation, then `off_83314AC0` = `sub_82BC4548` allocates a
//   CLuaEngine and runs AKScriptVM_Create (0x82BB74C0) on it, and the presentation keeps it
//   (vtable slot 4, 0x82bba648); `AK_RegisterDataPortAPI` and the AKGameplan globals follow.
//   AKScriptVM_Create calls `lua_newstate`, then `luaL_openlibs` (`off_83314BB4` = 0x82BCE220),
//   whose table at 0x82090800 opens base, package, table, io, os, string, math and debug, and
//   registers the element API (`AK_RegisterElementScriptAPI`). The state is +4 of the engine.
//   A slide change is inside one presentation (`rex_lhGuiAnimation_GotoSlide`), so the state
//   lives as long as its presentation; another presentation has another state.
//
// RUNNING A CHUNK. `sub_82372500(LHString*, engine)` (rex_lhGuiPresentation_RunLuaString) pushes
// its error handler `sub_82372480` (lua_pushcclosure 0x8227B968), loads the text (`getS` reader,
// 0x82372590) and calls it through `luaD_pcall` (0x823725c8) with that handler, then restores
// the top (`lua_settop`, 0x823725ec): PROTECTED, an error never leaves it. The engine is the
// presentation's vtable slot 5 (`sub_82426470`: `(*(*a2 + 20))(a2)` then `sub_82372500`). That
// call is made here directly: `sub_82426470` defers it onto GUI+88 once the GUI's first update
// has cleared `guiObject+0x64`.
//
// REFII NATIVES IN IT, as the gameplan registers its own (`sub_82BBA588` 0x82bba6b4..0x82bba700):
// `lua_pushcclosure(L, fn, 0)` then `lua_setfield(L, LUA_GLOBALSINDEX, name)` (0x82BCC600), fn a
// host function given a guest address by the thunk pool. A native reads its arguments where the
// Gameface natives do (`AKGameplan_fireEventOnPresentation` 0x82BC5C38): the frame's base at
// L+12, the top at L+8, 8-byte values {value, type}; a string (type 4) is a TString whose
// characters start at +16 (`v5 = *v3 + 16`), its length at +12; a number (type 3) is a 32-bit
// float (AKScriptVM_Create stores the time seed as one, 0x82bb7704). Results are pushed the way
// AKScriptVM_Create pushes its light userdata (0x82bb7598..0x82bb75ac): value, type, top += 8.
//
// WHICH STATE: the GUI's current presentation (`rex_lhGuiPresentationSet_GetCurrent` over
// GUI+56, as guest_tick_pump.cpp's RunGuestLua finds it). Before each chunk the natives are
// registered and the state asked whether it holds the mods' front-end runtime; one that does not
// (a fresh presentation) runs the host's composed chunk first (refii/mod_frontend.lua,
// Engine_ModLua: the front-end preamble and every mod's mod/frontend.lua).

PPC_EXTERN_FUNC(rex_lhCharString_CtorFromCStr);
PPC_EXTERN_FUNC(rex_lhCharString_Dtor);
PPC_EXTERN_FUNC(rex_lhGuiPresentationSet_GetCurrent);
PPC_EXTERN_FUNC(rex_lhSaveManager_CountSavedGamesAllDevices);

namespace {

using refii::mods::LoadF32;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

constexpr uint32_t kGuiObject = 0x83496B08u;
constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kPushClosure = 0x8227B968u;
constexpr uint32_t kSetField = 0x82BCC600u;
/// A guest function inside the game module, for the thunk pool (as mod_lua_natives.cpp).
constexpr uint32_t kModuleAnchor = 0x82451F18u;
constexpr int32_t kGlobalsIndex = -10002;
constexpr uint32_t kTypeNil = 0u;
constexpr uint32_t kTypeBoolean = 1u;
constexpr uint32_t kTypeNumber = 3u;
constexpr uint32_t kTypeString = 4u;

constexpr const char* kChunkKey = "refii/mod_frontend.lua";
constexpr const char* kProbe = "__refii_need_init(Refii == nil or Refii._frontend ~= true)";

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// Gameface's lua_pushlstring (TU1 0x8219CD00, disc 0x8219CDA8, the same 25 instructions: a GC
/// step when due, `bl luaS_newlstr` 0x8219cd40 — the Gameface interner 0x8227BBB8 — then the
/// TValue {TString, 4} at the top and `top += 8`, 0x8219cd44..58). The only way to put a string
/// on this state's stack: a string TValue must point at an interned TString.
constexpr uint32_t kPushLString = 0x8219CD00u;

/// A Gameface native's view of its frame (see above).
struct Gf {
  uint8_t* base;
  uint32_t L;
  /// The native's own context (the guest thread's): for guest calls a result needs.
  PPCContext* ctx;

  uint32_t Slot(int i) const {
    const uint32_t frame = LoadU32(base, L + 12u);
    const uint32_t top = LoadU32(base, L + 8u);
    const uint32_t at = frame + 8u * static_cast<uint32_t>(i - 1);
    return i >= 1 && Plausible(frame) && at + 8u <= top ? at : 0u;
  }

  uint32_t Type(int i) const {
    const uint32_t at = Slot(i);
    return at != 0u ? LoadU32(base, at + 4u) : kTypeNil;
  }

  double Number(int i) const {
    const uint32_t at = Slot(i);
    if (at == 0u) {
      return 0.0;
    }
    const uint32_t type = LoadU32(base, at + 4u);
    if (type == kTypeNumber) {
      return LoadF32(base, at);
    }
    return type == kTypeBoolean && LoadU32(base, at) != 0u ? 1.0 : 0.0;
  }

  int32_t Int(int i) const {
    const double v = Number(i);
    return std::isfinite(v) ? static_cast<int32_t>(v) : 0;
  }

  bool Truthy(int i) const {
    const uint32_t type = Type(i);
    return type != kTypeNil && !(type == kTypeBoolean && LoadU32(base, Slot(i)) == 0u);
  }

  void String(int i, char* out, size_t capacity) const {
    out[0] = 0;
    const uint32_t at = Slot(i);
    if (at == 0u) {
      return;
    }
    const uint32_t type = LoadU32(base, at + 4u);
    if (type == kTypeNumber) {
      std::snprintf(out, capacity, "%.14g", static_cast<double>(LoadF32(base, at)));
      return;
    }
    const uint32_t text = type == kTypeString ? LoadU32(base, at) : 0u;
    if (!Plausible(text)) {
      return;
    }
    size_t n = LoadU32(base, text + 12u);
    if (n >= capacity) {
      n = capacity - 1;
    }
    std::memcpy(out, base + text + 16u, n);
    out[n] = 0;
  }

  void Push(uint32_t value, uint32_t type) {
    const uint32_t top = LoadU32(base, L + 8u);
    StoreU32(base, top, value);
    StoreU32(base, top + 4u, type);
    StoreU32(base, L + 8u, top + 8u);
  }

  void PushNumber(double v) {
    const float f = static_cast<float>(v);
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof(bits));
    Push(bits, kTypeNumber);
  }

  void PushBoolean(bool b) { Push(b ? 1u : 0u, kTypeBoolean); }
  void PushNil() { Push(0u, kTypeNil); }

  /// A string result, interned by the state's own lua_pushlstring from a guest copy of `text`
  /// (freed after: the TString is the state's). Nil when no guest memory.
  void PushString(const char* text) {
    const size_t n = std::strlen(text);
    const uint32_t heap = refii::vfs::CallLhMalloc(*ctx, base, static_cast<uint32_t>(n) + 1u);
    if (!Plausible(heap)) {
      PushNil();
      return;
    }
    std::memcpy(base + heap, text, n + 1u);
    PPCContext work = refii::mods::Frame(*ctx, 0x200u);
    work.r3.u32 = L;
    work.r4.u32 = heap;
    work.r5.u32 = static_cast<uint32_t>(n);
    if (!refii::mods::CallGuest(work, base, refii::guest::Address(kPushLString))) {
      PushNil();
    }
    refii::vfs::CallLhFree(*ctx, base, heap);
  }
};

bool g_need_init = false;

#define REFII_FRONTEND_NATIVE(name)                           \
  void name##Body(Gf& lua, int32_t& results);                 \
  void name(PPCContext& ctx, uint8_t* base) {                 \
    Gf lua{base, ctx.r3.u32, &ctx};                           \
    int32_t results = 0;                                      \
    name##Body(lua, results);                                 \
    ctx.r3.u64 = static_cast<uint32_t>(results);              \
  }                                                           \
  void name##Body(Gf& lua, int32_t& results)

// __refii_need_init(b): the probe's answer.
REFII_FRONTEND_NATIVE(NativeNeedInit) {
  g_need_init = lua.Truthy(1);
  results = 0;
}

// Refii.publish(key, n): the value every reader sees as "mod:<key>" (refii::mods::Publish).
REFII_FRONTEND_NATIVE(NativePublish) {
  char key[128];
  lua.String(1, key, sizeof(key));
  if (key[0] != 0) {
    refii::mods::Publish(key, static_cast<float>(lua.Number(2)));
  }
  results = 0;
}

// Refii.value(key): a published value, or nil.
REFII_FRONTEND_NATIVE(NativeValue) {
  char key[128];
  lua.String(1, key, sizeof(key));
  float value = 0.0f;
  if (key[0] != 0 && refii::mods::Published(key, value)) {
    lua.PushNumber(value);
  } else {
    lua.PushNil();
  }
  results = 1;
}

REFII_FRONTEND_NATIVE(NativeLog) {
  char text[256];
  lua.String(1, text, sizeof(text));
  REXLOG_INFO("[mod lua, front end] {}", text);
  results = 0;
}

REFII_FRONTEND_NATIVE(NativeError) {
  char text[512];
  lua.String(1, text, sizeof(text));
  if (text[0] != 0) {
    refii_bridge_mod_error_post(text, std::strlen(text));
    REXLOG_WARN("[mod lua, front end] {}", text);
  }
  results = 0;
}

// Refii.open_menu(name [, p]): refii_mod_open_menu, which the front end's host list honours.
REFII_FRONTEND_NATIVE(NativeOpenMenu) {
  char name[96];
  lua.String(1, name, sizeof(name));
  lua.PushBoolean(name[0] != 0 && refii_mod_open_menu(lua.Int(2), name) != 0);
  results = 1;
}

// Refii._message_box(title, text, accept, cancel, question, p): refii_mod_message_box, on the
// front end's own dialog; the id mod_message_box carries, or nil.
REFII_FRONTEND_NATIVE(NativeMessageBox) {
  char title[128];
  char text[480];
  char accept[64];
  char cancel[64];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  lua.String(3, accept, sizeof(accept));
  lua.String(4, cancel, sizeof(cancel));
  const uint32_t id = refii_mod_message_box(lua.Int(6), title, text, accept, lua.Int(5) != 0 ? cancel : nullptr);
  if (id != 0u) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  results = 1;
}

// Refii.notice(title, text [, seconds [, p]]): refii_mod_notice — on the front end its own dialog
// until dismissed; the id mod_notice carries, or nil.
REFII_FRONTEND_NATIVE(NativeNotice) {
  char title[128];
  char text[480];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  const uint32_t id = refii_mod_notice(lua.Int(4), title, text, static_cast<float>(lua.Number(3)));
  if (id != 0u) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  results = 1;
}

// Refii._text(text): refii_mod_text, under the shared preamble's Refii.text — the host's text lookup
// for a bare key or each {KEY} in the text, or nil with no lookup installed.
REFII_FRONTEND_NATIVE(NativeText) {
  char text[480];
  lua.String(1, text, sizeof(text));
  char out[1024];
  if (text[0] != 0 && refii_mod_text(text, out, sizeof(out)) >= 0) {
    lua.PushString(out);
  } else {
    lua.PushNil();
  }
  results = 1;
}

// Refii._menu_box(title, text, p, cancelable, n, o1 .. o12): refii_mod_menu_box, under the shared
// preamble's Refii.menu_box(title, text, options [, p [, cancelable]]); the id mod_menu_box carries.
REFII_FRONTEND_NATIVE(NativeMenuBox) {
  constexpr int32_t kMax = 12;
  char title[128];
  char text[480];
  char options[kMax][96];
  const char* list[kMax];
  lua.String(1, title, sizeof(title));
  lua.String(2, text, sizeof(text));
  int32_t count = lua.Int(5);
  count = count < 0 ? 0 : (count > kMax ? kMax : count);
  for (int32_t i = 0; i < count; ++i) {
    lua.String(6 + i, options[i], sizeof(options[i]));
    list[i] = options[i];
  }
  const uint32_t id = refii_mod_menu_box(lua.Int(3), title, text, list, count, lua.Int(4) != 0 ? 1 : 0);
  if (id != 0u) {
    lua.PushNumber(id);
  } else {
    lua.PushNil();
  }
  results = 1;
}

// Refii.menu_player(): whose menus are open here (refii_mod_menu_player).
REFII_FRONTEND_NATIVE(NativeMenuPlayer) {
  lua.PushNumber(refii_mod_menu_player(lua.base));
  results = 1;
}

// Refii.hash(name): the game's FNV-1 of a name as a "0x…" string, as the game VM's native.
REFII_FRONTEND_NATIVE(NativeHash) {
  char text[256];
  lua.String(1, text, sizeof(text));
  char hex[16];
  std::snprintf(hex, sizeof(hex), "0x%08x", refii_mod_fnv1(text));
  lua.PushString(hex);
  results = 1;
}

// Refii.signed_in() -> the signed-in pad's user index, or nil (refii_mod_signed_in_user).
REFII_FRONTEND_NATIVE(NativeSignedIn) {
  const int32_t user = refii_mod_signed_in_user(lua.base);
  if (user >= 0) {
    lua.PushNumber(user);
  } else {
    lua.PushNil();
  }
  results = 1;
}

// Refii.save_count() -> the signed-in profile's saved games on every device, or nil with no
// front end (refii_mod_save_count: a content enumeration — ask once a screen).
REFII_FRONTEND_NATIVE(NativeSaveCount) {
  const int32_t count = refii_mod_save_count(lua.ctx, lua.base);
  if (count >= 0) {
    lua.PushNumber(count);
  } else {
    lua.PushNil();
  }
  results = 1;
}

// Refii.screen() -> the current presentation's name ("FrontendMainMenu", ...), or nil.
REFII_FRONTEND_NATIVE(NativeScreen) {
  char name[96];
  if (refii_mod_frontend_screen(lua.ctx, lua.base, name, sizeof(name)) != 0 && name[0] != 0) {
    lua.PushString(name);
  } else {
    lua.PushNil();
  }
  results = 1;
}

struct Native {
  const char* name;
  PPCFunc* function;
  uint32_t thunk;
};

Native g_natives[] = {
    {"__refii_need_init", &NativeNeedInit, 0},
    {"__refii_publish", &NativePublish, 0},
    {"__refii_value", &NativeValue, 0},
    {"__refii_log", &NativeLog, 0},
    {"__refii_error", &NativeError, 0},
    {"__refii_open_menu", &NativeOpenMenu, 0},
    {"__refii_message_box", &NativeMessageBox, 0},
    {"__refii_menu_player", &NativeMenuPlayer, 0},
    {"__refii_notice", &NativeNotice, 0},
    {"__refii_text", &NativeText, 0},
    {"__refii_menu_box", &NativeMenuBox, 0},
    {"__refii_hash", &NativeHash, 0},
    {"__refii_signed_in", &NativeSignedIn, 0},
    {"__refii_save_count", &NativeSaveCount, 0},
    {"__refii_screen", &NativeScreen, 0},
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
    if (native.thunk == 0u) {
      REXLOG_WARN("Refii front-end Lua: no guest address for {} -- not registered", native.name);
      ok = false;
    }
  }
  return ok;
}

/// The GUI's current presentation's script engine (its +4 is the lua_State), or 0.
uint32_t CurrentEngine(const PPCContext& ctx, uint8_t* base) {
  const uint32_t gui = LoadU32(base, refii::guest::Address(kGuiObject));
  const uint32_t stack = Plausible(gui) ? LoadU32(base, gui + 56u) : 0u;
  if (!Plausible(stack)) {
    return 0u;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  work.r3.u32 = stack;
  rex_lhGuiPresentationSet_GetCurrent(work, base);
  const uint32_t presentation = work.r3.u32;
  if (!Plausible(presentation)) {
    return 0u;
  }
  work = refii::mods::Frame(ctx, 0x200u);
  if (!refii::mods::CallVirtual(work, base, presentation, 5u)) {
    return 0u;
  }
  const uint32_t engine = work.r3.u32;
  return Plausible(engine) && Plausible(LoadU32(base, engine + 4u)) ? engine : 0u;
}

void Register(const PPCContext& ctx, uint8_t* base, uint32_t L) {
  const PPCContext frame = refii::mods::Frame(ctx, 0x200u);
  const uint32_t name = (frame.r1.u32 + 0x100u) & ~0xFu;
  for (const Native& native : g_natives) {
    PPCContext work = frame;
    work.r3.u32 = L;
    work.r4.u32 = native.thunk;
    work.r5.u64 = 0u;
    refii::mods::CallGuest(work, base, refii::guest::Address(kPushClosure));
    const size_t n = std::strlen(native.name);
    std::memcpy(base + name, native.name, n + 1u);
    work = frame;
    work.r3.u32 = L;
    work.r4.s64 = kGlobalsIndex;
    work.r5.u32 = name;
    refii::mods::CallGuest(work, base, refii::guest::Address(kSetField));
  }
}

/// `text` run protected in `engine`'s state (rex_lhGuiPresentation_RunLuaString).
bool RunChunk(PPCContext& ctx, uint8_t* base, uint32_t engine, const char* text, size_t length) {
  const uint32_t heap = refii::vfs::CallLhMalloc(ctx, base, static_cast<uint32_t>(length) + 1u);
  if (!Plausible(heap)) {
    return false;
  }
  std::memcpy(base + heap, text, length);
  base[heap + length] = 0;

  const PPCContext frame = refii::mods::Frame(ctx, 0x200u);
  const uint32_t handle = (frame.r1.u32 + 0x100u) & ~0xFu;
  StoreU32(base, handle, 0u);
  PPCContext work = frame;
  work.r3.u32 = handle;
  work.r4.u32 = heap;
  work.r5.u32 = 0xFFFFFFFFu;
  rex_lhCharString_CtorFromCStr(work, base);
  work = frame;
  work.r3.u32 = handle;
  work.r4.u32 = engine;
  __imp__rex_lhGuiPresentation_RunLuaString(work, base);
  work = frame;
  work.r3.u32 = handle;
  rex_lhCharString_Dtor(work, base);
  refii::vfs::CallLhFree(ctx, base, heap);
  return true;
}

}  // namespace

namespace refii::vfs {

/// Whether the game's own Lua VM is up (`*(*(*(0x83496AB8)+0xC)+0xAC)`, RunGameLua's state).
bool GameLuaUp(uint8_t* base) {
  const uint32_t root = LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadU32(base, root + 0xCu) : 0u;
  return Plausible(game) && Plausible(LoadU32(base, game + 0xACu));
}

/// Runs `chunk` in the front end's mod Lua: the current presentation's Gameface state, with the
/// Refii natives registered and the composed front-end chunk run first where it has not been.
/// False when there is no presentation with a script engine, or no composed chunk.
bool RunFrontEndLua(PPCContext& ctx, uint8_t* base, const std::string& chunk) {
  const uint32_t engine = CurrentEngine(ctx, base);
  if (engine == 0u || !AllocateThunks()) {
    return false;
  }
  const uint32_t L = LoadU32(base, engine + 4u);
  Register(ctx, base, L);

  g_need_init = false;
  RunChunk(ctx, base, engine, kProbe, std::strlen(kProbe));
  if (g_need_init) {
    std::vector<uint8_t> text;
    if (!ReadHostAsset(kChunkKey, text) || text.empty()) {
      REXLOG_INFO("Refii front-end Lua: no {} is composed (no mod enabled) -- '{}' not run", kChunkKey, chunk);
      return false;
    }
    RunChunk(ctx, base, engine, reinterpret_cast<const char*>(text.data()), text.size());
    REXLOG_INFO("Refii front-end Lua: {} ({} bytes) run in presentation state 0x{:08X}", kChunkKey,
                text.size(), L);
  }
  return RunChunk(ctx, base, engine, chunk.data(), chunk.size());
}

}  // namespace refii::vfs

extern "C" int32_t refii_mod_run_frontend_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  if (ctx == nullptr || chunk == nullptr) {
    return 0;
  }
  return refii::vfs::RunFrontEndLua(*ctx, base, chunk) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// THE FRONT END'S OWN STATE (core/refii_mod.h): the signed-in pad, the saves, the presentation.
// ---------------------------------------------------------------------------------------------

namespace {
constexpr uint32_t kFrontEndRoot = 0x83496AC0u;
constexpr uint32_t kSignedInUserIndex = 0x83496AF8u;
constexpr uint32_t kSignedInUserMask = 0x83496A70u;
}  // namespace

extern "C" int32_t refii_mod_signed_in_user(uint8_t* base) {
  if (base == nullptr) {
    return -1;
  }
  const uint32_t user = LoadU32(base, refii::guest::Address(kSignedInUserIndex));
  const uint32_t mask = LoadU32(base, refii::guest::Address(kSignedInUserMask));
  return user < 4u && ((mask >> user) & 1u) != 0u ? static_cast<int32_t>(user) : -1;
}

extern "C" int32_t refii_mod_save_count(PPCContext* ctx, uint8_t* base) {
  if (ctx == nullptr || base == nullptr) {
    return -1;
  }
  // The save manager the root screen's populate passes: *(*(0x83496AC0+12)+40) (0x826e2240..6c).
  const uint32_t app = LoadU32(base, refii::guest::Address(kFrontEndRoot));
  const uint32_t state = Plausible(app) ? LoadU32(base, app + 12u) : 0u;
  const uint32_t manager = Plausible(state) ? LoadU32(base, state + 40u) : 0u;
  if (!Plausible(manager)) {
    return -1;
  }
  if (refii_mod_signed_in_user(base) < 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x400u);
  work.r3.u32 = manager;
  rex_lhSaveManager_CountSavedGamesAllDevices(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_frontend_screen(PPCContext* ctx, uint8_t* base, char* out, uint32_t size) {
  if (out == nullptr || size == 0) {
    return 0;
  }
  out[0] = 0;
  if (ctx == nullptr || base == nullptr) {
    return 0;
  }
  const uint32_t gui = LoadU32(base, refii::guest::Address(kGuiObject));
  const uint32_t stack = Plausible(gui) ? LoadU32(base, gui + 56u) : 0u;
  if (!Plausible(stack)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x200u);
  work.r3.u32 = stack;
  rex_lhGuiPresentationSet_GetCurrent(work, base);
  const uint32_t presentation = work.r3.u32;
  // CPresentation+4 is its name, an AKString: `char*` at +0 (memory: the vtable is 0x82006D48;
  // SetScriptVM is `stw r4,0x1C(r3)`, so +4/+12 are the name and the path).
  const uint32_t text = Plausible(presentation) ? LoadU32(base, presentation + 4u) : 0u;
  if (!Plausible(text)) {
    return 0;
  }
  uint32_t n = 0;
  while (n + 1u < size && base[text + n] != 0 && n < 255u) {
    out[n] = static_cast<char>(base[text + n]);
    ++n;
  }
  out[n] = 0;
  return n != 0 ? 1 : 0;
}
