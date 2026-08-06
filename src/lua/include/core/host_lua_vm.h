#pragma once

#include <cstdint>
#include <string_view>

struct PPCContext;

namespace refii::lua {

struct NativeScriptRequest;
enum class NativeDispatchResult;

// Stage 2 of the Lua volume (docs/Refii_Lua_Volume_v0.1.md): execute a
// decompiled script's plaintext source in a host lua51 VM. Globals the chunk
// touches that the host does not define resolve to proxies that forward into
// the guest VM through GuestLuaBridge. Any marshal failure, parse failure, or
// guest call failure aborts host execution and the caller falls back to the
// next tier; the guest stack is restored on every exit path.
bool HostVmExecutionEnabled();
NativeDispatchResult TryExecuteScriptWithHostVm(PPCContext& ppc, uint8_t* base,
                                                const NativeScriptRequest& request,
                                                std::string_view script_key);

}  // namespace refii::lua
