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

#ifdef __cplusplus
extern "C" {
#endif

XENOSRECOMP_API bool XenosRecompTranslateToHlsl(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    const char** errorMessage);

#ifdef __cplusplus
}
#endif
