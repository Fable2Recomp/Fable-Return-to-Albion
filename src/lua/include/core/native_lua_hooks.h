#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::lua {

struct NativeScriptRequest {
  uint32_t lua_state_guest = 0;
  // sub_82451F18 receives r4=script_buffer_guest and r5=script_size.
  uint32_t script_buffer_guest = 0;
  uint32_t script_size = 0;
  uint32_t source_guest = 0;
  uint32_t chunk_name_guest = 0;
};

struct NativeScriptExecution {
  PPCContext& ppc;
  uint8_t* base = nullptr;
  NativeScriptRequest request{};
  std::string_view script_key;
};

using NativeScriptHandler = bool (*)(const NativeScriptExecution&);

enum class NativeDispatchResult {
  kUnhandled,
  kHandledSuccess,
  kHandledFailure,
};

bool NativeLuaHooksEnabled();
bool NativeLuaScriptLoadTracingEnabled();
bool NativeLuaForceLooseBanksEnabled();
bool NativeLuaModOverridesEnabled();
bool NativeLuaModOverrideTracingEnabled();
std::string ReadGuestCString(uint8_t* base, uint32_t guest_ptr, size_t max_bytes = 1024);
std::string ReadGuestLuaStringLike(uint8_t* base, uint32_t guest_ptr, size_t max_bytes = 1024);
std::string NormalizeLuaScriptKey(std::string key);
std::string ReadNativeLuaScriptKey(uint8_t* base, const NativeScriptRequest& request);
std::filesystem::path ResolveLuaModOverridePath(std::string_view script_key);
void RegisterNativeScriptHandler(std::string_view script_key, NativeScriptHandler handler);
NativeDispatchResult DispatchNativeScript(PPCContext& ppc, uint8_t* base,
                                          const NativeScriptRequest& request);

// Anchor for the native Lua hook translation unit. Strong PPC overrides live in
// src/lua/src/core/native_lua_hooks.cpp and dispatch into the runtime helpers
// in src/lua/src/core/native_lua_runtime.cpp.
void TouchNativeLuaHooks();

}  // namespace refii::lua
