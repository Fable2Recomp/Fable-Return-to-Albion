# Fable 3's native PPC hooks: strong overrides of functions the generator emits weak, compiled
# into refiii_generated. The dev build compiles them here so a hook that does not build fails
# now; the launcher compiles the staged copies (bin/recompile/Fable3/hooks) into the player's
# module, with the same definitions (GameCodeBuild's payload flags).

set(REFIII_HOOK_SOURCES
    # The device's no-GPU bit, armed as the ring buffer comes up.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/boot_no_gpu.cpp
    # The Bink players' open, rect, close and the startup player's skip byte, stated for the
    # host's native playback (core/movie_state.h).
    ${CMAKE_CURRENT_SOURCE_DIR}/src/movie_player.cpp
    # The present paced to the guest's stated vblank, where the 3D Engine loop holds no lock.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/present_pacing.cpp
    # The "Press START" splash's state, stated for the host's native screen.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/splash_screen.cpp
    # The camera the guest renders the world with, and its preload gate held while Godot builds.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/view_feed.cpp
    # The UI cut's seam: the `GUI.*` natives the game VM calls, with their arguments, for the host.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_natives.cpp
    # The Gameface elements the guest writes (resolver + state/opacity/text setters), stated by path.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_element_hooks.cpp
    # The display boxes and the menu box, stated as the box manager shows and drops them.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/display_box_hooks.cpp
)

add_library(refiii_hooks OBJECT ${REFIII_HOOK_SOURCES})
set_target_properties(refiii_hooks PROPERTIES POSITION_INDEPENDENT_CODE ON)

# The shared runtime's include directory ahead of the SDK's, as Fable 2's hooks have it: it
# carries the payload's rex/thread/mutex.h and the C logging bridge.
target_include_directories(refiii_hooks PRIVATE ${REFII_RUNTIME_INCLUDE_DIRS})
target_link_libraries(refiii_hooks PRIVATE rex::runtime)

# Payload-shaped, as Fable2/src/hooks/CMakeLists.txt explains: logging crosses as C, no Tracy.
target_compile_definitions(refiii_hooks PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)
target_compile_options(refiii_hooks PRIVATE "-UTRACY_ENABLE" "-UTRACY_MANUAL_LIFETIME" "-UTRACY_DELAYED_INIT" "-UTRACY_ON_DEMAND" "-UTRACY_FIBERS" "-UTRACY_IMPORTS" "-UREXGLUE_ENABLE_PROFILING")

# THE PAYLOAD/HOST ABI RULE, checked for these hooks as for Fable 2's (cmake/refii_check_payload_abi.cmake):
# a hook importing a C++ symbol from a host library links on Linux and fails the player's
# Windows build. REFII_NM is found by Fable 2's hooks, configured first.
if(REFII_CHECK_PAYLOAD_ABI AND REFII_NM)
    set(_refiii_abi_objects "${CMAKE_BINARY_DIR}/payload_abi_objects_refiii_$<CONFIG>.txt")
    file(GENERATE OUTPUT "${_refiii_abi_objects}"
         CONTENT "$<JOIN:$<TARGET_OBJECTS:refiii_hooks>,\n>\n")
    set(_refiii_abi_stamp "${CMAKE_CURRENT_BINARY_DIR}/payload_abi_checked_$<CONFIG>")
    add_custom_command(
        OUTPUT "${_refiii_abi_stamp}"
        COMMAND ${CMAKE_COMMAND}
            -DOBJECT_LIST=${_refiii_abi_objects}
            -DNM=${REFII_NM}
            -DCXXFILT=${REFII_CXXFILT}
            -DUNDNAME=${REFII_UNDNAME}
            -P "${CMAKE_SOURCE_DIR}/cmake/refii_check_payload_abi.cmake"
        COMMAND ${CMAKE_COMMAND} -E touch "${_refiii_abi_stamp}"
        DEPENDS $<TARGET_OBJECTS:refiii_hooks> "${_refiii_abi_objects}"
        COMMENT "Checking Fable 3's hooks import no C++ symbol from a host library"
        VERBATIM
    )
    add_custom_target(refiii_payload_abi_check ALL DEPENDS "${_refiii_abi_stamp}")
    add_dependencies(refiii_payload_abi_check refiii_hooks)
endif()
