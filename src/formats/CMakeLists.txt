# Lionhead data formats every title shares: the .gdb and .babel <-> CSV codecs.
#
# A FORMAT, NOT A GAME. Fable 3's globals.gdb parses with the same converter as Fable 2's, so
# the codecs live here and both the extractor and a title's runtime link them; nothing here
# names a title.

set(REFII_FORMATS_ROOT ${CMAKE_CURRENT_LIST_DIR})

add_library(refii_formats STATIC
    ${REFII_FORMATS_ROOT}/src/gdb/gdb_database.cpp
    ${REFII_FORMATS_ROOT}/src/babel/babel_database.cpp
)
target_include_directories(refii_formats
    PUBLIC
        ${REFII_FORMATS_ROOT}/include
)

# Babel blocks are deflate streams. The extractor provides ZLIB::ZLIB (its vendored copy),
# so this directory is added after it.
target_link_libraries(refii_formats PUBLIC ZLIB::ZLIB)

set_target_properties(refii_formats PROPERTIES POSITION_INDEPENDENT_CODE ON)

# The codecs compile whole CSVs at asset-open time; keep them optimized in Debug
# (unoptimized, globals.gdb takes ~1.4s to compile at boot vs ~140ms with -O2).
if(NOT MSVC)
    target_compile_options(refii_formats PRIVATE $<$<CONFIG:Debug>:-O2>)
endif()
