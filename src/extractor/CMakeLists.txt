set(REFII_EXTRACTOR_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_EXTRACTOR_THIRDPARTY_ROOT "${REFII_EXTRACTOR_ROOT}/thirdparty")
set(REFII_EXTRACTOR_ZLIB_ROOT "${REFII_EXTRACTOR_THIRDPARTY_ROOT}/zlib")
set(REFII_EXTRACTOR_ZLIB_TARGET)

if(TARGET ZLIB::ZLIB)
    set(REFII_EXTRACTOR_ZLIB_TARGET ZLIB::ZLIB)
elseif(EXISTS "${REFII_EXTRACTOR_ZLIB_ROOT}/CMakeLists.txt")
    set(ZLIB_BUILD_SHARED OFF CACHE BOOL "" FORCE)
    set(ZLIB_BUILD_STATIC ON CACHE BOOL "" FORCE)
    set(ZLIB_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
    set(ZLIB_BUILD_TESTING OFF CACHE BOOL "" FORCE)
    add_subdirectory(
        "${REFII_EXTRACTOR_ZLIB_ROOT}"
        "${CMAKE_CURRENT_BINARY_DIR}/thirdparty/zlib"
        EXCLUDE_FROM_ALL
    )

    if(TARGET ZLIB::ZLIB)
        set(REFII_EXTRACTOR_ZLIB_TARGET ZLIB::ZLIB)
    elseif(TARGET zlibstatic)
        add_library(ZLIB::ZLIB ALIAS zlibstatic)
        set(REFII_EXTRACTOR_ZLIB_TARGET ZLIB::ZLIB)
    elseif(TARGET zlib)
        add_library(ZLIB::ZLIB ALIAS zlib)
        set(REFII_EXTRACTOR_ZLIB_TARGET ZLIB::ZLIB)
    else()
        message(FATAL_ERROR
            "Vendored zlib was found at ${REFII_EXTRACTOR_ZLIB_ROOT} "
            "but no zlib CMake target is available.")
    endif()
    if(TARGET zlibstatic)
        set_target_properties(zlibstatic PROPERTIES POSITION_INDEPENDENT_CODE ON)
    endif()
    if(TARGET zlib)
        set_target_properties(zlib PROPERTIES POSITION_INDEPENDENT_CODE ON)
    endif()
else()
    message(FATAL_ERROR
        "Missing vendored zlib at ${REFII_EXTRACTOR_ZLIB_ROOT}; "
        "the extractor now requires the in-repo copy.")
endif()

add_library(refii_bnk_extractor SHARED
    ${REFII_EXTRACTOR_ROOT}/src/bnk_extractor.cpp
    ${REFII_EXTRACTOR_ROOT}/src/default_xex_icon_extractor.cpp
)

target_include_directories(refii_bnk_extractor
    PUBLIC
        ${REFII_EXTRACTOR_ROOT}/include
)

target_link_libraries(refii_bnk_extractor
    PUBLIC
        ${REFII_EXTRACTOR_ZLIB_TARGET}
)

set_target_properties(refii_bnk_extractor PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    CXX_EXTENSIONS OFF
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_bnk_extractor
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32)
    set_target_properties(refii_bnk_extractor PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

add_executable(refii-default-xex-icon-extractor
    ${REFII_EXTRACTOR_ROOT}/src/default_xex_icon_extractor_main.cpp
)
target_link_libraries(refii-default-xex-icon-extractor
    PRIVATE
        refii_bnk_extractor
)
set_target_properties(refii-default-xex-icon-extractor PROPERTIES
    OUTPUT_NAME refii-default-xex-icon-extractor
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN/../lib"
    INSTALL_RPATH "$ORIGIN/../lib"
)

if(WIN32)
    refii_stage_windows_runtime_dlls(refii-default-xex-icon-extractor)
endif()

if(REFII_BUILD_TOOLS)
    add_executable(refii-bnk-extractor
        ${REFII_EXTRACTOR_ROOT}/src/bnk_extractor_main.cpp
    )
    target_link_libraries(refii-bnk-extractor
        PRIVATE
            refii_bnk_extractor
    )
    set_target_properties(refii-bnk-extractor PROPERTIES
        OUTPUT_NAME refii-bnk-extractor
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
        BUILD_RPATH "$ORIGIN/../lib"
        INSTALL_RPATH "$ORIGIN/../lib"
    )

    if(WIN32)
        refii_stage_windows_runtime_dlls(refii-bnk-extractor)
    endif()
endif()
