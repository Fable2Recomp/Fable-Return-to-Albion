set(REFII_ACHIEVEMENT_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_ACHIEVEMENT_INCLUDE_DIRS
    ${REFII_ACHIEVEMENT_ROOT}/include
)

set(REFII_ACHIEVEMENT_SOURCES
    ${REFII_ACHIEVEMENT_ROOT}/src/achievement_runtime.cpp
)

set(REFII_ACHIEVEMENT_UI_SOURCES
    ${REFII_ACHIEVEMENT_ROOT}/src/achievement_fable_texture_codec.cpp
    ${REFII_ACHIEVEMENT_ROOT}/src/achievement_imgui.cpp
    ${REFII_ACHIEVEMENT_ROOT}/src/achievement_stb_image.cpp
)

add_library(refii_achievement STATIC
    ${REFII_ACHIEVEMENT_SOURCES}
)
set_target_properties(refii_achievement PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)
target_include_directories(refii_achievement
    PUBLIC
        ${REFII_ACHIEVEMENT_INCLUDE_DIRS}
)

add_library(refii_achievement_ui STATIC
    ${REFII_ACHIEVEMENT_UI_SOURCES}
)
set_target_properties(refii_achievement_ui PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)
target_include_directories(refii_achievement_ui
    PUBLIC
        ${REFII_ACHIEVEMENT_INCLUDE_DIRS}
    PRIVATE
        ${CMAKE_SOURCE_DIR}/src/launcher/vendors/stb
)
target_link_libraries(refii_achievement_ui
    PUBLIC
        refii_achievement
    PRIVATE
        rex::runtime
        imgui::imgui
)
