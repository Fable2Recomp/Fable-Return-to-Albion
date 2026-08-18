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
        set(_f "zig-aarch64-windows-${REFII_ZIG_VERSION}.zip")
        set(_s "aee38316ee4111717900f45dd3130145c39289e105541d737eb8c5ed653c78ef")
    else()
        message(FATAL_ERROR "No pinned Zig for ${platform}-${arch}")
    endif()

    set(${out_file} "${_f}" PARENT_SCOPE)
    set(${out_sha} "${_s}" PARENT_SCOPE)
endfunction()

# Stages Zig for one target into <dest>, which ends up as bin/recompile/zig.
function(refii_fetch_zig platform arch dest cache_dir)
    _refii_zig_spec("${platform}" "${arch}" _file _sha)

    file(MAKE_DIRECTORY "${cache_dir}")
    set(_archive "${cache_dir}/${_file}")

    if(EXISTS "${_archive}")
        file(SHA256 "${_archive}" _have)
        if(NOT _have STREQUAL _sha)
            message(STATUS "Cached ${_file} has the wrong hash; refetching")
            file(REMOVE "${_archive}")
        endif()
    endif()

    if(NOT EXISTS "${_archive}")
        message(STATUS "Downloading ${_file}")
        file(DOWNLOAD
             "https://ziglang.org/download/${REFII_ZIG_VERSION}/${_file}"
             "${_archive}"
             EXPECTED_HASH SHA256=${_sha}
             SHOW_PROGRESS
             STATUS _status)
        list(GET _status 0 _code)
        if(NOT _code EQUAL 0)
            list(GET _status 1 _reason)
            file(REMOVE "${_archive}")
            message(FATAL_ERROR "Could not download ${_file}: ${_reason}")
        endif()
    endif()

    set(_work "${cache_dir}/extract-zig-${platform}-${arch}")
    file(REMOVE_RECURSE "${_work}")
    file(MAKE_DIRECTORY "${_work}")
    file(ARCHIVE_EXTRACT INPUT "${_archive}" DESTINATION "${_work}")

    file(GLOB _root "${_work}/zig-*")
    if(NOT _root)
        message(FATAL_ERROR "Unexpected layout inside ${_file}")
    endif()

    # THE WHOLE TREE, not just the binary. zig's lib/ carries the libc headers, the libc++
    # sources, compiler-rt and the MinGW CRT — that is the part which makes it a complete
    # toolchain rather than a compiler that needs one. Taking the binary alone produces
    # something that cannot compile hello world.
    file(REMOVE_RECURSE "${dest}")
    file(MAKE_DIRECTORY "${dest}")
    file(COPY "${_root}/" DESTINATION "${dest}")

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

    file(REMOVE_RECURSE "${_work}")
    message(STATUS "Bundled Zig ${REFII_ZIG_VERSION} for ${platform}-${arch}")
endfunction()
