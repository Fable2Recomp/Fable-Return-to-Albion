// refii - the shader bank pipeline as a library.
//
// The C ABI the launcher P/Invokes to turn the player's Shaders.sbk into translated, compiled
// shader artifacts: every microcode blob extracted, translated through XenosRecomp, compiled
// with the bundled DirectX Shader Compiler, and checked for an artifact per target. It is the
// `refii-shader-sbk build` command without the process around it, in the same arrangement as
// refii_recompiler: a library in bin/ beside the ones it links, loaded the way every other
// refii library is.
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Bumped when a signature or an event's meaning changes. The launcher checks it before the
// first call so a stale library beside a new launcher fails with a message rather than a
// wrong argument.
#define REFII_SHADER_BUILD_ABI_VERSION 1

// What the pipeline reports while it runs. `detail` is UTF-8 and valid only for the duration
// of the callback.
enum refii_shader_build_event {
  // Where the pipeline is. detail: its phase tag — "extracting_bank", "bank_ready",
  // "extracting_blobs", "compiling_shader", "compile_complete", "verifying". index/total:
  // items completed of items in the phase, both 0 when the phase has no count.
  REFII_SHADER_BUILD_EVENT_PROGRESS = 1,
  // One line of outcome for the launcher log. detail: the line.
  REFII_SHADER_BUILD_EVENT_LOG = 2,
};

// Returns 0 to carry on, or nonzero to stop. A stop is honoured at the next event and the
// build then returns REFII_SHADER_BUILD_STOPPED with whatever it had written left on disk
// for the caller to remove. The callback may block for as long as it likes, which is how a
// pause works: the pipeline runs on the calling thread and simply waits inside the callback.
typedef int32_t (*refii_shader_build_progress_fn)(void* user, int32_t event, const char* detail,
                                                  int32_t index, int32_t total);

#define REFII_SHADER_BUILD_OK 0
#define REFII_SHADER_BUILD_FAILED 1
#define REFII_SHADER_BUILD_STOPPED 2

// The ABI version this library was built with; compare against REFII_SHADER_BUILD_ABI_VERSION.
int32_t refii_shader_build_v1_abi_version(void);

// Extracts the banks in `bank_dir` (Shaders.sbk and ShadersRelease.sbk) into `output_dir`,
// translates and compiles every blob for `targets` ("auto" is the host's backend set, or a
// comma list such as "vulkan,d3d12"), writes extract_manifest.tsv and compile_report.tsv
// there, and verifies that every blob has an artifact for every target. Stale output under
// `output_dir` from an earlier run is removed first.
//
// Runs on the calling thread and returns when the pipeline does; `progress` is invoked on
// that same thread. Returns REFII_SHADER_BUILD_OK on success and REFII_SHADER_BUILD_STOPPED
// when the callback asked it to stop. On failure, writes a UTF-8 message into `error`
// (NUL-terminated, truncated to `error_capacity`) and returns REFII_SHADER_BUILD_FAILED.
int32_t refii_shader_build_v1_run(const char* bank_dir, const char* output_dir,
                                  const char* targets, refii_shader_build_progress_fn progress,
                                  void* user, char* error, size_t error_capacity);

#ifdef __cplusplus
}
#endif
