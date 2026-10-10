# The ReXGlue code generator as a library the launcher loads.
#
# SHARED and C-linkage, the same shape as refii_hostbridge and refii_xmadecode: Godot
# reaches it through P/Invoke. It wraps the SDK's rexcodegen (the recompiler proper, a
# static library) and links the runtime that rexcodegen already requires -- the same
# rexruntime the host loads from bin/, so one copy of it is in the process.
#
# It REPLACES the staged rexglue executable. As a process the generator lived in
# bin/recompile/bin, imported rexruntime and TracyClient from bin/ through a PATH the
# launcher set, and needed its own copy of the MSVC runtime beside it; a machine whose
# DLL search policy ignored PATH failed at process start with nothing in any log. A
# library beside the runtime it imports is resolved from its own directory by the loader,
# which is how every other refii_* library already reaches rexruntime.
#
# LICENSING: rexcodegen carries the GNU binutils PowerPC disassembler (GPL-2.0-or-later),
# so this library is the one GPL component of a release, as the generator executable
# was before it. THIRD_PARTY_NOTICES.txt names it; nothing else may link rexcodegen.

add_library(refii_recompiler SHARED
    src/recompiler.cpp
)

target_include_directories(refii_recompiler
    PUBLIC
        ${CMAKE_CURRENT_LIST_DIR}/include
    PRIVATE
        # rex/version.h is configured into the SDK's binary tree and reached there by the
        # SDK's own targets through a directory-scoped include; a target outside that
        # directory names it itself, as refii_core does.
        "${CMAKE_BINARY_DIR}/rexglue-sdk/include"
)

target_link_libraries(refii_recompiler
    PRIVATE
        rex::codegen
)

set_target_properties(refii_recompiler PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_recompiler
    # The three entry points and nothing else: rexcodegen and the runtime's symbols stay
    # inside, so the library cannot be mistaken for a second copy of either.
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)

# OPTIMISED IN EVERY CONFIGURATION, like the SDK it wraps (refii_optimize_rexglue.cmake):
# this is the shipped generator, and the player should never receive a debug build of it.
# The work is inside rexcodegen, which that module already optimises; this keeps the one
# translation unit on top of it under the same rule rather than Debug's -O0.
if(MSVC)
    target_compile_options(refii_recompiler PRIVATE $<$<CONFIG:Debug>:/O2>)
else()
    target_compile_options(refii_recompiler PRIVATE $<$<CONFIG:Debug>:-O2 -g>)
endif()

# Resolved at link time, for the same reason as refii_hostbridge: this is dlopen'd by .NET
# with lazy binding, and an unresolved reference would end the process on first use with
# no message.
if(NOT WIN32)
    target_link_options(refii_recompiler PRIVATE -Wl,--no-undefined)
endif()

# Staged into bin/ by refii_stage_godot (src/hostbridge/CMakeLists.txt), which stages the
# whole native closure in one target so a rebuild of any one library restages it.
