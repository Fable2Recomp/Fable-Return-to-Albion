set(REFII_EXTRACTOR_ROOT ${CMAKE_CURRENT_LIST_DIR})

find_package(ZLIB REQUIRED)

add_library(refii_bnk_extractor SHARED
    ${REFII_EXTRACTOR_ROOT}/src/bnk_extractor.cpp
)

target_include_directories(refii_bnk_extractor
    PUBLIC
        ${REFII_EXTRACTOR_ROOT}/include
)

target_link_libraries(refii_bnk_extractor
    PUBLIC
        ZLIB::ZLIB
)

set_target_properties(refii_bnk_extractor PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    CXX_EXTENSIONS OFF
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_bnk_extractor
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32)
    set_target_properties(refii_bnk_extractor PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
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
endif()
