# Fable 3's VFS — the title library the guest's file-system hooks ask what the host mount
# overrides (Fable3/src/hooks/src/vfs_hooks.cpp; the C ABI in include/fable3_vfs.h).
#
# Fable 3's counterpart of Fable 2's refii_vfs: target refiii_vfs (refiii is Fable 3's key),
# file librefii_vfs in its own output directory so it does not collide with Fable 2's, staged to
# bin/Fable3/ (REFII_TITLE_INSTANCE_TARGETS_Fable3) and linked by the module the launcher builds
# when Engine_Title.Fable3's instance names it (GameCodeBuild). The host's statements it answers
# from — the hash index, override query and reader — are refii_runtime's, which it links.

add_library(refiii_vfs SHARED
    # The guest's resource hash to the host mount's override, and each hit's log line.
    ${CMAKE_CURRENT_LIST_DIR}/src/host_overrides.cpp
    # Which build is running (TU3 or disc), stated by the payload, and the TU3 -> disc address
    # table the hooks and the host bridge translate through (include/fable3_guest_build.h).
    ${CMAKE_CURRENT_LIST_DIR}/src/guest_build.cpp
)

target_include_directories(refiii_vfs
    PUBLIC
        ${CMAKE_CURRENT_LIST_DIR}/include
)

target_link_libraries(refiii_vfs
    PUBLIC
        refii_runtime
        rex::runtime
)

# RPATH from refii_apply_origin_rpath in the top-level CMakeLists: a title instance looks one
# directory up, where the shared natives it links are staged.
set_target_properties(refiii_vfs PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_vfs
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}/Fable3"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}/Fable3"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}/Fable3"
)

if(NOT WIN32)
    target_link_options(refiii_vfs PRIVATE -Wl,--no-undefined)
endif()

if(WIN32)
    set_target_properties(refiii_vfs PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()
