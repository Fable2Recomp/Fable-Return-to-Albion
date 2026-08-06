#include "core/host_lua_vm.h"
#include "core/native_lua_bridge.h"
#include "core/native_lua_hooks.h"

#include <algorithm>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>

extern "C" {
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"
}

namespace refii::lua {
namespace {

REXCVAR_DEFINE_BOOL(lua_use_host_vm, false, "Lua",
                    "Execute eligible decompiled scripts in a host lua51 VM with guest proxies. "
                    "Requires lua_use_native_handlers.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(lua_host_vm_scripts,
                      "data/scripts/startup/startup.lua,data/scripts/startup/audio.lua", "Lua",
                      "Comma-separated script keys eligible for host-VM execution; '*' means all "
                      "workspace-resolvable scripts. Interim rollout control (see Lua volume 4.2).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(lua_trace_host_vm, false, "Lua",
                    "Log host-VM script executions, guest proxy calls, and fallbacks.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

constexpr const char* kGuestHandleMetatable = "refii.guest.value";

// Guest lua type tags (stock 5.1 numbering, verified against the guest build).
constexpr int kGuestTNil = 0;
constexpr int kGuestTBoolean = 1;
constexpr int kGuestTNumber = 3;
constexpr int kGuestTString = 4;
constexpr int kGuestTFunction = 6;

struct HostVmExecution {
  GuestLuaBridge& bridge;
  int guest_entry_top = 0;
  std::string_view script_key;
  bool trace = false;
};

struct GuestHandle {
  // Absolute guest stack index of a value parked for the lifetime of the
  // host chunk execution.
  int guest_index = 0;
};

bool TraceEnabled() { return REXCVAR_GET(lua_trace_host_vm); }

bool ScriptAllowedForHostVm(std::string_view script_key) {
  const std::string allowed = REXCVAR_GET(lua_host_vm_scripts);
  if (allowed == "*") {
    return true;
  }

  size_t position = 0;
  while (position <= allowed.size()) {
    size_t next = allowed.find(',', position);
    if (next == std::string::npos) {
      next = allowed.size();
    }
    std::string entry = allowed.substr(position, next - position);
    entry.erase(std::remove_if(entry.begin(), entry.end(),
                               [](unsigned char ch) { return std::isspace(ch); }),
                entry.end());
    if (!entry.empty() && NormalizeLuaScriptKey(std::move(entry)) == script_key) {
      return true;
    }
    position = next + 1;
  }
  return false;
}

HostVmExecution* GetExecution(lua_State* L) {
  return static_cast<HostVmExecution*>(lua_touserdata(L, lua_upvalueindex(1)));
}

GuestHandle* TestGuestHandle(lua_State* L, int index) {
  if (lua_type(L, index) != LUA_TUSERDATA || !lua_getmetatable(L, index)) {
    return nullptr;
  }
  luaL_getmetatable(L, kGuestHandleMetatable);
  const bool matches = lua_rawequal(L, -1, -2) != 0;
  lua_pop(L, 2);
  return matches ? static_cast<GuestHandle*>(lua_touserdata(L, index)) : nullptr;
}

void PushGuestHandle(lua_State* L, int guest_index) {
  auto* handle = static_cast<GuestHandle*>(lua_newuserdata(L, sizeof(GuestHandle)));
  handle->guest_index = guest_index;
  luaL_getmetatable(L, kGuestHandleMetatable);
  lua_setmetatable(L, -2);
}

// Marshals the guest stack value at absolute index `guest_index` onto the host
// stack. Opaque values become handles referring to the (still parked) slot.
void PushGuestValueToHost(lua_State* L, HostVmExecution& execution, int guest_index,
                          int guest_type) {
  GuestLuaBridge& guest = execution.bridge;
  switch (guest_type) {
    case kGuestTNil:
      lua_pushnil(L);
      return;
    case kGuestTBoolean: {
      bool value = false;
      guest.ReadBoolean(guest_index, value);
      lua_pushboolean(L, value ? 1 : 0);
      return;
    }
    case kGuestTNumber: {
      double value = 0.0;
      guest.ReadNumber(guest_index, value);
      lua_pushnumber(L, value);
      return;
    }
    case kGuestTString: {
      std::string value;
      guest.ReadString(guest_index, value);
      lua_pushlstring(L, value.data(), value.size());
      return;
    }
    default:
      PushGuestHandle(L, guest_index);
      return;
  }
}

// A host closure standing in for a guest global function. Upvalues: the
// execution pointer and the guest global name.
int GuestProxyCall(lua_State* L) {
  auto* execution = GetExecution(L);
  const char* name = lua_tostring(L, lua_upvalueindex(2));
  if (execution == nullptr || name == nullptr) {
    return luaL_error(L, "guest proxy invoked without execution context");
  }

  GuestLuaBridge& guest = execution->bridge;
  guest.ResetScratch();
  const int nargs = lua_gettop(L);
  const int pre_call_top = guest.GetTop();

  if (!guest.PushGlobal(name)) {
    return luaL_error(L, "guest global %s is unavailable", name);
  }

  for (int i = 1; i <= nargs; ++i) {
    switch (lua_type(L, i)) {
      case LUA_TNIL:
        guest.PushNil();
        break;
      case LUA_TBOOLEAN:
        guest.PushBoolean(lua_toboolean(L, i) != 0);
        break;
      case LUA_TNUMBER:
        guest.PushNumber(lua_tonumber(L, i));
        break;
      case LUA_TSTRING: {
        size_t length = 0;
        const char* text = lua_tolstring(L, i, &length);
        if (!guest.PushString(std::string_view(text, length))) {
          guest.RestoreTop(pre_call_top);
          return luaL_error(L, "guest scratch exhausted while calling %s", name);
        }
        break;
      }
      default: {
        if (GuestHandle* handle = TestGuestHandle(L, i)) {
          guest.PushValueAt(handle->guest_index);
          break;
        }
        guest.RestoreTop(pre_call_top);
        return luaL_error(L, "argument %d of %s is not marshallable to the guest VM", i, name);
      }
    }
  }

  if (!guest.PCall(nargs, /*nresults=*/-1)) {
    std::string error;
    execution->bridge.DescribeValue(-1, error);
    guest.RestoreTop(pre_call_top);
    return luaL_error(L, "guest call %s failed: %s", name, error.c_str());
  }

  const int result_count = guest.GetTop() - pre_call_top;
  bool any_opaque = false;
  for (int r = 1; r <= result_count; ++r) {
    int guest_type = kGuestTNil;
    execution->bridge.ReadValueType(pre_call_top + r, guest_type);
    if (guest_type != kGuestTNil && guest_type != kGuestTBoolean &&
        guest_type != kGuestTNumber && guest_type != kGuestTString) {
      any_opaque = true;
    }
  }

  for (int r = 1; r <= result_count; ++r) {
    const int guest_index = pre_call_top + r;
    int guest_type = kGuestTNil;
    execution->bridge.ReadValueType(guest_index, guest_type);
    PushGuestValueToHost(L, *execution, guest_index, guest_type);
  }

  // Scalars can be dropped from the guest stack; opaque results must stay
  // parked at stable absolute indices until the chunk finishes.
  if (!any_opaque) {
    guest.RestoreTop(pre_call_top);
  }

  if (execution->trace) {
    REXLOG_INFO("Host Lua VM proxied {}({} args) -> {} results{}", name, nargs, result_count,
                any_opaque ? " [parked]" : "");
  }
  return result_count;
}

// _G __index fallback: resolve unknown globals from the guest VM. Scalars are
// copied by value, functions become cached proxies, everything else becomes a
// parked handle.
int GlobalIndex(lua_State* L) {
  auto* execution = GetExecution(L);
  if (execution == nullptr || lua_type(L, 2) != LUA_TSTRING) {
    lua_pushnil(L);
    return 1;
  }

  const char* name = lua_tostring(L, 2);
  GuestLuaBridge& guest = execution->bridge;
  guest.ResetScratch();
  const int pre_top = guest.GetTop();
  if (!guest.PushGlobal(name)) {
    lua_pushnil(L);
    return 1;
  }

  int guest_type = kGuestTNil;
  execution->bridge.ReadValueType(-1, guest_type);

  if (guest_type == kGuestTFunction) {
    guest.RestoreTop(pre_top);
    lua_pushlightuserdata(L, execution);
    lua_pushvalue(L, 2);
    lua_pushcclosure(L, &GuestProxyCall, 2);
    // Cache the proxy so later lookups skip __index; chunk-scoped state.
    lua_pushvalue(L, 2);
    lua_pushvalue(L, -2);
    lua_rawset(L, 1);
    return 1;
  }

  if (guest_type == kGuestTNil || guest_type == kGuestTBoolean ||
      guest_type == kGuestTNumber || guest_type == kGuestTString) {
    PushGuestValueToHost(L, *execution, pre_top + 1, guest_type);
    guest.RestoreTop(pre_top);
    return 1;
  }

  // Table/userdata/thread global: park it and hand out a handle. Field access
  // through the handle is not supported yet (Stage 2 increment 1 covers flat
  // global calls); it raises, which aborts host execution into fallback.
  PushGuestValueToHost(L, *execution, pre_top + 1, guest_type);
  lua_pushvalue(L, 2);
  lua_pushvalue(L, -2);
  lua_rawset(L, 1);
  return 1;
}

int GuestHandleIndex(lua_State* L) {
  return luaL_error(L, "field access on a guest value is not supported by the host VM yet");
}

void SetupHostState(lua_State* L, HostVmExecution& execution) {
  luaL_openlibs(L);

  luaL_newmetatable(L, kGuestHandleMetatable);
  lua_pushcfunction(L, &GuestHandleIndex);
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);

  lua_pushvalue(L, LUA_GLOBALSINDEX);
  lua_newtable(L);
  lua_pushlightuserdata(L, &execution);
  lua_pushcclosure(L, &GlobalIndex, 1);
  lua_setfield(L, -2, "__index");
  lua_setmetatable(L, -2);
  lua_pop(L, 1);
}

bool ReadHostFileBytes(const std::filesystem::path& path, std::string& out) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) {
    return false;
  }
  stream.seekg(0, std::ios::end);
  const std::streampos size = stream.tellg();
  if (size < 0) {
    return false;
  }
  stream.seekg(0, std::ios::beg);
  out.resize(static_cast<size_t>(size));
  stream.read(out.data(), static_cast<std::streamsize>(out.size()));
  return static_cast<bool>(stream) || stream.eof();
}

}  // namespace

bool HostVmExecutionEnabled() { return REXCVAR_GET(lua_use_host_vm); }

NativeDispatchResult TryExecuteScriptWithHostVm(PPCContext& ppc, uint8_t* base,
                                                const NativeScriptRequest& request,
                                                std::string_view script_key) {
  if (!HostVmExecutionEnabled() || !ScriptAllowedForHostVm(script_key)) {
    return NativeDispatchResult::kUnhandled;
  }

  const std::filesystem::path source_path = ResolveLuaModOverridePath(script_key);
  if (source_path.empty()) {
    if (TraceEnabled()) {
      REXLOG_INFO("Host Lua VM has no source for {}; falling back", script_key);
    }
    return NativeDispatchResult::kUnhandled;
  }

  std::string source;
  if (!ReadHostFileBytes(source_path, source) || source.empty() || source.front() == '\x1b') {
    // Host execution is source-only; bytecode stays on the guest path.
    if (TraceEnabled()) {
      REXLOG_INFO("Host Lua VM skipping {} (unreadable or bytecode)", script_key);
    }
    return NativeDispatchResult::kUnhandled;
  }

  GuestLuaBridge bridge(ppc, base, request.lua_state_guest);
  HostVmExecution execution{
      .bridge = bridge,
      .guest_entry_top = bridge.GetTop(),
      .script_key = script_key,
      .trace = TraceEnabled(),
  };

  lua_State* L = luaL_newstate();
  if (L == nullptr) {
    return NativeDispatchResult::kUnhandled;
  }

  SetupHostState(L, execution);

  const std::string chunk_name = "@" + std::string(script_key);
  bool succeeded = false;
  if (luaL_loadbuffer(L, source.data(), source.size(), chunk_name.c_str()) == 0) {
    if (lua_pcall(L, 0, 0, 0) == 0) {
      succeeded = true;
    } else if (execution.trace) {
      const char* error = lua_tostring(L, -1);
      REXLOG_INFO("Host Lua VM execution of {} failed: {}", script_key,
                  error != nullptr ? error : "(no message)");
    }
  } else if (execution.trace) {
    const char* error = lua_tostring(L, -1);
    REXLOG_INFO("Host Lua VM could not load {}: {}", script_key,
                error != nullptr ? error : "(no message)");
  }

  lua_close(L);
  bridge.RestoreTop(execution.guest_entry_top);

  if (succeeded) {
    if (execution.trace) {
      REXLOG_INFO("Host Lua VM executed {} natively", script_key);
    }
    return NativeDispatchResult::kHandledSuccess;
  }

  // Failure falls back to the next tier (native handler, then the original
  // guest loader) per the Lua volume invariants; never kHandledFailure here.
  return NativeDispatchResult::kUnhandled;
}

}  // namespace refii::lua
