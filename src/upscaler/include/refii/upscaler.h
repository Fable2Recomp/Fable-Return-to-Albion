// The C ABI the Godot side calls to run a neural upscaler.
//
// §8 of the texture modernization plan asks for an EMBEDDED implementation that keeps the model
// and the Vulkan context alive, rather than a process per texture. That is why this is an ABI
// over raw RGBA8 buffers and not a command line: everything the plan lists as the cost of the
// subprocess approach — process creation, Python startup, PNG encode, PNG decode, reinitializing
// Vulkan for every texture — is a consequence of the boundary being a file, so the boundary is
// not a file.
//
// SHAPED TO MATCH ITexUpscaler on the managed side. The Godot half already declines cleanly when
// a backend refuses, so every entry point here is allowed to fail and none of them may abort:
// refii_upscaler_create returning null is a supported outcome on a machine with no suitable
// Vulkan device, and the pipeline falls back to the built-in classical resampler.

#ifndef REFII_UPSCALER_H
#define REFII_UPSCALER_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define REFII_UPSCALER_ABI 1

/// How a filter must treat the channels. Mirrors TexFilterClass on the managed side.
///
/// The values are part of the ABI. A backend that cannot honour a class must DECLINE rather than
/// substitute one it can do — an upscaler that quietly treats a tangent-space normal as colour
/// produces a result that looks fine in isolation and lights wrongly on every surface.
typedef enum refii_upscaler_filter {
  REFII_UPSCALER_FILTER_NONE = 0,
  REFII_UPSCALER_FILTER_PERCEPTUAL = 1,
  REFII_UPSCALER_FILTER_NORMAL = 2,
  REFII_UPSCALER_FILTER_SCALAR = 3
} refii_upscaler_filter;

typedef struct refii_upscaler refii_upscaler;

/// Creates an upscaler, or returns null when this machine cannot run one.
///
/// `model_directory` holds the weights. It is NOT bundled into the repository: the weights are
/// third-party redistributable files that belong beside the build, and the same rule that keeps
/// converted game assets out of the tree applies to anything large and derived.
///
/// Null is a normal answer, not an error to report loudly. The caller falls back.
refii_upscaler* refii_upscaler_create(const char* model_directory, const char* model_name);

void refii_upscaler_destroy(refii_upscaler* upscaler);

/// Identifies the model AND its weights, for the cache key.
///
/// It must change whenever the weights do. The managed cache keys every enhanced texture on this
/// string, so a model swap that kept the same id would serve the previous model's output forever
/// and the change would read as having done nothing.
const char* refii_upscaler_id(refii_upscaler* upscaler);

/// Upscales one RGBA8 image by `scale`.
///
/// `src` is `src_width * src_height * 4` bytes, tightly packed, no mip chain — the managed side
/// strips the block compression and the chain first so that every backend does not have to, and
/// so they cannot each get it slightly differently.
///
/// `dst` is caller-allocated and must be `src_width * scale * src_height * scale * 4` bytes.
/// Returns 0 on success and non-zero to decline; declining is expected and must not log.
int refii_upscaler_run(refii_upscaler* upscaler,
                       const uint8_t* src,
                       int32_t src_width,
                       int32_t src_height,
                       int32_t scale,
                       refii_upscaler_filter filter,
                       uint8_t* dst,
                       int32_t dst_capacity);

#ifdef __cplusplus
}
#endif

#endif  // REFII_UPSCALER_H
