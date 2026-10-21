// Mods' own Lua, run in the GAME VM right after the shipped scripts.
//
// WHEN, from the binary (TU1; the disc build has the same two functions under the same names):
//   rex_lhLuaScriptSystem_LoadStartupScripts (0x82459280) runs every shipped script but the
//     cameras', once per game session -- Game_InitDatabases builds a fresh VM each session
//     (0x82310e30) and the session's end closes it (sub_823117A8), so a mod's patch is re-applied
//     every session by construction.
//   rex_lhLuaScriptSystem_RunCameraScripts (0x8245A610) runs the queued camera files, from the
//     CameraManager constructor (0x82481568) -- the first level of a session, a save load, a
//     co-op join -- and from the ReloadCameras native. The disc build's LoadStartupScripts also
//     runs camera files, so the camera chunk runs after both and must be idempotent.
//
// WHAT: the host composes every enabled mod's mod/init.lua and mod/camera.lua into one chunk each
// (Godot/src/Engine/mods/Engine_ModLua.cs) and serves them at refii/mod_init.lua and
// refii/mod_camera.lua. Read through the host asset reader rather than opened by the guest's own
// file loader: the loader opens through the VFS root provider, and a path no bank contains is not
// a path that provider is known to resolve.
//
// HOW: both functions take the script system in r3, whose first word is the game VM (`sub_82451E40(*v1,
// ...)` in each). The chunk is copied into a guest heap block (lh_malloc) -- it can be any size,
// unlike RunGameLua's stack copy -- and run with rex_lhLua_RunScriptBuffer(L, text, size, name, 0),
// the call RunGameLua makes.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

PPC_EXTERN_FUNC(__imp__rex_lhLuaScriptSystem_RunCameraScripts);
PPC_EXTERN_FUNC(__imp__rex_lhLuaScriptSystem_LoadScriptSave);
PPC_EXTERN_FUNC(rex_lhLua_RunScriptBuffer);

namespace refii::vfs {
/// The mod events LuaReady / SaveLoaded (hooks/mod_events.cpp).
void DispatchLuaEvent(PPCContext& ctx, uint8_t* base, bool save_loaded);
/// The Refii.* natives into the game VM (hooks/mod_lua_natives.cpp), before any mod's init runs.
void RegisterModLuaNatives(PPCContext& ctx, uint8_t* base, uint32_t state);
/// The script system the startup scripts last ran on, or 0 (refii_mod_spawn's binding).
uint32_t ModScriptSystem();
}  // namespace refii::vfs

namespace {

/// Refii's own Lua (co-op heroes: Godot/src/host/CoopHeroLua.cs), run before any mod's so a mod
/// patches on top of it. Mounted on every session, mods or not.
constexpr const char* kCoreKey = "refii/core_init.lua";
constexpr const char* kCoreCameraKey = "refii/core_camera.lua";
constexpr const char* kInitKey = "refii/mod_init.lua";
constexpr const char* kCameraKey = "refii/mod_camera.lua";

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The script system the startup scripts last ran on (r3 of LoadStartupScripts): what the game's
/// own script natives are bound to.
std::atomic<uint32_t> g_script_system{0u};

/// Runs the host's composed chunk at `key` in the VM `state`. False when there is none to run.
bool RunComposedChunk(PPCContext& ctx, uint8_t* base, uint32_t state, const char* key) {
  if (!Plausible(state)) {
    return false;
  }

  std::vector<uint8_t> text;
  if (!refii::vfs::ReadHostAsset(key, text) || text.empty()) {
    return false;
  }

  const uint32_t size = static_cast<uint32_t>(text.size());
  const uint32_t buffer = refii::vfs::CallLhMalloc(ctx, base, size + 1u);
  if (!Plausible(buffer)) {
    REXLOG_WARN("Refii mod Lua: no guest memory for {} ({} bytes) -- not run", key, size);
    return false;
  }

  std::memcpy(base + buffer, text.data(), size);
  base[buffer + size] = 0;

  // The chunk name, as a CCharString on our own frame below the caller's stack pointer.
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t name = frame + 0x20u;
  const uint32_t nameHandle = frame + 0x80u;
  const std::string label = std::string("=") + key;
  std::memcpy(base + name, label.c_str(), label.size() + 1u);
  refii::vfs::StoreGuestU32(base, nameHandle, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  work.r4.u32 = name;
  work.r5.u32 = 0xFFFFFFFFu;
  rex_lhCharString_CtorFromCStr(work, base);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = state;
  work.r4.u32 = buffer;
  work.r5.u32 = size;
  work.r6.u32 = nameHandle;
  work.r7.u32 = 0u;
  rex_lhLua_RunScriptBuffer(work, base);
  const bool ran = work.r3.u32 != 0u;

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  rex_lhCharString_Dtor(work, base);
  refii::vfs::CallLhFree(ctx, base, buffer);

  REXLOG_INFO("Refii mod Lua: {} ({} bytes) {}", key, size,
              ran ? "ran" : "FAILED to load or run -- see the Lua error above");
  return ran;
}

}  // namespace

bool refii::vfs::RunNewGameTransition(PPCContext& ctx, uint8_t* base) {
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t state = Plausible(game) ? LoadGuestU32(base, game + 0xACu) : 0u;
  const bool ran = RunComposedChunk(ctx, base, state, "refii/new_game.lua");
  refii_bridge_new_game_result(ran ? 1u : 0u);
  return ran;
}

REX_EXTERN(rex_lhLuaScriptSystem_LoadStartupScripts) {
  REX_FUNC_PROLOGUE();
  static std::atomic<bool> traced{false};
  const bool first = !traced.exchange(true, std::memory_order_relaxed);
  if (first) {
    REXLOG_INFO("Refii Game_InitDatabases ENTER rex_lhLuaScriptSystem_LoadStartupScripts");
  }

  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_LoadStartupScripts(ctx, base);

  if (first) {
    REXLOG_INFO("Refii Game_InitDatabases EXIT  rex_lhLuaScriptSystem_LoadStartupScripts");
  }

  const uint32_t system = entry.r3.u32;
  if (Plausible(system)) {
    g_script_system.store(system, std::memory_order_relaxed);
    PPCContext work = entry;
    const uint32_t state = refii::vfs::LoadGuestU32(base, system);
    refii::vfs::RegisterModLuaNatives(work, base, state);
    RunComposedChunk(work, base, state, kCoreKey);
    RunComposedChunk(work, base, state, kInitKey);
    RunComposedChunk(work, base, state, kCoreCameraKey);
    RunComposedChunk(work, base, state, kCameraKey);
  }
  refii::vfs::BumpLuaGeneration();
  if (Plausible(system)) {
    PPCContext work = entry;
    refii::vfs::DispatchLuaEvent(work, base, false);
  }
}

// A save's Lua state loaded into the VM: TU1 0x8245CC90 / disc 0x825B0EA8 unpersists the saved
// table and calls the global LoadScriptSave with it, always after LoadStartupScripts in the same
// session. Mods keeping state in the save (by wrapping GetSaveTable / LoadScriptSave in their
// init.lua) re-read it when refii_mod_lua_generation changes.
REX_EXTERN(rex_lhLuaScriptSystem_LoadScriptSave) {
  REX_FUNC_PROLOGUE();
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_LoadScriptSave(ctx, base);
  refii::vfs::BumpLuaGeneration();
  PPCContext work = entry;
  refii::vfs::DispatchLuaEvent(work, base, true);
}

REX_EXTERN(rex_lhLuaScriptSystem_RunCameraScripts) {
  REX_FUNC_PROLOGUE();
  const PPCContext entry = ctx;
  __imp__rex_lhLuaScriptSystem_RunCameraScripts(ctx, base);

  const uint32_t system = entry.r3.u32;
  if (Plausible(system)) {
    PPCContext work = entry;
    RunComposedChunk(work, base, refii::vfs::LoadGuestU32(base, system), kCoreCameraKey);
    RunComposedChunk(work, base, refii::vfs::LoadGuestU32(base, system), kCameraKey);
  }
}

uint32_t refii::vfs::ModScriptSystem() { return g_script_system.load(std::memory_order_relaxed); }
