// refii - the ReXGlue code generator as a library.
//
// The C ABI the launcher P/Invokes to turn the player's game executable into C++. It is
// the `rexglue codegen` command without the process around it: no executable to resolve,
// no PATH for its runtime, no console to parse. The library sits in bin/ beside the
// runtime it links and is loaded the way every other refii_* library is.
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Bumped when a signature or an event's meaning changes. The launcher checks it before
// the first call so a stale library beside a new launcher fails with a message rather
// than a wrong argument.
#define REFII_RECOMPILER_ABI_VERSION 1

// What the generator reports while it runs, in the order the SDK raises them. `detail`
// is UTF-8 and valid only for the duration of the callback.
enum refii_recompiler_event {
  // The input executable was loaded. detail: its name, title id and version.
  REFII_RECOMPILER_EVENT_BINARY = 1,
  // A module's analysis and emit cycle started. detail: module name; index/total set.
  REFII_RECOMPILER_EVENT_MODULE_STARTED = 2,
  // A phase inside the current module started. detail: the phase name.
  REFII_RECOMPILER_EVENT_PHASE = 3,
  // The current module finished. index: elapsed milliseconds.
  REFII_RECOMPILER_EVENT_MODULE_FINISHED = 4,
  // A project-level emit phase started (the module registry, the entrypoint). detail: name.
  REFII_RECOMPILER_EVENT_PROJECT_PHASE = 5,
  REFII_RECOMPILER_EVENT_PROJECT_PHASE_FINISHED = 6,
  // One line the generator logged. detail: "[level] [category] message".
  REFII_RECOMPILER_EVENT_LOG = 7,
};

// Returns 0 to carry on, or nonzero to stop. A stop is honoured at the next progress event
// (not at a log line: spdlog swallows what its sinks throw) and codegen then returns
// REFII_RECOMPILER_STOPPED with whatever it had written left on disk for the caller to
// remove. The callback may block for as long as it likes, which is how a pause works: the
// generator runs on the calling thread and simply waits inside the callback.
typedef int32_t (*refii_recompiler_progress_fn)(void* user, int32_t event, const char* detail,
                                                int32_t index, int32_t total);

#define REFII_RECOMPILER_OK 0
#define REFII_RECOMPILER_FAILED 1
#define REFII_RECOMPILER_STOPPED 2

// The ABI version this library was built with; compare against
// REFII_RECOMPILER_ABI_VERSION.
int32_t refii_recompiler_v1_abi_version(void);

// The SDK version the generator was built from ("0.10.0-dev"). Static storage.
const char* refii_recompiler_v1_sdk_version(void);

// Generates the sources the manifest describes, writing wherever it says. Everything the
// manifest holds is taken as written: the launcher produces the effective manifest with
// absolute paths before calling this.
//
// Runs on the calling thread and returns when the generator does; `progress` is invoked
// on that same thread. Returns REFII_RECOMPILER_OK on success and REFII_RECOMPILER_STOPPED
// when the callback asked it to stop. On failure, writes a UTF-8 message into `error`
// (NUL-terminated, truncated to `error_capacity`) and returns REFII_RECOMPILER_FAILED.
int32_t refii_recompiler_v1_codegen(const char* manifest_path,
                                    refii_recompiler_progress_fn progress, void* user,
                                    char* error, size_t error_capacity);

#ifdef __cplusplus
}
#endif
