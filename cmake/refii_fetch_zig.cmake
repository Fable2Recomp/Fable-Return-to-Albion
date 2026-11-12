# Bundles Zig, which is the player's entire C++ toolchain and build driver.
#
# ONE TOOL INSTEAD OF THREE PLUS A COMPILER. This previously shipped cmake and ninja and
# then asked the player to install clang or MSVC themselves, because a C++ toolchain needs
# headers, CRT objects and a linker that no build machine can lawfully redistribute.
#
# Zig solves exactly that problem: it is clang plus lld plus a bundled libc — musl and
# glibc stubs for Linux, and MinGW-w64 for Windows, all of which ARE redistributable. So
# `zig c++` compiles and links the payload on a machine with nothing installed.
#
# It also removes the build system. The launcher deletes the generated sources after every
# successful build, so the payload is never rebuilt incrementally — there is no dependency
# graph worth keeping, and ninja was maintaining one for a build that is always clean. The
# launcher drives zig directly: precompile the header, compile the TUs, link.
#
# PINNED AND HASH-VERIFIED for the TARGET architecture, so packaging windows-arm64 from an
# x86_64 Linux host ships arm64 Windows tools and the bytes do not depend on who packaged.
#
# Cached, so repeat packaging is offline.

cmake_minimum_required(VERSION 3.24)

set(REFII_ZIG_VERSION "0.16.0")

# platform-arch -> archive name + SHA-256, from ziglang.org/download/index.json.
# Changing the version means changing every hash.
function(_refii_zig_spec platform arch out_file out_sha)
    if(platform STREQUAL "linux" AND arch STREQUAL "x86_64")
        set(_f "zig-x86_64-linux-${REFII_ZIG_VERSION}.tar.xz")
        set(_s "70e49664a74374b48b51e6f3fdfbf437f6395d42509050588bd49abe52ba3d00")
    elseif(platform STREQUAL "linux" AND arch STREQUAL "arm64")
        set(_f "zig-aarch64-linux-${REFII_ZIG_VERSION}.tar.xz")
        set(_s "ea4b09bfb22ec6f6c6ceac57ab63efb6b46e17ab08d21f69f3a48b38e1534f17")
    elseif(platform STREQUAL "windows" AND arch STREQUAL "x86_64")
        set(_f "zig-x86_64-windows-${REFII_ZIG_VERSION}.zip")
        set(_s "68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e")
    elseif(platform STREQUAL "windows" AND arch STREQUAL "arm64")
        # THE x86_64 BUILD, ON PURPOSE, run under emulation and cross-compiling to aarch64.
        # zig 0.16.0's aarch64-windows binary crashes clang on -include-pch (see TargetFlags in
        # Godot/src/launcher/recompile/GameCodeBuild.cs for the measurements); the x86_64 build
        # emits the same objects with the precompiled header intact, and does it faster than the
        # native build manages without one. GameCodeBuild names the target explicitly, because
        # this toolchain can no longer infer it from the machine it is running on.
        set(_f "zig-x86_64-windows-${REFII_ZIG_VERSION}.zip")
        set(_s "68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e")
    else()
        message(FATAL_ERROR "No pinned Zig for ${platform}-${arch}")
    endif()

    set(${out_file} "${_f}" PARENT_SCOPE)
    set(${out_sha} "${_s}" PARENT_SCOPE)
endfunction()

# Copies the unpacked toolchain into the package.
#
# THE WHOLE TREE, not just the binary. zig's lib/ carries the libc headers, the libc++
# sources, compiler-rt and the MinGW CRT — that is the part which makes it a complete
# toolchain rather than a compiler that needs one. Taking the binary alone produces something
# that cannot compile hello world. The stamp is excluded: it is cache bookkeeping and has no
# business in a release.
function(_refii_zig_install unpacked dest platform)
    file(REMOVE_RECURSE "${dest}")
    file(MAKE_DIRECTORY "${dest}")
    file(COPY "${unpacked}/" DESTINATION "${dest}"
         PATTERN ".refii-zig-stamp" EXCLUDE)

    set(_binary "${dest}/zig")
    if(platform STREQUAL "windows")
        set(_binary "${_binary}.exe")
    endif()
    if(NOT EXISTS "${_binary}")
        message(FATAL_ERROR "Zig staged but ${_binary} is missing")
    endif()

    file(CHMOD "${_binary}"
         PERMISSIONS OWNER_READ OWNER_WRITE OWNER_EXECUTE
                     GROUP_READ GROUP_EXECUTE WORLD_READ WORLD_EXECUTE)
endfunction()

# Ensures the pinned Zig archive is downloaded, SHA-256 verified, and unpacked once into
# <cache_dir>/zig-<version>-<platform>-<arch> without copying 19,500+ files into a destination.
# Sets <out_unpacked> in PARENT_SCOPE to the cached unpacked toolchain directory.
function(refii_ensure_zig_cached platform arch cache_dir out_unpacked)
    _refii_zig_spec("${platform}" "${arch}" _file _sha)

    file(MAKE_DIRECTORY "${cache_dir}")
    set(_archive "${cache_dir}/${_file}")

    # UNPACKED ONCE, INTO THE CACHE, then mirrored incrementally per package.
    #
    # The download was already cached; the UNPACK is cached here too. The x86_64-windows archive
    # is 93 MiB and becomes 346 MiB across 19,546 files — libc and libc++ headers, compiler-rt,
    # the MinGW CRT. The stamp records the PIN, not just "something is here", so bumping
    # REFII_ZIG_VERSION or a hash re-extracts rather than leaving the previous toolchain in place.
    set(_unpacked "${cache_dir}/zig-${REFII_ZIG_VERSION}-${platform}-${arch}")
    set(_stamp "${_unpacked}/.refii-zig-stamp")
    set(_want "${REFII_ZIG_VERSION} ${platform} ${arch} ${_sha}")

    set(_have_unpacked FALSE)
    if(EXISTS "${_stamp}")
        file(READ "${_stamp}" _have_stamp)
        string(STRIP "${_have_stamp}" _have_stamp)
        if(_have_stamp STREQUAL _want)
            set(_have_unpacked TRUE)
        endif()
    endif()

    if(_have_unpacked)
        set(${out_unpacked} "${_unpacked}" PARENT_SCOPE)
        return()
    endif()

    if(EXISTS "${_archive}")
        file(SHA256 "${_archive}" _have)
        if(NOT _have STREQUAL _sha)
            message(STATUS "Cached ${_file} has the wrong hash; refetching")
            file(REMOVE "${_archive}")
        endif()
    endif()

    if(NOT EXISTS "${_archive}")
        message(STATUS "Downloading ${_file}")
        set(_url "https://ziglang.org/download/${REFII_ZIG_VERSION}/${_file}")
        set(_download "${_archive}.download")
        file(DOWNLOAD "${_url}" "${_download}"
             TLS_VERIFY ON
             TIMEOUT 300
             INACTIVITY_TIMEOUT 60
             SHOW_PROGRESS
             STATUS _status)
        list(GET _status 0 _code)
        list(GET _status 1 _reason)
        if(_code EQUAL 60 AND CMAKE_HOST_WIN32)
            set(_windows_curl "$ENV{SystemRoot}/System32/curl.exe")
            if(EXISTS "${_windows_curl}")
                message(STATUS "Retrying Zig download with the Windows certificate store")
                execute_process(
                    COMMAND "${_windows_curl}" --fail --location --silent --show-error
                            --proto "=https" --proto-redir "=https" --tlsv1.2
                            --connect-timeout 30 --max-time 300
                            --output "${_download}" "${_url}"
                    RESULT_VARIABLE _code
                    ERROR_VARIABLE _reason
                    TIMEOUT 310)
            endif()
        endif()
        if(NOT "${_code}" STREQUAL "0")
            file(REMOVE "${_download}")
            message(FATAL_ERROR "Could not download ${_file}: ${_reason}")
        endif()
        file(SHA256 "${_download}" _download_sha)
        if(NOT _download_sha STREQUAL _sha)
            file(REMOVE "${_download}")
            message(FATAL_ERROR
                "SHA256 mismatch for ${_file}: expected ${_sha}, got ${_download_sha}")
        endif()
        file(RENAME "${_download}" "${_archive}")
    endif()

    set(_work "${cache_dir}/extract-zig-${platform}-${arch}")
    file(REMOVE_RECURSE "${_work}")
    file(MAKE_DIRECTORY "${_work}")
    file(ARCHIVE_EXTRACT INPUT "${_archive}" DESTINATION "${_work}")

    file(GLOB _root "${_work}/zig-*")
    if(NOT _root)
        message(FATAL_ERROR "Unexpected layout inside ${_file}")
    endif()

    # MOVED, not copied. Both paths are under cache_dir and so on one volume, which makes this
    # a directory rename rather than a second pass over 19,546 files.
    file(REMOVE_RECURSE "${_unpacked}")
    file(RENAME "${_root}" "${_unpacked}")
    file(REMOVE_RECURSE "${_work}")

    # Written LAST, so an interrupted extract leaves no stamp and the next run redoes it
    # rather than serving half a toolchain out of the cache for ever.
    file(WRITE "${_stamp}" "${_want}\n")
    set(${out_unpacked} "${_unpacked}" PARENT_SCOPE)
endfunction()

# Stages Zig for one target into <dest>, which ends up as bin/recompile/zig.
function(refii_fetch_zig platform arch dest cache_dir)
    refii_ensure_zig_cached("${platform}" "${arch}" "${cache_dir}" _unpacked)
    _refii_zig_install("${_unpacked}" "${dest}" "${platform}")
    message(STATUS "Bundled Zig ${REFII_ZIG_VERSION} for ${platform}-${arch}")
endfunction()
