#include "core/native_lua_bridge.h"
#include "core/native_lua_hooks.h"

#include <array>
#include <string>
#include <string_view>

#include <rex/logging.h>

namespace refii::lua {
namespace {

struct AudioBankSpec {
  std::string_view adb;
  std::string_view bnk;
  bool mount = false;
  bool preload = false;
};

constexpr std::array kAudioBanks{
    AudioBankSpec{"audio/globals.adb", "", false, false},
    AudioBankSpec{"language/speechdoc/Speech.adb", "language/speech/Speech.bnk", false, false},
    AudioBankSpec{"audio/music.adb", "audio/music.bnk", false, false},
    AudioBankSpec{"audio/music_3d.adb", "audio/music_3d.bnk", false, true},
    AudioBankSpec{"audio/expressions.adb", "audio/expressions.bnk", false, true},
    AudioBankSpec{"audio/pub_games.adb", "audio/pub_games.bnk", false, true},
    AudioBankSpec{"audio/crescendo_hits.adb", "audio/crescendo_hits.bnk", true, true},
    AudioBankSpec{"audio/crescendo_track_01.adb", "audio/crescendo_track_01.bnk", false, true},
    AudioBankSpec{"audio/crescendo_track_02.adb", "audio/crescendo_track_02.bnk", false, true},
    AudioBankSpec{"audio/crescendo_track_03.adb", "audio/crescendo_track_03.bnk", false, true},
    AudioBankSpec{"audio/crescendo_track_04.adb", "audio/crescendo_track_04.bnk", false, true},
    AudioBankSpec{"audio/crescendo_track_05.adb", "audio/crescendo_track_05.bnk", false, true},
    AudioBankSpec{"audio/music_load_level.adb", "", false, true},
    AudioBankSpec{"audio/music_load_level_02.adb", "", false, true},
    AudioBankSpec{"audio/music_load_level_03.adb", "", false, true},
    AudioBankSpec{"audio/music_load_level_04.adb", "", false, true},
    AudioBankSpec{"audio/music_load_level_05.adb", "", false, true},
    AudioBankSpec{"audio/miscellaneous.adb", "audio/miscellaneous.bnk", true, true},
    AudioBankSpec{"audio/hero_vocalisations.adb", "audio/hero_vocalisations.bnk", true, true},
    AudioBankSpec{"audio/gui.adb", "audio/gui.bnk", true, false},
    AudioBankSpec{"audio/dog.adb", "audio/dog.bnk", true, true},
    AudioBankSpec{"audio/combat_and_weapons.adb", "audio/combat_and_weapons.bnk", true, true},
    AudioBankSpec{"audio/collisions.adb", "audio/collisions.bnk", true, true},
    AudioBankSpec{"audio/footsteps.adb", "audio/footsteps.bnk", true, true},
    AudioBankSpec{"audio/doors_and_windows.adb", "audio/doors_and_windows.bnk", true, true},
    AudioBankSpec{"audio/animals.adb", "audio/animals.bnk", true, true},
    AudioBankSpec{"audio/swimming.adb", "audio/swimming.bnk", true, true},
    AudioBankSpec{"audio/spells.adb", "audio/spells.bnk", true, true},
    AudioBankSpec{"audio/particle_fx.adb", "audio/particle_fx.bnk", true, true},
    AudioBankSpec{"audio/creature_hobbe.adb", "audio/creature_hobbe.bnk", true, true},
    AudioBankSpec{"audio/creature_beetle.adb", "audio/creature_beetle.bnk", true, true},
    AudioBankSpec{"audio/luciens_soldier.adb", "audio/luciens_soldier.bnk", true, true},
    AudioBankSpec{"audio/creature_balverine.adb", "audio/creature_balverine.bnk", true, true},
    AudioBankSpec{"audio/creature_hollowman.adb", "audio/creature_hollowman.bnk", true, true},
    AudioBankSpec{"audio/highwayman.adb", "audio/highwayman.bnk", true, true},
    AudioBankSpec{"audio/luciens_guard.adb", "audio/luciens_guard.bnk", true, true},
    AudioBankSpec{"audio/creature_shadows.adb", "audio/creature_shadows.bnk", true, true},
    AudioBankSpec{"audio/bandit_vocalisations.adb", "audio/bandit_vocalisations.bnk", true, true},
};

bool LogCallFailure(GuestLuaBridge& lua, std::string_view call_name) {
  std::string error;
  if (lua.DescribeValue(-1, error) && !error.empty()) {
    REXLOG_WARN("Native Lua call {} failed: {}", call_name, error);
  } else {
    REXLOG_WARN("Native Lua call {} failed", call_name);
  }
  return false;
}

bool PushGlobalOrLog(GuestLuaBridge& lua, std::string_view name) {
  if (lua.PushGlobal(name)) {
    return true;
  }

  REXLOG_WARN("Native Lua handler could not resolve global {}", name);
  return false;
}

bool CallGlobalString(GuestLuaBridge& lua, std::string_view name, std::string_view value) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, name) || !lua.PushString(value)) {
    lua.RestoreTop(original_top);
    return false;
  }

  if (!lua.PCall(1, 0)) {
    const bool logged = LogCallFailure(lua, name);
    lua.RestoreTop(original_top);
    return logged;
  }

  lua.RestoreTop(original_top);
  return true;
}

bool CallGlobalBoolean(GuestLuaBridge& lua, std::string_view name, bool value) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, name)) {
    lua.RestoreTop(original_top);
    return false;
  }

  lua.PushBoolean(value);
  if (!lua.PCall(1, 0)) {
    const bool logged = LogCallFailure(lua, name);
    lua.RestoreTop(original_top);
    return logged;
  }

  lua.RestoreTop(original_top);
  return true;
}

bool CallGlobalNoArgsString(GuestLuaBridge& lua, std::string_view name, std::string& out) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, name)) {
    lua.RestoreTop(original_top);
    return false;
  }

  if (!lua.PCall(0, 1)) {
    const bool logged = LogCallFailure(lua, name);
    lua.RestoreTop(original_top);
    return logged;
  }

  const bool ok = lua.ReadString(-1, out);
  if (!ok) {
    REXLOG_WARN("Native Lua call {} returned a non-string result", name);
  }
  lua.RestoreTop(original_top);
  return ok;
}

bool CallGlobalNoArgsBoolean(GuestLuaBridge& lua, std::string_view name, bool& out) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, name)) {
    lua.RestoreTop(original_top);
    return false;
  }

  if (!lua.PCall(0, 1)) {
    const bool logged = LogCallFailure(lua, name);
    lua.RestoreTop(original_top);
    return logged;
  }

  const bool ok = lua.ReadBoolean(-1, out);
  if (!ok) {
    REXLOG_WARN("Native Lua call {} returned an unreadable boolean result", name);
  }
  lua.RestoreTop(original_top);
  return ok;
}

bool CallSetScreenResolutionCurrent(GuestLuaBridge& lua) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, "SetScreenResolution") || !PushGlobalOrLog(lua, "GetScreenResolution")) {
    lua.RestoreTop(original_top);
    return false;
  }

  if (!lua.PCall(0, 1)) {
    const bool logged = LogCallFailure(lua, "GetScreenResolution");
    lua.RestoreTop(original_top);
    return logged;
  }

  if (!lua.PCall(1, 0)) {
    const bool logged = LogCallFailure(lua, "SetScreenResolution");
    lua.RestoreTop(original_top);
    return logged;
  }

  lua.RestoreTop(original_top);
  return true;
}

bool CallSetScreenResolutionVector(GuestLuaBridge& lua, int width, int height) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, "SetScreenResolution") || !PushGlobalOrLog(lua, "CI32Vector2")) {
    lua.RestoreTop(original_top);
    return false;
  }

  lua.PushInteger(width);
  lua.PushInteger(height);
  if (!lua.PCall(2, 1)) {
    const bool logged = LogCallFailure(lua, "CI32Vector2");
    lua.RestoreTop(original_top);
    return logged;
  }

  if (!lua.PCall(1, 0)) {
    const bool logged = LogCallFailure(lua, "SetScreenResolution");
    lua.RestoreTop(original_top);
    return logged;
  }

  lua.RestoreTop(original_top);
  return true;
}

bool CallAddAudioBank(GuestLuaBridge& lua, const AudioBankSpec& bank) {
  const int original_top = lua.GetTop();
  if (!PushGlobalOrLog(lua, "AddAudioBank") || !lua.PushString(bank.adb) ||
      !lua.PushString(bank.bnk)) {
    lua.RestoreTop(original_top);
    return false;
  }

  lua.PushBoolean(bank.mount);
  lua.PushBoolean(bank.preload);
  if (!lua.PCall(4, 0)) {
    const bool logged = LogCallFailure(lua, "AddAudioBank");
    lua.RestoreTop(original_top);
    return logged;
  }

  lua.RestoreTop(original_top);
  return true;
}

bool HandleStartupScript(const NativeScriptExecution& execution) {
  GuestLuaBridge lua(execution.ppc, execution.base, execution.request.lua_state_guest);

  std::string application_name;
  if (!CallGlobalNoArgsString(lua, "GetApplicationName", application_name)) {
    return false;
  }

  if (application_name == "Fable 2") {
    if (!CallGlobalString(lua, "SetApplicationName",
                          application_name + " - the one where Lua works")) {
      return false;
    }
    if (!CallSetScreenResolutionCurrent(lua)) {
      return false;
    }
  } else if (application_name == "Big res") {
    if (!CallSetScreenResolutionVector(lua, 1024, 768)) {
      return false;
    }
  } else {
    if (!CallGlobalString(lua, "SetApplicationName", "Fable -2")) {
      return false;
    }
    if (!CallSetScreenResolutionVector(lua, 640, 480)) {
      return false;
    }
  }

  if (!CallGlobalString(lua, "AddOptionalStartupScript", "audio.lua")) {
    return false;
  }

  // The original startup bytecode checks Win32/Xbox360 here, but both branches
  // immediately converge without side effects.
  if (!CallGlobalString(lua, "SetInitialWorldName", "TestWorld")) {
    return false;
  }
  if (!CallGlobalString(lua, "SetInitialLevelName", "TestLevel1")) {
    return false;
  }
  if (!CallGlobalString(lua, "SetInitialHeroMeleeWeapon",
                        "ObjectInventory_L_LongSword_Rusty")) {
    return false;
  }
  if (!CallGlobalString(lua, "SetInitialHeroRangedWeapon",
                        "ObjectInventoryPistol_Flintlock_Rusty")) {
    return false;
  }
  if (!CallGlobalString(lua, "SetInitialHenchmanMeleeWeapon",
                        "ObjectInventory_L_LongSword_Rusty")) {
    return false;
  }
  if (!CallGlobalString(lua, "SetInitialHenchmanRangedWeapon",
                        "ObjectInventoryPistol_Flintlock_Rusty")) {
    return false;
  }

  bool is_release_build = false;
  if (!CallGlobalNoArgsBoolean(lua, "IsReleaseBuild", is_release_build)) {
    return false;
  }

  const bool should_force_loose_banks =
      !is_release_build || NativeLuaForceLooseBanksEnabled();
  if (should_force_loose_banks &&
      !CallGlobalBoolean(lua, "SetUseMemoryMountedBanks", false)) {
    return false;
  }

  return CallGlobalString(lua, "AddOptionalStartupScript", "MyStartup.lua");
}

bool HandleAudioScript(const NativeScriptExecution& execution) {
  GuestLuaBridge lua(execution.ppc, execution.base, execution.request.lua_state_guest);
  for (const auto& bank : kAudioBanks) {
    if (!CallAddAudioBank(lua, bank)) {
      return false;
    }
  }
  return true;
}

}  // namespace

void RegisterBuiltinNativeScriptHandlers() {
  RegisterNativeScriptHandler("startup.lua", &HandleStartupScript);
  RegisterNativeScriptHandler("audio.lua", &HandleAudioScript);
  RegisterNativeScriptHandler("data/scripts/startup/startup.lua", &HandleStartupScript);
  RegisterNativeScriptHandler("data/scripts/startup/audio.lua", &HandleAudioScript);
}

}  // namespace refii::lua
