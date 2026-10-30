#pragma once

#ifdef _WIN32
#    ifdef XENOSRECOMP_API_EXPORT
#        define XENOSRECOMP_API __declspec(dllexport)
#    else
#        define XENOSRECOMP_API __declspec(dllimport)
#    endif
#else
#    define XENOSRECOMP_API
#endif

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Xenos memory export (memexport / GPU stream-out) facts recovered while
// recompiling one shader. These exist so a dropped export becomes a loud
// artifact in the translator report instead of a silent no-op.
typedef struct XenosRecompTranslationStats {
    // 1 when the shader writes any eM# export data register - the same
    // condition the ReXGlue SDK uses to enable its memexport path.
    uint32_t memexportEnabled;
    // Distinct stream constants recovered from `mad eA, r#, const0100, c#`.
    uint32_t memexportStreams;
    // Bit i set when eM<i> is written somewhere in the shader.
    uint32_t memexportDataRegisterMask;
    // eA writes that did not match the recognised idiom. Non-zero means the
    // destination stream could not be identified statically.
    uint32_t memexportUnresolvedAddressWrites;
} XenosRecompTranslationStats;

XENOSRECOMP_API bool XenosRecompTranslateToHlsl(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    const char** errorMessage);

XENOSRECOMP_API bool XenosRecompTranslateToHlslEx(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    XenosRecompTranslationStats* stats,
    const char** errorMessage);

// What the recompiler writes: HLSL for dxc, or Godot's shading language (one .gdshaderinc per
// blob, for the classic renderer's materials).
#define XENOSRECOMP_DIALECT_HLSL 0
#define XENOSRECOMP_DIALECT_GODOT 1

// Single-blob translation in the given dialect. The shader common header is read only for
// HLSL; the Godot output includes its own support file by resource path.
//
// `title` is the title folder the blob came from ("Fable2", "Fable3"; NULL or empty for none):
// the Godot dialect classifies a few constants by it, where two titles state the same engine's
// shaders with different per-draw blocks. HLSL ignores it.
XENOSRECOMP_API bool XenosRecompTranslateEx(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    uint32_t dialect,
    const char* title,
    XenosRecompTranslationStats* stats,
    const char** errorMessage);

#ifdef __cplusplus
}
#endif
