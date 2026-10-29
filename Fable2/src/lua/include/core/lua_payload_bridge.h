// refii — the payload/host C ABI for the native Lua hooks.
//
// Same contract as Fable2/src/vfs/include/core/payload_bridge.h, kept separate because librefii_lua
// and librefii_vfs are separate libraries: C linkage, explicit-width scalars, strings into a
// caller buffer, and no ownership crossing. The hooks that call these compile into the
// payload, which is built on the player's machine by a compiler whose C++ ABI is not ours.
//
// NativeScriptRequest crosses BY POINTER rather than being flattened: it is five uint32_t
// and nothing else, so its layout is already the C layout. Adding a non-POD member to it
// would break that silently — flatten it here if that ever happens.

// C LINKAGE, BUT A C++ HEADER. The entries carry C linkage so no name mangling crosses;
// the declarations still name a namespaced POD, which only C++ can spell. Nothing includes
// this from C, and the ABI is unaffected either way.

#pragma once

#include <stddef.h>
#include <stdint.h>

namespace refii::lua {
struct NativeScriptRequest;
}

extern "C" {
int32_t refii_bridge_lua_native_script_load_tracing_enabled(void);


size_t refii_bridge_lua_read_guest_lua_string_like(uint8_t* base, uint32_t guest_ptr,
                                                   size_t max_bytes, char* out, size_t capacity);
size_t refii_bridge_lua_read_native_script_key(
    uint8_t* base, const refii::lua::NativeScriptRequest* request, char* out,
    size_t capacity);
}  // extern "C"
