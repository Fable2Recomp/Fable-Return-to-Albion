#include "core/native_lua_hooks.h"
#include "core/native_lua_rexglue_compat.h"
#include <string>
#include <string_view>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/runtime.h>

PPC_EXTERN_FUNC(__imp__rex_lhLua_RunScriptFile);
PPC_EXTERN_FUNC(__imp__rex_lhLua_RunScriptBuffer);

namespace refii::lua {
namespace {
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
      source.empty() ? "<null>" : source,
      chunk_name.empty() ? "<null>" : chunk_name,
      source_guest,
      chunk_name_guest);
}
}  // namespace
}  // namespace refii::lua

REX_EXTERN(rex_lhLua_RunScriptFile) {
  REX_FUNC_PROLOGUE();

  refii::lua::TraceScriptLoad(base, ctx.r4.u32, ctx.r5.u32);

  __imp__rex_lhLua_RunScriptFile(ctx, base);
}

REX_EXTERN(rex_lhLua_RunScriptBuffer) {
  REX_FUNC_PROLOGUE();

  if (refii::lua::NativeLuaScriptLoadTracingEnabled()) {
    const refii::lua::NativeScriptRequest request{
        .lua_state_guest = ctx.r3.u32,
        .script_buffer_guest = ctx.r4.u32,
        .script_size = ctx.r5.u32,
        .source_guest = ctx.r6.u32,
        .chunk_name_guest = ctx.r7.u32,
    };
    const std::string script_key = refii::lua::ReadNativeLuaScriptKey(base, request);
    if (!script_key.empty()) {
      REXLOG_INFO("Lua execute script {}", script_key);
    }
  }

  __imp__rex_lhLua_RunScriptBuffer(ctx, base);
}
