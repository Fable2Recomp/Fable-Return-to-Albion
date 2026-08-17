# Bink video decoding for Godot, independent of the guest.
#
# A shared library so the C# side can P/Invoke it, the same shape as refii_assetbrowser
# and refii_launcher_ops.

add_library(refii_binkdecode SHARED
    src/bink_decode.cpp
    src/ffmpeg_arch_stubs.cpp
)

set_target_properties(refii_binkdecode PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    # WITHOUT THIS THE DLL EXPORTS NOTHING. extern "C" is linkage, not visibility, so on
    # Windows every RefiiBink* entry point stayed internal and Godot failed at the first
    # call with "Unable to find an entry point named 'RefiiBinkOpen' in DLL
    # 'refii_binkdecode'" — the library loads fine, which is what makes it confusing.
    # Same property the other Godot-loaded natives use (extractor, lua, renderer, shader).
    WINDOWS_EXPORT_ALL_SYMBOLS ON
)

# libavcodec brings libavutil with it (PUBLIC there), and its PUBLIC include interface
# points at the FFmpeg source root, which is what the <libavcodec/...> includes need.
target_link_libraries(refii_binkdecode PRIVATE libavcodec)

# Staged beside the other native libraries Godot loads, into the per-architecture root so
# an x86_64 and an ARM64 build can both be present for their respective export presets.
if(REFII_STAGE_GODOT_BUILD AND DEFINED REFII_GODOT_BUILD_BIN_DIR)
    add_custom_command(TARGET refii_binkdecode POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii_binkdecode>"
            "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMENT "Staging refii_binkdecode for Godot"
    )
endif()
