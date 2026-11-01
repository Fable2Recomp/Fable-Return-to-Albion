# The host bridge's shared core — the C ABI Godot binds, as far as it is the same for every title.
#
# STATIC, PIC, and linked WHOLE into each title's librefii_hostbridge (refii_link_hostcore below):
# nothing in the title's own feeds references most of these exports, so an ordinary archive link
# would drop them. A title's bridge is this core plus its feeds and its TitleCallbacks
# (include/host_core.h), and exports exactly the same core set whatever the title.
#
# Links only shared libraries: refii_host (the app), refii_runtime (the shared state), the
# runtime and the achievement seam's header.

set(REFII_HOSTCORE_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_hostcore STATIC
    # Session state and the guarded guest-memory reads every export is built on.
    ${REFII_HOSTCORE_ROOT}/src/guest_access.cpp
    # The crash record and the last-resort fault handlers.
    ${REFII_HOSTCORE_ROOT}/src/crash_handlers.cpp
    # The memory probe, both boot paths, the step and the shutdown, and the title's part in them.
    ${REFII_HOSTCORE_ROOT}/src/lifecycle.cpp
    # The ABI version, save root, player state, asset reader, mod API, audio, input, time,
    # achievements and rumble.
    ${REFII_HOSTCORE_ROOT}/src/host_exports.cpp
    # The debug channel's guest-independent half.
    ${REFII_HOSTCORE_ROOT}/src/debug_channel.cpp
    # RexNet for the shell.
    ${REFII_HOSTCORE_ROOT}/src/rexnet.cpp
    ${REFII_HOSTCORE_ROOT}/src/host_tracy.cpp
    ${REFII_HOSTCORE_ROOT}/src/hang_watchdog.cpp
    # The guest's audio output as a Godot audio effect. The bridge library is ALSO the
    # GDExtension Godot loads for it — the same file .NET holds — so the effect reads the ring
    # in-process.
    ${REFII_HOSTCORE_ROOT}/src/godot_audio_effect.cpp
)

set_target_properties(refii_hostcore PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    # Linked into a SHARED library Godot P/Invokes.
    POSITION_INDEPENDENT_CODE ON
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)

# host_core.h, host_abi.h and the other bridge headers, for the title's feeds as well.
# Godot's extension interface, dumped from the 4.7.1 editor (`--dump-gdextension-interface`),
# is under include/godot: header only, the extension talks to Godot through the procedure table
# it is handed.
target_include_directories(refii_hostcore PUBLIC ${REFII_HOSTCORE_ROOT}/include)

# The SDK's 5.1 fold is SSSE3 on x86-64, the flag the SDK's own audio objects get.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64|amd64" AND NOT MSVC)
    set_source_files_properties(${REFII_HOSTCORE_ROOT}/src/godot_audio_effect.cpp
        PROPERTIES COMPILE_OPTIONS "-mssse3")
endif()

# refii_host carries RunRefiiEmbedded and pulls in refii_core: the runtime the guest runs in.
target_link_libraries(refii_hostcore
    PUBLIC
        refii_host
        refii_runtime
        # Carries the achievement seam: its one instance lives in rexruntime, and linking the
        # seam here as well gave the bridge a second, empty copy of the state.
        rex::runtime
)

# The seam's header only — see src/achievement/CMakeLists.txt.
target_include_directories(refii_hostcore PUBLIC
    $<TARGET_PROPERTY:refii_achievement,INTERFACE_INCLUDE_DIRECTORIES>)

# SDL headers: the stepped loop drains the event queue itself rather than handing a
# thread to the guest's blocking SDL_WaitEvent. rex::runtime carries the linkage; this
# is only for the declarations.
if(TARGET SDL3::SDL3)
    target_link_libraries(refii_hostcore PUBLIC SDL3::SDL3)
endif()

# A title's host bridge: `target` (SHARED, OUTPUT_NAME refii_hostbridge) gets the whole core, the
# no-dangling-symbols rule, and the language level the core is built at. The title adds its feeds.
#
# NO DANGLING SYMBOLS. The bridge is dlopen'd by .NET with lazy binding, so a reference the link
# could not resolve is not a link error — it is `ld.so` ending the whole process with "symbol
# lookup error" (exit 127) the first time that call executes, with no signal, no handler and
# nothing in any log. That was four "crashes to desktop" in one afternoon. Resolve everything at
# link time instead; a weak reference (the renderer's optional ImGui hook) still resolves to null
# and is allowed.
#
# SDL3 DIRECTLY AFTER THE CORE, ahead of rexruntime on the link line. The SDL target is a static
# archive, and placed there it resolves the stepped loop's SDL_PollEvent from the archive into
# the bridge itself rather than from librexruntime's copy, and the bridge exports that copy's
# symbols — exactly the resolution and the export set the bridge had as one library.
function(refii_link_hostcore target)
    target_link_libraries(${target} PRIVATE "$<LINK_LIBRARY:WHOLE_ARCHIVE,refii_hostcore>")
    if(TARGET SDL3::SDL3)
        target_link_libraries(${target} PRIVATE SDL3::SDL3)
    endif()
    if(NOT WIN32)
        target_link_options(${target} PRIVATE -Wl,--no-undefined)
    endif()
    set_target_properties(${target} PROPERTIES
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        OUTPUT_NAME refii_hostbridge
    )
endfunction()
