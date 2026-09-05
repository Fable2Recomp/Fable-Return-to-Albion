set(REFII_ACHIEVEMENT_ROOT ${CMAKE_CURRENT_LIST_DIR})

# The guest-facing seam only: the kernel hooks write into it, the host bridge reads out of it.
# Definitions, the store, persistence and every pixel live in Godot (Engine_Achievements).
add_library(refii_achievement STATIC
    ${REFII_ACHIEVEMENT_ROOT}/src/achievement_runtime.cpp
)
set_target_properties(refii_achievement PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)
target_include_directories(refii_achievement
    PUBLIC
        ${REFII_ACHIEVEMENT_ROOT}/include
)
