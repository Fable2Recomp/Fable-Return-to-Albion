#include "core/native_lua_hooks.h"
#include "core/native_lua_rexglue_compat.h"
#include <algorithm>
#include <string>
#include <string_view>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/runtime.h>

PPC_EXTERN_FUNC(__imp__rex_lhLua_RunScriptFile);
PPC_EXTERN_FUNC(__imp__rex_lhLua_RunScriptBuffer);
PPC_EXTERN_FUNC(__imp__rex_lhLua_GamePanic);

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

// THE GAME VM'S PANIC NAMES ITSELF. lhCreateLuaState installs this as lua_atpanic (TU1
// 0x824517D8, disc 0x82414E88): it builds "PANIC: unprotected error in call to Lua API (" + the
// string at L->top-1 and destroys it unread; luaD_throw then calls exit(1). An error raised outside
// any pcall -- Pluto refusing a value while the save writes its Lua section is the case seen --
// otherwise surfaces only as a fault in a thread the atexit handlers tore down. L->top is L+8; a
// TValue is {value, tag}, tag 4 a string, a TString's length at +12 and its chars at +16.
REX_EXTERN(rex_lhLua_GamePanic) {
  REX_FUNC_PROLOGUE();

  const uint32_t state = ctx.r3.u32;
  const uint32_t top = state != 0 ? refii::lua::PpcLoadU32(base, state + 8) : 0;
  if (top >= 8) {
    const uint32_t tag = refii::lua::PpcLoadU32(base, top - 4);
    const uint32_t value = refii::lua::PpcLoadU32(base, top - 8);
    if (tag == 4 && value != 0) {
      const uint32_t length = std::min<uint32_t>(refii::lua::PpcLoadU32(base, value + 12), 4096);
      std::string message(length, '\0');
      for (uint32_t i = 0; i < length; ++i) {
        message[i] = static_cast<char>(refii::lua::PpcLoadU8(base, value + 16 + i));
      }
      REXLOG_ERROR("Lua PANIC (unprotected error, the game exits): {}", message);
    } else {
      REXLOG_ERROR("Lua PANIC (unprotected error, the game exits): error value of type {}", tag);
    }
  } else {
    REXLOG_ERROR("Lua PANIC (unprotected error, the game exits): no state");
  }

  __imp__rex_lhLua_GamePanic(ctx, base);
}
