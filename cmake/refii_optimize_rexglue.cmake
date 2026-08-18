# Builds the ReXGlue SDK optimised even in a Debug configuration.
#
# The SDK is the guest's runtime — the kernel, the filesystem, the audio mixer, the memory
# manager — and every guest call passes through it. It is not refii's own code being
# debugged, and an unoptimised copy of it is a tax on every session. It is also what gets
# STAGED for the install-time payload build and shipped to players, who should never be
# handed a debug runtime.
#
# The same reasoning as REFII_OPTIMIZE_GENERATED, applied to the other half of the guest:
# both the recompiled PPC payload and the runtime it calls stay optimised regardless of the
# configuration refii itself is built in. Debug info is kept, so frames still resolve.
#
# Applied to EVERY target the SDK defines, discovered by walking its directories rather
# than by listing target names. The SDK spreads its sources across a dozen component
# libraries and adds more per tree (the netplay tree has rexgpu-xenos and RexNet), so a
# hand-written list would quietly miss whichever one moved.

include_guard(GLOBAL)

option(REFII_OPTIMIZE_REXGLUE
       "Compile the ReXGlue SDK with optimisation even in Debug builds"
       ON)

function(_refii_collect_targets directory out_var)
    get_property(_targets DIRECTORY "${directory}" PROPERTY BUILDSYSTEM_TARGETS)
    get_property(_children DIRECTORY "${directory}" PROPERTY SUBDIRECTORIES)

    foreach(_child IN LISTS _children)
        _refii_collect_targets("${_child}" _child_targets)
        list(APPEND _targets ${_child_targets})
    endforeach()

    set(${out_var} ${_targets} PARENT_SCOPE)
endfunction()

function(refii_optimize_rexglue_targets sdk_binary_directory)
    if(NOT REFII_OPTIMIZE_REXGLUE)
        return()
    endif()

    if(NOT EXISTS "${sdk_binary_directory}")
        return()
    endif()

    _refii_collect_targets("${sdk_binary_directory}" _sdk_targets)

    set(_optimised 0)
    foreach(_target IN LISTS _sdk_targets)
        if(NOT TARGET ${_target})
            continue()
        endif()

        # INTERFACE and imported targets carry no sources to compile, and setting compile
        # options on them either fails outright or leaks the flag to every consumer.
        get_target_property(_type ${_target} TYPE)
        if(_type STREQUAL "INTERFACE_LIBRARY" OR _type STREQUAL "UTILITY")
            continue()
        endif()

        get_target_property(_imported ${_target} IMPORTED)
        if(_imported)
            continue()
        endif()

        if(MSVC)
            target_compile_options(${_target} PRIVATE $<$<CONFIG:Debug>:/O2>)
        else()
            target_compile_options(${_target} PRIVATE $<$<CONFIG:Debug>:-O2 -g>)
        endif()

        math(EXPR _optimised "${_optimised} + 1")
    endforeach()

    message(STATUS
        "ReXGlue SDK: ${_optimised} targets optimised (-O2) even in Debug; "
        "set REFII_OPTIMIZE_REXGLUE=OFF to disable")
endfunction()
