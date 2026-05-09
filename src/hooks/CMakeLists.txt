# Unified native PPC hook object target.
#
# Feature modules expose hook source files and runtime libraries. This target is
# the single place that compiles those strong PPC overrides into object files for
# `refii_generated`, matching the generated weak-symbol override model.

set(REFII_HOOK_SOURCES
    ${REFII_LUA_HOOK_SOURCES}
    ${REFII_VFS_HOOK_SOURCES}
)

set(REFII_HOOK_INCLUDE_DIRS
    ${REFII_LUA_INCLUDE_DIRS}
    ${REFII_VFS_INCLUDE_DIRS}
)

set(REFII_HOOK_LIBRARIES
    ${REFII_LUA_LIBRARIES}
    ${REFII_VFS_LIBRARIES}
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

    set(REFII_HOOK_OBJECTS
        $<TARGET_OBJECTS:refii_hooks>
        PARENT_SCOPE
    )
else()
    set(REFII_HOOK_OBJECTS
        PARENT_SCOPE
    )
endif()
