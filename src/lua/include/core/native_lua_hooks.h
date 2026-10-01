#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

#include "core/lua_payload_bridge.h"
#include "core/payload_bridge_string.h"

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

// The ones the payload calls are inline wrappers over the C entries in
// core/lua_payload_bridge.h — see that header for why. Call sites are unchanged, and the
// wrappers inline away at -O2. The rest stay plain declarations: the payload does not
// call them.
inline bool NativeLuaScriptLoadTracingEnabled() {
  return refii_bridge_lua_native_script_load_tracing_enabled() != 0;
}
std::string ReadGuestCString(uint8_t* base, uint32_t guest_ptr, size_t max_bytes = 1024);

inline std::string ReadGuestLuaStringLike(uint8_t* base, uint32_t guest_ptr,
                                          size_t max_bytes = 1024) {
  return refii::bridge::TakeString([=](char* out, size_t capacity) {
    return refii_bridge_lua_read_guest_lua_string_like(base, guest_ptr, max_bytes, out,
                                                       capacity);
  });
}

std::string NormalizeLuaScriptKey(std::string key);

inline std::string ReadNativeLuaScriptKey(uint8_t* base, const NativeScriptRequest& request) {
  return refii::bridge::TakeString([&](char* out, size_t capacity) {
    return refii_bridge_lua_read_native_script_key(base, &request, out, capacity);
  });
}
}  // namespace refii::lua
