# Unified native PPC hook object target.
#
# Feature modules expose hook source files and runtime libraries. This target is
# the single place that compiles those strong PPC overrides into object files for
# `refii_generated`, matching the generated weak-symbol override model.

set(REFII_HOOK_SOURCES
    ${REFII_LUA_HOOK_SOURCES}
    ${REFII_VFS_HOOK_SOURCES}
    ${REFII_RENDERER_HOOK_SOURCES}
    ${CMAKE_CURRENT_SOURCE_DIR}/src/xstorage_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/unlock_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_player_hooks.cpp
)

set(REFII_HOOK_INCLUDE_DIRS
    ${REFII_LUA_INCLUDE_DIRS}
    ${REFII_VFS_INCLUDE_DIRS}
    ${REFII_RENDERER_INCLUDE_DIRS}
)

set(REFII_HOOK_LIBRARIES
    ${REFII_LUA_LIBRARIES}
    ${REFII_VFS_LIBRARIES}
    ${REFII_RENDERER_LIBRARIES}
)

list(FILTER REFII_HOOK_SOURCES EXCLUDE REGEX "^$")

if(REFII_HOOK_SOURCES)
    add_library(refii_hooks OBJECT
        ${REFII_HOOK_SOURCES}
    )
    set_target_properties(refii_hooks PROPERTIES
        POSITION_INDEPENDENT_CODE ON
    )
    target_include_directories(refii_hooks
        PRIVATE
            ${REFII_HOOK_INCLUDE_DIRS}
    )
    target_link_libraries(refii_hooks
        PRIVATE
            ${REFII_HOOK_LIBRARIES}
            rex::runtime
    )

    # THE PAYLOAD LOGS THROUGH C. See the shadowed rex/logging/macros.h: under this define
    # REX_LOG_IMPL formats with fmt payload-side and hands the host a finished char*, so no
    # spdlog::logger* and no fmt arg pack crosses a boundary the player's compiler built.
    target_compile_definitions(refii_hooks PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)

    # NO TRACY IN THE PAYLOAD. TRACY_ENABLE arrives as an INTERFACE define from rex::runtime,
    # so it cannot be dropped by not asking for it — -U undoes it after the fact (compile
    # options are emitted after definitions). ZoneScoped and friends then compile to nothing,
    # which is what a shipped payload wants anyway: the profiler is a dev instrument, and its
    # seven C++ entries would otherwise cross a boundary the player's compiler built.
    target_compile_options(refii_hooks PRIVATE "-UTRACY_ENABLE" "-UTRACY_MANUAL_LIFETIME" "-UTRACY_DELAYED_INIT" "-UTRACY_ON_DEMAND" "-UTRACY_FIBERS" "-UTRACY_IMPORTS" "-UREXGLUE_ENABLE_PROFILING")

    # rex_bridge_payload.cpp DEFINES the rex:: symbols librexruntimed.so also defines, so its
    # copies MUST NOT be exported. The payload is dlopen'd RTLD_GLOBAL, which puts the global
    # scope ahead of librefii_vfs.so's own link group: an exported copy captures the host's
    # lazily-bound reference, and refii_bridge_rex_* -> rex::* -> refii_bridge_rex_* recurses
    # until the stack ends. Hidden makes them module-internal, which is the contract the file
    # header states. A visibility PRAGMA is not enough — out-of-line member definitions inherit
    # visibility from the class, so it has to be the compile option.
    if(NOT MSVC)
        set(_refii_payload_internal_sources ${REFII_HOOK_SOURCES})
        list(FILTER _refii_payload_internal_sources INCLUDE REGEX "rex_bridge_payload\\.cpp$")
        if(_refii_payload_internal_sources)
            set_source_files_properties(${_refii_payload_internal_sources}
                PROPERTIES COMPILE_OPTIONS "-fvisibility=hidden"
            )
        endif()
    endif()
    if(REFII_RENDERER_LIBRARIES)
        target_compile_definitions(refii_hooks
            PRIVATE
                REFII_VFS_HAS_RENDERER_RUNTIME=1
        )
    endif()

    set(REFII_HOOK_OBJECTS
        $<TARGET_OBJECTS:refii_hooks>
        PARENT_SCOPE
    )

    # THE PAYLOAD/HOST ABI RULE, CHECKED RATHER THAN REMEMBERED.
    #
    # payload_bridge.h states it and src/vfs/CMakeLists.txt restates it, and it was still
    # broken seven times, because Linux cannot see it being broken: both ends are Itanium
    # there, so a hook calling straight into refii_vfs links and runs. It surfaces only on
    # Windows, at the end of the player's install-time build. See
    # cmake/refii_check_payload_abi.cmake.
    #
    # Off with -DREFII_CHECK_PAYLOAD_ABI=OFF, and skipped when no llvm-nm is present: a guard
    # rail must not be the reason a build cannot run.
    option(REFII_CHECK_PAYLOAD_ABI
        "Fail the build if a payload hook imports a C++ symbol from a host library" ON)

    find_program(REFII_NM NAMES llvm-nm llvm-nm-20 llvm-nm-19 llvm-nm-18)
    find_program(REFII_CXXFILT NAMES llvm-cxxfilt llvm-cxxfilt-20 llvm-cxxfilt-19 c++filt)
    find_program(REFII_UNDNAME NAMES llvm-undname)

    if(REFII_CHECK_PAYLOAD_ABI AND NOT REFII_NM)
        message(STATUS
            "Refii: no llvm-nm found - the payload/host ABI check is skipped. A hook that "
            "imports a C++ symbol from refii_vfs will not be caught until the Windows payload "
            "link fails.")
    elseif(REFII_CHECK_PAYLOAD_ABI)
        # $<CONFIG> IS IN THE NAME. A multi-config generator evaluates this once per
        # configuration and file(GENERATE) refuses one path with two bodies -- the same wall
        # payload_include_dirs hit.
        set(_refii_abi_objects
            "${CMAKE_BINARY_DIR}/payload_abi_objects_$<CONFIG>.txt")
        file(GENERATE OUTPUT "${_refii_abi_objects}"
             CONTENT "$<JOIN:$<TARGET_OBJECTS:refii_hooks>,\n>\n")

        # Stamped, so it re-runs only when a hook object changed. A bare custom target would
        # re-read every object on every build.
        set(_refii_abi_stamp "${CMAKE_CURRENT_BINARY_DIR}/payload_abi_checked_$<CONFIG>")
        add_custom_command(
            OUTPUT "${_refii_abi_stamp}"
            COMMAND ${CMAKE_COMMAND}
                -DOBJECT_LIST=${_refii_abi_objects}
                -DNM=${REFII_NM}
                -DCXXFILT=${REFII_CXXFILT}
                -DUNDNAME=${REFII_UNDNAME}
                -P "${CMAKE_SOURCE_DIR}/cmake/refii_check_payload_abi.cmake"
            COMMAND ${CMAKE_COMMAND} -E touch "${_refii_abi_stamp}"
            DEPENDS $<TARGET_OBJECTS:refii_hooks> "${_refii_abi_objects}"
            COMMENT "Checking the payload imports no C++ symbol from a host library"
            VERBATIM
        )
        add_custom_target(refii_payload_abi_check ALL DEPENDS "${_refii_abi_stamp}")
        add_dependencies(refii_payload_abi_check refii_hooks)
    endif()

    # The same objects as an archive, for the install-time payload build to link.
    #
    # It cannot consume $<TARGET_OBJECTS:> — it is a separate CMake project on the
    # player's machine — and it must end up with EXACTLY this object set, which is why it
    # links this whole-archive. These hooks are strong overrides of functions the
    # generator emits weak, so a weak definition satisfies nothing and a normal archive
    # link would pull in none of them.
    add_library(refii_hooks_archive STATIC $<TARGET_OBJECTS:refii_hooks>)
    set_target_properties(refii_hooks_archive PROPERTIES
        OUTPUT_NAME refii_hooks
        LINKER_LANGUAGE CXX
        POSITION_INDEPENDENT_CODE ON
        ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    )
else()
    set(REFII_HOOK_OBJECTS
        PARENT_SCOPE
    )
endif()
