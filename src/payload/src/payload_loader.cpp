// Host-side loading of the recompiled guest payload.
//
// Resolved BY CONVENTION, beside the library that contains this code: the payload is
// written there by the install-time build, and both the dev tree and an exported app put
// the native libraries in one directory. Nothing here reads a configured path — a config
// name would be one more thing to get wrong on a machine we cannot inspect.

#include "refii_payload/payload.h"

#include <rex/logging.h>

#include <cstdlib>
#include <mutex>
#include <stdexcept>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace refii::payload {
namespace {

#if defined(_WIN32)
using ModuleHandle = HMODULE;
constexpr const char* kPayloadFileName = "refii_generated.dll";
#elif defined(__APPLE__)
using ModuleHandle = void*;
constexpr const char* kPayloadFileName = "librefii_generated.dylib";
#else
using ModuleHandle = void*;
constexpr const char* kPayloadFileName = "librefii_generated.so";
#endif

struct State {
  ModuleHandle handle = nullptr;
  const rex::PPCImageInfo* image = nullptr;
  std::string path;
  std::string error;
  bool attempted = false;
};

State& Get() {
  static State state;
  return state;
}

std::mutex& Lock() {
  static std::mutex mutex;
  return mutex;
}

/// Directory holding the library this function is compiled into.
///
/// Deliberately NOT the executable's directory: under the Godot host the executable is
/// Godot's, which is not where refii's own libraries are staged.
std::string OwnDirectory() {
#if defined(_WIN32)
  HMODULE self = nullptr;
  if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                             GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                         reinterpret_cast<LPCSTR>(&OwnDirectory), &self) == 0) {
    return {};
  }

  char buffer[MAX_PATH] = {};
  DWORD length = GetModuleFileNameA(self, buffer, MAX_PATH);
  if (length == 0 || length >= MAX_PATH) {
    return {};
  }

  std::string path(buffer, length);
  size_t separator = path.find_last_of("\\/");
  return separator == std::string::npos ? std::string{} : path.substr(0, separator);
#else
  Dl_info info{};
  if (dladdr(reinterpret_cast<const void*>(&OwnDirectory), &info) == 0 ||
      info.dli_fname == nullptr) {
    return {};
  }

  std::string path(info.dli_fname);
  size_t separator = path.find_last_of('/');
  return separator == std::string::npos ? std::string{} : path.substr(0, separator);
#endif
}

ModuleHandle OpenModule(const std::string& path, std::string& error) {
#if defined(_WIN32)
  ModuleHandle handle =
      LoadLibraryExA(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
  if (handle == nullptr) {
    handle = LoadLibraryA(path.c_str());
  }
  if (handle == nullptr) {
    error = "LoadLibrary failed with error " + std::to_string(GetLastError());
  }
  return handle;
#else
  // RTLD_GLOBAL: the payload's own symbols have to be visible to anything resolved
  // against the global scope once it is in, and RTLD_NOW surfaces a mismatched build
  // here rather than at the first guest call.
  ModuleHandle handle = dlopen(path.c_str(), RTLD_NOW | RTLD_GLOBAL);
  if (handle == nullptr) {
    const char* reason = dlerror();
    error = reason != nullptr ? reason : "dlopen failed";
  }
  return handle;
#endif
}

void* FindSymbol(ModuleHandle handle, const char* symbol) {
#if defined(_WIN32)
  return reinterpret_cast<void*>(GetProcAddress(handle, symbol));
#else
  return dlsym(handle, symbol);
#endif
}

}  // namespace

bool Load(std::string& error) {
  std::lock_guard<std::mutex> guard(Lock());
  State& state = Get();

  if (state.attempted) {
    error = state.error;
    return state.handle != nullptr;
  }

  state.attempted = true;

  std::string directory = OwnDirectory();
  if (directory.empty()) {
    state.error = "Could not locate the directory holding refii's native libraries";
    error = state.error;
    return false;
  }

#if defined(_WIN32)
  SetDllDirectoryA(directory.c_str());
#endif

  // Beside this library first: that is where the install-time build writes the module, and
  // where an exported app keeps its whole native closure. The others are dev-tree layouts
  // — the standalone executable is in bin/ while the libraries are in lib/, so "beside me"
  // on its own never finds it there.
  const std::string candidates[] = {
      directory + "/" + kPayloadFileName,
      directory + "/../lib/" + kPayloadFileName,
      directory + "/lib/" + kPayloadFileName,
  };

  // EVERY candidate's own error, not the last one's. Reporting a single `openError`
  // overwritten by each attempt named the FIRST path and quoted the LAST failure's
  // message, so a module that was present and failed to load its dependencies was reported
  // as "missing" — the real reason, a missing RUNPATH, was the one line thrown away.
  std::string attempts;
  for (const std::string& candidate : candidates) {
    std::string openError;
    state.handle = OpenModule(candidate, openError);
    if (state.handle != nullptr) {
      state.path = candidate;
      break;
    }

    attempts += "\n  " + candidate + ": " + openError;
  }

  if (state.handle == nullptr) {
    state.path = candidates[0];
    state.error = "The recompiled game module could not be loaded. Tried:" + attempts +
                  "\nIf none of these exist, run the game-code build step in the launcher "
                  "to produce it.";
    error = state.error;
    return false;
  }

  auto entry = reinterpret_cast<RefiiPayloadImageConfigFn>(
      FindSymbol(state.handle, REFII_PAYLOAD_ENTRY_SYMBOL));
  if (entry == nullptr) {
    state.error = "The recompiled game module (" + state.path + ") does not export " +
                  REFII_PAYLOAD_ENTRY_SYMBOL +
                  "; it was built by a different version of the launcher and must be rebuilt.";
    error = state.error;
    return false;
  }

  state.image = entry();
  if (state.image == nullptr) {
    state.error = "The recompiled game module (" + state.path + ") returned no image descriptor";
    error = state.error;
    return false;
  }

  REXLOG_INFO("Loaded recompiled game module: {}", state.path);
  error.clear();
  return true;
}

bool IsLoaded() noexcept { return Get().image != nullptr; }

const std::string& LoadError() noexcept { return Get().error; }

const std::string& ModulePath() noexcept { return Get().path; }

const rex::PPCImageInfo& ImageConfig() {
  State& state = Get();
  if (state.image == nullptr) {
    std::string error;
    Load(error);
  }

  // THROWS RATHER THAN ABORTS. A missing or unloadable payload is the ordinary state of a
  // fresh install — it is exactly what the launcher's build step exists to fix — so it must
  // not take the process down with SIGABRT and a core dump. The host bridge already has a
  // failure path for app creation and catches std::exception around it; throwing carries
  // the reason there instead of losing it to abort().
  if (state.image == nullptr) {
    REXLOG_ERROR("{}", state.error);
    throw std::runtime_error(state.error);
  }

  return *state.image;
}

const PPCFuncMapping* FuncMappings() { return ImageConfig().func_mappings; }

PPCFunc* ResolveGuestSymbol(const char* symbol) {
  State& state = Get();
  if (state.handle == nullptr) {
    std::string error;
    if (!Load(error)) {
      return nullptr;
    }
  }

  return reinterpret_cast<PPCFunc*>(FindSymbol(state.handle, symbol));
}

void CallGuest(PPCFunc* function, const char* symbol, PPCContext& ctx, uint8_t* base) {
  if (function == nullptr) {
    REXLOG_ERROR(
        "The recompiled game module does not export {}. It was generated from a different "
        "game image than this build expects; rebuild it from the launcher.",
        symbol);
    std::abort();
  }

  function(ctx, base);
}

}  // namespace refii::payload
