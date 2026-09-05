# Shared modding policy and launcher-facing settings.

set(REFII_MODDING_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_modding STATIC
    ${REFII_MODDING_ROOT}/src/runtime_settings.cpp
)

target_include_directories(refii_modding
    PUBLIC
        ${REFII_MODDING_ROOT}/include
)

set_target_properties(refii_modding PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)
