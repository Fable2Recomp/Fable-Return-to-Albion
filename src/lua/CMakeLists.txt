# Native Lua runtime and tooling.
#
# `refii_lua` owns host-side runtime helpers. Strong PPC overrides are exposed
# as source files and compiled by the unified `src/hooks` object target.

set(REFII_LUA_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_LUA_INCLUDE_DIRS
    ${REFII_LUA_ROOT}/include
)

set(REFII_LUA51_INCLUDE_DIRS
    ${REFII_LUA_ROOT}/thirdparty/lua51/src
)

# Appended here, NOT in the block above, because that one is declared before
# REFII_LUA51_INCLUDE_DIRS exists and would expand it to nothing. The staged payload
# sources include lua.h directly and have no target to inherit it from -- the install-time
# build gets a flat list of -I and nothing else.
list(APPEND REFII_LUA_INCLUDE_DIRS ${REFII_LUA51_INCLUDE_DIRS})

set(REFII_LUA_RUNTIME_PUBLIC_HEADERS
    ${REFII_LUA_ROOT}/include/core/host_lua_vm.h
    ${REFII_LUA_ROOT}/include/core/native_lua_bridge.h
    ${REFII_LUA_ROOT}/include/core/native_lua_hooks.h
    ${REFII_LUA_ROOT}/include/lua_script_paths.h
)

set(REFII_LUA_RUNTIME_PRIVATE_HEADERS
    ${REFII_LUA_ROOT}/include/core/native_lua_rexglue_compat.h
)

set(REFII_LUA_RUNTIME_SOURCES
    ${REFII_LUA_ROOT}/src/core/host_lua_vm.cpp
    ${REFII_LUA_ROOT}/src/core/native_lua_bridge.cpp
    ${REFII_LUA_ROOT}/src/core/native_lua_handlers.cpp
    ${REFII_LUA_ROOT}/src/core/native_lua_runtime.cpp
)

set(REFII_LUA_RUNTIME_PARTS
    ${REFII_LUA_RUNTIME_PUBLIC_HEADERS}
    ${REFII_LUA_RUNTIME_PRIVATE_HEADERS}
)
set_source_files_properties(${REFII_LUA_RUNTIME_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

# THE PAYLOAD COMPILES THESE, IT DOES NOT LINK THEM. Every one of these files calls the
# recompiled guest -- native_lua_bridge.cpp alone declares ten PPC_EXTERN_FUNCs -- and the
# guest only exists inside refii_generated, which is built on the player's machine. That
# rules out shipping them as a library: a Windows DLL must resolve every symbol at link
# time and cannot, while a STATIC lib links but is MSVC-built, so its /DEFAULTLIB:msvcrtd
# directives break the MinGW-driver payload link. Staging the sources puts them through
# the same compiler as the generated code, which is what src/vfs already does with its 51
# hook sources. refii_lua itself stays for staging and is linked by nothing.
set(REFII_LUA_HOOK_SOURCES
    ${REFII_LUA_ROOT}/src/core/native_lua_hooks.cpp
    ${REFII_LUA_RUNTIME_SOURCES}
)

set(REFII_LUA_HOOK_PARTS
    ${REFII_LUA_ROOT}/include/core/native_lua_hooks.h
)
set_source_files_properties(${REFII_LUA_HOOK_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

set(REFII_LUA51_SOURCES
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lapi.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lauxlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lbaselib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lcode.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldblib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldebug.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldo.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ldump.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lfunc.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lgc.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/linit.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/liolib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/llex.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lmathlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lmem.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/loadlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lobject.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lopcodes.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/loslib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lparser.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lstate.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lstring.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lstrlib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ltable.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ltablib.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/ltm.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lundump.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lvm.c
    ${REFII_LUA_ROOT}/thirdparty/lua51/src/lzio.c
)

# Appended after the list exists rather than inside the block above, which is declared
# earlier and would expand this to nothing. host_lua_vm.cpp calls 32 lua51 C entry points
# and the payload has nothing to link them from, so lua51 is compiled into the payload as
# well. Pure C, so zig builds it exactly as this tree does, and the payload stays one
# compiler's output.
list(APPEND REFII_LUA_HOOK_SOURCES ${REFII_LUA51_SOURCES})

set(REFII_LUADEC_SOURCES
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_chunk.cpp
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec.cpp
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.cpp
)

set(REFII_LUADEC_HEADERS
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_chunk.h
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec.h
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.h
    ${REFII_LUA_ROOT}/src/tools/luadec/luac32_luaconf.h
)

set(REFII_LUA_DECOMPILER_WORKSPACE_SOURCES
    ${REFII_LUA_ROOT}/src/tools/crypto/sha256.cpp
    ${REFII_LUA_ROOT}/src/tools/workspace/lua_workspace_manager.cpp
    ${REFII_LUA_ROOT}/src/tools/workspace/lua_workspace_decompile_pipeline.cpp
)

set(REFII_LUA_DECOMPILER_PUBLIC_HEADERS
    ${REFII_LUA_ROOT}/include/tools/crypto/sha256.h
    ${REFII_LUA_ROOT}/include/tools/native/native_lua_decompiler.h
    ${REFII_LUA_ROOT}/include/tools/workspace/lua_workspace_decompile_pipeline.h
)

set(REFII_LUA_DECOMPILER_PRIVATE_PARTS ${REFII_LUADEC_HEADERS})
set_source_files_properties(
    ${REFII_LUA_DECOMPILER_PUBLIC_HEADERS}
    ${REFII_LUA_DECOMPILER_PRIVATE_PARTS}
    PROPERTIES HEADER_FILE_ONLY TRUE
)

source_group(TREE ${REFII_LUA_ROOT} FILES
    ${REFII_LUA_RUNTIME_PARTS}
    ${REFII_LUA_RUNTIME_SOURCES}
    ${REFII_LUA_HOOK_PARTS}
    ${REFII_LUA_HOOK_SOURCES}
    ${REFII_LUA51_SOURCES}
    ${REFII_LUA_DECOMPILER_PUBLIC_HEADERS}
    ${REFII_LUA_DECOMPILER_PRIVATE_PARTS}
    ${REFII_LUA_DECOMPILER_WORKSPACE_SOURCES}
)

set(REFII_LUA_LIBRARY_TYPE SHARED)
if(WIN32)
    set(REFII_LUA_LIBRARY_TYPE STATIC)
endif()

add_library(refii_lua ${REFII_LUA_LIBRARY_TYPE}
    ${REFII_LUA_RUNTIME_SOURCES}
    ${REFII_LUA_RUNTIME_PARTS}
)
target_include_directories(refii_lua
    PUBLIC
        ${REFII_LUA_INCLUDE_DIRS}
        # For core/payload_bridge_string.h — the caller-buffer helper both bridge ABIs use.
        # An include path only; refii_lua does not link refii_vfs. Copying the helper here
        # instead would be a second implementation of the one thing that must behave
        # identically on both sides of the boundary.
        ${CMAKE_CURRENT_LIST_DIR}/../vfs/include
)
target_link_libraries(refii_lua
    PUBLIC
        rex::runtime
)
set_target_properties(refii_lua PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_lua
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32 AND NOT REFII_LUA_LIBRARY_TYPE STREQUAL "STATIC")
    set_target_properties(refii_lua PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

add_library(refii_lua51 STATIC
    ${REFII_LUA51_SOURCES}
)
target_include_directories(refii_lua51
    PUBLIC
        ${REFII_LUA51_INCLUDE_DIRS}
)
set_target_properties(refii_lua51 PROPERTIES
    C_STANDARD 99
    C_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)
if(WIN32)
    target_compile_definitions(refii_lua51 PUBLIC LUA_USE_WINDOWS)
elseif(APPLE)
    target_compile_definitions(refii_lua51 PUBLIC LUA_USE_MACOSX)
else()
    target_compile_definitions(refii_lua51 PUBLIC LUA_USE_LINUX)
    target_link_libraries(refii_lua51 PUBLIC ${CMAKE_DL_LIBS} m)
endif()

# The host-VM executor (src/core/host_lua_vm.cpp) embeds the vendored lua51.
target_link_libraries(refii_lua PRIVATE refii_lua51)

# luadec: the Lua 5.1 decompiler and its byte-exact oracle. The oracle
# compiles the vendored Lua 5.1.2 (+ `continue`) as C++ inside a namespace
# with the guest's 4-byte float lua_Number, so it can sit in one binary next
# to the runtime's own Lua. Its one translation unit takes the vendored
# sources' warnings as they are.
add_library(refii_luadec_core STATIC
    ${REFII_LUADEC_SOURCES}
    ${REFII_LUADEC_HEADERS}
)
target_include_directories(refii_luadec_core
    PUBLIC
        ${REFII_LUA_ROOT}/src/tools/luadec
    PRIVATE
        ${REFII_LUA_ROOT}/thirdparty/lua512/src
)
if(WIN32)
    target_compile_definitions(refii_luadec_core PRIVATE LUA_USE_WINDOWS)
elseif(APPLE)
    target_compile_definitions(refii_luadec_core PRIVATE LUA_USE_MACOSX)
else()
    target_compile_definitions(refii_luadec_core PRIVATE LUA_USE_LINUX)
endif()
if(MSVC)
    set_source_files_properties(${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.cpp
        PROPERTIES COMPILE_OPTIONS "/w")
else()
    set_source_files_properties(${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.cpp
        PROPERTIES COMPILE_OPTIONS "-w")
endif()
set_target_properties(refii_luadec_core PROPERTIES
    OUTPUT_NAME refii_luadec_core
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    POSITION_INDEPENDENT_CODE ON
    # Only the C ABI leaves the shared library; the namespaced Lua stays inside.
    CXX_VISIBILITY_PRESET hidden
)

# The editor's decompiler: the Godot bank-mount plugin decompiles a script
# bank entry on demand through this C ABI (Engine_LuaDecompiler.cs) and saves
# an edited copy into the mod folder, where the mount overlays it like any
# other file. Staged beside the other host libraries.
add_library(refii_luadec SHARED
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_c_api.cpp
)
target_link_libraries(refii_luadec PRIVATE refii_luadec_core)
set_target_properties(refii_luadec PROPERTIES
    OUTPUT_NAME refii_luadec
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    CXX_VISIBILITY_PRESET hidden
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)
if(REFII_STAGE_GODOT_BUILD AND DEFINED REFII_GODOT_BUILD_BIN_DIR)
    add_custom_command(TARGET refii_luadec POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii_luadec>"
            "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMENT "Staging refii_luadec for Godot"
    )
endif()

if(REFII_BUILD_TOOLS)
    # The oracle on its own: compile Lua text to a guest chunk, or verify a
    # decompiled file against the chunk it came from.
    add_executable(refii-luac32
        ${REFII_LUA_ROOT}/src/tools/luadec/luac32_main.cpp
    )
    target_link_libraries(refii-luac32 PRIVATE refii_luadec_core)
    set_target_properties(refii-luac32 PROPERTIES
        OUTPUT_NAME refii-luac32
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
    )
    refii_stage_windows_runtime_dlls(refii-luac32)

    # The decompiler on its own: one chunk to Lua text or a listing.
    add_executable(refii-luadec
        ${REFII_LUA_ROOT}/src/tools/luadec/luadec_main.cpp
    )
    target_link_libraries(refii-luadec PRIVATE refii_luadec_core)
    set_target_properties(refii-luadec PROPERTIES
        OUTPUT_NAME refii-luadec
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
    )
    refii_stage_windows_runtime_dlls(refii-luadec)
endif()

if(TARGET refii_bnk_extractor)
    add_library(refii_lua_decompiler_workspace STATIC
        ${REFII_LUA_DECOMPILER_WORKSPACE_SOURCES}
        ${REFII_LUA_DECOMPILER_PUBLIC_HEADERS}
    )
    target_include_directories(refii_lua_decompiler_workspace
        PUBLIC
            ${REFII_LUA_ROOT}/include
    )
    target_link_libraries(refii_lua_decompiler_workspace
        PUBLIC
            refii_bnk_extractor
        PRIVATE
            refii_luadec_core
    )
    set_target_properties(refii_lua_decompiler_workspace PROPERTIES
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
        # This static library is linked into refii_launcher_ops, which is SHARED.
        POSITION_INDEPENDENT_CODE ON
    )

    if(REFII_BUILD_TOOLS)
        add_executable(refii-lua-decompiler
            ${REFII_LUA_ROOT}/src/tools/refii_lua_decompiler_cli_main.cpp
        )
        target_link_libraries(refii-lua-decompiler
            PRIVATE
                refii_lua_decompiler_workspace
                refii_luadec_core
        )
        set_target_properties(refii-lua-decompiler PROPERTIES
            OUTPUT_NAME refii-lua-decompiler
            CXX_STANDARD 23
            CXX_STANDARD_REQUIRED ON
            RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
            BUILD_RPATH "$ORIGIN/../lib"
            INSTALL_RPATH "$ORIGIN/../lib"
        )
        refii_stage_windows_runtime_dlls(refii-lua-decompiler)
    endif()
else()
    message(STATUS
        "Skipping Lua decompiler workspace/tool because refii_bnk_extractor is not available. "
        "Enable REFII_BUILD_EXTRACTOR if you need refii-lua-decompiler."
    )
endif()

set(REFII_LUA_HOOK_SOURCES
    ${REFII_LUA_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_LUA_INCLUDE_DIRS
    ${REFII_LUA_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_LUA_LIBRARIES
    refii_lua
    PARENT_SCOPE
)
