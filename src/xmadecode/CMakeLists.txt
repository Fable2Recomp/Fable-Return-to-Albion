# XMA1/XMA2 audio decoding for Godot, independent of the guest.
#
# A shared library so the C# side can P/Invoke it, the same shape as refii_binkdecode.
# It exports the audio ABI that used to come from refii_assetbrowser, so Godot's
# Engine_XmaDecoder.cs only had to change which library it loads.

add_library(refii_xmadecode SHARED
    src/xma_decode.cpp
)

set_target_properties(refii_xmadecode PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    # Required, or the DLL exports nothing and Godot fails with "Unable to find an entry
    # point named 'RefiiXmaDecode'" despite loading the library successfully — extern "C"
    # controls linkage, not Windows export visibility.
    WINDOWS_EXPORT_ALL_SYMBOLS ON
)

# libavcodec brings libavutil with it (PUBLIC there), and its PUBLIC include interface
# points at the FFmpeg source root, which is what the <libavcodec/...> includes need.
#
# Unlike the Bink decoder this needs no extra FFmpeg sources or SIMD stubs: rexglue
# already builds wmaprodec.c for WMA Pro, and ff_xma1_decoder/ff_xma2_decoder live in
# that same file behind CONFIG_XMA1_DECODER/CONFIG_XMA2_DECODER, which cmake/ffmpeg-bink
# turns on.
target_link_libraries(refii_xmadecode PRIVATE libavcodec)

# Staged beside the other native libraries Godot loads, into the per-architecture root so
# an x86_64 and an ARM64 build can both be present for their respective export presets.
if(REFII_STAGE_GODOT_BUILD AND DEFINED REFII_GODOT_BUILD_BIN_DIR)
    add_custom_command(TARGET refii_xmadecode POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii_xmadecode>"
            "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMENT "Staging refii_xmadecode for Godot"
    )
endif()
