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
)

# libavcodec brings libavutil with it (PUBLIC there), and its PUBLIC include interface
# points at the FFmpeg source root, which is what the <libavcodec/...> includes need.
target_link_libraries(refii_binkdecode PRIVATE libavcodec)

# Staged beside the other native libraries Godot loads.
add_custom_command(TARGET refii_binkdecode POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${CMAKE_SOURCE_DIR}/Godot/build/bin"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:refii_binkdecode>"
        "${CMAKE_SOURCE_DIR}/Godot/build/bin/"
    COMMENT "Staging refii_binkdecode for Godot"
)
