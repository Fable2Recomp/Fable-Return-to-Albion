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
