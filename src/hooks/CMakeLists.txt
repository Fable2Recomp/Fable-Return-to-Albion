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
