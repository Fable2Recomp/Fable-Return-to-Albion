#include "core/native_lua_hooks.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/runtime.h>

PPC_EXTERN_FUNC(__imp__sub_82451E40);
PPC_EXTERN_FUNC(__imp__sub_82451F18);
PPC_EXTERN_FUNC(__imp__rex_lua_load_internal);
PPC_EXTERN_FUNC(__imp__rex_lua_cpcall);

namespace refii::lua {
namespace {

thread_local int g_lua_mod_override_dispatch_depth = 0;
thread_local std::vector<std::string> g_lua_mod_override_script_stack;
constexpr std::string_view kRefiiLuaDisassemblyPlaceholder =
    "-- refii-disassembly-placeholder";

enum class LuaOverrideDispatchKind {
  kLoad,
  kExecute,
};

struct RuntimeOverrideTraceState {
  LuaOverrideDispatchKind dispatch_kind = LuaOverrideDispatchKind::kExecute;
  std::string script_key;
  std::string override_path;
  std::chrono::steady_clock::time_point started;
  std::atomic<bool> active{true};
  std::mutex mutex;
  std::string status = "inside_override_setup";
  std::string last_nested_script;
  std::string last_nested_event;
  std::chrono::steady_clock::time_point last_nested_at{};
};

thread_local std::shared_ptr<RuntimeOverrideTraceState> g_runtime_override_trace;
thread_local int g_runtime_override_load_internal_depth = 0;

struct ScopedLuaModOverrideDispatch {
  explicit ScopedLuaModOverrideDispatch(std::string_view script_key)
      : script_key_(script_key) {
    ++g_lua_mod_override_dispatch_depth;
    g_lua_mod_override_script_stack.emplace_back(script_key_);
  }

  ~ScopedLuaModOverrideDispatch() {
    if (!g_lua_mod_override_script_stack.empty()) {
      g_lua_mod_override_script_stack.pop_back();
    }
    if (g_lua_mod_override_dispatch_depth > 0) {
      --g_lua_mod_override_dispatch_depth;
    }
  }

 private:
  std::string script_key_;
};

bool IsRunningLuaModOverrideDispatch() {
  return g_lua_mod_override_dispatch_depth > 0;
}

std::string_view LuaOverrideDispatchKindLabel(LuaOverrideDispatchKind kind) {
  switch (kind) {
    case LuaOverrideDispatchKind::kLoad:
      return "load";
    case LuaOverrideDispatchKind::kExecute:
      return "execute";
  }
  return "override";
}

int ReadGuestLuaRawStackTop(uint8_t* base, uint32_t lua_state_guest) {
  if (base == nullptr || lua_state_guest == 0) {
    return -1;
  }

  const uint32_t top = PPC_LOAD_U32(lua_state_guest + 8);
  const uint32_t stack_base = PPC_LOAD_U32(lua_state_guest + 32);
  if (top < stack_base) {
    return -1;
  }
  return static_cast<int>((top - stack_base) / 8);
}

void TraceGuestLuaStackTopOnly(LuaOverrideDispatchKind dispatch_kind,
                               std::string_view script_key,
                               std::string_view phase,
                               std::string_view skipped_values_label,
                               uint8_t* base,
                               uint32_t lua_state_guest) {
  if (!NativeLuaModOverrideTracingEnabled()) {
    return;
  }

  const int stack_top = ReadGuestLuaRawStackTop(base, lua_state_guest);
  if (stack_top < 0) {
    REXLOG_INFO("Lua {} override {} {} stack_top=<unavailable> values={}",
                LuaOverrideDispatchKindLabel(dispatch_kind),
                phase,
                script_key,
                skipped_values_label);
    return;
  }

  REXLOG_INFO("Lua {} override {} {} stack_top={} values={}",
              LuaOverrideDispatchKindLabel(dispatch_kind),
              phase,
              script_key,
              stack_top,
              skipped_values_label);
}

void UpdateRuntimeOverrideTraceStatus(
    const std::shared_ptr<RuntimeOverrideTraceState>& state,
    std::string_view status) {
  if (state == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(state->mutex);
  state->status = std::string(status);
}

void SpawnRuntimeOverrideWatchdog(
    const std::shared_ptr<RuntimeOverrideTraceState>& state) {
  if (!NativeLuaModOverrideTracingEnabled() || state == nullptr) {
    return;
  }

  std::thread([state]() {
    constexpr auto kInitialDelay = std::chrono::milliseconds(2000);
    constexpr auto kRepeatDelay = std::chrono::milliseconds(10000);

    std::this_thread::sleep_for(kInitialDelay);
    while (state->active.load()) {
      std::string status;
      std::string last_nested_script;
      std::string last_nested_event;
      long long last_nested_elapsed_ms = -1;
      {
        std::lock_guard<std::mutex> lock(state->mutex);
        status = state->status;
        last_nested_script = state->last_nested_script;
        last_nested_event = state->last_nested_event;
        if (state->last_nested_at != std::chrono::steady_clock::time_point{}) {
          last_nested_elapsed_ms =
              std::chrono::duration_cast<std::chrono::milliseconds>(
                  std::chrono::steady_clock::now() - state->last_nested_at)
                  .count();
        }
      }

      const auto elapsed_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - state->started)
              .count();
      if (last_nested_script.empty()) {
        REXLOG_WARN(
            "Lua {} override in_flight {} elapsed_ms={} status={} "
            "last_nested=<none>",
            LuaOverrideDispatchKindLabel(state->dispatch_kind),
            state->script_key,
            elapsed_ms,
            status);
      } else {
        REXLOG_WARN(
            "Lua {} override in_flight {} elapsed_ms={} status={} "
            "last_nested={} event={} last_nested_elapsed_ms={}",
            LuaOverrideDispatchKindLabel(state->dispatch_kind),
            state->script_key,
            elapsed_ms,
            status,
            last_nested_script,
            last_nested_event,
            last_nested_elapsed_ms);
      }

      std::this_thread::sleep_for(kRepeatDelay);
    }
  }).detach();
}

class ScopedRuntimeOverrideTrace {
 public:
  explicit ScopedRuntimeOverrideTrace(
      const std::shared_ptr<RuntimeOverrideTraceState>& state)
      : previous_(g_runtime_override_trace),
        state_(state),
        replaced_(state_ != nullptr) {
    if (replaced_) {
      g_runtime_override_trace = state_;
      SpawnRuntimeOverrideWatchdog(state_);
    }
  }

  ScopedRuntimeOverrideTrace(const ScopedRuntimeOverrideTrace&) = delete;
  ScopedRuntimeOverrideTrace& operator=(const ScopedRuntimeOverrideTrace&) = delete;

  ~ScopedRuntimeOverrideTrace() {
    if (state_ != nullptr) {
      state_->active.store(false);
    }
    if (replaced_) {
      g_runtime_override_trace = previous_;
    }
  }

 private:
  std::shared_ptr<RuntimeOverrideTraceState> previous_;
  std::shared_ptr<RuntimeOverrideTraceState> state_;
  bool replaced_ = false;
};

void TraceRuntimeOverrideNestedEvent(std::string_view child_script_key,
                                     std::string_view event) {
  if (!NativeLuaModOverrideTracingEnabled() || g_runtime_override_trace == nullptr ||
      !g_runtime_override_trace->active.load() ||
      child_script_key == g_runtime_override_trace->script_key) {
    return;
  }

  {
    std::lock_guard<std::mutex> lock(g_runtime_override_trace->mutex);
    g_runtime_override_trace->last_nested_script = std::string(child_script_key);
    g_runtime_override_trace->last_nested_event = std::string(event);
    g_runtime_override_trace->last_nested_at = std::chrono::steady_clock::now();
  }

  REXLOG_INFO("Lua nested {} parent={} child={}",
              event,
              g_runtime_override_trace->script_key,
              child_script_key);
}

bool HasActiveRuntimeOverrideTrace() {
  return NativeLuaModOverrideTracingEnabled() &&
         g_runtime_override_trace != nullptr &&
         g_runtime_override_trace->active.load();
}

std::string_view CurrentRuntimeOverrideCpcallPhase() {
  return g_runtime_override_load_internal_depth > 0 ? "load_cpcall"
                                                    : "execute_cpcall";
}

void TraceGuestLuaTopValueOnProtectedCallFailure(PPCContext probe_ppc,
                                                 std::string_view phase,
                                                 uint8_t* base,
                                                 uint32_t lua_state_guest) {
  if (!HasActiveRuntimeOverrideTrace()) {
    return;
  }

  const int stack_top = ReadGuestLuaRawStackTop(base, lua_state_guest);
  if (stack_top <= 0) {
    REXLOG_WARN("Lua override {} failure_top {} stack_top={}",
                phase,
                g_runtime_override_trace->script_key,
                stack_top);
    return;
  }

  (void)probe_ppc;

  const uint32_t top_guest = PPC_LOAD_U32(lua_state_guest + 8);
  const uint32_t stack_base_guest = PPC_LOAD_U32(lua_state_guest + 32);
  if (top_guest < stack_base_guest + 8) {
    REXLOG_WARN("Lua override {} failure_top {} stack_top={} top_value=<empty_slot>",
                phase,
                g_runtime_override_trace->script_key,
                stack_top);
    return;
  }

  const uint32_t value_address = top_guest - 8;
  const uint32_t raw_value = PPC_LOAD_U32(value_address + 0);
  const uint32_t raw_type = PPC_LOAD_U32(value_address + 4);

  switch (raw_type) {
    case 0:
      REXLOG_WARN("Lua override {} failure_top {} stack_top={} top_value=nil",
                  phase,
                  g_runtime_override_trace->script_key,
                  stack_top);
      return;
    case 1:
      REXLOG_WARN("Lua override {} failure_top {} stack_top={} top_value=boolean {}",
                  phase,
                  g_runtime_override_trace->script_key,
                  stack_top,
                  raw_value != 0 ? "true" : "false");
      return;
    case 4: {
      std::string string_value = ReadGuestLuaStringLike(base, raw_value, 4096);
      if (!string_value.empty()) {
        for (char& ch : string_value) {
          if (ch == '\r' || ch == '\n' || ch == '\t') {
            ch = ' ';
          }
        }
        REXLOG_WARN("Lua override {} failure_top {} stack_top={} top_value=string: {}",
                    phase,
                    g_runtime_override_trace->script_key,
                    stack_top,
                    string_value);
        return;
      }
      break;
    }
    default:
      break;
  }

  REXLOG_WARN(
      "Lua override {} failure_top {} stack_top={} top_type={} top_raw=0x{:08X}",
      phase,
      g_runtime_override_trace->script_key,
      stack_top,
      raw_type,
      raw_value);
}

enum class NestedLuaExecuteOverrideDisposition {
  kAllow,
  kSkipSameScript,
  kSkipReentrant,
};

NestedLuaExecuteOverrideDisposition ClassifyNestedLuaExecuteOverride(
    std::string_view script_key) {
  if (!IsRunningLuaModOverrideDispatch()) {
    return NestedLuaExecuteOverrideDisposition::kAllow;
  }

  if (!g_lua_mod_override_script_stack.empty() &&
      g_lua_mod_override_script_stack.back() == script_key) {
    return NestedLuaExecuteOverrideDisposition::kSkipSameScript;
  }

  const bool already_active = std::any_of(
      g_lua_mod_override_script_stack.begin(),
      g_lua_mod_override_script_stack.end(),
      [&](const std::string& active_script) { return active_script == script_key; });
  if (already_active) {
    return NestedLuaExecuteOverrideDisposition::kSkipReentrant;
  }

  return NestedLuaExecuteOverrideDisposition::kAllow;
}

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

bool ReadHostFileBytes(const std::filesystem::path& path,
                       std::vector<uint8_t>& out_bytes) {
  std::error_code file_size_error;
  const uintmax_t file_size = std::filesystem::file_size(path, file_size_error);
  if (file_size_error) {
    return false;
  }
  if (file_size > static_cast<uintmax_t>(std::numeric_limits<size_t>::max())) {
    return false;
  }

  std::ifstream stream(path, std::ios::binary);
  if (!stream) {
    return false;
  }

  out_bytes.resize(static_cast<size_t>(file_size));
  if (!out_bytes.empty()) {
    stream.read(reinterpret_cast<char*>(out_bytes.data()),
                static_cast<std::streamsize>(out_bytes.size()));
  }

  return stream.good() ||
         (stream.eof() &&
          stream.gcount() == static_cast<std::streamsize>(out_bytes.size()));
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
  return bytes.size() >= 4 && bytes[0] == 0x1B && bytes[1] == 'L' &&
         bytes[2] == 'u' && bytes[3] == 'a';
}

// Generated PPC contract summary:
// - __imp__sub_82451E40 is the load-path wrapper: r3=lua_state, r4=source, r5=chunk_name.
// - On success it resolves chunk bytes and calls sub_82451F18 with
//   r3=lua_state, r4=script_buffer, r5=script_size, r6=source, r7=chunk_name.
// - __imp__sub_82451F18 is the shared compile/execute seam used both by
//   __imp__sub_82451E40 and by direct callers, so loose overrides only replace
//   the staged r4/r5 payload there while preserving r3/r6/r7.
bool RunLuaModOverride(PPCContext& ctx,
                       uint8_t* base,
                       const NativeScriptRequest& request,
                       const std::string& script_key,
                       const std::filesystem::path& override_path,
                       LuaOverrideDispatchKind dispatch_kind) {
  const std::string_view dispatch_label =
      LuaOverrideDispatchKindLabel(dispatch_kind);
  auto* runtime = rex::Runtime::instance();
  if (runtime == nullptr || runtime->memory() == nullptr) {
    REXLOG_WARN(
        "Lua {} override matched {} at {}, but the runtime memory system is unavailable",
        dispatch_label,
        script_key,
        override_path.string());
    return false;
  }

  std::vector<uint8_t> script_bytes;
  if (!ReadHostFileBytes(override_path, script_bytes)) {
    REXLOG_WARN("Lua {} override matched {} at {}, but the file could not be read",
                dispatch_label,
                script_key,
                override_path.string());
    return false;
  }

  if (IsRefiiLuaDisassemblyPlaceholder(script_bytes)) {
    if (NativeLuaModOverrideTracingEnabled()) {
      REXLOG_INFO(
          "Lua {} override placeholder at {} is documentation-only and will be ignored",
          dispatch_label,
          override_path.string());
    }
    return false;
  }

  if (script_bytes.size() >
      static_cast<size_t>(std::numeric_limits<uint32_t>::max())) {
    REXLOG_WARN(
        "Lua {} override matched {} at {}, but the file is too large to stage into "
        "guest memory",
        dispatch_label,
        script_key,
        override_path.string());
    return false;
  }

  std::shared_ptr<RuntimeOverrideTraceState> runtime_trace_state;
  if (NativeLuaModOverrideTracingEnabled()) {
    runtime_trace_state = std::make_shared<RuntimeOverrideTraceState>();
    runtime_trace_state->dispatch_kind = dispatch_kind;
    runtime_trace_state->script_key = script_key;
    runtime_trace_state->override_path = override_path.string();
    runtime_trace_state->started = std::chrono::steady_clock::now();
    runtime_trace_state->status = "inside_override_setup";
    REXLOG_INFO("Lua {} override matched {} -> {}",
                dispatch_label,
                script_key,
                override_path.string());
  }

  const uint32_t script_size = static_cast<uint32_t>(script_bytes.size());
  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO("Lua {} override staged {} bytes as {} from {}",
                dispatch_label,
                script_size,
                LooksLikeLuaBytecode(script_bytes) ? "bytecode" : "source",
                override_path.string());
  }
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "staging_complete");

  const uint32_t guest_buffer =
      runtime->memory()->SystemHeapAlloc(std::max<uint32_t>(1, script_size));
  if (guest_buffer == 0) {
    REXLOG_WARN("Lua {} override matched {} at {}, but guest staging allocation failed",
                dispatch_label,
                script_key,
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

  uint8_t* guest_bytes =
      runtime->memory()->TranslateVirtual<uint8_t*>(guest_buffer);
  if (guest_bytes == nullptr) {
    REXLOG_WARN("Lua {} override matched {} at {}, but guest staging translation failed",
                dispatch_label,
                script_key,
                override_path.string());
    return false;
  }

  if (!script_bytes.empty()) {
    std::memcpy(guest_bytes, script_bytes.data(), script_bytes.size());
  }
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "guest_buffer_ready");

  PPCContext execution_ctx = ctx;
  execution_ctx.r3.u32 = request.lua_state_guest;
  execution_ctx.r4.u32 = guest_buffer;
  execution_ctx.r5.u32 = script_size;
  execution_ctx.r6.u32 = request.source_guest;
  execution_ctx.r7.u32 = request.chunk_name_guest;

  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO(
        "Lua {} override invoke_start {} r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} "
        "r6=0x{:08X} r7=0x{:08X}",
        dispatch_label,
        script_key,
        execution_ctx.r3.u32,
        execution_ctx.r4.u32,
        execution_ctx.r5.u32,
        execution_ctx.r6.u32,
        execution_ctx.r7.u32);
  }

  const ScopedLuaModOverrideDispatch override_dispatch_scope(script_key);
  const ScopedRuntimeOverrideTrace runtime_trace_scope(runtime_trace_state);
  const auto invoke_started = std::chrono::steady_clock::now();
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "preinvoke_stack_top");
  TraceGuestLuaStackTopOnly(dispatch_kind,
                            script_key,
                            "stack_before",
                            "<skipped_preinvoke>",
                            base,
                            request.lua_state_guest);
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "inside_guest_loader");
  __imp__sub_82451F18(execution_ctx, base);

  const auto invoke_elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - invoke_started)
          .count();
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "postinvoke_stack_top");
  TraceGuestLuaStackTopOnly(dispatch_kind,
                            script_key,
                            "stack_after",
                            "<skipped_postinvoke>",
                            base,
                            request.lua_state_guest);
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "guest_loader_returned");
  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO(
        "Lua {} override returned {} result={} elapsed_ms={}",
        dispatch_label,
        script_key,
        execution_ctx.r3.s64,
        invoke_elapsed_ms);
  }

  if (execution_ctx.r3.s64 == 0) {
    UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "guest_loader_rejected");
    if (NativeLuaModOverrideTracingEnabled()) {
      const int stack_top = ReadGuestLuaRawStackTop(base, request.lua_state_guest);
      if (stack_top < 0) {
        REXLOG_WARN(
            "Lua {} override failure_boundary {} result=0 "
            "contract=sub_82451F18(load_failed_or_protected_call_failed) "
            "stack_after_top=<unavailable>",
            dispatch_label,
            script_key);
      } else {
        REXLOG_WARN(
            "Lua {} override failure_boundary {} result=0 "
            "contract=sub_82451F18(load_failed_or_protected_call_failed) "
            "stack_after_top={}",
            dispatch_label,
            script_key,
            stack_top);
      }
    }
    REXLOG_WARN(
        "Lua {} override for {} matched {}, but execution fell back to the guest "
        "loader",
        dispatch_label,
        script_key,
        override_path.string());
    return false;
  }

  ctx = execution_ctx;
  UpdateRuntimeOverrideTraceStatus(runtime_trace_state, "completed");
  if (NativeLuaModOverrideTracingEnabled()) {
    REXLOG_INFO("Lua {} override completed {}",
                dispatch_label,
                script_key);
  }
  return true;
}

}  // namespace

void TouchNativeLuaHooks() {}

}  // namespace refii::lua

PPC_FUNC_IMPL(rex_lua_load_internal) {
  PPC_FUNC_PROLOGUE();

  if (!refii::lua::HasActiveRuntimeOverrideTrace()) {
    __imp__rex_lua_load_internal(ctx, base);
    return;
  }

  const uint32_t lua_state_guest = ctx.r3.u32;
  const uint32_t script_buffer_guest = ctx.r4.u32;
  const uint32_t script_size = ctx.r5.u32;
  const uint32_t source_guest = ctx.r6.u32;

  refii::lua::UpdateRuntimeOverrideTraceStatus(
      refii::lua::g_runtime_override_trace, "inside_rex_lua_load_internal");
  REXLOG_INFO(
      "Lua override load_internal_start {} lua_state=0x{:08X} "
      "script_buffer=0x{:08X} script_size=0x{:08X} source=0x{:08X}",
      refii::lua::g_runtime_override_trace->script_key,
      lua_state_guest,
      script_buffer_guest,
      script_size,
      source_guest);
  refii::lua::TraceGuestLuaStackTopOnly(
      refii::lua::g_runtime_override_trace->dispatch_kind,
      refii::lua::g_runtime_override_trace->script_key,
      "load_internal_stack_before",
      "<skipped_load_internal_pre>",
      base,
      lua_state_guest);

  ++refii::lua::g_runtime_override_load_internal_depth;
  __imp__rex_lua_load_internal(ctx, base);
  --refii::lua::g_runtime_override_load_internal_depth;

  refii::lua::UpdateRuntimeOverrideTraceStatus(
      refii::lua::g_runtime_override_trace, "returned_rex_lua_load_internal");
  refii::lua::TraceGuestLuaStackTopOnly(
      refii::lua::g_runtime_override_trace->dispatch_kind,
      refii::lua::g_runtime_override_trace->script_key,
      "load_internal_stack_after",
      "<skipped_load_internal_post>",
      base,
      lua_state_guest);
  REXLOG_INFO("Lua override load_internal_return {} result={}",
              refii::lua::g_runtime_override_trace->script_key,
              ctx.r3.s64);
}

PPC_FUNC_IMPL(rex_lua_cpcall) {
  PPC_FUNC_PROLOGUE();

  if (!refii::lua::HasActiveRuntimeOverrideTrace()) {
    __imp__rex_lua_cpcall(ctx, base);
    return;
  }

  const std::string_view phase = refii::lua::CurrentRuntimeOverrideCpcallPhase();
  const uint32_t lua_state_guest = ctx.r3.u32;

  refii::lua::UpdateRuntimeOverrideTraceStatus(
      refii::lua::g_runtime_override_trace,
      phase == "load_cpcall" ? "inside_rex_lua_cpcall_load"
                              : "inside_rex_lua_cpcall_execute");
  REXLOG_INFO(
      "Lua override {}_start {} lua_state=0x{:08X} r4=0x{:08X} r5=0x{:08X} "
      "r6=0x{:08X} r7=0x{:08X}",
      phase,
      refii::lua::g_runtime_override_trace->script_key,
      lua_state_guest,
      ctx.r4.u32,
      ctx.r5.u32,
      ctx.r6.u32,
      ctx.r7.u32);
  refii::lua::TraceGuestLuaStackTopOnly(
      refii::lua::g_runtime_override_trace->dispatch_kind,
      refii::lua::g_runtime_override_trace->script_key,
      phase == "load_cpcall" ? "load_cpcall_stack_before"
                              : "execute_cpcall_stack_before",
      "<skipped_cpcall_pre>",
      base,
      lua_state_guest);

  __imp__rex_lua_cpcall(ctx, base);

  refii::lua::UpdateRuntimeOverrideTraceStatus(
      refii::lua::g_runtime_override_trace,
      phase == "load_cpcall" ? "returned_rex_lua_cpcall_load"
                              : "returned_rex_lua_cpcall_execute");
  refii::lua::TraceGuestLuaStackTopOnly(
      refii::lua::g_runtime_override_trace->dispatch_kind,
      refii::lua::g_runtime_override_trace->script_key,
      phase == "load_cpcall" ? "load_cpcall_stack_after"
                              : "execute_cpcall_stack_after",
      "<skipped_cpcall_post>",
      base,
      lua_state_guest);
  if (ctx.r3.s64 != 0) {
    REXLOG_WARN("Lua override {} failure_top_probe {} result={}",
                phase,
                refii::lua::g_runtime_override_trace->script_key,
                ctx.r3.s64);
    refii::lua::TraceGuestLuaTopValueOnProtectedCallFailure(
        ctx, phase, base, lua_state_guest);
  }
  REXLOG_INFO("Lua override {}_return {} result={}",
              phase,
              refii::lua::g_runtime_override_trace->script_key,
              ctx.r3.s64);
}

PPC_FUNC_IMPL(sub_82451E40) {
  PPC_FUNC_PROLOGUE();

  const refii::lua::NativeScriptRequest request{
      .lua_state_guest = ctx.r3.u32,
      .source_guest = ctx.r4.u32,
      .chunk_name_guest = ctx.r5.u32,
  };

  refii::lua::TraceScriptLoad(base, request.source_guest, request.chunk_name_guest);

  if (refii::lua::NativeLuaModOverridesEnabled() &&
      refii::lua::NativeLuaModOverrideTracingEnabled()) {
    const std::string script_key = refii::lua::ReadNativeLuaScriptKey(base, request);
    if (!script_key.empty()) {
      refii::lua::TraceRuntimeOverrideNestedEvent(script_key, "load_requested");
    } else {
      REXLOG_INFO("Lua load override lookup skipped because no recognizable script key was "
                  "available (source=0x{:08X}, chunk=0x{:08X})",
                  request.source_guest,
                  request.chunk_name_guest);
    }
  }

  __imp__sub_82451E40(ctx, base);
}

PPC_FUNC_IMPL(sub_82451F18) {
  PPC_FUNC_PROLOGUE();

  const refii::lua::NativeScriptRequest request{
      .lua_state_guest = ctx.r3.u32,
      .script_buffer_guest = ctx.r4.u32,
      .script_size = ctx.r5.u32,
      .source_guest = ctx.r6.u32,
      .chunk_name_guest = ctx.r7.u32,
  };

  bool mod_override_matched = false;
  if (refii::lua::NativeLuaModOverridesEnabled()) {
    const std::string script_key = refii::lua::ReadNativeLuaScriptKey(base, request);
    if (!script_key.empty()) {
      refii::lua::TraceRuntimeOverrideNestedEvent(script_key, "execute_requested");
      const bool nested_override = refii::lua::IsRunningLuaModOverrideDispatch();
      const refii::lua::NestedLuaExecuteOverrideDisposition nested_disposition =
          refii::lua::ClassifyNestedLuaExecuteOverride(script_key);
      if (nested_disposition !=
          refii::lua::NestedLuaExecuteOverrideDisposition::kAllow) {
        if (nested_disposition ==
            refii::lua::NestedLuaExecuteOverrideDisposition::kSkipSameScript) {
          refii::lua::TraceRuntimeOverrideNestedEvent(script_key,
                                                     "execute_skipped_same_script");
          if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
            REXLOG_INFO("Lua nested execute override skipped_same_script {}",
                        script_key);
          }
        } else {
          refii::lua::TraceRuntimeOverrideNestedEvent(script_key,
                                                     "execute_skipped_reentrant");
          if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
            REXLOG_INFO("Lua nested execute override skipped_reentrant {}",
                        script_key);
          }
        }
      } else {
        const std::filesystem::path override_path =
            refii::lua::ResolveLuaModOverridePath(script_key);
        if (!override_path.empty()) {
          mod_override_matched = true;
          refii::lua::TraceRuntimeOverrideNestedEvent(script_key, "execute_matched");
          if (refii::lua::RunLuaModOverride(
                  ctx,
                  base,
                  request,
                  script_key,
                  override_path,
                  refii::lua::LuaOverrideDispatchKind::kExecute)) {
            refii::lua::TraceRuntimeOverrideNestedEvent(script_key,
                                                       "execute_completed");
            return;
          }

          refii::lua::TraceRuntimeOverrideNestedEvent(script_key,
                                                     "execute_rejected");
          if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
            if (nested_override) {
              REXLOG_WARN(
                  "Lua nested execute override for {} matched {}, but execution "
                  "fell back to the guest bytecode",
                  script_key,
                  override_path.string());
            } else {
              REXLOG_WARN(
                  "Lua execute override for {} matched {}, but execution fell "
                  "back to the guest bytecode",
                  script_key,
                  override_path.string());
            }
          }
        } else {
          refii::lua::TraceRuntimeOverrideNestedEvent(script_key,
                                                     "execute_guest_only");
        }
      }
    } else if (refii::lua::NativeLuaModOverrideTracingEnabled()) {
      REXLOG_INFO("Lua execute override lookup skipped because no recognizable script key was "
                  "available (buffer=0x{:08X}, size=0x{:08X}, source=0x{:08X}, chunk=0x{:08X})",
                  request.script_buffer_guest,
                  request.script_size,
                  request.source_guest,
                  request.chunk_name_guest);
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
