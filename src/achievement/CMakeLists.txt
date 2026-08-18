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
        # stb_image.h, vendored. It used to come from src/launcher/vendors/stb — a submodule of
        # the ImGui launcher, retired 2026-08-18, which took the only copy in the tree with it.
        # The SDK grew its own thirdparty/stb, but not until v0.10.0: the pin here is v0.8.0, so
        # ${REFII_REXGLUE_SDK_SOURCE_DIR}/thirdparty/stb does not exist and this target cannot
        # take it from there yet. thirdparty/stb/stb_image.h is the retired submodule's own file
        # at its recorded pin (904aa67e, nothings/stb — see archive/retired-submodules.txt), so
        # this is the same header the code was written against, not a newer one.
        ${REFII_ACHIEVEMENT_ROOT}/thirdparty/stb
)
target_link_libraries(refii_achievement_ui
    PUBLIC
        refii_achievement
    PRIVATE
        rex::runtime
        imgui::imgui
)
