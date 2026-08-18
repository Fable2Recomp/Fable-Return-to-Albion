# Stages the libstdc++ headers the payload is compiled against.
#
# ZIG BUNDLES libc++, NOT libstdc++, AND THE PAYLOAD NEEDS libstdc++.
#
# Two independent reasons, both load-bearing:
#
#   1. Our own SDK headers do not compile under libc++. rex/chrono/chrono.h specialises
#      std::chrono::clock_time_conversion, which libc++ does not declare:
#        "error: explicit specialization of undeclared template struct
#         'clock_time_conversion'"
#
#   2. The payload imports C++ symbols from librefii_vfs and librexruntime, which this
#      build compiles with GCC. libc++ mangles std:: types through an inline namespace
#      (std::__1::), so those imports would not resolve even if they compiled — and a
#      std::string constructed by one runtime and destroyed by the other corrupts.
#
# So the headers must MATCH the ones our shipped libraries were built with, which means
# taking them from this build's own compiler rather than from the player's machine — a
# player is not required to have libstdc++-dev, and if they did it would be the wrong
# version. They are GPL-3 with the GCC Runtime Library Exception, so shipping them is
# permitted; only the headers are staged, never a library.
#
# libstdc++ ONLY, which on Linux is what both GCC and clang link by default. A Windows
# build links MSVC's runtime, where this question does not arise and the answer differs.
#
# INCLUDE ORDER IS PART OF THE CONTRACT. <cmath> reaches the C library through
# `#include_next <math.h>`, which resumes searching AFTER the directory it was found in —
# so the C++ headers must precede zig's libc headers. GameCodeBuild.CompileFlags emits
# them in that order; changing it produces "fatal error: 'math.h' file not found".

include_guard(GLOBAL)

function(refii_stage_stdcxx destination out_staged)
    set(${out_staged} FALSE PARENT_SCOPE)

    # THE STANDARD LIBRARY, NOT THE COMPILER. This tested CMAKE_CXX_COMPILER_ID for GNU and
    # staged nothing, because this build uses clang++ — which on Linux links libstdc++ just
    # as GCC does. What decides the question is which C++ runtime our libraries carry, and
    # the compiler states that directly.
    if(WIN32)
        return()
    endif()

    if(NOT "stdc++" IN_LIST CMAKE_CXX_IMPLICIT_LINK_LIBRARIES)
        return()
    endif()

    # Taken from the compiler, never listed by hand: the version directory moves with every
    # GCC release, and the target-specific one holds bits/c++config.h without which nothing
    # parses.
    set(_roots "")
    foreach(_directory IN LISTS CMAKE_CXX_IMPLICIT_INCLUDE_DIRECTORIES)
        if(_directory MATCHES "/c\\+\\+/" OR _directory MATCHES "/c\\+\\+$")
            list(APPEND _roots "${_directory}")
        endif()
    endforeach()

    if(NOT _roots)
        message(WARNING
            "No libstdc++ include directories reported by the compiler; the install-time "
            "payload build will have no C++ standard library headers.")
        return()
    endif()

    file(REMOVE_RECURSE "${destination}")
    file(MAKE_DIRECTORY "${destination}")

    # Numbered in the compiler's own order, the same convention the payload include set
    # uses, so the order survives staging without a manifest to keep in step.
    set(_index 0)
    set(_manifest "")
    foreach(_root IN LISTS _roots)
        if(NOT IS_DIRECTORY "${_root}")
            continue()
        endif()

        string(REGEX REPLACE "^(.)$" "0\\1" _slot "${_index}")
        file(COPY "${_root}/" DESTINATION "${destination}/${_slot}")
        list(APPEND _manifest "${_slot}")
        math(EXPR _index "${_index} + 1")
    endforeach()

    if(NOT _manifest)
        return()
    endif()

    list(JOIN _manifest "\n" _manifest_text)
    file(WRITE "${destination}/order.txt" "${_manifest_text}\n")

    set(${out_staged} TRUE PARENT_SCOPE)
    message(STATUS "Staged libstdc++ headers for the payload build: ${destination}")
endfunction()
