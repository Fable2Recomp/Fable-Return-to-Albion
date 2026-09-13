# Finds the directories holding the MSVC runtime redistributables.
#
# InstallRequiredSystemLibraries cannot do this here. It gates on MSVC, which CMake does not
# set for clang's GNU-style driver even when the ABI, MSVC_VERSION and the toolset are all
# MSVC's -- and even forced past that gate it finds nothing, because it locates the redist
# through the Visual Studio generator's install record and this tree builds with Ninja. It
# returns an empty list rather than an error, which is the worst shape for this: the package
# stages nothing and says nothing, and the failure lands on a player as a dialog box.
#
# So the directories are found here, from three sources in order of authority: a developer
# prompt that states them, the Visual Studio install the compiler itself lives in, and
# vswhere. The DEBUG CRT is included on purpose. It cannot be redistributed and a package
# carrying it says so loudly -- but it is what a Debug build imports, and without it a Debug
# package cannot even start on the machine that built it.

include_guard(GLOBAL)

function(refii_find_windows_crt_dirs out_var arch)
    set(_roots "")

    # 1. A developer prompt names the redist outright.
    if(DEFINED ENV{VCToolsRedistDir} AND IS_DIRECTORY "$ENV{VCToolsRedistDir}")
        list(APPEND _roots "$ENV{VCToolsRedistDir}")
    endif()

    # 2. The compiler lives inside the Visual Studio install that owns the redist:
    #    <vs>/VC/Tools/Llvm/x64/bin/clang.exe, so <vs>/VC/Redist/MSVC/<version> is a sibling.
    if(CMAKE_CXX_COMPILER MATCHES "^(.+)/VC/Tools/")
        file(GLOB _versions LIST_DIRECTORIES TRUE "${CMAKE_MATCH_1}/VC/Redist/MSVC/*")
        list(SORT _versions)
        list(REVERSE _versions)
        foreach(_version IN LISTS _versions)
            if(IS_DIRECTORY "${_version}")
                list(APPEND _roots "${_version}")
            endif()
        endforeach()
    endif()

    # 3. vswhere, which is where Microsoft says to ask.
    if(NOT _roots)
        find_program(REFII_VSWHERE NAMES vswhere
            PATHS "$ENV{ProgramFiles\(x86\)}/Microsoft Visual Studio/Installer"
                  "$ENV{ProgramFiles}/Microsoft Visual Studio/Installer")
        if(REFII_VSWHERE)
            execute_process(
                COMMAND "${REFII_VSWHERE}" -latest -products * -property installationPath
                OUTPUT_VARIABLE _vs OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
            if(_vs)
                file(TO_CMAKE_PATH "${_vs}" _vs)
                file(GLOB _versions LIST_DIRECTORIES TRUE "${_vs}/VC/Redist/MSVC/*")
                list(SORT _versions)
                list(REVERSE _versions)
                list(APPEND _roots ${_versions})
            endif()
        endif()
    endif()

    set(_dirs "")
    foreach(_root IN LISTS _roots)
        file(GLOB _crt LIST_DIRECTORIES TRUE
            "${_root}/${arch}/Microsoft.VC*.CRT"
            "${_root}/debug_nonredist/${arch}/Microsoft.VC*.DebugCRT")
        foreach(_candidate IN LISTS _crt)
            if(IS_DIRECTORY "${_candidate}")
                list(APPEND _dirs "${_candidate}")
            endif()
        endforeach()
    endforeach()

    # ucrtbased.dll is NOT in the redist tree. Its release twin ships as part of Windows and
    # resolves in system32, so it is neither needed nor wanted in the package; the debug one
    # exists only beside the Windows Kit tools.
    set(_kits "")
    if(DEFINED ENV{WindowsSdkVerBinPath} AND IS_DIRECTORY "$ENV{WindowsSdkVerBinPath}")
        list(APPEND _kits "$ENV{WindowsSdkVerBinPath}/${arch}/ucrt")
    endif()
    foreach(_programfiles "$ENV{ProgramFiles\(x86\)}" "$ENV{ProgramFiles}")
        if(_programfiles)
            file(GLOB _ucrt LIST_DIRECTORIES TRUE
                "${_programfiles}/Windows Kits/10/bin/*/${arch}/ucrt")
            list(SORT _ucrt)
            list(REVERSE _ucrt)
            list(APPEND _kits ${_ucrt})
        endif()
    endforeach()
    foreach(_kit IN LISTS _kits)
        if(IS_DIRECTORY "${_kit}")
            list(APPEND _dirs "${_kit}")
        endif()
    endforeach()

    # Normalised, because the environment hands these back with native separators mixed into
    # globbed ones ("C:\Program Files (x86)/Windows Kits/...") and a consumer that matches on
    # them would have to know which it got.
    set(_normalised "")
    foreach(_dir IN LISTS _dirs)
        file(TO_CMAKE_PATH "${_dir}" _dir)
        list(APPEND _normalised "${_dir}")
    endforeach()
    set(_dirs "${_normalised}")
    if(_dirs)
        list(REMOVE_DUPLICATES _dirs)
    endif()
    set(${out_var} "${_dirs}" PARENT_SCOPE)
endfunction()
