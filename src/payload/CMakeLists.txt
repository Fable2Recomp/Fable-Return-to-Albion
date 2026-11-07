# Host-side loader for the recompiled guest payload.
#
# STATIC and dependency-free on purpose: both refii_core and refii_vfs need it, and
# refii_vfs does not link refii_core. A shared library here would put a third module in
# the load order for the sake of one dlopen.

set(REFII_PAYLOAD_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_payload STATIC
    ${REFII_PAYLOAD_ROOT}/src/payload_loader.cpp
)

set_target_properties(refii_payload PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)

target_include_directories(refii_payload
    PUBLIC
        ${REFII_PAYLOAD_ROOT}/include
)

target_link_libraries(refii_payload
    PUBLIC
        rex::runtime
)

if(NOT WIN32)
    target_link_libraries(refii_payload PUBLIC ${CMAKE_DL_LIBS})
endif()

# Compiled INTO the payload, not into this target — by our build in the dev tree and by
# the install-time project on the player's machine, from this same file.
set(REFII_PAYLOAD_EXPORT_SOURCE
    ${REFII_PAYLOAD_ROOT}/src/payload_export.cpp
    PARENT_SCOPE
)

# THE PAYLOAD'S SHARED SOURCES, compiled into every title's module beside the generated code
# and payload_export.cpp. A title with no hooks of its own builds its module from these alone.
#
#   rex_bridge_payload.cpp   payload-side definitions of the rex:: symbols the generated code
#                            calls, forwarding to the C entries in core/rex_payload_bridge.h
#                            (defined in refii_runtime). Hidden — see the file header.
#   guest_yield_backoff.cpp  the back-off policy for the guest's SwitchToThread spin waits,
#                            keyed on the link register, never on a call site. Payload-side: the
#                            payload must not import a C++ symbol from a host library.
#   headless_presentation.cpp the headless Gameface core (core/headless_presentation.h): a
#                            presentation made without its .bgf scene, its elements made from the
#                            file's element table, their attributes, flags and slides answered as
#                            the file authored them and as the guest last wrote them, and the
#                            statements told to them. Title-neutral: each title hands it its
#                            Gameface's addresses and offsets (a GamefaceTable) from its own hooks.
#
# Staged for the install-time build as recompile/payload_core_sources.cmake
# (REFII_PAYLOAD_CORE_SOURCES), separately from a title's hook_sources.cmake.
set(REFII_PAYLOAD_CORE_SOURCES
    ${REFII_PAYLOAD_ROOT}/src/core/rex_bridge_payload.cpp
    ${REFII_PAYLOAD_ROOT}/src/core/guest_yield_backoff.cpp
    ${REFII_PAYLOAD_ROOT}/src/core/headless_presentation.cpp
)

add_library(refii_payload_core OBJECT ${REFII_PAYLOAD_CORE_SOURCES})
set_target_properties(refii_payload_core PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)
# The runtime's include directory AHEAD of the SDK's: it carries the payload's
# rex/thread/mutex.h, which must win over the SDK header of the same name.
target_include_directories(refii_payload_core
    PRIVATE
        ${REFII_RUNTIME_INCLUDE_DIRS}
)
target_link_libraries(refii_payload_core
    PRIVATE
        rex::runtime
)
# The payload logs through C (see the shadowed rex/logging/macros.h) and carries no profiler:
# TRACY_ENABLE arrives as an INTERFACE define from rex::runtime, so -U undoes it.
target_compile_definitions(refii_payload_core PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)
if(NOT MSVC)
    target_compile_options(refii_payload_core PRIVATE "-UTRACY_ENABLE" "-UTRACY_MANUAL_LIFETIME" "-UTRACY_DELAYED_INIT" "-UTRACY_ON_DEMAND" "-UTRACY_FIBERS" "-UTRACY_IMPORTS" "-UREXGLUE_ENABLE_PROFILING")
    # rex_bridge_payload.cpp DEFINES rex:: symbols librexruntime also defines; exported, the
    # payload's copies would capture the host's references (it is dlopen'd RTLD_GLOBAL) and
    # refii_bridge_rex_* -> rex::* -> refii_bridge_rex_* recurses. A visibility pragma is not
    # enough: out-of-line members inherit the class's visibility, so it is the compile option.
    set_source_files_properties(${REFII_PAYLOAD_ROOT}/src/core/rex_bridge_payload.cpp
        PROPERTIES COMPILE_OPTIONS "-fvisibility=hidden"
    )
endif()

set(REFII_PAYLOAD_CORE_SOURCES
    ${REFII_PAYLOAD_CORE_SOURCES}
    PARENT_SCOPE
)
