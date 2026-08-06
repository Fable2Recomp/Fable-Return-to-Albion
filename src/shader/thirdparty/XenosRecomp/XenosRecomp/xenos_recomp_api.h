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

#ifdef __cplusplus
}
#endif
