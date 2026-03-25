#include "core/native_lua_hooks.h"

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/runtime.h>

PPC_EXTERN_FUNC(__imp__sub_82451E40);
PPC_EXTERN_FUNC(__imp__sub_82451F18);
PPC_EXTERN_FUNC(sub_82451F18);

namespace refii::lua {
namespace {

thread_local int g_lua_mod_override_dispatch_depth = 0;
constexpr std::string_view kRefiiLuaDisassemblyPlaceholder = "-- refii-disassembly-placeholder";

struct ScopedLuaModOverrideDispatch {
  ScopedLuaModOverrideDispatch() { ++g_lua_mod_override_dispatch_depth; }

  ~ScopedLuaModOverrideDispatch() { --g_lua_mod_override_dispatch_depth; }
};

bool IsRunningLuaModOverrideDispatch() { return g_lua_mod_override_dispatch_depth > 0; }

bool LooksLikeScriptReference(std::string_view value) {
  return value.find(".lua") != std::string_view::npos ||
         value.find("scripts") != std::string_view::npos ||
         value.find("game:\\") != std::string_view::npos;
}

void TraceScriptLoad(uint8_t* base, uint32_t source_guest, uint32_t chunk_name_guest) {
  if (!NativeLuaScriptLoadTracingEnabled()) {
    return;
  }

  const std::string source = ReadGuestLuaStringLike(base, source_guest);
  const std::string chunk_name = ReadGuestLuaStringLike(base, chunk_name_guest);
  if (!LooksLikeScriptReference(source) && !LooksLikeScriptReference(chunk_name)) {
    return;
  }

  REXLOG_INFO(
      "Lua script load source='{}' guest_chunk_label='{}' source_guest=0x{:08X} "
      "chunk_guest=0x{:08X}",
      source.empty() ? "<null>" : source, chunk_name.empty() ? "<null>" : chunk_name,
      source_guest, chunk_name_guest);
}

bool ReadHostFileBytes(const std::filesystem::path& path, std::vector<uint8_t>& out_bytes) {
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
  out_bytes.resize(static_cast<size_t>(size));
  if (!out_bytes.empty()) {
    stream.read(reinterpret_cast<char*>(out_bytes.data()), static_cast<std::streamsize>(out_bytes.size()));
  }

  return stream.good() || stream.eof();
}

bool IsRefiiLuaDisassemblyPlaceholder(const std::vector<uint8_t>& bytes) {
  if (bytes.size() < kRefiiLuaDisassemblyPlaceholder.size()) {
    return false;
  }

  return std::string_view(reinterpret_cast<const char*>(bytes.data()),
                          kRefiiLuaDisassemblyPlaceholder.size()) ==
         kRefiiLuaDisassemblyPlaceholder;
}

bool LooksLikeLuaBytecode(const std::vector<uint8_t>& bytes) {
  return bytes.size() >= 4 && bytes[0] == 0x1B && bytes[1] == 'L' && bytes[2] == 'u' &&
         bytes[3] == 'a';
}

bool RunLuaModOverride(PPCContext& ctx, uint8_t* base, const NativeScriptRequest& request,
                       const std::string& script_key, const std::filesystem::path& override_path) {
  auto* runtime = rex::Runtime::instance();
  if (runtime == nullptr || runtime->memory() == nullptr) {
    REXLOG_WARN("Lua mod override matched {} at {}, but the runtime memory system is unavailable",
                script_key, override_path.string());
    return false;
  }

  std::vector<uint8_t> script_bytes;
  if (!ReadHostFileBytes(override_path, script_bytes)) {
    REXLOG_WARN("Lua mod override matched {} at {}, but the file could not be read", script_key,
                override_path.string());
    return false;
  }

  if (IsRefiiLuaDisassemblyPlaceholder(script_bytes)) {
    if (NativeLuaModOverrideTracingEnabled()) {
      REXLOG_INFO("Lua mod override placeholder at {} is documentation-only and will be ignored",
                  override_path.string());
    }
    return false;
  }

  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO("Lua mod override staging {} bytes as {} from {}", script_bytes.size(),
                LooksLikeLuaBytecode(script_bytes) ? "bytecode" : "source",
                override_path.string());
  }

  if (script_bytes.size() > static_cast<size_t>(std::numeric_limits<uint32_t>::max())) {
    REXLOG_WARN("Lua mod override matched {} at {}, but the file is too large to stage into "
                "guest memory",
                script_key, override_path.string());
    return false;
  }

  const uint32_t script_size = static_cast<uint32_t>(script_bytes.size());
  const uint32_t guest_buffer =
      runtime->memory()->SystemHeapAlloc(std::max<uint32_t>(1, script_size));
  if (guest_buffer == 0) {
    REXLOG_WARN("Lua mod override matched {} at {}, but guest staging allocation failed", script_key,
                override_path.string());
    return false;
  }

  struct SystemHeapAllocation {
    rex::memory::Memory* memory = nullptr;
    uint32_t guest_address = 0;

    ~SystemHeapAllocation() {
      if (memory != nullptr && guest_address != 0) {
        memory->SystemHeapFree(guest_address);
      }
    }
  } allocation{runtime->memory(), guest_buffer};

  uint8_t* guest_bytes = runtime->memory()->TranslateVirtual<uint8_t*>(guest_buffer);
  if (guest_bytes == nullptr) {
    REXLOG_WARN("Lua mod override matched {} at {}, but guest staging translation failed",
                script_key, override_path.string());
    return false;
  }

  if (!script_bytes.empty()) {
    std::memcpy(guest_bytes, script_bytes.data(), script_bytes.size());
  }

  PPCContext execution_ctx = ctx;
  execution_ctx.r3.u32 = request.lua_state_guest;
  execution_ctx.r4.u32 = guest_buffer;
  execution_ctx.r5.u32 = script_size;
  execution_ctx.r6.u32 = request.source_guest;
  execution_ctx.r7.u32 = request.chunk_name_guest;
  const ScopedLuaModOverrideDispatch override_dispatch_scope;
  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO("Lua mod override invoking sub_82451F18 with r4=0x{:08X}, r5=0x{:08X} ({})",
                execution_ctx.r4.u32, execution_ctx.r5.u32,
                "buffer,size");
  }
  sub_82451F18(execution_ctx, base);

  const bool executed_successfully = execution_ctx.r3.s64 != 0;
  if (!executed_successfully) {
    REXLOG_WARN("Lua mod override {} from {} failed guest execution and will fall back to the "
                "original loader",
                script_key, override_path.string());
    return false;
  }

  ctx.r3 = execution_ctx.r3;
  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO("Lua mod override {} from {} returned {}", script_key, override_path.string(),
                execution_ctx.r3.s64);
  }
  return true;
}

}  // namespace

void TouchNativeLuaHooks() {}

}  // namespace refii::lua

PPC_FUNC_IMPL(sub_82451E40) {
  PPC_FUNC_PROLOGUE();

  const refii::lua::NativeScriptRequest request{
      .lua_state_guest = ctx.r3.u32,
      .source_guest = ctx.r4.u32,
      .chunk_name_guest = ctx.r5.u32,
  };

  refii::lua::TraceScriptLoad(base, request.source_guest, request.chunk_name_guest);
  if (refii::lua::NativeLuaModOverridesEnabled()) {
    const std::string script_key = refii::lua::ReadNativeLuaScriptKey(base, request);
    if (!script_key.empty()) {
      const std::filesystem::path override_path = refii::lua::ResolveLuaModOverridePath(script_key);
      if (!override_path.empty()) {
        if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
          REXLOG_INFO("Lua mod override matched {} -> {}", script_key, override_path.string());
        }
        if (refii::lua::RunLuaModOverride(ctx, base, request, script_key, override_path)) {
          if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
            REXLOG_INFO("Lua mod override completed {}", script_key);
          }
          return;
        }

        REXLOG_WARN("Lua mod override for {} matched {}, but execution fell back to the guest "
                    "loader",
                    script_key, override_path.string());
      } else if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
        REXLOG_INFO("Lua mod override fallback for {}", script_key);
      }
    } else if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
      REXLOG_INFO("Lua mod override lookup skipped because no recognizable script key was "
                  "available (source=0x{:08X}, chunk=0x{:08X})",
                  request.source_guest, request.chunk_name_guest);
    }
  }

  __imp__sub_82451E40(ctx, base);
}

PPC_FUNC_IMPL(sub_82451F18) {
  PPC_FUNC_PROLOGUE();

  const refii::lua::NativeScriptRequest request{
      .lua_state_guest = ctx.r3.u32,
      .script_size = ctx.r4.u32,
      .script_buffer_guest = ctx.r5.u32,
      .source_guest = ctx.r6.u32,
      .chunk_name_guest = ctx.r7.u32,
  };

  bool mod_override_matched = false;
  if (!refii::lua::IsRunningLuaModOverrideDispatch() &&
      refii::lua::NativeLuaModOverridesEnabled()) {
    const std::string script_key = refii::lua::ReadNativeLuaScriptKey(base, request);
    if (!script_key.empty()) {
      const std::filesystem::path override_path = refii::lua::ResolveLuaModOverridePath(script_key);
      if (!override_path.empty()) {
        mod_override_matched = true;
        if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
          REXLOG_INFO("Lua execute override matched {} -> {}", script_key, override_path.string());
        }
        if (refii::lua::RunLuaModOverride(ctx, base, request, script_key, override_path)) {
          if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
            REXLOG_INFO("Lua execute override completed {}", script_key);
          }
          return;
        }

        REXLOG_WARN("Lua execute override for {} matched {}, but execution fell back to the "
                    "guest bytecode",
                    script_key, override_path.string());
      }
    }
  }

  if (refii::lua::NativeLuaScriptLoadTracingEnabled()) {
    const std::string script_key = refii::lua::ReadNativeLuaScriptKey(base, request);
    if (!script_key.empty()) {
      REXLOG_INFO("Lua execute script {}", script_key);
    }
  }

  if (mod_override_matched) {
    __imp__sub_82451F18(ctx, base);
    return;
  }

  switch (refii::lua::DispatchNativeScript(ctx, base, request)) {
    case refii::lua::NativeDispatchResult::kHandledSuccess:
      ctx.r3.s64 = 1;
      return;
    case refii::lua::NativeDispatchResult::kHandledFailure:
      ctx.r3.s64 = 0;
      return;
    case refii::lua::NativeDispatchResult::kUnhandled:
      break;
  }

  __imp__sub_82451F18(ctx, base);
}
