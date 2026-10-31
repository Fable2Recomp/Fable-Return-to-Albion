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
    # The guest tick pump: the host's posted work drained once per HF step, and the step timed.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/game_tick_pump.cpp
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
    # No object added to a render scene the 3D Engine has already destroyed.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/scene_guard.cpp
    # The world: a level's up and down, each effect's creation, placement and end, each decal.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/world_hooks.cpp
    # Each character's appearance: the morph target lists and texture commands its draw virtual
    # hands the renderer, stated per entity.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/appearance_hooks.cpp
    # Each cloth piece's drawn vertices as the guest's draw publishes them to its vertex buffer,
    # and the piece's end.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/cloth_hooks.cpp
    # The mouse's turn, handed whole to the active camera mode around the camera manager's update.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mouse_look.cpp
    # The per-tick Lua GC slice, run against the real clock instead of the host-granted time base.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/lua_gc_slice.cpp
    # The CRT's pow/sin/cos/... answered by the host's libm.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/crt_math.cpp
    # The root file system's Open and Exists: the host mount's override served as a guest memory
    # file (the decision is Fable3/src/vfs's, refiii_vfs). Its C ABI header is listed so the
    # staging ships it, laid out relative to Fable3/src as the hook includes it.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/vfs_hooks.cpp
    ${CMAKE_SOURCE_DIR}/Fable3/src/vfs/include/fable3_vfs.h
    # The loading screen's quads cut (Godot draws the screen); its state machine runs unchanged.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/loading_screen_scene.cpp
    # What the UI hooks share (the guest's two string classes, the statement post). Listed with
    # the sources so the staging ships it beside them: the player's build compiles the staged
    # copies, and a header only in the tree is not there.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_statements.h
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
